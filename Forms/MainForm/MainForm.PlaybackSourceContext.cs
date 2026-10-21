#nullable disable
#pragma warning disable CS8632
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using YTPlayer.Core.Playback;
using YTPlayer.Models;

namespace YTPlayer
{
public partial class MainForm
{
	private sealed class PlaybackSourceQueueData
	{
		public string Source { get; set; } = string.Empty;
		public List<SongInfo> Songs { get; set; } = new List<SongInfo>();
		public int AnchorIndex { get; set; } = -1;
		public SongInfo TargetSong { get; set; }
	}

	private string NormalizePlaybackSourceForPersistence(string? source)
	{
		if (string.IsNullOrWhiteSpace(source))
		{
			return string.Empty;
		}

		string trimmed = source.Trim();
		if (trimmed.StartsWith("podcast:", StringComparison.OrdinalIgnoreCase))
		{
			ParsePodcastViewSource(trimmed, out long radioId, out _, out bool ascending);
			if (radioId > 0)
			{
				return BuildPodcastPlaybackSource(radioId, ascending);
			}
		}

		if (trimmed.StartsWith("artist_songs:", StringComparison.OrdinalIgnoreCase))
		{
			ParseArtistListViewSource(trimmed, out long artistId, out _, out string order);
			if (artistId > 0)
			{
				return $"artist_songs:{artistId}:order{order}:offset0";
			}
		}

		if (trimmed.StartsWith("search:", StringComparison.OrdinalIgnoreCase))
		{
			ParseSearchViewSource(trimmed, out string searchType, out string keyword, out _);
			if (!string.IsNullOrWhiteSpace(keyword))
			{
				if (string.Equals(searchType, "歌曲", StringComparison.OrdinalIgnoreCase))
				{
					return $"search:song:{keyword}:page1";
				}

				string typeToken = SearchTypeToViewSourceToken(searchType);
				return string.IsNullOrWhiteSpace(typeToken)
					? $"search:song:{keyword}:page1"
					: $"search:{typeToken}:{keyword}:page1";
			}
		}

		if (TryParseNewSongsViewSource(trimmed, out int areaType, out _, out _, out _))
		{
			return BuildNewSongsViewSource(areaType, 0);
		}

		string normalized = StripOffsetSuffix(trimmed, out _);
		return string.IsNullOrWhiteSpace(normalized) ? trimmed : normalized;
	}

	private static string SearchTypeToViewSourceToken(string searchType)
	{
		if (string.Equals(searchType, "歌单", StringComparison.OrdinalIgnoreCase))
		{
			return "playlist";
		}
		if (string.Equals(searchType, "专辑", StringComparison.OrdinalIgnoreCase))
		{
			return "album";
		}
		if (string.Equals(searchType, "歌手", StringComparison.OrdinalIgnoreCase))
		{
			return "artist";
		}
		if (string.Equals(searchType, "播客", StringComparison.OrdinalIgnoreCase))
		{
			return "podcast";
		}
		return string.Empty;
	}

	private static string BuildPodcastPlaybackSource(long radioId, bool ascending)
	{
		string source = $"podcast:{radioId}:offset0";
		if (ascending)
		{
			source += ":asc1";
		}
		return source;
	}

	private int ResolveCurrentPlaybackSourceIndex(string source, SongInfo currentSong, PlaybackSnapshot snapshot)
	{
		if (currentSong == null || string.IsNullOrWhiteSpace(source))
		{
			return -1;
		}

		int cachedIndex = ResolveSongIndexFromCache(source, currentSong);
		if (cachedIndex >= 0)
		{
			return cachedIndex;
		}

		if (snapshot != null &&
			snapshot.QueueIndex >= 0 &&
			string.Equals(NormalizePlaybackSourceForPersistence(snapshot.QueueSource), source, StringComparison.OrdinalIgnoreCase))
		{
			int queueIndex = snapshot.QueueIndex;
			int queueStartOffset = GetPlaybackSourceStartOffset(snapshot.QueueSource);
			if (snapshot.Queue != null && queueIndex >= 0 && queueIndex < snapshot.Queue.Count)
			{
				SongInfo queueSong = snapshot.Queue[queueIndex];
				if (IsSameSongForPlaybackContext(queueSong, currentSong) || IsPlaceholderSong(queueSong))
				{
					return queueStartOffset + queueIndex;
				}
			}
			else
			{
				return queueStartOffset + queueIndex;
			}
		}

		if (!string.IsNullOrWhiteSpace(_currentViewSource) &&
			string.Equals(NormalizePlaybackSourceForPersistence(_currentViewSource), source, StringComparison.OrdinalIgnoreCase) &&
			_currentSongs != null &&
			_currentSongs.Count > 0)
		{
			int viewIndex = FindSongIndexInList(_currentSongs, currentSong);
			if (viewIndex >= 0)
			{
				return GetPlaybackSourceStartOffset(_currentViewSource) + viewIndex;
			}
		}

		return -1;
	}

