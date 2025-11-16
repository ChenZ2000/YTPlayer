using System;
using System.Globalization;
using YTPlayer.Core;

namespace YTPlayer.Models
{
    internal static class AlbumDisplayHelper
    {
        public static string BuildTrackAndYearLabel(AlbumInfo? album)
        {
            if (album == null)
            {
                return string.Empty;
            }

            string trackLabel = album.TrackCount > 0 ? $"{album.TrackCount} 首" : string.Empty;
            string yearLabel = GetReleaseYearLabel(album);

            if (!string.IsNullOrEmpty(trackLabel) && !string.IsNullOrEmpty(yearLabel))
            {
                return $"{trackLabel} · {yearLabel}";
            }

            if (!string.IsNullOrEmpty(trackLabel))
            {
                return trackLabel;
            }

            return yearLabel;
        }

        public static string GetReleaseYearLabel(AlbumInfo? album)
        {
            if (album == null)
            {
                return string.Empty;
            }

            return ExtractYear(album.PublishTime);
        }

        private static string ExtractYear(string? publishTime)
        {
            if (string.IsNullOrWhiteSpace(publishTime))
            {
                return string.Empty;
            }

            if (DateTime.TryParse(publishTime, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
            {
                return parsed.Year.ToString(CultureInfo.InvariantCulture);
            }

            var trimmed = publishTime!.Trim();
            if (trimmed.Length >= 4 && int.TryParse(trimmed.Substring(0, 4), out var year))
            {
                return year.ToString(CultureInfo.InvariantCulture);
            }

            return string.Empty;
        }
    }
}
