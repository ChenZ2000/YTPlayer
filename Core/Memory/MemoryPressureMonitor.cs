using System;
using System.Linq;
using YTPlayer.Core.Data;

namespace YTPlayer.Core.Memory
{
    /// <summary>
    /// 内存压力监控器
    /// 监控当前内存使用情况，提供统计数据
    /// </summary>
    public sealed class MemoryPressureMonitor
    {
        /// <summary>
        /// 获取当前内存使用量（字节）
        /// </summary>
        public long GetCurrentMemoryUsage()
        {
            try
            {
                // 遍历所有歌曲数据模型，计算所有 chunk 数据总大小
                long totalBytes = SongDataStore.Instance.GetAllSongs()
                    .Sum(song => song.CalculateTotalMemoryUsage());

                return totalBytes;
            }
            catch (Exception ex)
            {
                Utils.DebugLogger.LogException("MemoryPressureMonitor", ex,
                    "Failed to calculate memory usage");
                return 0;
            }
        }

        /// <summary>
        /// 获取内存使用量（MB）
        /// </summary>
        public double GetCurrentMemoryUsageMB()
        {
            return GetCurrentMemoryUsage() / 1024.0 / 1024.0;
        }

        /// <summary>
        /// 获取内存使用量（GB）
        /// </summary>
        public double GetCurrentMemoryUsageGB()
        {
            return GetCurrentMemoryUsage() / 1024.0 / 1024.0 / 1024.0;
        }

        /// <summary>
        /// 获取详细统计信息
        /// </summary>
        public MemoryStats GetDetailedStats()
        {
            try
            {
                var allSongs = SongDataStore.Instance.GetAllSongs().ToList();

                var stats = new MemoryStats
                {
                    TotalSongs = allSongs.Count,
                    SongsWithData = allSongs.Count(s => s.CalculateTotalMemoryUsage() > 0),
                    TotalMemoryBytes = 0,
                    TotalChunksLoaded = 0,
                    AverageChunksPerSong = 0
                };

                foreach (var song in allSongs)
                {
                    long songMemory = song.CalculateTotalMemoryUsage();
                    stats.TotalMemoryBytes += songMemory;

                    foreach (var container in song.GetAllQualityContainers())
                    {
                        stats.TotalChunksLoaded += container.GetLoadedChunkCount();
                    }
                }

                if (stats.SongsWithData > 0)
                {
                    stats.AverageChunksPerSong = (double)stats.TotalChunksLoaded / stats.SongsWithData;
                }

                return stats;
            }
            catch (Exception ex)
            {
                Utils.DebugLogger.LogException("MemoryPressureMonitor", ex,
                    "Failed to get detailed stats");
                return new MemoryStats();
            }
        }
    }

    /// <summary>
    /// 内存统计信息
    /// </summary>
    public sealed class MemoryStats
    {
        public int TotalSongs { get; set; }
        public int SongsWithData { get; set; }
        public long TotalMemoryBytes { get; set; }
        public int TotalChunksLoaded { get; set; }
        public double AverageChunksPerSong { get; set; }

        public double TotalMemoryMB => TotalMemoryBytes / 1024.0 / 1024.0;
        public double TotalMemoryGB => TotalMemoryBytes / 1024.0 / 1024.0 / 1024.0;

        public override string ToString()
        {
            return $"Songs: {SongsWithData}/{TotalSongs}, " +
                   $"Memory: {TotalMemoryMB:F2}MB ({TotalMemoryGB:F3}GB), " +
                   $"Chunks: {TotalChunksLoaded}, " +
                   $"Avg/Song: {AverageChunksPerSong:F1}";
        }
    }
}
