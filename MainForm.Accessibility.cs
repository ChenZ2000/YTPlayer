#nullable disable

using System;

using System.Collections.Generic;

using System.Diagnostics;

using System.Drawing;

using System.Reflection;

using System.Runtime.InteropServices;

using System.Threading;

using System.Threading.Tasks;

using System.Windows.Forms;

using System.Windows.Forms.Automation;

using YTPlayer.Models;

using YTPlayer.Utils;



namespace YTPlayer

{

    public partial class MainForm

    {

        private static class NativeMethods

        {

                public struct LVITEM

                {

                        public uint mask;

                        public int iItem;

                        public int iSubItem;

                        public uint state;

                        public uint stateMask;

                        public nint pszText;

                        public int cchTextMax;

                        public int iImage;

                        public nint lParam;

                        public int iIndent;

                        public int iGroupId;

                        public uint cColumns;

                        public nint puColumns;

                        public nint piColFmt;

                        public int iGroup;

                }



                public const uint EVENT_OBJECT_FOCUS = 32773u;

                public const uint EVENT_OBJECT_SELECTION = 32774u;

                public const uint EVENT_OBJECT_NAMECHANGE = 32780u;

                public const uint EVENT_OBJECT_SHOW = 32770u;

                public const int OBJID_CLIENT = -4;

                public const int CHILDID_SELF = 0;

                public const int LVM_FIRST = 4096;

                public const int LVM_SETITEMSTATE = 4139;

                public const uint LVIF_STATE = 8u;

                public const uint LVIS_FOCUSED = 1u;

                public const uint LVIS_SELECTED = 2u;

                public const uint WM_KEYFIRST = 0x0100;

                public const uint WM_KEYDOWN = 0x0100;

                public const uint WM_SYSKEYDOWN = 0x0104;

                public const uint WM_KEYLAST = 0x0109;

                public const uint PM_NOREMOVE = 0x0000;

                public const uint PM_REMOVE = 0x0001;



                [StructLayout(LayoutKind.Sequential)]

                public struct MSG

                {

                        public nint HWnd;

                        public uint Message;

                        public nuint WParam;

                        public nint LParam;

                        public uint Time;

                        public Point Point;

                }



                [DllImport("user32.dll")]

                public static extern void NotifyWinEvent(uint eventId, nint hwnd, int idObject, int idChild);



                [DllImport("user32.dll", CharSet = CharSet.Auto)]

                public static extern nint SendMessage(nint hWnd, int msg, nint wParam, ref LVITEM lParam);



                [DllImport("user32.dll")]

                public static extern bool PeekMessage(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

        }



        private System.Windows.Forms.Timer _listViewSelectionDebounceTimer;

        private DateTime _lastListViewSelectionChangeUtc = DateTime.MinValue;

        private int _pendingListViewSelectionIndex = -1;

        private string _pendingListViewSelectionViewSource;

        private bool _listViewSelectionPending;

        private bool _listViewSelectionBurstActive;

        private int _listViewSelectionBurstCount;

        private DateTime _listViewSelectionBurstStartUtc = DateTime.MinValue;

        private DateTime _lastListViewBurstAccessibilityUpdateUtc = DateTime.MinValue;

        private DateTime _lastListViewBurstNavigationFlushUtc = DateTime.MinValue;

        private int _listViewBurstPropertySyncCount;

        private bool _listViewFocusChangedPending;

        private int _nvdaBufferedNavigationDelta;

        private static readonly MethodInfo _accessibilityNotifyClientsMethod = typeof(Control).GetMethod("AccessibilityNotifyClients", BindingFlags.Instance | BindingFlags.NonPublic, null, new Type[2]

        {

                typeof(AccessibleEvents),

                typeof(int)

        }, null);



        private static bool IsListViewNavigationKey(Keys key)

        {

                return key == Keys.Up || key == Keys.Down || key == Keys.Left || key == Keys.Right ||

                        key == Keys.Home || key == Keys.End || key == Keys.Prior || key == Keys.Next;

        }



        private static bool IsListViewNavigationKeyCode(int keyCode)

        {

                return IsListViewNavigationKey((Keys)keyCode);

        }



        private bool TryThrottleNvdaNavigationKeyDown(KeyEventArgs e)

        {

                if (e == null || resultListView == null || !resultListView.ContainsFocus)

                {

                        return false;

                }

                if (e.KeyCode != Keys.Up && e.KeyCode != Keys.Down)

                {

                        return false;

                }

                if (!IsNvdaRunningCached())

                {

                        _nvdaBufferedNavigationDelta = 0;

                        return false;

                }

                int direction = (e.KeyCode == Keys.Up) ? (-1) : 1;

                _nvdaBufferedNavigationDelta += direction;

                RecordAccessibilityNvdaNavSuppressedCall();

                e.Handled = true;

                e.SuppressKeyPress = true;

                DateTime nowUtc = DateTime.UtcNow;

                if (_lastNvdaListViewNavigationUtc != DateTime.MinValue &&

                        (nowUtc - _lastNvdaListViewNavigationUtc).TotalMilliseconds < ListViewNvdaNavigationKeyMinIntervalMs)

                {

                        return true;

                }

                ApplyBufferedNvdaNavigationDelta();

                _lastNvdaListViewNavigationUtc = nowUtc;

                return true;

        }



        private void ApplyBufferedNvdaNavigationDelta()

        {

                if (_nvdaBufferedNavigationDelta == 0 || resultListView == null || resultListView.Items.Count == 0)

                {

                        return;

                }

                int delta = _nvdaBufferedNavigationDelta;

                _nvdaBufferedNavigationDelta = 0;

                int currentIndex = GetSelectedListViewIndex();

                if (currentIndex < 0)

                {

                        currentIndex = GetFocusedListViewIndex();

                }

                if (currentIndex < 0)

                {

                        currentIndex = 0;

                }

                int targetIndex = Math.Max(0, Math.Min(currentIndex + delta, resultListView.Items.Count - 1));

                int movedSteps = Math.Abs(targetIndex - currentIndex);

                if (movedSteps <= 0)

                {

                        return;

                }

                try

                {

                        ListViewItem item = resultListView.Items[targetIndex];

                        if (!item.Selected)

                        {

                                item.Selected = true;

                        }

                        item.Focused = true;

                        item.EnsureVisible();

                        RecordAccessibilityNvdaNavAppliedStepCall(movedSteps);

                }

                catch

                {

                }

        }



        private int FlushPendingListViewNavigationMessages(bool keyDownOnly = false, int maxRemovals = int.MaxValue, bool logRemovals = true)

        {

                if (resultListView == null || !resultListView.IsHandleCreated)

                {

                        return 0;

                }



                nint handle = resultListView.Handle;

                int removed = 0;

                NativeMethods.MSG msg;

                while (removed < maxRemovals &&

                        NativeMethods.PeekMessage(out msg, handle, NativeMethods.WM_KEYFIRST, NativeMethods.WM_KEYLAST, NativeMethods.PM_NOREMOVE))

                {

                        if (keyDownOnly && msg.Message != NativeMethods.WM_KEYDOWN && msg.Message != NativeMethods.WM_SYSKEYDOWN)

                        {

                                break;

                        }

                        int keyCode = unchecked((int)msg.WParam);

                        if (!IsListViewNavigationKeyCode(keyCode))

                        {

                                break;

                        }



                        if (!NativeMethods.PeekMessage(out msg, handle, NativeMethods.WM_KEYFIRST, NativeMethods.WM_KEYLAST, NativeMethods.PM_REMOVE))

                        {

                                break;

                        }

                        removed++;

                }



                if (removed > 0)

                {

                        RecordAccessibilityNavigationFlushCall(removed);

                }



#if DEBUG

                if (logRemovals && removed > 0 && !_listViewSelectionBurstActive)

                {

                        DebugLogger.Log(DebugLogger.LogLevel.Info, "ListViewBrowse",

                                $"FlushNavKeys removed={removed}");

                }

#endif

                return removed;

        }



        private void TrimBurstNavigationBacklog(DateTime nowUtc)

        {

                if (!_listViewSelectionBurstActive)

                {

                        return;

                }

                if (_lastListViewBurstNavigationFlushUtc != DateTime.MinValue &&

                        (nowUtc - _lastListViewBurstNavigationFlushUtc).TotalMilliseconds < ListViewBurstNavigationFlushIntervalMs)

                {

                        return;

                }

                _lastListViewBurstNavigationFlushUtc = nowUtc;

                FlushPendingListViewNavigationMessages(keyDownOnly: true,

                        maxRemovals: ListViewBurstNavigationFlushMaxPerPass,

                        logRemovals: false);

        }



        private int CalculateTargetIndexAfterDeletion(int deletedIndex, int newListCount)

	{

		if (newListCount == 0)

		{

			return -1;

		}

		checked

		{

			int val = ((deletedIndex >= newListCount) ? (newListCount - 1) : deletedIndex);

			return Math.Max(0, Math.Min(val, newListCount - 1));

		}

	}



        private void EnsureListSelectionWithoutFocus(int targetIndex)

        {

                if (targetIndex < 0 || resultListView.Items.Count == 0)

                {

                        return;

                }

                targetIndex = Math.Max(0, Math.Min(targetIndex, checked(resultListView.Items.Count - 1)));

                resultListView.BeginUpdate();

                try

                {

                        ClearListViewSelection();

                        ListViewItem listViewItem = resultListView.Items[targetIndex];

                        listViewItem.Selected = true;

                        listViewItem.Focused = true;

                        listViewItem.EnsureVisible();

                }

                finally

                {

                        EndListViewUpdateAndRefreshAccessibility();

                }

        }



	private bool IsListViewVirtualSelectionMode()

	{

		return resultListView != null && resultListView.VirtualMode;

	}



	private bool HasListViewSelection()

	{

		if (resultListView == null)

		{

			return false;

		}

		if (IsListViewVirtualSelectionMode())

		{

			return resultListView.SelectedIndices.Count > 0;

		}

		return resultListView.SelectedItems.Count > 0;

	}



	private int GetSelectedListViewIndex()

	{

		if (resultListView == null)

		{

			return -1;

		}

		if (IsListViewVirtualSelectionMode())

		{

			return (resultListView.SelectedIndices.Count > 0) ? resultListView.SelectedIndices[0] : (-1);

		}

		return (resultListView.SelectedItems.Count > 0) ? resultListView.SelectedItems[0].Index : (-1);

	}



	private async Task<int> GetSelectedListViewIndexAsync()

	{

		int index = -1;

		await ExecuteOnUiThreadAsync(delegate

		{

			index = GetSelectedListViewIndex();

		}).ConfigureAwait(continueOnCapturedContext: false);

		return index;

	}



