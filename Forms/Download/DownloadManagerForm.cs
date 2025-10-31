using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using YTPlayer.Core.Download;
using YTPlayer.Models.Download;

namespace YTPlayer.Forms.Download
{
    /// <summary>
    /// 下载管理器窗体
    /// </summary>
    public partial class DownloadManagerForm : Form
    {
        #region 私有字段

        private readonly DownloadManager _downloadManager;
        private readonly System.Windows.Forms.Timer _refreshTimer;
        private DownloadTask? _selectedTask;

        #endregion

        #region 构造函数

        /// <summary>
        /// 构造函数
        /// </summary>
        public DownloadManagerForm()
        {
            InitializeComponent();

            _downloadManager = DownloadManager.Instance;

            // 创建刷新定时器
            _refreshTimer = new System.Windows.Forms.Timer
            {
                Interval = 500  // 500ms 刷新一次
            };
            _refreshTimer.Tick += RefreshTimer_Tick;

            // 订阅下载管理器事件
            _downloadManager.TaskProgressChanged += OnTaskProgressChanged;
            _downloadManager.TaskCompleted += OnTaskCompleted;
            _downloadManager.TaskFailed += OnTaskFailed;
            _downloadManager.TaskCancelled += OnTaskCancelled;
            _downloadManager.QueueStateChanged += OnQueueStateChanged;

            // 初始化列表
            InitializeListViews();
            RefreshLists();

            // 启动定时器
            _refreshTimer.Start();
        }

        #endregion

        #region 初始化

        /// <summary>
        /// 初始化 ListView
        /// </summary>
        private void InitializeListViews()
        {
            // 进行中列表
            lvActive.View = View.Details;
            lvActive.FullRowSelect = true;
            lvActive.GridLines = true;
            lvActive.Columns.Add("歌曲名", 200);
            lvActive.Columns.Add("歌手", 120);
            lvActive.Columns.Add("来源列表", 150);
            lvActive.Columns.Add("进度", 100);
            lvActive.Columns.Add("速度", 100);
            lvActive.Columns.Add("状态", 80);
            lvActive.MouseClick += LvActive_MouseClick;

            // 已完成列表
            lvCompleted.View = View.Details;
            lvCompleted.FullRowSelect = true;
            lvCompleted.GridLines = true;
            lvCompleted.Columns.Add("歌曲名", 200);
            lvCompleted.Columns.Add("歌手", 120);
            lvCompleted.Columns.Add("来源列表", 150);
            lvCompleted.Columns.Add("状态", 80);
            lvCompleted.Columns.Add("完成时间", 150);
            lvCompleted.Columns.Add("文件大小", 100);
            lvCompleted.MouseClick += LvCompleted_MouseClick;
        }

        #endregion

        #region 事件处理 - 下载管理器

