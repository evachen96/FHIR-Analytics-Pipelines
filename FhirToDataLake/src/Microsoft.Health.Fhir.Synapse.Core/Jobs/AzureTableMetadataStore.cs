﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using EnsureThat;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.Synapse.Common.Configurations;
using Microsoft.Health.Fhir.Synapse.Common.Logging;
using Microsoft.Health.Fhir.Synapse.Core.Jobs.Models.AzureStorage;
using Microsoft.Health.Fhir.Synapse.JobManagement;

namespace Microsoft.Health.Fhir.Synapse.Core.Jobs
{
    public class AzureTableMetadataStore : IMetadataStore
    {
        private readonly TableClient _metadataTableClient;
        private readonly ILogger<AzureTableMetadataStore> _logger;

        private const int MaxCountOfQueryEntities = 50;

        // The entities count of a transaction is limited to 100
        // https://docs.microsoft.com/en-us/rest/api/storageservices/table-service-error-codes
        private const int MaxCountOfTransactionEntities = 100;

        private bool _isInitialized;

        public AzureTableMetadataStore(
            IAzureTableClientFactory azureTableClientFactory,
            IOptions<JobConfiguration> config,
            ILogger<AzureTableMetadataStore> logger)
        {
            EnsureArg.IsNotNull(azureTableClientFactory, nameof(azureTableClientFactory));
            EnsureArg.IsNotNull(config, nameof(config));
            EnsureArg.IsNotNullOrWhiteSpace(config.Value.AgentName, nameof(config.Value.AgentName));

            _metadataTableClient = azureTableClientFactory.Create(TableKeyProvider.MetadataTableName(config.Value.AgentName));
            _metadataTableClient.CreateIfNotExists();
            _logger = EnsureArg.IsNotNull(logger, nameof(logger));
            _isInitialized = false;
        }

