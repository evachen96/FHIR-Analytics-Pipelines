﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Health.AnalyticsConnector.Common.Logging;
using Microsoft.Health.AnalyticsConnector.Core.Jobs;
using Microsoft.Health.AnalyticsConnector.HealthCheck.Checkers;
using Microsoft.Health.AnalyticsConnector.HealthCheck.Models;
using NSubstitute;
using Xunit;

namespace Microsoft.Health.AnalyticsConnector.HealthCheck.UnitTests.Checkers
{
    public class SchedulerServiceHealthCheckerTests
    {
        private static IDiagnosticLogger _diagnosticLogger = new DiagnosticLogger();

        [Fact]
        public void GivenNullInputParameters_WhenInitialize_ExceptionShouldBeThrown()
        {
            Assert.Throws<ArgumentNullException>(
                () => new SchedulerServiceHealthChecker(null, _diagnosticLogger, new NullLogger<SchedulerServiceHealthChecker>()));
        }

        [Fact]
        public async Task When_SchedulerService_IsActive_HealthCheck_Succeeds()
        {
            var schedulerService = Substitute.For<ISchedulerService>();
            schedulerService.LastHeartbeat.Returns(DateTimeOffset.UtcNow);
            var schedulerServiceHealthChecker = new SchedulerServiceHealthChecker(
                schedulerService,
                _diagnosticLogger,
                new NullLogger<SchedulerServiceHealthChecker>());

            HealthCheckResult result = await schedulerServiceHealthChecker.PerformHealthCheckAsync();
            Assert.Equal(HealthCheckStatus.HEALTHY, result.Status);
            Assert.True(result.IsCritical);
        }

        [Fact]
        public async Task When_SchedulerService_IsInactive_HealthCheck_Fails()
        {
            var schedulerService = Substitute.For<ISchedulerService>();
            schedulerService.LastHeartbeat.Returns(DateTimeOffset.UtcNow.AddMinutes(-5));
            var schedulerServiceHealthChecker = new SchedulerServiceHealthChecker(
                schedulerService,
                _diagnosticLogger,
                new NullLogger<SchedulerServiceHealthChecker>());

            HealthCheckResult result = await schedulerServiceHealthChecker.PerformHealthCheckAsync();
            Assert.Equal(HealthCheckStatus.UNHEALTHY, result.Status);
            Assert.True(result.IsCritical);
        }
    }
}
