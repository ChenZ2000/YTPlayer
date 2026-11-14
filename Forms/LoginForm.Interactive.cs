using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Forms.Automation;
using YTPlayer.Core;
using YTPlayer.Core.Auth;
using YTPlayer.Utils;

namespace YTPlayer.Forms
{
    public partial class LoginForm
    {
        private readonly TabPage _phoneTab = new TabPage("手机号登录") { Name = "phoneTabPage", AutoScroll = true };
        private readonly TabPage _weChatTab = new TabPage("微信登录") { Name = "weChatTabPage" };
        private readonly TextBox _countryCode = new TextBox { Text = "86", MaxLength = 4, AccessibleName = "国家区号，不含加号", Dock = DockStyle.Fill };
        private readonly TextBox _phone = new TextBox { MaxLength = 15, AccessibleName = "手机号", Dock = DockStyle.Fill };
        private readonly TextBox _captcha = new TextBox { MaxLength = 8, AccessibleName = "短信验证码", Dock = DockStyle.Fill };
        private readonly Button _sendCaptcha = new Button { Text = "获取验证码(&S)", AutoSize = true, TabIndex = 6 };
        private readonly Button _phoneSubmit = new Button { Text = "登录(&L)", AutoSize = true, TabIndex = 7 };
        private readonly Label _phoneStatus = new Label { Text = "输入手机号，获取短信验证码后登录。", AutoSize = true, AccessibleRole = AccessibleRole.StaticText };
        private readonly PictureBox _weChatPicture = new PictureBox { Size = new Size(280, 280), SizeMode = PictureBoxSizeMode.Zoom,
            AccessibleName = "微信登录二维码，请用手机微信扫一扫", TabStop = false };
        private const string WeChatIdleMessage = "点击“加载微信登录二维码”开始微信登录。";
        private readonly Label _weChatStatus = new Label { Text = WeChatIdleMessage, AutoSize = true,
            AccessibleRole = AccessibleRole.StaticText, TabIndex = 1 };
        private readonly Button _refreshWeChat = new Button { Text = "加载微信登录二维码(&L)",
            AccessibleName = "加载微信登录二维码", AutoSize = true, TabIndex = 0 };
        private CancellationTokenSource? _interactiveCancellation;
        private readonly System.Windows.Forms.Timer _smsTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        private const int SmsCooldownSeconds = 60;
        private long? _smsCooldownStartedAt;
        private bool _phoneBusy;
        private bool _weChatLoading;
        private bool _interactiveCompleted;

