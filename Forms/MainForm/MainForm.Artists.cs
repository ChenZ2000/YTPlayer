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
using YTPlayer.Forms;
using YTPlayer.Models;
using YTPlayer.Utils;
#pragma warning disable CS0219, CS8600, CS8601, CS8602, CS8603, CS8604, CS8618, CS8622, CS8625, CS8632, CS4014

namespace YTPlayer
{
public partial class MainForm
{
	private void ConfigureListViewForArtists()
	{
		DisableVirtualSongList();
		columnHeader0.Text = string.Empty;
		columnHeader1.Text = string.Empty;
		columnHeader2.Text = string.Empty;
		columnHeader3.Text = string.Empty;
		columnHeader4.Text = string.Empty;
		columnHeader5.Text = string.Empty;
	}

	private static string BuildArtistStatsLabel(int musicCount, int albumCount)
	{
		bool flag = musicCount > 0;
		bool flag2 = albumCount > 0;
		if (flag && flag2)
		{
			return $"歌曲 {musicCount} / 专辑 {albumCount}";
		}
		if (flag)
		{
			return $"歌曲 {musicCount}";
		}
		if (flag2)
		{
			return $"专辑 {albumCount}";
		}
		return string.Empty;
	}

	private static string ResolveArtistIntroText(ArtistInfo? artist)
	{
		if (artist == null)
		{
			return string.Empty;
		}
		if (!string.IsNullOrWhiteSpace(artist.Description))
		{
			return artist.Description;
		}
		if (!string.IsNullOrWhiteSpace(artist.BriefDesc))
		{
			return artist.BriefDesc;
		}
		return string.Empty;
	}

