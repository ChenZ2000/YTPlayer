using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnblockNCM.Core.Models;
using UnblockNCM.Core.Net;
using UnblockNCM.Core.Utils;
using System.Linq;

namespace UnblockNCM.Core.Providers
{
    public class PyncmdProvider : IProvider
    {
        private readonly HttpHelper _http;
        private readonly CacheStorage _cache;
        private readonly bool _noCache;

        public PyncmdProvider(HttpHelper http, CacheStorage cache, bool noCache)
        {
            _http = http;
            _cache = cache;
            _noCache = noCache;
        }

        private async Task<string> TrackAsync(SongInfo info, CancellationToken ct)
        {
            var url = $"https://music-api.gdstudio.xyz/api.php?types=url&source=netease&id={info.Id}&br={(SelectHelper.EnableFlac ? "999" : "320")}";
            var resp = await _http.GetAsync(url, null, ct);
            var json = JObject.Parse(await HttpHelper.ReadStringAsync(resp));
            var link = json["url"]?.ToString();
            var br = json.Value<int?>("br") ?? 0;
            if (string.IsNullOrEmpty(link) || br <= 0) throw new InvalidOperationException("pyncmd no url");
            return link;
        }

        public async Task<AudioResult> CheckAsync(SongInfo info, CancellationToken ct)
        {
            var url = await _cache.GetOrAddAsync($"pyncmd:{info.Id}", () => TrackAsync(info, ct), TimeSpan.FromMinutes(10), _noCache);
            return new AudioResult
            {
                Url = url,
                Source = "pyncmd",
                DurationMs = info.Duration,
                Title = info.Name,
                Artists = string.Join(" / ", info.Artists.Select(a => a.Name)),
                Headers = new System.Collections.Generic.Dictionary<string, string>
                {
                    { "Referer", "https://music.163.com" },
                    { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)" }
                }
            };
        }
    }
}
