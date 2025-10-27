using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace YTPlayer.Models
{
    /// <summary>
    /// 搜索资源类型，对应网易云搜索接口的 type 参数。
    /// </summary>
    public enum SearchResourceType
    {
        Song = 1,
        Album = 10,
        Artist = 100,
        Playlist = 1000,
        User = 1002,
        MV = 1004,
        Lyric = 1006,
        Radio = 1009,
        Video = 1014
    }

    /// <summary>
    /// 搜索结果包装类，包含条目数据与分页信息。
    /// </summary>
    /// <typeparam name="T">结果条目的类型。</typeparam>
    public class SearchResult<T>
    {
        public SearchResult(List<T> items, int totalCount, int offset, int limit, JObject rawResult = null)
        {
            Items = items ?? new List<T>();
            Offset = Math.Max(offset, 0);
            Limit = Math.Max(limit, 0);
            int inferredTotal = Offset + Items.Count;
            TotalCount = Math.Max(totalCount, inferredTotal);
            RawResult = rawResult;
        }

        /// <summary>
        /// 结果条目列表。
        /// </summary>
        public List<T> Items { get; }

        /// <summary>
        /// 总条目数（服务器返回，如缺失则取当前页估算）。
        /// </summary>
        public int TotalCount { get; }

        /// <summary>
        /// 当前请求的偏移量。
        /// </summary>
        public int Offset { get; }

        /// <summary>
        /// 当前请求的限制数量。
        /// </summary>
        public int Limit { get; }

        /// <summary>
        /// 原始 result 节点，便于后续扩展或调试。
        /// </summary>
        public JObject RawResult { get; }

        /// <summary>
        /// 是否还有更多结果可分页。
        /// </summary>
        public bool HasMore => Offset + Items.Count < TotalCount;
    }
}

