using System;
using System.Collections.Generic;
using YTPlayer.Core;

namespace YTPlayer.Models
{
    /// <summary>
    /// 列表项类型。
    /// </summary>
    public enum ListItemType
    {
        /// <summary>歌曲</summary>
        Song,
        /// <summary>歌单</summary>
        Playlist,
        /// <summary>专辑</summary>
        Album,
        /// <summary>歌手</summary>
        Artist,
        /// <summary>播客/电台</summary>
        Podcast,
        /// <summary>播客节目</summary>
        PodcastEpisode,
        /// <summary>分类入口（用于主页）</summary>
        Category
    }

    /// <summary>
    /// 统一的列表项信息（支持歌曲、歌单、专辑、歌手、分类混合显示）。
    /// </summary>
    public class ListItemInfo
    {
        /// <summary>列表项类型。</summary>
        public ListItemType Type { get; set; }

        /// <summary>歌曲信息。</summary>
        public SongInfo? Song { get; set; }

        /// <summary>歌单信息。</summary>
        public PlaylistInfo? Playlist { get; set; }

        /// <summary>专辑信息。</summary>
        public AlbumInfo? Album { get; set; }

        /// <summary>歌手信息。</summary>
        public ArtistInfo? Artist { get; set; }

        /// <summary>播客电台信息。</summary>
        public PodcastRadioInfo? Podcast { get; set; }

        /// <summary>播客节目信息。</summary>
        public PodcastEpisodeInfo? PodcastEpisode { get; set; }

        /// <summary>分类ID。</summary>
        public string? CategoryId { get; set; }

        /// <summary>分类名称。</summary>
        public string? CategoryName { get; set; }

        /// <summary>分类描述。</summary>
        public string? CategoryDescription { get; set; }

        /// <summary>分类项数量。</summary>
        public int? ItemCount { get; set; }

        /// <summary>分类项计量单位。</summary>
        public string? ItemUnit { get; set; }

        /// <summary>
        /// 列表项唯一标识。
        /// </summary>
        public string? Id
        {
            get
            {
                return Type switch
                {
                    ListItemType.Song => Song?.Id,
                    ListItemType.Playlist => Playlist?.Id,
                    ListItemType.Album => Album?.Id,
                    ListItemType.Artist => Artist?.Id.ToString(),
                    ListItemType.Podcast => Podcast?.Id.ToString(),
                    ListItemType.PodcastEpisode => PodcastEpisode?.ProgramId.ToString(),
                    ListItemType.Category => CategoryId,
                    _ => null
                };
            }
        }

        /// <summary>
        /// 列表项显示名称。
        /// </summary>
        public string Name
        {
            get
            {
                return Type switch
                {
                    ListItemType.Song => Song?.Name ?? string.Empty,
                    ListItemType.Playlist => Playlist?.Name ?? string.Empty,
                    ListItemType.Album => Album?.Name ?? string.Empty,
                    ListItemType.Artist => Artist?.Name ?? string.Empty,
                    ListItemType.Podcast => Podcast?.Name ?? string.Empty,
                    ListItemType.PodcastEpisode => PodcastEpisode?.Name ?? string.Empty,
                    ListItemType.Category => CategoryName ?? string.Empty,
                    _ => string.Empty
                };
            }
        }

        /// <summary>
        /// 附加的创建者/艺人描述。
        /// </summary>
        public string Creator
        {
            get
            {
                return Type switch
                {
                    ListItemType.Song => Song?.Artist ?? string.Empty,
                    ListItemType.Playlist => Playlist?.Creator ?? string.Empty,
                    ListItemType.Album => Album?.Artist ?? string.Empty,
                    ListItemType.Artist => string.Empty,
                    ListItemType.Podcast => Podcast?.DjName ?? string.Empty,
                    ListItemType.PodcastEpisode => string.IsNullOrWhiteSpace(PodcastEpisode?.DjName)
                        ? PodcastEpisode?.RadioName ?? string.Empty
                        : PodcastEpisode!.DjName,
                    ListItemType.Category => string.Empty,
                    _ => string.Empty
                };
            }
        }

