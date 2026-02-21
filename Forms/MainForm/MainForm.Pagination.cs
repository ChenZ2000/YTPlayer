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
#pragma warning disable CS0219, CS8600, CS8601, CS8602, CS8603, CS8604, CS8618, CS8622, CS8625, CS8632, CS4014

namespace YTPlayer
{
public partial class MainForm
{
        private async Task OnPrevPageAsync()
        {
		checked
		{
			if (!string.IsNullOrEmpty(_currentViewSource) && _currentViewSource.StartsWith("search:", StringComparison.OrdinalIgnoreCase))
			{
				if (_currentPage > 1)
				{
					int targetPage = _currentPage - 1;
					if (!(await ReloadCurrentSearchPageAsync(targetPage)))
					{
						UpdateStatusBar("没有可用的上一页数据");
					}
				}
				else
				{
					UpdateStatusBar("已经是第一页");
				}
			}
			else if (!string.IsNullOrEmpty(_currentViewSource) && _currentViewSource.StartsWith("artist_songs:", StringComparison.OrdinalIgnoreCase))
			{
				ParseArtistListViewSource(_currentViewSource, out var artistId, out var offset, out var order);
				if (offset <= 0)
				{
					UpdateStatusBar("已经是第一页");
				}
				else
				{
					await LoadArtistSongsAsync(offset: Math.Max(0, offset - 100), artistId: artistId, skipSave: true, orderOverride: ResolveArtistSongsOrder(order));
				}
			}
			else if (!string.IsNullOrEmpty(_currentViewSource) && _currentViewSource.StartsWith("artist_albums:", StringComparison.OrdinalIgnoreCase))
			{
				ParseArtistListViewSource(_currentViewSource, out var artistId2, out var offset2, out var order2, "latest");
				if (offset2 <= 0)
				{
					UpdateStatusBar("已经是第一页");
				}
				else
				{
					await LoadArtistAlbumsAsync(offset: Math.Max(0, offset2 - 100), artistId: artistId2, skipSave: true, sortOverride: ResolveArtistAlbumSort(order2));
				}
			}
			else if (!string.IsNullOrEmpty(_currentViewSource) && _currentViewSource.StartsWith("artist_category_list:", StringComparison.OrdinalIgnoreCase))
			{
				ParseArtistCategoryListViewSource(_currentViewSource, out var typeCode, out var areaCode, out var offset3);
				if (offset3 <= 0)
				{
					UpdateStatusBar("已经是第一页");
				}
				else
				{
					await LoadArtistsByCategoryAsync(offset: Math.Max(0, offset3 - 100), typeCode: typeCode, areaCode: areaCode, skipSave: true);
				}
			}
			else if (!string.IsNullOrEmpty(_currentViewSource) && _currentViewSource.StartsWith("podcast:", StringComparison.OrdinalIgnoreCase))
			{
				ParsePodcastViewSource(_currentViewSource, out var podcastId, out var offset4, out var ascending);
				if (podcastId <= 0)
				{
					UpdateStatusBar("无法定位播客页码");
				}
				else if (offset4 <= 0)
				{
					UpdateStatusBar("已经是第一页");
				}
				else
				{
					await LoadPodcastEpisodesAsync(offset: Math.Max(0, offset4 - 50), radioId: podcastId, skipSave: true, podcastInfo: null, sortAscendingOverride: ascending);
				}
			}
			else if (!string.IsNullOrEmpty(_currentViewSource) && _currentViewSource.StartsWith("podcast_cat_", StringComparison.OrdinalIgnoreCase))
			{
				ParsePodcastCategoryViewSource(_currentViewSource, out var categoryId, out var offset5);
				if (offset5 <= 0)
				{
					UpdateStatusBar("已经是第一页");
				}
				else
				{
					await LoadPodcastsByCategoryAsync(categoryId, Math.Max(0, offset5 - 50), skipSave: true);
				}
			}
			else if (!string.IsNullOrEmpty(_currentViewSource) && _currentViewSource.StartsWith("playlist_cat_", StringComparison.OrdinalIgnoreCase))
			{
				ParsePlaylistCategoryViewSource(_currentViewSource, out var catName, out var offset6);
				if (offset6 <= 0)
				{
					UpdateStatusBar("已经是第一页");
				}
				else
				{
					await LoadPlaylistsByCat(catName, Math.Max(0, offset6 - PlaylistCategoryPageSize), skipSave: true);
				}
			}
			else if (!string.IsNullOrEmpty(_currentViewSource) && _currentViewSource.StartsWith("new_songs_", StringComparison.OrdinalIgnoreCase))
			{
				if (!TryParseNewSongsViewSource(_currentViewSource, out var areaType, out var areaName, out var offset7, out var baseSource))
				{
					UpdateStatusBar("无法定位新歌页码");
				}
				else if (offset7 <= 0)
				{
					UpdateStatusBar("已经是第一页");
				}
				else
				{
					await LoadNewSongsByArea(areaType, areaName, baseSource, Math.Max(0, offset7 - NewSongsPageSize), skipSave: true);
				}
			}
			else if (!string.IsNullOrEmpty(_currentViewSource) && _currentViewSource.StartsWith("highquality_playlists", StringComparison.OrdinalIgnoreCase))
			{
				ParseHighQualityViewSource(_currentViewSource, out var offset8);
				if (offset8 <= 0)
				{
					UpdateStatusBar("已经是第一页");
				}
				else
				{
					await LoadHighQualityPlaylists(Math.Max(0, offset8 - HighQualityPlaylistsPageSize), skipSave: true);
				}
			}
			else if (!string.IsNullOrEmpty(_currentViewSource) && _currentViewSource.StartsWith("toplist", StringComparison.OrdinalIgnoreCase))
			{
				ParseToplistViewSource(_currentViewSource, out var offset9);
				if (offset9 <= 0)
				{
					UpdateStatusBar("已经是第一页");
				}
				else
				{
					await LoadToplist(offset: Math.Max(0, offset9 - ToplistPageSize), skipSave: true);
				}
			}
			else if (string.Equals(_currentViewSource, "user_cloud", StringComparison.OrdinalIgnoreCase))
			{
				if (_cloudPage <= 1)
				{
					UpdateStatusBar("已经是第一页");
					return;
				}
				_cloudPage = Math.Max(1, _cloudPage - 1);
				await LoadCloudSongsAsync(skipSave: true);
			}
			else
			{
				UpdateStatusBar("当前内容不支持翻页");
			}
		}
	}