        private ListViewItem GetSelectedListViewItemSafe()

	{

		if (resultListView == null)

		{

			return null;

		}

		int selectedListViewIndex = GetSelectedListViewIndex();

		if (selectedListViewIndex < 0 || selectedListViewIndex >= resultListView.Items.Count)

		{

			return null;

		}

		return resultListView.Items[selectedListViewIndex];

	}



	private void ClearListViewSelection()

	{

		if (resultListView != null)

		{

			if (IsListViewVirtualSelectionMode())

			{

				resultListView.SelectedIndices.Clear();

			}

			else

			{

				resultListView.SelectedItems.Clear();

			}

		}

	}



	private void ResetListViewSelectionState()

	{

		if (resultListView != null)

		{

			try

			{

				resultListView.SelectedIndices.Clear();

			}

			catch

			{

			}

			try

			{

				resultListView.FocusedItem = null;

			}

			catch

			{

			}

			_lastListViewFocusedIndex = -1;

			_lastListViewSpokenIndex = -1;

		}

                SetListViewRedrawDeferred(false);

	}



        private void RestoreListViewFocus(int targetIndex)

        {

                targetIndex = ResolvePendingListFocusIndex(targetIndex);

                EnsureListSelectionWithoutFocus(targetIndex);

                if (resultListView.CanFocus)

                {

                        resultListView.Focus();

                }

        }



        private void FocusListAfterEnrich(int pendingFocusIndex)

        {

            if (IsListAutoFocusSuppressed || resultListView == null || resultListView.Items.Count == 0)

            {

                return;

            }

            if (IsSearchViewSource(_currentViewSource) && resultListView.Items.Count == 1)

            {

                ListViewItem item = resultListView.Items[0];

                if (IsListViewRetryPlaceholderItem(item))

                {

                    return;

                }

            }



            bool viewMatched = _pendingSongFocusSatisfied

                && !string.IsNullOrWhiteSpace(_pendingSongFocusSatisfiedViewSource)

                && string.Equals(_pendingSongFocusSatisfiedViewSource, _currentViewSource, StringComparison.OrdinalIgnoreCase);

            bool hasSelection = resultListView.SelectedIndices.Count > 0 || resultListView.FocusedItem != null;

            if (viewMatched && hasSelection)

            {

                return;

            }



            int targetIndex = (pendingFocusIndex >= 0)

                ? pendingFocusIndex

                : ((resultListView.SelectedIndices.Count > 0) ? resultListView.SelectedIndices[0] : 0);

            RestoreListViewFocus(targetIndex);

        }



        private bool IsListViewLoadingPlaceholderItem(ListViewItem item)

        {

                if (item == null)

                {

                        return false;

                }

                string primaryText = item.Text?.Trim() ?? string.Empty;

                if (string.Equals(primaryText, ListLoadingPlaceholderText, StringComparison.Ordinal))

                {

                        return true;

                }

                if (item.SubItems.Count > 2)

                {

                        string text = item.SubItems[2]?.Text?.Trim() ?? string.Empty;

                        return string.Equals(text, ListLoadingPlaceholderText, StringComparison.Ordinal);

                }

                return false;

        }



        private bool IsListViewRetryPlaceholderItem(ListViewItem item)

        {

                if (item == null)

                {

                        return false;

                }

                string primaryText = item.Text?.Trim() ?? string.Empty;

                if (string.Equals(primaryText, ListRetryPlaceholderText, StringComparison.Ordinal))

                {

                        return true;

                }

                if (item.SubItems.Count > 2)

                {

                        string text = item.SubItems[2]?.Text?.Trim() ?? string.Empty;

                        return string.Equals(text, ListRetryPlaceholderText, StringComparison.Ordinal);

                }

                return false;

        }



        private void TryAnnounceLoadingPlaceholderReplacement()

        {

                if (!_listLoadingPlaceholderActive || resultListView == null || resultListView.Items.Count == 0)

                {

                        return;

                }

                int focusedIndex = GetFocusedListViewIndex();

                if (focusedIndex < 0 || focusedIndex >= resultListView.Items.Count)

                {

                        _listLoadingPlaceholderActive = false;

                        return;

                }

                ListViewItem item = resultListView.Items[focusedIndex];

                if (IsListViewLoadingPlaceholderItem(item))

                {

                        return;

                }

                _listLoadingPlaceholderActive = false;

                if (resultListView.ContainsFocus)

                {

                        QueueFocusedListViewItemRefreshAnnouncement(focusedIndex);

                }

        }



        private Task EnsureListFocusedAfterUrlParseAsync(int fallbackIndex = 0)

