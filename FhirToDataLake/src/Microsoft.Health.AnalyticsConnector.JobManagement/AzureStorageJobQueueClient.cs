﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Concurrent;
using Azure;
using Azure.Data.Tables;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using EnsureThat;
using Microsoft.Extensions.Logging;
using Microsoft.Health.AnalyticsConnector.JobManagement.Exceptions;
using Microsoft.Health.AnalyticsConnector.JobManagement.Extensions;
using Microsoft.Health.AnalyticsConnector.JobManagement.Models;
using Microsoft.Health.AnalyticsConnector.JobManagement.Models.AzureStorage;
using Microsoft.Health.JobManagement;

namespace Microsoft.Health.AnalyticsConnector.JobManagement
{
    // The maximum size of a single entity, including all property values is 1 MiB, and the maximum allowed size of property value is 64KB. If the property value is a string, it is UTF-16 encoded and the maximum number of characters should be 32K or less.
    // see https://docs.microsoft.com/en-us/azure/storage/tables/scalability-targets#scale-targets-for-table-storage.
    // The definition and result are serialized string of objects,
    // we should be careful that should not contain large fields when define the definition and result class.
    public class AzureStorageJobQueueClient<TJobInfo> : IQueueClient
        where TJobInfo : AzureStorageJobInfo, new()
    {
        private readonly TableClient _azureJobInfoTableClient;
        private readonly QueueClient _azureJobMessageQueueClient;

        private readonly ILogger<AzureStorageJobQueueClient<TJobInfo>> _logger;

        private const int DefaultVisibilityTimeoutInSeconds = 30;

        private const short MaxThreadsCountForGettingJob = 5;

        // A transaction can include at most 100 entities, so limit the jobs count to 50
        // https://docs.microsoft.com/en-us/azure/storage/tables/scalability-targets#scale-targets-for-table-storage
        private const int MaxJobsCountForEnqueuingInABatch = 50;

        private const int MaxRetryCountForUpdateJobIdEntityConflict = 5;

        private bool _isInitialized;

        public AzureStorageJobQueueClient(
            IAzureStorageClientFactory azureStorageClientFactory,
            ILogger<AzureStorageJobQueueClient<TJobInfo>> logger)
        {
            EnsureArg.IsNotNull(azureStorageClientFactory, nameof(azureStorageClientFactory));

            _logger = EnsureArg.IsNotNull(logger, nameof(logger));

            _azureJobInfoTableClient = azureStorageClientFactory.CreateTableClient();
            _azureJobMessageQueueClient = azureStorageClientFactory.CreateQueueClient();
            _isInitialized = false;
        }

        public bool IsInitialized()
        {
            if (_isInitialized)
            {
                return _isInitialized;
            }

            // try to initialize if it is not initialized yet.
            TryInitialize();
            return _isInitialized;
        }

        // The expected behaviors:
        // 1. multi agent instances enqueue same jobs concurrently, only one job entity will be created,
        //    there may be multi messages, while only one will be recorded in job lock entity, and the created jobInfo will be returned for all instances.
        // 2. re-enqueue job, no matter what the job status is now, will do nothing, and return the existing jobInfo, which means for cancelled/failed jobs, we don't allow resume it, will return the existing jobInfo.
        // 3. if one of the steps fails, will continue to process it when re-enqueue.
        // 4. if there are multi jobs to be enqueued, all the job will operate in one transaction
        // TODO: The parameter forceOneActiveJobGroup and isCompleted are ignored for now
        // Note: We don't allow the definitions to be enqueued partial overlap in different calls,  which means we don't allow the first time to enqueue "job1" and "job2", and the second time to only enqueue "job1", or enqueue "job1" and "job3", or enqueue "job1", "job2" and "job3"
        public async Task<IEnumerable<JobInfo>> EnqueueAsync(
            byte queueType,
            string[] definitions,
            long? groupId,
            bool forceOneActiveJobGroup,
            bool isCompleted,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation($"[Enqueue] Start to enqueue {definitions.Length} jobs.");

            if (definitions.Length > MaxJobsCountForEnqueuingInABatch)
            {
                _logger.LogError($"[Enqueue] The count of jobs to be enqueued {definitions.Length} is larger than the maximum allowed length {MaxJobsCountForEnqueuingInABatch}.");
                throw new JobManagementException(
                    $"The count of jobs to be enqueued {definitions.Length} is larger than the maximum allowed length {MaxJobsCountForEnqueuingInABatch}.");
            }

            // step 1: get incremental job ids, will try again if update job id entity conflicts, throw exceptions for other errors
            List<long> jobIds = await GetIncrementalJobIds(queueType, definitions.Length, 0, cancellationToken);

            // step 2: generate job info entities and job lock entities batch
            // The fields of JobInfo are not used: Data, Priority, StartDate, EndDate
            List<TJobInfo> jobInfos = definitions.Select((definition, i) => new TJobInfo
                {
                    Id = jobIds[i],
                    QueueType = queueType,
                    Status = JobStatus.Created,
                    GroupId = groupId ?? 0,
                    Definition = definition,
                    Result = string.Empty,
                    CancelRequested = false,
                    CreateDate = DateTime.UtcNow,
                    HeartbeatDateTime = DateTime.UtcNow,
                })
                .ToList();

            List<TableEntity> jobInfoEntities = jobInfos.Select(jobInfo => jobInfo.ToTableEntity()).ToList();
            List<TableEntity> jobLockEntities = jobInfoEntities.Select((jobInfoEntity, i) =>
                new TableEntity(jobInfoEntity.PartitionKey, AzureStorageKeyProvider.JobLockRowKey(jobInfos[i].JobIdentifier()))
                {
                    { JobLockEntityProperties.JobInfoEntityRowKey, jobInfoEntity.RowKey },
                }).ToList();

            // step 3: insert jobInfo entity and job lock entity in one transaction.
            IEnumerable<TableTransactionAction> transactionActions = jobInfoEntities
                .Select(entity => new TableTransactionAction(TableTransactionActionType.Add, entity))
                .Concat(jobLockEntities.Select(entity => new TableTransactionAction(TableTransactionActionType.Add, entity)));
            try
            {
                await _azureJobInfoTableClient.SubmitTransactionAsync(transactionActions, cancellationToken);
                _logger.LogInformation($"[Enqueue] Insert job info entities and job lock entities for jobs {string.Join(",", jobInfos.Select(jobInfo => jobInfo.Id).ToList())} in one transaction successfully.");
            }
            catch (RequestFailedException ex) when (IsSpecifiedErrorCode(ex, AzureStorageErrorCode.InvalidDuplicateRowErrorCode))
            {
                _logger.LogError(ex, "[Enqueue] There are duplicated jobs to be enqueued.");
                throw new JobManagementException("There are duplicated jobs to be enqueued.", ex);
            }
            catch (RequestFailedException ex) when (IsSpecifiedErrorCode(ex, AzureStorageErrorCode.AddEntityAlreadyExistsErrorCode))
            {
                // step 4: get the existing job lock entities and jobInfo entities
                // need to get job lock entity firstly to get the jobInfo entity row key
                AsyncPageable<TableEntity>? jobEntityQueryResult = _azureJobInfoTableClient.QueryAsync<TableEntity>(
                    filter: TransactionGetByKeys(jobLockEntities.First().PartitionKey, jobLockEntities.Select(entity => entity.RowKey).ToList()),
                    cancellationToken: cancellationToken);

                List<TableEntity> retrievedJobLockEntities = new List<TableEntity>();
                await foreach (Page<TableEntity> pageResult in jobEntityQueryResult.AsPages().WithCancellation(cancellationToken))
                {
                    retrievedJobLockEntities.AddRange(pageResult.Values);
                }

                // If there are duplicated jobs in a batch, it may throw Exception with InvalidDuplicateRowErrorCode or AddEntityAlreadyExistsErrorCode
                if (!retrievedJobLockEntities.Any())
                {
                    _logger.LogError(ex, "[Enqueue] There are duplicated jobs to be enqueued.");
                    throw new JobManagementException("There are duplicated jobs to be enqueued.", ex);
                }

                jobLockEntities = retrievedJobLockEntities;

                // get job info entity by specifying the row key stored in job lock entity
                jobEntityQueryResult = _azureJobInfoTableClient.QueryAsync<TableEntity>(
                    filter: TransactionGetByKeys(jobLockEntities.First().PartitionKey, jobLockEntities.Select(entity => entity.GetString(JobLockEntityProperties.JobInfoEntityRowKey)).ToList()),
                    cancellationToken: cancellationToken);

                List<TableEntity> retrievedJobInfoEntities = new List<TableEntity>();
                await foreach (Page<TableEntity> pageResult in jobEntityQueryResult.AsPages().WithCancellation(cancellationToken))
                {
                    retrievedJobInfoEntities.AddRange(pageResult.Values);
                }

                jobInfoEntities = retrievedJobInfoEntities;
                _logger.LogInformation(ex, "[Enqueue] The entities already exist. Fetched the existing jobs.");
            }
            catch (RequestFailedException ex) when (IsSpecifiedErrorCode(ex, AzureStorageErrorCode.RequestBodyTooLargeErrorCode))
            {
                _logger.LogError(ex, "[Enqueue] The table entity exceeds the the maximum allowed size (1MB).");
                throw new JobManagementException("The table entity exceeds the the maximum allowed size (1MB).", ex);
            }
            catch (RequestFailedException ex) when (IsSpecifiedErrorCode(ex, AzureStorageErrorCode.PropertyValueTooLargeErrorCode))
            {
                _logger.LogError(ex, "[Enqueue] The property value exceeds the maximum allowed size (64KB).");
                throw new JobManagementException("The property value exceeds the maximum allowed size (64KB).", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Enqueue] Failed to enqueue jobs, unhandled error while inserting jobInfo entity and job lock entity.");
                throw new JobManagementException("Failed to enqueue jobs, unhandled error while inserting jobInfo entity and job lock entity.", ex);
            }

            // step 5: try to add reverse index for jobInfo entity
            try
            {
                transactionActions = jobInfoEntities.Select(jobInfoEntity =>
                    new TableTransactionAction(TableTransactionActionType.Add, new JobReverseIndexEntity
                    {
                        PartitionKey = AzureStorageKeyProvider.JobReverseIndexPartitionKey(queueType, (long)jobInfoEntity[JobInfoEntityProperties.Id]),
                        RowKey = AzureStorageKeyProvider.JobReverseIndexRowKey(queueType, (long)jobInfoEntity[JobInfoEntityProperties.Id]),
                        JobInfoEntityPartitionKey = jobInfoEntity.PartitionKey,
                        JobInfoEntityRowKey = jobInfoEntity.RowKey,
                    }));

                _ = await _azureJobInfoTableClient.SubmitTransactionAsync(transactionActions, cancellationToken);
                _logger.LogInformation($"[Enqueue] Insert job reverse index entities for jobs {string.Join(",", jobInfos.Select(jobInfo => jobInfo.Id).ToList())} in one transaction successfully.");
            }
            catch (RequestFailedException ex) when (IsSpecifiedErrorCode(ex, AzureStorageErrorCode.AddEntityAlreadyExistsErrorCode))
            {
                _logger.LogInformation(ex, "[Enqueue] The job reverse index entities already exist.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Enqueue] Failed to enqueue jobs, unhandled error while inserting job reverse index entity.");
                throw new JobManagementException("Failed to enqueue jobs, unhandled error while inserting job reverse index entity.", ex);
            }

            try
            {
                // for new added job lock entities, their etag are empty, need to get them to get etag.
                if (jobLockEntities.Any(jobLockEntity => string.IsNullOrWhiteSpace(jobLockEntity.ETag.ToString())))
                {
                    AsyncPageable<TableEntity>? jobLockEntityQueryResult = _azureJobInfoTableClient.QueryAsync<TableEntity>(
                        filter: TransactionGetByKeys(jobLockEntities.First().PartitionKey, jobLockEntities.Select(entity => entity.RowKey).ToList()),
                        cancellationToken: cancellationToken);

                    jobLockEntities.Clear();
                    await foreach (Page<TableEntity> pageResult in jobLockEntityQueryResult.AsPages().WithCancellation(cancellationToken))
                    {
                        jobLockEntities.AddRange(pageResult.Values);
                    }
                }

                // step 6: if queue message not present in job lock entity, push message to queue.
                // if processing job failed and the message is deleted, then the message id is still in table entity,
                // we don't resend message for it, and return the existing jobInfo, so will do noting about it
                if (jobLockEntities.Any(jobLockEntity => !jobLockEntity.ContainsKey(JobLockEntityProperties.JobMessageId)))
                {
                    foreach (TableEntity jobLockEntity in jobLockEntities.Where(jobLockEntity => !jobLockEntity.ContainsKey(JobLockEntityProperties.JobMessageId)))
                    {
                        Response<SendReceipt>? response = await _azureJobMessageQueueClient.SendMessageAsync(
                            new JobMessage(jobLockEntity.PartitionKey, jobLockEntity.GetString(JobLockEntityProperties.JobInfoEntityRowKey), jobLockEntity.RowKey).ToString(),
                            cancellationToken);

                        jobLockEntity[JobLockEntityProperties.JobMessagePopReceipt] = response.Value.PopReceipt;
                        jobLockEntity[JobLockEntityProperties.JobMessageId] = response.Value.MessageId;
                    }

                    _logger.LogInformation($"[Enqueue] Send queue message for jobs {string.Join(",", jobInfos.Select(jobInfo => jobInfo.Id).ToList())} successfully.");

                    // step 7: update message id and message pop receipt to job lock entity
                    // if enqueue concurrently, it is possible that
                    // 1. one job sends message and updates entity, another job do nothing
                    // 2. two jobs both send message, while only one job update entity successfully
                    try
                    {
                        IEnumerable<TableTransactionAction> transactionUpdateActions = jobLockEntities.Select(jobLockEntity =>
                            new TableTransactionAction(TableTransactionActionType.UpdateReplace, jobLockEntity, jobLockEntity.ETag));

                        _ = await _azureJobInfoTableClient.SubmitTransactionAsync(transactionUpdateActions, cancellationToken);

                        _logger.LogInformation($"[Enqueue] Update message id and message pop receipt in job lock entity for jobs {string.Join(",", jobInfos.Select(jobInfo => jobInfo.Id).ToList())} in one transaction successfully.");
                    }
                    catch (RequestFailedException ex) when (IsSpecifiedErrorCode(ex, AzureStorageErrorCode.UpdateEntityPreconditionFailedErrorCode))
                    {
                        _logger.LogInformation(ex, "[Enqueue] Update job lock entity conflicts.");
                    }
                }

                jobInfos = jobInfoEntities.Select(entity => entity.ToJobInfo<TJobInfo>()).ToList();

                _logger.LogInformation($"[Enqueue] Enqueue jobs {string.Join(",", jobInfos.Select(jobInfo => jobInfo.Id).ToList())} successfully.");

                return jobInfos;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Enqueue] Failed to enqueue jobs, unhandled error while sending message and updating job lock entity.");
                throw new JobManagementException("Failed to enqueue jobs, unhandled error while sending message and updating job lock entity.", ex);
            }
        }

