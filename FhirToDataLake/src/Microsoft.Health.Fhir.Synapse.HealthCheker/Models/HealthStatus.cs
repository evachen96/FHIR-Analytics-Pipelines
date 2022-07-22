﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;

namespace Microsoft.Health.Fhir.Synapse.HealthCheker.Models
{
    public class HealthStatus
    {
        public DateTime StartTime { get; set; } = DateTime.UtcNow;

        public DateTime EndTime { get; set; } = DateTime.UtcNow;

        public IList<HealthCheckResult> HealthCheckResults { get; set; }
    }
}