using System;
using System.Collections.Generic;
using System.Linq;

namespace UnblockNCM.Core.Utils
{
    public static class CookieHelper
    {
        public static Dictionary<string, string> ParseToMap(string cookie)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(cookie)) return dict;
            var parts = cookie.Split(';');
            foreach (var p in parts)
            {
                var kv = p.Split(new[] { '=' }, 2);
                if (kv.Length == 2)
                    dict[kv[0].Trim()] = kv[1].Trim();
            }
            return dict;
        }

        public static string MapToCookie(Dictionary<string, string> map)
        {
            return string.Join("; ", map.Select(kv => $"{kv.Key}={kv.Value}"));
        }
    }
}
