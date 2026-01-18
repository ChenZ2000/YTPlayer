using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using MessageBox = YTPlayer.MessageBox;
using YTPlayer.Utils;

namespace YTPlayer.Forms
{
    internal sealed class AboutDialog : Form
    {
        private static readonly Lazy<AboutDocument> Document = new(() => AboutDocument.Load(), isThreadSafe: true);

        public AboutDialog()
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
                DetectUrls = true,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = SystemColors.Window,
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 10F),
                Text = document.BuildDisplayText(),
                WordWrap = false,
                ScrollBars = RichTextBoxScrollBars.Both
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

        private sealed class AboutDocument
        {
            private static readonly Regex TokenRegex = new(@"\{\{(?<token>[A-Za-z0-9_]+)\}\}", RegexOptions.Compiled);
            private static readonly Regex InlineLinkRegex = new(@"\[(?<text>[^\]]+)\]\((?<url>[^)]+)\)", RegexOptions.Compiled);

            private AboutDocument(string windowTitle, IReadOnlyList<string> bodyLines, IReadOnlyList<ButtonSpec> buttons)
            {
                WindowTitle = windowTitle;
                BodyLines = bodyLines;
                Buttons = buttons;
            }

            public string WindowTitle { get; }
            public IReadOnlyList<string> BodyLines { get; }
            public IReadOnlyList<ButtonSpec> Buttons { get; }

            public static AboutDocument Load()
            {
                try
                {
                    var markdown = DocumentationLoader.ReadMarkdown("Docs.About.md");
                    return Parse(markdown);
                }
                catch (Exception ex)
                {
                    return new AboutDocument(
                        "关于易听",
                        new[]
                        {
                            "易听 (YTPlayer)",
                            "无法加载关于文档。",
                            ex.Message
                        },
                        new[] { ButtonSpec.Close("关闭") });
                }
            }

            public string BuildDisplayText()
            {
                var builder = new StringBuilder();
                foreach (var line in BodyLines)
                {
                    builder.AppendLine(line);
                }

                return builder.ToString();
            }

            private static AboutDocument Parse(string markdown)
            {
                var lines = ReadAllLines(markdown);
                var metadata = ExtractMetadata(lines, out var startIndex);
                var replacements = BuildReplacements(metadata);
                var bodyLines = new List<string>();
                var buttons = new List<ButtonSpec>();
                var heading = "关于易听";
                var inButtonsSection = false;

                for (var i = startIndex; i < lines.Count; i++)
                {
                    var rawLine = lines[i];
                    var trimmed = rawLine.Trim();

                    if (string.IsNullOrWhiteSpace(trimmed))
                    {
                        if (!inButtonsSection)
                        {
                            bodyLines.Add(string.Empty);
                        }
                        continue;
                    }

                    if (trimmed.StartsWith("# ", StringComparison.Ordinal))
                    {
                        heading = trimmed.Substring(2).Trim();
                        continue;
                    }

                    if (trimmed.StartsWith("## Buttons", StringComparison.OrdinalIgnoreCase))
                    {
                        inButtonsSection = true;
                        continue;
                    }

                    if (inButtonsSection)
                    {
                        if (TryParseLinkLine(trimmed, out var text, out var target))
                        {
                            buttons.Add(ParseButton(text, target, replacements));
                        }
                        continue;
                    }

                    var resolved = ResolveTokens(rawLine, replacements);
                    var normalized = NormalizeLine(ReplaceInlineLinks(resolved));
                    bodyLines.Add(normalized);
                }

                if (bodyLines.Count == 0)
                {
                    bodyLines.Add("易听 (YTPlayer)");
                    bodyLines.Add("无障碍的网易云音乐桌面客户端");
                }

                if (buttons.Count == 0)
                {
                    buttons.Add(ButtonSpec.Close("关闭"));
                }

                return new AboutDocument(heading, bodyLines, buttons);
            }

