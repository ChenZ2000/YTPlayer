using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using YTPlayer.Core.Data;
using YTPlayer.Utils;

namespace YTPlayer.Core.Memory
{
    /// <summary>
    /// 缓存垃圾回收器
    /// 管理内存占用，在超过限制时自动清理缓存
    /// </summary>
    public sealed class CacheGarbageCollector
    {
        /// <summary>
        /// 软限制：1.2GB（给予20%缓冲空间）
        /// </summary>
        private const long SOFT_LIMIT_BYTES = 1200L * 1024 * 1024;

        /// <summary>
        /// 检查间隔：30秒
        /// </summary>
        private const int CHECK_INTERVAL_SECONDS = 30;

        private readonly MemoryPressureMonitor _monitor;
        private CancellationTokenSource _monitoringCts;
        private Task _monitoringTask;
        private bool _isMonitoring;

        private readonly object _lock = new object();

        /// <summary>
        /// 获取当前正在播放的歌曲 ID（由外部设置）
        /// </summary>
        public string CurrentPlayingSongId { get; set; }

        /// <summary>
        /// 获取预测的下一首歌曲 ID（由外部设置）
        /// </summary>
        public string NextSongId { get; set; }

        /// <summary>
        /// 获取活跃视图 ID（由外部设置）
        /// </summary>
        public string ActiveViewId { get; set; }

        /// <summary>
        /// 获取队列和插播列表中的歌曲 ID（由外部设置）
        /// </summary>
        public HashSet<string> QueueSongIds { get; set; } = new HashSet<string>();

        public CacheGarbageCollector()
        {
            _monitor = new MemoryPressureMonitor();
        }

        /// <summary>
        /// 启动后台监控
        /// </summary>
        public void StartMonitoring()
        {
            lock (_lock)
            {
                if (_isMonitoring)
                    return;

                _monitoringCts = new CancellationTokenSource();
                _isMonitoring = true;

                _monitoringTask = Task.Run(() => MonitoringLoop(_monitoringCts.Token), _monitoringCts.Token);

                Utils.DebugLogger.Log(Utils.LogLevel.Info, "CacheGarbageCollector",
                    $"Started monitoring (soft limit: {SOFT_LIMIT_BYTES / 1024.0 / 1024.0 / 1024.0:F2}GB)");
            }
        }

        /// <summary>
        /// 停止后台监控
        /// </summary>
        public void StopMonitoring()
        {
            lock (_lock)
            {
                if (!_isMonitoring)
                    return;

                _monitoringCts?.Cancel();
                _isMonitoring = false;
            }

            try
            {
                _monitoringTask?.Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                Utils.DebugLogger.Log(Utils.LogLevel.Warning, "CacheGarbageCollector",
                    $"Stop monitoring exception: {ex.Message}");
            }

            Utils.DebugLogger.Log(Utils.LogLevel.Info, "CacheGarbageCollector",
                "Stopped monitoring");
        }

        /// <summary>
        /// 监控循环
        /// </summary>
        private async Task MonitoringLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(CHECK_INTERVAL_SECONDS), token);

                    long usage = _monitor.GetCurrentMemoryUsage();

