﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

namespace Microsoft.Health.Fhir.Synapse.JobManagement
{
    public static class AzureStorageErrorCode
    {
        public const string GetEntityNotFoundErrorCode = "ResourceNotFound";
        public const string UpdateEntityPreconditionFailedErrorCode = "UpdateConditionNotSatisfied";
        public const string AddEntityAlreadyExistsErrorCode = "EntityAlreadyExists";
    }
}