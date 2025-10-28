using System.Collections.Generic;
using System.Threading.Tasks;
using YTPlayer.Models;

namespace YTPlayer.Core.Playback.Strategy
{
    /// <summary>
    /// 预加载候选
    /// </summary>
    public class PreloadCandidate
    {
        public SongInfo Song { get; set; }
        public int Priority { get; set; }
        public string Reason { get; set; }  // "NextInSequence", "RandomCandidate", etc.

        public override string ToString()
        {
            return $"PreloadCandidate[{Song.Name}, Priority={Priority}, Reason={Reason}]";
        }
    }

    /// <summary>
    /// 播放上下文（策略计算所需的信息）
    /// </summary>
    public class PlaybackContext
    {
        public PlaybackQueueManager QueueManager { get; set; }
        public string PlayMode { get; set; }  // "顺序播放", "列表循环", "单曲循环", "随机播放"
        public SongInfo CurrentSong { get; set; }
        public int CurrentQueueIndex { get; set; }
        public string CurrentQueueSource { get; set; }
    }

    /// <summary>
    /// 预加载策略接口
    /// </summary>
    public interface IPreloadStrategy
    {
        /// <summary>
        /// 策略名称
        /// </summary>
        string Name { get; }

        /// <summary>
        /// 获取应该预加载的歌曲列表
        /// </summary>
        /// <param name="context">播放上下文</param>
        /// <param name="maxCandidates">最大候选数量</param>
        /// <returns>预加载候选列表（按优先级排序）</returns>
        Task<List<PreloadCandidate>> GetPreloadCandidatesAsync(
            PlaybackContext context,
            int maxCandidates);

        /// <summary>
        /// 计算预加载优先级
        /// </summary>
        /// <param name="candidate">候选</param>
        /// <param name="index">在候选列表中的索引</param>
        /// <returns>优先级（0-100，越大越高）</returns>
        int CalculatePriority(PreloadCandidate candidate, int index);
    }
}
