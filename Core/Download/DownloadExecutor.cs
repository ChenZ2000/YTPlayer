using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using YTPlayer.Core.Streaming;
using YTPlayer.Models.Download;
using YTPlayer.Utils;

namespace YTPlayer.Core.Download
{
    /// <summary>
    /// 单个下载任务的执行器
    /// 负责执行 HTTP 下载、断点续传、进度报告、重试逻辑
    /// </summary>
    public class DownloadExecutor
    {
        #region 常量

        /// <summary>
        /// 最大重试次数
        /// </summary>
        private const int MAX_RETRY_COUNT = 3;

        /// <summary>
        /// 重试基础延迟（毫秒）
        /// </summary>
        private const int RETRY_BASE_DELAY_MS = 1000;

        /// <summary>
        /// 下载缓冲区大小（64KB）
        /// </summary>
        private const int BUFFER_SIZE = 64 * 1024;

        /// <summary>
        /// 进度回调间隔（毫秒）
        /// </summary>
        private const int PROGRESS_CALLBACK_INTERVAL_MS = 500;

        #endregion

        #region 事件

        /// <summary>
        /// 进度更新事件
        /// </summary>
        public event Action<DownloadTask>? ProgressChanged;

        /// <summary>
        /// 下载完成事件
        /// </summary>
        public event Action<DownloadTask>? DownloadCompleted;

        /// <summary>
        /// 下载失败事件
        /// </summary>
        public event Action<DownloadTask, Exception>? DownloadFailed;

        #endregion

        #region 私有字段

        private readonly HttpClient _httpClient;
        private readonly NeteaseApiClient? _apiClient;
        private readonly AudioMetadataWriter? _metadataWriter;
        private bool _disposed;

        #endregion

        #region 构造和释放

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="apiClient">API客户端（可选，用于写入元数据和歌词）</param>
        public DownloadExecutor(NeteaseApiClient? apiClient = null)
        {
            // 使用针对下载优化的 HttpClient（较长超时）
            _httpClient = OptimizedHttpClientFactory.CreateForPreCache(TimeSpan.FromMinutes(30));
            _disposed = false;
            _apiClient = apiClient;

            // 如果提供了 API 客户端，创建元数据写入器
            if (_apiClient != null)
            {
                _metadataWriter = new AudioMetadataWriter(_apiClient);
            }
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                _httpClient?.Dispose();
                _metadataWriter?.Dispose();
                _disposed = true;
            }
        }

        #endregion

        #region 公共方法

        /// <summary>
        /// 执行下载任务
        /// </summary>
        /// <param name="task">下载任务</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>下载是否成功</returns>
        public async Task<bool> ExecuteAsync(DownloadTask task, CancellationToken cancellationToken)
        {
            if (task == null)
            {
                throw new ArgumentNullException(nameof(task));
            }

            if (task.ContentType == DownloadContentType.Lyrics)
            {
                return await ExecuteLyricDownloadAsync(task, cancellationToken).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(task.DownloadUrl))
            {
                task.Status = DownloadStatus.Failed;
                task.ErrorMessage = "下载链接为空";
                DownloadFailed?.Invoke(task, new InvalidOperationException("下载链接为空"));
                return false;
            }

            // 重试循环
            for (int retry = 0; retry <= MAX_RETRY_COUNT; retry++)
            {
                try
                {
                    // 检查取消
                    cancellationToken.ThrowIfCancellationRequested();

                    // 执行下载
                    bool success = await ExecuteDownloadWithResumeAsync(task, cancellationToken).ConfigureAwait(false);

                    if (success)
                    {
                        // 下载成功，重命名临时文件
                        DownloadFileHelper.RenameTempFile(task.TempFilePath, task.DestinationPath);

                        // 写入元数据和歌词（如果启用）
                        if (_metadataWriter != null)
                        {
                            DebugLogger.Log(
                                DebugLogger.LogLevel.Info,
                                "DownloadExecutor",
                                $"开始写入元数据和歌词: {task.Song.Name}");

                            try
                            {
                                await _metadataWriter.WriteMetadataAsync(
                                    task.DestinationPath,
                                    task.Song,
                                    task.TrackNumber,
                                    cancellationToken).ConfigureAwait(false);
                            }
                            catch (Exception metadataEx)
                            {
                                // 元数据写入失败不应导致整个任务失败
                                DebugLogger.LogException(
                                    "DownloadExecutor",
                                    metadataEx,
                                    $"元数据写入失败（非致命）: {task.Song.Name}");
                            }
                        }

                        task.Status = DownloadStatus.Completed;
                        task.CompletedTime = DateTime.Now;
                        task.DownloadSpeed = 0;

                        DebugLogger.Log(
                            DebugLogger.LogLevel.Info,
                            "DownloadExecutor",
                            $"下载完成: {task.Song.Name} - {task.Song.Artist}");

                        DownloadCompleted?.Invoke(task);
                        return true;
                    }

                    // 如果不成功但没有抛出异常，说明被取消
                    return false;
                }
                catch (OperationCanceledException)
                {
                    // 用户取消，不重试
                    task.Status = DownloadStatus.Cancelled;
                    task.ErrorMessage = "用户取消下载";
                    DebugLogger.Log(
                        DebugLogger.LogLevel.Info,
                        "DownloadExecutor",
                        $"下载已取消: {task.Song.Name}");
                    return false;
                }
                catch (Exception ex)
                {
                    task.RetryCount = retry;

                    if (retry < MAX_RETRY_COUNT)
                    {
                        // 计算指数退避延迟
                        int delayMs = RETRY_BASE_DELAY_MS * (int)Math.Pow(2, retry);

                        DebugLogger.Log(
                            DebugLogger.LogLevel.Warning,
                            "DownloadExecutor",
                            $"下载失败，{delayMs}ms 后重试 ({retry + 1}/{MAX_RETRY_COUNT}): {task.Song.Name}, 错误: {ex.Message}");

                        // 等待后重试
                        await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        // 重试次数用尽，失败
                        task.Status = DownloadStatus.Failed;
                        task.ErrorMessage = $"下载失败: {ex.Message}";

                        DebugLogger.LogException(
                            "DownloadExecutor",
                            ex,
                            $"下载最终失败: {task.Song.Name}");

                        DownloadFailed?.Invoke(task, ex);
                        return false;
                    }
                }
            }

            return false;
        }

