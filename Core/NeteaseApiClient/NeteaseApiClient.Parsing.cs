using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BrotliSharpLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YTPlayer.Core.Auth;
using YTPlayer.Core.Streaming;
using YTPlayer.Models;
using YTPlayer.Models.Auth;
using YTPlayer.Utils;

#pragma warning disable CS8600, CS8601, CS8602, CS8603, CS8604, CS8625

namespace YTPlayer.Core
{
    public partial class NeteaseApiClient
    {
        #region 数据解析方法

        /// <summary>
        /// 解析歌手列表。
        /// </summary>
        private List<ArtistInfo> ParseArtistList(JArray artists)
        {
            var result = new List<ArtistInfo>();
            if (artists == null) return result;

            foreach (var artistToken in artists)
            {
                if (artistToken == null || artistToken.Type != JTokenType.Object)
                {
                    continue;
                }

                try
                {
                    var artistInfo = ParseArtistObject((JObject)artistToken);
                    if (artistInfo != null && artistInfo.Id > 0)
                    {
                        result.Add(artistInfo);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[API] 解析歌手失败: {ex.Message}");
                }
            }

            return result;
        }

        /// <summary>
        /// 解析歌手对象。
        /// </summary>
        private ArtistInfo? ParseArtistObject(JObject artistObject)
        {
            if (artistObject == null)
            {
                return null;
            }

            var artistInfo = new ArtistInfo
            {
                Id = artistObject["id"]?.Value<long>()
                     ?? artistObject["artistId"]?.Value<long>()
                     ?? artistObject["userId"]?.Value<long>()
                     ?? 0,
                Name = artistObject["name"]?.Value<string>()
                    ?? artistObject["artistName"]?.Value<string>()
                    ?? string.Empty,
                PicUrl = artistObject["picUrl"]?.Value<string>()
                    ?? artistObject["img1v1Url"]?.Value<string>()
                    ?? artistObject["avatar"]?.Value<string>()
                    ?? artistObject["cover"]?.Value<string>()
                    ?? string.Empty,
                AreaCode = artistObject["area"]?.Value<int?>()
                    ?? artistObject["areaCode"]?.Value<int?>()
                    ?? 0,
                TypeCode = artistObject["type"]?.Value<int?>()
                    ?? artistObject["artistType"]?.Value<int?>()
                    ?? 0,
                MusicCount = artistObject["musicSize"]?.Value<int?>()
                    ?? artistObject["musicCount"]?.Value<int?>()
                    ?? artistObject["songCount"]?.Value<int?>()
                    ?? 0,
                AlbumCount = artistObject["albumSize"]?.Value<int?>()
                    ?? artistObject["albumCount"]?.Value<int?>()
                    ?? 0,
                MvCount = artistObject["mvSize"]?.Value<int?>()
                    ?? artistObject["mvCount"]?.Value<int?>()
                    ?? 0,
                BriefDesc = artistObject["briefDesc"]?.Value<string>() ?? string.Empty,
                Description = artistObject["desc"]?.Value<string>() ?? string.Empty,
                IsSubscribed = artistObject["followed"]?.Value<bool?>()
                    ?? artistObject["follow"]?.Value<bool?>()
                    ?? false
            };

            if (string.IsNullOrWhiteSpace(artistInfo.PicUrl))
            {
                artistInfo.PicUrl = artistObject["avatarUrl"]?.Value<string>()
                    ?? artistObject["img1v1"]?.Value<string>()
                    ?? string.Empty;
            }

            var aliasArray = artistObject["alias"] as JArray;
            if (aliasArray != null && aliasArray.Count > 0)
            {
                var aliasList = aliasArray
                    .Select(a => a?.Value<string>())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();

                if (aliasList.Count > 0)
                {
                    artistInfo.Alias = string.Join("/", aliasList);
                }
            }

            if (string.IsNullOrWhiteSpace(artistInfo.Alias))
            {
                var translated = artistObject["trans"]?.Value<string>()
                    ?? artistObject["tns"]?.FirstOrDefault()?.Value<string>();
                if (!string.IsNullOrWhiteSpace(translated))
                {
                    artistInfo.Alias = translated;
                }
            }

            artistInfo.AreaName = string.Empty;
            artistInfo.TypeName = string.Empty;
            artistInfo.BriefDesc = NormalizeSummary(artistInfo.BriefDesc);
            artistInfo.Description = NormalizeDescription(artistInfo.Description, artistInfo.BriefDesc);

            return artistInfo;
        }

        /// <summary>
        /// 解析歌曲列表
        /// </summary>
        private List<SongInfo> ParseSongList(JArray songs)
        {
            var result = new List<SongInfo>();
            if (songs == null) return result;

            int successCount = 0;
            int failCount = 0;

            foreach (var songToken in songs)
            {
                try
                {
                    if (songToken == null || songToken.Type != JTokenType.Object)
                    {
                        failCount++;
                        System.Diagnostics.Debug.WriteLine($"[API] 跳过非对象类型的歌曲条目: 类型={songToken?.Type}");
                        continue;
                    }

                    var song = (JObject)songToken;

                    // 检查歌曲是否可用（参考网易云API，status=0表示正常，-200表示下架等）
                    var status = song["st"]?.Value<int>() ?? song["status"]?.Value<int>() ?? 0;
                    var id = song["id"]?.Value<string>();

                    // 跳过没有 ID 或名称的歌曲
                    var name = song["name"]?.Value<string>();
                    if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
                    {
                        System.Diagnostics.Debug.WriteLine($"[API] 跳过缺失字段的歌曲 ID={id}, Name={name}");
                        failCount++;
                        continue;
                    }

                    var albumToken = song["al"] as JObject ?? song["album"] as JObject;
                    string albumName = albumToken?["name"]?.Value<string>();
                    string albumId = albumToken?["id"]?.Value<string>();
                    string albumPic = albumToken?["picUrl"]?.Value<string>();

                    if (string.IsNullOrEmpty(albumName))
                    {
                        if (song["al"] != null && song["al"].Type == JTokenType.String)
                        {
                            albumName = song["al"].Value<string>();
                        }
                        else if (song["album"] != null && song["album"].Type == JTokenType.String)
                        {
                            albumName = song["album"].Value<string>();
                        }
                    }

                    var songInfo = new SongInfo
                    {
                        Id = id,
                        Name = name,
                        Duration = SanitizeDurationSeconds(song["dt"]?.Value<long>() ?? song["duration"]?.Value<long>()),
                        Album = albumName,
                        AlbumId = albumId,
                        PicUrl = albumPic,
                        IsAvailable = status >= 0
                    };
                    songInfo.RequiresVip = IsVipSong(song);

                    // 解析艺术家
                    var artists = song["ar"] as JArray ?? song["artists"] as JArray;
                    if (artists != null && artists.Count > 0)
                    {
                        var artistNames = new List<string>();
                        var artistIds = new List<long>();

                        foreach (var artistToken in artists)
                        {
                            if (artistToken == null || artistToken.Type != JTokenType.Object)
                            {
                                continue;
                            }

                            var artistObj = (JObject)artistToken;

                            var artistName = artistObj["name"]?.Value<string>();
                            if (!string.IsNullOrWhiteSpace(artistName))
                            {
                                artistNames.Add(artistName);
                            }

                            var artistIdValue = artistObj["id"]?.Value<long>() ?? 0;
                            if (artistIdValue > 0)
                            {
                                artistIds.Add(artistIdValue);
                            }
                        }

                        if (artistNames.Count > 0)
                        {
                            songInfo.ArtistNames = new List<string>(artistNames);
                            songInfo.Artist = string.Join("/", artistNames);
                        }

                        if (artistIds.Count > 0)
                        {
                            songInfo.ArtistIds = new List<long>(artistIds);
                        }
                    }

                    // 发布时间
                    var publishTime = song["publishTime"]?.Value<long>();
                    if (publishTime.HasValue)
                    {
                        songInfo.PublishTime = DateTimeOffset.FromUnixTimeMilliseconds(publishTime.Value)
                            .DateTime.ToString("yyyy-MM-dd");
                    }

                    result.Add(songInfo);
                    successCount++;
                }
                catch (Exception ex)
                {
                    failCount++;
                    System.Diagnostics.Debug.WriteLine($"[API] 解析歌曲失败: {ex.Message}");
                }
            }

            if (failCount > 0)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 解析完成: 成功 {successCount} 首, 失败/跳过 {failCount} 首");
            }

            return result;
        }

