#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using YTPlayer.Models;
#pragma warning disable CS0219, CS8600, CS8601, CS8602, CS8603, CS8604, CS8618, CS8622, CS8625, CS8632, CS4014

namespace YTPlayer
{
public partial class MainForm
{
	private void ConfigureListViewForPodcasts()
	{
		DisableVirtualSongList();
		columnHeader0.Text = string.Empty;
		columnHeader1.Text = string.Empty;
		columnHeader2.Text = string.Empty;
		columnHeader3.Text = string.Empty;
		columnHeader4.Text = string.Empty;
		columnHeader5.Text = string.Empty;
	}

	private void ConfigureListViewForPodcastEpisodes()
	{
		DisableVirtualSongList();
		columnHeader0.Text = string.Empty;
		columnHeader1.Text = string.Empty;
		columnHeader2.Text = string.Empty;
		columnHeader3.Text = string.Empty;
		columnHeader4.Text = string.Empty;
		columnHeader5.Text = string.Empty;
	}

	private void DisplayPodcasts(List<PodcastRadioInfo> podcasts, bool showPagination = false, bool hasNextPage = false, int startIndex = 1, bool preserveSelection = false, string? viewSource = null, string? accessibleName = null, bool announceHeader = true, bool suppressFocus = false, bool allowSelection = true, bool includeNavigationRows = true)
	{
		MarkListViewLayoutDataChanged();
		_listLoadingPlaceholderActive = false;
		ConfigureListViewForPodcasts();
		UpdateSequenceStartIndex(startIndex);
		int num = -1;
		if (preserveSelection && resultListView.SelectedIndices.Count > 0)
		{
			num = resultListView.SelectedIndices[0];
		}
		_currentSongs.Clear();
		_currentPlaylists.Clear();
		_currentAlbums.Clear();
		_currentArtists.Clear();
		_currentListItems.Clear();
		List<PodcastRadioInfo> list = (_currentPodcasts = CloneList(podcasts));
		_currentPodcastSounds.Clear();
		_currentPodcast = null;
		ApplyPodcastSubscriptionState(_currentPodcasts);
		if (list.Count == 0)
		{
			ShowListRetryPlaceholderCore(viewSource, accessibleName, "播客列表", announceHeader, suppressFocus: IsSearchViewSource(viewSource));
			return;
		}
		resultListView.BeginUpdate();
		ResetListViewSelectionState();
		resultListView.Items.Clear();
		int num2 = startIndex;
		foreach (PodcastRadioInfo item in list)
		{
			string text = item?.DjName ?? string.Empty;
			if (!string.IsNullOrWhiteSpace(item?.SecondCategory))
			{
				text = (string.IsNullOrWhiteSpace(text) ? item.SecondCategory : (text + " / " + item.SecondCategory));
			}
			else if (!string.IsNullOrWhiteSpace(item?.Category))
			{
				text = (string.IsNullOrWhiteSpace(text) ? item.Category : (text + " / " + item.Category));
			}
			string text2 = ((item != null && item.ProgramCount > 0) ? $"{item.ProgramCount} 个节目" : string.Empty);
			ListViewItem value = new ListViewItem(new string[6]
			{
				string.Empty,
				FormatIndex(num2),
				item?.Name ?? "未知",
				text,
				text2,
				item?.Description ?? string.Empty
			})
			{
				Tag = item
			};
			SetListViewItemPrimaryText(value, value.SubItems[2].Text);
			resultListView.Items.Add(value);
			num2 = checked(num2 + 1);
		}
		if (showPagination && includeNavigationRows)
		{
			if (startIndex > 1)
			{
				ListViewItem listViewItem = resultListView.Items.Add(new ListViewItem(new string[6]
				{
					string.Empty,
					"上一页",
					string.Empty,
					string.Empty,
					string.Empty,
					string.Empty
				}));
				listViewItem.Tag = -2;
				SetListViewItemPrimaryText(listViewItem, "上一页");
			}
			if (hasNextPage)
			{
				ListViewItem listViewItem2 = resultListView.Items.Add(new ListViewItem(new string[6]
				{
					string.Empty,
					"下一页",
					string.Empty,
					string.Empty,
					string.Empty,
					string.Empty
				}));
				listViewItem2.Tag = -3;
				SetListViewItemPrimaryText(listViewItem2, "下一页");
			}
			if (startIndex > 1 || hasNextPage)
			{
				ListViewItem listViewItem3 = resultListView.Items.Add(new ListViewItem(new string[6]
				{
					string.Empty,
					"跳转",
					string.Empty,
					string.Empty,
					string.Empty,
					string.Empty
				}));
				listViewItem3.Tag = -4;
				SetListViewItemPrimaryText(listViewItem3, "跳转");
			}
		}
		EndListViewUpdateAndRefreshAccessibility();
                        string accessibleName2 = accessibleName ?? "播客列表";
                        ApplyListViewContext(viewSource, accessibleName2, announceHeader);
        int fallbackIndex = ((num >= 0) ? Math.Min(num, checked(resultListView.Items.Count - 1)) : 0);
        ApplyStandardListViewSelection(fallbackIndex, allowSelection, suppressFocus);
	}

