using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using YTPlayer.Forms;
using YTPlayer.Models;
using YTPlayer.Models.Upload;

namespace YTPlayer
{
    public partial class MainForm
    {
        private async Task LoadCloudSongsAsync(bool skipSave = false, bool preserveSelection = false)
        {
            if (_apiClient == null)
            {
                return;
            }

            if (!IsUserLoggedIn())
            {
                MessageBox.Show("请先登录后再访问云盘", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (_cloudLoading)
            {
                return;
            }

            try
            {
                _cloudLoading = true;

                if (preserveSelection)
                {
                    CacheCurrentCloudSelection();
                }

                if (!skipSave)
                {
                    SaveNavigationState();
                }

                _isHomePage = false;
                UpdateStatusBar("正在加载云盘歌曲...");

                int offset = Math.Max(0, (_cloudPage - 1) * CloudPageSize);
                var pageResult = await _apiClient.GetCloudSongsAsync(CloudPageSize, offset);

                _cloudHasMore = pageResult.HasMore;
                _cloudTotalCount = pageResult.TotalCount;
                _cloudUsedSize = pageResult.UsedSize;
                _cloudMaxSize = pageResult.MaxSize;

                _currentPage = _cloudPage;
                _maxPage = _cloudHasMore ? _cloudPage + 1 : _cloudPage;
                _hasNextSearchPage = _cloudHasMore;

                var songs = pageResult.Songs ?? new System.Collections.Generic.List<SongInfo>();

                DisplaySongs(
                    songs,
                    showPagination: true,
                    hasNextPage: _cloudHasMore,
                    startIndex: offset + 1,
                    preserveSelection: preserveSelection,
                    viewSource: "user_cloud",
                    accessibleName: "云盘歌曲",
                    skipAvailabilityCheck: true);

                RestoreCloudSelection();

                UpdateStatusBar(BuildCloudStatusText(songs.Count));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Cloud] 加载云盘失败: {ex}");
                MessageBox.Show($"加载云盘失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatusBar("加载云盘失败");
            }
            finally
            {
                _cloudLoading = false;
            }
        }

        private string BuildCloudStatusText(int currentCount)
        {
            string used = FormatSize(_cloudUsedSize);
            string max = _cloudMaxSize > 0 ? FormatSize(_cloudMaxSize) : "未知";
            return $"云盘 - 第 {_cloudPage} 页，本页 {currentCount} 首 / 总 {_cloudTotalCount} 首，已用 {used} / {max}";
        }

        private static string FormatSize(long bytes)
        {
            if (bytes <= 0)
            {
                return "0 B";
            }

            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int order = 0;
            double value = bytes;
            while (value >= 1024 && order < units.Length - 1)
            {
                order++;
                value /= 1024;
            }
            return $"{value:0.##} {units[order]}";
        }

        private Task UploadCloudSongsAsync(string[] filePaths)
        {
            if (filePaths == null || filePaths.Length == 0)
            {
                return Task.CompletedTask;
            }

            if (!IsUserLoggedIn())
            {
                MessageBox.Show("请先登录后再上传云盘歌曲", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return Task.CompletedTask;
            }

            var validFiles = filePaths
                .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (validFiles.Length == 0)
            {
                MessageBox.Show("未找到可上传的音频文件", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return Task.CompletedTask;
            }

            // 使用上传管理器添加任务
            var uploadManager = YTPlayer.Core.Upload.UploadManager.Instance;
            var addedTasks = uploadManager.AddBatchUploadTasks(validFiles, "云盘");

            UpdateStatusBar($"已添加 {addedTasks.Count} 个上传任务到传输管理器");
            return Task.CompletedTask;
        }

        private async Task DeleteSelectedCloudSongAsync()
        {
            var song = GetSelectedCloudSong();
            if (song == null)
            {
                MessageBox.Show("请选择要删除的云盘歌曲", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (string.IsNullOrEmpty(song.CloudSongId))
            {
                MessageBox.Show("无法删除选中的歌曲", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (MessageBox.Show(
                    $"确定要从云盘删除歌曲：\n{song.Name} - {song.Artist}",
                    "确认删除",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }

            string? fallbackFocusId = DetermineNeighborCloudSongId(song);

            try
            {
                UpdateStatusBar("正在删除云盘歌曲...");
                bool success = await _apiClient.DeleteCloudSongsAsync(new[] { song.CloudSongId });
                if (success)
                {
                    UpdateStatusBar("云盘歌曲已删除");
                    _lastSelectedCloudSongId = fallbackFocusId;
                    RequestCloudRefresh(fallbackFocusId, preserveSelection: false);
                }
                else
                {
                    MessageBox.Show("删除云盘歌曲失败，请稍后重试。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    UpdateStatusBar("删除云盘歌曲失败");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"删除云盘歌曲失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatusBar("删除云盘歌曲失败");
            }
        }

        private SongInfo? GetSelectedCloudSong()
        {
            if (resultListView.SelectedItems.Count == 0)
            {
                return null;
            }

            return GetSongFromListViewItem(resultListView.SelectedItems[0]);
        }

        private SongInfo? GetSongFromListViewItem(ListViewItem? item)
        {
            if (item?.Tag is int index && index >= 0 && index < _currentSongs.Count)
            {
                var song = _currentSongs[index];
                return song?.IsCloudSong == true ? song : null;
            }

            return null;
        }

        private void CacheCurrentCloudSelection()
        {
            var song = GetSelectedCloudSong();
            if (song != null && !string.IsNullOrEmpty(song.CloudSongId))
            {
                _pendingCloudFocusId = song.CloudSongId;
                _lastSelectedCloudSongId = song.CloudSongId;
            }
        }

        private void RestoreCloudSelection()
        {
            if (!string.Equals(_currentViewSource, "user_cloud", StringComparison.OrdinalIgnoreCase))
            {
                _pendingCloudFocusId = null;
                return;
            }

            string? targetCloudId = _pendingCloudFocusId ?? _lastSelectedCloudSongId;
            if (string.IsNullOrEmpty(targetCloudId))
            {
                return;
            }

            for (int i = 0; i < resultListView.Items.Count; i++)
            {
                if (resultListView.Items[i].Tag is int index &&
                    index >= 0 &&
                    index < _currentSongs.Count)
                {
                    var song = _currentSongs[index];
                    if (song != null &&
                        song.IsCloudSong &&
                        string.Equals(song.CloudSongId, targetCloudId, StringComparison.Ordinal))
                    {
                        resultListView.Items[i].Selected = true;
                        resultListView.FocusedItem = resultListView.Items[i];
                        resultListView.EnsureVisible(i);
                        _lastSelectedCloudSongId = song.CloudSongId;
                        break;
                    }
                }
            }

            _pendingCloudFocusId = null;
        }

        private void RequestCloudRefresh(string? focusCloudSongId = null, bool preserveSelection = true)
        {
            void Runner()
            {
                async void RefreshImpl()
                {
                    try
                    {
                        if (!string.Equals(_currentViewSource, "user_cloud", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!string.IsNullOrEmpty(focusCloudSongId))
                            {
                                _pendingCloudFocusId = focusCloudSongId;
                                _lastSelectedCloudSongId = focusCloudSongId;
                            }
                            return;
                        }

                        if (!string.IsNullOrEmpty(focusCloudSongId))
                        {
                            _pendingCloudFocusId = focusCloudSongId;
                        }
                        else if (preserveSelection)
                        {
                            CacheCurrentCloudSelection();
                        }

                        int waitAttempts = 0;
                        while (_cloudLoading && waitAttempts < 10)
                        {
                            await Task.Delay(200);
                            waitAttempts++;
                        }

                        await LoadCloudSongsAsync(skipSave: true, preserveSelection: preserveSelection);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Cloud] 刷新云盘失败: {ex.Message}");
                    }
                }

                RefreshImpl();
            }

            SafeInvoke(Runner);
        }

        private async void uploadToCloudMenuItem_Click(object sender, EventArgs e)
        {
            using var dialog = new OpenFileDialog
            {
                Multiselect = true,
                Filter = "音频文件|*.mp3;*.flac;*.wav;*.m4a;*.ogg;*.ape;*.wma|所有文件|*.*"
            };

            if (dialog.ShowDialog(this) == DialogResult.OK && dialog.FileNames?.Length > 0)
            {
                await UploadCloudSongsAsync(dialog.FileNames);
            }
        }

        private async void deleteFromCloudMenuItem_Click(object sender, EventArgs e)
        {
            await DeleteSelectedCloudSongAsync();
        }

        private string? DetermineNeighborCloudSongId(SongInfo currentSong)
        {
            if (currentSong == null || _currentSongs == null || _currentSongs.Count == 0)
            {
                return null;
            }

            int index = _currentSongs.IndexOf(currentSong);
            if (index < 0)
            {
                return null;
            }

            // 尝试寻找下一首
            for (int i = index + 1; i < _currentSongs.Count; i++)
            {
                var candidate = _currentSongs[i];
                if (candidate != null && candidate.IsCloudSong && !string.IsNullOrEmpty(candidate.CloudSongId))
                {
                    return candidate.CloudSongId;
                }
            }

            // 回退上一首
            for (int i = index - 1; i >= 0; i--)
            {
                var candidate = _currentSongs[i];
                if (candidate != null && candidate.IsCloudSong && !string.IsNullOrEmpty(candidate.CloudSongId))
                {
                    return candidate.CloudSongId;
                }
            }

            return null;
        }

        private void OnCloudUploadTaskCompleted(UploadTask task)
        {
            if (task == null)
            {
                return;
            }

            void Handler()
            {
                if (!IsUserLoggedIn())
                {
                    return;
                }

                string? focusId = !string.IsNullOrEmpty(task.CloudSongId)
                    ? task.CloudSongId
                    : null;

                if (!string.IsNullOrEmpty(focusId))
                {
                    _lastSelectedCloudSongId = focusId;
                    _cloudPage = 1;
                }

                RequestCloudRefresh(focusId, preserveSelection: string.IsNullOrEmpty(focusId));
            }

            SafeInvoke(Handler);
        }

        private void OnCloudUploadTaskFailed(UploadTask task)
        {
            if (task == null)
            {
                return;
            }

            SafeInvoke(() =>
            {
                if (_lastNotifiedUploadFailureTaskId == task.TaskId)
                {
                    return;
                }

                _lastNotifiedUploadFailureTaskId = task.TaskId;

                string message = !string.IsNullOrWhiteSpace(task.ErrorMessage)
                    ? task.ErrorMessage
                    : (!string.IsNullOrWhiteSpace(task.StageMessage) ? task.StageMessage : "未知错误");

                UpdateStatusBar($"云盘上传失败：{message}");

                MessageBox.Show(
                    $"云盘上传失败：{message}\n\n文件：{task.FileName}",
                    "云盘上传失败",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            });
        }
    }
}
