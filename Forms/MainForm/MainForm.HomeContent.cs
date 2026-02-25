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
	private async Task<bool> LoadHomePageAsync(bool skipSave = false, bool showErrorDialog = true, bool isInitialLoad = false, CancellationToken cancellationToken = default(CancellationToken))
	{
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!skipSave)
			{
				_navigationHistory.Clear();
				Debug.WriteLine("[Navigation] 加载主页，清空导航历史");
			}
			ViewLoadRequest request = new ViewLoadRequest("homepage", "主页", "正在加载主页骨架...", !skipSave);
			ViewLoadResult<HomePageViewData?> loadResult = await RunViewLoadAsync(request, delegate(CancellationToken viewToken)
			{
				CancellationTokenSource linkedCts = null;
				CancellationToken cancellationToken2 = LinkCancellationTokens(viewToken, cancellationToken, out linkedCts);
				try
				{
					cancellationToken2.ThrowIfCancellationRequested();
					return Task.FromResult(BuildHomePageSkeletonViewData());
				}
				finally
				{
					linkedCts?.Dispose();
				}
			}, "加载主页已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (loadResult.IsCanceled)
			{
				return false;
			}
			HomePageViewData data = loadResult.Value ?? BuildHomePageSkeletonViewData();
			DisplayListItems(data.Items, "homepage", "主页");
			_currentSongs.Clear();
			_currentPlaylists.Clear();
			_currentAlbums.Clear();
			_currentPlaylist = null;
			_isHomePage = true;
			UpdateStatusBar(data.StatusText);
			EnrichHomePageAsync(isInitialLoad, cancellationToken);
			if (isInitialLoad)
			{
				_initialHomeLoadCompleted = true;
			}
			return true;
		}
		catch (OperationCanceledException)
		{
			UpdateStatusBar("加载主页已取消");
			return false;
		}
		catch (Exception ex2)
		{
			Exception ex3 = ex2;
			Exception ex4 = ex3;
			Debug.WriteLine($"[HomePage] 加载失败: {ex4}");
			if (showErrorDialog)
			{
				MessageBox.Show("加载主页失败: " + ex4.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			}
			UpdateStatusBar("加载主页失败");
			return false;
		}
	}

	private async Task<HomePageViewData> BuildHomePageEnrichedViewDataAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		List<ListItemInfo> homeItems = new List<ListItemInfo>();
		bool isLoggedIn = _accountState?.IsLoggedIn ?? false;
		int userPlaylistCount = 0;
		int userAlbumCount = 0;
		int artistFavoritesCount = 0;
		int podcastFavoritesCount = 0;
		PlaylistInfo likedPlaylist = null;
		int playlistCategoryCount = _homePlaylistCategoryPresets.Length;
		int artistCategoryTypeCount = ArtistMetadataHelper.GetTypeOptions().Count;
		int albumCategoryTypeCount = ArtistMetadataHelper.GetNewAlbumPeriodOptions().Count;
		Task<List<PlaylistInfo>> toplistTask = _apiClient.GetToplistAsync();
		Task<List<AlbumInfo>> newAlbumsTask = _apiClient.GetNewAlbumsAsync();
		int toplistCount = 0;
		int newAlbumCount = 0;
		int dailyRecommendSongCount = 0;
		int dailyRecommendPlaylistCount = 0;
		int personalizedPlaylistCount = 0;
		int personalizedSongCount = 0;
		int podcastCategoryCount = 0;
		int highQualityCount = 0;
		Task<int> highQualityCountTask = null;
		Task<List<PodcastCategoryInfo>> podcastCategoriesTask = _apiClient.GetPodcastCategoriesAsync(cancellationToken);
		Task<(List<SongInfo> Songs, List<PlaylistInfo> Playlists)> dailyRecommendTask = null;
		Task<(List<PlaylistInfo> Playlists, List<SongInfo> Songs)> personalizedTask = null;
		if (IsHighQualityCountFresh() && _homeCachedHighQualityCount.HasValue)
		{
			highQualityCount = _homeCachedHighQualityCount.Value;
		}
		else
		{
			highQualityCountTask = FetchHighQualityPlaylistsCountAsync(cancellationToken);
		}
		if (isLoggedIn)
		{
			dailyRecommendTask = FetchDailyRecommendBundleAsync(cancellationToken);
			personalizedTask = FetchPersonalizedBundleAsync(cancellationToken);
		}
		checked
		{
			if (isLoggedIn)
			{
				try
				{
					UserAccountInfo userInfo = await _apiClient.GetUserAccountAsync();
					ThrowIfHomeLoadCancelled();
					if (userInfo != null && userInfo.UserId > 0)
					{
						_loggedInUserId = userInfo.UserId;
						var (playlists, totalCount) = await _apiClient.GetUserPlaylistsAsync(userInfo.UserId);
						ThrowIfHomeLoadCancelled();
						if (playlists != null && playlists.Count > 0)
						{
							likedPlaylist = playlists.FirstOrDefault((PlaylistInfo p) => !string.IsNullOrEmpty(p.Name) && p.Name.IndexOf("喜欢的音乐", StringComparison.OrdinalIgnoreCase) >= 0);
							userPlaylistCount = totalCount;
							if (likedPlaylist != null && userPlaylistCount > 0)
							{
								userPlaylistCount = Math.Max(0, userPlaylistCount - 1);
							}
						}
						try
						{
							int albumCount = (await _apiClient.GetUserAlbumsAsync(1)).Item2;
							ThrowIfHomeLoadCancelled();
							userAlbumCount = albumCount;
						}
						catch (Exception ex)
						{
							Debug.WriteLine("[HomePage] 获取收藏专辑数量失败: " + ex.Message);
						}
						try
						{
							SearchResult<ArtistInfo> favoriteArtists = await _apiClient.GetArtistSubscriptionsAsync(1);
							ThrowIfHomeLoadCancelled();
							artistFavoritesCount = favoriteArtists?.TotalCount ?? favoriteArtists?.Items.Count ?? 0;
						}
						catch (Exception ex2)
						{
							Debug.WriteLine("[HomePage] 获取收藏歌手数量失败: " + ex2.Message);
						}
						try
						{
							int podcastCount = (await _apiClient.GetSubscribedPodcastsAsync(1)).Item2;
							ThrowIfHomeLoadCancelled();
							podcastFavoritesCount = podcastCount;
						}
						catch (Exception ex3)
						{
							Debug.WriteLine("[HomePage] 获取收藏播客数量失败: " + ex3.Message);
						}
					}
				}
				catch (Exception ex4)
				{
					Exception ex5 = ex4;
					Exception ex6 = ex5;
					Debug.WriteLine("[HomePage] 预加载用户数据失败: " + ex6.Message);
				}
			}
			else
			{
				_loggedInUserId = 0L;
				_recentSongsCache.Clear();
				_recentPlaylistsCache.Clear();
				_recentAlbumsCache.Clear();
				_recentPodcastsCache.Clear();
				_recentPlayCount = 0;
				_recentPlaylistCount = 0;
				_recentAlbumCount = 0;
				_recentPodcastCount = 0;
				_recentSummaryReady = false;
			}
			try
			{
				List<PlaylistInfo> toplist = await toplistTask;
				ThrowIfHomeLoadCancelled();
				toplistCount = toplist?.Count ?? 0;
			}
			catch (Exception ex7)
			{
				Debug.WriteLine("[HomePage] 获取排行榜数量失败: " + ex7.Message);
			}
			try
			{
				List<AlbumInfo> newAlbums = await newAlbumsTask;
				ThrowIfHomeLoadCancelled();
				newAlbumCount = newAlbums?.Count ?? 0;
			}
			catch (Exception ex8)
			{
				Debug.WriteLine("[HomePage] 获取新碟数量失败: " + ex8.Message);
			}
			try
			{
				List<PodcastCategoryInfo> podcastCategories = await podcastCategoriesTask.ConfigureAwait(continueOnCapturedContext: false);
				ThrowIfHomeLoadCancelled();
				podcastCategoryCount = podcastCategories?.Count ?? 0;
				if (podcastCategories != null)
				{
					lock (_podcastCategoryLock)
					{
						_podcastCategories.Clear();
						foreach (PodcastCategoryInfo cat in podcastCategories)
						{
							if (cat != null && cat.Id > 0 && !string.IsNullOrWhiteSpace(cat.Name))
							{
								_podcastCategories[cat.Id] = cat;
							}
						}
					}
				}
			}
			catch (Exception ex9)
			{
				Debug.WriteLine("[HomePage] 获取播客分类失败: " + ex9.Message);
			}
			if (dailyRecommendTask != null)
			{
				try
				{
					(List<SongInfo> Songs, List<PlaylistInfo> Playlists) dailyData = await dailyRecommendTask.ConfigureAwait(continueOnCapturedContext: false);
					ThrowIfHomeLoadCancelled();
					dailyRecommendSongCount = dailyData.Songs?.Count ?? 0;
					dailyRecommendPlaylistCount = dailyData.Playlists?.Count ?? 0;
				}
				catch (Exception ex4)
				{
					Exception ex10 = ex4;
					Exception ex11 = ex10;
					Debug.WriteLine("[HomePage] 获取每日推荐摘要失败: " + ex11.Message);
				}
			}
			if (personalizedTask != null)
			{
				try
				{
					(List<PlaylistInfo> Playlists, List<SongInfo> Songs) personalizedData = await personalizedTask.ConfigureAwait(continueOnCapturedContext: false);
					ThrowIfHomeLoadCancelled();
					personalizedPlaylistCount = personalizedData.Playlists?.Count ?? 0;
					personalizedSongCount = personalizedData.Songs?.Count ?? 0;
				}
				catch (Exception ex4)
				{
					Exception ex12 = ex4;
					Exception ex13 = ex12;
					Debug.WriteLine("[HomePage] 获取为您推荐摘要失败: " + ex13.Message);
				}
			}
			if (highQualityCountTask != null)
			{
				try
				{
					highQualityCount = await highQualityCountTask.ConfigureAwait(continueOnCapturedContext: false);
					ThrowIfHomeLoadCancelled();
					_homeCachedHighQualityCount = (highQualityCount > 0) ? highQualityCount : (int?)null;
					_highQualityCountFetchedUtc = DateTime.UtcNow;
				}
				catch (Exception ex4)
				{
					Exception ex16 = ex4;
					Exception ex17 = ex16;
					Debug.WriteLine("[HomePage] 获取精品歌单数量失败: " + ex17.Message);
				}
			}
			_userLikedPlaylist = likedPlaylist;
			await RefreshRecentSummariesAsync(isLoggedIn, cancellationToken);
			if (isLoggedIn)
			{
				CloudSongPageResult cloudSummary = null;
				try
				{
					_cloudTotalCount = 0;
					_cloudUsedSize = 0L;
					_cloudMaxSize = 0L;
					cloudSummary = await _apiClient.GetCloudSongsAsync(1);
					ThrowIfHomeLoadCancelled();
					_cloudTotalCount = cloudSummary.TotalCount;
					_cloudUsedSize = cloudSummary.UsedSize;
					_cloudMaxSize = cloudSummary.MaxSize;
				}
				catch (Exception ex4)
				{
					Exception ex14 = ex4;
					Exception ex15 = ex14;
					Debug.WriteLine("[Home] 获取云盘摘要失败: " + ex15.Message);
				}
				int? likedCount = _userLikedPlaylist?.TrackCount ?? likedPlaylist?.TrackCount;
				string dailyDescription = ((dailyRecommendSongCount + dailyRecommendPlaylistCount > 0) ? $"歌曲 {dailyRecommendSongCount} 首 / 歌单 {dailyRecommendPlaylistCount} 个" : "每日 6:00 更新");
				string personalizedDescription = ((personalizedPlaylistCount + personalizedSongCount > 0) ? $"歌单 {personalizedPlaylistCount} 个 / 歌曲 {personalizedSongCount} 首" : "为你推荐歌单和新歌");
				string cloudDescription = ((cloudSummary != null) ? ("已用 " + FormatSize(_cloudUsedSize) + " / " + FormatSize(_cloudMaxSize)) : "上传和管理您的私人音乐");
				homeItems.Add(BuildHomeCategoryItem("user_liked_songs", "喜欢的音乐", likedCount, "您收藏的所有歌曲"));
				homeItems.Add(BuildHomeCategoryItem("recent_listened", "最近听过", null, BuildRecentListenedDescription()));
				homeItems.Add(BuildHomeCategoryItem("user_playlists", "创建和收藏的歌单", userPlaylistCount));
				homeItems.Add(BuildHomeCategoryItem("user_albums", "收藏的专辑", userAlbumCount));
				homeItems.Add(BuildHomeCategoryItem("artist_favorites", "收藏的歌手", artistFavoritesCount));
				homeItems.Add(BuildHomeCategoryItem("user_podcasts", "收藏的播客", podcastFavoritesCount));
				homeItems.Add(BuildHomeCategoryItem("user_cloud", "云盘", _cloudTotalCount, cloudDescription));
				homeItems.Add(BuildHomeCategoryItem("daily_recommend", "每日推荐", null, dailyDescription));
				homeItems.Add(BuildHomeCategoryItem("personalized", "为您推荐", null, personalizedDescription));
				homeItems.Add(BuildHomeCategoryItem(PersonalFmCategoryId, PersonalFmAccessibleName, null, "专属于你的连续推荐电台"));
				homeItems.Add(BuildHomeCategoryItem("highquality_playlists", "精品歌单", (highQualityCount > 0) ? highQualityCount : (int?)null));
				homeItems.Add(BuildHomeCategoryItem("new_songs", "新歌速递", 5));
				homeItems.Add(BuildHomeCategoryItem("new_albums", "新碟上架", newAlbumCount));
				homeItems.Add(BuildHomeCategoryItem("new_album_categories", "新碟分类", albumCategoryTypeCount));
				homeItems.Add(BuildHomeCategoryItem("playlist_category", "歌单分类", playlistCategoryCount));
				homeItems.Add(BuildHomeCategoryItem("podcast_categories", "播客分类", podcastCategoryCount));
				homeItems.Add(BuildHomeCategoryItem("artist_categories", "歌手分类", artistCategoryTypeCount));
				homeItems.Add(BuildHomeCategoryItem("toplist", "官方排行榜", toplistCount));
			}
			else
			{
				_cloudTotalCount = 0;
				_cloudUsedSize = 0L;
				_cloudMaxSize = 0L;
				homeItems.Add(BuildHomeCategoryItem("highquality_playlists", "精品歌单", (highQualityCount > 0) ? highQualityCount : (int?)null));
				homeItems.Add(BuildHomeCategoryItem("new_songs", "新歌速递", 5));
				homeItems.Add(BuildHomeCategoryItem("new_albums", "新碟上架", newAlbumCount));
				homeItems.Add(BuildHomeCategoryItem("new_album_categories", "新碟分类", albumCategoryTypeCount));
				homeItems.Add(BuildHomeCategoryItem("playlist_category", "歌单分类", playlistCategoryCount));
				homeItems.Add(BuildHomeCategoryItem("podcast_categories", "播客分类", podcastCategoryCount));
				homeItems.Add(BuildHomeCategoryItem("artist_categories", "歌手分类", artistCategoryTypeCount));
				homeItems.Add(BuildHomeCategoryItem("toplist", "官方排行榜", toplistCount));
			}
			_homeCachedToplistCount = toplistCount;
			_homeCachedNewAlbumCount = newAlbumCount;
			_homeCachedPodcastCategoryCount = podcastCategoryCount;
			_homeCachedHighQualityCount = (highQualityCount > 0) ? highQualityCount : (int?)null;
			if (isLoggedIn)
			{
				_homeCachedUserPlaylistCount = userPlaylistCount;
				_homeCachedUserAlbumCount = userAlbumCount;
				_homeCachedArtistFavoritesCount = artistFavoritesCount;
				_homeCachedPodcastFavoritesCount = podcastFavoritesCount;
				_homeCachedDailyRecommendSongCount = dailyRecommendSongCount;
				_homeCachedDailyRecommendPlaylistCount = dailyRecommendPlaylistCount;
				_homeCachedPersonalizedPlaylistCount = personalizedPlaylistCount;
				_homeCachedPersonalizedSongCount = personalizedSongCount;
			}
			else
			{
				_homeCachedUserPlaylistCount = null;
				_homeCachedUserAlbumCount = null;
				_homeCachedArtistFavoritesCount = null;
				_homeCachedPodcastFavoritesCount = null;
				_homeCachedDailyRecommendSongCount = null;
				_homeCachedDailyRecommendPlaylistCount = null;
				_homeCachedPersonalizedPlaylistCount = null;
				_homeCachedPersonalizedSongCount = null;
			}
			string statusText = (isLoggedIn ? $"主页加载完成，共 {homeItems.Count} 个入口" : "欢迎访问主页，登录后可查看更多内容");
			return new HomePageViewData(homeItems, statusText);
		}
		void ThrowIfHomeLoadCancelled()
		{
			cancellationToken.ThrowIfCancellationRequested();
		}
	}

	private async Task HandleListItemActivate(ListItemInfo listItem)
	{
		switch (listItem.Type)
		{
                case ListItemType.Song:
                        if (listItem.Song != null)
                        {
                                AnnounceSongLoadingForActivation(listItem.Song);
                                await PlaySong(listItem.Song);
                        }
                        break;
		case ListItemType.Playlist:
			if (listItem.Playlist != null)
			{
				await OpenPlaylist(listItem.Playlist);
			}
			break;
		case ListItemType.Album:
			if (listItem.Album != null)
			{
				await OpenAlbum(listItem.Album);
			}
			break;
		case ListItemType.Artist:
			if (listItem.Artist != null)
			{
				if (IsArtistIntroEntryContext(listItem.Artist))
				{
					await ShowArtistIntroductionDialog(listItem.Artist);
				}
				else
				{
					await OpenArtistAsync(listItem.Artist);
				}
			}
			break;
		case ListItemType.Podcast:
			if (listItem.Podcast != null)
			{
				await OpenPodcastRadioAsync(listItem.Podcast);
			}
			break;
                case ListItemType.PodcastEpisode:
                        if (listItem.PodcastEpisode?.Song != null)
                        {
                                AnnounceSongLoadingForActivation(listItem.PodcastEpisode.Song);
                                await PlaySong(listItem.PodcastEpisode.Song);
                        }
                        break;
		case ListItemType.Category:
			await LoadCategoryContent(listItem.CategoryId);
			break;
		}
	}

	private bool IsArtistIntroEntryContext(ArtistInfo artist)
	{
		if (artist == null || string.IsNullOrWhiteSpace(_currentViewSource))
		{
			return false;
		}
		if (!_currentViewSource.StartsWith("artist_entries:", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		long num = ParseArtistIdFromViewSource(_currentViewSource, "artist_entries:");
		if (num > 0)
		{
			return num == artist.Id;
		}
		if (_currentArtist != null && _currentArtist.Id == artist.Id)
		{
			return true;
		}
		if (_currentArtistDetail != null && _currentArtistDetail.Id == artist.Id)
		{
			return true;
		}
		return false;
	}

	private async Task ShowArtistIntroductionDialog(ArtistInfo artist)
	{
		try
		{
			ArtistDetail artistDetail = ((_currentArtistDetail != null && _currentArtistDetail.Id == artist.Id) ? _currentArtistDetail : (await _apiClient.GetArtistDetailAsync(artist.Id)));
			ArtistDetail detail = artistDetail;
			if (detail == null)
			{
				MessageBox.Show("暂时无法获取该歌手的详细介绍。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				return;
			}
			if (string.IsNullOrWhiteSpace(detail.Name))
			{
				detail.Name = artist.Name;
			}
			if (string.IsNullOrWhiteSpace(detail.Alias))
			{
				detail.Alias = artist.Alias;
			}
			using ArtistDetailDialog dialog = new ArtistDetailDialog(detail);
			dialog.ShowDialog(this);
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Exception ex3 = ex2;
			MessageBox.Show("加载歌手介绍失败: " + ex3.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
		}
	}

	private async Task LoadCategoryContent(string categoryId, bool skipSave = false)
	{
		try
		{
			double timeSinceStartup = (DateTime.Now - _appStartTime).TotalSeconds;
			if (timeSinceStartup < 3.0)
			{
				int delayMs = checked((int)((3.0 - timeSinceStartup) * 1000.0));
				Debug.WriteLine($"[ColdStartProtection] 应用启动仅 {timeSinceStartup:F1}秒，延迟 {delayMs}ms 以避免风控");
				await Task.Delay(Math.Min(delayMs, 2000));
			}
			UpdateStatusBar("正在加载 " + categoryId + "...");
			int categoryOffset;
			string normalizedCategoryId = StripOffsetSuffix(categoryId, out categoryOffset);
			if (!skipSave)
			{
				SaveNavigationState();
			}
			_isHomePage = false;
			switch (normalizedCategoryId)
			{
			case "user_liked_songs":
				await LoadUserLikedSongs();
				return;
			case "user_playlists":
				await LoadUserPlaylists();
				return;
			case "user_albums":
				await LoadUserAlbums();
				return;
			case "user_podcasts":
				await LoadUserPodcasts();
				return;
			case "user_cloud":
				_cloudPage = 1;
				await LoadCloudSongsAsync();
				return;
			case "recent_play":
				await LoadRecentPlayedSongsAsync();
				return;
			case "recent_listened":
				await LoadRecentListenedCategoryAsync(skipSave);
				return;
			}
			if (normalizedCategoryId != null)
			{
				if (normalizedCategoryId != null && normalizedCategoryId.StartsWith("listen-match:", StringComparison.OrdinalIgnoreCase))
				{
					await LoadListenMatchAsync(NormalizeListenMatchViewSource(categoryId), skipSave: true);
					return;
				}
				switch (normalizedCategoryId)
				{
				case "recent_playlists":
					await LoadRecentPlaylistsAsync();
					return;
				case "recent_albums":
					await LoadRecentAlbumsAsync();
					return;
				case "recent_podcasts":
					await LoadRecentPodcastsAsync();
					return;
				case "daily_recommend":
					await LoadDailyRecommend();
					return;
				case "personalized":
					await LoadPersonalized();
					return;
				case PersonalFmCategoryId:
					await LoadPersonalFm();
					return;
				case "toplist":
					await LoadToplist(offset: categoryOffset, skipSave: true);
					return;
				}
				string s = normalizedCategoryId;
				if (s != null && s.StartsWith("listen-match:", StringComparison.OrdinalIgnoreCase))
				{
					await LoadListenMatchAsync(categoryId, skipSave: true);
					return;
				}
				switch (normalizedCategoryId)
				{
				case "daily_recommend_songs":
					await LoadDailyRecommendSongs();
					return;
				case "daily_recommend_playlists":
					await LoadDailyRecommendPlaylists();
					return;
				case "personalized_playlists":
					await LoadPersonalizedPlaylists();
					return;
			case "personalized_newsongs":
				await LoadPersonalizedNewSongs();
				return;
			case "highquality_playlists":
				await LoadHighQualityPlaylists(categoryOffset, skipSave: true);
				return;
				case "new_songs":
					await LoadNewSongs();
					return;
				case "new_songs_all":
					await LoadNewSongsByArea(0, "全部", "new_songs_all", categoryOffset, skipSave: true);
					return;
				case "new_songs_chinese":
					await LoadNewSongsByArea(7, "华语", "new_songs_chinese", categoryOffset, skipSave: true);
					return;
				case "new_songs_western":
					await LoadNewSongsByArea(96, "欧美", "new_songs_western", categoryOffset, skipSave: true);
					return;
				case "new_songs_japan":
					await LoadNewSongsByArea(8, "日本", "new_songs_japan", categoryOffset, skipSave: true);
					return;
				case "new_songs_korea":
					await LoadNewSongsByArea(16, "韩国", "new_songs_korea", categoryOffset, skipSave: true);
					return;
				case "personalized_newsong":
					await LoadPersonalizedNewSong();
					return;
				case "playlist_category":
					await LoadPlaylistCategory();
					return;
				case "podcast_categories":
					await LoadPodcastCategoriesAsync();
					return;
				case "new_albums":
					await LoadNewAlbums();
					return;
				case "artist_favorites":
					await LoadArtistFavoritesAsync(skipSave: true);
					return;
				case "artist_categories":
					await LoadArtistCategoryTypesAsync(skipSave: true);
					return;
				case "new_album_categories":
					await LoadAlbumCategoryTypesAsync(skipSave: true);
					return;
				}
			}
			long artistTopId;
			long artistSongsId;
			long artistAlbumsId;
			int typeCode;
			int albumTypeCode;
			if (normalizedCategoryId.StartsWith("playlist_cat_", StringComparison.OrdinalIgnoreCase))
			{
				ParsePlaylistCategoryViewSource(categoryId, out var catName, out var catOffset);
				if (!string.IsNullOrWhiteSpace(catName))
				{
					await LoadPlaylistsByCat(catName, catOffset, skipSave: true);
				}
				else
				{
					MessageBox.Show("未知的分类: " + categoryId, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				}
			}
			else if (categoryId.StartsWith("podcast_cat_", StringComparison.OrdinalIgnoreCase))
			{
				ParsePodcastCategoryViewSource(categoryId, out var podcastCatId, out var catOffset);
				await LoadPodcastsByCategoryAsync(podcastCatId, catOffset, skipSave: true);
			}
			else if (categoryId.StartsWith("artist_top_", StringComparison.OrdinalIgnoreCase) && long.TryParse(categoryId.Substring("artist_top_".Length), out artistTopId))
			{
				await LoadArtistTopSongsAsync(artistTopId, skipSave: true);
			}
			else if (categoryId.StartsWith("artist_songs_", StringComparison.OrdinalIgnoreCase) && long.TryParse(categoryId.Substring("artist_songs_".Length), out artistSongsId))
			{
				await LoadArtistSongsAsync(artistSongsId, 0, skipSave: true, ArtistSongSortOption.Hot);
			}
			else if (categoryId.StartsWith("artist_albums_", StringComparison.OrdinalIgnoreCase) && long.TryParse(categoryId.Substring("artist_albums_".Length), out artistAlbumsId))
			{
				await LoadArtistAlbumsAsync(artistAlbumsId, 0, skipSave: true, ArtistAlbumSortOption.Latest);
			}
			else if (categoryId.StartsWith("artist_type_", StringComparison.OrdinalIgnoreCase) && int.TryParse(categoryId.Substring("artist_type_".Length), out typeCode))
			{
				await LoadArtistCategoryAreasAsync(typeCode, skipSave: true);
			}
			else if (categoryId.StartsWith("artist_area_", StringComparison.OrdinalIgnoreCase))
			{
				string[] parts = categoryId.Split('_');
				if (parts.Length == 4 && int.TryParse(parts[2], out var typeFilter) && int.TryParse(parts[3], out var areaFilter))
				{
					await LoadArtistsByCategoryAsync(typeFilter, areaFilter, 0, skipSave: true);
				}
				else
				{
					MessageBox.Show("未知的歌手分类: " + categoryId, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				}
			}
			else if (categoryId.StartsWith("new_album_period_", StringComparison.OrdinalIgnoreCase) && int.TryParse(categoryId.Substring("new_album_period_".Length), out albumTypeCode))
			{
				await LoadAlbumCategoryAreasAsync(albumTypeCode, skipSave: true);
			}
			else if (categoryId.StartsWith("new_album_area_", StringComparison.OrdinalIgnoreCase))
			{
				string[] parts = categoryId.Split('_');
				if (parts.Length == 5 && int.TryParse(parts[3], out var typeFilter) && int.TryParse(parts[4], out var areaFilter))
				{
					await LoadAlbumsByCategoryAsync(typeFilter, areaFilter, 0, skipSave: true);
				}
				else
				{
					MessageBox.Show("未知的新碟分类: " + categoryId, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				}
			}
			else
			{
				MessageBox.Show("未知的分类: " + categoryId, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Exception ex3 = ex2;
			Debug.WriteLine($"[LoadCategoryContent] 异常: {ex3}");
			MessageBox.Show("加载分类内容失败: " + ex3.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("加载失败");
		}
	}

	private Task LoadRecentPlayedSongs(bool preserveSelection = false)
	{
		return LoadRecentPlayedSongsAsync();
	}

	private async Task LoadHighQualityPlaylists(int offset = 0, bool skipSave = false, int pendingFocusIndex = -1)
	{
		if (_enableHighQualityPlaylistsAll)
		{
			await LoadHighQualityPlaylistsAll(skipSave, pendingFocusIndex);
			return;
		}
		try
		{
			_currentHighQualityLoadedAll = false;
			string paginationKey = BuildHighQualityPaginationKey();
			bool offsetClamped;
			int normalizedOffset = NormalizeOffsetWithCap(paginationKey, HighQualityPlaylistsPageSize, offset, out offsetClamped);
			if (offsetClamped)
			{
				int page = (normalizedOffset / HighQualityPlaylistsPageSize) + 1;
				UpdateStatusBar($"页码过大，已跳到第 {page} 页");
			}
			offset = normalizedOffset;
			if (!skipSave)
			{
				SaveNavigationState();
			}
			int requestFocusIndex = (pendingFocusIndex >= 0) ? pendingFocusIndex : 0;
			string viewSource = BuildHighQualityViewSource(offset);
			int page2 = (offset / HighQualityPlaylistsPageSize) + 1;
			ViewLoadRequest request = new ViewLoadRequest(viewSource, "精品歌单", "正在加载精品歌单...", !skipSave, requestFocusIndex);
			ViewLoadResult<SearchViewData<PlaylistInfo>> loadResult = await RunViewLoadAsync(request, delegate
			{
				using (WorkScopes.BeginSkeleton("HighQualityPlaylists", request.ViewSource))
				{
					return Task.FromResult(BuildSearchSkeletonViewData("精品歌单", "歌单", page2, offset + 1, _currentPlaylists));
				}
			}, "加载精品歌单已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				SearchViewData<PlaylistInfo> skeleton = loadResult.Value;
				if (HasSkeletonItems(skeleton.Items))
				{
					DisplayPlaylists(skeleton.Items, preserveSelection: false, request.ViewSource, request.AccessibleName, skeleton.StartIndex, showPagination: false, hasNextPage: false, announceHeader: true, suppressFocus: true);
				}
				UpdateStatusBar(request.LoadingText);
				EnrichHighQualityPlaylistsAsync(offset, request.ViewSource, request.AccessibleName, request.PendingFocusIndex);
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Exception ex3 = ex2;
			if (!TryHandleOperationCancelled(ex3, "加载精品歌单已取消"))
			{
				Debug.WriteLine($"[LoadHighQualityPlaylists] 异常: {ex3}");
				MessageBox.Show("加载精品歌单失败: " + ex3.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("加载失败");
			}
		}
	}

	private async Task LoadHighQualityPlaylistsAll(bool skipSave = false, int pendingFocusIndex = -1)
	{
		try
		{
			if (!skipSave)
			{
				SaveNavigationState();
			}
			int requestFocusIndex = (pendingFocusIndex >= 0) ? pendingFocusIndex : 0;
			const int page = 1;
			const int startIndex = 1;
			ViewLoadRequest request = new ViewLoadRequest("highquality_playlists", "精品歌单", "正在加载精品歌单...", !skipSave, requestFocusIndex);
			ViewLoadResult<SearchViewData<PlaylistInfo>> loadResult = await RunViewLoadAsync(request, delegate
			{
				using (WorkScopes.BeginSkeleton("HighQualityPlaylistsAll", request.ViewSource))
				{
					return Task.FromResult(BuildSearchSkeletonViewData("精品歌单", "歌单", page, startIndex, _currentPlaylists));
				}
			}, "加载精品歌单已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				SearchViewData<PlaylistInfo> skeleton = loadResult.Value;
				if (HasSkeletonItems(skeleton.Items))
				{
					DisplayPlaylists(skeleton.Items, preserveSelection: false, request.ViewSource, request.AccessibleName, skeleton.StartIndex, showPagination: false, hasNextPage: false, announceHeader: true, suppressFocus: true);
				}
				UpdateStatusBar(request.LoadingText);
				EnrichHighQualityPlaylistsAllAsync(request.ViewSource, request.AccessibleName, request.PendingFocusIndex);
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Exception ex3 = ex2;
			if (!TryHandleOperationCancelled(ex3, "加载精品歌单已取消"))
			{
				Debug.WriteLine($"[LoadHighQualityPlaylistsAll] 异常: {ex3}");
				MessageBox.Show("加载精品歌单失败: " + ex3.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("加载失败");
			}
		}
	}

	private async Task EnrichHighQualityPlaylistsAllAsync(string viewSource, string accessibleName, int pendingFocusIndex = -1)
	{
		const int pageSize = HighQualityPlaylistsPageSize;
		CancellationToken viewToken = GetCurrentViewContentToken();
		using (WorkScopes.BeginEnrichment("HighQualityPlaylistsAll", viewSource))
		{
			try
			{
			List<PlaylistInfo> all = new List<PlaylistInfo>();
			long before = 0;
			bool hasMore = true;
			int safety = 0;
			while (hasMore && safety < 200)
			{
				viewToken.ThrowIfCancellationRequested();
				(List<PlaylistInfo> Items, long LastTime, bool HasMore) result = await FetchWithRetryUntilCancel((CancellationToken ct) => _apiClient.GetHighQualityPlaylistsAsync("全部", pageSize, before), $"highquality:all:before{before}:page{(safety + 1)}", viewToken, delegate(int attempt, Exception _)
				{
					SafeInvoke(delegate
					{
						UpdateStatusBar($"加载精品歌单失败，正在重试（第 {attempt} 次）...");
					});
				}).ConfigureAwait(continueOnCapturedContext: false);
				List<PlaylistInfo> pageItems = result.Items ?? new List<PlaylistInfo>();
				if (pageItems.Count == 0)
				{
					hasMore = false;
					break;
				}
				all.AddRange(pageItems);
				long nextBefore = result.LastTime;
				hasMore = result.HasMore;
				if (pageItems.Count < pageSize || nextBefore <= 0 || nextBefore == before)
				{
					hasMore = false;
				}
				before = nextBefore;
				safety++;
				SafeInvoke(delegate
				{
					UpdateStatusBar($"正在加载精品歌单... 已获取 {all.Count} 个");
				});
			}
			if (viewToken.IsCancellationRequested)
			{
				return;
			}
			await ExecuteOnUiThreadAsync(delegate
			{
				if (!ShouldAbortViewRender(viewToken, "加载精品歌单"))
				{
					_currentPlaylists = CloneList(all);
					_currentHighQualityLoadedAll = true;
					_currentHighQualityHasMore = false;
					_currentHighQualityOffset = 0;
					_currentHighQualityTotalCount = _currentPlaylists.Count;
					_homeCachedHighQualityCount = _currentHighQualityTotalCount;
					_highQualityCountFetchedUtc = DateTime.UtcNow;
					PatchPlaylists(_currentPlaylists, 1, showPagination: false, hasPreviousPage: false, hasNextPage: false, pendingFocusIndex);
					FocusListAfterEnrich(pendingFocusIndex);
					if (_currentPlaylists.Count == 0)
					{
						MessageBox.Show("暂无精品歌单", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
						UpdateStatusBar("暂无精品歌单");
					}
					else
					{
						UpdateStatusBar($"已加载 {_currentPlaylists.Count} 个精品歌单");
					}
				}
				}).ConfigureAwait(continueOnCapturedContext: false);
				await EnsureLibraryStateFreshAsync(LibraryEntityType.Playlists);
			}
			catch (Exception ex)
			{
				Exception ex2 = ex;
				if (TryHandleOperationCancelled(ex2, "加载精品歌单已取消"))
				{
					return;
				}
				Debug.WriteLine($"[LoadHighQualityPlaylistsAll] 异常: {ex2}");
				await ExecuteOnUiThreadAsync(delegate
				{
					if (!ShouldAbortViewRender(viewToken, "加载精品歌单"))
					{
						MessageBox.Show("加载精品歌单失败: " + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
						UpdateStatusBar("加载失败");
					}
				}).ConfigureAwait(continueOnCapturedContext: false);
			}
		}
	}

	private async Task EnrichHighQualityPlaylistsAsync(int offset, string viewSource, string accessibleName, int pendingFocusIndex = -1)
	{
		const int pageSize = HighQualityPlaylistsPageSize;
		CancellationToken viewToken = GetCurrentViewContentToken();
		using (WorkScopes.BeginEnrichment("HighQualityPlaylists", viewSource))
		{
			try
			{
				List<PlaylistInfo> playlists = null;
				bool hasMore = false;
				lock (_highQualityCacheLock)
				{
					if (_highQualityPlaylistsCache.TryGetValue(offset, out var cached))
					{
						playlists = new List<PlaylistInfo>(cached);
						_highQualityHasMoreByOffset.TryGetValue(offset, out hasMore);
					}
				}
				if (playlists == null)
				{
					long before = await ResolveHighQualityBeforeAsync(offset, viewToken).ConfigureAwait(continueOnCapturedContext: false);
					(List<PlaylistInfo> Items, long LastTime, bool HasMore) result = await FetchWithRetryUntilCancel((CancellationToken ct) => _apiClient.GetHighQualityPlaylistsAsync("全部", pageSize, before), $"highquality:before{before}:offset{offset}", viewToken, delegate(int attempt, Exception _)
					{
						SafeInvoke(delegate
						{
							UpdateStatusBar($"加载精品歌单失败，正在重试（第 {attempt} 次）...");
						});
					}).ConfigureAwait(continueOnCapturedContext: false);
					playlists = result.Items ?? new List<PlaylistInfo>();
					hasMore = result.HasMore;
					if (playlists.Count > 0)
					{
						CacheHighQualityPage(offset, playlists, hasMore, before, result.LastTime);
					}
					if (!hasMore)
					{
						SetPaginationOffsetCap(BuildHighQualityPaginationKey(), offset);
					}
				}
				string paginationKey = BuildHighQualityPaginationKey();
				if (playlists.Count == 0 && offset > 0)
				{
					HandleHighQualityEmptyPage(offset, viewSource, accessibleName);
					return;
				}
				if (playlists.Count > 0 && playlists.Count < pageSize)
				{
					hasMore = false;
					SetPaginationOffsetCap(paginationKey, offset);
				}
				int totalCount = _currentHighQualityTotalCount;
				if (!hasMore)
				{
					totalCount = Math.Max(totalCount, offset + playlists.Count);
				}
				if (TryHandlePaginationEmptyResult(paginationKey, offset, pageSize, totalCount, playlists.Count, hasMore, viewSource, accessibleName))
				{
					return;
				}
				if (viewToken.IsCancellationRequested)
				{
					return;
				}
				await ExecuteOnUiThreadAsync(delegate
				{
					if (!ShouldAbortViewRender(viewToken, "加载精品歌单"))
					{
						_currentPlaylists = CloneList(playlists);
						_currentHighQualityHasMore = hasMore;
						_currentHighQualityOffset = offset;
						if (!hasMore)
						{
							_currentHighQualityTotalCount = Math.Max(totalCount, offset + _currentPlaylists.Count);
						}
						bool showPagination = offset > 0 || hasMore;
						PatchPlaylists(_currentPlaylists, offset + 1, showPagination: showPagination, hasPreviousPage: offset > 0, hasNextPage: hasMore, pendingFocusIndex);
						FocusListAfterEnrich(pendingFocusIndex);
						if (_currentPlaylists.Count == 0)
						{
							MessageBox.Show("暂无精品歌单", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
							UpdateStatusBar("暂无精品歌单");
						}
						else
						{
							int page = (offset / pageSize) + 1;
							if (!hasMore && _currentHighQualityTotalCount > 0)
							{
								int maxPage = Math.Max(1, (int)Math.Ceiling((double)_currentHighQualityTotalCount / pageSize));
								UpdateStatusBar($"第 {page}/{maxPage} 页，本页 {_currentPlaylists.Count} 个 / 总 {_currentHighQualityTotalCount} 个");
							}
							else
							{
								UpdateStatusBar($"第 {page} 页，本页 {_currentPlaylists.Count} 个{(hasMore ? "，还有更多" : string.Empty)}");
							}
						}
					}
				}).ConfigureAwait(continueOnCapturedContext: false);
				await EnsureLibraryStateFreshAsync(LibraryEntityType.Playlists);
			}
			catch (Exception ex)
			{
				Exception ex2 = ex;
				string paginationKey = BuildHighQualityPaginationKey();
				if (TryHandlePaginationOffsetError(ex2, paginationKey, offset, pageSize, viewSource, accessibleName))
				{
					return;
				}
				if (TryHandleOperationCancelled(ex2, "加载精品歌单已取消"))
				{
					return;
				}
				Debug.WriteLine($"[LoadHighQualityPlaylists] 异常: {ex2}");
				await ExecuteOnUiThreadAsync(delegate
				{
					if (!ShouldAbortViewRender(viewToken, "加载精品歌单"))
					{
						MessageBox.Show("加载精品歌单失败: " + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
						UpdateStatusBar("加载失败");
					}
				}).ConfigureAwait(continueOnCapturedContext: false);
			}
		}
	}

	private async Task<long> ResolveHighQualityBeforeAsync(int targetOffset, CancellationToken token)
	{
		if (targetOffset <= 0)
		{
			return 0;
		}
		lock (_highQualityCacheLock)
		{
			if (_highQualityBeforeByOffset.TryGetValue(targetOffset, out var before))
			{
				return before;
			}
			if (!_highQualityBeforeByOffset.ContainsKey(0))
			{
				_highQualityBeforeByOffset[0] = 0;
			}
		}
		int currentOffset = 0;
		long currentBefore = 0;
		lock (_highQualityCacheLock)
		{
			foreach (var kv in _highQualityBeforeByOffset)
			{
				if (kv.Key <= targetOffset && kv.Key >= currentOffset)
				{
					currentOffset = kv.Key;
					currentBefore = kv.Value;
				}
			}
		}
		while (currentOffset < targetOffset)
		{
			token.ThrowIfCancellationRequested();
			bool hasMore = false;
			long nextBefore = 0;
			lock (_highQualityCacheLock)
			{
				_highQualityHasMoreByOffset.TryGetValue(currentOffset, out hasMore);
				_highQualityBeforeByOffset.TryGetValue(currentOffset + HighQualityPlaylistsPageSize, out nextBefore);
			}
			if (nextBefore == 0 && (currentOffset + HighQualityPlaylistsPageSize) <= targetOffset)
			{
				(List<PlaylistInfo> Items, long LastTime, bool HasMore) result = await FetchWithRetryUntilCancel((CancellationToken ct) => _apiClient.GetHighQualityPlaylistsAsync("全部", HighQualityPlaylistsPageSize, currentBefore), $"highquality:before{currentBefore}:offset{currentOffset}", token, delegate(int attempt, Exception _)
				{
					SafeInvoke(delegate
					{
						UpdateStatusBar($"加载精品歌单失败，正在重试（第 {attempt} 次）...");
					});
				}).ConfigureAwait(continueOnCapturedContext: false);
				List<PlaylistInfo> pageItems = result.Items ?? new List<PlaylistInfo>();
				hasMore = result.HasMore;
				if (pageItems.Count == 0)
				{
					hasMore = false;
				}
				nextBefore = result.LastTime;
				lock (_highQualityCacheLock)
				{
					_highQualityHasMoreByOffset[currentOffset] = hasMore;
					if (!_highQualityBeforeByOffset.ContainsKey(currentOffset))
					{
						_highQualityBeforeByOffset[currentOffset] = currentBefore;
					}
					if (nextBefore > 0)
					{
						_highQualityBeforeByOffset[currentOffset + HighQualityPlaylistsPageSize] = nextBefore;
					}
				}
				if (pageItems.Count == 0)
				{
					int capOffset = Math.Max(0, currentOffset - HighQualityPlaylistsPageSize);
					SetPaginationOffsetCap(BuildHighQualityPaginationKey(), capOffset);
					_currentHighQualityTotalCount = Math.Max(_currentHighQualityTotalCount, capOffset + ResolveHighQualityCachedCount(capOffset));
					return currentBefore;
				}
				if (!hasMore)
				{
					SetPaginationOffsetCap(BuildHighQualityPaginationKey(), currentOffset);
					_currentHighQualityTotalCount = Math.Max(_currentHighQualityTotalCount, currentOffset + pageItems.Count);
					if (currentOffset + HighQualityPlaylistsPageSize < targetOffset)
					{
						return currentBefore;
					}
				}
			}
			if (!hasMore && currentOffset + HighQualityPlaylistsPageSize < targetOffset)
			{
				SetPaginationOffsetCap(BuildHighQualityPaginationKey(), currentOffset);
				return currentBefore;
			}
			currentOffset = checked(currentOffset + HighQualityPlaylistsPageSize);
			currentBefore = nextBefore;
			if (currentBefore == 0 && currentOffset < targetOffset)
			{
				SetPaginationOffsetCap(BuildHighQualityPaginationKey(), Math.Max(0, currentOffset - HighQualityPlaylistsPageSize));
				break;
			}
		}
		lock (_highQualityCacheLock)
		{
			if (_highQualityBeforeByOffset.TryGetValue(targetOffset, out var resolved))
			{
				return resolved;
			}
		}
		return currentBefore;
	}

	private Task LoadNewSongs()
	{
		try
		{
			UpdateStatusBar("正在加载新歌速递...");
			List<ListItemInfo> items = new List<ListItemInfo>
			{
				new ListItemInfo
				{
					Type = ListItemType.Category,
					CategoryId = "new_songs_all",
					CategoryName = "全部"
				},
				new ListItemInfo
				{
					Type = ListItemType.Category,
					CategoryId = "new_songs_chinese",
					CategoryName = "华语"
				},
				new ListItemInfo
				{
					Type = ListItemType.Category,
					CategoryId = "new_songs_western",
					CategoryName = "欧美"
				},
				new ListItemInfo
				{
					Type = ListItemType.Category,
					CategoryId = "new_songs_japan",
					CategoryName = "日本"
				},
				new ListItemInfo
				{
					Type = ListItemType.Category,
					CategoryId = "new_songs_korea",
					CategoryName = "韩国"
				}
			};
			DisplayListItems(items, "new_songs", "新歌速递分类");
			UpdateStatusBar("请选择地区");
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"[LoadNewSongs] 异常: {ex}");
			MessageBox.Show("加载新歌速递失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("加载失败");
		}
		return Task.CompletedTask;
	}

	private Task LoadNewSongsAll(int offset = 0, bool skipSave = false, int pendingFocusIndex = -1)
	{
		return LoadNewSongsByArea(0, "全部", "new_songs_all", offset, skipSave, pendingFocusIndex);
	}

	private Task LoadNewSongsChinese(int offset = 0, bool skipSave = false, int pendingFocusIndex = -1)
	{
		return LoadNewSongsByArea(7, "华语", "new_songs_chinese", offset, skipSave, pendingFocusIndex);
	}

	private Task LoadNewSongsWestern(int offset = 0, bool skipSave = false, int pendingFocusIndex = -1)
	{
		return LoadNewSongsByArea(96, "欧美", "new_songs_western", offset, skipSave, pendingFocusIndex);
	}

	private Task LoadNewSongsJapan(int offset = 0, bool skipSave = false, int pendingFocusIndex = -1)
	{
		return LoadNewSongsByArea(8, "日本", "new_songs_japan", offset, skipSave, pendingFocusIndex);
	}

	private Task LoadNewSongsKorea(int offset = 0, bool skipSave = false, int pendingFocusIndex = -1)
	{
		return LoadNewSongsByArea(16, "韩国", "new_songs_korea", offset, skipSave, pendingFocusIndex);
	}

	private async Task LoadNewSongsByArea(int areaType, string areaName, string viewSource, int offset = 0, bool skipSave = false, int pendingFocusIndex = -1)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(viewSource))
			{
				viewSource = ResolveNewSongsViewSource(areaType);
			}
			List<SongInfo> cached = null;
			lock (_newSongsCacheLock)
			{
				_newSongsCacheByArea.TryGetValue(areaType, out cached);
			}
			if (cached != null && cached.Count > 0)
			{
				bool offsetClamped;
				int normalizedOffset = NormalizeOffsetWithTotal(cached.Count, NewSongsPageSize, offset, out offsetClamped);
				if (offsetClamped)
				{
					int page = (normalizedOffset / NewSongsPageSize) + 1;
					UpdateStatusBar($"页码过大，已跳到第 {page} 页");
				}
				offset = normalizedOffset;
			}
			if (!skipSave)
			{
				SaveNavigationState();
			}
			string requestViewSource = BuildNewSongsViewSource(areaType, offset);
			int page2 = (Math.Max(0, offset) / NewSongsPageSize) + 1;
			int requestFocusIndex = (pendingFocusIndex >= 0) ? pendingFocusIndex : 0;
			ViewLoadRequest request = new ViewLoadRequest(requestViewSource, areaName + "新歌速递", "正在加载" + areaName + "新歌...", !skipSave, requestFocusIndex);
			ViewLoadResult<SearchViewData<SongInfo>> loadResult = await RunViewLoadAsync(request, delegate
			{
				using (WorkScopes.BeginSkeleton("NewSongs", request.ViewSource))
				{
					return Task.FromResult(BuildSearchSkeletonViewData(areaName + "新歌", "歌曲", page2, offset + 1, _currentSongs));
				}
			}, "加载" + areaName + "新歌已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				SearchViewData<SongInfo> skeleton = loadResult.Value;
				_currentPlaylist = null;
				if (HasSkeletonItems(skeleton.Items))
				{
					DisplaySongs(skeleton.Items, showPagination: false, hasNextPage: false, skeleton.StartIndex, preserveSelection: false, request.ViewSource, request.AccessibleName);
				}
				UpdateStatusBar(request.LoadingText);
				EnrichNewSongsByAreaAsync(areaType, areaName, offset, request.ViewSource, request.AccessibleName, pendingFocusIndex);
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Exception ex3 = ex2;
			if (!TryHandleOperationCancelled(ex3, "加载" + areaName + "新歌已取消"))
			{
				Debug.WriteLine($"[LoadNewSongsByArea] 异常: {ex3}");
				MessageBox.Show("加载" + areaName + "新歌失败: " + ex3.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("加载失败");
			}
		}
	}

	private async Task EnrichNewSongsByAreaAsync(int areaType, string areaName, int offset, string viewSource, string accessibleName, int pendingFocusIndex = -1)
	{
		const int pageSize = NewSongsPageSize;
		CancellationToken viewToken = GetCurrentViewContentToken();
		using (WorkScopes.BeginEnrichment("NewSongs", viewSource))
		{
			try
			{
				List<SongInfo> songs;
				lock (_newSongsCacheLock)
				{
					_newSongsCacheByArea.TryGetValue(areaType, out songs);
				}
				if (songs == null || songs.Count == 0)
				{
					songs = await FetchWithRetryUntilCancel((CancellationToken ct) => _apiClient.GetNewSongsAsync(areaType), $"new_songs:{areaType}", viewToken, delegate(int attempt, Exception _)
					{
						SafeInvoke(delegate
						{
							UpdateStatusBar($"加载新歌失败，正在重试（第 {attempt} 次）...");
						});
					}).ConfigureAwait(continueOnCapturedContext: true);
					songs ??= new List<SongInfo>();
					lock (_newSongsCacheLock)
					{
						_newSongsCacheByArea[areaType] = songs;
					}
				}
				int totalCount = songs.Count;
				bool offsetClamped;
				int normalizedOffset = NormalizeOffsetWithTotal(totalCount, pageSize, offset, out offsetClamped);
				if (offsetClamped)
				{
					int page = (normalizedOffset / pageSize) + 1;
					UpdateStatusBar($"页码过大，已跳到第 {page} 页");
				}
				List<SongInfo> pageItems = songs.Skip(normalizedOffset).Take(pageSize).ToList();
				bool hasMore = normalizedOffset + pageSize < totalCount;
				string paginationKey = BuildNewSongsPaginationKey(areaType);
				if (TryHandlePaginationEmptyResult(paginationKey, normalizedOffset, pageSize, totalCount, pageItems.Count, hasMore, viewSource, accessibleName))
				{
					return;
				}
				int page2 = (normalizedOffset / pageSize) + 1;
				int maxPage = (totalCount > 0) ? Math.Max(1, (int)Math.Ceiling((double)totalCount / pageSize)) : page2;
				if (viewToken.IsCancellationRequested)
				{
					return;
				}
				await ExecuteOnUiThreadAsync(delegate
				{
					if (!ShouldAbortViewRender(viewToken, "加载新歌速递"))
					{
						_currentPlaylist = null;
						_currentSongs = CloneList(pageItems);
						_currentNewSongsHasMore = hasMore;
						_currentNewSongsTotalCount = Math.Max(totalCount, normalizedOffset + _currentSongs.Count);
						_currentNewSongsOffset = normalizedOffset;
						_currentNewSongsAreaType = areaType;
						_currentNewSongsAreaName = areaName;
						bool showPagination = normalizedOffset > 0 || hasMore;
						PatchSongs(_currentSongs, normalizedOffset + 1, skipAvailabilityCheck: false, showPagination: showPagination, hasPreviousPage: normalizedOffset > 0, hasNextPage: hasMore, pendingFocusIndex);
						FocusListAfterEnrich(pendingFocusIndex);
						if (_currentSongs.Count == 0)
						{
							UpdateStatusBar(areaName + "新歌速递");
						}
						else if (totalCount > 0)
						{
							UpdateStatusBar($"第 {page2}/{maxPage} 页，本页 {_currentSongs.Count} 首 / 总 {totalCount} 首");
						}
						else
						{
							UpdateStatusBar($"第 {page2} 页，本页 {_currentSongs.Count} 首{(hasMore ? "，还有更多" : string.Empty)}");
						}
					}
				}).ConfigureAwait(continueOnCapturedContext: false);
				await EnsureLibraryStateFreshAsync(LibraryEntityType.Songs);
				Debug.WriteLine($"[LoadNewSongs] 成功加载 {pageItems.Count} 首{areaName}新歌 offset={normalizedOffset}");
			}
			catch (Exception ex)
			{
				Exception ex2 = ex;
				if (TryHandleOperationCancelled(ex2, "加载" + areaName + "新歌已取消"))
				{
					return;
				}
				Debug.WriteLine($"[LoadNewSongsByArea] 异常: {ex2}");
				await ExecuteOnUiThreadAsync(delegate
				{
					if (!ShouldAbortViewRender(viewToken, "加载新歌速递"))
					{
						MessageBox.Show("加载" + areaName + "新歌失败: " + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
						UpdateStatusBar("加载失败");
					}
				}).ConfigureAwait(continueOnCapturedContext: false);
			}
		}
	}

	private async Task LoadPersonalizedNewSong(int pendingFocusIndex = -1)
	{
		try
		{
			int requestFocusIndex = (pendingFocusIndex >= 0) ? pendingFocusIndex : 0;
			ViewLoadRequest request = new ViewLoadRequest("personalized_newsong", "推荐新歌", "正在加载推荐新歌...", cancelActiveNavigation: true, pendingFocusIndex: requestFocusIndex);
			if (!(await RunViewLoadAsync(request, delegate
			{
				using (WorkScopes.BeginSkeleton("PersonalizedNewSong", request.ViewSource))
				{
					return Task.FromResult(result: true);
				}
			}, "加载推荐新歌已取消").ConfigureAwait(continueOnCapturedContext: true)).IsCanceled)
			{
				UpdateStatusBar(request.LoadingText);
				EnrichPersonalizedNewSongAsync(request.ViewSource, request.AccessibleName, request.PendingFocusIndex);
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Exception ex3 = ex2;
			if (!TryHandleOperationCancelled(ex3, "加载推荐新歌已取消"))
			{
				Debug.WriteLine($"[LoadPersonalizedNewSong] 异常: {ex3}");
				MessageBox.Show("加载推荐新歌失败: " + ex3.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("加载失败");
			}
		}
	}

	private async Task EnrichPersonalizedNewSongAsync(string viewSource, string accessibleName, int pendingFocusIndex)
	{
		CancellationToken viewToken = GetCurrentViewContentToken();
		using (WorkScopes.BeginEnrichment("PersonalizedNewSong", viewSource))
		{
			try
			{
				List<SongInfo> songsResult = (await FetchWithRetryUntilCancel((CancellationToken ct) => _apiClient.GetPersonalizedNewSongsAsync(), "personalized:new_song", viewToken, delegate(int attempt, Exception _)
				{
					SafeInvoke(delegate
					{
						UpdateStatusBar($"加载推荐新歌失败，正在重试（第 {attempt} 次）...");
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
					if (!ShouldAbortViewRender(viewToken, "推荐新歌"))
					{
						DisplaySongs(songsResult, showPagination: false, hasNextPage: false, 1, preserveSelection: true, viewSource, accessibleName, skipAvailabilityCheck: false, announceHeader: false);
						_currentPlaylist = null;
						FocusListAfterEnrich(pendingFocusIndex);
						if (songsResult.Count == 0)
						{
							MessageBox.Show("暂无推荐新歌", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
							UpdateStatusBar("暂无推荐新歌");
						}
						else
						{
							UpdateStatusBar($"加载完成，共 {songsResult.Count} 首推荐新歌");
						}
					}
				}).ConfigureAwait(continueOnCapturedContext: false);
				await EnsureLibraryStateFreshAsync(LibraryEntityType.Songs);
				Debug.WriteLine($"[LoadPersonalizedNewSong] 成功加载 {songsResult.Count} 首推荐新歌");
			}
			catch (Exception ex)
			{
				Exception ex2 = ex;
				if (TryHandleOperationCancelled(ex2, "加载推荐新歌已取消"))
				{
					return;
				}
				Debug.WriteLine($"[LoadPersonalizedNewSong] 异常: {ex2}");
				await ExecuteOnUiThreadAsync(delegate
				{
					if (!ShouldAbortViewRender(viewToken, "推荐新歌"))
					{
						MessageBox.Show("加载推荐新歌失败: " + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
						UpdateStatusBar("加载失败");
					}
				}).ConfigureAwait(continueOnCapturedContext: false);
			}
		}
	}

	private Task LoadPlaylistCategory()
	{
		try
		{
			UpdateStatusBar("正在加载歌单分类...");
			List<ListItemInfo> items = _homePlaylistCategoryPresets.Select(((string Cat, string DisplayName, string Description) preset) => new ListItemInfo
			{
				Type = ListItemType.Category,
				CategoryId = "playlist_cat_" + preset.Cat,
				CategoryName = preset.DisplayName
			}).ToList();
			DisplayListItems(items, "playlist_category", "歌单分类");
			UpdateStatusBar("请选择歌单分类");
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"[LoadPlaylistCategory] 异常: {ex}");
			MessageBox.Show("加载歌单分类失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("加载失败");
		}
		return Task.CompletedTask;
	}

	private async Task LoadPlaylistsByCat(string cat, int offset = 0, bool skipSave = false, int pendingFocusIndex = -1)
	{
		try
		{
			string paginationKey = BuildPlaylistCategoryPaginationKey(cat);
			bool offsetClamped;
			int normalizedOffset = NormalizeOffsetWithCap(paginationKey, PlaylistCategoryPageSize, offset, out offsetClamped);
			if (offsetClamped)
			{
				int page = (normalizedOffset / PlaylistCategoryPageSize) + 1;
				UpdateStatusBar($"页码过大，已跳到第 {page} 页");
			}
			offset = normalizedOffset;
			if (!skipSave)
			{
				SaveNavigationState();
			}
			string viewSource = BuildPlaylistCategoryViewSource(cat, offset);
			int page2 = (offset / PlaylistCategoryPageSize) + 1;
			int requestFocusIndex = (pendingFocusIndex >= 0) ? pendingFocusIndex : 0;
			ViewLoadRequest request = new ViewLoadRequest(viewSource, cat + "歌单", "正在加载" + cat + "歌单...", !skipSave, requestFocusIndex);
			ViewLoadResult<SearchViewData<PlaylistInfo>> loadResult = await RunViewLoadAsync(request, delegate
			{
				using (WorkScopes.BeginSkeleton("PlaylistCategory", viewSource))
				{
					return Task.FromResult(BuildSearchSkeletonViewData(cat, "歌单", page2, offset + 1, _currentPlaylists));
				}
			}, "加载" + cat + "歌单已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				SearchViewData<PlaylistInfo> skeleton = loadResult.Value;
				if (HasSkeletonItems(skeleton.Items))
				{
					DisplayPlaylists(skeleton.Items, preserveSelection: false, viewSource, request.AccessibleName, skeleton.StartIndex, showPagination: false, hasNextPage: false, announceHeader: true, suppressFocus: true);
				}
				UpdateStatusBar(request.LoadingText);
				EnrichPlaylistsByCatAsync(cat, offset, viewSource, request.AccessibleName, pendingFocusIndex);
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Exception ex3 = ex2;
			if (!TryHandleOperationCancelled(ex3, "加载" + cat + "歌单已取消"))
			{
				Debug.WriteLine($"[LoadPlaylistsByCat] 异常: {ex3}");
				MessageBox.Show("加载" + cat + "歌单失败: " + ex3.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("加载失败");
			}
		}
	}

	private async Task EnrichPlaylistsByCatAsync(string cat, int offset, string viewSource, string accessibleName, int pendingFocusIndex = -1)
	{
		const int pageSize = PlaylistCategoryPageSize;
		CancellationToken viewToken = GetCurrentViewContentToken();
		using (WorkScopes.BeginEnrichment("PlaylistCategory", viewSource))
		{
			try
			{
				(List<PlaylistInfo> Items, long TotalCount, bool HasMore) result = await FetchWithRetryUntilCancel((CancellationToken ct) => _apiClient.GetPlaylistsByCategoryAsync(cat, "hot", pageSize, offset), $"playlist_cat:{cat}:offset{offset}", viewToken, delegate(int attempt, Exception _)
				{
					SafeInvoke(delegate
					{
						UpdateStatusBar($"加载歌单分类失败，正在重试（第 {attempt} 次）...");
					});
				}).ConfigureAwait(continueOnCapturedContext: true);
				List<PlaylistInfo> playlists = result.Items ?? new List<PlaylistInfo>();
				int totalCount = (result.TotalCount > int.MaxValue) ? int.MaxValue : (int)result.TotalCount;
				bool hasMore = result.HasMore;
				string paginationKey = BuildPlaylistCategoryPaginationKey(cat);
				if (TryHandlePaginationEmptyResult(paginationKey, offset, pageSize, totalCount, playlists.Count, hasMore, viewSource, accessibleName))
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
					if (!ShouldAbortViewRender(viewToken, "加载歌单分类"))
					{
						_currentPlaylists = CloneList(playlists);
						_currentPlaylistCategoryHasMore = hasMore;
						_currentPlaylistCategoryTotalCount = Math.Max(totalCount, offset + _currentPlaylists.Count);
						_currentPlaylistCategoryOffset = offset;
						_currentPlaylistCategoryName = cat;
						bool showPagination = offset > 0 || hasMore;
						PatchPlaylists(_currentPlaylists, offset + 1, showPagination: showPagination, hasPreviousPage: offset > 0, hasNextPage: hasMore, pendingFocusIndex);
						FocusListAfterEnrich(pendingFocusIndex);
						if (_currentPlaylists.Count == 0)
						{
							UpdateStatusBar("暂无" + cat + "歌单");
						}
						else if (totalCount > 0)
						{
							UpdateStatusBar($"第 {page}/{maxPage} 页，本页 {_currentPlaylists.Count} 个 / 总 {totalCount} 个");
						}
						else
						{
							UpdateStatusBar($"第 {page} 页，本页 {_currentPlaylists.Count} 个{(hasMore ? "，还有更多" : string.Empty)}");
						}
					}
				}).ConfigureAwait(continueOnCapturedContext: false);
				await EnsureLibraryStateFreshAsync(LibraryEntityType.Playlists);
			}
			catch (Exception ex)
			{
				Exception ex2 = ex;
				string paginationKey = BuildPlaylistCategoryPaginationKey(cat);
				if (TryHandlePaginationOffsetError(ex2, paginationKey, offset, pageSize, viewSource, accessibleName))
				{
					return;
				}
				if (TryHandleOperationCancelled(ex2, "加载" + cat + "歌单已取消"))
				{
					return;
				}
				Debug.WriteLine($"[LoadPlaylistsByCat] 异常: {ex2}");
				await ExecuteOnUiThreadAsync(delegate
				{
					if (!ShouldAbortViewRender(viewToken, "加载歌单分类"))
					{
						MessageBox.Show("加载" + cat + "歌单失败: " + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
						UpdateStatusBar("加载失败");
					}
				}).ConfigureAwait(continueOnCapturedContext: false);
			}
		}
	}

	private async Task LoadPodcastCategoriesAsync()
	{
		try
		{
			ViewLoadRequest request = new ViewLoadRequest("podcast_categories", "播客分类", "正在加载播客分类...");
			ViewLoadResult<List<PodcastCategoryInfo>?> loadResult = await RunViewLoadAsync(request, async delegate(CancellationToken token)
			{
				List<PodcastCategoryInfo> categories2 = await _apiClient.GetPodcastCategoriesAsync(token).ConfigureAwait(continueOnCapturedContext: false);
				token.ThrowIfCancellationRequested();
				return categories2 ?? new List<PodcastCategoryInfo>();
			}, "加载播客分类已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (loadResult.IsCanceled)
			{
				return;
			}
			List<PodcastCategoryInfo> categories = loadResult.Value ?? new List<PodcastCategoryInfo>();
			lock (_podcastCategoryLock)
			{
				_podcastCategories.Clear();
				foreach (PodcastCategoryInfo cat in categories)
				{
					if (cat != null && cat.Id > 0 && !string.IsNullOrWhiteSpace(cat.Name))
					{
						_podcastCategories[cat.Id] = cat;
					}
				}
			}
			_currentPodcastCategoryId = 0;
			_currentPodcastCategoryName = string.Empty;
			_currentPodcastCategoryOffset = 0;
			_currentPodcastCategoryHasMore = false;
			List<ListItemInfo> items = categories.Select((PodcastCategoryInfo podcastCategoryInfo) => new ListItemInfo
			{
				Type = ListItemType.Category,
				CategoryId = "podcast_cat_" + podcastCategoryInfo.Id,
				CategoryName = podcastCategoryInfo.Name
			}).ToList();
			DisplayListItems(items, request.ViewSource, request.AccessibleName);
			if (items.Count == 0)
			{
				MessageBox.Show("暂时没有可用的播客分类。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				UpdateStatusBar("暂无播客分类");
				return;
			}
			UpdateStatusBar($"共 {items.Count} 个播客分类");
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			if (!TryHandleOperationCancelled(ex2, "加载播客分类已取消"))
			{
				Debug.WriteLine($"[LoadPodcastCategories] 异常: {ex2}");
				MessageBox.Show("加载播客分类失败: " + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("加载失败");
			}
		}
	}

	private async Task LoadPodcastsByCategoryAsync(int categoryId, int offset, bool skipSave = false)
	{
		if (categoryId <= 0)
		{
			MessageBox.Show("无法识别播客分类。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			return;
		}
		checked
		{
			string viewSource = string.Empty;
			string accessibleName = "播客列表";
			try
			{
				UpdateStatusBar("正在加载播客...");
				if (!skipSave)
				{
					SaveNavigationState();
				}
				offset = Math.Max(0, offset);
				string paginationKey = BuildPodcastCategoryPaginationKey(categoryId);
				bool offsetClamped;
				int normalizedOffset = NormalizeOffsetWithCap(paginationKey, PodcastCategoryPageSize, offset, out offsetClamped);
				if (offsetClamped)
				{
					int page = (normalizedOffset / PodcastCategoryPageSize) + 1;
					UpdateStatusBar($"页码过大，已跳到第 {page} 页");
				}
				offset = normalizedOffset;
				if (offset == 0 && !skipSave)
				{
					ClearPodcastCategoryFetchOffsets(categoryId);
				}
				string categoryName = ResolvePodcastCategoryName(categoryId);
				viewSource = $"podcast_cat_{categoryId}:offset{offset}";
				accessibleName = (string.IsNullOrWhiteSpace(categoryName) ? "播客列表" : (categoryName + " 播客"));
				int logicalOffset = offset;
				int fetchOffsetSeed = ResolvePodcastCategoryFetchOffset(categoryId, logicalOffset);
				ViewLoadRequest request = new ViewLoadRequest(viewSource, accessibleName, "正在加载播客...");
				ViewLoadResult<(List<PodcastRadioInfo> Items, bool HasMore, int TotalCount, int FetchOffsetStart, int FetchOffsetNext)> loadResult = await RunViewLoadAsync(request, async delegate(CancellationToken token)
				{
					const int pageSize = 50;
					int fetchOffset = Math.Max(0, fetchOffsetSeed);
					int fetchOffsetStart = fetchOffset;
					int totalCount = 0;
					bool hasMore = true;
					int safety = 0;
					List<PodcastRadioInfo> podcasts = new List<PodcastRadioInfo>();
					while (podcasts.Count < pageSize && hasMore && !token.IsCancellationRequested && safety < pageSize * 4)
					{
						int need = pageSize - podcasts.Count;
						(List<PodcastRadioInfo> Podcasts, bool HasMore, int TotalCount) batch = await _apiClient.GetPodcastsByCategoryAsync(categoryId, need, fetchOffset, token).ConfigureAwait(continueOnCapturedContext: false);
						token.ThrowIfCancellationRequested();
						List<PodcastRadioInfo> batchItems = batch.Podcasts ?? new List<PodcastRadioInfo>();
						totalCount = Math.Max(totalCount, batch.TotalCount);
						if (batchItems.Count == 0)
						{
							hasMore = batch.HasMore;
							if (hasMore)
							{
								fetchOffset = checked(fetchOffset + Math.Max(1, need));
								safety++;
								continue;
							}
							break;
						}
						podcasts.AddRange(batchItems);
						fetchOffset = checked(fetchOffset + batchItems.Count);
						hasMore = batch.HasMore;
						safety++;
					}
					return (Items: podcasts, HasMore: hasMore, TotalCount: totalCount, FetchOffsetStart: fetchOffsetStart, FetchOffsetNext: fetchOffset);
				}, "加载播客已取消").ConfigureAwait(continueOnCapturedContext: true);
				if (loadResult.IsCanceled)
				{
					return;
				}
				(List<PodcastRadioInfo> Items, bool HasMore, int TotalCount, int FetchOffsetStart, int FetchOffsetNext) data = loadResult.Value;
				List<PodcastRadioInfo> podcasts = data.Items ?? new List<PodcastRadioInfo>();
				int totalCount = Math.Max(data.TotalCount, logicalOffset + podcasts.Count);
				bool hasMore = data.HasMore;
				if (hasMore && totalCount <= logicalOffset + podcasts.Count)
				{
					totalCount = logicalOffset + podcasts.Count + 1;
				}
				bool hasMoreFinal = hasMore || logicalOffset + podcasts.Count < totalCount;
				SetPodcastCategoryFetchOffset(categoryId, logicalOffset, Math.Max(0, data.FetchOffsetStart));
				if (podcasts.Count > 0)
				{
					SetPodcastCategoryFetchOffset(categoryId, checked(logicalOffset + 50), Math.Max(0, data.FetchOffsetNext));
				}
				if (TryHandlePaginationEmptyResult(paginationKey, logicalOffset, PodcastCategoryPageSize, totalCount, podcasts.Count, hasMoreFinal, viewSource, accessibleName))
				{
					return;
				}
				_currentPodcastCategoryId = categoryId;
				_currentPodcastCategoryName = categoryName;
				_currentPodcastCategoryOffset = logicalOffset;
				_currentPodcastCategoryHasMore = hasMoreFinal;
				_currentPodcastCategoryTotalCount = Math.Max(totalCount, logicalOffset + podcasts.Count);
				int currentPage = unchecked(logicalOffset / 50) + 1;
				int totalPages = Math.Max(1, (int)Math.Ceiling((double)Math.Max(totalCount, logicalOffset + podcasts.Count + (hasMoreFinal ? 1 : 0)) / 50.0));
				await ExecuteOnUiThreadAsync(delegate
				{
					DisplayPodcasts(podcasts, showPagination: true, hasMoreFinal, logicalOffset + 1, preserveSelection: false, viewSource, accessibleName);
					if (podcasts.Count == 0)
					{
						MessageBox.Show("该分类暂时没有播客。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
						UpdateStatusBar("暂无播客");
						return;
					}
					UpdateStatusBar($"{accessibleName}：第 {currentPage}/{totalPages} 页，本页 {podcasts.Count} 个 / 总 {totalCount} 个（PageDown/Up 翻页）");
				});
			}
			catch (Exception ex)
			{
				string paginationKey = BuildPodcastCategoryPaginationKey(categoryId);
				if (TryHandlePaginationOffsetError(ex, paginationKey, offset, PodcastCategoryPageSize, viewSource, accessibleName))
				{
					return;
				}
				if (!TryHandleOperationCancelled(ex, "加载播客已取消"))
				{
					Debug.WriteLine($"[LoadPodcastsByCategory] 异常: {ex}");
					MessageBox.Show("加载播客失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
					UpdateStatusBar("加载失败");
				}
			}
		}
	}

	private async Task LoadNewAlbums()
	{
		try
		{
			ViewLoadRequest request = new ViewLoadRequest("new_albums", "新碟上架", "正在加载新碟上架...");
			ViewLoadResult<List<AlbumInfo>?> loadResult = await RunViewLoadAsync(request, async delegate(CancellationToken token)
			{
				List<AlbumInfo> albums = await _apiClient.GetNewAlbumsAsync().ConfigureAwait(continueOnCapturedContext: false);
				token.ThrowIfCancellationRequested();
				return albums ?? new List<AlbumInfo>();
			}, "加载新碟上架已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				List<AlbumInfo> albumsResult = loadResult.Value ?? new List<AlbumInfo>();
				DisplayAlbums(albumsResult, preserveSelection: false, request.ViewSource, request.AccessibleName);
				UpdateStatusBar((albumsResult.Count == 0) ? "新碟上架" : $"加载完成，共 {albumsResult.Count} 个新专辑");
				Debug.WriteLine($"[LoadNewAlbums] 成功加载 {albumsResult.Count} 个新专辑");
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Exception ex3 = ex2;
			if (!TryHandleOperationCancelled(ex3, "加载新碟上架已取消"))
			{
				Debug.WriteLine($"[LoadNewAlbums] 异常: {ex3}");
				MessageBox.Show("加载新碟上架失败: " + ex3.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("加载失败");
			}
		}
	}


}
}
