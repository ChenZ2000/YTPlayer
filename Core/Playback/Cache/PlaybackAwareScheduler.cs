using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using YTPlayer.Models.Playback;
using YTPlayer.Utils;

namespace YTPlayer.Core.Playback.Cache
{
    /// <summary>
    /// 播放感知调度器
    /// 根据播放位置动态调整下载优先级，确保播放流畅
    /// </summary>
    public sealed class PlaybackAwareScheduler
    {
        private readonly DynamicHotspotManager _hotspotManager;
        private readonly HttpClient _httpClient;
        private AudioQualityContainer _activeContainer;

        private CancellationTokenSource _schedulerCts;
        private Task _schedulerTask;

        private readonly object _lock = new object();
        private bool _isRunning;

        /// <summary>
        /// 最大并发下载数
        /// </summary>
        private const int MAX_CONCURRENT_DOWNLOADS = 8;

        private readonly SemaphoreSlim _downloadSemaphore;
        private readonly IDictionary<string, string>? _headers;

        public PlaybackAwareScheduler(DynamicHotspotManager hotspotManager, HttpClient httpClient, IDictionary<string, string>? headers = null)
        {
            _hotspotManager = hotspotManager ?? throw new ArgumentNullException(nameof(hotspotManager));
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _downloadSemaphore = new SemaphoreSlim(MAX_CONCURRENT_DOWNLOADS, MAX_CONCURRENT_DOWNLOADS);
            _headers = headers;
        }

        /// <summary>
        /// 启动调度器
        /// </summary>
        public void Start(AudioQualityContainer container)
        {
            lock (_lock)
            {
                if (_isRunning)
                {
                    Stop();
                }

                _activeContainer = container ?? throw new ArgumentNullException(nameof(container));
                _schedulerCts = new CancellationTokenSource();
                _isRunning = true;

                _schedulerTask = Task.Run(() => SchedulerLoop(_schedulerCts.Token), _schedulerCts.Token);
            }
        }

        /// <summary>
        /// 停止调度器
        /// </summary>
        public void Stop()
        {
            lock (_lock)
            {
                if (!_isRunning)
                    return;

                _schedulerCts?.Cancel();
                _isRunning = false;
            }

            try
            {
                _schedulerTask?.Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                Utils.DebugLogger.Log(Utils.LogLevel.Warning, "PlaybackAwareScheduler",
                    $"Stop exception: {ex.Message}");
            }
        }

        /// <summary>
        /// 调度循环
        /// </summary>
        private async Task SchedulerLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // 获取需要下载的块
                    var chunksToDownload = GetPriorityChunks();

                    if (chunksToDownload.Count == 0)
                    {
                        // 没有需要下载的块，等待一会儿
                        await Task.Delay(500, token);
                        continue;
                    }

                    // 启动并发下载
                    var downloadTasks = chunksToDownload.Select(chunkIndex =>
                        DownloadChunkAsync(chunkIndex, token)
                    ).ToList();

                    await Task.WhenAll(downloadTasks);