        /// <summary>
        /// 解析歌单列表
        /// </summary>
        private List<PlaylistInfo> ParsePlaylistList(JArray playlists)
        {
            var result = new List<PlaylistInfo>();
            if (playlists == null)
            {
                return result;
            }

            foreach (var playlistToken in playlists)
            {
                if (playlistToken is not JObject playlistObject)
                {
                    continue;
                }

                try
                {
                    var playlistInfo = CreatePlaylistInfo(playlistObject);
                    PopulatePlaylistOwnershipDefaults(playlistInfo, playlistObject);
                    result.Add(playlistInfo);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[API] 解析歌单失败: {ex.Message}");
                }
            }

            return result;
        }

        private PlaylistInfo CreatePlaylistInfo(JObject playlistToken)
        {
            if (playlistToken == null)
            {
                return new PlaylistInfo();
            }

            var playlistInfo = new PlaylistInfo
            {
                Id = playlistToken["id"]?.Value<string>()
                    ?? playlistToken["playlistId"]?.Value<string>()
                    ?? playlistToken["resourceId"]?.Value<string>()
                    ?? string.Empty,
                Name = playlistToken["name"]?.Value<string>()
                    ?? playlistToken["title"]?.Value<string>()
                    ?? string.Empty,
                CoverUrl = playlistToken["coverImgUrl"]?.Value<string>()
                    ?? playlistToken["coverUrl"]?.Value<string>()
                    ?? playlistToken["picUrl"]?.Value<string>()
                    ?? string.Empty,
                Description = playlistToken["description"]?.Value<string>()
                    ?? playlistToken["desc"]?.Value<string>()
                    ?? string.Empty,
                TrackCount = ResolveTrackCount(playlistToken),
                Creator = playlistToken["creator"]?["nickname"]?.Value<string>()
                    ?? playlistToken["creatorName"]?.Value<string>()
                    ?? string.Empty,
                CreatorId = playlistToken["creator"]?["userId"]?.Value<long?>() ?? 0,
                OwnerUserId = playlistToken["userId"]?.Value<long?>()
                    ?? playlistToken["ownerId"]?.Value<long?>()
                    ?? 0,
                IsSubscribed = playlistToken["subscribed"]?.Value<bool?>()
                    ?? playlistToken["isSub"]?.Value<bool?>()
                    ?? false
            };

            return playlistInfo;
        }

