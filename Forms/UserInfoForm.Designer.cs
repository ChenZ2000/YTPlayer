namespace YTPlayer.Forms
{
    partial class UserInfoForm
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
            this.loadingLabel = new System.Windows.Forms.Label();
            this.infoTextBox = new System.Windows.Forms.TextBox();
            this.buttonPanel = new System.Windows.Forms.Panel();
            this.logoutButton = new System.Windows.Forms.Button();
            this.closeButton = new System.Windows.Forms.Button();
            this.buttonPanel.SuspendLayout();
            this.SuspendLayout();
            //
            // loadingLabel
            //
            this.loadingLabel.Dock = System.Windows.Forms.DockStyle.Fill;
            this.loadingLabel.Font = new System.Drawing.Font("Microsoft YaHei UI", 10F);
            this.loadingLabel.Location = new System.Drawing.Point(0, 0);
            this.loadingLabel.Name = "loadingLabel";
            this.loadingLabel.Size = new System.Drawing.Size(484, 361);
            this.loadingLabel.TabIndex = 0;
            this.loadingLabel.Text = "正在加载用户信息...";
            this.loadingLabel.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            //
            // infoTextBox
            //
            this.infoTextBox.BackColor = System.Drawing.SystemColors.Window;
            this.infoTextBox.Dock = System.Windows.Forms.DockStyle.Fill;
            this.infoTextBox.Font = new System.Drawing.Font("Microsoft YaHei UI", 9.75F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.infoTextBox.Location = new System.Drawing.Point(0, 0);
            this.infoTextBox.Multiline = true;
            this.infoTextBox.Name = "infoTextBox";
            this.infoTextBox.ReadOnly = true;
            this.infoTextBox.ScrollBars = System.Windows.Forms.ScrollBars.Vertical;
            this.infoTextBox.Size = new System.Drawing.Size(484, 301);
            this.infoTextBox.TabIndex = 0;
            this.infoTextBox.TabStop = true;
            this.infoTextBox.Visible = false;
            //
            // buttonPanel
            //
            this.buttonPanel.Controls.Add(this.logoutButton);
            this.buttonPanel.Controls.Add(this.closeButton);
            this.buttonPanel.Dock = System.Windows.Forms.DockStyle.Bottom;
            this.buttonPanel.Location = new System.Drawing.Point(0, 301);
            this.buttonPanel.Name = "buttonPanel";
            this.buttonPanel.Size = new System.Drawing.Size(484, 60);
            this.buttonPanel.TabIndex = 2;
            //
            // logoutButton
            //
            this.logoutButton.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
            this.logoutButton.Location = new System.Drawing.Point(100, 12);
            this.logoutButton.Name = "logoutButton";
            this.logoutButton.Size = new System.Drawing.Size(120, 36);
            this.logoutButton.TabIndex = 1;
            this.logoutButton.Text = "退出登录(&L)";
            this.logoutButton.UseVisualStyleBackColor = true;
            this.logoutButton.Click += new System.EventHandler(this.logoutButton_Click);
            //
            // closeButton
            //
            this.closeButton.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
            this.closeButton.Location = new System.Drawing.Point(260, 12);
            this.closeButton.Name = "closeButton";
            this.closeButton.Size = new System.Drawing.Size(120, 36);
            this.closeButton.TabIndex = 2;
            this.closeButton.Text = "关闭(&C)";
            this.closeButton.UseVisualStyleBackColor = true;
            this.closeButton.Click += new System.EventHandler(this.closeButton_Click);
            //
            // UserInfoForm
            //
            this.AutoScaleDimensions = new System.Drawing.SizeF(120F, 120F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Dpi;
            this.ClientSize = new System.Drawing.Size(484, 361);
            this.Controls.Add(this.infoTextBox);
            this.Controls.Add(this.buttonPanel);
            this.Controls.Add(this.loadingLabel);
            this.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
            this.Name = "UserInfoForm";
            this.Text = "用户信息";
            this.buttonPanel.ResumeLayout(false);
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.Label loadingLabel;
        private System.Windows.Forms.TextBox infoTextBox;
        private System.Windows.Forms.Panel buttonPanel;
        private System.Windows.Forms.Button logoutButton;
        private System.Windows.Forms.Button closeButton;
    }
}
