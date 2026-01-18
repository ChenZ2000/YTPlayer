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

                [DllImport("user32.dll")]
                public static extern void NotifyWinEvent(uint eventId, nint hwnd, int idObject, int idChild);

                [DllImport("user32.dll", CharSet = CharSet.Auto)]
                public static extern nint SendMessage(nint hWnd, int msg, nint wParam, ref LVITEM lParam);
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
            if ((!_pendingSongFocusSatisfied || string.IsNullOrWhiteSpace(_pendingSongFocusSatisfiedViewSource) || !string.Equals(_pendingSongFocusSatisfiedViewSource, _currentViewSource, StringComparison.OrdinalIgnoreCase)) && !IsListAutoFocusSuppressed && resultListView.Items.Count != 0)
            {
                int targetIndex = ((pendingFocusIndex >= 0) ? pendingFocusIndex : ((resultListView.SelectedIndices.Count > 0) ? resultListView.SelectedIndices[0] : 0));
                RestoreListViewFocus(targetIndex);
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
		RefreshListViewAccessibilityProperties();
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
						UpdateListViewItemAccessibilityProperties(listViewItem, setRole);
					}
				}
			}
		}
		finally
		{
			resultListView.EndUpdate();
		}
		if (selectedListViewItemSafe != null)
		{
			UpdateListViewItemAccessibilityProperties(selectedListViewItemSafe, setRole);
		}
        if (flag)
        {
                QueueFocusedListViewItemRefreshAnnouncement(focusedListViewIndex);
        }
        TryDispatchPendingPlaceholderPlayback(updatedSongs);
}

	private void QueueFocusedListViewItemRefreshAnnouncement(int expectedIndex)
	{
		if (resultListView == null || expectedIndex < 0 || !resultListView.IsHandleCreated)
		{
			return;
		}
		BeginInvoke(delegate
		{
			if (resultListView != null && resultListView.IsHandleCreated && resultListView.ContainsFocus)
			{
				int focusedListViewIndex = GetFocusedListViewIndex();
				if (focusedListViewIndex == expectedIndex && focusedListViewIndex >= 0 && focusedListViewIndex < resultListView.Items.Count)
				{
					ListViewItem item = resultListView.Items[focusedListViewIndex];
					UpdateListViewItemAccessibilityProperties(item, IsNvdaRunningCached());
					AnnounceListViewItemAlert(item);
					if (ShouldUseCustomListViewSpeech())
					{
						SpeakListViewSelectionIfNeeded(item, forceRepeat: true, interrupt: false);
					}
								else
								{
									NotifyAccessibilityClients(resultListView, AccessibleEvents.NameChange, focusedListViewIndex);
								}
							}
						}
					});
	}

	private void AnnounceListViewItemAlert(ListViewItem item)
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
				RaiseAccessibilityAnnouncement(text);
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
                        RaiseAccessibilityAnnouncement(text, preferInterrupt: true);
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
                                        toolStripStatusLabel1.AccessibleName = "状态栏";
                                        toolStripStatusLabel1.AccessibleDescription = "状态栏";
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
		bool setRole = IsNvdaRunningCached();
		if (_isVirtualSongListActive && resultListView.VirtualMode)
		{
			RefreshVirtualListViewAccessibilityProperties(setRole);
			return;
		}
		if (resultListView.Items.Count >= 1000)
		{
			RefreshVirtualListViewAccessibilityProperties(setRole);
			return;
		}
		for (int i = 0; i < resultListView.Items.Count; i = checked(i + 1))
		{
			ListViewItem item = resultListView.Items[i];
			UpdateListViewItemAccessibilityProperties(item, setRole);
		}
		UpdateListViewAccessibleObjectNames();
	}

	private void RefreshVirtualListViewAccessibilityProperties(bool setRole)
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
				UpdateListViewItemAccessibilityProperties(item, setRole);
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
		if (item == null || resultListView == null || !resultListView.IsHandleCreated)
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
		if (item.SubItems[0].Text != text)
		{
			item.SubItems[0].Text = text;
		}
                int? role = (setRole ? new int?(41) : ((int?)null));
                try
                {
                        AccessibilityPropertyService.TrySetListItemProperties(resultListView.Handle, item.Index, text, role);
                        UpdateListViewAccessibleObjectName(item, text);
                }
                catch
                {
                }
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
                }
                catch
                {
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
                }
                catch
                {
                }
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
			return;
		}
		int selectedListViewIndex = GetSelectedListViewIndex();
		if (selectedListViewIndex < 0)
		{
			return;
		}
                if (_lastListViewFocusedIndex != selectedListViewIndex)
                {
                        _lastListViewFocusedIndex = selectedListViewIndex;
                        Debug.WriteLine($"[MainForm] 用户选择变化，保存索引={selectedListViewIndex}");
                }
                if (!string.IsNullOrWhiteSpace(_deferredPlaybackFocusViewSource) && string.Equals(_deferredPlaybackFocusViewSource, _currentViewSource, StringComparison.OrdinalIgnoreCase))
                {
                        _deferredPlaybackFocusViewSource = null;
                }
                ListViewItem selectedListViewItemSafe = GetSelectedListViewItemSafe();
		if (selectedListViewItemSafe == null)
		{
			return;
		}
		UpdateListViewItemAccessibilityProperties(selectedListViewItemSafe, IsNvdaRunningCached());
		if (resultListView.ContainsFocus && ShouldUseCustomListViewSpeech())
		{
			SpeakListViewSelectionIfNeeded(selectedListViewItemSafe);
		}
                if (resultListView.VirtualMode)
                {
                        NotifyAccessibilityClients(resultListView, AccessibleEvents.Selection, selectedListViewIndex);
                        NotifyAccessibilityClients(resultListView, AccessibleEvents.Focus, selectedListViewIndex);
                }
		if (string.Equals(_currentViewSource, "user_cloud", StringComparison.OrdinalIgnoreCase))
		{
			SongInfo songFromListViewItem = GetSongFromListViewItem(selectedListViewItemSafe);
			if (songFromListViewItem != null && songFromListViewItem.IsCloudSong && !string.IsNullOrEmpty(songFromListViewItem.CloudSongId))
			{
				_lastSelectedCloudSongId = songFromListViewItem.CloudSongId;
			}
		}
	}

	private void AnnounceVirtualListViewSelection(int index)
	{
		if (resultListView != null && resultListView.Items.Count != 0)
		{
			index = Math.Max(0, Math.Min(index, checked(resultListView.Items.Count - 1)));
			ListViewItem listViewItem = null;
			try
			{
				listViewItem = resultListView.Items[index];
			}
			catch
			{
				listViewItem = null;
			}
			if (listViewItem != null)
			{
				UpdateListViewItemAccessibilityProperties(listViewItem, IsNvdaRunningCached());
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

	private void resultListView_HandleCreated(object sender, EventArgs e)
	{
		RefreshListViewAccessibilityProperties();
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
		ListViewItem listViewItem = TryGetCachedVirtualItem(itemIndex);
		if (listViewItem != null)
		{
			e.Item = listViewItem;
			return;
		}
		ListViewItem item = BuildVirtualItemByIndex(itemIndex);
		ApplyVirtualItemAccessibility(item, itemIndex);
		e.Item = item;
	}

	private void resultListView_CacheVirtualItems(object sender, CacheVirtualItemsEventArgs e)
	{
		if (!_isVirtualSongListActive)
		{
			return;
		}
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
                UpdateListViewItemAccessibilityProperties(item, IsNvdaRunningCached());
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
                        else if (_hideSequenceNumbers || IsDefaultSequenceHiddenView())
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

                if (!IsNvdaRunningCached() && TryConsumeListHeaderPrefix(item, out string header))
                {
                        if (string.IsNullOrWhiteSpace(baseSpeech))
                        {
                                return header;
                        }
                        return header + "，" + baseSpeech;
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
                return string.Join("；", list);
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
                try
                {
                        MethodInfo method = typeof(Control).GetMethod("AccessibilityNotifyClients", BindingFlags.Instance | BindingFlags.NonPublic, null, new Type[2]
                        {
                                typeof(AccessibleEvents),
                                typeof(int)
                        }, null);
                        if (method != null)
                        {
                                method.Invoke(control, new object[2] { accEvent, childID });
                                Debug.WriteLine($"[AccessibilityHelper] 通知 {control.Name}: Event={accEvent}, ChildID={childID}");
                        }
                        else
                        {
                                Debug.WriteLine("[AccessibilityHelper] 无法找到 AccessibilityNotifyClients 方法");
                        }
                }
                catch (Exception ex)
                {
                        Debug.WriteLine("[AccessibilityHelper] 反射调用失败: " + ex.Message);
                }
        }

    }
}