        private static int ResolveTrackCount(JObject playlistToken)
        {
            if (playlistToken == null)
            {
                return 0;
            }

            int count =
                SafeToInt(playlistToken["trackCount"]) ??
                SafeToInt(playlistToken["songCount"]) ??
                SafeToInt(playlistToken["size"]) ??
                SafeToInt(playlistToken["trackNumber"]) ??
                0;

            if (count > 0)
            {
                return count;
            }

            var trackIds = playlistToken["trackIds"] as JArray;
            if (trackIds != null && trackIds.Count > 0)
            {
                return trackIds.Count;
            }

            var tracks = playlistToken["tracks"] as JArray;
            if (tracks != null && tracks.Count > 0)
            {
                return tracks.Count;
            }

            return 0;
        }

        private static int? SafeToInt(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            try
            {
                switch (token.Type)
                {
                    case JTokenType.Integer:
                        var integerValue = token.Value<long>();
                        if (integerValue < 0)
                        {
                            return 0;
                        }
                        if (integerValue > int.MaxValue)
                        {
                            return int.MaxValue;
                        }
                        return (int)integerValue;

                    case JTokenType.Float:
                        var floatValue = token.Value<double>();
                        if (double.IsNaN(floatValue))
                        {
                            return null;
                        }
                        if (floatValue < 0)
                        {
                            return 0;
                        }
                        if (floatValue > int.MaxValue)
                        {
                            return int.MaxValue;
                        }
                        return (int)Math.Round(floatValue);

                    case JTokenType.String:
                        var stringValue = token.Value<string>();
                        if (string.IsNullOrWhiteSpace(stringValue))
                        {
                            return null;
                        }

                        if (int.TryParse(stringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt))
                        {
                            return Math.Max(0, parsedInt);
                        }

                        if (long.TryParse(stringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLong))
                        {
                            if (parsedLong < 0)
                            {
                                return 0;
                            }

                            return parsedLong > int.MaxValue ? int.MaxValue : (int)parsedLong;
                        }

                        break;
                }
            }
            catch
            {
                // ignore parsing exceptions, fall back to null
            }

            return null;
        }

        private void PopulatePlaylistOwnershipDefaults(PlaylistInfo playlistInfo, JObject? playlistToken)
        {
            if (playlistInfo == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(playlistInfo.Id))
            {
                var idToken = playlistToken?["id"] ?? playlistToken?["playlistId"] ?? playlistToken?["resourceId"];
                if (idToken != null)
                {
                    playlistInfo.Id = idToken.ToString();
                }
            }

            long tokenCreatorId = playlistToken?["creator"]?["userId"]?.Value<long?>() ?? 0;
            long tokenOwnerId = playlistToken?["userId"]?.Value<long?>()
                ?? playlistToken?["ownerId"]?.Value<long?>()
                ?? 0;

            long currentUserId = GetCurrentUserId();

            if (playlistInfo.CreatorId == 0)
            {
                playlistInfo.CreatorId = tokenCreatorId != 0 ? tokenCreatorId : currentUserId;
            }

            if (playlistInfo.OwnerUserId == 0)
            {
                playlistInfo.OwnerUserId = tokenOwnerId != 0 ? tokenOwnerId : playlistInfo.CreatorId;
            }

            if (string.IsNullOrWhiteSpace(playlistInfo.Creator))
            {
                var creatorName = playlistToken?["creator"]?["nickname"]?.Value<string>()
                    ?? playlistToken?["creatorName"]?.Value<string>();

                if (!string.IsNullOrWhiteSpace(creatorName))
                {
                    playlistInfo.Creator = creatorName;
                }
                else
                {
                    var accountState = _authContext?.CurrentAccountState;
                    if (accountState != null &&
                        playlistInfo.CreatorId != 0 &&
                        long.TryParse(accountState.UserId, out var userId) &&
                        userId == playlistInfo.CreatorId &&
                        !string.IsNullOrWhiteSpace(accountState.Nickname))
                    {
                        playlistInfo.Creator = accountState.Nickname;
                    }
                }
            }

            if (playlistInfo.TrackCount < 0)
            {
                playlistInfo.TrackCount = 0;
            }
        }

        private static string ResolveAlbumArtistName(JToken? albumToken)
        {
            if (albumToken == null)
            {
                return string.Empty;
            }

            var names = new List<string>();

            void AddName(string? value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    names.Add(value.Trim());
                }
            }

            if (albumToken["artist"] is JObject artistObj)
            {
                AddName(artistObj["name"]?.Value<string>());
            }
            else
            {
                AddName(albumToken["artist"]?.Value<string>());
            }

            var artistsArray = albumToken["artists"] as JArray ?? albumToken["ar"] as JArray;
            if (artistsArray != null)
            {
                foreach (var artist in artistsArray.OfType<JToken>())
                {
                    AddName(artist["name"]?.Value<string>());
                }
            }

            AddName(albumToken["artistName"]?.Value<string>());
            AddName(albumToken["singerName"]?.Value<string>());

            if (names.Count == 0)
            {
                return string.Empty;
            }

            return string.Join("/", names.Distinct(StringComparer.OrdinalIgnoreCase));
        }

        private static int ResolveAlbumTrackCount(JToken? albumToken)
        {
            if (albumToken == null)
            {
                return 0;
            }

            var candidates = new[]
            {
                albumToken["trackCount"],
                albumToken["size"],
                albumToken["songCount"],
                albumToken["trackTotal"],
                albumToken["songs"]
            };

            foreach (var candidate in candidates)
            {
                if (candidate == null)
                {
                    continue;
                }

                if (candidate.Type == JTokenType.Integer || candidate.Type == JTokenType.Float)
                {
                    return Math.Max(0, candidate.Value<int>());
                }

                if (int.TryParse(candidate.Value<string>(), out var parsed))
                {
                    return Math.Max(0, parsed);
                }
            }

            if (albumToken["songs"] is JArray songsArray)
            {
                return songsArray.Count;
            }

            return 0;
        }

