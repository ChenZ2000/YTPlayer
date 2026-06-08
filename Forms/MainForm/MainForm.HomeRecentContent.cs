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
				FocusListAfterEnrich(request.PendingFocusIndex, request.ViewSource);
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
			_currentUserPodcastsNextOffset = 0;
			_currentUserPodcastsHasMore = false;
			_currentUserPodcastsTotalCount = 0;
			_userPodcastsLoadMoreInFlight = false;
			_userPodcastsProactiveLoadRunning = false;
			int proactiveGeneration = ++_userPodcastsProactiveLoadGeneration;
			ViewLoadRequest request = new ViewLoadRequest("user_podcasts", "收藏的播客", "正在加载收藏的播客...", cancelActiveNavigation: true, pendingIndex);
			ViewLoadResult<(List<PodcastRadioInfo> Items, int NextOffset, int TotalCount, bool HasMore)?> loadResult = await RunViewLoadAsync(request, (Func<CancellationToken, Task<(List<PodcastRadioInfo>, int, int, bool)?>>)async delegate(CancellationToken token)
			{
				return await FetchUserPodcastsPageAsync(0, token).ConfigureAwait(continueOnCapturedContext: false);
			}, "加载收藏的播客已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				(List<PodcastRadioInfo> Items, int NextOffset, int TotalCount, bool HasMore)? data = loadResult.Value;
				if (data.HasValue)
				{
					_currentUserPodcastsNextOffset = data.Value.NextOffset;
					_currentUserPodcastsHasMore = data.Value.HasMore;
					_currentUserPodcastsTotalCount = Math.Max(data.Value.TotalCount, data.Value.Items.Count);
				}
				if (!data.HasValue || data.Value.Items.Count == 0)
				{
					if (!data.HasValue || !data.Value.HasMore)
					{
						MessageBox.Show("您还没有收藏播客。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
						UpdateStatusBar("暂无收藏的播客");
					}
					else
					{
						UpdateStatusBar(BuildUserPodcastsStatusText(0, _currentUserPodcastsTotalCount, _currentUserPodcastsHasMore));
						StartUserPodcastsProactiveExpansion(proactiveGeneration);
					}
				}
				else
				{
					DisplayPodcasts(data.Value.Items, showPagination: false, hasNextPage: false, 1, preserveSelection, request.ViewSource, request.AccessibleName);
					UpdateStatusBar(BuildUserPodcastsStatusText(_currentPodcasts.Count, _currentUserPodcastsTotalCount, _currentUserPodcastsHasMore));
					StartUserPodcastsProactiveExpansion(proactiveGeneration);
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

	private async Task<(List<PodcastRadioInfo> Items, int NextOffset, int TotalCount, bool HasMore)> FetchUserPodcastsPageAsync(int offset, CancellationToken token)
	{
		const int pageSize = 100;
		var (podcasts, totalCount) = await _apiClient.GetSubscribedPodcastsAsync(pageSize, offset).ConfigureAwait(continueOnCapturedContext: false);
		token.ThrowIfCancellationRequested();
		List<PodcastRadioInfo> currentPage = podcasts ?? new List<PodcastRadioInfo>();
		int nextOffset = checked(offset + currentPage.Count);
		bool hasMore = currentPage.Count >= pageSize && (totalCount <= 0 || nextOffset < totalCount);
		int displayTotal = Math.Max(totalCount, nextOffset);
		return (currentPage, nextOffset, displayTotal, hasMore);
	}

	private static string BuildUserPodcastsStatusText(int loadedCount, int totalCount, bool hasMore)
	{
		if (loadedCount <= 0)
		{
			return hasMore ? "正在加载收藏的播客..." : "暂无收藏的播客";
		}
		if (totalCount > loadedCount)
		{
			return $"收藏的播客：已加载 {loadedCount}/{totalCount} 个";
		}
		if (hasMore)
		{
			return $"收藏的播客：已加载 {loadedCount} 个，正在自动加载更多";
		}
		return $"收藏的播客：{loadedCount} 个";
	}

	private void StartUserPodcastsProactiveExpansion(int generation)
	{
		if (!string.Equals(_currentViewSource, "user_podcasts", StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		if (!_currentUserPodcastsHasMore || _userPodcastsProactiveLoadRunning)
		{
			return;
		}
		_userPodcastsProactiveLoadRunning = true;
		LoadRemainingUserPodcastsAsync(generation).SafeFireAndForget("user podcasts proactive load");
	}

	private async Task LoadRemainingUserPodcastsAsync(int generation)
	{
		try
		{
			while (generation == _userPodcastsProactiveLoadGeneration &&
				string.Equals(_currentViewSource, "user_podcasts", StringComparison.OrdinalIgnoreCase) &&
				_currentUserPodcastsHasMore)
			{
				int beforeOffset = _currentUserPodcastsNextOffset;
				bool loaded = await LoadMoreUserPodcastsAsync().ConfigureAwait(continueOnCapturedContext: false);
				if (!loaded ||
					generation != _userPodcastsProactiveLoadGeneration ||
					!string.Equals(_currentViewSource, "user_podcasts", StringComparison.OrdinalIgnoreCase) ||
					!_currentUserPodcastsHasMore ||
					_currentUserPodcastsNextOffset <= beforeOffset)
				{
					break;
				}
				await Task.Yield();
			}
		}
		finally
		{
			if (generation == _userPodcastsProactiveLoadGeneration)
			{
				_userPodcastsProactiveLoadRunning = false;
			}
		}
	}

	private async Task<bool> LoadMoreUserPodcastsAsync()
	{
		if (_userPodcastsLoadMoreInFlight || !_currentUserPodcastsHasMore)
		{
			return false;
		}
		_userPodcastsLoadMoreInFlight = true;
		CancellationToken viewToken = GetCurrentViewContentToken();
		int offset = Math.Max(0, _currentUserPodcastsNextOffset);
		bool applied = false;
		try
		{
			UpdateStatusBar("正在自动加载更多收藏播客...");
			var page = await FetchUserPodcastsPageAsync(offset, viewToken).ConfigureAwait(continueOnCapturedContext: false);
			List<PodcastRadioInfo> merged = CloneList(_currentPodcasts);
			HashSet<long> seen = new HashSet<long>(merged.Where((PodcastRadioInfo p) => p != null && p.Id > 0).Select((PodcastRadioInfo p) => p.Id));
			foreach (PodcastRadioInfo podcast in page.Items)
			{
				if (podcast == null)
				{
					continue;
				}
				if (podcast.Id > 0 && !seen.Add(podcast.Id))
				{
					continue;
				}
				merged.Add(podcast);
			}
			await ExecuteOnUiThreadAsync(delegate
			{
				if (!string.Equals(_currentViewSource, "user_podcasts", StringComparison.OrdinalIgnoreCase))
				{
					return;
				}
				_currentUserPodcastsNextOffset = page.NextOffset;
				_currentUserPodcastsHasMore = page.HasMore;
				_currentUserPodcastsTotalCount = Math.Max(page.TotalCount, merged.Count);
				PatchPodcasts(merged, 1, showPagination: false, hasPreviousPage: false, hasNextPage: false, pendingFocusIndex: -1, allowSelection: false);
				UpdateStatusBar(BuildUserPodcastsStatusText(_currentPodcasts.Count, _currentUserPodcastsTotalCount, _currentUserPodcastsHasMore));
				applied = true;
			}).ConfigureAwait(continueOnCapturedContext: false);
			return applied;
		}
		catch (Exception ex)
		{
			if (!TryHandleOperationCancelled(ex, "加载收藏的播客已取消"))
			{
				Debug.WriteLine($"[LoadUserPodcasts] 自动加载更多失败: {ex}");
				await ExecuteOnUiThreadAsync(delegate
				{
					if (string.Equals(_currentViewSource, "user_podcasts", StringComparison.OrdinalIgnoreCase))
					{
						UpdateStatusBar("加载更多收藏播客失败，请稍后刷新重试");
					}
				}).ConfigureAwait(continueOnCapturedContext: false);
			}
			return false;
		}
		finally
		{
			_userPodcastsLoadMoreInFlight = false;
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
		CancellationToken token = GetCurrentViewContentToken();
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
					PodcastRadioInfo detail = await _apiClient.GetPodcastRadioDetailAsync(radioId, token);
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
			offset = 0;
			(List<PodcastEpisodeInfo> Episodes, bool HasMore, int TotalCount) tuple = await FetchWithRetryUntilCancel((CancellationToken ct) => _apiClient.GetPodcastEpisodesAsync(radioId, PodcastSoundPageSize, 0, isAscending, ct), $"podcast:{radioId}:offset0:{(isAscending ? "asc" : "desc")}", token, delegate(int attempt, Exception _)
			{
				SafeInvoke(delegate
				{
					UpdateStatusBar($"加载播客节目失败，正在重试（第 {attempt} 次）...");
				});
			}).ConfigureAwait(continueOnCapturedContext: true);
			List<PodcastEpisodeInfo> episodes = tuple.Episodes;
			bool hasMore = tuple.HasMore;
			int totalCount = tuple.TotalCount;
			string accessibleName = _currentPodcast?.Name ?? "播客节目";
			string viewSource = $"podcast:{radioId}:offset0";
			if (isAscending)
			{
				viewSource += ":asc1";
			}
			if (TryHandlePaginationEmptyResult(paginationKey, Math.Max(0, offset), PodcastSoundPageSize, totalCount, episodes?.Count ?? 0, hasMore, viewSource, accessibleName))
			{
				return;
			}
				_currentPodcastHasMore = false;
				_currentPodcastEpisodeTotalCount = Math.Max(totalCount, offset + (episodes?.Count ?? 0));
				DisplayPodcastEpisodes(episodes, showPagination: false, hasNextPage: false, 1, preserveSelection: false, viewSource, accessibleName);
				UpdatePodcastSortMenuChecks();
				if (episodes == null || episodes.Count == 0)
				{
					UpdateStatusBar(accessibleName + "，暂无节目");
					return;
				}
				bool hasMoreFinal = hasMore || (totalCount > episodes.Count);
				UpdateStatusBar(BuildPodcastEpisodesAutoStatus(accessibleName, episodes.Count, totalCount, hasMoreFinal));
				if (hasMoreFinal)
				{
					LoadRemainingPodcastEpisodesAsync(radioId, isAscending, viewSource, accessibleName, episodes, episodes.Count, totalCount).SafeFireAndForget("podcast episodes proactive load");
				}
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

	private async Task LoadRemainingPodcastEpisodesAsync(long radioId, bool isAscending, string viewSource, string accessibleName, List<PodcastEpisodeInfo> seedEpisodes, int nextOffset, int totalCount)
	{
		CancellationToken token = GetCurrentViewContentToken();
		List<PodcastEpisodeInfo> allEpisodes = CloneList(seedEpisodes ?? new List<PodcastEpisodeInfo>());
		int offset = Math.Max(0, nextOffset);
		int knownTotal = Math.Max(totalCount, allEpisodes.Count);
		bool hasMore = true;
		int safety = 0;
		try
		{
			checked
			{
				while (hasMore && safety < 200)
				{
					if (!string.Equals(_currentViewSource, viewSource, StringComparison.OrdinalIgnoreCase))
					{
						return;
					}
					int fetchOffset = offset;
					(List<PodcastEpisodeInfo> Episodes, bool HasMore, int TotalCount) page = await FetchWithRetryUntilCancel((CancellationToken ct) => _apiClient.GetPodcastEpisodesAsync(radioId, PodcastSoundPageSize, fetchOffset, isAscending, ct), $"podcast:{radioId}:offset{fetchOffset}:{(isAscending ? "asc" : "desc")}", token, delegate(int attempt, Exception _)
					{
						SafeInvoke(delegate
						{
							UpdateStatusBar($"加载播客节目失败，正在重试（第 {attempt} 次）...");
						});
					}).ConfigureAwait(continueOnCapturedContext: false);
					List<PodcastEpisodeInfo> pageEpisodes = page.Episodes ?? new List<PodcastEpisodeInfo>();
					if (pageEpisodes.Count == 0)
					{
						break;
					}
					allEpisodes.AddRange(pageEpisodes);
					offset = Math.Max(fetchOffset + PodcastSoundPageSize, fetchOffset + pageEpisodes.Count);
					knownTotal = Math.Max(knownTotal, page.TotalCount);
					hasMore = page.HasMore || (knownTotal > offset);
					int loadedCount = allEpisodes.Count;
					int totalForStatus = Math.Max(knownTotal, loadedCount);
					bool currentHasMore = hasMore;
					await ExecuteOnUiThreadAsync(delegate
					{
						if (string.Equals(_currentViewSource, viewSource, StringComparison.OrdinalIgnoreCase))
						{
							_currentPodcastHasMore = false;
							_currentPodcastEpisodeTotalCount = totalForStatus;
							PatchPodcastEpisodes(allEpisodes, 1, pendingFocusIndex: -1, allowSelection: false);
							UpdateStatusBar(BuildPodcastEpisodesAutoStatus(accessibleName, loadedCount, totalForStatus, currentHasMore));
						}
					}).ConfigureAwait(continueOnCapturedContext: false);
					safety++;
					await Task.Yield();
				}
			}
		}
		catch (Exception ex)
		{
			if (!TryHandleOperationCancelled(ex, "加载播客已取消"))
			{
				Debug.WriteLine($"[Podcast] 自动加载更多节目失败: {ex}");
				await ExecuteOnUiThreadAsync(delegate
				{
					if (string.Equals(_currentViewSource, viewSource, StringComparison.OrdinalIgnoreCase))
					{
						UpdateStatusBar("加载更多播客节目失败，请稍后刷新重试");
					}
				}).ConfigureAwait(continueOnCapturedContext: false);
			}
		}
	}

	private static string BuildPodcastEpisodesAutoStatus(string accessibleName, int loadedCount, int totalCount, bool hasMore)
	{
		int safeLoaded = Math.Max(0, loadedCount);
		int safeTotal = Math.Max(0, totalCount);
		if (safeLoaded <= 0)
		{
			return accessibleName + "，暂无节目";
		}
		if (hasMore)
		{
			if (safeTotal > safeLoaded)
			{
				return $"{accessibleName}：已加载 {safeLoaded} / {safeTotal} 个节目，正在自动加载更多";
			}
			return $"{accessibleName}：已加载 {safeLoaded} 个节目，正在自动加载更多";
		}
		return $"{accessibleName}：{safeLoaded} 个节目";
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
