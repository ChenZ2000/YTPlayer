using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using YTPlayer.Core.Streaming;
using YTPlayer.Utils;

namespace YTPlayer.Core.Playback.Cache
{
    /// <summary>
    /// 智能预缓存管理器 - 阶段3：预测性多点缓存
    /// 在后台预先下载文件的关键位置，减少用户跳转等待时间
    /// </summary>
    public class SmartPreCacheManager : IDisposable
    {
        /// <summary>
        /// 预缓存段
        /// </summary>
        public class PreCacheSegment
        {
            public double StartRatio { get; set; }
            public long StartPosition { get; set; }
            public ConcurrentDictionary<int, byte[]> Chunks { get; } = new ConcurrentDictionary<int, byte[]>();
            public long BytesDownloaded { get; set; }
            public bool IsActive { get; set; }
            public DateTime StartTime { get; set; }
            public CancellationTokenSource? CancellationSource { get; set; }

            public int ChunkCount => Chunks.Count;
        }

        private readonly HttpClient _preCacheHttpClient;
        private readonly ConcurrentDictionary<double, PreCacheSegment> _segments = new ConcurrentDictionary<double, PreCacheSegment>();
        private const int MaxParallelPreCacheConnections = 1; // ⭐ 优化：改为串行预缓存（1个连接），避免服务器限速
        private const int PreCacheChunkCount = 10; // 每个预缓存点下载10个块（~5MB）

        private bool _disposed;

        public SmartPreCacheManager()
        {
            // 使用优化的预缓存专用HttpClient
            _preCacheHttpClient = OptimizedHttpClientFactory.CreateForPreCache();
        }

        /// <summary>
        /// 启动智能预缓存（串行策略）
        /// ⭐ 优化：改为串行预缓存，先缓存 25%，完成后再缓存 50%，最后缓存 75%
        /// 避免并发请求导致服务器限速
        /// </summary>
        /// <param name="url">音频URL</param>
        /// <param name="totalSize">文件总大小</param>
        /// <param name="chunkSize">块大小</param>
        /// <param name="token">取消令牌</param>
        public async Task StartPreCachingAsync(
            string url,
            long totalSize,
            int chunkSize,
            CancellationToken token)
        {
            // ⭐ 优化：串行预缓存策略 - 先 25%，再 50%，最后 75%
            var predictPositions = new[] { 0.25, 0.5, 0.75 };

            DebugLogger.Log(
                DebugLogger.LogLevel.Info,
                "SmartPreCache",
                $"启动串行智能预缓存，目标位置: {string.Join(" -> ", predictPositions.Select(p => $"{p * 100}%"))}");

            // ⭐ 串行执行：逐个完成，不并发
            foreach (var ratio in predictPositions)
            {
                if (_disposed || token.IsCancellationRequested)
                {
                    break;
                }

                long skipTo = (long)(totalSize * ratio);

                var segment = new PreCacheSegment
                {
                    StartRatio = ratio,
                    StartPosition = skipTo,
                    IsActive = true,
                    StartTime = DateTime.UtcNow,
                    CancellationSource = CancellationTokenSource.CreateLinkedTokenSource(token)
                };

                if (_segments.TryAdd(ratio, segment))
                {
                    try
                    {
                        // ⭐ 串行执行：等待当前段完成后再开始下一个
                        await PreCacheSegmentAsync(url, totalSize, skipTo, chunkSize, segment).ConfigureAwait(false);

                        DebugLogger.Log(
                            DebugLogger.LogLevel.Info,
                            "SmartPreCache",
                            $"✓ [{ratio * 100:F0}%] 段预缓存完成，继续下一段");
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.LogException("SmartPreCache", ex, $"预缓存段 {ratio * 100:F0}% 失败");
                        // 继续处理下一个段，不中断整个流程
                    }
                }
            }

            DebugLogger.Log(
                DebugLogger.LogLevel.Info,
                "SmartPreCache",
                "✓ 所有预缓存段已完成");
        }

