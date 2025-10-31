using System;

namespace YTPlayer.Core.Playback.Cache
{
    /// <summary>
    /// 动态热点管理器
    /// 跟踪当前播放位置，提供热点窗口计算
    /// </summary>
    public sealed class DynamicHotspotManager
    {
        private long _currentHotspot; // 当前热点位置（字节）
        private readonly object _lock = new object();

        /// <summary>
        /// 数据块大小（256KB）
        /// </summary>
        private const int CHUNK_SIZE = 256 * 1024;

        /// <summary>
        /// 前向预加载块数
        /// </summary>
        private const int AHEAD_CHUNKS = 6;

        /// <summary>
        /// 后向预加载块数
        /// </summary>
        private const int BEHIND_CHUNKS = 2;

        /// <summary>
        /// 创建动态热点管理器
        /// </summary>
        public DynamicHotspotManager()
        {
            _currentHotspot = 0;
        }

        /// <summary>
        /// 更新播放位置
        /// </summary>
        /// <param name="position">当前播放位置（字节）</param>
        public void UpdatePlaybackPosition(long position)
        {
            lock (_lock)
            {
                _currentHotspot = position;
            }
        }

        /// <summary>
        /// 立即切换热点到新位置（用于 Seek 操作）
        /// </summary>
        /// <param name="newPosition">新位置（字节）</param>
        public void ShiftHotspot(long newPosition)
        {
            lock (_lock)
            {
                _currentHotspot = newPosition;
            }
        }

        /// <summary>
        /// 获取当前热点位置
        /// </summary>
        public long GetCurrentHotspot()
        {
            lock (_lock)
            {
                return _currentHotspot;
            }
        }

        /// <summary>
        /// 获取热点窗口（当前块索引范围）
        /// </summary>
        /// <returns>(startChunk, endChunk)</returns>
        public (int startChunk, int endChunk) GetHotWindow()
        {
            long hotspot;
            lock (_lock)
            {
                hotspot = _currentHotspot;
            }

            int currentChunk = (int)(hotspot / CHUNK_SIZE);
            int startChunk = Math.Max(0, currentChunk - BEHIND_CHUNKS);
            int endChunk = currentChunk + AHEAD_CHUNKS;

            return (startChunk, endChunk);
        }

        /// <summary>
        /// 获取当前块索引
        /// </summary>
        public int GetCurrentChunkIndex()
        {
            long hotspot;
            lock (_lock)
            {
                hotspot = _currentHotspot;
            }

            return (int)(hotspot / CHUNK_SIZE);
        }

        /// <summary>
        /// 计算块索引到当前热点的距离
        /// </summary>
        /// <param name="chunkIndex">块索引</param>
        /// <returns>距离（块数）</returns>
        public int CalculateDistanceToHotspot(int chunkIndex)
        {
            int currentChunk = GetCurrentChunkIndex();
            return Math.Abs(chunkIndex - currentChunk);
        }

        /// <summary>
        /// 检查块是否在热点窗口内
        /// </summary>
        public bool IsInHotWindow(int chunkIndex)
        {
            var (startChunk, endChunk) = GetHotWindow();
            return chunkIndex >= startChunk && chunkIndex <= endChunk;
        }

        /// <summary>
        /// 重置热点到起始位置
        /// </summary>
        public void Reset()
        {
            lock (_lock)
            {
                _currentHotspot = 0;
            }
        }
    }
}
