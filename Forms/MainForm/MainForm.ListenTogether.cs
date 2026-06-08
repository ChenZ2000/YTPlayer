using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using YTPlayer.Core;
using YTPlayer.Core.ListenTogether;
using YTPlayer.Core.Playback;
using YTPlayer.Models;
using YTPlayer.Utils;

namespace YTPlayer
{
    public partial class MainForm
    {
        private const string ListenTogetherViewSource = "listen_together";
        private const double ListenTogetherSeekThresholdSeconds = 2.0;
        private const double ListenTogetherPausedSeekThresholdSeconds = 0.75;

        private readonly SemaphoreSlim _listenTogetherApplyLock = new SemaphoreSlim(1, 1);
        private ToolStripMenuItem? _listenTogetherMenuItem;
        private ListenTogetherDialog? _listenTogetherDialog;
        private ListenTogetherHttpServer? _listenTogetherServer;
        private ListenTogetherClient? _listenTogetherClient;
        private readonly string _listenTogetherSessionId = Guid.NewGuid().ToString("N");
        private string _listenTogetherLastSnapshotSongId = string.Empty;
        private long _listenTogetherSnapshotSequence;

        private void InitializeListenTogetherFeature()
        {
            AddListenTogetherMenuEntry();
        }

        private void AddListenTogetherMenuEntry()
        {
            if (fileMenuItem == null || _listenTogetherMenuItem != null)
            {
                return;
            }

            _listenTogetherMenuItem = new ToolStripMenuItem
            {
                Text = "一起听(&T)",
                Name = "listenTogetherMenuItem"
            };
            _listenTogetherMenuItem.Click += (_, _) => OpenListenTogetherDialog();

            int insertIndex = _listenRecognitionMenuItem != null
                ? fileMenuItem.DropDownItems.IndexOf(_listenRecognitionMenuItem)
                : -1;
            if (insertIndex >= 0)
            {
                insertIndex++;
            }
            else
            {
                insertIndex = fileMenuItem.DropDownItems.IndexOf(downloadManagerMenuItem);
                if (insertIndex < 0)
                {
                    insertIndex = Math.Max(0, fileMenuItem.DropDownItems.IndexOf(refreshMenuItem));
                }
            }

            fileMenuItem.DropDownItems.Insert(Math.Max(0, insertIndex), _listenTogetherMenuItem);
        }

        private void OpenListenTogetherDialog()
        {
            if (_listenTogetherDialog != null && !_listenTogetherDialog.IsDisposed)
            {
                _listenTogetherDialog.Activate();
                return;
            }

            var dialog = new ListenTogetherDialog();
            dialog.StartHostingRequested += async (_, _) => await StartListenTogetherHostAsync().ConfigureAwait(true);
            dialog.CopyUrlRequested += async (_, _) => await CopyListenTogetherUrlAsync().ConfigureAwait(true);
            dialog.StopHostingRequested += (_, _) => StopListenTogetherHost(updateStatus: true);
            dialog.ConnectRequested += async (_, url) => await ConnectListenTogetherAsync(url).ConfigureAwait(true);
            dialog.DisconnectRequested += (_, _) => DisconnectListenTogetherClient(updateStatus: true);
            dialog.FormClosed += (_, _) => _listenTogetherDialog = null;
            _listenTogetherDialog = dialog;

            dialog.SetHostUrl(_listenTogetherServer?.InviteUrl);
            dialog.SetConnected(_listenTogetherClient?.IsConnected == true);
            dialog.SetStatus(ResolveListenTogetherStatusText());
            dialog.Show(this);
        }

