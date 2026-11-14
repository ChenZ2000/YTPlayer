using System.Reflection;
using YTPlayer.Core;
using YTPlayer.Forms;

internal static partial class Program
{
    private static void WeChatInteractionTests()
    {
        var previousContext = SynchronizationContext.Current;
        using var uiContext = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(uiContext);
        var unhandled = new List<Exception>();
        ThreadExceptionEventHandler onError = (_, e) => unhandled.Add(e.Exception);
        Application.ThreadException += onError;
        try
        {
            using var api = new NeteaseApiClient();
            using var form = new OfflineLoginForm(api);
            form.Show();
            var tabs = form.Controls.OfType<TabControl>().Single();
            tabs.SelectedIndex = 2; Application.DoEvents();
            var load = GetField<Button>(form, "_refreshWeChat");
            var status = GetField<Label>(form, "_weChatStatus");
            var picture = GetField<PictureBox>(form, "_weChatPicture");
            Check(GetField<CancellationTokenSource?>(form, "_interactiveCancellation") == null, "WeChat tab entry starts no login operation");
            Check(load.Enabled && load.AccessibilityObject.Name == "加载微信登录二维码" && load.AccessibilityObject.Role == AccessibleRole.PushButton, "initial load action is accessible and enabled");
            Check(picture.Image == null && !picture.Visible && status.Text.Contains("点击"), "idle WeChat page shows instructions instead of a stale QR");

            // Hold the existing API gate to simulate a slow startup without opening a browser or
            // making a request. This exercises the actual async click and cancellation lifecycle.
            var gate = (SemaphoreSlim)typeof(NeteaseApiClient).GetField("_weChatLoginGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(api)!;
            gate.Wait();
            try
            {
                load.PerformClick();
                var first = GetField<CancellationTokenSource>(form, "_interactiveCancellation");
                var firstToken = first.Token;
                Check(!load.Enabled && GetField<bool>(form, "_weChatLoading"), "click disables loading before async startup");
                Check(status.Text == "正在加载微信登录二维码" && status.AccessibilityObject.Name == status.Text, "loading status is available to the screen reader immediately");
                load.PerformClick();
                var duplicate = (Task)typeof(LoginForm).GetMethod("LoadWeChatAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null)!;
                Check(duplicate.IsCompletedSuccessfully && ReferenceEquals(first, GetField<CancellationTokenSource>(form, "_interactiveCancellation")), "repeat activation does not cancel or restart loading");
                Application.DoEvents();
                tabs.SelectedIndex = 1; Application.DoEvents();
                Check(firstToken.IsCancellationRequested, "leaving WeChat cancels pending loading");
                tabs.SelectedIndex = 2; Application.DoEvents();
                Check(load.Enabled && !GetField<bool>(form, "_weChatLoading") && load.AccessibleName == "加载微信登录二维码", "return restores a usable manual load button");
                Check(GetField<CancellationTokenSource?>(form, "_interactiveCancellation") == null && status.Text.Contains("点击"), "return does not restart or leave a loading announcement");
            }
            finally { gate.Release(); Application.DoEvents(); }
            Check(typeof(NeteaseApiClient).GetField("_weChatLogin", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(api) == null, "cancelled or duplicate clicks never reached the network client");
            form.Close(); Application.DoEvents();
            Check(unhandled.Count == 0, "WeChat button and tab switching raise no UI exception");
        }
        finally { Application.ThreadException -= onError; SynchronizationContext.SetSynchronizationContext(previousContext); }
    }
}
