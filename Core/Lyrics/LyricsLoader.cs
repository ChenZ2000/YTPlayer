using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using YTPlayer.Utils;

namespace YTPlayer.Core.Lyrics
{
    /// <summary>
    /// 歌词加载助手类，用于在播放流程中加载和解析歌词
    /// </summary>
    public sealed class LyricsLoader
    {
        private readonly NeteaseApiClient _apiClient;

        public LyricsLoader(NeteaseApiClient apiClient)
        {
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        }

        /// <summary>
        /// 异步加载并解析歌词数据
        /// 此方法设计为与 chunk 0 下载并行执行，不阻塞播放启动
        /// </summary>
        public async Task<LoadedLyricsContext?> LoadLyricsAsync(string songId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(songId))
            {
                DebugLogger.Log(DebugLogger.LogLevel.Warning, "LyricsLoader", "歌曲ID为空，跳过歌词加载");
                return null;
            }

            var stopwatch = Stopwatch.StartNew();

            try
            {
                DebugLogger.Log(
                    DebugLogger.LogLevel.Info,
                    "LyricsLoader",
                    $"⭐ 开始加载歌词: songId={songId}");

                // 统一歌词链路：拉取 + 语言选择 + 解析 + 输出构建
                var loadedContext = await _apiClient
                    .GetResolvedLyricsAsync(songId, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                stopwatch.Stop();

                if (loadedContext == null || loadedContext.LyricsData == null || loadedContext.LyricsData.IsEmpty)
                {
                    DebugLogger.Log(
                        DebugLogger.LogLevel.Warning,
                        "LyricsLoader",
                        $"歌词解析后为空，耗时 {stopwatch.ElapsedMilliseconds}ms");
                    return null;
                }

                // 检查取消
                cancellationToken.ThrowIfCancellationRequested();

                var lyricsData = loadedContext.LyricsData;
                string selectedLanguages = string.Join("/", loadedContext.SelectedLanguageKeys);
                string availableLanguages = string.Join("/", loadedContext.LanguageProfile.Tracks.Select(track => track.DisplayName));

                DebugLogger.Log(
                    DebugLogger.LogLevel.Info,
                    "LyricsLoader",
                    $"✓ 歌词加载成功: {lyricsData.Lines.Count} 行, " +
                    $"逐字={lyricsData.HasWordTimings}, " +
                    $"翻译={lyricsData.HasTranslation}, " +
                    $"罗马音={lyricsData.HasRomaLyric}, " +
                    $"可选语言={availableLanguages}, 已选={selectedLanguages}, " +
                    $"耗时 {stopwatch.ElapsedMilliseconds}ms");

                return loadedContext;
            }
            catch (OperationCanceledException)
            {
                DebugLogger.Log(
                    DebugLogger.LogLevel.Info,
                    "LyricsLoader",
                    $"歌词加载被取消，耗时 {stopwatch.ElapsedMilliseconds}ms");
                return null;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                DebugLogger.LogException(
                    "LyricsLoader",
                    ex,
                    $"歌词加载失败，耗时 {stopwatch.ElapsedMilliseconds}ms");
                return null;
            }
        }

    }
}
