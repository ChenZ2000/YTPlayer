using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Windows.Forms.Automation;
using YTPlayer.Core;
using YTPlayer.Utils;
using MessageBox = YTPlayer.MessageBox;

namespace YTPlayer.Forms.Download
{
    /// <summary>
    /// 批量下载选择对话框
    /// </summary>
    public partial class BatchDownloadDialog : Form
    {
        private const int TypeSearchTimeoutMs = 900;
        private const int NvdaCheckIntervalMs = 1000;
        private const int NavigationDebounceIntervalMs = 32;
        private const int NavigationBurstThresholdMs = 120;
        private const int LiveUpdateFlushIntervalMs = 120;
        private const int LiveUpdateMaxPerPass = 96;
        private const string LoadingPlaceholderText = "正在加载 ...";
        private static readonly MethodInfo? AccessibilityNotifyClientsMethod = typeof(Control).GetMethod(
            "AccessibilityNotifyClients",
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            new[] { typeof(AccessibleEvents), typeof(int) },
            null);

        private readonly ConfigManager _configManager;
        private readonly List<string> _itemTitles;
        private bool _hideSequenceNumbers;
        private string _typeSearchBuffer = string.Empty;
        private DateTime _typeSearchLastInputUtc = DateTime.MinValue;
        private DateTime _lastNvdaCheckAt = DateTime.MinValue;
        private bool _isNvdaRunningCached;
        private DateTime _lastNavigationInputUtc = DateTime.MinValue;
        private bool _navigationBurstActive;
        private int _bufferedNavigationDelta;
        private int _pendingAbsoluteNavigationIndex = -1;
        private readonly Dictionary<int, string> _pendingTitleUpdates = new Dictionary<int, string>();
        private System.Windows.Forms.Timer? _navigationDebounceTimer;
        private System.Windows.Forms.Timer? _liveUpdateTimer;
        private bool _isDisposed;
        private Label? _accessibilityAnnouncementLabel;
        private string _lastFocusedRefreshAnnouncementText = string.Empty;
        private DateTime _lastFocusedRefreshAnnouncementAt = DateTime.MinValue;
        private bool _suppressItemCheckLabelRefresh;
        private bool _pendingLabelUpdateAfterBulkCheck;
        private const string PerfLogCategory = "BatchDlgPerf";
#if DEBUG
        private string _activeBulkCheckOperationName = string.Empty;
        private Stopwatch? _activeBulkCheckOperationStopwatch;
        private int _activeBulkCheckItemCheckEvents;
        private int _activeBulkCheckChangedEvents;
        private int _activeBulkCheckQueuedLabelUpdates;
        private int _activeBulkCheckSuppressedLabelUpdates;
        private int _activeBulkCheckSelectedIndexChangedEvents;
        private int _checkedListHandleCreatedCount;
        private int _checkedListHandleDestroyedCount;
#endif

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
            ThemeManager.ApplyTheme(this);

            _configManager = ConfigManager.Instance;
            _itemTitles = items != null ? new List<string>(items) : new List<string>();
            SelectedIndices = new List<int>();

            KeyPreview = true;
            Text = title;

            checkedListBox.KeyPress += CheckedListBox_KeyPress;
            checkedListBox.KeyDown += CheckedListBox_KeyDown;
            checkedListBox.KeyUp += CheckedListBox_KeyUp;
            KeyDown += BatchDownloadDialog_KeyDown;
            FormClosed += BatchDownloadDialog_FormClosed;

            AttachPerfDebugHooks();
            LoadAccessibilitySettings();
            RebuildItems(preserveChecks: false, preferredSelectedIndex: 0, reason: "Initialize");
        }

        #endregion

        #region 事件处理

        private void BtnSelectAll_Click(object? sender, EventArgs e)
        {
            ApplyBulkCheckOperation("SelectAll", current => true);
        }

        private void BtnUnselectAll_Click(object? sender, EventArgs e)
        {
            ApplyBulkCheckOperation("UnselectAll", current => false);
        }

        private void BtnInvertSelection_Click(object? sender, EventArgs e)
        {
            ApplyBulkCheckOperation("InvertSelection", current => !current);
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
            TrackBulkCheckItemCheck(e);

            // ItemCheck 事件在状态改变之前触发，需要延迟更新
            // 确保窗口句柄已创建后再调用 BeginInvoke
            if (IsHandleCreated)
            {
                if (_suppressItemCheckLabelRefresh)
                {
                    _pendingLabelUpdateAfterBulkCheck = true;
                    TrackBulkLabelUpdateSuppressed();
                }
                else
                {
                    TrackBulkLabelUpdateQueued();
                    BeginInvoke(new Action(UpdateLabel));
                }
            }
        }

