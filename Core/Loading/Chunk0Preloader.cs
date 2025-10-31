using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using YTPlayer.Core.Data;
using YTPlayer.Models.Playback;

namespace YTPlayer.Core.Loading
{
    /// <summary>
    /// Chunk 0 预加载器
    /// 批量预加载歌曲的第一个数据块（用于快速启动播放）
    /// </summary>
    public sealed class Chunk0Preloader
    {
        private readonly HttpClient _httpClient;

        public Chunk0Preloader(HttpClient httpClient)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        }

        /// <summary>
        /// 批量预加载 Chunk 0（从低到高逐个音质）
        /// </summary>
        /// <param name="songIds">歌曲 ID 列表</param>
        /// <param name="token">取消令牌</param>
        public async Task PreloadBatchAsync(List<string> songIds, CancellationToken token)
        {
            if (songIds == null || songIds.Count == 0)
                return;

            // 从低到高的音质顺序
            var qualityOrder = new[] { "standard", "exhigh", "lossless", "hires", "jymaster", "sky" };

            foreach (var quality in qualityOrder)
            {
                token.ThrowIfCancellationRequested();

                try
                {
                    // 找出需要预加载该音质 Chunk 0 的歌曲
                    var needPreload = new List<(string songId, AudioQualityContainer container)>();

                    foreach (var songId in songIds)
                    {
                        var song = SongDataStore.Instance.GetOrCreate(songId);

                        if (song.TryGetQualityContainer(quality, out var container) &&
                            container.IsUrlResolved.Value &&
                            !string.IsNullOrEmpty(container.Url.Value))
                        {
                            // 检查 Chunk 0 是否已加载
                            if (!container.TryGetChunk(0, out var chunk0) || !chunk0.IsReady)
                            {
                                needPreload.Add((songId, container));
                            }
                        }
                    }

                    if (needPreload.Count == 0)
                        continue;

                    // 并发下载 Chunk 0
                    var downloadTasks = needPreload.Select(item =>
                        DownloadChunk0Async(item.songId, item.container, token)
                    );

                    await Task.WhenAll(downloadTasks);

                    Utils.DebugLogger.Log(Utils.LogLevel.Info, "Chunk0Preloader",
                        $"Preloaded Chunk 0 for {needPreload.Count} songs (quality: {quality})");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Utils.DebugLogger.LogException("Chunk0Preloader", ex,
                        $"Failed to preload Chunk 0 for quality: {quality}");
                }
            }
        }

        /// <summary>
        /// 下载单首歌曲的 Chunk 0
        /// </summary>
        private async Task DownloadChunk0Async(string songId, AudioQualityContainer container, CancellationToken token)
        {
            try
            {
                string url = container.Url.Value;
                if (string.IsNullOrEmpty(url))
                    return;

                int chunkSize = AudioQualityContainer.CHUNK_SIZE;
                long totalSize = container.TotalSize.Value;

                // 下载第一个 256KB
                long endByte = Math.Min(chunkSize - 1, totalSize - 1);

                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, endByte);

                    using (var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            Utils.DebugLogger.Log(Utils.LogLevel.Warning, "Chunk0Preloader",
                                $"Failed to download Chunk 0 for {songId}: {response.StatusCode}");
                            return;
                        }

                        byte[] data = await response.Content.ReadAsByteArrayAsync();

                        if (data.Length > 0)
                        {
                            // 创建 Chunk 0 槽位并填入数据
                            var chunk0 = container.GetOrCreateChunk(0);
                            chunk0.SetData(data);

                            Utils.DebugLogger.Log(Utils.LogLevel.Debug, "Chunk0Preloader",
                                $"Chunk 0 preloaded for {songId}: {data.Length} bytes");
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Utils.DebugLogger.LogException("Chunk0Preloader", ex,
                    $"Error downloading Chunk 0 for {songId}");
            }
        }
    }
}
