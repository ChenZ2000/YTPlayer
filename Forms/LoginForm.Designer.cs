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
            this.smsTabPage = new System.Windows.Forms.TabPage();
            this.countryCodeLabel = new System.Windows.Forms.Label();
            this.countryCodeTextBox = new System.Windows.Forms.TextBox();
            this.phoneLabel = new System.Windows.Forms.Label();
            this.phoneTextBox = new System.Windows.Forms.TextBox();
            this.sendSmsButton = new System.Windows.Forms.Button();
            this.captchaLabel = new System.Windows.Forms.Label();
            this.captchaTextBox = new System.Windows.Forms.TextBox();
            this.smsStatusLabel = new System.Windows.Forms.Label();
            this.smsLoginButton = new System.Windows.Forms.Button();
            this.loginTabControl.SuspendLayout();
            this.qrTabPage.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.qrPictureBox)).BeginInit();
            this.smsTabPage.SuspendLayout();
            this.SuspendLayout();
            //
            // loginTabControl
            //
            this.loginTabControl.Controls.Add(this.qrTabPage);
            this.loginTabControl.Controls.Add(this.smsTabPage);
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
            // smsTabPage
            //
            this.smsTabPage.Controls.Add(this.countryCodeLabel);
            this.smsTabPage.Controls.Add(this.countryCodeTextBox);
            this.smsTabPage.Controls.Add(this.phoneLabel);
            this.smsTabPage.Controls.Add(this.phoneTextBox);
            this.smsTabPage.Controls.Add(this.sendSmsButton);
            this.smsTabPage.Controls.Add(this.captchaLabel);
            this.smsTabPage.Controls.Add(this.captchaTextBox);
            this.smsTabPage.Controls.Add(this.smsStatusLabel);
            this.smsTabPage.Controls.Add(this.smsLoginButton);
            this.smsTabPage.Location = new System.Drawing.Point(4, 28);
            this.smsTabPage.Name = "smsTabPage";
            this.smsTabPage.Padding = new System.Windows.Forms.Padding(3);
            this.smsTabPage.Size = new System.Drawing.Size(476, 529);
            this.smsTabPage.TabIndex = 1;
            this.smsTabPage.Text = "短信验证码登录";
            this.smsTabPage.UseVisualStyleBackColor = true;
            //
            // countryCodeLabel
            //
            this.countryCodeLabel.AutoSize = true;
            this.countryCodeLabel.Font = new System.Drawing.Font("Microsoft YaHei UI", 10F);
            this.countryCodeLabel.Location = new System.Drawing.Point(90, 60);
            this.countryCodeLabel.Name = "countryCodeLabel";
            this.countryCodeLabel.Size = new System.Drawing.Size(92, 27);
            this.countryCodeLabel.TabIndex = 8;
            this.countryCodeLabel.Text = "国家号：";
            //
            // countryCodeTextBox
            //
            this.countryCodeTextBox.AccessibleDescription = "输入国家或地区代码，默认为中国大陆86";
            this.countryCodeTextBox.AccessibleName = "国家号";
            this.countryCodeTextBox.Font = new System.Drawing.Font("Microsoft YaHei UI", 10F);
            this.countryCodeTextBox.Location = new System.Drawing.Point(90, 90);
            this.countryCodeTextBox.MaxLength = 5;
            this.countryCodeTextBox.Name = "countryCodeTextBox";
            this.countryCodeTextBox.Size = new System.Drawing.Size(80, 34);
            this.countryCodeTextBox.TabIndex = 0;
            this.countryCodeTextBox.Text = "86";
            //
            // phoneLabel
            //
            this.phoneLabel.AutoSize = true;
            this.phoneLabel.Font = new System.Drawing.Font("Microsoft YaHei UI", 10F);
            this.phoneLabel.Location = new System.Drawing.Point(190, 60);
            this.phoneLabel.Name = "phoneLabel";
            this.phoneLabel.Size = new System.Drawing.Size(92, 27);
            this.phoneLabel.TabIndex = 9;
            this.phoneLabel.Text = "手机号：";
            //
            // phoneTextBox
            //
            this.phoneTextBox.AccessibleDescription = "输入手机号码";
            this.phoneTextBox.AccessibleName = "手机号";
            this.phoneTextBox.Font = new System.Drawing.Font("Microsoft YaHei UI", 10F);
            this.phoneTextBox.Location = new System.Drawing.Point(190, 90);
            this.phoneTextBox.MaxLength = 11;
            this.phoneTextBox.Name = "phoneTextBox";
            this.phoneTextBox.Size = new System.Drawing.Size(154, 34);
            this.phoneTextBox.TabIndex = 1;
            this.phoneTextBox.Text = "";
            //
            // sendSmsButton
            //
            this.sendSmsButton.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
            this.sendSmsButton.Location = new System.Drawing.Point(90, 140);
            this.sendSmsButton.Name = "sendSmsButton";
            this.sendSmsButton.Size = new System.Drawing.Size(296, 35);
            this.sendSmsButton.TabIndex = 2;
            this.sendSmsButton.Text = "发送验证码";
            this.sendSmsButton.UseVisualStyleBackColor = true;
            this.sendSmsButton.Click += new System.EventHandler(this.sendSmsButton_Click);
            //
            // captchaLabel
            //
            this.captchaLabel.AutoSize = true;
            this.captchaLabel.Font = new System.Drawing.Font("Microsoft YaHei UI", 10F);
            this.captchaLabel.Location = new System.Drawing.Point(90, 190);
            this.captchaLabel.Name = "captchaLabel";
            this.captchaLabel.Size = new System.Drawing.Size(92, 27);
            this.captchaLabel.TabIndex = 10;
            this.captchaLabel.Text = "验证码：";
            //
            // captchaTextBox
            //
            this.captchaTextBox.AccessibleDescription = "输入手机收到的6位验证码";
            this.captchaTextBox.AccessibleName = "验证码";
            this.captchaTextBox.Font = new System.Drawing.Font("Microsoft YaHei UI", 10F);
            this.captchaTextBox.Location = new System.Drawing.Point(90, 220);
            this.captchaTextBox.MaxLength = 6;
            this.captchaTextBox.Name = "captchaTextBox";
            this.captchaTextBox.Size = new System.Drawing.Size(296, 34);
            this.captchaTextBox.TabIndex = 3;
            this.captchaTextBox.Text = "";
            //
            // smsStatusLabel
            //
            this.smsStatusLabel.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
            this.smsStatusLabel.Location = new System.Drawing.Point(90, 270);
            this.smsStatusLabel.Name = "smsStatusLabel";
            this.smsStatusLabel.Size = new System.Drawing.Size(296, 30);
            this.smsStatusLabel.TabIndex = 11;
            this.smsStatusLabel.Text = "";
            this.smsStatusLabel.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            //
            // smsLoginButton
            //
            this.smsLoginButton.Font = new System.Drawing.Font("Microsoft YaHei UI", 10F);
            this.smsLoginButton.Location = new System.Drawing.Point(90, 315);
            this.smsLoginButton.Name = "smsLoginButton";
            this.smsLoginButton.Size = new System.Drawing.Size(296, 45);
            this.smsLoginButton.TabIndex = 4;
            this.smsLoginButton.Text = "登录";
            this.smsLoginButton.UseVisualStyleBackColor = true;
            this.smsLoginButton.Click += new System.EventHandler(this.smsLoginButton_Click);
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
            this.smsTabPage.ResumeLayout(false);
            this.smsTabPage.PerformLayout();
            this.ResumeLayout(false);

        }

        #endregion

        private System.Windows.Forms.TabControl loginTabControl;
        private System.Windows.Forms.TabPage qrTabPage;
        private System.Windows.Forms.PictureBox qrPictureBox;
        private System.Windows.Forms.Label qrStatusLabel;
        private System.Windows.Forms.Button refreshQrButton;
        private System.Windows.Forms.TabPage smsTabPage;
        private System.Windows.Forms.Label countryCodeLabel;
        private System.Windows.Forms.TextBox countryCodeTextBox;
        private System.Windows.Forms.Label phoneLabel;
        private System.Windows.Forms.TextBox phoneTextBox;
        private System.Windows.Forms.Button sendSmsButton;
        private System.Windows.Forms.Label captchaLabel;
        private System.Windows.Forms.TextBox captchaTextBox;
        private System.Windows.Forms.Label smsStatusLabel;
        private System.Windows.Forms.Button smsLoginButton;
    }
}
