using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using YTPlayer.Core.Download;
using YTPlayer.Models;
using YTPlayer.Models.Upload;
using YTPlayer.Utils;

namespace YTPlayer.Core.Upload
{
    /// <summary>
    /// 上传管理器（单例）
    /// 负责管理上传队列、任务调度、并发控制
    /// </summary>
    public class UploadManager
    {
        #region 单例

        private static readonly object _instanceLock = new object();
        private static UploadManager? _instance;

        /// <summary>
        /// 获取单例实例
        /// </summary>
        public static UploadManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_instanceLock)
                    {
                        if (_instance == null)
                        {
                            _instance = new UploadManager();
                        }
                    }
                }
                return _instance;
            }
        }

        #endregion

        #region 常量

        /// <summary>
        /// 最大并发上传数
        /// </summary>
        private const int MAX_CONCURRENT_UPLOADS = 2;

        /// <summary>
        /// 任务启动间隔（毫秒，避免同时发起请求）
        /// </summary>
        private const int TASK_START_INTERVAL_MS = 500;

        /// <summary>
        /// 调度循环间隔（毫秒）
        /// </summary>
        private const int SCHEDULER_INTERVAL_MS = 1000;

        #endregion

        #region 事件

        /// <summary>
        /// 任务进度更新事件
        /// </summary>
        public event Action<UploadTask>? TaskProgressChanged;

        /// <summary>
        /// 任务完成事件
        /// </summary>
        public event Action<UploadTask>? TaskCompleted;

        /// <summary>
        /// 任务失败事件
        /// </summary>
        public event Action<UploadTask>? TaskFailed;

        /// <summary>
        /// 任务取消事件
        /// </summary>
        public event Action<UploadTask>? TaskCancelled;

        /// <summary>
        /// 队列状态改变事件（用于 UI 刷新）
        /// </summary>
        public event Action? QueueStateChanged;

        #endregion

        #region 私有字段

        private readonly object _queueLock = new object();
        private readonly List<UploadTask> _pendingQueue;      // 待上传队列
        private readonly List<UploadTask> _activeQueue;       // 进行中队列
        private readonly List<UploadTask> _completedQueue;    // 已完成队列
        private readonly DownloadBandwidthCoordinator _bandwidthCoordinator;

        private NeteaseApiClient? _apiClient;

        private Task? _schedulerTask;
        private CancellationTokenSource? _schedulerCts;
        private bool _isRunning;

        #endregion

        #region 构造函数

        /// <summary>
        /// 私有构造函数（单例模式）
        /// </summary>
        private UploadManager()
        {
            _pendingQueue = new List<UploadTask>();
            _activeQueue = new List<UploadTask>();
            _completedQueue = new List<UploadTask>();

            _apiClient = null;
            _bandwidthCoordinator = DownloadBandwidthCoordinator.Instance;
            _isRunning = false;

            // 启动调度器
            StartScheduler();
        }

        #endregion

        #region 公共方法 - 初始化

        /// <summary>
        /// 初始化上传管理器（设置 API 客户端）
        /// </summary>
        public void Initialize(NeteaseApiClient apiClient)
        {
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        }

        #endregion

        #region 公共方法 - 添加任务

        /// <summary>
        /// 添加单个文件上传任务
        /// </summary>
        public UploadTask? AddUploadTask(string filePath, string sourceList = "")
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return null;
            }

            if (!System.IO.File.Exists(filePath))
            {
                DebugLogger.Log(
                    DebugLogger.LogLevel.Warning,
                    "UploadManager",
                    $"文件不存在: {filePath}");
                return null;
            }

            try
            {
                var fileInfo = new System.IO.FileInfo(filePath);
                var task = new UploadTask
                {
                    FilePath = filePath,
                    FileName = fileInfo.Name,
                    SourceList = sourceList,
                    TotalBytes = fileInfo.Length,
                    Status = UploadStatus.Pending
                };

                lock (_queueLock)
                {
                    _pendingQueue.Add(task);
                }

                DebugLogger.Log(
                    DebugLogger.LogLevel.Info,
                    "UploadManager",
                    $"已添加上传任务: {task.FileName}");

                QueueStateChanged?.Invoke();
                return task;
            }
            catch (Exception ex)
            {
                DebugLogger.LogException("UploadManager", ex, $"添加上传任务失败: {filePath}");
                return null;
            }
        }

        /// <summary>
        /// 批量添加上传任务
        /// </summary>
        public List<UploadTask> AddBatchUploadTasks(string[] filePaths, string sourceList = "")
        {
            if (filePaths == null || filePaths.Length == 0)
            {
                return new List<UploadTask>();
            }

            var addedTasks = new List<UploadTask>();

            foreach (var filePath in filePaths)
            {
                var task = AddUploadTask(filePath, sourceList);
                if (task != null)
                {
                    addedTasks.Add(task);
                }
            }

            return addedTasks;
        }

        #endregion

        #region 公共方法 - 任务控制

        /// <summary>
        /// 暂停任务
        /// </summary>
        public void PauseTask(UploadTask task)
        {
            if (task == null)
            {
                return;
            }

            lock (_queueLock)
            {
                if (task.Status == UploadStatus.Uploading)
                {
                    task.CancellationTokenSource?.Cancel();
                    task.Status = UploadStatus.Paused;

                    DebugLogger.Log(
                        DebugLogger.LogLevel.Info,
                        "UploadManager",
                        $"已暂停任务: {task.FileName}");

                    QueueStateChanged?.Invoke();
                }
            }
        }

        /// <summary>
        /// 继续任务
        /// </summary>
        public void ResumeTask(UploadTask task)
        {
            if (task == null)
            {
                return;
            }

            lock (_queueLock)
            {
                if (task.Status == UploadStatus.Paused)
                {
                    _activeQueue.Remove(task);
                    task.Status = UploadStatus.Pending;
                    _pendingQueue.Insert(0, task);

                    DebugLogger.Log(
                        DebugLogger.LogLevel.Info,
                        "UploadManager",
                        $"已恢复任务: {task.FileName}");

                    QueueStateChanged?.Invoke();
                }
            }
        }

        /// <summary>
        /// 取消任务
        /// </summary>
        public void CancelTask(UploadTask task)
        {
            if (task == null)
            {
                return;
            }

            lock (_queueLock)
            {
                task.CancellationTokenSource?.Cancel();
                task.Status = UploadStatus.Cancelled;

                _pendingQueue.Remove(task);
                _activeQueue.Remove(task);

                task.Dispose();

                DebugLogger.Log(
                    DebugLogger.LogLevel.Info,
                    "UploadManager",
                    $"已取消任务: {task.FileName}");

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
                    task.Status = UploadStatus.Cancelled;
                    task.Dispose();
                }

                _pendingQueue.Clear();
                _activeQueue.Clear();

                DebugLogger.Log(
                    DebugLogger.LogLevel.Info,
                    "UploadManager",
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
                    "UploadManager",
                    $"已清除完成任务: {count} 个");

                QueueStateChanged?.Invoke();
            }
        }

        /// <summary>
        /// 从已完成列表中移除单个任务
        /// </summary>
        public void RemoveCompletedTask(UploadTask task)
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
        /// 获取所有进行中的任务（待上传 + 上传中）
        /// </summary>
        public List<UploadTask> GetAllActiveTasks()
        {
            lock (_queueLock)
            {
                var allActive = new List<UploadTask>();
                allActive.AddRange(_pendingQueue);
                allActive.AddRange(_activeQueue);
                return allActive;
            }
        }

        /// <summary>
        /// 获取已完成任务列表（副本）
        /// </summary>
        public List<UploadTask> GetCompletedTasks()
        {
            lock (_queueLock)
            {
                return new List<UploadTask>(_completedQueue);
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
                "UploadManager",
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

            Task? schedulerTask = _schedulerTask;
            if (schedulerTask != null && !schedulerTask.IsCompleted)
            {
                _ = schedulerTask.ContinueWith(task =>
                {
                    if (task.IsFaulted && task.Exception != null)
                    {
                        DebugLogger.LogException("UploadManager", task.Exception, "Scheduler task ended with error after cancellation");
                    }
                }, TaskScheduler.Default);
            }
            _schedulerTask = null;

            _schedulerCts?.Dispose();
            _schedulerCts = null;

            DebugLogger.Log(
                DebugLogger.LogLevel.Info,
                "UploadManager",
                "Scheduler stopped");
        }

        private async Task SchedulerLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await TryStartNextTaskAsync(cancellationToken).ConfigureAwait(false);
                    await Task.Delay(SCHEDULER_INTERVAL_MS, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    DebugLogger.LogException("UploadManager", ex, "调度循环异常");
                    await Task.Delay(SCHEDULER_INTERVAL_MS, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// 尝试启动下一个任务
        /// </summary>
        private async Task TryStartNextTaskAsync(CancellationToken cancellationToken)
        {
            UploadTask? nextTask = null;

            lock (_queueLock)
            {
                int activeCount = _activeQueue.Count(t =>
                    t.Status == UploadStatus.Uploading ||
                    t.Status == UploadStatus.Pending);

                int recommendedMax = _bandwidthCoordinator.GetRecommendedMaxConcurrentUploads(MAX_CONCURRENT_UPLOADS);

                if (!_bandwidthCoordinator.CanStartNewUpload(activeCount, recommendedMax))
                {
                    return;
                }

                if (recommendedMax <= 0)
                {
                    return;
                }

                if (_pendingQueue.Count > 0)
                {
                    nextTask = _pendingQueue[0];
                    _pendingQueue.RemoveAt(0);
                    _activeQueue.Add(nextTask);
                    nextTask.StageMessage = "等待上传...";
                }
            }

            if (nextTask != null)
            {
                _ = Task.Run(async () =>
                {
                    await ExecuteTaskAsync(nextTask, cancellationToken).ConfigureAwait(false);
                }, cancellationToken);

                await Task.Delay(TASK_START_INTERVAL_MS, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 执行任务
        /// </summary>
        private async Task ExecuteTaskAsync(UploadTask task, CancellationToken cancellationToken)
        {
            if (_apiClient == null)
            {
                task.Status = UploadStatus.Failed;
                task.ErrorMessage = "上传管理器未初始化";
                OnTaskFailed(task);
                return;
            }

            try
            {
                task.CancellationTokenSource = new CancellationTokenSource();
                task.Status = UploadStatus.Uploading;
                task.StartTime = DateTime.Now;

                var progress = new Progress<CloudUploadProgress>(p =>
                {
                    task.ProgressPercentage = p.FileProgressPercent;
                    task.StageMessage = p.StageMessage;
                    task.UploadedBytes = p.BytesTransferred > 0 ? p.BytesTransferred : task.UploadedBytes;
                    if (p.TotalBytes > 0 && task.TotalBytes <= 0)
                    {
                        task.TotalBytes = p.TotalBytes;
                    }
                    task.CurrentSpeedBytesPerSecond = p.TransferSpeedBytesPerSecond > 0
                        ? p.TransferSpeedBytesPerSecond
                        : task.CurrentSpeedBytesPerSecond;
                    TaskProgressChanged?.Invoke(task);
                });

                var result = await _apiClient.UploadCloudSongAsync(
                    task.FilePath,
                    progress,
                    task.CancellationTokenSource.Token,
                    fileIndex: 1,
                    totalFiles: 1).ConfigureAwait(false);

                if (result.Success)
                {
                    task.Status = UploadStatus.Completed;
                    task.CloudSongId = result.CloudSongId;
                    task.MatchedSongId = result.MatchedSongId;
                    task.CompletedTime = DateTime.Now;
                    task.ProgressPercentage = 100;
                    task.StageMessage = "上传完成";
                    task.UploadedBytes = task.TotalBytes;
                    task.CurrentSpeedBytesPerSecond = 0;
                    OnTaskCompleted(task);
                }
                else
                {
                    task.Status = UploadStatus.Failed;
                    task.ErrorMessage = result.ErrorMessage;
                    task.StageMessage = string.IsNullOrEmpty(result.ErrorMessage) ? "上传失败" : result.ErrorMessage;
                    OnTaskFailed(task);
                }
            }
            catch (OperationCanceledException)
            {
                task.Status = UploadStatus.Cancelled;
                task.CurrentSpeedBytesPerSecond = 0;
                task.StageMessage = "上传已取消";
                OnTaskCancelled(task);
            }
            catch (Exception ex)
            {
                task.Status = UploadStatus.Failed;
                task.ErrorMessage = ex.Message;
                task.CurrentSpeedBytesPerSecond = 0;
                task.StageMessage = ex.Message;
                DebugLogger.LogException("UploadManager", ex, $"上传任务异常: {task.FileName}");
                OnTaskFailed(task);
            }
        }

        #endregion

        #region 私有方法 - 事件处理

        private void OnTaskCompleted(UploadTask task)
        {
            lock (_queueLock)
            {
                _activeQueue.Remove(task);
                _completedQueue.Add(task);
            }

            TaskCompleted?.Invoke(task);
            QueueStateChanged?.Invoke();
        }

        private void OnTaskFailed(UploadTask task)
        {
            lock (_queueLock)
            {
                _activeQueue.Remove(task);
                _completedQueue.Add(task);
            }

            TaskFailed?.Invoke(task);
            QueueStateChanged?.Invoke();
        }

        private void OnTaskCancelled(UploadTask task)
        {
            lock (_queueLock)
            {
                _activeQueue.Remove(task);
            }

            TaskCancelled?.Invoke(task);
            QueueStateChanged?.Invoke();
        }

        #endregion

        #region 释放资源

        public void Dispose()
        {
            StopScheduler();

            lock (_queueLock)
            {
                var allTasks = _pendingQueue
                    .Concat(_activeQueue)
                    .Concat(_completedQueue)
                    .ToList();

                foreach (var task in allTasks)
                {
                    try
                    {
                        task.Dispose();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[UploadManager] 释放任务时异常: {ex.Message}");
                    }
                }

                _pendingQueue.Clear();
                _activeQueue.Clear();
                _completedQueue.Clear();
            }
        }

        #endregion
    }
}
