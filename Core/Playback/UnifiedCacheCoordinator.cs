using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using YTPlayer.Core.Playback.Strategy;
using YTPlayer.Models;

namespace YTPlayer.Core.Playback
{
    /// <summary>
    /// 统一缓存协调器 - 管理所有预加载和缓存
    ///
    /// 核心职责：
    /// 1. 统一管理多个缓存实例（当前播放 + 多个预加载）
    /// 2. 协调资源分配（带宽、内存、优先级）
    /// 3. 监听队列变化，自动调整预加载策略
    /// 4. 支持多音质并存
    /// 5. 提供生命周期管理
    /// </summary>
    public class UnifiedCacheCoordinator : IDisposable
    {
        #region Fields

        private readonly object _lock = new object();
        private readonly BassAudioEngine _audioEngine;
        private readonly PlaybackQueueManager _queueManager;
        private readonly NextSongPreloader _preloader;

        // 缓存条目管理
        private readonly Dictionary<string, CacheEntry> _cacheEntries;
        private readonly int _maxCachedSongs;
        private readonly int _maxQualitiesPerSong;

        // 配置
        private readonly bool _enableMultiQualityPreload;
        private readonly List<string> _fallbackQualities;
        private readonly int _randomModeCandidateCount;

        // 状态
        private string _currentPlayMode;
        private string _currentQuality;
        private IPreloadStrategy _currentStrategy;

        // 清理任务
        private CancellationTokenSource _cleanupCts;
        private Task _cleanupTask;

        #endregion

        #region Constructor

        public UnifiedCacheCoordinator(
            BassAudioEngine audioEngine,
            PlaybackQueueManager queueManager,
            NextSongPreloader preloader,
            ConfigModel config = null)
        {
            _audioEngine = audioEngine ?? throw new ArgumentNullException(nameof(audioEngine));
            _queueManager = queueManager ?? throw new ArgumentNullException(nameof(queueManager));
            _preloader = preloader ?? throw new ArgumentNullException(nameof(preloader));

            _cacheEntries = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);

            // 从配置读取参数（如果提供）
            _maxCachedSongs = config?.MaxCachedSongs ?? 5;
            _maxQualitiesPerSong = config?.MaxQualitiesPerSong ?? 3;
            _enableMultiQualityPreload = config?.EnableMultiQualityPreload ?? true;
            _fallbackQualities = config?.FallbackQualities ?? new List<string> { "exhigh", "standard" };
            _randomModeCandidateCount = config?.RandomModeCandidateCount ?? 3;

            // 订阅事件
            SubscribeToEvents();

            // 启动后台清理任务
            StartCleanupTask();

            System.Diagnostics.Debug.WriteLine("[UnifiedCacheCoordinator] 已初始化");
        }

        #endregion

        #region Event Subscription

        private void SubscribeToEvents()
        {
            // 监听播放事件
            if (_audioEngine != null)
            {
                _audioEngine.PlaybackStarted += OnPlaybackStarted;
                _audioEngine.PlaybackEnded += OnPlaybackEnded;
                _audioEngine.StateChanged += OnPlaybackStateChanged;
            }

            // 注意：队列管理器目前没有 QueueChanged 事件
            // 这是未来的改进点，需要在 PlaybackQueueManager 中添加
            // _queueManager.QueueChanged += OnQueueChanged;
        }

        private void UnsubscribeFromEvents()
        {
            if (_audioEngine != null)
            {
                _audioEngine.PlaybackStarted -= OnPlaybackStarted;
                _audioEngine.PlaybackEnded -= OnPlaybackEnded;
                _audioEngine.StateChanged -= OnPlaybackStateChanged;
            }
        }

        #endregion

        #region Event Handlers

        private void OnPlaybackStarted(object sender, SongInfo song)
        {
            if (song == null) return;

            System.Diagnostics.Debug.WriteLine($"[UnifiedCacheCoordinator] OnPlaybackStarted: {song.Name}");

            // 提升当前歌曲的优先级
            PromoteToCritical(song.Id);

            // 触发预加载刷新
            _ = RefreshPreloadAsync();
        }

        private void OnPlaybackEnded(object sender, SongInfo song)
        {
            if (song == null) return;

            System.Diagnostics.Debug.WriteLine($"[UnifiedCacheCoordinator] OnPlaybackEnded: {song.Name}");

            // 降低已播放歌曲的优先级
            DemoteToLow(song.Id);
        }

        private void OnPlaybackStateChanged(object sender, PlaybackState newState)
        {
            System.Diagnostics.Debug.WriteLine($"[UnifiedCacheCoordinator] OnPlaybackStateChanged: {newState}");

            // 根据状态变化调整预加载策略
            if (newState == PlaybackState.Stopped || newState == PlaybackState.Error)
            {
                // 清理所有非Critical的预加载
                CleanupNonCriticalPreloads();
            }
        }

