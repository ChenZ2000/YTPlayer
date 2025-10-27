using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using YTPlayer.Core.Playback.Cache;
using YTPlayer.Core.Streaming;
using YTPlayer.Models;

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
            public string SongId { get; set; }
            public string Url { get; set; }
            public string Level { get; set; }
            public long Size { get; set; }
            public SmartCacheManager CacheManager { get; set; }
            public BassStreamProvider StreamProvider { get; set; }  // ⭐ 新增：流提供者
            public int StreamHandle { get; set; }                    // ⭐ 新增：BASS 流句柄
            public bool IsReady { get; set; }                        // ⭐ 新增：流是否就绪
            public DateTime CreateTime { get; set; }
        }

        #endregion

        #region 字段

        private readonly object _lock = new object();
        private readonly NeteaseApiClient _apiClient;
        private readonly HttpClient _httpClient;
        private readonly Dictionary<string, PreloadedSongData> _preloadedData; // 按 SongId 存储
        private CancellationTokenSource _preloadCts;

        #endregion

        #region 构造与析构

        public NextSongPreloader(NeteaseApiClient apiClient)
        {
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(15)
            };
            _preloadedData = new Dictionary<string, PreloadedSongData>(StringComparer.Ordinal);
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _preloadCts?.Cancel();
                _preloadCts?.Dispose();
                _preloadCts = null;

                foreach (var data in _preloadedData.Values)
                {
                    // ⭐ 释放 BASS 流资源
                    if (data.StreamHandle != 0)
                    {
                        BASS_StreamFree(data.StreamHandle);
                    }
                    data.StreamProvider?.Dispose();
                    data.CacheManager?.Dispose();
                }

                _preloadedData.Clear();
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

            // 如果已经检查过，直接返回结果
            if (song.IsAvailable != null)
            {
                return song.IsAvailable.Value;
            }

            try
            {
                System.Diagnostics.Debug.WriteLine($"[NextSongPreloader] 🔍 检查歌曲可用性: {song.Name}");

                // 调用 GetSongUrl API 检查
                var urlResult = await _apiClient.GetSongUrlAsync(
                    new[] { song.Id },
                    quality,
                    skipAvailabilityCheck: false).ConfigureAwait(false);

                if (cancellationToken.IsCancellationRequested) return false;

                // 检查 URL 是否有效
                if (urlResult != null &&
                    urlResult.TryGetValue(song.Id, out var songUrl) &&
                    !string.IsNullOrEmpty(songUrl?.Url))
                {
                    // 歌曲可用
                    song.IsAvailable = true;
                    song.Url = songUrl.Url;
                    song.Level = songUrl.Level;
                    song.Size = songUrl.Size;
                    System.Diagnostics.Debug.WriteLine($"[NextSongPreloader] ✓ 歌曲可用: {song.Name}");
                    return true;
                }
                else
                {
                    // 歌曲不可用
                    song.IsAvailable = false;
                    System.Diagnostics.Debug.WriteLine($"[NextSongPreloader] ✗ 歌曲不可用: {song.Name}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NextSongPreloader] 检查可用性异常: {song.Name}, {ex.Message}");
                song.IsAvailable = false;
                return false;
            }
        }

        /// <summary>
        /// 开始预加载下一首歌曲（异步非阻塞）
        /// </summary>
        /// <returns>true 表示预加载成功或正在进行，false 表示歌曲不可用需要跳过</returns>
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

            try
            {
                System.Diagnostics.Debug.WriteLine($"[NextSongPreloader] 开始预加载: {nextSong.Name}");

                // 步骤 1: 获取 URL（如果还没有）
                if (string.IsNullOrEmpty(nextSong.Url))
                {
                    bool shouldSkipCheck = nextSong.IsAvailable == true;
                    System.Diagnostics.Debug.WriteLine($"[NextSongPreloader] 获取 URL: {nextSong.Name}, IsAvailable={nextSong.IsAvailable}, skipCheck={shouldSkipCheck}");

                    var urlResult = await _apiClient.GetSongUrlAsync(
                        new[] { nextSong.Id },
                        quality,
                        skipAvailabilityCheck: shouldSkipCheck).ConfigureAwait(false);

                    if (cancellationToken.IsCancellationRequested) return false;

                    if (urlResult == null ||
                        !urlResult.TryGetValue(nextSong.Id, out var songUrl) ||
                        string.IsNullOrEmpty(songUrl?.Url))
                    {
                        // 🎯 标记歌曲为不可用，下次预加载会自动跳过
                        nextSong.IsAvailable = false;
                        System.Diagnostics.Debug.WriteLine($"[NextSongPreloader] 🎯 无法获取 URL，标记为不可用: {nextSong.Name}");
                        return false;
                    }

                    // ✅ 成功获取 URL，标记为可用并缓存信息
                    nextSong.IsAvailable = true;
                    nextSong.Url = songUrl.Url;
                    nextSong.Level = songUrl.Level;
                    nextSong.Size = songUrl.Size;
                    System.Diagnostics.Debug.WriteLine($"[NextSongPreloader] ✓ URL 已获取并标记为可用: {nextSong.Name}");
                }

                System.Diagnostics.Debug.WriteLine($"[NextSongPreloader] URL 已获取: {nextSong.Url}");

                // 🎯🎯🎯 步骤 2a: 预下载最后块到缓存（后台任务，不阻塞）
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Cache.LastChunkCacheManager.Instance
                            .PreDownloadLastChunksAsync(
                                nextSong.Id,
                                nextSong.Url,
                                nextSong.Size,
                                _httpClient,
                                cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[NextSongPreloader] 预下载最后块失败: {ex.Message}");
                    }
                }, cancellationToken);

                // 步骤 2b: 创建 SmartCacheManager 并预下载 Chunk 0
                var cacheManager = new SmartCacheManager(
                    nextSong.Id,
                    nextSong.Url,
                    nextSong.Size,
                    _httpClient);

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
        }

        #endregion
    }

    /// <summary>
    /// 预加载数据（返回给调用者）
    /// </summary>
    public class PreloadedData
    {
        public string Url { get; set; }
        public string Level { get; set; }
        public long Size { get; set; }
        public SmartCacheManager CacheManager { get; set; }

        // ⭐ 新增：完整的流对象信息
        public BassStreamProvider StreamProvider { get; set; }
        public int StreamHandle { get; set; }
        public bool IsReady { get; set; }
    }
}
