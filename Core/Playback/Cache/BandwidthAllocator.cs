using System;
using System.Diagnostics;
using System.Threading;

namespace YTPlayer.Core.Playback.Cache
{
    /// <summary>
    /// 带宽分配管理器 - 阶段3：智能带宽分配
    /// 确保主播放连接获得足够带宽，避免预缓存影响当前播放
    /// </summary>
    public class BandwidthAllocator
    {
        /// <summary>
        /// 连接优先级
        /// </summary>
        public enum ConnectionPriority
        {
            /// <summary>主播放连接 - 最高优先级</summary>
            MainPlayback = 100,

            /// <summary>快速跳转连接 - 高优先级</summary>
            FastSeek = 80,

            /// <summary>预缓存连接 - 低优先级</summary>
            PreCache = 30
        }

        /// <summary>
        /// 连接槽
        /// </summary>
        private class ConnectionSlot
        {
            public ConnectionPriority Priority { get; set; }
            public DateTime StartTime { get; set; }
            public long BytesTransferred { get; set; }
            public double AllocatedBandwidthRatio { get; set; }
            public bool IsActive { get; set; }
        }

        private readonly object _lock = new object();
        private readonly ConnectionSlot _mainPlaybackSlot;
        private readonly ConnectionSlot _seekSlot;
        private readonly ConnectionSlot[] _preCacheSlots;

        private const int MaxPreCacheSlots = 3;
        private const double MainPlaybackBandwidthRatio = 0.70;       // 70%给主播放
        private const double SeekBandwidthRatio = 0.90;               // 跳转时90%给Seek
        private const double PreCacheBandwidthRatio = 0.30;           // 30%给预缓存

        public BandwidthAllocator()
        {
            _mainPlaybackSlot = new ConnectionSlot
            {
                Priority = ConnectionPriority.MainPlayback,
                AllocatedBandwidthRatio = MainPlaybackBandwidthRatio
            };

            _seekSlot = new ConnectionSlot
            {
                Priority = ConnectionPriority.FastSeek,
                AllocatedBandwidthRatio = SeekBandwidthRatio
            };

            _preCacheSlots = new ConnectionSlot[MaxPreCacheSlots];
            for (int i = 0; i < MaxPreCacheSlots; i++)
            {
                _preCacheSlots[i] = new ConnectionSlot
                {
                    Priority = ConnectionPriority.PreCache,
                    AllocatedBandwidthRatio = PreCacheBandwidthRatio / MaxPreCacheSlots
                };
            }
        }

        /// <summary>
        /// 激活主播放连接
        /// </summary>
        public void ActivateMainPlayback()
        {
            lock (_lock)
            {
                _mainPlaybackSlot.IsActive = true;
                _mainPlaybackSlot.StartTime = DateTime.UtcNow;
                _mainPlaybackSlot.BytesTransferred = 0;

                // 主播放激活时，降低预缓存的带宽分配
                RebalanceBandwidth();

                Debug.WriteLine($"[BandwidthAllocator] 主播放连接激活，分配带宽: {_mainPlaybackSlot.AllocatedBandwidthRatio * 100:F0}%");
            }
        }

        /// <summary>
        /// 激活快速跳转连接
        /// </summary>
        public void ActivateFastSeek()
        {
            lock (_lock)
            {
                _seekSlot.IsActive = true;
                _seekSlot.StartTime = DateTime.UtcNow;
                _seekSlot.BytesTransferred = 0;

                // 跳转时，暂停所有预缓存
                foreach (var slot in _preCacheSlots)
                {
                    slot.IsActive = false;
                }

                RebalanceBandwidth();

                Debug.WriteLine($"[BandwidthAllocator] 快速跳转连接激活，分配带宽: {_seekSlot.AllocatedBandwidthRatio * 100:F0}%");
            }
        }

        /// <summary>
        /// 激活预缓存连接
        /// </summary>
        /// <returns>连接槽索引，-1表示无可用槽</returns>
        public int ActivatePreCache()
        {
            lock (_lock)
            {
                // 如果主播放未激活，不允许预缓存
                if (!_mainPlaybackSlot.IsActive)
                {
                    return -1;
                }

                // 如果正在跳转，不允许预缓存
                if (_seekSlot.IsActive)
                {
                    return -1;
                }

                // 查找空闲槽
                for (int i = 0; i < _preCacheSlots.Length; i++)
                {
                    if (!_preCacheSlots[i].IsActive)
                    {
                        _preCacheSlots[i].IsActive = true;
                        _preCacheSlots[i].StartTime = DateTime.UtcNow;
                        _preCacheSlots[i].BytesTransferred = 0;

                        RebalanceBandwidth();

                        Debug.WriteLine($"[BandwidthAllocator] 预缓存槽 {i} 激活，分配带宽: {_preCacheSlots[i].AllocatedBandwidthRatio * 100:F0}%");
                        return i;
                    }
                }

                return -1; // 无可用槽
            }
        }

        /// <summary>
        /// 停用快速跳转连接
        /// </summary>
        public void DeactivateFastSeek()
        {
            lock (_lock)
            {
                _seekSlot.IsActive = false;
                RebalanceBandwidth();

                Debug.WriteLine("[BandwidthAllocator] 快速跳转连接停用");
            }
        }

