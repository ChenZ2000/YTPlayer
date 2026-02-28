using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using YTPlayer.Core;
using YTPlayer.Models;
using YTPlayer.Utils;
using MessageBox = YTPlayer.MessageBox;

namespace YTPlayer.Forms
{
    /// <summary>
    /// 添加歌曲到歌单对话框（支持多选歌单批量添加）。
    /// </summary>
    public partial class AddToPlaylistDialog : Form
    {
        private const int MaxConcurrentPlaylistChecks = 8;
        private const string ListLoadingPlaceholderText = "正在加载歌单...";
        private static readonly TimeSpan ContainmentCacheTtl = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan IncompleteContainmentCacheTtl = TimeSpan.FromSeconds(20);
        private static readonly object ContainmentCacheLock = new object();
        private static readonly Dictionary<string, ContainmentCacheEntry> ContainmentCache =
            new Dictionary<string, ContainmentCacheEntry>(StringComparer.Ordinal);

        private readonly NeteaseApiClient _apiClient;
        private readonly string _songId;
        private readonly long _userId;
        private List<PlaylistInfo> _playlists = new List<PlaylistInfo>();
        private CancellationTokenSource? _loadCts;
        private bool _suppressItemCheckUpdate;

        private sealed class ContainmentCacheEntry
        {
            public bool ContainsSong { get; set; }

            public DateTime ExpiresUtc { get; set; }
        }

        private readonly struct PlaylistCandidate
        {
            public PlaylistCandidate(int index, PlaylistInfo playlist)
            {
                Index = index;
                Playlist = playlist;
            }

            public int Index { get; }

            public PlaylistInfo Playlist { get; }
        }

        private readonly struct PlaylistEvaluation
        {
            public PlaylistEvaluation(int index, PlaylistInfo playlist, bool containsSong)
            {
                Index = index;
                Playlist = playlist;
                ContainsSong = containsSong;
            }

            public int Index { get; }

            public PlaylistInfo Playlist { get; }

            public bool ContainsSong { get; }
        }

        private sealed class PlaylistListEntry
        {
            public PlaylistListEntry(PlaylistInfo playlist)
            {
                Playlist = playlist;
            }

            public PlaylistInfo Playlist { get; }

            public override string ToString()
            {
                string name = string.IsNullOrWhiteSpace(Playlist.Name) ? $"歌单 {Playlist.Id}" : Playlist.Name;
                int count = Playlist.TrackCount;
                return count > 0 ? $"{name}（{count} 首）" : name;
            }
        }

        private sealed class PlaylistPlaceholderEntry
        {
            public PlaylistPlaceholderEntry(string text)
            {
                Text = string.IsNullOrWhiteSpace(text) ? ListLoadingPlaceholderText : text.Trim();
            }

            public string Text { get; }

            public override string ToString()
            {
                return Text;
            }
        }

        /// <summary>
        /// 选中的歌单ID列表。
        /// </summary>
        public List<string> SelectedPlaylistIds { get; } = new List<string>();

        /// <summary>
        /// 兼容旧调用路径，返回第一个选中的歌单ID。
        /// </summary>
        public string? SelectedPlaylistId => SelectedPlaylistIds.Count > 0 ? SelectedPlaylistIds[0] : null;

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

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            BeginInvoke(new Action(() =>
            {
                try
                {
                    Activate();
                    BringToFront();
                }
                catch
                {
                }

                if (playlistCheckedListBox.CanFocus)
                {
                    playlistCheckedListBox.Focus();
                }
            }));
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            CancelCurrentLoad();
            base.OnFormClosed(e);
        }

