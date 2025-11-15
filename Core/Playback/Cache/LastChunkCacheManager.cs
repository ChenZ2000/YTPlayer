using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using YTPlayer.Core.Streaming;
using YTPlayer.Utils;

namespace YTPlayer.Core.Playback.Cache
{
    /// <summary>
    /// 最后块预缓存数据
    /// </summary>
    public class LastChunksData
    {
        public string SongId { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public long TotalSize { get; set; }
        public Dictionary<int, byte[]> Chunks { get; set; } = new Dictionary<int, byte[]>();
        public DateTime CachedTime { get; set; }
        public long MemorySize { get; set; }

        public LastChunksData()
        {
        }

        public LastChunksData(string songId, string url, long totalSize)
        {
            SongId = songId;
            Url = url;
            TotalSize = totalSize;
            CachedTime = DateTime.UtcNow;
            Chunks = new Dictionary<int, byte[]>();
        }
    }

    /// <summary>
    /// 最后块预缓存管理器（单例模式）
    /// 管理歌曲最后几个块的预缓存，用于加速播放启动
    /// </summary>
    public sealed class LastChunkCacheManager
    {
        private static readonly Lazy<LastChunkCacheManager> _instance =
            new Lazy<LastChunkCacheManager>(() => new LastChunkCacheManager());

        public static LastChunkCacheManager Instance => _instance.Value;

        private const int MaxCachedSongs = int.MaxValue;  // 🎯 无歌曲数量限制，只受内存限制
        private const int MaxMemoryMB = 1024;     // 🎯 最大内存占用 1GB (从 100MB 扩展 10 倍)
        private const int ChunkSize = 256 * 1024; // 256KB，与 SmartCacheManager 保持一致

        // songId -> LastChunksData
        private readonly ConcurrentDictionary<string, LastChunksData> _cache =
            new ConcurrentDictionary<string, LastChunksData>();

        // LRU 访问记录：songId -> lastAccessTime
        private readonly ConcurrentDictionary<string, DateTime> _accessTimes =
            new ConcurrentDictionary<string, DateTime>();

        private long _totalMemoryBytes = 0;
        private readonly object _memoryLock = new object();

        private LastChunkCacheManager()
        {
            DebugLogger.Log(
                DebugLogger.LogLevel.Info,
                "LastChunkCache",
                "🎯 最后块预缓存系统已初始化");
        }

        /// <summary>
        /// 尝试获取缓存的最后块数据
        /// </summary>
        public LastChunksData? TryGet(string songId, string url, long totalSize)
        {
            if (string.IsNullOrEmpty(songId))
            {
                return null;
            }

            if (_cache.TryGetValue(songId, out var data))
            {
                // 验证 URL 和大小是否匹配（URL 可能会过期或变化）
                if (data.Url == url && data.TotalSize == totalSize)
                {
                    // 更新访问时间
                    _accessTimes[songId] = DateTime.UtcNow;

                    DebugLogger.Log(
                        DebugLogger.LogLevel.Info,
                        "LastChunkCache",
                        $"🎯 ✓ 命中缓存: songId={songId}, chunks={data.Chunks.Count}, memory={data.MemorySize / 1024}KB");

                    return data;
                }
                else
                {
                    // URL 或大小不匹配，移除过期缓存
                    Remove(songId);
                    DebugLogger.Log(
                        DebugLogger.LogLevel.Warning,
                        "LastChunkCache",
                        $"🎯 ✗ 缓存失效: songId={songId} (URL或大小不匹配)");
                }
            }

            return null;
        }

        /// <summary>
        /// 添加最后块缓存
        /// </summary>
        public void Add(string songId, string url, long totalSize, Dictionary<int, byte[]> chunks)
        {
            if (string.IsNullOrEmpty(songId) || chunks == null || chunks.Count == 0)
            {
                return;
            }

            // 计算内存占用
            long memorySize = chunks.Values.Sum(c => c.Length);

            var data = new LastChunksData(songId, url, totalSize)
            {
                Chunks = new Dictionary<int, byte[]>(chunks),
                MemorySize = memorySize
            };

            // 添加或更新缓存
            bool isNew = !_cache.ContainsKey(songId);
            _cache[songId] = data;
            _accessTimes[songId] = DateTime.UtcNow;

            lock (_memoryLock)
            {
                _totalMemoryBytes += memorySize;
            }

            DebugLogger.Log(
                DebugLogger.LogLevel.Info,
                "LastChunkCache",
                $"🎯 {(isNew ? "新增" : "更新")}缓存: songId={songId}, chunks={chunks.Count}, " +
                $"memory={memorySize / 1024}KB, total={_totalMemoryBytes / 1024 / 1024}MB");

            // 触发淘汰检查
            EvictIfNeeded();
        }

