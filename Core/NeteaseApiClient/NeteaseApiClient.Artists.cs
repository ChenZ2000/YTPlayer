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
        #region 歌手相关

        /// <summary>
        /// 获取歌手详情。
        /// </summary>
        public async Task<ArtistDetail?> GetArtistDetailAsync(long artistId, bool includeIntroduction = true)
        {
            if (artistId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(artistId));
            }

            var payload = new Dictionary<string, object>
            {
                { "id", artistId }
            };

            JObject response;
            try
            {
                response = await PostWeApiAsync<JObject>(
                    "/api/artist/head/info/get",
                    payload,
                    autoConvertApiSegment: true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 获取歌手详情失败: {ex.Message}");
                throw;
            }

            if (response == null)
            {
                return null;
            }

            var dataNode = response["data"] as JObject ?? response["artist"] as JObject ?? response;
            var artistNode = dataNode?["artist"] as JObject ?? dataNode?["artistInfo"] as JObject ?? dataNode;

            var baseInfo = ParseArtistObject(artistNode) ?? new ArtistInfo { Id = artistId };

            if (baseInfo.Id <= 0)
            {
                baseInfo.Id = artistId;
            }

            var detail = new ArtistDetail
            {
                Id = baseInfo.Id,
                Name = baseInfo.Name,
                Alias = baseInfo.Alias,
                PicUrl = baseInfo.PicUrl,
                AreaCode = baseInfo.AreaCode,
                AreaName = baseInfo.AreaName,
                TypeCode = baseInfo.TypeCode,
                TypeName = baseInfo.TypeName,
                MusicCount = baseInfo.MusicCount,
                AlbumCount = baseInfo.AlbumCount,
                MvCount = baseInfo.MvCount,
                BriefDesc = baseInfo.BriefDesc,
                Description = baseInfo.Description,
                IsSubscribed = baseInfo.IsSubscribed,
                CoverImageUrl = artistNode?["cover"]?.Value<string>()
                    ?? artistNode?["coverUrl"]?.Value<string>()
                    ?? baseInfo.PicUrl,
                FollowerCount = artistNode?["fansGroup"]?["followCount"]?.Value<long>()
                    ?? artistNode?["fansCount"]?.Value<long>()
                    ?? artistNode?["followedCount"]?.Value<long>()
                    ?? dataNode?["fans"]?.Value<long>()
                    ?? 0
            };

            if (string.IsNullOrWhiteSpace(detail.Name))
            {
                detail.Name = artistNode?["name"]?.Value<string>() ?? dataNode?["name"]?.Value<string>() ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(detail.PicUrl))
            {
                detail.PicUrl = artistNode?["avatar"]?.Value<string>()
                    ?? artistNode?["avatarUrl"]?.Value<string>()
                    ?? detail.CoverImageUrl;
            }

            var identifyDesc = artistNode?["identifyDesc"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(identifyDesc))
            {
                detail.ExtraMetadata["认证信息"] = identifyDesc;
            }

            var companies = artistNode?["company"]?.Value<string>() ?? artistNode?["briefDescCompany"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(companies))
            {
                detail.ExtraMetadata["经纪公司"] = companies;
            }

            var birthTimestamp = artistNode?["birth"]?.Value<long?>() ?? dataNode?["birth"]?.Value<long?>();
            if (birthTimestamp.HasValue && birthTimestamp.Value > 0)
            {
                detail.ExtraMetadata["出生日期"] = DateTimeOffset.FromUnixTimeMilliseconds(birthTimestamp.Value)
                    .DateTime.ToString("yyyy-MM-dd");
            }

            if (includeIntroduction)
            {
                try
                {
                    var (briefDesc, fullDesc, sections) = await FetchArtistIntroductionAsync(artistId);

                    if (!string.IsNullOrWhiteSpace(briefDesc))
                    {
                        detail.BriefDesc = NormalizeSummary(briefDesc);
                    }

                    if (!string.IsNullOrWhiteSpace(fullDesc))
                    {
                        detail.Description = fullDesc;
                    }

                    if (sections != null && sections.Count > 0)
                    {
                        detail.Introductions = sections;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[API] 获取歌手介绍失败: {ex.Message}");
                }
            }

            return detail;
        }

        /// <summary>
        /// 获取歌手介绍信息。
        /// </summary>
        private async Task<(string BriefDesc, string Description, List<ArtistIntroductionSection> Sections)> FetchArtistIntroductionAsync(long artistId)
        {
            var payload = new Dictionary<string, object>
            {
                { "id", artistId }
            };

            var response = await PostWeApiAsync<JObject>("/artist/introduction", payload);

            if (response == null)
            {
                return (string.Empty, string.Empty, new List<ArtistIntroductionSection>());
            }

            string briefDesc = response["briefDesc"]?.Value<string>() ?? string.Empty;
            var sections = ParseArtistIntroductionSections(response["introduction"] as JArray);
            string description = BuildIntroductionSummary(sections);

            if (string.IsNullOrWhiteSpace(description))
            {
                description = NormalizeDescription(response["txt"]?.Value<string>(), briefDesc);
            }

            if (string.IsNullOrWhiteSpace(description))
            {
                description = NormalizeDescription(briefDesc);
            }

            return (briefDesc, description, sections);
        }

        /// <summary>
        /// 获取歌手热门 50 首歌曲。
        /// </summary>
        public async Task<List<SongInfo>> GetArtistTopSongsAsync(long artistId)
        {
            if (artistId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(artistId));
            }

            var payload = new Dictionary<string, object>
            {
                { "id", artistId }
            };

            var response = await PostWeApiAsync<JObject>(
                "/api/artist/top/song",
                payload,
                autoConvertApiSegment: true);
            return ParseSongList(response?["songs"] as JArray);
        }

        /// <summary>
        /// 分页获取歌手歌曲列表。
        /// </summary>
        public async Task<(List<SongInfo> Songs, bool HasMore, int TotalCount)> GetArtistSongsAsync(long artistId, int limit = 50, int offset = 0, string order = "hot")
        {
            if (artistId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(artistId));
            }

            var payload = new Dictionary<string, object>
            {
                { "id", artistId },
                { "private_cloud", "true" },
                { "work_type", 1 },
                { "order", string.IsNullOrWhiteSpace(order) ? "hot" : order },
                { "offset", offset },
                { "limit", limit }
            };

            var response = await PostWeApiAsync<JObject>(
                "/api/v1/artist/songs",
                payload,
                autoConvertApiSegment: true);

            var songs = ParseSongList(response?["songs"] as JArray);
            bool hasMore = response?["more"]?.Value<bool>() ?? response?["hasMore"]?.Value<bool>() ?? false;
            int totalCount = response?["total"]?.Value<int>() ?? response?["songCount"]?.Value<int>() ?? (offset + songs.Count + (hasMore ? 1 : 0));

            return (songs, hasMore, totalCount);
        }

        /// <summary>
        /// 分页获取歌手专辑列表。
        /// </summary>
        public async Task<(List<AlbumInfo> Albums, bool HasMore, int TotalCount)> GetArtistAlbumsAsync(long artistId, int limit = 30, int offset = 0)
        {
            if (artistId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(artistId));
            }

            var payload = new Dictionary<string, object>
            {
                { "limit", limit },
                { "offset", offset },
                { "total", true }
            };

            var response = await PostWeApiAsync<JObject>($"/artist/albums/{artistId}", payload);

            var albums = ParseAlbumList(response?["hotAlbums"] as JArray ?? response?["albums"] as JArray);
            bool hasMore = response?["more"]?.Value<bool>() ?? response?["hasMore"]?.Value<bool>() ?? ((offset + albums.Count) < (response?["albumCount"]?.Value<int>() ?? 0));
            int totalCount = response?["albumCount"]?.Value<int>() ?? response?["total"]?.Value<int>() ?? (offset + albums.Count + (hasMore ? 1 : 0));

            return (albums, hasMore, totalCount);
        }

        /// <summary>
        /// 获取已收藏的歌手列表。
        /// </summary>
        public async Task<SearchResult<ArtistInfo>> GetArtistSubscriptionsAsync(int limit = 25, int offset = 0)
        {
            var payload = new Dictionary<string, object>
            {
                { "limit", limit },
                { "offset", offset },
                { "total", true }
            };

            var response = await PostWeApiAsync<JObject>("/artist/sublist", payload);

            var dataNode = response?["data"] as JObject ?? response;
            var artists = ParseArtistList(dataNode?["artists"] as JArray ?? dataNode?["list"] as JArray ?? dataNode?["data"] as JArray);
            int totalCount = dataNode?["artistCount"]?.Value<int>() ?? response?["count"]?.Value<int>() ?? artists.Count;

            return new SearchResult<ArtistInfo>(artists, totalCount, offset, limit, response);
        }

        /// <summary>
        /// 收藏或取消收藏歌手。
        /// </summary>
        public async Task<bool> SetArtistSubscriptionAsync(long artistId, bool subscribe)
        {
            if (artistId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(artistId));
            }

            var payload = new Dictionary<string, object>
            {
                { "artistId", artistId.ToString() },
                { "artistIds", $"[{artistId}]" }
            };

            string endpoint = subscribe ? "/artist/sub" : "/artist/unsub";
            var response = await PostWeApiAsync<JObject>(endpoint, payload);
            int code = response?["code"]?.Value<int>() ?? -1;
            return code == 200;
        }

        /// <summary>
        /// 根据分类获取歌手列表。
        /// </summary>
        public async Task<SearchResult<ArtistInfo>> GetArtistsByCategoryAsync(int typeCode, int areaCode, int limit = 30, int offset = 0, int? initial = null)
        {
            var payload = new Dictionary<string, object>
            {
                { "type", typeCode },
                { "area", areaCode },
                { "limit", limit },
                { "offset", offset },
                { "total", true }
            };

            if (initial.HasValue)
            {
                payload["initial"] = initial.Value;
            }

            var response = await PostWeApiAsync<JObject>(
                "/api/v1/artist/list",
                payload,
                autoConvertApiSegment: true);

            var artists = ParseArtistList(response?["artists"] as JArray ?? response?["list"] as JArray);
            int totalCount = response?["total"]?.Value<int>() ?? response?["count"]?.Value<int>() ?? artists.Count;
            bool hasMore = response?["more"]?.Value<bool>() ?? false;
            if (totalCount <= 0)
            {
                totalCount = offset + artists.Count;
            }
            if (hasMore && totalCount <= offset + artists.Count)
            {
                totalCount = offset + Math.Max(limit, artists.Count) + 1;
            }

            return new SearchResult<ArtistInfo>(artists, totalCount, offset, limit, response);
        }

        #endregion

    }
}
