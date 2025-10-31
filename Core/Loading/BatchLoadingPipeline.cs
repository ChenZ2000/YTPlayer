using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using YTPlayer.Core.Data;
using YTPlayer.Models;

namespace YTPlayer.Core.Loading
{
    /// <summary>
    /// 批量加载管道
    /// 负责批量加载歌曲的资源验证、URL解析和Chunk 0预加载
    /// </summary>
    public sealed class BatchLoadingPipeline
    {
        private const int BATCH_SIZE = 100; // 每批100首
        private const int MAX_CONCURRENT_BATCHES = 3; // 最多3批并发

        private readonly SemaphoreSlim _concurrencyLimiter;
        private readonly NeteaseApiClient _apiClient;
        private readonly System.Net.Http.HttpClient _httpClient;

        private readonly ResourceValidator _validator;
        private readonly QualityUrlResolver _urlResolver;
        private readonly Chunk0Preloader _chunk0Loader;

        /// <summary>
        /// 是否启用完整批量加载（默认禁用，使用按需加载）
        /// </summary>
        public bool EnableFullBatchLoading { get; set; } = false;

        public BatchLoadingPipeline()
        {
            _concurrencyLimiter = new SemaphoreSlim(MAX_CONCURRENT_BATCHES, MAX_CONCURRENT_BATCHES);
            _apiClient = NeteaseApiClient.Instance;
            _httpClient = new System.Net.Http.HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };

            _validator = new ResourceValidator(_apiClient);
            _urlResolver = new QualityUrlResolver(_apiClient);
            _chunk0Loader = new Chunk0Preloader(_httpClient);
        }

        /// <summary>
        /// 执行批量加载（三阶段流水线）
        /// </summary>
        public async Task ExecuteAsync(ViewContainer view, CancellationToken token)
        {
            if (view == null)
                throw new ArgumentNullException(nameof(view));

            var songIds = view.SongIds.Value;
            if (songIds == null || songIds.Count == 0)
            {
                Utils.DebugLogger.Log(Utils.LogLevel.Warning, "BatchLoadingPipeline",
                    "No songs to load");
                return;
            }

            Utils.DebugLogger.Log(Utils.LogLevel.Info, "BatchLoadingPipeline",
                $"Starting batch loading for {songIds.Count} songs in view {view.ViewId}, " +
                $"FullLoading={EnableFullBatchLoading}");

            // 分批
            var batches = ChunkList(songIds, BATCH_SIZE);

            if (EnableFullBatchLoading)
            {
                // 完整批量加载模式
                await ExecuteFullLoadingAsync(view, batches, token);
            }
            else
            {
                // 简化模式（默认）- 仅标记状态，按需加载
                Utils.DebugLogger.Log(Utils.LogLevel.Info, "BatchLoadingPipeline",
                    "Using on-demand loading mode (recommended)");

                view.ValidatedCount.Value = songIds.Count;
                view.UrlResolvedCount.Value = 0;
                view.Chunk0LoadedCount.Value = 0;
            }

            Utils.DebugLogger.Log(Utils.LogLevel.Info, "BatchLoadingPipeline",
                $"Batch loading completed for view {view.ViewId}");
        }

        /// <summary>
        /// 执行完整批量加载（三阶段）
        /// </summary>
        private async Task ExecuteFullLoadingAsync(ViewContainer view, List<List<string>> batches, CancellationToken token)
        {
            // 阶段1：资源有效性检查（3批并发）
            Utils.DebugLogger.Log(Utils.LogLevel.Info, "BatchLoadingPipeline",
                "Phase 1: Resource validation (3 batches concurrent)");

            await ProcessBatchesAsync(batches, async (batch, batchIndex) =>
            {
                var results = await _validator.ValidateBatchAsync(batch, token);
                foreach (var (songId, qualities) in results)
                {
                    var song = SongDataStore.Instance.GetOrCreate(songId);
                    song.AvailableQualities.Value = qualities;
                }
                view.ValidatedCount.Value += batch.Count;
            }, token);

            // 阶段2：URL 批量解析（从低到高）
            Utils.DebugLogger.Log(Utils.LogLevel.Info, "BatchLoadingPipeline",
                "Phase 2: URL resolution (3 batches concurrent)");

            await ProcessBatchesAsync(batches, async (batch, batchIndex) =>
            {
                var validSongs = batch.Where(id =>
                {
                    var song = SongDataStore.Instance.GetOrCreate(id);
                    return song.AvailableQualities.Value != null &&
                           song.AvailableQualities.Value.Count > 0;
                }).ToList();

                if (validSongs.Count > 0)
                {
                    await _urlResolver.ResolveBatchAsync(validSongs, token);
                    view.UrlResolvedCount.Value += validSongs.Count;
                }
            }, token);

            // 阶段3：Chunk 0 批量预加载（从低到高）
            Utils.DebugLogger.Log(Utils.LogLevel.Info, "BatchLoadingPipeline",
                "Phase 3: Chunk 0 preloading (3 batches concurrent)");

            await ProcessBatchesAsync(batches, async (batch, batchIndex) =>
            {
                var readySongs = batch.Where(id =>
                {
                    var song = SongDataStore.Instance.GetOrCreate(id);
                    return song.GetAllQualityContainers().Any(c => c.IsUrlResolved.Value);
                }).ToList();

                if (readySongs.Count > 0)
                {
                    await _chunk0Loader.PreloadBatchAsync(readySongs, token);
                    view.Chunk0LoadedCount.Value += readySongs.Count;
                }
            }, token);
        }

        /// <summary>
        /// 处理批次（限制并发数）
        /// </summary>
        private async Task ProcessBatchesAsync(
            List<List<string>> batches,
            Func<List<string>, int, Task> processor,
            CancellationToken token)
        {
            var tasks = new List<Task>();

            for (int i = 0; i < batches.Count; i++)
            {
                await _concurrencyLimiter.WaitAsync(token);

                int batchIndex = i;
                var task = Task.Run(async () =>
                {
                    try
                    {
                        await processor(batches[batchIndex], batchIndex);
                    }
                    finally
                    {
                        _concurrencyLimiter.Release();
                    }
                }, token);

                tasks.Add(task);
            }

            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// 将列表分批
        /// </summary>
        private List<List<T>> ChunkList<T>(List<T> source, int chunkSize)
        {
            var result = new List<List<T>>();

            for (int i = 0; i < source.Count; i += chunkSize)
            {
                int count = Math.Min(chunkSize, source.Count - i);
                result.Add(source.GetRange(i, count));
            }

            return result;
        }
    }
}