        {

            return ExecuteOnUiThreadAsync(delegate

            {

                if (IsListAutoFocusSuppressed || resultListView == null || resultListView.Items.Count == 0)

                {

                    return;

                }



                int targetIndex = (resultListView.SelectedIndices.Count > 0)

                    ? resultListView.SelectedIndices[0]

                    : fallbackIndex;

                targetIndex = Math.Max(0, Math.Min(targetIndex, resultListView.Items.Count - 1));

                RestoreListViewFocus(targetIndex);

            });

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



        private bool IsZdsrRunningCached()

        {

                DateTime utcNow = DateTime.UtcNow;

                if ((utcNow - _lastZdsrCheckAt).TotalMilliseconds >= ZdsrCheckIntervalMs)

                {

                        _isZdsrRunningCached = IsZdsrRunning();

                        _lastZdsrCheckAt = utcNow;

                }

                return _isZdsrRunningCached;

        }



	private bool IsNarratorRunningCached()

	{

		DateTime utcNow = DateTime.UtcNow;

		if ((utcNow - _lastNarratorCheckAt).TotalMilliseconds >= NarratorCheckIntervalMs)

		{

			_isNarratorRunningCached = IsNarratorRunning();

			_lastNarratorCheckAt = utcNow;

		}

		return _isNarratorRunningCached;

	}



	private static bool IsNarratorRunning()

	{

		try

		{

			return Process.GetProcessesByName("Narrator").Length > 0;

		}

		catch

		{

			return false;

		}

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



        private static bool IsZdsrRunning()

        {

                try

                {

                        foreach (Process process in Process.GetProcesses())

                        {

                                string name = process.ProcessName ?? string.Empty;

                                if (name.Length == 0)

                                {

                                        continue;

                                }

                                if (name.Equals("ZDSR", StringComparison.OrdinalIgnoreCase)

                                        || name.StartsWith("ZDSR", StringComparison.OrdinalIgnoreCase))

                                {

                                        return true;

                                }

                        }

                }

                catch

                {

                }

                return false;

        }



	private bool ShouldUseCustomListViewSpeech()

	{

		return false;

	}



	private void EndListViewUpdateAndRefreshAccessibility()

	{

		resultListView.EndUpdate();

		ScheduleResultListViewLayoutUpdate();

		ScheduleListViewAccessibilityRefresh();

	}



        private void ScheduleListViewAccessibilityRefresh()

        {

                if (resultListView == null)

                {

                        return;

                }

                _listViewAccessibilityPending = true;

                _lastListViewAccessibilityRequestUtc = DateTime.UtcNow;

                if (_listViewAccessibilityDebounceTimer == null)

                {

                        _listViewAccessibilityDebounceTimer = new System.Windows.Forms.Timer();

                        _listViewAccessibilityDebounceTimer.Tick += (_, _) => HandleListViewAccessibilityTimerTick();

                }

                ScheduleListViewAccessibilityTimer();

        }



        private void HandleListViewAccessibilityTimerTick()

        {

                if (_listViewAccessibilityDebounceTimer != null)

                {

                        _listViewAccessibilityDebounceTimer.Stop();

                }

                if (!_listViewAccessibilityPending)

                {

                        return;

                }

                DateTime dueUtc = GetListViewAccessibilityDueUtc();

                if (DateTime.UtcNow < dueUtc)

                {

                        ScheduleListViewAccessibilityTimer();

                        return;

                }

                _listViewAccessibilityPending = false;

                RefreshListViewAccessibilityProperties();

                if (_listViewAccessibilityPending)

                {

                        ScheduleListViewAccessibilityTimer();

                }

        }



        private void ScheduleListViewAccessibilityTimer()

        {

                if (_listViewAccessibilityDebounceTimer == null)

                {

                        return;

                }

                DateTime dueUtc = GetListViewAccessibilityDueUtc();

                int delayMs = (int)Math.Max(1, Math.Ceiling((dueUtc - DateTime.UtcNow).TotalMilliseconds));

                _listViewAccessibilityDebounceTimer.Interval = delayMs;

                _listViewAccessibilityDebounceTimer.Stop();

                _listViewAccessibilityDebounceTimer.Start();

        }



        private DateTime GetListViewAccessibilityDueUtc()

        {

                DateTime baseUtc = _lastListViewAccessibilityRequestUtc;

                if (_lastListViewFocusChangeUtc > baseUtc)

                {

                        baseUtc = _lastListViewFocusChangeUtc;

                }

                if (baseUtc == DateTime.MinValue)

                {

                        baseUtc = DateTime.UtcNow;

                }

                return baseUtc.AddMilliseconds(ListViewFocusStableDelayMs);

        }



	private void PatchSongItemsInPlace(Dictionary<int, SongInfo> updatedSongs, int startIndex = 1)

	{

		if (updatedSongs == null || updatedSongs.Count == 0 || resultListView == null || resultListView.Items.Count == 0)

		{

			return;

		}

		bool setRole = IsNvdaRunningCached();

		ListViewItem selectedListViewItemSafe = GetSelectedListViewItemSafe();

		int num = selectedListViewItemSafe?.Index ?? (-1);

		int focusedListViewIndex = GetFocusedListViewIndex();

		bool flag = focusedListViewIndex >= 0 && updatedSongs.ContainsKey(focusedListViewIndex);

		bool flag2 = resultListView.Items.Count >= 1000;

		int startIndex2 = -1;

		int endIndex = -1;

		if (flag2 && !TryGetListViewVisibleRange(out startIndex2, out endIndex))

		{

			flag2 = false;

		}

		resultListView.BeginUpdate();

		try

		{

			foreach (KeyValuePair<int, SongInfo> updatedSong in updatedSongs)

			{

				int key = updatedSong.Key;

				if (key >= 0 && key < resultListView.Items.Count)

				{

					ListViewItem listViewItem = resultListView.Items[key];

					FillListViewItemFromSongInfo(listViewItem, updatedSong.Value, checked(startIndex + key));

					listViewItem.Tag = key;

					if (!flag2 || (key >= startIndex2 && key <= endIndex) || key == num)

					{

						UpdateListViewItemAccessibilityProperties(listViewItem, setRole, updateAccessibleObjectName: !setRole);

					}

				}

			}

		}

		finally

		{

			resultListView.EndUpdate();

		}

		ScheduleResultListViewLayoutUpdate();

		if (selectedListViewItemSafe != null)

		{

			UpdateListViewItemAccessibilityProperties(selectedListViewItemSafe, setRole, updateAccessibleObjectName: !setRole);

		}

        if (flag)

        {

                QueueFocusedListViewItemRefreshAnnouncement(focusedListViewIndex);

        }

        TryDispatchPendingPlaceholderPlayback(updatedSongs);

}



	private void QueueFocusedListViewItemRefreshAnnouncement(int expectedIndex, bool interruptAnnouncement = true)

	{

		if (resultListView == null || expectedIndex < 0 || !resultListView.IsHandleCreated)

		{

			return;

		}

		if (_focusedRefreshAnnouncementQueued && _pendingFocusedRefreshAnnouncementIndex == expectedIndex)

		{

#if DEBUG

			DebugLogger.Log(DebugLogger.LogLevel.Info, "ListViewAccess", $"CoalesceFocusedRefresh index={expectedIndex}");

#endif

			return;

		}

		_focusedRefreshAnnouncementQueued = true;

		_pendingFocusedRefreshAnnouncementIndex = expectedIndex;

		BeginInvoke(delegate

		{

			try

			{

				if (resultListView == null || !resultListView.IsHandleCreated || !resultListView.ContainsFocus)

				{

					return;

				}

				int focusedListViewIndex = GetFocusedListViewIndex();

				if (focusedListViewIndex != expectedIndex || focusedListViewIndex < 0 || focusedListViewIndex >= resultListView.Items.Count)

				{

					return;

				}

				ListViewItem item = resultListView.Items[focusedListViewIndex];

				bool isNvda = IsNvdaRunningCached();

				bool isNarrator = IsNarratorRunningCached();

				UpdateListViewItemAccessibilityProperties(item, isNvda, updateAccessibleObjectName: !isNvda);

#if DEBUG

				DebugLogger.Log(DebugLogger.LogLevel.Info, "ListViewAccess",

					$"FocusedRefreshDispatch index={focusedListViewIndex} interrupt={interruptAnnouncement} both={isNvda && isNarrator}");

#endif

				AnnounceListViewItemAlert(item, interruptAnnouncement);

				if (ShouldUseCustomListViewSpeech())

				{

					SpeakListViewSelectionIfNeeded(item, forceRepeat: true, interrupt: false);

				}

				else if (!(isNvda && isNarrator))

				{

					NotifyAccessibilityClients(resultListView, AccessibleEvents.NameChange, focusedListViewIndex);

				}

			}

			finally

			{

				_focusedRefreshAnnouncementQueued = false;

				_pendingFocusedRefreshAnnouncementIndex = -1;

			}

		});

	}



	private void AnnounceListViewItemAlert(ListViewItem item, bool interruptAnnouncement = true)

	{

		if (item != null && _accessibilityAnnouncementLabel != null)

		{

			string text = ((item.SubItems.Count > 0) ? item.SubItems[0].Text : string.Empty);

			if (string.IsNullOrWhiteSpace(text))

			{

				text = BuildListViewItemSpeech(item);

			}

			if (!string.IsNullOrWhiteSpace(text))

			{

#if DEBUG

				DebugLogger.Log(DebugLogger.LogLevel.Info, "ListViewAccess",

					$"FocusedRefreshLiveRegion interrupt={interruptAnnouncement} textLen={text.Length}");

#endif

				if (interruptAnnouncement)

				{

					RaiseAccessibilityAnnouncement(text);

				}

				else

				{

					RaiseAccessibilityAnnouncementUiOnly(text, AutomationNotificationProcessing.CurrentThenMostRecent, AutomationLiveSetting.Polite);

				}

			}

		}

	}



        private void RaiseAccessibilityAnnouncement(string text, bool preferInterrupt = false)

        {

                if (_accessibilityAnnouncementLabel == null)

                {

                        return;

                }

		string text2 = text.Trim();

		if (string.IsNullOrWhiteSpace(text2))

		{

			return;

		}

		DateTime utcNow = DateTime.UtcNow;

		if (string.Equals(_lastAnnouncementText, text2, StringComparison.Ordinal) && utcNow - _lastAnnouncementAt < AnnouncementRepeatCooldown)

		{

			return;

		}

		_lastAnnouncementText = text2;

		_lastAnnouncementAt = utcNow;

		Label accessibilityAnnouncementLabel = _accessibilityAnnouncementLabel;

		if (accessibilityAnnouncementLabel.IsDisposed)

		{

			return;

		}

                accessibilityAnnouncementLabel.Text = text2;

                accessibilityAnnouncementLabel.AccessibleName = text2;

                accessibilityAnnouncementLabel.AccessibleDescription = text2;

                TrySetLiveRegionSetting(accessibilityAnnouncementLabel, preferInterrupt ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);

                try

                {

                        accessibilityAnnouncementLabel.AccessibilityObject.RaiseAutomationNotification(AutomationNotificationKind.Other, AutomationNotificationProcessing.ImportantMostRecent, text2);

                }

                catch

                {

                }

                try

                {

                        NotifyAccessibilityClients(accessibilityAnnouncementLabel, AccessibleEvents.NameChange, -1);

                        NotifyAccessibilityClients(accessibilityAnnouncementLabel, AccessibleEvents.ValueChange, -1);

                }

                catch

                {

                }

                try

                {

                        RaiseControlWinEvent(accessibilityAnnouncementLabel, 32780u);

                        RaiseControlWinEvent(accessibilityAnnouncementLabel, 32770u);

                }

                catch

                {

                }

                if (preferInterrupt)

                {

                        TryRaiseLiveRegionChanged(accessibilityAnnouncementLabel);

                }

        }



        private void RaiseAccessibilityAnnouncementUiOnly(string text, AutomationNotificationProcessing processing, AutomationLiveSetting liveSetting)

        {

                if (_accessibilityAnnouncementLabel == null)

                {

                        return;

                }

                string trimmed = text?.Trim() ?? string.Empty;

                if (string.IsNullOrWhiteSpace(trimmed))

                {

                        return;

                }

                DateTime utcNow = DateTime.UtcNow;

                if (string.Equals(_lastAnnouncementText, trimmed, StringComparison.Ordinal) && utcNow - _lastAnnouncementAt < AnnouncementRepeatCooldown)

                {

                        return;

                }

                _lastAnnouncementText = trimmed;

                _lastAnnouncementAt = utcNow;

                Label accessibilityAnnouncementLabel = _accessibilityAnnouncementLabel;

                if (accessibilityAnnouncementLabel.IsDisposed)

                {

                        return;

                }

                accessibilityAnnouncementLabel.Text = trimmed;

                accessibilityAnnouncementLabel.AccessibleName = trimmed;

                accessibilityAnnouncementLabel.AccessibleDescription = trimmed;

                TrySetLiveRegionSetting(accessibilityAnnouncementLabel, liveSetting);

                try

                {

                        accessibilityAnnouncementLabel.AccessibilityObject.RaiseAutomationNotification(AutomationNotificationKind.Other, processing, trimmed);

                }

                catch

                {

                }

        }



        private bool SpeakNarratorAnnouncement(string text, bool interrupt)

        {

                if (string.IsNullOrWhiteSpace(text) || IsDisposed)

                {

                        return false;

                }



                if (InvokeRequired)

                {

                        try

                        {

                                BeginInvoke(new Action<string, bool>(SpeakNarratorAnnouncementInternal), text, interrupt);

                                return true;

                        }

                        catch

                        {

                                return false;

                        }

                }



                try

                {

                        SpeakNarratorAnnouncementInternal(text, interrupt);

                        return true;

                }

                catch

                {

                        return false;

                }

        }



        private void SpeakNarratorAnnouncementInternal(string text, bool interrupt)

        {

                if (interrupt)

                {

                        bool narratorRunning = IsNarratorRunningCached();

                        if (narratorRunning)

                        {

                                bool nvdaRunning = IsNvdaRunningCached();

#if DEBUG

                                DebugLogger.Log(DebugLogger.LogLevel.Info, "AccessibilitySpeech",

                                        $"NarratorInterruptDispatch mode={(nvdaRunning ? "UiOnly" : "Full")} nvda={nvdaRunning}");

#endif

                                if (nvdaRunning)

                                {

                                        RaiseAccessibilityAnnouncementUiOnly(text, AutomationNotificationProcessing.ImportantMostRecent, AutomationLiveSetting.Assertive);

                                }

                                else

                                {

                                        RaiseAccessibilityAnnouncement(text, preferInterrupt: true);

                                }

                                return;

                        }

                        RaiseAccessibilityAnnouncementUiOnly(text, AutomationNotificationProcessing.ImportantMostRecent, AutomationLiveSetting.Assertive);

                        return;

                }

                RaiseAccessibilityAnnouncementUiOnly(text, AutomationNotificationProcessing.CurrentThenMostRecent, AutomationLiveSetting.Polite);

        }



        private void UpdateStatusStripAccessibility(string message)

        {

                if (statusStrip1 == null || statusStrip1.IsDisposed)

                {

                        return;

                }

                string text = message?.Trim() ?? string.Empty;

                try

                {

                        statusStrip1.AccessibleRole = AccessibleRole.StatusBar;

                        statusStrip1.AccessibleName = string.Empty;

                        statusStrip1.AccessibleDescription = string.Empty;

                        statusStrip1.Text = string.Empty;

                        if (toolStripStatusLabel1 != null)

                        {

                                toolStripStatusLabel1.AccessibleRole = AccessibleRole.StaticText;

                                if (!string.IsNullOrWhiteSpace(text))

                                {

                                        string spokenText = $"{text}，状态栏";

                                        toolStripStatusLabel1.Text = text;

                                        toolStripStatusLabel1.AccessibleName = spokenText;

                                        toolStripStatusLabel1.AccessibleDescription = spokenText;

                                }

                                else

                                {

                                        toolStripStatusLabel1.AccessibleName = "\u6682\u65E0\u5185\u5BB9";

                                        toolStripStatusLabel1.AccessibleDescription = "\u6682\u65E0\u5185\u5BB9";

                                }

                        }

                }

                catch

                {

                }

        }



        private void AnnounceStatusStripAccessibility(string message)

        {

                if (statusStrip1 == null || statusStrip1.IsDisposed)

                {

                        return;

                }

                string text = message?.Trim() ?? string.Empty;

                if (string.IsNullOrWhiteSpace(text))

                {

                        return;

                }

                UpdateStatusStripAccessibility(text);

                try

                {

                        NotifyAccessibilityClients(statusStrip1, AccessibleEvents.NameChange, -1);

                        NotifyAccessibilityClients(statusStrip1, AccessibleEvents.ValueChange, -1);

                }

                catch

                {

                }

                try

                {

                        statusStrip1.AccessibilityObject.RaiseAutomationNotification(AutomationNotificationKind.Other, AutomationNotificationProcessing.ImportantMostRecent, text);

                }

                catch

                {

                }

        }



	private int GetFocusedListViewIndex()

	{

		if (resultListView == null)

		{

			return -1;

		}

		int num = GetSelectedListViewIndex();

		if (num < 0 && resultListView.FocusedItem != null)

		{

			num = resultListView.FocusedItem.Index;

		}

		return num;

	}



        private void InitializeAccessibilityAnnouncementLabel()

        {

		if (_accessibilityAnnouncementLabel == null && !base.IsDisposed)

		{

			_accessibilityAnnouncementLabel = new AccessibilityAnnouncementLabel

			{

				Name = "accessibilityAnnouncementLabel",

				TabStop = false,

				AutoSize = false,

				Size = new Size(1, 1),

				Location = new Point(-2000, -2000),

				Text = string.Empty,

				AccessibleName = string.Empty,

				AccessibleRole = AccessibleRole.StaticText

			};

			base.Controls.Add(_accessibilityAnnouncementLabel);

		}

	}



        private static void TrySetLiveRegionSetting(Control control, AutomationLiveSetting setting)

        {

                if (control == null)

                {

                        return;

                }

                try

                {

                        if (control.AccessibilityObject is IAutomationLiveRegion liveRegion)

                        {

                                liveRegion.LiveSetting = setting;

                        }

                }

                catch

                {

                }

        }



        private static void TryRaiseLiveRegionChanged(Control control)

        {

                if (control == null)

                {

                        return;

                }

                try

                {

                        control.AccessibilityObject?.RaiseLiveRegionChanged();

                }

                catch

                {

                }

        }



        private sealed class AccessibilityAnnouncementLabel : Label

        {

                protected override AccessibleObject CreateAccessibilityInstance()

                {

                        return new AccessibilityAnnouncementLabelAccessibleObject(this);

                }



                private sealed class AccessibilityAnnouncementLabelAccessibleObject : Control.ControlAccessibleObject, IAutomationLiveRegion

                {

                        private AutomationLiveSetting _liveSetting = AutomationLiveSetting.Polite;



                        public AccessibilityAnnouncementLabelAccessibleObject(Control owner) : base(owner)

                        {

                        }



                        public AutomationLiveSetting LiveSetting

                        {

                                get => _liveSetting;

                                set => _liveSetting = value;

                        }

                }

        }



        private AccessibleObject GetListViewItemAccessibleObject(int targetIndex)

	{

		if (resultListView == null || !resultListView.IsHandleCreated)

		{

			return null;

		}

		AccessibleObject accessibilityObject = resultListView.AccessibilityObject;

		if (accessibilityObject == null)

		{

			return null;

		}

		int childCount;

		try

		{

			childCount = accessibilityObject.GetChildCount();

		}

		catch

		{

			return null;

		}

		int num = 0;

		checked

		{

			for (int i = 0; i < childCount; i++)

			{

				AccessibleObject accessibleObject = null;

				try

				{

					accessibleObject = accessibilityObject.GetChild(i);

				}

				catch

				{

				}

				if (accessibleObject == null)

				{

					continue;

				}

				AccessibleRole role;

				try

				{

					role = accessibleObject.Role;

				}

				catch

				{

					continue;

				}

				if (role == AccessibleRole.ListItem || role == AccessibleRole.Row)

				{

					if (num == targetIndex)

					{

						return accessibleObject;

					}

					num++;

				}

			}

			return null;

		}

	}



	private void UpdatePlaybackQueueSongs(IEnumerable<SongInfo> songs)

	{

		if (songs == null)

		{

			return;

		}

		foreach (SongInfo song in songs)

		{

			_playbackQueue.TryUpdateSongDetail(song);

		}

	}



	private bool TryGetListViewVisibleRange(out int startIndex, out int endIndex)

	{

		startIndex = 0;

		endIndex = -1;

		if (resultListView == null || resultListView.Items.Count == 0 || !resultListView.IsHandleCreated)

		{

			return false;

		}

		checked

		{

			int num = resultListView.Items.Count - 1;

			try

			{

				startIndex = resultListView.TopItem?.Index ?? 0;

			}

			catch

			{

				startIndex = 0;

			}

			if (startIndex < 0 || startIndex > num)

			{

				startIndex = 0;

			}

			int num2 = 0;

			try

			{

				num2 = resultListView.GetItemRect(startIndex).Height;

			}

			catch

			{

				num2 = 0;

			}

			int num3 = ((num2 > 0) ? (unchecked(resultListView.ClientSize.Height / num2) + 2) : Math.Min(12, resultListView.Items.Count));

			endIndex = Math.Min(num, startIndex + num3);

			return true;

		}

	}



	private void RefreshListViewAccessibilityProperties()

	{

		if (resultListView == null || resultListView.Items.Count == 0 || !resultListView.IsHandleCreated)

		{

			return;

		}

#if DEBUG

                TouchUiThreadMarker("RefreshAccessibility");

#endif

                int itemCount = resultListView.Items.Count;

#if DEBUG

                Stopwatch refreshSw = Stopwatch.StartNew();

#endif

                bool setRole = IsNvdaRunningCached();

                bool updateNames = !setRole;

                bool usePartialRefresh = (_isVirtualSongListActive && resultListView.VirtualMode)

                        || itemCount >= ListViewAccessibilityPartialRefreshThreshold;

                if (usePartialRefresh)

                {

                        bool updatePartialNames = updateNames && itemCount <= ListViewAccessibilityNameSyncItemLimit;

                        RefreshVirtualListViewAccessibilityProperties(setRole, updatePartialNames);

#if DEBUG

                        if (refreshSw != null)

                        {

                                refreshSw.Stop();

                                long refreshMs = refreshSw.ElapsedMilliseconds;

                                if (refreshMs >= 200)

                                {

                                        string refreshScope = (_isVirtualSongListActive && resultListView.VirtualMode) ? "virtual" : "partial";

                                        DebugLogger.LogUIThreadBlock("ListViewAccess",

                                                $"RefreshAccessibility {refreshScope} items={itemCount}", refreshMs);

                                }

                        }

#endif

			return;

		}

		for (int i = 0; i < resultListView.Items.Count; i = checked(i + 1))

		{

			ListViewItem item = resultListView.Items[i];

                        UpdateListViewItemAccessibilityProperties(item, setRole, updateAccessibleObjectName: false);

                }

                if (updateNames)

                {

                        UpdateListViewAccessibleObjectNames();

                }

#if DEBUG

                if (refreshSw != null)

                {

                        refreshSw.Stop();

                        long refreshMs = refreshSw.ElapsedMilliseconds;

                        if (refreshMs >= 200)

                        {

                                DebugLogger.LogUIThreadBlock("ListViewAccess",

                                        $"RefreshAccessibility items={itemCount}", refreshMs);

                        }

                }

#endif

	}



	private void RefreshVirtualListViewAccessibilityProperties(bool setRole, bool updateNames)

	{

		if (resultListView == null || resultListView.Items.Count == 0 || !resultListView.IsHandleCreated)

		{

			return;

		}

		checked

		{

			int num = resultListView.Items.Count - 1;

			int num2 = 0;

			try

			{

				num2 = resultListView.TopItem?.Index ?? 0;

			}

			catch

			{

				num2 = 0;

			}

			if (num2 < 0 || num2 > num)

			{

				num2 = 0;

			}

			int num3 = 0;

			try

			{

				if (resultListView.Items.Count > 0)

				{

					num3 = resultListView.GetItemRect(num2).Height;

				}

			}

			catch

			{

				num3 = 0;

			}

			int num4 = ((num3 > 0) ? (unchecked(resultListView.ClientSize.Height / num3) + 2) : 0);

			if (num4 <= 0)

			{

				num4 = Math.Min(12, resultListView.Items.Count);

			}

			int num5 = Math.Min(num, num2 + num4);

			for (int i = num2; i <= num5; i++)

			{

				ListViewItem item = resultListView.Items[i];

				UpdateListViewItemAccessibilityProperties(item, setRole, updateAccessibleObjectName: updateNames);

			}

			ListViewItem selectedListViewItemSafe = GetSelectedListViewItemSafe();

			if (selectedListViewItemSafe != null)

			{

				UpdateListViewItemAccessibilityProperties(selectedListViewItemSafe, setRole);

			}

		}

	}



        private void UpdateListViewItemAccessibilityProperties(ListViewItem item, bool setRole)

        {

                UpdateListViewItemAccessibilityProperties(item, setRole, updateAccessibleObjectName: true);

        }



        private void UpdateListViewItemAccessibilityProperties(ListViewItem item, bool setRole, bool updateAccessibleObjectName)

        {

                if (item == null || resultListView == null || !resultListView.IsHandleCreated)

                {

                        return;

                }

#if DEBUG

                Stopwatch totalSw = Stopwatch.StartNew();

#endif

                EnsureSubItemCount(item, 6);

                string text = BuildListViewItemAccessibleName(item);

                bool allowEmpty = TtsHelper.IsBoyPcReaderActive();

                if (string.IsNullOrWhiteSpace(text) && !allowEmpty)

                {

                        return;

                }

                bool textChanged = item.SubItems[0].Text != text;

                if (textChanged)

                {

                        item.SubItems[0].Text = text;

                }

                int? role = (setRole ? new int?(41) : ((int?)null));

                bool shouldSetProperties = textChanged || setRole;

                try

                {

                        if (shouldSetProperties)

                        {

                                Stopwatch setSw = null;

#if DEBUG

                                setSw = Stopwatch.StartNew();

#endif

                                AccessibilityPropertyService.TrySetListItemProperties(resultListView.Handle, item.Index, text, role);

                                RecordAccessibilitySetPropertyCall();

#if DEBUG

                                if (setSw != null)

                                {

                                        setSw.Stop();

                                        long setMs = setSw.ElapsedMilliseconds;

                                        if (setMs >= 200)

                                        {

                                                DebugLogger.LogUIThreadBlock("ListViewAccess", $"TrySetListItemProperties index={item.Index} len={text.Length}", setMs);

                                        }

                                }

#endif

                        }

                        if (updateAccessibleObjectName && textChanged)

                        {

                                Stopwatch nameSw = null;

#if DEBUG

                                nameSw = Stopwatch.StartNew();

#endif

                                UpdateListViewAccessibleObjectName(item, text);

#if DEBUG

                                if (nameSw != null)

                                {

                                        nameSw.Stop();

                                        long nameMs = nameSw.ElapsedMilliseconds;

                                        if (nameMs >= 200)

                                        {

                                                DebugLogger.LogUIThreadBlock("ListViewAccess", $"UpdateAccessibleObjectName index={item.Index} len={text.Length}", nameMs);

                                        }

                                }

#endif

                        }

                }

                catch

                {

                }

#if DEBUG

                if (totalSw != null)

                {

                        totalSw.Stop();

                        long totalMs = totalSw.ElapsedMilliseconds;

                        if (totalMs >= 200)

                        {

                                DebugLogger.LogUIThreadBlock("ListViewAccess",

                                        $"UpdateItemAccessibility index={item.Index} len={text.Length}", totalMs);

                        }

                }

#endif

        }





	private void UpdateListViewAccessibleObjectNames()

        {

                if (resultListView == null || resultListView.Items.Count == 0 || !resultListView.IsHandleCreated)

                {

                        return;

                }

                bool allowEmpty = TtsHelper.IsBoyPcReaderActive();

		AccessibleObject accessibilityObject = resultListView.AccessibilityObject;

		if (accessibilityObject == null)

		{

			return;

		}

		int childCount;

		try

		{

			childCount = accessibilityObject.GetChildCount();

		}

		catch

		{

			return;

		}

		int num = 0;

		checked

		{

			for (int i = 0; i < childCount && num < resultListView.Items.Count; i++)

			{

				AccessibleObject accessibleObject = null;

				try

				{

					accessibleObject = accessibilityObject.GetChild(i);

				}

				catch

				{

				}

				if (accessibleObject == null)

				{

					continue;

				}

				AccessibleRole role;

				try

				{

					role = accessibleObject.Role;

				}

				catch

				{

					continue;

				}

				if (role != AccessibleRole.ListItem && role != AccessibleRole.Row)

				{

					continue;

				}

				ListViewItem item = resultListView.Items[num];

				string text = BuildListViewItemAccessibleName(item);

				if (string.IsNullOrWhiteSpace(text) && !allowEmpty)

				{

					continue;

				}

				try

				{

					accessibleObject.Name = text;

				}

				catch

				{

				}

				num++;

			}

		}

	}



        private void UpdateListViewAccessibleObjectName(ListViewItem item, string speech)

        {

                bool allowEmpty = TtsHelper.IsBoyPcReaderActive();

                if (resultListView == null || item == null || (!allowEmpty && string.IsNullOrWhiteSpace(speech)) || !resultListView.IsHandleCreated)

                {

                        return;

		}

		AccessibleObject accessibilityObject = resultListView.AccessibilityObject;

		if (accessibilityObject == null)

		{

			return;

		}

		int childCount;

		try

		{

			childCount = accessibilityObject.GetChildCount();

		}

		catch

		{

			return;

		}

		int num = 0;

#if DEBUG

                Stopwatch sw = Stopwatch.StartNew();

#endif

		checked

		{

			for (int i = 0; i < childCount; i++)

			{

				AccessibleObject accessibleObject = null;

				try

				{

					accessibleObject = accessibilityObject.GetChild(i);

				}

				catch

				{

				}

				if (accessibleObject == null)

				{

					continue;

				}

				AccessibleRole role;

				try

				{

					role = accessibleObject.Role;

				}

				catch

				{

					continue;

				}

				if (role != AccessibleRole.ListItem && role != AccessibleRole.Row)

				{

					continue;

				}

				if (num == item.Index)

				{

					try

					{

						accessibleObject.Name = speech;

						break;

					}

					catch

					{

						break;

					}

				}

                        num++;

                }

        }

#if DEBUG

                if (sw != null)

                {

                        sw.Stop();

                        long ms = sw.ElapsedMilliseconds;

                        if (ms >= 200)

                        {

                                DebugLogger.LogUIThreadBlock("ListViewAccess", $"UpdateAccessibleObjectNameLoop index={item.Index} children={childCount}", ms);

                        }

                }

#endif

        }



        private void TryRaiseListViewItemAccessibleFocus(int index)

        {

                if (resultListView != null && resultListView.IsHandleCreated && index >= 0 && index < resultListView.Items.Count)

                {

                        try

                        {

                                GetListViewItemAccessibleObject(index)?.Select(AccessibleSelection.TakeFocus | AccessibleSelection.TakeSelection);

                        }

                        catch

                        {

                        }

                        try

                        {

                                ForceListViewItemFocusState(index);

                        }

                        catch

                        {

                        }

                        RaiseListViewItemWinEvents(index);

                }

        }



        private void ForceListViewItemFocusState(int index)

        {

                if (resultListView != null && resultListView.IsHandleCreated && index >= 0 && index < resultListView.Items.Count)

                {

                        nint handle = resultListView.Handle;

                        if (handle != IntPtr.Zero)

                        {

                                NativeMethods.LVITEM lParam = new NativeMethods.LVITEM

                                {

                                        mask = 8u,

                                        stateMask = 3u,

                                        state = 0u

                                };

                                NativeMethods.SendMessage(handle, 4139, index, ref lParam);

                                lParam.state = 3u;

                                NativeMethods.SendMessage(handle, 4139, index, ref lParam);

                        }

                }

        }



        private void RaiseListViewItemWinEvents(int index)

        {

                if (resultListView == null || !resultListView.IsHandleCreated || index < 0 || index >= resultListView.Items.Count)

                {

                        return;

                }

                nint handle = resultListView.Handle;

                if (handle == IntPtr.Zero)

                {

                        return;

                }

                int idChild = checked(index + 1);

                try

                {

                        NativeMethods.NotifyWinEvent(32780u, handle, -4, idChild);

                        NativeMethods.NotifyWinEvent(32774u, handle, -4, idChild);

                        NativeMethods.NotifyWinEvent(32773u, handle, -4, idChild);

                        RecordAccessibilityWinEventCall(3);

                }

                catch (Exception ex)

                {

                        TryCaptureDiagnosticsDumpOnAccessibilityError($"RaiseListViewItemWinEvents index={index}", ex);

                }

        }





        private void RaiseControlWinEvent(Control control, uint eventId)

        {

                if (control == null || !control.IsHandleCreated)

                {

                        return;

                }

                nint handle = control.Handle;

                if (handle == IntPtr.Zero)

                {

                        return;

                }

                try

                {

                        NativeMethods.NotifyWinEvent(eventId, handle, -4, 0);

                        RecordAccessibilityWinEventCall();

                }

                catch (Exception ex)

                {

                        TryCaptureDiagnosticsDumpOnAccessibilityError($"RaiseControlWinEvent event={eventId}", ex);

                }

        }





        private void ScheduleListViewSelectionAccessibilityUpdate()

        {

                if (resultListView == null)

                {

                        return;

                }

                _listViewSelectionPending = true;

                if (_listViewSelectionDebounceTimer == null)

                {

                        _listViewSelectionDebounceTimer = new System.Windows.Forms.Timer();

                        _listViewSelectionDebounceTimer.Tick += (_, _) => HandleListViewSelectionTimerTick();

                }

                ScheduleListViewSelectionTimer();

        }



        private void HandleListViewSelectionTimerTick()

        {

                if (!_listViewSelectionPending)

                {

                        _listViewSelectionDebounceTimer?.Stop();

                        return;

                }

                DateTime dueUtc = GetListViewSelectionDueUtc();

                if (DateTime.UtcNow < dueUtc)

                {

                        ScheduleListViewSelectionTimer();

                        return;

                }

                ApplyPendingListViewSelectionAccessibilityUpdate();

        }



        private void ScheduleListViewSelectionTimer()

        {

                if (_listViewSelectionDebounceTimer == null)

                {

                        return;

                }

                DateTime dueUtc = GetListViewSelectionDueUtc();

                DateTime now = DateTime.UtcNow;

                int delayMs = ListViewSelectionStableDelayMs;

                if (dueUtc > now)

                {

                        delayMs = Math.Max(1, (int)Math.Ceiling((dueUtc - now).TotalMilliseconds));

                }

                _listViewSelectionDebounceTimer.Interval = delayMs;

                _listViewSelectionDebounceTimer.Stop();

                _listViewSelectionDebounceTimer.Start();

        }



        private DateTime GetListViewSelectionDueUtc()

        {

                DateTime baseUtc = _lastListViewSelectionChangeUtc;

                if (baseUtc == DateTime.MinValue)

                {

                        baseUtc = DateTime.UtcNow;

                }

                return baseUtc.AddMilliseconds(ListViewSelectionStableDelayMs);

        }



        private bool ShouldUpdateAccessibleObjectNameForSelection(bool isNvda, bool isBurstUpdate = false)

        {

                if (isNvda || isBurstUpdate || resultListView == null)

                {

                        return false;

                }



                return resultListView.Items.Count <= ListViewAccessibilityNameSyncItemLimit;

        }



        private void TryApplyBurstSelectionAccessibilityUpdate(int selectedListViewIndex, DateTime nowUtc)

        {

                if (resultListView == null || selectedListViewIndex < 0 || selectedListViewIndex >= resultListView.Items.Count)

                {

                        return;

                }



                int minIntervalMs = Math.Max(ListViewSelectionStableDelayMs, ListViewSelectionNvdaBurstUpdateMinIntervalMs);

#if DEBUG

                minIntervalMs = Math.Max(minIntervalMs, AccessibilityBurstUpdateMinIntervalMs);

#endif

                if (_lastListViewBurstAccessibilityUpdateUtc != DateTime.MinValue &&

                        (nowUtc - _lastListViewBurstAccessibilityUpdateUtc).TotalMilliseconds < minIntervalMs)

                {

                        return;

                }



                if (_listViewBurstPropertySyncCount >= ListViewBurstPropertySyncMaxUpdates)

                {

                        RecordAccessibilitySelectionApplyCall();

                        _lastListViewBurstAccessibilityUpdateUtc = nowUtc;

                        return;

                }



                ListViewItem selectedListViewItemSafe = GetSelectedListViewItemSafe();

                if (selectedListViewItemSafe == null)

                {

                        return;

                }



                // In burst mode, sync only name text to reduce COM traffic.

                UpdateListViewItemAccessibilityProperties(selectedListViewItemSafe, setRole: false, updateAccessibleObjectName: false);

                _listViewBurstPropertySyncCount++;

                RecordAccessibilitySelectionApplyCall();

                _lastListViewBurstAccessibilityUpdateUtc = nowUtc;

        }



        private void ApplyPendingListViewSelectionAccessibilityUpdate()

        {

                _listViewSelectionPending = false;

                _listViewSelectionBurstActive = false;

                _lastListViewBurstNavigationFlushUtc = DateTime.MinValue;

                _listViewBurstPropertySyncCount = 0;

                bool focusChangePending = _listViewFocusChangedPending;

                _listViewFocusChangedPending = false;

                if (_listViewSelectionDebounceTimer != null)

                {

                        _listViewSelectionDebounceTimer.Stop();

                }

                SetListViewRedrawDeferred(false);

                if (resultListView == null || !base.Visible || !HasListViewSelection())

                {

                        return;

                }

                int pendingIndex = _pendingListViewSelectionIndex;

                string pendingViewSource = _pendingListViewSelectionViewSource;

                _pendingListViewSelectionIndex = -1;

                _pendingListViewSelectionViewSource = null;

                if (!string.IsNullOrWhiteSpace(pendingViewSource) &&

                        !string.Equals(pendingViewSource, _currentViewSource, StringComparison.OrdinalIgnoreCase))

                {

                        return;

                }

                int selectedListViewIndex = GetSelectedListViewIndex();

                if (selectedListViewIndex < 0)

                {

                        selectedListViewIndex = pendingIndex;

                }

                if (selectedListViewIndex < 0 || selectedListViewIndex >= resultListView.Items.Count)

                {

                        return;

                }

                ListViewItem selectedListViewItemSafe = GetSelectedListViewItemSafe();

                if (selectedListViewItemSafe == null)

                {

                        return;

                }

#if DEBUG

                TouchUiThreadMarker($"SelectionStable index={selectedListViewIndex}");

#endif

                bool isNvda = IsNvdaRunningCached();

                bool updateAccessibleObjectName = ShouldUpdateAccessibleObjectNameForSelection(isNvda);

                Stopwatch selectionSw = null;

#if DEBUG

                selectionSw = Stopwatch.StartNew();

#endif

                UpdateListViewItemAccessibilityProperties(selectedListViewItemSafe, isNvda, updateAccessibleObjectName);

                RecordAccessibilitySelectionApplyCall();

                if (focusChangePending)

                {

                        NotifyListViewFocusChanged();

                }

                if (resultListView.ContainsFocus && ShouldUseCustomListViewSpeech())

                {

                        SpeakListViewSelectionIfNeeded(selectedListViewItemSafe);

                }

                if (resultListView.VirtualMode)

                {

                        Stopwatch notifySw = null;

#if DEBUG

                        notifySw = Stopwatch.StartNew();

#endif

                        NotifyAccessibilityClients(resultListView, AccessibleEvents.Selection, selectedListViewIndex);

                        NotifyAccessibilityClients(resultListView, AccessibleEvents.Focus, selectedListViewIndex);

#if DEBUG

                        if (notifySw != null)

                        {

                                notifySw.Stop();

                                long notifyMs = notifySw.ElapsedMilliseconds;

                                if (notifyMs >= 200)

                                {

                                        DebugLogger.LogUIThreadBlock("ListViewAccess", "NotifySelectionFocus", notifyMs);

                                }

                        }

#endif

                }

                if (_listViewSelectionBurstCount > 0 && _listViewSelectionBurstStartUtc != DateTime.MinValue)

                {

                        double durationMs = (DateTime.UtcNow - _listViewSelectionBurstStartUtc).TotalMilliseconds;

                        DebugLogger.Log(DebugLogger.LogLevel.Info, "ListViewBrowse",

                                $"快速浏览批次结束 count={_listViewSelectionBurstCount} durationMs={durationMs:F0} lastIndex={selectedListViewIndex}");

                        _listViewSelectionBurstCount = 0;

                        _listViewSelectionBurstStartUtc = DateTime.MinValue;

                        _lastListViewBurstAccessibilityUpdateUtc = DateTime.MinValue;

                        _lastListViewBurstNavigationFlushUtc = DateTime.MinValue;

                        _listViewBurstPropertySyncCount = 0;

                }

#if DEBUG

                if (_lastListViewSelectionChangeUtc != DateTime.MinValue)

                {

                        double stableDelayMs = (DateTime.UtcNow - _lastListViewSelectionChangeUtc).TotalMilliseconds;

                        if (stableDelayMs >= 1000)

                        {

                                DebugLogger.LogUIThreadBlock("ListViewBrowse",

                                        $"FocusStableDelay {stableDelayMs:F0}ms index={selectedListViewIndex}", (long)stableDelayMs);

                        }

                }

#endif

#if DEBUG

                if (selectionSw != null)

                {

                        selectionSw.Stop();

                        long selectionMs = selectionSw.ElapsedMilliseconds;

                        if (selectionMs >= 200)

                        {

                                DebugLogger.LogUIThreadBlock("ListViewAccess", $"SelectionAccessibility index={selectedListViewIndex}", selectionMs);

                        }

                }

#endif

                Debug.WriteLine($"[MainForm] 列表焦点变化稳定后应用无障碍更新 index={selectedListViewIndex}");

        }



  private void resultListView_SelectedIndexChanged(object sender, EventArgs e)

	{

		HandleListViewSelectionChanged();

	}



	private void resultListView_VirtualItemsSelectionRangeChanged(object sender, ListViewVirtualItemsSelectionRangeChangedEventArgs e)

	{

		HandleListViewSelectionChanged();

	}



	private void HandleListViewSelectionChanged()

	{

		if (!base.Visible || !HasListViewSelection())

		{

                        if (resultListView == null || resultListView.Items.Count == 0 || !_listViewSelectionBurstActive)

                        {

                                SetListViewRedrawDeferred(false);

                                _listViewBurstPropertySyncCount = 0;

                                _listViewFocusChangedPending = false;

                                _nvdaBufferedNavigationDelta = 0;

                        }

			return;

		}

		int selectedListViewIndex = GetSelectedListViewIndex();

		if (selectedListViewIndex < 0)

		{

			return;

		}

#if DEBUG

                TouchUiThreadMarker($"SelectionChanged index={selectedListViewIndex}");

#endif

                RecordAccessibilitySelectionChangedCall();

                DateTime now = DateTime.UtcNow;

                bool isRapid = _lastListViewSelectionChangeUtc != DateTime.MinValue &&

                        (now - _lastListViewSelectionChangeUtc).TotalMilliseconds <= ListViewSelectionBurstThresholdMs;

                _lastListViewSelectionChangeUtc = now;

                if (_lastListViewFocusedIndex != selectedListViewIndex)

                {

                        _lastListViewFocusedIndex = selectedListViewIndex;

                        if (_listViewSelectionBurstActive || isRapid)

                        {

                                _listViewFocusChangedPending = true;

                        }

                        else

                        {

                                _listViewFocusChangedPending = false;

                                Debug.WriteLine($"[MainForm] Selection changed index={selectedListViewIndex}");

                                NotifyListViewFocusChanged();

                        }

                }

                if (!string.IsNullOrWhiteSpace(_deferredPlaybackFocusViewSource) && string.Equals(_deferredPlaybackFocusViewSource, _currentViewSource, StringComparison.OrdinalIgnoreCase))

                {

                        _deferredPlaybackFocusViewSource = null;

                }

                if (string.Equals(_currentViewSource, "user_cloud", StringComparison.OrdinalIgnoreCase))

                {

                        ListViewItem selectedListViewItemSafe = GetSelectedListViewItemSafe();

                        if (selectedListViewItemSafe != null)

                        {

                                SongInfo songFromListViewItem = GetSongFromListViewItem(selectedListViewItemSafe);

                                if (songFromListViewItem != null && songFromListViewItem.IsCloudSong && !string.IsNullOrEmpty(songFromListViewItem.CloudSongId))

                                {

                                        _lastSelectedCloudSongId = songFromListViewItem.CloudSongId;

                                }

                        }

                }

                _pendingListViewSelectionIndex = selectedListViewIndex;

                _pendingListViewSelectionViewSource = _currentViewSource;

                if (_listViewSelectionBurstActive)

                {

                        _listViewSelectionBurstCount++;

                        if (_listViewSelectionBurstCount % 50 == 0)

                        {

                                DebugLogger.Log(DebugLogger.LogLevel.Info, "ListViewBrowse",

                                        $"快速浏览批次计数 count={_listViewSelectionBurstCount} lastIndex={selectedListViewIndex}");

                        }

                        TryApplyBurstSelectionAccessibilityUpdate(selectedListViewIndex, now);

                        TrimBurstNavigationBacklog(now);

                        if (!_listViewSelectionPending)

                        {

                                ScheduleListViewSelectionAccessibilityUpdate();

                        }

                        return;

                }

                if (isRapid)

                {

                        if (!_listViewSelectionBurstActive)

                        {

                                _listViewSelectionBurstActive = true;

                                _listViewSelectionBurstCount = 0;

                                _listViewBurstPropertySyncCount = 0;

                                _listViewFocusChangedPending = true;

                                SetListViewVisualAdjustDeferred(true);

                                _listViewSelectionBurstStartUtc = now;

                                Debug.WriteLine($"[MainForm] 列表焦点进入快速浏览模式 index={selectedListViewIndex}");

                                DebugLogger.Log(DebugLogger.LogLevel.Info, "ListViewBrowse",

                                        $"快速浏览模式开始 index={selectedListViewIndex}");

                        }

                        _listViewSelectionBurstCount++;

                        if (_listViewSelectionBurstCount % 50 == 0)

                        {

                                DebugLogger.Log(DebugLogger.LogLevel.Info, "ListViewBrowse",

                                        $"快速浏览批次计数 count={_listViewSelectionBurstCount} lastIndex={selectedListViewIndex}");

                        }

                        _listViewSelectionBurstActive = true;

                        TryApplyBurstSelectionAccessibilityUpdate(selectedListViewIndex, now);

                        TrimBurstNavigationBacklog(now);

                        if (!_listViewSelectionPending)

                        {

                                ScheduleListViewSelectionAccessibilityUpdate();

                        }

                        return;

                }

                ApplyPendingListViewSelectionAccessibilityUpdate();

	}



	private void AnnounceVirtualListViewSelection(int index)

	{

		if (resultListView != null && resultListView.Items.Count != 0)

		{

			index = Math.Max(0, Math.Min(index, checked(resultListView.Items.Count - 1)));

                        bool isNvda = IsNvdaRunningCached();

			ListViewItem listViewItem = null;

			try

			{

				listViewItem = resultListView.Items[index];

			}

			catch

			{

				listViewItem = null;

			}

			if (listViewItem != null && !isNvda)

			{

				UpdateListViewItemAccessibilityProperties(listViewItem, false);

			}

			NotifyAccessibilityClients(resultListView, AccessibleEvents.Focus, 0);

			NotifyAccessibilityClients(resultListView, AccessibleEvents.Selection, index);

			NotifyAccessibilityClients(resultListView, AccessibleEvents.SelectionAdd, index);

		}

	}



        private void resultListView_Enter(object sender, EventArgs e)

        {

                QueueListViewFocusSpeech();

                PrepareListViewHeaderPrefix(allowNvda: false);

                if (IsZdsrRunningCached())

                {

                        AnnounceListViewHeaderNotification(GetListViewHeaderSpeech(), allowNvda: false);

                }

        }



        private void resultListView_GotFocus(object sender, EventArgs e)        

        {

                QueueListViewFocusSpeech();

                PrepareListViewHeaderPrefix(allowNvda: false);

                if (IsZdsrRunningCached())

                {

                        AnnounceListViewHeaderNotification(GetListViewHeaderSpeech(), allowNvda: false);

                }

        }



	private void resultListView_MouseUp(object sender, MouseEventArgs e)

	{

		if (HandleListViewRowResizeMouseUp(e))

		{

                        _isUserResizingListViewColumns = false;

			return;

		}

                if (e.Button == MouseButtons.Left)

                {

                        EndListViewColumnResizeTracking();

                }

		if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Right)

		{

			QueueListViewFocusSpeech();

			PrepareListViewHeaderPrefix(allowNvda: false);

                        if (IsZdsrRunningCached())

                        {

                                AnnounceListViewHeaderNotification(GetListViewHeaderSpeech(), allowNvda: false);

                        }

		}

	}



	private void resultListView_KeyDown(object sender, KeyEventArgs e)

	{

		if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down || e.KeyCode == Keys.Left || e.KeyCode == Keys.Right ||

                    e.KeyCode == Keys.Home || e.KeyCode == Keys.End || e.KeyCode == Keys.Prior || e.KeyCode == Keys.Next)

		{

#if DEBUG

                        TouchUiThreadMarker($"ListViewKeyDown {e.KeyCode}");

#endif

                        if (TryThrottleNvdaNavigationKeyDown(e))

                        {

                                return;

                        }

			NotifyListViewFocusChanged();

		}

		if (resultListView == null || !resultListView.VirtualMode || (e.KeyCode != Keys.Home && e.KeyCode != Keys.End && e.KeyCode != Keys.Prior && e.KeyCode != Keys.Next))

		{

			return;

		}

		int beforeIndex = GetSelectedListViewIndex();

		BeginInvoke(delegate

		{

			if (resultListView != null && resultListView.VirtualMode && resultListView.Items.Count != 0)

			{

				int selectedListViewIndex = GetSelectedListViewIndex();

				if (selectedListViewIndex >= 0 && selectedListViewIndex != beforeIndex)

				{

					AnnounceVirtualListViewSelection(selectedListViewIndex);

				}

			}

		});

	}



