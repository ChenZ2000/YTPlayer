using System;

namespace YTPlayer.Models
{
    /// <summary>
    /// 播客/电台分类信息。
    /// </summary>
    public class PodcastCategoryInfo
    {
        /// <summary>分类 ID。</summary>
        public int Id { get; set; }

        /// <summary>分类名称。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>分类类型/层级（如果有）。</summary>
        public int? CategoryType { get; set; }

        /// <summary>分类图标地址。</summary>
        public string PicUrl { get; set; } = string.Empty;
    }
}
