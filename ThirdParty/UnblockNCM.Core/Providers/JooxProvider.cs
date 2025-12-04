using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnblockNCM.Core.Crypto;
using UnblockNCM.Core.Models;
using UnblockNCM.Core.Net;
using UnblockNCM.Core.Utils;

namespace UnblockNCM.Core.Providers
{
    public class JooxProvider : IProvider
    {
        private readonly HttpHelper _http;
        private readonly CacheStorage _cache;
        private readonly bool _noCache;
        private readonly System.Collections.Generic.Dictionary<string, string> _headers;

        public JooxProvider(HttpHelper http, CacheStorage cache, bool noCache)
        {
            _http = http;
            _cache = cache;
            _noCache = noCache;
            _headers = new System.Collections.Generic.Dictionary<string, string>
            {
                { "origin", "http://www.joox.com" },
                { "referer", "http://www.joox.com" },
                { "cookie", Environment.GetEnvironmentVariable("JOOX_COOKIE") ?? string.Empty }
            };
        }

        private string FitKeyword(SongInfo info)
        {
            return info.Name.Any(c => c >= 0x0800 && c <= 0x4e00) ? info.Name : info.Keyword;
        }

        private class JooxSong
        {
            public string Id { get; set; }
            public long Duration { get; set; }
        }

        private JooxSong Format(JObject song) => new JooxSong
        {
            Id = song.Value<string>("songid"),
            Duration = (song.Value<long?>("playtime") ?? 0) * 1000
        };

        private async Task<JooxSong> SearchAsync(SongInfo info, CancellationToken ct)
        {
            var keyword = FitKeyword(info);
            var url =
                "http://api-jooxtt.sanook.com/web-fcgi-bin/web_search?country=hk&lang=zh_TW&search_input=" +
                Uri.EscapeDataString(keyword) + "&sin=0&ein=30";
            var resp = await _http.GetAsync(url, _headers, ct);
            var body = await HttpHelper.ReadStringAsync(resp);
            var json = JObject.Parse(body.Replace("'", "\""));
            var list = json["itemlist"]?.Select(x => Format((JObject)x)).ToList();
            if (list == null || list.Count == 0) throw new InvalidOperationException("joox empty");
            var pick = SelectHelper.PickByDuration(list, info, s => s.Duration);
            return pick ?? throw new InvalidOperationException("joox no match");
        }

        private async Task<string> TrackAsync(string id, CancellationToken ct)
        {
            var url =
                $"http://api.joox.com/web-fcgi-bin/web_get_songinfo?songid={id}&country=hk&lang=zh_cn&from_type=-1&channel_id=-1&_={DateTimeOffset.Now.ToUnixTimeMilliseconds()}";
            var resp = await _http.GetAsync(url, _headers, ct);
            var txt = await HttpHelper.ReadStringAsync(resp).ConfigureAwait(false);
            // Joox returns JSONP-like single quotes
            if (txt.StartsWith("callback(") && txt.EndsWith(")"))
                txt = txt.Substring("callback(".Length, txt.Length - "callback(".Length - 1);
            var json = JObject.Parse(txt.Replace("'", "\""));
            var urlStr = (json["r320Url"] ?? json["r192Url"] ?? json["mp3Url"] ?? json["m4aUrl"])?.ToString();
            if (string.IsNullOrEmpty(urlStr)) throw new InvalidOperationException("joox no url");
            urlStr = System.Text.RegularExpressions.Regex.Replace(urlStr, "M\\d00([\\w]+).mp3", "M800$1.mp3");
            return urlStr;
        }

        public async Task<AudioResult> CheckAsync(SongInfo info, CancellationToken ct)
        {
            var song = await _cache.GetOrAddAsync($"joox:{info.Keyword}", () => SearchAsync(info, ct), null, _noCache);
            var url = await TrackAsync(song.Id, ct);
            return new AudioResult
            {
                Url = url,
                Source = "joox",
                DurationMs = song.Duration,
                Title = info.Name,
                Artists = string.Join(" / ", info.Artists.Select(a => a.Name)),
                Headers = new System.Collections.Generic.Dictionary<string, string>
                {
                    { "User-Agent", "Mozilla/5.0 (iPhone; CPU iPhone OS 13_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148 Joox/7.9" },
                    { "Referer", "https://www.joox.com/" }
                }
            };
        }
    }
}
