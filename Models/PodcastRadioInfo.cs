using System;

namespace YTPlayer.Models
{
    /// <summary>
    /// 播客/电台的基本信息。
    /// </summary>
    public class PodcastRadioInfo
    {
        /// <summary>电台 ID。</summary>
        public long Id { get; set; }

        /// <summary>电台名称。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>主持人/主播名称。</summary>
        public string DjName { get; set; } = string.Empty;

        /// <summary>主持人用户 ID。</summary>
        public long DjUserId { get; set; }

        /// <summary>一级分类。</summary>
        public string Category { get; set; } = string.Empty;

        /// <summary>二级分类。</summary>
        public string SecondCategory { get; set; } = string.Empty;

        /// <summary>简介。</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>封面 URL。</summary>
        public string CoverUrl { get; set; } = string.Empty;

        /// <summary>节目数量。</summary>
        public int ProgramCount { get; set; }

        /// <summary>订阅人数。</summary>
        public int SubscriberCount { get; set; }

        /// <summary>是否已订阅。</summary>
        public bool Subscribed { get; set; }

        /// <summary>分享次数。</summary>
        public int ShareCount { get; set; }

        /// <summary>点赞次数。</summary>
        public int LikedCount { get; set; }

        /// <summary>评论总数。</summary>
        public int CommentCount { get; set; }

        /// <summary>创建时间。</summary>
        public DateTime? CreateTime { get; set; }

        /// <summary>收费类型（0=免费，1=付费）。</summary>
        public int RadioFeeType { get; set; }
    }
}
