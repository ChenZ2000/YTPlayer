using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using YTPlayer.Core.Streaming;
using YTPlayer.Utils;

namespace YTPlayer.Core.Playback.Cache
{
    /// <summary>
    /// 新一代智能缓存管理器，负责在多种网络条件下为 BASS 提供稳定的块数据。
    /// 阶段3优化：集成智能预缓存和带宽分配管理
    /// </summary>
    public sealed class SmartCacheManager : IDisposable
    {
        private const int ChunkSize = 256 * 1024; // 256KB - Optimized for faster startup
        private const int PreloadAheadChunks = 6;
        private const int PreloadBehindChunks = 2;
        private const int MinReadyChunks = 3;
        private const int MaxPreloadConcurrency = 8; // ⭐ 提高并发度以加速 SequentialFull 下载
        private const int HealthPollDelayMs = 50; // 加快缓存就绪轮询以匹配更高的按键频率
        private const int WaitForCacheReadyHardTimeoutMs = 15000;
        private const int ReadAsyncHardTimeoutMs = 20000;

        // ⭐ Strategy detection cache per domain (reduces redundant HEAD requests)
        private static readonly ConcurrentDictionary<string, DownloadStrategy> _strategyCache
            = new ConcurrentDictionary<string, DownloadStrategy>();

        private readonly string _songId;  // 🎯 歌曲ID，用于预缓存系统
        private readonly string _url;
        private readonly long _totalSize;
        private readonly int _totalChunks;
        private readonly HttpClient _httpClient;
        private readonly IDictionary<string, string>? _headers;
        private readonly ConcurrentDictionary<int, byte[]> _cache;
        private readonly ChunkDownloadManager _downloader;
        private readonly bool _preferSequentialFull;

        private PriorityDownloadScheduler? _scheduler;
        private CancellationTokenSource? _preloadCts;
        private Task? _preloadTask;
        private TaskCompletionSource<bool>? _initialBufferTcs;
        private readonly CancellationTokenSource _lifecycleCts = new CancellationTokenSource();

        // ⭐ 阶段3：智能预缓存和带宽管理
        private SmartPreCacheManager? _smartPreCache;
        private BandwidthAllocator? _bandwidthAllocator;

        private DownloadStrategy _strategy = DownloadStrategy.SequentialFull;
        private long _cachedBytes;
        private int _currentChunk;
        private bool _disposed;

        private Task? _mainDownloadTask;
        private CancellationTokenSource? _mainDownloadCts;
        private bool _isPreloadMode;
        private bool _initialBufferSignaled;
        private bool _isFullyCached;
        private Exception? _sequentialFailure;
        private bool _rangePreloaderStarted;
        private readonly object _downloadLock = new object();
        private int _rangeFailureCount;
        private const int RangeFailureThreshold = 3;
        private bool _rangeFallbackTriggered;
        private bool _rangeSupportChecked;
        private bool _supportsRange;
        private readonly SemaphoreSlim _rangeSupportLock = new SemaphoreSlim(1, 1);

        private readonly object _seekBoostLock = new object();
        private readonly object _seekBoostStatsLock = new object();
        private CancellationTokenSource? _seekBoostCts;
        private long _seekBoostVersion;
        private const int SeekBoostAheadChunks = 8;
        private const int SeekBoostBehindChunks = 2;
        private const int SeekBoostMaxConcurrency = 3;
        private const int SeekBoostMinConcurrency = 2;
        private const int SeekBoostMaxAdaptiveConcurrency = 6;
        private double _seekBoostAvgChunkMs;

        private bool _resumeSequentialAfterSeek;
        private bool _resumeSequentialPreloadOnly;
        private long _resumeSequentialVersion;

        private readonly struct SeekBoostProfile
        {
            public int Ahead { get; }
            public int Behind { get; }
            public int BaseConcurrency { get; }
            public string Reason { get; }

            public SeekBoostProfile(int ahead, int behind, int baseConcurrency, string reason)
            {
                Ahead = ahead;
                Behind = behind;
                BaseConcurrency = baseConcurrency;
                Reason = reason;
            }
        }

        private readonly object _stateLock = new object();
        private readonly object _bufferingLock = new object();
        private BufferingState _bufferingState = BufferingState.Idle;

        public SmartCacheManager(string songId, string url, long totalSize, HttpClient httpClient, bool preferSequentialFull = false, IDictionary<string, string>? headers = null)
        {
            _songId = songId ?? string.Empty;  // 🎯 允许空字符串（用于不需要预缓存的场景）
            _url = url ?? throw new ArgumentNullException(nameof(url));
            _totalSize = totalSize > 0 ? totalSize : throw new ArgumentOutOfRangeException(nameof(totalSize));
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _preferSequentialFull = preferSequentialFull;
            _headers = headers;

            _totalChunks = (int)Math.Ceiling(totalSize / (double)ChunkSize);
            _cache = new ConcurrentDictionary<int, byte[]>();
            _downloader = new ChunkDownloadManager(url, totalSize, ChunkSize, _httpClient, _headers);
        }

        /// <summary>
        /// ⭐ 注入预加载的初始数据到缓存（避免重复下载）
        /// </summary>
        public void InjectInitialData(byte[] initialData)
        {
            if (initialData == null || initialData.Length == 0)
            {
                return;
            }

            try
            {
                int totalInjected = 0;
                int chunkIndex = 0;
                int offset = 0;

                while (offset < initialData.Length)
                {
                    int remainingInData = initialData.Length - offset;
                    int chunkDataSize = Math.Min(ChunkSize, remainingInData);
                    long expectedSize = Math.Min(ChunkSize, _totalSize - (long)chunkIndex * ChunkSize);
                    if (expectedSize <= 0 || chunkDataSize != expectedSize) break;

                    byte[] chunkData = new byte[chunkDataSize];
                    Array.Copy(initialData, offset, chunkData, 0, chunkDataSize);

                    if (_cache.TryAdd(chunkIndex, chunkData))
                    {
                        Interlocked.Add(ref _cachedBytes, chunkDataSize);
                        totalInjected++;
                        DebugLogger.Log(
                            DebugLogger.LogLevel.Info,
                            "SmartCache",
                            $"✓ 注入块 {chunkIndex}: {chunkDataSize / 1024}KB");
                    }

                    chunkIndex++;
                    offset += chunkDataSize;
                }

                DebugLogger.Log(
                    DebugLogger.LogLevel.Info,
                    "SmartCache",
                    $"✓✓✓ 初始数据注入完成: {totalInjected} 块, {initialData.Length / 1024 / 1024:F1}MB");

                ReportProgress();
            }
            catch (Exception ex)
            {
                DebugLogger.LogException("SmartCache", ex, "注入初始数据失败");
            }
        }

