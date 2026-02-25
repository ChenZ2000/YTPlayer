using System;
using System.Threading;

namespace YTPlayer.Models.Upload
{
    /// <summary>
    /// 上传任务状态
    /// </summary>
    public enum UploadStatus
    {
        /// <summary>
        /// 等待中
        /// </summary>
        Pending,

        /// <summary>
        /// 上传中
        /// </summary>
        Uploading,

        /// <summary>
        /// 已暂停
        /// </summary>
        Paused,

        /// <summary>
        /// 已完成
        /// </summary>
        Completed,

        /// <summary>
        /// 失败
        /// </summary>
        Failed,

        /// <summary>
        /// 已取消
        /// </summary>
        Cancelled
    }

    /// <summary>
    /// 上传任务
    /// </summary>
    public class UploadTask : IDisposable
    {
        /// <summary>
        /// 任务唯一标识
        /// </summary>
        public Guid TaskId { get; set; } = Guid.NewGuid();

        /// <summary>
        /// 本地文件路径
        /// </summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>
        /// 文件名
        /// </summary>
        public string FileName { get; set; } = string.Empty;

        /// <summary>
        /// 来源列表名称（可选）
        /// </summary>
        public string SourceList { get; set; } = string.Empty;

        /// <summary>
        /// 任务状态
        /// </summary>
        public UploadStatus Status { get; set; } = UploadStatus.Pending;

        /// <summary>
        /// 进度百分比 (0-100)
        /// </summary>
        public int ProgressPercentage { get; set; }

        /// <summary>
        /// 当前阶段消息
        /// </summary>
        public string StageMessage { get; set; } = "等待中";

        /// <summary>
        /// 文件大小（字节）
        /// </summary>
        public long TotalBytes { get; set; }

        /// <summary>
        /// 已上传字节数
        /// </summary>
        public long UploadedBytes { get; set; }

        /// <summary>
        /// 当前瞬时速度（字节/秒）
        /// </summary>
        public double CurrentSpeedBytesPerSecond { get; set; }

        /// <summary>
        /// 上传后的云盘歌曲 ID
        /// </summary>
        public string CloudSongId { get; set; } = string.Empty;

        /// <summary>
        /// 匹配到的官方歌曲 ID
        /// </summary>
        public string MatchedSongId { get; set; } = string.Empty;

        /// <summary>
        /// 上传条目解析出的时长（秒）
        /// </summary>
        public int DurationSeconds { get; set; }

        /// <summary>
        /// 错误信息
        /// </summary>
        public string ErrorMessage { get; set; } = string.Empty;

        /// <summary>
        /// 创建时间
        /// </summary>
        public DateTime CreatedTime { get; set; } = DateTime.Now;

        /// <summary>
        /// 开始时间
        /// </summary>
        public DateTime? StartTime { get; set; }

        /// <summary>
        /// 完成时间
        /// </summary>
        public DateTime? CompletedTime { get; set; }

        /// <summary>
        /// 取消令牌源
        /// </summary>
        public CancellationTokenSource? CancellationTokenSource { get; set; }

        /// <summary>
        /// 格式化文件大小
        /// </summary>
        public string FormattedFileSize
        {
            get
            {
                if (TotalBytes <= 0)
                {
                    return "0 B";
                }

                string[] units = { "B", "KB", "MB", "GB" };
                int order = 0;
                double value = TotalBytes;
                while (value >= 1024 && order < units.Length - 1)
                {
                    order++;
                    value /= 1024;
                }
                return $"{value:0.##} {units[order]}";
            }
        }

        /// <summary>
        /// 格式化的实时速度显示
        /// </summary>
        public string FormattedSpeed
        {
            get
            {
                if (CurrentSpeedBytesPerSecond <= 0)
                {
                    return "--";
                }

                double speed = CurrentSpeedBytesPerSecond;
                string[] units = { "B/s", "KB/s", "MB/s", "GB/s" };
                int order = 0;
                while (speed >= 1024 && order < units.Length - 1)
                {
                    speed /= 1024;
                    order++;
                }

                return $"{speed:0.##} {units[order]}";
            }
        }

        public void Dispose()
        {
            CancellationTokenSource?.Dispose();
            CancellationTokenSource = null;
        }
    }
}
