#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using YTPlayer.Core;
using YTPlayer.Core.Download;
using YTPlayer.Models;
#pragma warning disable CS0219, CS8600, CS8601, CS8602, CS8603, CS8604, CS8618, CS8622, CS8625, CS8632, CS4014

namespace YTPlayer
{
public partial class MainForm
{
	private string GetListViewItemPrimaryText(int index)
	{
		bool useSequenceCandidate = !(_hideSequenceNumbers || IsAlwaysSequenceHiddenView());
		return GetListViewItemPrimaryText(index, useSequenceCandidate);
	}

	private string GetListViewItemPrimaryText(int index, bool useSequenceCandidate)
	{
		if (index < 0)
		{
			return string.Empty;
		}
		if (_isVirtualSongListActive && resultListView != null && resultListView.VirtualMode)
		{
			return GetVirtualItemPrimaryText(index, useSequenceCandidate);
		}
		if (resultListView != null && index < resultListView.Items.Count)
		{
			ListViewItem item = resultListView.Items[index];
			return ResolveListViewPrimaryText(item, useSequenceCandidate);
		}
		if (_currentSongs.Count > 0)
		{
			if (index < 0 || index >= _currentSongs.Count)
			{
				return string.Empty;
			}
			if (useSequenceCandidate)
			{
				return FormatIndex(checked(Math.Max(1, _currentSequenceStartIndex) + index));
			}
			SongInfo song = _currentSongs[index];
			if (song != null && string.Equals(song.Name, ListLoadingPlaceholderText, StringComparison.Ordinal))
			{
				return string.Empty;
			}
			return BuildSongPrimaryText(song);
		}
		if (_currentPlaylists.Count > 0)
		{
			return (index >= 0 && index < _currentPlaylists.Count) ? (_currentPlaylists[index]?.Name ?? string.Empty) : string.Empty;
		}
		if (_currentAlbums.Count > 0)
		{
			return (index >= 0 && index < _currentAlbums.Count) ? (_currentAlbums[index]?.Name ?? string.Empty) : string.Empty;
		}
		if (_currentArtists.Count > 0)
		{
			return (index >= 0 && index < _currentArtists.Count) ? (_currentArtists[index]?.Name ?? string.Empty) : string.Empty;
		}
		if (_currentListItems.Count > 0)
		{
			return (index >= 0 && index < _currentListItems.Count) ? (_currentListItems[index]?.Name ?? string.Empty) : string.Empty;
		}
		if (_currentPodcasts.Count > 0)
		{
			return (index >= 0 && index < _currentPodcasts.Count) ? (_currentPodcasts[index]?.Name ?? string.Empty) : string.Empty;
		}
		if (_currentPodcastSounds.Count > 0)
		{
			return (index >= 0 && index < _currentPodcastSounds.Count) ? (_currentPodcastSounds[index]?.Name ?? string.Empty) : string.Empty;
		}
		return string.Empty;
	}



	private int FindListViewItemIndexByPrimaryText(string search, int startIndex, bool forward)
	{
		if (string.IsNullOrWhiteSpace(search))
		{
			return -1;
		}
		int total = GetResultListViewItemCount();
		if (total <= 0)
		{
			return -1;
		}
		bool useSequenceCandidate = !(_hideSequenceNumbers || IsAlwaysSequenceHiddenView());
		int start = Math.Max(0, Math.Min(startIndex, total - 1));
		StringComparison comparison = StringComparison.OrdinalIgnoreCase;
		if (forward)
		{
			for (int i = start; i < total; i++)
			{
				string text = GetListViewItemPrimaryText(i, useSequenceCandidate);
				if (!string.IsNullOrEmpty(text) && text.StartsWith(search, comparison))
				{
					return i;
				}
			}
			for (int i = 0; i < start; i++)
			{
				string text = GetListViewItemPrimaryText(i, useSequenceCandidate);
				if (!string.IsNullOrEmpty(text) && text.StartsWith(search, comparison))
				{
					return i;
				}
			}
			return -1;
		}
		for (int i = start; i >= 0; i--)
		{
			string text = GetListViewItemPrimaryText(i, useSequenceCandidate);
			if (!string.IsNullOrEmpty(text) && text.StartsWith(search, comparison))
			{
				return i;
			}
		}
		for (int i = total - 1; i > start; i--)
		{
			string text = GetListViewItemPrimaryText(i, useSequenceCandidate);
			if (!string.IsNullOrEmpty(text) && text.StartsWith(search, comparison))
			{
				return i;
			}
		}
		return -1;
	}



	private void ResetListViewTypeSearchBuffer()
	{
		_listViewTypeSearchBuffer = string.Empty;
		_listViewTypeSearchLastInputUtc = DateTime.MinValue;
	}

