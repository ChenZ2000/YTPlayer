using System;
using System.Drawing;
using System.Windows.Forms;
using YTPlayer.Utils;

namespace YTPlayer.Forms
{
    internal sealed class DependencyInstallDialog : Form
    {
        private readonly Label _titleLabel;
        private readonly Label _statusLabel;
        private readonly ListBox _logListBox;
        private readonly Label _logPlaceholderLabel;
        private readonly Panel _logPanel;
        private readonly ThemedProgressBar _progressBar;
        private bool _allowClose;
        private string? _lastMessage;

        public DependencyInstallDialog(string title, string initialStatus)
        {
            Text = string.IsNullOrWhiteSpace(title) ? "正在准备运行环境" : title;
            AccessibleName = Text;
            AccessibleDescription = "运行时依赖安装提示";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            AutoSize = false;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(12);
            ControlBox = false;
            Size = new Size(620, 360);
            MinimumSize = new Size(560, 320);

            var palette = ThemeManager.Current;
            BackColor = palette.BaseBackground;

            var contentPanel = new Panel
            {
                BackColor = palette.SurfaceBackground,
                Dock = DockStyle.Fill,
                Padding = new Padding(16)
            };
            Controls.Add(contentPanel);

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                ColumnCount = 1,
                RowCount = 4
            };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            contentPanel.Controls.Add(layout);

            _titleLabel = new Label
            {
                AutoSize = true,
                Font = new Font(Font.FontFamily, 12f, FontStyle.Bold),
                Text = Text,
                Margin = new Padding(0, 0, 0, 8),
                AccessibleName = "安装标题"
            };
            layout.Controls.Add(_titleLabel);

            _statusLabel = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(520, 0),
                Text = string.IsNullOrWhiteSpace(initialStatus) ? "正在处理，请稍候..." : initialStatus,
                Margin = new Padding(0, 0, 0, 12),
                AccessibleName = "安装状态"
            };
            layout.Controls.Add(_statusLabel);

            _logPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 0, 0, 12)
            };
            layout.Controls.Add(_logPanel);

            _logListBox = new ListBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                IntegralHeight = false,
                AccessibleName = "安装日志"
            };
            _logPanel.Controls.Add(_logListBox);

            _logPlaceholderLabel = new Label
            {
                AutoSize = true,
                Text = "正在准备安装日志...",
                ForeColor = ThemeManager.Current.TextSecondary,
                BackColor = Color.Transparent,
                Margin = new Padding(6),
                AccessibleName = "日志提示"
            };
            _logPanel.Controls.Add(_logPlaceholderLabel);
            _logPlaceholderLabel.Location = new Point(6, 6);
            _logPlaceholderLabel.BringToFront();

            _progressBar = new ThemedProgressBar
            {
                Width = 520,
                Height = 18,
                Style = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 28,
                AccessibleName = "安装进度"
            };
            layout.Controls.Add(_progressBar);

            ThemeManager.ApplyTheme(this);

            AppendMessage(_statusLabel.Text);
        }

        public void UpdateProgress(DependencyInstallProgress progress)
        {
            if (IsDisposed || Disposing)
            {
                return;
            }

            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => UpdateProgress(progress)));
                return;
            }

            if (!string.IsNullOrWhiteSpace(progress.Message))
            {
                string message = progress.Message;
                if (progress.Percent.HasValue && !progress.IsIndeterminate && message.IndexOf('%') < 0)
                {
                    message = $"{message} ({progress.Percent.Value}%)";
                }
                _statusLabel.Text = message;
                AppendMessage(message);
            }

            if (progress.IsIndeterminate)
            {
                SetIndeterminate();
            }
            else if (progress.Percent.HasValue)
            {
                SetProgress(progress.Percent.Value);
            }

            if (progress.IsError)
            {
                _statusLabel.ForeColor = ThemeManager.Current.AccentPressed;
            }
        }

        public void SetIndeterminate()
        {
            if (_progressBar.Style != ProgressBarStyle.Marquee)
            {
                _progressBar.Style = ProgressBarStyle.Marquee;
            }
        }

        public void SetProgress(int percent)
        {
            int clamped = Math.Max(0, Math.Min(100, percent));
            if (_progressBar.Style != ProgressBarStyle.Continuous)
            {
                _progressBar.Style = ProgressBarStyle.Continuous;
            }
            _progressBar.Value = clamped;
        }

        public void AllowClose()
        {
            _allowClose = true;
        }

        private void AppendMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            if (string.Equals(message, _lastMessage, StringComparison.Ordinal))
            {
                return;
            }

            _lastMessage = message;
            _logListBox.Items.Add(message);
            _logListBox.TopIndex = _logListBox.Items.Count - 1;
            _logPlaceholderLabel.Visible = _logListBox.Items.Count == 0;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_allowClose)
            {
                e.Cancel = true;
                return;
            }
            base.OnFormClosing(e);
        }
    }
}
