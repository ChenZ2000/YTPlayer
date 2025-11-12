using System;
using System.IO;
using YTPlayer.Core;

namespace YTPlayer.Models.Download
{
    /// <summary>
    /// 单个下载任务
    /// </summary>
    public class DownloadTask
    {
        /// <summary>
        /// 唯一标识
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// 歌曲信息
        /// </summary>
        public SongInfo Song { get; set; }

        /// <summary>
        /// 目标文件路径（含文件名）
        /// </summary>
        public string DestinationPath { get; set; }

        /// <summary>
        /// 下载 URL
        /// </summary>
        public string DownloadUrl { get; set; }

        /// <summary>
        /// 下载内容类型（音频/歌词等）
        /// </summary>
        public DownloadContentType ContentType { get; set; } = DownloadContentType.Audio;

        /// <summary>
        /// 质量级别
        /// </summary>
        public QualityLevel Quality { get; set; }

        /// <summary>
        /// 当前状态
        /// </summary>
        public DownloadStatus Status { get; set; }

        /// <summary>
        /// 总字节数
        /// </summary>
        public long TotalBytes { get; set; }

        /// <summary>
        /// 已下载字节数
        /// </summary>
        public long DownloadedBytes { get; set; }

        /// <summary>
        /// 下载速度 (bytes/s)
        /// </summary>
        public double DownloadSpeed { get; set; }

        /// <summary>
        /// 来源列表名称
        /// </summary>
        public string SourceList { get; set; }

        /// <summary>
        /// 曲目编号（用于批量下载，写入元数据）
        /// </summary>
        public int? TrackNumber { get; set; }

        /// <summary>
        /// 开始时间
        /// </summary>
        public DateTime StartTime { get; set; }

        /// <summary>
        /// 完成时间
        /// </summary>
        public DateTime? CompletedTime { get; set; }

        /// <summary>
        /// 文件句柄（占用文件，防止被删除）
        /// </summary>
        public FileStream? FileHandle { get; set; }

        /// <summary>
        /// 取消令牌源（用于取消/暂停任务）
        /// </summary>
        public System.Threading.CancellationTokenSource? CancellationTokenSource { get; set; }

        /// <summary>
        /// 错误信息（失败时）
        /// </summary>
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// 重试次数
        /// </summary>
        public int RetryCount { get; set; }

        /// <summary>
        /// 歌词内容缓存（用于歌词下载）
        /// </summary>
        public string? LyricContent { get; set; }

        /// <summary>
        /// 构造函数
        /// </summary>
        public DownloadTask()
        {
            Id = Guid.NewGuid().ToString();
            Status = DownloadStatus.Pending;
            StartTime = DateTime.Now;
            RetryCount = 0;
            SourceList = string.Empty;
            DownloadUrl = string.Empty;
            DestinationPath = string.Empty;
            Song = null!;
        }

        /// <summary>
        /// 获取进度百分比
        /// </summary>
        public double ProgressPercentage
        {
            get
            {
                if (TotalBytes <= 0)
                    return 0;
                return (double)DownloadedBytes / TotalBytes * 100;
            }
        }

        /// <summary>
        /// 获取格式化的速度显示
        /// </summary>
        public string FormattedSpeed
        {
            get
            {
                if (Status == DownloadStatus.Paused)
                    return "已暂停";
                if (Status == DownloadStatus.Pending)
                    return "等待中";
                if (Status == DownloadStatus.Failed)
                    return "失败";
                if (Status == DownloadStatus.Cancelled)
                    return "已取消";
                if (Status == DownloadStatus.Completed)
                    return "已完成";

                if (DownloadSpeed < 1024)
                    return $"{DownloadSpeed:F0} B/s";
                if (DownloadSpeed < 1024 * 1024)
                    return $"{DownloadSpeed / 1024:F1} KB/s";
                return $"{DownloadSpeed / 1024 / 1024:F2} MB/s";
            }
        }

        /// <summary>
        /// 获取临时下载文件路径
        /// </summary>
        public string TempFilePath => DestinationPath + ".downloading";

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            FileHandle?.Dispose();
            FileHandle = null;
            CancellationTokenSource?.Dispose();
            CancellationTokenSource = null;
        }
    }
}
