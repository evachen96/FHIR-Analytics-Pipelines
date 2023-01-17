﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Storage.Blobs.Models;
using Hl7.FhirPath.Sprache;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.Synapse.Common.Authentication;
using Microsoft.Health.Fhir.Synapse.Common.Configurations;
using Microsoft.Health.Fhir.Synapse.Common.Logging;
using Microsoft.Health.Fhir.Synapse.Common.Metrics;
using Microsoft.Health.Fhir.Synapse.Common.Models.FhirSearch;
using Microsoft.Health.Fhir.Synapse.Common.Models.Jobs;
using Microsoft.Health.Fhir.Synapse.Core.DataFilter;
using Microsoft.Health.Fhir.Synapse.Core.Extensions;
using Microsoft.Health.Fhir.Synapse.Core.Jobs;
using Microsoft.Health.Fhir.Synapse.Core.Jobs.Models;
using Microsoft.Health.Fhir.Synapse.Core.Jobs.Models.AzureStorage;
using Microsoft.Health.Fhir.Synapse.DataClient;
using Microsoft.Health.Fhir.Synapse.DataClient.Api.Fhir;
using Microsoft.Health.Fhir.Synapse.DataClient.Exceptions;
using Microsoft.Health.Fhir.Synapse.DataClient.Extensions;
using Microsoft.Health.Fhir.Synapse.DataClient.Models;
using Microsoft.Health.Fhir.Synapse.DataClient.UnitTests;
using Microsoft.Health.Fhir.Synapse.DataWriter;
using Microsoft.Health.Fhir.Synapse.DataWriter.Azure;
using Microsoft.Health.JobManagement;
using Newtonsoft.Json;
using NSubstitute;
using Xunit;

namespace Microsoft.Health.Fhir.Synapse.Core.UnitTests.Jobs
{
    public class FhirToDataLakeOrchestratorJobTests
    {
        private static IDiagnosticLogger _diagnosticLogger = new DiagnosticLogger();
        private const string TestBlobEndpoint = "UseDevelopmentStorage=true";
        private const long TBValue = 1024L * 1024L * 1024L * 1024L;
        private static readonly DateTimeOffset TestStartTime = new DateTimeOffset(2014, 8, 18, 0, 0, 0, TimeSpan.FromHours(0));
        private static readonly DateTimeOffset TestEndTime = new DateTimeOffset(2020, 11, 1, 0, 0, 0, TimeSpan.FromHours(0));

        private static readonly List<TypeFilter> TestResourceTypeFilters = new List<TypeFilter> { new TypeFilter("Patient", null) };
        private static readonly List<TypeFilter> TestFourResourceTypeFilters = new List<TypeFilter> { new TypeFilter("Patient1", null), new TypeFilter("Patient2", null), new TypeFilter("Patient3", null), new TypeFilter("Patient4", null) };

        [Fact]
        public async Task GivenASystemScopeWithOneResourceType_WhenProcessing_CorrectNumberOfJobWillBeCreated()
        {
            await VerifyCommonOrchestratorJobAsync(4, 2);
        }

        [Fact]
        public async Task GivenASystemScopeNewOrchestratorJob_WhenProcessingInputFilesMoreThanConcurrentCount_ThenJobShouldBeCompleted()
        {
            await VerifyCommonOrchestratorJobAsync(4, 2);
        }

        [Fact]
        public async Task GivenASystemScopeNewOrchestratorJob_WhenProcessingInputFilesEqualsConcurrentCount_ThenJobShouldBeCompleted()
        {
            await VerifyCommonOrchestratorJobAsync(4, 4);
        }

        [Fact]
        public async Task GivenASystemScopeNewOrchestratorJob_WhenProcessingInputFilesLessThanConcurrentCount_ThenJobShouldBeCompleted()
        {
            await VerifyCommonOrchestratorJobAsync(2, 4);
        }

