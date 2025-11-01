using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using YTPlayer.Core;
using YTPlayer.Models;
using YTPlayer.Models.Auth;
using QRCoder;

namespace YTPlayer.Forms
{
    /// <summary>
    /// 登录窗体 - 支持二维码登录和短信验证码登录
    /// </summary>
    public partial class LoginForm : Form
    {
        private readonly NeteaseApiClient _apiClient;
        private CancellationTokenSource _qrCheckCancellation;
        private QrLoginSession _qrSession;
        private bool _riskLinkPromptShown;
        private int _smsCountdown;
        private System.Windows.Forms.Timer _smsTimer;

        /// <summary>
        /// 登录成功事件
        /// </summary>
        public event EventHandler<LoginSuccessEventArgs> LoginSuccess;

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

            // 初始化短信倒计时定时器
            _smsTimer = new System.Windows.Forms.Timer();
            _smsTimer.Interval = 1000; // 1秒
            _smsTimer.Tick += SmsTimer_Tick;

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

            _smsTimer?.Stop();
            System.Diagnostics.Debug.WriteLine($"[LoginForm] ========== 窗口关闭处理完成 ==========");
        }

        private void LoginTabControl_SelectedIndexChanged(object sender, EventArgs e)
        {
            // 切换到二维码登录
            if (loginTabControl.SelectedIndex == 0)
            {
                LoadQrCodeAsync();
            }
            else
            {
                // 切换到短信登录，取消二维码检查
                _qrCheckCancellation?.Cancel();
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

                string qrUrl = _qrSession.Url;
                string qrKey = _qrSession.Key;

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
            const int maxConsecutiveErrors = 3;

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
                        consecutiveErrors = 0;
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

                            UserAccountInfo userInfo = null;
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
                                AvatarUrl = userInfo?.AvatarUrl
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

        #region 短信验证码登录

        private async void sendSmsButton_Click(object sender, EventArgs e)
        {
            string countryCode = countryCodeTextBox.Text.Trim();
            string phone = phoneTextBox.Text.Trim();

            // 验证国家号
            if (string.IsNullOrEmpty(countryCode))
            {
                MessageBox.Show("请输入国家号", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (string.IsNullOrEmpty(phone))
            {
                MessageBox.Show("请输入手机号", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // 中国手机号验证（仅当国家号为86时）
            if (countryCode == "86" && (phone.Length != 11 || !phone.StartsWith("1")))
            {
                MessageBox.Show("请输入正确的中国大陆手机号（11位，以1开头）", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                sendSmsButton.Enabled = false;
                smsStatusLabel.Text = "正在发送验证码...";
                smsStatusLabel.ForeColor = Color.Gray;

                // ⭐ 修复：传递国家号参数
                bool success = await _apiClient.SendCaptchaAsync(phone, countryCode);

                if (success)
                {
                    smsStatusLabel.Text = "验证码已发送";
                    smsStatusLabel.ForeColor = Color.Green;

                    // 启动60秒倒计时（按钮保持禁用状态）
                    _smsCountdown = 60;
                    sendSmsButton.Text = $"重新发送({_smsCountdown}s)";
                    _smsTimer.Start();
                }
                else
                {
                    smsStatusLabel.Text = "发送失败，请重试";
                    smsStatusLabel.ForeColor = Color.Red;
                    sendSmsButton.Enabled = true;
                }
            }
            catch (Exception ex)
            {
                // ⭐ 修复：显示详细错误信息并记录日志
                System.Diagnostics.Debug.WriteLine($"[LoginForm] 发送验证码异常: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[LoginForm] 异常类型: {ex.GetType().Name}");
                System.Diagnostics.Debug.WriteLine($"[LoginForm] 异常堆栈: {ex.StackTrace}");

                smsStatusLabel.Text = $"发送失败: {ex.Message}";
                smsStatusLabel.ForeColor = Color.Red;
                sendSmsButton.Enabled = true;

                // ⭐ 修复：显示更友好的错误对话框
                MessageBox.Show($"发送验证码失败：\n\n{ex.Message}\n\n请检查：\n1. 网络连接是否正常\n2. 手机号和国家号是否正确\n3. 稍后重试",
                    "发送失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void SmsTimer_Tick(object sender, EventArgs e)
        {
            _smsCountdown--;

            if (_smsCountdown <= 0)
            {
                _smsTimer.Stop();
                sendSmsButton.Text = "发送验证码";
                sendSmsButton.Enabled = true;
            }
            else
            {
                sendSmsButton.Text = $"重新发送({_smsCountdown}s)";
            }
        }

        private async void smsLoginButton_Click(object sender, EventArgs e)
        {
            string countryCode = countryCodeTextBox.Text.Trim();
            string phone = phoneTextBox.Text.Trim();
            string captcha = captchaTextBox.Text.Trim();

            // 验证国家号
            if (string.IsNullOrEmpty(countryCode))
            {
                MessageBox.Show("请输入国家号", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (string.IsNullOrEmpty(phone))
            {
                MessageBox.Show("请输入手机号", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (string.IsNullOrEmpty(captcha))
            {
                MessageBox.Show("请输入验证码", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                smsLoginButton.Enabled = false;
                smsStatusLabel.Text = "正在登录...";
                smsStatusLabel.ForeColor = Color.Gray;

                // ⭐ 修复：传递国家号参数
                var result = await _apiClient.LoginByCaptchaAsync(phone, captcha, countryCode);

                if (result.Code == 200)
                {
                    smsStatusLabel.Text = "登录成功！";
                    smsStatusLabel.ForeColor = Color.Green;

                    // ⭐ 修复：刷新登录token（与二维码登录保持一致）
                    try
                    {
                        await _apiClient.RefreshLoginAsync();
                        System.Diagnostics.Debug.WriteLine("[LoginForm SMS] RefreshLoginAsync 调用成功");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[LoginForm SMS] ⚠️ RefreshLoginAsync 失败: {ex.Message}");
                        // 继续登录流程，不因刷新失败而中断
                    }

                    // ⭐ 修复：获取用户账号信息，即使失败也继续登录流程
                    UserAccountInfo userInfo = null;
                    try
                    {
                        userInfo = await _apiClient.GetUserAccountAsync();
                        System.Diagnostics.Debug.WriteLine($"[LoginForm SMS] 短信登录成功，用户昵称={userInfo?.Nickname}");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[LoginForm SMS] 获取用户信息失败: {ex.Message}");
                        System.Diagnostics.Debug.WriteLine($"[LoginForm SMS] 继续使用基本信息完成登录");

                        // 即使获取用户信息失败，也继续登录流程
                        smsStatusLabel.Text = "登录成功（部分信息加载失败）";
                    }

                    if (userInfo != null)
                    {
                        _apiClient.ApplyLoginProfile(userInfo);
                    }

                    // ⭐⭐⭐ 新增：调用 CompleteLoginAsync 进行会话预热
                    // 确保登录后立即同步Cookie并向服务器发送账户数据，避免后续风控
                    try
                    {
                        await _apiClient.CompleteLoginAsync(result);
                        System.Diagnostics.Debug.WriteLine("[LoginForm SMS] ✅ CompleteLoginAsync 调用成功");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[LoginForm SMS] ⚠️ CompleteLoginAsync 失败（不影响登录）: {ex.Message}");
                    }

                    var eventArgs = new LoginSuccessEventArgs
                    {
                        Cookie = result.Cookie,
                        UserId = result.UserId ?? "0",
                        Nickname = userInfo?.Nickname ?? result.Nickname ?? "网易云用户",
                        VipType = userInfo?.VipType ?? result.VipType,
                        AvatarUrl = userInfo?.AvatarUrl ?? result.AvatarUrl
                    };

                    await CompleteLoginAsync(eventArgs);
                }
                else
                {
                    // ⭐ 修复：登录失败时弹出对话框提示
                    string errorMessage = result.Message ?? "未知错误";
                    smsStatusLabel.Text = $"登录失败: {errorMessage}";
                    smsStatusLabel.ForeColor = Color.Red;
                    smsLoginButton.Enabled = true;

                    MessageBox.Show(
                        $"登录失败：\n\n{errorMessage}\n\n请检查：\n1. 验证码是否正确\n2. 验证码是否过期（有效期5分钟）\n3. 手机号是否正确",
                        "登录失败",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
            }
            catch (Exception ex)
            {
                // ⭐ 修复：登录异常时弹出对话框提示
                smsStatusLabel.Text = $"登录失败: {ex.Message}";
                smsStatusLabel.ForeColor = Color.Red;
                smsLoginButton.Enabled = true;

                MessageBox.Show(
                    $"登录失败：\n\n{ex.Message}\n\n请检查：\n1. 网络连接是否正常\n2. 验证码是否正确\n3. 稍后重试",
                    "登录异常",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
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
                _smsTimer?.Stop();

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
        public string Cookie { get; set; }
        public string UserId { get; set; }
        public string Nickname { get; set; }
        public int VipType { get; set; }
        public string AvatarUrl { get; set; }
    }
}
