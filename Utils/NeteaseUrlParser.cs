using System;
using System.Collections.Generic;
using System.Globalization;

namespace YTPlayer.Utils
{
    internal enum NeteaseUrlType
    {
        Unknown = 0,
        Song,
        Playlist,
        Album,
        Artist
    }

    internal sealed class NeteaseUrlMatch
    {
        public NeteaseUrlMatch(NeteaseUrlType type, string resourceId, string raw)
        {
            Type = type;
            ResourceId = resourceId;
            RawUrl = raw;
        }

        public NeteaseUrlType Type { get; }
        public string ResourceId { get; }
        public string RawUrl { get; }
    }

    internal static class NeteaseUrlParser
    {
        private static readonly HashSet<string> SupportedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "music.163.com",
            "y.music.163.com",
            "m.music.163.com",
            "neteasecloudmusic.com",
            "163cn.tv",
            "163cn.com"
        };

        public static bool TryParse(string? rawInput, out NeteaseUrlMatch? match)
        {
            match = null;
            if (string.IsNullOrWhiteSpace(rawInput))
            {
                return false;
            }

            string input = rawInput!;
            string normalized = input.Trim();
            if (!normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                normalized = "https://" + normalized;
            }

            if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) || uri == null)
            {
                return false;
            }

            string host = uri.Host ?? string.Empty;
            if (!IsSupportedHost(host))
            {
                return false;
            }

            var (path, query) = NormalizePathAndQuery(uri);
            var segments = GetSegments(path);
            var type = ResolveType(segments);
            string? resourceId = ExtractResourceId(query);

            // 一些分享链接将 ID 直接放在路径段中，如 /song/12345
            if (string.IsNullOrEmpty(resourceId))
            {
                resourceId = ExtractIdFromSegments(segments);
            }

            if (type == NeteaseUrlType.Unknown || string.IsNullOrWhiteSpace(resourceId))
            {
                return false;
            }

            string resolvedId = resourceId!;
            match = new NeteaseUrlMatch(type, resolvedId, uri.ToString());
            return true;
        }

        private static bool IsSupportedHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                return false;
            }

            host = host.ToLowerInvariant();
            if (SupportedHosts.Contains(host))
            {
                return true;
            }

            // 允许子域，如 music.163.com.cn
            foreach (var supported in SupportedHosts)
            {
                if (host.EndsWith("." + supported, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static (string Path, string Query) NormalizePathAndQuery(Uri uri)
        {
            string path = uri.AbsolutePath ?? string.Empty;
            string query = uri.Query ?? string.Empty;

            if (!string.IsNullOrEmpty(uri.Fragment))
            {
                string fragment = uri.Fragment.TrimStart('#');
                if (!string.IsNullOrEmpty(fragment))
                {
                    // 处理 #/song?id=xxx 或 #song?id=xxx
                    if (!fragment.StartsWith("/", StringComparison.Ordinal))
                    {
                        fragment = "/" + fragment;
                    }

                    int index = fragment.IndexOf('?');
                    if (index >= 0)
                    {
                        path = fragment.Substring(0, index);
                        query = fragment.Substring(index);
                    }
                    else
                    {
                        path = fragment;
                        query = string.Empty;
                    }
                }
            }

            return (path, query);
        }

        private static List<string> GetSegments(string path)
        {
            var segments = new List<string>();
            if (string.IsNullOrWhiteSpace(path))
            {
                return segments;
            }

            foreach (var segment in path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                segments.Add(segment.ToLowerInvariant());
            }

            return segments;
        }

        private static NeteaseUrlType ResolveType(List<string> segments)
        {
            if (segments.Count == 0)
            {
                return NeteaseUrlType.Unknown;
            }

            foreach (var segment in segments)
            {
                switch (segment)
                {
                    case "song":
                    case "s":
                        return NeteaseUrlType.Song;
                    case "playlist":
                    case "pl":
                        return NeteaseUrlType.Playlist;
                    case "album":
                    case "al":
                        return NeteaseUrlType.Album;
                    case "artist":
                    case "ar":
                        return NeteaseUrlType.Artist;
                }
            }

            // 对于 /dj 或其他未知类型暂不处理
            return NeteaseUrlType.Unknown;
        }

        private static string? ExtractResourceId(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return null;
            }

            string trimmed = query[0] == '?' ? query.Substring(1) : query;
            foreach (var pair in trimmed.Split('&'))
            {
                if (string.IsNullOrWhiteSpace(pair))
                {
                    continue;
                }

                var kv = pair.Split(new[] { '=' }, 2);
                if (kv.Length != 2)
                {
                    continue;
                }

                if (kv[0].Equals("id", StringComparison.OrdinalIgnoreCase))
                {
                    return Uri.UnescapeDataString(kv[1]);
                }
            }

            return null;
        }

        private static string? ExtractIdFromSegments(List<string> segments)
        {
            if (segments.Count == 0)
            {
                return null;
            }

            // 尝试解析最后一个段为数字或 ID
            for (int i = segments.Count - 1; i >= 0; i--)
            {
                string segment = segments[i];
                if (long.TryParse(segment, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                {
                    return segment;
                }
            }

            return null;
        }
    }
}
