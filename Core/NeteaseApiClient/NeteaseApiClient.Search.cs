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
        #region 搜索相关

        /// <summary>
        /// 搜索歌曲（使用 NodeJS 云音乐 API 同步的 EAPI 接口）。
        /// </summary>
        public async Task<SearchResult<SongInfo>> SearchSongsAsync(string keyword, int limit = 30, int offset = 0, CancellationToken cancellationToken = default)
        {
            System.Diagnostics.Debug.WriteLine($"[API] 搜索歌曲: {keyword}, limit={limit}, offset={offset}");

            try
            {
                var result = await ExecuteSearchRequestAsync(keyword, SearchResourceType.Song, limit, offset, cancellationToken);
                var songs = ParseSongList(result?["songs"] as JArray);
                int totalCount = ResolveTotalCount(result, SearchResourceType.Song, offset, songs.Count);

                System.Diagnostics.Debug.WriteLine($"[API] 搜索成功，返回 {songs.Count} 首歌曲, total={totalCount}");
                return new SearchResult<SongInfo>(songs, totalCount, offset, limit, result);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 搜索失败: {ex.GetType().Name}: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[API] 堆栈: {ex.StackTrace}");
                throw;
            }
        }

        /// <summary>
        /// 搜索歌单（使用 NodeJS 云音乐 API 同步的 EAPI 接口）。
        /// </summary>
        public async Task<SearchResult<PlaylistInfo>> SearchPlaylistsAsync(string keyword, int limit = 30, int offset = 0, CancellationToken cancellationToken = default)
        {
            System.Diagnostics.Debug.WriteLine($"[API] 搜索歌单: {keyword}, limit={limit}, offset={offset}");

            var result = await ExecuteSearchRequestAsync(keyword, SearchResourceType.Playlist, limit, offset, cancellationToken);
            var playlists = ParsePlaylistList(result?["playlists"] as JArray);
            int totalCount = ResolveTotalCount(result, SearchResourceType.Playlist, offset, playlists.Count);

            return new SearchResult<PlaylistInfo>(playlists, totalCount, offset, limit, result);
        }

        /// <summary>
        /// 搜索专辑（使用 NodeJS 云音乐 API 同步的 EAPI 接口）。
        /// </summary>
        public async Task<SearchResult<AlbumInfo>> SearchAlbumsAsync(string keyword, int limit = 30, int offset = 0, CancellationToken cancellationToken = default)
        {
            System.Diagnostics.Debug.WriteLine($"[API] 搜索专辑: {keyword}, limit={limit}, offset={offset}");

            var result = await ExecuteSearchRequestAsync(keyword, SearchResourceType.Album, limit, offset, cancellationToken);
            var albums = ParseAlbumList(result?["albums"] as JArray);
            int totalCount = ResolveTotalCount(result, SearchResourceType.Album, offset, albums.Count);

            return new SearchResult<AlbumInfo>(albums, totalCount, offset, limit, result);
        }

        /// <summary>
        /// 搜索歌手。
        /// </summary>
        public async Task<SearchResult<ArtistInfo>> SearchArtistsAsync(string keyword, int limit = 30, int offset = 0, CancellationToken cancellationToken = default)
        {
            System.Diagnostics.Debug.WriteLine($"[API] 搜索歌手: {keyword}, limit={limit}, offset={offset}");

            var result = await ExecuteSearchRequestAsync(keyword, SearchResourceType.Artist, limit, offset, cancellationToken);
            var artists = ParseArtistList(result?["artists"] as JArray);
            int totalCount = ResolveTotalCount(result, SearchResourceType.Artist, offset, artists.Count);

            return new SearchResult<ArtistInfo>(artists, totalCount, offset, limit, result);
        }

        /// <summary>
        /// 搜索播客/电台。
        /// </summary>
        public async Task<SearchResult<PodcastRadioInfo>> SearchPodcastsAsync(string keyword, int limit = 30, int offset = 0, CancellationToken cancellationToken = default)
        {
            System.Diagnostics.Debug.WriteLine($"[API] 搜索播客: {keyword}, limit={limit}, offset={offset}");

            var result = await ExecuteSearchRequestAsync(keyword, SearchResourceType.Radio, limit, offset, cancellationToken);
            var radiosToken = result?["djRadios"] as JArray
                               ?? result?["djRadioes"] as JArray
                               ?? (result?["djRadioResult"]?["djRadios"] as JArray)
                               ?? result?["radios"] as JArray;

            if (radiosToken == null)
            {
                var nested = result?["djRadios"];
                if (nested is JObject nestedObj && nestedObj.TryGetValue("items", out var itemsToken) && itemsToken is JArray nestedArray)
                {
                    radiosToken = nestedArray;
                }
            }

            var radios = ParsePodcastRadioList(radiosToken);
            int totalCount = ResolveTotalCount(result, SearchResourceType.Radio, offset, radios.Count);

            return new SearchResult<PodcastRadioInfo>(radios, totalCount, offset, limit, result);
        }

        /// <summary>
        /// 听歌识曲：调用增强 API /audio/match。
        /// </summary>
        public async Task<List<SongInfo>> RecognizeSongAsync(string audioFingerprint, int durationSeconds, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(audioFingerprint))
            {
                throw new ArgumentException("音频指纹不能为空", nameof(audioFingerprint));
            }

            durationSeconds = Math.Max(1, Math.Min(30, durationSeconds));

            // 优先使用官方 EAPI 识曲接口，避免依赖外部节点服务
            JObject? obj = null;
            // 尝试官方公开接口（未加密 GET，与 api-enhanced 一致）
            try
            {
                var sessionId = Guid.NewGuid().ToString("N");
                var url =
                    $"https://interface.music.163.com/api/music/audio/match?sessionId={sessionId}&algorithmCode=shazam_v2&duration={durationSeconds}&rawdata={Uri.EscapeDataString(audioFingerprint)}&times=1&decrypt=1";

                var resp = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                var txt = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                obj = JsonConvert.DeserializeObject<JObject>(txt);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Recognition] interface.music GET match failed: {ex.Message}");
            }

            try
            {
                if (obj == null)
                {
                    var eapiPayload = new Dictionary<string, object?>
                    {
                    { "rawdata", audioFingerprint },
                    { "duration", durationSeconds },
                    { "times", 1 },
                    { "algorithmCode", "shazam_v2" },
                    { "from", "recognize-song" },
                    { "sessionId", Guid.NewGuid().ToString("N") },
                    { "verifyId", 1 },
                    { "os", "pc" }
                    };

                    obj = await PostEApiAsync<JObject>("/api/music/audio/match", eapiPayload, useIosHeaders: true, skipErrorHandling: true)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (IsConnectionRefused(ex as HttpRequestException))
            {
                // 忽略，尝试简化接口
                System.Diagnostics.Debug.WriteLine($"[Recognition] EAPI match connection refused, fallback. {ex.Message}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Recognition] EAPI match failed: {ex.Message}");
            }

            // 备选：使用简化 API（可配置）作为兜底
            if (obj == null)
            {
                string configuredBase = string.IsNullOrWhiteSpace(_config?.RecognitionApiBaseUrl)
                    ? SIMPLIFIED_API_BASE
                    : _config.RecognitionApiBaseUrl.TrimEnd('/');

                async Task<JObject?> SendAsync(string baseUrl)
                {
                    string url = $"{baseUrl}/audio/match?duration={durationSeconds}&audioFP={Uri.EscapeDataString(audioFingerprint)}";

                    using var request = new HttpRequestMessage(HttpMethod.Post, url);
                    request.Headers.Referrer = MUSIC_URI;

                    var response = await _simplifiedClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    var jsonText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return JsonConvert.DeserializeObject<JObject>(jsonText);
                }

                try
                {
                    obj = await SendAsync(configuredBase).ConfigureAwait(false);
                }
                catch (HttpRequestException hre) when (IsConnectionRefused(hre) &&
                    !string.Equals(configuredBase, SIMPLIFIED_API_BASE, StringComparison.OrdinalIgnoreCase))
                {
                    obj = await SendAsync(SIMPLIFIED_API_BASE).ConfigureAwait(false);
                }
            }

            if (obj == null)
            {
                throw new InvalidOperationException("识曲接口无有效响应");
            }

            var resultArray = obj?["data"]?["result"] as JArray;
            if (resultArray == null)
            {
                return new List<SongInfo>();
            }

            var songTokens = new JArray();
            foreach (var item in resultArray)
            {
                var songObj = item?["song"];
                if (songObj != null)
                {
                    songTokens.Add(songObj);
                }
            }

            var songs = ParseSongList(songTokens);
            int index = 0;
            foreach (var item in resultArray)
            {
                if (index >= songs.Count)
                {
                    break;
                }

                var song = songs[index];
                song.MatchStartMs = item?["startTime"]?.Value<long?>();
                song.MatchScore = item?["score"]?.Value<double?>();
                song.ViewSource = "listen-match";
                index++;
            }

            return songs;
        }

        private static bool IsConnectionRefused(HttpRequestException ex)
        {
            if (ex == null) return false;

            Exception? current = ex;
            while (current != null)
            {
                if (current is SocketException sockEx &&
                    sockEx.SocketErrorCode == SocketError.ConnectionRefused)
                {
                    return true;
                }

                if (current is WebException webEx &&
                    webEx.Status == WebExceptionStatus.ConnectFailure &&
                    webEx.InnerException is SocketException innerSock &&
                    innerSock.SocketErrorCode == SocketError.ConnectionRefused)
                {
                    return true;
                }

                current = current.InnerException;
            }

            return false;
        }

        /// <summary>
        /// 调用搜索接口，自动处理简化API与官方API切换。
        /// </summary>
        private async Task<JObject> ExecuteSearchRequestAsync(string keyword, SearchResourceType resourceType, int limit, int offset, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(keyword))
            {
                return new JObject();
            }

            string typeCode = ((int)resourceType).ToString();

            if (UseSimplifiedApi)
            {
                try
                {
                    var simplifiedParameters = new Dictionary<string, string>
                    {
                        { "keywords", keyword },
                        { "type", typeCode },
                        { "limit", limit.ToString() },
                        { "offset", offset.ToString() }
                    };

                    System.Diagnostics.Debug.WriteLine($"[API] 通过简化接口搜索: type={typeCode}, keyword={keyword}");
                    var simplifiedResponse = await GetSimplifiedApiAsync<JObject>("/search", simplifiedParameters, cancellationToken);
                    if (simplifiedResponse?["result"] is JObject simplifiedResult)
                    {
                        return simplifiedResult;
                    }

                    System.Diagnostics.Debug.WriteLine("[API] 简化接口结果为空或类型错误，切换到官方接口");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[API] 简化接口搜索失败，将使用官方接口: {ex.Message}");
                }
            }

            var payload = new Dictionary<string, object>
            {
                { "s", keyword },
                { "type", (int)resourceType },
                { "limit", limit },
                { "offset", offset },
                { "total", true }
            };

            // 优先使用 WEAPI 官方接口（与移动端一致），失败时再回退到 EAPI
            try
            {
                // 对标官方 Node 实现（module/search.js）：使用 weapi/search/get
                var weapiPayload = new Dictionary<string, object>
                {
                    { "s", keyword },
                    { "type", (int)resourceType },
                    { "limit", limit },
                    { "offset", offset },
                    { "total", true }
                };

                // 这两个字段会让搜索结果包含高亮标记，与官方客户端行为一致
                weapiPayload["hlpretag"] = "<span class=\"s-fc7\">";
                weapiPayload["hlposttag"] = "</span>";

                var weapiResponse = await PostWeApiAsync<JObject>("/search/get", weapiPayload, cancellationToken: cancellationToken);
                if (weapiResponse?["result"] is JObject weapiResult)
                {
                    return weapiResult;
                }

                System.Diagnostics.Debug.WriteLine("[API] WEAPI 搜索响应缺少 result 节点，尝试 EAPI 回退。");
            }
            catch (Exception weapiEx)
            {
                System.Diagnostics.Debug.WriteLine($"[API] WEAPI 搜索失败，尝试 EAPI 回退: {weapiEx.Message}");
            }

            var response = await PostEApiAsync<JObject>("/api/cloudsearch/pc", payload);
            if (response?["result"] is JObject result)
            {
                return result;
            }

            System.Diagnostics.Debug.WriteLine("[API] 官方搜索响应缺少 result 节点，返回空对象");
            return new JObject();
        }

        /// <summary>
        /// 提取搜索结果的总数量，若接口未返回则根据当前页估算。
        /// </summary>
        private static int ResolveTotalCount(JObject result, SearchResourceType resourceType, int offset, int itemsCount)
        {
            if (result == null)
            {
                return offset + itemsCount;
            }

            foreach (var propertyName in GetCountPropertyCandidates(resourceType))
            {
                var token = result[propertyName];
                if (token != null && token.Type == JTokenType.Integer)
                {
                    long reportedLong = token.Value<long>();
                    if (reportedLong >= 0)
                    {
                        int reported = reportedLong > int.MaxValue ? int.MaxValue : (int)reportedLong;
                        return Math.Max(reported, offset + itemsCount);
                    }
                }
            }

            return offset + itemsCount;
        }

        private static IEnumerable<string> GetCountPropertyCandidates(SearchResourceType resourceType)
        {
            switch (resourceType)
            {
                case SearchResourceType.Song:
                    yield return "songCount";
                    yield return "songsCount";
                    break;
                case SearchResourceType.Album:
                    yield return "albumCount";
                    break;
                case SearchResourceType.Playlist:
                    yield return "playlistCount";
                    break;
                case SearchResourceType.Artist:
                    yield return "artistCount";
                    break;
                case SearchResourceType.MV:
                    yield return "mvCount";
                    break;
                case SearchResourceType.Video:
                    yield return "videoCount";
                    break;
                case SearchResourceType.Radio:
                    yield return "djRadiosCount";
                    yield return "djRadioCount";
                    break;
                case SearchResourceType.Lyric:
                    yield return "lyricCount";
                    yield return "songCount";
                    break;
                case SearchResourceType.User:
                    yield return "userprofileCount";
                    yield return "userProfilesCount";
                    break;
            }

            yield return "totalCount";
            yield return "total";
            yield return "count";
        }

        #endregion

    }
}
