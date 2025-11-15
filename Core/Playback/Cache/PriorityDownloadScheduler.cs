using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace YTPlayer.Core.Playback.Cache
{
    /// <summary>
    /// 基于优先级的下载调度器，负责根据播放位置动态计算下一批需要拉取的块。
    /// </summary>
    public sealed class PriorityDownloadScheduler : IDisposable
    {
        private readonly int _totalChunks;
        private readonly int _preloadAhead;
        private readonly int _preloadBehind;
        private readonly ConcurrentDictionary<int, byte[]> _cache;

        private readonly SortedSet<ChunkRequest> _queue;
        private readonly Dictionary<int, ChunkRequest> _queuedLookup;
        private readonly HashSet<int> _inProgress;
        private readonly object _syncRoot = new object();

        private int _currentChunk;
        private long _sequence;
        private bool _disposed;

        public PriorityDownloadScheduler(
            int totalChunks,
            int preloadAhead,
            int preloadBehind,
            ConcurrentDictionary<int, byte[]> cache)
        {
            if (totalChunks <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(totalChunks));
            }

            _totalChunks = totalChunks;
            _preloadAhead = Math.Max(0, preloadAhead);
            _preloadBehind = Math.Max(0, preloadBehind);
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));

            _queue = new SortedSet<ChunkRequest>(new ChunkRequestComparer());
            _queuedLookup = new Dictionary<int, ChunkRequest>();
            _inProgress = new HashSet<int>();
        }

        /// <summary>
        /// 更新当前播放块，调度器会重新计算预加载窗口。
        /// </summary>
        public void UpdatePlaybackWindow(int currentChunk)
        {
            lock (_syncRoot)
            {
                if (_disposed)
                {
                    return;
                }

                _currentChunk = ClampChunkIndex(currentChunk);
                RebuildWindowLocked();
            }
        }

        /// <summary>
        /// 尝试取出下一个需要下载的块索引。
        /// </summary>
        public bool TryDequeue(out int chunkIndex)
        {
            lock (_syncRoot)
            {
                chunkIndex = -1;

                if (_disposed)
                {
                    return false;
                }

                while (_queue.Count > 0)
                {
                    var request = _queue.Min!;
                    _queue.Remove(request);
                    _queuedLookup.Remove(request.ChunkIndex);

                    if (_cache.ContainsKey(request.ChunkIndex))
                    {
                        continue;
                    }

                    if (_inProgress.Contains(request.ChunkIndex))
                    {
                        continue;
                    }

                    _inProgress.Add(request.ChunkIndex);
                    chunkIndex = request.ChunkIndex;
                    return true;
                }

                return false;
            }
        }

        /// <summary>
        /// 标记块下载完成，调度器会从并发列表中移除该块。
        /// </summary>
        public void MarkCompleted(int chunkIndex)
        {
            lock (_syncRoot)
            {
                if (_disposed)
                {
                    return;
                }

                _inProgress.Remove(chunkIndex);
            }
        }

        /// <summary>
        /// 下载失败时重新排队当前块。
        /// </summary>
        public void MarkFailed(int chunkIndex)
        {
            lock (_syncRoot)
            {
                if (_disposed)
                {
                    return;
                }

                if (_inProgress.Remove(chunkIndex))
                {
                    EnqueueChunkLocked(chunkIndex);
                }
            }
        }

        /// <summary>
        /// 将指定块以及其附近的块提升为最高优先级，以应对快速 Seek。
        /// </summary>
        public void BoostChunkPriority(int centerChunk, int radius = 2)
        {
            lock (_syncRoot)
            {
                if (_disposed)
                {
                    return;
                }

                for (int offset = -radius; offset <= radius; offset++)
                {
                    int idx = ClampChunkIndex(centerChunk + offset);

                    if (_cache.ContainsKey(idx) || _inProgress.Contains(idx))
                    {
                        continue;
                    }

                    if (_queuedLookup.TryGetValue(idx, out var existing))
                    {
                        _queue.Remove(existing);
                        _queuedLookup.Remove(idx);
                    }

                    int priority = -200 + Math.Abs(offset);
                    var request = new ChunkRequest(
                        idx,
                        priority,
                        Interlocked.Increment(ref _sequence));

                    _queue.Add(request);
                    _queuedLookup[idx] = request;
                }
            }
        }

        /// <summary>
        /// 清空队列和状态。
        /// </summary>
        public void Reset()
        {
            lock (_syncRoot)
            {
                if (_disposed)
                {
                    return;
                }

                _queue.Clear();
                _queuedLookup.Clear();
                _inProgress.Clear();
            }
        }

        public void Dispose()
        {
            lock (_syncRoot)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _queue.Clear();
                _queuedLookup.Clear();
                _inProgress.Clear();
            }
        }

        private void RebuildWindowLocked()
        {
            var desiredWindow = new HashSet<int>();

            int windowStart = Math.Max(0, _currentChunk - _preloadBehind);
            int windowEnd = Math.Min(_totalChunks - 1, _currentChunk + _preloadAhead);

            // ⭐⭐⭐ 关键修复：如果接近文件结尾，扩展窗口到包括最后的 chunks
            // 避免播放到结尾时，末尾 chunks 不在窗口内导致缓存未准备好
            if (_totalChunks > 0)
            {
                double progress = (double)_currentChunk / (_totalChunks - 1);
                if (progress >= 0.85)
                {
                    // 确保窗口包括最后 3 个 chunks
                    int lastChunk = _totalChunks - 1;
                    windowEnd = Math.Max(windowEnd, lastChunk);
                    windowStart = Math.Max(0, lastChunk - 10); // 扩展窗口到后 10 个 chunks

                    System.Diagnostics.Debug.WriteLine(
                        $"[PriorityScheduler] ⚡ 接近结尾（{progress:P1}），扩展窗口到 [{windowStart}, {windowEnd}]");
                }
            }

            for (int idx = windowStart; idx <= windowEnd; idx++)
            {
                desiredWindow.Add(idx);
                if (_cache.ContainsKey(idx) || _inProgress.Contains(idx))
                {
                    if (_queuedLookup.TryGetValue(idx, out var existing))
                    {
                        _queue.Remove(existing);
                        _queuedLookup.Remove(idx);
                    }

                    continue;
                }

                EnqueueChunkLocked(idx);
            }

            if (_queuedLookup.Count == 0)
            {
                return;
            }

            var obsolete = _queuedLookup.Keys.Where(chunk => !desiredWindow.Contains(chunk)).ToList();
            foreach (int chunk in obsolete)
            {
                if (_queuedLookup.TryGetValue(chunk, out var request))
                {
                    _queue.Remove(request);
                    _queuedLookup.Remove(chunk);
                }
            }
        }

        private void EnqueueChunkLocked(int chunkIndex)
        {
            if (_queuedLookup.TryGetValue(chunkIndex, out var existing))
            {
                _queue.Remove(existing);
            }

            var priority = CalculatePriority(chunkIndex, _currentChunk);
            var request = new ChunkRequest(
                chunkIndex,
                priority,
                Interlocked.Increment(ref _sequence));

            _queue.Add(request);
            _queuedLookup[chunkIndex] = request;
        }

        private int CalculatePriority(int chunkIndex, int currentChunk)
        {
            int distance = Math.Abs(chunkIndex - currentChunk);
            int directionPenalty = chunkIndex >= currentChunk ? 0 : 100;

            // ⭐⭐⭐ 关键修复：末尾 chunks 获得最高优先级
            // 避免播放到结尾时，末尾 chunks 还在队列末尾等待下载
            if (_totalChunks > 0 && chunkIndex >= _totalChunks - 3)
            {
                // 最后 3 个 chunks 的优先级为 -100（最高）
                return -100 + (chunkIndex - (_totalChunks - 3)); // -100, -99, -98
            }

            return distance + directionPenalty;
        }

        private int ClampChunkIndex(int chunkIndex)
        {
            if (chunkIndex < 0)
            {
                return 0;
            }

            if (chunkIndex >= _totalChunks)
            {
                return _totalChunks - 1;
            }

            return chunkIndex;
        }

        private sealed class ChunkRequest
        {
            public ChunkRequest(int chunkIndex, int priority, long sequence)
            {
                ChunkIndex = chunkIndex;
                Priority = priority;
                Sequence = sequence;
            }

            public int ChunkIndex { get; }

            public int Priority { get; set; }

            public long Sequence { get; set; }
        }

        private sealed class ChunkRequestComparer : IComparer<ChunkRequest>
        {
            public int Compare(ChunkRequest? x, ChunkRequest? y)
            {
                if (ReferenceEquals(x, y))
                {
                    return 0;
                }

                if (x is null)
                {
                    return -1;
                }

                if (y is null)
                {
                    return 1;
                }

                int priorityComparison = x.Priority.CompareTo(y.Priority);
                if (priorityComparison != 0)
                {
                    return priorityComparison;
                }

                int sequenceComparison = x.Sequence.CompareTo(y.Sequence);
                if (sequenceComparison != 0)
                {
                    return sequenceComparison;
                }

                return x.ChunkIndex.CompareTo(y.ChunkIndex);
            }
        }
    }
}
