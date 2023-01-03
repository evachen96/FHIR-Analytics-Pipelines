﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.AccessControl;
using System.Threading;
using System.Threading.Tasks;
using Antlr4.Runtime.Misc;
using Azure;
using EnsureThat;
using Hl7.Fhir.Utility;
using Hl7.FhirPath.Sprache;
using Microsoft.Extensions.Logging;
using Microsoft.Health.Fhir.Synapse.Common.Exceptions;
using Microsoft.Health.Fhir.Synapse.Common.Logging;
using Microsoft.Health.Fhir.Synapse.Common.Metrics;
using Microsoft.Health.Fhir.Synapse.Common.Models.FhirSearch;
using Microsoft.Health.Fhir.Synapse.Common.Models.Jobs;
using Microsoft.Health.Fhir.Synapse.Core.DataFilter;
using Microsoft.Health.Fhir.Synapse.Core.Exceptions;
using Microsoft.Health.Fhir.Synapse.Core.Extensions;
using Microsoft.Health.Fhir.Synapse.Core.Fhir;
using Microsoft.Health.Fhir.Synapse.Core.Jobs.Models;
using Microsoft.Health.Fhir.Synapse.DataClient;
using Microsoft.Health.Fhir.Synapse.DataClient.Api;
using Microsoft.Health.Fhir.Synapse.DataClient.Extensions;
using Microsoft.Health.Fhir.Synapse.DataClient.Models.FhirApiOption;
using Microsoft.Health.Fhir.Synapse.DataWriter;
using Microsoft.Health.JobManagement;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TypeFilter = Microsoft.Health.Fhir.Synapse.Common.Models.FhirSearch.TypeFilter;

namespace Microsoft.Health.Fhir.Synapse.Core.Jobs
{
    public class FhirToDataLakeOrchestratorJob : IJob
    {
        private readonly JobInfo _jobInfo;
        private readonly FhirToDataLakeOrchestratorJobInputData _inputData;
        private readonly IFhirDataClient _dataClient;
        private readonly IFhirDataWriter _dataWriter;
        private readonly IQueueClient _queueClient;
        private readonly IGroupMemberExtractor _groupMemberExtractor;
        private readonly IMetadataStore _metadataStore;
        private readonly IFilterManager _filterManager;
        private readonly int _maxJobCountInRunningPool;
        private readonly ILogger<FhirToDataLakeOrchestratorJob> _logger;
        private readonly IDiagnosticLogger _diagnosticLogger;
        private readonly IMetricsLogger _metricsLogger;
        private Dictionary<string, Dictionary<DateTimeOffset, int>> _anchor = new Dictionary<string, Dictionary<DateTimeOffset, int>>();
        private ConcurrentQueue<(string, List<FhirToDataLakeProcessingJobInputData>)> _jobQueue = new ConcurrentQueue<(string, List<FhirToDataLakeProcessingJobInputData>)>();

        private FhirToDataLakeOrchestratorJobResult _result;
        private List<(string, string, int)> _spliting = new List<(string, string, int)>();
        private List<(string, TimeRange, int)> _mergeList = new List<(string, TimeRange, int)>();

