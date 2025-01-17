﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Microsoft.Extensions.Logging;
using Microsoft.Health.AnalyticsConnector.Common;
using Microsoft.Health.AnalyticsConnector.Common.Authentication;
using Microsoft.Health.AnalyticsConnector.Common.Configurations;
using Microsoft.Health.AnalyticsConnector.Common.Logging;
using Microsoft.Health.AnalyticsConnector.DataClient.Exceptions;
using Microsoft.Health.AnalyticsConnector.DataClient.Extensions;
using Microsoft.Health.AnalyticsConnector.DataClient.Models;
using Polly.CircuitBreaker;

namespace Microsoft.Health.AnalyticsConnector.DataClient.Api
{
    public sealed class FhirApiDataClient : IApiDataClient
    {
        private readonly IApiDataSource _fhirApiDataSource;
        private readonly HttpClient _httpClient;
        private readonly IAccessTokenProvider _accessTokenProvider;
        private readonly IDiagnosticLogger _diagnosticLogger;
        private readonly ILogger<FhirApiDataClient> _logger;

        public FhirApiDataClient(
            IApiDataSource dataSource,
            HttpClient httpClient,
            ITokenCredentialProvider tokenCredentialProvider,
            IDiagnosticLogger diagnosticLogger,
            ILogger<FhirApiDataClient> logger)
        {
            EnsureArg.IsNotNull(dataSource, nameof(dataSource));
            EnsureArg.IsNotNullOrEmpty(dataSource.ServerUrl, nameof(dataSource.ServerUrl));
            EnsureArg.IsNotNull(httpClient, nameof(httpClient));
            EnsureArg.IsNotNull(tokenCredentialProvider, nameof(tokenCredentialProvider));
            EnsureArg.IsNotNull(diagnosticLogger, nameof(diagnosticLogger));
            EnsureArg.IsNotNull(logger, nameof(logger));

            _fhirApiDataSource = dataSource;
            _httpClient = httpClient;
            _accessTokenProvider = new AzureAccessTokenProvider(
                tokenCredentialProvider.GetCredential(TokenCredentialTypes.External),
                diagnosticLogger,
                new Logger<AzureAccessTokenProvider>(new LoggerFactory()));
            _diagnosticLogger = diagnosticLogger;
            _logger = logger;

            // Timeout will be handled by Polly policy.
            _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        }

        public async Task<string> SearchAsync(
            BaseApiOptions fhirApiOptions,
            CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException();
            }

            Uri searchUri;
            try
            {
                searchUri = CreateSearchUri(fhirApiOptions);
            }
            catch (Exception ex)
            {
                _diagnosticLogger.LogError(string.Format("Create search Uri failed, Reason: '{0}'", ex.Message));
                _logger.LogInformation(ex, "Create search Uri failed, Reason: '{reason}'", ex.Message);
                throw new ApiSearchException("Create search Uri failed", ex);
            }