	private void DisplayArtists(List<ArtistInfo> artists, bool showPagination = false, bool hasNextPage = false, int startIndex = 1, bool preserveSelection = false, string? viewSource = null, string? accessibleName = null, bool announceHeader = true, bool suppressFocus = false, bool allowSelection = true)
	{
		MarkListViewLayoutDataChanged();
		_listLoadingPlaceholderActive = false;
		CancellationToken currentViewContentToken = GetCurrentViewContentToken();
		if (ShouldAbortViewRender(currentViewContentToken, "DisplayArtists"))
		{
			return;
		}
		ResetPendingListFocusIfViewChanged(viewSource);
		UpdateSequenceStartIndex(startIndex);
		int num = -1;
		if (preserveSelection && resultListView.SelectedIndices.Count > 0)
		{
			num = resultListView.SelectedIndices[0];
		}
		List<ArtistInfo> list = (_currentArtists = CloneList(artists));
		_currentSongs.Clear();
		_currentPlaylists.Clear();
		_currentAlbums.Clear();
		_currentListItems.Clear();
		_currentPodcasts.Clear();
		_currentPodcastSounds.Clear();
		_currentPodcast = null;
		ApplyArtistSubscriptionStates(list);
		bool flag = list.Count > 0;
		if (!flag)
		{
			if (ShouldAbortViewRender(currentViewContentToken, "DisplayArtists"))
			{
				return;
			}
			ConfigureListViewForArtists();
			ShowListRetryPlaceholderCore(viewSource, accessibleName, "歌手列表", announceHeader, suppressFocus: IsSearchViewSource(viewSource));
			return;
		}
		resultListView.BeginUpdate();
		checked
		{
			try
			{
				ResetListViewSelectionState();
				resultListView.Items.Clear();
				if (ShouldAbortViewRender(currentViewContentToken, "DisplayArtists"))
				{
					return;
				}
				int num2 = startIndex;
				foreach (ArtistInfo item in list)
				{
					if (ShouldAbortViewRender(currentViewContentToken, "DisplayArtists"))
					{
						return;
					}
					if ((item.MusicCount <= 0 || item.AlbumCount <= 0) && TryGetCachedArtistStats(item.Id, out (int, int) stats))
					{
						if (item.MusicCount <= 0)
						{
							(item.MusicCount, _) = stats;
						}
						if (item.AlbumCount <= 0)
						{
							item.AlbumCount = stats.Item2;
						}
					}
						string text = BuildArtistStatsLabel(item.MusicCount, item.AlbumCount);
						string text2 = ResolveArtistIntroText(item);
						ListViewItem value = new ListViewItem(new string[6]
						{
							string.Empty,
							FormatIndex(num2),
							item.Name ?? "未知",
							text,
							text2 ?? string.Empty,
							string.Empty
						})
					{
						Tag = item
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
			}
			finally
			{
				EndListViewUpdateAndRefreshAccessibility();
			}
			if (ShouldAbortViewRender(currentViewContentToken, "DisplayArtists"))
			{
				return;
			}
			ConfigureListViewForArtists();
			ScheduleArtistStatsRefresh(_currentArtists);
			ApplyListViewContext(viewSource, accessibleName, "歌手列表", announceHeader);
			int fallbackIndex = ((num >= 0) ? Math.Min(num, resultListView.Items.Count - 1) : 0);
			ApplyStandardListViewSelection(fallbackIndex, allowSelection, suppressFocus);
		}
	}

	private async Task<bool> EnsureSongAvailabilityAsync(SongInfo song, QualityLevel quality, CancellationToken cancellationToken)
	{
		if (song == null || string.IsNullOrWhiteSpace(song.Id))
		{
			return false;
		}
		if (song.IsAvailable == true)
		{
			return true;
		}
		if (song.IsAvailable == false && song.IsUnblocked)
		{
			return true;
		}
		try
		{
			bool isAvailable = false;
			if ((await _apiClient.BatchCheckSongsAvailabilityAsync(new string[1] { song.Id }, quality).ConfigureAwait(continueOnCapturedContext: false))?.TryGetValue(song.Id, out isAvailable) ?? false)
			{
				if (isAvailable)
				{
					song.IsAvailable = true;
					return true;
				}
				if (await TryRecoverSongAvailabilityByUnblockAsync(song, quality, cancellationToken).ConfigureAwait(continueOnCapturedContext: false))
				{
					return true;
				}
				song.IsAvailable = false;
				return false;
			}
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			Debug.WriteLine("[StreamCheck] Single-song availability check failed (continue as available): " + ex.Message);
		}
		return true;
	}

	private void PatchArtists(List<ArtistInfo> artists, int startIndex = 1, bool showPagination = false, bool hasPreviousPage = false, bool hasNextPage = false, int pendingFocusIndex = -1, bool allowSelection = true)
	{
		MarkListViewLayoutDataChanged();
		CancellationToken currentViewContentToken = GetCurrentViewContentToken();
		if (ShouldAbortViewRender(currentViewContentToken, "PatchArtists"))
		{
			return;
		}
		ResetPendingListFocusIfViewChanged(_currentViewSource);
		UpdateSequenceStartIndex(startIndex);
		int num = ((resultListView.SelectedIndices.Count > 0) ? resultListView.SelectedIndices[0] : pendingFocusIndex);
		List<ArtistInfo> list = (_currentArtists = CloneList(artists));
		_currentSongs.Clear();
		_currentPlaylists.Clear();
		_currentAlbums.Clear();
		_currentListItems.Clear();
		_currentPodcasts.Clear();
		_currentPodcastSounds.Clear();
		_currentPodcast = null;
		ApplyArtistSubscriptionStates(list);
		bool flag = list.Count > 0;
		if (!flag)
		{
			if (ShouldAbortViewRender(currentViewContentToken, "PatchArtists"))
			{
				return;
			}
			ConfigureListViewForArtists();
			ShowListRetryPlaceholderCore(_currentViewSource, resultListView?.AccessibleName, "歌手列表", announceHeader: true, suppressFocus: IsSearchViewSource(_currentViewSource));
			return;
		}
		resultListView.BeginUpdate();
		checked
		{
			try
			{
				int count = list.Count;
				int count2 = resultListView.Items.Count;
				int num2 = Math.Min(count, count2);
				for (int i = 0; i < num2; i++)
				{
					ArtistInfo artistInfo = list[i];
					ListViewItem listViewItem = resultListView.Items[i];
					EnsureSubItemCount(listViewItem, 6);
					if ((artistInfo.MusicCount <= 0 || artistInfo.AlbumCount <= 0) && TryGetCachedArtistStats(artistInfo.Id, out (int, int) stats))
					{
						if (artistInfo.MusicCount <= 0)
						{
							(artistInfo.MusicCount, _) = stats;
						}
						if (artistInfo.AlbumCount <= 0)
						{
							artistInfo.AlbumCount = stats.Item2;
						}
					}
					string text = BuildArtistStatsLabel(artistInfo.MusicCount, artistInfo.AlbumCount);
					string text2 = ResolveArtistIntroText(artistInfo);
					listViewItem.SubItems[1].Text = FormatIndex(startIndex + i);
					listViewItem.SubItems[2].Text = artistInfo.Name ?? "未知";
					listViewItem.SubItems[3].Text = text;
					listViewItem.SubItems[4].Text = text2 ?? string.Empty;
					listViewItem.SubItems[5].Text = string.Empty;
					listViewItem.Tag = artistInfo;
					SetListViewItemPrimaryText(listViewItem, listViewItem.SubItems[2].Text);
				}
				for (int j = count2; j < count; j++)
				{
					ArtistInfo artistInfo2 = _currentArtists[j];
					if ((artistInfo2.MusicCount <= 0 || artistInfo2.AlbumCount <= 0) && TryGetCachedArtistStats(artistInfo2.Id, out (int, int) stats2))
					{
						if (artistInfo2.MusicCount <= 0)
						{
							(artistInfo2.MusicCount, _) = stats2;
						}
						if (artistInfo2.AlbumCount <= 0)
						{
							artistInfo2.AlbumCount = stats2.Item2;
						}
					}
					string text3 = BuildArtistStatsLabel(artistInfo2.MusicCount, artistInfo2.AlbumCount);
					string text4 = ResolveArtistIntroText(artistInfo2);
					ListViewItem value = new ListViewItem(new string[6]
					{
						string.Empty,
						FormatIndex(startIndex + j),
						artistInfo2.Name ?? "未知",
						text3,
						text4 ?? string.Empty,
						string.Empty
					})
					{
						Tag = artistInfo2
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
			}
			finally
			{
				EndListViewUpdateAndRefreshAccessibility();
			}
		}
		if (!ShouldAbortViewRender(currentViewContentToken, "PatchArtists") && flag)
		{
			ScheduleArtistStatsRefresh(_currentArtists);
		}
	if (!ShouldAbortViewRender(currentViewContentToken, "PatchArtists") && allowSelection && resultListView.Items.Count > 0)
	{
		int fallbackIndex = ((num >= 0) ? Math.Min(num, checked(resultListView.Items.Count - 1)) : 0);
		fallbackIndex = ResolvePendingListFocusIndex(fallbackIndex);
		EnsureListSelectionWithoutFocus(fallbackIndex);
	}
	TryAnnounceLoadingPlaceholderReplacement();
}

	private async Task OpenArtistAsync(ArtistInfo artist, bool skipSave = false)
	{
		if (artist == null)
		{
			return;
		}
		try
		{
			if (!skipSave)
			{
				SaveNavigationState();
			}
			_artistSongSortState.SetOption(ArtistSongSortOption.Hot);
			_artistAlbumSortState.SetOption(ArtistAlbumSortOption.Latest);
			string displayName = (string.IsNullOrWhiteSpace(artist.Name) ? $"歌手 {artist.Id}" : artist.Name);
			string viewSource = $"artist_entries:{artist.Id}";
			ViewLoadRequest request = new ViewLoadRequest(viewSource, displayName, "正在加载歌手：" + displayName + "...", !skipSave);
			ViewLoadResult<ArtistEntryViewData?> loadResult = await RunViewLoadAsync(request, async delegate(CancellationToken token)
			{
				_currentArtist = artist;
				_currentArtistSongsOffset = 0;
				_currentArtistAlbumsOffset = 0;
				_currentArtistSongsHasMore = false;
				_currentArtistAlbumsHasMore = false;
				_currentArtistAlbumsTotalCount = 0;
				ClearArtistAlbumsAscendingCache(artist.Id);
				ArtistDetail currentArtistDetail = await _apiClient.GetArtistDetailAsync(artist.Id);
				_currentArtistDetail = currentArtistDetail;
				token.ThrowIfCancellationRequested();
				if (_currentArtistDetail != null)
				{
					ApplyArtistDetailToArtist(artist, _currentArtistDetail);
				}
				List<ListItemInfo> items = BuildArtistEntryItems(artist, _currentArtistDetail);
				string statusText = "已打开歌手：" + displayName;
				return new ArtistEntryViewData(artist, _currentArtistDetail, items, statusText);
			}, "加载歌手已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				ArtistEntryViewData data = loadResult.Value;
				if (data == null)
				{
					UpdateStatusBar("加载歌手失败");
					return;
				}
				_currentArtist = data.Artist;
				_currentArtistDetail = data.Detail;
				_artistSongSortState.SetOption(ArtistSongSortOption.Hot);
				_artistAlbumSortState.SetOption(ArtistAlbumSortOption.Latest);
				DisplayListItems(data.Items, viewSource, displayName, preserveSelection: false, announceHeader: true, suppressFocus: true);
				FocusListAfterEnrich(0);
				UpdateStatusBar(data.StatusText);
			}
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"[Artist] 打开歌手失败: {ex}");
			MessageBox.Show("加载歌手信息失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("加载歌手失败");
		}
	}

	private List<ListItemInfo> BuildArtistEntryItems(ArtistInfo artist, ArtistDetail? detail)
	{
		List<ListItemInfo> list = new List<ListItemInfo>();
		ArtistInfo artistInfo = detail ?? artist;
		list.Add(new ListItemInfo
		{
			Type = ListItemType.Artist,
			Artist = new ArtistInfo
			{
				Id = artist.Id,
				Name = artistInfo.Name,
				Alias = artistInfo.Alias,
				PicUrl = artistInfo.PicUrl,
				AreaCode = artistInfo.AreaCode,
				AreaName = artistInfo.AreaName,
				TypeCode = artistInfo.TypeCode,
				TypeName = artistInfo.TypeName,
				MusicCount = artistInfo.MusicCount,
				AlbumCount = artistInfo.AlbumCount,
				MvCount = artistInfo.MvCount,
				BriefDesc = artistInfo.BriefDesc,
				Description = artistInfo.Description,
				IsSubscribed = artistInfo.IsSubscribed
			}
		});
		list.Add(new ListItemInfo
		{
			Type = ListItemType.Category,
			CategoryId = $"artist_top_{artist.Id}",
			CategoryName = "热门",
			CategoryDescription = "网易云热门 50 首",
			ItemCount = Math.Min(50, (artistInfo.MusicCount > 0) ? artistInfo.MusicCount : 50),
			ItemUnit = "首"
		});
		list.Add(new ListItemInfo
		{
			Type = ListItemType.Category,
			CategoryId = $"artist_songs_{artist.Id}",
			CategoryName = "全部单曲",
			CategoryDescription = "按热度/发布时间排序",
			ItemCount = artistInfo.MusicCount,
			ItemUnit = "首"
		});
		list.Add(new ListItemInfo
		{
			Type = ListItemType.Category,
			CategoryId = $"artist_albums_{artist.Id}",
			CategoryName = "全部专辑",
			CategoryDescription = "按发布时间排序",
			ItemCount = artistInfo.AlbumCount,
			ItemUnit = "张"
		});
		return list;
	}

	private async Task<string> ResolveArtistDisplayNameAsync(long artistId)
	{
		if (artistId <= 0)
		{
			return "歌手";
		}
		if (_currentArtist != null && _currentArtist.Id == artistId && !string.IsNullOrWhiteSpace(_currentArtist.Name))
		{
			return _currentArtist.Name;
		}
		if (_currentArtistDetail != null && _currentArtistDetail.Id == artistId && !string.IsNullOrWhiteSpace(_currentArtistDetail.Name))
		{
			return _currentArtistDetail.Name;
		}
		try
		{
			ArtistDetail detail = await _apiClient.GetArtistDetailAsync(artistId, includeIntroduction: false);
			if (detail != null)
			{
				_currentArtistDetail = detail;
				if (_currentArtist == null || _currentArtist.Id == artistId)
				{
					_currentArtist = new ArtistInfo
					{
						Id = artistId,
						Name = detail.Name,
						PicUrl = detail.PicUrl
					};
				}
				return string.IsNullOrWhiteSpace(detail.Name) ? $"歌手 {artistId}" : detail.Name;
			}
		}
		catch (Exception ex)
		{
			Exception arg = ex;
			Debug.WriteLine($"[Artist] 获取歌手信息失败: {arg}");
		}
		return $"歌手 {artistId}";
	}

	private async Task LoadArtistTopSongsAsync(long artistId, bool skipSave = false)
	{
		try
		{
			string artistName = await ResolveArtistDisplayNameAsync(artistId);
			if (!skipSave)
			{
				SaveNavigationState();
			}
			ViewLoadRequest request = new ViewLoadRequest($"artist_songs_top:{artistId}", artistName + " 热门 50 首", "正在加载 " + artistName + " 的热门歌曲...", !skipSave);
			ViewLoadResult<ArtistSongsViewData?> loadResult = await RunViewLoadAsync(request, async delegate(CancellationToken token)
			{
				List<SongInfo> songs = await _apiClient.GetArtistTopSongsAsync(artistId).ConfigureAwait(continueOnCapturedContext: false);
				token.ThrowIfCancellationRequested();
				List<SongInfo> normalized = songs ?? new List<SongInfo>();
				string statusText = ((normalized.Count == 0) ? (artistName + " 暂无热门歌曲") : $"已加载 {artistName} 热门 {normalized.Count} 首");
				return new ArtistSongsViewData(normalized, hasMore: false, 0, normalized.Count, statusText);
			}, "加载歌手热门歌曲已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				ArtistSongsViewData data = loadResult.Value ?? new ArtistSongsViewData(new List<SongInfo>(), hasMore: false, 0, 0, artistName + " 暂无热门歌曲");
				_currentArtistSongsOffset = 0;
				_currentArtistSongsHasMore = false;
				DisplaySongs(data.Songs, showPagination: false, hasNextPage: false, 1, preserveSelection: false, request.ViewSource, request.AccessibleName, skipAvailabilityCheck: true, announceHeader: true, suppressFocus: true);
				FocusListAfterEnrich(0);
				UpdateStatusBar(data.StatusText);
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Exception ex3 = ex2;
			Debug.WriteLine($"[Artist] 加载热门歌曲失败: {ex3}");
			MessageBox.Show("加载热门歌曲失败：" + ex3.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("加载热门歌曲失败");
		}
	}

	private async Task LoadArtistSongsByApiAsync(long artistId, int offset, bool skipSave, string orderToken, string artistName)
	{
		string paginationKey = BuildArtistSongsPaginationKey(artistId, orderToken);
		int totalCount = await EnsureArtistSongsTotalCountAsync(artistId, orderToken, GetCurrentViewContentToken());
		bool offsetClamped;
		int normalizedOffset = NormalizeOffsetWithTotal(totalCount, ArtistSongsPageSize, offset, out offsetClamped);
		if (offsetClamped)
		{
			int page = (normalizedOffset / ArtistSongsPageSize) + 1;
			UpdateStatusBar($"页码过大，已跳到第 {page} 页");
		}
		offset = normalizedOffset;
		string viewSource = $"artist_songs:{artistId}:order{orderToken}:offset{offset}";
		string accessibleName = artistName + " 的歌曲";
		ViewLoadRequest request = new ViewLoadRequest(viewSource, accessibleName, "正在加载 " + artistName + " 的歌曲...", !skipSave);
		ViewLoadResult<ArtistSongsViewData?> loadResult = await RunViewLoadAsync(request, async delegate(CancellationToken token)
		{
			(List<SongInfo> Songs, bool HasMore, int TotalCount) result = await _apiClient.GetArtistSongsAsync(artistId, ArtistSongsPageSize, offset, orderToken).ConfigureAwait(continueOnCapturedContext: false);
			List<SongInfo> songs = result.Songs ?? new List<SongInfo>();
			token.ThrowIfCancellationRequested();
			int resolvedTotal = result.TotalCount;
			if (resolvedTotal <= 0)
			{
				resolvedTotal = totalCount > 0 ? totalCount : (offset + songs.Count + (result.HasMore ? 1 : 0));
			}
			if (resolvedTotal > 0)
			{
				SetArtistSongsTotalCount(paginationKey, resolvedTotal);
			}
			bool hasMore = resolvedTotal > 0 ? (offset + songs.Count < resolvedTotal) : result.HasMore;
			string statusText;
			if (songs.Count == 0)
			{
				statusText = artistName + " 暂无歌曲";
			}
			else if (resolvedTotal > 0)
			{
				statusText = $"已加载 {artistName} 的歌曲 {offset + 1}-{offset + songs.Count} / {resolvedTotal}首";
			}
			else
			{
				statusText = $"已加载 {artistName} 的歌曲 {offset + 1}-{offset + songs.Count}";
			}
			return new ArtistSongsViewData(songs, hasMore, offset, resolvedTotal, statusText);
		}, "加载歌手歌曲已取消").ConfigureAwait(continueOnCapturedContext: true);
		if (!loadResult.IsCanceled)
		{
			ArtistSongsViewData data = loadResult.Value ?? new ArtistSongsViewData(new List<SongInfo>(), hasMore: false, offset, 0, artistName + " 暂无歌曲");
			if (data.Songs == null || data.Songs.Count == 0)
			{
				if (data.TotalCount > 0 && data.Offset >= data.TotalCount)
				{
					int maxPage = CalculateMaxPage(data.TotalCount, ArtistSongsPageSize, 1);
					UpdateStatusBar($"页码超出范围，最大可到第 {maxPage} 页");
					ShowListErrorRow(viewSource, accessibleName, $"加载失败：页码超出范围，最大可到第 {maxPage} 页。按回车重新跳转。");
					return;
				}
				if (data.TotalCount > data.Offset)
				{
					string hint = "接口返回空列表，可能存在接口限制";
					string advise = string.Equals(orderToken, "hot", StringComparison.OrdinalIgnoreCase) ? "建议切换到按发布时间排序" : "请稍后重试";
					UpdateStatusBar($"{hint}，{advise}");
					ShowListErrorRow(viewSource, accessibleName, $"加载失败：{hint}，{advise}。按回车重新跳转。");
					return;
				}
			}
			_currentArtistSongsOffset = data.Offset;
			_currentArtistSongsHasMore = data.HasMore;
			_currentArtistSongsTotalCount = Math.Max(data.TotalCount, data.Offset + (data.Songs?.Count ?? 0));
			DisplaySongs(data.Songs, showPagination: true, data.HasMore, data.Offset + 1, preserveSelection: false, viewSource, accessibleName, skipAvailabilityCheck: false, announceHeader: true, suppressFocus: true);
			UpdateArtistSongsSortMenuChecks();
			FocusListAfterEnrich(0);
			UpdateStatusBar(data.StatusText);
		}
	}

	private async Task LoadArtistSongsByAlbumIndexAsync(long artistId, int offset, bool skipSave, string orderToken, string artistName)
	{
		string paginationKey = BuildArtistSongsPaginationKey(artistId, orderToken);
		int totalCount = await EnsureArtistSongsTotalCountAsync(artistId, orderToken, GetCurrentViewContentToken());
		bool offsetClamped;
		int normalizedOffset = NormalizeOffsetWithTotal(totalCount, ArtistSongsPageSize, offset, out offsetClamped);
		if (offsetClamped)
		{
			int page = (normalizedOffset / ArtistSongsPageSize) + 1;
			UpdateStatusBar($"页码过大，已跳到第 {page} 页");
		}
		offset = normalizedOffset;
		string viewSource = $"artist_songs:{artistId}:order{orderToken}:offset{offset}";
		string accessibleName = artistName + " 的歌曲";
		ViewLoadRequest request = new ViewLoadRequest(viewSource, accessibleName, "正在加载 " + artistName + " 的歌曲...", !skipSave);
		ViewLoadResult<ArtistSongsViewData?> loadResult = await RunViewLoadAsync(request, async delegate(CancellationToken token)
		{
			int requiredCount = Math.Max(1, checked(offset + ArtistSongsPageSize));
			ArtistSongIndexCache cache = await EnsureArtistSongIndexAsync(paginationKey, artistId, newestFirst: true, requiredCount, token, artistName).ConfigureAwait(continueOnCapturedContext: false);
			List<SongInfo> songs = cache.Songs.Skip(offset).Take(ArtistSongsPageSize).ToList();
			int resolvedTotal = totalCount;
			if (cache.IsComplete && cache.Songs.Count > 0)
			{
				resolvedTotal = Math.Max(resolvedTotal, cache.Songs.Count);
			}
			if (resolvedTotal > 0)
			{
				SetArtistSongsTotalCount(paginationKey, resolvedTotal);
			}
			bool hasMore = resolvedTotal > 0 ? (offset + songs.Count < resolvedTotal) : !cache.IsComplete;
			string statusText;
			if (songs.Count == 0)
			{
				statusText = artistName + " 暂无歌曲";
			}
			else if (resolvedTotal > 0)
			{
				statusText = $"已加载 {artistName} 的歌曲 {offset + 1}-{offset + songs.Count} / {resolvedTotal}首";
			}
			else
			{
				statusText = $"已加载 {artistName} 的歌曲 {offset + 1}-{offset + songs.Count}";
			}
			return new ArtistSongsViewData(songs, hasMore, offset, resolvedTotal, statusText);
		}, "加载歌手歌曲已取消").ConfigureAwait(continueOnCapturedContext: true);
		if (!loadResult.IsCanceled)
		{
			ArtistSongsViewData data = loadResult.Value ?? new ArtistSongsViewData(new List<SongInfo>(), hasMore: false, offset, 0, artistName + " 暂无歌曲");
			if (data.Songs == null || data.Songs.Count == 0)
			{
				if (data.TotalCount > 0 && data.Offset >= data.TotalCount)
				{
					int maxPage = CalculateMaxPage(data.TotalCount, ArtistSongsPageSize, 1);
					UpdateStatusBar($"页码超出范围，最大可到第 {maxPage} 页");
					ShowListErrorRow(viewSource, accessibleName, $"加载失败：页码超出范围，最大可到第 {maxPage} 页。按回车重新跳转。");
					return;
				}
				if (data.TotalCount > data.Offset)
				{
					UpdateStatusBar("未能加载到该页，请稍后重试");
					ShowListErrorRow(viewSource, accessibleName, "加载失败：未能加载到该页，请稍后重试。按回车重新跳转。");
					return;
				}
			}
			_currentArtistSongsOffset = data.Offset;
			_currentArtistSongsHasMore = data.HasMore;
			_currentArtistSongsTotalCount = Math.Max(data.TotalCount, data.Offset + (data.Songs?.Count ?? 0));
			DisplaySongs(data.Songs, showPagination: true, data.HasMore, data.Offset + 1, preserveSelection: false, viewSource, accessibleName, skipAvailabilityCheck: false, announceHeader: true, suppressFocus: true);
			UpdateArtistSongsSortMenuChecks();
			FocusListAfterEnrich(0);
			UpdateStatusBar(data.StatusText);
		}
	}

	private async Task LoadArtistSongsAsync(long artistId, int offset = 0, bool skipSave = false, ArtistSongSortOption? orderOverride = null)
	{
		checked
		{
			try
			{
				string artistName = await ResolveArtistDisplayNameAsync(artistId);
				if (!skipSave)
				{
					SaveNavigationState();
				}
				if (orderOverride.HasValue)
				{
					_artistSongSortState.SetOption(orderOverride.Value);
				}
				string orderToken = MapArtistSongsOrder(_artistSongSortState.CurrentOption);
				if (string.Equals(orderToken, "time", StringComparison.OrdinalIgnoreCase))
				{
					await LoadArtistSongsByAlbumIndexAsync(artistId, offset, skipSave, orderToken, artistName);
				}
				else
				{
					await LoadArtistSongsByApiAsync(artistId, offset, skipSave, orderToken, artistName);
				}
			}
			catch (Exception ex)
			{
				Exception ex2 = ex;
				Exception ex3 = ex2;
				string orderToken = MapArtistSongsOrder(_artistSongSortState.CurrentOption);
				string fallbackName = _currentArtist?.Name ?? $"歌手 {artistId}";
				string accessibleName = fallbackName + " 的歌曲";
				string viewSource = $"artist_songs:{artistId}:order{orderToken}:offset{offset}";
				if (IsPaginationOffsetError(ex3))
				{
					int maxPage = (_currentArtistSongsTotalCount > 0) ? CalculateMaxPage(_currentArtistSongsTotalCount, ArtistSongsPageSize, 1) : 0;
					string hint = maxPage > 0 ? $"页码过大，最大可到第 {maxPage} 页" : "页码过大";
					UpdateStatusBar($"{hint}，请重新跳转");
					ShowListErrorRow(viewSource, accessibleName, $"加载失败：{hint}，请重新跳转。按回车重新跳转。");
					return;
				}
				Debug.WriteLine($"[Artist] 加载歌曲失败: {ex3}");
				MessageBox.Show("加载歌手的歌曲失败：" + ex3.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("加载歌手的歌曲失败");
			}
		}
	}

	private async Task LoadArtistAlbumsAsync(long artistId, int offset = 0, bool skipSave = false, ArtistAlbumSortOption? sortOverride = null)
	{
		checked
		{
			try
			{
				string artistName = await ResolveArtistDisplayNameAsync(artistId);
				if (!skipSave)
				{
					SaveNavigationState();
				}
				if (sortOverride.HasValue)
				{
					_artistAlbumSortState.SetOption(sortOverride.Value);
				}
				ArtistAlbumSortOption sortOption = _artistAlbumSortState.CurrentOption;
				string paginationKey = BuildArtistAlbumsPaginationKey(artistId, sortOption);
				bool offsetClamped;
				int normalizedOffset = NormalizeOffsetWithCap(paginationKey, ArtistAlbumsPageSize, offset, out offsetClamped);
				if (offsetClamped)
				{
					int page = (normalizedOffset / ArtistAlbumsPageSize) + 1;
					UpdateStatusBar($"页码过大，已跳到第 {page} 页");
				}
				offset = normalizedOffset;
				string viewSource = string.Format("artist_albums:{0}:order{1}:offset{2}", arg1: MapArtistAlbumSort(sortOption), arg0: artistId, arg2: offset);
				string accessibleName = artistName + " 的专辑";
				ViewLoadRequest request = new ViewLoadRequest(viewSource, accessibleName, "正在加载 " + artistName + " 的专辑...", !skipSave);
				ViewLoadResult<ArtistAlbumsViewData?> loadResult = await RunViewLoadAsync(request, async delegate(CancellationToken token)
				{
					int normalizedOffset = offset;
					List<AlbumInfo> albums;
					bool hasMore;
					int totalCount;
					if (sortOption == ArtistAlbumSortOption.Oldest)
					{
						(List<AlbumInfo> Albums, int NormalizedOffset, bool HasMore) ascendingResult = await LoadArtistAlbumsAscendingPageAsync(artistId, offset);
						albums = ascendingResult.Albums;
						normalizedOffset = ascendingResult.NormalizedOffset;
						hasMore = ascendingResult.HasMore;
						totalCount = _currentArtistAlbumsTotalCount;
					}
					else
					{
						(List<AlbumInfo> Albums, bool HasMore, int TotalCount) result = await _apiClient.GetArtistAlbumsAsync(artistId, 100, offset).ConfigureAwait(continueOnCapturedContext: false);
						albums = result.Albums ?? new List<AlbumInfo>();
						hasMore = result.HasMore;
						totalCount = result.TotalCount;
						_currentArtistAlbumsTotalCount = totalCount;
					}
					token.ThrowIfCancellationRequested();
					string statusText = ((albums.Count == 0) ? (artistName + " 暂无专辑") : $"已加载 {artistName} 专辑 {normalizedOffset + 1}-{normalizedOffset + albums.Count} / {totalCount}张");
					return new ArtistAlbumsViewData(albums, hasMore, normalizedOffset, totalCount, sortOption, statusText);
				}, "加载歌手专辑已取消").ConfigureAwait(continueOnCapturedContext: true);
				if (!loadResult.IsCanceled)
				{
					ArtistAlbumsViewData data = loadResult.Value ?? new ArtistAlbumsViewData(new List<AlbumInfo>(), hasMore: false, offset, 0, sortOption, artistName + " 暂无专辑");
					if (TryHandlePaginationEmptyResult(paginationKey, data.Offset, ArtistAlbumsPageSize, data.TotalCount, data.Albums?.Count ?? 0, data.HasMore, viewSource, accessibleName))
					{
						return;
					}
					_currentArtistAlbumsOffset = data.Offset;
					_currentArtistAlbumsHasMore = data.HasMore;
					_currentArtistAlbumsTotalCount = Math.Max(data.TotalCount, data.Offset + (data.Albums?.Count ?? 0));
					DisplayAlbums(data.Albums, preserveSelection: false, viewSource, accessibleName, data.Offset + 1, showPagination: true, data.HasMore, announceHeader: true, suppressFocus: true);
					UpdateArtistAlbumsSortMenuChecks();
					FocusListAfterEnrich(0);
					UpdateStatusBar(data.StatusText);
				}
			}
			catch (Exception ex)
			{
				Exception ex2 = ex;
				Exception ex3 = ex2;
				ArtistAlbumSortOption sortOption = _artistAlbumSortState.CurrentOption;
				string paginationKey = BuildArtistAlbumsPaginationKey(artistId, sortOption);
				string fallbackName = _currentArtist?.Name ?? $"歌手 {artistId}";
				string accessibleName = fallbackName + " 的专辑";
				string viewSource = $"artist_albums:{artistId}:order{MapArtistAlbumSort(sortOption)}:offset{offset}";
				if (TryHandlePaginationOffsetError(ex3, paginationKey, offset, ArtistAlbumsPageSize, viewSource, accessibleName))
				{
					return;
				}
				Debug.WriteLine($"[Artist] 加载专辑失败: {ex3}");
				MessageBox.Show("加载歌手专辑失败：" + ex3.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("加载歌手专辑失败");
			}
		}
	}

	private async Task LoadArtistFavoritesAsync(bool skipSave = false, bool preserveSelection = false)
	{
		try
		{
			if (!IsUserLoggedIn())
			{
				MessageBox.Show("请先登录后查看收藏的歌手", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				UpdateStatusBar("请先登录");
				return;
			}
			if (!skipSave)
			{
				SaveNavigationState();
			}
			await EnsureLibraryStateFreshAsync(LibraryEntityType.Artists);
			ViewLoadRequest request = new ViewLoadRequest("artist_favorites", "收藏的歌手", "正在加载收藏的歌手...");
			ViewLoadResult<(List<ArtistInfo> Items, bool HasMore, int Offset)?> result = await RunViewLoadAsync(request, (Func<CancellationToken, Task<(List<ArtistInfo>, bool, int)?>>)async delegate(CancellationToken token)
			{
				SearchResult<ArtistInfo> fetchResult = await FetchWithRetryUntilCancel((CancellationToken ct) => _apiClient.GetArtistSubscriptionsAsync(200), "artist_favorites", token, delegate(int attempt, Exception _)
				{
					SafeInvoke(delegate
					{
						UpdateStatusBar($"加载收藏歌手失败，正在重试（第 {attempt} 次）...");
					});
				}).ConfigureAwait(continueOnCapturedContext: true);
				List<ArtistInfo> favoriteArtists = fetchResult?.Items ?? new List<ArtistInfo>();
				foreach (ArtistInfo artist in favoriteArtists)
				{
					artist.IsSubscribed = true;
				}
				return (favoriteArtists, fetchResult?.HasMore ?? false, fetchResult?.Offset ?? 0);
			}, "加载收藏的歌手已取消");
			if (!result.IsCanceled)
			{
				(List<ArtistInfo> Items, bool HasMore, int Offset)? favoritesData = result.Value;
				if (favoritesData.HasValue)
				{
					DisplayArtists(favoritesData.Value.Items, favoritesData.Value.HasMore, favoritesData.Value.HasMore, checked(favoritesData.Value.Offset + 1), preserveSelection, "artist_favorites", "收藏的歌手");
					UpdateStatusBar($"收藏的歌手：{favoritesData.Value.Items.Count} 位");
				}
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Exception ex3 = ex2;
			Debug.WriteLine($"[Artist] 加载收藏歌手失败: {ex3}");
			MessageBox.Show("加载收藏的歌手失败：" + ex3.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("加载收藏歌手失败");
		}
	}

	private async Task LoadArtistCategoryTypesAsync(bool skipSave = false)
	{
		if (!skipSave)
		{
			SaveNavigationState();
		}
		ViewLoadRequest request = new ViewLoadRequest("artist_category_types", "歌手类型分类", "正在加载歌手类型...", !skipSave);
		ViewLoadResult<List<ListItemInfo>> loadResult = await RunViewLoadAsync(request, (CancellationToken _) => Task.FromResult((from option in ArtistMetadataHelper.GetTypeOptions()
			select new ListItemInfo
			{
				Type = ListItemType.Category,
				CategoryId = $"artist_type_{option.Code}",
				CategoryName = option.DisplayName
			}).ToList()), "加载歌手类型已取消").ConfigureAwait(continueOnCapturedContext: true);
		if (!loadResult.IsCanceled)
		{
			DisplayListItems(loadResult.Value, request.ViewSource, request.AccessibleName, preserveSelection: false, announceHeader: true, suppressFocus: true);
			FocusListAfterEnrich(0);
			UpdateStatusBar("请选择歌手类型");
		}
	}

	private async Task LoadArtistCategoryAreasAsync(int typeCode, bool skipSave = false)
	{
		if (!skipSave)
		{
			SaveNavigationState();
		}
		_currentArtistTypeFilter = typeCode;
		ViewLoadRequest request = new ViewLoadRequest($"artist_category_type:{typeCode}", "歌手地区筛选", "正在加载歌手地区...", !skipSave);
		ViewLoadResult<List<ListItemInfo>> loadResult = await RunViewLoadAsync(request, (CancellationToken _) => Task.FromResult((from option in ArtistMetadataHelper.GetAreaOptions()
			select new ListItemInfo
			{
				Type = ListItemType.Category,
				CategoryId = $"artist_area_{typeCode}_{option.Code}",
				CategoryName = option.DisplayName
			}).ToList()), "加载歌手地区已取消").ConfigureAwait(continueOnCapturedContext: true);
		if (!loadResult.IsCanceled)
		{
			DisplayListItems(loadResult.Value, request.ViewSource, request.AccessibleName, preserveSelection: false, announceHeader: true, suppressFocus: true);
			FocusListAfterEnrich(0);
			UpdateStatusBar("请选择歌手地区");
		}
	}

private async Task LoadArtistsByCategoryAsync(int typeCode, int areaCode, int offset = 0, bool skipSave = false, int pendingFocusIndex = -1)
{
	if (_enableArtistCategoryAll)
	{
		await LoadArtistsByCategoryAllAsync(typeCode, areaCode, skipSave, pendingFocusIndex);
		return;
	}
	try
	{
		_currentArtistCategoryLoadedAll = false;
		const int pageSize = 100;
		string paginationKey = BuildArtistCategoryPaginationKey(typeCode, areaCode);
		bool offsetClamped;
		int normalizedOffset = NormalizeOffsetWithCap(paginationKey, pageSize, offset, out offsetClamped);
		if (offsetClamped)
		{
			int normalizedPage = (normalizedOffset / pageSize) + 1;
			UpdateStatusBar($"页码过大，已跳到第 {normalizedPage} 页");
		}
		offset = normalizedOffset;
			if (!skipSave)
			{
				SaveNavigationState();
			}
			_currentArtistTypeFilter = typeCode;
			_currentArtistAreaFilter = areaCode;
			string viewSource = $"artist_category_list:{typeCode}:{areaCode}:offset{offset}";
			int page = (offset / pageSize) + 1;
		int requestFocusIndex = (pendingFocusIndex >= 0) ? pendingFocusIndex : 0;
		ViewLoadRequest request = new ViewLoadRequest(viewSource, "歌手分类列表", "正在加载歌手列表...", !skipSave, requestFocusIndex);
		ViewLoadResult<SearchViewData<ArtistInfo>> loadResult = await RunViewLoadAsync(request, delegate
		{
			using (WorkScopes.BeginSkeleton("ArtistCategory", viewSource))
			{
				return Task.FromResult(BuildSearchSkeletonViewData("歌手分类", "歌手", page, offset + 1, _currentArtists));
			}
		}, "加载歌手分类已取消").ConfigureAwait(continueOnCapturedContext: true);
		if (!loadResult.IsCanceled)
		{
			SearchViewData<ArtistInfo> skeleton = loadResult.Value;
			if (HasSkeletonItems(skeleton.Items))
			{
				DisplayArtists(skeleton.Items, showPagination: false, hasNextPage: false, skeleton.StartIndex, preserveSelection: false, viewSource, request.AccessibleName);
			}
			UpdateStatusBar(request.LoadingText);
			EnrichArtistCategoryResultsAsync(typeCode, areaCode, offset, viewSource, request.AccessibleName, pendingFocusIndex);
		}
	}
	catch (Exception ex)
		{
			Exception ex2 = ex;
			Exception ex3 = ex2;
			Debug.WriteLine($"[Artist] 加载分类歌手失败: {ex3}");
			MessageBox.Show("加载歌手分类失败：" + ex3.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("加载歌手分类失败");
		}
	}

private async Task LoadArtistsByCategoryAllAsync(int typeCode, int areaCode, bool skipSave = false, int pendingFocusIndex = -1)
{
	try
	{
		if (!skipSave)
		{
			SaveNavigationState();
		}
		_currentArtistTypeFilter = typeCode;
		_currentArtistAreaFilter = areaCode;
		int requestFocusIndex = (pendingFocusIndex >= 0) ? pendingFocusIndex : 0;
		const int page = 1;
		const int startIndex = 1;
		string viewSource = $"artist_category_list:{typeCode}:{areaCode}:offset0";
		ViewLoadRequest request = new ViewLoadRequest(viewSource, "歌手分类列表", "正在加载歌手列表...", !skipSave, requestFocusIndex);
		ViewLoadResult<SearchViewData<ArtistInfo>> loadResult = await RunViewLoadAsync(request, delegate
		{
			using (WorkScopes.BeginSkeleton("ArtistCategoryAll", viewSource))
			{
				return Task.FromResult(BuildSearchSkeletonViewData("歌手分类", "歌手", page, startIndex, _currentArtists));
			}
		}, "加载歌手分类已取消").ConfigureAwait(continueOnCapturedContext: true);
		if (!loadResult.IsCanceled)
		{
			SearchViewData<ArtistInfo> skeleton = loadResult.Value;
			if (HasSkeletonItems(skeleton.Items))
			{
				DisplayArtists(skeleton.Items, showPagination: false, hasNextPage: false, skeleton.StartIndex, preserveSelection: false, viewSource, request.AccessibleName);
			}
			UpdateStatusBar(request.LoadingText);
			EnrichArtistCategoryAllResultsAsync(typeCode, areaCode, viewSource, request.AccessibleName, pendingFocusIndex);
		}
	}
	catch (Exception ex)
	{
		Exception ex2 = ex;
		Exception ex3 = ex2;
		Debug.WriteLine($"[Artist] 加载分类歌手失败: {ex3}");
		MessageBox.Show("加载歌手分类失败：" + ex3.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
		UpdateStatusBar("加载歌手分类失败");
	}
}

private async Task EnrichArtistCategoryAllResultsAsync(int typeCode, int areaCode, string viewSource, string accessibleName, int pendingFocusIndex = -1)
{
	const int pageSize = 100;
	CancellationToken viewToken = GetCurrentViewContentToken();
	using (WorkScopes.BeginEnrichment("ArtistCategoryAll", viewSource))
	{
		try
		{
			List<ArtistInfo> all = new List<ArtistInfo>();
			int offset = 0;
			int totalCount = 0;
			bool hasMore = true;
			int safety = 0;
			while (hasMore && safety < 200)
			{
				viewToken.ThrowIfCancellationRequested();
				SearchResult<ArtistInfo> result = await FetchWithRetryUntilCancel((CancellationToken ct) => _apiClient.GetArtistsByCategoryAsync(typeCode, areaCode, pageSize, offset), $"artist_category:all:{typeCode}:{areaCode}:offset{offset}", viewToken, delegate(int attempt, Exception _)
				{
					SafeInvoke(delegate
					{
						UpdateStatusBar($"加载歌手分类失败，正在重试（第 {attempt} 次）...");
					});
				}).ConfigureAwait(continueOnCapturedContext: false);
				List<ArtistInfo> pageItems = result?.Items ?? new List<ArtistInfo>();
				if (pageItems.Count == 0)
				{
					hasMore = false;
					break;
				}
				all.AddRange(pageItems);
				offset = checked(offset + pageItems.Count);
				totalCount = Math.Max(totalCount, result?.TotalCount ?? 0);
				hasMore = result?.HasMore ?? false;
				if (pageItems.Count < pageSize)
				{
					hasMore = false;
				}
				if (totalCount > 0 && offset >= totalCount)
				{
					hasMore = false;
				}
				safety++;
				SafeInvoke(delegate
				{
					UpdateStatusBar($"正在加载歌手... 已获取 {all.Count} 位");
				});
			}
			if (viewToken.IsCancellationRequested)
			{
				return;
			}
			await ExecuteOnUiThreadAsync(delegate
			{
				if (!ShouldAbortViewRender(viewToken, "加载歌手分类"))
				{
					_currentArtists = CloneList(all);
					_currentArtistCategoryLoadedAll = true;
					_currentArtistCategoryHasMore = false;
					_currentArtistCategoryTotalCount = _currentArtists.Count;
					PatchArtists(_currentArtists, 1, showPagination: false, hasPreviousPage: false, hasNextPage: false, pendingFocusIndex);
					FocusListAfterEnrich(pendingFocusIndex);
					if (_currentArtists.Count == 0)
					{
						UpdateStatusBar("暂无歌手");
					}
					else
					{
						UpdateStatusBar($"已加载 {_currentArtists.Count} 位歌手");
					}
				}
			}).ConfigureAwait(continueOnCapturedContext: false);
			await EnsureLibraryStateFreshAsync(LibraryEntityType.Artists);
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			if (TryHandleOperationCancelled(ex2, "加载歌手分类已取消"))
			{
				return;
			}
			Debug.WriteLine($"[Artist] 加载分类歌手失败: {ex2}");
			await ExecuteOnUiThreadAsync(delegate
			{
				if (!ShouldAbortViewRender(viewToken, "加载歌手分类"))
				{
					MessageBox.Show("加载歌手分类失败：" + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
					UpdateStatusBar("加载歌手分类失败");
				}
			}).ConfigureAwait(continueOnCapturedContext: false);
		}
	}
}

	private async Task EnrichArtistCategoryResultsAsync(int typeCode, int areaCode, int offset, string viewSource, string accessibleName, int pendingFocusIndex = -1)
	{
		const int pageSize = 100;
		CancellationToken viewToken = GetCurrentViewContentToken();
		using (WorkScopes.BeginEnrichment("ArtistCategory", viewSource))
		{
			try
			{
				SearchResult<ArtistInfo> result = await FetchWithRetryUntilCancel((CancellationToken ct) => _apiClient.GetArtistsByCategoryAsync(typeCode, areaCode, pageSize, offset), $"artist_category:{typeCode}:{areaCode}:offset{offset}", viewToken, delegate(int attempt, Exception _)
				{
					SafeInvoke(delegate
					{
						UpdateStatusBar($"加载歌手分类失败，正在重试（第 {attempt} 次）...");
					});
				}).ConfigureAwait(continueOnCapturedContext: true);
				List<ArtistInfo> artists = result?.Items ?? new List<ArtistInfo>();
				int totalCount = result?.TotalCount ?? artists.Count;
				bool hasMore = result?.HasMore ?? false;
				string paginationKey = BuildArtistCategoryPaginationKey(typeCode, areaCode);
				if (TryHandlePaginationEmptyResult(paginationKey, offset, pageSize, totalCount, artists.Count, hasMore, viewSource, accessibleName))
				{
					return;
				}
				int page = (offset / pageSize) + 1;
				int maxPage = (totalCount > 0) ? Math.Max(1, (int)Math.Ceiling((double)totalCount / pageSize)) : page;
				if (viewToken.IsCancellationRequested)
				{
					return;
				}
				await ExecuteOnUiThreadAsync(delegate
				{
					if (!ShouldAbortViewRender(viewToken, "加载歌手分类"))
					{
						_currentArtists = CloneList(artists);
						_currentArtistCategoryHasMore = hasMore;
						_currentArtistCategoryTotalCount = Math.Max(totalCount, offset + _currentArtists.Count);
						bool showPagination = offset > 0 || hasMore;
						PatchArtists(_currentArtists, offset + 1, showPagination: showPagination, hasPreviousPage: offset > 0, hasNextPage: hasMore, pendingFocusIndex);
						FocusListAfterEnrich(pendingFocusIndex);
						if (_currentArtists.Count == 0)
						{
							UpdateStatusBar("暂无歌手");
						}
						else if (totalCount > 0)
						{
							UpdateStatusBar($"第 {page}/{maxPage} 页，本页 {_currentArtists.Count} 位 / 总 {totalCount} 位");
						}
						else
						{
							UpdateStatusBar($"第 {page} 页，本页 {_currentArtists.Count} 位{(hasMore ? "，还有更多" : string.Empty)}");
						}
					}
				}).ConfigureAwait(continueOnCapturedContext: false);
				await EnsureLibraryStateFreshAsync(LibraryEntityType.Artists);
			}
			catch (Exception ex)
			{
				Exception ex2 = ex;
				string paginationKey = BuildArtistCategoryPaginationKey(typeCode, areaCode);
				if (TryHandlePaginationOffsetError(ex2, paginationKey, offset, pageSize, viewSource, accessibleName))
				{
					return;
				}
				if (TryHandleOperationCancelled(ex2, "加载歌手分类已取消"))
				{
					return;
				}
				Debug.WriteLine($"[Artist] 加载分类歌手失败: {ex2}");
				await ExecuteOnUiThreadAsync(delegate
				{
					if (!ShouldAbortViewRender(viewToken, "加载歌手分类"))
					{
						MessageBox.Show("加载歌手分类失败：" + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
						UpdateStatusBar("加载歌手分类失败");
					}
				}).ConfigureAwait(continueOnCapturedContext: false);
			}
		}
	}

	private void ConfigureArtistContextMenu(ArtistInfo artist)
	{
		insertPlayMenuItem.Visible = false;
		likeSongMenuItem.Visible = false;
		unlikeSongMenuItem.Visible = false;
		addToPlaylistMenuItem.Visible = false;
		removeFromPlaylistMenuItem.Visible = false;
		downloadSongMenuItem.Visible = false;
		downloadPlaylistMenuItem.Visible = false;
		downloadAlbumMenuItem.Visible = false;
		batchDownloadMenuItem.Visible = false;
		downloadCategoryMenuItem.Visible = false;
		batchDownloadPlaylistsMenuItem.Visible = false;
		cloudMenuSeparator.Visible = false;
		subscribePlaylistMenuItem.Visible = false;
		unsubscribePlaylistMenuItem.Visible = false;
		deletePlaylistMenuItem.Visible = false;
		subscribeSongAlbumMenuItem.Visible = false;
		subscribeSongAlbumMenuItem.Tag = null;
		subscribeSongArtistMenuItem.Visible = false;
		subscribeSongArtistMenuItem.Tag = null;
		subscribeAlbumMenuItem.Visible = false;
		unsubscribeAlbumMenuItem.Visible = false;
		shareArtistMenuItem.Visible = true;
		shareArtistMenuItem.Tag = artist;
		subscribeArtistMenuItem.Tag = artist;
		unsubscribeArtistMenuItem.Tag = artist;
		bool flag = shareArtistMenuItem.Visible;
		if (IsUserLoggedIn())
		{
			bool flag2 = IsArtistSubscribed(artist);
			subscribeArtistMenuItem.Visible = !flag2;
			unsubscribeArtistMenuItem.Visible = flag2;
			flag |= subscribeArtistMenuItem.Visible || unsubscribeArtistMenuItem.Visible;
		}
		else
		{
			subscribeArtistMenuItem.Visible = false;
			unsubscribeArtistMenuItem.Visible = false;
		}
		toolStripSeparatorArtist.Visible = flag;
	}

	private ArtistInfo? GetArtistFromMenuSender(object sender)
	{
		if (sender is ToolStripMenuItem { Tag: ArtistInfo tag })
		{
			return tag;
		}
		return GetSelectedArtistFromSelection();
	}

	private ArtistInfo? GetSelectedArtistFromSelection()
	{
		ListViewItem selectedListViewItemSafe = GetSelectedListViewItemSafe();
		if (selectedListViewItemSafe == null)
		{
			return null;
		}
		if (selectedListViewItemSafe.Tag is ArtistInfo result)
		{
			return result;
		}
		if (selectedListViewItemSafe.Tag is ListItemInfo { Type: ListItemType.Artist } listItemInfo)
		{
			return listItemInfo.Artist;
		}
		return null;
	}

	private void shareArtistMenuItem_Click(object sender, EventArgs e)
	{
		ArtistInfo artistFromMenuSender = GetArtistFromMenuSender(sender);
		if (artistFromMenuSender == null)
		{
			return;
		}
		try
		{
			string text = $"https://music.163.com/#/artist?id={artistFromMenuSender.Id}";
			Clipboard.SetText(text);
			UpdateStatusBar("歌手链接已复制到剪贴板");
		}
		catch (Exception ex)
		{
			MessageBox.Show("复制链接失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("复制链接失败");
		}
	}

	private async void subscribeArtistMenuItem_Click(object sender, EventArgs e)
	{
		ArtistInfo artist = GetArtistFromMenuSender(sender);
		if (artist == null)
		{
			return;
		}
		await SubscribeArtistAsync(artist);
	}

	private async Task SubscribeArtistAsync(ArtistInfo artist)
	{
		if (artist == null || artist.Id <= 0)
		{
			MessageBox.Show("\u65E0\u6CD5\u8BC6\u522B\u6B4C\u624B\u4FE1\u606F\uFF0C\u6536\u85CF\u64CD\u4F5C\u5DF2\u53D6\u6D88\u3002", "\u63D0\u793A", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			return;
		}
		if (!IsUserLoggedIn())
		{
			MessageBox.Show("\u8BF7\u5148\u767B\u5F55\u540E\u518D\u6536\u85CF\u6B4C\u624B", "\u63D0\u793A", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			return;
		}
		try
		{
			UpdateStatusBar("\u6B63\u5728\u6536\u85CF\u6B4C\u624B...");
			if (await _apiClient.SetArtistSubscriptionAsync(artist.Id, subscribe: true))
			{
				artist.IsSubscribed = true;
				UpdateArtistSubscriptionState(artist.Id, isSubscribed: true);
				MessageBox.Show("\u5DF2\u6536\u85CF\u6B4C\u624B\uFF1A" + artist.Name, "\u6210\u529F", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				UpdateStatusBar("\u6536\u85CF\u6B4C\u624B\u6210\u529F");
				await RefreshArtistListAfterSubscriptionAsync(artist.Id);
			}
			else
			{
				MessageBox.Show("\u6536\u85CF\u6B4C\u624B\u5931\u8D25\uFF0C\u8BF7\u7A0D\u540E\u91CD\u8BD5\u3002", "\u5931\u8D25", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("\u6536\u85CF\u6B4C\u624B\u5931\u8D25");
			}
		}
		catch (Exception ex)
		{
			MessageBox.Show("\u6536\u85CF\u6B4C\u624B\u5931\u8D25: " + ex.Message, "\u9519\u8BEF", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("\u6536\u85CF\u6B4C\u624B\u5931\u8D25");
		}
	}

	private async void unsubscribeArtistMenuItem_Click(object sender, EventArgs e)
	{
		ArtistInfo artist = GetArtistFromMenuSender(sender);
		if (artist == null)
		{
			return;
		}
		if (!IsUserLoggedIn())
		{
			MessageBox.Show("请先登录后再取消收藏歌手", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			return;
		}
		try
		{
			UpdateStatusBar("正在取消收藏歌手...");
			if (await _apiClient.SetArtistSubscriptionAsync(artist.Id, subscribe: false))
			{
				artist.IsSubscribed = false;
				UpdateArtistSubscriptionState(artist.Id, isSubscribed: false);
				MessageBox.Show("已取消收藏歌手：" + artist.Name, "成功", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				UpdateStatusBar("取消收藏歌手成功");
				await RefreshArtistListAfterSubscriptionAsync(artist.Id);
			}
			else
			{
				MessageBox.Show("取消收藏歌手失败，请稍后重试。", "失败", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("取消收藏歌手失败");
			}
		}
		catch (Exception ex)
		{
			MessageBox.Show("取消收藏歌手失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("取消收藏歌手失败");
		}
	}

	private void ApplyArtistDetailToArtist(ArtistInfo artist, ArtistDetail detail)
	{
		if (artist != null && detail != null)
		{
			bool flag = !string.IsNullOrWhiteSpace(detail.Name);
			bool flag2 = string.IsNullOrWhiteSpace(artist.Name) || string.Equals(artist.Name, "歌手", StringComparison.OrdinalIgnoreCase) || (artist.Id > 0 && string.Equals(artist.Name, $"歌手 {artist.Id}", StringComparison.OrdinalIgnoreCase));
			if (flag && (flag2 || !string.Equals(artist.Name, detail.Name, StringComparison.OrdinalIgnoreCase)))
			{
				artist.Name = detail.Name;
			}
			artist.MusicCount = detail.MusicCount;
			artist.AlbumCount = detail.AlbumCount;
			artist.MvCount = detail.MvCount;
			artist.BriefDesc = (string.IsNullOrWhiteSpace(detail.BriefDesc) ? artist.BriefDesc : detail.BriefDesc);
			artist.Description = (string.IsNullOrWhiteSpace(detail.Description) ? artist.Description : detail.Description);
			artist.IsSubscribed = detail.IsSubscribed;
			UpdateArtistStatsCache(artist.Id, detail.MusicCount, detail.AlbumCount);
			UpdateArtistStatsInView(artist.Id, detail.MusicCount, detail.AlbumCount);
			string introText = ResolveArtistIntroText(artist);
			if (!string.IsNullOrWhiteSpace(introText))
			{
				UpdateArtistIntroCache(artist.Id, introText);
				UpdateArtistIntroInView(artist.Id, introText);
			}
		}
	}

	private bool TryGetCachedArtistStats(long artistId, out (int MusicCount, int AlbumCount) stats)
	{
		lock (_artistStatsCache)
		{
			return _artistStatsCache.TryGetValue(artistId, out stats);
		}
	}

	private bool TryGetCachedArtistIntro(long artistId, out string intro)
	{
		lock (_artistIntroCache)
		{
			return _artistIntroCache.TryGetValue(artistId, out intro);
		}
	}

	private void UpdateArtistStatsCache(long artistId, int musicCount, int albumCount)
	{
		if (artistId <= 0)
		{
			return;
		}
		lock (_artistStatsCache)
		{
			_artistStatsCache[artistId] = (musicCount, albumCount);
		}
	}

        private void UpdateArtistStatsInView(long artistId, int musicCount, int albumCount)
        {
                SafeInvoke(delegate
                {
                        int focusedListViewIndex = GetFocusedListViewIndex();
                        bool shouldAnnounce = false;
                        bool layoutDirty = false;
                        foreach (ListViewItem item in resultListView.Items)
                        {
                                if (item.Tag is ArtistInfo artistInfo && artistInfo.Id == artistId)
                                {
                                        artistInfo.MusicCount = musicCount;
                                        artistInfo.AlbumCount = albumCount;
                                        EnsureSubItemCount(item, 6);
                                        if (item.SubItems.Count > 3)
                                        {
                                                item.SubItems[3].Text = BuildArtistStatsLabel(musicCount, albumCount);
                                                UpdateListViewItemAccessibilityProperties(item, IsNvdaRunningCached());
                                                if (item.Index == focusedListViewIndex)
                                                {
                                                        shouldAnnounce = true;
                                                }
                                                layoutDirty = true;
                                        }
                                        break;
                                }
                        }
                        if (shouldAnnounce)
                        {
                                QueueFocusedListViewItemRefreshAnnouncement(focusedListViewIndex);
                        }
                        if (layoutDirty)
                        {
                                ScheduleResultListViewLayoutUpdate();
                        }
                });
        }

        private void UpdateArtistIntroInView(long artistId, string introText)
        {
                if (string.IsNullOrWhiteSpace(introText))
                {
                        return;
                }
                SafeInvoke(delegate
                {
                        int focusedListViewIndex = GetFocusedListViewIndex();
                        bool shouldAnnounce = false;
                        bool layoutDirty = false;
                        foreach (ListViewItem item in resultListView.Items)
                        {
                                if (item.Tag is ArtistInfo artistInfo && artistInfo.Id == artistId)
                                {
                                        artistInfo.Description = introText;
                                        EnsureSubItemCount(item, 6);
                                        if (item.SubItems.Count > 4)
                                        {
                                                item.SubItems[4].Text = introText;
                                                UpdateListViewItemAccessibilityProperties(item, IsNvdaRunningCached());
                                                if (item.Index == focusedListViewIndex)
                                                {
                                                        shouldAnnounce = true;
                                                }
                                                layoutDirty = true;
                                        }
                                        break;
                                }
                        }
                        if (shouldAnnounce)
                        {
                                QueueFocusedListViewItemRefreshAnnouncement(focusedListViewIndex);
                        }
                        if (layoutDirty)
                        {
                                ScheduleResultListViewLayoutUpdate();
                        }
                });
        }

	private void ScheduleArtistStatsRefresh(IEnumerable<ArtistInfo> artists)
	{
		if (_apiClient == null)
		{
			return;
		}
		List<ArtistInfo> pending = (from a in artists?.Where((ArtistInfo a) => a != null && a.Id > 0)
			group a by a.Id into g
			select g.First()).ToList();
		if (pending == null || pending.Count == 0)
		{
			return;
		}
		_artistStatsRefreshCts?.Cancel();
		_artistStatsRefreshCts?.Dispose();
		CancellationToken token = (_artistStatsRefreshCts = new CancellationTokenSource()).Token;
		lock (_artistStatsInFlight)
		{
			_artistStatsInFlight.Clear();
		}
		Task.Run(async delegate
		{
			try
			{
				if (TryGetListViewVisibleRange(out int visibleStart, out int visibleEnd))
				{
					HashSet<long> seen = new HashSet<long>();
					List<ArtistInfo> prioritized = new List<ArtistInfo>();
					int maxIndex = _currentArtists?.Count ?? 0;
					int start = Math.Max(0, Math.Min(visibleStart, maxIndex - 1));
					int end = Math.Max(start, Math.Min(visibleEnd, maxIndex - 1));
					for (int i = start; i <= end; i++)
					{
						ArtistInfo artistInfo = _currentArtists[i];
						if (artistInfo != null && artistInfo.Id > 0 && seen.Add(artistInfo.Id))
						{
							prioritized.Add(artistInfo);
						}
					}
					foreach (ArtistInfo artistInfo in pending)
					{
						if (artistInfo != null && artistInfo.Id > 0 && seen.Add(artistInfo.Id))
						{
							prioritized.Add(artistInfo);
						}
					}
					pending = prioritized;
				}
				SemaphoreSlim throttle = new SemaphoreSlim(Math.Max(1, ArtistDetailFetchConcurrency));
				List<Task> tasks = new List<Task>();
				foreach (ArtistInfo artist in pending)
				{
					if (token.IsCancellationRequested)
					{
						break;
					}
					bool needStats = artist.MusicCount <= 0 || artist.AlbumCount <= 0;
					bool hasIntro = !string.IsNullOrWhiteSpace(ResolveArtistIntroText(artist));
					bool introCacheHit = false;
					string cachedIntro = string.Empty;
					if (!hasIntro && TryGetCachedArtistIntro(artist.Id, out cachedIntro))
					{
						introCacheHit = true;
						if (!string.IsNullOrWhiteSpace(cachedIntro))
						{
							artist.Description = cachedIntro;
							hasIntro = true;
							UpdateArtistIntroInView(artist.Id, cachedIntro);
						}
					}
					if (TryGetCachedArtistStats(artist.Id, out var cachedStats))
					{
						UpdateArtistStatsInView(artist.Id, cachedStats.MusicCount, cachedStats.AlbumCount);
						needStats = false;
					}
					bool needIntroFetch = !hasIntro && !introCacheHit;
					if (!needStats && !needIntroFetch)
					{
						continue;
					}
					bool shouldFetch;
					lock (_artistStatsInFlight)
					{
						shouldFetch = _artistStatsInFlight.Add(artist.Id);
					}
					if (!shouldFetch)
					{
						continue;
					}
					await throttle.WaitAsync(token).ConfigureAwait(continueOnCapturedContext: false);
					bool includeIntro = needIntroFetch;
					tasks.Add(Task.Run(async delegate
					{
						try
						{
							ArtistDetail detail = await FetchWithRetryUntilCancel((CancellationToken ct) => _apiClient.GetArtistDetailAsync(artist.Id, includeIntroduction: includeIntro), $"artist_stats:{artist.Id}", token, delegate(int attempt, Exception ex7)
							{
								Debug.WriteLine($"[Artist] 刷新歌手统计失败（第 {attempt} 次）: {ex7.Message}");
							}).ConfigureAwait(continueOnCapturedContext: false);
							if (detail != null)
							{
								ApplyArtistDetailToArtist(artist, detail);
								if (includeIntro)
								{
									string introText = ResolveArtistIntroText(detail);
									if (string.IsNullOrWhiteSpace(introText))
									{
										UpdateArtistIntroCache(artist.Id, string.Empty);
									}
								}
							}
						}
						catch (OperationCanceledException)
						{
						}
						catch (Exception ex2)
						{
							Debug.WriteLine("[Artist] 刷新歌手统计放弃: " + ex2.Message);
						}
						finally
						{
							lock (_artistStatsInFlight)
							{
								_artistStatsInFlight.Remove(artist.Id);
							}
							throttle.Release();
						}
					}, token));
					if (ArtistDetailFetchDelayMs > 0)
					{
						try
						{
							await Task.Delay(ArtistDetailFetchDelayMs, token).ConfigureAwait(continueOnCapturedContext: false);
						}
						catch (OperationCanceledException)
						{
							break;
						}
					}
				}
				if (tasks.Count > 0)
				{
					await Task.WhenAll(tasks).ConfigureAwait(continueOnCapturedContext: false);
				}
			}
			catch (OperationCanceledException)
			{
			}
		}, token);
	}

	private async Task RefreshArtistListAfterSubscriptionAsync(long artistId)
	{
		try
		{
			if (string.Equals(_currentViewSource, "artist_favorites", StringComparison.OrdinalIgnoreCase))
			{
				await LoadArtistFavoritesAsync(skipSave: true, preserveSelection: true);
			}
			else if (_currentViewSource != null && _currentViewSource.StartsWith("artist_songs:", StringComparison.OrdinalIgnoreCase))
			{
				await LoadArtistSongsAsync(artistId, _currentArtistSongsOffset, skipSave: true, _artistSongSortState.CurrentOption);
			}
			else if (_currentViewSource != null && _currentViewSource.StartsWith("artist_albums:", StringComparison.OrdinalIgnoreCase))
			{
				await LoadArtistAlbumsAsync(artistId, _currentArtistAlbumsOffset, skipSave: true, _artistAlbumSortState.CurrentOption);
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Exception ex3 = ex2;
			Debug.WriteLine($"[Artist] 刷新列表失败: {ex3}");
		}
	}

	private static long ParseArtistIdFromViewSource(string source, string prefix)
	{
		if (!source.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
		{
			return 0L;
		}
		string text = source.Substring(prefix.Length);
		int num = text.IndexOf(':');
		if (num >= 0)
		{
			text = text.Substring(0, num);
		}
		long result;
		return long.TryParse(text, out result) ? result : 0;
	}

	private static void ParseArtistListViewSource(string source, out long artistId, out int offset, out string order, string defaultOrder = "hot")
	{
		artistId = 0L;
		offset = 0;
		order = defaultOrder;
		string[] array = source.Split(':');
		if (array.Length >= 2)
		{
			long.TryParse(array[1], out artistId);
		}
		string text = array.LastOrDefault((string p) => p.StartsWith("offset", StringComparison.OrdinalIgnoreCase));
		if (!string.IsNullOrEmpty(text))
		{
			int.TryParse(text.Substring("offset".Length), out offset);
		}
		checked
		{
			for (int num = 0; num < array.Length; num++)
			{
				string text2 = array[num];
				if (text2.StartsWith("order", StringComparison.OrdinalIgnoreCase))
				{
					if (text2.Length > "order".Length)
					{
						order = text2.Substring("order".Length);
						break;
					}
					if (num + 1 < array.Length)
					{
						order = array[num + 1];
						break;
					}
				}
			}
		}
	}

	private static void ParseArtistCategoryListViewSource(string source, out int typeCode, out int areaCode, out int offset)
	{
		typeCode = -1;
		areaCode = -1;
		offset = 0;
		string[] array = source.Split(':');
		if (array.Length >= 3)
		{
			int.TryParse(array[1], out typeCode);
			int.TryParse(array[2], out areaCode);
		}
		string text = array.LastOrDefault((string p) => p.StartsWith("offset", StringComparison.OrdinalIgnoreCase));
		if (!string.IsNullOrEmpty(text))
		{
			int.TryParse(text.Substring("offset".Length), out offset);
		}
	}

	private void UpdateArtistIntroCache(long artistId, string intro)
	{
		if (artistId <= 0)
		{
			return;
		}
		lock (_artistIntroCache)
		{
			_artistIntroCache[artistId] = intro ?? string.Empty;
		}
	}

	private static string StripOffsetSuffix(string source, out int offset)
	{
		offset = 0;
		if (string.IsNullOrWhiteSpace(source))
		{
			return source ?? string.Empty;
		}
		int index = source.LastIndexOf(":offset", StringComparison.OrdinalIgnoreCase);
		if (index < 0)
		{
			return source;
		}
		string tail = source.Substring(index + ":offset".Length);
		if (!int.TryParse(tail, out var parsed) || parsed < 0)
		{
			return source;
		}
		offset = parsed;
		return source.Substring(0, index);
	}

	private static void ParsePlaylistCategoryViewSource(string source, out string catName, out int offset)
	{
		catName = string.Empty;
		offset = 0;
		if (string.IsNullOrWhiteSpace(source))
		{
			return;
		}
		string baseSource = StripOffsetSuffix(source, out offset);
		if (!baseSource.StartsWith("playlist_cat_", StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		catName = baseSource.Substring("playlist_cat_".Length);
	}

	private static bool TryResolveNewSongsArea(string baseSource, out int areaType, out string areaName)
	{
		if (NewSongsAreaMap.TryGetValue(baseSource, out var info))
		{
			areaType = info.AreaType;
			areaName = info.AreaName;
			return true;
		}
		areaType = 0;
		areaName = string.Empty;
		return false;
	}

	private static string ResolveNewSongsViewSource(int areaType)
	{
		foreach (var kv in NewSongsAreaMap)
		{
			if (kv.Value.AreaType == areaType)
			{
				return kv.Key;
			}
		}
		return "new_songs_all";
	}

	private static bool TryParseNewSongsViewSource(string source, out int areaType, out string areaName, out int offset, out string baseSource)
	{
		areaType = 0;
		areaName = string.Empty;
		offset = 0;
		baseSource = string.Empty;
		if (string.IsNullOrWhiteSpace(source))
		{
			return false;
		}
		baseSource = StripOffsetSuffix(source, out offset);
		return TryResolveNewSongsArea(baseSource, out areaType, out areaName);
	}

	private static void ParseToplistViewSource(string source, out int offset)
	{
		offset = 0;
		if (string.IsNullOrWhiteSpace(source))
		{
			return;
		}
		string baseSource = StripOffsetSuffix(source, out offset);
		if (!string.Equals(baseSource, "toplist", StringComparison.OrdinalIgnoreCase))
		{
			offset = 0;
		}
	}

	private static void ParseHighQualityViewSource(string source, out int offset)
	{
		offset = 0;
		if (string.IsNullOrWhiteSpace(source))
		{
			return;
		}
		string baseSource = StripOffsetSuffix(source, out offset);
		if (!string.Equals(baseSource, "highquality_playlists", StringComparison.OrdinalIgnoreCase))
		{
			offset = 0;
		}
	}

	private static string BuildPlaylistCategoryViewSource(string cat, int offset)
	{
		return $"playlist_cat_{cat}:offset{Math.Max(0, offset)}";
	}

	private static string BuildNewSongsViewSource(int areaType, int offset)
	{
		string baseSource = ResolveNewSongsViewSource(areaType);
		return $"{baseSource}:offset{Math.Max(0, offset)}";
	}

	private static string BuildToplistViewSource(int offset)
	{
		return $"toplist:offset{Math.Max(0, offset)}";
	}

	private static string BuildHighQualityViewSource(int offset)
	{
		return $"highquality_playlists:offset{Math.Max(0, offset)}";
	}

	private static string MapArtistSongsOrder(ArtistSongSortOption order)
	{
		return (order == ArtistSongSortOption.Time) ? "time" : "hot";
	}

	private static ArtistSongSortOption ResolveArtistSongsOrder(string? order)
	{
		return string.Equals(order, "time", StringComparison.OrdinalIgnoreCase) ? ArtistSongSortOption.Time : ArtistSongSortOption.Hot;
	}

	private static string MapArtistAlbumSort(ArtistAlbumSortOption sort)
	{
		return (sort == ArtistAlbumSortOption.Oldest) ? "oldest" : "latest";
	}

	private static ArtistAlbumSortOption ResolveArtistAlbumSort(string? sort)
	{
		return string.Equals(sort, "oldest", StringComparison.OrdinalIgnoreCase) ? ArtistAlbumSortOption.Oldest : ArtistAlbumSortOption.Latest;
	}

	private async Task EnsureArtistAlbumsTotalCountAsync(long artistId)
	{
		if (_currentArtistAlbumsTotalCount <= 0)
		{
			int totalCount = (await _apiClient.GetArtistAlbumsAsync(artistId, 1)).Item3;
			_currentArtistAlbumsTotalCount = totalCount;
		}
	}

	private void ClearArtistAlbumsAscendingCache(long? artistId = null)
	{
		if (artistId.HasValue)
		{
			_artistAlbumsAscendingCache.Remove(artistId.Value);
		}
		else
		{
			_artistAlbumsAscendingCache.Clear();
		}
	}

	private void TrimArtistAlbumsAscendingCache()
	{
		while (_artistAlbumsAscendingCache.Count > 4)
		{
			long num = _artistAlbumsAscendingCache.Keys.FirstOrDefault();
			if (num != 0)
			{
				_artistAlbumsAscendingCache.Remove(num);
				continue;
			}
			break;
		}
	}

	private async Task<List<AlbumInfo>> LoadArtistAlbumsAscendingListAsync(long artistId)
	{
		List<AlbumInfo> allAlbums = new List<AlbumInfo>();
		int offset = 0;
		bool hasMore = true;
		int safety = 0;
		checked
		{
			while (hasMore && safety < 200)
			{
				var (albums, more, total) = await _apiClient.GetArtistAlbumsAsync(artistId, 100, offset);
				if (albums == null || albums.Count == 0)
				{
					break;
				}
				allAlbums.AddRange(albums);
				offset += albums.Count;
				hasMore = more;
				_currentArtistAlbumsTotalCount = total;
				safety++;
			}
			allAlbums.Reverse();
			return allAlbums;
		}
	}

	private async Task<(List<AlbumInfo> Albums, int NormalizedOffset, bool HasMore)> LoadArtistAlbumsAscendingPageAsync(long artistId, int offset)
	{
		if (!_artistAlbumsAscendingCache.TryGetValue(artistId, out var cachedAlbums))
		{
			cachedAlbums = await LoadArtistAlbumsAscendingListAsync(artistId);
			_artistAlbumsAscendingCache[artistId] = cachedAlbums;
			TrimArtistAlbumsAscendingCache();
		}
		int totalCount = cachedAlbums.Count;
		if (totalCount == 0)
		{
			return (Albums: new List<AlbumInfo>(), NormalizedOffset: 0, HasMore: false);
		}
		checked
		{
			int normalizedOffset = Math.Max(0, Math.Min(offset, Math.Max(0, totalCount - 1)));
			int remaining = Math.Max(0, totalCount - normalizedOffset);
			int takeCount = Math.Min(100, remaining);
			if (takeCount <= 0)
			{
				return (Albums: new List<AlbumInfo>(), NormalizedOffset: normalizedOffset, HasMore: false);
			}
			List<AlbumInfo> page = cachedAlbums.Skip(normalizedOffset).Take(takeCount).ToList();
			bool hasMore = normalizedOffset + takeCount < totalCount;
			return (Albums: page, NormalizedOffset: normalizedOffset, HasMore: hasMore);
		}
	}


}
}
