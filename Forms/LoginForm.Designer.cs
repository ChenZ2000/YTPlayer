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
            this.captchaTabPage = new System.Windows.Forms.TabPage();
            this.smsStatusLabel = new System.Windows.Forms.Label();
            this.captchaLoginButton = new System.Windows.Forms.Button();
            this.captchaTextBox = new System.Windows.Forms.TextBox();
            this.smsCaptchaLabel = new System.Windows.Forms.Label();
            this.sendCaptchaButton = new System.Windows.Forms.Button();
            this.phoneTextBox = new System.Windows.Forms.TextBox();
            this.smsPhoneLabel = new System.Windows.Forms.Label();
            this.countryCodeComboBox = new System.Windows.Forms.ComboBox();
            this.smsCountryLabel = new System.Windows.Forms.Label();
            this.webTabPage = new System.Windows.Forms.TabPage();
            this.webLoginView = new Microsoft.Web.WebView2.WinForms.WebView2();
            this.webTopPanel = new System.Windows.Forms.Panel();
            this.webReloadButton = new System.Windows.Forms.Button();
            this.webStatusLabel = new System.Windows.Forms.Label();
            this.loginTabControl.SuspendLayout();
            this.qrTabPage.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.qrPictureBox)).BeginInit();
            this.captchaTabPage.SuspendLayout();
            this.webTabPage.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.webLoginView)).BeginInit();
            this.webTopPanel.SuspendLayout();
            this.SuspendLayout();
            //
            // loginTabControl
            //
            this.loginTabControl.Controls.Add(this.qrTabPage);
            this.loginTabControl.Controls.Add(this.captchaTabPage);
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
            // captchaTabPage
            //
            this.captchaTabPage.Controls.Add(this.smsStatusLabel);
            this.captchaTabPage.Controls.Add(this.captchaLoginButton);
            this.captchaTabPage.Controls.Add(this.captchaTextBox);
            this.captchaTabPage.Controls.Add(this.smsCaptchaLabel);
            this.captchaTabPage.Controls.Add(this.sendCaptchaButton);
            this.captchaTabPage.Controls.Add(this.phoneTextBox);
            this.captchaTabPage.Controls.Add(this.smsPhoneLabel);
            this.captchaTabPage.Controls.Add(this.countryCodeComboBox);
            this.captchaTabPage.Controls.Add(this.smsCountryLabel);
            this.captchaTabPage.Location = new System.Drawing.Point(4, 28);
            this.captchaTabPage.Name = "captchaTabPage";
            this.captchaTabPage.Padding = new System.Windows.Forms.Padding(3);
            this.captchaTabPage.Size = new System.Drawing.Size(476, 529);
            this.captchaTabPage.TabIndex = 1;
            this.captchaTabPage.Text = "验证码登录";
            this.captchaTabPage.UseVisualStyleBackColor = true;
            //
            // smsStatusLabel
            //
            this.smsStatusLabel.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
            this.smsStatusLabel.Location = new System.Drawing.Point(20, 280);
            this.smsStatusLabel.Name = "smsStatusLabel";
            this.smsStatusLabel.Size = new System.Drawing.Size(436, 60);
            this.smsStatusLabel.TabIndex = 9;
            this.smsStatusLabel.Text = "请输入手机号获取验证码";
            this.smsStatusLabel.TextAlign = System.Drawing.ContentAlignment.TopCenter;
            //
            // captchaLoginButton
            //
            this.captchaLoginButton.AccessibleName = "登录";
            this.captchaLoginButton.Location = new System.Drawing.Point(167, 220);
            this.captchaLoginButton.Name = "captchaLoginButton";
            this.captchaLoginButton.Size = new System.Drawing.Size(142, 40);
            this.captchaLoginButton.TabIndex = 4;
            this.captchaLoginButton.Text = "登录";
            this.captchaLoginButton.UseVisualStyleBackColor = true;
            //
            // captchaTextBox
            //
            this.captchaTextBox.AccessibleName = "验证码";
            this.captchaTextBox.Location = new System.Drawing.Point(120, 146);
            this.captchaTextBox.MaxLength = 4;
            this.captchaTextBox.Name = "captchaTextBox";
            this.captchaTextBox.Size = new System.Drawing.Size(200, 28);
            this.captchaTextBox.TabIndex = 3;
            //
            // smsCaptchaLabel
            //
            this.smsCaptchaLabel.AutoSize = true;
            this.smsCaptchaLabel.Location = new System.Drawing.Point(20, 150);
            this.smsCaptchaLabel.Name = "smsCaptchaLabel";
            this.smsCaptchaLabel.Size = new System.Drawing.Size(90, 18);
            this.smsCaptchaLabel.TabIndex = 6;
            this.smsCaptchaLabel.Text = "验证码(4位):";
            //
            // sendCaptchaButton
            //
            this.sendCaptchaButton.AccessibleName = "发送验证码";
            this.sendCaptchaButton.Location = new System.Drawing.Point(330, 84);
            this.sendCaptchaButton.Name = "sendCaptchaButton";
            this.sendCaptchaButton.Size = new System.Drawing.Size(110, 36);
            this.sendCaptchaButton.TabIndex = 2;
            this.sendCaptchaButton.Text = "发送验证码";
            this.sendCaptchaButton.UseVisualStyleBackColor = true;
            //
            // phoneTextBox
            //
            this.phoneTextBox.AccessibleName = "手机号";
            this.phoneTextBox.Location = new System.Drawing.Point(120, 86);
            this.phoneTextBox.MaxLength = 20;
            this.phoneTextBox.Name = "phoneTextBox";
            this.phoneTextBox.Size = new System.Drawing.Size(200, 28);
            this.phoneTextBox.TabIndex = 1;
            //
            // smsPhoneLabel
            //
            this.smsPhoneLabel.AutoSize = true;
            this.smsPhoneLabel.Location = new System.Drawing.Point(20, 90);
            this.smsPhoneLabel.Name = "smsPhoneLabel";
            this.smsPhoneLabel.Size = new System.Drawing.Size(54, 18);
            this.smsPhoneLabel.TabIndex = 3;
            this.smsPhoneLabel.Text = "手机号:";
            //
            // countryCodeComboBox
            //
            this.countryCodeComboBox.AccessibleName = "国家地区";
            this.countryCodeComboBox.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDown;
            this.countryCodeComboBox.FormattingEnabled = true;
            this.countryCodeComboBox.Location = new System.Drawing.Point(120, 26);
            this.countryCodeComboBox.Name = "countryCodeComboBox";
            this.countryCodeComboBox.Size = new System.Drawing.Size(320, 26);
            this.countryCodeComboBox.TabIndex = 0;
            //
            // smsCountryLabel
            //
            this.smsCountryLabel.AutoSize = true;
            this.smsCountryLabel.Location = new System.Drawing.Point(20, 30);
            this.smsCountryLabel.Name = "smsCountryLabel";
            this.smsCountryLabel.Size = new System.Drawing.Size(78, 18);
            this.smsCountryLabel.TabIndex = 1;
            this.smsCountryLabel.Text = "国家/地区:";
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
            this.webReloadButton.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
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
            this.webStatusLabel.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
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
            this.captchaTabPage.ResumeLayout(false);
            this.captchaTabPage.PerformLayout();
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
        private System.Windows.Forms.TabPage captchaTabPage;
        private System.Windows.Forms.Label smsStatusLabel;
        private System.Windows.Forms.Button captchaLoginButton;
        private System.Windows.Forms.TextBox captchaTextBox;
        private System.Windows.Forms.Label smsCaptchaLabel;
        private System.Windows.Forms.Button sendCaptchaButton;
        private System.Windows.Forms.TextBox phoneTextBox;
        private System.Windows.Forms.Label smsPhoneLabel;
        private System.Windows.Forms.ComboBox countryCodeComboBox;
        private System.Windows.Forms.Label smsCountryLabel;
        private System.Windows.Forms.TabPage webTabPage;
        private Microsoft.Web.WebView2.WinForms.WebView2 webLoginView;
        private System.Windows.Forms.Panel webTopPanel;
        private System.Windows.Forms.Label webStatusLabel;
        private System.Windows.Forms.Button webReloadButton;
    }
}
