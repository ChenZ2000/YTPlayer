using System;
using System.Collections.Generic;
using System.Threading;
using YTPlayer.Core;
using YTPlayer.Models;

namespace YTPlayer
{
public partial class MainForm
{
    private sealed class PendingNavigationFocus
    {
        internal string ViewSource = string.Empty;
        internal int Index;
        internal string? ItemKey;
        internal bool Initializing;
        internal bool Bound;
        internal CancellationToken Token;
    }

    private PendingNavigationFocus? _pendingNavigationFocus;
    private bool _navigationFocusApplyQueued;
    private bool _applyingNavigationFocus;
    private readonly object _listPopulationLock = new object();
    private readonly Dictionary<CancellationToken, int> _listPopulations = new Dictionary<CancellationToken, int>();

    // A scope spans the entire population operation, including gaps between pages.
    // It is separate from metadata/availability work that cannot add or remove rows.
    private IDisposable TrackListPopulation()
    {
        CancellationToken token = GetCurrentViewContentToken();
        lock (_listPopulationLock)
        {
            _listPopulations.TryGetValue(token, out int count);
            _listPopulations[token] = count + 1;
        }
        return new ListPopulationScope(this, token);
    }

    private sealed class ListPopulationScope : IDisposable
    {
        private MainForm? _owner;
        private readonly CancellationToken _token;
        internal ListPopulationScope(MainForm owner, CancellationToken token) { _owner = owner; _token = token; }
        public void Dispose()
        {
            MainForm? owner = Interlocked.Exchange(ref _owner, null);
            if (owner == null) return;
            lock (owner._listPopulationLock)
            {
                if (owner._listPopulations.TryGetValue(_token, out int count))
                {
                    if (count <= 1) owner._listPopulations.Remove(_token);
                    else owner._listPopulations[_token] = count - 1;
                }
            }
            if (!_token.IsCancellationRequested) owner.QueuePendingNavigationFocus();
        }
    }

    private void SetPendingNavigationRestoreFocus(string? viewSource, int index, string? itemKey = null, bool initializing = false)
    {
        _pendingNavigationFocus = string.IsNullOrWhiteSpace(viewSource) || index < 0 ? null : new PendingNavigationFocus
        { ViewSource = viewSource!, Index = index, ItemKey = itemKey, Initializing = initializing };
    }

    private void BindPendingNavigationFocus(string? viewSource, CancellationToken token)
    {
        var pending = _pendingNavigationFocus;
        if (pending == null || !IsSameViewSourceForFocus(pending.ViewSource, viewSource)) return;
        if (pending.Bound && pending.Token != token) { ClearPendingNavigationRestoreFocus(); return; }
        pending.Token = token;
        pending.Bound = true;
    }

    private bool HasPendingNavigationRestoreFocus(string? viewSource) => TryGetPendingNavigationRestoreFocus(viewSource, out _);

    private bool TryGetPendingNavigationRestoreFocus(string? viewSource, out int index)
    {
        index = -1;
        var pending = _pendingNavigationFocus;
        if (pending == null || string.IsNullOrWhiteSpace(viewSource) || !IsSameViewSourceForFocus(pending.ViewSource, viewSource)) return false;
        if (pending.Bound && (pending.Token.IsCancellationRequested || pending.Token != GetCurrentViewContentToken()))
        {
            ClearPendingNavigationRestoreFocus();
            return false;
        }
        index = pending.Index;
        return true;
    }

    private void ClearPendingNavigationRestoreFocus(string? viewSource = null)
    {
        if (viewSource == null || (_pendingNavigationFocus != null && IsSameViewSourceForFocus(_pendingNavigationFocus.ViewSource, viewSource)))
            _pendingNavigationFocus = null;
    }

    private void CancelNavigationFocusForUserInput()
    {
        if (!HasPendingNavigationRestoreFocus(_currentViewSource)) return;
        ClearPendingNavigationRestoreFocus();
        if (!_pendingListFocusFromPlayback && IsSameViewSourceForFocus(_pendingListFocusViewSource, _currentViewSource))
        {
            _pendingListFocusIndex = -1;
            _pendingListFocusViewSource = null;
        }
    }

    private bool IsListPopulationActive(CancellationToken token)
    {
        lock (_listPopulationLock) return _listPopulations.ContainsKey(token);
    }

    private string? GetNavigationItemKey(int index)
    {
        if (resultListView == null || index < 0 || index >= resultListView.Items.Count) return null;
        // Avoid materializing virtual ListViewItems while scanning a large song list.
        if (resultListView.VirtualMode && _isVirtualSongListActive)
            return index < _virtualSongs.Count ? NavigationItemKey(_virtualSongs[index]) : null;
        object? item = resultListView.Items[index].Tag;
        if (item is int dataIndex && dataIndex >= 0 && dataIndex < _currentSongs.Count) item = _currentSongs[dataIndex];
        return NavigationItemKey(item);
    }

