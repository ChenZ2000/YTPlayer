using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using YTPlayer.Core.Playback.Cache;
using YTPlayer.Core.Download;
using YTPlayer.Core.Streaming;
using YTPlayer.Models;

#pragma warning disable CS8600, CS8601, CS8602, CS8603, CS8604, CS8625

namespace YTPlayer.Core.Playback
{
    /// <summary>
    /// 下一首歌曲预加载器 - 全新设计，简洁高效
    /// 职责：预获取 URL、预下载 Chunk 0、创建就绪的 BASS 流对象
    /// </summary>
    public class NextSongPreloader : IDisposable
    {
        #region BASS P/Invoke

        [DllImport("bass.dll")]
        private static extern bool BASS_StreamFree(int handle);

        #endregion

        #region 预加载数据结构

        /// <summary>
        /// 预加载的歌曲数据
        /// </summary>
        private class PreloadedSongData
        {
            public string SongId { get; set; } = string.Empty;
            public string Url { get; set; } = string.Empty;
            public string Level { get; set; } = string.Empty;
            public long Size { get; set; }
            public bool IsTrial { get; set; }
            public long TrialStart { get; set; }
            public long TrialEnd { get; set; }
            public bool IsUnblocked { get; set; }
            public string UnblockSource { get; set; } = string.Empty;
            public Dictionary<string, string>? CustomHeaders { get; set; }
            public SmartCacheManager CacheManager { get; set; } = null!;
            public BassStreamProvider StreamProvider { get; set; } = null!;  // ⭐ 新增：流提供者
            public int StreamHandle { get; set; }                    // ⭐ 新增：BASS 流句柄
            public bool IsReady { get; set; }                        // ⭐ 新增：流是否就绪
            public DateTime CreateTime { get; set; } = DateTime.UtcNow;
        }

        #endregion

        #region 字段

        private readonly object _lock = new object();
        private readonly NeteaseApiClient _apiClient;
        private readonly Func<SongInfo, QualityLevel, CancellationToken, Task<SongResolveResult>> _resolvePlaybackAsync;
        private readonly HttpClient _httpClient;
        private readonly Dictionary<string, PreloadedSongData> _preloadedData; // 按 SongId 存储
        private CancellationTokenSource? _preloadCts;

        private bool PreferSequentialFull(SongInfo song, long totalSize)
        {
            if (song != null && song.Duration > 0 && totalSize > 0)
            {
                double kbps = (totalSize * 8.0) / song.Duration / 1000.0;
                if (kbps >= 512)
                {
                    return true;
                }
            }

            return totalSize >= 12 * 1024 * 1024;
        }

        #endregion

        #region 构造与析构

        public NextSongPreloader(NeteaseApiClient apiClient, Func<SongInfo, QualityLevel, CancellationToken, Task<SongResolveResult>> resolvePlaybackAsync)
        {
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            _resolvePlaybackAsync = resolvePlaybackAsync ?? throw new ArgumentNullException(nameof(resolvePlaybackAsync));
            _httpClient = Core.Streaming.OptimizedHttpClientFactory.CreateForMainPlayback(TimeSpan.FromSeconds(60));
            _preloadedData = new Dictionary<string, PreloadedSongData>(StringComparer.Ordinal);
        }

        public void Dispose()
        {
            List<PreloadedSongData> snapshot;
            lock (_lock)
            {
                _preloadCts?.Cancel();
                _preloadCts?.Dispose();
                _preloadCts = null;

                snapshot = _preloadedData.Values.ToList();
                _preloadedData.Clear();
            }

            if (snapshot.Count > 0)
            {
                // Do not block the UI thread while releasing native handles during app shutdown.
                _ = Task.Run(() =>
                {
                    foreach (var data in snapshot)
                    {
                        try
                        {
                            if (data.StreamHandle != 0)
                            {
                                BASS_StreamFree(data.StreamHandle);
                            }

                            data.StreamProvider?.Dispose();
                            data.CacheManager?.Dispose();
                        }
                        catch
                        {
                        }
                    }
                });
            }

            _httpClient?.Dispose();
        }

