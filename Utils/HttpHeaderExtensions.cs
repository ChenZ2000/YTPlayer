using System;
using System.Collections.Generic;
using System.Net.Http;

namespace YTPlayer.Utils
{
    /// <summary>
    /// 简化为 HttpRequestMessage 应用自定义头的辅助方法。
    /// </summary>
    internal static class HttpHeaderExtensions
    {
        public static void ApplyCustomHeaders(this HttpRequestMessage request, IDictionary<string, string>? headers)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (headers == null) return;

            foreach (var kv in headers)
            {
                if (string.IsNullOrWhiteSpace(kv.Key)) continue;
                try
                {
                    if (kv.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
                    {
                        request.Headers.UserAgent.Clear();
                        request.Headers.UserAgent.ParseAdd(kv.Value);
                    }
                    else if (kv.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase) ||
                             kv.Key.Equals("Referrer", StringComparison.OrdinalIgnoreCase))
                    {
                        if (Uri.TryCreate(kv.Value, UriKind.Absolute, out var refUri))
                        {
                            request.Headers.Referrer = refUri;
                        }
                    }
                    else
                    {
                        request.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                    }
                }
                catch
                {
                    // 忽略单个头设置失败，避免影响后续请求
                }
            }
        }
    }
}