        [Fact]
        public async Task GivenASystemScopeResumedOrchestratorJob_WhenExecute_ThenJobShouldBeCompleted()
        {
            await VerifyCommonOrchestratorJobAsync(4, 2, 1);
        }

        [Fact]
        public async Task GivenASystemScopeResumedOrchestratorJob_WhenExecuteSomeJobCompleted_ThenJobShouldBeCompleted()
        {
            await VerifyCommonOrchestratorJobAsync(10, 2, 5, 3);
        }

        [Fact]
        public async Task GivenAGroupScopeNewOrchestratorJob_WhenExecute_ThenJobShouldBeCompleted()
        {
            IMetadataStore metadataStore = GetMetaDataStore();

            try
            {
                const int patientCnt = 20;
                List<string> patients = new List<string>();
                for (int i = 0; i < patientCnt; i++)
                {
                    patients.Add($"patientId{i}");
                }

                var previousPatientInfo = new CompartmentInfoEntity
                {
                    PartitionKey = TableKeyProvider.CompartmentPartitionKey((byte)QueueType.FhirToDataLake),
                    RowKey = TableKeyProvider.CompartmentRowKey(patients[0]),
                    VersionId = 3,
                };
                await metadataStore.TryAddEntityAsync(previousPatientInfo);
                FhirToDataLakeOrchestratorJobResult result = await VerifyCommonOrchestratorJobAsync(patientCnt, 4, filterScope: FilterScope.Group, metadataStore: metadataStore);

                // verify patient version for group scope
                // Assert.Equal(patientCnt, result.NextPatientIndex);
                foreach (string patientId in patients)
                {
                    CompartmentInfoEntity entity = await metadataStore.GetCompartmentInfoEntityAsync((byte)QueueType.FhirToDataLake, patientId);
                    Assert.Equal(patientId == patients[0] ? 4 : 1, entity.VersionId);
                }
            }
            finally
            {
                await metadataStore.DeleteMetadataTableAsync();
            }
        }

        [Fact]
        public async Task GivenABrokenDataClient_WhenExecute_ThenRetriableJobExceptionShouldBeThrown()
        {
            string progressResult = null;
            Progress<string> progress = new Progress<string>(r =>
            {
                progressResult = r;
            });

            string containerName = Guid.NewGuid().ToString("N");

            var blobClient = new InMemoryBlobContainerClient();

            FhirToDataLakeOrchestratorJobInputData inputData = GetInputData();
            MockQueueClient<FhirToDataLakeAzureStorageJobInfo> queueClient = GetQueueClient();
            List<JobInfo> jobInfoList = (await queueClient.EnqueueAsync(
                (byte)QueueType.FhirToDataLake,
                new[] { JsonConvert.SerializeObject(inputData) },
                inputData.TriggerSequenceId,
                false,
                false,
                CancellationToken.None)).ToList();
            Assert.Single(jobInfoList);
            JobInfo orchestratorJobInfo = jobInfoList.First();

            var job = new FhirToDataLakeOrchestratorJob(
                orchestratorJobInfo,
                inputData,
                new FhirToDataLakeProcessingJobSpliter(GetBrokenFhirDataClient(), _diagnosticLogger, new NullLogger<FhirToDataLakeProcessingJobSpliter>()),
                GetBrokenFhirDataClient(),
                GetDataWriter(containerName, blobClient),
                queueClient,
                GetGroupMemberExtractor(0),
                GetFilterManager(new FilterConfiguration()),
                GetMetaDataStore(),
                10,
                new MetricsLogger(new NullLogger<MetricsLogger>()),
                _diagnosticLogger,
                new NullLogger<FhirToDataLakeOrchestratorJob>());

            var retriableJobException = await Assert.ThrowsAsync<RetriableJobException>(async () =>
                await job.ExecuteAsync(progress, CancellationToken.None));

            Assert.IsType<ApiSearchException>(retriableJobException.InnerException);
        }

