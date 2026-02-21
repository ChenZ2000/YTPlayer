#nullable disable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using YTPlayer.Models;
#pragma warning disable CS0219, CS8600, CS8601, CS8602, CS8603, CS8604, CS8618, CS8622, CS8625, CS8632, CS4014

namespace YTPlayer
{
public partial class MainForm
{
	private void DisplayPlaylists(List<PlaylistInfo> playlists, bool preserveSelection = false, string? viewSource = null, string? accessibleName = null, int startIndex = 1, bool showPagination = false, bool hasNextPage = false, bool announceHeader = true, bool suppressFocus = false, bool allowSelection = true)
	{
		MarkListViewLayoutDataChanged();
		_listLoadingPlaceholderActive = false;
		ConfigureListViewDefault();
		UpdateSequenceStartIndex(startIndex);
		int num = -1;
		if (preserveSelection && resultListView.SelectedIndices.Count > 0)
		{
			num = resultListView.SelectedIndices[0];
		}
		_currentSongs.Clear();
		List<PlaylistInfo> list = (_currentPlaylists = CloneList(playlists));
		_currentPlaylist = null;
		_currentPlaylistOwnedByUser = false;
		_currentAlbums.Clear();
		_currentArtists.Clear();
		_currentListItems.Clear();
		_currentPodcasts.Clear();
		_currentPodcastSounds.Clear();
		ApplyPlaylistSubscriptionState(_currentPlaylists);
		if (list == null || list.Count == 0)
		{
			ShowListRetryPlaceholderCore(viewSource, accessibleName, "歌单列表", announceHeader, suppressFocus: IsSearchViewSource(viewSource));
			return;
		}
		resultListView.BeginUpdate();
		ResetListViewSelectionState();
		resultListView.Items.Clear();
		int num2 = Math.Max(1, startIndex);
		checked
		{
			foreach (PlaylistInfo item in list)
			{
				string text = (string.IsNullOrWhiteSpace(item.Creator) ? string.Empty : item.Creator);
				ListViewItem listViewItem = new ListViewItem(new string[6]
				{
					string.Empty,
					FormatIndex(num2),
					item.Name ?? "未知",
					text,
					(item.TrackCount > 0) ? $"{item.TrackCount} 首" : string.Empty,
					item.Description ?? string.Empty
				});
				listViewItem.Tag = item;
				SetListViewItemPrimaryText(listViewItem, listViewItem.SubItems[2].Text);
				resultListView.Items.Add(listViewItem);
				num2++;
			}
			if (showPagination)
			{
				if (startIndex > 1)
				{
					ListViewItem listViewItem2 = resultListView.Items.Add(new ListViewItem(new string[6]
					{
						string.Empty,
						"上一页",
						string.Empty,
						string.Empty,
						string.Empty,
						string.Empty
					}));
					listViewItem2.Tag = -2;
					SetListViewItemPrimaryText(listViewItem2, "上一页");
				}
				if (hasNextPage)
				{
					ListViewItem listViewItem3 = resultListView.Items.Add(new ListViewItem(new string[6]
					{
						string.Empty,
						"下一页",
						string.Empty,
						string.Empty,
						string.Empty,
						string.Empty
					}));
					listViewItem3.Tag = -3;
					SetListViewItemPrimaryText(listViewItem3, "下一页");
				}
				if (startIndex > 1 || hasNextPage)
				{
					ListViewItem listViewItem4 = resultListView.Items.Add(new ListViewItem(new string[6]
					{
						string.Empty,
						"跳转",
						string.Empty,
						string.Empty,
						string.Empty,
						string.Empty
					}));
					listViewItem4.Tag = -4;
					SetListViewItemPrimaryText(listViewItem4, "跳转");
				}
			}
			EndListViewUpdateAndRefreshAccessibility();
			string text2 = accessibleName;
			if (string.IsNullOrWhiteSpace(text2))
			{
				text2 = "歌单列表";
			}
                        ApplyListViewContext(viewSource, text2, announceHeader);
			if (!allowSelection || resultListView.Items.Count <= 0 || (suppressFocus && string.IsNullOrWhiteSpace(_pendingSongFocusId)) || IsListAutoFocusSuppressed)
			{
				return;
			}
			int num3 = -1;
			bool flag = false;
			if (!string.IsNullOrWhiteSpace(_pendingSongFocusId))
			{
                        num3 = _currentPlaylists.FindIndex((PlaylistInfo p) => p != null && string.Equals(p.Id, _pendingSongFocusId, StringComparison.OrdinalIgnoreCase));
				flag = num3 >= 0;
			}
			if (num3 < 0)
			{
				num3 = ((num >= 0) ? Math.Min(num, resultListView.Items.Count - 1) : 0);
			}
			if (flag)
			{
				_pendingListFocusIndex = -1;
				_pendingListFocusViewSource = null;
			}
			num3 = ResolvePendingListFocusIndex(num3);
			EnsureListSelectionWithoutFocus(num3);
			if (flag)
			{
				_pendingSongFocusId = null;
				_pendingSongFocusViewSource = null;
				_pendingSongFocusSatisfied = true;
				_pendingSongFocusSatisfiedViewSource = viewSource ?? _currentViewSource;
				if (resultListView != null && resultListView.CanFocus)
				{
					resultListView.Focus();
				}
			}
		}
	}

