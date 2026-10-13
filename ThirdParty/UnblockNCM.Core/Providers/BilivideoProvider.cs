using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
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
    public class BilivideoProvider : IProvider
    {
        private readonly HttpHelper _http;
        private readonly CacheStorage _cache;
        private readonly bool _noCache;
        private readonly Dictionary<string, string> _headers;
        private readonly string _scope = "provider/bilivideo";
        private const int MaxSearchResults = 50;
        private const int MaxTrackAttempts = 12;
        private const int MinAverageBitRate = 56000;
        private const long PreferredDurationToleranceMs = 1000;
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
            _headers = new Dictionary<string, string>
            {
                { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36" },
                { "Referer", "https://www.bilibili.com/" }
            };
        }

        private class BiliItem
        {
            public string Bvid { get; set; }
            public long Duration { get; set; }
            public string Title { get; set; }
            public string Author { get; set; }
            public int Rank { get; set; }
        }

        private class BiliTrack
        {
            public string Url { get; set; }
            public long DurationMs { get; set; }
            public long Size { get; set; }
            public int? BitRate { get; set; }
        }

        private string GetMixinKey(string orig)
        {
            return new string(mixinKeyEncTab.Select(i => orig[i]).ToArray()).Substring(0, 32);
        }

        private async Task<(string imgKey, string subKey)> GetWbiKeys(CancellationToken ct)
        {
            return await _cache.GetOrAddAsync("bili:wbi", async () =>
            {
                using var resp = await _http.GetAsync("https://api.bilibili.com/x/web-interface/nav", _headers, ct).ConfigureAwait(false);
                var json = JObject.Parse(await HttpHelper.ReadStringAsync(resp));
                var img = json["data"]?["wbi_img"];
                var imgUrl = img?["img_url"]?.ToString();
                var subUrl = img?["sub_url"]?.ToString();
                string imgKey = imgUrl.Substring(imgUrl.LastIndexOf('/') + 1, imgUrl.LastIndexOf('.') - imgUrl.LastIndexOf('/') - 1);
                string subKey = subUrl.Substring(subUrl.LastIndexOf('/') + 1, subUrl.LastIndexOf('.') - subUrl.LastIndexOf('/') - 1);
                return (imgKey, subKey);
            }, TimeSpan.FromHours(6), _noCache);
        }

        private async Task<string> SignParamAsync(Dictionary<string, string> param, CancellationToken ct)
        {
            var keys = await GetWbiKeys(ct).ConfigureAwait(false);
            var mixin = GetMixinKey(keys.imgKey + keys.subKey);
            param["wts"] = ((int)DateTimeOffset.Now.ToUnixTimeSeconds()).ToString();
            var chr_filter = new Regex("[!'()*]");
            var query = string.Join("&", param.OrderBy(k => k.Key).Select(k => $"{Uri.EscapeDataString(k.Key)}={Uri.EscapeDataString(chr_filter.Replace(k.Value, ""))}"));
            var w_rid = NeteaseCrypto.Md5Hex(query + mixin);
            return query + "&w_rid=" + w_rid;
        }

        private BiliItem FormatSearchResult(JObject item, int rank)
        {
            return new BiliItem
            {
                Bvid = item.Value<string>("bvid"),
                Duration = ParseDurationMs(item.Value<string>("duration")),
                Title = CleanTitle(item.Value<string>("title")),
                Author = item.Value<string>("author"),
                Rank = rank
            };
        }

        private async Task<List<BiliItem>> SearchAsync(SongInfo info, CancellationToken ct)
        {
            var param = await SignParamAsync(new Dictionary<string, string>
            {
                { "search_type", "video" },
                { "keyword", info.Keyword },
                { "page", "1" },
                { "page_size", MaxSearchResults.ToString() }
            }, ct).ConfigureAwait(false);
            var url = "https://api.bilibili.com/x/web-interface/wbi/search/type?" + param;
            using var resp = await _http.GetAsync(url, _headers, ct).ConfigureAwait(false);
            var json = JObject.Parse(await HttpHelper.ReadStringAsync(resp));
            var list = json["data"]?["result"]?
                .Select((x, i) => FormatSearchResult((JObject)x, i))
                .Where(x => !string.IsNullOrWhiteSpace(x.Bvid))
                .OrderBy(x => ScoreCandidate(x, info))
                .ThenBy(x => x.Rank)
                .Take(MaxTrackAttempts)
                .ToList();
            if (list == null || list.Count == 0) throw new InvalidOperationException("bilivideo empty");
            return list;
        }

        private async Task<BiliTrack> TrackAsync(BiliItem item, CancellationToken ct)
        {
            JObject viewJson;
            using (var viewResp = await _http.GetAsync($"https://api.bilibili.com/x/web-interface/view?bvid={Uri.EscapeDataString(item.Bvid)}", _headers, ct).ConfigureAwait(false))
            {
                viewJson = JObject.Parse(await HttpHelper.ReadStringAsync(viewResp));
            }

            if (viewJson.Value<int>("code") != 0)
            {
                var viewParam = await SignParamAsync(new Dictionary<string, string>
                {
                    { "bvid", item.Bvid }
                }, ct).ConfigureAwait(false);
                using var viewResp = await _http.GetAsync("https://api.bilibili.com/x/web-interface/wbi/view?" + viewParam, _headers, ct).ConfigureAwait(false);
                viewJson = JObject.Parse(await HttpHelper.ReadStringAsync(viewResp));
                if (viewJson.Value<int>("code") != 0) throw new InvalidOperationException("bilivideo view fail");
            }

            var cid = viewJson["data"]?["cid"]?.ToString();
            if (string.IsNullOrWhiteSpace(cid)) throw new InvalidOperationException("bilivideo cid empty");
            long durationMs = (viewJson["data"]?.Value<long?>("duration") ?? 0) * 1000L;
            item.Title = string.IsNullOrWhiteSpace(item.Title) ? CleanTitle(viewJson["data"]?["title"]?.ToString()) : item.Title;

            var playUrl = $"https://api.bilibili.com/x/player/playurl?bvid={Uri.EscapeDataString(item.Bvid)}&cid={Uri.EscapeDataString(cid)}&fnval=16&platform=pc";
            JObject playJson;
            using (var playResp = await _http.GetAsync(playUrl, _headers, ct).ConfigureAwait(false))
            {
                playJson = JObject.Parse(await HttpHelper.ReadStringAsync(playResp));
            }

            if (playJson.Value<int>("code") != 0 || playJson["data"]?["dash"]?["audio"] == null)
            {
                var playParam = await SignParamAsync(new Dictionary<string, string>
                {
                    { "bvid", item.Bvid },
                    { "cid", cid },
                    { "fnval", "16" },
                    { "platform", "pc" }
                }, ct).ConfigureAwait(false);
                using var playResp = await _http.GetAsync("https://api.bilibili.com/x/player/wbi/playurl?" + playParam, _headers, ct).ConfigureAwait(false);
                playJson = JObject.Parse(await HttpHelper.ReadStringAsync(playResp));
                if (playJson.Value<int>("code") != 0) throw new InvalidOperationException("bilivideo play fail");
            }

            var audioItem = playJson["data"]?["dash"]?["audio"]?
                .OrderByDescending(x => x.Value<int?>("bandwidth") ?? 0)
                .FirstOrDefault();
            var audio = audioItem?["base_url"]?.ToString();
            if (string.IsNullOrEmpty(audio)) throw new InvalidOperationException("bilivideo audio empty");
            long size = await ProbeLengthAsync(audio, item.Bvid, ct).ConfigureAwait(false);
            return new BiliTrack
            {
                Url = audio,
                DurationMs = durationMs > 0 ? durationMs : item.Duration,
                Size = size,
                BitRate = audioItem?.Value<int?>("bandwidth")
            };
        }

        public async Task<AudioResult> CheckAsync(SongInfo info, CancellationToken ct)
        {
            var items = await _cache.GetOrAddAsync($"bilivideo:{info.Keyword}", () => SearchAsync(info, ct), null, _noCache).ConfigureAwait(false);
            AudioResult fallback = null;
            Exception last = null;

            foreach (var item in items)
            {
                try
                {
                    var track = await TrackAsync(item, ct).ConfigureAwait(false);
                    long durationMs = track.DurationMs > 0 ? track.DurationMs : (item.Duration > 0 ? item.Duration : info.Duration);
                    var result = new AudioResult
                    {
                        Url = track.Url,
                        Source = "bilivideo",
                        DurationMs = durationMs,
                        Title = string.IsNullOrWhiteSpace(item.Title) ? info.Name : item.Title,
                        Artists = string.Join(" / ", info.Artists.Select(a => a.Name)),
                        Size = track.Size,
                        BitRate = track.BitRate,
                        Headers = new Dictionary<string, string>
                        {
                            { "Referer", $"https://www.bilibili.com/video/{item.Bvid}/" },
                            { "User-Agent", _headers["User-Agent"] }
                        }
                    };

                    if (LooksFullLength(track.Size, durationMs > 0 ? durationMs : info.Duration))
                    {
                        return result;
                    }

                    fallback ??= result;
                    Log.Debug(_scope, $"candidate kept as fallback: bvid={item.Bvid}, title={item.Title}, duration={durationMs}, size={track.Size}");
                }
                catch (Exception ex)
                {
                    last = ex;
                    Log.Debug(_scope, $"candidate failed: bvid={item.Bvid}, title={item.Title}, reason={ex.Message}");
                }
            }

            if (fallback != null)
            {
                return fallback;
            }

            throw new InvalidOperationException(last == null ? "bilivideo track failed" : "bilivideo track failed: " + last.Message);
        }

        private async Task<long> ProbeLengthAsync(string url, string bvid, CancellationToken ct)
        {
            try
            {
                var headers = new Dictionary<string, string>(_headers, StringComparer.OrdinalIgnoreCase)
                {
                    ["Referer"] = $"https://www.bilibili.com/video/{bvid}/",
                    ["Range"] = "bytes=0-0",
                    ["Accept-Encoding"] = "identity"
                };

                using var resp = await _http.GetAsync(url, headers, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    return 0;
                }

                return resp.Content.Headers.ContentRange?.Length ??
                       (resp.StatusCode == HttpStatusCode.PartialContent ? 0 : (resp.Content.Headers.ContentLength ?? 0));
            }
            catch (Exception ex)
            {
                Log.Debug(_scope, $"probe failed: {ex.Message}");
                return 0;
            }
        }

        private static bool LooksFullLength(long size, long durationMs)
        {
            if (size <= 0 || durationMs <= 0)
            {
                return true;
            }

            double seconds = durationMs / 1000d;
            return size >= seconds * MinAverageBitRate / 8d;
        }

        private static long ParseDurationMs(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return 0;
            }

            var parts = value.Trim().Split(':');
            long totalSeconds = 0;
            foreach (var part in parts)
            {
                if (!long.TryParse(part, out var n))
                {
                    return 0;
                }

                totalSeconds = totalSeconds * 60 + n;
            }

            return totalSeconds * 1000L;
        }

        private static string CleanTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return string.Empty;
            }

            string value = Regex.Replace(title, "<.*?>", string.Empty);
            value = WebUtility.HtmlDecode(value);
            return Regex.Replace(value, "\\s+", " ").Trim();
        }

        private static double ScoreCandidate(BiliItem item, SongInfo info)
        {
            string title = NormalizeText(item.Title);
            string author = NormalizeText(item.Author);
            string songName = NormalizeText(info?.Name);
            string albumName = NormalizeText(info?.Album?.Name);
            bool durationMatched = false;
            bool hasStrongAudioQuality = HasStrongAudioQualitySignal(title);
            bool hasNonOriginalSignal = HasNonOriginalSignal(title);
            double score = item.Rank * 80d;

            if (info != null && info.Duration > 0 && item.Duration > 0)
            {
                long diff = Math.Abs(item.Duration - info.Duration);
                long tolerance = GetDurationToleranceMs(info.Duration);
                durationMatched = diff <= PreferredDurationToleranceMs;

                if (durationMatched)
                {
                    score -= 180000d;
                    score += diff * 0.1d;
                }
                else if (diff <= tolerance)
                {
                    score += 60000d + (diff - PreferredDurationToleranceMs) * 1.5d;
                }
                else
                {
                    score += 140000d + (diff - tolerance) * 3d;
                }
            }
            else
            {
                score += 45000d;
            }

            if (!string.IsNullOrEmpty(songName))
            {
                score += title.Contains(songName) ? -25000d : 30000d;
            }

            if (info?.Artists != null && info.Artists.Count > 0)
            {
                foreach (var artist in info.Artists)
                {
                    string artistName = NormalizeText(artist.Name);
                    if (string.IsNullOrEmpty(artistName))
                    {
                        continue;
                    }

                    score += (title.Contains(artistName) || author.Contains(artistName)) ? -12000d : 8000d;
                }
            }

            if (!string.IsNullOrEmpty(albumName) && title.Contains(albumName))
            {
                score -= 6000d;
            }

            if (hasStrongAudioQuality)
            {
                score -= 70000d;
                if (durationMatched)
                {
                    score -= 30000d;
                }

                if (!string.IsNullOrEmpty(songName) && title.Contains(songName))
                {
                    score -= 12000d;
                }
            }

            if (hasNonOriginalSignal)
            {
                score += 90000d;
                if (!durationMatched)
                {
                    score += 30000d;
                }
            }

            return score;
        }

        private static bool HasStrongAudioQualitySignal(string title)
        {
            return ContainsAny(
                title,
                "hires",
                "hifi",
                "无损",
                "无损音质",
                "母带",
                "臻品母带",
                "高音质",
                "高品质",
                "flac",
                "lossless",
                "24bit",
                "24bits",
                "24位",
                "cd音质",
                "原曲",
                "原版",
                "原唱",
                "歌曲原声",
                "纯享音频");
        }

        private static bool HasNonOriginalSignal(string title)
        {
            return ContainsAny(
                title,
                "现场",
                "live",
                "演唱会",
                "嘉宾",
                "翻唱",
                "cover",
                "女声",
                "男声",
                "钢琴",
                "吉他",
                "架子鼓",
                "鼓谱",
                "琴谱",
                "教程",
                "教学",
                "伴奏",
                "指弹",
                "手碟",
                "remix",
                "改编",
                "光遇",
                "循环",
                "1小时",
                "一小时");
        }

        private static bool ContainsAny(string value, params string[] terms)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            return terms.Any(value.Contains);
        }

        private static string NormalizeText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string normalized = CleanTitle(value).ToLowerInvariant();
            return normalized
                .Replace(" ", string.Empty)
                .Replace("-", string.Empty)
                .Replace("_", string.Empty)
                .Replace("/", string.Empty)
                .Replace("·", string.Empty)
                .Replace("《", string.Empty)
                .Replace("》", string.Empty)
                .Replace("【", string.Empty)
                .Replace("】", string.Empty)
                .Replace("葉", "叶")
                .Replace("倫", "伦");
        }

        private static long GetDurationToleranceMs(long durationMs)
        {
            if (durationMs <= 0)
            {
                return 5000;
            }

            return Math.Max(3000, Math.Min(8000, durationMs / 50));
        }
    }
}
