using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using NUnit.Framework;
#if !NET48
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using YTPlayer;
using YTPlayer.Forms;
using YTPlayer.Utils;
#endif

[TestFixture, Apartment(ApartmentState.STA)]
public class MessageTextTests
{
    private static Assembly PlayerAssembly =>
#if NET48
        Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "YTPlayer.Updater.exe"));
#else
        typeof(UpdateCheckDialog).Assembly;
#endif

    private static Form CreateMessage(string text, MessageBoxButtons buttons = MessageBoxButtons.OKCancel)
    {
        var form = (Form)Activator.CreateInstance(PlayerAssembly.GetType("YTPlayer.Forms.ThemedMessageBoxForm", true)!,
            text, "多行提示测试", buttons, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1, null, null)!;
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-32000, -32000);
        return form;
    }

    private static TextBox Content(Form form) => LayoutTests.Descendants(form).OfType<TextBox>().Single();
    private static void Pump(int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        do { Application.DoEvents(); Thread.Sleep(5); } while (watch.ElapsedMilliseconds < milliseconds);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    private static int NativeLines(TextBox box) => SendMessage(box.Handle, 0xBA, IntPtr.Zero, IntPtr.Zero).ToInt32();

    private static void Key(Control control, Keys key)
    {
        var message = Message.Create(control.Handle, 0x100, (IntPtr)(int)key, (IntPtr)1);
        if (!control.PreProcessMessage(ref message)) SendMessage(control.Handle, message.Msg, message.WParam, message.LParam);
    }

    [TestCase("\n"), TestCase("\r\n"), TestCase("\r"), TestCase("\u0085"), TestCase("\u2028"), TestCase("\u2029")]
    public void NativeCaretCanReachEveryMessageLine(string separator)
    {
        using var form = CreateMessage("确定删除？" + separator + separator + "（保留原项目。）");
        form.Show();
        var content = Content(form);
        content.WordWrap = false;
        Assert.That(NativeLines(content), Is.EqualTo(3), "check the native EDIT line model, not TextBox.Lines");
        Assert.That(content.Text, Is.EqualTo("确定删除？\r\n\r\n（保留原项目。）"));
        Assert.That(content.AccessibilityObject.Value, Is.EqualTo(content.Text));
        Assert.That(content.AccessibleDescription, Is.EqualTo(content.Text));
        Assert.That(content.Focused, Is.True);
        content.Select(0, 0);
        Key(content, Keys.Down);
        Assert.That(content.GetLineFromCharIndex(content.SelectionStart), Is.EqualTo(1), "Down reaches the empty line");
        Key(content, Keys.Down);
        Assert.That(content.GetLineFromCharIndex(content.SelectionStart), Is.EqualTo(2), "Down reaches the explanation");
        Pump(350);
        Assert.That(content.GetLineFromCharIndex(content.SelectionStart), Is.EqualTo(2), "initial focus callbacks must not reset the caret");
        Key(content, Keys.Up);
        Assert.That(content.GetLineFromCharIndex(content.SelectionStart), Is.EqualTo(1));
        Key(content, Keys.Up);
        Assert.That(content.SelectionStart, Is.Zero);
        form.Close();
    }

    [Test]
    public void MixedLineEndingsUpdatesAndHandleRecreationRemainReadable()
    {
        using var form = CreateMessage("初始化");
        form.Show();
        var content = Content(form);
        content.WordWrap = false;
        string text = "首行\r\n第二行\n\n说明\r末行\u2028下一行\u2029\u0085";
        string expected = "首行\r\n第二行\r\n\r\n说明\r\n末行\r\n下一行\r\n\r\n";
        content.Text = text;
        Assert.That(content.Text, Is.EqualTo(expected));
        Assert.That(NativeLines(content), Is.EqualTo(8));
        content.Select(content.GetFirstCharIndexFromLine(3), 2);
        int caret = content.SelectionStart;
        content.Text = text;
        Assert.That(content.SelectionStart, Is.EqualTo(caret), "an identical normalized update keeps reading position");
        Assert.That(content.SelectedText, Is.EqualTo("说明"));
        content.BorderStyle = BorderStyle.FixedSingle; // Recreates the native EDIT window.
        Assert.That(NativeLines(content), Is.EqualTo(8));
        content.SelectAll();
        Assert.That(content.SelectedText, Is.EqualTo(expected), "all text is available for selection and copying");
        SendMessage(content.Handle, 0x102, (IntPtr)'X', (IntPtr)1);
        Assert.That(content.Text, Is.EqualTo(expected), "typing cannot alter a read-only message");
        content.Text = string.Empty;
        Assert.That(NativeLines(content), Is.EqualTo(1));
        form.Close();
    }

    [Test]
    public void ImmediateTabAndReturnTabPreserveUserFocusAndCaret()
    {
        using var form = CreateMessage("主句\n\n说明");
        form.Show();
        var content = Content(form);
        content.Select(content.GetFirstCharIndexFromLine(2), 0);
        int caret = content.SelectionStart;
        Key(content, Keys.Tab);
        var focused = LayoutTests.Descendants(form).OfType<Button>().Single(button => button.Focused);
        Pump(350);
        Assert.That(focused.Focused, Is.True, "a delayed focus retry must not steal Tab's destination");
        form.SelectNextControl(focused, false, true, true, true);
        Assert.That(content.Focused, Is.True, "Shift+Tab can return to content");
        Assert.That(content.SelectionStart, Is.EqualTo(caret), "returning to the message retains reading position");
        Key(content, Keys.Escape);
        Assert.That(form.DialogResult, Is.EqualTo(((Button)form.CancelButton!).DialogResult));
        form.Close();
    }

    [TestCase(MessageBoxButtons.OK), TestCase(MessageBoxButtons.OKCancel), TestCase(MessageBoxButtons.YesNo),
     TestCase(MessageBoxButtons.YesNoCancel), TestCase(MessageBoxButtons.RetryCancel), TestCase(MessageBoxButtons.AbortRetryIgnore)]
    public void ReadingTextPreservesConfiguredDefaultAction(MessageBoxButtons buttons)
    {
        using var form = CreateMessage("提示\n说明", buttons);
        form.Show();
        DialogResult expected = ((Button)form.AcceptButton!).DialogResult;
        Key(Content(form), Keys.Enter);
        Assert.That(form.DialogResult, Is.EqualTo(expected));
        form.Close();
    }

    [Test]
    public void LongMessageScrollsToFinalLine()
    {
        using var form = CreateMessage(string.Join("\n", Enumerable.Range(1, 100).Select(i => "第 " + i + " 行")));
        form.Show();
        var content = Content(form);
        Assert.That(content.ScrollBars, Is.EqualTo(ScrollBars.Vertical));
        Assert.That(NativeLines(content), Is.EqualTo(100));
        content.Select(content.GetFirstCharIndexFromLine(99), 0);
        content.ScrollToCaret();
        Assert.That(content.GetLineFromCharIndex(content.SelectionStart), Is.EqualTo(99));
        Assert.That(SendMessage(content.Handle, 0xCE, IntPtr.Zero, IntPtr.Zero).ToInt32(), Is.GreaterThan(0));
        Key(content, Keys.Up);
        Assert.That(content.GetLineFromCharIndex(content.SelectionStart), Is.EqualTo(98));
        form.Close();
    }

#if !NET48
    [Test]
    public void UiaTextPatternExposesSeparateLines()
    {
        using var form = CreateMessage("首行\n次行\n末行");
        form.Location = new Point(100, 100);
        form.Show();
        Pump(100);
        IntPtr handle = Content(form).Handle;
        var read = Task.Run(() =>
        {
            var element = AutomationElement.FromHandle(handle);
            var pattern = (TextPattern)element.GetCurrentPattern(TextPattern.Pattern);
            var range = pattern.DocumentRange;
            string all = range.GetText(-1);
            range.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start);
            range.ExpandToEnclosingUnit(TextUnit.Line);
            string first = range.GetText(-1);
            // Readers move a collapsed review cursor, then expand its new line to read it.
            range.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start);
            int moved = range.Move(TextUnit.Line, 1);
            range.ExpandToEnclosingUnit(TextUnit.Line);
            return (all, first, second: range.GetText(-1), moved);
        });
        var watch = Stopwatch.StartNew();
        while (!read.IsCompleted && watch.ElapsedMilliseconds < 8000) Pump(10);
        Assert.That(read.IsCompleted, Is.True, "UIA query must not block the dialog");
        var result = read.GetAwaiter().GetResult();
        TestContext.WriteLine($"UIA line movement {result.moved}: first={result.first.Replace("\r", "<CR>").Replace("\n", "<LF>")}; second={result.second.Replace("\r", "<CR>").Replace("\n", "<LF>")}");
        Assert.That(result.all, Does.Contain("首行\r\n次行\r\n末行"));
        Assert.That(result.first.TrimEnd(), Is.EqualTo("首行"));
        Assert.That(result.second.TrimEnd(), Is.EqualTo("次行"));
        form.Close();
    }

    [Test]
    public void OtherReadOnlyDisplaysUseNativeLineBreaks()
    {
        using var room = new ListenTogetherDialog();
        room.SetPlaybackSummary("歌曲\n进度\r状态");
        var status = LayoutTests.Descendants(room).OfType<TextBox>().Single(box => box.AccessibleName == "房间播放状态");
        status.WordWrap = false;
        Assert.That(NativeLines(status), Is.EqualTo(3));

        using var purchase = new PurchaseLinkDialog("歌名\n副标题", "专辑", "https://example.invalid/");
        purchase.Show();
        var explanation = Content(purchase);
        explanation.WordWrap = false;
        Assert.That(explanation.Focused && explanation.ReadOnly, Is.True);
        Assert.That(NativeLines(explanation), Is.EqualTo(4));
        Key(explanation, Keys.Tab);
        Pump(350);
        Assert.That(LayoutTests.Descendants(purchase).OfType<Button>().Any(button => button.Focused), Is.True);
        Key(explanation, Keys.Escape);
        Assert.That(purchase.PurchaseRequested, Is.False);
        purchase.Close();

        using var view = new AccessibleDocumentView();
        var document = new SemanticDocumentBuilder("测试文档").Paragraph("首行\n\n说明\r末行").Build();
        view.SetDocument(document);
        typeof(AccessibleDocumentView).GetMethod("ShowFallback", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null);
        var fallback = view.Controls.OfType<TextBox>().Single();
        fallback.WordWrap = false;
        Assert.That(fallback.Text, Does.Contain("首行\r\n\r\n说明\r\n末行"));
        Assert.That(NativeLines(fallback), Is.EqualTo(fallback.Text.Split(new[] { "\r\n" }, StringSplitOptions.None).Length));
        Assert.That(document.BodyHtml, Does.Contain("首行<br><br>说明<br>末行"));
    }
#endif
}
