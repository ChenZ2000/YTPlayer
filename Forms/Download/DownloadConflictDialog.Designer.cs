namespace YTPlayer.Forms.Download
{
    partial class DownloadConflictDialog
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
            this.lblInfo = new System.Windows.Forms.Label();
            this.listView = new System.Windows.Forms.ListView();
            this.columnHeaderFileName = ((System.Windows.Forms.ColumnHeader)(new System.Windows.Forms.ColumnHeader()));
            this.btnSkip = new System.Windows.Forms.Button();
            this.btnOverwrite = new System.Windows.Forms.Button();
            this.btnSkipAll = new System.Windows.Forms.Button();
            this.btnOverwriteAll = new System.Windows.Forms.Button();
            this.btnCancel = new System.Windows.Forms.Button();
            this.SuspendLayout();
            //
            // lblInfo
            //
            this.lblInfo.AutoSize = true;
            this.lblInfo.Location = new System.Drawing.Point(12, 12);
            this.lblInfo.Name = "lblInfo";
            this.lblInfo.Size = new System.Drawing.Size(189, 19);
            this.lblInfo.TabIndex = 0;
            this.lblInfo.Text = "发现 0 个文件冲突：";
            //
            // listView
            //
            this.listView.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom)
            | System.Windows.Forms.AnchorStyles.Left)
            | System.Windows.Forms.AnchorStyles.Right)));
            this.listView.Columns.AddRange(new System.Windows.Forms.ColumnHeader[] {
            this.columnHeaderFileName});
            this.listView.HideSelection = false;
            this.listView.Location = new System.Drawing.Point(12, 40);
            this.listView.Name = "listView";
            this.listView.Size = new System.Drawing.Size(560, 200);
            this.listView.TabIndex = 1;
            this.listView.UseCompatibleStateImageBehavior = false;
            this.listView.View = System.Windows.Forms.View.Details;
            //
            // columnHeaderFileName
            //
            this.columnHeaderFileName.Text = "文件名";
            this.columnHeaderFileName.Width = 500;
            //
            // btnSkip
            //
            this.btnSkip.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left)));
            this.btnSkip.Location = new System.Drawing.Point(12, 250);
            this.btnSkip.Name = "btnSkip";
            this.btnSkip.Size = new System.Drawing.Size(90, 34);
            this.btnSkip.TabIndex = 2;
            this.btnSkip.Text = "跳过";
            this.btnSkip.UseVisualStyleBackColor = true;
            this.btnSkip.Click += new System.EventHandler(this.BtnSkip_Click);
            //
            // btnOverwrite
            //
            this.btnOverwrite.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left)));
            this.btnOverwrite.Location = new System.Drawing.Point(108, 250);
            this.btnOverwrite.Name = "btnOverwrite";
            this.btnOverwrite.Size = new System.Drawing.Size(90, 34);
            this.btnOverwrite.TabIndex = 3;
            this.btnOverwrite.Text = "覆盖";
            this.btnOverwrite.UseVisualStyleBackColor = true;
            this.btnOverwrite.Click += new System.EventHandler(this.BtnOverwrite_Click);
            //
            // btnSkipAll
            //
            this.btnSkipAll.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left)));
            this.btnSkipAll.Location = new System.Drawing.Point(204, 250);
            this.btnSkipAll.Name = "btnSkipAll";
            this.btnSkipAll.Size = new System.Drawing.Size(110, 34);
            this.btnSkipAll.TabIndex = 4;
            this.btnSkipAll.Text = "全部跳过";
            this.btnSkipAll.UseVisualStyleBackColor = true;
            this.btnSkipAll.Click += new System.EventHandler(this.BtnSkipAll_Click);
            //
            // btnOverwriteAll
            //
            this.btnOverwriteAll.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left)));
            this.btnOverwriteAll.Location = new System.Drawing.Point(320, 250);
            this.btnOverwriteAll.Name = "btnOverwriteAll";
            this.btnOverwriteAll.Size = new System.Drawing.Size(110, 34);
            this.btnOverwriteAll.TabIndex = 5;
            this.btnOverwriteAll.Text = "全部覆盖";
            this.btnOverwriteAll.UseVisualStyleBackColor = true;
            this.btnOverwriteAll.Click += new System.EventHandler(this.BtnOverwriteAll_Click);
            //
            // btnCancel
            //
            this.btnCancel.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.btnCancel.Location = new System.Drawing.Point(478, 250);
            this.btnCancel.Name = "btnCancel";
            this.btnCancel.Size = new System.Drawing.Size(90, 34);
            this.btnCancel.TabIndex = 6;
            this.btnCancel.Text = "取消";
            this.btnCancel.UseVisualStyleBackColor = true;
            this.btnCancel.Click += new System.EventHandler(this.BtnCancel_Click);
            //
            // DownloadConflictDialog
            //
            this.AutoScaleDimensions = new System.Drawing.SizeF(9F, 19F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(584, 296);
            this.Controls.Add(this.btnCancel);
            this.Controls.Add(this.btnOverwriteAll);
            this.Controls.Add(this.btnSkipAll);
            this.Controls.Add(this.btnOverwrite);
            this.Controls.Add(this.btnSkip);
            this.Controls.Add(this.listView);
            this.Controls.Add(this.lblInfo);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "DownloadConflictDialog";
            this.ShowIcon = false;
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            this.Text = "文件冲突";
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.Label lblInfo;
        private System.Windows.Forms.ListView listView;
        private System.Windows.Forms.ColumnHeader columnHeaderFileName;
        private System.Windows.Forms.Button btnSkip;
        private System.Windows.Forms.Button btnOverwrite;
        private System.Windows.Forms.Button btnSkipAll;
        private System.Windows.Forms.Button btnOverwriteAll;
        private System.Windows.Forms.Button btnCancel;
    }
}
