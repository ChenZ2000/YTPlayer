using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using UnblockNCM.Core.Models;
using UnblockNCM.Core.Net;
using UnblockNCM.Core.Utils;
using UnblockNCM.Core.Logging;

namespace UnblockNCM.Core.Providers
{
    public class QQProvider : IProvider
    {
        private readonly HttpHelper _http;
        private readonly CacheStorage _cache;
        private readonly bool _noCache;
        private readonly string _guid;
        private readonly string _uin;
        private readonly string _scope = "provider/qq";
        private readonly string _vkeysBase;
        private readonly bool _useVkeys;
        private string _cookie;

        public QQProvider(HttpHelper http, CacheStorage cache, bool noCache)
        {
            _http = http;
            _cache = cache;
            _noCache = noCache;
            _cookie = Environment.GetEnvironmentVariable("NEW_QQ_COOKIE") ??
                      Environment.GetEnvironmentVariable("QQ_COOKIE") ?? string.Empty;
            _guid = Environment.GetEnvironmentVariable("QQ_GUID") ??
                    new Random().Next(10000000, 99999999).ToString();
            var mUin = System.Text.RegularExpressions.Regex.Match(_cookie ?? "", "uin=(\\d+)");
            _uin = mUin.Success ? mUin.Groups[1].Value : "0";
            _vkeysBase = Environment.GetEnvironmentVariable("VKEYS_API_BASE")?.TrimEnd('/') ?? "https://api.vkeys.cn";
            _useVkeys = (Environment.GetEnvironmentVariable("VKEYS_DISABLE") ?? "0") != "1";
        }

        private class QqSong
        {
            public string SongMid { get; set; }
            public string FileMid { get; set; }
            public long Duration { get; set; }
            public string Name { get; set; }
            public string Artists { get; set; }
        }

        private QqSong Format(JObject song)
        {
            var file = song["file"] as JObject;
            var mediaMid = file?.Value<string>("media_mid") ?? song.Value<string>("media_mid");
            var songMid = song.Value<string>("mid") ?? song.Value<string>("songmid");
            return new QqSong
            {
                SongMid = songMid,
                FileMid = string.IsNullOrEmpty(mediaMid) ? songMid : mediaMid,
                Duration = (song.Value<long?>("interval") ?? 0) * 1000,
                Name = song.Value<string>("title") ?? song.Value<string>("name") ?? song.Value<string>("songname"),
                Artists = string.Join(" / ",
                    (song["singer"] as JArray)?.Select(s => s.Value<string>("name")) ??
                    (song["singer"] as JObject)?["name"]?.ToString().Split(',') ??
                    Array.Empty<string>())
            };
        }

