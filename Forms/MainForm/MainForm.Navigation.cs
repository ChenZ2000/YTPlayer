#nullable disable
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Forms;
using YTPlayer.Models;
#pragma warning disable CS0219, CS4014, CS8632

namespace YTPlayer
{
public partial class MainForm
{
	private void SaveNavigationState()
	{
		if (_initialHomeLoadCts != null)
		{
			StopInitialHomeLoadLoop("保存导航状态前中断主页加载");
		}
		if (_currentSongs.Count == 0 && _currentPlaylists.Count == 0 && _currentAlbums.Count == 0 && _currentListItems.Count == 0 && _currentArtists.Count == 0 && _currentPodcasts.Count == 0 && _currentPodcastSounds.Count == 0)
		{
			return;
		}
		NavigationHistoryItem navigationHistoryItem = CreateCurrentState();
		if (_navigationHistory.Count > 0)
		{
			NavigationHistoryItem a = _navigationHistory.Peek();
			if (IsSameNavigationState(a, navigationHistoryItem))
			{
				_navigationHistory.Pop();
				_navigationHistory.Push(navigationHistoryItem);
				Debug.WriteLine($"[Navigation] 合并重复状态: {navigationHistoryItem.ViewName}, 类型={navigationHistoryItem.PageType}, 历史栈深度={_navigationHistory.Count}");
				return;
			}
		}
		_navigationHistory.Push(navigationHistoryItem);
		Debug.WriteLine($"[Navigation] 保存状态: {navigationHistoryItem.ViewName}, 类型={navigationHistoryItem.PageType}, 历史栈深度={_navigationHistory.Count}");
	}

