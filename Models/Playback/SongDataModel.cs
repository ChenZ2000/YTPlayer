using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using YTPlayer.Core.Reactive;

namespace YTPlayer.Models.Playback
{
    /// <summary>
    /// 歌曲数据中心 - 响应式数据模型
    /// 管理歌曲的所有状态和数据，支持订阅和并发访问
    /// </summary>
    public sealed class SongDataModel
    {
        #region 不可变标识字段

        /// <summary>
        /// 歌曲 ID（响应式字段）
        /// </summary>
        public ObservableField<string> SongId { get; }

        #endregion

        #region 元数据字段

        /// <summary>
        /// 歌曲元数据（名称、歌手、专辑等）
        /// </summary>
        public ObservableField<SongMetadata> Metadata { get; }

        /// <summary>
        /// 所有可用音质信息字典
        /// Key: 音质标识 (standard, exhigh, lossless, etc.)
        /// Value: 音质信息
        /// </summary>
        public ObservableField<Dictionary<string, QualityInfo>> AvailableQualities { get; }

        /// <summary>
        /// 歌词数据
        /// </summary>
        public ObservableField<Core.Lyrics.EnhancedLyricsModels.EnhancedLyricsData> Lyrics { get; }

        #endregion

        #region 视图和播放状态字段

        /// <summary>
        /// 来源视图 ID（如 "playlist-123", "search-20241028"）
        /// </summary>
        public ObservableField<string> SourceView { get; }

        /// <summary>
        /// 在视图中的索引
        /// </summary>
        public ObservableField<int> IndexInView { get; }

        /// <summary>
        /// 是否正在播放
        /// </summary>
        public ObservableField<bool> IsCurrentlyPlaying { get; }

        /// <summary>
        /// 当前活跃的音质（如果正在播放）
        /// </summary>
        public ObservableField<string> ActiveQuality { get; }

        /// <summary>
        /// 播放指针位置（字节）
        /// </summary>
        public ObservableField<long> PlaybackPosition { get; }

        #endregion

        #region 音质容器管理

        /// <summary>
        /// 音质容器字典（ConcurrentDictionary，线程安全）
        /// Key: 音质标识, Value: AudioQualityContainer
        /// </summary>
        private readonly ConcurrentDictionary<string, AudioQualityContainer> _qualityContainers;

        #endregion

        #region 构造函数

        /// <summary>
        /// 创建歌曲数据模型
        /// </summary>
        /// <param name="songId">歌曲 ID</param>
        public SongDataModel(string songId)
        {
            if (string.IsNullOrWhiteSpace(songId))
                throw new ArgumentException("Song ID cannot be null or empty", nameof(songId));

            // 初始化不可变字段
            SongId = new ObservableField<string>(songId);

            // 初始化元数据字段
            Metadata = new ObservableField<SongMetadata>(null);
            AvailableQualities = new ObservableField<Dictionary<string, QualityInfo>>(new Dictionary<string, QualityInfo>());
            Lyrics = new ObservableField<Core.Lyrics.EnhancedLyricsModels.EnhancedLyricsData>(null);

            // 初始化视图和播放状态字段
            SourceView = new ObservableField<string>(null);
            IndexInView = new ObservableField<int>(-1);
            IsCurrentlyPlaying = new ObservableField<bool>(false);
            ActiveQuality = new ObservableField<string>(null);
            PlaybackPosition = new ObservableField<long>(0);

            // 初始化音质容器字典
            _qualityContainers = new ConcurrentDictionary<string, AudioQualityContainer>();
        }

        /// <summary>
        /// 从现有 SongInfo 创建数据模型
        /// </summary>
        public SongDataModel(SongInfo songInfo) : this(songInfo.Id)
        {
            if (songInfo == null)
                throw new ArgumentNullException(nameof(songInfo));

            // 初始化元数据
            Metadata.Value = new SongMetadata(songInfo);

            // 如果有可用性信息，初始化 AvailableQualities
            if (songInfo.IsAvailable.HasValue)
            {
                var qualities = new Dictionary<string, QualityInfo>();

                // 如果歌曲不可用，创建空字典
                if (!songInfo.IsAvailable.Value)
                {
                    AvailableQualities.Value = qualities;
                    return;
                }

                // 如果有 URL 缓存，添加到可用音质
                if (!string.IsNullOrEmpty(songInfo.Url) && !string.IsNullOrEmpty(songInfo.Level))
                {
                    string level = songInfo.Level.ToLower();
                    qualities[level] = new QualityInfo(level, true, 0, 0);

                    // 同时填充音质容器
                    var container = GetOrCreateQualityContainer(level);
                    container.Url.Value = songInfo.Url;
                    container.TotalSize.Value = songInfo.Size;
                    container.IsUrlResolved.Value = true;
                }

                AvailableQualities.Value = qualities;
            }
        }

