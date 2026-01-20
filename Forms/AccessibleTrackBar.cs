using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using YTPlayer.Utils;

namespace YTPlayer
{
    internal sealed class AccessibleTrackBar : TrackBar
    {
        private const int WM_PAINT = 0x000F;
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_MOUSELEAVE = 0x02A3;
        private const int TBM_GETCHANNELRECT = 0x0400 + 26;
        private const int TBM_GETTHUMBRECT = 0x0400 + 25;

        public void RaiseNameChanged()
        {
            try
            {
                AccessibilityNotifyClients(AccessibleEvents.NameChange, 0);
            }
            catch
            {
            }
        }

        public void RaiseFocusChanged()
        {
            try
            {
                AccessibilityNotifyClients(AccessibleEvents.Focus, 0);
            }
            catch
            {
            }
        }

        public void RaiseValueChanged()
        {
            try
            {
                AccessibilityNotifyClients(AccessibleEvents.ValueChange, 0);
            }
            catch
            {
            }
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            RaiseFocusChanged();
            RaiseValueChanged();
            RaiseNameChanged();
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            Invalidate();
        }

        protected override void OnValueChanged(EventArgs e)
        {
            base.OnValueChanged(e);
            Invalidate();
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WM_PAINT ||
                m.Msg == WM_MOUSEMOVE ||
                m.Msg == WM_LBUTTONDOWN ||
                m.Msg == WM_LBUTTONUP ||
                m.Msg == WM_MOUSELEAVE)
            {
                DrawThemedOverlay();
            }
        }

