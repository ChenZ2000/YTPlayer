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
        #region 歌单相关

        /// <summary>
        /// 获取歌单详情
        /// </summary>
        public async Task<PlaylistInfo> GetPlaylistDetailAsync(string playlistId)
        {
            // 尝试使用简化API
            if (UseSimplifiedApi)
            {
                try
                {
                    var parameters = new Dictionary<string, string>
                    {
                        { "id", playlistId }
                    };
                    var result = await GetSimplifiedApiAsync<JObject>("/playlist/detail", parameters);
                    return ParsePlaylistDetail(result["playlist"] as JObject);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[PlaylistDetail] Simplified API failed: {ex.Message}");
                }
            }

            // 使用加密API
            var payload = new Dictionary<string, object>
            {
                { "id", playlistId },
                { "n", 100000 },
                { "s", 8 }
            };

            var response = await PostWeApiAsync<JObject>("/v6/playlist/detail", payload);
            return ParsePlaylistDetail(response["playlist"] as JObject);
        }

        /// <summary>
        /// 获取歌单内的所有歌曲（参考 Python 版本 _fetch_playlist_via_weapi，11917-11966行）
        /// </summary>
        public async Task<List<SongInfo>> GetPlaylistSongsAsync(string playlistId, CancellationToken cancellationToken = default)
        {
            System.Diagnostics.Debug.WriteLine($"[API] 获取歌单歌曲: {playlistId}");

            try
            {
                // 先获取歌单基本信息（参考 Python 11918行）
                var infoData = new Dictionary<string, object>
                {
                    { "id", playlistId },
                    { "n", 1 },
                    { "s", 8 }
                };

                System.Diagnostics.Debug.WriteLine($"[API] 获取歌单详情...");
                var infoResponse = await PostWeApiAsync<JObject>("/v3/playlist/detail", infoData, cancellationToken: cancellationToken);

                // 检查返回码（参考 Python 11920行）
                int code = infoResponse["code"]?.Value<int>() ?? 0;
                if (code != 200)
                {
                    string msg = infoResponse["message"]?.Value<string>() ?? "未知错误";
                    throw new Exception($"获取歌单详情失败: code={code}, message={msg}");
                }

                var playlist = infoResponse["playlist"];
                if (playlist == null)
                {
                    throw new Exception("返回数据中没有playlist字段");
                }

                string playlistName = playlist["name"]?.Value<string>() ?? $"歌单 {playlistId}";
                int total = playlist["trackCount"]?.Value<int>() ?? 0;
                System.Diagnostics.Debug.WriteLine($"[API] 歌单名称: {playlistName}, 总歌曲数: {total}");

                // 检查私密歌单权限（参考 Python 11928-11930行）
                bool isPrivate = (playlist["privacy"]?.Value<int>() ?? 0) == 10;
                if (isPrivate)
                {
                    System.Diagnostics.Debug.WriteLine($"[API] 检测到私密歌单");
                    // TODO: 检查是否是创建者
                }

                if (total <= 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[API] 歌单为空");
                    return new List<SongInfo>();
                }

                // 直接使用 trackIds 批量获取（参考 Python 11956-11964行）
                // /weapi/playlist/track/all 接口在未登录状态下会被风控，已废弃
                System.Diagnostics.Debug.WriteLine($"[API] 开始通过 trackIds 获取歌曲详情（共 {total} 首）");

                var trackIds = playlist["trackIds"] as JArray;
                if (trackIds == null || trackIds.Count == 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[API ERROR] trackIds 为空");
                    return new List<SongInfo>();
                }

                // 提取所有歌曲ID
                var allIds = new List<string>();
                foreach (var tid in trackIds)
                {
                    string id = tid["id"]?.ToString();
                    if (!string.IsNullOrEmpty(id))
                    {
                        allIds.Add(id);
                    }
                }

                System.Diagnostics.Debug.WriteLine($"[API] 提取到 {allIds.Count} 个歌曲ID，开始批量获取详情");

                // 批量获取歌曲详情
                var allSongs = await GetSongsByIdsAsync(allIds, cancellationToken);

                System.Diagnostics.Debug.WriteLine($"[API] 歌单歌曲获取完成，共 {allSongs.Count}/{total} 首");
                return allSongs;
            }
            catch (OperationCanceledException)
            {
                System.Diagnostics.Debug.WriteLine("[API] 获取歌单歌曲操作被取消");
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API ERROR] 获取歌单歌曲异常: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[API ERROR] 堆栈: {ex.StackTrace}");
                throw;
            }
        }

        /// <summary>
        /// 批量获取歌曲详情（参考 Python 版本 _fetch_songs_by_ids，11967-11977行）
        /// 添加延迟避免触发风控限流，减小批次大小提高成功率
        /// </summary>
        public async Task<List<SongInfo>> GetSongsByIdsAsync(List<string> ids, CancellationToken cancellationToken = default)
        {
            var allSongs = new List<SongInfo>();
            // 减小批次大小到200，降低触发风控概率
            int step = 200;
            int batchNum = 0;

            System.Diagnostics.Debug.WriteLine($"[API] 开始批量获取 {ids.Count} 首歌曲详情，每批 {step} 首");

            for (int i = 0; i < ids.Count; i += step)
            {
                batchNum++;
                var batch = ids.Skip(i).Take(step).ToList();

                // 每批之间延迟 1.5 秒，降低风控风险
                if (i > 0)
                {
                    int delayMs = 1500;
                    System.Diagnostics.Debug.WriteLine($"[API] 等待 {delayMs}ms 避免限流...");
                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                }

                System.Diagnostics.Debug.WriteLine($"[API] 获取第 {batchNum} 批（{i + 1}-{Math.Min(i + step, ids.Count)}）...");

                var cJson = JsonConvert.SerializeObject(batch.Select(x => new { id = long.Parse(x) }), Formatting.None);
                var idsJson = JsonConvert.SerializeObject(batch);

                var data = new Dictionary<string, object>
                {
                    { "c", cJson },
                    { "ids", idsJson }
                };

                int retryCount = 0;
                bool success = false;

                // 添加重试机制（最多重试2次）
                while (retryCount < 3 && !success)
                {
                    try
                    {
                        var response = await PostWeApiAsync<JObject>("/song/detail", data, cancellationToken: cancellationToken);
                        var songs = response["songs"] as JArray;

                        if (songs != null && songs.Count > 0)
                        {
                            var parsed = ParseSongList(songs);
                            allSongs.AddRange(parsed);
                            System.Diagnostics.Debug.WriteLine($"[API] 第 {batchNum} 批成功获取 {parsed.Count} 首");
                            success = true;
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"[API] 第 {batchNum} 批返回空数据");
                            throw new Exception("返回空数据");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        System.Diagnostics.Debug.WriteLine("[API] 批量获取歌曲操作被取消");
                        throw;
                    }
                    catch (Exception ex)
                    {
                        retryCount++;
                        System.Diagnostics.Debug.WriteLine($"[API ERROR] 第 {batchNum} 批获取失败（重试 {retryCount}/3）: {ex.Message}");

                        if (retryCount < 3)
                        {
                            // 重试前等待更长时间
                            int retryDelay = 2000 * retryCount;
                            System.Diagnostics.Debug.WriteLine($"[API] 等待 {retryDelay}ms 后重试...");
                            await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }

                if (!success)
                {
                    System.Diagnostics.Debug.WriteLine($"[API ERROR] 第 {batchNum} 批最终失败，跳过该批次");
                }
            }

            System.Diagnostics.Debug.WriteLine($"[API] 批量获取完成，共获得 {allSongs.Count}/{ids.Count} 首歌曲");
            return allSongs;
        }

        /// <summary>
        /// 获取专辑详情（名称、歌手、封面等基础信息）
        /// </summary>
        public async Task<AlbumInfo?> GetAlbumDetailAsync(string albumId)
        {
            if (string.IsNullOrWhiteSpace(albumId))
            {
                return null;
            }

            try
            {
                string url = $"https://music.163.com/api/album/{albumId}";
                var response = await _httpClient.GetAsync(url).ConfigureAwait(false);
                var jsonString = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var json = JObject.Parse(jsonString);
                var albumToken = json["album"];
                if (albumToken == null)
                {
                    return null;
                }

                var album = new AlbumInfo
                {
                    Id = albumToken["id"]?.Value<string>() ?? albumToken["id"]?.Value<long>().ToString() ?? albumId,
                    Name = albumToken["name"]?.Value<string>() ?? $"专辑 {albumId}",
                    Artist = ResolveAlbumArtistName(albumToken),
                    PicUrl = albumToken["picUrl"]?.Value<string>() ?? string.Empty,
                    PublishTime = FormatAlbumPublishDate(albumToken["publishTime"]),
                    TrackCount = ResolveAlbumTrackCount(albumToken),
                    Description = ResolveAlbumDescription(albumToken)
                };

                var songsToken = json["songs"] as JArray ?? albumToken["songs"] as JArray;
                if (songsToken != null && songsToken.Count > 0)
                {
                    album.Songs = ParseSongList(songsToken);
                    if (album.TrackCount <= 0)
                    {
                        album.TrackCount = album.Songs.Count;
                    }
                }

                return album;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 获取专辑详情失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 获取专辑内的所有歌曲（参考 Python 版本 _fetch_album_detail，14999-15048行）
        /// </summary>
        public async Task<List<SongInfo>> GetAlbumSongsAsync(string albumId)
        {
            System.Diagnostics.Debug.WriteLine($"[API] 获取专辑歌曲: {albumId}");

            // 尝试第一个API
            try
            {
                string url = $"https://music.163.com/api/album/{albumId}";
                var response = await _httpClient.GetAsync(url);
                var jsonString = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var json = JObject.Parse(jsonString);

                var songs = json["songs"] as JArray ?? json["album"]?["songs"] as JArray;
                if (songs != null && songs.Count > 0)
                {
                    return ParseSongList(songs);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 获取专辑歌曲方法1失败: {ex.Message}");
            }

            // 尝试第二个API
            try
            {
                string url = $"https://music.163.com/api/album/detail?id={albumId}";
                var response = await _httpClient.GetAsync(url);
                var jsonString = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var json = JObject.Parse(jsonString);

                var songs = json["songs"] as JArray ?? json["album"]?["songs"] as JArray;
                if (songs != null && songs.Count > 0)
                {
                    return ParseSongList(songs);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 获取专辑歌曲方法2失败: {ex.Message}");
            }

            throw new Exception("无法获取专辑歌曲");
        }

        /// <summary>
        /// 获取专辑内的所有歌曲ID（优先使用加密接口，避免公开接口返回异常JSON）。
        /// </summary>
        public async Task<List<string>> GetAlbumSongIdsAsync(string albumId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(albumId))
            {
                return new List<string>();
            }

            JObject? response = null;

            try
            {
                response = await PostWeApiAsync<JObject>($"/api/v1/album/{albumId}", new Dictionary<string, object>(), cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 获取专辑歌曲ID（v1）失败: {ex.Message}");
            }

            if (response == null)
            {
                try
                {
                    var payload = new Dictionary<string, object>
                    {
                        { "id", albumId }
                    };
                    response = await PostWeApiAsync<JObject>("/api/album/v3/detail", payload, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[API] 获取专辑歌曲ID（v3）失败: {ex.Message}");
                }
            }

            var songsToken = response?["songs"] as JArray ?? response?["album"]?["songs"] as JArray;
            if (songsToken == null || songsToken.Count == 0)
            {
                return new List<string>();
            }

            List<string> ids = new List<string>(songsToken.Count);
            foreach (var songToken in songsToken)
            {
                string id = songToken?["id"]?.ToString();
                if (!string.IsNullOrWhiteSpace(id))
                {
                    ids.Add(id);
                }
            }
            return ids;
        }

        /// <summary>
        /// 歌单收藏/取消收藏（参考 Python 版本 _playlist_subscribe_weapi，6761-6775行）
        /// </summary>
        /// <param name="playlistId">歌单ID</param>
        /// <param name="subscribe">true=收藏，false=取消收藏</param>
        public async Task<bool> SubscribePlaylistAsync(string playlistId, bool subscribe)
        {
            try
            {
                await EnforceThrottleAsync($"playlist:{playlistId}", TimeSpan.FromSeconds(2)).ConfigureAwait(false);

                string action = subscribe ? "subscribe" : "unsubscribe";
                var payload = new Dictionary<string, object>
                {
                    { "id", playlistId },
                    { "t", subscribe ? 1 : 2 }
                };
                var response = await PostWeApiAsync<JObject>($"/playlist/{action}", payload);
                int code = response["code"]?.Value<int>() ?? -1;
                return code == 200;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 歌单{(subscribe ? "收藏" : "取消收藏")}失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 删除歌单（仅限用户本人创建的歌单）
        /// 参考: NeteaseCloudMusicApi/module/playlist_delete.js
        /// </summary>
        public async Task<bool> DeletePlaylistAsync(string playlistId)
        {
            if (string.IsNullOrWhiteSpace(playlistId))
            {
                return false;
            }

            try
            {
                // 删除歌单接口要求 os=pc
                UpsertCookie("os", "pc");

                var payload = new Dictionary<string, object>
                {
                    { "ids", $"[{playlistId}]" }
                };

                var response = await PostWeApiAsync<JObject>("/playlist/remove", payload);
                int code = response["code"]?.Value<int>() ?? -1;
                return code == 200;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 删除歌单失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 专辑收藏（参考 Python 版本 _subscribe_album，10224-10268行）
        /// </summary>
        public async Task<bool> SubscribeAlbumAsync(string albumId)
        {
            try
            {
                await EnforceThrottleAsync($"album:{albumId}", TimeSpan.FromSeconds(2)).ConfigureAwait(false);

                var payload = new Dictionary<string, object>
                {
                    { "id", albumId },
                    { "t", "1" }
                };
                var response = await PostWeApiAsync<JObject>("/album/sub", payload);
                int code = response["code"]?.Value<int>() ?? -1;
                return code == 200;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 专辑收藏失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 专辑取消收藏（参考 Python 版本 _unsubscribe_album，10271-10296行）
        /// </summary>
        public async Task<bool> UnsubscribeAlbumAsync(string albumId)
        {
            try
            {
                await EnforceThrottleAsync($"album:{albumId}", TimeSpan.FromSeconds(2)).ConfigureAwait(false);

                var payload = new Dictionary<string, object>
                {
                    { "id", albumId }
                };
                var response = await PostWeApiAsync<JObject>("/album/unsub", payload);
                int code = response["code"]?.Value<int>() ?? -1;
                return code == 200;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 专辑取消收藏失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 歌单添加歌曲（参考 Python 版本 _playlist_manipulate_tracks_weapi，14557-14568行）
        /// </summary>
        public async Task<bool> AddTracksToPlaylistAsync(string playlistId, string[] songIds)
        {
            try
            {
                var payload = new Dictionary<string, object>
                {
                    { "op", "add" },
                    { "pid", playlistId },
                    { "trackIds", $"[{string.Join(",", songIds)}]" }
                };
                var response = await PostWeApiAsync<JObject>("/playlist/manipulate/tracks", payload);
                int code = response["code"]?.Value<int>() ?? -1;
                return code == 200;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 添加歌曲到歌单失败: {ex.Message}");
                return false;
            }
        }

        private static bool TryBuildIdArrayPayload(IEnumerable<string> ids, out string payload)
        {
            payload = "[]";
            if (ids == null)
            {
                return false;
            }

            var list = new List<string>();
            foreach (var raw in ids)
            {
                var text = raw?.Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    return false;
                }
                if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value <= 0)
                {
                    return false;
                }
                list.Add(value.ToString(CultureInfo.InvariantCulture));
            }

            if (list.Count == 0)
            {
                return false;
            }

            payload = $"[{string.Join(",", list)}]";
            return true;
        }

        /// <summary>
        /// 从歌单中移除歌曲
        /// API: POST /api/playlist/manipulate/tracks
        /// 参考: NeteaseCloudMusicApi/module/playlist_tracks.js
        /// </summary>
        /// <param name="playlistId">歌单ID</param>
        /// <param name="songIds">歌曲ID数组</param>
        public async Task<bool> RemoveTracksFromPlaylistAsync(string playlistId, string[] songIds)
        {
            try
            {
                var payload = new Dictionary<string, object>
                {
                    { "op", "del" },
                    { "pid", playlistId },
                    { "trackIds", $"[{string.Join(",", songIds)}]" }
                };
                var response = await PostWeApiAsync<JObject>("/playlist/manipulate/tracks", payload);
                int code = response["code"]?.Value<int>() ?? -1;
                return code == 200;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 从歌单中移除歌曲失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 调整用户创建/收藏歌单列表顺序
        /// API: POST /api/playlist/order/update
        /// 参考: api-enhanced/module/playlist_order_update.js
        /// </summary>
        public async Task<bool> UpdatePlaylistOrderAsync(IEnumerable<string> playlistIds)
        {
            try
            {
                if (!TryBuildIdArrayPayload(playlistIds, out var idsPayload))
                {
                    System.Diagnostics.Debug.WriteLine("[API] 更新歌单顺序失败: ids 无效");
                    return false;
                }

                var payload = new Dictionary<string, object>
                {
                    { "ids", idsPayload }
                };
                var response = await PostWeApiAsync<JObject>("/playlist/order/update", payload);
                int code = response["code"]?.Value<int>() ?? -1;
                return code == 200;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 更新歌单顺序失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 调整歌单内歌曲顺序
        /// API: POST /api/playlist/manipulate/tracks (op=update)
        /// 参考: api-enhanced/module/song_order_update.js
        /// </summary>
        public async Task<bool> UpdatePlaylistTrackOrderAsync(string playlistId, IEnumerable<string> songIds)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(playlistId))
                {
                    System.Diagnostics.Debug.WriteLine("[API] 更新歌单歌曲顺序失败: playlistId 为空");
                    return false;
                }
                if (!TryBuildIdArrayPayload(songIds, out var idsPayload))
                {
                    System.Diagnostics.Debug.WriteLine("[API] 更新歌单歌曲顺序失败: ids 无效");
                    return false;
                }

                var payload = new Dictionary<string, object>
                {
                    { "pid", playlistId },
                    { "trackIds", idsPayload },
                    { "op", "update" }
                };
                var response = await PostWeApiAsync<JObject>("/playlist/manipulate/tracks", payload);
                int code = response["code"]?.Value<int>() ?? -1;
                return code == 200;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 更新歌单歌曲顺序失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 创建歌单
        /// API: POST /api/playlist/create
        /// 参考: NeteaseCloudMusicApi/module/playlist_create.js
        /// </summary>
        /// <param name="name">歌单名称</param>
        /// <param name="privacy">隐私设置：0=公开，10=隐私</param>
        /// <param name="type">歌单类型：NORMAL(默认) | VIDEO | SHARED</param>
        public async Task<PlaylistInfo?> CreatePlaylistAsync(string name, int privacy = 0, string type = "NORMAL")
        {
            try
            {
                var payload = new Dictionary<string, object>
                {
                    { "name", name },
                    { "privacy", privacy },
                    { "type", type }
                };

                var response = await PostWeApiAsync<JObject>("/playlist/create", payload, autoConvertApiSegment: true);
                int code = response?["code"]?.Value<int>() ?? -1;

                if (code != 200)
                {
                    System.Diagnostics.Debug.WriteLine($"[API] 创建歌单失败: code={code}, message={response?["message"] ?? response?["msg"]}");
                    return null;
                }

                var playlistToken = response?["playlist"] as JObject;
                if (playlistToken != null)
                {
                    var created = CreatePlaylistInfo(playlistToken);
                    if (string.IsNullOrWhiteSpace(created.Name))
                    {
                        created.Name = name;
                    }

                    PopulatePlaylistOwnershipDefaults(created, playlistToken);
                    return created;
                }

                string? playlistId = ExtractPlaylistId(response);
                if (!string.IsNullOrWhiteSpace(playlistId))
                {
                    try
                    {
                        var detailed = await GetPlaylistDetailAsync(playlistId);
                        if (detailed != null)
                        {
                            if (string.IsNullOrWhiteSpace(detailed.Name))
                            {
                                detailed.Name = name;
                            }

                            PopulatePlaylistOwnershipDefaults(detailed, null);
                            return detailed;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[API] 获取新建歌单详情失败: {ex.Message}");
                    }

                    var fallback = new PlaylistInfo
                    {
                        Id = playlistId,
                        Name = string.IsNullOrWhiteSpace(name) ? "新建歌单" : name.Trim(),
                        TrackCount = 0
                    };

                    PopulatePlaylistOwnershipDefaults(fallback, null);
                    return fallback;
                }

                System.Diagnostics.Debug.WriteLine("[API] 创建歌单响应缺少 playlist/id 字段，无法构建返回对象。");
                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 创建歌单失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 喜欢/取消喜欢歌曲（红心）
        /// API: POST /api/radio/like
        /// 参考: NeteaseCloudMusicApi/module/like.js
        /// </summary>
        /// <param name="songId">歌曲ID</param>
        /// <param name="like">true=喜欢，false=取消喜欢</param>
        public async Task<bool> LikeSongAsync(string songId, bool like)
        {
            try
            {
                await EnforceThrottleAsync($"like:{songId}", TimeSpan.FromSeconds(1)).ConfigureAwait(false);

                var payload = new Dictionary<string, object>
                {
                    { "alg", "itembased" },
                    { "trackId", songId },
                    { "like", like },
                    { "time", "3" }
                };

                var response = await PostWeApiAsync<JObject>("/radio/like", payload);
                int code = response["code"]?.Value<int>() ?? -1;

                System.Diagnostics.Debug.WriteLine($"[API] {(like ? "喜欢" : "取消喜欢")}歌曲 {songId}: code={code}");
                return code == 200;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] {(like ? "喜欢" : "取消喜欢")}歌曲失败: {ex.Message}");
                return false;
            }
        }

        #endregion

    }
}