            string accessToken = null;
            if (fhirApiOptions.IsAccessTokenRequired)
            {
                try
                {
                    if (_fhirApiDataSource.Authentication == AuthenticationType.ManagedIdentity)
                    {
                        // Currently we support accessing FHIR server endpoints with Managed Identity.
                        // Obtaining access token against a resource uri only works with Azure API for FHIR now.
                        // To do: add configuration for OSS FHIR server endpoints.

                        // The thread-safe AzureServiceTokenProvider class caches the token in memory and retrieves it from Azure AD just before expiration.
                        // https://docs.microsoft.com/en-us/dotnet/api/overview/azure/service-to-service-authentication#using-the-library
                        accessToken = await _accessTokenProvider.GetAccessTokenAsync(_fhirApiDataSource.ServerUrl, cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    _diagnosticLogger.LogError(string.Format("Get fhir server access token failed, Reason: '{0}'", ex.Message));
                    _logger.LogInformation(ex, "Get fhir server access token failed, Reason: '{reason}'", ex.Message);
                    throw new ApiSearchException("Get fhir server access token failed", ex);
                }
            }

            return await GetResponseFromHttpRequestAsync(searchUri, accessToken, cancellationToken);
        }

        public string Search(BaseApiOptions fhirApiOptions)
        {
            if (fhirApiOptions.IsAccessTokenRequired && _fhirApiDataSource.Authentication == AuthenticationType.ManagedIdentity)
            {
                _diagnosticLogger.LogError("Synchronous search doesn't support AccessToken, please use Asynchronous method SearchAsync() instead.");
                _logger.LogInformation("Synchronous search doesn't support AccessToken, please use Asynchronous method SearchAsync() instead.");
                throw new ApiSearchException(
                    "Synchronous search doesn't support AccessToken, please use Asynchronous method SearchAsync() instead.");
            }

            Uri searchUri;
            try
            {
                searchUri = CreateSearchUri(fhirApiOptions);
            }
            catch (Exception ex)
            {
                _diagnosticLogger.LogError($"Create search Uri failed, Reason: '{ex.Message}'");
                _logger.LogInformation(ex, "Create search Uri failed, Reason: '{reason}'", ex.Message);
                throw new ApiSearchException("Create search Uri failed", ex);
            }

            return GetResponseFromHttpRequest(searchUri);
        }

        private Uri CreateSearchUri(BaseApiOptions fhirApiOptions)
        {
            string serverUrl = _fhirApiDataSource.ServerUrl;

            var baseUri = new Uri(serverUrl);

            var uri = new Uri(baseUri, fhirApiOptions.RelativeUri());

            // the query parameters is null for metadata
            if (fhirApiOptions.QueryParameters == null)
            {
                return uri;
            }

            return uri.AddQueryString(fhirApiOptions.QueryParameters);
        }

        private async Task<string> GetResponseFromHttpRequestAsync(Uri uri, string accessToken = null, CancellationToken cancellationToken = default)
        {
            try
            {
                var searchRequest = new HttpRequestMessage(HttpMethod.Get, uri);
                if (accessToken != null)
                {
                    // Currently we support accessing FHIR server endpoints with Managed Identity.
                    // Obtaining access token against a resource uri only works with Azure API for FHIR now.
                    // To do: add configuration for OSS FHIR server endpoints.

                    // The thread-safe AzureServiceTokenProvider class caches the token in memory and retrieves it from Azure AD just before expiration.
                    // https://docs.microsoft.com/en-us/dotnet/api/overview/azure/service-to-service-authentication#using-the-library
                    searchRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                }

                HttpResponseMessage response = await _httpClient.SendAsync(searchRequest, cancellationToken);
                response.EnsureSuccessStatusCode();

                _logger.LogInformation("Successfully retrieved result for url: '{url}'.", uri);

                return await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (HttpRequestException hrEx)
            {
                switch (hrEx.StatusCode)
                {
                    case HttpStatusCode.Unauthorized:
                        _diagnosticLogger.LogError(string.Format("Failed to search from FHIR server: FHIR server {0} is unauthorized.", _fhirApiDataSource.ServerUrl));
                        _logger.LogInformation(hrEx, "Failed to search from FHIR server: FHIR server {0} is unauthorized.", _fhirApiDataSource.ServerUrl);
                        break;
                    case HttpStatusCode.NotFound:
                        _diagnosticLogger.LogError(string.Format("Failed to search from FHIR server: FHIR server {0} is not found.", _fhirApiDataSource.ServerUrl));
                        _logger.LogInformation(hrEx, "Failed to search from FHIR server: FHIR server {0} is not found.", _fhirApiDataSource.ServerUrl);
                        break;
                    default:
                        _diagnosticLogger.LogError(string.Format("Failed to search from FHIR server: Status code: {0}. Reason: {1}", hrEx.StatusCode, hrEx.Message));
                        _logger.LogInformation(hrEx, "Failed to search from FHIR server: Status code: {0}. Reason: {1}", hrEx.StatusCode, hrEx.Message);
                        break;
                }

                throw new ApiSearchException(
                    string.Format(Resource.FhirSearchFailed, uri, hrEx.Message),
                    hrEx);
            }
            catch (BrokenCircuitException bcEx)
            {
                _diagnosticLogger.LogError($"Failed to search from FHIR server. Reason: {bcEx.Message}");
                _logger.LogInformation(bcEx, "Broken circuit while searching from FHIR server. Reason: {0}", bcEx.Message);

                throw new ApiSearchException(
                    string.Format(Resource.FhirSearchFailed, uri, bcEx.Message),
                    bcEx);
            }
            catch (Exception ex)
            {
                _diagnosticLogger.LogError($"Unknown error while searching from FHIR server. Reason: {ex.Message}");
                _logger.LogError(ex, "Unhandled error while searching from FHIR server. Reason: {0}", ex.Message);
                throw;
            }
        }

        private string GetResponseFromHttpRequest(Uri uri)
        {
            try
            {
                var searchRequest = new HttpRequestMessage(HttpMethod.Get, uri);

                HttpResponseMessage response = _httpClient.Send(searchRequest);
                response.EnsureSuccessStatusCode();
                _logger.LogInformation("Successfully retrieved result for url: '{url}'.", uri);

                Stream stream = response.Content.ReadAsStream();
                stream.Seek(0, SeekOrigin.Begin);
                var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (HttpRequestException ex)
            {
                switch (ex.StatusCode)
                {
                    case HttpStatusCode.Unauthorized:
                        _diagnosticLogger.LogError(string.Format("Failed to search from FHIR server: FHIR server {0} is unauthorized.", _fhirApiDataSource.ServerUrl));
                        _logger.LogInformation(ex, "Failed to search from FHIR server: FHIR server {0} is unauthorized.", _fhirApiDataSource.ServerUrl);
                        break;
                    case HttpStatusCode.NotFound:
                        _diagnosticLogger.LogError(string.Format("Failed to search from FHIR server: FHIR server {0} is not found.", _fhirApiDataSource.ServerUrl));
                        _logger.LogInformation(ex, "Failed to search from FHIR server: FHIR server {0} is not found.", _fhirApiDataSource.ServerUrl);
                        break;
                    default:
                        _diagnosticLogger.LogError(string.Format("Failed to search from FHIR server: Status code: {0}. Reason: {1}", ex.StatusCode, ex.Message));
                        _logger.LogInformation(ex, "Failed to search from FHIR server: Status code: {0}. Reason: {1}", ex.StatusCode, ex.Message);
                        break;
                }

                throw new ApiSearchException(
                    string.Format(Resource.FhirSearchFailed, uri, ex.Message),
                    ex);
            }
            catch (Exception ex)
            {
                _diagnosticLogger.LogError($"Unknown error while searching from FHIR server. Reason: {ex.Message}");
                _logger.LogError(ex, "Unhandled error while searching from FHIR server. Reason: {0}", ex.Message);
                throw;
            }
        }
    }
}
