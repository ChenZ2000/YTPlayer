using System;
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
    /// <summary>
    /// bodian = Kuwo AR client API.
    /// </summary>
    public class BodianProvider : IProvider
    {
        private readonly HttpHelper _http;
        private readonly CacheStorage _cache;
        private readonly string _scope = "provider/bodian";
        private readonly string _deviceId;
        private readonly bool _noCache;

        public BodianProvider(HttpHelper http, CacheStorage cache, bool noCache)
        {
            _http = http;
            _cache = cache;
            _noCache = noCache;
            _deviceId = new Random().Next(0, 1000000000).ToString();
        }

        private string GenerateSign(string url)
        {
            var u = new Uri(url);
            var str = url + $"&timestamp={DateTimeOffset.Now.ToUnixTimeMilliseconds()}";
            var filtered = new string(str.Substring(str.IndexOf('?') + 1)
                .Where(char.IsLetterOrDigit)
                .OrderBy(c => c)
                .ToArray());
            var data = $"kuwotest{filtered}{u.AbsolutePath}";
            var md5 = NeteaseCrypto.Md5Hex(data);
            return $"{str}&sign={md5}";
        }

        private async Task<string> SearchAsync(SongInfo info, CancellationToken ct)
        {
            var keyword = Uri.EscapeDataString(info.Keyword.Replace(" - ", " "));
            var searchUrl = $"http://search.kuwo.cn/r.s?&correct=1&vipver=1&stype=comprehensive&encoding=utf8&rformat=json&mobi=1&show_copyright_off=1&searchapi=6&all={keyword}";
            var resp = await _http.GetAsync(searchUrl, null, ct);
            var json = JObject.Parse(await HttpHelper.ReadStringAsync(resp));
            var list = json["content"]?[1]?["musicpage"]?["abslist"]?.Select(item => new
            {
                Id = item["MUSICRID"]?.ToString().Split('_').LastOrDefault(),
                Name = item.Value<string>("SONGNAME"),
                Duration = (long)(item.Value<double?>("DURATION") ?? 0) * 1000
            }).ToList();
            if (list == null || list.Count == 0) throw new InvalidOperationException("bodian search empty");
            var picked = SelectHelper.PickByDuration(list, info, x => x.Duration);
            return picked.Id ?? throw new InvalidOperationException("bodian no id");
        }

        private async Task SendAdFreeAsync(CancellationToken ct)
        {
            var adUrl = "http://bd-api.kuwo.cn/api/service/advert/watch?uid=-1&token=&timestamp=1724306124436&sign=15a676d66285117ad714e8c8371691da";
            var headers = KuwoHeaders();
            headers["content-type"] = "application/json; charset=utf-8";
            var data = "{\"type\":5,\"subType\":5,\"musicId\":0,\"adToken\":\"\"}";
            var resp = await _http.PostAsync(adUrl, headers, data, ct);
            Log.Debug(_scope, $"ad free status {resp.StatusCode}");
        }

        private System.Collections.Generic.Dictionary<string, string> KuwoHeaders()
        {
            return new System.Collections.Generic.Dictionary<string, string>
            {
                ["user-agent"] = "Dart/2.19 (dart:io)",
                ["plat"] = "ar",
                ["channel"] = "aliopen",
                ["devid"] = _deviceId,
                ["ver"] = "3.9.0",
                ["host"] = "bd-api.kuwo.cn",
                ["qimei36"] = "1e9970cbcdc20a031dee9f37100017e1840e"
            };
        }

        private async Task<string> TrackAsync(string id, CancellationToken ct)
        {
            await SendAdFreeAsync(ct);
            var headers = KuwoHeaders();
            headers["X-Forwarded-For"] = "1.0.1.114";

            var quality = SelectHelper.EnableFlac ? "2000kflac" : "320kmp3";
            var audioUrl = $"http://bd-api.kuwo.cn/api/play/music/v2/audioUrl?&br={quality}&musicId={id}";
            audioUrl = GenerateSign(audioUrl);

            var resp = await _http.GetAsync(audioUrl, headers, ct);
            if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"bodian track {resp.StatusCode}");
            var json = JObject.Parse(await HttpHelper.ReadStringAsync(resp));
            if (json.Value<int>("code") != 200) throw new InvalidOperationException("bodian code not 200");
            return json["data"]?["audioUrl"]?.ToString();
        }

        public async Task<AudioResult> CheckAsync(SongInfo info, CancellationToken ct)
        {
            var id = await _cache.GetOrAddAsync($"bodian:{info.Keyword}", () => SearchAsync(info, ct), null, _noCache);
            var url = await TrackAsync(id, ct);
            if (string.IsNullOrEmpty(url)) throw new InvalidOperationException("bodian empty url");
            return new AudioResult
            {
                Url = url,
                BitRate = null,
                Md5 = null,
                Size = 0,
                Source = "bodian",
                DurationMs = info.Duration,
                Title = info.Name,
                Artists = string.Join(" / ", info.Artists.Select(a => a.Name)),
                Headers = new System.Collections.Generic.Dictionary<string, string>
                {
                    { "Referer", "http://kuwo.cn" },
                    { "User-Agent", "Mozilla/5.0 (Linux; Android 11; Kuwo/9.5.0)" }
                }
            };
        }
    }
}