        [Fact]
        public async Task GivenOldOchestratorJobVersion_WhenExecute_ThenTheGeneratedProcessingJobVersionShouldBeTheSameAsOrchestratorJobVersion()
        {
            // orchestrator job with default job version
            var inputData = new FhirToDataLakeOrchestratorJobInputData
            {
                JobType = JobType.Orchestrator,
                TriggerSequenceId = 0L,
                DataStartTime = TestStartTime,
                DataEndTime = TestEndTime,
            };

            string progressResult = null;
            Progress<string> progress = new Progress<string>(r =>
            {
                progressResult = r;
            });

            string containerName = Guid.NewGuid().ToString("N");

            var blobClient = new InMemoryBlobContainerClient();

            MockQueueClient<FhirToDataLakeAzureStorageJobInfo> queueClient = GetQueueClient();
            List<JobInfo> jobInfoList = (await queueClient.EnqueueAsync(
                (byte)QueueType.FhirToDataLake,
                new[] { JsonConvert.SerializeObject(inputData) },
                inputData.TriggerSequenceId,
                false,
                false,
                CancellationToken.None)).ToList();
            Assert.Single(jobInfoList);
            JobInfo orchestratorJobInfo = jobInfoList.First();

            var job = new FhirToDataLakeOrchestratorJob(
                orchestratorJobInfo,
                inputData,
                new FhirToDataLakeProcessingJobSpliter(GetMockFhirDataClient(new Dictionary<string, int>() { { "patient", 60 } }, -1), _diagnosticLogger, new NullLogger<FhirToDataLakeProcessingJobSpliter>()),
                GetMockFhirDataClient(new Dictionary<string, int>() { { "patient", 60 } }, -1),
                GetDataWriter(containerName, blobClient),
                queueClient,
                GetGroupMemberExtractor(0),
                GetFilterManager(new FilterConfiguration()),
                GetMetaDataStore(),
                10,
                new MetricsLogger(new NullLogger<MetricsLogger>()),
                _diagnosticLogger,
                new NullLogger<FhirToDataLakeOrchestratorJob>());

            using var tokenSource = new CancellationTokenSource();
            Task task = job.ExecuteAsync(progress, tokenSource.Token);

            await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None);

            var processingJobInfos = (await queueClient.GetJobByGroupIdAsync((byte)QueueType.FhirToDataLake, inputData.TriggerSequenceId, true, CancellationToken.None)).Where(jobInfo => jobInfo.Id != orchestratorJobInfo.Id);

            Assert.True(processingJobInfos.Any());

            foreach (var processingJobInfo in processingJobInfos)
            {
                var processingJobInputData = JsonConvert.DeserializeObject<FhirToDataLakeOrchestratorJobInputData>(processingJobInfo.Definition);
                Assert.Equal(JobVersionManager.DefaultJobVersion, processingJobInputData.JobVersion);
            }

            tokenSource.Cancel();
            await task;
        }