        private async Task StartListenTogetherHostAsync()
        {
            try
            {
                DisconnectListenTogetherClient(updateStatus: false);

                _listenTogetherServer?.Dispose();
                _listenTogetherServer = new ListenTogetherHttpServer(CreateListenTogetherSnapshot);
                await _listenTogetherServer.StartAsync().ConfigureAwait(true);

                _listenTogetherDialog?.SetHostUrl(_listenTogetherServer.InviteUrl);
                string status = "本机一起听已开始。复制 URL 发给对方即可同步。";
                _listenTogetherDialog?.SetStatus(status);
                UpdateStatusBar(status);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ListenTogether] Start host failed: " + ex.Message);
                string message = "启动一起听失败：" + ex.Message;
                _listenTogetherDialog?.SetStatus(message);
                MessageBox.Show(this, message, "一起听", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async Task CopyListenTogetherUrlAsync()
        {
            if (_listenTogetherServer == null)
            {
                await StartListenTogetherHostAsync().ConfigureAwait(true);
            }

            string? url = _listenTogetherServer?.InviteUrl;
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            try
            {
                Clipboard.SetText(url);
                string status = "已复制本机一起听 URL。";
                _listenTogetherDialog?.SetStatus(status);
                UpdateStatusBar(status);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "复制 URL 失败：" + ex.Message, "一起听", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void StopListenTogetherHost(bool updateStatus)
        {
            _listenTogetherServer?.Dispose();
            _listenTogetherServer = null;
            _listenTogetherDialog?.SetHostUrl(null);

            if (updateStatus)
            {
                string status = "本机一起听已停止。";
                _listenTogetherDialog?.SetStatus(status);
                UpdateStatusBar(status);
            }
        }

        private async Task ConnectListenTogetherAsync(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                MessageBox.Show(this, "请先粘贴对方分享的一起听 URL。", "一起听", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                StopListenTogetherHost(updateStatus: false);

                _listenTogetherClient?.Dispose();
                _listenTogetherClient = new ListenTogetherClient();
                _listenTogetherClient.SnapshotReceived += ListenTogetherClientOnSnapshotReceived;
                _listenTogetherClient.StatusChanged += ListenTogetherClientOnStatusChanged;
                _listenTogetherClient.Connect(url);

                _listenTogetherDialog?.SetConnected(true);
                string status = "正在连接一起听...";
                _listenTogetherDialog?.SetStatus(status);
                UpdateStatusBar(status);
                await Task.CompletedTask.ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ListenTogether] Connect failed: " + ex.Message);
                MessageBox.Show(this, "连接一起听失败：" + ex.Message, "一起听", MessageBoxButtons.OK, MessageBoxIcon.Error);
                DisconnectListenTogetherClient(updateStatus: false);
            }
        }

        private void DisconnectListenTogetherClient(bool updateStatus)
        {
            if (_listenTogetherClient != null)
            {
                _listenTogetherClient.SnapshotReceived -= ListenTogetherClientOnSnapshotReceived;
                _listenTogetherClient.StatusChanged -= ListenTogetherClientOnStatusChanged;
                _listenTogetherClient.Dispose();
                _listenTogetherClient = null;
            }

            _listenTogetherDialog?.SetConnected(false);
            if (updateStatus)
            {
                string status = "已断开一起听连接。";
                _listenTogetherDialog?.SetStatus(status);
                UpdateStatusBar(status);
            }
        }

        private string ResolveListenTogetherStatusText()
        {
            if (_listenTogetherServer != null)
            {
                return "本机一起听已开始。";
            }

            if (_listenTogetherClient?.IsConnected == true)
            {
                return "已连接一起听。";
            }

            return "未开始";
        }

        private ListenTogetherPlaybackSnapshot CreateListenTogetherSnapshot()
        {
            SongInfo? song = _audioEngine?.CurrentSong;
            string songId = song?.Id ?? string.Empty;
            if (!string.Equals(songId, _listenTogetherLastSnapshotSongId, StringComparison.Ordinal))
            {
                _listenTogetherLastSnapshotSongId = songId;
                _listenTogetherSnapshotSequence++;
            }

            PlaybackState state = PlaybackState.Stopped;
            double position = 0;
            double duration = 0;
            try
            {
                if (_audioEngine != null)
                {
                    state = _audioEngine.GetPlaybackState();
                    position = _audioEngine.GetPosition();
                    duration = _audioEngine.GetDuration();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ListenTogether] Snapshot state failed: " + ex.Message);
            }

            if (duration <= 0 && song?.Duration > 0)
            {
                duration = song.Duration;
            }

            return new ListenTogetherPlaybackSnapshot
            {
                SessionId = _listenTogetherSessionId,
                Sequence = _listenTogetherSnapshotSequence,
                SongId = songId,
                SongName = song?.Name ?? string.Empty,
                Artist = song?.Artist ?? string.Empty,
                Album = song?.Album ?? string.Empty,
                DurationSeconds = duration > 0 ? (int)Math.Round(duration) : Math.Max(0, song?.Duration ?? 0),
                PositionSeconds = Math.Max(0, position),
                IsPlaying = state == PlaybackState.Playing || state == PlaybackState.Buffering,
                UpdatedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
        }

        private void ListenTogetherClientOnStatusChanged(object? sender, string status)
        {
            if (IsDisposed)
            {
                return;
            }

            SafeInvoke(() =>
            {
                _listenTogetherDialog?.SetStatus(status);
                UpdateStatusBar(status);
            });
        }

        private void ListenTogetherClientOnSnapshotReceived(object? sender, ListenTogetherPlaybackSnapshot snapshot)
        {
            if (IsDisposed || snapshot == null || string.Equals(snapshot.SessionId, _listenTogetherSessionId, StringComparison.Ordinal))
            {
                return;
            }

            SafeInvoke(() => ApplyListenTogetherRemoteStateAsync(snapshot).SafeFireAndForget("listen together remote state"));
        }

        private async Task ApplyListenTogetherRemoteStateAsync(ListenTogetherPlaybackSnapshot snapshot)
        {
            if (!snapshot.HasSong || _audioEngine == null || _apiClient == null)
            {
                return;
            }

            if (!await _listenTogetherApplyLock.WaitAsync(0).ConfigureAwait(true))
            {
                return;
            }

            try
            {
                string currentSongId = _audioEngine.CurrentSong?.Id ?? string.Empty;
                double expectedPosition = snapshot.EstimatePositionSeconds();

                if (!string.Equals(currentSongId, snapshot.SongId, StringComparison.Ordinal))
                {
                    SongInfo? remoteSong = await ResolveListenTogetherSongAsync(snapshot).ConfigureAwait(true);
                    if (remoteSong == null)
                    {
                        return;
                    }

                    remoteSong.ViewSource = ListenTogetherViewSource;
                    await PlaySong(remoteSong).ConfigureAwait(true);
                    if (expectedPosition > 1)
                    {
                        await _audioEngine.SetPositionWithCacheWaitAsync(expectedPosition, 15000, CancellationToken.None, waitTargetOnly: true).ConfigureAwait(true);
                    }
                }
                else
                {
                    double localPosition = _audioEngine.GetPosition();
                    double drift = expectedPosition - localPosition;
                    double threshold = snapshot.IsPlaying ? ListenTogetherSeekThresholdSeconds : ListenTogetherPausedSeekThresholdSeconds;
                    if (Math.Abs(drift) >= threshold)
                    {
                        await _audioEngine.SetPositionWithCacheWaitAsync(expectedPosition, 12000, CancellationToken.None, waitTargetOnly: true).ConfigureAwait(true);
                    }
                }

                ApplyListenTogetherPlaybackState(snapshot);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ListenTogether] Apply remote state failed: " + ex.Message);
                UpdateStatusBar("一起听同步失败：" + ex.Message);
            }
            finally
            {
                _listenTogetherApplyLock.Release();
            }
        }

        private async Task<SongInfo?> ResolveListenTogetherSongAsync(ListenTogetherPlaybackSnapshot snapshot)
        {
            try
            {
                SongInfo? detail = (await _apiClient.GetSongDetailAsync(new[] { snapshot.SongId }).ConfigureAwait(false))?.FirstOrDefault();
                if (detail != null)
                {
                    return detail;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ListenTogether] Resolve song detail failed: " + ex.Message);
            }

            return new SongInfo
            {
                Id = snapshot.SongId,
                Name = string.IsNullOrWhiteSpace(snapshot.SongName) ? "一起听歌曲" : snapshot.SongName,
                Artist = snapshot.Artist,
                Album = snapshot.Album,
                Duration = snapshot.DurationSeconds
            };
        }

        private void ApplyListenTogetherPlaybackState(ListenTogetherPlaybackSnapshot snapshot)
        {
            PlaybackState state = _audioEngine?.GetPlaybackState() ?? PlaybackState.Stopped;
            if (snapshot.IsPlaying)
            {
                if (state == PlaybackState.Paused || state == PlaybackState.Stopped || state == PlaybackState.Idle)
                {
                    _audioEngine?.Resume();
                }
            }
            else
            {
                if (state == PlaybackState.Playing || state == PlaybackState.Buffering)
                {
                    _audioEngine?.Pause();
                }
            }
        }
    }
}
