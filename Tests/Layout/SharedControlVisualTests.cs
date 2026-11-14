using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using NUnit.Framework;

[TestFixture, Apartment(ApartmentState.STA)]
public class SharedControlVisualTests
{
    private static Assembly VisualAssembly =>
#if NET48
        typeof(YTPlayer.Update.UpdateDownloadProgress).Assembly;
#else
        typeof(YTPlayer.MainForm).Assembly;
#endif
    private static readonly BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static void Invoke(Control control, string name, params object[] args) => typeof(Control).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(control, args);

    [Test]
    public void MarqueeStopsWhenHiddenDisabledOrContinuous()
    {
        using var form = new Form();
        using var progress = (Control)Activator.CreateInstance(VisualAssembly.GetType("YTPlayer.Utils.ThemedProgressBar", true)!, true)!;
        form.Controls.Add(progress); progress.SetBounds(20, 20, 200, 25);
        var type = progress.GetType();
        type.GetProperty("Style")!.SetValue(progress, ProgressBarStyle.Marquee);
        form.Show(); Application.DoEvents();
        bool Running() => ((System.Windows.Forms.Timer?)type.GetField("_marqueeTimer", Instance)!.GetValue(progress))?.Enabled == true;
        Assert.That(Running(), Is.True);
        int visibilityEvents = 0;
        progress.VisibleChanged += (_, _) => visibilityEvents++;
        form.Hide(); Assert.That(Running(), Is.False, $"form={form.Visible}, progress={progress.Visible}, visibility events={visibilityEvents}");
        form.Show(); Assert.That(Running(), Is.True);
        progress.Enabled = false; Assert.That(Running(), Is.False);
        progress.Enabled = true; Assert.That(Running(), Is.True);
        type.GetProperty("MarqueeAnimationSpeed")!.SetValue(progress, 0); Assert.That(Running(), Is.False);
        type.GetProperty("MarqueeAnimationSpeed")!.SetValue(progress, 30); Assert.That(Running(), Is.True);
        type.GetProperty("Style")!.SetValue(progress, ProgressBarStyle.Continuous); Assert.That(Running(), Is.False);
        type.GetProperty("Value")!.SetValue(progress, 47);
        VisualAssembly.GetType("YTPlayer.Utils.ThemeManager", true)!.GetMethod("ApplyTheme")!.Invoke(null, new object[] { form });
        LayoutTests.Capture(form, "Progress-continuous-" + Environment.Version.Major);
        progress.Dispose(); Assert.That(Running(), Is.False);
    }

    [Test]
    public void ExistingButtonsKeepDefaultCancelAndAccessibleSemantics()
    {
        using var form = new Form { ClientSize = new Size(540, 280) };
        var primary = new Button { Text = "播放(&P)", Tag = "Primary", Bounds = new Rectangle(20, 20, 180, 80), TabIndex = 0 };
        var secondary = new Button { Text = "取消(&C)", Bounds = new Rectangle(220, 20, 180, 80), DialogResult = DialogResult.Cancel, TabIndex = 1 };
        form.Controls.Add(primary); form.Controls.Add(secondary);
        form.AcceptButton = primary; form.CancelButton = secondary;
        form.Show();
        var theme = VisualAssembly.GetType("YTPlayer.Utils.ThemeManager", true)!;
        var scheme = VisualAssembly.GetType("YTPlayer.Utils.ThemeScheme", true)!;
        foreach (object value in Enum.GetValues(scheme))
        {
            theme.GetMethod("SetScheme")!.Invoke(null, new[] { value });
            theme.GetMethod("ApplyTheme")!.Invoke(null, new object[] { form });
            foreach (string state in new[] { "normal", "hover", "pressed", "focus", "disabled" })
            {
                primary.Enabled = true;
                secondary.Focus();
                Invoke(primary, "OnMouseLeave", EventArgs.Empty);
                Invoke(primary, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, 20, 20, 0));
                if (state == "hover") Invoke(primary, "OnMouseEnter", EventArgs.Empty);
                if (state == "pressed") Invoke(primary, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 20, 20, 0));
                if (state == "focus") primary.Focus();
                if (state == "disabled") primary.Enabled = false;
                LayoutTests.Capture(form, "Button-" + value + "-" + state + "-" + Environment.Version.Major);
            }
        }
        primary.Enabled = true;
        Assert.That(primary.Height, Is.GreaterThanOrEqualTo(32 * primary.DeviceDpi / 96f));
        Assert.That(primary.AccessibilityObject.Role, Is.EqualTo(AccessibleRole.PushButton));
        Assert.That(form.AcceptButton, Is.SameAs(primary));
        Assert.That(form.CancelButton, Is.SameAs(secondary));
        int clicks = 0; primary.Click += (_, _) => clicks++;
        primary.Focus();
        typeof(Form).GetMethod("ProcessDialogKey", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, new object[] { Keys.Enter });
        Assert.That(clicks, Is.EqualTo(1));
        typeof(Form).GetMethod("ProcessDialogKey", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, new object[] { Keys.Escape });
        Assert.That(form.DialogResult, Is.EqualTo(DialogResult.Cancel));
    }
}
