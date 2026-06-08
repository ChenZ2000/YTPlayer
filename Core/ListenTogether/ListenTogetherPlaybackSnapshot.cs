using System;

namespace YTPlayer.Core.ListenTogether
{
    public sealed class ListenTogetherPlaybackSnapshot
    {
        public string SessionId { get; set; } = string.Empty;
        public long Sequence { get; set; }
        public string SongId { get; set; } = string.Empty;
        public string SongName { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string Album { get; set; } = string.Empty;
        public int DurationSeconds { get; set; }
        public double PositionSeconds { get; set; }
        public bool IsPlaying { get; set; }
        public long UpdatedUnixMilliseconds { get; set; }

        public bool HasSong => !string.IsNullOrWhiteSpace(SongId);

        public double EstimatePositionSeconds()
        {
            double position = Math.Max(0, PositionSeconds);
            if (IsPlaying && UpdatedUnixMilliseconds > 0)
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                position += Math.Max(0, now - UpdatedUnixMilliseconds) / 1000.0;
            }

            if (DurationSeconds > 0)
            {
                position = Math.Min(position, DurationSeconds);
            }

            return position;
        }
    }
}