        private async Task OnNextPageAsync()
        {
		checked
		{
			if (!string.IsNullOrEmpty(_currentViewSource) && _currentViewSource.StartsWith("search:", StringComparison.OrdinalIgnoreCase))
			{
				if (!_hasNextSearchPage && _currentPage >= _maxPage)
				{
					UpdateStatusBar("已经是最后一页");
					return;
				}
				int targetPage = _currentPage + 1;
				if (_maxPage > 0)
				{
					targetPage = Math.Min(targetPage, _maxPage);
				}
				if (!(await ReloadCurrentSearchPageAsync(targetPage)))
				{
					UpdateStatusBar("无法加载下一页数据");
				}
			}
			else if (!string.IsNullOrEmpty(_currentViewSource) && _currentViewSource.StartsWith("artist_songs:", StringComparison.OrdinalIgnoreCase))
			{
				if (!_currentArtistSongsHasMore)
				{
					UpdateStatusBar("已经是最后一页");
					return;
				}
				ParseArtistListViewSource(_currentViewSource, out var artistId, out var offset, out var order);
				int newOffset = offset + 100;
				await LoadArtistSongsAsync(artistId, newOffset, skipSave: true, ResolveArtistSongsOrder(order));
			}
			else if (!string.IsNullOrEmpty(_currentViewSource) && _currentViewSource.StartsWith("artist_albums:", StringComparison.OrdinalIgnoreCase))
			{
				if (!_currentArtistAlbumsHasMore)
				{
					UpdateStatusBar("已经是最后一页");
					return;
				}
				ParseArtistListViewSource(_currentViewSource, out var artistId2, out var offset2, out var order2, "latest");
				int newOffset2 = offset2 + 100;
				await LoadArtistAlbumsAsync(artistId2, newOffset2, skipSave: true, ResolveArtistAlbumSort(order2));
			}
			else if (!string.IsNullOrEmpty(_currentViewSource) && _currentViewSource.StartsWith("artist_category_list:", StringComparison.OrdinalIgnoreCase))
			{
				if (!_currentArtistCategoryHasMore)
				{
					UpdateStatusBar("已经是最后一页");
					return;
				}
				ParseArtistCategoryListViewSource(_currentViewSource, out var typeCode, out var areaCode, out var offset3);
				int newOffset3 = offset3 + 100;
				await LoadArtistsByCategoryAsync(typeCode, areaCode, newOffset3, skipSave: true);
			}
			else if (!string.IsNullOrEmpty(_currentViewSource) && _currentViewSource.StartsWith("podcast:", StringComparison.OrdinalIgnoreCase))
			{
				if (!_currentPodcastHasMore)
				{
					UpdateStatusBar("已经是最后一页");
					return;
				}
				ParsePodcastViewSource(_currentViewSource, out var podcastId, out var offset4, out var ascending);
				int newOffset4 = offset4 + 50;
				await LoadPodcastEpisodesAsync(podcastId, newOffset4, skipSave: true, null, ascending);
			}
			else if (!string.IsNullOrEmpty(_currentViewSource) && _currentViewSource.StartsWith("podcast_cat_", StringComparison.OrdinalIgnoreCase))
			{
				if (!_currentPodcastCategoryHasMore)
				{
					UpdateStatusBar("已经是最后一页");
					return;
				}
				ParsePodcastCategoryViewSource(_currentViewSource, out var categoryId, out var offset5);
				int newOffset5 = offset5 + 50;
				await LoadPodcastsByCategoryAsync(categoryId, newOffset5, skipSave: true);
			}
			else if (!string.IsNullOrEmpty(_currentViewSource) && _currentViewSource.StartsWith("playlist_cat_", StringComparison.OrdinalIgnoreCase))
			{
				if (!_currentPlaylistCategoryHasMore)
				{
					UpdateStatusBar("已经是最后一页");
					return;
				}
				ParsePlaylistCategoryViewSource(_currentViewSource, out var catName, out var offset6);
				int newOffset6 = offset6 + PlaylistCategoryPageSize;
				await LoadPlaylistsByCat(catName, newOffset6, skipSave: true);
			}
			else if (!string.IsNullOrEmpty(_currentViewSource) && _currentViewSource.StartsWith("new_songs_", StringComparison.OrdinalIgnoreCase))
			{
				if (!_currentNewSongsHasMore)
				{
					UpdateStatusBar("已经是最后一页");
					return;
				}
				if (!TryParseNewSongsViewSource(_currentViewSource, out var areaType, out var areaName, out var offset7, out var baseSource))
				{
					UpdateStatusBar("无法定位新歌页码");
					return;
				}
				int newOffset7 = offset7 + NewSongsPageSize;
				await LoadNewSongsByArea(areaType, areaName, baseSource, newOffset7, skipSave: true);
			}
			else if (!string.IsNullOrEmpty(_currentViewSource) && _currentViewSource.StartsWith("highquality_playlists", StringComparison.OrdinalIgnoreCase))
			{
				if (!_currentHighQualityHasMore)
				{
					UpdateStatusBar("已经是最后一页");
					return;
				}
				ParseHighQualityViewSource(_currentViewSource, out var offset8);
				int newOffset8 = offset8 + HighQualityPlaylistsPageSize;
				await LoadHighQualityPlaylists(newOffset8, skipSave: true);
			}
			else if (!string.IsNullOrEmpty(_currentViewSource) && _currentViewSource.StartsWith("toplist", StringComparison.OrdinalIgnoreCase))
			{
				if (!_currentToplistHasMore)
				{
					UpdateStatusBar("已经是最后一页");
					return;
				}
				ParseToplistViewSource(_currentViewSource, out var offset9);
				int newOffset9 = offset9 + ToplistPageSize;
				await LoadToplist(offset: newOffset9, skipSave: true);
			}
			else if (string.Equals(_currentViewSource, "user_cloud", StringComparison.OrdinalIgnoreCase))
			{
				if (!_cloudHasMore)
				{
					UpdateStatusBar("已经是最后一页");
					return;
				}
				_cloudPage++;
				await LoadCloudSongsAsync(skipSave: true);
			}
			else
			{
				UpdateStatusBar("当前内容不支持翻页");
			}
		}
	}

