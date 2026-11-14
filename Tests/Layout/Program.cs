using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using NUnit.Framework;
using NUnitLite;

internal static class Program
{
    internal static readonly string Output = Path.GetFullPath(Environment.GetEnvironmentVariable("YTPLAYER_LAYOUT_OUTPUT") ?? "artifacts/layout/current");
    [STAThread]
    private static int Main(string[] args)
    {
        Directory.CreateDirectory(Output);
        Environment.SetEnvironmentVariable("YTPLAYER_ROOT", Path.Combine(Output, "profile"));
        bool unaware = Environment.GetEnvironmentVariable("YTPLAYER_LAYOUT_DPI") == "96";
#if NET48
        if (unaware) SetProcessDpiAwarenessContext(new IntPtr(-1));
#else
        Application.SetHighDpiMode(unaware ? HighDpiMode.DpiUnaware : HighDpiMode.PerMonitorV2);
        Application.SetDefaultFont(new Font("Microsoft YaHei UI", 10.5f));
#endif
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "nvdaControllerClient64.dll")))
            Console.WriteLine("NVDA controller status (0 = running): " + NvdaTestIfRunning());
        if (args.Contains("--preview"))
        {
            using Form form = LayoutTests.CreateUpdater();
            form.Show();
            form.Hide();
            Application.Run(form);
            return 0;
        }
        return new AutoRun().Execute(args);
    }
    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("nvdaControllerClient64.dll", EntryPoint = "nvdaController_testIfRunning")]
    private static extern int NvdaTestIfRunning();
}

[TestFixture, Apartment(ApartmentState.STA)]
public class LayoutTests
{
    internal static Form CreateUpdater()
    {
        var assembly = Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "YTPlayer.Updater.exe"));
        string session = Path.Combine(Program.Output, "sessions", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(session);
        string planPath = Path.Combine(session, "plan.json");
        File.WriteAllText(planPath, "{\"TargetVersion\":\"2.0.0\"}", new UTF8Encoding(false));
        var plan = Activator.CreateInstance(assembly.GetType("YTPlayer.Update.UpdatePlan", true)!);
        plan!.GetType().GetProperty("TargetVersion")!.SetValue(plan, "2.0.0");
        var optionsType = assembly.GetType("YTPlayer.Updater.UpdaterOptions", true)!;
        var options = optionsType.GetMethod("Parse")!.Invoke(null, new object[] { new[] { "--plan", planPath, "--target", Program.Output } });
        return (Form)Activator.CreateInstance(assembly.GetType("YTPlayer.Updater.UpdaterForm", true)!, plan, options)!;
    }

