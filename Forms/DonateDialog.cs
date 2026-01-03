using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace YTPlayer.Forms
{
    internal sealed class DonateDialog : Form
    {
        private const string DonatePrompt = "微信扫一扫，给 ChenZ 买点零食！";
        private readonly PictureBox _qrPictureBox;
        private readonly Label _subtitleLabel;
        private readonly string _qrCodePath;

        public DonateDialog()
        {
            _qrCodePath = Utils.PathHelper.ResolveFromLibsOrRoot(Path.Combine("assets", "WeChatQRCode.jpg"));
            _qrPictureBox = new PictureBox();
            _subtitleLabel = new Label();

            InitializeComponent();
            TryLoadQrImage();
        }

        private void InitializeComponent()
        {
            Text = DonatePrompt;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowIcon = false;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Font;
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Regular, GraphicsUnit.Point, 134);
            ClientSize = new Size(420, 520);
            KeyPreview = true;
            KeyDown += HandleEscKey;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(20)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _subtitleLabel.AutoSize = true;
            _subtitleLabel.Dock = DockStyle.Fill;
            _subtitleLabel.TextAlign = ContentAlignment.MiddleCenter;
            _subtitleLabel.Margin = new Padding(0, 0, 0, 12);
            _subtitleLabel.Text = "正在加载二维码...";

            _qrPictureBox.Dock = DockStyle.Fill;
            _qrPictureBox.SizeMode = PictureBoxSizeMode.Zoom;
            _qrPictureBox.BorderStyle = BorderStyle.FixedSingle;
            _qrPictureBox.Margin = new Padding(0, 0, 0, 12);

            var closeButton = new Button
            {
                Text = "关闭",
                AutoSize = true,
                DialogResult = DialogResult.OK,
                Anchor = AnchorStyles.Right
            };

            var buttonPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink
            };
            buttonPanel.Controls.Add(closeButton);

            layout.Controls.Add(_subtitleLabel, 0, 0);
            layout.Controls.Add(_qrPictureBox, 0, 1);
            layout.Controls.Add(buttonPanel, 0, 2);

            Controls.Add(layout);
            AcceptButton = closeButton;
        }

        private void TryLoadQrImage()
        {
            try
            {
                _qrPictureBox.Image = LoadQrImage();
                _subtitleLabel.Text = DonatePrompt;
            }
            catch (Exception ex)
            {
                _qrPictureBox.Visible = false;
                _subtitleLabel.Text = $"未能加载二维码：{ex.Message}";
            }
        }

        private Image LoadQrImage()
        {
            if (!File.Exists(_qrCodePath))
            {
                throw new FileNotFoundException("未找到二维码资源。", _qrCodePath);
            }

            using var fileStream = File.OpenRead(_qrCodePath);
            using var originalImage = Image.FromStream(fileStream);
            return (Image)originalImage.Clone();
        }

        private void HandleEscKey(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                Close();
            }
        }
    }
}
