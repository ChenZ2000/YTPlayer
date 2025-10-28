using System;
using System.Collections.Generic;
using System.Threading;
using YTPlayer.Core.Playback.Cache;
using YTPlayer.Core.Streaming;
using YTPlayer.Models;

namespace YTPlayer.Core.Playback
{
    /// <summary>
    /// 缓存优先级
    /// </summary>
    public enum CachePriority
    {
        Critical,  // 当前播放
        High,      // 即将播放（下一首）
        Medium,    // 备选预加载
        Low        // 低优先级（可随时清理）
    }

    /// <summary>
    /// 缓存状态
    /// </summary>
    public enum CacheStatus
    {
        Pending,   // 等待预加载
        Loading,   // 正在加载
        Ready,     // 就绪
        Playing,   // 正在播放
        Stale      // 过期（需要清理）
    }

    /// <summary>
    /// 缓存下载状态
    /// </summary>
    public enum CacheDownloadStatus
    {
        NotStarted,      // 未开始
        Initializing,    // 初始化中
        Downloading,     // 下载中
        Completed,       // 完成
        Failed,          // 失败
        Cancelled        // 已取消
    }

    /// <summary>
    /// 单个音质的缓存数据
    /// </summary>
    public class QualityCacheData : IDisposable
    {
        public string Quality { get; set; }
        public string Url { get; set; }
        public long Size { get; set; }

        // 缓存管理器（可能为null，如果尚未下载）
        public SmartCacheManager CacheManager { get; set; }
        public BassStreamProvider StreamProvider { get; set; }
        public int StreamHandle { get; set; }

        // 缓存状态
        public bool IsReady { get; set; }
        public CacheDownloadStatus DownloadStatus { get; set; }
        public DateTime CreateTime { get; set; }
        public Exception LastError { get; set; }

        public void Dispose()
        {
            try
            {
                // 释放BASS流
                if (StreamHandle != 0)
                {
                    Un4seen.Bass.Bass.BASS_StreamFree(StreamHandle);
                    StreamHandle = 0;
                }

                // 释放流提供者
                StreamProvider?.Dispose();
                StreamProvider = null;

                // 释放缓存管理器
                CacheManager?.Dispose();
                CacheManager = null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[QualityCacheData] Dispose失败: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 缓存条目 - 支持多音质并存
    /// </summary>
    public class CacheEntry : IDisposable
    {
        private readonly object _lock = new object();
        private int _refCount;

        public string SongId { get; set; }
        public SongInfo Song { get; set; }

        // 多音质缓存
        public Dictionary<string, QualityCacheData> QualityCaches { get; set; }

        // 缓存状态
        public CacheStatus Status { get; set; }
        public DateTime CreateTime { get; set; }
        public DateTime LastAccessTime { get; set; }

        // 优先级
        public CachePriority Priority { get; set; }

        // 引用计数（用于安全的资源管理）
        public int RefCount
        {
            get
            {
                lock (_lock)
                {
                    return _refCount;
                }
            }
        }

        public CacheEntry(string songId, SongInfo song)
        {
            SongId = songId ?? throw new ArgumentNullException(nameof(songId));
            Song = song ?? throw new ArgumentNullException(nameof(song));
            QualityCaches = new Dictionary<string, QualityCacheData>(StringComparer.OrdinalIgnoreCase);
            CreateTime = DateTime.UtcNow;
            LastAccessTime = DateTime.UtcNow;
            Status = CacheStatus.Pending;
            Priority = CachePriority.Medium;
            _refCount = 0;
        }

        /// <summary>
        /// 增加引用计数
        /// </summary>
        public void AddRef()
        {
            lock (_lock)
            {
                _refCount++;
                LastAccessTime = DateTime.UtcNow;
                System.Diagnostics.Debug.WriteLine($"[CacheEntry] {Song.Name} AddRef: {_refCount}");
            }
        }

        /// <summary>
        /// 释放引用
        /// </summary>
        public void Release()
        {
            lock (_lock)
            {
                if (_refCount > 0)
                {
                    _refCount--;
                    System.Diagnostics.Debug.WriteLine($"[CacheEntry] {Song.Name} Release: {_refCount}");

                    // 引用计数归零时，标记为可清理
                    if (_refCount == 0 && Status != CacheStatus.Playing)
                    {
                        Status = CacheStatus.Stale;
                    }
                }
            }
        }

        /// <summary>
        /// 获取指定音质的缓存
        /// </summary>
        public QualityCacheData GetQualityCache(string quality)
        {
            lock (_lock)
            {
                LastAccessTime = DateTime.UtcNow;
                return QualityCaches.TryGetValue(quality, out var cache) ? cache : null;
            }
        }

        /// <summary>
        /// 添加或更新音质缓存
        /// </summary>
        public void SetQualityCache(string quality, QualityCacheData cacheData)
        {
            lock (_lock)
            {
                QualityCaches[quality] = cacheData ?? throw new ArgumentNullException(nameof(cacheData));
                LastAccessTime = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// 检查是否有就绪的音质缓存
        /// </summary>
        public bool HasReadyQuality(string quality)
        {
            var cache = GetQualityCache(quality);
            return cache != null && cache.IsReady;
        }

        /// <summary>
        /// 获取所有就绪的音质
        /// </summary>
        public List<string> GetReadyQualities()
        {
            lock (_lock)
            {
                var ready = new List<string>();
                foreach (var kvp in QualityCaches)
                {
                    if (kvp.Value.IsReady)
                    {
                        ready.Add(kvp.Key);
                    }
                }
                return ready;
            }
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            lock (_lock)
            {
                System.Diagnostics.Debug.WriteLine($"[CacheEntry] Dispose: {Song.Name}, {QualityCaches.Count} qualities");

                foreach (var cache in QualityCaches.Values)
                {
                    cache?.Dispose();
                }

                QualityCaches.Clear();
                Status = CacheStatus.Stale;
            }
        }

        public override string ToString()
        {
            return $"CacheEntry[{Song.Name}, Status={Status}, Priority={Priority}, RefCount={RefCount}, Qualities={QualityCaches.Count}]";
        }
    }
}