        #endregion

        #region 私有方法

        private async Task<bool> ExecuteLyricDownloadAsync(DownloadTask task, CancellationToken cancellationToken)
        {
            try
            {
                var lyricContent = await ResolveLyricContentAsync(task, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(lyricContent))
                {
                    task.Status = DownloadStatus.Failed;
                    task.ErrorMessage = "该歌曲没有歌词";
                    DownloadFailed?.Invoke(task, new InvalidOperationException(task.ErrorMessage));
                    return false;
                }
                string lyricText = lyricContent!;

                string? directory = Path.GetDirectoryName(task.DestinationPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    DownloadFileHelper.EnsureDirectoryExists(directory);
                }

                task.Status = DownloadStatus.Downloading;
                using (var writer = new StreamWriter(task.DestinationPath, false, Encoding.UTF8))
                {
                    await writer.WriteAsync(lyricText).ConfigureAwait(false);
                }

                task.LyricContent = null;

                var info = new FileInfo(task.DestinationPath);
                task.TotalBytes = info.Exists ? info.Length : lyricText.Length;
                task.DownloadedBytes = task.TotalBytes;
                task.DownloadSpeed = 0;
                task.CompletedTime = DateTime.Now;
                task.Status = DownloadStatus.Completed;
                DownloadCompleted?.Invoke(task);
                return true;
            }
            catch (OperationCanceledException)
            {
                task.Status = DownloadStatus.Cancelled;
                throw;
            }
            catch (Exception ex)
            {
                task.Status = DownloadStatus.Failed;
                task.ErrorMessage = ex.Message;
                DownloadFailed?.Invoke(task, ex);
                return false;
            }
        }

        private async Task<string?> ResolveLyricContentAsync(DownloadTask task, CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(task.LyricContent))
            {
                return task.LyricContent;
            }

            if (_apiClient == null || task.Song == null || string.IsNullOrWhiteSpace(task.Song.Id))
            {
                return null;
            }