        private static string ResolveAlbumDescription(JToken? albumToken)
        {
            if (albumToken == null)
            {
                return string.Empty;
            }

            string? description =
                albumToken["description"]?.Value<string>() ??
                albumToken["briefDesc"]?.Value<string>() ??
                albumToken["desc"]?.Value<string>() ??
                albumToken["info"]?["introduction"]?.Value<string>() ??
                albumToken["intro"]?.Value<string>();

            return NormalizeSummary(description, maxLength: 200);
        }

        private static string FormatAlbumPublishDate(long? publishTime)
        {
            if (!publishTime.HasValue)
            {
                return string.Empty;
            }

            return FormatAlbumPublishDate(new Newtonsoft.Json.Linq.JValue(publishTime.Value));
        }

        private static string FormatAlbumPublishDate(JToken? publishToken)
        {
            if (publishToken == null || publishToken.Type == JTokenType.Null)
            {
                return string.Empty;
            }

            if (publishToken.Type == JTokenType.Integer || publishToken.Type == JTokenType.Float)
            {
                try
                {
                    long value = publishToken.Value<long>();
                    if (value <= 0)
                    {
                        return string.Empty;
                    }

                    return DateTimeOffset.FromUnixTimeMilliseconds(value).DateTime.ToString("yyyy-MM-dd");
                }
                catch
                {
                    return string.Empty;
                }
            }

            var text = publishToken.Value<string>();
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            if (DateTime.TryParse(text, out var parsed))
            {
                return parsed.ToString("yyyy-MM-dd");
            }

            text = text.Trim();
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericValue))
            {
                if (numericValue > 0)
                {
                    try
                    {
                        // 10 位时间戳按秒处理，其他情况按毫秒处理
                        return (text.Length == 10
                                ? DateTimeOffset.FromUnixTimeSeconds(numericValue)
                                : DateTimeOffset.FromUnixTimeMilliseconds(numericValue))
                            .DateTime.ToString("yyyy-MM-dd");
                    }
                    catch
                    {
                        // ignore and fall back to partial year parsing
                    }
                }
            }
            return text.Length >= 4 ? text.Substring(0, 4) : string.Empty;
        }

        private static int SanitizeDurationSeconds(long? durationValue)
        {
            if (!durationValue.HasValue)
            {
                return 0;
            }

            long raw = durationValue.Value;
            if (raw <= 0)
            {
                return 0;
            }

            // 大多数接口返回毫秒，若数值较大则按毫秒处理，否则视为秒数
            if (raw > 1000)
            {
                long seconds = raw / 1000;
                return (int)Math.Max(1, seconds);
            }

            return (int)raw;
        }

        /// <summary>
        /// 解析专辑列表
        /// </summary>
        private List<AlbumInfo> ParseAlbumList(JArray albums)
        {
            var result = new List<AlbumInfo>();
            if (albums == null) return result;

            foreach (var album in albums)
            {
                try
                {
                    var albumInfo = new AlbumInfo
                    {
                        Id = album["id"]?.Value<string>(),
                        Name = album["name"]?.Value<string>(),
                        PicUrl = album["picUrl"]?.Value<string>(),
                        Artist = ResolveAlbumArtistName(album),
                        TrackCount = ResolveAlbumTrackCount(album),
                        Description = ResolveAlbumDescription(album),
                        IsSubscribed = album["subscribed"]?.Value<bool?>() ?? album["isSub"]?.Value<bool?>() ?? false
                    };

                    albumInfo.PublishTime = FormatAlbumPublishDate(album["publishTime"]);

                    result.Add(albumInfo);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[AlbumParse] Failed to parse album entry: {ex.Message}");
                }
            }

            return result;
        }

        /// <summary>
        /// 解析歌手介绍段落。
        /// </summary>
        private List<ArtistIntroductionSection> ParseArtistIntroductionSections(JArray introductionArray)
        {
            var sections = new List<ArtistIntroductionSection>();
            if (introductionArray == null)
            {
                return sections;
            }

            foreach (var item in introductionArray)
            {
                if (item is not JObject section)
                {
                    continue;
                }

                var title = section["ti"]?.Value<string>() ?? string.Empty;
                var content = section["txt"]?.Value<string>() ?? string.Empty;

                if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(content))
                {
                    continue;
                }

                sections.Add(new ArtistIntroductionSection
                {
                    Title = title,
                    Content = content.Replace("\r\n", "\n").Trim()
                });
            }

            return sections;
        }

        private static string BuildIntroductionSummary(IEnumerable<ArtistIntroductionSection> sections, int maxLength = 320)
        {
            if (sections == null)
            {
                return string.Empty;
            }

            var printable = sections
                .Select(section =>
                {
                    if (string.IsNullOrWhiteSpace(section.Content))
                    {
                        return string.Empty;
                    }

                    if (string.IsNullOrWhiteSpace(section.Title))
                    {
                        return section.Content;
                    }

                    return $"{section.Title}\n{section.Content}";
                })
                .Where(s => !string.IsNullOrWhiteSpace(s));

            string combined = string.Join("\n\n", printable);
            if (string.IsNullOrWhiteSpace(combined))
            {
                return string.Empty;
            }

            combined = Regex.Replace(combined.Trim(), "\\s+", " ");
            return TrimToLength(combined, maxLength);
        }

        private static bool IsVipSong(JObject? song)
        {
            if (song == null)
            {
                return false;
            }

            int fee = song["fee"]?.Value<int?>() ?? 0;
            if (fee == 0)
            {
                var privilege = song["privilege"] as JObject;
                fee = privilege?["fee"]?.Value<int?>() ?? 0;
            }

            return fee == 1;
        }

        private static string NormalizeSummary(string? source, int maxLength = 140)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return string.Empty;
            }

            string condensed = Regex.Replace(source.Trim(), "\\s+", " ");
            return TrimToLength(condensed, maxLength);
        }

        private static string NormalizeDescription(string? description, string? fallback = null, int maxLength = 240)
        {
            string baseText = !string.IsNullOrWhiteSpace(description) ? description : fallback ?? string.Empty;
            if (string.IsNullOrWhiteSpace(baseText))
            {
                return string.Empty;
            }

            string condensed = Regex.Replace(baseText.Trim(), "\\s+", " ");
            return TrimToLength(condensed, maxLength);
        }

        private static string TrimToLength(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || maxLength <= 0)
            {
                return string.Empty;
            }

            if (value.Length <= maxLength)
            {
                return value;
            }

            string truncated = value.Substring(0, Math.Min(maxLength, value.Length)).TrimEnd();
            return truncated.Length < value.Length ? truncated + "…" : truncated;
        }

        /// <summary>
        /// 解析歌单详情
        /// </summary>
        private PlaylistInfo ParsePlaylistDetail(JObject playlist)
        {
            if (playlist == null)
            {
                return new PlaylistInfo();
            }

            var playlistInfo = CreatePlaylistInfo(playlist);
            PopulatePlaylistOwnershipDefaults(playlistInfo, playlist);

            var tracks = playlist["tracks"] as JArray;
            if (tracks != null && tracks.Count > 0)
            {
                var songs = ParseSongList(tracks);
                playlistInfo.Songs = songs;
                playlistInfo.TrackCount = Math.Max(playlistInfo.TrackCount, songs?.Count ?? 0);
            }
            else
            {
                var trackIds = playlist["trackIds"] as JArray;
                if (trackIds != null && trackIds.Count > 0 && playlistInfo.TrackCount <= 0)
                {
                    playlistInfo.TrackCount = Math.Max(playlistInfo.TrackCount, trackIds.Count);
                }
            }

            return playlistInfo;
        }

        private long GetCurrentUserId()
        {
            var accountState = _authContext?.CurrentAccountState;
            if (accountState == null)
            {
                return 0;
            }

            if (!string.IsNullOrWhiteSpace(accountState.UserId) &&
                long.TryParse(accountState.UserId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }

            if (accountState.AccountDetail?.UserId > 0)
            {
                return accountState.AccountDetail.UserId;
            }

            return 0;
        }

        private static string? ExtractPlaylistId(JObject? response)
        {
            if (response == null)
            {
                return null;
            }

            string? id =
                response["id"]?.ToString() ??
                response["playlistId"]?.ToString() ??
                response["resourceId"]?.ToString() ??
                response["data"]?["id"]?.ToString() ??
                response["result"]?["playlistId"]?.ToString() ??
                response["playlist"]?["id"]?.ToString();

            return string.IsNullOrWhiteSpace(id) ? null : id;
        }

        /// <summary>
        /// 解析歌词
        /// </summary>
        private LyricInfo ParseLyric(JObject lyricData)
        {
            if (lyricData == null) return null;

            return new LyricInfo
            {
                Lyric = lyricData["lrc"]?["lyric"]?.Value<string>(),
                TLyric = lyricData["tlyric"]?["lyric"]?.Value<string>(),
                RomaLyric = lyricData["romalrc"]?["lyric"]?.Value<string>(),
                YrcLyric = lyricData["yrc"]?["lyric"]?.Value<string>(),
                YTLyric = lyricData["ytlyric"]?["lyric"]?.Value<string>() ??
                          lyricData["ytlrc"]?["lyric"]?.Value<string>(),
                YRomaLyric = lyricData["yromalrc"]?["lyric"]?.Value<string>(),
                KLyric = lyricData["klyric"]?["lyric"]?.Value<string>()
            };
        }

        private static bool HasLyricContent(LyricInfo lyric)
        {
            if (lyric == null)
            {
                return false;
            }

            return !string.IsNullOrWhiteSpace(lyric.Lyric) ||
                   !string.IsNullOrWhiteSpace(lyric.TLyric) ||
                   !string.IsNullOrWhiteSpace(lyric.RomaLyric) ||
                   !string.IsNullOrWhiteSpace(lyric.YrcLyric) ||
                   !string.IsNullOrWhiteSpace(lyric.YTLyric) ||
                   !string.IsNullOrWhiteSpace(lyric.YRomaLyric) ||
                   !string.IsNullOrWhiteSpace(lyric.KLyric);
        }

        /// <summary>
        /// 解析评论
        /// </summary>
        private CommentResult ParseComments(JObject commentData, CommentSortType? preferredSortType)
        {
            var result = new CommentResult();

            if (commentData == null)
            {
                return result;
            }

            var data = commentData["data"] as JObject ?? commentData;
            result.TotalCount = data["totalCount"]?.Value<int>()
                ?? data["commentCount"]?.Value<int>()
                ?? data["size"]?.Value<int>()
                ?? result.TotalCount;

            result.HasMore = data["hasMore"]?.Value<bool>() ?? data["more"]?.Value<bool>() ?? false;
            result.PageNumber = data["pageNo"]?.Value<int>() ?? result.PageNumber;
            result.PageSize = data["pageSize"]?.Value<int>() ?? result.PageSize;
            result.Cursor = ExtractCursorValue(data["cursor"]) ?? result.Cursor;

            int apiSortType = data["sortType"]?.Value<int?>() ?? 0;
            if (apiSortType == 0 && preferredSortType.HasValue)
            {
                apiSortType = MapCommentSortType(preferredSortType.Value);
            }

            result.SortType = apiSortType switch
            {
                1 => CommentSortType.Recommend,
                2 => CommentSortType.Hot,
                3 => CommentSortType.Time,
                _ => preferredSortType ?? result.SortType
            };

            var commentIdSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<CommentInfo> ParseComments(JArray? items)
            {
                var parsedList = new List<CommentInfo>();
                if (items == null)
                {
                    return parsedList;
                }

                foreach (var comment in items)
                {
                    if (comment is not JObject commentObject)
                    {
                        continue;
                    }

                    var parsed = ParseCommentToken(commentObject);
                    if (parsed == null || string.IsNullOrWhiteSpace(parsed.CommentId))
                    {
                        continue;
                    }

                    if (commentIdSet.Add(parsed.CommentId))
                    {
                        parsedList.Add(parsed);
                    }
                }

                return parsedList;
            }

            var topList = ParseComments(data["topComments"] as JArray);
            var hotList = ParseComments(data["hotComments"] as JArray);
            var normalList = ParseComments(data["comments"] as JArray);

            if (apiSortType == 2)
            {
                hotList.Sort((left, right) =>
                {
                    int likedCompare = right.LikedCount.CompareTo(left.LikedCount);
                    if (likedCompare != 0)
                    {
                        return likedCompare;
                    }

                    int timeCompare = right.TimeMilliseconds.CompareTo(left.TimeMilliseconds);
                    if (timeCompare != 0)
                    {
                        return timeCompare;
                    }

                    return string.CompareOrdinal(left.CommentId, right.CommentId);
                });

                normalList.Sort((left, right) =>
                {
                    int likedCompare = right.LikedCount.CompareTo(left.LikedCount);
                    if (likedCompare != 0)
                    {
                        return likedCompare;
                    }

                    int timeCompare = right.TimeMilliseconds.CompareTo(left.TimeMilliseconds);
                    if (timeCompare != 0)
                    {
                        return timeCompare;
                    }

                    return string.CompareOrdinal(left.CommentId, right.CommentId);
                });
            }
            else if (apiSortType == 3)
            {
                normalList.Sort((left, right) =>
                {
                    int timeCompare = right.TimeMilliseconds.CompareTo(left.TimeMilliseconds);
                    if (timeCompare != 0)
                    {
                        return timeCompare;
                    }

                    return string.CompareOrdinal(left.CommentId, right.CommentId);
                });
            }

            result.Comments.AddRange(topList);
            if (apiSortType == 2)
            {
                result.Comments.AddRange(hotList);
            }
            result.Comments.AddRange(normalList);

            return result;
        }

        private List<PodcastCategoryInfo> ParsePodcastCategoryList(JArray? categories)
        {
            var result = new List<PodcastCategoryInfo>();
            if (categories == null)
            {
                return result;
            }

            foreach (var cat in categories)
            {
                if (cat is JObject obj)
                {
                    var id = obj["id"]?.Value<int>() ?? obj["categoryId"]?.Value<int>() ?? 0;
                    var name = obj["name"]?.Value<string>() ?? obj["categoryName"]?.Value<string>() ?? string.Empty;
                    if (id <= 0 || string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    result.Add(new PodcastCategoryInfo
                    {
                        Id = id,
                        Name = name,
                        CategoryType = obj["type"]?.Value<int?>() ?? obj["categoryType"]?.Value<int?>(),
                        PicUrl = obj["pic56x56Url"]?.Value<string>()
                                  ?? obj["pic84x84Url"]?.Value<string>()
                                  ?? obj["pic96x96Url"]?.Value<string>()
                                  ?? obj["pic120x120Url"]?.Value<string>()
                                  ?? obj["picUrl"]?.Value<string>() ?? string.Empty
                    });
                }
            }

            return result;
        }

        private List<PodcastRadioInfo> ParsePodcastRadioList(JArray? radios)
        {
            var result = new List<PodcastRadioInfo>();
            if (radios == null)
            {
                return result;
            }

            foreach (var radioToken in radios)
            {
                var radio = ParsePodcastRadio(radioToken);
                if (radio != null)
                {
                    result.Add(radio);
                }
            }

            return result;
        }

        private PodcastRadioInfo? ParsePodcastRadio(JToken? radioToken)
        {
            if (radioToken == null || radioToken.Type != JTokenType.Object)
            {
                return null;
            }

            var obj = (JObject)radioToken;
            var radio = new PodcastRadioInfo
            {
                Id = obj["id"]?.Value<long>() ?? obj["id"]?.Value<int>() ?? 0,
                Name = obj["name"]?.Value<string>() ?? string.Empty,
                DjName = obj["dj"]?["nickname"]?.Value<string>() ?? obj["dj"]?["name"]?.Value<string>() ?? string.Empty,
                DjUserId = obj["dj"]?["userId"]?.Value<long>() ?? obj["dj"]?["id"]?.Value<long>() ?? 0,
                Category = obj["category"]?.Value<string>() ?? obj["categoryId"]?.Value<string>() ?? string.Empty,
                SecondCategory = obj["secondCategory"]?.Value<string>() ?? obj["secondCategoryId"]?.Value<string>() ?? string.Empty,
                Description = obj["desc"]?.Value<string>() ?? obj["description"]?.Value<string>() ?? string.Empty,
                CoverUrl = obj["picUrl"]?.Value<string>() ?? obj["coverUrl"]?.Value<string>() ?? string.Empty,
                ProgramCount = obj["programCount"]?.Value<int>() ?? 0,
                SubscriberCount = obj["subCount"]?.Value<int>() ?? 0,
                ShareCount = obj["shareCount"]?.Value<int>() ?? 0,
                LikedCount = obj["likedCount"]?.Value<int>() ?? 0,
                CommentCount = obj["commentCount"]?.Value<int>() ?? 0,
                RadioFeeType = obj["radioFeeType"]?.Value<int>() ?? obj["feeType"]?.Value<int>() ?? 0,
                Subscribed = obj["subed"]?.Value<bool>() ?? (obj["subed"]?.Value<int>() == 1)
            };

            long createTime = obj["createTime"]?.Value<long>() ?? 0;
            if (createTime > 0)
            {
                radio.CreateTime = DateTimeOffset.FromUnixTimeMilliseconds(createTime).LocalDateTime;
            }

            return radio;
        }

        private List<PodcastEpisodeInfo> ParsePodcastEpisodeList(JArray? programs)
        {
            var list = new List<PodcastEpisodeInfo>();
            if (programs == null)
            {
                return list;
            }

            foreach (var token in programs)
            {
                if (token is JObject programObj)
                {
                    var episode = ParsePodcastEpisode(programObj);
                    if (episode != null)
                    {
                        list.Add(episode);
                    }
                }
            }

            return list;
        }

        private PodcastEpisodeInfo? ParsePodcastEpisode(JObject program)
        {
            long programId = program["id"]?.Value<long>() ?? program["programId"]?.Value<long>() ?? 0;
            var episode = new PodcastEpisodeInfo
            {
                ProgramId = programId,
                Name = program["name"]?.Value<string>() ?? "节目",
                Description = program["description"]?.Value<string>() ?? program["intro"]?.Value<string>() ?? string.Empty,
                RadioId = program["radioId"]?.Value<long>() ?? program["radio"]?["id"]?.Value<long>() ?? 0,
                RadioName = program["radio"]?["name"]?.Value<string>() ?? program["radioName"]?.Value<string>() ?? string.Empty,
                DjUserId = program["dj"]?["userId"]?.Value<long>() ?? program["dj"]?["id"]?.Value<long>() ?? 0,
                DjName = program["dj"]?["nickname"]?.Value<string>() ?? program["dj"]?["name"]?.Value<string>() ?? string.Empty,
                ListenerCount = program["listenerCount"]?.Value<int>() ?? program["adjustedPlayCount"]?.Value<int>() ?? 0,
                LikedCount = program["likedCount"]?.Value<int>() ?? 0,
                CommentCount = program["commentCount"]?.Value<int>() ?? 0,
                ShareCount = program["shareCount"]?.Value<int>() ?? 0,
                SerialNumber = program["serialNum"]?.Value<int>() ?? program["serialNo"]?.Value<int>() ?? 0,
                IsPaid = program["programFeeType"]?.Value<int>() == 1 || program["fee"]?.Value<int>() == 1,
                CoverUrl = program["coverUrl"]?.Value<string>() ?? program["blurCoverUrl"]?.Value<string>() ?? string.Empty
            };

            long publishTime = program["createTime"]?.Value<long>() ?? program["pubTime"]?.Value<long>() ?? 0;
            if (publishTime > 0)
            {
                episode.PublishTime = DateTimeOffset.FromUnixTimeMilliseconds(publishTime).LocalDateTime;
            }

            long durationMs = program["duration"]?.Value<long>() ?? program["trackTime"]?.Value<long>() ?? 0;
            if (durationMs > 0)
            {
                episode.Duration = TimeSpan.FromMilliseconds(durationMs);
            }

            var mainSong = program["mainSong"] as JObject ?? program["song"] as JObject;
            SongInfo? song = null;
            if (mainSong != null)
            {
                var songs = ParseSongList(new JArray(mainSong));
                song = songs.FirstOrDefault();
            }

            if (song == null)
            {
                song = new SongInfo
                {
                    Id = episode.ProgramId > 0 ? $"program_{episode.ProgramId}" : Guid.NewGuid().ToString("N"),
                    Name = episode.Name,
                    Artist = string.IsNullOrWhiteSpace(episode.DjName) ? episode.RadioName : episode.DjName,
                    Album = episode.RadioName,
                    Duration = episode.Duration.TotalSeconds > 0 ? (int)episode.Duration.TotalSeconds : 0,
                    PicUrl = episode.CoverUrl,
                    IsAvailable = true
                };
            }
            else if (song.Duration <= 0 && episode.Duration.TotalSeconds > 0)
            {
                song.Duration = (int)episode.Duration.TotalSeconds;
            }

            if (string.IsNullOrWhiteSpace(song.PicUrl) && !string.IsNullOrWhiteSpace(episode.CoverUrl))
            {
                song.PicUrl = episode.CoverUrl;
            }

            song.IsPodcastEpisode = true;
            song.PodcastProgramId = episode.ProgramId;
            song.PodcastRadioId = episode.RadioId;
            song.PodcastRadioName = episode.RadioName;
            song.PodcastDjName = episode.DjName;
            song.PodcastPublishTime = episode.PublishTime;
            song.PodcastEpisodeDescription = episode.Description;
            song.PodcastSerialNumber = episode.SerialNumber;

            episode.Song = song;

            return episode;
        }


        private CommentFloorResult ParseCommentFloor(JObject commentData, string parentCommentId)
        {
            var result = new CommentFloorResult
            {
                ParentCommentId = parentCommentId
            };

            if (commentData == null)
            {
                return result;
            }

            try
            {
                var data = commentData["data"] as JObject;
                if (data == null)
                {
                    return result;
                }

                result.ParentCommentId = ConvertToStringId(data["parentCommentId"]) ?? result.ParentCommentId;
                result.TotalCount = data["totalCount"]?.Value<int>()
                    ?? data["commentCount"]?.Value<int>()
                    ?? result.TotalCount;
                result.HasMore = data["hasMore"]?.Value<bool>() ?? false;
                result.NextTime = data["time"]?.Value<long?>();

                var comments = data["comments"] as JArray;
                if (comments != null)
                {
                    foreach (var comment in comments)
                    {
                        if (comment is JObject commentObject)
                        {
                            var parsed = ParseCommentToken(commentObject, result.ParentCommentId);
                            if (parsed != null)
                            {
                                result.Comments.Add(parsed);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 解析楼层评论失败: {ex.Message}");
            }

            return result;
        }

        private CommentInfo? ParseCommentToken(JObject? commentObject, string? parentCommentId = null)
        {
            if (commentObject == null)
            {
                return null;
            }

            try
            {
                string? commentId = ConvertToStringId(commentObject["commentId"]);
                if (string.IsNullOrWhiteSpace(commentId))
                {
                    return null;
                }

                var user = commentObject["user"] as JObject;
                var commentInfo = new CommentInfo
                {
                    CommentId = commentId,
                    UserId = ConvertToStringId(user?["userId"]) ?? string.Empty,
                    UserName = user?["nickname"]?.Value<string>() ?? string.Empty,
                    AvatarUrl = user?["avatarUrl"]?.Value<string>() ?? string.Empty,
                    Content = commentObject["content"]?.Value<string>() ?? string.Empty,
                    LikedCount = commentObject["likedCount"]?.Value<int>() ?? 0,
                    Liked = commentObject["liked"]?.Value<bool>() ?? false,
                    IpLocation = ExtractIpLocation(commentObject["ipLocation"]),
                    ParentCommentId = NormalizeParentCommentId(commentObject["parentCommentId"], parentCommentId)
                };

                var timeValue = commentObject["time"]?.Value<long?>();
                if (timeValue.HasValue)
                {
                    commentInfo.TimeMilliseconds = timeValue.Value;
                    commentInfo.Time = DateTimeOffset.FromUnixTimeMilliseconds(timeValue.Value).LocalDateTime;
                }

                var beReplied = commentObject["beReplied"] as JArray;
                if (beReplied != null && beReplied.Count > 0)
                {
                    var firstReply = beReplied[0];
                    if (firstReply is JObject replyObject)
                    {
                        commentInfo.BeRepliedId = ConvertToStringId(replyObject["beRepliedCommentId"]);
                        var replyUser = replyObject["user"] as JObject;
                        commentInfo.BeRepliedUserName = replyUser?["nickname"]?.Value<string>();
                    }
                    else
                    {
                        commentInfo.BeRepliedId = ConvertToStringId(firstReply);
                    }
                }

                int replyCount = commentObject["replyCount"]?.Value<int>() ?? 0;

                var showFloorComment = commentObject["showFloorComment"] as JObject;
                if (showFloorComment != null)
                {
                    replyCount = Math.Max(replyCount, showFloorComment["replyCount"]?.Value<int>() ?? 0);
                    var floorComments = showFloorComment["comments"] as JArray;
                    if (floorComments != null)
                    {
                        foreach (var reply in floorComments)
                        {
                            if (reply is JObject replyObject)
                            {
                                var replyInfo = ParseCommentToken(replyObject, commentInfo.CommentId);
                                if (replyInfo != null)
                                {
                                    commentInfo.Replies.Add(replyInfo);
                                }
                            }
                        }
                    }
                }

                commentInfo.ReplyCount = replyCount > 0
                    ? replyCount
                    : commentInfo.Replies.Count;

                return commentInfo;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 解析评论失败: {ex.Message}");
                return null;
            }
        }

        private static string ExtractIpLocation(JToken? token)
        {
            if (token == null)
            {
                return string.Empty;
            }

            if (token.Type == JTokenType.Object)
            {
                return token["location"]?.Value<string>()
                    ?? token["text"]?.Value<string>()
                    ?? token["value"]?.Value<string>()
                    ?? string.Empty;
            }

            return token.Value<string>() ?? string.Empty;
        }

        private static string? NormalizeParentCommentId(JToken? token, string? fallback)
        {
            var id = ConvertToStringId(token);
            if (string.IsNullOrWhiteSpace(id) || id == "0")
            {
                return fallback;
            }

            return id;
        }

        private static string? ConvertToStringId(JToken? token)
        {
            if (token == null)
            {
                return null;
            }

            var longValue = token.Value<long?>();
            if (longValue.HasValue && longValue.Value > 0)
            {
                return longValue.Value.ToString();
            }

            var stringValue = token.Value<string>();
            if (!string.IsNullOrWhiteSpace(stringValue))
            {
                return stringValue;
            }

            return null;
        }

        private static string? ExtractCursorValue(JToken? token)
        {
            if (token == null)
            {
                return null;
            }

            if (token.Type == JTokenType.String)
            {
                return token.Value<string>();
            }

            if (token.Type == JTokenType.Integer)
            {
                return token.Value<long?>()?.ToString(CultureInfo.InvariantCulture);
            }

            if (token.Type == JTokenType.Float)
            {
                return token.Value<double?>()?.ToString(CultureInfo.InvariantCulture);
            }

            if (token.Type == JTokenType.Object)
            {
                return token["cursor"]?.Value<string>()
                    ?? token["value"]?.Value<string>()
                    ?? token["text"]?.Value<string>();
            }

            return token.ToString();
        }

        #endregion

    }
}
