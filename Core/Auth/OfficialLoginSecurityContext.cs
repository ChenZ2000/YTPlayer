using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YTPlayer.Utils;

namespace YTPlayer.Core.Auth
{
    internal sealed class LoginSecurityPreparation
    {
        internal string UserAgent { get; init; } = "";
        internal string CheckToken { get; init; } = "";
    }

    internal interface ILoginSecurityContext : IDisposable
    {
        Task<LoginSecurityPreparation> PrepareAsync(CookieContainer cookies, bool requireToken, CancellationToken token);
    }

    /// <summary>
    /// Runs the site's own Watchman instance on its real HTTPS origin. Each login flow has
    /// an isolated InPrivate profile; tokens are obtained afresh and never synthesized or cached.
    /// A dedicated STA keeps the API usable without a caller-owned WinForms message loop.
    /// </summary>
    internal sealed class OfficialLoginSecurityContext : ILoginSecurityContext
    {
        internal const string LoginBusinessId = "0b0cdd23ed1144a0b78de049edc09824";
        private static readonly Uri Music = new Uri("https://music.163.com/");
        private readonly object _sync = new object();
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly TaskCompletionSource<Control> _dispatcher = new TaskCompletionSource<Control>(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _started;
        private bool _disposed;
        private WebView2? _view;
        private Task? _initialization;

        public async Task<LoginSecurityPreparation> PrepareAsync(CookieContainer cookies, bool requireToken, CancellationToken token)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(50));
            await _gate.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                var dispatcher = await GetDispatcherAsync(timeout.Token).ConfigureAwait(false);
                var completion = new TaskCompletionSource<LoginSecurityPreparation>(TaskCreationOptions.RunContinuationsAsynchronously);
                dispatcher.BeginInvoke(new Action(async () =>
                {
                    try
                    {
                        if (_initialization?.IsFaulted == true || _initialization?.IsCanceled == true)
                        {
                            _view?.Dispose(); _view = null; _initialization = null;
                        }
                        _initialization ??= InitializeAsync(dispatcher);
                        await _initialization.WaitAsync(timeout.Token);
                        timeout.Token.ThrowIfCancellationRequested();
                        var core = _view!.CoreWebView2;
                        if (!IsMusicOrigin(core.Source)) throw new InvalidOperationException("官方登录环境已离开网易云页面，请重新打开登录窗口。");
                        var manager = core.CookieManager;
                        foreach (Cookie source in cookies.GetCookies(Music))
                        {
                            var cookie = manager.CreateCookie(source.Name, source.Value.Trim('"'), source.Domain, source.Path);
                            cookie.IsSecure = source.Secure;
                            cookie.IsHttpOnly = source.HttpOnly;
                            if (source.Expires != DateTime.MinValue) cookie.Expires = source.Expires.ToUniversalTime();
                            manager.AddOrUpdateCookie(cookie);
                        }
                        string checkToken = requireToken ? await GetCheckTokenAsync(core, timeout.Token) : "";
                        foreach (var source in await manager.GetCookiesAsync(Music.AbsoluteUri))
                        {
                            // CookieContainer requires quoted comma-containing values; do not URL-encode
                            // a server cookie, which would silently change its meaning.
                            string value = source.Value.Contains(',') ? "\"" + source.Value + "\"" : source.Value;
                            var cookie = new Cookie(source.Name, value, source.Path, source.Domain)
                            { Secure = source.IsSecure, HttpOnly = source.IsHttpOnly };
                            if (!source.IsSession) cookie.Expires = source.Expires;
                            cookies.Add(cookie);
                        }
                        completion.TrySetResult(new LoginSecurityPreparation { UserAgent = core.Settings.UserAgent, CheckToken = checkToken });
                    }
                    catch (Exception ex) { completion.TrySetException(ex); }
                }));
                // Await the dispatched operation itself, including cancellation, so its cookie writes
                // cannot race a later attempt after the semaphore is released.
                return await completion.Task.ConfigureAwait(false);
            }
            catch (WebView2RuntimeNotFoundException)
            { throw new InvalidOperationException("手机号和微信登录需要 Microsoft Edge WebView2 运行时，请安装后重试。"); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && !_lifetime.IsCancellationRequested)
            { throw new TimeoutException("加载官方登录安全组件超时，请检查网络后重试，或使用网页登录。"); }
            finally { _gate.Release(); }
        }

        private async Task<Control> GetDispatcherAsync(CancellationToken token)
        {
            lock (_sync)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(OfficialLoginSecurityContext));
                if (!_started)
                {
                    _started = true;
                    var thread = new Thread(() =>
                    {
                        try
                        {
                            using var host = new Form { ShowInTaskbar = false, Width = 1024, Height = 768 };
                            _ = host.Handle;
                            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                            _dispatcher.TrySetResult(host);
                            Application.Run();
                        }
                        catch (Exception ex) { _dispatcher.TrySetException(ex); }
                    }) { IsBackground = true, Name = "Official login browser" };
                    thread.SetApartmentState(ApartmentState.STA);
                    thread.Start();
                }
            }
            return await _dispatcher.Task.WaitAsync(token).ConfigureAwait(false);
        }

        private async Task InitializeAsync(Control host)
        {
            string folder = Path.Combine(PathHelper.LibsDirectory, "LoginSessions", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var environment = await CoreWebView2Environment.CreateAsync(null, folder);
            environment.BrowserProcessExited += (_, _) =>
            {
                // This exact per-instance directory was created above. Never clean other profiles.
                try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            };
            _lifetime.Token.ThrowIfCancellationRequested();
            var options = environment.CreateCoreWebView2ControllerOptions();
            options.IsInPrivateModeEnabled = true;
            _view = new WebView2 { Dock = DockStyle.Fill };
            host.Controls.Add(_view);
            await _view.EnsureCoreWebView2Async(environment, options);
            var core = _view.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.NavigationStarting += (_, e) => { if (!IsMusicOrigin(e.Uri)) e.Cancel = true; };
            var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnLoaded(object? sender, CoreWebView2NavigationCompletedEventArgs e)
            {
                if (e.IsSuccess) loaded.TrySetResult(true);
                else loaded.TrySetException(new InvalidOperationException("无法加载网易云官方登录环境（" + e.WebErrorStatus + "）。"));
            }
            core.NavigationCompleted += OnLoaded;
            try
            {
                core.Navigate(Music.AbsoluteUri);
                await loaded.Task.WaitAsync(TimeSpan.FromSeconds(35), _lifetime.Token);
            }
            finally { core.NavigationCompleted -= OnLoaded; }
        }

        private static bool IsMusicOrigin(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme == "https" && uri.Host == "music.163.com" && uri.IsDefaultPort;

        private static async Task<string> GetCheckTokenAsync(CoreWebView2 core, CancellationToken token)
        {
            string id = Guid.NewGuid().ToString("N");
            var completed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
            {
                if (!IsMusicOrigin(e.Source)) return;
                try
                {
                    var message = JObject.Parse(e.WebMessageAsJson);
                    if (message.Value<string>("id") != id) return;
                    string? value = message.Value<string>("token");
                    if (string.IsNullOrWhiteSpace(value))
                        completed.TrySetException(new InvalidOperationException("官网安全组件未生成登录令牌，请检查网络后重试，或使用网页登录。"));
                    else completed.TrySetResult(value);
                }
                catch (JsonException) { /* Ignore unrelated page messages. */ }
            }
            core.WebMessageReceived += OnMessage;
            try
            {
                // Use the initialized official instance. Do not alter browser fingerprints, fake
                // device identifiers, or reuse the unrelated general-purpose Watchman business ID.
                string script = "(()=>{const id=" + JsonConvert.SerializeObject(id) + ";let ticks=0;" +
                    "const timer=setInterval(()=>{if(window.WM&&typeof WM.getToken==='function'){clearInterval(timer);" +
                    "try{WM.getToken(" + JsonConvert.SerializeObject(LoginBusinessId) + ",token=>chrome.webview.postMessage({id,token}));}" +
                    "catch(_){chrome.webview.postMessage({id});}}else if(++ticks>=100){clearInterval(timer);chrome.webview.postMessage({id});}},100);})()";
                await core.ExecuteScriptAsync(script);
                return await completed.Task.WaitAsync(TimeSpan.FromSeconds(18), token);
            }
            finally { core.WebMessageReceived -= OnMessage; }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                _lifetime.Cancel();
                if (!_started) return;
            }
            _ = _dispatcher.Task.ContinueWith(task =>
            {
                if (task.Status != TaskStatus.RanToCompletion) return;
                try
                {
                    task.Result.BeginInvoke(new Action(async () =>
                    {
                        await _gate.WaitAsync();
                        try
                        {
                            if (_initialization != null) { try { await _initialization; } catch (Exception) { } }
                            _view?.Dispose();
                        }
                        finally { _gate.Release(); Application.ExitThread(); }
                    }));
                }
                catch (InvalidOperationException) { }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
