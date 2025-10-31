using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using YTPlayer.Core.Reactive;
using YTPlayer.Models;

namespace YTPlayer.Core.Data
{
    /// <summary>
    /// 批量加载状态枚举
    /// </summary>
    public enum BatchLoadingStatus
    {
        /// <summary>
        /// 未开始
        /// </summary>
        NotStarted,

        /// <summary>
        /// 正在加载
        /// </summary>
        Loading,

        /// <summary>
        /// 加载完成
        /// </summary>
        Completed,

        /// <summary>
        /// 加载失败
        /// </summary>
        Failed,

        /// <summary>
        /// 已取消
        /// </summary>
        Cancelled
    }

    /// <summary>
    /// 列表视图容器
    /// 管理特定视图（播放列表、搜索结果等）中的歌曲集合
    /// </summary>
    public sealed class ViewContainer
    {
        #region 基本信息

        /// <summary>
        /// 视图唯一标识（如 "playlist-123", "search-20241028"）
        /// </summary>
        public string ViewId { get; }

        /// <summary>
        /// 视图显示名称
        /// </summary>
        public string ViewName { get; }

        #endregion

        #region 响应式字段

        /// <summary>
        /// 歌曲 ID 列表（有序）
        /// </summary>
        public ObservableField<List<string>> SongIds { get; }

        /// <summary>
        /// 是否当前显示（活跃视图）
        /// </summary>
        public ObservableField<bool> IsActive { get; }

        /// <summary>
        /// 批量加载状态
        /// </summary>
        public ObservableField<BatchLoadingStatus> LoadingStatus { get; }

        /// <summary>
        /// 已验证的歌曲数量（资源有效性检查完成）
        /// </summary>
        public ObservableField<int> ValidatedCount { get; }

        /// <summary>
        /// 已解析 URL 的歌曲数量
        /// </summary>
        public ObservableField<int> UrlResolvedCount { get; }

        /// <summary>
        /// 已加载 Chunk 0 的歌曲数量
        /// </summary>
        public ObservableField<int> Chunk0LoadedCount { get; }

        #endregion

        #region 批量加载控制

        /// <summary>
        /// 批量加载取消令牌源
        /// </summary>
        private CancellationTokenSource _batchLoadingCts;

        private readonly object _batchLoadingLock = new object();

        #endregion

        #region 构造函数

        /// <summary>
        /// 创建视图容器
        /// </summary>
        /// <param name="viewId">视图 ID</param>
        /// <param name="viewName">视图名称</param>
        public ViewContainer(string viewId, string viewName)
        {
            if (string.IsNullOrWhiteSpace(viewId))
                throw new ArgumentException("View ID cannot be null or empty", nameof(viewId));

            ViewId = viewId;
            ViewName = viewName ?? viewId;

            // 初始化响应式字段
            SongIds = new ObservableField<List<string>>(new List<string>());
            IsActive = new ObservableField<bool>(false);
            LoadingStatus = new ObservableField<BatchLoadingStatus>(BatchLoadingStatus.NotStarted);
            ValidatedCount = new ObservableField<int>(0);
            UrlResolvedCount = new ObservableField<int>(0);
            Chunk0LoadedCount = new ObservableField<int>(0);
        }

        #endregion

        #region 歌曲集合管理

        /// <summary>
        /// 初始化歌曲列表（从 SongInfo 集合）
        /// </summary>
        /// <param name="songs">歌曲信息列表</param>
        public void InitializeSongs(List<SongInfo> songs)
        {
            if (songs == null)
                throw new ArgumentNullException(nameof(songs));

            // 取消旧的批量加载
            CancelBatchLoading();

            // 创建/更新歌曲数据模型
            var songIds = new List<string>();

            for (int i = 0; i < songs.Count; i++)
            {
                var songInfo = songs[i];
                var songModel = SongDataStore.Instance.GetOrCreate(songInfo.Id);

                // 填充元数据
                if (songModel.Metadata.Value == null)
                {
                    songModel.Metadata.Value = new Models.Playback.SongMetadata(songInfo);
                }

                // 设置视图信息
                songModel.SourceView.Value = ViewId;
                songModel.IndexInView.Value = i;

                songIds.Add(songInfo.Id);

                // 如果 SongInfo 有 URL 缓存，填充到数据模型
                if (!string.IsNullOrEmpty(songInfo.Url) && !string.IsNullOrEmpty(songInfo.Level))
                {
                    string level = songInfo.Level.ToLower();
                    var container = songModel.GetOrCreateQualityContainer(level);

                    if (!container.IsUrlResolved.Value)
                    {
                        container.Url.Value = songInfo.Url;
                        container.TotalSize.Value = songInfo.Size;
                        container.IsUrlResolved.Value = true;
                    }
                }
            }

            // 更新歌曲 ID 列表
            SongIds.Value = songIds;

            // 重置计数器
            ValidatedCount.Value = 0;
            UrlResolvedCount.Value = 0;
            Chunk0LoadedCount.Value = 0;
            LoadingStatus.Value = BatchLoadingStatus.NotStarted;
        }

