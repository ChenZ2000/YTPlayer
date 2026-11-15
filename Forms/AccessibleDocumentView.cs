using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Newtonsoft.Json;
using YTPlayer.Utils;

namespace YTPlayer.Forms
{
    /// <summary>A single native tab stop containing a real HTML document. NVDA owns browse-mode
    /// navigation; only Tab, Shift+Tab and Escape are handed back to the containing dialog.</summary>
    internal sealed class AccessibleDocumentView : UserControl, IThemeAwareControl
    {
        private readonly TextBox _fallback = new ReadOnlyTextBox { Dock = DockStyle.Fill,
            ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, TabStop = true };
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly SemaphoreSlim _renderGate = new SemaphoreSlim(1, 1);
        private WebView2? _browser;
        private SemanticDocument? _document;
        private Task? _initialization;
        private string? _renderedBody;
        private string? _themeCss;
        private bool _ready;
        private bool _failed;
        private bool _allowDocumentNavigation;
        private string? _initialDocumentUri;
        private string? _documentSource;
        private Form? _ownerForm;

        internal AccessibleDocumentView()
        {
            // WinForms treats a UserControl as a tab-navigation container. It must participate
            // in tab order so SelectNextControl can enter its one visible child in either direction.
            TabStop = true;
            Controls.Add(_fallback);
        }

        internal bool IsDocumentReady => _ready;
        internal SemanticDocument? Document => _document;
        internal event EventHandler? DocumentReady;

        internal void SetDocument(SemanticDocument document)
        {
            if (IsDisposed || Disposing) return;
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (_document?.BodyHtml == document.BodyHtml && _document.Title == document.Title) return;
            _document = document;
            AccessibleName = document.Title;
            _fallback.AccessibleName = document.Title;
            _fallback.Text = (_failed ? "网页阅读组件暂不可用，以下为文本内容。" + Environment.NewLine + Environment.NewLine : "") + document.PlainText;
            if (_browser != null) _browser.AccessibleName = document.Title;
            if (_ready) _ = RenderLatestAsync();
            else TryInitialize();
        }

