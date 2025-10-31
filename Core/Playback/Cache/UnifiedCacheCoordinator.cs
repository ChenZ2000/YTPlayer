using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using YTPlayer.Core.Data;
using YTPlayer.Models.Playback;

namespace YTPlayer.Core.Playback.Cache
{
    /// <summary>
    /// 统一缓存协调器
    /// 核心组件，协调动态热点管理、播放感知调度和数据提供
    /// </summary>
    public sealed class UnifiedCacheCoordinator : IDisposable
    {
        private SongDataModel _currentSong;
        private AudioQualityContainer _activeContainer;
        private string _currentQuality;

        private readonly DynamicHotspotManager _hotspotManager;
        private readonly PlaybackAwareScheduler _scheduler;
        private readonly HttpClient _httpClient;

        private bool _isAttached;
        private readonly object _lock = new object();

        public UnifiedCacheCoordinator(HttpClient httpClient)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _hotspotManager = new DynamicHotspotManager();
            _scheduler = new PlaybackAwareScheduler(_hotspotManager, _httpClient);
        }

        /// <summary>
        /// 附加到播放会话
        /// </summary>
        /// <param name="songId">歌曲 ID</param>
        /// <param name="quality">音质标识</param>
        public void AttachToPlayback(string songId, string quality)
        {
            lock (_lock)
            {
                if (_isAttached)
                {
                    Detach();
                }

                _currentSong = SongDataStore.Instance.GetOrCreate(songId);
                _activeContainer = _currentSong.GetOrCreateQualityContainer(quality);
                _currentQuality = quality;

                // 订阅播放位置变化
                _currentSong.PlaybackPosition.Subscribe(position =>
                {
                    _hotspotManager.UpdatePlaybackPosition(position);
                });

                // 启动调度器
                _scheduler.Start(_activeContainer);

                _isAttached = true;

                Utils.DebugLogger.Log(Utils.LogLevel.Info, "UnifiedCacheCoordinator",
                    $"Attached to playback: {songId}, quality: {quality}");
            }
        }

        /// <summary>
        /// 分离当前播放会话
        /// </summary>
        public void Detach()
        {
            lock (_lock)
            {
                if (!_isAttached)
                    return;

                _scheduler.Stop();

                _currentSong = null;
                _activeContainer = null;
                _currentQuality = null;
                _isAttached = false;

                Utils.DebugLogger.Log(Utils.LogLevel.Info, "UnifiedCacheCoordinator",
                    "Detached from playback");
            }
        }

        /// <summary>
        /// 响应 Seek 操作（立即调整热点）
        /// </summary>
        /// <param name="targetPosition">目标位置（字节）</param>
        public void OnSeekRequested(long targetPosition)
        {
            lock (_lock)
            {
                if (!_isAttached)
                    return;

                _hotspotManager.ShiftHotspot(targetPosition);
                _currentSong.PlaybackPosition.Value = targetPosition;

                Utils.DebugLogger.Log(Utils.LogLevel.Debug, "UnifiedCacheCoordinator",
                    $"Seek requested to position: {targetPosition}");
            }
        }

        /// <summary>
        /// 读取块数据（供 BASS 使用）
        /// </summary>
        /// <param name="chunkIndex">块索引</param>
        /// <param name="timeout">超时时间</param>
        /// <param name="token">取消令牌</param>
        /// <returns>块数据，如果失败返回 null</returns>
        public async Task<byte[]> ReadChunkAsync(int chunkIndex, TimeSpan timeout, CancellationToken token)
        {
            lock (_lock)
            {
                if (!_isAttached || _activeContainer == null)
                    return null;
            }

            // 尝试从缓存读取
            if (_activeContainer.TryGetChunkData(chunkIndex, out var cachedData))
            {
                return cachedData;
            }

            // 缓存未命中，紧急请求
            Utils.DebugLogger.Log(Utils.LogLevel.Warning, "UnifiedCacheCoordinator",
                $"Cache miss for chunk {chunkIndex}, requesting urgently");

            using (var timeoutCts = new CancellationTokenSource(timeout))
            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token))
            {
                try
                {
                    bool success = await _scheduler.RequestUrgentChunkAsync(chunkIndex, linkedCts.Token);

                    if (success && _activeContainer.TryGetChunkData(chunkIndex, out var urgentData))
                    {
                        return urgentData;
                    }

                    return null;
                }
                catch (OperationCanceledException)
                {
                    Utils.DebugLogger.Log(Utils.LogLevel.Error, "UnifiedCacheCoordinator",
                        $"Urgent chunk request timeout for chunk {chunkIndex}");
                    return null;
                }
            }
        }

        /// <summary>
        /// 同步读取块数据（带超时）
        /// </summary>
        public byte[] ReadChunk(int chunkIndex)
        {
            lock (_lock)
            {
                if (!_isAttached || _activeContainer == null)
                    return null;
            }

            // 尝试从缓存读取
            if (_activeContainer.TryGetChunkData(chunkIndex, out var cachedData))
            {
                return cachedData;
            }

            // 缓存未命中，返回 null（避免阻塞）
            Utils.DebugLogger.Log(Utils.LogLevel.Warning, "UnifiedCacheCoordinator",
                $"Sync cache miss for chunk {chunkIndex}, returning null");

            return null;
        }

        /// <summary>
        /// 获取当前容器（用于外部查询）
        /// </summary>
        public AudioQualityContainer GetActiveContainer()
        {
            lock (_lock)
            {
                return _activeContainer;
            }
        }

        /// <summary>
        /// 获取缓存进度百分比
        /// </summary>
        public double GetCacheProgress()
        {
            lock (_lock)
            {
                return _activeContainer?.GetCacheProgress() ?? 0;
            }
        }

        public void Dispose()
        {
            Detach();
        }
    }
}
