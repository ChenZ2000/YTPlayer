using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using YTPlayer.Utils;
using MessageBox = YTPlayer.MessageBox;
using YTPlayer.Core;
using YTPlayer.Models;

namespace YTPlayer.Forms
{
    /// <summary>
    /// 添加歌曲到歌单对话框
    /// </summary>
    public partial class AddToPlaylistDialog : Form
    {
        private readonly NeteaseApiClient _apiClient;
        private readonly string _songId;
        private readonly long _userId;
        private List<PlaylistInfo> _playlists = new List<PlaylistInfo>();
        private Dictionary<string, bool> _songInPlaylistCache = new Dictionary<string, bool>();

        /// <summary>
        /// 选中的歌单ID
        /// </summary>
        public string? SelectedPlaylistId { get; private set; }

        public AddToPlaylistDialog(NeteaseApiClient apiClient, string songId, long userId)
        {
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            _songId = songId ?? throw new ArgumentNullException(nameof(songId));
            _userId = userId;

            InitializeComponent();
            ThemeManager.ApplyTheme(this);
        }

        private async void AddToPlaylistDialog_Load(object sender, EventArgs e)
        {
            await LoadPlaylistsAsync();
        }

        /// <summary>
        /// 加载用户的歌单列表
        /// </summary>
        private async Task LoadPlaylistsAsync(string? selectPlaylistId = null)
        {
            try
            {
                // 显示加载状态
                loadingLabel.Visible = true;
                createPlaylistButton.Enabled = false;
                confirmButton.Enabled = false;
                playlistListView.Items.Clear();

                // 获取用户歌单
                var (allPlaylists, _) = await _apiClient.GetUserPlaylistsAsync(_userId);

                // ⭐ 过滤只显示用户创建的歌单
                _playlists = allPlaylists?.Where(p =>
                    p.CreatorId == _userId || p.OwnerUserId == _userId
                ).ToList() ?? new List<PlaylistInfo>();

                if (_playlists == null || _playlists.Count == 0)
                {
                    MessageBox.Show("未找到您创建的歌单，请先创建一个歌单。", "提示",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    loadingLabel.Visible = false;
                    createPlaylistButton.Enabled = true;
                    return;
                }

                // 检查每个歌单是否已包含该歌曲
                await CheckSongInPlaylistsAsync();

                // 填充列表
                ListViewItem? pendingSelection = null;
                foreach (var playlist in _playlists)
                {
                    bool songExists = _songInPlaylistCache.ContainsKey(playlist.Id) && _songInPlaylistCache[playlist.Id];
                    var item = new ListViewItem(playlist.Name);
                    item.SubItems.Add(playlist.TrackCount.ToString());
                    item.SubItems.Add(songExists ? "已存在" : "");
                    item.Tag = playlist;

                    // 如果歌曲已存在，设置为灰色并禁用
                    if (songExists)
                    {
                        item.ForeColor = SystemColors.GrayText;
                    }

                    if (!string.IsNullOrWhiteSpace(selectPlaylistId) &&
                        string.Equals(playlist.Id, selectPlaylistId, StringComparison.Ordinal))
                    {
                        pendingSelection = item;
                    }

                    playlistListView.Items.Add(item);
                }

                if (pendingSelection != null)
                {
                    pendingSelection.Selected = true;
                    pendingSelection.Focused = true;
                    pendingSelection.EnsureVisible();
                    confirmButton.Enabled = !(pendingSelection.SubItems.Count > 2 &&
                        string.Equals(pendingSelection.SubItems[2].Text, "已存在", StringComparison.Ordinal));
                }

                loadingLabel.Visible = false;
                createPlaylistButton.Enabled = true;

                if (pendingSelection == null && playlistListView.SelectedItems.Count == 0)
                {
                    confirmButton.Enabled = false;
                }
            }
            catch (Exception ex)
            {
                loadingLabel.Visible = false;
                createPlaylistButton.Enabled = true;
                MessageBox.Show($"加载歌单失败: {ex.Message}", "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// 检查歌曲是否在歌单中
        /// </summary>
        private async Task CheckSongInPlaylistsAsync()
        {
            _songInPlaylistCache.Clear();

            // 批量检查（为了性能，可以并行检查，但这里简化为串行）
            foreach (var playlist in _playlists)
            {
                try
                {
                    // 获取歌单详情，检查是否包含该歌曲
                    var songs = await _apiClient.GetPlaylistSongsAsync(playlist.Id);
                    bool exists = songs != null && songs.Any(s => s.Id == _songId);
                    _songInPlaylistCache[playlist.Id] = exists;
                }
                catch
                {
                    // 如果获取失败，默认认为不存在
                    _songInPlaylistCache[playlist.Id] = false;
                }
            }
        }

        private void playlistListView_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (playlistListView.SelectedItems.Count > 0)
            {
                var selectedItem = playlistListView.SelectedItems[0];
                var playlist = selectedItem.Tag as PlaylistInfo;

                if (playlist != null)
                {
                    // 检查歌曲是否已存在
                    bool songExists = _songInPlaylistCache.ContainsKey(playlist.Id) && _songInPlaylistCache[playlist.Id];
                    confirmButton.Enabled = !songExists;
                }
            }
            else
            {
                confirmButton.Enabled = false;
            }
        }

        private void playlistListView_DoubleClick(object sender, EventArgs e)
        {
            // 双击直接确认（如果可用）
            if (confirmButton.Enabled)
            {
                confirmButton_Click(sender, e);
            }
        }

        private async void createPlaylistButton_Click(object sender, EventArgs e)
        {
            string trimmedName;
            using (var dialog = new NewPlaylistDialog())
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                trimmedName = dialog.PlaylistName;
            }

            if (string.IsNullOrWhiteSpace(trimmedName))
            {
                return;
            }

            var existingIds = CaptureCurrentPlaylistIds();

            createPlaylistButton.Enabled = false;
            confirmButton.Enabled = false;
            loadingLabel.Visible = true;

            try
            {
                var verifiedPlaylist = await CreatePlaylistWithVerificationAsync(trimmedName, existingIds);
                if (verifiedPlaylist != null && !string.IsNullOrWhiteSpace(verifiedPlaylist.Id))
                {
                    MessageBox.Show($"歌单 \"{verifiedPlaylist.Name}\" 创建成功，即将把歌曲添加到该歌单。", "成功",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);

                    await LoadPlaylistsAsync(verifiedPlaylist.Id);

                    SelectedPlaylistId = verifiedPlaylist.Id;
                    DialogResult = DialogResult.OK;
                    Close();
                    return;
                }

                MessageBox.Show("新建歌单失败，请稍后重试。", "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"新建歌单失败: {ex.Message}", "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            RestoreCreatePlaylistUiState();
        }

        private void confirmButton_Click(object sender, EventArgs e)
        {
            if (playlistListView.SelectedItems.Count > 0)
            {
                var selectedItem = playlistListView.SelectedItems[0];
                var playlist = selectedItem.Tag as PlaylistInfo;

                if (playlist != null)
                {
                    SelectedPlaylistId = playlist.Id;
                    DialogResult = DialogResult.OK;
                    Close();
                }
            }
        }

        private void cancelButton_Click(object sender, EventArgs e)
        {
            DialogResult = DialogResult.Cancel;
            Close();
        }
        private HashSet<string> CaptureCurrentPlaylistIds()
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);

            foreach (var playlist in _playlists)
            {
                var playlistId = playlist?.Id;
                if (!string.IsNullOrWhiteSpace(playlistId))
                {
                    var nonNullId = playlistId!;
                    ids.Add(nonNullId);
                }
            }

            return ids;
        }

        private async Task<PlaylistInfo?> CreatePlaylistWithVerificationAsync(string playlistName, HashSet<string> existingPlaylistIds)
        {
            PlaylistInfo? created = null;
            try
            {
                created = await _apiClient.CreatePlaylistAsync(playlistName);
            }
            catch
            {
                // 继续向后验证
            }

            if (created != null && !string.IsNullOrWhiteSpace(created.Id))
            {
                var verified = await WaitForPlaylistAvailabilityAsync(created.Id, playlistName, existingPlaylistIds);
                return verified ?? created;
            }

            return await WaitForPlaylistAvailabilityAsync(null, playlistName, existingPlaylistIds);
        }

        private async Task<PlaylistInfo?> WaitForPlaylistAvailabilityAsync(
            string? playlistId,
            string playlistName,
            HashSet<string> existingPlaylistIds,
            int maxAttempts = 5,
            int initialDelayMs = 300)
        {
            string normalizedName = playlistName?.Trim() ?? string.Empty;

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                if (attempt > 0)
                {
                    await Task.Delay(initialDelayMs + attempt * 200);
                }

                if (!string.IsNullOrWhiteSpace(playlistId))
                {
                    try
                    {
                        var targetPlaylistId = playlistId!;
                        var detail = await _apiClient.GetPlaylistDetailAsync(targetPlaylistId);
                        if (detail != null && !string.IsNullOrWhiteSpace(detail.Id))
                        {
                            return detail;
                        }
                    }
                    catch
                    {
                        // 接口可能暂时不可用，继续轮询
                    }
                }

                try
                {
                    var (playlists, _) = await _apiClient.GetUserPlaylistsAsync(_userId);
                    var matched = playlists?.FirstOrDefault(p =>
                        !string.IsNullOrWhiteSpace(p.Id) &&
                        (
                            (!string.IsNullOrWhiteSpace(playlistId) && string.Equals(p.Id, playlistId, StringComparison.Ordinal)) ||
                            (string.IsNullOrWhiteSpace(playlistId) &&
                             !existingPlaylistIds.Contains(p.Id) &&
                             string.Equals((p.Name ?? string.Empty).Trim(), normalizedName, StringComparison.OrdinalIgnoreCase))
                        ));

                    if (matched != null)
                    {
                        return matched;
                    }
                }
                catch
                {
                    // 忽略并继续
                }
            }

            return null;
        }

        private void RestoreCreatePlaylistUiState()
        {
            if (IsDisposed)
            {
                return;
            }

            loadingLabel.Visible = false;
            createPlaylistButton.Enabled = true;

            if (playlistListView.SelectedItems.Count > 0)
            {
                var selectedItem = playlistListView.SelectedItems[0];
                if (selectedItem?.Tag is PlaylistInfo playlist)
                {
                    bool songExists = _songInPlaylistCache.TryGetValue(playlist.Id, out var exists) && exists;
                    confirmButton.Enabled = !songExists;
                    return;
                }
            }

            confirmButton.Enabled = false;
        }
    }
}


