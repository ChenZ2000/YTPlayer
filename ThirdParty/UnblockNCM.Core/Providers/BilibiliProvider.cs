using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnblockNCM.Core.Models;
using UnblockNCM.Core.Net;
using UnblockNCM.Core.Utils;

namespace UnblockNCM.Core.Providers
{
    public class BilibiliProvider : IProvider
    {
        private readonly HttpHelper _http;
        private readonly CacheStorage _cache;
        private readonly bool _noCache;

        public BilibiliProvider(HttpHelper http, CacheStorage cache, bool noCache)
        {
            _http = http;
            _cache = cache;
            _noCache = noCache;
        }

        private class BiliSong
        {
            public string Id { get; set; }
            public long Duration { get; set; }
        }

        private BiliSong Format(JObject song)
        {
            return new BiliSong
            {
                Id = song.Value<string>("id"),
                Duration = 0 // api not giving duration; fallback select first
            };
        }

        private async Task<BiliSong> SearchAsync(SongInfo info, CancellationToken ct)
        {
            var url =
                "https://api.bilibili.com/audio/music-service-c/s?search_type=music&page=1&pagesize=30&keyword=" +
                Uri.EscapeDataString(info.Keyword);
            var headers = new System.Collections.Generic.Dictionary<string, string>
            {
                { "Referer", "https://www.bilibili.com" },
                { "User-Agent", "Mozilla/5.0" }
            };
            var resp = await _http.GetAsync(url, headers, ct);
            var json = JObject.Parse(await HttpHelper.ReadStringAsync(resp));
            var list = json["data"]?["result"]?.Select(x => Format((JObject)x)).ToList();
            if (list == null || list.Count == 0) throw new InvalidOperationException("bilibili empty");
            var pick = list.First();
            return pick;
        }

        private async Task<string> TrackAsync(string id, CancellationToken ct)
        {
            var url =
                $"https://www.bilibili.com/audio/music-service-c/web/url?rivilege=2&quality=2&sid={id}";
            var resp = await _http.GetAsync(url, null, ct);
            var json = JObject.Parse(await HttpHelper.ReadStringAsync(resp));
            if (json.Value<int>("code") == 0)
            {
                var cdn = json["data"]?["cdns"]?.FirstOrDefault()?.ToString();
                if (!string.IsNullOrEmpty(cdn))
                    return cdn.Replace("https", "http"); // mimic node behavior
            }
            throw new InvalidOperationException("bilibili track failed");
        }

        public async Task<AudioResult> CheckAsync(SongInfo info, CancellationToken ct)
        {
            var song = await _cache.GetOrAddAsync($"bilibili:{info.Keyword}", () => SearchAsync(info, ct), null, _noCache);
            var url = await TrackAsync(song.Id, ct);
            return new AudioResult
            {
                Url = url,
                Source = "bilibili",
                DurationMs = song.Duration,
                Title = info.Name,
                Artists = string.Join(" / ", info.Artists.Select(a => a.Name)),
                Headers = new System.Collections.Generic.Dictionary<string, string>
                {
                    { "Referer", "https://www.bilibili.com" },
                    { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36" }
                }
            };
        }
    }
}
