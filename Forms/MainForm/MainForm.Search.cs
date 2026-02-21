#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using YTPlayer.Core;
using YTPlayer.Models;
using YTPlayer.Utils;
#pragma warning disable CS0219, CS8600, CS8601, CS8602, CS8603, CS8604, CS8618, CS8622, CS8625, CS8632, CS4014

namespace YTPlayer
{
public partial class MainForm
{
	private string GetSelectedSearchType()
	{
		if (_isMixedSearchTypeActive)
		{
			return _lastExplicitSearchType;
		}
        if (searchTypeComboBox.SelectedIndex >= 0 && searchTypeComboBox.SelectedIndex < searchTypeComboBox.Items.Count)
        {
                string text = searchTypeComboBox.Items[searchTypeComboBox.SelectedIndex]?.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                        return text;
                }
        }
        return _lastExplicitSearchType;
	}

	private string NormalizeSearchTypeName(string? searchType)
	{
		string text = (string.IsNullOrWhiteSpace(searchType) ? "歌曲" : searchType.Trim());
		if (string.Equals(text, "混合", StringComparison.OrdinalIgnoreCase))
		{
			text = (string.IsNullOrWhiteSpace(_lastExplicitSearchType) ? "歌曲" : _lastExplicitSearchType);
		}
		switch (text)
		{
		case "歌单":
		case "专辑":
		case "歌手":
		case "播客":
		case "歌曲":
			return text;
		default:
			return "歌曲";
		}
	}

private void EnsureSearchTypeSelection(string searchType)
{
        if (string.IsNullOrWhiteSpace(searchType))
        {
                return;
        }
        if (string.Equals(searchType, MixedSearchTypeDisplayName, StringComparison.OrdinalIgnoreCase))
        {
                ActivateMixedSearchTypeOption();
                return;
        }
        _lastExplicitSearchType = searchType;
        _isMixedSearchTypeActive = false;
        UpdateSearchTypeCombo(delegate
        {
                int num = searchTypeComboBox.Items.IndexOf(searchType);
                if (num >= 0)
                {
                        if (searchTypeComboBox.SelectedIndex != num)
                        {
                                searchTypeComboBox.SelectedIndex = num;
                        }
                }
                else
                {
                        int fallbackIndex = searchTypeComboBox.Items.IndexOf("歌曲");
                        if (fallbackIndex < 0)
                        {
                                fallbackIndex = 0;
                        }
                        if (searchTypeComboBox.Items.Count > 0 && fallbackIndex < searchTypeComboBox.Items.Count)
                        {
                                searchTypeComboBox.SelectedIndex = fallbackIndex;
                        }
                }
                RemoveMixedSearchTypeOptionCore();
        });
}

private void ActivateMixedSearchTypeOption()
{
        ShowMixedSearchTypeOption();
}

        private void UpdateSearchTypeCombo(Action update)
        {
                if (_suppressSearchTypeComboEvents)
                {
                        update();
                        return;
                }
                _suppressSearchTypeComboEvents = true;
                try
                {
                        update();
                }
                finally
                {
                        _suppressSearchTypeComboEvents = false;
                }
        }

        private void ShowMixedSearchTypeOption()
        {
                if (searchTypeComboBox == null)
                {
                        return;
                }
                UpdateSearchTypeCombo(delegate
                {
                        int mixedIndex = searchTypeComboBox.Items.IndexOf(MixedSearchTypeDisplayName);
                        if (mixedIndex < 0)
                        {
                                searchTypeComboBox.Items.Add(MixedSearchTypeDisplayName);
                                mixedIndex = searchTypeComboBox.Items.Count - 1;
                        }
                        _isMixedSearchTypeActive = true;
                        if (mixedIndex >= 0 && searchTypeComboBox.SelectedIndex != mixedIndex)
                        {
                                searchTypeComboBox.SelectedIndex = mixedIndex;
                        }
                });
        }

        private void HideMixedSearchTypeOption()
        {
                UpdateSearchTypeCombo(delegate
                {
                        RemoveMixedSearchTypeOptionCore();
                });
        }

        private void RemoveMixedSearchTypeOptionCore()
        {
                if (searchTypeComboBox == null)
                {
                        return;
                }
                int mixedIndex = searchTypeComboBox.Items.IndexOf(MixedSearchTypeDisplayName);
                if (mixedIndex < 0)
                {
                        return;
                }
                if (searchTypeComboBox.SelectedIndex == mixedIndex)
                {
                        return;
                }
                searchTypeComboBox.Items.RemoveAt(mixedIndex);
        }

	private static List<string> SplitMultiSearchInput(string? rawInput)
	{
		if (string.IsNullOrWhiteSpace(rawInput))
		{
			return new List<string>();
		}
		return (from part in rawInput.Split(MultiUrlSeparators, StringSplitOptions.RemoveEmptyEntries)
			select part.Trim() into part
			where !string.IsNullOrEmpty(part)
			select part).ToList();
	}

	private bool TryParseMultiUrlInput(List<string> segments, out List<NeteaseUrlMatch> matches, out string errorMessage)
	{
		matches = new List<NeteaseUrlMatch>();
		errorMessage = string.Empty;
		if (segments == null || segments.Count == 0)
		{
			return false;
		}
		List<string> list = new List<string>();
		foreach (string segment in segments)
		{
			if (!NeteaseUrlParser.TryParse(segment, out NeteaseUrlMatch match) || match == null)
			{
				list.Add(segment);
			}
			else
			{
				matches.Add(match);
			}
		}
		if (list.Count > 0)
		{
			IEnumerable<string> values = list.Take(5).Select((string value, int index) => $"{checked(index + 1)}. {value}");
			string text = ((list.Count > 5) ? "\n..." : string.Empty);
			errorMessage = "以下链接无法解析：\n" + string.Join("\n", values) + text;
			matches.Clear();
			return false;
		}
		return matches.Count > 0;
	}

	private string ResolveSearchTypeForMatches(IReadOnlyCollection<NeteaseUrlMatch> matches)
	{
		if (matches == null || matches.Count == 0)
		{
			return "歌曲";
		}
		List<NeteaseUrlType> list = matches.Select((NeteaseUrlMatch m) => m.Type).Distinct().Take(2)
			.ToList();
		if (list.Count > 1)
		{
			return "混合";
		}
		return MapUrlTypeToSearchType(list[0]);
	}

	private void ApplySearchTypeDisplayForMatches(IReadOnlyCollection<NeteaseUrlMatch> matches)
	{
		string text = ResolveSearchTypeForMatches(matches);
		if (string.Equals(text, "混合", StringComparison.OrdinalIgnoreCase))
		{
			ActivateMixedSearchTypeOption();
		}
		else
		{
			EnsureSearchTypeSelection(text);
		}
	}

	private string BuildMixedQueryKey(IEnumerable<NeteaseUrlMatch> matches)
	{
		if (matches == null)
		{
			return string.Empty;
		}
		return string.Join(";", matches.Select((NeteaseUrlMatch m) => $"{m.Type}:{m.ResourceId}"));
	}

	private bool TryParseMixedQueryKey(string? key, out List<NeteaseUrlMatch> matches)
	{
		matches = new List<NeteaseUrlMatch>();
		if (string.IsNullOrWhiteSpace(key))
		{
			return false;
		}
		string[] array = key.Split(new char[1] { ';' }, StringSplitOptions.RemoveEmptyEntries);
		string[] array2 = array;
		string[] array3 = array2;
		foreach (string text in array3)
		{
			string[] array4 = text.Split(new char[1] { ':' }, 2);
			if (array4.Length != 2)
			{
				return false;
			}
			if (!int.TryParse(array4[0], out var result) || !Enum.IsDefined(typeof(NeteaseUrlType), result))
			{
				return false;
			}
			string text2 = array4[1];
			matches.Add(new NeteaseUrlMatch((NeteaseUrlType)result, text2, text2));
		}
		return matches.Count > 0;
	}

	private static string GetEntityDisplayName(NeteaseUrlType type)
	{
		if (1 == 0)
		{
		}
		string result = type switch
		{
			NeteaseUrlType.Playlist => "歌单", 
			NeteaseUrlType.Album => "专辑", 
			NeteaseUrlType.Artist => "歌手", 
			NeteaseUrlType.Podcast => "播客", 
			NeteaseUrlType.PodcastEpisode => "播客节目", 
			_ => "歌曲", 
		};
		if (1 == 0)
		{
		}
		return result;
	}