	private void PatchPlaylists(List<PlaylistInfo> playlists, int startIndex, bool showPagination = false, bool hasPreviousPage = false, bool hasNextPage = false, int pendingFocusIndex = -1, bool allowSelection = true)
	{
		MarkListViewLayoutDataChanged();
		UpdateSequenceStartIndex(startIndex);
		int num = ((resultListView.SelectedIndices.Count > 0) ? resultListView.SelectedIndices[0] : pendingFocusIndex);
		_currentSongs.Clear();
		List<PlaylistInfo> list = (_currentPlaylists = CloneList(playlists));
		_currentAlbums.Clear();
		_currentArtists.Clear();
		_currentListItems.Clear();
		_currentPodcasts.Clear();
		_currentPodcastSounds.Clear();
		ApplyPlaylistSubscriptionState(_currentPlaylists);
		if (list.Count == 0)
		{
			ShowListRetryPlaceholderCore(_currentViewSource, resultListView?.AccessibleName, "歌单列表", announceHeader: true, suppressFocus: IsSearchViewSource(_currentViewSource));
			return;
		}
		int focusedListViewIndex = GetFocusedListViewIndex();
		bool shouldAnnounceFocusedRefresh = false;
		resultListView.BeginUpdate();
		int count = list.Count;
		int count2 = resultListView.Items.Count;
		int num2 = Math.Min(count, count2);
		checked
		{
			for (int i = 0; i < num2; i++)
			{
				PlaylistInfo playlistInfo = list[i];
				ListViewItem listViewItem = resultListView.Items[i];
				EnsureSubItemCount(listViewItem, 6);
				string beforeSpeech = null;
				bool trackFocusedChange = !allowSelection && i == focusedListViewIndex;
				if (trackFocusedChange)
				{
					beforeSpeech = BuildListViewItemSpeech(listViewItem);
				}
				listViewItem.SubItems[1].Text = FormatIndex(startIndex + i);
				listViewItem.SubItems[2].Text = playlistInfo.Name ?? "未知";
				listViewItem.SubItems[3].Text = (string.IsNullOrWhiteSpace(playlistInfo.Creator) ? string.Empty : playlistInfo.Creator);
				listViewItem.SubItems[4].Text = ((playlistInfo.TrackCount > 0) ? $"{playlistInfo.TrackCount} 首" : string.Empty);
				listViewItem.SubItems[5].Text = playlistInfo.Description ?? string.Empty;
				listViewItem.Tag = playlistInfo;
				SetListViewItemPrimaryText(listViewItem, listViewItem.SubItems[2].Text);
				listViewItem.ForeColor = SystemColors.WindowText;
				listViewItem.ToolTipText = null;
				if (trackFocusedChange)
				{
					string afterSpeech = BuildListViewItemSpeech(listViewItem);
					if (!string.Equals(beforeSpeech, afterSpeech, StringComparison.Ordinal))
					{
						shouldAnnounceFocusedRefresh = true;
					}
				}
			}
			for (int j = count2; j < count; j++)
			{
				PlaylistInfo playlistInfo2 = list[j];
				ListViewItem value = new ListViewItem(new string[6]
				{
					string.Empty,
					FormatIndex(startIndex + j),
					playlistInfo2.Name ?? "未知",
					string.IsNullOrWhiteSpace(playlistInfo2.Creator) ? string.Empty : playlistInfo2.Creator,
					(playlistInfo2.TrackCount > 0) ? $"{playlistInfo2.TrackCount} 首" : string.Empty,
					playlistInfo2.Description ?? string.Empty
				})
				{
					Tag = playlistInfo2
				};
				SetListViewItemPrimaryText(value, value.SubItems[2].Text);
				resultListView.Items.Add(value);
			}
			for (int num3 = resultListView.Items.Count - 1; num3 >= count; num3--)
			{
				resultListView.Items.RemoveAt(num3);
			}
			if (showPagination)
			{
				if (hasPreviousPage)
				{
					ListViewItem value2 = new ListViewItem(new string[6]
					{
						string.Empty,
						"上一页",
						string.Empty,
						string.Empty,
						string.Empty,
						string.Empty
					})
					{
						Tag = -2
					};
					SetListViewItemPrimaryText(value2, "上一页");
					resultListView.Items.Add(value2);
				}
				if (hasNextPage)
				{
					ListViewItem value3 = new ListViewItem(new string[6]
					{
						string.Empty,
						"下一页",
						string.Empty,
						string.Empty,
						string.Empty,
						string.Empty
					})
					{
						Tag = -3
					};
					SetListViewItemPrimaryText(value3, "下一页");
					resultListView.Items.Add(value3);
				}
				if (hasPreviousPage || hasNextPage)
				{
					ListViewItem value4 = new ListViewItem(new string[6]
					{
						string.Empty,
						"跳转",
						string.Empty,
						string.Empty,
						string.Empty,
						string.Empty
					})
					{
						Tag = -4
					};
					SetListViewItemPrimaryText(value4, "跳转");
					resultListView.Items.Add(value4);
				}
			}
			EndListViewUpdateAndRefreshAccessibility();
			if (allowSelection && resultListView.Items.Count > 0)
			{
				int fallbackIndex = ((num >= 0) ? Math.Min(num, resultListView.Items.Count - 1) : 0);
				fallbackIndex = ResolvePendingListFocusIndex(fallbackIndex);
				EnsureListSelectionWithoutFocus(fallbackIndex);
			}
			TryAnnounceLoadingPlaceholderReplacement();
			if (!allowSelection && shouldAnnounceFocusedRefresh)
			{
				QueueFocusedListViewItemRefreshAnnouncement(focusedListViewIndex);
			}
		}
	}


}
}
