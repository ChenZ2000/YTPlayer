using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace YTPlayer.Utils
{
    internal sealed class ThemedProgressBar : Control
    {
        private const int DefaultCornerRadius = 8;
        private int _minimum;
        private int _maximum = 100;
        private int _value;
        private ProgressBarStyle _style = ProgressBarStyle.Continuous;
        private int _marqueeAnimationSpeed = 30;
        private int _marqueeOffset;
        private Timer? _marqueeTimer;

        public ThemedProgressBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            TabStop = false;
            Height = 18;
        }

        [DefaultValue(0)]
        public int Minimum
        {
            get => _minimum;
            set
            {
                int next = value;
                if (next > _maximum)
                {
                    _maximum = next;
                }
                _minimum = next;
                if (_value < _minimum)
                {
                    _value = _minimum;
                }
                Invalidate();
            }
        }

        [DefaultValue(100)]
        public int Maximum
        {
            get => _maximum;
            set
            {
                int next = Math.Max(value, _minimum);
                _maximum = next;
                if (_value > _maximum)
                {
                    _value = _maximum;
                }
                Invalidate();
            }
        }

        [DefaultValue(0)]
        public int Value
        {
            get => _value;
            set
            {
                int next = Math.Max(_minimum, Math.Min(_maximum, value));
                if (_value == next)
                {
                    return;
                }
                _value = next;
                Invalidate();
            }
        }

        [DefaultValue(typeof(ProgressBarStyle), "Continuous")]
        public ProgressBarStyle Style
        {
            get => _style;
            set
            {
                if (_style == value)
                {
                    return;
                }
                _style = value;
                UpdateMarqueeState();
                Invalidate();
            }
        }

        [DefaultValue(30)]
        public int MarqueeAnimationSpeed
        {
            get => _marqueeAnimationSpeed;
            set
            {
                int next = Math.Max(0, value);
                _marqueeAnimationSpeed = next;
                UpdateMarqueeState();
            }
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            UpdateMarqueeState();
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Invalidate();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            StopMarquee();
            base.OnHandleDestroyed(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            var palette = ThemeManager.Current;
            Rectangle bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return;
            }

            int radius = Math.Min(DefaultCornerRadius, Math.Max(2, bounds.Height / 2));
            Color trackColor = Enabled ? palette.SurfaceAlt : palette.Divider;
            Color fillColor = Enabled ? palette.Accent : palette.Border;
            Color borderColor = palette.Border;

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            using (GraphicsPath trackPath = BuildRoundedPath(bounds, radius))
            {
                using (var trackBrush = new SolidBrush(trackColor))
                {
                    e.Graphics.FillPath(trackBrush, trackPath);
                }

                if (_style == ProgressBarStyle.Marquee)
                {
                    DrawMarquee(e.Graphics, bounds, radius, fillColor);
                }
                else
                {
                    DrawContinuous(e.Graphics, bounds, radius, fillColor);
                }

                using (var borderPen = new Pen(borderColor))
                {
                    e.Graphics.DrawPath(borderPen, trackPath);
                }
            }
        }

        private void DrawContinuous(Graphics graphics, Rectangle bounds, int radius, Color fillColor)
        {
            int range = Math.Max(1, _maximum - _minimum);
            float percent = (_value - _minimum) / (float)range;
            int fillWidth = (int)Math.Round(bounds.Width * percent);
            if (fillWidth <= 0)
            {
                return;
            }

            Rectangle fillBounds = new Rectangle(bounds.X, bounds.Y, fillWidth, bounds.Height);
            using (GraphicsPath fillPath = BuildRoundedPath(fillBounds, radius))
            using (var fillBrush = new SolidBrush(fillColor))
            {
                graphics.FillPath(fillBrush, fillPath);
            }
        }

        private void DrawMarquee(Graphics graphics, Rectangle bounds, int radius, Color fillColor)
        {
            int segmentWidth = Math.Max(36, bounds.Width / 4);
            int travel = bounds.Width + segmentWidth;
            int offset = _marqueeOffset % Math.Max(1, travel);
            int x = bounds.X - segmentWidth + offset;

            Rectangle segment = new Rectangle(x, bounds.Y, segmentWidth, bounds.Height);
            Rectangle clip = Rectangle.Intersect(segment, bounds);
            if (clip.Width <= 0 || clip.Height <= 0)
            {
                return;
            }

            using (GraphicsPath segmentPath = BuildRoundedPath(clip, radius))
            using (var fillBrush = new SolidBrush(fillColor))
            {
                graphics.FillPath(fillBrush, segmentPath);
            }
        }

        private void UpdateMarqueeState()
        {
            if (_style == ProgressBarStyle.Marquee && Visible && _marqueeAnimationSpeed > 0)
            {
                StartMarquee();
            }
            else
            {
                StopMarquee();
            }
        }

        private void StartMarquee()
        {
            if (_marqueeTimer == null)
            {
                _marqueeTimer = new Timer();
                _marqueeTimer.Tick += (_, _) =>
                {
                    _marqueeOffset += 6;
                    Invalidate();
                };
            }

            _marqueeTimer.Interval = Math.Max(10, _marqueeAnimationSpeed);
            if (!_marqueeTimer.Enabled)
            {
                _marqueeTimer.Start();
            }
        }

        private void StopMarquee()
        {
            if (_marqueeTimer == null)
            {
                return;
            }

            _marqueeTimer.Stop();
            _marqueeOffset = 0;
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
    }
}
