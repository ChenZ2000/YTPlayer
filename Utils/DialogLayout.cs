using System;
using System.Drawing;
using System.Windows.Forms;

namespace YTPlayer.Utils
{
    /// <summary>Content-sized rows with one scrollable/flexible content area.</summary>
    internal static class DialogLayout
    {
        internal static TableLayoutPanel CreateVertical(Control owner, int fillRow, params Control[] rows)
        {
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                ColumnCount = 1,
                RowCount = rows.Length,
                Padding = new Padding(20, 15, 20, 15),
                TabIndex = 0,
                TabStop = false
            };
            // An implicit AutoSize column can grow beyond the window for long status text.
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            for (int i = 0; i < rows.Length; i++)
            {
                Control control = rows[i];
                layout.RowStyles.Add(new RowStyle(i == fillRow ? SizeType.Percent : SizeType.AutoSize, i == fillRow ? 100F : 0F));
                control.Dock = DockStyle.Fill;
                control.Margin = new Padding(0, 0, 0, i == rows.Length - 1 ? 0 : 8);
                if (control is Label label)
                {
                    label.AutoSize = true;
                    label.AutoEllipsis = false;
                }
                layout.Controls.Add(control, 0, i);
            }
            EnableVerticalScrolling(layout, fillRow);
            owner.Controls.Add(layout);
            return layout;
        }

        internal static void EnableVerticalScrolling(TableLayoutPanel layout, int fillRow)
        {
            layout.AutoScroll = true;
            bool measuring = false;
            layout.Layout += (_, __) =>
            {
                if (measuring || !layout.Visible) return;
                measuring = true;
                try
                {
                    int MeasureHeight(int width)
                    {
                        int height = layout.Padding.Vertical;
                        foreach (Control control in layout.Controls)
                        {
                            if (!control.Visible) continue;
                            int available = Math.Max(1, width - layout.Padding.Horizontal - control.Margin.Horizontal);
                            // ComboBox ignores MinimumSize.Height; reserve its native preferred height explicitly.
                            if (control is ComboBox)
                            {
                                int row = layout.GetPositionFromControl(control).Row;
                                if (row >= 0 && row < layout.RowStyles.Count)
                                {
                                    layout.RowStyles[row].SizeType = SizeType.Absolute;
                                    layout.RowStyles[row].Height = control.GetPreferredSize(new Size(available, 0)).Height + control.Margin.Vertical;
                                }
                            }
                            int content = layout.GetRow(control) == fillRow ? control.MinimumSize.Height :
                                control.GetPreferredSize(new Size(available, 0)).Height;
                            height += Math.Max(content, control.MinimumSize.Height) + control.Margin.Vertical;
                        }
                        return height;
                    }

                    int scrollbarWidth = SystemInformation.VerticalScrollBarWidth;
                    int fullWidth = layout.ClientSize.Width + (layout.VerticalScroll.Visible ? scrollbarWidth : 0);
                    int minimumHeight = MeasureHeight(fullWidth);
                    if (minimumHeight > layout.ClientSize.Height)
                        minimumHeight = MeasureHeight(fullWidth - scrollbarWidth);
                    // A percent row can otherwise become negative and pull the footer over long text.
                    layout.AutoScrollMinSize = new Size(0, minimumHeight);
                }
                finally { measuring = false; }
            };
        }

        internal static FlowLayoutPanel CreateButtonRow(params Button[] buttons)
        {
            var panel = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = true,
                TabStop = false
            };
            foreach (Button button in buttons)
            {
                button.AutoSize = true;
                button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                button.MinimumSize = new Size(90, 32);
                button.Padding = new Padding(8, 3, 8, 3);
                panel.Controls.Add(button);
            }
            return panel;
        }
    }
}
