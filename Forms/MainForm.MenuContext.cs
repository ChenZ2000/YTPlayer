using System;
using System.Linq;
using System.Windows.Forms;
using YTPlayer.Core;
using YTPlayer.Models;

namespace YTPlayer
{
    public partial class MainForm
    {
        private enum MenuInvocationSource
        {
            ViewSelection,
            CurrentPlayback
        }

        private enum MenuEntityKind
        {
            None,
            Song,
            Playlist,
            Album,
            Artist,
            Podcast,
            PodcastEpisode,
            Category
        }

        private class MenuContextSnapshot
        {
            public bool IsValid { get; set; }
            public MenuInvocationSource InvocationSource { get; set; }
            public MenuEntityKind PrimaryEntity { get; set; }

            public SongInfo? Song { get; set; }
            public PlaylistInfo? Playlist { get; set; }
            public AlbumInfo? Album { get; set; }
            public ArtistInfo? Artist { get; set; }
            public PodcastRadioInfo? Podcast { get; set; }
            public PodcastEpisodeInfo? PodcastEpisode { get; set; }
            public ListItemInfo? ListItem { get; set; }
            public ListViewItem? SelectedListItem { get; set; }

            public bool IsLoggedIn { get; set; }
            public bool IsCloudView { get; set; }
            public bool IsMyPlaylistsView { get; set; }
            public bool IsUserAlbumsView { get; set; }
            public bool IsPodcastEpisodeView { get; set; }
            public bool IsArtistSongsView { get; set; }
            public bool IsArtistAlbumsView { get; set; }
            public string ViewSource { get; set; } = string.Empty;

            public bool IsCurrentPlayback => InvocationSource == MenuInvocationSource.CurrentPlayback;
            public bool HasPrimaryEntity => PrimaryEntity != MenuEntityKind.None;
        }

        private MenuContextSnapshot BuildMenuContextSnapshot(bool isCurrentPlayingRequest)
        {
            string currentView = _currentViewSource ?? string.Empty;
            bool hasViewSource = !string.IsNullOrWhiteSpace(currentView);

            var snapshot = new MenuContextSnapshot
            {
                InvocationSource = isCurrentPlayingRequest ? MenuInvocationSource.CurrentPlayback : MenuInvocationSource.ViewSelection,
                ViewSource = currentView,
                IsLoggedIn = IsUserLoggedIn(),
                IsCloudView = string.Equals(currentView, "user_cloud", StringComparison.OrdinalIgnoreCase),
                IsMyPlaylistsView = string.Equals(currentView, "user_playlists", StringComparison.OrdinalIgnoreCase),
                IsUserAlbumsView = string.Equals(currentView, "user_albums", StringComparison.OrdinalIgnoreCase),
                IsPodcastEpisodeView = IsPodcastEpisodeView(),
                IsArtistSongsView = hasViewSource && currentView.StartsWith("artist_songs:", StringComparison.OrdinalIgnoreCase),
                IsArtistAlbumsView = hasViewSource && currentView.StartsWith("artist_albums:", StringComparison.OrdinalIgnoreCase),
                PrimaryEntity = MenuEntityKind.None,
                IsValid = true
            };

            if (isCurrentPlayingRequest)
            {
                var song = _audioEngine?.CurrentSong;
                if (song == null)
                {
                    snapshot.IsValid = false;
                    return snapshot;
                }

                snapshot.Song = song;
                if (song.IsPodcastEpisode)
                {
                    snapshot.PrimaryEntity = MenuEntityKind.PodcastEpisode;
                    snapshot.PodcastEpisode = ResolvePodcastEpisodeFromSong(song);
                }
                else
                {
                    snapshot.PrimaryEntity = MenuEntityKind.Song;
                }

                return snapshot;
            }

            if (resultListView.SelectedItems.Count == 0)
            {
                snapshot.IsValid = false;
                return snapshot;
            }

            var listViewItem = resultListView.SelectedItems[0];
            snapshot.SelectedListItem = listViewItem;

            if (snapshot.IsPodcastEpisodeView && listViewItem.Tag is int pagerTag && pagerTag < 0)
            {
                snapshot.IsValid = false;
                return snapshot;
            }

            switch (listViewItem.Tag)
            {
                case PlaylistInfo playlist:
                    snapshot.PrimaryEntity = MenuEntityKind.Playlist;
                    snapshot.Playlist = playlist;
                    return snapshot;
                case AlbumInfo album:
                    snapshot.PrimaryEntity = MenuEntityKind.Album;
                    snapshot.Album = album;
                    return snapshot;
                case ArtistInfo artist:
                    snapshot.PrimaryEntity = MenuEntityKind.Artist;
                    snapshot.Artist = artist;
                    return snapshot;
                case PodcastRadioInfo podcast:
                    snapshot.PrimaryEntity = MenuEntityKind.Podcast;
                    snapshot.Podcast = podcast;
                    return snapshot;
                case PodcastEpisodeInfo episode:
                    snapshot.PrimaryEntity = MenuEntityKind.PodcastEpisode;
                    snapshot.PodcastEpisode = episode;
                    snapshot.Song = episode.Song;
                    return snapshot;
                case SongInfo song:
                    snapshot.PrimaryEntity = MenuEntityKind.Song;
                    snapshot.Song = song;
                    return snapshot;
                case ListItemInfo listItem:
                    snapshot.ListItem = listItem;
                    return ResolveListItemSnapshot(snapshot, listItem);
                case int songIndex when songIndex >= 0 && songIndex < _currentSongs.Count:
                    snapshot.PrimaryEntity = MenuEntityKind.Song;
                    snapshot.Song = _currentSongs[songIndex];
                    return snapshot;
                case int sentinel when sentinel < 0:
                    snapshot.IsValid = false;
                    return snapshot;
                default:
                    snapshot.IsValid = false;
                    return snapshot;
            }
        }