        public event EventHandler<int>? BufferingProgressChanged;
        public event EventHandler<DownloadStrategy>? StrategyDetermined;
        public event EventHandler<BufferingState>? BufferingStateChanged;

        public DownloadStrategy Strategy => _strategy;
        public long TotalSize => _totalSize;
        public int TotalChunks => _totalChunks;
        public int CachedChunkCount => _cache.Count;
        public long TotalCachedBytes => Interlocked.Read(ref _cachedBytes);
        public BufferingState CurrentBufferingState
        {
            get
            {
                lock (_bufferingLock)
                {
                    return _bufferingState;
                }
            }
        }
        public bool IsFullyCached => _isFullyCached;
        public bool IsDisposed => _disposed;
        internal CancellationToken LifecycleToken => _lifecycleCts.Token;
        public double CacheFillFraction => _totalSize == 0 ? 0 : Math.Min(1.0, TotalCachedBytes / (double)_totalSize);
        public bool CanSpareBandwidthForPreload
        {
            get
            {
                if (IsFullyCached)
                {
                    return true;
                }

                double fill = CacheFillFraction;

                if (_strategy == DownloadStrategy.Range)
                {
                    return CurrentBufferingState == BufferingState.Playing && fill >= 0.20;
                }

                return fill >= 0.65;
            }
        }

        /// <summary>
        /// 初始化缓存管理器
        /// </summary>
        /// <param name="token">取消令牌</param>
        /// <param name="isPreload">是否为预加载场景（预加载只需要 Chunk0，不需要最后块）</param>
        public async Task<bool> InitializeAsync(CancellationToken token, bool isPreload = false)
        {
            EnsureNotDisposed();

            if (!_strategyCache.TryGetValue(GetStrategyCacheKey(), out _strategy))
            {
                _strategy = await DetectStrategyAsync(token).ConfigureAwait(false);
                _strategyCache[GetStrategyCacheKey()] = _strategy;
            }

            if (_strategy == DownloadStrategy.Range)
            {
                _supportsRange = true;
                _rangeSupportChecked = true;
            }

            StrategyDetermined?.Invoke(this, _strategy);
            DebugLogger.Log(
                DebugLogger.LogLevel.Info,
                "SmartCache",
                $"最终策略：{_strategy} (size={_totalSize:N0} bytes)");

            // ⭐ 阶段3：初始化带宽分配器
            _bandwidthAllocator = new BandwidthAllocator();
            _bandwidthAllocator.ActivateMainPlayback();

            bool initResult;
            switch (_strategy)
            {
                case DownloadStrategy.Range:
                    initResult = await InitializeRangeModeAsync(token, isPreload).ConfigureAwait(false);
                    break;

                case DownloadStrategy.ParallelFull:
                case DownloadStrategy.SequentialFull:
                    initResult = await InitializeFullDownloadModeAsync(token, isPreload).ConfigureAwait(false);

                    // ⭐ 阶段3：仅在多连接策略下启用智能预缓存，避免顺序流重复跳读
                    if (initResult &&
                        _totalSize > 100 * 1024 * 1024 &&
                        _strategy != DownloadStrategy.SequentialFull)
                    {
                        StartSmartPreCache(token);
                    }
                    break;

                default:
                    initResult = false;
                    break;
            }

            return initResult;
        }

