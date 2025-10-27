using System;
using YTPlayer.Core;

namespace YTPlayer.Models
{
    /// <summary>
    /// 列表项类型
    /// </summary>
    public enum ListItemType
    {
        /// <summary>歌曲</summary>
        Song,
        /// <summary>歌单</summary>
        Playlist,
        /// <summary>专辑</summary>
        Album,
        /// <summary>分类入口（用于主页）</summary>
        Category
    }

    /// <summary>
    /// 统一的列表项信息（支持歌曲、歌单、专辑混合显示）
    /// 参考 Python 版本的混合列表实现
    /// </summary>
    public class ListItemInfo
    {
        /// <summary>
        /// 列表项类型
        /// </summary>
        public ListItemType Type { get; set; }

        /// <summary>
        /// 歌曲信息（当Type为Song时使用）
        /// </summary>
        public SongInfo Song { get; set; }

        /// <summary>
        /// 歌单信息（当Type为Playlist时使用）
        /// </summary>
        public PlaylistInfo Playlist { get; set; }

        /// <summary>
        /// 专辑信息（当Type为Album时使用）
        /// </summary>
        public AlbumInfo Album { get; set; }

        /// <summary>
        /// 分类ID（当Type为Category时使用）
        /// </summary>
        public string CategoryId { get; set; }

        /// <summary>
        /// 分类名称（当Type为Category时使用）
        /// </summary>
        public string CategoryName { get; set; }

        /// <summary>
        /// 分类描述（当Type为Category时使用）
        /// </summary>
        public string CategoryDescription { get; set; }

        /// <summary>
        /// 获取显示ID
        /// </summary>
        public string Id
        {
            get
            {
                switch (Type)
                {
                    case ListItemType.Song:
                        return Song?.Id;
                    case ListItemType.Playlist:
                        return Playlist?.Id;
                    case ListItemType.Album:
                        return Album?.Id;
                    case ListItemType.Category:
                        return CategoryId;
                    default:
                        return null;
                }
            }
        }

        /// <summary>
        /// 获取显示名称
        /// </summary>
        public string Name
        {
            get
            {
                switch (Type)
                {
                    case ListItemType.Song:
                        return Song?.Name;
                    case ListItemType.Playlist:
                        return Playlist?.Name;
                    case ListItemType.Album:
                        return Album?.Name;
                    case ListItemType.Category:
                        return CategoryName;
                    default:
                        return "";
                }
            }
        }

        /// <summary>
        /// 获取显示的艺术家/创建者/发行者
        /// </summary>
        public string Creator
        {
            get
            {
                switch (Type)
                {
                    case ListItemType.Song:
                        return Song?.Artist;
                    case ListItemType.Playlist:
                        return Playlist?.Creator;
                    case ListItemType.Album:
                        return Album?.Artist;
                    case ListItemType.Category:
                        return CategoryDescription ?? "";
                    default:
                        return "";
                }
            }
        }

        /// <summary>
        /// 获取额外信息（专辑名/歌曲数量）
        /// </summary>
        public string ExtraInfo
        {
            get
            {
                switch (Type)
                {
                    case ListItemType.Song:
                        return Song?.Album ?? "";
                    case ListItemType.Playlist:
                        return $"{Playlist?.TrackCount ?? 0} 首";
                    case ListItemType.Album:
                        return Album?.PublishTime ?? "";
                    case ListItemType.Category:
                        return ""; // 分类入口不显示额外信息
                    default:
                        return "";
                }
            }
        }
    }
}
