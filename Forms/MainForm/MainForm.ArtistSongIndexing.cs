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
using YTPlayer.Models;
#pragma warning disable CS0219, CS8600, CS8601, CS8602, CS8603, CS8604, CS8618, CS8622, CS8625, CS8632, CS4014

namespace YTPlayer
{
public partial class MainForm
{
	private ArtistSongIndexCache GetOrCreateArtistSongIndex(string key)
	{
		bool added = false;
		ArtistSongIndexCache cache;
		lock (_artistSongCacheLock)
		{
			if (!_artistSongIndexCache.TryGetValue(key, out cache))
			{
				cache = new ArtistSongIndexCache();
				_artistSongIndexCache[key] = cache;
				added = true;
			}
			cache.LastAccessUtc = DateTime.UtcNow;
		}
		if (added)
		{
			TrimArtistSongIndexCache();
		}
		return cache;
	}

	private SemaphoreSlim GetArtistSongIndexLock(string key)
	{
		lock (_artistSongCacheLock)
		{
			if (!_artistSongIndexLocks.TryGetValue(key, out var gate))
			{
				gate = new SemaphoreSlim(1, 1);
				_artistSongIndexLocks[key] = gate;
			}
			return gate;
		}
	}

	private SemaphoreSlim GetAlbumSongsLock(string albumId)
	{
		lock (_artistSongCacheLock)
		{
			if (!_albumSongsLocks.TryGetValue(albumId, out var gate))
			{
				gate = new SemaphoreSlim(1, 1);
				_albumSongsLocks[albumId] = gate;
			}
			return gate;
		}
	}

	private void TrimArtistSongIndexCache()
	{
		lock (_artistSongCacheLock)
		{
			if (_artistSongIndexCache.Count <= ArtistSongIndexCacheLimit)
			{
				return;
			}
			var oldest = _artistSongIndexCache.OrderBy(kv => kv.Value.LastAccessUtc).FirstOrDefault();
			if (!string.IsNullOrWhiteSpace(oldest.Key))
			{
				_artistSongIndexCache.Remove(oldest.Key);
				_artistSongIndexLocks.Remove(oldest.Key);
			}
		}
	}

	private void TrimAlbumSongsCache()
	{
		lock (_artistSongCacheLock)
		{
			if (_albumSongsCache.Count <= AlbumSongsCacheLimit)
			{
				return;
			}
			int removeCount = _albumSongsCache.Count - AlbumSongsCacheLimit;
			var removeKeys = _albumSongsCache.Keys.Take(removeCount).ToList();
			foreach (var key in removeKeys)
			{
				_albumSongsCache.Remove(key);
				_albumSongsLocks.Remove(key);
			}
		}
	}

	private int GetArtistSongsTotalCount(string key)
	{
		lock (_artistSongCacheLock)
		{
			if (_artistSongsTotalCountCache.TryGetValue(key, out int total))
			{
				return total;
			}
		}
		return 0;
	}

	private void SetArtistSongsTotalCount(string key, int total)
	{
		if (string.IsNullOrWhiteSpace(key) || total <= 0)
		{
			return;
		}
		lock (_artistSongCacheLock)
		{
			_artistSongsTotalCountCache[key] = total;
		}
	}

