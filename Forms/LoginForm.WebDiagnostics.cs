using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YTPlayer.Core.Auth;

namespace YTPlayer.Forms
{
    public partial class LoginForm
    {
        private readonly string _webDiagnosticFlow = Guid.NewGuid().ToString("N");
        private readonly List<Form> _officialLoginPopups = new List<Form>();

        private async Task RecordWebLoginResponseAsync(CoreWebView2WebResourceResponseReceivedEventArgs e)
        {
            if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "music.163.com") return;
            string path = uri.AbsolutePath;
            if (path != "/api/sns/authorize" && path != "/back/weichat" && path != "/back/sns" &&
                path != "/weapi/login/cellphone" && path != "/weapi/w/login/cellphone" &&
                path != "/weapi/sms/captcha/sent" && path != "/weapi/w/nuser/account/get") return;
            string? body = null;
            JObject? json = null;
            try
            {
                using var stream = await e.Response.GetContentAsync();
                if (stream != null)
                {
                    using var reader = new StreamReader(stream);
                    var buffer = new char[65536];
                    int length = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
                    body = new string(buffer, 0, length);
                    try { json = JObject.Parse(body); } catch (JsonException) { }
                }
            }
            catch (Exception) { /* Some redirects have no readable body. Record their status below. */ }
            LoginDiagnostics.Write(_webDiagnosticFlow, "official-web-response", uri, e.Response.StatusCode, response: json, body: body);
        }

        private async void WebLoginView_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            // Keep the official OAuth popup in the same browser profile. Opening the system
            // browser loses both the parent/opener relationship and this login's cookies.
            if (sender is not CoreWebView2 opener || !Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) ||
                (uri.AbsoluteUri != "about:blank" && (uri.Scheme != "https" ||
                (uri.Host != "music.163.com" && uri.Host != "open.weixin.qq.com"))))
            { e.Handled = true; return; }
            var deferral = e.GetDeferral();
            Form? popup = null;
            try
            {
                e.Handled = true;
                popup = new Form { Text = "网易云官方登录确认", Width = 720, Height = 700,
                    StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false };
                var view = new WebView2 { Dock = DockStyle.Fill, AccessibleName = "网易云官方授权页面" };
                popup.Controls.Add(view);
                _officialLoginPopups.Add(popup);
                popup.FormClosed += (_, _) => { _officialLoginPopups.Remove(popup); popup.Dispose(); };
                popup.Show(this);
                var options = opener.Environment.CreateCoreWebView2ControllerOptions();
                options.ProfileName = opener.Profile.ProfileName;
                options.IsInPrivateModeEnabled = opener.Profile.IsInPrivateModeEnabled;
                await view.EnsureCoreWebView2Async(opener.Environment, options);
                if (IsDisposed || Disposing || popup.IsDisposed) return;
                var core = view.CoreWebView2;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.WebResourceResponseReceived += WebLoginView_WebResourceResponseReceived;
                core.NewWindowRequested += (_, nested) => nested.Handled = true;
                core.WindowCloseRequested += (_, _) => popup.Close();
                e.NewWindow = core;
            }
            catch (Exception)
            {
                popup?.Close();
                if (!IsDisposed && !Disposing) UpdateWebStatus("无法打开官方授权窗口，请刷新网页登录后重试。", System.Drawing.Color.Red);
            }
            finally { deferral.Complete(); }
        }
    }
}
