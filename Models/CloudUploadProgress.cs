namespace YTPlayer.Models
{
    /// <summary>
    /// 云盘上传进度信息
    /// </summary>
    public class CloudUploadProgress
    {
        /// <summary>
        /// 当前处理的文件路径
        /// </summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>
        /// 当前文件在批次中的序号（从1开始）
        /// </summary>
        public int FileIndex { get; set; }

        /// <summary>
        /// 总文件数
        /// </summary>
        public int TotalFiles { get; set; }

        /// <summary>
        /// 当前文件的进度百分比（0-100）
        /// </summary>
        public int FileProgressPercent { get; set; }

        /// <summary>
        /// 已传输字节数
        /// </summary>
        public long BytesTransferred { get; set; }

        /// <summary>
        /// 文件总字节数
        /// </summary>
        public long TotalBytes { get; set; }

        /// <summary>
        /// 估算的瞬时传输速率（字节/秒）
        /// </summary>
        public double TransferSpeedBytesPerSecond { get; set; }

        /// <summary>
        /// 描述当前步骤
        /// </summary>
        public string StageMessage { get; set; } = string.Empty;
    }
}