        /// <summary>
        /// 移除指定歌曲的缓存
        /// </summary>
        public void Remove(string songId)
        {
            if (_cache.TryRemove(songId, out var data))
            {
                _accessTimes.TryRemove(songId, out _);

                lock (_memoryLock)
                {
                    _totalMemoryBytes -= data.MemorySize;
                }

                DebugLogger.Log(
                    DebugLogger.LogLevel.Info,
                    "LastChunkCache",
                    $"🎯 移除缓存: songId={songId}, released={data.MemorySize / 1024}KB");
            }
        }

        /// <summary>
        /// 清空所有缓存
        /// </summary>
        public void Clear()
        {
            _cache.Clear();
            _accessTimes.Clear();

            lock (_memoryLock)
            {
                _totalMemoryBytes = 0;
            }

            DebugLogger.Log(
                DebugLogger.LogLevel.Info,
                "LastChunkCache",
                "🎯 清空所有缓存");
        }

        /// <summary>
        /// 预下载指定歌曲的最后块（异步）
        /// </summary>
        public async Task<bool> PreDownloadLastChunksAsync(
            string songId,
            string url,
            long totalSize,
            HttpClient httpClient,
            CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(songId) || string.IsNullOrEmpty(url) || totalSize <= 0)
            {
                return false;
            }

            try
            {
                // 检查是否已经有有效缓存
                if (TryGet(songId, url, totalSize) != null)
                {
                    DebugLogger.Log(
                        DebugLogger.LogLevel.Info,
                        "LastChunkCache",
                        $"🎯 跳过预下载: songId={songId} (已有缓存)");
                    return true;
                }

                DebugLogger.Log(
                    DebugLogger.LogLevel.Info,
                    "LastChunkCache",
                    $"🎯 开始预下载最后块: songId={songId}, size={totalSize:N0}");

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                // 使用 StreamSkipDownloader 下载最后 2 chunks
                var downloadedChunks = new ConcurrentDictionary<int, byte[]>();
                var skipDownloader = new StreamSkipDownloader(httpClient, url, totalSize);

                bool success = await skipDownloader.DownloadLastChunksAsync(
                    lastNChunks: 2,
                    chunkSize: ChunkSize,
                    onChunkReady: async (chunkIndex, chunkData) =>
                    {
                        downloadedChunks[chunkIndex] = chunkData;
                        await Task.CompletedTask;
                    },
                    progress: null,
                    token: token).ConfigureAwait(false);

                stopwatch.Stop();

                if (success && downloadedChunks.Count > 0)
                {
                    // 添加到缓存
                    Add(songId, url, totalSize, new Dictionary<int, byte[]>(downloadedChunks));

                    DebugLogger.Log(
                        DebugLogger.LogLevel.Info,
                        "LastChunkCache",
                        $"🎯 ✓ 预下载完成: songId={songId}, chunks={downloadedChunks.Count}, " +
                        $"耗时={stopwatch.ElapsedMilliseconds}ms");

                    return true;
                }
                else
                {
                    DebugLogger.Log(
                        DebugLogger.LogLevel.Warning,
                        "LastChunkCache",
                        $"🎯 ✗ 预下载失败: songId={songId}");

                    return false;
                }
            }
            catch (OperationCanceledException)
            {
                DebugLogger.Log(
                    DebugLogger.LogLevel.Info,
                    "LastChunkCache",
                    $"🎯 预下载取消: songId={songId}");
                return false;
            }
            catch (Exception ex)
            {
                DebugLogger.LogException("LastChunkCache", ex, $"预下载失败: songId={songId}");
                return false;
            }
        }

        /// <summary>
        /// 根据 LRU 策略淘汰缓存
        /// </summary>
        private void EvictIfNeeded()
        {
            // 检查缓存数量
            while (_cache.Count > MaxCachedSongs)
            {
                EvictLeastRecentlyUsed();
            }

            // 检查内存占用
            while (_totalMemoryBytes > MaxMemoryMB * 1024 * 1024)
            {
                if (!EvictLeastRecentlyUsed())
                {
                    break;  // 没有可淘汰的了
                }
            }
        }

        /// <summary>
        /// 淘汰最久未使用的缓存项
        /// </summary>
        private bool EvictLeastRecentlyUsed()
        {
            if (_accessTimes.IsEmpty)
            {
                return false;
            }

            // 找到最久未使用的项
            var lruItem = _accessTimes.OrderBy(kvp => kvp.Value).FirstOrDefault();
            if (!string.IsNullOrEmpty(lruItem.Key))
            {
                DebugLogger.Log(
                    DebugLogger.LogLevel.Info,
                    "LastChunkCache",
                    $"🎯 LRU淘汰: songId={lruItem.Key}, lastAccess={lruItem.Value}");

                Remove(lruItem.Key);
                return true;
            }

            return false;
        }

        /// <summary>
        /// 获取缓存统计信息
        /// </summary>
        public string GetStatistics()
        {
            lock (_memoryLock)
            {
                return $"缓存歌曲数: {_cache.Count}/{MaxCachedSongs}, " +
                       $"内存占用: {_totalMemoryBytes / 1024 / 1024}MB/{MaxMemoryMB}MB";
            }
        }
    }
}
