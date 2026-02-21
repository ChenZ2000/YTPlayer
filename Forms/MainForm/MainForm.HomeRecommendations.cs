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
	private async Task<(List<SongInfo> Songs, List<PlaylistInfo> Playlists)> FetchDailyRecommendBundleAsync(CancellationToken token, bool allowCache = true)
	{
		if (!IsUserLoggedIn())
		{
			return (Songs: new List<SongInfo>(), Playlists: new List<PlaylistInfo>());
		}
		if (allowCache && _dailyRecommendSongsCache != null && _dailyRecommendPlaylistsCache != null && IsRecommendationCacheFresh(_dailyRecommendSongsFetchedUtc) && IsRecommendationCacheFresh(_dailyRecommendPlaylistsFetchedUtc))
		{
			return (Songs: _dailyRecommendSongsCache, Playlists: _dailyRecommendPlaylistsCache);
		}
                Task<List<SongInfo>> songsTask = _apiClient.GetDailyRecommendSongsAsync();
                Task<List<PlaylistInfo>> playlistsTask = _apiClient.GetDailyRecommendPlaylistsAsync();
                await Task.WhenAll(songsTask, playlistsTask).ConfigureAwait(continueOnCapturedContext: false);
                token.ThrowIfCancellationRequested();
                List<SongInfo> songs = await songsTask.ConfigureAwait(false);
                List<PlaylistInfo> playlists = await playlistsTask.ConfigureAwait(false);
                _dailyRecommendSongsCache = CloneList(songs);
                _dailyRecommendPlaylistsCache = CloneList(playlists);
		_dailyRecommendSongsFetchedUtc = (_dailyRecommendPlaylistsFetchedUtc = DateTime.UtcNow);
		_dailyRecommendCacheFetchedUtc = _dailyRecommendSongsFetchedUtc;
		return (Songs: _dailyRecommendSongsCache, Playlists: _dailyRecommendPlaylistsCache);
	}

	private async Task<(List<PlaylistInfo> Playlists, List<SongInfo> Songs)> FetchPersonalizedBundleAsync(CancellationToken token, bool allowCache = true)
	{
		if (allowCache && _personalizedPlaylistsCache != null && _personalizedNewSongsCache != null && IsRecommendationCacheFresh(_personalizedCacheFetchedUtc))
		{
			return (Playlists: _personalizedPlaylistsCache, Songs: _personalizedNewSongsCache);
		}
                Task<List<PlaylistInfo>> playlistsTask = _apiClient.GetPersonalizedPlaylistsAsync();
                Task<List<SongInfo>> songsTask = _apiClient.GetPersonalizedNewSongsAsync(20);
                await Task.WhenAll(playlistsTask, songsTask).ConfigureAwait(continueOnCapturedContext: false);
                token.ThrowIfCancellationRequested();
                List<PlaylistInfo> playlists = await playlistsTask.ConfigureAwait(false);
                List<SongInfo> songs = await songsTask.ConfigureAwait(false);
                _personalizedPlaylistsCache = CloneList(playlists);
                _personalizedNewSongsCache = CloneList(songs);
		_personalizedCacheFetchedUtc = DateTime.UtcNow;
		return (Playlists: _personalizedPlaylistsCache, Songs: _personalizedNewSongsCache);
	}

	private async Task LoadDailyRecommend(int pendingFocusIndex = -1)
	{
		try
		{
			int requestFocusIndex = (pendingFocusIndex >= 0) ? pendingFocusIndex : 0;
			ViewLoadRequest request = new ViewLoadRequest("daily_recommend", "每日推荐", "正在加载每日推荐...", cancelActiveNavigation: true, pendingFocusIndex: requestFocusIndex);
			ViewLoadResult<(List<ListItemInfo> Items, string StatusText)> loadResult = await RunViewLoadAsync(request, async delegate(CancellationToken viewToken)
			{
				(List<SongInfo> Songs, List<PlaylistInfo> Playlists) bundle = await FetchDailyRecommendBundleAsync(viewToken);
				viewToken.ThrowIfCancellationRequested();
				int songCount = bundle.Songs?.Count ?? 0;
				int playlistCount = bundle.Playlists?.Count ?? 0;
				_homeCachedDailyRecommendSongCount = songCount;
				_homeCachedDailyRecommendPlaylistCount = playlistCount;
				List<ListItemInfo> items = new List<ListItemInfo>
				{
					new ListItemInfo
					{
						Type = ListItemType.Category,
						CategoryId = "daily_recommend_songs",
						CategoryName = "每日推荐歌曲",
						ItemCount = ((songCount > 0) ? new int?(songCount) : ((int?)null)),
						ItemUnit = ((songCount > 0) ? "首" : null)
					},
					new ListItemInfo
					{
						Type = ListItemType.Category,
						CategoryId = "daily_recommend_playlists",
						CategoryName = "每日推荐歌单",
						ItemCount = ((playlistCount > 0) ? new int?(playlistCount) : ((int?)null)),
						ItemUnit = ((playlistCount > 0) ? "个" : null)
					}
				};
				string status = ((checked(songCount + playlistCount) > 0) ? $"每日推荐：歌曲 {songCount} 首 / 歌单 {playlistCount} 个" : "每日推荐");
				return (Items: items, StatusText: status);
			}, "加载每日推荐已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				(List<ListItemInfo> Items, string StatusText) data = loadResult.Value;
				DisplayListItems(data.Items, request.ViewSource, request.AccessibleName, preserveSelection: true);
				UpdateStatusBar(data.StatusText);
				FocusListAfterEnrich(request.PendingFocusIndex);
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Exception ex3 = ex2;
			Debug.WriteLine($"[LoadDailyRecommend] 异常: {ex3}");
			throw;
		}
	}

	private async Task LoadPersonalized(int pendingFocusIndex = -1)
	{
		try
		{
			int requestFocusIndex = (pendingFocusIndex >= 0) ? pendingFocusIndex : 0;
			ViewLoadRequest request = new ViewLoadRequest("personalized", "为您推荐", "正在加载为您推荐...", cancelActiveNavigation: true, pendingFocusIndex: requestFocusIndex);
			ViewLoadResult<(List<ListItemInfo> Items, string StatusText)> loadResult = await RunViewLoadAsync(request, async delegate(CancellationToken viewToken)
			{
				(List<PlaylistInfo> Playlists, List<SongInfo> Songs) bundle = await FetchPersonalizedBundleAsync(viewToken);
				viewToken.ThrowIfCancellationRequested();
				int playlistCount = bundle.Playlists?.Count ?? 0;
				int songCount = bundle.Songs?.Count ?? 0;
				_homeCachedPersonalizedPlaylistCount = playlistCount;
				_homeCachedPersonalizedSongCount = songCount;
				List<ListItemInfo> items = new List<ListItemInfo>
				{
					new ListItemInfo
					{
						Type = ListItemType.Category,
						CategoryId = "personalized_newsongs",
						CategoryName = "推荐新歌",
						ItemCount = ((songCount > 0) ? new int?(songCount) : ((int?)null)),
						ItemUnit = ((songCount > 0) ? "首" : null)
					},
					new ListItemInfo
					{
						Type = ListItemType.Category,
						CategoryId = "personalized_playlists",
						CategoryName = "推荐歌单",
						ItemCount = ((playlistCount > 0) ? new int?(playlistCount) : ((int?)null)),
						ItemUnit = ((playlistCount > 0) ? "个" : null)
					}
				};
				string status = ((checked(playlistCount + songCount) > 0) ? $"为您推荐：歌单 {playlistCount} 个 / 歌曲 {songCount} 首" : "为您推荐");
				return (Items: items, StatusText: status);
			}, "加载个性化推荐已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				(List<ListItemInfo> Items, string StatusText) data = loadResult.Value;
				DisplayListItems(data.Items, request.ViewSource, request.AccessibleName, preserveSelection: true);
				UpdateStatusBar(data.StatusText);
				FocusListAfterEnrich(request.PendingFocusIndex);
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Exception ex3 = ex2;
			Debug.WriteLine($"[LoadPersonalized] 异常: {ex3}");
			throw;
		}
	}

	private async Task LoadPersonalFm(int pendingFocusIndex = -1)
	{
		if (!IsUserLoggedIn())
		{
			MessageBox.Show("请先登录网易云账号以使用私人 FM。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			await LoadHomePageAsync(skipSave: true, showErrorDialog: false);
			return;
		}
		try
		{
			int requestFocusIndex = ResolvePersonalFmFocusIndexForEntry(pendingFocusIndex);
			ViewLoadRequest request = new ViewLoadRequest(PersonalFmCategoryId, PersonalFmAccessibleName, "正在加载私人 FM...", cancelActiveNavigation: true, pendingFocusIndex: requestFocusIndex);
			ViewLoadResult<List<SongInfo>> loadResult = await RunViewLoadAsync(request, async delegate(CancellationToken viewToken)
			{
				viewToken.ThrowIfCancellationRequested();
				List<SongInfo> cachedSongs = GetPersonalFmCachedSongsSnapshot();
				if (cachedSongs.Count > 0)
				{
					return cachedSongs;
				}
				SongInfo firstSong = await FetchNextPersonalFmSongAsync(viewToken, allowDuplicateFallback: true).ConfigureAwait(continueOnCapturedContext: false);
				if (firstSong == null)
				{
					return new List<SongInfo>();
				}
				List<SongInfo> initialSongs = new List<SongInfo> { firstSong };
				ResetPersonalFmCache(initialSongs, focusedIndex: 0);
				return CloneList(initialSongs);
			}, "加载私人 FM 已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (loadResult.IsCanceled)
			{
				return;
			}
			List<SongInfo> songs = CloneList(loadResult.Value ?? new List<SongInfo>());
			ApplyViewSourceToSongs(songs, PersonalFmCategoryId);
			if (songs.Count == 0)
			{
				ShowListRetryPlaceholderCore(PersonalFmCategoryId, PersonalFmAccessibleName, PersonalFmAccessibleName, announceHeader: true, suppressFocus: false);
				UpdateStatusBar("暂无私人 FM 内容，刷新重试");
				return;
			}
			int effectiveFocusIndex = ResolvePersonalFmFocusIndexForEntry(request.PendingFocusIndex, songs.Count);
			RequestListFocus(PersonalFmCategoryId, effectiveFocusIndex);
			DisplaySongs(songs, showPagination: false, hasNextPage: false, 1, preserveSelection: false, PersonalFmCategoryId, PersonalFmAccessibleName, skipAvailabilityCheck: false, announceHeader: true, suppressFocus: false);
			ResetPersonalFmCache(songs, effectiveFocusIndex);
			UpdateStatusBar($"私人 FM，共 {songs.Count} 首");
			FocusListAfterEnrich(effectiveFocusIndex);
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Exception ex3 = ex2;
			if (TryHandleOperationCancelled(ex3, "加载私人 FM 已取消"))
			{
				return;
			}
			Debug.WriteLine($"[LoadPersonalFm] 异常: {ex3}");
			MessageBox.Show("加载私人 FM 失败: " + ex3.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("加载私人 FM 失败");
			ShowListRetryPlaceholderCore(PersonalFmCategoryId, resultListView?.AccessibleName, PersonalFmAccessibleName, announceHeader: true, suppressFocus: false);
		}
	}

	private async Task LoadToplist(int pendingFocusIndex = 0, int offset = 0, bool skipSave = false)
	{
		try
		{
			List<PlaylistInfo> cached;
			lock (_toplistCacheLock)
			{
				cached = (_toplistCache.Count > 0) ? new List<PlaylistInfo>(_toplistCache) : null;
			}
			if (cached != null && cached.Count > 0)
			{
				bool offsetClamped;
				int normalizedOffset = NormalizeOffsetWithTotal(cached.Count, ToplistPageSize, offset, out offsetClamped);
				if (offsetClamped)
				{
					int page = (normalizedOffset / ToplistPageSize) + 1;
					UpdateStatusBar($"页码过大，已跳到第 {page} 页");
				}
				offset = normalizedOffset;
			}
			if (!skipSave)
			{
				SaveNavigationState();
			}
			string viewSource = BuildToplistViewSource(offset);
			int page2 = (Math.Max(0, offset) / ToplistPageSize) + 1;
			ViewLoadRequest request = new ViewLoadRequest(viewSource, "官方排行榜", "正在加载官方排行榜...", cancelActiveNavigation: true, pendingFocusIndex);
			ViewLoadResult<SearchViewData<PlaylistInfo>> loadResult = await RunViewLoadAsync(request, delegate
			{
				using (WorkScopes.BeginSkeleton("Toplist", request.ViewSource))
				{
					return Task.FromResult(BuildSearchSkeletonViewData("官方排行榜", "歌单", page2, offset + 1, _currentPlaylists));
				}
			}, "加载排行榜已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				SearchViewData<PlaylistInfo> skeleton = loadResult.Value;
				if (HasSkeletonItems(skeleton.Items))
				{
					DisplayPlaylists(skeleton.Items, preserveSelection: false, request.ViewSource, request.AccessibleName, skeleton.StartIndex, showPagination: false, hasNextPage: false, announceHeader: false, suppressFocus: true);
				}
				UpdateStatusBar(request.LoadingText);
				EnrichToplistAsync(request.ViewSource, request.AccessibleName, request.PendingFocusIndex, offset);
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Exception ex3 = ex2;
			if (TryHandleOperationCancelled(ex3, "加载排行榜已取消"))
			{
				return;
			}
			Debug.WriteLine($"[LoadToplist] 异常: {ex3}");
			throw;
		}
	}

	private async Task EnrichToplistAsync(string viewSource, string accessibleName, int pendingFocusIndex, int offset)
	{
		CancellationToken viewToken = GetCurrentViewContentToken();
		using (WorkScopes.BeginEnrichment("Toplist", viewSource))
		{
			try
			{
				List<PlaylistInfo> playlists;
				lock (_toplistCacheLock)
				{
					playlists = (_toplistCache.Count > 0) ? new List<PlaylistInfo>(_toplistCache) : null;
				}
				if (playlists == null)
				{
					playlists = (await FetchWithRetryUntilCancel((CancellationToken ct) => _apiClient.GetToplistAsync(), "toplist", viewToken, delegate(int attempt, Exception _)
					{
						SafeInvoke(delegate
						{
							UpdateStatusBar($"加载排行榜失败，正在重试（第 {attempt} 次）...");
						});
					}).ConfigureAwait(continueOnCapturedContext: true)) ?? new List<PlaylistInfo>();
					lock (_toplistCacheLock)
					{
						_toplistCache.Clear();
						_toplistCache.AddRange(playlists);
					}
				}
				int totalCount = playlists.Count;
				bool offsetClamped;
				int normalizedOffset = NormalizeOffsetWithTotal(totalCount, ToplistPageSize, offset, out offsetClamped);
				if (offsetClamped)
				{
					int page = (normalizedOffset / ToplistPageSize) + 1;
					UpdateStatusBar($"页码过大，已跳到第 {page} 页");
				}
				List<PlaylistInfo> pageItems = playlists.Skip(normalizedOffset).Take(ToplistPageSize).ToList();
				bool hasMore = normalizedOffset + ToplistPageSize < totalCount;
				string paginationKey = BuildToplistPaginationKey();
				if (TryHandlePaginationEmptyResult(paginationKey, normalizedOffset, ToplistPageSize, totalCount, pageItems.Count, hasMore, viewSource, accessibleName))
				{
					return;
				}
				if (viewToken.IsCancellationRequested)
				{
					return;
				}
				await ExecuteOnUiThreadAsync(delegate
				{
					if (!ShouldAbortViewRender(viewToken, "加载排行榜"))
					{
						_currentPlaylists = CloneList(pageItems);
						_currentToplistHasMore = hasMore;
						_currentToplistTotalCount = Math.Max(totalCount, normalizedOffset + _currentPlaylists.Count);
						_currentToplistOffset = normalizedOffset;
						bool showPagination = normalizedOffset > 0 || hasMore;
						PatchPlaylists(_currentPlaylists, normalizedOffset + 1, showPagination: showPagination, hasPreviousPage: normalizedOffset > 0, hasNextPage: hasMore, pendingFocusIndex);
						FocusListAfterEnrich((pendingFocusIndex >= 0) ? pendingFocusIndex : (-1));
						if (_currentPlaylists.Count == 0)
						{
							MessageBox.Show("暂无排行榜数据。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
							UpdateStatusBar("暂无排行榜");
						}
						else if (totalCount > 0)
						{
							int page = (normalizedOffset / ToplistPageSize) + 1;
							int maxPage = Math.Max(1, (int)Math.Ceiling((double)totalCount / ToplistPageSize));
							UpdateStatusBar($"第 {page}/{maxPage} 页，本页 {_currentPlaylists.Count} 个 / 总 {totalCount} 个");
						}
						else
						{
							UpdateStatusBar($"加载完成，本页 {_currentPlaylists.Count} 个排行榜");
						}
					}
				}).ConfigureAwait(continueOnCapturedContext: false);
				await EnsureLibraryStateFreshAsync(LibraryEntityType.Playlists);
			}
			catch (Exception ex)
			{
				Exception ex2 = ex;
				if (TryHandleOperationCancelled(ex2, "加载排行榜已取消"))
				{
					return;
				}
				Debug.WriteLine($"[LoadToplist] 异常: {ex2}");
				await ExecuteOnUiThreadAsync(delegate
				{
					if (!ShouldAbortViewRender(viewToken, "加载排行榜"))
					{
						MessageBox.Show("加载排行榜失败: " + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
						UpdateStatusBar("加载排行榜失败");
					}
				}).ConfigureAwait(continueOnCapturedContext: false);
			}
		}
	}

	private async Task LoadDailyRecommendSongs(int pendingFocusIndex = -1)
	{
		try
		{
			int requestFocusIndex = (pendingFocusIndex >= 0) ? pendingFocusIndex : 0;
			ViewLoadRequest request = new ViewLoadRequest("daily_recommend_songs", "每日推荐歌曲", "正在加载每日推荐歌曲...", cancelActiveNavigation: true, pendingFocusIndex: requestFocusIndex);
			if (!(await RunViewLoadAsync(request, delegate
			{
				using (WorkScopes.BeginSkeleton("DailyRecommendSongs", request.ViewSource))
				{
					return Task.FromResult(result: true);
				}
			}, "加载每日推荐歌曲已取消").ConfigureAwait(continueOnCapturedContext: true)).IsCanceled)
			{
				List<SongInfo> cachedSongs = (IsRecommendationCacheFresh(_dailyRecommendSongsFetchedUtc) ? _dailyRecommendSongsCache : null);
				if (cachedSongs != null && cachedSongs.Count > 0)
				{
					DisplaySongs(cachedSongs, showPagination: false, hasNextPage: false, 1, preserveSelection: true, request.ViewSource, request.AccessibleName, skipAvailabilityCheck: true);
					UpdateStatusBar($"每日推荐歌曲（缓存）共 {cachedSongs.Count} 首，正在刷新...");
					FocusListAfterEnrich(request.PendingFocusIndex);
				}
				else
				{
					UpdateStatusBar(request.LoadingText);
				}
				EnrichDailyRecommendSongsAsync(request.ViewSource, request.AccessibleName, request.PendingFocusIndex);
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Exception ex3 = ex2;
			if (TryHandleOperationCancelled(ex3, "加载每日推荐歌曲已取消"))
			{
				return;
			}
			Debug.WriteLine($"[LoadDailyRecommendSongs] 异常: {ex3}");
			throw;
		}
	}

	private async Task EnrichDailyRecommendSongsAsync(string viewSource, string accessibleName, int pendingFocusIndex)
	{
		CancellationToken viewToken = GetCurrentViewContentToken();
		using (WorkScopes.BeginEnrichment("DailyRecommendSongs", viewSource))
		{
			try
			{
				if (_dailyRecommendSongsCache != null && _dailyRecommendSongsCache.Count > 0 && IsRecommendationCacheFresh(_dailyRecommendSongsFetchedUtc))
				{
					List<SongInfo> cachedSongs = _dailyRecommendSongsCache;
					await ExecuteOnUiThreadAsync(delegate
					{
						if (!ShouldAbortViewRender(viewToken, "每日推荐歌曲"))
						{
							DisplaySongs(cachedSongs, showPagination: false, hasNextPage: false, 1, preserveSelection: true, viewSource, accessibleName, skipAvailabilityCheck: false, announceHeader: false);
							FocusListAfterEnrich(pendingFocusIndex);
							UpdateStatusBar((cachedSongs.Count == 0) ? "暂无每日推荐歌曲" : $"每日推荐歌曲（缓存）共 {cachedSongs.Count} 首");
						}
					}).ConfigureAwait(continueOnCapturedContext: false);
					return;
				}
				List<SongInfo> songsResult = (await FetchWithRetryUntilCancel((CancellationToken ct) => _apiClient.GetDailyRecommendSongsAsync(), "daily_recommend:songs", viewToken, delegate(int attempt, Exception _)
				{
					SafeInvoke(delegate
					{
						UpdateStatusBar($"加载每日推荐歌曲失败，正在重试（第 {attempt} 次）...");
					});
				}).ConfigureAwait(continueOnCapturedContext: true)) ?? new List<SongInfo>();
				_dailyRecommendSongsCache = CloneList(songsResult);
				_dailyRecommendSongsFetchedUtc = DateTime.UtcNow;
				if (viewToken.IsCancellationRequested)
				{
					return;
				}
				await ExecuteOnUiThreadAsync(delegate
				{
					if (!ShouldAbortViewRender(viewToken, "每日推荐歌曲"))
					{
						DisplaySongs(songsResult, showPagination: false, hasNextPage: false, 1, preserveSelection: true, viewSource, accessibleName, skipAvailabilityCheck: false, announceHeader: false);
						FocusListAfterEnrich(pendingFocusIndex);
						if (songsResult.Count == 0)
						{
							MessageBox.Show("今日暂无推荐歌曲。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
							UpdateStatusBar("暂无每日推荐歌曲");
						}
						else
						{
							UpdateStatusBar($"加载完成，共 {songsResult.Count} 首歌曲");
						}
					}
				}).ConfigureAwait(continueOnCapturedContext: false);
				await EnsureLibraryStateFreshAsync(LibraryEntityType.Songs);
			}
			catch (Exception ex)
			{
				Exception ex2 = ex;
				if (TryHandleOperationCancelled(ex2, "加载每日推荐歌曲已取消"))
				{
					return;
				}
				Debug.WriteLine($"[LoadDailyRecommendSongs] 异常: {ex2}");
				await ExecuteOnUiThreadAsync(delegate
				{
					if (!ShouldAbortViewRender(viewToken, "每日推荐歌曲"))
					{
						MessageBox.Show("加载每日推荐歌曲失败: " + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
						UpdateStatusBar("加载每日推荐歌曲失败");
					}
				}).ConfigureAwait(continueOnCapturedContext: false);
			}
		}
	}

	private async Task LoadDailyRecommendPlaylists(int pendingFocusIndex = -1)
	{
		try
		{
			int requestFocusIndex = (pendingFocusIndex >= 0) ? pendingFocusIndex : 0;
			ViewLoadRequest request = new ViewLoadRequest("daily_recommend_playlists", "每日推荐歌单", "正在加载每日推荐歌单...", cancelActiveNavigation: true, pendingFocusIndex: requestFocusIndex);
			if (!(await RunViewLoadAsync(request, delegate
			{
				using (WorkScopes.BeginSkeleton("DailyRecommendPlaylists", request.ViewSource))
				{
					return Task.FromResult(result: true);
				}
			}, "加载每日推荐歌单已取消").ConfigureAwait(continueOnCapturedContext: true)).IsCanceled)
			{
				List<PlaylistInfo> cachedPlaylists = (IsRecommendationCacheFresh(_dailyRecommendPlaylistsFetchedUtc) ? _dailyRecommendPlaylistsCache : null);
				if (cachedPlaylists != null && cachedPlaylists.Count > 0)
				{
					DisplayPlaylists(cachedPlaylists, preserveSelection: true, request.ViewSource, request.AccessibleName, 1, showPagination: false, hasNextPage: false, announceHeader: false);
					UpdateStatusBar($"每日推荐歌单（缓存）共 {cachedPlaylists.Count} 个，正在刷新...");
					FocusListAfterEnrich(request.PendingFocusIndex);
				}
				else
				{
					UpdateStatusBar(request.LoadingText);
				}
				EnrichDailyRecommendPlaylistsAsync(request.ViewSource, request.AccessibleName, request.PendingFocusIndex);
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Exception ex3 = ex2;
			if (TryHandleOperationCancelled(ex3, "加载每日推荐歌单已取消"))
			{
				return;
			}
			Debug.WriteLine($"[LoadDailyRecommendPlaylists] 异常: {ex3}");
			throw;
		}
	}

	private async Task EnrichDailyRecommendPlaylistsAsync(string viewSource, string accessibleName, int pendingFocusIndex)
	{
		CancellationToken viewToken = GetCurrentViewContentToken();
		using (WorkScopes.BeginEnrichment("DailyRecommendPlaylists", viewSource))
		{
			try
			{
				if (_dailyRecommendPlaylistsCache != null && _dailyRecommendPlaylistsCache.Count > 0 && IsRecommendationCacheFresh(_dailyRecommendPlaylistsFetchedUtc))
				{
					List<PlaylistInfo> cachedPlaylists = _dailyRecommendPlaylistsCache;
					await ExecuteOnUiThreadAsync(delegate
					{
						if (!ShouldAbortViewRender(viewToken, "每日推荐歌单"))
						{
							DisplayPlaylists(cachedPlaylists, preserveSelection: true, viewSource, accessibleName, 1, showPagination: false, hasNextPage: false, announceHeader: false);
							FocusListAfterEnrich(pendingFocusIndex);
							UpdateStatusBar((cachedPlaylists.Count == 0) ? "暂无每日推荐歌单" : $"每日推荐歌单（缓存）共 {cachedPlaylists.Count} 个");
						}
					}).ConfigureAwait(continueOnCapturedContext: false);
					return;
				}
				List<PlaylistInfo> playlistsResult = (await FetchWithRetryUntilCancel((CancellationToken ct) => _apiClient.GetDailyRecommendPlaylistsAsync(), "daily_recommend:playlists", viewToken, delegate(int attempt, Exception _)
				{
					SafeInvoke(delegate
					{
						UpdateStatusBar($"加载每日推荐歌单失败，正在重试（第 {attempt} 次）...");
					});
				}).ConfigureAwait(continueOnCapturedContext: true)) ?? new List<PlaylistInfo>();
				_dailyRecommendPlaylistsCache = CloneList(playlistsResult);
				_dailyRecommendPlaylistsFetchedUtc = DateTime.UtcNow;
				if (viewToken.IsCancellationRequested)
				{
					return;
				}
				await ExecuteOnUiThreadAsync(delegate
				{
					if (!ShouldAbortViewRender(viewToken, "每日推荐歌单"))
					{
						DisplayPlaylists(playlistsResult, preserveSelection: true, viewSource, accessibleName, 1, showPagination: false, hasNextPage: false, announceHeader: false);
						FocusListAfterEnrich(pendingFocusIndex);
						if (playlistsResult.Count == 0)
						{
							MessageBox.Show("今日暂无推荐歌单。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
							UpdateStatusBar("暂无每日推荐歌单");
						}
						else
						{
							UpdateStatusBar($"加载完成，共 {playlistsResult.Count} 个歌单");
						}
					}
				}).ConfigureAwait(continueOnCapturedContext: false);
				await EnsureLibraryStateFreshAsync(LibraryEntityType.Playlists);
			}
			catch (Exception ex)
			{
				Exception ex2 = ex;
				if (TryHandleOperationCancelled(ex2, "加载每日推荐歌单已取消"))
				{
					return;
				}
				Debug.WriteLine($"[LoadDailyRecommendPlaylists] 异常: {ex2}");
				await ExecuteOnUiThreadAsync(delegate
				{
					if (!ShouldAbortViewRender(viewToken, "每日推荐歌单"))
					{
						MessageBox.Show("加载每日推荐歌单失败: " + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
						UpdateStatusBar("加载每日推荐歌单失败");
					}
				}).ConfigureAwait(continueOnCapturedContext: false);
			}
		}
	}

	private async Task LoadPersonalizedPlaylists(int pendingFocusIndex = -1)
	{
		try
		{
			int requestFocusIndex = (pendingFocusIndex >= 0) ? pendingFocusIndex : 0;
			ViewLoadRequest request = new ViewLoadRequest("personalized_playlists", "推荐歌单", "正在加载推荐歌单...", cancelActiveNavigation: true, pendingFocusIndex: requestFocusIndex);
			if (!(await RunViewLoadAsync(request, delegate
			{
				using (WorkScopes.BeginSkeleton("PersonalizedPlaylists", request.ViewSource))
				{
					return Task.FromResult(result: true);
				}
			}, "加载推荐歌单已取消").ConfigureAwait(continueOnCapturedContext: true)).IsCanceled)
			{
				List<PlaylistInfo> cachedPlaylists = (IsRecommendationCacheFresh(_personalizedCacheFetchedUtc) ? _personalizedPlaylistsCache : null);
				if (cachedPlaylists != null && cachedPlaylists.Count > 0)
				{
					DisplayPlaylists(cachedPlaylists, preserveSelection: true, request.ViewSource, request.AccessibleName, 1, showPagination: false, hasNextPage: false, announceHeader: false);
					UpdateStatusBar($"推荐歌单（缓存）共 {cachedPlaylists.Count} 个，正在刷新...");
					FocusListAfterEnrich(request.PendingFocusIndex);
				}
				else
				{
					UpdateStatusBar(request.LoadingText);
				}
				EnrichPersonalizedPlaylistsAsync(request.ViewSource, request.AccessibleName, request.PendingFocusIndex);
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Exception ex3 = ex2;
			if (TryHandleOperationCancelled(ex3, "加载推荐歌单已取消"))
			{
				return;
			}
			Debug.WriteLine($"[LoadPersonalizedPlaylists] 异常: {ex3}");
			throw;
		}
	}

	private async Task EnrichPersonalizedPlaylistsAsync(string viewSource, string accessibleName, int pendingFocusIndex)
	{
		CancellationToken viewToken = GetCurrentViewContentToken();
		using (WorkScopes.BeginEnrichment("PersonalizedPlaylists", viewSource))
		{
			try
			{
				if (_personalizedPlaylistsCache != null && _personalizedPlaylistsCache.Count > 0 && IsRecommendationCacheFresh(_personalizedCacheFetchedUtc))
				{
					List<PlaylistInfo> cachedPlaylists = _personalizedPlaylistsCache;
					await ExecuteOnUiThreadAsync(delegate
					{
						if (!ShouldAbortViewRender(viewToken, "推荐歌单"))
						{
							DisplayPlaylists(cachedPlaylists, preserveSelection: true, viewSource, accessibleName, 1, showPagination: false, hasNextPage: false, announceHeader: false);
							FocusListAfterEnrich(pendingFocusIndex);
							UpdateStatusBar((cachedPlaylists.Count == 0) ? "暂无推荐歌单" : $"推荐歌单（缓存）共 {cachedPlaylists.Count} 个");
						}
					}).ConfigureAwait(continueOnCapturedContext: false);
					return;
				}
				List<PlaylistInfo> playlistsResult = (await FetchWithRetryUntilCancel((CancellationToken ct) => _apiClient.GetPersonalizedPlaylistsAsync(), "personalized:playlists", viewToken, delegate(int attempt, Exception _)
				{
					SafeInvoke(delegate
					{
						UpdateStatusBar($"加载推荐歌单失败，正在重试（第 {attempt} 次）...");
					});
				}).ConfigureAwait(continueOnCapturedContext: true)) ?? new List<PlaylistInfo>();
				_personalizedPlaylistsCache = CloneList(playlistsResult);
				_personalizedCacheFetchedUtc = DateTime.UtcNow;
				if (viewToken.IsCancellationRequested)
				{
					return;
				}
				await ExecuteOnUiThreadAsync(delegate
				{
					if (!ShouldAbortViewRender(viewToken, "推荐歌单"))
					{
						DisplayPlaylists(playlistsResult, preserveSelection: true, viewSource, accessibleName, 1, showPagination: false, hasNextPage: false, announceHeader: false);
						FocusListAfterEnrich(pendingFocusIndex);
						if (playlistsResult.Count == 0)
						{
							MessageBox.Show("暂无推荐歌单。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
							UpdateStatusBar("暂无推荐歌单");
						}
						else
						{
							UpdateStatusBar($"加载完成，共 {playlistsResult.Count} 个歌单");
						}
					}
				}).ConfigureAwait(continueOnCapturedContext: false);
				await EnsureLibraryStateFreshAsync(LibraryEntityType.Playlists);
			}
			catch (Exception ex)
			{
				Exception ex2 = ex;
				if (TryHandleOperationCancelled(ex2, "加载推荐歌单已取消"))
				{
					return;
				}
				Debug.WriteLine($"[LoadPersonalizedPlaylists] 异常: {ex2}");
				await ExecuteOnUiThreadAsync(delegate
				{
					if (!ShouldAbortViewRender(viewToken, "推荐歌单"))
					{
						MessageBox.Show("加载推荐歌单失败: " + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
						UpdateStatusBar("加载推荐歌单失败");
					}
				}).ConfigureAwait(continueOnCapturedContext: false);
			}
		}
	}

	private async Task LoadPersonalizedNewSongs(int pendingFocusIndex = -1)
	{
		try
		{
			int requestFocusIndex = (pendingFocusIndex >= 0) ? pendingFocusIndex : 0;
			ViewLoadRequest request = new ViewLoadRequest("personalized_newsongs", "推荐新歌", "正在加载推荐新歌...", cancelActiveNavigation: true, pendingFocusIndex: requestFocusIndex);
			if (!(await RunViewLoadAsync(request, delegate
			{
				using (WorkScopes.BeginSkeleton("PersonalizedNewSongs", request.ViewSource))
				{
					return Task.FromResult(result: true);
				}
			}, "加载推荐新歌已取消").ConfigureAwait(continueOnCapturedContext: true)).IsCanceled)
			{
				List<SongInfo> cachedSongs = (IsRecommendationCacheFresh(_personalizedCacheFetchedUtc) ? _personalizedNewSongsCache : null);
				if (cachedSongs != null && cachedSongs.Count > 0)
				{
					DisplaySongs(cachedSongs, showPagination: false, hasNextPage: false, 1, preserveSelection: true, request.ViewSource, request.AccessibleName, skipAvailabilityCheck: false, announceHeader: false);
					UpdateStatusBar($"推荐新歌（缓存）共 {cachedSongs.Count} 首，正在刷新...");
					FocusListAfterEnrich(request.PendingFocusIndex);
				}
				else
				{
					UpdateStatusBar(request.LoadingText);
				}
				EnrichPersonalizedNewSongsAsync(request.ViewSource, request.AccessibleName, request.PendingFocusIndex);
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Exception ex3 = ex2;
			if (TryHandleOperationCancelled(ex3, "加载推荐新歌已取消"))
			{
				return;
			}
			Debug.WriteLine($"[LoadPersonalizedNewSongs] 异常: {ex3}");
			throw;
		}
	}

	private async Task EnrichPersonalizedNewSongsAsync(string viewSource, string accessibleName, int pendingFocusIndex)
	{
		CancellationToken viewToken = GetCurrentViewContentToken();
		using (WorkScopes.BeginEnrichment("PersonalizedNewSongs", viewSource))
		{
			try
			{
				if (_personalizedNewSongsCache != null && _personalizedNewSongsCache.Count > 0 && IsRecommendationCacheFresh(_personalizedCacheFetchedUtc))
				{
					List<SongInfo> cachedSongs = _personalizedNewSongsCache;
					await ExecuteOnUiThreadAsync(delegate
					{
						if (!ShouldAbortViewRender(viewToken, "推荐新歌"))
						{
							DisplaySongs(cachedSongs, showPagination: false, hasNextPage: false, 1, preserveSelection: true, viewSource, accessibleName, skipAvailabilityCheck: false, announceHeader: false);
							FocusListAfterEnrich(pendingFocusIndex);
							UpdateStatusBar((cachedSongs.Count == 0) ? "暂无推荐新歌" : $"推荐新歌（缓存）共 {cachedSongs.Count} 首");
						}
					}).ConfigureAwait(continueOnCapturedContext: false);
					return;
				}
				List<SongInfo> songsResult = (await FetchWithRetryUntilCancel((CancellationToken ct) => _apiClient.GetPersonalizedNewSongsAsync(20), "personalized:newsongs", viewToken, delegate(int attempt, Exception _)
				{
					SafeInvoke(delegate
					{
						UpdateStatusBar($"加载推荐新歌失败，正在重试（第 {attempt} 次）...");
					});
				}).ConfigureAwait(continueOnCapturedContext: true)) ?? new List<SongInfo>();
				_personalizedNewSongsCache = CloneList(songsResult);
				_personalizedCacheFetchedUtc = DateTime.UtcNow;
				if (viewToken.IsCancellationRequested)
				{
					return;
				}
				await ExecuteOnUiThreadAsync(delegate
				{
					if (!ShouldAbortViewRender(viewToken, "推荐新歌"))
					{
						DisplaySongs(songsResult, showPagination: false, hasNextPage: false, 1, preserveSelection: true, viewSource, accessibleName, skipAvailabilityCheck: false, announceHeader: false);
						FocusListAfterEnrich(pendingFocusIndex);
						if (songsResult.Count == 0)
						{
							MessageBox.Show("暂无推荐新歌。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
							UpdateStatusBar("暂无推荐新歌");
						}
						else
						{
							UpdateStatusBar($"加载完成，共 {songsResult.Count} 首歌曲");
						}
					}
				}).ConfigureAwait(continueOnCapturedContext: false);
				await EnsureLibraryStateFreshAsync(LibraryEntityType.Songs);
			}
			catch (Exception ex)
			{
				Exception ex2 = ex;
				if (TryHandleOperationCancelled(ex2, "加载推荐新歌已取消"))
				{
					return;
				}
				Debug.WriteLine($"[LoadPersonalizedNewSongs] 异常: {ex2}");
				await ExecuteOnUiThreadAsync(delegate
				{
					if (!ShouldAbortViewRender(viewToken, "推荐新歌"))
					{
						MessageBox.Show("加载推荐新歌失败: " + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
						UpdateStatusBar("加载推荐新歌失败");
					}
				}).ConfigureAwait(continueOnCapturedContext: false);
			}
		}
	}

}
}
