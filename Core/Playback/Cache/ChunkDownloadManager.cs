using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using YTPlayer.Utils;

namespace YTPlayer.Core.Playback.Cache
{
    /// <summary>
    /// 负责执行各类下载策略，将网络数据转换为缓存块。
    /// </summary>
    public sealed class ChunkDownloadManager
    {
        // 增加重试次数到 5 次，提高成功率
        private const int MaxRetryCount = 5;

        // 基础重试延迟（毫秒），使用指数退避
        private const int BaseRetryDelayMs = 300;
        private const int ChunkBodyReadTimeoutMs = 15000;

        private readonly string _url;
        private readonly long _totalSize;
        private readonly int _chunkSize;
        private readonly int _totalChunks;
        private readonly HttpClient _httpClient;
        private readonly IDictionary<string, string>? _headers;

        public ChunkDownloadManager(string url, long totalSize, int chunkSize, HttpClient httpClient, IDictionary<string, string>? headers = null)
        {
            _url = url ?? throw new ArgumentNullException(nameof(url));
            _totalSize = totalSize;
            _chunkSize = chunkSize;
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _headers = headers;

            _totalChunks = (int)Math.Ceiling(totalSize / (double)chunkSize);
        }

        public async Task<byte[]?> DownloadChunkAsync(int chunkIndex, CancellationToken token)
        {
            if (chunkIndex < 0 || chunkIndex >= _totalChunks)
            {
                return null;
            }

            long start = chunkIndex * (long)_chunkSize;
            long end = Math.Min(_totalSize - 1, start + _chunkSize - 1);

            for (int attempt = 0; attempt < MaxRetryCount; attempt++)
            {
                token.ThrowIfCancellationRequested();

                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, _url);
                    request.Headers.Range = new RangeHeaderValue(start, end);
                    request.ApplyCustomHeaders(_headers);

                    using var response = await _httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        token).ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        DebugLogger.Log(
                            DebugLogger.LogLevel.Warning,
                            "ChunkDownload",
                            $"Range download {chunkIndex} failed ({response.StatusCode}) attempt {attempt + 1}");

                        await DelayBeforeRetryAsync(chunkIndex, attempt, token, response).ConfigureAwait(false);
                        continue;
                    }

                    // Never cache the start of a whole-file 200 response at a requested
                    // middle offset. Incorrect ranges silently corrupt decoder input.
                    var range = response.Content.Headers.ContentRange;
                    long expectedLength = end - start + 1;
                    if (response.StatusCode != System.Net.HttpStatusCode.PartialContent ||
                        range?.Unit != "bytes" || range.From != start || range.To != end || range.Length != _totalSize ||
                        (response.Content.Headers.ContentLength is long length && length != expectedLength))
                    {
                        DebugLogger.Log(DebugLogger.LogLevel.Warning, "ChunkDownload", "Server did not return the requested byte range; rejecting chunk");
                        return null;
                    }

                    byte[] data = await ReadChunkBodyAsync(response.Content, chunkIndex, (int)expectedLength, token).ConfigureAwait(false);

                    if (data.Length != expectedLength)
                    {
                        DebugLogger.Log(
                            DebugLogger.LogLevel.Warning,
                            "ChunkDownload",
                            $"Range download {chunkIndex} returned 0 bytes");

                        await DelayBeforeRetryAsync(chunkIndex, attempt, token).ConfigureAwait(false);
                        continue;
                    }

