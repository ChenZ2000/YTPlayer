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

        public ListenTogetherDialog()
        {
            Text = "一起听";
            AccessibleName = "一起听";
            AccessibleDescription = "复制本机一起听 URL，或粘贴别人分享的 URL 连接同步听歌。";
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            Size = new Size(620, 310);
            MinimumSize = new Size(560, 280);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(12),
                ColumnCount = 1,
                RowCount = 5
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var hostLabel = new Label
            {
                AutoSize = true,
                Text = "本机一起听 URL",
                Margin = new Padding(0, 0, 0, 4)
            };

            _hostUrlTextBox = new TextBox
            {
                ReadOnly = true,
                Dock = DockStyle.Fill,
                AccessibleName = "本机一起听 URL",
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
                Text = "开始本机一起听(&S)",
                AccessibleName = "开始本机一起听"
            };
            _copyUrlButton = new Button
            {
                AutoSize = true,
                Text = "复制 URL(&C)",
                AccessibleName = "复制本机一起听 URL"
            };
            _stopHostButton = new Button
            {
                AutoSize = true,
                Text = "停止本机一起听(&T)",
                AccessibleName = "停止本机一起听",
                Enabled = false
            };
            hostButtonPanel.Controls.Add(_startHostButton);
            hostButtonPanel.Controls.Add(_copyUrlButton);
            hostButtonPanel.Controls.Add(_stopHostButton);

            var connectLabel = new Label
            {
                AutoSize = true,
                Text = "连接别人的一起听 URL",
                Margin = new Padding(0, 0, 0, 4)
            };

            _connectUrlTextBox = new TextBox
            {
                Dock = DockStyle.Fill,
                AccessibleName = "连接别人的一起听 URL",
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
                Text = "连接(&J)",
                AccessibleName = "连接一起听"
            };
            _disconnectButton = new Button
            {
                AutoSize = true,
                Text = "断开连接(&D)",
                AccessibleName = "断开一起听连接",
                Enabled = false
            };
            connectButtonPanel.Controls.Add(_connectButton);
            connectButtonPanel.Controls.Add(_disconnectButton);

            _statusLabel = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Fill,
                Text = "未开始",
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

        public void SetHostUrl(string? url)
        {
            _hostUrlTextBox.Text = url ?? string.Empty;
            _copyUrlButton.Enabled = !string.IsNullOrWhiteSpace(url);
            _stopHostButton.Enabled = !string.IsNullOrWhiteSpace(url);
        }

        public void SetConnected(bool connected)
        {
            _connectButton.Enabled = !connected;
            _disconnectButton.Enabled = connected;
        }

        public void SetStatus(string status)
        {
            _statusLabel.Text = string.IsNullOrWhiteSpace(status) ? "未开始" : status;
        }
    }
}
