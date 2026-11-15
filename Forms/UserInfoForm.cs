using System;
using System.Drawing;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;
using YTPlayer.Utils;
using MessageBox = YTPlayer.MessageBox;
using YTPlayer.Core;
using YTPlayer.Models;

namespace YTPlayer.Forms
{
    /// <summary>
    /// 用户信息对话框 - 语义化网页阅读区域
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
            ThemeManager.ApplyTheme(this);

            // 设置窗体属性
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowIcon = false;
            this.Text = "用户信息";
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
                infoView.Visible = false;

                _userInfo = await _apiClient.GetUserAccountAsync();
                if (IsDisposed || Disposing) return;

                if (_userInfo != null)
                {
                    infoView.SetDocument(BuildUserDocument(_userInfo));
                    loadingLabel.Visible = false;
                    infoView.Visible = true;
                    infoView.FocusDocument();

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
                if (IsDisposed || Disposing) return;
                System.Diagnostics.Debug.WriteLine($"[UserInfoForm] 异常: {ex.Message}");
                MessageBox.Show($"加载用户信息失败: {ex.Message}", "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                this.Close();
            }
        }

        internal static SemanticDocument BuildUserDocument(UserAccountInfo info)
        {
            var basic = new List<string> { $"昵称：{info.Nickname ?? "未知用户"}", $"用户 ID：{info.UserId}" };
            if (!string.IsNullOrEmpty(info.ArtistName))
            {
                basic.Add($"艺人名：{info.ArtistName}");
                if (info.ArtistId.HasValue) basic.Add($"艺人 ID：{info.ArtistId.Value}");
            }
            basic.Add($"性别：{info.GenderName}");
            var identity = new List<string> { $"用户类型：{info.UserTypeName}" };
            if (!string.IsNullOrEmpty(info.AuthTypeDesc)) identity.Add($"认证信息：{info.AuthTypeDesc}");
            identity.Add($"会员状态：{info.VipTypeName}");
            identity.Add($"等级：Lv.{info.Level}");
            var statistics = new List<string> { $"关注：{info.Follows}", $"粉丝：{info.Followers}",
                $"动态：{info.EventCount}", $"听歌数：{info.ListenSongs}" };
            if (info.PlaylistCount > 0) statistics.Add($"歌单数：{info.PlaylistCount}");
            if (info.PlaylistBeSubscribedCount > 0) statistics.Add($"歌单被收藏：{info.PlaylistBeSubscribedCount}");
            if (info.DjProgramCount > 0) statistics.Add($"DJ 节目：{info.DjProgramCount}");
            var dates = new List<string>
            {
                info.Birthday.HasValue && info.Birthday.Value.Year > 1900 ? $"生日：{info.Birthday.Value:yyyy-MM-dd}" : "生日：未设置",
                info.CreateTime.HasValue ? $"注册时间：{info.CreateTime.Value:yyyy-MM-dd HH:mm:ss}" : "注册时间：未知"
            };
            if (info.CreateDays > 0) dates.Add($"注册天数：{info.CreateDays} 天");
            return new SemanticDocumentBuilder("用户信息")
                .Heading("基本资料").List(basic)
                .Heading("身份与会员").List(identity)
                .Heading("个性签名").Paragraph(string.IsNullOrWhiteSpace(info.Signature) ? "这个人很懒，什么都没留下..." : info.Signature)
                .Heading("统计信息").List(statistics)
                .Heading("时间信息").List(dates).Build();
        }

        /// <summary>
        /// 退出登录
        /// </summary>
        private async void logoutButton_Click(object sender, EventArgs e)
        {
            var result = ShowConfirmDialog(
                "确定要退出登录吗？\n\n退出后将清除所有账号信息和Cookie。",
                "确认退出");

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

        private DialogResult ShowInfoDialog(string text, string caption)
        {
            return MessageBox.Show(this, text, caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private DialogResult ShowConfirmDialog(string text, string caption)
        {
            return MessageBox.Show(this, text, caption, MessageBoxButtons.YesNo,
                MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        }

    }
}


