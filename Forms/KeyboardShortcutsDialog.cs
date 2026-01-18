using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using MessageBox = YTPlayer.MessageBox;
using YTPlayer.Utils;

namespace YTPlayer.Forms
{
    internal sealed class KeyboardShortcutsDialog : Form
    {
        private static readonly Lazy<ShortcutDocument> Document = new(() => ShortcutDocument.Load(), isThreadSafe: true);

        public KeyboardShortcutsDialog()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            var document = Document.Value;

            Text = document.WindowTitle;
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
                Text = document.BuildDisplayText()
            };

            var buttonPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true
            };
            Button? acceptButton = null;
            Button? fallbackButton = null;
            foreach (var buttonSpec in document.Buttons)
            {
                var button = CreateButton(buttonSpec);
                buttonPanel.Controls.Add(button);
                fallbackButton ??= button;
                if (acceptButton is null && buttonSpec.Behavior.Type == ButtonBehaviorType.Close)
                {
                    acceptButton = button;
                }
            }

            if (fallbackButton is null)
            {
                var button = CreateButton(ButtonSpec.Close("关闭"));
                buttonPanel.Controls.Add(button);
                acceptButton = button;
                fallbackButton = button;
            }

            layout.Controls.Add(infoBox, 0, 0);
            layout.Controls.Add(buttonPanel, 0, 1);

            Controls.Add(layout);
            AcceptButton = acceptButton ?? fallbackButton;
        }

        private Button CreateButton(ButtonSpec spec)
        {
            var button = new Button
            {
                Text = spec.Text,
                AutoSize = true
            };

            switch (spec.Behavior.Type)
            {
                case ButtonBehaviorType.Close:
                    button.DialogResult = DialogResult.OK;
                    break;
                case ButtonBehaviorType.OpenUrl when !string.IsNullOrWhiteSpace(spec.Behavior.Target):
                    button.Click += (_, __) => OpenUrl(spec.Behavior.Target!);
                    break;
            }

            return button;
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

        private sealed class ShortcutDocument
        {
            private ShortcutDocument(string windowTitle, string? tip, IReadOnlyList<ShortcutGroup> groups, IReadOnlyList<ButtonSpec> buttons)
            {
                WindowTitle = windowTitle;
                Tip = string.IsNullOrWhiteSpace(tip) ? null : tip;
                Groups = groups;
                Buttons = buttons;
            }

            public string WindowTitle { get; }
            public string? Tip { get; }
            public IReadOnlyList<ShortcutGroup> Groups { get; }
            public IReadOnlyList<ButtonSpec> Buttons { get; }

            public static ShortcutDocument Load()
            {
                try
                {
                    var markdown = DocumentationLoader.ReadMarkdown("Docs.KeyboardShortcuts.md");
                    return Parse(markdown);
                }
                catch (Exception ex)
                {
                    var fallbackGroup = new ShortcutGroup("文档状态", new[]
                    {
                        new ShortcutEntry("请检查 Docs/KeyboardShortcuts.md 是否存在。", "-", ex.Message)
                    });

                    return new ShortcutDocument(
                        "快捷键",
                        "无法加载快捷键文档，列出了错误详情以便快速处理。",
                        new[] { fallbackGroup },
                        new[] { ButtonSpec.Close("关闭") });
                }
            }

            public string BuildDisplayText()
            {
                var builder = new StringBuilder();
                builder.AppendLine(WindowTitle);
                builder.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");

                if (!string.IsNullOrWhiteSpace(Tip))
                {
                    builder.AppendLine(Tip);
                    builder.AppendLine();
                }

                foreach (var group in Groups)
                {
                    builder.AppendLine(group.Name);

                    foreach (var entry in group.Entries)
                    {
                        builder.AppendLine($"  {entry.Shortcut.PadRight(18)} {entry.Action}");
                        builder.AppendLine($"      {entry.Description}");
                    }

                    builder.AppendLine();
                }

                builder.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                return builder.ToString();
            }

            private static ShortcutDocument Parse(string markdown)
            {
                var title = "快捷键";
                var tipBuilder = new StringBuilder();
                var groups = new List<ShortcutGroup>();
                var buttons = new List<ButtonSpec>();
                ShortcutGroup? currentGroup = null;
                var inButtonsSection = false;
                var titleInitialized = false;

                foreach (var rawLine in ReadLines(markdown))
                {
                    var trimmed = rawLine.Trim();
                    if (string.IsNullOrWhiteSpace(trimmed))
                    {
                        continue;
                    }

                    if (!titleInitialized && trimmed.StartsWith("# ", StringComparison.Ordinal))
                    {
                        title = trimmed.Substring(2).Trim();
                        titleInitialized = true;
                        continue;
                    }

                    if (trimmed.StartsWith("> ", StringComparison.Ordinal))
                    {
                        if (tipBuilder.Length > 0)
                        {
                            tipBuilder.AppendLine();
                        }

                        tipBuilder.Append(trimmed.Substring(2).Trim());
                        continue;
                    }

                    if (trimmed.StartsWith("## Buttons", StringComparison.OrdinalIgnoreCase))
                    {
                        inButtonsSection = true;
                        currentGroup = null;
                        continue;
                    }

                    if (trimmed.StartsWith("### ", StringComparison.Ordinal))
                    {
                        inButtonsSection = false;
                        currentGroup = new ShortcutGroup(trimmed.Substring(4).Trim(), new List<ShortcutEntry>());
                        groups.Add(currentGroup);
                        continue;
                    }

                    if (inButtonsSection)
                    {
                        if (TryParseLinkLine(trimmed, out var text, out var target))
                        {
                            buttons.Add(ParseButton(text, target));
                        }

                        continue;
                    }

                    if (trimmed.StartsWith("- ", StringComparison.Ordinal) && currentGroup is not null)
                    {
                        var entry = ParseEntry(trimmed.Substring(2).Trim());
                        if (entry is not null)
                        {
                            currentGroup.Entries.Add(entry);
                        }
                    }
                }

                if (groups.Count == 0)
                {
                    var fallback = new ShortcutGroup("快捷方式", new List<ShortcutEntry>
                    {
                        new ShortcutEntry("暂无数据", "-", "Docs/KeyboardShortcuts.md 中没有任何条目。")
                    });
                    groups.Add(fallback);
                }

                if (buttons.Count == 0)
                {
                    buttons.Add(ButtonSpec.Close("关闭"));
                }

                return new ShortcutDocument(title, tipBuilder.ToString(), groups, buttons);
            }

            private static ShortcutEntry? ParseEntry(string payload)
            {
                var parts = payload.Split(new[] { '|' }, StringSplitOptions.None);
                if (parts.Length < 3)
                {
                    return null;
                }

                var action = parts[0].Trim();
                var shortcut = parts[1].Trim();
                var description = string.Join(" | ", parts, 2, parts.Length - 2).Trim();
                return new ShortcutEntry(action, shortcut, description);
            }

            private static IEnumerable<string> ReadLines(string markdown)
            {
                using var reader = new System.IO.StringReader(markdown);
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    yield return line;
                }
            }
        }

        private sealed class ShortcutGroup
        {
            public ShortcutGroup(string name, IList<ShortcutEntry> entries)
            {
                Name = name;
                Entries = entries;
            }

            public string Name { get; }
            public IList<ShortcutEntry> Entries { get; }
        }

        private sealed class ShortcutEntry
        {
            public ShortcutEntry(string action, string shortcut, string description)
            {
                Action = action;
                Shortcut = shortcut;
                Description = description;
            }

            public string Action { get; }
            public string Shortcut { get; }
            public string Description { get; }
        }

        private sealed class ButtonSpec
        {
            public ButtonSpec(string text, ButtonBehavior behavior)
            {
                Text = text;
                Behavior = behavior;
            }

            public string Text { get; }
            public ButtonBehavior Behavior { get; }

            public static ButtonSpec Close(string text) => new(text, ButtonBehavior.Close());

            public static ButtonSpec OpenUrl(string text, string url) =>
                new(text, ButtonBehavior.OpenUrl(url));
        }

        private sealed class ButtonBehavior
        {
            private ButtonBehavior(ButtonBehaviorType type, string? target)
            {
                Type = type;
                Target = target;
            }

            public ButtonBehaviorType Type { get; }
            public string? Target { get; }

            public static ButtonBehavior Close() => new(ButtonBehaviorType.Close, null);

            public static ButtonBehavior OpenUrl(string url) => new(ButtonBehaviorType.OpenUrl, url);
        }

        private enum ButtonBehaviorType
        {
            Close,
            OpenUrl
        }

        private static bool TryParseLinkLine(string line, out string text, out string target)
        {
            text = string.Empty;
            target = string.Empty;

            if (!line.StartsWith("- [", StringComparison.Ordinal))
            {
                return false;
            }

            var closingBracketIndex = line.IndexOf("](", StringComparison.Ordinal);
            if (closingBracketIndex <= 3)
            {
                return false;
            }

            var closingParenIndex = line.LastIndexOf(')');
            if (closingParenIndex <= closingBracketIndex + 1)
            {
                return false;
            }

            text = line.Substring(3, closingBracketIndex - 3).Trim();
            target = line.Substring(closingBracketIndex + 2, closingParenIndex - closingBracketIndex - 2).Trim();
            return true;
        }

        private static ButtonSpec ParseButton(string text, string target)
        {
            if (target.Equals("action:close", StringComparison.OrdinalIgnoreCase))
            {
                return ButtonSpec.Close(text);
            }

            return ButtonSpec.OpenUrl(text, target);
        }
    }
}
