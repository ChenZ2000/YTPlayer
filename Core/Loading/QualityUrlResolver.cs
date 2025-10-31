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
    /// 音质 URL 解析器
    /// 批量解析歌曲的各个音质播放 URL
    /// </summary>
    public sealed class QualityUrlResolver
    {
        private readonly NeteaseApiClient _apiClient;

        public QualityUrlResolver(NeteaseApiClient apiClient)
        {
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        }

        /// <summary>
        /// 批量解析 URL（从低到高逐个音质）
        /// </summary>
        /// <param name="songIds">歌曲 ID 列表</param>
        /// <param name="token">取消令牌</param>
        public async Task ResolveBatchAsync(List<string> songIds, CancellationToken token)
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
                    // 找出需要解析该音质的歌曲
                    var needResolve = songIds.Where(id =>
                    {
                        var song = SongDataStore.Instance.GetOrCreate(id);
                        var availableQualities = song.AvailableQualities.Value;

                        // 如果该歌曲有这个音质，且 URL 未解析
                        if (availableQualities != null && availableQualities.ContainsKey(quality))
                        {
                            var container = song.GetOrCreateQualityContainer(quality);
                            return !container.IsUrlResolved.Value;
                        }

                        return false;
                    }).ToArray();

                    if (needResolve.Length == 0)
                        continue;

                    // 批量调用 GetSongUrlAsync
                    var qualityLevel = MapStringToQualityLevel(quality);
                    var urlResults = await _apiClient.GetSongUrlAsync(
                        needResolve,
                        qualityLevel,
                        skipAvailabilityCheck: true, // 已经验证过可用性
                        cancellationToken: token);

                    // 填入 URL 到数据模型
                    if (urlResults != null)
                    {
                        foreach (var kvp in urlResults)
                        {
                            string songId = kvp.Key;
                            var urlInfo = kvp.Value;

                            if (urlInfo == null || string.IsNullOrEmpty(urlInfo.Url))
                                continue;

                            var song = SongDataStore.Instance.GetOrCreate(songId);
                            var container = song.GetOrCreateQualityContainer(quality);

                            container.Url.Value = urlInfo.Url;
                            container.TotalSize.Value = urlInfo.Size;
                            container.IsUrlResolved.Value = true;

                            Utils.DebugLogger.Log(Utils.LogLevel.Debug, "QualityUrlResolver",
                                $"Resolved URL for {songId} quality {quality}: {urlInfo.Size / 1024.0 / 1024.0:F2}MB");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Utils.DebugLogger.LogException("QualityUrlResolver", ex,
                        $"Failed to resolve URLs for quality: {quality}");
                }
            }

            Utils.DebugLogger.Log(Utils.LogLevel.Info, "QualityUrlResolver",
                $"URL resolution completed for {songIds.Count} songs");
        }

        private QualityLevel MapStringToQualityLevel(string level)
        {
            switch (level?.ToLower())
            {
                case "standard": return QualityLevel.Standard;
                case "higher": return QualityLevel.Higher;
                case "exhigh": return QualityLevel.ExHigh;
                case "lossless": return QualityLevel.Lossless;
                case "hires": return QualityLevel.HiRes;
                case "jymaster": return QualityLevel.JyMaster;
                case "sky": return QualityLevel.Sky;
                default: return QualityLevel.Standard;
            }
        }
    }
}