	private async Task<List<SongInfo>> FetchRecentSongsAsync(int limit, CancellationToken cancellationToken = default(CancellationToken))
	{
		return await ExecuteWithRetryAsync(async () => (await _apiClient.GetRecentPlayedSongsAsync(limit)) ?? new List<SongInfo>(), 3, 600, "RecentSongs", cancellationToken);
	}

	private async Task<List<PlaylistInfo>> FetchRecentPlaylistsAsync(int limit, CancellationToken cancellationToken = default(CancellationToken))
	{
		return await ExecuteWithRetryAsync(async () => (await _apiClient.GetRecentPlaylistsAsync(limit)) ?? new List<PlaylistInfo>(), 3, 600, "RecentPlaylists", cancellationToken);
	}

	private async Task<List<AlbumInfo>> FetchRecentAlbumsAsync(int limit, CancellationToken cancellationToken = default(CancellationToken))
	{
		return await ExecuteWithRetryAsync(async () => (await _apiClient.GetRecentAlbumsAsync(limit)) ?? new List<AlbumInfo>(), 3, 600, "RecentAlbums", cancellationToken);
	}

	private async Task<List<PodcastRadioInfo>> FetchRecentPodcastsAsync(int limit, CancellationToken cancellationToken = default(CancellationToken))
	{
		return await ExecuteWithRetryAsync(async () => (await _apiClient.GetRecentPodcastsAsync(limit)) ?? new List<PodcastRadioInfo>(), 3, 600, "RecentPodcasts", cancellationToken);
	}

	private async Task RefreshRecentSummariesAsync(bool forceRefresh, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (!IsUserLoggedIn())
		{
			_recentSongsCache.Clear();
			_recentPlaylistsCache.Clear();
			_recentAlbumsCache.Clear();
			_recentPodcastsCache.Clear();
			_recentPlayCount = 0;
			_recentPlaylistCount = 0;
			_recentAlbumCount = 0;
			_recentPodcastCount = 0;
			_recentSummaryLastUpdatedUtc = DateTime.MinValue;
			_recentSummaryReady = false;
		}
		else
		{
			if (!forceRefresh && !(_recentSummaryLastUpdatedUtc == DateTime.MinValue) && !(DateTime.UtcNow - _recentSummaryLastUpdatedUtc > TimeSpan.FromSeconds(30.0)))
			{
				return;
			}
			Task<List<SongInfo>> songsTask = FetchRecentSongsAsync(300, cancellationToken);
			Task<List<PlaylistInfo>> playlistsTask = FetchRecentPlaylistsAsync(100, cancellationToken);
			Task<List<AlbumInfo>> albumsTask = FetchRecentAlbumsAsync(100, cancellationToken);
			Task<List<PodcastRadioInfo>> podcastsTask = FetchRecentPodcastsAsync(100, cancellationToken);
			try
			{
				_recentSongsCache = await songsTask;
			}
			catch (Exception ex)
			{
				Exception ex2 = ex;
				Exception ex3 = ex2;
				Debug.WriteLine($"[RecentSummary] 获取最近歌曲失败: {ex3}");
				if (forceRefresh)
				{
					_recentSongsCache = new List<SongInfo>();
				}
			}
			_recentPlayCount = _recentSongsCache.Count;
			try
			{
				_recentPlaylistsCache = await playlistsTask;
			}
			catch (Exception ex)
			{
				Exception ex4 = ex;
				Exception ex5 = ex4;
				Debug.WriteLine($"[RecentSummary] 获取最近歌单失败: {ex5}");
				if (forceRefresh)
				{
					_recentPlaylistsCache = new List<PlaylistInfo>();
				}
			}
			_recentPlaylistCount = _recentPlaylistsCache.Count;
			try
			{
				_recentAlbumsCache = await albumsTask;
			}
			catch (Exception ex)
			{
				Exception ex6 = ex;
				Exception ex7 = ex6;
				Debug.WriteLine($"[RecentSummary] 获取最近专辑失败: {ex7}");
				if (forceRefresh)
				{
					_recentAlbumsCache = new List<AlbumInfo>();
				}
			}
			_recentAlbumCount = _recentAlbumsCache.Count;
			try
			{
				_recentPodcastsCache = await podcastsTask;
			}
			catch (Exception ex)
			{
				Exception ex8 = ex;
				Exception ex9 = ex8;
				Debug.WriteLine($"[RecentSummary] 获取最近播客失败: {ex9}");
				if (forceRefresh)
				{
					_recentPodcastsCache = new List<PodcastRadioInfo>();
				}
			}
			_recentPodcastCount = _recentPodcastsCache.Count;
			_recentSummaryLastUpdatedUtc = DateTime.UtcNow;
			_recentSummaryReady = true;
		}
	}

	private bool IsSameSearchContext(string keyword, string searchType, int page)
	{
		ParseSearchViewSource(_currentViewSource, out var searchType2, out var keyword2, out var page2);
		return string.Equals(keyword2, keyword, StringComparison.OrdinalIgnoreCase) && string.Equals(searchType2, searchType, StringComparison.OrdinalIgnoreCase) && page2 == page;
	}

	private SearchViewData<T> BuildSearchSkeletonViewData<T>(string keyword, string searchType, int page, int startIndex, IReadOnlyList<T> cachedItems)
	{
		return new SearchViewData<T>(new List<T>(), 0, hasMore: false, startIndex);
	}

	private static bool HasSkeletonItems<T>(IReadOnlyCollection<T>? items)
	{
		return items != null && items.Count > 0;
	}

	private string BuildSearchSkeletonStatus(string keyword, int cachedCount, string resourceName)
	{
		return (cachedCount > 0) ? $"正在刷新{resourceName}搜索结果（缓存 {cachedCount} 条）..." : ("正在搜索 " + keyword + "...");
	}

