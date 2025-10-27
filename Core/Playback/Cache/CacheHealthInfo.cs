using System;

namespace YTPlayer.Core.Playback.Cache
{
    /// <summary>
    /// 表示缓存健康状态，用于向上层汇报当前缓冲情况。
    /// </summary>
    public sealed class CacheHealthInfo
    {
        /// <summary>
        /// 当前请求的目标块索引。
        /// </summary>
        public int TargetChunk { get; }

        /// <summary>
        /// 已经准备好的块数。
        /// </summary>
        public int ReadyChunks { get; }

        /// <summary>
        /// 满足流畅播放所需的块数。
        /// </summary>
        public int RequiredChunks { get; }

        /// <summary>
        /// 尚未就绪的块数。
        /// </summary>
        public int MissingChunks { get; }

        /// <summary>
        /// 缓冲是否满足播放要求。
        /// </summary>
        public bool IsReady { get; }

        /// <summary>
        /// 当前是否仍处于缓冲阶段。
        /// </summary>
        public bool IsBuffering { get; }

        /// <summary>
        /// 当前缓冲进度 (0-1)。
        /// </summary>
        public double Progress { get; }

        public CacheHealthInfo(
            int targetChunk,
            int readyChunks,
            int requiredChunks,
            bool isReady,
            bool isBuffering)
        {
            TargetChunk = targetChunk;
            ReadyChunks = readyChunks;
            RequiredChunks = requiredChunks;
            MissingChunks = Math.Max(0, requiredChunks - readyChunks);
            IsReady = isReady;
            IsBuffering = isBuffering;
            Progress = requiredChunks <= 0
                ? 1.0
                : Math.Min(1.0, readyChunks / (double)requiredChunks);
        }
    }
}