        private static async Task<FhirToDataLakeOrchestratorJobResult> VerifyJobCountAsync(
            Dictionary<string, int> inputFileCount,
            int resumeFrom = -1,
            FilterScope filterScope = FilterScope.System)
        {
            string progressResult = null;
            Progress<string> progress = new Progress<string>(r =>
            {
                progressResult = r;
            });

            string containerName = Guid.NewGuid().ToString("N");

            var filterConfiguration = new FilterConfiguration
            {
                FilterScope = filterScope,
                RequiredTypes = string.Join(",", inputFileCount.Select(x => x.Key).ToArray()),
            };

            var blobClient = new InMemoryBlobContainerClient();

            FhirToDataLakeOrchestratorJobInputData inputData = GetInputData();
            MockQueueClient<FhirToDataLakeAzureStorageJobInfo> queueClient = GetQueueClient(filterScope);
            List<JobInfo> jobInfoList = (await queueClient.EnqueueAsync(
                (byte)QueueType.FhirToDataLake,
                new[] { JsonConvert.SerializeObject(inputData) },
                inputData.TriggerSequenceId,
                false,
                false,
                CancellationToken.None)).ToList();
            Assert.Single(jobInfoList);
            JobInfo orchestratorJobInfo = jobInfoList.First();

            var orchestratorJobResult = new FhirToDataLakeOrchestratorJobResult();

            IGroupMemberExtractor groupMemberExtractor = GetGroupMemberExtractor(inputFileCount.Count);
            var job = new FhirToDataLakeOrchestratorJob(
                orchestratorJobInfo,
                inputData,
                new FhirToDataLakeProcessingJobSpliter(GetMockFhirDataClient(inputFileCount, resumeFrom), _diagnosticLogger, new NullLogger<FhirToDataLakeProcessingJobSpliter>()),
                GetMockFhirDataClient(inputFileCount, resumeFrom),
                GetDataWriter(containerName, blobClient),
                queueClient,
                groupMemberExtractor,
                GetFilterManager(filterConfiguration),
                GetMetaDataStore(),
                10,
                new MetricsLogger(new NullLogger<MetricsLogger>()),
                _diagnosticLogger,
                new NullLogger<FhirToDataLakeOrchestratorJob>())
            {
                NumberOfPatientsPerProcessingJob = 1,
            };

            string resultString = await job.ExecuteAsync(progress, CancellationToken.None);
            var result = JsonConvert.DeserializeObject<FhirToDataLakeOrchestratorJobResult>(resultString);

            Assert.NotNull(result);
            Assert.Equal(inputFileCount.Count, result.CreatedJobCount);
            return result;
        }

