using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using YTPlayer.Models;

namespace YTPlayer.Core.Playback.Strategy
{
    /// <summary>
    /// 顺序播放预加载策略
    /// 预加载接下来的1-2首歌曲
    /// </summary>
    public class SequentialPreloadStrategy : IPreloadStrategy
    {
        public string Name => "Sequential";

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
                // 预测接下来的N首歌曲
                var queue = context.QueueManager.GetCurrentQueue();
                if (queue == null || queue.Count == 0)
                {
                    return Task.FromResult(candidates);
                }

                int currentIndex = context.CurrentQueueIndex;
                int queueCount = queue.Count;

                // 添加接下来的maxCandidates首歌曲
                for (int i = 1; i <= maxCandidates; i++)
                {
                    int nextIndex = currentIndex + i;

                    // 顺序播放到达队列末尾则停止
                    if (nextIndex >= queueCount)
                    {
                        break;
                    }

                    var nextSong = queue[nextIndex];
                    if (nextSong != null)
                    {
                        candidates.Add(new PreloadCandidate
                        {
                            Song = nextSong,
                            Priority = CalculatePriority(null, i - 1),
                            Reason = i == 1 ? "NextInSequence" : $"SequencePosition{i}"
                        });
                    }
                }

                System.Diagnostics.Debug.WriteLine($"[SequentialPreloadStrategy] 找到 {candidates.Count} 个候选");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SequentialPreloadStrategy] 获取候选失败: {ex.Message}");
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