        private async Task OnJumpPageAsync()
        {
		if (string.IsNullOrWhiteSpace(_currentViewSource))
		{
			UpdateStatusBar("当前内容不支持跳转");
			return;
		}
		string viewSource = _currentViewSource;
		int currentPage = 1;
		int maxPage = -1;
		int pageSize = 0;
		string paginationKey = string.Empty;
		Func<int, Task> jumpAction = null;
		if (viewSource.StartsWith("search:", StringComparison.OrdinalIgnoreCase))
		{
			ParseSearchViewSource(viewSource, out var parsedType, out var parsedKeyword, out var _);
			string keyKeyword = (!string.IsNullOrWhiteSpace(parsedKeyword)) ? parsedKeyword : _lastKeyword;
			string keyType = (!string.IsNullOrWhiteSpace(parsedType)) ? parsedType : _currentSearchType;
			paginationKey = BuildSearchPaginationKey(keyKeyword ?? string.Empty, keyType ?? string.Empty);
			pageSize = _resultsPerPage;
			currentPage = Math.Max(1, _currentPage);
			maxPage = _maxPage;
			jumpAction = async delegate(int page)
			{
				if (!await ReloadCurrentSearchPageAsync(page))
				{
					UpdateStatusBar("无法加载指定页");
				}
			};
		}
		else if (viewSource.StartsWith("artist_songs:", StringComparison.OrdinalIgnoreCase))
		{
			ParseArtistListViewSource(viewSource, out var artistId, out var offset, out var order);
			if (_currentArtistSongsTotalCount <= 0)
			{
				_currentArtistSongsTotalCount = await EnsureArtistSongsTotalCountAsync(artistId, order, GetCurrentViewContentToken());
			}
			currentPage = (offset / ArtistSongsPageSize) + 1;
			maxPage = CalculateMaxPage(_currentArtistSongsTotalCount, ArtistSongsPageSize, currentPage);
			pageSize = ArtistSongsPageSize;
			paginationKey = BuildArtistSongsPaginationKey(artistId, order);
			jumpAction = async delegate(int page)
			{
				int targetOffset = Math.Max(0, (page - 1) * ArtistSongsPageSize);
				await LoadArtistSongsAsync(artistId, targetOffset, skipSave: true, orderOverride: ResolveArtistSongsOrder(order));
			};
		}
		else if (viewSource.StartsWith("artist_albums:", StringComparison.OrdinalIgnoreCase))
		{
			ParseArtistListViewSource(viewSource, out var artistId2, out var offset2, out var order2, "latest");
			if (_currentArtistAlbumsTotalCount <= 0)
			{
				await EnsureArtistAlbumsTotalCountAsync(artistId2);
			}
			currentPage = (offset2 / ArtistAlbumsPageSize) + 1;
			maxPage = CalculateMaxPage(_currentArtistAlbumsTotalCount, ArtistAlbumsPageSize, currentPage);
			pageSize = ArtistAlbumsPageSize;
			paginationKey = BuildArtistAlbumsPaginationKey(artistId2, ResolveArtistAlbumSort(order2));
			jumpAction = async delegate(int page)
			{
				int targetOffset = Math.Max(0, (page - 1) * ArtistAlbumsPageSize);
				await LoadArtistAlbumsAsync(artistId2, targetOffset, skipSave: true, sortOverride: ResolveArtistAlbumSort(order2));
			};
		}
		else if (viewSource.StartsWith("artist_category_list:", StringComparison.OrdinalIgnoreCase))
		{
			if (_currentArtistCategoryLoadedAll)
			{
				UpdateStatusBar("已加载全部歌手，无法跳转");
				return;
			}
			ParseArtistCategoryListViewSource(viewSource, out var typeCode, out var areaCode, out var offset3);
			currentPage = (offset3 / ArtistSongsPageSize) + 1;
			maxPage = CalculateMaxPage(_currentArtistCategoryTotalCount, ArtistSongsPageSize, currentPage);
			pageSize = ArtistSongsPageSize;
			paginationKey = BuildArtistCategoryPaginationKey(typeCode, areaCode);
			jumpAction = async delegate(int page)
			{
				int targetOffset = Math.Max(0, (page - 1) * ArtistSongsPageSize);
				await LoadArtistsByCategoryAsync(typeCode, areaCode, targetOffset, skipSave: true);
			};
		}
		else if (viewSource.StartsWith("podcast:", StringComparison.OrdinalIgnoreCase))
		{
			ParsePodcastViewSource(viewSource, out var podcastId, out var offset4, out var ascending);
			if (podcastId <= 0)
			{
				UpdateStatusBar("无法定位播客页码");
				return;
			}
			currentPage = (offset4 / PodcastSoundPageSize) + 1;
			maxPage = CalculateMaxPage(_currentPodcastEpisodeTotalCount, PodcastSoundPageSize, currentPage);
			pageSize = PodcastSoundPageSize;
			paginationKey = BuildPodcastEpisodesPaginationKey(podcastId, ascending);
			jumpAction = async delegate(int page)
			{
				int targetOffset = Math.Max(0, (page - 1) * PodcastSoundPageSize);
				await LoadPodcastEpisodesAsync(podcastId, targetOffset, skipSave: true, podcastInfo: null, sortAscendingOverride: ascending);
			};
		}
		else if (viewSource.StartsWith("podcast_cat_", StringComparison.OrdinalIgnoreCase))
		{
			ParsePodcastCategoryViewSource(viewSource, out var categoryId, out var offset5);
			currentPage = (offset5 / PodcastCategoryPageSize) + 1;
			maxPage = CalculateMaxPage(_currentPodcastCategoryTotalCount, PodcastCategoryPageSize, currentPage);
			pageSize = PodcastCategoryPageSize;
			paginationKey = BuildPodcastCategoryPaginationKey(categoryId);
			jumpAction = async delegate(int page)
			{
				int targetOffset = Math.Max(0, (page - 1) * PodcastCategoryPageSize);
				await LoadPodcastsByCategoryAsync(categoryId, targetOffset, skipSave: true);
			};
		}
		else if (viewSource.StartsWith("playlist_cat_", StringComparison.OrdinalIgnoreCase))
		{
			ParsePlaylistCategoryViewSource(viewSource, out var catName, out var offset6);
			currentPage = (offset6 / PlaylistCategoryPageSize) + 1;
			maxPage = CalculateMaxPage(_currentPlaylistCategoryTotalCount, PlaylistCategoryPageSize, currentPage);
			pageSize = PlaylistCategoryPageSize;
			paginationKey = BuildPlaylistCategoryPaginationKey(catName);
			jumpAction = async delegate(int page)
			{
				int targetOffset = Math.Max(0, (page - 1) * PlaylistCategoryPageSize);
				await LoadPlaylistsByCat(catName, targetOffset, skipSave: true);
			};
		}
		else if (viewSource.StartsWith("new_songs_", StringComparison.OrdinalIgnoreCase))
		{
			if (!TryParseNewSongsViewSource(viewSource, out var areaType, out var areaName, out var offset7, out var baseSource))
			{
				UpdateStatusBar("无法定位新歌页码");
				return;
			}
			int totalCount = _currentNewSongsTotalCount;
			if (totalCount <= 0)
			{
				lock (_newSongsCacheLock)
				{
					if (_newSongsCacheByArea.TryGetValue(areaType, out var cached) && cached != null)
					{
						totalCount = cached.Count;
					}
				}
			}
			currentPage = (offset7 / NewSongsPageSize) + 1;
			maxPage = CalculateMaxPage(totalCount, NewSongsPageSize, currentPage);
			pageSize = NewSongsPageSize;
			paginationKey = BuildNewSongsPaginationKey(areaType);
			jumpAction = async delegate(int page)
			{
				int targetOffset = Math.Max(0, (page - 1) * NewSongsPageSize);
				await LoadNewSongsByArea(areaType, areaName, baseSource, targetOffset, skipSave: true);
			};
		}
		else if (viewSource.StartsWith("highquality_playlists", StringComparison.OrdinalIgnoreCase))
		{
			if (_currentHighQualityLoadedAll)
			{
				UpdateStatusBar("已加载全部精品歌单，无法跳转");
				return;
			}
			ParseHighQualityViewSource(viewSource, out var offset8);
			int totalCount = _currentHighQualityTotalCount;
			currentPage = (offset8 / HighQualityPlaylistsPageSize) + 1;
			maxPage = CalculateMaxPage(totalCount, HighQualityPlaylistsPageSize, currentPage);
			pageSize = HighQualityPlaylistsPageSize;
			paginationKey = BuildHighQualityPaginationKey();
			jumpAction = async delegate(int page)
			{
				int targetOffset = Math.Max(0, (page - 1) * HighQualityPlaylistsPageSize);
				await LoadHighQualityPlaylists(targetOffset, skipSave: true);
			};
		}
		else if (viewSource.StartsWith("toplist", StringComparison.OrdinalIgnoreCase))
		{
			ParseToplistViewSource(viewSource, out var offset9);
			int totalCount = _currentToplistTotalCount;
			if (totalCount <= 0)
			{
				lock (_toplistCacheLock)
				{
					totalCount = _toplistCache.Count;
				}
			}
			currentPage = (offset9 / ToplistPageSize) + 1;
			maxPage = CalculateMaxPage(totalCount, ToplistPageSize, currentPage);
			pageSize = ToplistPageSize;
			paginationKey = BuildToplistPaginationKey();
			jumpAction = async delegate(int page)
			{
				int targetOffset = Math.Max(0, (page - 1) * ToplistPageSize);
				await LoadToplist(offset: targetOffset, skipSave: true);
			};
		}
		else if (string.Equals(viewSource, "user_cloud", StringComparison.OrdinalIgnoreCase))
		{
			currentPage = Math.Max(1, _cloudPage);
			maxPage = CalculateMaxPage(_cloudTotalCount, CloudPageSize, currentPage);
			pageSize = CloudPageSize;
			paginationKey = BuildCloudPaginationKey();
			jumpAction = async delegate(int page)
			{
				_cloudPage = Math.Max(1, page);
				await LoadCloudSongsAsync(skipSave: true);
			};
		}
		else
		{
			UpdateStatusBar("当前内容不支持跳转");
			return;
		}
		if (pageSize > 0 && !string.IsNullOrWhiteSpace(paginationKey))
		{
			maxPage = ResolveCappedMaxPage(paginationKey, pageSize, maxPage);
		}
		if (maxPage <= 0)
		{
			UpdateStatusBar("无法获取最大页数");
			return;
		}
		if (maxPage <= 1)
		{
			UpdateStatusBar("只有一页，无法跳转");
			return;
		}
		using PageJumpDialog dialog = new PageJumpDialog(currentPage, maxPage);
		if (dialog.ShowDialog(this) != DialogResult.OK)
		{
			return;
		}
		int targetPage2 = dialog.TargetPage;
		if (targetPage2 < 1 || targetPage2 > maxPage)
		{
			UpdateStatusBar("页码超出范围");
			return;
		}
		if (targetPage2 == currentPage)
		{
			UpdateStatusBar("已在该页");
			return;
		}
		if (jumpAction != null)
		{
			await jumpAction(targetPage2);
		}
	}

