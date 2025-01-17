﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Microsoft.Extensions.Logging;
using Microsoft.Health.AnalyticsConnector.Common.Exceptions;
using Microsoft.Health.AnalyticsConnector.Common.Logging;
using Microsoft.Health.AnalyticsConnector.Common.Metrics;
using Microsoft.Health.AnalyticsConnector.Core.Extensions;
using Microsoft.Health.AnalyticsConnector.Core.Jobs.Models;
using Microsoft.Health.AnalyticsConnector.DataWriter;
using Microsoft.Health.JobManagement;
using Newtonsoft.Json;

namespace Microsoft.Health.AnalyticsConnector.Core.Jobs
{
    public class DicomToDataLakeOrchestratorJob : IJob
    {
        private readonly JobInfo _jobInfo;
        private readonly DicomToDataLakeOrchestratorJobInputData _inputData;
        private readonly IDataWriter _dataWriter;
        private readonly IQueueClient _queueClient;
        private readonly int _maxJobCountInRunningPool;
        private readonly ILogger<DicomToDataLakeOrchestratorJob> _logger;
        private readonly IDiagnosticLogger _diagnosticLogger;
        private readonly IMetricsLogger _metricsLogger;

        private DicomToDataLakeOrchestratorJobResult _result;

        public DicomToDataLakeOrchestratorJob(
            JobInfo jobInfo,
            DicomToDataLakeOrchestratorJobInputData inputData,
            DicomToDataLakeOrchestratorJobResult result,
            IDataWriter dataWriter,
            IQueueClient queueClient,
            int maxJobCountInRunningPool,
            IMetricsLogger metricsLogger,
            IDiagnosticLogger diagnosticLogger,
            ILogger<DicomToDataLakeOrchestratorJob> logger)
        {
            _jobInfo = EnsureArg.IsNotNull(jobInfo, nameof(jobInfo));
            _inputData = EnsureArg.IsNotNull(inputData, nameof(inputData));
            _result = EnsureArg.IsNotNull(result, nameof(result));
            _dataWriter = EnsureArg.IsNotNull(dataWriter, nameof(dataWriter));
            _queueClient = EnsureArg.IsNotNull(queueClient, nameof(queueClient));
            _diagnosticLogger = EnsureArg.IsNotNull(diagnosticLogger, nameof(diagnosticLogger));
            _metricsLogger = EnsureArg.IsNotNull(metricsLogger, nameof(metricsLogger));
            _maxJobCountInRunningPool = maxJobCountInRunningPool;
            _logger = EnsureArg.IsNotNull(logger, nameof(logger));
        }

        public int CheckFrequencyInSeconds { get; set; } = JobConfigurationConstants.DefaultCheckFrequencyInSeconds;

        public int JobChangeFeedLimit { get; set; } = JobConfigurationConstants.DicomJobChangeFeedLimit;

        public async Task<string> ExecuteAsync(IProgress<string> progress, CancellationToken cancellationToken)
        {
            _diagnosticLogger.LogInformation("Start executing DicomToDataLake job.");
            _logger.LogInformation($"Start executing DicomToDataLake orchestrator job {_jobInfo.Id}.");

            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException();
                }

                IEnumerable<DicomToDataLakeProcessingJobInputData> inputs = GetInputs();

                foreach (DicomToDataLakeProcessingJobInputData input in inputs)
                {
                    while (_result.RunningJobIds.Count >= _maxJobCountInRunningPool)
                    {
                        await CheckRunningJobComplete(progress, cancellationToken);
                        if (_result.RunningJobIds.Count >= _maxJobCountInRunningPool)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(CheckFrequencyInSeconds), cancellationToken);
                        }
                    }

                    string[] jobDefinitions = { JsonConvert.SerializeObject(input) };
                    IEnumerable<JobInfo> jobInfos = await _queueClient.EnqueueAsync(
                        _jobInfo.QueueType,
                        jobDefinitions,
                        _jobInfo.GroupId,
                        false,
                        false,
                        cancellationToken);
                    long newJobId = jobInfos.First().Id;
                    _result.CreatedJobCount++;
                    _result.RunningJobIds.Add(newJobId);

                    // if enqueue successfully while fails to report result, will re-enqueue and return the existing jobInfo
                    progress.Report(JsonConvert.SerializeObject(_result));