        private MenuContextSnapshot ResolveListItemSnapshot(MenuContextSnapshot snapshot, ListItemInfo listItem)
        {
            switch (listItem.Type)
            {
                case ListItemType.Playlist when listItem.Playlist != null:
                    snapshot.PrimaryEntity = MenuEntityKind.Playlist;
                    snapshot.Playlist = listItem.Playlist;
                    break;
                case ListItemType.Album when listItem.Album != null:
                    snapshot.PrimaryEntity = MenuEntityKind.Album;
                    snapshot.Album = listItem.Album;
                    break;
                case ListItemType.Artist when listItem.Artist != null:
                    snapshot.PrimaryEntity = MenuEntityKind.Artist;
                    snapshot.Artist = listItem.Artist;
                    break;
                case ListItemType.Podcast when listItem.Podcast != null:
                    snapshot.PrimaryEntity = MenuEntityKind.Podcast;
                    snapshot.Podcast = listItem.Podcast;
                    break;
                case ListItemType.PodcastEpisode when listItem.PodcastEpisode != null:
                    snapshot.PrimaryEntity = MenuEntityKind.PodcastEpisode;
                    snapshot.PodcastEpisode = listItem.PodcastEpisode;
                    snapshot.Song = listItem.PodcastEpisode.Song;
                    break;
                case ListItemType.Song when listItem.Song != null:
                    snapshot.PrimaryEntity = MenuEntityKind.Song;
                    snapshot.Song = listItem.Song;
                    break;
                case ListItemType.Category:
                    snapshot.PrimaryEntity = MenuEntityKind.Category;
                    break;
                default:
                    snapshot.PrimaryEntity = MenuEntityKind.None;
                    snapshot.IsValid = false;
                    break;
            }

            return snapshot;
        }