        private void resultListView_KeyUp(object sender, KeyEventArgs e)

        {

                if (!IsListViewNavigationKey(e.KeyCode))

                {

                        return;

                }

                ApplyBufferedNvdaNavigationDelta();

                _lastNvdaListViewNavigationUtc = DateTime.MinValue;



                FlushPendingListViewNavigationMessages(keyDownOnly: true);

                if (_listViewSelectionPending)

                {

                        ApplyPendingListViewSelectionAccessibilityUpdate();

                }

        }



	private void resultListView_KeyPress(object sender, KeyPressEventArgs e)

	{

		if (resultListView == null || !resultListView.ContainsFocus)

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

		NotifyListViewFocusChanged();

		int total = GetResultListViewItemCount();

		if (total <= 0)

		{

			return;

		}

		DateTime now = DateTime.UtcNow;

		if (_listViewTypeSearchLastInputUtc == DateTime.MinValue || (now - _listViewTypeSearchLastInputUtc).TotalMilliseconds > ListViewTypeSearchTimeoutMs)

		{

			_listViewTypeSearchBuffer = string.Empty;

		}

		_listViewTypeSearchLastInputUtc = now;

		_listViewTypeSearchBuffer += e.KeyChar;

		string search = _listViewTypeSearchBuffer;

		int startIndex = GetSelectedListViewIndex();

		if (startIndex < 0)

		{

			startIndex = 0;

		}

#if DEBUG

                Stopwatch searchSw = Stopwatch.StartNew();

#endif

		int targetIndex = FindListViewItemIndexByPrimaryText(search, startIndex, forward: true);

		if (targetIndex < 0 && search.Length > 1)

		{

			_listViewTypeSearchBuffer = e.KeyChar.ToString();

			search = _listViewTypeSearchBuffer;

			targetIndex = FindListViewItemIndexByPrimaryText(search, startIndex, forward: true);

		}

#if DEBUG

                if (searchSw != null)

                {

                        searchSw.Stop();

                        long searchMs = searchSw.ElapsedMilliseconds;

                        if (searchMs >= 200)

                        {

                                DebugLogger.LogUIThreadBlock("ListViewSearch",

                                        $"TypeSearch len={search.Length} total={total}", searchMs);

                        }

                }

#endif

		if (targetIndex >= 0)

		{

			EnsureListSelectionWithoutFocus(targetIndex);

		}

		e.Handled = true;

	}



