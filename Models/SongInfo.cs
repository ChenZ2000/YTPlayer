using System;
using System.Collections.Generic;

namespace YTPlayer.Models
{
    /// <summary>
    /// 音质URL信息
    /// </summary>
    public class QualityUrlInfo
    {
        public string Url { get; set; } = string.Empty;
        public string Level { get; set; } = string.Empty;
        public long Size { get; set; }
        public bool IsAvailable { get; set; }

        /// <summary>
        /// 是否为试听版本
        /// </summary>
        public bool IsTrial { get; set; }

        /// <summary>
        /// 试听片段起始时间（毫秒）
        /// </summary>
        public long TrialStart { get; set; }

        /// <summary>
        /// 试听片段结束时间（毫秒）
        /// </summary>
        public long TrialEnd { get; set; }

        /// <summary>
        /// 缓存键（songId+level+trial标记）
        /// </summary>
        public string CacheKey(string songId)
        {
            var trialMark = IsTrial ? "trial" : "full";
            var lvl = string.IsNullOrWhiteSpace(Level) ? "unknown" : Level;
            return $"{songId}:{lvl}:{trialMark}";
        }
    }

    /// <summary>
    /// 歌曲信息模型
    /// </summary>
    public class SongInfo
    {
        /// <summary>
        /// 歌曲ID
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// 歌曲名称
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// 艺术家
        /// </summary>
        public string Artist { get; set; } = string.Empty;

        /// <summary>
        /// 艺术家ID集合（主唱/id列表，按接口返回顺序）。
        /// </summary>
        public List<long> ArtistIds { get; set; } = new List<long>();

        /// <summary>
        /// 艺术家名称集合（按接口返回顺序）。
        /// </summary>
        public List<string> ArtistNames { get; set; } = new List<string>();

        /// <summary>
        /// 主唱ID（ArtistIds的首个元素，若不存在则为0）。
        /// </summary>
        public long PrimaryArtistId => ArtistIds.Count > 0 ? ArtistIds[0] : 0;

        /// <summary>
        /// 主唱名称（ArtistNames首个元素，若不存在则退回 Artist 字段）。
        /// </summary>
        public string PrimaryArtistName => ArtistNames.Count > 0 ? ArtistNames[0] : Artist;

        /// <summary>
        /// 专辑名称
        /// </summary>
        public string Album { get; set; } = string.Empty;

        /// <summary>
        /// 专辑ID
        /// </summary>
        public string AlbumId { get; set; } = string.Empty;

        /// <summary>
        /// 时长（秒）
        /// </summary>
        public int Duration { get; set; }

        /// <summary>
        /// 播放URL（快捷访问当前选择的音质）
        /// </summary>
        public string Url { get; set; } = string.Empty;

        /// <summary>
        /// 封面URL
        /// </summary>
        public string PicUrl { get; set; } = string.Empty;

        /// <summary>
        /// 歌词
        /// </summary>
        public string Lyrics { get; set; } = string.Empty;

        /// <summary>
        /// 音质级别（快捷访问当前选择的音质）
        /// </summary>
        public string Level { get; set; } = string.Empty;

        /// <summary>
        /// 文件大小（快捷访问当前选择的音质）
        /// </summary>
        public long Size { get; set; }

        /// <summary>
        /// 是否来自云盘
        /// </summary>
        public bool IsCloudSong { get; set; }

        /// <summary>
        /// 云盘条目ID（用于删除等操作）
        /// </summary>
        public string CloudSongId { get; set; } = string.Empty;

        /// <summary>
        /// 云盘上传时的原始文件名
        /// </summary>
        public string CloudFileName { get; set; } = string.Empty;

        /// <summary>
        /// 云盘文件大小（字节）
        /// </summary>
        public long CloudFileSize { get; set; }

        /// <summary>
        /// 云盘匹配到的官方歌曲ID（若存在）
        /// </summary>
        public string CloudMatchedSongId { get; set; } = string.Empty;

        /// <summary>
        /// 云盘上传时间（Unix毫秒时间戳）
        /// </summary>
        public long? CloudUploadTime { get; set; }

        /// <summary>
        /// 发布时间
        /// </summary>
        public string PublishTime { get; set; } = string.Empty;

        /// <summary>
        /// 所有音质的URL缓存（按音质级别存储）
        /// Key: 音质级别名称（如 "jymaster", "sky", "lossless", "exhigh", "standard"）
        /// Value: 该音质的URL信息
        /// </summary>
        private Dictionary<string, QualityUrlInfo> _qualityUrls = new Dictionary<string, QualityUrlInfo>();
        private readonly HashSet<string> _qualityCacheKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
        /// 是否为试听版本（非VIP用户播放VIP歌曲时）
        /// </summary>
        public bool IsTrial { get; set; }

        /// <summary>
        /// 试听片段起始时间（毫秒）
        /// </summary>
        public long TrialStart { get; set; }

        /// <summary>
        /// 试听片段结束时间（毫秒）
        /// </summary>
        public long TrialEnd { get; set; }

        /// <summary>
        /// 是否需要 VIP 才能播放。
        /// </summary>
        public bool RequiresVip { get; set; }

        /// <summary>
        /// 当前登录用户是否已收藏（“我喜欢的音乐”）。
        /// </summary>
        public bool IsLiked { get; set; }

        /// <summary>
        /// 是否为播客节目。
        /// </summary>
        public bool IsPodcastEpisode { get; set; }

        /// <summary>
        /// 记录歌曲被加入播放队列或插播链时的来源标识，用于“查看来源”
        /// </summary>
        public string ViewSource { get; set; } = string.Empty;

        /// <summary>
        /// 播客节目 ID。
        /// </summary>
        public long PodcastProgramId { get; set; }

        /// <summary>
        /// 播客电台 ID。
        /// </summary>
        public long PodcastRadioId { get; set; }

        /// <summary>
        /// 播客电台名称。
        /// </summary>
        public string PodcastRadioName { get; set; } = string.Empty;

        /// <summary>
        /// 播客主持人名称。
        /// </summary>
        public string PodcastDjName { get; set; } = string.Empty;

        /// <summary>
        /// 播客节目发布时间。
        /// </summary>
        public DateTime? PodcastPublishTime { get; set; }

        /// <summary>
        /// 播客节目简介。
        /// </summary>
        public string PodcastEpisodeDescription { get; set; } = string.Empty;

        /// <summary>
        /// 播客节目序号。
        /// </summary>
        public int PodcastSerialNumber { get; set; }

        /// <summary>
        /// 设置特定音质的URL信息
        /// </summary>
        public void SetQualityUrl(string level, string url, long size, bool isAvailable, bool isTrial = false, long trialStart = 0, long trialEnd = 0)
        {
            if (string.IsNullOrEmpty(level)) return;

            var info = new QualityUrlInfo
            {
                Url = url,
                Level = level,
                Size = size,
                IsAvailable = isAvailable,
                IsTrial = isTrial,
                TrialStart = trialStart,
                TrialEnd = trialEnd
            };

            _qualityUrls[level] = info;
            _qualityCacheKeys.Add(info.CacheKey(Id));
        }

        /// <summary>
        /// 获取特定音质的URL信息
        /// </summary>
        public QualityUrlInfo? GetQualityUrl(string level)
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
            _qualityCacheKeys.Clear();
            Url = string.Empty;
            Level = string.Empty;
            Size = 0;
            IsAvailable = null;
        }
    }
}


