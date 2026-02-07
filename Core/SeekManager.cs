using System;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using YTPlayer.Core.Playback.Cache;
using YTPlayer.Core.Playback;

namespace YTPlayer.Core
{
    /// <summary>
    /// 新一代 Seek 管理器 - 丢弃式非阻塞模式
    /// 核心思想：新命令覆盖旧命令，50ms 快速响应，不暂停播放
    /// </summary>
    public class SeekManager : IDisposable
    {
        #region 字段

        private readonly BassAudioEngine _audioEngine;
        private readonly object _seekLock = new object();

        // 缓存层引用（如果使用缓存流）
        private SmartCacheManager? _cacheManager = null;
        private bool _isUsingCacheStream = false;

        // 丢弃式 Seek 机制
        private double _latestSeekPosition = -1;  // 最新的目标位置
        private double _latestSeekOriginPosition = -1; // 最新Seek请求时的播放位置
        private bool _latestSeekIsPreview = false;   // 是否预览（scrub）请求
        private bool _hasNewSeekRequest = false;   // 是否有新的 Seek 请求
        private bool _isExecutingSeek = false;     // 是否正在执行 Seek
        private bool _executingIsPreview = false;  // 当前执行的是否为预览
        private double _currentExecutingTarget = -1; // 正在执行的目标位置（避免重复触发）
        private CancellationTokenSource? _currentSeekCts = null;  // 当前 Seek 操作的取消令牌
        private CancellationTokenSource? _deferredSeekCts = null; // 延迟 Seek 等待的取消令牌
        private bool _lastSeekSuccess = true;       // 最近一次 Seek 执行结果，用于 FinishSeek 事件

        private long _pendingSeekVersion = 0;
        private PendingSeekContext? _pendingSeekContext = null;
        private bool _hasPendingSeekContext = false;
        private long _seekRequestVersion = 0;
        private long _latestSeekVersion = 0;
        private long _currentExecutingVersion = 0;

        // 快速定时器（50ms）
        private Timer? _seekTimer;
        private const int SEEK_INTERVAL_MS = 50;  // 50ms 一次，快速响应

        // 远距离跳转等待超时（60 秒，覆盖更多网络慢的情况）
        // 缓存等待超时：缩短，超时后立即降级为“直接跳 + 后台缓冲”
        private const int SEEK_CACHE_WAIT_TIMEOUT_MS = 12000;
        private const int PREVIEW_CACHE_WAIT_TIMEOUT_MS = 1200; // 预览拖动用更短超时
        private const int SEEK_DEFERRED_WAIT_TIMEOUT_MS = 30000;
        private const double NATURAL_PASS_TOLERANCE_SECONDS = 0.35;
        private const int NATURAL_PROGRESS_POLL_INTERVAL_MS = 200;

        // 状态监控
        private int _consecutiveFailures = 0;
        private const int MAX_CONSECUTIVE_FAILURES = 3;

        private int _disposed = 0;

        private bool IsDisposed => System.Threading.Volatile.Read(ref _disposed) == 1;

        #endregion

        #region 事件

        /// <summary>
        /// Seek 完成事件（仅在用户停止拖动后触发）
        /// </summary>
        public event EventHandler<bool>? SeekCompleted; // bool = 是否成功

        public event EventHandler<SeekRequestEventArgs>? SeekRequested;

        public event EventHandler<SeekExecutionEventArgs>? SeekExecuted;

        #endregion

        public sealed class SeekRequestEventArgs : EventArgs
        {
            public double TargetSeconds { get; }
            public double OriginSeconds { get; }
            public bool IsPreview { get; }
            public long Version { get; }

            public SeekRequestEventArgs(double targetSeconds, double originSeconds, bool isPreview, long version)
            {
                TargetSeconds = targetSeconds;
                OriginSeconds = originSeconds;
                IsPreview = isPreview;
                Version = version;
            }
        }

        public sealed class SeekExecutionEventArgs : EventArgs
        {
            public double TargetSeconds { get; }
            public bool Success { get; }
            public bool IsPreview { get; }
            public long Version { get; }

