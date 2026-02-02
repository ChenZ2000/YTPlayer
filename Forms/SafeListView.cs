using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using YTPlayer.Utils;

namespace YTPlayer
{
    internal sealed class SafeListView : ListView
    {
        private const int TextPadding = 6;
        private const int SequenceTextPadding = 3;
        private const int MaxRowHeight = 512;
        private const int SequenceColumnIndex = 1;
        private const int GridLineAlpha = 110;
#if DEBUG
        private const int AlignmentLogMaxSamples = 12;
        private const int SequenceAlignmentLogMaxSamples = 10;
        private int _alignmentLogCount;
        private int _sequenceAlignmentLogRemaining = SequenceAlignmentLogMaxSamples;
        private readonly HashSet<int> _alignmentLoggedColumns = new HashSet<int>();
#endif
        private Font? _sequenceFont;
        private bool _sequenceFontOwned;
        private ToolTip? _subItemToolTip;
        private ListViewItem? _toolTipItem;
        private ListViewItem.ListViewSubItem? _toolTipSubItem;
        private string? _toolTipText;

        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal int MultiLineMaxLines { get; set; } = 2;

        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal int ShortInfoColumnIndex { get; set; } = 4;

        public SafeListView()
        {
            OwnerDraw = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            UpdateStyles();

            if (SmallImageList == null)
            {
                SmallImageList = new ImageList
                {
                    ImageSize = new Size(1, 30),
                    ColorDepth = ColorDepth.Depth8Bit
                };
            }
        }

        protected override void OnFontChanged(EventArgs e)
        {
            ResetSequenceFont();
            base.OnFontChanged(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ResetSequenceFont();
                if (_subItemToolTip != null)
                {
                    _subItemToolTip.Dispose();
                    _subItemToolTip = null;
                }
            }
            base.Dispose(disposing);
        }

        internal void ResetAlignmentLog(string reason, string? viewSource)
        {
#if DEBUG
            _alignmentLogCount = 0;
            _alignmentLoggedColumns.Clear();
            _sequenceAlignmentLogRemaining = SequenceAlignmentLogMaxSamples;
            DebugLogger.Log(DebugLogger.LogLevel.Info, "ListViewAlign",
                $"Reset alignment logs: reason={reason} viewSource={viewSource ?? ""}");
#endif
        }

        internal int GetRowHeight()
        {
            if (SmallImageList != null)
            {
                return SmallImageList.ImageSize.Height;
            }
            return Math.Max(1, Font?.Height ?? 1);
        }

        internal void SetRowHeight(int height)
        {
            height = Math.Max(height, Math.Max(1, Font?.Height ?? 1));
            height = Math.Min(height, MaxRowHeight);
            if (SmallImageList == null)
            {
                SmallImageList = new ImageList
                {
                    ImageSize = new Size(1, height),
                    ColorDepth = ColorDepth.Depth8Bit
                };
                return;
            }

            if (SmallImageList.ImageSize.Height != height)
            {
                SmallImageList.ImageSize = new Size(1, height);
            }
        }

        protected override void OnGotFocus(EventArgs e)
        {
            try
            {
                SafeClearSelectionIfEmpty();
                base.OnGotFocus(e);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                Debug.WriteLine("[SafeListView] OnGotFocus suppressed: " + ex.Message);
                SafeClearSelection();
            }
        }

        protected override void OnLostFocus(EventArgs e)
        {
            try
            {
                SafeClearSelectionIfEmpty();
                base.OnLostFocus(e);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                Debug.WriteLine("[SafeListView] OnLostFocus suppressed: " + ex.Message);
                SafeClearSelection();
            }
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            try
            {
                SafeClearSelectionIfEmpty();
                base.OnHandleDestroyed(e);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                Debug.WriteLine("[SafeListView] OnHandleDestroyed suppressed: " + ex.Message);
                SafeClearSelection();
            }
        }

        protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
        {
            ThemePalette palette = ThemeManager.Current;
            using (var backBrush = new SolidBrush(palette.SurfaceAlt))
            {
                e.Graphics.FillRectangle(backBrush, e.Bounds);
            }

            TextFormatFlags flags = BuildTextFlags(e.Header?.TextAlign ?? HorizontalAlignment.Left, allowWrap: false, useEllipsis: true);
            string text = e.Header?.Text ?? string.Empty;
            Rectangle textBounds = AlignTextBounds(e.Graphics, e.Bounds, text, Font, flags);
            TextRenderer.DrawText(e.Graphics, text, Font, textBounds, palette.TextSecondary, flags);

            DrawColumnSeparatorLine(e.Graphics, palette, e.Bounds, e.ColumnIndex, Columns.Count);
            using (var pen = new Pen(GetGridLineColor(palette)))
            {
                int y = e.Bounds.Bottom - 1;
                e.Graphics.DrawLine(pen, e.Bounds.Left, y, e.Bounds.Right, y);
            }
        }

        protected override void OnDrawItem(DrawListViewItemEventArgs e)
        {
            if (View == View.Details)
            {
                // Avoid clearing full rows on partial redraws; subitems handle painting in Details view.
                return;
            }

            DrawRowBackground(e);
            ThemePalette palette = ThemeManager.Current;
            bool selected = (e.State & ListViewItemStates.Selected) != 0;
            Color textColor = ResolveItemTextColor(e.Item, palette, selected);
            TextFormatFlags flags = BuildTextFlags(HorizontalAlignment.Left, allowWrap: false, useEllipsis: true);
            Rectangle textBounds = AlignTextBounds(e.Graphics, e.Bounds, e.Item.Text, Font, flags);
            TextRenderer.DrawText(e.Graphics, e.Item.Text, Font, textBounds, textColor, flags);
        }

        protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
        {
            if (View != View.Details)
            {
                base.OnDrawSubItem(e);
                return;
            }

            if (e.Item == null || e.SubItem == null)
            {
                return;
            }

            DrawSubItemBackground(e);

            ThemePalette palette = ThemeManager.Current;
            bool selected = (e.ItemState & ListViewItemStates.Selected) != 0;
            Color textColor = ResolveSubItemTextColor(e.SubItem, e.Item, palette, selected);
            int columnIndex = e.ColumnIndex;
            int maxLines = GetColumnMaxLines(columnIndex);
            bool allowWrap = maxLines > 1;
            HorizontalAlignment alignment = e.Header?.TextAlign ?? HorizontalAlignment.Left;
            string text = e.SubItem.Text ?? string.Empty;
            Font drawFont = Font;
            GetColumnTextPadding(columnIndex, text, out int leftPadding, out int rightPadding);
            if (columnIndex == SequenceColumnIndex)
            {
                drawFont = ResolveSequenceFontForText(text);
            }
            TextFormatFlags flags = BuildTextFlags(alignment, allowWrap, useEllipsis: true);
            if (allowWrap)
            {
                text = TrimTextToMaxLines(e.Graphics, text, drawFont, e.Bounds, maxLines, leftPadding, rightPadding);
            }
            if (allowWrap && alignment != HorizontalAlignment.Left)
            {
                TextFormatFlags leftFlags = BuildTextFlags(HorizontalAlignment.Left, allowWrap, useEllipsis: true);
                Rectangle textBounds = AlignWrappedTextBounds(e.Graphics, e.Bounds, text, drawFont, leftFlags, alignment, leftPadding, rightPadding);
#if DEBUG
                TryLogSubItemAlignment(e, alignment, allowWrap, maxLines, flags, leftFlags, text, textBounds);
#endif
                TextRenderer.DrawText(e.Graphics, text, drawFont, textBounds, textColor, leftFlags);
            }
            else
            {
                Rectangle textBounds = AlignTextBounds(e.Graphics, e.Bounds, text, drawFont, flags, leftPadding, rightPadding);
#if DEBUG
                TryLogSubItemAlignment(e, alignment, allowWrap, maxLines, flags, null, text, textBounds);
#endif
                TextRenderer.DrawText(e.Graphics, text, drawFont, textBounds, textColor, flags);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            DrawListBorder(e.Graphics);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            UpdateSubItemToolTip(e.Location);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            HideSubItemToolTip();
        }

        private void DrawSubItemBackground(DrawListViewSubItemEventArgs e)
        {
            ThemePalette palette = ThemeManager.Current;
            bool selected = (e.ItemState & ListViewItemStates.Selected) != 0;
            bool hot = (e.ItemState & ListViewItemStates.Hot) != 0;
            Color backColor = selected ? palette.AccentSoft : (hot ? BlendColor(BackColor, palette.Highlight, 0.12f) : BackColor);

            using (var backBrush = new SolidBrush(backColor))
            {
                e.Graphics.FillRectangle(backBrush, e.Bounds);
            }

            if (selected && e.ColumnIndex == 0)
            {
                using (var accentBrush = new SolidBrush(palette.Accent))
                {
                    e.Graphics.FillRectangle(accentBrush, new Rectangle(0, e.Bounds.Top, 5, e.Bounds.Height));
                }
            }

            DrawColumnSeparatorLine(e.Graphics, palette, e.Bounds, e.ColumnIndex, Columns.Count);
            if (e.ColumnIndex >= 0 && e.ColumnIndex == Columns.Count - 1)
            {
                using (var pen = new Pen(GetGridLineColor(palette)))
                {
                    int y = e.Bounds.Bottom - 1;
                    e.Graphics.DrawLine(pen, 0, y, ClientRectangle.Width, y);
                }
            }
        }

        private void DrawRowBackground(DrawListViewItemEventArgs e)
        {
            ThemePalette palette = ThemeManager.Current;
            bool selected = (e.State & ListViewItemStates.Selected) != 0;
            bool hot = (e.State & ListViewItemStates.Hot) != 0;
            Color backColor = selected ? palette.AccentSoft : (hot ? BlendColor(BackColor, palette.Highlight, 0.12f) : BackColor);
            Rectangle rowBounds = new Rectangle(0, e.Bounds.Top, ClientRectangle.Width, e.Bounds.Height);

            using (var backBrush = new SolidBrush(backColor))
            {
                e.Graphics.FillRectangle(backBrush, rowBounds);
            }

            if (selected)
            {
                using (var accentBrush = new SolidBrush(palette.Accent))
                {
                    e.Graphics.FillRectangle(accentBrush, new Rectangle(0, e.Bounds.Top, 5, e.Bounds.Height));
                }
            }

            using (var pen = new Pen(GetGridLineColor(palette)))
            {
                int y = e.Bounds.Bottom - 1;
                e.Graphics.DrawLine(pen, 0, y, ClientRectangle.Width, y);
            }
        }

        private static void DrawColumnSeparatorLine(Graphics graphics, ThemePalette palette, Rectangle bounds, int columnIndex, int columnCount)
        {
            if (graphics == null || columnIndex < 0 || columnIndex >= columnCount - 1)
            {
                return;
            }

            if (bounds.Width <= 0)
            {
                return;
            }

            using (var pen = new Pen(GetGridLineColor(palette)))
            {
                int x = bounds.Right - 1;
                graphics.DrawLine(pen, x, bounds.Top + 2, x, bounds.Bottom - 2);
            }
        }

        private static Color GetGridLineColor(ThemePalette palette)
        {
            Color baseColor = palette.TextSecondary;
            return Color.FromArgb(GridLineAlpha, baseColor.R, baseColor.G, baseColor.B);
        }

        private void DrawListBorder(Graphics graphics)
        {
            if (graphics == null)
            {
                return;
            }

            Rectangle rect = ClientRectangle;
            if (rect.Width <= 1 || rect.Height <= 1)
            {
                return;
            }

            ThemePalette palette = ThemeManager.Current;
            using (var pen = new Pen(GetGridLineColor(palette)))
            {
                graphics.DrawRectangle(pen, new Rectangle(0, 0, rect.Width - 1, rect.Height - 1));
            }
        }

        private static TextFormatFlags BuildTextFlags(HorizontalAlignment alignment, bool allowWrap, bool useEllipsis)
        {
            TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPadding;
            if (allowWrap)
            {
                flags |= TextFormatFlags.WordBreak | TextFormatFlags.VerticalCenter;
            }
            else
            {
                flags |= TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;
            }
            if (useEllipsis)
            {
                flags |= TextFormatFlags.EndEllipsis;
            }
            flags |= alignment switch
            {
                HorizontalAlignment.Center => TextFormatFlags.HorizontalCenter,
                HorizontalAlignment.Right => TextFormatFlags.Right,
                _ => TextFormatFlags.Left
            };
            return flags;
        }

        private static Rectangle InflateTextBounds(Rectangle bounds)
        {
            return InflateTextBounds(bounds, TextPadding);
        }

        private static Rectangle InflateTextBounds(Rectangle bounds, int padding)
        {
            return Rectangle.FromLTRB(bounds.Left + padding, bounds.Top, bounds.Right - padding, bounds.Bottom);
        }

        private static Rectangle InflateTextBounds(Rectangle bounds, int leftPadding, int rightPadding)
        {
            return Rectangle.FromLTRB(bounds.Left + leftPadding, bounds.Top, bounds.Right - rightPadding, bounds.Bottom);
        }

        private static Rectangle AlignTextBounds(Graphics graphics, Rectangle bounds, string text, Font font, TextFormatFlags flags)
        {
            return AlignTextBounds(graphics, bounds, text, font, flags, TextPadding);
        }

        private static Rectangle AlignTextBounds(Graphics graphics, Rectangle bounds, string text, Font font, TextFormatFlags flags, int padding)
        {
            return AlignTextBounds(graphics, bounds, text, font, flags, padding, padding);
        }

        private static Rectangle AlignTextBounds(Graphics graphics, Rectangle bounds, string text, Font font, TextFormatFlags flags, int leftPadding, int rightPadding)
        {
            Rectangle textBounds = InflateTextBounds(bounds, leftPadding, rightPadding);
            if (string.IsNullOrEmpty(text))
            {
                return textBounds;
            }
            if (textBounds.Width <= 0 || textBounds.Height <= 0)
            {
                return textBounds;
            }

            if ((flags & TextFormatFlags.WordBreak) == 0 || (flags & TextFormatFlags.VerticalCenter) == 0)
            {
                return textBounds;
            }

            TextFormatFlags measureFlags = flags & ~TextFormatFlags.VerticalCenter;
            measureFlags |= TextFormatFlags.NoPadding;
            Size measured = TextRenderer.MeasureText(graphics, text, font, new Size(textBounds.Width, int.MaxValue), measureFlags);
            int extra = textBounds.Height - measured.Height;
            if (extra > 0)
            {
                return new Rectangle(textBounds.Left, textBounds.Top + extra / 2, textBounds.Width, measured.Height);
            }

            return textBounds;
        }

        private static Rectangle AlignWrappedTextBounds(Graphics graphics, Rectangle bounds, string text, Font font, TextFormatFlags leftFlags, HorizontalAlignment alignment)
        {
            return AlignWrappedTextBounds(graphics, bounds, text, font, leftFlags, alignment, TextPadding);
        }

        private static Rectangle AlignWrappedTextBounds(Graphics graphics, Rectangle bounds, string text, Font font, TextFormatFlags leftFlags, HorizontalAlignment alignment, int padding)
        {
            Rectangle textBounds = AlignTextBounds(graphics, bounds, text, font, leftFlags, padding, padding);
            if (string.IsNullOrEmpty(text) || alignment == HorizontalAlignment.Left)
            {
                return textBounds;
            }

            Rectangle measureBounds = InflateTextBounds(bounds, padding, padding);
            if (measureBounds.Width <= 0 || measureBounds.Height <= 0)
            {
                return textBounds;
            }

            TextFormatFlags measureFlags = leftFlags & ~TextFormatFlags.VerticalCenter;
            Size measured = TextRenderer.MeasureText(graphics, text, font, new Size(measureBounds.Width, int.MaxValue), measureFlags);
            int blockWidth = Math.Min(measureBounds.Width, Math.Max(1, measured.Width));
            int left = alignment == HorizontalAlignment.Center
                ? measureBounds.Left + Math.Max(0, (measureBounds.Width - blockWidth) / 2)
                : measureBounds.Right - blockWidth;
            return new Rectangle(left, textBounds.Top, blockWidth, textBounds.Height);
        }

        private static Rectangle AlignWrappedTextBounds(Graphics graphics, Rectangle bounds, string text, Font font, TextFormatFlags leftFlags, HorizontalAlignment alignment, int leftPadding, int rightPadding)
        {
            Rectangle textBounds = AlignTextBounds(graphics, bounds, text, font, leftFlags, leftPadding, rightPadding);
            if (string.IsNullOrEmpty(text) || alignment == HorizontalAlignment.Left)
            {
                return textBounds;
            }

            Rectangle measureBounds = InflateTextBounds(bounds, leftPadding, rightPadding);
            if (measureBounds.Width <= 0 || measureBounds.Height <= 0)
            {
                return textBounds;
            }

            TextFormatFlags measureFlags = leftFlags & ~TextFormatFlags.VerticalCenter;
            Size measured = TextRenderer.MeasureText(graphics, text, font, new Size(measureBounds.Width, int.MaxValue), measureFlags);
            int blockWidth = Math.Min(measureBounds.Width, Math.Max(1, measured.Width));
            int left = alignment == HorizontalAlignment.Center
                ? measureBounds.Left + Math.Max(0, (measureBounds.Width - blockWidth) / 2)
                : measureBounds.Right - blockWidth;
            return new Rectangle(left, textBounds.Top, blockWidth, textBounds.Height);
        }

        private int GetColumnMaxLines(int columnIndex)
        {
            if (columnIndex <= SequenceColumnIndex)
            {
                return 1;
            }
            if (columnIndex == ShortInfoColumnIndex)
            {
                return 1;
            }
            return Math.Max(1, MultiLineMaxLines);
        }

        private static string TrimTextToMaxLines(Graphics graphics, string text, Font font, Rectangle bounds, int maxLines)
        {
            return TrimTextToMaxLines(graphics, text, font, bounds, maxLines, TextPadding);
        }

        private static string TrimTextToMaxLines(Graphics graphics, string text, Font font, Rectangle bounds, int maxLines, int padding)
        {
            return TrimTextToMaxLines(graphics, text, font, bounds, maxLines, padding, padding);
        }

        private static string TrimTextToMaxLines(Graphics graphics, string text, Font font, Rectangle bounds, int maxLines, int leftPadding, int rightPadding)
        {
            if (string.IsNullOrWhiteSpace(text) || maxLines <= 0)
            {
                return string.Empty;
            }

            Rectangle textBounds = InflateTextBounds(bounds, leftPadding, rightPadding);
            if (textBounds.Width <= 0 || textBounds.Height <= 0)
            {
                return text;
            }

            int lineHeight = TextRenderer.MeasureText(graphics, "A", font, new Size(textBounds.Width, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Height;
            int maxHeight = Math.Max(1, lineHeight * maxLines);
            TextFormatFlags measureFlags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPadding;
            Size fullMeasured = TextRenderer.MeasureText(graphics, text, font, new Size(textBounds.Width, int.MaxValue), measureFlags);
            if (fullMeasured.Height <= maxHeight)
            {
                return text;
            }

            const string ellipsis = "...";
            int left = 0;
            int right = text.Length;
            int best = 0;
            while (left <= right)
            {
                int mid = (left + right) / 2;
                string candidate = text.Substring(0, mid).TrimEnd() + ellipsis;
                Size size = TextRenderer.MeasureText(graphics, candidate, font, new Size(textBounds.Width, maxHeight), measureFlags);
                if (size.Height <= maxHeight)
                {
                    best = mid;
                    left = mid + 1;
                }
                else
                {
                    right = mid - 1;
                }
            }

            if (best <= 0)
            {
                return ellipsis;
            }

            return text.Substring(0, best).TrimEnd() + ellipsis;
        }

#if DEBUG
        private void TryLogSubItemAlignment(
            DrawListViewSubItemEventArgs e,
            HorizontalAlignment alignment,
            bool allowWrap,
            int maxLines,
            TextFormatFlags layoutFlags,
            TextFormatFlags? drawFlags,
            string text,
            Rectangle textBounds)
        {
            int columnIndex = e.ColumnIndex;
            bool isSequenceColumn = columnIndex == SequenceColumnIndex;
            if (isSequenceColumn)
            {
                if (_sequenceAlignmentLogRemaining <= 0)
                {
                    return;
                }
                _sequenceAlignmentLogRemaining--;
            }
            else
            {
                if (_alignmentLogCount >= AlignmentLogMaxSamples)
                {
                    return;
                }
                if (_alignmentLoggedColumns.Contains(columnIndex))
                {
                    return;
                }
                _alignmentLoggedColumns.Add(columnIndex);
                _alignmentLogCount++;
            }

            string headerText = e.Header?.Text ?? string.Empty;
            string preview = TruncateTextForLog(text, 40);
            string flagsText = drawFlags.HasValue ? drawFlags.Value.ToString() : layoutFlags.ToString();

            DebugLogger.Log(DebugLogger.LogLevel.Info, "ListViewAlign",
                $"col={columnIndex} header=\"{headerText}\" align={alignment} allowWrap={allowWrap} maxLines={maxLines} " +
                $"bounds={e.Bounds.X},{e.Bounds.Y},{e.Bounds.Width},{e.Bounds.Height} " +
                $"textBounds={textBounds.X},{textBounds.Y},{textBounds.Width},{textBounds.Height} " +
                $"flags={layoutFlags} drawFlags={flagsText} padding={TextPadding} " +
                $"seqSample={(isSequenceColumn ? "Y" : "N")} text=\"{preview}\" len={text.Length}");
        }

        private static string TruncateTextForLog(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            {
                return text ?? string.Empty;
            }

            return text.Substring(0, maxLength) + "...";
        }
#endif

        private static Color BlendColor(Color baseColor, Color overlay, float amount)
        {
            amount = Math.Max(0f, Math.Min(1f, amount));
            int r = (int)(baseColor.R + (overlay.R - baseColor.R) * amount);
            int g = (int)(baseColor.G + (overlay.G - baseColor.G) * amount);
            int b = (int)(baseColor.B + (overlay.B - baseColor.B) * amount);
            return Color.FromArgb(baseColor.A, r, g, b);
        }

        private static Color ResolveItemTextColor(ListViewItem item, ThemePalette palette, bool selected)
        {
            Color color = item.ForeColor;
            if (color.IsEmpty || color == SystemColors.WindowText || color == SystemColors.HighlightText)
            {
                color = palette.TextPrimary;
            }
            return color;
        }

        private static Color ResolveSubItemTextColor(ListViewItem.ListViewSubItem subItem, ListViewItem item, ThemePalette palette, bool selected)
        {
            Color color = subItem.ForeColor;
            if (color.IsEmpty || color == SystemColors.WindowText)
            {
                color = item.ForeColor;
            }
            if (color.IsEmpty || color == SystemColors.WindowText || color == SystemColors.HighlightText)
            {
                color = palette.TextPrimary;
            }
            return color;
        }

        private int GetColumnTextPadding(int columnIndex)
        {
            if (columnIndex == SequenceColumnIndex)
            {
                return SequenceTextPadding;
            }
            return TextPadding;
        }

        private void GetColumnTextPadding(int columnIndex, string text, out int leftPadding, out int rightPadding)
        {
            if (columnIndex == SequenceColumnIndex)
            {
                leftPadding = SequenceTextPadding;
                rightPadding = SequenceTextPadding + (IsWideSequence(text) ? 1 : 0);
                return;
            }

            leftPadding = TextPadding;
            rightPadding = TextPadding;
        }

        private static bool IsWideSequence(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            int digits = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (!char.IsDigit(text[i]))
                {
                    return false;
                }
                digits++;
            }
            return digits >= 4;
        }

        private Font GetSequenceFont()
        {
            if (_sequenceFont != null)
            {
                return _sequenceFont;
            }

            Font baseFont = Font ?? DefaultFont;
            string[] candidates = new[] { "Cascadia Mono", "Consolas", "Lucida Console", "Courier New" };
            foreach (string name in candidates)
            {
                try
                {
                    var family = new FontFamily(name);
                    if (!family.IsStyleAvailable(baseFont.Style))
                    {
                        continue;
                    }
                    _sequenceFont = new Font(family, baseFont.Size, baseFont.Style, baseFont.Unit);
                    _sequenceFontOwned = true;
                    return _sequenceFont;
                }
                catch
                {
                }
            }

            _sequenceFont = baseFont;
            _sequenceFontOwned = false;
            return _sequenceFont;
        }

        private Font ResolveSequenceFontForText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return Font ?? DefaultFont;
            }

            for (int i = 0; i < text.Length; i++)
            {
                if (!char.IsDigit(text[i]))
                {
                    return Font ?? DefaultFont;
                }
            }

            return GetSequenceFont();
        }