        #endregion

        #region Public API

        /// <summary>
        /// 刷新预加载（主入口）
        /// </summary>
        public async Task RefreshPreloadAsync()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("[UnifiedCacheCoordinator] RefreshPreloadAsync 开始");

                // 获取播放上下文
                var context = GetPlaybackContext();
                if (context == null)
                {
                    System.Diagnostics.Debug.WriteLine("[UnifiedCacheCoordinator] 无法获取播放上下文");
                    return;
                }

                // 选择策略
                _currentStrategy = SelectStrategy(context.PlayMode);
                System.Diagnostics.Debug.WriteLine($"[UnifiedCacheCoordinator] 使用策略: {_currentStrategy.Name}");

                // 获取预加载候选
                int maxCandidates = GetMaxCandidatesForMode(context.PlayMode);
                var candidates = await _currentStrategy.GetPreloadCandidatesAsync(context, maxCandidates);

                if (candidates == null || candidates.Count == 0)
                {
                    System.Diagnostics.Debug.WriteLine("[UnifiedCacheCoordinator] 无预加载候选");
                    return;
                }

                System.Diagnostics.Debug.WriteLine($"[UnifiedCacheCoordinator] 找到 {candidates.Count} 个候选");

                // 执行预加载
                await ExecutePreloadAsync(candidates);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UnifiedCacheCoordinator] RefreshPreloadAsync 失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 音质变化通知
        /// </summary>
        public void OnQualityChanged(string newQuality)
        {
            _currentQuality = newQuality;
            System.Diagnostics.Debug.WriteLine($"[UnifiedCacheCoordinator] OnQualityChanged: {newQuality}");

            // 触发预加载刷新（使用新音质）
            _ = RefreshPreloadAsync();
        }

        /// <summary>
        /// 播放模式变化通知
        /// </summary>
        public void OnPlayModeChanged(string newMode)
        {
            _currentPlayMode = newMode;
            System.Diagnostics.Debug.WriteLine($"[UnifiedCacheCoordinator] OnPlayModeChanged: {newMode}");

            // 清理不再需要的预加载
            CleanupStalePreloads();

            // 根据新模式重新预加载
            _ = RefreshPreloadAsync();
        }

        /// <summary>
        /// 队列变化通知（需要在 PlaybackQueueManager 中添加事件支持）
        /// </summary>
        public void OnQueueChanged(string newQueueSource)
        {
            System.Diagnostics.Debug.WriteLine($"[UnifiedCacheCoordinator] OnQueueChanged: {newQueueSource}");

            // 标记所有预加载为 Stale（除了 Critical）
            MarkAllPreloadsStale();

            // 根据新队列重新预加载
            _ = RefreshPreloadAsync();
        }

        #endregion

        #region Cache Management

        /// <summary>
        /// 获取或创建缓存条目
        /// </summary>
        private CacheEntry GetOrCreateCacheEntry(SongInfo song)
        {
            lock (_lock)
            {
                if (!_cacheEntries.TryGetValue(song.Id, out var entry))
                {
                    entry = new CacheEntry(song.Id, song);
                    _cacheEntries[song.Id] = entry;
                    System.Diagnostics.Debug.WriteLine($"[UnifiedCacheCoordinator] 创建缓存条目: {song.Name}");
                }

                entry.AddRef();
                return entry;
            }
        }

        /// <summary>
        /// 提升为Critical优先级（当前播放）
        /// </summary>
        private void PromoteToCritical(string songId)
        {
            lock (_lock)
            {
                if (_cacheEntries.TryGetValue(songId, out var entry))
                {
                    entry.Priority = CachePriority.Critical;
                    entry.Status = CacheStatus.Playing;
                    System.Diagnostics.Debug.WriteLine($"[UnifiedCacheCoordinator] 提升为Critical: {entry.Song.Name}");
                }
            }
        }

        /// <summary>
        /// 降级为Low优先级
        /// </summary>
        private void DemoteToLow(string songId)
        {
            lock (_lock)
            {
                if (_cacheEntries.TryGetValue(songId, out var entry))
                {
                    entry.Priority = CachePriority.Low;
                    entry.Release();
                    System.Diagnostics.Debug.WriteLine($"[UnifiedCacheCoordinator] 降级为Low: {entry.Song.Name}");
                }
            }
        }

        /// <summary>
        /// 清理所有非Critical的预加载
        /// </summary>
        private void CleanupNonCriticalPreloads()
        {
            lock (_lock)
            {
                foreach (var entry in _cacheEntries.Values.ToList())
                {
                    if (entry.Priority != CachePriority.Critical)
                    {
                        entry.Status = CacheStatus.Stale;
                    }
                }
            }
        }

        /// <summary>
        /// 标记所有预加载为Stale
        /// </summary>
        private void MarkAllPreloadsStale()
        {
            lock (_lock)
            {
                foreach (var entry in _cacheEntries.Values)
                {
                    if (entry.Priority != CachePriority.Critical)
                    {
                        entry.Status = CacheStatus.Stale;
                        System.Diagnostics.Debug.WriteLine($"[UnifiedCacheCoordinator] 标记为Stale: {entry.Song.Name}");
                    }
                }
            }
        }

        /// <summary>
        /// 清理Stale的预加载
        /// </summary>
        private void CleanupStalePreloads()
        {
            lock (_lock)
            {
                var toRemove = _cacheEntries.Values
                    .Where(e => e.Status == CacheStatus.Stale && e.RefCount == 0)
                    .ToList();

                foreach (var entry in toRemove)
                {
                    System.Diagnostics.Debug.WriteLine($"[UnifiedCacheCoordinator] 清理Stale缓存: {entry.Song.Name}");
                    entry.Dispose();
                    _cacheEntries.Remove(entry.SongId);
                }
            }
        }

        #endregion

        #region Strategy

        /// <summary>
        /// 选择预加载策略
        /// </summary>
        private IPreloadStrategy SelectStrategy(string playMode)
        {
            return playMode switch
            {
                "随机播放" => new RandomPreloadStrategy(),
                "单曲循环" => new LoopOnePreloadStrategy(),
                "列表循环" => new LoopPreloadStrategy(),
                "顺序播放" => new SequentialPreloadStrategy(),
                _ => new SequentialPreloadStrategy()  // 默认使用顺序策略
            };
        }

        /// <summary>
        /// 获取最大候选数量
        /// </summary>
        private int GetMaxCandidatesForMode(string playMode)
        {
            return playMode switch
            {
                "随机播放" => _randomModeCandidateCount,
                "单曲循环" => 0,  // 不预加载
                _ => 2  // 顺序播放、列表循环：预加载1-2首
            };
        }

        /// <summary>
        /// 执行预加载
        /// </summary>
        private async Task ExecutePreloadAsync(List<PreloadCandidate> candidates)
        {
            foreach (var candidate in candidates)
            {
                try
                {
                    var entry = GetOrCreateCacheEntry(candidate.Song);
                    entry.Priority = candidate.Priority >= 80 ? CachePriority.High :
                                    candidate.Priority >= 50 ? CachePriority.Medium :
                                    CachePriority.Low;

                    // 调用现有的预加载器
                    // TODO: 集成多音质预加载
                    // await PreloadMultipleQualitiesAsync(entry, candidate);

                    System.Diagnostics.Debug.WriteLine($"[UnifiedCacheCoordinator] 预加载: {candidate}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[UnifiedCacheCoordinator] 预加载失败: {ex.Message}");
                }
            }
        }

        #endregion

        #region Helper

        /// <summary>
        /// 获取播放上下文
        /// </summary>
        private PlaybackContext GetPlaybackContext()
        {
            try
            {
                var currentSong = _audioEngine?.CurrentSong;
                if (currentSong == null)
                    return null;

                return new PlaybackContext
                {
                    QueueManager = _queueManager,
                    PlayMode = _currentPlayMode ?? "顺序播放",
                    CurrentSong = currentSong,
                    CurrentQueueIndex = _queueManager.CurrentQueueIndex,
                    CurrentQueueSource = _queueManager.QueueSource
                };
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region Cleanup Task

        /// <summary>
        /// 启动后台清理任务
        /// </summary>
        private void StartCleanupTask()
        {
            _cleanupCts = new CancellationTokenSource();
            _cleanupTask = Task.Run(async () =>
            {
                while (!_cleanupCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(60000, _cleanupCts.Token);  // 每60秒清理一次
                        CleanupStalePreloads();
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                }
            });
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            UnsubscribeFromEvents();

            // 🔧 修复：先取消清理任务，然后等待其完成
            _cleanupCts?.Cancel();

            // 等待清理任务完成（避免在 Dispose 期间后台任务仍在访问资源）
            try
            {
                _cleanupTask?.Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UnifiedCacheCoordinator] 等待清理任务完成时异常: {ex.Message}");
            }

            _cleanupCts?.Dispose();

            lock (_lock)
            {
                foreach (var entry in _cacheEntries.Values)
                {
                    try
                    {
                        entry.Dispose();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[UnifiedCacheCoordinator] 释放缓存条目时异常: {ex.Message}");
                    }
                }
                _cacheEntries.Clear();
            }

            System.Diagnostics.Debug.WriteLine("[UnifiedCacheCoordinator] 已释放");
        }

        #endregion
    }
}
