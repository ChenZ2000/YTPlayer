using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using YTPlayer.Models;
using YTPlayer.Utils;

namespace YTPlayer
{
public partial class MainForm
{
    private ToolStripMenuItem? _personalFmModeMenuItem;
    private (ToolStripItem Item, bool Available)[]? _personalFmHiddenMenuItems;
    private readonly Dictionary<string, ToolStripMenuItem> _personalFmModeItems = new Dictionary<string, ToolStripMenuItem>();
    private readonly Queue<SongInfo> _personalFmFetchedSongs = new Queue<SongInfo>();
    private CancellationTokenSource _personalFmModeCts = new CancellationTokenSource();
    private int _personalFmModeGeneration;
    private int _personalFmModeSwitchInFlight;
    private int _personalFmDisposed;

    private PersonalFmMode SelectedPersonalFmMode => PersonalFmMode.FromKey(_config?.PersonalFmModeKey);
    private string PersonalFmStatusName => PersonalFmAccessibleName + "（" + SelectedPersonalFmMode.Name + "）";

    private (PersonalFmMode Mode, int Generation, CancellationToken Token) CapturePersonalFmMode()
    {
        lock (_personalFmStateLock)
            return (SelectedPersonalFmMode, _personalFmModeGeneration,
                _personalFmDisposed != 0 ? new CancellationToken(canceled: true) : _personalFmModeCts.Token);
    }

    private void DisposePersonalFmRequests()
    {
        if (Interlocked.Exchange(ref _personalFmDisposed, 1) != 0) return;
        lock (_personalFmStateLock)
        {
            _personalFmModeCts.Cancel();
            _personalFmModeCts.Dispose();
            _personalFmFetchedSongs.Clear();
        }
    }

    private void ResetPersonalFmSession()
    {
        CancellationTokenSource previous;
        lock (_personalFmStateLock)
        {
            if (_personalFmDisposed != 0) return;
            previous = _personalFmModeCts;
            _personalFmModeCts = new CancellationTokenSource();
            _personalFmModeGeneration++;
            _personalFmFetchedSongs.Clear();
            ResetPersonalFmCache(null, -1);
        }
        previous.Cancel();
        previous.Dispose();
    }

    private void EnsurePersonalFmModeMenu()
    {
        if (_personalFmModeMenuItem != null) return;
        _personalFmModeMenuItem = new ToolStripMenuItem("推荐模式") { Name = "personalFmModeMenuItem", Visible = false };
        int insertIndex = songContextMenu.Items.IndexOf(dislikeSongMenuItem) + 1;
        songContextMenu.Items.Insert(Math.Max(0, insertIndex), _personalFmModeMenuItem);
        var groups = new Dictionary<string, ToolStripMenuItem>();
        foreach (PersonalFmMode mode in PersonalFmMode.All)
        {
            ToolStripMenuItem parent = _personalFmModeMenuItem;
            if (mode.Group != null)
            {
                if (!groups.TryGetValue(mode.Group, out var group))
                {
                    group = new ToolStripMenuItem(mode.Group);
                    groups.Add(mode.Group, group);
                    _personalFmModeMenuItem.DropDownItems.Add(group);
                }
                parent = group;
            }
            var item = new ToolStripMenuItem(mode.Name.Replace("&", "&&"))
                { Tag = mode, CheckOnClick = false, AccessibleName = mode.Name };
            item.Click += async (_, _) => await SwitchPersonalFmModeAsync(mode);
            parent.DropDownItems.Add(item);
            _personalFmModeItems.Add(mode.Key, item);
        }
        foreach (ToolStripMenuItem parent in groups.Values.Append(_personalFmModeMenuItem))
        {
            EnsureSortMenuCheckMargin(parent);
            ContextMenuAccessibilityHelper.PrimeForAccessibility(parent.DropDown);
            MenuNavigationBoundaryHelper.Attach(parent.DropDown);
        }
    }

    private void UpdatePersonalFmModeMenu(bool visible)
    {
        EnsurePersonalFmModeMenu();
        _personalFmModeMenuItem!.Visible = visible;
        _personalFmModeMenuItem.Enabled = IsUserLoggedIn() && Volatile.Read(ref _personalFmModeSwitchInFlight) == 0;
        PersonalFmMode selected = SelectedPersonalFmMode;
        _personalFmModeMenuItem.Text = "推荐模式（" + selected.Name.Replace("&", "&&") + "）";
        _personalFmModeMenuItem.AccessibleName = "推荐模式（" + selected.Name + "）";
        foreach (var pair in _personalFmModeItems)
            SetMenuItemCheckedState(pair.Value, pair.Key == selected.Key);
    }

    private void RestorePersonalFmMenuItems()
    {
        if (_personalFmHiddenMenuItems == null) return;
        foreach (var item in _personalFmHiddenMenuItems)
            if (!item.Item.IsDisposed) item.Item.Available = item.Available;
        _personalFmHiddenMenuItems = null;
    }

