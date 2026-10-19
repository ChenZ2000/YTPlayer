using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using YTPlayer.Core.Recognition;
using YTPlayer.Models;

namespace YTPlayer.Core
{
    public partial class NeteaseApiClient
    {
        public Task<List<SongInfo>> RecognizeSongAsync(string audioFingerprint, int durationSeconds, CancellationToken cancellationToken = default) =>
            RecognizeSongAsync(audioFingerprint, durationSeconds, Guid.NewGuid().ToString("N"), 1, cancellationToken);

        internal async Task<List<SongInfo>> RecognizeSongAsync(string fingerprint, double duration, string sessionId, int attempt, CancellationToken token)
        {
            var payload = BuildRecognitionPayload(fingerprint, duration, sessionId, attempt);
            token.ThrowIfCancellationRequested();
            JObject response;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                var header = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                string cookies;
                lock (_cookieLock)
                {
                    foreach (Cookie cookie in _cookieContainer.GetCookies(EAPI_URI)) header[cookie.Name] = cookie.Value;
                    cookies = _cookieContainer.GetCookieHeader(EAPI_URI);
                }
                payload["header"] = JObject.FromObject(header);
                payload["e_r"] = "true";
                response = await PostEApiAsync<JObject>("/api/music/audio/match", payload, useIosHeaders: false,
                    retryCount: MAX_RETRY_COUNT, skipErrorHandling: true, cancellationToken: timeout.Token,
                    cookieHeaderOverride: cookies).ConfigureAwait(false);
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                // One first-party transport fallback, preserving the same recognition session.
                // Business errors are checked below and must never masquerade as a no-match.
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                string query = string.Join("&", payload.Properties().Where(p => p.Name != "header" && p.Name != "e_r")
                    .Select(p => Uri.EscapeDataString(p.Name) + "=" + Uri.EscapeDataString(p.Value.ToString())));
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://interface.music.163.com/api/music/audio/match?" + query);
                request.Headers.Referrer = MUSIC_URI;
                try
                {
                    using var http = await _httpClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
                    http.EnsureSuccessStatusCode();
                    string json = await http.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(json)) throw new HttpRequestException("识曲服务返回空响应。");
                    response = JObject.Parse(json);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                { throw new TimeoutException("识曲服务响应超时，请检查网络后重试。"); }
            }
            token.ThrowIfCancellationRequested();
            return ParseRecognitionResponse(response);
        }

        internal static JObject BuildRecognitionPayload(string fingerprint, double duration, string sessionId, int attempt)
        {
            if (string.IsNullOrWhiteSpace(fingerprint)) throw new ArgumentException("音频指纹不能为空。", nameof(fingerprint));
            _ = Convert.FromBase64String(fingerprint);
            if (!double.IsFinite(duration) || duration <= 0 || duration > 30) throw new ArgumentOutOfRangeException(nameof(duration));
            if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("识别会话不能为空。", nameof(sessionId));
            if (attempt < 1 || attempt > 10) throw new ArgumentOutOfRangeException(nameof(attempt));
            return new JObject { ["rawdata"] = fingerprint, ["duration"] = duration.ToString("0.0###", CultureInfo.InvariantCulture),
                ["times"] = attempt.ToString(CultureInfo.InvariantCulture), ["sessionId"] = sessionId,
                ["sceneParams"] = "{\"code\":2}", ["decrypt"] = "1", ["algorithmCode"] = "shazam_v2" };
        }

        internal List<SongInfo> ParseRecognitionResponse(JObject? response)
        {
            int code = response?.Value<int?>("code") ?? -1;
            if (code != 200) throw new SongRecognitionException(code,
                response?.Value<string>("message") ?? response?.Value<string>("msg") ?? "识曲接口返回了异常状态。");
            if (response?["data"] is not JObject data || !data.ContainsKey("result"))
                throw new SongRecognitionException(-1, "识曲响应缺少结果字段，请稍后重试。");
            if (data["result"]?.Type == JTokenType.Null) return new List<SongInfo>();
            if (data["result"] is not JArray items) throw new SongRecognitionException(-1, "识曲响应格式无效。");
            var songs = new List<SongInfo>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in items.OfType<JObject>())
            {
                if (item["song"] is not JObject songData) continue;
                var song = ParseSongList(new JArray(songData)).FirstOrDefault();
                if (song == null || !seen.Add(song.Id)) continue;
                song.MatchStartMs = item.Value<long?>("startTime");
                song.MatchScore = item.Value<double?>("score");
                song.ViewSource = "listen-match";
                songs.Add(song);
            }
            if (items.Count > 0 && songs.Count == 0) throw new SongRecognitionException(-1, "识曲服务返回的歌曲信息不完整。");
            return songs;
        }
    }
}
