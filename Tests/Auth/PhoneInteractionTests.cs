using System.Diagnostics;
using System.Reflection;
using System.Windows.Forms;
using YTPlayer.Core;
using YTPlayer.Core.Auth;
using YTPlayer.Forms;

internal static partial class Program
{
    private static void PhoneInteractionTests()
    {
        var previousContext = SynchronizationContext.Current;
        using var uiContext = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(uiContext);
        var unhandled = new List<Exception>();
        ThreadExceptionEventHandler onError = (_, e) => unhandled.Add(e.Exception);
        Application.ThreadException += onError;
        try
        {
            CountdownBounds();
            SendFromFreshForm();
            InvalidPhoneAndRetry();
            SubmitFromFreshForm();
            NetworkFailureAndClose();
            RelatedTimerBounds();
        }
        finally { Application.ThreadException -= onError; SynchronizationContext.SetSynchronizationContext(previousContext); }

        void WaitForCompletion(LoginForm form)
        {
            var timeout = Stopwatch.StartNew();
            while (GetField<bool>(form, "_phoneBusy") && unhandled.Count == 0 && timeout.Elapsed < TimeSpan.FromSeconds(5))
            { Application.DoEvents(); Thread.Sleep(5); }
            Application.DoEvents();
            Check(unhandled.Count == 0, "phone button must not raise a UI exception: " + unhandled.FirstOrDefault()?.GetType().Name);
            Check(!GetField<bool>(form, "_phoneBusy"), "phone button always clears busy state");
        }

        void SendFromFreshForm()
        {
            using var api = new NeteaseApiClient();
            var handler = new FakeHandler { Reply = "{\"code\":200}" };
            using var protocol = new LoginProtocolClient(new System.Net.CookieContainer(), handler, new FakeSecurity());
            typeof(NeteaseApiClient).GetField("_phoneLogin", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(api, protocol);
            using var form = new OfflineLoginForm(api);
            ShowPhonePage(form);
            GetField<TextBox>(form, "_phone").Text = "13800000000";
            var send = GetField<Button>(form, "_sendCaptcha");
            send.PerformClick(); WaitForCompletion(form);
            Check(handler.Calls == 1, "first send button dispatches exactly one mocked SMS request");
            Check(GetField<Label>(form, "_phoneStatus").Text.Contains("验证码已发送"), "successful send reaches status notification");
            Check(!send.Enabled && GetField<System.Windows.Forms.Timer>(form, "_smsTimer").Enabled, "cooldown starts after send");
            send.PerformClick(); Application.DoEvents();
            Check(handler.Calls == 1, "cooldown prevents repeat button dispatch");
            typeof(LoginForm).GetField("_smsCooldownStartedAt", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(form, Stopwatch.GetTimestamp() - 61L * Stopwatch.Frequency);
            typeof(LoginForm).GetMethod("UpdateSmsCountdown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
            Check(send.Enabled && !GetField<System.Windows.Forms.Timer>(form, "_smsTimer").Enabled, "expired cooldown restores send button and stops timer");
            handler.Reply = "{\"code\":400,\"message\":\"发送受限\"}";
            send.PerformClick(); WaitForCompletion(form);
            Check(handler.Calls == 2 && GetField<Label>(form, "_phoneStatus").Text.Contains("发送受限"), "server rejection is handled after cooldown expiry");
            Check(!send.Enabled, "dispatched request retains cooldown on business rejection");
        }

        void InvalidPhoneAndRetry()
        {
            using var api = new NeteaseApiClient();
            var handler = new FakeHandler { Reply = "{\"code\":200}" };
            using var protocol = new LoginProtocolClient(new System.Net.CookieContainer(), handler, new FakeSecurity());
            typeof(NeteaseApiClient).GetField("_phoneLogin", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(api, protocol);
            using var form = new OfflineLoginForm(api);
            ShowPhonePage(form);
            var phone = GetField<TextBox>(form, "_phone"); var send = GetField<Button>(form, "_sendCaptcha");
            phone.Text = "123"; send.PerformClick(); WaitForCompletion(form);
            Check(handler.Calls == 0 && send.Enabled, "invalid phone releases busy state without a request or cooldown");
            Check(GetField<Label>(form, "_phoneStatus").Text.Contains("有效"), "invalid phone keeps validation message");
            phone.Text = "13800000000"; send.PerformClick(); WaitForCompletion(form);
            Check(handler.Calls == 1, "corrected phone can send immediately");
        }

        void SubmitFromFreshForm()
        {
            using var api = new NeteaseApiClient();
            var handler = new FakeHandler { Reply = "{\"code\":502,\"message\":\"验证码错误\"}" };
            using var protocol = new LoginProtocolClient(new System.Net.CookieContainer(), handler, new FakeSecurity());
            typeof(NeteaseApiClient).GetField("_phoneLogin", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(api, protocol);
            using var form = new OfflineLoginForm(api);
            ShowPhonePage(form);
            GetField<TextBox>(form, "_phone").Text = "13800000000";
            GetField<Button>(form, "_phoneSubmit").PerformClick(); WaitForCompletion(form);
            Check(handler.Calls == 0 && GetField<Button>(form, "_sendCaptcha").Enabled, "missing code before first send restores controls");
            GetField<TextBox>(form, "_captcha").Text = "123456";
            GetField<Button>(form, "_phoneSubmit").PerformClick(); WaitForCompletion(form);
            Check(handler.Calls == 1 && GetField<Label>(form, "_phoneStatus").Text.Contains("验证码错误"), "rejected login before first send remains recoverable");
            Check(GetField<Button>(form, "_sendCaptcha").Enabled, "login rejection does not create SMS cooldown");
        }

        void NetworkFailureAndClose()
        {
            using var api = new NeteaseApiClient();
            var handler = new FakeHandler { Failure = new HttpRequestException("模拟网络故障") };
            using var protocol = new LoginProtocolClient(new System.Net.CookieContainer(), handler, new FakeSecurity());
            typeof(NeteaseApiClient).GetField("_phoneLogin", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(api, protocol);
            using var form = new OfflineLoginForm(api);
            ShowPhonePage(form);
            GetField<TextBox>(form, "_phone").Text = "13800000000";
            GetField<Button>(form, "_sendCaptcha").PerformClick(); WaitForCompletion(form);
            Check(handler.Calls == 1 && GetField<Label>(form, "_phoneStatus").Text.Contains("模拟网络故障"), "network error stays in phone status");
            Check(!GetField<Button>(form, "_sendCaptcha").Enabled && GetField<TextBox>(form, "_phone").Enabled, "network failure releases inputs but retains dispatch cooldown");
            form.Close(); Application.DoEvents();
            Check(unhandled.Count == 0, "closing with an active cooldown has no UI exception");
        }
    }

    private static void CountdownBounds()
    {
        foreach (var (elapsed, expected) in new[] {
            (TimeSpan.Zero, 60), (TimeSpan.FromTicks(1), 60),
            (TimeSpan.FromSeconds(59.5), 1), (TimeSpan.FromSeconds(60), 0),
            (TimeSpan.FromSeconds(61), 0), (TimeSpan.MaxValue, 0),
            (TimeSpan.FromTicks(-1), 60), (TimeSpan.FromDays(-36500), 60), (TimeSpan.MinValue, 60) })
            Check(LoginForm.CalculateSmsRemainingSeconds(elapsed) == expected, "countdown range/rounding at " + elapsed);
        foreach (var (elapsed, expected) in new[] {
            (TimeSpan.Zero, 200), (TimeSpan.FromMilliseconds(0.5), 200),
            (TimeSpan.FromMilliseconds(199.5), 1), (TimeSpan.FromMilliseconds(200), 0),
            (TimeSpan.MaxValue, 0), (TimeSpan.MinValue, 200), (TimeSpan.FromDays(-100), 200) })
            Check(YTPlayer.MainForm.GetPlayRequestDelayMs(elapsed) == expected, "play request throttle bounds at " + elapsed);
    }

    private static void RelatedTimerBounds()
    {
        using var tree = new CommentTreeView();
        var type = typeof(CommentTreeView);
        var tick = typeof(System.Windows.Forms.Timer).GetMethod("OnTick", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var (deadlineField, timerField) in new[] {
            ("_suppressControlRoleUntil", "_restoreControlRoleTimer"),
            ("_suppressAccessibleTextUntil", "_restoreAccessibleTextTimer") })
        {
            var deadline = type.GetField(deadlineField, BindingFlags.Instance | BindingFlags.NonPublic)!;
            var timer = (System.Windows.Forms.Timer)type.GetField(timerField, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tree)!;
            deadline.SetValue(tree, DateTime.MaxValue);
            tick.Invoke(timer, new object[] { EventArgs.Empty });
            Check(timer.Interval == 1000 && timer.Enabled, "accessibility restore timer bounds distant deadline: " + timerField);
            deadline.SetValue(tree, DateTime.MinValue);
            tick.Invoke(timer, new object[] { EventArgs.Empty });
            Check(!timer.Enabled, "accessibility restore timer stops at expired deadline: " + timerField);
        }
    }

    private static T GetField<T>(LoginForm form, string name) => (T)typeof(LoginForm)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;

    private static void ShowPhonePage(LoginForm form)
    { form.Show(); form.Controls.OfType<TabControl>().Single().SelectedIndex = 1; Application.DoEvents(); }
}
