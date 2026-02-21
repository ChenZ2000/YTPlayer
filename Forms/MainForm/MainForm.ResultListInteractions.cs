#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using YTPlayer.Core;
using YTPlayer.Core.Download;
using YTPlayer.Models;
#pragma warning disable CS0219, CS8600, CS8601, CS8602, CS8603, CS8604, CS8618, CS8622, CS8625, CS8632, CS4014

namespace YTPlayer
{
public partial class MainForm
{
	private async void resultListView_ItemActivate(object sender, EventArgs e)
	{
		ListViewItem item = GetSelectedListViewItemSafe();
		if (item == null)
		{
			return;
		}
		object tag = item.Tag;
		if (tag is ListItemInfo listItem)
		{
			await HandleListItemActivate(listItem);
			return;
		}
		if (tag is PlaylistInfo playlist)
		{
			await OpenPlaylist(playlist);
			return;
		}
		if (tag is AlbumInfo album)
		{
			await OpenAlbum(album);
			return;
		}
		if (tag is ArtistInfo artist)
		{
			await OpenArtistAsync(artist);
			return;
		}
		if (tag is PodcastRadioInfo podcast)
		{
			await OpenPodcastRadioAsync(podcast);
			return;
		}
		if (tag is PodcastEpisodeInfo episodeInfo)
		{
			if (episodeInfo?.Song != null)
			{
				AnnounceSongLoadingForActivation(episodeInfo.Song);
				await PlaySong(episodeInfo.Song);
			}
			return;
		}

		int data = (tag is int actionTag) ? actionTag : item.Index;
		if (data == ListRetryPlaceholderTag)
		{
			await RefreshCurrentViewAsync();
		}
		else if (data == -2)
		{
			await OnPrevPageAsync();
		}
		else if (data == -3)
		{
			await OnNextPageAsync();
		}
		else if (data == -4)
		{
			await OnJumpPageAsync();
		}
		else if (data >= 0 && data < _currentSongs.Count)
		{
			await PlaySongByIndex(data);
		}
	}

	private async void resultListView_DoubleClick(object sender, EventArgs e)
	{
		ListViewItem item = GetSelectedListViewItemSafe();
		if (item == null)
		{
			return;
		}
		Debug.WriteLine($"[MainForm] DoubleClick, Tag={item.Tag}, Type={item.Tag?.GetType().Name}");
		object tag = item.Tag;
		if (tag is ListItemInfo listItem)
		{
			await HandleListItemActivate(listItem);
			return;
		}
		if (tag is PlaylistInfo playlist)
		{
			Debug.WriteLine("[MainForm] 双击打开歌单: " + playlist.Name);
			await OpenPlaylist(playlist);
			return;
		}
		if (tag is AlbumInfo album)
		{
			Debug.WriteLine("[MainForm] 双击打开专辑: " + album.Name);
			await OpenAlbum(album);
			return;
		}
		if (tag is ArtistInfo artist)
		{
			Debug.WriteLine("[MainForm] 双击打开歌手: " + artist.Name);
			await OpenArtistAsync(artist);
			return;
		}
		if (tag is PodcastRadioInfo podcast)
		{
			await OpenPodcastRadioAsync(podcast);
			return;
		}
		if (tag is PodcastEpisodeInfo episode)
		{
			if (episode?.Song != null)
			{
				AnnounceSongLoadingForActivation(episode.Song);
				await PlaySong(episode.Song);
			}
			return;
		}
		if (tag is int actionTag && actionTag == ListRetryPlaceholderTag)
		{
			await RefreshCurrentViewAsync();
			return;
		}
		if (tag is int index && index >= 0 && index < _currentSongs.Count)
		{
			SongInfo song = _currentSongs[index];
			Debug.WriteLine("[MainForm] 双击播放歌曲: " + song?.Name);
			await PlaySongByIndex(index);
			return;
		}
		if (tag is SongInfo song2)
		{
			Debug.WriteLine("[MainForm] 双击播放歌曲(直接Tag): " + song2?.Name);
			AnnounceSongLoadingForActivation(song2);
			await PlaySong(song2);
		}
	}

