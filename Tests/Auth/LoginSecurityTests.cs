using System.Net;
using Newtonsoft.Json.Linq;
using YTPlayer.Core.Auth;

internal static partial class Program
{
    private sealed class FakeSecurity : ILoginSecurityContext
    {
        internal int Calls;
        internal readonly List<bool> Requirements = new();
        internal string Token = "synthetic_login_token";
        public Task<LoginSecurityPreparation> PrepareAsync(CookieContainer cookies, bool requireToken, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls++;
            Requirements.Add(requireToken);
            return Task.FromResult(new LoginSecurityPreparation { UserAgent = "Auth.Tests", CheckToken = requireToken ? Token : "" });
        }
        public void Dispose() { }
    }

    private static async Task SecurityRegression()
    {
        var security = new FakeSecurity();
        var handler = new FakeHandler { Reply = "{\"code\":502,\"message\":\"验证码错误\"}" };
        var cookies = new CookieContainer();
        using var client = new LoginProtocolClient(cookies, handler, security);
        await client.SendCaptchaAsync("13800000000", "86", default);
        await client.LoginAsync("13800000000", "123456", "86", default);
        await client.LoginAsync("13800000000", "654321", "86", default);
        Check(security.Requirements.SequenceEqual(new[] { false, true, true }), "prepare send session and refresh security token for every redemption");
        Check(handler.Path == "/weapi/login/cellphone", "SMS uses the official captcha route");
        Check(handler.UserAgent == "Auth.Tests" && handler.Origin == "https://music.163.com", "HTTP browser identity and origin are consistent");
        int before = handler.Calls;
        security.Token = "";
        await Throws<InvalidOperationException>(() => client.LoginAsync("13800000000", "123456", "86", default), "missing token fails before SMS redemption");
        await Throws<InvalidOperationException>(() => client.CreateWeChatAsync(default), "missing token fails before OAuth authorization");
        Check(handler.Calls == before, "never send an empty or fabricated login token");
        security.Token = "synthetic+token/==";
        handler.ResponseUri = new Uri("https://open.weixin.qq.com/connect/qrconnect?state=initial");
        handler.Reply = "var usenewdomain = '1';var poll='https://long.open.weixin.qq.com/connect/l/qrconnect?uuid=synthetic_uuid';var back='https://music.163.com/back/weichat';back+='&state=rendered_state';";
        var session = await client.CreateWeChatAsync(default);
        Check(handler.RequestUri!.Query.Contains("checkToken=synthetic%2Btoken%2F%3D%3D", StringComparison.Ordinal), "OAuth receives an encoded login token");
        Check(session.State == "rendered_state", "OAuth still preserves server-rendered state");
        handler.ResponseUri = new Uri("https://music.163.com/back/sns?code=must_not_be_logged");
        foreach (int code in new[] { 401, 10004, 415 })
        {
            session = new WeChatLoginSession("synthetic", "state", handler.ResponseUri, "lp.open.weixin.qq.com");
            handler.Reply = "{\"code\":" + code + ",\"message\":\"synthetic rejection\"}";
            try { await client.CompleteWeChatAsync(session, "synthetic-auth-code", default); throw new Exception("callback rejection was accepted"); }
            catch (InvalidOperationException ex)
            {
                Check(ex.Message.Contains(code.ToString()) && ex.Message.Contains("synthetic rejection"), "preserve callback business error " + code);
            }
            before = handler.Calls;
            await Throws<InvalidOperationException>(() => client.CompleteWeChatAsync(session, "synthetic-auth-code", default), "callback code cannot be redeemed twice");
            Check(handler.Calls == before, "callback business failure is not retried");
        }
        handler.Reply = "{\"code\":200}";
        session = new WeChatLoginSession("synthetic", "state", handler.ResponseUri, "lp.open.weixin.qq.com");
        await Throws<InvalidOperationException>(() => client.CompleteWeChatAsync(session, "synthetic-auth-code", default), "callback success code without credentials is not login");
        handler.Reply = "<html>official verification page</html>";
        session = new WeChatLoginSession("synthetic", "state", handler.ResponseUri, "lp.open.weixin.qq.com");
        await Throws<InvalidOperationException>(() => client.CompleteWeChatAsync(session, "synthetic-auth-code", default), "callback HTML without credentials is not login");
        await Throws<InvalidOperationException>(() => Task.FromResult(LoginProtocolClient.ParseWeChatPoll("window.wx_errcode=99999999999999999999;")), "malformed poll integer fails without overflow");

        string? oldPath = Environment.GetEnvironmentVariable("YTPLAYER_LOGIN_DIAGNOSTICS");
        string path = Path.Combine(Environment.GetEnvironmentVariable("YTPLAYER_ROOT")!, "redaction-test.jsonl");
        try
        {
            Environment.SetEnvironmentVariable("YTPLAYER_LOGIN_DIAGNOSTICS", path);
            string secret = "DO_NOT_LOG_THIS_SECRET";
            var data = new JObject { ["phone"] = secret, ["captcha"] = secret, ["checkToken"] = secret,
                ["message"] = secret, ["data"] = new JObject { ["verifyUrl"] = "https://example.com/?token=" + secret }, [secret] = secret, ["code"] = 10004 };
            var logCookies = new CookieContainer();
            logCookies.Add(new Uri("https://music.163.com"), new Cookie("MUSIC_U", secret));
            LoginDiagnostics.Write("synthetic-flow", "redaction-test", new Uri("https://music.163.com/back/sns?code=" + secret),
                200, data, data, data.ToString(), logCookies, true);
            string log = File.ReadAllText(path);
            Check(!log.Contains(secret), "no secrets in query, request, message, nested response, unknown field name or cookie");
            var metadata = JObject.Parse(log);
            Check(metadata.Value<int>("businessCode") == 10004 && metadata.Value<bool>("hasMusicU"), "retain actionable business code and credential presence");
            Check(metadata["requestSchema"]!.Value<string>("captcha") == "String", "retain field type without value");
            Check(LoginDiagnostics.SafeRoute(new Uri("https://open.weixin.qq.com/connect/qrcode/" + secret)).EndsWith("[redacted]"), "QR identifier redacted");
            Check(LoginDiagnostics.SafeRoute(new Uri("https://music.163.com.evil.test/" + secret)) == "other-origin", "host lookalikes and arbitrary paths redacted");
        }
        finally { Environment.SetEnvironmentVariable("YTPLAYER_LOGIN_DIAGNOSTICS", oldPath); }
    }

    private static async Task SecurityProbe()
    {
        using var security = new OfficialLoginSecurityContext();
        var cookies = new CookieContainer();
        using (var cancellation = new CancellationTokenSource(100))
            await Throws<OperationCanceledException>(() => security.PrepareAsync(cookies, true, cancellation.Token), "cancel browser initialization without leaving the queue blocked");
        var first = await security.PrepareAsync(cookies, true, default);
        Check(!string.IsNullOrWhiteSpace(first.CheckToken), "official login SDK returns a real token");
        Check(first.UserAgent.Contains("Edg"), "use the actual browser user agent");
        Check(string.IsNullOrEmpty(cookies.GetCookies(new Uri("https://music.163.com"))["MUSIC_U"]?.Value), "browser starts anonymously");
        var second = await security.PrepareAsync(cookies, true, default);
        Check(!string.IsNullOrWhiteSpace(second.CheckToken) && first.CheckToken != second.CheckToken, "new token for every attempt");
        Console.WriteLine("Official login SDK: two fresh tokens, anonymous isolated cookies, no SMS or login submitted.");
    }
}
