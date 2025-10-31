using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using YTPlayer.Core.Data;
using YTPlayer.Models;
using YTPlayer.Models.Playback;

namespace YTPlayer.Core.Loading
{
    /// <summary>
    /// 资源有效性检查器
    /// 批量检查歌曲在网易云曲库中的可用性和支持的音质
    /// </summary>
    public sealed class ResourceValidator
    {
        private readonly NeteaseApiClient _apiClient;

        public ResourceValidator(NeteaseApiClient apiClient)
        {
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        }

        /// <summary>
        /// 批量验证歌曲资源有效性
        /// </summary>
        /// <param name="songIds">歌曲 ID 列表</param>
        /// <param name="token">取消令牌</param>
        /// <returns>字典：songId -> 可用音质信息</returns>
        public async Task<Dictionary<string, Dictionary<string, QualityInfo>>> ValidateBatchAsync(
            List<string> songIds,
            CancellationToken token)
        {
            if (songIds == null || songIds.Count == 0)
                return new Dictionary<string, Dictionary<string, QualityInfo>>();

            var result = new Dictionary<string, Dictionary<string, QualityInfo>>();

            try
            {
                // 使用现有的 CheckSongsAvailabilityAsync 方法
                // 该方法会检查每首歌的每个音质是否可用
                var qualityOrder = new[] { "standard", "exhigh", "lossless", "hires", "jymaster", "sky" };

                foreach (var quality in qualityOrder)
                {
                    try
                    {
                        var qualityLevel = MapStringToQualityLevel(quality);

                        // 调用 API 检查可用性（skipAvailabilityCheck = false）
                        var availabilityResult = await _apiClient.CheckSongsAvailabilityAsync(
                            songIds.ToArray(),
                            qualityLevel,
                            token);

                        // 处理结果
                        foreach (var songId in songIds)
                        {
                            if (!result.ContainsKey(songId))
                            {
                                result[songId] = new Dictionary<string, QualityInfo>();
                            }

                            // 如果该歌曲该音质不在缺失列表中，说明可用
                            bool isAvailable = !availabilityResult.Contains(songId);

                            result[songId][quality] = new QualityInfo
                            {
                                Level = quality,
                                IsAvailable = isAvailable,
                                RequiredVipLevel = GetRequiredVipLevel(quality),
                                Bitrate = GetBitrate(quality)
                            };
                        }
                    }
                    catch (Exception ex)
                    {
                        Utils.DebugLogger.LogException("ResourceValidator", ex,
                            $"Failed to check quality: {quality}");

                        // 如果检查失败，标记为不可用
                        foreach (var songId in songIds)
                        {
                            if (!result.ContainsKey(songId))
                            {
                                result[songId] = new Dictionary<string, QualityInfo>();
                            }

                            result[songId][quality] = new QualityInfo
                            {
                                Level = quality,
                                IsAvailable = false,
                                RequiredVipLevel = GetRequiredVipLevel(quality),
                                Bitrate = GetBitrate(quality)
                            };
                        }
                    }
                }

                Utils.DebugLogger.Log(Utils.LogLevel.Info, "ResourceValidator",
                    $"Validated {songIds.Count} songs");

                return result;
            }
            catch (Exception ex)
            {
                Utils.DebugLogger.LogException("ResourceValidator", ex, "Batch validation failed");
                return result;
            }
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

        private int GetRequiredVipLevel(string quality)
        {
            switch (quality?.ToLower())
            {
                case "standard":
                case "higher":
                case "exhigh":
                    return 0; // 普通用户

                case "lossless":
                case "hires":
                    return 1; // VIP

                case "jymaster":
                case "sky":
                    return 11; // SVIP

                default:
                    return 0;
            }
        }

        private int GetBitrate(string quality)
        {
            switch (quality?.ToLower())
            {
                case "standard": return 128;
                case "higher": return 192;
                case "exhigh": return 320;
                case "lossless": return 1411;
                case "hires": return 2822;
                case "jymaster": return 3000; // 估算值
                case "sky": return 3500; // 估算值
                default: return 128;
            }
        }
    }
}
