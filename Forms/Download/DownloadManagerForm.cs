using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using MessageBox = YTPlayer.MessageBox;
using YTPlayer.Core.Download;
using YTPlayer.Core.Upload;
using YTPlayer.Models.Download;
using YTPlayer.Models.Upload;
using YTPlayer.Utils;

namespace YTPlayer.Forms.Download
{
    /// <summary>
    /// 传输管理器窗体（下载+上传）
    /// </summary>
    public partial class DownloadManagerForm : Form
    {
        #region 私有字段

        private readonly DownloadManager _downloadManager;
        private readonly UploadManager _uploadManager;
        private readonly System.Windows.Forms.Timer _refreshTimer;
        private DownloadTask? _selectedDownloadTask;
        private UploadTask? _selectedUploadTask;
        private static int _lastSelectedTabIndex = 0;
        private static bool _hasLastTabSelection;

        private sealed class ContextMenuState
        {
            public Control Owner { get; }
            public string Name { get; }

            public ContextMenuState(Control owner, string name)
            {
                Owner = owner;
                Name = name;
            }
        }

        #endregion

        #region 构造函数

        /// <summary>
        /// 构造函数
        /// </summary>
        public DownloadManagerForm()
        {
            InitializeComponent();
            ThemeManager.ApplyTheme(this);
            this.KeyPreview = true;
            this.KeyDown += DownloadManagerForm_KeyDown;

            _downloadManager = DownloadManager.Instance;
            _uploadManager = UploadManager.Instance;

            // 创建刷新定时器
            _refreshTimer = new System.Windows.Forms.Timer
            {
                Interval = 500  // 500ms 刷新一次
            };
            _refreshTimer.Tick += RefreshTimer_Tick;

            // 订阅下载管理器事件
            _downloadManager.TaskProgressChanged += OnDownloadTaskProgressChanged;
            _downloadManager.TaskCompleted += OnDownloadTaskCompleted;
            _downloadManager.TaskFailed += OnDownloadTaskFailed;
            _downloadManager.TaskCancelled += OnDownloadTaskCancelled;
            _downloadManager.QueueStateChanged += OnDownloadQueueStateChanged;

            // 订阅上传管理器事件
            _uploadManager.TaskProgressChanged += OnUploadTaskProgressChanged;
            _uploadManager.TaskCompleted += OnUploadTaskCompleted;
            _uploadManager.TaskFailed += OnUploadTaskFailed;
            _uploadManager.TaskCancelled += OnUploadTaskCancelled;
            _uploadManager.QueueStateChanged += OnUploadQueueStateChanged;

            // 初始化列表
            InitializeListViews();
            ApplyInitialTabSelection();
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
            // 下载中列表
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

            // 上传中列表
            lvUpload.View = View.Details;
            lvUpload.FullRowSelect = true;
            lvUpload.GridLines = true;
            lvUpload.Columns.Add("文件名", 250);
            lvUpload.Columns.Add("来源", 150);
            lvUpload.Columns.Add("进度", 100);
            lvUpload.Columns.Add("阶段", 150);
            lvUpload.Columns.Add("状态", 80);
            lvUpload.MouseClick += LvUpload_MouseClick;

            // 已完成列表
            lvCompleted.View = View.Details;
            lvCompleted.FullRowSelect = true;
            lvCompleted.GridLines = true;
            lvCompleted.Columns.Add("名称", 200);
            lvCompleted.Columns.Add("类型", 80);
            lvCompleted.Columns.Add("来源列表", 150);
            lvCompleted.Columns.Add("状态", 80);
            lvCompleted.Columns.Add("完成时间", 150);
            lvCompleted.Columns.Add("文件大小", 100);
            lvCompleted.MouseClick += LvCompleted_MouseClick;
        }

