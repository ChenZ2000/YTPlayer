using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using YTPlayer.Models;

namespace YTPlayer.Core.Playback.Strategy
{
    /// <summary>
    /// 列表循环预加载策略
    /// 预加载接下来的1-2首歌曲，到达队列末尾时循环到开头
    /// </summary>
    public class LoopPreloadStrategy : IPreloadStrategy
    {
        public string Name => "Loop";

        public Task<List<PreloadCandidate>> GetPreloadCandidatesAsync(
            PlaybackContext context,
            int maxCandidates)
        {
            var candidates = new List<PreloadCandidate>();

            if (context?.QueueManager == null || maxCandidates <= 0)
            {
                return Task.FromResult(candidates);
            }

            try
            {
                // 预测接下来的N首歌曲（循环模式）
                var queue = context.QueueManager.GetCurrentQueue();
                if (queue == null || queue.Count == 0)
                {
                    return Task.FromResult(candidates);
                }

                int currentIndex = context.CurrentQueueIndex;
                int queueCount = queue.Count;

                // 添加接下来的maxCandidates首歌曲（循环）
                for (int i = 1; i <= maxCandidates; i++)
                {
                    int nextIndex = (currentIndex + i) % queueCount;  // 循环索引
                    var nextSong = queue[nextIndex];

                    if (nextSong != null)
                    {
                        string reason = i == 1 ? "NextInLoop" : $"LoopPosition{i}";

                        // 如果循环回到开头，添加标记
                        if (nextIndex < currentIndex || (currentIndex + i >= queueCount))
                        {
                            reason += "_Wrapped";
                        }

                        candidates.Add(new PreloadCandidate
                        {
                            Song = nextSong,
                            Priority = CalculatePriority(null, i - 1),
                            Reason = reason
                        });
                    }
                }

                System.Diagnostics.Debug.WriteLine($"[LoopPreloadStrategy] 找到 {candidates.Count} 个候选");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LoopPreloadStrategy] 获取候选失败: {ex.Message}");
            }

            return Task.FromResult(candidates);
        }

        public int CalculatePriority(PreloadCandidate candidate, int index)
        {
            // 第一首优先级90，第二首70，第三首50，依此类推
            return Math.Max(30, 90 - (index * 20));
        }
    }
}
