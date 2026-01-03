using System;
using System.Collections.Generic;
using System.Net;

namespace UnblockNCM.Core.Utils
{
    internal static class FormUrlEncodedParser
    {
        public static Dictionary<string, string> Parse(string body)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(body))
            {
                return result;
            }

            var raw = body;
            if (raw.Length > 0 && raw[0] == '?')
            {
                raw = raw.Substring(1);
            }

            var segments = raw.Split('&', StringSplitOptions.RemoveEmptyEntries);
            foreach (var segment in segments)
            {
                var index = segment.IndexOf('=');
                string key;
                string value;
                if (index >= 0)
                {
                    key = segment.Substring(0, index);
                    value = index + 1 < segment.Length ? segment.Substring(index + 1) : string.Empty;
                }
                else
                {
                    key = segment;
                    value = string.Empty;
                }

                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                key = WebUtility.UrlDecode(key) ?? string.Empty;
                value = WebUtility.UrlDecode(value) ?? string.Empty;
                result[key] = value;
            }

            return result;
        }
    }
}
