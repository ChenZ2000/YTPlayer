using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using YTPlayer.Utils;

namespace YTPlayer.Core.Streaming
{
    /// <summary>
    /// HTTP Range 请求辅助类
    /// </summary>
    public static class HttpRangeHelper
    {
        private static readonly HttpClient SharedClient = CreateSharedClient();

        private static HttpClient CreateSharedClient()
        {
            return new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        }

        /// <summary>
        /// 检查URL是否支持Range请求并获取文件大小
        /// </summary>
        /// <param name="url">目标URL</param>
        /// <param name="httpClient">HTTP客户端（可选）</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>元组 (是否支持Range, 文件大小)</returns>
        public static async Task<(bool supportsRange, long contentLength)> CheckRangeSupportAsync(
            string url,
            HttpClient? httpClient = null,
            CancellationToken cancellationToken = default,
            IDictionary<string, string>? headers = null)
        {
            if (string.IsNullOrEmpty(url))
                return (false, 0);

            if (httpClient == null)
            {
                httpClient = SharedClient;
            }

            try
            {
                bool supportsRange = false;
                long contentLength = 0;

                using var request = new HttpRequestMessage(HttpMethod.Head, url);
                request.ApplyCustomHeaders(headers);

                using var response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    Debug.WriteLine($"[HttpRangeHelper] HEAD request failed: {response.StatusCode}");
                    // HEAD 失败时仍可尝试 Range 探测
                    return await ProbeRangeSupportAsync(url, httpClient, cancellationToken, headers).ConfigureAwait(false);
                }

                // 检查是否支持 Range（仅基于 HEAD 的 Accept-Ranges）
                supportsRange = response.Headers.AcceptRanges?.Contains("bytes") == true ||
                                response.Headers.Contains("Accept-Ranges");

                // 获取内容长度
                contentLength = response.Content.Headers.ContentLength ?? 0;

                Debug.WriteLine($"[HttpRangeHelper] URL: {url}");
                Debug.WriteLine($"[HttpRangeHelper] Supports Range: {supportsRange}, Content-Length: {contentLength}");

                if (contentLength > 0 && supportsRange)
                {
                    return (supportsRange, contentLength);
                }

                // HEAD 不提供长度或未声明 Range 时，尝试 Range 探测以补全信息
                var probe = await ProbeRangeSupportAsync(url, httpClient, cancellationToken, headers).ConfigureAwait(false);
                if (contentLength <= 0 && probe.contentLength > 0)
                {
                    contentLength = probe.contentLength;
                }

                supportsRange = supportsRange || probe.supportsRange;
                return (supportsRange, contentLength);
            }
            catch (TaskCanceledException)
            {
                Debug.WriteLine("[HttpRangeHelper] Request cancelled");
                return (false, 0);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HttpRangeHelper] Error checking range support: {ex.Message}");
                return (false, 0);
            }
        }

        /// <summary>
        /// 获取文件大小（通过 HEAD 请求）
        /// </summary>
        public static async Task<long> GetContentLengthAsync(
            string url,
            HttpClient? httpClient = null,
            CancellationToken cancellationToken = default)
        {
            var (_, contentLength) = await CheckRangeSupportAsync(url, httpClient, cancellationToken);
            return contentLength;
        }

        /// <summary>
        /// 尝试获取文件大小（HEAD/Range -> GET Header 兜底）
        /// </summary>
        public static async Task<long> TryGetContentLengthAsync(
            string url,
            IDictionary<string, string>? headers = null,
            CancellationToken cancellationToken = default,
            TimeSpan? timeout = null)
        {
            if (string.IsNullOrEmpty(url))
            {
                return 0;
            }

            bool disposeClient = false;
            HttpClient httpClient = SharedClient;
            try
            {
                if (timeout.HasValue && timeout.Value > TimeSpan.Zero)
                {
                    httpClient = new HttpClient { Timeout = timeout.Value };
                    disposeClient = true;
                }

                var (_, length) = await CheckRangeSupportAsync(url, httpClient, cancellationToken, headers).ConfigureAwait(false);
                if (length > 0)
                {
                    return length;
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.ApplyCustomHeaders(headers);
                if (!request.Headers.Contains("Accept-Encoding"))
                {
                    request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
                }

                using var response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    return 0;
                }

                return response.Content.Headers.ContentLength ?? 0;
            }
            catch (TaskCanceledException)
            {
                Debug.WriteLine("[HttpRangeHelper] Content-Length probe cancelled");
                return 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HttpRangeHelper] Content-Length probe error: {ex.Message}");
                return 0;
            }
            finally
            {
                if (disposeClient)
                {
                    httpClient.Dispose();
                }
            }
        }

        /// <summary>
        /// 测试 Range 请求是否真正有效（下载第一个字节）
        /// </summary>
        public static async Task<bool> TestRangeRequestAsync(
            string url,
            HttpClient? httpClient = null,
            CancellationToken cancellationToken = default,
            IDictionary<string, string>? headers = null)
        {
            if (string.IsNullOrEmpty(url))
                return false;

            if (httpClient == null)
            {
                httpClient = SharedClient;
            }

            try
            {
                var probe = await ProbeRangeSupportAsync(url, httpClient, cancellationToken, headers).ConfigureAwait(false);
                Debug.WriteLine($"[HttpRangeHelper] Range test: {(probe.supportsRange ? "PASS" : "FAIL")}");
                return probe.supportsRange;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HttpRangeHelper] Range test error: {ex.Message}");
                return false;
            }
        }

        private static async Task<(bool supportsRange, long contentLength)> ProbeRangeSupportAsync(
            string url,
            HttpClient httpClient,
            CancellationToken cancellationToken,
            IDictionary<string, string>? headers)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new RangeHeaderValue(0, 0);
                request.ApplyCustomHeaders(headers);

                using var response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);

                bool supportsRange = response.StatusCode == System.Net.HttpStatusCode.PartialContent;
                long contentLength = 0;

                // Content-Range 提供最准确的总长度
                ContentRangeHeaderValue? range = response.Content.Headers.ContentRange;
                if (range != null && range.Length.HasValue)
                {
                    contentLength = range.Length.Value;
                }
                else if (response.Content.Headers.ContentLength.HasValue)
                {
                    contentLength = response.Content.Headers.ContentLength.Value;
                }

                if (!supportsRange)
                {
                    // 某些 CDN 未返回 206，但仍支持 Range；尝试通过 Content-Range 判断
                    supportsRange = range != null && range.Unit == "bytes";
                }

                Debug.WriteLine($"[HttpRangeHelper] Range probe: supports={supportsRange}, length={contentLength}");
                return (supportsRange, contentLength);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HttpRangeHelper] Range probe error: {ex.Message}");
                return (false, 0);
            }
        }
    }
}