#if NET48
    private sealed class RecordedProgress : IProgress<YTPlayer.Update.UpdateDownloadProgress>
    {
        public readonly List<YTPlayer.Update.UpdateDownloadProgress> Values = new();
        public void Report(YTPlayer.Update.UpdateDownloadProgress value) => Values.Add(value);
    }

    [Test]
    public void DownloadProgressDoesNotFloodUiQueue()
    {
        var recorder = new RecordedProgress();
        var assembly = typeof(YTPlayer.Update.UpdateDownloadProgress).Assembly;
        var reporter = (IProgress<YTPlayer.Update.UpdateDownloadProgress>)Activator.CreateInstance(
            assembly.GetType("YTPlayer.Updater.UpdaterForm+DownloadProgressReporter", true)!, recorder)!;
        for (int i = 0; i <= 5000; i++) reporter.Report(new YTPlayer.Update.UpdateDownloadProgress(i, 5000));
        Assert.That(recorder.Values.Count, Is.LessThan(50), "buffer callbacks must not enqueue thousands of UI layouts");
        Assert.That(recorder.Values.First().BytesReceived, Is.EqualTo(0));
        Assert.That(recorder.Values.Last().BytesReceived, Is.EqualTo(5000), "final completion must always be delivered");
        TestContext.WriteLine($"5001 buffer reports -> {recorder.Values.Count} UI reports");
    }

    [TestCase(1f), TestCase(1.25f), TestCase(1.5f), TestCase(2f), TestCase(3f)]
    public void UpdaterGeometry(float scale)
    {
        using Form form = CreateUpdater();
        form.Show(); // Missing download URL deterministically enters the failure state before any I/O.
        Application.DoEvents();
        if (scale != 1f)
        {
            form.Font = new Font(form.Font.FontFamily, form.Font.SizeInPoints * scale);
            form.PerformAutoScale();
        }
        form.PerformLayout();
        Capture(form, "Updater-" + scale);
        CheckGeometry(form);
        form.Close();
    }

    [Test]
    public void UpdaterStatesAndNarrowViewport()
    {
        using Form form = CreateUpdater();
        form.Show();
        Application.DoEvents();
        form.Size = form.MinimumSize;
        var controls = Descendants(form).Where(c => c.Name.Length > 0).ToDictionary(c => c.Name, c => c, StringComparer.Ordinal);
        void Invoke(string method, params object[] args) => form.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, args);
        string reason = string.Concat(Enumerable.Repeat("下载路径较长时仍应完整显示错误说明。", 20));
        Invoke("ShowFailedState", reason);
        Assert.That(controls["statusLabel"].Text, Does.Contain(reason));
        Assert.That(controls["statusLabel"].AccessibilityObject.Name, Is.EqualTo(controls["statusLabel"].Text));
        CheckGeometry(form);
        Capture(form, "Updater-long-error-minimum");
        var scrollable = (ScrollableControl)controls["statusLabel"].Parent!;
        Assert.That(scrollable.VerticalScroll.Visible, Is.True, "long content must expose a usable scroll range");
        scrollable.ScrollControlIntoView(controls["cancelButton"].Parent!);
        Assert.That(scrollable.ClientRectangle.Contains(controls["cancelButton"].Parent!.Bounds), Is.True, "footer remains reachable by scrolling");
        Capture(form, "Updater-long-error-footer");
        Invoke("ShowCancelledState");
        Assert.That(controls["resumeButton"].Visible && controls["resumeButton"].Enabled, Is.True);
        controls["logListBox"].Focus();
        Assert.That(controls["logListBox"].Focused, Is.True);
        Assert.That(controls["logListBox"].AccessibilityObject.Role, Is.EqualTo(AccessibleRole.List));
        form.SelectNextControl(controls["logListBox"], true, true, true, true);
        Assert.That(controls["cancelButton"].Focused, Is.True, "Log -> close tab order");
        form.SelectNextControl(controls["cancelButton"], true, true, true, true);
        Assert.That(controls["resumeButton"].Focused, Is.True, "Close -> resume tab order");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 200; i++) Invoke("UpdateStage", "正在下载更新包：" + i + " MB / 200 MB");
        TestContext.WriteLine($"200 status layouts: {watch.ElapsedMilliseconds} ms");
        Invoke("UpdateProgress", 55);
        Assert.That((int)controls["progressBar"].GetType().GetProperty("Value")!.GetValue(controls["progressBar"])!, Is.EqualTo(55));
        CheckGeometry(form);
        typeof(Form).GetMethod("ProcessDialogKey", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, new object[] { Keys.Escape });
        Assert.That(form.Visible, Is.False);
    }
#endif

    [TestCase(MessageBoxButtons.OK), TestCase(MessageBoxButtons.OKCancel), TestCase(MessageBoxButtons.YesNoCancel), TestCase(MessageBoxButtons.AbortRetryIgnore)]
    public void MessageContentAndButtonsRemainAccessible(MessageBoxButtons buttons)
    {
#if NET48
        var assembly = Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "YTPlayer.Updater.exe"));
#else
        var assembly = typeof(YTPlayer.Forms.UpdateCheckDialog).Assembly;
#endif
        string message = string.Join(Environment.NewLine, Enumerable.Repeat("更新状态：正在检查运行环境与下载文件。可使用方向键逐行阅读此消息。", 30));
        using var form = (Form)Activator.CreateInstance(assembly.GetType("YTPlayer.Forms.ThemedMessageBoxForm", true)!, message, "更新状态", buttons, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1, null, null)!;
        form.Show();
        Application.DoEvents();
        var content = Descendants(form).OfType<TextBox>().Single();
        Assert.That(content.Text, Is.EqualTo(message));
        Assert.That(content.ReadOnly && content.TabStop, Is.True);
        Assert.That(content.AccessibilityObject.Name, Is.EqualTo("提示内容"));
        Assert.That(content.AccessibilityObject.Role, Is.EqualTo(AccessibleRole.Text));
        Assert.That(content.AccessibleDescription, Is.EqualTo(message));
        Assert.That(content.ScrollBars, Is.EqualTo(ScrollBars.Vertical));
        Assert.That(content.CanFocus, Is.True);
        content.Focus();
        Assert.That(content.Focused, Is.True);
        CheckGeometry(form);
        Font originalFont = form.Font;
        using (var largerFont = new Font(originalFont.FontFamily, originalFont.SizeInPoints * 1.5f))
        {
            form.Font = largerFont;
            form.PerformLayout();
            CheckGeometry(form);
            form.Font = originalFont;
            form.PerformLayout();
            CheckGeometry(form);
        }
        Capture(form, "Message-" + buttons);
        var cancel = (Button)form.CancelButton!;
        Assert.That(cancel.CanFocus && cancel.Enabled, Is.True);
        typeof(Form).GetMethod("ProcessDialogKey", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, new object[] { Keys.Escape });
        Assert.That(form.DialogResult, Is.EqualTo(cancel.DialogResult), "Esc activates the configured cancel result");
        form.Close();
    }

    [TestCase(1, 18), TestCase(6, 18), TestCase(120, 1), TestCase(120, 18)]
    public void ProgressPaintStaysInsideSmallSegments(int width, int height)
    {
#if NET48
        var assembly = typeof(YTPlayer.Update.UpdateDownloadProgress).Assembly;
#else
        var assembly = typeof(YTPlayer.Forms.UpdateCheckDialog).Assembly;
#endif
        var bounds = new Rectangle(3, 5, width, height);
        using var path = (System.Drawing.Drawing2D.GraphicsPath)assembly.GetType("YTPlayer.Utils.ThemedProgressBar", true)!
            .GetMethod("BuildRoundedPath", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { bounds, 8 })!;
        RectangleF actual = path.GetBounds();
        Assert.That(RectangleF.Inflate(bounds, 0.01f, 0.01f).Contains(actual), Is.True,
            "a tiny progress segment must not paint outside its allocated canvas: " + actual);
    }

