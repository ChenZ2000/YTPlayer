using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using YTPlayer.Utils;

namespace YTPlayer
{
    [DefaultEvent(nameof(Scroll))]
    [DefaultProperty(nameof(Value))]
    internal sealed class AccessibleTrackBar : TrackBar
    {
        private const int WM_PAINT = 0x000F;
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_CAPTURECHANGED = 0x0215;
        private const int MinimumTrackLength = 1;

        private string _accessibleValueText = string.Empty;
        private bool _isDragging;

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Func<bool>? ShouldYieldNavigationKeys { get; set; }

        public AccessibleTrackBar()
        {
            SetStyle(
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.Selectable,
                true);

            AutoSize = false;
            AccessibleRole = AccessibleRole.Slider;
            TickStyle = TickStyle.None;
        }

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string AccessibleValueText
        {
            get => _accessibleValueText;
            set => SetAccessibleValueText(value, raiseValueChanged: ContainsFocus);
        }

        public void RaiseNameChanged()
        {
            NotifyAccessibleClients(AccessibleEvents.NameChange);
        }

        public void RaiseFocusChanged()
        {
            NotifyAccessibleClients(AccessibleEvents.Focus);
        }

        public void RaiseValueChanged()
        {
            NotifyAccessibleClients(AccessibleEvents.ValueChange);
        }

        public void RaiseDescriptionChanged()
        {
            NotifyAccessibleClients(AccessibleEvents.DescriptionChange);
        }

        public void SetAccessibleValueText(string? value, bool raiseValueChanged)
        {
            string next = value ?? string.Empty;
            if (string.Equals(_accessibleValueText, next, StringComparison.Ordinal))
            {
                return;
            }

            _accessibleValueText = next;
            if (raiseValueChanged)
            {
                RaiseValueChanged();
            }
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys keyCode = keyData & Keys.KeyCode;
            if (keyCode == Keys.Left ||
                keyCode == Keys.Right ||
                keyCode == Keys.Up ||
                keyCode == Keys.Down ||
                keyCode == Keys.Home ||
                keyCode == Keys.End ||
                keyCode == Keys.PageUp ||
                keyCode == Keys.PageDown)
            {
                if (ShouldYieldNavigationKeys?.Invoke() == true)
                {
                    return false;
                }

                return true;
            }

            return base.IsInputKey(keyData);
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            RaiseFocusChanged();
            RaiseValueChanged();
            RaiseNameChanged();
        }

        protected override void OnValueChanged(EventArgs e)
        {
            base.OnValueChanged(e);
            Invalidate();
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Invalidate();
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WM_LBUTTONDOWN:
                    if (HandleDirectMouseDown(m))
                    {
                        return;
                    }
                    break;
                case WM_MOUSEMOVE:
                    if (HandleDirectMouseMove(m))
                    {
                        return;
                    }
                    break;
                case WM_LBUTTONUP:
                    if (HandleDirectMouseUp(m))
                    {
                        return;
                    }
                    break;
                case WM_CAPTURECHANGED:
                    _isDragging = false;
                    break;
            }

            base.WndProc(ref m);

            if (m.Msg == WM_PAINT)
            {
                DrawThemedOverlay();
            }
        }

