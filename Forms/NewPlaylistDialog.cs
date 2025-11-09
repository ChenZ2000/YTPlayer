using System;
using System.Drawing;
using System.Windows.Forms;

namespace YTPlayer.Forms
{
    /// <summary>
    /// 用于输入新歌单名称的对话框。
    /// </summary>
    public class NewPlaylistDialog : Form
    {
        private readonly Label _promptLabel;
        private readonly TextBox _nameTextBox;
        private readonly Button _confirmButton;
        private readonly Button _cancelButton;

        /// <summary>
        /// 获取用户输入的歌单名称（已自动Trim）。
        /// </summary>
        public string PlaylistName => (_nameTextBox.Text ?? string.Empty).Trim();

        public NewPlaylistDialog(string? initialName = null)
        {
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(420, 150);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowIcon = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            Text = "新建歌单";

            _promptLabel = new Label
            {
                AutoSize = true,
                Text = "请输入新歌单的名称：",
                Location = new Point(15, 20)
            };

            _nameTextBox = new TextBox
            {
                Location = new Point(18, 46),
                Width = 380,
                MaxLength = 80
            };
            _nameTextBox.TextChanged += (_, __) => UpdateConfirmState();

            _confirmButton = new Button
            {
                Text = "确认",
                DialogResult = DialogResult.OK,
                Location = new Point(236, 96),
                Size = new Size(80, 30)
            };
            _confirmButton.Click += ConfirmButton_Click;

            _cancelButton = new Button
            {
                Text = "取消",
                DialogResult = DialogResult.Cancel,
                Location = new Point(322, 96),
                Size = new Size(80, 30)
            };

            Controls.Add(_promptLabel);
            Controls.Add(_nameTextBox);
            Controls.Add(_confirmButton);
            Controls.Add(_cancelButton);

            AcceptButton = _confirmButton;
            CancelButton = _cancelButton;

            if (!string.IsNullOrWhiteSpace(initialName))
            {
                _nameTextBox.Text = initialName;
                var textLength = _nameTextBox.Text?.Length ?? 0;
                _nameTextBox.SelectionStart = textLength;
            }

            UpdateConfirmState();
        }

        private void ConfirmButton_Click(object? sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(PlaylistName))
            {
                DialogResult = DialogResult.None;
                return;
            }

            DialogResult = DialogResult.OK;
            Close();
        }

        private void UpdateConfirmState()
        {
            _confirmButton.Enabled = !string.IsNullOrWhiteSpace(_nameTextBox.Text);
        }
    }
}