        /// <summary>
        /// 停用预缓存连接
        /// </summary>
        public void DeactivatePreCache(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= _preCacheSlots.Length)
            {
                return;
            }

            lock (_lock)
            {
                _preCacheSlots[slotIndex].IsActive = false;
                RebalanceBandwidth();

                Debug.WriteLine($"[BandwidthAllocator] 预缓存槽 {slotIndex} 停用");
            }
        }

        /// <summary>
        /// 停用所有预缓存连接
        /// </summary>
        public void DeactivateAllPreCache()
        {
            lock (_lock)
            {
                foreach (var slot in _preCacheSlots)
                {
                    slot.IsActive = false;
                }

                RebalanceBandwidth();

                Debug.WriteLine("[BandwidthAllocator] 所有预缓存连接停用");
            }
        }

        /// <summary>
        /// 记录数据传输
        /// </summary>
        public void RecordTransfer(ConnectionPriority priority, long bytes)
        {
            lock (_lock)
            {
                switch (priority)
                {
                    case ConnectionPriority.MainPlayback:
                        _mainPlaybackSlot.BytesTransferred += bytes;
                        break;
                    case ConnectionPriority.FastSeek:
                        _seekSlot.BytesTransferred += bytes;
                        break;
                }
            }
        }

        /// <summary>
        /// 获取指定优先级的带宽分配比例
        /// </summary>
        public double GetBandwidthRatio(ConnectionPriority priority)
        {
            lock (_lock)
            {
                switch (priority)
                {
                    case ConnectionPriority.MainPlayback:
                        return _mainPlaybackSlot.IsActive ? _mainPlaybackSlot.AllocatedBandwidthRatio : 0;
                    case ConnectionPriority.FastSeek:
                        return _seekSlot.IsActive ? _seekSlot.AllocatedBandwidthRatio : 0;
                    case ConnectionPriority.PreCache:
                        int activePreCache = 0;
                        foreach (var slot in _preCacheSlots)
                        {
                            if (slot.IsActive) activePreCache++;
                        }
                        return activePreCache > 0 ? PreCacheBandwidthRatio / activePreCache : 0;
                    default:
                        return 0;
                }
            }
        }

        /// <summary>
        /// 重新平衡带宽分配
        /// </summary>
        private void RebalanceBandwidth()
        {
            // 带宽分配策略：
            // 1. 如果有Seek，给Seek 90%，主播放10%，预缓存0%
            // 2. 如果有主播放，给主播放70%，预缓存平分30%
            // 3. 没有主播放时，清空所有分配

            if (_seekSlot.IsActive)
            {
                // Seek优先
                _seekSlot.AllocatedBandwidthRatio = 0.90;
                _mainPlaybackSlot.AllocatedBandwidthRatio = 0.10;

                foreach (var slot in _preCacheSlots)
                {
                    slot.AllocatedBandwidthRatio = 0;
                }
            }
            else if (_mainPlaybackSlot.IsActive)
            {
                // 正常播放：主播放70%，预缓存30%
                _mainPlaybackSlot.AllocatedBandwidthRatio = MainPlaybackBandwidthRatio; // 70%

                int activePreCache = 0;
                foreach (var slot in _preCacheSlots)
                {
                    if (slot.IsActive) activePreCache++;
                }

                double perPreCacheRatio = activePreCache > 0 ? PreCacheBandwidthRatio / activePreCache : 0;
                foreach (var slot in _preCacheSlots)
                {
                    slot.AllocatedBandwidthRatio = slot.IsActive ? perPreCacheRatio : 0;
                }
            }
            else
            {
                // 没有主播放，清空所有分配
                _mainPlaybackSlot.AllocatedBandwidthRatio = 0;
                _seekSlot.AllocatedBandwidthRatio = 0;

                foreach (var slot in _preCacheSlots)
                {
                    slot.AllocatedBandwidthRatio = 0;
                }
            }
        }

        /// <summary>
        /// 分配预缓存槽（用于下一首预加载）
        /// </summary>
        /// <returns>槽索引，-1表示无可用槽</returns>
        public int AllocatePreCacheSlot()
        {
            return ActivatePreCache();
        }

        /// <summary>
        /// 释放预缓存槽（预加载完成后调用）
        /// </summary>
        public void ReleasePreCacheSlot(int slotIndex)
        {
            DeactivatePreCache(slotIndex);
        }

        /// <summary>
        /// 记录预缓存进度
        /// </summary>
        public void RecordPreCacheProgress(int slotIndex, long bytes)
        {
            if (slotIndex < 0 || slotIndex >= _preCacheSlots.Length)
            {
                return;
            }

            lock (_lock)
            {
                _preCacheSlots[slotIndex].BytesTransferred += bytes;
            }
        }

        /// <summary>
        /// 获取带宽分配统计信息
        /// </summary>
        public string GetStatistics()
        {
            lock (_lock)
            {
                int activePreCache = 0;
                foreach (var slot in _preCacheSlots)
                {
                    if (slot.IsActive) activePreCache++;
                }

                return $"主播放: {_mainPlaybackSlot.AllocatedBandwidthRatio * 100:F0}%, " +
                       $"Seek: {_seekSlot.AllocatedBandwidthRatio * 100:F0}%, " +
                       $"预缓存: {activePreCache}连接";
            }
        }
    }
}
