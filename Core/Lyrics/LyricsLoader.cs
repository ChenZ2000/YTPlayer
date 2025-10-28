using System;
using System.Diagnostics;
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
        public async Task<LyricsData?> LoadLyricsAsync(string songId, CancellationToken cancellationToken = default)
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

                // 从API获取歌词
                var lyricInfo = await _apiClient.GetLyricsAsync(songId).ConfigureAwait(false);

                stopwatch.Stop();

                if (lyricInfo == null)
                {
                    DebugLogger.Log(
                        DebugLogger.LogLevel.Warning,
                        "LyricsLoader",
                        $"API返回空歌词信息，耗时 {stopwatch.ElapsedMilliseconds}ms");
                    return null;
                }

                // 检查取消
                cancellationToken.ThrowIfCancellationRequested();

                // 优先使用 YrcLyric（逐字歌词），如果没有则使用 Lyric
                string primaryLyric = !string.IsNullOrWhiteSpace(lyricInfo.YrcLyric)
                    ? lyricInfo.YrcLyric
                    : lyricInfo.Lyric;

                if (string.IsNullOrWhiteSpace(primaryLyric))
                {
                    DebugLogger.Log(
                        DebugLogger.LogLevel.Info,
                        "LyricsLoader",
                        $"歌曲无歌词内容，耗时 {stopwatch.ElapsedMilliseconds}ms");
                    return null;
                }

                // 解析歌词数据
                var lyricsData = EnhancedLyricsParser.ParseLyricsData(
                    songId,
                    primaryLyric,
                    lyricInfo.TLyric,
                    lyricInfo.RomaLyric
                );

                if (lyricsData.IsEmpty)
                {
                    DebugLogger.Log(
                        DebugLogger.LogLevel.Warning,
                        "LyricsLoader",
                        $"歌词解析后为空，耗时 {stopwatch.ElapsedMilliseconds}ms");
                    return null;
                }

                DebugLogger.Log(
                    DebugLogger.LogLevel.Info,
                    "LyricsLoader",
                    $"✓ 歌词加载成功: {lyricsData.Lines.Count} 行, " +
                    $"逐字={lyricsData.HasWordTimings}, " +
                    $"翻译={lyricsData.HasTranslation}, " +
                    $"罗马音={lyricsData.HasRomaLyric}, " +
                    $"耗时 {stopwatch.ElapsedMilliseconds}ms");

                return lyricsData;
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

        /// <summary>
        /// 同步加载歌词（用于向后兼容，不推荐使用）
        /// </summary>
        public LyricsData? LoadLyrics(string songId)
        {
            try
            {
                return LoadLyricsAsync(songId).ConfigureAwait(false).GetAwaiter().GetResult();
            }
            catch
            {
                return null;
            }
        }
    }
}