        private async Task LoadPlaylistsAsync(string? autoCheckPlaylistId = null)
        {
            var loadCts = BeginNewLoad();
            var cancellationToken = loadCts.Token;

            try
            {
                if (_userId <= 0)
                {
                    const string message = "无法获取当前账号信息，请重新登录后重试。";
                    loadingLabel.Text = message;
                    loadingLabel.Visible = true;
                    ShowPlaceholderEntry(message);
                    UpdateSelectionSummary();
                    return;
                }

                SetLoadingState(ListLoadingPlaceholderText, keepListFocusable: true);

                var (allPlaylists, _) = await _apiClient.GetUserPlaylistsAsync(_userId);
                if (cancellationToken.IsCancellationRequested || IsDisposed)
                {
                    return;
                }

                // 保持 API 原始顺序，仅过滤为当前用户创建/拥有歌单
                _playlists = allPlaylists?.Where(IsOwnedPlaylist).ToList() ?? new List<PlaylistInfo>();

                if (_playlists.Count == 0)
                {
                    const string message = "未找到您创建的歌单，请先创建一个歌单。";
                    loadingLabel.Text = message;
                    loadingLabel.Visible = true;
                    ShowPlaceholderEntry(message);
                    UpdateSelectionSummary();
                    return;
                }

                await CheckAndPopulatePlaylistsAsync(autoCheckPlaylistId, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // 关闭对话框或重新加载时取消属于预期行为
            }
            catch (Exception ex)
            {
                if (!IsDisposed)
                {
                    loadingLabel.Text = "加载歌单失败，请稍后重试。";
                    loadingLabel.Visible = true;
                    ShowPlaceholderEntry("加载歌单失败，请稍后重试。");
                    MessageBox.Show(this, $"加载歌单失败: {ex.Message}", "错误",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            finally
            {
                if (ReferenceEquals(_loadCts, loadCts))
                {
                    bool hasAnyItems = playlistCheckedListBox.Items.Count > 0;
                    bool hasSelectableItems = GetSelectablePlaylistItemCount() > 0;
                    createPlaylistButton.Enabled = true;
                    playlistCheckedListBox.Enabled = hasAnyItems;
                    btnSelectAll.Enabled = hasSelectableItems;
                    btnUnselectAll.Enabled = hasSelectableItems;
                    btnInvertSelection.Enabled = hasSelectableItems;
                    UpdateSelectionSummary();

                    loadCts.Dispose();
                    _loadCts = null;
                }
            }
        }

        private bool IsOwnedPlaylist(PlaylistInfo playlist)
        {
            return playlist != null && (playlist.CreatorId == _userId || playlist.OwnerUserId == _userId);
        }

        private async Task CheckAndPopulatePlaylistsAsync(string? autoCheckPlaylistId, CancellationToken cancellationToken)
        {
            var candidates = _playlists
                .Where(p => p != null && !string.IsNullOrWhiteSpace(p.Id))
                .Select((p, index) => new PlaylistCandidate(index, p))
                .ToList();

            if (candidates.Count == 0)
            {
                const string message = "未找到可用歌单。";
                loadingLabel.Text = message;
                loadingLabel.Visible = true;
                ShowPlaceholderEntry(message);
                UpdateSelectionSummary();
                return;
            }

            ShowPlaceholderEntry("正在检查歌单...");
            int total = candidates.Count;
            int finished = 0;
            int addable = 0;
            var evaluations = new PlaylistEvaluation[total];
            var hasEvaluations = new bool[total];

            loadingLabel.Text = $"正在检查歌单... (0/{total})";
            loadingLabel.Visible = true;

            using var semaphore = new SemaphoreSlim(MaxConcurrentPlaylistChecks);
            var tasks = candidates.Select(candidate => EvaluatePlaylistAsync(candidate, semaphore, cancellationToken)).ToList();

            while (tasks.Count > 0)
            {
                var completedTask = await Task.WhenAny(tasks);
                tasks.Remove(completedTask);

                PlaylistEvaluation evaluation;
                try
                {
                    evaluation = await completedTask;
                }
                catch (OperationCanceledException)
                {
                    if (cancellationToken.IsCancellationRequested || IsDisposed)
                    {
                        return;
                    }
                    continue;
                }

                if (cancellationToken.IsCancellationRequested || IsDisposed)
                {
                    return;
                }

                hasEvaluations[evaluation.Index] = true;
                evaluations[evaluation.Index] = evaluation;
                finished++;
                if (!evaluation.ContainsSong)
                {
                    addable++;
                }

                loadingLabel.Text = $"正在检查歌单... ({finished}/{total})，可添加 {addable} 个";
            }

            _suppressItemCheckUpdate = true;
            try
            {
                playlistCheckedListBox.BeginUpdate();
                try
                {
                    playlistCheckedListBox.Items.Clear();
                    for (int i = 0; i < total; i++)
                    {
                        if (!hasEvaluations[i])
                        {
                            continue;
                        }

                        PlaylistEvaluation evaluation = evaluations[i];
                        if (evaluation.ContainsSong)
                        {
                            continue;
                        }

                        int itemIndex = playlistCheckedListBox.Items.Add(new PlaylistListEntry(evaluation.Playlist), false);
                        if (!string.IsNullOrWhiteSpace(autoCheckPlaylistId) &&
                            string.Equals(evaluation.Playlist.Id, autoCheckPlaylistId, StringComparison.Ordinal))
                        {
                            playlistCheckedListBox.SetItemChecked(itemIndex, true);
                            playlistCheckedListBox.SelectedIndex = itemIndex;
                        }
                    }
                }
                finally
                {
                    playlistCheckedListBox.EndUpdate();
                }
            }
            finally
            {
                _suppressItemCheckUpdate = false;
            }

            if (playlistCheckedListBox.Items.Count == 0)
            {
                const string message = "所有歌单均已包含该歌曲，可新建歌单。";
                loadingLabel.Text = message;
                loadingLabel.Visible = true;
                ShowPlaceholderEntry(message);
            }
            else
            {
                loadingLabel.Visible = false;
                if (playlistCheckedListBox.SelectedIndex < 0)
                {
                    playlistCheckedListBox.SelectedIndex = 0;
                }
            }

            UpdateSelectionSummary();
        }

        private async Task<PlaylistEvaluation> EvaluatePlaylistAsync(PlaylistCandidate candidate, SemaphoreSlim semaphore, CancellationToken cancellationToken)
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                string cacheKey = BuildContainmentCacheKey(candidate.Playlist.Id, _songId);
                if (TryGetContainmentCache(cacheKey, out bool cachedContains))
                {
                    return new PlaylistEvaluation(candidate.Index, candidate.Playlist, cachedContains);
                }

                var checkResult = await _apiClient.CheckPlaylistContainmentByTrackIdsAsync(
                    candidate.Playlist.Id,
                    _songId,
                    enforceCompletenessCheck: true,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                TimeSpan ttl = checkResult.IsTrackIdsComplete ? ContainmentCacheTtl : IncompleteContainmentCacheTtl;
                SetContainmentCache(cacheKey, checkResult.ContainsSong, ttl);

                return new PlaylistEvaluation(candidate.Index, candidate.Playlist, checkResult.ContainsSong);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AddToPlaylist] 检查歌单 {candidate.Playlist.Id} 是否包含歌曲失败: {ex.Message}");
                // 失败时按“不包含”处理，避免误伤可添加歌单
                return new PlaylistEvaluation(candidate.Index, candidate.Playlist, false);
            }
            finally
            {
                semaphore.Release();
            }
        }

        private static string BuildContainmentCacheKey(string playlistId, string songId)
        {
            return $"{playlistId}|{songId}";
        }

        private static bool TryGetContainmentCache(string key, out bool containsSong)
        {
            containsSong = false;
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            lock (ContainmentCacheLock)
            {
                if (!ContainmentCache.TryGetValue(key, out var entry))
                {
                    return false;
                }

                if (entry.ExpiresUtc <= DateTime.UtcNow)
                {
                    ContainmentCache.Remove(key);
                    return false;
                }

                containsSong = entry.ContainsSong;
                return true;
            }
        }

        private static void SetContainmentCache(string key, bool containsSong, TimeSpan ttl)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            DateTime expires = DateTime.UtcNow.Add(ttl <= TimeSpan.Zero ? TimeSpan.FromSeconds(10) : ttl);
            lock (ContainmentCacheLock)
            {
                ContainmentCache[key] = new ContainmentCacheEntry
                {
                    ContainsSong = containsSong,
                    ExpiresUtc = expires
                };
            }
        }

        private void SetLoadingState(string text, bool keepListFocusable = false)
        {
            loadingLabel.Text = text;
            loadingLabel.Visible = true;
            createPlaylistButton.Enabled = false;
            confirmButton.Enabled = false;
            if (keepListFocusable)
            {
                ShowPlaceholderEntry(text);
                playlistCheckedListBox.Enabled = true;
            }
            else
            {
                playlistCheckedListBox.Enabled = false;
            }
            btnSelectAll.Enabled = false;
            btnUnselectAll.Enabled = false;
            btnInvertSelection.Enabled = false;
        }

        private void ShowPlaceholderEntry(string text)
        {
            string placeholderText = string.IsNullOrWhiteSpace(text) ? ListLoadingPlaceholderText : text.Trim();

            _suppressItemCheckUpdate = true;
            try
            {
                playlistCheckedListBox.BeginUpdate();
                playlistCheckedListBox.Items.Clear();
                playlistCheckedListBox.Items.Add(new PlaylistPlaceholderEntry(placeholderText), false);
                if (playlistCheckedListBox.Items.Count > 0)
                {
                    playlistCheckedListBox.SelectedIndex = 0;
                }
            }
            finally
            {
                playlistCheckedListBox.EndUpdate();
                _suppressItemCheckUpdate = false;
            }
        }

        private static bool IsSelectablePlaylistItem(object? item)
        {
            return item is PlaylistListEntry;
        }

        private int GetSelectablePlaylistItemCount()
        {
            return playlistCheckedListBox.Items.Cast<object>().Count(IsSelectablePlaylistItem);
        }

        private int GetCheckedSelectablePlaylistCount()
        {
            int checkedCount = 0;
            foreach (int index in playlistCheckedListBox.CheckedIndices)
            {
                if (index >= 0 &&
                    index < playlistCheckedListBox.Items.Count &&
                    IsSelectablePlaylistItem(playlistCheckedListBox.Items[index]))
                {
                    checkedCount++;
                }
            }

            return checkedCount;
        }

        private CancellationTokenSource BeginNewLoad()
        {
            var current = new CancellationTokenSource();
            var previous = Interlocked.Exchange(ref _loadCts, current);
            if (previous != null)
            {
                try
                {
                    previous.Cancel();
                }
                catch
                {
                }

                previous.Dispose();
            }

            return current;
        }

        private void CancelCurrentLoad()
        {
            var cts = Interlocked.Exchange(ref _loadCts, null);
            if (cts == null)
            {
                return;
            }

            try
            {
                cts.Cancel();
            }
            catch
            {
            }

            cts.Dispose();
        }

        private void UpdateSelectionSummary()
        {
            int total = GetSelectablePlaylistItemCount();
            int selected = GetCheckedSelectablePlaylistCount();
            selectionInfoLabel.Text = $"已选择 {selected}/{total} 项";
            confirmButton.Enabled = selected > 0;
        }

        private void playlistCheckedListBox_ItemCheck(object sender, ItemCheckEventArgs e)
        {
            if (_suppressItemCheckUpdate || IsDisposed)
            {
                return;
            }

            if (e.Index < 0 || e.Index >= playlistCheckedListBox.Items.Count)
            {
                return;
            }

            if (!IsSelectablePlaylistItem(playlistCheckedListBox.Items[e.Index]))
            {
                e.NewValue = e.CurrentValue;
                return;
            }

            BeginInvoke(new Action(UpdateSelectionSummary));
        }

        private void btnSelectAll_Click(object sender, EventArgs e)
        {
            ApplyBulkSelection(current => true);
        }

        private void btnUnselectAll_Click(object sender, EventArgs e)
        {
            ApplyBulkSelection(current => false);
        }

        private void btnInvertSelection_Click(object sender, EventArgs e)
        {
            ApplyBulkSelection(current => !current);
        }

        private void ApplyBulkSelection(Func<bool, bool> selector)
        {
            if (GetSelectablePlaylistItemCount() == 0)
            {
                return;
            }

            _suppressItemCheckUpdate = true;
            try
            {
                playlistCheckedListBox.BeginUpdate();
                for (int i = 0; i < playlistCheckedListBox.Items.Count; i++)
                {
                    if (!IsSelectablePlaylistItem(playlistCheckedListBox.Items[i]))
                    {
                        continue;
                    }

                    bool current = playlistCheckedListBox.GetItemChecked(i);
                    bool target = selector(current);
                    if (current != target)
                    {
                        playlistCheckedListBox.SetItemChecked(i, target);
                    }
                }
            }
            finally
            {
                playlistCheckedListBox.EndUpdate();
                _suppressItemCheckUpdate = false;
            }

            UpdateSelectionSummary();
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
            SetLoadingState("正在创建歌单...", keepListFocusable: false);

            try
            {
                var verifiedPlaylist = await CreatePlaylistWithVerificationAsync(trimmedName, existingIds);
                if (verifiedPlaylist != null && !string.IsNullOrWhiteSpace(verifiedPlaylist.Id))
                {
                    MessageBox.Show(this, $"歌单 \"{verifiedPlaylist.Name}\" 创建成功，已自动勾选。", "成功",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    await LoadPlaylistsAsync(verifiedPlaylist.Id);
                    return;
                }

                MessageBox.Show(this, "新建歌单失败，请稍后重试。", "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"新建歌单失败: {ex.Message}", "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            RestoreCreatePlaylistUiState();
        }

        private void confirmButton_Click(object sender, EventArgs e)
        {
            SelectedPlaylistIds.Clear();

            for (int i = 0; i < playlistCheckedListBox.Items.Count; i++)
            {
                if (!playlistCheckedListBox.GetItemChecked(i))
                {
                    continue;
                }

                if (playlistCheckedListBox.Items[i] is PlaylistListEntry entry &&
                    entry.Playlist != null &&
                    !string.IsNullOrWhiteSpace(entry.Playlist.Id))
                {
                    SelectedPlaylistIds.Add(entry.Playlist.Id);
                }
            }

            if (SelectedPlaylistIds.Count == 0)
            {
                MessageBox.Show(this, "请至少选择一个歌单。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            DialogResult = DialogResult.OK;
            Close();
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
                if (playlist != null && !string.IsNullOrWhiteSpace(playlist.Id))
                {
                    ids.Add(playlist.Id);
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
                        var detail = await _apiClient.GetPlaylistDetailAsync(playlistId);
                        if (detail != null && !string.IsNullOrWhiteSpace(detail.Id))
                        {
                            return detail;
                        }
                    }
                    catch
                    {
                        // 接口可能短暂不可用，继续轮询
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
                    // 忽略后继续轮询
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

            bool hasAnyItems = playlistCheckedListBox.Items.Count > 0;
            bool hasSelectableItems = GetSelectablePlaylistItemCount() > 0;
            loadingLabel.Visible = false;
            createPlaylistButton.Enabled = true;
            playlistCheckedListBox.Enabled = hasAnyItems;
            btnSelectAll.Enabled = hasSelectableItems;
            btnUnselectAll.Enabled = hasSelectableItems;
            btnInvertSelection.Enabled = hasSelectableItems;
            UpdateSelectionSummary();
        }
    }
}
