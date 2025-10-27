using System;
using System.Diagnostics;

namespace YTPlayer.Core.Streaming
{
    /// <summary>
    /// 动态Buffer大小优化器 - 阶段2：根据网速和文件大小动态调整buffer
    /// </summary>
    public class DynamicBufferOptimizer
    {
        private const int MinBufferSize = 1 * 1024 * 1024;      // 1MB 最小
        private const int MaxBufferSize = 16 * 1024 * 1024;     // 16MB 最大
        private const int DefaultBufferSize = 4 * 1024 * 1024;  // 4MB 默认

        private readonly long _fileSize;
        private readonly Stopwatch _speedMeasurement = new Stopwatch();
        private long _bytesProcessed;
        private int _currentBufferSize;

        public DynamicBufferOptimizer(long fileSize)
        {
            _fileSize = fileSize;
            _currentBufferSize = CalculateInitialBufferSize(fileSize);
        }

        /// <summary>
        /// 获取当前推荐的buffer大小
        /// </summary>
        public int CurrentBufferSize => _currentBufferSize;

        /// <summary>
        /// 开始测速
        /// </summary>
        public void StartMeasurement()
        {
            _speedMeasurement.Restart();
            _bytesProcessed = 0;
        }

        /// <summary>
        /// 记录处理的字节数并更新buffer大小
        /// </summary>
        /// <param name="bytes">处理的字节数</param>
        public void RecordProgress(long bytes)
        {
            _bytesProcessed += bytes;

            // 每处理 64MB 就重新计算一次最优buffer大小
            if (_bytesProcessed >= 64 * 1024 * 1024)
            {
                UpdateBufferSize();
            }
        }

        /// <summary>
        /// 停止测速
        /// </summary>
        public void StopMeasurement()
        {
            _speedMeasurement.Stop();
        }

        /// <summary>
        /// 获取当前测量的速度（MB/s）
        /// </summary>
        public double GetCurrentSpeed()
        {
            if (!_speedMeasurement.IsRunning || _speedMeasurement.Elapsed.TotalSeconds < 0.1)
            {
                return 0;
            }

            return _bytesProcessed / _speedMeasurement.Elapsed.TotalSeconds / (1024 * 1024);
        }

        /// <summary>
        /// 根据文件大小计算初始buffer大小
        /// </summary>
        private int CalculateInitialBufferSize(long fileSize)
        {
            // 小文件 (<50MB): 2MB buffer
            if (fileSize < 50 * 1024 * 1024)
            {
                return 2 * 1024 * 1024;
            }

            // 中文件 (50-200MB): 4MB buffer
            if (fileSize < 200 * 1024 * 1024)
            {
                return 4 * 1024 * 1024;
            }

            // 大文件 (200-500MB): 8MB buffer
            if (fileSize < 500 * 1024 * 1024)
            {
                return 8 * 1024 * 1024;
            }

            // 超大文件 (>500MB): 16MB buffer
            return 16 * 1024 * 1024;
        }

        /// <summary>
        /// 根据实测速度更新buffer大小
        /// </summary>
        private void UpdateBufferSize()
        {
            double speedMBps = GetCurrentSpeed();

            // 根据网速调整buffer大小
            // 高速网络（>50MB/s）：使用大buffer加速丢弃
            if (speedMBps > 50)
            {
                _currentBufferSize = Math.Min(MaxBufferSize, _currentBufferSize * 2);
            }
            // 中速网络（10-50MB/s）：保持当前buffer
            else if (speedMBps > 10)
            {
                // 保持不变
            }
            // 低速网络（<10MB/s）：使用小buffer减少内存
            else if (speedMBps > 0)
            {
                _currentBufferSize = Math.Max(MinBufferSize, _currentBufferSize / 2);
            }

            // 重置计数器
            _speedMeasurement.Restart();
            _bytesProcessed = 0;

            Debug.WriteLine($"[DynamicBuffer] 速度: {speedMBps:F1} MB/s, 新Buffer大小: {_currentBufferSize / 1024 / 1024} MB");
        }

        /// <summary>
        /// 估算跳转到指定位置需要的时间（秒）
        /// </summary>
        public double EstimateSeekTime(long skipToPosition)
        {
            double speedMBps = GetCurrentSpeed();
            if (speedMBps <= 0)
            {
                // 没有速度数据，使用保守估计（假设 20MB/s）
                speedMBps = 20;
            }

            double skipMB = skipToPosition / (1024.0 * 1024.0);
            return skipMB / speedMBps;
        }
    }
}
