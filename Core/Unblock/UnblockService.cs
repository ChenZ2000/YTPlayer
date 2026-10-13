using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnblockNCM.Core.Config;
using UnblockNCM.Core.Net;
using UnblockNCM.Core.Providers;
using UnblockNCM.Core.Services;
using UnblockNCM.Core.Utils;
using YTPlayer.Models;

namespace YTPlayer.Core.Unblock
{
    /// <summary>
    /// 封装第三方多源匹配逻辑（UnblockNCM.Core），用于播放/下载兜底。
    /// </summary>
    public sealed class UnblockService : IDisposable
    {
        private const int MinAverageBitRate = 56000;
        private const long AbsoluteMinimumAudioBytes = 96 * 1024;

        public sealed class UnblockMatchResult
        {
            public string Url { get; set; } = string.Empty;
            public long Size { get; set; }
            public string Source { get; set; } = string.Empty;
            public Dictionary<string, string>? Headers { get; set; }
            public int? BitRate { get; set; }
            public long? DurationMs { get; set; }
            public bool IsTrial { get; set; }
            public long TrialStart { get; set; }
            public long TrialEnd { get; set; }
        }

        private readonly object _initLock = new object();
        private bool _initialized;

        private UnblockOptions _options = null!;
        private HttpHelper _http = null!;
        private CacheStorage _cache = null!;
        private FindService _find = null!;
        private ProviderManager _manager = null!;

        public void Dispose()
        {
            _http?.Dispose();
        }

        private void EnsureInitialized()
        {
            if (_initialized) return;

            lock (_initLock)
            {
                if (_initialized) return;

                _options = new UnblockOptions
                {
                    EnableFlac = true,
                    MinBr = 0,
                    Endpoint = string.Empty,
                    AllowUnvalidatedFallback = false,
                    MatchOrder = new List<string>
                    {
                        "byfuns",
                        "gdmusic",
                        "baka",
                        "bikonoo",
                        "msls",
                        "qijieya",
                        "pyncmd",
                        "bodian",
                        "qq",
                        "kugou",
                        "kuwo",
                        "migu",
                        "joox",
                        "bilibili",
                        "bilivideo"
                    }
                };

                _http = new HttpHelper();
                _cache = new CacheStorage();
                _find = new FindService(_http, _cache, searchAlbum: false, _options.NoCache);

                var providers = new Dictionary<string, IProvider>(StringComparer.OrdinalIgnoreCase)
                {
                    { "byfuns",   new ByfunsProvider(_cache, _options.NoCache) },
                    { "gdmusic",  new GdMusicProvider(_cache, _options.NoCache) },
                    { "baka",     new BakaProvider(_cache, _options.NoCache) },
                    { "bikonoo",  new BikonooProvider(_cache, _options.NoCache) },
                    { "msls",     new MslsProvider(_cache, _options.NoCache) },
                    { "qijieya",  new QijieyaProvider(_cache, _options.NoCache) },
                    { "kugou",    new KugouProvider(_http, _cache, _options.NoCache) },
                    { "kuwo",     new KuwoProvider(_http, _cache, _options.NoCache) },
                    { "qq",       new QQProvider(_http, _cache, _options.NoCache) },
                    { "bodian",   new BodianProvider(_http, _cache, _options.NoCache) },
                    { "migu",     new MiguProvider(_http, _cache, Environment.GetEnvironmentVariable("MIGU_COOKIE"), _options.NoCache) },
                    { "joox",     new JooxProvider(_http, _cache, _options.NoCache) },
                    { "bilibili", new BilibiliProvider(_http, _cache, _options.NoCache) },
                    { "bilivideo",new BilivideoProvider(_http, _cache, _options.NoCache) },
                    { "pyncmd",   new PyncmdProvider(_http, _cache, _options.NoCache) }
                };

                _manager = new ProviderManager(providers, _find, _options);
                _initialized = true;
            }
        }

