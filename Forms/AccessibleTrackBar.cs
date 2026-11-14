using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using YTPlayer.Utils;

namespace YTPlayer
{
    internal sealed class SliderGestureEventArgs : EventArgs
    {
        internal double Position { get; }
        internal bool Canceled { get; }
        internal SliderGestureEventArgs(double position, bool canceled = false)
        { Position = position; Canceled = canceled; }
    }

    // Keep the native trackbar handle, integer range and accessibility provider.
    // UserPaint gives both WM_PAINT and WM_PRINTCLIENT the same buffered renderer.
    [DefaultEvent(nameof(Scroll)), DefaultProperty(nameof(Value))]
    internal sealed class AccessibleTrackBar : TrackBar
    {
        private string _accessibleValueText = string.Empty;
        private double _visualPosition;
        private double _visualMaximum = double.NaN;
        private double _gestureOrigin;
        private double _hoverPosition;
        private float _grabOffset;
        private bool _isDragging;
        private bool _hovered;
        private bool _settingPointerValue;
        private readonly SolidBrush _brush = new SolidBrush(Color.Black);
        private readonly Pen _pen = new Pen(Color.Black);
        private readonly ControlVisibilityObserver _visibilityObserver;

        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Func<bool>? ShouldYieldNavigationKeys { get; set; }
        internal event EventHandler<SliderGestureEventArgs>? GestureStarted;
        internal event EventHandler<SliderGestureEventArgs>? GesturePreview;
        internal event EventHandler<SliderGestureEventArgs>? GestureEnded;
        internal bool IsDragging => _isDragging;
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal Func<double, double?, string>? PreviewText { get; set; }
        // Geometry-only injection; never changes Windows' actual DPI or accessibility.
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal int? GeometryDpi { get; set; }
        private float ScaleFactor => (GeometryDpi ?? DeviceDpi) / 96f;
        internal int PreferredVisualHeight => Font.Height + (int)Math.Ceiling(38 * ScaleFactor);
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal double VisualMaximum
        {
            get => double.IsNaN(_visualMaximum) ? Maximum : _visualMaximum;
            set { if (_visualMaximum == value) return; _visualMaximum = value; Invalidate(); }
        }
        internal double VisualPosition => Clamp(_visualPosition);

        public AccessibleTrackBar()
        {
            _visibilityObserver = new ControlVisibilityObserver(this, () =>
            {
                if (!Visible || !Enabled || FindForm()?.WindowState == FormWindowState.Minimized)
                { _hovered = false; EndGesture(canceled: true); }
            });
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            AutoSize = false;
            AccessibleRole = AccessibleRole.Slider;
            TickStyle = TickStyle.None;
        }

        internal void SetVisualPosition(double value)
        {
            if (!_isDragging) SetVisualPositionCore(value);
        }

        private void SetVisualPositionCore(double value)
        {
            double next = Clamp(value);
            if (Math.Abs(next - _visualPosition) < 0.000001) return;
            _visualPosition = next;
            Invalidate();
        }

        private double Clamp(double value) => double.IsNaN(value) || double.IsInfinity(value)
            ? Minimum : Math.Max(Minimum, Math.Min(Math.Max(Minimum, VisualMaximum), value));

        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string AccessibleValueText
        {
            get => _accessibleValueText;
            set => SetAccessibleValueText(value, ContainsFocus);
        }
        public void RaiseNameChanged() => NotifyAccessibleClients(AccessibleEvents.NameChange);
        public void RaiseFocusChanged() => NotifyAccessibleClients(AccessibleEvents.Focus);
        public void RaiseValueChanged() => NotifyAccessibleClients(AccessibleEvents.ValueChange);
        public void RaiseDescriptionChanged() => NotifyAccessibleClients(AccessibleEvents.DescriptionChange);

        public void SetAccessibleValueText(string? value, bool raiseValueChanged)
        {
            string next = value ?? string.Empty;
            if (_accessibleValueText == next) return;
            _accessibleValueText = next;
            if (raiseValueChanged) RaiseValueChanged();
        }

