using System;
using System.Drawing;
using System.Windows.Forms;
using YTPlayer.Utils;

namespace YTPlayer.Forms
{
    internal sealed class ThemedMessageBoxForm : Form
    {
        private readonly Label _iconLabel;
        private readonly Label _messageLabel;
        private readonly FlowLayoutPanel _buttonPanel;

        public ThemedMessageBoxForm(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton defaultButton)
        {
            string titleText = string.IsNullOrWhiteSpace(text)
                ? (string.IsNullOrWhiteSpace(caption) ? "提示" : caption)
                : text;
            Text = titleText;
            AccessibleName = titleText;
            AccessibleDescription = string.IsNullOrWhiteSpace(caption) ? string.Empty : caption;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(12);

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
                AutoSize = true,
                ColumnCount = 2,
                RowCount = 2
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            contentPanel.Controls.Add(layout);

            _iconLabel = new Label
            {
                AutoSize = true,
                Font = new Font(Font.FontFamily, 16f, FontStyle.Bold),
                Margin = new Padding(0, 0, 12, 0),
                Text = GetIconText(icon),
                AccessibleName = "提示图标"
            };

            if (string.IsNullOrWhiteSpace(_iconLabel.Text))
            {
                _iconLabel.Visible = false;
                _iconLabel.Margin = new Padding(0);
            }

            _messageLabel = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(520, 0),
                Text = text,
                AccessibleName = "提示内容"
            };

            layout.Controls.Add(_iconLabel, 0, 0);
            layout.Controls.Add(_messageLabel, 1, 0);

            _buttonPanel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                Dock = DockStyle.Fill,
                AutoSize = true,
                Margin = new Padding(0, 16, 0, 0)
            };
            layout.SetColumnSpan(_buttonPanel, 2);
            layout.Controls.Add(_buttonPanel, 0, 1);

            CreateButtons(buttons, defaultButton);

            ThemeManager.ApplyTheme(this);
        }

        private void CreateButtons(MessageBoxButtons buttons, MessageBoxDefaultButton defaultButton)
        {
            Button? acceptButton = null;
            Button? cancelButton = null;

            void AddButton(string text, DialogResult result, bool isCancel = false)
            {
                var button = new Button
                {
                    Text = text,
                    DialogResult = result,
                    Margin = new Padding(8, 0, 0, 0),
                    AutoSize = true,
                    AccessibleName = text
                };
                _buttonPanel.Controls.Add(button);

                if (acceptButton == null)
                {
                    acceptButton = button;
                }

                if (isCancel)
                {
                    cancelButton = button;
                }
            }

            switch (buttons)
            {
                case MessageBoxButtons.OK:
                    AddButton("确定", DialogResult.OK, isCancel: true);
                    break;
                case MessageBoxButtons.OKCancel:
                    AddButton("取消", DialogResult.Cancel, isCancel: true);
                    AddButton("确定", DialogResult.OK);
                    break;
                case MessageBoxButtons.YesNo:
                    AddButton("否", DialogResult.No, isCancel: true);
                    AddButton("是", DialogResult.Yes);
                    break;
                case MessageBoxButtons.YesNoCancel:
                    AddButton("取消", DialogResult.Cancel, isCancel: true);
                    AddButton("否", DialogResult.No);
                    AddButton("是", DialogResult.Yes);
                    break;
                case MessageBoxButtons.RetryCancel:
                    AddButton("取消", DialogResult.Cancel, isCancel: true);
                    AddButton("重试", DialogResult.Retry);
                    break;
                case MessageBoxButtons.AbortRetryIgnore:
                    AddButton("忽略", DialogResult.Ignore, isCancel: true);
                    AddButton("重试", DialogResult.Retry);
                    AddButton("终止", DialogResult.Abort);
                    break;
                default:
                    AddButton("确定", DialogResult.OK, isCancel: true);
                    break;
            }

            if (defaultButton == MessageBoxDefaultButton.Button2 && _buttonPanel.Controls.Count >= 2)
            {
                acceptButton = _buttonPanel.Controls[1] as Button;
            }
            else if (defaultButton == MessageBoxDefaultButton.Button3 && _buttonPanel.Controls.Count >= 3)
            {
                acceptButton = _buttonPanel.Controls[2] as Button;
            }

            AcceptButton = acceptButton;
            CancelButton = cancelButton ?? acceptButton;
        }

        private static string GetIconText(MessageBoxIcon icon)
        {
            switch ((int)icon)
            {
                case 16: // Hand/Error/Stop
                    return "×";
                case 48: // Exclamation/Warning
                    return "!";
                case 64: // Asterisk/Information
                    return "i";
                case 32: // Question
                    return "?";
                default:
                    return string.Empty;
            }
        }
    }
}