            private static IReadOnlyDictionary<string, string> BuildReplacements(IDictionary<string, string> metadata)
            {
                var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Version"] = YTPlayer.VersionInfo.Version,
                    ["ProjectUrl"] = "https://github.com/ChenZ2000/YTPlayer",
                    ["AuthorName"] = "ChenZ",
                    ["AuthorUrl"] = "https://github.com/ChenZ2000",
                    ["ContributorName"] = "ZJN046",
                    ["ContributorUrl"] = "https://github.com/zjn046"
                };

                foreach (var pair in metadata)
                {
                    replacements[pair.Key] = pair.Value;
                }

                return replacements;
            }

            private static List<string> ReadAllLines(string markdown)
            {
                var lines = new List<string>();
                using var reader = new System.IO.StringReader(markdown);
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    lines.Add(line);
                }

                return lines;
            }

            private static IDictionary<string, string> ExtractMetadata(IList<string> lines, out int startIndex)
            {
                var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var index = 0;

                while (index < lines.Count && string.IsNullOrWhiteSpace(lines[index]))
                {
                    index++;
                }

                if (index < lines.Count && lines[index].Trim() == "---")
                {
                    index++;
                    while (index < lines.Count)
                    {
                        var current = lines[index].Trim();
                        if (current == "---")
                        {
                            index++;
                            break;
                        }

                        if (current.Length == 0)
                        {
                            index++;
                            continue;
                        }

                        var separatorIndex = current.IndexOf(':');
                        if (separatorIndex > 0)
                        {
                            var key = current.Substring(0, separatorIndex).Trim();
                            var value = current.Substring(separatorIndex + 1).Trim();
                            if (key.Length > 0)
                            {
                                metadata[key] = value;
                            }
                        }

                        index++;
                    }
                }

                while (index < lines.Count && string.IsNullOrWhiteSpace(lines[index]))
                {
                    index++;
                }

                startIndex = index;
                return metadata;
            }

            private static string ResolveTokens(string text, IReadOnlyDictionary<string, string> replacements)
            {
                return TokenRegex.Replace(text, match =>
                {
                    var token = match.Groups["token"].Value;
                    return replacements.TryGetValue(token, out var value) ? value : match.Value;
                });
            }

            private static string ReplaceInlineLinks(string text)
            {
                return InlineLinkRegex.Replace(text, match =>
                {
                    var linkText = match.Groups["text"].Value;
                    var url = match.Groups["url"].Value;
                    return $"{linkText} ({url})";
                });
            }

            private static string NormalizeLine(string text)
            {
                var trimmed = text.TrimEnd();
                if (trimmed.Length == 0)
                {
                    return string.Empty;
                }

                if (IsHorizontalRule(trimmed))
                {
                    return "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━";
                }

                if (trimmed.StartsWith("- ", StringComparison.Ordinal))
                {
                    return $"• {trimmed.Substring(2).Trim()}";
                }

                if (trimmed.StartsWith("> ", StringComparison.Ordinal))
                {
                    return trimmed.Substring(2).Trim();
                }

                if (trimmed.StartsWith("## ", StringComparison.Ordinal))
                {
                    return trimmed.Substring(3).Trim();
                }

                return trimmed;
            }

            private static bool IsHorizontalRule(string text)
            {
                if (text.Length < 3)
                {
                    return false;
                }

                foreach (var ch in text)
                {
                    if (ch != '-')
                    {
                        return false;
                    }
                }

                return true;
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

            private static ButtonSpec ParseButton(string text, string target, IReadOnlyDictionary<string, string> replacements)
            {
                var resolvedText = ResolveTokens(text, replacements);
                var resolvedTarget = ResolveTokens(target, replacements);

                if (resolvedTarget.Equals("action:close", StringComparison.OrdinalIgnoreCase))
                {
                    return ButtonSpec.Close(resolvedText);
                }

                return ButtonSpec.OpenUrl(resolvedText, resolvedTarget);
            }
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
    }
}
