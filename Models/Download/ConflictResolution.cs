namespace YTPlayer.Models.Download
{
    /// <summary>
    /// 文件冲突解决策略
    /// </summary>
    public enum ConflictResolution
    {
        /// <summary>
        /// 跳过当前文件
        /// </summary>
        Skip,

        /// <summary>
        /// 覆盖当前文件
        /// </summary>
        Overwrite,

        /// <summary>
        /// 全部跳过
        /// </summary>
        SkipAll,

        /// <summary>
        /// 全部覆盖
        /// </summary>
        OverwriteAll,

        /// <summary>
        /// 取消操作
        /// </summary>
        Cancel
    }
}
