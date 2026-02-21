#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Forms;
using YTPlayer.Models;
#pragma warning disable CS0219, CS8600, CS8601, CS8602, CS8603, CS8604, CS8618, CS8622, CS8625, CS8632, CS4014

namespace YTPlayer
{
public partial class MainForm
{
	private void DisplayListItems(List<ListItemInfo> items, string? viewSource = null, string? accessibleName = null, bool preserveSelection = false, bool announceHeader = true, bool suppressFocus = false, bool allowSelection = true)
	{
		MarkListViewLayoutDataChanged();
		_listLoadingPlaceholderActive = false;
		ConfigureListViewDefault();
		UpdateSequenceStartIndex(1);
		int num = -1;
		if (preserveSelection && resultListView.SelectedIndices.Count > 0)
		{
			num = resultListView.SelectedIndices[0];
		}
		_currentSongs.Clear();
		_currentPlaylists.Clear();
		_currentAlbums.Clear();
		_currentArtists.Clear();
		List<ListItemInfo> list = (_currentListItems = CloneList(items));
		_currentPodcasts.Clear();
		_currentPodcastSounds.Clear();
		_currentPodcast = null;
		ApplyListItemLibraryStates(_currentListItems);
		_homeItemIndexMap.Clear();
		checked
		{
			if (string.Equals(viewSource, "homepage", StringComparison.OrdinalIgnoreCase))
			{
				Debug.WriteLine($"[HomeIndex] init 构建主页索引，项数={_currentListItems.Count}");
				for (int i = 0; i < _currentListItems.Count; i++)
				{
					string text = _currentListItems[i].CategoryId ?? _currentListItems[i].Id ?? _currentListItems[i].Name;
					if (!string.IsNullOrWhiteSpace(text) && !_homeItemIndexMap.ContainsKey(text))
					{
						_homeItemIndexMap[text] = i;
						Debug.WriteLine($"[HomeIndex] init {text} -> {i}");
					}
				}
			}
			if (list == null || list.Count == 0)
			{
				ShowListRetryPlaceholderCore(viewSource, accessibleName, "分类列表", announceHeader, suppressFocus: IsSearchViewSource(viewSource));
				return;
			}
			resultListView.BeginUpdate();
			ResetListViewSelectionState();
			resultListView.Items.Clear();
			int num2 = 1;
			foreach (ListItemInfo item in list)
			{
				string text2 = item.Name ?? "未知";
				string text3 = item.Creator ?? "";
				string text4 = item.ExtraInfo ?? "";
				string text5 = item.Description ?? string.Empty;
				if (item.Type == ListItemType.Song)
				{
					SongInfo song = item.Song;
					if (song != null && song.RequiresVip)
					{
						text2 += "  [VIP]";
					}
				}
				switch (item.Type)
				{
				case ListItemType.Playlist:
					text5 = item.Playlist?.Description ?? "";
					break;
				case ListItemType.Album:
					(text3, text4, text5) = BuildAlbumDisplayLabels(item.Album);
					break;
				case ListItemType.Song:
					text5 = ((!string.IsNullOrWhiteSpace(text5)) ? text5 : (item.Song?.FormattedDuration ?? ""));
					break;
				case ListItemType.Artist:
					if (string.IsNullOrWhiteSpace(text5) && item.Artist != null)
					{
						text5 = item.Artist.Description ?? item.Artist.BriefDesc;
					}
					break;
				case ListItemType.Podcast:
				{
					text3 = item.Podcast?.DjName ?? text3;
					PodcastRadioInfo podcast = item.Podcast;
					text4 = ((podcast != null && podcast.ProgramCount > 0) ? $"{item.Podcast.ProgramCount} 个节目" : text4);
					text5 = ((!string.IsNullOrWhiteSpace(text5)) ? text5 : (item.Podcast?.Description ?? string.Empty));
					break;
				}
				case ListItemType.PodcastEpisode:
				{
					text3 = ((!string.IsNullOrWhiteSpace(text3)) ? text3 : ((!string.IsNullOrWhiteSpace(item.PodcastEpisode?.DjName)) ? (item.PodcastEpisode.RadioName + " / " + item.PodcastEpisode.DjName) : (item.PodcastEpisode?.RadioName ?? string.Empty)));
					PodcastEpisodeInfo podcastEpisode = item.PodcastEpisode;
					if (podcastEpisode != null && podcastEpisode.PublishTime.HasValue)
					{
						text4 = item.PodcastEpisode.PublishTime.Value.ToString("yyyy-MM-dd");
					}
					if (string.IsNullOrWhiteSpace(text5))
					{
						text5 = item.PodcastEpisode?.Description ?? string.Empty;
					}
					break;
				}
				}
				ListViewItem listViewItem = new ListViewItem(new string[6]
				{
					string.Empty,
					string.Empty,
					text2,
					text3,
					text4,
					text5
				});
				listViewItem.Tag = item;
				SetListViewItemPrimaryText(listViewItem, text2);
				resultListView.Items.Add(listViewItem);
				num2++;
			}
			EndListViewUpdateAndRefreshAccessibility();
			string text6 = accessibleName;
			if (string.IsNullOrWhiteSpace(text6))
			{
				text6 = "分类列表";
			}
                        ApplyListViewContext(viewSource, text6, announceHeader);
                int fallbackIndex = ((num >= 0) ? Math.Min(num, resultListView.Items.Count - 1) : 0);
                ApplyStandardListViewSelection(fallbackIndex, allowSelection, suppressFocus);
		}
	}

