using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using YTPlayer.Core.ListenTogether;

namespace YTPlayer.Core
{
    public partial class NeteaseApiClient
    {
        public ListenTogetherApi CreateListenTogetherApi() => new ListenTogetherApi(SendListenTogetherAsync);

        private Task<JObject> SendListenTogetherAsync(string path, JObject payload, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(_musicU))
                throw new ListenTogetherApiException(301, "请先登录网易云音乐");

            // The live desktop-session probe requires the complete session cookie map.
            // Do not replace it with only MUSIC_U / csrf, or percent-encode saved cookies twice.
            var header = new Dictionary<string, object>(System.StringComparer.OrdinalIgnoreCase);
            string cookieHeader;
            lock (_cookieLock)
            {
                foreach (Cookie cookie in _cookieContainer.GetCookies(EAPI_URI)) header[cookie.Name] = cookie.Value;
                cookieHeader = _cookieContainer.GetCookieHeader(EAPI_URI);
            }
            header["MUSIC_U"] = _musicU;
            header["__csrf"] = _csrfToken ?? string.Empty;
            var body = payload.ToObject<Dictionary<string, object>>() ?? new Dictionary<string, object>();
            // status/get must use the same EAPI session as room mutations. The WEAPI
            // route intermittently returns HTTP 200 with zero bytes for valid accounts.
            body["header"] = header;
            body["e_r"] = "true";
            return PostEApiAsync<JObject>(path, body, useIosHeaders: false, retryCount: MAX_RETRY_COUNT,
                skipErrorHandling: true, cancellationToken: token, cookieHeaderOverride: cookieHeader);
        }
    }
}
