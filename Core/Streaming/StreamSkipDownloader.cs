using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using YTPlayer.Utils;

namespace YTPlayer.Core.Streaming
{
    /// <summary>
    /// 流式快速跳转下载器 - 阶段2优化版
    /// 用于在不支持 HTTP Range 的情况下，通过读取并丢弃数据快速定位到目标位置
    /// 集成了动态buffer优化和性能监控
    /// </summary>
    public class StreamSkipDownloader
    {
        private readonly HttpClient _httpClient;
        private readonly string _url;
        private readonly long _totalSize;
        private readonly DynamicBufferOptimizer _bufferOptimizer;
        private readonly System.Collections.Generic.IDictionary<string, string>? _headers;

        public StreamSkipDownloader(HttpClient httpClient, string url, long totalSize, System.Collections.Generic.IDictionary<string, string>? headers = null)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _url = url ?? throw new ArgumentNullException(nameof(url));
            _totalSize = totalSize;
            _bufferOptimizer = new DynamicBufferOptimizer(totalSize);
            _headers = headers;
        }

        /// <summary>
        /// 下载并跳过到指定位置，然后开始保存数据块
        /// </summary>
        /// <param name="skipToPosition">要跳过到的目标位置</param>
        /// <param name="chunkSize">块大小</param>
        /// <param name="onChunkReady">块准备好的回调（块索引，数据）</param>
        /// <param name="progress">进度报告（0-1）</param>
        /// <param name="token">取消令牌</param>
        /// <returns>是否成功</returns>
        public async Task<bool> DownloadWithSkipAsync(
            long skipToPosition,
            int chunkSize,
            Func<int, byte[], Task> onChunkReady,
            IProgress<(long Current, long Total, bool IsSkipping)>? progress,
            CancellationToken token)
        {
            if (skipToPosition >= _totalSize)
            {
                return false;
            }

            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, _url))
                {
                    request.ApplyCustomHeaders(_headers);
                    using (var response = await _httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        token).ConfigureAwait(false))
                    {
                    if (!response.IsSuccessStatusCode)
                    {
                        DebugLogger.Log(
                            DebugLogger.LogLevel.Error,
                            "StreamSkip",
                            $"HTTP请求失败: {response.StatusCode}");
                        return false;
                    }

                    using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    {
                        long currentPosition = 0;

                        // ⭐ 阶段2优化：使用动态buffer
                        _bufferOptimizer.StartMeasurement();
                        byte[] discardBuffer = new byte[_bufferOptimizer.CurrentBufferSize];

                        var stopwatch = Stopwatch.StartNew();

                        // ======== 第一阶段：快速丢弃到目标位置 ========
                        double estimatedTime = _bufferOptimizer.EstimateSeekTime(skipToPosition);
                        DebugLogger.Log(
                            DebugLogger.LogLevel.Info,
                            "StreamSkip",
                            $"开始快速跳转到位置 {skipToPosition:N0} / {_totalSize:N0} ({skipToPosition * 100.0 / _totalSize:F1}%), 预计耗时: {estimatedTime:F1}秒");

                        while (currentPosition < skipToPosition && !token.IsCancellationRequested)
                        {
                            long remaining = skipToPosition - currentPosition;

                            // ⭐ 阶段2优化：动态调整buffer大小
                            int currentBufferSize = _bufferOptimizer.CurrentBufferSize;
                            if (discardBuffer.Length != currentBufferSize)
                            {
                                discardBuffer = new byte[currentBufferSize];
                                DebugLogger.Log(
                                    DebugLogger.LogLevel.Info,
                                    "StreamSkip",
                                    $"Buffer大小调整为 {currentBufferSize / 1024 / 1024} MB (速度: {_bufferOptimizer.GetCurrentSpeed():F1} MB/s)");
                            }

                            int toRead = (int)Math.Min(remaining, discardBuffer.Length);

                            int bytesRead = await stream.ReadAsync(
                                discardBuffer,
                                0,
                                toRead,
                                token).ConfigureAwait(false);

                            if (bytesRead == 0)
                            {
                                DebugLogger.Log(
                                    DebugLogger.LogLevel.Warning,
                                    "StreamSkip",
                                    $"流意外结束于 {currentPosition:N0} (目标: {skipToPosition:N0})");
                                return false;
                            }

                            currentPosition += bytesRead;

                            // ⭐ 阶段2优化：记录进度以优化buffer
                            _bufferOptimizer.RecordProgress(bytesRead);

                            // 报告丢弃进度（每 16MB 报告一次）
                            if (progress != null && currentPosition % (16 * 1024 * 1024) < discardBuffer.Length)
                            {
                                progress.Report((currentPosition, _totalSize, true));
                            }
                        }

                        _bufferOptimizer.StopMeasurement();
                        stopwatch.Stop();
                        double skipSpeed = currentPosition / stopwatch.Elapsed.TotalSeconds / (1024 * 1024);
                        DebugLogger.Log(
                            DebugLogger.LogLevel.Info,
                            "StreamSkip",
                            $"✓ 快速跳转完成: {currentPosition:N0} bytes, 耗时 {stopwatch.Elapsed.TotalSeconds:F2}秒, 速度 {skipSpeed:F1} MB/s, 最终Buffer: {_bufferOptimizer.CurrentBufferSize / 1024 / 1024} MB");

                        if (token.IsCancellationRequested)
                        {
                            return false;
                        }

                        // ======== 第二阶段：保存目标位置之后的数据块 ========
                        int startChunkIndex = (int)(skipToPosition / chunkSize);
                        byte[] chunkBuffer = new byte[chunkSize];
                        int bufferOffset = 0;

                        DebugLogger.Log(
                            DebugLogger.LogLevel.Info,
                            "StreamSkip",
                            $"开始保存数据块，起始块索引: {startChunkIndex}");

                        while (!token.IsCancellationRequested)
                        {
                            int toRead = chunkBuffer.Length - bufferOffset;
                            int bytesRead = await stream.ReadAsync(
                                chunkBuffer,
                                bufferOffset,
                                toRead,
                                token).ConfigureAwait(false);

                            if (bytesRead == 0)
                            {
                                // ⭐⭐⭐ 流结束，检查是否真的完成了
                                if (currentPosition < _totalSize)
                                {
                                    DebugLogger.Log(
                                        DebugLogger.LogLevel.Warning,
                                        "StreamSkip",
                                        $"⚠️ HTTP stream提前结束: {currentPosition:N0}/{_totalSize:N0} (缺少 {_totalSize - currentPosition:N0} bytes)");
                                }

                                // 保存最后一个不完整的块
                                if (bufferOffset > 0)
                                {
                                    byte[] lastChunk = new byte[bufferOffset];
                                    Array.Copy(chunkBuffer, 0, lastChunk, 0, bufferOffset);
                                    await onChunkReady(startChunkIndex, lastChunk).ConfigureAwait(false);
                                    DebugLogger.Log(
                                        DebugLogger.LogLevel.Info,
                                        "StreamSkip",
                                        $"✓ 保存最后块 {startChunkIndex} ({bufferOffset} bytes)");
                                }
                                break;
                            }

                            currentPosition += bytesRead;
                            bufferOffset += bytesRead;

                            // 当缓冲区满时，保存整块
                            if (bufferOffset >= chunkBuffer.Length)
                            {
                                await onChunkReady(startChunkIndex, chunkBuffer).ConfigureAwait(false);
                                DebugLogger.Log(
                                    DebugLogger.LogLevel.Info,
                                    "StreamSkip",
                                    $"保存块 {startChunkIndex} ({chunkBuffer.Length} bytes)");

                                startChunkIndex++;
                                bufferOffset = 0;
                                chunkBuffer = new byte[chunkSize]; // 新建缓冲区
                            }

                            // 报告保存进度
                            if (progress != null)
                            {
                                progress.Report((currentPosition, _totalSize, false));
                            }
                        }

                        DebugLogger.Log(
                            DebugLogger.LogLevel.Info,
                            "StreamSkip",
                            $"✓ 下载完成: {currentPosition:N0} / {_totalSize:N0} bytes");

                        return true;
                    }
                }
            }
            }
            catch (OperationCanceledException)
            {
                DebugLogger.Log(
                    DebugLogger.LogLevel.Info,
                    "StreamSkip",
                    "下载被取消");
                return false;
            }
            catch (Exception ex)
            {
                DebugLogger.LogException("StreamSkip", ex, "下载失败");
                return false;
            }
        }

        /// <summary>
        /// 仅下载文件末尾的最后N个块
        /// </summary>
        /// <param name="lastNChunks">最后N块</param>
        /// <param name="chunkSize">块大小</param>
        /// <param name="onChunkReady">块准备好的回调</param>
        /// <param name="progress">进度报告</param>
        /// <param name="token">取消令牌</param>
        /// <returns>是否成功</returns>
        public async Task<bool> DownloadLastChunksAsync(
            int lastNChunks,
            int chunkSize,
            Func<int, byte[], Task> onChunkReady,
            IProgress<(long Current, long Total, bool IsSkipping)>? progress,
            CancellationToken token)
        {
            int totalChunks = (int)Math.Ceiling((double)_totalSize / chunkSize);
            int startChunkIndex = Math.Max(0, totalChunks - lastNChunks);
            long skipToPosition = startChunkIndex * (long)chunkSize;

            DebugLogger.Log(
                DebugLogger.LogLevel.Info,
                "StreamSkip",
                $"下载最后 {lastNChunks} 块 (总共 {totalChunks} 块，从块 {startChunkIndex} 开始)");

            return await DownloadWithSkipAsync(
                skipToPosition,
                chunkSize,
                onChunkReady,
                progress,
                token).ConfigureAwait(false);
        }
    }
}
