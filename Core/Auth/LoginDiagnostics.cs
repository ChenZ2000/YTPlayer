using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YTPlayer.Utils;

namespace YTPlayer.Core.Auth
{
    /// <summary>Allowlisted metadata only. Never write response text, headers, URLs with queries,
    /// phone numbers, verification codes, tokens, user IDs, or cookie values.</summary>
    internal static class LoginDiagnostics
    {
        private static readonly object Sync = new object();
        private static readonly HashSet<string> Fields = new HashSet<string>(StringComparer.Ordinal)
        {
            "phone", "countrycode", "captcha", "rememberLogin", "checkToken", "cellphone", "ctcode", "secrete",
            "csrf_token", "code", "message", "msg", "data", "account", "profile", "bindings", "token", "loginType",
            "captchaId", "url", "redirectUrl", "verifyUrl", "ext", "type", "https", "remember", "secureCaptcha"
        };

        internal static string LogPath => Environment.GetEnvironmentVariable("YTPLAYER_LOGIN_DIAGNOSTICS")
            ?? Path.Combine(PathHelper.ApplicationRootDirectory, "logs", "login-protocol.jsonl");

        internal static JObject Schema(JObject? data) => data == null ? new JObject() : new JObject(data.Properties()
            .Where(p => Fields.Contains(p.Name)).Select(p => new JProperty(p.Name, p.Value.Type.ToString())));

        internal static string SafeRoute(Uri? uri)
        {
            if (uri == null) return "unknown";
            string host = uri.Host;
            if (host != "music.163.com" && host != "open.weixin.qq.com" && host != "lp.open.weixin.qq.com" && host != "long.open.weixin.qq.com") return "other-origin";
            string path = uri.AbsolutePath;
            if (path.StartsWith("/connect/qrcode/", StringComparison.Ordinal)) path = "/connect/qrcode/[redacted]";
            else if (path != "/" && path != "/back/sns" && path != "/back/weichat" && path != "/api/sns/authorize" &&
                path != "/connect/qrconnect" && path != "/connect/l/qrconnect" && path != "/weapi/sms/captcha/sent" &&
                path != "/weapi/login/cellphone" && path != "/weapi/w/login/cellphone" && path != "/weapi/w/nuser/account/get") path = "/[other-path]";
            return host + path;
        }

        internal static void Write(string flow, string stage, Uri? uri = null, int? httpStatus = null,
            JObject? request = null, JObject? response = null, string? body = null, CookieContainer? cookies = null,
            bool? checkTokenPresent = null, bool? success = null)
        {
            try
            {
                var record = new JObject
                {
                    ["timeUtc"] = DateTimeOffset.UtcNow.ToString("O"), ["flow"] = flow, ["stage"] = stage,
                    ["route"] = SafeRoute(uri), ["httpStatus"] = httpStatus,
                    ["requestSchema"] = Schema(request), ["responseSchema"] = Schema(response),
                    ["businessCode"] = response?["code"]?.Type == JTokenType.Integer ? response["code"]!.Value<long>() : (long?)null,
                    ["bodyKind"] = body == null ? null : string.IsNullOrWhiteSpace(body) ? "empty" : response != null ? "json" : body.TrimStart().StartsWith("<", StringComparison.Ordinal) ? "html" : "text",
                    ["bodyLength"] = body?.Length, ["checkTokenPresent"] = checkTokenPresent, ["success"] = success
                };
                if (cookies != null)
                {
                    var list = cookies.GetCookies(new Uri("https://music.163.com"));
                    record["cookieCount"] = list.Count;
                    record["hasMusicU"] = !string.IsNullOrEmpty(list["MUSIC_U"]?.Value);
                    record["hasCsrf"] = !string.IsNullOrEmpty(list["__csrf"]?.Value);
                }
                lock (Sync)
                {
                    string path = Path.GetFullPath(LogPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.AppendAllText(path, record.ToString(Formatting.None) + Environment.NewLine, new System.Text.UTF8Encoding(false));
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is JsonException || ex is OverflowException)
            { /* Diagnostics must not break authentication or leak raw values through an error log. */ }
        }
    }
}
