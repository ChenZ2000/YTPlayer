using System.Collections.Generic;

namespace YTPlayer.Models
{
    /// <summary>
    /// 歌单信息模型
    /// </summary>
    public class PlaylistInfo
    {
        /// <summary>
        /// 歌单ID
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// 歌单名称
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// 创建者用户ID
        /// </summary>
        public long CreatorId { get; set; }

        /// <summary>
        /// 创建者
        /// </summary>
        public string Creator { get; set; } = string.Empty;

        /// <summary>
        /// 拥有者用户ID（部分接口返回 userId 字段）
        /// </summary>
        public long OwnerUserId { get; set; }

        /// <summary>
        /// 描述
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// 封面URL
        /// </summary>
        public string CoverUrl { get; set; } = string.Empty;

        /// <summary>
        /// 歌曲列表
        /// </summary>
        public List<SongInfo> Songs { get; set; } = new List<SongInfo>();

        /// <summary>
        /// 歌曲数量
        /// </summary>
        public int TrackCount { get; set; }
    }
}