    private static string? NavigationItemKey(object? item)
    {
        switch (item)
        {
            case SongInfo song:
                string? songKey = ResolveSongFocusKey(song);
                return string.IsNullOrEmpty(songKey) ? null : "song:" + songKey;
            case ArtistInfo artist when artist.Id > 0: return "artist:" + artist.Id;
            case PlaylistInfo playlist when !string.IsNullOrEmpty(playlist.Id): return "playlist:" + playlist.Id;
            case AlbumInfo album when !string.IsNullOrEmpty(album.Id): return "album:" + album.Id;
            case PodcastRadioInfo podcast when podcast.Id > 0: return "podcast:" + podcast.Id;
            case PodcastEpisodeInfo episode when episode.ProgramId > 0: return "episode:" + episode.ProgramId;
            case ListItemInfo mixed:
                return mixed.Type switch
                {
                    ListItemType.Song => NavigationItemKey(mixed.Song),
                    ListItemType.Playlist => NavigationItemKey(mixed.Playlist),
                    ListItemType.Album => NavigationItemKey(mixed.Album),
                    ListItemType.Artist => NavigationItemKey(mixed.Artist),
                    ListItemType.Podcast => NavigationItemKey(mixed.Podcast),
                    ListItemType.PodcastEpisode => NavigationItemKey(mixed.PodcastEpisode),
                    ListItemType.Category when !string.IsNullOrEmpty(mixed.CategoryId) => "category:" + mixed.CategoryId,
                    _ => null
                };
            default: return null;
        }
    }

    private int ResolveNavigationFocusIndex()
    {
        var pending = _pendingNavigationFocus;
        if (pending == null || resultListView == null || resultListView.Items.Count == 0) return -1;
        if (!string.IsNullOrEmpty(pending.ItemKey))
        {
            if (string.Equals(GetNavigationItemKey(pending.Index), pending.ItemKey, StringComparison.Ordinal)) return pending.Index;
            for (int i = 0; i < resultListView.Items.Count; i++)
                if (string.Equals(GetNavigationItemKey(i), pending.ItemKey, StringComparison.Ordinal)) return i;
        }
        else if (pending.Index < resultListView.Items.Count && GetNavigationItemKey(pending.Index) != null)
            return pending.Index;

        // An unavailable row is not a deleted row while data is still arriving.
        if (pending.Initializing || !pending.Bound) return -1;
        if (IsListPopulationActive(pending.Token)) return -1;

        int fallback = Math.Min(pending.Index, resultListView.Items.Count - 1);
        // Pagination and loading rows must not become the fallback for a removed resource.
        for (int i = fallback; i >= 0; i--)
            if (GetNavigationItemKey(i) != null) return i;
        for (int i = fallback + 1; i < resultListView.Items.Count; i++)
            if (GetNavigationItemKey(i) != null) return i;
        return 0; // Completed empty/error view: retain access to its explanatory row.
    }

    private bool ApplyPendingNavigationRestoreFocus(string? viewSource, bool consume = true)
    {
        if (!HasPendingNavigationRestoreFocus(viewSource)) return false;
        var pending = _pendingNavigationFocus!;
        int index = ResolveNavigationFocusIndex();
        if (index < 0) return true; // Handled, but still waiting; suppress other fallback focus.
        _applyingNavigationFocus = true;
        try
        {
            if (!IsListSelectionAndFocusAt(index)) EnsureListSelectionWithoutFocus(index);
            pending.Index = index;
            _lastListViewFocusedIndex = index;
        }
        finally { _applyingNavigationFocus = false; }
        // Later pages can reorder rows (for example, owned playlists precede subscriptions).
        // Follow the resource until population completes, unless the user chooses another row.
        if (consume && !pending.Initializing && !IsListPopulationActive(pending.Token))
            ClearPendingNavigationRestoreFocus(viewSource);
        return true;
    }

    private void QueuePendingNavigationFocus()
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(new Action(QueuePendingNavigationFocus)); }
            catch (InvalidOperationException) { }
            return;
        }
        var pending = _pendingNavigationFocus;
        if (pending == null || _navigationFocusApplyQueued) return;
        _navigationFocusApplyQueued = true;
        BeginInvoke(new Action(() =>
        {
            _navigationFocusApplyQueued = false;
            if (IsDisposed || Disposing) return;
            if (!ReferenceEquals(pending, _pendingNavigationFocus)) { QueuePendingNavigationFocus(); return; }
            if (!pending.Initializing && !IsListAutoFocusSuppressed)
                ApplyPendingNavigationRestoreFocus(_currentViewSource);
        }));
    }
}
}
