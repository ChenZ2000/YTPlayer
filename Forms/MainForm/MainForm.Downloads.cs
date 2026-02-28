#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using YTPlayer.Core;
using YTPlayer.Core.Download;
using YTPlayer.Core.Upload;
using YTPlayer.Forms.Download;
using YTPlayer.Models;
using YTPlayer.Models.Download;
using YTPlayer.Models.Upload;
using YTPlayer.Utils;
#pragma warning disable CS0219, CS8600, CS8601, CS8602, CS8603, CS8604, CS8618, CS8622, CS8625, CS8632, CS4014

namespace YTPlayer
{
public partial class MainForm
{
	private void InitializeDownload()
	{
		_downloadManager = DownloadManager.Instance;
		_downloadManager.Initialize(_apiClient, _unblockService);
		UploadManager instance = UploadManager.Instance;
		instance.Initialize(_apiClient);
		instance.TaskCompleted -= OnCloudUploadTaskCompleted;
		instance.TaskCompleted += OnCloudUploadTaskCompleted;
		instance.TaskFailed -= OnCloudUploadTaskFailed;
		instance.TaskFailed += OnCloudUploadTaskFailed;
	}

	internal void OpenDownloadDirectory_Click(object? sender, EventArgs e)
	{
		try
		{
			ConfigModel configModel = _configManager.Load();
			string fullDownloadPath = ConfigManager.GetFullDownloadPath(configModel.DownloadDirectory);
			if (!Directory.Exists(fullDownloadPath))
			{
				Directory.CreateDirectory(fullDownloadPath);
			}
			Process.Start("explorer.exe", fullDownloadPath);
		}
		catch (Exception ex)
		{
			MessageBox.Show("无法打开下载目录：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
		}
	}

	internal void ChangeDownloadDirectory_Click(object? sender, EventArgs e)
	{
		try
		{
			ConfigModel configModel = _configManager.Load();
			string fullDownloadPath = ConfigManager.GetFullDownloadPath(configModel.DownloadDirectory);
			using FolderBrowserDialog folderBrowserDialog = new FolderBrowserDialog();
			folderBrowserDialog.Description = "选择下载目录";
			folderBrowserDialog.SelectedPath = fullDownloadPath;
			if (folderBrowserDialog.ShowDialog() == DialogResult.OK)
			{
				string text = (configModel.DownloadDirectory = folderBrowserDialog.SelectedPath);
				string text2 = text;
				_configManager.Save(configModel);
				MessageBox.Show("下载目录已更改为：\n" + text2, "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			}
		}
		catch (Exception ex)
		{
			MessageBox.Show("更改下载目录失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
		}
	}

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
			}
			else
			{
				_downloadManagerForm = new DownloadManagerForm();
				_downloadManagerForm.FormClosed += DownloadManagerForm_FormClosed;
				_downloadManagerForm.Show(this);
				_downloadManagerForm.BringToFront();
				_downloadManagerForm.Activate();
			}
		}
		catch (Exception ex)
		{
			MessageBox.Show("无法打开下载管理器：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
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

	private void NotifyTransferTaskAdded(string message)
	{
		if (string.IsNullOrWhiteSpace(message))
		{
			return;
		}

		AnnounceUiMessage(message, interrupt: true);
		UpdateStatusBar(message);
	}

	internal async void DownloadSong_Click(object? sender, EventArgs e)
	{
		SongInfo song = GetSelectedSongFromContextMenu(sender);
		if (song == null)
		{
			ShowContextSongMissingMessage("下载的歌曲");
			return;
		}
		try
		{
			QualityLevel quality = GetCurrentQuality();
			string sourceList = GetCurrentViewName();
			NotifyTransferTaskAdded("正在添加下载任务...");
			if (await _downloadManager.AddSongDownloadAsync(song, quality, sourceList) != null)
			{
				NotifyTransferTaskAdded("已添加下载任务：" + song.Name + " - " + song.Artist);
			}
			else
			{
				MessageBox.Show("添加下载任务失败", "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			}
		}
		catch (Exception ex)
		{
			MessageBox.Show("下载失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
		}
	}

	internal async void DownloadLyrics_Click(object? sender, EventArgs e)
	{
		SongInfo song = GetSelectedSongFromContextMenu(sender);
		if (song == null || string.IsNullOrWhiteSpace(song.Id))
		{
			ShowContextSongMissingMessage("下载歌词的歌曲");
			return;
		}
		try
		{
			var lyricContext = await _apiClient.GetResolvedLyricsAsync(song.Id);
			if (lyricContext == null || string.IsNullOrWhiteSpace(lyricContext.ExportLyricContent))
			{
				MessageBox.Show("该歌曲没有歌词", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				return;
			}
			CacheLoadedLyricsContext(lyricContext);
			string sourceList = GetCurrentViewName();
			NotifyTransferTaskAdded("正在添加歌词下载任务...");
			if (await _downloadManager.AddLyricDownloadAsync(song, sourceList, lyricContext.ExportLyricContent) != null)
			{
				NotifyTransferTaskAdded("已添加歌词下载任务：" + song.Name + " - " + song.Artist);
			}
			else
			{
				MessageBox.Show("歌词下载任务创建失败", "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			}
		}
		catch (Exception ex)
		{
			MessageBox.Show("下载歌词失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
		}
	}

	internal async void BatchDownloadSongs_Click(object? sender, EventArgs e)
	{
		if (_currentSongs == null || _currentSongs.Count == 0)
		{
			MessageBox.Show("当前列表为空", "提示", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
			return;
		}
		checked
		{
			try
			{
				string viewName = GetCurrentViewName();
				string viewSource = _currentViewSource ?? string.Empty;
				SongDialogLoadState state = new SongDialogLoadState(new List<SongInfo>(_currentSongs ?? new List<SongInfo>()));
				if (state.Songs.Count == 0)
				{
					MessageBox.Show("当前列表中没有可下载的歌曲", "提示", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
					return;
				}
				List<string> displayNames = state.Songs.Select(BuildSongDownloadDisplayText).ToList();
				BatchDownloadDialog dialog = new BatchDownloadDialog(displayNames, "批量下载 - " + viewName);
				using CancellationTokenSource syncCts = new CancellationTokenSource();
				Task syncTask = SyncBatchDialogWithCurrentSongsAsync(dialog, state.Songs, viewSource, syncCts.Token);
				DialogResult result = dialog.ShowDialog();
				syncCts.Cancel();
				try
				{
					await syncTask;
				}
				catch (OperationCanceledException)
				{
				}
				catch (Exception ex)
				{
					Debug.WriteLine($"[BatchDownload] 同步主列表到下载对话框失败: {ex.Message}");
				}
				if (result != DialogResult.OK)
				{
					return;
				}
				List<int> selectedIndices = dialog.SelectedIndices;
				if (selectedIndices.Count == 0)
				{
					return;
				}
				var (selectedSongs, originalIndices, missingCount) = await ResolveSelectedSongsFromStateAsync(state, selectedIndices, viewSource, CancellationToken.None);
				if (selectedSongs.Count == 0)
				{
					MessageBox.Show("选中的条目仍在加载中，请稍后重试。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
					return;
				}
				QualityLevel quality = GetCurrentQuality();
				NotifyTransferTaskAdded("正在添加批量下载任务...");
				int addedCount = (await _downloadManager.AddBatchDownloadAsync(selectedSongs, quality, viewName, viewName, originalIndices)).Count;
				string suffix = (missingCount > 0) ? $"（{missingCount} 项尚未加载，已跳过）" : string.Empty;
				NotifyTransferTaskAdded($"已添加 {addedCount}/{selectedSongs.Count} 个下载任务{suffix}");
			}
			catch (Exception ex)
			{
				MessageBox.Show("批量下载失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			}
		}
	}

	internal async void DownloadPlaylist_Click(object? sender, EventArgs e)
	{
		ListViewItem selectedItem = GetSelectedListViewItemSafe();
		if (selectedItem == null)
		{
			return;
		}
		object tag = selectedItem.Tag;
		if (!(tag is PlaylistInfo playlist))
		{
			return;
		}
		checked
		{
			try
			{
				if (string.IsNullOrWhiteSpace(playlist.Id))
				{
					MessageBox.Show("歌单 ID 无效，无法下载。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
					return;
				}
				string viewSource = "playlist:" + playlist.Id;
				PlaylistInfo playlistSeed = playlist;
				Cursor originalCursor = System.Windows.Forms.Cursor.Current;
				System.Windows.Forms.Cursor.Current = Cursors.WaitCursor;
				try
				{
					PlaylistInfo detail = await _apiClient.GetPlaylistDetailAsync(playlist.Id);
					if (detail != null)
					{
						if (string.IsNullOrWhiteSpace(detail.Name))
						{
							detail.Name = playlist.Name;
						}
						if (detail.TrackCount <= 0)
						{
							detail.TrackCount = playlist.TrackCount;
						}
						playlistSeed = detail;
					}
				}
				catch (Exception ex)
				{
					Debug.WriteLine($"[DownloadPlaylist] 获取歌单详情失败，继续使用占位加载: {ex.Message}");
				}
				finally
				{
					System.Windows.Forms.Cursor.Current = originalCursor;
				}
				List<SongInfo> knownSongs = (playlistSeed.Songs != null && playlistSeed.Songs.Count > 0)
					? new List<SongInfo>(playlistSeed.Songs.Where((SongInfo song) => song != null))
					: new List<SongInfo>();
				int totalCount = Math.Max(playlistSeed.TrackCount, knownSongs.Count);
				if (totalCount <= 0)
				{
					MessageBox.Show("无法获取歌单歌曲列表或歌单为空", "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
					return;
				}
				SongDialogLoadState state = new SongDialogLoadState(BuildPlaceholderSongBuffer(totalCount, viewSource));
				for (int i = 0; i < knownSongs.Count && i < state.Songs.Count; i++)
				{
					state.Songs[i] = MergeSongFields(state.Songs[i], knownSongs[i], viewSource);
				}
				string playlistName = string.IsNullOrWhiteSpace(playlistSeed.Name) ? (string.IsNullOrWhiteSpace(playlist.Name) ? $"歌单_{playlist.Id}" : playlist.Name) : playlistSeed.Name;
				List<string> displayNames = state.Songs.Select(BuildSongDownloadDisplayText).ToList();
				BatchDownloadDialog dialog = new BatchDownloadDialog(displayNames, "下载歌单 - " + playlistName);
				using CancellationTokenSource enrichCts = new CancellationTokenSource();
				Task enrichTask = EnrichSongDialogByOrderedIdsAsync(viewSource, state, dialog, (CancellationToken ct) => FetchPlaylistTrackIdsAsync(playlist.Id, ct), (List<string> ids, CancellationToken ct) => FetchPlaylistSongsByIdsBatchAsync(ids, ct), enrichCts.Token);
				DialogResult result = dialog.ShowDialog();
				enrichCts.Cancel();
				try
				{
					await enrichTask;
				}
				catch (OperationCanceledException)
				{
				}
				catch (Exception ex)
				{
					Debug.WriteLine($"[DownloadPlaylist] 歌单条目补齐失败: {ex.Message}");
				}
				if (result != DialogResult.OK)
				{
					return;
				}
				List<int> selectedIndices = dialog.SelectedIndices;
				if (selectedIndices.Count == 0)
				{
					return;
				}
				var (selectedSongs, originalIndices, missingCount) = await ResolveSelectedSongsFromStateAsync(state, selectedIndices, viewSource, CancellationToken.None);
				if (selectedSongs.Count == 0)
				{
					MessageBox.Show("选中的条目仍在加载中，请稍后重试。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
					return;
				}
				QualityLevel quality = GetCurrentQuality();
				NotifyTransferTaskAdded("正在添加歌单下载任务...");
				int addedCount = (await _downloadManager.AddBatchDownloadAsync(selectedSongs, quality, playlistName, playlistName, originalIndices)).Count;
				string suffix = (missingCount > 0) ? $"（{missingCount} 项尚未加载，已跳过）" : string.Empty;
				NotifyTransferTaskAdded($"已添加 {addedCount}/{selectedSongs.Count} 个下载任务，歌单：{playlistName}{suffix}");
			}
			catch (Exception ex)
			{
				MessageBox.Show("下载歌单失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			}
		}
	}

	internal async void DownloadAlbum_Click(object? sender, EventArgs e)
	{
		ListViewItem selectedItem = GetSelectedListViewItemSafe();
		if (selectedItem == null)
		{
			return;
		}
		object tag = selectedItem.Tag;
		if (!(tag is AlbumInfo album))
		{
			return;
		}
		checked
		{
			try
			{
				if (string.IsNullOrWhiteSpace(album.Id))
				{
					MessageBox.Show("专辑 ID 无效，无法下载。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
					return;
				}
				string albumId = album.Id;
				string viewSource = "album:" + albumId;
				AlbumInfo albumSeed = album;
				Cursor originalCursor = System.Windows.Forms.Cursor.Current;
				System.Windows.Forms.Cursor.Current = Cursors.WaitCursor;
				try
				{
					AlbumInfo detail = await _apiClient.GetAlbumDetailAsync(albumId);
					if (detail != null)
					{
						if (string.IsNullOrWhiteSpace(detail.Name))
						{
							detail.Name = album.Name;
						}
						if (string.IsNullOrWhiteSpace(detail.Artist))
						{
							detail.Artist = album.Artist;
						}
						if (detail.TrackCount <= 0)
						{
							detail.TrackCount = album.TrackCount;
						}
						albumSeed = detail;
					}
				}
				catch (Exception ex)
				{
					Debug.WriteLine($"[DownloadAlbum] 获取专辑详情失败，继续使用占位加载: {ex.Message}");
				}
				finally
				{
					System.Windows.Forms.Cursor.Current = originalCursor;
				}
				List<SongInfo> knownSongs = (albumSeed.Songs != null && albumSeed.Songs.Count > 0)
					? new List<SongInfo>(albumSeed.Songs.Where((SongInfo song) => song != null))
					: new List<SongInfo>();
				int totalCount = Math.Max(albumSeed.TrackCount, knownSongs.Count);
				if (totalCount <= 0)
				{
					MessageBox.Show("无法获取专辑歌曲列表或专辑为空", "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
					return;
				}
				SongDialogLoadState state = new SongDialogLoadState(BuildPlaceholderSongBuffer(totalCount, viewSource));
				for (int i = 0; i < knownSongs.Count && i < state.Songs.Count; i++)
				{
					state.Songs[i] = MergeSongFields(state.Songs[i], knownSongs[i], viewSource);
				}
				string albumName = string.IsNullOrWhiteSpace(albumSeed.Name) ? (string.IsNullOrWhiteSpace(album.Name) ? $"专辑_{albumId}" : album.Name) : albumSeed.Name;
				string albumArtist = string.IsNullOrWhiteSpace(albumSeed.Artist) ? album.Artist : albumSeed.Artist;
				string albumFolderName = string.IsNullOrWhiteSpace(albumArtist) ? albumName : (albumName + " - " + albumArtist);
				List<string> displayNames = state.Songs.Select(BuildSongDownloadDisplayText).ToList();
				BatchDownloadDialog dialog = new BatchDownloadDialog(displayNames, "下载专辑 - " + albumName);
				using CancellationTokenSource enrichCts = new CancellationTokenSource();
				Task enrichTask = EnrichSongDialogByOrderedIdsAsync(viewSource, state, dialog, (CancellationToken ct) => _apiClient.GetAlbumSongIdsAsync(albumId, ct), (List<string> ids, CancellationToken ct) => _apiClient.GetSongsByIdsAsync(ids, ct), enrichCts.Token);
				DialogResult result = dialog.ShowDialog();
				enrichCts.Cancel();
				try
				{
					await enrichTask;
				}
				catch (OperationCanceledException)
				{
				}
				catch (Exception ex)
				{
					Debug.WriteLine($"[DownloadAlbum] 专辑条目补齐失败: {ex.Message}");
				}
				if (result != DialogResult.OK)
				{
					return;
				}
				List<int> selectedIndices = dialog.SelectedIndices;
				if (selectedIndices.Count == 0)
				{
					return;
				}
				var (selectedSongs, originalIndices, missingCount) = await ResolveSelectedSongsFromStateAsync(state, selectedIndices, viewSource, CancellationToken.None);
				if (selectedSongs.Count == 0)
				{
					MessageBox.Show("选中的条目仍在加载中，请稍后重试。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
					return;
				}
				QualityLevel quality = GetCurrentQuality();
				NotifyTransferTaskAdded("正在添加专辑下载任务...");
				List<DownloadTask> tasks = await _downloadManager.AddBatchDownloadAsync(selectedSongs, quality, albumFolderName, albumFolderName, originalIndices);
				string suffix = (missingCount > 0) ? $"（{missingCount} 项尚未加载，已跳过）" : string.Empty;
				NotifyTransferTaskAdded($"已添加 {tasks.Count}/{selectedSongs.Count} 个下载任务，专辑：{albumFolderName}{suffix}");
			}
			catch (Exception ex)
			{
				MessageBox.Show("下载专辑失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			}
		}
	}

	internal async void DownloadPodcast_Click(object? sender, EventArgs e)
	{
		PodcastRadioInfo podcast = GetSelectedPodcastFromContextMenu(sender);
		if (podcast == null || podcast.Id <= 0)
		{
			MessageBox.Show("请选择要下载的播客。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			return;
		}
		checked
		{
			try
			{
				string safeName = (string.IsNullOrWhiteSpace(podcast.Name) ? $"播客_{podcast.Id}" : podcast.Name);
				string viewSource = "podcast:" + podcast.Id.ToString(CultureInfo.InvariantCulture);
				int initialTotal = Math.Max(0, podcast.ProgramCount);
				List<SongInfo> seedSongs = BuildPlaceholderSongBuffer(initialTotal, viewSource);
				List<string> seedTitles = new List<string>(initialTotal);
				EnsureTitleBufferSize(seedTitles, initialTotal);
				PodcastDialogLoadState state = new PodcastDialogLoadState(seedSongs, seedTitles, initialTotal);
				List<string> displayNames = new List<string>(state.Titles);
				BatchDownloadDialog dialog = new BatchDownloadDialog(displayNames, "下载播客 - " + safeName);
				using CancellationTokenSource enrichCts = new CancellationTokenSource();
				Task enrichTask = EnrichPodcastDialogAsync(podcast.Id, viewSource, state, dialog, enrichCts.Token);
				DialogResult result = dialog.ShowDialog();
				enrichCts.Cancel();
				try
				{
					await enrichTask;
				}
				catch (OperationCanceledException)
				{
				}
				catch (Exception ex)
				{
					Debug.WriteLine($"[DownloadPodcast] 播客条目补齐失败: {ex.Message}");
				}
				if (result != DialogResult.OK || dialog.SelectedIndices.Count == 0)
				{
					return;
				}
				List<int> selectedIndices = dialog.SelectedIndices;
				await EnsurePodcastSelectionsLoadedAsync(podcast.Id, state, selectedIndices, viewSource, dialog, CancellationToken.None);
				List<SongInfo> selectedSongs = new List<SongInfo>();
				List<int> originalIndices = new List<int>();
				int missingCount = 0;
				foreach (int index in selectedIndices)
				{
					if (index < 0 || index >= state.Songs.Count)
					{
						missingCount++;
						continue;
					}
					SongInfo song = state.Songs[index];
					if (song == null || IsPlaceholderSong(song))
					{
						missingCount++;
						continue;
					}
					selectedSongs.Add(song);
					originalIndices.Add(index + 1);
				}
				if (selectedSongs.Count == 0)
				{
					MessageBox.Show("选中的节目仍在加载中或缺少可下载音频信息，请稍后重试。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
					return;
				}
				QualityLevel quality = GetCurrentQuality();
				NotifyTransferTaskAdded("正在添加播客下载任务...");
				int addedCount = (await _downloadManager.AddBatchDownloadAsync(selectedSongs, quality, "播客 - " + safeName, safeName, originalIndices)).Count;
				string suffix = (missingCount > 0) ? $"（{missingCount} 项尚未加载，已跳过）" : string.Empty;
				NotifyTransferTaskAdded($"已添加 {addedCount}/{selectedSongs.Count} 个下载任务，播客：{safeName}{suffix}");
			}
			catch (Exception ex)
			{
				MessageBox.Show("下载播客失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			}
		}
	}

	private QualityLevel GetCurrentQuality()
	{
		ConfigModel configModel = _configManager.Load();
		string defaultQuality = configModel.DefaultQuality;
		return NeteaseApiClient.GetQualityLevelFromName(defaultQuality);
	}

	private string GetCurrentViewName()
	{
		if (!string.IsNullOrWhiteSpace(resultListView.AccessibleName))
		{
			return resultListView.AccessibleName.Trim();
		}
		return "下载";
	}

	private sealed class SongDialogLoadState
	{
		public SongDialogLoadState(List<SongInfo> songs)
		{
			Songs = songs ?? new List<SongInfo>();
		}

		public List<SongInfo> Songs { get; }

		public List<string> OrderedSongIds { get; } = new List<string>();

		public Dictionary<string, int> SongIdIndexMap { get; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
	}

	private sealed class PodcastDialogLoadState
	{
		public PodcastDialogLoadState(List<SongInfo> songs, List<string> titles, int totalCount)
		{
			Songs = songs ?? new List<SongInfo>();
			Titles = titles ?? new List<string>();
			TotalCount = Math.Max(0, totalCount);
		}

		public List<SongInfo> Songs { get; }

		public List<string> Titles { get; }

		public int TotalCount { get; set; }
	}

	private static string BuildSongDownloadDisplayText(SongInfo song)
	{
		if (song == null || IsPlaceholderSong(song))
		{
			return "正在加载 ...";
		}

		string name = string.IsNullOrWhiteSpace(song.Name) ? "未知歌曲" : song.Name.Trim();
		string artist = string.IsNullOrWhiteSpace(song.Artist) ? string.Empty : song.Artist.Trim();
		return string.IsNullOrWhiteSpace(artist) ? name : (name + " - " + artist);
	}

	private static string BuildPodcastEpisodeDownloadDisplayText(PodcastEpisodeInfo episode)
	{
		if (episode == null)
		{
			return "正在加载 ...";
		}

		string meta = string.Empty;
		if (episode.PublishTime.HasValue)
		{
			meta = episode.PublishTime.Value.ToString("yyyy-MM-dd");
		}

		if (episode.Duration > TimeSpan.Zero)
		{
			string durationLabel = $"{episode.Duration:mm\\:ss}";
			meta = string.IsNullOrEmpty(meta) ? durationLabel : (meta + " | " + durationLabel);
		}

		string hostLabel = string.Empty;
		if (!string.IsNullOrWhiteSpace(episode.RadioName))
		{
			hostLabel = episode.RadioName;
		}

		if (!string.IsNullOrWhiteSpace(episode.DjName))
		{
			hostLabel = string.IsNullOrWhiteSpace(hostLabel) ? episode.DjName : (hostLabel + " / " + episode.DjName);
		}

		string line = string.IsNullOrWhiteSpace(episode.Name) ? "未命名节目" : episode.Name;
		if (!string.IsNullOrWhiteSpace(meta))
		{
			line = line + " (" + meta + ")";
		}

		if (!string.IsNullOrWhiteSpace(hostLabel))
		{
			line = line + " - " + hostLabel;
		}

		return line;
	}

	private static List<SongInfo> BuildPlaceholderSongBuffer(int totalCount, string viewSource)
	{
		int count = Math.Max(0, totalCount);
		List<SongInfo> placeholders = new List<SongInfo>(count);
		for (int i = 0; i < count; i++)
		{
			placeholders.Add(CreatePlaceholderSong(viewSource));
		}

		return placeholders;
	}

	private static void EnsureSongBufferSize(List<SongInfo> songs, int totalCount, string viewSource)
	{
		if (songs == null)
		{
			return;
		}

		int target = Math.Max(0, totalCount);
		if (songs.Count >= target)
		{
			return;
		}

		for (int i = songs.Count; i < target; i++)
		{
			songs.Add(CreatePlaceholderSong(viewSource));
		}
	}

	private static void EnsureTitleBufferSize(List<string> titles, int totalCount)
	{
		if (titles == null)
		{
			return;
		}

		int target = Math.Max(0, totalCount);
		if (titles.Count >= target)
		{
			return;
		}

		for (int i = titles.Count; i < target; i++)
		{
			titles.Add("正在加载 ...");
		}
	}

	private static bool HasSongDisplayChanged(SongInfo oldSong, SongInfo newSong)
	{
		if (oldSong == null && newSong == null)
		{
			return false;
		}

		if (oldSong == null || newSong == null)
		{
			return true;
		}

		return !string.Equals(oldSong.Id ?? string.Empty, newSong.Id ?? string.Empty, StringComparison.OrdinalIgnoreCase)
			|| !string.Equals(oldSong.Name ?? string.Empty, newSong.Name ?? string.Empty, StringComparison.Ordinal)
			|| !string.Equals(oldSong.Artist ?? string.Empty, newSong.Artist ?? string.Empty, StringComparison.Ordinal);
	}

	private async Task SyncBatchDialogWithCurrentSongsAsync(BatchDownloadDialog dialog, List<SongInfo> songs, string viewSource, CancellationToken cancellationToken)
	{
		if (dialog == null || songs == null)
		{
			return;
		}

		while (!cancellationToken.IsCancellationRequested)
		{
			bool viewMatched = true;
			List<SongInfo> snapshot = null;

			await ExecuteOnUiThreadAsync(delegate
			{
				if (!string.Equals(_currentViewSource, viewSource, StringComparison.OrdinalIgnoreCase))
				{
					viewMatched = false;
					return;
				}

				snapshot = (_currentSongs != null) ? new List<SongInfo>(_currentSongs) : new List<SongInfo>();
			}).ConfigureAwait(continueOnCapturedContext: false);

			if (!viewMatched || snapshot == null)
			{
				return;
			}

			if (snapshot.Count > 0)
			{
				dialog.EnsureItemCount(snapshot.Count);
				EnsureSongBufferSize(songs, snapshot.Count, viewSource);

				Dictionary<int, string> updates = new Dictionary<int, string>();
				for (int i = 0; i < snapshot.Count; i++)
				{
					SongInfo current = snapshot[i] ?? CreatePlaceholderSong(viewSource);
					SongInfo previous = songs[i] ?? CreatePlaceholderSong(viewSource);
					if (HasSongDisplayChanged(previous, current))
					{
						songs[i] = current;
						updates[i] = BuildSongDownloadDisplayText(current);
					}
				}

				if (updates.Count > 0)
				{
					dialog.QueueItemTextUpdates(updates);
				}
			}

			await Task.Delay(250, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		}
	}

	private static void UpdateOrderedSongIdState(SongDialogLoadState state, IReadOnlyList<string> orderedIds)
	{
		if (state == null)
		{
			return;
		}

		state.OrderedSongIds.Clear();
		state.SongIdIndexMap.Clear();
		if (orderedIds == null)
		{
			return;
		}

		for (int i = 0; i < orderedIds.Count; i++)
		{
			string id = orderedIds[i] ?? string.Empty;
			state.OrderedSongIds.Add(id);
			if (!string.IsNullOrWhiteSpace(id) && !state.SongIdIndexMap.ContainsKey(id))
			{
				state.SongIdIndexMap[id] = i;
			}
		}
	}

	private async Task EnrichSongDialogByOrderedIdsAsync(string viewSource, SongDialogLoadState state, BatchDownloadDialog dialog, Func<CancellationToken, Task<List<string>>> orderedIdsLoader, Func<List<string>, CancellationToken, Task<List<SongInfo>>> songBatchLoader, CancellationToken cancellationToken)
	{
		if (state == null || dialog == null || orderedIdsLoader == null || songBatchLoader == null)
		{
			return;
		}

		List<string> orderedIds = await orderedIdsLoader(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		if (orderedIds == null || orderedIds.Count == 0)
		{
			return;
		}

		UpdateOrderedSongIdState(state, orderedIds);
		int totalCount = orderedIds.Count;
		EnsureSongBufferSize(state.Songs, totalCount, viewSource);
		dialog.EnsureItemCount(totalCount);

		Dictionary<int, SongInfo> knownByIndex = new Dictionary<int, SongInfo>();
		for (int i = 0; i < state.Songs.Count; i++)
		{
			SongInfo known = state.Songs[i];
			if (known != null && !IsPlaceholderSong(known) && !string.IsNullOrWhiteSpace(known.Id) && state.SongIdIndexMap.TryGetValue(known.Id, out int index))
			{
				knownByIndex[index] = MergeSongFields(state.Songs[index], known, viewSource);
			}
		}

		for (int i = 0; i < state.Songs.Count; i++)
		{
			state.Songs[i] = CreatePlaceholderSong(viewSource);
		}

		Dictionary<int, string> initialUpdates = new Dictionary<int, string>();
		foreach (KeyValuePair<int, SongInfo> known in knownByIndex)
		{
			if (known.Key >= 0 && known.Key < state.Songs.Count)
			{
				state.Songs[known.Key] = known.Value;
				initialUpdates[known.Key] = BuildSongDownloadDisplayText(known.Value);
			}
		}

		if (initialUpdates.Count > 0)
		{
			dialog.QueueItemTextUpdates(initialUpdates);
		}

		bool[] loadedFlags = new bool[totalCount];
		int loadedCount = 0;
		for (int i = 0; i < totalCount; i++)
		{
			SongInfo current = (i < state.Songs.Count) ? state.Songs[i] : null;
			if (current != null && !string.IsNullOrWhiteSpace(current.Id) && string.Equals(current.Id, orderedIds[i], StringComparison.OrdinalIgnoreCase))
			{
				loadedFlags[i] = true;
				loadedCount++;
			}
		}

		List<int> pendingBatchStarts = new List<int>();
		for (int start = 0; start < totalCount; start += 200)
		{
			int end = Math.Min(start + 200, totalCount);
			bool hasMissing = false;
			for (int i = start; i < end; i++)
			{
				if (!loadedFlags[i])
				{
					hasMissing = true;
					break;
				}
			}

			if (hasMissing)
			{
				pendingBatchStarts.Add(start);
			}
		}

		int batchFetched = 0;
		while (pendingBatchStarts.Count > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();

			int focusedIndex = dialog.GetPreferredLoadIndex();
			int focusedBatchStart = (focusedIndex >= 0) ? (focusedIndex / 200) * 200 : -1;
			int batchStart = (focusedBatchStart >= 0 && pendingBatchStarts.Contains(focusedBatchStart)) ? focusedBatchStart : pendingBatchStarts[0];
			pendingBatchStarts.Remove(batchStart);

			int batchEnd = Math.Min(batchStart + 200, totalCount);
			List<string> idsToFetch = new List<string>();
			for (int i = batchStart; i < batchEnd; i++)
			{
				if (!loadedFlags[i])
				{
					string id = orderedIds[i];
					if (!string.IsNullOrWhiteSpace(id))
					{
						idsToFetch.Add(id);
					}
				}
			}

			if (idsToFetch.Count == 0)
			{
				continue;
			}

			if (batchFetched > 0)
			{
				await Task.Delay(1500, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			}

			batchFetched++;
			List<SongInfo> fetchedSongs = await songBatchLoader(idsToFetch, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			if (fetchedSongs == null || fetchedSongs.Count == 0)
			{
				continue;
			}

			Dictionary<int, SongInfo> updates = new Dictionary<int, SongInfo>();
			foreach (SongInfo fetched in fetchedSongs)
			{
				if (fetched == null || string.IsNullOrWhiteSpace(fetched.Id) || !state.SongIdIndexMap.TryGetValue(fetched.Id, out int index) || index < 0 || index >= state.Songs.Count)
				{
					continue;
				}

				SongInfo merged = MergeSongFields(state.Songs[index], fetched, viewSource);
				state.Songs[index] = merged;
				updates[index] = merged;
				if (!loadedFlags[index])
				{
					loadedFlags[index] = true;
					loadedCount++;
				}
			}

			if (updates.Count > 0)
			{
				Dictionary<int, string> displayUpdates = updates.ToDictionary(k => k.Key, v => BuildSongDownloadDisplayText(v.Value));
				dialog.QueueItemTextUpdates(displayUpdates);
			}
		}
	}

	private async Task<(List<SongInfo> Songs, List<int> OriginalIndices, int MissingCount)> ResolveSelectedSongsFromStateAsync(SongDialogLoadState state, List<int> selectedIndices, string viewSource, CancellationToken cancellationToken)
	{
		List<SongInfo> selectedSongs = new List<SongInfo>();
		List<int> originalIndices = new List<int>();
		int missingCount = 0;

		if (state == null || selectedIndices == null || selectedIndices.Count == 0)
		{
			return (selectedSongs, originalIndices, missingCount);
		}

		foreach (int index in selectedIndices)
		{
			if (index < 0 || index >= state.Songs.Count)
			{
				missingCount++;
				continue;
			}

			SongInfo song = state.Songs[index];
			if (song == null || IsPlaceholderSong(song))
			{
				song = await ResolveSongForSelectionAsync(state, viewSource, index, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				if (song != null && !IsPlaceholderSong(song))
				{
					state.Songs[index] = song;
				}
			}

			if (song == null || IsPlaceholderSong(song))
			{
				missingCount++;
				continue;
			}

			selectedSongs.Add(song);
			originalIndices.Add(index + 1);
		}

		return (selectedSongs, originalIndices, missingCount);
	}

	private async Task<SongInfo> ResolveSongForSelectionAsync(SongDialogLoadState state, string viewSource, int index, CancellationToken cancellationToken)
	{
		if (state != null && index >= 0 && index < state.OrderedSongIds.Count)
		{
			string songId = state.OrderedSongIds[index];
			if (!string.IsNullOrWhiteSpace(songId))
			{
				List<SongInfo> fetched = await FetchPlaylistSongsByIdsBatchAsync(new List<string> { songId }, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				SongInfo fetchedSong = fetched?.FirstOrDefault((SongInfo s) => s != null && string.Equals(s.Id, songId, StringComparison.OrdinalIgnoreCase));
				if (fetchedSong != null && !IsPlaceholderSong(fetchedSong))
				{
					return MergeSongFields(state.Songs[index], fetchedSong, viewSource);
				}
			}
		}

		SongInfo fallback = await ResolvePlaceholderSongByIndexAsync(viewSource, index, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		if (fallback != null && !IsPlaceholderSong(fallback))
		{
			return fallback;
		}

		return CreatePlaceholderSong(viewSource);
	}

	private async Task EnrichPodcastDialogAsync(long podcastId, string viewSource, PodcastDialogLoadState state, BatchDownloadDialog dialog, CancellationToken cancellationToken)
	{
		if (podcastId <= 0 || state == null || dialog == null)
		{
			return;
		}

		int totalCount = Math.Max(state.TotalCount, state.Songs.Count);
		if (totalCount > 0)
		{
			state.TotalCount = totalCount;
			EnsureSongBufferSize(state.Songs, totalCount, viewSource);
			EnsureTitleBufferSize(state.Titles, totalCount);
			dialog.EnsureItemCount(totalCount);
		}

		HashSet<int> pendingPages = new HashSet<int>();
		if (totalCount > 0)
		{
			for (int start = 0; start < totalCount; start += 100)
			{
				pendingPages.Add(start);
			}
		}
		else
		{
			pendingPages.Add(0);
		}

		while (pendingPages.Count > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();

			int focusedIndex = dialog.GetPreferredLoadIndex();
			int focusedPage = (focusedIndex >= 0) ? (focusedIndex / 100) * 100 : -1;
			int pageStart = (focusedPage >= 0 && pendingPages.Contains(focusedPage)) ? focusedPage : pendingPages.First();
			pendingPages.Remove(pageStart);

			var (episodes, hasMore, reportedTotal) = await _apiClient.GetPodcastEpisodesAsync(podcastId, 100, pageStart).ConfigureAwait(continueOnCapturedContext: false);
			int requiredCount = totalCount;
			if (reportedTotal > requiredCount)
			{
				requiredCount = reportedTotal;
			}
			if (episodes != null && episodes.Count > 0)
			{
				requiredCount = Math.Max(requiredCount, pageStart + episodes.Count);
			}
			if (requiredCount > totalCount)
			{
				totalCount = requiredCount;
				state.TotalCount = totalCount;
				EnsureSongBufferSize(state.Songs, totalCount, viewSource);
				EnsureTitleBufferSize(state.Titles, totalCount);
				dialog.EnsureItemCount(totalCount);
				for (int extra = 0; extra < totalCount; extra += 100)
				{
					if (extra >= pageStart + 100)
					{
						pendingPages.Add(extra);
					}
				}
			}

			if (episodes == null || episodes.Count == 0)
			{
				if (!hasMore)
				{
					continue;
				}

				if (pageStart + 100 < totalCount)
				{
					pendingPages.Add(pageStart + 100);
				}
				continue;
			}

			Dictionary<int, string> titleUpdates = new Dictionary<int, string>();
			for (int i = 0; i < episodes.Count; i++)
			{
				int index = pageStart + i;
				if (index < 0 || index >= state.Songs.Count)
				{
					continue;
				}

				PodcastEpisodeInfo episode = episodes[i];
				SongInfo mergedSong = MergeSongFields(state.Songs[index], episode?.Song, viewSource);
				state.Songs[index] = mergedSong;
				string title = BuildPodcastEpisodeDownloadDisplayText(episode);
				state.Titles[index] = title;
				titleUpdates[index] = title;
			}

			if (titleUpdates.Count > 0)
			{
				dialog.QueueItemTextUpdates(titleUpdates);
			}

			if (hasMore)
			{
				int nextPage = pageStart + 100;
				if (nextPage < totalCount || episodes.Count > 0)
				{
					pendingPages.Add(nextPage);
				}
			}
		}
	}

	private async Task<int> EnsurePodcastSelectionsLoadedAsync(long podcastId, PodcastDialogLoadState state, IEnumerable<int> selectedIndices, string viewSource, BatchDownloadDialog dialog, CancellationToken cancellationToken)
	{
		if (podcastId <= 0 || state == null || selectedIndices == null)
		{
			return 0;
		}

		HashSet<int> pages = new HashSet<int>();
		foreach (int index in selectedIndices)
		{
			if (index < 0 || index >= state.Songs.Count)
			{
				continue;
			}

			if (IsPlaceholderSong(state.Songs[index]))
			{
				pages.Add((index / 100) * 100);
			}
		}

		int loaded = 0;
		foreach (int pageStart in pages)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var (episodes, _, _) = await _apiClient.GetPodcastEpisodesAsync(podcastId, 100, pageStart).ConfigureAwait(continueOnCapturedContext: false);
			if (episodes == null || episodes.Count == 0)
			{
				continue;
			}

			Dictionary<int, string> titleUpdates = new Dictionary<int, string>();
			for (int i = 0; i < episodes.Count; i++)
			{
				int index = pageStart + i;
				if (index < 0 || index >= state.Songs.Count)
				{
					continue;
				}

				PodcastEpisodeInfo episode = episodes[i];
				SongInfo mergedSong = MergeSongFields(state.Songs[index], episode?.Song, viewSource);
				if (IsPlaceholderSong(state.Songs[index]) && !IsPlaceholderSong(mergedSong))
				{
					loaded++;
				}
				state.Songs[index] = mergedSong;
				string title = BuildPodcastEpisodeDownloadDisplayText(episode);
				state.Titles[index] = title;
				titleUpdates[index] = title;
			}

			if (titleUpdates.Count > 0 && dialog != null)
			{
				dialog.QueueItemTextUpdates(titleUpdates);
			}
		}

		return loaded;
	}

	private async Task<List<SongInfo>> FetchPlaylistSongsForDownloadAsync(PlaylistInfo playlist)
	{
		if (playlist == null || string.IsNullOrWhiteSpace(playlist.Id))
		{
			return new List<SongInfo>();
		}

		try
		{
			List<SongInfo> fullSongs = await _apiClient.GetPlaylistSongsAsync(playlist.Id);
			if (fullSongs != null && fullSongs.Count > 0)
			{
				return fullSongs;
			}
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"[DownloadPlaylist] 全量拉取歌单歌曲失败，降级到详情接口: {ex.Message}");
		}

		try
		{
			PlaylistInfo detail = await _apiClient.GetPlaylistDetailAsync(playlist.Id);
			if (detail?.Songs != null && detail.Songs.Count > 0)
			{
				return detail.Songs;
			}
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"[DownloadPlaylist] 歌单详情回退获取失败: {ex.Message}");
		}

		return new List<SongInfo>();
	}

	internal async void DownloadCategory_Click(object? sender, EventArgs e)
	{
		ListViewItem selectedItem = GetSelectedListViewItemSafe();
		if (selectedItem == null)
		{
			return;
		}
		object tag = selectedItem.Tag;
		ListItemInfo listItem = tag as ListItemInfo;
		if (listItem == null || listItem.Type != ListItemType.Category)
		{
			return;
		}
		try
		{
			Cursor originalCursor = System.Windows.Forms.Cursor.Current;
			System.Windows.Forms.Cursor.Current = Cursors.WaitCursor;
			try
			{
				string categoryId = listItem.CategoryId;
				string categoryName = listItem.CategoryName ?? listItem.CategoryId;
				NotifyTransferTaskAdded("正在添加分类下载任务...");
				QualityLevel quality = GetCurrentQuality();
				int totalTasks;
				switch (categoryId)
				{
				case "user_liked_songs":
					totalTasks = await DownloadSongListCategory(categoryName, async delegate
					{
						UserAccountInfo userInfo = await _apiClient.GetUserAccountAsync();
						if (userInfo == null || userInfo.UserId <= 0)
						{
							throw new Exception("获取用户信息失败");
						}
						List<string> likedIds = await _apiClient.GetUserLikedSongsAsync(userInfo.UserId);
						if (likedIds == null || likedIds.Count == 0)
						{
							throw new Exception("您还没有喜欢的歌曲");
						}
						List<SongInfo> allSongs = new List<SongInfo>();
						for (int i = 0; i < likedIds.Count; i = checked(i + 100))
						{
							string[] batchIds = likedIds.Skip(i).Take(100).ToArray();
							List<SongInfo> songs = await _apiClient.GetSongDetailAsync(batchIds);
							if (songs != null)
							{
								allSongs.AddRange(songs);
							}
						}
						return allSongs;
					}, quality);
					break;
				case "daily_recommend_songs":
					totalTasks = await DownloadSongListCategory(categoryName, async delegate
					{
						List<SongInfo> songs = await _apiClient.GetDailyRecommendSongsAsync();
						if (songs == null || songs.Count == 0)
						{
							throw new Exception("获取每日推荐歌曲失败");
						}
						return songs;
					}, quality);
					break;
				case "personalized_newsongs":
					totalTasks = await DownloadSongListCategory(categoryName, async delegate
					{
						List<SongInfo> songs = await _apiClient.GetPersonalizedNewSongsAsync(20);
						if (songs == null || songs.Count == 0)
						{
							throw new Exception("获取推荐新歌失败");
						}
						return songs;
					}, quality);
					break;
				case "user_playlists":
					totalTasks = await DownloadPlaylistListCategory(categoryName, async delegate
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
					totalTasks = await DownloadPlaylistListCategory(categoryName, async delegate
					{
						List<PlaylistInfo> toplists = await _apiClient.GetToplistAsync();
						if (toplists == null || toplists.Count == 0)
						{
							throw new Exception("获取排行榜失败");
						}
						return toplists;
					}, quality);
					break;
				case "daily_recommend_playlists":
					totalTasks = await DownloadPlaylistListCategory(categoryName, async delegate
					{
						List<PlaylistInfo> playlists = await _apiClient.GetDailyRecommendPlaylistsAsync();
						if (playlists == null || playlists.Count == 0)
						{
							throw new Exception("获取每日推荐歌单失败");
						}
						return playlists;
					}, quality);
					break;
				case "personalized_playlists":
					totalTasks = await DownloadPlaylistListCategory(categoryName, async delegate
					{
						List<PlaylistInfo> playlists = await _apiClient.GetPersonalizedPlaylistsAsync();
						if (playlists == null || playlists.Count == 0)
						{
							throw new Exception("获取推荐歌单失败");
						}
						return playlists;
					}, quality);
					break;
				case "user_albums":
					totalTasks = await DownloadAlbumListCategory(categoryName, async delegate
					{
						(List<AlbumInfo>, int) tuple = await _apiClient.GetUserAlbumsAsync();
						var (albums, _) = tuple;
						_ = tuple;
						if (albums == null || albums.Count == 0)
						{
							throw new Exception("您还没有收藏专辑");
						}
						return albums;
					}, quality);
					break;
				case "daily_recommend":
					await DownloadMixedCategory(categoryName, () => new List<ListItemInfo>
					{
						new ListItemInfo
						{
							Type = ListItemType.Category,
							CategoryId = "daily_recommend_songs",
							CategoryName = "每日推荐歌曲",
							CategoryDescription = "根据您的听歌习惯推荐的歌曲"
						},
						new ListItemInfo
						{
							Type = ListItemType.Category,
							CategoryId = "daily_recommend_playlists",
							CategoryName = "每日推荐歌单",
							CategoryDescription = "根据您的听歌习惯推荐的歌单"
						}
					}, quality);
					return;
				case "personalized":
					await DownloadMixedCategory(categoryName, () => new List<ListItemInfo>
					{
						new ListItemInfo
						{
							Type = ListItemType.Category,
							CategoryId = "personalized_playlists",
							CategoryName = "推荐歌单",
							CategoryDescription = "根据您的听歌习惯推荐的歌单"
						},
						new ListItemInfo
						{
							Type = ListItemType.Category,
							CategoryId = "personalized_newsongs",
							CategoryName = "推荐新歌",
							CategoryDescription = "最新发行的歌曲推荐"
						}
					}, quality);
					return;
				case "user_play_record":
					await DownloadMixedCategory(categoryName, () => new List<ListItemInfo>
					{
						new ListItemInfo
						{
							Type = ListItemType.Category,
							CategoryId = "user_play_record_week",
							CategoryName = "周榜单",
							CategoryDescription = "最近一周的听歌排行"
						},
						new ListItemInfo
						{
							Type = ListItemType.Category,
							CategoryId = "user_play_record_all",
							CategoryName = "全部时间",
							CategoryDescription = "所有时间的听歌排行"
						}
					}, quality);
					return;
				case "user_play_record_week":
					totalTasks = await DownloadSongListCategory(categoryName, async delegate
					{
						UserAccountInfo userInfo = await _apiClient.GetUserAccountAsync();
						if (userInfo == null || userInfo.UserId <= 0)
						{
							throw new Exception("获取用户信息失败");
						}
						List<(SongInfo song, int playCount)> playRecords = await _apiClient.GetUserPlayRecordAsync(userInfo.UserId, 1);
						if (playRecords == null || playRecords.Count == 0)
						{
							throw new Exception("暂无周榜单听歌记录");
						}
						return playRecords.Select(((SongInfo song, int playCount) r) => r.song).ToList();
					}, quality);
					break;
				case "user_play_record_all":
					totalTasks = await DownloadSongListCategory(categoryName, async delegate
					{
						UserAccountInfo userInfo = await _apiClient.GetUserAccountAsync();
						if (userInfo == null || userInfo.UserId <= 0)
						{
							throw new Exception("获取用户信息失败");
						}
						List<(SongInfo song, int playCount)> playRecords = await _apiClient.GetUserPlayRecordAsync(userInfo.UserId);
						if (playRecords == null || playRecords.Count == 0)
						{
							throw new Exception("暂无全部时间听歌记录");
						}
						return playRecords.Select(((SongInfo song, int playCount) r) => r.song).ToList();
					}, quality);
					break;
				case "highquality_playlists":
					totalTasks = await DownloadPlaylistListCategory(categoryName, async delegate
					{
						List<PlaylistInfo> playlists = await FetchHighQualityPlaylistsForDownloadAsync();
						if (playlists == null || playlists.Count == 0)
						{
							throw new Exception("\u83B7\u53D6\u7CBE\u54C1\u6B4C\u5355\u5931\u8D25");
						}
						return playlists;
					}, quality);
					break;
				case "new_songs":
					await DownloadMixedCategory(categoryName, () => new List<ListItemInfo>
					{
						new ListItemInfo
						{
							Type = ListItemType.Category,
							CategoryId = "new_songs_all",
							CategoryName = "全部",
							CategoryDescription = "全部地区新歌"
						},
						new ListItemInfo
						{
							Type = ListItemType.Category,
							CategoryId = "new_songs_chinese",
							CategoryName = "华语",
							CategoryDescription = "华语新歌"
						},
						new ListItemInfo
						{
							Type = ListItemType.Category,
							CategoryId = "new_songs_western",
							CategoryName = "欧美",
							CategoryDescription = "欧美新歌"
						},
						new ListItemInfo
						{
							Type = ListItemType.Category,
							CategoryId = "new_songs_japan",
							CategoryName = "日本",
							CategoryDescription = "日本新歌"
						},
						new ListItemInfo
						{
							Type = ListItemType.Category,
							CategoryId = "new_songs_korea",
							CategoryName = "韩国",
							CategoryDescription = "韩国新歌"
						}
					}, quality);
					return;
				case "new_songs_all":
					totalTasks = await DownloadSongListCategory(categoryName, async delegate
					{
						List<SongInfo> songs = await _apiClient.GetNewSongsAsync();
						if (songs == null || songs.Count == 0)
						{
							throw new Exception("获取全部新歌失败");
						}
						return songs;
					}, quality);
					break;
				case "new_songs_chinese":
					totalTasks = await DownloadSongListCategory(categoryName, async delegate
					{
						List<SongInfo> songs = await _apiClient.GetNewSongsAsync(7);
						if (songs == null || songs.Count == 0)
						{
							throw new Exception("获取华语新歌失败");
						}
						return songs;
					}, quality);
					break;
				case "new_songs_western":
					totalTasks = await DownloadSongListCategory(categoryName, async delegate
					{
						List<SongInfo> songs = await _apiClient.GetNewSongsAsync(96);
						if (songs == null || songs.Count == 0)
						{
							throw new Exception("获取欧美新歌失败");
						}
						return songs;
					}, quality);
					break;
				case "new_songs_japan":
					totalTasks = await DownloadSongListCategory(categoryName, async delegate
					{
						List<SongInfo> songs = await _apiClient.GetNewSongsAsync(8);
						if (songs == null || songs.Count == 0)
						{
							throw new Exception("获取日本新歌失败");
						}
						return songs;
					}, quality);
					break;
				case "new_songs_korea":
					totalTasks = await DownloadSongListCategory(categoryName, async delegate
					{
						List<SongInfo> songs = await _apiClient.GetNewSongsAsync(16);
						if (songs == null || songs.Count == 0)
						{
							throw new Exception("获取韩国新歌失败");
						}
						return songs;
					}, quality);
					break;
				case "recent_playlists":
					totalTasks = await DownloadPlaylistListCategory(categoryName, async delegate
					{
						List<PlaylistInfo> playlists = await _apiClient.GetRecentPlaylistsAsync();
						if (playlists == null || playlists.Count == 0)
						{
							throw new Exception("暂无最近播放的歌单");
						}
						return playlists;
					}, quality);
					break;
				case "recent_albums":
					totalTasks = await DownloadAlbumListCategory(categoryName, async delegate
					{
						List<AlbumInfo> albums = await _apiClient.GetRecentAlbumsAsync();
						if (albums == null || albums.Count == 0)
						{
							throw new Exception("暂无最近播放的专辑");
						}
						return albums;
					}, quality);
					break;
				case "recent_listened":
					await DownloadMixedCategory(categoryName, BuildRecentListenedEntries, quality);
					return;
				case "playlist_category":
					await DownloadMixedCategory(categoryName, delegate
					{
						List<ListItemInfo> list = new List<ListItemInfo>();
						string[] array = new string[10] { "华语", "流行", "摇滚", "民谣", "电子", "轻音乐", "影视原声", "ACG", "怀旧", "治愈" };
						string[] array2 = array;
						string[] array3 = array2;
						foreach (string text in array3)
						{
							list.Add(new ListItemInfo
							{
								Type = ListItemType.Category,
								CategoryId = "playlist_cat_" + text,
								CategoryName = text,
								CategoryDescription = text + "歌单"
							});
						}
						return list;
					}, quality);
					return;
				case "new_albums":
					totalTasks = await DownloadAlbumListCategory(categoryName, async delegate
					{
						List<AlbumInfo> albums = await _apiClient.GetNewAlbumsAsync();
						if (albums == null || albums.Count == 0)
						{
							throw new Exception("暂无新碟上架");
						}
						return albums;
					}, quality);
					break;
				case "recent_played":
					totalTasks = await DownloadSongListCategory(categoryName, async delegate
					{
						List<SongInfo> songs = await _apiClient.GetRecentPlayedSongsAsync();
						if (songs == null || songs.Count == 0)
						{
							throw new Exception("暂无最近播放记录");
						}
						return songs;
					}, quality);
					break;
				default:
					if (categoryId.StartsWith("playlist_cat_"))
					{
						string catName = categoryId.Substring("playlist_cat_".Length);
						totalTasks = await DownloadPlaylistListCategory(catName, async delegate
						{
							var (playlists, _, _) = await _apiClient.GetPlaylistsByCategoryAsync(catName);
							if (playlists == null || playlists.Count == 0)
							{
								throw new Exception("获取" + catName + "歌单失败");
							}
							return playlists;
						}, quality);
						break;
					}
					MessageBox.Show("暂不支持下载该分类: " + categoryName, "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
					return;
				}
				if (totalTasks > 0)
				{
					NotifyTransferTaskAdded($"已添加 {totalTasks} 个下载任务，分类：{categoryName}");
				}
			}
			finally
			{
				System.Windows.Forms.Cursor.Current = originalCursor;
			}
		}
		catch (Exception ex)
		{
			MessageBox.Show("下载分类失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
		}
	}

	private async Task<List<PlaylistInfo>> FetchHighQualityPlaylistsForDownloadAsync()
	{
		if (_enableHighQualityPlaylistsAll)
		{
			List<PlaylistInfo> all = new List<PlaylistInfo>();
			HashSet<string> seenIds = new HashSet<string>(StringComparer.Ordinal);
			long before = 0;
			bool hasMore = true;
			int safety = 0;
			while (hasMore && safety < 200)
			{
				(List<PlaylistInfo> Items, long LastTime, bool HasMore) result = await _apiClient.GetHighQualityPlaylistsAsync("\u5168\u90E8", HighQualityPlaylistsPageSize, before);
				List<PlaylistInfo> pageItems = result.Items ?? new List<PlaylistInfo>();
				if (pageItems.Count == 0)
				{
					break;
				}
				foreach (PlaylistInfo playlist in pageItems)
				{
					if (playlist == null || string.IsNullOrWhiteSpace(playlist.Id))
					{
						continue;
					}
					if (seenIds.Add(playlist.Id))
					{
						all.Add(playlist);
					}
				}
				long nextBefore = result.LastTime;
				hasMore = result.HasMore;
				if (pageItems.Count < HighQualityPlaylistsPageSize || nextBefore <= 0 || nextBefore == before)
				{
					hasMore = false;
				}
				before = nextBefore;
				safety++;
			}
			return all;
		}

		var (playlists, _, _) = await _apiClient.GetHighQualityPlaylistsAsync("\u5168\u90E8", HighQualityPlaylistsPageSize, 0L);
		return playlists ?? new List<PlaylistInfo>();
	}



	private async Task<int> DownloadSongListCategory(string categoryName, Func<Task<List<SongInfo>>> getSongsFunc, QualityLevel quality, string? parentDirectory = null, bool showDialog = true)
	{
		List<SongInfo> songs = await getSongsFunc();
		checked
		{
			List<SongInfo> selectedSongs;
			List<int> originalIndices;
			if (showDialog)
			{
				List<string> displayNames = new List<string>();
				for (int i = 0; i < songs.Count; i++)
				{
					SongInfo song = songs[i];
					displayNames.Add($"{song.Name} - {song.Artist}");
				}
				BatchDownloadDialog dialog = new BatchDownloadDialog(displayNames, "下载分类 - " + categoryName);
				if (dialog.ShowDialog() != DialogResult.OK || dialog.SelectedIndices.Count == 0)
				{
					return 0;
				}
				List<int> selectedIndicesList = dialog.SelectedIndices;
				selectedSongs = selectedIndicesList.Select((int index) => songs[index]).ToList();
				originalIndices = selectedIndicesList.Select((int num) => num + 1).ToList();
			}
			else
			{
				selectedSongs = songs;
				originalIndices = Enumerable.Range(1, songs.Count).ToList();
			}
			string fullDirectory = (string.IsNullOrEmpty(parentDirectory) ? categoryName : Path.Combine(parentDirectory, categoryName));
			return (await _downloadManager.AddBatchDownloadAsync(selectedSongs, quality, categoryName, fullDirectory, originalIndices)).Count;
		}
	}

	private async Task<int> DownloadPlaylistListCategory(string categoryName, Func<Task<List<PlaylistInfo>>> getPlaylistsFunc, QualityLevel quality, string? parentDirectory = null, bool showDialog = true)
	{
		List<PlaylistInfo> playlists = await getPlaylistsFunc();
		List<PlaylistInfo> selectedPlaylists;
		if (showDialog)
		{
			List<string> displayNames = playlists.Select((PlaylistInfo p) => p.Name).ToList();
			BatchDownloadDialog dialog = new BatchDownloadDialog(displayNames, "下载分类 - " + categoryName);
			if (dialog.ShowDialog() != DialogResult.OK || dialog.SelectedIndices.Count == 0)
			{
				return 0;
			}
			List<int> selectedIndices = dialog.SelectedIndices;
			selectedPlaylists = selectedIndices.Select((int i) => playlists[i]).ToList();
		}
		else
		{
			selectedPlaylists = playlists;
		}
		int totalTasks = 0;
		foreach (PlaylistInfo playlist in selectedPlaylists)
		{
			List<SongInfo> playlistSongs = await FetchPlaylistSongsForDownloadAsync(playlist);
			if (playlistSongs.Count > 0)
			{
				string baseDirectory = (string.IsNullOrEmpty(parentDirectory) ? categoryName : Path.Combine(parentDirectory, categoryName));
				string subDirectory = Path.Combine(baseDirectory, playlist.Name);
				List<int> originalIndices = Enumerable.Range(1, playlistSongs.Count).ToList();
				int num = totalTasks;
				totalTasks = checked(num + (await _downloadManager.AddBatchDownloadAsync(playlistSongs, quality, playlist.Name, subDirectory, originalIndices)).Count);
			}
		}
		return totalTasks;
	}

	private async Task<int> DownloadAlbumListCategory(string categoryName, Func<Task<List<AlbumInfo>>> getAlbumsFunc, QualityLevel quality, string? parentDirectory = null, bool showDialog = true)
	{
		List<AlbumInfo> albums = await getAlbumsFunc();
		List<AlbumInfo> selectedAlbums;
		if (showDialog)
		{
			List<string> displayNames = albums.Select((AlbumInfo a) => a.Name + " - " + a.Artist).ToList();
			BatchDownloadDialog dialog = new BatchDownloadDialog(displayNames, "下载分类 - " + categoryName);
			if (dialog.ShowDialog() != DialogResult.OK || dialog.SelectedIndices.Count == 0)
			{
				return 0;
			}
			List<int> selectedIndices = dialog.SelectedIndices;
			selectedAlbums = selectedIndices.Select((int i) => albums[i]).ToList();
		}
		else
		{
			selectedAlbums = albums;
		}
		int totalTasks = 0;
		foreach (AlbumInfo album in selectedAlbums)
		{
			List<SongInfo> songs = await _apiClient.GetAlbumSongsAsync(album.Id);
			if (songs != null && songs.Count > 0)
			{
				string baseDirectory = (string.IsNullOrEmpty(parentDirectory) ? categoryName : Path.Combine(parentDirectory, categoryName));
				string albumFolderName = album.Name + " - " + album.Artist;
				string subDirectory = Path.Combine(baseDirectory, albumFolderName);
				List<int> originalIndices = Enumerable.Range(1, songs.Count).ToList();
				int num = totalTasks;
				totalTasks = checked(num + (await _downloadManager.AddBatchDownloadAsync(songs, quality, albumFolderName, subDirectory, originalIndices)).Count);
			}
		}
		return totalTasks;
	}

	private async Task DownloadMixedCategory(string categoryName, Func<List<ListItemInfo>> getSubCategoriesFunc, QualityLevel quality)
	{
		List<ListItemInfo> subCategories = getSubCategoriesFunc();
		if (subCategories == null || subCategories.Count == 0)
		{
			MessageBox.Show("该分类下没有可用的子分类", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			return;
		}
		List<string> displayNames = subCategories.Select((ListItemInfo item) => item.CategoryName ?? item.CategoryId ?? "未命名分类").ToList();
		BatchDownloadDialog dialog = new BatchDownloadDialog(displayNames, "下载分类 - " + categoryName);
		if (dialog.ShowDialog() != DialogResult.OK || dialog.SelectedIndices.Count == 0)
		{
			return;
		}
		List<ListItemInfo> selectedSubCategories = dialog.SelectedIndices.Select((int i) => subCategories[i]).ToList();
		int totalTasks = 0;
		Cursor originalCursor = System.Windows.Forms.Cursor.Current;
		System.Windows.Forms.Cursor.Current = Cursors.WaitCursor;
		try
		{
			foreach (ListItemInfo subCategory in selectedSubCategories)
			{
				string subCategoryId = subCategory.CategoryId;
				string subCategoryName = subCategory.CategoryName ?? subCategoryId;
				try
				{
					int taskCount;
					switch (subCategoryId)
					{
					case "daily_recommend_songs":
						taskCount = await DownloadSongListCategory(subCategoryName, async delegate
						{
							List<SongInfo> songs = await _apiClient.GetDailyRecommendSongsAsync();
							if (songs == null || songs.Count == 0)
							{
								throw new Exception("获取每日推荐歌曲失败");
							}
							return songs;
						}, quality, categoryName, showDialog: false);
						break;
					case "personalized_newsongs":
						taskCount = await DownloadSongListCategory(subCategoryName, async delegate
						{
							List<SongInfo> songs = await _apiClient.GetPersonalizedNewSongsAsync(20);
							if (songs == null || songs.Count == 0)
							{
								throw new Exception("获取推荐新歌失败");
							}
							return songs;
						}, quality, categoryName, showDialog: false);
						break;
					case "daily_recommend_playlists":
						taskCount = await DownloadPlaylistListCategory(subCategoryName, async delegate
						{
							List<PlaylistInfo> playlists = await _apiClient.GetDailyRecommendPlaylistsAsync();
							if (playlists == null || playlists.Count == 0)
							{
								throw new Exception("获取每日推荐歌单失败");
							}
							return playlists;
						}, quality, categoryName, showDialog: false);
						break;
					case "personalized_playlists":
						taskCount = await DownloadPlaylistListCategory(subCategoryName, async delegate
						{
							List<PlaylistInfo> playlists = await _apiClient.GetPersonalizedPlaylistsAsync();
							if (playlists == null || playlists.Count == 0)
							{
								throw new Exception("获取推荐歌单失败");
							}
							return playlists;
						}, quality, categoryName, showDialog: false);
						break;
					case "user_play_record_week":
						taskCount = await DownloadSongListCategory(subCategoryName, async delegate
						{
							UserAccountInfo userInfo = await _apiClient.GetUserAccountAsync();
							if (userInfo == null || userInfo.UserId <= 0)
							{
								throw new Exception("获取用户信息失败");
							}
							List<(SongInfo song, int playCount)> playRecords = await _apiClient.GetUserPlayRecordAsync(userInfo.UserId, 1);
							if (playRecords == null || playRecords.Count == 0)
							{
								throw new Exception("暂无周榜单听歌记录");
							}
							return playRecords.Select(((SongInfo song, int playCount) r) => r.song).ToList();
						}, quality, categoryName, showDialog: false);
						break;
					case "user_play_record_all":
						taskCount = await DownloadSongListCategory(subCategoryName, async delegate
						{
							UserAccountInfo userInfo = await _apiClient.GetUserAccountAsync();
							if (userInfo == null || userInfo.UserId <= 0)
							{
								throw new Exception("获取用户信息失败");
							}
							List<(SongInfo song, int playCount)> playRecords = await _apiClient.GetUserPlayRecordAsync(userInfo.UserId);
							if (playRecords == null || playRecords.Count == 0)
							{
								throw new Exception("暂无全部时间听歌记录");
							}
							return playRecords.Select(((SongInfo song, int playCount) r) => r.song).ToList();
						}, quality, categoryName, showDialog: false);
						break;
					case "new_songs_all":
						taskCount = await DownloadSongListCategory(subCategoryName, async delegate
						{
							List<SongInfo> songs = await _apiClient.GetNewSongsAsync();
							if (songs == null || songs.Count == 0)
							{
								throw new Exception("获取全部新歌失败");
							}
							return songs;
						}, quality, categoryName, showDialog: false);
						break;
					case "new_songs_chinese":
						taskCount = await DownloadSongListCategory(subCategoryName, async delegate
						{
							List<SongInfo> songs = await _apiClient.GetNewSongsAsync(7);
							if (songs == null || songs.Count == 0)
							{
								throw new Exception("获取华语新歌失败");
							}
							return songs;
						}, quality, categoryName, showDialog: false);
						break;
					case "new_songs_western":
						taskCount = await DownloadSongListCategory(subCategoryName, async delegate
						{
							List<SongInfo> songs = await _apiClient.GetNewSongsAsync(96);
							if (songs == null || songs.Count == 0)
							{
								throw new Exception("获取欧美新歌失败");
							}
							return songs;
						}, quality, categoryName, showDialog: false);
						break;
					case "new_songs_japan":
						taskCount = await DownloadSongListCategory(subCategoryName, async delegate
						{
							List<SongInfo> songs = await _apiClient.GetNewSongsAsync(8);
							if (songs == null || songs.Count == 0)
							{
								throw new Exception("获取日本新歌失败");
							}
							return songs;
						}, quality, categoryName, showDialog: false);
						break;
					case "new_songs_korea":
						taskCount = await DownloadSongListCategory(subCategoryName, async delegate
						{
							List<SongInfo> songs = await _apiClient.GetNewSongsAsync(16);
							if (songs == null || songs.Count == 0)
							{
								throw new Exception("获取韩国新歌失败");
							}
							return songs;
						}, quality, categoryName, showDialog: false);
						break;
					default:
						if (subCategoryId.StartsWith("playlist_cat_"))
						{
							string catName = subCategoryId.Substring("playlist_cat_".Length);
							taskCount = await DownloadPlaylistListCategory(catName, async delegate
							{
								var (playlists, _, _) = await _apiClient.GetPlaylistsByCategoryAsync(catName);
								if (playlists == null || playlists.Count == 0)
								{
									throw new Exception("获取" + catName + "歌单失败");
								}
								return playlists;
							}, quality, categoryName, showDialog: false);
							break;
						}
						MessageBox.Show("暂不支持下载该子分类: " + subCategoryName, "提示", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
						goto end_IL_0237;
					}
					totalTasks = checked(totalTasks + taskCount);
					end_IL_0237:;
				}
				catch (Exception ex)
				{
					MessageBox.Show("下载子分类 '" + subCategoryName + "' 失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				}
			}
			if (totalTasks > 0)
			{
				NotifyTransferTaskAdded($"已添加 {totalTasks} 个下载任务，分类：{categoryName}");
			}
		}
		finally
		{
			System.Windows.Forms.Cursor.Current = originalCursor;
		}
	}

	internal async void BatchDownloadPlaylistsOrAlbums_Click(object? sender, EventArgs e)
	{
		checked
		{
			try
			{
				if (resultListView.Items.Count == 0)
				{
					MessageBox.Show("当前列表为空", "提示", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
					return;
				}
				bool isPlaylistView = resultListView.Items.Count > 0 && resultListView.Items[0].Tag is PlaylistInfo;
				bool isAlbumView = resultListView.Items.Count > 0 && resultListView.Items[0].Tag is AlbumInfo;
				if (!isPlaylistView && !isAlbumView)
				{
					MessageBox.Show("当前视图不是歌单或专辑列表", "提示", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
					return;
				}
				List<string> displayNames = new List<string>();
				List<object> items = new List<object>();
				foreach (ListViewItem listViewItem in resultListView.Items)
				{
					object tag = listViewItem.Tag;
					if (tag is PlaylistInfo playlist)
					{
						displayNames.Add(playlist.Name);
						items.Add(playlist);
						continue;
					}
					tag = listViewItem.Tag;
					if (tag is AlbumInfo album)
					{
						displayNames.Add(album.Name + " - " + album.Artist);
						items.Add(album);
					}
				}
				if (items.Count == 0)
				{
					MessageBox.Show("当前列表中没有可下载的歌单或专辑", "提示", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
					return;
				}
				string viewName = GetCurrentViewName();
				BatchDownloadDialog dialog = new BatchDownloadDialog(displayNames, "批量下载 - " + viewName);
				if (dialog.ShowDialog() != DialogResult.OK)
				{
					return;
				}
				List<int> selectedIndices = dialog.SelectedIndices;
				if (selectedIndices.Count == 0)
				{
					return;
				}
				Cursor originalCursor = System.Windows.Forms.Cursor.Current;
				System.Windows.Forms.Cursor.Current = Cursors.WaitCursor;
				try
				{
					QualityLevel quality = GetCurrentQuality();
					NotifyTransferTaskAdded("正在添加批量下载任务...");
					int totalTasks = 0;
					foreach (int index in selectedIndices)
					{
						object item = items[index];
						if (item is PlaylistInfo playlist2)
						{
							List<SongInfo> playlistSongs = await FetchPlaylistSongsForDownloadAsync(playlist2);
							if (playlistSongs.Count > 0)
							{
								List<int> originalIndices = Enumerable.Range(1, playlistSongs.Count).ToList();
								int num = totalTasks;
								totalTasks = num + (await _downloadManager.AddBatchDownloadAsync(playlistSongs, quality, playlist2.Name, playlist2.Name, originalIndices)).Count;
							}
						}
						else if (item is AlbumInfo album2)
						{
							List<SongInfo> songs = await _apiClient.GetAlbumSongsAsync(album2.Id);
							if (songs != null && songs.Count > 0)
							{
								string albumName = album2.Name + " - " + album2.Artist;
								List<int> originalIndices2 = Enumerable.Range(1, songs.Count).ToList();
								int num2 = totalTasks;
								totalTasks = num2 + (await _downloadManager.AddBatchDownloadAsync(songs, quality, albumName, albumName, originalIndices2)).Count;
							}
						}
					}
					if (totalTasks > 0)
					{
						NotifyTransferTaskAdded($"已添加 {totalTasks} 个下载任务");
					}
				}
				finally
				{
					System.Windows.Forms.Cursor.Current = originalCursor;
				}
			}
			catch (Exception ex)
			{
				Exception ex2 = ex;
				Exception ex3 = ex2;
				MessageBox.Show("批量下载失败：" + ex3.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			}
		}
	}

	private async Task<List<PodcastEpisodeInfo>> FetchAllPodcastEpisodesAsync(long podcastId)
	{
		List<PodcastEpisodeInfo> result = new List<PodcastEpisodeInfo>();
		if (podcastId <= 0)
		{
			return result;
		}
		int offset = 0;
		bool hasMore;
		int totalCount;
		do
		{
			List<PodcastEpisodeInfo> episodes;
			(episodes, hasMore, totalCount) = await _apiClient.GetPodcastEpisodesAsync(podcastId, 100, offset);
			if (episodes == null || episodes.Count == 0)
			{
				break;
			}
			result.AddRange(episodes);
			offset = checked(offset + episodes.Count);
		}
		while (hasMore && offset < totalCount);
		return result;
	}

	private void ConfigureListViewDefault()
	{
		DisableVirtualSongList();
		columnHeader0.Text = string.Empty;
		columnHeader1.Text = string.Empty;
		columnHeader2.Text = string.Empty;
		columnHeader3.Text = string.Empty;
		columnHeader4.Text = string.Empty;
		columnHeader5.Text = string.Empty;
	}

	private async Task LoadCloudSongsAsync(bool skipSave = false, bool preserveSelection = false)
	{
		if (_apiClient == null)
		{
			return;
		}
		checked
		{
			if (!IsUserLoggedIn())
			{
				MessageBox.Show("请先登录后再访问云盘", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			}
			else
			{
				if (_cloudLoading)
				{
					return;
				}
				try
				{
					_cloudLoading = true;
					string paginationKey = BuildCloudPaginationKey();
					bool offsetClamped;
					int requestedOffset = Math.Max(0, (_cloudPage - 1) * CloudPageSize);
					int normalizedOffset = NormalizeOffsetWithCap(paginationKey, CloudPageSize, requestedOffset, out offsetClamped);
					if (offsetClamped)
					{
						int page = (normalizedOffset / CloudPageSize) + 1;
						_cloudPage = page;
						UpdateStatusBar($"页码过大，已跳到第 {page} 页");
					}
					int pendingIndex = 0;
					int num;
					if (preserveSelection)
					{
						ListView listView = resultListView;
						num = ((listView != null && listView.SelectedIndices.Count > 0) ? 1 : 0);
					}
					else
					{
						num = 0;
					}
					if (num != 0)
					{
						pendingIndex = Math.Max(0, resultListView.SelectedIndices[0]);
					}
					if (preserveSelection)
					{
						CacheCurrentCloudSelection();
					}
					_isHomePage = false;
					ViewLoadRequest request = new ViewLoadRequest("user_cloud", "云盘歌曲", "正在加载云盘歌曲...", cancelActiveNavigation: true, pendingIndex);
					ViewLoadResult<CloudPageViewData?> loadResult = await RunViewLoadAsync(request, async delegate(CancellationToken token)
					{
						int offset = Math.Max(0, (_cloudPage - 1) * 50);
						CloudSongPageResult pageResult = await FetchWithRetryUntilCancel((CancellationToken ct) => _apiClient.GetCloudSongsAsync(50, offset), $"user_cloud:page{_cloudPage}", token, delegate(int attempt, Exception _)
						{
							SafeInvoke(delegate
							{
								UpdateStatusBar($"加载云盘失败，正在重试（第 {attempt} 次）...");
							});
						}).ConfigureAwait(continueOnCapturedContext: true);
						return (pageResult == null) ? new CloudPageViewData(new List<SongInfo>(), hasMore: false, 0, 0L, 0L, offset) : new CloudPageViewData(pageResult.Songs ?? new List<SongInfo>(), pageResult.HasMore, pageResult.TotalCount, pageResult.UsedSize, pageResult.MaxSize, offset);
					}, "加载云盘已取消");
					if (!loadResult.IsCanceled)
					{
						CloudPageViewData data = loadResult.Value ?? new CloudPageViewData(new List<SongInfo>(), hasMore: false, 0, 0L, 0L, Math.Max(0, (_cloudPage - 1) * 50));
						if (TryHandlePaginationEmptyResult(paginationKey, data.Offset, CloudPageSize, data.TotalCount, data.Songs?.Count ?? 0, data.HasMore, "user_cloud", "云盘歌曲"))
						{
							return;
						}
						_cloudHasMore = data.HasMore;
						_cloudTotalCount = data.TotalCount;
						_cloudUsedSize = data.UsedSize;
						_cloudMaxSize = data.MaxSize;
						_currentPage = _cloudPage;
						_maxPage = (_cloudHasMore ? (_cloudPage + 1) : _cloudPage);
						_hasNextSearchPage = data.HasMore;
						DisplaySongs(data.Songs, showPagination: true, data.HasMore, data.Offset + 1, preserveSelection, "user_cloud", "云盘歌曲", skipAvailabilityCheck: true);
						RestoreCloudSelection();
						UpdateStatusBar(BuildCloudStatusText(data.Songs.Count));
					}
				}
				catch (Exception ex)
				{
					Exception ex2 = ex;
					Exception ex3 = ex2;
					string paginationKey = BuildCloudPaginationKey();
					int offset = Math.Max(0, (_cloudPage - 1) * CloudPageSize);
					if (TryHandlePaginationOffsetError(ex3, paginationKey, offset, CloudPageSize, "user_cloud", "云盘歌曲"))
					{
						return;
					}
					Debug.WriteLine($"[Cloud] 加载云盘失败: {ex3}");
					MessageBox.Show("加载云盘失败: " + ex3.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
					UpdateStatusBar("加载云盘失败");
				}
				finally
				{
					_cloudLoading = false;
				}
			}
		}
	}

	private string BuildCloudStatusText(int currentCount)
	{
		string value = FormatSize(_cloudUsedSize);
		string value2 = ((_cloudMaxSize > 0) ? FormatSize(_cloudMaxSize) : "未知");
		return $"云盘 - 第 {_cloudPage} 页，本页 {currentCount} 首 / 总 {_cloudTotalCount} 首，已用 {value} / {value2}";
	}

	private static string FormatSize(long bytes)
	{
		if (bytes <= 0)
		{
			return "0 B";
		}
		string[] array = new string[5] { "B", "KB", "MB", "GB", "TB" };
		int num = 0;
		double num2 = bytes;
		checked
		{
			while (num2 >= 1024.0 && num < array.Length - 1)
			{
				num++;
				num2 /= 1024.0;
			}
			return $"{num2:0.##} {array[num]}";
		}
	}

	private Task UploadCloudSongsAsync(string[] filePaths)
	{
		if (filePaths == null || filePaths.Length == 0)
		{
			return Task.CompletedTask;
		}
		if (!IsUserLoggedIn())
		{
			MessageBox.Show("请先登录后再上传云盘歌曲", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			return Task.CompletedTask;
		}
		string[] array = filePaths.Where((string path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path)).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToArray();
		if (array.Length == 0)
		{
			MessageBox.Show("未找到可上传的音频文件", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			return Task.CompletedTask;
		}
		UploadManager instance = UploadManager.Instance;
		NotifyTransferTaskAdded("正在添加上传任务...");
		List<UploadTask> list = instance.AddBatchUploadTasks(array, "云盘");
		if (list.Count > 0)
		{
			NotifyTransferTaskAdded($"已添加 {list.Count} 个上传任务到传输管理器");
		}
		else
		{
			UpdateStatusBar("未添加上传任务");
		}
		return Task.CompletedTask;
	}

	private async Task DeleteSelectedCloudSongAsync()
	{
		SongInfo song = GetSelectedCloudSong();
		if (song == null)
		{
			MessageBox.Show("请选择要删除的云盘歌曲", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
		}
		else if (string.IsNullOrEmpty(song.CloudSongId))
		{
			MessageBox.Show("无法删除选中的歌曲", "提示", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
		}
		else
		{
			if (MessageBox.Show("确定要从云盘删除歌曲：\n" + song.Name + " - " + song.Artist, "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
			{
				return;
			}
			string fallbackFocusId = DetermineNeighborCloudSongId(song);
			try
			{
				UpdateStatusBar("正在删除云盘歌曲...");
				if (await _apiClient.DeleteCloudSongsAsync(new string[1] { song.CloudSongId }))
				{
					UpdateStatusBar("云盘歌曲已删除");
					_lastSelectedCloudSongId = fallbackFocusId;
					RequestCloudRefresh(fallbackFocusId, preserveSelection: false);
				}
				else
				{
					MessageBox.Show("删除云盘歌曲失败，请稍后重试。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
					UpdateStatusBar("删除云盘歌曲失败");
				}
			}
			catch (Exception ex)
			{
				Exception ex2 = ex;
				MessageBox.Show("删除云盘歌曲失败：" + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("删除云盘歌曲失败");
			}
		}
	}

	private SongInfo? GetSelectedCloudSong()
	{
		ListViewItem selectedListViewItemSafe = GetSelectedListViewItemSafe();
		if (selectedListViewItemSafe == null)
		{
			return null;
		}
		return GetSongFromListViewItem(selectedListViewItemSafe);
	}

	private SongInfo? GetSongFromListViewItem(ListViewItem? item)
	{
		if (item?.Tag is int num && num >= 0 && num < _currentSongs.Count)
		{
			SongInfo songInfo = _currentSongs[num];
			return (songInfo != null && songInfo.IsCloudSong) ? songInfo : null;
		}
		return null;
	}

	private void CacheCurrentCloudSelection()
	{
		SongInfo selectedCloudSong = GetSelectedCloudSong();
		if (selectedCloudSong != null && !string.IsNullOrEmpty(selectedCloudSong.CloudSongId))
		{
			_pendingCloudFocusId = selectedCloudSong.CloudSongId;
			_lastSelectedCloudSongId = selectedCloudSong.CloudSongId;
		}
	}

	private void RestoreCloudSelection()
	{
		if (_skipCloudRestoreOnce)
		{
			_skipCloudRestoreOnce = false;
			_pendingCloudFocusId = null;
			return;
		}
		if (!string.Equals(_currentViewSource, "user_cloud", StringComparison.OrdinalIgnoreCase))
		{
			_pendingCloudFocusId = null;
			return;
		}
		string text = _pendingCloudFocusId ?? _lastSelectedCloudSongId;
		if (string.IsNullOrEmpty(text))
		{
			return;
		}
		int num = _currentSongs.FindIndex((SongInfo s) => s != null && s.IsCloudSong && string.Equals(s.CloudSongId, text, StringComparison.Ordinal));
		if (num >= 0 && num < resultListView.Items.Count)
		{
			EnsureListSelectionWithoutFocus(num);
			SongInfo songInfo = _currentSongs[num];
			if (songInfo != null && !string.IsNullOrEmpty(songInfo.CloudSongId))
			{
				_lastSelectedCloudSongId = songInfo.CloudSongId;
			}
		}
		_pendingCloudFocusId = null;
	}

	private void RequestCloudRefresh(string? focusCloudSongId = null, bool preserveSelection = true)
	{
		SafeInvoke(Runner);
                async Task RefreshImpl()
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
				}
				else
				{
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
						waitAttempts = checked(waitAttempts + 1);
					}
					await LoadCloudSongsAsync(skipSave: true, preserveSelection);
				}
			}
			catch (Exception ex)
			{
				Exception ex2 = ex;
				Exception ex3 = ex2;
				Debug.WriteLine("[Cloud] 刷新云盘失败: " + ex3.Message);
			}
		}
                void Runner()
                {
                        RefreshImpl().SafeFireAndForget("Cloud refresh");
                }
        }

	private async void uploadToCloudMenuItem_Click(object sender, EventArgs e)
	{
		using OpenFileDialog dialog = new OpenFileDialog
		{
			Multiselect = true,
			Filter = "音频文件|*.mp3;*.flac;*.wav;*.m4a;*.ogg;*.ape;*.wma|所有文件|*.*"
		};
		int num;
		if (dialog.ShowDialog(this) == DialogResult.OK)
		{
			string[] fileNames = dialog.FileNames;
			num = ((fileNames != null && fileNames.Length != 0) ? 1 : 0);
		}
		else
		{
			num = 0;
		}
		if (num != 0)
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
		int num = _currentSongs.IndexOf(currentSong);
		if (num < 0)
		{
			return null;
		}
		checked
		{
			for (int i = num + 1; i < _currentSongs.Count; i++)
			{
				SongInfo songInfo = _currentSongs[i];
				if (songInfo != null && songInfo.IsCloudSong && !string.IsNullOrEmpty(songInfo.CloudSongId))
				{
					return songInfo.CloudSongId;
				}
			}
			for (int num2 = num - 1; num2 >= 0; num2--)
			{
				SongInfo songInfo2 = _currentSongs[num2];
				if (songInfo2 != null && songInfo2.IsCloudSong && !string.IsNullOrEmpty(songInfo2.CloudSongId))
				{
					return songInfo2.CloudSongId;
				}
			}
			return null;
		}
	}

	private void OnCloudUploadTaskCompleted(UploadTask task)
	{
		if (task != null)
		{
			SafeInvoke(Handler);
		}
		void Handler()
		{
			if (IsUserLoggedIn())
			{
				string text = ((!string.IsNullOrEmpty(task.CloudSongId)) ? task.CloudSongId : null);
				if (!string.IsNullOrEmpty(text))
				{
					_lastSelectedCloudSongId = text;
					_cloudPage = 1;
				}
				RequestCloudRefresh(text, string.IsNullOrEmpty(text));
			}
		}
	}

	private void OnCloudUploadTaskFailed(UploadTask task)
	{
		if (task == null)
		{
			return;
		}
		SafeInvoke(delegate
		{
			if (!(_lastNotifiedUploadFailureTaskId == task.TaskId))
			{
				_lastNotifiedUploadFailureTaskId = task.TaskId;
				string text = ((!string.IsNullOrWhiteSpace(task.ErrorMessage)) ? task.ErrorMessage : ((!string.IsNullOrWhiteSpace(task.StageMessage)) ? task.StageMessage : "未知错误"));
				UpdateStatusBar("云盘上传失败：" + text);
				MessageBox.Show("云盘上传失败：" + text + "\n\n文件：" + task.FileName, "云盘上传失败", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			}
		});
	}
}
}
