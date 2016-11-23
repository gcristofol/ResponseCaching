﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Headers;
using Microsoft.AspNetCore.ResponseCaching.Internal;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Microsoft.AspNetCore.ResponseCaching
{
    public class ResponseCachingMiddleware
    {
        private static readonly TimeSpan DefaultExpirationTimeSpan = TimeSpan.FromSeconds(10);

        private readonly RequestDelegate _next;
        private readonly ResponseCachingOptions _options;
        private readonly ILogger _logger;
        private readonly IResponseCachingPolicyProvider _policyProvider;
        private readonly IResponseCache _cache;
        private readonly IResponseCachingKeyProvider _keyProvider;
        private readonly Func<object, Task> _onStartingCallback;

        public ResponseCachingMiddleware(
            RequestDelegate next,
            IOptions<ResponseCachingOptions> options,
            ILoggerFactory loggerFactory,
            IResponseCachingPolicyProvider policyProvider,
            IResponseCache cache,
            IResponseCachingKeyProvider keyProvider)
        {
            if (next == null)
            {
                throw new ArgumentNullException(nameof(next));
            }
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }
            if (loggerFactory == null)
            {
                throw new ArgumentNullException(nameof(loggerFactory));
            }
            if (policyProvider == null)
            {
                throw new ArgumentNullException(nameof(policyProvider));
            }
            if (cache == null)
            {
                throw new ArgumentNullException(nameof(cache));
            }
            if (keyProvider == null)
            {
                throw new ArgumentNullException(nameof(keyProvider));
            }

            _next = next;
            _options = options.Value;
            _logger = loggerFactory.CreateLogger<ResponseCachingMiddleware>();
            _policyProvider = policyProvider;
            _cache = cache;
            _keyProvider = keyProvider;
            _onStartingCallback = state => OnResponseStartingAsync((ResponseCachingContext)state);
        }

        public async Task Invoke(HttpContext httpContext)
        {
            var context = new ResponseCachingContext(httpContext, _logger);

            // Should we attempt any caching logic?
            if (_policyProvider.IsRequestCacheable(context))
            {
                // Can this request be served from cache?
                if (await TryServeFromCacheAsync(context))
                {
                    return;
                }

                // Hook up to listen to the response stream
                ShimResponseStream(context);

                try
                {
                    // Subscribe to OnStarting event
                    httpContext.Response.OnStarting(_onStartingCallback, context);

                    await _next(httpContext);

                    // If there was no response body, check the response headers now. We can cache things like redirects.
                    await OnResponseStartingAsync(context);

                    // Finalize the cache entry
                    await FinalizeCacheBodyAsync(context);
                }
                finally
                {
                    UnshimResponseStream(context);
                }
            }
            else
            {
                await _next(httpContext);
            }
        }

        internal async Task<bool> TryServeCachedResponseAsync(ResponseCachingContext context, IResponseCacheEntry cacheEntry)
        {
            var cachedResponse = cacheEntry as CachedResponse;
            if (cachedResponse == null)
            {
                return false;
            }

            context.CachedResponse = cachedResponse;
            context.CachedResponseHeaders = cachedResponse.Headers;
            context.ResponseTime = _options.SystemClock.UtcNow;
            var cachedEntryAge = context.ResponseTime.Value - context.CachedResponse.Created;
            context.CachedEntryAge = cachedEntryAge > TimeSpan.Zero ? cachedEntryAge : TimeSpan.Zero;

            if (_policyProvider.IsCachedEntryFresh(context))
            {
                // Check conditional request rules
                if (ContentIsNotModified(context))
                {
                    _logger.LogNotModifiedServed();
                    context.HttpContext.Response.StatusCode = StatusCodes.Status304NotModified;
                }
                else
                {
                    var response = context.HttpContext.Response;
                    // Copy the cached status code and response headers
                    response.StatusCode = context.CachedResponse.StatusCode;
                    foreach (var header in context.CachedResponse.Headers)
                    {
                        response.Headers.Add(header);
                    }

                    response.Headers[HeaderNames.Age] = context.CachedEntryAge.Value.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture);

                    // Copy the cached response body
                    var body = context.CachedResponse.Body;
                    if (body.Length > 0)
                    {
                        // Add a content-length if required
                        if (!response.ContentLength.HasValue && StringValues.IsNullOrEmpty(response.Headers[HeaderNames.TransferEncoding]))
                        {
                            response.ContentLength = body.Length;
                        }

                        try
                        {
                            await body.CopyToAsync(response.Body, StreamUtilities.BodySegmentSize, context.HttpContext.RequestAborted);
                        }
                        catch (OperationCanceledException)
                        {
                            context.HttpContext.Abort();
                        }
                    }
                    _logger.LogCachedResponseServed();
                }
                return true;
            }

            return false;
        }

        internal async Task<bool> TryServeFromCacheAsync(ResponseCachingContext context)
        {
            context.BaseKey = _keyProvider.CreateBaseKey(context);
            var cacheEntry = await _cache.GetAsync(context.BaseKey);

            var cachedVaryByRules = cacheEntry as CachedVaryByRules;
            if (cachedVaryByRules != null)
            {
                // Request contains vary rules, recompute key(s) and try again
                context.CachedVaryByRules = cachedVaryByRules;

                foreach (var varyKey in _keyProvider.CreateLookupVaryByKeys(context))
                {
                    if (await TryServeCachedResponseAsync(context, await _cache.GetAsync(varyKey)))
                    {
                        return true;
                    }
                }
            }
            else
            {
                if (await TryServeCachedResponseAsync(context, cacheEntry))
                {
                    return true;
                }
            }

            if (HeaderUtilities.Contains(context.HttpContext.Request.Headers[HeaderNames.CacheControl], CacheControlValues.OnlyIfCachedString))
            {
                _logger.LogGatewayTimeoutServed();
                context.HttpContext.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
                return true;
            }

            _logger.LogNoResponseServed();
            return false;
        }

        internal async Task FinalizeCacheHeadersAsync(ResponseCachingContext context)
        {
            if (_policyProvider.IsResponseCacheable(context))
            {
                context.ShouldCacheResponse = true;

                // Create the cache entry now
                var response = context.HttpContext.Response;
                var varyHeaders = new StringValues(response.Headers.GetCommaSeparatedValues(HeaderNames.Vary));
                var varyQueryKeys = new StringValues(context.HttpContext.Features.Get<IResponseCachingFeature>()?.VaryByQueryKeys);
                context.CachedResponseValidFor = context.ResponseSharedMaxAge ??
                    context.ResponseMaxAge ??
                    (context.ResponseExpires - context.ResponseTime.Value) ??
                    DefaultExpirationTimeSpan;

                // Check if any vary rules exist
                if (!StringValues.IsNullOrEmpty(varyHeaders) || !StringValues.IsNullOrEmpty(varyQueryKeys))
                {
                    // Normalize order and casing of vary by rules
                    var normalizedVaryHeaders = GetOrderCasingNormalizedStringValues(varyHeaders);
                    var normalizedVaryQueryKeys = GetOrderCasingNormalizedStringValues(varyQueryKeys);

                    // Update vary rules if they are different
                    if (context.CachedVaryByRules == null ||
                        !StringValues.Equals(context.CachedVaryByRules.QueryKeys, normalizedVaryQueryKeys) ||
                        !StringValues.Equals(context.CachedVaryByRules.Headers, normalizedVaryHeaders))
                    {
                        context.CachedVaryByRules = new CachedVaryByRules
                        {
                            VaryByKeyPrefix = FastGuid.NewGuid().IdString,
                            Headers = normalizedVaryHeaders,
                            QueryKeys = normalizedVaryQueryKeys
                        };
                    }

                    // Always overwrite the CachedVaryByRules to update the expiry information
                    _logger.LogVaryByRulesUpdated(normalizedVaryHeaders, normalizedVaryQueryKeys);
                    await _cache.SetAsync(context.BaseKey, context.CachedVaryByRules, context.CachedResponseValidFor);

                    context.StorageVaryKey = _keyProvider.CreateStorageVaryByKey(context);
                }

                // Ensure date header is set
                if (!context.ResponseDate.HasValue)
                {
                    context.ResponseDate = context.ResponseTime.Value;
                    // Setting the date on the raw response headers.
                    context.HttpContext.Response.Headers[HeaderNames.Date] = HeaderUtilities.FormatDate(context.ResponseDate.Value);
                }

                // Store the response on the state
                context.CachedResponse = new CachedResponse
                {
                    Created = context.ResponseDate.Value,
                    StatusCode = context.HttpContext.Response.StatusCode
                };

                foreach (var header in context.HttpContext.Response.Headers)
                {
                    if (!string.Equals(header.Key, HeaderNames.Age, StringComparison.OrdinalIgnoreCase))
                    {
                        context.CachedResponse.Headers.Add(header);
                    }
                }
            }
            else
            {
                context.ResponseCachingStream.DisableBuffering();
            }
        }

        internal async Task FinalizeCacheBodyAsync(ResponseCachingContext context)
        {
            var contentLength = context.HttpContext.Response.ContentLength;
            if (context.ShouldCacheResponse && context.ResponseCachingStream.BufferingEnabled)
            {
                var bufferStream = context.ResponseCachingStream.GetBufferStream();
                if (!contentLength.HasValue || contentLength == bufferStream.Length)
                {
                    context.CachedResponse.Body = bufferStream;
                    _logger.LogResponseCached();
                    await _cache.SetAsync(context.StorageVaryKey ?? context.BaseKey, context.CachedResponse, context.CachedResponseValidFor);
                }
                else
                {
                    _logger.LogResponseContentLengthMismatchNotCached();
                }
            }
            else
            {
                _logger.LogResponseNotCached();
            }
        }

        internal Task OnResponseStartingAsync(ResponseCachingContext context)
        {
            if (!context.ResponseStarted)
            {
                context.ResponseStarted = true;
                context.ResponseTime = _options.SystemClock.UtcNow;

                return FinalizeCacheHeadersAsync(context);
            }
            else
            {
                return TaskCache.CompletedTask;
            }
        }

        internal void ShimResponseStream(ResponseCachingContext context)
        {
            // Shim response stream
            context.OriginalResponseStream = context.HttpContext.Response.Body;
            context.ResponseCachingStream = new ResponseCachingStream(context.OriginalResponseStream, _options.MaximumBodySize, StreamUtilities.BodySegmentSize);
            context.HttpContext.Response.Body = context.ResponseCachingStream;

            // Shim IHttpSendFileFeature
            context.OriginalSendFileFeature = context.HttpContext.Features.Get<IHttpSendFileFeature>();
            if (context.OriginalSendFileFeature != null)
            {
                context.HttpContext.Features.Set<IHttpSendFileFeature>(new SendFileFeatureWrapper(context.OriginalSendFileFeature, context.ResponseCachingStream));
            }

            // Add IResponseCachingFeature
            if (context.HttpContext.Features.Get<IResponseCachingFeature>() != null)
            {
                throw new InvalidOperationException($"Another instance of {nameof(ResponseCachingFeature)} already exists. Only one instance of {nameof(ResponseCachingMiddleware)} can be configured for an application.");
            }
            context.HttpContext.Features.Set<IResponseCachingFeature>(new ResponseCachingFeature());
        }

        internal static void UnshimResponseStream(ResponseCachingContext context)
        {
            // Unshim response stream
            context.HttpContext.Response.Body = context.OriginalResponseStream;

            // Unshim IHttpSendFileFeature
            context.HttpContext.Features.Set(context.OriginalSendFileFeature);

            // Remove IResponseCachingFeature
            context.HttpContext.Features.Set<IResponseCachingFeature>(null);
        }

        internal static bool ContentIsNotModified(ResponseCachingContext context)
        {
            var cachedResponseHeaders = context.CachedResponseHeaders;
            var ifNoneMatchHeader = context.HttpContext.Request.Headers[HeaderNames.IfNoneMatch];

            if (!StringValues.IsNullOrEmpty(ifNoneMatchHeader))
            {
                if (ifNoneMatchHeader.Count == 1 && ifNoneMatchHeader[0].Equals(EntityTagHeaderValue.Any.Tag))
                {
                    context.Logger.LogNotModifiedIfNoneMatchStar();
                    return true;
                }

                EntityTagHeaderValue eTag;
                if (!StringValues.IsNullOrEmpty(cachedResponseHeaders[HeaderNames.ETag])
                    && EntityTagHeaderValue.TryParse(cachedResponseHeaders[HeaderNames.ETag], out eTag))
                {
                    foreach (var tag in ifNoneMatchHeader)
                    {
                        EntityTagHeaderValue requestETag;
                        if (EntityTagHeaderValue.TryParse(tag, out requestETag) &&
                            eTag.Compare(requestETag, useStrongComparison: false))
                        {
                            context.Logger.LogNotModifiedIfNoneMatchMatched(requestETag);
                            return true;
                        }
                    }
                }
            }
            else
            {
                var ifUnmodifiedSince = context.HttpContext.Request.Headers[HeaderNames.IfUnmodifiedSince];
                if (!StringValues.IsNullOrEmpty(ifUnmodifiedSince))
                {
                    DateTimeOffset modified;
                    if (!HeaderUtilities.TryParseDate(cachedResponseHeaders[HeaderNames.LastModified], out modified) &&
                        !HeaderUtilities.TryParseDate(cachedResponseHeaders[HeaderNames.Date], out modified))
                    {
                        return false;
                    }

                    DateTimeOffset unmodifiedSince;
                    if (HeaderUtilities.TryParseDate(ifUnmodifiedSince, out unmodifiedSince) &&
                        modified <= unmodifiedSince)
                    {
                        context.Logger.LogNotModifiedIfUnmodifiedSinceSatisfied(modified, unmodifiedSince);
                        return true;
                    }
                }
            }

            return false;
        }

        // Normalize order and casing
        internal static StringValues GetOrderCasingNormalizedStringValues(StringValues stringValues)
        {
            if (stringValues.Count == 1)
            {
                return new StringValues(stringValues.ToString().ToUpperInvariant());
            }
            else
            {
                var originalArray = stringValues.ToArray();
                var newArray = new string[originalArray.Length];

                for (var i = 0; i < originalArray.Length; i++)
                {
                    newArray[i] = originalArray[i].ToUpperInvariant();
                }

                // Since the casing has already been normalized, use Ordinal comparison
                Array.Sort(newArray, StringComparer.Ordinal);

                return new StringValues(newArray);
            }
        }
    }
}