        // return null if the queue is empty;
        // throw an exception if there is any issue for this message, the caller (jobHosting) will catch the exception, log a message and retry.
        public async Task<JobInfo> DequeueAsync(byte queueType, string worker, int heartbeatTimeoutSec, CancellationToken cancellationToken)
        {
            _logger.LogInformation("[Dequeue] Start to dequeue.");

            // step 1: receive message from message queue
            TimeSpan visibilityTimeout =
                TimeSpan.FromSeconds(heartbeatTimeoutSec <= 0
                    ? DefaultVisibilityTimeoutInSeconds
                    : heartbeatTimeoutSec);
            QueueMessage? message = (await _azureJobMessageQueueClient.ReceiveMessageAsync(visibilityTimeout, cancellationToken)).Value;

            // the queue is empty
            if (message == null)
            {
                _logger.LogInformation("[Dequeue] The queue is empty.");
                return null;
            }

            JobMessage? jobMessage = JobMessage.Parse(message.Body.ToString());
            if (jobMessage == null)
            {
                _logger.LogError($"[Dequeue] Discard queue message {message.MessageId}, failed to deserialize message {message.Body}.");
                await _azureJobMessageQueueClient.DeleteMessageAsync(message.MessageId, message.PopReceipt, cancellationToken);
                throw new JobManagementException($"Discard queue message {message.MessageId}, failed to deserialize message {message.Body}.");
            }

            // step 2: get jobInfo entity and job lock entity
            (TableEntity? jobInfoEntity, TableEntity? jobLockEntity) = await AcquireJobEntityByRowKeysAsync(jobMessage.PartitionKey, new List<string> { jobMessage.RowKey, jobMessage.LockRowKey }, cancellationToken);

            if (jobInfoEntity == null || jobLockEntity == null)
            {
                _logger.LogError($"[Dequeue] Discard queue message {message.MessageId}, failed to acquire job entity from table for message {jobMessage}.");
                await _azureJobMessageQueueClient.DeleteMessageAsync(message.MessageId, message.PopReceipt, cancellationToken);
                throw new JobManagementException($"Discard queue message {message.MessageId}, failed to acquire job entity from table for message {jobMessage}.");
            }

            var jobInfo = jobInfoEntity.ToJobInfo<TJobInfo>();

            _logger.LogInformation($"[Dequeue] Get job info entity and job lock entity for message {jobMessage} successfully.");

            // step 3: check job status
            // delete this message if job is already completed / failed / cancelled, it occurs when complete job fail, the table is updated while fail to delete message.
            // if status is running and CancelRequest is true, the job will be dequeued, and jobHosting will continue to handle it
            if (jobInfo.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled)
            {
                _logger.LogInformation($"[Dequeue] Discard queue message {message.MessageId}: {jobMessage}, the job status is {jobInfo.Status}.");
                await _azureJobMessageQueueClient.DeleteMessageAsync(message.MessageId, message.PopReceipt, cancellationToken);

                throw new JobManagementException($"Discard queue message {message.MessageId}: {jobMessage}, the job status is {jobInfo.Status}.");
            }

            // step 4: check if the message id in job lock entity is null
            // it occurs when the message is enqueued and dequeued immediately before updating the message info to table entity, skip processing it this time.
            if (!jobLockEntity.ContainsKey(JobLockEntityProperties.JobMessageId))
            {
                _logger.LogInformation($"[Dequeue] The message id field in job lock entity is null, skip processing this message {message.MessageId}: {jobMessage} this time.");
                return null;
            }

            // step 5: check this message is consistent with table
            // it occurs when enqueue fails at first time, the message is sent while fail to update job lock entity, and re-enqueue, will send a new message, the first message's id is inconsistent with job lock entity
            if (!string.Equals(jobLockEntity.GetString(JobLockEntityProperties.JobMessageId), message.MessageId, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation($"[Dequeue] Discard queue message {message.MessageId}: {jobMessage}, the message id is inconsistent with the one in the table entity.");
                await _azureJobMessageQueueClient.DeleteMessageAsync(message.MessageId, message.PopReceipt, cancellationToken);

                throw new JobManagementException(
                    $"Discard queue message {message.MessageId}: {jobMessage}, the message id is inconsistent with the one in the table entity.");
            }

            // step 6: skip it if the job is running and still active
            if (jobInfo.Status == JobStatus.Running && jobInfo.HeartbeatDateTime.AddSeconds(heartbeatTimeoutSec) > DateTime.UtcNow)
            {
                _logger.LogInformation($"[Dequeue] Job {jobInfo.Id} of this message {message.MessageId}: {jobMessage} is still active.");

                throw new JobManagementException($"Job {jobInfo.Id} of this message {message.MessageId}: {jobMessage} is still active.");
            }

            // step 7: update jobInfo entity's status to running, also update version and heartbeat
            jobInfo.Status = JobStatus.Running;

            // jobInfo's version is set when dequeue, if there are multi running jobs for this jobInfo, only the last one will keep alive successfully
            jobInfo.Version = DateTimeOffset.UtcNow.Ticks;
            jobInfo.HeartbeatDateTime = DateTime.UtcNow;
            jobInfo.HeartbeatTimeoutSec = heartbeatTimeoutSec;
            var updatedJobInfoEntity = jobInfo.ToTableEntity();

            // step 8: update message pop receipt to job lock entity
            jobLockEntity[JobLockEntityProperties.JobMessagePopReceipt] = message.PopReceipt;

            // step 9: transaction update jobInfo entity and job lock entity
            IEnumerable<TableTransactionAction> transactionUpdateActions = new List<TableTransactionAction>
            {
                new TableTransactionAction(TableTransactionActionType.UpdateReplace, updatedJobInfoEntity, jobInfoEntity.ETag),
                new TableTransactionAction(TableTransactionActionType.UpdateReplace, jobLockEntity, jobLockEntity.ETag),
            };

            _ = await _azureJobInfoTableClient.SubmitTransactionAsync(transactionUpdateActions, cancellationToken);

            _logger.LogInformation($"[Dequeue] Dequeue job {jobInfo.Id} Successfully.");
            return jobInfo;
        }

        public async Task<JobInfo> GetJobByIdAsync(byte queueType, long jobId, bool returnDefinition, CancellationToken cancellationToken)
        {
            _logger.LogInformation($"[GetJobById] Start to get job {jobId}.");

            // step 1: get job reverse index entity
            JobReverseIndexEntity jobReverseIndexEntity = await GetJobReverseIndexEntityByIdAsync(queueType, jobId, cancellationToken);

            // step 2: get job info entity
            IEnumerable<string>? selectedProperties = returnDefinition ? null : SelectPropertiesExceptDefinition();

            Response<TableEntity>? jobInfoEntityResponse = await _azureJobInfoTableClient.GetEntityAsync<TableEntity>(
                jobReverseIndexEntity.JobInfoEntityPartitionKey,
                jobReverseIndexEntity.JobInfoEntityRowKey,
                selectedProperties,
                cancellationToken);
            TableEntity jobInfoEntity = jobInfoEntityResponse.Value;

            // step 3: convert to job info.
            var jobInfo = jobInfoEntity.ToJobInfo<TJobInfo>();

            _logger.LogInformation($"[GetJobById] Get job {jobId} successfully.");
            return jobInfo;
        }

        public async Task<IEnumerable<JobInfo>> GetJobsByIdsAsync(byte queueType, long[] jobIds, bool returnDefinition, CancellationToken cancellationToken)
        {
            _logger.LogInformation($"[GetJobsByIds] Start to get jobs {string.Join(",", jobIds)}.");

            ConcurrentBag<JobInfo> result = new ConcurrentBag<JobInfo>();

            // https://docs.microsoft.com/en-us/dotnet/api/system.threading.semaphoreslim?view=net-6.0
            using (var throttler = new SemaphoreSlim(MaxThreadsCountForGettingJob, MaxThreadsCountForGettingJob))
            {
                IEnumerable<Task> tasks = jobIds.Select(async id =>
                {
                    await throttler.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        result.Add(await GetJobByIdAsync(queueType, id, returnDefinition, cancellationToken));
                    }
                    finally
                    {
                        throttler.Release();
                    }
                });

                try
                {
                    await Task.WhenAll(tasks);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"[GetJobsByIds] Failed to get jobs by ids {string.Join(",", jobIds)}.", ex);
                    throw new JobManagementException($"Failed to get jobs by ids {string.Join(",", jobIds)}.", ex);
                }
            }

