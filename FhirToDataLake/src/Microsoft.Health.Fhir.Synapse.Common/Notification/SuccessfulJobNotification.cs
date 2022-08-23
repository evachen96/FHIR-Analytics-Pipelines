﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using MediatR;
using Microsoft.Health.Fhir.Synapse.Common.Models.Jobs;
using Newtonsoft.Json;

namespace Microsoft.Health.Fhir.Synapse.Common.Notification
{
    public class SuccessfulJobNotification : INotification
    {
        public SuccessfulJobNotification(Job job)
        {
            Id = job.Id;
            CreatedTime = job.CreatedTime;
            StartedTime = (DateTimeOffset)job.StartedTime;
            CompletedTime = (DateTimeOffset)job.CompletedTime;
            ContainerName = job.ContainerName;
            DataPeriod = job.DataPeriod;
            TotalResourceCounts = job.TotalResourceCounts;
            OutputResourceCounts = job.OutputResourceCounts;
            OutputResourceDataSize = job.OutputResourceDataSize;
            ProcessedResourceCounts = job.ProcessedResourceCounts;
            SkippedResourceCounts = job.SkippedResourceCounts;
            DataLatency = CompletedTime - DataPeriod.End;
        }

        /// <summary>
        /// Job id.
        /// </summary>
        public string Id { get; }

        /// <summary>
        /// Created timestamp of job.
        /// </summary>
        public DateTimeOffset CreatedTime { get; }

        /// <summary>
        /// Started timestamp of job.
        /// </summary>
        public DateTimeOffset StartedTime { get; }

        /// <summary>
        /// Completed timestamp of job.
        /// </summary>
        public DateTimeOffset CompletedTime { get; set; }

        /// <summary>
        /// Container name.
        /// </summary>
        public string ContainerName { get; }

        /// <summary>
        /// Will process all data with timestamp greater than or equal to DataPeriod.Start and less than DataPeriod.End.
        /// For FHIR data, we use the lastUpdated field as the record timestamp.
        /// </summary>
        public DataPeriod DataPeriod { get; }

        /// <summary>
        /// Latency for data output pipepline.
        /// </summary>
        public TimeSpan DataLatency { get; }

        /// <summary>
        /// Total resource count (from data source) for each resource types.
        /// </summary>
        public Dictionary<string, int> TotalResourceCounts { get; set; }

        /// <summary>
        /// Output resource count (from data source) for each resource types.
        /// </summary>
        public Dictionary<string, int> OutputResourceCounts { get; set; }

        /// <summary>
        /// Output resource data size for each resource types.
        /// </summary>
        public Dictionary<string, long> OutputResourceDataSize { get; set; }

        /// <summary>
        /// Processed resource count for each schema type.
        /// </summary>
        public Dictionary<string, int> ProcessedResourceCounts { get; set; }

        /// <summary>
        /// Skipped resource count for each schema type.
        /// </summary>
        public Dictionary<string, int> SkippedResourceCounts { get; set; }
    }
}
