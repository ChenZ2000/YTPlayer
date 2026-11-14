using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YTPlayer.Utils;

namespace YTPlayer.Core.Auth
{
    /// <summary>
    /// A login attempt owns its cookies. Failed/cancelled attempts never replace the player's account.
    /// There are deliberately no automatic retries for SMS sends or one-use OAuth codes.
    /// </summary>
    internal sealed class LoginProtocolClient : IDisposable
    {
        private static readonly Uri Music = new Uri("https://music.163.com");
        private readonly CookieContainer _cookies;
        private readonly HttpClient _http;
        private readonly ILoginSecurityContext _security;
        private readonly string _flow = Guid.NewGuid().ToString("N");

        internal LoginProtocolClient() : this(new CookieContainer { PerDomainCapacity = 100 }, null, new OfficialLoginSecurityContext()) { }

        internal LoginProtocolClient(CookieContainer cookies, HttpMessageHandler? handler, ILoginSecurityContext security)
        {
            _cookies = cookies;
            _security = security;
            _http = new HttpClient(handler ?? new HttpClientHandler
            {
                CookieContainer = _cookies,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                AllowAutoRedirect = true
            }) { Timeout = TimeSpan.FromSeconds(40) };
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", AuthConstants.WeapiUserAgent);
            _http.DefaultRequestHeaders.Referrer = Music;
            _http.DefaultRequestHeaders.ExpectContinue = false;
        }

        internal static void ValidatePhone(string phone, string countryCode)
        {
            if (!Regex.IsMatch(countryCode ?? "", @"^[1-9][0-9]{0,3}$") ||
                !Regex.IsMatch(phone ?? "", @"^[0-9]{5,15}$") ||
                (countryCode == "86" && !Regex.IsMatch(phone ?? "", @"^1[3-9][0-9]{9}$")))
                throw new ArgumentException("请输入有效的手机号和国家区号（区号不含加号）。");
        }

        internal static Dictionary<string, object> CaptchaPayload(string phone, string countryCode)
        {
            ValidatePhone(phone, countryCode);
            return new Dictionary<string, object> { ["cellphone"] = phone, ["ctcode"] = countryCode };
        }

        internal static Dictionary<string, object> PhonePayload(string phone, string captcha, string countryCode)
        {
            ValidatePhone(phone, countryCode);
            if (!Regex.IsMatch(captcha ?? "", @"^[0-9]{4,8}$")) throw new ArgumentException("请输入4至8位短信验证码。");
            return new Dictionary<string, object> { ["phone"] = phone, ["countrycode"] = countryCode,
                ["captcha"] = captcha!, ["rememberLogin"] = true };
        }

        internal async Task<JObject> SendCaptchaAsync(string phone, string countryCode, CancellationToken token)
        {
            var data = CaptchaPayload(phone, countryCode);
            await PrepareSecurityAsync(false, token).ConfigureAwait(false);
            return await PostAsync("/sms/captcha/sent", data, token).ConfigureAwait(false);
        }

        internal async Task<JObject> LoginAsync(string phone, string captcha, string countryCode, CancellationToken token)
        {
            var data = PhonePayload(phone, captcha, countryCode);
            data["checkToken"] = await PrepareSecurityAsync(true, token).ConfigureAwait(false);
            // The current official web client uses a separate route for SMS, not its password route.
            return await PostAsync("/login/cellphone", data, token).ConfigureAwait(false);
        }

