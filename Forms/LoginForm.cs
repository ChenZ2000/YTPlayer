using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using YTPlayer.Core.Auth;
using YTPlayer.Core;
using YTPlayer.Models;
using YTPlayer.Models.Auth;
using QRCoder;

namespace YTPlayer.Forms
{
    /// <summary>
    /// 登录窗体 - 支持二维码、短信验证码和网页登录
    /// </summary>
    public partial class LoginForm : Form
    {
        private readonly NeteaseApiClient _apiClient;
        private CancellationTokenSource? _qrCheckCancellation;
        private QrLoginSession? _qrSession;
        private bool _riskLinkPromptShown;
        private bool _webLoginInitialized;
        private bool _webLoginProcessing;
        private bool _webLoginCompleted;
        private bool _webPreheated;
        private bool _webPreheatInProgress;
        private bool _webReloadedAfterFailure;
        private bool _webAutoLoginClicked;
        private bool _webAutoLoginRequested;
        private string? _webUserDataFolder;
        private CoreWebView2Environment? _webEnvironment;
        private const string WebLoginUrl = "https://music.163.com";
        private const string WebPreheatUrl = "https://music.163.com/favicon.ico";
        private readonly string[] _webCookieDomains =
        {
            "https://music.163.com",
            "https://interface.music.163.com",
            "https://login.music.163.com",
            "https://passport.music.163.com",
            "https://163.com"
        };

        /// <summary>
        /// 登录成功事件
        /// </summary>
        public event EventHandler<LoginSuccessEventArgs> LoginSuccess = delegate { };

        public LoginForm(NeteaseApiClient apiClient)
        {
            // ⭐ Layer 3 防护：确保 API 客户端不为 null
            if (apiClient == null)
            {
                throw new ArgumentNullException(nameof(apiClient),
                    "API客户端不能为null。登录功能需要有效的API客户端实例。");
            }

            _apiClient = apiClient;
            InitializeComponent();
            InitializeCustomComponents();
        }

        private void InitializeCustomComponents()
        {
            // 设置窗体属性
            this.Text = "登录易听";
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.Size = new Size(500, 600);
            this.KeyPreview = true;

            // 默认显示二维码登录标签页
            loginTabControl.SelectedIndex = 0;

            // 绑定事件
            this.Load += LoginForm_Load;
            this.FormClosing += LoginForm_FormClosing;
            loginTabControl.SelectedIndexChanged += LoginTabControl_SelectedIndexChanged;
        }

        private void LoginForm_Load(object sender, EventArgs e)
        {
            // 如果默认显示二维码标签页，则加载二维码
            if (loginTabControl.SelectedIndex == 0)
            {
                LoadQrCodeAsync();
            }
        }

        private void LoginForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine($"[LoginForm] ========== 窗口正在关闭 ==========");
            System.Diagnostics.Debug.WriteLine($"[LoginForm] DialogResult={this.DialogResult}");
            System.Diagnostics.Debug.WriteLine($"[LoginForm] CloseReason={e.CloseReason}");

            // 取消二维码检查任务
            if (_qrCheckCancellation != null && !_qrCheckCancellation.IsCancellationRequested)
            {
                System.Diagnostics.Debug.WriteLine($"[LoginForm] 取消轮询任务");
                _qrCheckCancellation.Cancel();
            }
            System.Diagnostics.Debug.WriteLine($"[LoginForm] ========== 窗口关闭处理完成 ==========");

            // 删除临时 WebView 用户数据目录，避免残留历史 Cookie
            try
            {
                if (!string.IsNullOrEmpty(_webUserDataFolder) && Directory.Exists(_webUserDataFolder))
                {
                    Directory.Delete(_webUserDataFolder, true);
                    System.Diagnostics.Debug.WriteLine($"[LoginForm] 已清理 WebView 用户数据目录: {_webUserDataFolder}");
                }
            }
            catch (Exception cleanEx)
            {
                System.Diagnostics.Debug.WriteLine($"[LoginForm] 清理 WebView 用户数据目录失败: {cleanEx.Message}");
            }
        }

        private void LoginTabControl_SelectedIndexChanged(object sender, EventArgs e)
        {
            // 切换到二维码登录
            if (loginTabControl.SelectedTab == qrTabPage)
            {
                LoadQrCodeAsync();
            }
            else
            {
                // 切换到短信登录，取消二维码检查
                _qrCheckCancellation?.Cancel();

                // 打开网页登录时启动 WebView
                if (loginTabControl.SelectedTab == webTabPage)
                {
                    _ = InitializeWebLoginAsync();
                }
            }
        }