        private void ResetSongContextMenuState()
        {
            subscribePlaylistMenuItem.Visible = false;
            unsubscribePlaylistMenuItem.Visible = false;
            deletePlaylistMenuItem.Visible = false;
            createPlaylistMenuItem.Visible = false;
            subscribeAlbumMenuItem.Visible = false;
            unsubscribeAlbumMenuItem.Visible = false;
            subscribePodcastMenuItem.Visible = false;
            subscribePodcastMenuItem.Enabled = true;
            subscribePodcastMenuItem.Tag = null;
            unsubscribePodcastMenuItem.Visible = false;
            unsubscribePodcastMenuItem.Enabled = true;
            unsubscribePodcastMenuItem.Tag = null;
            likeSongMenuItem.Visible = false;
            likeSongMenuItem.Tag = null;
            unlikeSongMenuItem.Visible = false;
            unlikeSongMenuItem.Tag = null;
            addToPlaylistMenuItem.Visible = false;
            addToPlaylistMenuItem.Tag = null;
            removeFromPlaylistMenuItem.Visible = false;
            removeFromPlaylistMenuItem.Tag = null;
            insertPlayMenuItem.Visible = true;
            insertPlayMenuItem.Tag = null;
            if (refreshMenuItem != null)
            {
                refreshMenuItem.Visible = true;
                refreshMenuItem.Enabled = true;
            }

            downloadSongMenuItem.Visible = false;
            downloadSongMenuItem.Tag = null;
            downloadSongMenuItem.Text = DownloadSongMenuText;
            downloadPlaylistMenuItem.Visible = false;
            downloadAlbumMenuItem.Visible = false;
            batchDownloadMenuItem.Visible = false;
            downloadCategoryMenuItem.Visible = false;
            batchDownloadPlaylistsMenuItem.Visible = false;
            downloadPodcastMenuItem.Visible = false;
            downloadPodcastMenuItem.Tag = null;
            downloadLyricsMenuItem.Visible = false;
            downloadLyricsMenuItem.Tag = null;

            cloudMenuSeparator.Visible = false;
            uploadToCloudMenuItem.Visible = false;
            deleteFromCloudMenuItem.Visible = false;

            toolStripSeparatorArtist.Visible = false;
            shareArtistMenuItem.Visible = false;
            subscribeArtistMenuItem.Visible = false;
            unsubscribeArtistMenuItem.Visible = false;

            toolStripSeparatorView.Visible = false;
            commentMenuItem.Visible = false;
            commentMenuItem.Tag = null;
            commentMenuSeparator.Visible = false;

            viewSongArtistMenuItem.Visible = false;
            viewSongArtistMenuItem.Tag = null;
            viewSongAlbumMenuItem.Visible = false;
            viewSongAlbumMenuItem.Tag = null;
            if (viewPodcastMenuItem != null)
            {
                viewPodcastMenuItem.Visible = false;
                viewPodcastMenuItem.Tag = null;
            }

            shareSongMenuItem.Visible = false;
            shareSongMenuItem.Tag = null;
            shareSongWebMenuItem.Tag = null;
            shareSongDirectMenuItem.Tag = null;
            sharePlaylistMenuItem.Visible = false;
            sharePlaylistMenuItem.Tag = null;
            shareAlbumMenuItem.Visible = false;
            shareAlbumMenuItem.Tag = null;
            sharePodcastMenuItem.Visible = false;
            sharePodcastMenuItem.Tag = null;
            sharePodcastEpisodeMenuItem.Visible = false;
            sharePodcastEpisodeMenuItem.Tag = null;
            sharePodcastEpisodeWebMenuItem.Tag = null;
            sharePodcastEpisodeDirectMenuItem.Tag = null;

            podcastSortMenuItem.Visible = false;
            if (artistSongsSortMenuItem != null)
            {
                artistSongsSortMenuItem.Visible = false;
            }

            if (artistAlbumsSortMenuItem != null)
            {
                artistAlbumsSortMenuItem.Visible = false;
            }
        }

        private void ConfigureSortMenus(MenuContextSnapshot snapshot, ref bool showViewSection)
        {
            bool canSortPodcastEpisodes = !snapshot.IsCurrentPlayback &&
                                          snapshot.IsPodcastEpisodeView &&
                                          !string.IsNullOrWhiteSpace(snapshot.ViewSource) &&
                                          snapshot.ViewSource.StartsWith("podcast:", StringComparison.OrdinalIgnoreCase);
            podcastSortMenuItem.Visible = canSortPodcastEpisodes;
            if (canSortPodcastEpisodes)
            {
                UpdatePodcastSortMenuChecks();
            }

            if (artistSongsSortMenuItem != null)
            {
                bool isArtistSongsView = !snapshot.IsCurrentPlayback && snapshot.IsArtistSongsView;
                artistSongsSortMenuItem.Visible = isArtistSongsView;
                if (isArtistSongsView)
                {
                    UpdateArtistSongsSortMenuChecks();
                }
            }

            if (artistAlbumsSortMenuItem != null)
            {
                bool isArtistAlbumsView = !snapshot.IsCurrentPlayback && snapshot.IsArtistAlbumsView;
                artistAlbumsSortMenuItem.Visible = isArtistAlbumsView;
                if (isArtistAlbumsView)
                {
                    UpdateArtistAlbumsSortMenuChecks();
                }
            }

            if (podcastSortMenuItem.Visible ||
                (artistSongsSortMenuItem?.Visible ?? false) ||
                (artistAlbumsSortMenuItem?.Visible ?? false))
            {
                showViewSection = true;
            }
        }

