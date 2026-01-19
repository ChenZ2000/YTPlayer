using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using YTPlayer.Utils;

namespace YTPlayer
{
    internal sealed class SafeListView : ListView
    {
        public SafeListView()
        {
            OwnerDraw = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            UpdateStyles();

            if (SmallImageList == null)
            {
                SmallImageList = new ImageList
                {
                    ImageSize = new Size(1, 28),
                    ColorDepth = ColorDepth.Depth8Bit
                };
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

            TextFormatFlags flags = BuildTextFlags(e.Header?.TextAlign ?? HorizontalAlignment.Left);
            Rectangle textBounds = InflateTextBounds(e.Bounds);
            TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? string.Empty, Font, textBounds, palette.TextSecondary, flags);

            using (var pen = new Pen(palette.Divider))
            {
                int y = e.Bounds.Bottom - 1;
                e.Graphics.DrawLine(pen, e.Bounds.Left, y, e.Bounds.Right, y);
            }
        }

        protected override void OnDrawItem(DrawListViewItemEventArgs e)
        {
            if (View == View.Details)
            {
                DrawRowBackground(e);
                return;
            }

            DrawRowBackground(e);
            ThemePalette palette = ThemeManager.Current;
            Color textColor = ResolveItemTextColor(e.Item, palette);
            TextFormatFlags flags = BuildTextFlags(HorizontalAlignment.Left);
            Rectangle textBounds = InflateTextBounds(e.Bounds);
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

            ThemePalette palette = ThemeManager.Current;
            Color textColor = ResolveSubItemTextColor(e.SubItem, e.Item, palette);
            TextFormatFlags flags = BuildTextFlags(e.Header?.TextAlign ?? HorizontalAlignment.Left);
            Rectangle textBounds = InflateTextBounds(e.Bounds);
            TextRenderer.DrawText(e.Graphics, e.SubItem.Text, Font, textBounds, textColor, flags);
        }

        private void DrawRowBackground(DrawListViewItemEventArgs e)
        {
            ThemePalette palette = ThemeManager.Current;
            bool selected = e.Item.Selected;
            Color backColor = selected ? palette.AccentSoft : BackColor;
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

            using (var pen = new Pen(palette.Divider))
            {
                int y = e.Bounds.Bottom - 1;
                e.Graphics.DrawLine(pen, 0, y, ClientRectangle.Width, y);
            }
        }

        private static TextFormatFlags BuildTextFlags(HorizontalAlignment alignment)
        {
            TextFormatFlags flags = TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;
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
            const int padding = 6;
            return Rectangle.FromLTRB(bounds.Left + padding, bounds.Top, bounds.Right - padding, bounds.Bottom);
        }

        private static Color ResolveItemTextColor(ListViewItem item, ThemePalette palette)
        {
            Color color = item.ForeColor;
            if (color.IsEmpty || color == SystemColors.WindowText)
            {
                color = palette.TextPrimary;
            }
            return color;
        }

        private static Color ResolveSubItemTextColor(ListViewItem.ListViewSubItem subItem, ListViewItem item, ThemePalette palette)
        {
            Color color = subItem.ForeColor;
            if (color.IsEmpty || color == SystemColors.WindowText)
            {
                color = item.ForeColor;
            }
            if (color.IsEmpty || color == SystemColors.WindowText)
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
