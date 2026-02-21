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
        #region 歌词相关

        /// <summary>
        /// 获取歌词
        /// </summary>
        public async Task<LyricInfo> GetLyricsAsync(string songId)
        {
            async Task<LyricInfo> TrySimplifiedAsync()
            {
                try
                {
                    var parameters = new Dictionary<string, string>
                    {
                        { "id", songId }
                    };
                    var result = await GetSimplifiedApiAsync<JObject>("/lyric", parameters);
                    var lyric = ParseLyric(result);
                    if (HasLyricContent(lyric))
                    {
                        System.Diagnostics.Debug.WriteLine("[Lyrics] 使用公共API获取歌词成功。");
                    }
                    return lyric;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Lyrics] 公共API获取歌词失败: {ex.Message}");
                    return null;
                }
            }

            LyricInfo lyricInfo = null;
            bool simplifiedAttempted = false;

            // 1. 先尝试简化API（当启用或后续需要兜底时）
            if (UseSimplifiedApi)
            {
                simplifiedAttempted = true;
                lyricInfo = await TrySimplifiedAsync();
                if (HasLyricContent(lyricInfo))
                {
                    return lyricInfo;
                }
            }

            // 2. 尝试 WEAPI（请求所有类型的歌词，包括逐字歌词）
            try
            {
                var payload = new Dictionary<string, object>
                {
                    { "id", songId },
                    { "lv", -1 },    // lrc version
                    { "tv", -1 },    // translation version
                    { "rv", -1 },    // roma version
                    { "yv", -1 }     // yrc version (逐字歌词)
                };

                var response = await PostWeApiAsync<JObject>("/song/lyric", payload);
                lyricInfo = ParseLyric(response);
                if (HasLyricContent(lyricInfo))
                {
                    return lyricInfo;
                }

                System.Diagnostics.Debug.WriteLine("[Lyrics] WEAPI 返回空歌词内容，准备使用公共API兜底。");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Lyrics] WEAPI 获取歌词失败: {ex.Message}");
            }

            // 3. 如果尚未尝试简化API，则作为兜底再次尝试
            if (!simplifiedAttempted)
            {
                lyricInfo = await TrySimplifiedAsync();
                if (HasLyricContent(lyricInfo))
                {
                    return lyricInfo;
                }
            }

            // 最终返回（可能为空，调用方需自行处理）
            return lyricInfo ?? new LyricInfo();
        }

        #endregion

    }
}
