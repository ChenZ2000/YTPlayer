using System;

namespace YTPlayer.Models
{
    /// <summary>
    /// 歌曲信息模型
    /// </summary>
    public class SongInfo
    {
        /// <summary>
        /// 歌曲ID
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// 歌曲名称
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// 艺术家
        /// </summary>
        public string Artist { get; set; }

        /// <summary>
        /// 专辑名称
        /// </summary>
        public string Album { get; set; }

        /// <summary>
        /// 专辑ID
        /// </summary>
        public string AlbumId { get; set; }

        /// <summary>
        /// 时长（秒）
        /// </summary>
        public int Duration { get; set; }

        /// <summary>
        /// 播放URL
        /// </summary>
        public string Url { get; set; }

        /// <summary>
        /// 封面URL
        /// </summary>
        public string PicUrl { get; set; }

        /// <summary>
        /// 歌词
        /// </summary>
        public string Lyrics { get; set; }

        /// <summary>
        /// 音质级别
        /// </summary>
        public string Level { get; set; }

        /// <summary>
        /// 文件大小（字节）
        /// </summary>
        public long Size { get; set; }

        /// <summary>
        /// 发布时间
        /// </summary>
        public string PublishTime { get; set; }

        /// <summary>
        /// 格式化的时长（MM:SS）
        /// </summary>
        public string FormattedDuration
        {
            get
            {
                var minutes = Duration / 60;
                var seconds = Duration % 60;
                return $"{minutes:D2}:{seconds:D2}";
            }
        }

        /// <summary>
        /// 格式化的文件大小
        /// </summary>
        public string FormattedSize
        {
            get
            {
                if (Size < 1024)
                    return $"{Size} B";
                else if (Size < 1024 * 1024)
                    return $"{Size / 1024.0:F2} KB";
                else
                    return $"{Size / (1024.0 * 1024.0):F2} MB";
            }
        }

        /// <summary>
        /// 资源是否可用（批量预检结果缓存）
        /// null: 未检查
        /// true: 资源有效，可以播放
        /// false: 资源无效，官方曲库中不存在或已下架
        /// </summary>
        public bool? IsAvailable { get; set; }
    }
}