        /// <summary>
        /// 额外信息（如数量、别名等）。
        /// </summary>
        public string ExtraInfo
        {
            get
            {
                return Type switch
                {
                    ListItemType.Song => Song?.Album ?? string.Empty,
                    ListItemType.Playlist => FormatCount(Playlist?.TrackCount),
                    ListItemType.Album => AlbumDisplayHelper.BuildTrackAndYearLabel(Album),
                    ListItemType.Artist => BuildArtistExtraInfo(),
                    ListItemType.Podcast => Podcast != null && Podcast.ProgramCount > 0
                        ? $"{Podcast.ProgramCount} 个节目"
                        : string.Empty,
                    ListItemType.PodcastEpisode => BuildPodcastEpisodeExtraInfo(),
                    ListItemType.Category => BuildCategoryExtraInfo(),
                    _ => string.Empty
                };
            }
        }

        /// <summary>
        /// 描述信息（用于列表的最后一列）。
        /// </summary>
        public string Description
        {
            get
            {
                return Type switch
                {
                    ListItemType.Category => CategoryDescription ?? string.Empty,
                    ListItemType.Playlist => Playlist?.Description ?? string.Empty,
                    ListItemType.Album => Album?.Description ?? string.Empty,
                    ListItemType.Artist => !string.IsNullOrWhiteSpace(Artist?.Description)
                        ? Artist!.Description
                        : Artist?.BriefDesc ?? string.Empty,
                    ListItemType.Podcast => Podcast?.Description ?? string.Empty,
                    ListItemType.PodcastEpisode => BuildPodcastEpisodeDescription(),
                    ListItemType.Song => Song?.FormattedDuration ?? string.Empty,
                    _ => string.Empty
                };
            }
        }

        private string BuildArtistExtraInfo()
        {
            if (Artist == null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(Artist.Alias))
            {
                return Artist.Alias;
            }

            var parts = new List<string>();

            if (Artist.MusicCount > 0)
            {
                parts.Add($"歌曲 {Artist.MusicCount}");
            }

            if (Artist.AlbumCount > 0)
            {
                parts.Add($"专辑 {Artist.AlbumCount}");
            }

            if (parts.Count > 0)
            {
                return string.Join(" | ", parts);
            }

            return string.Empty;
        }

        private string BuildCategoryExtraInfo()
        {
            if (ItemCount.HasValue && ItemCount.Value > 0 && !string.IsNullOrEmpty(ItemUnit))
            {
                return $"{ItemCount.Value} {ItemUnit}";
            }

            return string.Empty;
        }

        private static string FormatCount(int? count)
        {
            if (!count.HasValue || count.Value <= 0)
            {
                return string.Empty;
            }

            return $"{count.Value} 首";
        }

        private string BuildPodcastEpisodeExtraInfo()
        {
            if (PodcastEpisode == null)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            if (PodcastEpisode.PublishTime.HasValue)
            {
                parts.Add(PodcastEpisode.PublishTime.Value.ToString("yyyy-MM-dd"));
            }

            if (PodcastEpisode.Duration > TimeSpan.Zero)
            {
                parts.Add($"{PodcastEpisode.Duration:mm\\:ss}");
            }

            if (parts.Count == 0 && PodcastEpisode.ListenerCount > 0)
            {
                parts.Add($"收听 {PodcastEpisode.ListenerCount}");
            }

            return string.Join(" | ", parts);
        }

        private string BuildPodcastEpisodeDescription()
        {
            if (PodcastEpisode == null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(PodcastEpisode.Description))
            {
                return PodcastEpisode.Description;
            }

            if (PodcastEpisode.ListenerCount > 0 || PodcastEpisode.LikedCount > 0)
            {
                var parts = new List<string>();
                if (PodcastEpisode.ListenerCount > 0)
                {
                    parts.Add($"收听 {PodcastEpisode.ListenerCount}");
                }

                if (PodcastEpisode.LikedCount > 0)
                {
                    parts.Add($"赞 {PodcastEpisode.LikedCount}");
                }

                return string.Join(" / ", parts);
            }

            return string.Empty;
        }
    }
}

