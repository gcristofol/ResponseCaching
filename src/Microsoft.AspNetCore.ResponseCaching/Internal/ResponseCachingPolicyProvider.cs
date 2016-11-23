﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Microsoft.AspNetCore.ResponseCaching.Internal
{
    public class ResponseCachingPolicyProvider : IResponseCachingPolicyProvider
    {
        public virtual bool IsRequestCacheable(ResponseCachingContext context)
        {
            // Verify the method
            var request = context.HttpContext.Request;
            if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
            {
                context.Logger.LogRequestMethodNotCacheable(request.Method);
                return false;
            }

            // Verify existence of authorization headers
            if (!StringValues.IsNullOrEmpty(request.Headers[HeaderNames.Authorization]))
            {
                context.Logger.LogRequestWithAuthorizationNotCacheable();
                return false;
            }

            // Verify request cache-control parameters
            if (!StringValues.IsNullOrEmpty(request.Headers[HeaderNames.CacheControl]))
            {
                if (HeaderUtilities.Contains(request.Headers[HeaderNames.CacheControl], CacheControlValues.NoCacheString))
                {
                    context.Logger.LogRequestWithNoCacheNotCacheable();
                    return false;
                }
            }
            else
            {
                // Support for legacy HTTP 1.0 cache directive
                var pragmaHeaderValues = request.Headers[HeaderNames.Pragma];
                if (HeaderUtilities.Contains(request.Headers[HeaderNames.Pragma], CacheControlValues.NoCacheString))
                {
                    context.Logger.LogRequestWithPragmaNoCacheNotCacheable();
                    return false;
                }
            }

            return true;
        }

        public virtual bool IsResponseCacheable(ResponseCachingContext context)
        {
            var responseCacheControlHeader = context.HttpContext.Response.Headers[HeaderNames.CacheControl];

            // Only cache pages explicitly marked with public
            if (!HeaderUtilities.Contains(responseCacheControlHeader, CacheControlValues.PublicString))
            {
                context.Logger.LogResponseWithoutPublicNotCacheable();
                return false;
            }

            // Check no-store
            if (HeaderUtilities.Contains(context.HttpContext.Request.Headers[HeaderNames.CacheControl], CacheControlValues.NoStoreString)
                || HeaderUtilities.Contains(responseCacheControlHeader, CacheControlValues.NoStoreString))
            {
                context.Logger.LogResponseWithNoStoreNotCacheable();
                return false;
            }

            // Check no-cache
            if (HeaderUtilities.Contains(responseCacheControlHeader, CacheControlValues.NoCacheString))
            {
                context.Logger.LogResponseWithNoCacheNotCacheable();
                return false;
            }

            var response = context.HttpContext.Response;

            // Do not cache responses with Set-Cookie headers
            if (!StringValues.IsNullOrEmpty(response.Headers[HeaderNames.SetCookie]))
            {
                context.Logger.LogResponseWithSetCookieNotCacheable();
                return false;
            }

            // Do not cache responses varying by *
            var varyHeader = response.Headers[HeaderNames.Vary];
            if (varyHeader.Count == 1 && string.Equals(varyHeader, "*", StringComparison.OrdinalIgnoreCase))
            {
                context.Logger.LogResponseWithVaryStarNotCacheable();
                return false;
            }

            // Check private
            if (HeaderUtilities.Contains(responseCacheControlHeader, CacheControlValues.PrivateString))
            {
                context.Logger.LogResponseWithPrivateNotCacheable();
                return false;
            }

            // Check response code
            if (response.StatusCode != StatusCodes.Status200OK)
            {
                context.Logger.LogResponseWithUnsuccessfulStatusCodeNotCacheable(response.StatusCode);
                return false;
            }

            // Check response freshness
            if (!context.ResponseDate.HasValue)
            {
                if (!context.ResponseSharedMaxAge.HasValue &&
                    !context.ResponseMaxAge.HasValue &&
                    context.ResponseTime.Value >= context.ResponseExpires)
                {
                    context.Logger.LogExpirationExpiresExceeded(context.ResponseTime.Value, context.ResponseExpires.Value);
                    return false;
                }
            }
            else
            {
                var age = context.ResponseTime.Value - context.ResponseDate.Value;

                // Validate shared max age
                if (age >= context.ResponseSharedMaxAge)
                {
                    context.Logger.LogExpirationSharedMaxAgeExceeded(age, context.ResponseSharedMaxAge.Value);
                    return false;
                }
                else if (!context.ResponseSharedMaxAge.HasValue)
                {
                    // Validate max age
                    if (age >= context.ResponseMaxAge)
                    {
                        context.Logger.LogExpirationMaxAgeExceeded(age, context.ResponseMaxAge.Value);
                        return false;
                    }
                    else if (!context.ResponseMaxAge.HasValue)
                    {
                        // Validate expiration
                        if (context.ResponseTime.Value >= context.ResponseExpires)
                        {
                            context.Logger.LogExpirationExpiresExceeded(context.ResponseTime.Value, context.ResponseExpires.Value);
                            return false;
                        }
                    }
                }
            }

            return true;
        }

        public virtual bool IsCachedEntryFresh(ResponseCachingContext context)
        {
            var age = context.CachedEntryAge.Value;
            var cachedCacheControlHeaders = context.CachedResponseHeaders[HeaderNames.CacheControl];
            var requestCacheControlHeaders = context.HttpContext.Request.Headers[HeaderNames.CacheControl];

            // Add min-fresh requirements
            TimeSpan? minFresh;
            if (HeaderUtilities.TryParseTimeSpan(requestCacheControlHeaders, CacheControlValues.MinFreshString, out minFresh))
            {
                age += minFresh.Value;
                context.Logger.LogExpirationMinFreshAdded(minFresh.Value);
            }

            // Validate shared max age, this overrides any max age settings for shared caches
            TimeSpan? cachedSharedMaxAge;
            HeaderUtilities.TryParseTimeSpan(cachedCacheControlHeaders, CacheControlValues.SharedMaxAgeString, out cachedSharedMaxAge);

            if (age >= cachedSharedMaxAge)
            {
                // shared max age implies must revalidate
                context.Logger.LogExpirationSharedMaxAgeExceeded(age, cachedSharedMaxAge.Value);
                return false;
            }
            else if (!cachedSharedMaxAge.HasValue)
            {
                TimeSpan? requestMaxAge;
                HeaderUtilities.TryParseTimeSpan(requestCacheControlHeaders, CacheControlValues.MaxAgeString, out requestMaxAge);

                TimeSpan? cachedMaxAge;
                HeaderUtilities.TryParseTimeSpan(cachedCacheControlHeaders, CacheControlValues.MaxAgeString, out cachedMaxAge);

                var lowestMaxAge = cachedMaxAge < requestMaxAge ? cachedMaxAge : requestMaxAge ?? cachedMaxAge;
                // Validate max age
                if (age >= lowestMaxAge)
                {
                    // Must revalidate
                    if (HeaderUtilities.Contains(cachedCacheControlHeaders, CacheControlValues.MustRevalidateString))
                    {
                        context.Logger.LogExpirationMustRevalidate(age, lowestMaxAge.Value);
                        return false;
                    }

                    TimeSpan? requestMaxStale;
                    HeaderUtilities.TryParseTimeSpan(requestCacheControlHeaders, CacheControlValues.MaxStaleString, out requestMaxStale);

                    // Request allows stale values
                    if (requestMaxStale.HasValue && age - lowestMaxAge < requestMaxStale)
                    {
                        context.Logger.LogExpirationMaxStaleSatisfied(age, lowestMaxAge.Value, requestMaxStale.Value);
                        return true;
                    }

                    context.Logger.LogExpirationMaxAgeExceeded(age, lowestMaxAge.Value);
                    return false;
                }
                else if (!cachedMaxAge.HasValue && !requestMaxAge.HasValue)
                {
                    // Validate expiration
                    DateTimeOffset expires;
                    if (HeaderUtilities.TryParseDate(context.CachedResponseHeaders[HeaderNames.Expires], out expires) &&
                        context.ResponseTime.Value >= expires)
                    {
                        context.Logger.LogExpirationExpiresExceeded(context.ResponseTime.Value, expires);
                        return false;
                    }
                }
            }

            return true;
        }
    }
}
