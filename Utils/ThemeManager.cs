using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace YTPlayer.Utils
{
    internal enum ThemeScheme
    {
        GrassFresh,
        GrassSoft,
        GrassWarm,
        GrassMuted
    }

    internal sealed class ThemePalette
    {
        public ThemeScheme Scheme { get; }
        public string Name { get; }
        public Color BaseBackground { get; }
        public Color SurfaceBackground { get; }
        public Color SurfaceAlt { get; }
        public Color TextPrimary { get; }
        public Color TextSecondary { get; }
        public Color Border { get; }
        public Color Divider { get; }
        public Color Highlight { get; }
        public Color Focus { get; }
        public Color Accent { get; }
        public Color AccentHover { get; }
        public Color AccentPressed { get; }
        public Color AccentSoft { get; }
        public Color AccentText { get; }
        public Color FocusRing { get; }
        public Color ControlBorder => TextSecondary;

        public ThemePalette(
            ThemeScheme scheme,
            string name,
            Color baseBackground,
            Color surfaceBackground,
            Color surfaceAlt,
            Color textPrimary,
            Color textSecondary,
            Color border,
            Color divider,
            Color highlight,
            Color focus,
            Color accent,
            Color accentHover,
            Color accentPressed,
            Color accentSoft,
            Color accentText,
            Color focusRing)
        {
            Scheme = scheme;
            Name = name;
            BaseBackground = baseBackground;
            SurfaceBackground = surfaceBackground;
            SurfaceAlt = surfaceAlt;
            TextPrimary = textPrimary;
            TextSecondary = textSecondary;
            Border = border;
            Divider = divider;
            Highlight = highlight;
            Focus = focus;
            Accent = accent;
            AccentHover = accentHover;
            AccentPressed = accentPressed;
            AccentSoft = accentSoft;
            AccentText = accentText;
            FocusRing = focusRing;
        }
    }

    internal static class ThemeManager
    {
        private const int DefaultCornerRadius = 10;
        private static readonly List<ThemePalette> Palettes = new List<ThemePalette>
        {
            new ThemePalette(
                ThemeScheme.GrassFresh,
                "草绿清新",
                Color.FromArgb(247, 248, 246),
                Color.FromArgb(255, 255, 255),
                Color.FromArgb(238, 243, 239),
                Color.FromArgb(31, 35, 31),
                Color.FromArgb(76, 82, 78),
                Color.FromArgb(214, 222, 212),
                Color.FromArgb(230, 234, 228),
                Color.FromArgb(238, 242, 239),
                Color.FromArgb(196, 230, 210),
                Color.FromArgb(27, 124, 65),
                Color.FromArgb(23, 110, 57),
                Color.FromArgb(18, 92, 47),
                Color.FromArgb(223, 243, 231),
                Color.FromArgb(255, 255, 255),
                Color.FromArgb(27, 112, 63)),
            new ThemePalette(
                ThemeScheme.GrassSoft,
                "草绿柔和",
                Color.FromArgb(247, 247, 244),
                Color.FromArgb(255, 255, 255),
                Color.FromArgb(238, 241, 238),
                Color.FromArgb(31, 35, 31),
                Color.FromArgb(78, 83, 79),
                Color.FromArgb(214, 221, 215),
                Color.FromArgb(231, 234, 230),
                Color.FromArgb(237, 239, 236),
                Color.FromArgb(202, 224, 210),
                Color.FromArgb(65, 119, 80),
                Color.FromArgb(55, 105, 69),
                Color.FromArgb(46, 90, 59),
                Color.FromArgb(227, 239, 230),
                Color.FromArgb(255, 255, 255),
                Color.FromArgb(57, 110, 73)),
            new ThemePalette(
                ThemeScheme.GrassWarm,
                "草绿暖意",
                Color.FromArgb(248, 247, 243),
                Color.FromArgb(255, 255, 255),
                Color.FromArgb(240, 241, 233),
                Color.FromArgb(31, 35, 31),
                Color.FromArgb(80, 84, 79),
                Color.FromArgb(221, 217, 206),
                Color.FromArgb(234, 231, 222),
                Color.FromArgb(240, 241, 233),
                Color.FromArgb(216, 228, 197),
                Color.FromArgb(87, 116, 49),
                Color.FromArgb(76, 102, 42),
                Color.FromArgb(63, 85, 34),
                Color.FromArgb(237, 243, 224),
                Color.FromArgb(255, 255, 255),
                Color.FromArgb(79, 106, 44)),
            new ThemePalette(
                ThemeScheme.GrassMuted,
                "草绿静雅",
                Color.FromArgb(246, 247, 245),
                Color.FromArgb(255, 255, 255),
                Color.FromArgb(238, 241, 239),
                Color.FromArgb(30, 34, 31),
                Color.FromArgb(75, 81, 78),
                Color.FromArgb(213, 219, 214),
                Color.FromArgb(230, 233, 230),
                Color.FromArgb(238, 241, 239),
                Color.FromArgb(198, 221, 210),
                Color.FromArgb(62, 123, 90),
                Color.FromArgb(52, 105, 77),
                Color.FromArgb(46, 92, 68),
                Color.FromArgb(224, 236, 229),
                Color.FromArgb(255, 255, 255),
                Color.FromArgb(52, 105, 77))
        };

        private static readonly HashSet<Form> ThemedForms = new HashSet<Form>();
        private static readonly Dictionary<Control, int> RoundedControls = new Dictionary<Control, int>();
        private static bool _initialized;
        private static ThemePalette _current = Palettes[0];

        private static readonly ThemePalette HighContrastPalette = new ThemePalette(
            ThemeScheme.GrassFresh, "系统高对比度", SystemColors.Window, SystemColors.Window,
            SystemColors.Control, SystemColors.WindowText, SystemColors.WindowText,
            SystemColors.WindowText, SystemColors.GrayText, SystemColors.Control,
            SystemColors.Control, SystemColors.Highlight, SystemColors.Highlight,
            SystemColors.Highlight, SystemColors.Control, SystemColors.HighlightText,
            SystemColors.WindowText);
        internal static bool? HighContrastOverride { get; set; }
        private static bool _lastHighContrast;
        internal static bool IsHighContrast => HighContrastOverride ?? SystemInformation.HighContrast;
        public static ThemePalette Current => IsHighContrast ? HighContrastPalette : _current;
        internal static ThemeScheme SelectedScheme => _current.Scheme;
        public static IReadOnlyList<ThemePalette> AvailablePalettes => Palettes;

        public static void Initialize(ThemeScheme scheme = ThemeScheme.GrassFresh)
        {
            if (_initialized)
            {
                return;
            }

            SetScheme(scheme);
            Application.Idle += OnApplicationIdle;
            _initialized = true;
        }

        public static void SetScheme(ThemeScheme scheme)
        {
            ThemePalette? palette = Palettes.FirstOrDefault(p => p.Scheme == scheme);
            if (palette == null)
            {
                palette = Palettes[0];
            }

            _current = palette;
            ThemedForms.Clear();
            ApplyThemeToOpenForms();
        }

        public static void ApplyTheme(Control root)
        {
            if (root == null)
            {
                return;
            }

            ApplyThemeToControl(root);

            foreach (Control child in root.Controls)
            {
                ApplyTheme(child);
            }

            if (root is ToolStrip toolStrip)
            {
                ApplyToolStripTheme(toolStrip);
            }
        }

        private static void OnApplicationIdle(object? sender, EventArgs e)
        {
            if (_lastHighContrast != IsHighContrast)
            {
                _lastHighContrast = IsHighContrast;
                ThemedForms.Clear();
            }
            ApplyThemeToOpenForms();
        }

        private static void ApplyThemeToOpenForms()
        {
            foreach (Form form in Application.OpenForms)
            {
                if (!ThemedForms.Add(form))
                {
                    continue;
                }

                ApplyTheme(form);
                form.FormClosed -= ThemedFormClosed;
                form.FormClosed += ThemedFormClosed;
            }
        }

        private static void ThemedFormClosed(object? sender, FormClosedEventArgs e)
        { if (sender is Form form) ThemedForms.Remove(form); }

        private static void ApplyThemeToControl(Control control)
        {
            ThemePalette palette = Current;

            if (control is Form form)
            {
                form.BackColor = palette.BaseBackground;
                form.ForeColor = palette.TextPrimary;
            }
            else if (control is Panel || control is GroupBox || control is TabPage || control is TabControl || control is SplitContainer ||
                     control is FlowLayoutPanel || control is TableLayoutPanel || control is UserControl)
            {
                control.BackColor = palette.SurfaceBackground;
                control.ForeColor = palette.TextPrimary;
            }
            else if (control is StatusStrip || control is MenuStrip || control is ContextMenuStrip || control is ToolStrip)
            {
                control.BackColor = palette.SurfaceAlt;
                control.ForeColor = palette.TextPrimary;
            }
            else if (control is Label label && !(control is LinkLabel))
            {
                label.BackColor = Color.Transparent;
                label.ForeColor = palette.TextPrimary;
            }
            else if (control is LinkLabel linkLabel)
            {
                linkLabel.BackColor = Color.Transparent;
                linkLabel.LinkColor = palette.Accent;
                linkLabel.ActiveLinkColor = palette.AccentHover;
                linkLabel.VisitedLinkColor = palette.TextSecondary;
                linkLabel.ForeColor = palette.TextPrimary;
            }
            else if (control is Button button)
            {
                bool isPrimary = IsPrimaryButton(button);
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderSize = Math.Max(1, (int)Math.Round(button.DeviceDpi / 96f));
                button.FlatAppearance.BorderColor = isPrimary ? palette.Accent : palette.ControlBorder;
                button.FlatAppearance.MouseOverBackColor = isPrimary ? palette.AccentHover : palette.Highlight;
                button.FlatAppearance.MouseDownBackColor = isPrimary ? palette.AccentPressed : palette.Focus;
                button.BackColor = isPrimary ? palette.Accent : palette.SurfaceBackground;
                button.ForeColor = isPrimary ? palette.AccentText : palette.TextPrimary;
                button.UseVisualStyleBackColor = false;
                TryApplyRounded(button, DefaultCornerRadius);
                ButtonFeedback.Attach(button);
            }
            else if (control is TextBoxBase textBox)
            {
                textBox.BackColor = palette.SurfaceBackground;
                textBox.ForeColor = palette.TextPrimary;
                textBox.BorderStyle = BorderStyle.FixedSingle;
                TryApplyRounded(textBox, DefaultCornerRadius);
            }
            else if (control is ComboBox comboBox)
            {
                comboBox.BackColor = palette.SurfaceBackground;
                comboBox.ForeColor = palette.TextPrimary;
                comboBox.FlatStyle = FlatStyle.Flat;
                TryApplyRounded(comboBox, DefaultCornerRadius);
            }
            else if (control is ListView listView)
            {
                listView.BackColor = palette.SurfaceBackground;
                listView.ForeColor = palette.TextPrimary;
                bool isSafeListView = string.Equals(listView.GetType().Name, "SafeListView", StringComparison.Ordinal);
                listView.BorderStyle = isSafeListView ? BorderStyle.None : BorderStyle.FixedSingle;
                TryApplyRounded(listView, DefaultCornerRadius);
            }
            else if (control is TreeView treeView)
            {
                treeView.BackColor = palette.SurfaceBackground;
                treeView.ForeColor = palette.TextPrimary;
                TryApplyRounded(treeView, DefaultCornerRadius);
            }
            else if (control is ListBox listBox)
            {
                listBox.BackColor = palette.SurfaceBackground;
                listBox.ForeColor = palette.TextPrimary;
                listBox.BorderStyle = BorderStyle.FixedSingle;
                TryApplyRounded(listBox, DefaultCornerRadius);
            }
            else if (control is CheckedListBox checkedListBox)
            {
                checkedListBox.BackColor = palette.SurfaceBackground;
                checkedListBox.ForeColor = palette.TextPrimary;
                checkedListBox.BorderStyle = BorderStyle.FixedSingle;
                TryApplyRounded(checkedListBox, DefaultCornerRadius);
            }
            else if (control is NumericUpDown numericUpDown)
            {
                numericUpDown.BackColor = palette.SurfaceBackground;
                numericUpDown.ForeColor = palette.TextPrimary;
                numericUpDown.BorderStyle = BorderStyle.FixedSingle;
                TryApplyRounded(numericUpDown, DefaultCornerRadius);
            }
            else if (control is TrackBar trackBar)
            {
                trackBar.BackColor = palette.SurfaceBackground;
                trackBar.ForeColor = palette.TextPrimary;
                TrySetTickColor(trackBar, palette.Divider);
                trackBar.Invalidate();
            }
            else if (control is ThemedProgressBar themedProgressBar)
            {
                themedProgressBar.BackColor = palette.SurfaceAlt;
                themedProgressBar.ForeColor = palette.Accent;
                themedProgressBar.Invalidate();
            }
            else if (control is DataGridView gridView)
            {
                gridView.BackgroundColor = palette.SurfaceBackground;
                gridView.GridColor = palette.Divider;
                gridView.ForeColor = palette.TextPrimary;
                gridView.EnableHeadersVisualStyles = false;
                gridView.ColumnHeadersDefaultCellStyle.BackColor = palette.SurfaceAlt;
                gridView.ColumnHeadersDefaultCellStyle.ForeColor = palette.TextPrimary;
                gridView.DefaultCellStyle.BackColor = palette.SurfaceBackground;
                gridView.DefaultCellStyle.ForeColor = palette.TextPrimary;
                gridView.DefaultCellStyle.SelectionBackColor = palette.AccentSoft;
                gridView.DefaultCellStyle.SelectionForeColor = palette.TextPrimary;
                TryApplyRounded(gridView, DefaultCornerRadius);
            }
            else if (control is PictureBox pictureBox)
            {
                pictureBox.BackColor = Color.Transparent;
            }
            else if (control is RichTextBox richTextBox)
            {
                richTextBox.BackColor = palette.SurfaceBackground;
                richTextBox.ForeColor = palette.TextPrimary;
                richTextBox.BorderStyle = BorderStyle.FixedSingle;
                TryApplyRounded(richTextBox, DefaultCornerRadius);
            }

            ApplyWebViewBackground(control, palette);

            if (control.ContextMenuStrip != null)
            {
                ApplyToolStripTheme(control.ContextMenuStrip);
            }
        }

        private static void ApplyToolStripTheme(ToolStrip toolStrip)
        {
            ThemePalette palette = Current;
            toolStrip.BackColor = palette.SurfaceAlt;
            toolStrip.ForeColor = palette.TextPrimary;
            toolStrip.Renderer = new ThemedToolStripRenderer(palette);

            foreach (ToolStripItem item in toolStrip.Items)
            {
                item.ForeColor = palette.TextPrimary;
            }
        }

        private static void ApplyWebViewBackground(Control control, ThemePalette palette)
        {
            if (control == null)
            {
                return;
            }

            Type type = control.GetType();
            if (!string.Equals(type.Name, "WebView2", StringComparison.Ordinal))
            {
                return;
            }

            var property = type.GetProperty("DefaultBackgroundColor");
            if (property != null && property.CanWrite)
            {
                try
                {
                    property.SetValue(control, palette.SurfaceBackground, null);
                }
                catch
                {
                }
            }
        }

        private static void TrySetTickColor(TrackBar trackBar, Color color)
        {
            try
            {
                var property = trackBar.GetType().GetProperty("TickColor");
                if (property != null && property.CanWrite)
                {
                    property.SetValue(trackBar, color, null);
                }
            }
            catch
            {
            }
        }

        private static void TryApplyRounded(Control control, int radius)
        {
            if (control == null || control.Region != null)
            {
                return;
            }

            RegisterRoundedControl(control, radius);
        }

        private static void RegisterRoundedControl(Control control, int radius)
        {
            RoundedControls[control] = radius;
            control.SizeChanged -= RoundedControl_SizeChanged;
            control.SizeChanged += RoundedControl_SizeChanged;
            control.DpiChangedAfterParent -= RoundedControl_SizeChanged;
            control.DpiChangedAfterParent += RoundedControl_SizeChanged;
            control.Disposed -= RoundedControl_Disposed;
            control.Disposed += RoundedControl_Disposed;
            ApplyRoundedRegion(control, radius);
        }

        private static void RoundedControl_SizeChanged(object? sender, EventArgs e)
        {
            if (sender is Control control && RoundedControls.TryGetValue(control, out int radius))
            {
                ApplyRoundedRegion(control, radius);
            }
        }

        private static void RoundedControl_Disposed(object? sender, EventArgs e)
        {
            if (sender is Control control)
            {
                RoundedControls.Remove(control);
            }
        }

        private static void ApplyRoundedRegion(Control control, int radius)
        {
            if (control == null || control.Width <= 0 || control.Height <= 0)
            {
                return;
            }

            int safeRadius = Math.Max(0, Math.Min((int)Math.Round(radius * control.DeviceDpi / 96f), Math.Min(control.Width, control.Height) / 2));
            Rectangle bounds = new Rectangle(0, 0, control.Width, control.Height);
            using (GraphicsPath path = BuildRoundedRectanglePath(bounds, safeRadius))
            {
                Region? previous = control.Region;
                control.Region = new Region(path);
                previous?.Dispose();
            }
        }

        // Decorate existing Button instances so Enter/Esc, mnemonics, DialogResult,
        // default-button behavior and native accessibility remain owned by WinForms.
        private sealed class ButtonFeedback
        {
            private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Button, ButtonFeedback> States =
                new System.Runtime.CompilerServices.ConditionalWeakTable<Button, ButtonFeedback>();
            private bool _pressed;
            internal static void Attach(Button button) => States.GetValue(button, b => new ButtonFeedback(b));
            private ButtonFeedback(Button button)
            {
                void SizeForText()
                {
                    if (!button.IsHandleCreated) return;
                    int minimum = Math.Max((int)Math.Ceiling(32 * button.DeviceDpi / 96f), button.Font.Height + (int)Math.Ceiling(8 * button.DeviceDpi / 96f));
                    button.MinimumSize = new Size(button.MinimumSize.Width, minimum);
                    button.FlatAppearance.BorderSize = Math.Max(1, (int)Math.Round(button.DeviceDpi / 96f));
                }
                button.HandleCreated += (_, _) => SizeForText();
                button.DpiChangedAfterParent += (_, _) => SizeForText();
                button.FontChanged += (_, _) => SizeForText();
                SizeForText();
                button.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { _pressed = true; button.Invalidate(); } };
                button.MouseUp += (_, _) => { _pressed = false; button.Invalidate(); };
                button.MouseMove += (_, e) =>
                {
                    if (!button.Capture || e.Button != MouseButtons.Left) return;
                    bool pressed = button.ClientRectangle.Contains(e.Location);
                    if (_pressed != pressed) { _pressed = pressed; button.Invalidate(); }
                };
                button.MouseCaptureChanged += (_, _) => { if (!button.Capture) { _pressed = false; button.Invalidate(); } };
                button.KeyDown += (_, e) => { if (e.KeyCode == Keys.Space) { _pressed = true; button.Invalidate(); } };
                button.KeyUp += (_, e) => { if (e.KeyCode == Keys.Space) { _pressed = false; button.Invalidate(); } };
                button.GotFocus += (_, _) => button.Invalidate();
                button.LostFocus += (_, _) => { _pressed = false; button.Invalidate(); };
                button.EnabledChanged += (_, _) => { _pressed = false; button.Invalidate(); };
                button.Paint += (_, e) =>
                {
                    if (!button.Enabled || (!button.Focused && !_pressed)) return;
                    float scale = button.DeviceDpi / 96f;
                    int inset = Math.Max(3, (int)Math.Ceiling(4 * scale));
                    Rectangle bounds = Rectangle.Inflate(button.ClientRectangle, -inset, -inset);
                    if (bounds.Width < 1 || bounds.Height < 1) return;
                    Color color = IsPrimaryButton(button) ? Current.AccentText : Current.FocusRing;
                    using (var pen = new Pen(color, Math.Max(1, (_pressed ? 2 : 1.5f) * scale)))
                    using (var path = BuildRoundedRectanglePath(bounds, (int)(4 * scale)))
                    {
                        // Pressed has a solid inset border; keyboard focus has a dashed outline.
                        pen.DashStyle = _pressed ? DashStyle.Solid : DashStyle.Dash;
                        e.Graphics.DrawPath(pen, path);
                    }
                };
            }
        }

        private static GraphicsPath BuildRoundedRectanglePath(Rectangle bounds, int radius)
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

        private sealed class ThemedToolStripRenderer : ToolStripProfessionalRenderer
        {
            private readonly ThemePalette _palette;

            public ThemedToolStripRenderer(ThemePalette palette)
            {
                _palette = palette;
            }

            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                e.Graphics.Clear(_palette.SurfaceAlt);
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                using (var pen = new Pen(_palette.Divider))
                {
                    Rectangle rect = new Rectangle(Point.Empty, e.ToolStrip.Size);
                    rect.Width -= 1;
                    rect.Height -= 1;
                    e.Graphics.DrawRectangle(pen, rect);
                }
            }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                Color backColor = e.Item.Selected ? _palette.AccentSoft : _palette.SurfaceAlt;
                using (var brush = new SolidBrush(backColor))
                {
                    e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.Item.Size));
                }
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = _palette.TextPrimary;
                base.OnRenderItemText(e);
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                using (var pen = new Pen(_palette.Divider))
                {
                    int y = e.Item.Bounds.Height / 2;
                    e.Graphics.DrawLine(pen, 2, y, e.Item.Bounds.Width - 2, y);
                }
            }
        }

        private static bool IsPrimaryButton(Button button)
        {
            if (button == null)
            {
                return false;
            }

            if (button.Tag is string tag)
            {
                return string.Equals(tag, "Primary", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }
    }
}