    private void ShowPersonalFmModeOnlyMenu()
    {
        _personalFmHiddenMenuItems = songContextMenu.Items.Cast<ToolStripItem>()
            .Select(item => (item, item.Available)).ToArray();
        foreach (ToolStripItem item in songContextMenu.Items) item.Visible = false;
        UpdatePersonalFmModeMenu(visible: true);
    }

    private async Task SwitchPersonalFmModeAsync(PersonalFmMode mode)
    {
        if (IsDisposed || Disposing || !IsPersonalFmViewSource(_currentViewSource) || !IsUserLoggedIn()
            || mode.Key == SelectedPersonalFmMode.Key) return;
        if (Interlocked.CompareExchange(ref _personalFmModeSwitchInFlight, 1, 0) != 0) return;
        CancellationToken viewToken = GetCurrentViewContentToken();
        string accountId = _accountState?.UserId ?? _loggedInUserId.ToString();
        CancellationTokenSource previous;
        int generation;
        lock (_personalFmStateLock)
        {
            previous = _personalFmModeCts;
            _personalFmModeCts = new CancellationTokenSource();
            generation = ++_personalFmModeGeneration;
            _personalFmFetchedSongs.Clear();
        }
        previous.Cancel();
        previous.Dispose();
        CancelNavigationFocusForUserInput();
        UpdatePersonalFmModeMenu(visible: true);
        string loading = "正在切换私人 FM 至" + mode.Name;
        UpdateStatusBar(loading);
        AnnounceUiMessage(loading);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(viewToken, CapturePersonalFmMode().Token);
            List<SongInfo> songs = await _apiClient.GetPersonalFMAsync(mode, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (generation != _personalFmModeGeneration || !IsPersonalFmViewSource(_currentViewSource)
                || accountId != (_accountState?.UserId ?? _loggedInUserId.ToString())) return;
            if (!songs.Any(IsPersonalFmSongCandidate))
                throw new InvalidOperationException("该模式暂未返回可用歌曲，请稍后重试");
            int added = ApplyPersonalFmBatch(songs);
            lock (_personalFmStateLock) EnsureConfigInitialized().PersonalFmModeKey = mode.Key;
            SaveConfig(refreshCookieFromClient: false);
            string completed = "已切换至" + mode.Name + (added > 0 ? $"，已追加 {added} 首歌曲" : "，后续将按此模式推荐");
            UpdateStatusBar(PersonalFmStatusName + $"，共 {_personalFmSongsCache.Count} 首");
            AnnounceUiMessage(completed);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!IsDisposed && !viewToken.IsCancellationRequested && IsPersonalFmViewSource(_currentViewSource))
            {
                UpdateStatusBar("私人 FM 模式切换失败，仍使用" + SelectedPersonalFmMode.Name);
                MessageBox.Show(this, "切换私人 FM 模式失败：" + ex.Message, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _personalFmModeSwitchInFlight, 0);
            if (!IsDisposed && !Disposing) UpdatePersonalFmModeMenu(IsPersonalFmViewSource(_currentViewSource));
        }
    }

    // Commit on the UI thread as one operation. Old-mode replies cannot leave cache-only rows
    // behind or append to a new visit after cancellation.
    private int ApplyPersonalFmBatch(IEnumerable<SongInfo> songs)
    {
        var added = new List<(SongInfo Song, int Index)>();
        foreach (SongInfo song in songs)
            if (TryAppendPersonalFmSongToCache(song, out int index)) added.Add((song, index));
        if (added.Count == 0) return 0;
        _playbackQueue?.TryAppendQueueSongs(added.Select(item => item.Song).ToList(), PersonalFmCategoryId);
        if (IsPersonalFmViewSource(_currentViewSource) && resultListView != null)
        {
            // An empty/error row is replaced; an existing FM list keeps its selection and focus.
            if (_currentSongs.Count == 0 || _currentSongs.Count != added[0].Index || resultListView.Items.Count != _currentSongs.Count
                || (resultListView.Items.Count == 1 && GetNavigationItemKey(0) == null))
            {
                int selected = Math.Max(0, GetSelectedListViewIndex());
                DisplaySongs(GetPersonalFmCachedSongsSnapshot(), false, false, 1, false,
                    PersonalFmCategoryId, PersonalFmAccessibleName, true, false, true, false);
                EnsureListSelectionWithoutFocus(selected);
            }
            else
            {
                foreach (var item in added)
                {
                    if (item.Index < _currentSongs.Count) continue;
                    if (item.Index != _currentSongs.Count) break;
                    _currentSongs.Add(item.Song);
                    AppendPersonalFmListViewItem(item.Song, item.Index);
                }
            }
            UpdateStatusBar(PersonalFmStatusName + $"，已扩展到 {_currentSongs.Count} 首");
        }
        return added.Count;
    }
}
}
