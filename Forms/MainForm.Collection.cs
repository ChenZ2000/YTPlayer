using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using YTPlayer.Core;
using YTPlayer.Forms;
using YTPlayer.Models;

namespace YTPlayer
{
    /// <summary>
    /// MainForm 的歌曲收藏和歌单管理功能
    /// </summary>
    public partial class MainForm
    {
        #region 歌曲收藏功能

        /// <summary>
        /// 喜欢歌曲（收藏到"我喜欢的音乐"）
        /// </summary>
        private async void likeSongMenuItem_Click(object sender, EventArgs e)
        {
            // 检查登录状态
            if (!IsUserLoggedIn())
            {
                MessageBox.Show("请先登录后再收藏歌曲", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var song = GetSelectedSongFromContextMenu(sender);
            if (song == null)
            {
                ShowContextSongMissingMessage("收藏的歌曲");
                return;
            }

            if (!TryResolveSongIdForLibraryActions(song, "收藏歌曲", out var targetSongId))
            {
                return;
            }

            try
            {
                UpdateStatusBar("正在收藏歌曲...");
                bool success = await _apiClient.LikeSongAsync(targetSongId, true);

                if (success)
                {
                    UpdateSongLikeState(song, true);
                    MessageBox.Show($"已收藏歌曲：{song.Name} - {song.Artist}", "成功",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    UpdateStatusBar("歌曲收藏成功");
                }
                else
                {
                    MessageBox.Show("收藏歌曲失败，请检查网络或稍后重试。", "失败",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    UpdateStatusBar("歌曲收藏失败");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"收藏歌曲失败: {ex.Message}", "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatusBar("歌曲收藏失败");
            }
        }

        /// <summary>
        /// 取消喜欢歌曲（从"我喜欢的音乐"移除）
        /// </summary>
        private async void unlikeSongMenuItem_Click(object sender, EventArgs e)
        {
            await UnlikeSelectedSongAsync(sender);
        }

        private async Task<bool> UnlikeSelectedSongAsync(object? sender = null)
        {
            if (!IsUserLoggedIn())
            {
                MessageBox.Show("请先登录", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }

            var song = GetSelectedSongFromContextMenu(sender);
            if (song == null)
            {
                ShowContextSongMissingMessage("取消收藏的歌曲");
                return false;
            }

            if (!TryResolveSongIdForLibraryActions(song, "取消收藏歌曲", out var targetSongId))
            {
                return false;
            }

            try
            {
                UpdateStatusBar("正在取消收藏...");
                bool success = await _apiClient.LikeSongAsync(targetSongId, false);

                if (success)
                {
                    UpdateSongLikeState(song, false);
                    MessageBox.Show($"已取消收藏：{song.Name} - {song.Artist}", "成功",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    UpdateStatusBar("取消收藏成功");

                    if (IsCurrentLikedSongsView())
                    {
                        try
                        {
                            await LoadUserLikedSongs(
                                preserveSelection: true,
                                skipSaveNavigation: true);
                        }
                        catch (Exception refreshEx)
                        {
                            System.Diagnostics.Debug.WriteLine($"[UI] 刷新我喜欢的音乐列表失败: {refreshEx}");
                        }
                    }

                    return true;
                }

                MessageBox.Show("取消收藏失败，请检查网络或稍后重试。", "失败",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatusBar("取消收藏失败");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"取消收藏失败: {ex.Message}", "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatusBar("取消收藏失败");
            }

            return false;
        }

        #endregion

        #region 从歌单中移除功能

        /// <summary>
        /// 从歌单中移除歌曲
        /// </summary>
        private async void removeFromPlaylistMenuItem_Click(object sender, EventArgs e)
        {
            if (IsCurrentLikedSongsView())
            {
                await UnlikeSelectedSongAsync(sender);
                return;
            }

            // 检查登录状态
            if (!IsUserLoggedIn())
            {
                MessageBox.Show("请先登录", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // 获取当前歌单ID
            string? playlistId = _currentPlaylist?.Id;
            if (string.IsNullOrEmpty(playlistId) && _currentViewSource.StartsWith("playlist:"))
            {
                playlistId = _currentViewSource.Substring("playlist:".Length);
            }

            if (string.IsNullOrEmpty(playlistId))
            {
                MessageBox.Show("无法获取歌单信息", "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            string playlistIdValue = playlistId!;

            var selectedItem = resultListView.SelectedItems.Count > 0 ? resultListView.SelectedItems[0] : null;
            if (selectedItem == null) return;

            // 兼容逻辑：Tag 可能是 int 索引或 SongInfo 对象
            SongInfo? song = null;
            if (selectedItem.Tag is int index && index >= 0 && index < _currentSongs.Count)
            {
                song = _currentSongs[index];
            }
            else if (selectedItem.Tag is SongInfo songInfo)
            {
                song = songInfo;
            }

            if (song != null)
            {
                if (!TryResolveSongIdForLibraryActions(song, "从歌单中移除歌曲", out var targetSongId))
                {
                    return;
                }

                try
                {
                    UpdateStatusBar("正在从歌单中移除歌曲...");
                    bool success = await _apiClient.RemoveTracksFromPlaylistAsync(playlistIdValue, new[] { targetSongId });

                    if (success)
                    {
                        MessageBox.Show($"已将歌曲 \"{song.Name}\" 从歌单中移除", "成功",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                        UpdateStatusBar("移除成功");

                        // 刷新歌单列表并保持焦点
                        if (_currentPlaylist != null)
                        {
                            try
                            {
                                await OpenPlaylist(_currentPlaylist, skipSave: true, preserveSelection: true);
                            }
                            catch (Exception refreshEx)
                            {
                                System.Diagnostics.Debug.WriteLine($"[UI] 刷新歌单失败: {refreshEx}");
                            }
                        }
                    }
                    else
                    {
                        MessageBox.Show("从歌单中移除歌曲失败，请稍后重试。", "失败",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        UpdateStatusBar("移除失败");
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"从歌单中移除歌曲失败: {ex.Message}", "错误",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    UpdateStatusBar("移除失败");
                }
            }
        }

        #endregion

        #region 添加到歌单功能

        /// <summary>
        /// 添加歌曲到歌单
        /// </summary>
        private async void addToPlaylistMenuItem_Click(object sender, EventArgs e)
        {
            // 检查登录状态
            if (!IsUserLoggedIn())
            {
                MessageBox.Show("请先登录后再添加歌曲到歌单", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var song = GetSelectedSongFromContextMenu(sender);
            if (song == null)
            {
                ShowContextSongMissingMessage("添加到歌单的歌曲");
                return;
            }

            if (!TryResolveSongIdForLibraryActions(song, "添加到歌单", out var targetSongId))
            {
                return;
            }

            try
            {
                // 获取用户信息
                var userInfo = await _apiClient.GetUserInfoAsync();
                if (userInfo == null)
                {
                    MessageBox.Show("获取用户信息失败", "错误",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                long userId = long.Parse(userInfo.UserId);

                // 打开歌单选择对话框
                using (var dialog = new AddToPlaylistDialog(_apiClient, targetSongId, userId))
                {
                    if (dialog.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(dialog.SelectedPlaylistId))
                    {
                        // 添加歌曲到选中的歌单
                        UpdateStatusBar("正在添加歌曲到歌单...");
                        string targetPlaylistId = dialog.SelectedPlaylistId!;
                        bool success = await _apiClient.AddTracksToPlaylistAsync(targetPlaylistId, new[] { targetSongId });

                        if (success)
                        {
                            MessageBox.Show($"已将歌曲 \"{song.Name}\" 添加到歌单", "成功",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                            UpdateStatusBar("添加成功");
                        }
                        else
                        {
                            MessageBox.Show("添加歌曲到歌单失败，请稍后重试。", "失败",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                            UpdateStatusBar("添加失败");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"添加歌曲到歌单失败: {ex.Message}", "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatusBar("添加失败");
            }
        }

        #endregion

        private bool TryResolveSongIdForLibraryActions(SongInfo? song, string actionDescription, out string resolvedSongId)
        {
            resolvedSongId = string.Empty;

            if (!CanSongUseLibraryFeatures(song))
            {
                if (song?.IsCloudSong == true)
                {
                    MessageBox.Show($"当前云盘歌曲尚未匹配到网易云曲库，无法执行“{actionDescription}”。",
                        "暂不支持", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    MessageBox.Show($"无法执行“{actionDescription}”，歌曲缺少必要的ID信息。",
                        "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }

                return false;
            }

            if (song == null)
            {
                return false;
            }

            resolvedSongId = song.IsCloudSong && !string.IsNullOrWhiteSpace(song.CloudMatchedSongId)
                ? song.CloudMatchedSongId!
                : song.Id;

            return !string.IsNullOrWhiteSpace(resolvedSongId);
        }

        private string? ResolveSongIdForLibraryState(SongInfo? song)
        {
            if (!CanSongUseLibraryFeatures(song) || song == null)
            {
                return null;
            }

            return song.IsCloudSong && !string.IsNullOrWhiteSpace(song.CloudMatchedSongId)
                ? song.CloudMatchedSongId
                : song.Id;
        }

        private static bool CanSongUseLibraryFeatures(SongInfo? song)
        {
            if (song == null)
            {
                return false;
            }

            if (!song.IsCloudSong)
            {
                return !string.IsNullOrWhiteSpace(song.Id);
            }

            if (!string.IsNullOrWhiteSpace(song.CloudMatchedSongId))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(song.Id) || string.IsNullOrWhiteSpace(song.CloudSongId))
            {
                return false;
            }

            return !string.Equals(song.Id, song.CloudSongId, StringComparison.Ordinal);
        }
    }
}
