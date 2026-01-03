using System;
using System.Drawing;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using YTPlayer.Core;
using YTPlayer.Models;

namespace YTPlayer.Forms
{
    /// <summary>
    /// 用户信息对话框 - 纯文本样式
    /// </summary>
    public partial class UserInfoForm : Form
    {
        private readonly NeteaseApiClient _apiClient;
        private readonly ConfigManager? _configManager;
        private readonly Action? _onLogout;
        private UserAccountInfo? _userInfo;

        public UserInfoForm(NeteaseApiClient apiClient, ConfigManager? configManager = null, Action? onLogout = null)
        {
            _apiClient = apiClient;
            _configManager = configManager;
            _onLogout = onLogout;
            InitializeComponent();

            // 设置窗体属性
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowIcon = false;
            this.Text = "用户信息";
            this.Size = new Size(500, 400);
            this.BackColor = SystemColors.Control;
            this.KeyPreview = true;
            this.KeyDown += UserInfoForm_KeyDown;

            // 加载用户信息
            this.Load += UserInfoForm_Load;
        }

        private void UserInfoForm_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                Close();
            }
        }

        private async void UserInfoForm_Load(object? sender, EventArgs e)
        {
            await LoadUserInfoAsync();
        }

        /// <summary>
        /// 加载用户信息
        /// </summary>
        private async Task LoadUserInfoAsync()
        {
            try
            {
                loadingLabel.Visible = true;
                infoTextBox.Visible = false;

                _userInfo = await _apiClient.GetUserAccountAsync();

                if (_userInfo != null)
                {
                    // 构建纯文本信息
                    var sb = new StringBuilder();
                    sb.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                    sb.AppendLine($"昵称: {_userInfo.Nickname ?? "未知用户"}");
                    sb.AppendLine($"用户ID: {_userInfo.UserId}");

                    // 如果是音乐人，显示艺人信息
                    if (!string.IsNullOrEmpty(_userInfo.ArtistName))
                    {
                        sb.AppendLine($"艺人名: {_userInfo.ArtistName}");
                        if (_userInfo.ArtistId.HasValue)
                        {
                            sb.AppendLine($"艺人ID: {_userInfo.ArtistId.Value}");
                        }
                    }

                    sb.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                    sb.AppendLine();

                    // 用户类型和认证
                    sb.AppendLine($"用户类型: {_userInfo.UserTypeName}");
                    if (!string.IsNullOrEmpty(_userInfo.AuthTypeDesc))
                    {
                        sb.AppendLine($"认证信息: {_userInfo.AuthTypeDesc}");
                    }
                    sb.AppendLine();

                    // 会员状态
                    sb.AppendLine($"会员状态: {_userInfo.VipTypeName}");
                    sb.AppendLine($"等级: Lv.{_userInfo.Level}");
                    sb.AppendLine($"性别: {_userInfo.GenderName}");
                    sb.AppendLine();

                    // 个性签名
                    string signature = string.IsNullOrWhiteSpace(_userInfo.Signature)
                        ? "这个人很懒，什么都没留下..."
                        : _userInfo.Signature;
                    sb.AppendLine($"个性签名: {signature}");
                    sb.AppendLine();

                    // 统计信息
                    sb.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                    sb.AppendLine("统计信息:");
                    sb.AppendLine($"  关注: {_userInfo.Follows}");
                    sb.AppendLine($"  粉丝: {_userInfo.Followers}");
                    sb.AppendLine($"  动态: {_userInfo.EventCount}");
                    sb.AppendLine($"  听歌数: {_userInfo.ListenSongs}");
                    if (_userInfo.PlaylistCount > 0)
                    {
                        sb.AppendLine($"  歌单数: {_userInfo.PlaylistCount}");
                    }
                    if (_userInfo.PlaylistBeSubscribedCount > 0)
                    {
                        sb.AppendLine($"  歌单被收藏: {_userInfo.PlaylistBeSubscribedCount}");
                    }
                    if (_userInfo.DjProgramCount > 0)
                    {
                        sb.AppendLine($"  DJ节目: {_userInfo.DjProgramCount}");
                    }
                    sb.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                    sb.AppendLine();

                    // 生日
                    if (_userInfo.Birthday.HasValue && _userInfo.Birthday.Value.Year > 1900)
                    {
                        sb.AppendLine($"生日: {_userInfo.Birthday.Value:yyyy-MM-dd}");
                    }
                    else
                    {
                        sb.AppendLine("生日: 未设置");
                    }

                    // 注册时间和天数
                    if (_userInfo.CreateTime.HasValue)
                    {
                        sb.AppendLine($"注册时间: {_userInfo.CreateTime.Value:yyyy-MM-dd HH:mm:ss}");
                    }
                    else
                    {
                        sb.AppendLine("注册时间: 未知");
                    }

                    if (_userInfo.CreateDays > 0)
                    {
                        sb.AppendLine($"注册天数: {_userInfo.CreateDays} 天");
                    }

                    // 显示信息
                    infoTextBox.Text = sb.ToString();
                    loadingLabel.Visible = false;
                    infoTextBox.Visible = true;

                    // 自动聚焦到信息文本框，方便屏幕阅读器用户
                    infoTextBox.Focus();
                    infoTextBox.Select(0, 0); // 将光标移到开头

                    System.Diagnostics.Debug.WriteLine($"[UserInfoForm] 已加载用户: {_userInfo.Nickname}");
                }
                else
                {
                    MessageBox.Show("无法获取用户信息，请稍后重试。", "提示",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    this.Close();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UserInfoForm] 异常: {ex.Message}");
                MessageBox.Show($"加载用户信息失败: {ex.Message}", "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                this.Close();
            }
        }

        /// <summary>
        /// 退出登录
        /// </summary>
        private async void logoutButton_Click(object sender, EventArgs e)
        {
            var result = MessageBox.Show(
                "确定要退出登录吗？\n\n退出后将清除所有账号信息和Cookie。",
                "确认退出",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (result != DialogResult.Yes)
            {
                return;
            }

            try
            {
                if (_apiClient != null)
                {
                    await _apiClient.LogoutAsync().ConfigureAwait(true);
                }

                ShowInfoDialog("已成功退出登录。", "提示");

                // 调用回调函数通知 MainForm 刷新本地状态
                _onLogout?.Invoke();

                this.Close();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UserInfoForm] 退出登录异常: {ex.Message}");
                MessageBox.Show($"退出登录失败: {ex.Message}", "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void closeButton_Click(object sender, EventArgs e)
        {
            this.Close();
        }

        /// <summary>
        /// 显示自绘提示对话框。
        /// </summary>
        private DialogResult ShowInfoDialog(string text, string caption)
        {
            using (var dialog = new Form())
            using (var iconBox = new PictureBox())
            using (var messageLabel = new Label())
            using (var okButton = new Button())
            using (var layout = new TableLayoutPanel())
            {
                dialog.Text = caption ?? string.Empty;
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.MaximizeBox = false;
                dialog.MinimizeBox = false;
                dialog.ShowIcon = false;
                dialog.ShowInTaskbar = false;
                dialog.Font = SystemFonts.MessageBoxFont;
                dialog.AutoSize = true;
                dialog.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                dialog.Padding = new Padding(12, 12, 12, 12);

                layout.ColumnCount = 2;
                layout.RowCount = 2;
                layout.AutoSize = true;
                layout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                layout.Dock = DockStyle.Fill;
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

                iconBox.Image = SystemIcons.Information.ToBitmap();
                iconBox.SizeMode = PictureBoxSizeMode.CenterImage;
                iconBox.Margin = new Padding(0, 0, 12, 0);
                iconBox.Width = 32;
                iconBox.Height = 32;

                messageLabel.Text = text ?? string.Empty;
                messageLabel.AutoSize = true;
                messageLabel.MaximumSize = new Size(360, 0);
                messageLabel.Margin = new Padding(0);

                okButton.Text = "确定";
                okButton.AutoSize = true;
                okButton.DialogResult = DialogResult.OK;
                okButton.Margin = new Padding(0, 12, 0, 0);

                layout.Controls.Add(iconBox, 0, 0);
                layout.SetRowSpan(iconBox, 2);
                layout.Controls.Add(messageLabel, 1, 0);
                layout.Controls.Add(okButton, 1, 1);

                dialog.Controls.Add(layout);
                dialog.AcceptButton = okButton;
                dialog.CancelButton = okButton;

                return dialog.ShowDialog(this);
            }
        }
    }
}