        internal int MeasureSequenceTextWidth(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            Font font = ResolveSequenceFontForText(text);
            Size size = TextRenderer.MeasureText(text, font, new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl);
            return size.Width;
        }

        internal int GetSequenceTextPadding()
        {
            return SequenceTextPadding;
        }

        internal int GetSequenceTextPaddingTotal(int digitCount)
        {
            int extra = digitCount >= 4 ? 1 : 0;
            return SequenceTextPadding * 2 + extra;
        }

        private void EnsureSubItemToolTip()
        {
            if (_subItemToolTip != null)
            {
                return;
            }

            _subItemToolTip = new ToolTip
            {
                AutoPopDelay = 12000,
                InitialDelay = 450,
                ReshowDelay = 150,
                ShowAlways = false
            };
        }

        private void UpdateSubItemToolTip(Point location)
        {
            if (View != View.Details)
            {
                HideSubItemToolTip();
                return;
            }

            ListViewHitTestInfo hit = HitTest(location);
            if (hit.Item == null || hit.SubItem == null)
            {
                HideSubItemToolTip();
                return;
            }

            int columnIndex = hit.Item.SubItems.IndexOf(hit.SubItem);
            if (columnIndex <= 0)
            {
                HideSubItemToolTip();
                return;
            }

            string text = hit.SubItem.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                HideSubItemToolTip();
                return;
            }

