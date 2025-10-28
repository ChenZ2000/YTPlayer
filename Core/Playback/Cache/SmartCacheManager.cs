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
        private const int HealthPollDelayMs = 120;

        // ⭐ Strategy detection cache per domain (reduces redundant HEAD requests)
        private static readonly ConcurrentDictionary<string, DownloadStrategy> _strategyCache
            = new ConcurrentDictionary<string, DownloadStrategy>();

        private readonly string _songId;  // 🎯 歌曲ID，用于预缓存系统
        private readonly string _url;
        private readonly long _totalSize;
        private readonly int _totalChunks;
        private readonly HttpClient _httpClient;
        private readonly ConcurrentDictionary<int, byte[]> _cache;
        private readonly ChunkDownloadManager _downloader;

        private PriorityDownloadScheduler? _scheduler;
        private CancellationTokenSource? _preloadCts;
        private Task? _preloadTask;
        private TaskCompletionSource<bool>? _initialBufferTcs;

        // ⭐ 阶段3：智能预缓存和带宽管理
        private SmartPreCacheManager? _smartPreCache;
        private BandwidthAllocator? _bandwidthAllocator;

        private DownloadStrategy _strategy = DownloadStrategy.SequentialFull;
        private long _cachedBytes;
        private int _currentChunk;
        private bool _disposed;

        private readonly object _stateLock = new object();
        private readonly object _bufferingLock = new object();
        private BufferingState _bufferingState = BufferingState.Idle;

        public SmartCacheManager(string songId, string url, long totalSize, HttpClient httpClient)
        {
            _songId = songId ?? string.Empty;  // 🎯 允许空字符串（用于不需要预缓存的场景）
            _url = url ?? throw new ArgumentNullException(nameof(url));
            _totalSize = totalSize > 0 ? totalSize : throw new ArgumentOutOfRangeException(nameof(totalSize));
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

            _totalChunks = (int)Math.Ceiling(totalSize / (double)ChunkSize);
            _cache = new ConcurrentDictionary<int, byte[]>();
            _downloader = new ChunkDownloadManager(url, totalSize, ChunkSize, _httpClient);
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

        /// <summary>
        /// 初始化缓存管理器
        /// </summary>
        /// <param name="token">取消令牌</param>
        /// <param name="isPreload">是否为预加载场景（预加载只需要 Chunk0，不需要最后块）</param>
        public async Task<bool> InitializeAsync(CancellationToken token, bool isPreload = false)
        {
            EnsureNotDisposed();

            // ⚡⚡⚡ 网易云音乐所有 CDN 都不支持 Range 请求
            // 直接使用 SequentialFull 策略，跳过 HEAD 请求检测（节省 100-300ms）
            _strategy = DownloadStrategy.SequentialFull;

            DebugLogger.Log(
                DebugLogger.LogLevel.Info,
                "SmartCache",
                $"⚡ [优化] 直接使用 SequentialFull 策略（网易云不支持 Range）");

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
                    initResult = await InitializeRangeModeAsync(token).ConfigureAwait(false);
                    break;

                case DownloadStrategy.ParallelFull:
                case DownloadStrategy.SequentialFull:
                    initResult = await InitializeFullDownloadModeAsync(token, isPreload).ConfigureAwait(false);

                    // ⭐ 阶段3：对于大文件（>100MB），启动智能预缓存
                    if (initResult && _totalSize > 100 * 1024 * 1024)
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
            CancellationToken token)
        {
            var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMilliseconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);

            try
            {
                while (true)
                {
                    linkedCts.Token.ThrowIfCancellationRequested();

                    var health = CheckCacheHealth(bytePosition);
                    if (health.IsReady)
                    {
                        return true;
                    }

                    await Task.Delay(HealthPollDelayMs, linkedCts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return CheckCacheHealth(bytePosition).IsReady;
            }
        }

        public bool IsChunkCached(int chunkIndex)
        {
            return _cache.ContainsKey(chunkIndex);
        }

        public Task EnsureChunkAsync(int chunkIndex, CancellationToken token)
        {
            if (_cache.ContainsKey(chunkIndex))
            {
                return Task.CompletedTask;
            }

            if (_strategy != DownloadStrategy.Range)
            {
                return Task.CompletedTask;
            }

            return DownloadChunkOnDemandAsync(chunkIndex, token);
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

            try
            {
                _preloadCts?.Cancel();
                _preloadTask?.Wait(TimeSpan.FromSeconds(1));
            }
            catch (AggregateException ex)
            {
                foreach (var inner in ex.InnerExceptions)
                {
                    DebugLogger.LogException("SmartCache", inner, "预加载任务结束时出现异常");
                }
            }
            catch (Exception ex)
            {
                DebugLogger.LogException("SmartCache", ex, "释放预加载任务时发生异常");
            }
            finally
            {
                _preloadCts?.Dispose();
                _preloadTask = null;
            }

            _scheduler?.Dispose();
            _cache.Clear();

            // ⭐ 阶段3：清理智能预缓存
            _smartPreCache?.Dispose();
            _smartPreCache = null;
        }

        private async Task<bool> InitializeRangeModeAsync(CancellationToken token)
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

            // ⭐ 立即启动后台预加载器（下载 chunk 1, 2, 3...）
            StartRangePreloader(token);

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

            ReportProgress();
            SetBufferingState(BufferingState.Ready);
            return true;
        }

        private async Task<bool> InitializeFullDownloadModeAsync(CancellationToken token, bool isPreload)
        {
            SetBufferingState(BufferingState.Buffering);

            _initialBufferTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _preloadCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var preloadToken = _preloadCts.Token;

            // ⭐⭐⭐ 快速启动优化：优先下载 Chunk 0，立即初始化播放
            // BASS 初始化只需要：
            // 1. Chunk 0 - 验证格式并开始播放（必需）
            // 2. 最后块 - 验证文件完整性（可选，改为后台下载）
            //
            // 修复：移除最后块的强制要求，避免大文件初始化失败
            // 原因：网易云不支持 Range，下载最后块需要"假跳转"整个文件，
            //       对于大文件（>100MB）可能需要 10+ 秒，导致启动缓慢或失败

            var chunk0DownloadTask = Task.Run(async () =>
            {
                try
                {
                    DebugLogger.Log(
                        DebugLogger.LogLevel.Info,
                        "SmartCache",
                        "⚡ [并发优化] 开始优先下载 Chunk 0，以支持 BASS 初始化");

                    var stopwatch = Stopwatch.StartNew();

                    // 使用 Range 请求下载 Chunk 0（即使服务器不支持完整 Range，通常也支持从头开始的请求）
                    byte[]? chunk0Data = await _downloader.DownloadChunkAsync(0, preloadToken).ConfigureAwait(false);

                    stopwatch.Stop();

                    if (chunk0Data != null)
                    {
                        await OnChunkReadyAsync(0, chunk0Data).ConfigureAwait(false);
                        DebugLogger.Log(
                            DebugLogger.LogLevel.Info,
                            "SmartCache",
                            $"⚡ ✓ Chunk 0 下载完成，耗时 {stopwatch.ElapsedMilliseconds}ms，大小 {chunk0Data.Length} bytes");
                    }
                    else
                    {
                        DebugLogger.Log(
                            DebugLogger.LogLevel.Warning,
                            "SmartCache",
                            "⚡ ✗ Chunk 0 下载失败");
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.LogException("SmartCache", ex, "⚡ Chunk 0 下载失败");
                }
            }, preloadToken);

            var lastChunkDownloadTask = Task.Run(async () =>
            {
                try
                {
                    int lastChunkIndex = _totalChunks - 1;

                    // 🎯🎯🎯 检查预缓存系统是否有最后块
                    var cachedData = LastChunkCacheManager.Instance.TryGet(_songId, _url, _totalSize);
                    if (cachedData != null && cachedData.Chunks.Count > 0)
                    {
                        DebugLogger.Log(
                            DebugLogger.LogLevel.Info,
                            "SmartCache",
                            $"🎯 [预缓存命中] 使用预缓存的最后块 (共 {cachedData.Chunks.Count} 块)");

                        // 直接使用预缓存的块
                        foreach (var kvp in cachedData.Chunks)
                        {
                            await OnChunkReadyAsync(kvp.Key, kvp.Value).ConfigureAwait(false);
                        }

                        DebugLogger.Log(
                            DebugLogger.LogLevel.Info,
                            "SmartCache",
                            "🎯 ✓ 预缓存最后块加载完成（0ms，即时加载）");

                        return;  // 跳过下载
                    }

                    DebugLogger.Log(
                        DebugLogger.LogLevel.Info,
                        "SmartCache",
                        $"⚡ [并发优化] 开始预下载最后块 (块索引 {lastChunkIndex})，以支持 BASS 初始化");

                    var stopwatch = Stopwatch.StartNew();
                    var skipDownloader = new StreamSkipDownloader(_httpClient, _url, _totalSize);
                    var skipProgress = new Progress<(long Current, long Total, bool IsSkipping)>(p =>
                    {
                        if (p.IsSkipping && p.Current % (64 * 1024 * 1024) == 0) // 每 64MB 报告一次
                        {
                            DebugLogger.Log(
                                DebugLogger.LogLevel.Info,
                                "StreamSkip",
                                $"⚡ 跳转进度: {p.Current:N0} / {p.Total:N0} ({p.Current * 100.0 / p.Total:F1}%)");
                        }
                    });

                    bool success = await skipDownloader.DownloadLastChunksAsync(
                        lastNChunks: 2, // 下载最后 2 个块以确保安全
                        chunkSize: ChunkSize,
                        onChunkReady: OnChunkReadyAsync,
                        progress: skipProgress,
                        token: preloadToken).ConfigureAwait(false);

                    stopwatch.Stop();

                    if (success)
                    {
                        DebugLogger.Log(
                            DebugLogger.LogLevel.Info,
                            "SmartCache",
                            $"⚡ ✓ 最后块预下载完成，耗时 {stopwatch.ElapsedMilliseconds}ms");
                    }
                    else
                    {
                        DebugLogger.Log(
                            DebugLogger.LogLevel.Warning,
                            "SmartCache",
                            "⚡ ✗ 最后块预下载失败，可能影响播放初始化");
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.LogException("SmartCache", ex, "⚡ 预下载最后块失败");
                }
            }, preloadToken);

            // ⭐⭐⭐ 快速启动：只等待 Chunk 0，立即初始化 BASS
            // 修复：不再等待最后块，避免大文件初始化超时
            // 最后块在后台下载，不阻塞播放启动
            var criticalChunksTask = Task.Run(async () =>
            {
                try
                {
                    var overallStopwatch = Stopwatch.StartNew();
                    DebugLogger.Log(
                        DebugLogger.LogLevel.Info,
                        "SmartCache",
                        "⚡ [快速启动] 等待 Chunk 0 下载完成（最后块在后台下载）...");

                    // 🎯 关键修复：只等待 Chunk 0，添加 10 秒超时保护
                    using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                    using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(preloadToken, timeoutCts.Token))
                    {
                        try
                        {
                            await chunk0DownloadTask.ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                        {
                            DebugLogger.Log(
                                DebugLogger.LogLevel.Warning,
                                "SmartCache",
                                "⚡ ✗ Chunk 0 下载超时（10秒），初始化失败");
                            _initialBufferTcs?.TrySetResult(false);
                            return;
                        }
                    }

                    overallStopwatch.Stop();

                    // 检查 Chunk 0 是否成功下载
                    bool hasChunk0 = _cache.ContainsKey(0);

                    DebugLogger.Log(
                        DebugLogger.LogLevel.Info,
                        "SmartCache",
                        $"⚡ ✓✓✓ Chunk 0 下载完成，总耗时 {overallStopwatch.ElapsedMilliseconds}ms，BASS 现在可以初始化了！");

                    DebugLogger.Log(
                        DebugLogger.LogLevel.Info,
                        "SmartCache",
                        $"⚡ 初始化就绪检查: Chunk0={hasChunk0}, Ready={hasChunk0} (最后块在后台下载)");

                    _initialBufferTcs?.TrySetResult(hasChunk0);

                    if (hasChunk0)
                    {
                        SetBufferingState(BufferingState.Ready);
                    }
                }
                catch (OperationCanceledException)
                {
                    _initialBufferTcs?.TrySetCanceled();
                }
                catch (Exception ex)
                {
                    DebugLogger.LogException("SmartCache", ex, "⚡ Chunk 0 下载任务异常");
                    _initialBufferTcs?.TrySetException(ex);
                }
            }, preloadToken);

            // ⭐⭐⭐ 后台顺序下载任务：在 Chunk 0 下载完成后，继续在后台填充剩余的块
            // 这个任务不影响 BASS 初始化，但会逐步填充完整文件以支持播放和 seek
            // 修复：最后块的下载也在此后台任务中完成（与顺序下载并行）
            _preloadTask = Task.Run(async () =>
            {
                try
                {
                    DebugLogger.Log(
                        DebugLogger.LogLevel.Info,
                        "SmartCache",
                        "⚡ [后台下载] 启动后台任务：顺序下载剩余块 + 最后块预下载...");

                    var progress = new Progress<double>(p => ReportProgress((int)(p * 100)));

                    // 🎯 并行启动两个后台任务：
                    // 1. 顺序下载所有块（从 chunk 1 开始）
                    // 2. 最后块预下载（用于文件完整性验证）
                    var sequentialDownloadTask = Task.Run(async () =>
                    {
                        try
                        {
                            if (_strategy == DownloadStrategy.ParallelFull)
                            {
                                bool success = await _downloader.DownloadAllChunksParallelAsync(
                                    maxConnections: MaxPreloadConcurrency,
                                    onChunkReady: OnChunkReadyAsync,
                                    progress: progress,
                                    token: preloadToken).ConfigureAwait(false);

                                if (!success)
                                {
                                    DebugLogger.Log(
                                        DebugLogger.LogLevel.Warning,
                                        "SmartCache",
                                        "⚡ Parallel 模式失败，降级为顺序全量下载");

                                    await _downloader.DownloadAllChunksSequentialAsync(
                                        OnChunkReadyAsync,
                                        progress,
                                        preloadToken).ConfigureAwait(false);
                                }
                            }
                            else
                            {
                                await _downloader.DownloadAllChunksSequentialAsync(
                                    OnChunkReadyAsync,
                                    progress,
                                    preloadToken).ConfigureAwait(false);
                            }

                            DebugLogger.Log(
                                DebugLogger.LogLevel.Info,
                                "SmartCache",
                                "⚡ ✓ 后台顺序下载完成，文件已完全缓存");
                        }
                        catch (OperationCanceledException)
                        {
                            DebugLogger.Log(
                                DebugLogger.LogLevel.Info,
                                "SmartCache",
                                "⚡ 后台顺序下载被取消");
                        }
                        catch (Exception ex)
                        {
                            DebugLogger.LogException("SmartCache", ex, "⚡ 后台顺序下载异常");
                        }
                    }, preloadToken);

                    // 等待两个后台任务完成（或其中一个取消）
                    await Task.WhenAny(
                        Task.WhenAll(sequentialDownloadTask, lastChunkDownloadTask),
                        Task.Delay(Timeout.Infinite, preloadToken)).ConfigureAwait(false);

                    DebugLogger.Log(
                        DebugLogger.LogLevel.Info,
                        "SmartCache",
                        "⚡ ✓ 后台下载任务完成");
                }
                catch (OperationCanceledException)
                {
                    DebugLogger.Log(
                        DebugLogger.LogLevel.Info,
                        "SmartCache",
                        "⚡ 后台下载任务被取消（正常，用户可能切换了歌曲）");
                }
                catch (Exception ex)
                {
                    DebugLogger.LogException("SmartCache", ex, "⚡ 后台下载任务异常");
                }
            }, preloadToken);

            using (token.Register(() => _initialBufferTcs.TrySetCanceled()))
            using (preloadToken.Register(() => _initialBufferTcs.TrySetCanceled()))
            {
                try
                {
                    bool ready = await _initialBufferTcs.Task.ConfigureAwait(false);
                    if (ready)
                    {
                        SetBufferingState(BufferingState.Ready);
                    }
                    return ready;
                }
                catch (TaskCanceledException)
                {
                    int lastChunkIndex = _totalChunks - 1;

                    // 检查前 MinReadyChunks 个块是否都存在（连续的块0, 1, 2, ...）
                    bool hasFirstChunks = true;
                    for (int i = 0; i < MinReadyChunks; i++)
                    {
                        if (!_cache.ContainsKey(i))
                        {
                            hasFirstChunks = false;
                            break;
                        }
                    }

                    bool hasLastChunk = _cache.ContainsKey(lastChunkIndex);
                    bool hasFirstAndLast = hasFirstChunks && hasLastChunk;

                    if (hasFirstAndLast)
                    {
                        SetBufferingState(BufferingState.Ready);
                    }
                    return hasFirstAndLast;
                }
            }
        }

        private void StartRangePreloader(CancellationToken externalToken)
        {
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
                    _cache.TryAdd(chunkIndex, data);
                    Interlocked.Add(ref _cachedBytes, data.Length);
                    ReportProgress();
                }

                return data;
            }
            catch (OperationCanceledException)
            {
                return null;
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
                for (int i = 0; i < MinReadyChunks; i++)
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
            try
            {
                var (supportsRange, _) = await HttpRangeHelper.CheckRangeSupportAsync(
                    _url,
                    _httpClient,
                    token).ConfigureAwait(false);

                if (supportsRange)
                {
                    bool rangeVerified = await HttpRangeHelper.TestRangeRequestAsync(
                        _url,
                        _httpClient,
                        token).ConfigureAwait(false);

                    if (rangeVerified)
                    {
                        return DownloadStrategy.Range;
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.LogException("SmartCache", ex, "检测 Range 能力失败");
            }

            if (_totalSize <= 50 * 1024 * 1024)
            {
                return DownloadStrategy.ParallelFull;
            }

            return DownloadStrategy.SequentialFull;
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
        }

        public async Task<bool> WaitForCacheReadyAsync(
            long position,
            bool forPlayback,
            CancellationToken token)
        {
            SetBufferingState(BufferingState.Buffering);

            int required = forPlayback ? MinReadyChunks : 1;

            while (!token.IsCancellationRequested)
            {
                var health = CheckCacheHealth(position);
                if (health.ReadyChunks >= required)
                {
                    SetBufferingState(forPlayback ? BufferingState.Ready : BufferingState.Buffering);
                    return true;
                }

                await Task.Delay(HealthPollDelayMs, token).ConfigureAwait(false);
            }

            return CheckCacheHealth(position).ReadyChunks >= required;
        }

        public async Task<int> ReadAsync(
            long position,
            byte[] buffer,
            int offset,
            int count,
            CancellationToken token,
            bool waitIfNotReady = true)
        {
            // ⭐⭐⭐ 关键修复：在读取前检查预缓存并合并到主缓存
            CheckAndMergePreCache(position);

            int bytesRead = Read(position, buffer, offset, count);
            if (bytesRead > 0 || !waitIfNotReady)
            {
                return bytesRead;
            }

            while (waitIfNotReady && !token.IsCancellationRequested)
            {
                await Task.Delay(HealthPollDelayMs, token).ConfigureAwait(false);

                // ⭐ 每次重试前都检查预缓存
                CheckAndMergePreCache(position);

                bytesRead = Read(position, buffer, offset, count);
                if (bytesRead > 0)
                {
                    return bytesRead;
                }
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

        public async Task<bool> SeekAsync(long position, CancellationToken token)
        {
            int chunkIndex = GetChunkIndex(position);
            UpdatePlaybackPosition(position);

            // ⭐ 使用统一的预缓存合并方法
            CheckAndMergePreCache(position);

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
                                await EnsureChunkAsync(chunkToDownload, CancellationToken.None).ConfigureAwait(false);
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

            await EnsureChunkAsync(chunkIndex, token).ConfigureAwait(false);
            return true;
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
    }
}