        private async Task<string> PrepareSecurityAsync(bool requireToken, CancellationToken token)
        {
            var prepared = await _security.PrepareAsync(_cookies, requireToken, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (requireToken && string.IsNullOrWhiteSpace(prepared.CheckToken))
                throw new InvalidOperationException("官网安全组件未生成登录令牌，请重新打开登录窗口。");
            if (!string.IsNullOrWhiteSpace(prepared.UserAgent))
            {
                _http.DefaultRequestHeaders.Remove("User-Agent");
                _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", prepared.UserAgent);
            }
            LoginDiagnostics.Write(_flow, "security-ready", cookies: _cookies, checkTokenPresent: !string.IsNullOrWhiteSpace(prepared.CheckToken));
            return prepared.CheckToken;
        }

        private async Task<JObject> PostAsync(string path, Dictionary<string, object> data, CancellationToken token)
        {
            data["csrf_token"] = _cookies.GetCookies(Music)["__csrf"]?.Value ?? "";
            var uri = new Uri(Music, "/weapi" + path);
            LoginDiagnostics.Write(_flow, "request", uri, request: JObject.FromObject(data), cookies: _cookies,
                checkTokenPresent: data.TryGetValue("checkToken", out var check) && !string.IsNullOrWhiteSpace(check as string));
            var encrypted = EncryptionHelper.EncryptWeapi(JsonConvert.SerializeObject(data, Formatting.None));
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
                { ["params"] = encrypted.Params, ["encSecKey"] = encrypted.EncSecKey });
            using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = content };
            request.Headers.TryAddWithoutValidation("Origin", Music.GetLeftPart(UriPartial.Authority));
            using var response = await _http.SendAsync(request, token).ConfigureAwait(false);
            string text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            JObject? result = null;
            try { result = JObject.Parse(text); } catch (JsonException) { }
            LoginDiagnostics.Write(_flow, "response", uri, (int)response.StatusCode, response: result, body: text, cookies: _cookies);
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(text)) throw new HttpRequestException("登录服务器返回空响应，请稍后重试。");
            if (result == null) throw new HttpRequestException("登录服务器返回非 JSON 数据（HTTP " + (int)response.StatusCode + "）。");
            if (!response.IsSuccessStatusCode && result.Value<int?>("code") == 200)
                throw new HttpRequestException("登录请求 HTTP 状态异常（" + (int)response.StatusCode + "）。");
            return result;
        }

        internal async Task<JObject> VerifyAccountAsync(CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(_cookies.GetCookies(Music)["MUSIC_U"]?.Value))
                throw new InvalidOperationException("服务器未建立登录会话；尚未完成登录。可在网页登录中重试，脱敏诊断已记录。");
            var result = await PostAsync("/w/nuser/account/get", new Dictionary<string, object>(), token).ConfigureAwait(false);
            var data = result["data"] as JObject ?? result;
            if ((data.Value<int?>("code") ?? result.Value<int?>("code")) != 200 ||
                ((data["account"]?["id"]?.Value<long>() ?? 0) <= 0 && (data["profile"]?["userId"]?.Value<long>() ?? 0) <= 0))
                throw new InvalidOperationException("登录凭据尚未通过账号接口验证，请重试登录。");
            LoginDiagnostics.Write(_flow, "account-verified", cookies: _cookies, success: true);
            return data;
        }

        internal string CookieSnapshot() => string.Join("; ", _cookies.GetCookies(Music).Cast<Cookie>()
            .Select(c => c.Name + "=" + c.Value));

        internal async Task<WeChatLoginSession> CreateWeChatAsync(CancellationToken token)
        {
            string checkToken = await PrepareSecurityAsync(true, token).ConfigureAwait(false);
            var uri = new Uri(Music, "/api/sns/authorize?clientType=web2&snsType=10&callbackType=Login&forcelogin=true&checkToken=" + Uri.EscapeDataString(checkToken));
            LoginDiagnostics.Write(_flow, "wechat-authorize", uri, cookies: _cookies, checkTokenPresent: true);
            using var response = await _http.GetAsync(uri, token).ConfigureAwait(false);
            LoginDiagnostics.Write(_flow, "wechat-authorize-response", response.RequestMessage?.RequestUri, (int)response.StatusCode, cookies: _cookies);
            response.EnsureSuccessStatusCode();
            Uri? authorizeUri = response.RequestMessage?.RequestUri;
            if (authorizeUri?.Scheme != "https" || authorizeUri.Host != "open.weixin.qq.com" || authorizeUri.AbsolutePath != "/connect/qrconnect")
                throw new InvalidOperationException("网易云未返回有效的微信授权页面。");
            string html = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            return ParseWeChatPage(html, authorizeUri);
        }

        internal static WeChatLoginSession ParseWeChatPage(string html, Uri authorizeUri)
        {
            // Read the server-rendered callback state, which can differ from the initial URL's state.
            var uuid = Regex.Match(html, @"https://(?:long|lp)\.open\.weixin\.qq\.com/connect/l/qrconnect\?uuid=([A-Za-z0-9_-]+)");
            var state = Regex.Match(html, @"[""']&state=([A-Za-z0-9_-]+)[""']");
            var callback = Regex.Match(html, @"https://music\.163\.com/back/weichat(?=[""'])");
            if (!uuid.Success || !state.Success || !callback.Success)
                throw new InvalidOperationException("微信授权页面格式已变化，请使用网页登录并反馈此问题。");
            return new WeChatLoginSession(uuid.Groups[1].Value, state.Groups[1].Value, authorizeUri,
                html.Contains("var usenewdomain = '1'", StringComparison.Ordinal) ? "lp.open.weixin.qq.com" : "long.open.weixin.qq.com");
        }

        internal async Task<byte[]> GetWeChatImageAsync(WeChatLoginSession session, CancellationToken token)
        {
            using var response = await _http.GetAsync("https://open.weixin.qq.com/connect/qrcode/" + session.Uuid, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true)
                throw new InvalidOperationException("微信未返回二维码图片。");
            return await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
        }

        internal async Task<WeChatLoginPollResult> PollWeChatAsync(WeChatLoginSession session, CancellationToken token)
        {
            if (DateTimeOffset.UtcNow - session.CreatedAt > TimeSpan.FromMinutes(5))
                return new WeChatLoginPollResult { Status = WeChatLoginStatus.Expired };
            string url = "https://" + session.PollHost + "/connect/l/qrconnect?uuid=" + session.Uuid;
            if (session.LastCode.HasValue) url += "&last=" + session.LastCode.Value.ToString(CultureInfo.InvariantCulture);
            using var response = await _http.GetAsync(url, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var result = ParseWeChatPoll(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
            LoginDiagnostics.Write(_flow, "wechat-poll", response.RequestMessage?.RequestUri, (int)response.StatusCode,
                response: new JObject { ["code"] = result.Code }, cookies: _cookies);
            session.LastCode = result.Code;
            return result;
        }

        internal static WeChatLoginPollResult ParseWeChatPoll(string script)
        {
            var match = Regex.Match(script, @"\bwx_errcode\s*=\s*(\d+)\s*;");
            if (!match.Success) throw new InvalidOperationException("微信轮询响应格式无效，请刷新二维码。");
            if (!int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int code))
                throw new InvalidOperationException("微信轮询状态码无效，请刷新二维码。");
            var result = new WeChatLoginPollResult { Code = code, Status = code switch
            {
                408 => WeChatLoginStatus.WaitingForScan, 404 => WeChatLoginStatus.AwaitingConfirmation,
                403 => WeChatLoginStatus.Denied, 402 => WeChatLoginStatus.Expired,
                405 => WeChatLoginStatus.Authorized, _ => WeChatLoginStatus.Error
            }};
            if (code == 405)
            {
                var authCode = Regex.Match(script, @"\bwx_code\s*=\s*[""']([A-Za-z0-9_-]+)[""']\s*;");
                if (!authCode.Success) throw new InvalidOperationException("微信授权响应缺少授权码。");
                result.AuthorizationCode = authCode.Groups[1].Value;
            }
            return result;
        }

        internal async Task CompleteWeChatAsync(WeChatLoginSession session, string code, CancellationToken token)
        {
            if (Interlocked.Exchange(ref session.CodeRedeemed, 1) != 0)
                throw new InvalidOperationException("此微信授权已处理，请刷新二维码。");
            // The official callback performs the OAuth exchange; no app secret is shipped in the player.
            using var response = await _http.GetAsync("https://music.163.com/back/weichat?code=" +
                Uri.EscapeDataString(code) + "&state=" + Uri.EscapeDataString(session.State), token).ConfigureAwait(false);
            string text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            JObject? result = null;
            try { result = JObject.Parse(text); } catch (JsonException) { }
            LoginDiagnostics.Write(_flow, "wechat-callback", response.RequestMessage?.RequestUri, (int)response.StatusCode,
                response: result, body: text, cookies: _cookies);
            response.EnsureSuccessStatusCode();
            token.ThrowIfCancellationRequested();
            if (result?.Value<int?>("code") is int businessCode && businessCode != 200)
            {
                string message = result.Value<string>("message") ?? result.Value<string>("msg") ?? "微信授权回调未通过，请刷新二维码后重新确认";
                throw new InvalidOperationException(message + "（" + businessCode + "）。");
            }
            if (string.IsNullOrWhiteSpace(_cookies.GetCookies(Music)["MUSIC_U"]?.Value))
                throw new InvalidOperationException("微信授权回调未建立登录会话（HTTP " + (int)response.StatusCode +
                    "，" + (result != null ? "JSON 响应" : "页面响应") + "）。请使用网页登录继续，脱敏诊断已记录。");
        }

        public void Dispose() { _http.Dispose(); _security.Dispose(); }
    }
}
