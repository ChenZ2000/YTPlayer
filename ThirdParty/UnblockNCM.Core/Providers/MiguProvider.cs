using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnblockNCM.Core.Logging;
using UnblockNCM.Core.Models;
using UnblockNCM.Core.Net;
using UnblockNCM.Core.Utils;

namespace UnblockNCM.Core.Providers
{
    public class MiguProvider : IProvider
    {
        private readonly HttpHelper _http;
        private readonly CacheStorage _cache;
        private readonly bool _noCache;
        private readonly Dictionary<string, string> _headers;

        public MiguProvider(HttpHelper http, CacheStorage cache, string cookie, bool noCache)
        {
            _http = http;
            _cache = cache;
            _noCache = noCache;
            _headers = new Dictionary<string, string>
            {
                ["origin"] = "http://music.migu.cn/",
                ["referer"] = "http://m.music.migu.cn/v3/",
                ["aversionid"] = cookie ?? string.Empty,
                ["channel"] = "0146921",
            };
        }

        private class MgSong
        {
            public string Id { get; set; }
            public long Duration { get; set; }
        }

        private MgSong Format(JObject song)
        {
            var singerId = song.Value<string>("singerId")?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
            var singerName = song.Value<string>("singerName")?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
            return new MgSong
            {
                Id = song.Value<string>("id"),
                Duration = (song.Value<long?>("duration") ?? 0)
            };
        }

        private async Task<MgSong> SearchAsync(SongInfo info, CancellationToken ct)
        {
            var url = $"https://m.music.migu.cn/migu/remoting/scr_search_tag?keyword={Uri.EscapeDataString(info.Keyword)}&type=2&rows=20&pgc=1";
            var resp = await _http.GetAsync(url, _headers, ct);
            var json = JObject.Parse(await HttpHelper.ReadStringAsync(resp));
            var list = (json["musics"] ?? json["musics"])?.Select(x => Format((JObject)x)).ToList() ?? new List<MgSong>();
            var picked = SelectHelper.PickByDuration(list, info, s => s.Duration);
            return picked ?? throw new InvalidOperationException("migu search empty");
        }

        private async Task<string> SingleAsync(string id, string tone, CancellationToken ct)
        {
            var url = $"https://app.c.nf.migu.cn/MIGUM2.0/strategy/listen-url/v2.4?netType=01&resourceType=2&songId={id}&toneFlag={tone}";
            var resp = await _http.GetAsync(url, _headers, ct);
            var json = JObject.Parse(await HttpHelper.ReadStringAsync(resp));
            var fmt = json["data"]?["audioFormatType"]?.ToString();
            if (fmt != tone) throw new InvalidOperationException("tone mismatch");
            return json["data"]?["url"]?.ToString();
        }

        private async Task<string> TrackAsync(string id, CancellationToken ct)
        {
            var tones = new[] { "ZQ24", "SQ", "HQ", "PQ" };
            var start = SelectHelper.EnableFlac ? 0 : 2;
            for (int i = start; i < tones.Length; i++)
            {
                try
                {
                    var url = await SingleAsync(id, tones[i], ct);
                    if (!string.IsNullOrEmpty(url)) return url;
                }
                catch { }
            }
            throw new InvalidOperationException("migu track failed");
        }

        public async Task<AudioResult> CheckAsync(SongInfo info, CancellationToken ct)
        {
            var song = await _cache.GetOrAddAsync($"migu:{info.Keyword}", () => SearchAsync(info, ct), null, _noCache);
            var url = await TrackAsync(song.Id, ct);
            return new AudioResult
            {
                Url = url,
                Source = "migu",
                DurationMs = song.Duration,
                Title = info.Name,
                Artists = string.Join(" / ", info.Artists.Select(a => a.Name)),
                Headers = new Dictionary<string, string>
                {
                    { "referer", "http://m.music.migu.cn/v3/" },
                    { "origin", "http://music.migu.cn/" },
                    { "channel", "0146921" },
                    { "User-Agent", "Mozilla/5.0 (Linux; Android 11; MiguMusic)" }
                }
            };
        }
    }
}
