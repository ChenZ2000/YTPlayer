using System.Reflection;
using System.Windows.Forms;
using YTPlayer.Core.Recognition;
using YTPlayer.Forms;
using YTPlayer.Models;

internal static class DialogTests
{
    internal static void Run()
    {
        var devices = new[] { new AudioInputDeviceInfo { DisplayName = "测试麦克风" }, new AudioInputDeviceInfo { DeviceId = "Loopback", DisplayName = "系统内录：测试设备", IsLoopback = true } };
        static IEnumerable<Control> All(Control c) => c.Controls.Cast<Control>().SelectMany(x => new[] { x }.Concat(All(x)));
        static void Escape(Form f) => typeof(Form).GetMethod("ProcessDialogKey", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(f, new object[] { Keys.Escape });
        static void Hidden(Form f) { f.StartPosition = FormStartPosition.Manual; f.Location = new System.Drawing.Point(-32000, -32000); }
        using (var idle = new SongRecognitionDialog(devices, "Loopback", (_, _, _, _) => throw new Exception("Must not start")))
        {
            Hidden(idle);
            Program.Check(idle.SelectedDevice!.IsLoopback && idle.SelectedDurationSeconds == 15, "input preference and 15-second default");
            Program.Check(All(idle).OfType<Button>().All(b => !string.IsNullOrEmpty(b.AccessibleName)), "recognition buttons accessible");
            idle.Shown += (_, _) => Escape(idle);
            Program.Check(idle.ShowDialog() == DialogResult.Cancel, "Esc closes idle recognition dialog");
        }
        bool cleaned = false;
        using (var busy = new SongRecognitionDialog(devices, null, async (_, _, token, _) =>
        {
            try { await Task.Delay(Timeout.Infinite, token); return new SongRecognitionResult("x", Array.Empty<SongInfo>(), 0); }
            finally { await Task.Delay(20); cleaned = true; }
        }))
        {
            Hidden(busy);
            busy.Shown += (_, _) =>
            {
                All(busy).OfType<Button>().Single(b => b.AccessibleName == "开始听歌识曲").PerformClick();
                Program.Check(All(busy).OfType<ComboBox>().All(b => !b.Enabled), "device and duration locked while recording");
                Escape(busy);
            };
            Program.Check(busy.ShowDialog() == DialogResult.Cancel && cleaned, "Esc during recognition waits for cancellation cleanup");
        }
        using (var late = new SongRecognitionDialog(devices, null, async (_, _, _, _) =>
        {
            await Task.Delay(30);
            return new SongRecognitionResult("x", new[] { new SongInfo { Id = "123" } }, 3);
        }))
        {
            Hidden(late);
            late.Shown += (_, _) => { All(late).OfType<Button>().Single(b => b.AccessibleName == "开始听歌识曲").PerformClick(); Escape(late); };
            Program.Check(late.ShowDialog() == DialogResult.Cancel, "late successful response cannot reopen cancelled recognition");
        }
        int attempts = 0;
        using (var retry = new SongRecognitionDialog(devices, null, (_, _, _, _) =>
        {
            if (++attempts == 1) throw new SongRecognitionException(491, "测试服务错误");
            return Task.FromResult(new SongRecognitionResult("x", new[] { new SongInfo { Id = "123" } }, 3));
        }))
        {
            Hidden(retry);
            retry.Shown += (_, _) =>
            {
                var button = All(retry).OfType<Button>().Single(b => b.AccessibleName == "开始听歌识曲");
                button.PerformClick();
                Program.Check(button.Enabled && All(retry).OfType<TextBox>().Single().Text.Contains("491"), "error remains visible and allows retry");
                using var bitmap = new System.Drawing.Bitmap(retry.Width, retry.Height);
                retry.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height));
                bitmap.Save(Path.Combine(AppContext.BaseDirectory, "recognition-dialog.png"));
                button.PerformClick();
            };
            Program.Check(retry.ShowDialog() == DialogResult.OK && retry.Result!.Matches.Count == 1 && attempts == 2, "successful retry returns result and closes");
        }
    }
}