        private static string Md5(string input)
        {
            using var md5 = MD5.Create();
            var bytes = Encoding.UTF8.GetBytes(input);
            var hash = md5.ComputeHash(bytes);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        private JObject BuildComm()
        {
            return new JObject
            {
                ["ct"] = 24,
                ["cv"] = 0,
                ["format"] = "json",
                ["uin"] = _uin,
                ["g_tk_new_20200303"] = 5381,
                ["g_tk"] = 5381,
                ["platform"] = "yqq.json",
                ["needNewCode"] = 1
            };
        }

        private async Task<JObject> CallAsync(JObject data, bool withCookie, CancellationToken ct)
        {
            if (data["comm"] == null) data["comm"] = BuildComm();
            var json = data.ToString(Newtonsoft.Json.Formatting.None);
            var sign = Md5("CJBPACrRuNy7" + json + "CJBPACrRuNy7");
            var url = $"https://u.y.qq.com/cgi-bin/musicu.fcg?sign={sign}";

            var headers = new Dictionary<string, string>
            {
                { "origin", "https://y.qq.com" },
                { "referer", "https://y.qq.com/" },
                { "content-type", "application/json" },
                { "user-agent", "Mozilla/5.0 (Linux; Android 11; QQMusic/11.5.0)" }
            };
            if (withCookie && !string.IsNullOrEmpty(_cookie))
                headers["cookie"] = _cookie;

            var resp = await _http.PostAsync(url, headers, json, ct).ConfigureAwait(false);
            var str = await HttpHelper.ReadStringAsync(resp).ConfigureAwait(false);
            return JObject.Parse(str);
        }

        private async Task<QqSong> SearchAsync(SongInfo info, CancellationToken ct)
        {
            var query = $"{info.Name} {string.Join(" ", info.Artists.Select(a => a.Name))}".Trim();
            var payload = new JObject
            {
                ["search"] = new JObject
                {
                    ["module"] = "music.search.SearchCgiService",
                    ["method"] = "DoSearchForQQMusicDesktop",
                    ["param"] = new JObject
                    {
                        ["num_per_page"] = 5,
                        ["page_num"] = 1,
                        ["query"] = string.IsNullOrEmpty(query) ? info.Keyword ?? string.Empty : query,
                        ["search_type"] = 0
                    }
                }
            };

            var json = await CallAsync(payload, withCookie: false, ct).ConfigureAwait(false);
            var list = json["search"]?["data"]?["body"]?["song"]?["list"]?.Select(x => Format((JObject)x)).ToList();
            if (list == null || list.Count == 0)
            {
                Log.Debug(_scope, $"qq search empty resp={json}");
                // legacy fallback API (no sign, older web endpoint)
                var legacy = await SearchLegacyAsync(info, ct).ConfigureAwait(false);
                return legacy;
            }
            var pick = SelectHelper.PickByDuration(list, info, s => s.Duration);
            return pick ?? throw new InvalidOperationException("qq no match");
        }

        private async Task<QqSong> SearchLegacyAsync(SongInfo info, CancellationToken ct)
        {
            var query = $"{info.Name} {string.Join(" ", info.Artists.Select(a => a.Name))}".Trim();
            var keyword = string.IsNullOrEmpty(query) ? info.Keyword : query;
            var url = $"https://c.y.qq.com/soso/fcgi-bin/client_search_cp?p=1&n=10&w={Uri.EscapeDataString(keyword)}&format=json";
            var headers = new Dictionary<string, string>
            {
                { "referer", "https://y.qq.com/" },
                { "user-agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)" }
            };
            var resp = await _http.GetAsync(url, headers, ct).ConfigureAwait(false);
            var text = await HttpHelper.ReadStringAsync(resp).ConfigureAwait(false);
            var json = JObject.Parse(text);
            var list = json["data"]?["song"]?["list"]?.Select(x => Format((JObject)x)).ToList();
            if (list == null || list.Count == 0) throw new InvalidOperationException("qq empty");
            var pick = SelectHelper.PickByDuration(list, info, s => s.Duration);
            return pick ?? throw new InvalidOperationException("qq no match");
        }

        private long ParseIntervalMs(string intervalStr)
        {
            if (string.IsNullOrWhiteSpace(intervalStr)) return 0;
            long totalMs = 0;
            var m = System.Text.RegularExpressions.Regex.Match(intervalStr, @"(?:(\d+)分)?(?:(\d+)秒)?");
            if (m.Success)
            {
                if (long.TryParse(m.Groups[1].Value, out var min)) totalMs += min * 60_000;
                if (long.TryParse(m.Groups[2].Value, out var sec)) totalMs += sec * 1000;
            }
            return totalMs;
        }

        private async Task<AudioResult> TryVkeysAsync(SongInfo info, CancellationToken ct)
        {
            if (!_useVkeys) throw new InvalidOperationException("vkeys disabled");
            var keyword = string.IsNullOrWhiteSpace(info.Keyword) ? $"{info.Name} {string.Join(" ", info.Artists.Select(a => a.Name))}" : info.Keyword;
            var quality = SelectHelper.EnableFlac ? 10 : 8; // SQ/320
            var url = $"{_vkeysBase}/v2/music/tencent?word={Uri.EscapeDataString(keyword)}&num=5&quality={quality}";
            var headers = new Dictionary<string, string>
            {
                { "accept", "application/json" },
                { "user-agent", "UnblockNCM.NET" }
            };
            var resp = await _http.GetAsync(url, headers, ct).ConfigureAwait(false);
            var str = await HttpHelper.ReadStringAsync(resp).ConfigureAwait(false);
            var json = JObject.Parse(str);
            if ((int?)json["code"] != 200) throw new InvalidOperationException("vkeys code != 200");
            var dataToken = json["data"];
            var items = new List<JObject>();
            if (dataToken is JArray arr)
                items.AddRange(arr.OfType<JObject>());
            else if (dataToken is JObject obj)
                items.Add(obj);
            if (items.Count == 0) throw new InvalidOperationException("vkeys empty");

            var list = items.Select(it => new QqSong
            {
                SongMid = it.Value<string>("mid"),
                FileMid = it.Value<string>("mid"),
                Duration = ParseIntervalMs(it.Value<string>("interval")) > 0 ? ParseIntervalMs(it.Value<string>("interval")) : (long?)(it.Value<long?>("interval") * 1000 ?? 0) ?? 0,
                Name = it.Value<string>("song"),
                Artists = it.Value<string>("singer")
            }).ToList();
            var pick = SelectHelper.PickByDuration(list, info, s => s.Duration) ?? list.First();
            // find url: maybe present, otherwise use mid to geturl
            var pickedObj = items.FirstOrDefault(x => x.Value<string>("mid") == pick.SongMid) ?? items.First();
            var playUrl = pickedObj.Value<string>("url");
            if (string.IsNullOrWhiteSpace(playUrl))
            {
                var geturl = $"{_vkeysBase}/v2/music/tencent/geturl?mid={pick.SongMid}&quality={quality}";
                var resp2 = await _http.GetAsync(geturl, headers, ct).ConfigureAwait(false);
                var str2 = await HttpHelper.ReadStringAsync(resp2).ConfigureAwait(false);
                var json2 = JObject.Parse(str2);
                if ((int?)json2["code"] == 200)
                    playUrl = json2["data"]?["url"]?.ToString();
            }
            if (string.IsNullOrWhiteSpace(playUrl))
                throw new InvalidOperationException("vkeys url empty");

            return new AudioResult
            {
                Url = playUrl,
                Source = "qq",
                DurationMs = pick.Duration,
                Title = pick.Name ?? info.Name,
                Artists = string.IsNullOrWhiteSpace(pick.Artists) ? string.Join(" / ", info.Artists.Select(a => a.Name)) : pick.Artists,
                Headers = new Dictionary<string, string>
                {
                    { "Referer", "https://y.qq.com/" },
                    { "Origin", "https://y.qq.com" },
                    { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)" }
                }
            };
        }

        private async Task<string> SingleAsync(QqSong id, string prefix, string ext, bool useCookie, CancellationToken ct)
        {
            var uin = useCookie && !string.IsNullOrEmpty(_uin) ? _uin : "0";
            var filename = string.IsNullOrEmpty(prefix) ? null : $"{prefix}{id.FileMid}{ext}";
            var payload = new JObject
            {
                ["req_0"] = new JObject
                {
                    ["module"] = "vkey.GetVkeyServer",
                    ["method"] = "CgiGetVkey",
                    ["param"] = new JObject
                    {
                        ["guid"] = _guid,
                        ["loginflag"] = useCookie && !string.IsNullOrEmpty(_cookie) ? 1 : 0,
                        ["filename"] = filename == null ? null : new JArray(filename),
                        ["songmid"] = new JArray(id.SongMid),
                        ["songtype"] = new JArray(0),
                        ["uin"] = uin,
                        ["platform"] = "yqq.json",
                        ["h5to"] = "speed",
                        ["reqtype"] = 1
                    }
                }
            };

            var json = await CallAsync(payload, useCookie, ct).ConfigureAwait(false);
            var data = json["req_0"]?["data"];
            var midInfos = data?["midurlinfo"] as JArray;
            if (midInfos == null || midInfos.Count == 0)
            {
                Log.Debug(_scope, $"qq midurl empty resp={json}");
                throw new InvalidOperationException("qq midurl empty");
            }
            var purl = midInfos[0]?["purl"]?.ToString();
            var sip = data?["sip"]?[0]?.ToString();
            if (string.IsNullOrEmpty(purl))
            {
                Log.Debug(_scope, $"qq purl empty resp={json}");
                throw new InvalidOperationException("qq purl empty");
            }
            if (string.IsNullOrEmpty(sip))
                sip = "https://isure.stream.qqmusic.qq.com/";
            var play = sip + purl;

            var headHeaders = new Dictionary<string, string>
            {
                { "range", "bytes=0-8191" },
                { "accept-encoding", "identity" },
                { "referer", "https://y.qq.com/" },
                { "origin", "https://y.qq.com" }
            };
            var probe = await _http.GetAsync(play, headHeaders, ct).ConfigureAwait(false);
            if (!probe.IsSuccessStatusCode)
            {
                Log.Debug(_scope, $"qq play invalid code={(int)probe.StatusCode} url={play}");
                throw new InvalidOperationException("qq play invalid");
            }
            return play;
        }

        private async Task<string> TrackAsync(QqSong id, CancellationToken ct)
        {
            var formats = (_cookie.Length > 0 && SelectHelper.EnableFlac)
                ? new[] { ("F000", ".flac"), ("M800", ".mp3"), ("M500", ".mp3"), ("C400", ".m4a"), ("C200", ".m4a") }
                : new[] { ("M800", ".mp3"), ("M500", ".mp3"), ("C400", ".m4a"), ("C200", ".m4a") };
            foreach (var f in formats)
            {
                try
                {
                    Log.Debug(_scope, $"try guest {f.Item1} songmid={id.SongMid} filemid={id.FileMid}");
                    var url = await SingleAsync(id, f.Item1, f.Item2, useCookie: false, ct).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(url)) return url;
                }
                catch (Exception ex)
                {
                    Log.Debug(_scope, $"guest {f.Item1} failed: {ex.Message}");
                }
                try
                {
                    var url = await SingleAsync(id, f.Item1, f.Item2, useCookie: true, ct).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(url)) return url;
                }
                catch (Exception ex)
                {
                    Log.Debug(_scope, $"cookie {f.Item1} failed: {ex.Message}");
                }
            }
            throw new InvalidOperationException("qq track failed");
        }

        public async Task<AudioResult> CheckAsync(SongInfo info, CancellationToken ct)
        {
            // prefer vkeys (handles VIP) when enabled
            if (_useVkeys)
            {
                try
                {
                    return await TryVkeysAsync(info, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Debug(_scope, $"vkeys failed: {ex.Message}");
                }
            }

            var song = await _cache.GetOrAddAsync($"qq:{info.Keyword}", () => SearchAsync(info, ct), null, _noCache);
            var url = await TrackAsync(song, ct);
            return new AudioResult
            {
                Url = url,
                Source = "qq",
                DurationMs = song.Duration,
                Title = song.Name ?? info.Name,
                Artists = !string.IsNullOrWhiteSpace(song.Artists)
                    ? song.Artists
                    : string.Join(" / ", info.Artists.Select(a => a.Name)),
                Headers = new Dictionary<string, string>
                {
                    { "Referer", "https://y.qq.com/" },
                    { "Origin", "https://y.qq.com" },
                    { "User-Agent", "Mozilla/5.0 (Linux; Android 11; QQMusic/11.5.0)" },
                    { "Cookie", _cookie }
                }
            };
        }
    }
}
