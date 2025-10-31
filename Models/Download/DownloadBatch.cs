using System.Collections.Generic;

namespace YTPlayer.Models.Download
{
    /// <summary>
    /// 批量下载任务组
    /// </summary>
    public class DownloadBatch
    {
        /// <summary>
        /// 批次唯一标识
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// 批次名称（如：我喜欢的音乐、周杰伦专辑）
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// 任务列表
        /// </summary>
        public List<DownloadTask> Tasks { get; set; }

        /// <summary>
        /// 根目录（批量下载时创建的子文件夹）
        /// </summary>
        public string RootDirectory { get; set; }

        /// <summary>
        /// 构造函数
        /// </summary>
        public DownloadBatch()
        {
            Id = System.Guid.NewGuid().ToString();
            Name = string.Empty;
            Tasks = new List<DownloadTask>();
            RootDirectory = string.Empty;
        }

        /// <summary>
        /// 获取完成的任务数
        /// </summary>
        public int CompletedCount
        {
            get
            {
                int count = 0;
                foreach (var task in Tasks)
                {
                    if (task.Status == DownloadStatus.Completed)
                        count++;
                }
                return count;
            }
        }

        /// <summary>
        /// 获取总任务数
        /// </summary>
        public int TotalCount => Tasks.Count;

        /// <summary>
        /// 获取进度百分比
        /// </summary>
        public double ProgressPercentage
        {
            get
            {
                if (TotalCount == 0)
                    return 0;
                return (double)CompletedCount / TotalCount * 100;
            }
        }
    }
}