	private void DisplaySongs(List<SongInfo> songs, bool showPagination = false, bool hasNextPage = false, int startIndex = 1, bool preserveSelection = false, string? viewSource = null, string? accessibleName = null, bool skipAvailabilityCheck = false, bool announceHeader = true, bool suppressFocus = false, bool allowSelection = true)
	{
		MarkListViewLayoutDataChanged();
		_listLoadingPlaceholderActive = false;
		ConfigureListViewDefault();
		UpdateSequenceStartIndex(startIndex);
		DebugLogListFocusState("DisplaySongs: start", viewSource);
		int num = -1;
		if (preserveSelection && resultListView.SelectedIndices.Count > 0)
		{
			num = resultListView.SelectedIndices[0];
		}
		bool flag = viewSource?.StartsWith("playlist:", StringComparison.OrdinalIgnoreCase) ?? false;
		_currentPlaylistOwnedByUser = flag && _currentPlaylist != null && IsPlaylistOwnedByUser(_currentPlaylist, GetCurrentUserId());
		List<SongInfo> list = (_currentSongs = CloneList(songs));
		ApplySongLikeStates(list);
		_currentPlaylists.Clear();
		_currentAlbums.Clear();
		_currentArtists.Clear();
		_currentListItems.Clear();
		_currentPodcasts.Clear();
		_currentPodcastSounds.Clear();
		_currentPodcast = null;
		bool flag2 = _currentPage > 1 || startIndex > 1;
		bool flag3 = ShouldUseVirtualSongList(list);
		if (list == null || list.Count == 0)
		{
			string fallbackName = (!string.IsNullOrEmpty(viewSource) && viewSource.StartsWith("search:", StringComparison.OrdinalIgnoreCase)) ? "搜索结果" : "歌曲列表";
			_currentPlaylistOwnedByUser = viewSource != null && viewSource.StartsWith("playlist:", StringComparison.OrdinalIgnoreCase) && _currentPlaylist != null && IsPlaylistOwnedByUser(_currentPlaylist, GetCurrentUserId());
			ShowListRetryPlaceholderCore(viewSource, accessibleName, fallbackName, announceHeader, suppressFocus: IsSearchViewSource(viewSource));
			DebugLogListFocusState("DisplaySongs: empty", viewSource);
			return;
		}
		resultListView.BeginUpdate();
		ResetListViewSelectionState();
		if (resultListView.VirtualMode)
		{
			resultListView.VirtualListSize = 0;
		}
		resultListView.Items.Clear();
		int num2 = startIndex;
		int num3 = 0;
		checked
		{
			foreach (SongInfo item in list)
			{
				ListViewItem listViewItem = new ListViewItem(new string[6]);
				FillListViewItemFromSongInfo(listViewItem, item, num2);
				listViewItem.Tag = num3;
				resultListView.Items.Add(listViewItem);
				num2++;
				num3++;
			}
			if (showPagination)
			{
				if (flag2)
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
				if (flag2 || hasNextPage)
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
				text = ((!string.IsNullOrEmpty(viewSource) && viewSource.StartsWith("search:", StringComparison.OrdinalIgnoreCase)) ? "搜索结果" : "歌曲列表");
			}
                        ApplyListViewContext(viewSource, text, announceHeader);
			int num4 = -1;
                        bool flag5 = false;
                        if (CanApplyPendingSongFocusForView(viewSource ?? _currentViewSource))
                        {
                                num4 = _currentSongs.FindIndex((SongInfo s) => s != null && string.Equals(s.Id, _pendingSongFocusId, StringComparison.OrdinalIgnoreCase));
                                if (num4 < 0)
                                {
                                        num4 = _currentSongs.FindIndex((SongInfo s) => s != null && s.IsCloudSong && !string.IsNullOrEmpty(s.CloudSongId) && string.Equals(s.CloudSongId, _pendingSongFocusId, StringComparison.OrdinalIgnoreCase));
                                }
                                flag5 = num4 >= 0;
                        }
                        bool deferPlaybackFocus = !string.IsNullOrWhiteSpace(_deferredPlaybackFocusViewSource) && IsSameViewSourceForFocus(_deferredPlaybackFocusViewSource, viewSource ?? _currentViewSource);
                        if (deferPlaybackFocus && !flag5)
                        {
                                suppressFocus = true;
                        }
                        else if (deferPlaybackFocus && flag5)
                        {
                                _deferredPlaybackFocusViewSource = null;
                        }
                        bool flag6 = _pendingSongFocusSatisfied && !string.IsNullOrWhiteSpace(_pendingSongFocusSatisfiedViewSource) && IsSameViewSourceForFocus(_pendingSongFocusSatisfiedViewSource, viewSource ?? _currentViewSource);
			if (allowSelection && resultListView.Items.Count > 0 && unchecked(!suppressFocus || flag5) && !IsListAutoFocusSuppressed && !flag6)
			{
				int num5 = -1;
				bool flag7 = flag5;
				if (flag5)
				{
					num5 = num4;
					_pendingListFocusIndex = -1;
					_pendingListFocusViewSource = null;
				}
				if (num5 < 0)
				{
					num5 = ((num >= 0) ? Math.Min(num, resultListView.Items.Count - 1) : 0);
				}
				num5 = ResolvePendingListFocusIndex(num5);
				EnsureListSelectionWithoutFocus(num5);
				if (flag7)
				{
					_pendingSongFocusId = null;
					_pendingSongFocusViewSource = null;
					_pendingSongFocusSatisfied = true;
					_pendingSongFocusSatisfiedViewSource = viewSource ?? _currentViewSource;
					if (string.Equals(viewSource, "user_cloud", StringComparison.OrdinalIgnoreCase))
					{
						_skipCloudRestoreOnce = true;
						if (_currentSongs != null && num5 >= 0 && num5 < _currentSongs.Count)
						{
							SongInfo songInfo = _currentSongs[num5];
							if (songInfo != null && songInfo.IsCloudSong && !string.IsNullOrEmpty(songInfo.CloudSongId))
							{
								_lastSelectedCloudSongId = songInfo.CloudSongId;
								_pendingCloudFocusId = null;
							}
						}
					}
					if (resultListView != null && resultListView.CanFocus)
					{
						resultListView.Focus();
					}
				}
			}
			DebugLogListFocusState("DisplaySongs: after selection", viewSource);
			if (!skipAvailabilityCheck)
			{
				ScheduleAvailabilityCheck(list);
			}
		}
	}

