namespace YTPlayer.Core.Playback.Cache
{
    /// <summary>
    /// 智能缓存可采用的下载策略。
    /// </summary>
    public enum DownloadStrategy
    {
        /// <summary>
        /// 支持 HTTP Range，按块下载。
        /// </summary>
        Range,

        /// <summary>
        /// 通过多连接并行方式拉取整个文件并写入缓存。
        /// </summary>
        ParallelFull,

        /// <summary>
        /// 通过单连接顺序方式拉取整个文件。
        /// </summary>
        SequentialFull
    }
}
