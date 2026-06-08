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
	private async Task LoadUserLikedSongs(bool preserveSelection = false, bool skipSaveNavigation = false)
	{
		try
		{
			await EnsureLibraryStateFreshAsync(LibraryEntityType.Songs);
			int pendingIndex = ((preserveSelection && resultListView.SelectedIndices.Count > 0) ? resultListView.SelectedIndices[0] : 0);
			if (!skipSaveNavigation)
			{
				ResetPendingListFocusIfViewChanged("user_liked_songs");
			}
			DebugLogListFocusState($"LoadUserLikedSongs: start preserve={preserveSelection} skipSave={skipSaveNavigation} pendingIndex={pendingIndex}", "user_liked_songs");
			ViewLoadRequest request = new ViewLoadRequest("user_liked_songs", "喜欢的音乐", "正在加载喜欢的音乐...", !skipSaveNavigation, pendingIndex);
			ViewLoadResult<(PlaylistInfo Playlist, List<SongInfo> Songs, string StatusText)?> loadResult = await RunViewLoadAsync(request, (CancellationToken token) => BuildPlaylistSkeletonViewDataAsync(async delegate(CancellationToken ct)
			{
				ct.ThrowIfCancellationRequested();
				UserAccountInfo userInfo = await _apiClient.GetUserAccountAsync().ConfigureAwait(continueOnCapturedContext: false);
				ct.ThrowIfCancellationRequested();
				if (userInfo == null || userInfo.UserId <= 0)
				{
					throw new InvalidOperationException("请先登录有效账号。");
				}
				_loggedInUserId = userInfo.UserId;
				var (likedPlaylist, _) = await FetchUserPlaylistSummaryAsync(userInfo.UserId, ct).ConfigureAwait(continueOnCapturedContext: false);
				ct.ThrowIfCancellationRequested();
				if (likedPlaylist != null)
				{
					PlaylistInfo detail = await _apiClient.GetPlaylistDetailAsync(likedPlaylist.Id).ConfigureAwait(continueOnCapturedContext: false);
					ct.ThrowIfCancellationRequested();
					if (detail != null)
					{
						if (string.IsNullOrWhiteSpace(detail.Name))
						{
							detail.Name = likedPlaylist.Name;
						}
						likedPlaylist = detail;
					}
					_userLikedPlaylist = likedPlaylist;
				}
				return likedPlaylist;
			}, "user_liked_songs", token), "加载喜欢的音乐已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				(PlaylistInfo Playlist, List<SongInfo> Songs, string StatusText)? data = loadResult.Value;
				if (!data.HasValue || data.Value.Playlist == null)
				{
					MessageBox.Show("未找到喜欢的音乐歌单或歌单为空。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
					UpdateStatusBar("未找到喜欢的音乐");
					return;
				}
				(PlaylistInfo Playlist, List<SongInfo> Songs, string StatusText) viewData = data.Value;
				(_currentPlaylist, _, _) = viewData;
				if (HasSkeletonItems(viewData.Songs))
				{
					DisplaySongs(viewData.Songs, showPagination: false, hasNextPage: false, 1, preserveSelection, request.ViewSource, request.AccessibleName, skipAvailabilityCheck: true);
				}
				DebugLogListFocusState("LoadUserLikedSongs: after DisplaySongs", request.ViewSource);
				UpdateStatusBar(viewData.StatusText);
				EnrichCurrentPlaylistSongsAsync(viewData.Playlist, viewData.Songs, request.ViewSource, request.AccessibleName);
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Exception ex3 = ex2;
			if (!TryHandleOperationCancelled(ex3, "加载喜欢的音乐已取消"))
			{
				Debug.WriteLine($"[LoadUserLikedSongs] 异常: {ex3}");
				MessageBox.Show("加载喜欢的音乐失败: " + ex3.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("加载喜欢的音乐失败");
			}
		}
	}

	private async Task<(PlaylistInfo LikedPlaylist, int PlaylistCount)> FetchUserPlaylistSummaryAsync(long userId, CancellationToken token = default)
	{
		const int pageSize = 1000;
		int offset = 0;
		int reportedTotal = 0;
		int loadedCount = 0;
		PlaylistInfo likedPlaylist = null;
		while (true)
		{
			var (pageItems, totalCount) = await _apiClient.GetUserPlaylistsAsync(userId, pageSize, offset).ConfigureAwait(continueOnCapturedContext: false);
			token.ThrowIfCancellationRequested();
			List<PlaylistInfo> currentPage = pageItems ?? new List<PlaylistInfo>();
			if (currentPage.Count == 0)
			{
				break;
			}
			loadedCount = checked(loadedCount + currentPage.Count);
			reportedTotal = Math.Max(reportedTotal, totalCount);
			if (likedPlaylist == null)
			{
				likedPlaylist = currentPage.FirstOrDefault((PlaylistInfo p) => IsLikedMusicPlaylist(p, userId));
			}
			if (likedPlaylist != null && reportedTotal > 0)
			{
				break;
			}
			if (currentPage.Count < pageSize || (reportedTotal > 0 && loadedCount >= reportedTotal))
			{
				break;
			}
			offset = checked(offset + currentPage.Count);
		}
		int playlistCount = Math.Max(reportedTotal, loadedCount);
		if (likedPlaylist != null && playlistCount > 0)
		{
			playlistCount = Math.Max(0, playlistCount - 1);
		}
		return (likedPlaylist, playlistCount);
	}

	private async Task LoadUserPlaylists(bool preserveSelection = false)
	{
		try
		{
			await EnsureLibraryStateFreshAsync(LibraryEntityType.Playlists);
			int pendingIndex = ((preserveSelection && resultListView.SelectedIndices.Count > 0) ? resultListView.SelectedIndices[0] : 0);
			_currentUserPlaylistsNextOffset = 0;
			_currentUserPlaylistsHasMore = false;
			_currentUserPlaylistsTotalCount = 0;
			_userPlaylistsLoadMoreInFlight = false;
			_userPlaylistsProactiveLoadRunning = false;
			int proactiveGeneration = ++_userPlaylistsProactiveLoadGeneration;
			ViewLoadRequest request = new ViewLoadRequest("user_playlists", "创建和收藏的歌单", "正在加载创建和收藏的歌单...", cancelActiveNavigation: true, pendingIndex);
			ViewLoadResult<(List<PlaylistInfo> Items, int NextOffset, int TotalCount, bool HasMore, long UserId)?> loadResult = await RunViewLoadAsync(request, (Func<CancellationToken, Task<(List<PlaylistInfo>, int, int, bool, long)?>>)async delegate(CancellationToken token)
			{
				UserAccountInfo userInfo = await _apiClient.GetUserAccountAsync().ConfigureAwait(continueOnCapturedContext: false);
				token.ThrowIfCancellationRequested();
				if (userInfo == null || userInfo.UserId <= 0)
				{
					throw new InvalidOperationException("请先登录网易云账号。");
				}
				_loggedInUserId = userInfo.UserId;
				return await FetchUserPlaylistsPageAsync(userInfo.UserId, 0, token).ConfigureAwait(continueOnCapturedContext: false);
			}, "加载创建和收藏的歌单已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				(List<PlaylistInfo> Items, int NextOffset, int TotalCount, bool HasMore, long UserId)? data = loadResult.Value;
				if (data.HasValue)
				{
					_loggedInUserId = data.Value.UserId;
					_currentUserPlaylistsNextOffset = data.Value.NextOffset;
					_currentUserPlaylistsHasMore = data.Value.HasMore;
					_currentUserPlaylistsTotalCount = Math.Max(data.Value.TotalCount, data.Value.Items.Count);
				}
				if (!data.HasValue || data.Value.Items.Count == 0)
				{
					if (!data.HasValue || !data.Value.HasMore)
					{
						MessageBox.Show("您还没有创建或收藏歌单。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
						UpdateStatusBar("暂无歌单");
					}
					else
					{
						UpdateStatusBar(BuildUserPlaylistsStatusText(0, _currentUserPlaylistsTotalCount, _currentUserPlaylistsHasMore));
						StartUserPlaylistsProactiveExpansion(proactiveGeneration);
					}
				}
				else
				{
					DisplayPlaylists(data.Value.Items, preserveSelection, request.ViewSource, request.AccessibleName);
					_currentPlaylist = null;
					UpdateStatusBar(BuildUserPlaylistsStatusText(_currentPlaylists.Count, _currentUserPlaylistsTotalCount, _currentUserPlaylistsHasMore));
					StartUserPlaylistsProactiveExpansion(proactiveGeneration);
				}
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Exception ex3 = ex2;
			if (TryHandleOperationCancelled(ex3, "加载创建和收藏的歌单已取消"))
			{
				return;
			}
			Debug.WriteLine($"[LoadUserPlaylists] 异常: {ex3}");
			throw;
		}
	}

	private async Task<(List<PlaylistInfo> Items, int NextOffset, int TotalCount, bool HasMore, long UserId)> FetchUserPlaylistsPageAsync(long userId, int offset, CancellationToken token)
	{
		const int pageSize = 100;
		var (pageItems, totalCount) = await _apiClient.GetUserPlaylistsAsync(userId, pageSize, offset).ConfigureAwait(continueOnCapturedContext: false);
		token.ThrowIfCancellationRequested();
		List<PlaylistInfo> currentPage = pageItems ?? new List<PlaylistInfo>();
		List<PlaylistInfo> filtered = currentPage.Where((PlaylistInfo p) => !IsLikedMusicPlaylist(p, userId)).ToList();
		List<PlaylistInfo> normalized = NormalizeUserPlaylists(filtered);
		int nextOffset = checked(offset + currentPage.Count);
		bool hasMore = currentPage.Count >= pageSize && (totalCount <= 0 || nextOffset < totalCount);
		int displayTotal = totalCount > 0 ? Math.Max(0, totalCount - 1) : 0;
		displayTotal = Math.Max(displayTotal, normalized.Count);
		return (normalized, nextOffset, displayTotal, hasMore, userId);
	}

	private static string BuildUserPlaylistsStatusText(int loadedCount, int totalCount, bool hasMore)
	{
		if (loadedCount <= 0)
		{
			return hasMore ? "正在加载创建和收藏的歌单..." : "暂无歌单";
		}
		if (totalCount > loadedCount)
		{
			return $"创建和收藏的歌单：已加载 {loadedCount}/{totalCount} 个";
		}
		if (hasMore)
		{
			return $"创建和收藏的歌单：已加载 {loadedCount} 个，正在自动加载更多";
		}
		return $"创建和收藏的歌单：{loadedCount} 个";
	}

	private void StartUserPlaylistsProactiveExpansion(int generation)
	{
		if (!string.Equals(_currentViewSource, "user_playlists", StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		if (!_currentUserPlaylistsHasMore || _userPlaylistsProactiveLoadRunning)
		{
			return;
		}
		_userPlaylistsProactiveLoadRunning = true;
		LoadRemainingUserPlaylistsAsync(generation).SafeFireAndForget("user playlists proactive load");
	}

	private async Task LoadRemainingUserPlaylistsAsync(int generation)
	{
		try
		{
			while (generation == _userPlaylistsProactiveLoadGeneration &&
				string.Equals(_currentViewSource, "user_playlists", StringComparison.OrdinalIgnoreCase) &&
				_currentUserPlaylistsHasMore)
			{
				int beforeOffset = _currentUserPlaylistsNextOffset;
				bool loaded = await LoadMoreUserPlaylistsAsync().ConfigureAwait(continueOnCapturedContext: false);
				if (!loaded ||
					generation != _userPlaylistsProactiveLoadGeneration ||
					!string.Equals(_currentViewSource, "user_playlists", StringComparison.OrdinalIgnoreCase) ||
					!_currentUserPlaylistsHasMore ||
					_currentUserPlaylistsNextOffset <= beforeOffset)
				{
					break;
				}
				await Task.Yield();
			}
		}
		finally
		{
			if (generation == _userPlaylistsProactiveLoadGeneration)
			{
				_userPlaylistsProactiveLoadRunning = false;
			}
		}
	}

	private async Task<bool> LoadMoreUserPlaylistsAsync()
	{
		if (_userPlaylistsLoadMoreInFlight || !_currentUserPlaylistsHasMore)
		{
			return false;
		}
		long userId = GetCurrentUserId();
		if (userId <= 0)
		{
			return false;
		}
		_userPlaylistsLoadMoreInFlight = true;
		CancellationToken viewToken = GetCurrentViewContentToken();
		int offset = Math.Max(0, _currentUserPlaylistsNextOffset);
		bool applied = false;
		try
		{
			UpdateStatusBar("正在自动加载更多歌单...");
			var page = await FetchUserPlaylistsPageAsync(userId, offset, viewToken).ConfigureAwait(continueOnCapturedContext: false);
			List<PlaylistInfo> merged = CloneList(_currentPlaylists);
			HashSet<string> seen = new HashSet<string>(merged.Where((PlaylistInfo p) => p != null && !string.IsNullOrWhiteSpace(p.Id)).Select((PlaylistInfo p) => p.Id), StringComparer.OrdinalIgnoreCase);
			foreach (PlaylistInfo playlist in page.Items)
			{
				if (playlist == null)
				{
					continue;
				}
				if (!string.IsNullOrWhiteSpace(playlist.Id) && !seen.Add(playlist.Id))
				{
					continue;
				}
				merged.Add(playlist);
			}
			merged = NormalizeUserPlaylists(merged);
			await ExecuteOnUiThreadAsync(delegate
			{
				if (!string.Equals(_currentViewSource, "user_playlists", StringComparison.OrdinalIgnoreCase))
				{
					return;
				}
				_currentUserPlaylistsNextOffset = page.NextOffset;
				_currentUserPlaylistsHasMore = page.HasMore;
				_currentUserPlaylistsTotalCount = Math.Max(page.TotalCount, merged.Count);
				PatchPlaylists(merged, 1, showPagination: false, hasPreviousPage: false, hasNextPage: false, pendingFocusIndex: -1, allowSelection: false);
				_currentPlaylist = null;
				UpdateStatusBar(BuildUserPlaylistsStatusText(_currentPlaylists.Count, _currentUserPlaylistsTotalCount, _currentUserPlaylistsHasMore));
				applied = true;
			}).ConfigureAwait(continueOnCapturedContext: false);
			return applied;
		}
		catch (Exception ex)
		{
			if (!TryHandleOperationCancelled(ex, "加载创建和收藏的歌单已取消"))
			{
				Debug.WriteLine($"[LoadUserPlaylists] 自动加载更多失败: {ex}");
				await ExecuteOnUiThreadAsync(delegate
				{
					if (string.Equals(_currentViewSource, "user_playlists", StringComparison.OrdinalIgnoreCase))
					{
						UpdateStatusBar("加载更多歌单失败，请稍后刷新重试");
					}
				}).ConfigureAwait(continueOnCapturedContext: false);
			}
			return false;
		}
		finally
		{
			_userPlaylistsLoadMoreInFlight = false;
		}
	}

	private static bool IsLikedMusicPlaylist(PlaylistInfo? playlist, long userId)
	{
		if (playlist == null)
		{
			return false;
		}
		string b = userId.ToString();
		if (!string.IsNullOrWhiteSpace(playlist.Id) && string.Equals(playlist.Id, b, StringComparison.Ordinal))
		{
			return true;
		}
		if (!string.IsNullOrWhiteSpace(playlist.Name) && playlist.Name.IndexOf("喜欢的音乐", StringComparison.OrdinalIgnoreCase) >= 0 && (playlist.OwnerUserId == userId || playlist.CreatorId == userId))
		{
			return true;
		}
		return false;
	}

	private async Task LoadUserAlbums(bool preserveSelection = false)
	{
		try
		{
			await EnsureLibraryStateFreshAsync(LibraryEntityType.Albums);
			int pendingIndex = ((preserveSelection && resultListView.SelectedIndices.Count > 0) ? resultListView.SelectedIndices[0] : 0);
			_currentUserAlbumsNextOffset = 0;
			_currentUserAlbumsHasMore = false;
			_currentUserAlbumsTotalCount = 0;
			_userAlbumsLoadMoreInFlight = false;
			_userAlbumsProactiveLoadRunning = false;
			int proactiveGeneration = ++_userAlbumsProactiveLoadGeneration;
			ViewLoadRequest request = new ViewLoadRequest("user_albums", "收藏的专辑", "正在加载收藏的专辑...", cancelActiveNavigation: true, pendingIndex);
			ViewLoadResult<(List<AlbumInfo> Items, int NextOffset, int TotalCount, bool HasMore)?> loadResult = await RunViewLoadAsync(request, (Func<CancellationToken, Task<(List<AlbumInfo>, int, int, bool)?>>)async delegate(CancellationToken token)
			{
				return await FetchUserAlbumsPageAsync(0, token).ConfigureAwait(continueOnCapturedContext: false);
			}, "加载收藏的专辑已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				(List<AlbumInfo> Items, int NextOffset, int TotalCount, bool HasMore)? data = loadResult.Value;
				if (data.HasValue)
				{
					_currentUserAlbumsNextOffset = data.Value.NextOffset;
					_currentUserAlbumsHasMore = data.Value.HasMore;
					_currentUserAlbumsTotalCount = Math.Max(data.Value.TotalCount, data.Value.Items.Count);
				}
				if (!data.HasValue || data.Value.Items.Count == 0)
				{
					if (!data.HasValue || !data.Value.HasMore)
					{
						MessageBox.Show("您还没有收藏专辑。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
						UpdateStatusBar("暂无收藏的专辑");
					}
					else
					{
						UpdateStatusBar(BuildUserAlbumsStatusText(0, _currentUserAlbumsTotalCount, _currentUserAlbumsHasMore));
						StartUserAlbumsProactiveExpansion(proactiveGeneration);
					}
				}
				else
				{
					DisplayAlbums(data.Value.Items, preserveSelection, request.ViewSource, request.AccessibleName);
					UpdateStatusBar(BuildUserAlbumsStatusText(_currentAlbums.Count, _currentUserAlbumsTotalCount, _currentUserAlbumsHasMore));
					StartUserAlbumsProactiveExpansion(proactiveGeneration);
				}
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Exception ex3 = ex2;
			if (TryHandleOperationCancelled(ex3, "加载收藏的专辑已取消"))
			{
				return;
			}
			Debug.WriteLine($"[LoadUserAlbums] 异常: {ex3}");
			throw;
		}
	}

	private async Task<(List<AlbumInfo> Items, int NextOffset, int TotalCount, bool HasMore)> FetchUserAlbumsPageAsync(int offset, CancellationToken token)
	{
		const int pageSize = 100;
		var (albums, totalCount) = await _apiClient.GetUserAlbumsAsync(pageSize, offset).ConfigureAwait(continueOnCapturedContext: false);
		token.ThrowIfCancellationRequested();
		List<AlbumInfo> currentPage = albums ?? new List<AlbumInfo>();
		int nextOffset = checked(offset + currentPage.Count);
		bool hasMore = currentPage.Count >= pageSize && (totalCount <= 0 || nextOffset < totalCount);
		int displayTotal = Math.Max(totalCount, nextOffset);
		return (currentPage, nextOffset, displayTotal, hasMore);
	}

	private static string BuildUserAlbumsStatusText(int loadedCount, int totalCount, bool hasMore)
	{
		if (loadedCount <= 0)
		{
			return hasMore ? "正在加载收藏的专辑..." : "暂无收藏的专辑";
		}
		if (totalCount > loadedCount)
		{
			return $"收藏的专辑：已加载 {loadedCount}/{totalCount} 张";
		}
		if (hasMore)
		{
			return $"收藏的专辑：已加载 {loadedCount} 张，正在自动加载更多";
		}
		return $"收藏的专辑：{loadedCount} 张";
	}

	private void StartUserAlbumsProactiveExpansion(int generation)
	{
		if (!string.Equals(_currentViewSource, "user_albums", StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		if (!_currentUserAlbumsHasMore || _userAlbumsProactiveLoadRunning)
		{
			return;
		}
		_userAlbumsProactiveLoadRunning = true;
		LoadRemainingUserAlbumsAsync(generation).SafeFireAndForget("user albums proactive load");
	}

	private async Task LoadRemainingUserAlbumsAsync(int generation)
	{
		try
		{
			while (generation == _userAlbumsProactiveLoadGeneration &&
				string.Equals(_currentViewSource, "user_albums", StringComparison.OrdinalIgnoreCase) &&
				_currentUserAlbumsHasMore)
			{
				int beforeOffset = _currentUserAlbumsNextOffset;
				bool loaded = await LoadMoreUserAlbumsAsync().ConfigureAwait(continueOnCapturedContext: false);
				if (!loaded ||
					generation != _userAlbumsProactiveLoadGeneration ||
					!string.Equals(_currentViewSource, "user_albums", StringComparison.OrdinalIgnoreCase) ||
					!_currentUserAlbumsHasMore ||
					_currentUserAlbumsNextOffset <= beforeOffset)
				{
					break;
				}
				await Task.Yield();
			}
		}
		finally
		{
			if (generation == _userAlbumsProactiveLoadGeneration)
			{
				_userAlbumsProactiveLoadRunning = false;
			}
		}
	}

	private async Task<bool> LoadMoreUserAlbumsAsync()
	{
		if (_userAlbumsLoadMoreInFlight || !_currentUserAlbumsHasMore)
		{
			return false;
		}
		_userAlbumsLoadMoreInFlight = true;
		CancellationToken viewToken = GetCurrentViewContentToken();
		int offset = Math.Max(0, _currentUserAlbumsNextOffset);
		bool applied = false;
		try
		{
			UpdateStatusBar("正在自动加载更多收藏专辑...");
			var page = await FetchUserAlbumsPageAsync(offset, viewToken).ConfigureAwait(continueOnCapturedContext: false);
			List<AlbumInfo> merged = CloneList(_currentAlbums);
			HashSet<string> seen = new HashSet<string>(merged.Where((AlbumInfo a) => a != null && !string.IsNullOrWhiteSpace(a.Id)).Select((AlbumInfo a) => a.Id), StringComparer.OrdinalIgnoreCase);
			foreach (AlbumInfo album in page.Items)
			{
				if (album == null)
				{
					continue;
				}
				if (!string.IsNullOrWhiteSpace(album.Id) && !seen.Add(album.Id))
				{
					continue;
				}
				merged.Add(album);
			}
			await ExecuteOnUiThreadAsync(delegate
			{
				if (!string.Equals(_currentViewSource, "user_albums", StringComparison.OrdinalIgnoreCase))
				{
					return;
				}
				_currentUserAlbumsNextOffset = page.NextOffset;
				_currentUserAlbumsHasMore = page.HasMore;
				_currentUserAlbumsTotalCount = Math.Max(page.TotalCount, merged.Count);
				PatchAlbums(merged, 1, showPagination: false, hasPreviousPage: false, hasNextPage: false, pendingFocusIndex: -1, allowSelection: false);
				UpdateStatusBar(BuildUserAlbumsStatusText(_currentAlbums.Count, _currentUserAlbumsTotalCount, _currentUserAlbumsHasMore));
				applied = true;
			}).ConfigureAwait(continueOnCapturedContext: false);
			return applied;
		}
		catch (Exception ex)
		{
			if (!TryHandleOperationCancelled(ex, "加载收藏的专辑已取消"))
			{
				Debug.WriteLine($"[LoadUserAlbums] 自动加载更多失败: {ex}");
				await ExecuteOnUiThreadAsync(delegate
				{
					if (string.Equals(_currentViewSource, "user_albums", StringComparison.OrdinalIgnoreCase))
					{
						UpdateStatusBar("加载更多收藏专辑失败，请稍后刷新重试");
					}
				}).ConfigureAwait(continueOnCapturedContext: false);
			}
			return false;
		}
		finally
		{
			_userAlbumsLoadMoreInFlight = false;
		}
	}

}
}
