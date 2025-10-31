using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using YTPlayer.Models.Download;

namespace YTPlayer.Forms.Download
{
    /// <summary>
    /// 文件冲突对话框
    /// </summary>
    public partial class DownloadConflictDialog : Form
    {
        #region 属性

        /// <summary>
        /// 获取用户选择的冲突解决策略
        /// </summary>
        public ConflictResolution Resolution { get; private set; }

        #endregion

        #region 私有字段

        private readonly List<string> _conflictPaths;
        private readonly bool _hasMultipleConflicts;

        #endregion

        #region 构造函数

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="conflictPaths">冲突文件路径列表</param>
        public DownloadConflictDialog(List<string> conflictPaths)
        {
            InitializeComponent();

            _conflictPaths = conflictPaths ?? new List<string>();
            _hasMultipleConflicts = _conflictPaths.Count > 1;

            Resolution = ConflictResolution.Cancel;

            // 更新标签
            lblInfo.Text = $"发现 {_conflictPaths.Count} 个文件/目录冲突：";

            // 添加冲突路径到列表
            foreach (var path in _conflictPaths)
            {
                var fileName = System.IO.Path.GetFileName(path);
                listView.Items.Add(fileName);
            }

            // 根据冲突数量显示/隐藏 "全部" 按钮
            if (_hasMultipleConflicts)
            {
                btnSkipAll.Visible = true;
                btnOverwriteAll.Visible = true;
            }
            else
            {
                btnSkipAll.Visible = false;
                btnOverwriteAll.Visible = false;
                lblInfo.Text = "发现文件/目录冲突：";
            }
        }

        #endregion

        #region 事件处理

        private void BtnSkip_Click(object? sender, EventArgs e)
        {
            Resolution = ConflictResolution.Skip;
            DialogResult = DialogResult.OK;
            Close();
        }

        private void BtnOverwrite_Click(object? sender, EventArgs e)
        {
            Resolution = ConflictResolution.Overwrite;
            DialogResult = DialogResult.OK;
            Close();
        }

        private void BtnSkipAll_Click(object? sender, EventArgs e)
        {
            Resolution = ConflictResolution.SkipAll;
            DialogResult = DialogResult.OK;
            Close();
        }

        private void BtnOverwriteAll_Click(object? sender, EventArgs e)
        {
            Resolution = ConflictResolution.OverwriteAll;
            DialogResult = DialogResult.OK;
            Close();
        }

        private void BtnCancel_Click(object? sender, EventArgs e)
        {
            Resolution = ConflictResolution.Cancel;
            DialogResult = DialogResult.Cancel;
            Close();
        }

        #endregion
    }
}
