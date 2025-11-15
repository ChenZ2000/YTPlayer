using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using YTPlayer.Core;
using YTPlayer.Core.Download;
using YTPlayer.Forms.Download;
using YTPlayer.Models;
using YTPlayer.Models.Download;

#pragma warning disable CS8600, CS8601, CS8602, CS8603, CS8604

namespace YTPlayer
{
    /// <summary>
    /// MainForm 的下载功能部分（Partial Class）
    /// </summary>
    public partial class MainForm
    {
        #region 私有字段 - 下载

        private DownloadManager? _downloadManager;
        private DownloadManagerForm? _downloadManagerForm;

        #endregion

        #region 初始化 - 下载

        /// <summary>
        /// 初始化下载和上传功能
        /// </summary>
        private void InitializeDownload()
        {
            _downloadManager = DownloadManager.Instance;
            _downloadManager.Initialize(_apiClient);

            // 初始化上传管理器
            var uploadManager = YTPlayer.Core.Upload.UploadManager.Instance;
            uploadManager.Initialize(_apiClient);
            uploadManager.TaskCompleted -= OnCloudUploadTaskCompleted;
            uploadManager.TaskCompleted += OnCloudUploadTaskCompleted;
            uploadManager.TaskFailed -= OnCloudUploadTaskFailed;
            uploadManager.TaskFailed += OnCloudUploadTaskFailed;
        }

        #endregion

        #region 菜单事件 - 文件菜单

