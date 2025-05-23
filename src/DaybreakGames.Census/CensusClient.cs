﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Net.Http;
using DaybreakGames.Census.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DaybreakGames.Census.Operators;
using DaybreakGames.Census.JsonConverters;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DaybreakGames.Census
{
    public class CensusClient : ICensusClient
    {
        private readonly IOptions<CensusOptions> _options;
        private readonly ILogger<CensusClient> _logger;
        private readonly HttpClient _client;
        private readonly JsonSerializerOptions _serializerOptions;

        public CensusClient(IOptions<CensusOptions> options, ILogger<CensusClient> logger)
        {
            _options = options;
            _logger = logger;

            _client = new HttpClient();

            if (options.Value.UserAgent != null) {
                _client.DefaultRequestHeaders.UserAgent.TryParseAdd(options.Value.UserAgent);
            }

            _serializerOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = new UnderscorePropertyJsonNamingPolicy(),
                NumberHandling = JsonNumberHandling.AllowReadingFromString,
                Converters =
                {
                    new BooleanJsonConverter(),
                    new DateTimeJsonConverter()
                }
            };
        }

        public CensusQuery CreateQuery(string serviceName)
        {
            return new CensusQuery(this, serviceName);
        }

        public Task<IEnumerable<T>> ExecuteQueryList<T>(CensusQuery query)
        {
            return ExecuteQuery<IEnumerable<T>>(query);
        }

        public async Task<T> ExecuteQuery<T>(CensusQuery query)
        {
            var requestUri = CreateRequestUri(query);
            _logger.LogTrace(85400, $"Getting Census request for: \"{ requestUri}\"");

            try
            {
                HttpResponseMessage result;

                try
                {
                    result = await _client.GetAsync(requestUri);
                }
                catch (HttpRequestException ex)
                {
                    var exMessage = ex.InnerException?.Message ?? ex.Message;
                    var errorMessage = $"Census query failed for query: {requestUri}: {exMessage}";

                    throw new CensusConnectionException(errorMessage);
                }

                _logger.LogTrace(85401, $"Census Request completed with status code: {result.StatusCode}");

                if (!result.IsSuccessStatusCode)
                {
                    throw new CensusConnectionException($"Census returned status code {result.StatusCode}");
                }

                JsonElement jResult;

                try
                {
                    var serializedString = await result.Content.ReadAsStringAsync();
                    jResult = JsonSerializer.Deserialize<JsonElement>(serializedString);
                }
                catch (JsonException ex)
                {
                    throw new CensusException("Failed to read JSON. Endpoint may be in maintence mode.", ex);
                }

                var error = jResult.TryGetString("error");
                var errorCode = jResult.TryGetString("errorCode");

                if (error != null)
                {
                    if (error == "service_unavailable")
                    {
                        throw new CensusServiceUnavailableException();
                    }
                    else
                    {
                        throw new CensusServerException(error);
                    }
                }
                else if (errorCode != null)
                {
                    var errorMessage = jResult.TryGetString("errorMessage");

                    throw new CensusServerException($"{errorCode}: {errorMessage}");
                }

                var jBody = jResult.GetProperty($"{query.ServiceName}_list");
                return Convert<T>(jBody);
            }
            catch(Exception ex)
            {
                HandleCensusExceptions(ex, requestUri);
                throw ex;
            }
        }

        public async Task<IEnumerable<T>> ExecuteQueryBatch<T>(CensusQuery query)
        {
            var count = 0;
            List<JsonElement> batchResult = new List<JsonElement>();

            if (query.Limit == null)
            {
                query.SetLimit(Constants.DefaultBatchLimit);
            }

            if (query.Start == null)
            {
                query.SetStart(count);
            }

            var result = await ExecuteQueryList<JsonElement>(query);

            if (result.Count() < Constants.DefaultBatchLimit)
            {
                return result.Select(r => Convert<T>(r));
            }

            do
            {
                batchResult.AddRange(result);

                if (result.Count() < Constants.DefaultBatchLimit)
                {
                    return batchResult.Select(r => Convert<T>(r));
                }

                count += result.Count();
                query.SetStart(count);

                result = await ExecuteQuery<IEnumerable<JsonElement>>(query);
            } while (result.Any());

            return batchResult.Select(r => Convert<T>(r));
        }

        public Uri CreateRequestUri(CensusQuery query)
        {
            var endpoint = _options.Value.CensusApiEndpoint;
            var sId = query.ServiceId ?? _options.Value.CensusServiceId;
            var ns = query.ServiceNamespace ?? _options.Value.CensusServiceNamespace;

            var encArgs = query.ToString();
            return new Uri($"http{(_options.Value.UseHttps ? "s" : "")}://{endpoint}/s:{sId}/get/{ns}/{encArgs}");
        }

        private void HandleCensusExceptions(Exception ex, Uri query)
        {
            if (!_options.Value.LogCensusErrors)
            {
                return;
            }

            switch(ex)
            {
                case CensusServiceUnavailableException cex:
                    _logger.LogError(84531, cex, "Census service unavailable during query: {0}", query);
                    break;
                case CensusServerException cex:
                    _logger.LogError(84532, cex, "Census server failed for query: {0}", query);
                    break;
                case CensusConnectionException cex:
                    _logger.LogError(84533, cex, "Census connection failed for query: {0}", query);
                    break;
                default:
                    _logger.LogError(84530, ex, "Unknown exception throw when processing census query: {0}", query);
                    break;
            }
        }

        private T Convert<T>(JsonElement content)
        {
            return content.Deserialize<T>(_serializerOptions);
        }

        public void Dispose()
        {
            _client.Dispose();
        }
    }
}
