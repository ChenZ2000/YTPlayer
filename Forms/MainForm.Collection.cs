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
                try
                {
                    UpdateStatusBar("正在收藏歌曲...");
                    bool success = await _apiClient.LikeSongAsync(song.Id, true);

                    if (success)
                    {
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
        }

        /// <summary>
        /// 取消喜欢歌曲（从"我喜欢的音乐"移除）
        /// </summary>
        private async void unlikeSongMenuItem_Click(object sender, EventArgs e)
        {
            // 检查登录状态
            if (!IsUserLoggedIn())
            {
                MessageBox.Show("请先登录", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

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
                try
                {
                    UpdateStatusBar("正在取消收藏...");
                    bool success = await _apiClient.LikeSongAsync(song.Id, false);

                    if (success)
                    {
                        MessageBox.Show($"已取消收藏：{song.Name} - {song.Artist}", "成功",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                        UpdateStatusBar("取消收藏成功");

                        // 如果当前在"我喜欢的音乐"列表中，刷新列表并保持焦点
                        if (string.Equals(_currentViewSource, "user_liked_songs", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                await LoadUserLikedSongs(preserveSelection: true);
                            }
                            catch (Exception refreshEx)
                            {
                                System.Diagnostics.Debug.WriteLine($"[UI] 刷新我喜欢的音乐列表失败: {refreshEx}");
                            }
                        }
                    }
                    else
                    {
                        MessageBox.Show("取消收藏失败，请检查网络或稍后重试。", "失败",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        UpdateStatusBar("取消收藏失败");
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"取消收藏失败: {ex.Message}", "错误",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    UpdateStatusBar("取消收藏失败");
                }
            }
        }

        #endregion

        #region 从歌单中移除功能

        /// <summary>
        /// 从歌单中移除歌曲
        /// </summary>
        private async void removeFromPlaylistMenuItem_Click(object sender, EventArgs e)
        {
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
                try
                {
                    UpdateStatusBar("正在从歌单中移除歌曲...");
                    bool success = await _apiClient.RemoveTracksFromPlaylistAsync(playlistIdValue, new[] { song.Id });

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
                    using (var dialog = new AddToPlaylistDialog(_apiClient, song.Id, userId))
                    {
                        if (dialog.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(dialog.SelectedPlaylistId))
                        {
                            // 添加歌曲到选中的歌单
                            UpdateStatusBar("正在添加歌曲到歌单...");
                            string targetPlaylistId = dialog.SelectedPlaylistId!;
                            bool success = await _apiClient.AddTracksToPlaylistAsync(targetPlaylistId, new[] { song.Id });

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
        }

        #endregion
    }
}