        public async Task<TriggerLeaseEntity> GetTriggerLeaseEntityAsync(byte queueType, CancellationToken cancellationToken = default)
        {
            return (await _metadataTableClient.GetEntityAsync<TriggerLeaseEntity>(
                TableKeyProvider.LeasePartitionKey(queueType),
                TableKeyProvider.LeaseRowKey(queueType),
                cancellationToken: cancellationToken)).Value;
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

        public async Task<Response> AddEntityAsync(ITableEntity tableEntity, CancellationToken cancellationToken = default)
        {
            return await _metadataTableClient.AddEntityAsync(tableEntity, cancellationToken);
        }

        public async Task<Response> UpdateEntityAsync(ITableEntity tableEntity, CancellationToken cancellationToken = default)
        {
            return await _metadataTableClient.UpdateEntityAsync(
                tableEntity,
                tableEntity.ETag,
                cancellationToken: cancellationToken);
        }

        public async Task<CurrentTriggerEntity> GetCurrentTriggerEntityAsync(byte queueType, CancellationToken cancellationToken = default)
        {
            CurrentTriggerEntity entity = null;
            try
            {
                var response = await _metadataTableClient.GetEntityAsync<CurrentTriggerEntity>(
                    TableKeyProvider.TriggerPartitionKey(queueType),
                    TableKeyProvider.TriggerRowKey(queueType),
                    cancellationToken: cancellationToken);

                // Todo: test behavior if status is not 200.
                if (response.GetRawResponse().Status == 200)
                {
                    entity = response.Value;
                }
                else
                {
                    _logger.LogError($"Failed to get current trigger entity from table, status {response.GetRawResponse().Status}");
                }
            }
            catch (RequestFailedException ex) when (ex.ErrorCode == AzureStorageErrorCode.GetEntityNotFoundErrorCode)
            {
                _logger.LogInformation("The current trigger doesn't exist, will create a new one.");
            }
            catch (Exception ex)
            {
                // any exceptions while getting entity will log a error and try next time
                _logger.LogError(ex, $"Failed to get current trigger entity from table, exception: {ex.Message}");
                throw;
            }

            return entity;
        }

        public async Task<CompartmentInfoEntity> GetCompartmentInfoEntityAsync(byte queueType, string patientId, CancellationToken cancellationToken = default)
        {
            return (await _metadataTableClient.GetEntityAsync<CompartmentInfoEntity>(TableKeyProvider.CompartmentPartitionKey(queueType), TableKeyProvider.CompartmentRowKey(patientId), cancellationToken: cancellationToken)).Value;
        }

        public async Task<Dictionary<string, long>> GetPatientVersionsAsync(byte queueType, List<string> patientsHash, CancellationToken cancellationToken = default)
        {
            var pk = TableKeyProvider.CompartmentPartitionKey(queueType);
            var patientVersions = new Dictionary<string, long>();

            for (var i = 0; i < patientsHash.Count; i += MaxCountOfQueryEntities)
            {
                var selectedPatients = patientsHash.Skip(i).Take(MaxCountOfQueryEntities).ToList();
                var jobEntityQueryResult = _metadataTableClient.QueryAsync<CompartmentInfoEntity>(
                        filter: TransactionGetByKeys(pk, selectedPatients),
                        cancellationToken: cancellationToken);

                await foreach (var pageResult in jobEntityQueryResult.AsPages().WithCancellation(cancellationToken))
                {
                    foreach (var entity in pageResult.Values)
                    {
                        patientVersions[entity.RowKey] = entity.VersionId;
                    }
                }
            }

            return patientVersions;
        }

        public async Task<Dictionary<string, long>> GetPatientVersionsAsync(byte queueType, CancellationToken cancellationToken = default)
        {
            var patientVersions = new Dictionary<string, long>();
            var jobEntityQueryResult = _metadataTableClient.QueryAsync<CompartmentInfoEntity>(
                filter: $"PartitionKey eq '{TableKeyProvider.CompartmentPartitionKey(queueType)}'",
                cancellationToken: cancellationToken);

            await foreach (var pageResult in jobEntityQueryResult.AsPages().WithCancellation(cancellationToken))
            {
                foreach (var entity in pageResult.Values)
                {
                    patientVersions[entity.RowKey] = entity.VersionId;
                }
            }

            return patientVersions;
        }

        public async Task UpdatePatientVersionsAsync(byte queueType, Dictionary<string, long> patientVersions, CancellationToken cancellationToken = default)
        {
            var transactionActions = patientVersions
                .Select(patientVersion => new TableTransactionAction(TableTransactionActionType.UpsertReplace, new CompartmentInfoEntity
                {
                    PartitionKey = TableKeyProvider.CompartmentPartitionKey(queueType),
                    RowKey = patientVersion.Key,
                    VersionId = patientVersion.Value,
                })).ToList();

            for (var i = 0; i < patientVersions.Count; i += MaxCountOfTransactionEntities)
            {
                var selectedTransactionActions = transactionActions.Skip(i).Take(MaxCountOfTransactionEntities).ToList();
                if (selectedTransactionActions.Any())
                {
                    await _metadataTableClient.SubmitTransactionAsync(selectedTransactionActions, cancellationToken);
                }
            }
        }

        public async Task DeleteMetadataTableAsync()
        {
            await _metadataTableClient.DeleteAsync();
        }

        private void TryInitialize()
        {
            try
            {
                _metadataTableClient.CreateIfNotExists();
                _isInitialized = true;
                _logger.LogInformation("Initialize metadata store successfully.");
            }
            catch (RequestFailedException ex) when (IsAuthenticationError(ex))
            {
                _logger.LogInformation(ex, "Failed to initialize metadata store due to authentication issue.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize metadata store.");
            }
        }

        private static bool IsAuthenticationError(RequestFailedException exception) =>
            string.Equals(exception.ErrorCode, AzureStorageErrorCode.NoAuthenticationInformationErrorCode, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(exception.ErrorCode, AzureStorageErrorCode.InvalidAuthenticationInfoErrorCode, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(exception.ErrorCode, AzureStorageErrorCode.AuthenticationFailedErrorCode, StringComparison.OrdinalIgnoreCase);

        private static string TransactionGetByKeys(string pk, List<string> rowKeys) =>
            $"PartitionKey eq '{pk}' and ({string.Join(" or ", rowKeys.Select(rowKey => $"RowKey eq '{rowKey}'"))})";
    }
}