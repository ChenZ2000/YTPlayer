using System.Windows.Forms;
namespace YTPlayer.Forms.Download
{
    partial class DownloadManagerForm
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
            this.tabControl = new System.Windows.Forms.TabControl();
            this.tabPageActive = new System.Windows.Forms.TabPage();
            this.lvActive = new System.Windows.Forms.ListView();
            this.btnCancelAll = new System.Windows.Forms.Button();
            this.tabPageUpload = new System.Windows.Forms.TabPage();
            this.lvUpload = new System.Windows.Forms.ListView();
            this.btnCancelAllUpload = new System.Windows.Forms.Button();
            this.tabPageCompleted = new System.Windows.Forms.TabPage();
            this.lvCompleted = new System.Windows.Forms.ListView();
            this.btnClearAll = new System.Windows.Forms.Button();
            this.tabControl.SuspendLayout();
            this.tabPageActive.SuspendLayout();
            this.tabPageUpload.SuspendLayout();
            this.tabPageCompleted.SuspendLayout();
            this.SuspendLayout();
            //
            // tabControl
            //
            this.tabControl.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom)
            | System.Windows.Forms.AnchorStyles.Left)
            | System.Windows.Forms.AnchorStyles.Right)));
            this.tabControl.Controls.Add(this.tabPageActive);
            this.tabControl.Controls.Add(this.tabPageUpload);
            this.tabControl.Controls.Add(this.tabPageCompleted);
            this.tabControl.Location = new System.Drawing.Point(12, 12);
            this.tabControl.Name = "tabControl";
            this.tabControl.SelectedIndex = 0;
            this.tabControl.Size = new System.Drawing.Size(960, 497);
            this.tabControl.TabIndex = 0;
            this.tabControl.SelectedIndexChanged += new System.EventHandler(this.TabControl_SelectedIndexChanged);
            //
            // tabPageActive
            //
            this.tabPageActive.Controls.Add(this.btnCancelAll);
            this.tabPageActive.Controls.Add(this.lvActive);
            this.tabPageActive.Location = new System.Drawing.Point(4, 28);
            this.tabPageActive.Name = "tabPageActive";
            this.tabPageActive.Padding = new System.Windows.Forms.Padding(3);
            this.tabPageActive.Size = new System.Drawing.Size(952, 465);
            this.tabPageActive.TabIndex = 0;
            this.tabPageActive.Text = "下载中";
            this.tabPageActive.UseVisualStyleBackColor = true;
            //
            // lvActive
            //
            this.lvActive.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom)
            | System.Windows.Forms.AnchorStyles.Left)
            | System.Windows.Forms.AnchorStyles.Right)));
            this.lvActive.HideSelection = false;
            this.lvActive.Location = new System.Drawing.Point(6, 6);
            this.lvActive.Name = "lvActive";
            this.lvActive.Size = new System.Drawing.Size(940, 413);
            this.lvActive.TabIndex = 0;
            this.lvActive.UseCompatibleStateImageBehavior = false;
            //
            // btnCancelAll
            //
            this.btnCancelAll.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.btnCancelAll.Location = new System.Drawing.Point(846, 425);
            this.btnCancelAll.Name = "btnCancelAll";
            this.btnCancelAll.Size = new System.Drawing.Size(100, 34);
            this.btnCancelAll.TabIndex = 1;
            this.btnCancelAll.Text = "全部取消";
            this.btnCancelAll.UseVisualStyleBackColor = true;
            this.btnCancelAll.Click += new System.EventHandler(this.BtnCancelAll_Click);
            //
            // tabPageUpload
            //
            this.tabPageUpload.Controls.Add(this.btnCancelAllUpload);
            this.tabPageUpload.Controls.Add(this.lvUpload);
            this.tabPageUpload.Location = new System.Drawing.Point(4, 28);
            this.tabPageUpload.Name = "tabPageUpload";
            this.tabPageUpload.Padding = new System.Windows.Forms.Padding(3);
            this.tabPageUpload.Size = new System.Drawing.Size(952, 465);
            this.tabPageUpload.TabIndex = 2;
            this.tabPageUpload.Text = "上传中";
            this.tabPageUpload.UseVisualStyleBackColor = true;
            //
            // lvUpload
            //
            this.lvUpload.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom)
            | System.Windows.Forms.AnchorStyles.Left)
            | System.Windows.Forms.AnchorStyles.Right)));
            this.lvUpload.HideSelection = false;
            this.lvUpload.Location = new System.Drawing.Point(6, 6);
            this.lvUpload.Name = "lvUpload";
            this.lvUpload.Size = new System.Drawing.Size(940, 413);
            this.lvUpload.TabIndex = 0;
            this.lvUpload.UseCompatibleStateImageBehavior = false;
            //
            // btnCancelAllUpload
            //
            this.btnCancelAllUpload.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.btnCancelAllUpload.Location = new System.Drawing.Point(846, 425);
            this.btnCancelAllUpload.Name = "btnCancelAllUpload";
            this.btnCancelAllUpload.Size = new System.Drawing.Size(100, 34);
            this.btnCancelAllUpload.TabIndex = 1;
            this.btnCancelAllUpload.Text = "全部取消";
            this.btnCancelAllUpload.UseVisualStyleBackColor = true;
            this.btnCancelAllUpload.Click += new System.EventHandler(this.BtnCancelAllUpload_Click);
            //
            // tabPageCompleted
            //
            this.tabPageCompleted.Controls.Add(this.btnClearAll);
            this.tabPageCompleted.Controls.Add(this.lvCompleted);
            this.tabPageCompleted.Location = new System.Drawing.Point(4, 28);
            this.tabPageCompleted.Name = "tabPageCompleted";
            this.tabPageCompleted.Padding = new System.Windows.Forms.Padding(3);
            this.tabPageCompleted.Size = new System.Drawing.Size(952, 465);
            this.tabPageCompleted.TabIndex = 1;
            this.tabPageCompleted.Text = "已完成";
            this.tabPageCompleted.UseVisualStyleBackColor = true;
            //
            // lvCompleted
            //
            this.lvCompleted.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom)
            | System.Windows.Forms.AnchorStyles.Left)
            | System.Windows.Forms.AnchorStyles.Right)));
            this.lvCompleted.HideSelection = false;
            this.lvCompleted.Location = new System.Drawing.Point(6, 6);
            this.lvCompleted.Name = "lvCompleted";
            this.lvCompleted.Size = new System.Drawing.Size(940, 413);
            this.lvCompleted.TabIndex = 0;
            this.lvCompleted.UseCompatibleStateImageBehavior = false;
            //
            // btnClearAll
            //
            this.btnClearAll.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.btnClearAll.Location = new System.Drawing.Point(846, 425);
            this.btnClearAll.Name = "btnClearAll";
            this.btnClearAll.Size = new System.Drawing.Size(100, 34);
            this.btnClearAll.TabIndex = 1;
            this.btnClearAll.Text = "全部清除";
            this.btnClearAll.UseVisualStyleBackColor = true;
            this.btnClearAll.Click += new System.EventHandler(this.BtnClearAll_Click);
            //
            // DownloadManagerForm
            //
            this.AutoScaleDimensions = new System.Drawing.SizeF(9F, 19F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(984, 521);
            this.Controls.Add(this.tabControl);
            this.MinimumSize = new System.Drawing.Size(800, 400);
            this.Name = "DownloadManagerForm";
            this.ShowIcon = false;
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            this.Text = "传输管理";
            this.tabControl.ResumeLayout(false);
            this.tabPageActive.ResumeLayout(false);
            this.tabPageUpload.ResumeLayout(false);
            this.tabPageCompleted.ResumeLayout(false);
            this.ResumeLayout(false);

        }

        #endregion

        private System.Windows.Forms.TabControl tabControl;
        private System.Windows.Forms.TabPage tabPageActive;
        private System.Windows.Forms.TabPage tabPageUpload;
        private System.Windows.Forms.TabPage tabPageCompleted;
        private System.Windows.Forms.ListView lvActive;
        private System.Windows.Forms.ListView lvUpload;
        private System.Windows.Forms.ListView lvCompleted;
        private System.Windows.Forms.Button btnCancelAll;
        private System.Windows.Forms.Button btnCancelAllUpload;
        private System.Windows.Forms.Button btnClearAll;
    }
}
