using System;
using YTPlayer.Core;

namespace YTPlayer.Models
{
    /// <summary>
    /// 评论目标信息，用于调度评论对话框。
    /// </summary>
    public sealed class CommentTarget
    {
        public CommentTarget(string resourceId, CommentType type, string displayName, string? subtitle = null)
        {
            if (string.IsNullOrWhiteSpace(resourceId))
            {
                throw new ArgumentException("resourceId cannot be null or whitespace.", nameof(resourceId));
            }

            ResourceId = resourceId.Trim();
            Type = type;
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? string.Empty : displayName.Trim();
            Subtitle = subtitle;
        }

        /// <summary>
        /// 资源ID。
        /// </summary>
        public string ResourceId { get; }

        /// <summary>
        /// 评论类型（歌曲、歌单、专辑等）。
        /// </summary>
        public CommentType Type { get; }

        /// <summary>
        /// 展示名称。
        /// </summary>
        public string DisplayName { get; }

        /// <summary>
        /// 次级说明（如歌手或创建者）。
        /// </summary>
        public string? Subtitle { get; }
    }
}