            _logger.LogInformation($"[GetJobsByIds] Get jobs {string.Join(",", jobIds)} successfully.");
            return result;
        }

        public async Task<IEnumerable<JobInfo>> GetJobByGroupIdAsync(byte queueType, long groupId, bool returnDefinition, CancellationToken cancellationToken)
        {
            _logger.LogInformation($"[GetJobByGroupId] Start to get jobs in group {groupId}.");

            List<JobInfo> jobs = new List<JobInfo>();

            IEnumerable<string>? selectedProperties = returnDefinition ? null : SelectPropertiesExceptDefinition();

            // job lock entity has the same partition key, so we need to query the row key here
            AsyncPageable<TableEntity>? queryResult = _azureJobInfoTableClient.QueryAsync<TableEntity>(
                filter: FilterJobInfosByGroupId(queueType, groupId),
                select: selectedProperties,
                cancellationToken: cancellationToken);
            await foreach (Page<TableEntity> pageResult in queryResult.AsPages().WithCancellation(cancellationToken))
            {
                jobs.AddRange(pageResult.Values.Select(entity => entity.ToJobInfo<TJobInfo>()));
            }

            _logger.LogInformation($"[GetJobByGroupId] Get jobs in group {groupId} successfully.");
            return jobs;
        }

        public async Task<bool> KeepAliveJobAsync(JobInfo jobInfo, CancellationToken cancellationToken)
        {
            _logger.LogInformation($"[KeepAliveJob] Start to keep alive for job {jobInfo.Id}.");

            // step 1: get jobInfo entity and job lock entity
            (TableEntity? jobInfoEntity, TableEntity? jobLockEntity) = await AcquireJobEntityByJobInfoAsync(jobInfo, cancellationToken);
            if (jobInfoEntity == null || jobLockEntity == null)
            {
                _logger.LogError($"[KeepAliveJob] Failed to acquire job entity from table for job {jobInfo.Id}.");

                throw new JobNotExistException($"Failed to acquire job entity from table for job {jobInfo.Id}.");
            }

            _logger.LogInformation($"[KeepAliveJob] The job entity for job {jobInfo.Id} is acquired successfully.");

            // step 2: check version
            // the version is assigned when dequeue,
            // if the version does not match, means there are more than one running jobs for it, only the last one keep alive
            if ((long)jobInfoEntity[JobInfoEntityProperties.Version] != jobInfo.Version)
            {
                _logger.LogInformation($"[KeepAliveJob] Job {jobInfo.Id} precondition failed, version does not match.");

                throw new JobNotExistException($"Job {jobInfo.Id} precondition failed, version does not match.");
            }

            // step 3: update message visibility timeout
            Response<UpdateReceipt>? response;
            try
            {
                TimeSpan visibilityTimeout = TimeSpan.FromSeconds((long)jobInfoEntity[JobInfoEntityProperties.HeartbeatTimeoutSec] <= 0
                    ? DefaultVisibilityTimeoutInSeconds
                    : (long)jobInfoEntity[JobInfoEntityProperties.HeartbeatTimeoutSec]);
                response = await _azureJobMessageQueueClient.UpdateMessageAsync(
                    jobLockEntity.GetString(JobLockEntityProperties.JobMessageId),
                    jobLockEntity.GetString(JobLockEntityProperties.JobMessagePopReceipt),
                    visibilityTimeout: visibilityTimeout,
                    cancellationToken: cancellationToken);

                _logger.LogInformation($"[KeepAliveJob] Update message visibility timeout for job {jobInfo.Id} successfully.");
            }
            catch (RequestFailedException ex) when (IsSpecifiedErrorCode(ex, AzureStorageErrorCode.UpdateOrDeleteMessageNotFoundErrorCode) || IsSpecifiedErrorCode(ex, AzureStorageErrorCode.PopReceiptMismatchErrorCode))
            {
                // the message does not exists or pop receipt does not match
                // Note: there are two error codes defined: 404 "MessageNotFound" and 400 "PopReceiptMismatch" in https://learn.microsoft.com/en-us/rest/api/storageservices/Queue-Service-Error-Codes?redirectedfrom=MSDN
                // However, the doc says that "If a message with a matching pop receipt isn't found, the service returns error code 404 (Not Found). This error occurs in the previously listed cases in which the pop receipt is no longer valid." That means the queue service would return 400 when pop receipt is not in correct format, and return 404 when the pop receipt was of the correct format but the service did not find the message id with that receipt
                // It throws 404 in local machine, while throw 400 in Pipeline
                // https://social.msdn.microsoft.com/Forums/azure/en-US/aab37e27-2f04-47db-9e1d-66fd224ac925/handling-queue-message-deletion-error?forum=windowsazuredata
                // http://www.tiernok.com/posts/real-world-azure-queue-popreceiptmismatch.html
                _logger.LogInformation($"[KeepAliveJob] Failed to keep alive for job {jobInfo.Id}, the job message with the specified pop receipt is not found, the job is {jobInfo.Status}.");
                throw new JobNotExistException($"Failed to keep alive for job {jobInfo.Id}, the job message with the specified pop receipt is not found, the job is {jobInfo.Status}.", ex);
            }

            // step 4: sync result to jobInfo entity
            // if update message successfully while updating table entity failed, then the message pop receipt is invalid,
            // keeping alive always fails to update message, so the message will be visible and dequeue again
            // when re-dequeue, the new message pop receipt is updated to table entity,
            // and the previous job will throw JobNotExistException as the version doesn't match, and jobHosting cancels the previous job.
            jobInfoEntity[JobInfoEntityProperties.HeartbeatDateTime] = DateTime.UtcNow;

            jobInfoEntity[JobInfoEntityProperties.Result] = jobInfo.Result;

            // step 5: update message pop receipt to job lock entity
            jobLockEntity[JobLockEntityProperties.JobMessagePopReceipt] = response?.Value.PopReceipt;

            // step 6: update jobInfo entity and job lock entity in one transaction
            IEnumerable<TableTransactionAction> transactionUpdateActions = new List<TableTransactionAction>
            {
                new TableTransactionAction(TableTransactionActionType.UpdateReplace, jobInfoEntity, jobInfoEntity.ETag),
                new TableTransactionAction(TableTransactionActionType.UpdateReplace, jobLockEntity, jobLockEntity.ETag),
            };
            try
            {
                _ = await _azureJobInfoTableClient.SubmitTransactionAsync(transactionUpdateActions, cancellationToken);
            }
            catch (RequestFailedException ex) when (IsSpecifiedErrorCode(ex, AzureStorageErrorCode.RequestBodyTooLargeErrorCode))
            {
                _logger.LogError(ex, "[KeepAliveJob] The table entity exceeds the the maximum allowed size (1MB).");
                throw new JobManagementException("The table entity exceeds the the maximum allowed size (1MB).", ex);
            }
            catch (RequestFailedException ex) when (IsSpecifiedErrorCode(ex, AzureStorageErrorCode.PropertyValueTooLargeErrorCode))
            {
                _logger.LogError(ex, "[KeepAliveJob] The property value exceeds the maximum allowed size (64KB).");
                throw new JobManagementException("The property value exceeds the maximum allowed size (64KB).", ex);
            }

            // step 7: check if cancel requested
            bool shouldCancel = (bool)jobInfoEntity[JobInfoEntityProperties.CancelRequested];

            _logger.LogInformation($"[KeepAliveJob] Keep alive for job {jobInfo.Id} successfully.");

            return shouldCancel;
        }

        public async Task CancelJobByGroupIdAsync(byte queueType, long groupId, CancellationToken cancellationToken)
        {
            _logger.LogInformation($"[CancelJobByGroupId] Start to cancel jobs in group {groupId}.");

            List<TableEntity> jobInfoEntities = new List<TableEntity>();

            // step 1: query all jobs in a group, using range query for row key to ignore lock/index entities in same partition
            AsyncPageable<TableEntity>? queryResult = _azureJobInfoTableClient.QueryAsync<TableEntity>(
                filter: FilterJobInfosByGroupId(queueType, groupId),
                cancellationToken: cancellationToken);
            await foreach (Page<TableEntity> pageResult in queryResult.AsPages().WithCancellation(cancellationToken))
            {
                foreach (TableEntity? jobInfoEntity in pageResult.Values)
                {
                    // step 2: cancel job.
                    CancelJobInternal(jobInfoEntity);
                    jobInfoEntities.Add(jobInfoEntity);
                }
            }

            if (!jobInfoEntities.Any())
            {
                _logger.LogInformation($"[CancelJobByGroupId] There are no jobs in group {groupId}.");
                return;
            }

            _logger.LogInformation($"[CancelJobByGroupId] There are {jobInfoEntities.Count} jobs acquired successfully.");

            // step 3: update the cancelled job info entities in one transaction
            IEnumerable<TableTransactionAction> transactionActions = jobInfoEntities.Select(entity =>
                new TableTransactionAction(TableTransactionActionType.UpdateReplace, entity, entity.ETag));

            Response<IReadOnlyList<Response>>? responseList = await _azureJobInfoTableClient.SubmitTransactionAsync(transactionActions, cancellationToken);
            bool batchFailed = responseList.Value.Any(response => response.IsError);

            // step 4: log error and throw exceptions for failure
            if (batchFailed)
            {
                string errorMessage = responseList.Value.Where(response => response.IsError).Select(response => response.ReasonPhrase).First();
                _logger.LogError($"[CancelJobByGroupId] Failed to cancel jobs in group {groupId}: {errorMessage}");
                throw new JobManagementException($"Failed to cancel jobs in group {groupId}: {errorMessage}");
            }

            _logger.LogInformation($"[CancelJobByGroupId] Cancel jobs in group {groupId} successfully.");
        }

        public async Task CancelJobByIdAsync(byte queueType, long jobId, CancellationToken cancellationToken)
        {
            _logger.LogInformation($"[CancelJobById] Start to cancel job {jobId}.");

            // step 1: get reverse index entity.
            JobReverseIndexEntity reverseIndexEntity = await GetJobReverseIndexEntityByIdAsync(queueType, jobId, cancellationToken);

            // step 2: get jobInfo entity
            TableEntity? jobInfoEntity = (await _azureJobInfoTableClient.GetEntityAsync<TableEntity>(reverseIndexEntity.JobInfoEntityPartitionKey, reverseIndexEntity.JobInfoEntityRowKey, cancellationToken: cancellationToken)).Value;

            _logger.LogInformation($"[CancelJobById] The job info entity for job {jobId} is acquired successfully.");

            // step 3: cancel job
            CancelJobInternal(jobInfoEntity);

            // step 4: update job info entity to table.
            await _azureJobInfoTableClient.UpdateEntityAsync(jobInfoEntity, jobInfoEntity.ETag, cancellationToken: cancellationToken);

            _logger.LogInformation($"[CancelJobById] Cancel job {jobId} successfully.");
        }

        public async Task CompleteJobAsync(JobInfo jobInfo, bool requestCancellationOnFailure, CancellationToken cancellationToken)
        {
            _logger.LogInformation($"[CompleteJob] Start to complete job {jobInfo.Id}.");

            // step 1: get jobInfo entity and job lock entity
            (TableEntity? retrievedJobInfoEntity, TableEntity? jobLockEntity) = await AcquireJobEntityByJobInfoAsync(jobInfo, cancellationToken);
            if (retrievedJobInfoEntity == null || jobLockEntity == null)
            {
                _logger.LogError($"[CompleteJob] Failed to acquire job entity from table for job {jobInfo.Id}.");
                throw new JobNotExistException($"Failed to acquire job entity from table for job {jobInfo.Id}.");
            }

            _logger.LogInformation($"[CompleteJob] The job entity for job {jobInfo.Id} is acquired successfully.");

            // step 2: check version
            if ((long)retrievedJobInfoEntity[JobInfoEntityProperties.Version] != jobInfo.Version)
            {
                _logger.LogInformation($"[CompleteJob] Job {jobInfo.Id} precondition failed, version does not match.");
                throw new JobNotExistException($"Job {jobInfo.Id} precondition failed, version does not match.");
            }

            // step 3: get CancelRequested
            bool shouldCancel = (bool)retrievedJobInfoEntity[JobInfoEntityProperties.CancelRequested];

            // step 4: update status
            // Reference: https://github.com/microsoft/fhir-server/blob/e1117009b6db995672cc4d31457cb3e6f32e19a3/src/Microsoft.Health.Fhir.SqlServer/Features/Schema/Sql/Sprocs/PutJobStatus.sql#L16
            var jobInfoEntity = ((TJobInfo)jobInfo).ToTableEntity();
            if ((int)jobInfoEntity[JobInfoEntityProperties.Status] == (int)JobStatus.Failed)
            {
                jobInfoEntity[JobInfoEntityProperties.Status] = (int)JobStatus.Failed;
            }
            else if (shouldCancel)
            {
                jobInfoEntity[JobInfoEntityProperties.Status] = (int)JobStatus.Cancelled;
            }
            else
            {
                jobInfoEntity[JobInfoEntityProperties.Status] = (int)JobStatus.Completed;
            }

            // step 5: update job info entity to table
            try
            {
                await _azureJobInfoTableClient.UpdateEntityAsync(jobInfoEntity, ETag.All, cancellationToken: cancellationToken);
                _logger.LogInformation($"[CompleteJob] Update job info entity for job {jobInfo.Id} to table successfully.");
            }
            catch (RequestFailedException ex) when (IsSpecifiedErrorCode(ex, AzureStorageErrorCode.RequestBodyTooLargeErrorCode))
            {
                _logger.LogError(ex, "[CompleteJob] The table entity exceeds the the maximum allowed size (1MB).");
                throw new JobManagementException("The table entity exceeds the the maximum allowed size (1MB).", ex);
            }
            catch (RequestFailedException ex) when (IsSpecifiedErrorCode(ex, AzureStorageErrorCode.PropertyValueTooLargeErrorCode))
            {
                _logger.LogError(ex, "[CompleteJob] The property value exceeds the maximum allowed size (64KB).");
                throw new JobManagementException("The property value exceeds the maximum allowed size (64KB).", ex);
            }

            // step 6: delete message
            // if table entity is updated successfully while delete message fails, then the message is visible and dequeue again,
            // and the message will be deleted since the table entity's status is completed/failed/cancelled
            try
            {
                await _azureJobMessageQueueClient.DeleteMessageAsync(jobLockEntity.GetString(JobLockEntityProperties.JobMessageId), jobLockEntity.GetString(JobLockEntityProperties.JobMessagePopReceipt), cancellationToken: cancellationToken);
                _logger.LogInformation($"[CompleteJob] Delete message for job {jobInfo.Id} successfully.");
            }
            catch (RequestFailedException ex) when (IsSpecifiedErrorCode(ex, AzureStorageErrorCode.UpdateOrDeleteMessageNotFoundErrorCode) || IsSpecifiedErrorCode(ex, AzureStorageErrorCode.PopReceiptMismatchErrorCode))
            {
                _logger.LogInformation(ex, $"[CompleteJob] Failed to delete message for job {jobInfo.Id}, the job message with the specified pop receipt is not found, the job is {jobInfo.Status}.");
            }

            // step 7: cancel group jobs if requested
            if (requestCancellationOnFailure && jobInfo.Status == JobStatus.Failed)
            {
                await CancelJobByGroupIdAsync(jobInfo.QueueType, jobInfo.GroupId, cancellationToken);
                _logger.LogInformation($"[CompleteJob] Cancel jobs in the group {jobInfo.GroupId} for job {jobInfo.Id} successfully.");
            }

            _logger.LogInformation($"[CompleteJob] Complete job {jobInfo.Id} successfully.");
        }

        private void TryInitialize()
        {
            try
            {
                _azureJobInfoTableClient.CreateIfNotExists();
                _azureJobMessageQueueClient.CreateIfNotExists();
                _isInitialized = true;
                _logger.LogInformation("Initialize azure storage client successfully.");
            }
            catch (RequestFailedException ex) when (IsAuthenticationError(ex))
            {
                _logger.LogInformation(ex, "Failed to initialize azure storage client due to authentication issue.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize azure storage client.");
            }
        }

        /// <summary>
        /// Get incremental job ids, will retry if update job id entity conflicts
        /// </summary>
        /// <param name="queueType">the queue type.</param>
        /// <param name="count">the count of job ids to be retrieved.</param>
        /// <param name="retryCount">retry times for conflicts.</param>
        /// <param name="cancellationToken">the cancellation token.</param>
        /// <returns>The job ids, throw exceptions if fails.</returns>
        private async Task<List<long>> GetIncrementalJobIds(byte queueType, int count, int retryCount, CancellationToken cancellationToken)
        {
            _logger.LogInformation($"Start to generate {count} job ids.");
            string partitionKey = AzureStorageKeyProvider.JobIdPartitionKey(queueType);
            string rowKey = AzureStorageKeyProvider.JobIdRowKey(queueType);
            JobIdEntity entity;
            try
            {
                Response<JobIdEntity>? response = await _azureJobInfoTableClient.GetEntityAsync<JobIdEntity>(partitionKey, rowKey, cancellationToken: cancellationToken);
                entity = response.Value;
            }
            catch (RequestFailedException ex) when (IsSpecifiedErrorCode(ex, AzureStorageErrorCode.GetEntityNotFoundErrorCode))
            {
                _logger.LogInformation(ex, "The job id entity doesn't exist, will create one.");

                // create new entity if not exist
                var initialJobIdEntity = new JobIdEntity
                {
                    PartitionKey = partitionKey,
                    RowKey = rowKey,
                    NextJobId = 0,
                };

                try
                {
                    await _azureJobInfoTableClient.AddEntityAsync(initialJobIdEntity, cancellationToken);
                }
                catch (RequestFailedException exception) when (IsSpecifiedErrorCode(exception, AzureStorageErrorCode.AddEntityAlreadyExistsErrorCode))
                {
                    _logger.LogInformation(exception, "Failed to add job id entity, the entity already exists.");
                }

                // get the job id entity again
                entity = (await _azureJobInfoTableClient.GetEntityAsync<JobIdEntity>(partitionKey, rowKey, cancellationToken: cancellationToken)).Value;
            }

            List<long> result = new List<long>();

            for (int i = 0; i < count; i++)
            {
                result.Add(entity.NextJobId);
                entity.NextJobId++;
            }

            try
            {
                await _azureJobInfoTableClient.UpdateEntityAsync(entity, entity.ETag, cancellationToken: cancellationToken);
            }
            catch (RequestFailedException ex) when (IsSpecifiedErrorCode(ex, AzureStorageErrorCode.UpdateEntityPreconditionFailedErrorCode))
            {
                if (retryCount >= MaxRetryCountForUpdateJobIdEntityConflict)
                {
                    _logger.LogError(ex, $"Failed to get job ids after {retryCount} retries, updating job id entity conflicts");
                    throw new JobManagementException("Failed to get job ids after {retryCount} retries, updating job id entity conflicts", ex);
                }

                _logger.LogInformation(ex, $"Update job id entity conflicts, will make retry {retryCount + 1}.");

                // try to get job ids again
                result = await GetIncrementalJobIds(queueType, count, retryCount + 1, cancellationToken);
            }

            _logger.LogInformation($"Generate {count} job ids {string.Join(',', result)} successfully.");

            return result;
        }

        private async Task<JobReverseIndexEntity> GetJobReverseIndexEntityByIdAsync(byte queueType, long jobId, CancellationToken cancellationToken)
        {
            string reversePartitionKey = AzureStorageKeyProvider.JobReverseIndexPartitionKey(queueType, jobId);
            string reverseRowKey = AzureStorageKeyProvider.JobReverseIndexRowKey(queueType, jobId);
            try
            {
                Response<JobReverseIndexEntity>? reverseIndexResponse = await _azureJobInfoTableClient.GetEntityAsync<JobReverseIndexEntity>(reversePartitionKey, reverseRowKey, cancellationToken: cancellationToken);
                return reverseIndexResponse.Value;
            }
            catch (RequestFailedException ex) when (IsSpecifiedErrorCode(ex, AzureStorageErrorCode.GetEntityNotFoundErrorCode))
            {
                _logger.LogError($"Failed to get job reverse index entity by id {jobId}, the job reverse index entity does not exist.", ex);
                throw new JobManagementException($"Failed to get job reverse index entity by id {jobId}, the job reverse index entity does not exist.");
            }
        }

        private async Task<Tuple<TableEntity?, TableEntity?>> AcquireJobEntityByJobInfoAsync(
            JobInfo jobInfo,
            CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(jobInfo, nameof(jobInfo));

            string pk = AzureStorageKeyProvider.JobInfoPartitionKey(jobInfo.QueueType, jobInfo.GroupId);
            List<string> rks = new List<string>
            {
                AzureStorageKeyProvider.JobLockRowKey(((TJobInfo)jobInfo).JobIdentifier()),
                AzureStorageKeyProvider.JobInfoRowKey(jobInfo.GroupId, jobInfo.Id),
            };

            return await AcquireJobEntityByRowKeysAsync(pk, rks, cancellationToken);
        }

        private async Task<Tuple<TableEntity?, TableEntity?>> AcquireJobEntityByRowKeysAsync(
            string pk,
            List<string> rks,
            CancellationToken cancellationToken)
        {
            AsyncPageable<TableEntity>? jobEntityQueryResult = _azureJobInfoTableClient.QueryAsync<TableEntity>(
                filter: TransactionGetByKeys(pk, rks),
                cancellationToken: cancellationToken);

            List<TableEntity> retrievedJobInfoEntities = new List<TableEntity>();
            List<TableEntity> retrievedJobLockEntities = new List<TableEntity>();

            await foreach (Page<TableEntity> pageResult in jobEntityQueryResult.AsPages().WithCancellation(cancellationToken))
            {
                retrievedJobInfoEntities.AddRange(pageResult.Values.Where(entity => entity.ContainsKey(JobInfoEntityProperties.Id)));
                retrievedJobLockEntities.AddRange(pageResult.Values.Where(entity => !entity.ContainsKey(JobInfoEntityProperties.Id)));
            }

            return new Tuple<TableEntity?, TableEntity?>(retrievedJobInfoEntities.FirstOrDefault(), retrievedJobLockEntities.FirstOrDefault());
        }

        private static bool IsSpecifiedErrorCode(RequestFailedException exception, string expectedErrorCode) =>
            string.Equals(exception.ErrorCode, expectedErrorCode, StringComparison.OrdinalIgnoreCase);

        private static bool IsAuthenticationError(RequestFailedException exception) =>
            string.Equals(exception.ErrorCode, AzureStorageErrorCode.NoAuthenticationInformationErrorCode, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(exception.ErrorCode, AzureStorageErrorCode.InvalidAuthenticationInfoErrorCode, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(exception.ErrorCode, AzureStorageErrorCode.AuthenticationFailedErrorCode, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(exception.ErrorCode, AzureStorageErrorCode.AuthorizationFailureErrorCode, StringComparison.OrdinalIgnoreCase);

        private static string FilterJobInfosByGroupId(byte queueType, long groupId) =>
            $"PartitionKey eq '{AzureStorageKeyProvider.JobInfoPartitionKey(queueType, groupId)}' and RowKey ge '{groupId:D20}' and RowKey lt '{groupId + 1:D20}'";

        private static string TransactionGetByKeys(string pk, List<string> rowKeys) =>
        $"PartitionKey eq '{pk}' and ({string.Join(" or ", rowKeys.Select(rowKey => $"RowKey eq '{rowKey}'"))})";

        private static IEnumerable<string> SelectPropertiesExceptDefinition()
        {
            var type = Type.GetType(typeof(JobInfoEntityProperties).FullName ?? throw new InvalidOperationException());
            if (type == null)
            {
                throw new JobManagementException("Failed to get JobInfoEntity properties, the type is null.");
            }

            string[] tableEntityProperties = { "PartitionKey", "RowKey", "Timestamp", "ETag" };
            return tableEntityProperties.Concat(type.GetFields().Select(p => p.Name).Except(new List<string> { JobInfoEntityProperties.Definition }));
        }

        /// <summary>
        /// when cancel a job, always set the cancelRequest to true, and set its status to cancelled only when it's created,
        /// for other cases, shouldCancel will be returned when keep alive, and jobHosting will cancel this job and set job to completed
        /// </summary>
        private static void CancelJobInternal(TableEntity jobInfoEntity)
        {
            // set jobInfo entity's cancel request to true.
            jobInfoEntity[JobInfoEntityProperties.CancelRequested] = true;

            // only set job status to cancelled when the job status is created.
            if ((int)jobInfoEntity[JobInfoEntityProperties.Status] == (int)JobStatus.Created)
            {
                jobInfoEntity[JobInfoEntityProperties.Status] = (int)JobStatus.Cancelled;
            }
        }
    }
}