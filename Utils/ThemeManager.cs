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
        public Color TextPrimary { get; }
        public Color TextSecondary { get; }
        public Color Border { get; }
        public Color Highlight { get; }
        public Color Focus { get; }

        public ThemePalette(
            ThemeScheme scheme,
            string name,
            Color baseBackground,
            Color surfaceBackground,
            Color textPrimary,
            Color textSecondary,
            Color border,
            Color highlight,
            Color focus)
        {
            Scheme = scheme;
            Name = name;
            BaseBackground = baseBackground;
            SurfaceBackground = surfaceBackground;
            TextPrimary = textPrimary;
            TextSecondary = textSecondary;
            Border = border;
            Highlight = highlight;
            Focus = focus;
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
                Color.FromArgb(230, 246, 224),
                Color.FromArgb(247, 241, 230),
                Color.FromArgb(42, 42, 42),
                Color.FromArgb(64, 64, 64),
                Color.FromArgb(42, 42, 42),
                Color.FromArgb(236, 228, 212),
                Color.FromArgb(216, 236, 208)),
            new ThemePalette(
                ThemeScheme.GrassSoft,
                "草绿柔和",
                Color.FromArgb(226, 242, 216),
                Color.FromArgb(245, 239, 228),
                Color.FromArgb(43, 43, 43),
                Color.FromArgb(66, 66, 66),
                Color.FromArgb(43, 43, 43),
                Color.FromArgb(234, 225, 209),
                Color.FromArgb(214, 234, 204)),
            new ThemePalette(
                ThemeScheme.GrassWarm,
                "草绿暖意",
                Color.FromArgb(231, 247, 217),
                Color.FromArgb(248, 241, 226),
                Color.FromArgb(44, 44, 44),
                Color.FromArgb(68, 68, 68),
                Color.FromArgb(44, 44, 44),
                Color.FromArgb(237, 228, 208),
                Color.FromArgb(216, 235, 200)),
            new ThemePalette(
                ThemeScheme.GrassMuted,
                "草绿静雅",
                Color.FromArgb(224, 240, 214),
                Color.FromArgb(244, 238, 226),
                Color.FromArgb(41, 41, 41),
                Color.FromArgb(63, 63, 63),
                Color.FromArgb(41, 41, 41),
                Color.FromArgb(232, 224, 208),
                Color.FromArgb(210, 232, 204))
        };

        private static readonly HashSet<Form> ThemedForms = new HashSet<Form>();
        private static readonly Dictionary<Control, int> RoundedControls = new Dictionary<Control, int>();
        private static bool _initialized;
        private static ThemePalette _current = Palettes[0];

        public static ThemePalette Current => _current;
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
                form.FormClosed += (_, _) => ThemedForms.Remove(form);
            }
        }

        private static void ApplyThemeToControl(Control control)
        {
            ThemePalette palette = _current;

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
                control.BackColor = palette.SurfaceBackground;
                control.ForeColor = palette.TextPrimary;
            }
            else if (control is Label label)
            {
                label.BackColor = Color.Transparent;
                label.ForeColor = palette.TextPrimary;
            }
            else if (control is LinkLabel linkLabel)
            {
                linkLabel.BackColor = Color.Transparent;
                linkLabel.LinkColor = palette.TextPrimary;
                linkLabel.ActiveLinkColor = palette.TextPrimary;
                linkLabel.VisitedLinkColor = palette.TextSecondary;
                linkLabel.ForeColor = palette.TextPrimary;
            }
            else if (control is Button button)
            {
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderSize = 1;
                button.FlatAppearance.BorderColor = palette.Border;
                button.FlatAppearance.MouseOverBackColor = palette.Highlight;
                button.FlatAppearance.MouseDownBackColor = palette.Focus;
                button.BackColor = palette.SurfaceBackground;
                button.ForeColor = palette.TextPrimary;
                button.UseVisualStyleBackColor = false;
                TryApplyRounded(button, DefaultCornerRadius);
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
                listView.BorderStyle = BorderStyle.FixedSingle;
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
            }
            else if (control is DataGridView gridView)
            {
                gridView.BackgroundColor = palette.SurfaceBackground;
                gridView.GridColor = palette.Border;
                gridView.ForeColor = palette.TextPrimary;
                gridView.EnableHeadersVisualStyles = false;
                gridView.ColumnHeadersDefaultCellStyle.BackColor = palette.SurfaceBackground;
                gridView.ColumnHeadersDefaultCellStyle.ForeColor = palette.TextPrimary;
                gridView.DefaultCellStyle.BackColor = palette.SurfaceBackground;
                gridView.DefaultCellStyle.ForeColor = palette.TextPrimary;
                gridView.DefaultCellStyle.SelectionBackColor = palette.Highlight;
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
            ThemePalette palette = _current;
            toolStrip.BackColor = palette.SurfaceBackground;
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

            int safeRadius = Math.Max(0, Math.Min(radius, Math.Min(control.Width, control.Height) / 2));
            Rectangle bounds = new Rectangle(0, 0, control.Width, control.Height);
            using (GraphicsPath path = BuildRoundedRectanglePath(bounds, safeRadius))
            {
                control.Region = new Region(path);
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
                e.Graphics.Clear(_palette.SurfaceBackground);
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                using (var pen = new Pen(_palette.Border))
                {
                    Rectangle rect = new Rectangle(Point.Empty, e.ToolStrip.Size);
                    rect.Width -= 1;
                    rect.Height -= 1;
                    e.Graphics.DrawRectangle(pen, rect);
                }
            }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                Color backColor = e.Item.Selected ? _palette.Highlight : _palette.SurfaceBackground;
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
                using (var pen = new Pen(_palette.Border))
                {
                    int y = e.Item.Bounds.Height / 2;
                    e.Graphics.DrawLine(pen, 2, y, e.Item.Bounds.Width - 2, y);
                }
            }
        }
    }
}
