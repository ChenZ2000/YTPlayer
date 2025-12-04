using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnblockNCM.Core.Crypto;
using UnblockNCM.Core.Logging;
using UnblockNCM.Core.Models;
using UnblockNCM.Core.Net;
using UnblockNCM.Core.Utils;

namespace UnblockNCM.Core.Providers
{
    public class KugouProvider : IProvider
    {
        private readonly HttpHelper _http;
        private readonly CacheStorage _cache;
        private readonly bool _noCache;

        private class KgSong
        {
            public string Hash { get; set; }
            public string HqHash { get; set; }
            public string SqHash { get; set; }
            public string Name { get; set; }
            public long Duration { get; set; }
            public string AlbumId { get; set; }
        }

        public KugouProvider(HttpHelper http, CacheStorage cache, bool noCache)
        {
            _http = http;
            _cache = cache;
            _noCache = noCache;
        }

        private KgSong Format(JObject song)
        {
            return new KgSong
            {
                Hash = song.Value<string>("hash"),
                HqHash = song.Value<string>("320hash"),
                SqHash = song.Value<string>("sqhash"),
                Name = song.Value<string>("songname"),
                Duration = (long)(song.Value<double?>("duration") ?? 0) * 1000,
                AlbumId = song.Value<string>("album_id"),
            };
        }

        private async Task<KgSong> SearchAsync(SongInfo info, CancellationToken ct)
        {
            var url = $"http://mobilecdn.kugou.com/api/v3/search/song?keyword={Uri.EscapeDataString(info.Keyword)}&page=1&pagesize=10";
            var resp = await _http.GetAsync(url, null, ct);
            if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"kugou search {resp.StatusCode}");
            var json = JObject.Parse(await HttpHelper.ReadStringAsync(resp));
            var list = json["data"]?["info"]?.Select(x => Format((JObject)x)).ToList() ?? new List<KgSong>();
            var picked = SelectHelper.PickByDuration(list, info, s => s.Duration);
            return picked ?? throw new InvalidOperationException("no kugou match");
        }

        private async Task<string> SingleAsync(KgSong song, string format, CancellationToken ct)
        {
            string hash = format switch
            {
                "sqhash" => song.SqHash,
                "hqhash" => song.HqHash,
                _ => song.Hash
            };
            if (string.IsNullOrEmpty(hash)) return null;
            var key = NeteaseCrypto.Md5Hex($"{hash}kgcloudv2");
            var url = $"http://trackercdn.kugou.com/i/v2/?key={key}&hash={hash}&appid=1005&pid=2&cmd=25&behavior=play&album_id={song.AlbumId}";
            var resp = await _http.GetAsync(url, null, ct);
            var json = JObject.Parse(await HttpHelper.ReadStringAsync(resp));
            return json["url"]?[0]?.ToString();
        }

        private async Task<string> TrackAsync(KgSong song, CancellationToken ct)
        {
            var formats = SelectHelper.EnableFlac ? new[] { "sqhash", "hqhash", "hash" } : new[] { "hqhash", "hash" };
            foreach (var f in formats)
            {
                try
                {
                    var url = await SingleAsync(song, f, ct);
                    if (!string.IsNullOrEmpty(url)) return url;
                }
                catch { }
            }
            throw new InvalidOperationException("kugou track failed");
        }

        public async Task<AudioResult> CheckAsync(SongInfo info, CancellationToken ct)
        {
            var song = await _cache.GetOrAddAsync($"kugou:{info.Keyword}", () => SearchAsync(info, ct), null, _noCache);
            var url = await TrackAsync(song, ct);
            return new AudioResult
            {
                Url = url,
                BitRate = null,
                Md5 = null,
                Size = 0,
                Source = "kugou",
                DurationMs = song.Duration,
                Title = song.Name,
                Artists = info.Artists != null ? string.Join(" / ", info.Artists) : info.Keyword,
                Headers = new System.Collections.Generic.Dictionary<string, string>
                {
                    { "User-Agent", "Mozilla/5.0 (Linux; Android 11; KUGOU_MUSIC)" },
                    { "Referer", "https://www.kugou.com" }
                }
            };
        }
    }
}