        private static async Task<FhirToDataLakeOrchestratorJobResult> VerifyCommonOrchestratorJobAsync(
            int inputFileCount,
            int concurrentCount,
            int resumeFrom = -1,
            int completedCount = 0,
            FilterScope filterScope = FilterScope.System,
            IMetadataStore metadataStore = null)
        {
            string progressResult = null;
            Progress<string> progress = new Progress<string>(r =>
            {
                progressResult = r;
            });

            string containerName = Guid.NewGuid().ToString("N");

            var filterConfiguration = new FilterConfiguration
            {
                FilterScope = filterScope,
                RequiredTypes = "patient",
            };

            var blobClient = new InMemoryBlobContainerClient();

            FhirToDataLakeOrchestratorJobInputData inputData = GetInputData();
            MockQueueClient<FhirToDataLakeAzureStorageJobInfo> queueClient = GetQueueClient(filterScope);
            List<JobInfo> jobInfoList = (await queueClient.EnqueueAsync(
                (byte)QueueType.FhirToDataLake,
                new[] { JsonConvert.SerializeObject(inputData) },
                inputData.TriggerSequenceId,
                false,
                false,
                CancellationToken.None)).ToList();
            Assert.Single(jobInfoList);
            JobInfo orchestratorJobInfo = jobInfoList.First();

            var orchestratorJobResult = new FhirToDataLakeOrchestratorJobResult();
            bool resumeMode = resumeFrom >= 0;

            var daysInterval = ((TestEndTime - TestStartTime) / inputFileCount).TotalDays;

            if (resumeMode)
            {
                metadataStore = GetMetaDataStore();
                var resumeJobResult = new FhirToDataLakeOrchestratorJobResult()
                {
                    CreatedJobCount = resumeFrom + 1,
                    SubmittedResourceTimestamps = new Dictionary<string, DateTimeOffset>() { { "patient", TestStartTime.AddDays((resumeFrom + 1) * daysInterval) } },
                    TotalResourceCounts = new Dictionary<string, int> { { "Patient", resumeFrom+1 } },
                    ProcessedResourceCounts = new Dictionary<string, int> { { "Patient", resumeFrom + 1 } },
                    SkippedResourceCounts = new Dictionary<string, int> { { "Patient", 0} },
                    ProcessedCountInTotal = resumeFrom +1,
                    ProcessedDataSizeInTotal = (resumeFrom + 1) * 1000L * TBValue,
                };
                var resumeJobStatus = new OrchestratorJobStatusEntity()
                {
                    PartitionKey = TableKeyProvider.JobStatusPartitionKey((byte)QueueType.FhirToDataLake, Convert.ToInt32(JobType.Orchestrator)),
                    RowKey = TableKeyProvider.JobStatusRowKey((byte)QueueType.FhirToDataLake, Convert.ToInt32(JobType.Orchestrator), 0),
                    GroupId = 0,
                    StatisticResult = JsonConvert.SerializeObject(resumeJobResult),
                };
                metadataStore.TryAddEntityAsync(resumeJobStatus);
            }
            for (int i = 0; i < inputFileCount; ++i)
            {
                if (resumeMode)
                {
                    if (i <= resumeFrom)
                    {
                        var processingInput = new FhirToDataLakeProcessingJobInputData
                        {
                            JobType = JobType.Processing,
                            JobVersion = JobVersionManager.CurrentJobVersion,
                            ProcessingJobSequenceId = i,
                            TriggerSequenceId = inputData.TriggerSequenceId,
                            Since = inputData.Since,
                            DataStartTime = TestStartTime.AddDays(i * daysInterval),
                            DataEndTime = TestStartTime.AddDays((i + 1) * daysInterval),
                        };

                        JobInfo jobInfo = (await queueClient.EnqueueAsync(0, new[] { JsonConvert.SerializeObject(processingInput) }, 1, false, false, CancellationToken.None)).First();

                        var processingResult = new FhirToDataLakeProcessingJobResult
                        {
                            SearchCount = new Dictionary<string, int> { { "Patient", 1 } },
                            ProcessedCount = new Dictionary<string, int> { { "Patient", 1 } },
                            SkippedCount = new Dictionary<string, int> { { "Patient", 0 } },
                            ProcessedCountInTotal = 1,
                            ProcessedDataSizeInTotal = 1000L * TBValue,
                        };

                        jobInfo.Result = JsonConvert.SerializeObject(processingResult);
                        if (i < completedCount)
                        {
                            jobInfo.Status = JobStatus.Completed;
                            orchestratorJobResult.TotalResourceCounts.ConcatDictionaryCount(processingResult.SearchCount);
                            orchestratorJobResult.ProcessedResourceCounts.ConcatDictionaryCount(processingResult.ProcessedCount);
                            orchestratorJobResult.ProcessedCountInTotal += processingResult.ProcessedCountInTotal;
                            orchestratorJobResult.ProcessedDataSizeInTotal += processingResult.ProcessedDataSizeInTotal;
                        }
                        else
                        {
                            jobInfo.Status = JobStatus.Running;
                        }

                        orchestratorJobResult.CreatedJobCount += 1;
                    }
                }
            }

            for (int i = 0; i < inputFileCount; ++i)
            {
                if (i < completedCount)
                {
                    await CreateBlobForProcessingJob(orchestratorJobInfo.Id + i + 1, TestStartTime.AddDays((i + 1) * daysInterval).DateTime, true, blobClient);
                }
                else
                {
                    await CreateBlobForProcessingJob(orchestratorJobInfo.Id + i + 1, TestStartTime.AddDays((i + 1) * daysInterval).DateTime, false, blobClient);
                }
            }

            IGroupMemberExtractor groupMemberExtractor = GetGroupMemberExtractor(inputFileCount);
            var job = new FhirToDataLakeOrchestratorJob(
                orchestratorJobInfo,
                inputData,
                new FhirToDataLakeProcessingJobSpliter(GetMockFhirDataClient(new Dictionary<string, int>() { { "patient", inputFileCount}}, resumeFrom), _diagnosticLogger, new NullLogger<FhirToDataLakeProcessingJobSpliter>()),
                GetMockFhirDataClient(new Dictionary<string, int>() { { "patient", inputFileCount } }, resumeFrom),
                GetDataWriter(containerName, blobClient),
                queueClient,
                groupMemberExtractor,
                GetFilterManager(filterConfiguration),
                metadataStore ?? GetMetaDataStore(),
                concurrentCount,
                new MetricsLogger(new NullLogger<MetricsLogger>()),
                _diagnosticLogger,
                new NullLogger<FhirToDataLakeOrchestratorJob>())
            {
                NumberOfPatientsPerProcessingJob = 1,
            };

            string resultString = await job.ExecuteAsync(progress, CancellationToken.None);
            var result = JsonConvert.DeserializeObject<FhirToDataLakeOrchestratorJobResult>(resultString);

            Assert.NotNull(result);
            Assert.Equal(inputFileCount, result.CreatedJobCount);
            Assert.Equal(inputFileCount, result.TotalResourceCounts["Patient"]);
            Assert.Equal(inputFileCount, result.ProcessedResourceCounts["Patient"]);
            Assert.Equal(0, result.SkippedResourceCounts["Patient"]);
            Assert.Equal(inputFileCount, result.ProcessedCountInTotal);
            Assert.Equal(inputFileCount * 1000L * TBValue, result.ProcessedDataSizeInTotal);

            await Task.Delay(TimeSpan.FromMilliseconds(100));
            var progressForContext = JsonConvert.DeserializeObject<FhirToDataLakeOrchestratorJobResult>(progressResult);
            Assert.NotNull(progressForContext);
            Assert.Equal(inputFileCount, progressForContext.CreatedJobCount);
            Assert.Equal(progressForContext.TotalResourceCounts["Patient"], result.TotalResourceCounts["Patient"]);
            Assert.Equal(progressForContext.ProcessedResourceCounts["Patient"], result.ProcessedResourceCounts["Patient"]);
            Assert.Equal(progressForContext.SkippedResourceCounts["Patient"], result.SkippedResourceCounts["Patient"]);
            Assert.Equal(progressForContext.ProcessedDataSizeInTotal, result.ProcessedDataSizeInTotal);
            Assert.Equal(progressForContext.ProcessedCountInTotal, result.ProcessedCountInTotal);

            Assert.Equal(inputFileCount, queueClient.JobInfos.Count - 1);

            // verify blob data;
            IEnumerable<string> blobs = await blobClient.ListBlobsAsync("staging");
            Assert.Empty(blobs);

            blobs = await blobClient.ListBlobsAsync("result");
            Assert.Equal(inputFileCount, blobs.Count());

            return result;
        }

