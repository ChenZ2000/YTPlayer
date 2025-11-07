using System.Collections.Generic;

namespace YTPlayer.Models
{
    /// <summary>
    /// 基础歌手信息。
    /// </summary>
    public class ArtistInfo
    {
        /// <summary>歌手ID。</summary>
        public long Id { get; set; }

        /// <summary>歌手名称。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>歌手别名。</summary>
        public string Alias { get; set; } = string.Empty;

        /// <summary>头像或封面图。</summary>
        public string PicUrl { get; set; } = string.Empty;

        /// <summary>地区编码。</summary>
        public int AreaCode { get; set; }

        /// <summary>地区显示名称。</summary>
        public string AreaName { get; set; } = string.Empty;

        /// <summary>歌手类型编码。</summary>
        public int TypeCode { get; set; }

        /// <summary>歌手类型显示名称。</summary>
        public string TypeName { get; set; } = string.Empty;

        /// <summary>歌曲数量。</summary>
        public int MusicCount { get; set; }

        /// <summary>专辑数量。</summary>
        public int AlbumCount { get; set; }

        /// <summary>MV 数量。</summary>
        public int MvCount { get; set; }

        /// <summary>简要描述（briefDesc）。</summary>
        public string BriefDesc { get; set; } = string.Empty;

        /// <summary>完整描述（来自 /artist/desc）。</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>是否已收藏该歌手。</summary>
        public bool IsSubscribed { get; set; }
    }

    /// <summary>
    /// 歌手详细信息。
    /// </summary>
    public class ArtistDetail : ArtistInfo
    {
        /// <summary>封面大图。</summary>
        public string CoverImageUrl { get; set; } = string.Empty;

        /// <summary>粉丝数量。</summary>
        public long FollowerCount { get; set; }

        /// <summary>扩展资料。</summary>
        public Dictionary<string, string> ExtraMetadata { get; set; } = new Dictionary<string, string>();

        /// <summary>详情段落。</summary>
        public List<ArtistIntroductionSection> Introductions { get; set; } = new List<ArtistIntroductionSection>();
    }

    /// <summary>
    /// 歌手简介段落。
    /// </summary>
    public class ArtistIntroductionSection
    {
        /// <summary>段落标题。</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>段落内容。</summary>
        public string Content { get; set; } = string.Empty;
    }

    /// <summary>
    /// 歌手分类选项（type 或 area）。
    /// </summary>
    public class ArtistCategoryOption
    {
        public int Code { get; set; }
        public string DisplayName { get; set; } = string.Empty;
    }

    /// <summary>
    /// 歌手分类过滤器（type + area）。
    /// </summary>
    public class ArtistCategoryFilter
    {
        public ArtistCategoryOption Type { get; set; } = new ArtistCategoryOption();
        public ArtistCategoryOption Area { get; set; } = new ArtistCategoryOption();
    }
}