                    if (usage >= SOFT_LIMIT_BYTES)
                    {
                        Utils.DebugLogger.Log(Utils.LogLevel.Warning, "CacheGarbageCollector",
                            $"Memory limit exceeded: {usage / 1024.0 / 1024.0 / 1024.0:F2}GB, triggering GC");

                        await CollectGarbageAsync();
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Utils.DebugLogger.LogException("CacheGarbageCollector", ex,
                        "Monitoring loop error");
                    await Task.Delay(TimeSpan.FromSeconds(5), token);
                }
            }
        }

        /// <summary>
        /// 执行垃圾回收
        /// </summary>
        public async Task CollectGarbageAsync()
        {
            await Task.Run(() =>
            {
                try
                {
                    var stats = _monitor.GetDetailedStats();
                    Utils.DebugLogger.Log(Utils.LogLevel.Info, "CacheGarbageCollector",
                        $"Starting GC - Current stats: {stats}");

                    // 构建保护集合（不可回收）
                    var protectedSongIds = BuildProtectedSet();

                    Utils.DebugLogger.Log(Utils.LogLevel.Debug, "CacheGarbageCollector",
                        $"Protected songs: {protectedSongIds.Count}");

                    // 第一轮：清理不在保护集合中的歌曲的非 Chunk 0 数据
                    int phase1Cleared = ClearNonChunk0Data(protectedSongIds);

                    long usageAfterPhase1 = _monitor.GetCurrentMemoryUsage();
                    Utils.DebugLogger.Log(Utils.LogLevel.Info, "CacheGarbageCollector",
                        $"Phase 1 complete: Cleared {phase1Cleared} songs, " +
                        $"Memory: {usageAfterPhase1 / 1024.0 / 1024.0 / 1024.0:F2}GB");

                    // 第二轮：如果仍超限，清理不在活跃视图和队列中的歌曲的 Chunk 0
                    if (usageAfterPhase1 >= SOFT_LIMIT_BYTES)
                    {
                        int phase2Cleared = ClearChunk0Data(protectedSongIds);

                        long usageAfterPhase2 = _monitor.GetCurrentMemoryUsage();
                        Utils.DebugLogger.Log(Utils.LogLevel.Info, "CacheGarbageCollector",
                            $"Phase 2 complete: Cleared {phase2Cleared} chunks, " +
                            $"Memory: {usageAfterPhase2 / 1024.0 / 1024.0 / 1024.0:F2}GB");
                    }

                    // 触发 .NET GC
                    GC.Collect(2, GCCollectionMode.Optimized);

                    var finalStats = _monitor.GetDetailedStats();
                    Utils.DebugLogger.Log(Utils.LogLevel.Info, "CacheGarbageCollector",
                        $"GC complete - Final stats: {finalStats}");
                }
                catch (Exception ex)
                {
                    Utils.DebugLogger.LogException("CacheGarbageCollector", ex,
                        "Garbage collection failed");
                }
            });
        }

        /// <summary>
        /// 构建保护集合
        /// </summary>
        private HashSet<string> BuildProtectedSet()
        {
            var protected = new HashSet<string>();

            // 当前播放和下一首
            if (!string.IsNullOrEmpty(CurrentPlayingSongId))
                protected.Add(CurrentPlayingSongId);

            if (!string.IsNullOrEmpty(NextSongId))
                protected.Add(NextSongId);

            // 队列和插播列表
            if (QueueSongIds != null)
            {
                foreach (var id in QueueSongIds)
                {
                    protected.Add(id);
                }
            }

            return protected;
        }

        /// <summary>
        /// 第一轮：清理非 Chunk 0 数据
        /// </summary>
        private int ClearNonChunk0Data(HashSet<string> protectedSongIds)
        {
            int clearedCount = 0;

            foreach (var song in SongDataStore.Instance.GetAllSongs())
            {
                string songId = song.SongId.Value;

                if (protectedSongIds.Contains(songId))
                    continue; // 跳过保护的歌曲

                // 清理非 Chunk 0 数据
                song.ClearNonChunk0Data();
                clearedCount++;
            }

            return clearedCount;
        }

        /// <summary>
        /// 第二轮：清理 Chunk 0 数据（更激进）
        /// </summary>
        private int ClearChunk0Data(HashSet<string> protectedSongIds)
        {
            int clearedCount = 0;

            // 活跃视图中的歌曲 ID
            var viewSongIds = new HashSet<string>();
            if (!string.IsNullOrEmpty(ActiveViewId))
            {
                if (SongDataStore.Instance.TryGetView(ActiveViewId, out var view))
                {
                    foreach (var songId in view.SongIds.Value)
                    {
                        viewSongIds.Add(songId);
                    }
                }
            }

            // 严格保护集合：当前播放 + 下一首 + 队列 + 活跃视图
            var strictProtected = new HashSet<string>(protectedSongIds);
            strictProtected.UnionWith(viewSongIds);

            foreach (var song in SongDataStore.Instance.GetAllSongs())
            {
                string songId = song.SongId.Value;

                if (strictProtected.Contains(songId))
                    continue; // 跳过严格保护的歌曲

                // 清理所有数据（包括 Chunk 0）
                song.ClearAllData();
                clearedCount++;
            }

            return clearedCount;
        }

        /// <summary>
        /// 手动触发垃圾回收（用于测试）
        /// </summary>
        public void ForceCollect()
        {
            Utils.DebugLogger.Log(Utils.LogLevel.Info, "CacheGarbageCollector", 
                "Force collect triggered");

            CollectGarbageAsync().SafeFireAndForget("CacheGarbageCollector force collect");
        }
    }
}