        /// <summary>
        /// 打开下载目录
        /// </summary>
        internal void OpenDownloadDirectory_Click(object? sender, EventArgs e)
        {
            try
            {
                var config = _configManager.Load();
                string downloadDir = ConfigManager.GetFullDownloadPath(config.DownloadDirectory);

                if (!Directory.Exists(downloadDir))
                {
                    Directory.CreateDirectory(downloadDir);
                }

                Process.Start("explorer.exe", downloadDir);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"无法打开下载目录：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// 更改下载目录
        /// </summary>
        internal void ChangeDownloadDirectory_Click(object? sender, EventArgs e)
        {
            try
            {
                var config = _configManager.Load();
                string currentDir = ConfigManager.GetFullDownloadPath(config.DownloadDirectory);

                using (var dialog = new FolderBrowserDialog())
                {
                    dialog.Description = "选择下载目录";
                    dialog.SelectedPath = currentDir;

                    if (dialog.ShowDialog() == DialogResult.OK)
                    {
                        string newPath = dialog.SelectedPath;

                        // 更新配置（保存绝对路径）
                        config.DownloadDirectory = newPath;
                        _configManager.Save(config);

                        MessageBox.Show($"下载目录已更改为：\n{newPath}", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"更改下载目录失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// 打开下载管理器
        /// </summary>
        internal void OpenDownloadManager_Click(object? sender, EventArgs e)
        {
            try
            {
                if (_downloadManagerForm != null && !_downloadManagerForm.IsDisposed)
                {
                    if (_downloadManagerForm.WindowState == FormWindowState.Minimized)
                    {
                        _downloadManagerForm.WindowState = FormWindowState.Normal;
                    }
                    _downloadManagerForm.BringToFront();
                    _downloadManagerForm.Activate();
                    _downloadManagerForm.Focus();
                    return;
                }

                _downloadManagerForm = new DownloadManagerForm();
                _downloadManagerForm.FormClosed += DownloadManagerForm_FormClosed;
                _downloadManagerForm.Show(this);
                _downloadManagerForm.BringToFront();
                _downloadManagerForm.Activate();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"无法打开下载管理器：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void DownloadManagerForm_FormClosed(object? sender, FormClosedEventArgs e)
        {
            if (_downloadManagerForm != null)
            {
                _downloadManagerForm.FormClosed -= DownloadManagerForm_FormClosed;
                _downloadManagerForm = null;
            }
        }

        #endregion

        #region 上下文菜单 - 下载

        /// <summary>
        /// 下载单曲（从上下文菜单）
        /// </summary>
        internal async void DownloadSong_Click(object? sender, EventArgs e)
        {
            var song = GetSelectedSongFromContextMenu(sender);
            if (song == null)
            {
                ShowContextSongMissingMessage("下载的歌曲");
                return;
            }

            try
            {
                // 获取当前音质
                var quality = GetCurrentQuality();

                // 获取来源列表名称
                string sourceList = GetCurrentViewName();

                // 添加下载任务
                var task = await _downloadManager!.AddSongDownloadAsync(song, quality, sourceList);

                if (task != null)
                {
                    MessageBox.Show($"已添加到下载队列：\n{song.Name} - {song.Artist}", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    MessageBox.Show($"添加下载任务失败", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"下载失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        internal async void DownloadLyrics_Click(object? sender, EventArgs e)
        {
            var song = GetSelectedSongFromContextMenu(sender);
            if (song == null || string.IsNullOrWhiteSpace(song.Id))
            {
                ShowContextSongMissingMessage("下载歌词的歌曲");
                return;
            }

            try
            {
                var lyricInfo = await _apiClient.GetLyricsAsync(song.Id);
                if (lyricInfo == null || string.IsNullOrWhiteSpace(lyricInfo.Lyric))
                {
                    MessageBox.Show("该歌曲没有歌词", "提示",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                string sourceList = GetCurrentViewName();
                var task = await _downloadManager!.AddLyricDownloadAsync(song, sourceList, lyricInfo.Lyric);
                if (task != null)
                {
                    MessageBox.Show($"已添加歌词下载任务：\n{song.Name} - {song.Artist}", "提示",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    MessageBox.Show("歌词下载任务创建失败", "错误",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"下载歌词失败：{ex.Message}", "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// 批量下载歌曲
        /// </summary>
        internal async void BatchDownloadSongs_Click(object? sender, EventArgs e)
        {
            if (_currentSongs == null || _currentSongs.Count == 0)
            {
                MessageBox.Show("当前列表为空", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                // 准备歌曲列表（从 _currentSongs 获取）
                var songs = new List<SongInfo>(_currentSongs);
                var displayNames = new List<string>();

                for (int i = 0; i < songs.Count; i++)
                {
                    var song = songs[i];
                    displayNames.Add($"{i + 1}. {song.Name} - {song.Artist}");
                }

                if (songs.Count == 0)
                {
                    MessageBox.Show("当前列表中没有可下载的歌曲", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // 显示批量下载选择对话框
                string viewName = GetCurrentViewName();
                var dialog = new BatchDownloadDialog(displayNames, $"批量下载 - {viewName}");

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    var selectedIndices = dialog.SelectedIndices;
                    if (selectedIndices.Count == 0)
                    {
                        return;
                    }

                    // 获取选中的歌曲
                    var selectedSongs = selectedIndices.Select(i => songs[i]).ToList();

                    // 转换为1-based索引（用于文件命名）
                    var originalIndices = selectedIndices.Select(i => i + 1).ToList();

                    // 获取当前音质
                    var quality = GetCurrentQuality();

                    // 添加批量下载任务
                    var tasks = await _downloadManager!.AddBatchDownloadAsync(
                        selectedSongs,
                        quality,
                        viewName,
                        subDirectory: viewName,
                        originalIndices: originalIndices);

                    MessageBox.Show(
                        $"已添加 {tasks.Count}/{selectedSongs.Count} 个下载任务",
                        "提示",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"批量下载失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// 下载歌单（从上下文菜单）
        /// </summary>
        internal async void DownloadPlaylist_Click(object? sender, EventArgs e)
        {
            if (resultListView.SelectedItems.Count == 0)
            {
                return;
            }

            var selectedItem = resultListView.SelectedItems[0];
            if (selectedItem.Tag is not PlaylistInfo playlist)
            {
                return;
            }

            try
            {
                // 使用 Cursor.Current 在 UI 线程上显示等待光标
                var originalCursor = Cursor.Current;
                Cursor.Current = Cursors.WaitCursor;

                try
                {
                    // 获取歌单详情和歌曲列表
                    var playlistDetail = await _apiClient.GetPlaylistDetailAsync(playlist.Id);
                    if (playlistDetail == null || playlistDetail.Songs == null || playlistDetail.Songs.Count == 0)
                    {
                        MessageBox.Show("无法获取歌单歌曲列表或歌单为空", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    // 准备歌曲列表
                    var displayNames = new List<string>();
                    for (int i = 0; i < playlistDetail.Songs.Count; i++)
                    {
                        var song = playlistDetail.Songs[i];
                        displayNames.Add($"{i + 1}. {song.Name} - {song.Artist}");
                    }

                    // 显示批量下载选择对话框
                    var dialog = new BatchDownloadDialog(displayNames, $"下载歌单 - {playlist.Name}");

                    if (dialog.ShowDialog() == DialogResult.OK)
                    {
                        var selectedIndices = dialog.SelectedIndices;
                        if (selectedIndices.Count == 0)
                        {
                            return;
                        }

                        // 获取选中的歌曲
                        var selectedSongs = selectedIndices.Select(i => playlistDetail.Songs[i]).ToList();

                        // 转换为1-based索引（用于文件命名）
                        var originalIndices = selectedIndices.Select(i => i + 1).ToList();

                        // 获取当前音质
                        var quality = GetCurrentQuality();

                        // 添加批量下载任务
                        var tasks = await _downloadManager!.AddBatchDownloadAsync(
                            selectedSongs,
                            quality,
                            sourceList: playlist.Name,
                            subDirectory: playlist.Name,
                            originalIndices: originalIndices);

                        MessageBox.Show(
                            $"已添加 {tasks.Count}/{selectedSongs.Count} 个下载任务\n歌单：{playlist.Name}",
                            "提示",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Information);
                    }
                }
                finally
                {
                    Cursor.Current = originalCursor;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"下载歌单失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// 下载专辑（从上下文菜单）
        /// </summary>
        internal async void DownloadAlbum_Click(object? sender, EventArgs e)
        {
            if (resultListView.SelectedItems.Count == 0)
            {
                return;
            }

            var selectedItem = resultListView.SelectedItems[0];
            if (selectedItem.Tag is not AlbumInfo album)
            {
                return;
            }

            try
            {
                // 使用 Cursor.Current 在 UI 线程上显示等待光标
                var originalCursor = Cursor.Current;
                Cursor.Current = Cursors.WaitCursor;

                try
                {
                    // 获取专辑歌曲列表
                    var songs = await _apiClient.GetAlbumSongsAsync(album.Id);
                    if (songs == null || songs.Count == 0)
                    {
                        MessageBox.Show("无法获取专辑歌曲列表或专辑为空", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    // 准备歌曲列表
                    var displayNames = new List<string>();
                    for (int i = 0; i < songs.Count; i++)
                    {
                        var song = songs[i];
                        displayNames.Add($"{i + 1}. {song.Name} - {song.Artist}");
                    }

                    // 显示批量下载选择对话框
                    var dialog = new BatchDownloadDialog(displayNames, $"下载专辑 - {album.Name}");

                    if (dialog.ShowDialog() == DialogResult.OK)
                    {
                        var selectedIndices = dialog.SelectedIndices;
                        if (selectedIndices.Count == 0)
                        {
                            return;
                        }

                        // 获取选中的歌曲
                        var selectedSongs = selectedIndices.Select(i => songs[i]).ToList();

                        // 转换为1-based索引（用于文件命名）
                        var originalIndices = selectedIndices.Select(i => i + 1).ToList();

                        // 获取当前音质
                        var quality = GetCurrentQuality();

                        // 添加批量下载任务
                        var tasks = await _downloadManager!.AddBatchDownloadAsync(
                            selectedSongs,
                            quality,
                            sourceList: $"{album.Name} - {album.Artist}",
                            subDirectory: $"{album.Name} - {album.Artist}",
                            originalIndices: originalIndices);

                        MessageBox.Show(
                            $"已添加 {tasks.Count}/{selectedSongs.Count} 个下载任务\n专辑：{album.Name} - {album.Artist}",
                            "提示",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Information);
                    }
                }
                finally
                {
                    Cursor.Current = originalCursor;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"下载专辑失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// 下载播客的全部节目（从上下文菜单）
        /// </summary>
        internal async void DownloadPodcast_Click(object? sender, EventArgs e)
        {
            var podcast = GetSelectedPodcastFromContextMenu(sender);
            if (podcast == null || podcast.Id <= 0)
            {
                MessageBox.Show("请选择要下载的播客。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                var originalCursor = Cursor.Current;
                Cursor.Current = Cursors.WaitCursor;

                try
                {
                    var episodes = await FetchAllPodcastEpisodesAsync(podcast.Id);
                    if (episodes == null || episodes.Count == 0)
                    {
                        MessageBox.Show("该播客暂无可下载的节目。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }

                    var displayNames = new List<string>();
                    for (int i = 0; i < episodes.Count; i++)
                    {
                        var episode = episodes[i];
                        string meta = string.Empty;
                        if (episode.PublishTime.HasValue)
                        {
                            meta = episode.PublishTime.Value.ToString("yyyy-MM-dd");
                        }
                        if (episode.Duration > TimeSpan.Zero)
                        {
                            string durationLabel = $"{episode.Duration:mm\\:ss}";
                            meta = string.IsNullOrEmpty(meta) ? durationLabel : $"{meta} | {durationLabel}";
                        }

                        string hostLabel = string.Empty;
                        if (!string.IsNullOrWhiteSpace(episode.RadioName))
                        {
                            hostLabel = episode.RadioName;
                        }
                        if (!string.IsNullOrWhiteSpace(episode.DjName))
                        {
                            hostLabel = string.IsNullOrWhiteSpace(hostLabel)
                                ? episode.DjName
                                : $"{hostLabel} / {episode.DjName}";
                        }

                        string line = $"{i + 1}. {episode.Name}";
                        if (!string.IsNullOrWhiteSpace(meta))
                        {
                            line += $" ({meta})";
                        }
                        if (!string.IsNullOrWhiteSpace(hostLabel))
                        {
                            line += $" - {hostLabel}";
                        }

                        displayNames.Add(line);
                    }

                    string safeName = string.IsNullOrWhiteSpace(podcast.Name)
                        ? $"播客_{podcast.Id}"
                        : podcast.Name;
                    var dialog = new BatchDownloadDialog(displayNames, $"下载播客 - {safeName}");
                    if (dialog.ShowDialog() != DialogResult.OK || dialog.SelectedIndices.Count == 0)
                    {
                        return;
                    }

                    var selectedIndices = dialog.SelectedIndices;
                    var selectedSongs = new List<SongInfo>();
                    var originalIndices = new List<int>();
                    foreach (int index in selectedIndices)
                    {
                        if (index < 0 || index >= episodes.Count)
                        {
                            continue;
                        }

                        var song = episodes[index].Song;
                        if (song != null)
                        {
                            selectedSongs.Add(song);
                            originalIndices.Add(index + 1);
                        }
                    }

                    if (selectedSongs.Count == 0)
                    {
                        MessageBox.Show("选中的节目缺少可下载的音频信息。", "提示",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    var quality = GetCurrentQuality();
                    var tasks = await _downloadManager!.AddBatchDownloadAsync(
                        selectedSongs,
                        quality,
                        sourceList: $"播客 - {safeName}",
                        subDirectory: safeName,
                        originalIndices: originalIndices);

                    MessageBox.Show(
                        $"已添加 {tasks.Count}/{selectedSongs.Count} 个下载任务\n播客：{safeName}",
                        "提示",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
                finally
                {
                    Cursor.Current = originalCursor;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"下载播客失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        #endregion

        #region 辅助方法 - 下载

        /// <summary>
        /// 获取当前选择的音质级别
        /// ⭐ 优化：直接使用 NeteaseApiClient.GetQualityLevelFromName 避免代码重复
        /// </summary>
        private QualityLevel GetCurrentQuality()
        {
            var config = _configManager.Load();
            string qualityName = config.DefaultQuality;

            // ⭐ 使用统一的音质映射系统，避免重复实现和不一致
            // NeteaseApiClient.GetQualityLevelFromName 会处理所有标准音质名称
            return NeteaseApiClient.GetQualityLevelFromName(qualityName);
        }

        /// <summary>
        /// 获取当前视图名称（用于下载文件夹命名）
        /// </summary>
        private string GetCurrentViewName()
        {
            // 尝试获取当前视图名称（从 AccessibleName）
            if (!string.IsNullOrWhiteSpace(resultListView.AccessibleName))
            {
                return resultListView.AccessibleName.Trim();
            }

            // 默认名称
            return "下载";
        }

        #endregion

        #region 新增下载功能 - 分类和批量

        /// <summary>
        /// 下载分类（从上下文菜单）
        /// ⭐ 统一处理所有分类类型的下载，支持完整的层级结构
        /// </summary>
        internal async void DownloadCategory_Click(object? sender, EventArgs e)
        {
            if (resultListView.SelectedItems.Count == 0)
            {
                return;
            }

            var selectedItem = resultListView.SelectedItems[0];
            if (selectedItem.Tag is not ListItemInfo listItem || listItem.Type != ListItemType.Category)
            {
                return;
            }

            try
            {
                var originalCursor = Cursor.Current;
                Cursor.Current = Cursors.WaitCursor;

                try
                {
                    string categoryId = listItem.CategoryId;
                    string categoryName = listItem.CategoryName ?? listItem.CategoryId;
                    var quality = GetCurrentQuality();

                    // ⭐ 根据分类类型处理下载
                    int totalTasks = 0;
                    switch (categoryId)
                    {
                        // ========== 歌曲列表分类 ==========
                        case "user_liked_songs":
                            totalTasks = await DownloadSongListCategory(categoryName, async () =>
                            {
                                var userInfo = await _apiClient.GetUserAccountAsync();
                                if (userInfo == null || userInfo.UserId <= 0)
                                {
                                    throw new Exception("获取用户信息失败");
                                }
                                var likedIds = await _apiClient.GetUserLikedSongsAsync(userInfo.UserId);
                                if (likedIds == null || likedIds.Count == 0)
                                {
                                    throw new Exception("您还没有喜欢的歌曲");
                                }
                                // 分批获取歌曲详情
                                var allSongs = new List<SongInfo>();
                                for (int i = 0; i < likedIds.Count; i += 100)
                                {
                                    var batchIds = likedIds.Skip(i).Take(100).ToArray();
                                    var songs = await _apiClient.GetSongDetailAsync(batchIds);
                                    if (songs != null)
                                    {
                                        allSongs.AddRange(songs);
                                    }
                                }
                                return allSongs;
                            }, quality);
                            break;

                        case "daily_recommend_songs":
                            totalTasks = await DownloadSongListCategory(categoryName, async () =>
                            {
                                var songs = await _apiClient.GetDailyRecommendSongsAsync();
                                if (songs == null || songs.Count == 0)
                                {
                                    throw new Exception("获取每日推荐歌曲失败");
                                }
                                return songs;
                            }, quality);
                            break;

                        case "personalized_newsongs":
                            totalTasks = await DownloadSongListCategory(categoryName, async () =>
                            {
                                var songs = await _apiClient.GetPersonalizedNewSongsAsync(20);
                                if (songs == null || songs.Count == 0)
                                {
                                    throw new Exception("获取推荐新歌失败");
                                }
                                return songs;
                            }, quality);
                            break;

                        // ========== 歌单列表分类 ==========
                        case "user_playlists":
                            totalTasks = await DownloadPlaylistListCategory(categoryName, async () =>
                            {
                                if (string.IsNullOrEmpty(_accountState?.UserId))
                                {
                                    throw new Exception("请先登录");
                                }
                                long userId = long.Parse(_accountState.UserId);
                                var (playlists, _) = await _apiClient.GetUserPlaylistsAsync(userId);
                                if (playlists == null || playlists.Count == 0)
                                {
                                    throw new Exception("您还没有歌单");
                                }
                                return playlists;
                            }, quality);
                            break;

                        case "toplist":
                            totalTasks = await DownloadPlaylistListCategory(categoryName, async () =>
                            {
                                var toplists = await _apiClient.GetToplistAsync();
                                if (toplists == null || toplists.Count == 0)
                                {
                                    throw new Exception("获取排行榜失败");
                                }
                                return toplists;
                            }, quality);
                            break;

                        case "daily_recommend_playlists":
                            totalTasks = await DownloadPlaylistListCategory(categoryName, async () =>
                            {
                                var playlists = await _apiClient.GetDailyRecommendPlaylistsAsync();
                                if (playlists == null || playlists.Count == 0)
                                {
                                    throw new Exception("获取每日推荐歌单失败");
                                }
                                return playlists;
                            }, quality);
                            break;

                        case "personalized_playlists":
                            totalTasks = await DownloadPlaylistListCategory(categoryName, async () =>
                            {
                                var playlists = await _apiClient.GetPersonalizedPlaylistsAsync(30);
                                if (playlists == null || playlists.Count == 0)
                                {
                                    throw new Exception("获取推荐歌单失败");
                                }
                                return playlists;
                            }, quality);
                            break;

                        // ========== 专辑列表分类 ==========
                        case "user_albums":
                            totalTasks = await DownloadAlbumListCategory(categoryName, async () =>
                            {
                                var (albums, totalCount) = await _apiClient.GetUserAlbumsAsync();
                                if (albums == null || albums.Count == 0)
                                {
                                    throw new Exception("您还没有收藏专辑");
                                }
                                return albums;
                            }, quality);
                            break;

                        // ========== 混合分类（包含多个子分类）==========
                        case "daily_recommend":
                            await DownloadMixedCategory(categoryName, () =>
                            {
                                var items = new List<ListItemInfo>();

                                // 每日推荐歌曲
                                items.Add(new ListItemInfo
                                {
                                    Type = ListItemType.Category,
                                    CategoryId = "daily_recommend_songs",
                                    CategoryName = "每日推荐歌曲",
                                    CategoryDescription = "根据您的听歌习惯推荐的歌曲"
                                });

                                // 每日推荐歌单
                                items.Add(new ListItemInfo
                                {
                                    Type = ListItemType.Category,
                                    CategoryId = "daily_recommend_playlists",
                                    CategoryName = "每日推荐歌单",
                                    CategoryDescription = "根据您的听歌习惯推荐的歌单"
                                });

                                return items;
                            }, quality);
                            return;

                        case "personalized":
                            await DownloadMixedCategory(categoryName, () =>
                            {
                                var items = new List<ListItemInfo>();

                                // 推荐歌单
                                items.Add(new ListItemInfo
                                {
                                    Type = ListItemType.Category,
                                    CategoryId = "personalized_playlists",
                                    CategoryName = "推荐歌单",
                                    CategoryDescription = "根据您的听歌习惯推荐的歌单"
                                });

                                // 推荐新歌
                                items.Add(new ListItemInfo
                                {
                                    Type = ListItemType.Category,
                                    CategoryId = "personalized_newsongs",
                                    CategoryName = "推荐新歌",
                                    CategoryDescription = "最新发行的歌曲推荐"
                                });

                                return items;
                            }, quality);
                            return;

                        // ========== 新增主页入口下载支持 ==========

                        // 听歌排行（混合分类：周榜单/全部时间）
                        case "user_play_record":
                            await DownloadMixedCategory(categoryName, () =>
                            {
                                var items = new List<ListItemInfo>();

                                items.Add(new ListItemInfo
                                {
                                    Type = ListItemType.Category,
                                    CategoryId = "user_play_record_week",
                                    CategoryName = "周榜单",
                                    CategoryDescription = "最近一周的听歌排行"
                                });

                                items.Add(new ListItemInfo
                                {
                                    Type = ListItemType.Category,
                                    CategoryId = "user_play_record_all",
                                    CategoryName = "全部时间",
                                    CategoryDescription = "所有时间的听歌排行"
                                });

                                return items;
                            }, quality);
                            return;

                        // 周听歌排行
                        case "user_play_record_week":
                            totalTasks = await DownloadSongListCategory(categoryName, async () =>
                            {
                                var userInfo = await _apiClient.GetUserAccountAsync();
                                if (userInfo == null || userInfo.UserId <= 0)
                                {
                                    throw new Exception("获取用户信息失败");
                                }
                                var playRecords = await _apiClient.GetUserPlayRecordAsync(userInfo.UserId, 1);
                                if (playRecords == null || playRecords.Count == 0)
                                {
                                    throw new Exception("暂无周榜单听歌记录");
                                }
                                return playRecords.Select(r => r.song).ToList();
                            }, quality);
                            break;

                        // 全部时间听歌排行
                        case "user_play_record_all":
                            totalTasks = await DownloadSongListCategory(categoryName, async () =>
                            {
                                var userInfo = await _apiClient.GetUserAccountAsync();
                                if (userInfo == null || userInfo.UserId <= 0)
                                {
                                    throw new Exception("获取用户信息失败");
                                }
                                var playRecords = await _apiClient.GetUserPlayRecordAsync(userInfo.UserId, 0);
                                if (playRecords == null || playRecords.Count == 0)
                                {
                                    throw new Exception("暂无全部时间听歌记录");
                                }
                                return playRecords.Select(r => r.song).ToList();
                            }, quality);
                            break;

                        // 精品歌单
                        case "highquality_playlists":
                            totalTasks = await DownloadPlaylistListCategory(categoryName, async () =>
                            {
                                var result = await _apiClient.GetHighQualityPlaylistsAsync("全部", 50, 0);
                                var playlists = result.Item1;
                                if (playlists == null || playlists.Count == 0)
                                {
                                    throw new Exception("获取精品歌单失败");
                                }
                                return playlists;
                            }, quality);
                            break;

                        // 新歌速递（混合分类：全部/华语/欧美/日本/韩国）
                        case "new_songs":
                            await DownloadMixedCategory(categoryName, () =>
                            {
                                var items = new List<ListItemInfo>();

                                items.Add(new ListItemInfo
                                {
                                    Type = ListItemType.Category,
                                    CategoryId = "new_songs_all",
                                    CategoryName = "全部",
                                    CategoryDescription = "全部地区新歌"
                                });

                                items.Add(new ListItemInfo
                                {
                                    Type = ListItemType.Category,
                                    CategoryId = "new_songs_chinese",
                                    CategoryName = "华语",
                                    CategoryDescription = "华语新歌"
                                });

                                items.Add(new ListItemInfo
                                {
                                    Type = ListItemType.Category,
                                    CategoryId = "new_songs_western",
                                    CategoryName = "欧美",
                                    CategoryDescription = "欧美新歌"
                                });

                                items.Add(new ListItemInfo
                                {
                                    Type = ListItemType.Category,
                                    CategoryId = "new_songs_japan",
                                    CategoryName = "日本",
                                    CategoryDescription = "日本新歌"
                                });

                                items.Add(new ListItemInfo
                                {
                                    Type = ListItemType.Category,
                                    CategoryId = "new_songs_korea",
                                    CategoryName = "韩国",
                                    CategoryDescription = "韩国新歌"
                                });

                                return items;
                            }, quality);
                            return;

                        // 全部新歌
                        case "new_songs_all":
                            totalTasks = await DownloadSongListCategory(categoryName, async () =>
                            {
                                var songs = await _apiClient.GetNewSongsAsync(0);
                                if (songs == null || songs.Count == 0)
                                {
                                    throw new Exception("获取全部新歌失败");
                                }
                                return songs;
                            }, quality);
                            break;

                        // 华语新歌
                        case "new_songs_chinese":
                            totalTasks = await DownloadSongListCategory(categoryName, async () =>
                            {
                                var songs = await _apiClient.GetNewSongsAsync(7);
                                if (songs == null || songs.Count == 0)
                                {
                                    throw new Exception("获取华语新歌失败");
                                }
                                return songs;
                            }, quality);
                            break;

                        // 欧美新歌
                        case "new_songs_western":
                            totalTasks = await DownloadSongListCategory(categoryName, async () =>
                            {
                                var songs = await _apiClient.GetNewSongsAsync(96);
                                if (songs == null || songs.Count == 0)
                                {
                                    throw new Exception("获取欧美新歌失败");
                                }
                                return songs;
                            }, quality);
                            break;

                        // 日本新歌
                        case "new_songs_japan":
                            totalTasks = await DownloadSongListCategory(categoryName, async () =>
                            {
                                var songs = await _apiClient.GetNewSongsAsync(8);
                                if (songs == null || songs.Count == 0)
                                {
                                    throw new Exception("获取日本新歌失败");
                                }
                                return songs;
                            }, quality);
                            break;

                        // 韩国新歌
                        case "new_songs_korea":
                            totalTasks = await DownloadSongListCategory(categoryName, async () =>
                            {
                                var songs = await _apiClient.GetNewSongsAsync(16);
                                if (songs == null || songs.Count == 0)
                                {
                                    throw new Exception("获取韩国新歌失败");
                                }
                                return songs;
                            }, quality);
                            break;

                        // 最近播放的歌单
                        case "recent_playlists":
                            totalTasks = await DownloadPlaylistListCategory(categoryName, async () =>
                            {
                                var playlists = await _apiClient.GetRecentPlaylistsAsync(100);
                                if (playlists == null || playlists.Count == 0)
                                {
                                    throw new Exception("暂无最近播放的歌单");
                                }
                                return playlists;
                            }, quality);
                            break;

                        // 最近播放的专辑
                        case "recent_albums":
                            totalTasks = await DownloadAlbumListCategory(categoryName, async () =>
                            {
                                var albums = await _apiClient.GetRecentAlbumsAsync(100);
                                if (albums == null || albums.Count == 0)
                                {
                                    throw new Exception("暂无最近播放的专辑");
                                }
                                return albums;
                            }, quality);
                            break;

                        case RecentListenedCategoryId:
                            await DownloadMixedCategory(categoryName, BuildRecentListenedEntries, quality);
                            return;

                        // 歌单分类（混合分类：10个常用分类）
                        case "playlist_category":
                            await DownloadMixedCategory(categoryName, () =>
                            {
                                var items = new List<ListItemInfo>();

                                var categories = new[] { "华语", "流行", "摇滚", "民谣", "电子", "轻音乐", "影视原声", "ACG", "怀旧", "治愈" };
                                foreach (var cat in categories)
                                {
                                    items.Add(new ListItemInfo
                                    {
                                        Type = ListItemType.Category,
                                        CategoryId = $"playlist_cat_{cat}",
                                        CategoryName = cat,
                                        CategoryDescription = $"{cat}歌单"
                                    });
                                }

                                return items;
                            }, quality);
                            return;

                        // 新碟上架
                        case "new_albums":
                            totalTasks = await DownloadAlbumListCategory(categoryName, async () =>
                            {
                                var albums = await _apiClient.GetNewAlbumsAsync();
                                if (albums == null || albums.Count == 0)
                                {
                                    throw new Exception("暂无新碟上架");
                                }
                                return albums;
                            }, quality);
                            break;

                        // 最近听过
                        case "recent_played":
                            totalTasks = await DownloadSongListCategory(categoryName, async () =>
                            {
                                var songs = await _apiClient.GetRecentPlayedSongsAsync(100);
                                if (songs == null || songs.Count == 0)
                                {
                                    throw new Exception("暂无最近播放记录");
                                }
                                return songs;
                            }, quality);
                            break;

                        default:
                            // 处理歌单分类的动态子分类（playlist_cat_xxx）
                            if (categoryId.StartsWith("playlist_cat_"))
                            {
                                string catName = categoryId.Substring("playlist_cat_".Length);
                                totalTasks = await DownloadPlaylistListCategory(catName, async () =>
                                {
                                    var result = await _apiClient.GetPlaylistsByCategoryAsync(catName, "hot", 50, 0);
                                    var playlists = result.Item1;
                                    if (playlists == null || playlists.Count == 0)
                                    {
                                        throw new Exception($"获取{catName}歌单失败");
                                    }
                                    return playlists;
                                }, quality);
                                break;
                            }
                            MessageBox.Show($"暂不支持下载该分类: {categoryName}", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            return;
                    }

                    // 显示下载完成消息（仅针对单一分类，混合分类在 DownloadMixedCategory 中处理）
                    if (totalTasks > 0)
                    {
                        MessageBox.Show(
                            $"已添加 {totalTasks} 个下载任务\n分类：{categoryName}",
                            "提示",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Information);
                    }
                }
                finally
                {
                    Cursor.Current = originalCursor;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"下载分类失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// 下载歌曲列表类型的分类
        /// </summary>
        /// <param name="categoryName">分类名称</param>
        /// <param name="getSongsFunc">获取歌曲列表的异步函数</param>
        /// <param name="quality">音质级别</param>
        /// <param name="parentDirectory">父目录路径（用于多级层级）</param>
        /// <param name="showDialog">是否显示选择对话框（默认为 true）</param>
        private async Task<int> DownloadSongListCategory(
            string categoryName,
            Func<Task<List<SongInfo>>> getSongsFunc,
            QualityLevel quality,
            string? parentDirectory = null,
            bool showDialog = true)
        {
            // 获取歌曲列表
            var songs = await getSongsFunc();

            List<SongInfo> selectedSongs;
            List<int> originalIndices;

            if (showDialog)
            {
                // 准备歌曲列表
                var displayNames = new List<string>();
                for (int i = 0; i < songs.Count; i++)
                {
                    var song = songs[i];
                    displayNames.Add($"{i + 1}. {song.Name} - {song.Artist}");
                }

                // 显示批量下载选择对话框
                var dialog = new BatchDownloadDialog(displayNames, $"下载分类 - {categoryName}");

                if (dialog.ShowDialog() != DialogResult.OK || dialog.SelectedIndices.Count == 0)
                {
                    return 0;
                }

                // 获取选中的歌曲
                var selectedIndicesList = dialog.SelectedIndices;
                selectedSongs = selectedIndicesList.Select(i => songs[i]).ToList();
                originalIndices = selectedIndicesList.Select(i => i + 1).ToList();
            }
            else
            {
                // 不显示对话框，下载所有歌曲
                selectedSongs = songs;
                originalIndices = Enumerable.Range(1, songs.Count).ToList();
            }

            // 构建完整的目录路径
            string fullDirectory = string.IsNullOrEmpty(parentDirectory)
                ? categoryName
                : Path.Combine(parentDirectory, categoryName);

            // 添加批量下载任务
            var tasks = await _downloadManager!.AddBatchDownloadAsync(
                selectedSongs,
                quality,
                sourceList: categoryName,
                subDirectory: fullDirectory,
                originalIndices: originalIndices);

            return tasks.Count;
        }

        /// <summary>
        /// 下载歌单列表类型的分类
        /// </summary>
        /// <param name="categoryName">分类名称</param>
        /// <param name="getPlaylistsFunc">获取歌单列表的异步函数</param>
        /// <param name="quality">音质级别</param>
        /// <param name="parentDirectory">父目录路径（用于多级层级）</param>
        /// <param name="showDialog">是否显示选择对话框（默认为 true）</param>
        private async Task<int> DownloadPlaylistListCategory(
            string categoryName,
            Func<Task<List<PlaylistInfo>>> getPlaylistsFunc,
            QualityLevel quality,
            string? parentDirectory = null,
            bool showDialog = true)
        {
            // 获取歌单列表
            var playlists = await getPlaylistsFunc();

            List<PlaylistInfo> selectedPlaylists;

            if (showDialog)
            {
                // 显示批量下载选择对话框（不带序号）
                var displayNames = playlists.Select(p => p.Name).ToList();
                var dialog = new BatchDownloadDialog(displayNames, $"下载分类 - {categoryName}");

                if (dialog.ShowDialog() != DialogResult.OK || dialog.SelectedIndices.Count == 0)
                {
                    return 0;
                }

                // 获取选中的歌单
                var selectedIndices = dialog.SelectedIndices;
                selectedPlaylists = selectedIndices.Select(i => playlists[i]).ToList();
            }
            else
            {
                // 不显示对话框，下载所有歌单
                selectedPlaylists = playlists;
            }

            int totalTasks = 0;
            foreach (var playlist in selectedPlaylists)
            {
                // 获取歌单详情
                var playlistDetail = await _apiClient.GetPlaylistDetailAsync(playlist.Id);
                if (playlistDetail?.Songs != null && playlistDetail.Songs.Count > 0)
                {
                    // 构建完整的目录路径：父目录/分类名/歌单名
                    string baseDirectory = string.IsNullOrEmpty(parentDirectory)
                        ? categoryName
                        : Path.Combine(parentDirectory, categoryName);
                    string subDirectory = Path.Combine(baseDirectory, playlist.Name);

                    var originalIndices = Enumerable.Range(1, playlistDetail.Songs.Count).ToList();

                    var tasks = await _downloadManager!.AddBatchDownloadAsync(
                        playlistDetail.Songs,
                        quality,
                        sourceList: playlist.Name,
                        subDirectory: subDirectory,
                        originalIndices: originalIndices);

                    totalTasks += tasks.Count;
                }
            }

            return totalTasks;
        }

        /// <summary>
        /// 下载专辑列表类型的分类
        /// </summary>
        /// <param name="categoryName">分类名称</param>
        /// <param name="getAlbumsFunc">获取专辑列表的异步函数</param>
        /// <param name="quality">音质级别</param>
        /// <param name="parentDirectory">父目录路径（用于多级层级）</param>
        /// <param name="showDialog">是否显示选择对话框（默认为 true）</param>
        private async Task<int> DownloadAlbumListCategory(
            string categoryName,
            Func<Task<List<AlbumInfo>>> getAlbumsFunc,
            QualityLevel quality,
            string? parentDirectory = null,
            bool showDialog = true)
        {
            // 获取专辑列表
            var albums = await getAlbumsFunc();

            List<AlbumInfo> selectedAlbums;

            if (showDialog)
            {
                // 显示批量下载选择对话框（不带序号）
                var displayNames = albums.Select(a => $"{a.Name} - {a.Artist}").ToList();
                var dialog = new BatchDownloadDialog(displayNames, $"下载分类 - {categoryName}");

                if (dialog.ShowDialog() != DialogResult.OK || dialog.SelectedIndices.Count == 0)
                {
                    return 0;
                }

                // 获取选中的专辑
                var selectedIndices = dialog.SelectedIndices;
                selectedAlbums = selectedIndices.Select(i => albums[i]).ToList();
            }
            else
            {
                // 不显示对话框，下载所有专辑
                selectedAlbums = albums;
            }

            int totalTasks = 0;
            foreach (var album in selectedAlbums)
            {
                // 获取专辑歌曲列表
                var songs = await _apiClient.GetAlbumSongsAsync(album.Id);
                if (songs != null && songs.Count > 0)
                {
                    // 构建完整的目录路径：父目录/分类名/专辑名
                    string baseDirectory = string.IsNullOrEmpty(parentDirectory)
                        ? categoryName
                        : Path.Combine(parentDirectory, categoryName);
                    string albumFolderName = $"{album.Name} - {album.Artist}";
                    string subDirectory = Path.Combine(baseDirectory, albumFolderName);

                    var originalIndices = Enumerable.Range(1, songs.Count).ToList();

                    var tasks = await _downloadManager!.AddBatchDownloadAsync(
                        songs,
                        quality,
                        sourceList: albumFolderName,
                        subDirectory: subDirectory,
                        originalIndices: originalIndices);

                    totalTasks += tasks.Count;
                }
            }

            return totalTasks;
        }

        /// <summary>
        /// 下载混合分类（包含多个子分类的分类）
        /// ⭐ 统一处理混合层级结构，支持子分类的递归下载
        /// </summary>
        /// <param name="categoryName">分类名称</param>
        /// <param name="getSubCategoriesFunc">获取子分类列表的函数</param>
        /// <param name="quality">音质级别</param>
        private async Task DownloadMixedCategory(
            string categoryName,
            Func<List<ListItemInfo>> getSubCategoriesFunc,
            QualityLevel quality)
        {
            // 获取子分类列表
            var subCategories = getSubCategoriesFunc();

            if (subCategories == null || subCategories.Count == 0)
            {
                MessageBox.Show("该分类下没有可用的子分类", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // 显示子分类选择对话框
            var displayNames = subCategories
                .Select(item => item.CategoryName ?? item.CategoryId ?? "未命名分类")
                .ToList();
            var dialog = new BatchDownloadDialog(displayNames, $"下载分类 - {categoryName}");

            if (dialog.ShowDialog() != DialogResult.OK || dialog.SelectedIndices.Count == 0)
            {
                return;
            }

            // 获取选中的子分类
            var selectedSubCategories = dialog.SelectedIndices.Select(i => subCategories[i]).ToList();

            int totalTasks = 0;
            var originalCursor = Cursor.Current;
            Cursor.Current = Cursors.WaitCursor;

            try
            {
                foreach (var subCategory in selectedSubCategories)
                {
                    string subCategoryId = subCategory.CategoryId;
                    string subCategoryName = subCategory.CategoryName ?? subCategoryId;

                    try
                    {
                        int taskCount = 0;

                        // ⭐ 根据子分类 ID 判断类型并调用对应的处理方法
                        switch (subCategoryId)
                        {
                            // ========== 歌曲列表子分类 ==========
                            case "daily_recommend_songs":
                                taskCount = await DownloadSongListCategory(
                                    subCategoryName,
                                    async () =>
                                    {
                                        var songs = await _apiClient.GetDailyRecommendSongsAsync();
                                        if (songs == null || songs.Count == 0)
                                        {
                                            throw new Exception("获取每日推荐歌曲失败");
                                        }
                                        return songs;
                                    },
                                    quality,
                                    parentDirectory: categoryName,
                                    showDialog: false);  // 不再显示对话框，下载所有
                                break;

                            case "personalized_newsongs":
                                taskCount = await DownloadSongListCategory(
                                    subCategoryName,
                                    async () =>
                                    {
                                        var songs = await _apiClient.GetPersonalizedNewSongsAsync(20);
                                        if (songs == null || songs.Count == 0)
                                        {
                                            throw new Exception("获取推荐新歌失败");
                                        }
                                        return songs;
                                    },
                                    quality,
                                    parentDirectory: categoryName,
                                    showDialog: false);
                                break;

                            // ========== 歌单列表子分类 ==========
                            case "daily_recommend_playlists":
                                taskCount = await DownloadPlaylistListCategory(
                                    subCategoryName,
                                    async () =>
                                    {
                                        var playlists = await _apiClient.GetDailyRecommendPlaylistsAsync();
                                        if (playlists == null || playlists.Count == 0)
                                        {
                                            throw new Exception("获取每日推荐歌单失败");
                                        }
                                        return playlists;
                                    },
                                    quality,
                                    parentDirectory: categoryName,
                                    showDialog: false);
                                break;

                            case "personalized_playlists":
                                taskCount = await DownloadPlaylistListCategory(
                                    subCategoryName,
                                    async () =>
                                    {
                                        var playlists = await _apiClient.GetPersonalizedPlaylistsAsync(30);
                                        if (playlists == null || playlists.Count == 0)
                                        {
                                            throw new Exception("获取推荐歌单失败");
                                        }
                                        return playlists;
                                    },
                                    quality,
                                    parentDirectory: categoryName,
                                    showDialog: false);
                                break;

                            // ========== 新增主页入口子分类 ==========

                            // 周听歌排行
                            case "user_play_record_week":
                                taskCount = await DownloadSongListCategory(
                                    subCategoryName,
                                    async () =>
                                    {
                                        var userInfo = await _apiClient.GetUserAccountAsync();
                                        if (userInfo == null || userInfo.UserId <= 0)
                                        {
                                            throw new Exception("获取用户信息失败");
                                        }
                                        var playRecords = await _apiClient.GetUserPlayRecordAsync(userInfo.UserId, 1);
                                        if (playRecords == null || playRecords.Count == 0)
                                        {
                                            throw new Exception("暂无周榜单听歌记录");
                                        }
                                        return playRecords.Select(r => r.song).ToList();
                                    },
                                    quality,
                                    parentDirectory: categoryName,
                                    showDialog: false);
                                break;

                            // 全部时间听歌排行
                            case "user_play_record_all":
                                taskCount = await DownloadSongListCategory(
                                    subCategoryName,
                                    async () =>
                                    {
                                        var userInfo = await _apiClient.GetUserAccountAsync();
                                        if (userInfo == null || userInfo.UserId <= 0)
                                        {
                                            throw new Exception("获取用户信息失败");
                                        }
                                        var playRecords = await _apiClient.GetUserPlayRecordAsync(userInfo.UserId, 0);
                                        if (playRecords == null || playRecords.Count == 0)
                                        {
                                            throw new Exception("暂无全部时间听歌记录");
                                        }
                                        return playRecords.Select(r => r.song).ToList();
                                    },
                                    quality,
                                    parentDirectory: categoryName,
                                    showDialog: false);
                                break;

                            // 全部新歌
                            case "new_songs_all":
                                taskCount = await DownloadSongListCategory(
                                    subCategoryName,
                                    async () =>
                                    {
                                        var songs = await _apiClient.GetNewSongsAsync(0);
                                        if (songs == null || songs.Count == 0)
                                        {
                                            throw new Exception("获取全部新歌失败");
                                        }
                                        return songs;
                                    },
                                    quality,
                                    parentDirectory: categoryName,
                                    showDialog: false);
                                break;

                            // 华语新歌
                            case "new_songs_chinese":
                                taskCount = await DownloadSongListCategory(
                                    subCategoryName,
                                    async () =>
                                    {
                                        var songs = await _apiClient.GetNewSongsAsync(7);
                                        if (songs == null || songs.Count == 0)
                                        {
                                            throw new Exception("获取华语新歌失败");
                                        }
                                        return songs;
                                    },
                                    quality,
                                    parentDirectory: categoryName,
                                    showDialog: false);
                                break;

                            // 欧美新歌
                            case "new_songs_western":
                                taskCount = await DownloadSongListCategory(
                                    subCategoryName,
                                    async () =>
                                    {
                                        var songs = await _apiClient.GetNewSongsAsync(96);
                                        if (songs == null || songs.Count == 0)
                                        {
                                            throw new Exception("获取欧美新歌失败");
                                        }
                                        return songs;
                                    },
                                    quality,
                                    parentDirectory: categoryName,
                                    showDialog: false);
                                break;

                            // 日本新歌
                            case "new_songs_japan":
                                taskCount = await DownloadSongListCategory(
                                    subCategoryName,
                                    async () =>
                                    {
                                        var songs = await _apiClient.GetNewSongsAsync(8);
                                        if (songs == null || songs.Count == 0)
                                        {
                                            throw new Exception("获取日本新歌失败");
                                        }
                                        return songs;
                                    },
                                    quality,
                                    parentDirectory: categoryName,
                                    showDialog: false);
                                break;

                            // 韩国新歌
                            case "new_songs_korea":
                                taskCount = await DownloadSongListCategory(
                                    subCategoryName,
                                    async () =>
                                    {
                                        var songs = await _apiClient.GetNewSongsAsync(16);
                                        if (songs == null || songs.Count == 0)
                                        {
                                            throw new Exception("获取韩国新歌失败");
                                        }
                                        return songs;
                                    },
                                    quality,
                                    parentDirectory: categoryName,
                                    showDialog: false);
                                break;

                            default:
                                // 处理歌单分类的动态子分类（playlist_cat_xxx）
                                if (subCategoryId.StartsWith("playlist_cat_"))
                                {
                                    string catName = subCategoryId.Substring("playlist_cat_".Length);
                                    taskCount = await DownloadPlaylistListCategory(
                                        catName,
                                        async () =>
                                        {
                                            var result = await _apiClient.GetPlaylistsByCategoryAsync(catName, "hot", 50, 0);
                                            var playlists = result.Item1;
                                            if (playlists == null || playlists.Count == 0)
                                            {
                                                throw new Exception($"获取{catName}歌单失败");
                                            }
                                            return playlists;
                                        },
                                        quality,
                                        parentDirectory: categoryName,
                                        showDialog: false);
                                    break;
                                }
                                MessageBox.Show($"暂不支持下载该子分类: {subCategoryName}", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                                continue;
                        }

                        totalTasks += taskCount;
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"下载子分类 '{subCategoryName}' 失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }

                if (totalTasks > 0)
                {
                    MessageBox.Show(
                        $"已添加 {totalTasks} 个下载任务\n分类：{categoryName}",
                        "提示",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
            }
            finally
            {
                Cursor.Current = originalCursor;
            }
        }

        /// <summary>
        /// 批量下载歌单/专辑（从上下文菜单）
        /// </summary>
        internal async void BatchDownloadPlaylistsOrAlbums_Click(object? sender, EventArgs e)
        {
            try
            {
                // 检查当前视图是否为空
                if (resultListView.Items.Count == 0)
                {
                    MessageBox.Show("当前列表为空", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // 检查当前视图类型
                bool isPlaylistView = resultListView.Items.Count > 0 && resultListView.Items[0].Tag is PlaylistInfo;
                bool isAlbumView = resultListView.Items.Count > 0 && resultListView.Items[0].Tag is AlbumInfo;

                if (!isPlaylistView && !isAlbumView)
                {
                    MessageBox.Show("当前视图不是歌单或专辑列表", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // 准备歌单/专辑列表（不带序号）
                var displayNames = new List<string>();
                var items = new List<object>();

                foreach (ListViewItem listViewItem in resultListView.Items)
                {
                    if (listViewItem.Tag is PlaylistInfo playlist)
                    {
                        displayNames.Add(playlist.Name);
                        items.Add(playlist);
                    }
                    else if (listViewItem.Tag is AlbumInfo album)
                    {
                        displayNames.Add($"{album.Name} - {album.Artist}");
                        items.Add(album);
                    }
                }

                if (items.Count == 0)
                {
                    MessageBox.Show("当前列表中没有可下载的歌单或专辑", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // 显示批量下载选择对话框
                string viewName = GetCurrentViewName();
                var dialog = new BatchDownloadDialog(displayNames, $"批量下载 - {viewName}");

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    var selectedIndices = dialog.SelectedIndices;
                    if (selectedIndices.Count == 0)
                    {
                        return;
                    }

                    var originalCursor = Cursor.Current;
                    Cursor.Current = Cursors.WaitCursor;

                    try
                    {
                        // 获取当前音质
                        var quality = GetCurrentQuality();

                        int totalTasks = 0;
                        foreach (int index in selectedIndices)
                        {
                            var item = items[index];

                            if (item is PlaylistInfo playlist)
                            {
                                // 下载歌单
                                var playlistDetail = await _apiClient.GetPlaylistDetailAsync(playlist.Id);
                                if (playlistDetail?.Songs != null && playlistDetail.Songs.Count > 0)
                                {
                                    var originalIndices = Enumerable.Range(1, playlistDetail.Songs.Count).ToList();

                                    var tasks = await _downloadManager!.AddBatchDownloadAsync(
                                        playlistDetail.Songs,
                                        quality,
                                        sourceList: playlist.Name,
                                        subDirectory: playlist.Name,
                                        originalIndices: originalIndices);

                                    totalTasks += tasks.Count;
                                }
                            }
                            else if (item is AlbumInfo album)
                            {
                                // 下载专辑
                                var songs = await _apiClient.GetAlbumSongsAsync(album.Id);
                                if (songs != null && songs.Count > 0)
                                {
                                    string albumName = $"{album.Name} - {album.Artist}";
                                    var originalIndices = Enumerable.Range(1, songs.Count).ToList();

                                    var tasks = await _downloadManager!.AddBatchDownloadAsync(
                                        songs,
                                        quality,
                                        sourceList: albumName,
                                        subDirectory: albumName,
                                        originalIndices: originalIndices);

                                    totalTasks += tasks.Count;
                                }
                            }
                        }

                        MessageBox.Show(
                            $"已添加 {totalTasks} 个下载任务",
                            "提示",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Information);
                    }
                    finally
                    {
                        Cursor.Current = originalCursor;
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"批量下载失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async Task<List<PodcastEpisodeInfo>> FetchAllPodcastEpisodesAsync(long podcastId)
        {
            var result = new List<PodcastEpisodeInfo>();
            if (podcastId <= 0)
            {
                return result;
            }

            int offset = 0;
            const int FetchLimit = 100;

            while (true)
            {
                var (episodes, hasMore, totalCount) = await _apiClient.GetPodcastEpisodesAsync(podcastId, FetchLimit, offset);
                if (episodes == null || episodes.Count == 0)
                {
                    break;
                }

                result.AddRange(episodes);
                offset += episodes.Count;

                if (!hasMore || offset >= totalCount)
                {
                    break;
                }
            }

            return result;
        }

        #endregion
    }
}

#pragma warning restore CS8600, CS8601, CS8602, CS8603, CS8604, CS8625
