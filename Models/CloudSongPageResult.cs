using System.Collections.Generic;

namespace YTPlayer.Models
{
    /// <summary>
    /// 云盘歌曲分页结果
    /// </summary>
    public class CloudSongPageResult
    {
        /// <summary>
        /// 当前页歌曲列表
        /// </summary>
        public List<SongInfo> Songs { get; set; } = new List<SongInfo>();

        /// <summary>
        /// 总曲目数
        /// </summary>
        public int TotalCount { get; set; }

        /// <summary>
        /// 云盘已用容量（字节）
        /// </summary>
        public long UsedSize { get; set; }

        /// <summary>
        /// 云盘最大容量（字节）
        /// </summary>
        public long MaxSize { get; set; }

        /// <summary>
        /// 是否还有更多页
        /// </summary>
        public bool HasMore { get; set; }

        /// <summary>
        /// 本次请求的限制数量
        /// </summary>
        public int Limit { get; set; }

        /// <summary>
        /// 本次请求的偏移量
        /// </summary>
        public int Offset { get; set; }
    }
}
