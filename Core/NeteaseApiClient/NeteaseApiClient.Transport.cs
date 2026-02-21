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
        #region 加密请求方法

        /// <summary>
        /// WEAPI POST 请求
        /// </summary>
        public async Task<T> PostWeApiAsync<T>(
            string path,
            object payload,
            int retryCount = 0,
            bool skipErrorHandling = false,
            CancellationToken cancellationToken = default,
            string baseUrl = OFFICIAL_API_BASE,
            bool autoConvertApiSegment = false,
            string? userAgentOverride = null)
        {
            try
            {
                // 转换payload为字典（Python源码：_weapi_post，7567-7628行）
                var payloadDict = payload as Dictionary<string, object> ??
                    JsonConvert.DeserializeObject<Dictionary<string, object>>(JsonConvert.SerializeObject(payload));

                // 添加csrf_token到payload（如果有的话）
                if (!string.IsNullOrEmpty(_csrfToken))
                {
                    if (!payloadDict.ContainsKey("csrf_token"))
                    {
                        payloadDict["csrf_token"] = _csrfToken;
                    }
                }

                // 序列化payload（Python源码：json.dumps(data, separators=(",", ":"), ensure_ascii=False)）
                // 使用紧凑格式，不添加空格，与Python保持一致
                string jsonPayload = JsonConvert.SerializeObject(payloadDict, new JsonSerializerSettings
                {
                    Formatting = Formatting.None,  // 不添加空格和换行
                    StringEscapeHandling = StringEscapeHandling.Default
                });

                // 调试：输出原始payload
                System.Diagnostics.Debug.WriteLine($"[DEBUG WEAPI] Raw JSON Payload: {jsonPayload}");

                // WEAPI加密
                var encrypted = EncryptionHelper.EncryptWeapi(jsonPayload);

                // 调试：输出加密结果（仅显示前100个字符）
                System.Diagnostics.Debug.WriteLine($"[DEBUG WEAPI] Encrypted params (first 100 chars): {encrypted.Params.Substring(0, Math.Min(100, encrypted.Params.Length))}");
                System.Diagnostics.Debug.WriteLine($"[DEBUG WEAPI] Encrypted encSecKey (first 100 chars): {encrypted.EncSecKey.Substring(0, Math.Min(100, encrypted.EncSecKey.Length))}");

                // 构造表单数据
                var formData = new Dictionary<string, string>
                {
                    { "params", encrypted.Params },
                    { "encSecKey", encrypted.EncSecKey }
                };

                var content = new FormUrlEncodedContent(formData);

                // 调试：输出Content-Type
                System.Diagnostics.Debug.WriteLine($"[DEBUG WEAPI] Content-Type: {content.Headers.ContentType}");

                // 归一化基础地址和路径
                string normalizedBaseUrl = SelectBaseUrl(baseUrl, retryCount);

                string normalizedPath = path ?? string.Empty;
                if (!normalizedPath.StartsWith("/"))
                {
                    normalizedPath = "/" + normalizedPath;
                }

                bool hasExplicitPrefix = normalizedPath.StartsWith("/weapi", StringComparison.OrdinalIgnoreCase)
                    || normalizedPath.StartsWith("/api", StringComparison.OrdinalIgnoreCase)
                    || normalizedPath.StartsWith("/eapi", StringComparison.OrdinalIgnoreCase)
                    || normalizedPath.StartsWith("/openapi", StringComparison.OrdinalIgnoreCase);

                if (autoConvertApiSegment)
                {
                    normalizedPath = Regex.Replace(normalizedPath, @"\b\w*api\b", "weapi", RegexOptions.IgnoreCase);
                    hasExplicitPrefix = normalizedPath.StartsWith("/weapi", StringComparison.OrdinalIgnoreCase);
                }

                if (!hasExplicitPrefix)
                {
                    normalizedPath = "/weapi" + normalizedPath;
                }

                string url = $"{normalizedBaseUrl}{normalizedPath}";
                var baseUri = new Uri(normalizedBaseUrl);

                // 添加csrf_token查询参数（如果有的话）
                if (!string.IsNullOrEmpty(_csrfToken))
                {
                    url = AppendQueryParameter(url, "csrf_token", _csrfToken);
                }

                // 添加时间戳参数，避免缓存
                long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                url = AppendQueryParameter(url, "t", timestamp.ToString(CultureInfo.InvariantCulture));

                // ⭐ 调试：输出Cookie信息
                var cookies = _cookieContainer.GetCookies(baseUri);
                System.Diagnostics.Debug.WriteLine($"[DEBUG WEAPI] Cookie Count: {cookies.Count}");
                foreach (Cookie cookie in cookies)
                {
                    if (cookie.Name == "MUSIC_U")
                    {
                        System.Diagnostics.Debug.WriteLine($"[DEBUG WEAPI] Cookie: {cookie.Name}={cookie.Value.Substring(0, Math.Min(30, cookie.Value.Length))}... (长度:{cookie.Value.Length})");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[DEBUG WEAPI] Cookie: {cookie.Name}={cookie.Value}");
                    }
                }

                using (var requestMessage = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    requestMessage.Content = content;
                    ApplyFingerprintHeaders(requestMessage);
                    if (!string.IsNullOrWhiteSpace(userAgentOverride))
                    {
                        requestMessage.Headers.Remove("User-Agent");
                        requestMessage.Headers.TryAddWithoutValidation("User-Agent", userAgentOverride);
                    }

                    var response = await _httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

                    byte[] rawBytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    string responseText = DecodeResponseContent(response, rawBytes);

                System.Diagnostics.Debug.WriteLine($"[DEBUG WEAPI] Request URL: {url}");
                System.Diagnostics.Debug.WriteLine($"[DEBUG WEAPI] Response Status: {response.StatusCode}");
                System.Diagnostics.Debug.WriteLine($"[DEBUG WEAPI] Response Headers: {string.Join(", ", response.Headers.Select(h => $"{h.Key}={string.Join(",", h.Value)}"))}");
                    System.Diagnostics.Debug.WriteLine($"[DEBUG WEAPI] Response Length(bytes): {rawBytes?.Length ?? 0}, TextLength: {responseText.Length}");
                    if (!string.IsNullOrEmpty(responseText))
                    {
                        System.Diagnostics.Debug.WriteLine($"[DEBUG WEAPI] Response Preview: {responseText.Substring(0, Math.Min(200, responseText.Length))}");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("[DEBUG WEAPI] Response Preview: <empty>");
                    }

                    string trimmedResponse = responseText?.TrimStart() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(trimmedResponse) ||
                        (!trimmedResponse.StartsWith("{") && !trimmedResponse.StartsWith("[")))
                    {
                        string? debugFile = null;
                        if (!string.IsNullOrWhiteSpace(responseText))
                        {
                            try
                            {
                                debugFile = TryWriteDebugFile(
                                    "netease_debug_response",
                                    "html",
                                    $"URL: {url}\n\nStatus: {response.StatusCode}\n\n{responseText}"
                                );
                                if (!string.IsNullOrEmpty(debugFile))
                                {
                                    System.Diagnostics.Debug.WriteLine($"[DEBUG WEAPI] !!!响应不是JSON!!! 已保存到: {debugFile}");
                                }
                            }
                            catch
                            {
                            }
                        }

                        string errorMessage = string.IsNullOrWhiteSpace(responseText)
                            ? $"服务器返回空响应（状态码: {response.StatusCode}），可能是网络问题或API限流"
                            : $"服务器返回非JSON响应（状态码: {response.StatusCode}），可能是网络问题或API限流";

                        return await HandleWeApiInvalidResponseAsync<T>(
                            errorMessage,
                            url,
                            response.StatusCode,
                            debugFile,
                            path,
                            payload,
                            retryCount,
                            skipErrorHandling,
                            cancellationToken,
                            baseUrl,
                            autoConvertApiSegment,
                            userAgentOverride).ConfigureAwait(false);
                    }

                    JObject json;
                    try
                    {
                        string cleanedResponse = CleanJsonResponse(responseText);
                        json = JObject.Parse(cleanedResponse);
                    }
                    catch (JsonReaderException ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[DEBUG WEAPI] JSON解析失败: {ex.Message}");
                        System.Diagnostics.Debug.WriteLine($"[DEBUG WEAPI] 响应原文: {responseText}");
                    
                        string? debugFile = null;
                        try
                        {
                            debugFile = TryWriteDebugFile("netease_json_error", "txt", $"URL: {url}\n\nError: {ex.Message}\n\nResponse:\n{responseText}");
                            if (!string.IsNullOrEmpty(debugFile))
                            {
                                System.Diagnostics.Debug.WriteLine($"[DEBUG WEAPI] 错误响应已保存到: {debugFile}");
                            }
                        }
                        catch
                        {
                        }
                    
                        return await HandleWeApiInvalidResponseAsync<T>(
                            $"JSON解析失败: {ex.Message}，响应内容可能已损坏",
                            url,
                            response.StatusCode,
                            debugFile,
                            path,
                            payload,
                            retryCount,
                            skipErrorHandling,
                            cancellationToken,
                            baseUrl,
                            autoConvertApiSegment,
                            userAgentOverride).ConfigureAwait(false);
                    }


                    int code = json["code"]?.Value<int>() ?? -1;
                    string message = json["message"]?.Value<string>() ?? json["msg"]?.Value<string>() ?? "Unknown error";

                    if (!skipErrorHandling)
                    {
                        // 自动刷新（301）一次
                        if (code == 301 && retryCount == 0)
                        {
                            bool refreshed = await TryAutoRefreshLoginAsync().ConfigureAwait(false);
                            if (refreshed)
                            {
                                System.Diagnostics.Debug.WriteLine("[WEAPI] 检测到 301，已自动刷新登录，重试当前请求。");
                                return await PostWeApiAsync<T>(path, payload, retryCount + 1, skipErrorHandling, cancellationToken, baseUrl, autoConvertApiSegment, userAgentOverride).ConfigureAwait(false);
                            }
                        }

                        HandleApiError(code, message);
                    }

                    return json.ToObject<T>();
                }
            }
            catch (Exception ex) when (retryCount < MAX_RETRY_COUNT &&
                                       !(ex is UnauthorizedAccessException) &&
                                       !(ex is ApiResourceUnavailableException))
            {
                if (ex is OperationCanceledException)
                {
                    throw;
                }
                if (ShouldRetry(ex))
                {
                    int delayMs = GetRandomRetryDelay();
                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                    return await PostWeApiAsync<T>(path, payload, retryCount + 1, skipErrorHandling, cancellationToken, baseUrl, autoConvertApiSegment, userAgentOverride).ConfigureAwait(false);
                }

                throw;
            }
        }

        /// <summary>
        /// 使用 interface.music.163.com 域名的 WEAPI 接口
        /// </summary>
        public Task<T> PostInterfaceWeApiAsync<T>(
            string path,
            object payload,
            int retryCount = 0,
            bool skipErrorHandling = false,
            CancellationToken cancellationToken = default)
        {
            return PostWeApiAsync<T>(
                path,
                payload,
                retryCount,
                skipErrorHandling,
                cancellationToken,
                baseUrl: INTERFACE_URI.ToString().TrimEnd('/'),
                autoConvertApiSegment: true);
        }

        /// <summary>
        /// 使用 iOS User-Agent 的 WEAPI 接口调用，专门用于短信验证码登录
        /// ⭐ 参考 netease-music-simple-player/Net/NetClasses.cs:2054-2203
        /// 关键修复：使用独立的 _iOSLoginClient (UseCookies=false) + 手动添加访客Cookie
        /// 模拟参考项目的 ApplyCookiesToRequest 行为
        /// </summary>
        /// <param name="path">API路径</param>
        /// <param name="data">请求数据</param>
        /// <param name="maxRetries">最大重试次数</param>
        /// <param name="sendCookies">是否发送访客Cookie（验证码发送需要true，登录需要false）</param>
        private async Task<T> PostWeApiWithiOSAsync<T>(string path, Dictionary<string, object> data, int maxRetries = 3, bool sendCookies = false)
        {
            string IOS_USER_AGENT = _authContext?.CurrentAccountState?.DeviceUserAgent ?? AuthConstants.MobileUserAgent;
            string IOS_ACCEPT_LANGUAGE = "zh-Hans-CN;q=1, en-CN;q=0.9";
            string antiCheatToken = _authContext?.GetActiveAntiCheatToken();

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    // 添加 csrf_token 到 payload
                    var payloadDict = new Dictionary<string, object>(data);
                    if (!string.IsNullOrEmpty(_csrfToken))
                    {
                        payloadDict["csrf_token"] = _csrfToken;
                    }

                    // 构造 URL
                    string url = $"{OFFICIAL_API_BASE}/weapi{path}";
                    if (!string.IsNullOrEmpty(_csrfToken))
                    {
                        url = AppendQueryParameter(url, "csrf_token", _csrfToken);
                    }
                    // 添加时间戳
                    long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    url = AppendQueryParameter(url, "t", timestamp.ToString(CultureInfo.InvariantCulture));

                    // WEAPI 加密
                    string jsonPayload = JsonConvert.SerializeObject(payloadDict, Formatting.None);
                    var encrypted = EncryptionHelper.EncryptWeapi(jsonPayload);

                    var formData = new Dictionary<string, string>
                    {
                        { "params", encrypted.Params },
                        { "encSecKey", encrypted.EncSecKey }
                    };
                    var content = new FormUrlEncodedContent(formData);

                    // 创建请求并设置 iOS User-Agent
                    using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                    {
                        request.Content = content;

                        // ⭐ 关键：使用 iOS User-Agent，而不是桌面 PC User-Agent
                        request.Headers.TryAddWithoutValidation("User-Agent", IOS_USER_AGENT);
                        request.Headers.TryAddWithoutValidation("Referer", REFERER);
                        request.Headers.TryAddWithoutValidation("Origin", ORIGIN);
                        request.Headers.TryAddWithoutValidation("Accept", "*/*");
                        request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate, br");
                        request.Headers.TryAddWithoutValidation("Connection", "keep-alive");
                        request.Headers.TryAddWithoutValidation("Accept-Language", IOS_ACCEPT_LANGUAGE);
                        if (!string.IsNullOrEmpty(antiCheatToken))
                        {
                            request.Headers.TryAddWithoutValidation("X-antiCheatToken", antiCheatToken);
                        }

                        // 默认不注入随机IP，避免登录记录出现异常地区
                        ApplyFingerprintHeaders(request);

                        // ??? 双模式Cookie策略（与1.7.3保持一致）
                        // sendCookies=true  : 发送访客Cookie（用于验证码发送）
                        // sendCookies=false : 完全零Cookie（用于手机号登录），避免 UA/设备指纹冲突触发 -462
                        string cookieHeader = string.Empty;

                        if (sendCookies)
                        {
                            // 使用纯移动端指纹生成的访客 Cookie，避免桌面指纹混入
                            var mobileCookies = _authContext?.BuildMobileCookieMap(includeAnonymousToken: true)
                                                ?? new Dictionary<string, string>();
                            var cookieBuilder = new StringBuilder();
                            foreach (var kv in mobileCookies)
                            {
                                if (cookieBuilder.Length > 0)
                                {
                                    cookieBuilder.Append("; ");
                                }
                                cookieBuilder.Append($"{kv.Key}={kv.Value}");
                            }
                            cookieHeader = cookieBuilder.ToString();
                            if (!string.IsNullOrEmpty(cookieHeader))
                            {
                                request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
                            }
                        }

                        System.Diagnostics.Debug.WriteLine($"[iOS WEAPI] Attempt {attempt}/{maxRetries}");
                        System.Diagnostics.Debug.WriteLine($"[iOS WEAPI] URL: {url}");
                        System.Diagnostics.Debug.WriteLine($"[iOS WEAPI] User-Agent: {IOS_USER_AGENT}");
                        System.Diagnostics.Debug.WriteLine($"[iOS WEAPI] Cookie Mode: {(sendCookies ? "访客Cookie" : "ZERO Cookie")}");
                        System.Diagnostics.Debug.WriteLine($"[iOS WEAPI] Cookie: {(string.IsNullOrEmpty(cookieHeader) ? "(empty)" : cookieHeader.Substring(0, Math.Min(200, cookieHeader.Length)) + "...")}");

                        // ⭐ 核心修复：使用iOS登录专用客户端（UseCookies=false），避免HttpClientHandler自动注入Cookie
                        // 参考项目 netease-music-simple-player 使用 UseCookies=false，确保零Cookie请求真正发送零Cookie
                        var response = await _iOSLoginClient.SendAsync(request).ConfigureAwait(false);
                        byte[] rawBytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                        string responseText = DecodeResponseContent(response, rawBytes);

                        System.Diagnostics.Debug.WriteLine($"[iOS WEAPI] Response Status: {response.StatusCode}");
                        System.Diagnostics.Debug.WriteLine($"[iOS WEAPI] Response Preview: {responseText.Substring(0, Math.Min(200, responseText.Length))}");

                        if (response.StatusCode == HttpStatusCode.OK)
                        {
                            // ⭐ 处理 Set-Cookie 响应头，提取 MUSIC_U/__csrf 等关键 Cookie
                            if (response.Headers.Contains("Set-Cookie"))
                            {
                                try
                                {
                                    bool appliedCookie = false;
                                    foreach (var setCookie in response.Headers.GetValues("Set-Cookie"))
                                    {
                                        if (ApplySetCookieHeader(setCookie))
                                        {
                                            appliedCookie = true;
                                        }
                                    }

                                    if (appliedCookie)
                                    {
                                        UpdateCookies();
                                        try
                                        {
                                            var syncedCookies = _cookieContainer.GetCookies(MUSIC_URI);
                                            _authContext?.SyncFromCookies(syncedCookies);
                                        }
                                        catch (Exception syncEx)
                                        {
                                            System.Diagnostics.Debug.WriteLine($"[iOS WEAPI] Sync cookies failed: {syncEx.Message}");
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine($"[iOS WEAPI] Failed to extract Set-Cookie: {ex.Message}");
                                }
                            }

                            // 解析 JSON 响应
                            try
                            {
                                string cleanedResponse = CleanJsonResponse(responseText);
                                var json = JObject.Parse(cleanedResponse);
                                return json.ToObject<T>();
                            }
                            catch (JsonReaderException ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[iOS WEAPI] JSON parse error (attempt {attempt}/{maxRetries}): {ex.Message}");
                                if (attempt == maxRetries)
                                {
                                    throw new Exception($"JSON 解析失败: {ex.Message}");
                                }
                            }
                        }
                        else if (attempt == maxRetries)
                        {
                            throw new Exception($"HTTP {response.StatusCode}: {responseText}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[iOS WEAPI] Exception (attempt {attempt}/{maxRetries}): {ex.Message}");
                    if (attempt == maxRetries)
                    {
                        throw;
                    }
                }

                // 重试延迟（参考 netease-music-simple-player）
                if (attempt < maxRetries)
                {
                    int delayMs = attempt <= 3 ? 50 : Math.Min(attempt * 100, 500);
                    if (delayMs > 0)
                    {
                        await Task.Delay(delayMs).ConfigureAwait(false);
                    }
                }
            }

            throw new Exception("所有重试均失败");
        }

        /// <summary>
        /// 二维码登录专用的WEAPI请求
        /// ⭐ 核心修复：使用标准 _httpClient（UseCookies=true）自动发送CookieContainer中的所有Cookie
        /// 参考备份版本（二维码登录工作正常）的实现，避免手动Cookie构建可能的格式错误
        /// 使用桌面User-Agent（因为二维码在桌面浏览器环境显示）
        /// </summary>
        private async Task<T> PostWeApiWithoutCookiesAsync<T>(string path, Dictionary<string, object> data, int maxRetries = 3)
        {
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    // 添加 csrf_token 到 payload
                    var payloadDict = new Dictionary<string, object>(data);
                    if (!string.IsNullOrEmpty(_csrfToken))
                    {
                        payloadDict["csrf_token"] = _csrfToken;
                    }

                    // 构造 URL
                    string url = $"{OFFICIAL_API_BASE}/weapi{path}";
                    if (!string.IsNullOrEmpty(_csrfToken))
                    {
                        url = AppendQueryParameter(url, "csrf_token", _csrfToken);
                    }
                    // 添加时间戳
                    long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    url = AppendQueryParameter(url, "t", timestamp.ToString(CultureInfo.InvariantCulture));

                    // WEAPI 加密
                    string jsonPayload = JsonConvert.SerializeObject(payloadDict, Formatting.None);
                    var encrypted = EncryptionHelper.EncryptWeapi(jsonPayload);

                    var formData = new Dictionary<string, string>
                    {
                        { "params", encrypted.Params },
                        { "encSecKey", encrypted.EncSecKey }
                    };
                    var content = new FormUrlEncodedContent(formData);

                    // 创建请求，使用桌面User-Agent（二维码在桌面浏览器环境显示）
                    using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                    {
                        request.Content = content;

                        // ⭐ 关键：使用桌面User-Agent（二维码是在桌面浏览器环境展示的）
                        request.Headers.TryAddWithoutValidation("User-Agent", USER_AGENT);
                        request.Headers.TryAddWithoutValidation("Referer", REFERER);
                        request.Headers.TryAddWithoutValidation("Origin", ORIGIN);
                        request.Headers.TryAddWithoutValidation("Accept", "*/*");
                        request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
                        ApplyFingerprintHeaders(request);

                        System.Diagnostics.Debug.WriteLine($"[QR WEAPI] Attempt {attempt}/{maxRetries}");
                        System.Diagnostics.Debug.WriteLine($"[QR WEAPI] URL: {url}");
                        System.Diagnostics.Debug.WriteLine($"[QR WEAPI] User-Agent: Desktop");

                        // ⭐⭐⭐ 核心修复：使用标准 _httpClient（UseCookies=true）
                        // 参考备份版本（二维码登录工作正常）的实现
                        // _httpClient 会自动附加 _cookieContainer 中的所有Cookie（包括访客Cookie）
                        // 避免手动构建Cookie header可能导致的格式错误
                        var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
                        byte[] rawBytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                        string responseText = DecodeResponseContent(response, rawBytes);

                        System.Diagnostics.Debug.WriteLine($"[QR WEAPI] Response Status: {response.StatusCode}");
                        System.Diagnostics.Debug.WriteLine($"[QR WEAPI] Response Preview: {(string.IsNullOrEmpty(responseText) ? "<empty>" : responseText.Substring(0, Math.Min(200, responseText.Length)))}");

                        if (response.StatusCode == HttpStatusCode.OK)
                        {
                            // 解析 JSON 响应
                            try
                            {
                                string cleanedResponse = CleanJsonResponse(responseText);
                                var json = JObject.Parse(cleanedResponse);
                                return json.ToObject<T>();
                            }
                            catch (JsonReaderException ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[QR WEAPI] JSON parse error (attempt {attempt}/{maxRetries}): {ex.Message}");
                                if (attempt == maxRetries)
                                {
                                    throw new Exception($"JSON 解析失败: {ex.Message}");
                                }
                            }
                        }
                        else if (attempt == maxRetries)
                        {
                            throw new Exception($"HTTP {response.StatusCode}: {responseText}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[QR WEAPI] Exception (attempt {attempt}/{maxRetries}): {ex.Message}");
                    if (attempt == maxRetries)
                    {
                        throw;
                    }
                }

                // 重试延迟
                if (attempt < maxRetries)
                {
                    int delayMs = attempt <= 3 ? 50 : Math.Min(attempt * 100, 500);
                    if (delayMs > 0)
                    {
                        await Task.Delay(delayMs).ConfigureAwait(false);
                    }
                }
            }

            throw new Exception("所有重试均失败");
        }

        /// <summary>
        /// EAPI POST 请求
        /// </summary>
        public async Task<T> PostEApiAsync<T>(string path, object payload, bool useIosHeaders = true, int retryCount = 0, bool skipErrorHandling = false, bool injectRealIpHeaders = false, CancellationToken cancellationToken = default)
        {
            try
            {
                var payloadDict = payload as Dictionary<string, object> ??
                    JsonConvert.DeserializeObject<Dictionary<string, object>>(JsonConvert.SerializeObject(payload));

                var headerMap = EnsureEapiHeader(payloadDict);

                string jsonPayload = JsonConvert.SerializeObject(payloadDict, Formatting.None);

                System.Diagnostics.Debug.WriteLine($"[DEBUG EAPI] Path: {path}");
                System.Diagnostics.Debug.WriteLine($"[DEBUG EAPI] Payload: {jsonPayload}");

                // EAPI加密 - 使用 /api/ 路径
                string encrypted = EncryptionHelper.EncryptEapi(path, jsonPayload);

                // 构造表单数据
                var formData = new Dictionary<string, string>
                {
                    { "params", encrypted }
                };

                var content = new FormUrlEncodedContent(formData);

                string antiCheatToken = useIosHeaders ? _authContext?.GetActiveAntiCheatToken() : null;
                var requestHeaders = BuildEapiRequestHeaders(useIosHeaders, antiCheatToken);
                string cookieHeader = BuildEapiCookieHeader(headerMap);

                // 构建请求 URL - 将 /api/ 替换为 /eapi/
                // ⭐ 使用 interface3 域名（iOS 端 API，性能更好）
                string requestPath = path.Replace("/api/", "/eapi/");
                string url = $"{EAPI_BASE_URL}{requestPath}";
                System.Diagnostics.Debug.WriteLine($"[DEBUG EAPI] Request URL: {url}");
                using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    request.Content = content;
                    ApplyFingerprintHeaders(request, injectRealIpHeaders);

                    System.Diagnostics.Debug.WriteLine($"[DEBUG EAPI] Cookie Length: {(string.IsNullOrEmpty(cookieHeader) ? 0 : cookieHeader.Length)}");
                    if (requestHeaders.TryGetValue("User-Agent", out var resolvedUa))
                    {
                        System.Diagnostics.Debug.WriteLine($"[DEBUG EAPI] User-Agent: {resolvedUa}");
                    }

                    foreach (var header in requestHeaders)
                    {
                        request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }

                    if (!string.IsNullOrEmpty(cookieHeader))
                    {
                        request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
                    }

                    var response = await _eapiClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

                    byte[] rawBytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    string decryptedText = null;

                    System.Diagnostics.Debug.WriteLine($"[DEBUG EAPI] 原始响应大小: {rawBytes.Length} bytes");
                    if (rawBytes.Length > 0)
                    {
                        // 显示前16个字节的十六进制
                        var preview = rawBytes.Take(Math.Min(16, rawBytes.Length)).ToArray();
                        System.Diagnostics.Debug.WriteLine($"[DEBUG EAPI] 响应前{preview.Length}字节 (hex): {BitConverter.ToString(preview)}");
                        // 也尝试显示为ASCII字符
                        try
                        {
                            string asciiPreview = Encoding.ASCII.GetString(preview).Replace("\r", "\\r").Replace("\n", "\\n").Replace("\0", "\\0");
                            System.Diagnostics.Debug.WriteLine($"[DEBUG EAPI] 响应前{preview.Length}字节 (ASCII): {asciiPreview}");
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[DEBUG EAPI] ASCII preview failed: {ex.Message}");
                        }
                    }

                    // 记录响应头中的编码信息
                    var contentEncoding = response.Content.Headers.ContentEncoding;
                    if (contentEncoding != null && contentEncoding.Any())
                    {
                        System.Diagnostics.Debug.WriteLine($"[DEBUG EAPI] Content-Encoding: {string.Join(", ", contentEncoding)}");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("[DEBUG EAPI] Content-Encoding: <none>");
                    }

                    var contentType = response.Content.Headers.ContentType?.ToString() ?? "<none>";
                    System.Diagnostics.Debug.WriteLine($"[DEBUG EAPI] Content-Type: {contentType}");

                    // 检查响应是否经过压缩（gzip / brotli）
                    if (contentEncoding != null && contentEncoding.Any(e => e.Equals("gzip", StringComparison.OrdinalIgnoreCase)))
                    {
                        // gzip content encoding will be handled by TryDecompressEapiPayload
                    }
                    // 也检查 gzip 魔数 (0x1f, 0x8b)
                    else if (rawBytes.Length >= 2 && rawBytes[0] == 0x1f && rawBytes[1] == 0x8b)
                    {
                        // gzip magic number detected
                    }

                    if (contentEncoding != null && contentEncoding.Any(e =>
                        e.Equals("br", StringComparison.OrdinalIgnoreCase) ||
                        e.Equals("brotli", StringComparison.OrdinalIgnoreCase)))
                    {
                        // brotli content encoding will be handled if runtime supports it
                    }

                    var mediaType = response.Content.Headers.ContentType?.MediaType;
                    byte[] candidatePlainBytes = rawBytes;
                    if (!LooksLikePlainJson(candidatePlainBytes))
                    {
                        var decompressedRaw = TryDecompressEapiPayload(candidatePlainBytes, $"{path} [raw]");
                        if (!ReferenceEquals(decompressedRaw, candidatePlainBytes))
                        {
                            candidatePlainBytes = decompressedRaw;
                        }
                    }

                    bool looksLikeJson = LooksLikePlainJson(candidatePlainBytes);
                    if (looksLikeJson && !ReferenceEquals(candidatePlainBytes, rawBytes))
                    {
                        System.Diagnostics.Debug.WriteLine("[DEBUG EAPI] 原始响应解压后已是明文JSON，跳过解密。");
                    }

                    if (!looksLikeJson && !string.IsNullOrEmpty(mediaType) &&
                        (mediaType.IndexOf("json", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         mediaType.IndexOf("text", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        System.Diagnostics.Debug.WriteLine("[DEBUG EAPI] Content-Type提示为JSON/TEXT，但内容不像明文JSON，继续尝试解密。");
                    }

                    byte[] cipherBytes = looksLikeJson ? Array.Empty<byte>() : PrepareEapiCipherBytes(rawBytes);
                    if (!looksLikeJson && (cipherBytes == null || cipherBytes.Length == 0))
                    {
                        throw new Exception("EAPI 响应为空，无法解密。");
                    }

                    try
                    {
                        byte[] decryptedBytes;
                        if (looksLikeJson)
                        {
                            decryptedBytes = candidatePlainBytes;
                        }
                        else
                        {
                        decryptedBytes = EncryptionHelper.DecryptEapiToBytes(cipherBytes);
                        if (decryptedBytes != null && decryptedBytes.Length > 0)
                        {
                            var decryptedPreview = decryptedBytes.Take(Math.Min(16, decryptedBytes.Length)).ToArray();
                            System.Diagnostics.Debug.WriteLine($"[DEBUG EAPI] 解密后前{decryptedPreview.Length}字节 (hex): {BitConverter.ToString(decryptedPreview)}");
                            if (LooksLikePlainJson(decryptedPreview))
                            {
                                System.Diagnostics.Debug.WriteLine("[DEBUG EAPI] 解密结果前缀可读，为避免多次解密循环，直接尝试解析。");
                            }
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine("[DEBUG EAPI] 解密后内容为空。");
                        }

                            decryptedBytes = TryDecompressEapiPayload(decryptedBytes, path);
                        }

                        decryptedText = Encoding.UTF8.GetString(decryptedBytes ?? Array.Empty<byte>());
                    }
                    catch (Exception decryptEx)
                    {
                        string fallbackText = Encoding.UTF8.GetString(rawBytes);
                        if (!string.IsNullOrWhiteSpace(fallbackText))
                        {
                            try
                            {
                                // 如果能够解析为JSON，说明服务端返回的是明文响应，直接透传
                                JToken.Parse(fallbackText);
                                System.Diagnostics.Debug.WriteLine("[DEBUG EAPI] 响应看起来是明文JSON，跳过解密。");
                                decryptedText = fallbackText;
                            }
                            catch (JsonReaderException)
                            {
                                SaveEapiDebugArtifact(path, rawBytes, null, decryptEx);
                                throw new ApiResponseCorruptedException(response.StatusCode, url, "EAPI 解密失败", null, decryptEx);
                            }
                        }
                        else
                        {
                            SaveEapiDebugArtifact(path, rawBytes, null, decryptEx);
                            throw new ApiResponseCorruptedException(response.StatusCode, url, "EAPI 解密失败", null, decryptEx);
                        }
                    }

                    System.Diagnostics.Debug.WriteLine($"[DEBUG EAPI] Response Status: {response.StatusCode}");
                    if (!string.IsNullOrEmpty(decryptedText))
                    {
                        System.Diagnostics.Debug.WriteLine($"[DEBUG EAPI] Decrypted Preview: {decryptedText.Substring(0, Math.Min(200, decryptedText.Length))}");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("[DEBUG EAPI] Decrypted Preview: <empty>");
                    }

                    JObject json;
                    try
                    {
                        json = JObject.Parse(decryptedText);
                    }
                    catch (JsonReaderException ex)
                    {
                        SaveEapiDebugArtifact(path, rawBytes, string.IsNullOrEmpty(decryptedText) ? null : Encoding.UTF8.GetBytes(decryptedText), ex);
                        throw new ApiResponseCorruptedException(response.StatusCode, url, $"EAPI JSON解析失败: {ex.Message}", null, ex);
                    }

                    int code = json["code"]?.Value<int>() ?? -1;
                    string message = json["message"]?.Value<string>() ?? json["msg"]?.Value<string>() ?? "Unknown error";

                    if (!skipErrorHandling)
                    {
                        if (code == 301 && retryCount == 0)
                        {
                            bool refreshed = await TryAutoRefreshLoginAsync().ConfigureAwait(false);
                            if (refreshed)
                            {
                                System.Diagnostics.Debug.WriteLine("[EAPI] 检测到 301，已自动刷新登录，重试当前请求。");
                                return await PostEApiAsync<T>(path, payload, useIosHeaders, retryCount + 1, skipErrorHandling, injectRealIpHeaders, cancellationToken).ConfigureAwait(false);
                            }
                        }

                        HandleApiError(code, message);
                    }

                    return json.ToObject<T>();
                }
            }
            catch (Exception ex) when (retryCount < MAX_RETRY_COUNT &&
                                       !(ex is UnauthorizedAccessException) &&
                                       !(ex is ApiResourceUnavailableException))
            {
                if (ex is OperationCanceledException)
                {
                    throw;
                }
                if (ShouldRetry(ex))
                {
                    int delayMs = GetRandomRetryDelay();
                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                    return await PostEApiAsync<T>(path, payload, useIosHeaders, retryCount + 1, skipErrorHandling, injectRealIpHeaders, cancellationToken).ConfigureAwait(false);
                }

                throw;
            }
        }

        /// <summary>
        /// 计算自适应重试延迟（参考 netease-music-simple-player）
        /// 策略：第1-3次重试用 50ms，之后按 attempt * 100ms，最大 500ms
        /// </summary>
        private static int GetAdaptiveRetryDelay(int retryAttempt)
        {
            if (retryAttempt <= 3)
            {
                return MIN_RETRY_DELAY_MS;
            }
            return Math.Min(retryAttempt * 100, MAX_RETRY_DELAY_MS);
        }

        private int GetRandomRetryDelay()
        {
            lock (_random)
            {
                return _random.Next(500, 1501);
            }
        }

        private static bool ShouldRetry(Exception ex)
        {
            return ex is ApiTransientException
                || ex is ApiAccessRestrictedException
                || ex is ApiResponseCorruptedException;
        }

        private static bool LooksLikePlainJson(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                return false;
            }

            int index = 0;
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            {
                index = 3;
            }

            while (index < data.Length)
            {
                byte current = data[index];
                if (current == 0x20 || current == 0x09 || current == 0x0D || current == 0x0A)
                {
                    index++;
                    continue;
                }

                return current == (byte)'{' || current == (byte)'[';
            }

            return false;
        }

        private static byte[] PrepareEapiCipherBytes(byte[] rawBytes)
        {
            if (rawBytes == null || rawBytes.Length == 0)
            {
                return rawBytes ?? Array.Empty<byte>();
            }

            // 先尝试检查是否是十六进制字符串（优先级最高）
            try
            {
                string candidate = Encoding.UTF8.GetString(rawBytes);

                // 检查原始字符串
                if (IsHexString(candidate))
                {
                    System.Diagnostics.Debug.WriteLine("[DEBUG EAPI] 响应是十六进制字符串，转换为字节数组");
                    return HexStringToBytes(candidate);
                }

                // 检查去除空白后的字符串
                string trimmed = candidate?.Trim();
                if (!string.Equals(candidate, trimmed, StringComparison.Ordinal) && IsHexString(trimmed))
                {
                    System.Diagnostics.Debug.WriteLine("[DEBUG EAPI] 响应是带空白的十六进制字符串，转换为字节数组");
                    return HexStringToBytes(trimmed);
                }
            }
            catch
            {
                // 转换失败，继续检查其他可能性
            }

            // 如果不是十六进制字符串，且长度是 16 的倍数，假设是二进制密文
            if (rawBytes.Length % 16 == 0)
            {
                System.Diagnostics.Debug.WriteLine($"[DEBUG EAPI] 响应是二进制数据，长度: {rawBytes.Length} bytes");
                return rawBytes;
            }

            // 其他情况，直接返回原始字节
            System.Diagnostics.Debug.WriteLine($"[DEBUG EAPI] 响应格式未知，长度: {rawBytes.Length} bytes，直接使用原始字节");
            return rawBytes;
        }

        /// <summary>
        /// 通用兜底解压：在响应头缺失或错误时，基于魔数尝试 gzip/deflate/brotli。
        /// </summary>
        private static byte[] TryDecompressCommonPayload(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                return data ?? Array.Empty<byte>();
            }

            if (LooksLikePlainJson(data))
            {
                return data;
            }

            byte[] working = data;
            bool decompressed = false;

            if (HasGzipHeader(working))
            {
                try
                {
                    using (var compressedStream = new MemoryStream(working))
                    using (var gzip = new GZipStream(compressedStream, CompressionMode.Decompress))
                    using (var decompressedStream = new MemoryStream())
                    {
                        gzip.CopyTo(decompressedStream);
                        working = decompressedStream.ToArray();
                        decompressed = true;
                    }
                    System.Diagnostics.Debug.WriteLine($"[DecodeResponseContent] 兜底 Gzip 解压成功，大小: {working.Length} bytes");
                }
                catch (Exception gzipEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[DecodeResponseContent] 兜底 Gzip 解压失败: {gzipEx.Message}");
                }
            }

            if (!decompressed && HasZlibHeader(working))
            {
                try
                {
                    using (var compressedStream = new MemoryStream(working))
                    using (var deflate = new DeflateStream(compressedStream, CompressionMode.Decompress))
                    using (var decompressedStream = new MemoryStream())
                    {
                        deflate.CopyTo(decompressedStream);
                        working = decompressedStream.ToArray();
                        decompressed = true;
                    }
                    System.Diagnostics.Debug.WriteLine($"[DecodeResponseContent] 兜底 Deflate 解压成功，大小: {working.Length} bytes");
                }
                catch (Exception deflateEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[DecodeResponseContent] 兜底 Deflate 解压失败: {deflateEx.Message}");
                }
            }

            if (!decompressed && !LooksLikePlainJson(working))
            {
                try
                {
                    working = Brotli.DecompressBuffer(working, 0, working.Length, null);
                    decompressed = true;
                    System.Diagnostics.Debug.WriteLine($"[DecodeResponseContent] 兜底 Brotli 解压成功，大小: {working.Length} bytes");
                }
                catch (Exception brotliEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[DecodeResponseContent] 兜底 Brotli 解压失败: {brotliEx.Message}");
                }
            }

            return working;
        }

        private static byte[] TryDecompressEapiPayload(byte[] data, string path)
        {
            if (data == null || data.Length == 0)
            {
                return data ?? Array.Empty<byte>();
            }

            if (LooksLikePlainJson(data))
            {
                return data;
            }

            byte[] working = data;
            bool decompressed = false;
            string normalizedPath = string.IsNullOrWhiteSpace(path) ? "<unknown>" : path;

            if (HasGzipHeader(working))
            {
                try
                {
                    using (var compressedStream = new MemoryStream(working))
                    using (var gzip = new GZipStream(compressedStream, CompressionMode.Decompress))
                    using (var decompressedStream = new MemoryStream())
                    {
                        gzip.CopyTo(decompressedStream);
                        working = decompressedStream.ToArray();
                        decompressed = true;
                    }
                    System.Diagnostics.Debug.WriteLine($"[DEBUG EAPI] ({normalizedPath}) Payload Gzip 解压成功，大小: {working.Length} bytes");
                }
                catch (Exception gzipEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[DEBUG EAPI] ({normalizedPath}) Payload Gzip 解压失败: {gzipEx.Message}");
                }
            }

            if (!decompressed && HasZlibHeader(working))
            {
                try
                {
                    using (var compressedStream = new MemoryStream(working))
                    using (var deflate = new DeflateStream(compressedStream, CompressionMode.Decompress))
                    using (var decompressedStream = new MemoryStream())
                    {
                        deflate.CopyTo(decompressedStream);
                        working = decompressedStream.ToArray();
                        decompressed = true;
                    }
                    System.Diagnostics.Debug.WriteLine($"[DEBUG EAPI] ({normalizedPath}) Payload Deflate 解压成功，大小: {working.Length} bytes");
                }
                catch (Exception deflateEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[DEBUG EAPI] ({normalizedPath}) Payload Deflate 解压失败: {deflateEx.Message}");
                }
            }

            // Brotli 解压依赖外部库，保持可选启用策略以避免运行时缺失导致失败。
            if (!decompressed && !LooksLikePlainJson(working))
            {
                try
                {
                    working = Brotli.DecompressBuffer(working, 0, working.Length, null);
                    decompressed = true;
                    System.Diagnostics.Debug.WriteLine($"[DEBUG EAPI] ({normalizedPath}) Payload Brotli 解压成功，大小: {working.Length} bytes");
                }
                catch (Exception brotliEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[DEBUG EAPI] ({normalizedPath}) Payload Brotli 解压失败: {brotliEx.Message}");
                }
            }

            if (decompressed)
            {
                var preview = working.Take(Math.Min(16, working.Length)).ToArray();
                System.Diagnostics.Debug.WriteLine($"[DEBUG EAPI] ({normalizedPath}) Payload 解压后前{preview.Length}字节 (hex): {BitConverter.ToString(preview)}");
            }

            return working;
        }

        private static bool HasGzipHeader(byte[] data)
        {
            return data != null && data.Length >= 2 && data[0] == 0x1f && data[1] == 0x8b;
        }

        private static bool HasZlibHeader(byte[] data)
        {
            if (data == null || data.Length < 2)
            {
                return false;
            }

            if (data[0] != 0x78)
            {
                return false;
            }

            byte second = data[1];
            return second == 0x01 || second == 0x5E || second == 0x9C || second == 0xDA;
        }

        private static void SaveEapiDebugArtifact(string path, byte[] rawBytes, byte[] decryptedBytes, Exception exception)
        {
            try
            {
                string safeName = string.IsNullOrWhiteSpace(path) ? "unknown" : path.Replace('/', '_').Replace('\\', '_').Trim('_');
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmssfff", CultureInfo.InvariantCulture);
                string baseName = Path.Combine(Path.GetTempPath(), $"netease_eapi_{safeName}_{timestamp}");

                if (rawBytes != null)
                {
                    File.WriteAllBytes(baseName + ".raw.bin", rawBytes);
                }

                if (decryptedBytes != null && decryptedBytes.Length > 0)
                {
                    File.WriteAllBytes(baseName + ".decoded.bin", decryptedBytes);
                }

                var info = new StringBuilder();
                info.AppendLine($"Path: {path}");
                if (exception != null)
                {
                    info.AppendLine($"Exception: {exception.GetType().FullName}: {exception.Message}");
                }
                info.AppendLine($"RawLength: {rawBytes?.Length ?? 0}");
                info.AppendLine($"DecodedLength: {decryptedBytes?.Length ?? 0}");

                File.WriteAllText(baseName + ".txt", info.ToString(), Encoding.UTF8);
                System.Diagnostics.Debug.WriteLine($"[DEBUG EAPI] 调试数据已写入: {baseName}.*");
            }
            catch
            {
                // 忽略调试文件写入失败
            }
        }

        private static bool IsHexString(string value)
        {
            if (string.IsNullOrEmpty(value) || (value.Length % 2) != 0)
            {
                return false;
            }

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool isDigit = (c >= '0' && c <= '9');
                bool isUpper = (c >= 'A' && c <= 'F');
                bool isLower = (c >= 'a' && c <= 'f');
                if (!(isDigit || isUpper || isLower))
                {
                    return false;
                }
            }

            return true;
        }

        private static byte[] HexStringToBytes(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                string segment = hex.Substring(i * 2, 2);
                bytes[i] = Convert.ToByte(segment, 16);
            }

            return bytes;
        }

        /// <summary>
        /// 专用于海外试听兜底：在 payload/header 中注入 realIP（大陆随机），其余逻辑保持不变。
        /// 仅在未登录/无个人 Cookie 且主请求返回失败时调用，避免影响正常会员链路。
        /// </summary>
        private async Task<JObject> PostEApiWithOverseasBypassAsync(string path, Dictionary<string, object> originalPayload, CancellationToken cancellationToken = default)
        {
            var payload = new Dictionary<string, object>(originalPayload ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
            string realIp = GenerateRandomChineseIp();
            payload["realIP"] = realIp;

            if (payload.TryGetValue("header", out var headerObj))
            {
                var header = NormalizeEapiHeader(headerObj);
                header["realIP"] = realIp;
                payload["header"] = header;
            }

            return await PostEApiAsync<JObject>(path, payload, useIosHeaders: true, skipErrorHandling: true, injectRealIpHeaders: true, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        private IDictionary<string, object> EnsureEapiHeader(Dictionary<string, object> payloadDict)
        {
            if (payloadDict == null)
            {
                return CreateDefaultEapiHeader();
            }

            IDictionary<string, object> header;
            if (payloadDict.TryGetValue("header", out var headerValue))
            {
                header = NormalizeEapiHeader(headerValue);
            }
            else
            {
                header = CreateDefaultEapiHeader();
                payloadDict["header"] = header;
            }

            ApplyEapiHeaderDefaults(header);
            payloadDict["header"] = header;

            return header;
        }

        private IDictionary<string, object> NormalizeEapiHeader(object headerValue)
        {
            Dictionary<string, object> header = null;

            try
            {
                switch (headerValue)
                {
                    case null:
                        break;
                    case IDictionary<string, object> dictObj:
                        header = new Dictionary<string, object>(dictObj, StringComparer.OrdinalIgnoreCase);
                        break;
                    case IDictionary<string, string> dictStr:
                        header = dictStr.ToDictionary(kvp => kvp.Key, kvp => (object)kvp.Value, StringComparer.OrdinalIgnoreCase);
                        break;
                    case JObject jObject:
                        header = jObject.ToObject<Dictionary<string, object>>();
                        break;
                    case JToken jToken:
                        header = jToken.ToObject<Dictionary<string, object>>();
                        break;
                    case string headerString when !string.IsNullOrWhiteSpace(headerString):
                        header = JsonConvert.DeserializeObject<Dictionary<string, object>>(headerString);
                        break;
                    default:
                        header = JsonConvert.DeserializeObject<Dictionary<string, object>>(JsonConvert.SerializeObject(headerValue));
                        break;
                }
            }
            catch
            {
                header = null;
            }

            if (header == null)
            {
                header = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            }
            else if (!(header is Dictionary<string, object> dict) || dict.Comparer != StringComparer.OrdinalIgnoreCase)
            {
                header = new Dictionary<string, object>(header, StringComparer.OrdinalIgnoreCase);
            }

            return header;
        }

        private IDictionary<string, object> CreateDefaultEapiHeader()
        {
            var accountState = _authContext?.CurrentAccountState;
            var header = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["osver"] = accountState?.DeviceOsVersion ?? "13.0",
                ["deviceId"] = _deviceId ?? accountState?.DeviceId ?? EncryptionHelper.GenerateDeviceId(),
                ["appver"] = accountState?.DeviceAppVersion ?? "8.10.90",
                ["versioncode"] = accountState?.DeviceVersionCode ?? "8010090",
                ["mobilename"] = accountState?.DeviceMobileName ?? "Xiaomi 2211133C",
                ["buildver"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
                ["resolution"] = accountState?.DeviceResolution ?? "1080x2400",
                ["__csrf"] = _csrfToken ?? string.Empty,
                ["os"] = accountState?.DeviceOs ?? "android",
                ["channel"] = accountState?.DeviceChannel ?? "xiaomi",
                ["requestId"] = EncryptionHelper.GenerateRequestId()
            };

#pragma warning disable IDE0028 // 简化对象初始化器
            return header;
#pragma warning restore IDE0028
        }

        private void ApplyEapiHeaderDefaults(IDictionary<string, object> header)
        {
            if (header == null)
            {
                return;
            }

            var defaults = CreateDefaultEapiHeader();
            foreach (var kvp in defaults)
            {
                string existing = null;
                if (header.TryGetValue(kvp.Key, out var value) && value != null)
                {
                    existing = Convert.ToString(value, CultureInfo.InvariantCulture);
                }

                if (string.IsNullOrWhiteSpace(existing))
                {
                    header[kvp.Key] = kvp.Value;
                }
            }

            header["requestId"] = EncryptionHelper.GenerateRequestId();
        }

        private IDictionary<string, string> BuildEapiRequestHeaders(bool useIosHeaders, string antiCheatToken)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string userAgent = _desktopUserAgent ?? USER_AGENT;

            if (useIosHeaders && _authContext != null)
            {
                var mobileHeaders = _authContext.BuildMobileRequestHeaders(_musicU, antiCheatToken);
                if (mobileHeaders != null)
                {
                    foreach (var kvp in mobileHeaders)
                    {
                        if (string.Equals(kvp.Key, "User-Agent", StringComparison.OrdinalIgnoreCase))
                        {
                            userAgent = kvp.Value;
                        }
                        else
                        {
                            headers[kvp.Key] = kvp.Value;
                        }
                    }
                }
            }

            if (string.IsNullOrEmpty(userAgent))
            {
                userAgent = USER_AGENT_IOS;
            }

            headers["User-Agent"] = userAgent;

            if (!headers.ContainsKey("Accept"))
            {
                headers["Accept"] = "*/*";
            }

            if (!headers.ContainsKey("Accept-Language"))
            {
                headers["Accept-Language"] = "zh-CN,zh;q=0.9,en;q=0.8";
            }

            if (!headers.ContainsKey("Connection"))
            {
                headers["Connection"] = "keep-alive";
            }

            if (!headers.ContainsKey("Referer"))
            {
                headers["Referer"] = REFERER;
            }

            if (!headers.ContainsKey("Origin"))
            {
                headers["Origin"] = ORIGIN;
            }

            if (headers.TryGetValue("Accept-Encoding", out var acceptEncoding))
            {
                headers["Accept-Encoding"] = NormalizeAcceptEncoding(acceptEncoding, BrotliSupported);
            }
            else
            {
                headers["Accept-Encoding"] = NormalizeAcceptEncoding(null, BrotliSupported);
            }

            if (!string.IsNullOrEmpty(antiCheatToken) && !headers.ContainsKey("X-antiCheatToken"))
            {
                headers["X-antiCheatToken"] = antiCheatToken;
            }

            return headers;
        }

        private static string NormalizeAcceptEncoding(string acceptEncoding, bool brotliAvailable)
        {
            var fallback = brotliAvailable ? "gzip, deflate, br" : "gzip, deflate";
            if (string.IsNullOrWhiteSpace(acceptEncoding))
            {
                return fallback;
            }

            var encodings = acceptEncoding
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(token => token.Trim())
                .Where(token => !string.IsNullOrEmpty(token))
                .ToList();

            if (encodings.Count == 0)
            {
                return fallback;
            }

            var filtered = new List<string>();
            foreach (var token in encodings)
            {
                var delimiterIndex = token.IndexOf(';');
                var name = delimiterIndex >= 0 ? token.Substring(0, delimiterIndex) : token;
                if (!brotliAvailable && name.Equals("br", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!filtered.Contains(token, StringComparer.OrdinalIgnoreCase))
                {
                    filtered.Add(token);
                }
            }

            if (filtered.Count == 0)
            {
                return fallback;
            }

            if (!filtered.Any(value => value.StartsWith("gzip", StringComparison.OrdinalIgnoreCase)))
            {
                filtered.Add("gzip");
            }

            if (!filtered.Any(value => value.StartsWith("deflate", StringComparison.OrdinalIgnoreCase)))
            {
                filtered.Add("deflate");
            }

            if (brotliAvailable &&
                !filtered.Any(value => value.StartsWith("br", StringComparison.OrdinalIgnoreCase)))
            {
                filtered.Add("br");
            }

            return string.Join(", ", filtered);
        }

        private string BuildEapiCookieHeader(IDictionary<string, object> headerMap)
        {
            var fingerprint = _authContext?.GetFingerprintSnapshot() ?? new FingerprintSnapshot();
            var cookieMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (_authContext != null)
            {
                var baseCookies = _authContext.BuildBaseCookieMap(string.IsNullOrEmpty(_musicU));
                if (baseCookies != null)
                {
                    foreach (var kvp in baseCookies)
                    {
                        if (!string.IsNullOrEmpty(kvp.Value))
                        {
                            cookieMap[kvp.Key] = kvp.Value;
                        }
                    }
                }
            }
            else
            {
                cookieMap["__remember_me"] = "true";
            }

            if (headerMap != null)
            {
                foreach (var kvp in headerMap)
                {
                    string valueString = Convert.ToString(kvp.Value, CultureInfo.InvariantCulture);
                    if (!string.IsNullOrEmpty(valueString))
                    {
                        cookieMap[kvp.Key] = valueString;
                    }
                }
            }

            // 基础指纹
            if (!cookieMap.ContainsKey("deviceId") && !string.IsNullOrEmpty(fingerprint.DeviceId))
            {
                cookieMap["deviceId"] = fingerprint.DeviceId;
            }
            if (!cookieMap.ContainsKey("os") && !string.IsNullOrEmpty(fingerprint.DeviceOs))
            {
                cookieMap["os"] = fingerprint.DeviceOs;
            }
            if (!cookieMap.ContainsKey("osver") && !string.IsNullOrEmpty(fingerprint.DeviceOsVersion))
            {
                cookieMap["osver"] = fingerprint.DeviceOsVersion;
            }
            if (!cookieMap.ContainsKey("appver") && !string.IsNullOrEmpty(fingerprint.DeviceAppVersion))
            {
                cookieMap["appver"] = fingerprint.DeviceAppVersion;
            }

            // 访客/指纹 ID
            if (!cookieMap.ContainsKey("_ntes_nuid") && !string.IsNullOrEmpty(fingerprint.NtesNuid))
            {
                cookieMap["_ntes_nuid"] = fingerprint.NtesNuid;
            }
            if (!cookieMap.ContainsKey("NMTID") && !string.IsNullOrEmpty(fingerprint.NmtId))
            {
                cookieMap["NMTID"] = fingerprint.NmtId;
            }
            if (!cookieMap.ContainsKey("WNMCID") && !string.IsNullOrEmpty(fingerprint.WnmCid))
            {
                cookieMap["WNMCID"] = fingerprint.WnmCid;
            }

            // 登录/匿名凭证
            if (!string.IsNullOrEmpty(_musicU))
            {
                cookieMap["MUSIC_U"] = _musicU;
            }
            else if (!cookieMap.ContainsKey("MUSIC_A") && !string.IsNullOrEmpty(fingerprint.MusicA))
            {
                cookieMap["MUSIC_A"] = fingerprint.MusicA;
            }

            if (!string.IsNullOrEmpty(_csrfToken))
            {
                cookieMap["__csrf"] = _csrfToken;
            }
            else if (_authContext?.CurrentAccountState?.CsrfToken != null && !cookieMap.ContainsKey("__csrf"))
            {
                cookieMap["__csrf"] = _authContext.CurrentAccountState.CsrfToken;
            }

            return cookieMap.Count == 0
                ? string.Empty
                : string.Join("; ", cookieMap.Select(kvp => $"{kvp.Key}={Uri.EscapeDataString(kvp.Value)}"));
        }

        private static string SelectBaseUrl(string requestedBaseUrl, int retryCount)
        {
            string candidate = (requestedBaseUrl ?? OFFICIAL_API_BASE).TrimEnd('/');
            if (string.IsNullOrWhiteSpace(candidate))
            {
                candidate = OFFICIAL_API_BASE;
            }

            if (retryCount <= 0)
            {
                return candidate;
            }

            // fallback to other domains when repeated failures occur
            int domainIndex = retryCount - 1;
            if (domainIndex < DomainFallbackOrder.Length)
            {
                return DomainFallbackOrder[domainIndex];
            }

            return candidate;
        }

        private void ApplyFingerprintHeaders(HttpRequestMessage request, bool injectRealIpHeaders = false)
        {
            if (request == null)
            {
                return;
            }

            if (injectRealIpHeaders)
            {
                string ip = GenerateRandomChineseIp();
                if (!string.IsNullOrEmpty(ip))
                {
                    request.Headers.Remove("X-Real-IP");
                    request.Headers.Remove("X-Forwarded-For");
                    request.Headers.TryAddWithoutValidation("X-Real-IP", ip);
                    request.Headers.TryAddWithoutValidation("X-Forwarded-For", ip);
                }
            }

            if (!request.Headers.Contains("Accept-Encoding"))
            {
                request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate, br");
            }

            if (!request.Headers.Contains("Accept"))
            {
                request.Headers.TryAddWithoutValidation("Accept", "*/*");
            }

            if (!request.Headers.Contains("Connection"))
            {
                request.Headers.TryAddWithoutValidation("Connection", "keep-alive");
            }
        }

        private async Task<bool> TryAutoRefreshLoginAsync()
        {
            try
            {
                return await RefreshLoginAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Auth] TryAutoRefreshLoginAsync 失败: {ex.Message}");
                return false;
            }
        }

        private async Task EnforceThrottleAsync(string actionKey, TimeSpan minInterval)
        {
            if (string.IsNullOrWhiteSpace(actionKey) || minInterval <= TimeSpan.Zero)
            {
                return;
            }

            TimeSpan delay = TimeSpan.Zero;
            var now = DateTime.UtcNow;

            lock (_throttleLock)
            {
                if (_lastActionTimestamps.TryGetValue(actionKey, out var lastTime))
                {
                    var elapsed = now - lastTime;
                    if (elapsed < minInterval)
                    {
                        delay = minInterval - elapsed;
                    }
                }

                _lastActionTimestamps[actionKey] = now + delay;
            }

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay).ConfigureAwait(false);
            }
        }

        private string GenerateRandomChineseIp()
        {
            if (ChineseIpRanges.Length == 0)
            {
                return "39.144.0.1";
            }

            (uint Start, uint End) range;
            lock (_random)
            {
                range = ChineseIpRanges[_random.Next(ChineseIpRanges.Length)];
            }

            uint span = range.End > range.Start ? range.End - range.Start : 1;
            uint offset;
            lock (_random)
            {
                offset = (uint)_random.Next((int)Math.Max(1, Math.Min(span, int.MaxValue)));
            }
            uint addr = range.Start + offset;
            return $"{(addr >> 24) & 255}.{(addr >> 16) & 255}.{(addr >> 8) & 255}.{addr & 255}";
        }

        private sealed class ApiTransientException : Exception
        {
            public int Code { get; }
            public ApiTransientException(int code, string message) : base(message)
            {
                Code = code;
            }
        }

        private sealed class ApiAccessRestrictedException : Exception
        {
            public int Code { get; }
            public ApiAccessRestrictedException(int code, string message) : base(message)
            {
                Code = code;
            }
        }

        private sealed class ApiResourceUnavailableException : Exception
        {
            public int Code { get; }
            public ApiResourceUnavailableException(int code, string message) : base(message)
            {
                Code = code;
            }
        }

        private sealed class ApiResponseCorruptedException : Exception
        {
            public HttpStatusCode StatusCode { get; }
            public string RequestUrl { get; }
            public string? DebugFilePath { get; }

            public ApiResponseCorruptedException(HttpStatusCode statusCode, string requestUrl, string message, string? debugFilePath = null, Exception? inner = null)
                : base(message, inner)
            {
                StatusCode = statusCode;
                RequestUrl = requestUrl;
                DebugFilePath = debugFilePath;
            }
        }

        /// <summary>
        /// 简化API GET 请求（降级策略）
        /// </summary>
        private async Task<T> GetSimplifiedApiAsync<T>(string endpoint, Dictionary<string, string>? parameters = null, CancellationToken cancellationToken = default)
        {
            if (!UseSimplifiedApi)
                throw new InvalidOperationException("Simplified API is disabled");

            try
            {
                var queryString = "";
                if (parameters != null && parameters.Count > 0)
                {
                    queryString = "?" + string.Join("&", parameters.Select(kv =>
                        $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
                }

                string url = $"{SIMPLIFIED_API_BASE}{endpoint}{queryString}";
                var response = await _simplifiedClient.GetAsync(url, cancellationToken);
                response.EnsureSuccessStatusCode();

                string responseText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return JsonConvert.DeserializeObject<T>(responseText);
            }
            catch
            {
                // 简化API失败，抛出异常由上层决定是否使用加密API
                throw;
            }
        }

        #endregion

    }
}
