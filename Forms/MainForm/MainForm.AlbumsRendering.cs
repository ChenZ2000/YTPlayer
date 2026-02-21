#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using YTPlayer.Core;
using YTPlayer.Models;
#pragma warning disable CS0219, CS8600, CS8601, CS8602, CS8603, CS8604, CS8618, CS8622, CS8625, CS8632, CS4014

namespace YTPlayer
{
public partial class MainForm
{
	private static (string ArtistLabel, string TrackLabel, string DescriptionLabel) BuildAlbumDisplayLabels(AlbumInfo? album)
	{
		if (album == null)
		{
			return (ArtistLabel: "未知歌手", TrackLabel: "未知曲目数", DescriptionLabel: string.Empty);
		}
		string text = (string.IsNullOrWhiteSpace(album.Artist) ? "未知" : album.Artist.Trim());
		string text2 = AlbumDisplayHelper.BuildTrackAndYearLabel(album);
		if (string.IsNullOrWhiteSpace(text2))
		{
			text2 = ((album.TrackCount > 0) ? $"{album.TrackCount} 首" : "未知");
		}
		string item = (string.IsNullOrWhiteSpace(album.Description) ? string.Empty : (album.Description ?? ""));
		return (ArtistLabel: text ?? "", TrackLabel: text2 ?? "", DescriptionLabel: item);
	}

	private void DisplayAlbums(List<AlbumInfo> albums, bool preserveSelection = false, string? viewSource = null, string? accessibleName = null, int startIndex = 1, bool showPagination = false, bool hasNextPage = false, bool announceHeader = true, bool suppressFocus = false, bool allowSelection = true)
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
		_currentPlaylists.Clear();
		List<AlbumInfo> list = (_currentAlbums = CloneList(albums));
		_currentArtists.Clear();
		_currentListItems.Clear();
		_currentPodcasts.Clear();
		_currentPodcastSounds.Clear();
		_currentPodcast = null;
		ApplyAlbumSubscriptionState(_currentAlbums);
		if (list == null || list.Count == 0)
		{
			ShowListRetryPlaceholderCore(viewSource, accessibleName, "专辑列表", announceHeader, suppressFocus: IsSearchViewSource(viewSource));
			return;
		}
		resultListView.BeginUpdate();
		ResetListViewSelectionState();
		resultListView.Items.Clear();
		int num2 = startIndex;
		checked
		{
			foreach (AlbumInfo item in list)
			{
				(string, string, string) tuple = BuildAlbumDisplayLabels(item);
				ListViewItem listViewItem = new ListViewItem(new string[6]
				{
					string.Empty,
					FormatIndex(num2),
					item.Name ?? "未知",
					tuple.Item1,
					tuple.Item2,
					tuple.Item3
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
			string text = accessibleName;
			if (string.IsNullOrWhiteSpace(text))
			{
				text = "专辑列表";
			}
                        ApplyListViewContext(viewSource, text, announceHeader);
                int fallbackIndex = ((num >= 0) ? Math.Min(num, resultListView.Items.Count - 1) : 0);
                ApplyStandardListViewSelection(fallbackIndex, allowSelection, suppressFocus);
		}
	}

	private void PatchAlbums(List<AlbumInfo> albums, int startIndex, bool showPagination = false, bool hasPreviousPage = false, bool hasNextPage = false, int pendingFocusIndex = -1, bool allowSelection = true)
	{
		MarkListViewLayoutDataChanged();
		UpdateSequenceStartIndex(startIndex);
		int num = ((resultListView.SelectedIndices.Count > 0) ? resultListView.SelectedIndices[0] : pendingFocusIndex);
		_currentSongs.Clear();
		_currentPlaylists.Clear();
		List<AlbumInfo> list = (_currentAlbums = CloneList(albums));
		_currentArtists.Clear();
		_currentListItems.Clear();
		_currentPodcasts.Clear();
		_currentPodcastSounds.Clear();
		_currentPodcast = null;
		ApplyAlbumSubscriptionState(_currentAlbums);
		if (list.Count == 0)
		{
			ShowListRetryPlaceholderCore(_currentViewSource, resultListView?.AccessibleName, "专辑列表", announceHeader: true, suppressFocus: IsSearchViewSource(_currentViewSource));
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
				AlbumInfo albumInfo = list[i];
				ListViewItem listViewItem = resultListView.Items[i];
				EnsureSubItemCount(listViewItem, 6);
				string beforeSpeech = null;
				bool trackFocusedChange = !allowSelection && i == focusedListViewIndex;
				if (trackFocusedChange)
				{
					beforeSpeech = BuildListViewItemSpeech(listViewItem);
				}
				(string, string, string) tuple = BuildAlbumDisplayLabels(albumInfo);
				listViewItem.SubItems[1].Text = FormatIndex(startIndex + i);
				listViewItem.SubItems[2].Text = albumInfo.Name ?? "未知";
				listViewItem.SubItems[3].Text = tuple.Item1;
				listViewItem.SubItems[4].Text = tuple.Item2;
				listViewItem.SubItems[5].Text = tuple.Item3;
				listViewItem.Tag = albumInfo;
				SetListViewItemPrimaryText(listViewItem, listViewItem.SubItems[2].Text);
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
				AlbumInfo albumInfo2 = list[j];
				(string, string, string) tuple2 = BuildAlbumDisplayLabels(albumInfo2);
				ListViewItem value = new ListViewItem(new string[6]
				{
					string.Empty,
					FormatIndex(startIndex + j),
					albumInfo2.Name ?? "未知",
					tuple2.Item1,
					tuple2.Item2,
					tuple2.Item3
				})
				{
					Tag = albumInfo2
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