	private void resultListView_HandleCreated(object sender, EventArgs e)

	{

		ScheduleResultListViewLayoutUpdate();

		ScheduleListViewAccessibilityRefresh();

	}



	private void resultListView_RetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e)

	{

		if (!_isVirtualSongListActive)

		{

			e.Item = new ListViewItem(new string[6])

			{

				Tag = null

			};

			return;

		}

		int itemIndex = e.ItemIndex;

#if DEBUG

                TouchUiThreadMarker($"RetrieveVirtualItem index={itemIndex}");

                Stopwatch retrieveSw = Stopwatch.StartNew();

#endif

		ListViewItem listViewItem = TryGetCachedVirtualItem(itemIndex);

		if (listViewItem != null)

		{

			e.Item = listViewItem;

			return;

		}

		ListViewItem item = BuildVirtualItemByIndex(itemIndex);

		ApplyVirtualItemAccessibility(item, itemIndex);

		e.Item = item;

#if DEBUG

                if (retrieveSw != null)

                {

                        retrieveSw.Stop();

                        long retrieveMs = retrieveSw.ElapsedMilliseconds;

                        if (retrieveMs >= 200)

                        {

                                DebugLogger.LogUIThreadBlock("ListViewVirtual",

                                        $"RetrieveVirtualItem index={itemIndex}", retrieveMs);

                        }

                }

#endif

	}