#if !NET48
    [TestCase(false), TestCase(true)]
    public void UpdaterConfigurationSurvivesSessionStaging(bool legacyLayout)
    {
        string expected = File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fixtures", "YTPlayer.Updater.exe.config"));
        string root = Path.Combine(Program.Output, "staging", Guid.NewGuid().ToString("N"));
        string libs = Path.Combine(root, "libs");
        string destination = Path.Combine(root, "session");
        Directory.CreateDirectory(libs);
        string configPath = Path.Combine(root, "YTPlayer.Updater.exe.config");
        // Current packages must not accidentally pick an older configuration left in libs.
        File.WriteAllText(Path.Combine(libs, Path.GetFileName(configPath)), legacyLayout ? expected : "<configuration />");
        if (!legacyLayout) File.WriteAllText(configPath, expected);
        string? previousRoot = Environment.GetEnvironmentVariable("YTPLAYER_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("YTPLAYER_ROOT", root);
            typeof(YTPlayer.MainForm).GetMethod("CopyUpdaterDependency", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new object[] { configPath, destination });
            var config = new System.Xml.XmlDocument();
            config.Load(Path.Combine(destination, Path.GetFileName(configPath)));
            Assert.That(config.SelectSingleNode("/configuration/System.Windows.Forms.ApplicationConfigurationSection/add[@key='DpiAwareness']/@value")?.Value,
                Is.EqualTo("PerMonitorV2"), "the temporary updater must retain its DPI configuration");
            Assert.That(File.ReadAllText(Path.Combine(destination, Path.GetFileName(configPath))), Is.EqualTo(expected));
        }
        finally
        {
            Environment.SetEnvironmentVariable("YTPLAYER_ROOT", previousRoot);
        }
    }

    private sealed class OfflineUpdateCheckDialog : YTPlayer.Forms.UpdateCheckDialog
    {
        protected override void OnShown(EventArgs e) { }
    }
    private sealed class OfflineLoginForm : YTPlayer.Forms.LoginForm
    {
        public OfflineLoginForm() : base(new YTPlayer.Core.NeteaseApiClient()) { }
        protected override void OnLoad(EventArgs e) { }
    }
    private sealed class OfflineUserInfoForm : YTPlayer.Forms.UserInfoForm
    {
        public OfflineUserInfoForm() : base(new YTPlayer.Core.NeteaseApiClient()) { }
        protected override void OnLoad(EventArgs e) { }
    }
    private sealed class OfflinePlaylistDialog : YTPlayer.Forms.AddToPlaylistDialog
    {
        public OfflinePlaylistDialog() : base(new YTPlayer.Core.NeteaseApiClient(), "1", 0) { }
        protected override void OnLoad(EventArgs e) { }
    }

    public static IEnumerable<TestCaseData> DialogCases()
    {
        foreach (string name in new[] { "AboutDialog", "DonateDialog", "KeyboardShortcutsDialog", "PageJumpDialog", "JumpToPositionDialog", "PurchaseLinkDialog", "NewPlaylistDialog", "ListenTogetherDialog", "DependencyInstallDialog", "UpdateCheckDialog", "UpdateAvailablePromptDialog", "BatchDownloadDialog", "DownloadConflictDialog", "ArtistDetailDialog", "LoginForm", "UserInfoForm", "AddToPlaylistDialog", "OutputDeviceDialog", "SongRecognitionDialog", "ThemedMessageBoxForm", "DownloadManagerForm", "CommentsDialog" })
            foreach (float scale in new[] { 1f, 1.5f, 2f })
                yield return new TestCaseData(name, scale);
    }

    [TestCaseSource(nameof(DialogCases))]
    public void DialogGeometry(string name, float scale)
    {
        using Form form = CreateDialog(name);
        form.Show();
        Application.DoEvents();
        if (name == "AboutDialog")
            Assert.That(Descendants(form).OfType<RichTextBox>().Single().Text, Does.Contain("zjncn (https://github.com/zjncn)"));
        if (scale != 1f)
        {
            form.Font = new Font(form.Font.FontFamily, form.Font.SizeInPoints * scale);
            form.PerformAutoScale();
        }
        form.PerformLayout();
        typeof(YTPlayer.MainForm).Assembly.GetType("YTPlayer.Utils.ThemeManager", true)!.GetMethod("ApplyTheme")!.Invoke(null, new object[] { form });
        Capture(form, name + "-" + scale);
        CheckGeometry(form);
        if (scale == 1 && !form.MinimumSize.IsEmpty && form.FormBorderStyle == FormBorderStyle.Sizable)
        {
            form.Size = form.MinimumSize;
            form.PerformLayout();
            Capture(form, name + "-minimum");
            CheckGeometry(form);
        }
        form.Close();
    }

    [Test]
    public void MainWindowLayoutRemainsUsable()
    {
        using var form = (Form)Activator.CreateInstance(typeof(YTPlayer.MainForm), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[] { false }, null)!;
        form.Show();
        Application.DoEvents();
        // initializeRuntime:false deliberately skips the normal startup layout pipeline.
        foreach (string method in new[] { "ApplySearchPanelStyle", "ApplyResultListViewStyle", "ApplyControlPanelStyle" })
            typeof(YTPlayer.MainForm).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
        typeof(YTPlayer.MainForm).GetMethod("ApplyResultListViewLayoutCore", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
        Capture(form, "MainForm");
        foreach (string name in new[] { "resultListView", "searchTextBox" })
        {
            Control control = Descendants(form).Single(c => c.Name == name);
            Assert.That(control.Visible, Is.True, name);
            Assert.That(control.ClientSize.Height, Is.GreaterThan(15), name);
            // Native ListView borders can round outward by one pixel when scaling 120 -> 96 DPI.
            Assert.That(Rectangle.Inflate(control.Parent!.ClientRectangle, 1, 1).Contains(control.Bounds), Is.True, name);
        }
        form.Close();
    }

    internal static Form CreateDialog(string name)
    {
        var assembly = typeof(YTPlayer.Forms.UpdateCheckDialog).Assembly;
        Type type = assembly.GetTypes().Single(t => t.Name == name);
        object?[] args = name switch
        {
            "PageJumpDialog" => new object?[] { 2, 100 },
            "JumpToPositionDialog" => new object?[] { 120d, 300d },
            "PurchaseLinkDialog" => new object?[] { "测试歌曲", "测试专辑", "https://example.invalid" },
            "NewPlaylistDialog" => new object?[] { "测试歌单" },
            "DependencyInstallDialog" => new object?[] { "安装依赖", "正在准备安装运行环境，请稍候。" },
            "UpdateAvailablePromptDialog" => new object?[] { new YTPlayer.Update.UpdatePlan(), null },
            "BatchDownloadDialog" => new object?[] { new List<string> { "测试歌曲 1", "测试歌曲 2" }, "批量下载" },
            "DownloadConflictDialog" => new object?[] { new List<string> { "测试歌曲.mp3", "测试歌曲 2.mp3" } },
            "ArtistDetailDialog" => new object?[] { new YTPlayer.Models.ArtistDetail { Name = "测试歌手" } },
            "OutputDeviceDialog" => new object?[] { new[] { new YTPlayer.Models.AudioOutputDeviceInfo { DisplayName = "默认输出设备" } }, null },
            "SongRecognitionDialog" => new object?[] { new[] { new YTPlayer.Models.AudioInputDeviceInfo { DisplayName = "默认输入设备" } }, null, null, CancellationToken.None },
            "ThemedMessageBoxForm" => new object?[] { "测试提示内容，保留完整的可访问文本。", "提示", MessageBoxButtons.OKCancel, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1, null, null },
            "CommentsDialog" => new object?[] { new YTPlayer.Core.NeteaseApiClient(), new YTPlayer.Models.CommentTarget("1", YTPlayer.Core.CommentType.Song, "测试歌曲"), null, false },
            _ => Array.Empty<object?>()
        };
        if (name == "UpdateCheckDialog") return new OfflineUpdateCheckDialog();
        if (name == "LoginForm") return new OfflineLoginForm();
        if (name == "UserInfoForm") return new OfflineUserInfoForm();
        if (name == "AddToPlaylistDialog") return new OfflinePlaylistDialog();
        var result = (Form)Activator.CreateInstance(type, args)!;
        if (name == "CommentsDialog")
        {
            var load = type.GetMethod("OnDialogLoad", BindingFlags.Instance | BindingFlags.NonPublic)!;
            result.Load -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), result, load);
        }
        return result;
    }
