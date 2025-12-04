using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnblockNCM.Core.Crypto;
using UnblockNCM.Core.Models;
using UnblockNCM.Core.Net;
using UnblockNCM.Core.Utils;

namespace UnblockNCM.Core.Providers
{
    public class BilivideoProvider : IProvider
    {
        private readonly HttpHelper _http;
        private readonly CacheStorage _cache;
        private readonly bool _noCache;
        private readonly int[] mixinKeyEncTab = {
            46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35, 27, 43, 5, 49,
            33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13, 37, 48, 7, 16, 24, 55, 40,
            61, 26, 17, 0, 1, 60, 51, 30, 4, 22, 25, 54, 21, 56, 59, 6, 63, 57, 62, 11,
            36, 20, 34, 44, 52
        };

        public BilivideoProvider(HttpHelper http, CacheStorage cache, bool noCache)
        {
            _http = http;
            _cache = cache;
            _noCache = noCache;
        }

        private class BiliItem
        {
            public string Bvid { get; set; }
            public long Duration { get; set; }
        }

        private string GetMixinKey(string orig)
        {
            return new string(mixinKeyEncTab.Select(i => orig[i]).ToArray()).Substring(0, 32);
        }

        private async Task<(string imgKey, string subKey)> GetWbiKeys()
        {
            return await _cache.GetOrAddAsync("bili:wbi", async () =>
            {
                var resp = await _http.GetAsync("https://api.bilibili.com/x/web-interface/nav", new Dictionary<string, string>
                {
                    { "User-Agent", "Mozilla/5.0" },
                    { "Referer", "https://www.bilibili.com/" }
                }, CancellationToken.None);
                var json = JObject.Parse(await HttpHelper.ReadStringAsync(resp));
                var img = json["data"]?["wbi_img"];
                var imgUrl = img?["img_url"]?.ToString();
                var subUrl = img?["sub_url"]?.ToString();
                string imgKey = imgUrl.Substring(imgUrl.LastIndexOf('/') + 1, imgUrl.LastIndexOf('.') - imgUrl.LastIndexOf('/') - 1);
                string subKey = subUrl.Substring(subUrl.LastIndexOf('/') + 1, subUrl.LastIndexOf('.') - subUrl.LastIndexOf('/') - 1);
                return (imgKey, subKey);
            }, TimeSpan.FromHours(6), _noCache);
        }

        private async Task<string> SignParamAsync(Dictionary<string, string> param)
        {
            var keys = await GetWbiKeys();
            var mixin = GetMixinKey(keys.imgKey + keys.subKey);
            param["wts"] = ((int)DateTimeOffset.Now.ToUnixTimeSeconds()).ToString();
            var chr_filter = new System.Text.RegularExpressions.Regex("[!'()*]");
            var query = string.Join("&", param.OrderBy(k => k.Key).Select(k => $"{Uri.EscapeDataString(k.Key)}={Uri.EscapeDataString(chr_filter.Replace(k.Value, ""))}"));
            var w_rid = NeteaseCrypto.Md5Hex(query + mixin);
            return query + "&w_rid=" + w_rid;
        }

        private BiliItem FormatSearchResult(JObject item)
        {
            return new BiliItem
            {
                Bvid = item.Value<string>("bvid"),
                Duration = 0
            };
        }

        private async Task<BiliItem> SearchAsync(SongInfo info, CancellationToken ct)
        {
            var param = await SignParamAsync(new Dictionary<string, string>
            {
                { "search_type", "video" },
                { "keyword", info.Keyword }
            });
            var url = "https://api.bilibili.com/x/web-interface/wbi/search/type?" + param;
            var resp = await _http.GetAsync(url, null, ct);
            var json = JObject.Parse(await HttpHelper.ReadStringAsync(resp));
            var list = json["data"]?["result"]?.Select(x => FormatSearchResult((JObject)x)).ToList();
            if (list == null || list.Count == 0) throw new InvalidOperationException("bilivideo empty");
            return list.First();
        }

        private async Task<string> TrackAsync(string bvid, CancellationToken ct)
        {
            var viewParam = await SignParamAsync(new Dictionary<string, string>
            {
                { "bvid", bvid }
            });
            var viewResp = await _http.GetAsync("https://api.bilibili.com/x/web-interface/wbi/view?" + viewParam, null, ct);
            var viewJson = JObject.Parse(await HttpHelper.ReadStringAsync(viewResp));
            if (viewJson.Value<int>("code") != 0) throw new InvalidOperationException("bilivideo view fail");
            var cid = viewJson["data"]?["cid"]?.ToString();
            var playParam = await SignParamAsync(new Dictionary<string, string>
            {
                { "bvid", bvid },
                { "cid", cid },
                { "fnval", "16" },
                { "platform", "pc" }
            });
            var playResp = await _http.GetAsync("https://api.bilibili.com/x/player/wbi/playurl?" + playParam, null, ct);
            var playJson = JObject.Parse(await HttpHelper.ReadStringAsync(playResp));
            if (playJson.Value<int>("code") != 0) throw new InvalidOperationException("bilivideo play fail");
            var audio = playJson["data"]?["dash"]?["audio"]?.FirstOrDefault()?["base_url"]?.ToString();
            if (string.IsNullOrEmpty(audio)) throw new InvalidOperationException("bilivideo audio empty");
            return audio;
        }

        public async Task<AudioResult> CheckAsync(SongInfo info, CancellationToken ct)
        {
            var item = await _cache.GetOrAddAsync($"bilivideo:{info.Keyword}", () => SearchAsync(info, ct), null, _noCache);
            var url = await TrackAsync(item.Bvid, ct);
            return new AudioResult
            {
                Url = url,
                Source = "bilivideo",
                DurationMs = item.Duration,
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