	private void PatchPodcasts(List<PodcastRadioInfo> podcasts, int startIndex, bool showPagination = false, bool hasPreviousPage = false, bool hasNextPage = false, int pendingFocusIndex = -1, bool allowSelection = true, bool includeNavigationRows = true)
	{
		MarkListViewLayoutDataChanged();
		UpdateSequenceStartIndex(startIndex);
		int num = ((resultListView.SelectedIndices.Count > 0) ? resultListView.SelectedIndices[0] : pendingFocusIndex);
		_currentSongs.Clear();
		_currentPlaylists.Clear();
		_currentAlbums.Clear();
		_currentArtists.Clear();
		_currentListItems.Clear();
		List<PodcastRadioInfo> list = (_currentPodcasts = CloneList(podcasts));
		_currentPodcastSounds.Clear();
		_currentPodcast = null;
		ApplyPodcastSubscriptionState(_currentPodcasts);
		if (list.Count == 0)
		{
			ShowListRetryPlaceholderCore(_currentViewSource, resultListView?.AccessibleName, "播客列表", announceHeader: true, suppressFocus: IsSearchViewSource(_currentViewSource));
			return;
		}
		bool canAnnounceFocusedRefresh = !allowSelection && !IsSearchViewSource(_currentViewSource);
		int focusedListViewIndex = canAnnounceFocusedRefresh ? GetFocusedListViewIndex() : (-1);
		bool shouldAnnounceFocusedRefresh = false;
		resultListView.BeginUpdate();
		int count = list.Count;
		int count2 = resultListView.Items.Count;
		int num2 = Math.Min(count, count2);
		checked
		{
			for (int i = 0; i < num2; i++)
			{
				PodcastRadioInfo podcastRadioInfo = list[i];
				ListViewItem listViewItem = resultListView.Items[i];
				EnsureSubItemCount(listViewItem, 6);
				string beforeSpeech = null;
				bool trackFocusedChange = canAnnounceFocusedRefresh && i == focusedListViewIndex;
				if (trackFocusedChange)
				{
					beforeSpeech = BuildListViewItemSpeech(listViewItem);
				}
				string text = podcastRadioInfo?.DjName ?? string.Empty;
				if (!string.IsNullOrWhiteSpace(podcastRadioInfo?.SecondCategory))
				{
					text = (string.IsNullOrWhiteSpace(text) ? podcastRadioInfo.SecondCategory : (text + " / " + podcastRadioInfo.SecondCategory));
				}
				else if (!string.IsNullOrWhiteSpace(podcastRadioInfo?.Category))
				{
					text = (string.IsNullOrWhiteSpace(text) ? podcastRadioInfo.Category : (text + " / " + podcastRadioInfo.Category));
				}
				string text2 = ((podcastRadioInfo != null && podcastRadioInfo.ProgramCount > 0) ? $"{podcastRadioInfo.ProgramCount} 个节目" : string.Empty);
				listViewItem.SubItems[1].Text = FormatIndex(startIndex + i);
				listViewItem.SubItems[2].Text = podcastRadioInfo?.Name ?? "未知";
				listViewItem.SubItems[3].Text = text;
				listViewItem.SubItems[4].Text = text2;
				listViewItem.SubItems[5].Text = podcastRadioInfo?.Description ?? string.Empty;
				listViewItem.Tag = podcastRadioInfo;
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
				PodcastRadioInfo podcastRadioInfo2 = list[j];
				string text3 = podcastRadioInfo2?.DjName ?? string.Empty;
				if (!string.IsNullOrWhiteSpace(podcastRadioInfo2?.SecondCategory))
				{
					text3 = (string.IsNullOrWhiteSpace(text3) ? podcastRadioInfo2.SecondCategory : (text3 + " / " + podcastRadioInfo2.SecondCategory));
				}
				else if (!string.IsNullOrWhiteSpace(podcastRadioInfo2?.Category))
				{
					text3 = (string.IsNullOrWhiteSpace(text3) ? podcastRadioInfo2.Category : (text3 + " / " + podcastRadioInfo2.Category));
				}
				string text4 = ((podcastRadioInfo2 != null && podcastRadioInfo2.ProgramCount > 0) ? $"{podcastRadioInfo2.ProgramCount} 个节目" : string.Empty);
				ListViewItem value = new ListViewItem(new string[6]
				{
					string.Empty,
					FormatIndex(startIndex + j),
					podcastRadioInfo2?.Name ?? "未知",
					text3,
					text4,
					podcastRadioInfo2?.Description ?? string.Empty
				})
				{
					Tag = podcastRadioInfo2
				};
				SetListViewItemPrimaryText(value, value.SubItems[2].Text);
				resultListView.Items.Add(value);
			}
			for (int num3 = resultListView.Items.Count - 1; num3 >= count; num3--)
			{
				resultListView.Items.RemoveAt(num3);
			}
			if (unchecked(showPagination && includeNavigationRows))
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
			if (canAnnounceFocusedRefresh && shouldAnnounceFocusedRefresh)
			{
				QueueFocusedListViewItemRefreshAnnouncement(focusedListViewIndex);
			}
		}
	}

