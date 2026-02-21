using System;
using System.Drawing;
using System.Windows.Forms;
using YTPlayer.Utils;
namespace YTPlayer.Forms
{
    internal sealed class ThemedMessageBoxForm : Form
    {
        private const int MessageMinWidth = 360;
        private const int MessageMaxWidth = 520;
        private const int MessageMaxVisibleLines = 12;
        private readonly Label _iconLabel;
        private readonly TextBox _messageTextBox;
        private readonly FlowLayoutPanel _buttonPanel;
        private readonly ColumnStyle _messageColumnStyle;
        public ThemedMessageBoxForm(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton defaultButton)
        {
            string messageText = text ?? string.Empty;
            string titleText = string.IsNullOrWhiteSpace(caption) ? "\u63D0\u793A" : caption;
            Text = titleText;
            AccessibleName = titleText;
            AccessibleDescription = string.IsNullOrWhiteSpace(messageText) ? titleText : messageText;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(12);
            ThemePalette palette = ThemeManager.Current;
            BackColor = palette.BaseBackground;
            var contentPanel = new Panel
            {
                BackColor = palette.SurfaceBackground,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Top,
                Padding = new Padding(16)
            };
            Controls.Add(contentPanel);
            var layout = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                RowCount = 2,
                Dock = DockStyle.Top,
                Margin = Padding.Empty
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _messageColumnStyle = new ColumnStyle(SizeType.Absolute, MessageMinWidth);
            layout.ColumnStyles.Add(_messageColumnStyle);
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            contentPanel.Controls.Add(layout);
            _iconLabel = new Label
            {
                AutoSize = true,
                Font = new Font(Font.FontFamily, 16f, FontStyle.Bold),
                Margin = new Padding(0, 0, 12, 0),
                Text = GetIconText(icon),
                AccessibleName = "\u63D0\u793A\u56FE\u6807"
            };
            if (string.IsNullOrWhiteSpace(_iconLabel.Text))
            {
                _iconLabel.Visible = false;
                _iconLabel.Margin = new Padding(0);
            }
            _messageTextBox = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                HideSelection = false,
                ShortcutsEnabled = true,
                WordWrap = true,
                AutoSize = false,
                Text = messageText,
                TabStop = true,
                TabIndex = 0,
                AccessibleName = "\u63D0\u793A\u5185\u5BB9",
                AccessibleDescription = messageText,
                AccessibleRole = AccessibleRole.Text,
                Margin = Padding.Empty
            };
            _messageTextBox.KeyDown += MessageTextBox_KeyDown;
            layout.Controls.Add(_iconLabel, 0, 0);
            layout.Controls.Add(_messageTextBox, 1, 0);
            _buttonPanel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                Margin = new Padding(0, 16, 0, 0),
                Anchor = AnchorStyles.Right
            };
            layout.SetColumnSpan(_buttonPanel, 2);
            layout.Controls.Add(_buttonPanel, 0, 1);
            CreateButtons(buttons, defaultButton);
            ThemeManager.ApplyTheme(this);
            ApplyMessageTextBoxTheme(palette);
            UpdateMessageLayout();
            Shown += ThemedMessageBoxForm_Shown;
        }
        private void UpdateMessageLayout()
        {
            Size messageSize = MeasureMessageSize(_messageTextBox, MessageMinWidth, MessageMaxWidth, MessageMaxVisibleLines);
            _messageTextBox.Size = messageSize;
            _messageTextBox.MinimumSize = messageSize;
            _messageColumnStyle.Width = messageSize.Width;
        }
        private void ThemedMessageBoxForm_Shown(object? sender, EventArgs e)
        {
            if (_messageTextBox.CanFocus)
            {
                _messageTextBox.Focus();
                _messageTextBox.SelectionStart = 0;
                _messageTextBox.SelectionLength = 0;
            }
        }
        private void MessageTextBox_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && AcceptButton is IButtonControl accept)
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                accept.PerformClick();
            }
        }
        private static Size MeasureMessageSize(TextBox textBox, int minWidth, int maxWidth, int maxVisibleLines)
        {
            string content = string.IsNullOrWhiteSpace(textBox.Text) ? " " : textBox.Text;
            int targetWidth = MeasureMessageWidth(textBox, content, minWidth, maxWidth);
            TextFormatFlags wrappedFlags = TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPadding;
            TextFormatFlags singleLineFlags = TextFormatFlags.TextBoxControl | TextFormatFlags.NoPadding;
            Size measured = TextRenderer.MeasureText(content, textBox.Font, new Size(targetWidth, int.MaxValue), wrappedFlags);
            int lineHeight = TextRenderer.MeasureText("\u793A", textBox.Font, new Size(targetWidth, int.MaxValue), singleLineFlags).Height;
            int minHeight = Math.Max(lineHeight + 8, 32);
            int maxHeight = Math.Max(minHeight, lineHeight * maxVisibleLines + 8);
            int desiredHeight = Math.Max(minHeight, measured.Height + 6);
            bool needsScroll = desiredHeight > maxHeight;
            textBox.ScrollBars = needsScroll ? ScrollBars.Vertical : ScrollBars.None;
            return new Size(targetWidth, needsScroll ? maxHeight : desiredHeight);
        }
        private static int MeasureMessageWidth(TextBox textBox, string content, int minWidth, int maxWidth)
        {
            TextFormatFlags singleLineFlags = TextFormatFlags.SingleLine | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPadding;
            int width = minWidth;
            string normalized = content.Replace("\r\n", "\n");
            string[] lines = normalized.Split('\n');
            foreach (string rawLine in lines)
            {
                string line = string.IsNullOrEmpty(rawLine) ? " " : rawLine;
                int lineWidth = TextRenderer.MeasureText(line, textBox.Font, new Size(maxWidth, int.MaxValue), singleLineFlags).Width + 12;
                width = Math.Max(width, Math.Min(maxWidth, lineWidth));
                if (width >= maxWidth)
                {
                    break;
                }
            }
            return width;
        }
        private void ApplyMessageTextBoxTheme(ThemePalette palette)
        {
            _messageTextBox.BackColor = palette.SurfaceBackground;
            _messageTextBox.ForeColor = palette.TextPrimary;
            _messageTextBox.BorderStyle = BorderStyle.None;
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
                    AccessibleName = text,
                    TabStop = true,
                    TabIndex = _buttonPanel.Controls.Count + 1
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
                    AddButton("\u786E\u5B9A", DialogResult.OK, isCancel: true);
                    break;
                case MessageBoxButtons.OKCancel:
                    AddButton("\u53D6\u6D88", DialogResult.Cancel, isCancel: true);
                    AddButton("\u786E\u5B9A", DialogResult.OK);
                    break;
                case MessageBoxButtons.YesNo:
                    AddButton("\u5426", DialogResult.No, isCancel: true);
                    AddButton("\u662F", DialogResult.Yes);
                    break;
                case MessageBoxButtons.YesNoCancel:
                    AddButton("\u53D6\u6D88", DialogResult.Cancel, isCancel: true);
                    AddButton("\u5426", DialogResult.No);
                    AddButton("\u662F", DialogResult.Yes);
                    break;
                case MessageBoxButtons.RetryCancel:
                    AddButton("\u53D6\u6D88", DialogResult.Cancel, isCancel: true);
                    AddButton("\u91CD\u8BD5", DialogResult.Retry);
                    break;
                case MessageBoxButtons.AbortRetryIgnore:
                    AddButton("\u5FFD\u7565", DialogResult.Ignore, isCancel: true);
                    AddButton("\u91CD\u8BD5", DialogResult.Retry);
                    AddButton("\u7EC8\u6B62", DialogResult.Abort);
                    break;
                default:
                    AddButton("\u786E\u5B9A", DialogResult.OK, isCancel: true);
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
                    return "\u00D7";
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
