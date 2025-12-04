using System;
using System.Collections.Generic;
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
        public sealed class UnblockMatchResult
        {
            public string Url { get; set; } = string.Empty;
            public long Size { get; set; }
            public string Source { get; set; } = string.Empty;
            public Dictionary<string, string>? Headers { get; set; }
            public int? BitRate { get; set; }
            public long? DurationMs { get; set; }
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
                    MatchOrder = new List<string>() // 使用默认顺序并行
                };

                _http = new HttpHelper();
                _cache = new CacheStorage();
                _find = new FindService(_http, _cache, searchAlbum: false, _options.NoCache);

                var providers = new Dictionary<string, IProvider>(StringComparer.OrdinalIgnoreCase)
                {
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
                if (targetMs > 0 && audio.DurationMs.HasValue)
                {
                    long diff = Math.Abs(audio.DurationMs.Value - targetMs);
                    if (diff > 1000)
                    {
                        return null;
                    }
                }

                return new UnblockMatchResult
                {
                    Url = audio.Url,
                    Size = audio.Size,
                    Source = audio.Source ?? string.Empty,
                    Headers = audio.Headers,
                    BitRate = audio.BitRate,
                    DurationMs = audio.DurationMs
                };
            }
            catch
            {
                // 仅兜底使用，失败时静默，让上层走原有流程。
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
    }
}