	private static bool IsSameSongForPlaybackContext(SongInfo first, SongInfo second)
	{
		if (first == null || second == null)
		{
			return false;
		}

		if (!string.IsNullOrWhiteSpace(first.Id) &&
			!string.IsNullOrWhiteSpace(second.Id) &&
			string.Equals(first.Id, second.Id, StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		return first.IsCloudSong &&
			second.IsCloudSong &&
			!string.IsNullOrWhiteSpace(first.CloudSongId) &&
			string.Equals(first.CloudSongId, second.CloudSongId, StringComparison.OrdinalIgnoreCase);
	}

	private int GetPlaybackSourceStartOffset(string source)
	{
		if (string.IsNullOrWhiteSpace(source))
		{
			return 0;
		}

		if (source.StartsWith("search:", StringComparison.OrdinalIgnoreCase))
		{
			ParseSearchViewSource(source, out _, out _, out int page);
			return Math.Max(0, page - 1) * Math.Max(1, _resultsPerPage);
		}

		if (source.StartsWith("podcast:", StringComparison.OrdinalIgnoreCase))
		{
			ParsePodcastViewSource(source, out _, out int offset, out _);
			return Math.Max(0, offset);
		}

		if (source.StartsWith("artist_songs:", StringComparison.OrdinalIgnoreCase))
		{
			ParseArtistListViewSource(source, out _, out int offset, out _);
			return Math.Max(0, offset);
		}

		if (TryParseNewSongsViewSource(source, out _, out _, out int newSongsOffset, out _))
		{
			return Math.Max(0, newSongsOffset);
		}

		StripOffsetSuffix(source, out int genericOffset);
		return Math.Max(0, genericOffset);
	}

	private async Task<PlaybackSourceQueueData> BuildPlaybackQueueFromSavedSourceAsync(ConfigModel configModel, CancellationToken cancellationToken)
	{
		if (configModel == null || string.IsNullOrWhiteSpace(configModel.LastPlayingSongId))
		{
			return null;
		}

		string source = NormalizePlaybackSourceForPersistence(configModel.LastPlayingSource);
		if (string.IsNullOrWhiteSpace(source))
		{
			return null;
		}

		int sourceIndex = Math.Max(-1, configModel.LastPlayingSourceIndex);
		string songId = configModel.LastPlayingSongId;

		try
		{
			return await BuildPlaybackQueueFromSourceAsync(source, songId, sourceIndex, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			Debug.WriteLine("[PlaybackSource] 来源上下文恢复失败: " + ex.Message);
			return null;
		}
	}

	private async Task<PlaybackSourceQueueData> BuildPlaybackQueueFromSourceAsync(string source, string songId, int sourceIndex, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(songId) || _apiClient == null)
		{
			return null;
		}

		if (source.StartsWith("playlist:", StringComparison.OrdinalIgnoreCase))
		{
			return await BuildPlaylistPlaybackQueueAsync(source, source.Substring("playlist:".Length), songId, sourceIndex, cancellationToken).ConfigureAwait(false);
		}

		if (string.Equals(source, "user_liked_songs", StringComparison.OrdinalIgnoreCase))
		{
			return await BuildLikedSongsPlaybackQueueAsync(source, songId, sourceIndex, cancellationToken).ConfigureAwait(false);
		}

		if (source.StartsWith("album:", StringComparison.OrdinalIgnoreCase))
		{
			return await BuildFullSongListPlaybackQueueAsync(
				source,
				songId,
				sourceIndex,
				ct => _apiClient.GetAlbumSongsAsync(source.Substring("album:".Length), ct),
				cancellationToken).ConfigureAwait(false);
		}

		if (source.StartsWith("podcast:", StringComparison.OrdinalIgnoreCase))
		{
			return await BuildPodcastPlaybackQueueAsync(source, songId, sourceIndex, cancellationToken).ConfigureAwait(false);
		}

		if (source.StartsWith("artist_songs:", StringComparison.OrdinalIgnoreCase))
		{
			return await BuildArtistSongsPlaybackQueueAsync(source, songId, sourceIndex, cancellationToken).ConfigureAwait(false);
		}

		if (source.StartsWith("search:", StringComparison.OrdinalIgnoreCase))
		{
			ParseSearchViewSource(source, out string searchType, out _, out _);
			if (string.Equals(searchType, "歌曲", StringComparison.OrdinalIgnoreCase))
			{
				return await BuildSearchSongsPlaybackQueueAsync(source, songId, sourceIndex, cancellationToken).ConfigureAwait(false);
			}
		}

		if (string.Equals(source, "user_cloud", StringComparison.OrdinalIgnoreCase))
		{
			return await BuildCloudPlaybackQueueAsync(source, songId, sourceIndex, cancellationToken).ConfigureAwait(false);
		}

		if (TryParseNewSongsViewSource(source, out int areaType, out _, out _, out _))
		{
			return await BuildFullSongListPlaybackQueueAsync(
				BuildNewSongsViewSource(areaType, 0),
				songId,
				sourceIndex,
				ct => _apiClient.GetNewSongsAsync(areaType),
				cancellationToken).ConfigureAwait(false);
		}

		if (string.Equals(source, "daily_recommend_songs", StringComparison.OrdinalIgnoreCase) ||
			string.Equals(source, "daily_recommend", StringComparison.OrdinalIgnoreCase))
		{
			return await BuildFullSongListPlaybackQueueAsync(source, songId, sourceIndex, ct => _apiClient.GetDailyRecommendSongsAsync(ct), cancellationToken).ConfigureAwait(false);
		}

		if (string.Equals(source, "personalized_newsongs", StringComparison.OrdinalIgnoreCase) ||
			string.Equals(source, "personalized_newsong", StringComparison.OrdinalIgnoreCase))
		{
			return await BuildFullSongListPlaybackQueueAsync(source, songId, sourceIndex, ct => _apiClient.GetPersonalizedNewSongsAsync(100), cancellationToken).ConfigureAwait(false);
		}

		if (string.Equals(source, "recent_play", StringComparison.OrdinalIgnoreCase) ||
			string.Equals(source, "recent_listened", StringComparison.OrdinalIgnoreCase))
		{
			return await BuildFullSongListPlaybackQueueAsync(source, songId, sourceIndex, ct => _apiClient.GetRecentPlayedSongsAsync(300), cancellationToken).ConfigureAwait(false);
		}

		return null;
	}

	private async Task<PlaybackSourceQueueData> BuildPlaylistPlaybackQueueAsync(string source, string playlistId, string songId, int sourceIndex, CancellationToken cancellationToken)
	{
		List<string> ids = await FetchPlaylistTrackIdsAsync(playlistId, cancellationToken).ConfigureAwait(false);
		if (ids == null || ids.Count == 0)
		{
			return await BuildFullSongListPlaybackQueueAsync(source, songId, sourceIndex, ct => _apiClient.GetPlaylistSongsAsync(playlistId, ct), cancellationToken).ConfigureAwait(false);
		}

		CacheSourceTrackIdMap(source, ids);
		int anchorIndex = ResolveAnchorIndexFromIds(ids, songId, sourceIndex);
		List<SongInfo> songs = BuildPlaceholderQueue(ids.Count, source);
		SongInfo target = await FetchSourceSongByIdAsync(songId, source, cancellationToken).ConfigureAwait(false);
		if (target == null && anchorIndex >= 0 && anchorIndex < ids.Count)
		{
			target = await FetchSourceSongByIdAsync(ids[anchorIndex], source, cancellationToken).ConfigureAwait(false);
		}
		if (target == null)
		{
			return null;
		}
		if (anchorIndex < 0 || anchorIndex >= songs.Count)
		{
			anchorIndex = Math.Max(0, Math.Min(sourceIndex, songs.Count - 1));
		}
		songs[anchorIndex] = target;
		return CreatePlaybackSourceQueueData(source, songs, anchorIndex, target);
	}

	private async Task<PlaybackSourceQueueData> BuildLikedSongsPlaybackQueueAsync(string source, string songId, int sourceIndex, CancellationToken cancellationToken)
	{
		UserAccountInfo userInfo = await _apiClient.GetUserAccountAsync().ConfigureAwait(false);
		if (userInfo == null || userInfo.UserId <= 0)
		{
			return null;
		}

		List<string> ids = await _apiClient.GetUserLikedSongsAsync(userInfo.UserId).ConfigureAwait(false);
		if (ids == null || ids.Count == 0)
		{
			return null;
		}

		CacheSourceTrackIdMap(source, ids);
		int anchorIndex = ResolveAnchorIndexFromIds(ids, songId, sourceIndex);
		List<SongInfo> songs = BuildPlaceholderQueue(ids.Count, source);
		SongInfo target = await FetchSourceSongByIdAsync(songId, source, cancellationToken).ConfigureAwait(false);
		if (target == null && anchorIndex >= 0 && anchorIndex < ids.Count)
		{
			target = await FetchSourceSongByIdAsync(ids[anchorIndex], source, cancellationToken).ConfigureAwait(false);
		}
		if (target == null)
		{
			return null;
		}
		if (anchorIndex < 0 || anchorIndex >= songs.Count)
		{
			anchorIndex = Math.Max(0, Math.Min(sourceIndex, songs.Count - 1));
		}
		songs[anchorIndex] = target;
		return CreatePlaybackSourceQueueData(source, songs, anchorIndex, target);
	}

	private async Task<PlaybackSourceQueueData> BuildFullSongListPlaybackQueueAsync(
		string source,
		string songId,
		int sourceIndex,
		Func<CancellationToken, Task<List<SongInfo>>> fetchSongsAsync,
		CancellationToken cancellationToken)
	{
		if (fetchSongsAsync == null)
		{
			return null;
		}

		List<SongInfo> songs = await fetchSongsAsync(cancellationToken).ConfigureAwait(false) ?? new List<SongInfo>();
		if (songs.Count == 0)
		{
			return null;
		}

		ApplyViewSourceToSongs(songs, source);
		CacheSourceSongsIndexMap(source, songs);
		int anchorIndex = ResolveAnchorIndexFromSongs(songs, songId, sourceIndex);
		if (anchorIndex < 0 || anchorIndex >= songs.Count)
		{
			return null;
		}
		return CreatePlaybackSourceQueueData(source, songs, anchorIndex, songs[anchorIndex]);
	}

	private async Task<PlaybackSourceQueueData> BuildPodcastPlaybackQueueAsync(string source, string songId, int sourceIndex, CancellationToken cancellationToken)
	{
		ParsePodcastViewSource(source, out long radioId, out _, out bool ascending);
		if (radioId <= 0)
		{
			return null;
		}

		int anchorIndex = Math.Max(0, sourceIndex);
		int pageOffset = (anchorIndex / PodcastSoundPageSize) * PodcastSoundPageSize;
		var page = await _apiClient.GetPodcastEpisodesAsync(radioId, PodcastSoundPageSize, pageOffset, ascending, cancellationToken).ConfigureAwait(false);
		List<SongInfo> pageSongs = ConvertPodcastEpisodesToSongs(page.Episodes, source);
		int relative = anchorIndex - pageOffset;
		SongInfo target = (relative >= 0 && relative < pageSongs.Count) ? pageSongs[relative] : null;

		if (!IsSongIdMatch(target, songId))
		{
			var searched = await SearchPodcastSongByIdAsync(radioId, ascending, songId, cancellationToken).ConfigureAwait(false);
			if (searched.Song != null)
			{
				anchorIndex = searched.Index;
				pageOffset = searched.PageOffset;
				pageSongs = searched.PageSongs;
				target = searched.Song;
			}
		}

		if (target == null)
		{
			target = await FetchSourceSongByIdAsync(songId, source, cancellationToken).ConfigureAwait(false);
		}
		if (target == null)
		{
			return null;
		}

		int desiredCount = Math.Max(page.TotalCount, pageOffset + pageSongs.Count + (page.HasMore ? 1 : 0));
		desiredCount = Math.Max(desiredCount, anchorIndex + 1);
		List<SongInfo> songs = BuildPlaceholderQueue(desiredCount, source);
		FillQueuePage(songs, pageSongs, pageOffset);
		if (anchorIndex >= 0 && anchorIndex < songs.Count)
		{
			songs[anchorIndex] = target;
		}
		return CreatePlaybackSourceQueueData(source, songs, anchorIndex, target);
	}

	private async Task<PlaybackSourceQueueData> BuildArtistSongsPlaybackQueueAsync(string source, string songId, int sourceIndex, CancellationToken cancellationToken)
	{
		ParseArtistListViewSource(source, out long artistId, out _, out string order);
		if (artistId <= 0)
		{
			return null;
		}

		int anchorIndex = Math.Max(0, sourceIndex);
		int pageOffset = (anchorIndex / ArtistSongsPageSize) * ArtistSongsPageSize;
		var page = await _apiClient.GetArtistSongsAsync(artistId, ArtistSongsPageSize, pageOffset, order, cancellationToken).ConfigureAwait(false);
		List<SongInfo> pageSongs = page.Songs ?? new List<SongInfo>();
		ApplyViewSourceToSongs(pageSongs, source);
		int relative = anchorIndex - pageOffset;
		SongInfo target = (relative >= 0 && relative < pageSongs.Count) ? pageSongs[relative] : null;

		if (!IsSongIdMatch(target, songId))
		{
			var searched = await SearchArtistSongByIdAsync(artistId, order, songId, cancellationToken).ConfigureAwait(false);
			if (searched.Song != null)
			{
				anchorIndex = searched.Index;
				pageOffset = searched.PageOffset;
				pageSongs = searched.PageSongs;
				target = searched.Song;
			}
		}

		if (target == null)
		{
			target = await FetchSourceSongByIdAsync(songId, source, cancellationToken).ConfigureAwait(false);
		}
		if (target == null)
		{
			return null;
		}

		int desiredCount = Math.Max(page.TotalCount, pageOffset + pageSongs.Count + (page.HasMore ? 1 : 0));
		desiredCount = Math.Max(desiredCount, anchorIndex + 1);
		List<SongInfo> songs = BuildPlaceholderQueue(desiredCount, source);
		FillQueuePage(songs, pageSongs, pageOffset);
		if (anchorIndex >= 0 && anchorIndex < songs.Count)
		{
			songs[anchorIndex] = target;
		}
		return CreatePlaybackSourceQueueData(source, songs, anchorIndex, target);
	}

	private async Task<PlaybackSourceQueueData> BuildSearchSongsPlaybackQueueAsync(string source, string songId, int sourceIndex, CancellationToken cancellationToken)
	{
		ParseSearchViewSource(source, out _, out string keyword, out _);
		if (string.IsNullOrWhiteSpace(keyword))
		{
			return null;
		}

		int pageSize = Math.Max(1, _resultsPerPage);
		int anchorIndex = Math.Max(0, sourceIndex);
		int pageOffset = (anchorIndex / pageSize) * pageSize;
		SearchResult<SongInfo> page = await _apiClient.SearchSongsAsync(keyword, pageSize, pageOffset, cancellationToken).ConfigureAwait(false);
		List<SongInfo> pageSongs = page?.Items ?? new List<SongInfo>();
		ApplyViewSourceToSongs(pageSongs, source);
		int relative = anchorIndex - pageOffset;
		SongInfo target = (relative >= 0 && relative < pageSongs.Count) ? pageSongs[relative] : null;

		if (!IsSongIdMatch(target, songId))
		{
			var searched = await SearchSongResultByIdAsync(keyword, songId, cancellationToken).ConfigureAwait(false);
			if (searched.Song != null)
			{
				anchorIndex = searched.Index;
				pageOffset = searched.PageOffset;
				pageSongs = searched.PageSongs;
				target = searched.Song;
			}
		}

		if (target == null)
		{
			target = await FetchSourceSongByIdAsync(songId, source, cancellationToken).ConfigureAwait(false);
		}
		if (target == null)
		{
			return null;
		}

		int desiredCount = Math.Max(page?.TotalCount ?? 0, pageOffset + pageSongs.Count + ((page?.HasMore ?? false) ? 1 : 0));
		desiredCount = Math.Max(desiredCount, anchorIndex + 1);
		List<SongInfo> songs = BuildPlaceholderQueue(desiredCount, source);
		FillQueuePage(songs, pageSongs, pageOffset);
		if (anchorIndex >= 0 && anchorIndex < songs.Count)
		{
			songs[anchorIndex] = target;
		}
		return CreatePlaybackSourceQueueData(source, songs, anchorIndex, target);
	}

	private async Task<PlaybackSourceQueueData> BuildCloudPlaybackQueueAsync(string source, string songId, int sourceIndex, CancellationToken cancellationToken)
	{
		int anchorIndex = Math.Max(0, sourceIndex);
		int pageOffset = (anchorIndex / CloudPageSize) * CloudPageSize;
		CloudSongPageResult page = await _apiClient.GetCloudSongsAsync(CloudPageSize, pageOffset, cancellationToken).ConfigureAwait(false);
		List<SongInfo> pageSongs = page?.Songs ?? new List<SongInfo>();
		ApplyViewSourceToSongs(pageSongs, source);
		int relative = anchorIndex - pageOffset;
		SongInfo target = (relative >= 0 && relative < pageSongs.Count) ? pageSongs[relative] : null;

		if (!IsSongIdMatch(target, songId))
		{
			var searched = await SearchCloudSongByIdAsync(songId, cancellationToken).ConfigureAwait(false);
			if (searched.Song != null)
			{
				anchorIndex = searched.Index;
				pageOffset = searched.PageOffset;
				pageSongs = searched.PageSongs;
				target = searched.Song;
			}
		}

		if (target == null)
		{
			target = await FetchSourceSongByIdAsync(songId, source, cancellationToken).ConfigureAwait(false);
		}
		if (target == null)
		{
			return null;
		}

		int desiredCount = Math.Max(page?.TotalCount ?? 0, pageOffset + pageSongs.Count + ((page?.HasMore ?? false) ? 1 : 0));
		desiredCount = Math.Max(desiredCount, anchorIndex + 1);
		List<SongInfo> songs = BuildPlaceholderQueue(desiredCount, source);
		FillQueuePage(songs, pageSongs, pageOffset);
		if (anchorIndex >= 0 && anchorIndex < songs.Count)
		{
			songs[anchorIndex] = target;
		}
		return CreatePlaybackSourceQueueData(source, songs, anchorIndex, target);
	}

	private async Task<SongInfo> ResolveSongFromPlaybackSourceByIndexAsync(string source, int songIndex, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(source) || songIndex < 0 || _apiClient == null)
		{
			return null;
		}

		string normalizedSource = NormalizePlaybackSourceForPersistence(source);
		if (normalizedSource.StartsWith("playlist:", StringComparison.OrdinalIgnoreCase))
		{
			string playlistId = normalizedSource.Substring("playlist:".Length);
			List<string> ids = await FetchPlaylistTrackIdsAsync(playlistId, cancellationToken).ConfigureAwait(false);
			return await ResolveSongFromOrderedSourceIdsAsync(normalizedSource, ids, songIndex, cancellationToken).ConfigureAwait(false);
		}

		if (string.Equals(normalizedSource, "user_liked_songs", StringComparison.OrdinalIgnoreCase))
		{
			UserAccountInfo userInfo = await _apiClient.GetUserAccountAsync().ConfigureAwait(false);
			if (userInfo == null || userInfo.UserId <= 0)
			{
				return null;
			}
			List<string> ids = await _apiClient.GetUserLikedSongsAsync(userInfo.UserId).ConfigureAwait(false);
			return await ResolveSongFromOrderedSourceIdsAsync(normalizedSource, ids, songIndex, cancellationToken).ConfigureAwait(false);
		}

		if (normalizedSource.StartsWith("album:", StringComparison.OrdinalIgnoreCase))
		{
			List<SongInfo> songs = await _apiClient.GetAlbumSongsAsync(normalizedSource.Substring("album:".Length), cancellationToken).ConfigureAwait(false) ?? new List<SongInfo>();
			return GetSongFromFullSourceList(normalizedSource, songs, songIndex);
		}

		if (normalizedSource.StartsWith("podcast:", StringComparison.OrdinalIgnoreCase))
		{
			ParsePodcastViewSource(normalizedSource, out long radioId, out _, out bool ascending);
			if (radioId > 0)
			{
				int pageOffset = (songIndex / PodcastSoundPageSize) * PodcastSoundPageSize;
				var page = await _apiClient.GetPodcastEpisodesAsync(radioId, PodcastSoundPageSize, pageOffset, ascending, cancellationToken).ConfigureAwait(false);
				List<SongInfo> songs = ConvertPodcastEpisodesToSongs(page.Episodes, normalizedSource);
				EnsurePlaybackQueueSourceCapacity(normalizedSource, Math.Max(page.TotalCount, pageOffset + songs.Count + (page.HasMore ? 1 : 0)));
				return GetSongFromPage(songs, songIndex - pageOffset, normalizedSource);
			}
		}

		if (normalizedSource.StartsWith("artist_songs:", StringComparison.OrdinalIgnoreCase))
		{
			ParseArtistListViewSource(normalizedSource, out long artistId, out _, out string order);
			if (artistId > 0)
			{
				int pageOffset = (songIndex / ArtistSongsPageSize) * ArtistSongsPageSize;
				var page = await _apiClient.GetArtistSongsAsync(artistId, ArtistSongsPageSize, pageOffset, order, cancellationToken).ConfigureAwait(false);
				List<SongInfo> songs = page.Songs ?? new List<SongInfo>();
				ApplyViewSourceToSongs(songs, normalizedSource);
				EnsurePlaybackQueueSourceCapacity(normalizedSource, Math.Max(page.TotalCount, pageOffset + songs.Count + (page.HasMore ? 1 : 0)));
				return GetSongFromPage(songs, songIndex - pageOffset, normalizedSource);
			}
		}

		if (normalizedSource.StartsWith("search:", StringComparison.OrdinalIgnoreCase))
		{
			ParseSearchViewSource(normalizedSource, out string searchType, out string keyword, out _);
			if (string.Equals(searchType, "歌曲", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(keyword))
			{
				int pageSize = Math.Max(1, _resultsPerPage);
				int pageOffset = (songIndex / pageSize) * pageSize;
				SearchResult<SongInfo> page = await _apiClient.SearchSongsAsync(keyword, pageSize, pageOffset, cancellationToken).ConfigureAwait(false);
				List<SongInfo> songs = page?.Items ?? new List<SongInfo>();
				ApplyViewSourceToSongs(songs, normalizedSource);
				EnsurePlaybackQueueSourceCapacity(normalizedSource, Math.Max(page?.TotalCount ?? 0, pageOffset + songs.Count + ((page?.HasMore ?? false) ? 1 : 0)));
				return GetSongFromPage(songs, songIndex - pageOffset, normalizedSource);
			}
		}

		if (string.Equals(normalizedSource, "user_cloud", StringComparison.OrdinalIgnoreCase))
		{
			int pageOffset = (songIndex / CloudPageSize) * CloudPageSize;
			CloudSongPageResult page = await _apiClient.GetCloudSongsAsync(CloudPageSize, pageOffset, cancellationToken).ConfigureAwait(false);
			List<SongInfo> songs = page?.Songs ?? new List<SongInfo>();
			ApplyViewSourceToSongs(songs, normalizedSource);
			EnsurePlaybackQueueSourceCapacity(normalizedSource, Math.Max(page?.TotalCount ?? 0, pageOffset + songs.Count + ((page?.HasMore ?? false) ? 1 : 0)));
			return GetSongFromPage(songs, songIndex - pageOffset, normalizedSource);
		}

		if (TryParseNewSongsViewSource(normalizedSource, out int areaType, out _, out _, out _))
		{
			List<SongInfo> songs = await _apiClient.GetNewSongsAsync(areaType).ConfigureAwait(false) ?? new List<SongInfo>();
			return GetSongFromFullSourceList(BuildNewSongsViewSource(areaType, 0), songs, songIndex);
		}

		if (string.Equals(normalizedSource, "daily_recommend_songs", StringComparison.OrdinalIgnoreCase) ||
			string.Equals(normalizedSource, "daily_recommend", StringComparison.OrdinalIgnoreCase))
		{
			List<SongInfo> songs = await _apiClient.GetDailyRecommendSongsAsync(cancellationToken).ConfigureAwait(false) ?? new List<SongInfo>();
			return GetSongFromFullSourceList(normalizedSource, songs, songIndex);
		}

		if (string.Equals(normalizedSource, "personalized_newsongs", StringComparison.OrdinalIgnoreCase) ||
			string.Equals(normalizedSource, "personalized_newsong", StringComparison.OrdinalIgnoreCase))
		{
			List<SongInfo> songs = await _apiClient.GetPersonalizedNewSongsAsync(100).ConfigureAwait(false) ?? new List<SongInfo>();
			return GetSongFromFullSourceList(normalizedSource, songs, songIndex);
		}

		if (string.Equals(normalizedSource, "recent_play", StringComparison.OrdinalIgnoreCase) ||
			string.Equals(normalizedSource, "recent_listened", StringComparison.OrdinalIgnoreCase))
		{
			List<SongInfo> songs = await _apiClient.GetRecentPlayedSongsAsync(300).ConfigureAwait(false) ?? new List<SongInfo>();
			return GetSongFromFullSourceList(normalizedSource, songs, songIndex);
		}

		return null;
	}

	private async Task<SongInfo> ResolveSongFromOrderedSourceIdsAsync(string source, List<string> ids, int songIndex, CancellationToken cancellationToken)
	{
		if (ids == null || songIndex < 0 || songIndex >= ids.Count)
		{
			return null;
		}

		CacheSourceTrackIdMap(source, ids);
		EnsurePlaybackQueueSourceCapacity(source, ids.Count);
		string songId = ids[songIndex];
		if (string.IsNullOrWhiteSpace(songId))
		{
			return null;
		}

		return await FetchSourceSongByIdAsync(songId, source, cancellationToken).ConfigureAwait(false);
	}

	private SongInfo GetSongFromFullSourceList(string source, List<SongInfo> songs, int songIndex)
	{
		if (songs == null || songIndex < 0 || songIndex >= songs.Count)
		{
			return null;
		}

		ApplyViewSourceToSongs(songs, source);
		CacheSourceSongsIndexMap(source, songs);
		EnsurePlaybackQueueSourceCapacity(source, songs.Count);
		return songs[songIndex];
	}

	private void EnsurePlaybackQueueSourceCapacity(string source, int desiredCount)
	{
		if (_playbackQueue == null || string.IsNullOrWhiteSpace(source) || desiredCount <= 0)
		{
			return;
		}

		_playbackQueue.TryEnsureQueueLengthWithPlaceholders(source, desiredCount);
	}

	private SongInfo GetSongFromPage(List<SongInfo> songs, int relativeIndex, string source)
	{
		if (songs == null || relativeIndex < 0 || relativeIndex >= songs.Count)
		{
			return null;
		}

		SongInfo song = songs[relativeIndex];
		if (song != null)
		{
			song.ViewSource = source ?? string.Empty;
		}
		return song;
	}

	private static List<SongInfo> ConvertPodcastEpisodesToSongs(List<PodcastEpisodeInfo> episodes, string source)
	{
		List<SongInfo> songs = new List<SongInfo>();
		if (episodes == null)
		{
			return songs;
		}

		foreach (PodcastEpisodeInfo episode in episodes)
		{
			SongInfo song = episode?.Song;
			if (song == null)
			{
				continue;
			}
			song.ViewSource = source ?? string.Empty;
			song.IsPodcastEpisode = true;
			songs.Add(song);
		}
		return songs;
	}

	private async Task<SongInfo> FetchSourceSongByIdAsync(string songId, string source, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(songId))
		{
			return null;
		}

		List<SongInfo> songs = await FetchPlaylistSongsByIdsBatchAsync(new List<string> { songId }, cancellationToken).ConfigureAwait(false);
		SongInfo song = songs?.FirstOrDefault(s => s != null && string.Equals(s.Id, songId, StringComparison.OrdinalIgnoreCase));
		if (song != null)
		{
			song.ViewSource = source ?? string.Empty;
			ApplySongLikeStates(new[] { song });
		}
		return song;
	}

	private void CacheSourceTrackIdMap(string source, List<string> ids)
	{
		if (string.IsNullOrWhiteSpace(source) || ids == null || ids.Count == 0)
		{
			return;
		}

		Dictionary<string, int> map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		for (int i = 0; i < ids.Count; i++)
		{
			string id = ids[i];
			if (!string.IsNullOrWhiteSpace(id) && !map.ContainsKey(id))
			{
				map[id] = i;
			}
		}
		CachePlaylistTrackIndexMap(source, map);
	}

	private void CacheSourceSongsIndexMap(string source, List<SongInfo> songs)
	{
		if (string.IsNullOrWhiteSpace(source) || songs == null || songs.Count == 0)
		{
			return;
		}

		Dictionary<string, int> map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		for (int i = 0; i < songs.Count; i++)
		{
			string id = songs[i]?.Id;
			if (!string.IsNullOrWhiteSpace(id) && !map.ContainsKey(id))
			{
				map[id] = i;
			}
		}
		CachePlaylistTrackIndexMap(source, map);
	}

	private int ResolveAnchorIndexFromIds(List<string> ids, string songId, int sourceIndex)
	{
		if (sourceIndex >= 0 && ids != null && sourceIndex < ids.Count)
		{
			if (string.IsNullOrWhiteSpace(songId) || string.Equals(ids[sourceIndex], songId, StringComparison.OrdinalIgnoreCase))
			{
				return sourceIndex;
			}
		}

		if (ids != null && !string.IsNullOrWhiteSpace(songId))
		{
			int found = ids.FindIndex(id => string.Equals(id, songId, StringComparison.OrdinalIgnoreCase));
			if (found >= 0)
			{
				return found;
			}
		}

		return sourceIndex;
	}

	private int ResolveAnchorIndexFromSongs(List<SongInfo> songs, string songId, int sourceIndex)
	{
		if (sourceIndex >= 0 && songs != null && sourceIndex < songs.Count)
		{
			if (string.IsNullOrWhiteSpace(songId) || IsSongIdMatch(songs[sourceIndex], songId))
			{
				return sourceIndex;
			}
		}

		if (songs != null && !string.IsNullOrWhiteSpace(songId))
		{
			int found = songs.FindIndex(song => IsSongIdMatch(song, songId));
			if (found >= 0)
			{
				return found;
			}
		}

		return sourceIndex;
	}

	private static bool IsSongIdMatch(SongInfo song, string songId)
	{
		return song != null &&
			!string.IsNullOrWhiteSpace(song.Id) &&
			!string.IsNullOrWhiteSpace(songId) &&
			string.Equals(song.Id, songId, StringComparison.OrdinalIgnoreCase);
	}

	private List<SongInfo> BuildPlaceholderQueue(int totalCount, string source)
	{
		int count = Math.Max(0, totalCount);
		List<SongInfo> songs = new List<SongInfo>(count);
		for (int i = 0; i < count; i++)
		{
			songs.Add(CreatePlaceholderSong(source));
		}
		return songs;
	}

	private static void FillQueuePage(List<SongInfo> queue, List<SongInfo> pageSongs, int pageOffset)
	{
		if (queue == null || pageSongs == null || pageSongs.Count == 0)
		{
			return;
		}

		for (int i = 0; i < pageSongs.Count; i++)
		{
			int targetIndex = pageOffset + i;
			if (targetIndex >= 0 && targetIndex < queue.Count && pageSongs[i] != null)
			{
				queue[targetIndex] = pageSongs[i];
			}
		}
	}

	private PlaybackSourceQueueData CreatePlaybackSourceQueueData(string source, List<SongInfo> songs, int anchorIndex, SongInfo target)
	{
		if (songs == null || target == null || anchorIndex < 0 || anchorIndex >= songs.Count)
		{
			return null;
		}

		ApplyViewSourceToSongs(songs.Where(song => song != null && !IsPlaceholderSong(song)), source);
		target.ViewSource = source ?? string.Empty;
		return new PlaybackSourceQueueData
		{
			Source = source ?? string.Empty,
			Songs = songs,
			AnchorIndex = anchorIndex,
			TargetSong = target
		};
	}

	private async Task<(SongInfo Song, int Index, int PageOffset, List<SongInfo> PageSongs)> SearchPodcastSongByIdAsync(long radioId, bool ascending, string songId, CancellationToken cancellationToken)
	{
		int offset = 0;
		for (int safety = 0; safety < 200; safety++)
		{
			var page = await _apiClient.GetPodcastEpisodesAsync(radioId, PodcastSoundPageSize, offset, ascending, cancellationToken).ConfigureAwait(false);
			List<SongInfo> songs = ConvertPodcastEpisodesToSongs(page.Episodes, BuildPodcastPlaybackSource(radioId, ascending));
			int index = songs.FindIndex(song => IsSongIdMatch(song, songId));
			if (index >= 0)
			{
				return (songs[index], offset + index, offset, songs);
			}
			if (songs.Count == 0 || !page.HasMore)
			{
				break;
			}
			offset = Math.Max(offset + PodcastSoundPageSize, offset + songs.Count);
		}
		return (null, -1, 0, new List<SongInfo>());
	}

	private async Task<(SongInfo Song, int Index, int PageOffset, List<SongInfo> PageSongs)> SearchArtistSongByIdAsync(long artistId, string order, string songId, CancellationToken cancellationToken)
	{
		int offset = 0;
		string source = $"artist_songs:{artistId}:order{order}:offset0";
		for (int safety = 0; safety < 200; safety++)
		{
			var page = await _apiClient.GetArtistSongsAsync(artistId, ArtistSongsPageSize, offset, order, cancellationToken).ConfigureAwait(false);
			List<SongInfo> songs = page.Songs ?? new List<SongInfo>();
			ApplyViewSourceToSongs(songs, source);
			int index = songs.FindIndex(song => IsSongIdMatch(song, songId));
			if (index >= 0)
			{
				return (songs[index], offset + index, offset, songs);
			}
			if (songs.Count == 0 || !page.HasMore)
			{
				break;
			}
			offset = Math.Max(offset + ArtistSongsPageSize, offset + songs.Count);
		}
		return (null, -1, 0, new List<SongInfo>());
	}

	private async Task<(SongInfo Song, int Index, int PageOffset, List<SongInfo> PageSongs)> SearchSongResultByIdAsync(string keyword, string songId, CancellationToken cancellationToken)
	{
		int offset = 0;
		int pageSize = Math.Max(1, _resultsPerPage);
		string source = $"search:song:{keyword}:page1";
		for (int safety = 0; safety < 200; safety++)
		{
			SearchResult<SongInfo> page = await _apiClient.SearchSongsAsync(keyword, pageSize, offset, cancellationToken).ConfigureAwait(false);
			List<SongInfo> songs = page?.Items ?? new List<SongInfo>();
			ApplyViewSourceToSongs(songs, source);
			int index = songs.FindIndex(song => IsSongIdMatch(song, songId));
			if (index >= 0)
			{
				return (songs[index], offset + index, offset, songs);
			}
			if (songs.Count == 0 || !(page?.HasMore ?? false))
			{
				break;
			}
			offset = Math.Max(offset + pageSize, offset + songs.Count);
		}
		return (null, -1, 0, new List<SongInfo>());
	}

	private async Task<(SongInfo Song, int Index, int PageOffset, List<SongInfo> PageSongs)> SearchCloudSongByIdAsync(string songId, CancellationToken cancellationToken)
	{
		int offset = 0;
		for (int safety = 0; safety < 200; safety++)
		{
			CloudSongPageResult page = await _apiClient.GetCloudSongsAsync(CloudPageSize, offset, cancellationToken).ConfigureAwait(false);
			List<SongInfo> songs = page?.Songs ?? new List<SongInfo>();
			ApplyViewSourceToSongs(songs, "user_cloud");
			int index = songs.FindIndex(song => IsSongIdMatch(song, songId));
			if (index >= 0)
			{
				return (songs[index], offset + index, offset, songs);
			}
			if (songs.Count == 0 || !(page?.HasMore ?? false))
			{
				break;
			}
			offset = Math.Max(offset + CloudPageSize, offset + songs.Count);
		}
		return (null, -1, 0, new List<SongInfo>());
	}
}
}