        /// <summary>
        /// 阶段3：启动智能预缓存（后台任务）
        /// </summary>
        private void StartSmartPreCache(CancellationToken token)
        {
            try
            {
                _smartPreCache = new SmartPreCacheManager();

                _ = Task.Run(async () =>
                {
                    try
                    {
                        // ⭐⭐⭐ 关键优化：移除延迟，立即启动预缓存（在后台低优先级运行）
                        // 不会阻塞主播放，因为主播放已经通过 chunk 0 快速启动了
                        if (!_disposed && !token.IsCancellationRequested)
                        {
                            DebugLogger.Log(
                                DebugLogger.LogLevel.Info,
                                "SmartCache",
                                "🚀 立即启动智能预缓存系统（后台串行）");

                            await _smartPreCache.StartPreCachingAsync(
                                _url,
                                _totalSize,
                                ChunkSize,
                                token).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.LogException("SmartCache", ex, "智能预缓存启动失败");
                    }
                }, token);
            }
            catch (Exception ex)
            {
                DebugLogger.LogException("SmartCache", ex, "创建智能预缓存管理器失败");
            }
        }

        public void UpdatePlaybackPosition(long bytePosition)
        {
            if (_disposed)
            {
                return;
            }

            int newChunk = GetChunkIndex(bytePosition);
            if (newChunk != _currentChunk)
            {
                _currentChunk = newChunk;
                _scheduler?.UpdatePlaybackWindow(newChunk);
            }

            var health = CheckCacheHealth(bytePosition);
            if (!health.IsReady)
            {
                if (CurrentBufferingState != BufferingState.Buffering)
                {
                    SetBufferingState(BufferingState.LowBuffer);
                }
            }
            else if (CurrentBufferingState == BufferingState.LowBuffer ||
                     CurrentBufferingState == BufferingState.Buffering)
            {
                SetBufferingState(BufferingState.Playing);
            }
        }

        public CacheHealthInfo CheckCacheHealth(long bytePosition, bool forPlayback = true)
        {
            int targetChunk = GetChunkIndex(bytePosition);
            int requiredBase = forPlayback ? MinReadyChunks : 1;
            int required = Math.Min(requiredBase, Math.Max(1, _totalChunks - targetChunk));

            int ready = 0;
            for (int i = 0; i < required; i++)
            {
                int idx = targetChunk + i;
                if (_cache.ContainsKey(idx))
                {
                    ready++;
                }
            }

            bool isBuffering = ready < required;
            return new CacheHealthInfo(targetChunk, ready, required, !isBuffering, isBuffering);
        }

        public async Task<bool> WaitForPositionReadyAsync(
            long bytePosition,
            int timeoutMilliseconds,
            CancellationToken token,
            bool forPlayback = true)
        {
            var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMilliseconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token, _lifecycleCts.Token);

            try
            {
                while (true)
                {
                    linkedCts.Token.ThrowIfCancellationRequested();

                    var health = CheckCacheHealth(bytePosition, forPlayback);
                    if (health.IsReady)
                    {
                        return true;
                    }

                    await Task.Delay(HealthPollDelayMs, linkedCts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return CheckCacheHealth(bytePosition, forPlayback).IsReady;
            }
        }

        /// <summary>
        /// 主动预取目标块及后续若干块，避免短距跳转后的立即卡顿。
        /// </summary>
        public Task PrefetchAroundAsync(long bytePosition, int aheadChunks, CancellationToken token, bool allowRangeRescue = false)
        {
            int centerChunk = (int)(bytePosition / ChunkSize);
            if (aheadChunks <= 0) return Task.CompletedTask;

            var tasks = new List<Task>(aheadChunks + 1);
            for (int i = 0; i <= aheadChunks; i++)
            {
                int idx = centerChunk + i;
                if (idx < 0 || idx >= _totalChunks) continue;
                tasks.Add(EnsureChunkAsync(idx, token, allowRangeRescue));
            }

            return Task.WhenAll(tasks);
        }

        public bool IsChunkCached(int chunkIndex)
        {
            return _cache.ContainsKey(chunkIndex);
        }

        /// <summary>
        /// 确保包含指定字节位置的块已被调度/下载。
        /// </summary>
        public Task EnsurePositionAsync(long bytePosition, CancellationToken token, bool allowRangeRescue = false)
        {
            int chunkIndex = GetChunkIndex(bytePosition);
            return EnsureChunkAsync(chunkIndex, token, allowRangeRescue);
        }

        public Task EnsureChunkAsync(int chunkIndex, CancellationToken token, bool allowRangeRescue = false)
        {
            if (_cache.ContainsKey(chunkIndex))
            {
                return Task.CompletedTask;
            }

            if (_strategy != DownloadStrategy.Range)
            {
                if (allowRangeRescue)
                {
                    return EnsureChunkRescueAsync(chunkIndex, token);
                }
                return Task.CompletedTask;
            }

            return DownloadChunkOnDemandAsync(chunkIndex, token);
        }

        public void RequestSeekBoost(long bytePosition, CancellationToken token)
        {
            if (_disposed)
            {
                return;
            }

            var profile = GetSeekBoostProfile();
            int targetChunk = GetChunkIndex(bytePosition);
            int maxAhead = Math.Max(0, _totalChunks - 1 - targetChunk);
            int maxBehind = Math.Max(0, targetChunk);
            int ahead = Math.Min(profile.Ahead, maxAhead);
            int behind = Math.Min(profile.Behind, maxBehind);
            var chunks = BuildSeekBoostChunkList(targetChunk, ahead, behind);

            CancellationTokenSource? cts;
            long version;
            lock (_seekBoostLock)
            {
                _seekBoostCts?.Cancel();
                _seekBoostCts?.Dispose();

                _seekBoostVersion++;
                version = _seekBoostVersion;

                _seekBoostCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts = _seekBoostCts;
            }

            TryPauseSequentialDownloadForSeekBoost(version);

            double avgMs = GetSeekBoostAverageMs();
            int concurrency = DecideSeekBoostConcurrency(profile.BaseConcurrency);
            DebugLogger.Log(
                DebugLogger.LogLevel.Info,
                "SmartCache",
                $"SeekBoost profile: size={_totalSize / 1024d / 1024d:F1}MB, chunks={_totalChunks}, " +
                $"target={targetChunk}, ahead={ahead}, behind={behind}, baseConc={profile.BaseConcurrency}, " +
                $"avgMs={avgMs:F0}, conc={concurrency}, reason={profile.Reason}");

            _ = Task.Run(async () =>
            {
                try
                {
                    await RunSeekBoostAsync(chunks, version, concurrency, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    DebugLogger.LogException("SmartCache", ex, "SeekBoost 执行异常");
                }
            });
        }

        public void CancelSeekBoost()
        {
            lock (_seekBoostLock)
            {
                _seekBoostCts?.Cancel();
                _seekBoostCts?.Dispose();
                _seekBoostCts = null;
                _seekBoostVersion++;
            }

            DebugLogger.Log(
                DebugLogger.LogLevel.Info,
                "SmartCache",
                "SeekBoost 已取消");
        }

        private List<int> BuildSeekBoostChunkList(int targetChunk, int ahead, int behind)
        {
            var list = new List<int>();
            if (targetChunk < 0 || targetChunk >= _totalChunks)
            {
                return list;
            }

            list.Add(targetChunk);

            for (int i = 1; i <= ahead; i++)
            {
                int idx = targetChunk + i;
                if (idx >= 0 && idx < _totalChunks)
                {
                    list.Add(idx);
                }
            }

            for (int i = 1; i <= behind; i++)
            {
                int idx = targetChunk - i;
                if (idx >= 0 && idx < _totalChunks)
                {
                    list.Add(idx);
                }
            }

            return list;
        }

        private async Task RunSeekBoostAsync(List<int> chunks, long version, int maxConcurrency, CancellationToken token)
        {
            if (chunks == null || chunks.Count == 0)
            {
                TryResumeSequentialDownloadAfterSeekBoost(version);
                return;
            }

            bool supportsRange = await EnsureRangeSupportAsync(token).ConfigureAwait(false);
            if (!supportsRange)
            {
                TryResumeSequentialDownloadAfterSeekBoost(version);
                return;
            }

            _bandwidthAllocator?.ActivateFastSeek();

            int concurrency = Math.Max(SeekBoostMinConcurrency, Math.Min(SeekBoostMaxAdaptiveConcurrency, maxConcurrency));
            var semaphore = new SemaphoreSlim(concurrency, concurrency);
            var tasks = new List<Task>();

            try
            {
                foreach (int chunkIndex in chunks)
                {
                    if (token.IsCancellationRequested || version != Volatile.Read(ref _seekBoostVersion))
                    {
                        break;
                    }

                    if (_cache.ContainsKey(chunkIndex))
                    {
                        continue;
                    }

                    try
                    {
                        await semaphore.WaitAsync(token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    tasks.Add(Task.Run(async () =>
                    {
                        var stopwatch = Stopwatch.StartNew();
                        try
                        {
                            if (token.IsCancellationRequested || version != Volatile.Read(ref _seekBoostVersion))
                            {
                                return;
                            }

                            byte[]? data = await DownloadChunkOnDemandAsync(chunkIndex, token).ConfigureAwait(false);
                            if (data != null && !token.IsCancellationRequested)
                            {
                                UpdateSeekBoostAverage(stopwatch.Elapsed.TotalMilliseconds);
                            }
                        }
                        catch (OperationCanceledException)
                        {
                        }
                        finally
                        {
                            try
                            {
                                semaphore.Release();
                            }
                            catch (ObjectDisposedException)
                            {
                            }
                        }
                    }));
                }

                if (tasks.Count > 0)
                {
                    try
                    {
                        await Task.WhenAll(tasks).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }
            }
            finally
            {
                _bandwidthAllocator?.DeactivateFastSeek();
                semaphore.Dispose();
                TryResumeSequentialDownloadAfterSeekBoost(version);
            }
        }

        private SeekBoostProfile GetSeekBoostProfile()
        {
            long size = _totalSize;
            int ahead;
            int behind;
            int baseConcurrency;
            string reason;

            if (_preferSequentialFull || size >= 80 * 1024 * 1024)
            {
                ahead = 14;
                behind = 3;
                baseConcurrency = 5;
                reason = _preferSequentialFull ? "preferSequential" : "large";
            }
            else if (size >= 20 * 1024 * 1024)
            {
                ahead = 9;
                behind = 3;
                baseConcurrency = 4;
                reason = "medium";
            }
            else
            {
                ahead = 6;
                behind = 2;
                baseConcurrency = 3;
                reason = "small";
            }

            baseConcurrency = Math.Max(SeekBoostMinConcurrency, Math.Min(SeekBoostMaxAdaptiveConcurrency, baseConcurrency));
            return new SeekBoostProfile(ahead, behind, baseConcurrency, reason);
        }

        private int DecideSeekBoostConcurrency(int baseConcurrency)
        {
            double avgMs = GetSeekBoostAverageMs();
            int adaptive = baseConcurrency;

            if (avgMs > 0)
            {
                if (avgMs < 300)
                {
                    adaptive = 5;
                }
                else if (avgMs < 800)
                {
                    adaptive = 4;
                }
                else
                {
                    adaptive = 2;
                }
            }

            adaptive = Math.Max(SeekBoostMinConcurrency, Math.Min(SeekBoostMaxAdaptiveConcurrency, adaptive));
            int chosen = Math.Max(SeekBoostMinConcurrency, Math.Min(baseConcurrency, adaptive));

            return chosen;
        }

        private void UpdateSeekBoostAverage(double chunkMs)
        {
            if (chunkMs <= 0)
            {
                return;
            }

            lock (_seekBoostStatsLock)
            {
                if (_seekBoostAvgChunkMs <= 0)
                {
                    _seekBoostAvgChunkMs = chunkMs;
                }
                else
                {
                    _seekBoostAvgChunkMs = _seekBoostAvgChunkMs * 0.8 + chunkMs * 0.2;
                }
            }
        }

        private double GetSeekBoostAverageMs()
        {
            lock (_seekBoostStatsLock)
            {
                return _seekBoostAvgChunkMs;
            }
        }

        private void TryPauseSequentialDownloadForSeekBoost(long version)
        {
            if (_strategy != DownloadStrategy.SequentialFull && _strategy != DownloadStrategy.ParallelFull)
            {
                return;
            }

            lock (_downloadLock)
            {
                bool active = _mainDownloadTask != null &&
                              !_mainDownloadTask.IsCompleted &&
                              !_mainDownloadTask.IsCanceled &&
                              !_mainDownloadTask.IsFaulted;

                if (!active)
                {
                    return;
                }

                _mainDownloadCts?.Cancel();
                _resumeSequentialAfterSeek = true;
                _resumeSequentialPreloadOnly = _isPreloadMode;
                _resumeSequentialVersion = version;

                DebugLogger.Log(
                    DebugLogger.LogLevel.Info,
                    "SmartCache",
                    "SeekBoost 启动：暂停顺序下载以释放带宽");
            }
        }

        private void TryResumeSequentialDownloadAfterSeekBoost(long version)
        {
            bool shouldResume = false;
            bool preloadOnly = false;

            lock (_downloadLock)
            {
                if (_resumeSequentialAfterSeek && version == _resumeSequentialVersion)
                {
                    shouldResume = true;
                    preloadOnly = _resumeSequentialPreloadOnly;
                    _resumeSequentialAfterSeek = false;
                    _resumeSequentialPreloadOnly = false;
                    _resumeSequentialVersion = 0;
                }
            }

            if (!shouldResume)
            {
                return;
            }

            if (preloadOnly)
            {
                DebugLogger.Log(
                    DebugLogger.LogLevel.Info,
                    "SmartCache",
                    "SeekBoost 结束：检测到预加载模式，跳过顺序下载恢复");
                return;
            }

            DebugLogger.Log(
                DebugLogger.LogLevel.Info,
                "SmartCache",
                "SeekBoost 结束：恢复顺序下载任务");
            StartSequentialDownload(CancellationToken.None, preloadOnly: false);
        }

        /// <summary>
        /// 检查目标块及后续若干块是否都已缓存。
        /// </summary>
        public bool AreChunksReady(long bytePosition, int aheadChunks)
        {
            int startChunk = GetChunkIndex(bytePosition);
            int endChunk = Math.Min(_totalChunks - 1, startChunk + Math.Max(0, aheadChunks));
            for (int i = startChunk; i <= endChunk; i++)
            {
                if (!_cache.ContainsKey(i))
                {
                    return false;
                }
            }
            return true;
        }

        public int Read(long position, byte[] buffer, int offset, int count)
        {
            if (_disposed)
            {
                return 0;
            }

            int startChunk = GetChunkIndex(position);
            long endPosition = Math.Min(position + count, _totalSize);
            int endChunk = GetChunkIndex(endPosition - 1);

            int totalRead = 0;
            long currentPosition = position;

            for (int chunk = startChunk; chunk <= endChunk; chunk++)
            {
                if (!_cache.TryGetValue(chunk, out var data))
                {
                    break;
                }

                long chunkStart = chunk * (long)ChunkSize;
                int chunkOffset = (int)(currentPosition - chunkStart);
                int available = data.Length - chunkOffset;
                if (available <= 0)
                {
                    continue;
                }

                int toCopy = Math.Min(available, count - totalRead);
                Array.Copy(data, chunkOffset, buffer, offset + totalRead, toCopy);

                totalRead += toCopy;
                currentPosition += toCopy;

                if (totalRead >= count)
                {
                    break;
                }
            }

            return totalRead;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            // Initialization may still be waiting for its first complete blocks.
            // Releasing a cache must settle that waiter even if its worker never starts.
            _initialBufferTcs?.TrySetCanceled();

            lock (_seekBoostLock)
            {
                _seekBoostCts?.Cancel();
                _seekBoostCts?.Dispose();
                _seekBoostCts = null;
            }

            Task? mainDownloadTask = _mainDownloadTask;
            CancellationTokenSource? mainDownloadCts = _mainDownloadCts;
            Task? preloadTask = _preloadTask;
            CancellationTokenSource? preloadCts = _preloadCts;

            _mainDownloadTask = null;
            _mainDownloadCts = null;
            _preloadTask = null;
            _preloadCts = null;

            try
            {
                mainDownloadCts?.Cancel();
                preloadCts?.Cancel();
            }
            catch (Exception ex)
            {
                DebugLogger.LogException("SmartCache", ex, "Failed to cancel background download tasks");
            }

            ReleaseBackgroundTask(mainDownloadTask, mainDownloadCts, "main download task");
            ReleaseBackgroundTask(preloadTask, preloadCts, "preload task");

            _scheduler?.Dispose();
            _cache.Clear();
            _rangePreloaderStarted = false;

            try
            {
                _lifecycleCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            _lifecycleCts.Dispose();

            // Stage 3: release smart pre-cache resources.
            _smartPreCache?.Dispose();
            _smartPreCache = null;

            void ReleaseBackgroundTask(Task? task, CancellationTokenSource? cts, string taskName)
            {
                if (cts == null)
                {
                    return;
                }

                if (task != null && !task.IsCompleted)
                {
                    _ = task.ContinueWith(completed =>
                    {
                        try
                        {
                            if (completed.IsFaulted && completed.Exception != null)
                            {
                                DebugLogger.LogException("SmartCache", completed.Exception, $"{taskName} ended with error after cancellation");
                            }
                        }
                        finally
                        {
                            try
                            {
                                cts.Dispose();
                            }
                            catch (ObjectDisposedException)
                            {
                            }
                        }
                    }, TaskScheduler.Default);
                }
                else
                {
                    try
                    {
                        cts.Dispose();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }
            }
        }

        private async Task<bool> InitializeRangeModeAsync(CancellationToken token, bool isPreload)
        {
            SetBufferingState(BufferingState.Buffering);

            // ⭐⭐⭐ 关键优化：只等待 chunk 0 下载完成，立即返回让播放开始
            // 其他 chunks 在后台并发下载，不阻塞播放启动
            DebugLogger.Log(
                DebugLogger.LogLevel.Info,
                "SmartCache",
                "⚡ 快速启动模式：仅等待 chunk 0，其他块后台加载");

            // 下载 chunk 0（必须完成才能播放）
            var chunk0Data = await _downloader.DownloadChunkAsync(0, token).ConfigureAwait(false);
            if (chunk0Data == null)
            {
                DebugLogger.Log(
                    DebugLogger.LogLevel.Error,
                    "SmartCache",
                    "❌ Chunk 0 下载失败，无法初始化");
                SetBufferingState(BufferingState.Buffering);
                return false;
            }

            if (_cache.TryAdd(0, chunk0Data))
            {
                Interlocked.Add(ref _cachedBytes, chunk0Data.Length);
                DebugLogger.Log(
                    DebugLogger.LogLevel.Info,
                    "SmartCache",
                    $"✓ Chunk 0 下载完成 ({chunk0Data.Length:N0} bytes)，立即启动播放");
            }

            if (!isPreload)
            {
                // ⭐ 立即启动后台预加载器（下载 chunk 1, 2, 3...）
                StartRangePreloader(token);
            }
            else
            {
                _isPreloadMode = true;
                _rangePreloaderStarted = false;
            }

            if (!isPreload)
            {
                // ⭐ 后台并发下载接下来的几个 chunks（不等待完成）
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // 并发下载 chunk 1-5（MinReadyChunks + PreloadAheadChunks 的一部分）
                        int backgroundChunks = Math.Min(5, _totalChunks - 1);
                        var backgroundTasks = new List<Task>();

                        for (int i = 1; i <= backgroundChunks; i++)
                        {
                            int chunkIndex = i;
                            backgroundTasks.Add(Task.Run(async () =>
                            {
                                var data = await _downloader.DownloadChunkAsync(chunkIndex, token).ConfigureAwait(false);
                                if (data != null && _cache.TryAdd(chunkIndex, data))
                                {
                                    Interlocked.Add(ref _cachedBytes, data.Length);
                                }
                            }, token));
                        }

                        await Task.WhenAll(backgroundTasks).ConfigureAwait(false);

                        DebugLogger.Log(
                            DebugLogger.LogLevel.Info,
                            "SmartCache",
                            $"✓ 后台初始缓存完成: chunks 1-{backgroundChunks}");
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.LogException("SmartCache", ex, "后台初始缓存失败");
                    }
                }, token);
            }

            ReportProgress();
            SetBufferingState(BufferingState.Ready);
            return true;
        }

        private async Task<bool> InitializeFullDownloadModeAsync(CancellationToken token, bool isPreload)
        {
            SetBufferingState(BufferingState.Buffering);

            _initialBufferSignaled = false;
            _initialBufferTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            StartSequentialDownload(token, isPreload);

            var completion = _initialBufferTcs;
            using (token.Register(() => completion.TrySetCanceled(token)))
            {
                try
                {
                    bool ready = await completion.Task.ConfigureAwait(false);
                    if (ready)
                    {
                        SetBufferingState(BufferingState.Ready);
                    }
                    return ready && !_disposed;
                }
                catch (TaskCanceledException) when (!token.IsCancellationRequested)
                {
                    return !_disposed && CheckCacheHealth(0).IsReady;
                }
            }
        }

        private void StartSequentialDownload(CancellationToken externalToken, bool preloadOnly)
        {
            lock (_downloadLock)
            {
                _mainDownloadCts?.Cancel();
                _mainDownloadCts?.Dispose();

                _mainDownloadCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken, _lifecycleCts.Token);
                var downloadToken = _mainDownloadCts.Token;

                _isPreloadMode = preloadOnly;

                _mainDownloadTask = Task.Run(
                    () => RunSequentialDownloadAsync(downloadToken, preloadOnly),
                    downloadToken);
            }
        }

        private async Task RunSequentialDownloadAsync(CancellationToken token, bool preloadOnly)
        {
            DebugLogger.Log(
                DebugLogger.LogLevel.Info,
                "SmartCache",
                preloadOnly
                    ? "🚀 顺序预加载任务启动（仅首段缓冲）"
                    : "🚀 顺序下载任务启动（完整文件）");

            var tailChunks = new Dictionary<int, byte[]>();
            var initialBuffer = _initialBufferTcs;
            int receivedChunks = 0;
            try
            {
                bool completed = await _downloader.DownloadAllChunksSequentialAsync((chunkIndex, chunk) =>
                {
                    token.ThrowIfCancellationRequested();
                    if (_cache.TryAdd(chunkIndex, chunk))
                    {
                        Interlocked.Add(ref _cachedBytes, chunk.Length);
                        ReportProgress();
                    }
                    receivedChunks++;
                    TrySignalInitialBufferReady();
                    TrackTailChunk(tailChunks, chunkIndex, chunk);
                    return Task.CompletedTask;
                }, null, token, preloadOnly ? MinReadyChunks + 1 : null).ConfigureAwait(false);

                token.ThrowIfCancellationRequested();
                if (completed && _cache.Count == _totalChunks && TotalCachedBytes == _totalSize)
                {
                    if (tailChunks.Count > 0)
                        LastChunkCacheManager.Instance.Add(_songId, _url, _totalSize, tailChunks);
                    _isFullyCached = true;
                    ReportProgress();
                    DebugLogger.Log(DebugLogger.LogLevel.Info, "SmartCache", $"Sequential transfer complete: song={_songId}, bytes={TotalCachedBytes}");
                }
                else if (preloadOnly)
                {
                    DebugLogger.Log(DebugLogger.LogLevel.Info, "SmartCache", $"Preload prefix ready: song={_songId}, chunks={receivedChunks}");
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // An old preload must not cancel the completion source of its successor.
                DebugLogger.Log(DebugLogger.LogLevel.Info, "SmartCache", $"Sequential transfer cancelled: song={_songId}, preload={preloadOnly}");
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    _sequentialFailure = ex;
                    DebugLogger.Log(DebugLogger.LogLevel.Warning, "SmartCache", $"Sequential transfer exhausted: song={_songId}, error={ex.GetType().Name}, cached={TotalCachedBytes}/{_totalSize}");
                    initialBuffer?.TrySetException(ex);
                }
            }
            finally
            {
                // Keep the task identity: a completed old preload must never clear the
                // handle of a newly started full download and spawn duplicate workers.
                if (!_initialBufferSignaled && !token.IsCancellationRequested)
                    initialBuffer?.TrySetResult(CheckCacheHealth(0).IsReady);
            }
        }

        private void TrySignalInitialBufferReady()
        {
            if (_initialBufferSignaled || _initialBufferTcs == null)
            {
                return;
            }

            for (int i = 0; i < Math.Min(MinReadyChunks, _totalChunks); i++)
            {
                if (!_cache.ContainsKey(i))
                {
                    return;
                }
            }

            _initialBufferSignaled = true;
            _initialBufferTcs.TrySetResult(true);
        }

        private void TrackTailChunk(Dictionary<int, byte[]> tailChunks, int chunkIndex, byte[] data)
        {
            if (_totalChunks <= 0)
            {
                return;
            }

            int firstTail = Math.Max(0, _totalChunks - 4);
            if (chunkIndex >= firstTail)
            {
                tailChunks[chunkIndex] = data;
            }

            // 保持最多 4 个块
            while (tailChunks.Count > 4)
            {
                int minKey = int.MaxValue;
                foreach (var key in tailChunks.Keys)
                {
                    if (key < minKey)
                    {
                        minKey = key;
                    }
                }
                tailChunks.Remove(minKey);
            }
        }

        private void StartRangePreloader(CancellationToken externalToken)
        {
            _rangePreloaderStarted = true;
            _scheduler = new PriorityDownloadScheduler(
                _totalChunks,
                PreloadAheadChunks,
                PreloadBehindChunks,
                _cache);
            _scheduler.UpdatePlaybackWindow(_currentChunk);

            _preloadCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
            var preloadToken = _preloadCts.Token;

            _preloadTask = Task.Run(async () =>
            {
                try
                {
                    var activeTasks = new System.Collections.Generic.List<Task>();

                    while (!preloadToken.IsCancellationRequested)
                    {
                        while (activeTasks.Count < MaxPreloadConcurrency &&
                               _scheduler.TryDequeue(out int chunkIndex))
                        {
                            var task = DownloadPreloadChunkAsync(chunkIndex, preloadToken);
                            activeTasks.Add(task);
                        }

                        if (activeTasks.Count == 0)
                        {
                            await Task.Delay(150, preloadToken).ConfigureAwait(false);
                        }
                        else
                        {
                            var completed = await Task.WhenAny(activeTasks).ConfigureAwait(false);
                            activeTasks.Remove(completed);
                        }
                    }

                    await Task.WhenAll(activeTasks).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                finally
                {
                    _scheduler.Reset();
                }
            }, preloadToken);
        }

        private async Task DownloadPreloadChunkAsync(int chunkIndex, CancellationToken token)
        {
            try
            {
                byte[]? data = await _downloader.DownloadChunkAsync(chunkIndex, token).ConfigureAwait(false);
                if (data == null)
                {
                    _scheduler?.MarkFailed(chunkIndex);
                    return;
                }

                if (_cache.TryAdd(chunkIndex, data))
                {
                    Interlocked.Add(ref _cachedBytes, data.Length);
                    ReportProgress();
                }

                _scheduler?.MarkCompleted(chunkIndex);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                DebugLogger.LogException("SmartCache", ex, $"预加载块 {chunkIndex} 出错");
                _scheduler?.MarkFailed(chunkIndex);
            }
        }

        private async Task<byte[]?> DownloadChunkOnDemandAsync(int chunkIndex, CancellationToken token)
        {
            try
            {
                byte[]? data = await _downloader.DownloadChunkAsync(chunkIndex, token).ConfigureAwait(false);
                if (data != null)
                {
                    if (_cache.TryAdd(chunkIndex, data))
                    {
                        Interlocked.Add(ref _cachedBytes, data.Length);
                        ReportProgress();
                    }
                    _rangeFailureCount = 0; // 成功清零
                }
                else
                {
                    HandleRangeFailure();
                }

                return data;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception)
            {
                HandleRangeFailure();
                throw;
            }
        }

        private void HandleRangeFailure()
        {
            if (_strategy != DownloadStrategy.Range)
            {
                return;
            }

            int failures = Interlocked.Increment(ref _rangeFailureCount);
            if (!_rangeFallbackTriggered && failures >= RangeFailureThreshold)
            {
                TriggerRangeFallback();
            }
        }

        private void TriggerRangeFallback()
        {
            lock (_downloadLock)
            {
                if (_rangeFallbackTriggered || _strategy != DownloadStrategy.Range)
                {
                    return;
                }

                _rangeFallbackTriggered = true;
                _strategy = DownloadStrategy.SequentialFull;

                DebugLogger.Log(
                    DebugLogger.LogLevel.Warning,
                    "SmartCache",
                    "⚠️ Range 多次失败，切换为顺序整流模式以稳定播放");

                _preloadCts?.Cancel();
                StartSequentialDownload(CancellationToken.None, preloadOnly: false);
            }
        }

        private Task OnChunkReadyAsync(int chunkIndex, byte[] data)
        {
            // ⭐⭐⭐ 关键修复：验证最后chunk的完整性，防止不完整chunk进入缓存
            int lastChunkIndex = _totalChunks - 1;
            if (chunkIndex == lastChunkIndex)
            {
                // 计算最后chunk的预期大小
                long lastChunkStart = lastChunkIndex * (long)ChunkSize;
                int expectedLastChunkSize = (int)(_totalSize - lastChunkStart);

                if (data.Length < expectedLastChunkSize)
                {
                    // 最后chunk不完整，记录警告但拒绝添加到缓存
                    DebugLogger.Log(
                        DebugLogger.LogLevel.Warning,
                        "SmartCache",
                        $"⚠️ 拒绝不完整的最后块 {chunkIndex}: {data.Length} bytes < {expectedLastChunkSize} bytes (缺少 {expectedLastChunkSize - data.Length} bytes)");
                    return Task.CompletedTask; // 不添加到缓存，让顺序下载继续
                }
            }

            if (_cache.TryAdd(chunkIndex, data))
            {
                Interlocked.Add(ref _cachedBytes, data.Length);
                ReportProgress();
            }

            // ⭐⭐⭐ 关键修复：只有在前 MinReadyChunks 个**连续**块和最后一个块都已下载时才报告 Ready
            // 这确保 BASS 初始化时 seek 到文件末尾不会失败，同时确保前几块数据完整
            if (_initialBufferTcs != null && !_initialBufferTcs.Task.IsCompleted)
            {
                // 检查前 MinReadyChunks 个块是否都存在（连续的块0, 1, 2, ...）
                bool hasFirstChunks = true;
                for (int i = 0; i < Math.Min(MinReadyChunks, _totalChunks); i++)
                {
                    if (!_cache.ContainsKey(i))
                    {
                        hasFirstChunks = false;
                        break;
                    }
                }

                bool hasLastChunk = _cache.ContainsKey(lastChunkIndex);

                if (hasFirstChunks && hasLastChunk)
                {
                    DebugLogger.Log(
                        DebugLogger.LogLevel.Info,
                        "SmartCache",
                        $"✓ 缓存就绪: 前{MinReadyChunks}块(0-{MinReadyChunks-1}) + 最后块({lastChunkIndex}) 均已下载");
                    _initialBufferTcs.TrySetResult(true);
                }
            }

            return Task.CompletedTask;
        }

        private async Task<DownloadStrategy> DetectStrategyAsync(CancellationToken token)
        {
            bool supportsRange = await EnsureRangeSupportAsync(token).ConfigureAwait(false);

            if (_preferSequentialFull)
            {
                DebugLogger.Log(
                    DebugLogger.LogLevel.Info,
                    "SmartCache",
                    "启用顺序整流优先策略（高码率/大文件）");
                return DownloadStrategy.SequentialFull;
            }

            if (supportsRange)
            {
                return DownloadStrategy.Range;
            }

            if (_totalSize <= 50 * 1024 * 1024)
            {
                return DownloadStrategy.ParallelFull;
            }

            return DownloadStrategy.SequentialFull;
        }

        private string GetStrategyCacheKey()
        {
            try
            {
                var uri = new Uri(_url);
                return $"{uri.Host}|sequential={_preferSequentialFull}|large={_totalSize > 50L * 1024 * 1024}";
            }
            catch
            {
                return _url;
            }
        }

        private int GetChunkIndex(long bytePosition)
        {
            if (bytePosition <= 0)
            {
                return 0;
            }

            if (bytePosition >= _totalSize)
            {
                return _totalChunks - 1;
            }

            return (int)(bytePosition / ChunkSize);
        }

        private void ReportProgress()
        {
            int percent = _totalSize == 0
                ? 0
                : (int)Math.Min(100, (TotalCachedBytes * 100L) / _totalSize);

            ReportProgress(percent);
        }

        private void ReportProgress(int percent)
        {
            BufferingProgressChanged?.Invoke(this, percent);
        }

        public void SetPlayingState()
        {
            SetBufferingState(BufferingState.Playing);
            EnsureActiveDownload();
        }

        public async Task<bool> WaitForCacheReadyAsync(
            long position,
            bool forPlayback,
            CancellationToken token)
        {
            SetBufferingState(BufferingState.Buffering);

            int targetChunk = GetChunkIndex(position);
            int requiredBase = forPlayback ? MinReadyChunks : 1;
            int required = Math.Min(requiredBase, Math.Max(1, _totalChunks - targetChunk));

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(WaitForCacheReadyHardTimeoutMs));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token, _lifecycleCts.Token);

            try
            {
                while (true)
                {
                    linkedCts.Token.ThrowIfCancellationRequested();

                    var health = CheckCacheHealth(position, forPlayback);
                    if (health.ReadyChunks >= required)
                    {
                        SetBufferingState(forPlayback ? BufferingState.Ready : BufferingState.Buffering);
                        return true;
                    }

                    await Task.Delay(HealthPollDelayMs, linkedCts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                DebugLogger.Log(
                    DebugLogger.LogLevel.Warning,
                    "SmartCache",
                    $"WaitForCacheReadyAsync timeout at position {position:N0} after {WaitForCacheReadyHardTimeoutMs}ms");
            }
            catch (OperationCanceledException)
            {
                // Cancellation requested by caller or disposal; fall through to final health check.
            }

            return CheckCacheHealth(position, forPlayback).ReadyChunks >= required;
        }

        public async Task<int> ReadAsync(
            long position,
            byte[] buffer,
            int offset,
            int count,
            CancellationToken token,
            bool waitIfNotReady = true)
        {
            token.ThrowIfCancellationRequested();
            // Merge any staged pre-cache blocks before reading from primary cache.
            CheckAndMergePreCache(position);

            int bytesRead = Read(position, buffer, offset, count);
            if (bytesRead > 0 || !waitIfNotReady)
            {
                return bytesRead;
            }

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(ReadAsyncHardTimeoutMs));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token, _lifecycleCts.Token);

            try
            {
                if (_sequentialFailure != null) throw new System.IO.IOException("Audio download exhausted its recovery budget", _sequentialFailure);
                PrioritizeDestination(position);
                EnsureActiveDownload();
                await EnsurePositionAsync(position, linkedCts.Token, allowRangeRescue: true).ConfigureAwait(false);

                bytesRead = Read(position, buffer, offset, count);
                if (bytesRead > 0)
                {
                    return bytesRead;
                }

                while (!linkedCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(HealthPollDelayMs, linkedCts.Token).ConfigureAwait(false);

                    if (_sequentialFailure != null) throw new System.IO.IOException("Audio download exhausted its recovery budget", _sequentialFailure);
                    // Retry after each poll and merge newly prepared pre-cache chunks.
                    CheckAndMergePreCache(position);
                    PrioritizeDestination(position);
                    EnsureActiveDownload();
                    await EnsurePositionAsync(position, linkedCts.Token, allowRangeRescue: true).ConfigureAwait(false);

                    bytesRead = Read(position, buffer, offset, count);
                    if (bytesRead > 0)
                    {
                        return bytesRead;
                    }
                }
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                DebugLogger.Log(
                    DebugLogger.LogLevel.Warning,
                    "SmartCache",
                    $"ReadAsync timeout at position {position:N0} after {ReadAsyncHardTimeoutMs}ms");
            }
            catch (OperationCanceledException)
            {
                // Caller cancellation or disposal.
            }

            return bytesRead;
        }

        /// <summary>
        /// ⭐⭐⭐ 检查并合并预缓存数据到主缓存（关键修复）
        /// </summary>
        private void CheckAndMergePreCache(long position)
        {
            if (_smartPreCache != null && _smartPreCache.IsPositionPreCached(position, _totalSize, ChunkSize, out var segment))
            {
                if (segment == null)
                {
                    return;
                }

                // 将预缓存的块合并到主缓存
                int mergedCount = 0;
                foreach (var kvp in segment.Chunks)
                {
                    if (_cache.TryAdd(kvp.Key, kvp.Value))
                    {
                        Interlocked.Add(ref _cachedBytes, kvp.Value.Length);
                        mergedCount++;
                    }
                }

                if (mergedCount > 0)
                {
                    DebugLogger.Log(
                        DebugLogger.LogLevel.Info,
                        "SmartCache",
                        $"✨ 预缓存命中！位置: {position:N0}, 合并块数: {mergedCount}");
                }
            }
        }

        private void PrioritizeDestination(long position)
        {
            if (_strategy != DownloadStrategy.Range || _scheduler == null)
            {
                return;
            }

            int chunkIndex = GetChunkIndex(position);
            int radius = Math.Max(2, PreloadAheadChunks / 2);
            _scheduler.BoostChunkPriority(chunkIndex, radius);
        }

        public async Task<bool> SeekAsync(long position, CancellationToken token)
        {
            int chunkIndex = GetChunkIndex(position);
            UpdatePlaybackPosition(position);

            // ⭐ 使用统一的预缓存合并方法
            CheckAndMergePreCache(position);
            PrioritizeDestination(position);
            EnsureActiveDownload();

            // ⭐⭐⭐ 关键修复：如果 seek 到接近结尾（>90%），立即触发末尾 chunks 的优先下载
            // 避免用户 seek 到结尾时，BASS 读取末尾 chunks 时缓存还没准备好
            if (_totalSize > 0)
            {
                double progress = (double)position / _totalSize;
                if (progress >= 0.90)
                {
                    int lastChunkIndex = GetChunkIndex(_totalSize - 1);
                    int startChunk = Math.Max(chunkIndex + 1, lastChunkIndex - 2);

                    // 立即请求最后 3 个 chunks 的下载（异步，不阻塞当前 seek）
                    for (int i = startChunk; i <= lastChunkIndex; i++)
                    {
                        int chunkToDownload = i; // 捕获循环变量
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await EnsureChunkAsync(chunkToDownload, CancellationToken.None, allowRangeRescue: true).ConfigureAwait(false);
                                DebugLogger.Log(
                                    DebugLogger.LogLevel.Info,
                                    "SmartCache",
                                    $"✓ Seek触发：末尾chunk {chunkToDownload} 已下载");
                            }
                            catch (Exception ex)
                            {
                                DebugLogger.LogException("SmartCache", ex, $"Seek触发末尾chunk {chunkToDownload} 下载失败");
                            }
                        });
                    }

                    DebugLogger.Log(
                        DebugLogger.LogLevel.Info,
                        "SmartCache",
                        $"⚡ Seek到{progress:P1}，已触发末尾chunks [{startChunk}, {lastChunkIndex}] 优先下载");
                }
            }

            await EnsureChunkAsync(chunkIndex, token, allowRangeRescue: true).ConfigureAwait(false);
            return true;
        }

