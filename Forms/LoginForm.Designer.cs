using System.Windows.Forms;
namespace YTPlayer.Forms
{
    partial class LoginForm
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.loginTabControl = new System.Windows.Forms.TabControl();
            this.qrTabPage = new System.Windows.Forms.TabPage();
            this.refreshQrButton = new System.Windows.Forms.Button();
            this.qrStatusLabel = new System.Windows.Forms.Label();
            this.qrPictureBox = new System.Windows.Forms.PictureBox();
            this.webTabPage = new System.Windows.Forms.TabPage();
            this.webLoginView = new Microsoft.Web.WebView2.WinForms.WebView2();
            this.webTopPanel = new System.Windows.Forms.Panel();
            this.webReloadButton = new System.Windows.Forms.Button();
            this.webStatusLabel = new System.Windows.Forms.Label();
            this.loginTabControl.SuspendLayout();
            this.qrTabPage.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.qrPictureBox)).BeginInit();
            this.webTabPage.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.webLoginView)).BeginInit();
            this.webTopPanel.SuspendLayout();
            this.SuspendLayout();
            //
            // loginTabControl
            //
            this.loginTabControl.Controls.Add(this.qrTabPage);
            this.loginTabControl.Controls.Add(this.webTabPage);
            this.loginTabControl.Dock = System.Windows.Forms.DockStyle.Fill;
            this.loginTabControl.Location = new System.Drawing.Point(0, 0);
            this.loginTabControl.Name = "loginTabControl";
            this.loginTabControl.SelectedIndex = 0;
            this.loginTabControl.Size = new System.Drawing.Size(484, 561);
            this.loginTabControl.TabIndex = 0;
            //
            // qrTabPage
            //
            this.qrTabPage.Controls.Add(this.refreshQrButton);
            this.qrTabPage.Controls.Add(this.qrStatusLabel);
            this.qrTabPage.Controls.Add(this.qrPictureBox);
            this.qrTabPage.Location = new System.Drawing.Point(4, 28);
            this.qrTabPage.Name = "qrTabPage";
            this.qrTabPage.Padding = new System.Windows.Forms.Padding(3);
            this.qrTabPage.Size = new System.Drawing.Size(476, 529);
            this.qrTabPage.TabIndex = 0;
            this.qrTabPage.Text = "二维码登录";
            this.qrTabPage.UseVisualStyleBackColor = true;
            //
            // refreshQrButton
            //
            this.refreshQrButton.Location = new System.Drawing.Point(167, 470);
            this.refreshQrButton.Name = "refreshQrButton";
            this.refreshQrButton.Size = new System.Drawing.Size(142, 40);
            this.refreshQrButton.TabIndex = 2;
            this.refreshQrButton.Text = "刷新二维码";
            this.refreshQrButton.UseVisualStyleBackColor = true;
            this.refreshQrButton.Click += new System.EventHandler(this.refreshQrButton_Click);
            //
            // qrStatusLabel
            //
            this.qrStatusLabel.Font = new System.Drawing.Font("Microsoft YaHei UI", 10F);
            this.qrStatusLabel.Location = new System.Drawing.Point(20, 420);
            this.qrStatusLabel.Name = "qrStatusLabel";
            this.qrStatusLabel.Size = new System.Drawing.Size(436, 30);
            this.qrStatusLabel.TabIndex = 1;
            this.qrStatusLabel.Text = "正在加载二维码...";
            this.qrStatusLabel.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            //
            // qrPictureBox
            //
            this.qrPictureBox.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle;
            this.qrPictureBox.Location = new System.Drawing.Point(88, 80);
            this.qrPictureBox.Name = "qrPictureBox";
            this.qrPictureBox.Size = new System.Drawing.Size(300, 300);
            this.qrPictureBox.SizeMode = System.Windows.Forms.PictureBoxSizeMode.Zoom;
            this.qrPictureBox.TabIndex = 0;
            this.qrPictureBox.TabStop = false;
            //
            // webTabPage
            //
            this.webTabPage.Controls.Add(this.webLoginView);
            this.webTabPage.Controls.Add(this.webTopPanel);
            this.webTabPage.Location = new System.Drawing.Point(4, 28);
            this.webTabPage.Name = "webTabPage";
            this.webTabPage.Padding = new System.Windows.Forms.Padding(3);
            this.webTabPage.Size = new System.Drawing.Size(476, 529);
            this.webTabPage.TabIndex = 2;
            this.webTabPage.Text = "网页登录";
            this.webTabPage.UseVisualStyleBackColor = true;
            //
            // webLoginView
            //
            this.webLoginView.AllowExternalDrop = true;
            this.webLoginView.CreationProperties = null;
            this.webLoginView.DefaultBackgroundColor = System.Drawing.Color.White;
            this.webLoginView.Dock = System.Windows.Forms.DockStyle.Fill;
            this.webLoginView.Location = new System.Drawing.Point(3, 67);
            this.webLoginView.Name = "webLoginView";
            this.webLoginView.Size = new System.Drawing.Size(470, 459);
            this.webLoginView.TabIndex = 1;
            this.webLoginView.ZoomFactor = 1D;
            this.webLoginView.Visible = false;
            //
            // webTopPanel
            //
            this.webTopPanel.Controls.Add(this.webReloadButton);
            this.webTopPanel.Controls.Add(this.webStatusLabel);
            this.webTopPanel.Dock = System.Windows.Forms.DockStyle.Top;
            this.webTopPanel.Location = new System.Drawing.Point(3, 3);
            this.webTopPanel.Name = "webTopPanel";
            this.webTopPanel.Size = new System.Drawing.Size(470, 64);
            this.webTopPanel.TabIndex = 0;
            //
            // webReloadButton
            //
            this.webReloadButton.Font = new System.Drawing.Font("Microsoft YaHei UI", 10F);
            this.webReloadButton.Location = new System.Drawing.Point(360, 14);
            this.webReloadButton.Name = "webReloadButton";
            this.webReloadButton.Size = new System.Drawing.Size(90, 36);
            this.webReloadButton.TabIndex = 1;
            this.webReloadButton.Text = "加载页面";
            this.webReloadButton.UseVisualStyleBackColor = true;
            this.webReloadButton.Click += new System.EventHandler(this.webReloadButton_Click);
            //
            // webStatusLabel
            //
            this.webStatusLabel.AutoEllipsis = true;
            this.webStatusLabel.Font = new System.Drawing.Font("Microsoft YaHei UI", 10F);
            this.webStatusLabel.Location = new System.Drawing.Point(12, 18);
            this.webStatusLabel.Name = "webStatusLabel";
            this.webStatusLabel.Size = new System.Drawing.Size(230, 24);
            this.webStatusLabel.TabIndex = 0;
            this.webStatusLabel.Text = "点击“加载页面”以打开官方登录页";
            //
            // LoginForm
            //
            this.AutoScaleDimensions = new System.Drawing.SizeF(9F, 18F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(484, 561);
            this.Controls.Add(this.loginTabControl);
            this.Name = "LoginForm";
            this.Text = "登录";
            this.loginTabControl.ResumeLayout(false);
            this.qrTabPage.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.qrPictureBox)).EndInit();
            this.webTabPage.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.webLoginView)).EndInit();
            this.webTopPanel.ResumeLayout(false);
            this.ResumeLayout(false);

        }

        #endregion

        private System.Windows.Forms.TabControl loginTabControl;
        private System.Windows.Forms.TabPage qrTabPage;
        private System.Windows.Forms.PictureBox qrPictureBox;
        private System.Windows.Forms.Label qrStatusLabel;
        private System.Windows.Forms.Button refreshQrButton;
        private System.Windows.Forms.TabPage webTabPage;
        private Microsoft.Web.WebView2.WinForms.WebView2 webLoginView;
        private System.Windows.Forms.Panel webTopPanel;
        private System.Windows.Forms.Label webStatusLabel;
        private System.Windows.Forms.Button webReloadButton;
    }
}