        private void ConfigureCategoryMenu()
        {
            insertPlayMenuItem.Visible = false;
            downloadCategoryMenuItem.Visible = true;
        }

        private void ApplyViewContextFlags(MenuContextSnapshot snapshot, ref bool showViewSection)
        {
            if (snapshot.IsCloudView)
            {
                uploadToCloudMenuItem.Visible = true;
                cloudMenuSeparator.Visible = true;
            }

            if (!snapshot.IsCurrentPlayback && snapshot.IsMyPlaylistsView && snapshot.IsLoggedIn)
            {
                createPlaylistMenuItem.Visible = true;
            }

            ConfigureSortMenus(snapshot, ref showViewSection);
        }

        private void ConfigurePlaylistMenu(
            MenuContextSnapshot snapshot,
            bool isLoggedIn,
            ref bool showViewSection,
            ref CommentTarget? contextCommentTarget)
        {
            var playlist = snapshot.Playlist;
            if (playlist == null)
            {
                return;
            }

            bool isCreatedByCurrentUser = IsPlaylistCreatedByCurrentUser(playlist);
            bool isSubscribed = !isCreatedByCurrentUser && IsPlaylistSubscribed(playlist);

            if (isLoggedIn)
            {
                subscribePlaylistMenuItem.Visible = !isCreatedByCurrentUser && !isSubscribed;
                unsubscribePlaylistMenuItem.Visible = !isCreatedByCurrentUser && isSubscribed;
                deletePlaylistMenuItem.Visible = isCreatedByCurrentUser;
            }
            else
            {
                subscribePlaylistMenuItem.Visible = false;
                unsubscribePlaylistMenuItem.Visible = false;
                deletePlaylistMenuItem.Visible = false;
            }

            insertPlayMenuItem.Visible = false;
            downloadPlaylistMenuItem.Visible = true;
            batchDownloadPlaylistsMenuItem.Visible = true;

            sharePlaylistMenuItem.Visible = true;
            sharePlaylistMenuItem.Tag = playlist;
            showViewSection = true;

            if (!string.IsNullOrWhiteSpace(playlist.Id))
            {
                contextCommentTarget = new CommentTarget(
                    playlist.Id!,
                    CommentType.Playlist,
                    string.IsNullOrWhiteSpace(playlist.Name) ? "歌单" : playlist.Name,
                    playlist.Creator);
            }
        }

        private void ConfigureAlbumMenu(
            MenuContextSnapshot snapshot,
            bool isLoggedIn,
            ref bool showViewSection,
            ref CommentTarget? contextCommentTarget)
        {
            var album = snapshot.Album;
            if (album == null)
            {
                return;
            }

            if (isLoggedIn)
            {
                bool isSubscribed = IsAlbumSubscribed(album);
                subscribeAlbumMenuItem.Visible = !isSubscribed;
                unsubscribeAlbumMenuItem.Visible = isSubscribed;
            }
            else
            {
                subscribeAlbumMenuItem.Visible = false;
                unsubscribeAlbumMenuItem.Visible = false;
            }

            insertPlayMenuItem.Visible = false;
            downloadAlbumMenuItem.Visible = true;
            batchDownloadPlaylistsMenuItem.Visible = true;

            shareAlbumMenuItem.Visible = true;
            shareAlbumMenuItem.Tag = album;
            showViewSection = true;

            if (!string.IsNullOrWhiteSpace(album.Id))
            {
                contextCommentTarget = new CommentTarget(
                    album.Id!,
                    CommentType.Album,
                    string.IsNullOrWhiteSpace(album.Name) ? "专辑" : album.Name,
                    album.Artist);
            }
        }

        private void ConfigurePodcastMenu(
            MenuContextSnapshot snapshot,
            bool isLoggedIn,
            ref bool showViewSection)
        {
            var podcast = snapshot.Podcast;
            if (podcast == null)
            {
                return;
            }

            insertPlayMenuItem.Visible = false;
            ConfigurePodcastMenuItems(podcast, isLoggedIn);
            if (sharePodcastMenuItem.Visible)
            {
                showViewSection = true;
            }
        }