                    // 短暂延迟避免过度轮询
                    await Task.Delay(100, token);
                }
                catch (OperationCanceledException)
                {
                    // 正常取消
                    break;
                }
                catch (Exception ex)
                {
                    Utils.DebugLogger.LogException("PlaybackAwareScheduler", ex, "Scheduler loop error");
                    await Task.Delay(1000, token); // 出错后等待1秒
                }
            }
        }

        /// <summary>
        /// 获取优先下载的块列表
        /// </summary>
        private List<int> GetPriorityChunks()
        {
            if (_activeContainer == null)
                return new List<int>();

            var (startChunk, endChunk) = _hotspotManager.GetHotWindow();
            int totalChunks = _activeContainer.TotalChunks.Value;

            if (totalChunks == 0)
                return new List<int>();

            var priorityChunks = new List<(int index, int priority)>();

            // P0: 前方 6 块（最高优先级）
            int currentChunk = _hotspotManager.GetCurrentChunkIndex();
            for (int i = 1; i <= 6; i++)
            {
                int chunkIndex = currentChunk + i;
                if (chunkIndex < totalChunks && !_activeContainer.TryGetChunk(chunkIndex, out var chunk) || !chunk.IsReady)
                {
                    priorityChunks.Add((chunkIndex, -1000 + i));
                }
            }

            // P1: 后方 2 块
            for (int i = 1; i <= 2; i++)
            {
                int chunkIndex = currentChunk - i;
                if (chunkIndex >= 0 && (!_activeContainer.TryGetChunk(chunkIndex, out var chunk) || !chunk.IsReady))
                {
                    priorityChunks.Add((chunkIndex, -900 + i));
                }
            }

            // P2: 最后 3 块（用于平滑结尾）
            for (int i = Math.Max(0, totalChunks - 3); i < totalChunks; i++)
            {
                if (!_activeContainer.TryGetChunk(i, out var chunk) || !chunk.IsReady)
                {
                    if (!priorityChunks.Any(p => p.index == i))
                    {
                        priorityChunks.Add((i, -800));
                    }
                }
            }

            // 按优先级排序并返回前 MAX_CONCURRENT_DOWNLOADS 个
            return priorityChunks
                .OrderBy(p => p.priority)
                .Take(MAX_CONCURRENT_DOWNLOADS)
                .Select(p => p.index)
                .ToList();
        }

        /// <summary>
        /// 下载单个块
        /// </summary>
        private async Task DownloadChunkAsync(int chunkIndex, CancellationToken token)
        {
            if (_activeContainer == null)
                return;

            // 检查是否已经在下载或已下载
            if (_activeContainer.TryGetChunk(chunkIndex, out var existingChunk) && existingChunk.IsReady)
                return;

            await _downloadSemaphore.WaitAsync(token);

            try
            {
                // 再次检查（避免重复下载）
                if (_activeContainer.TryGetChunk(chunkIndex, out var recheckChunk) && recheckChunk.IsReady)
                    return;

                string url = _activeContainer.Url.Value;
                if (string.IsNullOrEmpty(url))
                    return;

                long totalSize = _activeContainer.TotalSize.Value;
                int chunkSize = AudioQualityContainer.CHUNK_SIZE;

                long startByte = chunkIndex * (long)chunkSize;
                long endByte = Math.Min(startByte + chunkSize - 1, totalSize - 1);

                // 创建槽位并标记为加载中
                var slot = _activeContainer.GetOrCreateChunk(chunkIndex);
                slot.State.Value = ChunkState.Loading;

                // 下载数据
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(startByte, endByte);
                    request.ApplyCustomHeaders(_headers);

                    using (var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token))
                    {
                        response.EnsureSuccessStatusCode();

                        byte[] data = await response.Content.ReadAsByteArrayAsync();

                        if (data.Length > 0)
                        {
                            slot.SetData(data);

                            Utils.DebugLogger.Log(Utils.LogLevel.Debug, "PlaybackAwareScheduler",
                                $"Downloaded chunk {chunkIndex} ({data.Length} bytes)");
                        }
                        else
                        {
                            slot.State.Value = ChunkState.Failed;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 取消是正常的
            }
            catch (Exception ex)
            {
                Utils.DebugLogger.LogException("PlaybackAwareScheduler", ex,
                    $"Failed to download chunk {chunkIndex}");

                if (_activeContainer.TryGetChunk(chunkIndex, out var slot))
                {
                    slot.State.Value = ChunkState.Failed;
                }
            }
            finally
            {
                _downloadSemaphore.Release();
            }
        }

        /// <summary>
        /// 紧急请求块（用于缓存未命中）
        /// </summary>
        public async Task<bool> RequestUrgentChunkAsync(int chunkIndex, CancellationToken token)
        {
            try
            {
                await DownloadChunkAsync(chunkIndex, token);

                if (_activeContainer.TryGetChunk(chunkIndex, out var chunk))
                {
                    return chunk.IsReady;
                }

                return false;
            }
            catch (Exception ex)
            {
                Utils.DebugLogger.LogException("PlaybackAwareScheduler", ex,
                    $"Urgent chunk request failed: {chunkIndex}");
                return false;
            }
        }
    }
}