                    return data;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    DebugLogger.LogException("ChunkDownload", ex, $"下载块 {chunkIndex} 失败 (attempt {attempt + 1}/{MaxRetryCount})");
                    await DelayBeforeRetryAsync(chunkIndex, attempt, token).ConfigureAwait(false);
                }
            }

            return null;
        }

        private async Task<byte[]> ReadChunkBodyAsync(HttpContent content, int chunkIndex, int expectedLength, CancellationToken token)
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(ChunkBodyReadTimeoutMs));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);

            try
            {
                await using Stream stream = await content.ReadAsStreamAsync(linkedCts.Token).ConfigureAwait(false);
                using var memory = new MemoryStream(_chunkSize);
                byte[] buffer = new byte[16 * 1024];

                while (true)
                {
                    int remaining = expectedLength + 1 - (int)memory.Length;
                    if (remaining <= 0) break;
                    int read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), linkedCts.Token).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        break;
                    }

                    memory.Write(buffer, 0, read);

                    // Prevent runaway payloads when a server ignores range semantics.
                    if (memory.Length > expectedLength)
                    {
                        break;
                    }
                }

                return memory.ToArray();
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new TimeoutException($"Chunk body read timeout for chunk {chunkIndex} ({ChunkBodyReadTimeoutMs}ms)");
            }
        }

        private async Task DelayBeforeRetryAsync(int chunkIndex, int attempt, CancellationToken token, HttpResponseMessage? response = null)
        {
            int delayMs = GetRetryDelayMs(attempt, response?.Headers?.RetryAfter);
            DebugLogger.Log(
                DebugLogger.LogLevel.Info,
                "ChunkDownload",
                $"Wait {delayMs}ms before retrying chunk {chunkIndex} (attempt {attempt + 1}/{MaxRetryCount})");

            await Task.Delay(delayMs, token).ConfigureAwait(false);
        }

        private static int GetRetryDelayMs(int attempt, RetryConditionHeaderValue? retryAfter)
        {
            if (retryAfter != null)
            {
                if (retryAfter.Delta.HasValue)
                {
                    return ClampRetryDelayMs((int)Math.Ceiling(retryAfter.Delta.Value.TotalMilliseconds));
                }

                if (retryAfter.Date.HasValue)
                {
                    TimeSpan delta = retryAfter.Date.Value - DateTimeOffset.UtcNow;
                    if (delta.TotalMilliseconds > 0)
                    {
                        return ClampRetryDelayMs((int)Math.Ceiling(delta.TotalMilliseconds));
                    }
                }
            }

            // Exponential backoff: 300, 600, 1200, 2400, 4800ms.
            return ClampRetryDelayMs(BaseRetryDelayMs * (1 << Math.Min(attempt, 10)));
        }

        private static int ClampRetryDelayMs(int delayMs)
        {
            const int minDelay = 200;
            const int maxDelay = 30000;
            if (delayMs < minDelay)
            {
                return minDelay;
            }

            return delayMs > maxDelay ? maxDelay : delayMs;
        }

        public async Task DownloadAllChunksSequentialAsync(
            Func<int, byte[], Task> onChunkReady,
            IProgress<double>? progress,
            CancellationToken token)
        {
            if (onChunkReady == null)
            {
                throw new ArgumentNullException(nameof(onChunkReady));
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, _url);
            request.ApplyCustomHeaders(_headers);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                token).ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            {
                var buffer = new byte[_chunkSize];
                int chunkIndex = 0;
                long totalBytesRead = 0;

                while (totalBytesRead < _totalSize)
                {
                    token.ThrowIfCancellationRequested();

                    // ⭐⭐⭐ 关键修复：循环读取直到填满整个chunk
                    // stream.ReadAsync可能返回少于请求的字节数，必须循环读取！
                    int chunkBytesRead = 0;
                    while (chunkBytesRead < buffer.Length)
                    {
                        int read = await stream.ReadAsync(
                            buffer,
                            chunkBytesRead,
                            buffer.Length - chunkBytesRead,
                            token).ConfigureAwait(false);

                        if (read == 0)
                        {
                            // ⭐⭐⭐ 流结束，检查是否真的完成了
                            if (totalBytesRead < _totalSize)
                            {
                                DebugLogger.Log(
                                    DebugLogger.LogLevel.Warning,
                                    "ChunkDownload",
                                    $"⚠️ HTTP stream提前结束: {totalBytesRead:N0}/{_totalSize:N0} (缺少 {_totalSize - totalBytesRead:N0} bytes)");
                            }
                            break;
                        }

                        chunkBytesRead += read;
                        totalBytesRead += read;

                        // 如果已经读取完整个文件，退出内层循环
                        if (totalBytesRead >= _totalSize)
                        {
                            break;
                        }
                    }

                    // 如果这个chunk没有读取到任何数据，说明文件结束
                    if (chunkBytesRead == 0)
                    {
                        break;
                    }

                    // 创建实际大小的chunk副本
                    var chunkCopy = new byte[chunkBytesRead];
                    Buffer.BlockCopy(buffer, 0, chunkCopy, 0, chunkBytesRead);
                    await onChunkReady(chunkIndex, chunkCopy).ConfigureAwait(false);

                    DebugLogger.Log(
                        DebugLogger.LogLevel.Info,
                        "ChunkDownload",
                        $"✓ 顺序下载块 {chunkIndex}: {chunkBytesRead} bytes (总进度: {totalBytesRead:N0}/{_totalSize:N0})");

                    chunkIndex++;
                    progress?.Report(Math.Min(1.0, totalBytesRead / (double)_totalSize));
                }
            }
        }

        public async Task<bool> DownloadAllChunksParallelAsync(
            int maxConnections,
            Func<int, byte[], Task> onChunkReady,
            IProgress<double>? progress,
            CancellationToken token)
        {
            if (onChunkReady == null)
            {
                throw new ArgumentNullException(nameof(onChunkReady));
            }

            if (maxConnections <= 0)
            {
                maxConnections = 1;
            }

            var semaphore = new SemaphoreSlim(maxConnections, maxConnections);
            var tasks = new List<Task>();
            long totalBytes = 0;
            int failures = 0;

            // 交错延迟：每个请求间隔 200ms，避免服务器限流
            const int StaggerDelayMs = 200;

            for (int chunkIndex = 0; chunkIndex < _totalChunks; chunkIndex++)
            {
                await semaphore.WaitAsync(token).ConfigureAwait(false);

                // 🎯 关键优化：在启动每个下载前添加小延迟，让服务器更容易处理
                // 前几个请求不延迟，后续请求交错启动
                if (chunkIndex > 0 && chunkIndex % maxConnections == 0)
                {
                    await Task.Delay(StaggerDelayMs, token).ConfigureAwait(false);
                }

                int capturedIndex = chunkIndex;
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        byte[]? data = await DownloadChunkAsync(capturedIndex, token).ConfigureAwait(false);
                        if (data == null)
                        {
                            Interlocked.Increment(ref failures);
                            return;
                        }

                        await onChunkReady(capturedIndex, data).ConfigureAwait(false);
                        long written = Interlocked.Add(ref totalBytes, data.Length);
                        progress?.Report(Math.Min(1.0, written / (double)_totalSize));
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, token));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);

            return failures == 0;
        }
    }
}