                    if (_result.RunningJobIds.Count >
                        JobConfigurationConstants.CheckRunningJobCompleteRunningJobCountThreshold)
                    {
                        await CheckRunningJobComplete(progress, cancellationToken);
                    }
                }

                _logger.LogInformation($"Orchestrator job {_jobInfo.Id} finished generating and enqueueing processing jobs.");

                while (_result.RunningJobIds.Count > 0)
                {
                    await CheckRunningJobComplete(progress, cancellationToken);
                    if (_result.RunningJobIds.Count > 0)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(CheckFrequencyInSeconds), cancellationToken);
                    }
                }

                _result.CompleteTime = DateTimeOffset.UtcNow;

                progress.Report(JsonConvert.SerializeObject(_result));

                _diagnosticLogger.LogInformation("Finish DicomToDataLake job.");
                _logger.LogInformation($"Finish DicomToDataLake orchestrator job {_jobInfo.Id}");

                return JsonConvert.SerializeObject(_result);
            }
            catch (OperationCanceledException operationCanceledEx)
            {
                _diagnosticLogger.LogError("DicomToDataLake job is canceled.");
                _logger.LogInformation(operationCanceledEx, "DicomToDataLake orchestrator job {0} is canceled.", _jobInfo.Id);
                _metricsLogger.LogTotalErrorsMetrics(operationCanceledEx, $"DicomToDataLake orchestrator job is canceled. Reason: {operationCanceledEx.Message}", JobOperations.RunJob);
                throw new RetriableJobException("Job is cancelled.", operationCanceledEx);
            }
            catch (SynapsePipelineExternalException synapsePipelineRetriableEx)
            {
                // Customer exceptions.
                _diagnosticLogger.LogError($"Error in DicomToDataLake job. Reason:{synapsePipelineRetriableEx.Message}");
                _logger.LogInformation(synapsePipelineRetriableEx, "Error in orchestrator job {0}. Reason:{1}", _jobInfo.Id, synapsePipelineRetriableEx);
                _metricsLogger.LogTotalErrorsMetrics(synapsePipelineRetriableEx, $"Error in orchestrator job. Reason: {synapsePipelineRetriableEx.Message}", JobOperations.RunJob);
                throw new RetriableJobException("Error in orchestrator job.", synapsePipelineRetriableEx);
            }
            catch (RetriableJobException retriableJobEx)
            {
                // always throw RetriableJobException
                _diagnosticLogger.LogError($"Error in DicomToDataLake job. Reason:{retriableJobEx.Message}");
                _logger.LogInformation(retriableJobEx, "Error in orchestrator job {0}. Reason:{1}", _jobInfo.Id, retriableJobEx);
                _metricsLogger.LogTotalErrorsMetrics(retriableJobEx, $"Error in orchestrator job. Reason: {retriableJobEx.Message}", JobOperations.RunJob);
                throw;
            }
            catch (SynapsePipelineInternalException synapsePipelineInternalEx)
            {
                _diagnosticLogger.LogError("Internal error occurred in DicomToDataLake job.");
                _logger.LogError(synapsePipelineInternalEx, "Error in orchestrator job {0}. Reason:{1}", _jobInfo.Id, synapsePipelineInternalEx);
                _metricsLogger.LogTotalErrorsMetrics(synapsePipelineInternalEx, $"Error in orchestrator job. Reason: {synapsePipelineInternalEx.Message}", JobOperations.RunJob);
                throw new RetriableJobException("Error in orchestrator job.", synapsePipelineInternalEx);
            }
            catch (Exception unhandledEx)
            {
                // Unhandled exceptions.
                _diagnosticLogger.LogError("Unknown error occurred in DicomToDataLake job.");
                _logger.LogError(unhandledEx, "Unhandled error occurred in orchestrator job {0}. Reason:{1}", _jobInfo.Id, unhandledEx);
                _metricsLogger.LogTotalErrorsMetrics(unhandledEx, $"Unhandled error occurred in orchestrator job. Reason: {unhandledEx.Message}", JobOperations.RunJob);
                throw new RetriableJobException("Unhandled error occurred in orchestrator job.", unhandledEx);
            }
        }

        private IEnumerable<DicomToDataLakeProcessingJobInputData> GetInputs()
        {
            if (_result.CreatedJobCount == 0)
            {
                _result.NextOffset = _inputData.StartOffset;
            }

            while (_result.NextOffset + JobChangeFeedLimit <= _inputData.EndOffset)
            {
                var input = new DicomToDataLakeProcessingJobInputData
                {
                    JobType = JobType.Processing,
                    JobVersion = _inputData.JobVersion,
                    ProcessingJobSequenceId = _result.CreatedJobCount,
                    TriggerSequenceId = _inputData.TriggerSequenceId,
                    StartOffset = _result.NextOffset,
                    EndOffset = _result.NextOffset + JobChangeFeedLimit,
                };

                _result.NextOffset += JobChangeFeedLimit;
                yield return input;
            }

            if (_result.NextOffset < _inputData.EndOffset)
            {
                var input = new DicomToDataLakeProcessingJobInputData
                {
                    JobType = JobType.Processing,
                    JobVersion = _inputData.JobVersion,
                    ProcessingJobSequenceId = _result.CreatedJobCount,
                    TriggerSequenceId = _inputData.TriggerSequenceId,
                    StartOffset = _result.NextOffset,
                    EndOffset = _inputData.EndOffset,
                };

                _result.NextOffset = _inputData.EndOffset;
                yield return input;
            }
        }

        private async Task CheckRunningJobComplete(IProgress<string> progress, CancellationToken cancellationToken)
        {
            _logger.LogInformation($"Orchestrator job {_jobInfo.Id} starts to check running job status.");

            HashSet<long> completedJobIds = new HashSet<long>();
            List<JobInfo> runningJobs = new List<JobInfo>();

            runningJobs.AddRange(await _queueClient.GetJobsByIdsAsync(_jobInfo.QueueType, _result.RunningJobIds.ToArray(), false, cancellationToken));

            foreach (JobInfo latestJobInfo in runningJobs)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException();
                }

                if (latestJobInfo.Status != JobStatus.Created && latestJobInfo.Status != JobStatus.Running)
                {
                    if (latestJobInfo.Status == JobStatus.Completed)
                    {
                        await CommitJobData(latestJobInfo.Id, cancellationToken);
                        if (latestJobInfo.Result != null)
                        {
                            var processingJobResult =
                                JsonConvert.DeserializeObject<DicomToDataLakeProcessingJobResult>(latestJobInfo.Result);
                            _result.TotalResourceCounts =
                                _result.TotalResourceCounts.ConcatDictionaryCount(processingJobResult.SearchCount);
                            _result.ProcessedResourceCounts =
                                _result.ProcessedResourceCounts.ConcatDictionaryCount(processingJobResult.ProcessedCount);
                            _result.SkippedResourceCounts =
                                _result.SkippedResourceCounts.ConcatDictionaryCount(processingJobResult.SkippedCount);
                            _result.ProcessedCountInTotal += processingJobResult.ProcessedCountInTotal;
                            _result.ProcessedDataSizeInTotal += processingJobResult.ProcessedDataSizeInTotal;

                            // log metrics
                            _metricsLogger.LogSuccessfulResourceCountMetric(processingJobResult.ProcessedCountInTotal);
                            _metricsLogger.LogSuccessfulDataSizeMetric(processingJobResult.ProcessedDataSizeInTotal);

                            _logger.LogInformation($"Report a {MetricNames.SuccessfulResourceCountMetric} metric with {processingJobResult.ProcessedCountInTotal} resources.");
                            _logger.LogInformation($"Report a {MetricNames.SuccessfulDataSizeMetric} metric with {processingJobResult.ProcessedDataSizeInTotal} bytes data size.");
                        }
                    }
                    else if (latestJobInfo.Status == JobStatus.Failed)
                    {
                        _logger.LogInformation("The processing job is failed.");
                        throw new RetriableJobException("The processing job is failed.");
                    }
                    else if (latestJobInfo.Status == JobStatus.Cancelled)
                    {
                        _logger.LogInformation("Operation cancelled by customer.");
                        throw new OperationCanceledException("Operation cancelled by customer.");
                    }

                    completedJobIds.Add(latestJobInfo.Id);
                }
            }

            if (completedJobIds.Count > 0)
            {
                _result.RunningJobIds.ExceptWith(completedJobIds);
                progress.Report(JsonConvert.SerializeObject(_result));
            }

            _logger.LogInformation($"Orchestrator job {_jobInfo.Id} finished checking running job status, there are {completedJobIds.Count} jobs completed.");
        }

        private async Task CommitJobData(long jobId, CancellationToken cancellationToken)
        {
            // TODO: job Id or job sequence index?
            await _dataWriter.CommitJobDataAsync(jobId, cancellationToken);
        }
    }
}