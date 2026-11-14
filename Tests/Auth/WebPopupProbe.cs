using Microsoft.Web.WebView2.WinForms;
using YTPlayer.Core;
using YTPlayer.Forms;

internal static partial class Program
{
    private static void WebPopupProbe()
    {
        using var api = new NeteaseApiClient();
        using var form = new LoginForm(api);
        form.Show();
        form.Controls.OfType<TabControl>().Single().SelectedIndex = 3;
        var view = GetField<WebView2>(form, "webLoginView");
        try { PumpUntil(() => view.CoreWebView2 != null && view.Source?.AbsolutePath == "/", "official web page initialization"); }
        catch (TimeoutException)
        {
            Console.WriteLine("WebView initialized=" + (view.CoreWebView2 != null) + "; formInitialized=" + GetField<bool>(form, "_webLoginInitialized") + "; visible=" + view.Visible + "; sourcePath=" + view.Source?.AbsolutePath);
            Console.WriteLine("WebView status=" + GetField<Label>(form, "webStatusLabel").Text);
            throw;
        }
        var core = view.CoreWebView2;
        var probe = core.CookieManager.CreateCookie("ytplayer_test_cookie", "synthetic", ".music.163.com", "/");
        core.CookieManager.AddOrUpdateCookie(probe);
        PumpTask(core.ExecuteScriptAsync("window.open('about:blank', '_blank');"));
        var popups = GetField<List<Form>>(form, "_officialLoginPopups");
        PumpUntil(() => popups.Count == 1 && popups[0].Controls.OfType<WebView2>().Single().CoreWebView2 != null, "official OAuth popup creation");
        var popup = popups[0];
        var child = popup.Controls.OfType<WebView2>().Single().CoreWebView2;
        var list = PumpTask(child.CookieManager.GetCookiesAsync("https://music.163.com/"));
        Check(list.Any(x => x.Name == "ytplayer_test_cookie" && x.Value == "synthetic"), "official popup shares parent's cookie profile");
        Check(child.BrowserProcessId == core.BrowserProcessId, "official popup shares browser process");
        PumpUntil(() => PumpTask(child.ExecuteScriptAsync("window.opener !== null")) == "true", "official opener relationship");
        Check(PumpTask(child.ExecuteScriptAsync("window.opener !== null")) == "true", "OAuth popup preserves opener");
        popup.Close(); Application.DoEvents();
        Check(popups.Count == 0, "closed official popup releases its window");
        form.Close();
        Console.WriteLine("Official WebView popup: shared cookie profile, preserved opener, closed cleanly; no login submitted.");
    }

    private static void PumpUntil(Func<bool> done, string name)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!done())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(45)) throw new TimeoutException(name);
            Application.DoEvents(); Thread.Sleep(20);
        }
    }

    private static T PumpTask<T>(Task<T> task)
    {
        PumpUntil(() => task.IsCompleted, "browser operation");
        return task.GetAwaiter().GetResult();
    }
}
