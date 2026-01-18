using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using MessageBox = YTPlayer.MessageBox;

namespace YTPlayer.Forms
{
    internal sealed class PurchaseLinkDialog : Form
    {
        private readonly string _purchaseUrl;
        private readonly Label _messageLabel;
        private readonly Button _openButton;
        private readonly Button _cancelButton;

        public bool PurchaseRequested { get; private set; }

        public PurchaseLinkDialog(string songName, string albumName, string purchaseUrl)
        {
            _purchaseUrl = purchaseUrl ?? throw new ArgumentNullException(nameof(purchaseUrl));
            PurchaseRequested = false;

            string albumDisplay = string.IsNullOrWhiteSpace(albumName) ? "该专辑" : $"《{albumName}》";
            string songDisplay = string.IsNullOrWhiteSpace(songName) ? "该歌曲" : $"《{songName}》";
            string description = $"{albumDisplay} 属于网易云数字专辑，{songDisplay} 需要购买后才能播放。\n\n" +
                                 "点击“购买”将打开网易云音乐官网的支付页面，请在浏览器中登录当前账号并完成购买后返回本应用重新播放。";

            Text = description;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(420, 200);

            _messageLabel = new Label
            {
                AutoSize = false,
                Location = new Point(15, 15),
                Size = new Size(390, 100),
                Text = description,
                TabStop = true
            };

            _openButton = new Button
            {
                Text = "购买",
                DialogResult = DialogResult.OK,
                Location = new Point(220, 130),
                Size = new Size(90, 30)
            };
            _openButton.Click += (_, __) => OpenPurchaseLink(true);

            _cancelButton = new Button
            {
                Text = "取消",
                DialogResult = DialogResult.Cancel,
                Location = new Point(320, 130),
                Size = new Size(90, 30)
            };
            _cancelButton.Click += (_, __) =>
            {
                PurchaseRequested = false;
                Close();
            };

            Controls.AddRange(new Control[] { _messageLabel, _openButton, _cancelButton });
            AcceptButton = _openButton;
            CancelButton = _cancelButton;
            Shown += (_, __) =>
            {
                try
                {
                    ActiveControl = _messageLabel;
                    _messageLabel.Focus();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[PurchaseLinkDialog] Focus failed: {ex.Message}");
                }
            };
        }

        private void OpenPurchaseLink(bool closeAfterLaunch)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _purchaseUrl,
                    UseShellExecute = true
                });

                PurchaseRequested = true;
                if (closeAfterLaunch)
                {
                    DialogResult = DialogResult.OK;
                    Close();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    $"无法打开购买链接：{ex.Message}",
                    "错误",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }
    }
}
