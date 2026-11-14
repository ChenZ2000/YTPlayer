using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using NUnit.Framework;
#if !NET48
using System.Windows.Automation;
using System.Runtime.Loader;
#endif

#if !NET48
[TestFixture, Apartment(ApartmentState.STA)]
public class ControlBaselineTests
{
    [DllImport("user32.dll")] internal static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
    internal static void Mouse(Control control, int message, int x, int y) =>
        SendMessage(control.Handle, message, message == 0x202 ? IntPtr.Zero : new IntPtr(1), new IntPtr((y << 16) | (x & 0xffff)));

    [Test]
    public void CaptureControlBaseline()
    {
        string? baseline = Environment.GetEnvironmentVariable("YTPLAYER_CONTROL_BASELINE_ASSEMBLY");
        var assembly = string.IsNullOrEmpty(baseline) ? typeof(YTPlayer.MainForm).Assembly :
            new AssemblyLoadContext("control-baseline", isCollectible: true).LoadFromAssemblyPath(Path.GetFullPath(baseline));
        using var form = new Form { ClientSize = new Size(660, 210), Text = "控件渲染", AutoScaleMode = AutoScaleMode.None };
        using var slider = (TrackBar)Activator.CreateInstance(assembly.GetType("YTPlayer.AccessibleTrackBar", true)!, true)!;
        slider.SetBounds(16, 16, 620, 64);
        slider.Maximum = 3600; slider.Value = 1234; slider.AccessibleName = "播放进度";
        form.Controls.Add(slider);
        var button = new Button { Text = "播放(&P)", Tag = "Primary", Bounds = new Rectangle(16, 100, 140, 48) };
        form.Controls.Add(button);
        var secondary = new Button { Text = "取消(&C)", Bounds = new Rectangle(174, 100, 140, 48) };
        form.Controls.Add(secondary);
        var theme = assembly.GetType("YTPlayer.Utils.ThemeManager", true)!;
        form.Show(); Application.DoEvents();
        theme.GetMethod("ApplyTheme")!.Invoke(null, new object[] { form });
        slider.Focus();
        var semantics = new[] { $"dpi={slider.DeviceDpi}", $"type={slider.AccessibilityObject.GetType().FullName}",
            $"role={slider.AccessibilityObject.Role}", $"name={slider.AccessibilityObject.Name}", $"value={slider.AccessibilityObject.Value}",
            $"range={SendMessage(slider.Handle, 0x401, IntPtr.Zero, IntPtr.Zero)}/{SendMessage(slider.Handle, 0x402, IntPtr.Zero, IntPtr.Zero)}",
            $"children={slider.AccessibilityObject.GetChildCount()}", $"tabStop={slider.TabStop}", $"focused={slider.Focused}" };
        File.WriteAllLines(Path.Combine(Program.Output, "slider-accessibility.txt"), semantics, new UTF8Encoding(false));
        var element = AutomationElement.FromHandle(slider.Handle);
        File.WriteAllLines(Path.Combine(Program.Output, "slider-uia.txt"), new[] {
            "role=" + element.Current.ControlType.ProgrammaticName, "name=" + element.Current.Name,
            "patterns=" + string.Join(",", element.GetSupportedPatterns().Select(p => p.ProgrammaticName)),
            "value=" + ((ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern)).Current.Value,
            "focused=" + element.Current.HasKeyboardFocus }, new UTF8Encoding(false));
        LayoutTests.Capture(form, "Controls");
        Mouse(slider, 0x201, 70, 45);
        var timings = new double[500];
        for (int i = 0; i < timings.Length; i++)
        {
            long start = Stopwatch.GetTimestamp();
            Mouse(slider, 0x200, 20 + i % 590, 45); slider.Update();
            timings[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        Mouse(slider, 0x202, 509, 45);
        Array.Sort(timings);
        string timing = $"native DPI={slider.DeviceDpi}, input-to-WM_PAINT-complete P95={timings[474]:F3} ms, max={timings.Last():F3} ms (500 events)";
        File.WriteAllText(Path.Combine(Program.Output, "slider-timing.txt"), timing, new UTF8Encoding(false));
        TestContext.WriteLine(timing);
    }
}
#endif
