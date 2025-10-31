using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using YTPlayer.Models.Playback;

namespace YTPlayer.Core.Data
{
    /// <summary>
    /// 歌曲数据仓库 - 单例模式
    /// 全局管理所有歌曲的响应式数据模型
    /// </summary>
    public sealed class SongDataStore
    {
        #region 单例实现

        private static readonly Lazy<SongDataStore> _instance = new Lazy<SongDataStore>(() => new SongDataStore());

        /// <summary>
        /// 获取单例实例
        /// </summary>
        public static SongDataStore Instance => _instance.Value;

        private SongDataStore()
        {
            _songs = new ConcurrentDictionary<string, SongDataModel>();
            _views = new ConcurrentDictionary<string, ViewContainer>();
        }

        #endregion

        #region 数据存储

        /// <summary>
        /// 所有歌曲数据（ConcurrentDictionary，线程安全）
        /// Key: 歌曲 ID, Value: SongDataModel
        /// </summary>
        private readonly ConcurrentDictionary<string, SongDataModel> _songs;

        /// <summary>
        /// 视图容器管理
        /// Key: 视图 ID, Value: ViewContainer
        /// </summary>
        private readonly ConcurrentDictionary<string, ViewContainer> _views;

        #endregion

        #region 歌曲数据访问

        /// <summary>
        /// 获取或创建歌曲数据模型
        /// </summary>
        /// <param name="songId">歌曲 ID</param>
        /// <returns>歌曲数据模型</returns>
        public SongDataModel GetOrCreate(string songId)
        {
            if (string.IsNullOrWhiteSpace(songId))
                throw new ArgumentException("Song ID cannot be null or empty", nameof(songId));

            return _songs.GetOrAdd(songId, id => new SongDataModel(id));
        }

        /// <summary>
        /// 尝试获取歌曲数据模型（不创建新模型）
        /// </summary>
        public bool TryGet(string songId, out SongDataModel song)
        {
            return _songs.TryGetValue(songId, out song);
        }

        /// <summary>
        /// 移除歌曲数据（谨慎使用，仅用于永久删除）
        /// </summary>
        public bool RemoveSong(string songId)
        {
            if (_songs.TryRemove(songId, out var song))
            {
                // 清空所有数据释放内存
                song.ClearAllData();
                return true;
            }
            return false;
        }

        /// <summary>
        /// 获取所有歌曲数据
        /// </summary>
        public IEnumerable<SongDataModel> GetAllSongs()
        {
            return _songs.Values;
        }

        /// <summary>
        /// 获取歌曲总数
        /// </summary>
        public int GetSongCount()
        {
            return _songs.Count;
        }

        #endregion

        #region 视图容器访问

        /// <summary>
        /// 获取或创建视图容器
        /// </summary>
        /// <param name="viewId">视图 ID</param>
        /// <param name="viewName">视图名称</param>
        /// <returns>视图容器</returns>
        public ViewContainer GetOrCreateView(string viewId, string viewName = null)
        {
            if (string.IsNullOrWhiteSpace(viewId))
                throw new ArgumentException("View ID cannot be null or empty", nameof(viewId));

            return _views.GetOrAdd(viewId, id => new ViewContainer(id, viewName ?? id));
        }

        /// <summary>
        /// 尝试获取视图容器（不创建新容器）
        /// </summary>
        public bool TryGetView(string viewId, out ViewContainer view)
        {
            return _views.TryGetValue(viewId, out view);
        }

        /// <summary>
        /// 移除视图容器
        /// </summary>
        public bool RemoveView(string viewId)
        {
            if (_views.TryRemove(viewId, out var view))
            {
                // 取消批量加载
                view.CancelBatchLoading();
                return true;
            }
            return false;
        }

        /// <summary>
        /// 获取所有视图容器
        /// </summary>
        public IEnumerable<ViewContainer> GetAllViews()
        {
            return _views.Values;
        }

        /// <summary>
        /// 获取活跃视图（当前显示的视图）
        /// </summary>
        public ViewContainer GetActiveView()
        {
            return _views.Values.FirstOrDefault(v => v.IsActive.Value);
        }

        #endregion

        #region 内存管理

        /// <summary>
        /// 计算总内存占用（所有歌曲数据）
        /// </summary>
        public long CalculateTotalMemoryUsage()
        {
            return _songs.Values.Sum(s => s.CalculateTotalMemoryUsage());
        }

        /// <summary>
        /// 清空所有非关键数据（保留 Chunk 0 和元数据）
        /// </summary>
        public void ClearNonCriticalData()
        {
            foreach (var song in _songs.Values)
            {
                song.ClearNonChunk0Data();
            }
        }

        /// <summary>
        /// 完全清空所有数据（慎用）
        /// </summary>
        public void ClearAll()
        {
            foreach (var song in _songs.Values)
            {
                song.ClearAllData();
            }

            _songs.Clear();

            foreach (var view in _views.Values)
            {
                view.CancelBatchLoading();
            }

            _views.Clear();
        }

        #endregion

        #region 统计信息

        /// <summary>
        /// 获取统计信息
        /// </summary>
        public SongDataStoreStats GetStats()
        {
            return new SongDataStoreStats
            {
                TotalSongs = _songs.Count,
                TotalViews = _views.Count,
                TotalMemoryBytes = CalculateTotalMemoryUsage(),
                SongsWithData = _songs.Values.Count(s => s.CalculateTotalMemoryUsage() > 0),
                ActiveViews = _views.Values.Count(v => v.IsActive.Value)
            };
        }

        #endregion

        public override string ToString()
        {
            var stats = GetStats();
            return $"SongDataStore: {stats.TotalSongs} songs, {stats.TotalViews} views, " +
                   $"{stats.TotalMemoryBytes / 1024.0 / 1024.0:F2}MB memory";
        }
    }

    /// <summary>
    /// 数据仓库统计信息
    /// </summary>
    public sealed class SongDataStoreStats
    {
        public int TotalSongs { get; set; }
        public int TotalViews { get; set; }
        public long TotalMemoryBytes { get; set; }
        public int SongsWithData { get; set; }
        public int ActiveViews { get; set; }

        public double TotalMemoryMB => TotalMemoryBytes / 1024.0 / 1024.0;
    }
}
