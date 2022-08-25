﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Microsoft.Extensions.Logging;
using Microsoft.Health.Fhir.Synapse.DataClient;
using Microsoft.Health.Fhir.Synapse.DataClient.Models.FhirApiOption;
using Microsoft.Health.Fhir.Synapse.HealthCheck.Models;

namespace Microsoft.Health.Fhir.Synapse.HealthCheck.Checkers
{
    public class FhirServerHealthChecker : BaseHealthChecker
    {
        private const string SampleResourceType = "Patient";
        private readonly IFhirDataClient _fhirApiDataClient;
        private readonly BaseSearchOptions _searchOptions;

        public FhirServerHealthChecker(
            IFhirDataClient fhirApiDataClient,
            ILogger<FhirServerHealthChecker> logger)
            : base(HealthCheckTypes.FhirServiceCanRead, false, logger)
        {
            EnsureArg.IsNotNull(fhirApiDataClient, nameof(fhirApiDataClient));

            _fhirApiDataClient = fhirApiDataClient;
            _searchOptions = new BaseSearchOptions(SampleResourceType, null);
        }

        protected override async Task<HealthCheckResult> PerformHealthCheckImplAsync(CancellationToken cancellationToken)
        {
            var healthCheckResult = new HealthCheckResult(HealthCheckTypes.FhirServiceCanRead, false);

            try
            {
                // Ensure we can search from FHIR server.
                await _fhirApiDataClient.SearchAsync(_searchOptions, cancellationToken);
            }
            catch (Exception e)
            {
                healthCheckResult.Status = HealthCheckStatus.UNHEALTHY;
                healthCheckResult.ErrorMessage = "Read from FHIR server failed." + e.Message;
                return healthCheckResult;
            }

            healthCheckResult.Status = HealthCheckStatus.HEALTHY;
            return healthCheckResult;
        }
    }
}