	private static void EnsureSubItemCount(ListViewItem item, int desiredCount)
	{
		while (item.SubItems.Count < desiredCount)
		{
			item.SubItems.Add(string.Empty);
		}
	}

	private static bool IsPlaceholderSong(SongInfo? song)
	{
		return song == null || string.IsNullOrWhiteSpace(song.Id);
	}

	private static SongInfo CreatePlaceholderSong(string? viewSource)
	{
		return new SongInfo
		{
			Name = ListLoadingPlaceholderText,
			ViewSource = (viewSource ?? string.Empty)
		};
	}

	private static string BuildSongPrimaryText(SongInfo? song)
	{
		if (song == null)
		{
			return string.Empty;
		}
		string text = string.IsNullOrWhiteSpace(song.Name) ? "未知" : song.Name;
		if (song.RequiresVip)
		{
			text += "  [VIP]";
		}
		return text;
	}

	private static void SetListViewItemPrimaryText(ListViewItem item, string? text)
	{
		if (item == null)
		{
			return;
		}
		string normalized = string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim();
		item.Text = normalized;
	}

	private int ResolveSongIndexInCurrentSongs(SongInfo song)
	{
		if (song == null || _currentSongs == null || _currentSongs.Count == 0)
		{
			return -1;
		}
		int index = _currentSongs.IndexOf(song);
		if (index >= 0)
		{
			return index;
		}
		if (!string.IsNullOrWhiteSpace(song.Id))
		{
			index = _currentSongs.FindIndex((SongInfo s) => s != null && string.Equals(s.Id, song.Id, StringComparison.OrdinalIgnoreCase));
		}
		return index;
	}

	private void CancelPendingPlaceholderPlayback(string? reason = null)
	{
		if (_pendingPlaceholderPlaybackIndex >= 0)
		{
			Debug.WriteLine($"[MainForm] 取消占位播放等待: index={_pendingPlaceholderPlaybackIndex}, reason={reason ?? "unknown"}");
		}
		_pendingPlaceholderPlaybackIndex = -1;
		_pendingPlaceholderPlaybackViewSource = null;
	}

	private bool TryQueuePlaceholderPlayback(SongInfo song, int? songIndex = null, string? pendingViewSource = null)
	{
		if (!IsPlaceholderSong(song))
		{
			return false;
		}

		int resolvedIndex = songIndex ?? ResolveSongIndexInCurrentSongs(song);
		if (resolvedIndex < 0)
		{
			Debug.WriteLine("[MainForm] Placeholder playback queued but index resolution failed");
			UpdateStatusBar("Song is still loading, please wait...");
			return true;
		}

		_pendingPlaceholderPlaybackIndex = resolvedIndex;
		_pendingPlaceholderPlaybackViewSource = string.IsNullOrWhiteSpace(pendingViewSource) ? _currentViewSource : pendingViewSource;
		Debug.WriteLine($"[MainForm] Queued placeholder playback: index={resolvedIndex}, view={_pendingPlaceholderPlaybackViewSource ?? "unknown"}");
		UpdateStatusBar("Song is still loading, please wait...");
		return true;
	}