        #region 二维码登录

        private async void LoadQrCodeAsync()
        {
            System.Diagnostics.Debug.WriteLine($"[LoginForm] ========== LoadQrCodeAsync开始 ==========");
            try
            {
                // 取消之前的检查任务
                _qrCheckCancellation?.Cancel();
                _qrCheckCancellation = new CancellationTokenSource();
                _riskLinkPromptShown = false;

                // 显示加载状态
                qrPictureBox.Image = null;
                qrStatusLabel.Text = "正在加载二维码...";
                qrStatusLabel.ForeColor = Color.Gray;
                refreshQrButton.Enabled = false;

                System.Diagnostics.Debug.WriteLine("[LoginForm] 调用_apiClient.CreateQrLoginSessionAsync()...");
                _qrSession = await _apiClient.CreateQrLoginSessionAsync();
                if (_qrSession == null || string.IsNullOrEmpty(_qrSession.Key))
                {
                    qrStatusLabel.Text = "获取二维码失败，请刷新重试";
                    qrStatusLabel.ForeColor = Color.Red;
                    refreshQrButton.Enabled = true;
                    return;
                }

                string qrUrl = _qrSession.Url ?? throw new InvalidOperationException("QR session URL is missing.");
                string qrKey = _qrSession.Key ?? throw new InvalidOperationException("QR session key is missing.");

                // 生成二维码图片
                using (QRCodeGenerator qrGenerator = new QRCodeGenerator())
                using (QRCodeData qrCodeData = qrGenerator.CreateQrCode(qrUrl, QRCodeGenerator.ECCLevel.Q))
                using (QRCode qrCode = new QRCode(qrCodeData))
                {
                    Bitmap qrImage = qrCode.GetGraphic(10);
                    qrPictureBox.Image = qrImage;
                }

                qrStatusLabel.Text = "请使用网易云音乐APP扫码登录";
                qrStatusLabel.ForeColor = Color.Blue;
                refreshQrButton.Enabled = true;

                // 开始轮询检查二维码状态
                System.Diagnostics.Debug.WriteLine($"[LoginForm] 开始轮询检查二维码状态...");
                await CheckQrCodeStatusAsync(qrKey, _qrCheckCancellation.Token);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LoginForm] LoadQrCodeAsync异常: {ex.Message}");
                qrStatusLabel.Text = $"加载失败: {ex.Message}";
                qrStatusLabel.ForeColor = Color.Red;
                refreshQrButton.Enabled = true;
            }
        }

        private async Task CheckQrCodeStatusAsync(string qrKey, CancellationToken cancellationToken)
        {
            System.Diagnostics.Debug.WriteLine($"[LoginForm] CheckQrCodeStatusAsync开始，qrKey={qrKey}");
            int pollCount = 0;
            int delayMs = 2000; // 初始延迟2秒
            bool userScanned = false;
            int consecutiveErrors = 0;
            const int maxConsecutiveErrors = 5;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    pollCount++;
                    System.Diagnostics.Debug.WriteLine($"[LoginForm] 轮询#{pollCount}，等待{delayMs}ms...");
                    await Task.Delay(delayMs, cancellationToken);

                    System.Diagnostics.Debug.WriteLine($"[LoginForm] 轮询#{pollCount}，调用PollQrLoginAsync...");

                    QrLoginPollResult result;
                    try
                    {
                        result = await _apiClient.PollQrLoginAsync(qrKey);
                    }
                    catch (Exception pollEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"[LoginForm] ⚠️ 轮询#{pollCount}异常: {pollEx.Message}");
                        consecutiveErrors++;
                        if (consecutiveErrors >= maxConsecutiveErrors)
                        {
                            qrStatusLabel.Text = "登录失败：服务器返回异常响应，请稍后重试或改用短信登录";
                            qrStatusLabel.ForeColor = Color.Red;
                            refreshQrButton.Enabled = true;
                            return;
                        }
                        continue;
                    }

                    System.Diagnostics.Debug.WriteLine($"[LoginForm] 轮询#{pollCount}，返回状态={result.State}, 原始Code={result.RawCode}");

                    // 将解析/解压失败（RawCode == -2）视为网络抖动，最多重试 maxConsecutiveErrors 次
                    if (result.State == QrLoginState.Error && result.RawCode == -2)
                    {
                        consecutiveErrors++;
                        if (consecutiveErrors >= maxConsecutiveErrors)
                        {
                            qrStatusLabel.Text = "登录失败：服务器返回异常响应，请稍后重试或改用短信登录";
                            qrStatusLabel.ForeColor = Color.Red;
                            refreshQrButton.Enabled = true;
                            return;
                        }

                        continue;
                    }
                    else
                    {
                        consecutiveErrors = 0;
                    }

                    switch (result.State)
                    {
                        case QrLoginState.WaitingForScan:
                            qrStatusLabel.Text = "等待扫码...";
                            qrStatusLabel.ForeColor = Color.Gray;
                            delayMs = 2000;
                            break;

                        case QrLoginState.AwaitingConfirmation:
                            qrStatusLabel.Text = "已扫码，请在手机上确认登录";
                            qrStatusLabel.ForeColor = Color.Orange;
                            if (!userScanned)
                            {
                                userScanned = true;
                                delayMs = 5000;
                            }
                            break;

                        case QrLoginState.Authorized:
                        {
                            qrStatusLabel.Text = "登录成功！";
                            qrStatusLabel.ForeColor = Color.Green;

                            await _apiClient.RefreshLoginAsync();

                            UserAccountInfo? userInfo = null;
                            try
                            {
                                userInfo = await _apiClient.GetUserAccountAsync();
                                System.Diagnostics.Debug.WriteLine($"[LoginForm QR] 用户信息获取成功: 昵称={userInfo?.Nickname}, ID={userInfo?.UserId}, VipType={userInfo?.VipType}");
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[LoginForm QR] ⚠️ 获取用户信息失败: {ex.Message}");
                                qrStatusLabel.Text = "登录成功（部分信息加载失败）";
                            }

                            if (userInfo != null)
                            {
                                _apiClient.ApplyLoginProfile(userInfo);
                            }

                            // ⭐⭐⭐ 新增：调用 CompleteLoginAsync 进行会话预热
                            // 构造 LoginResult 对象以便进行会话预热
                            try
                            {
                                var loginResult = new LoginResult
                                {
                                    Code = 200,
                                    Cookie = result.Cookie ?? _apiClient.GetCurrentCookieString(),
                                    UserId = userInfo?.UserId.ToString() ?? "0",
                                    Nickname = userInfo?.Nickname ?? "网易云用户",
                                    VipType = userInfo?.VipType ?? 0,
                                    AvatarUrl = userInfo?.AvatarUrl
                                };

                                await _apiClient.CompleteLoginAsync(loginResult);
                                System.Diagnostics.Debug.WriteLine("[LoginForm QR] ✅ CompleteLoginAsync 调用成功");
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[LoginForm QR] ⚠️ CompleteLoginAsync 失败（不影响登录）: {ex.Message}");
                            }

                            var eventArgs = new LoginSuccessEventArgs
                            {
                                Cookie = result.Cookie ?? _apiClient.GetCurrentCookieString(),
                                UserId = userInfo?.UserId.ToString() ?? "0",
                                Nickname = userInfo?.Nickname ?? "网易云用户",
                                VipType = userInfo?.VipType ?? 0,
                                AvatarUrl = userInfo?.AvatarUrl ?? string.Empty
                            };

                            await CompleteLoginAsync(eventArgs);
                            System.Diagnostics.Debug.WriteLine("[LoginForm QR] ========== 登录流程结束 ==========");
                            return;
                        }

                        case QrLoginState.Expired:
                            qrStatusLabel.Text = "二维码已过期，请刷新";
                            qrStatusLabel.ForeColor = Color.Red;
                            refreshQrButton.Enabled = true;
                            return;

                        case QrLoginState.RiskControl:
                        {
                            string friendlyMessage = BuildQrFailureMessage(result);
                            qrStatusLabel.Text = friendlyMessage;
                            qrStatusLabel.ForeColor = Color.Red;
                            refreshQrButton.Enabled = true;

                            if (!string.IsNullOrEmpty(result.RedirectUrl) && !_riskLinkPromptShown)
                            {
                                _riskLinkPromptShown = true;
                                System.Diagnostics.Debug.WriteLine($"[LoginForm] 风控提示链接: {result.RedirectUrl}");
                                try
                                {
                                    var promptResult = MessageBox.Show(
                                        $"{friendlyMessage}\n\n是否打开网易云提供的安全验证链接？",
                                        "登录受限",
                                        MessageBoxButtons.YesNo,
                                        MessageBoxIcon.Warning);
                                    if (promptResult == DialogResult.Yes)
                                    {
                                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                                        {
                                            FileName = result.RedirectUrl,
                                            UseShellExecute = true
                                        });
                                    }
                                }
                                catch (Exception openEx)
                                {
                                    System.Diagnostics.Debug.WriteLine($"[LoginForm] 打开安全验证链接失败: {openEx.Message}");
                                }
                            }
                            return;
                        }

                        case QrLoginState.Canceled:
                            qrStatusLabel.Text = "二维码登录已取消";
                            qrStatusLabel.ForeColor = Color.Red;
                            refreshQrButton.Enabled = true;
                            return;

                        case QrLoginState.Error:
                        default:
                        {
                            string friendlyMessage = BuildQrFailureMessage(result);
                            qrStatusLabel.Text = friendlyMessage;
                            qrStatusLabel.ForeColor = Color.Red;
                            refreshQrButton.Enabled = true;
                            return;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 任务被取消，正常退出
            }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    qrStatusLabel.Text = $"检查失败: {ex.Message}";
                    qrStatusLabel.ForeColor = Color.Red;
                    refreshQrButton.Enabled = true;
                }
            }
        }

        private string BuildQrFailureMessage(QrLoginPollResult result)
        {
            if (result == null)
            {
                return "登录失败：二维码状态未知";
            }

            if (!string.IsNullOrEmpty(result.Message) &&
                (result.State == QrLoginState.RiskControl || result.State == QrLoginState.Error))
            {
                return $"登录失败：{result.Message}";
            }

            switch (result.State)
            {
                case QrLoginState.Expired:
                    return "二维码已过期，请刷新后重新扫描";
                case QrLoginState.Canceled:
                    return "二维码登录已取消";
                case QrLoginState.RiskControl:
                    return "登录失败：请切换其他登录方式或升级新版本再试";
                case QrLoginState.Error:
                    if (!string.IsNullOrEmpty(result.Message))
                    {
                        return $"登录失败：{result.Message}";
                    }
                    break;
            }

            switch (result.RawCode)
            {
                case 8605:
                    return "登录失败：二维码已失效，请刷新后重新扫描";
                case 8606:
                    return "登录失败：二维码请求过于频繁，请稍后再试";
                case 8620:
                    return "登录失败：需要在手机端完成额外验证，请按照提示操作";
                case 8821:
                    return "登录失败：网易云提示需升级官方客户端或使用其他登录方式";
                default:
                    return $"登录失败：服务器返回状态码 {result.RawCode}";
            }
        }

        private void refreshQrButton_Click(object sender, EventArgs e)
        {
            LoadQrCodeAsync();
        }

        #endregion

        #region 官方网页登录

        /// <summary>
        /// 确保为 WebView2 创建隔离的、一次性使用的用户数据目录，避免沿用旧 Cookie。
        /// </summary>
        private async Task EnsureWebViewEnvironmentAsync()
        {
            if (_webEnvironment != null && webLoginView.CoreWebView2 != null)
            {
                return;
            }

            string baseDir = AppDomain.CurrentDomain.BaseDirectory ?? ".";
            string libsDir = Path.Combine(baseDir, "libs");
            _webUserDataFolder = Path.Combine(libsDir, "YTPlayer.exe.WebView2");

            Directory.CreateDirectory(libsDir);

            // 确保之前残留的数据被清理，保证干净登录
            try
            {
                // 额外清理历史默认位置（程序根目录下的 YTPlayer.exe.WebView2）
                string legacyFolder = Path.Combine(baseDir, "YTPlayer.exe.WebView2");
                if (Directory.Exists(legacyFolder))
                {
                    Directory.Delete(legacyFolder, true);
                }

                if (Directory.Exists(_webUserDataFolder))
                {
                    Directory.Delete(_webUserDataFolder, true);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebLogin] 清理旧 WebView 用户数据目录失败（忽略继续）: {ex.Message}");
            }

            Directory.CreateDirectory(_webUserDataFolder);

            _webEnvironment = await CoreWebView2Environment.CreateAsync(null, _webUserDataFolder);
            await webLoginView.EnsureCoreWebView2Async(_webEnvironment);
        }

        /// <summary>
        /// 清理当前 WebView 会话的所有浏览数据与 Cookie，保证每次登录流程干净。
        /// </summary>
        private async Task ClearWebViewDataAsync()
        {
            try
            {
                if (webLoginView.CoreWebView2 != null)
                {
                    // 清除全部浏览数据（缓存、存储、历史等）
                    await webLoginView.CoreWebView2.Profile.ClearBrowsingDataAsync(
                        CoreWebView2BrowsingDataKinds.AllProfile);

                    // 再保险地清空 Cookie
                    webLoginView.CoreWebView2.CookieManager.DeleteAllCookies();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebLogin] 清理浏览数据失败: {ex.Message}");
            }
        }

        private async Task InitializeWebLoginAsync(bool forceNavigate = false)
        {
            if (IsDisposed || Disposing)
            {
                return;
            }

            UpdateWebStatus("正在打开官方网页登录页面...", Color.Gray);

            try
            {
                if (!_webLoginInitialized)
                {
                    await EnsureWebViewEnvironmentAsync();
                    ConfigureWebViewForLogin(webLoginView.CoreWebView2);
                    _webLoginInitialized = true;
                }

                if (forceNavigate || webLoginView.Source == null)
                {
                    _webLoginCompleted = false;
                    _webLoginProcessing = false;
                    _webReloadedAfterFailure = false;
                    _webAutoLoginClicked = false;

                    await ClearWebViewDataAsync();

                    if (!_webPreheated || forceNavigate)
                    {
                        _webPreheatInProgress = true;
                        webLoginView.Source = new Uri(WebPreheatUrl);
                        UpdateWebStatus("预热登录环境...", Color.Gray);
                    }
                    else
                    {
                        webLoginView.Source = new Uri(WebLoginUrl);
                    }
                }
            }
            catch (WebView2RuntimeNotFoundException)
            {
                UpdateWebStatus("未检测到 WebView2 运行时，请安装后再试。", Color.Red);
                MessageBox.Show(
                    "未检测到 Microsoft Edge WebView2 运行时，请先安装后再使用网页登录。",
                    "缺少组件",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch (Exception ex)
            {
                UpdateWebStatus($"启动网页登录失败：{ex.Message}", Color.Red);
                System.Diagnostics.Debug.WriteLine($"[WebLogin] 初始化失败: {ex.Message}");
            }
        }

        private void ConfigureWebViewForLogin(CoreWebView2? core)
        {
            if (core == null)
            {
                return;
            }

            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsWebMessageEnabled = false;
            core.Settings.UserAgent = AuthConstants.DesktopUserAgent;

            core.WebResourceResponseReceived += WebLoginView_WebResourceResponseReceived;
            core.NavigationCompleted += WebLoginView_NavigationCompleted;
        }

        private async void WebLoginView_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            // 如果刚完成预热，立即导航正式页面
            if (_webPreheatInProgress)
            {
                _webPreheatInProgress = false;
                _webPreheated = e.IsSuccess;
                webLoginView.Source = new Uri(WebLoginUrl);
                return;
            }

            if (e.IsSuccess)
            {
                UpdateWebStatus("页面已加载，正在尝试打开登录框...", Color.Blue);

                // 首次成功加载后，确保按钮文案切换为刷新
                if (!string.Equals(webReloadButton.Text, "刷新页面", StringComparison.Ordinal))
                {
                    webReloadButton.Text = "刷新页面";
                }

                if (_webAutoLoginRequested && !_webAutoLoginClicked && webLoginView.CoreWebView2 != null)
                {
                    _webAutoLoginClicked = true;
                    _webAutoLoginRequested = false;
                    try
                    {
                        const string clickLoginScript = @"
(() => {
  if (window.__ytp_login_auto_clicking) return 'running';
  window.__ytp_login_auto_clicking = true;
  let attempts = 0;
  const selectors = [
    'a[href*=""#/login""]',
    'a[href*=""login""]',
    'a[class*=""login""]',
    '#g-topbar .login',
    'a[data-action*=""login""]',
    'button[class*=""login""]'
  ];
  const tryClick = () => {
    attempts++;
    const candidates = [];
    selectors.forEach(sel => document.querySelectorAll(sel).forEach(el => candidates.push(el)));
    document.querySelectorAll('a,button').forEach(el => {
      const t = (el.textContent || '').trim();
      if (t === '登录' || t === '登入' || t.toLowerCase() === 'login') candidates.push(el);
    });
    const btn = candidates.find(el => el instanceof HTMLElement);
    if (btn) {
      btn.focus();
      ['mouseover','mousedown','mouseup','click','keydown','keyup'].forEach(type => {
        const evt = new MouseEvent(type, { bubbles: true, cancelable: true, view: window });
        if (type === 'keydown' || type === 'keyup') {
          const kev = new KeyboardEvent(type, { key: 'Enter', code: 'Enter', bubbles: true, cancelable: true });
          btn.dispatchEvent(kev);
        } else {
          btn.dispatchEvent(evt);
        }
      });
      clearInterval(timer);
      window.__ytp_login_auto_clicking = false;
      return 'clicked';
    }
    if (attempts > 20) {
      clearInterval(timer);
      window.__ytp_login_auto_clicking = false;
      return 'not-found';
    }
    return 'waiting';
  };
  const timer = setInterval(tryClick, 350);
  return 'start';
})();";
                        await webLoginView.CoreWebView2.ExecuteScriptAsync(clickLoginScript);
                        UpdateWebStatus("已尝试自动打开登录框，正在后台等待弹窗...", Color.Blue);
                        await WaitForLoginModalAndShowAsync();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[WebLogin] 自动点击登录失败: {ex.Message}");
                        UpdateWebStatus("页面已加载，请手动点击右上角“登录”。", Color.Blue);
                        await WaitForLoginModalAndShowAsync();
                    }
                }
                else
                {
                    await WaitForLoginModalAndShowAsync();
                }
            }
            else
            {
                if (!_webReloadedAfterFailure)
                {
                    _webReloadedAfterFailure = true;
                    UpdateWebStatus("加载失败，正在自动重试...", Color.OrangeRed);
                    webLoginView.Reload();
                }
                else
                {
                    UpdateWebStatus($"登录页加载失败：{e.WebErrorStatus}，请点击“刷新页面”再试。", Color.Red);
                }
            }
        }

        private async void WebLoginView_WebResourceResponseReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
        {
            try
            {
                if (_webLoginCompleted) return;

                if (_webCookieDomains.Any(domain =>
                        e.Request.Uri.StartsWith(domain, StringComparison.OrdinalIgnoreCase)))
                {
                    await TryCaptureLoginCookiesAsync($"resp:{e.Request.Uri}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebLogin] 捕获响应失败: {ex.Message}");
            }
        }

        private async void webReloadButton_Click(object sender, EventArgs e)
        {
            // 后台加载，待流程完成再显示
            webLoginView.Visible = false;

            // 切换按钮文案为刷新
            if (!string.Equals(webReloadButton.Text, "刷新页面", StringComparison.Ordinal))
            {
                webReloadButton.Text = "刷新页面";
            }

            // 用户主动点击后，允许自动打开登录模态框
            _webAutoLoginRequested = true;

            await InitializeWebLoginAsync(forceNavigate: true);
        }

        private async Task<bool> TryCaptureLoginCookiesAsync(string hint)
        {
            if (_webLoginCompleted || _webLoginProcessing)
            {
                return false;
            }

            if (webLoginView.CoreWebView2 == null)
            {
                return false;
            }

            _webLoginProcessing = true;
            try
            {
                var manager = webLoginView.CoreWebView2.CookieManager;
                var cookieList = new List<CoreWebView2Cookie>();

                foreach (var domain in _webCookieDomains)
                {
                    try
                    {
                        var items = await manager.GetCookiesAsync(domain);
                        if (items != null && items.Count > 0)
                        {
                            cookieList.AddRange(items);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[WebLogin] 获取 {domain} Cookie 失败: {ex.Message}");
                    }
                }

                var musicUCookie = cookieList.FirstOrDefault(c => c.Name == "MUSIC_U" && !string.IsNullOrEmpty(c.Value));
                if (musicUCookie == null)
                {
                    return false;
                }

                string cookieString = BuildCookieString(cookieList);
                System.Diagnostics.Debug.WriteLine($"[WebLogin] 捕获到 MUSIC_U，来源={hint}，长度={musicUCookie.Value.Length}");

                _apiClient.SetCookieString(cookieString);
                UpdateWebStatus("已获取登录 Cookie，正在验证...", Color.DarkSlateBlue);

                try
                {
                    await _apiClient.RefreshLoginAsync();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[WebLogin] RefreshLoginAsync 失败（继续尝试）: {ex.Message}");
                }

                var status = await _apiClient.GetLoginStatusAsync();
                if (status != null && status.IsLoggedIn)
                {
                    UserAccountInfo? userInfo = status.AccountDetail;
                    try
                    {
                        if (userInfo == null)
                        {
                            userInfo = await _apiClient.GetUserAccountAsync();
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[WebLogin] 获取用户信息失败: {ex.Message}");
                    }

                    if (userInfo != null)
                    {
                        _apiClient.ApplyLoginProfile(userInfo);
                    }

                    var loginResult = new LoginResult
                    {
                        Code = 200,
                        Cookie = _apiClient.GetCurrentCookieString(),
                        UserId = userInfo?.UserId.ToString() ?? status.AccountId?.ToString() ?? "0",
                        Nickname = userInfo?.Nickname ?? status.Nickname ?? "网易云用户",
                        VipType = userInfo?.VipType ?? status.VipType,
                        AvatarUrl = userInfo?.AvatarUrl ?? status.AvatarUrl ?? string.Empty
                    };

                    try
                    {
                        await _apiClient.CompleteLoginAsync(loginResult);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[WebLogin] CompleteLoginAsync 异常: {ex.Message}");
                    }

                    UpdateWebStatus("登录成功，正在返回...", Color.Green);
                    _webLoginCompleted = true;

                    var eventArgs = new LoginSuccessEventArgs
                    {
                        Cookie = loginResult.Cookie ?? _apiClient.GetCurrentCookieString(),
                        UserId = loginResult.UserId ?? "0",
                        Nickname = loginResult.Nickname ?? "网易云用户",
                        VipType = loginResult.VipType,
                        AvatarUrl = loginResult.AvatarUrl ?? string.Empty
                    };

                    await CompleteLoginAsync(eventArgs);
                    return true;
                }

                UpdateWebStatus("尚未登录，请在下方完成手机号验证码后点击“完成登录”。", Color.DarkOrange);
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebLogin] 捕获登录信息异常: {ex.Message}");
                UpdateWebStatus($"捕获登录信息失败：{ex.Message}", Color.Red);
                return false;
            }
            finally
            {
                _webLoginProcessing = false;
            }
        }

        /// <summary>
        /// 后台等待登录模态框出现并聚焦其第一个可聚焦元素，然后再显示 WebView。
        /// </summary>
        private async Task WaitForLoginModalAndShowAsync()
        {
            if (webLoginView.CoreWebView2 == null)
            {
                webLoginView.Visible = true;
                webLoginView.Focus();
                return;
            }

            const string focusCheckScript = @"
(() => {
  const dialog = document.querySelector('[role=""dialog""], .mrc-modal-container, .ant-modal-content');
  if (!dialog) return 'none';
  const first = dialog.querySelector('input,button,a,[tabindex]');
  if (first) { first.focus(); return 'dialog-focused'; }
  if (dialog.focus) dialog.focus();
  return 'dialog';
})();";

            const string clickOtherModeScript = @"
(() => {
  const dialog = document.querySelector('[role=""dialog""], .mrc-modal-container, .ant-modal-content');
  if (!dialog) return 'no-modal';
  const texts = ['其他登录', '其他登录模式', '其他方式'];
  const target = Array.from(dialog.querySelectorAll('a,button')).find(el => {
    const t = (el.textContent || '').trim();
    return texts.some(x => t.includes(x));
  });
  if (target) {
    ['mouseover','mousedown','mouseup','click'].forEach(type => {
      const evt = new MouseEvent(type, { bubbles: true, cancelable: true, view: window });
      target.dispatchEvent(evt);
    });
    return 'clicked';
  }
  return 'not-found';
})();";

            const string isolateModalScript = @"
(() => {
  const dialog = document.querySelector('[role=""dialog""], .mrc-modal-container, .ant-modal-content');
  if (!dialog) return 'no-modal';
  const bodyKids = Array.from(document.body.children);
  bodyKids.forEach(el => { if (!el.contains(dialog) && !dialog.contains(el)) el.style.display = 'none'; });
  document.body.style.background = '#ffffff';
  return 'isolated';
})();";

            for (int i = 0; i < 20; i++)
            {
                try
                {
                    // 尝试点击“其他登录模式”，让登录方式列表直接展开
                    await webLoginView.CoreWebView2.ExecuteScriptAsync(clickOtherModeScript);

                    // 隔离只显示模态框，隐藏页面其它元素
                    await webLoginView.CoreWebView2.ExecuteScriptAsync(isolateModalScript);

                    string result = await webLoginView.CoreWebView2.ExecuteScriptAsync(focusCheckScript);
                    if (!string.IsNullOrEmpty(result) && result.Contains("dialog"))
                    {
                        break;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[WebLogin] 等待登录框异常: {ex.Message}");
                    break;
                }

                await Task.Delay(400);
            }

            // 显示 WebView，并再尝试一次聚焦以确保可见时焦点正确
            webLoginView.Visible = true;
            webLoginView.Focus();
            try
            {
                await webLoginView.CoreWebView2.ExecuteScriptAsync(isolateModalScript);
                await webLoginView.CoreWebView2.ExecuteScriptAsync(focusCheckScript);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebLogin] 显示后聚焦登录框失败: {ex.Message}");
            }
        }

        private string BuildCookieString(IEnumerable<CoreWebView2Cookie> cookies)
        {
            return string.Join("; ",
                cookies
                    .Where(c => !string.IsNullOrEmpty(c.Name))
                    .Select(c => $"{c.Name}={c.Value}")
                    .Distinct());
        }

        private void UpdateWebStatus(string text, Color? color = null)
        {
            if (IsDisposed || Disposing)
            {
                return;
            }

            if (webStatusLabel.InvokeRequired)
            {
                if (!webStatusLabel.IsHandleCreated)
                {
                    return;
                }

                webStatusLabel.Invoke(new Action(() => UpdateWebStatus(text, color)));
                return;
            }

            webStatusLabel.Text = text;
            if (color.HasValue)
            {
                webStatusLabel.ForeColor = color.Value;
            }
        }

        #endregion

        /// <summary>
        /// 统一处理登录成功后的事件派发与窗口关闭
        /// </summary>
        private async Task CompleteLoginAsync(LoginSuccessEventArgs eventArgs)
        {
            if (eventArgs == null)
            {
                return;
            }

            try
            {
                string cookieSnapshot = _apiClient.GetCurrentCookieString();
                if (!string.IsNullOrEmpty(cookieSnapshot))
                {
                    eventArgs.Cookie = cookieSnapshot;
                }

                _qrCheckCancellation?.Cancel();

                Action raiseEvent = () =>
                {
                    System.Diagnostics.Debug.WriteLine("[LoginForm] 准备触发 LoginSuccess 事件");
                    LoginSuccess?.Invoke(this, eventArgs);
                    System.Diagnostics.Debug.WriteLine("[LoginForm] LoginSuccess 事件派发完成");
                };

                if (this.InvokeRequired && this.IsHandleCreated)
                {
                    this.Invoke(raiseEvent);
                }
                else
                {
                    raiseEvent();
                }

                await Task.Delay(300);

                Action closeAction = () =>
                {
                    if (IsDisposed || Disposing)
                    {
                        System.Diagnostics.Debug.WriteLine("[LoginForm] 窗体已释放，跳过关闭操作");
                        return;
                    }

                    System.Diagnostics.Debug.WriteLine("[LoginForm] 设置 DialogResult=OK 并关闭窗体");
                    this.DialogResult = DialogResult.OK;
                    this.Close();
                };

                if (this.InvokeRequired && this.IsHandleCreated)
                {
                    this.Invoke(closeAction);
                }
                else
                {
                    closeAction();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LoginForm] CompleteLoginAsync 异常: {ex.Message}");
                if (!IsDisposed && !Disposing)
                {
                    MessageBox.Show($"登录成功但更新界面失败：{ex.Message}", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }
    }

    /// <summary>
    /// 登录成功事件参数
    /// </summary>
    public class LoginSuccessEventArgs : EventArgs
    {
        public string Cookie { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;
        public string Nickname { get; set; } = string.Empty;
        public int VipType { get; set; }
        public string AvatarUrl { get; set; } = string.Empty;
    }
}


