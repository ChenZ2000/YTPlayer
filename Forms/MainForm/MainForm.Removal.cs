#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using YTPlayer.Models;
using YTPlayer.Utils;

namespace YTPlayer
{
public partial class MainForm
{
    private readonly HashSet<string> _pendingListRemovals = new HashSet<string>(StringComparer.Ordinal);

    private bool CanDeletePlaylistFromView(PlaylistInfo playlist, string viewSource)
    {
        return IsUserLoggedIn() && string.Equals(viewSource, "user_playlists", StringComparison.OrdinalIgnoreCase) &&
            playlist != null && IsPlaylistCreatedByCurrentUser(playlist) && !IsLikedMusicPlaylist(playlist, GetCurrentUserId());
    }

    private bool CanRemovePlaylistSong(string viewSource)
    {
        if (string.IsNullOrWhiteSpace(viewSource) || !viewSource.StartsWith("playlist:", StringComparison.OrdinalIgnoreCase)) return false;
        string id = viewSource.Substring("playlist:".Length);
        return _currentPlaylist != null && string.Equals(_currentPlaylist.Id, id, StringComparison.OrdinalIgnoreCase) &&
            (_currentPlaylistOwnedByUser || IsPlaylistOwnedByUser(_currentPlaylist, GetCurrentUserId()));
    }

    private bool TryHandleRemoveShortcut()
    {
        if (!TryBuildListShortcutSnapshot(out MenuContextSnapshot snapshot, requireListFocus: true) || !snapshot.IsLoggedIn) return false;
        if (TryGetRecentResourceRemovalTarget(snapshot, out RecentResourceRemovalTarget recentTarget))
        {
            deleteRecentPlayMenuItem_Click(recentTarget, EventArgs.Empty);
            return true;
        }
        if (snapshot.PrimaryEntity == MenuEntityKind.Playlist)
        {
            if (!CanDeletePlaylistFromView(snapshot.Playlist, snapshot.ViewSource)) return false;
            deletePlaylistMenuItem_Click(snapshot.Playlist, EventArgs.Empty);
            return true;
        }
        if (snapshot.PrimaryEntity != MenuEntityKind.Song) return false;
        SongInfo song = ResolveSongFromShortcutSnapshot(snapshot);
        if (song == null || song.IsPodcastEpisode) return false;
        if (string.Equals(snapshot.ViewSource, "user_cloud", StringComparison.OrdinalIgnoreCase) && song.IsCloudSong && !string.IsNullOrWhiteSpace(song.CloudSongId))
            deleteFromCloudMenuItem_Click(song, EventArgs.Empty);
        else if (!CanSongUseLibraryFeatures(song)) return false;
        else if (string.Equals(snapshot.ViewSource, "recent_play", StringComparison.OrdinalIgnoreCase))
            deleteRecentPlayMenuItem_Click(song, EventArgs.Empty);
        else if (IsCurrentLikedSongsView() || CanRemovePlaylistSong(snapshot.ViewSource))
            removeFromPlaylistMenuItem_Click(song, EventArgs.Empty);
        else return false;
        return true;
    }

    private bool IsRemovalViewCurrent(string source, long accountId)
    {
        return !IsDisposed && accountId == GetCurrentUserId() && string.Equals(source, _currentViewSource, StringComparison.OrdinalIgnoreCase);
    }

    private bool RemoveSongFromVisibleList(Predicate<SongInfo> matches, bool playlistChanged = false)
    {
        int removedIndex = _currentSongs.FindIndex(matches);
        if (removedIndex < 0) return false;
        int selectedIndex = GetSelectedListViewItemSafe()?.Index ?? -1;
        var updated = new List<SongInfo>(_currentSongs);
        updated.RemoveAt(removedIndex);
        int targetIndex = ListRemovalSelection.ResolveIndex(selectedIndex, removedIndex, updated.Count);
        if (playlistChanged) BeginViewContentOperation(cancelNavigation: false);
        PatchSongs(updated, _currentSequenceStartIndex, pendingFocusIndex: targetIndex, allowSelection: false);
        CompleteListRemovalSelection(targetIndex, selectedIndex == removedIndex);
        if (playlistChanged && _currentPlaylist != null)
        {
            _currentPlaylist.Songs = new List<SongInfo>(_currentSongs);
            _currentPlaylist.TrackCount = Math.Max(_currentSongs.Count, _currentPlaylist.TrackCount - 1);
            var indices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _currentSongs.Count; i++)
                if (!string.IsNullOrWhiteSpace(_currentSongs[i].Id)) indices[_currentSongs[i].Id] = i;
            CachePlaylistTrackIndexMap(_currentViewSource, indices);
            // Only incomplete lists need another fetch; loaded songs remain in place.
            if (_currentSongs.Any(IsPlaceholderSong) || _currentPlaylist.TrackCount > _currentSongs.Count)
                EnrichCurrentPlaylistSongsAsync(_currentPlaylist, new List<SongInfo>(_currentSongs), _currentViewSource, resultListView.AccessibleName)
                    .SafeFireAndForget("playlist enrichment after removal");
        }
        return true;
    }