        private void DrawThemedOverlay()
        {
            if (!IsHandleCreated || Width <= 0 || Height <= 0)
            {
                return;
            }

            var palette = ThemeManager.Current;
            using (Graphics graphics = Graphics.FromHwnd(Handle))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;

                Color background = Parent?.BackColor ?? BackColor;
                using (var backgroundBrush = new SolidBrush(background))
                {
                    graphics.FillRectangle(backgroundBrush, ClientRectangle);
                }

                Rectangle channel = GetChannelRect();
                Rectangle thumb = GetThumbRect();
                if (channel.Width <= 0 || channel.Height <= 0)
                {
                    channel = new Rectangle(6, Height / 2 - 2, Math.Max(1, Width - 12), 4);
                }

                int trackHeight = Math.Max(7, Math.Min(9, Height / 3));
                int trackY = channel.Top + (channel.Height - trackHeight) / 2;
                Rectangle trackBounds = new Rectangle(channel.Left, trackY, channel.Width, trackHeight);

                Color trackColor = Enabled ? palette.Divider : palette.Border;
                Color fillColor = Enabled ? palette.Accent : palette.Border;
                Color thumbColor = Enabled ? palette.Accent : palette.Border;
                Color thumbBorder = BlendColor(thumbColor, palette.TextPrimary, 0.35f);
                Color thumbHighlight = BlendColor(thumbColor, Color.White, 0.35f);

                using (var trackBrush = new SolidBrush(trackColor))
                using (var trackPath = BuildRoundedPath(trackBounds, trackHeight / 2))
                {
                    graphics.FillPath(trackBrush, trackPath);
                }

                Rectangle progress = GetProgressRect(trackBounds, thumb);
                if (progress.Width > 0 && progress.Height > 0)
                {
                    using (var fillBrush = new SolidBrush(fillColor))
                    using (var fillPath = BuildRoundedPath(progress, trackHeight / 2))
                    {
                        graphics.FillPath(fillBrush, fillPath);
                    }
                }

                int thumbSize = Math.Max(14, Math.Min(18, trackHeight * 2 + 2));
                int thumbCenterX = thumb.Left + thumb.Width / 2;
                int thumbCenterY = thumb.Top + thumb.Height / 2;
                Rectangle thumbBounds = new Rectangle(
                    thumbCenterX - thumbSize / 2,
                    thumbCenterY - thumbSize / 2,
                    thumbSize,
                    thumbSize);
                thumbBounds = ClampToClient(thumbBounds);
                if (thumbBounds.Width > 0 && thumbBounds.Height > 0)
                {
                    using (var thumbBrush = new SolidBrush(thumbColor))
                    {
                        graphics.FillEllipse(thumbBrush, thumbBounds);
                    }

                    using (var borderPen = new Pen(thumbBorder))
                    {
                        graphics.DrawEllipse(borderPen, thumbBounds);
                    }

                    Rectangle highlightBounds = thumbBounds;
                    highlightBounds.Inflate(-4, -4);
                    highlightBounds = ClampToClient(highlightBounds);
                    if (highlightBounds.Width > 0 && highlightBounds.Height > 0)
                    {
                        using (var highlightBrush = new SolidBrush(thumbHighlight))
                        {
                            graphics.FillEllipse(highlightBrush, highlightBounds);
                        }
                    }

                    if (Focused)
                    {
                        using (var focusPen = new Pen(palette.FocusRing, 2))
                        {
                            Rectangle focusBounds = thumbBounds;
                            focusBounds.Inflate(3, 3);
                            focusBounds = ClampToClient(focusBounds);
                            graphics.DrawEllipse(focusPen, focusBounds);
                        }
                    }
                }
            }
        }

        private Rectangle GetChannelRect()
        {
            RECT rect = new RECT();
            SendMessage(Handle, TBM_GETCHANNELRECT, IntPtr.Zero, ref rect);
            return rect.ToRectangle();
        }

        private Rectangle GetThumbRect()
        {
            RECT rect = new RECT();
            SendMessage(Handle, TBM_GETTHUMBRECT, IntPtr.Zero, ref rect);
            return rect.ToRectangle();
        }

        private Rectangle GetProgressRect(Rectangle trackBounds, Rectangle thumbBounds)
        {
            if (Orientation == Orientation.Vertical)
            {
                int center = thumbBounds.Top + thumbBounds.Height / 2;
                int height = Math.Max(0, trackBounds.Bottom - center);
                return new Rectangle(trackBounds.Left, trackBounds.Bottom - height, trackBounds.Width, height);
            }

            int x = thumbBounds.Left + thumbBounds.Width / 2;
            int width = Math.Max(0, Math.Min(trackBounds.Width, x - trackBounds.Left));
            return new Rectangle(trackBounds.Left, trackBounds.Top, width, trackBounds.Height);
        }

        private Rectangle ClampToClient(Rectangle rect)
        {
            Rectangle client = ClientRectangle;
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                return Rectangle.Empty;
            }

            int x = Math.Max(client.Left, Math.Min(rect.X, client.Right - rect.Width));
            int y = Math.Max(client.Top, Math.Min(rect.Y, client.Bottom - rect.Height));
            return new Rectangle(x, y, rect.Width, rect.Height);
        }

        private static GraphicsPath BuildRoundedPath(Rectangle bounds, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            if (radius <= 0)
            {
                path.AddRectangle(bounds);
                return path;
            }

            int diameter = radius * 2;
            Rectangle arc = new Rectangle(bounds.Location, new Size(diameter, diameter));
            path.AddArc(arc, 180, 90);
            arc.X = bounds.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = bounds.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = bounds.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static Color BlendColor(Color baseColor, Color overlay, float amount)
        {
            amount = Math.Max(0f, Math.Min(1f, amount));
            int r = (int)(baseColor.R + (overlay.R - baseColor.R) * amount);
            int g = (int)(baseColor.G + (overlay.G - baseColor.G) * amount);
            int b = (int)(baseColor.B + (overlay.B - baseColor.B) * amount);
            return Color.FromArgb(baseColor.A, r, g, b);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref RECT lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;

            public Rectangle ToRectangle()
            {
                return Rectangle.FromLTRB(Left, Top, Right, Bottom);
            }
        }
    }
}
