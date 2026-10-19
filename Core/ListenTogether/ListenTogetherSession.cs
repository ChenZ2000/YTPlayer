using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace YTPlayer.Core.ListenTogether
{
    public sealed class ListenTogetherLocalState
    {
        public ListenTogetherPlaybackSnapshot Playback { get; set; } = new ListenTogetherPlaybackSnapshot();
        public string[] Queue { get; set; } = Array.Empty<string>();
        public string PlayMode { get; set; } = "ORDER_LOOP";
        public long? SeekVersion { get; set; }
        public bool IsChangingPlayback { get; set; }
    }

    public sealed class ListenTogetherRemoteState
    {
        public ListenTogetherPlaybackSnapshot? Playback { get; set; }
        public string[]? Queue { get; set; }
        public string? PlayMode { get; set; }
    }

    public sealed class ListenTogetherRoomClosedException : Exception
    {
        public ListenTogetherRoomClosedException() : base("一起听房间已结束，或当前账号已在其他设备退出房间。") { }
    }

    /// <summary>A local user action superseded an asynchronous remote playback update.</summary>
    public sealed class ListenTogetherLocalChangeException : OperationCanceledException { }

    /// <summary>
    /// One serialized room session. The caller owns cancellation and invokes TickAsync every two seconds.
    /// The official playlist snapshot carries both playCommand and versioned queue; no local HTTP server is used.
    /// Callbacks resume on the caller's synchronization context so playback stays on the UI thread.
    /// </summary>
    public sealed class ListenTogetherSession
    {
        private readonly ListenTogetherApi _api;
        private readonly string _userId;
        private readonly Func<long> _now;
        private string[] _queue = Array.Empty<string>();
        private string _mode = "ORDER_LOOP";
        private ListenTogetherPlaybackSnapshot? _lastLocal;
        private long _clientSeq;
        private long _lastSentSeq;
        private long _serverSeq;
        private long _clockOffset;
        private long _nextHeartbeat;
        private long _nextStatus;
        private long _membershipGraceUntil;
        private bool _membershipConfirmed;
        private bool _hasServerClock;
        private bool _receivedPlayback;
        private long? _lastSeekVersion;
        private long _nextCorrection;

        public ListenTogetherSession(ListenTogetherApi api, string userId, Func<long>? now = null)
        {
            _api = api;
            if (!ListenTogetherInvitation.IsSongId(userId)) throw new InvalidOperationException("请先登录网易云音乐。");
            _userId = userId;
            _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        public string RoomId { get; private set; } = "";
        public string UserId => _userId;
        public bool IsHost { get; private set; }
        public bool IsActive => RoomId.Length > 0;
        public bool IsReady { get; private set; }
        public string StatusText { get; private set; } = "未加入房间";
        public string InviteUrl(string songId) => new ListenTogetherInvitation(RoomId, _userId, songId).ToString();

        public async Task StartAsync(ListenTogetherInvitation? invitation, Func<ListenTogetherLocalState> capture,
            Func<ListenTogetherRemoteState, CancellationToken, Task> apply, CancellationToken token)
        {
            if (IsActive) throw new InvalidOperationException("请先退出当前一起听房间。");
            var status = await _api.StatusAsync(token);
            token.ThrowIfCancellationRequested();
            if (status.Value<bool?>("inRoom") == true)
                throw new InvalidOperationException("此账号已有一起听房间，请先在原客户端退出，再创建或加入房间。");

            _queue = Array.Empty<string>();
            _mode = "ORDER_LOOP";
            _lastLocal = null;
            _lastSeekVersion = null;
            _clientSeq = _lastSentSeq = _serverSeq = _nextHeartbeat = _nextCorrection = 0;
            _receivedPlayback = _hasServerClock = IsReady = false;
            _clockOffset = 0;

            IsHost = invitation == null;
            if (invitation == null)
            {
                if (capture().IsChangingPlayback)
                    throw new InvalidOperationException("请等待当前切歌或进度调整完成，再创建一起听房间。");
                if (!ListenTogetherInvitation.IsSongId(capture().Playback.SongId))
                    throw new InvalidOperationException("请先选择一首网易云在线歌曲，再创建一起听房间。");
                JObject created = await _api.CreateAsync(token);
                RoomId = created["roomInfo"]?.Value<string>("roomId") ?? "";
                if (!IsActive) throw new InvalidOperationException("网易云未返回房间编号，无法建立一起听会话。");
            }
            else
            {
                JObject check = await _api.CheckAsync(invitation.RoomId, token);
                if (check.Value<bool?>("joinable") == false)
                    throw new InvalidOperationException(check.Value<string>("copywriting") ?? "该一起听邀请已失效，或房间不可加入。");
                await _api.JoinAsync(invitation.RoomId, invitation.InviterId, token);
                RoomId = invitation.RoomId;
            }

            token.ThrowIfCancellationRequested();
            _nextStatus = _now() + 2000;
            _membershipGraceUntil = _now() + 15000;
            _membershipConfirmed = false;
            StatusText = IsHost ? "一起听房间已创建，等待对方加入。" : "已加入一起听房间，等待播放状态。";
            if (IsHost)
            {
                var local = capture();
                await HeartbeatAsync(local.Playback, token);
                await PublishQueueAsync(local, token);
                await PublishPlaybackAsync(local.Playback, "PLAY", token);
                _lastLocal = local.Playback;
                _lastSeekVersion = local.SeekVersion;
                _receivedPlayback = true;
            }
            else
            {
                // The heartbeat endpoint rejects songId=-1. Load the room's current
                // song before reporting playback; never report the guest's unrelated local song.
                await ReceiveAsync(capture, apply, token);
                if (_receivedPlayback) await HeartbeatAsync(capture().Playback, token);
            }
            IsReady = true;
            if (!IsHost && _receivedPlayback) StatusText = "已加入一起听房间，播放状态已同步。";
        }

        public async Task TickAsync(Func<ListenTogetherLocalState> capture,
            Func<ListenTogetherRemoteState, CancellationToken, Task> apply, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!IsActive) return;
            // Check membership even when a closed room's playlist endpoint starts failing.
            if (_now() >= _nextStatus)
            {
                _nextStatus = _now() + 5000;
                var status = await _api.StatusAsync(token);
                token.ThrowIfCancellationRequested();
                bool inRoom = status.Value<bool>("inRoom");
                if ((!inRoom && (_membershipConfirmed || _now() >= _membershipGraceUntil)) ||
                    (inRoom && status["roomInfo"]?.Value<string>("roomId") is string current && current != RoomId))
                    throw new ListenTogetherRoomClosedException();
                if (inRoom)
                {
                    _membershipConfirmed = true;
                    int members = (status["roomInfo"]?["roomUsers"] as JArray)?.Count ?? 1;
                    StatusText = members > 1 ? $"一起听已连接，房间内 {members} 人。" : "一起听房间已连接，等待对方加入。";
                }
            }
            if (capture().IsChangingPlayback)
            {
                if (_now() >= _nextHeartbeat && _lastLocal != null) await HeartbeatAsync(_lastLocal, token);
                return;
            }
            await ReceiveAsync(capture, apply, token);
            token.ThrowIfCancellationRequested();
            var local = capture();
            if (local.IsChangingPlayback) return;
            if (_receivedPlayback && ListenTogetherInvitation.IsSongId(local.Playback.SongId))
            {
                if (!CleanQueue(local).SequenceEqual(_queue) || local.PlayMode != _mode)
                    await PublishQueueAsync(local, token);
                string? command = DetectLocalCommand(local);
                if (command != null) await PublishPlaybackAsync(local.Playback, command, token);
                if (!local.Playback.IsBuffering) _lastSeekVersion = local.SeekVersion;
                _lastLocal = local.Playback;
            }
            if (_now() >= _nextHeartbeat)
                await HeartbeatAsync(_receivedPlayback ? local.Playback : new ListenTogetherPlaybackSnapshot(), token);
        }

        private async Task ReceiveAsync(Func<ListenTogetherLocalState> capture,
            Func<ListenTogetherRemoteState, CancellationToken, Task> apply, CancellationToken token)
        {
            var data = await _api.PlaylistAsync(RoomId, token);
            token.ThrowIfCancellationRequested();
            if (capture().IsChangingPlayback) return;
            var command = data["playCommand"] as JObject;
            long sequence = command?.Value<long?>("serverSeq") ?? 0;
            bool own = command?.Value<string>("userId") == _userId && _lastSentSeq > 0 &&
                (command.Value<long?>("clientSeq") ?? long.MaxValue) <= _lastSentSeq;
            var update = new ListenTogetherRemoteState();
            if (sequence > 0 && sequence >= _serverSeq)
            {
                string songId = command!.Value<string>("targetSongId") ?? "";
                string playStatus = command.Value<string>("playStatus") ?? "";
                if (!ListenTogetherInvitation.IsSongId(songId) || (playStatus != "PLAY" && playStatus != "PAUSE"))
                    throw new InvalidOperationException("一起听房间返回了无法识别的播放状态。");
                // Obtain the room clock with a valid song before applying the first snapshot.
                // Otherwise joining an already-playing room starts at the old command position.
                if (!_hasServerClock)
                    await HeartbeatAsync(new ListenTogetherPlaybackSnapshot
                    {
                        SongId = songId, IsPlaying = playStatus == "PLAY",
                        PositionSeconds = Math.Max(0, command.Value<double?>("progress") ?? 0) / 1000.0
                    }, token);
                long localCommandTime = sequence - _clockOffset;
                // Observed serverSeq is an epoch-millisecond timestamp. Do not extrapolate opaque/future sequences.
                long age = _hasServerClock && Math.Abs(_now() - localCommandTime) < 7L * 86400000
                    ? Math.Max(0, _now() - localCommandTime) : 0;
                var snapshot = new ListenTogetherPlaybackSnapshot
                {
                    SessionId = RoomId, Sequence = sequence, SongId = songId,
                    PositionSeconds = Math.Max(0, command.Value<double?>("progress") ?? 0) / 1000.0 + (playStatus == "PLAY" ? age / 1000.0 : 0),
                    IsPlaying = playStatus == "PLAY", UpdatedUnixMilliseconds = _now()
                };
                if (sequence > _serverSeq && !own) update.Playback = snapshot;
                else if (sequence == _serverSeq && _receivedPlayback && _now() >= _nextCorrection)
                {
                    var current = capture();
                    // Explicit seek events distinguish user intent from an audio buffer stall.
                    // Reconcile idle drift only; never override a local action waiting to be sent.
                    if (current.SeekVersion.HasValue && current.SeekVersion == _lastSeekVersion &&
                        !current.Playback.IsBuffering && current.Playback.SongId == snapshot.SongId &&
                        current.Playback.IsPlaying == snapshot.IsPlaying &&
                        _lastLocal?.SongId == current.Playback.SongId && _lastLocal.IsPlaying == current.Playback.IsPlaying)
                    {
                        _nextCorrection = _now() + 10000;
                        if (Math.Abs(current.Playback.PositionSeconds - snapshot.PositionSeconds) >= 2)
                            update.Playback = snapshot;
                    }
                }
            }
            if (data["playlist"]?["displayList"]?["result"] is JArray list)
            {
                var ids = list.Values<string>().Where(ListenTogetherInvitation.IsSongId).Select(x => x!).ToArray();
                if (ids.Length > 0 && !ids.SequenceEqual(_queue)) update.Queue = ids;
            }
            string? mode = data["playlist"]?.Value<string>("playMode");
            if (mode == "ORDER_LOOP" || mode == "SINGLE_LOOP" || mode == "RANDOM")
                if (!_receivedPlayback || mode != _mode) update.PlayMode = mode;

            if (_receivedPlayback)
            {
                // UI actions can happen while the snapshot request is in flight. Preserve
                // those pending changes so TickAsync can publish them after this read.
                var pending = capture();
                string? pendingCommand = DetectLocalCommand(pending);
                if (ListenTogetherInvitation.IsSongId(pending.Playback.SongId) && pendingCommand != null &&
                    (pendingCommand != "seek" || pending.SeekVersion.HasValue))
                    update.Playback = null;
                if (!CleanQueue(pending).SequenceEqual(_queue)) update.Queue = null;
                if (pending.PlayMode != _mode) update.PlayMode = null;
            }

            if (update.Playback != null || update.Queue != null || update.PlayMode != null)
            {
                try { await apply(update, token); }
                catch (ListenTogetherLocalChangeException) when (!token.IsCancellationRequested) { return; }
                token.ThrowIfCancellationRequested();
                if (update.Queue != null) _queue = update.Queue;
                if (update.PlayMode != null) _mode = update.PlayMode;
                _receivedPlayback |= update.Playback != null;
                // Remote changes become the local baseline; never echo them back as a new command.
                if (update.Playback != null)
                {
                    var applied = capture();
                    _lastLocal = applied.Playback;
                    _lastSeekVersion = applied.SeekVersion;
                }
            }
            _serverSeq = Math.Max(_serverSeq, sequence);
        }

        private string? DetectLocalCommand(ListenTogetherLocalState local)
        {
            string? command = DetectCommand(_lastLocal, local.Playback);
            if (local.SeekVersion.HasValue)
            {
                if (command == "seek" && local.SeekVersion == _lastSeekVersion) command = null;
                if (command == null && local.SeekVersion != _lastSeekVersion && !local.Playback.IsBuffering) command = "seek";
            }
            return command;
        }

        public static string? DetectCommand(ListenTogetherPlaybackSnapshot? previous, ListenTogetherPlaybackSnapshot current)
        {
            if (previous == null || previous.SongId != current.SongId) return "GOTO";
            if (previous.IsPlaying != current.IsPlaying) return current.IsPlaying ? "PLAY" : "PAUSE";
            if (previous.IsBuffering || current.IsBuffering) return null;
            double expected = previous.PositionSeconds + (previous.IsPlaying
                ? Math.Max(0, current.UpdatedUnixMilliseconds - previous.UpdatedUnixMilliseconds) / 1000.0 : 0);
            // The backend's maintained host example uses the lower-case seek command.
            return Math.Abs(current.PositionSeconds - expected) >= 1.5 ? "seek" : null;
        }

        private static string[] CleanQueue(ListenTogetherLocalState state)
        {
            var ids = state.Queue.Where(ListenTogetherInvitation.IsSongId).Distinct().ToList();
            if (ListenTogetherInvitation.IsSongId(state.Playback.SongId) && !ids.Contains(state.Playback.SongId)) ids.Add(state.Playback.SongId);
            return ids.ToArray();
        }

        private async Task PublishQueueAsync(ListenTogetherLocalState local, CancellationToken token)
        {
            string[] ids = CleanQueue(local);
            await _api.ReportPlaylistAsync(RoomId, new JObject
            {
                ["commandType"] = "REPLACE", ["version"] = new JArray(new JObject { ["userId"] = _userId, ["version"] = ++_clientSeq }),
                ["anchorSongId"] = "", ["anchorPosition"] = -1, ["randomList"] = new JArray(ids),
                ["displayList"] = new JArray(ids), ["playMode"] = local.PlayMode
            }, token);
            _queue = ids;
            _mode = local.PlayMode;
        }

        private async Task PublishPlaybackAsync(ListenTogetherPlaybackSnapshot local, string command, CancellationToken token)
        {
            long clientSeq = ++_clientSeq;
            await _api.CommandAsync(RoomId, new JObject
            {
                ["commandType"] = command == "PLAY" && !local.IsPlaying ? "PAUSE" : command,
                ["progress"] = Math.Max(0, (long)(local.PositionSeconds * 1000)), ["playStatus"] = local.IsPlaying ? "PLAY" : "PAUSE",
                ["formerSongId"] = _lastLocal?.SongId ?? "-1", ["targetSongId"] = local.SongId, ["clientSeq"] = clientSeq
            }, token);
            _lastSentSeq = clientSeq;
            _nextHeartbeat = 0; // Song/transport changes should also refresh the server heartbeat.
        }

        private async Task HeartbeatAsync(ListenTogetherPlaybackSnapshot local, CancellationToken token)
        {
            if (!ListenTogetherInvitation.IsSongId(local.SongId))
            {
                _nextHeartbeat = _now() + 5000;
                return;
            }
            long sent = _now();
            var data = await _api.HeartbeatAsync(RoomId, local.SongId,
                local.IsPlaying, Math.Max(0, (long)(local.PositionSeconds * 1000)), token);
            long received = _now();
            if (data.Value<long?>("time") is long serverTime)
            {
                _clockOffset = serverTime - (sent + received) / 2;
                _hasServerClock = true;
            }
            _nextHeartbeat = received + Math.Clamp(data.Value<int?>("timeSpan") ?? 30, 5, 60) * 1000L;
        }

        public async Task LeaveAsync(CancellationToken token)
        {
            if (!IsActive) return;
            await _api.EndAsync(RoomId, token);
            RoomId = "";
            IsReady = false;
            StatusText = "已退出一起听房间。";
        }
    }
}
