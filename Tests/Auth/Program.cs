using System.Net;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using YTPlayer.Core;
using YTPlayer.Core.Auth;
using YTPlayer.Forms;
using YTPlayer.Utils;

internal static partial class Program
{
    private static int _passed;
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); _passed++; }
    private static async Task Throws<T>(Func<Task> action, string name) where T : Exception
    { try { await action(); } catch (T) { _passed++; return; } throw new Exception(name); }

    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "test-data-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root); Environment.SetEnvironmentVariable("YTPLAYER_ROOT", root);
        try
        {
            if (args.Length > 0 && args[0] == "--interactive-capture") InteractiveCapture(args.Length > 1 ? args[1] : "Both");
            else if (args.Length > 0 && args[0] == "--capture-smoke") InteractiveCapture("Phone", smoke: true);
            else if (args.Length > 0 && args[0] == "--security-probe") SecurityProbe().GetAwaiter().GetResult();
            else if (args.Length > 0 && args[0] == "--web-popup-probe") WebPopupProbe();
            else if (args.Length > 0 && args[0] == "--live-sms") LiveSms(args[1]).GetAwaiter().GetResult();
            else if (args.Length > 0 && args[0] == "--redeem-sms") RedeemSms(args[1]).GetAwaiter().GetResult();
            else if (args.Length > 0 && args[0] == "--live-ui") LiveUi();
            else if (args.Length > 0 && args[0] == "--probe-code") ProbeCode(args[1]).GetAwaiter().GetResult();
            else if (args.Length > 0 && args[0] == "--live") Live(args.Skip(1).ToArray()).GetAwaiter().GetResult();
            else { UiTests(); PhoneInteractionTests(); WeChatInteractionTests(); Run().GetAwaiter().GetResult(); SecurityRegression().GetAwaiter().GetResult(); }
            Console.WriteLine($"PASS {_passed} assertions"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message); return 1; }
        finally
        {
            // Give disposed WebView controllers time to release their per-test files.
            for (int attempt = 0; attempt < 25 && Directory.Exists(root); attempt++)
            {
                try { Directory.Delete(root, true); break; }
                catch (IOException) { Application.DoEvents(); Thread.Sleep(200); }
                catch (UnauthorizedAccessException) { Application.DoEvents(); Thread.Sleep(200); }
            }
        }
    }

    private static void UiTests()
    {
        using var api = new NeteaseApiClient();
        using var form = new OfflineLoginForm(api);
        var tabs = form.Controls.OfType<TabControl>().Single();
        Check(tabs.TabPages.Cast<TabPage>().Select(p => p.Text).SequenceEqual(new[] { "二维码登录", "手机号登录", "微信登录", "网页登录" }), "tab order");
        Control[] All(Control c) => c.Controls.Cast<Control>().SelectMany(x => new[] { x }.Concat(All(x))).ToArray();
        var fields = All(tabs.TabPages[1]).OfType<TextBox>().ToArray();
        Check(fields.Length == 3 && fields.All(x => !string.IsNullOrEmpty(x.AccessibleName)), "accessible phone fields");
        Check(fields.Single(x => x.AccessibleName == "手机号").Text.Length == 0, "no test account hardcoded");
        form.Show(); tabs.SelectedIndex = 1; Application.DoEvents();
        Check(fields.All(x => x.AccessibilityObject.Name == x.AccessibleName && x.AccessibilityObject.Role == AccessibleRole.Text), "Windows accessibility exposes named text fields");
        var output = Environment.GetEnvironmentVariable("YTPLAYER_AUTH_OUTPUT");
        foreach (float scale in new[] { 1f, 1.5f, 2f })
        {
            form.Font = new System.Drawing.Font("Microsoft YaHei UI", 10.5f * scale);
            form.PerformAutoScale(); form.PerformLayout(); Application.DoEvents();
            foreach (var c in All(tabs.TabPages[1]).Where(c => c.Visible && c is TextBox or Button))
                Check(c.Parent!.ClientRectangle.Contains(c.Bounds), "visible phone control within parent at " + scale);
            Check(fields.OrderBy(x => x.TabIndex).Select(x => x.AccessibleName).SequenceEqual(new[] { "国家区号，不含加号", "手机号", "短信验证码" }), "keyboard field order");
            if (output != null)
            {
                Directory.CreateDirectory(output);
                using var image = new System.Drawing.Bitmap(form.Width, form.Height);
                form.DrawToBitmap(image, new System.Drawing.Rectangle(0, 0, image.Width, image.Height));
                image.Save(Path.Combine(output, "phone-" + scale + ".png"));
            }
        }
        form.Close();
    }

    private sealed class OfflineLoginForm : LoginForm
    {
        internal OfflineLoginForm(NeteaseApiClient api) : base(api) { }
        protected override void OnLoad(EventArgs e) { }
    }

    private static async Task Run()
    {
        var payload = LoginProtocolClient.PhonePayload("13800000000", "123456", "86");
        Check(payload["rememberLogin"] is true && !payload.ContainsKey("password") && !payload.ContainsKey("type"), "official SMS contract");
        Check(!LoginProtocolClient.CaptchaPayload("13800000000", "86").ContainsKey("secrete"), "official web SMS fields without password-flow secret");
        await Throws<ArgumentException>(() => Task.FromResult(LoginProtocolClient.PhonePayload("13800000000", "", "86")), "invalid captcha");
        await Throws<ArgumentException>(() => Task.FromResult(LoginProtocolClient.CaptchaPayload("123", "86")), "invalid phone");
        foreach (var pair in new Dictionary<int, WeChatLoginStatus> { [408] = WeChatLoginStatus.WaitingForScan, [404] = WeChatLoginStatus.AwaitingConfirmation,
            [403] = WeChatLoginStatus.Denied, [402] = WeChatLoginStatus.Expired, [500] = WeChatLoginStatus.Error, [405] = WeChatLoginStatus.Authorized })
        {
            var result = LoginProtocolClient.ParseWeChatPoll($"window.wx_errcode={pair.Key};window.wx_code='synthetic_code';");
            Check(result.Status == pair.Value, "WeChat state " + pair.Key);
        }
        await Throws<InvalidOperationException>(() => Task.FromResult(LoginProtocolClient.ParseWeChatPoll("window.wx_errcode=405;")), "missing authorization code");
        await Throws<InvalidOperationException>(() => Task.FromResult(LoginProtocolClient.ParseWeChatPoll("<html>error</html>")), "bad poll format");
        var uri = new Uri("https://open.weixin.qq.com/connect/qrconnect?state=initial");
        var session = LoginProtocolClient.ParseWeChatPage("var usenewdomain = '1'; var x='https://long.open.weixin.qq.com/connect/l/qrconnect?uuid=synthetic_uuid';var t=\"https://music.163.com/back/weichat\";t+=\"&state=rendered_state\";", uri);
        Check(session.State == "rendered_state" && session.PollHost == "lp.open.weixin.qq.com", "rendered state and poll domain");
        await Throws<InvalidOperationException>(() => Task.FromResult(LoginProtocolClient.ParseWeChatPage("unexpected page", uri)), "changed page fails closed");

        var cookies = new CookieContainer();
        var handler = new FakeHandler();
        using var protocol = new LoginProtocolClient(cookies, handler, new FakeSecurity());
        handler.Reply = "{\"code\":400,\"message\":\"invalid captcha\"}";
        var failed = await protocol.LoginAsync("13800000000", "123456", "86", default);
        Check(failed.Value<int>("code") == 400 && handler.Calls == 1 && handler.Path == "/weapi/login/cellphone", "business failure without retry");
        Check(handler.Body.Contains("encSecKey=") && !handler.Body.Contains("13800000000") && !handler.Body.Contains("123456"), "encrypted form");
        handler.Reply = "";
        await Throws<HttpRequestException>(() => protocol.SendCaptchaAsync("13800000000", "86", default), "empty response rejected");
        Check(handler.Calls == 2 && handler.Path == "/weapi/sms/captcha/sent", "SMS not retried");
        handler.Reply = "<html>error</html>";
        await Throws<HttpRequestException>(() => protocol.SendCaptchaAsync("13800000000", "86", default), "HTML rejected");
        handler.Reply = "{\"code\":200}"; handler.Status = HttpStatusCode.InternalServerError;
        await Throws<HttpRequestException>(() => protocol.SendCaptchaAsync("13800000000", "86", default), "HTTP failure cannot claim success");
        handler.Status = HttpStatusCode.OK;
        await Throws<InvalidOperationException>(() => protocol.VerifyAccountAsync(default), "success without MUSIC_U rejected");
        cookies.Add(new Uri("https://music.163.com"), new Cookie("MUSIC_U", "synthetic-session", "/", ".music.163.com"));
        handler.Reply = "{\"code\":200,\"account\":null,\"profile\":null}";
        await Throws<InvalidOperationException>(() => protocol.VerifyAccountAsync(default), "anonymous account rejected");
        handler.Reply = "{\"code\":200,\"account\":{\"id\":123},\"profile\":{\"userId\":123,\"nickname\":\"测试\"}}";
        Check((await protocol.VerifyAccountAsync(default))["account"]!.Value<int>("id") == 123, "account verified");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Throws<OperationCanceledException>(() => protocol.SendCaptchaAsync("13800000000", "86", cancelled.Token), "cancelled send");

        using var api = new NeteaseApiClient();
        string before = api.GetCurrentCookieString();
        typeof(NeteaseApiClient).GetField("_phoneLogin", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(api, protocol);
        handler.Reply = "{\"code\":502,\"message\":\"验证码错误\"}";
        Check((await api.LoginByCaptchaAsync("13800000000", "123456")).Code == 502 && api.GetCurrentCookieString() == before, "rejected login preserves player session");
        using var inFlightCancellation = new CancellationTokenSource();
        handler.CancelBeforeReply = inFlightCancellation;
        handler.Reply = "{\"code\":200}";
        await Throws<OperationCanceledException>(() => api.LoginByCaptchaAsync("13800000000", "123456", cancellationToken: inFlightCancellation.Token), "cancel after dispatch");
        Check(api.GetCurrentCookieString() == before, "cancelled login preserves player session");
        handler.CancelBeforeReply = null;
        handler.Reply = "{\"code\":200,\"account\":{\"id\":123},\"profile\":{\"userId\":123,\"nickname\":\"测试\"}}";
        Check((await api.LoginByCaptchaAsync("13800000000", "123456")).Code == 200 && api.GetCurrentCookieString().Contains("MUSIC_U=synthetic-session"), "verified login committed");
        await Throws<InvalidOperationException>(() => api.PollWeChatLoginAsync(session), "stale WeChat session rejected");
        handler.Reply = "ok";
        await protocol.CompleteWeChatAsync(session, "synthetic-code", default);
        int calls = handler.Calls;
        await Throws<InvalidOperationException>(() => protocol.CompleteWeChatAsync(session, "synthetic-code", default), "one-use OAuth code");
        Check(calls == handler.Calls, "OAuth exchange not retried");
        var vectorPath = Path.Combine(AppContext.BaseDirectory, "crypto-vector.json");
        var vector = JObject.Parse(File.ReadAllText(vectorPath));
        string json = vector["data"]!.ToString(Newtonsoft.Json.Formatting.None);
        string Aes(string s, string key) => (string)typeof(EncryptionHelper).GetMethod("AesEncrypt", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { s, key, "0102030405060708" })!;
        string encrypted = Aes(Aes(json, "0CoJUm6Qyw8W8jud"), new string('a', 16));
        Check(encrypted == vector["weapi"]!.Value<string>("params"), "AES matches upstream JS UTF-8 vector");
        var t = typeof(EncryptionHelper);
        string rsa = (string)t.GetMethod("RsaEncrypt", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { new string('a', 16),
            t.GetField("WEAPI_PUBLIC_KEY", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!, t.GetField("WEAPI_MODULUS", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()! })!;
        Check(rsa == vector["weapi"]!.Value<string>("encSecKey"), "RSA matches upstream JS vector");
        Check(EncryptionHelper.EncryptEapi("/api/w/login/cellphone", json) == vector["eapi"]!.Value<string>("params"), "EAPI matches upstream JS vector");
        string unicodeSecret = vector.Value<string>("unicodeSecret")!;
        Check(Aes(Aes(vector["unicodeData"]!.ToString(Newtonsoft.Json.Formatting.None), "0CoJUm6Qyw8W8jud"), unicodeSecret) == vector["unicodeWeapi"]!.Value<string>("params"), "UTF-8 nested JSON matches upstream");
        string reversedRsa = (string)t.GetMethod("RsaEncrypt", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { unicodeSecret,
            t.GetField("WEAPI_PUBLIC_KEY", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!, t.GetField("WEAPI_MODULUS", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()! })!;
        Check(reversedRsa == vector["unicodeWeapi"]!.Value<string>("encSecKey"), "RSA key reversal matches upstream");
        for (int i = 0; i < 64; i++)
        {
            var sample = EncryptionHelper.EncryptWeapi("{\"message\":\"登录验证✓\"}");
            Check(sample.EncSecKey.Length == 256 && sample.EncSecKey.All(Uri.IsHexDigit), "fixed RSA size across random keys");
        }
        var ensure = typeof(NeteaseApiClient).GetMethod("EnsurePhoneLoginSession", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var phoneClient = typeof(NeteaseApiClient).GetField("_phoneLogin", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ensure.Invoke(api, new object[] { "13900000000", "86" });
        var fresh = (LoginProtocolClient)phoneClient.GetValue(api)!;
        Check(!ReferenceEquals(fresh, protocol) && !fresh.CookieSnapshot().Contains("MUSIC_U"), "new login after success gets anonymous session");
        ensure.Invoke(api, new object[] { "13900000000", "86" });
        Check(ReferenceEquals(fresh, phoneClient.GetValue(api)), "send and redeem share the same phone session");
        ensure.Invoke(api, new object[] { "13700000000", "86" });
        Check(!ReferenceEquals(fresh, phoneClient.GetValue(api)), "changing phone replaces its login session");
    }

    private static void LiveUi()
    {
        using var api = new NeteaseApiClient(); using var form = new OfflineLoginForm(api);
        form.Show(); var tabs = form.Controls.OfType<TabControl>().Single(); tabs.SelectedIndex = 2;
        var field = typeof(LoginForm).GetField("_weChatPicture", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var picture = (PictureBox)field.GetValue(form)!;
        Check(picture.Image == null && GetField<CancellationTokenSource?>(form, "_interactiveCancellation") == null, "live tab entry waits for explicit loading");
        var load = GetField<Button>(form, "_refreshWeChat");
        load.PerformClick();
        Check(!load.Enabled && GetField<Label>(form, "_weChatStatus").AccessibleName == "正在加载微信登录二维码", "live click announces loading and disables repeat clicks");
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (picture.Image == null && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(25); }
        Check(picture.Image != null, "live form loads WeChat image");
        Check(load.Enabled && load.AccessibleName == "刷新微信登录二维码", "ready QR exposes the refresh action");
        var output = Environment.GetEnvironmentVariable("YTPLAYER_AUTH_OUTPUT");
        if (output != null)
        {
            Directory.CreateDirectory(output); using var bitmap = new System.Drawing.Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height));
            bitmap.Save(Path.Combine(output, "wechat-live.png"));
        }
        tabs.SelectedIndex = 1; Application.DoEvents();
        Check(typeof(LoginForm).GetField("_interactiveCancellation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form) == null, "switch tab cancels live WeChat poll");
        tabs.SelectedIndex = 2; Application.DoEvents();
        Check(picture.Image == null && !picture.Visible && load.Enabled && load.AccessibleName == "加载微信登录二维码", "returning to WeChat requires another explicit load");
        form.Close();
    }

    private static async Task ProbeCode(string phone)
    {
        using var api = new NeteaseApiClient(); string before = api.GetCurrentCookieString();
        var result = await api.LoginByCaptchaAsync(phone, "000000");
        Console.WriteLine("SMS redemption probe business code=" + result.Code);
        if (result.Code != 200) Check(api.GetCurrentCookieString() == before, "real rejected login preserves session");
        else Console.WriteLine("Server accepted the supplied probe code; isolated test account state will be removed.");
    }

    private static async Task LiveSms(string phone)
    {
        using var api = new NeteaseApiClient();
        Check(await api.SendCaptchaAsync(phone), "current official SMS request accepted");
        Console.WriteLine("SMS business code=200; delivery and successful redemption still require the account owner.");
    }

    private static async Task RedeemSms(string phone)
    {
        string code = Environment.GetEnvironmentVariable("YTPLAYER_TEST_SMS_CODE") ?? throw new ArgumentException("SMS code must be supplied via the process environment.");
        Environment.SetEnvironmentVariable("YTPLAYER_TEST_SMS_CODE", null);
        using var api = new NeteaseApiClient();
        var login = await api.LoginByCaptchaAsync(phone, code);
        Console.WriteLine("SMS redemption business code=" + login.Code);
        if (login.Code == 200)
        {
            Check(!string.IsNullOrWhiteSpace(login.UserId) && login.UserId != "0", "real account verified");
            Check(api.GetCurrentCookieString().Contains("MUSIC_U="), "verified credentials committed to isolated player session");
            Console.WriteLine("Real SMS login: credential and account verification passed.");
        }
    }

    private static async Task Live(string[] args)
    {
        using var api = new NeteaseApiClient();
        for (int i = 0; i < 2; i++)
        {
            var session = await api.CreateWeChatLoginAsync();
            byte[] png = await api.GetWeChatQrImageAsync(session);
            using var stream = new MemoryStream(png); using var image = System.Drawing.Image.FromStream(stream);
            Check(image.Width >= 100 && image.Height >= 100, "live WeChat image decoded");
            var poll = await api.PollWeChatLoginAsync(session);
            Check(poll.Status == WeChatLoginStatus.WaitingForScan, "live WeChat waiting state");
            Console.WriteLine($"WeChat attempt={i + 1} image={image.Width}x{image.Height} poll={poll.Code}");
        }
        if (args.Length == 2 && args[0] == "--send-sms")
        {
            bool sent = await api.SendCaptchaAsync(args[1]);
            Check(sent, "live SMS accepted"); Console.WriteLine("SMS business code=200; delivery and redemption require account owner.");
        }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        internal int Calls;
        internal HttpStatusCode Status = HttpStatusCode.OK;
        internal CancellationTokenSource? CancelBeforeReply;
        internal Exception? Failure;
        internal string Path = "", Body = "", Reply = "";
        internal Uri? RequestUri, ResponseUri;
        internal string UserAgent = "", Origin = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls++; Path = request.RequestUri!.AbsolutePath;
            RequestUri = request.RequestUri;
            UserAgent = request.Headers.UserAgent.ToString();
            Origin = request.Headers.TryGetValues("Origin", out var origins) ? origins.Single() : "";
            Body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(token);
            if (Failure != null) throw Failure;
            CancelBeforeReply?.Cancel();
            return new HttpResponseMessage(Status) { Content = new StringContent(Reply, Encoding.UTF8, "application/json"),
                RequestMessage = ResponseUri == null ? request : new HttpRequestMessage(HttpMethod.Get, ResponseUri) };
        }
    }
}
