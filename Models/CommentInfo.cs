using System;

namespace YTPlayer.Models
{
    /// <summary>
    /// 评论信息模型
    /// </summary>
    public class CommentInfo
    {
        /// <summary>
        /// 评论ID
        /// </summary>
        public string CommentId { get; set; } = string.Empty;

        /// <summary>
        /// 用户ID
        /// </summary>
        public string UserId { get; set; } = string.Empty;

        /// <summary>
        /// 用户名
        /// </summary>
        public string UserName { get; set; } = string.Empty;

        /// <summary>
        /// 用户头像URL
        /// </summary>
        public string AvatarUrl { get; set; } = string.Empty;

        /// <summary>
        /// 评论内容
        /// </summary>
        public string Content { get; set; } = string.Empty;

        /// <summary>
        /// 点赞数
        /// </summary>
        public int LikedCount { get; set; }

        /// <summary>
        /// 是否已点赞
        /// </summary>
        public bool Liked { get; set; }

        /// <summary>
        /// 评论时间
        /// </summary>
        public DateTime Time { get; set; }

        /// <summary>
        /// IP归属地
        /// </summary>
        public string IpLocation { get; set; } = string.Empty;

        /// <summary>
        /// 被回复的评论ID（楼中楼）
        /// </summary>
        public string? BeRepliedId { get; set; }

        /// <summary>
        /// 被回复的用户名
        /// </summary>
        public string? BeRepliedUserName { get; set; }
    }
}

