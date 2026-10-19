using System;
using System.Threading;
using System.Threading.Tasks;
using YTPlayer.Core.Playback;

namespace YTPlayer.Core.ListenTogether
{
    public interface IListenTogetherPlaybackTarget
    {
        string SongId { get; }
        PlaybackState State { get; }
        double Position { get; }
        double Duration { get; }
        Task LoadAsync(string songId, CancellationToken token);
        void Pause();
        void Resume();
        Task<bool> SeekAsync(double position, CancellationToken token);
        void RefreshPresentation(bool timelineChanged);
    }

    /// <summary>Applies room transport state before publishing the resulting UI snapshot.</summary>
    public static class ListenTogetherPlaybackApplier
    {
        public static async Task ApplyAsync(ListenTogetherPlaybackSnapshot snapshot, IListenTogetherPlaybackTarget player,
            Action ensureCurrent, CancellationToken token, Func<long>? now = null)
        {
            void Check() { token.ThrowIfCancellationRequested(); ensureCurrent(); }
            double Target() => Math.Clamp(snapshot.EstimatePositionSeconds(now?.Invoke()), 0,
                player.Duration > 0 ? player.Duration : double.MaxValue);

            Check();
            bool changed = player.SongId != snapshot.SongId || player.State == PlaybackState.Idle || player.State == PlaybackState.Stopped;
            if (changed)
            {
                await player.LoadAsync(snapshot.SongId, token);
                Check();
                if (player.SongId != snapshot.SongId || player.State == PlaybackState.Idle || player.State == PlaybackState.Stopped)
                    throw new InvalidOperationException("房间歌曲未能在本机播放，请检查歌曲权限或网络连接。");
            }
            // Pause immediately, including during buffering, before any cache wait for a seek.
            if (!snapshot.IsPlaying) player.Pause();
            for (int attempt = 0; attempt < 2; attempt++)
            {
                Check();
                double position = Target();
                if (Math.Abs(player.Position - position) < (snapshot.IsPlaying ? 2 : 0.75)) break;
                bool sought = await player.SeekAsync(position, token);
                Check();
                if (!sought) throw new InvalidOperationException("一起听进度同步未完成，正在等待歌曲缓存。");
                changed = true;
                // A playing room advances while this device waits for cached audio. Recalculate
                // the target once before resuming, rather than resuming at the stale wait target.
                if (!snapshot.IsPlaying) break;
            }
            Check();
            if (snapshot.IsPlaying && player.State == PlaybackState.Paused) player.Resume();
            if (snapshot.IsPlaying && player.State != PlaybackState.Playing && player.State != PlaybackState.Buffering)
                throw new InvalidOperationException("一起听播放状态尚未在本机生效。");
            if (!snapshot.IsPlaying && player.State != PlaybackState.Paused)
                throw new InvalidOperationException("一起听暂停状态尚未在本机生效。");
            Check();
            player.RefreshPresentation(changed);
        }
    }
}
