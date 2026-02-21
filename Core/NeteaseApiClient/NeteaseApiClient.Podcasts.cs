using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using YTPlayer.Models;

#pragma warning disable CS8600, CS8601, CS8602, CS8603, CS8604, CS8625

namespace YTPlayer.Core
{
    public partial class NeteaseApiClient
    {
        #region 播客/电台

        /// <summary>
        /// 获取播客/电台分类列表。
        /// </summary>
        public async Task<List<PodcastCategoryInfo>> GetPodcastCategoriesAsync(CancellationToken cancellationToken = default)
        {
            await EnforceThrottleAsync("podcast:categories", TimeSpan.FromSeconds(1)).ConfigureAwait(false);

            var response = await PostWeApiAsync<JObject>(
                "/api/djradio/category/get",
                new Dictionary<string, object>(),
                cancellationToken: cancellationToken,
                autoConvertApiSegment: true);

            cancellationToken.ThrowIfCancellationRequested();

            var categoriesToken = response?["categories"] as JArray
                                  ?? response?["data"]?["categories"] as JArray;

            return ParsePodcastCategoryList(categoriesToken);
        }

        /// <summary>
        /// 获取指定分类下的热门播客/电台。
        /// </summary>
        public async Task<(List<PodcastRadioInfo> Podcasts, bool HasMore, int TotalCount)> GetPodcastsByCategoryAsync(int categoryId, int limit = 30, int offset = 0, CancellationToken cancellationToken = default)
        {
            if (categoryId <= 0)
            {
                return (new List<PodcastRadioInfo>(), false, 0);
            }

            limit = Math.Max(1, Math.Min(100, limit));
            offset = Math.Max(0, offset);

            await EnforceThrottleAsync($"podcast:cat:{categoryId}", TimeSpan.FromSeconds(1)).ConfigureAwait(false);

            var payload = new Dictionary<string, object>
            {
                { "cateId", categoryId },
                { "limit", limit },
                { "offset", offset }
            };

            var response = await PostWeApiAsync<JObject>(
                "/api/djradio/hot",
                payload,
                cancellationToken: cancellationToken,
                autoConvertApiSegment: true);

            cancellationToken.ThrowIfCancellationRequested();

            if (response["code"]?.Value<int>() != 200)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 获取播客分类 {categoryId} 热门列表失败: {response}");
                return (new List<PodcastRadioInfo>(), false, 0);
            }

            var radiosToken = response["djRadios"] as JArray ?? response["radios"] as JArray;
            var radios = ParsePodcastRadioList(radiosToken);
            int total = response["count"]?.Value<int>() ?? response["total"]?.Value<int>() ?? radios.Count;
            bool hasMore = response["hasMore"]?.Value<bool>() ?? (offset + radios.Count < total);

            return (radios, hasMore, total);
        }

        /// <summary>
        /// 获取播客/电台详情。
        /// </summary>
        public async Task<PodcastRadioInfo?> GetPodcastRadioDetailAsync(long radioId)
        {
            if (radioId <= 0)
            {
                return null;
            }

            var payload = new Dictionary<string, object>
            {
                { "id", radioId }
            };

            var response = await PostWeApiAsync<JObject>(
                "/api/djradio/v2/get",
                payload,
                autoConvertApiSegment: true);

            if (response["code"]?.Value<int>() != 200)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 获取播客详情失败: {response}");
                return null;
            }

            var radioToken = response["djRadio"] as JObject
                             ?? response["data"]?["djRadio"] as JObject
                             ?? response["data"] as JObject;

            return ParsePodcastRadio(radioToken);
        }

        /// <summary>
        /// 获取指定播客的节目列表。
        /// </summary>
        public async Task<(List<PodcastEpisodeInfo> Episodes, bool HasMore, int TotalCount)> GetPodcastEpisodesAsync(long radioId, int limit = 50, int offset = 0, bool asc = false)
        {
            if (radioId <= 0)
            {
                return (new List<PodcastEpisodeInfo>(), false, 0);
            }

            limit = Math.Max(1, Math.Min(100, limit));
            offset = Math.Max(0, offset);

            var payload = new Dictionary<string, object>
            {
                { "radioId", radioId },
                { "limit", limit },
                { "offset", offset },
                { "asc", asc }
            };

            var response = await PostWeApiAsync<JObject>(
                "/weapi/dj/program/byradio",
                payload);

            if (response["code"]?.Value<int>() != 200)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 获取播客节目失败: {response}");
                return (new List<PodcastEpisodeInfo>(), false, 0);
            }

            var programs = response["programs"] as JArray ?? response["program"] as JArray;
            var episodes = ParsePodcastEpisodeList(programs);
            bool hasMore = response["hasMore"]?.Value<bool>() ?? false;
            int total = response["count"]?.Value<int>() ?? (offset + episodes.Count);

            if (!hasMore && total > offset + episodes.Count)
            {
                hasMore = true;
            }

            return (episodes, hasMore, total);
        }

        /// <summary>
        /// 获取单个播客节目的详情。
        /// </summary>
        public async Task<PodcastEpisodeInfo?> GetPodcastEpisodeDetailAsync(long programId)
        {
            if (programId <= 0)
            {
                return null;
            }

            var payload = new Dictionary<string, object>
            {
                { "id", programId }
            };

            var response = await PostWeApiAsync<JObject>(
                "/api/dj/program/detail",
                payload,
                autoConvertApiSegment: true);

            if (response["code"]?.Value<int>() != 200)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 获取播客节目详情失败: {response}");
                return null;
            }

            var program = response["program"] as JObject ?? response["data"] as JObject;
            if (program == null)
            {
                return null;
            }

            var episodes = ParsePodcastEpisodeList(new JArray(program));
            return episodes.FirstOrDefault();
        }

        /// <summary>
        /// 收藏播客/电台。
        /// </summary>
        public Task<bool> SubscribePodcastAsync(long radioId)
        {
            return SetPodcastSubscriptionAsync(radioId, subscribe: true);
        }

        /// <summary>
        /// 取消收藏播客/电台。
        /// </summary>
        public Task<bool> UnsubscribePodcastAsync(long radioId)
        {
            return SetPodcastSubscriptionAsync(radioId, subscribe: false);
        }

        private async Task<bool> SetPodcastSubscriptionAsync(long radioId, bool subscribe)
        {
            if (radioId <= 0)
            {
                return false;
            }

            await EnforceThrottleAsync($"podcast:{radioId}", TimeSpan.FromSeconds(2)).ConfigureAwait(false);

            var payload = new Dictionary<string, object>
            {
                { "id", radioId }
            };

            string path = subscribe ? "/weapi/djradio/sub" : "/weapi/djradio/unsub";
            var response = await PostWeApiAsync<JObject>(path, payload);

            return response["code"]?.Value<int>() == 200;
        }

        #endregion

    }
}
