#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using YTPlayer.Core;
using YTPlayer.Forms;
using YTPlayer.Models;
using YTPlayer.Utils;
#pragma warning disable CS0219, CS8600, CS8601, CS8602, CS8603, CS8604, CS8618, CS8622, CS8625, CS8632, CS4014

namespace YTPlayer
{
public partial class MainForm
{
	private async Task LoadRecentListenedCategoryAsync(bool skipSave = false, int pendingFocusIndex = -1)
	{
		if (!IsUserLoggedIn())
		{
			MessageBox.Show("请先登录网易云账号以查看最近听过内容。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			await LoadHomePageAsync(skipSave: true, showErrorDialog: false);
			return;
		}
		try
		{
			int requestFocusIndex = (pendingFocusIndex >= 0) ? pendingFocusIndex : 0;
			ViewLoadRequest request = new ViewLoadRequest("recent_listened", "最近听过", "正在加载最近听过骨架...", !skipSave, requestFocusIndex);
			ViewLoadResult<(List<ListItemInfo> Items, string StatusText)?> loadResult = await RunViewLoadAsync(request, (CancellationToken _) => Task.FromResult(((List<ListItemInfo>, string)?)BuildRecentListenedSkeletonViewData()), "加载最近听过已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				(List<ListItemInfo> Items, string StatusText) data = loadResult.Value ?? BuildRecentListenedSkeletonViewData();
				if (HasSkeletonItems(data.Items))
				{
					DisplayListItems(data.Items, request.ViewSource, request.AccessibleName, preserveSelection: true, announceHeader: false);
				}
				UpdateStatusBar(data.StatusText);
				FocusListAfterEnrich(request.PendingFocusIndex);
				EnrichRecentListenedAsync(request.ViewSource, request.AccessibleName);
			}
		}
		catch (Exception ex)
		{
			if (!TryHandleOperationCancelled(ex, "加载最近听过已取消"))
			{
				Debug.WriteLine($"[RecentListened] 加载失败: {ex}");
				MessageBox.Show("加载最近听过失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("加载最近听过失败");
			}
		}
	}

	private (List<ListItemInfo> Items, string StatusText) BuildRecentListenedSkeletonViewData()
	{
		List<ListItemInfo> item = BuildRecentListenedEntries();
		string item2 = (_recentSummaryReady ? (BuildRecentListenedStatus() + "（缓存）") : "最近听过");
		return (Items: item, StatusText: item2);
	}

	private async Task EnrichRecentListenedAsync(string viewSource, string accessibleName)
	{
		CancellationToken viewToken = GetCurrentViewContentToken();
		try
		{
			await RefreshRecentSummariesAsync(forceRefresh: true, viewToken).ConfigureAwait(continueOnCapturedContext: false);
			if (viewToken.IsCancellationRequested)
			{
				return;
			}
			List<ListItemInfo> items = BuildRecentListenedEntries();
			string status = BuildRecentListenedStatus();
			await ExecuteOnUiThreadAsync(delegate
			{
				if (string.Equals(_currentViewSource, viewSource, StringComparison.OrdinalIgnoreCase))
				{
					SafeListView safeListView = resultListView;
					int num = ((safeListView != null && safeListView.SelectedIndices.Count > 0) ? resultListView.SelectedIndices[0] : (-1));
					if (_currentListItems.Count == items.Count)
					{
						PatchListItems(items, showPagination: false, hasPreviousPage: false, hasNextPage: false, num, incremental: true, preserveDisplayIndex: true);
					}
					else
					{
						DisplayListItems(items, viewSource, accessibleName, preserveSelection: true, announceHeader: false, suppressFocus: true);
						if (num >= 0)
						{
							EnsureListSelectionWithoutFocus(Math.Min(num, checked(resultListView.Items.Count - 1)));
						}
					}
					UpdateStatusBar(status);
				}
			}).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Exception ex3 = ex2;
			if (!TryHandleOperationCancelled(ex3, "加载最近听过已取消"))
			{
				Debug.WriteLine($"[RecentListened] 丰富失败: {ex3}");
			}
		}
	}

	private async Task LoadRecentPlayedSongsAsync(int pendingFocusIndex = -1)
	{
		if (!IsUserLoggedIn())
		{
			MessageBox.Show("请先登录网易云账号以查看最近播放记录。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			await LoadHomePageAsync(skipSave: true, showErrorDialog: false);
			return;
		}
		try
		{
			int requestFocusIndex = (pendingFocusIndex >= 0) ? pendingFocusIndex : 0;
			ViewLoadRequest request = new ViewLoadRequest("recent_play", "最近播放", "正在加载最近播放骨架...", cancelActiveNavigation: true, pendingFocusIndex: requestFocusIndex);
			ViewLoadResult<(List<SongInfo> Items, string StatusText)?> loadResult = await RunViewLoadAsync(request, (CancellationToken _) => Task.FromResult(((List<SongInfo>, string)?)BuildRecentSongsSkeletonViewData()), "加载最近播放已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				(List<SongInfo> Items, string StatusText) data = loadResult.Value ?? BuildRecentSongsSkeletonViewData();
				var (songs, _) = data;
				_currentPlaylist = null;
				if (HasSkeletonItems(songs))
				{
					DisplaySongs(songs, showPagination: false, hasNextPage: false, 1, preserveSelection: true, request.ViewSource, request.AccessibleName, skipAvailabilityCheck: true);
				}
				UpdateStatusBar(data.StatusText);
				EnrichRecentSongsAsync(request.ViewSource, request.AccessibleName);
			}
		}
		catch (Exception ex)
		{
			if (TryHandleOperationCancelled(ex, "加载最近播放已取消"))
			{
				return;
			}
			Debug.WriteLine($"[LoadRecentPlayedSongs] 异常: {ex}");
			MessageBox.Show("加载最近播放失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("加载最近播放失败");
			ShowListRetryPlaceholderCore("recent_play", resultListView?.AccessibleName, "歌曲列表", announceHeader: true, suppressFocus: false);
		}
	}

	private (List<SongInfo> Items, string StatusText) BuildRecentSongsSkeletonViewData()
	{
		List<SongInfo> list = ((_recentSongsCache.Count > 0) ? new List<SongInfo>(_recentSongsCache) : new List<SongInfo>());
		string item = ((list.Count > 0) ? $"最近播放（缓存）共 {list.Count} 首，正在刷新..." : "正在刷新最近播放...");
		return (Items: list, StatusText: item);
	}

	private async Task EnrichRecentSongsAsync(string viewSource, string accessibleName)
	{
		CancellationToken viewToken = GetCurrentViewContentToken();
		try
		{
			List<SongInfo> list = await FetchRecentSongsAsync(300, viewToken).ConfigureAwait(continueOnCapturedContext: false);
			if (viewToken.IsCancellationRequested || list == null)
			{
				return;
			}
			_recentSongsCache = new List<SongInfo>(list);
			_recentPlayCount = list.Count;
			string status = ((list.Count == 0) ? "暂无最近播放记录" : $"最近播放，共 {list.Count} 首歌曲");
			await ExecuteOnUiThreadAsync(delegate
			{
				if (string.Equals(_currentViewSource, viewSource, StringComparison.OrdinalIgnoreCase))
				{
					SafeListView safeListView = resultListView;
					int num = ((safeListView != null && safeListView.SelectedIndices.Count > 0) ? resultListView.SelectedIndices[0] : (-1));
					if (_currentSongs.Count == list.Count)
					{
						PatchSongs(list, 1, skipAvailabilityCheck: false, showPagination: false, hasPreviousPage: false, hasNextPage: false, num, allowSelection: false);
					}
					else
					{
						DisplaySongs(list, showPagination: false, hasNextPage: false, 1, preserveSelection: true, viewSource, accessibleName, skipAvailabilityCheck: false, announceHeader: false, suppressFocus: true);
						if (num >= 0)
						{
							EnsureListSelectionWithoutFocus(Math.Min(num, checked(resultListView.Items.Count - 1)));
						}
					}
					_currentPlaylist = null;
					if (list.Count == 0)
					{
						MessageBox.Show("暂时没有最近播放记录。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
					}
					UpdateStatusBar(status);
				}
			}).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Exception ex3 = ex2;
			if (!TryHandleOperationCancelled(ex3, "加载最近播放已取消"))
			{
				Debug.WriteLine($"[RecentPlay] 丰富最近播放失败: {ex3}");
			}
		}
	}

	private async Task LoadUserPodcasts(bool preserveSelection = false)
	{
		try
		{
			await EnsureLibraryStateFreshAsync(LibraryEntityType.Podcasts);
			int pendingIndex = ((preserveSelection && resultListView.SelectedIndices.Count > 0) ? resultListView.SelectedIndices[0] : 0);
			ViewLoadRequest request = new ViewLoadRequest("user_podcasts", "收藏的播客", "正在加载收藏的播客...", cancelActiveNavigation: true, pendingIndex);
			ViewLoadResult<(List<PodcastRadioInfo> Items, string StatusText)?> loadResult = await RunViewLoadAsync(request, (Func<CancellationToken, Task<(List<PodcastRadioInfo>, string)?>>)async delegate(CancellationToken token)
			{
				var (podcasts, totalCount) = await _apiClient.GetSubscribedPodcastsAsync(300).ConfigureAwait(continueOnCapturedContext: false);
				token.ThrowIfCancellationRequested();
				List<PodcastRadioInfo> normalized = podcasts ?? new List<PodcastRadioInfo>();
				string status = ((normalized.Count == 0) ? "暂无收藏的播客" : $"加载完成，共 {totalCount} 个播客");
				return (normalized, status);
			}, "加载收藏的播客已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				(List<PodcastRadioInfo> Items, string StatusText)? data = loadResult.Value;
				if (!data.HasValue || data.Value.Items.Count == 0)
				{
					MessageBox.Show("您还没有收藏播客。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
					UpdateStatusBar("暂无收藏的播客");
				}
				else
				{
					DisplayPodcasts(data.Value.Items, showPagination: false, hasNextPage: false, 1, preserveSelection, request.ViewSource, request.AccessibleName);
					UpdateStatusBar(data.Value.StatusText);
				}
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Exception ex3 = ex2;
			if (TryHandleOperationCancelled(ex3, "加载收藏的播客已取消"))
			{
				return;
			}
			Debug.WriteLine($"[LoadUserPodcasts] 异常: {ex3}");
			throw;
		}
	}

	private async Task LoadRecentPodcastsAsync(int pendingFocusIndex = -1)
	{
		if (!IsUserLoggedIn())
		{
			MessageBox.Show("请先登录网易云账号以查看最近播客。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			await LoadHomePageAsync(skipSave: true, showErrorDialog: false);
			return;
		}
		try
		{
			int requestFocusIndex = (pendingFocusIndex >= 0) ? pendingFocusIndex : 0;
			ViewLoadRequest request = new ViewLoadRequest("recent_podcasts", "最近播客", "正在加载最近播客骨架...", cancelActiveNavigation: true, pendingFocusIndex: requestFocusIndex);
			ViewLoadResult<(List<PodcastRadioInfo> Items, string StatusText)?> loadResult = await RunViewLoadAsync(request, (CancellationToken _) => Task.FromResult(((List<PodcastRadioInfo>, string)?)BuildRecentPodcastsSkeletonViewData()), "加载最近播客已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				(List<PodcastRadioInfo> Items, string StatusText) data = loadResult.Value ?? BuildRecentPodcastsSkeletonViewData();
				List<PodcastRadioInfo> podcasts = data.Items ?? new List<PodcastRadioInfo>();
				if (HasSkeletonItems(podcasts))
				{
					DisplayPodcasts(podcasts, showPagination: false, hasNextPage: false, 1, preserveSelection: true, request.ViewSource, request.AccessibleName);
				}
				UpdateStatusBar(data.StatusText);
				EnrichRecentPodcastsAsync(request.ViewSource, request.AccessibleName);
			}
		}
		catch (Exception ex)
		{
			if (TryHandleOperationCancelled(ex, "加载最近播客已取消"))
			{
				return;
			}
			Debug.WriteLine($"[LoadRecentPodcasts] 异常: {ex}");
			MessageBox.Show("加载最近播客失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("加载最近播客失败");
			ShowListRetryPlaceholderCore("recent_podcasts", resultListView?.AccessibleName, "播客列表", announceHeader: true, suppressFocus: false);
		}
	}

	private (List<PodcastRadioInfo> Items, string StatusText) BuildRecentPodcastsSkeletonViewData()
	{
		List<PodcastRadioInfo> list = ((_recentPodcastsCache.Count > 0) ? new List<PodcastRadioInfo>(_recentPodcastsCache) : new List<PodcastRadioInfo>());
		string item = ((list.Count > 0) ? $"最近播客（缓存）共 {list.Count} 个，正在刷新..." : "正在刷新最近播客...");
		return (Items: list, StatusText: item);
	}

	private async Task EnrichRecentPodcastsAsync(string viewSource, string accessibleName)
	{
		CancellationToken viewToken = GetCurrentViewContentToken();
		try
		{
			List<PodcastRadioInfo> list = await FetchRecentPodcastsAsync(100, viewToken).ConfigureAwait(continueOnCapturedContext: false);
			if (viewToken.IsCancellationRequested || list == null)
			{
				return;
			}
			_recentPodcastsCache = new List<PodcastRadioInfo>(list);
			_recentPodcastCount = list.Count;
			string status = ((list.Count == 0) ? "暂无最近播放的播客" : $"最近播客，共 {list.Count} 个");
			await ExecuteOnUiThreadAsync(delegate
			{
				if (string.Equals(_currentViewSource, viewSource, StringComparison.OrdinalIgnoreCase))
				{
					SafeListView safeListView = resultListView;
					int num = ((safeListView != null && safeListView.SelectedIndices.Count > 0) ? resultListView.SelectedIndices[0] : (-1));
					if (_currentPodcasts.Count == list.Count)
					{
						PatchPodcasts(list, 1, showPagination: false, hasPreviousPage: false, hasNextPage: false, num, allowSelection: false);
					}
					else
					{
						DisplayPodcasts(list, showPagination: false, hasNextPage: false, 1, preserveSelection: true, viewSource, accessibleName, announceHeader: false, suppressFocus: true);
						if (num >= 0)
						{
							EnsureListSelectionWithoutFocus(Math.Min(num, checked(resultListView.Items.Count - 1)));
						}
					}
					if (list.Count == 0)
					{
						MessageBox.Show("暂时没有最近播放的播客。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
					}
					UpdateStatusBar(status);
				}
			}).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Exception ex3 = ex2;
			if (!TryHandleOperationCancelled(ex3, "加载最近播客已取消"))
			{
				Debug.WriteLine($"[RecentPodcasts] 丰富最近播客失败: {ex3}");
			}
		}
	}

	private async Task OpenPodcastRadioAsync(PodcastRadioInfo podcast, bool skipSave = false)
	{
		if (podcast == null)
		{
			MessageBox.Show("无法打开播客，缺少有效信息。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
		}
		else
		{
			await LoadPodcastEpisodesAsync(podcast.Id, 0, skipSave, podcast);
		}
	}

	private async Task LoadPodcastEpisodesAsync(long radioId, int offset, bool skipSave = false, PodcastRadioInfo? podcastInfo = null, bool? sortAscendingOverride = null)
	{
		if (radioId <= 0)
		{
			MessageBox.Show("无法加载播客节目，缺少播客标识。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			return;
		}
		checked
		{
			try
			{
				UpdateStatusBar("正在加载播客...");
				if (!skipSave)
				{
					SaveNavigationState();
				}
				bool isDifferentRadio = _currentPodcast == null || _currentPodcast.Id != radioId;
				if (podcastInfo != null)
				{
					_currentPodcast = podcastInfo;
				}
				else if (_currentPodcast == null || _currentPodcast.Id != radioId)
				{
					PodcastRadioInfo detail = await _apiClient.GetPodcastRadioDetailAsync(radioId);
					if (detail != null)
					{
						_currentPodcast = detail;
					}
				}
				if (isDifferentRadio && !sortAscendingOverride.HasValue)
				{
					_podcastSortState.SetOption(option: false);
				}
			if (sortAscendingOverride.HasValue)
			{
				_podcastSortState.SetOption(sortAscendingOverride.Value);
			}
			bool isAscending = _podcastSortState.CurrentOption;
			string paginationKey = BuildPodcastEpisodesPaginationKey(radioId, isAscending);
			bool offsetClamped;
			int normalizedOffset = NormalizeOffsetWithCap(paginationKey, PodcastSoundPageSize, Math.Max(0, offset), out offsetClamped);
			if (offsetClamped)
			{
				int page = (normalizedOffset / PodcastSoundPageSize) + 1;
				UpdateStatusBar($"页码过大，已跳到第 {page} 页");
			}
			offset = normalizedOffset;
			(List<PodcastEpisodeInfo> Episodes, bool HasMore, int TotalCount) tuple = await _apiClient.GetPodcastEpisodesAsync(radioId, 50, Math.Max(0, offset), isAscending);
			List<PodcastEpisodeInfo> episodes = tuple.Episodes;
			bool hasMore = tuple.HasMore;
			int totalCount = tuple.TotalCount;
			string accessibleName = _currentPodcast?.Name ?? "播客节目";
			string viewSource = $"podcast:{radioId}:offset{Math.Max(0, offset)}";
			if (isAscending)
			{
				viewSource += ":asc1";
			}
			if (TryHandlePaginationEmptyResult(paginationKey, Math.Max(0, offset), PodcastSoundPageSize, totalCount, episodes?.Count ?? 0, hasMore, viewSource, accessibleName))
			{
				return;
			}
			_currentPodcastSoundOffset = Math.Max(0, offset);
				_currentPodcastHasMore = hasMore;
				_currentPodcastEpisodeTotalCount = Math.Max(totalCount, offset + (episodes?.Count ?? 0));
				DisplayPodcastEpisodes(episodes, unchecked(_currentPodcastSoundOffset > 0 || hasMore), hasMore, _currentPodcastSoundOffset + 1, preserveSelection: false, viewSource, accessibleName);
				UpdatePodcastSortMenuChecks();
				if (episodes == null || episodes.Count == 0)
				{
					UpdateStatusBar(accessibleName + "，暂无节目");
					return;
				}
				int currentPage = unchecked(_currentPodcastSoundOffset / 50) + 1;
				int totalPages = Math.Max(1, (int)Math.Ceiling((double)totalCount / 50.0));
				UpdateStatusBar($"{accessibleName}：第 {currentPage}/{totalPages} 页，本页 {episodes.Count} 个节目");
			}
			catch (Exception ex)
			{
				bool isAscending = _podcastSortState.CurrentOption;
				string paginationKey = BuildPodcastEpisodesPaginationKey(radioId, isAscending);
				string accessibleName = _currentPodcast?.Name ?? "播客节目";
				string viewSource = $"podcast:{radioId}:offset{Math.Max(0, offset)}";
				if (isAscending)
				{
					viewSource += ":asc1";
				}
				if (TryHandlePaginationOffsetError(ex, paginationKey, offset, PodcastSoundPageSize, viewSource, accessibleName))
				{
					return;
				}
				Debug.WriteLine($"[Podcast] 加载播客失败: {ex}");
				MessageBox.Show("加载播客失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("加载播客失败");
			}
		}
	}

	private static void ParsePodcastViewSource(string? viewSource, out long radioId, out int offset, out bool ascending)
	{
		radioId = 0L;
		offset = 0;
		ascending = false;
		if (string.IsNullOrWhiteSpace(viewSource) || !viewSource.StartsWith("podcast:", StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		string[] array = viewSource.Split(new char[1] { ':' }, StringSplitOptions.RemoveEmptyEntries);
		if (array.Length >= 2)
		{
			long.TryParse(array[1], out radioId);
		}
		foreach (string item in array.Skip(2))
		{
			if (item.StartsWith("offset", StringComparison.OrdinalIgnoreCase) && int.TryParse(item.Substring("offset".Length), out var result))
			{
				offset = result;
			}
			else if (item.StartsWith("asc", StringComparison.OrdinalIgnoreCase))
			{
				string text = item.Substring("asc".Length);
				int result2;
				if (string.IsNullOrEmpty(text))
				{
					ascending = true;
				}
				else if (int.TryParse(text, out result2))
				{
					ascending = result2 != 0;
				}
			}
		}
	}

	private static void ParsePodcastCategoryViewSource(string? viewSource, out int categoryId, out int offset)
	{
		categoryId = 0;
		offset = 0;
		if (string.IsNullOrWhiteSpace(viewSource) || !viewSource.StartsWith("podcast_cat_", StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		string[] array = viewSource.Substring("podcast_cat_".Length).Split(new char[1] { ':' }, StringSplitOptions.RemoveEmptyEntries);
		if (array.Length >= 1)
		{
			int.TryParse(array[0], out categoryId);
		}
		foreach (string item in array.Skip(1))
		{
			if (item.StartsWith("offset", StringComparison.OrdinalIgnoreCase) && int.TryParse(item.Substring("offset".Length), out var result))
			{
				offset = result;
			}
		}
	}

	private string ResolvePodcastCategoryName(int categoryId)
	{
		if (categoryId <= 0)
		{
			return string.Empty;
		}
		lock (_podcastCategoryLock)
		{
			if (_podcastCategories.TryGetValue(categoryId, out var value) && value != null && !string.IsNullOrWhiteSpace(value.Name))
			{
				return value.Name;
			}
		}
		return string.Empty;
	}

	private async Task LoadRecentPlaylistsAsync(int pendingFocusIndex = -1)
	{
		if (!IsUserLoggedIn())
		{
			MessageBox.Show("请先登录网易云账号以查看最近播放的歌单。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			await LoadHomePageAsync(skipSave: true, showErrorDialog: false);
			return;
		}
		try
		{
			int requestFocusIndex = (pendingFocusIndex >= 0) ? pendingFocusIndex : 0;
			ViewLoadRequest request = new ViewLoadRequest("recent_playlists", "最近歌单", "正在加载最近歌单骨架...", cancelActiveNavigation: true, pendingFocusIndex: requestFocusIndex);
			ViewLoadResult<(List<PlaylistInfo> Items, string StatusText)?> loadResult = await RunViewLoadAsync(request, (CancellationToken _) => Task.FromResult(((List<PlaylistInfo>, string)?)BuildRecentPlaylistsSkeletonViewData()), "加载最近歌单已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				(List<PlaylistInfo> Items, string StatusText) data = loadResult.Value ?? BuildRecentPlaylistsSkeletonViewData();
				var (playlists, _) = data;
				if (HasSkeletonItems(playlists))
				{
					DisplayPlaylists(playlists, preserveSelection: true, request.ViewSource, request.AccessibleName);
				}
				UpdateStatusBar(data.StatusText);
				EnrichRecentPlaylistsAsync(request.ViewSource, request.AccessibleName);
			}
		}
		catch (Exception ex)
		{
			if (TryHandleOperationCancelled(ex, "加载最近歌单已取消"))
			{
				return;
			}
			Debug.WriteLine($"[LoadRecentPlaylists] 异常: {ex}");
			MessageBox.Show("加载最近歌单失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("加载最近歌单失败");
			ShowListRetryPlaceholderCore("recent_playlists", resultListView?.AccessibleName, "歌单列表", announceHeader: true, suppressFocus: false);
		}
	}

	private (List<PlaylistInfo> Items, string StatusText) BuildRecentPlaylistsSkeletonViewData()
	{
		List<PlaylistInfo> list = ((_recentPlaylistsCache.Count > 0) ? new List<PlaylistInfo>(_recentPlaylistsCache) : new List<PlaylistInfo>());
		string item = ((list.Count > 0) ? $"最近歌单（缓存）共 {list.Count} 个，正在刷新..." : "正在刷新最近歌单...");
		return (Items: list, StatusText: item);
	}

	private async Task EnrichRecentPlaylistsAsync(string viewSource, string accessibleName)
	{
		CancellationToken viewToken = GetCurrentViewContentToken();
		try
		{
			List<PlaylistInfo> list = await FetchRecentPlaylistsAsync(100, viewToken).ConfigureAwait(continueOnCapturedContext: false);
			if (viewToken.IsCancellationRequested || list == null)
			{
				return;
			}
			_recentPlaylistsCache = new List<PlaylistInfo>(list);
			_recentPlaylistCount = list.Count;
			string status = ((list.Count == 0) ? "暂无最近播放的歌单" : $"最近歌单，共 {list.Count} 个");
			await ExecuteOnUiThreadAsync(delegate
			{
				if (string.Equals(_currentViewSource, viewSource, StringComparison.OrdinalIgnoreCase))
				{
					SafeListView safeListView = resultListView;
					int num = ((safeListView != null && safeListView.SelectedIndices.Count > 0) ? resultListView.SelectedIndices[0] : (-1));
					if (_currentPlaylists.Count == list.Count)
					{
						PatchPlaylists(list, 1, showPagination: false, hasPreviousPage: false, hasNextPage: false, num, allowSelection: false);
					}
					else
					{
						DisplayPlaylists(list, preserveSelection: true, viewSource, accessibleName, 1, showPagination: false, hasNextPage: false, announceHeader: false, suppressFocus: true);
						if (num >= 0)
						{
							EnsureListSelectionWithoutFocus(Math.Min(num, checked(resultListView.Items.Count - 1)));
						}
					}
					if (list.Count == 0)
					{
						MessageBox.Show("暂时没有最近播放的歌单。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
					}
					UpdateStatusBar(status);
				}
			}).ConfigureAwait(continueOnCapturedContext: false);
			if (!viewToken.IsCancellationRequested && list.Count > 0)
			{
				bool metaUpdated = await EnrichRecentPlaylistsMetaAsync(list, viewToken).ConfigureAwait(continueOnCapturedContext: false);
				if (!viewToken.IsCancellationRequested && metaUpdated)
				{
					await ExecuteOnUiThreadAsync(delegate
					{
						if (string.Equals(_currentViewSource, viewSource, StringComparison.OrdinalIgnoreCase))
						{
							SafeListView safeListView = resultListView;
							int num = ((safeListView != null && safeListView.SelectedIndices.Count > 0) ? resultListView.SelectedIndices[0] : (-1));
							if (_currentPlaylists.Count == list.Count)
							{
								PatchPlaylists(list, 1, showPagination: false, hasPreviousPage: false, hasNextPage: false, num, allowSelection: false);
							}
							else
							{
								DisplayPlaylists(list, preserveSelection: true, viewSource, accessibleName, 1, showPagination: false, hasNextPage: false, announceHeader: false, suppressFocus: true);
								if (num >= 0)
								{
									EnsureListSelectionWithoutFocus(Math.Min(num, checked(resultListView.Items.Count - 1)));
								}
							}
						}
					}).ConfigureAwait(continueOnCapturedContext: false);
				}
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Exception ex3 = ex2;
			if (!TryHandleOperationCancelled(ex3, "加载最近歌单已取消"))
			{
				Debug.WriteLine($"[RecentPlaylists] 丰富最近歌单失败: {ex3}");
			}
		}
	}

	private async Task<bool> EnrichRecentPlaylistsMetaAsync(List<PlaylistInfo> playlists, CancellationToken cancellationToken)
	{
		if (playlists == null || playlists.Count == 0 || _apiClient == null)
		{
			return false;
		}
		List<PlaylistInfo> targets = playlists.Where((PlaylistInfo p) => p != null && !string.IsNullOrWhiteSpace(p.Id) && p.TrackCount <= 0).ToList();
		if (targets.Count == 0)
		{
			return false;
		}
		int updatedCount = 0;
		using SemaphoreSlim semaphore = new SemaphoreSlim(4);
		List<Task> tasks = new List<Task>();
		foreach (PlaylistInfo target in targets)
		{
			tasks.Add(Task.Run(async delegate
			{
				bool acquired = false;
				try
				{
					await semaphore.WaitAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
					acquired = true;
					cancellationToken.ThrowIfCancellationRequested();
					PlaylistInfo detail = await _apiClient.GetPlaylistDetailAsync(target.Id).ConfigureAwait(continueOnCapturedContext: false);
					cancellationToken.ThrowIfCancellationRequested();
					if (detail == null)
					{
						return;
					}
					bool changed = false;
					if (target.TrackCount <= 0 && detail.TrackCount > 0)
					{
						target.TrackCount = detail.TrackCount;
						changed = true;
					}
					if (string.IsNullOrWhiteSpace(target.Description) && !string.IsNullOrWhiteSpace(detail.Description))
					{
						target.Description = detail.Description;
						changed = true;
					}
					if (string.IsNullOrWhiteSpace(target.Creator) && !string.IsNullOrWhiteSpace(detail.Creator))
					{
						target.Creator = detail.Creator;
						changed = true;
					}
					if (changed)
					{
						Interlocked.Increment(ref updatedCount);
					}
				}
				catch (OperationCanceledException)
				{
				}
				catch (Exception ex)
				{
					Debug.WriteLine($"[RecentPlaylists] 获取歌单详情失败: {target?.Id} {ex.Message}");
				}
				finally
				{
					if (acquired)
					{
						semaphore.Release();
					}
				}
			}, cancellationToken));
		}
		try
		{
			await Task.WhenAll(tasks).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (OperationCanceledException)
		{
		}
		return updatedCount > 0;
	}

	private async Task LoadRecentAlbumsAsync(int pendingFocusIndex = -1)
	{
		if (!IsUserLoggedIn())
		{
			MessageBox.Show("请先登录网易云账号以查看最近播放的专辑。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			await LoadHomePageAsync(skipSave: true, showErrorDialog: false);
			return;
		}
		try
		{
			int requestFocusIndex = (pendingFocusIndex >= 0) ? pendingFocusIndex : 0;
			ViewLoadRequest request = new ViewLoadRequest("recent_albums", "最近专辑", "正在加载最近专辑骨架...", cancelActiveNavigation: true, pendingFocusIndex: requestFocusIndex);
			ViewLoadResult<(List<AlbumInfo> Items, string StatusText)?> loadResult = await RunViewLoadAsync(request, (CancellationToken _) => Task.FromResult(((List<AlbumInfo>, string)?)BuildRecentAlbumsSkeletonViewData()), "加载最近专辑已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				(List<AlbumInfo> Items, string StatusText) data = loadResult.Value ?? BuildRecentAlbumsSkeletonViewData();
				var (albums, _) = data;
				if (HasSkeletonItems(albums))
				{
					DisplayAlbums(albums, preserveSelection: true, request.ViewSource, request.AccessibleName);
				}
				UpdateStatusBar(data.StatusText);
				EnrichRecentAlbumsAsync(request.ViewSource, request.AccessibleName);
			}
		}
		catch (Exception ex)
		{
			if (TryHandleOperationCancelled(ex, "加载最近专辑已取消"))
			{
				return;
			}
			Debug.WriteLine($"[LoadRecentAlbums] 异常: {ex}");
			MessageBox.Show("加载最近专辑失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("加载最近专辑失败");
			ShowListRetryPlaceholderCore("recent_albums", resultListView?.AccessibleName, "专辑列表", announceHeader: true, suppressFocus: false);
		}
	}

	private (List<AlbumInfo> Items, string StatusText) BuildRecentAlbumsSkeletonViewData()
	{
		List<AlbumInfo> list = ((_recentAlbumsCache.Count > 0) ? new List<AlbumInfo>(_recentAlbumsCache) : new List<AlbumInfo>());
		string item = ((list.Count > 0) ? $"最近专辑（缓存）共 {list.Count} 张，正在刷新..." : "正在刷新最近专辑...");
		return (Items: list, StatusText: item);
	}

	private async Task EnrichRecentAlbumsAsync(string viewSource, string accessibleName)
	{
		CancellationToken viewToken = GetCurrentViewContentToken();
		try
		{
			List<AlbumInfo> list = await FetchRecentAlbumsAsync(100, viewToken).ConfigureAwait(continueOnCapturedContext: false);
			if (viewToken.IsCancellationRequested || list == null)
			{
				return;
			}
			_recentAlbumsCache = new List<AlbumInfo>(list);
			_recentAlbumCount = list.Count;
			string status = ((list.Count == 0) ? "暂无最近播放的专辑" : $"最近专辑，共 {list.Count} 张");
			await ExecuteOnUiThreadAsync(delegate
			{
				if (string.Equals(_currentViewSource, viewSource, StringComparison.OrdinalIgnoreCase))
				{
					SafeListView safeListView = resultListView;
					int num = ((safeListView != null && safeListView.SelectedIndices.Count > 0) ? resultListView.SelectedIndices[0] : (-1));
					if (_currentAlbums.Count == list.Count)
					{
						PatchAlbums(list, 1, showPagination: false, hasPreviousPage: false, hasNextPage: false, num, allowSelection: false);
					}
					else
					{
						DisplayAlbums(list, preserveSelection: true, viewSource, accessibleName, 1, showPagination: false, hasNextPage: false, announceHeader: false, suppressFocus: true);
						if (num >= 0)
						{
							EnsureListSelectionWithoutFocus(Math.Min(num, checked(resultListView.Items.Count - 1)));
						}
					}
					if (list.Count == 0)
					{
						MessageBox.Show("暂时没有最近播放的专辑。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
					}
					UpdateStatusBar(status);
				}
			}).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Exception ex3 = ex2;
			if (!TryHandleOperationCancelled(ex3, "加载最近专辑已取消"))
			{
				Debug.WriteLine($"[RecentAlbums] 丰富最近专辑失败: {ex3}");
			}
		}
	}


}
}
