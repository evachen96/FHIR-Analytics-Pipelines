﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using Microsoft.Health.Fhir.Synapse.DataClient.Api;

namespace Microsoft.Health.Fhir.Synapse.DataClient.Models.SearchOption
{
    public class ResourceIdSearchOptions : BaseSearchOptions
    {
        public ResourceIdSearchOptions(
            string resourceType,
            string resourceId,
            List<KeyValuePair<string, string>> queryParameters)
            : base(resourceType, queryParameters)
        {
            ResourceId = resourceId;

            QueryParameters ??= new List<KeyValuePair<string, string>>();

            QueryParameters.Add(new KeyValuePair<string, string>(FhirApiConstants.IdKey, ResourceId));
        }

        public string ResourceId { get; set; }
    }
}