	private void resultListView_CacheVirtualItems(object sender, CacheVirtualItemsEventArgs e)

	{

		if (!_isVirtualSongListActive)

		{

			return;

		}

#if DEBUG

                TouchUiThreadMarker($"CacheVirtualItems {e.StartIndex}-{e.EndIndex}");

                Stopwatch cacheSw = Stopwatch.StartNew();

#endif

		checked

		{

			int num = GetVirtualSongListSize() - 1;

			if (num < 0)

			{

				ResetVirtualItemCache();

				return;

			}

			int num2 = Math.Max(0, Math.Min(e.StartIndex, num));

			int num3 = Math.Max(num2, Math.Min(e.EndIndex, num));

			if (_virtualItemCache == null || num2 < _virtualCacheStartIndex || num3 > _virtualCacheEndIndex)

			{

				_virtualCacheStartIndex = num2;

				_virtualCacheEndIndex = num3;

				int num4 = num3 - num2 + 1;

				_virtualItemCache = new ListViewItem[num4];

				for (int i = 0; i < num4; i++)

				{

					int num5 = num2 + i;

					ListViewItem listViewItem = BuildVirtualItemByIndex(num5);

					ApplyVirtualItemAccessibility(listViewItem, num5);

					_virtualItemCache[i] = listViewItem;

				}

			}

		}

#if DEBUG

                if (cacheSw != null)

                {

                        cacheSw.Stop();

                        long cacheMs = cacheSw.ElapsedMilliseconds;

                        if (cacheMs >= 200)

                        {

                                DebugLogger.LogUIThreadBlock("ListViewVirtual",

                                        $"CacheVirtualItems range={e.StartIndex}-{e.EndIndex}", cacheMs);

                        }

                }

#endif

	}