        private void InitializeInteractiveLogin()
        {
            loginTabControl.TabPages.Insert(1, _phoneTab);
            loginTabControl.TabPages.Insert(2, _weChatTab);
            var phoneLayout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(24), ColumnCount = 2 };
            phoneLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            phoneLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            AddPhoneField(phoneLayout, "国家区号(&C)", _countryCode, 0);
            AddPhoneField(phoneLayout, "手机号(&P)", _phone, 1);
            AddPhoneField(phoneLayout, "短信验证码(&V)", _captcha, 2);
            phoneLayout.Controls.Add(_sendCaptcha, 1, 3);
            phoneLayout.Controls.Add(_phoneSubmit, 1, 4);
            phoneLayout.Controls.Add(_phoneStatus, 0, 5); phoneLayout.SetColumnSpan(_phoneStatus, 2);
            _phoneTab.Controls.Add(phoneLayout);
            phoneLayout.ClientSizeChanged += (_, _) => _phoneStatus.MaximumSize = new Size(Math.Max(120, phoneLayout.ClientSize.Width - phoneLayout.Padding.Horizontal - 8), 0);
            var weChatLayout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
                WrapContents = false, AutoScroll = true, Padding = new Padding(24) };
            _weChatPicture.Visible = false;
            weChatLayout.Controls.AddRange(new Control[] { _refreshWeChat, _weChatStatus, _weChatPicture });
            _weChatTab.Controls.Add(weChatLayout);
            weChatLayout.ClientSizeChanged += (_, _) => _weChatStatus.MaximumSize = new Size(Math.Max(120, weChatLayout.ClientSize.Width - weChatLayout.Padding.Horizontal - 8), 0);
            _sendCaptcha.Click += async (_, _) => await SendPhoneCaptchaAsync();
            _phoneSubmit.Click += async (_, _) => await SubmitPhoneLoginAsync();
            _refreshWeChat.Click += async (_, _) => await LoadWeChatAsync();
            _smsTimer.Tick += (_, _) => UpdateSmsCountdown();
            _captcha.KeyDown += async (_, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.Handled = true; e.SuppressKeyPress = true; await SubmitPhoneLoginAsync(); }
            };
        }

        private static void AddPhoneField(TableLayoutPanel layout, string label, Control control, int row)
        {
            layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 10, 12, 10), TabIndex = row * 2 }, 0, row);
            control.TabIndex = row * 2 + 1; control.Margin = new Padding(3, 8, 3, 8);
            layout.Controls.Add(control, 1, row);
        }

        private CancellationToken BeginInteractiveOperation()
        {
            CancelInteractiveOperation();
            _interactiveCancellation = new CancellationTokenSource();
            return _interactiveCancellation.Token;
        }

        private void CancelInteractiveOperation()
        {
            _interactiveCancellation?.Cancel();
            _interactiveCancellation?.Dispose();
            _interactiveCancellation = null;
        }

        private bool CanUpdateInteractive(CancellationToken token) => !token.IsCancellationRequested && !IsDisposed && !Disposing && !_interactiveCompleted;

        private void SetLoginStatus(Label label, string message, bool important = false)
        {
            if (label.Text == message) return;
            label.Text = message;
            label.AccessibleName = message;
            label.AccessibilityObject.RaiseAutomationNotification(AutomationNotificationKind.Other,
                important ? AutomationNotificationProcessing.ImportantMostRecent : AutomationNotificationProcessing.MostRecent, message);
        }

        private void ResetWeChatView()
        {
            _weChatLoading = false;
            _refreshWeChat.Text = "加载微信登录二维码(&L)";
            _refreshWeChat.AccessibleName = "加载微信登录二维码";
            _refreshWeChat.Enabled = true;
            _weChatPicture.Image?.Dispose(); _weChatPicture.Image = null;
            _weChatPicture.Visible = false;
            // Tab changes reset the instructions silently; the loading announcement belongs to
            // the user's explicit button action, not to tab navigation.
            _weChatStatus.Text = WeChatIdleMessage;
            _weChatStatus.AccessibleName = WeChatIdleMessage;
        }

        private void SetPhoneBusy(bool busy)
        {
            _phoneBusy = busy;
            _countryCode.Enabled = _phone.Enabled = _captcha.Enabled = _phoneSubmit.Enabled = !busy;
            UpdateSmsCountdown();
        }

        private void UpdateSmsCountdown()
        {
            int seconds = GetSmsRemainingSeconds();
            _sendCaptcha.Enabled = !_phoneBusy && seconds == 0;
            _sendCaptcha.Text = seconds == 0 ? "获取验证码(&S)" : $"{seconds}秒后重新获取";
            if (seconds == 0) _smsTimer.Stop();
        }

        private int GetSmsRemainingSeconds() => _smsCooldownStartedAt.HasValue
            ? CalculateSmsRemainingSeconds(Stopwatch.GetElapsedTime(_smsCooldownStartedAt.Value)) : 0;

        internal static int CalculateSmsRemainingSeconds(TimeSpan elapsed)
        {
            // Bound the floating-point value before converting to int. A monotonic
            // timestamp also keeps wall-clock changes out of this short cooldown.
            return (int)Math.Ceiling(Math.Clamp(SmsCooldownSeconds - elapsed.TotalSeconds, 0d, SmsCooldownSeconds));
        }

        private async Task SendPhoneCaptchaAsync()
        {
            if (_phoneBusy || GetSmsRemainingSeconds() > 0) return;
            var token = BeginInteractiveOperation();
            try
            {
                SetPhoneBusy(true);
                LoginProtocolClient.ValidatePhone(_phone.Text.Trim(), _countryCode.Text.Trim());
                // Reserve the cooldown before dispatch: a timed-out send may still have delivered an SMS.
                _smsCooldownStartedAt = Stopwatch.GetTimestamp(); _smsTimer.Start();
                UpdateSmsCountdown();
                SetLoginStatus(_phoneStatus, "正在发送短信验证码…");
                await _apiClient.SendCaptchaAsync(_phone.Text.Trim(), _countryCode.Text.Trim(), token);
                if (!CanUpdateInteractive(token)) return;
                SetLoginStatus(_phoneStatus, "验证码已发送，请输入短信验证码。");
                _captcha.Focus();
            }
            catch (ArgumentException ex)
            { if (CanUpdateInteractive(token)) { _smsCooldownStartedAt = null; SetLoginStatus(_phoneStatus, ex.Message); } }
            catch (OperationCanceledException)
            { if (CanUpdateInteractive(token)) SetLoginStatus(_phoneStatus, "发送超时，短信可能已发出，请检查手机；稍后可重新获取。"); }
            catch (Exception ex)
            { if (CanUpdateInteractive(token)) SetLoginStatus(_phoneStatus, ex.Message); }
            finally
            { if (!IsDisposed && !Disposing) { SetPhoneBusy(false); if (CanUpdateInteractive(token)) _captcha.Focus(); } }
        }

        private async Task SubmitPhoneLoginAsync()
        {
            if (_phoneBusy || _interactiveCompleted) return;
            var token = BeginInteractiveOperation();
            try
            {
                SetPhoneBusy(true);
                SetLoginStatus(_phoneStatus, "正在验证手机号登录…");
                var result = await _apiClient.LoginByCaptchaAsync(_phone.Text.Trim(), _captcha.Text.Trim(), _countryCode.Text.Trim(), token);
                if (!CanUpdateInteractive(token)) return;
                if (result.Code != 200) { SetLoginStatus(_phoneStatus, result.Message ?? "登录失败，请重试。"); return; }
                await FinishInteractiveLoginAsync(result);
            }
            catch (OperationCanceledException)
            { if (CanUpdateInteractive(token)) SetLoginStatus(_phoneStatus, "登录请求超时，请稍后重试。"); }
            catch (Exception ex)
            { if (CanUpdateInteractive(token)) SetLoginStatus(_phoneStatus, ex.Message); }
            finally { if (!IsDisposed && !Disposing) SetPhoneBusy(false); }
        }

        private async Task LoadWeChatAsync()
        {
            if (_interactiveCompleted || _weChatLoading || loginTabControl.SelectedTab != _weChatTab) return;
            var token = BeginInteractiveOperation();
            try
            {
                _weChatLoading = true;
                _refreshWeChat.Enabled = false;
                _weChatPicture.Image?.Dispose(); _weChatPicture.Image = null;
                _weChatPicture.Visible = false;
                SetLoginStatus(_weChatStatus, "正在加载微信登录二维码", important: true);
                // Let the UI process the notification before starting the login environment.
                await Task.Yield();
                token.ThrowIfCancellationRequested();
                var session = await _apiClient.CreateWeChatLoginAsync(token);
                var bytes = await _apiClient.GetWeChatQrImageAsync(session, token);
                if (!CanUpdateInteractive(token)) return;
                using (var stream = new MemoryStream(bytes))
                using (var source = Image.FromStream(stream)) _weChatPicture.Image = new Bitmap(source);
                _weChatPicture.Visible = true;
                _weChatLoading = false;
                _refreshWeChat.Text = "刷新微信登录二维码(&R)";
                _refreshWeChat.AccessibleName = "刷新微信登录二维码";
                _refreshWeChat.Enabled = true;
                SetLoginStatus(_weChatStatus, "请使用手机微信扫一扫，并在微信中确认登录。");
                int failures = 0;
                while (CanUpdateInteractive(token))
                {
                    await Task.Delay(1500, token);
                    WeChatLoginPollResult poll;
                    try { poll = await _apiClient.PollWeChatLoginAsync(session, token); failures = 0; }
                    catch (HttpRequestException) when (++failures < 3)
                    { if (CanUpdateInteractive(token)) SetLoginStatus(_weChatStatus, "网络暂时中断，正在重新检查微信扫码状态…"); continue; }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested && ++failures < 3)
                    { continue; }
                    if (!CanUpdateInteractive(token)) return;
                    switch (poll.Status)
                    {
                        case WeChatLoginStatus.WaitingForScan:
                            SetLoginStatus(_weChatStatus, "等待微信扫码。"); break;
                        case WeChatLoginStatus.AwaitingConfirmation:
                            SetLoginStatus(_weChatStatus, "已扫码，请在微信中确认登录。"); break;
                        case WeChatLoginStatus.Authorized:
                            if (poll.Login == null) throw new InvalidOperationException("微信登录凭据未通过验证。");
                            await FinishInteractiveLoginAsync(poll.Login); return;
                        case WeChatLoginStatus.Denied:
                            SetLoginStatus(_weChatStatus, "已在微信中取消登录，请刷新二维码后重试。"); return;
                        case WeChatLoginStatus.Expired:
                            SetLoginStatus(_weChatStatus, "微信二维码已过期，请刷新二维码。"); return;
                        default:
                            SetLoginStatus(_weChatStatus, "微信扫码服务暂不可用，请刷新重试（" + poll.Code + "）。"); return;
                    }
                }
            }
            catch (OperationCanceledException)
            { if (CanUpdateInteractive(token)) SetLoginStatus(_weChatStatus, "微信请求超时，请刷新二维码。"); }
            catch (Exception ex)
            { if (CanUpdateInteractive(token)) SetLoginStatus(_weChatStatus, "微信登录失败：" + ex.Message); }
            finally
            {
                if (CanUpdateInteractive(token)) { _weChatLoading = false; _refreshWeChat.Enabled = true; }
            }
        }

        private async Task FinishInteractiveLoginAsync(LoginResult result)
        {
            if (_interactiveCompleted) return;
            _interactiveCompleted = true;
            _captcha.Clear();
            await _apiClient.CompleteLoginAsync(result);
            if (IsDisposed || Disposing) return;
            await CompleteLoginAsync(new LoginSuccessEventArgs { Cookie = result.Cookie ?? "", UserId = result.UserId ?? "",
                Nickname = result.Nickname ?? "网易云用户", AvatarUrl = result.AvatarUrl ?? "", VipType = result.VipType });
        }

        private void DisposeInteractiveLogin()
        {
            CancelInteractiveOperation(); _smsTimer.Dispose();
            _apiClient.ReleaseInteractiveLoginSessionsAsync().SafeFireAndForget("Release login sessions");
            foreach (var popup in _officialLoginPopups.ToArray()) popup.Close();
            _weChatPicture.Image?.Dispose(); _weChatPicture.Image = null;
            _qrCheckCancellation?.Cancel(); _qrCheckCancellation?.Dispose(); _qrCheckCancellation = null;
            qrPictureBox?.Image?.Dispose();
        }
    }
}