        private void NotifyAccessibleClients(AccessibleEvents accEvent)
        {
            try { AccessibilityNotifyClients(accEvent, 0); AccessibilityNotifyClients(accEvent, -1); }
            catch (InvalidOperationException) { }
        }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Left: case Keys.Right: case Keys.Up: case Keys.Down:
                case Keys.Home: case Keys.End: case Keys.PageUp: case Keys.PageDown:
                    return ShouldYieldNavigationKeys?.Invoke() != true;
                default: return base.IsInputKey(keyData);
            }
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            RaiseFocusChanged(); RaiseValueChanged(); RaiseNameChanged(); Invalidate();
        }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnValueChanged(EventArgs e)
        {
            if (!_settingPointerValue && !_isDragging) _visualPosition = Value;
            base.OnValueChanged(e);
            Invalidate();
        }
        protected override void OnEnabledChanged(EventArgs e)
        {
            if (!Enabled) { _hovered = false; EndGesture(canceled: true); }
            base.OnEnabledChanged(e); Invalidate();
        }
        protected override void OnVisibleChanged(EventArgs e)
        {
            if (!Visible) { _hovered = false; EndGesture(canceled: true); }
            base.OnVisibleChanged(e);
        }
        protected override void OnHandleDestroyed(EventArgs e)
        { EndGesture(canceled: true); base.OnHandleDestroyed(e); }
        protected override void OnMouseLeave(EventArgs e)
        { _hovered = false; base.OnMouseLeave(e); Invalidate(); }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!Enabled || _isDragging) return;
            bool wasHovered = _hovered;
            _hovered = GetHitBounds().Contains(e.Location);
            double next = PositionFromPoint(e.Location, 0);
            if (wasHovered != _hovered || (_hovered && next != _hoverPosition))
            { _hoverPosition = next; Invalidate(); }
        }

        protected override void WndProc(ref Message m)
        {
            Point point;
            switch (m.Msg)
            {
                case 0x201: // WM_LBUTTONDOWN
                case 0x203: // WM_LBUTTONDBLCLK: don't start a native second gesture.
                    point = PointFromLParam(m.LParam);
                    if (!Enabled || VisualMaximum <= Minimum || !GetHitBounds().Contains(point)) return;
                    Focus();
                    PointF center = GetThumbCenter();
                    float radius = 12 * ScaleFactor;
                    bool onThumb = Math.Abs(point.X - center.X) <= radius && Math.Abs(point.Y - center.Y) <= radius;
                    _grabOffset = onThumb ? (Orientation == Orientation.Vertical ? point.Y - center.Y : point.X - center.X) : 0;
                    _gestureOrigin = VisualPosition;
                    _isDragging = true;
                    Capture = true;
                    GestureStarted?.Invoke(this, new SliderGestureEventArgs(VisualPosition));
                    OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0));
                    if (_isDragging) UpdatePointer(point);
                    return;
                case 0x200: // WM_MOUSEMOVE
                    if (!_isDragging) break;
                    point = PointFromLParam(m.LParam);
                    UpdatePointer(point);
                    OnMouseMove(new MouseEventArgs(MouseButtons.Left, 0, point.X, point.Y, 0));
                    return;
                case 0x202: // WM_LBUTTONUP
                    if (!_isDragging) break;
                    point = PointFromLParam(m.LParam);
                    UpdatePointer(point);
                    EndGesture(canceled: false);
                    OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0));
                    return;
                case 0x215: // WM_CAPTURECHANGED
                case 0x1F:  // WM_CANCELMODE
                    EndGesture(canceled: true);
                    break;
            }
            base.WndProc(ref m);
        }

        internal void EndGesture(bool canceled)
        {
            if (!_isDragging) return;
            // Clear first: releasing capture reenters WndProc synchronously.
            _isDragging = false;
            _hovered = false;
            if (Capture) Capture = false;
            GestureEnded?.Invoke(this, new SliderGestureEventArgs(VisualPosition, canceled));
            Invalidate();
        }

        private void UpdatePointer(Point point)
        {
            double position = PositionFromPoint(point, _grabOffset);
            SetVisualPositionCore(position);
            int next = Math.Max(Minimum, Math.Min(Maximum, (int)Math.Round(position, MidpointRounding.AwayFromZero)));
            if (Value != next)
            {
                _settingPointerValue = true;
                try { Value = next; }
                finally { _settingPointerValue = false; }
                OnScroll(EventArgs.Empty);
            }
            if (_isDragging) GesturePreview?.Invoke(this, new SliderGestureEventArgs(position));
        }

        internal RectangleF GetTrackBounds()
        {
            float margin = 16 * ScaleFactor; // Stable travel bounds for every thumb state.
            float thickness = 6 * ScaleFactor;
            if (Orientation == Orientation.Vertical)
                return new RectangleF((Width - thickness) / 2, margin, thickness, Math.Max(1, Height - 2 * margin));
            float band = Math.Min(Font.Height + 4 * ScaleFactor, Math.Max(0, Height - 32 * ScaleFactor));
            float y = band + (Height - band) / 2;
            return new RectangleF(margin, y - thickness / 2, Math.Max(1, Width - 2 * margin), thickness);
        }

        internal RectangleF GetHitBounds()
        {
            var track = GetTrackBounds();
            return Orientation == Orientation.Vertical
                ? new RectangleF(track.Left + track.Width / 2 - 16 * ScaleFactor, 0, 32 * ScaleFactor, Height)
                : new RectangleF(0, track.Top + track.Height / 2 - 16 * ScaleFactor, Width, 32 * ScaleFactor);
        }

        internal PointF GetThumbCenter()
        {
            var track = GetTrackBounds();
            double range = VisualMaximum - Minimum;
            float percent = range > 0 ? (float)((VisualPosition - Minimum) / range) : 0;
            return Orientation == Orientation.Vertical
                ? new PointF(track.Left + track.Width / 2, track.Bottom - track.Height * percent)
                : new PointF(track.Left + track.Width * percent, track.Top + track.Height / 2);
        }

        private double PositionFromPoint(Point point, float offset)
        {
            var track = GetTrackBounds();
            double percent = Orientation == Orientation.Vertical
                ? (track.Bottom - point.Y + offset) / track.Height : (point.X - offset - track.Left) / track.Width;
            return Clamp(Minimum + (VisualMaximum - Minimum) * percent);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e)
        {
            DrawTrackBar(e.Graphics);
            base.OnPaint(e);
        }

        private void FillCapsule(Graphics graphics, RectangleF bounds, Color color)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            _brush.Color = color;
            float diameter = Math.Min(bounds.Width, bounds.Height);
            if (bounds.Width >= bounds.Height)
            {
                graphics.FillRectangle(_brush, bounds.X + diameter / 2, bounds.Y, Math.Max(0, bounds.Width - diameter), bounds.Height);
                graphics.FillEllipse(_brush, bounds.X, bounds.Y, diameter, diameter);
                graphics.FillEllipse(_brush, bounds.Right - diameter, bounds.Y, diameter, diameter);
            }
            else
            {
                graphics.FillRectangle(_brush, bounds.X, bounds.Y + diameter / 2, bounds.Width, Math.Max(0, bounds.Height - diameter));
                graphics.FillEllipse(_brush, bounds.X, bounds.Y, diameter, diameter);
                graphics.FillEllipse(_brush, bounds.X, bounds.Bottom - diameter, diameter, diameter);
            }
        }

        private void DrawTrackBar(Graphics graphics)
        {
            var palette = ThemeManager.Current;
            graphics.Clear(Parent?.BackColor ?? BackColor);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var track = GetTrackBounds();
            PointF center = GetThumbCenter();
            FillCapsule(graphics, track, Enabled ? palette.ControlBorder : palette.Divider);
            RectangleF fill = Orientation == Orientation.Vertical
                ? RectangleF.FromLTRB(track.Left, center.Y, track.Right, track.Bottom)
                : RectangleF.FromLTRB(track.Left, track.Top, center.X, track.Bottom);
            FillCapsule(graphics, fill, Enabled ? palette.Accent : palette.TextSecondary);
            float diameter = (_isDragging || _hovered ? 20 : 16) * ScaleFactor;
            var thumb = new RectangleF(center.X - diameter / 2, center.Y - diameter / 2, diameter, diameter);
            _brush.Color = Enabled ? palette.Accent : palette.SurfaceAlt;
            graphics.FillEllipse(_brush, thumb);
            _pen.Color = Enabled ? palette.AccentText : palette.TextSecondary;
            _pen.Width = 2 * ScaleFactor;
            graphics.DrawEllipse(_pen, thumb);
            if (Focused)
            {
                thumb.Inflate(3 * ScaleFactor, 3 * ScaleFactor);
                _pen.Color = palette.FocusRing;
                graphics.DrawEllipse(_pen, thumb);
            }
            if ((_hovered || _isDragging) && Enabled && PreviewText != null && Orientation == Orientation.Horizontal)
            {
                double value = _isDragging ? VisualPosition : _hoverPosition;
                string text = PreviewText(value, _isDragging ? value - _gestureOrigin : (double?)null);
                TextRenderer.DrawText(graphics, text, Font, GetHintBounds(text, value), palette.TextPrimary,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            }
        }

        internal Rectangle GetHintBounds(string text, double value)
        {
            var track = GetTrackBounds();
            int width = Math.Min(Width, TextRenderer.MeasureText(text, Font, Size.Empty, TextFormatFlags.SingleLine).Width);
            double ratio = VisualMaximum > Minimum ? (Clamp(value) - Minimum) / (VisualMaximum - Minimum) : 0;
            int x = (int)Math.Round(track.Left + track.Width * ratio - width / 2.0);
            return new Rectangle(Math.Max(0, Math.Min(Width - width, x)), 0, width, Font.Height + (int)(4 * ScaleFactor));
        }

        private static Point PointFromLParam(IntPtr value) => new Point(unchecked((short)value.ToInt64()), unchecked((short)(value.ToInt64() >> 16)));
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _visibilityObserver.Dispose(); EndGesture(canceled: true); _brush.Dispose(); _pen.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