        private static MockQueueClient<FhirToDataLakeAzureStorageJobInfo> GetQueueClient(FilterScope filterScope = FilterScope.System)
        {
            var queueClient = new MockQueueClient<FhirToDataLakeAzureStorageJobInfo>
            {
                GetJobByIdFunc = (queueClient, id,  _) =>
                {
                    JobInfo jobInfo = queueClient.JobInfos.First(t => t.Id == id);

                    if (jobInfo == null)
                    {
                        return null;
                    }

                    if (jobInfo.Status == JobStatus.Completed)
                    {
                        return jobInfo;
                    }

                    var processingResult = new FhirToDataLakeProcessingJobResult
                    {
                        SearchCount = new Dictionary<string, int> { { "Patient", 1 } },
                        ProcessedCount = new Dictionary<string, int> { { "Patient", 1 } },
                        SkippedCount = new Dictionary<string, int> { { "Patient", 0 } },
                        ProcessedCountInTotal = 1,
                        ProcessedDataSizeInTotal = 1000L * TBValue,
                    };

                    if (filterScope == FilterScope.Group)
                    {
                        var input =
                            JsonConvert.DeserializeObject<FhirToDataLakeProcessingJobInputData>(jobInfo.Definition);
                        foreach (PatientWrapper patientWrapper in input.ToBeProcessedPatients)
                        {
                            processingResult.ProcessedPatientVersion[patientWrapper.PatientHash] =
                                patientWrapper.VersionId + 1;
                        }
                    }

                    jobInfo.Result = JsonConvert.SerializeObject(processingResult);
                    jobInfo.Status = JobStatus.Completed;
                    return jobInfo;
                },
            };

            return queueClient;
        }