	private static int CalculateMaxPage(int totalCount, int pageSize, int fallbackPage)
	{
		if (pageSize <= 0)
		{
			return Math.Max(1, fallbackPage);
		}
		if (totalCount <= 0)
		{
			return Math.Max(1, fallbackPage);
		}
		return Math.Max(1, (int)Math.Ceiling((double)totalCount / (double)pageSize));
	}

	private string BuildSearchPaginationKey(string keyword, string searchType)
	{
		return $"search:{searchType}:{keyword}";
	}

	private string BuildArtistSongsPaginationKey(long artistId, string orderToken)
	{
		return $"artist_songs:{artistId}:order{orderToken}";
	}

	private string BuildArtistAlbumsPaginationKey(long artistId, ArtistAlbumSortOption sortOption)
	{
		return $"artist_albums:{artistId}:order{MapArtistAlbumSort(sortOption)}";
	}

	private string BuildArtistCategoryPaginationKey(int typeCode, int areaCode)
	{
		return $"artist_category_list:{typeCode}:{areaCode}";
	}

	private string BuildPodcastEpisodesPaginationKey(long radioId, bool ascending)
	{
		return $"podcast:{radioId}:asc{(ascending ? 1 : 0)}";
	}

	private string BuildPodcastCategoryPaginationKey(int categoryId)
	{
		return $"podcast_cat_{categoryId}";
	}

