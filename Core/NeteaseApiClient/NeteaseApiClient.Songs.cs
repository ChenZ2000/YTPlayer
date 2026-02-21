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
        #region 歌曲相关

        /// <summary>
        /// 根据音质级别获取编码类型（参考 Python 版本：_encode_type_for_level，12615-12618行）
        /// </summary>
        private static string GetEncodeType(string level)
        {
            // Python源码：
            // if level in ("standard", "higher", "exhigh", "medium"):
            //     return "mp3"
            // return "flac"

            if (level == "standard" || level == "higher" || level == "exhigh" || level == "medium")
            {
                return "mp3";
            }
            return "flac";
        }

        private static int GetBitrateForQualityLevel(QualityLevel quality)
        {
            switch (quality)
            {
                case QualityLevel.Standard:
                    return 128000;
                case QualityLevel.High:
                    return 320000;
                case QualityLevel.Lossless:
                    return 999000;
                case QualityLevel.HiRes:
                    return 2000000;
                case QualityLevel.SurroundHD:
                    return 2000000;
                case QualityLevel.Dolby:
                    return 3200000;
                case QualityLevel.Master:
                    return 4000000;
                default:
                    return 999000;
            }
        }

        /// <summary>
        /// 获取歌曲URL（完全基于Suxiaoqinx/Netease_url Python项目重写）
        /// 使用纯EAPI实现，简单直接
        /// </summary>
        /// <param name="ids">歌曲ID数组</param>
        /// <param name="quality">音质级别</param>
        /// <param name="skipAvailabilityCheck">跳过可用性检查（当已通过批量预检时）</param>
        public async Task<Dictionary<string, SongUrlInfo>> GetSongUrlAsync(string[] ids, QualityLevel quality = QualityLevel.Standard, bool skipAvailabilityCheck = false, CancellationToken cancellationToken = default)
        {
            if (ids == null || ids.Length == 0)
            {
                return new Dictionary<string, SongUrlInfo>();
            }

            var startTime = DateTime.UtcNow;
            System.Diagnostics.Debug.WriteLine($"[GetSongUrl] ⏱ 开始: IDs={string.Join(",", ids)}, quality={quality}, skipCheck={skipAvailabilityCheck}");

            string requestedLevel = GetQualityLevel(quality);
            string[] qualityOrder = { "jymaster", "sky", "jyeffect", "hires", "lossless", "exhigh", "standard" };
            var missingSongIds = new HashSet<string>(StringComparer.Ordinal);

            // ⭐ 可用性预检：仅在登录状态下启用，未登录时跳过以避免误判
            bool isLoggedIn = _authContext?.CurrentAccountState?.IsLoggedIn ?? false;
            bool shouldPrecheck = isLoggedIn && !skipAvailabilityCheck;
            if (shouldPrecheck)
            {
                var checkStart = DateTime.UtcNow;
                try
                {
                    System.Diagnostics.Debug.WriteLine($"[GetSongUrl] 开始可用性检查...");
                    var precheckMissing = await CheckSongsAvailabilityAsync(ids, quality, cancellationToken).ConfigureAwait(false);
                    var checkElapsed = (DateTime.UtcNow - checkStart).TotalMilliseconds;
                    System.Diagnostics.Debug.WriteLine($"[GetSongUrl] 可用性检查完成，耗时: {checkElapsed:F0}ms");
                    foreach (var missing in precheckMissing)
                    {
                        missingSongIds.Add(missing);
                    }
                    // 仅记录缺失，不立即抛出，后续仍尝试获取/降级
                }
                catch (SongResourceNotFoundException)
                {
                    // 记录但不立刻抛出，避免误判；后续获取仍会尝试
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[SongUrl] 资源存在性预检失败: {ex.Message}");
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[SongUrl] 跳过可用性检查（未登录或已通过批量预检）");
            }

            int startIndex = Array.IndexOf(qualityOrder, requestedLevel);
            if (startIndex == -1)
            {
                startIndex = qualityOrder.Length - 1;
            }

            Exception lastException = null;
            bool simplifiedAttempted = false;

            if (!UsePersonalCookie)
            {
                simplifiedAttempted = true;
                try
                {
                    System.Diagnostics.Debug.WriteLine("[SongUrl] 未登录，优先使用公共API获取歌曲URL。");
                    var simplifiedResult = await GetSongUrlViaSimplifiedApiAsync(ids, requestedLevel, cancellationToken).ConfigureAwait(false);
                    if (simplifiedResult != null && simplifiedResult.Count > 0)
                    {
                        System.Diagnostics.Debug.WriteLine("[SongUrl] 公共API成功返回歌曲URL，跳过 EAPI 尝试。");
                        return simplifiedResult;
                    }

                    System.Diagnostics.Debug.WriteLine("[SongUrl] 公共API未返回有效结果，尝试使用 EAPI 兜底。");
                }
                catch (Exception simplifiedEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[SongUrl] 公共API获取失败: {simplifiedEx.Message}，尝试使用 EAPI 兜底。");
                    lastException = simplifiedEx;
                }
            }

            long[] numericIds;
            try
            {
                numericIds = ids.Select(id => long.Parse(id, CultureInfo.InvariantCulture)).ToArray();
            }
            catch (Exception parseEx)
            {
                System.Diagnostics.Debug.WriteLine($"[SongUrl] 歌曲ID解析失败: {parseEx.Message}");
                throw;
            }

            for (int i = startIndex; i < qualityOrder.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string currentLevel = qualityOrder[i];

                try
                {
                    System.Diagnostics.Debug.WriteLine($"[EAPI] 尝试音质: {currentLevel}");

                    var header = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    var baseHeader = _authContext?.BuildEapiHeaderPayload(useMobileMode: true);
                    if (baseHeader != null)
                    {
                        foreach (var kvp in baseHeader)
                        {
                            header[kvp.Key] = kvp.Value;
                        }
                    }

                    if (UsePersonalCookie && !string.IsNullOrEmpty(_musicU))
                    {
                        header["MUSIC_U"] = _musicU;
                        System.Diagnostics.Debug.WriteLine("[EAPI] 使用个人账号Cookie获取高音质");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("[EAPI] 未登录或未开启个人Cookie，使用公开API");
                    }

                    if (!header.ContainsKey("__csrf") && !string.IsNullOrEmpty(_csrfToken))
                    {
                        header["__csrf"] = _csrfToken;
                    }

            var payload = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["ids"] = numericIds,
                ["level"] = currentLevel,
                ["encodeType"] = GetEncodeType(currentLevel),
                ["header"] = header
            };

                    if (currentLevel == "sky")
                    {
                        payload["immerseType"] = "c51";
                    }

                    var response = await PostEApiAsync<JObject>("/api/song/enhance/player/url/v1", payload, useIosHeaders: true, skipErrorHandling: true, cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (response == null)
                    {
                        System.Diagnostics.Debug.WriteLine("[EAPI] 响应为空，尝试下一个音质");
                        continue;
                    }

                    int code = response["code"]?.Value<int>() ?? -1;
                    string message = response["message"]?.Value<string>() ?? response["msg"]?.Value<string>() ?? "unknown";
                    if (code == 404 || (!string.IsNullOrEmpty(message) && message.Contains("不存在")))
                    {
                        System.Diagnostics.Debug.WriteLine($"[EAPI] 官方接口返回资源不存在 (code={code}, message={message})，尝试降级。");
                        continue;
                    }

                    if (code != 200)
                    {
                        // 海外未登录场景尝试一次 realIP 兜底
                        if (!UsePersonalCookie && string.IsNullOrEmpty(_musicU))
                        {
                            System.Diagnostics.Debug.WriteLine($"[EAPI] code={code}，尝试海外兜底 realIP 获取试听 URL");
                            response = await PostEApiWithOverseasBypassAsync("/api/song/enhance/player/url/v1", payload, cancellationToken).ConfigureAwait(false);
                            code = response?["code"]?.Value<int>() ?? code;
                            message = response?["message"]?.Value<string>() ?? response?["msg"]?.Value<string>() ?? message;
                        }

                        if (code != 200)
                        {
                            System.Diagnostics.Debug.WriteLine($"[EAPI] code={code}, message={message}，尝试下一个音质");
                            continue;
                        }
                    }

                    var data = response["data"] as JArray;
                    if (data == null || data.Count == 0)
                    {
                        System.Diagnostics.Debug.WriteLine("[EAPI] data为空，尝试下一个音质");
                        continue;
                    }

                    var result = new Dictionary<string, SongUrlInfo>();
                    bool fallbackToLowerQuality = false;

                        foreach (var item in data)
                        {
                            string id = item["id"]?.ToString();
                            if (string.IsNullOrEmpty(id))
                            {
                                System.Diagnostics.Debug.WriteLine("[EAPI] 返回数据缺少歌曲ID，跳过。");
                                fallbackToLowerQuality = true;
                                break;
                        }

                        int itemCode = item["code"]?.Value<int>() ?? 0;
                        string itemMessage = item["message"]?.Value<string>() ?? item["msg"]?.Value<string>();
                        bool itemMissing = itemCode == 404 ||
                                           string.Equals(itemMessage, "not found", StringComparison.OrdinalIgnoreCase) ||
                                           (!string.IsNullOrEmpty(itemMessage) && itemMessage.Contains("不存在"));

                        int fee = item["fee"]?.Value<int>() ?? 0;
                        int payed = item["payed"]?.Value<int?>() ?? 0;
                        bool isPaidAlbumLocked = fee == 4 && payed == 0;

                        if (isPaidAlbumLocked)
                        {
                            System.Diagnostics.Debug.WriteLine($"[EAPI] 歌曲{id} 归属付费数字专辑且未购买，停止重试。");
                            string safeId = id ?? string.Empty;
                            throw new PaidAlbumNotPurchasedException(new[] { safeId }, "该歌曲属于付费数字专辑，未购买无法播放。");
                        }

                        if (itemMissing)
                        {
                            System.Diagnostics.Debug.WriteLine($"[EAPI] 歌曲{id} 在音质 {currentLevel} 下不可用，尝试降级。");
                            if (!string.IsNullOrEmpty(id))
                            {
                                missingSongIds.Add(id);
                            }
                            fallbackToLowerQuality = true;
                            break;
                        }

                            string url = item["url"]?.Value<string>();
                            if (string.IsNullOrEmpty(url) && !UsePersonalCookie && string.IsNullOrEmpty(_musicU))
                            {
                                // 对单曲再尝试一次 realIP 兜底获取试听 URL
                                System.Diagnostics.Debug.WriteLine($"[EAPI] 歌曲{id} 在音质 {currentLevel} 下无URL，尝试海外兜底 realIP");
                                var singlePayload = new Dictionary<string, object>(payload, StringComparer.OrdinalIgnoreCase)
                                {
                                    ["ids"] = new[] { long.Parse(id, CultureInfo.InvariantCulture) }
                                };
                                var fallback = await PostEApiWithOverseasBypassAsync("/api/song/enhance/player/url/v1", singlePayload, cancellationToken).ConfigureAwait(false);
                                var fallbackData = fallback?["data"] as JArray;
                                var first = fallbackData?.FirstOrDefault();
                                if (first != null)
                                {
                                    url = first["url"]?.Value<string>() ?? url;
                                    itemCode = first["code"]?.Value<int>() ?? itemCode;
                                    itemMessage = first["message"]?.Value<string>() ?? first["msg"]?.Value<string>() ?? itemMessage;
                                }
                            }

                            if (string.IsNullOrEmpty(url))
                            {
                                System.Diagnostics.Debug.WriteLine($"[EAPI] 歌曲{id} 在音质 {currentLevel} 下无可用URL，尝试降级");
                                if (!string.IsNullOrEmpty(id))
                                {
                                    missingSongIds.Add(id);
                                }
                                fallbackToLowerQuality = true;
                                break;
                            }

                        // ⭐ 获取服务器实际返回的音质级别
                        string returnedLevel = item["level"]?.Value<string>();

                        // ⭐ 修复：即使返回的音质与请求不同，只要URL有效，就接受这个结果
                        // 原因：服务器返回的音质就是该歌曲的最佳可用音质（例如请求HiRes但歌曲只有Lossless）
                        // 删除了错误的"服务器降级"检测逻辑，避免不必要的fallback
                        if (!string.IsNullOrEmpty(returnedLevel) && !returnedLevel.Equals(currentLevel, StringComparison.OrdinalIgnoreCase))
                        {
                            System.Diagnostics.Debug.WriteLine($"[EAPI] ℹ️ 音质差异: 请求={currentLevel}, 返回={returnedLevel}（接受服务器返回的最佳可用音质）");
                        }

                        // 解析试听信息
                        FreeTrialInfo trialInfo = null;
                        var freeTrialInfoToken = item["freeTrialInfo"];
                        if (freeTrialInfoToken != null && freeTrialInfoToken.Type != Newtonsoft.Json.Linq.JTokenType.Null)
                        {
                            trialInfo = new FreeTrialInfo
                            {
                                Start = freeTrialInfoToken["start"]?.Value<long>() ?? 0,
                                End = freeTrialInfoToken["end"]?.Value<long>() ?? 0
                            };
                        }

                        result[id] = new SongUrlInfo
                        {
                            Id = id,
                            Url = url,
                            Level = returnedLevel ?? currentLevel,
                            Size = item["size"]?.Value<long>() ?? 0,
                            Br = item["br"]?.Value<int>() ?? 0,
                            Type = item["type"]?.Value<string>(),
                            Md5 = item["md5"]?.Value<string>(),
                            Fee = item["fee"]?.Value<int>() ?? 0,
                            FreeTrialInfo = trialInfo
                        };

                        string trialIndicator = trialInfo != null ? $" [试听: {trialInfo.Start / 1000}s-{trialInfo.End / 1000}s]" : "";
                        System.Diagnostics.Debug.WriteLine($"[EAPI] ✓ 歌曲{id}: level={result[id].Level}, br={result[id].Br}, fee={result[id].Fee}{trialIndicator}, URL={url.Substring(0, Math.Min(50, url.Length))}...");
                    }

                    if (fallbackToLowerQuality || result.Count == 0)
                    {
                        System.Diagnostics.Debug.WriteLine("[EAPI] 当前音质返回为空或不可用，尝试下一档。");
                        continue;
                    }

                    if (result.Count > 0)
                    {
                        string actualLevel = result.Values.FirstOrDefault()?.Level ?? currentLevel;
                        int actualBr = result.Values.FirstOrDefault()?.Br ?? 0;
                        System.Diagnostics.Debug.WriteLine($"[EAPI] ✓✓✓ 成功获取音质: {actualLevel} (比特率: {actualBr / 1000} kbps)");
                        return result;
                    }
                }
                catch (PaidAlbumNotPurchasedException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[EAPI] 音质 {currentLevel} 异常: {ex.Message}");
                    lastException = ex;
                }
            }

            if (missingSongIds.Count > 0)
            {
                throw new SongResourceNotFoundException("请求的歌曲资源在官方曲库中不存在或已下架。", missingSongIds);
            }

            if (!UsePersonalCookie && !simplifiedAttempted)
            {
                try
                {
                    System.Diagnostics.Debug.WriteLine("[EAPI] 所有音质的加密接口均失败，回退到公共API。");
                    return await GetSongUrlViaSimplifiedApiAsync(ids, requestedLevel, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception simplifiedEx)
                {
                    lastException = simplifiedEx;
                }
            }

            if (lastException != null)
            {
                throw new Exception("无法获取歌曲播放地址，请检查网络或稍后再试。", lastException);
            }

            throw new Exception("无法获取歌曲播放地址，请检查网络或稍后再试。");
        }

        /// <summary>
        /// 通过公共API获取歌曲URL（参考 Python 版本：get_song_url_api，256-298行）
        /// </summary>
        private async Task<Dictionary<string, SongUrlInfo>> GetSongUrlViaSimplifiedApiAsync(string[] ids, string level, CancellationToken cancellationToken = default)
        {
            var result = new Dictionary<string, SongUrlInfo>();

            // 公共API一次只能查询一首歌曲，所以需要循环调用
            foreach (var songId in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    // Python源码参考：
                    // data = {'url': str(song_id), 'level': quality, 'type': 'json'}
                    // result = call_netease_api('/song', data)
                    var payload = new
                    {
                        url = songId,
                        level = level,
                        type = "json"
                    };

                    var jsonPayload = JsonConvert.SerializeObject(payload);
                    var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                    // Python源码：base_url = "http://159.75.21.45:5000"
                    string apiUrl = $"{SIMPLIFIED_API_BASE}/song";

                    System.Diagnostics.Debug.WriteLine($"[API] 公共API请求: {apiUrl}, songId={songId}, level={level}");

                    var response = await _simplifiedClient.PostAsync(apiUrl, content, cancellationToken).ConfigureAwait(false);
                    string responseText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    System.Diagnostics.Debug.WriteLine($"[API] 公共API响应状态: {response.StatusCode}");
                    System.Diagnostics.Debug.WriteLine($"[API] 公共API响应内容(前500字符): {(responseText.Length > 500 ? responseText.Substring(0, 500) : responseText)}");

                    // 解析响应
                    var json = JObject.Parse(responseText);
                    bool success = json["success"]?.Value<bool>() ?? false;

                    // Python源码：if result.get('success') and result.get('data'):
                    if (success && json["data"] != null)
                    {
                        var data = json["data"];
                        string url = data["url"]?.Value<string>();

                        if (!string.IsNullOrEmpty(url))
                        {
                            var urlInfo = new SongUrlInfo
                            {
                                Id = songId,
                                Url = url,
                                Level = data["level"]?.Value<string>() ?? level,
                                Size = ParseFileSizeToken(data["size"]),
                                Br = 0,  // 公共API不提供比特率信息
                                Type = url.Contains(".flac") ? "flac" : "mp3",
                                Md5 = null
                            };

                            result[songId] = urlInfo;
                            System.Diagnostics.Debug.WriteLine($"[API] 公共API成功获取歌曲: {songId}, URL={url}");
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"[API] 公共API返回的URL为空: {songId}");
                        }
                    }
                    else
                    {
                        string message = json["message"]?.Value<string>() ?? "未知错误";
                        System.Diagnostics.Debug.WriteLine($"[API] 公共API失败: {songId}, message={message}");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[API] 公共API异常: {songId}, error={ex.Message}");
                    // 继续尝试下一首歌曲
                }
            }

            if (result.Count == 0)
            {
                throw new SongResourceNotFoundException("请求的歌曲资源在官方曲库中不存在或已下架。", ids);
            }

            return result;
        }

        private async Task<HashSet<string>> CheckSongsAvailabilityAsync(string[] ids, QualityLevel quality, CancellationToken cancellationToken = default)
        {
            var missing = new HashSet<string>(StringComparer.Ordinal);

            if (ids == null || ids.Length == 0)
            {
                return missing;
            }

            cancellationToken.ThrowIfCancellationRequested();

            long[] numericIds;
            var idLookup = new Dictionary<long, string>();

            try
            {
                numericIds = ids
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id =>
                    {
                        long parsed = long.Parse(id, CultureInfo.InvariantCulture);
                        idLookup[parsed] = id;
                        return parsed;
                    })
                    .ToArray();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SongUrl] 资源预检解析ID失败: {ex.Message}");
                return missing;
            }

            if (numericIds.Length == 0)
            {
                return missing;
            }

            var payload = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["ids"] = JsonConvert.SerializeObject(numericIds),
                ["br"] = GetBitrateForQualityLevel(quality)
            };

            cancellationToken.ThrowIfCancellationRequested();

            JObject response;
            try
            {
                response = await PostWeApiAsync<JObject>("/song/enhance/player/url", payload, retryCount: 0, skipErrorHandling: true, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                System.Diagnostics.Debug.WriteLine($"[SongUrl] 资源预检被取消");
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SongUrl] 资源预检调用失败: {ex.Message}");
                return missing;
            }

            int topCode = response?["code"]?.Value<int>() ?? -1;
            if (topCode == 404)
            {
                foreach (var id in ids)
                {
                    if (!string.IsNullOrEmpty(id))
                    {
                        missing.Add(id);
                    }
                }
                return missing;
            }

            var data = response?["data"] as JArray;
            if (data == null)
            {
                return missing;
            }

            var seenIds = new HashSet<long>();
            foreach (var item in data)
            {
                if (item == null)
                {
                    continue;
                }

                long itemId = item["id"]?.Value<long>() ?? 0;
                if (itemId != 0)
                {
                    seenIds.Add(itemId);
                }

                int itemCode = item["code"]?.Value<int>() ?? 0;
                string itemMessage = item["message"]?.Value<string>() ?? item["msg"]?.Value<string>();
                bool isMissing = itemCode == 404 ||
                                 (!string.IsNullOrEmpty(itemMessage) && itemMessage.IndexOf("不存在", StringComparison.OrdinalIgnoreCase) >= 0);

                if (!isMissing)
                {
                    continue;
                }

                if (itemId != 0 && idLookup.TryGetValue(itemId, out var original))
                {
                    missing.Add(original);
                }
            }

            if (seenIds.Count < numericIds.Length)
            {
                foreach (var candidate in numericIds)
                {
                    if (!seenIds.Contains(candidate) && idLookup.TryGetValue(candidate, out var original))
                    {
                        missing.Add(original);
                    }
                }
            }

            return missing;
        }

        /// <summary>
        /// 批量检查歌曲资源可用性（用于列表预检）
        /// </summary>
        /// <param name="ids">歌曲ID列表</param>
        /// <param name="quality">音质级别</param>
        /// <returns>歌曲ID到可用性的映射。true=可用，false=不可用</returns>
        public async Task<Dictionary<string, bool>> BatchCheckSongsAvailabilityAsync(string[] ids, QualityLevel quality)
        {
            var result = new Dictionary<string, bool>(StringComparer.Ordinal);

            if (ids == null || ids.Length == 0)
            {
                return result;
            }

            // 去重并过滤空ID
            var uniqueIds = ids
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (uniqueIds.Length == 0)
            {
                return result;
            }

            // 分批处理，每批100首（避免URL过长）
            const int batchSize = 100;
            for (int i = 0; i < uniqueIds.Length; i += batchSize)
            {
                int count = Math.Min(batchSize, uniqueIds.Length - i);
                var batch = new string[count];
                Array.Copy(uniqueIds, i, batch, 0, count);

                try
                {
                    var batchResult = await CheckSingleBatchAvailabilityAsync(batch, quality).ConfigureAwait(false);
                    foreach (var kvp in batchResult)
                    {
                        result[kvp.Key] = kvp.Value;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[BatchCheck] 批次 {i / batchSize + 1} 检查失败: {ex.Message}");
                    // 失败的批次中的歌曲默认为可用（保守策略，避免误杀）
                    foreach (var id in batch)
                    {
                        if (!result.ContainsKey(id))
                        {
                            result[id] = true;
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// 流式批量检查歌曲资源可用性（实时回调，收到一首填写一首）
        /// </summary>
        /// <param name="ids">歌曲ID列表</param>
        /// <param name="quality">音质级别</param>
        /// <param name="onSongChecked">每首歌曲检查完成后的回调 (songId, isAvailable)</param>
        /// <param name="cancellationToken">取消令牌</param>
        public async Task BatchCheckSongsAvailabilityStreamAsync(
            string[] ids,
            QualityLevel quality,
            Action<string, bool> onSongChecked,
            CancellationToken cancellationToken = default)
        {
            if (ids == null || ids.Length == 0 || onSongChecked == null)
            {
                return;
            }

            // 去重并过滤空ID
            var uniqueIds = ids
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (uniqueIds.Length == 0)
            {
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[StreamCheck] 🚀 开始流式批量检查 {uniqueIds.Length} 首歌曲");

            // 分批处理，每批100首
            const int batchSize = 100;
            for (int i = 0; i < uniqueIds.Length; i += batchSize)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    System.Diagnostics.Debug.WriteLine($"[StreamCheck] 检查被取消");
                    break;
                }

                int count = Math.Min(batchSize, uniqueIds.Length - i);
                var batch = new string[count];
                Array.Copy(uniqueIds, i, batch, 0, count);

                int batchNumber = i / batchSize + 1;
                System.Diagnostics.Debug.WriteLine($"[StreamCheck] 📦 批次 {batchNumber}: 检查 {batch.Length} 首歌曲...");

                try
                {
                    var batchResult = await CheckSingleBatchAvailabilityAsync(batch, quality, cancellationToken).ConfigureAwait(false);

                    foreach (var songId in batch)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            break;
                        }

                        bool isAvailable = batchResult.TryGetValue(songId, out bool value) ? value : true;
                        try
                        {
                            onSongChecked(songId, isAvailable);
                        }
                        catch (Exception callbackEx)
                        {
                            System.Diagnostics.Debug.WriteLine($"[StreamCheck] 回调处理异常: {callbackEx.Message}");
                        }
                    }

                    System.Diagnostics.Debug.WriteLine($"[StreamCheck] ✅ 批次 {batchNumber} 完成");
                }
                catch (OperationCanceledException)
                {
                    System.Diagnostics.Debug.WriteLine($"[StreamCheck] 批次 {batchNumber} 已取消");
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[StreamCheck] 批次 {batchNumber} 失败: {ex.Message}，所有歌曲默认视为可用");
                    foreach (var songId in batch)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            break;
                        }

                        try
                        {
                            onSongChecked(songId, true);
                        }
                        catch (Exception callbackEx)
                        {
                            System.Diagnostics.Debug.WriteLine($"[StreamCheck] 回调处理异常: {callbackEx.Message}");
                        }
                    }
                }
            }

            System.Diagnostics.Debug.WriteLine($"[StreamCheck] 🎉 流式检查全部完成");
        }

        /// <summary>
        /// 检查单批歌曲的可用性
        /// </summary>
        private async Task<Dictionary<string, bool>> CheckSingleBatchAvailabilityAsync(string[] ids, QualityLevel quality, CancellationToken cancellationToken = default)
        {
            var result = new Dictionary<string, bool>(StringComparer.Ordinal);

            if (ids == null || ids.Length == 0)
            {
                return result;
            }

            long[] numericIds;
            var idLookup = new Dictionary<long, string>();

            try
            {
                numericIds = ids
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id =>
                    {
                        long parsed = long.Parse(id, CultureInfo.InvariantCulture);
                        idLookup[parsed] = id;
                        return parsed;
                    })
                    .ToArray();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BatchCheck] 解析ID失败: {ex.Message}");
                // 解析失败，默认所有歌曲可用
                foreach (var id in ids)
                {
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        result[id] = true;
                    }
                }
                return result;
            }

            if (numericIds.Length == 0)
            {
                return result;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var payload = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["ids"] = JsonConvert.SerializeObject(numericIds),
                ["br"] = GetBitrateForQualityLevel(quality)
            };

            JObject response;
            try
            {
                response = await PostWeApiAsync<JObject>("/song/enhance/player/url", payload, retryCount: 0, skipErrorHandling: true, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BatchCheck] API调用失败: {ex.Message}");
                // API调用失败，默认所有歌曲可用
                foreach (var id in ids)
                {
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        result[id] = true;
                    }
                }
                return result;
            }

            // 初始化所有歌曲为可用（默认值）
            foreach (var id in ids)
            {
                if (!string.IsNullOrWhiteSpace(id))
                {
                    result[id] = true;
                }
            }

            int topCode = response?["code"]?.Value<int>() ?? -1;
            if (topCode == 404)
            {
                // 整批都不可用
                foreach (var id in ids)
                {
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        result[id] = false;
                    }
                }
                return result;
            }

            var data = response?["data"] as JArray;
            if (data == null)
            {
                return result;
            }

            cancellationToken.ThrowIfCancellationRequested();

            // 检查每首歌的状态
            foreach (var item in data)
            {
                if (item == null)
                {
                    continue;
                }

                long itemId = item["id"]?.Value<long>() ?? 0;
                if (itemId == 0 || !idLookup.TryGetValue(itemId, out var originalId))
                {
                    continue;
                }

                int itemCode = item["code"]?.Value<int>() ?? 0;
                string itemMessage = item["message"]?.Value<string>() ?? item["msg"]?.Value<string>();

                // 检查是否不可用
                bool isUnavailable = itemCode == 404 ||
                                     itemCode == 403 ||
                                     (!string.IsNullOrEmpty(itemMessage) &&
                                      (itemMessage.IndexOf("不存在", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                       itemMessage.IndexOf("版权", StringComparison.OrdinalIgnoreCase) >= 0));

                result[originalId] = !isUnavailable;
            }

            return result;
        }

        private static long ParseFileSizeToken(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return 0;
            }

            try
            {
                if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
                {
                    return token.Value<long>();
                }

                if (token.Type == JTokenType.String)
                {
                    string text = token.Value<string>();
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        return 0;
                    }

                    text = text.Trim();
                    if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLong))
                    {
                        return parsedLong;
                    }

                    var match = Regex.Match(text, @"(?<value>\d+(?:\.\d+)?)\s*(?<unit>[KMG]?B)", RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        if (!double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                        {
                            return 0;
                        }

                        string unit = match.Groups["unit"].Value.ToUpperInvariant();
                        double multiplier = 1d;
                        switch (unit)
                        {
                            case "KB":
                                multiplier = 1024d;
                                break;
                            case "MB":
                                multiplier = 1024d * 1024d;
                                break;
                            case "GB":
                                multiplier = 1024d * 1024d * 1024d;
                                break;
                            case "B":
                            default:
                                multiplier = 1d;
                                break;
                        }

                        return (long)Math.Round(value * multiplier);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 解析文件大小失败: {ex.Message} (token={token})");
            }

            return 0;
        }

        /// <summary>
        /// 通过WEAPI获取歌曲URL（Python源码：_fetch_song_url_via_weapi，12651-12674行）
        /// </summary>
        private async Task<Dictionary<string, SongUrlInfo>> FetchSongUrlViaWeapi(string[] ids, string level, string encodeType)
        {
            var payload = new Dictionary<string, object>
            {
                { "ids", $"[{string.Join(",", ids)}]" },
                { "level", level },
                { "encodeType", encodeType }
            };

            // Python源码：12657-12658行
            // if level == "sky":
            //     payload["immerseType"] = "c51"
            if (level == "sky")
            {
                payload["immerseType"] = "c51";
            }

            JObject response;
            try
            {
                // 注意：PostWeApiAsync会自动添加/weapi前缀，所以这里只需要/song/enhance/player/url/v1
                response = await PostWeApiAsync<JObject>("/song/enhance/player/url/v1", payload);
            }
            catch (Exception ex)
            {
                // Python源码12661-12662行：如果code!=200，抛出RuntimeError
                // 但这个RuntimeError会被_fetch_song_url_for_level catch（12682-12694行）
                // 然后尝试下一个方法或音质
                // 所以我们这里抛出异常，让FetchSongUrlForLevel catch并记录错误
                throw new Exception($"WEAPI请求失败: {ex.Message}", ex);
            }

            var data = response["data"] as JArray;

            var result = new Dictionary<string, SongUrlInfo>();
            if (data != null)
            {
                foreach (var item in data)
                {
                    string id = item["id"]?.ToString();
                    if (string.IsNullOrEmpty(id))
                        continue;

                    // 检查 item 中的 code 字段（参考 Python 版本 12665-12674）
                    int itemCode = item["code"]?.Value<int>() ?? 0;
                    string url = item["url"]?.Value<string>();

                    // 如果 url 为 null，说明这个音质不可用（可能是版权限制或需要 VIP）
                    // Python 版本：if url: return url, size else: return None, None
                    // 当返回 None 时，上层会继续尝试下一个音质
                    if (string.IsNullOrEmpty(url))
                    {
                        // 根据 code 提供更具体的错误信息（C# 7.3 兼容写法）
                        string errorMsg;
                        if (itemCode == -110)
                        {
                            errorMsg = "需要VIP会员或版权受限";
                        }
                        else if (itemCode == -100)
                        {
                            errorMsg = "参数错误";
                        }
                        else if (itemCode == -460)
                        {
                            errorMsg = "IP限流";
                        }
                        else
                        {
                            errorMsg = $"播放链接为空 (code={itemCode})";
                        }
                        throw new Exception(errorMsg);
                    }

                    var urlInfo = new SongUrlInfo
                    {
                        Id = id,
                        Url = url,
                        Level = item["level"]?.Value<string>(),
                        Size = item["size"]?.Value<long>() ?? 0,
                        Br = item["br"]?.Value<int>() ?? 0,
                        Type = item["type"]?.Value<string>(),
                        Md5 = item["md5"]?.Value<string>()
                    };

                    result[id] = urlInfo;
                }
            }

            return result;
        }

        /// <summary>
        /// 获取歌曲详情
        /// </summary>
        public async Task<List<SongInfo>> GetSongDetailAsync(string[] ids)
        {
            var payload = new Dictionary<string, object>
            {
                { "c", "[" + string.Join(",", ids.Select(id => $"{{\"id\":{id}}}")) + "]" },
                { "ids", $"[{string.Join(",", ids)}]" }
            };

            var response = await PostWeApiAsync<JObject>("/v3/song/detail", payload);
            var songs = response["songs"] as JArray;
            return ParseSongList(songs);
        }

        #endregion

    }
}