        private void OnTaskProgressChanged(DownloadTask task)
        {
            // 在 UI 线程上更新
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnTaskProgressChanged(task)));
                return;
            }

            // 更新列表项
            UpdateTaskInList(task);
        }

        private void OnTaskCompleted(DownloadTask task)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnTaskCompleted(task)));
                return;
            }

            RefreshLists();
        }

        private void OnTaskFailed(DownloadTask task)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnTaskFailed(task)));
                return;
            }

            RefreshLists();
        }

        private void OnTaskCancelled(DownloadTask task)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnTaskCancelled(task)));
                return;
            }

            RefreshLists();
        }

        private void OnQueueStateChanged()
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(OnQueueStateChanged));
                return;
            }

            RefreshLists();
        }

        #endregion

        #region 事件处理 - UI

        private void RefreshTimer_Tick(object? sender, EventArgs e)
        {
            RefreshLists();
        }

        private void TabControl_SelectedIndexChanged(object? sender, EventArgs e)
        {
            // 切换选项卡时刷新
            RefreshLists();
        }

        private void BtnCancelAll_Click(object? sender, EventArgs e)
        {
            var result = MessageBox.Show(
                "确定要取消所有进行中的下载任务吗？\n未完成的文件将被删除。",
                "确认",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (result == DialogResult.Yes)
            {
                _downloadManager.CancelAllActiveTasks();
                RefreshLists();
            }
        }

        private void BtnClearAll_Click(object? sender, EventArgs e)
        {
            var result = MessageBox.Show(
                "确定要清除所有已完成的任务记录吗？",
                "确认",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (result == DialogResult.Yes)
            {
                _downloadManager.ClearCompletedTasks();
                // ✅ 优化：移除多余的 RefreshLists() 调用
                // ClearCompletedTasks 会触发 QueueStateChanged 事件，该事件已经会调用 RefreshLists()
            }
        }

        private void LvActive_MouseClick(object? sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                var item = lvActive.GetItemAt(e.X, e.Y);
                if (item != null && item.Tag is DownloadTask task)
                {
                    _selectedTask = task;
                    ShowActiveContextMenu(e.Location);
                }
            }
        }

        private void LvCompleted_MouseClick(object? sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                var item = lvCompleted.GetItemAt(e.X, e.Y);
                if (item != null && item.Tag is DownloadTask task)
                {
                    _selectedTask = task;
                    ShowCompletedContextMenu(e.Location);
                }
            }
        }

        #endregion

        #region 上下文菜单

        private void ShowActiveContextMenu(Point location)
        {
            var menu = new ContextMenuStrip();

            if (_selectedTask != null)
            {
                // 暂停/继续
                if (_selectedTask.Status == DownloadStatus.Downloading)
                {
                    var pauseItem = new ToolStripMenuItem("暂停");
                    pauseItem.Click += (s, e) =>
                    {
                        if (_selectedTask != null)
                        {
                            _downloadManager.PauseTask(_selectedTask);
                        }
                    };
                    menu.Items.Add(pauseItem);
                }
                else if (_selectedTask.Status == DownloadStatus.Paused || _selectedTask.Status == DownloadStatus.Pending)
                {
                    var resumeItem = new ToolStripMenuItem("继续");
                    resumeItem.Click += (s, e) =>
                    {
                        if (_selectedTask != null)
                        {
                            _downloadManager.ResumeTask(_selectedTask);
                        }
                    };
                    menu.Items.Add(resumeItem);
                }

                // 删除
                var cancelItem = new ToolStripMenuItem("删除");
                cancelItem.Click += (s, e) =>
                {
                    if (_selectedTask != null)
                    {
                        _downloadManager.CancelTask(_selectedTask);
                    }
                };
                menu.Items.Add(cancelItem);

                menu.Items.Add(new ToolStripSeparator());

                // 复制下载链接
                var copyUrlItem = new ToolStripMenuItem("复制下载链接");
                copyUrlItem.Click += (s, e) =>
                {
                    if (_selectedTask != null && !string.IsNullOrEmpty(_selectedTask.DownloadUrl))
                    {
                        try
                        {
                            Clipboard.SetText(_selectedTask.DownloadUrl);
                            MessageBox.Show("下载链接已复制到剪贴板", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                        catch
                        {
                            MessageBox.Show("复制失败", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    }
                };
                menu.Items.Add(copyUrlItem);
            }

            menu.Show(lvActive, location);
        }

        private void ShowCompletedContextMenu(Point location)
        {
            var menu = new ContextMenuStrip();

            if (_selectedTask != null)
            {
                // 清除
                var removeItem = new ToolStripMenuItem("清除");
                removeItem.Click += (s, e) =>
                {
                    if (_selectedTask != null)
                    {
                        _downloadManager.RemoveCompletedTask(_selectedTask);
                        // ✅ 优化：移除多余的 RefreshLists() 调用
                        // RemoveCompletedTask 会触发 QueueStateChanged 事件，该事件已经会调用 RefreshLists()
                    }
                };
                menu.Items.Add(removeItem);

                menu.Items.Add(new ToolStripSeparator());

                // 复制下载链接
                var copyUrlItem = new ToolStripMenuItem("复制下载链接");
                copyUrlItem.Click += (s, e) =>
                {
                    if (_selectedTask != null && !string.IsNullOrEmpty(_selectedTask.DownloadUrl))
                    {
                        try
                        {
                            Clipboard.SetText(_selectedTask.DownloadUrl);
                            MessageBox.Show("下载链接已复制到剪贴板", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                        catch
                        {
                            MessageBox.Show("复制失败", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    }
                };
                menu.Items.Add(copyUrlItem);
            }

            menu.Show(lvCompleted, location);
        }

        #endregion

        #region 列表刷新

        /// <summary>
        /// 刷新列表
        /// </summary>
        private void RefreshLists()
        {
            if (tabControl.SelectedIndex == 0)
            {
                RefreshActiveList();
            }
            else
            {
                RefreshCompletedList();
            }
        }

        /// <summary>
        /// 刷新进行中列表
        /// </summary>
        private void RefreshActiveList()
        {
            lvActive.BeginUpdate();

            try
            {
                var allActiveTasks = _downloadManager.GetAllActiveTasks();

                // 移除不存在的项
                var itemsToRemove = new List<ListViewItem>();
                foreach (ListViewItem item in lvActive.Items)
                {
                    if (item.Tag is DownloadTask task && !allActiveTasks.Contains(task))
                    {
                        itemsToRemove.Add(item);
                    }
                }
                foreach (var item in itemsToRemove)
                {
                    lvActive.Items.Remove(item);
                }

                // 更新或添加项
                foreach (var task in allActiveTasks)
                {
                    UpdateTaskInList(task);
                }
            }
            finally
            {
                lvActive.EndUpdate();
            }
        }

        /// <summary>
        /// 刷新已完成列表（使用增量更新策略，保护屏幕阅读器焦点）
        /// </summary>
        private void RefreshCompletedList()
        {
            lvCompleted.BeginUpdate();

            try
            {
                var completedTasks = _downloadManager.GetCompletedTasks();

                // ✅ 修复：使用增量更新策略，不再全部清空
                // 移除不存在的项
                var itemsToRemove = new List<ListViewItem>();
                foreach (ListViewItem item in lvCompleted.Items)
                {
                    if (item.Tag is DownloadTask task && !completedTasks.Contains(task))
                    {
                        itemsToRemove.Add(item);
                    }
                }
                foreach (var item in itemsToRemove)
                {
                    lvCompleted.Items.Remove(item);
                }

                // 更新或添加项
                foreach (var task in completedTasks)
                {
                    UpdateCompletedTaskInList(task);
                }
            }
            finally
            {
                lvCompleted.EndUpdate();
            }
        }

        /// <summary>
        /// 更新任务在进行中列表
        /// </summary>
        private void UpdateTaskInList(DownloadTask task)
        {
            // 查找现有项
            ListViewItem? existingItem = null;
            foreach (ListViewItem item in lvActive.Items)
            {
                if (item.Tag == task)
                {
                    existingItem = item;
                    break;
                }
            }

            // 如果不存在，创建新项
            if (existingItem == null)
            {
                existingItem = new ListViewItem(new[]
                {
                    task.Song.Name,
                    task.Song.Artist,
                    task.SourceList,
                    $"{task.ProgressPercentage:F1}%",
                    task.FormattedSpeed,
                    GetStatusText(task.Status)
                })
                {
                    Tag = task
                };
                lvActive.Items.Add(existingItem);
            }
            else
            {
                // 更新现有项
                existingItem.SubItems[0].Text = task.Song.Name;
                existingItem.SubItems[1].Text = task.Song.Artist;
                existingItem.SubItems[2].Text = task.SourceList;
                existingItem.SubItems[3].Text = $"{task.ProgressPercentage:F1}%";
                existingItem.SubItems[4].Text = task.FormattedSpeed;
                existingItem.SubItems[5].Text = GetStatusText(task.Status);
            }
        }

        /// <summary>
        /// 添加已完成任务到列表
        /// </summary>
        private void AddCompletedTaskToList(DownloadTask task)
        {
            string completedTime = task.CompletedTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "";
            string fileSize = task.TotalBytes > 0
                ? DownloadFileHelper.FormatFileSize(task.TotalBytes)
                : "";

            var item = new ListViewItem(new[]
            {
                task.Song.Name,
                task.Song.Artist,
                task.SourceList,
                GetStatusText(task.Status),
                completedTime,
                fileSize
            })
            {
                Tag = task
            };

            // 根据状态设置颜色
            if (task.Status == DownloadStatus.Failed)
            {
                item.ForeColor = Color.Red;
            }
            else if (task.Status == DownloadStatus.Completed)
            {
                item.ForeColor = Color.Green;
            }

            lvCompleted.Items.Add(item);
        }

        /// <summary>
        /// 更新已完成任务在列表中（增量更新，保护焦点）
        /// </summary>
        private void UpdateCompletedTaskInList(DownloadTask task)
        {
            // 查找现有项
            ListViewItem? existingItem = null;
            foreach (ListViewItem item in lvCompleted.Items)
            {
                if (item.Tag == task)
                {
                    existingItem = item;
                    break;
                }
            }

            string completedTime = task.CompletedTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "";
            string fileSize = task.TotalBytes > 0
                ? DownloadFileHelper.FormatFileSize(task.TotalBytes)
                : "";

            // 如果不存在，创建新项
            if (existingItem == null)
            {
                existingItem = new ListViewItem(new[]
                {
                    task.Song.Name,
                    task.Song.Artist,
                    task.SourceList,
                    GetStatusText(task.Status),
                    completedTime,
                    fileSize
                })
                {
                    Tag = task
                };

                // 根据状态设置颜色
                if (task.Status == DownloadStatus.Failed)
                {
                    existingItem.ForeColor = Color.Red;
                }
                else if (task.Status == DownloadStatus.Completed)
                {
                    existingItem.ForeColor = Color.Green;
                }

                lvCompleted.Items.Add(existingItem);
            }
            else
            {
                // 更新现有项（保留 ListViewItem 对象，只更新内容）
                existingItem.SubItems[0].Text = task.Song.Name;
                existingItem.SubItems[1].Text = task.Song.Artist;
                existingItem.SubItems[2].Text = task.SourceList;
                existingItem.SubItems[3].Text = GetStatusText(task.Status);
                existingItem.SubItems[4].Text = completedTime;
                existingItem.SubItems[5].Text = fileSize;

                // 根据状态更新颜色
                if (task.Status == DownloadStatus.Failed)
                {
                    existingItem.ForeColor = Color.Red;
                }
                else if (task.Status == DownloadStatus.Completed)
                {
                    existingItem.ForeColor = Color.Green;
                }
            }
        }

        #endregion

        #region 辅助方法

        /// <summary>
        /// 获取状态文本
        /// </summary>
        private string GetStatusText(DownloadStatus status)
        {
            switch (status)
            {
                case DownloadStatus.Pending:
                    return "等待中";
                case DownloadStatus.Downloading:
                    return "下载中";
                case DownloadStatus.Paused:
                    return "已暂停";
                case DownloadStatus.Completed:
                    return "已完成";
                case DownloadStatus.Failed:
                    return "失败";
                case DownloadStatus.Cancelled:
                    return "已取消";
                default:
                    return "未知";
            }
        }

        #endregion

        #region 窗体关闭

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // 停止定时器
            _refreshTimer?.Stop();

            // 取消订阅事件
            _downloadManager.TaskProgressChanged -= OnTaskProgressChanged;
            _downloadManager.TaskCompleted -= OnTaskCompleted;
            _downloadManager.TaskFailed -= OnTaskFailed;
            _downloadManager.TaskCancelled -= OnTaskCancelled;
            _downloadManager.QueueStateChanged -= OnQueueStateChanged;

            base.OnFormClosing(e);
        }

        #endregion
    }
}