	private string BuildPlaylistCategoryPaginationKey(string cat)
	{
		return $"playlist_cat_{cat}";
	}

	private string BuildNewSongsPaginationKey(int areaType)
	{
		return $"new_songs:{areaType}";
	}

	private string BuildHighQualityPaginationKey()
	{
		return "highquality_playlists";
	}

	private void CacheHighQualityPage(int offset, List<PlaylistInfo> playlists, bool hasMore, long before, long nextBefore)
	{
		lock (_highQualityCacheLock)
		{
			_highQualityPlaylistsCache[offset] = new List<PlaylistInfo>(playlists);
			_highQualityHasMoreByOffset[offset] = hasMore;
			if (!_highQualityBeforeByOffset.ContainsKey(offset))
			{
				_highQualityBeforeByOffset[offset] = before;
			}
			if (nextBefore > 0)
			{
				_highQualityBeforeByOffset[offset + HighQualityPlaylistsPageSize] = nextBefore;
			}
			if (!_highQualityCacheOrder.Contains(offset))
			{
				_highQualityCacheOrder.Enqueue(offset);
			}
			TrimHighQualityCacheLocked();
		}
	}

	private void TrimHighQualityCacheLocked()
	{
		while (_highQualityCacheOrder.Count > HighQualityPlaylistsCacheLimit)
		{
			int removeOffset = _highQualityCacheOrder.Dequeue();
			_highQualityPlaylistsCache.Remove(removeOffset);
			_highQualityHasMoreByOffset.Remove(removeOffset);
		}
	}

