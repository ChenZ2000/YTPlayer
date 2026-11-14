using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace YTPlayer.Utils
{
    internal sealed class SemanticDocument
    {
        internal SemanticDocument(string title, string html, string plainText)
        { Title = title; BodyHtml = html; PlainText = plainText; }
        internal string Title { get; }
        internal string BodyHtml { get; }
        internal string PlainText { get; }
    }

    /// <summary>Builds semantic local documents. All caller-supplied text is encoded; raw HTML
    /// and external resources are never accepted. No Markdown or browser dependency is added.</summary>
    internal sealed class SemanticDocumentBuilder
    {
        private readonly string _title;
        private readonly StringBuilder _html = new StringBuilder();
        private readonly StringBuilder _text = new StringBuilder();
        private int _heading;
        private static readonly Regex InlinePattern = new Regex(
            @"\[(?<label>[^\]\r\n]+)\]\((?<href>[^\s)]+)\)|`(?<code>[^`\r\n]+)`|\*\*(?<strong>[^*\r\n]+)\*\*|(?<url>https?://[^\s<>()]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

        internal SemanticDocumentBuilder(string title)
        {
            _title = title;
            _html.Append("<h1 id=\"document-title\">").Append(Encode(title)).Append("</h1>");
            _text.AppendLine(title).AppendLine();
        }

        internal SemanticDocumentBuilder Heading(string title, int level = 2)
        {
            level = Math.Clamp(level, 2, 6);
            _html.Append("<h").Append(level).Append(" id=\"section-").Append(++_heading).Append("\">")
                .Append(Encode(title)).Append("</h").Append(level).Append('>');
            _text.AppendLine().AppendLine(title);
            return this;
        }

        internal SemanticDocumentBuilder Paragraph(string? text, bool markdown = false)
        {
            if (string.IsNullOrWhiteSpace(text)) return this;
            _html.Append("<p>").Append(Inline(text, markdown).Replace("\r\n", "\n").Replace("\n", "<br>")).Append("</p>");
            _text.AppendLine(text).AppendLine();
            return this;
        }

        internal SemanticDocumentBuilder List(IEnumerable<string> values, bool ordered = false, bool markdown = false)
        {
            var items = values.ToArray();
            if (items.Length == 0) return this;
            string tag = ordered ? "ol" : "ul";
            _html.Append('<').Append(tag).Append('>');
            foreach (string value in items)
            {
                _html.Append("<li>").Append(Inline(value, markdown).Replace("\r\n", "\n").Replace("\n", "<br>")).Append("</li>");
                _text.Append("• ").AppendLine(value);
            }
            _html.Append("</").Append(tag).Append('>');
            _text.AppendLine();
            return this;
        }

        internal SemanticDocumentBuilder Table(string caption, string[] headers, IEnumerable<string[]> rows, int keyboardColumn = -1)
        {
            _html.Append("<table><caption>").Append(Encode(caption)).Append("</caption><thead><tr>");
            foreach (string header in headers) _html.Append("<th scope=\"col\">").Append(Encode(header)).Append("</th>");
            _html.Append("</tr></thead><tbody>");
            _text.AppendLine(string.Join(" / ", headers));
            foreach (var row in rows)
            {
                _html.Append("<tr>");
                for (int col = 0; col < headers.Length; col++)
                {
                    string value = col < row.Length ? row[col] : "";
                    _html.Append(col == 0 ? "<th scope=\"row\">" : "<td>");
                    if (col == keyboardColumn) _html.Append("<kbd>").Append(Encode(value)).Append("</kbd>");
                    else _html.Append(Inline(value, markdown: false));
                    _html.Append(col == 0 ? "</th>" : "</td>");
                }
                _html.Append("</tr>");
                _text.AppendLine(string.Join(" / ", row));
            }
            _html.Append("</tbody></table>");
            _text.AppendLine();
            return this;
        }

        // The bundled documentation uses headings, paragraphs, lists, quotes, rules and inline
        // links. Preserve those structures instead of displaying Markdown or decorative separators.
        internal SemanticDocumentBuilder Markdown(string markdown)
        {
            var lines = markdown.Replace("\r\n", "\n").Split('\n');
            var paragraph = new List<string>();
            var list = new List<string>();
            bool ordered = false;
            int previousHeading = 1;
            void FlushParagraph() { if (paragraph.Count > 0) { Paragraph(string.Join("\n", paragraph), markdown: true); paragraph.Clear(); } }
            void FlushList() { if (list.Count > 0) { List(list, ordered, markdown: true); list.Clear(); } }
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0) { FlushParagraph(); FlushList(); continue; }
                if (line.StartsWith("```", StringComparison.Ordinal))
                {
                    FlushParagraph(); FlushList();
                    var code = new StringBuilder();
                    while (++i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal)) code.AppendLine(lines[i]);
                    _html.Append("<pre><code>").Append(Encode(code.ToString())).Append("</code></pre>");
                    _text.AppendLine(code.ToString());
                    continue;
                }
                var heading = Regex.Match(line, @"^(#{1,6})\s+(.+)$");
                if (heading.Success)
                {
                    FlushParagraph(); FlushList();
                    int level = Math.Clamp(heading.Groups[1].Length, 2, Math.Min(previousHeading + 1, 6));
                    Heading(heading.Groups[2].Value, level); previousHeading = level; continue;
                }
                if (line.Length >= 3 && (line.All(c => c == '-') || line.All(c => c == '*')))
                { FlushParagraph(); FlushList(); _html.Append("<hr>"); continue; }
                var item = Regex.Match(line, @"^(?<mark>[-*+]|\d+[.)])\s+(?<text>.+)$");
                if (item.Success)
                {
                    FlushParagraph();
                    bool isOrdered = char.IsDigit(item.Groups["mark"].Value[0]);
                    if (list.Count > 0 && ordered != isOrdered) FlushList();
                    ordered = isOrdered; list.Add(item.Groups["text"].Value); continue;
                }
                FlushList();
                if (line.StartsWith("> ", StringComparison.Ordinal))
                {
                    FlushParagraph(); _html.Append("<blockquote><p>").Append(Inline(line.Substring(2))).Append("</p></blockquote>");
                    _text.AppendLine(line.Substring(2)); continue;
                }
                paragraph.Add(line);
            }
            FlushParagraph(); FlushList();
            return this;
        }

        internal SemanticDocument Build() => new SemanticDocument(_title, _html.ToString(), _text.ToString().TrimEnd());
        internal static string Encode(string? text) => WebUtility.HtmlEncode(text ?? "");

        internal static bool IsExternalLink(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeMailto);

        internal static string Inline(string value, bool markdown = true)
        {
            var result = new StringBuilder();
            int offset = 0;
            foreach (Match match in InlinePattern.Matches(value))
            {
                result.Append(Encode(value.Substring(offset, match.Index - offset)));
                if (!markdown && !match.Groups["url"].Success) result.Append(Encode(match.Value));
                else if (match.Groups["code"].Success) result.Append("<code>").Append(Encode(match.Groups["code"].Value)).Append("</code>");
                else if (match.Groups["strong"].Success) result.Append("<strong>").Append(Encode(match.Groups["strong"].Value)).Append("</strong>");
                else
                {
                    string url = match.Groups["href"].Success ? match.Groups["href"].Value : match.Value;
                    string label = match.Groups["label"].Success ? match.Groups["label"].Value : match.Value;
                    if (IsExternalLink(url)) result.Append("<a tabindex=\"-1\" href=\"").Append(Encode(url)).Append("\">").Append(Encode(label)).Append("</a>");
                    else result.Append(Encode(label));
                }
                offset = match.Index + match.Length;
            }
            return result.Append(Encode(value.Substring(offset))).ToString();
        }

        internal static string ThemeCss(ThemePalette palette, float fontPoints)
        {
            string Color(System.Drawing.Color color) => "#" + color.R.ToString("x2") + color.G.ToString("x2") + color.B.ToString("x2");
            string size = Math.Max(16, fontPoints * 96d / 72d).ToString("0.##", CultureInfo.InvariantCulture);
            return ":root{color-scheme:light dark;--bg:" + Color(palette.SurfaceBackground) + ";--fg:" + Color(palette.TextPrimary) +
                ";--muted:" + Color(palette.TextSecondary) + ";--line:" + Color(palette.Border) + ";--link:" + Color(palette.Accent) +
                ";--focus:" + Color(palette.FocusRing) + ";font-size:" + size + "px}";
        }

        internal static string Wrap(SemanticDocument document, ThemePalette palette, float fontPoints)
        {
            string nonce = Guid.NewGuid().ToString("N");
            return "<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">" +
                "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; script-src 'nonce-" + nonce +
                "'; base-uri 'none'; form-action 'none'\"><title>" + Encode(document.Title) + "</title><style id=\"theme\">" + ThemeCss(palette, fontPoints) + "</style><style>" +
                "*{box-sizing:border-box}html{background:var(--bg);color:var(--fg)}body{margin:0;padding:1rem 1.25rem;font-family:'Microsoft YaHei UI','Segoe UI',sans-serif;line-height:1.65;overflow-wrap:anywhere}" +
                "main{max-width:78rem;margin:auto}h1{font-size:1.45rem;line-height:1.4;margin:.1rem 0 1rem}h2{font-size:1.18rem;margin:1.5rem 0 .55rem}h3,h4,h5,h6{font-size:1.08rem;margin:1rem 0 .5rem}" +
                "p{margin:.55rem 0 1rem}ul,ol{padding-inline-start:1.6rem;margin:.6rem 0 1rem}li{padding:.12rem 0}a{color:var(--link);text-decoration:underline;text-underline-offset:.18em}" +
                "a:focus-visible{outline:2px solid var(--focus);outline-offset:3px}hr{border:0;border-top:1px solid var(--line);margin:1.1rem 0}" +
                "table{border-collapse:collapse;width:100%;margin:.7rem 0 1.3rem;font-size:.95rem}caption{text-align:left;font-weight:600;margin-bottom:.4rem}th,td{text-align:left;vertical-align:top;border:1px solid var(--line);padding:.5rem .65rem}thead th{font-weight:700}tbody th{font-weight:500}" +
                "kbd,code{font-family:Consolas,monospace;font-size:.95em}kbd{font-weight:600}pre{white-space:pre-wrap;overflow-wrap:anywhere;padding:.75rem;border:1px solid var(--line)}blockquote{margin:1rem 0;border-inline-start:3px solid var(--line);padding-inline-start:1rem;color:var(--muted)}" +
                "@media(forced-colors:active){:root{--bg:Canvas;--fg:CanvasText;--muted:CanvasText;--line:CanvasText;--link:LinkText;--focus:Highlight}}@media print{body{background:white;color:black}a{color:inherit}}" +
                "</style></head><body><main id=\"content\" aria-labelledby=\"document-title\">" + document.BodyHtml + "</main><script nonce=\"" + nonce + "\">" +
                "document.addEventListener('keydown',e=>{if(e.key==='Tab'&&!e.ctrlKey&&!e.altKey&&!e.metaKey){e.preventDefault();chrome.webview.postMessage(e.shiftKey?'document:previous':'document:next')}" +
                "else if(e.key==='Escape'&&!e.ctrlKey&&!e.altKey&&!e.metaKey){e.preventDefault();chrome.webview.postMessage('document:escape')}},true);" +
                "</script></body></html>";
        }
    }
}
