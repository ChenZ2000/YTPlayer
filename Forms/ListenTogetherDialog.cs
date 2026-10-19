using System;
using System.Drawing;
using System.Windows.Forms;

namespace YTPlayer
{
    public sealed class ListenTogetherDialog : Form
    {
        private readonly TextBox _hostUrlTextBox;
        private readonly Button _startHostButton;
        private readonly Button _copyUrlButton;
        private readonly Button _stopHostButton;
        private readonly TextBox _connectUrlTextBox;
        private readonly Button _connectButton;
        private readonly Button _disconnectButton;
        private readonly Label _statusLabel;
        private readonly TextBox _playbackTextBox;

        public ListenTogetherDialog()
        {
            Text = "一起听";
            AccessibleName = "一起听";
            AccessibleDescription = "使用网易云一起听房间。创建房间分享邀请，或粘贴一起听完整链接加入。按 Esc 关闭此窗口，房间保持连接。";
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            Size = new Size(700, 470);
            MinimumSize = new Size(620, 450);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(12),
                ColumnCount = 1,
                RowCount = 7
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var hostLabel = new Label
            {
                AutoSize = true,
                Text = "一起听邀请链接",
                Margin = new Padding(0, 0, 0, 4)
            };

            _hostUrlTextBox = new TextBox
            {
                ReadOnly = true,
                Dock = DockStyle.Fill,
                AccessibleName = "一起听邀请链接",
                Margin = new Padding(0, 0, 0, 8)
            };

            var hostButtonPanel = new FlowLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                Margin = new Padding(0, 0, 0, 14)
            };

            _startHostButton = new Button
            {
                AutoSize = true,
                Text = "创建一起听房间(&S)",
                AccessibleName = "创建一起听房间"
            };
            _copyUrlButton = new Button
            {
                AutoSize = true,
                Text = "复制邀请链接(&C)",
                AccessibleName = "复制一起听邀请链接"
            };
            _stopHostButton = new Button
            {
                AutoSize = true,
                Text = "退出一起听房间(&T)",
                AccessibleName = "退出一起听房间",
                Enabled = false
            };
            hostButtonPanel.Controls.Add(_startHostButton);
            hostButtonPanel.Controls.Add(_copyUrlButton);
            hostButtonPanel.Controls.Add(_stopHostButton);

            var connectLabel = new Label
            {
                AutoSize = true,
                Text = "粘贴一起听完整邀请链接",
                Margin = new Padding(0, 0, 0, 4)
            };

            _connectUrlTextBox = new TextBox
            {
                Dock = DockStyle.Fill,
                AccessibleName = "粘贴一起听完整邀请链接",
                Margin = new Padding(0, 0, 0, 8)
            };

            var connectButtonPanel = new FlowLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                Margin = new Padding(0, 0, 0, 14)
            };
            _connectButton = new Button
            {
                AutoSize = true,
                Text = "加入房间(&J)",
                AccessibleName = "加入一起听房间"
            };
            _disconnectButton = new Button
            {
                AutoSize = true,
                Text = "退出房间(&D)",
                AccessibleName = "退出一起听房间",
                Enabled = false
            };
            connectButtonPanel.Controls.Add(_connectButton);
            connectButtonPanel.Controls.Add(_disconnectButton);

            _statusLabel = new Label
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                Text = "未加入一起听房间",
                AccessibleName = "一起听状态",
                TextAlign = ContentAlignment.TopLeft
            };

            root.Controls.Add(hostLabel, 0, 0);
            root.Controls.Add(_hostUrlTextBox, 0, 1);
            root.Controls.Add(hostButtonPanel, 0, 2);

            var connectPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Margin = new Padding(0)
            };
            connectPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            connectPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            connectPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            connectPanel.Controls.Add(connectLabel, 0, 0);
            connectPanel.Controls.Add(_connectUrlTextBox, 0, 1);
            connectPanel.Controls.Add(connectButtonPanel, 0, 2);

            root.Controls.Add(connectPanel, 0, 3);
            root.Controls.Add(_statusLabel, 0, 4);
            _playbackTextBox = new TextBox
            {
                ReadOnly = true, Multiline = true, Dock = DockStyle.Fill,
                ScrollBars = ScrollBars.Vertical, AccessibleName = "房间播放状态",
                Text = "加入房间后显示歌曲、播放状态和进度。",
                Margin = new Padding(0, 8, 0, 8)
            };
            root.Controls.Add(_playbackTextBox, 0, 5);
            var closeButton = new Button
            {
                Text = "关闭（Esc）(&X)", AccessibleName = "关闭一起听窗口，保持房间连接",
                AutoSize = true, DialogResult = DialogResult.Cancel, Anchor = AnchorStyles.Right
            };
            closeButton.Click += (_, _) => Close();
            CancelButton = closeButton;
            root.Controls.Add(closeButton, 0, 6);
            Controls.Add(root);

            _startHostButton.Click += (_, _) => StartHostingRequested?.Invoke(this, EventArgs.Empty);
            _copyUrlButton.Click += (_, _) => CopyUrlRequested?.Invoke(this, EventArgs.Empty);
            _stopHostButton.Click += (_, _) => StopHostingRequested?.Invoke(this, EventArgs.Empty);
            _connectButton.Click += (_, _) => ConnectRequested?.Invoke(this, _connectUrlTextBox.Text);
            _disconnectButton.Click += (_, _) => DisconnectRequested?.Invoke(this, EventArgs.Empty);
        }

        public event EventHandler? StartHostingRequested;

        public event EventHandler? CopyUrlRequested;

        public event EventHandler? StopHostingRequested;

        public event EventHandler<string>? ConnectRequested;

        public event EventHandler? DisconnectRequested;

        public void SetSession(bool connected, bool host, bool busy, string inviteUrl)
        {
            if (_hostUrlTextBox.Text != inviteUrl) _hostUrlTextBox.Text = inviteUrl;
            _startHostButton.Enabled = !connected && !busy;
            _copyUrlButton.Enabled = connected && !busy && inviteUrl.Length > 0;
            _stopHostButton.Enabled = connected && host && !busy;
            _connectButton.Enabled = !connected && !busy;
            _connectUrlTextBox.Enabled = !connected && !busy;
            _disconnectButton.Enabled = connected && !host && !busy;
        }

        public void SetStatus(string status)
        {
            _statusLabel.Text = string.IsNullOrWhiteSpace(status) ? "未加入一起听房间" : status;
            _statusLabel.AccessibleName = "一起听状态：" + _statusLabel.Text;
        }

        public void SetPlaybackSummary(string? summary)
        {
            string text = string.IsNullOrWhiteSpace(summary) ? "加入房间后显示歌曲、播放状态和进度。" : summary;
            if (_playbackTextBox.Text == text) return;
            int start = _playbackTextBox.SelectionStart, length = _playbackTextBox.SelectionLength;
            _playbackTextBox.Text = text;
            _playbackTextBox.Select(Math.Min(start, text.Length), Math.Min(length, Math.Max(0, text.Length - start)));
        }
    }
}
