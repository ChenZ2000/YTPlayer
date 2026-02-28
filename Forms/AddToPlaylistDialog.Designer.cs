using System.Windows.Forms;

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
            this.selectionInfoLabel = new System.Windows.Forms.Label();
            this.playlistCheckedListBox = new System.Windows.Forms.CheckedListBox();
            this.btnSelectAll = new System.Windows.Forms.Button();
            this.btnUnselectAll = new System.Windows.Forms.Button();
            this.btnInvertSelection = new System.Windows.Forms.Button();
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
            this.titleLabel.Location = new System.Drawing.Point(12, 12);
            this.titleLabel.Name = "titleLabel";
            this.titleLabel.Size = new System.Drawing.Size(152, 19);
            this.titleLabel.TabIndex = 0;
            this.titleLabel.Text = "选择要添加到的歌单（可多选）";
            //
            // selectionInfoLabel
            //
            this.selectionInfoLabel.AutoSize = true;
            this.selectionInfoLabel.Location = new System.Drawing.Point(12, 38);
            this.selectionInfoLabel.Name = "selectionInfoLabel";
            this.selectionInfoLabel.Size = new System.Drawing.Size(88, 19);
            this.selectionInfoLabel.TabIndex = 1;
            this.selectionInfoLabel.Text = "已选择 0/0 项";
            //
            // playlistCheckedListBox
            //
            this.playlistCheckedListBox.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom)
            | System.Windows.Forms.AnchorStyles.Left)
            | System.Windows.Forms.AnchorStyles.Right)));
            this.playlistCheckedListBox.CheckOnClick = true;
            this.playlistCheckedListBox.FormattingEnabled = true;
            this.playlistCheckedListBox.Location = new System.Drawing.Point(16, 64);
            this.playlistCheckedListBox.Name = "playlistCheckedListBox";
            this.playlistCheckedListBox.Size = new System.Drawing.Size(552, 256);
            this.playlistCheckedListBox.TabIndex = 2;
            this.playlistCheckedListBox.ItemCheck += new System.Windows.Forms.ItemCheckEventHandler(this.playlistCheckedListBox_ItemCheck);
            //
            // btnSelectAll
            //
            this.btnSelectAll.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left)));
            this.btnSelectAll.Location = new System.Drawing.Point(16, 330);
            this.btnSelectAll.Name = "btnSelectAll";
            this.btnSelectAll.Size = new System.Drawing.Size(80, 30);
            this.btnSelectAll.TabIndex = 3;
            this.btnSelectAll.Text = "全选";
            this.btnSelectAll.UseVisualStyleBackColor = true;
            this.btnSelectAll.Click += new System.EventHandler(this.btnSelectAll_Click);
            //
            // btnUnselectAll
            //
            this.btnUnselectAll.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left)));
            this.btnUnselectAll.Location = new System.Drawing.Point(102, 330);
            this.btnUnselectAll.Name = "btnUnselectAll";
            this.btnUnselectAll.Size = new System.Drawing.Size(80, 30);
            this.btnUnselectAll.TabIndex = 4;
            this.btnUnselectAll.Text = "全不选";
            this.btnUnselectAll.UseVisualStyleBackColor = true;
            this.btnUnselectAll.Click += new System.EventHandler(this.btnUnselectAll_Click);
            //
            // btnInvertSelection
            //
            this.btnInvertSelection.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left)));
            this.btnInvertSelection.Location = new System.Drawing.Point(188, 330);
            this.btnInvertSelection.Name = "btnInvertSelection";
            this.btnInvertSelection.Size = new System.Drawing.Size(80, 30);
            this.btnInvertSelection.TabIndex = 5;
            this.btnInvertSelection.Text = "反选";
            this.btnInvertSelection.UseVisualStyleBackColor = true;
            this.btnInvertSelection.Click += new System.EventHandler(this.btnInvertSelection_Click);
            //
            // createPlaylistButton
            //
            this.createPlaylistButton.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left)));
            this.createPlaylistButton.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
            this.createPlaylistButton.Location = new System.Drawing.Point(16, 368);
            this.createPlaylistButton.Name = "createPlaylistButton";
            this.createPlaylistButton.Size = new System.Drawing.Size(120, 32);
            this.createPlaylistButton.TabIndex = 6;
            this.createPlaylistButton.Text = "新建歌单...";
            this.createPlaylistButton.UseVisualStyleBackColor = true;
            this.createPlaylistButton.Click += new System.EventHandler(this.createPlaylistButton_Click);
            //
            // confirmButton
            //
            this.confirmButton.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.confirmButton.Enabled = false;
            this.confirmButton.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
            this.confirmButton.Location = new System.Drawing.Point(402, 368);
            this.confirmButton.Name = "confirmButton";
            this.confirmButton.Size = new System.Drawing.Size(80, 32);
            this.confirmButton.TabIndex = 7;
            this.confirmButton.Text = "确定";
            this.confirmButton.UseVisualStyleBackColor = true;
            this.confirmButton.Click += new System.EventHandler(this.confirmButton_Click);
            //
            // cancelButton
            //
            this.cancelButton.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.cancelButton.DialogResult = System.Windows.Forms.DialogResult.Cancel;
            this.cancelButton.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
            this.cancelButton.Location = new System.Drawing.Point(488, 368);
            this.cancelButton.Name = "cancelButton";
            this.cancelButton.Size = new System.Drawing.Size(80, 32);
            this.cancelButton.TabIndex = 8;
            this.cancelButton.Text = "取消";
            this.cancelButton.UseVisualStyleBackColor = true;
            this.cancelButton.Click += new System.EventHandler(this.cancelButton_Click);
            //
            // loadingLabel
            //
            this.loadingLabel.AutoSize = true;
            this.loadingLabel.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
            this.loadingLabel.ForeColor = System.Drawing.SystemColors.GrayText;
            this.loadingLabel.Location = new System.Drawing.Point(142, 376);
            this.loadingLabel.Name = "loadingLabel";
            this.loadingLabel.Size = new System.Drawing.Size(101, 17);
            this.loadingLabel.TabIndex = 9;
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
            this.Controls.Add(this.btnInvertSelection);
            this.Controls.Add(this.btnUnselectAll);
            this.Controls.Add(this.btnSelectAll);
            this.Controls.Add(this.playlistCheckedListBox);
            this.Controls.Add(this.selectionInfoLabel);
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
        private System.Windows.Forms.Label selectionInfoLabel;
        private System.Windows.Forms.CheckedListBox playlistCheckedListBox;
        private System.Windows.Forms.Button btnSelectAll;
        private System.Windows.Forms.Button btnUnselectAll;
        private System.Windows.Forms.Button btnInvertSelection;
        private System.Windows.Forms.Button createPlaylistButton;
        private System.Windows.Forms.Button confirmButton;
        private System.Windows.Forms.Button cancelButton;
        private System.Windows.Forms.Label loadingLabel;
    }
}
