﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Microsoft.Extensions.Logging;
using Microsoft.Health.Fhir.Synapse.Common.Logging;
using Microsoft.Health.Fhir.Synapse.Core.Exceptions;
using Microsoft.Health.Fhir.Synapse.Core.Extensions;
using Microsoft.Health.Fhir.Synapse.Core.Fhir;
using Microsoft.Health.Fhir.Synapse.Core.Jobs.Models;
using Microsoft.Health.Fhir.Synapse.DataClient;
using Microsoft.Health.Fhir.Synapse.DataClient.Api.Fhir;
using Microsoft.Health.Fhir.Synapse.DataClient.Extensions;
using Microsoft.Health.Fhir.Synapse.DataClient.Models.FhirApiOption;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Microsoft.Health.Fhir.Synapse.Core.Jobs
{
    public class FhirToDataLakeProcessingJobSpliter
    {
        private readonly IApiDataClient _dataClient;
        private readonly IDiagnosticLogger _diagnosticLogger;
        private readonly ILogger<FhirToDataLakeProcessingJobSpliter> _logger;

        public FhirToDataLakeProcessingJobSpliter(
            IApiDataClient dataClient,
            IDiagnosticLogger diagnosticLogger,
            ILogger<FhirToDataLakeProcessingJobSpliter> logger)
        {
            _dataClient = EnsureArg.IsNotNull(dataClient, nameof(dataClient));
            _diagnosticLogger = EnsureArg.IsNotNull(diagnosticLogger, nameof(diagnosticLogger));
            _logger = EnsureArg.IsNotNull(logger, nameof(logger));
        }

        public int LowBoundOfProcessingJobResourceCount { get; set; } = JobConfigurationConstants.LowBoundOfProcessingJobResourceCount;

        public int HighBoundOfProcessingJobResourceCount { get; set; } = JobConfigurationConstants.HighBoundOfProcessingJobResourceCount;

        public async IAsyncEnumerable<SubJobInfo> SplitJobAsync(string resourceType, DateTimeOffset? startTime, DateTimeOffset endTime, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var totalCount = await GetResourceCountAsync(resourceType, startTime, endTime, cancellationToken);

            if (totalCount < HighBoundOfProcessingJobResourceCount)
            {
                // Return job with total count lower than high bound.
                _logger.LogInformation($"Generate one {resourceType} job with {totalCount} count.");

                yield return new SubJobInfo
                {
                    ResourceType = resourceType,
                    TimeRange = new TimeRange() { DataStartTime = startTime, DataEndTime = endTime },
                    JobSize = totalCount,
                };
            }
            else
            {
                // Split large size job using binary search.
                var splitingStartTime = DateTimeOffset.Now;
                _logger.LogInformation($"Start spliting {resourceType} job, total {totalCount} resources.");

                var anchorList = await InitializeAnchorListAsync(resourceType, startTime, endTime, totalCount, cancellationToken);

                _logger.LogInformation($"Spliting {resourceType} job. Use {(DateTimeOffset.Now - splitingStartTime).TotalMilliseconds} milliseconds to initilize anchor list.");
                var lastSplitTimestamp = DateTimeOffset.Now;
                DateTimeOffset? nextJobEnd = null;
                var jobCount = 0;

                while (nextJobEnd == null || nextJobEnd < endTime)
                {
                    anchorList = anchorList.OrderBy(p => p.Key).ToDictionary(p => p.Key, o => o.Value);
                    DateTimeOffset? lastEndTime = nextJobEnd ?? startTime;

                    nextJobEnd = await GetNextSplitTimestamp(resourceType, lastEndTime, anchorList, cancellationToken);

                    var jobSize = lastEndTime == null ? anchorList[(DateTimeOffset)nextJobEnd] : anchorList[(DateTimeOffset)nextJobEnd] - anchorList[(DateTimeOffset)lastEndTime];
                    _logger.LogInformation($"Spliting {resourceType} job. Generated new sub job using {(DateTimeOffset.UtcNow - lastSplitTimestamp).TotalMilliseconds} milliseconds with {jobSize} resource counts. ");
                    lastSplitTimestamp = DateTimeOffset.Now;

                    yield return new SubJobInfo
                    {
                        ResourceType = resourceType,
                        TimeRange = new TimeRange() { DataStartTime = lastEndTime, DataEndTime = (DateTimeOffset)nextJobEnd },
                        JobSize = jobSize,
                    };

                    jobCount += 1;
                }

                _logger.LogInformation($"Spliting {resourceType} jobs, finish split {jobCount} jobs. use : {(DateTimeOffset.Now - splitingStartTime).TotalMilliseconds} milliseconds.");
            }

        }

        private async Task<Dictionary<DateTimeOffset, int>> InitializeAnchorListAsync(string resourceType, DateTimeOffset? startTime, DateTimeOffset endTime, int totalCount, CancellationToken cancellationToken)
        {
            var anchorList = new Dictionary<DateTimeOffset, int>();
            if (startTime != null)
            {
                anchorList[(DateTimeOffset)startTime] = 0;
            }

            // Set isDescending parameter false to get first timestmp.
            var nextTimestamp = await GetNextTimestamp(resourceType, startTime, endTime, false, cancellationToken);

            // Set isDescending parameter as true to get last timestmp.
            var lastTimestamp = await GetNextTimestamp(resourceType, startTime, endTime, true, cancellationToken);

            if (nextTimestamp != null)
            {
                anchorList[(DateTimeOffset)nextTimestamp] = 0;
            }

            if (lastTimestamp != null)
            {
                anchorList[(DateTimeOffset)lastTimestamp] = totalCount;
            }

            anchorList[endTime] = totalCount;

            return anchorList;
        }

        // get the lastUpdated timestamp of next resource for next processing job
        private async Task<DateTimeOffset?> GetNextTimestamp(string resourceType, DateTimeOffset? start, DateTimeOffset end, bool isDescending, CancellationToken cancellationToken)
        {
            List<KeyValuePair<string, string>> parameters = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>(FhirApiConstants.LastUpdatedKey, $"lt{end.ToInstantString()}"),
                new KeyValuePair<string, string>(FhirApiConstants.PageCountKey, FhirApiPageCount.Single.ToString("d")),
                new KeyValuePair<string, string>(FhirApiConstants.TypeKey, resourceType),
            };

            if (isDescending)
            {
                parameters.Add(new KeyValuePair<string, string>(FhirApiConstants.SortKey, "-_lastUpdated"));
            }
            else
            {
                parameters.Add(new KeyValuePair<string, string>(FhirApiConstants.SortKey, "_lastUpdated"));
            }

            if (start != null)
            {
                parameters.Add(new KeyValuePair<string, string>(FhirApiConstants.LastUpdatedKey, $"ge{((DateTimeOffset)start).ToInstantString()}"));
            }

            var searchOptions = new BaseSearchOptions(null, parameters);

            string fhirBundleResult = await _dataClient.SearchAsync(searchOptions, cancellationToken);

            // Parse bundle result.
            JObject fhirBundleObject;
            try
            {
                fhirBundleObject = JObject.Parse(fhirBundleResult);
            }
            catch (JsonReaderException exception)
            {
                string reason = string.Format(
                        "Failed to parse fhir search result for '{0}' with search parameters '{1}'.",
                        searchOptions.ResourceType,
                        string.Join(", ", searchOptions.QueryParameters.Select(parameter => $"{parameter.Key}: {parameter.Value}")));

                _diagnosticLogger.LogError(reason);
                _logger.LogInformation(exception, reason);
                throw new FhirDataParseException(reason, exception);
            }

            List<JObject> fhirResources = FhirBundleParser.ExtractResourcesFromBundle(fhirBundleObject).ToList();

            if (fhirResources.Any() && fhirResources.First().GetLastUpdated() != null)
            {
                return fhirResources.First().GetLastUpdated();
            }

            return null;
        }

        private async Task<DateTimeOffset?> GetNextSplitTimestamp(string resourceType, DateTimeOffset? start, Dictionary<DateTimeOffset, int> anchorList, CancellationToken cancellationToken)
        {
            int baseSize = start == null ? 0 : anchorList[(DateTimeOffset)start];
            var last = start;
            foreach (var item in anchorList)
            {
                var value = item.Value;
                if (value == int.MaxValue)
                {
                    var resourceCount = await GetResourceCountAsync(resourceType, last, item.Key, cancellationToken);
                    var lastAnchorValue = last == null ? 0 : anchorList[(DateTimeOffset)last];
                    anchorList[item.Key] = resourceCount == int.MaxValue ? int.MaxValue : resourceCount + lastAnchorValue;
                    value = anchorList[item.Key];
                }

                if (value - baseSize < LowBoundOfProcessingJobResourceCount)
                {
                    last = item.Key;
                    continue;
                }

                if (value - baseSize <= HighBoundOfProcessingJobResourceCount && value - baseSize >= LowBoundOfProcessingJobResourceCount)
                {
                    return item.Key;
                }

                return await BisectAnchor(resourceType, last == null ? DateTimeOffset.MinValue : (DateTimeOffset)last, item.Key, anchorList, baseSize, cancellationToken);
            }

            return last;
        }

        private async Task<DateTimeOffset> BisectAnchor(string resourceType, DateTimeOffset start, DateTimeOffset end, Dictionary<DateTimeOffset, int> anchorList, int baseSize, CancellationToken cancellationToken)
        {
            while ((end - start).TotalMilliseconds > 1)
            {
                DateTimeOffset mid = start.Add((end - start) / 2);
                var resourceCount = await GetResourceCountAsync(resourceType, start, mid, cancellationToken);
                resourceCount = resourceCount == int.MaxValue ? int.MaxValue : resourceCount + anchorList[start];
                anchorList[mid] = resourceCount;
                if (resourceCount - baseSize > HighBoundOfProcessingJobResourceCount)
                {
                    end = mid;
                }
                else if (resourceCount - baseSize < LowBoundOfProcessingJobResourceCount)
                {
                    start = mid;
                }
                else
                {
                    return mid;
                }
            }

            return end;
        }

        private async Task<int> GetResourceCountAsync(string resourceType, DateTimeOffset? start, DateTimeOffset end, CancellationToken cancellationToken)
        {
            List<KeyValuePair<string, string>> parameters = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>(FhirApiConstants.LastUpdatedKey, $"lt{end.ToInstantString()}"),
                new KeyValuePair<string, string>(FhirApiConstants.SummaryKey, "count"),
                new KeyValuePair<string, string>(FhirApiConstants.TypeKey, resourceType),
            };

            if (start != null)
            {
                parameters.Add(new KeyValuePair<string, string>(FhirApiConstants.LastUpdatedKey, $"ge{((DateTimeOffset)start).ToInstantString()}"));
            }

            var searchOptions = new BaseSearchOptions(null, parameters);
            string fhirBundleResult;
            try
            {
                fhirBundleResult = await _dataClient.SearchAsync(searchOptions, cancellationToken);
            }
            catch
            {
                _logger.LogInformation("Get resource count error: too mush resoucres");
                return int.MaxValue;
            }

            // Parse bundle result.
            JObject fhirBundleObject;
            try
            {
                fhirBundleObject = JObject.Parse(fhirBundleResult);
                return (int)fhirBundleObject["total"];
            }
            catch (JsonReaderException exception)
            {
                string reason = string.Format(
                        "Failed to parse fhir search result for '{0}' with search parameters '{1}'.",
                        searchOptions.ResourceType,
                        string.Join(", ", searchOptions.QueryParameters.Select(parameter => $"{parameter.Key}: {parameter.Value}")));

                _diagnosticLogger.LogError(reason);
                _logger.LogInformation(exception, reason);
                throw new FhirDataParseException(reason, exception);
            }
        }
    }
}