        private bool HandleDirectMouseDown(Message m)
        {
            if (!Enabled)
            {
                return false;
            }

            Point point = GetPointFromLParam(m.LParam);
            Focus();
            _isDragging = true;
            Capture = true;
            OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0));
            SetValueFromPoint(point, raiseScroll: true);
            return true;
        }

        private bool HandleDirectMouseMove(Message m)
        {
            if (!_isDragging)
            {
                return false;
            }

            Point point = GetPointFromLParam(m.LParam);
            SetValueFromPoint(point, raiseScroll: true);
            OnMouseMove(new MouseEventArgs(MouseButtons.Left, 0, point.X, point.Y, 0));
            return true;
        }

        private bool HandleDirectMouseUp(Message m)
        {
            if (!_isDragging)
            {
                return false;
            }

            Point point = GetPointFromLParam(m.LParam);
            SetValueFromPoint(point, raiseScroll: true);
            _isDragging = false;
            Capture = false;
            OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0));
            return true;
        }

        private void SetValueFromPoint(Point point, bool raiseScroll)
        {
            RectangleF track = GetTrackBounds();
            if (track.Width < MinimumTrackLength || track.Height < MinimumTrackLength || Maximum <= Minimum)
            {
                return;
            }

            double percent;
            if (Orientation == Orientation.Vertical)
            {
                double y = Math.Max(track.Top, Math.Min(track.Bottom, point.Y));
                percent = 1.0 - ((y - track.Top) / track.Height);
            }
            else
            {
                double x = Math.Max(track.Left, Math.Min(track.Right, point.X));
                percent = (x - track.Left) / track.Width;
            }

            int next = Minimum + (int)Math.Round((Maximum - Minimum) * percent, MidpointRounding.AwayFromZero);
            next = Math.Max(Minimum, Math.Min(Maximum, next));
            if (Value == next)
            {
                return;
            }

            Value = next;
            if (raiseScroll)
            {
                OnScroll(EventArgs.Empty);
            }
        }

        private void NotifyAccessibleClients(AccessibleEvents accEvent)
        {
            NotifyAccessibleClients(accEvent, 0);
            NotifyAccessibleClients(accEvent, -1);
        }

        private void NotifyAccessibleClients(AccessibleEvents accEvent, int childId)
        {
            try
            {
                AccessibilityNotifyClients(accEvent, childId);
            }
            catch
            {
            }
        }

        private void DrawThemedOverlay()
        {
            if (!IsHandleCreated || Width <= 0 || Height <= 0)
            {
                return;
            }

            try
            {
                using (Graphics graphics = Graphics.FromHwnd(Handle))
                {
                    DrawTrackBar(graphics);
                }
            }
            catch
            {
            }
        }

        private void DrawTrackBar(Graphics graphics)
        {
            var palette = ThemeManager.Current;
            Color background = Parent?.BackColor ?? BackColor;
            Color trackColor = Enabled ? palette.Divider : palette.Border;
            Color fillColor = Enabled ? palette.Accent : palette.Border;
            Color thumbColor = Enabled ? palette.Accent : palette.Border;
            Color thumbBorder = BlendColor(thumbColor, palette.TextPrimary, 0.35f);
            Color thumbHighlight = BlendColor(thumbColor, Color.White, 0.35f);

            using (var backgroundBrush = new SolidBrush(background))
            {
                graphics.FillRectangle(backgroundBrush, ClientRectangle);
            }

            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

            RectangleF trackBounds = GetTrackBounds();
            if (trackBounds.Width < MinimumTrackLength || trackBounds.Height < MinimumTrackLength)
            {
                return;
            }

            float trackRadius = Math.Min(trackBounds.Width, trackBounds.Height) / 2f;
            using (GraphicsPath trackPath = BuildRoundedPath(trackBounds, trackRadius))
            using (var trackBrush = new SolidBrush(trackColor))
            {
                graphics.FillPath(trackBrush, trackPath);
            }

            DrawFill(graphics, trackBounds, trackRadius, fillColor);
            DrawThumb(graphics, trackBounds, thumbColor, thumbBorder, thumbHighlight, palette.FocusRing);
        }

        private RectangleF GetTrackBounds()
        {
            int thumbDiameter = GetThumbDiameter();
            int trackThickness = GetTrackThickness();
            float margin = thumbDiameter / 2f + 4f;

            if (Orientation == Orientation.Vertical)
            {
                float left = (Width - trackThickness) / 2f;
                float top = margin;
                float height = Math.Max(MinimumTrackLength, Height - (margin * 2f));
                return new RectangleF(left, top, trackThickness, height);
            }

            float x = margin;
            float y = (Height - trackThickness) / 2f;
            float width = Math.Max(MinimumTrackLength, Width - (margin * 2f));
            return new RectangleF(x, y, width, trackThickness);
        }

        private float GetValuePercent()
        {
            int range = Math.Max(1, Maximum - Minimum);
            return (Value - Minimum) / (float)range;
        }

        private PointF GetThumbCenter(RectangleF trackBounds)
        {
            float percent = GetValuePercent();
            if (Orientation == Orientation.Vertical)
            {
                float y = trackBounds.Bottom - (trackBounds.Height * percent);
                return new PointF(trackBounds.Left + (trackBounds.Width / 2f), y);
            }

            float x = trackBounds.Left + (trackBounds.Width * percent);
            return new PointF(x, trackBounds.Top + (trackBounds.Height / 2f));
        }

        private int GetTrackThickness()
        {
            int basis = Orientation == Orientation.Vertical ? Width : Height;
            return Math.Max(7, Math.Min(9, Math.Max(1, basis) / 3));
        }

        private int GetThumbDiameter()
        {
            int basis = Orientation == Orientation.Vertical ? Width : Height;
            return Math.Max(14, Math.Min(18, Math.Max(1, basis) - 12));
        }

        private void DrawFill(Graphics graphics, RectangleF trackBounds, float trackRadius, Color fillColor)
        {
            PointF center = GetThumbCenter(trackBounds);
            RectangleF fillBounds;
            if (Orientation == Orientation.Vertical)
            {
                fillBounds = RectangleF.FromLTRB(trackBounds.Left, center.Y, trackBounds.Right, trackBounds.Bottom);
            }
            else
            {
                fillBounds = RectangleF.FromLTRB(trackBounds.Left, trackBounds.Top, center.X, trackBounds.Bottom);
            }

            if (fillBounds.Width <= 0.5f || fillBounds.Height <= 0.5f)
            {
                return;
            }

            using (GraphicsPath fillPath = BuildRoundedPath(fillBounds, trackRadius))
            using (var fillBrush = new SolidBrush(fillColor))
            {
                graphics.FillPath(fillBrush, fillPath);
            }
        }

        private void DrawThumb(
            Graphics graphics,
            RectangleF trackBounds,
            Color thumbColor,
            Color thumbBorder,
            Color thumbHighlight,
            Color focusRing)
        {
            PointF center = GetThumbCenter(trackBounds);
            float size = GetThumbDiameter();
            RectangleF thumbBounds = new RectangleF(
                center.X - (size / 2f),
                center.Y - (size / 2f),
                size,
                size);

            using (var thumbBrush = new SolidBrush(thumbColor))
            {
                graphics.FillEllipse(thumbBrush, thumbBounds);
            }

            using (var borderPen = new Pen(thumbBorder))
            {
                graphics.DrawEllipse(borderPen, thumbBounds);
            }

            RectangleF highlightBounds = thumbBounds;
            highlightBounds.Inflate(-4f, -4f);
            if (highlightBounds.Width > 0f && highlightBounds.Height > 0f)
            {
                using (var highlightBrush = new SolidBrush(thumbHighlight))
                {
                    graphics.FillEllipse(highlightBrush, highlightBounds);
                }
            }

            if (Focused || ContainsFocus)
            {
                RectangleF focusBounds = thumbBounds;
                focusBounds.Inflate(3f, 3f);
                using (var focusPen = new Pen(focusRing, 2f))
                {
                    graphics.DrawEllipse(focusPen, focusBounds);
                }
            }
        }

        private static GraphicsPath BuildRoundedPath(RectangleF bounds, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            if (bounds.Width <= 0f || bounds.Height <= 0f)
            {
                return path;
            }

            radius = Math.Max(0f, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2f));
            if (radius <= 0f)
            {
                path.AddRectangle(bounds);
                return path;
            }

            float diameter = radius * 2f;
            RectangleF arc = new RectangleF(bounds.Left, bounds.Top, diameter, diameter);
            path.AddArc(arc, 180f, 90f);
            arc.X = bounds.Right - diameter;
            path.AddArc(arc, 270f, 90f);
            arc.Y = bounds.Bottom - diameter;
            path.AddArc(arc, 0f, 90f);
            arc.X = bounds.Left;
            path.AddArc(arc, 90f, 90f);
            path.CloseFigure();
            return path;
        }

        private static Color BlendColor(Color baseColor, Color overlay, float amount)
        {
            amount = Math.Max(0f, Math.Min(1f, amount));
            int r = (int)(baseColor.R + ((overlay.R - baseColor.R) * amount));
            int g = (int)(baseColor.G + ((overlay.G - baseColor.G) * amount));
            int b = (int)(baseColor.B + ((overlay.B - baseColor.B) * amount));
            return Color.FromArgb(baseColor.A, r, g, b);
        }

        private static Point GetPointFromLParam(IntPtr lParam)
        {
            int value = lParam.ToInt32();
            int x = unchecked((short)(value & 0xFFFF));
            int y = unchecked((short)((value >> 16) & 0xFFFF));
            return new Point(x, y);
        }
    }
}