        private async Task EnsureChunkRescueAsync(int chunkIndex, CancellationToken token)
        {
            if (chunkIndex < 0 || chunkIndex >= _totalChunks)
            {
                return;
            }

            bool supportsRange = await EnsureRangeSupportAsync(token).ConfigureAwait(false);
            if (!supportsRange)
            {
                return;
            }

            await DownloadChunkOnDemandAsync(chunkIndex, token).ConfigureAwait(false);
        }

        private async Task<bool> EnsureRangeSupportAsync(CancellationToken token)
        {
            if (_rangeSupportChecked)
            {
                return _supportsRange;
            }

            await _rangeSupportLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (_rangeSupportChecked)
                {
                    return _supportsRange;
                }

                bool supportsRange = false;
                try
                {
                    var (rangeHint, _) = await HttpRangeHelper.CheckRangeSupportAsync(
                        _url,
                        _httpClient,
                        token,
                        _headers).ConfigureAwait(false);
                    supportsRange = rangeHint;

                    if (supportsRange)
                    {
                        bool rangeVerified = await HttpRangeHelper.TestRangeRequestAsync(
                            _url,
                            _httpClient,
                            token,
                            _headers).ConfigureAwait(false);
                        supportsRange = supportsRange && rangeVerified;
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.LogException("SmartCache", ex, "检测 Range 能力失败");
                    supportsRange = false;
                }

                _supportsRange = supportsRange;
                _rangeSupportChecked = true;
                return supportsRange;
            }
            finally
            {
                _rangeSupportLock.Release();
            }
        }

