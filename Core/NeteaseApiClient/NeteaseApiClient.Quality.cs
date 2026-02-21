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
        #region 音质辅助方法

        /// <summary>
        /// 音质映射（参考 Python 版本 quality_map，5742-5750行）
        /// </summary>
        public static readonly Dictionary<string, string> QualityMap = new Dictionary<string, string>
        {
            { "标准音质", "standard" },
            { "极高音质", "exhigh" },
            { "无损音质", "lossless" },
            { "Hi-Res音质", "hires" },
            { "高清环绕声", "jyeffect" },
            { "沉浸环绕声", "sky" },
            { "超清母带", "jymaster" }
        };

        /// <summary>
        /// 音质顺序（从低到高）
        /// </summary>
        public static readonly string[] QualityOrder = { "标准音质", "极高音质", "无损音质", "Hi-Res音质", "高清环绕声", "沉浸环绕声", "超清母带" };

        /// <summary>
        /// 根据音质代码获取显示名称（参考 Python 版本 _level_display_name，12620-12624行）
        /// </summary>
        public static string GetQualityDisplayName(string level)
        {
            if (string.IsNullOrEmpty(level))
                return "未知";

            foreach (var kvp in QualityMap)
            {
                if (kvp.Value == level)
                    return kvp.Key;
            }

            return level;
        }

        /// <summary>
        /// 根据显示名称获取QualityLevel枚举（参考 Python 版本 quality_map）
        /// </summary>
        public static QualityLevel GetQualityLevelFromName(string qualityName)
        {
            if (string.IsNullOrEmpty(qualityName) || !QualityMap.ContainsKey(qualityName))
                return QualityLevel.Standard;

            string code = QualityMap[qualityName];
            switch (code)
            {
                case "standard":
                    return QualityLevel.Standard;
                case "exhigh":
                    return QualityLevel.High;
                case "lossless":
                    return QualityLevel.Lossless;
                case "hires":
                    return QualityLevel.HiRes;
                case "jyeffect":
                    return QualityLevel.SurroundHD;
                case "sky":
                    return QualityLevel.Dolby;
                case "jymaster":
                    return QualityLevel.Master;
                default:
                    return QualityLevel.Standard;
            }
        }

        #endregion

    }
}
