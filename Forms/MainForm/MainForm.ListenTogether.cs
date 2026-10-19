using System;
using System.Globalization;
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
        private readonly SemaphoreSlim _listenTogetherOperationLock = new SemaphoreSlim(1, 1);
        private ToolStripMenuItem? _listenTogetherMenuItem;
        private ListenTogetherDialog? _listenTogetherDialog;
        private ListenTogetherSession? _listenTogetherSession;
        private CancellationTokenSource? _listenTogetherCancellation;
        private Task? _listenTogetherLoop;
        private bool _listenTogetherBusy;
        private bool _listenTogetherClosing;
        private bool _listenTogetherCloseReady;
        private long _listenTogetherSeekVersion;
        private long _listenTogetherLocalControlVersion;
        private CancellationTokenSource? _listenTogetherApplyCancellation;
        private string _listenTogetherStatus = "未加入一起听房间";

        private void InitializeListenTogetherFeature()
        {
            if (fileMenuItem == null || _listenTogetherMenuItem != null) return;
            _listenTogetherMenuItem = new ToolStripMenuItem { Text = "一起听(&T)", Name = "listenTogetherMenuItem" };
            _listenTogetherMenuItem.Click += (_, _) => OpenListenTogetherDialog();
            int index = _listenRecognitionMenuItem == null ? -1 : fileMenuItem.DropDownItems.IndexOf(_listenRecognitionMenuItem);
            if (index >= 0) index++;
            else index = Math.Max(0, fileMenuItem.DropDownItems.IndexOf(downloadManagerMenuItem));
            fileMenuItem.DropDownItems.Insert(index, _listenTogetherMenuItem);
        }

        private void OpenListenTogetherDialog()
        {
            if (_listenTogetherDialog != null && !_listenTogetherDialog.IsDisposed)
            {
                _listenTogetherDialog.Activate();
                return;
            }
            var dialog = new ListenTogetherDialog();
            dialog.StartHostingRequested += async (_, _) => await StartOfficialListenTogetherAsync(null);
            dialog.ConnectRequested += async (_, url) => await StartOfficialListenTogetherAsync(url);
            dialog.CopyUrlRequested += (_, _) => CopyListenTogetherUrl();
            dialog.StopHostingRequested += async (_, _) => await StopOfficialListenTogetherAsync();
            dialog.DisconnectRequested += async (_, _) => await StopOfficialListenTogetherAsync();
            dialog.FormClosed += (_, _) => _listenTogetherDialog = null;
            _listenTogetherDialog = dialog;
            RefreshListenTogetherDialog();
            dialog.Show(this);
        }

        private void RefreshListenTogetherDialog()
        {
            var session = _listenTogetherSession;
            if (sequentialMenuItem != null) sequentialMenuItem.Enabled = session?.IsActive != true;
            _listenTogetherDialog?.SetSession(session?.IsActive == true, session?.IsHost == true, _listenTogetherBusy,
                session?.IsReady == true ? session.InviteUrl(_audioEngine?.CurrentSong?.Id ?? "") : "");
            _listenTogetherDialog?.SetStatus(_listenTogetherStatus);
            RefreshListenTogetherPlaybackDisplay();
        }

        private void RefreshListenTogetherPlaybackDisplay()
        {
            if (_listenTogetherDialog == null || _listenTogetherDialog.IsDisposed) return;
            if (_listenTogetherSession?.IsReady != true || _audioEngine == null || _listenTogetherCancellation?.IsCancellationRequested != false)
            {
                _listenTogetherDialog.SetPlaybackSummary(null);
                return;
            }
            var song = _audioEngine.CurrentSong;
            var state = _audioEngine.GetPlaybackState();
            string stateText = GetPlaybackStatusText(state);
            var timeline = NormalizePlaybackTimelineForUi(_audioEngine.GetPosition(), _audioEngine.GetDuration());
            _listenTogetherDialog.SetPlaybackSummary(
                $"当前歌曲：{song?.Name ?? "等待歌曲"} - {song?.Artist ?? ""}\r\n" +
                $"播放状态：{stateText}；进度：{FormatTimeFromSeconds(timeline.Position)} / {FormatTimeFromSeconds(timeline.Duration)}\r\n" +
                $"播放模式：{_config.PlaybackOrder}；队列：{_playbackQueue.CurrentQueue.Count} 首");
        }

        private void NotifyListenTogetherLocalControl()
        {
            Interlocked.Increment(ref _listenTogetherLocalControlVersion);
            try { Volatile.Read(ref _listenTogetherApplyCancellation)?.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        private void SetListenTogetherStatus(string message, bool announce = false)
        {
            if (_isFormClosing || IsDisposed) return;
            bool changed = _listenTogetherStatus != message;
            _listenTogetherStatus = message;
            RefreshListenTogetherDialog();
            if (changed)
            {
                if (announce) AnnounceStatusBarMessage(message);
                else UpdateStatusBar(message);
            }
        }

        private async Task StartOfficialListenTogetherAsync(string? inviteText)
        {
            if (_listenTogetherBusy || _listenTogetherSession?.IsActive == true || _listenTogetherClosing) return;
            await _listenTogetherOperationLock.WaitAsync();
            _listenTogetherBusy = true;
            _listenTogetherCancellation?.Dispose();
            _listenTogetherCancellation = new CancellationTokenSource();
            var token = _listenTogetherCancellation.Token;
            try
            {
                SetListenTogetherStatus(inviteText == null ? "正在创建一起听房间…" : "正在解析一起听邀请链接…", true);
                var invitation = inviteText == null ? null : await ListenTogetherInvitation.ResolveAsync(inviteText, token);
                _listenTogetherSession = new ListenTogetherSession(_apiClient.CreateListenTogetherApi(),
                    GetCurrentUserId().ToString(CultureInfo.InvariantCulture));
                SetListenTogetherStatus(invitation == null ? "正在创建一起听房间…" : "正在加入一起听房间…", true);
                await _listenTogetherSession.StartAsync(invitation, CaptureListenTogetherState, ApplyOfficialListenTogetherStateAsync, token);
                token.ThrowIfCancellationRequested();
                // ORDER_LOOP is the room's sequential mode; the local stop-at-end mode
                // cannot be represented by that protocol value.
                if (_audioEngine?.PlayMode == PlayMode.Sequential)
                {
                    _audioEngine.PlayMode = PlayMode.Loop;
                    _config.PlaybackOrder = "列表循环";
                    UpdatePlaybackOrderMenuCheck();
                    PersistPlaybackState();
                    RefreshNextSongPreload();
                }
                SetListenTogetherStatus(_listenTogetherSession.StatusText, true);
                _listenTogetherLoop = RunListenTogetherLoopAsync(_listenTogetherSession, token);
            }
            catch (Exception ex)
            {
                bool cancelled = token.IsCancellationRequested;
                string message = cancelled ? "一起听操作已取消。" : "一起听连接失败：" + ex.Message;
                if (_listenTogetherSession?.IsActive == true)
                {
                    try
                    {
                        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await _listenTogetherSession.LeaveAsync(cleanup.Token);
                    }
                    catch { message += " 房间退出未确认，请在网易云客户端检查。"; }
                }
                if (_listenTogetherSession?.IsActive != true) _listenTogetherSession = null;
                _listenTogetherCancellation.Cancel();
                _listenTogetherCancellation.Dispose();
                _listenTogetherCancellation = null;
                SetListenTogetherStatus(message, cancelled || _listenTogetherClosing);
                if (!cancelled && !_listenTogetherClosing)
                    MessageBox.Show(this, message, "一起听", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _listenTogetherBusy = false;
                RefreshListenTogetherDialog();
                _listenTogetherOperationLock.Release();
            }
        }

        private async Task RunListenTogetherLoopAsync(ListenTogetherSession session, CancellationToken token)
        {
            int failures = 0;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(Math.Min(15000, 2000 * (1 + failures)), token);
                    if (GetCurrentUserId().ToString(CultureInfo.InvariantCulture) != session.UserId)
                        throw new ListenTogetherApiException(301, "账号已退出或切换，一起听同步已停止");
                    await session.TickAsync(CaptureListenTogetherState, ApplyOfficialListenTogetherStateAsync, token);
                    failures = 0;
                    SetListenTogetherStatus(session.StatusText);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    if (token.IsCancellationRequested) break;
                    if (ex is ListenTogetherRoomClosedException || ex is ListenTogetherApiException { Code: 301 })
                    {
                        _listenTogetherCancellation?.Cancel();
                        _listenTogetherSession = null;
                        SetListenTogetherStatus(ex.Message, true);
                        break;
                    }
                    failures = Math.Min(failures + 1, 6);
                    SetListenTogetherStatus("一起听同步暂时失败，将自动重试：" + ex.Message, failures == 1);
                }
            }
        }

        private async Task StopOfficialListenTogetherAsync()
        {
            _listenTogetherCancellation?.Cancel();
            await _listenTogetherOperationLock.WaitAsync();
            _listenTogetherBusy = true;
            RefreshListenTogetherDialog();
            try
            {
                if (_listenTogetherLoop != null) await _listenTogetherLoop;
                _listenTogetherLoop = null;
                if (_listenTogetherSession?.IsActive == true)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await _listenTogetherSession.LeaveAsync(timeout.Token);
                }
                _listenTogetherSession = null;
                SetListenTogetherStatus("已退出一起听房间。", true);
            }
            catch (Exception ex)
            {
                // Retain the Exit button so a failed end request can be retried.
                SetListenTogetherStatus("本地同步已停止，一起听房间退出未确认，请重试退出：" + ex.Message, true);
            }
            finally
            {
                _listenTogetherCancellation?.Dispose();
                _listenTogetherCancellation = null;
                _listenTogetherBusy = false;
                RefreshListenTogetherDialog();
                _listenTogetherOperationLock.Release();
            }
        }

        private void CopyListenTogetherUrl()
        {
            if (_listenTogetherSession?.IsReady != true) return;
            try
            {
                Clipboard.SetText(_listenTogetherSession.InviteUrl(_audioEngine?.CurrentSong?.Id ?? ""));
                SetListenTogetherStatus("已复制一起听邀请链接。", true);
            }
            catch (Exception ex) { SetListenTogetherStatus("复制邀请链接失败：" + ex.Message, true); }
        }

        private ListenTogetherLocalState CaptureListenTogetherState()
        {
            var song = _audioEngine?.CurrentSong;
            var state = _audioEngine?.GetPlaybackState() ?? PlaybackState.Stopped;
            return new ListenTogetherLocalState
            {
                Playback = new ListenTogetherPlaybackSnapshot
                {
                    SongId = song?.Id ?? "", SongName = song?.Name ?? "", Artist = song?.Artist ?? "", Album = song?.Album ?? "",
                    DurationSeconds = song?.Duration ?? 0, PositionSeconds = Math.Max(0, _audioEngine?.GetPosition() ?? 0),
                    IsPlaying = state == PlaybackState.Playing || state == PlaybackState.Buffering,
                    IsBuffering = state == PlaybackState.Buffering,
                    UpdatedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                },
                Queue = _playbackQueue.CurrentQueue.Select(s => s.Id).ToArray(),
                SeekVersion = Interlocked.Read(ref _listenTogetherSeekVersion),
                IsChangingPlayback = IsSwitchingTrack || _isPlaybackLoading || _isUserDragging ||
                    state == PlaybackState.Loading || Volatile.Read(ref _playPauseCommandInFlight) != 0 ||
                    _seekManager?.IsSeeking == true || _seekManager?.IsSeekingLong == true || _seekManager?.HasPendingDeferredSeek == true,
                PlayMode = _audioEngine?.PlayMode switch { PlayMode.Random => "RANDOM", PlayMode.LoopOne => "SINGLE_LOOP", _ => "ORDER_LOOP" }
            };
        }

        private async Task ApplyOfficialListenTogetherStateAsync(ListenTogetherRemoteState update, CancellationToken token)
        {
            long localVersion = Interlocked.Read(ref _listenTogetherLocalControlVersion);
            using var applying = CancellationTokenSource.CreateLinkedTokenSource(token);
            _listenTogetherApplyCancellation = applying;
            try { await ApplyListenTogetherStateCoreAsync(update, localVersion, applying.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested &&
                localVersion != Interlocked.Read(ref _listenTogetherLocalControlVersion))
            { throw new ListenTogetherLocalChangeException(); }
            finally { _listenTogetherApplyCancellation = null; }
        }

        private async Task ApplyListenTogetherStateCoreAsync(ListenTogetherRemoteState update, long localVersion, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (_audioEngine == null || _isApplicationExitRequested) return;
            void EnsureCurrent()
            {
                token.ThrowIfCancellationRequested();
                if (_isApplicationExitRequested || IsDisposed) throw new OperationCanceledException(token);
                if (localVersion != Interlocked.Read(ref _listenTogetherLocalControlVersion) || CaptureListenTogetherState().IsChangingPlayback)
                    throw new ListenTogetherLocalChangeException();
            }
            EnsureCurrent();
            if (update.Playback != null)
                await ListenTogetherPlaybackApplier.ApplyAsync(update.Playback,
                    new ListenTogetherPlaybackTarget(this, EnsureCurrent), EnsureCurrent, token);
            EnsureCurrent();
            string targetId = update.Playback?.SongId ?? _audioEngine.CurrentSong?.Id ?? "";
            if (update.Queue != null)
            {
                var known = _playbackQueue.CurrentQueue.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First());
                if (_audioEngine.CurrentSong is SongInfo current) known[current.Id] = current;
                var songs = update.Queue.Select(id => known.TryGetValue(id, out var song) ? song :
                    new SongInfo { Id = id, Name = "一起听歌曲", ViewSource = ListenTogetherViewSource }).ToList();
                int anchor = songs.FindIndex(s => s.Id == targetId);
                _playbackQueue.TrySyncQueueWithView(songs, ListenTogetherViewSource, Math.Max(0, anchor));
            }
            if (_audioEngine.CurrentSong is SongInfo selected)
            {
                _playbackQueue.TryUpdateSongDetail(selected);
                if (update.Playback != null || update.Queue != null)
                {
                    var queue = _playbackQueue.CaptureSnapshot();
                    if (queue.QueueSource != ListenTogetherViewSource || queue.QueueIndex < 0 || queue.QueueIndex >= queue.Queue.Count ||
                        queue.Queue[queue.QueueIndex].Id != selected.Id)
                        _playbackQueue.ManualSelect(selected, _playbackQueue.CurrentQueue, ListenTogetherViewSource);
                }
            }
            if (update.PlayMode != null)
            {
                _audioEngine.PlayMode = update.PlayMode switch { "RANDOM" => PlayMode.Random, "SINGLE_LOOP" => PlayMode.LoopOne, _ => PlayMode.Loop };
                _config.PlaybackOrder = update.PlayMode switch { "RANDOM" => "随机播放", "SINGLE_LOOP" => "单曲循环", _ => "列表循环" };
                UpdatePlaybackOrderMenuCheck();
            }
            if (update.Queue != null || update.PlayMode != null) RefreshNextSongPreload();
            PersistPlaybackState();
            RefreshListenTogetherPlaybackDisplay();
        }

        private void RefreshListenTogetherPlaybackUi(bool timelineChanged)
        {
            if (_audioEngine == null || _isApplicationExitRequested) return;
            var song = _audioEngine.CurrentSong;
            var state = _audioEngine.GetPlaybackState();
            var timeline = NormalizePlaybackTimelineForUi(_audioEngine.GetPosition(), _audioEngine.GetDuration());
            _positionCoordinator?.OnPositionSynchronized(song?.Id, timeline.Position);
            if (timelineChanged)
            {
                CancelPendingLyricSpeech(resetSuppression: false);
                BeginLyricSeekSuppression(timeline.Position);
                _lastLyricPlaybackPosition = TimeSpan.FromSeconds(timeline.Position);
            }
            lock (_stateCacheLock)
            {
                _cachedPosition = timeline.Position;
                _cachedDuration = timeline.Duration;
                _cachedPlaybackState = state;
            }
            UpdateUIForPlaybackState(state);
            currentSongLabel.Text = song == null ? "未播放" : (song.IsTrial ? song.Name + "(试听版)" : song.Name) + " - " + song.Artist;
            UpdatePlayButtonDescription(song);
            UpdateTrayIconTooltip(song);
            UpdateProgressTrackBarDisplay(timeline.Position, timeline.Duration, announceWhenFocused: true);
            _lyricsDisplayManager?.UpdatePosition(TimeSpan.FromSeconds(timeline.Position));
            if (_currentLyrics != null)
                lyricsLabel.Text = LyricsManager.GetCurrentLyric(_currentLyrics, TimeSpan.FromSeconds(timeline.Position))?.Text ?? "";
            RefreshListenTogetherPlaybackDisplay();
        }

        private sealed class ListenTogetherPlaybackTarget : IListenTogetherPlaybackTarget
        {
            private readonly MainForm _owner;
            private readonly Action _ensureCurrent;
            public ListenTogetherPlaybackTarget(MainForm owner, Action ensureCurrent) { _owner = owner; _ensureCurrent = ensureCurrent; }
            public string SongId => _owner._audioEngine.CurrentSong?.Id ?? "";
            public PlaybackState State => _owner._audioEngine.GetPlaybackState();
            public double Position => _owner._audioEngine.GetPosition();
            public double Duration => _owner._audioEngine.GetDuration();
            public async Task LoadAsync(string songId, CancellationToken token)
            {
                var detail = (await _owner._apiClient.GetSongDetailAsync(new[] { songId }, token)).FirstOrDefault()
                    ?? throw new InvalidOperationException("无法获取房间正在播放的歌曲。");
                _ensureCurrent();
                detail.ViewSource = ListenTogetherViewSource;
                await _owner.PlaySongDirectWithCancellation(detail, parentToken: token);
            }
            public void Pause() => _owner._audioEngine.Pause();
            public void Resume() => _owner._audioEngine.Resume();
            public Task<bool> SeekAsync(double position, CancellationToken token) =>
                _owner._audioEngine.SetPositionWithCacheWaitAsync(position, 12000, token, waitTargetOnly: true);
            public void RefreshPresentation(bool timelineChanged) => _owner.RefreshListenTogetherPlaybackUi(timelineChanged);
        }

        private bool DeferCloseForListenTogether(FormClosingEventArgs e)
        {
            if (_listenTogetherCloseReady || (!_listenTogetherClosing && _listenTogetherSession == null && !_listenTogetherBusy)) return false;
            e.Cancel = true;
            if (_listenTogetherClosing) return true;
            _listenTogetherClosing = true;
            FinishListenTogetherBeforeCloseAsync().SafeFireAndForget("close listen together");
            return true;
        }

        private async Task FinishListenTogetherBeforeCloseAsync()
        {
            try { await StopOfficialListenTogetherAsync(); }
            finally
            {
                _listenTogetherCloseReady = true;
                if (!IsDisposed) BeginInvoke(new Action(Close));
            }
        }
    }
}