        private void ApplyInitialTabSelection()
        {
            int activeDownloads = _downloadManager.GetActiveTasks().Count;
            int activeUploads = _uploadManager.GetAllActiveTasks().Count;
            bool hasCompleted = _downloadManager.GetCompletedTasks().Any() ||
                                _uploadManager.GetCompletedTasks().Any();

            if (activeDownloads > 0)
            {
                tabControl.SelectedTab = tabPageActive;
            }
            else if (activeUploads > 0)
            {
                tabControl.SelectedTab = tabPageUpload;
            }
            else if (hasCompleted)
            {
                tabControl.SelectedTab = tabPageCompleted;
            }
            else if (_hasLastTabSelection && _lastSelectedTabIndex >= 0 && _lastSelectedTabIndex < tabControl.TabCount)
            {
                tabControl.SelectedIndex = _lastSelectedTabIndex;
            }
            else
            {
                tabControl.SelectedIndex = 0;
            }
        }

        #endregion

        #region 事件处理 - 下载管理器

        private void OnDownloadTaskProgressChanged(DownloadTask task)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnDownloadTaskProgressChanged(task)));
                return;
            }
            UpdateDownloadTaskInList(task);
        }

        private void OnDownloadTaskCompleted(DownloadTask task)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnDownloadTaskCompleted(task)));
                return;
            }
            RefreshLists();
        }

        private void OnDownloadTaskFailed(DownloadTask task)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnDownloadTaskFailed(task)));
                return;
            }
            RefreshLists();
        }

        private void OnDownloadTaskCancelled(DownloadTask task)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnDownloadTaskCancelled(task)));
                return;
            }
            RefreshLists();
        }

        private void OnDownloadQueueStateChanged()
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(OnDownloadQueueStateChanged));
                return;
            }
            RefreshLists();
        }

        #endregion

        #region 事件处理 - 上传管理器

        private void OnUploadTaskProgressChanged(UploadTask task)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnUploadTaskProgressChanged(task)));
                return;
            }
            UpdateUploadTaskInList(task);
        }

        private void OnUploadTaskCompleted(UploadTask task)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnUploadTaskCompleted(task)));
                return;
            }
            RefreshLists();
        }

        private void OnUploadTaskFailed(UploadTask task)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnUploadTaskFailed(task)));
                return;
            }
            RefreshLists();
        }

        private void OnUploadTaskCancelled(UploadTask task)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnUploadTaskCancelled(task)));
                return;
            }
            RefreshLists();
        }

        private void OnUploadQueueStateChanged()
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(OnUploadQueueStateChanged));
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

        private void DownloadManagerForm_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                Close();
            }
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

        private void BtnCancelAllUpload_Click(object? sender, EventArgs e)
        {
            var result = MessageBox.Show(
                "确定要取消所有进行中的上传任务吗？",
                "确认",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (result == DialogResult.Yes)
            {
                _uploadManager.CancelAllActiveTasks();
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
                    _selectedDownloadTask = task;
                    ShowDownloadContextMenu(e.Location);
                }
            }
        }

        private void LvUpload_MouseClick(object? sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                var item = lvUpload.GetItemAt(e.X, e.Y);
                if (item != null && item.Tag is UploadTask task)
                {
                    _selectedUploadTask = task;
                    ShowUploadContextMenu(e.Location);
                }
            }
        }

        private void LvCompleted_MouseClick(object? sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                var item = lvCompleted.GetItemAt(e.X, e.Y);
                if (item != null)
                {
                    if (item.Tag is DownloadTask downloadTask)
                    {
                        _selectedDownloadTask = downloadTask;
                        ShowCompletedContextMenu(e.Location, true);
                    }
                    else if (item.Tag is UploadTask uploadTask)
                    {
                        _selectedUploadTask = uploadTask;
                        ShowCompletedContextMenu(e.Location, false);
                    }
                }
            }
        }

        #endregion

        #region 上下文菜单

        private void ShowAccessibleContextMenu(ContextMenuStrip menu, Control owner, Point location, string menuName)
        {
            if (menu.Items.Count == 0)
            {
                menu.Dispose();
                return;
            }

            menu.Tag = new ContextMenuState(owner, menuName);
            menu.Opened += TemporaryContextMenu_Opened;
            menu.Closed += TemporaryContextMenu_Closed;
            ContextMenuAccessibilityHelper.PrimeForAccessibility(menu);
            menu.Show(owner, location);
        }

        private void TemporaryContextMenu_Opened(object? sender, EventArgs e)
        {
            if (sender is not ContextMenuStrip menu || IsDisposed)
            {
                return;
            }

            string menuName = (menu.Tag as ContextMenuState)?.Name ?? "ContextMenu";
            ContextMenuAccessibilityHelper.EnsureFirstItemFocusedOnOpen(this, menu, menuName, message => Debug.WriteLine(message));
        }

        private void TemporaryContextMenu_Closed(object? sender, ToolStripDropDownClosedEventArgs e)
        {
            if (sender is not ContextMenuStrip menu)
            {
                return;
            }

            menu.Opened -= TemporaryContextMenu_Opened;
            menu.Closed -= TemporaryContextMenu_Closed;

            if (menu.Tag is ContextMenuState state &&
                e.CloseReason != ToolStripDropDownCloseReason.ItemClicked &&
                !state.Owner.IsDisposed &&
                state.Owner.CanFocus)
            {
                try
                {
                    state.Owner.BeginInvoke(new Action(() =>
                    {
                        if (!state.Owner.IsDisposed && state.Owner.CanFocus)
                        {
                            state.Owner.Focus();
                        }
                    }));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[{state.Name}] 恢复焦点失败: {ex.Message}");
                }
            }

            menu.Tag = null;
            menu.Dispose();
        }

        private void ShowDownloadContextMenu(Point location)
        {
            var menu = new ContextMenuStrip();

            if (_selectedDownloadTask != null)
            {
                // 暂停/继续
                if (_selectedDownloadTask.Status == DownloadStatus.Downloading)
                {
                    var pauseItem = new ToolStripMenuItem("暂停");
                    pauseItem.Click += (s, e) =>
                    {
                        if (_selectedDownloadTask != null)
                        {
                            _downloadManager.PauseTask(_selectedDownloadTask);
                        }
                    };
                    menu.Items.Add(pauseItem);
                }
                else if (_selectedDownloadTask.Status == DownloadStatus.Paused || _selectedDownloadTask.Status == DownloadStatus.Pending)
                {
                    var resumeItem = new ToolStripMenuItem("继续");
                    resumeItem.Click += (s, e) =>
                    {
                        if (_selectedDownloadTask != null)
                        {
                            _downloadManager.ResumeTask(_selectedDownloadTask);
                        }
                    };
                    menu.Items.Add(resumeItem);
                }

                // 删除
                var cancelItem = new ToolStripMenuItem("删除");
                cancelItem.Click += (s, e) =>
                {
                    if (_selectedDownloadTask != null)
                    {
                        _downloadManager.CancelTask(_selectedDownloadTask);
                    }
                };
                menu.Items.Add(cancelItem);

                menu.Items.Add(new ToolStripSeparator());

                // 复制下载链接
                var copyUrlItem = new ToolStripMenuItem("复制下载链接");
                copyUrlItem.Click += (s, e) =>
                {
                    if (_selectedDownloadTask != null && !string.IsNullOrEmpty(_selectedDownloadTask.DownloadUrl))
                    {
                        try
                        {
                            Clipboard.SetText(_selectedDownloadTask.DownloadUrl);
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

            ShowAccessibleContextMenu(menu, lvActive, location, "DownloadActiveContextMenu");
        }

        private void ShowCompletedContextMenu(Point location, bool isDownload)
        {
            var menu = new ContextMenuStrip();

            if (isDownload && _selectedDownloadTask != null)
            {
                // 清除
                var removeItem = new ToolStripMenuItem("清除");
                removeItem.Click += (s, e) =>
                {
                    if (_selectedDownloadTask != null)
                    {
                        _downloadManager.RemoveCompletedTask(_selectedDownloadTask);
                    }
                };
                menu.Items.Add(removeItem);

                menu.Items.Add(new ToolStripSeparator());

                // 复制下载链接
                var copyUrlItem = new ToolStripMenuItem("复制下载链接");
                copyUrlItem.Click += (s, e) =>
                {
                    if (_selectedDownloadTask != null && !string.IsNullOrEmpty(_selectedDownloadTask.DownloadUrl))
                    {
                        try
                        {
                            Clipboard.SetText(_selectedDownloadTask.DownloadUrl);
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
            else if (!isDownload && _selectedUploadTask != null)
            {
                // 清除
                var removeItem = new ToolStripMenuItem("清除");
                removeItem.Click += (s, e) =>
                {
                    if (_selectedUploadTask != null)
                    {
                        _uploadManager.RemoveCompletedTask(_selectedUploadTask);
                    }
                };
                menu.Items.Add(removeItem);
            }

            ShowAccessibleContextMenu(menu, lvCompleted, location, isDownload ? "DownloadCompletedContextMenu" : "UploadCompletedContextMenu");
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
            else if (tabControl.SelectedIndex == 1)
            {
                RefreshUploadList();
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
                    UpdateDownloadTaskInList(task);
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
                var completedDownloadTasks = _downloadManager.GetCompletedTasks();
                var completedUploadTasks = _uploadManager.GetCompletedTasks();
                var allTasks = completedDownloadTasks.Cast<object>()
                    .Concat(completedUploadTasks.Cast<object>())
                    .ToList();

                // ✅ 修复：使用增量更新策略，不再全部清空
                // 移除不存在的项
                var itemsToRemove = new List<ListViewItem>();
                foreach (ListViewItem item in lvCompleted.Items)
                {
                    if (item.Tag == null || !allTasks.Contains(item.Tag))
                    {
                        itemsToRemove.Add(item);
                    }
                }
                foreach (var item in itemsToRemove)
                {
                    lvCompleted.Items.Remove(item);
                }

                // 更新或添加项
                foreach (var task in completedDownloadTasks)
                {
                    UpdateCompletedItem(task);
                }

                foreach (var task in completedUploadTasks)
                {
                    UpdateCompletedItem(task);
                }
            }
            finally
            {
                lvCompleted.EndUpdate();
            }
        }

        /// <summary>
        /// 更新下载任务在进行中列表
        /// </summary>
        private void UpdateDownloadTaskInList(DownloadTask task)
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
            string title = FormatTaskTitle(task);

            if (existingItem == null)
            {
                existingItem = new ListViewItem(new[]
                {
                    title,
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
                existingItem.SubItems[0].Text = title;
                existingItem.SubItems[1].Text = task.Song.Artist;
                existingItem.SubItems[2].Text = task.SourceList;
                existingItem.SubItems[3].Text = $"{task.ProgressPercentage:F1}%";
                existingItem.SubItems[4].Text = task.FormattedSpeed;
                existingItem.SubItems[5].Text = GetStatusText(task.Status);
            }
        }

        private void UpdateCompletedItem(object task)
        {
            if (task == null)
            {
                return;
            }

            ListViewItem? existingItem = null;
            foreach (ListViewItem item in lvCompleted.Items)
            {
                if (item.Tag == task)
                {
                    existingItem = item;
                    break;
                }
            }

            if (existingItem == null)
            {
                existingItem = new ListViewItem(new[]
                {
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty
                })
                {
                    Tag = task
                };
                lvCompleted.Items.Add(existingItem);
            }

            if (task is DownloadTask downloadTask)
            {
                string completedTime = downloadTask.CompletedTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "";
                string fileSize = downloadTask.TotalBytes > 0
                    ? DownloadFileHelper.FormatFileSize(downloadTask.TotalBytes)
                    : "";

                existingItem.SubItems[0].Text = FormatTaskTitle(downloadTask);
                existingItem.SubItems[1].Text = downloadTask.ContentType == DownloadContentType.Lyrics ? "歌词" : "下载";
                existingItem.SubItems[2].Text = downloadTask.SourceList;
                existingItem.SubItems[3].Text = GetStatusText(downloadTask.Status);
                existingItem.SubItems[4].Text = completedTime;
                existingItem.SubItems[5].Text = fileSize;

                if (downloadTask.Status == DownloadStatus.Failed)
                {
                    existingItem.ForeColor = ThemeManager.Current.TextPrimary;
                }
                else if (downloadTask.Status == DownloadStatus.Completed)
                {
                    existingItem.ForeColor = ThemeManager.Current.TextPrimary;
                }
                else
                {
                    existingItem.ForeColor = SystemColors.ControlText;
                }
            }
            else if (task is UploadTask uploadTask)
            {
                string completedTime = uploadTask.CompletedTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "";
                string fileSize = uploadTask.TotalBytes > 0
                    ? uploadTask.FormattedFileSize
                    : "";

                existingItem.SubItems[0].Text = uploadTask.FileName;
                existingItem.SubItems[1].Text = "上传";
                existingItem.SubItems[2].Text = uploadTask.SourceList;
                existingItem.SubItems[3].Text = GetUploadStatusText(uploadTask.Status);
                existingItem.SubItems[4].Text = completedTime;
                existingItem.SubItems[5].Text = fileSize;

                if (uploadTask.Status == UploadStatus.Failed)
                {
                    existingItem.ForeColor = ThemeManager.Current.TextPrimary;
                }
                else if (uploadTask.Status == UploadStatus.Completed)
                {
                    existingItem.ForeColor = ThemeManager.Current.TextPrimary;
                }
                else
                {
                    existingItem.ForeColor = SystemColors.ControlText;
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

        private static string FormatTaskTitle(DownloadTask task)
        {
            if (task == null || task.Song == null)
            {
                return string.Empty;
            }

            return task.ContentType == DownloadContentType.Lyrics
                ? $"{task.Song.Name} (歌词)"
                : task.Song.Name;
        }

        #endregion

        #region 窗体关闭

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (tabControl?.TabCount > 0)
            {
                _lastSelectedTabIndex = Math.Max(0, Math.Min(tabControl.SelectedIndex, tabControl.TabCount - 1));
                _hasLastTabSelection = true;
            }

            // 停止定时器
            _refreshTimer?.Stop();

            // 取消订阅下载管理器事件
            _downloadManager.TaskProgressChanged -= OnDownloadTaskProgressChanged;
            _downloadManager.TaskCompleted -= OnDownloadTaskCompleted;
            _downloadManager.TaskFailed -= OnDownloadTaskFailed;
            _downloadManager.TaskCancelled -= OnDownloadTaskCancelled;
            _downloadManager.QueueStateChanged -= OnDownloadQueueStateChanged;

            // 取消订阅上传管理器事件
            _uploadManager.TaskProgressChanged -= OnUploadTaskProgressChanged;
            _uploadManager.TaskCompleted -= OnUploadTaskCompleted;
            _uploadManager.TaskFailed -= OnUploadTaskFailed;
            _uploadManager.TaskCancelled -= OnUploadTaskCancelled;
            _uploadManager.QueueStateChanged -= OnUploadQueueStateChanged;

            base.OnFormClosing(e);
        }

        #endregion

        #region 上传列表刷新和显示

        /// <summary>
        /// 刷新上传中列表
        /// </summary>
        private void RefreshUploadList()
        {
            lvUpload.BeginUpdate();

            try
            {
                var allUploadTasks = _uploadManager.GetAllActiveTasks();

                // 移除不存在的项
                var itemsToRemove = new List<ListViewItem>();
                foreach (ListViewItem item in lvUpload.Items)
                {
                    if (item.Tag is UploadTask task && !allUploadTasks.Contains(task))
                    {
                        itemsToRemove.Add(item);
                    }
                }
                foreach (var item in itemsToRemove)
                {
                    lvUpload.Items.Remove(item);
                }

                // 更新或添加项
                foreach (var task in allUploadTasks)
                {
                    UpdateUploadTaskInList(task);
                }
            }
            finally
            {
                lvUpload.EndUpdate();
            }
        }

        /// <summary>
        /// 更新上传任务在列表中
        /// </summary>
        private void UpdateUploadTaskInList(UploadTask task)
        {
            ListViewItem? existingItem = null;
            foreach (ListViewItem item in lvUpload.Items)
            {
                if (item.Tag == task)
                {
                    existingItem = item;
                    break;
                }
            }

            if (existingItem == null)
            {
                existingItem = new ListViewItem(new[]
                {
                    task.FileName,
                    task.SourceList,
                    $"{task.ProgressPercentage}%",
                    GetUploadStageText(task),
                    GetUploadStatusText(task.Status)
                })
                {
                    Tag = task
                };
                lvUpload.Items.Add(existingItem);
            }
            else
            {
                existingItem.SubItems[0].Text = task.FileName;
                existingItem.SubItems[1].Text = task.SourceList;
                existingItem.SubItems[2].Text = $"{task.ProgressPercentage}%";
                existingItem.SubItems[3].Text = GetUploadStageText(task);
                existingItem.SubItems[4].Text = GetUploadStatusText(task.Status);
            }
        }

        /// <summary>
        /// 获取上传状态文本
        /// </summary>
        private string GetUploadStatusText(UploadStatus status)
        {
            switch (status)
            {
                case UploadStatus.Pending:
                    return "等待中";
                case UploadStatus.Uploading:
                    return "上传中";
                case UploadStatus.Paused:
                    return "已暂停";
                case UploadStatus.Completed:
                    return "已完成";
                case UploadStatus.Failed:
                    return "失败";
                case UploadStatus.Cancelled:
                    return "已取消";
                default:
                    return "未知";
            }
        }

        /// <summary>
        /// 构建上传阶段文本，附带实时速度
        /// </summary>
        private string GetUploadStageText(UploadTask task)
        {
            string stage = string.IsNullOrWhiteSpace(task.StageMessage) ? "等待中" : task.StageMessage;
            if (task.CurrentSpeedBytesPerSecond > 0)
            {
                stage = $"{stage} ({task.FormattedSpeed})";
            }
            return stage;
        }

        #endregion

        #region 上传上下文菜单

        private void ShowUploadContextMenu(Point location)
        {
            var menu = new ContextMenuStrip();

            if (_selectedUploadTask != null)
            {
                // 暂停/继续
                if (_selectedUploadTask.Status == UploadStatus.Uploading)
                {
                    var pauseItem = new ToolStripMenuItem("暂停");
                    pauseItem.Click += (s, e) =>
                    {
                        if (_selectedUploadTask != null)
                        {
                            _uploadManager.PauseTask(_selectedUploadTask);
                        }
                    };
                    menu.Items.Add(pauseItem);
                }
                else if (_selectedUploadTask.Status == UploadStatus.Paused || _selectedUploadTask.Status == UploadStatus.Pending)
                {
                    var resumeItem = new ToolStripMenuItem("继续");
                    resumeItem.Click += (s, e) =>
                    {
                        if (_selectedUploadTask != null)
                        {
                            _uploadManager.ResumeTask(_selectedUploadTask);
                        }
                    };
                    menu.Items.Add(resumeItem);
                }

                // 删除
                var cancelItem = new ToolStripMenuItem("删除");
                cancelItem.Click += (s, e) =>
                {
                    if (_selectedUploadTask != null)
                    {
                        _uploadManager.CancelTask(_selectedUploadTask);
                    }
                };
                menu.Items.Add(cancelItem);
            }

            ShowAccessibleContextMenu(menu, lvUpload, location, "UploadContextMenu");
        }

        #endregion
    }
}


