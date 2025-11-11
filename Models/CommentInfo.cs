using System;
using System.Collections.Generic;

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
        /// 原始时间戳（毫秒）
        /// </summary>
        public long TimeMilliseconds { get; set; }

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

        /// <summary>
        /// 父级评论ID（楼层归属）
        /// </summary>
        public string? ParentCommentId { get; set; }

        /// <summary>
        /// 该评论拥有的回复数量（包含懒加载数据）
        /// </summary>
        public int ReplyCount { get; set; }

        /// <summary>
        /// 子回复集合（用于树状展示）
        /// </summary>
        public List<CommentInfo> Replies { get; } = new List<CommentInfo>();
    }
}