        public FhirToDataLakeOrchestratorJob(
            JobInfo jobInfo,
            FhirToDataLakeOrchestratorJobInputData inputData,
            FhirToDataLakeOrchestratorJobResult result,
            IFhirDataClient dataClient,
            IFhirDataWriter dataWriter,
            IQueueClient queueClient,
            IGroupMemberExtractor groupMemberExtractor,
            IFilterManager filterManager,
            IMetadataStore metadataStore,
            int maxJobCountInRunningPool,
            IMetricsLogger metricsLogger,
            IDiagnosticLogger diagnosticLogger,
            ILogger<FhirToDataLakeOrchestratorJob> logger)
        {
            _jobInfo = EnsureArg.IsNotNull(jobInfo, nameof(jobInfo));
            _inputData = EnsureArg.IsNotNull(inputData, nameof(inputData));
            _result = EnsureArg.IsNotNull(result, nameof(result));
            _dataClient = EnsureArg.IsNotNull(dataClient, nameof(dataClient));
            _dataWriter = EnsureArg.IsNotNull(dataWriter, nameof(dataWriter));
            _queueClient = EnsureArg.IsNotNull(queueClient, nameof(queueClient));
            _groupMemberExtractor = EnsureArg.IsNotNull(groupMemberExtractor, nameof(groupMemberExtractor));
            _filterManager = EnsureArg.IsNotNull(filterManager, nameof(filterManager));
            _metadataStore = EnsureArg.IsNotNull(metadataStore, nameof(metadataStore));
            _diagnosticLogger = EnsureArg.IsNotNull(diagnosticLogger, nameof(diagnosticLogger));
            _metricsLogger = EnsureArg.IsNotNull(metricsLogger, nameof(metricsLogger));
            _maxJobCountInRunningPool = maxJobCountInRunningPool;
            _logger = EnsureArg.IsNotNull(logger, nameof(logger));
        }

        public int InitialOrchestrationIntervalInSeconds { get; set; } = JobConfigurationConstants.DefaultInitialOrchestrationIntervalInSeconds;

        public int IncrementalOrchestrationIntervalInSeconds { get; set; } = JobConfigurationConstants.DefaultIncrementalOrchestrationIntervalInSeconds;

        public int CheckFrequencyInSeconds { get; set; } = JobConfigurationConstants.DefaultCheckFrequencyInSeconds;

        public int NumberOfPatientsPerProcessingJob { get; set; } = JobConfigurationConstants.DefaultNumberOfPatientsPerProcessingJob;

        public int LowBoundOfProcessingJobResourceCount { get; set; } = JobConfigurationConstants.LowBoundOfProcessingJobResourceCount;

        public int HighBoundOfProcessingJobResourceCount { get; set; } = JobConfigurationConstants.HighBoundOfProcessingJobResourceCount;

        public async Task<string> ExecuteAsync(IProgress<string> progress, CancellationToken cancellationToken)
        {
            _diagnosticLogger.LogInformation("Start executing FhirToDataLake job.");
            _logger.LogInformation($"Start executing FhirToDataLake orchestrator job {_jobInfo.Id}.");

            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException();
                }

                FilterScope filterScope = await _filterManager.GetFilterScopeAsync(cancellationToken);
                IAsyncEnumerable<FhirToDataLakeProcessingJobInputData> inputs = filterScope switch
                {
                    FilterScope.System => GetInputsAsyncForSystem(cancellationToken),
                    FilterScope.Group => GetInputsAsyncForGroup(cancellationToken),
                    _ => throw new ConfigurationErrorException(
                        $"The filterScope {filterScope} isn't supported now.")
                };

                await foreach (FhirToDataLakeProcessingJobInputData input in inputs.WithCancellation(cancellationToken))
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

                _diagnosticLogger.LogInformation("Finish FhirToDataLake job.");
                _logger.LogInformation($"Finish FhirToDataLake orchestrator job {_jobInfo.Id}");

