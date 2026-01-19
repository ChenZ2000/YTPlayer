namespace YTPlayer.Forms
{
    partial class AddToPlaylistDialog
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
            this.titleLabel = new System.Windows.Forms.Label();
            this.playlistListView = new System.Windows.Forms.ListView();
            this.nameColumn = ((System.Windows.Forms.ColumnHeader)(new System.Windows.Forms.ColumnHeader()));
            this.tracksColumn = ((System.Windows.Forms.ColumnHeader)(new System.Windows.Forms.ColumnHeader()));
            this.statusColumn = ((System.Windows.Forms.ColumnHeader)(new System.Windows.Forms.ColumnHeader()));
            this.createPlaylistButton = new System.Windows.Forms.Button();
            this.confirmButton = new System.Windows.Forms.Button();
            this.cancelButton = new System.Windows.Forms.Button();
            this.loadingLabel = new System.Windows.Forms.Label();
            this.SuspendLayout();
            //
            // titleLabel
            //
            this.titleLabel.AutoSize = true;
            this.titleLabel.Font = new System.Drawing.Font("Microsoft YaHei UI", 10F, System.Drawing.FontStyle.Bold);
            this.titleLabel.Location = new System.Drawing.Point(12, 15);
            this.titleLabel.Name = "titleLabel";
            this.titleLabel.Size = new System.Drawing.Size(135, 19);
            this.titleLabel.TabIndex = 0;
            this.titleLabel.Text = "选择要添加到的歌单";
            //
            // playlistListView
            //
            this.playlistListView.Columns.AddRange(new System.Windows.Forms.ColumnHeader[] {
            this.nameColumn,
            this.tracksColumn,
            this.statusColumn});
            this.playlistListView.FullRowSelect = true;
            this.playlistListView.HideSelection = false;
            this.playlistListView.Location = new System.Drawing.Point(16, 45);
            this.playlistListView.MultiSelect = false;
            this.playlistListView.Name = "playlistListView";
            this.playlistListView.Size = new System.Drawing.Size(552, 310);
            this.playlistListView.TabIndex = 1;
            this.playlistListView.UseCompatibleStateImageBehavior = false;
            this.playlistListView.View = System.Windows.Forms.View.Details;
            this.playlistListView.SelectedIndexChanged += new System.EventHandler(this.playlistListView_SelectedIndexChanged);
            this.playlistListView.DoubleClick += new System.EventHandler(this.playlistListView_DoubleClick);
            //
            // nameColumn
            //
            this.nameColumn.Text = "歌单名称";
            this.nameColumn.Width = 300;
            //
            // tracksColumn
            //
            this.tracksColumn.Text = "歌曲数";
            this.tracksColumn.Width = 80;
            //
            // statusColumn
            //
            this.statusColumn.Text = "状态";
            this.statusColumn.Width = 150;
            //
            // createPlaylistButton
            //
            this.createPlaylistButton.Font = new System.Drawing.Font("Microsoft YaHei UI", 10F);
            this.createPlaylistButton.Location = new System.Drawing.Point(16, 365);
            this.createPlaylistButton.Name = "createPlaylistButton";
            this.createPlaylistButton.Size = new System.Drawing.Size(120, 32);
            this.createPlaylistButton.TabIndex = 2;
            this.createPlaylistButton.Text = "新建歌单...";
            this.createPlaylistButton.UseVisualStyleBackColor = true;
            this.createPlaylistButton.Click += new System.EventHandler(this.createPlaylistButton_Click);
            //
            // confirmButton
            //
            this.confirmButton.Enabled = false;
            this.confirmButton.Font = new System.Drawing.Font("Microsoft YaHei UI", 10F);
            this.confirmButton.Location = new System.Drawing.Point(402, 365);
            this.confirmButton.Name = "confirmButton";
            this.confirmButton.Size = new System.Drawing.Size(80, 32);
            this.confirmButton.TabIndex = 3;
            this.confirmButton.Text = "确定";
            this.confirmButton.UseVisualStyleBackColor = true;
            this.confirmButton.Click += new System.EventHandler(this.confirmButton_Click);
            //
            // cancelButton
            //
            this.cancelButton.DialogResult = System.Windows.Forms.DialogResult.Cancel;
            this.cancelButton.Font = new System.Drawing.Font("Microsoft YaHei UI", 10F);
            this.cancelButton.Location = new System.Drawing.Point(488, 365);
            this.cancelButton.Name = "cancelButton";
            this.cancelButton.Size = new System.Drawing.Size(80, 32);
            this.cancelButton.TabIndex = 4;
            this.cancelButton.Text = "取消";
            this.cancelButton.UseVisualStyleBackColor = true;
            this.cancelButton.Click += new System.EventHandler(this.cancelButton_Click);
            //
            // loadingLabel
            //
            this.loadingLabel.AutoSize = true;
            this.loadingLabel.Font = new System.Drawing.Font("Microsoft YaHei UI", 10F);
            this.loadingLabel.ForeColor = System.Drawing.SystemColors.GrayText;
            this.loadingLabel.Location = new System.Drawing.Point(13, 372);
            this.loadingLabel.Name = "loadingLabel";
            this.loadingLabel.Size = new System.Drawing.Size(104, 17);
            this.loadingLabel.TabIndex = 5;
            this.loadingLabel.Text = "正在加载歌单...";
            this.loadingLabel.Visible = false;
            //
            // AddToPlaylistDialog
            //
            this.AcceptButton = this.confirmButton;
            this.AutoScaleDimensions = new System.Drawing.SizeF(96F, 96F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Dpi;
            this.CancelButton = this.cancelButton;
            this.ClientSize = new System.Drawing.Size(584, 411);
            this.Controls.Add(this.loadingLabel);
            this.Controls.Add(this.cancelButton);
            this.Controls.Add(this.confirmButton);
            this.Controls.Add(this.createPlaylistButton);
            this.Controls.Add(this.playlistListView);
            this.Controls.Add(this.titleLabel);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "AddToPlaylistDialog";
            this.ShowIcon = false;
            this.ShowInTaskbar = false;
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            this.Text = "添加到歌单";
            this.Load += new System.EventHandler(this.AddToPlaylistDialog_Load);
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.Label titleLabel;
        private System.Windows.Forms.ListView playlistListView;
        private System.Windows.Forms.ColumnHeader nameColumn;
        private System.Windows.Forms.ColumnHeader tracksColumn;
        private System.Windows.Forms.ColumnHeader statusColumn;
        private System.Windows.Forms.Button createPlaylistButton;
        private System.Windows.Forms.Button confirmButton;
        private System.Windows.Forms.Button cancelButton;
        private System.Windows.Forms.Label loadingLabel;
    }
}