	private int ResolveHighQualityCachedCount(int offset)
	{
		lock (_highQualityCacheLock)
		{
			if (_highQualityPlaylistsCache.TryGetValue(offset, out var cached) && cached != null)
			{
				return cached.Count;
			}
		}
		return 0;
	}

	private void HandleHighQualityEmptyPage(int offset, string? viewSource, string? accessibleName)
	{
		int capOffset = Math.Max(0, offset - HighQualityPlaylistsPageSize);
		SetPaginationOffsetCap(BuildHighQualityPaginationKey(), capOffset);
		int totalCount = capOffset + ResolveHighQualityCachedCount(capOffset);
		if (totalCount > 0)
		{
			_currentHighQualityTotalCount = Math.Max(_currentHighQualityTotalCount, totalCount);
		}
		_currentHighQualityHasMore = false;
		UpdateStatusBar("已经是最后一页");
		ShowListErrorRow(viewSource, accessibleName, "加载失败：已经是最后一页，无法继续翻页。按回车重新跳转。");
	}

	private async Task<int> FetchHighQualityPlaylistsCountAsync(CancellationToken token)
	{
		int count = 0;
		long before = 0;
		bool hasMore = true;
		int safety = 0;
		while (hasMore && safety < 200)
		{
			token.ThrowIfCancellationRequested();
			(List<PlaylistInfo> Items, long LastTime, bool HasMore) result = await FetchWithRetryUntilCancel((CancellationToken ct) => _apiClient.GetHighQualityPlaylistsAsync("全部", HighQualityPlaylistsPageSize, before), $"highquality:count:before{before}:page{(safety + 1)}", token).ConfigureAwait(continueOnCapturedContext: false);
			List<PlaylistInfo> pageItems = result.Items ?? new List<PlaylistInfo>();
			if (pageItems.Count == 0)
			{
				break;
			}
			count = checked(count + pageItems.Count);
			long nextBefore = result.LastTime;
			hasMore = result.HasMore;
			if (pageItems.Count < HighQualityPlaylistsPageSize || nextBefore <= 0 || nextBefore == before)
			{
				hasMore = false;
			}
			before = nextBefore;
			safety++;
		}
		return count;
	}

