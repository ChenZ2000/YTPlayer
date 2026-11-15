using System.Text;
using System.Windows.Forms;

namespace YTPlayer.Utils
{
    /// <summary>Native, caret-browsable text for short messages and live status displays.</summary>
    internal sealed class ReadOnlyTextBox : TextBox
    {
        internal ReadOnlyTextBox()
        {
            Multiline = true;
            ReadOnly = true;
            TabStop = true;
            AcceptsTab = false;
            HideSelection = false;
            ShortcutsEnabled = true;
            WordWrap = true;
            ScrollBars = ScrollBars.Vertical;
        }

#if !NETFRAMEWORK
        [System.Diagnostics.CodeAnalysis.AllowNull]
#endif
        public override string Text
        {
            get => base.Text;
            // WinForms Lines splits lone LF/CR, but the underlying EDIT control only
            // exposes CRLF as a hard line break to the caret and accessibility APIs.
            set => base.Text = NormalizeLineEndings(value);
        }

        internal static string NormalizeLineEndings(string? text)
        {
            if (text == null || text.Length == 0) return string.Empty;
            int first = 0;
            for (; first < text.Length; first++)
            {
                char c = text[first];
                if (c == '\r' && first + 1 < text.Length && text[first + 1] == '\n') { first++; continue; }
                if (IsLineBreak(c)) break;
            }
            // Already-correct status updates allocate nothing and retain the caret.
            if (first == text.Length) return text;
            var result = new StringBuilder(text.Length);
            result.Append(text, 0, first);
            for (int i = first; i < text.Length; i++)
            {
                char c = text[i];
                if (!IsLineBreak(c)) { result.Append(c); continue; }
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                result.Append("\r\n");
            }
            return result.ToString();
        }

        private static bool IsLineBreak(char c) => c == '\r' || c == '\n' || c == '\u0085' || c == '\u2028' || c == '\u2029';
    }
}