	private void PatchListItems(List<ListItemInfo> items, bool showPagination = false, bool hasPreviousPage = false, bool hasNextPage = false, int pendingFocusIndex = -1, bool incremental = false, bool preserveDisplayIndex = false)
	{
		MarkListViewLayoutDataChanged();
		int num = ((resultListView.SelectedIndices.Count > 0) ? resultListView.SelectedIndices[0] : pendingFocusIndex);
		checked
		{
			if (items == null || items.Count == 0)
			{
				_currentSongs.Clear();
				_currentPlaylists.Clear();
				_currentAlbums.Clear();
				_currentArtists.Clear();
				_currentListItems = CloneList(items);
				_currentPodcasts.Clear();
				_currentPodcastSounds.Clear();
				_currentPodcast = null;
				_homeItemIndexMap.Clear();
				ShowListRetryPlaceholderCore(_currentViewSource, resultListView?.AccessibleName, "分类列表", announceHeader: true, suppressFocus: IsSearchViewSource(_currentViewSource));
				return;
			}
			if (incremental && items != null && resultListView.Items.Count == _currentListItems.Count && _currentListItems.Count == items.Count)
			{
				int focusedListViewIndex = GetFocusedListViewIndex();
				bool flag = false;
				resultListView.BeginUpdate();
				try
				{
					for (int i = 0; i < items.Count; i++)
					{
						ListItemInfo listItemInfo = items[i];
						ListItemInfo a = _currentListItems[i];
						if (IsListItemDifferent(a, listItemInfo))
						{
							_currentListItems[i] = listItemInfo;
							ListViewItem item = resultListView.Items[i];
							FillListViewItemFromListItemInfo(item, listItemInfo, i + 1, preserveDisplayIndex);
							if (!string.IsNullOrWhiteSpace(listItemInfo.CategoryId))
							{
								_homeItemIndexMap[listItemInfo.CategoryId] = i;
							}
							if (i == focusedListViewIndex)
							{
								flag = true;
							}
						}
					}
				}
				finally
				{
					EndListViewUpdateAndRefreshAccessibility();
				}
				if (flag)
				{
					QueueFocusedListViewItemRefreshAnnouncement(focusedListViewIndex);
				}
				return;
			}
			_currentSongs.Clear();
			_currentPlaylists.Clear();
			_currentAlbums.Clear();
			_currentArtists.Clear();
			_currentListItems = CloneList(items);
			_currentPodcasts.Clear();
			_currentPodcastSounds.Clear();
			_currentPodcast = null;
			ApplyListItemLibraryStates(_currentListItems);
			_homeItemIndexMap.Clear();
			if (string.Equals(_currentViewSource, "homepage", StringComparison.OrdinalIgnoreCase))
			{
				Debug.WriteLine($"[HomeIndex] init 构建主页索引，项数={_currentListItems.Count}");
				for (int j = 0; j < _currentListItems.Count; j++)
				{
					string text = _currentListItems[j].CategoryId ?? _currentListItems[j].Id ?? _currentListItems[j].Name;
					if (!string.IsNullOrWhiteSpace(text) && !_homeItemIndexMap.ContainsKey(text))
					{
						_homeItemIndexMap[text] = j;
						Debug.WriteLine($"[HomeIndex] init {text} -> {j}");
					}
				}
			}
			resultListView.BeginUpdate();
			int count = _currentListItems.Count;
			int count2 = resultListView.Items.Count;
			int num2 = Math.Min(count, count2);
			for (int k = 0; k < num2; k++)
			{
				ListItemInfo listItem = _currentListItems[k];
				ListViewItem item2 = resultListView.Items[k];
				EnsureSubItemCount(item2, 6);
				FillListViewItemFromListItemInfo(item2, listItem, k + 1, preserveDisplayIndex);
			}
			for (int l = count2; l < count; l++)
			{
				ListItemInfo listItem2 = _currentListItems[l];
				ListViewItem listViewItem = new ListViewItem(new string[6]
				{
					string.Empty,
					string.Empty,
					string.Empty,
					string.Empty,
					string.Empty,
					string.Empty
				});
				FillListViewItemFromListItemInfo(listViewItem, listItem2, l + 1, preserveDisplayIndex);
				resultListView.Items.Add(listViewItem);
			}
			for (int num3 = resultListView.Items.Count - 1; num3 >= count; num3--)
			{
				resultListView.Items.RemoveAt(num3);
			}
			if (showPagination)
			{
				if (hasPreviousPage)
				{
					ListViewItem value = new ListViewItem(new string[6]
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
					SetListViewItemPrimaryText(value, "上一页");
					resultListView.Items.Add(value);
				}
				if (hasNextPage)
				{
					ListViewItem value2 = new ListViewItem(new string[6]
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
					SetListViewItemPrimaryText(value2, "下一页");
					resultListView.Items.Add(value2);
				}
				if (hasPreviousPage || hasNextPage)
				{
					ListViewItem value3 = new ListViewItem(new string[6]
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
					SetListViewItemPrimaryText(value3, "跳转");
					resultListView.Items.Add(value3);
				}
			}
			EndListViewUpdateAndRefreshAccessibility();
			if (resultListView.Items.Count > 0)
			{
				int fallbackIndex = ((num >= 0) ? Math.Min(num, resultListView.Items.Count - 1) : 0);
				fallbackIndex = ResolvePendingListFocusIndex(fallbackIndex);
				EnsureListSelectionWithoutFocus(fallbackIndex);
			}
			TryAnnounceLoadingPlaceholderReplacement();
		}
	}

	private void FillListViewItemFromListItemInfo(ListViewItem item, ListItemInfo listItem, int displayIndex, bool preserveDisplayIndex = false)
	{
		string nameText = listItem.Name ?? "未知";
		string creatorText = listItem.Creator ?? string.Empty;
		string extraInfoText = listItem.ExtraInfo ?? string.Empty;
		string descriptionText = listItem.Description ?? string.Empty;
		if (listItem.Type == ListItemType.Song)
		{
			SongInfo song = listItem.Song;
			if (song != null && song.RequiresVip)
			{
				nameText += "  [VIP]";
			}
		}
		bool isHomePageView = string.Equals(_currentViewSource, "homepage", StringComparison.OrdinalIgnoreCase);
		EnsureSubItemCount(item, 6);
		string indexText = FormatIndex(displayIndex);
		if (isHomePageView || string.IsNullOrWhiteSpace(indexText))
		{
			item.SubItems[1].Text = string.Empty;
		}
		else if (!preserveDisplayIndex || string.IsNullOrWhiteSpace(item.SubItems[1].Text))
		{
			item.SubItems[1].Text = indexText;
		}
		item.SubItems[2].Text = nameText;
		item.SubItems[3].Text = creatorText;
		item.SubItems[4].Text = extraInfoText;
		item.SubItems[5].Text = descriptionText;
		SetListViewItemPrimaryText(item, nameText);
		item.Tag = listItem;
		UpdateListViewItemAccessibilityProperties(item, IsNvdaRunningCached());
	}


}
}