        private void BatchDownloadDialog_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.F8)
            {
                return;
            }

            e.Handled = true;
            e.SuppressKeyPress = true;
            ToggleSequenceNumberHidden();
        }

        private void CheckedListBox_KeyPress(object? sender, KeyPressEventArgs e)
        {
            if (!checkedListBox.ContainsFocus || checkedListBox.Items.Count == 0)
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

            FlushBufferedNavigation(resetBurstState: false);

            DateTime now = DateTime.UtcNow;
            if (_typeSearchLastInputUtc == DateTime.MinValue ||
                (now - _typeSearchLastInputUtc).TotalMilliseconds > TypeSearchTimeoutMs)
            {
                _typeSearchBuffer = string.Empty;
            }

            _typeSearchLastInputUtc = now;
            _typeSearchBuffer += e.KeyChar;

            int startIndex = checkedListBox.SelectedIndex;
            if (startIndex < 0)
            {
                startIndex = 0;
            }

            int targetIndex = FindItemIndexByPrimaryText(_typeSearchBuffer, startIndex);
            if (targetIndex < 0 && _typeSearchBuffer.Length > 1)
            {
                _typeSearchBuffer = e.KeyChar.ToString();
                targetIndex = FindItemIndexByPrimaryText(_typeSearchBuffer, startIndex);
            }

            if (targetIndex >= 0)
            {
                SelectItem(targetIndex);
            }

            e.Handled = true;
        }

        private void CheckedListBox_KeyDown(object? sender, KeyEventArgs e)
        {
            bool shouldDebounce = ShouldDebounceNavigation(e);
            if (!checkedListBox.ContainsFocus || checkedListBox.Items.Count == 0 || !shouldDebounce)
            {
                return;
            }

            if (!TryGetNavigationRequest(e.KeyCode, out int delta, out int absoluteIndex))
            {
                return;
            }

            BufferNavigationRequest(delta, absoluteIndex);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        private void CheckedListBox_KeyUp(object? sender, KeyEventArgs e)
        {
            if (!IsNavigationKey(e.KeyCode))
            {
                return;
            }

            FlushBufferedNavigation(resetBurstState: true);
        }

        private void BatchDownloadDialog_FormClosed(object? sender, FormClosedEventArgs e)
        {
            _isDisposed = true;

            if (_navigationDebounceTimer != null)
            {
                _navigationDebounceTimer.Stop();
                _navigationDebounceTimer.Dispose();
                _navigationDebounceTimer = null;
            }

            if (_liveUpdateTimer != null)
            {
                _liveUpdateTimer.Stop();
                _liveUpdateTimer.Dispose();
                _liveUpdateTimer = null;
            }
        }

        private void CheckedListBox_SelectedIndexChanged(object? sender, EventArgs e)
        {
            TrackBulkSelectedIndexChanged();
        }

        private void CheckedListBox_HandleCreated(object? sender, EventArgs e)
        {
#if DEBUG
            _checkedListHandleCreatedCount++;
            LogPerf(
                $"CheckedListBox.HandleCreated created={_checkedListHandleCreatedCount} destroyed={_checkedListHandleDestroyedCount} handle={GetCheckedListBoxHandleForLog()} items={checkedListBox.Items.Count} selected={checkedListBox.SelectedIndex}");
#endif
        }

        private void CheckedListBox_HandleDestroyed(object? sender, EventArgs e)
        {
#if DEBUG
            _checkedListHandleDestroyedCount++;
            bool disposing = Disposing || _isDisposed || checkedListBox.IsDisposed;
            LogPerf(
                $"CheckedListBox.HandleDestroyed created={_checkedListHandleCreatedCount} destroyed={_checkedListHandleDestroyedCount} disposing={disposing}");
#endif
        }

        #endregion

        #region 辅助方法

        [Conditional("DEBUG")]
        private void AttachPerfDebugHooks()
        {
            checkedListBox.HandleCreated += CheckedListBox_HandleCreated;
            checkedListBox.HandleDestroyed += CheckedListBox_HandleDestroyed;
            checkedListBox.SelectedIndexChanged += CheckedListBox_SelectedIndexChanged;
            LogPerf(
                $"DialogInit items={_itemTitles.Count} hideSequence={_hideSequenceNumbers} handle={GetCheckedListBoxHandleForLog()}");
        }

        [Conditional("DEBUG")]
        private void BeginBulkCheckOperationTrace(string operationName)
        {
#if DEBUG
            _activeBulkCheckOperationName = operationName ?? string.Empty;
            _activeBulkCheckOperationStopwatch = Stopwatch.StartNew();
            _activeBulkCheckItemCheckEvents = 0;
            _activeBulkCheckChangedEvents = 0;
            _activeBulkCheckQueuedLabelUpdates = 0;
            _activeBulkCheckSuppressedLabelUpdates = 0;
            _activeBulkCheckSelectedIndexChangedEvents = 0;
            LogPerf(
                $"BulkCheck.Start op={_activeBulkCheckOperationName} items={checkedListBox.Items.Count} checked={checkedListBox.CheckedItems.Count} selected={checkedListBox.SelectedIndex} top={checkedListBox.TopIndex} handle={GetCheckedListBoxHandleForLog()}");
#endif
        }

        [Conditional("DEBUG")]
        private void EndBulkCheckOperationTrace()
        {
#if DEBUG
            if (string.IsNullOrEmpty(_activeBulkCheckOperationName))
            {
                return;
            }

            _activeBulkCheckOperationStopwatch?.Stop();
            long elapsedMs = _activeBulkCheckOperationStopwatch?.ElapsedMilliseconds ?? -1;
            LogPerf(
                $"BulkCheck.End op={_activeBulkCheckOperationName} elapsedMs={elapsedMs} itemCheckEvents={_activeBulkCheckItemCheckEvents} changedEvents={_activeBulkCheckChangedEvents} queuedLabelUpdates={_activeBulkCheckQueuedLabelUpdates} suppressedLabelUpdates={_activeBulkCheckSuppressedLabelUpdates} selectedChangedEvents={_activeBulkCheckSelectedIndexChangedEvents} checked={checkedListBox.CheckedItems.Count}/{checkedListBox.Items.Count} selected={checkedListBox.SelectedIndex} top={checkedListBox.TopIndex} handle={GetCheckedListBoxHandleForLog()}");

            _activeBulkCheckOperationName = string.Empty;
            _activeBulkCheckOperationStopwatch = null;
            _activeBulkCheckItemCheckEvents = 0;
            _activeBulkCheckChangedEvents = 0;
            _activeBulkCheckQueuedLabelUpdates = 0;
            _activeBulkCheckSuppressedLabelUpdates = 0;
            _activeBulkCheckSelectedIndexChangedEvents = 0;
#endif
        }

        [Conditional("DEBUG")]
        private void TrackBulkCheckItemCheck(ItemCheckEventArgs e)
        {
#if DEBUG
            if (string.IsNullOrEmpty(_activeBulkCheckOperationName))
            {
                return;
            }

            _activeBulkCheckItemCheckEvents++;
            if (e.CurrentValue != e.NewValue)
            {
                _activeBulkCheckChangedEvents++;
            }
#endif
        }

        [Conditional("DEBUG")]
        private void TrackBulkLabelUpdateQueued()
        {
#if DEBUG
            if (!string.IsNullOrEmpty(_activeBulkCheckOperationName))
            {
                _activeBulkCheckQueuedLabelUpdates++;
            }
#endif
        }

        [Conditional("DEBUG")]
        private void TrackBulkLabelUpdateSuppressed()
        {
#if DEBUG
            if (!string.IsNullOrEmpty(_activeBulkCheckOperationName))
            {
                _activeBulkCheckSuppressedLabelUpdates++;
            }
#endif
        }

        [Conditional("DEBUG")]
        private void TrackBulkSelectedIndexChanged()
        {
#if DEBUG
            if (!string.IsNullOrEmpty(_activeBulkCheckOperationName))
            {
                _activeBulkCheckSelectedIndexChangedEvents++;
            }
#endif
        }

        private void BeginBulkLabelUpdateSuppression()
        {
            _pendingLabelUpdateAfterBulkCheck = false;
            _suppressItemCheckLabelRefresh = true;
        }

        private void EndBulkLabelUpdateSuppression(bool flushPendingUpdate)
        {
            _suppressItemCheckLabelRefresh = false;
            if (flushPendingUpdate && _pendingLabelUpdateAfterBulkCheck)
            {
                UpdateLabel();
            }

            _pendingLabelUpdateAfterBulkCheck = false;
        }

        private void ApplyBulkCheckOperation(string operationName, Func<bool, bool> resolveTargetState)
        {
            if (resolveTargetState == null || checkedListBox.Items.Count == 0)
            {
                return;
            }

            BeginBulkCheckOperationTrace(operationName);
            BeginBulkLabelUpdateSuppression();
            checkedListBox.BeginUpdate();
            try
            {
                for (int i = 0; i < checkedListBox.Items.Count; i++)
                {
                    bool currentChecked = checkedListBox.GetItemChecked(i);
                    bool targetChecked = resolveTargetState(currentChecked);
                    if (targetChecked == currentChecked)
                    {
                        continue;
                    }

                    checkedListBox.SetItemChecked(i, targetChecked);
                }
            }
            finally
            {
                checkedListBox.EndUpdate();
                EndBulkLabelUpdateSuppression(flushPendingUpdate: false);
                UpdateLabel();
                FocusCheckedListBox();
                EndBulkCheckOperationTrace();
            }
        }

        private string GetCheckedListBoxHandleForLog()
        {
            if (checkedListBox == null || checkedListBox.IsDisposed || !checkedListBox.IsHandleCreated)
            {
                return "0x0";
            }

            return $"0x{checkedListBox.Handle.ToInt64():X}";
        }

        [Conditional("DEBUG")]
        private void LogPerf(string message)
        {
            DebugLogger.Log(DebugLogger.LogLevel.Info, PerfLogCategory, message);
        }

        public int GetPreferredLoadIndex()
        {
            if (_isDisposed)
            {
                return -1;
            }

            if (InvokeRequired)
            {
                try
                {
                    return (int)Invoke(new Func<int>(GetPreferredLoadIndexCore));
                }
                catch
                {
                    return -1;
                }
            }

            return GetPreferredLoadIndexCore();
        }

        public void EnsureItemCount(int totalCount, bool defaultChecked = true)
        {
            if (totalCount <= 0 || _isDisposed)
            {
                return;
            }

            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke(new Action<int, bool>(EnsureItemCount), totalCount, defaultChecked);
                }
                catch
                {
                }
                return;
            }

            EnsureItemCountCore(totalCount, defaultChecked);
        }

        public void QueueItemTextUpdates(IReadOnlyDictionary<int, string> updates)
        {
            if (_isDisposed || updates == null || updates.Count == 0)
            {
                return;
            }

            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke(new Action<IReadOnlyDictionary<int, string>>(QueueItemTextUpdates), updates);
                }
                catch
                {
                }
                return;
            }

            foreach (KeyValuePair<int, string> update in updates)
            {
                int index = update.Key;
                if (index < 0)
                {
                    continue;
                }

                _pendingTitleUpdates[index] = NormalizeTitle(update.Value);
            }

            EnsureLiveUpdateTimer();
            _liveUpdateTimer!.Start();
        }

        private int GetPreferredLoadIndexCore()
        {
            if (_isDisposed || checkedListBox == null)
            {
                return -1;
            }

            return checkedListBox.SelectedIndex;
        }

        private bool ShouldDebounceNavigation(KeyEventArgs e)
        {
            if (e == null || !IsNavigationKey(e.KeyCode))
            {
                return false;
            }

            if ((ModifierKeys & (Keys.Control | Keys.Alt)) != 0)
            {
                return false;
            }

            return IsNvdaRunningCached();
        }

        private static bool IsNavigationKey(Keys key)
        {
            switch (key)
            {
                case Keys.Up:
                case Keys.Down:
                case Keys.PageUp:
                case Keys.PageDown:
                case Keys.Home:
                case Keys.End:
                    return true;
                default:
                    return false;
            }
        }

        private bool TryGetNavigationRequest(Keys key, out int delta, out int absoluteIndex)
        {
            delta = 0;
            absoluteIndex = -1;

            int pageStep = GetPageStep();
            int itemCount = checkedListBox.Items.Count;
            if (itemCount <= 0)
            {
                return false;
            }

            switch (key)
            {
                case Keys.Up:
                    delta = -1;
                    return true;
                case Keys.Down:
                    delta = 1;
                    return true;
                case Keys.PageUp:
                    delta = -pageStep;
                    return true;
                case Keys.PageDown:
                    delta = pageStep;
                    return true;
                case Keys.Home:
                    absoluteIndex = 0;
                    return true;
                case Keys.End:
                    absoluteIndex = itemCount - 1;
                    return true;
                default:
                    return false;
            }
        }

        private void BufferNavigationRequest(int delta, int absoluteIndex)
        {
            DateTime now = DateTime.UtcNow;
            bool isRapid = _lastNavigationInputUtc != DateTime.MinValue &&
                (now - _lastNavigationInputUtc).TotalMilliseconds <= NavigationBurstThresholdMs;

            _lastNavigationInputUtc = now;
            _navigationBurstActive = _navigationBurstActive || isRapid;

            if (absoluteIndex >= 0)
            {
                _pendingAbsoluteNavigationIndex = absoluteIndex;
                _bufferedNavigationDelta = 0;
            }
            else
            {
                _bufferedNavigationDelta = Math.Max(-checkedListBox.Items.Count,
                    Math.Min(checkedListBox.Items.Count, checked(_bufferedNavigationDelta + delta)));
            }

            EnsureNavigationDebounceTimer();
            _navigationDebounceTimer!.Interval = NavigationDebounceIntervalMs;
            _navigationDebounceTimer.Stop();
            _navigationDebounceTimer.Start();
        }

        private void EnsureNavigationDebounceTimer()
        {
            if (_navigationDebounceTimer != null)
            {
                return;
            }

            _navigationDebounceTimer = new System.Windows.Forms.Timer
            {
                Interval = NavigationDebounceIntervalMs
            };
            _navigationDebounceTimer.Tick += NavigationDebounceTimer_Tick;
        }

        private void NavigationDebounceTimer_Tick(object? sender, EventArgs e)
        {
            _navigationDebounceTimer?.Stop();
            FlushBufferedNavigation(resetBurstState: false);
        }

        private void FlushBufferedNavigation(bool resetBurstState)
        {
            if (_isDisposed || checkedListBox.Items.Count <= 0)
            {
                _bufferedNavigationDelta = 0;
                _pendingAbsoluteNavigationIndex = -1;
                return;
            }

            if (_pendingAbsoluteNavigationIndex < 0 && _bufferedNavigationDelta == 0)
            {
                if (resetBurstState)
                {
                    _navigationBurstActive = false;
                }
                return;
            }

            int currentIndex = checkedListBox.SelectedIndex;
            if (currentIndex < 0)
            {
                currentIndex = 0;
            }

            int targetIndex;
            if (_pendingAbsoluteNavigationIndex >= 0)
            {
                targetIndex = _pendingAbsoluteNavigationIndex;
            }
            else
            {
                targetIndex = checked(currentIndex + _bufferedNavigationDelta);
            }

            _bufferedNavigationDelta = 0;
            _pendingAbsoluteNavigationIndex = -1;

            targetIndex = Math.Max(0, Math.Min(targetIndex, checkedListBox.Items.Count - 1));
            SelectItem(targetIndex);

            if (resetBurstState)
            {
                _navigationBurstActive = false;
            }
        }

        private int GetPageStep()
        {
            int itemHeight = Math.Max(1, checkedListBox.ItemHeight);
            return Math.Max(1, checkedListBox.ClientSize.Height / itemHeight);
        }

        private bool IsNvdaRunningCached()
        {
            DateTime utcNow = DateTime.UtcNow;
            if ((utcNow - _lastNvdaCheckAt).TotalMilliseconds >= NvdaCheckIntervalMs)
            {
                _isNvdaRunningCached = IsNvdaRunning();
                _lastNvdaCheckAt = utcNow;
            }

            return _isNvdaRunningCached;
        }

        private static bool IsNvdaRunning()
        {
            try
            {
                return Process.GetProcessesByName("nvda").Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private void LoadAccessibilitySettings()
        {
            try
            {
                _hideSequenceNumbers = _configManager.Load().BatchDownloadDialogSequenceNumberHidden;
            }
            catch (Exception ex)
            {
                _hideSequenceNumbers = false;
                Debug.WriteLine($"[BatchDownloadDialog] 读取序号配置失败，使用默认值: {ex.Message}");
            }
        }

        private void SaveAccessibilitySettings()
        {
            try
            {
                var config = _configManager.Load();
                config.BatchDownloadDialogSequenceNumberHidden = _hideSequenceNumbers;
                _configManager.Save(config);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BatchDownloadDialog] 保存序号配置失败: {ex.Message}");
            }
        }

        private void ToggleSequenceNumberHidden()
        {
            int preferredSelectedIndex = checkedListBox.SelectedIndex;
#if DEBUG
            Stopwatch stopwatch = Stopwatch.StartNew();
            int beforeTopIndex = checkedListBox.TopIndex;
            int beforeItemCount = checkedListBox.Items.Count;
            int beforeCheckedCount = checkedListBox.CheckedItems.Count;
            string beforeHandle = GetCheckedListBoxHandleForLog();
#endif

            _hideSequenceNumbers = !_hideSequenceNumbers;
            SaveAccessibilitySettings();
#if DEBUG
            LogPerf(
                $"F8Toggle.Start hideSequence={_hideSequenceNumbers} items={beforeItemCount} checked={beforeCheckedCount} selected={preferredSelectedIndex} top={beforeTopIndex} handle={beforeHandle}");
#endif

            bool updatedInPlace = TryRefreshDisplayTextsInPlace(preferredSelectedIndex, reason: "F8Toggle");
            if (!updatedInPlace)
            {
                RebuildItems(preserveChecks: true, preferredSelectedIndex, reason: "F8ToggleFallback");
            }
            ResetTypeSearchBuffer();
            FocusCheckedListBox();
            if (checkedListBox.ContainsFocus && checkedListBox.SelectedIndex >= 0)
            {
                QueueFocusedItemRefreshAccessibilityEvents(checkedListBox.SelectedIndex);
                QueueFocusedItemRefreshAnnouncement(checkedListBox.SelectedIndex);
            }
#if DEBUG
            stopwatch.Stop();
            LogPerf(
                $"F8Toggle.End elapsedMs={stopwatch.ElapsedMilliseconds} hideSequence={_hideSequenceNumbers} inPlace={updatedInPlace} items={checkedListBox.Items.Count} checked={checkedListBox.CheckedItems.Count} selected={checkedListBox.SelectedIndex} top={checkedListBox.TopIndex} handle={GetCheckedListBoxHandleForLog()}");
#endif
        }

        private bool TryRefreshDisplayTextsInPlace(int preferredSelectedIndex, string reason)
        {
#if DEBUG
            Stopwatch stopwatch = Stopwatch.StartNew();
            LogPerf(
                $"RefreshInPlace.Start reason={reason} hideSequence={_hideSequenceNumbers} sourceCount={_itemTitles.Count} items={checkedListBox.Items.Count} selected={checkedListBox.SelectedIndex} top={checkedListBox.TopIndex} handle={GetCheckedListBoxHandleForLog()}");
#endif

            int itemCount = _itemTitles.Count;
            if (itemCount != checkedListBox.Items.Count)
            {
#if DEBUG
                stopwatch.Stop();
                LogPerf(
                    $"RefreshInPlace.Skip reason={reason} sourceCount={itemCount} items={checkedListBox.Items.Count}");
#endif
                return false;
            }

            int changedItems = 0;
            int replacedItems = 0;
            checkedListBox.BeginUpdate();
            try
            {
                for (int i = 0; i < itemCount; i++)
                {
                    string title = NormalizeTitle(_itemTitles[i]);
                    _itemTitles[i] = title;
                    string displayText = BuildDisplayText(title, i);

                    if (checkedListBox.Items[i] is BatchDownloadListItem listItem)
                    {
                        if (string.Equals(listItem.DisplayText, displayText, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        listItem.DisplayText = displayText;
                        changedItems++;
                        continue;
                    }

                    bool wasChecked = checkedListBox.GetItemChecked(i);
                    checkedListBox.Items[i] = new BatchDownloadListItem(displayText);
                    if (checkedListBox.GetItemChecked(i) != wasChecked)
                    {
                        checkedListBox.SetItemChecked(i, wasChecked);
                    }

                    changedItems++;
                    replacedItems++;
                }
            }
            finally
            {
                checkedListBox.EndUpdate();
            }

            if (itemCount > 0)
            {
                int targetIndex = preferredSelectedIndex;
                if (targetIndex < 0 || targetIndex >= itemCount)
                {
                    targetIndex = 0;
                }

                if (checkedListBox.SelectedIndex != targetIndex)
                {
                    checkedListBox.SelectedIndex = targetIndex;
                }
                EnsureVisible(targetIndex);
            }

            if (changedItems > 0)
            {
                checkedListBox.Invalidate();
            }

            UpdateLabel();
#if DEBUG
            stopwatch.Stop();
            LogPerf(
                $"RefreshInPlace.End reason={reason} elapsedMs={stopwatch.ElapsedMilliseconds} changedItems={changedItems} replacedItems={replacedItems} items={checkedListBox.Items.Count} checked={checkedListBox.CheckedItems.Count} selected={checkedListBox.SelectedIndex} top={checkedListBox.TopIndex} handle={GetCheckedListBoxHandleForLog()}");
#endif
            return true;
        }

        private void RebuildItems(bool preserveChecks, int preferredSelectedIndex, string reason = "General")
        {
#if DEBUG
            Stopwatch stopwatch = Stopwatch.StartNew();
            int beforeItemCount = checkedListBox.Items.Count;
            int beforeCheckedCount = checkedListBox.CheckedItems.Count;
            int beforeSelectedIndex = checkedListBox.SelectedIndex;
            int beforeTopIndex = checkedListBox.TopIndex;
            string beforeHandle = GetCheckedListBoxHandleForLog();
            LogPerf(
                $"Rebuild.Start reason={reason} preserveChecks={preserveChecks} sourceCount={_itemTitles.Count} items={beforeItemCount} checked={beforeCheckedCount} selected={beforeSelectedIndex} top={beforeTopIndex} handle={beforeHandle}");
#endif

            int itemCount = _itemTitles.Count;
            bool[] checkedStates = new bool[itemCount];
            if (preserveChecks)
            {
                int existingCount = checkedListBox.Items.Count;
                for (int i = 0; i < itemCount; i++)
                {
                    checkedStates[i] = i < existingCount ? checkedListBox.GetItemChecked(i) : true;
                }
            }
            else
            {
                for (int i = 0; i < itemCount; i++)
                {
                    checkedStates[i] = true;
                }
            }

            checkedListBox.BeginUpdate();
            try
            {
                checkedListBox.Items.Clear();
                for (int i = 0; i < itemCount; i++)
                {
                    string title = NormalizeTitle(_itemTitles[i]);
                    _itemTitles[i] = title;
                    string displayText = BuildDisplayText(title, i);
                    checkedListBox.Items.Add(new BatchDownloadListItem(displayText), checkedStates[i]);
                }
            }
            finally
            {
                checkedListBox.EndUpdate();
            }

            if (itemCount > 0)
            {
                int targetIndex = preferredSelectedIndex;
                if (targetIndex < 0 || targetIndex >= itemCount)
                {
                    targetIndex = 0;
                }

                SelectItem(targetIndex);
            }

            UpdateLabel();

#if DEBUG
            stopwatch.Stop();
            LogPerf(
                $"Rebuild.End reason={reason} elapsedMs={stopwatch.ElapsedMilliseconds} items={checkedListBox.Items.Count} checked={checkedListBox.CheckedItems.Count} selected={checkedListBox.SelectedIndex} top={checkedListBox.TopIndex} handle={GetCheckedListBoxHandleForLog()}");
#endif
        }

        private void EnsureItemCountCore(int totalCount, bool defaultChecked)
        {
            totalCount = Math.Max(0, totalCount);
            if (totalCount <= _itemTitles.Count)
            {
                return;
            }

            checkedListBox.BeginUpdate();
            try
            {
                for (int i = _itemTitles.Count; i < totalCount; i++)
                {
                    _itemTitles.Add(LoadingPlaceholderText);
                    checkedListBox.Items.Add(new BatchDownloadListItem(BuildDisplayText(LoadingPlaceholderText, i)), defaultChecked);
                }
            }
            finally
            {
                checkedListBox.EndUpdate();
            }

            if (checkedListBox.SelectedIndex < 0 && checkedListBox.Items.Count > 0)
            {
                SelectItem(0);
            }

            UpdateLabel();
        }

        private static string NormalizeTitle(string? title)
        {
            string value = (title ?? string.Empty).Trim();
            return string.IsNullOrEmpty(value) ? LoadingPlaceholderText : value;
        }

        private void EnsureLiveUpdateTimer()
        {
            if (_liveUpdateTimer != null)
            {
                return;
            }

            _liveUpdateTimer = new System.Windows.Forms.Timer
            {
                Interval = LiveUpdateFlushIntervalMs
            };
            _liveUpdateTimer.Tick += LiveUpdateTimer_Tick;
        }

        private void LiveUpdateTimer_Tick(object? sender, EventArgs e)
        {
            if (_isDisposed)
            {
                _liveUpdateTimer?.Stop();
                return;
            }

            ApplyPendingTitleUpdates();
            if (_pendingTitleUpdates.Count == 0)
            {
                _liveUpdateTimer?.Stop();
            }
        }

        private void ApplyPendingTitleUpdates()
        {
            if (_pendingTitleUpdates.Count == 0)
            {
                return;
            }

            int maxIndex = _pendingTitleUpdates.Keys.Max();
            if (maxIndex >= _itemTitles.Count)
            {
                EnsureItemCountCore(checked(maxIndex + 1), defaultChecked: true);
            }

            List<int> updateIndices = OrderUpdateIndicesByPriority(_pendingTitleUpdates.Keys);
            if (updateIndices.Count == 0)
            {
                _pendingTitleUpdates.Clear();
                return;
            }

            int selectedIndex = checkedListBox.SelectedIndex;
            bool hasChanges = false;
            bool selectedItemUpdated = false;
            int applyCount = Math.Min(updateIndices.Count, LiveUpdateMaxPerPass);

            checkedListBox.BeginUpdate();
            try
            {
                for (int i = 0; i < applyCount; i++)
                {
                    int index = updateIndices[i];
                    if (index < 0 || index >= _itemTitles.Count)
                    {
                        _pendingTitleUpdates.Remove(index);
                        continue;
                    }

                    if (!_pendingTitleUpdates.TryGetValue(index, out string? updatedTitle))
                    {
                        continue;
                    }

                    updatedTitle = NormalizeTitle(updatedTitle);
                    _pendingTitleUpdates.Remove(index);

                    if (string.Equals(_itemTitles[index], updatedTitle, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    _itemTitles[index] = updatedTitle;
                    if (index >= 0 && index < checkedListBox.Items.Count)
                    {
                        string after = BuildDisplayText(updatedTitle, index);
                        bool itemChanged = TryUpdateItemDisplayText(index, after);
                        if (itemChanged && index == selectedIndex)
                        {
                            selectedItemUpdated = true;
                        }
                        if (itemChanged)
                        {
                            hasChanges = true;
                        }
                    }
                }
            }
            finally
            {
                checkedListBox.EndUpdate();
            }

            if (hasChanges && selectedIndex >= 0 && selectedIndex < checkedListBox.Items.Count)
            {
                checkedListBox.SelectedIndex = selectedIndex;
                EnsureVisible(selectedIndex);
                if (selectedItemUpdated)
                {
                    QueueFocusedItemRefreshAccessibilityEvents(selectedIndex);
                    QueueFocusedItemRefreshAnnouncement(selectedIndex);
                }
            }
        }

        private bool TryUpdateItemDisplayText(int index, string displayText)
        {
            if (index < 0 || index >= checkedListBox.Items.Count)
            {
                return false;
            }

            if (checkedListBox.Items[index] is BatchDownloadListItem listItem)
            {
                if (string.Equals(listItem.DisplayText, displayText, StringComparison.Ordinal))
                {
                    return false;
                }

                listItem.DisplayText = displayText;
                InvalidateItemVisual(index);
                return true;
            }

            string currentText = checkedListBox.GetItemText(checkedListBox.Items[index]) ?? string.Empty;
            if (string.Equals(currentText, displayText, StringComparison.Ordinal))
            {
                return false;
            }

            bool wasChecked = checkedListBox.GetItemChecked(index);
            checkedListBox.Items[index] = new BatchDownloadListItem(displayText);
            if (checkedListBox.GetItemChecked(index) != wasChecked)
            {
                checkedListBox.SetItemChecked(index, wasChecked);
            }

            InvalidateItemVisual(index);
            return true;
        }

        private void InvalidateItemVisual(int index)
        {
            if (index < 0 || index >= checkedListBox.Items.Count)
            {
                return;
            }

            try
            {
                checkedListBox.Invalidate(checkedListBox.GetItemRectangle(index));
            }
            catch
            {
                checkedListBox.Invalidate();
            }
        }

        private void QueueFocusedItemRefreshAccessibilityEvents(int selectedIndex)
        {
            if (_isDisposed || selectedIndex < 0)
            {
                return;
            }

            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke(new Action<int>(QueueFocusedItemRefreshAccessibilityEvents), selectedIndex);
                }
                catch
                {
                }
                return;
            }

            try
            {
                BeginInvoke(new Action<int>(RaiseFocusedItemRefreshAccessibilityEventsCore), selectedIndex);
            }
            catch
            {
            }
        }

        private void QueueFocusedItemRefreshAnnouncement(int selectedIndex)
        {
            if (_isDisposed || selectedIndex < 0)
            {
                return;
            }

            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke(new Action<int>(QueueFocusedItemRefreshAnnouncement), selectedIndex);
                }
                catch
                {
                }
                return;
            }

            try
            {
                BeginInvoke(new Action<int>(RaiseFocusedItemRefreshAnnouncementCore), selectedIndex);
            }
            catch
            {
            }
        }

        private void RaiseFocusedItemRefreshAnnouncementCore(int selectedIndex)
        {
            if (_isDisposed || selectedIndex < 0 || selectedIndex >= checkedListBox.Items.Count)
            {
                return;
            }

            if (!checkedListBox.ContainsFocus)
            {
                return;
            }

            string speech = GetListItemDisplayText(selectedIndex).Trim();
            if (string.IsNullOrWhiteSpace(speech))
            {
                return;
            }

            DateTime utcNow = DateTime.UtcNow;
            if (string.Equals(_lastFocusedRefreshAnnouncementText, speech, StringComparison.Ordinal) &&
                (utcNow - _lastFocusedRefreshAnnouncementAt).TotalMilliseconds < 700)
            {
                return;
            }

            _lastFocusedRefreshAnnouncementText = speech;
            _lastFocusedRefreshAnnouncementAt = utcNow;

            EnsureAccessibilityAnnouncementLabel();
            if (_accessibilityAnnouncementLabel == null || _accessibilityAnnouncementLabel.IsDisposed)
            {
                return;
            }

            _accessibilityAnnouncementLabel.Text = speech;
            _accessibilityAnnouncementLabel.AccessibleName = speech;
            _accessibilityAnnouncementLabel.AccessibleDescription = speech;

            try
            {
                _accessibilityAnnouncementLabel.AccessibilityObject.RaiseAutomationNotification(
                    AutomationNotificationKind.Other,
                    AutomationNotificationProcessing.CurrentThenMostRecent,
                    speech);
            }
            catch
            {
            }

            try
            {
                AccessibilityNotifyClientsMethod?.Invoke(_accessibilityAnnouncementLabel, new object[] { AccessibleEvents.NameChange, -1 });
                AccessibilityNotifyClientsMethod?.Invoke(_accessibilityAnnouncementLabel, new object[] { AccessibleEvents.ValueChange, -1 });
            }
            catch
            {
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
                Name = "batchDownloadAccessibilityAnnouncementLabel",
                TabStop = false,
                AutoSize = false,
                Size = new System.Drawing.Size(1, 1),
                Location = new System.Drawing.Point(-2000, -2000),
                Text = string.Empty,
                AccessibleName = string.Empty,
                AccessibleRole = AccessibleRole.StaticText
            };
            Controls.Add(_accessibilityAnnouncementLabel);
        }

        private void RaiseFocusedItemRefreshAccessibilityEventsCore(int selectedIndex)
        {
            if (_isDisposed || selectedIndex < 0 || selectedIndex >= checkedListBox.Items.Count || !checkedListBox.IsHandleCreated)
            {
                return;
            }

            RaiseFocusedItemRefreshManagedAccessibilityEvents(selectedIndex);

            IntPtr handle = checkedListBox.Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            int childId = checked(selectedIndex + 1);
            try
            {
                AccessibilityNativeMethods.NotifyWinEvent(AccessibilityNativeMethods.EVENT_OBJECT_NAMECHANGE, handle, AccessibilityNativeMethods.OBJID_CLIENT, childId);
                AccessibilityNativeMethods.NotifyWinEvent(AccessibilityNativeMethods.EVENT_OBJECT_SELECTION, handle, AccessibilityNativeMethods.OBJID_CLIENT, childId);
                AccessibilityNativeMethods.NotifyWinEvent(AccessibilityNativeMethods.EVENT_OBJECT_SELECTIONADD, handle, AccessibilityNativeMethods.OBJID_CLIENT, childId);
                AccessibilityNativeMethods.NotifyWinEvent(AccessibilityNativeMethods.EVENT_OBJECT_FOCUS, handle, AccessibilityNativeMethods.OBJID_CLIENT, childId);
            }
            catch
            {
            }
        }

        private void RaiseFocusedItemRefreshManagedAccessibilityEvents(int selectedIndex)
        {
            if (AccessibilityNotifyClientsMethod == null || selectedIndex < 0)
            {
                return;
            }

            try
            {
                AccessibilityNotifyClientsMethod.Invoke(checkedListBox, new object[] { AccessibleEvents.NameChange, selectedIndex });
                AccessibilityNotifyClientsMethod.Invoke(checkedListBox, new object[] { AccessibleEvents.Selection, selectedIndex });
                AccessibilityNotifyClientsMethod.Invoke(checkedListBox, new object[] { AccessibleEvents.SelectionAdd, selectedIndex });
                AccessibilityNotifyClientsMethod.Invoke(checkedListBox, new object[] { AccessibleEvents.Focus, selectedIndex });
            }
            catch
            {
            }
        }

        private static class AccessibilityNativeMethods
        {
            public const uint EVENT_OBJECT_FOCUS = 0x8005;
            public const uint EVENT_OBJECT_SELECTION = 0x8006;
            public const uint EVENT_OBJECT_SELECTIONADD = 0x8007;
            public const uint EVENT_OBJECT_NAMECHANGE = 0x800C;
            public const int OBJID_CLIENT = unchecked((int)0xFFFFFFFC);

            [DllImport("user32.dll")]
            public static extern void NotifyWinEvent(uint eventId, IntPtr hwnd, int idObject, int idChild);
        }

        private List<int> OrderUpdateIndicesByPriority(IEnumerable<int> indices)
        {
            List<int> list = indices.Distinct().ToList();
            if (list.Count <= 1)
            {
                return list;
            }

            int focusIndex = checkedListBox.SelectedIndex;
            if (focusIndex < 0)
            {
                focusIndex = 0;
            }

            GetVisibleRange(out int visibleStart, out int visibleEnd);
            return list
                .OrderBy(index => index == focusIndex ? 0 : ((index >= visibleStart && index <= visibleEnd) ? 1 : 2))
                .ThenBy(index => Math.Abs(index - focusIndex))
                .ToList();
        }

        private void GetVisibleRange(out int start, out int end)
        {
            int itemHeight = Math.Max(1, checkedListBox.ItemHeight);
            int visibleCount = Math.Max(1, checkedListBox.ClientSize.Height / itemHeight);
            start = Math.Max(0, checkedListBox.TopIndex);
            end = Math.Max(start, checked(start + visibleCount - 1));
        }

        private string GetListItemDisplayText(int index)
        {
            if (index < 0 || index >= checkedListBox.Items.Count)
            {
                return string.Empty;
            }

            if (checkedListBox.Items[index] is BatchDownloadListItem listItem)
            {
                return listItem.DisplayText ?? string.Empty;
            }

            return checkedListBox.GetItemText(checkedListBox.Items[index]) ?? string.Empty;
        }

        private string BuildDisplayText(string title, int index)
        {
            if (_hideSequenceNumbers)
            {
                return title;
            }

            int sequence = checked(index + 1);
            return $"{sequence.ToString(CultureInfo.CurrentCulture)}. {title}";
        }

        private sealed class BatchDownloadListItem
        {
            public BatchDownloadListItem(string displayText)
            {
                DisplayText = displayText ?? string.Empty;
            }

            public string DisplayText { get; set; }

            public override string ToString()
            {
                return DisplayText;
            }
        }

        private int FindItemIndexByPrimaryText(string searchPrefix, int startIndex)
        {
            if (string.IsNullOrWhiteSpace(searchPrefix))
            {
                return -1;
            }

            int total = _itemTitles.Count;
            if (total <= 0)
            {
                return -1;
            }

            int start = startIndex;
            if (start < 0 || start >= total)
            {
                start = 0;
            }

            StringComparison comparison = StringComparison.OrdinalIgnoreCase;
            for (int i = start; i < total; i++)
            {
                string candidate = GetItemPrimaryText(i);
                if (!string.IsNullOrEmpty(candidate) && candidate.StartsWith(searchPrefix, comparison))
                {
                    return i;
                }
            }

            for (int i = 0; i < start; i++)
            {
                string candidate = GetItemPrimaryText(i);
                if (!string.IsNullOrEmpty(candidate) && candidate.StartsWith(searchPrefix, comparison))
                {
                    return i;
                }
            }

            return -1;
        }

        private string GetItemPrimaryText(int index)
        {
            if (index < 0 || index >= _itemTitles.Count)
            {
                return string.Empty;
            }

            if (_hideSequenceNumbers)
            {
                return (_itemTitles[index] ?? string.Empty).Trim();
            }

            int sequence = checked(index + 1);
            return sequence.ToString(CultureInfo.CurrentCulture);
        }

        private void SelectItem(int targetIndex)
        {
            if (targetIndex < 0 || targetIndex >= checkedListBox.Items.Count)
            {
                return;
            }

            checkedListBox.SelectedIndex = targetIndex;
            EnsureVisible(targetIndex);
        }

        private void EnsureVisible(int targetIndex)
        {
            if (targetIndex < 0 || targetIndex >= checkedListBox.Items.Count)
            {
                return;
            }

            int itemHeight = Math.Max(1, checkedListBox.ItemHeight);
            int visibleCount = Math.Max(1, checkedListBox.ClientSize.Height / itemHeight);
            int visibleStart = checkedListBox.TopIndex;
            int visibleEnd = checked(visibleStart + visibleCount - 1);

            if (targetIndex < visibleStart)
            {
                checkedListBox.TopIndex = targetIndex;
            }
            else if (targetIndex > visibleEnd)
            {
                checkedListBox.TopIndex = Math.Max(0, targetIndex - visibleCount + 1);
            }

        }

        private void FocusCheckedListBox()
        {
            FlushBufferedNavigation(resetBurstState: true);

            if (checkedListBox.Items.Count > 0 && checkedListBox.SelectedIndex < 0)
            {
                SelectItem(0);
            }

            if (checkedListBox.CanFocus)
            {
                checkedListBox.Focus();
            }

        }

        private void ResetTypeSearchBuffer()
        {
            _typeSearchBuffer = string.Empty;
            _typeSearchLastInputUtc = DateTime.MinValue;
        }

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
