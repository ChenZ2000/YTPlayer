#nullable disable
using System;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using YTPlayer.Core;
using YTPlayer.Models;
#pragma warning disable CS8632

namespace YTPlayer
{
public partial class MainForm
{
	private SongInfo? GetSelectedSongFromContextMenu(object? sender = null)
	{
		if (_isCurrentPlayingMenuActive && _currentPlayingMenuSong != null)
		{
			return _currentPlayingMenuSong;
		}
		if (sender is ToolStripItem { Tag: SongInfo tag })
		{
			return tag;
		}
		ListViewItem selectedListViewItemSafe = GetSelectedListViewItemSafe();
		if (selectedListViewItemSafe == null)
		{
			return null;
		}
		if (selectedListViewItemSafe.Tag is int num && num >= 0 && num < _currentSongs.Count)
		{
			return _currentSongs[num];
		}
		if (selectedListViewItemSafe.Tag is SongInfo result)
		{
			return result;
		}
		if (selectedListViewItemSafe.Tag is ListItemInfo listItemInfo)
		{
			if (listItemInfo.Type == ListItemType.Song)
			{
				return listItemInfo.Song;
			}
			if (listItemInfo.Type == ListItemType.PodcastEpisode)
			{
				return listItemInfo.PodcastEpisode?.Song;
			}
		}
		if (selectedListViewItemSafe.Tag is PodcastEpisodeInfo podcastEpisodeInfo)
		{
			return podcastEpisodeInfo.Song;
		}
		return null;
	}