	private void resultListView_SearchForVirtualItem(object sender, SearchForVirtualItemEventArgs e)

	{

		if (resultListView == null || !_isVirtualSongListActive || !resultListView.VirtualMode)

		{

			e.Index = -1;

			return;

		}

		string search = e.Text ?? string.Empty;

		if (string.IsNullOrWhiteSpace(search))

		{

			e.Index = -1;

			return;

		}

		bool forward = e.Direction != SearchDirectionHint.Up;

		e.Index = FindVirtualItemIndexByPrimaryText(search, e.StartIndex, forward);

	}



	private void ApplyVirtualItemAccessibility(ListViewItem item, int itemIndex)

	{

		if (item == null)

		{

			return;

		}

		EnsureSubItemCount(item, 6);

		string text = BuildListViewItemAccessibleName(item);

                bool allowEmpty = TtsHelper.IsBoyPcReaderActive();

		if (string.IsNullOrWhiteSpace(text) && !allowEmpty)

		{

			return;

		}

		item.SubItems[0].Text = text;

		if (resultListView != null && resultListView.IsHandleCreated)

		{

                        int? role = (IsNvdaRunningCached() ? new int?(41) : ((int?)null));

                        AccessibilityPropertyService.TrySetListItemProperties(resultListView.Handle, itemIndex, text, role);

                }

        }



        private void QueueListViewFocusSpeech()

        {

                if (resultListView != null && resultListView.Items.Count != 0 && ShouldUseCustomListViewSpeech())

                {

                        TtsHelper.StopSpeaking();

                        _listViewFocusSpeechCts?.Cancel();

                        _listViewFocusSpeechCts?.Dispose();

                        _listViewFocusSpeechCts = new CancellationTokenSource();

                        CancellationToken token = _listViewFocusSpeechCts.Token;

                        _ = QueueListViewFocusSpeechAsync(token);

                }

        }



        private void PrepareListViewHeaderPrefix(string header = null, bool allowNvda = false)

        {

                if (resultListView == null || resultListView.Items.Count == 0)

                {

                        return;

                }

                if (!allowNvda && IsNvdaRunningCached())

                {

                        return;

                }

                string resolved = header ?? GetListViewHeaderSpeech();

                if (string.IsNullOrWhiteSpace(resolved))

                {

                        return;

                }

                _pendingListHeaderPrefix = resolved.Trim();

                _pendingListHeaderViewSource = _currentViewSource;

                ApplyPendingListViewHeaderPrefix();

                QueueListViewHeaderFocusSequence();

        }



        private void ApplyPendingListViewHeaderPrefix()

