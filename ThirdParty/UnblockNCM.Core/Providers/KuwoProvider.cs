using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnblockNCM.Core.Logging;
using UnblockNCM.Core.Models;
using UnblockNCM.Core.Net;
using UnblockNCM.Core.Utils;

namespace UnblockNCM.Core.Providers
{
    /// <summary>
    /// Kuwo provider using mobile API (convert_url2/3) with promo-audio filtering and fallbacks.
    /// </summary>
    public class KuwoProvider : IProvider
    {
        private readonly HttpHelper _http;
        private readonly CacheStorage _cache;
        private readonly bool _noCache;
        private readonly string _token;
        private readonly string _mobileUa;
        private readonly string _qimei;
        private readonly string _deviceId;
        private readonly string _scope = "provider/kuwo";

        public KuwoProvider(HttpHelper http, CacheStorage cache, bool noCache)
        {
            _http = http;
            _cache = cache;
            _noCache = noCache;
            _token = Environment.GetEnvironmentVariable("KUWO_TOKEN") ?? "tiqqmusic";
            _mobileUa = Environment.GetEnvironmentVariable("KUWO_MOBILE_UA") ??
                        "okhttp/3.10.0 (Linux;Android 11) Mobile KuwoMusic";
            _qimei = Environment.GetEnvironmentVariable("KUWO_QIMEI") ?? string.Empty;
            _deviceId = Environment.GetEnvironmentVariable("KUWO_DEVICEID") ?? string.Empty;
        }

        private class KwSong
        {
            public string Rid { get; set; }
            public long Duration { get; set; }
        }

        private KwSong Format(JObject song)
        {
            long duration = song.Value<long?>("duration") ??
                            song.Value<long?>("DURATION") ??
                            0;
            if (duration == 0)
            {
                var stm = song.Value<string>("songTimeMinutes");
                if (!string.IsNullOrWhiteSpace(stm) && TimeSpan.TryParse("0:" + stm, out var ts))
                    duration = (long)ts.TotalSeconds;
            }
            return new KwSong
            {
                Rid = song.Value<string>("rid") ?? song.Value<string>("MUSICRID")?.Split('_').Last(),
                Duration = duration * 1000
            };
        }

        private async Task<KwSong> SearchAsync(SongInfo info, CancellationToken ct)
        {
            var url =
                $"http://search.kuwo.cn/r.s?all={Uri.EscapeDataString(info.Keyword)}&ft=music&itemset=web_2013&client=kt&pn=0&rn=10&rformat=json&encoding=utf8";
            var resp = await _http.GetAsync(url, null, ct).ConfigureAwait(false);
            var json = JObject.Parse(await HttpHelper.ReadStringAsync(resp).ConfigureAwait(false));
            var list = json["abslist"]?.Select(x => Format((JObject)x)).ToList();
            if (list == null || list.Count == 0) throw new InvalidOperationException("kuwo empty");
            var picked = SelectHelper.PickByDuration(list, info, s => s.Duration);
            return picked ?? throw new InvalidOperationException("kuwo no match");
        }

        private async Task<string> ConvertMobileAsync(string rid, string type, string fmtList, CancellationToken ct)
        {
            var source = "kwplayer_ar_5.1.0.0_B_jiakong_vh.apk";
            var q =
                $"user=0&corp=kuwo&source={source}&p2p=1&type={type}&sig=0&format={fmtList}&rid={rid}";
            if (!string.IsNullOrEmpty(_deviceId)) q += $"&did={_deviceId}";
            if (!string.IsNullOrEmpty(_qimei)) q += $"&qimei={_qimei}";
            var encoded = KwDes.EncryptQuery(q);
            var url = $"http://mobi.kuwo.cn/mobi.s?f=kuwo&q={Uri.EscapeDataString(encoded)}";
            var headers = new Dictionary<string, string>
            {
                { "User-Agent", _mobileUa },
                { "Accept", "*/*" }
            };
            var resp = await _http.GetAsync(url, headers, ct).ConfigureAwait(false);
            var body = await HttpHelper.ReadStringAsync(resp).ConfigureAwait(false);
            var match = Regex.Match(body, @"http[^\s$\""]+");
            if (!match.Success) throw new InvalidOperationException("kuwo mobi empty");
            return match.Value.Trim();
        }