	private async Task<SongInfo?> TryResolvePlaceholderSongForPlaybackAsync(SongInfo song, int? queueIndexHint, CancellationToken cancellationToken)
	{
		if (!IsPlaceholderSong(song))
		{
			return song;
		}

		int resolvedIndex = queueIndexHint ?? ResolveSongIndexInCurrentSongs(song);
		string queueSource = ResolvePlaceholderPlaybackSource();
		if (resolvedIndex < 0)
		{
			Debug.WriteLine("[MainForm] Placeholder resolve skipped: queue index unresolved");
			return null;
		}

		try
		{
			using CancellationTokenSource timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
			using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
			SongInfo? resolvedSong = await ResolvePlaceholderSongByIndexAsync(queueSource, resolvedIndex, linkedCts.Token).ConfigureAwait(continueOnCapturedContext: false);
			if (resolvedSong != null)
			{
				Debug.WriteLine($"[MainForm] Placeholder resolved on-demand: source={queueSource}, index={resolvedIndex}, id={resolvedSong.Id}");
			}
			return resolvedSong;
		}
		catch (OperationCanceledException)
		{
			Debug.WriteLine($"[MainForm] Placeholder resolve timeout/canceled: source={queueSource}, index={resolvedIndex}");
			return null;
		}
		catch (Exception ex)
		{
			Debug.WriteLine("[MainForm] Placeholder resolve exception: " + ex.Message);
			return null;
		}
	}

	private string ResolvePlaceholderPlaybackSource()
	{
		string queueSource = _playbackQueue?.QueueSource;
		if (!string.IsNullOrWhiteSpace(queueSource))
		{
			return queueSource;
		}

		return _currentViewSource ?? string.Empty;
	}