	private async Task PerformSearch()
	{
		string keyword = searchTextBox.Text.Trim();
		if (string.IsNullOrEmpty(keyword))
		{
			MessageBox.Show("请输入搜索关键词", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			searchTextBox.Focus();
			return;
		}
		List<string> multiSegments = SplitMultiSearchInput(keyword);
		List<NeteaseUrlMatch> multiMatches = null;
		bool isMultiUrlSearch = false;
		if (multiSegments.Count > 1)
		{
			if (!TryParseMultiUrlInput(multiSegments, out var parsedMatches, out var parseError))
			{
				MessageBox.Show(parseError, "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				return;
			}
			if (parsedMatches.Count > 1)
			{
				isMultiUrlSearch = true;
				multiMatches = parsedMatches;
			}
		}
		bool singleUrlSearch = false;
		NeteaseUrlMatch parsedUrl = null;
		if (!isMultiUrlSearch)
		{
			singleUrlSearch = NeteaseUrlParser.TryParse(keyword, out parsedUrl);
		}
		bool isUrlSearch = isMultiUrlSearch || singleUrlSearch;
		RecordSearchHistory(keyword);
		string searchType;
		if (isMultiUrlSearch && multiMatches != null)
		{
			searchType = ResolveSearchTypeForMatches(multiMatches);
			ApplySearchTypeDisplayForMatches(multiMatches);
		}
		else if (singleUrlSearch && parsedUrl != null)
		{
			searchType = MapUrlTypeToSearchType(parsedUrl.Type);
			EnsureSearchTypeSelection(searchType);
		}
		else
		{
			searchType = GetSelectedSearchType();
		}
		bool isNewKeyword = !string.Equals(keyword, _lastKeyword, StringComparison.OrdinalIgnoreCase);
		bool isTypeChanged = !string.Equals(searchType, _currentSearchType, StringComparison.OrdinalIgnoreCase);
		bool shouldSaveNavigation = !isUrlSearch && (isNewKeyword || isTypeChanged);
		CancellationTokenSource currentSearchCts = BeginSearchOperation();
		CancellationToken searchToken = currentSearchCts.Token;
		try
		{
			UpdateStatusBar("正在搜索: " + keyword + "...");
			_isHomePage = false;
                if (isMultiUrlSearch && multiMatches != null)
                {
                    await HandleMultipleNeteaseUrlSearchAsync(multiMatches, searchToken).ConfigureAwait(continueOnCapturedContext: true);
                    await EnsureListFocusedAfterUrlParseAsync().ConfigureAwait(continueOnCapturedContext: true);
                    _lastKeyword = keyword;
                }
                else if (singleUrlSearch && parsedUrl != null)
                {
                    await HandleNeteaseUrlSearchAsync(parsedUrl, searchToken).ConfigureAwait(continueOnCapturedContext: true);
                    await EnsureListFocusedAfterUrlParseAsync().ConfigureAwait(continueOnCapturedContext: true);
                    _lastKeyword = keyword;
                }
			else
			{
				await ExecuteSearchAsync(keyword, searchType, 1, !shouldSaveNavigation, showEmptyPrompt: true, searchToken).ConfigureAwait(continueOnCapturedContext: true);
			}
		}
		catch (OperationCanceledException) when (searchToken.IsCancellationRequested)
		{
			UpdateStatusBar("搜索已取消");
		}
		catch (Exception ex2)
		{
			Exception ex3 = ex2;
			Exception ex4 = ex3;
			Debug.WriteLine($"[Search] 执行搜索失败: {ex4}");
			MessageBox.Show("搜索失败: " + ex4.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("搜索失败");
		}
		finally
		{
			if (_searchCts == currentSearchCts)
			{
				_searchCts = null;
			}
			currentSearchCts.Dispose();
		}
	}

	private async Task ExecuteSearchAsync(string keyword, string searchType, int page, bool skipSaveNavigation, bool showEmptyPrompt, CancellationToken searchToken, int pendingFocusIndex = -1)
	{
		string normalizedSearchType = NormalizeSearchTypeName(searchType);
		if (!skipSaveNavigation)
		{
			SaveNavigationState();
		}
		_lastKeyword = keyword;
		_currentPage = Math.Max(1, page);
		_currentSearchType = normalizedSearchType;
		_isHomePage = false;
		_currentMixedQueryKey = null;
		EnsureSearchTypeSelection(normalizedSearchType);
		switch (normalizedSearchType)
		{
		case "歌单":
			await ShowPlaylistSearchResultsAsync(keyword, _currentPage, skipSaveNavigation, showEmptyPrompt, searchToken, pendingFocusIndex).ConfigureAwait(continueOnCapturedContext: true);
			break;
		case "专辑":
			await ShowAlbumSearchResultsAsync(keyword, _currentPage, skipSaveNavigation, showEmptyPrompt, searchToken, pendingFocusIndex).ConfigureAwait(continueOnCapturedContext: true);
			break;
		case "歌手":
			await ShowArtistSearchResultsAsync(keyword, _currentPage, skipSaveNavigation, showEmptyPrompt, searchToken, pendingFocusIndex).ConfigureAwait(continueOnCapturedContext: true);
			break;
		case "播客":
			await ShowPodcastSearchResultsAsync(keyword, _currentPage, skipSaveNavigation, showEmptyPrompt, searchToken, pendingFocusIndex).ConfigureAwait(continueOnCapturedContext: true);
			break;
		default:
			await ShowSongSearchResultsAsync(keyword, _currentPage, skipSaveNavigation, showEmptyPrompt, searchToken, pendingFocusIndex).ConfigureAwait(continueOnCapturedContext: true);
			break;
		}
	}

	private async Task ShowSongSearchResultsAsync(string keyword, int page, bool skipSaveNavigation, bool showEmptyPrompt, CancellationToken searchToken, int pendingFocusIndex = -1)
	{
		checked
		{
			int offset = Math.Max(0, (page - 1) * _resultsPerPage);
			string viewSource = $"search:{keyword}:page{page}";
			string accessibleName = "搜索: " + keyword;
			ViewLoadRequest request = new ViewLoadRequest(viewSource, accessibleName, "正在搜索 " + keyword + "...", !skipSaveNavigation, (pendingFocusIndex >= 0) ? pendingFocusIndex : 0);
			ViewLoadResult<SearchViewData<SongInfo>> loadResult = await RunViewLoadAsync(request, delegate
			{
				using (WorkScopes.BeginSkeleton("SearchSong", viewSource))
				{
					return Task.FromResult(BuildSearchSkeletonViewData(keyword, "歌曲", page, offset + 1, _currentSongs));
				}
			}, "搜索歌曲已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				SearchViewData<SongInfo> skeleton = loadResult.Value;
				string statusText = BuildSearchSkeletonStatus(keyword, skeleton.Items.Count, "歌曲");
				_currentPlaylist = null;
				if (HasSkeletonItems(skeleton.Items))
				{
					DisplaySongs(skeleton.Items, showPagination: false, hasNextPage: false, skeleton.StartIndex, preserveSelection: false, viewSource, accessibleName, skipAvailabilityCheck: true);
				}
				UpdateStatusBar(statusText);
				EnrichSongSearchResultsAsync(keyword, page, offset, viewSource, accessibleName, showEmptyPrompt, searchToken, pendingFocusIndex);
			}
		}
	}

	private async Task EnrichSongSearchResultsAsync(string keyword, int page, int offset, string viewSource, string accessibleName, bool showEmptyPrompt, CancellationToken searchToken, int pendingFocusIndex = -1)
	{
		CancellationToken viewToken = GetCurrentViewContentToken();
		checked
		{
			using (WorkScopes.BeginEnrichment("SearchSong", viewSource))
			{
				CancellationTokenSource linkedCts = null;
				CancellationToken effectiveToken = LinkCancellationTokens(viewToken, searchToken, out linkedCts);
				try
				{
					SearchResult<SongInfo> songResult = await FetchWithRetryUntilCancel((CancellationToken ct) => _apiClient.SearchSongsAsync(keyword, _resultsPerPage, offset, ct), $"search:song:{keyword}:page{page}", effectiveToken, delegate(int attempt, Exception _)
					{
						SafeInvoke(delegate
						{
							UpdateStatusBar($"搜索歌曲失败，正在重试（第 {attempt} 次）...");
						});
					}).ConfigureAwait(continueOnCapturedContext: true);
					List<SongInfo> songs = songResult?.Items ?? new List<SongInfo>();
					int totalCount = songResult?.TotalCount ?? songs.Count;
					bool hasMore = songResult?.HasMore ?? false;
					int maxPage = Math.Max(1, (int)Math.Ceiling((double)Math.Max(1, totalCount) / (double)Math.Max(1, _resultsPerPage)));
					string paginationKey = BuildSearchPaginationKey(keyword, "歌曲");
					if (TryHandlePaginationEmptyResult(paginationKey, offset, _resultsPerPage, totalCount, songs.Count, hasMore, viewSource, accessibleName))
					{
						return;
					}
					if (effectiveToken.IsCancellationRequested)
					{
						return;
					}
					await ExecuteOnUiThreadAsync(delegate
					{
						if (!ShouldAbortViewRender(viewToken, "搜索歌曲"))
						{
							_currentSongs = CloneList(songs);
							_currentPlaylist = null;
							_maxPage = maxPage;
							_hasNextSearchPage = hasMore;
							PatchSongs(_currentSongs, offset + 1, skipAvailabilityCheck: false, showPagination: true, page > 1, hasMore, pendingFocusIndex);
							FocusListAfterEnrich(pendingFocusIndex);
							if (_currentSongs.Count == 0)
							{
								UpdateStatusBar("未找到结果");
								if (showEmptyPrompt)
								{
									MessageBox.Show("未找到相关歌曲: " + keyword, "搜索结果", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
								}
							}
							else
							{
								UpdateStatusBar($"第 {_currentPage}/{_maxPage} 页，本页 {_currentSongs.Count} 首 / 总 {totalCount} 首");
							}
						}
					}).ConfigureAwait(continueOnCapturedContext: false);
					await EnsureLibraryStateFreshAsync(LibraryEntityType.Songs);
				}
				catch (Exception ex)
				{
					Exception ex2 = ex;
					string paginationKey = BuildSearchPaginationKey(keyword, "歌曲");
					if (TryHandlePaginationOffsetError(ex2, paginationKey, offset, _resultsPerPage, viewSource, accessibleName))
					{
						return;
					}
					if (TryHandleOperationCancelled(ex2, "搜索歌曲已取消"))
					{
						return;
					}
					Debug.WriteLine($"[Search] 搜索歌曲失败: {ex2}");
					await ExecuteOnUiThreadAsync(delegate
					{
						if (!ShouldAbortViewRender(viewToken, "搜索歌曲"))
						{
							MessageBox.Show("搜索歌曲失败: " + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
							UpdateStatusBar("搜索歌曲失败");
						}
					}).ConfigureAwait(continueOnCapturedContext: false);
				}
				finally
				{
					linkedCts?.Dispose();
				}
			}
		}
	}

	private async Task ShowPlaylistSearchResultsAsync(string keyword, int page, bool skipSaveNavigation, bool showEmptyPrompt, CancellationToken searchToken, int pendingFocusIndex = -1)
	{
		checked
		{
			int offset = Math.Max(0, (page - 1) * _resultsPerPage);
			string viewSource = $"search:playlist:{keyword}:page{page}";
			string accessibleName = "搜索歌单: " + keyword;
			ViewLoadRequest request = new ViewLoadRequest(viewSource, accessibleName, "正在搜索 " + keyword + "...", !skipSaveNavigation, (pendingFocusIndex >= 0) ? pendingFocusIndex : 0);
			ViewLoadResult<SearchViewData<PlaylistInfo>> loadResult = await RunViewLoadAsync(request, delegate
			{
				using (WorkScopes.BeginSkeleton("SearchPlaylist", viewSource))
				{
					return Task.FromResult(BuildSearchSkeletonViewData(keyword, "歌单", page, offset + 1, _currentPlaylists));
				}
			}, "搜索歌单已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				SearchViewData<PlaylistInfo> skeleton = loadResult.Value;
				string statusText = BuildSearchSkeletonStatus(keyword, skeleton.Items.Count, "歌单");
				if (HasSkeletonItems(skeleton.Items))
				{
					DisplayPlaylists(skeleton.Items, preserveSelection: false, viewSource, accessibleName, skeleton.StartIndex);
				}
				UpdateStatusBar(statusText);
				EnrichPlaylistSearchResultsAsync(keyword, page, offset, viewSource, accessibleName, showEmptyPrompt, searchToken, pendingFocusIndex);
			}
		}
	}

	private async Task EnrichPlaylistSearchResultsAsync(string keyword, int page, int offset, string viewSource, string accessibleName, bool showEmptyPrompt, CancellationToken searchToken, int pendingFocusIndex = -1)
	{
		CancellationToken viewToken = GetCurrentViewContentToken();
		checked
		{
			using (WorkScopes.BeginEnrichment("SearchPlaylist", viewSource))
			{
				CancellationTokenSource linkedCts = null;
				CancellationToken effectiveToken = LinkCancellationTokens(viewToken, searchToken, out linkedCts);
				try
				{
					SearchResult<PlaylistInfo> playlistResult = await FetchWithRetryUntilCancel((CancellationToken ct) => _apiClient.SearchPlaylistsAsync(keyword, _resultsPerPage, offset, ct), $"search:playlist:{keyword}:page{page}", effectiveToken, delegate(int attempt, Exception _)
					{
						SafeInvoke(delegate
						{
							UpdateStatusBar($"搜索歌单失败，正在重试（第 {attempt} 次）...");
						});
					}).ConfigureAwait(continueOnCapturedContext: true);
					List<PlaylistInfo> playlists = playlistResult?.Items ?? new List<PlaylistInfo>();
					int totalCount = playlistResult?.TotalCount ?? playlists.Count;
					bool hasMore = playlistResult?.HasMore ?? false;
					int maxPage = Math.Max(1, (int)Math.Ceiling((double)Math.Max(1, totalCount) / (double)Math.Max(1, _resultsPerPage)));
					string paginationKey = BuildSearchPaginationKey(keyword, "歌单");
					if (TryHandlePaginationEmptyResult(paginationKey, offset, _resultsPerPage, totalCount, playlists.Count, hasMore, viewSource, accessibleName))
					{
						return;
					}
					if (effectiveToken.IsCancellationRequested)
					{
						return;
					}
					await ExecuteOnUiThreadAsync(delegate
					{
						if (!ShouldAbortViewRender(viewToken, "搜索歌单"))
						{
							_currentPlaylists = CloneList(playlists);
							_maxPage = maxPage;
							_hasNextSearchPage = hasMore;
							PatchPlaylists(_currentPlaylists, offset + 1, showPagination: true, page > 1, hasMore, pendingFocusIndex);
							FocusListAfterEnrich(pendingFocusIndex);
							if (_currentPlaylists.Count == 0)
							{
								UpdateStatusBar("未找到结果");
								if (showEmptyPrompt)
								{
									MessageBox.Show("未找到相关歌单: " + keyword, "搜索结果", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
								}
							}
							else
							{
								UpdateStatusBar($"第 {_currentPage}/{_maxPage} 页，本页 {_currentPlaylists.Count} 个 / 总 {totalCount} 个");
							}
						}
					}).ConfigureAwait(continueOnCapturedContext: false);
					await EnsureLibraryStateFreshAsync(LibraryEntityType.Playlists);
				}
				catch (Exception ex)
				{
					Exception ex2 = ex;
					string paginationKey = BuildSearchPaginationKey(keyword, "歌单");
					if (TryHandlePaginationOffsetError(ex2, paginationKey, offset, _resultsPerPage, viewSource, accessibleName))
					{
						return;
					}
					if (TryHandleOperationCancelled(ex2, "搜索歌单已取消"))
					{
						return;
					}
					Debug.WriteLine($"[Search] 搜索歌单失败: {ex2}");
					await ExecuteOnUiThreadAsync(delegate
					{
						if (!ShouldAbortViewRender(viewToken, "搜索歌单"))
						{
							MessageBox.Show("搜索歌单失败: " + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
							UpdateStatusBar("搜索歌单失败");
						}
					}).ConfigureAwait(continueOnCapturedContext: false);
				}
				finally
				{
					linkedCts?.Dispose();
				}
			}
		}
	}

	private async Task ShowAlbumSearchResultsAsync(string keyword, int page, bool skipSaveNavigation, bool showEmptyPrompt, CancellationToken searchToken, int pendingFocusIndex = -1)
	{
		checked
		{
			int offset = Math.Max(0, (page - 1) * _resultsPerPage);
			string viewSource = $"search:album:{keyword}:page{page}";
			string accessibleName = "搜索专辑: " + keyword;
			ViewLoadRequest request = new ViewLoadRequest(viewSource, accessibleName, "正在搜索 " + keyword + "...", !skipSaveNavigation, (pendingFocusIndex >= 0) ? pendingFocusIndex : 0);
			ViewLoadResult<SearchViewData<AlbumInfo>> loadResult = await RunViewLoadAsync(request, delegate
			{
				using (WorkScopes.BeginSkeleton("SearchAlbum", viewSource))
				{
					return Task.FromResult(BuildSearchSkeletonViewData(keyword, "专辑", page, offset + 1, _currentAlbums));
				}
			}, "搜索专辑已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				SearchViewData<AlbumInfo> skeleton = loadResult.Value;
				string statusText = BuildSearchSkeletonStatus(keyword, skeleton.Items.Count, "专辑");
				if (HasSkeletonItems(skeleton.Items))
				{
					DisplayAlbums(skeleton.Items, preserveSelection: false, viewSource, accessibleName, skeleton.StartIndex);
				}
				UpdateStatusBar(statusText);
				EnrichAlbumSearchResultsAsync(keyword, page, offset, viewSource, accessibleName, showEmptyPrompt, searchToken, pendingFocusIndex);
			}
		}
	}

	private async Task EnrichAlbumSearchResultsAsync(string keyword, int page, int offset, string viewSource, string accessibleName, bool showEmptyPrompt, CancellationToken searchToken, int pendingFocusIndex = -1)
	{
		CancellationToken viewToken = GetCurrentViewContentToken();
		checked
		{
			using (WorkScopes.BeginEnrichment("SearchAlbum", viewSource))
			{
				CancellationTokenSource linkedCts = null;
				CancellationToken effectiveToken = LinkCancellationTokens(viewToken, searchToken, out linkedCts);
				try
				{
					SearchResult<AlbumInfo> albumResult = await FetchWithRetryUntilCancel((CancellationToken ct) => _apiClient.SearchAlbumsAsync(keyword, _resultsPerPage, offset, ct), $"search:album:{keyword}:page{page}", effectiveToken, delegate(int attempt, Exception _)
					{
						SafeInvoke(delegate
						{
							UpdateStatusBar($"搜索专辑失败，正在重试（第 {attempt} 次）...");
						});
					}).ConfigureAwait(continueOnCapturedContext: true);
					List<AlbumInfo> albums = albumResult?.Items ?? new List<AlbumInfo>();
					int totalCount = albumResult?.TotalCount ?? albums.Count;
					bool hasMore = albumResult?.HasMore ?? false;
					int maxPage = Math.Max(1, (int)Math.Ceiling((double)Math.Max(1, totalCount) / (double)Math.Max(1, _resultsPerPage)));
					string paginationKey = BuildSearchPaginationKey(keyword, "专辑");
					if (TryHandlePaginationEmptyResult(paginationKey, offset, _resultsPerPage, totalCount, albums.Count, hasMore, viewSource, accessibleName))
					{
						return;
					}
					if (effectiveToken.IsCancellationRequested)
					{
						return;
					}
					await ExecuteOnUiThreadAsync(delegate
					{
						if (!ShouldAbortViewRender(viewToken, "搜索专辑"))
						{
							_currentAlbums = CloneList(albums);
							_maxPage = maxPage;
							_hasNextSearchPage = hasMore;
							PatchAlbums(_currentAlbums, offset + 1, showPagination: true, page > 1, hasMore, pendingFocusIndex);
							FocusListAfterEnrich(pendingFocusIndex);
							if (_currentAlbums.Count == 0)
							{
								UpdateStatusBar("未找到结果");
								if (showEmptyPrompt)
								{
									MessageBox.Show("未找到相关专辑: " + keyword, "搜索结果", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
								}
							}
							else
							{
								UpdateStatusBar($"第 {_currentPage}/{_maxPage} 页，本页 {_currentAlbums.Count} 张 / 总 {totalCount} 张");
							}
						}
					}).ConfigureAwait(continueOnCapturedContext: false);
					await EnsureLibraryStateFreshAsync(LibraryEntityType.Albums);
				}
				catch (Exception ex)
				{
					Exception ex2 = ex;
					string paginationKey = BuildSearchPaginationKey(keyword, "专辑");
					if (TryHandlePaginationOffsetError(ex2, paginationKey, offset, _resultsPerPage, viewSource, accessibleName))
					{
						return;
					}
					if (TryHandleOperationCancelled(ex2, "搜索专辑已取消"))
					{
						return;
					}
					Debug.WriteLine($"[Search] 搜索专辑失败: {ex2}");
					await ExecuteOnUiThreadAsync(delegate
					{
						if (!ShouldAbortViewRender(viewToken, "搜索专辑"))
						{
							MessageBox.Show("搜索专辑失败: " + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
							UpdateStatusBar("搜索专辑失败");
						}
					}).ConfigureAwait(continueOnCapturedContext: false);
				}
				finally
				{
					linkedCts?.Dispose();
				}
			}
		}
	}

	private async Task ShowArtistSearchResultsAsync(string keyword, int page, bool skipSaveNavigation, bool showEmptyPrompt, CancellationToken searchToken, int pendingFocusIndex = -1)
	{
		checked
		{
			int offset = Math.Max(0, (page - 1) * _resultsPerPage);
			string viewSource = $"search:artist:{keyword}:page{page}";
			string accessibleName = "搜索歌手: " + keyword;
			ViewLoadRequest request = new ViewLoadRequest(viewSource, accessibleName, "正在搜索 " + keyword + "...", !skipSaveNavigation, (pendingFocusIndex >= 0) ? pendingFocusIndex : 0);
			ViewLoadResult<SearchViewData<ArtistInfo>> loadResult = await RunViewLoadAsync(request, delegate
			{
				using (WorkScopes.BeginSkeleton("SearchArtist", viewSource))
				{
					return Task.FromResult(BuildSearchSkeletonViewData(keyword, "歌手", page, offset + 1, _currentArtists));
				}
			}, "搜索歌手已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				SearchViewData<ArtistInfo> skeleton = loadResult.Value;
				string statusText = BuildSearchSkeletonStatus(keyword, skeleton.Items.Count, "歌手");
				if (HasSkeletonItems(skeleton.Items))
				{
					DisplayArtists(skeleton.Items, showPagination: false, hasNextPage: false, skeleton.StartIndex, preserveSelection: false, viewSource, accessibleName);
				}
				UpdateStatusBar(statusText);
				EnrichArtistSearchResultsAsync(keyword, page, offset, viewSource, accessibleName, showEmptyPrompt, searchToken, pendingFocusIndex);
			}
		}
	}

	private async Task EnrichArtistSearchResultsAsync(string keyword, int page, int offset, string viewSource, string accessibleName, bool showEmptyPrompt, CancellationToken searchToken, int pendingFocusIndex = -1)
	{
		CancellationToken viewToken = GetCurrentViewContentToken();
		checked
		{
			using (WorkScopes.BeginEnrichment("SearchArtist", viewSource))
			{
				CancellationTokenSource linkedCts = null;
				CancellationToken effectiveToken = LinkCancellationTokens(viewToken, searchToken, out linkedCts);
				try
				{
					SearchResult<ArtistInfo> artistResult = await FetchWithRetryUntilCancel((CancellationToken ct) => _apiClient.SearchArtistsAsync(keyword, _resultsPerPage, offset, ct), $"search:artist:{keyword}:page{page}", effectiveToken, delegate(int attempt, Exception _)
					{
						SafeInvoke(delegate
						{
							UpdateStatusBar($"搜索歌手失败，正在重试（第 {attempt} 次）...");
						});
					}).ConfigureAwait(continueOnCapturedContext: true);
					List<ArtistInfo> artists = artistResult?.Items ?? new List<ArtistInfo>();
					int totalCount = artistResult?.TotalCount ?? artists.Count;
					bool hasMore = artistResult?.HasMore ?? false;
					int maxPage = Math.Max(1, (int)Math.Ceiling((double)Math.Max(1, totalCount) / (double)Math.Max(1, _resultsPerPage)));
					string paginationKey = BuildSearchPaginationKey(keyword, "歌手");
					if (TryHandlePaginationEmptyResult(paginationKey, offset, _resultsPerPage, totalCount, artists.Count, hasMore, viewSource, accessibleName))
					{
						return;
					}
					if (effectiveToken.IsCancellationRequested)
					{
						return;
					}
					await ExecuteOnUiThreadAsync(delegate
					{
						if (!ShouldAbortViewRender(viewToken, "搜索歌手"))
						{
							_currentArtists = CloneList(artists);
							_maxPage = maxPage;
							_hasNextSearchPage = hasMore;
							PatchArtists(_currentArtists, offset + 1, showPagination: true, page > 1, hasMore, pendingFocusIndex);
							FocusListAfterEnrich(pendingFocusIndex);
							if (_currentArtists.Count == 0)
							{
								UpdateStatusBar("未找到结果");
								if (showEmptyPrompt)
								{
									MessageBox.Show("未找到相关歌手: " + keyword, "搜索结果", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
								}
							}
							else
							{
								UpdateStatusBar($"第 {_currentPage}/{_maxPage} 页，本页 {_currentArtists.Count} 位 / 总 {totalCount} 位");
							}
						}
					}).ConfigureAwait(continueOnCapturedContext: false);
					await EnsureLibraryStateFreshAsync(LibraryEntityType.Artists);
				}
				catch (Exception ex)
				{
					Exception ex2 = ex;
					string paginationKey = BuildSearchPaginationKey(keyword, "歌手");
					if (TryHandlePaginationOffsetError(ex2, paginationKey, offset, _resultsPerPage, viewSource, accessibleName))
					{
						return;
					}
					if (TryHandleOperationCancelled(ex2, "搜索歌手已取消"))
					{
						return;
					}
					Debug.WriteLine($"[Search] 搜索歌手失败: {ex2}");
					await ExecuteOnUiThreadAsync(delegate
					{
						if (!ShouldAbortViewRender(viewToken, "搜索歌手"))
						{
							MessageBox.Show("搜索歌手失败: " + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
							UpdateStatusBar("搜索歌手失败");
						}
					}).ConfigureAwait(continueOnCapturedContext: false);
				}
				finally
				{
					linkedCts?.Dispose();
				}
			}
		}
	}

	private async Task ShowPodcastSearchResultsAsync(string keyword, int page, bool skipSaveNavigation, bool showEmptyPrompt, CancellationToken searchToken, int pendingFocusIndex = -1)
	{
		checked
		{
			int offset = Math.Max(0, (page - 1) * _resultsPerPage);
			string viewSource = $"search:podcast:{keyword}:page{page}";
			string accessibleName = "搜索播客: " + keyword;
			ViewLoadRequest request = new ViewLoadRequest(viewSource, accessibleName, "正在搜索 " + keyword + "...", !skipSaveNavigation, (pendingFocusIndex >= 0) ? pendingFocusIndex : 0);
			ViewLoadResult<SearchViewData<PodcastRadioInfo>> loadResult = await RunViewLoadAsync(request, delegate
			{
				using (WorkScopes.BeginSkeleton("SearchPodcast", viewSource))
				{
					return Task.FromResult(BuildSearchSkeletonViewData(keyword, "播客", page, offset + 1, _currentPodcasts));
				}
			}, "搜索播客已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				SearchViewData<PodcastRadioInfo> skeleton = loadResult.Value;
				string statusText = BuildSearchSkeletonStatus(keyword, skeleton.Items.Count, "播客");
				if (HasSkeletonItems(skeleton.Items))
				{
					DisplayPodcasts(skeleton.Items, showPagination: false, hasNextPage: false, skeleton.StartIndex, preserveSelection: false, viewSource, accessibleName);
				}
				UpdateStatusBar(statusText);
				EnrichPodcastSearchResultsAsync(keyword, page, offset, viewSource, accessibleName, showEmptyPrompt, searchToken, pendingFocusIndex);
			}
		}
	}

	private async Task EnrichPodcastSearchResultsAsync(string keyword, int page, int offset, string viewSource, string accessibleName, bool showEmptyPrompt, CancellationToken searchToken, int pendingFocusIndex = -1)
	{
		CancellationToken viewToken = GetCurrentViewContentToken();
		checked
		{
			using (WorkScopes.BeginEnrichment("SearchPodcast", viewSource))
			{
				CancellationTokenSource linkedCts = null;
				CancellationToken effectiveToken = LinkCancellationTokens(viewToken, searchToken, out linkedCts);
				try
				{
					SearchResult<PodcastRadioInfo> podcastResult = await FetchWithRetryUntilCancel((CancellationToken ct) => _apiClient.SearchPodcastsAsync(keyword, _resultsPerPage, offset, ct), $"search:podcast:{keyword}:page{page}", effectiveToken, delegate(int attempt, Exception _)
					{
						SafeInvoke(delegate
						{
							UpdateStatusBar($"搜索播客失败，正在重试（第 {attempt} 次）...");
						});
					}).ConfigureAwait(continueOnCapturedContext: true);
					List<PodcastRadioInfo> podcasts = podcastResult?.Items ?? new List<PodcastRadioInfo>();
					int totalCount = podcastResult?.TotalCount ?? podcasts.Count;
					bool hasMore = podcastResult?.HasMore ?? false;
					int maxPage = Math.Max(1, (int)Math.Ceiling((double)Math.Max(1, totalCount) / (double)Math.Max(1, _resultsPerPage)));
					string paginationKey = BuildSearchPaginationKey(keyword, "播客");
					if (TryHandlePaginationEmptyResult(paginationKey, offset, _resultsPerPage, totalCount, podcasts.Count, hasMore, viewSource, accessibleName))
					{
						return;
					}
					if (effectiveToken.IsCancellationRequested)
					{
						return;
					}
					await ExecuteOnUiThreadAsync(delegate
					{
						if (!ShouldAbortViewRender(viewToken, "搜索播客"))
						{
							_currentPodcasts = CloneList(podcasts);
							_maxPage = maxPage;
							_hasNextSearchPage = hasMore;
							PatchPodcasts(_currentPodcasts, offset + 1, showPagination: true, page > 1, hasMore, pendingFocusIndex, allowSelection: false);
							FocusListAfterEnrich(pendingFocusIndex);
							if (_currentPodcasts.Count == 0)
							{
								UpdateStatusBar("未找到结果");
								if (showEmptyPrompt)
								{
									MessageBox.Show("未找到相关播客: " + keyword, "搜索结果", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
								}
							}
							else
							{
								UpdateStatusBar($"第 {_currentPage}/{_maxPage} 页，本页 {_currentPodcasts.Count} 个 / 总 {totalCount} 个");
							}
						}
					}).ConfigureAwait(continueOnCapturedContext: false);
					await EnsureLibraryStateFreshAsync(LibraryEntityType.Podcasts);
				}
				catch (Exception ex)
				{
					Exception ex2 = ex;
					string paginationKey = BuildSearchPaginationKey(keyword, "播客");
					if (TryHandlePaginationOffsetError(ex2, paginationKey, offset, _resultsPerPage, viewSource, accessibleName))
					{
						return;
					}
					if (TryHandleOperationCancelled(ex2, "搜索播客已取消"))
					{
						return;
					}
					Debug.WriteLine($"[Search] 搜索播客失败: {ex2}");
					await ExecuteOnUiThreadAsync(delegate
					{
						if (!ShouldAbortViewRender(viewToken, "搜索播客"))
						{
							MessageBox.Show("搜索播客失败: " + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
							UpdateStatusBar("搜索播客失败");
						}
					}).ConfigureAwait(continueOnCapturedContext: false);
				}
				finally
				{
					linkedCts?.Dispose();
				}
			}
		}
	}

	private async Task HandleNeteaseUrlSearchAsync(NeteaseUrlMatch match, CancellationToken cancellationToken)
	{
		switch (match.Type)
		{
		case NeteaseUrlType.Song:
			await HandleSongUrlAsync(match, cancellationToken);
			UpdateStatusBar("已定位歌曲");
			break;
		case NeteaseUrlType.Playlist:
			await HandlePlaylistUrlAsync(match, cancellationToken);
			break;
		case NeteaseUrlType.Album:
			await HandleAlbumUrlAsync(match, cancellationToken);
			break;
		case NeteaseUrlType.Artist:
			await HandleArtistUrlAsync(match, cancellationToken);
			break;
		case NeteaseUrlType.Podcast:
			await HandlePodcastUrlAsync(match, cancellationToken);
			break;
		case NeteaseUrlType.PodcastEpisode:
			await HandlePodcastEpisodeUrlAsync(match, cancellationToken);
			break;
		default:
			MessageBox.Show("暂不支持该链接类型。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			UpdateStatusBar("不支持的链接类型");
			break;
		}
	}

	private async Task HandleSongUrlAsync(NeteaseUrlMatch match, CancellationToken cancellationToken)
	{
		if (TryValidateNeteaseResourceId(match.ResourceId, "歌曲", out var parsedSongId))
		{
			string resolvedSongId = parsedSongId.ToString();
			List<SongInfo> songs = await _apiClient.GetSongDetailAsync(new string[1] { resolvedSongId });
			cancellationToken.ThrowIfCancellationRequested();
			SongInfo song = songs?.FirstOrDefault();
			if (song == null)
			{
				MessageBox.Show("未能找到该链接指向的歌曲。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				UpdateStatusBar("未找到歌曲");
			}
			else
			{
				DisplaySongFromUrl(song, resolvedSongId, skipSave: false);
			}
		}
	}

	private async Task<bool> LoadSongFromUrlAsync(string songId, bool skipSave = false)
	{
		if (string.IsNullOrWhiteSpace(songId))
		{
			Debug.WriteLine("[Navigation] 无法加载歌曲视图，缺少歌曲ID");
			return false;
		}
		try
		{
			SongInfo song = (await _apiClient.GetSongDetailAsync(new string[1] { songId }))?.FirstOrDefault();
			if (song == null)
			{
				MessageBox.Show("未能找到该链接指向的歌曲。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				UpdateStatusBar("未找到歌曲");
				return false;
			}
			return DisplaySongFromUrl(song, songId, skipSave);
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"[Navigation] 加载歌曲失败: {ex}");
			MessageBox.Show("加载歌曲失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("加载歌曲失败");
			return false;
		}
	}

	private bool DisplaySongFromUrl(SongInfo song, string? fallbackSongId, bool skipSave)
	{
		if (song == null)
		{
			return false;
		}
		string text = ((!string.IsNullOrWhiteSpace(song.Id)) ? song.Id : (fallbackSongId ?? string.Empty));
		if (string.IsNullOrWhiteSpace(text))
		{
			MessageBox.Show("无法显示该歌曲，缺少有效的歌曲ID。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			return false;
		}
		if (!skipSave)
		{
			SaveNavigationState();
		}
		_isHomePage = false;
		_currentSongs = new List<SongInfo> { song };
		_currentPlaylist = null;
		_currentPage = 1;
		_maxPage = 1;
		_hasNextSearchPage = false;
		string viewSource = (_currentViewSource = "url:song:" + text);
		string accessibleName = (string.IsNullOrWhiteSpace(song.Name) ? ("歌曲: " + text) : ("歌曲: " + song.Name));
		DisplaySongs(_currentSongs, showPagination: false, hasNextPage: false, 1, preserveSelection: false, viewSource, accessibleName);
		FocusListAfterEnrich(0);
		return true;
	}

	private async Task HandlePlaylistUrlAsync(NeteaseUrlMatch match, CancellationToken cancellationToken)
	{
		if (TryValidateNeteaseResourceId(match.ResourceId, "歌单", out var parsedPlaylistId))
		{
			await LoadPlaylistById(parsedPlaylistId.ToString());
			FocusListAfterEnrich(0);
		}
	}

	private async Task HandleAlbumUrlAsync(NeteaseUrlMatch match, CancellationToken cancellationToken)
	{
		if (TryValidateNeteaseResourceId(match.ResourceId, "专辑", out var parsedAlbumId))
		{
			await LoadAlbumById(parsedAlbumId.ToString());
			FocusListAfterEnrich(0);
		}
	}

	private async Task HandleArtistUrlAsync(NeteaseUrlMatch match, CancellationToken cancellationToken)
	{
		if (TryValidateNeteaseResourceId(match.ResourceId, "歌手", out var artistId))
		{
			ArtistInfo artist = new ArtistInfo
			{
				Id = artistId,
				Name = $"歌手 {artistId}"
			};
			await OpenArtistAsync(artist);
		}
	}

	private async Task HandlePodcastUrlAsync(NeteaseUrlMatch match, CancellationToken cancellationToken)
	{
		if (TryValidateNeteaseResourceId(match.ResourceId, "播客", out var podcastId))
		{
			PodcastRadioInfo podcast = await _apiClient.GetPodcastRadioDetailAsync(podcastId);
			cancellationToken.ThrowIfCancellationRequested();
			if (podcast == null)
			{
				MessageBox.Show("未能找到该播客。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				UpdateStatusBar("未找到播客");
			}
			else
			{
				await OpenPodcastRadioAsync(podcast);
				FocusListAfterEnrich(0);
			}
		}
	}

	private async Task HandlePodcastEpisodeUrlAsync(NeteaseUrlMatch match, CancellationToken cancellationToken)
	{
		if (TryValidateNeteaseResourceId(match.ResourceId, "播客节目", out var programId))
		{
			PodcastEpisodeInfo episode = await _apiClient.GetPodcastEpisodeDetailAsync(programId);
			cancellationToken.ThrowIfCancellationRequested();
			if (episode == null)
			{
				MessageBox.Show("未能找到该播客节目。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				UpdateStatusBar("未找到播客节目");
			}
			else if (episode.Song != null)
			{
				await PlaySong(episode.Song);
			}
			else
			{
				MessageBox.Show("该播客节目暂无可播放的音频。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				UpdateStatusBar("无法播放播客节目");
			}
		}
	}

	private async Task HandleMultipleNeteaseUrlSearchAsync(List<NeteaseUrlMatch> matches, CancellationToken cancellationToken, bool skipSave = false, string? mixedQueryKeyOverride = null)
	{
		if (matches == null || matches.Count == 0)
		{
			return;
		}
		List<NormalizedUrlMatch> normalizedMatches = new List<NormalizedUrlMatch>();
		List<string> parseFailures = new List<string>();
		foreach (NeteaseUrlMatch match in matches)
		{
			string entityName = GetEntityDisplayName(match.Type);
			if (!TryValidateNeteaseResourceId(match.ResourceId, entityName, out var parsedId))
			{
				parseFailures.Add(entityName + "（" + match.ResourceId + "）");
			}
			else
			{
				normalizedMatches.Add(new NormalizedUrlMatch(match.Type, entityName, parsedId));
			}
		}
		if (normalizedMatches.Count == 0)
		{
			string failureMessage = ((parseFailures.Count > 0) ? ("以下链接无法解析：\n" + string.Join("\n", parseFailures.Take(5))) : "未能解析任何有效的链接。");
			MessageBox.Show(failureMessage, "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			UpdateStatusBar("链接解析失败");
			return;
		}
		if (!skipSave)
		{
			SaveNavigationState();
		}
		ApplySearchTypeDisplayForMatches(matches);
		List<NeteaseUrlMatch> normalizedForKey = normalizedMatches.Select((NormalizedUrlMatch n) => new NeteaseUrlMatch(n.Type, n.IdText, n.IdText)).ToList();
		string targetMixedKey = mixedQueryKeyOverride ?? BuildMixedQueryKey(normalizedForKey);
		string viewSource = "url:mixed:" + targetMixedKey;
		ViewLoadRequest request = new ViewLoadRequest(viewSource, "结果", "正在解析链接...", !skipSave);
		ViewLoadResult<MultiUrlViewData?> loadResult = await RunViewLoadAsync(request, async delegate(CancellationToken viewToken)
		{
			CancellationTokenSource linkedCts = null;
			CancellationToken effectiveToken = LinkCancellationTokens(viewToken, cancellationToken, out linkedCts);
			try
			{
				return await BuildMultiUrlViewDataAsync(normalizedMatches, parseFailures, effectiveToken).ConfigureAwait(continueOnCapturedContext: true);
			}
			finally
			{
				linkedCts?.Dispose();
			}
		}, "链接解析已取消").ConfigureAwait(continueOnCapturedContext: true);
		if (loadResult.IsCanceled)
		{
			return;
		}
		MultiUrlViewData data = loadResult.Value;
		object obj;
		if (data == null || data.Items.Count == 0)
		{
			if (data != null)
			{
				List<string> failures = data.Failures;
				if (failures != null && failures.Count > 0)
				{
					obj = "未能加载任何结果：\n" + string.Join("\n", data.Failures.Take(5));
					goto IL_042c;
				}
			}
			obj = "未能加载任何结果。";
			goto IL_042c;
		}
		_currentMixedQueryKey = targetMixedKey;
		foreach (SongInfo song in data.AggregatedSongs)
		{
			if (song != null)
			{
				song.ViewSource = viewSource;
			}
		}
		foreach (ListItemInfo item in data.Items)
		{
			if (item?.Song != null)
			{
				item.Song.ViewSource = viewSource;
			}
			else if (item?.PodcastEpisode?.Song != null)
			{
				item.PodcastEpisode.Song.ViewSource = viewSource;
			}
		}
		DisplayListItems(data.Items, viewSource, "结果");
		_currentSongs.Clear();
		if (data.AggregatedSongs.Count > 0)
		{
			_currentSongs.AddRange(data.AggregatedSongs);
		}
		UpdateStatusBar($"已加载 {data.Items.Count} 个链接结果");
		if (data.Failures.Count > 0)
		{
			IEnumerable<string> preview = data.Failures.Take(5);
			MessageBox.Show(string.Concat("部分链接未能加载：\n", str2: (data.Failures.Count > 5) ? "\n..." : string.Empty, str1: string.Join("\n", preview)), "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
		}
		FocusListAfterEnrich(0);
		return;
		IL_042c:
		string failureMessage2 = (string)obj;
		MessageBox.Show(failureMessage2, "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
		UpdateStatusBar("链接加载失败");
	}

	private async Task<MultiUrlViewData?> BuildMultiUrlViewDataAsync(List<NormalizedUrlMatch> normalizedMatches, List<string> initialFailures, CancellationToken cancellationToken)
	{
		List<ListItemInfo> listItems = new List<ListItemInfo>();
		List<SongInfo> aggregatedSongs = new List<SongInfo>();
		Dictionary<string, PlaylistInfo> playlistCache = new Dictionary<string, PlaylistInfo>(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, AlbumInfo> albumCache = new Dictionary<string, AlbumInfo>(StringComparer.OrdinalIgnoreCase);
		Dictionary<long, ArtistInfo> artistCache = new Dictionary<long, ArtistInfo>();
		List<string> failures = new List<string>(initialFailures);
		Dictionary<string, SongInfo> songMap = null;
		List<string> songIds = normalizedMatches.Where(delegate(NormalizedUrlMatch n)
		{
			NormalizedUrlMatch normalizedUrlMatch = n;
			return normalizedUrlMatch.Type == NeteaseUrlType.Song;
		}).Select(delegate(NormalizedUrlMatch n)
		{
			NormalizedUrlMatch normalizedUrlMatch = n;
			return normalizedUrlMatch.IdText;
		}).Distinct<string>(StringComparer.OrdinalIgnoreCase)
			.ToList();
		if (songIds.Count > 0)
		{
			List<SongInfo> songDetails = await _apiClient.GetSongDetailAsync(songIds.ToArray()).ConfigureAwait(continueOnCapturedContext: true);
			cancellationToken.ThrowIfCancellationRequested();
			if (songDetails != null)
			{
				songMap = songDetails.Where((SongInfo s) => !string.IsNullOrWhiteSpace(s.Id)).GroupBy<SongInfo, string>((SongInfo s) => s.Id, StringComparer.OrdinalIgnoreCase).ToDictionary<IGrouping<string, SongInfo>, string, SongInfo>((IGrouping<string, SongInfo> g) => g.Key, (IGrouping<string, SongInfo> g) => g.First(), StringComparer.OrdinalIgnoreCase);
			}
		}
		foreach (NormalizedUrlMatch normalized in normalizedMatches)
		{
			cancellationToken.ThrowIfCancellationRequested();
			SongInfo song;
			PlaylistInfo playlist;
			AlbumInfo album;
			ArtistInfo artist;
			switch (normalized.Type)
			{
			case NeteaseUrlType.Song:
				if (songMap != null && songMap.TryGetValue(normalized.IdText, out song) && song != null)
				{
					listItems.Add(new ListItemInfo
					{
						Type = ListItemType.Song,
						Song = song
					});
					aggregatedSongs.Add(song);
				}
				else
				{
					failures.Add(normalized.EntityName + "（" + normalized.IdText + "）");
				}
				break;
			case NeteaseUrlType.Playlist:
				if (!playlistCache.TryGetValue(normalized.IdText, out playlist) || playlist == null)
				{
					playlist = await _apiClient.GetPlaylistDetailAsync(normalized.IdText).ConfigureAwait(continueOnCapturedContext: true);
					cancellationToken.ThrowIfCancellationRequested();
					if (playlist != null)
					{
						playlistCache[normalized.IdText] = playlist;
					}
				}
				if (playlist != null)
				{
					listItems.Add(new ListItemInfo
					{
						Type = ListItemType.Playlist,
						Playlist = playlist
					});
				}
				else
				{
					failures.Add(normalized.EntityName + "（" + normalized.IdText + "）");
				}
				break;
			case NeteaseUrlType.Album:
				if (!albumCache.TryGetValue(normalized.IdText, out album) || album == null)
				{
					album = await _apiClient.GetAlbumDetailAsync(normalized.IdText).ConfigureAwait(continueOnCapturedContext: true);
					cancellationToken.ThrowIfCancellationRequested();
					if (album != null)
					{
						albumCache[normalized.IdText] = album;
					}
				}
				if (album != null)
				{
					listItems.Add(new ListItemInfo
					{
						Type = ListItemType.Album,
						Album = album
					});
				}
				else
				{
					failures.Add(normalized.EntityName + "（" + normalized.IdText + "）");
				}
				break;
			case NeteaseUrlType.Artist:
				if (!artistCache.TryGetValue(normalized.NumericId, out artist) || artist == null)
				{
					ArtistDetail detail = await _apiClient.GetArtistDetailAsync(normalized.NumericId).ConfigureAwait(continueOnCapturedContext: true);
					cancellationToken.ThrowIfCancellationRequested();
					if (detail != null)
					{
						artist = new ArtistInfo
						{
							Id = normalized.NumericId,
							Name = (string.IsNullOrWhiteSpace(detail.Name) ? $"歌手 {normalized.NumericId}" : detail.Name)
						};
						ApplyArtistDetailToArtist(artist, detail);
						artistCache[normalized.NumericId] = artist;
					}
				}
				if (artist != null)
				{
					listItems.Add(new ListItemInfo
					{
						Type = ListItemType.Artist,
						Artist = artist
					});
				}
				else
				{
					failures.Add(normalized.EntityName + "（" + normalized.IdText + "）");
				}
				break;
			case NeteaseUrlType.Podcast:
			{
				PodcastRadioInfo podcastDetail = await _apiClient.GetPodcastRadioDetailAsync(normalized.NumericId).ConfigureAwait(continueOnCapturedContext: true);
				cancellationToken.ThrowIfCancellationRequested();
				if (podcastDetail != null)
				{
					listItems.Add(new ListItemInfo
					{
						Type = ListItemType.Podcast,
						Podcast = podcastDetail
					});
				}
				else
				{
					failures.Add(normalized.EntityName + "（" + normalized.IdText + "）");
				}
				break;
			}
			case NeteaseUrlType.PodcastEpisode:
			{
				PodcastEpisodeInfo episodeDetail = await _apiClient.GetPodcastEpisodeDetailAsync(normalized.NumericId).ConfigureAwait(continueOnCapturedContext: true);
				cancellationToken.ThrowIfCancellationRequested();
				if (episodeDetail != null)
				{
					listItems.Add(new ListItemInfo
					{
						Type = ListItemType.PodcastEpisode,
						PodcastEpisode = episodeDetail
					});
					if (episodeDetail.Song != null)
					{
						aggregatedSongs.Add(episodeDetail.Song);
					}
				}
				else
				{
					failures.Add(normalized.EntityName + "（" + normalized.IdText + "）");
				}
				break;
			}
			default:
				failures.Add(normalized.EntityName + "（" + normalized.IdText + "）");
				break;
			}
			song = null;
			playlist = null;
			album = null;
			artist = null;
		}
		return new MultiUrlViewData(listItems, aggregatedSongs, failures);
	}

	private async Task<bool> RestoreMixedUrlStateAsync(string mixedQueryKey)
	{
		if (!TryParseMixedQueryKey(mixedQueryKey, out var matches) || matches.Count == 0)
		{
			MessageBox.Show("无法恢复混合链接结果。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			return false;
		}
		await HandleMultipleNeteaseUrlSearchAsync(matches, CancellationToken.None, skipSave: true, mixedQueryKey);
		return true;
	}

	private string MapUrlTypeToSearchType(NeteaseUrlType type)
	{
		switch (type)
		{
		case NeteaseUrlType.Playlist:
			return "歌单";
		case NeteaseUrlType.Album:
			return "专辑";
		case NeteaseUrlType.Artist:
			return "歌手";
		case NeteaseUrlType.Podcast:
		case NeteaseUrlType.PodcastEpisode:
			return "播客";
		default:
			return "歌曲";
		}
	}

	private bool TryValidateNeteaseResourceId(string? resourceId, string entityName, out long parsedId)
	{
		parsedId = 0L;
		if (string.IsNullOrWhiteSpace(resourceId) || !long.TryParse(resourceId, out parsedId) || parsedId <= 0)
		{
			MessageBox.Show(entityName + "链接格式不正确。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			UpdateStatusBar("无法解析" + entityName + "链接");
			return false;
		}
		return true;
	}
}
}
