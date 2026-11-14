using System.Windows.Forms;
namespace YTPlayer.Forms
{
    partial class UpdateAvailablePromptDialog
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
            this.summaryLabel = new System.Windows.Forms.Label();
            this.resultView = new AccessibleDocumentView();
            this.updateButton = new System.Windows.Forms.Button();
            this.closeButton = new System.Windows.Forms.Button();
            this.SuspendLayout();
            // 
            // summaryLabel
            // 
            this.summaryLabel.AutoSize = true;
            this.summaryLabel.Font = new System.Drawing.Font("Microsoft YaHei UI", 11F, System.Drawing.FontStyle.Bold);
            this.summaryLabel.Location = new System.Drawing.Point(24, 20);
            this.summaryLabel.Name = "summaryLabel";
            this.summaryLabel.Size = new System.Drawing.Size(119, 30);
            this.summaryLabel.TabIndex = 0;
            this.summaryLabel.Text = "发现新版本";
            // 
            // resultView
            // 
            this.resultView.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom)
            | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.resultView.BackColor = System.Drawing.Color.White;
            this.resultView.Location = new System.Drawing.Point(29, 63);
            this.resultView.Name = "resultView";
            this.resultView.Size = new System.Drawing.Size(522, 211);
            this.resultView.TabIndex = 1;
            // 
            // updateButton
            // 
            this.updateButton.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.updateButton.Font = new System.Drawing.Font("Microsoft YaHei UI", 10F, System.Drawing.FontStyle.Bold);
            this.updateButton.Location = new System.Drawing.Point(350, 290);
            this.updateButton.Name = "updateButton";
            this.updateButton.Size = new System.Drawing.Size(100, 36);
            this.updateButton.TabIndex = 2;
            this.updateButton.Text = "立即更新";
            this.updateButton.UseVisualStyleBackColor = true;
            this.updateButton.Click += new System.EventHandler(this.updateButton_Click);
            // 
            // closeButton
            // 
            this.closeButton.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.closeButton.DialogResult = System.Windows.Forms.DialogResult.Cancel;
            this.closeButton.Location = new System.Drawing.Point(456, 290);
            this.closeButton.Name = "closeButton";
            this.closeButton.Size = new System.Drawing.Size(95, 36);
            this.closeButton.TabIndex = 3;
            this.closeButton.Text = "关闭";
            this.closeButton.UseVisualStyleBackColor = true;
            this.closeButton.Click += new System.EventHandler(this.closeButton_Click);
            // 
            // UpdateAvailablePromptDialog
            // 
            this.AcceptButton = this.updateButton;
            this.AutoScaleDimensions = new System.Drawing.SizeF(96F, 96F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Dpi;
            this.CancelButton = this.closeButton;
            this.ClientSize = new System.Drawing.Size(576, 345);
            this.resultView.MinimumSize = new System.Drawing.Size(0, 100);
            var buttons = YTPlayer.Utils.DialogLayout.CreateButtonRow(this.closeButton, this.updateButton);
            buttons.TabIndex = 2;
            YTPlayer.Utils.DialogLayout.CreateVertical(this, 1, this.summaryLabel, this.resultView, buttons);
            this.Font = new System.Drawing.Font("Microsoft YaHei UI", 10F);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.Sizable;
            this.MinimumSize = new System.Drawing.Size(420, 300);
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "UpdateAvailablePromptDialog";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            this.Text = "发现新版本";
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.Label summaryLabel;
        private AccessibleDocumentView resultView;
        private System.Windows.Forms.Button updateButton;
        private System.Windows.Forms.Button closeButton;
    }
}