        /// <summary>
        /// 预缓存单个段
        /// </summary>
        private async Task PreCacheSegmentAsync(
            string url,
            long totalSize,
            long skipTo,
            int chunkSize,
            PreCacheSegment segment)
        {
            if (_disposed || segment.CancellationSource == null)
            {
                return;
            }

            try
            {
                DebugLogger.Log(
                    DebugLogger.LogLevel.Info,
                    "SmartPreCache",
                    $"[{segment.StartRatio * 100:F0}%] 开始预缓存，位置: {skipTo:N0} bytes");

                var downloader = new StreamSkipDownloader(_preCacheHttpClient, url, totalSize);
                var progress = new Progress<(long Current, long Total, bool IsSkipping)>(p =>
                {
                    // 静默处理，不输出太多日志
                    if (p.IsSkipping && p.Current % (100 * 1024 * 1024) == 0)
                    {
                        DebugLogger.Log(
                            DebugLogger.LogLevel.Info,
                            "SmartPreCache",
                            $"[{segment.StartRatio * 100:F0}%] 跳转进度: {p.Current * 100.0 / p.Total:F1}%");
                    }
                });

                int chunksDownloaded = 0;

                bool success = await downloader.DownloadWithSkipAsync(
                    skipTo,
                    chunkSize,
                    async (chunkIndex, data) =>
                    {
                        if (segment.IsActive && chunksDownloaded < PreCacheChunkCount)
                        {
                            segment.Chunks.TryAdd(chunkIndex, data);
                            long newBytes = segment.BytesDownloaded + data.Length;
                            segment.BytesDownloaded = newBytes;
                            chunksDownloaded++;

                            // 下载足够的块后停止
                            if (chunksDownloaded >= PreCacheChunkCount)
                            {
                                segment.CancellationSource?.Cancel();
                            }
                        }

                        await Task.CompletedTask;
                    },
                    progress,
                    segment.CancellationSource.Token).ConfigureAwait(false);

                DebugLogger.Log(
                    DebugLogger.LogLevel.Info,
                    "SmartPreCache",
                    $"[{segment.StartRatio * 100:F0}%] ✓ 预缓存完成: {segment.ChunkCount} 块, {segment.BytesDownloaded:N0} bytes");
            }
            catch (OperationCanceledException)
            {
                DebugLogger.Log(
                    DebugLogger.LogLevel.Info,
                    "SmartPreCache",
                    $"[{segment.StartRatio * 100:F0}%] 预缓存被取消");
            }
            catch (Exception ex)
            {
                DebugLogger.LogException("SmartPreCache", ex, $"预缓存失败: {segment.StartRatio * 100:F0}%");
            }
            finally
            {
                segment.IsActive = false;
                segment.CancellationSource?.Dispose();
                segment.CancellationSource = null;
            }
        }

        /// <summary>
        /// 尝试从预缓存中获取数据
        /// </summary>
        /// <param name="targetRatio">目标位置比例（0-1）</param>
        /// <param name="segment">输出预缓存段</param>
        /// <returns>是否命中预缓存</returns>
        public bool TryGetPreCachedSegment(double targetRatio, out PreCacheSegment segment)
        {
            // 查找最接近的预缓存段（误差范围5%）
            var closest = _segments
                .Where(kvp => Math.Abs(kvp.Key - targetRatio) < 0.05)
                .OrderBy(kvp => Math.Abs(kvp.Key - targetRatio))
                .FirstOrDefault();

            segment = closest.Value;

            if (segment != null && segment.ChunkCount >= 3)
            {
                DebugLogger.Log(
                    DebugLogger.LogLevel.Info,
                    "SmartPreCache",
                    $"✓ 预缓存命中！目标: {targetRatio * 100:F0}%, 命中段: {segment.StartRatio * 100:F0}%, 块数: {segment.ChunkCount}");
                return true;
            }

            return false;
        }

        /// <summary>
        /// 检查指定位置是否已预缓存
        /// ⭐⭐⭐ 关键修复：即使segment还在活动，只要数据块已下载就返回
        /// </summary>
        public bool IsPositionPreCached(long position, long totalSize, int chunkSize, out PreCacheSegment? segment)
        {
            double ratio = (double)position / totalSize;
            int targetChunkIndex = (int)(position / chunkSize);

            foreach (var kvp in _segments)
            {
                var seg = kvp.Value;

                // ⭐ 移除 IsActive 检查 - 只检查块数量
                if (seg.ChunkCount < 3)
                {
                    continue;
                }

                int segmentStartChunk = (int)(seg.StartPosition / chunkSize);
                int segmentEndChunk = segmentStartChunk + seg.ChunkCount;

                if (targetChunkIndex >= segmentStartChunk && targetChunkIndex < segmentEndChunk)
                {
                    // ⭐ 进一步验证：目标块是否真的在 Chunks 中
                    if (seg.Chunks.ContainsKey(targetChunkIndex))
                    {
                        segment = seg;
                        return true;
                    }
                }
            }

            segment = null;
            return false;
        }

        /// <summary>
        /// 停止指定位置的预缓存
        /// </summary>
        public void StopPreCaching(double ratio)
        {
            if (_segments.TryGetValue(ratio, out var segment))
            {
                segment.IsActive = false;
                segment.CancellationSource?.Cancel();
            }
        }

        /// <summary>
        /// 停止所有预缓存
        /// </summary>
        public void StopAllPreCaching()
        {
            foreach (var segment in _segments.Values)
            {
                segment.IsActive = false;
                segment.CancellationSource?.Cancel();
            }
        }

        /// <summary>
        /// 清除预缓存数据
        /// </summary>
        public void Clear()
        {
            StopAllPreCaching();
            _segments.Clear();
        }

        /// <summary>
        /// 获取预缓存统计信息
        /// </summary>
        public string GetStatistics()
        {
            int activeSegments = _segments.Count(s => s.Value.IsActive);
            int completedSegments = _segments.Count(s => !s.Value.IsActive && s.Value.ChunkCount > 0);
            long totalBytes = _segments.Sum(s => s.Value.BytesDownloaded);

            return $"预缓存段: {_segments.Count} (活跃: {activeSegments}, 完成: {completedSegments}), 总缓存: {totalBytes:N0} bytes";
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopAllPreCaching();
            _preCacheHttpClient?.Dispose();
        }
    }
}