        internal void FocusDocument()
        {
            if (_ready) _browser?.Focus();
            else _fallback.Focus();
        }

        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); TryInitialize(); }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); TryInitialize(); }
        protected override void OnParentChanged(EventArgs e) { base.OnParentChanged(e); TryInitialize(); }
        protected override void OnBackColorChanged(EventArgs e) { base.OnBackColorChanged(e); ApplyDocumentTheme(); }
        protected override void OnForeColorChanged(EventArgs e) { base.OnForeColorChanged(e); ApplyDocumentTheme(); }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); ApplyDocumentTheme(); }

        private void TryInitialize()
        {
            if (_document == null || _initialization != null || _failed || !Visible || !IsHandleCreated ||
                IsDisposed || Disposing || DesignMode || FindForm()?.Visible != true) return;
            _ownerForm = FindForm();
            if (_ownerForm != null) _ownerForm.FormClosed += OwnerFormClosed;
            _initialization = InitializeAsync();
        }

        private void OwnerFormClosed(object? sender, FormClosedEventArgs e) => _lifetime.Cancel();

        private async Task InitializeAsync()
        {
            try
            {
                var environment = await DocumentBrowserEnvironment.GetAsync().WaitAsync(_lifetime.Token);
                if (_lifetime.IsCancellationRequested) return;
                _browser = new WebView2 { Dock = DockStyle.Fill, TabStop = false, Visible = false, AccessibleName = _document!.Title };
                Controls.Add(_browser);
                _ = _browser.Handle;
                var options = environment.CreateCoreWebView2ControllerOptions();
                options.ProfileName = "ReadOnlyDocuments";
                options.IsInPrivateModeEnabled = true;
                await _browser.EnsureCoreWebView2Async(environment, options).WaitAsync(TimeSpan.FromSeconds(20), _lifetime.Token);
                if (_lifetime.IsCancellationRequested) return;
                var core = _browser.CoreWebView2;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsPasswordAutosaveEnabled = false;
                core.Settings.IsGeneralAutofillEnabled = false;
                core.Settings.IsWebMessageEnabled = true;
                core.Settings.AreHostObjectsAllowed = false;
                core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
                core.DownloadStarting += (_, e) => e.Cancel = true;
                core.NewWindowRequested += (_, e) => { e.Handled = true; if (e.IsUserInitiated) OpenExternalLink(e.Uri); };
                core.NavigationStarting += (_, e) =>
                {
                    // Runtime versions report NavigateToString as either about:blank or its
                    // exact data URI. Admit only the HTML generated by this control.
                    if (_allowDocumentNavigation && (e.Uri == "about:blank" || e.Uri == _initialDocumentUri)) return;
                    e.Cancel = true;
                    if (e.IsUserInitiated) OpenExternalLink(e.Uri);
                };
                // These views contain no remote assets, frames or requests, including user-supplied
                // profile text and update descriptions. Login uses a different environment entirely.
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += (_, e) =>
                {
                    e.Response = environment.CreateWebResourceResponse(null, 403, "Local document only", "Content-Type: text/plain");
                };
                core.WebMessageReceived += (_, e) =>
                {
                    if (e.Source != _documentSource || IsDisposed || Disposing) return;
                    string message;
                    try { message = e.TryGetWebMessageAsString(); } catch (ArgumentException) { return; }
                    if (message == "document:next") MoveOutside(true);
                    else if (message == "document:previous") MoveOutside(false);
                    else if (message == "document:escape") BeginInvoke(new Action(() => FindForm()?.Close()));
                };
                core.ProcessFailed += (_, _) =>
                {
                    if (!IsDisposed && !Disposing) BeginInvoke(new Action(ShowFallback));
                };
                var navigated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnNavigated(object? sender, CoreWebView2NavigationCompletedEventArgs e)
                {
                    if (e.IsSuccess) navigated.TrySetResult(true);
                    else navigated.TrySetException(new InvalidOperationException("Local document navigation failed: " + e.WebErrorStatus));
                }
                core.NavigationCompleted += OnNavigated;
                try
                {
                    _allowDocumentNavigation = true;
                    _themeCss = SemanticDocumentBuilder.ThemeCss(ThemeManager.Current, Font.SizeInPoints);
                    var initial = _document!;
                    string html = SemanticDocumentBuilder.Wrap(initial, ThemeManager.Current, Font.SizeInPoints);
                    _initialDocumentUri = "data:text/html;charset=utf-8;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(html));
                    core.NavigateToString(html);
                    await navigated.Task.WaitAsync(TimeSpan.FromSeconds(15), _lifetime.Token);
                    _documentSource = core.Source;
                    _renderedBody = initial.BodyHtml;
                }
                finally { core.NavigationCompleted -= OnNavigated; _allowDocumentNavigation = false; _initialDocumentUri = null; }
                if (_lifetime.IsCancellationRequested) return;
                bool restoreFocus = _fallback.ContainsFocus;
                _ready = true;
                _browser.TabStop = true;
                _browser.Visible = true;
                _browser.BringToFront();
                _fallback.TabStop = false;
                _fallback.Visible = false;
                await RenderLatestAsync();
                if (_lifetime.IsCancellationRequested) return;
                if (restoreFocus && (ContainsFocus || FindForm()?.ActiveControl == this)) _browser.Focus();
                if (_ready) DocumentReady?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (_lifetime.IsCancellationRequested || IsDisposed || Disposing) return;
                Trace.TraceWarning("[DocumentView] Browser unavailable: {0}", ex);
                ShowFallback();
            }
        }

        private async Task RenderLatestAsync()
        {
            try
            {
                await _renderGate.WaitAsync(_lifetime.Token);
                try
                {
                    if (!_ready || _browser?.CoreWebView2 == null || _document == null || _renderedBody == _document.BodyHtml) return;
                    var current = _document;
                    // Keep the document and focus node alive. Status changes do not create a new
                    // browser or navigate the page, and rapid identical updates are coalesced.
                    await _browser.CoreWebView2.ExecuteScriptAsync("(()=>{const y=scrollY;document.title=" + JsonConvert.SerializeObject(current.Title) +
                        ";document.getElementById('content').innerHTML=" + JsonConvert.SerializeObject(current.BodyHtml) + ";scrollTo(0,y);})()");
                    _renderedBody = current.BodyHtml;
                }
                finally { _renderGate.Release(); }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception) { if (!IsDisposed && !Disposing) ShowFallback(); }
        }

        void IThemeAwareControl.RefreshTheme() => ApplyDocumentTheme();

        private async void ApplyDocumentTheme()
        {
            if (!_ready || _browser?.CoreWebView2 == null || IsDisposed || Disposing) return;
            string css = SemanticDocumentBuilder.ThemeCss(ThemeManager.Current, Font.SizeInPoints);
            if (css == _themeCss) return;
            _themeCss = css;
            try { await _browser.CoreWebView2.ExecuteScriptAsync("document.getElementById('theme').textContent=" + JsonConvert.SerializeObject(css)); }
            catch (Exception) { /* A closing renderer must not raise an async UI exception. */ }
        }

        private void MoveOutside(bool forward)
        {
            // Post out of the WebView callback; never synchronously reenter the browser's focus APIs.
            BeginInvoke(new Action(() =>
            {
                if (!IsDisposed && !Disposing && _browser != null)
                    FindForm()?.SelectNextControl(forward ? _browser : this, forward, tabStopOnly: true, nested: true, wrap: true);
            }));
        }

        private static void OpenExternalLink(string value)
        {
            if (!SemanticDocumentBuilder.IsExternalLink(value)) return;
            try { Process.Start(new ProcessStartInfo { FileName = value, UseShellExecute = true }); }
            catch (Exception) { YTPlayer.MessageBox.Show("无法打开链接，请稍后重试。", "打开链接", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        }

        private void ShowFallback()
        {
            if (IsDisposed || Disposing) return;
            bool restoreFocus = ContainsFocus;
            _ready = false; _failed = true;
            if (_browser != null) { _browser.Visible = false; _browser.TabStop = false; }
            _fallback.Text = "网页阅读组件暂不可用，以下为文本内容。" + Environment.NewLine + Environment.NewLine + _document?.PlainText;
            _fallback.Visible = true; _fallback.TabStop = true; _fallback.BringToFront();
            if (restoreFocus) _fallback.Focus();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _lifetime.Cancel();
                if (_ownerForm != null) _ownerForm.FormClosed -= OwnerFormClosed;
                _browser?.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal static class DocumentBrowserEnvironment
    {
        private static Task<CoreWebView2Environment>? _environment;
        internal static Task<CoreWebView2Environment> GetAsync()
        {
            // All document controls are owned by the application's UI thread. Only the environment
            // is cached; each closed dialog disposes its controller and document immediately.
            if (_environment == null || _environment.IsFaulted || _environment.IsCanceled)
            {
                string directory = Path.Combine(PathHelper.LibsDirectory, "DocumentView.WebView2");
                Directory.CreateDirectory(directory);
                _environment = CoreWebView2Environment.CreateAsync(null, directory);
            }
            return _environment;
        }
    }
}