        private void resultListView_SizeChanged(object sender, EventArgs e)
        {
                ScheduleResultListViewLayoutUpdate();
        }

        private void MainForm_SizeChanged(object? sender, EventArgs e)
        {
                if (_isApplyingWindowLayout || WindowState == FormWindowState.Minimized)
                {
                        return;
                }
                if (_lastWindowState != WindowState)
                {
                        _lastWindowState = WindowState;
                        ScheduleWindowLayoutPersist();
                        return;
                }
                if (WindowState == FormWindowState.Normal)
                {
                        ScheduleWindowLayoutPersist();
                }
        }

        private void MainForm_LocationChanged(object? sender, EventArgs e)
        {
                if (_isApplyingWindowLayout || WindowState != FormWindowState.Normal)
                {
                        return;
                }
                ScheduleWindowLayoutPersist();
        }

        private void resultListView_ColumnWidthChanged(object sender, ColumnWidthChangedEventArgs e)
        {
                if (!_listViewLayoutInitialized || _isApplyingListViewLayout || resultListView == null)
                {
                        return;
                }
                if (!_isUserResizingListViewColumns && _listViewColumnWidthSnapshot == null)
                {
                        return;
                }
                if (e.ColumnIndex <= 0 || e.ColumnIndex >= resultListView.Columns.Count)
                {
                        return;
                }
                int width = resultListView.Columns[e.ColumnIndex].Width;
                switch (e.ColumnIndex)
                {
                case 1:
                        _customListViewIndexWidth = width;
                        break;
                case 2:
                        _customListViewNameWidth = width;
                        break;
                case 3:
                        _customListViewCreatorWidth = width;
                        break;
                case 4:
                        _customListViewExtraWidth = width;
                        break;
                case 5:
                        _customListViewDescriptionWidth = width;
                        break;
                }
        }

        private void resultListView_MouseDown(object sender, MouseEventArgs e)
        {
                if (e.Button != MouseButtons.Left || resultListView == null)
                {
                        return;
                }

                if (!TryGetListViewRowResizeTarget(new Point(e.X, e.Y)))
                {
                        return;
                }

                if (resultListView is SafeListView safeListView)
                {
                        _isListViewRowResizing = true;
                        _listViewRowResizeStartY = e.Y;
                        _listViewRowResizeStartHeight = safeListView.GetRowHeight();
                        if (_listViewRowResizeOriginalCursor == null)
                        {
                                _listViewRowResizeOriginalCursor = resultListView.Cursor;
                        }
                        resultListView.Cursor = Cursors.SizeNS;
                        resultListView.Capture = true;
                }
        }

        private void resultListView_ColumnWidthChanging(object sender, ColumnWidthChangingEventArgs e)
        {
                if (!_listViewLayoutInitialized || _isApplyingListViewLayout || resultListView == null)
                {
                        return;
                }
                if (e.ColumnIndex <= 0 || e.ColumnIndex >= resultListView.Columns.Count)
                {
                        return;
                }
                if (!_isUserResizingListViewColumns)
                {
                        BeginListViewColumnResizeTracking();
                }
        }

        private void resultListView_MouseMove(object sender, MouseEventArgs e)
        {
                if (resultListView == null)
                {
                        return;
                }

                if (_isListViewRowResizing)
                {
                        UpdateListViewRowResize(e.Y);
                        return;
                }

                UpdateListViewRowResizeCursor(new Point(e.X, e.Y));
        }

        private bool HandleListViewRowResizeMouseUp(MouseEventArgs e)
        {
                if (!_isListViewRowResizing || resultListView == null)
                {
                        return false;
                }

                _isListViewRowResizing = false;
                resultListView.Capture = false;
                if (_listViewRowResizeOriginalCursor != null)
                {
                        resultListView.Cursor = _listViewRowResizeOriginalCursor;
                }
                _listViewRowResizeOriginalCursor = null;
                return true;
        }

