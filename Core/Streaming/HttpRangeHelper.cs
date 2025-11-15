using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace YTPlayer.Core.Streaming
{
    /// <summary>
    /// HTTP Range 请求辅助类
    /// </summary>
    public static class HttpRangeHelper
    {
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
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(url))
                return (false, 0);

            bool disposeClient = false;
            if (httpClient == null)
            {
                httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                disposeClient = true;
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, url);

                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    Debug.WriteLine($"[HttpRangeHelper] HEAD request failed: {response.StatusCode}");
                    return (false, 0);
                }

                // 检查是否支持 Range
                bool supportsRange = response.Headers.AcceptRanges?.Contains("bytes") == true ||
                                     response.Headers.Contains("Accept-Ranges");

                // 获取内容长度
                long contentLength = response.Content.Headers.ContentLength ?? 0;

                Debug.WriteLine($"[HttpRangeHelper] URL: {url}");
                Debug.WriteLine($"[HttpRangeHelper] Supports Range: {supportsRange}, Content-Length: {contentLength}");

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
            finally
            {
                if (disposeClient)
                {
                    httpClient?.Dispose();
                }
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
        /// 测试 Range 请求是否真正有效（下载第一个字节）
        /// </summary>
        public static async Task<bool> TestRangeRequestAsync(
            string url,
            HttpClient? httpClient = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(url))
                return false;

            bool disposeClient = false;
            if (httpClient == null)
            {
                httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                disposeClient = true;
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);

                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                // 206 Partial Content 表示服务器支持 Range
                bool success = response.StatusCode == System.Net.HttpStatusCode.PartialContent;

                Debug.WriteLine($"[HttpRangeHelper] Range test: {(success ? "PASS" : "FAIL")} ({response.StatusCode})");

                return success;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HttpRangeHelper] Range test error: {ex.Message}");
                return false;
            }
            finally
            {
                if (disposeClient)
                {
                    httpClient?.Dispose();
                }
            }
        }
    }
}
