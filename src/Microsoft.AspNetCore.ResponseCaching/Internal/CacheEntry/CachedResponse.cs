// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO;
using Microsoft.AspNetCore.Http;

namespace Microsoft.AspNetCore.ResponseCaching.Internal
{
    public class CachedResponse : IResponseCacheEntry
    {
        private IHeaderDictionary _headers;
        private bool _headersInitilized = false;

        public DateTimeOffset Created { get; set; }

        public int StatusCode { get; set; }

        public IHeaderDictionary Headers
        {
            get
            {
                if (!_headersInitilized && _headers == null)
                {
                    _headersInitilized = true;
                    _headers = new HeaderDictionary();
                }
                return _headers;
            }
            set
            {
                // Don't initialized header dictionary if it's explicitly set
                _headersInitilized = true;
                _headers = value;
            }
        }

        public Stream Body { get; set; }
    }
}
