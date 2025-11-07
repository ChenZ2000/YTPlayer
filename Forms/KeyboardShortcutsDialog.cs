using System;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace YTPlayer.Forms
{
    internal sealed class KeyboardShortcutsDialog : Form
    {
        private readonly ShortcutEntry[] _entries =
        {
            new ShortcutEntry("播放控制", "播放 / 暂停", "空格", "切换当前曲目的播放状态"),
            new ShortcutEntry("播放控制", "上一曲 / 下一曲", "F5 / F6", "在播放队列中切换歌曲"),
            new ShortcutEntry("播放控制", "快退 / 快进", "← / →", "以 5 秒为步长调整进度"),
            new ShortcutEntry("播放控制", "任意位置跳转", "F12", "打开跳转对话框，接受[时：分：秒]格式或百分比加%格式的位置跳转"),
            new ShortcutEntry("播放控制", "切换歌词朗读", "F11", "启用或关闭屏幕阅读器的歌词朗读"),
            new ShortcutEntry("音量", "音量减 / 加", "F7 / F8", "以 2% 步长调节音量"),
            new ShortcutEntry("导航", "后退到上一视图", "Backspace", "返回上一页的浏览内容"),
            new ShortcutEntry("导航", "隐藏到托盘", "Shift + Esc", "最小化到托盘并保持后台播放"),
            new ShortcutEntry("搜索体验", "执行搜索", "Enter", "在关键词或类型组合框内按下回车"),
            new ShortcutEntry("搜索体验", "焦点保护 / 取消", "Esc", "保持焦点在编辑控件或关闭对话框")
        };

        public KeyboardShortcutsDialog()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            Text = "快捷键";
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
                DetectUrls = false,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = SystemColors.Window,
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 10F),
                WordWrap = false,
                ScrollBars = RichTextBoxScrollBars.Both,
                Text = BuildShortcutText()
            };

            var closeButton = new Button
            {
                Text = "关闭",
                AutoSize = true,
                DialogResult = DialogResult.OK
            };

            var buttonPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true
            };
            buttonPanel.Controls.Add(closeButton);

            layout.Controls.Add(infoBox, 0, 0);
            layout.Controls.Add(buttonPanel, 0, 1);

            Controls.Add(layout);
            AcceptButton = closeButton;
        }

        private string BuildShortcutText()
        {
            var builder = new StringBuilder();
            builder.AppendLine("键盘快捷方式参考");
            builder.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            builder.AppendLine("提示：在任意对话框内按 ESC 也可立即关闭当前窗口。");
            builder.AppendLine();

            var grouped = _entries.GroupBy(e => e.Category);
            foreach (var group in grouped)
            {
                builder.AppendLine(group.Key);
                foreach (var entry in group)
                {
                    builder.AppendLine($"  {entry.Shortcut.PadRight(12)} {entry.Action}");
                    builder.AppendLine($"      {entry.Description}");
                }
                builder.AppendLine();
            }

            builder.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            return builder.ToString();
        }

        private void HandleEscKey(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                Close();
            }
        }

        private readonly struct ShortcutEntry
        {
            public ShortcutEntry(string category, string action, string shortcut, string description)
            {
                Category = category;
                Action = action;
                Shortcut = shortcut;
                Description = description;
            }

            public string Category { get; }
            public string Action { get; }
            public string Shortcut { get; }
            public string Description { get; }
        }
    }
}