        private void ConfigureSongOrEpisodeMenu(
            MenuContextSnapshot snapshot,
            bool isLoggedIn,
            bool isCloudView,
            ref bool showViewSection,
            ref CommentTarget? contextCommentTarget,
            ref PodcastRadioInfo? contextPodcastForEpisode,
            ref PodcastEpisodeInfo? effectiveEpisode,
            ref bool isPodcastEpisodeContext)
        {
            insertPlayMenuItem.Visible = true;

            SongInfo? currentSong = snapshot.Song;
            if (snapshot.IsCurrentPlayback && _currentPlayingMenuSong != null)
            {
                currentSong = _currentPlayingMenuSong;
            }

            var activePodcastEpisode = snapshot.PodcastEpisode;
            if (currentSong == null && activePodcastEpisode?.Song != null)
            {
                currentSong = activePodcastEpisode.Song;
            }

            if (currentSong == null && activePodcastEpisode != null)
            {
                currentSong = EnsurePodcastEpisodeSong(activePodcastEpisode);
            }

            if (activePodcastEpisode != null)
            {
                effectiveEpisode = activePodcastEpisode;
                isPodcastEpisodeContext = true;
            }
            else if (currentSong?.IsPodcastEpisode == true)
            {
                isPodcastEpisodeContext = true;
                effectiveEpisode = ResolvePodcastEpisodeFromSong(currentSong);
            }

            if (effectiveEpisode != null)
            {
                contextPodcastForEpisode = ResolvePodcastFromEpisode(effectiveEpisode);
                currentSong = EnsurePodcastEpisodeSong(effectiveEpisode);
            }

            insertPlayMenuItem.Tag = currentSong;

            if (currentSong != null && !string.IsNullOrWhiteSpace(currentSong.Id) && !currentSong.IsCloudSong && !isPodcastEpisodeContext)
            {
                contextCommentTarget = new CommentTarget(
                    currentSong.Id,
                    CommentType.Song,
                    string.IsNullOrWhiteSpace(currentSong.Name) ? "歌曲" : currentSong.Name,
                    currentSong.Artist);
            }

            bool canUseLibraryFeatures = !isPodcastEpisodeContext && CanSongUseLibraryFeatures(currentSong);

            if (isCloudView && currentSong != null && currentSong.IsCloudSong)
            {
                deleteFromCloudMenuItem.Visible = true;
                cloudMenuSeparator.Visible = true;
            }
            else
            {
                deleteFromCloudMenuItem.Visible = false;
            }

            if (isLoggedIn)
            {
                bool isLikedSongsView = IsCurrentLikedSongsView();

                bool isLiked = isLikedSongsView;
                if (canUseLibraryFeatures && currentSong != null && !isLiked)
                {
                    isLiked = IsSongLiked(currentSong);
                }

                likeSongMenuItem.Visible = canUseLibraryFeatures && !isLiked;
                unlikeSongMenuItem.Visible = canUseLibraryFeatures && isLiked;

                likeSongMenuItem.Tag = canUseLibraryFeatures ? currentSong : null;
                unlikeSongMenuItem.Tag = canUseLibraryFeatures ? currentSong : null;
                addToPlaylistMenuItem.Visible = canUseLibraryFeatures;
                addToPlaylistMenuItem.Tag = canUseLibraryFeatures ? currentSong : null;

                bool isInUserPlaylist = snapshot.ViewSource.StartsWith("playlist:", StringComparison.OrdinalIgnoreCase) &&
                                        _currentPlaylist != null &&
                                        IsPlaylistCreatedByCurrentUser(_currentPlaylist);

                if (snapshot.IsCurrentPlayback)
                {
                    removeFromPlaylistMenuItem.Visible = false;
                    removeFromPlaylistMenuItem.Tag = null;
                    removeFromPlaylistMenuItem.Text = "从歌单中移除(&R)";
                }
                else if (isLikedSongsView)
                {
                    removeFromPlaylistMenuItem.Text = "取消收藏(&R)";
                    removeFromPlaylistMenuItem.Visible = canUseLibraryFeatures;
                    removeFromPlaylistMenuItem.Tag = canUseLibraryFeatures ? currentSong : null;
                }
                else
                {
                    removeFromPlaylistMenuItem.Text = "从歌单中移除(&R)";
                    removeFromPlaylistMenuItem.Visible = canUseLibraryFeatures && isInUserPlaylist;
                    removeFromPlaylistMenuItem.Tag = removeFromPlaylistMenuItem.Visible ? currentSong : null;
                }
            }
            else
            {
                likeSongMenuItem.Visible = false;
                unlikeSongMenuItem.Visible = false;
                addToPlaylistMenuItem.Visible = false;
                removeFromPlaylistMenuItem.Visible = false;
                removeFromPlaylistMenuItem.Text = "从歌单中移除(&R)";
                likeSongMenuItem.Tag = null;
                unlikeSongMenuItem.Tag = null;
                addToPlaylistMenuItem.Tag = null;
                removeFromPlaylistMenuItem.Tag = null;
            }

            bool isCloudSong = isCloudView && currentSong != null && currentSong.IsCloudSong;
            downloadSongMenuItem.Visible = !isCloudSong;
            downloadSongMenuItem.Tag = currentSong;
            downloadSongMenuItem.Text = isPodcastEpisodeContext ? DownloadSoundMenuText : DownloadSongMenuText;
            bool allowLyricsDownload = !isCloudSong && !isPodcastEpisodeContext;
            downloadLyricsMenuItem.Visible = allowLyricsDownload;
            downloadLyricsMenuItem.Tag = allowLyricsDownload ? currentSong : null;
            batchDownloadMenuItem.Visible = !isCloudSong && !snapshot.IsCurrentPlayback;

            bool showArtistMenu = currentSong != null &&
                (!currentSong.IsCloudSong || !string.IsNullOrWhiteSpace(currentSong?.Artist));
            bool showAlbumMenu = currentSong != null &&
                (!currentSong.IsCloudSong || !string.IsNullOrWhiteSpace(currentSong?.Album));
            bool showShareMenu = currentSong != null && canUseLibraryFeatures;

            if (isPodcastEpisodeContext)
            {
                showArtistMenu = false;
                showAlbumMenu = false;
                showShareMenu = false;
            }

            viewSongArtistMenuItem.Visible = showArtistMenu;
            viewSongArtistMenuItem.Tag = showArtistMenu ? currentSong : null;

            viewSongAlbumMenuItem.Visible = showAlbumMenu;
            viewSongAlbumMenuItem.Tag = showAlbumMenu ? currentSong : null;

            shareSongMenuItem.Visible = showShareMenu;
            if (showShareMenu)
            {
                shareSongMenuItem.Tag = currentSong;
                shareSongWebMenuItem.Tag = currentSong;
                shareSongDirectMenuItem.Tag = currentSong;
            }
            else
            {
                shareSongMenuItem.Tag = null;
                shareSongWebMenuItem.Tag = null;
                shareSongDirectMenuItem.Tag = null;
            }

            if (contextPodcastForEpisode == null && effectiveEpisode == null && currentSong?.IsPodcastEpisode == true)
            {
                contextPodcastForEpisode = ResolvePodcastFromSong(currentSong);
            }

            if (isPodcastEpisodeContext)
            {
                ConfigurePodcastEpisodeShareMenu(effectiveEpisode ?? ResolvePodcastEpisodeFromSong(currentSong));
            }
            else
            {
                ConfigurePodcastEpisodeShareMenu(null);
            }

            bool showPodcastViewMenu = false;
            if (viewPodcastMenuItem != null)
            {
                bool canViewPodcast = contextPodcastForEpisode != null && contextPodcastForEpisode.Id > 0;
                viewPodcastMenuItem.Visible = canViewPodcast;
                viewPodcastMenuItem.Tag = canViewPodcast ? contextPodcastForEpisode : null;
                showPodcastViewMenu = canViewPodcast;
            }

            bool sharePodcastVisible = sharePodcastMenuItem.Visible;
            bool sharePodcastEpisodeVisible = sharePodcastEpisodeMenuItem.Visible;

            showViewSection = showViewSection ||
                              showArtistMenu ||
                              showAlbumMenu ||
                              showShareMenu ||
                              showPodcastViewMenu ||
                              sharePodcastVisible ||
                              sharePodcastEpisodeVisible;
        }
    }
}