	private async Task<int> EnsureArtistSongsTotalCountAsync(long artistId, string orderToken, CancellationToken token)
	{
		string key = BuildArtistSongsPaginationKey(artistId, orderToken);
		int cachedTotal = GetArtistSongsTotalCount(key);
		if (cachedTotal > 0 && !string.Equals(orderToken, "time", StringComparison.OrdinalIgnoreCase))
		{
			return cachedTotal;
		}
		int resolvedTotal = cachedTotal;
		try
		{
			(List<SongInfo> Songs, bool HasMore, int TotalCount) result = await _apiClient.GetArtistSongsAsync(artistId, 1, 0, orderToken).ConfigureAwait(continueOnCapturedContext: false);
			token.ThrowIfCancellationRequested();
			if (result.TotalCount > 0)
			{
				resolvedTotal = Math.Max(resolvedTotal, result.TotalCount);
			}
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"[ArtistSongs] 获取歌曲总数失败: {ex.Message}");
		}
		try
		{
			ArtistDetail detail = await _apiClient.GetArtistDetailAsync(artistId, includeIntroduction: false).ConfigureAwait(continueOnCapturedContext: false);
			int total = detail?.MusicCount ?? 0;
			if (total > 0)
			{
				resolvedTotal = Math.Max(resolvedTotal, total);
			}
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"[ArtistSongs] 获取歌手统计失败: {ex.Message}");
		}
		if (resolvedTotal > 0)
		{
			SetArtistSongsTotalCount(key, resolvedTotal);
		}
		return resolvedTotal;
	}

	private async Task<List<AlbumInfo>> LoadArtistAlbumsOrderedAsync(long artistId, bool newestFirst, CancellationToken token)
	{
		List<AlbumInfo> albums = await LoadArtistAlbumsAscendingListAsync(artistId).ConfigureAwait(continueOnCapturedContext: false);
		token.ThrowIfCancellationRequested();
		if (albums == null)
		{
			return new List<AlbumInfo>();
		}
		if (newestFirst)
		{
			albums = albums.AsEnumerable().Reverse().ToList();
		}
		return albums;
	}

	private async Task<List<SongInfo>> GetAlbumSongsCachedAsync(string albumId, CancellationToken token)
	{
		if (string.IsNullOrWhiteSpace(albumId))
		{
			return new List<SongInfo>();
		}
		lock (_artistSongCacheLock)
		{
			if (_albumSongsCache.TryGetValue(albumId, out var cached))
			{
				return cached;
			}
		}
		SemaphoreSlim gate = GetAlbumSongsLock(albumId);
		await gate.WaitAsync(token).ConfigureAwait(continueOnCapturedContext: false);
		try
		{
			lock (_artistSongCacheLock)
			{
				if (_albumSongsCache.TryGetValue(albumId, out var cached))
				{
					return cached;
				}
			}
			List<SongInfo> songs = new List<SongInfo>();
			try
			{
				songs = await FetchWithRetryUntilCancel((CancellationToken ct) => _apiClient.GetAlbumSongsAsync(albumId), $"album_songs:{albumId}", token, maxAttempts: 3).ConfigureAwait(continueOnCapturedContext: false);
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"[ArtistSongs] 获取专辑歌曲失败: album={albumId}, err={ex.Message}");
			}
			songs ??= new List<SongInfo>();
			lock (_artistSongCacheLock)
			{
				_albumSongsCache[albumId] = songs;
			}
			TrimAlbumSongsCache();
			return songs;
		}
		finally
		{
			gate.Release();
		}
	}

	private async Task<ArtistSongIndexCache> EnsureArtistSongIndexAsync(string cacheKey, long artistId, bool newestFirst, int requiredCount, CancellationToken token, string artistName)
	{
		ArtistSongIndexCache cache = GetOrCreateArtistSongIndex(cacheKey);
		if (cache.Songs.Count >= requiredCount)
		{
			return cache;
		}
		SemaphoreSlim gate = GetArtistSongIndexLock(cacheKey);
		await gate.WaitAsync(token).ConfigureAwait(continueOnCapturedContext: false);
		try
		{
			cache = GetOrCreateArtistSongIndex(cacheKey);
			if (cache.Albums == null || cache.Albums.Count == 0)
			{
				cache.Albums = await LoadArtistAlbumsOrderedAsync(artistId, newestFirst, token).ConfigureAwait(continueOnCapturedContext: false);
				cache.AlbumCursor = 0;
				cache.IsComplete = cache.Albums.Count == 0;
			}
			int totalAlbums = cache.Albums.Count;
			while (cache.Songs.Count < requiredCount && cache.AlbumCursor < totalAlbums)
			{
				token.ThrowIfCancellationRequested();
				int remaining = totalAlbums - cache.AlbumCursor;
				int batchSize = Math.Min(ArtistSongsAlbumFetchConcurrency, remaining);
				List<AlbumInfo> batchAlbums = new List<AlbumInfo>(batchSize);
				for (int i = 0; i < batchSize; i++)
				{
					AlbumInfo album = cache.Albums[cache.AlbumCursor];
					cache.AlbumCursor = checked(cache.AlbumCursor + 1);
					if (album != null && !string.IsNullOrWhiteSpace(album.Id))
					{
						batchAlbums.Add(album);
					}
				}
				if (batchAlbums.Count == 0)
				{
					continue;
				}
				if (cache.AlbumCursor % 8 == 0 || cache.AlbumCursor >= totalAlbums)
				{
					SafeInvoke(delegate
					{
						UpdateStatusBar($"正在整理 {artistName} 的歌曲... {cache.AlbumCursor}/{totalAlbums}");
					});
				}
				var fetchTasks = batchAlbums
					.Select(a => GetAlbumSongsCachedAsync(a.Id!, token))
					.ToArray();
				List<SongInfo>[] batchResults = await Task.WhenAll(fetchTasks).ConfigureAwait(continueOnCapturedContext: false);
				for (int i = 0; i < batchAlbums.Count; i++)
				{
					List<SongInfo> albumSongs = batchResults[i];
					if (albumSongs == null || albumSongs.Count == 0)
					{
						continue;
					}
					foreach (SongInfo song in albumSongs)
					{
						if (song == null || string.IsNullOrWhiteSpace(song.Id))
						{
							continue;
						}
						if (cache.SeenIds.Add(song.Id))
						{
							cache.Songs.Add(song);
						}
					}
				}
			}
			cache.IsComplete = cache.AlbumCursor >= totalAlbums;
			cache.LastAccessUtc = DateTime.UtcNow;
			return cache;
		}
		finally
		{
			gate.Release();
		}
	}


}
}