        private void SetBufferingState(BufferingState newState)
        {
            bool changed = false;
            lock (_bufferingLock)
            {
                if (_bufferingState != newState)
                {
                    _bufferingState = newState;
                    changed = true;
                }
            }

            if (changed)
            {
                BufferingStateChanged?.Invoke(this, newState);
            }
        }

        private void EnsureNotDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SmartCacheManager));
            }
        }

        private void EnsureActiveDownload()
        {
            if (_strategy == DownloadStrategy.SequentialFull ||
                _strategy == DownloadStrategy.ParallelFull)
            {
                bool shouldStartFullDownload = false;

                lock (_downloadLock)
                {
                    if (_isPreloadMode)
                    {
                        bool noActiveDownload = _mainDownloadTask == null ||
                                                _mainDownloadTask.IsCompleted ||
                                                _mainDownloadTask.IsCanceled ||
                                                _mainDownloadTask.IsFaulted;

                        if (noActiveDownload)
                        {
                            _isPreloadMode = false;
                            shouldStartFullDownload = true;
                        }
                    }
                }

                if (shouldStartFullDownload)
                {
                    DebugLogger.Log(
                        DebugLogger.LogLevel.Info,
                        "SmartCache",
                        "🎬 播放开始，启动完整文件下载");

                    StartSequentialDownload(CancellationToken.None, preloadOnly: false);
                }
            }
            else if (_strategy == DownloadStrategy.Range)
            {
                bool startPreloader = false;

                lock (_downloadLock)
                {
                    if (!_rangePreloaderStarted)
                    {
                        _rangePreloaderStarted = true;
                        startPreloader = true;
                    }
                }

                if (startPreloader)
                {
                    DebugLogger.Log(
                        DebugLogger.LogLevel.Info,
                        "SmartCache",
                        "🎬 播放开始，启动区间调度下载");

                    StartRangePreloader(CancellationToken.None);
                }
            }
        }
    }
}
