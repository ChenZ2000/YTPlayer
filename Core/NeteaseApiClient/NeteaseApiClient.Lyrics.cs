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
using YTPlayer.Core.Lyrics;
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
            async Task<LyricInfo?> TrySimplifiedEndpointAsync(string endpoint)
            {
                try
                {
                    var parameters = new Dictionary<string, string>
                    {
                        { "id", songId }
                    };
                    var result = await GetSimplifiedApiAsync<JObject>(endpoint, parameters);
                    var lyric = ParseLyric(result);
                    if (HasLyricContent(lyric))
                    {
                        System.Diagnostics.Debug.WriteLine($"[Lyrics] 使用公共API获取歌词成功: {endpoint}");
                    }
                    return lyric;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Lyrics] 公共API获取歌词失败({endpoint}): {ex.Message}");
                    return null;
                }
            }

            async Task<LyricInfo?> TrySimplifiedAsync()
            {
                // 新接口优先（包含 yrc / ytlrc 等多语言字段）
                var lyric = await TrySimplifiedEndpointAsync("/lyric_new").ConfigureAwait(false);
                if (HasLyricContent(lyric))
                {
                    return lyric;
                }

                // 兼容旧接口兜底
                return await TrySimplifiedEndpointAsync("/lyric").ConfigureAwait(false);
            }

            async Task<LyricInfo?> TryWeApiAsync(string endpoint, Dictionary<string, object> payload)
            {
                try
                {
                    var response = await PostWeApiAsync<JObject>(endpoint, payload).ConfigureAwait(false);
                    var lyric = ParseLyric(response);
                    if (HasLyricContent(lyric))
                    {
                        System.Diagnostics.Debug.WriteLine($"[Lyrics] WEAPI 获取歌词成功: {endpoint}");
                        return lyric;
                    }

                    System.Diagnostics.Debug.WriteLine($"[Lyrics] WEAPI 返回空歌词: {endpoint}");
                    return lyric;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Lyrics] WEAPI 获取歌词失败({endpoint}): {ex.Message}");
                    return null;
                }
            }

            LyricInfo? lyricInfo = null;
            bool simplifiedAttempted = false;

            // 1. 先尝试简化API（当启用或后续需要兜底时）
            if (UseSimplifiedApi)
            {
                simplifiedAttempted = true;
                lyricInfo = await TrySimplifiedAsync().ConfigureAwait(false);
                if (HasLyricContent(lyricInfo))
                {
                    return lyricInfo;
                }
            }

            // 2. 尝试 WEAPI v1（新版歌词接口，支持逐字/多语言）
            {
                var payloadV1 = new Dictionary<string, object>
                {
                    { "id", songId },
                    { "cp", false },
                    { "tv", 0 },
                    { "lv", 0 },
                    { "rv", 0 },
                    { "kv", 0 },
                    { "yv", 0 },
                    { "ytv", 0 },
                    { "yrv", 0 }
                };

                lyricInfo = await TryWeApiAsync("/song/lyric/v1", payloadV1).ConfigureAwait(false);
                if (HasLyricContent(lyricInfo))
                {
                    return lyricInfo;
                }
            }

            // 3. 兼容旧版 WEAPI 歌词接口
            {
                var payload = new Dictionary<string, object>
                {
                    { "id", songId },
                    { "lv", -1 },    // lrc version
                    { "tv", -1 },    // translation version
                    { "rv", -1 },    // roma version
                    { "kv", -1 },    // karaoke version
                    { "yv", -1 }     // yrc version (逐字歌词)
                };

                lyricInfo = await TryWeApiAsync("/song/lyric", payload).ConfigureAwait(false);
                if (HasLyricContent(lyricInfo))
                {
                    return lyricInfo;
                }
            }

            // 4. 如果尚未尝试简化API，则作为兜底再次尝试
            if (!simplifiedAttempted)
            {
                lyricInfo = await TrySimplifiedAsync().ConfigureAwait(false);
                if (HasLyricContent(lyricInfo))
                {
                    return lyricInfo;
                }
            }

            // 最终返回（可能为空，调用方需自行处理）
            return lyricInfo ?? new LyricInfo();
        }

        public async Task<LoadedLyricsContext?> GetResolvedLyricsAsync(
            string songId,
            IEnumerable<string>? overrideSelectedLanguageKeys = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(songId))
            {
                return null;
            }

            var lyricInfo = await GetLyricsAsync(songId).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (lyricInfo == null)
            {
                return null;
            }

            var profile = LyricLanguagePipeline.BuildProfile(songId, lyricInfo);
            if (!profile.HasLyrics)
            {
                return null;
            }

            var persistedSelection = overrideSelectedLanguageKeys?.ToList() ??
                                     GetSongLyricLanguagePreference(songId).ToList();
            var selectedKeys = LyricLanguagePipeline.NormalizeSelection(profile, persistedSelection);

            // 清理过期/非法偏好，保证 account.json 仅保留有效差异数据。
            if (persistedSelection.Count > 0 &&
                !persistedSelection.SequenceEqual(selectedKeys, StringComparer.OrdinalIgnoreCase))
            {
                SetSongLyricLanguagePreference(songId, selectedKeys, profile.DefaultLanguageKeys);
            }

            var lyricsData = LyricLanguagePipeline.BuildLyricsData(songId, profile, selectedKeys);
            if (lyricsData == null || lyricsData.IsEmpty)
            {
                return null;
            }

            var exportContent = LyricLanguagePipeline.BuildExportLyricContent(songId, profile, selectedKeys);
            return new LoadedLyricsContext(songId, lyricInfo, profile, selectedKeys, lyricsData, exportContent);
        }

        #endregion

    }
}
