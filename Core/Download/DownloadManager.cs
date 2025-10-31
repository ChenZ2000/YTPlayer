using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using YTPlayer.Models;
using YTPlayer.Models.Download;
using YTPlayer.Utils;

namespace YTPlayer.Core.Download
{
    /// <summary>
    /// 下载管理器（单例）
    /// 负责管理下载队列、任务调度、并发控制
    /// </summary>
    public class DownloadManager
    {
        #region 单例

        private static readonly object _instanceLock = new object();
        private static DownloadManager? _instance;

        /// <summary>
        /// 获取单例实例
        /// </summary>
        public static DownloadManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_instanceLock)
                    {
                        if (_instance == null)
                        {
                            _instance = new DownloadManager();
                        }
                    }
                }
                return _instance;
            }
        }

        #endregion

        #region 常量

        /// <summary>
        /// 最大并发下载数
        /// </summary>
        private const int MAX_CONCURRENT_DOWNLOADS = 3;

        /// <summary>
        /// 任务启动间隔（毫秒，避免同时发起请求触发限流）
        /// </summary>
        private const int TASK_START_INTERVAL_MS = 200;

        /// <summary>
        /// 调度循环间隔（毫秒）
        /// </summary>
        private const int SCHEDULER_INTERVAL_MS = 1000;

        #endregion

        #region 事件

        /// <summary>
        /// 任务进度更新事件
        /// </summary>
        public event Action<DownloadTask>? TaskProgressChanged;

        /// <summary>
        /// 任务完成事件
        /// </summary>
        public event Action<DownloadTask>? TaskCompleted;

        /// <summary>
        /// 任务失败事件
        /// </summary>
        public event Action<DownloadTask>? TaskFailed;

        /// <summary>
        /// 任务取消事件
        /// </summary>
        public event Action<DownloadTask>? TaskCancelled;

        /// <summary>
        /// 队列状态改变事件（用于 UI 刷新）
        /// </summary>
        public event Action? QueueStateChanged;

        #endregion

        #region 私有字段

        private readonly object _queueLock = new object();
        private readonly List<DownloadTask> _pendingQueue;      // 待下载队列
        private readonly List<DownloadTask> _activeQueue;       // 进行中队列
        private readonly List<DownloadTask> _completedQueue;    // 已完成队列

        private NeteaseApiClient? _apiClient;
        private readonly ConfigManager _configManager;
        private readonly DownloadBandwidthCoordinator _bandwidthCoordinator;

        private Task? _schedulerTask;
        private CancellationTokenSource? _schedulerCts;
        private bool _isRunning;

        #endregion

        #region 构造函数

        /// <summary>
        /// 私有构造函数（单例模式）
        /// </summary>
        private DownloadManager()
        {
            _pendingQueue = new List<DownloadTask>();
            _activeQueue = new List<DownloadTask>();
            _completedQueue = new List<DownloadTask>();

            _apiClient = null;  // Will be set by Initialize()
            _configManager = ConfigManager.Instance;
            _bandwidthCoordinator = DownloadBandwidthCoordinator.Instance;

            _isRunning = false;

            // 启动调度器
            StartScheduler();
        }

        #endregion

        #region 公共方法 - 初始化

        /// <summary>
        /// 初始化下载管理器（设置 API 客户端）
        /// </summary>
        /// <param name="apiClient">NetEase API 客户端实例</param>
        public void Initialize(NeteaseApiClient apiClient)
        {
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        }

        #endregion

        #region 公共方法 - 添加任务

        /// <summary>
        /// 添加单曲下载任务
        /// </summary>
        /// <param name="song">歌曲信息</param>
        /// <param name="quality">音质级别</param>
        /// <param name="sourceList">来源列表名称</param>
        /// <returns>下载任务</returns>
        public async Task<DownloadTask?> AddSongDownloadAsync(
            SongInfo song,
            QualityLevel quality,
            string sourceList = "")
        {
            if (song == null)
            {
                throw new ArgumentNullException(nameof(song));
            }

            var config = _configManager.Load();
            string downloadDirectory = ConfigManager.GetFullDownloadPath(config.DownloadDirectory);

            try
            {
                // 先创建临时下载任务（文件路径暂时为空）
                var task = new DownloadTask
                {
                    Song = song,
                    Quality = quality,
                    DestinationPath = "",  // 稍后填充
                    SourceList = sourceList,
                    Status = DownloadStatus.Pending
                };

                // ⭐ 先获取下载 URL（会填充试听信息到 song.IsTrial）
                bool urlSuccess = await FetchDownloadUrlAsync(task).ConfigureAwait(false);
                if (!urlSuccess)
                {
                    return null;
                }

                // ⭐ 在获取URL后构建文件路径（此时 song.IsTrial 已正确填充）
                string filePath = DownloadFileHelper.BuildFilePath(
                    downloadDirectory,
                    song,
                    quality);

                task.DestinationPath = filePath;

                // 添加到待下载队列
                lock (_queueLock)
                {
                    _pendingQueue.Add(task);
                }

                DebugLogger.Log(
                    DebugLogger.LogLevel.Info,
                    "DownloadManager",
                    $"已添加下载任务: {song.Name} - {song.Artist}");

                QueueStateChanged?.Invoke();
                return task;
            }
            catch (Exception ex)
            {
                DebugLogger.LogException("DownloadManager", ex, $"添加下载任务失败: {song.Name}");
                return null;
            }
        }

        /// <summary>
        /// 批量添加下载任务
        /// </summary>
        /// <param name="songs">歌曲列表</param>
        /// <param name="quality">音质级别</param>
        /// <param name="sourceList">来源列表名称</param>
        /// <param name="subDirectory">子目录（用于歌单/专辑批量下载）</param>
        /// <param name="originalIndices">原始列表索引（可选，用于保留真实列表序号）</param>
        /// <returns>成功添加的任务列表</returns>
        public async Task<List<DownloadTask>> AddBatchDownloadAsync(
            List<SongInfo> songs,
            QualityLevel quality,
            string sourceList = "",
            string? subDirectory = null,
            List<int>? originalIndices = null)
        {
            if (songs == null || songs.Count == 0)
            {
                return new List<DownloadTask>();
            }

            var config = _configManager.Load();
            string downloadDirectory = ConfigManager.GetFullDownloadPath(config.DownloadDirectory);

            var addedTasks = new List<DownloadTask>();

            for (int i = 0; i < songs.Count; i++)
            {
                var song = songs[i];
                try
                {
                    // 使用原始索引或顺序编号
                    int trackNumber = (originalIndices != null && i < originalIndices.Count)
                        ? originalIndices[i]
                        : i + 1;

                    // 先创建临时下载任务（文件路径暂时为空）
                    var task = new DownloadTask
                    {
                        Song = song,
                        Quality = quality,
                        DestinationPath = "",  // 稍后填充
                        SourceList = sourceList,
                        TrackNumber = trackNumber,  // 设置曲目编号（用于元数据写入）
                        Status = DownloadStatus.Pending
                    };

                    // ⭐ 先获取下载 URL（会填充试听信息到 song.IsTrial）
                    bool urlSuccess = await FetchDownloadUrlAsync(task).ConfigureAwait(false);
                    if (urlSuccess)
                    {
                        // ⭐ 在获取URL后构建文件路径（此时 song.IsTrial 已正确填充）
                        string filePath = DownloadFileHelper.BuildFilePath(
                            downloadDirectory,
                            song,
                            quality,
                            trackNumber: trackNumber,
                            subDirectory: subDirectory);

                        task.DestinationPath = filePath;
                        addedTasks.Add(task);
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.LogException("DownloadManager", ex, $"添加批量下载任务失败: {song.Name}");
                }
            }

            // 批量添加到待下载队列
            if (addedTasks.Count > 0)
            {
                lock (_queueLock)
                {
                    _pendingQueue.AddRange(addedTasks);
                }

                DebugLogger.Log(
                    DebugLogger.LogLevel.Info,
                    "DownloadManager",
                    $"已添加批量下载任务: {addedTasks.Count}/{songs.Count} 首");

                QueueStateChanged?.Invoke();
            }

            return addedTasks;
        }

        #endregion

        #region 公共方法 - 任务控制

        /// <summary>
        /// 暂停任务
        /// </summary>
        public void PauseTask(DownloadTask task)
        {
            if (task == null)
            {
                return;
            }

            lock (_queueLock)
            {
                if (task.Status == DownloadStatus.Downloading)
                {
                    // 取消下载
                    task.CancellationTokenSource?.Cancel();
                    task.Status = DownloadStatus.Paused;

                    DebugLogger.Log(
                        DebugLogger.LogLevel.Info,
                        "DownloadManager",
                        $"已暂停任务: {task.Song.Name}");

                    QueueStateChanged?.Invoke();
                }
            }
        }

        /// <summary>
        /// 继续任务
        /// </summary>
        public void ResumeTask(DownloadTask task)
        {
            if (task == null)
            {
                return;
            }

            lock (_queueLock)
            {
                if (task.Status == DownloadStatus.Paused)
                {
                    // 移回待下载队列
                    _activeQueue.Remove(task);
                    task.Status = DownloadStatus.Pending;
                    _pendingQueue.Insert(0, task);  // 插入到队首优先下载

                    DebugLogger.Log(
                        DebugLogger.LogLevel.Info,
                        "DownloadManager",
                        $"已恢复任务: {task.Song.Name}");

                    QueueStateChanged?.Invoke();
                }
            }
        }

        /// <summary>
        /// 取消任务
        /// </summary>
        public void CancelTask(DownloadTask task)
        {
            if (task == null)
            {
                return;
            }

            lock (_queueLock)
            {
                // 取消下载
                task.CancellationTokenSource?.Cancel();
                task.Status = DownloadStatus.Cancelled;

                // 从队列中移除
                _pendingQueue.Remove(task);
                _activeQueue.Remove(task);

                // 删除临时文件
                DownloadFileHelper.DeleteTempFileIfExists(task.TempFilePath);

                // 释放资源
                task.Dispose();

                DebugLogger.Log(
                    DebugLogger.LogLevel.Info,
                    "DownloadManager",
                    $"已取消任务: {task.Song.Name}");

                TaskCancelled?.Invoke(task);
                QueueStateChanged?.Invoke();
            }
        }

        /// <summary>
        /// 取消所有进行中的任务
        /// </summary>
        public void CancelAllActiveTasks()
        {
            lock (_queueLock)
            {
                var tasksToCancel = _pendingQueue.Concat(_activeQueue).ToList();

                foreach (var task in tasksToCancel)
                {
                    task.CancellationTokenSource?.Cancel();
                    task.Status = DownloadStatus.Cancelled;
                    DownloadFileHelper.DeleteTempFileIfExists(task.TempFilePath);
                    task.Dispose();
                }

                _pendingQueue.Clear();
                _activeQueue.Clear();

                DebugLogger.Log(
                    DebugLogger.LogLevel.Info,
                    "DownloadManager",
                    $"已取消所有任务: {tasksToCancel.Count} 个");

                QueueStateChanged?.Invoke();
            }
        }

        /// <summary>
        /// 清除已完成任务列表
        /// </summary>
        public void ClearCompletedTasks()
        {
            lock (_queueLock)
            {
                int count = _completedQueue.Count;
                foreach (var task in _completedQueue)
                {
                    task.Dispose();
                }
                _completedQueue.Clear();

                DebugLogger.Log(
                    DebugLogger.LogLevel.Info,
                    "DownloadManager",
                    $"已清除完成任务: {count} 个");

                QueueStateChanged?.Invoke();
            }
        }

        /// <summary>
        /// 从已完成列表中移除单个任务
        /// </summary>
        public void RemoveCompletedTask(DownloadTask task)
        {
            if (task == null)
            {
                return;
            }

            lock (_queueLock)
            {
                if (_completedQueue.Remove(task))
                {
                    task.Dispose();
                    QueueStateChanged?.Invoke();
                }
            }
        }

        #endregion

        #region 公共方法 - 查询

        /// <summary>
        /// 获取待下载任务列表（副本）
        /// </summary>
        public List<DownloadTask> GetPendingTasks()
        {
            lock (_queueLock)
            {
                return new List<DownloadTask>(_pendingQueue);
            }
        }

        /// <summary>
        /// 获取进行中任务列表（副本）
        /// </summary>
        public List<DownloadTask> GetActiveTasks()
        {
            lock (_queueLock)
            {
                return new List<DownloadTask>(_activeQueue);
            }
        }

        /// <summary>
        /// 获取已完成任务列表（副本）
        /// </summary>
        public List<DownloadTask> GetCompletedTasks()
        {
            lock (_queueLock)
            {
                return new List<DownloadTask>(_completedQueue);
            }
        }

        /// <summary>
        /// 获取所有进行中的任务（待下载 + 下载中）
        /// </summary>
        public List<DownloadTask> GetAllActiveTasks()
        {
            lock (_queueLock)
            {
                var allActive = new List<DownloadTask>();
                allActive.AddRange(_pendingQueue);
                allActive.AddRange(_activeQueue);
                return allActive;
            }
        }

        #endregion

        #region 私有方法 - 调度器

        /// <summary>
        /// 启动调度器
        /// </summary>
        private void StartScheduler()
        {
            if (_isRunning)
            {
                return;
            }

            _isRunning = true;
            _schedulerCts = new CancellationTokenSource();

            _schedulerTask = Task.Run(async () =>
            {
                await SchedulerLoopAsync(_schedulerCts.Token).ConfigureAwait(false);
            });

            DebugLogger.Log(
                DebugLogger.LogLevel.Info,
                "DownloadManager",
                "调度器已启动");
        }

        /// <summary>
        /// 停止调度器
        /// </summary>
        private void StopScheduler()
        {
            if (!_isRunning)
            {
                return;
            }

            _isRunning = false;
            _schedulerCts?.Cancel();

            try
            {
                _schedulerTask?.Wait(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // 忽略等待超时
            }

            _schedulerCts?.Dispose();
            _schedulerCts = null;

            DebugLogger.Log(
                DebugLogger.LogLevel.Info,
                "DownloadManager",
                "调度器已停止");
        }

        /// <summary>
        /// 调度循环
        /// </summary>
        private async Task SchedulerLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // 检查是否可以启动新任务
                    await TryStartNextTaskAsync(cancellationToken).ConfigureAwait(false);

                    // 等待下一次调度
                    await Task.Delay(SCHEDULER_INTERVAL_MS, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    DebugLogger.LogException("DownloadManager", ex, "调度循环异常");
                    await Task.Delay(SCHEDULER_INTERVAL_MS, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// 尝试启动下一个任务
        /// </summary>
        private async Task TryStartNextTaskAsync(CancellationToken cancellationToken)
        {
            DownloadTask? nextTask = null;

            lock (_queueLock)
            {
                // 检查活跃任务数
                int activeCount = _activeQueue.Count(t =>
                    t.Status == DownloadStatus.Downloading ||
                    t.Status == DownloadStatus.Pending);

                // 检查带宽协调器是否允许启动新任务
                if (!_bandwidthCoordinator.CanStartNewDownload(activeCount, MAX_CONCURRENT_DOWNLOADS))
                {
                    return;
                }

                // 从待下载队列取出任务
                if (_pendingQueue.Count > 0)
                {
                    nextTask = _pendingQueue[0];
                    _pendingQueue.RemoveAt(0);
                    _activeQueue.Add(nextTask);
                }
            }

            if (nextTask != null)
            {
                // 异步启动任务（不阻塞调度循环）
                _ = Task.Run(async () =>
                {
                    await ExecuteTaskAsync(nextTask, cancellationToken).ConfigureAwait(false);
                }, cancellationToken);

                // 间隔启动（避免同时发起请求）
                await Task.Delay(TASK_START_INTERVAL_MS, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 执行任务
        /// </summary>
        private async Task ExecuteTaskAsync(DownloadTask task, CancellationToken cancellationToken)
        {
            // 创建下载执行器，传入 API 客户端以启用元数据写入
            var executor = new DownloadExecutor(_apiClient);

            try
            {
                // 订阅事件
                executor.ProgressChanged += OnTaskProgressChanged;
                executor.DownloadCompleted += OnTaskCompleted;
                executor.DownloadFailed += OnTaskFailed;

                // 创建任务专用的 CancellationTokenSource
                task.CancellationTokenSource = new CancellationTokenSource();

                // 执行下载
                await executor.ExecuteAsync(task, task.CancellationTokenSource.Token).ConfigureAwait(false);
            }
            finally
            {
                // 取消订阅
                executor.ProgressChanged -= OnTaskProgressChanged;
                executor.DownloadCompleted -= OnTaskCompleted;
                executor.DownloadFailed -= OnTaskFailed;

                executor.Dispose();
            }
        }

        #endregion

        #region 私有方法 - 事件处理

        /// <summary>
        /// 任务进度更新处理
        /// </summary>
        private void OnTaskProgressChanged(DownloadTask task)
        {
            TaskProgressChanged?.Invoke(task);
        }

        /// <summary>
        /// 任务完成处理
        /// </summary>
        private void OnTaskCompleted(DownloadTask task)
        {
            lock (_queueLock)
            {
                _activeQueue.Remove(task);
                _completedQueue.Add(task);
            }

            TaskCompleted?.Invoke(task);
            QueueStateChanged?.Invoke();
        }

        /// <summary>
        /// 任务失败处理
        /// </summary>
        private void OnTaskFailed(DownloadTask task, Exception ex)
        {
            lock (_queueLock)
            {
                _activeQueue.Remove(task);
                // 失败的任务也移到完成队列（便于用户查看失败原因）
                _completedQueue.Add(task);
            }

            TaskFailed?.Invoke(task);
            QueueStateChanged?.Invoke();
        }

        #endregion

        #region 私有方法 - URL 获取

        /// <summary>
        /// 获取下载 URL
        /// </summary>
        private async Task<bool> FetchDownloadUrlAsync(DownloadTask task)
        {
            try
            {
                if (_apiClient == null)
                {
                    task.ErrorMessage = "下载管理器未初始化（API 客户端为空）";
                    return false;
                }

                // 调用 NeteaseApiClient 获取歌曲 URL
                var urlDict = await _apiClient.GetSongUrlAsync(
                    new[] { task.Song.Id },
                    ConvertToApiQuality(task.Quality),
                    skipAvailabilityCheck: false,
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);

                if (urlDict == null || !urlDict.ContainsKey(task.Song.Id))
                {
                    task.ErrorMessage = "无法获取下载链接";
                    return false;
                }

                var urlInfo = urlDict[task.Song.Id];
                if (string.IsNullOrWhiteSpace(urlInfo.Url))
                {
                    task.ErrorMessage = "歌曲下载链接为空（可能无版权或 VIP 专属）";
                    return false;
                }

                task.DownloadUrl = urlInfo.Url;
                task.TotalBytes = urlInfo.Size;

                // ⭐ 关键修复：将试听信息填充到 SongInfo，确保文件名包含试听标记
                if (urlInfo.FreeTrialInfo != null)
                {
                    task.Song.IsTrial = true;
                    task.Song.TrialStart = urlInfo.FreeTrialInfo.Start;
                    task.Song.TrialEnd = urlInfo.FreeTrialInfo.End;
                }
                else
                {
                    task.Song.IsTrial = false;
                    task.Song.TrialStart = 0;
                    task.Song.TrialEnd = 0;
                }

                return true;
            }
            catch (Exception ex)
            {
                task.ErrorMessage = $"获取下载链接失败: {ex.Message}";
                DebugLogger.LogException("DownloadManager", ex, $"获取下载 URL 失败: {task.Song.Name}");
                return false;
            }
        }

        /// <summary>
        /// 将 QualityLevel 转换为 API 使用的 QualityLevel
        /// </summary>
        private QualityLevel ConvertToApiQuality(QualityLevel quality)
        {
            // 这里直接返回，因为枚举定义一致
            return quality;
        }

        #endregion

        #region 释放资源

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            StopScheduler();

            lock (_queueLock)
            {
                foreach (var task in _pendingQueue.Concat(_activeQueue).Concat(_completedQueue))
                {
                    task.Dispose();
                }

                _pendingQueue.Clear();
                _activeQueue.Clear();
                _completedQueue.Clear();
            }
        }

        #endregion
    }
}