            Font font = columnIndex == SequenceColumnIndex ? ResolveSequenceFontForText(text) : (Font ?? DefaultFont);
            GetColumnTextPadding(columnIndex, text, out int leftPadding, out int rightPadding);
            Rectangle bounds = hit.SubItem.Bounds;
            int availableWidth = Math.Max(1, bounds.Width - leftPadding - rightPadding);

            bool allowWrap = GetColumnMaxLines(columnIndex) > 1;
            bool truncated;
            if (allowWrap)
            {
                int lineHeight = TextRenderer.MeasureText("A", font, new Size(availableWidth, int.MaxValue),
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Height;
                int maxHeight = Math.Max(1, lineHeight * GetColumnMaxLines(columnIndex));
                Size measured = TextRenderer.MeasureText(text, font, new Size(availableWidth, int.MaxValue),
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl);
                truncated = measured.Height > maxHeight;
            }
            else
            {
                Size measured = TextRenderer.MeasureText(text, font, new Size(int.MaxValue, int.MaxValue),
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl);
                truncated = measured.Width > availableWidth;
            }

            if (!truncated)
            {
                HideSubItemToolTip();
                return;
            }

            if (ReferenceEquals(_toolTipItem, hit.Item) &&
                ReferenceEquals(_toolTipSubItem, hit.SubItem) &&
                string.Equals(_toolTipText, text, StringComparison.Ordinal))
            {
                return;
            }

            EnsureSubItemToolTip();
            _toolTipItem = hit.Item;
            _toolTipSubItem = hit.SubItem;
            _toolTipText = text;
            _subItemToolTip!.Show(text, this, location.X + 12, location.Y + 18, 12000);
        }

        private void HideSubItemToolTip()
        {
            if (_subItemToolTip == null)
            {
                return;
            }

            _subItemToolTip.Hide(this);
            _toolTipItem = null;
            _toolTipSubItem = null;
            _toolTipText = null;
        }

        private void ResetSequenceFont()
        {
            if (_sequenceFontOwned && _sequenceFont != null)
            {
                _sequenceFont.Dispose();
            }
            _sequenceFont = null;
            _sequenceFontOwned = false;
        }

        private void SafeClearSelectionIfEmpty()
        {
            if (VirtualMode)
            {
                if (VirtualListSize <= 0)
                {
                    SafeClearSelection();
                }
                return;
            }

            if (Items.Count == 0)
            {
                SafeClearSelection();
            }
        }

        private void SafeClearSelection()
        {
            try
            {
                SelectedIndices.Clear();
            }
            catch
            {
            }

            try
            {
                FocusedItem = null;
            }
            catch
            {
            }
        }
    }
}