        private static FhirToDataLakeOrchestratorJobInputData GetInputData()
        {
            return new FhirToDataLakeOrchestratorJobInputData
            {
                JobVersion = JobVersionManager.CurrentJobVersion,
                JobType = JobType.Orchestrator,
                TriggerSequenceId = 0L,
                DataStartTime = TestStartTime,
                DataEndTime = TestEndTime,
            };
        }

        private static IApiDataClient GetBrokenFhirDataClient()
        {
            var dataClient = Substitute.For<IApiDataClient>();
            dataClient.SearchAsync(default)
                .ReturnsForAnyArgs(Task.FromException<string>(new ApiSearchException("fake fhir search exception.")));
            return dataClient;
        }

        private static IApiDataClient GetMockFhirDataClient(Dictionary<string, int> count, int resumedFrom)
        {
            var dataClient = Substitute.For<IApiDataClient>();

            Func<BaseApiOptions, Dictionary<string, int>, string> func = new Func<BaseApiOptions, Dictionary<string, int>, string>(FunWithPara);
            dataClient.SearchAsync(Arg.Any<BaseApiOptions>(), default).Returns(x => func((BaseApiOptions)x[0], count));

            return dataClient;
        }

        private static string FunWithPara(BaseApiOptions options, Dictionary<string, int> count)
        {
            DateTimeOffset start = DateTimeOffset.MinValue;
            DateTimeOffset end = DateTimeOffset.Now;
            string resource = "patient";
            foreach (var parameter in options.QueryParameters)
            {
                if (parameter.Key == FhirApiConstants.LastUpdatedKey)
                {
                    if (parameter.Value.Contains("lt"))
                    {
                        end = DateTimeOffset.Parse(parameter.Value.Substring(2));
                    }
                    else if (parameter.Value.Contains("ge"))
                    {
                        start = DateTimeOffset.Parse(parameter.Value.Substring(2));
                    }
                }

                if (parameter.Key == FhirApiConstants.TypeKey)
                {
                    resource = parameter.Value;
                }
            }

            foreach (var parameter in options.QueryParameters)
            {
                if (parameter.Key == FhirApiConstants.SortKey)
                {
                    if (parameter.Value.Contains("-"))
                    {
                        return $"{{\"entry\": [{{\"resource\":{{\"meta\":{{\"lastUpdated\":\"{end.ToInstantString()}\"}}}}}}]}}";
                    }
                    else
                    {
                        return $"{{\"entry\": [{{\"resource\":{{\"meta\":{{\"lastUpdated\":\"{start.ToInstantString()}\"}}}}}}]}}";
                    }
                }
            }

            var total = (int)(end - start).TotalDays * count[resource] * 200;
            return $"{{\"total\":{total}}}";
        }

        private static List<string> GetSearchBundles(int count)
        {
            string bundleSample = TestDataProvider.GetDataFromFile(TestDataConstants.PatientBundleFile2);
            List<string> results = new List<string> { bundleSample };
            for (int i = 1; i < count; i++)
            {
                string nextTime = TestStartTime.AddDays(i).ToString("yyyy-MM-dd");
                results.Add(bundleSample.Replace("2014-08-18", nextTime));
            }

            return results;
        }

