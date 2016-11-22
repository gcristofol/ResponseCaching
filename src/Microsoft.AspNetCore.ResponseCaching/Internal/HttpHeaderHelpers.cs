// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Globalization;
using Microsoft.Extensions.Primitives;

namespace Microsoft.AspNetCore.ResponseCaching.Internal
{
    internal static class HttpHeaderHelpers
    {
        private static readonly string[] DateFormats = new string[] {
            // "r", // RFC 1123, required output format but too strict for input
            "ddd, d MMM yyyy H:m:s 'GMT'", // RFC 1123 (r, except it allows both 1 and 01 for date and time)
            "ddd, d MMM yyyy H:m:s", // RFC 1123, no zone - assume GMT
            "d MMM yyyy H:m:s 'GMT'", // RFC 1123, no day-of-week
            "d MMM yyyy H:m:s", // RFC 1123, no day-of-week, no zone
            "ddd, d MMM yy H:m:s 'GMT'", // RFC 1123, short year
            "ddd, d MMM yy H:m:s", // RFC 1123, short year, no zone
            "d MMM yy H:m:s 'GMT'", // RFC 1123, no day-of-week, short year
            "d MMM yy H:m:s", // RFC 1123, no day-of-week, short year, no zone

            "dddd, d'-'MMM'-'yy H:m:s 'GMT'", // RFC 850
            "dddd, d'-'MMM'-'yy H:m:s", // RFC 850 no zone
            "ddd MMM d H:m:s yyyy", // ANSI C's asctime() format

            "ddd, d MMM yyyy H:m:s zzz", // RFC 5322
            "ddd, d MMM yyyy H:m:s", // RFC 5322 no zone
            "d MMM yyyy H:m:s zzz", // RFC 5322 no day-of-week
            "d MMM yyyy H:m:s", // RFC 5322 no day-of-week, no zone
        };

        // Try the various date formats in the order listed above.
        // We should accept a wide verity of common formats, but only output RFC 1123 style dates.
        internal static bool TryParseDate(string input, out DateTimeOffset result) => DateTimeOffset.TryParseExact(input, DateFormats, DateTimeFormatInfo.InvariantInfo,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out result);

        // Try to get the value of a specific header from a list of headers
        // e.g. "header1=10, header2=30"
        internal static bool TryParseTimeSpan(StringValues headerValues, string targetValue, out TimeSpan? value)
        {
            foreach (var headerValue in headerValues)
            {
                var index = headerValue.IndexOf(targetValue, StringComparison.OrdinalIgnoreCase);
                if (index != -1)
                {
                    index += targetValue.Length;
                    int seconds;
                    if (!TryParseInt(index, headerValue, out seconds))
                    {
                        break;
                    }
                    value = TimeSpan.FromSeconds(seconds);
                    return true;
                }
            }
            value = null;
            return false;
        }

        internal static bool Contains(StringValues headerValues, string targetValue)
        {
            foreach (var headerValue in headerValues)
            {
                var index = headerValue.IndexOf(targetValue, StringComparison.OrdinalIgnoreCase);
                if (index != -1)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryParseInt(int startIndex, string headerValue, out int value)
        {
            var found = false;
            while (startIndex != headerValue.Length)
            {
                var c = headerValue[startIndex];
                if (c == '=')
                {
                    found = true;
                }
                else if (c != ' ')
                {
                    break;
                }
                ++startIndex;
            }
            if (found)
            {
                var endIndex = startIndex;
                while (endIndex < headerValue.Length)
                {
                    var c = headerValue[endIndex];
                    if ((c >= '0') && (c <= '9'))
                    {
                        endIndex++;
                    }
                    else
                    {
                        break;
                    }
                }
                var length = endIndex - startIndex;
                if (length > 0)
                {
                    value = int.Parse(headerValue.Substring(startIndex, length), NumberStyles.None, NumberFormatInfo.InvariantInfo);
                    return true;
                }
            }
            value = 0;
            return false;
        }
    }
}
