using System;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using YTPlayer.Core.Playback.Cache;

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
        private bool _hasNewSeekRequest = false;   // 是否有新的 Seek 请求
        private bool _isExecutingSeek = false;     // 是否正在执行 Seek
        private CancellationTokenSource? _currentSeekCts = null;  // 当前 Seek 操作的取消令牌

        // 快速定时器（50ms）
        private Timer? _seekTimer;
        private const int SEEK_INTERVAL_MS = 50;  // 50ms 一次，快速响应

        // 远距离跳转等待超时（60 秒，覆盖更多网络慢的情况）
        private const int SEEK_CACHE_WAIT_TIMEOUT_MS = 60000;
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

        #endregion

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
        public void RequestSeek(double targetSeconds)
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

            lock (_seekLock)
            {
                // 保存最新的目标位置（丢弃旧的）
                _latestSeekPosition = targetSeconds;
                _latestSeekOriginPosition = _audioEngine?.GetPosition() ?? -1;
                _hasNewSeekRequest = true;

                // 如果定时器未启动，启动它
                if (_seekTimer != null)
                {
                    _seekTimer.Change(SEEK_INTERVAL_MS, Timeout.Infinite);
                }

                Debug.WriteLine($"[SeekManager] Seek 请求: {targetSeconds:F1}s（50ms 后执行，新命令覆盖旧命令）");
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

            // 触发完成事件
            SeekCompleted?.Invoke(this, true);
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

                // 重置状态
                _latestSeekPosition = -1;
                _latestSeekOriginPosition = -1;
                _hasNewSeekRequest = false;
                _isExecutingSeek = false;

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
                    return _hasNewSeekRequest || _isExecutingSeek;
                }
            }
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
            bool isUsingCache;
            SmartCacheManager? cacheManager;
            CancellationTokenSource? seekCts;

            // 获取状态（线程安全）
            lock (_seekLock)
            {
                // 如果正在执行 Seek，取消旧操作并启动新操作（丢弃模式）
                if (_isExecutingSeek)
                {
                    Debug.WriteLine("[SeekManager] 上一次 Seek 未完成，取消旧操作并启动新操作");

                    // 取消旧的 seek 操作
                    _currentSeekCts?.Cancel();
                    _currentSeekCts?.Dispose();
                    _currentSeekCts = null;

                    // 重置状态以允许新操作
                    _isExecutingSeek = false;
                }

                // 如果没有新的请求，退出
                if (!_hasNewSeekRequest || _latestSeekPosition < 0)
                {
                    return;
                }

                // 获取最新的目标位置
                targetPosition = _latestSeekPosition;
                originPosition = _latestSeekOriginPosition;
                isUsingCache = _isUsingCacheStream;
                cacheManager = _cacheManager;

                // 创建新的取消令牌
                _currentSeekCts = new CancellationTokenSource();
                seekCts = _currentSeekCts;

                // 标记正在执行
                _isExecutingSeek = true;
                _hasNewSeekRequest = false;
            }

            // 在后台线程执行（不阻塞定时器）
            _ = Task.Run(async () =>
            {
                try
                {
                    await ExecuteSeekAsync(targetPosition, originPosition, isUsingCache, cacheManager, seekCts.Token).ConfigureAwait(false);
                }
                finally
                {
                    lock (_seekLock)
                    {
                        _isExecutingSeek = false;

                        // 如果有新的请求，继续启动定时器
                        if (_hasNewSeekRequest)
                        {
                            _seekTimer?.Change(SEEK_INTERVAL_MS, Timeout.Infinite);
                        }
                    }
                }
            });
        }

        /// <summary>
        /// 异步执行 Seek（智能等待缓存数据）
        /// </summary>
        private async Task ExecuteSeekAsync(
            double targetSeconds,
            double originSeconds,
            bool isUsingCache,
            SmartCacheManager? cacheManager,
            CancellationToken cancellationToken)
        {
            CancellationTokenSource? linkedCts = null;
            Task? progressMonitor = null;
            bool success = false;
            bool cancelledByNaturalProgress = false;

            try
            {
                Debug.WriteLine($"[SeekManager] ⚡ 执行智能 Seek: {targetSeconds:F1}s");
                var startTime = DateTime.Now;

                bool isForwardSeek = originSeconds >= 0 && targetSeconds > originSeconds + 0.01;
                CancellationToken effectiveToken = cancellationToken;

                if (isForwardSeek)
                {
                    linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    effectiveToken = linkedCts.Token;
                    progressMonitor = MonitorNaturalProgressAsync(targetSeconds, linkedCts);
                }

                if (isUsingCache && cacheManager != null)
                {
                    success = await _audioEngine.SetPositionWithCacheWaitAsync(
                        targetSeconds,
                        SEEK_CACHE_WAIT_TIMEOUT_MS,
                        effectiveToken).ConfigureAwait(false);
                }
                else
                {
                    if (linkedCts == null || !linkedCts.IsCancellationRequested)
                    {
                        success = _audioEngine.SetPosition(targetSeconds);
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
        }

        #endregion
    }
}