        private async Task<string> AntiServerAsync(string rid, string format, CancellationToken ct)
        {
            var url =
                $"http://antiserver.kuwo.cn/anti.s?type=convert_url&format={format}&response=url&rid=MUSIC_{rid}&csrf={_token}";
            var headers = new Dictionary<string, string>
            {
                { "Referer", "http://kuwo.cn" },
                { "Cookie", $"kw_token={_token}" },
                { "csrf", _token },
                { "User-Agent", _mobileUa }
            };
            var resp = await _http.GetAsync(url, headers, ct).ConfigureAwait(false);
            var body = await HttpHelper.ReadStringAsync(resp).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(body) && body.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return body.Trim();
            throw new InvalidOperationException("kuwo anti empty");
        }

        private async Task<long?> ProbeLengthAsync(string url, CancellationToken ct)
        {
            try
            {
                var headers = new Dictionary<string, string>
                {
                    { "User-Agent", _mobileUa },
                    { "Range", "bytes=0-0" },
                    { "Accept-Encoding", "identity" },
                    { "Referer", "http://kuwo.cn" }
                };
                var resp = await _http.GetAsync(url, headers, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;
                var len = resp.Content.Headers.ContentRange?.Length ?? resp.Content.Headers.ContentLength;
                return len;
            }
            catch (Exception ex)
            {
                Log.Debug(_scope, $"probe failed: {ex.Message}");
                return null;
            }
        }

        private bool SizeLooksReal(long? length, long durationMs)
        {
            if (!length.HasValue || durationMs <= 0) return true;
            var sec = durationMs / 1000.0;
            var minSize = sec * 4000; // about 32 kbps lower bound
            return length.Value >= minSize;
        }

        private async Task<string> TrackAsync(KwSong song, CancellationToken ct)
        {
            var fmtList = SelectHelper.EnableFlac ? "flac|mp3" : "mp3";
            var antiFormats = SelectHelper.EnableFlac ? new[] { "flac", "320kmp3" } : new[] { "320kmp3", "192kmp3" };

            var attempts = new List<Func<Task<string>>>
            {
                () => ConvertMobileAsync(song.Rid, "convert_url2", fmtList, ct),
                () => ConvertMobileAsync(song.Rid, "convert_url3", fmtList, ct),
                () => AntiServerAsync(song.Rid, antiFormats[0], ct),
                () => AntiServerAsync(song.Rid, antiFormats.Last(), ct)
            };

            foreach (var attempt in attempts)
            {
                try
                {
                    var url = await attempt().ConfigureAwait(false);
                    var len = await ProbeLengthAsync(url, ct).ConfigureAwait(false);
                    if (SizeLooksReal(len, song.Duration))
                        return url;
                    Log.Debug(_scope, $"candidate rejected (size {len ?? -1}) {url}");
                }
                catch (Exception ex)
                {
                    Log.Debug(_scope, $"candidate failed: {ex.Message}");
                }
            }
            throw new InvalidOperationException("kuwo track failed");
        }

        public async Task<AudioResult> CheckAsync(SongInfo info, CancellationToken ct)
        {
            var song = await _cache.GetOrAddAsync($"kuwo:{info.Keyword}", () => SearchAsync(info, ct), null, _noCache);
            var url = await TrackAsync(song, ct);
            return new AudioResult
            {
                Url = url,
                Source = "kuwo",
                DurationMs = song.Duration,
                Title = info.Name,
                Artists = string.Join(" / ", info.Artists.Select(a => a.Name)),
                Headers = new Dictionary<string, string>
                {
                    { "Referer", "http://kuwo.cn" },
                    { "Cookie", $"kw_token={_token}" },
                    { "csrf", _token },
                    { "User-Agent", _mobileUa },
                    { "Accept", "*/*" }
                }
            };
        }
    }
}