	private void DebugLogListFocusState(string tag, string? viewSource = null, NavigationHistoryItem? state = null, int? resolvedIndex = null)
	{
		string source = viewSource ?? _currentViewSource ?? string.Empty;
		if (!string.Equals(source, "user_liked_songs", StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		int selectedIndex = GetSelectedListViewIndex();
		int focusedIndex = (resultListView != null && resultListView.FocusedItem != null) ? resultListView.FocusedItem.Index : -1;
		int selectedDataIndex = -1;
		if (resultListView != null && selectedIndex >= 0 && selectedIndex < resultListView.Items.Count && resultListView.Items[selectedIndex].Tag is int dataIndex)
		{
			selectedDataIndex = dataIndex;
		}
		string stateInfo = string.Empty;
		if (state != null)
		{
			stateInfo = $", stateSel={state.SelectedIndex}, stateData={state.SelectedDataIndex}, stateView={state.ViewSource}, stateType={state.PageType}";
		}
		string resolvedInfo = resolvedIndex.HasValue ? $", resolved={resolvedIndex.Value}" : string.Empty;
		Debug.WriteLine($"[NavDebug] {tag} view={source}, sel={selectedIndex}, focus={focusedIndex}, lastFocus={_lastListViewFocusedIndex}, dataIndex={selectedDataIndex}, items={resultListView?.Items.Count ?? -1}, pendingFocus={_pendingListFocusIndex}, pendingView={_pendingListFocusViewSource}{stateInfo}{resolvedInfo}");
	}

	private NavigationHistoryItem CreateCurrentState()
	{
		int selectedListViewIndex = GetSelectedListViewIndex();
		if (resultListView != null && resultListView.FocusedItem != null)
		{
			int focusedIndex = resultListView.FocusedItem.Index;
			if (focusedIndex >= 0)
			{
				selectedListViewIndex = focusedIndex;
			}
		}
		ListViewItem selectedListViewItemSafe = null;
		if (resultListView != null && selectedListViewIndex >= 0 && selectedListViewIndex < resultListView.Items.Count)
		{
			selectedListViewItemSafe = resultListView.Items[selectedListViewIndex];
		}
		else
		{
			selectedListViewItemSafe = GetSelectedListViewItemSafe();
		}
		if (selectedListViewIndex < 0 && resultListView != null && _lastListViewFocusedIndex >= 0 && _lastListViewFocusedIndex < resultListView.Items.Count)
		{
			selectedListViewIndex = _lastListViewFocusedIndex;
			selectedListViewItemSafe = resultListView.Items[selectedListViewIndex];
		}
		NavigationHistoryItem navigationHistoryItem = new NavigationHistoryItem
		{
			ViewSource = _currentViewSource,
			ViewName = resultListView.AccessibleName,
			SelectedIndex = selectedListViewIndex,
			SelectedDataIndex = -1
		};
		if (selectedListViewItemSafe != null && selectedListViewItemSafe.Tag is int num && num >= 0)
		{
			navigationHistoryItem.SelectedDataIndex = num;
		}
		DebugLogListFocusState("CreateCurrentState", _currentViewSource, navigationHistoryItem);
		if (_isHomePage || string.Equals(_currentViewSource, "homepage", StringComparison.OrdinalIgnoreCase))
		{
			navigationHistoryItem.PageType = "homepage";
		}
		else if (_currentViewSource.StartsWith("playlist:", StringComparison.OrdinalIgnoreCase))
		{
			navigationHistoryItem.PageType = "playlist";
			navigationHistoryItem.PlaylistId = _currentViewSource.Substring("playlist:".Length);
		}
		else if (_currentViewSource.StartsWith("album:", StringComparison.OrdinalIgnoreCase))
		{
			navigationHistoryItem.PageType = "album";
			navigationHistoryItem.AlbumId = _currentViewSource.Substring("album:".Length);
		}
		else if (_currentViewSource.StartsWith("search:", StringComparison.OrdinalIgnoreCase))
		{
			navigationHistoryItem.PageType = "search";
			ParseSearchViewSource(_currentViewSource, out var searchType, out var keyword, out var page);
			navigationHistoryItem.SearchType = ((!string.IsNullOrWhiteSpace(searchType)) ? searchType : _currentSearchType);
			navigationHistoryItem.SearchKeyword = ((!string.IsNullOrWhiteSpace(keyword)) ? keyword : _lastKeyword);
			navigationHistoryItem.CurrentPage = ((page > 0) ? page : _currentPage);
		}
		else if (_currentViewSource.StartsWith("artist_entries:", StringComparison.OrdinalIgnoreCase))
		{
			navigationHistoryItem.PageType = "artist_entries";
			navigationHistoryItem.ArtistId = ParseArtistIdFromViewSource(_currentViewSource, "artist_entries:");
			navigationHistoryItem.ArtistName = _currentArtist?.Name ?? _currentArtistDetail?.Name ?? string.Empty;
		}
		else if (_currentViewSource.StartsWith("artist_songs_top:", StringComparison.OrdinalIgnoreCase))
		{
			navigationHistoryItem.PageType = "artist_top";
			navigationHistoryItem.ArtistId = ParseArtistIdFromViewSource(_currentViewSource, "artist_songs_top:");
			navigationHistoryItem.ArtistName = _currentArtist?.Name ?? _currentArtistDetail?.Name ?? string.Empty;
		}
		else if (_currentViewSource.StartsWith("artist_songs:", StringComparison.OrdinalIgnoreCase))
		{
			navigationHistoryItem.PageType = "artist_songs";
			ParseArtistListViewSource(_currentViewSource, out var artistId, out var offset, out var order);
			navigationHistoryItem.ArtistId = artistId;
			navigationHistoryItem.ArtistOffset = offset;
			navigationHistoryItem.ArtistOrder = order;
			navigationHistoryItem.ArtistName = _currentArtist?.Name ?? _currentArtistDetail?.Name ?? string.Empty;
		}
		else if (_currentViewSource.StartsWith("artist_albums:", StringComparison.OrdinalIgnoreCase))
		{
			navigationHistoryItem.PageType = "artist_albums";
			ParseArtistListViewSource(_currentViewSource, out var artistId2, out var offset2, out var order2, "latest");
			navigationHistoryItem.ArtistId = artistId2;
			navigationHistoryItem.ArtistOffset = offset2;
			navigationHistoryItem.ArtistAlbumSort = order2;
			navigationHistoryItem.ArtistName = _currentArtist?.Name ?? _currentArtistDetail?.Name ?? string.Empty;
		}
		else if (string.Equals(_currentViewSource, "artist_favorites", StringComparison.OrdinalIgnoreCase))
		{
			navigationHistoryItem.PageType = "artist_favorites";
		}
		else if (string.Equals(_currentViewSource, "artist_category_types", StringComparison.OrdinalIgnoreCase))
		{
			navigationHistoryItem.PageType = "artist_category_types";
		}
		else if (_currentViewSource.StartsWith("artist_category_type:", StringComparison.OrdinalIgnoreCase))
		{
			navigationHistoryItem.PageType = "artist_category_type";
			navigationHistoryItem.ArtistType = checked((int)ParseArtistIdFromViewSource(_currentViewSource, "artist_category_type:"));
		}
		else if (_currentViewSource.StartsWith("artist_category_list:", StringComparison.OrdinalIgnoreCase))
		{
			navigationHistoryItem.PageType = "artist_category_list";
			ParseArtistCategoryListViewSource(_currentViewSource, out var typeCode, out var areaCode, out var offset3);
			navigationHistoryItem.ArtistType = typeCode;
			navigationHistoryItem.ArtistArea = areaCode;
			navigationHistoryItem.ArtistOffset = offset3;
		}
		else if (string.Equals(_currentViewSource, "new_album_category_periods", StringComparison.OrdinalIgnoreCase))
		{
			navigationHistoryItem.PageType = "new_album_category_periods";
		}
		else if (_currentViewSource.StartsWith("new_album_category_period:", StringComparison.OrdinalIgnoreCase))
		{
			navigationHistoryItem.PageType = "new_album_category_period";
			navigationHistoryItem.AlbumCategoryType = checked((int)ParseArtistIdFromViewSource(_currentViewSource, "new_album_category_period:"));
		}
		else if (_currentViewSource.StartsWith("new_album_category_list:", StringComparison.OrdinalIgnoreCase))
		{
			navigationHistoryItem.PageType = "new_album_category_list";
			ParseAlbumCategoryListViewSource(_currentViewSource, out var typeCode2, out var areaCode2, out var offset4);
			navigationHistoryItem.AlbumCategoryType = typeCode2;
			navigationHistoryItem.AlbumCategoryArea = areaCode2;
			navigationHistoryItem.AlbumCategoryOffset = offset4;
		}
		else if (_currentViewSource.StartsWith("podcast:", StringComparison.OrdinalIgnoreCase))
		{
			navigationHistoryItem.PageType = "podcast";
			ParsePodcastViewSource(_currentViewSource, out var radioId, out var offset5, out var ascending);
			navigationHistoryItem.PodcastRadioId = radioId;
			navigationHistoryItem.PodcastOffset = offset5;
			navigationHistoryItem.PodcastRadioName = _currentPodcast?.Name ?? string.Empty;
			navigationHistoryItem.PodcastAscending = ascending;
		}
		else if (_currentViewSource.StartsWith("url:mixed", StringComparison.OrdinalIgnoreCase))
		{
			navigationHistoryItem.PageType = "url_mixed";
			navigationHistoryItem.MixedQueryKey = _currentMixedQueryKey ?? string.Empty;
		}
		else if (_currentViewSource.StartsWith("url:song:", StringComparison.OrdinalIgnoreCase))
		{
			navigationHistoryItem.PageType = "url_song";
			navigationHistoryItem.SongId = _currentViewSource.Substring("url:song:".Length);
		}
		else
		{
			navigationHistoryItem.PageType = "category";
			navigationHistoryItem.CategoryId = _currentViewSource;
		}
		return navigationHistoryItem;
	}

	private static void ParseSearchViewSource(string? viewSource, out string searchType, out string keyword, out int page)
	{
		searchType = string.Empty;
		keyword = string.Empty;
		page = 1;
		if (string.IsNullOrWhiteSpace(viewSource) || !viewSource.StartsWith("search:", StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		string text = viewSource.Substring("search:".Length);
		string text2 = null;
		if (text.StartsWith("artist:", StringComparison.OrdinalIgnoreCase))
		{
			text2 = "artist";
			text = text.Substring("artist:".Length);
		}
		else if (text.StartsWith("album:", StringComparison.OrdinalIgnoreCase))
		{
			text2 = "album";
			text = text.Substring("album:".Length);
		}
		else if (text.StartsWith("playlist:", StringComparison.OrdinalIgnoreCase))
		{
			text2 = "playlist";
			text = text.Substring("playlist:".Length);
		}
		else if (text.StartsWith("podcast:", StringComparison.OrdinalIgnoreCase))
		{
			text2 = "podcast";
			text = text.Substring("podcast:".Length);
		}
		bool flag = false;
		if (1 == 0)
		{
		}
		string text3 = text2 switch
		{
			"artist" => "歌手", 
			"album" => "专辑", 
			"playlist" => "歌单", 
			"podcast" => "播客", 
			_ => "歌曲", 
		};
		if (1 == 0)
		{
		}
		string text4 = text3;
		bool flag2 = false;
		searchType = text4;
		int num = text.LastIndexOf(":page", StringComparison.OrdinalIgnoreCase);
		checked
		{
			if (num >= 0 && num + 5 < text.Length)
			{
				string s = text.Substring(num + 5);
				if (int.TryParse(s, out var result) && result > 0)
				{
					page = result;
					text = text.Substring(0, num);
				}
			}
			keyword = (string.IsNullOrWhiteSpace(text) ? string.Empty : text);
		}
	}

	private static bool IsSameNavigationState(NavigationHistoryItem a, NavigationHistoryItem b)
	{
		if (a == null || b == null)
		{
			return false;
		}
		if (!string.Equals(a.PageType, b.PageType, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		switch (a.PageType)
		{
		case "homepage":
			return true;
		case "category":
			return string.Equals(a.CategoryId, b.CategoryId, StringComparison.OrdinalIgnoreCase);
		case "playlist":
			return string.Equals(a.PlaylistId, b.PlaylistId, StringComparison.OrdinalIgnoreCase);
		case "album":
			return string.Equals(a.AlbumId, b.AlbumId, StringComparison.OrdinalIgnoreCase);
		case "search":
			return string.Equals(a.SearchKeyword, b.SearchKeyword, StringComparison.OrdinalIgnoreCase) && string.Equals(a.SearchType, b.SearchType, StringComparison.OrdinalIgnoreCase) && a.CurrentPage == b.CurrentPage;
		case "artist_entries":
		case "artist_top":
			return a.ArtistId == b.ArtistId;
		case "artist_songs":
			return a.ArtistId == b.ArtistId && a.ArtistOffset == b.ArtistOffset && string.Equals(a.ArtistOrder, b.ArtistOrder, StringComparison.OrdinalIgnoreCase);
		case "artist_albums":
			return a.ArtistId == b.ArtistId && a.ArtistOffset == b.ArtistOffset && string.Equals(a.ArtistAlbumSort, b.ArtistAlbumSort, StringComparison.OrdinalIgnoreCase);
		case "artist_favorites":
		case "artist_category_types":
			return true;
		case "artist_category_type":
			return a.ArtistType == b.ArtistType;
		case "artist_category_list":
			return a.ArtistType == b.ArtistType && a.ArtistArea == b.ArtistArea && a.ArtistOffset == b.ArtistOffset;
		case "new_album_category_periods":
			return true;
		case "new_album_category_period":
			return a.AlbumCategoryType == b.AlbumCategoryType;
		case "new_album_category_list":
			return a.AlbumCategoryType == b.AlbumCategoryType && a.AlbumCategoryArea == b.AlbumCategoryArea && a.AlbumCategoryOffset == b.AlbumCategoryOffset;
		case "podcast":
			return a.PodcastRadioId == b.PodcastRadioId && a.PodcastOffset == b.PodcastOffset && a.PodcastAscending == b.PodcastAscending;
		case "url_song":
			return string.Equals(a.SongId, b.SongId, StringComparison.OrdinalIgnoreCase);
		case "url_mixed":
			return string.Equals(a.MixedQueryKey, b.MixedQueryKey, StringComparison.OrdinalIgnoreCase);
		default:
			return string.Equals(a.ViewSource, b.ViewSource, StringComparison.OrdinalIgnoreCase);
		}
	}

	private async Task GoBackAsync()
	{
		DateTime now = DateTime.UtcNow;
		double elapsed = (now - _lastBackTime).TotalMilliseconds;
		if (elapsed < 300.0)
		{
			Debug.WriteLine($"[Navigation] \ud83d\uded1 防抖拦截：距上次后退仅 {elapsed:F0}ms");
			return;
		}
		if (_isNavigating)
		{
			Debug.WriteLine("[Navigation] \ud83d\uded1 并发拦截：已有导航操作正在执行，标记 pendingBack 并尝试取消当前视图");
			_pendingBackNavigation = true;
			CancelViewContentOperation();
			CancelNavigationOperation();
			return;
		}
		try
		{
			_isNavigating = true;
			_lastBackTime = now;
			if (_navigationHistory.Count == 0)
			{
				Debug.WriteLine("[Navigation] 导航历史为空，返回主页");
				if (!_isHomePage)
				{
					await LoadHomePageAsync();
				}
				else
				{
					UpdateStatusBar("已经在主页了");
				}
				return;
			}
			NavigationHistoryItem state = _navigationHistory.Peek();
			Debug.WriteLine($"[Navigation] 尝试后退到: {state.ViewName}, 类型={state.PageType}, 当前历史={_navigationHistory.Count}");
			if (await RestoreNavigationStateAsync(state))
			{
				_navigationHistory.Pop();
				Debug.WriteLine($"[Navigation] 后退成功: {state.ViewName}, 剩余历史={_navigationHistory.Count}");
			}
			else
			{
				UpdateStatusBar("返回失败，已保持当前页面");
			}
		}
		finally
		{
			_isNavigating = false;
			if (_pendingBackNavigation)
			{
				_pendingBackNavigation = false;
				Debug.WriteLine("[Navigation] 处理 pendingBack");
				Task.Run(async delegate
				{
					await GoBackAsync();
				});
			}
		}
	}

	private async Task<bool> RestoreNavigationStateAsync(NavigationHistoryItem state)
	{
		string previousViewSource = _currentViewSource ?? string.Empty;
		int previousAutoFocusDepth = _autoFocusSuppressionDepth;
		checked
		{
			_autoFocusSuppressionDepth++;
			bool handledByView = false;
			try
			{
				switch (state.PageType)
				{
				case "homepage":
					await LoadHomePageAsync(skipSave: true);
					break;
				case "category":
					int pendingCategoryIndex = (state.SelectedDataIndex >= 0) ? state.SelectedDataIndex : state.SelectedIndex;
					string normalizedCategoryId = StripOffsetSuffix(state.CategoryId ?? string.Empty, out int categoryOffset);
					if (string.Equals(normalizedCategoryId, "toplist", StringComparison.OrdinalIgnoreCase))
					{
						await LoadToplist(pendingCategoryIndex, categoryOffset, skipSave: true);
					}
					else if (string.Equals(state.CategoryId, "daily_recommend", StringComparison.OrdinalIgnoreCase))
					{
						await LoadDailyRecommend(pendingCategoryIndex);
					}
					else if (string.Equals(state.CategoryId, "personalized", StringComparison.OrdinalIgnoreCase))
					{
						await LoadPersonalized(pendingCategoryIndex);
					}
					else if (string.Equals(normalizedCategoryId, PersonalFmCategoryId, StringComparison.OrdinalIgnoreCase))
					{
						await LoadPersonalFm(pendingCategoryIndex);
					}
					else if (string.Equals(state.CategoryId, "daily_recommend_songs", StringComparison.OrdinalIgnoreCase))
					{
						await LoadDailyRecommendSongs(pendingCategoryIndex);
					}
					else if (string.Equals(state.CategoryId, "daily_recommend_playlists", StringComparison.OrdinalIgnoreCase))
					{
						await LoadDailyRecommendPlaylists(pendingCategoryIndex);
					}
					else if (string.Equals(state.CategoryId, "personalized_playlists", StringComparison.OrdinalIgnoreCase))
					{
						await LoadPersonalizedPlaylists(pendingCategoryIndex);
					}
					else if (string.Equals(state.CategoryId, "personalized_newsongs", StringComparison.OrdinalIgnoreCase))
					{
						await LoadPersonalizedNewSongs(pendingCategoryIndex);
					}
					else if (string.Equals(state.CategoryId, "personalized_newsong", StringComparison.OrdinalIgnoreCase))
					{
						await LoadPersonalizedNewSong(pendingCategoryIndex);
					}
					else if (string.Equals(normalizedCategoryId, "highquality_playlists", StringComparison.OrdinalIgnoreCase))
					{
						await LoadHighQualityPlaylists(categoryOffset, skipSave: true, pendingFocusIndex: pendingCategoryIndex);
					}
					else if (string.Equals(state.CategoryId, "recent_listened", StringComparison.OrdinalIgnoreCase))
					{
						await LoadRecentListenedCategoryAsync(skipSave: true, pendingFocusIndex: pendingCategoryIndex);
					}
					else if (string.Equals(state.CategoryId, "recent_play", StringComparison.OrdinalIgnoreCase))
					{
						await LoadRecentPlayedSongsAsync(pendingCategoryIndex);
					}
					else if (string.Equals(state.CategoryId, "recent_playlists", StringComparison.OrdinalIgnoreCase))
					{
						await LoadRecentPlaylistsAsync(pendingCategoryIndex);
					}
					else if (string.Equals(state.CategoryId, "recent_albums", StringComparison.OrdinalIgnoreCase))
					{
						await LoadRecentAlbumsAsync(pendingCategoryIndex);
					}
					else if (string.Equals(state.CategoryId, "recent_podcasts", StringComparison.OrdinalIgnoreCase))
					{
						await LoadRecentPodcastsAsync(pendingCategoryIndex);
					}
					else
					{
						await LoadCategoryContent(state.CategoryId, skipSave: true);
					}
					break;
				case "playlist":
					await LoadPlaylistById(state.PlaylistId, skipSave: true);
					break;
				case "album":
					await LoadAlbumById(state.AlbumId, skipSave: true);
					break;
				case "url_song":
					if (!(await LoadSongFromUrlAsync(state.SongId, skipSave: true)))
					{
						return false;
					}
					break;
				case "url_mixed":
					if (!(await RestoreMixedUrlStateAsync(state.MixedQueryKey)))
					{
						return false;
					}
					break;
				case "search":
					await LoadSearchResults(state.SearchKeyword, state.SearchType, state.CurrentPage, skipSave: true, (state.SelectedDataIndex >= 0) ? state.SelectedDataIndex : state.SelectedIndex);
					handledByView = true;
					break;
				case "artist_entries":
					if (state.ArtistId > 0)
					{
						ArtistInfo artistInfo = new ArtistInfo
						{
							Id = state.ArtistId,
							Name = state.ArtistName
						};
						await OpenArtistAsync(artistInfo, skipSave: true);
					}
					else
					{
						await LoadArtistCategoryTypesAsync(skipSave: true);
					}
					break;
				case "artist_top":
					if (state.ArtistId > 0)
					{
						await LoadArtistTopSongsAsync(state.ArtistId, skipSave: true);
					}
					break;
				case "artist_songs":
					if (state.ArtistId > 0)
					{
						await LoadArtistSongsAsync(orderOverride: ResolveArtistSongsOrder(state.ArtistOrder), artistId: state.ArtistId, offset: state.ArtistOffset, skipSave: true);
					}
					break;
				case "artist_albums":
					if (state.ArtistId > 0)
					{
						await LoadArtistAlbumsAsync(sortOverride: ResolveArtistAlbumSort(state.ArtistAlbumSort), artistId: state.ArtistId, offset: state.ArtistOffset, skipSave: true);
					}
					break;
				case "artist_favorites":
					await LoadArtistFavoritesAsync(skipSave: true);
					break;
				case "artist_category_types":
					await LoadArtistCategoryTypesAsync(skipSave: true);
					break;
				case "artist_category_type":
					await LoadArtistCategoryAreasAsync(state.ArtistType, skipSave: true);
					break;
				case "artist_category_list":
					await LoadArtistsByCategoryAsync(state.ArtistType, state.ArtistArea, state.ArtistOffset, skipSave: true, pendingFocusIndex: state.SelectedIndex);
					handledByView = true;
					break;
				case "new_album_category_periods":
					await LoadAlbumCategoryTypesAsync(skipSave: true);
					break;
				case "new_album_category_period":
					await LoadAlbumCategoryAreasAsync(state.AlbumCategoryType, skipSave: true);
					break;
				case "new_album_category_list":
					await LoadAlbumsByCategoryAsync(state.AlbumCategoryType, state.AlbumCategoryArea, state.AlbumCategoryOffset, skipSave: true, pendingFocusIndex: state.SelectedIndex);
					handledByView = true;
					break;
				case "podcast":
					if (state.PodcastRadioId <= 0)
					{
						return false;
					}
					await LoadPodcastEpisodesAsync(state.PodcastRadioId, state.PodcastOffset, skipSave: true, null, state.PodcastAscending);
					break;
				default:
					Debug.WriteLine("[Navigation] 未知的页面类型: " + state.PageType);
					UpdateStatusBar("无法恢复页面");
					return false;
				}
				if (!IsNavigationStateApplied(state))
				{
					Debug.WriteLine("[Navigation] 页面状态未切换，当前 view=" + _currentViewSource + ", 期望=" + state.ViewSource);
					return false;
				}
				if (handledByView)
				{
					UpdateStatusBar("返回到: " + state.ViewName);
					return true;
				}
				DebugLogListFocusState("Restore: after load", _currentViewSource, state);
				int resolvedIndex = -1;
				if (state.SelectedIndex >= 0 && state.SelectedIndex < resultListView.Items.Count)
				{
					resolvedIndex = state.SelectedIndex;
				}
				else if (state.SelectedDataIndex >= 0 && resultListView.Items.Count > 0)
				{
					for (int i = 0; i < resultListView.Items.Count; i++)
					{
						if (resultListView.Items[i].Tag is int dataIndex && dataIndex == state.SelectedDataIndex)
						{
							resolvedIndex = i;
							break;
						}
					}
					if (resolvedIndex < 0 && state.SelectedDataIndex < resultListView.Items.Count)
					{
						resolvedIndex = state.SelectedDataIndex;
					}
				}
				else if (resultListView.Items.Count > 0)
				{
					resolvedIndex = Math.Min(Math.Max(state.SelectedIndex, 0), resultListView.Items.Count - 1);
				}
				DebugLogListFocusState("Restore: resolved", _currentViewSource, state, resolvedIndex);
				if (resolvedIndex >= 0 && resolvedIndex < resultListView.Items.Count)
				{
					resultListView.BeginUpdate();
					ClearListViewSelection();
					ListViewItem targetItem = resultListView.Items[resolvedIndex];
					targetItem.Selected = true;
					targetItem.Focused = true;
					targetItem.EnsureVisible();
					EndListViewUpdateAndRefreshAccessibility();
					resultListView.Focus();
					_lastListViewFocusedIndex = resolvedIndex;
				}
				else
				{
					resultListView.Focus();
				}
				DebugLogListFocusState("Restore: applied", _currentViewSource, state, resolvedIndex);
				_pendingListFocusIndex = -1;
				_pendingListFocusViewSource = null;
				_pendingListFocusFromPlayback = false;
				UpdateStatusBar("返回到: " + state.ViewName);
				return true;
			}
			catch (Exception ex)
			{
				Exception ex2 = ex;
				Exception ex3 = ex2;
				Debug.WriteLine($"[Navigation] 恢复状态失败: {ex3}");
				MessageBox.Show("返回失败: " + ex3.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("返回失败");
				_currentViewSource = previousViewSource;
				return false;
			}
			finally
			{
				_autoFocusSuppressionDepth = previousAutoFocusDepth;
			}
		}
	}

	private bool IsNavigationStateApplied(NavigationHistoryItem state)
	{
		if (state == null)
		{
			return false;
		}
		if (!string.IsNullOrWhiteSpace(state.ViewSource) && string.Equals(_currentViewSource, state.ViewSource, StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		switch (state.PageType)
		{
		case "homepage":
			return _isHomePage || string.Equals(_currentViewSource, "homepage", StringComparison.OrdinalIgnoreCase);
		case "category":
			return string.Equals(_currentViewSource, state.CategoryId, StringComparison.OrdinalIgnoreCase);
		case "playlist":
			return string.Equals(_currentViewSource, "playlist:" + state.PlaylistId, StringComparison.OrdinalIgnoreCase);
		case "album":
			return string.Equals(_currentViewSource, "album:" + state.AlbumId, StringComparison.OrdinalIgnoreCase);
		case "artist_entries":
		case "artist_top":
			return state.ArtistId > 0 && (_currentViewSource ?? string.Empty).IndexOf(state.ArtistId.ToString(), StringComparison.OrdinalIgnoreCase) >= 0;
		case "artist_songs":
		{
			if (state.ArtistId <= 0)
			{
				return false;
			}
			string b = $"artist_songs:{state.ArtistId}:order{state.ArtistOrder}:offset{state.ArtistOffset}";
			if (string.Equals(_currentViewSource, b, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
			return string.Equals(_currentViewSource, $"artist_songs:{state.ArtistId}:offset{state.ArtistOffset}", StringComparison.OrdinalIgnoreCase);
		}
		case "artist_albums":
		{
			if (state.ArtistId <= 0)
			{
				return false;
			}
			string b2 = $"artist_albums:{state.ArtistId}:order{state.ArtistAlbumSort}:offset{state.ArtistOffset}";
			if (string.Equals(_currentViewSource, b2, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
			return string.Equals(_currentViewSource, $"artist_albums:{state.ArtistId}:offset{state.ArtistOffset}", StringComparison.OrdinalIgnoreCase);
		}
		case "artist_favorites":
			return string.Equals(_currentViewSource, "artist_favorites", StringComparison.OrdinalIgnoreCase);
		case "artist_category_types":
			return string.Equals(_currentViewSource, "artist_category_types", StringComparison.OrdinalIgnoreCase);
		case "artist_category_type":
			return string.Equals(_currentViewSource, $"artist_category_type:{state.ArtistType}", StringComparison.OrdinalIgnoreCase);
		case "artist_category_list":
			return string.Equals(_currentViewSource, $"artist_category_list:{state.ArtistType}:{state.ArtistArea}:offset{state.ArtistOffset}", StringComparison.OrdinalIgnoreCase);
		case "new_album_category_periods":
			return string.Equals(_currentViewSource, "new_album_category_periods", StringComparison.OrdinalIgnoreCase);
		case "new_album_category_period":
			return string.Equals(_currentViewSource, $"new_album_category_period:{state.AlbumCategoryType}", StringComparison.OrdinalIgnoreCase);
		case "new_album_category_list":
			return string.Equals(_currentViewSource, $"new_album_category_list:{state.AlbumCategoryType}:{state.AlbumCategoryArea}:offset{state.AlbumCategoryOffset}", StringComparison.OrdinalIgnoreCase);
		case "podcast":
		{
			ParsePodcastViewSource(_currentViewSource, out var radioId, out var offset, out var ascending);
			return radioId == state.PodcastRadioId && offset == state.PodcastOffset && ascending == state.PodcastAscending;
		}
		case "url_song":
			return string.Equals(_currentViewSource, "url:song:" + state.SongId, StringComparison.OrdinalIgnoreCase);
		case "url_mixed":
			return string.Equals(_currentMixedQueryKey, state.MixedQueryKey, StringComparison.OrdinalIgnoreCase);
		default:
			return string.Equals(_currentViewSource, state.ViewSource, StringComparison.OrdinalIgnoreCase);
		}
	}
}
}