        private static IMetadataStore GetMetaDataStore()
        {
            var uniqueGuid = Guid.NewGuid().ToString("N");
            IOptions<JobConfiguration> jobConfig = Options.Create(new JobConfiguration
            {
                JobInfoTableName = "jobinfotable" + uniqueGuid,
                MetadataTableName = "metadatatable" + uniqueGuid,
                JobInfoQueueName = "jobinfoqueue" + uniqueGuid,
            });
            var tableClientFactory = new AzureTableClientFactory(
                new DefaultTokenCredentialProvider(new NullLogger<DefaultTokenCredentialProvider>()));

            IMetadataStore metadataStore = new AzureTableMetadataStore(tableClientFactory, jobConfig, new NullLogger<AzureTableMetadataStore>());
            Assert.True(metadataStore.IsInitialized());
            return metadataStore;
        }

        private static IDataWriter GetDataWriter(string containerName, IAzureBlobContainerClient blobClient)
        {
            var dataSourceOption = Options.Create(new DataSourceConfiguration());

            var mockFactory = Substitute.For<IAzureBlobContainerClientFactory>();
            mockFactory.Create(Arg.Any<string>(), Arg.Any<string>()).ReturnsForAnyArgs(blobClient);

            var storageConfig = new DataLakeStoreConfiguration
            {
                StorageUrl = TestBlobEndpoint,
            };
            var jobConfig = new JobConfiguration
            {
                ContainerName = containerName,
            };

            var dataSink = new AzureBlobDataSink(Options.Create(storageConfig), Options.Create(jobConfig));
            return new AzureBlobDataWriter(dataSourceOption, mockFactory, dataSink, new NullLogger<AzureBlobDataWriter>());
        }

        private static IFilterManager GetFilterManager(FilterConfiguration filterConfiguration)
        {
            var filterManager = Substitute.For<IFilterManager>();
            if (filterConfiguration.RequiredTypes != string.Empty)
            {
                filterManager.GetTypeFiltersAsync(default).ReturnsForAnyArgs(filterConfiguration.RequiredTypes.Split(",").Select(x => new TypeFilter(x, null)).ToList());
            }
            else
            {
                filterManager.GetTypeFiltersAsync(default).ReturnsForAnyArgs(new List<TypeFilter>() { new TypeFilter("patient", null) });
            }
            filterManager.GetFilterScopeAsync(default).ReturnsForAnyArgs(filterConfiguration.FilterScope);
            filterManager.GetGroupIdAsync(default).ReturnsForAnyArgs(filterConfiguration.GroupId);
            return filterManager;
        }

        private static IGroupMemberExtractor GetGroupMemberExtractor(int count)
        {
            var groupMemberExtractor = Substitute.For<IGroupMemberExtractor>();
            HashSet<string> patients = new HashSet<string>();
            for (int i = 0; i < count; i++)
            {
                patients.Add($"patientId{i}");
            }

            groupMemberExtractor.GetGroupPatientsAsync(default, default, default, default).ReturnsForAnyArgs(patients);
            return groupMemberExtractor;
        }

        private static async Task CreateBlobForProcessingJob(long jobId, DateTime dateTime, bool isCompleted, IAzureBlobContainerClient blobClient)
        {
            var stream = new MemoryStream();
            byte[] bytes = { 1, 2, 3, 4, 5, 6 };
            await stream.WriteAsync(bytes, 0, bytes.Length);
            stream.Position = 0;
            int partId = 0;
            string blobName = isCompleted
                ? $"result/Patient/{dateTime.Year:d4}/{dateTime.Month:d2}/{dateTime.Day:d2}/{jobId:d20}/Patient_{partId:d10}.parquet"
                : $"staging/{jobId:d20}/Patient/{dateTime.Year:d4}/{dateTime.Month:d2}/{dateTime.Day:d2}/Patient_{partId:d10}.parquet";

            await blobClient.CreateBlobAsync(blobName, stream, CancellationToken.None);
        }
    }
}