﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using EnsureThat;

namespace Microsoft.Health.Fhir.Synapse.Common.Models.FhirSearch
{
    public class TypeFilter
    {
        public TypeFilter(string resourceType, IList<Tuple<string, string>> parameters)
        {
            EnsureArg.IsNotNullOrWhiteSpace(resourceType, nameof(resourceType));
            EnsureArg.IsNotNull(parameters, nameof(parameters));

            ResourceType = resourceType;
            Parameters = parameters;
        }

        public string ResourceType { get; set; }

        // TODO: Noted by yanhon: we should use List here, the parameter keys may be the same, such as lastUpdated=gt1900-01-01&lastUpdated=lt2000-01-01
        public IList<Tuple<string, string>> Parameters { get; set; }
    }
}