	private async Task<SongInfo?> ResolvePlaceholderSongByIndexAsync(string? viewSource, int songIndex, CancellationToken cancellationToken)
	{
		if (songIndex < 0 || cancellationToken.IsCancellationRequested)
		{
			return null;
		}

		if (_apiClient == null)
		{
			return null;
		}

		string effectiveViewSource = string.IsNullOrWhiteSpace(viewSource) ? (_currentViewSource ?? string.Empty) : viewSource;
		SongInfo? resolvedFromCurrentView = null;

		await ExecuteOnUiThreadAsync(delegate
		{
			if (string.Equals(_currentViewSource, effectiveViewSource, StringComparison.OrdinalIgnoreCase) && _currentSongs != null && songIndex >= 0 && songIndex < _currentSongs.Count)
			{
				SongInfo candidate = _currentSongs[songIndex];
				if (!IsPlaceholderSong(candidate))
				{
					resolvedFromCurrentView = candidate;
				}
			}
		}).ConfigureAwait(continueOnCapturedContext: false);

		if (resolvedFromCurrentView != null)
		{
			UpdatePlaybackQueueSongs(new Dictionary<int, SongInfo>
			{
				[songIndex] = resolvedFromCurrentView
			}, effectiveViewSource);
			return resolvedFromCurrentView;
		}

		if (!TryResolveSongIdFromCache(effectiveViewSource, songIndex, out string songId))
		{
			Debug.WriteLine($"[MainForm] Placeholder resolve cache miss: source={effectiveViewSource}, index={songIndex}");
			return null;
		}

		List<SongInfo> fetchedSongs = await FetchPlaylistSongsByIdsBatchAsync(new List<string> { songId }, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		SongInfo fetchedSong = fetchedSongs?.FirstOrDefault((SongInfo s) => s != null && string.Equals(s.Id, songId, StringComparison.OrdinalIgnoreCase));
		if (fetchedSong == null)
		{
			Debug.WriteLine($"[MainForm] Placeholder resolve fetch returned empty: source={effectiveViewSource}, index={songIndex}, id={songId}");
			return null;
		}

		ApplySongLikeStates(new SongInfo[] { fetchedSong });

		SongInfo mergedSong = MergeSongFields(fetchedSong, fetchedSong, effectiveViewSource);
		Dictionary<int, SongInfo>? patched = null;

		await ExecuteOnUiThreadAsync(delegate
		{
			if (string.Equals(_currentViewSource, effectiveViewSource, StringComparison.OrdinalIgnoreCase) && _currentSongs != null && songIndex >= 0 && songIndex < _currentSongs.Count)
			{
				SongInfo skeleton = _currentSongs[songIndex];
				mergedSong = MergeSongFields(skeleton, fetchedSong, effectiveViewSource);
				_currentSongs[songIndex] = mergedSong;
				patched = new Dictionary<int, SongInfo>
				{
					[songIndex] = mergedSong
				};
				PatchSongItemsInPlace(patched);
				TryDispatchPendingPlaceholderPlayback(patched);
			}
		}).ConfigureAwait(continueOnCapturedContext: false);

		UpdatePlaybackQueueSongs(new Dictionary<int, SongInfo>
		{
			[songIndex] = mergedSong
		}, effectiveViewSource);

		return mergedSong;
	}

	private void TryDispatchPendingPlaceholderPlayback(Dictionary<int, SongInfo> updatedSongs)
	{
		if (updatedSongs == null || updatedSongs.Count == 0 || _pendingPlaceholderPlaybackIndex < 0)
		{
			return;
		}
		if (!string.Equals(_pendingPlaceholderPlaybackViewSource, _currentViewSource, StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		if (!updatedSongs.TryGetValue(_pendingPlaceholderPlaybackIndex, out SongInfo song))
		{
			return;
		}
		if (IsPlaceholderSong(song))
		{
			return;
		}
		int index = _pendingPlaceholderPlaybackIndex;
		CancelPendingPlaceholderPlayback("resolved");
		Debug.WriteLine($"[MainForm] 占位歌曲已加载，开始播放: index={index}, id={song?.Id}");
		if (base.IsHandleCreated)
		{
			BeginInvoke((Action)(async () =>
			{
				if (song != null)
				{
					await PlaySong(song);
				}
			}));
		}
		else if (song != null)
		{
			_ = PlaySong(song);
		}
	}

	private void FillListViewItemFromSongInfo(ListViewItem item, SongInfo? song, int displayIndex)
	{
		EnsureSubItemCount(item, 6);
		if (IsPlaceholderSong(song))
		{
			item.SubItems[1].Text = FormatIndex(displayIndex);
			item.SubItems[2].Text = ListLoadingPlaceholderText;
			item.SubItems[3].Text = string.Empty;
			item.SubItems[4].Text = string.Empty;
			item.SubItems[5].Text = string.Empty;
			SetListViewItemPrimaryText(item, item.SubItems[2].Text);
			item.ForeColor = SystemColors.WindowText;
			item.ToolTipText = null;
			return;
		}
		string text = BuildSongPrimaryText(song);
		item.SubItems[1].Text = FormatIndex(displayIndex);
		item.SubItems[2].Text = text;
		item.SubItems[3].Text = string.IsNullOrWhiteSpace(song.Artist) ? string.Empty : song.Artist;
		item.SubItems[4].Text = string.IsNullOrWhiteSpace(song.Album) ? string.Empty : song.Album;
		item.SubItems[5].Text = song.FormattedDuration;
		SetListViewItemPrimaryText(item, text);
		if (song.IsAvailable == false)
		{
			item.ForeColor = SystemColors.GrayText;
			string formattedDuration = song.FormattedDuration;
			item.SubItems[5].Text = string.IsNullOrWhiteSpace(formattedDuration) ? "不可播放" : (formattedDuration + " (不可播放)");
			item.ToolTipText = "歌曲已下架或暂不可播放";
		}
		else
		{
			item.ForeColor = SystemColors.WindowText;
			item.ToolTipText = null;
		}
	}

	private void RefreshAvailabilityIndicatorsInCurrentView(string? songId = null, SongInfo? sourceSong = null)
	{
		SafeInvoke(delegate
		{
			if (base.IsDisposed || resultListView == null || resultListView.IsDisposed)
			{
				return;
			}
			string targetSongId = !string.IsNullOrWhiteSpace(songId) ? songId : sourceSong?.Id;
			if (sourceSong != null && !string.IsNullOrWhiteSpace(targetSongId) && _currentSongs != null)
			{
				for (int i = 0; i < _currentSongs.Count; i++)
				{
					SongInfo rowSong = _currentSongs[i];
					if (rowSong == null || ReferenceEquals(rowSong, sourceSong) || !string.Equals(rowSong.Id, targetSongId, StringComparison.Ordinal))
					{
						continue;
					}
					rowSong.IsAvailable = sourceSong.IsAvailable;
					rowSong.IsTrial = sourceSong.IsTrial;
					rowSong.TrialStart = sourceSong.TrialStart;
					rowSong.TrialEnd = sourceSong.TrialEnd;
					rowSong.IsUnblocked = sourceSong.IsUnblocked;
					rowSong.UnblockSource = sourceSong.UnblockSource;
					if (!string.IsNullOrWhiteSpace(sourceSong.Url))
					{
						rowSong.Url = sourceSong.Url;
					}
					if (!string.IsNullOrWhiteSpace(sourceSong.Level))
					{
						rowSong.Level = sourceSong.Level;
					}
					if (sourceSong.Size > 0)
					{
						rowSong.Size = sourceSong.Size;
					}
				}
			}
			MarkListViewLayoutDataChanged();
			if (_isVirtualSongListActive && resultListView.VirtualMode)
			{
				ResetVirtualItemCache();
				resultListView.Invalidate();
				return;
			}
			if (_currentSongs == null || _currentSongs.Count == 0 || resultListView.Items.Count == 0)
			{
				resultListView.Invalidate();
				return;
			}
			int baseIndex = Math.Max(1, _currentSequenceStartIndex);
			resultListView.BeginUpdate();
			try
			{
				for (int i = 0; i < resultListView.Items.Count; i++)
				{
					ListViewItem listViewItem = resultListView.Items[i];
					if (!(listViewItem?.Tag is int dataIndex) || dataIndex < 0 || dataIndex >= _currentSongs.Count)
					{
						continue;
					}
					SongInfo rowSong = _currentSongs[dataIndex];
					if (!string.IsNullOrWhiteSpace(targetSongId) && !string.Equals(rowSong?.Id, targetSongId, StringComparison.Ordinal))
					{
						continue;
					}
					FillListViewItemFromSongInfo(listViewItem, rowSong, checked(baseIndex + dataIndex));
				}
			}
			finally
			{
				EndListViewUpdateAndRefreshAccessibility();
			}
		});
	}

	private static List<T> CloneList<T>(IEnumerable<T> source)
	{
		if (source == null)
		{
			return new List<T>();
		}
		return (source is List<T> collection) ? new List<T>(collection) : new List<T>(source);
	}

	private void UpdateSequenceStartIndex(int startIndex)
	{
		_currentSequenceStartIndex = Math.Max(1, startIndex);
	}

	private string FormatIndex(int index)
	{
		return (_hideSequenceNumbers || IsAlwaysSequenceHiddenView()) ? string.Empty : index.ToString();
	}

	private bool IsDefaultSequenceHiddenView()
	{
		return _currentListItems != null && _currentListItems.Count > 0 && _currentSongs.Count == 0 && _currentPlaylists.Count == 0 && _currentAlbums.Count == 0 && _currentPodcasts.Count == 0 && _currentPodcastSounds.Count == 0 && _currentArtists.Count == 0;
	}

	private bool IsAlwaysSequenceHiddenView()
	{
		if (IsDefaultSequenceHiddenView())
		{
			return true;
		}
		if (_currentArtists != null && _currentArtists.Count > 0 && _currentSongs.Count == 0 && _currentPlaylists.Count == 0 && _currentAlbums.Count == 0 && _currentPodcasts.Count == 0 && _currentPodcastSounds.Count == 0 && _currentListItems.Count == 0)
		{
			string viewSource = _currentViewSource ?? string.Empty;
			if (viewSource.StartsWith("artist_category_list:", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			return true;
		}
		return false;
	}

	private void RefreshSequenceDisplayInPlace()
	{
		if (resultListView == null)
		{
			return;
		}
		if (_isVirtualSongListActive && resultListView.VirtualMode)
		{
			ResetVirtualItemCache();
			ApplyResultListViewLayout();
			resultListView.Invalidate();
			return;
		}
		if (resultListView.Items.Count == 0)
		{
			return;
		}
		if (resultListView.Items.Count == 1)
		{
			ListViewItem onlyItem = resultListView.Items[0];
			if (IsListViewLoadingPlaceholderItem(onlyItem) || IsListViewRetryPlaceholderItem(onlyItem))
			{
				resultListView.BeginUpdate();
				try
				{
					EnsureSubItemCount(onlyItem, 6);
					onlyItem.SubItems[1].Text = string.Empty;
				}
				finally
				{
					EndListViewUpdateAndRefreshAccessibility();
				}
				return;
			}
		}
		resultListView.BeginUpdate();
		checked
		{
			try
			{
				bool hideSequence = _hideSequenceNumbers || IsAlwaysSequenceHiddenView();
				int baseIndex = Math.Max(1, _currentSequenceStartIndex);
				for (int i = 0; i < resultListView.Items.Count; i++)
				{
					ListViewItem listViewItem = resultListView.Items[i];
					if (listViewItem != null)
					{
						EnsureSubItemCount(listViewItem, 6);
						string a = ((listViewItem.SubItems.Count > 1) ? listViewItem.SubItems[1].Text : listViewItem.Text);
						bool flag = (listViewItem.Tag is int num && num == -2) || string.Equals(a, "上一页", StringComparison.Ordinal);
						bool flag2 = (listViewItem.Tag is int num2 && num2 == -3) || string.Equals(a, "下一页", StringComparison.Ordinal);
						bool flag3 = (listViewItem.Tag is int num3 && num3 == -4) || string.Equals(a, "跳转", StringComparison.Ordinal);
						if (flag)
						{
							listViewItem.SubItems[1].Text = "上一页";
						}
						else if (flag2)
						{
							listViewItem.SubItems[1].Text = "下一页";
						}
						else if (flag3)
						{
							listViewItem.SubItems[1].Text = "跳转";
						}
						else if (hideSequence)
						{
							listViewItem.SubItems[1].Text = string.Empty;
						}
						else
						{
							int dataIndex = i;
							if (listViewItem.Tag is int tagIndex && tagIndex >= 0)
							{
								dataIndex = tagIndex;
							}
							listViewItem.SubItems[1].Text = FormatIndex(checked(baseIndex + dataIndex));
						}
					}
				}
			}
			finally
			{
				EndListViewUpdateAndRefreshAccessibility();
			}
		}
	}

	private void PatchSongs(List<SongInfo> songs, int startIndex, bool skipAvailabilityCheck = false, bool showPagination = false, bool hasPreviousPage = false, bool hasNextPage = false, int pendingFocusIndex = -1, bool allowSelection = true)
	{
		MarkListViewLayoutDataChanged();
		UpdateSequenceStartIndex(startIndex);
		int num = ((resultListView.SelectedIndices.Count > 0) ? resultListView.SelectedIndices[0] : pendingFocusIndex);
		List<SongInfo> list = (_currentSongs = CloneList(songs));
		ApplySongLikeStates(list);
		_currentPlaylists.Clear();
		_currentAlbums.Clear();
		_currentArtists.Clear();
		_currentListItems.Clear();
		_currentPodcasts.Clear();
		_currentPodcastSounds.Clear();
		_currentPodcast = null;
		if (list.Count == 0)
		{
			string viewSource = _currentViewSource ?? string.Empty;
			string fallbackName = (!string.IsNullOrEmpty(viewSource) && viewSource.StartsWith("search:", StringComparison.OrdinalIgnoreCase)) ? "搜索结果" : "歌曲列表";
			ShowListRetryPlaceholderCore(viewSource, resultListView?.AccessibleName, fallbackName, announceHeader: true, suppressFocus: IsSearchViewSource(viewSource));
			return;
		}
		bool flag = _isVirtualSongListActive || ShouldUseVirtualSongList(list);
		int focusedListViewIndex = GetFocusedListViewIndex();
		bool shouldAnnounceFocusedRefresh = false;
		resultListView.BeginUpdate();
		bool flag2 = false;
		int count = list.Count;
		int count2 = resultListView.Items.Count;
		int num2 = Math.Min(count, count2);
		checked
		{
			for (int i = 0; i < num2; i++)
			{
				SongInfo song = list[i];
				ListViewItem listViewItem = resultListView.Items[i];
				string beforeSpeech = null;
				bool trackFocusedChange = !allowSelection && i == focusedListViewIndex;
				if (trackFocusedChange)
				{
					EnsureSubItemCount(listViewItem, 6);
					beforeSpeech = BuildListViewItemSpeech(listViewItem);
				}
				FillListViewItemFromSongInfo(listViewItem, song, startIndex + i);
				listViewItem.Tag = i;
				if (trackFocusedChange)
				{
					EnsureSubItemCount(listViewItem, 6);
					string afterSpeech = BuildListViewItemSpeech(listViewItem);
					if (!string.Equals(beforeSpeech, afterSpeech, StringComparison.Ordinal))
					{
						shouldAnnounceFocusedRefresh = true;
					}
				}
			}
			for (int j = count2; j < count; j++)
			{
				SongInfo song2 = list[j];
				ListViewItem listViewItem2 = new ListViewItem(new string[6]);
				FillListViewItemFromSongInfo(listViewItem2, song2, startIndex + j);
				listViewItem2.Tag = j;
				resultListView.Items.Add(listViewItem2);
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
			if (allowSelection && resultListView.Items.Count > 0)
			{
				int num4 = -1;
				bool flag3 = false;
				if (CanApplyPendingSongFocusForView(_currentViewSource))
				{
					num4 = _currentSongs.FindIndex((SongInfo s) => s != null && string.Equals(s.Id, _pendingSongFocusId, StringComparison.OrdinalIgnoreCase));
					if (num4 < 0)
					{
						num4 = _currentSongs.FindIndex((SongInfo s) => s != null && s.IsCloudSong && !string.IsNullOrEmpty(s.CloudSongId) && string.Equals(s.CloudSongId, _pendingSongFocusId, StringComparison.OrdinalIgnoreCase));
					}
					flag3 = num4 >= 0;
				}
				if (num4 < 0)
				{
					num4 = ((num >= 0) ? Math.Min(num, resultListView.Items.Count - 1) : 0);
				}
				if (flag3)
				{
					_pendingListFocusIndex = -1;
					_pendingListFocusViewSource = null;
				}
				num4 = ResolvePendingListFocusIndex(num4);
				EnsureListSelectionWithoutFocus(num4);
				if (flag3)
				{
					_pendingSongFocusId = null;
					_pendingSongFocusViewSource = null;
					_pendingSongFocusSatisfied = true;
					_pendingSongFocusSatisfiedViewSource = _currentViewSource;
					if (string.Equals(_currentViewSource, "user_cloud", StringComparison.OrdinalIgnoreCase) && num4 >= 0 && num4 < _currentSongs.Count)
					{
						SongInfo songInfo = _currentSongs[num4];
						if (songInfo != null && songInfo.IsCloudSong && !string.IsNullOrEmpty(songInfo.CloudSongId))
						{
							_lastSelectedCloudSongId = songInfo.CloudSongId;
							_pendingCloudFocusId = null;
							_skipCloudRestoreOnce = true;
						}
					}
					if (resultListView != null && resultListView.CanFocus && !IsListAutoFocusSuppressed)
					{
						resultListView.Focus();
					}
				}
			}
			TryAnnounceLoadingPlaceholderReplacement();
			if (!allowSelection && shouldAnnounceFocusedRefresh)
			{
				QueueFocusedListViewItemRefreshAnnouncement(focusedListViewIndex);
			}
			if (!skipAvailabilityCheck)
			{
				ScheduleAvailabilityCheck(list);
			}
		}
	}

	private void SetViewContext(string? viewSource, string? accessibleName)
	{
		if (!string.IsNullOrWhiteSpace(viewSource))
		{
                if (!string.Equals(_currentViewSource, viewSource, StringComparison.OrdinalIgnoreCase))
                {
                        string? previousViewSource = _currentViewSource;
                        CapturePersonalFmSnapshotOnViewLeave(viewSource);
                        CapturePlaybackFocusBeforeViewChanged(previousViewSource, viewSource);
                        _pendingSongFocusSatisfied = false;
                        _pendingSongFocusSatisfiedViewSource = null;
                        _lastAnnouncedViewSource = null;
                        _lastAnnouncedHeader = null;
                        CancelPendingPlaceholderPlayback("view changed");
                        ResetListViewTypeSearchBuffer();
#if DEBUG
                        if (resultListView is SafeListView safeListView)
                        {
                                safeListView.ResetAlignmentLog("ViewSourceChanged", viewSource);
                        }
#endif
                }
			_currentViewSource = viewSource;
			_isHomePage = string.Equals(viewSource, "homepage", StringComparison.OrdinalIgnoreCase);
		}
		else if (string.IsNullOrEmpty(_currentViewSource))
		{
			_isHomePage = false;
		}
		if (string.IsNullOrWhiteSpace(_currentViewSource) || !_currentViewSource.StartsWith("url:mixed", StringComparison.OrdinalIgnoreCase))
		{
			_currentMixedQueryKey = null;
		}
                if (!string.IsNullOrWhiteSpace(accessibleName))
                {
                        if (!string.Equals(resultListView.AccessibleName, accessibleName, StringComparison.Ordinal))
                        {
                                resultListView.AccessibleName = accessibleName;
                        }
                }
                else if (string.IsNullOrWhiteSpace(resultListView.AccessibleName))
                {
                        resultListView.AccessibleName = "列表内容";
                }
        }

        private void AnnounceListViewHeaderIfNeeded(string? accessibleName)     
        {
                if (resultListView != null && resultListView.ContainsFocus)     
                {
			string text = ((!string.IsNullOrWhiteSpace(accessibleName)) ? accessibleName : ((!string.IsNullOrWhiteSpace(resultListView.AccessibleName)) ? resultListView.AccessibleName : string.Empty));
			if (!string.IsNullOrWhiteSpace(text) && (string.IsNullOrWhiteSpace(_currentViewSource) || !string.Equals(_lastAnnouncedViewSource, _currentViewSource, StringComparison.OrdinalIgnoreCase) || !string.Equals(_lastAnnouncedHeader, text, StringComparison.Ordinal)))
			{
				PrepareListViewHeaderPrefix(text, allowNvda: true);
                                AnnounceListViewHeaderNotification(text, allowNvda: true);
				_lastAnnouncedViewSource = _currentViewSource;
				_lastAnnouncedHeader = text;
                        }
                }
        }

        private static bool IsSearchViewSource(string? viewSource)
        {
                return !string.IsNullOrWhiteSpace(viewSource) && viewSource.StartsWith("search:", StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolveAccessibleName(string? accessibleName, string fallbackName)
        {
                return string.IsNullOrWhiteSpace(accessibleName) ? fallbackName : accessibleName;
        }

        private void ApplyListViewContext(string? viewSource, string? accessibleName, string fallbackName, bool announceHeader)
        {
                string resolvedName = ResolveAccessibleName(accessibleName, fallbackName);
                SetViewContext(viewSource, resolvedName);
                if (announceHeader)
                {
                        AnnounceListViewHeaderIfNeeded(resolvedName);
                }
        }

        private void ApplyListViewContext(string? viewSource, string resolvedName, bool announceHeader)
        {
                SetViewContext(viewSource, resolvedName);
                if (announceHeader)
                {
                        AnnounceListViewHeaderIfNeeded(resolvedName);
                }
        }

        private void ApplyStandardListViewSelection(int fallbackIndex, bool allowSelection, bool suppressFocus)
        {
                if (!allowSelection || suppressFocus || IsListAutoFocusSuppressed || resultListView == null || resultListView.Items.Count == 0)
                {
                        return;
                }
                fallbackIndex = ResolvePendingListFocusIndex(fallbackIndex);
                EnsureListSelectionWithoutFocus(fallbackIndex);
        }

	private void ScheduleAvailabilityCheck(List<SongInfo> songs)
	{
		_availabilityCheckCts?.Cancel();
		_availabilityCheckCts?.Dispose();
		_availabilityCheckCts = null;
		if (songs == null || songs.Count == 0)
		{
			return;
		}
		BatchCheckSongsAvailabilityAsync(songs, (_availabilityCheckCts = new CancellationTokenSource()).Token).ContinueWith(delegate(Task task)
		{
			if (task.IsFaulted && task.Exception != null)
			{
				foreach (Exception innerException in task.Exception.Flatten().InnerExceptions)
				{
					Debug.WriteLine("[StreamCheck] 可用性检查任务异常: " + innerException.Message);
				}
			}
		}, TaskScheduler.Default);
	}


}
}
