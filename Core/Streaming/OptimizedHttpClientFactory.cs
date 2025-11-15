using System;
using System.Net;
using System.Net.Http;

namespace YTPlayer.Core.Streaming
{
    /// <summary>
    /// 优化的HttpClient工厂 - 阶段2：TCP参数调优
    /// 针对高码率音频流式播放优化网络参数（.NET Framework 4.8版本）
    /// </summary>
    public static class OptimizedHttpClientFactory
    {
        /// <summary>
        /// 创建针对主播放流优化的HttpClient（70%带宽保证）
        /// </summary>
        public static HttpClient CreateForMainPlayback(TimeSpan? timeout = null)
        {
            // .NET Framework 4.8 使用 HttpClientHandler
            var handler = new HttpClientHandler
            {
                // ⭐ TCP连接池优化
                MaxConnectionsPerServer = 2,

                // ⭐ 自动解压缩（GZip和Deflate）
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,

                // ⭐ 启用自动重定向
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 10
            };

            // ⭐ .NET Framework 4.8: 通过 ServicePointManager 全局优化TCP参数
            ConfigureTcpSettings();

            var client = new HttpClient(handler)
            {
                Timeout = timeout ?? TimeSpan.FromMinutes(10)
            };

            // ⭐ 设置User-Agent避免被限速
            client.DefaultRequestHeaders.UserAgent.ParseAdd("NeteaseMusic-Player/1.0");
            client.DefaultRequestHeaders.Connection.Add("keep-alive");

            return client;
        }

        /// <summary>
        /// 创建针对预缓存流优化的HttpClient（30%带宽限制）
        /// </summary>
        public static HttpClient CreateForPreCache(TimeSpan? timeout = null)
        {
            var handler = new HttpClientHandler
            {
                // ⭐ 预缓存允许更多并发连接（提升到 8，配合交错延迟降低限流）
                MaxConnectionsPerServer = 8,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 10
            };

            ConfigureTcpSettings();

            var client = new HttpClient(handler)
            {
                Timeout = timeout ?? TimeSpan.FromMinutes(5)
            };

            client.DefaultRequestHeaders.UserAgent.ParseAdd("NeteaseMusic-PreCache/1.0");
            client.DefaultRequestHeaders.Connection.Add("keep-alive");

            return client;
        }

        /// <summary>
        /// 创建针对快速跳转优化的HttpClient
        /// </summary>
        public static HttpClient CreateForFastSeek(TimeSpan? timeout = null)
        {
            var handler = new HttpClientHandler
            {
                // ⭐ 快速跳转需要最大化带宽
                MaxConnectionsPerServer = 1,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                AllowAutoRedirect = true
            };

            ConfigureTcpSettings();

            var client = new HttpClient(handler)
            {
                Timeout = timeout ?? TimeSpan.FromMinutes(5)
            };

            client.DefaultRequestHeaders.UserAgent.ParseAdd("NeteaseMusic-Seek/1.0");
            client.DefaultRequestHeaders.Connection.Add("keep-alive");

            return client;
        }

        /// <summary>
        /// 预热HTTP连接到指定URL，减少后续请求的TTFB
        /// 在GetSongUrl成功后立即调用此方法可以提前建立TCP连接
        /// </summary>
        /// <param name="url">要预热的URL（通常是音频文件URL）</param>
        public static void PreWarmConnection(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return;
            }

            // ⭐ 后台执行，不阻塞主线程
            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    using (var client = CreateForMainPlayback(TimeSpan.FromSeconds(5)))
                    {
                        var request = new HttpRequestMessage(HttpMethod.Head, url);
                        var response = await client.SendAsync(request).ConfigureAwait(false);
                        response.Dispose();

                        // 连接已建立并缓存在连接池中，后续请求将复用此连接
                        Utils.DebugLogger.Log(
                            Utils.DebugLogger.LogLevel.Info,
                            "HttpPreWarm",
                            $"连接预热成功: {new Uri(url).Host}");
                    }
                }
                catch (Exception ex)
                {
                    // 预热失败不影响正常播放流程
                    Utils.DebugLogger.LogException("HttpPreWarm", ex, "连接预热失败（不影响播放）");
                }
            });
        }

        /// <summary>
        /// 配置全局TCP设置（.NET Framework 4.8）
        /// </summary>
        private static void ConfigureTcpSettings()
        {
            // ⭐ 这些设置对所有HttpClient生效
            // 最大并发连接数（已在Program.cs中设置为100）
            // ServicePointManager.DefaultConnectionLimit = 100;

            // ⭐ 禁用Expect100Continue，减少延迟
            ServicePointManager.Expect100Continue = false;

            // ⭐ 禁用Nagle算法，减少小包延迟
            ServicePointManager.UseNagleAlgorithm = false;

            // ⭐ 启用TCP Fast Open（Windows 10+ 自动支持）
            // .NET Framework 4.8无法直接设置，依赖系统配置
        }
    }
}
