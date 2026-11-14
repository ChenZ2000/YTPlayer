using System.Reflection;
using YTPlayer.Core;
using YTPlayer.Core.Auth;
using YTPlayer.Forms;

internal static partial class Program
{
    private static void InteractiveCapture(string mode, bool smoke = false)
    {
        if (mode != "Both" && mode != "Phone" && mode != "WeChat" && mode != "Official") throw new ArgumentException("Invalid capture mode.");
        string[] modes = mode == "Both" ? new[] { "Phone", "WeChat" } : new[] { mode };
        foreach (string item in modes)
        {
            using var api = new NeteaseApiClient();
            using var form = new LoginForm(api);
            string caption = item == "Phone" ? "手机号" : item == "WeChat" ? "微信" : "官方网页";
            form.Text = "易听登录诊断 — " + caption;
            var tabs = form.Controls.OfType<TabControl>().Single();
            bool completed = false;
            string flow = Guid.NewGuid().ToString("N");
            LoginDiagnostics.Write(flow, "capture-start-" + item);
            form.LoginSuccess += (_, _) =>
            {
                completed = true;
                LoginDiagnostics.Write(flow, "capture-login-completed", success: true);
            };
            using var closeTimer = new System.Windows.Forms.Timer { Interval = 750 };
            closeTimer.Tick += (_, _) => { closeTimer.Stop(); form.Close(); };
            using var qrTimer = new System.Windows.Forms.Timer { Interval = 250 };
            System.Drawing.Image? lastQr = null;
            string? qrOutput = Environment.GetEnvironmentVariable("YTPLAYER_AUTH_OUTPUT");
            qrTimer.Tick += (_, _) =>
            {
                var qr = GetField<PictureBox>(form, "_weChatPicture").Image;
                if (qr == null || ReferenceEquals(qr, lastQr) || string.IsNullOrWhiteSpace(qrOutput)) return;
                Directory.CreateDirectory(qrOutput);
                qr.Save(Path.Combine(qrOutput, "wechat-qr.png"), System.Drawing.Imaging.ImageFormat.Png);
                lastQr = qr;
                Console.WriteLine("微信二维码已就绪；会话保持在线，等待扫码确认。");
            };
            form.Shown += (_, _) =>
            {
                tabs.SelectedIndex = item == "Phone" ? 1 : item == "WeChat" ? 2 : 3;
                if (item == "Official")
                {
                    typeof(LoginForm).GetField("_webAutoLoginRequested", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, true);
                }
                if (smoke) closeTimer.Start();
                if (item == "WeChat" && !string.IsNullOrWhiteSpace(qrOutput)) qrTimer.Start();
            };
            Console.WriteLine(caption + "诊断窗口已打开。请完成登录；遇到错误可切到网页登录对照，关闭窗口后继续下一项。");
            if (item == "WeChat") Console.WriteLine("请先点击“加载微信登录二维码”，等待加载完成后扫码确认。");
            form.ShowDialog();
            LoginDiagnostics.Write(flow, "capture-window-closed-" + item, success: completed);
            Console.WriteLine(caption + (completed ? "：登录窗口已确认成功。" : "：窗口已关闭，未完成登录。"));
        }
        Console.WriteLine("脱敏协议日志：" + LoginDiagnostics.LogPath);
    }
}
