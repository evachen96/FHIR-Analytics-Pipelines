﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;

namespace Microsoft.Health.Fhir.Synapse.Common.Models.Jobs
{
    public class TypeFilter
    {
        public string ResourceType { get; set; }

        public Dictionary<string, string> Parameters { get; set; }
    }
}
