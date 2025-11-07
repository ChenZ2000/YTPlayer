using System;
using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace YTPlayer.Forms
{
    internal sealed class AboutDialog : Form
    {
        private const string ProjectUrl = "https://github.com/ChenZ2000/YTPlayer";
        private const string AuthorUrl = "https://github.com/chenz2000";
        private const string ContributorUrl = "https://github.com/zjn046";

        public AboutDialog()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            Text = "关于易听";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowIcon = false;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Font;
            KeyPreview = true;
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Regular, GraphicsUnit.Point, 134);
            ClientSize = new Size(560, 420);

            KeyDown += HandleEscKey;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Padding = new Padding(16)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var infoBox = new RichTextBox
            {
                ReadOnly = true,
                DetectUrls = true,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = SystemColors.Window,
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 10F),
                Text = BuildAboutText(),
                WordWrap = false,
                ScrollBars = RichTextBoxScrollBars.Both
            };

            var buttonPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true
            };

            var projectButton = new Button
            {
                Text = "项目主页",
                AutoSize = true
            };
            projectButton.Click += (_, __) => OpenUrl(ProjectUrl);

            var closeButton = new Button
            {
                Text = "关闭",
                AutoSize = true,
                DialogResult = DialogResult.OK
            };

            buttonPanel.Controls.Add(projectButton);
            buttonPanel.Controls.Add(closeButton);

            layout.Controls.Add(infoBox, 0, 0);
            layout.Controls.Add(buttonPanel, 0, 1);

            Controls.Add(layout);
            AcceptButton = closeButton;
        }

        private static string BuildAboutText()
        {
            var builder = new StringBuilder();
            builder.AppendLine("易听 (YTPlayer)");
            builder.AppendLine("无障碍的网易云音乐桌面客户端");
            builder.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            builder.AppendLine($"版本      : {YTPlayer.VersionInfo.Version}");
            builder.AppendLine("作者 & 贡献者");
            builder.AppendLine($"  • 作者      : ChenZ  ({AuthorUrl})");
            builder.AppendLine($"  • 贡献者    : ZJN046 ({ContributorUrl})");
            builder.AppendLine($"  • 项目主页  : {ProjectUrl}");

            return builder.ToString();
        }

        private static void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"无法打开链接：{ex.Message}", "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void HandleEscKey(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                Close();
            }
        }
    }
}
