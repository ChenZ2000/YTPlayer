using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.WinForms;
using Newtonsoft.Json.Linq;
using YTPlayer.Forms;
using YTPlayer.Models;
using YTPlayer.Update;
using YTPlayer.Utils;

internal static class Program
{
    private static int _assertions;
    private static readonly string Output = Path.Combine(RepositoryRoot(), "artifacts", "document-view");

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "YTPlayer.csproj"))) return directory.FullName;
        throw new DirectoryNotFoundException("Run the test executable from the repository build output.");
    }

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Trace.Listeners.Add(new ConsoleTraceListener());
        Directory.CreateDirectory(Output);
        string root = Path.Combine(Output, "runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("YTPLAYER_ROOT", root);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetDefaultFont(new Font("Microsoft YaHei UI", 10F));
        ThemeManager.Initialize();
        if (args.Length == 0 || args.Contains("--preview"))
        {
            using var preview = CreatePreview();
            preview.Show();
            preview.Hide();
            Application.Run(preview);
            return 0;
        }
        int result = 0;
        using var runner = new Form { Text = "文档组件自动验证", Size = new Size(640, 480), ShowInTaskbar = false };
        Application.ThreadException += (_, e) => { Console.Error.WriteLine(e.Exception); result = 1; runner.Close(); };
        runner.Shown += async (_, _) =>
        {
            try
            {
                await RunAsync(runner);
                Console.WriteLine($"PASS: {_assertions} assertions");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); result = 1; }
            finally { runner.Close(); }
        };
        Application.Run(runner);
        return result;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + message);
        _assertions++;
    }

    private static IEnumerable<Control> Descendants(Control control) => control.Controls.Cast<Control>()
        .SelectMany(child => new[] { child }.Concat(Descendants(child)));
    private static AccessibleDocumentView View(Form form) => Descendants(form).OfType<AccessibleDocumentView>().Single();
    private static WebView2 Browser(AccessibleDocumentView view) => view.Controls.OfType<WebView2>().Single();
    private static async Task WaitAsync(Func<bool> condition, string description, int milliseconds = 25000)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > milliseconds) throw new TimeoutException(description);
            await Task.Delay(20);
        }
    }
    private static async Task<JToken> Evaluate(AccessibleDocumentView view, string script) =>
        JToken.Parse(await Browser(view).CoreWebView2.ExecuteScriptAsync(script));

    private static SemanticDocument UserFixture() => UserInfoForm.BuildUserDocument(new UserAccountInfo
    {
        UserId = 12345, Nickname = "测试用户 <独立样例>", ArtistName = "测试艺人", ArtistId = 456,
        UserType = 4, AuthTypeDesc = "认证音乐人", VipType = 11, Level = 9, Gender = 1,
        Signature = "热爱音乐，也热爱无障碍。\n第二行个性签名。<script>window.injected=true</script>",
        Follows = 12, Followers = 34, EventCount = 56, ListenSongs = 789, PlaylistCount = 3,
        PlaylistBeSubscribedCount = 4, DjProgramCount = 5, Birthday = new DateTime(1995, 3, 6),
        CreateTime = new DateTime(2020, 2, 3, 4, 5, 6), CreateDays = 2000
    });

    private static SemanticDocument StressFixture(int revision = 0)
    {
        var builder = new SemanticDocumentBuilder("无障碍文档测试").Heading("阅读说明")
            .Paragraph("Tab 进入文档后，可用 H 阅读标题、L 阅读列表、T 阅读表格。再次 Tab 到下一个原生按钮。")
            .List(new[] { "第一项内容", "第二项内容", "示例链接：https://example.com/" })
            .Heading("快捷键示例").Table("示例快捷键", new[] { "功能", "快捷键", "说明" },
                new[] { new[] { "播放或暂停", "Space", "控制当前播放状态" }, new[] { "打开菜单", "Alt", "进入菜单栏" } }, 1);
        for (int i = 0; i < 30; i++) builder.Heading("章节 " + (i + 1)).Paragraph("第 " + (i + 1) + " 节正文，用于验证长文档滚动和缩放。版本 " + revision);
        return builder.Build();
    }

    private static void Export(string name, SemanticDocument document) => File.WriteAllText(Path.Combine(Output, name + ".html"),
        SemanticDocumentBuilder.Wrap(document, ThemeManager.Current, 10), new UTF8Encoding(false));

    private static async Task RunAsync(Form runner)
    {
        var user = UserFixture();
        Export("user-info", user);
        Check(Regex.Matches(user.BodyHtml, "<h2 ").Count == 5, "all user sections retained");
        foreach (string value in new[] { "12345", "456", "认证音乐人", "黑胶VIP", "Lv.9", "789", "1995-03-06", "2020-02-03 04:05:06", "2000", "第二行个性签名" })
            Check(user.PlainText.Contains(value), "user field: " + value);
        Check(!user.BodyHtml.Contains("<script>"), "profile HTML encoded");
        var literal = new SemanticDocumentBuilder("原样文本").Paragraph("**签名** [原样](https://example.com/)").Build();
        Check(literal.BodyHtml.Contains("**签名** [原样](https://example.com/)"), "profile text is not interpreted as Markdown");
        var blank = UserInfoForm.BuildUserDocument(new UserAccountInfo());
        Check(blank.PlainText.Contains("生日：未设置") && blank.PlainText.Contains("注册时间：未知"), "empty dates explained");
        var unsafeDoc = new SemanticDocumentBuilder("<title>").Markdown("### 分组\n- [恶意](javascript:alert)\n- [本地](file:///C:/secret)\n- [安全](https://example.com/?x=1&y=2)\n\n<img src=x onerror=alert(1)>").Build();
        Check(!unsafeDoc.BodyHtml.Contains("href=\"javascript:") && !unsafeDoc.BodyHtml.Contains("href=\"file:"), "unsafe schemes suppressed");
        Check(!unsafeDoc.BodyHtml.Contains("<img ") && unsafeDoc.BodyHtml.Contains("&lt;img"), "raw markup encoded");
        Check(unsafeDoc.BodyHtml.Contains("<h2 ") && unsafeDoc.BodyHtml.Contains("<ul>"), "normalized headings and lists");
        Export("untrusted-input", unsafeDoc);

        using var about = new AboutDialog();
        using var shortcuts = new KeyboardShortcutsDialog();
        using var artist = new ArtistDetailDialog(new ArtistDetail { Name = "示例艺人", Description = "完整介绍第一段。", BriefDesc = "艺人简介。",
            Introductions = new() { new() { Title = "音乐历程", Content = "第一行。\n第二行。" }, new() { Title = "获奖经历", Content = "示例奖项。" } } });
        using var update = new UpdateAvailablePromptDialog(new UpdatePlan { TargetVersion = "99.0.0", AssetName = "sample.zip", AssetSize = 123456,
            ReleaseTitle = "示例更新", ReleasePage = "https://example.com/releases", PublishedAt = new DateTime(2026, 1, 2, 3, 4, 5) });
        using var checkUpdate = new UpdateCheckDialog();
        typeof(UpdateCheckDialog).GetMethod("SetResultText", BindingFlags.NonPublic | BindingFlags.Instance, new[] { typeof(string[]) })!
            .Invoke(checkUpdate, new object[] { new[] { "当前版本：1.0", "您使用的是最新版本！" } });
        Export("update-check", View(checkUpdate).Document!);
        Check(View(checkUpdate).Document!.BodyHtml.Contains("<ul>"), "update check semantic result");
        foreach (var (name, form) in new[] { ("about", (Form)about), ("shortcuts", shortcuts), ("artist", artist), ("update", update) })
        {
            var view = View(form);
            Check(view.Controls.OfType<WebView2>().Count() == 0, name + " lazy renderer before showing");
            Export(name, view.Document!);
            var watch = Stopwatch.StartNew();
            form.Show(runner);
            await WaitAsync(() => view.IsDocumentReady, name + " renderer ready");
            Console.WriteLine($"{name} ready: {watch.ElapsedMilliseconds} ms");
            Check((int)(await Evaluate(view, "document.querySelectorAll('h1').length")) == 1, name + " one h1");
            Check((string?)(await Evaluate(view, "document.documentElement.lang")) == "zh-CN", name + " language");
            Check((int)(await Evaluate(view, "document.querySelectorAll('a:not([tabindex=\"-1\"])').length")) == 0, name + " links do not trap Tab");
            if (name == "shortcuts")
            {
                string[] shortcutLines = DocumentationLoader.ReadMarkdown("Docs.KeyboardShortcuts.md").Split('\n');
                int expected = shortcutLines.Count(line => line.TrimStart().StartsWith("- ") && line.Contains('|'));
                Check((int)(await Evaluate(view, "document.querySelectorAll('tbody tr').length")) == expected, "every shortcut preserved");
                int expectedGroups = shortcutLines.Count(line => line.TrimStart().StartsWith("### "));
                Check((int)(await Evaluate(view, "document.querySelectorAll('table').length")) == expectedGroups, "every shortcut group has a table");
                Check((int)(await Evaluate(view, "document.querySelectorAll('th:not([scope])').length")) == 0, "all table headers scoped");
            }
            view.FocusDocument();
            await WaitAsync(() => view.ContainsFocus, name + " focus inside");
            await Evaluate(view, "document.dispatchEvent(new KeyboardEvent('keydown',{key:'Tab',bubbles:true,cancelable:true}))");
            await WaitAsync(() => !view.ContainsFocus, name + " forward Tab out", 2000);
            Check(Descendants(form).OfType<Button>().Any(b => b.ContainsFocus), name + " Tab reaches native button");
            var firstButton = Descendants(form).OfType<Button>().Single(b => b.ContainsFocus);
            firstButton.Focus();
            form.SelectNextControl(firstButton, false, true, true, true);
            await WaitAsync(() => view.ContainsFocus, name + " reverse native Tab in", 2000);
            form.Hide();
        }

        using var focusForm = new Form { Text = "焦点和生命周期验证", Size = new Size(640, 480) };
        var before = new Button { Text = "前一个组件", Dock = DockStyle.Top, TabIndex = 0 };
        var content = new AccessibleDocumentView { Dock = DockStyle.Fill, TabIndex = 1 };
        var after = new Button { Text = "后一个组件", Dock = DockStyle.Bottom, TabIndex = 2 };
        focusForm.Controls.Add(content); focusForm.Controls.Add(after); focusForm.Controls.Add(before);
        content.SetDocument(StressFixture()); focusForm.Show(runner); before.Focus();
        await WaitAsync(() => content.IsDocumentReady, "focus fixture ready");
        Check(before.ContainsFocus, "async load does not steal external focus");
        var browser = Browser(content);
        Check(browser.CoreWebView2.BrowserProcessId == Browser(View(about)).CoreWebView2.BrowserProcessId, "dialogs share browser process");
        Check(ReferenceEquals(DocumentBrowserEnvironment.GetAsync(), DocumentBrowserEnvironment.GetAsync()), "environment initialization cached");
        Check(browser.CoreWebView2.Profile.IsInPrivateModeEnabled, "document profile is private");
        Check(browser.CoreWebView2.Profile.ProfileName == "ReadOnlyDocuments", "separate document profile");
        int remoteRequests = 0;
        browser.CoreWebView2.WebResourceRequested += (_, e) =>
        {
            if (e.Request.Uri.StartsWith("http", StringComparison.OrdinalIgnoreCase)) remoteRequests++;
        };
        await Evaluate(content, "fetch('https://example.com/document-view-test').then(()=>window.fetchResult='allowed').catch(()=>window.fetchResult='blocked');null");
        for (int i = 0; i < 100 && (string?)(await Evaluate(content, "window.fetchResult")) != "blocked"; i++) await Task.Delay(20);
        Check((string?)(await Evaluate(content, "window.fetchResult")) == "blocked" && remoteRequests == 0, "CSP blocks remote fetch before network request");
        content.SetDocument(user);
        for (int i = 0; i < 100 && !(bool)(await Evaluate(content, "document.body.innerText.includes('第二行个性签名')")); i++) await Task.Delay(20);
        Check((bool)(await Evaluate(content, "typeof window.injected === 'undefined' && document.querySelectorAll('script').length === 1")), "profile markup cannot execute in renderer");
        content.SetDocument(StressFixture());
        for (int i = 0; i < 100 && !(bool)(await Evaluate(content, "document.body.innerText.includes('章节 30')")); i++) await Task.Delay(20);
        focusForm.SelectNextControl(before, true, true, true, true);
        await WaitAsync(() => content.ContainsFocus, "native forward Tab in", 2000);
        await Evaluate(content, "document.dispatchEvent(new KeyboardEvent('keydown',{key:'Tab',shiftKey:true,bubbles:true,cancelable:true}))");
        await WaitAsync(() => before.ContainsFocus, "reverse Tab out", 2000);
        content.FocusDocument();
        await Evaluate(content, "document.dispatchEvent(new KeyboardEvent('keydown',{key:'Tab',bubbles:true,cancelable:true}))");
        await WaitAsync(() => after.ContainsFocus, "forward Tab out", 2000);
        Check(true, "both Tab directions return to correct adjacent native controls");
        int navigations = 0;
        browser.CoreWebView2.NavigationStarting += (_, _) => navigations++;
        content.FocusDocument();
        await Evaluate(content, "scrollTo(0,500)");
        await Task.Delay(100);
        for (int i = 1; i <= 40; i++) content.SetDocument(StressFixture(i));
        for (int i = 0; i < 100 && !(bool)(await Evaluate(content, "document.body.innerText.includes('版本 40')")); i++) await Task.Delay(20);
        Check((bool)(await Evaluate(content, "document.body.innerText.includes('版本 40')")), "rapid updates keep final version");
        Check(navigations == 0 && content.ContainsFocus, "updates preserve browser and focus");
        Check((int)(await Evaluate(content, "Math.round(scrollY)")) == 500, "updates preserve scroll position");
        var oldTheme = ThemeManager.SelectedScheme;
        ThemeManager.SetScheme(ThemeManager.AvailablePalettes.Last().Scheme);
        await Task.Delay(100);
        Check((string?)(await Evaluate(content, "document.getElementById('theme').textContent")) == SemanticDocumentBuilder.ThemeCss(ThemeManager.Current, content.Font.SizeInPoints), "theme changes update existing document");
        ThemeManager.SetScheme(oldTheme);
        ThemeManager.HighContrastOverride = true;
        try
        {
            ThemeManager.ApplyTheme(focusForm);
            await Task.Delay(100);
            Check((string?)(await Evaluate(content, "document.getElementById('theme').textContent")) == SemanticDocumentBuilder.ThemeCss(ThemeManager.Current, content.Font.SizeInPoints), "high contrast palette applied");
        }
        finally { ThemeManager.HighContrastOverride = null; ThemeManager.ApplyTheme(focusForm); }
        Check((int)(await Evaluate(content, "document.documentElement.scrollWidth <= innerWidth ? 1 : 0")) == 1, "content wraps without horizontal scrolling");
        browser.ZoomFactor = 2;
        await Task.Delay(100);
        Check((int)(await Evaluate(content, "document.documentElement.scrollWidth <= innerWidth ? 1 : 0")) == 1, "200 percent zoom reflows");
        browser.ZoomFactor = 1;
        await Evaluate(content, "document.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true,cancelable:true}))");
        await WaitAsync(() => focusForm.IsDisposed, "Escape closes containing window", 2000);
        Check(browser.IsDisposed, "closing form disposes browser");
        foreach (var form in new Form[] { about, shortcuts, artist, update }) form.Close();
        for (int i = 0; i < 3; i++)
        {
            await Task.Delay(200);
            using var reopened = new AboutDialog();
            var watch = Stopwatch.StartNew();
            reopened.Show(runner);
            await WaitAsync(() => View(reopened).IsDocumentReady, "reopen after all documents closed");
            Console.WriteLine($"reopen {i + 1} ready: {watch.ElapsedMilliseconds} ms");
            Check(View(reopened).IsDocumentReady, "reuse environment after last controller disposed " + i);
            reopened.Close();
        }
        using (var loadingForm = new Form())
        {
            var loadingView = new AccessibleDocumentView { Dock = DockStyle.Fill };
            loadingForm.Controls.Add(loadingView);
            loadingView.SetDocument(user); loadingForm.Show(runner); loadingView.FocusDocument();
            await WaitAsync(() => loadingView.IsDocumentReady, "focused document loading");
            Check(Browser(loadingView).ContainsFocus, "focused loading text transfers focus to HTML");
            loadingForm.Close();
        }
        for (int i = 0; i < 5; i++)
        {
            using var early = new Form();
            var earlyView = new AccessibleDocumentView { Dock = DockStyle.Fill };
            early.Controls.Add(earlyView); earlyView.SetDocument(user);
            early.Show(runner); early.Close();
            await Task.Delay(40);
            Check(earlyView.IsDisposed, "close during initialization " + i);
        }
        using var fallbackForm = new Form();
        var fallback = new AccessibleDocumentView { Dock = DockStyle.Fill };
        fallbackForm.Controls.Add(fallback); fallback.SetDocument(user);
        typeof(AccessibleDocumentView).GetMethod("ShowFallback", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(fallback, null);
        fallbackForm.Show(runner);
        Check(fallback.Controls.OfType<TextBox>().Single().Visible && !fallback.IsDocumentReady, "browser failure retains readable text");
        Check(fallback.Controls.OfType<TextBox>().Single().Text.Contains("2000"), "fallback preserves profile fields");
        fallbackForm.Close();
    }

    private static Form CreatePreview()
    {
        var form = new Form { Text = "易听 — 文档无障碍验收", Size = new Size(800, 680), StartPosition = FormStartPosition.CenterScreen };
        var tabs = new TabControl { Dock = DockStyle.Fill, TabIndex = 1 };
        var before = new Button { Text = "文档前的按钮", Dock = DockStyle.Top, TabIndex = 0 };
        var after = new Button { Text = "关闭", Dock = DockStyle.Bottom, TabIndex = 2 };
        using var shortcuts = new KeyboardShortcutsDialog();
        using var about = new AboutDialog();
        foreach (var doc in new[] { UserFixture(), View(shortcuts).Document!, View(about).Document!, StressFixture() })
        {
            var page = new TabPage(doc.Title);
            var view = new AccessibleDocumentView { Dock = DockStyle.Fill, TabIndex = 0 };
            view.SetDocument(doc); page.Controls.Add(view); tabs.TabPages.Add(page);
        }
        form.Controls.Add(tabs); form.Controls.Add(after); form.Controls.Add(before);
        after.Click += (_, _) => form.Close();
        form.CancelButton = after;
        ThemeManager.ApplyTheme(form);
        form.Shown += (_, _) => before.Focus();
        return form;
    }
}