        {

                if (string.IsNullOrWhiteSpace(_pendingListHeaderPrefix))

                {

                        return;

                }

                if (resultListView == null || !resultListView.ContainsFocus || resultListView.Items.Count == 0)

                {

                        return;

                }

                int focusedIndex = GetFocusedListViewIndex();

                if (focusedIndex < 0)

                {

                        focusedIndex = GetSelectedListViewIndex();

                }

                if (focusedIndex < 0 || focusedIndex >= resultListView.Items.Count)

                {

                        return;

                }

                ListViewItem item = resultListView.Items[focusedIndex];

                bool isNvda = IsNvdaRunningCached();

                UpdateListViewItemAccessibilityProperties(item, isNvda, updateAccessibleObjectName: !isNvda);

                NotifyAccessibilityClients(resultListView, AccessibleEvents.NameChange, focusedIndex);

        }



        private void QueueListViewHeaderFocusSequence()

        {

                if (resultListView == null || resultListView.Items.Count == 0)

                {

                        return;

                }

                if (!resultListView.ContainsFocus)

                {

                        return;

                }

                bool isZdsr = IsZdsrRunningCached();

                bool isBoy = TtsHelper.IsBoyPcReaderActive();

                if (!isBoy || isZdsr)

                {

                        return;

                }

                int targetIndex = GetFocusedListViewIndex();

                if (targetIndex < 0)

                {

                        targetIndex = GetSelectedListViewIndex();

                }

                if (targetIndex < 0 || targetIndex >= resultListView.Items.Count)

                {

                        return;

                }

                _listViewHeaderFocusCts?.Cancel();

                _listViewHeaderFocusCts?.Dispose();

                _listViewHeaderFocusCts = new CancellationTokenSource();

                CancellationToken token = _listViewHeaderFocusCts.Token;

                _ = QueueListViewHeaderFocusSequenceAsync(targetIndex, token);

        }



        private async Task QueueListViewHeaderFocusSequenceAsync(int targetIndex, CancellationToken token)

        {

                try

                {

                        await Task.Delay(80, token);

                        if (token.IsCancellationRequested || base.IsDisposed || resultListView == null || !resultListView.ContainsFocus)

                        {

                                return;

                        }

                        if (targetIndex < 0 || targetIndex >= resultListView.Items.Count)

                        {

                                return;

                        }

                        BeginInvoke(new Action<int>(TryRaiseListViewItemAccessibleFocus), targetIndex);

                }

                catch (TaskCanceledException)

                {

                }

                catch (Exception ex)

                {

                        Debug.WriteLine("[AccessibilityHelper] 列表标题朗读顺序调整失败: " + ex.Message);

                }

        }





	private async Task QueueListViewFocusSpeechAsync(CancellationToken token)

	{

		try

		{

			await Task.Delay(120, token);

			if (token.IsCancellationRequested || base.IsDisposed || resultListView == null || !resultListView.ContainsFocus)

			{

				return;

			}

			ListViewItem item = GetListViewItemForSpeech();

			if (item != null)

			{

				string header = GetListViewHeaderSpeech();

				if (!string.IsNullOrWhiteSpace(header))

				{

					TtsHelper.SpeakText(header);

					SpeakListViewSelectionIfNeeded(item, forceRepeat: true, interrupt: false);

				}

				else

				{

					SpeakListViewSelectionIfNeeded(item, forceRepeat: true);

				}

			}

		}

		catch (TaskCanceledException)

		{

		}

		catch (Exception ex2)

		{

			Exception ex3 = ex2;

			Debug.WriteLine("[AccessibilityHelper] 列表焦点朗读失败: " + ex3.Message);

		}

	}



        private string GetListViewHeaderSpeech()

	{

		if (resultListView == null)

		{

			return null;

		}

		string text = resultListView.AccessibleName ?? string.Empty;

		text = text.Trim();

		if (string.IsNullOrWhiteSpace(text))

		{

			return "列表内容";

		}

		return text;

	}



        private ListViewItem GetListViewItemForSpeech()

	{

		if (resultListView == null || resultListView.Items.Count == 0)

		{

			return null;

		}

		ListViewItem selectedListViewItemSafe = GetSelectedListViewItemSafe();

		if (selectedListViewItemSafe != null)

		{

			return selectedListViewItemSafe;

		}

		if (resultListView.FocusedItem != null)

		{

			return resultListView.FocusedItem;

		}

		return resultListView.Items[0];

	}



	private void SpeakListViewSelectionIfNeeded(ListViewItem item, bool forceRepeat = false, bool interrupt = true)

	{

		if (item == null)

		{

			return;

		}

		string text = BuildListViewItemSpeech(item);

		if (!string.IsNullOrWhiteSpace(text))

		{

			bool flag = _lastListViewSpokenIndex == item.Index && string.Equals(_lastListViewSpokenViewSource, _currentViewSource, StringComparison.OrdinalIgnoreCase) && string.Equals(_lastListViewSpokenText, text, StringComparison.Ordinal);

			DateTime utcNow = DateTime.UtcNow;

			if (!flag || (forceRepeat && !((utcNow - _lastListViewSpokenAt).TotalMilliseconds < 200.0)))

			{

				_lastListViewSpokenIndex = item.Index;

				_lastListViewSpokenViewSource = _currentViewSource;

				_lastListViewSpokenText = text;

				_lastListViewSpokenAt = utcNow;

				TtsHelper.SpeakText(text, interrupt);

			}

		}

	}



        private string BuildListViewItemAccessibleName(ListViewItem item)

        {

                return BuildListViewItemAccessibleName(item, consumeHeader: true);

        }



        private string BuildListViewItemSearchText(ListViewItem item)

        {

                return BuildListViewItemAccessibleName(item, consumeHeader: false);

        }



        private string BuildListViewItemAccessibleName(ListViewItem item, bool consumeHeader)

        {

                if (item == null)

                {

                        return string.Empty;

                }

                string baseSpeech;

                if (TtsHelper.IsBoyPcReaderActive())

                {

                        string indexText = (item.SubItems.Count > 1) ? (item.SubItems[1]?.Text?.Trim() ?? string.Empty) : string.Empty;

                        if (string.IsNullOrWhiteSpace(indexText))

                        {

                                baseSpeech = string.Empty;

                        }

                        else if (_hideSequenceNumbers || IsAlwaysSequenceHiddenView())

                        {

                                baseSpeech = string.Empty;

                        }

                        else

                        {

                                baseSpeech = indexText;

                        }

                }

                else

                {

                        baseSpeech = BuildListViewItemSpeech(item);

                }



                if (consumeHeader && !IsNvdaRunningCached() && TryConsumeListHeaderPrefix(item, out string header))

                {

                        if (string.IsNullOrWhiteSpace(baseSpeech))

                        {

                                return header;

                        }

                        return header + ", " + baseSpeech;

                }



                return baseSpeech;

        }



        private static string BuildListViewItemSpeech(ListViewItem item)

        {

                List<string> list = new List<string>();

                for (int i = 1; i < item.SubItems.Count; i = checked(i + 1))

                {

			string text = item.SubItems[i]?.Text?.Trim() ?? string.Empty;

			if (!string.IsNullOrWhiteSpace(text))

			{

				list.Add(text);

			}

                }

                return string.Join(", ", list);

        }



        private void AnnounceListViewHeaderNotification(string header, bool allowNvda)

        {

                if (string.IsNullOrWhiteSpace(header))

                {

                        return;

                }

                if (!allowNvda && IsNvdaRunningCached())

                {

                        return;

                }

                if (TtsHelper.IsBoyPcReaderActive())

                {

                        TtsHelper.SpeakText(header, interrupt: false);

                        return;

                }

                RaiseAccessibilityAnnouncementUiOnly(header, AutomationNotificationProcessing.CurrentThenMostRecent, AutomationLiveSetting.Polite);

        }



        private void AnnounceUiMessage(string text, bool interrupt = true)

        {

                if (string.IsNullOrWhiteSpace(text))

                {

                        return;

                }

                bool spoken = interrupt

                        ? TtsHelper.SpeakPriorityText(text)

                        : TtsHelper.SpeakText(text, interrupt: false);

                if (spoken)

                {

                        return;

                }

                AutomationNotificationProcessing processing = interrupt

                        ? AutomationNotificationProcessing.ImportantMostRecent

                        : AutomationNotificationProcessing.CurrentThenMostRecent;

                AutomationLiveSetting liveSetting = interrupt ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite;

                RaiseAccessibilityAnnouncementUiOnly(text, processing, liveSetting);

        }



        private bool TryConsumeListHeaderPrefix(ListViewItem item, out string header)

        {

                header = string.Empty;

                if (string.IsNullOrWhiteSpace(_pendingListHeaderPrefix))

                {

                        return false;

                }

                if (!string.Equals(_pendingListHeaderViewSource ?? string.Empty, _currentViewSource ?? string.Empty, StringComparison.OrdinalIgnoreCase))

                {

                        _pendingListHeaderPrefix = null;

                        _pendingListHeaderViewSource = null;

                        return false;

                }

                int focusedIndex = GetFocusedListViewIndex();

                if (focusedIndex < 0)

                {

                        focusedIndex = GetSelectedListViewIndex();

                }

                if (focusedIndex < 0 || item.Index != focusedIndex)

                {

                        return false;

                }

                header = _pendingListHeaderPrefix ?? string.Empty;

                _pendingListHeaderPrefix = null;

                _pendingListHeaderViewSource = null;

                return !string.IsNullOrWhiteSpace(header);

        }



        private void NotifyAccessibilityClients(Control control, AccessibleEvents accEvent, int childID)

        {

                if (control == null)

                {

                        return;

                }



                bool suppressed = ShouldThrottleBurstListViewAccessibilityEvent(control, accEvent);

                RecordAccessibilityNotifyCall(suppressed);

                if (suppressed)

                {

                        return;

                }



                Stopwatch sw = null;

#if DEBUG

                sw = Stopwatch.StartNew();

#endif

                try

                {

                        if (_accessibilityNotifyClientsMethod != null)

                        {

                                _accessibilityNotifyClientsMethod.Invoke(control, new object[2] { accEvent, childID });

                        }

                }

                catch (Exception ex)

                {

                        Debug.WriteLine("[AccessibilityHelper] AccessibilityNotifyClients failed: " + ex.Message);

                        TryCaptureDiagnosticsDumpOnAccessibilityError($"NotifyAccessibilityClients {control.Name} {accEvent} child={childID}", ex);

                }

#if DEBUG

                if (sw != null)

                {

                        sw.Stop();

                        long ms = sw.ElapsedMilliseconds;

                        if (ms >= 200)

                        {

                                DebugLogger.LogUIThreadBlock("Accessibility", $"Notify {control.Name} {accEvent} child={childID}", ms);

                        }

                }

#endif

        }





    }

}

