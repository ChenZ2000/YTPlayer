using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using YTPlayer.Utils;

namespace YTPlayer
{
    internal sealed class SafeListView : ListView
    {
        private const int TextPadding = 6;
        private const int MaxRowHeight = 512;
        private const int SequenceColumnIndex = 1;
        private const int GridLineAlpha = 150;

        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal int MultiLineMaxLines { get; set; } = 3;

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
            TextFormatFlags flags = BuildTextFlags(e.Header?.TextAlign ?? HorizontalAlignment.Left, allowWrap, useEllipsis: true);
            string text = e.SubItem.Text ?? string.Empty;
            if (allowWrap)
            {
                text = TrimTextToMaxLines(e.Graphics, text, Font, e.Bounds, maxLines);
            }
            Rectangle textBounds = AlignTextBounds(e.Graphics, e.Bounds, text, Font, flags);
            TextRenderer.DrawText(e.Graphics, text, Font, textBounds, textColor, flags);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            DrawListBorder(e.Graphics);
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
            return Rectangle.FromLTRB(bounds.Left + TextPadding, bounds.Top, bounds.Right - TextPadding, bounds.Bottom);
        }

        private static Rectangle AlignTextBounds(Graphics graphics, Rectangle bounds, string text, Font font, TextFormatFlags flags)
        {
            Rectangle textBounds = InflateTextBounds(bounds);
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
            if (string.IsNullOrWhiteSpace(text) || maxLines <= 0)
            {
                return string.Empty;
            }

            Rectangle textBounds = InflateTextBounds(bounds);
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
