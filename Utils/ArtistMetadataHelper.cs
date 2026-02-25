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

        private static readonly Dictionary<int, (string DisplayName, string ApiToken)> NewAlbumAreaMappings = new Dictionary<int, (string, string)>
        {
            { 0, ("全部地区", "ALL") },
            { 7, ("华语", "ZH") },
            { 96, ("欧美", "EA") },
            { 8, ("日本", "JP") },
            { 16, ("韩国", "KR") }
        };

        private static readonly Dictionary<int, (string DisplayName, string ApiToken)> NewAlbumPeriodMappings = new Dictionary<int, (string, string)>
        {
            { 0, ("本周新碟", "week") },
            { 1, ("本月新碟", "month") }
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

        /// <summary>
        /// 获取新碟地区筛选选项列表。
        /// </summary>
        public static IReadOnlyList<ArtistCategoryOption> GetNewAlbumAreaOptions(bool includeAll = true)
        {
            IEnumerable<KeyValuePair<int, (string DisplayName, string ApiToken)>> items = NewAlbumAreaMappings;

            if (!includeAll)
            {
                items = items.Where(kv => kv.Key != 0);
            }

            return items
                .Select(kv => new ArtistCategoryOption { Code = kv.Key, DisplayName = kv.Value.DisplayName })
                .ToList();
        }

        /// <summary>
        /// 获取新碟时间筛选选项列表。
        /// </summary>
        public static IReadOnlyList<ArtistCategoryOption> GetNewAlbumPeriodOptions()
        {
            return NewAlbumPeriodMappings
                .Select(kv => new ArtistCategoryOption { Code = kv.Key, DisplayName = kv.Value.DisplayName })
                .ToList();
        }

        /// <summary>
        /// 将新碟地区代码转换为接口参数。
        /// </summary>
        public static string ResolveNewAlbumAreaApiToken(int areaCode)
        {
            return NewAlbumAreaMappings.TryGetValue(areaCode, out var mapping) ? mapping.ApiToken : "ALL";
        }

        /// <summary>
        /// 将新碟时间代码转换为内部标识。
        /// </summary>
        public static string ResolveNewAlbumPeriodApiToken(int periodCode)
        {
            return NewAlbumPeriodMappings.TryGetValue(periodCode, out var mapping) ? mapping.ApiToken : "month";
        }
    }
}