	private string BuildToplistPaginationKey()
	{
		return "toplist";
	}

	private string BuildCloudPaginationKey()
	{
		return "user_cloud";
	}

	private int GetPaginationOffsetCap(string key)
	{
		if (string.IsNullOrWhiteSpace(key))
		{
			return -1;
		}
		lock (_paginationLimitLock)
		{
			if (_paginationOffsetCaps.TryGetValue(key, out int cap))
			{
				return cap;
			}
		}
		return -1;
	}

	private void SetPaginationOffsetCap(string key, int capOffset)
	{
		if (string.IsNullOrWhiteSpace(key) || capOffset < 0)
		{
			return;
		}
		lock (_paginationLimitLock)
		{
			if (_paginationOffsetCaps.TryGetValue(key, out int existing))
			{
				if (existing <= 0 || capOffset < existing)
				{
					_paginationOffsetCaps[key] = capOffset;
				}
			}
			else
			{
				_paginationOffsetCaps[key] = capOffset;
			}
		}
	}

	private int ResolveCappedMaxPage(string key, int pageSize, int maxPageFromTotal)
	{
		int maxPage = Math.Max(1, maxPageFromTotal);
		if (pageSize <= 0)
		{
			return maxPage;
		}
		int cap = GetPaginationOffsetCap(key);
		if (cap >= 0)
		{
			int capMaxPage = Math.Max(1, (cap / pageSize) + 1);
			maxPage = Math.Min(maxPage, capMaxPage);
		}
		return maxPage;
	}

