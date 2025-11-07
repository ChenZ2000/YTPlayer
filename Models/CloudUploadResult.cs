namespace YTPlayer.Models
{
    /// <summary>
    /// 云盘上传结果
    /// </summary>
    public class CloudUploadResult
    {
        /// <summary>
        /// 是否上传成功
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// 云盘条目ID
        /// </summary>
        public string CloudSongId { get; set; } = string.Empty;

        /// <summary>
        /// 匹配到的官方歌曲ID（可能为空）
        /// </summary>
        public string MatchedSongId { get; set; } = string.Empty;

        /// <summary>
        /// 上传的本地文件路径
        /// </summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>
        /// 错误信息（失败时）
        /// </summary>
        public string ErrorMessage { get; set; } = string.Empty;
    }
}