    private void CompleteListRemovalSelection(int targetIndex, bool selectedItemRemoved)
    {
        // Patch methods reuse native rows. Explicitly update selection, item focus,
        // visibility and accessibility even when the numeric index did not change.
        if (targetIndex < 0) targetIndex = 0; // The existing empty-list placeholder.
        bool alreadyFocused = IsListSelectionAndFocusAt(targetIndex);
        EnsureListSelectionWithoutFocus(targetIndex);
        if (targetIndex < resultListView.Items.Count) resultListView.Items[targetIndex].EnsureVisible();
        if (selectedItemRemoved && alreadyFocused && resultListView.ContainsFocus)
            QueueFocusedListViewItemRefreshAnnouncement(targetIndex, interruptAnnouncement: false);
    }

    private void RemovePlaylistFromVisibleList(PlaylistInfo playlist)
    {
        if (_currentListItems.Count > 0)
        {
            int mixedIndex = _currentListItems.FindIndex(item => item.Playlist != null && string.Equals(item.Playlist.Id, playlist.Id, StringComparison.OrdinalIgnoreCase));
            if (mixedIndex < 0) return;
            int selected = GetSelectedListViewItemSafe()?.Index ?? -1;
            var mixed = new List<ListItemInfo>(_currentListItems);
            mixed.RemoveAt(mixedIndex);
            int target = ListRemovalSelection.ResolveIndex(selected, mixedIndex, mixed.Count);
            PatchListItems(mixed, pendingFocusIndex: target);
            CompleteListRemovalSelection(target, selected == mixedIndex);
            return;
        }
        int index = _currentPlaylists.FindIndex(item => string.Equals(item.Id, playlist.Id, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;
        int selectedIndex = GetSelectedListViewItemSafe()?.Index ?? -1;
        var updated = new List<PlaylistInfo>(_currentPlaylists);
        updated.RemoveAt(index);
        bool userPlaylists = string.Equals(_currentViewSource, "user_playlists", StringComparison.OrdinalIgnoreCase);
        if (userPlaylists)
        {
            BeginViewContentOperation(cancelNavigation: false);
            _userPlaylistsProactiveLoadGeneration++;
            _userPlaylistsLoadMoreInFlight = false;
            _userPlaylistsProactiveLoadRunning = false;
            _currentUserPlaylistsNextOffset = Math.Max(0, _currentUserPlaylistsNextOffset - 1);
            _currentUserPlaylistsTotalCount = Math.Max(updated.Count, _currentUserPlaylistsTotalCount - 1);
        }
        int targetIndex = ListRemovalSelection.ResolveIndex(selectedIndex, index, updated.Count);
        bool previousPage = resultListView.Items.Cast<ListViewItem>().Any(item => item.Tag is int tag && tag == -2);
        bool nextPage = resultListView.Items.Cast<ListViewItem>().Any(item => item.Tag is int tag && tag == -3);
        PatchPlaylists(updated, _currentSequenceStartIndex, showPagination: previousPage || nextPage,
            hasPreviousPage: previousPage, hasNextPage: nextPage, pendingFocusIndex: targetIndex, allowSelection: false);
        CompleteListRemovalSelection(targetIndex, selectedIndex == index);
        if (userPlaylists) StartUserPlaylistsProactiveExpansion(_userPlaylistsProactiveLoadGeneration);
    }
}
}