                return JsonConvert.SerializeObject(_result);
            }
            catch (OperationCanceledException operationCanceledEx)
            {
                _diagnosticLogger.LogError("FhirToDataLake job is canceled.");
                _logger.LogInformation(operationCanceledEx, "FhirToDataLake orchestrator job {0} is canceled.", _jobInfo.Id);
                _metricsLogger.LogTotalErrorsMetrics(operationCanceledEx, $"FhirToDataLake orchestrator job is canceled. Reason: {operationCanceledEx.Message}", Operations.RunJob);
                throw new RetriableJobException("Job is cancelled.", operationCanceledEx);
            }
            catch (SynapsePipelineExternalException synapsePipelineRetriableEx)
            {
                // Customer exceptions.
                _diagnosticLogger.LogError($"Error in FhirToDataLake job. Reason:{synapsePipelineRetriableEx.Message}");
                _logger.LogInformation(synapsePipelineRetriableEx, "Error in orchestrator job {0}. Reason:{1}", _jobInfo.Id, synapsePipelineRetriableEx);
                _metricsLogger.LogTotalErrorsMetrics(synapsePipelineRetriableEx, $"Error in orchestrator job. Reason: {synapsePipelineRetriableEx.Message}", Operations.RunJob);
                throw new RetriableJobException("Error in orchestrator job.", synapsePipelineRetriableEx);
            }
            catch (RetriableJobException retriableJobEx)
            {
                // always throw RetriableJobException
                _diagnosticLogger.LogError($"Error in FhirToDataLake job. Reason:{retriableJobEx.Message}");
                _logger.LogInformation(retriableJobEx, "Error in orchestrator job {0}. Reason:{1}", _jobInfo.Id, retriableJobEx);
                _metricsLogger.LogTotalErrorsMetrics(retriableJobEx, $"Error in orchestrator job. Reason: {retriableJobEx.Message}", Operations.RunJob);
                throw;
            }
            catch (SynapsePipelineInternalException synapsePipelineInternalEx)
            {
                _diagnosticLogger.LogError("Internal error occurred in FhirToDataLake job.");
                _logger.LogError(synapsePipelineInternalEx, "Error in orchestrator job {0}. Reason:{1}", _jobInfo.Id, synapsePipelineInternalEx);
                _metricsLogger.LogTotalErrorsMetrics(synapsePipelineInternalEx, $"Error in orchestrator job. Reason: {synapsePipelineInternalEx.Message}", Operations.RunJob);
                throw new RetriableJobException("Error in orchestrator job.", synapsePipelineInternalEx);
            }
            catch (Exception unhandledEx)
            {
                // Unhandled exceptions.
                _diagnosticLogger.LogError("Unknown error occurred in FhirToDataLake job.");
                _logger.LogError(unhandledEx, "Unhandled error occurred in orchestrator job {0}. Reason:{1}", _jobInfo.Id, unhandledEx);
                _metricsLogger.LogTotalErrorsMetrics(unhandledEx, $"Unhandled error occurred in orchestrator job. Reason: {unhandledEx.Message}", Operations.RunJob);
                throw new RetriableJobException("Unhandled error occurred in orchestrator job.", unhandledEx);
            }
        }

        private async Task<DateTimeOffset?> GetAnchor(string resourceType, DateTimeOffset? start)
        {
            int baseSize = start == null ? 0 : _anchor[resourceType][(DateTimeOffset)start];
            var last = start;
            foreach (var item in _anchor[resourceType])
            {
                var value = item.Value;
                if (value == int.MaxValue)
                {
                    var resourceCount = await GetResourceCountAsync(resourceType, last, item.Key);
                    var lastAnchorValue = last == null ? 0 : _anchor[resourceType][(DateTimeOffset)last];
                    _anchor[resourceType][item.Key] = resourceCount == int.MaxValue ? int.MaxValue : resourceCount + lastAnchorValue;
                    value = _anchor[resourceType][item.Key];
                }

                if (value - baseSize < LowBoundOfProcessingJobResourceCount)
                {
                    last = item.Key;
                    continue;
                }

                if (value - baseSize <= HighBoundOfProcessingJobResourceCount && value - baseSize >= LowBoundOfProcessingJobResourceCount)
                {
                    return item.Key;
                }

                return await BisectAnchor(resourceType, last == null ? DateTimeOffset.MinValue : (DateTimeOffset)last, item.Key, baseSize);
            }

            return last;
        }

        private async Task<DateTimeOffset> BisectAnchor(string resourceType, DateTimeOffset start, DateTimeOffset end, int baseSize)
        {
            while ((end - start).TotalMilliseconds > 1)
            {
                DateTimeOffset mid = start.Add((end - start) / 2);
                var resourceCount = await GetResourceCountAsync(resourceType, start, mid);
                resourceCount = resourceCount == int.MaxValue ? int.MaxValue : resourceCount + _anchor[resourceType][start];
                _anchor[resourceType][mid] = resourceCount;
                if (resourceCount - baseSize > HighBoundOfProcessingJobResourceCount)
                {
                    end = mid;
                }
                else if (resourceCount - baseSize < LowBoundOfProcessingJobResourceCount)
                {
                    start = mid;
                }
                else
                {
                    return mid;
                }
            }

            return end;
        }

        private async Task<int> GetResourceCountAsync(string resourceType, DateTimeOffset? start, DateTimeOffset end, CancellationToken cancellationToken = default)
        {
            var count = 0;
            List<KeyValuePair<string, string>> parameters = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>(FhirApiConstants.LastUpdatedKey, $"lt{end.ToInstantString()}"),
                new KeyValuePair<string, string>(FhirApiConstants.Summary, "count"),
                new KeyValuePair<string, string>(FhirApiConstants.TypeKey, resourceType),
            };

            if (start != null)
            {
                parameters.Add(new KeyValuePair<string, string>(FhirApiConstants.LastUpdatedKey, $"ge{((DateTimeOffset)start).ToInstantString()}"));
            }

            var searchOptions = new BaseSearchOptions(null, parameters);
            string fhirBundleResult;
            try
            {
                fhirBundleResult = await _dataClient.SearchAsync(searchOptions, cancellationToken);
            }
            catch
            {
                _logger.LogInformation("Get resource count error: too mush resoucres");
                return int.MaxValue;
            }

            // Parse bundle result.
            JObject fhirBundleObject;
            try
            {
                fhirBundleObject = JObject.Parse(fhirBundleResult);
                count = (int)fhirBundleObject["total"];
            }
            catch (JsonReaderException exception)
            {
                string reason = string.Format(
                        "Failed to parse fhir search result for '{0}' with search parameters '{1}'.",
                        searchOptions.ResourceType,
                        string.Join(", ", searchOptions.QueryParameters.Select(parameter => $"{parameter.Key}: {parameter.Value}")));

                _diagnosticLogger.LogError(reason);
                _logger.LogInformation(exception, reason);
                throw new FhirDataParseException(reason, exception);
            }

            return count;
        }

        private Dictionary<string, TimeRange> PushMergeList(string resourceType, TimeRange range, int count)
        {
            var current = _mergeList.Sum(x => x.Item3);
            if (current + count > LowBoundOfProcessingJobResourceCount)
            {
                var result = new Dictionary<string, TimeRange>();
                foreach (var item in _mergeList)
                {
                    result.Add(item.Item1, item.Item2);
                }

                result.Add(resourceType, range);
                _mergeList.Clear();
                return result;
            }

            _mergeList.Add((resourceType, range, count));
            return null;
        }

        private async IAsyncEnumerable<FhirToDataLakeProcessingJobInputData> GetInputsAsyncForSystem([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _logger.LogInformation($"Start spliting. {DateTimeOffset.Now.ToInstantString()}");

            List<TypeFilter> typeFilters = await _filterManager.GetTypeFiltersAsync(cancellationToken);
            var resourceTypes = typeFilters.Select(filter => filter.ResourceType).ToList();
            resourceTypes.Sort();

            foreach (var resourceType in resourceTypes)
            {
                var startTime = _result.ProcessingResourceType.ContainsKey(resourceType) ? _result.ProcessingResourceType[resourceType] : _inputData.DataStartTime;

                // The resource type has already been processed.
                if (startTime >= _inputData.DataEndTime)
                {
                    continue;
                }

                var totalCount = await GetResourceCountAsync(resourceType, startTime, _inputData.DataEndTime);

                if (totalCount == 0)
                {
                    // No resources found.
                    continue;
                }
                else if (totalCount < LowBoundOfProcessingJobResourceCount)
                {
                    // Small size job, put it into pool waiting for merge.
                    _logger.LogInformation($"Spliting jobs. {resourceType} push one small job {DateTimeOffset.Now.ToInstantString()}");

                    var timeRange = new TimeRange()
                    {
                        DataStartTime = _inputData.DataStartTime,
                        DataEndTime = _inputData.DataEndTime,
                    };

                    var parameters = PushMergeList(resourceType, timeRange, _anchor[resourceType][_inputData.DataEndTime]);
                    if (parameters != null)
                    {
                        _logger.LogInformation($"Spliting jobs. Merge {parameters.Count}jobs : {parameters.Keys}");
                        yield return new FhirToDataLakeProcessingJobInputData
                        {
                            JobType = JobType.Processing,
                            ProcessingJobSequenceId = _result.CreatedJobCount,
                            TriggerSequenceId = _inputData.TriggerSequenceId,
                            Since = _inputData.Since,
                            Parameters = parameters,
                        };
                    }
                }
                else if (totalCount < HighBoundOfProcessingJobResourceCount)
                {
                    // Generate one job with properate size.
                    _logger.LogInformation($"Spliting jobs. Generate one {resourceType} job. {DateTimeOffset.Now.ToInstantString()}");

                    yield return new FhirToDataLakeProcessingJobInputData
                    {
                        JobType = JobType.Processing,
                        ProcessingJobSequenceId = _result.CreatedJobCount,
                        TriggerSequenceId = _inputData.TriggerSequenceId,
                        Since = _inputData.Since,
                        Parameters = new Dictionary<string, TimeRange>
                        {
                            {
                                resourceType,
                                new TimeRange()
                                {
                                    DataEndTime = _inputData.DataEndTime,
                                    DataStartTime = _inputData.DataStartTime,
                                }
                            },
                        },
                    };
                }
                else
                {
                    // Split large size job using binary search.
                    _anchor[resourceType] = new Dictionary<DateTimeOffset, int>();

                    if (startTime != null)
                    {
                        _anchor[resourceType][(DateTimeOffset)_inputData.DataStartTime] = 0;
                    }

                    var splitingStartTime = DateTimeOffset.Now;
                    _logger.LogInformation($"Start spliting {resourceType} job, total {_anchor[resourceType][_inputData.DataEndTime]} resources. {splitingStartTime.ToInstantString()}.");

                    // Set isDescending parameter false to get first timestmp.
                    var nextTimestamp = await GetNextTimestamp(resourceType, _inputData.DataStartTime, _inputData.DataEndTime, false, cancellationToken);

                    // Set isDescending parameter as true to get last timestmp.
                    var lastTimestamp = await GetNextTimestamp(resourceType, _inputData.DataStartTime, _inputData.DataEndTime, true, cancellationToken);

                    if (nextTimestamp != null)
                    {
                        _anchor[resourceType][(DateTimeOffset)nextTimestamp] = 0;
                    }

                    if (lastTimestamp != null)
                    {
                        _anchor[resourceType][(DateTimeOffset)lastTimestamp] = totalCount;
                    }

                    var time2 = DateTimeOffset.Now;

                    _logger.LogInformation($"Start spliting {resourceType} job, get first and last timestamp. {(DateTimeOffset.Now - splitingStartTime).TotalMilliseconds}. {DateTimeOffset.Now.ToInstantString()}");
                    DateTimeOffset? nextJobEnd = null;
                    while (nextJobEnd == null || nextJobEnd < _inputData.DataEndTime)
                    {
                        var time1 = DateTimeOffset.Now;

                        _anchor[resourceType] = _anchor[resourceType].OrderBy(p => p.Key).ToDictionary(p => p.Key, o => o.Value);
                        DateTimeOffset? lastEndTime = nextJobEnd ?? _inputData.DataStartTime;
                        nextJobEnd = await GetAnchor(resourceType, lastEndTime);

                        var jobSize = lastEndTime == null ? _anchor[resourceType][(DateTimeOffset)nextJobEnd] : _anchor[resourceType][(DateTimeOffset)nextJobEnd] - _anchor[resourceType][(DateTimeOffset)lastEndTime];

                        if (jobSize == 0)
                        {
                            continue;
                        }

                        if (jobSize < LowBoundOfProcessingJobResourceCount)
                        {
                            var timeRange = new TimeRange()
                            {
                                DataEndTime = (DateTimeOffset)nextJobEnd,
                                DataStartTime = lastEndTime,
                            };
                            var parameters = PushMergeList(resourceType, timeRange, jobSize);

                            if (parameters != null)
                            {
                                _logger.LogInformation($"Spliting {resourceType}job, generate new split. merge {parameters.Count}jobs : {parameters.Keys}");
                                yield return new FhirToDataLakeProcessingJobInputData
                                {
                                    JobType = JobType.Processing,
                                    ProcessingJobSequenceId = _result.CreatedJobCount,
                                    TriggerSequenceId = _inputData.TriggerSequenceId,
                                    Since = _inputData.Since,
                                    Parameters = parameters,
                                };
                                continue;
                            }
                        }

                        time2 = DateTimeOffset.Now;
                        yield return new FhirToDataLakeProcessingJobInputData
                        {
                            JobType = JobType.Processing,
                            ProcessingJobSequenceId = _result.CreatedJobCount,
                            TriggerSequenceId = _inputData.TriggerSequenceId,
                            Since = _inputData.Since,
                            Parameters = new Dictionary<string, TimeRange>
                            {
                                {
                                    resourceType,
                                    new TimeRange()
                                    {
                                        DataEndTime = (DateTimeOffset)nextJobEnd,
                                        DataStartTime = lastEndTime,
                                    }
                                },
                            },
                        };
                    }

                    _logger.LogInformation($"Spliting {resourceType}job, finish split. use : {(DateTimeOffset.Now - splitingStartTime).TotalMilliseconds} milliseconds.");
                }
            }

            if (_mergeList.Count != 0)
            {
                var result = new Dictionary<string, TimeRange>();
                foreach (var item in _mergeList)
                {
                    result.Add(item.Item1, item.Item2);
                }

                yield return new FhirToDataLakeProcessingJobInputData
                {
                    JobType = JobType.Processing,
                    ProcessingJobSequenceId = _result.CreatedJobCount,
                    TriggerSequenceId = _inputData.TriggerSequenceId,
                    Since = _inputData.Since,
                    Parameters = result,
                };
            }
        }

        private async IAsyncEnumerable<FhirToDataLakeProcessingJobInputData> GetInputsAsyncForGroup([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            // for group scope, extract patient list from group at first
            List<PatientWrapper> toBeProcessedPatients = await GetToBeProcessedPatientsAsync(cancellationToken);

            while (_result.NextPatientIndex < toBeProcessedPatients.Count)
            {
                List<PatientWrapper> selectedPatients = toBeProcessedPatients.Skip(_result.NextPatientIndex)
                    .Take(NumberOfPatientsPerProcessingJob).ToList();
                var input = new FhirToDataLakeProcessingJobInputData
                {
                    JobType = JobType.Processing,
                    ProcessingJobSequenceId = _result.CreatedJobCount,
                    TriggerSequenceId = _inputData.TriggerSequenceId,
                    Since = _inputData.Since,
                    DataStartTime = _inputData.DataStartTime,
                    DataEndTime = _inputData.DataEndTime,
                    ToBeProcessedPatients = selectedPatients,
                };
                _result.NextPatientIndex += selectedPatients.Count;
                yield return input;
            }
        }

        private async Task<List<PatientWrapper>> GetToBeProcessedPatientsAsync(CancellationToken cancellationToken)
        {
            string groupID = await _filterManager.GetGroupIdAsync(cancellationToken);

            // extract patient ids from group
            _logger.LogInformation($"Start extracting patients from group '{groupID}'.");

            // For now, the queryParameters is always null.
            // This parameter will be used when we enable filter groups in the future.
            HashSet<string> patientsHash = (await _groupMemberExtractor.GetGroupPatientsAsync(
                groupID,
                null,
                _inputData.DataEndTime,
                cancellationToken)).Select(TableKeyProvider.CompartmentRowKey).ToHashSet();

            Dictionary<string, long> processedPatientVersions;
            try
            {
                processedPatientVersions = await _metadataStore.GetPatientVersionsAsync(_jobInfo.QueueType, patientsHash.ToList(), cancellationToken);
            }
            catch (RequestFailedException ex)
            {
                _logger.LogError(ex, "Failed to get patient versions from metadata table.");
                throw new MetadataStoreException("Failed to get patient versions from metadata table.", ex);
            }

            // set the version id for processed patient
            // the processed patients is empty at the beginning , and will be updated when completing a successful job.
            List<PatientWrapper> toBeProcessedPatients = patientsHash.Select(patientHash =>
                new PatientWrapper(
                    patientHash,
                    processedPatientVersions.ContainsKey(patientHash) ? processedPatientVersions[patientHash] : 0)).ToList();

            _logger.LogInformation(
                "Extract {patientCount} patients from group '{groupId}', including {newPatientCount} new patients.",
                patientsHash.Count,
                groupID,
                toBeProcessedPatients.Where(p => p.VersionId == 0).ToList().Count);

            return toBeProcessedPatients;
        }

        // get the lastUpdated timestamp of next resource for next processing job
        private async Task<DateTimeOffset?> GetNextTimestamp(string resourceType, DateTimeOffset? start, DateTimeOffset end, bool isDescending,  CancellationToken cancellationToken)
        {
            List<TypeFilter> typeFilters = await _filterManager.GetTypeFiltersAsync(cancellationToken);

            List<KeyValuePair<string, string>> parameters = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>(FhirApiConstants.LastUpdatedKey, $"lt{end.ToInstantString()}"),
                new KeyValuePair<string, string>(FhirApiConstants.PageCountKey, FhirApiPageCount.Single.ToString("d")),
                new KeyValuePair<string, string>(FhirApiConstants.TypeKey, resourceType),
            };

            if (isDescending)
            {
                parameters.Add(new KeyValuePair<string, string>(FhirApiConstants.SortKey, "-_lastUpdated"));
            }
            else
            {
                parameters.Add(new KeyValuePair<string, string>(FhirApiConstants.SortKey, "_lastUpdated"));
            }

            if (start != null)
            {
                parameters.Add(new KeyValuePair<string, string>(FhirApiConstants.LastUpdatedKey, $"ge{((DateTimeOffset)start).ToInstantString()}"));
            }

            var searchOptions = new BaseSearchOptions(null, parameters);

            string fhirBundleResult = await _dataClient.SearchAsync(searchOptions, cancellationToken);

            // Parse bundle result.
            JObject fhirBundleObject;
            try
            {
                fhirBundleObject = JObject.Parse(fhirBundleResult);
            }
            catch (JsonReaderException exception)
            {
                string reason = string.Format(
                        "Failed to parse fhir search result for '{0}' with search parameters '{1}'.",
                        searchOptions.ResourceType,
                        string.Join(", ", searchOptions.QueryParameters.Select(parameter => $"{parameter.Key}: {parameter.Value}")));

                _diagnosticLogger.LogError(reason);
                _logger.LogInformation(exception, reason);
                throw new FhirDataParseException(reason, exception);
            }

            List<JObject> fhirResources = FhirBundleParser.ExtractResourcesFromBundle(fhirBundleObject).ToList();

            if (fhirResources.Any() && fhirResources.First().GetLastUpdated() != null)
            {
                return fhirResources.First().GetLastUpdated();
            }

            return null;
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
                                JsonConvert.DeserializeObject<FhirToDataLakeProcessingJobResult>(latestJobInfo.Result);
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

                            if (await _filterManager.GetFilterScopeAsync(cancellationToken) == FilterScope.Group)
                            {
                                try
                                {
                                    await _metadataStore.UpdatePatientVersionsAsync(
                                    _jobInfo.QueueType,
                                    processingJobResult.ProcessedPatientVersion,
                                    cancellationToken);
                                }
                                catch (RequestFailedException ex)
                                {
                                    _logger.LogError(ex, "Failed to update patient versions from metadata table.");
                                    throw new MetadataStoreException("Failed to update patient versions from metadata table.", ex);
                                }
                            }
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