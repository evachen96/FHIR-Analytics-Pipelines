﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Health.AnalyticsConnector.Common.Configurations.Arrow;

namespace Microsoft.Health.AnalyticsConnector.Common.Configurations.ConfigurationResolvers
{
    public abstract class BaseConfigurationResolver
    {
        protected static void BaseResolve(
            IServiceCollection services,
            IConfiguration configuration)
        {
            services.Configure<JobConfiguration>(options =>
                configuration.GetSection(ConfigurationConstants.JobConfigurationKey).Bind(options));
            services.Configure<FilterConfiguration>(options =>
                configuration.GetSection(ConfigurationConstants.FilterConfigurationKey).Bind(options));
            services.Configure<FilterLocation>(options =>
                configuration.GetSection(ConfigurationConstants.FilterConfigurationKey).Bind(options));
            services.Configure<DataLakeStoreConfiguration>(options =>
                configuration.GetSection(ConfigurationConstants.DataLakeStoreConfigurationKey).Bind(options));
            services.Configure<ArrowConfiguration>(options =>
                configuration.GetSection(ConfigurationConstants.ArrowConfigurationKey).Bind(options));
            services.Configure<SchemaConfiguration>(options =>
                configuration.GetSection(ConfigurationConstants.SchemaConfigurationKey).Bind(options));
            services.Configure<HealthCheckConfiguration>(options =>
                configuration.GetSection(ConfigurationConstants.HealthCheckConfigurationKey).Bind(options));
            services.Configure<StorageConfiguration>(options =>
                configuration.GetSection(ConfigurationConstants.StorageConfigurationKey).Bind(options));
        }
    }
}
