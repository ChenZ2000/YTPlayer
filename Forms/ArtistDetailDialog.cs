using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using YTPlayer.Models;

namespace YTPlayer.Forms
{
    internal sealed class ArtistDetailDialog : Form
    {
        private readonly ArtistDetail _detail;
        private readonly Dictionary<string, string> _metadata;
        private readonly Font _boldLabelFont;
        private readonly Font _statValueFont;
        private readonly Font _statCaptionFont;

        public ArtistDetailDialog(ArtistDetail detail)
        {
            _detail = detail ?? throw new ArgumentNullException(nameof(detail));
            _metadata = detail.ExtraMetadata != null
                ? new Dictionary<string, string>(detail.ExtraMetadata, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            _boldLabelFont = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold, GraphicsUnit.Point, 134, false);
            _statValueFont = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold, GraphicsUnit.Point, 134, false);
            _statCaptionFont = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point, 134, false);

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            InitializeComponent();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _boldLabelFont?.Dispose();
                _statValueFont?.Dispose();
                _statCaptionFont?.Dispose();
            }

            base.Dispose(disposing);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var createParams = base.CreateParams;
                createParams.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                createParams.ExStyle &= ~0x00040000; // WS_EX_APPWINDOW
                return createParams;
            }
        }

        private void InitializeComponent()
        {
            SuspendLayout();

            AutoScaleMode = AutoScaleMode.Font;
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Regular, GraphicsUnit.Point, 134, false);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ShowIcon = false;
            ControlBox = false;
            KeyPreview = true;
            Padding = new Padding(16);
            Width = 880;
            Height = 640;
            Text = $"歌手详情 - {_detail.Name}";
            AccessibleName = $"歌手详情 {_detail.Name}";
            BackColor = SystemColors.Window;

            KeyDown += ArtistDetailDialog_KeyDown;

            var mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 0,
                AutoSize = false
            };
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

            Controls.Add(mainLayout);

            AddRow(mainLayout, BuildHeaderSection(), SizeType.AutoSize);
            AddRow(mainLayout, BuildStatisticsSection(), SizeType.AutoSize);

            var metadataSection = BuildMetadataSection();
            if (metadataSection != null)
            {
                AddRow(mainLayout, metadataSection, SizeType.AutoSize);
            }

            AddRow(mainLayout, BuildIntroductionSection(), SizeType.Percent, 100F);
            AddRow(mainLayout, BuildButtonSection(), SizeType.AutoSize);

            ResumeLayout(performLayout: true);
        }

        private Control BuildHeaderSection()
        {
            var layout = new TableLayoutPanel
            {
                ColumnCount = 2,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 0, 0, 12)
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160F));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

            var pictureBox = new PictureBox
            {
                Size = new Size(140, 140),
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = SystemColors.ControlLight,
                Margin = new Padding(0, 0, 16, 0),
                SizeMode = PictureBoxSizeMode.Zoom,
                TabStop = false,
                AccessibleName = "歌手封面"
            };

            LoadCoverImage(pictureBox);

            layout.Controls.Add(pictureBox, 0, 0);
            layout.Controls.Add(BuildBasicInfoGroup(), 1, 0);

            return layout;
        }

        private Control BuildBasicInfoGroup()
        {
            var group = new GroupBox
            {
                Text = "基础信息",
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(12, 8, 12, 12),
                Margin = new Padding(0)
            };

            var table = new TableLayoutPanel
            {
                ColumnCount = 2,
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

            AddInfoRow(table, "姓名", FormatValue(_detail.Name));
            AddInfoRow(table, "别名", FormatAlias(_detail.Alias));
            AddInfoRow(table, "地区", FormatValue(_detail.AreaName));
            AddInfoRow(table, "类型", FormatValue(_detail.TypeName));

            var agency = TakeMetadataValue("经纪公司");
            if (!string.IsNullOrWhiteSpace(agency))
            {
                AddInfoRow(table, "经纪公司", agency);
            }

            var birth = TakeMetadataValue("出生日期");
            if (!string.IsNullOrWhiteSpace(birth))
            {
                AddInfoRow(table, "出生日期", birth);
            }

            group.Controls.Add(table);
            return group;
        }

        private Control BuildStatisticsSection()
        {
            var group = new GroupBox
            {
                Text = "数据统计",
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(12),
                Margin = new Padding(0, 0, 0, 12)
            };

            var table = new TableLayoutPanel
            {
                ColumnCount = 4,
                RowCount = 1,
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink
            };

            for (int i = 0; i < 4; i++)
            {
                table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
            }
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            table.Controls.Add(CreateStatPanel("歌曲", FormatCount(_detail.MusicCount)), 0, 0);
            table.Controls.Add(CreateStatPanel("专辑", FormatCount(_detail.AlbumCount)), 1, 0);
            table.Controls.Add(CreateStatPanel("MV", FormatCount(_detail.MvCount)), 2, 0);
            table.Controls.Add(CreateStatPanel("粉丝", FormatCount(_detail.FollowerCount)), 3, 0);

            group.Controls.Add(table);
            return group;
        }

        private Control? BuildMetadataSection()
        {
            var entries = _metadata
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
                .Select(pair => new KeyValuePair<string, string>(pair.Key.Trim(), pair.Value.Trim()))
                .OrderBy(pair => pair.Key, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            if (entries.Count == 0)
            {
                return null;
            }

            var group = new GroupBox
            {
                Text = "扩展信息",
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(12),
                Margin = new Padding(0, 0, 0, 12)
            };

            var table = new TableLayoutPanel
            {
                ColumnCount = 2,
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

            foreach (var entry in entries)
            {
                AddInfoRow(table, entry.Key, entry.Value);
            }

            group.Controls.Add(table);
            return group;
        }

        private Control BuildIntroductionSection()
        {
            var group = new GroupBox
            {
                Text = "详细介绍",
                Dock = DockStyle.Fill,
                Padding = new Padding(12),
                Margin = new Padding(0, 0, 0, 12)
            };

            var contentBox = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = SystemColors.Window,
                Font = Font,
                Text = BuildDetailedNarrative(),
                ScrollBars = RichTextBoxScrollBars.Vertical,
                DetectUrls = true,
                WordWrap = true
            };

            group.Controls.Add(contentBox);
            return group;
        }

        private Control BuildButtonSection()
        {
            var panel = new Panel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(0, 8, 0, 0),
                Margin = new Padding(0)
            };

            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false
            };

            var closeButton = new Button
            {
                Text = "关闭",
                DialogResult = DialogResult.Cancel,
                AutoSize = true,
                Padding = new Padding(12, 6, 12, 6)
            };
            closeButton.Click += (_, __) => Close();

            flow.Controls.Add(closeButton);
            panel.Controls.Add(flow);
            CancelButton = closeButton;

            return panel;
        }

        private Control CreateStatPanel(string caption, string value)
        {
            var panel = new Panel
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
                Padding = new Padding(8),
                AccessibleName = caption
            };

            var captionLabel = new Label
            {
                Text = caption,
                Font = _statCaptionFont,
                ForeColor = SystemColors.GrayText,
                Dock = DockStyle.Top,
                AutoSize = true,
                Margin = new Padding(0, 6, 0, 0)
            };

            var valueLabel = new Label
            {
                Text = value,
                Font = _statValueFont,
                ForeColor = Color.FromArgb(220, 74, 59),
                Dock = DockStyle.Top,
                AutoSize = true,
                Margin = new Padding(0),
                UseMnemonic = false
            };

            panel.Controls.Add(captionLabel);
            panel.Controls.Add(valueLabel);
            return panel;
        }

        private void AddInfoRow(TableLayoutPanel table, string label, string value)
        {
            int rowIndex = table.RowCount;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.RowCount = rowIndex + 1;

            var labelControl = new Label
            {
                Text = $"{label}:",
                Font = _boldLabelFont,
                AutoSize = true,
                Margin = new Padding(0, 4, 8, 4),
                TextAlign = ContentAlignment.MiddleLeft,
                Dock = DockStyle.Fill
            };

            var valueControl = new Label
            {
                Text = value,
                AutoSize = true,
                MaximumSize = new Size(520, 0),
                Margin = new Padding(0, 4, 0, 4),
                Dock = DockStyle.Fill,
                UseMnemonic = false
            };

            table.Controls.Add(labelControl, 0, rowIndex);
            table.Controls.Add(valueControl, 1, rowIndex);
        }

        private static void AddRow(TableLayoutPanel panel, Control control, SizeType sizeType, float height = 0f)
        {
            control.Dock = DockStyle.Fill;
            panel.RowStyles.Add(new RowStyle(sizeType, height));
            panel.Controls.Add(control, 0, panel.RowCount);
            panel.RowCount += 1;
        }

        private string? TakeMetadataValue(string key)
        {
            if (_metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                _metadata.Remove(key);
                return value.Trim();
            }

            return null;
        }

        private static string FormatValue(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? "--" : value.Trim();
        }

        private static string FormatAlias(string? alias)
        {
            if (string.IsNullOrWhiteSpace(alias))
            {
                return "--";
            }

            var parts = alias
                .Split(new[] { ',', ';', '/', '、', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .Where(part => part.Length > 0)
                .ToList();

            return parts.Count == 0 ? "--" : string.Join(" / ", parts);
        }

        private static string FormatCount(long value)
        {
            return value > 0
                ? value.ToString("N0", CultureInfo.CurrentCulture)
                : "--";
        }

        private string BuildDetailedNarrative()
        {
            var builder = new StringBuilder();
            bool hasWrittenBlock = false;

            void AppendBlock(string? title, string? content)
            {
                if (string.IsNullOrWhiteSpace(content))
                {
                    return;
                }

                if (hasWrittenBlock)
                {
                    builder.AppendLine();
                }

                if (!string.IsNullOrWhiteSpace(title))
                {
                    builder.AppendLine(title.Trim());
                }

                builder.AppendLine(content.Trim());
                hasWrittenBlock = true;
            }

            // 主描述优先
            if (!string.IsNullOrWhiteSpace(_detail.Description))
            {
                AppendBlock(null, _detail.Description);
            }

            // 补充简介
            if (!string.IsNullOrWhiteSpace(_detail.BriefDesc) &&
                !string.Equals(_detail.BriefDesc, _detail.Description, StringComparison.Ordinal))
            {
                AppendBlock("简介", _detail.BriefDesc);
            }

            if (_detail.Introductions != null)
            {
                foreach (var section in _detail.Introductions)
                {
                    if (section == null)
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(section.Title) && string.IsNullOrWhiteSpace(section.Content))
                    {
                        continue;
                    }

                    AppendBlock(section.Title, section.Content);
                }
            }

            if (!hasWrittenBlock)
            {
                builder.AppendLine("暂无详细介绍。");
            }

            return builder.ToString().TrimEnd('\r', '\n');
        }

        private void LoadCoverImage(PictureBox pictureBox)
        {
            pictureBox.Image = SystemIcons.Information.ToBitmap();

            string? coverUrl = !string.IsNullOrWhiteSpace(_detail.CoverImageUrl)
                ? _detail.CoverImageUrl
                : _detail.PicUrl;

            if (string.IsNullOrWhiteSpace(coverUrl))
            {
                return;
            }

            try
            {
                pictureBox.LoadAsync(coverUrl);
            }
            catch
            {
                // Ignore loading problems and keep the fallback image.
            }
        }

        private void ArtistDetailDialog_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                Close();
            }
        }
    }
}
