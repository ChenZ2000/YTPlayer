#if !NET48
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;
using NUnit.Framework;
using YTPlayer;
using YTPlayer.Core;
using YTPlayer.Core.Playback;
using YTPlayer.Models;
using YTPlayer.Utils;

[TestFixture, Apartment(ApartmentState.STA)]
public class ControlInteractionTests
{
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static void SetField(object owner, string name, object? value) => owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);
    private static object? Invoke(object owner, string name, params object[] args) => owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, args);
    private static void Pump(int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        do { Application.DoEvents(); Thread.Sleep(5); } while (watch.ElapsedMilliseconds < milliseconds);
    }
    private static void Mouse(Control slider, int message, int x, int y) => ControlBaselineTests.Mouse(slider, message, x, y);
    private static AccessibleTrackBar AddSlider(Form form, int dpi = 96, int maximum = 3600)
    {
        var slider = new AccessibleTrackBar { GeometryDpi = dpi, Maximum = maximum, Value = maximum / 3,
            AccessibleName = "播放进度", Width = 600 * dpi / 96, TabIndex = 0 };
        slider.Font = new Font("Microsoft YaHei UI", 10.5f * dpi / slider.DeviceDpi);
        slider.Height = slider.PreferredVisualHeight;
        slider.PreviewText = (p, delta) => $"{TimeSpan.FromSeconds(p):hh\\:mm\\:ss}" + (delta.HasValue ? $" ({delta:+0.0;-0.0;0.0}s)" : "");
        form.Controls.Add(slider);
        return slider;
    }

    [TestCase(96), TestCase(120), TestCase(144), TestCase(192), TestCase(288)]
    public void SliderGeometryAndPointerLifecycle(int dpi)
    {
        using var form = new Form { ClientSize = new Size(1900, 240) };
        using var slider = AddSlider(form, dpi);
        form.Show();
        var track = slider.GetTrackBounds();
        Assert.That(track.Height, Is.EqualTo(6 * dpi / 96f).Within(.01));
        Assert.That(slider.GetHitBounds().Height, Is.GreaterThanOrEqualTo(32 * dpi / 96f));
        Assert.That(slider.ClientRectangle.Contains(Rectangle.Ceiling(slider.GetHitBounds())), Is.True);
        slider.SetVisualPosition(1234.25);
        int businessValue = slider.Value;
        Assert.That(slider.Value, Is.EqualTo(businessValue), "visual updates leave native value alone");
        double origin = slider.VisualPosition;
        PointF thumb = slider.GetThumbCenter();
        int x = (int)thumb.X + 4, y = (int)thumb.Y;
        var ended = new List<SliderGestureEventArgs>();
        int started = 0;
        slider.GestureStarted += (_, _) => started++;
        slider.GestureEnded += (_, e) => ended.Add(e);
        Mouse(slider, 0x201, x, y);
        Assert.That(slider.VisualPosition, Is.EqualTo(origin).Within(.001), "grabbing the edge must not jump");
        slider.SetVisualPosition(0);
        Assert.That(slider.VisualPosition, Is.EqualTo(origin).Within(.001), "background update cannot overwrite pointer preview");
        Mouse(slider, 0x200, x + 30, y);
        Assert.That(slider.VisualPosition, Is.EqualTo(origin + 30 * slider.Maximum / track.Width).Within(.001));
        Mouse(slider, 0x200, -200, y);
        Assert.That(slider.VisualPosition, Is.EqualTo(0));
        Mouse(slider, 0x200, slider.Width + 200, y);
        Assert.That(slider.VisualPosition, Is.EqualTo(slider.Maximum));
        Mouse(slider, 0x202, slider.Width + 200, y);
        Assert.That(started, Is.EqualTo(1));
        Assert.That(ended.Count, Is.EqualTo(1));
        Assert.That(ended[0].Canceled, Is.False);
        Assert.That(slider.Capture || slider.IsDragging, Is.False);
        Mouse(slider, 0x200, 40, y);
        Assert.That(slider.VisualPosition, Is.EqualTo(slider.Maximum), "released slider cannot follow pointer");
        foreach (Action cancel in new Action[] { () => slider.Capture = false, () => slider.Enabled = false,
            () => slider.Visible = false, () => slider.EndGesture(true) })
        {
            slider.Enabled = true; slider.Visible = true;
            Mouse(slider, 0x201, slider.Width / 2, y);
            cancel();
            Assert.That(slider.IsDragging || slider.Capture, Is.False);
            Assert.That(ended.Last().Canceled, Is.True);
        }
        Assert.That(started, Is.EqualTo(5));
        Assert.That(ended.Count, Is.EqualTo(5), "capture reentrancy cannot emit duplicate end events");
        slider.Enabled = true; slider.Visible = true;
        foreach (double value in new[] { 0d, 1, 1800.25, 3599, 3600 })
            Assert.That(slider.ClientRectangle.Contains(slider.GetHintBounds("01:00:00 (+01:00:00)", value)), Is.True);
        ThemeManager.ApplyTheme(form);
        Mouse(slider, 0x201, slider.Width - (int)(22 * dpi / 96f), y);
        LayoutTests.Capture(form, "Slider-geometry-" + dpi);
    }

    [TestCase(false, PlaybackState.Playing), TestCase(false, PlaybackState.Paused), TestCase(false, PlaybackState.Buffering), TestCase(true, PlaybackState.Playing)]
    public void MainGestureCommitsFinalFractionAndDropsOldTrack(bool changeTrack, PlaybackState state)
    {
        using var form = new MainForm(false);
        using var engine = new BassAudioEngine();
        engine.SetVolume(0);
        var machine = Field<YTPlayer.Core.Playback.PlaybackStateMachine>(engine, "_stateMachine");
        machine.TransitionTo(PlaybackState.Loading);
        machine.TransitionTo(PlaybackState.Buffering);
        if (state != PlaybackState.Buffering) machine.TransitionTo(PlaybackState.Playing);
        if (state == PlaybackState.Paused) machine.TransitionTo(PlaybackState.Paused);
        using var seeks = new SeekManager(engine);
        SetField(engine, "_currentSong", new SongInfo { Id = "gesture-fixture", Duration = 300, Name = "离线手势测试" });
        SetField(form, "_audioEngine", engine);
        SetField(form, "_seekManager", seeks);
        SetField(form, "_cachedDuration", 300d);
        var requested = new List<double>();
        int finished = 0;
        seeks.SeekRequested += (_, e) => requested.Add(e.TargetSeconds);
        seeks.SeekCompleted += (_, _) => finished++;
        form.Show();
        Invoke(form, "UpdateProgressTrackBarDisplay", 5.25, 300d, false);
        var slider = Field<AccessibleTrackBar>(form, "progressTrackBar");
        int y = (int)slider.GetThumbCenter().Y;
        Mouse(slider, 0x201, slider.Width / 3 + 1, y);
        Mouse(slider, 0x200, slider.Width / 2 + 3, y);
        double final = slider.VisualPosition;
        Invoke(form, "UpdateProgressTrackBarDisplay", 0d, 300d, false);
        Assert.That(slider.VisualPosition, Is.EqualTo(final));
        if (changeTrack)
        {
            Invoke(form, "CancelProgressGestureForTrackChange");
            SetField(engine, "_currentSong", new SongInfo { Id = "next", Duration = 300 });
        }
        int beforeUp = requested.Count;
        Mouse(slider, 0x202, slider.Width / 2 + 3, y);
        Assert.That(Field<bool>(form, "_isUserDragging"), Is.False);
        Assert.That(slider.IsDragging, Is.False);
        Assert.That(requested.Count - beforeUp, Is.EqualTo(changeTrack ? 0 : 1));
        Assert.That(finished, Is.EqualTo(changeTrack ? 0 : 1));
        if (changeTrack)
        {
            // A worker can post cancellation and the user can start a newer gesture
            // before that callback is dispatched. The old callback must leave it alone.
            Mouse(slider, 0x201, slider.Width / 3, y);
            System.Threading.Tasks.Task.Run(() => Invoke(form, "CancelProgressGestureForTrackChange")).GetAwaiter().GetResult();
            slider.EndGesture(true);
            Mouse(slider, 0x201, slider.Width / 2, y);
            Pump(20);
            Assert.That(slider.IsDragging && Field<bool>(form, "_isUserDragging"), Is.True, "late cancellation cannot end a newer gesture");
            slider.EndGesture(true);
        }
        if (!changeTrack)
        {
            Assert.That(requested.Last(), Is.EqualTo(final).Within(.000001));
            Assert.That(requested.Count(p => Math.Abs(p - final) < .000001), Is.EqualTo(1));
            Mouse(slider, 0x201, slider.Width / 4, y);
            slider.Capture = false;
            Assert.That(Field<bool>(form, "_isUserDragging"), Is.False, "capture loss resets both layers");
            Mouse(slider, 0x201, slider.Width / 3, y);
            Invoke(form, "MainForm_Deactivate", form, EventArgs.Empty);
            Assert.That(slider.IsDragging, Is.False, "window deactivation ends gesture");
            Mouse(slider, 0x201, slider.Width / 2, y);
            Field<GroupBox>(form, "controlPanel").Hide();
            Assert.That(Field<bool>(form, "_isUserDragging"), Is.False, "hiding control bar ends gesture");
        }
        Invoke(form, "UpdateProgressTrackBarDisplay", 4d, 0d, false);
        // Clear song metadata too: production prefers a known metadata duration.
        SetField(engine, "_currentSong", null);
        Invoke(form, "UpdateProgressTrackBarDisplay", 4d, 0d, false);
        Assert.That(slider.Enabled, Is.False, "unknown duration is not seekable");
        seeks.CancelPendingSeeks();
        SetField(form, "_audioEngine", null); SetField(form, "_seekManager", null);
    }

    [Test]
    public void VolumeSavesOnlyAfterGestureOrQuietPeriod()
    {
        int saves = 0;
        using var scheduler = new VolumeSaveScheduler(() => saves++);
        scheduler.Request();
        scheduler.BeginGesture();
        for (int i = 0; i < 500; i++) scheduler.Request();
        Pump(350);
        Assert.That(saves, Is.Zero, "no disk callback while dragging, even after debounce interval");
        scheduler.EndGesture(); scheduler.EndGesture();
        Assert.That(saves, Is.EqualTo(1));
        for (int i = 0; i < 4; i++) { scheduler.Request(); Pump(100); }
        Assert.That(saves, Is.EqualTo(1));
        Pump(350);
        Assert.That(saves, Is.EqualTo(2));
        scheduler.Request(); scheduler.Flush(); scheduler.Flush();
        Assert.That(saves, Is.EqualTo(3), "exit flush happens exactly once");
        Pump(350);
        Assert.That(saves, Is.EqualTo(3), "idle timer is stopped");
    }

    [TestCase(520, 440, 1f), TestCase(760, 620, 1f), TestCase(1200, 760, 1f), TestCase(650, 480, 1.5f)]
    public void MainLayoutThemesAndTextRemainReachable(int width, int height, float fontScale)
    {
        using var form = new MainForm(false);
        form.Show();
        float scale = form.DeviceDpi / 96f;
        form.ClientSize = new Size((int)(width * scale), (int)(height * scale));
        foreach (var control in LayoutTests.Descendants(form).Where(c => c is Label || c is Button || c is TrackBar || c is ComboBox || c is TextBox))
            control.Font = new Font(control.Font.FontFamily, control.Font.SizeInPoints * fontScale, control.Font.Style);
        string longTitle = string.Concat(Enumerable.Repeat("长标题与演出信息 — 测试完整可访问名称 ", 10));
        var title = Field<Label>(form, "currentSongLabel"); title.Text = longTitle;
        var lyric = Field<Label>(form, "lyricsLabel"); lyric.Text = longTitle;
        Invoke(form, "UpdateProgressTrackBarDisplay", 36000.125, 360000d, false);
        Invoke(form, "LayoutPlayerControls");
        var searchPanel = Field<Panel>(form, "searchPanel");
        Assert.That(searchPanel.VerticalScroll.Visible || searchPanel.HorizontalScroll.Visible, Is.False, "search rows fit without incidental scrollbars");
        Assert.That(title.AccessibilityObject.Name, Is.EqualTo(longTitle));
        Assert.That(title.AutoEllipsis && lyric.AutoEllipsis, Is.True);
        var progress = Field<AccessibleTrackBar>(form, "progressTrackBar");
        var volume = Field<AccessibleTrackBar>(form, "volumeTrackBar");
        var play = Field<Button>(form, "playPauseButton");
        Assert.That(play.Height, Is.GreaterThanOrEqualTo(40 * scale));
        foreach (Control control in progress.Parent!.Controls)
        {
            Assert.That(control.Width, Is.GreaterThan(0));
            foreach (Control sibling in progress.Parent.Controls)
                if (control != sibling) Assert.That(control.Bounds.IntersectsWith(sibling.Bounds), Is.False, control.Name + " overlaps " + sibling.Name);
        }
        var host = (ScrollableControl)progress.Parent;
        host.ScrollControlIntoView(volume);
        Assert.That(host.ClientRectangle.Contains(volume.Bounds), Is.True, "volume reachable in local viewport");
        play.Focus(); form.SelectNextControl(play, true, true, true, true);
        Assert.That(progress.Focused, Is.True, "Play -> progress Tab order");
        form.SelectNextControl(progress, true, true, true, true);
        Assert.That(volume.Focused, Is.True, "progress -> volume Tab order");
        foreach (ThemeScheme scheme in Enum.GetValues(typeof(ThemeScheme)))
        {
            ThemeManager.SetScheme(scheme); ThemeManager.ApplyTheme(form);
            Invoke(form, "ApplyControlPanelStyle");
            LayoutTests.Capture(form, $"Main-{width}-{height}-{fontScale}-{scheme}");
        }
        ThemeManager.HighContrastOverride = true;
        try
        {
            ThemeManager.ApplyTheme(form);
            Assert.That(play.BackColor, Is.EqualTo(SystemColors.Highlight));
            Assert.That(ThemeManager.SelectedScheme, Is.EqualTo(ThemeScheme.GrassMuted), "system colors must preserve selected theme");
            LayoutTests.Capture(form, $"Main-{width}-system-colors-simulated");
        }
        finally { ThemeManager.HighContrastOverride = null; ThemeManager.SetScheme(ThemeScheme.GrassFresh); }
    }

    private static double Contrast(Color a, Color b)
    {
        double L(Color color)
        {
            double C(byte v) { double c = v / 255d; return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4); }
            return .2126 * C(color.R) + .7152 * C(color.G) + .0722 * C(color.B);
        }
        double x = L(a), y = L(b); return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05);
    }

    [Test]
    public void ThemeContrastMeetsTextAndOutlineTargets()
    {
        foreach (var palette in ThemeManager.AvailablePalettes)
        {
            foreach (Color background in new[] { palette.Accent, palette.AccentHover, palette.AccentPressed })
                Assert.That(Contrast(palette.AccentText, background), Is.GreaterThanOrEqualTo(4.5), palette.Name + " primary state");
            foreach (Color background in new[] { palette.SurfaceBackground, palette.SurfaceAlt, palette.Highlight, palette.Focus })
            {
                Assert.That(Contrast(palette.TextPrimary, background), Is.GreaterThanOrEqualTo(4.5));
                Assert.That(Contrast(palette.FocusRing, background), Is.GreaterThanOrEqualTo(3));
                Assert.That(Contrast(palette.ControlBorder, background), Is.GreaterThanOrEqualTo(3));
            }
            TestContext.WriteLine($"{palette.Name}: primary text={Contrast(palette.AccentText, palette.Accent):F2}; focus on white={Contrast(palette.FocusRing, palette.SurfaceBackground):F2}");
        }
    }

    [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr process, int flag);
    [Test]
    public void RepeatedDragAndPaintDoesNotLeakHandlesOrPaintAtRest()
    {
        using var form = new Form { ClientSize = new Size(650, 150) };
        using var slider = AddSlider(form);
        form.Show();
        int paints = 0; slider.Paint += (_, _) => paints++;
        int y = (int)slider.GetThumbCenter().Y;
        void Drag(int count)
        {
            Mouse(slider, 0x201, 40, y);
            for (int i = 0; i < count; i++) { Mouse(slider, 0x200, 30 + i % 530, y); slider.Update(); }
            Mouse(slider, 0x202, 200, y);
        }
        Drag(100); Pump(30);
        int gdi = GetGuiResources(Process.GetCurrentProcess().Handle, 0), user = GetGuiResources(Process.GetCurrentProcess().Handle, 1);
        Drag(3000); Pump(40);
        Assert.That(GetGuiResources(Process.GetCurrentProcess().Handle, 0) - gdi, Is.LessThanOrEqualTo(2));
        Assert.That(GetGuiResources(Process.GetCurrentProcess().Handle, 1) - user, Is.LessThanOrEqualTo(2));
        TestContext.WriteLine($"GDI={gdi}->{GetGuiResources(Process.GetCurrentProcess().Handle, 0)}, USER={user}->{GetGuiResources(Process.GetCurrentProcess().Handle, 1)}");
        int atRest = paints; Pump(350);
        Assert.That(paints, Is.EqualTo(atRest), "no animation timer when idle");
        Assert.That(paints, Is.GreaterThan(3000), "test must exercise real WM_PAINT");
    }

    [Test]
    public void NativeAccessibilityAndVisualOnlyEvents()
    {
        using var form = new Form { ClientSize = new Size(650, 150) };
        using var slider = AddSlider(form);
        form.Show(); slider.Focus(); Pump(40);
        var element = AutomationElement.FromHandle(slider.Handle);
        Assert.That(element.Current.ControlType, Is.EqualTo(ControlType.Slider));
        Assert.That(element.Current.Name, Is.EqualTo("播放进度"));
        TestContext.WriteLine("Native UIA patterns: " + string.Join(",", element.GetSupportedPatterns().Select(p => p.ProgrammaticName)));
        var nativeValue = slider.AccessibilityObject.Value;
        var uiaValue = (ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern);
        Assert.That(uiaValue.Current.Value, Is.EqualTo(nativeValue));
        Assert.That(ControlBaselineTests.SendMessage(slider.Handle, 0x401, IntPtr.Zero, IntPtr.Zero).ToInt32(), Is.EqualTo(0));
        Assert.That(ControlBaselineTests.SendMessage(slider.Handle, 0x402, IntPtr.Zero, IntPtr.Zero).ToInt32(), Is.EqualTo(3600));
        Assert.That(slider.AccessibilityObject.Role, Is.EqualTo(AccessibleRole.Slider));
        var events = new List<uint>();
        WinEvent callback = (_, evt, hwnd, _, _, _, _) => { if (hwnd == slider.Handle) events.Add(evt); };
        IntPtr hook = SetWinEventHook(0x8000, 0x8018, IntPtr.Zero, callback, (uint)Process.GetCurrentProcess().Id, 0, 0);
        Assert.That(hook, Is.Not.EqualTo(IntPtr.Zero));
        try
        {
            for (int i = 0; i < 20; i++)
            {
                slider.SetVisualPosition(1200 + i * .01);
                Mouse(slider, 0x200, 100 + i, (int)slider.GetThumbCenter().Y);
                slider.Update();
            }
            Pump(50);
            Assert.That(events.Where(e => e == 0x800C || e == 0x800E || e == 0x8005), Is.Empty, "pure visuals must not emit name/value/focus changes");
            Assert.That(slider.Value, Is.EqualTo(1200));
            Assert.That(slider.AccessibilityObject.Value, Is.EqualTo(nativeValue));
            Assert.That(uiaValue.Current.Value, Is.EqualTo(nativeValue));
        }
        finally { UnhookWinEvent(hook); GC.KeepAlive(callback); }
    }
    private delegate void WinEvent(IntPtr hook, uint evt, IntPtr hwnd, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEvent callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
}
#endif
