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
	private void ScheduleLibraryStateRefresh(bool includeLikedSongs = true, bool includePlaylists = true, bool includeAlbums = true, bool includePodcasts = true, bool includeArtists = true)
	{
		if (!IsUserLoggedIn() || _apiClient == null)
		{
			return;
		}
		List<LibraryEntityType> list = new List<LibraryEntityType>();
		if (includeLikedSongs)
		{
			list.Add(LibraryEntityType.Songs);
		}
		if (includePlaylists)
		{
			list.Add(LibraryEntityType.Playlists);
		}
		if (includeAlbums)
		{
			list.Add(LibraryEntityType.Albums);
		}
		if (includePodcasts)
		{
			list.Add(LibraryEntityType.Podcasts);
		}
		if (includeArtists)
		{
			list.Add(LibraryEntityType.Artists);
		}
		foreach (LibraryEntityType item in list)
		{
			RequestLibraryRefresh(item);
		}
	}

	private void RequestLibraryRefresh(LibraryEntityType entity, bool forceRefresh = false)
	{
		if (IsUserLoggedIn() && _apiClient != null)
		{
			Task.Run(() => RefreshLibraryStateAsync(entity, forceRefresh, CancellationToken.None));
		}
	}

	private Task EnsureLibraryStateFreshAsync(LibraryEntityType entity, bool forceRefresh = false, CancellationToken cancellationToken = default(CancellationToken))
	{
		RequestLibraryRefresh(entity, forceRefresh);
		return Task.CompletedTask;
	}

	private async Task RefreshLibraryStateAsync(LibraryEntityType entity, bool forceRefresh, CancellationToken cancellationToken)
	{
		List<LibraryEntityType> targets = ExpandLibraryEntities(entity).ToList();
		if (targets.Count == 0)
		{
			return;
		}
		double allocation = DownloadBandwidthCoordinator.Instance.GetDownloadBandwidthAllocation();
		if (allocation >= 0.6 && targets.Count > 1)
		{
			IEnumerable<Task> tasks = targets.Select((LibraryEntityType t) => RefreshLibraryEntityAsync(t, forceRefresh, cancellationToken));
			await Task.WhenAll(tasks);
			return;
		}
		foreach (LibraryEntityType target in targets)
		{
			await RefreshLibraryEntityAsync(target, forceRefresh, cancellationToken);
		}
	}

	private IEnumerable<LibraryEntityType> ExpandLibraryEntities(LibraryEntityType entity)
	{
		if (entity == LibraryEntityType.All)
		{
			yield return LibraryEntityType.Songs;
			yield return LibraryEntityType.Playlists;
			yield return LibraryEntityType.Albums;
			yield return LibraryEntityType.Artists;
			yield return LibraryEntityType.Podcasts;
		}
		else
		{
			yield return entity;
		}
	}

	private async Task RefreshLibraryEntityAsync(LibraryEntityType entity, bool forceRefresh, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (!forceRefresh && IsLibraryCacheFresh(entity))
		{
			NotifyLibraryStateUpdated(entity);
			return;
		}
		switch (entity)
		{
		case LibraryEntityType.Songs:
			await RefreshLikedSongsCacheAsync(cancellationToken);
			break;
		case LibraryEntityType.Playlists:
			await RefreshPlaylistSubscriptionCacheAsync(cancellationToken);
			break;
		case LibraryEntityType.Albums:
			await RefreshAlbumSubscriptionCacheAsync(cancellationToken);
			break;
		case LibraryEntityType.Artists:
			await RefreshArtistSubscriptionCacheAsync(cancellationToken);
			break;
		case LibraryEntityType.Podcasts:
			await RefreshPodcastSubscriptionCacheAsync(cancellationToken);
			break;
		}
		lock (_libraryStateLock)
		{
			_libraryCacheTimestamps[entity] = DateTime.UtcNow;
		}
		NotifyLibraryStateUpdated(entity);
	}

	private void NotifyLibraryStateUpdated(LibraryEntityType entity)
	{
		SafeInvoke(delegate
		{
			ApplyLibraryStatePatch(entity);
		});
	}

	private void ApplyLibraryStatePatch(LibraryEntityType entity)
	{
		switch (entity)
		{
		case LibraryEntityType.Songs:
			ApplySongLikeStates(_currentSongs);
			break;
		case LibraryEntityType.Playlists:
			ApplyPlaylistSubscriptionState(_currentPlaylists);
			break;
		case LibraryEntityType.Albums:
			ApplyAlbumSubscriptionState(_currentAlbums);
			break;
		case LibraryEntityType.Artists:
			ApplyArtistSubscriptionStates(_currentArtists);
			break;
		case LibraryEntityType.Podcasts:
			ApplyPodcastSubscriptionState(_currentPodcasts);
			break;
		case LibraryEntityType.All:
			ApplySongLikeStates(_currentSongs);
			ApplyPlaylistSubscriptionState(_currentPlaylists);
			ApplyAlbumSubscriptionState(_currentAlbums);
			ApplyArtistSubscriptionStates(_currentArtists);
			ApplyPodcastSubscriptionState(_currentPodcasts);
			break;
		}
		if (_currentListItems != null && _currentListItems.Count > 0)
		{
			ApplyListItemLibraryStates(_currentListItems);
		}
		resultListView?.Invalidate();
	}

	private bool IsLibraryCacheFresh(LibraryEntityType entity)
	{
		lock (_libraryStateLock)
		{
			DateTime value;
			return _libraryCacheTimestamps.TryGetValue(entity, out value) && DateTime.UtcNow - value < LibraryRefreshInterval;
		}
	}

	private void InvalidateLibraryCaches()
	{
		lock (_libraryStateLock)
		{
			_likedSongIds.Clear();
			_subscribedPlaylistIds.Clear();
			_ownedPlaylistIds.Clear();
			_subscribedAlbumIds.Clear();
			_subscribedPodcastIds.Clear();
			_subscribedArtistIds.Clear();
			_likedSongsCacheValid = false;
			foreach (LibraryEntityType item in _libraryCacheTimestamps.Keys.ToList())
			{
				_libraryCacheTimestamps[item] = DateTime.MinValue;
			}
		}
	}

	private async Task RefreshLikedSongsCacheAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		long userId = GetCurrentUserId();
		if (userId <= 0)
		{
			return;
		}
		try
		{
			List<string> ids = await _apiClient.GetUserLikedSongsAsync(userId);
			cancellationToken.ThrowIfCancellationRequested();
			lock (_libraryStateLock)
			{
				_likedSongIds.Clear();
				foreach (string id in ids)
				{
					if (!string.IsNullOrWhiteSpace(id))
					{
						_likedSongIds.Add(id);
					}
				}
				_likedSongsCacheValid = true;
			}
		}
		catch (Exception value)
		{
			Debug.WriteLine($"[LibraryCache] 刷新喜欢的歌曲失败: {value}");
		}
	}

	private async Task RefreshPlaylistSubscriptionCacheAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		long userId = GetCurrentUserId();
		if (userId <= 0)
		{
			return;
		}
		try
		{
			int offset = 0;
			List<PlaylistInfo> aggregated = new List<PlaylistInfo>();
			while (true)
			{
				var (playlists, total) = await _apiClient.GetUserPlaylistsAsync(userId, 1000, offset);
				cancellationToken.ThrowIfCancellationRequested();
				if (playlists == null || playlists.Count == 0)
				{
					break;
				}
				aggregated.AddRange(playlists);
				if (playlists.Count < 1000 || aggregated.Count >= total)
				{
					break;
				}
				offset = checked(offset + playlists.Count);
			}
			lock (_libraryStateLock)
			{
				_subscribedPlaylistIds.Clear();
				_ownedPlaylistIds.Clear();
				foreach (PlaylistInfo playlist in aggregated)
				{
					if (playlist != null && !string.IsNullOrWhiteSpace(playlist.Id))
					{
						if (IsPlaylistOwnedByUser(playlist, userId))
						{
							_ownedPlaylistIds.Add(playlist.Id);
						}
						else
						{
							_subscribedPlaylistIds.Add(playlist.Id);
						}
					}
				}
			}
		}
		catch (Exception value)
		{
			Debug.WriteLine($"[LibraryCache] 刷新歌单收藏状态失败: {value}");
		}
	}

	private async Task RefreshAlbumSubscriptionCacheAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		if (!IsUserLoggedIn())
		{
			return;
		}
		try
		{
			int offset = 0;
			List<AlbumInfo> aggregated = new List<AlbumInfo>();
			while (true)
			{
				var (albums, total) = await _apiClient.GetUserAlbumsAsync(100, offset);
				cancellationToken.ThrowIfCancellationRequested();
				if (albums == null || albums.Count == 0)
				{
					break;
				}
				aggregated.AddRange(albums);
				if (albums.Count < 100 || aggregated.Count >= total)
				{
					break;
				}
				offset = checked(offset + albums.Count);
			}
			lock (_libraryStateLock)
			{
				_subscribedAlbumIds.Clear();
				foreach (AlbumInfo album in aggregated)
				{
					if (!string.IsNullOrWhiteSpace(album?.Id))
					{
						_subscribedAlbumIds.Add(album.Id);
					}
				}
			}
		}
		catch (Exception value)
		{
			Debug.WriteLine($"[LibraryCache] 刷新收藏专辑失败: {value}");
		}
	}

	private async Task RefreshPodcastSubscriptionCacheAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		if (!IsUserLoggedIn())
		{
			return;
		}
		try
		{
			int offset = 0;
			List<PodcastRadioInfo> aggregated = new List<PodcastRadioInfo>();
			while (true)
			{
				var (podcasts, total) = await _apiClient.GetSubscribedPodcastsAsync(300, offset);
				cancellationToken.ThrowIfCancellationRequested();
				if (podcasts == null || podcasts.Count == 0)
				{
					break;
				}
				aggregated.AddRange(podcasts);
				if (podcasts.Count < 300 || aggregated.Count >= total)
				{
					break;
				}
				offset = checked(offset + podcasts.Count);
			}
			lock (_libraryStateLock)
			{
				_subscribedPodcastIds.Clear();
				foreach (PodcastRadioInfo podcast in aggregated)
				{
					if (podcast != null && podcast.Id > 0)
					{
						_subscribedPodcastIds.Add(podcast.Id);
					}
				}
			}
		}
		catch (Exception value)
		{
			Debug.WriteLine($"[LibraryCache] 刷新收藏播客失败: {value}");
		}
	}

	private async Task RefreshArtistSubscriptionCacheAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		if (!IsUserLoggedIn())
		{
			return;
		}
		try
		{
			int offset = 0;
			List<ArtistInfo> aggregated = new List<ArtistInfo>();
			while (true)
			{
				SearchResult<ArtistInfo> result = await _apiClient.GetArtistSubscriptionsAsync(200, offset);
				cancellationToken.ThrowIfCancellationRequested();
				if (result?.Items == null || result.Items.Count == 0)
				{
					break;
				}
				aggregated.AddRange(result.Items);
				if (!result.HasMore)
				{
					break;
				}
				offset = checked(offset + result.Items.Count);
			}
			lock (_libraryStateLock)
			{
				_subscribedArtistIds.Clear();
				foreach (ArtistInfo artist in aggregated)
				{
					if (artist != null && artist.Id > 0)
					{
						_subscribedArtistIds.Add(artist.Id);
					}
				}
			}
		}
		catch (Exception value)
		{
			Debug.WriteLine($"[LibraryCache] 刷新收藏歌手失败: {value}");
		}
	}

	private void ApplySongLikeStates(IEnumerable<SongInfo?>? songs)
	{
		if (songs == null)
		{
			return;
		}
		lock (_libraryStateLock)
		{
			if (_likedSongIds.Count == 0 && !_likedSongsCacheValid)
			{
				return;
			}
			foreach (SongInfo song in songs)
			{
				if (song != null)
				{
					string text = ResolveSongIdForLibraryState(song);
					if (!string.IsNullOrEmpty(text) && _likedSongIds.Contains(text))
					{
						song.IsLiked = true;
					}
				}
			}
		}
	}

	private void ApplyPlaylistSubscriptionState(IEnumerable<PlaylistInfo?>? playlists)
	{
		if (playlists == null)
		{
			return;
		}
		lock (_libraryStateLock)
		{
			foreach (PlaylistInfo playlist in playlists)
			{
				if (playlist != null && !string.IsNullOrWhiteSpace(playlist.Id))
				{
					if (_ownedPlaylistIds.Contains(playlist.Id))
					{
						playlist.IsSubscribed = false;
					}
					else if (_subscribedPlaylistIds.Contains(playlist.Id))
					{
						playlist.IsSubscribed = true;
					}
				}
			}
		}
	}

	private void ApplyAlbumSubscriptionState(IEnumerable<AlbumInfo?>? albums)
	{
		if (albums == null)
		{
			return;
		}
		lock (_libraryStateLock)
		{
			foreach (AlbumInfo album in albums)
			{
				if (album != null && !string.IsNullOrWhiteSpace(album.Id) && _subscribedAlbumIds.Contains(album.Id))
				{
					album.IsSubscribed = true;
				}
			}
		}
	}

	private void ApplyArtistSubscriptionStates(IEnumerable<ArtistInfo?>? artists)
	{
		if (artists == null)
		{
			return;
		}
		lock (_libraryStateLock)
		{
			foreach (ArtistInfo artist in artists)
			{
				if (artist != null && artist.Id > 0 && _subscribedArtistIds.Contains(artist.Id))
				{
					artist.IsSubscribed = true;
				}
			}
		}
	}

	private void ApplyPodcastSubscriptionState(IEnumerable<PodcastRadioInfo?>? podcasts)
	{
		if (podcasts == null)
		{
			return;
		}
		lock (_libraryStateLock)
		{
			foreach (PodcastRadioInfo podcast in podcasts)
			{
				if (podcast != null && podcast.Id > 0 && !podcast.Subscribed && _subscribedPodcastIds.Contains(podcast.Id))
				{
					podcast.Subscribed = true;
				}
			}
		}
	}

	private void ApplyListItemLibraryStates(IEnumerable<ListItemInfo>? items)
	{
		if (items != null)
		{
			ApplySongLikeStates(from i in items
				where i?.Song != null
				select i.Song);
			ApplyPlaylistSubscriptionState(from i in items
				where i?.Playlist != null
				select i.Playlist);
			ApplyAlbumSubscriptionState(from i in items
				where i?.Album != null
				select i.Album);
			ApplyArtistSubscriptionStates(from i in items
				where i?.Artist != null
				select i.Artist);
			ApplyPodcastSubscriptionState(from i in items
				where i?.Podcast != null
				select i.Podcast);
		}
	}

	private bool IsSongLiked(SongInfo? song)
	{
		if (song == null)
		{
			return false;
		}
		if (song.IsLiked)
		{
			return true;
		}
		string text = ResolveSongIdForLibraryState(song);
		if (string.IsNullOrEmpty(text))
		{
			return false;
		}
		lock (_libraryStateLock)
		{
			if (_likedSongIds.Contains(text))
			{
				song.IsLiked = true;
				return true;
			}
		}
		return false;
	}

	private bool IsPlaylistSubscribed(PlaylistInfo? playlist)
	{
		if (playlist == null || string.IsNullOrWhiteSpace(playlist.Id))
		{
			return false;
		}
		if (IsPlaylistOwnedByUser(playlist, GetCurrentUserId()))
		{
			return false;
		}
		if (playlist.IsSubscribed)
		{
			return true;
		}
		lock (_libraryStateLock)
		{
			if (_subscribedPlaylistIds.Contains(playlist.Id))
			{
				playlist.IsSubscribed = true;
				return true;
			}
		}
		return false;
	}

	private bool IsAlbumSubscribed(AlbumInfo? album)
	{
		if (album == null || string.IsNullOrWhiteSpace(album.Id))
		{
			return false;
		}
		if (album.IsSubscribed)
		{
			return true;
		}
		lock (_libraryStateLock)
		{
			if (_subscribedAlbumIds.Contains(album.Id))
			{
				album.IsSubscribed = true;
				return true;
			}
		}
		return false;
	}

	private bool IsArtistSubscribed(ArtistInfo? artist)
	{
		if (artist == null || artist.Id <= 0)
		{
			return false;
		}
		if (artist.IsSubscribed)
		{
			return true;
		}
		lock (_libraryStateLock)
		{
			if (_subscribedArtistIds.Contains(artist.Id))
			{
				artist.IsSubscribed = true;
				return true;
			}
		}
		return false;
	}

	private void UpdateArtistSubscriptionState(long artistId, bool isSubscribed)
	{
		if (artistId <= 0)
		{
			return;
		}
		lock (_libraryStateLock)
		{
			if (isSubscribed)
			{
				_subscribedArtistIds.Add(artistId);
			}
			else
			{
				_subscribedArtistIds.Remove(artistId);
			}
		}
	}

	private static bool IsPlaylistOwnedByUser(PlaylistInfo? playlist, long userId)
	{
		if (playlist == null || userId <= 0)
		{
			return false;
		}
		if (playlist.CreatorId > 0 && playlist.CreatorId == userId)
		{
			return true;
		}
		if (playlist.OwnerUserId > 0 && playlist.OwnerUserId == userId)
		{
			return true;
		}
		return IsLikedMusicPlaylist(playlist, userId);
	}

	private void UpdateSongLikeState(SongInfo? song, bool isLiked)
	{
		if (song == null)
		{
			return;
		}
		song.IsLiked = isLiked;
		string text = ResolveSongIdForLibraryState(song);
		if (string.IsNullOrEmpty(text))
		{
			return;
		}
		lock (_libraryStateLock)
		{
			if (isLiked)
			{
				_likedSongIds.Add(text);
			}
			else
			{
				_likedSongIds.Remove(text);
			}
		}
	}

	private void UpdatePlaylistSubscriptionState(string? playlistId, bool isSubscribed)
	{
		if (string.IsNullOrWhiteSpace(playlistId))
		{
			return;
		}
		lock (_libraryStateLock)
		{
			if (isSubscribed)
			{
				_subscribedPlaylistIds.Add(playlistId);
			}
			else
			{
				_subscribedPlaylistIds.Remove(playlistId);
			}
		}
	}

	private void UpdatePlaylistOwnershipState(string? playlistId, bool isOwned)
	{
		if (string.IsNullOrWhiteSpace(playlistId))
		{
			return;
		}
		lock (_libraryStateLock)
		{
			if (isOwned)
			{
				_ownedPlaylistIds.Add(playlistId);
				_subscribedPlaylistIds.Remove(playlistId);
			}
			else
			{
				_ownedPlaylistIds.Remove(playlistId);
			}
		}
	}

	private void UpdateAlbumSubscriptionState(string? albumId, bool isSubscribed)
	{
		if (string.IsNullOrWhiteSpace(albumId))
		{
			return;
		}
		lock (_libraryStateLock)
		{
			if (isSubscribed)
			{
				_subscribedAlbumIds.Add(albumId);
			}
			else
			{
				_subscribedAlbumIds.Remove(albumId);
			}
		}
	}

	private void UpdatePodcastSubscriptionState(long podcastId, bool isSubscribed)
	{
		if (podcastId <= 0)
		{
			return;
		}
		lock (_libraryStateLock)
		{
			if (isSubscribed)
			{
				_subscribedPodcastIds.Add(podcastId);
			}
			else
			{
				_subscribedPodcastIds.Remove(podcastId);
			}
		}
	}


}
}