        #endregion

        #region 音质容器访问

        /// <summary>
        /// 获取或创建音质容器
        /// </summary>
        /// <param name="quality">音质标识</param>
        /// <returns>音质容器</returns>
        public AudioQualityContainer GetOrCreateQualityContainer(string quality)
        {
            if (string.IsNullOrWhiteSpace(quality))
                throw new ArgumentException("Quality cannot be null or empty", nameof(quality));

            return _qualityContainers.GetOrAdd(quality.ToLower(), q => new AudioQualityContainer(q));
        }

        /// <summary>
        /// 尝试获取音质容器（不创建新容器）
        /// </summary>
        public bool TryGetQualityContainer(string quality, out AudioQualityContainer container)
        {
            return _qualityContainers.TryGetValue(quality?.ToLower(), out container);
        }

        /// <summary>
        /// 获取所有音质容器
        /// </summary>
        public IEnumerable<AudioQualityContainer> GetAllQualityContainers()
        {
            return _qualityContainers.Values;
        }

        /// <summary>
        /// 移除特定音质容器
        /// </summary>
        public bool RemoveQualityContainer(string quality)
        {
            return _qualityContainers.TryRemove(quality?.ToLower(), out _);
        }

        #endregion

        #region 内存管理

        /// <summary>
        /// 计算总内存占用（所有音质容器）
        /// </summary>
        public long CalculateTotalMemoryUsage()
        {
            return _qualityContainers.Values.Sum(c => c.CalculateMemoryUsage());
        }

        /// <summary>
        /// 清空所有音质容器的非 Chunk 0 数据
        /// </summary>
        public void ClearNonChunk0Data()
        {
            foreach (var container in _qualityContainers.Values)
            {
                container.ClearNonChunk0Data();
            }
        }

        /// <summary>
        /// 清空所有音质容器的所有数据（包括 Chunk 0）
        /// </summary>
        public void ClearAllData()
        {
            foreach (var container in _qualityContainers.Values)
            {
                container.ClearAllData();
            }
        }

        #endregion

        #region 状态查询

        /// <summary>
        /// 检查是否有任何音质可用
        /// </summary>
        public bool HasAnyQualityAvailable()
        {
            var qualities = AvailableQualities.Value;
            return qualities != null && qualities.Values.Any(q => q.IsAvailable);
        }

        /// <summary>
        /// 检查特定音质是否可用
        /// </summary>
        public bool IsQualityAvailable(string quality)
        {
            var qualities = AvailableQualities.Value;
            return qualities != null &&
                   qualities.TryGetValue(quality?.ToLower(), out var info) &&
                   info.IsAvailable;
        }

        /// <summary>
        /// 获取最佳可用音质（从高到低查找）
        /// </summary>
        public string GetBestAvailableQuality()
        {
            var qualities = AvailableQualities.Value;
            if (qualities == null || qualities.Count == 0)
                return null;

            // 音质优先级顺序
            var qualityOrder = new[] { "jymaster", "sky", "hires", "lossless", "exhigh", "higher", "standard" };

            foreach (var quality in qualityOrder)
            {
                if (qualities.TryGetValue(quality, out var info) && info.IsAvailable)
                {
                    return quality;
                }
            }

            // 返回第一个可用的音质
            return qualities.FirstOrDefault(kvp => kvp.Value.IsAvailable).Key;
        }

        #endregion

        #region 辅助方法

        public override string ToString()
        {
            var metadata = Metadata.Value;
            string songName = metadata != null ? $"{metadata.Name} - {metadata.Artist}" : SongId.Value;

            int availableQualities = AvailableQualities.Value?.Count(kvp => kvp.Value.IsAvailable) ?? 0;
            int loadedContainers = _qualityContainers.Count;
            long memoryMB = CalculateTotalMemoryUsage() / 1024 / 1024;

            return $"Song[{songName}] Qualities={availableQualities}, Containers={loadedContainers}, Memory={memoryMB}MB, Playing={IsCurrentlyPlaying.Value}";
        }

        #endregion
    }
}
