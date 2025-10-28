using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using YTPlayer.Models;

namespace YTPlayer.Core.Playback.Strategy
{
    /// <summary>
    /// 随机播放预加载策略
    /// 预加载3-5首随机候选歌曲
    /// </summary>
    public class RandomPreloadStrategy : IPreloadStrategy
    {
        private readonly Random _random = new Random();

        public string Name => "Random";

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
                // 获取当前队列
                var queue = context.QueueManager.GetCurrentQueue();
                if (queue == null || queue.Count == 0)
                {
                    return Task.FromResult(candidates);
                }

                // 获取注入队列（如果有）
                var injectionQueue = context.QueueManager.GetInjectionQueue();
                var hasInjectionQueue = injectionQueue != null && injectionQueue.Count > 0;

                // 如果有注入队列，优先预加载注入队列的第一首
                if (hasInjectionQueue)
                {
                    var nextInjectedSong = injectionQueue[0];
                    if (nextInjectedSong != null)
                    {
                        candidates.Add(new PreloadCandidate
                        {
                            Song = nextInjectedSong,
                            Priority = 95,  // 注入队列优先级最高
                            Reason = "InjectedNext"
                        });

                        System.Diagnostics.Debug.WriteLine($"[RandomPreloadStrategy] 添加注入队列候选: {nextInjectedSong.Name}");
                    }
                }

                // 随机选择候选歌曲
                int remainingSlots = maxCandidates - candidates.Count;
                if (remainingSlots > 0)
                {
                    var randomCandidates = GetRandomCandidates(
                        queue,
                        context.CurrentSong?.Id,
                        remainingSlots);

                    candidates.AddRange(randomCandidates);
                }

                System.Diagnostics.Debug.WriteLine($"[RandomPreloadStrategy] 找到 {candidates.Count} 个候选");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RandomPreloadStrategy] 获取候选失败: {ex.Message}");
            }

            return Task.FromResult(candidates);
        }

        /// <summary>
        /// 从队列中随机选择候选歌曲（排除当前播放的歌曲）
        /// </summary>
        private List<PreloadCandidate> GetRandomCandidates(
            List<SongInfo> queue,
            string currentSongId,
            int count)
        {
            var candidates = new List<PreloadCandidate>();

            try
            {
                // 过滤掉当前播放的歌曲
                var availableSongs = queue
                    .Where(s => s != null && s.Id != currentSongId)
                    .ToList();

                if (availableSongs.Count == 0)
                {
                    return candidates;
                }

                // 随机选择候选歌曲（不重复）
                int actualCount = Math.Min(count, availableSongs.Count);
                var selectedIndices = new HashSet<int>();

                while (selectedIndices.Count < actualCount)
                {
                    int randomIndex = _random.Next(availableSongs.Count);
                    if (selectedIndices.Add(randomIndex))
                    {
                        var song = availableSongs[randomIndex];
                        candidates.Add(new PreloadCandidate
                        {
                            Song = song,
                            Priority = CalculatePriority(null, selectedIndices.Count - 1),
                            Reason = $"RandomCandidate{selectedIndices.Count}"
                        });
                    }
                }

                System.Diagnostics.Debug.WriteLine(
                    $"[RandomPreloadStrategy] 从 {availableSongs.Count} 首歌中随机选择了 {candidates.Count} 个候选");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RandomPreloadStrategy] GetRandomCandidates失败: {ex.Message}");
            }

            return candidates;
        }

        public int CalculatePriority(PreloadCandidate candidate, int index)
        {
            // 随机模式下，第一个候选80，后续递减10
            // 因为随机模式的预测准确度较低，所以优先级整体低于顺序模式
            return Math.Max(30, 80 - (index * 10));
        }
    }
}