        private void UpdateListViewRowResize(int currentY)
        {
                if (resultListView == null || !_isListViewRowResizing)
                {
                        return;
                }
                int delta = currentY - _listViewRowResizeStartY;
                int targetHeight = _listViewRowResizeStartHeight + delta;
                int minHeight = Math.Max(ListViewRowResizeMinHeight, resultListView.Font?.Height ?? ListViewRowResizeMinHeight);
                targetHeight = Math.Max(minHeight, Math.Min(ListViewMaxRowHeight, targetHeight));
                _customListViewRowHeight = targetHeight;
                if (resultListView is SafeListView safeListView)
                {
                        safeListView.SetRowHeight(targetHeight);
                        resultListView.Invalidate();
                }
        }

        private int[]? CaptureResultListViewColumnWidths()
        {
                if (resultListView == null || resultListView.Columns.Count < ListViewTotalColumnCount)
                {
                        return null;
                }
                return new int[5]
                {
                        columnHeader1.Width,
                        columnHeader2.Width,
                        columnHeader3.Width,
                        columnHeader4.Width,
                        columnHeader5.Width
                };
        }

        private void BeginListViewColumnResizeTracking()
        {
                if (!_listViewLayoutInitialized || _isApplyingListViewLayout || resultListView == null)
                {
                        return;
                }
                _isUserResizingListViewColumns = true;
                _listViewColumnWidthSnapshot = CaptureResultListViewColumnWidths();
        }

        private void EndListViewColumnResizeTracking()
        {
                if (_listViewColumnWidthSnapshot == null || !_listViewLayoutInitialized || _isApplyingListViewLayout || resultListView == null)
                {
                        _listViewColumnWidthSnapshot = null;
                        _isUserResizingListViewColumns = false;
                        return;
                }
                int[]? current = CaptureResultListViewColumnWidths();
                if (current == null || current.Length != _listViewColumnWidthSnapshot.Length)
                {
                        _listViewColumnWidthSnapshot = null;
                        _isUserResizingListViewColumns = false;
                        return;
                }

                if (current[0] != _listViewColumnWidthSnapshot[0])
                {
                        _customListViewIndexWidth = current[0];
                }
                if (current[1] != _listViewColumnWidthSnapshot[1])
                {
                        _customListViewNameWidth = current[1];
                }
                if (current[2] != _listViewColumnWidthSnapshot[2])
                {
                        _customListViewCreatorWidth = current[2];
                }
                if (current[3] != _listViewColumnWidthSnapshot[3])
                {
                        _customListViewExtraWidth = current[3];
                }
                if (current[4] != _listViewColumnWidthSnapshot[4])
                {
                        _customListViewDescriptionWidth = current[4];
                }

                _listViewColumnWidthSnapshot = null;
                _isUserResizingListViewColumns = false;
        }

        private void UpdateListViewRowResizeCursor(Point location)
        {
                if (resultListView == null || _isListViewRowResizing)
                {
                        return;
                }

                bool hit = TryGetListViewRowResizeTarget(location);
                if (hit)
                {
                        if (resultListView.Cursor != Cursors.SizeNS)
                        {
                                _listViewRowResizeOriginalCursor = resultListView.Cursor;
                                resultListView.Cursor = Cursors.SizeNS;
                        }
                }
                else if (_listViewRowResizeOriginalCursor != null)
                {
                        resultListView.Cursor = _listViewRowResizeOriginalCursor;
                        _listViewRowResizeOriginalCursor = null;
                }
        }

        private bool TryGetListViewRowResizeTarget(Point location)
        {
                if (resultListView == null || resultListView.Items.Count == 0)
                {
                        return false;
                }
                ListViewItem item = resultListView.GetItemAt(location.X, location.Y);
                if (item == null)
                {
                        return false;
                }
                Rectangle bounds = item.Bounds;
                int distance = Math.Abs(location.Y - bounds.Bottom);
                return distance <= ListViewRowResizeGripHeight;
        }


}
}
