using System;

namespace YTPlayer.Models
{
    /// <summary>
    /// 播客节目信息。
    /// </summary>
    public class PodcastEpisodeInfo
    {
        /// <summary>节目 ID。</summary>
        public long ProgramId { get; set; }

        /// <summary>节目标题。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>节目简介。</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>所在电台 ID。</summary>
        public long RadioId { get; set; }

        /// <summary>所在电台名称。</summary>
        public string RadioName { get; set; } = string.Empty;

        /// <summary>主持人 ID。</summary>
        public long DjUserId { get; set; }

        /// <summary>主持人名称。</summary>
        public string DjName { get; set; } = string.Empty;

        /// <summary>发布时间。</summary>
        public DateTime? PublishTime { get; set; }

        /// <summary>节目时长。</summary>
        public TimeSpan Duration { get; set; }

        /// <summary>收听次数。</summary>
        public int ListenerCount { get; set; }

        /// <summary>点赞次数。</summary>
        public int LikedCount { get; set; }

        /// <summary>评论数。</summary>
        public int CommentCount { get; set; }

        /// <summary>分享次数。</summary>
        public int ShareCount { get; set; }

        /// <summary>节目期号（序号）。</summary>
        public int SerialNumber { get; set; }

        /// <summary>是否付费节目。</summary>
        public bool IsPaid { get; set; }

        /// <summary>节目封面。</summary>
        public string CoverUrl { get; set; } = string.Empty;

        /// <summary>对应的歌曲实体，用于沿用现有播放/下载逻辑。</summary>
        public SongInfo Song { get; set; } = new SongInfo();
    }
}