	private void ShowContextSongMissingMessage(string actionDescription)
	{
		string text = (_isCurrentPlayingMenuActive ? "当前没有正在播放的歌曲" : ("请先选择要" + actionDescription + "的歌曲"));
		MessageBox.Show(text, "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
	}

	private PlaylistInfo? GetSelectedPlaylistFromContextMenu(object? sender = null)
	{
		if (sender is ToolStripItem { Tag: PlaylistInfo tag })
		{
			return tag;
		}
		ListViewItem selectedListViewItemSafe = GetSelectedListViewItemSafe();
		if (selectedListViewItemSafe == null)
		{
			return null;
		}
		if (selectedListViewItemSafe.Tag is PlaylistInfo result)
		{
			return result;
		}
		if (selectedListViewItemSafe.Tag is ListItemInfo { Type: ListItemType.Playlist } listItemInfo)
		{
			return listItemInfo.Playlist;
		}
		return null;
	}

	private AlbumInfo? GetSelectedAlbumFromContextMenu(object? sender = null)
	{
		if (sender is ToolStripItem { Tag: AlbumInfo tag })
		{
			return tag;
		}
		ListViewItem selectedListViewItemSafe = GetSelectedListViewItemSafe();
		if (selectedListViewItemSafe == null)
		{
			return null;
		}
		if (selectedListViewItemSafe.Tag is AlbumInfo result)
		{
			return result;
		}
		if (selectedListViewItemSafe.Tag is ListItemInfo { Type: ListItemType.Album } listItemInfo)
		{
			return listItemInfo.Album;
		}
		return null;
	}

	private PodcastRadioInfo? GetSelectedPodcastFromContextMenu(object? sender = null)
	{
		if (sender is ToolStripItem { Tag: PodcastRadioInfo tag })
		{
			return tag;
		}
		if (_isCurrentPlayingMenuActive)
		{
			SongInfo currentPlayingMenuSong = _currentPlayingMenuSong;
			if (currentPlayingMenuSong != null && currentPlayingMenuSong.IsPodcastEpisode)
			{
				PodcastRadioInfo podcastRadioInfo = ResolvePodcastFromSong(_currentPlayingMenuSong);
				if (podcastRadioInfo != null)
				{
					return podcastRadioInfo;
				}
			}
		}
		ListViewItem selectedListViewItemSafe = GetSelectedListViewItemSafe();
		if (selectedListViewItemSafe != null)
		{
			if (selectedListViewItemSafe.Tag is PodcastRadioInfo result)
			{
				return result;
			}
			if (selectedListViewItemSafe.Tag is PodcastEpisodeInfo episode)
			{
				PodcastRadioInfo podcastRadioInfo2 = ResolvePodcastFromEpisode(episode);
				if (podcastRadioInfo2 != null)
				{
					return podcastRadioInfo2;
				}
			}
			if (selectedListViewItemSafe.Tag is ListItemInfo listItemInfo)
			{
				if (listItemInfo.Type == ListItemType.Podcast && listItemInfo.Podcast != null)
				{
					return listItemInfo.Podcast;
				}
				if (listItemInfo.Type == ListItemType.PodcastEpisode && listItemInfo.PodcastEpisode != null)
				{
					PodcastRadioInfo podcastRadioInfo3 = ResolvePodcastFromEpisode(listItemInfo.PodcastEpisode);
					if (podcastRadioInfo3 != null)
					{
						return podcastRadioInfo3;
					}
				}
			}
			if (selectedListViewItemSafe.Tag is SongInfo { IsPodcastEpisode: not false } songInfo)
			{
				PodcastRadioInfo podcastRadioInfo4 = ResolvePodcastFromSong(songInfo);
				if (podcastRadioInfo4 != null)
				{
					return podcastRadioInfo4;
				}
			}
			if (selectedListViewItemSafe.Tag is int num && num >= 0 && num < _currentSongs.Count)
			{
				SongInfo songInfo2 = _currentSongs[num];
				if (songInfo2 != null && songInfo2.IsPodcastEpisode)
				{
					PodcastRadioInfo podcastRadioInfo5 = ResolvePodcastFromSong(songInfo2);
					if (podcastRadioInfo5 != null)
					{
						return podcastRadioInfo5;
					}
				}
			}
		}
		if (_currentPodcast != null)
		{
			return _currentPodcast;
		}
		return null;
	}

	private PodcastEpisodeInfo? GetSelectedPodcastEpisodeFromContextMenu(object? sender = null)
	{
		if (sender is ToolStripItem { Tag: PodcastEpisodeInfo tag })
		{
			return tag;
		}
		if (_isCurrentPlayingMenuActive)
		{
			SongInfo currentPlayingMenuSong = _currentPlayingMenuSong;
			if (currentPlayingMenuSong != null && currentPlayingMenuSong.IsPodcastEpisode)
			{
				return ResolvePodcastEpisodeFromSong(_currentPlayingMenuSong);
			}
		}
		ListViewItem selectedListViewItemSafe = GetSelectedListViewItemSafe();
		if (selectedListViewItemSafe == null)
		{
			return null;
		}
		if (selectedListViewItemSafe.Tag is PodcastEpisodeInfo result)
		{
			return result;
		}
		if (selectedListViewItemSafe.Tag is ListItemInfo listItemInfo)
		{
			if (listItemInfo.Type == ListItemType.PodcastEpisode && listItemInfo.PodcastEpisode != null)
			{
				return listItemInfo.PodcastEpisode;
			}
			if (listItemInfo.Type == ListItemType.Song)
			{
				SongInfo song = listItemInfo.Song;
				if (song != null && song.IsPodcastEpisode)
				{
					return ResolvePodcastEpisodeFromSong(listItemInfo.Song);
				}
			}
		}
		if (selectedListViewItemSafe.Tag is SongInfo { IsPodcastEpisode: not false } songInfo)
		{
			return ResolvePodcastEpisodeFromSong(songInfo);
		}
		if (selectedListViewItemSafe.Tag is int num && num >= 0 && num < _currentSongs.Count)
		{
			SongInfo songInfo2 = _currentSongs[num];
			if (songInfo2 != null && songInfo2.IsPodcastEpisode)
			{
				return ResolvePodcastEpisodeFromSong(songInfo2);
			}
		}
		return GetPodcastEpisodeBySelectedIndex();
	}

	private void ConfigurePodcastMenuItems(PodcastRadioInfo? podcast, bool isLoggedIn, bool allowShare = true)
	{
		if (podcast != null)
		{
			bool flag = podcast.Id > 0;
			if (flag)
			{
				downloadPodcastMenuItem.Visible = true;
				downloadPodcastMenuItem.Tag = podcast;
				sharePodcastMenuItem.Visible = allowShare;
				sharePodcastMenuItem.Tag = (allowShare ? podcast : null);
			}
			else
			{
				sharePodcastMenuItem.Visible = false;
				sharePodcastMenuItem.Tag = null;
			}
			if (isLoggedIn && flag)
			{
				bool flag2 = ResolvePodcastSubscriptionState(podcast);
				subscribePodcastMenuItem.Visible = !flag2;
				unsubscribePodcastMenuItem.Visible = flag2;
				subscribePodcastMenuItem.Tag = podcast;
				unsubscribePodcastMenuItem.Tag = podcast;
				subscribePodcastMenuItem.Enabled = true;
				unsubscribePodcastMenuItem.Enabled = true;
			}
		}
	}

	private bool ResolvePodcastSubscriptionState(PodcastRadioInfo? podcast)
	{
		if (podcast == null)
		{
			return false;
		}
		if (podcast.Subscribed)
		{
			return true;
		}
		if (_currentPodcast != null && _currentPodcast.Id == podcast.Id)
		{
			return _currentPodcast.Subscribed;
		}
		lock (_libraryStateLock)
		{
			return _subscribedPodcastIds.Contains(podcast.Id);
		}
	}

	private void ConfigurePodcastEpisodeShareMenu(PodcastEpisodeInfo? episode)
	{
		if (episode == null || episode.ProgramId <= 0)
		{
			sharePodcastEpisodeMenuItem.Visible = false;
			sharePodcastEpisodeMenuItem.Tag = null;
			sharePodcastEpisodeWebMenuItem.Tag = null;
			sharePodcastEpisodeDirectMenuItem.Tag = null;
		}
		else
		{
			sharePodcastEpisodeMenuItem.Visible = true;
			sharePodcastEpisodeMenuItem.Tag = episode;
			sharePodcastEpisodeWebMenuItem.Tag = episode;
			sharePodcastEpisodeDirectMenuItem.Tag = episode;
		}
	}

	private PodcastRadioInfo? ResolvePodcastFromEpisode(PodcastEpisodeInfo? episode)
	{
		if (episode == null || episode.RadioId <= 0)
		{
			return null;
		}
		if (_currentPodcast != null && _currentPodcast.Id == episode.RadioId)
		{
			return _currentPodcast;
		}
		return new PodcastRadioInfo
		{
			Id = episode.RadioId,
			Name = (string.IsNullOrWhiteSpace(episode.RadioName) ? $"播客 {episode.RadioId}" : episode.RadioName),
			DjName = episode.DjName,
			DjUserId = episode.DjUserId
		};
	}

	private PodcastRadioInfo? ResolvePodcastFromSong(SongInfo? song)
	{
		if (song == null || song.PodcastRadioId <= 0)
		{
			return null;
		}
		if (_currentPodcast != null && _currentPodcast.Id == song.PodcastRadioId)
		{
			return _currentPodcast;
		}
		return new PodcastRadioInfo
		{
			Id = song.PodcastRadioId,
			Name = (string.IsNullOrWhiteSpace(song.PodcastRadioName) ? $"播客 {song.PodcastRadioId}" : song.PodcastRadioName),
			DjName = song.PodcastDjName
		};
	}

	private PodcastEpisodeInfo? ResolvePodcastEpisodeFromSong(SongInfo? song)
	{
		if (song == null || song.PodcastProgramId <= 0)
		{
			return null;
		}
		PodcastEpisodeInfo podcastEpisodeInfo = _currentPodcastSounds.FirstOrDefault((PodcastEpisodeInfo e) => e.ProgramId == song.PodcastProgramId);
		if (podcastEpisodeInfo != null)
		{
			if (podcastEpisodeInfo.Song == null)
			{
				podcastEpisodeInfo.Song = song;
			}
			return podcastEpisodeInfo;
		}
		return new PodcastEpisodeInfo
		{
			ProgramId = song.PodcastProgramId,
			Name = (string.IsNullOrWhiteSpace(song.Name) ? $"节目 {song.PodcastProgramId}" : song.Name),
			RadioId = song.PodcastRadioId,
			RadioName = song.PodcastRadioName,
			DjName = song.PodcastDjName,
			Song = song
		};
	}

	private SongInfo? EnsurePodcastEpisodeSong(PodcastEpisodeInfo? episode)
	{
		if (episode == null)
		{
			return null;
		}
		if (episode.Song != null)
		{
			return episode.Song;
		}
		if (episode.ProgramId <= 0)
		{
			return null;
		}
		return episode.Song = new SongInfo
		{
			Id = episode.ProgramId.ToString(CultureInfo.InvariantCulture),
			Name = (string.IsNullOrWhiteSpace(episode.Name) ? $"节目 {episode.ProgramId}" : episode.Name),
			Artist = (string.IsNullOrWhiteSpace(episode.DjName) ? (episode.RadioName ?? string.Empty) : episode.DjName),
			Album = (string.IsNullOrWhiteSpace(episode.RadioName) ? (episode.DjName ?? string.Empty) : (episode.RadioName ?? string.Empty)),
			PicUrl = episode.CoverUrl,
			Duration = ((episode.Duration > TimeSpan.Zero) ? checked((int)episode.Duration.TotalSeconds) : 0),
			IsAvailable = true,
			IsPodcastEpisode = true,
			PodcastProgramId = episode.ProgramId,
			PodcastRadioId = episode.RadioId,
			PodcastRadioName = (episode.RadioName ?? string.Empty),
			PodcastDjName = (episode.DjName ?? string.Empty),
			PodcastPublishTime = episode.PublishTime,
			PodcastEpisodeDescription = episode.Description,
			PodcastSerialNumber = episode.SerialNumber
		};
	}

	private bool IsPodcastEpisodeView()
	{
		if (string.IsNullOrWhiteSpace(_currentViewSource))
		{
			return false;
		}
		return _currentViewSource.StartsWith("podcast:", StringComparison.OrdinalIgnoreCase);
	}

	private PodcastEpisodeInfo? GetPodcastEpisodeBySelectedIndex()
	{
		if (!IsPodcastEpisodeView())
		{
			return null;
		}
		ListViewItem selectedListViewItemSafe = GetSelectedListViewItemSafe();
		if (selectedListViewItemSafe == null)
		{
			return null;
		}
		if (selectedListViewItemSafe.Tag is int num && num < 0)
		{
			return null;
		}
		int index = selectedListViewItemSafe.Index;
		if (index >= 0 && index < _currentPodcastSounds.Count)
		{
			return _currentPodcastSounds[index];
		}
		return null;
	}

	private void UpdatePodcastSortMenuChecks()
	{
		if (podcastSortLatestMenuItem != null && podcastSortSerialMenuItem != null)
		{
			SetMenuItemCheckedState(podcastSortLatestMenuItem, !_podcastSortState.CurrentOption);
			SetMenuItemCheckedState(podcastSortSerialMenuItem, _podcastSortState.CurrentOption);
			if (podcastSortMenuItem != null)
			{
				string text = (_podcastSortState.CurrentOption ? "节目顺序" : "按最新");
				podcastSortMenuItem.Text = "排序（" + text + "）";
			}
		}
	}

	private void EnsureSortMenuCheckMargins()
	{
		EnsureSortMenuCheckMargin(artistSongsSortMenuItem);
		EnsureSortMenuCheckMargin(artistAlbumsSortMenuItem);
		EnsureSortMenuCheckMargin(podcastSortMenuItem);
		EnsureSortMenuCheckMargin(playbackMenuItem);
		EnsureSortMenuCheckMargin(qualityMenuItem);
		EnsureSortMenuCheckMargin(playControlMenuItem);
	}

	private void EnsureSortMenuCheckMargin(ToolStripMenuItem? menuItem)
	{
		if (menuItem?.DropDown is ToolStripDropDownMenu { ShowCheckMargin: false } toolStripDropDownMenu)
		{
			toolStripDropDownMenu.ShowCheckMargin = true;
		}
	}

	private void UpdateArtistSongsSortMenuChecks()
	{
		if (artistSongsSortHotMenuItem != null && artistSongsSortTimeMenuItem != null)
		{
			SetMenuItemCheckedState(artistSongsSortHotMenuItem, _artistSongSortState.EqualsOption(ArtistSongSortOption.Hot));
			SetMenuItemCheckedState(artistSongsSortTimeMenuItem, _artistSongSortState.EqualsOption(ArtistSongSortOption.Time));
			if (artistSongsSortMenuItem != null)
			{
				string text = (_artistSongSortState.EqualsOption(ArtistSongSortOption.Hot) ? "按热度" : "按发布时间");
				artistSongsSortMenuItem.Text = "排序（" + text + "）";
			}
		}
	}

	private void UpdateArtistAlbumsSortMenuChecks()
	{
		if (artistAlbumsSortLatestMenuItem != null && artistAlbumsSortOldestMenuItem != null)
		{
			SetMenuItemCheckedState(artistAlbumsSortLatestMenuItem, _artistAlbumSortState.EqualsOption(ArtistAlbumSortOption.Latest));
			SetMenuItemCheckedState(artistAlbumsSortOldestMenuItem, _artistAlbumSortState.EqualsOption(ArtistAlbumSortOption.Oldest));
			if (artistAlbumsSortMenuItem != null)
			{
				string text = (_artistAlbumSortState.EqualsOption(ArtistAlbumSortOption.Latest) ? "按最新" : "按最早");
				artistAlbumsSortMenuItem.Text = "排序（" + text + "）";
			}
		}
	}

	private static void SetMenuItemCheckedState(ToolStripMenuItem? menuItem, bool isChecked)
	{
		if (menuItem != null)
		{
			menuItem.Checked = isChecked;
			menuItem.CheckState = (isChecked ? CheckState.Checked : CheckState.Unchecked);
		}
	}
	private MenuContextSnapshot BuildMenuContextSnapshot(bool isCurrentPlayingRequest)
	{
		string text = _currentViewSource ?? string.Empty;
		bool flag = !string.IsNullOrWhiteSpace(text);
		MenuContextSnapshot menuContextSnapshot = new MenuContextSnapshot
		{
			InvocationSource = (isCurrentPlayingRequest ? MenuInvocationSource.CurrentPlayback : MenuInvocationSource.ViewSelection),
			ViewSource = text,
			IsLoggedIn = IsUserLoggedIn(),
			IsCloudView = (flag && text.StartsWith("user_cloud", StringComparison.OrdinalIgnoreCase)),
			IsMyPlaylistsView = string.Equals(text, "user_playlists", StringComparison.OrdinalIgnoreCase),
			IsUserAlbumsView = string.Equals(text, "user_albums", StringComparison.OrdinalIgnoreCase),
			IsPodcastEpisodeView = IsPodcastEpisodeView(),
			IsArtistSongsView = (flag && text.StartsWith("artist_songs:", StringComparison.OrdinalIgnoreCase)),
			IsArtistAlbumsView = (flag && text.StartsWith("artist_albums:", StringComparison.OrdinalIgnoreCase)),
			PrimaryEntity = MenuEntityKind.None,
			IsValid = true
		};
		if (isCurrentPlayingRequest)
		{
			SongInfo songInfo = _audioEngine?.CurrentSong;
			if (songInfo == null)
			{
				menuContextSnapshot.IsValid = false;
				return menuContextSnapshot;
			}
			menuContextSnapshot.Song = songInfo;
			if (songInfo.IsPodcastEpisode)
			{
				menuContextSnapshot.PrimaryEntity = MenuEntityKind.PodcastEpisode;
				menuContextSnapshot.PodcastEpisode = ResolvePodcastEpisodeFromSong(songInfo);
			}
			else
			{
				menuContextSnapshot.PrimaryEntity = MenuEntityKind.Song;
			}
			return menuContextSnapshot;
		}
		ListViewItem selectedListViewItemSafe = GetSelectedListViewItemSafe();
		if (selectedListViewItemSafe == null)
		{
			menuContextSnapshot.IsValid = false;
			return menuContextSnapshot;
		}
		menuContextSnapshot.SelectedListItem = selectedListViewItemSafe;
		if (menuContextSnapshot.IsPodcastEpisodeView && selectedListViewItemSafe.Tag is int num && num < 0)
		{
			menuContextSnapshot.IsValid = false;
			return menuContextSnapshot;
		}
		object tag = selectedListViewItemSafe.Tag;
		object obj = tag;
		if (!(obj is PlaylistInfo playlist))
		{
			if (!(obj is AlbumInfo album))
			{
				if (!(obj is ArtistInfo artist))
				{
					if (!(obj is PodcastRadioInfo podcast))
					{
						if (!(obj is PodcastEpisodeInfo podcastEpisodeInfo))
						{
							if (!(obj is SongInfo song))
							{
								if (!(obj is ListItemInfo listItem))
								{
									if (obj is int num2)
									{
										if (num2 >= 0 && num2 < _currentSongs.Count)
										{
											menuContextSnapshot.PrimaryEntity = MenuEntityKind.Song;
											menuContextSnapshot.Song = _currentSongs[num2];
											return menuContextSnapshot;
										}
										int num3 = num2;
										if (num3 < 0)
										{
											menuContextSnapshot.IsValid = false;
											return menuContextSnapshot;
										}
									}
									menuContextSnapshot.IsValid = false;
									return menuContextSnapshot;
								}
								menuContextSnapshot.ListItem = listItem;
								return ResolveListItemSnapshot(menuContextSnapshot, listItem);
							}
							menuContextSnapshot.PrimaryEntity = MenuEntityKind.Song;
							menuContextSnapshot.Song = song;
							return menuContextSnapshot;
						}
						menuContextSnapshot.PrimaryEntity = MenuEntityKind.PodcastEpisode;
						menuContextSnapshot.PodcastEpisode = podcastEpisodeInfo;
						menuContextSnapshot.Song = podcastEpisodeInfo.Song;
						return menuContextSnapshot;
					}
					menuContextSnapshot.PrimaryEntity = MenuEntityKind.Podcast;
					menuContextSnapshot.Podcast = podcast;
					return menuContextSnapshot;
				}
				menuContextSnapshot.PrimaryEntity = MenuEntityKind.Artist;
				menuContextSnapshot.Artist = artist;
				return menuContextSnapshot;
			}
			menuContextSnapshot.PrimaryEntity = MenuEntityKind.Album;
			menuContextSnapshot.Album = album;
			return menuContextSnapshot;
		}
		menuContextSnapshot.PrimaryEntity = MenuEntityKind.Playlist;
		menuContextSnapshot.Playlist = playlist;
		return menuContextSnapshot;
	}

	private MenuContextSnapshot ResolveListItemSnapshot(MenuContextSnapshot snapshot, ListItemInfo listItem)
	{
		switch (listItem.Type)
		{
		case ListItemType.Playlist:
			if (listItem.Playlist == null)
			{
				goto default;
			}
			snapshot.PrimaryEntity = MenuEntityKind.Playlist;
			snapshot.Playlist = listItem.Playlist;
			break;
		case ListItemType.Album:
			if (listItem.Album == null)
			{
				goto default;
			}
			snapshot.PrimaryEntity = MenuEntityKind.Album;
			snapshot.Album = listItem.Album;
			break;
		case ListItemType.Artist:
			if (listItem.Artist == null)
			{
				goto default;
			}
			snapshot.PrimaryEntity = MenuEntityKind.Artist;
			snapshot.Artist = listItem.Artist;
			break;
		case ListItemType.Podcast:
			if (listItem.Podcast == null)
			{
				goto default;
			}
			snapshot.PrimaryEntity = MenuEntityKind.Podcast;
			snapshot.Podcast = listItem.Podcast;
			break;
		case ListItemType.PodcastEpisode:
			if (listItem.PodcastEpisode == null)
			{
				goto default;
			}
			snapshot.PrimaryEntity = MenuEntityKind.PodcastEpisode;
			snapshot.PodcastEpisode = listItem.PodcastEpisode;
			snapshot.Song = listItem.PodcastEpisode.Song;
			break;
		case ListItemType.Song:
			if (listItem.Song == null)
			{
				goto default;
			}
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
		subscribeSongAlbumMenuItem.Visible = false;
		subscribeSongAlbumMenuItem.Tag = null;
		subscribeSongArtistMenuItem.Visible = false;
		subscribeSongArtistMenuItem.Tag = null;
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
		downloadSongMenuItem.Text = "下载歌曲(&D)";
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
		bool flag = !snapshot.IsCurrentPlayback && snapshot.IsPodcastEpisodeView && !string.IsNullOrWhiteSpace(snapshot.ViewSource) && snapshot.ViewSource.StartsWith("podcast:", StringComparison.OrdinalIgnoreCase);
		podcastSortMenuItem.Visible = flag;
		if (flag)
		{
			UpdatePodcastSortMenuChecks();
		}
		if (artistSongsSortMenuItem != null)
		{
			bool flag2 = !snapshot.IsCurrentPlayback && snapshot.IsArtistSongsView;
			artistSongsSortMenuItem.Visible = flag2;
			if (flag2)
			{
				UpdateArtistSongsSortMenuChecks();
			}
		}
		if (artistAlbumsSortMenuItem != null)
		{
			bool flag3 = !snapshot.IsCurrentPlayback && snapshot.IsArtistAlbumsView;
			artistAlbumsSortMenuItem.Visible = flag3;
			if (flag3)
			{
				UpdateArtistAlbumsSortMenuChecks();
			}
		}
		if (!podcastSortMenuItem.Visible)
		{
			ToolStripMenuItem toolStripMenuItem = artistSongsSortMenuItem;
			if (toolStripMenuItem == null || !toolStripMenuItem.Visible)
			{
				ToolStripMenuItem toolStripMenuItem2 = artistAlbumsSortMenuItem;
				if (toolStripMenuItem2 == null || !toolStripMenuItem2.Visible)
				{
					return;
				}
			}
		}
		showViewSection = true;
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

	private void ConfigurePlaylistMenu(MenuContextSnapshot snapshot, bool isLoggedIn, ref bool showViewSection, ref CommentTarget? contextCommentTarget)
	{
		PlaylistInfo playlist = snapshot.Playlist;
		if (playlist != null)
		{
			bool flag = IsPlaylistCreatedByCurrentUser(playlist);
			bool flag2 = !flag && IsPlaylistSubscribed(playlist);
			if (isLoggedIn)
			{
				subscribePlaylistMenuItem.Visible = !flag && !flag2;
				unsubscribePlaylistMenuItem.Visible = !flag && flag2;
				deletePlaylistMenuItem.Visible = flag;
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
				contextCommentTarget = new CommentTarget(playlist.Id, CommentType.Playlist, string.IsNullOrWhiteSpace(playlist.Name) ? "歌单" : playlist.Name, playlist.Creator);
			}
		}
	}

	private void ConfigureAlbumMenu(MenuContextSnapshot snapshot, bool isLoggedIn, ref bool showViewSection, ref CommentTarget? contextCommentTarget)
	{
		AlbumInfo album = snapshot.Album;
		if (album != null)
		{
			if (isLoggedIn)
			{
				bool flag = IsAlbumSubscribed(album);
				subscribeAlbumMenuItem.Visible = !flag;
				unsubscribeAlbumMenuItem.Visible = flag;
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
				contextCommentTarget = new CommentTarget(album.Id, CommentType.Album, string.IsNullOrWhiteSpace(album.Name) ? "专辑" : album.Name, album.Artist);
			}
		}
	}

	private void ConfigurePodcastMenu(MenuContextSnapshot snapshot, bool isLoggedIn, ref bool showViewSection)
	{
		PodcastRadioInfo podcast = snapshot.Podcast;
		if (podcast != null)
		{
			insertPlayMenuItem.Visible = false;
			ConfigurePodcastMenuItems(podcast, isLoggedIn);
			if (sharePodcastMenuItem.Visible)
			{
				showViewSection = true;
			}
		}
	}

	private void ConfigureSongOrEpisodeMenu(MenuContextSnapshot snapshot, bool isLoggedIn, bool isCloudView, ref bool showViewSection, ref CommentTarget? contextCommentTarget, ref PodcastRadioInfo? contextPodcastForEpisode, ref PodcastEpisodeInfo? effectiveEpisode, ref bool isPodcastEpisodeContext)
	{
		insertPlayMenuItem.Visible = true;
		SongInfo songInfo = snapshot.Song;
		if (snapshot.IsCurrentPlayback && _currentPlayingMenuSong != null)
		{
			songInfo = _currentPlayingMenuSong;
		}
		PodcastEpisodeInfo podcastEpisode = snapshot.PodcastEpisode;
		if (songInfo == null && podcastEpisode?.Song != null)
		{
			songInfo = podcastEpisode.Song;
		}
		if (songInfo == null && podcastEpisode != null)
		{
			songInfo = EnsurePodcastEpisodeSong(podcastEpisode);
		}
		if (podcastEpisode != null)
		{
			effectiveEpisode = podcastEpisode;
			isPodcastEpisodeContext = true;
		}
		else if (songInfo != null && songInfo.IsPodcastEpisode)
		{
			isPodcastEpisodeContext = true;
			effectiveEpisode = ResolvePodcastEpisodeFromSong(songInfo);
		}
		if (effectiveEpisode != null)
		{
			contextPodcastForEpisode = ResolvePodcastFromEpisode(effectiveEpisode);
			songInfo = EnsurePodcastEpisodeSong(effectiveEpisode);
		}
		insertPlayMenuItem.Tag = songInfo;
		if (songInfo != null && !string.IsNullOrWhiteSpace(songInfo.Id) && !songInfo.IsCloudSong && !isPodcastEpisodeContext)
		{
			contextCommentTarget = new CommentTarget(songInfo.Id, CommentType.Song, string.IsNullOrWhiteSpace(songInfo.Name) ? "歌曲" : songInfo.Name, songInfo.Artist);
		}
		bool flag = !isPodcastEpisodeContext && CanSongUseLibraryFeatures(songInfo);
		AlbumInfo albumInfo = ((!isPodcastEpisodeContext) ? TryCreateAlbumInfoFromSong(songInfo) : null);
		bool isCloudSongContext = IsCloudSongContext(songInfo, isCloudView);
		if (!isCloudSongContext && snapshot.IsCurrentPlayback)
		{
			string text2 = ResolveCurrentPlayingViewSource(songInfo);
			isCloudSongContext = !string.IsNullOrWhiteSpace(text2) && text2.StartsWith("user_cloud", StringComparison.OrdinalIgnoreCase);
		}
		bool flag2 = isLoggedIn && !isPodcastEpisodeContext;
		if (flag2)
		{
			if (isCloudSongContext)
			{
				flag2 = albumInfo != null && !IsAlbumSubscribed(albumInfo);
			}
			else
			{
				flag2 = !string.IsNullOrWhiteSpace(ResolveSongIdForLibraryState(songInfo)) && (albumInfo == null || !IsAlbumSubscribed(albumInfo));
			}
		}
		object subscribeSongAlbumContext = null;
		if (flag2)
		{
			if (albumInfo != null)
			{
				subscribeSongAlbumContext = albumInfo;
			}
			else if (songInfo != null && !isCloudSongContext)
			{
				subscribeSongAlbumContext = songInfo;
			}
		}
		subscribeSongAlbumMenuItem.Visible = flag2;
		subscribeSongAlbumMenuItem.Tag = subscribeSongAlbumContext;
		bool canSubscribeSongArtist = isLoggedIn && !isPodcastEpisodeContext;
		object subscribeSongArtistContext = null;
		if (canSubscribeSongArtist)
		{
			ArtistInfo artistInfo = TryCreatePrimaryArtistInfoFromSong(songInfo);
			if (isCloudSongContext)
			{
				canSubscribeSongArtist = artistInfo != null && !IsArtistSubscribed(artistInfo);
				if (canSubscribeSongArtist)
				{
					subscribeSongArtistContext = artistInfo;
				}
			}
			else if (string.IsNullOrWhiteSpace(ResolveSongIdForLibraryState(songInfo)))
			{
				canSubscribeSongArtist = false;
			}
			else if (artistInfo != null)
			{
				canSubscribeSongArtist = !IsArtistSubscribed(artistInfo);
				if (canSubscribeSongArtist)
				{
					subscribeSongArtistContext = artistInfo;
				}
			}
			else
			{
				subscribeSongArtistContext = songInfo;
			}
		}
		subscribeSongArtistMenuItem.Visible = canSubscribeSongArtist;
		subscribeSongArtistMenuItem.Tag = (canSubscribeSongArtist ? subscribeSongArtistContext : null);
		if (isCloudView && songInfo != null && songInfo.IsCloudSong)
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
			bool flag3 = IsCurrentLikedSongsView();
			bool flag4 = flag3;
			if (flag && songInfo != null && !flag4)
			{
				flag4 = IsSongLiked(songInfo);
			}
			likeSongMenuItem.Visible = flag && !flag4;
			unlikeSongMenuItem.Visible = flag && flag4;
			likeSongMenuItem.Tag = (flag ? songInfo : null);
			unlikeSongMenuItem.Tag = (flag ? songInfo : null);
			addToPlaylistMenuItem.Visible = flag;
			addToPlaylistMenuItem.Tag = (flag ? songInfo : null);
			string playlistId = (snapshot.ViewSource.StartsWith("playlist:", StringComparison.OrdinalIgnoreCase) ? snapshot.ViewSource.Substring("playlist:".Length) : null);
			bool flag5 = snapshot.ViewSource.StartsWith("playlist:", StringComparison.OrdinalIgnoreCase) || (_currentPlaylist != null && !string.IsNullOrWhiteSpace(_currentPlaylist.Id));
			bool flag6 = _currentPlaylistOwnedByUser;
			if (!flag6 && !string.IsNullOrWhiteSpace(playlistId) && _currentPlaylist != null && string.Equals(_currentPlaylist.Id, playlistId, StringComparison.OrdinalIgnoreCase))
			{
				flag6 = IsPlaylistOwnedByUser(_currentPlaylist, GetCurrentUserId());
			}
			bool flag7 = flag5 && flag6;
			if (snapshot.IsCurrentPlayback)
			{
				removeFromPlaylistMenuItem.Visible = false;
				removeFromPlaylistMenuItem.Tag = null;
				removeFromPlaylistMenuItem.Text = "从歌单中移除(&R)";
			}
			else
			{
				removeFromPlaylistMenuItem.Text = "从歌单中移除(&R)";
				removeFromPlaylistMenuItem.Visible = flag && flag7 && !flag3;
				removeFromPlaylistMenuItem.Tag = (removeFromPlaylistMenuItem.Visible ? songInfo : null);
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
		bool flag8 = isCloudView && songInfo != null && songInfo.IsCloudSong;
		downloadSongMenuItem.Visible = !flag8;
		downloadSongMenuItem.Tag = songInfo;
		downloadSongMenuItem.Text = (isPodcastEpisodeContext ? "下载声音(&D)" : "下载歌曲(&D)");
		bool flag9 = !flag8 && !isPodcastEpisodeContext;
		downloadLyricsMenuItem.Visible = flag9;
		downloadLyricsMenuItem.Tag = (flag9 ? songInfo : null);
		batchDownloadMenuItem.Visible = !flag8 && !snapshot.IsCurrentPlayback;
		bool flag10 = songInfo != null && (!songInfo.IsCloudSong || !string.IsNullOrWhiteSpace(songInfo?.Artist));
		bool flag11 = songInfo != null && (!songInfo.IsCloudSong || !string.IsNullOrWhiteSpace(songInfo?.Album));
		bool flag12 = songInfo != null && flag;
		if (isPodcastEpisodeContext)
		{
			flag10 = false;
			flag11 = false;
			flag12 = false;
		}
		viewSongArtistMenuItem.Visible = flag10;
		viewSongArtistMenuItem.Tag = (flag10 ? songInfo : null);
		viewSongAlbumMenuItem.Visible = flag11;
		viewSongAlbumMenuItem.Tag = (flag11 ? songInfo : null);
		shareSongMenuItem.Visible = flag12;
		if (flag12)
		{
			shareSongMenuItem.Tag = songInfo;
			shareSongWebMenuItem.Tag = songInfo;
			shareSongDirectMenuItem.Tag = songInfo;
		}
		else
		{
			shareSongMenuItem.Tag = null;
			shareSongWebMenuItem.Tag = null;
			shareSongDirectMenuItem.Tag = null;
		}
		if (contextPodcastForEpisode == null && effectiveEpisode == null && songInfo != null && songInfo.IsPodcastEpisode)
		{
			contextPodcastForEpisode = ResolvePodcastFromSong(songInfo);
		}
		if (isPodcastEpisodeContext)
		{
			ConfigurePodcastEpisodeShareMenu(effectiveEpisode ?? ResolvePodcastEpisodeFromSong(songInfo));
		}
		else
		{
			ConfigurePodcastEpisodeShareMenu(null);
		}
		bool flag13 = false;
		if (viewPodcastMenuItem != null)
		{
			bool flag14 = contextPodcastForEpisode != null && contextPodcastForEpisode.Id > 0;
			viewPodcastMenuItem.Visible = flag14;
			viewPodcastMenuItem.Tag = (flag14 ? contextPodcastForEpisode : null);
			flag13 = flag14;
		}
		bool visible = sharePodcastMenuItem.Visible;
		bool visible2 = sharePodcastEpisodeMenuItem.Visible;
		showViewSection = showViewSection || flag10 || flag11 || flag12 || flag2 || canSubscribeSongArtist || flag13 || visible || visible2;
	}


}
}