#endif

    internal static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    internal static void Capture(Form form, string name)
    {
        var lines = new List<string> { $"{form.GetType().Name}: client={form.ClientSize}, dpi={form.DeviceDpi}, font={form.Font}, baseline={form.AutoScaleDimensions}, current={form.CurrentAutoScaleDimensions}" };
        foreach (Control control in Descendants(form))
        {
            using var bitmap = new Bitmap(1, 1);
            using var graphics = Graphics.FromImage(bitmap);
            lines.Add($"{control.GetType().Name} {control.Name}: bounds={control.Bounds}, client={control.ClientSize}, preferred={control.PreferredSize}, minimum={control.MinimumSize}, font={control.Font.SizeInPoints}, visible={control.Visible}, region={control.Region?.GetBounds(graphics)}, text={control.Text}");
            if (control is TableLayoutPanel table) lines.Add($"  rows={string.Join(",", table.GetRowHeights())}, scrollMinimum={table.AutoScrollMinSize}, display={table.DisplayRectangle}");
        }
        File.WriteAllLines(Path.Combine(Program.Output, name + ".txt"), lines, new UTF8Encoding(false));
        using var image = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
        image.Save(Path.Combine(Program.Output, name + ".png"), ImageFormat.Png);
    }

    internal static void CheckGeometry(Form form)
    {
        using (Assert.EnterMultipleScope())
        {
            foreach (Control control in Descendants(form).Where(c => c.Visible))
            {
                // This off-screen live announcement is intentionally reserved for screen readers.
                if (control.Name == "commentsAccessibilityAnnouncementLabel") continue;
                Assert.That(control.Width, Is.GreaterThan(0), control.Name + " width");
                Assert.That(control.Height, Is.GreaterThan(0), control.Name + " height");
                if (control.Region != null)
                {
                    using var graphics = control.CreateGraphics();
                    RectangleF visibleBounds = control.Region.GetBounds(graphics);
                    Assert.That(visibleBounds.Width, Is.GreaterThanOrEqualTo(control.ClientSize.Width - 1), control.Name + " clipped region width");
                    Assert.That(visibleBounds.Height, Is.GreaterThanOrEqualTo(control.ClientSize.Height - 1), control.Name + " clipped region height");
                }
                if (control is Label && !string.IsNullOrEmpty(control.Text))
                    Assert.That(control.Height, Is.GreaterThanOrEqualTo(TextRenderer.MeasureText(control.Text, control.Font, new Size(control.Width, int.MaxValue), TextFormatFlags.WordBreak).Height), control.Name + " text height");
                if (control.Parent != null && !(control.Parent is ScrollableControl { AutoScroll: true }))
                    Assert.That(control.Parent.ClientRectangle.Contains(control.Bounds), Is.True, control.Name + " outside parent: " + control.Bounds + " / " + control.Parent.ClientRectangle);
                if (control.Parent is TableLayoutPanel table)
                    foreach (Control sibling in table.Controls)
                        if (sibling != control && sibling.Visible && table.GetPositionFromControl(sibling) != table.GetPositionFromControl(control))
                            Assert.That(control.Bounds.IntersectsWith(sibling.Bounds), Is.False,
                                control.Name + " table cell overlaps " + sibling.Name);
                if (control is Button button)
                {
                    Size text = TextRenderer.MeasureText(button.Text, button.Font);
                    Assert.That(button.ClientSize.Height, Is.GreaterThanOrEqualTo(text.Height), button.Text + " button text height");
                    Assert.That(button.ClientSize.Width, Is.GreaterThanOrEqualTo(text.Width), button.Text + " button text width");
                    foreach (Control sibling in button.Parent!.Controls)
                        if (sibling is Button && sibling != button && sibling.Visible)
                            Assert.That(button.Bounds.IntersectsWith(sibling.Bounds), Is.False, button.Text + " overlaps " + sibling.Text);
                }
            }
        }
    }
}
