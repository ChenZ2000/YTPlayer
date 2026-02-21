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
        #region 推荐和个性化

        /// <summary>
        /// 获取用户账号信息
        /// 参考: NeteaseCloudMusicApi/module/user_account.js
        /// </summary>
        public async Task<UserAccountInfo> GetUserAccountAsync()
        {
            try
            {
                var payload = new Dictionary<string, object>();

                var response = await PostWeApiAsync<JObject>("/nuser/account/get", payload);

                // ⭐ 修复：添加更详细的错误检查
                if (response == null)
                {
                    System.Diagnostics.Debug.WriteLine("[GetUserAccountAsync] 获取用户信息失败: 响应为空");
                    return new UserAccountInfo();
                }

                int code = response["code"]?.Value<int>() ?? -1;
                if (code != 200)
                {
                    string message = response["message"]?.Value<string>() ?? "未知错误";
                    System.Diagnostics.Debug.WriteLine($"[GetUserAccountAsync] 获取用户信息失败: code={code}, message={message}");
                    return new UserAccountInfo();
                }

                // 调试：输出完整的响应数据
                System.Diagnostics.Debug.WriteLine("[GetUserAccountAsync] 完整响应:");
                System.Diagnostics.Debug.WriteLine(response.ToString(Newtonsoft.Json.Formatting.Indented));

                var profile = response["profile"];
                var account = response["account"];

                // ⭐ 修复：添加 null 检查并抛出异常
                if (profile == null)
                {
                    System.Diagnostics.Debug.WriteLine("[GetUserAccountAsync] 获取用户信息失败: profile 字段为空");
                    return new UserAccountInfo();
                }

            // 从 account 字段获取 VIP 信息
            int vipType = 0;
            if (account != null)
            {
                vipType = account["vipType"]?.Value<int>() ?? 0;
                System.Diagnostics.Debug.WriteLine($"[GetUserAccountAsync] VIP类型(从account): {vipType}");
            }

            // 如果 account 中没有，尝试从 profile 获取
            if (vipType == 0)
            {
                vipType = profile["vipType"]?.Value<int>() ?? 0;
                System.Diagnostics.Debug.WriteLine($"[GetUserAccountAsync] VIP类型(从profile): {vipType}");
            }

            // 单独获取用户等级（EAPI 优先）
            int level = 0;
            try
            {
                var levelPayload = new Dictionary<string, object>();
                var levelResponse = await PostEApiAsync<JObject>("/api/user/level", levelPayload, useIosHeaders: true, skipErrorHandling: true).ConfigureAwait(false);
                if (levelResponse?["code"]?.Value<int>() == 200)
                {
                    var data = levelResponse["data"];
                    if (data != null)
                    {
                        level = data["level"]?.Value<int>() ?? 0;
                    }
                    else
                    {
                        level = levelResponse["level"]?.Value<int>() ?? 0;
                    }
                    System.Diagnostics.Debug.WriteLine($"[GetUserAccountAsync] 用户等级(从/api/user/level): {level}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GetUserAccountAsync] 获取用户等级失败: {ex.Message}");
                level = profile["level"]?.Value<int>() ?? 0;
            }

            // 获取生日和创建时间（修复时区问题：使用本地时间而不是UTC）
            DateTime? birthday = null;
            if (profile["birthday"] != null)
            {
                long birthdayTimestamp = profile["birthday"].Value<long>();
                if (birthdayTimestamp > 0)
                {
                    // 使用 ToLocalTime() 修复时区问题
                    birthday = DateTimeOffset.FromUnixTimeMilliseconds(birthdayTimestamp).LocalDateTime;
                    System.Diagnostics.Debug.WriteLine($"[GetUserAccountAsync] 生日时间戳: {birthdayTimestamp}, 转换后: {birthday}");
                }
            }

            DateTime? createTime = null;
            if (profile["createTime"] != null)
            {
                long createTimestamp = profile["createTime"].Value<long>();
                if (createTimestamp > 0)
                {
                    createTime = DateTimeOffset.FromUnixTimeMilliseconds(createTimestamp).LocalDateTime;
                }
            }

            // 获取统计信息（粉丝、关注、动态、听歌数）和额外信息 - 需要单独调用 user/detail API
            // 参考: NeteaseCloudMusicApi/module/user_detail.js
            int followers = 0;
            int follows = 0;
            int eventCount = 0;
            int listenSongs = 0;
            string artistName = null;
            long? artistId = null;
            int userType = 0;
            int playlistCount = 0;
            int playlistBeSubscribedCount = 0;
            int createDays = 0;
            string authTypeDesc = null;
            int djProgramCount = 0;
            bool inBlacklist = false;

            try
            {
                long userId = profile["userId"]?.Value<long>() ?? 0;
                if (userId > 0)
                {
                    var detailResponse = await PostWeApiAsync<JObject>($"/v1/user/detail/{userId}", new Dictionary<string, object>());
                    System.Diagnostics.Debug.WriteLine($"[GetUserAccountAsync] user/detail API 响应:");
                    System.Diagnostics.Debug.WriteLine(detailResponse.ToString(Newtonsoft.Json.Formatting.Indented));

                    if (detailResponse["code"]?.Value<int>() == 200)
                    {
                        var detailProfile = detailResponse["profile"];
                        if (detailProfile != null)
                        {
                            // 统计信息
                            followers = detailProfile["followeds"]?.Value<int>() ?? 0;
                            follows = detailProfile["follows"]?.Value<int>() ?? 0;
                            eventCount = detailProfile["eventCount"]?.Value<int>() ?? 0;

                            // 额外信息
                            artistName = detailProfile["artistName"]?.Value<string>();
                            artistId = detailProfile["artistId"]?.Value<long>();
                            userType = detailProfile["userType"]?.Value<int>() ?? 0;
                            playlistCount = detailProfile["playlistCount"]?.Value<int>() ?? 0;
                            playlistBeSubscribedCount = detailProfile["playlistBeSubscribedCount"]?.Value<int>() ?? 0;
                            djProgramCount = detailProfile["sDJPCount"]?.Value<int>() ?? 0;
                            inBlacklist = detailProfile["inBlacklist"]?.Value<bool>() ?? false;

                            // 解析认证类型
                            var allAuthTypes = detailProfile["allAuthTypes"];
                            if (allAuthTypes != null && allAuthTypes.HasValues)
                            {
                                try
                                {
                                    var authList = new System.Collections.Generic.List<string>();
                                    foreach (var authType in allAuthTypes)
                                    {
                                        string desc = authType["desc"]?.Value<string>();
                                        var tags = authType["tags"] as Newtonsoft.Json.Linq.JArray;
                                        if (!string.IsNullOrEmpty(desc))
                                        {
                                            if (tags != null && tags.Count > 0)
                                            {
                                                authList.Add($"{desc}（{string.Join("、", tags.Select(t => t.Value<string>()))}）");
                                            }
                                            else
                                            {
                                                authList.Add(desc);
                                            }
                                        }
                                    }
                                    if (authList.Count > 0)
                                    {
                                        authTypeDesc = string.Join("；", authList);
                                    }
                                }
                                catch
                                {
                                    // 解析失败，忽略
                                }
                            }
                        }

                        // 注意：listenSongs 和 createDays 在 API 响应的顶层，不在 profile 里！
                        listenSongs = detailResponse["listenSongs"]?.Value<int>() ?? 0;
                        createDays = detailResponse["createDays"]?.Value<int>() ?? 0;

                        System.Diagnostics.Debug.WriteLine($"[GetUserAccountAsync] 从 user/detail 获取统计: 粉丝={followers}, 关注={follows}, 动态={eventCount}, 听歌数={listenSongs}");
                        System.Diagnostics.Debug.WriteLine($"[GetUserAccountAsync] 额外信息: 艺人名={artistName}, 用户类型={userType}, 歌单数={playlistCount}, 注册天数={createDays}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GetUserAccountAsync] 获取统计信息失败: {ex.Message}");
                // 继续执行，使用默认值 0
            }

            var userInfo = new UserAccountInfo
            {
                UserId = profile["userId"]?.Value<long>() ?? 0,
                Nickname = profile["nickname"]?.Value<string>(),
                AvatarUrl = profile["avatarUrl"]?.Value<string>(),
                Signature = profile["signature"]?.Value<string>(),
                VipType = vipType,
                Level = level,
                Gender = profile["gender"]?.Value<int>() ?? 0,
                Province = profile["province"]?.Value<int>() ?? 0,
                City = profile["city"]?.Value<int>() ?? 0,
                ListenSongs = listenSongs,
                Followers = followers,
                Follows = follows,
                EventCount = eventCount,
                Birthday = birthday,
                CreateTime = createTime,
                ArtistName = artistName,
                ArtistId = artistId,
                UserType = userType,
                PlaylistCount = playlistCount,
                PlaylistBeSubscribedCount = playlistBeSubscribedCount,
                CreateDays = createDays,
                AuthTypeDesc = authTypeDesc,
                DjProgramCount = djProgramCount,
                InBlacklist = inBlacklist
            };

            System.Diagnostics.Debug.WriteLine($"[GetUserAccountAsync] 最终解析结果: 昵称={userInfo.Nickname}, VIP={userInfo.VipType}, 等级={userInfo.Level}");
            System.Diagnostics.Debug.WriteLine($"[GetUserAccountAsync] 最终统计信息: 粉丝={userInfo.Followers}, 关注={userInfo.Follows}, 动态={userInfo.EventCount}, 听歌数={userInfo.ListenSongs}");

            return userInfo;
            }
            catch (Exception ex)
            {
                // ⭐ 修复：记录完整错误信息并返回空对象，避免界面异常
                System.Diagnostics.Debug.WriteLine($"[GetUserAccountAsync] 失败: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[GetUserAccountAsync] 异常类型: {ex.GetType().Name}");
                System.Diagnostics.Debug.WriteLine($"[GetUserAccountAsync] 堆栈跟踪: {ex.StackTrace}");
                return new UserAccountInfo();
            }
        }


        /// <summary>
        /// 获取每日推荐歌单
        /// 参考: NeteaseCloudMusicApi/module/recommend_resource.js
        /// </summary>
        public async Task<List<PlaylistInfo>> GetDailyRecommendPlaylistsAsync()
        {
            var payload = new Dictionary<string, object>();

            var response = await PostWeApiAsync<JObject>("/v1/discovery/recommend/resource", payload);

            if (response["code"]?.Value<int>() != 200)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 获取每日推荐歌单失败: {response["message"]}");
                return new List<PlaylistInfo>();
            }

            var recommend = response["recommend"] as JArray;
            return ParsePlaylistList(recommend);
        }

        /// <summary>
        /// 获取每日推荐歌曲
        /// 参考: NeteaseCloudMusicApi/module/recommend_songs.js
        /// 注意: 需要设置 os = "ios"
        /// </summary>
        public async Task<List<SongInfo>> GetDailyRecommendSongsAsync()
        {
            // 创建临时cookies，设置os为ios (这是关键!)
            var tempCookies = new Dictionary<string, string>(_cookieContainer.GetCookies(new Uri(OFFICIAL_API_BASE))
                .Cast<Cookie>()
                .ToDictionary(c => c.Name, c => c.Value))
            {
                ["os"] = "ios"
            };

            var payload = new Dictionary<string, object>();

            // 构造Cookie header
            string cookieHeader = string.Join("; ", tempCookies.Select(kvp => $"{kvp.Key}={kvp.Value}"));

            // 手动发送请求
            var encrypted = EncryptionHelper.EncryptWeapi(JsonConvert.SerializeObject(payload));

            var formData = new Dictionary<string, string>
            {
                { "params", encrypted.Params },
                { "encSecKey", encrypted.EncSecKey }
            };
            var content = new FormUrlEncodedContent(formData);

            var request = new HttpRequestMessage(HttpMethod.Post, $"{OFFICIAL_API_BASE}/api/v3/discovery/recommend/songs")
            {
                Content = content
            };

            request.Headers.Add("Cookie", cookieHeader);
            request.Headers.Add("User-Agent", _desktopUserAgent ?? USER_AGENT);
            request.Headers.Add("Referer", REFERER);

            var httpResponse = await _httpClient.SendAsync(request);
            string responseText = await httpResponse.Content.ReadAsStringAsync();

            var response = JObject.Parse(responseText);

            if (response["code"]?.Value<int>() != 200)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 获取每日推荐歌曲失败: {response["message"]}");
                return new List<SongInfo>();
            }

            var data = response["data"];
            var dailySongs = data?["dailySongs"] as JArray;

            return ParseSongList(dailySongs);
        }

        /// <summary>
        /// 获取个性化推荐歌单
        /// 参考: NeteaseCloudMusicApi/module/personalized.js
        /// </summary>
        public async Task<List<PlaylistInfo>> GetPersonalizedPlaylistsAsync(int limit = 30)
        {
            var payload = new Dictionary<string, object>
            {
                { "limit", limit },
                { "total", true },
                { "n", 1000 }
            };

            var response = await PostWeApiAsync<JObject>("/personalized/playlist", payload);

            if (response["code"]?.Value<int>() != 200)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 获取个性化推荐失败: {response["message"]}");
                return new List<PlaylistInfo>();
            }

            var result = response["result"] as JArray;
            return ParsePlaylistList(result);
        }

        /// <summary>
        /// 获取私人FM歌曲 (私人雷达)
        /// 参考: NeteaseCloudMusicApi/module/personal_fm.js
        /// </summary>
        public async Task<List<SongInfo>> GetPersonalFMAsync()
        {
            var payload = new Dictionary<string, object>();

            var response = await PostWeApiAsync<JObject>("/v1/radio/get", payload);

            if (response["code"]?.Value<int>() != 200)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 获取私人FM失败: {response["message"]}");
                return new List<SongInfo>();
            }

            var data = response["data"] as JArray;
            return ParseSongList(data);
        }

        /// <summary>
        /// 获取用户歌单（包括创建和收藏的歌单）
        /// 参考: NeteaseCloudMusicApi/module/user_playlist.js
        /// </summary>
        public async Task<(List<PlaylistInfo>, int)> GetUserPlaylistsAsync(long userId, int limit = 1000, int offset = 0)
        {
            var payload = new Dictionary<string, object>
            {
                { "uid", userId },
                { "limit", limit },
                { "offset", offset },
                { "includeVideo", true }
            };

            var response = await PostWeApiAsync<JObject>("/user/playlist", payload);

            if (response["code"]?.Value<int>() != 200)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 获取用户歌单失败: {response["message"]}");
                return (new List<PlaylistInfo>(), 0);
            }

            var playlists = response["playlist"] as JArray;

            // 尝试从响应中解析总数，检查常见的字段名
            int totalCount = 0;
            if (response["total"] != null)
            {
                totalCount = response["total"].Value<int>();
                System.Diagnostics.Debug.WriteLine($"[API] 用户歌单总数(total): {totalCount}");
            }
            else if (response["count"] != null)
            {
                totalCount = response["count"].Value<int>();
                System.Diagnostics.Debug.WriteLine($"[API] 用户歌单总数(count): {totalCount}");
            }
            else if (playlists != null)
            {
                // 如果API不返回总数，使用当前获取的数量
                totalCount = playlists.Count;
                System.Diagnostics.Debug.WriteLine($"[API] 用户歌单数量(从列表计算): {totalCount}");
            }

            var parsed = ParsePlaylistList(playlists) ?? new List<PlaylistInfo>();
            if (parsed.Count > 0)
            {
                foreach (var playlist in parsed)
                {
                    if (playlist == null)
                    {
                        continue;
                    }

                    bool isOwned = playlist.CreatorId == userId || playlist.OwnerUserId == userId;
                    if (!isOwned && !playlist.IsSubscribed)
                    {
                        playlist.IsSubscribed = true;
                    }
                }
            }

            return (parsed, totalCount);
        }

        /// <summary>
        /// 获取所有排行榜
        /// 参考: NeteaseCloudMusicApi/module/toplist.js
        /// </summary>
        public async Task<List<PlaylistInfo>> GetToplistAsync()
        {
            var payload = new Dictionary<string, object>();

            var response = await PostWeApiAsync<JObject>("/toplist", payload);

            if (response["code"]?.Value<int>() != 200)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 获取排行榜失败: {response["message"]}");
                return new List<PlaylistInfo>();
            }

            var list = response["list"] as JArray;
            if (list == null)
            {
                return new List<PlaylistInfo>();
            }

            var result = new List<PlaylistInfo>();
            foreach (var item in list)
            {
                var playlist = new PlaylistInfo
                {
                    Id = item["id"]?.Value<string>(),
                    Name = item["name"]?.Value<string>(),
                    CoverUrl = item["coverImgUrl"]?.Value<string>(),
                    Description = item["description"]?.Value<string>(),
                    TrackCount = item["trackCount"]?.Value<int>() ?? 0
                };

                result.Add(playlist);
            }

            return result;
        }

        /// <summary>
        /// 获取用户喜欢的歌曲列表
        /// 参考: NeteaseCloudMusicApi/module/likelist.js
        /// </summary>
        public async Task<List<string>> GetUserLikedSongsAsync(long userId)
        {
            // ⭐ 调试信息：检查登录状态
            System.Diagnostics.Debug.WriteLine($"[GetUserLikedSongs] 开始获取喜欢的歌曲");
            System.Diagnostics.Debug.WriteLine($"[GetUserLikedSongs] UserId={userId}");
            System.Diagnostics.Debug.WriteLine($"[GetUserLikedSongs] UsePersonalCookie={UsePersonalCookie}");
            System.Diagnostics.Debug.WriteLine($"[GetUserLikedSongs] MUSIC_U={(string.IsNullOrEmpty(_musicU) ? "未设置" : $"已设置(长度:{_musicU.Length})")}");
            System.Diagnostics.Debug.WriteLine($"[GetUserLikedSongs] CSRF={(string.IsNullOrEmpty(_csrfToken) ? "未设置" : "已设置")}");

            var payload = new Dictionary<string, object>
            {
                { "uid", userId }
            };

            var response = await PostWeApiAsync<JObject>("/song/like/get", payload);

            if (response["code"]?.Value<int>() != 200)
            {
                int code = response["code"]?.Value<int>() ?? -1;
                string message = response["message"]?.Value<string>() ?? response["msg"]?.Value<string>() ?? "未知错误";
                System.Diagnostics.Debug.WriteLine($"[API] 获取喜欢的歌曲失败: code={code}, message={message}");
                System.Diagnostics.Debug.WriteLine($"[API] 完整响应: {response.ToString()}");
                return new List<string>();
            }

            var ids = response["ids"] as JArray;
            if (ids == null)
            {
                return new List<string>();
            }

            return ids
                .Select(id => id.Value<string>())
                .Where(id => !string.IsNullOrEmpty(id))
                .Select(id => id!)
                .ToList();
        }

        /// <summary>
        /// 获取最近播放的歌曲
        /// 参考: NeteaseCloudMusicApi/module/record_recent_song.js
        /// </summary>
        /// <param name="limit">返回数量，默认100</param>
        /// <returns>最近播放的歌曲列表</returns>
        public async Task<List<SongInfo>> GetRecentPlayedSongsAsync(int limit = 100)
        {
            System.Diagnostics.Debug.WriteLine($"[GetRecentPlayedSongs] 开始获取最近播放歌曲, limit={limit}");

            var payload = new Dictionary<string, object>
            {
                { "limit", limit }
            };

            try
            {
                // 注意：该接口位于 /api 前缀，需保持原始路径
                var response = await PostWeApiAsync<JObject>(
                    "/api/play-record/song/list",
                    payload,
                    autoConvertApiSegment: true);

                if (response["code"]?.Value<int>() != 200)
                {
                    int code = response["code"]?.Value<int>() ?? -1;
                    string message = response["message"]?.Value<string>() ?? response["msg"]?.Value<string>() ?? "未知错误";
                    System.Diagnostics.Debug.WriteLine($"[API] 获取最近播放歌曲失败: code={code}, message={message}");
                    return new List<SongInfo>();
                }

                var data = response["data"]?["list"] as JArray;
                if (data == null)
                {
                    // 尝试直接从 data 字段获取
                    data = response["data"] as JArray;
                }

                if (data == null)
                {
                    System.Diagnostics.Debug.WriteLine("[API] 最近播放歌曲数据为空");
                    return new List<SongInfo>();
                }

                var songs = new List<SongInfo>();
                int skippedNonSongEntries = 0;
                foreach (var item in data)
                {
                    if (IsRecentSongEntryPodcastOrVoice(item))
                    {
                        skippedNonSongEntries++;
                        continue;
                    }

                    // 提取歌曲数据（可能在 data 或 song 字段中）
                    var songData = item["data"] ?? item["song"] ?? item;

                    if (songData == null) continue;

                    var song = new SongInfo
                    {
                        Id = songData["id"]?.Value<string>() ?? songData["id"]?.Value<long>().ToString(),
                        Name = songData["name"]?.Value<string>() ?? "未知歌曲",
                        Artist = string.Join("/",
                            (songData["artists"] ?? songData["ar"])?.Select(a => a["name"]?.Value<string>()).Where(n => !string.IsNullOrWhiteSpace(n))
                            ?? new[] { "未知艺术家" }),
                        Album = (songData["album"] ?? songData["al"])?["name"]?.Value<string>() ?? "未知专辑",
                        AlbumId = (songData["album"] ?? songData["al"])?["id"]?.Value<string>()
                            ?? (songData["album"] ?? songData["al"])?["id"]?.Value<long>().ToString(),
                        Duration = SanitizeDurationSeconds(songData["duration"]?.Value<long>() ?? songData["dt"]?.Value<long>()),
                        PicUrl = (songData["album"] ?? songData["al"])?["picUrl"]?.Value<string>() ?? ""
                    };

                    var recentArtists = songData["artists"] as JArray ?? songData["ar"] as JArray;
                    if (recentArtists != null && recentArtists.Count > 0)
                    {
                        var artistNames = new List<string>();
                        foreach (var artistToken in recentArtists)
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
                                song.ArtistIds.Add(artistIdValue);
                            }
                        }

                        if (artistNames.Count > 0)
                        {
                            song.ArtistNames = new List<string>(artistNames);
                            song.Artist = string.Join("/", artistNames);
                        }
                    }

                    songs.Add(song);
                }

                if (skippedNonSongEntries > 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[API] 最近播放歌曲过滤掉 {skippedNonSongEntries} 个播客/声音条目");
                }

                System.Diagnostics.Debug.WriteLine($"[API] 成功获取 {songs.Count} 首最近播放歌曲");
                return songs;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 获取最近播放歌曲异常: {ex.Message}");
                return new List<SongInfo>();
            }
        }

        private static bool IsRecentSongEntryPodcastOrVoice(JToken? entry)
        {
            if (entry == null)
            {
                return false;
            }

            var entryResourceType = entry["resourceType"]?.Value<string>();
            if (IsNonSongResourceType(entryResourceType))
            {
                return true;
            }

            var dataToken = entry["data"] ?? entry["song"];
            if (dataToken == null)
            {
                return false;
            }

            if (IsNonSongResourceType(dataToken["resourceType"]?.Value<string>()))
            {
                return true;
            }

            if (HasPodcastIndicators(dataToken))
            {
                return true;
            }

            if (dataToken["song"] is JObject nestedSong && HasPodcastIndicators(nestedSong))
            {
                return true;
            }

            return false;
        }

        private static bool HasPodcastIndicators(JToken? token)
        {
            if (token == null)
            {
                return false;
            }

            string[] indicatorProperties =
            {
                "program",
                "programId",
                "programInfo",
                "djProgram",
                "djRadio",
                "radioProgram",
                "mainProgram",
                "sound",
                "soundId",
                "voiceId",
                "voiceInfo",
                "podcast",
                "radio",
                "radioId"
            };

            foreach (var property in indicatorProperties)
            {
                if (token[property] != null && token[property]?.Type != JTokenType.Null)
                {
                    return true;
                }
            }

            var typeValue = token["type"];
            if (typeValue != null)
            {
                if (typeValue.Type == JTokenType.String && IsNonSongResourceType(typeValue.Value<string>()))
                {
                    return true;
                }

                if (typeValue.Type == JTokenType.Integer)
                {
                    var numericType = typeValue.Value<int>();
                    if (numericType == 2000 || numericType == 2001)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool IsNonSongResourceType(string? resourceType)
        {
            if (string.IsNullOrWhiteSpace(resourceType))
            {
                return false;
            }

            var normalized = resourceType.Trim().ToLowerInvariant();
            if (normalized.Contains("song") || normalized.Contains("music"))
            {
                return false;
            }

            string[] podcastIndicators = { "voice", "sound", "program", "dj", "radio", "podcast" };
            if (podcastIndicators.Any(indicator => normalized.Contains(indicator)))
            {
                return true;
            }

            return false;
        }

        public async Task<bool> SendPlaybackLogsAsync(IEnumerable<Dictionary<string, object>> logEntries, CancellationToken cancellationToken = default)
        {
            if (logEntries == null)
            {
                return false;
            }

            var entries = new List<Dictionary<string, object>>();
            foreach (var entry in logEntries)
            {
                if (entry != null && entry.Count > 0)
                {
                    entries.Add(entry);
                }
            }

            if (entries.Count == 0)
            {
                return false;
            }

            var payload = new Dictionary<string, object>
            {
                { "logs", JsonConvert.SerializeObject(entries, Formatting.None) }
            };

            try
            {
                var response = await PostWeApiAsync<JObject>("/feedback/weblog", payload, cancellationToken: cancellationToken).ConfigureAwait(false);
                int code = response["code"]?.Value<int>() ?? -1;
                if (code != 200)
                {
                    System.Diagnostics.Debug.WriteLine($"[PlaybackReporting] weblog 返回异常: code={code}, msg={response["message"]}");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PlaybackReporting] weblog 请求失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 获取用户收藏的专辑列表
        /// 参考: NeteaseCloudMusicApi/module/album_sublist.js
        /// </summary>
        public async Task<(List<AlbumInfo>, int)> GetUserAlbumsAsync(int limit = 100, int offset = 0)
        {
            var payload = new Dictionary<string, object>
            {
                { "limit", limit },
                { "offset", offset },
                { "total", true }
            };

            var response = await PostWeApiAsync<JObject>("/album/sublist", payload);

            if (response["code"]?.Value<int>() != 200)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 获取收藏专辑失败: {response["message"]}");
                return (new List<AlbumInfo>(), 0);
            }

            // 解析总数
            int totalCount = response["count"]?.Value<int>() ?? 0;
            System.Diagnostics.Debug.WriteLine($"[API] 收藏专辑总数: {totalCount}");

            var data = response["data"] as JArray;
            if (data == null)
            {
                return (new List<AlbumInfo>(), totalCount);
            }

            var result = new List<AlbumInfo>();
            foreach (var item in data)
            {
                // album_sublist 返回的专辑对象有时被包裹在 album 字段中，也可能直接位于根节点
                var albumToken = item["album"] as JObject ?? item as JObject;
                if (albumToken == null)
                {
                    continue;
                }

                // 兼容字段缺失的情况，优先使用专辑对象上的 publishTime，回退到根节点
                var publishTimeToken = albumToken["publishTime"] ?? item["publishTime"];

                var album = new AlbumInfo
                {
                    Id = albumToken["id"]?.Value<string>() ?? albumToken["id"]?.Value<long>().ToString(),
                    Name = albumToken["name"]?.Value<string>() ?? "未知专辑",
                    Artist = ResolveAlbumArtistName(albumToken),
                    PicUrl = albumToken["picUrl"]?.Value<string>() ?? string.Empty,
                    PublishTime = FormatAlbumPublishDate(publishTimeToken),
                    TrackCount = ResolveAlbumTrackCount(albumToken),
                    Description = ResolveAlbumDescription(albumToken),
                    IsSubscribed = true
                };

                result.Add(album);
            }

            await FillAlbumDetailsAsync(result);
            return (result, totalCount);
        }

        /// <summary>
        /// 补全专辑缺失的发布日期和曲目数（收藏列表部分字段可能缺失）。
        /// </summary>
        private async Task FillAlbumDetailsAsync(List<AlbumInfo> albums, int batchSize = 50)
        {
            if (albums == null || albums.Count == 0)
            {
                return;
            }

            if (batchSize < 1)
            {
                batchSize = 12;
            }

            // 仅对缺失关键信息的专辑做额外请求
            var needFetch = albums
                .Where(a =>
                    !string.IsNullOrWhiteSpace(a.Id) &&
                    (string.IsNullOrWhiteSpace(a.PublishTime) || a.TrackCount <= 0))
                .ToList();

            for (int i = 0; i < needFetch.Count; i += batchSize)
            {
                var batch = needFetch.Skip(i).Take(batchSize).ToList();
                var detailTasks = batch.Select(a => GetAlbumDetailAsync(a.Id!)).ToList();

                for (int j = 0; j < detailTasks.Count; j++)
                {
                    var detail = await detailTasks[j];
                    if (detail == null || detail.Id == null)
                    {
                        continue;
                    }

                    var album = batch[j];
                    if (string.IsNullOrWhiteSpace(album.PublishTime) && !string.IsNullOrWhiteSpace(detail.PublishTime))
                    {
                        album.PublishTime = detail.PublishTime;
                    }

                    if (album.TrackCount <= 0 && detail.TrackCount > 0)
                    {
                        album.TrackCount = detail.TrackCount;
                    }
                }
            }
        }

        /// <summary>
        /// EAPI 版本的播放日志上报（/eapi/feedback/weblog）
        /// </summary>
        public async Task<bool> SendPlaybackLogsEapiAsync(IEnumerable<Dictionary<string, object>> logEntries, CancellationToken cancellationToken = default)
        {
            if (logEntries == null)
            {
                return false;
            }

            var entries = new List<Dictionary<string, object>>();
            foreach (var entry in logEntries)
            {
                if (entry != null && entry.Count > 0)
                {
                    entries.Add(entry);
                }
            }

            if (entries.Count == 0)
            {
                return false;
            }

            var payload = new Dictionary<string, object>
            {
                { "logs", JsonConvert.SerializeObject(entries, Formatting.None) }
            };

            try
            {
                // 使用 EAPI 客户端，路径以 /api/ 开头，由 PostEApiAsync 自动替换为 /eapi/
                var response = await PostEApiAsync<JObject>("/api/feedback/weblog", payload, useIosHeaders: true, cancellationToken: cancellationToken).ConfigureAwait(false);
                int code = response["code"]?.Value<int>() ?? -1;
                if (code != 200)
                {
                    System.Diagnostics.Debug.WriteLine($"[PlaybackReporting][EAPI] weblog 返回异常: code={code}, msg={response["message"]}");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PlaybackReporting][EAPI] weblog 请求失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// EAPI 版本的 scrobble 封装（基于 feedback/weblog）
        /// </summary>
        public Task<bool> SendScrobbleEapiAsync(long songId, long sourceId, int timeSeconds, string endReason = "playend", CancellationToken cancellationToken = default)
        {
            var logs = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>
                {
                    { "action", "play" },
                    {
                        "json", new Dictionary<string, object>
                        {
                            { "download", 0 },
                            { "end", endReason },
                            { "id", songId },
                            { "sourceId", sourceId },
                            { "time", timeSeconds },
                            { "type", "song" },
                            { "wifi", 0 },
                            { "source", "list" },
                            { "mainsite", 1 },
                            { "content", string.Empty }
                        }
                    }
                }
            };

            return SendPlaybackLogsEapiAsync(logs, cancellationToken);
        }

        /// <summary>
        /// 获取用户收藏的播客列表
        /// 参考: NeteaseCloudMusicApi/module/dj_sublist.js
        /// </summary>
        public async Task<(List<PodcastRadioInfo>, int)> GetSubscribedPodcastsAsync(int limit = 30, int offset = 0)
        {
            try
            {
                var payload = new Dictionary<string, object>
                {
                    { "limit", limit },
                    { "offset", offset },
                    { "total", true }
                };

                var response = await PostWeApiAsync<JObject>("/weapi/djradio/get/subed", payload);

                if (response["code"]?.Value<int>() != 200)
                {
                    System.Diagnostics.Debug.WriteLine($"[API] 获取收藏播客失败: {response["message"]}");
                    return (new List<PodcastRadioInfo>(), 0);
                }

                var list = response["djRadios"] as JArray
                           ?? response["data"]?["djRadios"] as JArray
                           ?? response["data"] as JArray;
                int totalCount = response["count"]?.Value<int>()
                                 ?? response["total"]?.Value<int>()
                                 ?? list?.Count
                                 ?? 0;

                var radios = ParsePodcastRadioList(list);
                return (radios, totalCount);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 获取收藏播客异常: {ex.Message}");
                return (new List<PodcastRadioInfo>(), 0);
            }
        }

        /// <summary>
        /// 云盘功能
        /// </summary>
        #region 云盘

        /// <summary>
        /// 获取云盘歌曲列表
        /// </summary>
        public async Task<CloudSongPageResult> GetCloudSongsAsync(
            int limit = 50,
            int offset = 0,
            CancellationToken cancellationToken = default)
        {
            var page = new CloudSongPageResult
            {
                Limit = limit,
                Offset = offset
            };

            try
            {
                var payload = new Dictionary<string, object>
                {
                    { "limit", limit },
                    { "offset", offset }
                };

                var response = await PostWeApiAsync<JObject>(
                    "/v1/cloud/get",
                    payload,
                    cancellationToken: cancellationToken);

                if (response == null)
                {
                    return page;
                }

                page.TotalCount = response["count"]?.Value<int>() ?? response["size"]?.Value<int>() ?? page.TotalCount;
                page.UsedSize = response["size"]?.Value<long>() ?? page.UsedSize;
                page.MaxSize = response["maxSize"]?.Value<long>() ?? page.MaxSize;
                page.HasMore = response["hasMore"]?.Value<bool>() ?? response["more"]?.Value<bool>() ?? false;

                var dataArray = response["data"] as JArray;
                if (dataArray == null || dataArray.Count == 0)
                {
                    return page;
                }

                var songIds = new List<string>();
                foreach (var item in dataArray.OfType<JObject>())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string matchedId = item["simpleSong"]?["id"]?.ToString();
                    string cloudId = item["songId"]?.ToString();

                    if (!string.IsNullOrEmpty(matchedId))
                    {
                        songIds.Add(matchedId);
                    }
                    else if (!string.IsNullOrEmpty(cloudId))
                    {
                        songIds.Add(cloudId);
                    }
                }

                var uniqueSongIds = songIds
                    .Where(id => !string.IsNullOrEmpty(id))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                var resolvedSongs = uniqueSongIds.Count > 0
                    ? await GetSongsByIdsAsync(uniqueSongIds, cancellationToken)
                    : new List<SongInfo>();

                var resolvedMap = resolvedSongs.ToDictionary(s => s.Id, StringComparer.Ordinal);

                foreach (var item in dataArray.OfType<JObject>())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string cloudSongId = item["songId"]?.ToString() ?? string.Empty;
                    string matchedSongId = item["simpleSong"]?["id"]?.ToString();
                    string lookupId = !string.IsNullOrEmpty(matchedSongId) ? matchedSongId : cloudSongId;

                    SongInfo song;
                    if (!string.IsNullOrEmpty(lookupId) && resolvedMap.TryGetValue(lookupId, out var resolved))
                    {
                        song = resolved;
                    }
                    else
                    {
                        song = BuildFallbackCloudSong(item);
                        if (song == null)
                        {
                            continue;
                        }
                    }

                    song.IsCloudSong = true;
                    song.IsAvailable = true;
                    song.CloudSongId = string.IsNullOrEmpty(cloudSongId) ? lookupId ?? string.Empty : cloudSongId;
                    song.CloudMatchedSongId = matchedSongId ?? string.Empty;
                    song.CloudFileName = item["fileName"]?.Value<string>() ?? item["songName"]?.Value<string>() ?? song.CloudFileName ?? song.Name;
                    song.CloudFileSize = item["fileSize"]?.Value<long>() ?? song.CloudFileSize;
                    song.CloudUploadTime = item["addTime"]?.Value<long>();

                    if (song.CloudFileSize > 0 && song.Size == 0)
                    {
                        song.Size = song.CloudFileSize;
                    }

                    if (string.IsNullOrEmpty(song.Name))
                    {
                        song.Name = song.CloudFileName ?? $"云盘歌曲 {song.CloudSongId}";
                    }

                    if (string.IsNullOrEmpty(song.Artist))
                    {
                        song.Artist = item["artist"]?.Value<string>() ?? string.Empty;
                    }

                    if (string.IsNullOrEmpty(song.Album))
                    {
                        song.Album = item["album"]?.Value<string>() ?? string.Empty;
                    }

                    if (song.IsCloudSong)
                    {
                        if (string.Equals(song.Artist, "未知艺术家", StringComparison.OrdinalIgnoreCase))
                        {
                            song.Artist = string.Empty;
                        }

                        if (string.Equals(song.Album, "未知专辑", StringComparison.OrdinalIgnoreCase))
                        {
                            song.Album = string.Empty;
                        }
                    }

                    page.Songs.Add(song);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Cloud] 获取云盘歌曲失败: {ex.Message}");
            }

            return page;
        }

        /// <summary>
        /// 删除云盘歌曲
        /// </summary>
        public async Task<bool> DeleteCloudSongsAsync(
            IEnumerable<string> cloudSongIds,
            CancellationToken cancellationToken = default)
        {
            if (cloudSongIds == null)
            {
                return false;
            }

            var ids = cloudSongIds
                .Select(id => id?.Trim())
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (ids.Count == 0)
            {
                return false;
            }

            var payload = new Dictionary<string, object>
            {
                { "songIds", ids }
            };

            var response = await PostWeApiAsync<JObject>(
                "/cloud/del",
                payload,
                cancellationToken: cancellationToken);

            return response?["code"]?.Value<int>() == 200;
        }

        /// <summary>
        /// 上传单个文件到云盘
        /// </summary>
        public async Task<CloudUploadResult> UploadCloudSongAsync(
            string filePath,
            IProgress<CloudUploadProgress> progress = null,
            CancellationToken cancellationToken = default,
            int fileIndex = 1,
            int totalFiles = 1)
        {
            var result = new CloudUploadResult
            {
                FilePath = filePath
            };

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(filePath))
                {
                    throw new ArgumentException("文件路径不能为空", nameof(filePath));
                }

                if (!System.IO.File.Exists(filePath))
                {
                    throw new FileNotFoundException("找不到指定的文件", filePath);
                }

                string originalFileName = System.IO.Path.GetFileName(filePath);
                string ext = System.IO.Path.GetExtension(filePath)?.TrimStart('.').ToLowerInvariant() ?? "mp3";
                if (originalFileName != null && originalFileName.ToLowerInvariant().Contains("flac"))
                {
                    ext = "flac";
                }

                string sanitizedFileName = SanitizeCloudFileName(originalFileName, ext);
                long fileSize = new System.IO.FileInfo(filePath).Length;
                const int bitrate = 999000;

                ReportUploadProgress(progress, filePath, fileIndex, totalFiles, 5, "计算文件校验", 0, fileSize);
                string md5 = await ComputeFileMd5Async(filePath, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                ReportUploadProgress(progress, filePath, fileIndex, totalFiles, 15, "检查云盘状态", 0, fileSize);
                var checkPayload = new Dictionary<string, object>
                {
                    { "bitrate", bitrate.ToString() },
                    { "ext", "" },
                    { "length", fileSize },
                    { "md5", md5 },
                    { "songId", "0" },
                    { "version", 1 }
                };

                var checkResp = await PostInterfaceWeApiAsync<JObject>(
                    "/api/cloud/upload/check",
                    checkPayload,
                    cancellationToken: cancellationToken);

                ValidateCloudResponse(checkResp, "检查云盘状态");

                bool needUpload = checkResp?["needUpload"]?.Value<bool>() ?? true;
                string checkSongId = checkResp?["songId"]?.Value<string>() ?? "0";

                ReportUploadProgress(progress, filePath, fileIndex, totalFiles, 30, "请求上传令牌", 0, fileSize);

                const string bucket = "jd-musicrep-privatecloud-audio-public";
                var tokenPayload = new Dictionary<string, object>
                {
                    { "bucket", bucket },
                    { "ext", ext },
                    { "filename", sanitizedFileName },
                    { "local", false },
                    { "nos_product", 3 },
                    { "type", "audio" },
                    { "md5", md5 }
                };

                var tokenResp = await PostWeApiAsync<JObject>(
                    "/nos/token/alloc",
                    tokenPayload,
                    cancellationToken: cancellationToken);

                ValidateCloudResponse(tokenResp, "获取上传令牌");

                var tokenResult = tokenResp?["result"] as JObject;
                string resourceId = tokenResult?["resourceId"]?.Value<string>() ?? string.Empty;
                string objectKey = tokenResult?["objectKey"]?.Value<string>() ?? string.Empty;
                string token = tokenResult?["token"]?.Value<string>() ?? string.Empty;

                if (string.IsNullOrWhiteSpace(resourceId) ||
                    string.IsNullOrWhiteSpace(objectKey) ||
                    string.IsNullOrWhiteSpace(token))
                {
                    throw new Exception("上传令牌响应缺少必要字段");
                }

                if (string.IsNullOrEmpty(objectKey) || string.IsNullOrEmpty(token))
                {
                    throw new Exception("获取上传令牌失败");
                }

                if (needUpload)
                {
                    ReportUploadProgress(progress, filePath, fileIndex, totalFiles, 45, "上传音频文件", 0, fileSize);
                    await UploadToNosAsync(
                        filePath,
                        bucket,
                        objectKey,
                        token,
                        md5,
                        ext,
                        fileSize,
                        progress,
                        fileIndex,
                        totalFiles,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    ReportUploadProgress(progress, filePath, fileIndex, totalFiles, 55, "文件已存在，跳过上传", fileSize, fileSize);
                }

                var metadata = ExtractAudioMetadata(filePath);
                string songName = string.IsNullOrWhiteSpace(metadata.Song)
                    ? System.IO.Path.GetFileNameWithoutExtension(originalFileName)
                    : metadata.Song;
                string artist = string.IsNullOrWhiteSpace(metadata.Artist) ? "未知艺术家" : metadata.Artist;
                string album = string.IsNullOrWhiteSpace(metadata.Album) ? "未知专辑" : metadata.Album;

                ReportUploadProgress(progress, filePath, fileIndex, totalFiles, 70, "提交云盘信息", fileSize, fileSize);
                var infoPayload = new Dictionary<string, object>
                {
                    { "md5", md5 },
                    { "songid", checkSongId },
                    { "filename", originalFileName },
                    { "song", songName },
                    { "album", album },
                    { "artist", artist },
                    { "bitrate", bitrate.ToString() },
                    { "resourceId", resourceId }
                };

                var infoResp = await PostWeApiAsync<JObject>(
                    "/upload/cloud/info/v2",
                    infoPayload,
                    cancellationToken: cancellationToken);

                ValidateCloudResponse(infoResp, "提交云盘信息");

                string cloudSongId = infoResp?["songId"]?.Value<string>() ?? infoResp?["id"]?.Value<string>() ?? checkSongId;

                ReportUploadProgress(progress, filePath, fileIndex, totalFiles, 85, "发布到云盘", fileSize, fileSize);
                var publishPayload = new Dictionary<string, object>
                {
                    { "songid", cloudSongId }
                };

                var publishResp = await PostInterfaceWeApiAsync<JObject>(
                    "/api/cloud/pub/v2",
                    publishPayload,
                    cancellationToken: cancellationToken);

                int publishCode = publishResp?["code"]?.Value<int>() ?? -1;
                if (publishCode != 200)
                {
                    string publishMsg = publishResp?["message"]?.Value<string>() ?? publishResp?["msg"]?.Value<string>() ?? $"code={publishCode}";
                    throw new Exception($"发布云盘歌曲失败: {publishMsg}");
                }

                result.Success = true;
                result.CloudSongId = cloudSongId ?? string.Empty;
                result.MatchedSongId = checkSongId ?? string.Empty;

                ReportUploadProgress(progress, filePath, fileIndex, totalFiles, 100, "上传完成", fileSize, fileSize);
            }
            catch (OperationCanceledException)
            {
                result.Success = false;
                result.ErrorMessage = "上传已取消";
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Cloud] 上传失败: {ex}");
                result.Success = false;
                result.ErrorMessage = GetInnermostExceptionMessage(ex);
            }

            return result;
        }

        private static string SanitizeCloudFileName(string fileName, string extension)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                return $"CloudUpload_{DateTime.Now:yyyyMMddHHmmss}";
            }

            string sanitized = fileName;
            if (!string.IsNullOrEmpty(extension))
            {
                string suffix = "." + extension;
                if (sanitized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    sanitized = sanitized.Substring(0, sanitized.Length - suffix.Length);
                }
            }

            sanitized = sanitized.Replace(" ", string.Empty).Replace(".", "_");

            return sanitized;
        }

        private static (string Song, string Artist, string Album) ExtractAudioMetadata(string filePath)
        {
            try
            {
                using var tagFile = TagLib.File.Create(filePath);
                var tag = tagFile?.Tag;

                string song = tag?.Title ?? string.Empty;
                string artist = string.Empty;

                if (tag != null)
                {
                    if (tag.Performers != null && tag.Performers.Length > 0)
                    {
                        artist = string.Join("/", tag.Performers.Where(p => !string.IsNullOrWhiteSpace(p)));
                    }

                    if (string.IsNullOrEmpty(artist) && tag.FirstPerformer != null)
                    {
                        artist = tag.FirstPerformer;
                    }
                }

                string album = tag?.Album ?? string.Empty;
                return (song ?? string.Empty, artist ?? string.Empty, album ?? string.Empty);
            }
            catch
            {
                return (string.Empty, string.Empty, string.Empty);
            }
        }

        private static async Task<string> ComputeFileMd5Async(string filePath, CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                using var stream = System.IO.File.OpenRead(filePath);
                using var md5 = MD5.Create();
                var hash = md5.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
            }, cancellationToken).ConfigureAwait(false);
        }

        private async Task UploadToNosAsync(
            string filePath,
            string bucket,
            string objectKey,
            string token,
            string md5,
            string extension,
            long fileSize,
            IProgress<CloudUploadProgress> progress,
            int fileIndex,
            int totalFiles,
            CancellationToken cancellationToken)
        {
            var lbsUrl = $"https://wanproxy.127.net/lbs?version=1.0&bucketname={Uri.EscapeDataString(bucket)}";
            var lbsResponse = await _uploadHttpClient.GetAsync(lbsUrl, cancellationToken).ConfigureAwait(false);
            lbsResponse.EnsureSuccessStatusCode();

            string lbsBody = await lbsResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
            var lbsJson = JObject.Parse(lbsBody);
            var uploadUri = BuildNosUploadUri(lbsJson, bucket, objectKey);

            using var request = new HttpRequestMessage(HttpMethod.Post, uploadUri);
            request.Headers.TryAddWithoutValidation("x-nos-token", token);
            request.Headers.TryAddWithoutValidation("Content-MD5", md5);
            request.Headers.TryAddWithoutValidation("Accept", "*/*");
            request.Headers.TryAddWithoutValidation("Connection", "keep-alive");
            request.Headers.Referrer = MUSIC_URI;
            request.Headers.ExpectContinue = false;
            var userAgent = _desktopUserAgent ?? USER_AGENT;
            if (!string.IsNullOrEmpty(userAgent))
            {
                request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            }

            using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            int lastPercent = -1;
            double lastSpeed = 0;
            var content = new ProgressStreamContent(
                fileStream,
                64 * 1024,
                uploadedBytes =>
                {
                    if (fileSize <= 0)
                    {
                        return;
                    }

                    int percent = (int)Math.Min(100, Math.Round(uploadedBytes * 100.0 / fileSize));
                    if (percent == lastPercent && uploadedBytes < fileSize)
                    {
                        return;
                    }

                    double speed = 0;
                    if (stopwatch.Elapsed.TotalSeconds > 0.01)
                    {
                        speed = uploadedBytes / stopwatch.Elapsed.TotalSeconds;
                        lastSpeed = speed;
                    }
                    else
                    {
                        speed = lastSpeed;
                    }

                    ReportUploadProgress(
                        progress,
                        filePath,
                        fileIndex,
                        totalFiles,
                        percent,
                        "上传音频文件",
                        uploadedBytes,
                        fileSize,
                        speed);

                    lastPercent = percent;
                },
                fileSize,
                MapMimeType(extension),
                cancellationToken);

            request.Content = content;

            HttpResponseMessage response;
            try
            {
                response = await _uploadHttpClient
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new Exception($"NOS上传请求失败（{uploadUri.Host}）：{ex.Message}", ex);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    string error = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    throw new Exception($"NOS上传失败: {response.StatusCode} {error}");
                }
            }

            stopwatch.Stop();

            if (lastSpeed <= 0 && fileSize > 0 && stopwatch.Elapsed.TotalSeconds > 0.01)
            {
                lastSpeed = fileSize / stopwatch.Elapsed.TotalSeconds;
            }

            ReportUploadProgress(
                progress,
                filePath,
                fileIndex,
                totalFiles,
                60,
                "文件上传完成",
                fileSize,
                fileSize,
                lastSpeed);
        }

        private static Uri BuildNosUploadUri(JObject lbsJson, string bucket, string objectKey)
        {
            var uploadArray = lbsJson["upload"] as JArray;
            string? endpointCandidate = uploadArray?.FirstOrDefault()?.Value<string>()?.Trim();

            Uri baseUri;
            if (!string.IsNullOrEmpty(endpointCandidate))
            {
                if (!Uri.TryCreate(endpointCandidate, UriKind.Absolute, out baseUri))
                {
                    string normalized = endpointCandidate.IndexOf("://", StringComparison.OrdinalIgnoreCase) >= 0
                        ? endpointCandidate
                        : $"http://{endpointCandidate}";
                    baseUri = Uri.TryCreate(normalized, UriKind.Absolute, out var parsed)
                        ? parsed
                        : new Uri("http://45.127.129.8");
                }
            }
            else
            {
                baseUri = new Uri("http://45.127.129.8");
            }

            var pathSegments = new List<string>();
            AppendPathSegments(pathSegments, baseUri.AbsolutePath);
            AppendPathSegments(pathSegments, bucket);
            AppendPathSegments(pathSegments, objectKey);

            var builder = new UriBuilder(baseUri.Scheme, baseUri.Host, baseUri.IsDefaultPort ? -1 : baseUri.Port)
            {
                Path = string.Join("/", pathSegments),
                Query = "offset=0&complete=true&version=1.0"
            };

            return builder.Uri;
        }

        private static void AppendPathSegments(List<string> segments, string path)
        {
            if (segments == null || string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            string decodedPath = Uri.UnescapeDataString(path);
            var parts = decodedPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                segments.Add(Uri.EscapeDataString(part));
            }
        }

        private static string MapMimeType(string extension)
        {
            return extension switch
            {
                "flac" => "audio/flac",
                "m4a" => "audio/mp4",
                "mp4" => "audio/mp4",
                "wav" => "audio/wav",
                "ogg" => "audio/ogg",
                "ape" => "audio/ape",
                "wma" => "audio/x-ms-wma",
                _ => "audio/mpeg"
            };
        }

        private static void ReportUploadProgress(
            IProgress<CloudUploadProgress> progress,
            string filePath,
            int fileIndex,
            int totalFiles,
            int percent,
            string stage,
            long bytesTransferred = 0,
            long totalBytes = 0,
            double speedBytesPerSecond = 0)
        {
            progress?.Report(new CloudUploadProgress
            {
                FilePath = filePath,
                FileIndex = fileIndex,
                TotalFiles = totalFiles,
                FileProgressPercent = percent,
                StageMessage = stage,
                BytesTransferred = bytesTransferred,
                TotalBytes = totalBytes,
                TransferSpeedBytesPerSecond = speedBytesPerSecond
            });
        }

        private static string GetInnermostExceptionMessage(Exception ex)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var ordered = new List<string>();
            Exception? current = ex;
            while (current != null)
            {
                if (!string.IsNullOrWhiteSpace(current.Message))
                {
                    string trimmed = current.Message.Trim();
                    if (seen.Add(trimmed))
                    {
                        ordered.Add(trimmed);
                    }
                }

                current = current.InnerException;
            }

            return ordered.Count > 0 ? string.Join(" -> ", ordered) : "未知错误";
        }

        private static void ValidateCloudResponse(JObject? response, string stage)
        {
            if (response == null)
            {
                throw new Exception($"{stage}：服务器未返回数据");
            }

            int code = response["code"]?.Value<int>() ?? 200;
            if (code != 200)
            {
                string message = response["message"]?.Value<string>() ??
                                 response["msg"]?.Value<string>() ??
                                 $"code={code}";
                throw new Exception($"{stage}失败：{message}");
            }
        }

        /// <summary>
        /// 支持进度回调的 HttpContent 包装
        /// </summary>
        private sealed class ProgressStreamContent : HttpContent
        {
            private readonly Stream _sourceStream;
            private readonly int _bufferSize;
            private readonly Action<long> _progressCallback;
            private readonly long _totalLength;
            private readonly CancellationToken _cancellationToken;

            public ProgressStreamContent(
                Stream sourceStream,
                int bufferSize,
                Action<long> progressCallback,
                long totalLength,
                string mediaType,
                CancellationToken cancellationToken)
            {
                _sourceStream = sourceStream ?? throw new ArgumentNullException(nameof(sourceStream));
                _bufferSize = bufferSize <= 0 ? 64 * 1024 : bufferSize;
                _progressCallback = progressCallback ?? (_ => { });
                _totalLength = totalLength;
                _cancellationToken = cancellationToken;

                Headers.ContentType = new MediaTypeHeaderValue(mediaType);
                if (totalLength > 0)
                {
                    Headers.ContentLength = totalLength;
                }
            }

            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            {
                var buffer = new byte[_bufferSize];
                long uploaded = 0;
                int bytesRead;

                while ((bytesRead = await _sourceStream
                           .ReadAsync(buffer, 0, buffer.Length, _cancellationToken)
                           .ConfigureAwait(false)) > 0)
                {
                    await stream.WriteAsync(buffer, 0, bytesRead, _cancellationToken).ConfigureAwait(false);
                    uploaded += bytesRead;
                    _progressCallback(uploaded);

                    if (_cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }

            protected override bool TryComputeLength(out long length)
            {
                if (_totalLength > 0)
                {
                    length = _totalLength;
                    return true;
                }

                length = 0;
                return false;
            }
        }

        private SongInfo? BuildFallbackCloudSong(JObject entry)
        {
            if (entry == null)
            {
                return null;
            }

            string cloudId = entry["songId"]?.ToString() ?? Guid.NewGuid().ToString("N");
            var song = new SongInfo
            {
                Id = cloudId,
                Name = entry["songName"]?.Value<string>() ?? entry["fileName"]?.Value<string>() ?? $"云盘歌曲 {cloudId}",
                Artist = entry["artist"]?.Value<string>() ?? string.Empty,
                Album = entry["album"]?.Value<string>() ?? string.Empty,
                CloudSongId = cloudId,
                CloudFileSize = entry["fileSize"]?.Value<long>() ?? 0,
                CloudUploadTime = entry["addTime"]?.Value<long>(),
                IsAvailable = true,
                RequiresVip = false
            };

            long durationMs = entry["simpleSong"]?["duration"]?.Value<long>() ??
                              entry["songData"]?["duration"]?.Value<long>() ??
                              entry["duration"]?.Value<long>() ?? 0;
            if (durationMs > 0)
            {
                song.Duration = SanitizeDurationSeconds(durationMs);
            }

            return song;
        }

        #endregion

        /// <summary>
        /// 获取推荐新歌
        /// 参考: NeteaseCloudMusicApi/module/personalized_newsong.js
        /// </summary>
        public async Task<List<SongInfo>> GetPersonalizedNewSongsAsync(int limit = 10)
        {
            var payload = new Dictionary<string, object>
            {
                { "type", "recommend" },
                { "limit", limit },
                { "areaId", 0 }
            };

            var response = await PostWeApiAsync<JObject>("/personalized/newsong", payload);

            if (response["code"]?.Value<int>() != 200)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 获取推荐新歌失败: {response["message"]}");
                return new List<SongInfo>();
            }

            var result = response["result"] as JArray;
            if (result == null)
            {
                return new List<SongInfo>();
            }

            // 从result中提取song字段
            var songs = new JArray();
            foreach (var item in result)
            {
                var song = item["song"];
                if (song != null)
                {
                    songs.Add(song);
                }
            }

            return ParseSongList(songs);
        }

        /// <summary>
        /// 获取用户听歌排行
        /// 参考: NeteaseCloudMusicApi/module/user_record.js
        /// </summary>
        /// <param name="uid">用户ID</param>
        /// <param name="type">0=全部时间, 1=最近一周</param>
        public async Task<List<(SongInfo song, int playCount)>> GetUserPlayRecordAsync(long uid, int type = 0)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[GetUserPlayRecord] uid={uid}, type={type}");

                var payload = new Dictionary<string, object>
                {
                    { "uid", uid },
                    { "type", type }
                };

                var response = await PostWeApiAsync<JObject>("/v1/play/record", payload);

                if (response["code"]?.Value<int>() != 200)
                {
                    int code = response["code"]?.Value<int>() ?? -1;
                    string message = response["message"]?.Value<string>() ?? response["msg"]?.Value<string>() ?? "未知错误";
                    System.Diagnostics.Debug.WriteLine($"[API] 获取用户听歌排行失败: code={code}, message={message}");
                    return new List<(SongInfo, int)>();
                }

                // 根据type选择weekData或allData
                JArray data = type == 1
                    ? response["weekData"] as JArray
                    : response["allData"] as JArray;

                if (data == null)
                {
                    System.Diagnostics.Debug.WriteLine("[API] 听歌排行数据为空");
                    return new List<(SongInfo, int)>();
                }

                var result = new List<(SongInfo, int)>();
                foreach (var item in data)
                {
                    var songData = item["song"];
                    if (songData == null) continue;

                    var song = new SongInfo
                    {
                        Id = songData["id"]?.Value<string>() ?? songData["id"]?.Value<long>().ToString(),
                        Name = songData["name"]?.Value<string>() ?? "未知歌曲",
                        Artist = string.Join("/",
                            (songData["ar"] ?? songData["artists"])?.Select(a => a["name"]?.Value<string>()).Where(n => !string.IsNullOrWhiteSpace(n))
                            ?? new[] { "未知艺术家" }),
                        Album = (songData["al"] ?? songData["album"])?["name"]?.Value<string>() ?? "未知专辑",
                        AlbumId = (songData["al"] ?? songData["album"])?["id"]?.Value<string>()
                            ?? (songData["al"] ?? songData["album"])?["id"]?.Value<long>().ToString(),
                        Duration = SanitizeDurationSeconds(songData["dt"]?.Value<long>() ?? songData["duration"]?.Value<long>()),
                        PicUrl = (songData["al"] ?? songData["album"])?["picUrl"]?.Value<string>() ?? ""
                    };
                    if (songData is JObject playRecordSong)
                    {
                        song.RequiresVip = IsVipSong(playRecordSong);
                    }
                    if (songData is JObject recentSongObj)
                    {
                        song.RequiresVip = IsVipSong(recentSongObj);
                    }

                    var recordArtists = songData["ar"] as JArray ?? songData["artists"] as JArray;
                    if (recordArtists != null && recordArtists.Count > 0)
                    {
                        var artistNames = new List<string>();
                        foreach (var artistToken in recordArtists)
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
                                song.ArtistIds.Add(artistIdValue);
                            }
                        }

                        if (artistNames.Count > 0)
                        {
                            song.ArtistNames = new List<string>(artistNames);
                            song.Artist = string.Join("/", artistNames);
                        }
                    }

                    var playCount = item["playCount"]?.Value<int>() ?? 0;
                    result.Add((song, playCount));
                }

                System.Diagnostics.Debug.WriteLine($"[API] 成功获取 {result.Count} 首听歌排行");
                return result;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 获取听歌排行异常: {ex.Message}");
                return new List<(SongInfo, int)>();
            }
        }

        /// <summary>
        /// 获取精品歌单
        /// 参考: NeteaseCloudMusicApi/module/top_playlist_highquality.js
        /// </summary>
        /// <param name="cat">分类</param>
        /// <param name="limit">返回数量</param>
        /// <param name="before">游标(上一次返回的最后一个歌单的updateTime)</param>
        public async Task<(List<PlaylistInfo>, long, bool)> GetHighQualityPlaylistsAsync(
            string cat = "全部", int limit = 50, long before = 0)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[GetHighQualityPlaylists] cat={cat}, limit={limit}, before={before}");

                var payload = new Dictionary<string, object>
                {
                    { "cat", cat },
                    { "limit", limit },
                    { "lasttime", before },
                    { "total", true }
                };

                var response = await PostWeApiAsync<JObject>(
                    "/api/playlist/highquality/list",
                    payload,
                    autoConvertApiSegment: true);

                if (response["code"]?.Value<int>() != 200)
                {
                    System.Diagnostics.Debug.WriteLine($"[API] 获取精品歌单失败: {response["message"]}");
                    return (new List<PlaylistInfo>(), 0, false);
                }

                var playlists = response["playlists"] as JArray;
                var more = response["more"]?.Value<bool>() ?? false;
                var lasttime = response["lasttime"]?.Value<long>() ?? 0;

                var result = new List<PlaylistInfo>();
                if (playlists != null)
                {
                    foreach (var item in playlists)
                    {
                        var playlist = ParsePlaylistDetail(item as JObject);
                        if (playlist != null)
                        {
                            result.Add(playlist);
                        }
                    }
                }

                System.Diagnostics.Debug.WriteLine($"[API] 成功获取 {result.Count} 个精品歌单, more={more}");
                return (result, lasttime, more);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 获取精品歌单异常: {ex.Message}");
                return (new List<PlaylistInfo>(), 0, false);
            }
        }

        /// <summary>
        /// 获取新歌速递
        /// 参考: NeteaseCloudMusicApi/module/top_song.js
        /// </summary>
        /// <param name="areaType">地区: 0=全部, 7=华语, 96=欧美, 8=日本, 16=韩国</param>
        public async Task<List<SongInfo>> GetNewSongsAsync(int areaType = 0)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[GetNewSongs] areaType={areaType}");

                var payload = new Dictionary<string, object>
                {
                    { "areaId", areaType },
                    { "total", true }
                };

                var response = await PostWeApiAsync<JObject>("/v1/discovery/new/songs", payload);

                if (response["code"]?.Value<int>() != 200)
                {
                    System.Diagnostics.Debug.WriteLine($"[API] 获取新歌速递失败: {response["message"]}");
                    return new List<SongInfo>();
                }

                var data = response["data"] as JArray;
                if (data == null)
                {
                    System.Diagnostics.Debug.WriteLine("[API] 新歌速递数据为空");
                    return new List<SongInfo>();
                }

                var songs = ParseSongList(data);
                System.Diagnostics.Debug.WriteLine($"[API] 成功获取 {songs.Count} 首新歌");
                return songs;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 获取新歌速递异常: {ex.Message}");
                return new List<SongInfo>();
            }
        }

        /// <summary>
        /// 获取最近播放的歌单
        /// 参考: NeteaseCloudMusicApi/module/record_recent_playlist.js
        /// </summary>
        public async Task<List<PlaylistInfo>> GetRecentPlaylistsAsync(int limit = 100)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[GetRecentPlaylists] limit={limit}");

                var payload = new Dictionary<string, object>
                {
                    { "limit", limit }
                };

                var response = await PostWeApiAsync<JObject>(
                    "/api/play-record/playlist/list",
                    payload,
                    autoConvertApiSegment: true);

                if (response["code"]?.Value<int>() != 200)
                {
                    System.Diagnostics.Debug.WriteLine($"[API] 获取最近播放歌单失败: {response["message"]}");
                    return new List<PlaylistInfo>();
                }

                var list = response["data"]?["list"] as JArray;
                if (list == null)
                {
                    System.Diagnostics.Debug.WriteLine("[API] 最近播放歌单数据为空");
                    return new List<PlaylistInfo>();
                }

                var result = new List<PlaylistInfo>();
                foreach (var item in list)
                {
                    var playlistData = item["data"];
                    if (playlistData == null) continue;

                    var playlist = new PlaylistInfo
                    {
                        Id = playlistData["id"]?.Value<string>() ?? playlistData["id"]?.Value<long>().ToString(),
                        Name = playlistData["name"]?.Value<string>() ?? "未知歌单",
                        Creator = playlistData["creator"]?["nickname"]?.Value<string>() ?? "未知",
                        CreatorId = playlistData["creator"]?["userId"]?.Value<long>() ?? 0,
                        TrackCount = playlistData["trackCount"]?.Value<int>() ?? 0,
                        CoverUrl = playlistData["coverImgUrl"]?.Value<string>() ?? "",
                        Description = playlistData["description"]?.Value<string>() ?? ""
                    };

                    result.Add(playlist);
                }

                System.Diagnostics.Debug.WriteLine($"[API] 成功获取 {result.Count} 个最近播放歌单");
                return result;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 获取最近播放歌单异常: {ex.Message}");
                return new List<PlaylistInfo>();
            }
        }

        /// <summary>
        /// 获取最近播放的专辑
        /// 参考: NeteaseCloudMusicApi/module/record_recent_album.js
        /// </summary>
        public async Task<List<AlbumInfo>> GetRecentAlbumsAsync(int limit = 100)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[GetRecentAlbums] limit={limit}");

                var payload = new Dictionary<string, object>
                {
                    { "limit", limit }
                };

                var response = await PostWeApiAsync<JObject>(
                    "/api/play-record/album/list",
                    payload,
                    autoConvertApiSegment: true);

                if (response["code"]?.Value<int>() != 200)
                {
                    System.Diagnostics.Debug.WriteLine($"[API] 获取最近播放专辑失败: {response["message"]}");
                    return new List<AlbumInfo>();
                }

                var list = response["data"]?["list"] as JArray;
                if (list == null)
                {
                    System.Diagnostics.Debug.WriteLine("[API] 最近播放专辑数据为空");
                    return new List<AlbumInfo>();
                }

                var result = new List<AlbumInfo>();
                foreach (var item in list)
                {
                    var albumData = item["data"];
                    if (albumData == null) continue;

                    var album = new AlbumInfo
                    {
                        Id = albumData["id"]?.Value<string>() ?? albumData["id"]?.Value<long>().ToString(),
                        Name = albumData["name"]?.Value<string>() ?? "未知专辑",
                        Artist = ResolveAlbumArtistName(albumData),
                        PicUrl = albumData["picUrl"]?.Value<string>() ?? "",
                        PublishTime = FormatAlbumPublishDate(albumData["publishTime"]),
                        TrackCount = ResolveAlbumTrackCount(albumData),
                        Description = ResolveAlbumDescription(albumData)
                    };

                    result.Add(album);
                }

                System.Diagnostics.Debug.WriteLine($"[API] 成功获取 {result.Count} 个最近播放专辑");
                return result;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 获取最近播放专辑异常: {ex.Message}");
                return new List<AlbumInfo>();
            }
        }

        /// <summary>
        /// 获取最近播放的播客
        /// 参考: NeteaseCloudMusicApi/module/record_recent_dj.js
        /// </summary>
        public async Task<List<PodcastRadioInfo>> GetRecentPodcastsAsync(int limit = 100)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[GetRecentPodcasts] limit={limit}");

                var payload = new Dictionary<string, object>
                {
                    { "limit", limit }
                };

                var response = await PostWeApiAsync<JObject>(
                    "/api/play-record/djradio/list",
                    payload,
                    autoConvertApiSegment: true);

                if (response["code"]?.Value<int>() != 200)
                {
                    System.Diagnostics.Debug.WriteLine($"[API] 获取最近播放播客失败: {response["message"]}");
                    return new List<PodcastRadioInfo>();
                }

                var list = response["data"]?["list"] as JArray
                           ?? response["list"] as JArray
                           ?? response["data"] as JArray;
                if (list == null)
                {
                    System.Diagnostics.Debug.WriteLine("[API] 最近播放播客数据为空");
                    return new List<PodcastRadioInfo>();
                }

                var result = new List<PodcastRadioInfo>();
                foreach (var item in list)
                {
                    var radioToken = item["data"]?["radio"]
                                     ?? item["data"]
                                     ?? item["radio"]
                                     ?? item["djRadio"]
                                     ?? item;

                    var radio = ParsePodcastRadio(radioToken);
                    if (radio != null)
                    {
                        result.Add(radio);
                    }
                }

                System.Diagnostics.Debug.WriteLine($"[API] 成功获取 {result.Count} 个最近播放播客");
                return result;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 获取最近播放播客异常: {ex.Message}");
                return new List<PodcastRadioInfo>();
            }
        }


        /// <summary>
        /// 获取分类歌单
        /// 参考: NeteaseCloudMusicApi/module/top_playlist.js
        /// </summary>
        /// <param name="cat">分类名称</param>
        /// <param name="order">排序: hot=最热, new=最新</param>
        /// <param name="limit">每页数量</param>
        /// <param name="offset">偏移量</param>
        public async Task<(List<PlaylistInfo>, long, bool)> GetPlaylistsByCategoryAsync(
            string cat = "全部", string order = "hot", int limit = 50, int offset = 0)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[GetPlaylistsByCategory] cat={cat}, order={order}, limit={limit}, offset={offset}");

                var payload = new Dictionary<string, object>
                {
                    { "cat", cat },
                    { "order", order },
                    { "limit", limit },
                    { "offset", offset },
                    { "total", true }
                };

                var response = await PostWeApiAsync<JObject>("/playlist/list", payload);

                if (response["code"]?.Value<int>() != 200)
                {
                    System.Diagnostics.Debug.WriteLine($"[API] 获取分类歌单失败: {response["message"]}");
                    return (new List<PlaylistInfo>(), 0, false);
                }

                var playlists = response["playlists"] as JArray;
                var total = response["total"]?.Value<long>() ?? 0;
                var more = response["more"]?.Value<bool>() ?? false;

                var result = new List<PlaylistInfo>();
                if (playlists != null)
                {
                    foreach (var item in playlists)
                    {
                        var playlist = ParsePlaylistDetail(item as JObject);
                        if (playlist != null)
                        {
                            result.Add(playlist);
                        }
                    }
                }

                System.Diagnostics.Debug.WriteLine($"[API] 成功获取 {result.Count} 个分类歌单, total={total}, more={more}");
                return (result, total, more);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 获取分类歌单异常: {ex.Message}");
                return (new List<PlaylistInfo>(), 0, false);
            }
        }

        /// <summary>
        /// 获取新碟上架
        /// 参考: NeteaseCloudMusicApi/module/album_newest.js
        /// </summary>
        public async Task<List<AlbumInfo>> GetNewAlbumsAsync()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("[GetNewAlbums] 获取新碟上架");

                var payload = new Dictionary<string, object>();

                var response = await PostWeApiAsync<JObject>(
                    "/api/discovery/newAlbum",
                    payload,
                    autoConvertApiSegment: true);

                if (response["code"]?.Value<int>() != 200)
                {
                    System.Diagnostics.Debug.WriteLine($"[API] 获取新碟上架失败: {response["message"]}");
                    return new List<AlbumInfo>();
                }

                var albums = response["albums"] as JArray;
                if (albums == null)
                {
                    System.Diagnostics.Debug.WriteLine("[API] 新碟上架数据为空");
                    return new List<AlbumInfo>();
                }

                var result = new List<AlbumInfo>();
                foreach (var album in albums)
                {
                    var albumInfo = new AlbumInfo
                    {
                        Id = album["id"]?.Value<string>() ?? album["id"]?.Value<long>().ToString(),
                        Name = album["name"]?.Value<string>() ?? "未知专辑",
                        Artist = ResolveAlbumArtistName(album),
                        PicUrl = album["picUrl"]?.Value<string>() ?? "",
                        PublishTime = FormatAlbumPublishDate(album["publishTime"]),
                        TrackCount = ResolveAlbumTrackCount(album),
                        Description = ResolveAlbumDescription(album)
                    };

                    result.Add(albumInfo);
                }

                System.Diagnostics.Debug.WriteLine($"[API] 成功获取 {result.Count} 个新碟");
                return result;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 获取新碟上架异常: {ex.Message}");
                return new List<AlbumInfo>();
            }
        }

        #endregion

    }
}
