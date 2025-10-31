using System;

namespace YTPlayer.Models.Playback
{
    /// <summary>
    /// 歌曲元数据（不可变）
    /// </summary>
    public sealed class SongMetadata
    {
        public string Name { get; set; }
        public string Artist { get; set; }
        public string Album { get; set; }
        public string AlbumCoverUrl { get; set; }
        public int Duration { get; set; } // 秒
        public string Id { get; set; }

        public SongMetadata()
        {
        }

        public SongMetadata(SongInfo songInfo)
        {
            if (songInfo == null)
                throw new ArgumentNullException(nameof(songInfo));

            Id = songInfo.Id;
            Name = songInfo.Name;
            Artist = songInfo.Artist;
            Album = songInfo.Album;
            AlbumCoverUrl = songInfo.AlbumCoverUrl;
            Duration = songInfo.Duration;
        }

        public override string ToString()
        {
            return $"{Name} - {Artist}";
        }
    }
}
