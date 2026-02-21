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
				if (_userLikedPlaylist != null && _userLikedPlaylist.Songs != null && _userLikedPlaylist.Songs.Count > 0)
				{
					return _userLikedPlaylist;
				}
				UserAccountInfo userInfo = await _apiClient.GetUserAccountAsync().ConfigureAwait(continueOnCapturedContext: false);
				ct.ThrowIfCancellationRequested();
				if (userInfo == null || userInfo.UserId <= 0)
				{
					throw new InvalidOperationException("请先登录有效账号。");
				}
				_loggedInUserId = userInfo.UserId;
				var (playlists, _) = await _apiClient.GetUserPlaylistsAsync(userInfo.UserId).ConfigureAwait(continueOnCapturedContext: false);
				ct.ThrowIfCancellationRequested();
				PlaylistInfo likedPlaylist = playlists?.FirstOrDefault((PlaylistInfo p) => IsLikedMusicPlaylist(p, userInfo.UserId));
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

	private async Task LoadUserPlaylists(bool preserveSelection = false)
	{
		try
		{
			await EnsureLibraryStateFreshAsync(LibraryEntityType.Playlists);
			int pendingIndex = ((preserveSelection && resultListView.SelectedIndices.Count > 0) ? resultListView.SelectedIndices[0] : 0);
			ViewLoadRequest request = new ViewLoadRequest("user_playlists", "创建和收藏的歌单", "正在加载创建和收藏的歌单...", cancelActiveNavigation: true, pendingIndex);
			ViewLoadResult<(List<PlaylistInfo> Items, string StatusText)?> loadResult = await RunViewLoadAsync(request, (Func<CancellationToken, Task<(List<PlaylistInfo>, string)?>>)async delegate(CancellationToken token)
			{
				UserAccountInfo userInfo = await _apiClient.GetUserAccountAsync().ConfigureAwait(continueOnCapturedContext: false);
				token.ThrowIfCancellationRequested();
				if (userInfo == null || userInfo.UserId <= 0)
				{
					throw new InvalidOperationException("请先登录网易云账号。");
				}
				_loggedInUserId = userInfo.UserId;
				var (playlists, _) = await _apiClient.GetUserPlaylistsAsync(userInfo.UserId).ConfigureAwait(continueOnCapturedContext: false);
				token.ThrowIfCancellationRequested();
				List<PlaylistInfo> filtered = (playlists ?? new List<PlaylistInfo>()).Where((PlaylistInfo p) => !IsLikedMusicPlaylist(p, userInfo.UserId)).ToList();
				List<PlaylistInfo> normalized = NormalizeUserPlaylists(filtered);
				string status = ((normalized.Count == 0) ? "暂无歌单" : $"加载完成，共 {normalized.Count} 个歌单");
				return (normalized, status);
			}, "加载创建和收藏的歌单已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				(List<PlaylistInfo> Items, string StatusText)? data = loadResult.Value;
				if (!data.HasValue || data.Value.Items.Count == 0)
				{
					MessageBox.Show("您还没有创建或收藏歌单。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
					UpdateStatusBar("暂无歌单");
				}
				else
				{
					DisplayPlaylists(data.Value.Items, preserveSelection, request.ViewSource, request.AccessibleName);
					_currentPlaylist = null;
					UpdateStatusBar(data.Value.StatusText);
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
			ViewLoadRequest request = new ViewLoadRequest("user_albums", "收藏的专辑", "正在加载收藏的专辑...", cancelActiveNavigation: true, pendingIndex);
			ViewLoadResult<(List<AlbumInfo> Items, string StatusText)?> loadResult = await RunViewLoadAsync(request, (Func<CancellationToken, Task<(List<AlbumInfo>, string)?>>)async delegate(CancellationToken token)
			{
				var (albums, totalCount) = await _apiClient.GetUserAlbumsAsync().ConfigureAwait(continueOnCapturedContext: false);
				token.ThrowIfCancellationRequested();
				List<AlbumInfo> normalized = albums ?? new List<AlbumInfo>();
				string status = ((normalized.Count == 0) ? "暂无收藏的专辑" : $"加载完成，共 {totalCount} 个专辑");
				return (normalized, status);
			}, "加载收藏的专辑已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				(List<AlbumInfo> Items, string StatusText)? data = loadResult.Value;
				if (!data.HasValue || data.Value.Items.Count == 0)
				{
					MessageBox.Show("您还没有收藏专辑。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
					UpdateStatusBar("暂无收藏的专辑");
				}
				else
				{
					DisplayAlbums(data.Value.Items, preserveSelection, request.ViewSource, request.AccessibleName);
					UpdateStatusBar(data.Value.StatusText);
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

}
}
