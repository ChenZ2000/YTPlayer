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
        #region 私有方法

        private void ApplyBaseCookies(bool includeAnonymousToken = true)
        {
            if (_authContext == null)
            {
                return;
            }

            var baseCookies = _authContext.BuildBaseCookieMap(includeAnonymousToken);
            foreach (var kvp in baseCookies)
            {
                UpsertCookie(kvp.Key, kvp.Value);
            }
        }

        private void UpsertCookie(string name, string value)
        {
            if (string.IsNullOrEmpty(name) || value == null)
            {
                return;
            }

            lock (_cookieLock)
            {
                try
                {
                    var existing = _cookieContainer.GetCookies(MUSIC_URI);
                    if (existing[name] != null)
                    {
                        existing[name].Value = value;
                    }
                    else
                    {
                        var cookie = new Cookie(name, value, "/", ".music.163.com");
                        _cookieContainer.Add(MUSIC_URI, cookie);
                    }

                    var interfaceCookies = _cookieContainer.GetCookies(INTERFACE_URI);
                    if (interfaceCookies[name] != null)
                    {
                        interfaceCookies[name].Value = value;
                    }
                    else
                    {
                        var interfaceCookie = new Cookie(name, value, "/", ".music.163.com");
                        _cookieContainer.Add(INTERFACE_URI, interfaceCookie);
                    }

                    // ⭐ 同时添加到 EAPI_URI (interface3)
                    var eapiCookies = _cookieContainer.GetCookies(EAPI_URI);
                    if (eapiCookies[name] != null)
                    {
                        eapiCookies[name].Value = value;
                    }
                    else
                    {
                        var eapiCookie = new Cookie(name, value, "/", ".music.163.com");
                        _cookieContainer.Add(EAPI_URI, eapiCookie);
                    }
                }
                catch (CookieException ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[COOKIE] Upsert {name} 失败: {ex.Message}");
                }
            }
        }

        private bool ApplySetCookieHeader(string? rawSetCookie)
        {
            if (string.IsNullOrWhiteSpace(rawSetCookie))
            {
                return false;
            }

            var segments = rawSetCookie.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                return false;
            }

            var nameValue = segments[0].Split(new[] { '=' }, 2);
            if (nameValue.Length != 2)
            {
                return false;
            }

            string name = nameValue[0].Trim();
            string value = nameValue[1].Trim();
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            UpsertCookie(name, value);

            if (name.Equals("MUSIC_U", StringComparison.OrdinalIgnoreCase))
            {
                _musicU = value;
                System.Diagnostics.Debug.WriteLine($"[COOKIE] Captured MUSIC_U (len={value.Length})");
            }
            else if (name.Equals("__csrf", StringComparison.OrdinalIgnoreCase))
            {
                _csrfToken = value;
                System.Diagnostics.Debug.WriteLine($"[COOKIE] Captured __csrf ({_csrfToken})");
            }
            else if (name.Equals("MUSIC_A", StringComparison.OrdinalIgnoreCase))
            {
                System.Diagnostics.Debug.WriteLine("[COOKIE] Captured MUSIC_A from Set-Cookie");
            }

            return true;
        }

        /// <summary>
        /// 设置默认请求头（参考 Python 版本 Netease-music.py:7598-7606）
        /// 使用完整的浏览器请求头，避免触发风控机制返回 404
        /// </summary>
        private void SetupDefaultHeaders()
        {
            _httpClient.DefaultRequestHeaders.Clear();
            // Python 版本完整请求头（7600-7605 行）
            var desktopUa = _desktopUserAgent ?? USER_AGENT;
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", desktopUa);
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Referer", REFERER);
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Origin", ORIGIN);
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");

            _simplifiedClient.DefaultRequestHeaders.Clear();
            _simplifiedClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", desktopUa);
        }

        /// <summary>
        /// 更新Cookies
        /// ⭐⭐⭐ 核心修复：恢复 ApplyBaseCookies 调用，确保桌面设备指纹Cookie始终存在
        /// 修复8821风控错误：WEAPI请求（包括二维码登录）必须包含完整设备指纹
        /// </summary>
        private void UpdateCookies()
        {
            if (_disposed)
            {
                return;
            }

            // ⭐⭐⭐ 核心修复：恢复 ApplyBaseCookies 调用
            // 参考备份版本成功实现，始终确保桌面设备指纹Cookie存在
            // 这些Cookie包括: __remember_me, os, osver, appver, buildver, channel, deviceId, sDeviceId
            ApplyBaseCookies(includeAnonymousToken: string.IsNullOrEmpty(_musicU));

            if (!string.IsNullOrEmpty(_musicU))
            {
                UpsertCookie("MUSIC_U", _musicU);
                if (string.IsNullOrEmpty(_csrfToken) && _musicU.Length > 10)
                {
                    _csrfToken = EncryptionHelper.ComputeMd5(_musicU).Substring(0, Math.Min(32, _musicU.Length));
                }

                System.Diagnostics.Debug.WriteLine($"[Cookie] ✅ 已更新登录凭证: MUSIC_U (长度={_musicU.Length}), __csrf={_csrfToken?.Substring(0, Math.Min(8, _csrfToken.Length))}...");
            }

            if (!string.IsNullOrEmpty(_csrfToken))
            {
                UpsertCookie("__csrf", _csrfToken);
            }
        }

        /// <summary>
        /// 从 Cookie 字符串设置 Cookie（参考 Python 版本 set_cookie_string，Netease-music.py:412-422）
        /// </summary>
        /// <param name="cookieString">Cookie 字符串，格式：'MUSIC_U=xxxx; __csrf=yyyy; os=pc; appver=2.10.13;'</param>
        public void SetCookieString(string cookieString)
        {
            if (string.IsNullOrWhiteSpace(cookieString))
                return;

            _musicU = null;
            _csrfToken = null;

            var parts = cookieString.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var trimmedPart = part.Trim();
                if (string.IsNullOrEmpty(trimmedPart) || !trimmedPart.Contains("="))
                    continue;

                var kvPair = trimmedPart.Split(new[] { '=' }, 2);
                if (kvPair.Length != 2)
                    continue;

                var key = kvPair[0].Trim();
                var value = kvPair[1].Trim();
                if (string.IsNullOrEmpty(key))
                    continue;

                UpsertCookie(key, value);

                switch (key)
                {
                    case "MUSIC_U":
                        _musicU = value;
                        break;
                    case "__csrf":
                        _csrfToken = value;
                        break;
                    case "MUSIC_A":
                        // Note: MUSIC_A is now managed by AccountState via AuthContext
                        break;
                }
            }

            if (string.IsNullOrEmpty(_csrfToken) && !string.IsNullOrEmpty(_musicU) && _musicU.Length > 10)
            {
                _csrfToken = EncryptionHelper.ComputeMd5(_musicU).Substring(0, Math.Min(32, _musicU.Length));
            }

            ApplyBaseCookies(includeAnonymousToken: string.IsNullOrEmpty(_musicU));
            UpdateCookies();

            try
            {
                var cookies = _cookieContainer.GetCookies(MUSIC_URI);
                _authContext?.SyncFromCookies(cookies);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[COOKIE] SetCookieString 同步失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 构建当前Cookie字符串快照
        /// </summary>
        private string BuildCookieSnapshot()
        {
            try
            {
                var cookies = _cookieContainer.GetCookies(MUSIC_URI);
                if (cookies == null || cookies.Count == 0)
                {
                    return string.Empty;
                }

                var builder = new StringBuilder();
                foreach (Cookie cookie in cookies)
                {
                    if (builder.Length > 0)
                    {
                        builder.Append("; ");
                    }
                    builder.Append(cookie.Name).Append('=').Append(cookie.Value);
                }
                return builder.ToString();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[COOKIE] 构建Cookie快照失败: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// 获取当前 Cookie 列表（用于配置持久化）。
        /// </summary>
        public void ApplyCookies(IEnumerable<CookieItem> cookies)
        {
            if (cookies == null)
                return;

            var builder = new StringBuilder();
            foreach (var item in cookies)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Name))
                    continue;

                if (builder.Length > 0)
                    builder.Append("; ");

                builder.Append(item.Name).Append('=').Append(item.Value ?? string.Empty);
            }

            if (builder.Length == 0)
                return;

            try
            {
                SetCookieString(builder.ToString());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[COOKIE] ApplyCookies -> SetCookieString 异常: {ex.Message}");
            }

            UpdateCookies();
        }

        /// <summary>
        /// 清空所有 Cookie（用于退出登录）。
        /// ⭐⭐⭐ 完全清理所有认证数据，确保干净状态
        /// </summary>
        public void ClearCookies()
        {
            System.Diagnostics.Debug.WriteLine("[Cookie] 🧹 开始清理所有Cookie和认证数据...");

            try
            {
                var field = typeof(CookieContainer).GetField("m_domainTable", BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null)
                {
                    if (field.GetValue(_cookieContainer) is Hashtable table)
                    {
                        int cookieCount = table.Count;
                        table.Clear();
                        System.Diagnostics.Debug.WriteLine($"[Cookie] ✅ 已清空 CookieContainer ({cookieCount} 个域)");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Cookie] ⚠️ 清空 CookieContainer 失败: {ex.Message}");
            }

            // 清理登录凭证
            _musicU = null;
            _csrfToken = null;

            System.Diagnostics.Debug.WriteLine("[Cookie] ✅ 已清理 MUSIC_U 和 __csrf");

            // ⭐⭐⭐ 移除 UpdateCookies() 调用 - 已全部清空，无需更新
            // ⭐⭐⭐ 移除 ClearLoginProfile() 调用 - LogoutAsync 已经调用过了
            // 原代码：UpdateCookies();
            // 原代码：_authContext?.ClearLoginProfile();

            System.Diagnostics.Debug.WriteLine("[Cookie] ✅✅✅ Cookie清理完成");
        }

        /// <summary>
        /// 退出登录或需要重建访客态时，重新构建匿名会话 Cookie。
        /// </summary>
        public void ResetToAnonymousSession(bool clearAccountState = false)
        {
            System.Diagnostics.Debug.WriteLine("[Cookie] 🔄 开始重建匿名会话 Cookie...");

            ClearCookies();

            if (clearAccountState)
            {
                _authContext?.ClearLoginProfile();
            }

            UpdateCookies();

            try
            {
                var cookies = _cookieContainer.GetCookies(MUSIC_URI);
                _authContext?.SyncFromCookies(cookies);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Cookie] ⚠️ 匿名会话同步失败: {ex.Message}");
            }

            System.Diagnostics.Debug.WriteLine("[Cookie] ✅ 匿名会话 Cookie 已重建");
        }

        /// <summary>
        /// 登录前清理当前 Cookie，避免旧会话残留。
        /// </summary>
        public void PrepareForLogin()
        {
            ClearCookies();
        }

        /// <summary>
        /// 登录成功后标准化 Cookie 并同步内部状态
        /// </summary>
        private string FinalizeLoginCookies(string rawCookieString)
        {
            if (!string.IsNullOrWhiteSpace(rawCookieString))
            {
                try
                {
                    SetCookieString(rawCookieString);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[COOKIE] SetCookieString 异常: {ex.Message}");
                }
            }

            string snapshot = BuildCookieSnapshot();
            try
            {
                var cookies = _cookieContainer.GetCookies(MUSIC_URI);
                if (cookies != null && cookies.Count > 0)
                {
                    var music = cookies["MUSIC_U"];
                    if (music != null && !string.IsNullOrEmpty(music.Value))
                    {
                        _musicU = music.Value;
                    }

                    var csrf = cookies["__csrf"];
                    if (csrf != null && !string.IsNullOrEmpty(csrf.Value))
                    {
                        _csrfToken = csrf.Value;
                    }

                    _authContext?.SyncFromCookies(cookies);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[COOKIE] 读取Cookie容器失败: {ex.Message}");
            }

            if (string.IsNullOrEmpty(_csrfToken) && !string.IsNullOrEmpty(_musicU) && _musicU.Length > 10)
            {
                _csrfToken = EncryptionHelper.ComputeMd5(_musicU).Substring(0, Math.Min(32, _musicU.Length));
                UpsertCookie("__csrf", _csrfToken);
                snapshot = BuildCookieSnapshot();
            }

            UpdateCookies();

            if (_authContext != null)
            {
                try
                {
                    var cookieItems = GetAllCookies();
                    var state = _authContext.CreateLoginStateSnapshot(snapshot, cookieItems, null);
                    _authContext.UpdateAccountState(state);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Auth] 更新持久化登录状态失败: {ex.Message}");
                }
            }
            return string.IsNullOrEmpty(snapshot) ? (rawCookieString ?? string.Empty) : snapshot;
        }

        /// <summary>
        /// 更新登录资料并持久化到 account.json/config.json
        /// </summary>
        public void ApplyLoginProfile(UserAccountInfo profile)
        {
            if (_authContext == null)
            {
                return;
            }

            _authContext.ApplyLoginProfile(profile, _musicU, _csrfToken);

            try
            {
                var cookieItems = GetAllCookies();
                var snapshot = GetCurrentCookieString();
                var state = _authContext.CreateLoginStateSnapshot(snapshot, cookieItems, profile);
                _authContext.UpdateAccountState(state);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Auth] 同步登录资料失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取当前Cookie字符串
        /// </summary>
        public string GetCurrentCookieString()
        {
            var snapshot = BuildCookieSnapshot();
            if (!string.IsNullOrEmpty(snapshot))
            {
                return snapshot;
            }

            if (string.IsNullOrEmpty(_musicU))
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            builder.Append("MUSIC_U=").Append(_musicU);
            if (!string.IsNullOrEmpty(_csrfToken))
            {
                builder.Append("; __csrf=").Append(_csrfToken);
            }

            return builder.ToString();
        }

        /// <summary>
        /// 检查 Cookie 是否就绪（参考 Python 版本 _cookie_ready，Netease-music.py:450-474）
        /// </summary>
        /// <returns>Cookie 是否包含必要的 MUSIC_U 和 __csrf</returns>
        public bool IsCookieReady()
        {
            return !string.IsNullOrEmpty(_musicU) && !string.IsNullOrEmpty(_csrfToken);
        }

        /// <summary>
        /// 加载默认示范 Cookie（参考 Python 版本 APP_COOKIE）
        /// </summary>
        public void LoadDefaultCookie()
        {
            if (!string.IsNullOrEmpty(DEFAULT_MUSIC_U) && !string.IsNullOrEmpty(DEFAULT_CSRF))
            {
                _musicU = DEFAULT_MUSIC_U;
                _csrfToken = DEFAULT_CSRF;
                UpdateCookies();
            }
        }

        /// <summary>
        /// 获取音质对应的level参数（参考 Python 版本 quality_map，5742-5749行）
        /// </summary>
        private static string GetQualityLevel(QualityLevel quality)
        {
            switch (quality)
            {
                case QualityLevel.Standard:
                    return "standard";
                case QualityLevel.High:
                    return "exhigh";  // Python版本: "极高音质": "exhigh"
                case QualityLevel.Lossless:
                    return "lossless";
                case QualityLevel.HiRes:
                    return "hires";
                case QualityLevel.SurroundHD:
                    return "jyeffect";
                case QualityLevel.Dolby:
                    return "sky";
                case QualityLevel.Master:
                    return "jymaster";
                default:
                    return "standard";
            }
        }

        /// <summary>
        /// 处理API错误码
        /// </summary>
        private void HandleApiError(int code, string message)
        {
            switch (code)
            {
                case 200:
                    return;
                case 301:
                    throw new UnauthorizedAccessException("未登录或登录已过期");
                case 400:
                    throw new ArgumentException($"请求参数错误: {message}");
                case 401:
                case 403:
                case -460:
                    throw new ApiAccessRestrictedException(code, string.IsNullOrWhiteSpace(message) ? "接口暂不可用，可能需要代理或官方客户端验证" : message);
                case 404:
                case -110:
                    throw new ApiResourceUnavailableException(code, "资源不存在或已下架");
                case 405:
                    throw new InvalidOperationException("请求频率过快，请稍后再试");
                case 429:
                case 500:
                case 502:
                case 503:
                case 504:
                    throw new ApiTransientException(code, string.IsNullOrWhiteSpace(message) ? "服务器繁忙，请稍后重试" : message);
                default:
                    throw new InvalidOperationException($"API错误 [{code}]: {message}");
            }
        }

        private static string DecodeResponseContent(HttpResponseMessage response, byte[] rawBytes)
        {
            if (rawBytes == null || rawBytes.Length == 0)
            {
                return string.Empty;
            }

            // 处理 Content-Encoding（gzip/deflate/br）
            var encodings = response?.Content?.Headers?.ContentEncoding;
            if (encodings != null && encodings.Any())
            {
                foreach (var encodingName in encodings.Reverse())
                {
                    try
                    {
                        if (encodingName.Equals("gzip", StringComparison.OrdinalIgnoreCase))
                        {
                            rawBytes = DecompressBytes(rawBytes, stream => new GZipStream(stream, CompressionMode.Decompress));
                        }
                        else if (encodingName.Equals("deflate", StringComparison.OrdinalIgnoreCase))
                        {
                            rawBytes = DecompressBytes(rawBytes, stream => new DeflateStream(stream, CompressionMode.Decompress));
                        }
                        else if (encodingName.Equals("br", StringComparison.OrdinalIgnoreCase) ||
                                 encodingName.Equals("brotli", StringComparison.OrdinalIgnoreCase))
                        {
                            rawBytes = DecompressBrotli(rawBytes);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[DecodeResponseContent] 无法按 {encodingName} 解压: {ex.Message}");
                        // 如果解压失败，保留原始字节，继续尝试解码
                    }
                }
            }

            // ⚠️ 针对部分 CDN/代理丢失 Content-Encoding 头的兜底解压（与 EAPI 逻辑对齐）
            try
            {
                rawBytes = TryDecompressCommonPayload(rawBytes);
            }
            catch (Exception ex)
            {
                // 兜底解压失败视为网络抖动，继续后续解码流程
                System.Diagnostics.Debug.WriteLine($"[DecodeResponseContent] 兜底解压失败（忽略）: {ex.Message}");
            }

            Encoding encoding = null;
            string charset = response?.Content?.Headers?.ContentType?.CharSet;

            if (!string.IsNullOrWhiteSpace(charset))
            {
                try
                {
                    encoding = Encoding.GetEncoding(charset.Trim('"'));
                }
                catch
                {
                    // 忽略非法编码声明
                }
            }

            // BOM 检测
            if (encoding == null)
            {
                if (rawBytes.Length >= 3 &&
                    rawBytes[0] == 0xEF &&
                    rawBytes[1] == 0xBB &&
                    rawBytes[2] == 0xBF)
                {
                    return Encoding.UTF8.GetString(rawBytes, 3, rawBytes.Length - 3);
                }

                if (rawBytes.Length >= 2 &&
                    rawBytes[0] == 0xFF &&
                    rawBytes[1] == 0xFE)
                {
                    return Encoding.Unicode.GetString(rawBytes, 2, rawBytes.Length - 2);
                }

                if (rawBytes.Length >= 2 &&
                    rawBytes[0] == 0xFE &&
                    rawBytes[1] == 0xFF)
                {
                    return Encoding.BigEndianUnicode.GetString(rawBytes, 2, rawBytes.Length - 2);
                }
            }

            if (encoding == null)
            {
                // 识别无 BOM 的 UTF-16
                if (rawBytes.Length >= 4 &&
                    rawBytes[1] == 0x00 &&
                    rawBytes[3] == 0x00)
                {
                    encoding = Encoding.Unicode; // UTF-16 LE
                }
                else if (rawBytes.Length >= 4 &&
                         rawBytes[0] == 0x00 &&
                         rawBytes[2] == 0x00)
                {
                    encoding = Encoding.BigEndianUnicode; // UTF-16 BE
                }
            }

            if (encoding == null)
            {
                // 回退优先使用UTF-8
                encoding = Encoding.UTF8;
            }

            try
            {
                return encoding.GetString(rawBytes);
            }
            catch
            {
                try
                {
                    return Encoding.UTF8.GetString(rawBytes);
                }
                catch
                {
                    return Encoding.Default.GetString(rawBytes);
                }
            }
        }

        private static byte[] DecompressBytes(byte[] source, Func<Stream, Stream> streamFactory)
        {
            if (source == null || source.Length == 0)
            {
                return source ?? Array.Empty<byte>();
            }

            using (var input = new MemoryStream(source))
            using (var decompressor = streamFactory(input))
            using (var output = new MemoryStream())
            {
                decompressor.CopyTo(output);
                return output.ToArray();
            }
        }

        private static byte[] DecompressBrotli(byte[] source)
        {
            if (source == null || source.Length == 0)
            {
                return source ?? Array.Empty<byte>();
            }

            try
            {
                return Brotli.DecompressBuffer(source, 0, source.Length, null);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DecodeResponseContent] Brotli 解压失败: {ex.Message}");
                return source;
            }
        }

        /// <summary>
        /// 清理JSON响应，处理可能的多余内容或格式问题
        /// </summary>
        private string CleanJsonResponse(string responseText)
        {
            if (string.IsNullOrEmpty(responseText))
                return responseText;

            // 移除BOM (Byte Order Mark)
            responseText = responseText.TrimStart('\uFEFF', '\u200B');

            // 移除前后空白字符
            responseText = responseText.Trim();

            // 如果响应包含多个JSON对象，只提取第一个
            // 查找第一个完整的JSON对象
            int braceCount = 0;
            int firstBraceIndex = responseText.IndexOf('{');

            if (firstBraceIndex >= 0)
            {
                for (int i = firstBraceIndex; i < responseText.Length; i++)
                {
                    if (responseText[i] == '{')
                    {
                        braceCount++;
                    }
                    else if (responseText[i] == '}')
                    {
                        braceCount--;
                        if (braceCount == 0)
                        {
                            // 找到了第一个完整的JSON对象
                            string cleanJson = responseText.Substring(firstBraceIndex, i - firstBraceIndex + 1);

                            // 如果后面还有内容，记录警告
                            if (i + 1 < responseText.Length)
                            {
                                string extraContent = responseText.Substring(i + 1).Trim();
                                if (!string.IsNullOrEmpty(extraContent))
                                {
                                    System.Diagnostics.Debug.WriteLine($"[WEAPI] 警告：响应包含额外内容（已忽略）: {extraContent.Substring(0, Math.Min(50, extraContent.Length))}...");
                                }
                            }

                            return cleanJson;
                        }
                    }
                }
            }

            // 如果没有找到完整的JSON对象，返回原文
            return responseText;
        }

        private static string AppendQueryParameter(string url, string key, string value)
        {
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key))
            {
                return url ?? string.Empty;
            }

            string separator;
            if (!url.Contains("?"))
            {
                separator = "?";
            }
            else if (url.EndsWith("?") || url.EndsWith("&"))
            {
                separator = string.Empty;
            }
            else
            {
                separator = "&";
            }

            string encodedValue = Uri.EscapeDataString(value ?? string.Empty);
            return $"{url}{separator}{key}={encodedValue}";
        }

        private static string? TryWriteDebugFile(string prefix, string extension, string content)
        {
            try
            {
                string safeExtension = string.IsNullOrWhiteSpace(extension) ? "log" : extension.TrimStart('.');
                string fileName = $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.{safeExtension}";
                string path = Path.Combine(Path.GetTempPath(), fileName);
                File.WriteAllText(path, content);
                return path;
            }
            catch
            {
                return null;
            }
        }

        private async Task<T> HandleWeApiInvalidResponseAsync<T>(
            string message,
            string url,
            HttpStatusCode statusCode,
            string? debugFile,
            string path,
            object payload,
            int retryCount,
            bool skipErrorHandling,
            CancellationToken cancellationToken,
            string baseUrl,
            bool autoConvertApiSegment,
            string? userAgentOverride)
        {
            if (retryCount < MAX_RETRY_COUNT)
            {
                int delayMs = GetRandomRetryDelay();
                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                return await PostWeApiAsync<T>(
                    path,
                    payload,
                    retryCount + 1,
                    skipErrorHandling,
                    cancellationToken,
                    baseUrl,
                    autoConvertApiSegment,
                    userAgentOverride).ConfigureAwait(false);
            }

            if (typeof(T) == typeof(JObject))
            {
                var error = new JObject
                {
                    ["code"] = -1,
                    ["message"] = message,
                    ["status"] = (int)statusCode
                };
                return (T)(object)error;
            }

            throw new ApiResponseCorruptedException(statusCode, url, message, debugFile);
        }

        #endregion

    }
}