	private void DisplayPodcastEpisodes(List<PodcastEpisodeInfo> episodes, bool showPagination = false, bool hasNextPage = false, int startIndex = 1, bool preserveSelection = false, string? viewSource = null, string? accessibleName = null)
	{
		_listLoadingPlaceholderActive = false;
		ConfigureListViewForPodcastEpisodes();
		UpdateSequenceStartIndex(startIndex);
		int num = -1;
		if (preserveSelection && resultListView.SelectedIndices.Count > 0)
		{
			num = resultListView.SelectedIndices[0];
		}
		List<PodcastEpisodeInfo> list = new List<PodcastEpisodeInfo>();
		if (episodes != null)
		{
			foreach (PodcastEpisodeInfo episode in episodes)
			{
				if (episode != null)
				{
					EnsurePodcastEpisodeSong(episode);
					list.Add(episode);
				}
			}
		}
		_currentPodcastSounds = list;
		_currentSongs = _currentPodcastSounds.Select((PodcastEpisodeInfo e) => e.Song ?? new SongInfo()).ToList();
		_currentPlaylists.Clear();
		_currentAlbums.Clear();
		_currentArtists.Clear();
		_currentListItems.Clear();
		_currentPodcasts.Clear();
		if (_currentPodcastSounds.Count == 0)
		{
			ShowListRetryPlaceholderCore(viewSource, accessibleName, "播客节目", announceHeader: true, suppressFocus: IsSearchViewSource(viewSource));
			return;
		}
		resultListView.BeginUpdate();
		ResetListViewSelectionState();
		resultListView.Items.Clear();
		int num2 = startIndex;
		checked
		{
			foreach (PodcastEpisodeInfo currentPodcastSound in _currentPodcastSounds)
			{
				string text = string.Empty;
				if (!string.IsNullOrWhiteSpace(currentPodcastSound.RadioName))
				{
					text = currentPodcastSound.RadioName;
				}
				if (!string.IsNullOrWhiteSpace(currentPodcastSound.DjName))
				{
					text = (string.IsNullOrWhiteSpace(text) ? currentPodcastSound.DjName : (text + " / " + currentPodcastSound.DjName));
				}
				string text2 = currentPodcastSound.PublishTime?.ToString("yyyy-MM-dd") ?? string.Empty;
				if (currentPodcastSound.Duration > TimeSpan.Zero)
				{
					string text3 = $"{currentPodcastSound.Duration:mm\\:ss}";
					text2 = (string.IsNullOrEmpty(text2) ? text3 : (text2 + " | " + text3));
				}
				ListViewItem value = new ListViewItem(new string[6]
				{
					string.Empty,
					FormatIndex(num2),
					currentPodcastSound.Name ?? "未知",
					text,
					text2,
					currentPodcastSound.Description ?? string.Empty
				})
				{
					Tag = currentPodcastSound
				};
				SetListViewItemPrimaryText(value, value.SubItems[2].Text);
				resultListView.Items.Add(value);
				num2++;
			}
			if (showPagination)
			{
				if (startIndex > 1)
				{
					ListViewItem listViewItem = resultListView.Items.Add(new ListViewItem(new string[6]
					{
						string.Empty,
						"上一页",
						string.Empty,
						string.Empty,
						string.Empty,
						string.Empty
					}));
					listViewItem.Tag = -2;
					SetListViewItemPrimaryText(listViewItem, "上一页");
				}
				if (hasNextPage)
				{
					ListViewItem listViewItem2 = resultListView.Items.Add(new ListViewItem(new string[6]
					{
						string.Empty,
						"下一页",
						string.Empty,
						string.Empty,
						string.Empty,
						string.Empty
					}));
					listViewItem2.Tag = -3;
					SetListViewItemPrimaryText(listViewItem2, "下一页");
				}
				if (startIndex > 1 || hasNextPage)
				{
					ListViewItem listViewItem3 = resultListView.Items.Add(new ListViewItem(new string[6]
					{
						string.Empty,
						"跳转",
						string.Empty,
						string.Empty,
						string.Empty,
						string.Empty
					}));
					listViewItem3.Tag = -4;
					SetListViewItemPrimaryText(listViewItem3, "跳转");
				}
			}
                EndListViewUpdateAndRefreshAccessibility();
                ApplyListViewContext(viewSource, accessibleName, "播客节目", announceHeader: true);
			if (IsListAutoFocusSuppressed || resultListView.Items.Count <= 0)
			{
				return;
			}
			int num3 = -1;
			bool flag = false;
			if (CanApplyPendingSongFocusForView(viewSource ?? _currentViewSource))
			{
				num3 = _currentSongs.FindIndex((SongInfo s) => s != null && string.Equals(s.Id, _pendingSongFocusId, StringComparison.OrdinalIgnoreCase));
				if (num3 < 0)
				{
					num3 = _currentSongs.FindIndex((SongInfo s) => s != null && s.IsCloudSong && !string.IsNullOrEmpty(s.CloudSongId) && string.Equals(s.CloudSongId, _pendingSongFocusId, StringComparison.OrdinalIgnoreCase));
				}
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

}
}
