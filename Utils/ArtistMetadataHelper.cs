using System;
using System.Collections.Generic;
using System.Linq;
using YTPlayer.Models;

namespace YTPlayer.Utils
{
    /// <summary>
    /// 提供歌手分类及元数据的静态帮助方法。
    /// </summary>
    public static class ArtistMetadataHelper
    {
        private static readonly Dictionary<int, string> AreaMappings = new Dictionary<int, string>
        {
            { -1, "全部地区" },
            { 7, "华语" },
            { 96, "欧美" },
            { 8, "日本" },
            { 16, "韩国" },
            { 0, "其他" }
        };

        private static readonly Dictionary<int, string> TypeMappings = new Dictionary<int, string>
        {
            { -1, "全部类型" },
            { 1, "男歌手" },
            { 2, "女歌手" },
            { 3, "乐队/组合" }
        };

        /// <summary>
        /// 获取地区分类选项列表。
        /// </summary>
        public static IReadOnlyList<ArtistCategoryOption> GetAreaOptions(bool includeAll = true)
        {
            IEnumerable<KeyValuePair<int, string>> items = AreaMappings;

            if (!includeAll)
            {
                items = items.Where(kv => kv.Key != -1);
            }

            return items
                .OrderBy(kv => kv.Key == -1 ? int.MinValue : kv.Key)
                .Select(kv => new ArtistCategoryOption { Code = kv.Key, DisplayName = kv.Value })
                .ToList();
        }

        /// <summary>
        /// 获取歌手类型选项列表。
        /// </summary>
        public static IReadOnlyList<ArtistCategoryOption> GetTypeOptions(bool includeAll = true)
        {
            IEnumerable<KeyValuePair<int, string>> items = TypeMappings;

            if (!includeAll)
            {
                items = items.Where(kv => kv.Key != -1);
            }

            return items
                .OrderBy(kv => kv.Key == -1 ? int.MinValue : kv.Key)
                .Select(kv => new ArtistCategoryOption { Code = kv.Key, DisplayName = kv.Value })
                .ToList();
        }
    }
}