            public SeekExecutionEventArgs(double targetSeconds, bool success, bool isPreview, long version)
            {
                TargetSeconds = targetSeconds;
                Success = success;
                IsPreview = isPreview;
                Version = version;
            }
        }

        private readonly struct PendingSeekContext
        {
            public double OriginSeconds { get; }
            public double TargetSeconds { get; }
            public bool WasPlaying { get; }
            public bool WasPaused { get; }
            public PlaybackState PlaybackState { get; }
            public long SeekVersion { get; }

            public PendingSeekContext(
                double originSeconds,
                double targetSeconds,
                bool wasPlaying,
                bool wasPaused,
                PlaybackState playbackState,
                long seekVersion)
            {
                OriginSeconds = originSeconds;
                TargetSeconds = targetSeconds;
                WasPlaying = wasPlaying;
                WasPaused = wasPaused;
                PlaybackState = playbackState;
                SeekVersion = seekVersion;
            }
        }

        #region 构造函数

        public SeekManager(BassAudioEngine audioEngine)
        {
            _audioEngine = audioEngine ?? throw new ArgumentNullException(nameof(audioEngine));

            // 创建 50ms 快速定时器（非阻塞模式）
            _seekTimer = new Timer(ExecuteSeekTimerCallback, null, Timeout.Infinite, Timeout.Infinite);
        }

        #endregion

        #region 流模式管理

        /// <summary>
        /// 设置为缓存流模式（支持任意位置跳转）
        /// </summary>
        public void SetCacheStream(SmartCacheManager cacheManager)
        {
            if (IsDisposed)
            {
                return;
            }

            lock (_seekLock)
            {
                _cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
                _isUsingCacheStream = true;
                Debug.WriteLine("[SeekManager] ✓ 切换到缓存流模式 - 支持任意位置跳转");
            }
        }

        /// <summary>
        /// 设置为直接流模式（仅支持已下载位置跳转）
        /// </summary>
        public void SetDirectStream()
        {
            if (IsDisposed)
            {
                return;
            }

            lock (_seekLock)
            {
                _cacheManager = null;
                _isUsingCacheStream = false;
                Debug.WriteLine("[SeekManager] ⚠️ 切换到直接流模式 - 只能跳转到已下载位置");
            }
        }

        /// <summary>
        /// 清除流引用（停止播放时调用）
        /// </summary>
        public void ClearStream()
        {
            if (IsDisposed)
            {
                return;
            }

            lock (_seekLock)
            {
                _cacheManager = null;
                _isUsingCacheStream = false;
                Debug.WriteLine("[SeekManager] 清除流引用");
            }
        }

        #endregion

        #region 公共方法