            var lyricInfo = await _apiClient.GetLyricsAsync(task.Song.Id!).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return lyricInfo?.Lyric;
        }

        /// <summary>
        /// 执行下载（支持断点续传）
        /// </summary>
        private async Task<bool> ExecuteDownloadWithResumeAsync(
            DownloadTask task,
            CancellationToken cancellationToken)
        {
            // 确保目标目录存在
            string? directory = Path.GetDirectoryName(task.DestinationPath);
            if (!string.IsNullOrEmpty(directory))
            {
                DownloadFileHelper.EnsureDirectoryExists(directory);
            }

            // 检查是否有未完成的临时文件（断点续传）
            long startPosition = 0;
            if (File.Exists(task.TempFilePath))
            {
                var fileInfo = new FileInfo(task.TempFilePath);
                startPosition = fileInfo.Length;
                task.DownloadedBytes = startPosition;

                DebugLogger.Log(
                    DebugLogger.LogLevel.Info,
                    "DownloadExecutor",
                    $"检测到未完成下载，从 {DownloadFileHelper.FormatFileSize(startPosition)} 继续: {task.Song.Name}");
            }

            // 创建 HTTP 请求
            var request = new HttpRequestMessage(HttpMethod.Get, task.DownloadUrl);

            // 如果支持断点续传，设置 Range 头
            if (startPosition > 0)
            {
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(startPosition, null);
            }

            // 发送请求
            HttpResponseMessage? response = null;
            try
            {
                response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);

                // 检查响应状态
                if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.PartialContent)
                {
                    throw new HttpRequestException($"HTTP 请求失败: {response.StatusCode}");
                }

                // 获取内容总大小
                if (task.TotalBytes == 0)
                {
                    task.TotalBytes = response.Content.Headers.ContentLength ?? 0;
                    if (task.TotalBytes == 0 && startPosition == 0)
                    {
                        throw new InvalidOperationException("无法获取文件大小");
                    }
                }

                // 如果服务器不支持 Range 请求（返回 200 而不是 206），需要从头下载
                if (startPosition > 0 && response.StatusCode != HttpStatusCode.PartialContent)
                {
                    DebugLogger.Log(
                        DebugLogger.LogLevel.Warning,
                        "DownloadExecutor",
                        $"服务器不支持断点续传，从头开始下载: {task.Song.Name}");

                    startPosition = 0;
                    task.DownloadedBytes = 0;

                    // 删除旧的临时文件
                    DownloadFileHelper.DeleteTempFileIfExists(task.TempFilePath);
                }

                // 开始下载到临时文件
                using (var contentStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                {
                    // 打开或创建临时文件
                    FileMode fileMode = startPosition > 0 ? FileMode.Append : FileMode.Create;
                    using (var fileStream = new FileStream(
                        task.TempFilePath,
                        fileMode,
                        FileAccess.Write,
                        FileShare.None,  // 独占访问，防止被删除
                        BUFFER_SIZE))
                    {
                        // 保存文件句柄到任务（占用文件）
                        task.FileHandle = fileStream;

                        // 下载数据
                        await DownloadDataAsync(
                            task,
                            contentStream,
                            fileStream,
                            cancellationToken).ConfigureAwait(false);
                    }
                }

                return true;
            }
            finally
            {
                response?.Dispose();
                request.Dispose();
                task.FileHandle = null;  // 释放文件句柄
            }
        }

        /// <summary>
        /// 下载数据到文件
        /// </summary>
        private async Task DownloadDataAsync(
            DownloadTask task,
            Stream contentStream,
            FileStream fileStream,
            CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[BUFFER_SIZE];
            int bytesRead;

            Stopwatch stopwatch = Stopwatch.StartNew();
            long lastReportedBytes = task.DownloadedBytes;
            long lastReportTime = stopwatch.ElapsedMilliseconds;

            task.Status = DownloadStatus.Downloading;

            while ((bytesRead = await contentStream.ReadAsync(
                buffer,
                0,
                buffer.Length,
                cancellationToken).ConfigureAwait(false)) > 0)
            {
                // 写入文件
                await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken).ConfigureAwait(false);

                // 更新已下载字节数
                task.DownloadedBytes += bytesRead;

                // 计算速度并触发进度回调
                long currentTime = stopwatch.ElapsedMilliseconds;
                if (currentTime - lastReportTime >= PROGRESS_CALLBACK_INTERVAL_MS)
                {
                    // 计算速度（bytes/s）
                    long bytesDelta = task.DownloadedBytes - lastReportedBytes;
                    double timeDeltaSeconds = (currentTime - lastReportTime) / 1000.0;
                    task.DownloadSpeed = bytesDelta / timeDeltaSeconds;

                    // 触发进度事件
                    ProgressChanged?.Invoke(task);

                    // 更新上次报告的值
                    lastReportedBytes = task.DownloadedBytes;
                    lastReportTime = currentTime;
                }

                // 检查取消
                cancellationToken.ThrowIfCancellationRequested();
            }

            // 确保刷新到磁盘
            await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);

            // 最后一次进度回调
            task.DownloadSpeed = 0;
            ProgressChanged?.Invoke(task);
        }

        #endregion
    }
}
