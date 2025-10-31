using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace YTPlayer.Forms.Download
{
    /// <summary>
    /// 批量下载选择对话框
    /// </summary>
    public partial class BatchDownloadDialog : Form
    {
        #region 属性

        /// <summary>
        /// 获取选中的项目（通过索引列表）
        /// </summary>
        public List<int> SelectedIndices { get; private set; }

        #endregion

        #region 构造函数

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="items">项目列表（显示文本）</param>
        /// <param name="title">对话框标题</param>
        public BatchDownloadDialog(List<string> items, string title = "批量下载")
        {
            InitializeComponent();

            SelectedIndices = new List<int>();

            Text = title;

            // 添加项目到 CheckedListBox
            foreach (var item in items)
            {
                checkedListBox.Items.Add(item, true);  // 默认全选
            }

            // 更新标签
            UpdateLabel();
        }

        #endregion

        #region 事件处理

        private void BtnSelectAll_Click(object? sender, EventArgs e)
        {
            for (int i = 0; i < checkedListBox.Items.Count; i++)
            {
                checkedListBox.SetItemChecked(i, true);
            }
            UpdateLabel();
        }

        private void BtnUnselectAll_Click(object? sender, EventArgs e)
        {
            for (int i = 0; i < checkedListBox.Items.Count; i++)
            {
                checkedListBox.SetItemChecked(i, false);
            }
            UpdateLabel();
        }

        private void BtnInvertSelection_Click(object? sender, EventArgs e)
        {
            for (int i = 0; i < checkedListBox.Items.Count; i++)
            {
                checkedListBox.SetItemChecked(i, !checkedListBox.GetItemChecked(i));
            }
            UpdateLabel();
        }

        private void BtnOk_Click(object? sender, EventArgs e)
        {
            // 收集选中的索引
            SelectedIndices.Clear();
            for (int i = 0; i < checkedListBox.Items.Count; i++)
            {
                if (checkedListBox.GetItemChecked(i))
                {
                    SelectedIndices.Add(i);
                }
            }

            if (SelectedIndices.Count == 0)
            {
                MessageBox.Show("请至少选择一项", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            DialogResult = DialogResult.OK;
            Close();
        }

        private void BtnCancel_Click(object? sender, EventArgs e)
        {
            DialogResult = DialogResult.Cancel;
            Close();
        }

        private void CheckedListBox_ItemCheck(object? sender, ItemCheckEventArgs e)
        {
            // ItemCheck 事件在状态改变之前触发，需要延迟更新
            // 确保窗口句柄已创建后再调用 BeginInvoke
            if (IsHandleCreated)
            {
                BeginInvoke(new Action(UpdateLabel));
            }
        }

        #endregion

        #region 辅助方法

        /// <summary>
        /// 更新标签显示
        /// </summary>
        private void UpdateLabel()
        {
            int checkedCount = checkedListBox.CheckedItems.Count;
            int totalCount = checkedListBox.Items.Count;
            lblInfo.Text = $"已选择 {checkedCount}/{totalCount} 项";
        }

        #endregion
    }
}
