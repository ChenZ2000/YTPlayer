using System.Collections.Generic;
using System.Threading.Tasks;
using YTPlayer.Models;

namespace YTPlayer.Core.Playback.Strategy
{
    /// <summary>
    /// 单曲循环预加载策略
    /// 不需要预加载（循环播放同一首歌）
    /// </summary>
    public class LoopOnePreloadStrategy : IPreloadStrategy
    {
        public string Name => "LoopOne";

        public Task<List<PreloadCandidate>> GetPreloadCandidatesAsync(
            PlaybackContext context,
            int maxCandidates)
        {
            // 单曲循环模式下不需要预加载其他歌曲
            System.Diagnostics.Debug.WriteLine("[LoopOnePreloadStrategy] 单曲循环模式，不预加载");
            return Task.FromResult(new List<PreloadCandidate>());
        }

        public int CalculatePriority(PreloadCandidate candidate, int index)
        {
            // 单曲循环不会有候选，此方法不应被调用
            return 0;
        }
    }
}