        public async Task<UnblockMatchResult?> TryMatchAsync(SongInfo song, CancellationToken ct)
        {
            if (song == null || string.IsNullOrWhiteSpace(song.Id))
            {
                return null;
            }

            EnsureInitialized();

            JObject rawData = BuildRawSongData(song);

            try
            {
                var audio = await _manager.MatchAsync(song.Id, rawData, ct).ConfigureAwait(false);
                if (audio == null || string.IsNullOrWhiteSpace(audio.Url))
                {
                    return null;
                }

                // 严格时长匹配：误差不超过 ±1s，否则视为无效解封结果
                long targetMs = song.Duration > 0 ? song.Duration * 1000L : 0;
                bool isTrial = audio.IsTrial;
                if (targetMs > 0 && audio.DurationMs.HasValue && audio.DurationMs.Value > 0)
                {
                    long diff = Math.Abs(audio.DurationMs.Value - targetMs);
                    if (diff > 1000)
                    {
                        if (audio.DurationMs.Value < targetMs)
                        {
                            isTrial = true;
                        }
                        else
                        {
                            return null;
                        }
                    }
                }

                if (targetMs > 0)
                {
                    bool sizeLooksFull = audio.Size > 0 && SizeLooksFullLength(audio.Size, targetMs, audio.BitRate);
                    if (!sizeLooksFull)
                    {
                        isTrial = true;
                    }
                }

                long trialStart = isTrial ? Math.Max(0L, audio.TrialStartMs) : 0L;
                long trialEnd = isTrial ? audio.TrialEndMs : 0L;
                if (isTrial && trialEnd <= trialStart)
                {
                    trialEnd = EstimateTrialEndMs(audio.Size, audio.BitRate, targetMs);
                }

                return new UnblockMatchResult
                {
                    Url = audio.Url,
                    Size = audio.Size,
                    Source = audio.Source ?? string.Empty,
                    Headers = audio.Headers,
                    BitRate = audio.BitRate,
                    DurationMs = audio.DurationMs,
                    IsTrial = isTrial,
                    TrialStart = trialStart,
                    TrialEnd = trialEnd
                };
            }
            catch (Exception ex)
            {
                // 仅兜底使用，失败时静默，让上层走原有流程。
                Debug.WriteLine("[UnblockService] 匹配失败: " + song.Id + " - " + song.Name + "，原因: " + ex.Message);
                return null;
            }
        }

        private static JObject BuildRawSongData(SongInfo song)
        {
            var artistObjects = new JArray();
            var names = (song.ArtistNames != null && song.ArtistNames.Count > 0)
                ? song.ArtistNames
                : (song.Artist ?? string.Empty).Split(new[] { '/', '&' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();

            for (int i = 0; i < names.Count; i++)
            {
                var artist = new JObject
                {
                    ["id"] = (song.ArtistIds != null && song.ArtistIds.Count > i) ? song.ArtistIds[i].ToString() : null,
                    ["name"] = names[i]
                };
                artistObjects.Add(artist);
            }

            var albumObj = new JObject
            {
                ["id"] = song.AlbumId,
                ["name"] = song.Album
            };

            var obj = new JObject
            {
                ["id"] = song.Id,
                ["name"] = song.Name,
                ["duration"] = song.Duration > 0 ? song.Duration * 1000 : 0,
                ["alias"] = new JArray(),
                ["album"] = albumObj,
                ["artists"] = artistObjects
            };

            return obj;
        }

        private static bool SizeLooksFullLength(long totalLength, long targetDurationMs, int? bitRate)
        {
            if (totalLength < AbsoluteMinimumAudioBytes || targetDurationMs <= 0)
            {
                return targetDurationMs <= 0 && totalLength >= AbsoluteMinimumAudioBytes;
            }

            double seconds = targetDurationMs / 1000d;
            double absoluteMinimum = seconds * MinAverageBitRate / 8d;
            if (totalLength < absoluteMinimum)
            {
                return false;
            }

            int normalizedBitRate = NormalizeBitRate(bitRate);
            if (normalizedBitRate > 0)
            {
                double expected = seconds * normalizedBitRate / 8d;
                double required = expected * GetBitRateSizeRatio(normalizedBitRate);
                if (totalLength < required)
                {
                    return false;
                }
            }

            return true;
        }

        private static int NormalizeBitRate(int? bitRate)
        {
            if (!bitRate.HasValue || bitRate.Value <= 0)
            {
                return 0;
            }

            int value = bitRate.Value;
            if (value < 1000)
            {
                value *= 1000;
            }

            return value;
        }

        private static double GetBitRateSizeRatio(int bitRate)
        {
            if (bitRate >= 900000)
            {
                return 0.30d;
            }

            if (bitRate >= 96000)
            {
                return 0.55d;
            }

            return 0.45d;
        }

        private static long EstimateTrialEndMs(long size, int? bitRate, long targetDurationMs)
        {
            int normalizedBitRate = NormalizeBitRate(bitRate);
            long estimated = 0;
            if (size > 0 && normalizedBitRate > 0)
            {
                estimated = (long)Math.Round(size * 8d * 1000d / normalizedBitRate);
            }

            if (estimated <= 0)
            {
                estimated = 30000;
            }

            if (targetDurationMs > 0)
            {
                estimated = Math.Min(estimated, targetDurationMs);
            }

            return estimated > 0 ? estimated : 30000;
        }

    }
}