	private int NormalizeOffsetWithCap(string key, int pageSize, int offset, out bool clamped)
	{
		clamped = false;
		int safeOffset = Math.Max(0, offset);
		if (pageSize <= 0)
		{
			return safeOffset;
		}
		int cap = GetPaginationOffsetCap(key);
		if (cap >= 0 && safeOffset > cap)
		{
			int maxPage = Math.Max(1, (cap / pageSize) + 1);
			int normalizedOffset = Math.Max(0, (maxPage - 1) * pageSize);
			clamped = normalizedOffset != safeOffset;
			return normalizedOffset;
		}
		return safeOffset;
	}

	private int NormalizePageWithCap(string key, int pageSize, int requestedPage, int maxPageFromTotal, out bool clamped, out int cappedMaxPage)
	{
		int maxPage = ResolveCappedMaxPage(key, pageSize, maxPageFromTotal);
		int page = Math.Max(1, requestedPage);
		clamped = page > maxPage;
		if (clamped)
		{
			page = maxPage;
		}
		cappedMaxPage = maxPage;
		return page;
	}

	private static bool IsPaginationOffsetError(Exception ex)
	{
		Exception current = ex;
		while (current != null)
		{
			if (current is ArgumentException)
			{
				return true;
			}
			string message = current.Message ?? string.Empty;
			if (message.IndexOf("请求参数错误", StringComparison.OrdinalIgnoreCase) >= 0 || message.IndexOf("参数错误", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return true;
			}
			current = current.InnerException;
		}
		return false;
	}

	private bool TryHandlePaginationOffsetError(Exception ex, string key, int offset, int pageSize, string? viewSource, string? accessibleName)
	{
		if (!IsPaginationOffsetError(ex))
		{
			return false;
		}
		if (pageSize > 0)
		{
			SetPaginationOffsetCap(key, Math.Max(0, offset - 1));
		}
		int cappedMaxPage = ResolveCappedMaxPage(key, pageSize, int.MaxValue);
		string hint = (cappedMaxPage > 0 && cappedMaxPage < int.MaxValue) ? $"页码过大，最大可到第 {cappedMaxPage} 页" : "页码过大";
		UpdateStatusBar($"{hint}，请重新跳转");
		ShowListErrorRow(viewSource, accessibleName, $"加载失败：{hint}，请重新跳转。按回车重新跳转。");
		return true;
	}

	private bool TryHandlePaginationEmptyResult(string key, int offset, int pageSize, int totalCount, int itemsCount, bool hasMore, string? viewSource, string? accessibleName)
	{
		if (pageSize <= 0)
		{
			return false;
		}
		if (itemsCount > 0)
		{
			return false;
		}
		if (totalCount <= 0)
		{
			return false;
		}
		if (totalCount <= offset)
		{
			return false;
		}
		SetPaginationOffsetCap(key, Math.Max(0, offset - 1));
		int cappedMaxPage = ResolveCappedMaxPage(key, pageSize, int.MaxValue);
		string hint = (cappedMaxPage > 0 && cappedMaxPage < int.MaxValue) ? $"接口限制最多到第 {cappedMaxPage} 页" : "接口限制导致无法继续翻页";
		UpdateStatusBar($"{hint}，请重新跳转");
		ShowListErrorRow(viewSource, accessibleName, $"加载失败：{hint}，请重新跳转。按回车重新跳转。");
		return true;
	}

	private static int NormalizeOffsetWithTotal(int totalCount, int pageSize, int offset, out bool clamped)
	{
		clamped = false;
		int safeOffset = Math.Max(0, offset);
		if (pageSize <= 0 || totalCount <= 0)
		{
			return safeOffset;
		}
		int maxStart = ((totalCount - 1) / pageSize) * pageSize;
		if (safeOffset > maxStart)
		{
			clamped = true;
			return maxStart;
		}
		return safeOffset;
	}


}
}
