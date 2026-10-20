using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
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
        // One bounded transport policy for foreground blocks and sequential transfers.
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
                RetryConditionHeaderValue? retryAfter = null;

                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, _url);
                    request.Headers.Range = new RangeHeaderValue(start, end);
                    request.ApplyCustomHeaders(_headers);

                    using var response = await SendHeadersAsync(request, token).ConfigureAwait(false);
                    retryAfter = response.Headers.RetryAfter;
                    response.EnsureSuccessStatusCode();

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
                        throw new EndOfStreamException($"Chunk {chunkIndex} was truncated ({data.Length}/{expectedLength} bytes)");

                    return data;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    DebugLogger.Log(DebugLogger.LogLevel.Warning, "ChunkDownload",
                        $"Block {chunkIndex}: {ex.GetType().Name}, status={(ex as HttpRequestException)?.StatusCode}, attempt={attempt + 1}/{MaxRetryCount}");
                    if (!IsTransient(ex) || attempt + 1 == MaxRetryCount) return null;
                }
                await DelayBeforeRetryAsync(chunkIndex, attempt, token, retryAfter).ConfigureAwait(false);
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

        private async Task DelayBeforeRetryAsync(int chunkIndex, int attempt, CancellationToken token, RetryConditionHeaderValue? retryAfter = null)
        {
            int delayMs = GetRetryDelayMs(attempt, retryAfter);
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
                    return ClampRetryDelayMs((int)Math.Min(30000, Math.Ceiling(retryAfter.Delta.Value.TotalMilliseconds)));
                }

                if (retryAfter.Date.HasValue)
                {
                    TimeSpan delta = retryAfter.Date.Value - DateTimeOffset.UtcNow;
                    if (delta.TotalMilliseconds > 0)
                    {
                        return ClampRetryDelayMs((int)Math.Min(30000, Math.Ceiling(delta.TotalMilliseconds)));
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

        private async Task<HttpResponseMessage> SendHeadersAsync(HttpRequestMessage request, CancellationToken token)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(ChunkBodyReadTimeoutMs);
            try
            {
                return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                // HttpClient.Timeout is not a user cancellation on modern .NET.
                throw new TimeoutException("Audio response headers timed out");
            }
        }

        private static bool IsTransient(Exception error) => error switch
        {
            InvalidDataException => false,
            HttpRequestException http when http.StatusCode.HasValue =>
                http.StatusCode == HttpStatusCode.RequestTimeout || (int)http.StatusCode == 429 || (int)http.StatusCode >= 500,
            HttpRequestException http => http.HttpRequestError != HttpRequestError.SecureConnectionError,
            IOException or TimeoutException or OperationCanceledException => true,
            _ => false
        };

        /// <summary>
        /// Publish complete blocks only. On interruption resume at the last committed block;
        /// a server without Range support can restart with a full response and skip the prefix.
        /// The return value indicates whether the entire file (rather than a preload prefix) completed.
        /// </summary>
        public async Task<bool> DownloadAllChunksSequentialAsync(
            Func<int, byte[], Task> onChunkReady,
            IProgress<double>? progress,
            CancellationToken token,
            int? maxChunks = null)
        {
            if (onChunkReady == null) throw new ArgumentNullException(nameof(onChunkReady));
            int limit = Math.Min(_totalChunks, maxChunks ?? _totalChunks);
            if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(maxChunks));
            int chunkIndex = 0;
            string? entityTag = null;
            var buffer = new byte[_chunkSize];
            for (int attempt = 0; attempt < MaxRetryCount; attempt++)
            {
                token.ThrowIfCancellationRequested();
                RetryConditionHeaderValue? retryAfter = null;
                try
                {
                    long start = (long)chunkIndex * _chunkSize;
                    using var request = new HttpRequestMessage(HttpMethod.Get, _url);
                    request.ApplyCustomHeaders(_headers);
                    if (start > 0) request.Headers.Range = new RangeHeaderValue(start, null);
                    using var response = await SendHeadersAsync(request, token).ConfigureAwait(false);
                    retryAfter = response.Headers.RetryAfter;
                    response.EnsureSuccessStatusCode();
                    var range = response.Content.Headers.ContentRange;
                    bool full = response.StatusCode == HttpStatusCode.OK;
                    if (!full && (response.StatusCode != HttpStatusCode.PartialContent || range?.Unit != "bytes" ||
                        range.From != start || range.To != _totalSize - 1 || range.Length != _totalSize))
                        throw new InvalidDataException("Sequential response does not match the requested file interval");
                    long expected = full ? _totalSize : _totalSize - start;
                    if (response.Content.Headers.ContentLength is long length && length != expected)
                        throw new InvalidDataException("Sequential response length differs from the resolved media size");
                    string? currentTag = response.Headers.ETag?.ToString();
                    if (entityTag != null && currentTag != null && entityTag != currentTag)
                        throw new InvalidDataException("Media changed during a resumed transfer");
                    entityTag ??= currentTag;
                    await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                    // If Range was ignored, discard the prefix; never place byte zero at a later block.
                    for (long skip = full ? start : 0; skip > 0;)
                    {
                        int size = (int)Math.Min(buffer.Length, skip);
                        await ReadExactlyAsync(stream, buffer, size, token).ConfigureAwait(false);
                        skip -= size;
                    }
                    while (chunkIndex < limit)
                    {
                        int size = (int)Math.Min(_chunkSize, _totalSize - (long)chunkIndex * _chunkSize);
                        await ReadExactlyAsync(stream, buffer, size, token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        var chunk = new byte[size];
                        Buffer.BlockCopy(buffer, 0, chunk, 0, size);
                        await onChunkReady(chunkIndex, chunk).ConfigureAwait(false);
                        chunkIndex++;
                        progress?.Report(Math.Min(1.0, chunkIndex * (double)_chunkSize / _totalSize));
                    }
                    return chunkIndex == _totalChunks;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    DebugLogger.Log(DebugLogger.LogLevel.Warning, "ChunkDownload",
                        $"Sequential transfer: {ex.GetType().Name}, status={(ex as HttpRequestException)?.StatusCode}, nextBlock={chunkIndex}, attempt={attempt + 1}/{MaxRetryCount}");
                    if (!IsTransient(ex) || attempt + 1 == MaxRetryCount) throw;
                }
                // Response and stream have already been released before the backoff.
                await DelayBeforeRetryAsync(chunkIndex, attempt, token, retryAfter).ConfigureAwait(false);
            }
            return false;
        }

        private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, int count, CancellationToken token)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(ChunkBodyReadTimeoutMs);
            try
            {
                int offset = 0;
                while (offset < count)
                {
                    int read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), timeout.Token).ConfigureAwait(false);
                    if (read == 0) throw new EndOfStreamException($"Audio block ended early ({offset}/{count} bytes)");
                    offset += read;
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new TimeoutException("Audio block body timed out"); }
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
