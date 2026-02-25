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
	private static readonly Dictionary<string, string> HomeCategoryUnits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
	{
		{ "user_liked_songs", "首" },
		{ "user_playlists", "个" },
		{ "user_albums", "张" },
		{ "artist_favorites", "位" },
		{ "user_podcasts", "个" },
		{ "user_cloud", "首" },
		{ "highquality_playlists", "个" },
		{ "new_songs", "类" },
		{ "playlist_category", "类" },
		{ "podcast_categories", "类" },
		{ "artist_categories", "类" },
		{ "new_album_categories", "类" },
		{ "new_albums", "张" },
		{ "toplist", "个" },
		{ PersonalFmCategoryId, "首" }
	};

	private static readonly Dictionary<string, (int AreaType, string AreaName)> NewSongsAreaMap = new Dictionary<string, (int, string)>(StringComparer.OrdinalIgnoreCase)
	{
		{ "new_songs_all", (0, "全部") },
		{ "new_songs_chinese", (7, "华语") },
		{ "new_songs_western", (96, "欧美") },
		{ "new_songs_japan", (8, "日本") },
		{ "new_songs_korea", (16, "韩国") }
	};

	private static string? ResolveHomeCategoryUnit(string? categoryId)
	{
		if (string.IsNullOrWhiteSpace(categoryId))
		{
			return null;
		}
		return HomeCategoryUnits.TryGetValue(categoryId, out string unit) ? unit : null;
	}

	private void ApplyHomeCategoryCount(ListItemInfo info, int? count)
	{
		if (count.HasValue && count.Value > 0)
		{
			info.ItemCount = count.Value;
			info.ItemUnit = ResolveHomeCategoryUnit(info.CategoryId);
		}
		else
		{
			info.ItemCount = null;
			info.ItemUnit = null;
		}
	}

	private ListItemInfo BuildHomeCategoryItem(string categoryId, string name, int? count = null, string? description = null)
	{
		ListItemInfo info = new ListItemInfo
		{
			Type = ListItemType.Category,
			CategoryId = categoryId,
			CategoryName = name,
			CategoryDescription = description
		};
		ApplyHomeCategoryCount(info, count);
		return info;
	}

	private HomePageViewData BuildHomePageSkeletonViewData()
	{
		bool flag = _accountState?.IsLoggedIn ?? false;
		List<ListItemInfo> list = new List<ListItemInfo>();
		int value = _homePlaylistCategoryPresets.Length;
		int count = ArtistMetadataHelper.GetTypeOptions().Count;
		int albumCategoryTypeCount = ArtistMetadataHelper.GetNewAlbumPeriodOptions().Count;
		if (flag)
		{
			int? likedCount = _userLikedPlaylist?.TrackCount;
			string recentDescription = BuildRecentListenedDescription();
			string dailyDescription = (_homeCachedDailyRecommendSongCount.GetValueOrDefault() > 0 || _homeCachedDailyRecommendPlaylistCount.GetValueOrDefault() > 0)
				? $"歌曲 {_homeCachedDailyRecommendSongCount.GetValueOrDefault()} 首 / 歌单 {_homeCachedDailyRecommendPlaylistCount.GetValueOrDefault()} 个"
				: null;
			string personalizedDescription = (_homeCachedPersonalizedPlaylistCount.GetValueOrDefault() > 0 || _homeCachedPersonalizedSongCount.GetValueOrDefault() > 0)
				? $"歌单 {_homeCachedPersonalizedPlaylistCount.GetValueOrDefault()} 个 / 歌曲 {_homeCachedPersonalizedSongCount.GetValueOrDefault()} 首"
				: null;
			string cloudDescription = ((_cloudMaxSize > 0 || _cloudTotalCount > 0) ? ("已用 " + FormatSize(_cloudUsedSize) + " / " + FormatSize(_cloudMaxSize)) : null);
			list.Add(BuildHomeCategoryItem("user_liked_songs", "喜欢的音乐", likedCount));
			list.Add(BuildHomeCategoryItem("recent_listened", "最近听过", null, string.IsNullOrWhiteSpace(recentDescription) ? null : recentDescription));
			list.Add(BuildHomeCategoryItem("user_playlists", "创建和收藏的歌单", _homeCachedUserPlaylistCount));
			list.Add(BuildHomeCategoryItem("user_albums", "收藏的专辑", _homeCachedUserAlbumCount));
			list.Add(BuildHomeCategoryItem("artist_favorites", "收藏的歌手", _homeCachedArtistFavoritesCount));
			list.Add(BuildHomeCategoryItem("user_podcasts", "收藏的播客", _homeCachedPodcastFavoritesCount));
			list.Add(BuildHomeCategoryItem("user_cloud", "云盘", _cloudTotalCount > 0 ? _cloudTotalCount : (int?)null, cloudDescription));
			list.Add(BuildHomeCategoryItem("daily_recommend", "每日推荐", null, dailyDescription));
			list.Add(BuildHomeCategoryItem("personalized", "为您推荐", null, personalizedDescription));
			list.Add(BuildHomeCategoryItem(PersonalFmCategoryId, PersonalFmAccessibleName, null, "专属于你的连续推荐电台"));
		}
		list.Add(BuildHomeCategoryItem("highquality_playlists", "精品歌单", _homeCachedHighQualityCount));
		list.Add(BuildHomeCategoryItem("new_songs", "新歌速递", 5));
		list.Add(BuildHomeCategoryItem("new_albums", "新碟上架", _homeCachedNewAlbumCount));
		list.Add(BuildHomeCategoryItem("new_album_categories", "新碟分类", albumCategoryTypeCount));
		list.Add(BuildHomeCategoryItem("playlist_category", "歌单分类", value));
		list.Add(BuildHomeCategoryItem("podcast_categories", "播客分类", _homeCachedPodcastCategoryCount));
		list.Add(BuildHomeCategoryItem("artist_categories", "歌手分类", count));
		list.Add(BuildHomeCategoryItem("toplist", "官方排行榜", _homeCachedToplistCount));
		string statusText = (flag ? "主页骨架已加载，正在同步数据..." : "欢迎使用，登录后解锁更多入口");
		return new HomePageViewData(list, statusText);
	}

	private async Task EnrichHomePageAsync(bool isInitialLoad, CancellationToken externalToken)
	{
		CancellationToken viewToken = GetCurrentViewContentToken();
		CancellationTokenSource linkedCts = null;
		CancellationToken effectiveToken = LinkCancellationTokens(viewToken, externalToken, out linkedCts);
		try
		{
			await RunHomeProvidersAsync(isInitialLoad, effectiveToken).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Exception ex3 = ex2;
			if (!TryHandleOperationCancelled(ex3, "主页丰富已取消"))
			{
				Debug.WriteLine($"[HomePage] 丰富失败: {ex3}");
			}
		}
		finally
		{
			linkedCts?.Dispose();
		}
	}

	private static bool IsRecommendationCacheFresh(DateTime fetchedUtc)
	{
		return fetchedUtc != DateTime.MinValue && DateTime.UtcNow - fetchedUtc < RecommendationCacheTtl;
	}

	private bool IsHighQualityCountFresh()
	{
		return _highQualityCountFetchedUtc != DateTime.MinValue && DateTime.UtcNow - _highQualityCountFetchedUtc < HighQualityCountCacheTtl;
	}

	private static bool IsListItemDifferent(ListItemInfo? a, ListItemInfo? b)
	{
		if (a == null || b == null)
		{
			return true;
		}
		if (diff(a.Id, b.Id))
		{
			return true;
		}
		if (diff(a.Name, b.Name))
		{
			return true;
		}
		if (diff(a.Creator, b.Creator))
		{
			return true;
		}
		if (diff(a.ExtraInfo, b.ExtraInfo))
		{
			return true;
		}
		if (diff(a.Description, b.Description))
		{
			return true;
		}
		return false;
		static bool diff(string x, string y)
		{
			return !string.Equals(x ?? string.Empty, y ?? string.Empty, StringComparison.Ordinal);
		}
	}

	private string BuildCloudSummaryDescription(long used, long max)
	{
		if (used <= 0 || max <= 0)
		{
			return "上传和管理您的私人音乐";
		}
		return "已用 " + FormatSize(used) + " / " + FormatSize(max);
	}

	private async Task RunHomeProvidersAsync(bool isInitialLoad, CancellationToken token)
	{
		if (!string.Equals(_currentViewSource, "homepage", StringComparison.OrdinalIgnoreCase))
		{
			Debug.WriteLine("[HomeProviders] 当前视图=" + _currentViewSource + "，跳过丰富");
			return;
		}
		List<Task> tasks = new List<Task>();
		SemaphoreSlim semaphore = new SemaphoreSlim(6);
		bool isLoggedIn = _accountState?.IsLoggedIn ?? false;
		tasks.Add(Wrap("user_playlists", async delegate
		{
			if (!isLoggedIn)
			{
				return true;
			}
			Debug.WriteLine("[HomeProviders] fetch user playlists");
			UserAccountInfo userInfo = await _apiClient.GetUserAccountAsync();
			token.ThrowIfCancellationRequested();
			if (userInfo == null || userInfo.UserId <= 0)
			{
				return false;
			}
			_loggedInUserId = userInfo.UserId;
			var (playlists, totalCount) = await _apiClient.GetUserPlaylistsAsync(userInfo.UserId);
			token.ThrowIfCancellationRequested();
			PlaylistInfo liked = playlists?.FirstOrDefault((PlaylistInfo p) => !string.IsNullOrEmpty(p.Name) && p.Name.IndexOf("喜欢的音乐", StringComparison.OrdinalIgnoreCase) >= 0);
			int playlistCount = totalCount;
			if (liked != null && playlistCount > 0)
			{
				playlistCount = Math.Max(0, checked(playlistCount - 1));
			}
			ApplyHomeItemUpdate("user_playlists", delegate(ListItemInfo info)
			{
				ApplyHomeCategoryCount(info, playlistCount);
				info.CategoryDescription = null;
			});
			if (liked != null)
			{
				ApplyHomeItemUpdate("user_liked_songs", delegate(ListItemInfo info)
				{
					ApplyHomeCategoryCount(info, liked.TrackCount);
					info.CategoryDescription = null;
				});
			}
			return true;
		}));
		tasks.Add(Wrap("user_albums", async delegate
		{
			if (!isLoggedIn)
			{
				return true;
			}
			Debug.WriteLine("[HomeProviders] fetch user albums");
			int albumCount = (await _apiClient.GetUserAlbumsAsync(1)).Item2;
			token.ThrowIfCancellationRequested();
			ApplyHomeItemUpdate("user_albums", delegate(ListItemInfo info)
			{
				ApplyHomeCategoryCount(info, albumCount);
				info.CategoryDescription = null;
			});
			return true;
		}));
		tasks.Add(Wrap("artist_favorites", async delegate
		{
			if (!isLoggedIn)
			{
				return true;
			}
			Debug.WriteLine("[HomeProviders] fetch artist subs");
			SearchResult<ArtistInfo> artists = await _apiClient.GetArtistSubscriptionsAsync(1);
			token.ThrowIfCancellationRequested();
			int count = artists?.TotalCount ?? artists?.Items.Count ?? 0;
			ApplyHomeItemUpdate("artist_favorites", delegate(ListItemInfo info)
			{
				ApplyHomeCategoryCount(info, count);
				info.CategoryDescription = null;
			});
			return true;
		}));
		tasks.Add(Wrap("user_podcasts", async delegate
		{
			if (!isLoggedIn)
			{
				return true;
			}
			Debug.WriteLine("[HomeProviders] fetch podcast subs");
			int podcastCount = (await _apiClient.GetSubscribedPodcastsAsync(1)).Item2;
			token.ThrowIfCancellationRequested();
			ApplyHomeItemUpdate("user_podcasts", delegate(ListItemInfo info)
			{
				ApplyHomeCategoryCount(info, podcastCount);
				info.CategoryDescription = null;
			});
			return true;
		}));
		tasks.Add(Wrap("user_cloud", async delegate
		{
			if (!isLoggedIn)
			{
				return true;
			}
			Debug.WriteLine("[HomeProviders] fetch cloud summary");
			try
			{
				CloudSongPageResult cloudSummary = await _apiClient.GetCloudSongsAsync(1, 0, token);
				token.ThrowIfCancellationRequested();
				_cloudTotalCount = cloudSummary?.TotalCount ?? 0;
				_cloudUsedSize = cloudSummary?.UsedSize ?? 0;
				_cloudMaxSize = cloudSummary?.MaxSize ?? 0;
			}
			catch (Exception ex)
			{
				Debug.WriteLine("[HomeProvider] cloud query failed: " + ex.Message);
			}
			ApplyHomeItemUpdate("user_cloud", delegate(ListItemInfo info)
			{
				ApplyHomeCategoryCount(info, _cloudTotalCount);
				info.CategoryDescription = ((_cloudMaxSize > 0 || _cloudTotalCount > 0) ? BuildCloudSummaryDescription(_cloudUsedSize, _cloudMaxSize) : null);
			});
			return true;
		}));
		tasks.Add(Wrap("recent_listened", async delegate
		{
			Debug.WriteLine("[HomeProviders] fetch recent summary");
			await RefreshRecentSummariesAsync(isLoggedIn, token).ConfigureAwait(continueOnCapturedContext: false);
			token.ThrowIfCancellationRequested();
			ApplyHomeItemUpdate("recent_listened", delegate(ListItemInfo info)
			{
				info.CategoryDescription = BuildRecentListenedDescription();
			});
			return true;
		}));
		tasks.Add(Wrap("highquality_playlists", async delegate
		{
			Debug.WriteLine("[HomeProviders] fetch highquality playlists count");
			int count = 0;
			if (IsHighQualityCountFresh() && _homeCachedHighQualityCount.HasValue)
			{
				count = _homeCachedHighQualityCount.Value;
			}
			else if (_currentHighQualityLoadedAll && string.Equals(_currentViewSource, "highquality_playlists", StringComparison.OrdinalIgnoreCase))
			{
				count = Math.Max(0, _currentPlaylists?.Count ?? 0);
				_homeCachedHighQualityCount = (count > 0) ? count : (int?)null;
				_highQualityCountFetchedUtc = DateTime.UtcNow;
			}
			else
			{
				count = await FetchHighQualityPlaylistsCountAsync(token).ConfigureAwait(continueOnCapturedContext: false);
				_homeCachedHighQualityCount = (count > 0) ? count : (int?)null;
				_highQualityCountFetchedUtc = DateTime.UtcNow;
			}
			ApplyHomeItemUpdate("highquality_playlists", delegate(ListItemInfo info)
			{
				ApplyHomeCategoryCount(info, (count > 0) ? count : (int?)null);
				info.CategoryDescription = null;
			});
			return true;
		}));
		tasks.Add(Wrap("toplist", async delegate
		{
			Debug.WriteLine("[HomeProviders] fetch toplist");
			List<PlaylistInfo> toplist = await _apiClient.GetToplistAsync();
			token.ThrowIfCancellationRequested();
			int count = toplist?.Count ?? 0;
			ApplyHomeItemUpdate("toplist", delegate(ListItemInfo info)
			{
				ApplyHomeCategoryCount(info, count);
				info.CategoryDescription = null;
			});
			return true;
		}));
		tasks.Add(Wrap("new_albums", async delegate
		{
			Debug.WriteLine("[HomeProviders] fetch new albums");
			List<AlbumInfo> newAlbums = await _apiClient.GetNewAlbumsAsync();
			token.ThrowIfCancellationRequested();
			int count = newAlbums?.Count ?? 0;
			ApplyHomeItemUpdate("new_albums", delegate(ListItemInfo info)
			{
				ApplyHomeCategoryCount(info, count);
				info.CategoryDescription = null;
			});
			return true;
		}));
		tasks.Add(Wrap("podcast_categories", async delegate
		{
			Debug.WriteLine("[HomeProviders] fetch podcast categories");
			List<PodcastCategoryInfo> categories = await _apiClient.GetPodcastCategoriesAsync(token);
			token.ThrowIfCancellationRequested();
			int count = categories?.Count ?? 0;
			lock (_podcastCategoryLock)
			{
				_podcastCategories.Clear();
				if (categories != null)
				{
					foreach (PodcastCategoryInfo cat in categories)
					{
						if (cat != null && cat.Id > 0 && !string.IsNullOrWhiteSpace(cat.Name))
						{
							_podcastCategories[cat.Id] = cat;
						}
					}
				}
			}
			ApplyHomeItemUpdate("podcast_categories", delegate(ListItemInfo info)
			{
				ApplyHomeCategoryCount(info, count);
				info.CategoryDescription = null;
			});
			return true;
		}));
		tasks.Add(Wrap("daily_recommend", async delegate
		{
			if (!isLoggedIn)
			{
				return true;
			}
			Debug.WriteLine("[HomeProviders] fetch daily recommend");
			(List<SongInfo> Songs, List<PlaylistInfo> Playlists) daily = await FetchDailyRecommendBundleAsync(token);
			token.ThrowIfCancellationRequested();
			int songCount = daily.Songs?.Count ?? 0;
			int playlistCount = daily.Playlists?.Count ?? 0;
			ApplyHomeItemUpdate("daily_recommend", delegate(ListItemInfo info)
			{
				if (songCount > 0 || playlistCount > 0)
				{
					info.CategoryDescription = $"歌曲 {songCount} 首 / 歌单 {playlistCount} 个";
				}
				else
				{
					info.CategoryDescription = null;
				}
				ApplyHomeCategoryCount(info, null);
			});
			return true;
		}));
		tasks.Add(Wrap("personalized", async delegate
		{
			if (!isLoggedIn)
			{
				return true;
			}
			Debug.WriteLine("[HomeProviders] fetch personalized");
			(List<PlaylistInfo> Playlists, List<SongInfo> Songs) personalized = await FetchPersonalizedBundleAsync(token);
			token.ThrowIfCancellationRequested();
			int playlistCount = personalized.Playlists?.Count ?? 0;
			int songCount = personalized.Songs?.Count ?? 0;
			ApplyHomeItemUpdate("personalized", delegate(ListItemInfo info)
			{
				if (songCount > 0 || playlistCount > 0)
				{
					info.CategoryDescription = $"歌曲 {songCount} 首 / 歌单 {playlistCount} 个";
				}
				else
				{
					info.CategoryDescription = null;
				}
				ApplyHomeCategoryCount(info, null);
			});
			return true;
		}));
		await Task.WhenAll(tasks).ConfigureAwait(continueOnCapturedContext: false);
		if (token.IsCancellationRequested)
		{
			return;
		}
		await ExecuteOnUiThreadAsync(delegate
		{
			if (string.Equals(_currentViewSource, "homepage", StringComparison.OrdinalIgnoreCase))
			{
				_isHomePage = true;
				UpdateStatusBar(isInitialLoad ? "主页加载完成，数据持续同步中" : "主页已更新");
				if (isInitialLoad && !_initialHomeLoadCompleted)
				{
					_initialHomeLoadCompleted = true;
				}
			}
		}).ConfigureAwait(continueOnCapturedContext: false);
		async Task ExecuteWithRetryAsync(string name, Func<Task<bool>> work, int maxRetry = 10)
		{
			checked
			{
				for (int attempt = 1; attempt <= maxRetry; attempt++)
				{
					token.ThrowIfCancellationRequested();
					try
					{
						if (await work().ConfigureAwait(continueOnCapturedContext: false))
						{
							return;
						}
						Debug.WriteLine($"[HomeRetry] {name} attempt {attempt} 未返回有效数据");
					}
					catch (Exception ex)
					{
						Debug.WriteLine($"[HomeRetry] {name} attempt {attempt} 异常: {ex.Message}");
					}
					if (attempt < maxRetry)
					{
						await Task.Delay(TimeSpan.FromMilliseconds(200 * Math.Min(attempt, 10)), token).ConfigureAwait(continueOnCapturedContext: false);
					}
				}
				Debug.WriteLine("[HomeRetry] " + name + " 达到最大重试次数，放弃更新");
			}
		}
		async Task Wrap(string name, Func<Task<bool>> work)
		{
			await semaphore.WaitAsync(token).ConfigureAwait(continueOnCapturedContext: false);
			try
			{
				await ExecuteWithRetryAsync(name, work).ConfigureAwait(continueOnCapturedContext: false);
			}
			finally
			{
				semaphore.Release();
			}
		}
	}

	private void ApplyHomeItemUpdate(string categoryId, Action<ListItemInfo> updater)
	{
		int index;
		if (string.IsNullOrWhiteSpace(categoryId))
		{
			Debug.WriteLine("[HomeUpdate] categoryId为空，跳过");
		}
		else if (!string.Equals(_currentViewSource, "homepage", StringComparison.OrdinalIgnoreCase))
		{
			Debug.WriteLine("[HomeUpdate] 当前视图非主页(" + _currentViewSource + "), 跳过 " + categoryId);
		}
		else if (!_homeItemIndexMap.TryGetValue(categoryId, out index))
		{
			Debug.WriteLine($"[HomeUpdate] 找不到索引: {categoryId}（map条目={_homeItemIndexMap.Count}）");
		}
		else
		{
			if (index < 0 || index >= _currentListItems.Count || index >= resultListView.Items.Count)
			{
				return;
			}
			ListItemInfo listItemInfo = _currentListItems[index];
			ListItemInfo clone = new ListItemInfo
			{
				Type = listItemInfo.Type,
				Song = listItemInfo.Song,
				Playlist = listItemInfo.Playlist,
				Album = listItemInfo.Album,
				Artist = listItemInfo.Artist,
				Podcast = listItemInfo.Podcast,
				PodcastEpisode = listItemInfo.PodcastEpisode,
				CategoryId = listItemInfo.CategoryId,
				CategoryName = listItemInfo.CategoryName,
				CategoryDescription = listItemInfo.CategoryDescription,
				ItemCount = listItemInfo.ItemCount,
				ItemUnit = listItemInfo.ItemUnit
			};
			updater(clone);
			if (!IsListItemDifferent(listItemInfo, clone))
			{
				return;
			}
			_currentListItems[index] = clone;
			SafeInvoke(delegate
			{
				Debug.WriteLine($"[HomeUpdate] {categoryId} -> row {index}: count={clone.ItemCount}, desc={clone.CategoryDescription}");
				if (index >= 0 && index < resultListView.Items.Count)
				{
					ListViewItem item = resultListView.Items[index];
					bool isFocusedRow = GetFocusedListViewIndex() == index;
					string beforeSpeech = null;
					if (isFocusedRow)
					{
						EnsureSubItemCount(item, 6);
						beforeSpeech = BuildListViewItemSpeech(item);
					}
					FillListViewItemFromListItemInfo(item, clone, checked(index + 1), preserveDisplayIndex: true);
					ScheduleResultListViewLayoutUpdate();
					if (isFocusedRow)
					{
						EnsureSubItemCount(item, 6);
						string afterSpeech = BuildListViewItemSpeech(item);
						if (!string.Equals(beforeSpeech, afterSpeech, StringComparison.Ordinal))
						{
							QueueFocusedListViewItemRefreshAnnouncement(index);
						}
					}
				}
			});
		}
	}

	private List<ListItemInfo> BuildRecentListenedEntries()
	{
		return new List<ListItemInfo>
		{
			new ListItemInfo
			{
				Type = ListItemType.Category,
				CategoryId = "recent_play",
				CategoryName = "最近歌曲",
				CategoryDescription = (_recentSummaryReady ? $"{_recentPlayCount} 首" : null)
			},
			new ListItemInfo
			{
				Type = ListItemType.Category,
				CategoryId = "recent_playlists",
				CategoryName = "最近歌单",
				CategoryDescription = (_recentSummaryReady ? $"{_recentPlaylistCount} 个" : null)
			},
			new ListItemInfo
			{
				Type = ListItemType.Category,
				CategoryId = "recent_albums",
				CategoryName = "最近专辑",
				CategoryDescription = (_recentSummaryReady ? $"{_recentAlbumCount} 张" : null)
			},
			new ListItemInfo
			{
				Type = ListItemType.Category,
				CategoryId = "recent_podcasts",
				CategoryName = "最近播客",
				CategoryDescription = (_recentSummaryReady ? $"{_recentPodcastCount} 个" : null)
			}
		};
	}

	private string BuildRecentListenedDescription()
	{
		if (!_recentSummaryReady)
		{
			return string.Empty;
		}
		return $"歌曲 {_recentPlayCount} 首 | 歌单 {_recentPlaylistCount} 个 | 专辑 {_recentAlbumCount} 张 | 播客 {_recentPodcastCount} 个";
	}

	private string BuildRecentListenedStatus()
	{
		if (!_recentSummaryReady)
		{
			return "最近听过";
		}
		return $"最近听过：歌曲 {_recentPlayCount} 首 / 歌单 {_recentPlaylistCount} 个 / 专辑 {_recentAlbumCount} 张 / 播客 {_recentPodcastCount} 个";
	}


}
}
