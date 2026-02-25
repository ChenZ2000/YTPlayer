using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using System.Windows.Forms.Automation;
using MessageBox = YTPlayer.MessageBox;
using YTPlayer.Core;
using YTPlayer.Core.Download;
using YTPlayer.Core.Upload;
using YTPlayer.Models;
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
        #region 常量

        private const int RefreshIntervalMs = 500;
        private const int TypeSearchTimeoutMs = 900;
        private const int SequenceColumnVisibleWidth = 64;

        private const int HiddenAccessibilityColumnIndex = 0;
        private const int SequenceColumnIndex = 1;

        private const int ActiveColumnCount = 8;
        private const int ActiveColumnSequence = SequenceColumnIndex;
        private const int ActiveColumnStatus = 2;
        private const int ActiveColumnName = 3;
        private const int ActiveColumnDuration = 4;
        private const int ActiveColumnView = 5;
        private const int ActiveColumnProgress = 6;
        private const int ActiveColumnSpeed = 7;

        private const int CompletedColumnCount = 9;
        private const int CompletedColumnSequence = SequenceColumnIndex;
        private const int CompletedColumnName = 2;
        private const int CompletedColumnDuration = 3;
        private const int CompletedColumnType = 4;
        private const int CompletedColumnSource = 5;
        private const int CompletedColumnStatus = 6;
        private const int CompletedColumnTime = 7;
        private const int CompletedColumnSize = 8;

        private const string ActivePlaceholderText = "暂无正在下载的任务，按 F5 刷新";
        private const string UploadPlaceholderText = "暂无正在上传的任务，按 F5 刷新";
        private const string CompletedPlaceholderText = "暂无已完成的任务，按 F5 刷新";
        private static readonly object ActivePlaceholderTag = new object();
        private static readonly object UploadPlaceholderTag = new object();
        private static readonly object CompletedPlaceholderTag = new object();

        #endregion

        #region 私有字段

        private readonly DownloadManager _downloadManager;
        private readonly UploadManager _uploadManager;
        private readonly ConfigManager _configManager;
        private readonly System.Windows.Forms.Timer _refreshTimer;

        private DownloadTask? _selectedDownloadTask;
        private UploadTask? _selectedUploadTask;

        private bool _hideTransferSequenceNumbers;
        private string _listTypeSearchBuffer = string.Empty;
        private DateTime _listTypeSearchLastInputUtc = DateTime.MinValue;
        private Label? _accessibilityAnnouncementLabel;
        private string _lastAnnouncementText = string.Empty;
        private DateTime _lastAnnouncementAt = DateTime.MinValue;
        private bool _queuedListRefresh;
        private bool _focusedRefreshAnnouncementQueued;
        private ListView? _pendingFocusedRefreshListView;
        private int _pendingFocusedRefreshAnnouncementIndex = -1;
        private bool _pendingListFocusAfterCtrlTab;

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

            KeyPreview = true;
            KeyDown += DownloadManagerForm_KeyDown;
            Shown += DownloadManagerForm_Shown;

            _configManager = ConfigManager.Instance;
            LoadTransferAccessibilitySettings();

            _downloadManager = DownloadManager.Instance;
            _uploadManager = UploadManager.Instance;

            _refreshTimer = new System.Windows.Forms.Timer
            {
                Interval = RefreshIntervalMs
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
            ApplyTransferSequenceColumnVisibility();
            ApplyInitialTabSelection();
            RefreshLists();

            _refreshTimer.Start();
        }

        #endregion

        #region 初始化

        private void LoadTransferAccessibilitySettings()
        {
            try
            {
                ConfigModel config = _configManager.Load();
                _hideTransferSequenceNumbers = config.TransferListSequenceNumberHidden;
            }
            catch (Exception ex)
            {
                _hideTransferSequenceNumbers = false;
                Debug.WriteLine($"[TransferAccessibility] 读取配置失败，使用默认值: {ex.Message}");
            }
        }

        private void SaveTransferAccessibilitySettings()
        {
            try
            {
                ConfigModel config = _configManager.Load();
                config.TransferListSequenceNumberHidden = _hideTransferSequenceNumbers;
                _configManager.Save(config);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TransferAccessibility] 保存配置失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 初始化 ListView
        /// </summary>
        private void InitializeListViews()
        {
            ConfigureTransferListView(lvActive, "下载任务列表", LvActive_MouseClick);
            ConfigureTransferListView(lvUpload, "上传任务列表", LvUpload_MouseClick);
            ConfigureTransferListView(lvCompleted, "已完成任务列表", LvCompleted_MouseClick);

            ConfigureActiveTransferListColumns(lvActive, "来源视图");
            ConfigureActiveTransferListColumns(lvUpload, "目标视图");
            ConfigureCompletedListColumns(lvCompleted);
        }

        private void ConfigureTransferListView(ListView listView, string accessibleName, MouseEventHandler mouseHandler)
        {
            listView.View = View.Details;
            listView.FullRowSelect = true;
            listView.GridLines = true;
            listView.MultiSelect = false;
            listView.HideSelection = false;
            listView.LabelWrap = false;
            listView.AccessibleName = accessibleName;
            listView.AccessibleRole = AccessibleRole.Table;

            listView.MouseClick += mouseHandler;
            listView.KeyDown += TransferListView_KeyDown;
            listView.KeyPress += TransferListView_KeyPress;
            listView.ItemSelectionChanged += TransferListView_ItemSelectionChanged;
            listView.HandleCreated += TransferListView_HandleCreated;
        }

        private static void ConfigureActiveTransferListColumns(ListView listView, string viewColumnHeader)
        {
            listView.Columns.Clear();
            listView.Columns.Add(string.Empty, 0);
            listView.Columns.Add("序号", SequenceColumnVisibleWidth);
            listView.Columns.Add("状态", 96);
            listView.Columns.Add("名称", 280);
            listView.Columns.Add("时长", 90);
            listView.Columns.Add(viewColumnHeader, 230);
            listView.Columns.Add("进度", 120);
            listView.Columns.Add("速度", 120);
        }

        private static void ConfigureCompletedListColumns(ListView listView)
        {
            listView.Columns.Clear();
            listView.Columns.Add(string.Empty, 0);
            listView.Columns.Add("序号", SequenceColumnVisibleWidth);
            listView.Columns.Add("名称", 220);
            listView.Columns.Add("时长", 90);
            listView.Columns.Add("类型", 90);
            listView.Columns.Add("来源列表", 170);
            listView.Columns.Add("状态", 90);
            listView.Columns.Add("完成时间", 170);
            listView.Columns.Add("文件大小", 120);
        }

        private void ApplyTransferSequenceColumnVisibility()
        {
            ApplySequenceColumnVisibility(lvActive);
            ApplySequenceColumnVisibility(lvUpload);
            ApplySequenceColumnVisibility(lvCompleted);

            RefreshSequenceAndAccessibility(lvActive, allowFocusedItemAccessibilityRefresh: true);
            RefreshSequenceAndAccessibility(lvUpload, allowFocusedItemAccessibilityRefresh: true);
            RefreshSequenceAndAccessibility(lvCompleted, allowFocusedItemAccessibilityRefresh: true);
        }

        private void ApplySequenceColumnVisibility(ListView listView)
        {
            if (listView.Columns.Count <= SequenceColumnIndex)
            {
                return;
            }

            listView.Columns[HiddenAccessibilityColumnIndex].Width = 0;
            listView.Columns[SequenceColumnIndex].Width = _hideTransferSequenceNumbers ? 0 : SequenceColumnVisibleWidth;
        }

        private void ToggleTransferSequenceNumberHidden()
        {
            ListView? interactionListView = GetInteractionListView();
            bool hadListFocus = interactionListView != null && interactionListView.ContainsFocus;
            int previousFocusedIndex = interactionListView != null ? GetSelectedListIndex(interactionListView) : -1;

            _hideTransferSequenceNumbers = !_hideTransferSequenceNumbers;
            ApplyTransferSequenceColumnVisibility();
            SaveTransferAccessibilitySettings();

            string announce = _hideTransferSequenceNumbers ? "已隐藏序号" : "已显示序号";
            AccessibleDescription = announce;
            RaiseAccessibilityAnnouncement(announce, AutomationNotificationProcessing.All);
            try
            {
                AccessibilityNotifyClients(AccessibleEvents.DescriptionChange, -1);
            }
            catch
            {
            }

            ReplayListViewFocusAfterSequenceToggle(interactionListView, previousFocusedIndex, hadListFocus);
            Debug.WriteLine("[TransferAccessibility] " + announce);
        }

        private ListView? GetInteractionListView()
        {
            if (lvActive.ContainsFocus)
            {
                return lvActive;
            }

            if (lvUpload.ContainsFocus)
            {
                return lvUpload;
            }

            if (lvCompleted.ContainsFocus)
            {
                return lvCompleted;
            }

            if (tabControl.SelectedIndex == 0)
            {
                return lvActive;
            }

            if (tabControl.SelectedIndex == 1)
            {
                return lvUpload;
            }

            return lvCompleted;
        }

        private bool IsAnyTransferListViewFocused()
        {
            return lvActive.ContainsFocus || lvUpload.ContainsFocus || lvCompleted.ContainsFocus;
        }

        private void ReplayListViewFocusAfterSequenceToggle(ListView? listView, int expectedIndex, bool hadListFocus)
        {
            if (listView == null || listView.IsDisposed || !listView.IsHandleCreated)
            {
                return;
            }

            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed || listView.IsDisposed || !listView.IsHandleCreated)
                    {
                        return;
                    }

                    int targetIndex = expectedIndex;
                    if (targetIndex < 0 || targetIndex >= listView.Items.Count || IsPlaceholderItem(listView.Items[targetIndex]))
                    {
                        targetIndex = GetSelectedListIndex(listView);
                    }

                    if (targetIndex >= 0 && targetIndex < listView.Items.Count && !IsPlaceholderItem(listView.Items[targetIndex]))
                    {
                        SelectListViewItem(listView, targetIndex, focusListView: hadListFocus);
                        QueueFocusedListViewItemRefreshAnnouncement(listView, targetIndex, hadListFocus);
                        return;
                    }

                    if (hadListFocus && listView.CanFocus)
                    {
                        listView.Focus();
                    }

                    NotifyAccessibilityClients(listView, AccessibleEvents.Focus, -1);
                }));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TransferAccessibility] 序号切换后焦点重播失败: {ex.Message}");
            }
        }

        private void QueueFocusedListViewItemRefreshAnnouncement(ListView listView, int expectedIndex, bool hadListFocus)
        {
            if (listView == null || listView.IsDisposed || !listView.IsHandleCreated)
            {
                return;
            }

            if (expectedIndex < 0)
            {
                return;
            }

            if (_focusedRefreshAnnouncementQueued
                && ReferenceEquals(_pendingFocusedRefreshListView, listView)
                && _pendingFocusedRefreshAnnouncementIndex == expectedIndex)
            {
                return;
            }

            _focusedRefreshAnnouncementQueued = true;
            _pendingFocusedRefreshListView = listView;
            _pendingFocusedRefreshAnnouncementIndex = expectedIndex;

            try
            {
                BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (IsDisposed || listView.IsDisposed || !listView.IsHandleCreated)
                        {
                            return;
                        }

                        if (hadListFocus && listView.CanFocus && !listView.ContainsFocus)
                        {
                            listView.Focus();
                        }

                        if (!listView.ContainsFocus)
                        {
                            return;
                        }

                        int focusedIndex = GetSelectedListIndex(listView);
                        if (focusedIndex != expectedIndex || focusedIndex < 0 || focusedIndex >= listView.Items.Count)
                        {
                            return;
                        }

                        ListViewItem item = listView.Items[focusedIndex];
                        if (item == null || IsPlaceholderItem(item))
                        {
                            return;
                        }

                        TrySyncListViewItemAccessibility(listView, item, allowFocusedItemAccessibilityRefresh: true);

                        // 与主界面列表一致：对焦点项发 NameChange 并补 Selection/Focus，促使读屏刷新当前项。
                        NotifyAccessibilityClients(listView, AccessibleEvents.NameChange, focusedIndex);
                        NotifyAccessibilityClients(listView, AccessibleEvents.ValueChange, focusedIndex);
                        NotifyAccessibilityClients(listView, AccessibleEvents.Selection, focusedIndex);
                        NotifyAccessibilityClients(listView, AccessibleEvents.SelectionAdd, focusedIndex);
                        NotifyAccessibilityClients(listView, AccessibleEvents.Focus, focusedIndex);

                        string speech = GetSubItemText(item, HiddenAccessibilityColumnIndex);
                        if (string.IsNullOrWhiteSpace(speech))
                        {
                            speech = BuildAccessibleRowSpeech(listView, item);
                        }

                        if (!string.IsNullOrWhiteSpace(speech))
                        {
                            // 不打断“已显示/已隐藏序号”提示，采用 polite 方式补播焦点项。
                            RaiseAccessibilityAnnouncement(
                                speech,
                                AutomationNotificationProcessing.CurrentThenMostRecent,
                                notifyMsaa: true,
                                updateLabelText: true);
                        }
                    }
                    finally
                    {
                        _focusedRefreshAnnouncementQueued = false;
                        _pendingFocusedRefreshListView = null;
                        _pendingFocusedRefreshAnnouncementIndex = -1;
                    }
                }));
            }
            catch
            {
                _focusedRefreshAnnouncementQueued = false;
                _pendingFocusedRefreshListView = null;
                _pendingFocusedRefreshAnnouncementIndex = -1;
            }
        }

        private void RaiseAccessibilityAnnouncement(
            string text,
            AutomationNotificationProcessing processing = AutomationNotificationProcessing.ImportantMostRecent,
            bool notifyMsaa = true,
            bool updateLabelText = true)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            EnsureAccessibilityAnnouncementLabel();
            if (_accessibilityAnnouncementLabel == null || _accessibilityAnnouncementLabel.IsDisposed)
            {
                return;
            }

            string trimmed = text.Trim();
            DateTime now = DateTime.UtcNow;
            if (string.Equals(_lastAnnouncementText, trimmed, StringComparison.Ordinal)
                && now - _lastAnnouncementAt < TimeSpan.FromMilliseconds(250))
            {
                return;
            }

            _lastAnnouncementText = trimmed;
            _lastAnnouncementAt = now;

            if (updateLabelText)
            {
                _accessibilityAnnouncementLabel.Text = trimmed;
                _accessibilityAnnouncementLabel.AccessibleName = trimmed;
                _accessibilityAnnouncementLabel.AccessibleDescription = trimmed;
            }

            try
            {
                _accessibilityAnnouncementLabel.AccessibilityObject.RaiseAutomationNotification(
                    AutomationNotificationKind.Other,
                    processing,
                    trimmed);
            }
            catch
            {
            }

            if (notifyMsaa)
            {
                NotifyAccessibilityClients(_accessibilityAnnouncementLabel, AccessibleEvents.NameChange, -1);
                NotifyAccessibilityClients(_accessibilityAnnouncementLabel, AccessibleEvents.ValueChange, -1);
            }
        }

        private void EnsureAccessibilityAnnouncementLabel()
        {
            if (_accessibilityAnnouncementLabel != null || IsDisposed)
            {
                return;
            }

            _accessibilityAnnouncementLabel = new Label
            {
                Name = "downloadManagerAccessibilityAnnouncementLabel",
                TabStop = false,
                AutoSize = false,
                Size = new Size(1, 1),
                Location = new Point(-2000, -2000),
                Text = string.Empty,
                AccessibleName = string.Empty,
                AccessibleRole = AccessibleRole.StaticText
            };
            Controls.Add(_accessibilityAnnouncementLabel);
        }

        private void NotifyAccessibilityClients(Control control, AccessibleEvents accEvent, int childId)
        {
            if (control == null)
            {
                return;
            }

            try
            {
                MethodInfo? method = typeof(Control).GetMethod(
                    "AccessibilityNotifyClients",
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    null,
                    new[] { typeof(AccessibleEvents), typeof(int) },
                    null);
                method?.Invoke(control, new object[] { accEvent, childId });
            }
            catch
            {
            }
        }

        private void RequestRefreshLists()
        {
            if (_queuedListRefresh || IsDisposed)
            {
                return;
            }

            _queuedListRefresh = true;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    _queuedListRefresh = false;
                    if (!IsDisposed)
                    {
                        RefreshLists();
                    }
                }));
            }
            catch
            {
                _queuedListRefresh = false;
            }
        }

        private void ApplyInitialTabSelection()
        {
            // 初始页签判定只基于真实任务数据，不依赖列表占位符。
            List<DownloadTask> activeDownloads = _downloadManager.GetAllActiveTasks();
            List<UploadTask> activeUploads = _uploadManager.GetAllActiveTasks();

            bool hasDownloadTasks = activeDownloads.Count > 0;
            bool hasUploadTasks = activeUploads.Count > 0;

            if (hasDownloadTasks && !hasUploadTasks)
            {
                tabControl.SelectedTab = tabPageActive;
                return;
            }

            if (!hasDownloadTasks && hasUploadTasks)
            {
                tabControl.SelectedTab = tabPageUpload;
                return;
            }

            if (!hasDownloadTasks && !hasUploadTasks)
            {
                tabControl.SelectedTab = tabPageCompleted;
                return;
            }

            DateTime latestDownloadCreatedAt = activeDownloads.Max(GetDownloadTaskCreatedTime);
            DateTime latestUploadCreatedAt = activeUploads.Max(GetUploadTaskCreatedTime);
            tabControl.SelectedTab = latestUploadCreatedAt > latestDownloadCreatedAt
                ? tabPageUpload
                : tabPageActive;
        }

        private static DateTime GetDownloadTaskCreatedTime(DownloadTask? task)
        {
            if (task == null)
            {
                return DateTime.MinValue;
            }

            return task.StartTime;
        }

        private static DateTime GetUploadTaskCreatedTime(UploadTask? task)
        {
            if (task == null)
            {
                return DateTime.MinValue;
            }

            DateTime created = task.CreatedTime;
            if (created > DateTime.MinValue)
            {
                return created;
            }

            if (task.StartTime.HasValue)
            {
                return task.StartTime.Value;
            }

            return DateTime.MinValue;
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

            // 实时刷新，但不主动重设焦点，不触发额外焦点播报。
            UpdateDownloadTaskInList(task, allowFocusedItemAccessibilityRefresh: false);
        }

        private void OnDownloadTaskCompleted(DownloadTask task)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnDownloadTaskCompleted(task)));
                return;
            }

            // 队列状态事件中会统一触发刷新，避免重复刷新导致读屏重复播报。
        }

        private void OnDownloadTaskFailed(DownloadTask task)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnDownloadTaskFailed(task)));
                return;
            }

            // 队列状态事件中会统一触发刷新，避免重复刷新导致读屏重复播报。
        }

        private void OnDownloadTaskCancelled(DownloadTask task)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnDownloadTaskCancelled(task)));
                return;
            }

            // 队列状态事件中会统一触发刷新，避免重复刷新导致读屏重复播报。
        }

        private void OnDownloadQueueStateChanged()
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(OnDownloadQueueStateChanged));
                return;
            }

            RequestRefreshLists();
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

            // 实时刷新，但不主动重设焦点，不触发额外焦点播报。
            UpdateUploadTaskInList(task, allowFocusedItemAccessibilityRefresh: false);
        }

        private void OnUploadTaskCompleted(UploadTask task)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnUploadTaskCompleted(task)));
                return;
            }

            // 队列状态事件中会统一触发刷新，避免重复刷新导致读屏重复播报。
        }

        private void OnUploadTaskFailed(UploadTask task)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnUploadTaskFailed(task)));
                return;
            }

            // 队列状态事件中会统一触发刷新，避免重复刷新导致读屏重复播报。
        }

        private void OnUploadTaskCancelled(UploadTask task)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnUploadTaskCancelled(task)));
                return;
            }

            // 队列状态事件中会统一触发刷新，避免重复刷新导致读屏重复播报。
        }

        private void OnUploadQueueStateChanged()
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(OnUploadQueueStateChanged));
                return;
            }

            RequestRefreshLists();
        }

        #endregion

        #region 事件处理 - UI

        private void RefreshTimer_Tick(object? sender, EventArgs e)
        {
            // 下载中/上传中标签页由任务事件驱动刷新，避免焦点项被定时刷新反复刺激读屏。
            if (tabControl.SelectedIndex == 0)
            {
                if (!lvActive.ContainsFocus)
                {
                    RefreshActiveList();
                }
                return;
            }

            if (tabControl.SelectedIndex == 1)
            {
                if (!lvUpload.ContainsFocus)
                {
                    RefreshUploadList();
                }
                return;
            }

            if (!lvCompleted.ContainsFocus)
            {
                RefreshCompletedList();
            }
        }

        private void DownloadManagerForm_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Tab && e.Control)
            {
                _pendingListFocusAfterCtrlTab = IsAnyTransferListViewFocused();
            }
            else
            {
                _pendingListFocusAfterCtrlTab = false;
            }

            if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                Close();
                return;
            }

            if (e.KeyCode == Keys.F5)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                RefreshLists();
                return;
            }

            if (e.KeyCode == Keys.F8)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                ToggleTransferSequenceNumberHidden();
            }
        }

        private void DownloadManagerForm_Shown(object? sender, EventArgs e)
        {
            EnsureSelectedTabListFocusDeferred(forceListFocus: true);
        }

        private void TabControl_SelectedIndexChanged(object? sender, EventArgs e)
        {
            bool shouldRestoreListFocus = _pendingListFocusAfterCtrlTab;
            _pendingListFocusAfterCtrlTab = false;

            ResetTypeSearchBuffer();
            RefreshLists();
            if (shouldRestoreListFocus)
            {
                EnsureSelectedTabListFocusDeferred(forceListFocus: true);
            }
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
                // ClearCompletedTasks 会触发 QueueStateChanged 事件，事件中已包含 RefreshLists。
            }
        }

        private void LvActive_MouseClick(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right)
            {
                return;
            }

            ListViewItem? item = lvActive.GetItemAt(e.X, e.Y);
            if (item == null || item.Tag is not DownloadTask task)
            {
                return;
            }

            if (!TryPrepareContextMenuTarget(lvActive, item))
            {
                return;
            }

            _selectedDownloadTask = task;
            ShowDownloadContextMenu(e.Location);
        }

        private void LvUpload_MouseClick(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right)
            {
                return;
            }

            ListViewItem? item = lvUpload.GetItemAt(e.X, e.Y);
            if (item == null || item.Tag is not UploadTask task)
            {
                return;
            }

            if (!TryPrepareContextMenuTarget(lvUpload, item))
            {
                return;
            }

            _selectedUploadTask = task;
            ShowUploadContextMenu(e.Location);
        }

        private void LvCompleted_MouseClick(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right)
            {
                return;
            }

            ListViewItem? item = lvCompleted.GetItemAt(e.X, e.Y);
            if (item == null || IsPlaceholderItem(item))
            {
                return;
            }

            if (!TryPrepareContextMenuTarget(lvCompleted, item))
            {
                return;
            }

            if (item.Tag is DownloadTask downloadTask)
            {
                _selectedDownloadTask = downloadTask;
                ShowCompletedContextMenu(e.Location, isDownload: true);
            }
            else if (item.Tag is UploadTask uploadTask)
            {
                _selectedUploadTask = uploadTask;
                ShowCompletedContextMenu(e.Location, isDownload: false);
            }
        }

        private void TransferListView_KeyDown(object? sender, KeyEventArgs e)
        {
            if (sender is not ListView listView)
            {
                return;
            }

            bool openContextMenu = e.KeyCode == Keys.Apps || (e.KeyCode == Keys.F10 && e.Shift);
            if (!openContextMenu)
            {
                return;
            }

            if (ShowContextMenuByKeyboard(listView))
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        private void TransferListView_KeyPress(object? sender, KeyPressEventArgs e)
        {
            if (sender is not ListView listView || !listView.ContainsFocus)
            {
                return;
            }

            if (char.IsControl(e.KeyChar) || char.IsWhiteSpace(e.KeyChar))
            {
                return;
            }

            if ((ModifierKeys & (Keys.Control | Keys.Alt)) != 0)
            {
                return;
            }

            if (!HasTypeSearchableItems(listView))
            {
                return;
            }

            DateTime now = DateTime.UtcNow;
            if (_listTypeSearchLastInputUtc == DateTime.MinValue ||
                (now - _listTypeSearchLastInputUtc).TotalMilliseconds > TypeSearchTimeoutMs)
            {
                _listTypeSearchBuffer = string.Empty;
            }

            _listTypeSearchLastInputUtc = now;
            _listTypeSearchBuffer += e.KeyChar;

            int startIndex = GetSelectedListIndex(listView);
            int targetIndex = FindListItemIndexByPrimaryText(listView, _listTypeSearchBuffer, startIndex);
            if (targetIndex < 0 && _listTypeSearchBuffer.Length > 1)
            {
                _listTypeSearchBuffer = e.KeyChar.ToString();
                targetIndex = FindListItemIndexByPrimaryText(listView, _listTypeSearchBuffer, startIndex);
            }

            if (targetIndex >= 0)
            {
                SelectListViewItem(listView, targetIndex, focusListView: false);
            }

            e.Handled = true;
        }

        private void TransferListView_ItemSelectionChanged(object? sender, ListViewItemSelectionChangedEventArgs e)
        {
            if (!e.IsSelected || e.Item == null || sender is not ListView listView)
            {
                return;
            }

            TrySyncListViewItemAccessibility(listView, e.Item, allowFocusedItemAccessibilityRefresh: true);
        }

        private void TransferListView_HandleCreated(object? sender, EventArgs e)
        {
            if (sender is not ListView listView || !listView.IsHandleCreated)
            {
                return;
            }

            AccessibilityPropertyService.TrySetControlRole(listView.Handle, AccessibleRole.Table);
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed || listView.IsDisposed || !listView.IsHandleCreated)
                    {
                        return;
                    }

                    RefreshSequenceAndAccessibility(listView, allowFocusedItemAccessibilityRefresh: true);
                }));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TransferAccessibility] 延迟刷新列表无障碍失败: {ex.Message}");
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
            MenuNavigationBoundaryHelper.Attach(menu);
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
            DisposeContextMenuDeferred(menu);
        }

        private void DisposeContextMenuDeferred(ContextMenuStrip? menu)
        {
            if (menu == null || menu.IsDisposed)
            {
                return;
            }

            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (menu.IsDisposed)
                    {
                        return;
                    }

                    try
                    {
                        menu.Dispose();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }));
            }
            catch
            {
                // 窗口关闭等阶段可能无法再投递到 UI 队列；此时放弃释放即可，避免同帧立即释放触发已处置访问。
            }
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
                else if (_selectedDownloadTask.Status == DownloadStatus.Paused)
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
                    TryCopySelectedDownloadUrlToClipboard();
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

                var copyUrlItem = new ToolStripMenuItem("复制下载链接");
                copyUrlItem.Click += (s, e) =>
                {
                    TryCopySelectedDownloadUrlToClipboard();
                };
                menu.Items.Add(copyUrlItem);
            }
            else if (!isDownload && _selectedUploadTask != null)
            {
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

        private void TryCopySelectedDownloadUrlToClipboard()
        {
            if (_selectedDownloadTask == null || string.IsNullOrWhiteSpace(_selectedDownloadTask.DownloadUrl))
            {
                return;
            }

            try
            {
                Clipboard.SetText(_selectedDownloadTask.DownloadUrl);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TransferContextMenu] 复制下载链接失败: {ex.Message}");
            }
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
        /// 刷新进行中下载列表
        /// </summary>
        private void RefreshActiveList()
        {
            lvActive.BeginUpdate();

            try
            {
                var allActiveTasks = _downloadManager.GetAllActiveTasks()
                    .Where(static task => task != null)
                    .ToList();

                var itemsToRemove = new List<ListViewItem>();
                foreach (ListViewItem item in EnumerateItemsSafe(lvActive))
                {
                    if (IsPlaceholderItem(item))
                    {
                        itemsToRemove.Add(item);
                        continue;
                    }

                    if (item.Tag is DownloadTask task)
                    {
                        if (!ContainsTaskIdentity(allActiveTasks, task))
                        {
                            itemsToRemove.Add(item);
                        }
                    }
                    else
                    {
                        itemsToRemove.Add(item);
                    }
                }

                foreach (var item in itemsToRemove)
                {
                    lvActive.Items.Remove(item);
                }

                foreach (var task in allActiveTasks)
                {
                    UpdateDownloadTaskInList(task);
                }

                EnsurePlaceholderItemIfNeeded(lvActive, ActivePlaceholderTag, ActivePlaceholderText, ActiveColumnCount);
                RefreshSequenceAndAccessibility(lvActive, allowFocusedItemAccessibilityRefresh: false);
            }
            finally
            {
                lvActive.EndUpdate();
            }
        }

        /// <summary>
        /// 刷新上传列表
        /// </summary>
        private void RefreshUploadList()
        {
            lvUpload.BeginUpdate();

            try
            {
                var allUploadTasks = _uploadManager.GetAllActiveTasks()
                    .Where(static task => task != null)
                    .ToList();

                var itemsToRemove = new List<ListViewItem>();
                foreach (ListViewItem item in EnumerateItemsSafe(lvUpload))
                {
                    if (IsPlaceholderItem(item))
                    {
                        itemsToRemove.Add(item);
                        continue;
                    }

                    if (item.Tag is UploadTask task)
                    {
                        if (!ContainsTaskIdentity(allUploadTasks, task))
                        {
                            itemsToRemove.Add(item);
                        }
                    }
                    else
                    {
                        itemsToRemove.Add(item);
                    }
                }

                foreach (var item in itemsToRemove)
                {
                    lvUpload.Items.Remove(item);
                }

                foreach (var task in allUploadTasks)
                {
                    UpdateUploadTaskInList(task);
                }

                EnsurePlaceholderItemIfNeeded(lvUpload, UploadPlaceholderTag, UploadPlaceholderText, ActiveColumnCount);
                RefreshSequenceAndAccessibility(lvUpload, allowFocusedItemAccessibilityRefresh: false);
            }
            finally
            {
                lvUpload.EndUpdate();
            }
        }

        /// <summary>
        /// 刷新已完成列表（增量更新，避免焦点丢失）
        /// </summary>
        private void RefreshCompletedList()
        {
            lvCompleted.BeginUpdate();

            try
            {
                var completedDownloadTasks = _downloadManager.GetCompletedTasks()
                    .Where(static task => task != null)
                    .ToList();
                var completedUploadTasks = _uploadManager.GetCompletedTasks()
                    .Where(static task => task != null)
                    .ToList();
                var allTasks = completedDownloadTasks.Cast<object>()
                    .Concat(completedUploadTasks.Cast<object>())
                    .ToList();

                var itemsToRemove = new List<ListViewItem>();
                foreach (ListViewItem item in EnumerateItemsSafe(lvCompleted))
                {
                    if (IsPlaceholderItem(item))
                    {
                        itemsToRemove.Add(item);
                        continue;
                    }

                    if (item.Tag == null || !ContainsTaskIdentity(allTasks, item.Tag))
                    {
                        itemsToRemove.Add(item);
                    }
                }

                foreach (var item in itemsToRemove)
                {
                    lvCompleted.Items.Remove(item);
                }

                foreach (var task in completedDownloadTasks)
                {
                    UpdateCompletedItem(task);
                }

                foreach (var task in completedUploadTasks)
                {
                    UpdateCompletedItem(task);
                }

                EnsurePlaceholderItemIfNeeded(lvCompleted, CompletedPlaceholderTag, CompletedPlaceholderText, CompletedColumnCount);
                RefreshSequenceAndAccessibility(lvCompleted, allowFocusedItemAccessibilityRefresh: false);
            }
            finally
            {
                lvCompleted.EndUpdate();
            }
        }

        #endregion

        #region 列表项更新

        /// <summary>
        /// 更新下载任务在进行中列表
        /// </summary>
        private void UpdateDownloadTaskInList(DownloadTask task, bool allowFocusedItemAccessibilityRefresh = false)
        {
            ListViewItem item = FindOrCreateListViewItem(lvActive, task, ActiveColumnCount);

            string title = FormatTaskTitle(task);
            string duration = GetDownloadDurationText(task);
            string source = GetDownloadViewText(task);
            string progress = GetDownloadProgressText(task);
            string speed = GetDownloadSpeedText(task);
            string status = GetActiveDownloadStatusText(task.Status);

            SetSubItemText(item, ActiveColumnStatus, status);
            SetSubItemText(item, ActiveColumnName, title);
            SetSubItemText(item, ActiveColumnDuration, duration);
            SetSubItemText(item, ActiveColumnView, source);
            SetSubItemText(item, ActiveColumnProgress, progress);
            SetSubItemText(item, ActiveColumnSpeed, speed);

            item.ForeColor = ThemeManager.Current.TextPrimary;
            TrySyncListViewItemAccessibility(lvActive, item, allowFocusedItemAccessibilityRefresh);
        }

        /// <summary>
        /// 更新上传任务在进行中列表
        /// </summary>
        private void UpdateUploadTaskInList(UploadTask task, bool allowFocusedItemAccessibilityRefresh = false)
        {
            ListViewItem item = FindOrCreateListViewItem(lvUpload, task, ActiveColumnCount);

            string duration = GetUploadDurationText(task);
            string targetView = GetUploadTargetViewText(task);
            string progress = GetUploadProgressText(task);
            string speed = GetUploadSpeedText(task);
            string status = GetActiveUploadStatusText(task.Status);

            SetSubItemText(item, ActiveColumnStatus, status);
            SetSubItemText(item, ActiveColumnName, task.FileName);
            SetSubItemText(item, ActiveColumnDuration, duration);
            SetSubItemText(item, ActiveColumnView, targetView);
            SetSubItemText(item, ActiveColumnProgress, progress);
            SetSubItemText(item, ActiveColumnSpeed, speed);

            item.ForeColor = ThemeManager.Current.TextPrimary;
            TrySyncListViewItemAccessibility(lvUpload, item, allowFocusedItemAccessibilityRefresh);
        }

        /// <summary>
        /// 更新任务在已完成列表
        /// </summary>
        private void UpdateCompletedItem(object task, bool allowFocusedItemAccessibilityRefresh = false)
        {
            if (task == null)
            {
                return;
            }

            ListViewItem item = FindOrCreateListViewItem(lvCompleted, task, CompletedColumnCount);

            if (task is DownloadTask downloadTask)
            {
                string completedTime = downloadTask.CompletedTime?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture) ?? string.Empty;
                string fileSize = downloadTask.TotalBytes > 0 ? DownloadFileHelper.FormatFileSize(downloadTask.TotalBytes) : string.Empty;

                SetSubItemText(item, CompletedColumnName, FormatTaskTitle(downloadTask));
                SetSubItemText(item, CompletedColumnDuration, GetDownloadDurationText(downloadTask));
                SetSubItemText(item, CompletedColumnType, downloadTask.ContentType == DownloadContentType.Lyrics ? "歌词下载" : "下载");
                SetSubItemText(item, CompletedColumnSource, downloadTask.SourceList);
                SetSubItemText(item, CompletedColumnStatus, GetStatusText(downloadTask.Status));
                SetSubItemText(item, CompletedColumnTime, completedTime);
                SetSubItemText(item, CompletedColumnSize, fileSize);
                item.ForeColor = ThemeManager.Current.TextPrimary;
            }
            else if (task is UploadTask uploadTask)
            {
                string completedTime = uploadTask.CompletedTime?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture) ?? string.Empty;
                string fileSize = uploadTask.TotalBytes > 0 ? uploadTask.FormattedFileSize : string.Empty;
                string duration = GetUploadDurationText(uploadTask);

                SetSubItemText(item, CompletedColumnName, uploadTask.FileName);
                SetSubItemText(item, CompletedColumnDuration, duration);
                SetSubItemText(item, CompletedColumnType, "上传");
                SetSubItemText(item, CompletedColumnSource, uploadTask.SourceList);
                SetSubItemText(item, CompletedColumnStatus, GetUploadStatusText(uploadTask.Status));
                SetSubItemText(item, CompletedColumnTime, completedTime);
                SetSubItemText(item, CompletedColumnSize, fileSize);
                item.ForeColor = ThemeManager.Current.TextPrimary;
            }

            TrySyncListViewItemAccessibility(lvCompleted, item, allowFocusedItemAccessibilityRefresh);
        }

        private ListViewItem FindOrCreateListViewItem(ListView listView, object tag, int columnCount)
        {
            ListViewItem? existingItem = null;
            foreach (ListViewItem item in EnumerateItemsSafe(listView))
            {
                if (IsSameTaskIdentity(item.Tag, tag))
                {
                    existingItem = item;
                    break;
                }
            }

            if (existingItem != null)
            {
                existingItem.Tag = tag;
                EnsureSubItemCount(existingItem, columnCount);
                return existingItem;
            }

            var created = new ListViewItem(new string[columnCount])
            {
                Tag = tag
            };
            listView.Items.Add(created);
            return created;
        }

        private static void EnsureSubItemCount(ListViewItem item, int requiredCount)
        {
            if (item == null || requiredCount <= 0)
            {
                return;
            }

            ListViewItem.ListViewSubItemCollection? subItems = null;
            try
            {
                subItems = item.SubItems;
            }
            catch
            {
                return;
            }

            if (subItems == null)
            {
                return;
            }

            while (subItems.Count < requiredCount)
            {
                subItems.Add(string.Empty);
            }
        }

        private static void SetSubItemText(ListViewItem item, int subItemIndex, string value)
        {
            if (item == null || subItemIndex < 0)
            {
                return;
            }

            EnsureSubItemCount(item, subItemIndex + 1);
            string normalized = value ?? string.Empty;
            if (string.Equals(item.SubItems[subItemIndex].Text, normalized, StringComparison.Ordinal))
            {
                return;
            }

            item.SubItems[subItemIndex].Text = normalized;
        }

        private static string GetSubItemText(ListViewItem item, int subItemIndex)
        {
            if (item == null || subItemIndex < 0)
            {
                return string.Empty;
            }

            try
            {
                if (item.SubItems.Count > subItemIndex)
                {
                    return item.SubItems[subItemIndex].Text ?? string.Empty;
                }
            }
            catch
            {
                return string.Empty;
            }

            return string.Empty;
        }

        private static string GetUploadDurationText(UploadTask task)
        {
            if (task == null || task.DurationSeconds <= 0)
            {
                return "--:--";
            }

            return FormatDurationFromSeconds(task.DurationSeconds);
        }

        private static bool ContainsTaskIdentity<TTask>(IEnumerable<TTask> tasks, object? target)
        {
            if (target == null)
            {
                return false;
            }

            foreach (TTask task in tasks)
            {
                if (IsSameTaskIdentity(task, target))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsSameTaskIdentity(object? left, object? right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left == null || right == null)
            {
                return false;
            }

            string leftKey = GetTaskIdentityKey(left);
            string rightKey = GetTaskIdentityKey(right);
            if (string.IsNullOrEmpty(leftKey) || string.IsNullOrEmpty(rightKey))
            {
                return false;
            }

            return string.Equals(leftKey, rightKey, StringComparison.OrdinalIgnoreCase);
        }

        private static string GetTaskIdentityKey(object task)
        {
            if (task is DownloadTask downloadTask)
            {
                if (!string.IsNullOrWhiteSpace(downloadTask.Id))
                {
                    return "D:" + downloadTask.Id.Trim();
                }

                string songId = downloadTask.Song?.Id ?? string.Empty;
                return "D:" + songId + "|" + downloadTask.DestinationPath + "|" + downloadTask.ContentType;
            }

            if (task is UploadTask uploadTask)
            {
                return "U:" + uploadTask.TaskId.ToString("N");
            }

            return string.Empty;
        }

        #endregion

        #region 占位符与序号

        private static bool IsPlaceholderItem(ListViewItem item)
        {
            if (item == null)
            {
                return true;
            }

            object? tag = item.Tag;
            return ReferenceEquals(tag, ActivePlaceholderTag)
                   || ReferenceEquals(tag, UploadPlaceholderTag)
                   || ReferenceEquals(tag, CompletedPlaceholderTag);
        }

        private void EnsurePlaceholderItemIfNeeded(ListView listView, object placeholderTag, string placeholderText, int columnCount)
        {
            bool hasRealItems = EnumerateItemsSafe(listView).Any(item => !IsPlaceholderItem(item));
            if (hasRealItems)
            {
                RemovePlaceholderItems(listView);
                return;
            }

            if (listView.Items.Count == 1 && ReferenceEquals(listView.Items[0].Tag, placeholderTag))
            {
                EnsurePlaceholderSelection(listView, listView.Items[0]);
                return;
            }

            listView.Items.Clear();
            var placeholderItem = new ListViewItem(new string[columnCount])
            {
                Tag = placeholderTag
            };

            int primaryTextColumnIndex = GetPrimaryTextColumnIndex(listView);
            SetSubItemText(placeholderItem, HiddenAccessibilityColumnIndex, placeholderText);
            SetSubItemText(placeholderItem, primaryTextColumnIndex, placeholderText);
            placeholderItem.ForeColor = ThemeManager.Current.TextSecondary;
            listView.Items.Add(placeholderItem);
            EnsurePlaceholderSelection(listView, placeholderItem);
        }

        private static void RemovePlaceholderItems(ListView listView)
        {
            for (int i = listView.Items.Count - 1; i >= 0; i--)
            {
                if (IsPlaceholderItem(listView.Items[i]))
                {
                    listView.Items.RemoveAt(i);
                }
            }
        }

        private void EnsurePlaceholderSelection(ListView listView, ListViewItem placeholderItem)
        {
            if (placeholderItem == null || !IsPlaceholderItem(placeholderItem))
            {
                return;
            }

            bool alreadySelected = listView.SelectedIndices.Count == 1 &&
                                   listView.SelectedIndices[0] == placeholderItem.Index &&
                                   placeholderItem.Focused;
            if (alreadySelected)
            {
                return;
            }

            listView.SelectedIndices.Clear();
            placeholderItem.Selected = true;
            placeholderItem.Focused = true;
            if (listView.ContainsFocus)
            {
                placeholderItem.EnsureVisible();
            }

            TrySyncListViewItemAccessibility(listView, placeholderItem, allowFocusedItemAccessibilityRefresh: true);
        }

        private void RefreshSequenceAndAccessibility(ListView listView, bool allowFocusedItemAccessibilityRefresh)
        {
            int sequence = 0;
            foreach (ListViewItem item in EnumerateItemsSafe(listView))
            {
                if (listView == lvCompleted)
                {
                    EnsureSubItemCount(item, CompletedColumnCount);
                    if (_hideTransferSequenceNumbers || IsPlaceholderItem(item))
                    {
                        SetSubItemText(item, CompletedColumnSequence, string.Empty);
                    }
                    else
                    {
                        sequence++;
                        SetSubItemText(item, CompletedColumnSequence, sequence.ToString(CultureInfo.CurrentCulture));
                    }
                }
                else
                {
                    EnsureSubItemCount(item, ActiveColumnCount);
                    if (_hideTransferSequenceNumbers || IsPlaceholderItem(item))
                    {
                        SetSubItemText(item, ActiveColumnSequence, string.Empty);
                    }
                    else
                    {
                        sequence++;
                        SetSubItemText(item, ActiveColumnSequence, sequence.ToString(CultureInfo.CurrentCulture));
                    }
                }

                TrySyncListViewItemAccessibility(listView, item, allowFocusedItemAccessibilityRefresh);
            }
        }

        #endregion

        #region 键入匹配跳转

        private void ResetTypeSearchBuffer()
        {
            _listTypeSearchBuffer = string.Empty;
            _listTypeSearchLastInputUtc = DateTime.MinValue;
        }

        private static bool HasTypeSearchableItems(ListView listView)
        {
            foreach (ListViewItem item in EnumerateItemsSafe(listView))
            {
                if (!IsPlaceholderItem(item))
                {
                    return true;
                }
            }

            return false;
        }

        private static IEnumerable<ListViewItem> EnumerateItemsSafe(ListView listView)
        {
            if (listView == null || listView.IsDisposed)
            {
                yield break;
            }

            int count;
            try
            {
                count = listView.Items.Count;
            }
            catch
            {
                yield break;
            }

            for (int i = 0; i < count; i++)
            {
                ListViewItem? item = null;
                try
                {
                    item = listView.Items[i];
                }
                catch
                {
                    continue;
                }

                if (item != null)
                {
                    yield return item;
                }
            }
        }

        private static int GetSelectedListIndex(ListView listView)
        {
            if (listView.SelectedIndices.Count > 0)
            {
                return listView.SelectedIndices[0];
            }

            return listView.FocusedItem?.Index ?? -1;
        }

        private int GetPrimaryTextColumnIndex(ListView listView)
        {
            if (ReferenceEquals(listView, lvCompleted))
            {
                return CompletedColumnName;
            }

            return ActiveColumnName;
        }

        private string GetPrimaryText(ListView listView, ListViewItem item)
        {
            int primaryTextColumnIndex = GetPrimaryTextColumnIndex(listView);
            if (item.SubItems.Count > primaryTextColumnIndex)
            {
                return item.SubItems[primaryTextColumnIndex].Text ?? string.Empty;
            }

            if (item.SubItems.Count > SequenceColumnIndex)
            {
                return item.SubItems[SequenceColumnIndex].Text ?? string.Empty;
            }

            return item.Text ?? string.Empty;
        }

        private int FindListItemIndexByPrimaryText(ListView listView, string searchPrefix, int startIndex)
        {
            if (string.IsNullOrWhiteSpace(searchPrefix))
            {
                return -1;
            }

            int count = listView.Items.Count;
            if (count <= 0)
            {
                return -1;
            }

            int cursor = startIndex;
            if (cursor < 0 || cursor >= count)
            {
                cursor = -1;
            }

            for (int step = 1; step <= count; step++)
            {
                int candidateIndex = (cursor + step) % count;
                ListViewItem candidate = listView.Items[candidateIndex];
                if (IsPlaceholderItem(candidate))
                {
                    continue;
                }

                string primary = GetPrimaryText(listView, candidate);
                if (primary.StartsWith(searchPrefix, StringComparison.CurrentCultureIgnoreCase))
                {
                    return candidateIndex;
                }
            }

            return -1;
        }

        private void SelectListViewItem(ListView listView, int targetIndex, bool focusListView)
        {
            if (targetIndex < 0 || targetIndex >= listView.Items.Count)
            {
                return;
            }

            ListViewItem item = listView.Items[targetIndex];
            if (IsPlaceholderItem(item))
            {
                return;
            }

            listView.BeginUpdate();
            try
            {
                listView.SelectedIndices.Clear();
                item.Selected = true;
                item.Focused = true;
                item.EnsureVisible();
            }
            finally
            {
                listView.EndUpdate();
            }

            if (focusListView && listView.CanFocus)
            {
                listView.Focus();
            }

            TrySyncListViewItemAccessibility(listView, item, allowFocusedItemAccessibilityRefresh: true);
        }

        private ListView GetSelectedTabListView()
        {
            if (tabControl.SelectedIndex == 0)
            {
                return lvActive;
            }

            if (tabControl.SelectedIndex == 1)
            {
                return lvUpload;
            }

            return lvCompleted;
        }

        private static int GetFirstFocusableItemIndex(ListView listView)
        {
            if (listView == null || listView.IsDisposed)
            {
                return -1;
            }

            for (int i = 0; i < listView.Items.Count; i++)
            {
                if (!IsPlaceholderItem(listView.Items[i]))
                {
                    return i;
                }
            }

            return listView.Items.Count > 0 ? 0 : -1;
        }

        private void EnsureListViewHasStableSelectionAndFocus(ListView listView, bool forceListFocus)
        {
            if (listView == null || listView.IsDisposed || !listView.IsHandleCreated)
            {
                return;
            }

            if (listView.Items.Count == 0)
            {
                return;
            }

            int targetIndex = GetSelectedListIndex(listView);
            if (targetIndex < 0 || targetIndex >= listView.Items.Count)
            {
                targetIndex = GetFirstFocusableItemIndex(listView);
            }

            if (targetIndex < 0 || targetIndex >= listView.Items.Count)
            {
                return;
            }

            ListViewItem targetItem = listView.Items[targetIndex];
            if (IsPlaceholderItem(targetItem))
            {
                EnsurePlaceholderSelection(listView, targetItem);
                if (forceListFocus && listView.CanFocus && !listView.ContainsFocus)
                {
                    listView.Focus();
                }
                return;
            }

            bool alreadyStable = targetItem.Selected && targetItem.Focused;
            if (alreadyStable)
            {
                if (forceListFocus && listView.CanFocus && !listView.ContainsFocus)
                {
                    listView.Focus();
                }

                return;
            }

            SelectListViewItem(listView, targetIndex, focusListView: forceListFocus);
        }

        private void EnsureSelectedTabListFocusDeferred(bool forceListFocus)
        {
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed)
                    {
                        return;
                    }

                    ListView listView = GetSelectedTabListView();
                    EnsureListViewHasStableSelectionAndFocus(listView, forceListFocus);
                }));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TransferFocus] 延迟聚焦列表失败: {ex.Message}");
            }
        }

        #endregion

        #region 上下文菜单选中与键盘打开

        private bool TryPrepareContextMenuTarget(ListView listView, ListViewItem item)
        {
            if (item == null || IsPlaceholderItem(item))
            {
                return false;
            }

            try
            {
                if (listView.SelectedIndices.Count != 1 || listView.SelectedIndices[0] != item.Index || !item.Focused)
                {
                    listView.BeginUpdate();
                    try
                    {
                        listView.SelectedIndices.Clear();
                        item.Selected = true;
                        item.Focused = true;
                    }
                    finally
                    {
                        listView.EndUpdate();
                    }
                }

                item.EnsureVisible();
                if (listView.CanFocus && !listView.ContainsFocus)
                {
                    listView.Focus();
                }

                TrySyncListViewItemAccessibility(listView, item, allowFocusedItemAccessibilityRefresh: true);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TransferAccessibility] 准备上下文菜单目标失败: {ex.Message}");
                return false;
            }
        }

        private bool ShowContextMenuByKeyboard(ListView listView)
        {
            ListViewItem? item = listView.SelectedItems.Count > 0 ? listView.SelectedItems[0] : listView.FocusedItem;
            if (item == null || IsPlaceholderItem(item))
            {
                return false;
            }

            if (!TryPrepareContextMenuTarget(listView, item))
            {
                return false;
            }

            Point location = GetKeyboardMenuLocation(listView, item);
            if (ReferenceEquals(listView, lvActive) && item.Tag is DownloadTask downloadTask)
            {
                _selectedDownloadTask = downloadTask;
                ShowDownloadContextMenu(location);
                return true;
            }

            if (ReferenceEquals(listView, lvUpload) && item.Tag is UploadTask uploadTask)
            {
                _selectedUploadTask = uploadTask;
                ShowUploadContextMenu(location);
                return true;
            }

            if (ReferenceEquals(listView, lvCompleted))
            {
                if (item.Tag is DownloadTask completedDownloadTask)
                {
                    _selectedDownloadTask = completedDownloadTask;
                    ShowCompletedContextMenu(location, isDownload: true);
                    return true;
                }

                if (item.Tag is UploadTask completedUploadTask)
                {
                    _selectedUploadTask = completedUploadTask;
                    ShowCompletedContextMenu(location, isDownload: false);
                    return true;
                }
            }

            return false;
        }

        private static Point GetKeyboardMenuLocation(ListView listView, ListViewItem item)
        {
            Rectangle bounds = item.Bounds;
            int x = Math.Max(0, Math.Min(listView.ClientSize.Width - 1, bounds.Left + Math.Min(24, Math.Max(4, bounds.Width / 4))));
            int y = Math.Max(0, Math.Min(listView.ClientSize.Height - 1, bounds.Top + Math.Max(4, bounds.Height / 2)));
            return new Point(x, y);
        }

        #endregion

        #region 无障碍文本同步

        private void TrySyncListViewItemAccessibility(ListView listView, ListViewItem item, bool allowFocusedItemAccessibilityRefresh)
        {
            if (listView == null || item == null || item.Index < 0)
            {
                return;
            }

            if (!allowFocusedItemAccessibilityRefresh && listView.ContainsFocus && IsInteractionFocusItem(listView, item))
            {
                // 实时更新期间不刷新焦点项无障碍名称，避免进度变化导致持续朗读。
                return;
            }

            string speech = BuildAccessibleRowSpeech(listView, item);
            string previousSpeech = GetSubItemText(item, HiddenAccessibilityColumnIndex);
            if (string.Equals(previousSpeech, speech, StringComparison.Ordinal))
            {
                return;
            }

            SetSubItemText(item, HiddenAccessibilityColumnIndex, speech);
            if (string.IsNullOrWhiteSpace(speech))
            {
                return;
            }

            if (listView.IsHandleCreated)
            {
                AccessibilityPropertyService.TrySetListItemProperties(
                    listView.Handle,
                    item.Index,
                    speech,
                    (int)AccessibleRole.ListItem);
            }
        }

        private string BuildAccessibleRowSpeech(ListView listView, ListViewItem item)
        {
            if (listView.Columns.Count == 0)
            {
                return GetPrimaryText(listView, item);
            }

            var parts = new List<string>();
            int max = Math.Min(listView.Columns.Count, item.SubItems.Count);
            for (int i = 0; i < max; i++)
            {
                if (i == HiddenAccessibilityColumnIndex)
                {
                    continue;
                }

                if (_hideTransferSequenceNumbers && i == SequenceColumnIndex)
                {
                    continue;
                }

                string value = item.SubItems[i].Text?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }

                parts.Add(value);
            }

            return parts.Count > 0 ? string.Join("，", parts) : GetPrimaryText(listView, item);
        }

        private static bool IsInteractionFocusItem(ListView listView, ListViewItem item)
        {
            if (item.Focused || item.Selected)
            {
                return true;
            }

            if (listView.FocusedItem != null && listView.FocusedItem.Index == item.Index)
            {
                return true;
            }

            return listView.SelectedIndices.Count > 0 && listView.SelectedIndices[0] == item.Index;
        }

        #endregion

        #region 文本格式化

        private static string GetActiveDownloadStatusText(DownloadStatus status)
        {
            switch (status)
            {
                case DownloadStatus.Pending:
                    return "等待下载";
                case DownloadStatus.Downloading:
                    return "进行中";
                case DownloadStatus.Paused:
                    return "已暂停";
                default:
                    return "进行中";
            }
        }

        private static string GetActiveUploadStatusText(UploadStatus status)
        {
            switch (status)
            {
                case UploadStatus.Pending:
                    return "等待下载";
                case UploadStatus.Uploading:
                    return "进行中";
                case UploadStatus.Paused:
                    return "已暂停";
                default:
                    return "进行中";
            }
        }

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

        private static string FormatDurationFromSeconds(int durationSeconds)
        {
            if (durationSeconds <= 0)
            {
                return "--:--";
            }

            int hours = durationSeconds / 3600;
            int minutes = (durationSeconds % 3600) / 60;
            int seconds = durationSeconds % 60;
            if (hours > 0)
            {
                return $"{hours:D2}:{minutes:D2}:{seconds:D2}";
            }

            return $"{minutes:D2}:{seconds:D2}";
        }

        private static string GetDownloadDurationText(DownloadTask task)
        {
            if (task?.Song == null || task.Song.Duration <= 0)
            {
                return "--:--";
            }

            return task.Song.FormattedDuration;
        }

        private static string GetDownloadViewText(DownloadTask task)
        {
            if (task == null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(task.SourceList))
            {
                return task.SourceList;
            }

            if (task.Song?.IsPodcastEpisode == true && !string.IsNullOrWhiteSpace(task.Song.PodcastRadioName))
            {
                return task.Song.PodcastRadioName;
            }

            return "下载";
        }

        private string GetDownloadProgressText(DownloadTask task)
        {
            if (task == null)
            {
                return string.Empty;
            }

            double percent = task.ProgressPercentage;
            if (task.Status == DownloadStatus.Completed)
            {
                percent = 100d;
            }

            if (percent < 0d)
            {
                percent = 0d;
            }
            else if (percent > 100d)
            {
                percent = 100d;
            }

            return $"{percent:F1}%";
        }

        private static string GetDownloadSpeedText(DownloadTask task)
        {
            if (task == null || task.Status != DownloadStatus.Downloading)
            {
                return "--";
            }

            return task.FormattedSpeed;
        }

        private string GetUploadProgressText(UploadTask task)
        {
            if (task == null)
            {
                return string.Empty;
            }

            double percent = task.ProgressPercentage;
            if (task.Status == UploadStatus.Completed)
            {
                percent = 100d;
            }

            if (percent < 0d)
            {
                percent = 0d;
            }
            else if (percent > 100d)
            {
                percent = 100d;
            }

            return $"{percent:F1}%";
        }

        private static string GetUploadSpeedText(UploadTask task)
        {
            if (task == null || task.Status != UploadStatus.Uploading)
            {
                return "--";
            }

            return task.FormattedSpeed;
        }

        private static string GetUploadTargetViewText(UploadTask task)
        {
            if (task == null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(task.SourceList))
            {
                return task.SourceList;
            }

            return "云盘";
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
                else if (_selectedUploadTask.Status == UploadStatus.Paused)
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

        #region 窗体关闭

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _refreshTimer.Stop();

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
    }
}
