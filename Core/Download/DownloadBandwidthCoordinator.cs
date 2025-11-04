using System;
using YTPlayer.Utils;

namespace YTPlayer.Core.Download
{
    /// <summary>
    /// 下载带宽协调器
    /// 负责协调播放、预加载和下载之间的带宽分配
    /// </summary>
    public class DownloadBandwidthCoordinator
    {
        #region 单例

        private static readonly object _lock = new object();
        private static DownloadBandwidthCoordinator? _instance;

        /// <summary>
        /// 获取单例实例
        /// </summary>
        public static DownloadBandwidthCoordinator Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = new DownloadBandwidthCoordinator();
                        }
                    }
                }
                return _instance;
            }
        }

        #endregion

        #region 常量 - 带宽分配策略

        /// <summary>
        /// 播放中 + 预加载 → 下载/上传分配比例
        /// </summary>
        private const double TRANSFER_ALLOCATION_WITH_PLAYBACK_AND_PRECACHE = 0.10;

        /// <summary>
        /// 播放中 + 无预加载 → 下载/上传分配比例
        /// </summary>
        private const double TRANSFER_ALLOCATION_WITH_PLAYBACK_ONLY = 0.20;

        /// <summary>
        /// 无播放 → 下载/上传分配比例
        /// </summary>
        private const double TRANSFER_ALLOCATION_NO_PLAYBACK = 1.0;

        #endregion

        #region 私有字段

        private bool _isPlaybackActive;
        private bool _isPrecacheActive;

        #endregion

        #region 构造函数

        /// <summary>
        /// 私有构造函数（单例模式）
        /// </summary>
        private DownloadBandwidthCoordinator()
        {
            _isPlaybackActive = false;
            _isPrecacheActive = false;
        }

        #endregion

        #region 公共方法 - 状态更新

        /// <summary>
        /// 通知播放状态改变
        /// </summary>
        /// <param name="isPlaying">是否正在播放</param>
        public void NotifyPlaybackStateChanged(bool isPlaying)
        {
            lock (_lock)
            {
                _isPlaybackActive = isPlaying;
                LogBandwidthAllocation();
            }
        }

        /// <summary>
        /// 通知预加载状态改变
        /// </summary>
        /// <param name="isPrecaching">是否正在预加载</param>
        public void NotifyPrecacheStateChanged(bool isPrecaching)
        {
            lock (_lock)
            {
                _isPrecacheActive = isPrecaching;
                LogBandwidthAllocation();
            }
        }

        #endregion

        #region 公共方法 - 查询

        /// <summary>
        /// 获取当前下载可用的带宽分配比例
        /// </summary>
        /// <returns>带宽分配比例（0.0 - 1.0）</returns>
        public double GetDownloadBandwidthAllocation()
        {
            lock (_lock)
            {
                if (!_isPlaybackActive)
                {
                    // 无播放：下载使用 100% 带宽
                    return TRANSFER_ALLOCATION_NO_PLAYBACK;
                }
                else if (_isPrecacheActive)
                {
                    // 播放中 + 预加载：下载使用 10% 带宽
                    return TRANSFER_ALLOCATION_WITH_PLAYBACK_AND_PRECACHE;
                }
                else
                {
                    // 播放中 + 无预加载：下载使用 20% 带宽
                    return TRANSFER_ALLOCATION_WITH_PLAYBACK_ONLY;
                }
            }
        }

        /// <summary>
        /// 获取当前上传可用的带宽分配比例
        /// </summary>
        public double GetUploadBandwidthAllocation()
        {
            // 目前上传遵循与下载相同的带宽策略
            return GetDownloadBandwidthAllocation();
        }

        /// <summary>
        /// 判断当前是否适合启动新的下载任务
        /// </summary>
        /// <param name="activeDownloadCount">当前活跃的下载任务数</param>
        /// <param name="maxConcurrentDownloads">最大并发下载数</param>
        /// <returns>是否可以启动新任务</returns>
        public bool CanStartNewDownload(int activeDownloadCount, int maxConcurrentDownloads)
        {
            lock (_lock)
            {
                // 检查是否已达到最大并发数
                if (activeDownloadCount >= maxConcurrentDownloads)
                {
                    return false;
                }

                // 如果正在播放且有预加载，限制下载并发数为 1
                if (_isPlaybackActive && _isPrecacheActive)
                {
                    return activeDownloadCount < 1;
                }

                // 如果正在播放但无预加载，限制下载并发数为 2
                if (_isPlaybackActive && !_isPrecacheActive)
                {
                    return activeDownloadCount < 2;
                }

                // 无播放，允许全部并发
                return true;
            }
        }

        /// <summary>
        /// 判断当前是否适合启动新的上传任务
        /// </summary>
        /// <param name="activeUploadCount">当前活跃的上传任务数</param>
        /// <param name="maxConcurrentUploads">配置的最大并发上传数</param>
        /// <returns>是否可以启动新的上传任务</returns>
        public bool CanStartNewUpload(int activeUploadCount, int maxConcurrentUploads)
        {
            lock (_lock)
            {
                if (activeUploadCount >= maxConcurrentUploads)
                {
                    return false;
                }

                if (_isPlaybackActive && _isPrecacheActive)
                {
                    return activeUploadCount < 1;
                }

                if (_isPlaybackActive && !_isPrecacheActive)
                {
                    int ceiling = Math.Min(2, maxConcurrentUploads);
                    return activeUploadCount < ceiling;
                }

                return true;
            }
        }

        /// <summary>
        /// 获取当前推荐的最大并发下载数
        /// </summary>
        /// <param name="configuredMaxConcurrent">配置的最大并发数</param>
        /// <returns>推荐的最大并发数</returns>
        public int GetRecommendedMaxConcurrentDownloads(int configuredMaxConcurrent)
        {
            lock (_lock)
            {
                if (_isPlaybackActive && _isPrecacheActive)
                {
                    // 播放中 + 预加载：最多 1 个下载任务
                    return Math.Min(1, configuredMaxConcurrent);
                }
                else if (_isPlaybackActive)
                {
                    // 播放中 + 无预加载：最多 2 个下载任务
                    return Math.Min(2, configuredMaxConcurrent);
                }
                else
                {
                    // 无播放：使用配置的最大值
                    return configuredMaxConcurrent;
                }
            }
        }

        /// <summary>
        /// 获取当前推荐的最大并发上传数
        /// </summary>
        /// <param name="configuredMaxConcurrent">配置的最大并发上传数</param>
        public int GetRecommendedMaxConcurrentUploads(int configuredMaxConcurrent)
        {
            lock (_lock)
            {
                if (_isPlaybackActive && _isPrecacheActive)
                {
                    return Math.Min(1, configuredMaxConcurrent);
                }

                if (_isPlaybackActive)
                {
                    return Math.Min(2, configuredMaxConcurrent);
                }

                return configuredMaxConcurrent;
            }
        }

        #endregion

        #region 私有方法

        /// <summary>
        /// 记录当前带宽分配情况
        /// </summary>
        private void LogBandwidthAllocation()
        {
            double allocation = GetDownloadBandwidthAllocation();
            string state = _isPlaybackActive
                ? (_isPrecacheActive ? "播放中+预加载" : "播放中")
                : "无播放";

            DebugLogger.Log(
                DebugLogger.LogLevel.Info,
                "BandwidthCoordinator",
                $"带宽分配更新: {state}, 下载分配: {allocation * 100:F0}%");
        }

        #endregion

        #region 辅助方法 - 供外部调用

        /// <summary>
        /// 重置协调器状态（用于测试或重启场景）
        /// </summary>
        public void Reset()
        {
            lock (_lock)
            {
                _isPlaybackActive = false;
                _isPrecacheActive = false;
                DebugLogger.Log(
                    DebugLogger.LogLevel.Info,
                    "BandwidthCoordinator",
                    "协调器状态已重置");
            }
        }

        #endregion
    }
}
