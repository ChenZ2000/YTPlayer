#nullable disable
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace YTPlayer
{
public partial class MainForm
{
	private async Task LoadCategoryContent(string categoryId, bool skipSave = false, int pendingFocusIndex = -1)
	{
		try
		{
			double timeSinceStartup = (DateTime.Now - _appStartTime).TotalSeconds;
			if (timeSinceStartup < 3.0)
			{
				int delayMs = checked((int)((3.0 - timeSinceStartup) * 1000.0));
				await Task.Delay(Math.Min(delayMs, 2000));
			}
			if (!skipSave)
			{
				SaveNavigationState();
			}
			UpdateStatusBar("正在加载 " + categoryId + "...");
			if (!await TryLoadCategoryContentAsync(categoryId, pendingFocusIndex))
			{
				MessageBox.Show("未知的分类: " + categoryId, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			}
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"[LoadCategoryContent] 异常: {ex}");
			MessageBox.Show("加载分类内容失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("加载失败");
		}
	}

	// Category entry, Backspace/F5 restoration and playback-source navigation share
	// this dispatch. History belongs to the caller; nested loaders must not save it.
	private async Task<bool> TryLoadCategoryContentAsync(string categoryId, int pendingFocusIndex = -1)
	{
		if (string.IsNullOrWhiteSpace(categoryId))
		{
			return false;
		}
		if (IsListenMatchViewSource(categoryId))
		{
			await LoadListenMatchAsync(categoryId, skipSave: true, pendingFocusIndex);
			return true;
		}
		string normalizedCategoryId = StripOffsetSuffix(categoryId, out int categoryOffset);
		switch (normalizedCategoryId.ToLowerInvariant())
		{
		case "user_liked_songs":
			await LoadUserLikedSongs(skipSaveNavigation: true);
			return true;
		case "user_playlists":
			await LoadUserPlaylists();
			return true;
		case "user_albums":
			await LoadUserAlbums();
			return true;
		case "user_podcasts":
			await LoadUserPodcasts();
			return true;
		case "user_cloud":
			await LoadCloudSongsAsync(skipSave: true);
			return true;
		case "recent_play":
			await LoadRecentPlayedSongsAsync(pendingFocusIndex);
			return true;
		case "recent_listened":
			await LoadRecentListenedCategoryAsync(skipSave: true, pendingFocusIndex: pendingFocusIndex);
			return true;
		case "recent_playlists":
			await LoadRecentPlaylistsAsync(pendingFocusIndex);
			return true;
		case "recent_albums":
			await LoadRecentAlbumsAsync(pendingFocusIndex);
			return true;
		case "recent_podcasts":
			await LoadRecentPodcastsAsync(pendingFocusIndex);
			return true;
		case "daily_recommend":
			await LoadDailyRecommend(pendingFocusIndex);
			return true;
		case "personalized":
			await LoadPersonalized(pendingFocusIndex);
			return true;
		case PersonalFmCategoryId:
			await LoadPersonalFm(pendingFocusIndex);
			return true;
		case "toplist":
			await LoadToplist(pendingFocusIndex, categoryOffset, skipSave: true);
			return true;
		case "daily_recommend_songs":
			await LoadDailyRecommendSongs(pendingFocusIndex);
			return true;
		case "daily_recommend_playlists":
			await LoadDailyRecommendPlaylists(pendingFocusIndex);
			return true;
		case "personalized_playlists":
			await LoadPersonalizedPlaylists(pendingFocusIndex);
			return true;
		case "personalized_newsongs":
			await LoadPersonalizedNewSongs(pendingFocusIndex);
			return true;
		case "personalized_newsong":
			await LoadPersonalizedNewSong(pendingFocusIndex);
			return true;
		case "highquality_playlists":
			await LoadHighQualityPlaylists(categoryOffset, skipSave: true, pendingFocusIndex: pendingFocusIndex);
			return true;
		case "new_songs":
			await LoadNewSongs();
			return true;
		case "new_songs_all":
			await LoadNewSongsByArea(0, "全部", "new_songs_all", categoryOffset, skipSave: true);
			return true;
		case "new_songs_chinese":
			await LoadNewSongsByArea(7, "华语", "new_songs_chinese", categoryOffset, skipSave: true);
			return true;
		case "new_songs_western":
			await LoadNewSongsByArea(96, "欧美", "new_songs_western", categoryOffset, skipSave: true);
			return true;
		case "new_songs_japan":
			await LoadNewSongsByArea(8, "日本", "new_songs_japan", categoryOffset, skipSave: true);
			return true;
		case "new_songs_korea":
			await LoadNewSongsByArea(16, "韩国", "new_songs_korea", categoryOffset, skipSave: true);
			return true;
		case "playlist_category":
			await LoadPlaylistCategory();
			return true;
		case "podcast_categories":
			await LoadPodcastCategoriesAsync();
			return true;
		case "new_albums":
			await LoadNewAlbums();
			return true;
		case "artist_favorites":
			await LoadArtistFavoritesAsync(skipSave: true);
			return true;
		case "artist_categories":
			await LoadArtistCategoryTypesAsync(skipSave: true);
			return true;
		case "new_album_categories":
			await LoadAlbumCategoryTypesAsync(skipSave: true);
			return true;
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
				return false;
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
				return false;
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
				return false;
			}
		}
		else
		{
			return false;
		}
		return true;
	}
}
}
