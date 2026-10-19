using YTPlayer.Core.ListenTogether;
using YTPlayer.Core.Playback;

internal static class PlaybackUiTests
{
    public static async Task RunAsync()
    {
        var buttonText = typeof(YTPlayer.MainForm).GetMethod("BuildPlayPauseButtonText", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var statusText = typeof(YTPlayer.MainForm).GetMethod("GetPlaybackStatusText", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        foreach (var (state, button, status) in new[] {
            (PlaybackState.Playing, "暂停", "正在播放"), (PlaybackState.Paused, "播放", "已暂停"),
            (PlaybackState.Buffering, "缓冲中...", "正在缓冲"), (PlaybackState.Loading, "加载中...", "正在加载"),
            (PlaybackState.Stopped, "播放", "已停止") })
            Program.Check(Equals(buttonText.Invoke(null, new object?[] { state, null }), button) &&
                Equals(statusText.Invoke(null, new object[] { state }), status), "main button, tray and room summary status: " + state);
        var positions = new PositionCoordinator();
        positions.ResetForTrack("186016");
        positions.OnSeekRequested(90, false, 1);
        positions.OnSeekExecuted(90, true, false, 1);
        positions.OnPositionSynchronized("186016", 12);
        Program.Check(positions.GetEffectivePosition(12, 180, false) == 12 &&
            positions.GetSeekBasePosition(12, 180, false) == 12, "room seek replaces stale UI progress and next keyboard-seek base");
        positions.OnSeekRequested(30, false, 2);
        Program.Check(positions.GetSeekBasePosition(12, 180, false) == 30, "local seek still works after room progress update");

        var machine = new PlaybackStateMachine();
        machine.TransitionTo(PlaybackState.Loading); machine.TransitionTo(PlaybackState.Buffering);
        Program.Check(machine.TransitionTo(PlaybackState.Paused) && machine.CurrentState == PlaybackState.Paused,
            "buffering can transition to paused for synchronized transport and UI");

        long now = 100000;
        ListenTogetherPlaybackSnapshot Snapshot(bool playing, double position = 42) => new()
        { SongId = "186016", IsPlaying = playing, PositionSeconds = position, UpdatedUnixMilliseconds = now };
        var player = new Player { State = PlaybackState.Buffering };
        await ListenTogetherPlaybackApplier.ApplyAsync(Snapshot(false), player, () => { }, default, () => now);
        Program.Check(player.Events.SequenceEqual(new[] { "pause", "seek", "refresh" }), "remote pause takes effect before cache wait and UI refresh");
        Program.Check(player.Presentation == (PlaybackState.Paused, 42.0) && player.TimelineChanged, "UI receives final paused progress");

        player = new Player { State = PlaybackState.Paused };
        player.OnSeek = _ => { if (player.Seeks.Count == 1) now += 5000; };
        await ListenTogetherPlaybackApplier.ApplyAsync(Snapshot(true, 30), player, () => { }, default, () => now);
        Program.Check(player.Seeks.SequenceEqual(new[] { 30.0, 35.0 }), "cache wait recalculates current playing room position");
        Program.Check(player.Events[^2] == "resume" && player.Presentation == (PlaybackState.Playing, 35.0),
            "resume and presentation occur after fresh seek");

        player = new Player { State = PlaybackState.Stopped };
        await ListenTogetherPlaybackApplier.ApplyAsync(Snapshot(false), player, () => { }, default, () => now);
        Program.Check(player.Events[0] == "load" && player.Presentation.State == PlaybackState.Paused,
            "same song with stopped stream reloads before synchronized pause");

        player = new Player { SeekSucceeds = false };
        await ExpectFailure(() => ListenTogetherPlaybackApplier.ApplyAsync(Snapshot(false), player, () => { }, default, () => now));
        Program.Check(player.State == PlaybackState.Paused && !player.Events.Contains("refresh"), "failed seek stays paused and is not acknowledged by UI");

        player = new Player { State = PlaybackState.Paused, ResumeSucceeds = false, Position = 42 };
        await ExpectFailure(() => ListenTogetherPlaybackApplier.ApplyAsync(Snapshot(true), player, () => { }, default, () => now));
        Program.Check(!player.Events.Contains("refresh"), "failed resume cannot be reported as synchronized playing");

        bool superseded = false;
        player = new Player { State = PlaybackState.Paused, OnSeek = _ => superseded = true };
        try
        {
            await ListenTogetherPlaybackApplier.ApplyAsync(Snapshot(true), player,
                () => { if (superseded) throw new ListenTogetherLocalChangeException(); }, default, () => now);
            throw new Exception("Expected local action supersession");
        }
        catch (ListenTogetherLocalChangeException) { }
        Program.Check(!player.Events.Contains("resume") && !player.Events.Contains("refresh"), "late remote seek does not resume or redraw over a newer user action");

        using var cancellation = new CancellationTokenSource();
        player = new Player { OnSeek = _ => cancellation.Cancel() };
        try
        {
            await ListenTogetherPlaybackApplier.ApplyAsync(Snapshot(false), player, () => { }, cancellation.Token, () => now);
            throw new Exception("Expected cancelled room update");
        }
        catch (OperationCanceledException) { }
        Program.Check(!player.Events.Contains("refresh"), "leaving during cache wait prevents late UI update");
    }

    private static async Task ExpectFailure(Func<Task> operation)
    {
        try { await operation(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Expected playback application failure");
    }

    private sealed class Player : IListenTogetherPlaybackTarget
    {
        public string SongId { get; private set; } = "186016";
        public PlaybackState State { get; set; } = PlaybackState.Playing;
        public double Position { get; set; }
        public double Duration => 180;
        public bool SeekSucceeds = true, ResumeSucceeds = true, TimelineChanged;
        public Action<double>? OnSeek;
        public readonly List<string> Events = new();
        public readonly List<double> Seeks = new();
        public (PlaybackState State, double Position) Presentation;
        public Task LoadAsync(string id, CancellationToken token)
        { Events.Add("load"); SongId = id; State = PlaybackState.Playing; return Task.CompletedTask; }
        public void Pause() { Events.Add("pause"); State = PlaybackState.Paused; }
        public void Resume() { Events.Add("resume"); if (ResumeSucceeds) State = PlaybackState.Playing; }
        public async Task<bool> SeekAsync(double position, CancellationToken token)
        {
            Events.Add("seek"); Seeks.Add(position);
            await Task.Yield(); OnSeek?.Invoke(position);
            if (SeekSucceeds) Position = position;
            return SeekSucceeds;
        }
        public void RefreshPresentation(bool timelineChanged)
        { Events.Add("refresh"); Presentation = (State, Position); TimelineChanged = timelineChanged; }
    }
}