        /// <summary>
        /// 请求 Seek 到指定位置（丢弃式，新命令覆盖旧命令）
        /// </summary>
        /// <param name="targetSeconds">目标位置（秒）</param>
        public void RequestSeek(double targetSeconds, bool isPreview = false)
        {
            if (IsDisposed)
            {
                return;
            }

            if (_audioEngine == null || !_audioEngine.IsInitialized)
            {
                Debug.WriteLine("[SeekManager] 音频引擎未初始化，忽略 Seek 请求");
                return;
            }

            // 避免跳到曲终后导致 BASS 拒绝定位：将目标时间钳制在曲长-50ms 以内
            double duration = _audioEngine.GetDuration();
            if (duration > 0)
            {
                double maxTarget = Math.Max(0, duration - 0.05);
                targetSeconds = Math.Min(targetSeconds, maxTarget);
            }

            double originSeconds;
            long requestVersion;
            lock (_seekLock)
            {
                // 保存最新的目标位置（丢弃旧的）
                _latestSeekPosition = targetSeconds;
                _latestSeekOriginPosition = _audioEngine?.GetPosition() ?? -1;
                _latestSeekIsPreview = isPreview;
                _hasNewSeekRequest = true;
                _seekRequestVersion++;
                _latestSeekVersion = _seekRequestVersion;
                requestVersion = _latestSeekVersion;
                originSeconds = _latestSeekOriginPosition;

                // 如果定时器未启动，启动它
                if (_seekTimer != null)
                {
                    _seekTimer.Change(SEEK_INTERVAL_MS, Timeout.Infinite);
                }

                Debug.WriteLine($"[SeekManager] Seek 请求: {targetSeconds:F1}s（50ms 后执行，新命令覆盖旧命令）");
            }

            try
            {
                SeekRequested?.Invoke(this, new SeekRequestEventArgs(targetSeconds, originSeconds, isPreview, requestVersion));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SeekManager] SeekRequested 处理异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 完成 Seek 序列（用户停止拖动时调用）
        /// </summary>
        public void FinishSeek()
        {
            if (IsDisposed)
            {
                return;
            }

            lock (_seekLock)
            {
                // 停止定时器
                _seekTimer?.Change(Timeout.Infinite, Timeout.Infinite);

                // 清除状态（但不取消当前正在执行的 seek，让它完成）
                _hasNewSeekRequest = false;
                _latestSeekPosition = -1;
                _latestSeekOriginPosition = -1;

                Debug.WriteLine("[SeekManager] Seek 序列结束（最后一次 seek 将继续完成）");
            }

            // 触发完成事件，带上最近一次执行结果
            SeekCompleted?.Invoke(this, _lastSeekSuccess);
        }

        /// <summary>
        /// 立即取消所有待处理的 Seek 操作
        /// </summary>
        public void CancelPendingSeeks()
        {
            if (IsDisposed)
            {
                return;
            }

            lock (_seekLock)
            {
                // 停止定时器
                _seekTimer?.Change(Timeout.Infinite, Timeout.Infinite);

                // 取消当前的 Seek 操作
                _currentSeekCts?.Cancel();
                _currentSeekCts?.Dispose();
                _currentSeekCts = null;

                _deferredSeekCts?.Cancel();
                _deferredSeekCts?.Dispose();
                _deferredSeekCts = null;

                // 重置状态
                _latestSeekPosition = -1;
                _latestSeekOriginPosition = -1;
                _latestSeekIsPreview = false;
                _hasNewSeekRequest = false;
                _isExecutingSeek = false;
                _executingIsPreview = false;
                _currentExecutingTarget = -1;

                _hasPendingSeekContext = false;
                _pendingSeekContext = null;

                Debug.WriteLine("[SeekManager] 所有 Seek 操作已取消");
            }
        }

        /// <summary>
        /// 获取当前是否正在 Seek
        /// </summary>
        public bool IsSeeking
        {
            get
            {
                lock (_seekLock)
                {
                    bool pendingReal = _hasNewSeekRequest && !_latestSeekIsPreview;
                    bool executingReal = _isExecutingSeek && !_executingIsPreview;
                    return pendingReal || executingReal;
                }
            }
        }

        /// <summary>
        /// 是否存在长跳待恢复上下文
        /// </summary>
        public bool IsSeekingLong
        {
            get
            {
                lock (_seekLock)
                {
                    return _hasPendingSeekContext;
                }
            }
        }

        /// <summary>
        /// 是否仍有延迟等待中的 Seek
        /// </summary>
        public bool HasPendingDeferredSeek
        {
            get
            {
                lock (_seekLock)
                {
                    return _deferredSeekCts != null;
                }
            }
        }

        /// <summary>
        /// 取消当前长跳等待并恢复原播放状态
        /// </summary>
        public bool CancelPendingSeekAndRestore(bool? resumePlayback = null)
        {
            if (!TryCancelPendingSeekContext(out PendingSeekContext context))
            {
                Debug.WriteLine("[SeekManager] CancelPendingSeekAndRestore：无待恢复上下文");
                return false;
            }

            var snapshot = context;
            bool setOk = _audioEngine.SetPosition(snapshot.OriginSeconds);
            bool shouldResume = ApplyResumePause(snapshot, resumePlayback);

            Debug.WriteLine($"[SeekManager] CancelPendingSeekAndRestore：恢复到 {snapshot.OriginSeconds:F1}s，" +
                            $"setOk={setOk}, resume={shouldResume}, targetWas={snapshot.TargetSeconds:F1}s");
            return true;
        }

        /// <summary>
        /// 取消长跳等待（不做位置恢复，仅恢复播放/暂停状态）
        /// </summary>
        public bool CancelPendingLongSeek(bool? resumePlayback = null)
        {
            if (!TryCancelPendingSeekContext(out PendingSeekContext context))
            {
                Debug.WriteLine("[SeekManager] CancelPendingLongSeek：无待恢复上下文");
                return false;
            }

            var snapshot = context;
            bool shouldResume = ApplyResumePause(snapshot, resumePlayback);

            Debug.WriteLine($"[SeekManager] CancelPendingLongSeek：取消等待，resume={shouldResume}, targetWas={snapshot.TargetSeconds:F1}s");
            return true;
        }

        private bool TryCancelPendingSeekContext(out PendingSeekContext context)
        {
            context = default;

            if (IsDisposed || _audioEngine == null)
            {
                return false;
            }

            bool hasContext = false;
            lock (_seekLock)
            {
                _seekTimer?.Change(Timeout.Infinite, Timeout.Infinite);

                _currentSeekCts?.Cancel();
                _currentSeekCts?.Dispose();
                _currentSeekCts = null;

                _deferredSeekCts?.Cancel();
                _deferredSeekCts?.Dispose();
                _deferredSeekCts = null;

                _latestSeekPosition = -1;
                _latestSeekOriginPosition = -1;
                _latestSeekIsPreview = false;
                _hasNewSeekRequest = false;
                _isExecutingSeek = false;
                _executingIsPreview = false;
                _currentExecutingTarget = -1;

                if (_hasPendingSeekContext && _pendingSeekContext.HasValue)
                {
                    context = _pendingSeekContext.Value;
                    _hasPendingSeekContext = false;
                    _pendingSeekContext = null;
                    hasContext = true;
                }
            }

            _cacheManager?.CancelSeekBoost();

            return hasContext;
        }

        private bool ApplyResumePause(PendingSeekContext snapshot, bool? resumePlayback)
        {
            bool shouldResume = resumePlayback ?? snapshot.WasPlaying;
            if (shouldResume)
            {
                if (!_audioEngine.IsPlaying)
                {
                    _audioEngine.Resume();
                }
            }
            else
            {
                if (_audioEngine.IsPlaying)
                {
                    _audioEngine.Pause();
                }
            }

            return shouldResume;
        }

        #endregion

        #region 私有方法

        /// <summary>
        /// 快速定时器回调（50ms 执行一次）
        /// </summary>
        private void ExecuteSeekTimerCallback(object? state)
        {
            double targetPosition;
            double originPosition;
            bool isPreview;
            bool isUsingCache;
            SmartCacheManager? cacheManager;
            CancellationTokenSource? seekCts;
            long seekVersion;

            // 获取状态（线程安全）
            lock (_seekLock)
            {
                // 如果正在执行 Seek，无论预览/正式都取消并用新请求替换，保证快速响应
                if (_isExecutingSeek)
                {
                    _currentSeekCts?.Cancel();
                    _currentSeekCts?.Dispose();
                    _currentSeekCts = null;
                    _isExecutingSeek = false;
                    _executingIsPreview = false;
                }

                // 如果没有新的请求，退出
                if (!_hasNewSeekRequest || _latestSeekPosition < 0)
                {
                    return;
                }

                // 获取最新的目标位置
                targetPosition = _latestSeekPosition;
                originPosition = _latestSeekOriginPosition;
                isPreview = _latestSeekIsPreview;
                isUsingCache = _isUsingCacheStream;
                cacheManager = _cacheManager;
                seekVersion = _latestSeekVersion;

                // 创建新的取消令牌
                _currentSeekCts = new CancellationTokenSource();
                seekCts = _currentSeekCts;

                // 标记正在执行
                _isExecutingSeek = true;
                _executingIsPreview = isPreview;
                _currentExecutingTarget = targetPosition;
                _currentExecutingVersion = seekVersion;
                _hasNewSeekRequest = false;
            }

            // 在后台线程执行（不阻塞定时器）
            _ = Task.Run(async () =>
            {
                bool seekSuccess = false;
                try
                {
                    seekSuccess = await ExecuteSeekAsync(targetPosition, originPosition, isPreview, isUsingCache, cacheManager, seekCts.Token, seekVersion).ConfigureAwait(false);
                }
                finally
                {
                    lock (_seekLock)
                    {
                        _isExecutingSeek = false;
                        _executingIsPreview = false;
                        _currentExecutingTarget = -1;
                        _currentExecutingVersion = 0;

                        // 如果有新的请求，继续启动定时器
                        if (_hasNewSeekRequest)
                        {
                            _seekTimer?.Change(SEEK_INTERVAL_MS, Timeout.Infinite);
                        }
                        _lastSeekSuccess = seekSuccess;
                    }
                }
            });
        }

        /// <summary>
        /// 异步执行 Seek（智能等待缓存数据）
        /// </summary>
        private async Task<bool> ExecuteSeekAsync(
            double targetSeconds,
            double originSeconds,
            bool isPreview,
            bool isUsingCache,
            SmartCacheManager? cacheManager,
            CancellationToken cancellationToken,
            long seekVersion)
        {
            CancellationTokenSource? linkedCts = null;
            Task? progressMonitor = null;
            bool success = false;
            bool cancelledByNaturalProgress = false;
            long pendingVersion = 0;

            try
            {
                Debug.WriteLine($"[SeekManager] ⚡ 执行智能 Seek: {targetSeconds:F1}s");
                var startTime = DateTime.Now;

                bool isForwardSeek = originSeconds >= 0 && targetSeconds > originSeconds + 0.01;
                linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                CancellationToken effectiveToken = linkedCts.Token;

                if (!isPreview && isForwardSeek)
                {
                    progressMonitor = MonitorNaturalProgressAsync(targetSeconds, linkedCts);
                }

                if (isPreview)
                {
                    // 预览模式：快速跳转，不等待缓存就绪，减少卡顿感
                    success = _audioEngine.SetPosition(targetSeconds);
                }
                else
                {
                    double distance = originSeconds >= 0 ? Math.Abs(targetSeconds - originSeconds) : double.MaxValue;
                    bool isShortJump = distance <= 6.0; // 短按快进/快退
                    bool isLongSeek = !isPreview && !isShortJump;

                    if (isLongSeek && originSeconds >= 0)
                    {
                        pendingVersion = BeginPendingLongSeek(originSeconds, targetSeconds);
                    }

                    if (isUsingCache && cacheManager != null && isShortJump)
                    {
                        long targetBytes = _audioEngine.GetBytesFromSeconds(targetSeconds);
                        bool ready = cacheManager.AreChunksReady(targetBytes, aheadChunks: 5);

                        // 类似 scrub：若目标及后续3块已在缓存，立即跳转；否则也直接跳，并后台补块
                        success = _audioEngine.SetPosition(targetSeconds);
                        cacheManager.RequestSeekBoost(targetBytes, effectiveToken);
                        _ = cacheManager.PrefetchAroundAsync(targetBytes, aheadChunks: 5, effectiveToken, allowRangeRescue: true);
                        if (!ready)
                        {
                            // 若尚未就绪，额外触发一次按需下载目标块，降低后续阻塞概率
                            _ = cacheManager.EnsurePositionAsync(targetBytes, effectiveToken, allowRangeRescue: true);
                        }
                    }
                    else if (isUsingCache && cacheManager != null)
                    {
                        long targetBytes = _audioEngine.GetBytesFromSeconds(targetSeconds);
                        int timeoutMs = isPreview ? PREVIEW_CACHE_WAIT_TIMEOUT_MS : SEEK_CACHE_WAIT_TIMEOUT_MS;
                        bool waitTargetOnly = false;

                        // 长距离跳转：提前触发目标区间的按需缓存，缩短等待时间
                        cacheManager.RequestSeekBoost(targetBytes, effectiveToken);
                        _ = cacheManager.PrefetchAroundAsync(targetBytes, aheadChunks: 6, effectiveToken, allowRangeRescue: true);

                        success = await _audioEngine.SetPositionWithCacheWaitAsync(
                            targetSeconds,
                            timeoutMs,
                            effectiveToken,
                            waitTargetOnly: waitTargetOnly).ConfigureAwait(false);

                        // 若等待超时/失败，则进入后台缓冲等待，避免跳转后长时间无声
                        if (!success && !effectiveToken.IsCancellationRequested)
                        {
                            Debug.WriteLine($"[SeekManager] ?? 缓存未就绪，转入后台缓冲等待: {targetSeconds:F1}s");
                            success = await ExecuteDeferredSeekAsync(targetSeconds, cacheManager, effectiveToken).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        if (linkedCts == null || !linkedCts.IsCancellationRequested)
                        {
                            success = _audioEngine.SetPosition(targetSeconds);
                        }
                    }
                }

                var elapsed = (DateTime.Now - startTime).TotalMilliseconds;

                if (success)
                {
                    _audioEngine.ApplySeekFadeIn();
                    Debug.WriteLine($"[SeekManager] ✓ 智能 Seek 成功 (含淡入): {targetSeconds:F1}s (耗时 {elapsed:F0}ms)");
                    _consecutiveFailures = 0;
                }
                else if (!effectiveToken.IsCancellationRequested)
                {
                    Debug.WriteLine($"[SeekManager] ⚠️ 智能 Seek 失败: {targetSeconds:F1}s (耗时 {elapsed:F0}ms)");
                    _consecutiveFailures++;
                }
            }
            catch (OperationCanceledException)
            {
                if (linkedCts != null && linkedCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    cancelledByNaturalProgress = true;
                    Debug.WriteLine($"[SeekManager] ⏹ Seek 因自然播放经过目标位置而取消: {targetSeconds:F1}s");
                }
                else
                {
                    Debug.WriteLine($"[SeekManager] 🚫 Seek 被取消（新命令优先）: {targetSeconds:F1}s");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SeekManager] ❌ 智能 Seek 异常: {ex.Message}");
                _consecutiveFailures++;
            }
            finally
            {
                if (progressMonitor != null)
                {
                    try
                    {
                        await progressMonitor.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // 忽略
                    }
                }

                linkedCts?.Dispose();
            }

            if (cancelledByNaturalProgress)
            {
                _consecutiveFailures = 0;
            }

            if (pendingVersion > 0)
            {
                ClearPendingSeekContext(pendingVersion);
            }

            bool effectiveSuccess = success && !cancelledByNaturalProgress;

            try
            {
                SeekExecuted?.Invoke(this, new SeekExecutionEventArgs(targetSeconds, effectiveSuccess, isPreview, seekVersion));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SeekManager] SeekExecuted 处理异常: {ex.Message}");
            }

            return effectiveSuccess;
        }

        private async Task<bool> ExecuteDeferredSeekAsync(double targetSeconds, SmartCacheManager cacheManager, CancellationToken token)
        {
            if (cacheManager == null)
            {
                return false;
            }

            bool wasPlaying = false;
            bool pausedBySeek = false;

            try
            {
                Debug.WriteLine($"[SeekManager] ?? ????????: {targetSeconds:F1}s");
                long targetBytes = _audioEngine.GetBytesFromSeconds(targetSeconds);
                wasPlaying = _audioEngine.IsPlaying;

                if (wasPlaying)
                {
                    _audioEngine.Pause();
                    pausedBySeek = true;
                }

                _ = cacheManager.PrefetchAroundAsync(targetBytes, aheadChunks: 6, token, allowRangeRescue: true);

                CancellationTokenSource? waitCts = null;
                try
                {
                    waitCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    waitCts.CancelAfter(TimeSpan.FromMilliseconds(SEEK_DEFERRED_WAIT_TIMEOUT_MS));

                    lock (_seekLock)
                    {
                        _deferredSeekCts?.Cancel();
                        _deferredSeekCts?.Dispose();
                        _deferredSeekCts = waitCts;
                    }

                    bool ready = await cacheManager.WaitForCacheReadyAsync(targetBytes, true, waitCts.Token).ConfigureAwait(false);
                    if (!ready)
                    {
                        Debug.WriteLine($"[SeekManager] ?? ??????: {targetSeconds:F1}s");
                        return false;
                    }
                }
                finally
                {
                    if (waitCts != null)
                    {
                        lock (_seekLock)
                        {
                            if (ReferenceEquals(_deferredSeekCts, waitCts))
                            {
                                _deferredSeekCts = null;
                            }
                        }

                        try
                        {
                            waitCts.Dispose();
                        }
                        catch (ObjectDisposedException)
                        {
                        }
                    }
                }

                Debug.WriteLine($"[SeekManager] ? ???????????: {targetSeconds:F1}s");
                bool setSuccess = _audioEngine.SetPosition(targetSeconds);
                if (!setSuccess)
                {
                    return false;
                }

                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SeekManager] ? ??Seek??: {ex.Message}");
                return false;
            }
            finally
            {
                if (pausedBySeek && wasPlaying && !token.IsCancellationRequested)
                {
                    try
                    {
                        if (!_audioEngine.IsPlaying)
                        {
                            _audioEngine.Resume();
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[SeekManager] ??????: {ex.Message}");
                    }
                }
            }
        }

        private long BeginPendingLongSeek(double originSeconds, double targetSeconds)
        {
            PlaybackState playbackState = _audioEngine.GetPlaybackState();
            bool wasPlaying = _audioEngine.IsPlaying;
            bool wasPaused = _audioEngine.IsPaused;

            lock (_seekLock)
            {
                _pendingSeekVersion++;
                long version = _pendingSeekVersion;
                _pendingSeekContext = new PendingSeekContext(originSeconds, targetSeconds, wasPlaying, wasPaused, playbackState, version);
                _hasPendingSeekContext = true;
                return version;
            }
        }

        private void ClearPendingSeekContext(long version)
        {
            lock (_seekLock)
            {
                if (_hasPendingSeekContext && _pendingSeekContext.HasValue && _pendingSeekContext.Value.SeekVersion == version)
                {
                    _hasPendingSeekContext = false;
                    _pendingSeekContext = null;
                }
            }
        }

        private Task MonitorNaturalProgressAsync(double targetSeconds, CancellationTokenSource linkedCts)
        {
            return Task.Run(async () =>
            {
                try
                {
                    while (!linkedCts.IsCancellationRequested)
                    {
                        await Task.Delay(NATURAL_PROGRESS_POLL_INTERVAL_MS, linkedCts.Token).ConfigureAwait(false);

                        double currentPosition = _audioEngine.GetPosition();
                        if (currentPosition + NATURAL_PASS_TOLERANCE_SECONDS >= targetSeconds)
                        {
                            Debug.WriteLine($"[SeekManager] 🎯 当前播放 {currentPosition:F1}s 已超过目标 {targetSeconds:F1}s，取消本次 Seek");
                            linkedCts.Cancel();
                            break;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // 正常取消
                }
            });
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (System.Threading.Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            _seekTimer?.Dispose();
            _seekTimer = null;

            _currentSeekCts?.Cancel();
            _currentSeekCts?.Dispose();
            _currentSeekCts = null;

            _deferredSeekCts?.Cancel();
            _deferredSeekCts?.Dispose();
            _deferredSeekCts = null;

            _hasPendingSeekContext = false;
            _pendingSeekContext = null;
        }

        #endregion
    }
}