        /// <summary>
        /// 获取视图中的总歌曲数
        /// </summary>
        public int TotalSongs => SongIds.Value.Count;

        /// <summary>
        /// 获取视图中的所有歌曲数据模型
        /// </summary>
        public IEnumerable<Models.Playback.SongDataModel> GetAllSongModels()
        {
            return SongIds.Value.Select(id => SongDataStore.Instance.GetOrCreate(id));
        }

        #endregion

        #region 批量加载控制

        /// <summary>
        /// 启动批量加载管道
        /// </summary>
        public void StartBatchLoading()
        {
            lock (_batchLoadingLock)
            {
                // 如果已经在加载，先取消
                if (_batchLoadingCts != null && !_batchLoadingCts.IsCancellationRequested)
                {
                    _batchLoadingCts.Cancel();
                    _batchLoadingCts.Dispose();
                }

                // 创建新的取消令牌
                _batchLoadingCts = new CancellationTokenSource();
                LoadingStatus.Value = BatchLoadingStatus.Loading;

                // 启动批量加载管道（异步执行）
                var pipeline = new Loading.BatchLoadingPipeline();
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    try
                    {
                        await pipeline.ExecuteAsync(this, _batchLoadingCts.Token);
                        LoadingStatus.Value = BatchLoadingStatus.Completed;
                    }
                    catch (OperationCanceledException)
                    {
                        LoadingStatus.Value = BatchLoadingStatus.Cancelled;
                    }
                    catch (Exception ex)
                    {
                        Utils.DebugLogger.Log(Utils.LogLevel.Error, "ViewContainer",
                            $"Batch loading failed: {ex.Message}");
                        LoadingStatus.Value = BatchLoadingStatus.Failed;
                    }
                }, _batchLoadingCts.Token);
            }
        }

        /// <summary>
        /// 取消批量加载
        /// </summary>
        public void CancelBatchLoading()
        {
            lock (_batchLoadingLock)
            {
                if (_batchLoadingCts != null && !_batchLoadingCts.IsCancellationRequested)
                {
                    _batchLoadingCts.Cancel();
                    _batchLoadingCts.Dispose();
                    _batchLoadingCts = null;
                }
            }
        }

        #endregion

        #region 进度查询

        /// <summary>
        /// 获取验证进度百分比（0-100）
        /// </summary>
        public double GetValidationProgress()
        {
            int total = TotalSongs;
            if (total == 0) return 0;
            return (double)ValidatedCount.Value / total * 100.0;
        }

        /// <summary>
        /// 获取 URL 解析进度百分比（0-100）
        /// </summary>
        public double GetUrlResolveProgress()
        {
            int total = TotalSongs;
            if (total == 0) return 0;
            return (double)UrlResolvedCount.Value / total * 100.0;
        }

        /// <summary>
        /// 获取 Chunk 0 加载进度百分比（0-100）
        /// </summary>
        public double GetChunk0LoadProgress()
        {
            int total = TotalSongs;
            if (total == 0) return 0;
            return (double)Chunk0LoadedCount.Value / total * 100.0;
        }

        #endregion

        public override string ToString()
        {
            return $"View[{ViewName}] Songs={TotalSongs}, Status={LoadingStatus.Value}, " +
                   $"Validated={ValidatedCount.Value}, URLResolved={UrlResolvedCount.Value}, " +
                   $"Chunk0={Chunk0LoadedCount.Value}";
        }
    }
}
