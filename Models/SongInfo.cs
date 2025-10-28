using System;
using System.Collections.Generic;

namespace YTPlayer.Models
{
    /// <summary>
    /// 音质URL信息
    /// </summary>
    public class QualityUrlInfo
    {
        public string Url { get; set; }
        public string Level { get; set; }
        public long Size { get; set; }
        public bool IsAvailable { get; set; }
    }

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
        /// 播放URL（快捷访问当前选择的音质）
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
        /// 音质级别（快捷访问当前选择的音质）
        /// </summary>
        public string Level { get; set; }

        /// <summary>
        /// 文件大小（快捷访问当前选择的音质）
        /// </summary>
        public long Size { get; set; }

        /// <summary>
        /// 发布时间
        /// </summary>
        public string PublishTime { get; set; }

        /// <summary>
        /// 所有音质的URL缓存（按音质级别存储）
        /// Key: 音质级别名称（如 "jymaster", "sky", "lossless", "exhigh", "standard"）
        /// Value: 该音质的URL信息
        /// </summary>
        private Dictionary<string, QualityUrlInfo> _qualityUrls = new Dictionary<string, QualityUrlInfo>();

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

        /// <summary>
        /// 设置特定音质的URL信息
        /// </summary>
        public void SetQualityUrl(string level, string url, long size, bool isAvailable)
        {
            if (string.IsNullOrEmpty(level)) return;

            _qualityUrls[level] = new QualityUrlInfo
            {
                Url = url,
                Level = level,
                Size = size,
                IsAvailable = isAvailable
            };
        }

        /// <summary>
        /// 获取特定音质的URL信息
        /// </summary>
        public QualityUrlInfo GetQualityUrl(string level)
        {
            if (string.IsNullOrEmpty(level)) return null;

            if (_qualityUrls.TryGetValue(level, out var info))
            {
                return info;
            }

            return null;
        }

        /// <summary>
        /// 检查特定音质是否已缓存
        /// </summary>
        public bool HasQualityUrl(string level)
        {
            if (string.IsNullOrEmpty(level)) return false;
            return _qualityUrls.ContainsKey(level);
        }

        /// <summary>
        /// 获取所有已缓存的音质信息
        /// </summary>
        public Dictionary<string, QualityUrlInfo> GetAllQualityUrls()
        {
            return new Dictionary<string, QualityUrlInfo>(_qualityUrls);
        }

        /// <summary>
        /// 清除所有音质缓存（当歌曲资源失效时使用）
        /// </summary>
        public void ClearAllQualityUrls()
        {
            _qualityUrls.Clear();
            Url = null;
            Level = null;
            Size = 0;
            IsAvailable = null;
        }
    }
}
