namespace YTPlayer.Updater
{
    partial class UpdaterForm
    {
        /// <summary>
        ///  Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        #region Windows Form Designer generated code

        /// <summary>
        ///  Required method for Designer support - do not modify
        ///  the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.headingLabel = new System.Windows.Forms.Label();
            this.versionLabel = new System.Windows.Forms.Label();
            this.statusLabel = new System.Windows.Forms.Label();
            this.progressBar = new YTPlayer.Utils.ThemedProgressBar();
            this.logPanel = new System.Windows.Forms.Panel();
            this.logListBox = new System.Windows.Forms.ListBox();
            this.logPlaceholderLabel = new System.Windows.Forms.Label();
            this.cancelButton = new System.Windows.Forms.Button();
            this.resumeButton = new System.Windows.Forms.Button();
            this.logPanel.SuspendLayout();
            this.SuspendLayout();
            // 
            // headingLabel
            // 
            this.headingLabel.AutoSize = true;
            this.headingLabel.Font = new System.Drawing.Font("Microsoft YaHei UI", 12F, System.Drawing.FontStyle.Bold);
            this.headingLabel.Location = new System.Drawing.Point(20, 15);
            this.headingLabel.Name = "headingLabel";
            this.headingLabel.Size = new System.Drawing.Size(156, 27);
            this.headingLabel.TabIndex = 0;
            this.headingLabel.Text = "正在更新至 v0";
            // 
            // versionLabel
            // 
            this.versionLabel.AutoSize = true;
            this.versionLabel.Location = new System.Drawing.Point(22, 50);
            this.versionLabel.Name = "versionLabel";
            this.versionLabel.Size = new System.Drawing.Size(125, 20);
            this.versionLabel.TabIndex = 1;
            this.versionLabel.Text = "目标版本：v0.0.0";
            // 
            // statusLabel
            // 
            this.statusLabel.AutoEllipsis = true;
            this.statusLabel.Location = new System.Drawing.Point(22, 82);
            this.statusLabel.Name = "statusLabel";
            this.statusLabel.Size = new System.Drawing.Size(506, 23);
            this.statusLabel.TabIndex = 2;
            this.statusLabel.Text = "准备开始...";
            // 
            // progressBar
            // 
            this.progressBar.Location = new System.Drawing.Point(22, 110);
            this.progressBar.MarqueeAnimationSpeed = 25;
            this.progressBar.Name = "progressBar";
            this.progressBar.Size = new System.Drawing.Size(506, 18);
            this.progressBar.Style = System.Windows.Forms.ProgressBarStyle.Continuous;
            this.progressBar.TabIndex = 3;
            // 
            // logPanel
            // 
            this.logPanel.Controls.Add(this.logListBox);
            this.logPanel.Controls.Add(this.logPlaceholderLabel);
            this.logPanel.Location = new System.Drawing.Point(22, 140);
            this.logPanel.Name = "logPanel";
            this.logPanel.Size = new System.Drawing.Size(506, 184);
            this.logPanel.TabIndex = 4;
            // 
            // logListBox
            // 
            this.logListBox.Dock = System.Windows.Forms.DockStyle.Fill;
            this.logListBox.FormattingEnabled = true;
            this.logListBox.HorizontalScrollbar = true;
            this.logListBox.ItemHeight = 20;
            this.logListBox.Name = "logListBox";
            this.logListBox.Size = new System.Drawing.Size(506, 184);
            this.logListBox.TabIndex = 0;
            // 
            // logPlaceholderLabel
            // 
            this.logPlaceholderLabel.BackColor = System.Drawing.Color.Transparent;
            this.logPlaceholderLabel.Dock = System.Windows.Forms.DockStyle.Fill;
            this.logPlaceholderLabel.Location = new System.Drawing.Point(0, 0);
            this.logPlaceholderLabel.Name = "logPlaceholderLabel";
            this.logPlaceholderLabel.Size = new System.Drawing.Size(506, 184);
            this.logPlaceholderLabel.TabIndex = 1;
            this.logPlaceholderLabel.Text = "暂无日志";
            this.logPlaceholderLabel.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            // 
            // cancelButton
            //
            this.cancelButton.DialogResult = System.Windows.Forms.DialogResult.Cancel;
            this.cancelButton.Location = new System.Drawing.Point(417, 333);
            this.cancelButton.Name = "cancelButton";
            this.cancelButton.Size = new System.Drawing.Size(111, 32);
            this.cancelButton.TabIndex = 5;
            this.cancelButton.Text = "关闭";
            this.cancelButton.UseVisualStyleBackColor = true;
            this.cancelButton.Click += new System.EventHandler(this.cancelButton_Click);
            //
            // resumeButton
            //
            this.resumeButton.Location = new System.Drawing.Point(298, 333);
            this.resumeButton.Name = "resumeButton";
            this.resumeButton.Size = new System.Drawing.Size(111, 32);
            this.resumeButton.TabIndex = 6;
            this.resumeButton.Text = "回到易听";
            this.resumeButton.UseVisualStyleBackColor = true;
            this.resumeButton.Visible = false;
            this.resumeButton.Click += new System.EventHandler(this.resumeButton_Click);
            //
            // UpdaterForm
            //
            this.AutoScaleDimensions = new System.Drawing.SizeF(8F, 20F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.CancelButton = this.cancelButton;
            this.ClientSize = new System.Drawing.Size(550, 380);
            this.Controls.Add(this.resumeButton);
            this.Controls.Add(this.cancelButton);
            this.Controls.Add(this.logPanel);
            this.Controls.Add(this.progressBar);
            this.Controls.Add(this.statusLabel);
            this.Controls.Add(this.versionLabel);
            this.Controls.Add(this.headingLabel);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "UpdaterForm";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "易听 - 更新程序";
            this.logPanel.ResumeLayout(false);
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.Label headingLabel;
        private System.Windows.Forms.Label versionLabel;
        private System.Windows.Forms.Label statusLabel;
        private YTPlayer.Utils.ThemedProgressBar progressBar;
        private System.Windows.Forms.Panel logPanel;
        private System.Windows.Forms.ListBox logListBox;
        private System.Windows.Forms.Label logPlaceholderLabel;
        private System.Windows.Forms.Button cancelButton;
        private System.Windows.Forms.Button resumeButton;
    }
}