        #endregion

        #region 公共方法

        /// <summary>
        /// 检查单个歌曲的资源可用性（如果未检查过）
        /// </summary>
        /// <returns>true 表示可用，false 表示不可用</returns>
        private async Task<bool> CheckSongAvailabilityAsync(SongInfo song, QualityLevel quality, CancellationToken cancellationToken = default)
        {
            if (song == null || string.IsNullOrWhiteSpace(song.Id))
            {
                return false;
            }

            if (song.IsAvailable.HasValue)
            {
                return song.IsAvailable.Value;
            }

            try
            {
                SongResolveResult resolveResult = await _resolvePlaybackAsync(song, quality, cancellationToken).ConfigureAwait(false);
                if (resolveResult.Status == SongResolveStatus.Success)
                {
                    song.IsAvailable = true;
                    return true;
                }

                if (resolveResult.Status == SongResolveStatus.NotAvailable)
                {
                    song.IsAvailable = false;
                }

                return false;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NextSongPreloader] Unified availability check failed: {song.Name}, {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Start preloading the next track asynchronously.
        /// </summary>
        /// <returns>true if preload succeeded; otherwise false.</returns>
        public async Task<bool> StartPreloadAsync(SongInfo nextSong, QualityLevel quality)
        {
            if (nextSong == null || string.IsNullOrWhiteSpace(nextSong.Id))
            {
                return false;
            }

            // 取消之前的预加载任务
            CancelCurrentPreload();

            lock (_lock)
            {
                _preloadCts = new CancellationTokenSource();
            }

            var cancellationToken = _preloadCts.Token;
            bool notifiedPreload = false;

            try
            {
                DownloadBandwidthCoordinator.Instance.NotifyPrecacheStateChanged(true);
                notifiedPreload = true;
                System.Diagnostics.Debug.WriteLine($"[NextSongPreloader] 开始预加载: {nextSong.Name}");

                // 步骤 1: 使用统一播放解析流程获取 URL
                System.Diagnostics.Debug.WriteLine($"[NextSongPreloader] 使用统一流程解析: {nextSong.Name}");
                SongResolveResult resolveResult = await _resolvePlaybackAsync(nextSong, quality, cancellationToken).ConfigureAwait(false);
                if (resolveResult.Status != SongResolveStatus.Success)
                {
                    System.Diagnostics.Debug.WriteLine($"[NextSongPreloader] URL 解析失败: {nextSong.Name}, 状态: {resolveResult.Status}");
                    return false;
                }
                if (cancellationToken.IsCancellationRequested)
                {
                    return false;
                }
                if (string.IsNullOrEmpty(nextSong.Url))
                {
                    System.Diagnostics.Debug.WriteLine($"[NextSongPreloader] URL 为空，取消预加载: {nextSong.Name}");
                    return false;
                }
                System.Diagnostics.Debug.WriteLine($"[NextSongPreloader] URL 已获取: {nextSong.Url}");
                if (nextSong.IsTrial)
                {
                    System.Diagnostics.Debug.WriteLine($"[NextSongPreloader] 🎵 试听版本: {nextSong.Name}, 片段: {nextSong.TrialStart / 1000}s - {nextSong.TrialEnd / 1000}s");
                }

                // 步骤 2: 创建 SmartCacheManager 并预下载首段
                var cacheManager = new SmartCacheManager(
                    nextSong.Id,
                    nextSong.Url,
                    nextSong.Size,
                    _httpClient,
                    PreferSequentialFull(nextSong, nextSong.Size),
                    nextSong.CustomHeaders);

                // 🎯 预加载场景：只需要 Chunk0，不需要最后块
                bool initialized = await cacheManager.InitializeAsync(cancellationToken, isPreload: true).ConfigureAwait(false);

                if (!initialized)
                {
                    System.Diagnostics.Debug.WriteLine($"[NextSongPreloader] 缓存初始化失败: {nextSong.Name}");
                    cacheManager.Dispose();
                    return false;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    cacheManager.Dispose();
                    return false;
                }

                System.Diagnostics.Debug.WriteLine($"[NextSongPreloader] Chunk 0 已预下载完成");

                // ⭐⭐⭐ 步骤 3: 创建完整的 BASS 流对象（就绪状态）
                BassStreamProvider streamProvider = null;
                int streamHandle = 0;
                bool isReady = false;

                try
                {
                    streamProvider = new BassStreamProvider(cacheManager);
                    streamHandle = streamProvider.CreateStream();

                    if (streamHandle == 0)
                    {
                        System.Diagnostics.Debug.WriteLine($"[NextSongPreloader] BASS 流创建失败: {nextSong.Name}");
                        streamProvider?.Dispose();
                        cacheManager.Dispose();
                        return false;
                    }

                    isReady = true;
                    System.Diagnostics.Debug.WriteLine($"[NextSongPreloader] ✓ BASS 流创建成功，句柄: {streamHandle}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[NextSongPreloader] BASS 流创建异常: {ex.Message}");
                    if (streamHandle != 0)
                    {
                        BASS_StreamFree(streamHandle);
                    }
                    streamProvider?.Dispose();
                    cacheManager.Dispose();
                    return false;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    BASS_StreamFree(streamHandle);
                    streamProvider?.Dispose();
                    cacheManager.Dispose();
                    return false;
                }

                // 步骤 4: 保存预加载数据
                lock (_lock)
                {
                    var preloadedData = new PreloadedSongData
                    {
                        SongId = nextSong.Id,
                        Url = nextSong.Url,
                        Level = nextSong.Level,
                        Size = nextSong.Size,
                        IsTrial = nextSong.IsTrial,
                        TrialStart = nextSong.TrialStart,
                        TrialEnd = nextSong.TrialEnd,
                        IsUnblocked = nextSong.IsUnblocked,
                        UnblockSource = nextSong.UnblockSource ?? string.Empty,
                        CustomHeaders = nextSong.CustomHeaders != null ? new Dictionary<string, string>(nextSong.CustomHeaders, StringComparer.OrdinalIgnoreCase) : null,
                        CacheManager = cacheManager,
                        StreamProvider = streamProvider,
                        StreamHandle = streamHandle,
                        IsReady = isReady,
                        CreateTime = DateTime.UtcNow
                    };

                    _preloadedData[nextSong.Id] = preloadedData;
                }

                System.Diagnostics.Debug.WriteLine($"[NextSongPreloader] ✓✓✓ 预加载完成（含完整流）: {nextSong.Name}, 句柄: {streamHandle}");
                return true;  // 🎯 预加载成功
            }
            catch (OperationCanceledException)
            {
                System.Diagnostics.Debug.WriteLine($"[NextSongPreloader] 预加载被取消: {nextSong.Name}");
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NextSongPreloader] 预加载失败: {nextSong.Name}, 错误: {ex.Message}");
                return false;
            }
            finally
            {
                if (notifiedPreload)
                {
                    DownloadBandwidthCoordinator.Instance.NotifyPrecacheStateChanged(false);
                }
            }
        }

        /// <summary>
        /// 尝试获取预加载的数据
        /// </summary>
        public PreloadedData TryGetPreloadedData(string songId)
        {
            if (string.IsNullOrWhiteSpace(songId))
            {
                return null;
            }

            lock (_lock)
            {
                if (_preloadedData.TryGetValue(songId, out var data))
                {
                    System.Diagnostics.Debug.WriteLine($"[NextSongPreloader] ✓ 命中预加载缓存: {songId}, 流就绪: {data.IsReady}, 句柄: {data.StreamHandle}");

                    // 从字典中移除（一次性使用）
                    _preloadedData.Remove(songId);

                    return new PreloadedData
                    {
                        Url = data.Url,
                        Level = data.Level,
                        Size = data.Size,
                        IsTrial = data.IsTrial,
                        TrialStart = data.TrialStart,
                        TrialEnd = data.TrialEnd,
                        IsUnblocked = data.IsUnblocked,
                        UnblockSource = data.UnblockSource,
                        CustomHeaders = data.CustomHeaders != null ? new Dictionary<string, string>(data.CustomHeaders, StringComparer.OrdinalIgnoreCase) : null,
                        CacheManager = data.CacheManager,
                        StreamProvider = data.StreamProvider,
                        StreamHandle = data.StreamHandle,
                        IsReady = data.IsReady
                    };
                }

                return null;
            }
        }

        /// <summary>
        /// 清理所有预加载数据
        /// </summary>
        public void Clear()
        {
            CancelCurrentPreload();

            lock (_lock)
            {
                foreach (var data in _preloadedData.Values)
                {
                    // ⭐ 释放 BASS 流资源
                    if (data.StreamHandle != 0)
                    {
                        BASS_StreamFree(data.StreamHandle);
                        System.Diagnostics.Debug.WriteLine($"[NextSongPreloader] 释放流句柄: {data.StreamHandle}");
                    }
                    data.StreamProvider?.Dispose();
                    data.CacheManager?.Dispose();
                }

                _preloadedData.Clear();
            }

            System.Diagnostics.Debug.WriteLine("[NextSongPreloader] 已清理所有预加载数据");
        }

        /// <summary>
        /// 清理过期数据（只保留当前歌曲和下一首的预加载数据）
        /// </summary>
        public void CleanupStaleData(string currentSongId, string nextSongId)
        {
            lock (_lock)
            {
                var toRemove = _preloadedData.Keys
                    .Where(id => id != currentSongId && id != nextSongId)
                    .ToList();

                foreach (var id in toRemove)
                {
                    if (_preloadedData.TryGetValue(id, out var data))
                    {
                        // ⭐ 释放 BASS 流资源
                        if (data.StreamHandle != 0)
                        {
                            BASS_StreamFree(data.StreamHandle);
                            System.Diagnostics.Debug.WriteLine($"[NextSongPreloader] 释放过期流句柄: {data.StreamHandle} (ID: {id})");
                        }
                        data.StreamProvider?.Dispose();
                        data.CacheManager?.Dispose();
                        _preloadedData.Remove(id);
                    }
                }

                if (toRemove.Count > 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[NextSongPreloader] 清理了 {toRemove.Count} 个过期数据");
                }
            }
        }

        #endregion

        #region 私有方法

        private void CancelCurrentPreload()
        {
            lock (_lock)
            {
                _preloadCts?.Cancel();
                _preloadCts?.Dispose();
                _preloadCts = null;
            }

            DownloadBandwidthCoordinator.Instance.NotifyPrecacheStateChanged(false);
        }

        #endregion
    }

    /// <summary>
    /// 预加载数据（返回给调用者）
    /// </summary>
    public class PreloadedData
    {
        public string Url { get; set; } = string.Empty;
        public string Level { get; set; } = string.Empty;
        public long Size { get; set; }
        public bool IsTrial { get; set; }
        public long TrialStart { get; set; }
        public long TrialEnd { get; set; }
        public bool IsUnblocked { get; set; }
        public string UnblockSource { get; set; } = string.Empty;
        public Dictionary<string, string>? CustomHeaders { get; set; }
        public SmartCacheManager CacheManager { get; set; } = null!;

        // ⭐ 新增：完整的流对象信息
        public BassStreamProvider StreamProvider { get; set; } = null!;
        public int StreamHandle { get; set; }
        public bool IsReady { get; set; }

        // ⭐ 新增：歌词数据
        public YTPlayer.Core.Lyrics.LyricsData LyricsData { get; set; } = null!;
    }
}

#pragma warning restore CS8600, CS8601, CS8602, CS8603, CS8604, CS8625
