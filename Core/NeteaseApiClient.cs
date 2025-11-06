using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using YTPlayer.Core.Auth;
using YTPlayer.Models;
using YTPlayer.Utils;
using YTPlayer.Models.Auth;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using BrotliSharpLib;
using System.Reflection;
using YTPlayer.Core.Streaming;

#pragma warning disable CS8600, CS8601, CS8602, CS8603, CS8604, CS8625

namespace YTPlayer.Core
{
    /// <summary>
    /// 网易云音乐API客户端
    /// </summary>
    public class NeteaseApiClient : IDisposable
    {
        #region 常量定义

        // API 基础地址
        private const string OFFICIAL_API_BASE = "https://music.163.com";
        private const string SIMPLIFIED_API_BASE = "http://159.75.21.45:5000";
        private static readonly Uri MUSIC_URI = new Uri(OFFICIAL_API_BASE);
        private static readonly Uri INTERFACE_URI = new Uri("https://interface.music.163.com");
        // ⭐ iOS 端 EAPI 域名 - 参考 netease-music-simple-player
        private const string EAPI_BASE_URL = "https://interface3.music.163.com";
        private static readonly Uri EAPI_URI = new Uri(EAPI_BASE_URL);
        private const bool BrotliSupported = true;

        // 请求头（参考 Python 版本 Netease-music.py:7600-7605）
        // 使用完整的浏览器 User-Agent，避免触发风控
        private const string USER_AGENT = AuthConstants.DesktopUserAgent;
        private const string USER_AGENT_IOS = "NeteaseMusic/8.10.90(8010090);Dalvik/2.1.0 (Linux; U; Android 13; 2211133C Build/TQ3A.230805.001)";
        private const string REFERER = "https://music.163.com";
        private const string ORIGIN = "https://music.163.com";
        private const string DEFAULT_APPVER = AuthConstants.DesktopAppVersion;

        // 重试设置（参考 netease-music-simple-player 的自适应延迟策略）
        private const int MAX_RETRY_COUNT = 4;
        private const int RETRY_DELAY_MS = 1000;  // 保留作为 fallback
        private const int MIN_RETRY_DELAY_MS = 50;   // 最小延迟
        private const int MAX_RETRY_DELAY_MS = 500;  // 最大延迟

        #endregion

        #region 字段和属性

        private readonly HttpClient _httpClient;
        private readonly HttpClient _simplifiedClient;
        private readonly HttpClient _eapiClient;  // 专用于EAPI请求，不使用CookieContainer
        private readonly HttpClient _iOSLoginClient;  // iOS登录专用（UseCookies=false，避免自动Cookie注入）
        private readonly HttpClient _uploadHttpClient;  // 云盘上传专用客户端
        private readonly CookieContainer _cookieContainer;
        private readonly object _cookieLock = new object();
        private readonly ConfigManager _configManager;
        private readonly ConfigModel _config;
        private readonly AuthContext _authContext;
        private string? _musicU;
        private string? _csrfToken;
        private bool _disposed;
        private readonly Random _random = new Random();
        private readonly string? _deviceId;
        private readonly string? _desktopUserAgent;

        // 默认示范 Cookie（参考 Python 版本 Netease-music.py:410）
        // 这是一个公开的示范 Cookie，用于获取高音质歌曲
        private const string DEFAULT_MUSIC_U = "";  // 待填入示范 Cookie
        private const string DEFAULT_CSRF = "";

        /// <summary>
        /// 是否启用简化API（降级策略）
        /// </summary>
        public bool UseSimplifiedApi { get; set; } = false;

        /// <summary>
        /// 是否使用个人 Cookie 播放（自动检测登录状态）
        /// 参考 Python 版本 Netease-music.py:2512
        /// </summary>
        public bool UsePersonalCookie => !string.IsNullOrEmpty(_musicU);

        /// <summary>
        /// Cookie: MUSIC_U
        /// </summary>
        public string? MusicU
        {
            get => _musicU;
            set
            {
                _musicU = value;
                UpdateCookies();
            }
        }

        /// <summary>
        /// CSRF Token
        /// </summary>
        public string? CsrfToken
        {
            get => _csrfToken;
            set
            {
                _csrfToken = value;
                UpdateCookies();
            }
        }

        #endregion

        #region 构造函数

        /// <summary>
        /// 初始化网易云音乐API客户端
        /// </summary>
        private static ConfigModel CreateConfigFromParameters(string musicU, string csrfToken, string deviceId)
        {
            ConfigModel config;
            try
            {
                config = ConfigManager.Instance.Load();
            }
            catch
            {
                config = new ConfigModel();
            }

            // Note: DeviceId is now managed by AccountState, not ConfigModel
            // If you need to set a custom deviceId for testing, it should be set on AccountState
            // after creating the NeteaseApiClient instance

            // Note: MusicU and CsrfToken are now managed by AccountState, not ConfigModel
            // These will be set directly on the NeteaseApiClient properties in the constructor

            return config;
        }

        public NeteaseApiClient(ConfigModel? config = null)
        {
            _configManager = ConfigManager.Instance;
            _config = config ?? _configManager.Load() ?? new ConfigModel();
            _authContext = new AuthContext(_configManager, _config);

            _deviceId = _authContext.CurrentAccountState?.DeviceId;
            _desktopUserAgent = _authContext.CurrentAccountState?.DesktopUserAgent ?? AuthConstants.DesktopUserAgent;

            _cookieContainer = new CookieContainer();
            var handler = new HttpClientHandler
            {
                CookieContainer = _cookieContainer,
                UseCookies = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };

            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(15)  // 优化：降低超时时间，配合音质fallback机制加快加载
            };

            _simplifiedClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(8)   // 优化：降低公共API超时时间
            };

            // EAPI专用客户端：不使用CookieContainer，避免Cookie冲突
            var eapiHandler = new HttpClientHandler
            {
                UseCookies = false  // 关键：不自动处理Cookie
                // EAPI 返回的是 AES 密文，不能启用自动解压缩，否则密文会被破坏
            };
            _eapiClient = new HttpClient(eapiHandler)
            {
                Timeout = TimeSpan.FromSeconds(15)
            };

            // iOS登录专用客户端：模拟参考项目 netease-music-simple-player (UseCookies=false)
            // 关键修复：避免 HttpClientHandler 自动注入 _cookieContainer 中的访客Cookie
            // 参考项目使用 UseCookies=false + 手动Cookie管理，确保首次登录时发送零Cookie
            var iOSLoginHandler = new HttpClientHandler
            {
                UseCookies = false,  // ⭐ 核心：禁用自动Cookie管理，完全手动控制Cookie
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
            _iOSLoginClient = new HttpClient(iOSLoginHandler)
            {
                Timeout = TimeSpan.FromSeconds(15)
            };

            _uploadHttpClient = OptimizedHttpClientFactory.CreateForPreCache(TimeSpan.FromMinutes(30));

            SetupDefaultHeaders();

            // 只从 account.json 读取账户数据（不再从 config.json 读取）
            var persistedState = _authContext.CurrentAccountState;
            if (persistedState != null && persistedState.IsLoggedIn)
            {
                try
                {
                    if (persistedState.Cookies != null && persistedState.Cookies.Count > 0)
                    {
                        ApplyCookies(persistedState.Cookies);
                    }
                    else if (!string.IsNullOrEmpty(persistedState.Cookie))
                    {
                        SetCookieString(persistedState.Cookie);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Auth] 还原持久化Cookie失败: {ex.Message}");
                }

                if (!string.IsNullOrEmpty(persistedState.MusicU))
                {
                    _musicU = persistedState.MusicU;
                }

                if (!string.IsNullOrEmpty(persistedState.CsrfToken))
                {
                    _csrfToken = persistedState.CsrfToken;
                }
            }

            _authContext.GetActiveAntiCheatToken();

            // ⭐ 访客模式下必须初始化基础 Cookie（WEAPI 请求依赖这些 Cookie）
            // EAPI 请求通过 header 传递设备信息，不依赖 Cookie
            // WEAPI 请求（榜单、搜索、登录状态等）必须有访客令牌（MUSIC_A, NMTID 等）
            if (string.IsNullOrEmpty(_musicU))
            {
                ApplyBaseCookies(includeAnonymousToken: true);
            }

            UpdateCookies();
        }

        public NeteaseApiClient(string musicU, string csrfToken, string deviceId)
            : this(CreateConfigFromParameters(musicU, csrfToken, deviceId))
        {
            if (!string.IsNullOrWhiteSpace(musicU))
            {
                MusicU = musicU;
            }

            if (!string.IsNullOrWhiteSpace(csrfToken))
            {
                CsrfToken = csrfToken;
            }
        }

        /// <summary>
        /// 会话热身：发起轻量级API请求，避免冷启动风控
        /// 解决应用刚启动后立即请求复杂API导致的空响应问题
        /// </summary>
        public async Task WarmupSessionAsync()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("[SessionWarmup] 开始会话热身...");

                // ⭐ 方案1-1: 请求轻量级登录状态接口（不处理结果）
                try
                {
                    await GetLoginStatusAsync();
                    System.Diagnostics.Debug.WriteLine("[SessionWarmup] ✓ 登录状态接口热身成功");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[SessionWarmup] 热身请求失败（忽略）: {ex.Message}");
                }

                // ⭐ 方案1-2: 短暂延迟，给服务器建立会话的时间
                await Task.Delay(800);

                System.Diagnostics.Debug.WriteLine("[SessionWarmup] ✓ 会话热身完成");
            }
            catch (Exception ex)
            {
                // 完全静默失败，不影响主流程
                System.Diagnostics.Debug.WriteLine($"[SessionWarmup] 热身异常（忽略）: {ex.Message}");
            }
        }

        #endregion

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
        public List<CookieItem> GetAllCookies()
        {
            var result = new List<CookieItem>();

            try
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var uris = new[]
                {
                    MUSIC_URI,
                    INTERFACE_URI,
                    EAPI_URI  // ⭐ 添加 interface3 域名支持
                };

                foreach (var uri in uris)
                {
                    CookieCollection collection = null;
                    try
                    {
                        collection = _cookieContainer.GetCookies(uri);
                    }
                    catch { }

                    if (collection == null || collection.Count == 0)
                        continue;

                    foreach (Cookie cookie in collection)
                    {
                        string key = $"{cookie.Name}|{cookie.Domain}|{cookie.Path}";
                        if (seen.Add(key))
                        {
                            result.Add(new CookieItem
                            {
                                Name = cookie.Name,
                                Value = cookie.Value,
                                Domain = cookie.Domain,
                                Path = cookie.Path
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[COOKIE] 获取Cookie列表失败: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// 获取当前登录状态的快照副本，供上层安全读取。
        /// </summary>
        public AccountState GetAccountStateSnapshot()
        {
            if (_authContext == null)
            {
                return new AccountState { IsLoggedIn = false };
            }

            try
            {
                var state = _authContext.CurrentAccountState;
                return CloneAccountState(state);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Auth] 获取登录状态快照失败: {ex.Message}");
                return new AccountState { IsLoggedIn = false };
            }
        }

        private static AccountState CloneAccountState(AccountState source)
        {
            if (source == null)
            {
                return new AccountState { IsLoggedIn = false };
            }

            var clone = new AccountState
            {
                IsLoggedIn = source.IsLoggedIn,
                Cookie = source.Cookie,
                MusicU = source.MusicU,
                CsrfToken = source.CsrfToken,
                UserId = source.UserId,
                Nickname = source.Nickname,
                AvatarUrl = source.AvatarUrl,
                VipType = source.VipType,
                LastUpdated = source.LastUpdated,
                DeviceId = source.DeviceId,
                NmtId = source.NmtId,
                NtesNuid = source.NtesNuid,
                WnmCid = source.WnmCid,
                AntiCheatToken = source.AntiCheatToken,
                AntiCheatTokenExpiresAt = source.AntiCheatTokenExpiresAt
            };

            clone.Cookies = CloneCookieItems(source.Cookies);
            return clone;
        }

        private static List<CookieItem> CloneCookieItems(IEnumerable<CookieItem> items)
        {
            var clone = new List<CookieItem>();
            if (items == null)
            {
                return clone;
            }

            foreach (var item in items)
            {
                if (item == null)
                {
                    continue;
                }

                clone.Add(new CookieItem
                {
                    Name = item.Name,
                    Value = item.Value,
                    Domain = item.Domain,
                    Path = item.Path
                });
            }

            return clone;
        }

        /// <summary>
        /// 应用配置中保存的 Cookie 列表。
        /// </summary>
        /// <param name="cookies">Cookie 集合</param>
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
                case 301:
                    throw new UnauthorizedAccessException("未登录或登录已过期");
                case 405:
                    throw new InvalidOperationException("请求频率过快，请稍后再试");
                case 400:
                    throw new ArgumentException($"请求参数错误: {message}");
                case 404:
                    throw new InvalidOperationException("资源不存在");
                case 500:
                    throw new InvalidOperationException($"服务器错误: {message}");
                default:
                    if (code != 200)
                    {
                        throw new InvalidOperationException($"API错误 [{code}]: {message}");
                    }
                    break;
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

        #endregion

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
            bool autoConvertApiSegment = false)
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
                string normalizedBaseUrl = (baseUrl ?? OFFICIAL_API_BASE).TrimEnd('/');
                if (string.IsNullOrWhiteSpace(normalizedBaseUrl))
                {
                    normalizedBaseUrl = OFFICIAL_API_BASE;
                }

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
                    string sep = url.Contains("?") ? "&" : "?";
                    url = $"{url}{sep}csrf_token={_csrfToken}";
                }

                // 添加时间戳参数，避免缓存
                string sep2 = url.Contains("?") ? "&" : "?";
                long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                url = $"{url}{sep2}t={timestamp}";

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

                // 发送请求
                var response = await _httpClient.PostAsync(url, content, cancellationToken).ConfigureAwait(false);

                // 读取响应（二进制 -> 自动探测编码解码）
                byte[] rawBytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                string responseText = DecodeResponseContent(response, rawBytes);

                // 调试：输出请求和响应信息
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

                // 如果响应不是JSON，保存到文件以便检查
                if (!responseText.TrimStart().StartsWith("{") && !responseText.TrimStart().StartsWith("["))
                {
                    try
                    {
                        string debugFile = System.IO.Path.Combine(
                            System.IO.Path.GetTempPath(),
                            $"netease_debug_response_{DateTime.Now:yyyyMMdd_HHmmss}.html"
                        );
                        System.IO.File.WriteAllText(debugFile, $"URL: {url}\n\nStatus: {response.StatusCode}\n\n{responseText}");
                        System.Diagnostics.Debug.WriteLine($"[DEBUG WEAPI] !!!响应不是JSON!!! 已保存到: {debugFile}");
                    }
                    catch { }

                    // 直接抛出异常，避免尝试解析HTML
                    throw new Exception($"服务器返回非JSON响应（状态码: {response.StatusCode}），可能是网络问题或API限流");
                }

                // 解析响应（添加try-catch避免JSON解析异常）
                JObject json;
                try
                {
                    // ⭐ 修复：清理响应文本，处理可能的多余内容
                    string cleanedResponse = CleanJsonResponse(responseText);
                    json = JObject.Parse(cleanedResponse);
                }
                catch (JsonReaderException ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[DEBUG WEAPI] JSON解析失败: {ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"[DEBUG WEAPI] 响应原文: {responseText}");

                    // 保存错误响应到文件以便调试
                    try
                    {
                        string debugFile = System.IO.Path.Combine(
                            System.IO.Path.GetTempPath(),
                            $"netease_json_error_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
                        );
                        System.IO.File.WriteAllText(debugFile, $"URL: {url}\n\nError: {ex.Message}\n\nResponse:\n{responseText}");
                        System.Diagnostics.Debug.WriteLine($"[DEBUG WEAPI] 错误响应已保存到: {debugFile}");
                    }
                    catch { }

                    throw new Exception($"JSON解析失败: {ex.Message}，响应内容可能已损坏");
                }

                int code = json["code"]?.Value<int>() ?? -1;
                string message = json["message"]?.Value<string>() ?? json["msg"]?.Value<string>() ?? "Unknown error";

                // ⭐ 修复：对于二维码登录，跳过错误处理（800-803 都是正常状态码）
                if (!skipErrorHandling)
                {
                    // 处理错误
                    HandleApiError(code, message);
                }

                // 返回结果
                return json.ToObject<T>();
            }
            catch (Exception ex) when (retryCount < MAX_RETRY_COUNT && !(ex is UnauthorizedAccessException))
            {
                if (ex is OperationCanceledException)
                {
                    throw;
                }
                // ⭐ 使用自适应延迟策略（参考 netease-music-simple-player）
                int delayMs = GetAdaptiveRetryDelay(retryCount + 1);
                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                return await PostWeApiAsync<T>(path, payload, retryCount + 1, skipErrorHandling, cancellationToken, baseUrl, autoConvertApiSegment);
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
            const string IOS_USER_AGENT = "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148 CloudMusic/0.1.1 NeteaseMusic/9.0.65";

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
                        string sep = url.Contains("?") ? "&" : "?";
                        url = $"{url}{sep}csrf_token={_csrfToken}";
                    }
                    // 添加时间戳
                    string sep2 = url.Contains("?") ? "&" : "?";
                    long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    url = $"{url}{sep2}t={timestamp}";

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
                        request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");

                        // ⭐⭐⭐ 双模式Cookie策略：
                        // 1. 验证码发送（sendCookies=true）：需要访客Cookie（MUSIC_A、NMTID等）
                        //    - 服务器需要验证这是一个有效的访客会话
                        //    - 发送桌面环境生成的访客Cookie，但过滤掉os/osver等
                        // 2. 登录请求（sendCookies=false）：完全零Cookie
                        //    - 模拟真实iPhone首次登录场景
                        //    - 避免桌面Cookie与iOS UA的设备指纹不匹配
                        string cookieHeader = "";

                        if (sendCookies)
                        {
                            // 模式1: 发送访客Cookie（用于验证码发送）
                            var cookies = _cookieContainer.GetCookies(MUSIC_URI);
                            var cookieBuilder = new StringBuilder();
                            foreach (Cookie cookie in cookies)
                            {
                                // 过滤桌面相关Cookie，避免与iOS User-Agent冲突
                                if (cookie.Name == "os" ||
                                    cookie.Name == "osver" ||
                                    cookie.Name == "channel" ||
                                    cookie.Name == "appver" ||
                                    cookie.Name == "buildver")
                                {
                                    continue;
                                }
                                if (cookieBuilder.Length > 0)
                                {
                                    cookieBuilder.Append("; ");
                                }
                                cookieBuilder.Append($"{cookie.Name}={cookie.Value}");
                            }
                            cookieHeader = cookieBuilder.ToString();

                            if (!string.IsNullOrEmpty(cookieHeader))
                            {
                                request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
                            }
                        }
                        // 模式2: sendCookies=false时，完全不发送任何Cookie（用于登录）

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
                            // ⭐ 处理 Set-Cookie 响应头，更新 __csrf token
                            if (response.Headers.Contains("Set-Cookie"))
                            {
                                try
                                {
                                    foreach (var setCookie in response.Headers.GetValues("Set-Cookie"))
                                    {
                                        if (setCookie.Contains("__csrf="))
                                        {
                                            var match = Regex.Match(setCookie, @"__csrf=([^;]+)");
                                            if (match.Success)
                                            {
                                                string csrfValue = match.Groups[1].Value;
                                                UpsertCookie("__csrf", csrfValue);
                                                _csrfToken = csrfValue;
                                                System.Diagnostics.Debug.WriteLine($"[iOS WEAPI] Updated CSRF token: {csrfValue}");
                                            }
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
                        string sep = url.Contains("?") ? "&" : "?";
                        url = $"{url}{sep}csrf_token={_csrfToken}";
                    }
                    // 添加时间戳
                    string sep2 = url.Contains("?") ? "&" : "?";
                    long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    url = $"{url}{sep2}t={timestamp}";

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
        public async Task<T> PostEApiAsync<T>(string path, object payload, bool useIosHeaders = true, int retryCount = 0, bool skipErrorHandling = false)
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

                    var response = await _eapiClient.SendAsync(request);

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
                        catch { }
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
                                throw new Exception("EAPI 解密失败", decryptEx);
                            }
                        }
                        else
                        {
                            SaveEapiDebugArtifact(path, rawBytes, null, decryptEx);
                            throw new Exception("EAPI 解密失败", decryptEx);
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
                        throw new Exception($"EAPI JSON解析失败: {ex.Message}");
                    }

                    int code = json["code"]?.Value<int>() ?? -1;
                    string message = json["message"]?.Value<string>() ?? json["msg"]?.Value<string>() ?? "Unknown error";

                    if (!skipErrorHandling)
                    {
                        HandleApiError(code, message);
                    }

                    return json.ToObject<T>();
                }
            }
            catch (Exception ex) when (retryCount < MAX_RETRY_COUNT && !(ex is UnauthorizedAccessException))
            {
                if (ex is OperationCanceledException)
                {
                    throw;
                }
                // ⭐ 使用自适应延迟策略（参考 netease-music-simple-player）
                int delayMs = GetAdaptiveRetryDelay(retryCount + 1);
                await Task.Delay(delayMs).ConfigureAwait(false);
                return await PostEApiAsync<T>(path, payload, useIosHeaders, retryCount + 1, skipErrorHandling);
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

            // Brotli 解压在 .NET Framework 4.8 中需要额外依赖，运行时不一定提供。
            // 为避免缺少类型导致编译失败，这里通过反射探测并在可用时才启用。
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

            if (!string.IsNullOrEmpty(_musicU))
            {
                cookieMap["MUSIC_U"] = _musicU;
            }
            else if (_authContext?.CurrentAccountState?.MusicA != null)
            {
                cookieMap["MUSIC_A"] = _authContext.CurrentAccountState.MusicA;
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

        /// <summary>
        /// 简化API GET 请求（降级策略）
        /// </summary>
        private async Task<T> GetSimplifiedApiAsync<T>(string endpoint, Dictionary<string, string>? parameters = null)
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
                var response = await _simplifiedClient.GetAsync(url);
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

        #region 登录相关

        /// <summary>
        /// 从文件（支持 .saz 抓包或纯文本）加载 X-antiCheatToken 并写入当前上下文。
        /// </summary>
        public bool LoadAntiCheatTokenFromFile(string path, TimeSpan? ttl = null)
        {
            if (string.IsNullOrWhiteSpace(path) || _authContext == null)
            {
                return false;
            }

            var token = _authContext.LoadAntiCheatTokenFromFile(path, ttl ?? AuthConstants.AntiCheatTokenLifetime);
            return !string.IsNullOrEmpty(token);
        }

        /// <summary>
        /// 手工注入 X-antiCheatToken。
        /// </summary>
        public void InjectAntiCheatToken(string token, TimeSpan? ttl = null)
        {
            if (_authContext == null)
            {
                return;
            }

            _authContext.ProvideAntiCheatToken(token, ttl ?? AuthConstants.AntiCheatTokenLifetime);
        }

        /// <summary>
        /// 创建二维码登录会话。
        /// </summary>
        public async Task<QrLoginSession> CreateQrLoginSessionAsync()
        {
            var payload = new Dictionary<string, object>
            {
                { "type", 1 },
                { "noWarning", true }
            };

            var antiCheatToken = _authContext?.GetActiveAntiCheatToken();
            if (!string.IsNullOrEmpty(antiCheatToken))
            {
                payload["antiCheatToken"] = antiCheatToken;
            }

            System.Diagnostics.Debug.WriteLine("[QR LOGIN] 请求新的二维码登录会话 (type=1)");
            // ⭐ 核心修复：使用标准 _httpClient，自动发送CookieContainer中的所有Cookie
            var result = await PostWeApiWithoutCookiesAsync<JObject>("/login/qrcode/unikey", payload);

            int code = result["code"]?.Value<int>() ?? -1;
            if (code != 200)
            {
                string message = result["message"]?.Value<string>() ?? "Unknown error";
                throw new Exception($"获取二维码Key失败: code={code}, message={message}");
            }

            string unikey = result["unikey"]?.Value<string>();
            if (string.IsNullOrEmpty(unikey))
            {
                throw new Exception("二维码登录接口返回的响应中缺少 unikey 字段");
            }

            var session = new QrLoginSession
            {
                Key = unikey,
                Url = $"https://music.163.com/login?codekey={unikey}",
                CreatedAt = DateTimeOffset.UtcNow,
                ExpireInSeconds = result["endTime"]?.Value<int?>()
            };

            System.Diagnostics.Debug.WriteLine($"[QR LOGIN] 二维码会话创建成功, key={session.Key}");
            return session;
        }

        /// <summary>
        /// 轮询二维码登录状态。
        /// </summary>
        public async Task<QrLoginPollResult> PollQrLoginAsync(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentNullException(nameof(key));
            }

            var payload = new Dictionary<string, object>
            {
                { "key", key },
                { "type", 1 }
            };

            var antiCheatToken = _authContext?.GetActiveAntiCheatToken();
            if (!string.IsNullOrEmpty(antiCheatToken))
            {
                payload["antiCheatToken"] = antiCheatToken;
            }

            System.Diagnostics.Debug.WriteLine($"[QR LOGIN] 轮询二维码状态 (WEAPI type=1), key={key}");

            JObject result;
            try
            {
                // ⭐ 核心修复：使用标准 _httpClient，自动发送CookieContainer中的所有Cookie
                result = await PostWeApiWithoutCookiesAsync<JObject>("/login/qrcode/client/login", payload);
                System.Diagnostics.Debug.WriteLine($"[QR LOGIN] 状态检查响应: {result.ToString(Formatting.Indented)}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[QR LOGIN] 轮询异常: {ex.Message}");
                return new QrLoginPollResult
                {
                    State = QrLoginState.Error,
                    Message = ex.Message,
                    RawCode = -2
                };
            }

            int statusCode = result["code"]?.Value<int>() ?? -1;
            string message = result["message"]?.Value<string>() ?? result["msg"]?.Value<string>() ?? string.Empty;
            string redirectUrl = result["redirectUrl"]?.Value<string>();

            var dataToken = result["data"];
            if (dataToken != null)
            {
                var nestedCodeToken = dataToken["code"] ?? dataToken["qrCodeStatus"] ?? dataToken["status"];
                if (nestedCodeToken != null)
                {
                    int nestedCode = nestedCodeToken.Value<int>();
                    System.Diagnostics.Debug.WriteLine($"[QR LOGIN] 检测到 data.code={nestedCode}");
                    statusCode = nestedCode;
                }

                if (string.IsNullOrEmpty(message))
                {
                    message = dataToken["message"]?.Value<string>() ?? dataToken["msg"]?.Value<string>() ?? message;
                }

                if (string.IsNullOrEmpty(redirectUrl))
                {
                    redirectUrl = dataToken["redirectUrl"]?.Value<string>();
                }
            }

            if (statusCode < 0)
            {
                int qrCodeStatus = result["qrCodeStatus"]?.Value<int>() ?? -1;
                if (qrCodeStatus >= 0)
                {
                    statusCode = qrCodeStatus;
                }
            }

            string cookieString = result["cookie"]?.Value<string>() ?? dataToken?["cookie"]?.Value<string>();
            if (string.IsNullOrEmpty(cookieString))
            {
                var cookieArray = result["cookies"] as JArray ?? dataToken?["cookies"] as JArray;
                if (cookieArray != null && cookieArray.Count > 0)
                {
                    cookieString = string.Join("; ", cookieArray
                        .Select(token => token?.Value<string>())
                        .Where(value => !string.IsNullOrEmpty(value)));
                }
            }

            if ((statusCode == 200 || statusCode == -1) && !string.IsNullOrEmpty(cookieString))
            {
                statusCode = 803;
            }

            var pollResult = new QrLoginPollResult
            {
                RawCode = statusCode,
                RedirectUrl = redirectUrl
            };

            switch (statusCode)
            {
                case 800:
                    pollResult.State = QrLoginState.Expired;
                    pollResult.Message = "二维码已过期，请刷新后重新扫码";
                    break;
                case 801:
                    pollResult.State = QrLoginState.WaitingForScan;
                    pollResult.Message = "等待扫码";
                    break;
                case 802:
                    pollResult.State = QrLoginState.AwaitingConfirmation;
                    pollResult.Message = "已扫码，请在手机上确认登录";
                    break;
                case 803:
                    pollResult.State = QrLoginState.Authorized;
                    pollResult.Message = "登录成功";
                    if (!string.IsNullOrEmpty(cookieString))
                    {
                        pollResult.Cookie = FinalizeLoginCookies(cookieString);
                    }
                    break;
                case 8605:
                case 8606:
                case 8620:
                case 8621:
                case 8800:
                case 8806:
                case 8815:
                case 8820:
                case 8821:
                    pollResult.State = QrLoginState.RiskControl;
                    pollResult.Message = string.IsNullOrEmpty(message)
                        ? "网易云检测到异常登录环境，请在官方客户端完成安全验证或稍后再试"
                        : message;
                    break;
                default:
                    pollResult.State = QrLoginState.Error;
                    pollResult.Message = string.IsNullOrEmpty(message)
                        ? $"二维码登录失败，服务器返回状态码 {statusCode}"
                        : message;
                    break;
            }

            if (pollResult.State == QrLoginState.Authorized)
            {
                System.Diagnostics.Debug.WriteLine("[QR LOGIN] 登录成功，Cookie 已刷新");
            }
            else if (!string.IsNullOrEmpty(pollResult.Cookie) && pollResult.State != QrLoginState.Authorized)
            {
                // 如果服务器提前返回了Cookie，但状态不等于成功，避免污染现有状态
                pollResult.Cookie = null;
            }

            return pollResult;
        }

        /// <summary>
        /// 刷新登录状态（对应 Node login_refresh）。
        /// </summary>
        public async Task<bool> RefreshLoginAsync()
        {
            try
            {
                var payload = new Dictionary<string, object>();
                var result = await PostWeApiAsync<JObject>("/login/token/refresh", payload);
                int code = result["code"]?.Value<int>() ?? -1;
                System.Diagnostics.Debug.WriteLine($"[Auth] RefreshLoginAsync code={code}");
                return code == 200;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Auth] RefreshLoginAsync 失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 获取当前登录状态信息。
        /// </summary>
        public async Task<LoginStatusResult> GetLoginStatusAsync()
        {
            var payload = new Dictionary<string, object>();
            try
            {
                var result = await PostWeApiAsync<JObject>("/w/nuser/account/get", payload, retryCount: 0, skipErrorHandling: true);
                int code = result["code"]?.Value<int>() ?? result["data"]?["code"]?.Value<int>() ?? -1;
                var responseData = result["data"] ?? result;
                var status = new LoginStatusResult
                {
                    RawJson = result.ToString(Formatting.None),
                    IsLoggedIn = code == 200
                };

                if (status.IsLoggedIn)
                {
                    var profile = responseData["profile"];
                    var account = responseData["account"];

                    if (profile != null)
                    {
                        status.Nickname = profile["nickname"]?.Value<string>();
                        status.AccountId = profile["userId"]?.Value<long?>();
                        status.AvatarUrl = profile["avatarUrl"]?.Value<string>();
                        status.VipType = profile["vipType"]?.Value<int>() ?? 0;
                    }
                    else if (account != null)
                    {
                        status.AccountId = account["id"]?.Value<long?>();
                        status.VipType = account["vipType"]?.Value<int>() ?? 0;
                    }

                    try
                    {
                        status.AccountDetail = await GetUserAccountAsync();
                        if (status.AccountDetail != null)
                        {
                            status.Nickname = status.AccountDetail.Nickname ?? status.Nickname;
                            status.AvatarUrl = status.AccountDetail.AvatarUrl ?? status.AvatarUrl;
                            status.VipType = status.AccountDetail.VipType != 0 ? status.AccountDetail.VipType : status.VipType;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Auth] GetUserAccountAsync 在登录状态刷新时失败: {ex.Message}");
                    }
                }

                return status;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Auth] GetLoginStatusAsync 失败: {ex.Message}");
                return new LoginStatusResult
                {
                    IsLoggedIn = false,
                    RawJson = $"Error: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// 退出登录
        /// ⭐⭐⭐ 完全清理当前账户的所有数据，确保下次登录时使用全新状态
        /// </summary>
        public async Task LogoutAsync()
        {
            System.Diagnostics.Debug.WriteLine("[Logout] 开始退出登录...");

            try
            {
                // 1. 调用服务器退出接口
                await PostWeApiAsync<JObject>("/logout", new Dictionary<string, object>());
                System.Diagnostics.Debug.WriteLine("[Logout] ✅ 服务器退出成功");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Logout] ⚠️ 服务器退出失败（继续清理）: {ex.Message}");
            }
            finally
            {
                // 2. 清理本地所有数据
                ClearCookies();

                // 3. 清理账户状态
                _authContext?.ClearLoginProfile();

                System.Diagnostics.Debug.WriteLine("[Logout] ✅✅✅ 退出登录完成，所有数据已清理");
            }
        }

        /// <summary>
        /// 发送短信验证码（手机号登录）
        /// </summary>
        public async Task<bool> SendCaptchaAsync(string phone, string ctcode = "86")
        {
            var payload = new Dictionary<string, object>
            {
                { "cellphone", phone },
                { "ctcode", ctcode }
            };

            System.Diagnostics.Debug.WriteLine($"[SMS] 发送验证码请求: phone={phone}, ctcode={ctcode}");

            // ⭐ 核心修复：验证码发送需要访客Cookie
            // 使用 iOS User-Agent + 访客Cookie（过滤桌面Cookie）
            // 访客Cookie（MUSIC_A、NMTID等）是必需的，否则触发-462风控
            var result = await PostWeApiWithiOSAsync<JObject>("/sms/captcha/sent", payload, maxRetries: 3, sendCookies: true);

            int code = result["code"]?.Value<int>() ?? -1;
            string message = result["message"]?.Value<string>() ?? result["msg"]?.Value<string>() ?? "未知错误";

            System.Diagnostics.Debug.WriteLine($"[SMS] 发送验证码结果: code={code}, msg={message}");
            System.Diagnostics.Debug.WriteLine($"[SMS] 完整响应: {result.ToString(Newtonsoft.Json.Formatting.Indented)}");

            if (code != 200)
            {
                throw new Exception($"发送验证码失败: {message} (code={code})");
            }

            return true;
        }

        /// <summary>
        /// 验证短信验证码并登录
        /// ⭐ 参考 netease-music-simple-player/Net/NetClasses.cs:2521-2541
        /// 关键: 使用 iOS User-Agent 避免 -462 风控错误
        /// </summary>
        public async Task<LoginResult> LoginByCaptchaAsync(string phone, string captcha, string ctcode = "86")
        {
            // ⭐ 重要：不再在 Cookie 中设置 os=ios 和 appver=8.7.01
            // 因为 PostWeApiWithiOSAsync 已经使用 iOS User-Agent
            // 在 Cookie 中设置 os=ios 会与桌面系统的其他 Cookie 冲突，触发风控

            // ⭐ 核心修复：完全模拟参考项目的payload，只发送3个字段
            // 参考项目 netease-music-simple-player/Net/NetClasses.cs:2525-2530
            // 任何额外字段（如 rememberLogin）都可能触发风控
            var payload = new Dictionary<string, object>
            {
                { "phone", phone },
                { "countrycode", ctcode },
                { "captcha", captcha }
            };

            System.Diagnostics.Debug.WriteLine($"[LOGIN] 短信登录请求: phone={phone}, captcha={captcha}, countrycode={ctcode}");
            System.Diagnostics.Debug.WriteLine("[LOGIN] 使用 iOS User-Agent + 零Cookie模式 + 精简payload（仅3字段）");

            // ⭐ 核心修复：登录请求使用零Cookie模式（sendCookies默认为false）
            // 模拟真实iPhone首次登录场景，避免桌面Cookie与iOS UA的设备指纹不匹配
            var result = await PostWeApiWithiOSAsync<JObject>("/login/cellphone", payload, maxRetries: 3);

            System.Diagnostics.Debug.WriteLine($"[LOGIN] 短信登录完整响应: {result.ToString(Formatting.Indented)}");
            int code = result["code"]?.Value<int>() ?? -1;

            var loginResult = new LoginResult
            {
                Code = code,
                Message = result["message"]?.Value<string>() ?? result["msg"]?.Value<string>() ?? ""
            };

            if (code == 200)
            {
                System.Diagnostics.Debug.WriteLine("[LOGIN] 短信登录成功，提取Cookie...");

                string cookieString = result["cookie"]?.Value<string>();
                if (!string.IsNullOrEmpty(cookieString))
                {
                    System.Diagnostics.Debug.WriteLine($"[LOGIN] 收到Cookie: {cookieString.Substring(0, Math.Min(100, cookieString.Length))}...");
                }

                cookieString = FinalizeLoginCookies(cookieString);
                loginResult.Cookie = cookieString;

                if (!string.IsNullOrEmpty(_musicU))
                {
                    System.Diagnostics.Debug.WriteLine($"[LOGIN] 已缓存MUSIC_U，长度={_musicU.Length}");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[LOGIN] ⚠️ 登录成功但未能捕获MUSIC_U");
                }

                if (string.IsNullOrEmpty(cookieString))
                {
                    System.Diagnostics.Debug.WriteLine("[LOGIN] ⚠️ Cookie 快照为空，后续请求可能无法使用登录态");
                }

                // 提取用户信息
                var account = result["account"];
                if (account != null)
                {
                    loginResult.UserId = account["id"]?.Value<string>();
                    loginResult.Nickname = account["userName"]?.Value<string>();
                    loginResult.VipType = account["vipType"]?.Value<int>() ?? 0;
                    System.Diagnostics.Debug.WriteLine($"[LOGIN] 用户信息: ID={loginResult.UserId}, 昵称={loginResult.Nickname}, VipType={loginResult.VipType}");
                }

                var profile = result["profile"];
                if (profile != null)
                {
                    if (string.IsNullOrEmpty(loginResult.Nickname))
                    {
                        var profileNickname = profile["nickname"]?.Value<string>();
                        if (!string.IsNullOrEmpty(profileNickname))
                        {
                            loginResult.Nickname = profileNickname;
                        }
                    }

                    loginResult.AvatarUrl = profile["avatarUrl"]?.Value<string>();
                    if (loginResult.VipType == 0)
                    {
                        loginResult.VipType = profile["vipType"]?.Value<int>() ?? 0;
                    }
                }
            }

            return loginResult;
        }

        /// <summary>
        /// 完成登录后的初始化工作
        /// ⭐⭐⭐ 在登录成功后调用，确保Cookie完全同步并进行会话预热
        /// </summary>
        /// <param name="loginResult">登录结果</param>
        public async Task CompleteLoginAsync(LoginResult loginResult)
        {
            if (loginResult == null || loginResult.Code != 200)
            {
                System.Diagnostics.Debug.WriteLine("[CompleteLogin] ⚠️ 登录未成功，跳过初始化");
                return;
            }

            System.Diagnostics.Debug.WriteLine("[CompleteLogin] 开始登录后初始化...");

            try
            {
                // 1. 确保Cookie已完全更新（通常已在 FinalizeLoginCookies 中完成）
                UpdateCookies();
                System.Diagnostics.Debug.WriteLine("[CompleteLogin] ✅ Cookie已同步");

                // 2. 会话预热 - 向服务器发送当前账户数据，避免后续风控
                // 注意：账户信息保存由 LoginForm 调用 ApplyLoginProfile 完成，这里只做预热
                System.Diagnostics.Debug.WriteLine("[CompleteLogin] 开始会话预热...");
                await WarmupSessionAsync();
                System.Diagnostics.Debug.WriteLine("[CompleteLogin] ✅ 会话预热完成");

                System.Diagnostics.Debug.WriteLine("[CompleteLogin] ✅✅✅ 登录初始化全部完成！");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CompleteLogin] ⚠️ 初始化过程出现异常（不影响登录）: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取用户信息
        /// </summary>
        public async Task<UserInfo> GetUserInfoAsync()
        {
            try
            {
                var result = await PostWeApiAsync<JObject>("/nuser/account/get", new Dictionary<string, object>());
                var account = result["account"];

                if (account != null)
                {
                    return new UserInfo
                    {
                        UserId = account["id"]?.Value<string>(),
                        Nickname = account["userName"]?.Value<string>(),
                        VipType = account["vipType"]?.Value<int>() ?? 0,
                        AvatarUrl = account["avatarUrl"]?.Value<string>()
                    };
                }
            }
            catch { }

            return null;
        }

        #endregion

        #region 搜索相关

        /// <summary>
        /// 搜索歌曲（使用 NodeJS 云音乐 API 同步的 EAPI 接口）。
        /// </summary>
        public async Task<SearchResult<SongInfo>> SearchSongsAsync(string keyword, int limit = 30, int offset = 0)
        {
            System.Diagnostics.Debug.WriteLine($"[API] 搜索歌曲: {keyword}, limit={limit}, offset={offset}");

            try
            {
                var result = await ExecuteSearchRequestAsync(keyword, SearchResourceType.Song, limit, offset);
                var songs = ParseSongList(result?["songs"] as JArray);
                int totalCount = ResolveTotalCount(result, SearchResourceType.Song, offset, songs.Count);

                System.Diagnostics.Debug.WriteLine($"[API] 搜索成功，返回 {songs.Count} 首歌曲, total={totalCount}");
                return new SearchResult<SongInfo>(songs, totalCount, offset, limit, result);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 搜索失败: {ex.GetType().Name}: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[API] 堆栈: {ex.StackTrace}");
                throw;
            }
        }

        /// <summary>
        /// 搜索歌单（使用 NodeJS 云音乐 API 同步的 EAPI 接口）。
        /// </summary>
        public async Task<SearchResult<PlaylistInfo>> SearchPlaylistsAsync(string keyword, int limit = 30, int offset = 0)
        {
            System.Diagnostics.Debug.WriteLine($"[API] 搜索歌单: {keyword}, limit={limit}, offset={offset}");

            var result = await ExecuteSearchRequestAsync(keyword, SearchResourceType.Playlist, limit, offset);
            var playlists = ParsePlaylistList(result?["playlists"] as JArray);
            int totalCount = ResolveTotalCount(result, SearchResourceType.Playlist, offset, playlists.Count);

            return new SearchResult<PlaylistInfo>(playlists, totalCount, offset, limit, result);
        }

        /// <summary>
        /// 搜索专辑（使用 NodeJS 云音乐 API 同步的 EAPI 接口）。
        /// </summary>
        public async Task<SearchResult<AlbumInfo>> SearchAlbumsAsync(string keyword, int limit = 30, int offset = 0)
        {
            System.Diagnostics.Debug.WriteLine($"[API] 搜索专辑: {keyword}, limit={limit}, offset={offset}");

            var result = await ExecuteSearchRequestAsync(keyword, SearchResourceType.Album, limit, offset);
            var albums = ParseAlbumList(result?["albums"] as JArray);
            int totalCount = ResolveTotalCount(result, SearchResourceType.Album, offset, albums.Count);

            return new SearchResult<AlbumInfo>(albums, totalCount, offset, limit, result);
        }

        /// <summary>
        /// 搜索歌手。
        /// </summary>
        public async Task<SearchResult<ArtistInfo>> SearchArtistsAsync(string keyword, int limit = 30, int offset = 0)
        {
            System.Diagnostics.Debug.WriteLine($"[API] 搜索歌手: {keyword}, limit={limit}, offset={offset}");

            var result = await ExecuteSearchRequestAsync(keyword, SearchResourceType.Artist, limit, offset);
            var artists = ParseArtistList(result?["artists"] as JArray);
            int totalCount = ResolveTotalCount(result, SearchResourceType.Artist, offset, artists.Count);

            return new SearchResult<ArtistInfo>(artists, totalCount, offset, limit, result);
        }

        /// <summary>
        /// 调用搜索接口，自动处理简化API与官方API切换。
        /// </summary>
        private async Task<JObject> ExecuteSearchRequestAsync(string keyword, SearchResourceType resourceType, int limit, int offset)
        {
            if (string.IsNullOrWhiteSpace(keyword))
            {
                return new JObject();
            }

            string typeCode = ((int)resourceType).ToString();

            if (UseSimplifiedApi)
            {
                try
                {
                    var simplifiedParameters = new Dictionary<string, string>
                    {
                        { "keywords", keyword },
                        { "type", typeCode },
                        { "limit", limit.ToString() },
                        { "offset", offset.ToString() }
                    };

                    System.Diagnostics.Debug.WriteLine($"[API] 通过简化接口搜索: type={typeCode}, keyword={keyword}");
                    var simplifiedResponse = await GetSimplifiedApiAsync<JObject>("/search", simplifiedParameters);
                    if (simplifiedResponse?["result"] is JObject simplifiedResult)
                    {
                        return simplifiedResult;
                    }

                    System.Diagnostics.Debug.WriteLine("[API] 简化接口结果为空或类型错误，切换到官方接口");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[API] 简化接口搜索失败，将使用官方接口: {ex.Message}");
                }
            }

            var payload = new Dictionary<string, object>
            {
                { "s", keyword },
                { "type", (int)resourceType },
                { "limit", limit },
                { "offset", offset },
                { "total", true }
            };

            // 优先使用 WEAPI 官方接口（与移动端一致），失败时再回退到 EAPI
            try
            {
                // 对标官方 Node 实现（module/search.js）：使用 weapi/search/get
                var weapiPayload = new Dictionary<string, object>
                {
                    { "s", keyword },
                    { "type", (int)resourceType },
                    { "limit", limit },
                    { "offset", offset },
                    { "total", true }
                };

                // 这两个字段会让搜索结果包含高亮标记，与官方客户端行为一致
                weapiPayload["hlpretag"] = "<span class=\"s-fc7\">";
                weapiPayload["hlposttag"] = "</span>";

                var weapiResponse = await PostWeApiAsync<JObject>("/search/get", weapiPayload);
                if (weapiResponse?["result"] is JObject weapiResult)
                {
                    return weapiResult;
                }

                System.Diagnostics.Debug.WriteLine("[API] WEAPI 搜索响应缺少 result 节点，尝试 EAPI 回退。");
            }
            catch (Exception weapiEx)
            {
                System.Diagnostics.Debug.WriteLine($"[API] WEAPI 搜索失败，尝试 EAPI 回退: {weapiEx.Message}");
            }

            var response = await PostEApiAsync<JObject>("/api/cloudsearch/pc", payload);
            if (response?["result"] is JObject result)
            {
                return result;
            }

            System.Diagnostics.Debug.WriteLine("[API] 官方搜索响应缺少 result 节点，返回空对象");
            return new JObject();
        }

        /// <summary>
        /// 提取搜索结果的总数量，若接口未返回则根据当前页估算。
        /// </summary>
        private static int ResolveTotalCount(JObject result, SearchResourceType resourceType, int offset, int itemsCount)
        {
            if (result == null)
            {
                return offset + itemsCount;
            }

            foreach (var propertyName in GetCountPropertyCandidates(resourceType))
            {
                var token = result[propertyName];
                if (token != null && token.Type == JTokenType.Integer)
                {
                    long reportedLong = token.Value<long>();
                    if (reportedLong >= 0)
                    {
                        int reported = reportedLong > int.MaxValue ? int.MaxValue : (int)reportedLong;
                        return Math.Max(reported, offset + itemsCount);
                    }
                }
            }

            return offset + itemsCount;
        }

        private static IEnumerable<string> GetCountPropertyCandidates(SearchResourceType resourceType)
        {
            switch (resourceType)
            {
                case SearchResourceType.Song:
                    yield return "songCount";
                    yield return "songsCount";
                    break;
                case SearchResourceType.Album:
                    yield return "albumCount";
                    break;
                case SearchResourceType.Playlist:
                    yield return "playlistCount";
                    break;
                case SearchResourceType.Artist:
                    yield return "artistCount";
                    break;
                case SearchResourceType.MV:
                    yield return "mvCount";
                    break;
                case SearchResourceType.Video:
                    yield return "videoCount";
                    break;
                case SearchResourceType.Radio:
                    yield return "djRadiosCount";
                    yield return "djRadioCount";
                    break;
                case SearchResourceType.Lyric:
                    yield return "lyricCount";
                    yield return "songCount";
                    break;
                case SearchResourceType.User:
                    yield return "userprofileCount";
                    yield return "userProfilesCount";
                    break;
            }

            yield return "totalCount";
            yield return "total";
            yield return "count";
        }

        #endregion

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

            // ⭐ 如果已通过批量预检，跳过可用性检查以加快播放速度
            if (!skipAvailabilityCheck)
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

                    if (missingSongIds.Count > 0)
                    {
                        throw new SongResourceNotFoundException("请求的歌曲资源在官方曲库中不存在或已下架。", missingSongIds);
                    }
                }
                catch (SongResourceNotFoundException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[SongUrl] 资源存在性预检失败: {ex.Message}");
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[SongUrl] 跳过可用性检查（已通过批量预检）");
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
                    var simplifiedResult = await GetSongUrlViaSimplifiedApiAsync(ids, requestedLevel);
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

                    var response = await PostEApiAsync<JObject>("/api/song/enhance/player/url/v1", payload, useIosHeaders: true, skipErrorHandling: true);
                    if (response == null)
                    {
                        System.Diagnostics.Debug.WriteLine("[EAPI] 响应为空，尝试下一个音质");
                        continue;
                    }

                    int code = response["code"]?.Value<int>() ?? -1;
                    string message = response["message"]?.Value<string>() ?? response["msg"]?.Value<string>() ?? "unknown";
                    if (code == 404 || (!string.IsNullOrEmpty(message) && message.Contains("不存在")))
                    {
                        System.Diagnostics.Debug.WriteLine($"[EAPI] 官方接口返回资源不存在 (code={code}, message={message})，停止降级。");
                        foreach (var missingId in ids)
                        {
                            if (!string.IsNullOrEmpty(missingId))
                            {
                                missingSongIds.Add(missingId);
                            }
                        }
                        break;
                    }

                    if (code != 200)
                    {
                        System.Diagnostics.Debug.WriteLine($"[EAPI] code={code}, message={message}，尝试下一个音质");
                        continue;
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

                        if (itemMissing)
                        {
                            System.Diagnostics.Debug.WriteLine($"[EAPI] 歌曲{id} 官方不存在 (itemCode={itemCode}, message={itemMessage})。");
                            missingSongIds.Add(id);
                            continue;
                        }

                        string url = item["url"]?.Value<string>();
                        if (string.IsNullOrEmpty(url))
                        {
                            System.Diagnostics.Debug.WriteLine($"[EAPI] 歌曲{id} 在音质 {currentLevel} 下无可用URL，尝试降级");
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

                    if (missingSongIds.Count > 0)
                    {
                        System.Diagnostics.Debug.WriteLine("[EAPI] 检测到官方缺失的歌曲，停止进一步降级。");
                        break;
                    }

                    if (fallbackToLowerQuality)
                    {
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
                    return await GetSongUrlViaSimplifiedApiAsync(ids, requestedLevel);
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
        private async Task<Dictionary<string, SongUrlInfo>> GetSongUrlViaSimplifiedApiAsync(string[] ids, string level)
        {
            var result = new Dictionary<string, SongUrlInfo>();

            // 公共API一次只能查询一首歌曲，所以需要循环调用
            foreach (var songId in ids)
            {
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

                    var response = await _simplifiedClient.PostAsync(apiUrl, content);
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

        #region 歌单相关

        /// <summary>
        /// 获取歌单详情
        /// </summary>
        public async Task<PlaylistInfo> GetPlaylistDetailAsync(string playlistId)
        {
            // 尝试使用简化API
            if (UseSimplifiedApi)
            {
                try
                {
                    var parameters = new Dictionary<string, string>
                    {
                        { "id", playlistId }
                    };
                    var result = await GetSimplifiedApiAsync<JObject>("/playlist/detail", parameters);
                    return ParsePlaylistDetail(result["playlist"] as JObject);
                }
                catch { }
            }

            // 使用加密API
            var payload = new Dictionary<string, object>
            {
                { "id", playlistId },
                { "n", 100000 },
                { "s", 8 }
            };

            var response = await PostWeApiAsync<JObject>("/v6/playlist/detail", payload);
            return ParsePlaylistDetail(response["playlist"] as JObject);
        }

        /// <summary>
        /// 获取歌单内的所有歌曲（参考 Python 版本 _fetch_playlist_via_weapi，11917-11966行）
        /// </summary>
        public async Task<List<SongInfo>> GetPlaylistSongsAsync(string playlistId, CancellationToken cancellationToken = default)
        {
            System.Diagnostics.Debug.WriteLine($"[API] 获取歌单歌曲: {playlistId}");

            try
            {
                // 先获取歌单基本信息（参考 Python 11918行）
                var infoData = new Dictionary<string, object>
                {
                    { "id", playlistId },
                    { "n", 1 },
                    { "s", 8 }
                };

                System.Diagnostics.Debug.WriteLine($"[API] 获取歌单详情...");
                var infoResponse = await PostWeApiAsync<JObject>("/v3/playlist/detail", infoData, cancellationToken: cancellationToken);

                // 检查返回码（参考 Python 11920行）
                int code = infoResponse["code"]?.Value<int>() ?? 0;
                if (code != 200)
                {
                    string msg = infoResponse["message"]?.Value<string>() ?? "未知错误";
                    throw new Exception($"获取歌单详情失败: code={code}, message={msg}");
                }

                var playlist = infoResponse["playlist"];
                if (playlist == null)
                {
                    throw new Exception("返回数据中没有playlist字段");
                }

                string playlistName = playlist["name"]?.Value<string>() ?? $"歌单 {playlistId}";
                int total = playlist["trackCount"]?.Value<int>() ?? 0;
                System.Diagnostics.Debug.WriteLine($"[API] 歌单名称: {playlistName}, 总歌曲数: {total}");

                // 检查私密歌单权限（参考 Python 11928-11930行）
                bool isPrivate = (playlist["privacy"]?.Value<int>() ?? 0) == 10;
                if (isPrivate)
                {
                    System.Diagnostics.Debug.WriteLine($"[API] 检测到私密歌单");
                    // TODO: 检查是否是创建者
                }

                if (total <= 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[API] 歌单为空");
                    return new List<SongInfo>();
                }

                // 直接使用 trackIds 批量获取（参考 Python 11956-11964行）
                // /weapi/playlist/track/all 接口在未登录状态下会被风控，已废弃
                System.Diagnostics.Debug.WriteLine($"[API] 开始通过 trackIds 获取歌曲详情（共 {total} 首）");

                var trackIds = playlist["trackIds"] as JArray;
                if (trackIds == null || trackIds.Count == 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[API ERROR] trackIds 为空");
                    return new List<SongInfo>();
                }

                // 提取所有歌曲ID
                var allIds = new List<string>();
                foreach (var tid in trackIds)
                {
                    string id = tid["id"]?.ToString();
                    if (!string.IsNullOrEmpty(id))
                    {
                        allIds.Add(id);
                    }
                }

                System.Diagnostics.Debug.WriteLine($"[API] 提取到 {allIds.Count} 个歌曲ID，开始批量获取详情");

                // 批量获取歌曲详情
                var allSongs = await GetSongsByIdsAsync(allIds, cancellationToken);

                System.Diagnostics.Debug.WriteLine($"[API] 歌单歌曲获取完成，共 {allSongs.Count}/{total} 首");
                return allSongs;
            }
            catch (OperationCanceledException)
            {
                System.Diagnostics.Debug.WriteLine("[API] 获取歌单歌曲操作被取消");
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API ERROR] 获取歌单歌曲异常: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[API ERROR] 堆栈: {ex.StackTrace}");
                throw;
            }
        }

        /// <summary>
        /// 批量获取歌曲详情（参考 Python 版本 _fetch_songs_by_ids，11967-11977行）
        /// 添加延迟避免触发风控限流，减小批次大小提高成功率
        /// </summary>
        private async Task<List<SongInfo>> GetSongsByIdsAsync(List<string> ids, CancellationToken cancellationToken = default)
        {
            var allSongs = new List<SongInfo>();
            // 减小批次大小到200，降低触发风控概率
            int step = 200;
            int batchNum = 0;

            System.Diagnostics.Debug.WriteLine($"[API] 开始批量获取 {ids.Count} 首歌曲详情，每批 {step} 首");

            for (int i = 0; i < ids.Count; i += step)
            {
                batchNum++;
                var batch = ids.Skip(i).Take(step).ToList();

                // 每批之间延迟 1.5 秒，降低风控风险
                if (i > 0)
                {
                    int delayMs = 1500;
                    System.Diagnostics.Debug.WriteLine($"[API] 等待 {delayMs}ms 避免限流...");
                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                }

                System.Diagnostics.Debug.WriteLine($"[API] 获取第 {batchNum} 批（{i + 1}-{Math.Min(i + step, ids.Count)}）...");

                var cJson = JsonConvert.SerializeObject(batch.Select(x => new { id = long.Parse(x) }), Formatting.None);
                var idsJson = JsonConvert.SerializeObject(batch);

                var data = new Dictionary<string, object>
                {
                    { "c", cJson },
                    { "ids", idsJson }
                };

                int retryCount = 0;
                bool success = false;

                // 添加重试机制（最多重试2次）
                while (retryCount < 3 && !success)
                {
                    try
                    {
                        var response = await PostWeApiAsync<JObject>("/song/detail", data, cancellationToken: cancellationToken);
                        var songs = response["songs"] as JArray;

                        if (songs != null && songs.Count > 0)
                        {
                            var parsed = ParseSongList(songs);
                            allSongs.AddRange(parsed);
                            System.Diagnostics.Debug.WriteLine($"[API] 第 {batchNum} 批成功获取 {parsed.Count} 首");
                            success = true;
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"[API] 第 {batchNum} 批返回空数据");
                            throw new Exception("返回空数据");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        System.Diagnostics.Debug.WriteLine("[API] 批量获取歌曲操作被取消");
                        throw;
                    }
                    catch (Exception ex)
                    {
                        retryCount++;
                        System.Diagnostics.Debug.WriteLine($"[API ERROR] 第 {batchNum} 批获取失败（重试 {retryCount}/3）: {ex.Message}");

                        if (retryCount < 3)
                        {
                            // 重试前等待更长时间
                            int retryDelay = 2000 * retryCount;
                            System.Diagnostics.Debug.WriteLine($"[API] 等待 {retryDelay}ms 后重试...");
                            await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }

                if (!success)
                {
                    System.Diagnostics.Debug.WriteLine($"[API ERROR] 第 {batchNum} 批最终失败，跳过该批次");
                }
            }

            System.Diagnostics.Debug.WriteLine($"[API] 批量获取完成，共获得 {allSongs.Count}/{ids.Count} 首歌曲");
            return allSongs;
        }

        /// <summary>
        /// 获取专辑内的所有歌曲（参考 Python 版本 _fetch_album_detail，14999-15048行）
        /// </summary>
        public async Task<List<SongInfo>> GetAlbumSongsAsync(string albumId)
        {
            System.Diagnostics.Debug.WriteLine($"[API] 获取专辑歌曲: {albumId}");

            // 尝试第一个API
            try
            {
                string url = $"https://music.163.com/api/album/{albumId}";
                var response = await _httpClient.GetAsync(url);
                var jsonString = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var json = JObject.Parse(jsonString);

                var songs = json["songs"] as JArray ?? json["album"]?["songs"] as JArray;
                if (songs != null && songs.Count > 0)
                {
                    return ParseSongList(songs);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 获取专辑歌曲方法1失败: {ex.Message}");
            }

            // 尝试第二个API
            try
            {
                string url = $"https://music.163.com/api/album/detail?id={albumId}";
                var response = await _httpClient.GetAsync(url);
                var jsonString = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var json = JObject.Parse(jsonString);

                var songs = json["songs"] as JArray ?? json["album"]?["songs"] as JArray;
                if (songs != null && songs.Count > 0)
                {
                    return ParseSongList(songs);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 获取专辑歌曲方法2失败: {ex.Message}");
            }

            throw new Exception("无法获取专辑歌曲");
        }

        /// <summary>
        /// 歌单收藏/取消收藏（参考 Python 版本 _playlist_subscribe_weapi，6761-6775行）
        /// </summary>
        /// <param name="playlistId">歌单ID</param>
        /// <param name="subscribe">true=收藏，false=取消收藏</param>
        public async Task<bool> SubscribePlaylistAsync(string playlistId, bool subscribe)
        {
            try
            {
                string action = subscribe ? "subscribe" : "unsubscribe";
                var payload = new Dictionary<string, object>
                {
                    { "id", playlistId },
                    { "t", subscribe ? 1 : 2 }
                };
                var response = await PostWeApiAsync<JObject>($"/playlist/{action}", payload);
                int code = response["code"]?.Value<int>() ?? -1;
                return code == 200;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 歌单{(subscribe ? "收藏" : "取消收藏")}失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 删除歌单（仅限用户本人创建的歌单）
        /// 参考: NeteaseCloudMusicApi/module/playlist_delete.js
        /// </summary>
        public async Task<bool> DeletePlaylistAsync(string playlistId)
        {
            if (string.IsNullOrWhiteSpace(playlistId))
            {
                return false;
            }

            try
            {
                // 删除歌单接口要求 os=pc
                UpsertCookie("os", "pc");

                var payload = new Dictionary<string, object>
                {
                    { "ids", $"[{playlistId}]" }
                };

                var response = await PostWeApiAsync<JObject>("/playlist/remove", payload);
                int code = response["code"]?.Value<int>() ?? -1;
                return code == 200;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 删除歌单失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 专辑收藏（参考 Python 版本 _subscribe_album，10224-10268行）
        /// </summary>
        public async Task<bool> SubscribeAlbumAsync(string albumId)
        {
            try
            {
                var payload = new Dictionary<string, object>
                {
                    { "id", albumId },
                    { "t", "1" }
                };
                var response = await PostWeApiAsync<JObject>("/album/sub", payload);
                int code = response["code"]?.Value<int>() ?? -1;
                return code == 200;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 专辑收藏失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 专辑取消收藏（参考 Python 版本 _unsubscribe_album，10271-10296行）
        /// </summary>
        public async Task<bool> UnsubscribeAlbumAsync(string albumId)
        {
            try
            {
                var payload = new Dictionary<string, object>
                {
                    { "id", albumId }
                };
                var response = await PostWeApiAsync<JObject>("/album/unsub", payload);
                int code = response["code"]?.Value<int>() ?? -1;
                return code == 200;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 专辑取消收藏失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 歌单添加歌曲（参考 Python 版本 _playlist_manipulate_tracks_weapi，14557-14568行）
        /// </summary>
        public async Task<bool> AddTracksToPlaylistAsync(string playlistId, string[] songIds)
        {
            try
            {
                var payload = new Dictionary<string, object>
                {
                    { "op", "add" },
                    { "pid", playlistId },
                    { "trackIds", $"[{string.Join(",", songIds)}]" }
                };
                var response = await PostWeApiAsync<JObject>("/playlist/manipulate/tracks", payload);
                int code = response["code"]?.Value<int>() ?? -1;
                return code == 200;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 添加歌曲到歌单失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 从歌单中移除歌曲
        /// API: POST /api/playlist/manipulate/tracks
        /// 参考: NeteaseCloudMusicApi/module/playlist_tracks.js
        /// </summary>
        /// <param name="playlistId">歌单ID</param>
        /// <param name="songIds">歌曲ID数组</param>
        public async Task<bool> RemoveTracksFromPlaylistAsync(string playlistId, string[] songIds)
        {
            try
            {
                var payload = new Dictionary<string, object>
                {
                    { "op", "del" },
                    { "pid", playlistId },
                    { "trackIds", $"[{string.Join(",", songIds)}]" }
                };
                var response = await PostWeApiAsync<JObject>("/playlist/manipulate/tracks", payload);
                int code = response["code"]?.Value<int>() ?? -1;
                return code == 200;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 从歌单中移除歌曲失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 创建歌单
        /// API: POST /api/playlist/create
        /// 参考: NeteaseCloudMusicApi/module/playlist_create.js
        /// </summary>
        /// <param name="name">歌单名称</param>
        /// <param name="privacy">隐私设置：0=公开，10=隐私</param>
        /// <param name="type">歌单类型：NORMAL(默认) | VIDEO | SHARED</param>
        public async Task<PlaylistInfo?> CreatePlaylistAsync(string name, int privacy = 0, string type = "NORMAL")
        {
            try
            {
                var payload = new Dictionary<string, object>
                {
                    { "name", name },
                    { "privacy", privacy },
                    { "type", type }
                };

                var response = await PostWeApiAsync<JObject>("/playlist/create", payload, autoConvertApiSegment: true);
                int code = response?["code"]?.Value<int>() ?? -1;

                if (code != 200)
                {
                    System.Diagnostics.Debug.WriteLine($"[API] 创建歌单失败: code={code}, message={response?["message"] ?? response?["msg"]}");
                    return null;
                }

                var playlistToken = response?["playlist"] as JObject;
                if (playlistToken != null)
                {
                    var created = CreatePlaylistInfo(playlistToken);
                    if (string.IsNullOrWhiteSpace(created.Name))
                    {
                        created.Name = name;
                    }

                    PopulatePlaylistOwnershipDefaults(created, playlistToken);
                    return created;
                }

                string? playlistId = ExtractPlaylistId(response);
                if (!string.IsNullOrWhiteSpace(playlistId))
                {
                    try
                    {
                        var detailed = await GetPlaylistDetailAsync(playlistId);
                        if (detailed != null)
                        {
                            if (string.IsNullOrWhiteSpace(detailed.Name))
                            {
                                detailed.Name = name;
                            }

                            PopulatePlaylistOwnershipDefaults(detailed, null);
                            return detailed;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[API] 获取新建歌单详情失败: {ex.Message}");
                    }

                    var fallback = new PlaylistInfo
                    {
                        Id = playlistId,
                        Name = string.IsNullOrWhiteSpace(name) ? "新建歌单" : name.Trim(),
                        TrackCount = 0
                    };

                    PopulatePlaylistOwnershipDefaults(fallback, null);
                    return fallback;
                }

                System.Diagnostics.Debug.WriteLine("[API] 创建歌单响应缺少 playlist/id 字段，无法构建返回对象。");
                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 创建歌单失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 喜欢/取消喜欢歌曲（红心）
        /// API: POST /api/radio/like
        /// 参考: NeteaseCloudMusicApi/module/like.js
        /// </summary>
        /// <param name="songId">歌曲ID</param>
        /// <param name="like">true=喜欢，false=取消喜欢</param>
        public async Task<bool> LikeSongAsync(string songId, bool like)
        {
            try
            {
                var payload = new Dictionary<string, object>
                {
                    { "alg", "itembased" },
                    { "trackId", songId },
                    { "like", like },
                    { "time", "3" }
                };

                var response = await PostWeApiAsync<JObject>("/radio/like", payload);
                int code = response["code"]?.Value<int>() ?? -1;

                System.Diagnostics.Debug.WriteLine($"[API] {(like ? "喜欢" : "取消喜欢")}歌曲 {songId}: code={code}");
                return code == 200;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] {(like ? "喜欢" : "取消喜欢")}歌曲失败: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region 歌手相关

        /// <summary>
        /// 获取歌手详情。
        /// </summary>
        public async Task<ArtistDetail?> GetArtistDetailAsync(long artistId, bool includeIntroduction = true)
        {
            if (artistId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(artistId));
            }

            var payload = new Dictionary<string, object>
            {
                { "id", artistId }
            };

            JObject response;
            try
            {
                response = await PostWeApiAsync<JObject>(
                    "/api/artist/head/info/get",
                    payload,
                    autoConvertApiSegment: true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 获取歌手详情失败: {ex.Message}");
                throw;
            }

            if (response == null)
            {
                return null;
            }

            var dataNode = response["data"] as JObject ?? response["artist"] as JObject ?? response;
            var artistNode = dataNode?["artist"] as JObject ?? dataNode?["artistInfo"] as JObject ?? dataNode;

            var baseInfo = ParseArtistObject(artistNode) ?? new ArtistInfo { Id = artistId };

            if (baseInfo.Id <= 0)
            {
                baseInfo.Id = artistId;
            }

            var detail = new ArtistDetail
            {
                Id = baseInfo.Id,
                Name = baseInfo.Name,
                Alias = baseInfo.Alias,
                PicUrl = baseInfo.PicUrl,
                AreaCode = baseInfo.AreaCode,
                AreaName = baseInfo.AreaName,
                TypeCode = baseInfo.TypeCode,
                TypeName = baseInfo.TypeName,
                MusicCount = baseInfo.MusicCount,
                AlbumCount = baseInfo.AlbumCount,
                MvCount = baseInfo.MvCount,
                BriefDesc = baseInfo.BriefDesc,
                Description = baseInfo.Description,
                IsSubscribed = baseInfo.IsSubscribed,
                CoverImageUrl = artistNode?["cover"]?.Value<string>()
                    ?? artistNode?["coverUrl"]?.Value<string>()
                    ?? baseInfo.PicUrl,
                FollowerCount = artistNode?["fansGroup"]?["followCount"]?.Value<long>()
                    ?? artistNode?["fansCount"]?.Value<long>()
                    ?? artistNode?["followedCount"]?.Value<long>()
                    ?? dataNode?["fans"]?.Value<long>()
                    ?? 0
            };

            if (string.IsNullOrWhiteSpace(detail.Name))
            {
                detail.Name = artistNode?["name"]?.Value<string>() ?? dataNode?["name"]?.Value<string>() ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(detail.PicUrl))
            {
                detail.PicUrl = artistNode?["avatar"]?.Value<string>()
                    ?? artistNode?["avatarUrl"]?.Value<string>()
                    ?? detail.CoverImageUrl;
            }

            var identifyDesc = artistNode?["identifyDesc"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(identifyDesc))
            {
                detail.ExtraMetadata["认证信息"] = identifyDesc;
            }

            var companies = artistNode?["company"]?.Value<string>() ?? artistNode?["briefDescCompany"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(companies))
            {
                detail.ExtraMetadata["经纪公司"] = companies;
            }

            var birthTimestamp = artistNode?["birth"]?.Value<long?>() ?? dataNode?["birth"]?.Value<long?>();
            if (birthTimestamp.HasValue && birthTimestamp.Value > 0)
            {
                detail.ExtraMetadata["出生日期"] = DateTimeOffset.FromUnixTimeMilliseconds(birthTimestamp.Value)
                    .DateTime.ToString("yyyy-MM-dd");
            }

            if (includeIntroduction)
            {
                try
                {
                    var (briefDesc, fullDesc, sections) = await FetchArtistIntroductionAsync(artistId);

                    if (!string.IsNullOrWhiteSpace(briefDesc))
                    {
                        detail.BriefDesc = NormalizeSummary(briefDesc);
                    }

                    if (!string.IsNullOrWhiteSpace(fullDesc))
                    {
                        detail.Description = fullDesc;
                    }

                    if (sections != null && sections.Count > 0)
                    {
                        detail.Introductions = sections;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[API] 获取歌手介绍失败: {ex.Message}");
                }
            }

            return detail;
        }

        /// <summary>
        /// 获取歌手介绍信息。
        /// </summary>
        private async Task<(string BriefDesc, string Description, List<ArtistIntroductionSection> Sections)> FetchArtistIntroductionAsync(long artistId)
        {
            var payload = new Dictionary<string, object>
            {
                { "id", artistId }
            };

            var response = await PostWeApiAsync<JObject>("/artist/introduction", payload);

            if (response == null)
            {
                return (string.Empty, string.Empty, new List<ArtistIntroductionSection>());
            }

            string briefDesc = response["briefDesc"]?.Value<string>() ?? string.Empty;
            var sections = ParseArtistIntroductionSections(response["introduction"] as JArray);
            string description = BuildIntroductionSummary(sections);

            if (string.IsNullOrWhiteSpace(description))
            {
                description = NormalizeDescription(response["txt"]?.Value<string>(), briefDesc);
            }

            if (string.IsNullOrWhiteSpace(description))
            {
                description = NormalizeDescription(briefDesc);
            }

            return (briefDesc, description, sections);
        }

        /// <summary>
        /// 获取歌手热门 50 首歌曲。
        /// </summary>
        public async Task<List<SongInfo>> GetArtistTopSongsAsync(long artistId)
        {
            if (artistId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(artistId));
            }

            var payload = new Dictionary<string, object>
            {
                { "id", artistId }
            };

            var response = await PostWeApiAsync<JObject>(
                "/api/artist/top/song",
                payload,
                autoConvertApiSegment: true);
            return ParseSongList(response?["songs"] as JArray);
        }

        /// <summary>
        /// 分页获取歌手歌曲列表。
        /// </summary>
        public async Task<(List<SongInfo> Songs, bool HasMore, int TotalCount)> GetArtistSongsAsync(long artistId, int limit = 50, int offset = 0, string order = "hot")
        {
            if (artistId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(artistId));
            }

            var payload = new Dictionary<string, object>
            {
                { "id", artistId },
                { "private_cloud", "true" },
                { "work_type", 1 },
                { "order", string.IsNullOrWhiteSpace(order) ? "hot" : order },
                { "offset", offset },
                { "limit", limit }
            };

            var response = await PostWeApiAsync<JObject>(
                "/api/v1/artist/songs",
                payload,
                autoConvertApiSegment: true);

            var songs = ParseSongList(response?["songs"] as JArray);
            bool hasMore = response?["more"]?.Value<bool>() ?? response?["hasMore"]?.Value<bool>() ?? false;
            int totalCount = response?["total"]?.Value<int>() ?? response?["songCount"]?.Value<int>() ?? (offset + songs.Count + (hasMore ? 1 : 0));

            return (songs, hasMore, totalCount);
        }

        /// <summary>
        /// 分页获取歌手专辑列表。
        /// </summary>
        public async Task<(List<AlbumInfo> Albums, bool HasMore, int TotalCount)> GetArtistAlbumsAsync(long artistId, int limit = 30, int offset = 0)
        {
            if (artistId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(artistId));
            }

            var payload = new Dictionary<string, object>
            {
                { "limit", limit },
                { "offset", offset },
                { "total", true }
            };

            var response = await PostWeApiAsync<JObject>($"/artist/albums/{artistId}", payload);

            var albums = ParseAlbumList(response?["hotAlbums"] as JArray ?? response?["albums"] as JArray);
            bool hasMore = response?["more"]?.Value<bool>() ?? response?["hasMore"]?.Value<bool>() ?? ((offset + albums.Count) < (response?["albumCount"]?.Value<int>() ?? 0));
            int totalCount = response?["albumCount"]?.Value<int>() ?? response?["total"]?.Value<int>() ?? (offset + albums.Count + (hasMore ? 1 : 0));

            return (albums, hasMore, totalCount);
        }

        /// <summary>
        /// 获取已收藏的歌手列表。
        /// </summary>
        public async Task<SearchResult<ArtistInfo>> GetArtistSubscriptionsAsync(int limit = 25, int offset = 0)
        {
            var payload = new Dictionary<string, object>
            {
                { "limit", limit },
                { "offset", offset },
                { "total", true }
            };

            var response = await PostWeApiAsync<JObject>("/artist/sublist", payload);

            var dataNode = response?["data"] as JObject ?? response;
            var artists = ParseArtistList(dataNode?["artists"] as JArray ?? dataNode?["list"] as JArray ?? dataNode?["data"] as JArray);
            int totalCount = dataNode?["artistCount"]?.Value<int>() ?? response?["count"]?.Value<int>() ?? artists.Count;

            return new SearchResult<ArtistInfo>(artists, totalCount, offset, limit, response);
        }

        /// <summary>
        /// 收藏或取消收藏歌手。
        /// </summary>
        public async Task<bool> SetArtistSubscriptionAsync(long artistId, bool subscribe)
        {
            if (artistId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(artistId));
            }

            var payload = new Dictionary<string, object>
            {
                { "artistId", artistId.ToString() },
                { "artistIds", $"[{artistId}]" }
            };

            string endpoint = subscribe ? "/artist/sub" : "/artist/unsub";
            var response = await PostWeApiAsync<JObject>(endpoint, payload);
            int code = response?["code"]?.Value<int>() ?? -1;
            return code == 200;
        }

        /// <summary>
        /// 根据分类获取歌手列表。
        /// </summary>
        public async Task<SearchResult<ArtistInfo>> GetArtistsByCategoryAsync(int typeCode, int areaCode, int limit = 30, int offset = 0, int? initial = null)
        {
            var payload = new Dictionary<string, object>
            {
                { "type", typeCode },
                { "area", areaCode },
                { "limit", limit },
                { "offset", offset },
                { "total", true }
            };

            if (initial.HasValue)
            {
                payload["initial"] = initial.Value;
            }

            var response = await PostWeApiAsync<JObject>(
                "/api/v1/artist/list",
                payload,
                autoConvertApiSegment: true);

            var artists = ParseArtistList(response?["artists"] as JArray ?? response?["list"] as JArray);
            int totalCount = response?["total"]?.Value<int>() ?? response?["count"]?.Value<int>() ?? artists.Count;

            return new SearchResult<ArtistInfo>(artists, totalCount, offset, limit, response);
        }

        #endregion

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

        #region 推荐和个性化

        /// <summary>
        /// 获取用户账号信息
        /// 参考: NeteaseCloudMusicApi/module/user_account.js
        /// </summary>
        public async Task<UserAccountInfo> GetUserAccountAsync()
        {
            try
            {
                var payload = new Dictionary<string, object>();

                var response = await PostWeApiAsync<JObject>("/nuser/account/get", payload);

                // ⭐ 修复：添加更详细的错误检查
                if (response == null)
                {
                    throw new Exception("获取用户信息失败: 响应为空");
                }

                int code = response["code"]?.Value<int>() ?? -1;
                if (code != 200)
                {
                    string message = response["message"]?.Value<string>() ?? "未知错误";
                    throw new Exception($"获取用户信息失败: code={code}, message={message}");
                }

                // 调试：输出完整的响应数据
                System.Diagnostics.Debug.WriteLine("[GetUserAccountAsync] 完整响应:");
                System.Diagnostics.Debug.WriteLine(response.ToString(Newtonsoft.Json.Formatting.Indented));

                var profile = response["profile"];
                var account = response["account"];

                // ⭐ 修复：添加 null 检查并抛出异常
                if (profile == null)
                {
                    throw new Exception("获取用户信息失败: profile 字段为空");
                }

            // 从 account 字段获取 VIP 信息
            int vipType = 0;
            if (account != null)
            {
                vipType = account["vipType"]?.Value<int>() ?? 0;
                System.Diagnostics.Debug.WriteLine($"[GetUserAccountAsync] VIP类型(从account): {vipType}");
            }

            // 如果 account 中没有，尝试从 profile 获取
            if (vipType == 0)
            {
                vipType = profile["vipType"]?.Value<int>() ?? 0;
                System.Diagnostics.Debug.WriteLine($"[GetUserAccountAsync] VIP类型(从profile): {vipType}");
            }

            // 单独获取用户等级（参考 Python: /weapi/user/level）
            int level = 0;
            try
            {
                var levelResponse = await PostWeApiAsync<JObject>("/user/level", new Dictionary<string, object>());
                if (levelResponse["code"]?.Value<int>() == 200)
                {
                    // 尝试从 data.level 或直接从 level 获取
                    var data = levelResponse["data"];
                    if (data != null)
                    {
                        level = data["level"]?.Value<int>() ?? 0;
                    }
                    else
                    {
                        level = levelResponse["level"]?.Value<int>() ?? 0;
                    }
                    System.Diagnostics.Debug.WriteLine($"[GetUserAccountAsync] 用户等级(从/user/level): {level}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GetUserAccountAsync] 获取用户等级失败: {ex.Message}");
                // 如果失败，尝试从 profile 获取
                level = profile["level"]?.Value<int>() ?? 0;
            }

            // 获取生日和创建时间（修复时区问题：使用本地时间而不是UTC）
            DateTime? birthday = null;
            if (profile["birthday"] != null)
            {
                long birthdayTimestamp = profile["birthday"].Value<long>();
                if (birthdayTimestamp > 0)
                {
                    // 使用 ToLocalTime() 修复时区问题
                    birthday = DateTimeOffset.FromUnixTimeMilliseconds(birthdayTimestamp).LocalDateTime;
                    System.Diagnostics.Debug.WriteLine($"[GetUserAccountAsync] 生日时间戳: {birthdayTimestamp}, 转换后: {birthday}");
                }
            }

            DateTime? createTime = null;
            if (profile["createTime"] != null)
            {
                long createTimestamp = profile["createTime"].Value<long>();
                if (createTimestamp > 0)
                {
                    createTime = DateTimeOffset.FromUnixTimeMilliseconds(createTimestamp).LocalDateTime;
                }
            }

            // 获取统计信息（粉丝、关注、动态、听歌数）和额外信息 - 需要单独调用 user/detail API
            // 参考: NeteaseCloudMusicApi/module/user_detail.js
            int followers = 0;
            int follows = 0;
            int eventCount = 0;
            int listenSongs = 0;
            string artistName = null;
            long? artistId = null;
            int userType = 0;
            int playlistCount = 0;
            int playlistBeSubscribedCount = 0;
            int createDays = 0;
            string authTypeDesc = null;
            int djProgramCount = 0;
            bool inBlacklist = false;

            try
            {
                long userId = profile["userId"]?.Value<long>() ?? 0;
                if (userId > 0)
                {
                    var detailResponse = await PostWeApiAsync<JObject>($"/v1/user/detail/{userId}", new Dictionary<string, object>());
                    System.Diagnostics.Debug.WriteLine($"[GetUserAccountAsync] user/detail API 响应:");
                    System.Diagnostics.Debug.WriteLine(detailResponse.ToString(Newtonsoft.Json.Formatting.Indented));

                    if (detailResponse["code"]?.Value<int>() == 200)
                    {
                        var detailProfile = detailResponse["profile"];
                        if (detailProfile != null)
                        {
                            // 统计信息
                            followers = detailProfile["followeds"]?.Value<int>() ?? 0;
                            follows = detailProfile["follows"]?.Value<int>() ?? 0;
                            eventCount = detailProfile["eventCount"]?.Value<int>() ?? 0;

                            // 额外信息
                            artistName = detailProfile["artistName"]?.Value<string>();
                            artistId = detailProfile["artistId"]?.Value<long>();
                            userType = detailProfile["userType"]?.Value<int>() ?? 0;
                            playlistCount = detailProfile["playlistCount"]?.Value<int>() ?? 0;
                            playlistBeSubscribedCount = detailProfile["playlistBeSubscribedCount"]?.Value<int>() ?? 0;
                            djProgramCount = detailProfile["sDJPCount"]?.Value<int>() ?? 0;
                            inBlacklist = detailProfile["inBlacklist"]?.Value<bool>() ?? false;

                            // 解析认证类型
                            var allAuthTypes = detailProfile["allAuthTypes"];
                            if (allAuthTypes != null && allAuthTypes.HasValues)
                            {
                                try
                                {
                                    var authList = new System.Collections.Generic.List<string>();
                                    foreach (var authType in allAuthTypes)
                                    {
                                        string desc = authType["desc"]?.Value<string>();
                                        var tags = authType["tags"] as Newtonsoft.Json.Linq.JArray;
                                        if (!string.IsNullOrEmpty(desc))
                                        {
                                            if (tags != null && tags.Count > 0)
                                            {
                                                authList.Add($"{desc}（{string.Join("、", tags.Select(t => t.Value<string>()))}）");
                                            }
                                            else
                                            {
                                                authList.Add(desc);
                                            }
                                        }
                                    }
                                    if (authList.Count > 0)
                                    {
                                        authTypeDesc = string.Join("；", authList);
                                    }
                                }
                                catch
                                {
                                    // 解析失败，忽略
                                }
                            }
                        }

                        // 注意：listenSongs 和 createDays 在 API 响应的顶层，不在 profile 里！
                        listenSongs = detailResponse["listenSongs"]?.Value<int>() ?? 0;
                        createDays = detailResponse["createDays"]?.Value<int>() ?? 0;

                        System.Diagnostics.Debug.WriteLine($"[GetUserAccountAsync] 从 user/detail 获取统计: 粉丝={followers}, 关注={follows}, 动态={eventCount}, 听歌数={listenSongs}");
                        System.Diagnostics.Debug.WriteLine($"[GetUserAccountAsync] 额外信息: 艺人名={artistName}, 用户类型={userType}, 歌单数={playlistCount}, 注册天数={createDays}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GetUserAccountAsync] 获取统计信息失败: {ex.Message}");
                // 继续执行，使用默认值 0
            }

            var userInfo = new UserAccountInfo
            {
                UserId = profile["userId"]?.Value<long>() ?? 0,
                Nickname = profile["nickname"]?.Value<string>(),
                AvatarUrl = profile["avatarUrl"]?.Value<string>(),
                Signature = profile["signature"]?.Value<string>(),
                VipType = vipType,
                Level = level,
                Gender = profile["gender"]?.Value<int>() ?? 0,
                Province = profile["province"]?.Value<int>() ?? 0,
                City = profile["city"]?.Value<int>() ?? 0,
                ListenSongs = listenSongs,
                Followers = followers,
                Follows = follows,
                EventCount = eventCount,
                Birthday = birthday,
                CreateTime = createTime,
                ArtistName = artistName,
                ArtistId = artistId,
                UserType = userType,
                PlaylistCount = playlistCount,
                PlaylistBeSubscribedCount = playlistBeSubscribedCount,
                CreateDays = createDays,
                AuthTypeDesc = authTypeDesc,
                DjProgramCount = djProgramCount,
                InBlacklist = inBlacklist
            };

            System.Diagnostics.Debug.WriteLine($"[GetUserAccountAsync] 最终解析结果: 昵称={userInfo.Nickname}, VIP={userInfo.VipType}, 等级={userInfo.Level}");
            System.Diagnostics.Debug.WriteLine($"[GetUserAccountAsync] 最终统计信息: 粉丝={userInfo.Followers}, 关注={userInfo.Follows}, 动态={userInfo.EventCount}, 听歌数={userInfo.ListenSongs}");

            return userInfo;
            }
            catch (Exception ex)
            {
                // ⭐ 修复：记录完整错误信息并重新抛出异常
                System.Diagnostics.Debug.WriteLine($"[GetUserAccountAsync] 失败: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[GetUserAccountAsync] 异常类型: {ex.GetType().Name}");
                System.Diagnostics.Debug.WriteLine($"[GetUserAccountAsync] 堆栈跟踪: {ex.StackTrace}");
                throw; // 重新抛出异常，让调用者处理
            }
        }

        /// <summary>
        /// 获取每日推荐歌单
        /// 参考: NeteaseCloudMusicApi/module/recommend_resource.js
        /// </summary>
        public async Task<List<PlaylistInfo>> GetDailyRecommendPlaylistsAsync()
        {
            var payload = new Dictionary<string, object>();

            var response = await PostWeApiAsync<JObject>("/v1/discovery/recommend/resource", payload);

            if (response["code"]?.Value<int>() != 200)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 获取每日推荐歌单失败: {response["message"]}");
                return new List<PlaylistInfo>();
            }

            var recommend = response["recommend"] as JArray;
            return ParsePlaylistList(recommend);
        }

        /// <summary>
        /// 获取每日推荐歌曲
        /// 参考: NeteaseCloudMusicApi/module/recommend_songs.js
        /// 注意: 需要设置 os = "ios"
        /// </summary>
        public async Task<List<SongInfo>> GetDailyRecommendSongsAsync()
        {
            // 创建临时cookies，设置os为ios (这是关键!)
            var tempCookies = new Dictionary<string, string>(_cookieContainer.GetCookies(new Uri(OFFICIAL_API_BASE))
                .Cast<Cookie>()
                .ToDictionary(c => c.Name, c => c.Value))
            {
                ["os"] = "ios"
            };

            var payload = new Dictionary<string, object>();

            // 构造Cookie header
            string cookieHeader = string.Join("; ", tempCookies.Select(kvp => $"{kvp.Key}={kvp.Value}"));

            // 手动发送请求
            var encrypted = EncryptionHelper.EncryptWeapi(JsonConvert.SerializeObject(payload));

            var formData = new Dictionary<string, string>
            {
                { "params", encrypted.Params },
                { "encSecKey", encrypted.EncSecKey }
            };
            var content = new FormUrlEncodedContent(formData);

            var request = new HttpRequestMessage(HttpMethod.Post, $"{OFFICIAL_API_BASE}/api/v3/discovery/recommend/songs")
            {
                Content = content
            };

            request.Headers.Add("Cookie", cookieHeader);
            request.Headers.Add("User-Agent", _desktopUserAgent ?? USER_AGENT);
            request.Headers.Add("Referer", REFERER);

            var httpResponse = await _httpClient.SendAsync(request);
            string responseText = await httpResponse.Content.ReadAsStringAsync();

            var response = JObject.Parse(responseText);

            if (response["code"]?.Value<int>() != 200)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 获取每日推荐歌曲失败: {response["message"]}");
                return new List<SongInfo>();
            }

            var data = response["data"];
            var dailySongs = data?["dailySongs"] as JArray;

            return ParseSongList(dailySongs);
        }

        /// <summary>
        /// 获取个性化推荐歌单
        /// 参考: NeteaseCloudMusicApi/module/personalized.js
        /// </summary>
        public async Task<List<PlaylistInfo>> GetPersonalizedPlaylistsAsync(int limit = 30)
        {
            var payload = new Dictionary<string, object>
            {
                { "limit", limit },
                { "total", true },
                { "n", 1000 }
            };

            var response = await PostWeApiAsync<JObject>("/personalized/playlist", payload);

            if (response["code"]?.Value<int>() != 200)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 获取个性化推荐失败: {response["message"]}");
                return new List<PlaylistInfo>();
            }

            var result = response["result"] as JArray;
            return ParsePlaylistList(result);
        }

        /// <summary>
        /// 获取私人FM歌曲 (私人雷达)
        /// 参考: NeteaseCloudMusicApi/module/personal_fm.js
        /// </summary>
        public async Task<List<SongInfo>> GetPersonalFMAsync()
        {
            var payload = new Dictionary<string, object>();

            var response = await PostWeApiAsync<JObject>("/v1/radio/get", payload);

            if (response["code"]?.Value<int>() != 200)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 获取私人FM失败: {response["message"]}");
                return new List<SongInfo>();
            }

            var data = response["data"] as JArray;
            return ParseSongList(data);
        }

        /// <summary>
        /// 获取用户歌单（包括创建和收藏的歌单）
        /// 参考: NeteaseCloudMusicApi/module/user_playlist.js
        /// </summary>
        public async Task<(List<PlaylistInfo>, int)> GetUserPlaylistsAsync(long userId, int limit = 1000, int offset = 0)
        {
            var payload = new Dictionary<string, object>
            {
                { "uid", userId },
                { "limit", limit },
                { "offset", offset },
                { "includeVideo", true }
            };

            var response = await PostWeApiAsync<JObject>("/user/playlist", payload);

            if (response["code"]?.Value<int>() != 200)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 获取用户歌单失败: {response["message"]}");
                return (new List<PlaylistInfo>(), 0);
            }

            var playlists = response["playlist"] as JArray;

            // 尝试从响应中解析总数，检查常见的字段名
            int totalCount = 0;
            if (response["total"] != null)
            {
                totalCount = response["total"].Value<int>();
                System.Diagnostics.Debug.WriteLine($"[API] 用户歌单总数(total): {totalCount}");
            }
            else if (response["count"] != null)
            {
                totalCount = response["count"].Value<int>();
                System.Diagnostics.Debug.WriteLine($"[API] 用户歌单总数(count): {totalCount}");
            }
            else if (playlists != null)
            {
                // 如果API不返回总数，使用当前获取的数量
                totalCount = playlists.Count;
                System.Diagnostics.Debug.WriteLine($"[API] 用户歌单数量(从列表计算): {totalCount}");
            }

            return (ParsePlaylistList(playlists), totalCount);
        }

        /// <summary>
        /// 获取所有排行榜
        /// 参考: NeteaseCloudMusicApi/module/toplist.js
        /// </summary>
        public async Task<List<PlaylistInfo>> GetToplistAsync()
        {
            var payload = new Dictionary<string, object>();

            var response = await PostWeApiAsync<JObject>("/toplist", payload);

            if (response["code"]?.Value<int>() != 200)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 获取排行榜失败: {response["message"]}");
                return new List<PlaylistInfo>();
            }

            var list = response["list"] as JArray;
            if (list == null)
            {
                return new List<PlaylistInfo>();
            }

            var result = new List<PlaylistInfo>();
            foreach (var item in list)
            {
                var playlist = new PlaylistInfo
                {
                    Id = item["id"]?.Value<string>(),
                    Name = item["name"]?.Value<string>(),
                    CoverUrl = item["coverImgUrl"]?.Value<string>(),
                    Description = item["description"]?.Value<string>(),
                    TrackCount = item["trackCount"]?.Value<int>() ?? 0
                };

                result.Add(playlist);
            }

            return result;
        }

        /// <summary>
        /// 获取用户喜欢的歌曲列表
        /// 参考: NeteaseCloudMusicApi/module/likelist.js
        /// </summary>
        public async Task<List<string>> GetUserLikedSongsAsync(long userId)
        {
            // ⭐ 调试信息：检查登录状态
            System.Diagnostics.Debug.WriteLine($"[GetUserLikedSongs] 开始获取喜欢的歌曲");
            System.Diagnostics.Debug.WriteLine($"[GetUserLikedSongs] UserId={userId}");
            System.Diagnostics.Debug.WriteLine($"[GetUserLikedSongs] UsePersonalCookie={UsePersonalCookie}");
            System.Diagnostics.Debug.WriteLine($"[GetUserLikedSongs] MUSIC_U={(string.IsNullOrEmpty(_musicU) ? "未设置" : $"已设置(长度:{_musicU.Length})")}");
            System.Diagnostics.Debug.WriteLine($"[GetUserLikedSongs] CSRF={(string.IsNullOrEmpty(_csrfToken) ? "未设置" : "已设置")}");

            var payload = new Dictionary<string, object>
            {
                { "uid", userId }
            };

            var response = await PostWeApiAsync<JObject>("/song/like/get", payload);

            if (response["code"]?.Value<int>() != 200)
            {
                int code = response["code"]?.Value<int>() ?? -1;
                string message = response["message"]?.Value<string>() ?? response["msg"]?.Value<string>() ?? "未知错误";
                System.Diagnostics.Debug.WriteLine($"[API] 获取喜欢的歌曲失败: code={code}, message={message}");
                System.Diagnostics.Debug.WriteLine($"[API] 完整响应: {response.ToString()}");
                return new List<string>();
            }

            var ids = response["ids"] as JArray;
            if (ids == null)
            {
                return new List<string>();
            }

            return ids
                .Select(id => id.Value<string>())
                .Where(id => !string.IsNullOrEmpty(id))
                .Select(id => id!)
                .ToList();
        }

        /// <summary>
        /// 获取最近播放的歌曲
        /// 参考: NeteaseCloudMusicApi/module/record_recent_song.js
        /// </summary>
        /// <param name="limit">返回数量，默认100</param>
        /// <returns>最近播放的歌曲列表</returns>
        public async Task<List<SongInfo>> GetRecentPlayedSongsAsync(int limit = 100)
        {
            System.Diagnostics.Debug.WriteLine($"[GetRecentPlayedSongs] 开始获取最近播放歌曲, limit={limit}");

            var payload = new Dictionary<string, object>
            {
                { "limit", limit }
            };

            try
            {
                // 注意：该接口位于 /api 前缀，需保持原始路径
                var response = await PostWeApiAsync<JObject>(
                    "/api/play-record/song/list",
                    payload,
                    autoConvertApiSegment: true);

                if (response["code"]?.Value<int>() != 200)
                {
                    int code = response["code"]?.Value<int>() ?? -1;
                    string message = response["message"]?.Value<string>() ?? response["msg"]?.Value<string>() ?? "未知错误";
                    System.Diagnostics.Debug.WriteLine($"[API] 获取最近播放歌曲失败: code={code}, message={message}");
                    return new List<SongInfo>();
                }

                var data = response["data"]?["list"] as JArray;
                if (data == null)
                {
                    // 尝试直接从 data 字段获取
                    data = response["data"] as JArray;
                }

                if (data == null)
                {
                    System.Diagnostics.Debug.WriteLine("[API] 最近播放歌曲数据为空");
                    return new List<SongInfo>();
                }

                var songs = new List<SongInfo>();
                foreach (var item in data)
                {
                    // 提取歌曲数据（可能在 data 或 song 字段中）
                    var songData = item["data"] ?? item["song"] ?? item;

                    if (songData == null) continue;

                    var song = new SongInfo
                    {
                        Id = songData["id"]?.Value<string>() ?? songData["id"]?.Value<long>().ToString(),
                        Name = songData["name"]?.Value<string>() ?? "未知歌曲",
                        Artist = string.Join("/",
                            (songData["artists"] ?? songData["ar"])?.Select(a => a["name"]?.Value<string>()).Where(n => !string.IsNullOrWhiteSpace(n))
                            ?? new[] { "未知艺术家" }),
                        Album = (songData["album"] ?? songData["al"])?["name"]?.Value<string>() ?? "未知专辑",
                        AlbumId = (songData["album"] ?? songData["al"])?["id"]?.Value<string>()
                            ?? (songData["album"] ?? songData["al"])?["id"]?.Value<long>().ToString(),
                        Duration = (int)(songData["duration"]?.Value<long>() ?? songData["dt"]?.Value<long>() ?? 0),
                        PicUrl = (songData["album"] ?? songData["al"])?["picUrl"]?.Value<string>() ?? ""
                    };

                    var recentArtists = songData["artists"] as JArray ?? songData["ar"] as JArray;
                    if (recentArtists != null && recentArtists.Count > 0)
                    {
                        var artistNames = new List<string>();
                        foreach (var artistToken in recentArtists)
                        {
                            if (artistToken == null || artistToken.Type != JTokenType.Object)
                            {
                                continue;
                            }

                            var artistObj = (JObject)artistToken;
                            var artistName = artistObj["name"]?.Value<string>();
                            if (!string.IsNullOrWhiteSpace(artistName))
                            {
                                artistNames.Add(artistName);
                            }

                            var artistIdValue = artistObj["id"]?.Value<long>() ?? 0;
                            if (artistIdValue > 0)
                            {
                                song.ArtistIds.Add(artistIdValue);
                            }
                        }

                        if (artistNames.Count > 0)
                        {
                            song.ArtistNames = new List<string>(artistNames);
                            song.Artist = string.Join("/", artistNames);
                        }
                    }

                    songs.Add(song);
                }

                System.Diagnostics.Debug.WriteLine($"[API] 成功获取 {songs.Count} 首最近播放歌曲");
                return songs;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 获取最近播放歌曲异常: {ex.Message}");
                return new List<SongInfo>();
            }
        }

        /// <summary>
        /// 获取用户收藏的专辑列表
        /// 参考: NeteaseCloudMusicApi/module/album_sublist.js
        /// </summary>
        public async Task<(List<AlbumInfo>, int)> GetUserAlbumsAsync(int limit = 100, int offset = 0)
        {
            var payload = new Dictionary<string, object>
            {
                { "limit", limit },
                { "offset", offset },
                { "total", true }
            };

            var response = await PostWeApiAsync<JObject>("/album/sublist", payload);

            if (response["code"]?.Value<int>() != 200)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 获取收藏专辑失败: {response["message"]}");
                return (new List<AlbumInfo>(), 0);
            }

            // 解析总数
            int totalCount = response["count"]?.Value<int>() ?? 0;
            System.Diagnostics.Debug.WriteLine($"[API] 收藏专辑总数: {totalCount}");

            var data = response["data"] as JArray;
            if (data == null)
            {
                return (new List<AlbumInfo>(), totalCount);
            }

            var result = new List<AlbumInfo>();
            foreach (var item in data)
            {
                var album = new AlbumInfo
                {
                    Id = item["id"]?.Value<string>(),
                    Name = item["name"]?.Value<string>(),
                    Artist = item["artist"]?.Value<string>() ?? item["artists"]?[0]?["name"]?.Value<string>(),
                    PicUrl = item["picUrl"]?.Value<string>(),
                    PublishTime = item["publishTime"]?.Value<long>().ToString()
                };

                result.Add(album);
            }

            return (result, totalCount);
        }

        /// <summary>
        /// 云盘功能
        /// </summary>
        #region 云盘

        /// <summary>
        /// 获取云盘歌曲列表
        /// </summary>
        public async Task<CloudSongPageResult> GetCloudSongsAsync(
            int limit = 50,
            int offset = 0,
            CancellationToken cancellationToken = default)
        {
            var page = new CloudSongPageResult
            {
                Limit = limit,
                Offset = offset
            };

            try
            {
                var payload = new Dictionary<string, object>
                {
                    { "limit", limit },
                    { "offset", offset }
                };

                var response = await PostWeApiAsync<JObject>(
                    "/v1/cloud/get",
                    payload,
                    cancellationToken: cancellationToken);

                if (response == null)
                {
                    return page;
                }

                page.TotalCount = response["count"]?.Value<int>() ?? response["size"]?.Value<int>() ?? page.TotalCount;
                page.UsedSize = response["size"]?.Value<long>() ?? page.UsedSize;
                page.MaxSize = response["maxSize"]?.Value<long>() ?? page.MaxSize;
                page.HasMore = response["hasMore"]?.Value<bool>() ?? response["more"]?.Value<bool>() ?? false;

                var dataArray = response["data"] as JArray;
                if (dataArray == null || dataArray.Count == 0)
                {
                    return page;
                }

                var songIds = new List<string>();
                foreach (var item in dataArray.OfType<JObject>())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string matchedId = item["simpleSong"]?["id"]?.ToString();
                    string cloudId = item["songId"]?.ToString();

                    if (!string.IsNullOrEmpty(matchedId))
                    {
                        songIds.Add(matchedId);
                    }
                    else if (!string.IsNullOrEmpty(cloudId))
                    {
                        songIds.Add(cloudId);
                    }
                }

                var uniqueSongIds = songIds
                    .Where(id => !string.IsNullOrEmpty(id))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                var resolvedSongs = uniqueSongIds.Count > 0
                    ? await GetSongsByIdsAsync(uniqueSongIds, cancellationToken)
                    : new List<SongInfo>();

                var resolvedMap = resolvedSongs.ToDictionary(s => s.Id, StringComparer.Ordinal);

                foreach (var item in dataArray.OfType<JObject>())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string cloudSongId = item["songId"]?.ToString() ?? string.Empty;
                    string matchedSongId = item["simpleSong"]?["id"]?.ToString();
                    string lookupId = !string.IsNullOrEmpty(matchedSongId) ? matchedSongId : cloudSongId;

                    SongInfo song;
                    if (!string.IsNullOrEmpty(lookupId) && resolvedMap.TryGetValue(lookupId, out var resolved))
                    {
                        song = resolved;
                    }
                    else
                    {
                        song = BuildFallbackCloudSong(item);
                        if (song == null)
                        {
                            continue;
                        }
                    }

                    song.IsCloudSong = true;
                    song.IsAvailable = true;
                    song.CloudSongId = string.IsNullOrEmpty(cloudSongId) ? lookupId ?? string.Empty : cloudSongId;
                    song.CloudMatchedSongId = matchedSongId ?? string.Empty;
                    song.CloudFileName = item["fileName"]?.Value<string>() ?? item["songName"]?.Value<string>() ?? song.CloudFileName ?? song.Name;
                    song.CloudFileSize = item["fileSize"]?.Value<long>() ?? song.CloudFileSize;
                    song.CloudUploadTime = item["addTime"]?.Value<long>();

                    if (song.CloudFileSize > 0 && song.Size == 0)
                    {
                        song.Size = song.CloudFileSize;
                    }

                    if (string.IsNullOrEmpty(song.Name))
                    {
                        song.Name = song.CloudFileName ?? $"云盘歌曲 {song.CloudSongId}";
                    }

                    if (string.IsNullOrEmpty(song.Artist))
                    {
                        song.Artist = item["artist"]?.Value<string>() ?? string.Empty;
                    }

                    if (string.IsNullOrEmpty(song.Album))
                    {
                        song.Album = item["album"]?.Value<string>() ?? string.Empty;
                    }

                    page.Songs.Add(song);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Cloud] 获取云盘歌曲失败: {ex.Message}");
            }

            return page;
        }

        /// <summary>
        /// 删除云盘歌曲
        /// </summary>
        public async Task<bool> DeleteCloudSongsAsync(
            IEnumerable<string> cloudSongIds,
            CancellationToken cancellationToken = default)
        {
            if (cloudSongIds == null)
            {
                return false;
            }

            var ids = cloudSongIds
                .Select(id => id?.Trim())
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (ids.Count == 0)
            {
                return false;
            }

            var payload = new Dictionary<string, object>
            {
                { "songIds", ids }
            };

            var response = await PostWeApiAsync<JObject>(
                "/cloud/del",
                payload,
                cancellationToken: cancellationToken);

            return response?["code"]?.Value<int>() == 200;
        }

        /// <summary>
        /// 上传单个文件到云盘
        /// </summary>
        public async Task<CloudUploadResult> UploadCloudSongAsync(
            string filePath,
            IProgress<CloudUploadProgress> progress = null,
            CancellationToken cancellationToken = default,
            int fileIndex = 1,
            int totalFiles = 1)
        {
            var result = new CloudUploadResult
            {
                FilePath = filePath
            };

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(filePath))
                {
                    throw new ArgumentException("文件路径不能为空", nameof(filePath));
                }

                if (!System.IO.File.Exists(filePath))
                {
                    throw new FileNotFoundException("找不到指定的文件", filePath);
                }

                string originalFileName = System.IO.Path.GetFileName(filePath);
                string ext = System.IO.Path.GetExtension(filePath)?.TrimStart('.').ToLowerInvariant() ?? "mp3";
                if (originalFileName != null && originalFileName.ToLowerInvariant().Contains("flac"))
                {
                    ext = "flac";
                }

                string sanitizedFileName = SanitizeCloudFileName(originalFileName, ext);
                long fileSize = new System.IO.FileInfo(filePath).Length;
                const int bitrate = 999000;

                ReportUploadProgress(progress, filePath, fileIndex, totalFiles, 5, "计算文件校验", 0, fileSize);
                string md5 = await ComputeFileMd5Async(filePath, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                ReportUploadProgress(progress, filePath, fileIndex, totalFiles, 15, "检查云盘状态", 0, fileSize);
                var checkPayload = new Dictionary<string, object>
                {
                    { "bitrate", bitrate.ToString() },
                    { "ext", "" },
                    { "length", fileSize },
                    { "md5", md5 },
                    { "songId", "0" },
                    { "version", 1 }
                };

                var checkResp = await PostInterfaceWeApiAsync<JObject>(
                    "/api/cloud/upload/check",
                    checkPayload,
                    cancellationToken: cancellationToken);

                ValidateCloudResponse(checkResp, "检查云盘状态");

                bool needUpload = checkResp?["needUpload"]?.Value<bool>() ?? true;
                string checkSongId = checkResp?["songId"]?.Value<string>() ?? "0";

                ReportUploadProgress(progress, filePath, fileIndex, totalFiles, 30, "请求上传令牌", 0, fileSize);

                const string bucket = "jd-musicrep-privatecloud-audio-public";
                var tokenPayload = new Dictionary<string, object>
                {
                    { "bucket", bucket },
                    { "ext", ext },
                    { "filename", sanitizedFileName },
                    { "local", false },
                    { "nos_product", 3 },
                    { "type", "audio" },
                    { "md5", md5 }
                };

                var tokenResp = await PostWeApiAsync<JObject>(
                    "/nos/token/alloc",
                    tokenPayload,
                    cancellationToken: cancellationToken);

                ValidateCloudResponse(tokenResp, "获取上传令牌");

                var tokenResult = tokenResp?["result"] as JObject;
                string resourceId = tokenResult?["resourceId"]?.Value<string>() ?? string.Empty;
                string objectKey = tokenResult?["objectKey"]?.Value<string>() ?? string.Empty;
                string token = tokenResult?["token"]?.Value<string>() ?? string.Empty;

                if (string.IsNullOrWhiteSpace(resourceId) ||
                    string.IsNullOrWhiteSpace(objectKey) ||
                    string.IsNullOrWhiteSpace(token))
                {
                    throw new Exception("上传令牌响应缺少必要字段");
                }

                if (string.IsNullOrEmpty(objectKey) || string.IsNullOrEmpty(token))
                {
                    throw new Exception("获取上传令牌失败");
                }

                if (needUpload)
                {
                    ReportUploadProgress(progress, filePath, fileIndex, totalFiles, 45, "上传音频文件", 0, fileSize);
                    await UploadToNosAsync(
                        filePath,
                        bucket,
                        objectKey,
                        token,
                        md5,
                        ext,
                        fileSize,
                        progress,
                        fileIndex,
                        totalFiles,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    ReportUploadProgress(progress, filePath, fileIndex, totalFiles, 55, "文件已存在，跳过上传", fileSize, fileSize);
                }

                var metadata = ExtractAudioMetadata(filePath);
                string songName = string.IsNullOrWhiteSpace(metadata.Song)
                    ? System.IO.Path.GetFileNameWithoutExtension(originalFileName)
                    : metadata.Song;
                string artist = string.IsNullOrWhiteSpace(metadata.Artist) ? "未知艺术家" : metadata.Artist;
                string album = string.IsNullOrWhiteSpace(metadata.Album) ? "未知专辑" : metadata.Album;

                ReportUploadProgress(progress, filePath, fileIndex, totalFiles, 70, "提交云盘信息", fileSize, fileSize);
                var infoPayload = new Dictionary<string, object>
                {
                    { "md5", md5 },
                    { "songid", checkSongId },
                    { "filename", originalFileName },
                    { "song", songName },
                    { "album", album },
                    { "artist", artist },
                    { "bitrate", bitrate.ToString() },
                    { "resourceId", resourceId }
                };

                var infoResp = await PostWeApiAsync<JObject>(
                    "/upload/cloud/info/v2",
                    infoPayload,
                    cancellationToken: cancellationToken);

                ValidateCloudResponse(infoResp, "提交云盘信息");

                string cloudSongId = infoResp?["songId"]?.Value<string>() ?? infoResp?["id"]?.Value<string>() ?? checkSongId;

                ReportUploadProgress(progress, filePath, fileIndex, totalFiles, 85, "发布到云盘", fileSize, fileSize);
                var publishPayload = new Dictionary<string, object>
                {
                    { "songid", cloudSongId }
                };

                var publishResp = await PostInterfaceWeApiAsync<JObject>(
                    "/api/cloud/pub/v2",
                    publishPayload,
                    cancellationToken: cancellationToken);

                int publishCode = publishResp?["code"]?.Value<int>() ?? -1;
                if (publishCode != 200)
                {
                    string publishMsg = publishResp?["message"]?.Value<string>() ?? publishResp?["msg"]?.Value<string>() ?? $"code={publishCode}";
                    throw new Exception($"发布云盘歌曲失败: {publishMsg}");
                }

                result.Success = true;
                result.CloudSongId = cloudSongId ?? string.Empty;
                result.MatchedSongId = checkSongId ?? string.Empty;

                ReportUploadProgress(progress, filePath, fileIndex, totalFiles, 100, "上传完成", fileSize, fileSize);
            }
            catch (OperationCanceledException)
            {
                result.Success = false;
                result.ErrorMessage = "上传已取消";
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Cloud] 上传失败: {ex}");
                result.Success = false;
                result.ErrorMessage = GetInnermostExceptionMessage(ex);
            }

            return result;
        }

        private static string SanitizeCloudFileName(string fileName, string extension)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                return $"CloudUpload_{DateTime.Now:yyyyMMddHHmmss}";
            }

            string sanitized = fileName;
            if (!string.IsNullOrEmpty(extension))
            {
                string suffix = "." + extension;
                if (sanitized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    sanitized = sanitized.Substring(0, sanitized.Length - suffix.Length);
                }
            }

            sanitized = sanitized.Replace(" ", string.Empty).Replace(".", "_");

            return sanitized;
        }

        private static (string Song, string Artist, string Album) ExtractAudioMetadata(string filePath)
        {
            try
            {
                using var tagFile = TagLib.File.Create(filePath);
                var tag = tagFile?.Tag;

                string song = tag?.Title ?? string.Empty;
                string artist = string.Empty;

                if (tag != null)
                {
                    if (tag.Performers != null && tag.Performers.Length > 0)
                    {
                        artist = string.Join("/", tag.Performers.Where(p => !string.IsNullOrWhiteSpace(p)));
                    }

                    if (string.IsNullOrEmpty(artist) && tag.FirstPerformer != null)
                    {
                        artist = tag.FirstPerformer;
                    }
                }

                string album = tag?.Album ?? string.Empty;
                return (song ?? string.Empty, artist ?? string.Empty, album ?? string.Empty);
            }
            catch
            {
                return (string.Empty, string.Empty, string.Empty);
            }
        }

        private static async Task<string> ComputeFileMd5Async(string filePath, CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                using var stream = System.IO.File.OpenRead(filePath);
                using var md5 = MD5.Create();
                var hash = md5.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
            }, cancellationToken).ConfigureAwait(false);
        }

        private async Task UploadToNosAsync(
            string filePath,
            string bucket,
            string objectKey,
            string token,
            string md5,
            string extension,
            long fileSize,
            IProgress<CloudUploadProgress> progress,
            int fileIndex,
            int totalFiles,
            CancellationToken cancellationToken)
        {
            var lbsUrl = $"https://wanproxy.127.net/lbs?version=1.0&bucketname={Uri.EscapeDataString(bucket)}";
            var lbsResponse = await _uploadHttpClient.GetAsync(lbsUrl, cancellationToken).ConfigureAwait(false);
            lbsResponse.EnsureSuccessStatusCode();

            string lbsBody = await lbsResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
            var lbsJson = JObject.Parse(lbsBody);
            var uploadUri = BuildNosUploadUri(lbsJson, bucket, objectKey);

            using var request = new HttpRequestMessage(HttpMethod.Post, uploadUri);
            request.Headers.TryAddWithoutValidation("x-nos-token", token);
            request.Headers.TryAddWithoutValidation("Content-MD5", md5);
            request.Headers.TryAddWithoutValidation("Accept", "*/*");
            request.Headers.TryAddWithoutValidation("Connection", "keep-alive");
            request.Headers.Referrer = MUSIC_URI;
            request.Headers.ExpectContinue = false;
            var userAgent = _desktopUserAgent ?? USER_AGENT;
            if (!string.IsNullOrEmpty(userAgent))
            {
                request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            }

            using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            int lastPercent = -1;
            double lastSpeed = 0;
            var content = new ProgressStreamContent(
                fileStream,
                64 * 1024,
                uploadedBytes =>
                {
                    if (fileSize <= 0)
                    {
                        return;
                    }

                    int percent = (int)Math.Min(100, Math.Round(uploadedBytes * 100.0 / fileSize));
                    if (percent == lastPercent && uploadedBytes < fileSize)
                    {
                        return;
                    }

                    double speed = 0;
                    if (stopwatch.Elapsed.TotalSeconds > 0.01)
                    {
                        speed = uploadedBytes / stopwatch.Elapsed.TotalSeconds;
                        lastSpeed = speed;
                    }
                    else
                    {
                        speed = lastSpeed;
                    }

                    ReportUploadProgress(
                        progress,
                        filePath,
                        fileIndex,
                        totalFiles,
                        percent,
                        "上传音频文件",
                        uploadedBytes,
                        fileSize,
                        speed);

                    lastPercent = percent;
                },
                fileSize,
                MapMimeType(extension),
                cancellationToken);

            request.Content = content;

            HttpResponseMessage response;
            try
            {
                response = await _uploadHttpClient
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new Exception($"NOS上传请求失败（{uploadUri.Host}）：{ex.Message}", ex);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    string error = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    throw new Exception($"NOS上传失败: {response.StatusCode} {error}");
                }
            }

            stopwatch.Stop();

            if (lastSpeed <= 0 && fileSize > 0 && stopwatch.Elapsed.TotalSeconds > 0.01)
            {
                lastSpeed = fileSize / stopwatch.Elapsed.TotalSeconds;
            }

            ReportUploadProgress(
                progress,
                filePath,
                fileIndex,
                totalFiles,
                60,
                "文件上传完成",
                fileSize,
                fileSize,
                lastSpeed);
        }

        private static Uri BuildNosUploadUri(JObject lbsJson, string bucket, string objectKey)
        {
            var uploadArray = lbsJson["upload"] as JArray;
            string? endpointCandidate = uploadArray?.FirstOrDefault()?.Value<string>()?.Trim();

            Uri baseUri;
            if (!string.IsNullOrEmpty(endpointCandidate))
            {
                if (!Uri.TryCreate(endpointCandidate, UriKind.Absolute, out baseUri))
                {
                    string normalized = endpointCandidate.IndexOf("://", StringComparison.OrdinalIgnoreCase) >= 0
                        ? endpointCandidate
                        : $"http://{endpointCandidate}";
                    baseUri = Uri.TryCreate(normalized, UriKind.Absolute, out var parsed)
                        ? parsed
                        : new Uri("http://45.127.129.8");
                }
            }
            else
            {
                baseUri = new Uri("http://45.127.129.8");
            }

            var pathSegments = new List<string>();
            AppendPathSegments(pathSegments, baseUri.AbsolutePath);
            AppendPathSegments(pathSegments, bucket);
            AppendPathSegments(pathSegments, objectKey);

            var builder = new UriBuilder(baseUri.Scheme, baseUri.Host, baseUri.IsDefaultPort ? -1 : baseUri.Port)
            {
                Path = string.Join("/", pathSegments),
                Query = "offset=0&complete=true&version=1.0"
            };

            return builder.Uri;
        }

        private static void AppendPathSegments(List<string> segments, string path)
        {
            if (segments == null || string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            string decodedPath = Uri.UnescapeDataString(path);
            var parts = decodedPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                segments.Add(Uri.EscapeDataString(part));
            }
        }

        private static string MapMimeType(string extension)
        {
            return extension switch
            {
                "flac" => "audio/flac",
                "m4a" => "audio/mp4",
                "mp4" => "audio/mp4",
                "wav" => "audio/wav",
                "ogg" => "audio/ogg",
                "ape" => "audio/ape",
                "wma" => "audio/x-ms-wma",
                _ => "audio/mpeg"
            };
        }

        private static void ReportUploadProgress(
            IProgress<CloudUploadProgress> progress,
            string filePath,
            int fileIndex,
            int totalFiles,
            int percent,
            string stage,
            long bytesTransferred = 0,
            long totalBytes = 0,
            double speedBytesPerSecond = 0)
        {
            progress?.Report(new CloudUploadProgress
            {
                FilePath = filePath,
                FileIndex = fileIndex,
                TotalFiles = totalFiles,
                FileProgressPercent = percent,
                StageMessage = stage,
                BytesTransferred = bytesTransferred,
                TotalBytes = totalBytes,
                TransferSpeedBytesPerSecond = speedBytesPerSecond
            });
        }

        private static string GetInnermostExceptionMessage(Exception ex)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var ordered = new List<string>();
            Exception? current = ex;
            while (current != null)
            {
                if (!string.IsNullOrWhiteSpace(current.Message))
                {
                    string trimmed = current.Message.Trim();
                    if (seen.Add(trimmed))
                    {
                        ordered.Add(trimmed);
                    }
                }

                current = current.InnerException;
            }

            return ordered.Count > 0 ? string.Join(" -> ", ordered) : "未知错误";
        }

        private static void ValidateCloudResponse(JObject? response, string stage)
        {
            if (response == null)
            {
                throw new Exception($"{stage}：服务器未返回数据");
            }

            int code = response["code"]?.Value<int>() ?? 200;
            if (code != 200)
            {
                string message = response["message"]?.Value<string>() ??
                                 response["msg"]?.Value<string>() ??
                                 $"code={code}";
                throw new Exception($"{stage}失败：{message}");
            }
        }

        /// <summary>
        /// 支持进度回调的 HttpContent 包装
        /// </summary>
        private sealed class ProgressStreamContent : HttpContent
        {
            private readonly Stream _sourceStream;
            private readonly int _bufferSize;
            private readonly Action<long> _progressCallback;
            private readonly long _totalLength;
            private readonly CancellationToken _cancellationToken;

            public ProgressStreamContent(
                Stream sourceStream,
                int bufferSize,
                Action<long> progressCallback,
                long totalLength,
                string mediaType,
                CancellationToken cancellationToken)
            {
                _sourceStream = sourceStream ?? throw new ArgumentNullException(nameof(sourceStream));
                _bufferSize = bufferSize <= 0 ? 64 * 1024 : bufferSize;
                _progressCallback = progressCallback ?? (_ => { });
                _totalLength = totalLength;
                _cancellationToken = cancellationToken;

                Headers.ContentType = new MediaTypeHeaderValue(mediaType);
                if (totalLength > 0)
                {
                    Headers.ContentLength = totalLength;
                }
            }

            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            {
                var buffer = new byte[_bufferSize];
                long uploaded = 0;
                int bytesRead;

                while ((bytesRead = await _sourceStream
                           .ReadAsync(buffer, 0, buffer.Length, _cancellationToken)
                           .ConfigureAwait(false)) > 0)
                {
                    await stream.WriteAsync(buffer, 0, bytesRead, _cancellationToken).ConfigureAwait(false);
                    uploaded += bytesRead;
                    _progressCallback(uploaded);

                    if (_cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }

            protected override bool TryComputeLength(out long length)
            {
                if (_totalLength > 0)
                {
                    length = _totalLength;
                    return true;
                }

                length = 0;
                return false;
            }
        }

        private SongInfo? BuildFallbackCloudSong(JObject entry)
        {
            if (entry == null)
            {
                return null;
            }

            string cloudId = entry["songId"]?.ToString() ?? Guid.NewGuid().ToString("N");
            var song = new SongInfo
            {
                Id = cloudId,
                Name = entry["songName"]?.Value<string>() ?? entry["fileName"]?.Value<string>() ?? $"云盘歌曲 {cloudId}",
                Artist = entry["artist"]?.Value<string>() ?? string.Empty,
                Album = entry["album"]?.Value<string>() ?? string.Empty,
                CloudSongId = cloudId,
                CloudFileSize = entry["fileSize"]?.Value<long>() ?? 0,
                CloudUploadTime = entry["addTime"]?.Value<long>(),
                IsAvailable = true
            };

            long durationMs = entry["simpleSong"]?["duration"]?.Value<long>() ??
                              entry["songData"]?["duration"]?.Value<long>() ??
                              entry["duration"]?.Value<long>() ?? 0;
            if (durationMs > 0)
            {
                song.Duration = (int)(durationMs / 1000);
            }

            return song;
        }

        #endregion

        /// <summary>
        /// 获取推荐新歌
        /// 参考: NeteaseCloudMusicApi/module/personalized_newsong.js
        /// </summary>
        public async Task<List<SongInfo>> GetPersonalizedNewSongsAsync(int limit = 10)
        {
            var payload = new Dictionary<string, object>
            {
                { "type", "recommend" },
                { "limit", limit },
                { "areaId", 0 }
            };

            var response = await PostWeApiAsync<JObject>("/personalized/newsong", payload);

            if (response["code"]?.Value<int>() != 200)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 获取推荐新歌失败: {response["message"]}");
                return new List<SongInfo>();
            }

            var result = response["result"] as JArray;
            if (result == null)
            {
                return new List<SongInfo>();
            }

            // 从result中提取song字段
            var songs = new JArray();
            foreach (var item in result)
            {
                var song = item["song"];
                if (song != null)
                {
                    songs.Add(song);
                }
            }

            return ParseSongList(songs);
        }

        /// <summary>
        /// 获取用户听歌排行
        /// 参考: NeteaseCloudMusicApi/module/user_record.js
        /// </summary>
        /// <param name="uid">用户ID</param>
        /// <param name="type">0=全部时间, 1=最近一周</param>
        public async Task<List<(SongInfo song, int playCount)>> GetUserPlayRecordAsync(long uid, int type = 0)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[GetUserPlayRecord] uid={uid}, type={type}");

                var payload = new Dictionary<string, object>
                {
                    { "uid", uid },
                    { "type", type }
                };

                var response = await PostWeApiAsync<JObject>("/v1/play/record", payload);

                if (response["code"]?.Value<int>() != 200)
                {
                    int code = response["code"]?.Value<int>() ?? -1;
                    string message = response["message"]?.Value<string>() ?? response["msg"]?.Value<string>() ?? "未知错误";
                    System.Diagnostics.Debug.WriteLine($"[API] 获取用户听歌排行失败: code={code}, message={message}");
                    return new List<(SongInfo, int)>();
                }

                // 根据type选择weekData或allData
                JArray data = type == 1
                    ? response["weekData"] as JArray
                    : response["allData"] as JArray;

                if (data == null)
                {
                    System.Diagnostics.Debug.WriteLine("[API] 听歌排行数据为空");
                    return new List<(SongInfo, int)>();
                }

                var result = new List<(SongInfo, int)>();
                foreach (var item in data)
                {
                    var songData = item["song"];
                    if (songData == null) continue;

                    var song = new SongInfo
                    {
                        Id = songData["id"]?.Value<string>() ?? songData["id"]?.Value<long>().ToString(),
                        Name = songData["name"]?.Value<string>() ?? "未知歌曲",
                        Artist = string.Join("/",
                            (songData["ar"] ?? songData["artists"])?.Select(a => a["name"]?.Value<string>()).Where(n => !string.IsNullOrWhiteSpace(n))
                            ?? new[] { "未知艺术家" }),
                        Album = (songData["al"] ?? songData["album"])?["name"]?.Value<string>() ?? "未知专辑",
                        AlbumId = (songData["al"] ?? songData["album"])?["id"]?.Value<string>()
                            ?? (songData["al"] ?? songData["album"])?["id"]?.Value<long>().ToString(),
                        Duration = (int)(songData["dt"]?.Value<long>() ?? songData["duration"]?.Value<long>() ?? 0),
                        PicUrl = (songData["al"] ?? songData["album"])?["picUrl"]?.Value<string>() ?? ""
                    };

                    var recordArtists = songData["ar"] as JArray ?? songData["artists"] as JArray;
                    if (recordArtists != null && recordArtists.Count > 0)
                    {
                        var artistNames = new List<string>();
                        foreach (var artistToken in recordArtists)
                        {
                            if (artistToken == null || artistToken.Type != JTokenType.Object)
                            {
                                continue;
                            }

                            var artistObj = (JObject)artistToken;
                            var artistName = artistObj["name"]?.Value<string>();
                            if (!string.IsNullOrWhiteSpace(artistName))
                            {
                                artistNames.Add(artistName);
                            }

                            var artistIdValue = artistObj["id"]?.Value<long>() ?? 0;
                            if (artistIdValue > 0)
                            {
                                song.ArtistIds.Add(artistIdValue);
                            }
                        }

                        if (artistNames.Count > 0)
                        {
                            song.ArtistNames = new List<string>(artistNames);
                            song.Artist = string.Join("/", artistNames);
                        }
                    }

                    var playCount = item["playCount"]?.Value<int>() ?? 0;
                    result.Add((song, playCount));
                }

                System.Diagnostics.Debug.WriteLine($"[API] 成功获取 {result.Count} 首听歌排行");
                return result;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 获取听歌排行异常: {ex.Message}");
                return new List<(SongInfo, int)>();
            }
        }

        /// <summary>
        /// 获取精品歌单
        /// 参考: NeteaseCloudMusicApi/module/top_playlist_highquality.js
        /// </summary>
        /// <param name="cat">分类</param>
        /// <param name="limit">返回数量</param>
        /// <param name="before">游标(上一次返回的最后一个歌单的updateTime)</param>
        public async Task<(List<PlaylistInfo>, long, bool)> GetHighQualityPlaylistsAsync(
            string cat = "全部", int limit = 50, long before = 0)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[GetHighQualityPlaylists] cat={cat}, limit={limit}, before={before}");

                var payload = new Dictionary<string, object>
                {
                    { "cat", cat },
                    { "limit", limit },
                    { "lasttime", before },
                    { "total", true }
                };

                var response = await PostWeApiAsync<JObject>(
                    "/api/playlist/highquality/list",
                    payload,
                    autoConvertApiSegment: true);

                if (response["code"]?.Value<int>() != 200)
                {
                    System.Diagnostics.Debug.WriteLine($"[API] 获取精品歌单失败: {response["message"]}");
                    return (new List<PlaylistInfo>(), 0, false);
                }

                var playlists = response["playlists"] as JArray;
                var more = response["more"]?.Value<bool>() ?? false;
                var lasttime = response["lasttime"]?.Value<long>() ?? 0;

                var result = new List<PlaylistInfo>();
                if (playlists != null)
                {
                    foreach (var item in playlists)
                    {
                        var playlist = ParsePlaylistDetail(item as JObject);
                        if (playlist != null)
                        {
                            result.Add(playlist);
                        }
                    }
                }

                System.Diagnostics.Debug.WriteLine($"[API] 成功获取 {result.Count} 个精品歌单, more={more}");
                return (result, lasttime, more);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 获取精品歌单异常: {ex.Message}");
                return (new List<PlaylistInfo>(), 0, false);
            }
        }

        /// <summary>
        /// 获取新歌速递
        /// 参考: NeteaseCloudMusicApi/module/top_song.js
        /// </summary>
        /// <param name="areaType">地区: 0=全部, 7=华语, 96=欧美, 8=日本, 16=韩国</param>
        public async Task<List<SongInfo>> GetNewSongsAsync(int areaType = 0)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[GetNewSongs] areaType={areaType}");

                var payload = new Dictionary<string, object>
                {
                    { "areaId", areaType },
                    { "total", true }
                };

                var response = await PostWeApiAsync<JObject>("/v1/discovery/new/songs", payload);

                if (response["code"]?.Value<int>() != 200)
                {
                    System.Diagnostics.Debug.WriteLine($"[API] 获取新歌速递失败: {response["message"]}");
                    return new List<SongInfo>();
                }

                var data = response["data"] as JArray;
                if (data == null)
                {
                    System.Diagnostics.Debug.WriteLine("[API] 新歌速递数据为空");
                    return new List<SongInfo>();
                }

                var songs = ParseSongList(data);
                System.Diagnostics.Debug.WriteLine($"[API] 成功获取 {songs.Count} 首新歌");
                return songs;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 获取新歌速递异常: {ex.Message}");
                return new List<SongInfo>();
            }
        }

        /// <summary>
        /// 获取最近播放的歌单
        /// 参考: NeteaseCloudMusicApi/module/record_recent_playlist.js
        /// </summary>
        public async Task<List<PlaylistInfo>> GetRecentPlaylistsAsync(int limit = 100)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[GetRecentPlaylists] limit={limit}");

                var payload = new Dictionary<string, object>
                {
                    { "limit", limit }
                };

                var response = await PostWeApiAsync<JObject>(
                    "/api/play-record/playlist/list",
                    payload,
                    autoConvertApiSegment: true);

                if (response["code"]?.Value<int>() != 200)
                {
                    System.Diagnostics.Debug.WriteLine($"[API] 获取最近播放歌单失败: {response["message"]}");
                    return new List<PlaylistInfo>();
                }

                var list = response["data"]?["list"] as JArray;
                if (list == null)
                {
                    System.Diagnostics.Debug.WriteLine("[API] 最近播放歌单数据为空");
                    return new List<PlaylistInfo>();
                }

                var result = new List<PlaylistInfo>();
                foreach (var item in list)
                {
                    var playlistData = item["data"];
                    if (playlistData == null) continue;

                    var playlist = new PlaylistInfo
                    {
                        Id = playlistData["id"]?.Value<string>() ?? playlistData["id"]?.Value<long>().ToString(),
                        Name = playlistData["name"]?.Value<string>() ?? "未知歌单",
                        Creator = playlistData["creator"]?["nickname"]?.Value<string>() ?? "未知",
                        CreatorId = playlistData["creator"]?["userId"]?.Value<long>() ?? 0,
                        TrackCount = playlistData["trackCount"]?.Value<int>() ?? 0,
                        CoverUrl = playlistData["coverImgUrl"]?.Value<string>() ?? "",
                        Description = playlistData["description"]?.Value<string>() ?? ""
                    };

                    result.Add(playlist);
                }

                System.Diagnostics.Debug.WriteLine($"[API] 成功获取 {result.Count} 个最近播放歌单");
                return result;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 获取最近播放歌单异常: {ex.Message}");
                return new List<PlaylistInfo>();
            }
        }

        /// <summary>
        /// 获取最近播放的专辑
        /// 参考: NeteaseCloudMusicApi/module/record_recent_album.js
        /// </summary>
        public async Task<List<AlbumInfo>> GetRecentAlbumsAsync(int limit = 100)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[GetRecentAlbums] limit={limit}");

                var payload = new Dictionary<string, object>
                {
                    { "limit", limit }
                };

                var response = await PostWeApiAsync<JObject>(
                    "/api/play-record/album/list",
                    payload,
                    autoConvertApiSegment: true);

                if (response["code"]?.Value<int>() != 200)
                {
                    System.Diagnostics.Debug.WriteLine($"[API] 获取最近播放专辑失败: {response["message"]}");
                    return new List<AlbumInfo>();
                }

                var list = response["data"]?["list"] as JArray;
                if (list == null)
                {
                    System.Diagnostics.Debug.WriteLine("[API] 最近播放专辑数据为空");
                    return new List<AlbumInfo>();
                }

                var result = new List<AlbumInfo>();
                foreach (var item in list)
                {
                    var albumData = item["data"];
                    if (albumData == null) continue;

                    var album = new AlbumInfo
                    {
                        Id = albumData["id"]?.Value<string>() ?? albumData["id"]?.Value<long>().ToString(),
                        Name = albumData["name"]?.Value<string>() ?? "未知专辑",
                        Artist = albumData["artist"]?["name"]?.Value<string>() ?? "未知艺术家",
                        PicUrl = albumData["picUrl"]?.Value<string>() ?? "",
                        PublishTime = albumData["publishTime"]?.Value<long>().ToString() ?? ""
                    };

                    result.Add(album);
                }

                System.Diagnostics.Debug.WriteLine($"[API] 成功获取 {result.Count} 个最近播放专辑");
                return result;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 获取最近播放专辑异常: {ex.Message}");
                return new List<AlbumInfo>();
            }
        }

        /// <summary>
        /// 获取分类歌单
        /// 参考: NeteaseCloudMusicApi/module/top_playlist.js
        /// </summary>
        /// <param name="cat">分类名称</param>
        /// <param name="order">排序: hot=最热, new=最新</param>
        /// <param name="limit">每页数量</param>
        /// <param name="offset">偏移量</param>
        public async Task<(List<PlaylistInfo>, long, bool)> GetPlaylistsByCategoryAsync(
            string cat = "全部", string order = "hot", int limit = 50, int offset = 0)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[GetPlaylistsByCategory] cat={cat}, order={order}, limit={limit}, offset={offset}");

                var payload = new Dictionary<string, object>
                {
                    { "cat", cat },
                    { "order", order },
                    { "limit", limit },
                    { "offset", offset },
                    { "total", true }
                };

                var response = await PostWeApiAsync<JObject>("/playlist/list", payload);

                if (response["code"]?.Value<int>() != 200)
                {
                    System.Diagnostics.Debug.WriteLine($"[API] 获取分类歌单失败: {response["message"]}");
                    return (new List<PlaylistInfo>(), 0, false);
                }

                var playlists = response["playlists"] as JArray;
                var total = response["total"]?.Value<long>() ?? 0;
                var more = response["more"]?.Value<bool>() ?? false;

                var result = new List<PlaylistInfo>();
                if (playlists != null)
                {
                    foreach (var item in playlists)
                    {
                        var playlist = ParsePlaylistDetail(item as JObject);
                        if (playlist != null)
                        {
                            result.Add(playlist);
                        }
                    }
                }

                System.Diagnostics.Debug.WriteLine($"[API] 成功获取 {result.Count} 个分类歌单, total={total}, more={more}");
                return (result, total, more);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 获取分类歌单异常: {ex.Message}");
                return (new List<PlaylistInfo>(), 0, false);
            }
        }

        /// <summary>
        /// 获取新碟上架
        /// 参考: NeteaseCloudMusicApi/module/album_newest.js
        /// </summary>
        public async Task<List<AlbumInfo>> GetNewAlbumsAsync()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("[GetNewAlbums] 获取新碟上架");

                var payload = new Dictionary<string, object>();

                var response = await PostWeApiAsync<JObject>(
                    "/api/discovery/newAlbum",
                    payload,
                    autoConvertApiSegment: true);

                if (response["code"]?.Value<int>() != 200)
                {
                    System.Diagnostics.Debug.WriteLine($"[API] 获取新碟上架失败: {response["message"]}");
                    return new List<AlbumInfo>();
                }

                var albums = response["albums"] as JArray;
                if (albums == null)
                {
                    System.Diagnostics.Debug.WriteLine("[API] 新碟上架数据为空");
                    return new List<AlbumInfo>();
                }

                var result = new List<AlbumInfo>();
                foreach (var album in albums)
                {
                    var albumInfo = new AlbumInfo
                    {
                        Id = album["id"]?.Value<string>() ?? album["id"]?.Value<long>().ToString(),
                        Name = album["name"]?.Value<string>() ?? "未知专辑",
                        Artist = album["artist"]?["name"]?.Value<string>() ?? "未知艺术家",
                        PicUrl = album["picUrl"]?.Value<string>() ?? "",
                        PublishTime = album["publishTime"]?.Value<long>().ToString() ?? ""
                    };

                    result.Add(albumInfo);
                }

                System.Diagnostics.Debug.WriteLine($"[API] 成功获取 {result.Count} 个新碟");
                return result;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 获取新碟上架异常: {ex.Message}");
                return new List<AlbumInfo>();
            }
        }

        #endregion

        #region 评论相关

        /// <summary>
        /// 获取评论
        /// </summary>
        public async Task<CommentResult> GetCommentsAsync(string resourceId, CommentType type = CommentType.Song,
            int pageNo = 1, int pageSize = 20, CommentSortType sortType = CommentSortType.Hot)
        {
            int resourceType = (int)type;
            int sort = (int)sortType;

            var payload = new Dictionary<string, object>
            {
                { "rid", resourceId },
                { "threadId", $"R_SO_4_{resourceId}" },
                { "pageNo", pageNo },
                { "pageSize", pageSize },
                { "cursor", (pageNo - 1) * pageSize },
                { "sortType", sort }
            };

            var response = await PostWeApiAsync<JObject>("/comment/page", payload);
            return ParseComments(response);
        }

        #endregion

        #region 数据解析方法

        /// <summary>
        /// 解析歌手列表。
        /// </summary>
        private List<ArtistInfo> ParseArtistList(JArray artists)
        {
            var result = new List<ArtistInfo>();
            if (artists == null) return result;

            foreach (var artistToken in artists)
            {
                if (artistToken == null || artistToken.Type != JTokenType.Object)
                {
                    continue;
                }

                try
                {
                    var artistInfo = ParseArtistObject((JObject)artistToken);
                    if (artistInfo != null && artistInfo.Id > 0)
                    {
                        result.Add(artistInfo);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[API] 解析歌手失败: {ex.Message}");
                }
            }

            return result;
        }

        /// <summary>
        /// 解析歌手对象。
        /// </summary>
        private ArtistInfo? ParseArtistObject(JObject artistObject)
        {
            if (artistObject == null)
            {
                return null;
            }

            var artistInfo = new ArtistInfo
            {
                Id = artistObject["id"]?.Value<long>()
                     ?? artistObject["artistId"]?.Value<long>()
                     ?? artistObject["userId"]?.Value<long>()
                     ?? 0,
                Name = artistObject["name"]?.Value<string>()
                    ?? artistObject["artistName"]?.Value<string>()
                    ?? string.Empty,
                PicUrl = artistObject["picUrl"]?.Value<string>()
                    ?? artistObject["img1v1Url"]?.Value<string>()
                    ?? artistObject["avatar"]?.Value<string>()
                    ?? artistObject["cover"]?.Value<string>()
                    ?? string.Empty,
                AreaCode = artistObject["area"]?.Value<int?>()
                    ?? artistObject["areaCode"]?.Value<int?>()
                    ?? 0,
                TypeCode = artistObject["type"]?.Value<int?>()
                    ?? artistObject["artistType"]?.Value<int?>()
                    ?? 0,
                MusicCount = artistObject["musicSize"]?.Value<int?>()
                    ?? artistObject["musicCount"]?.Value<int?>()
                    ?? artistObject["songCount"]?.Value<int?>()
                    ?? 0,
                AlbumCount = artistObject["albumSize"]?.Value<int?>()
                    ?? artistObject["albumCount"]?.Value<int?>()
                    ?? 0,
                MvCount = artistObject["mvSize"]?.Value<int?>()
                    ?? artistObject["mvCount"]?.Value<int?>()
                    ?? 0,
                BriefDesc = artistObject["briefDesc"]?.Value<string>() ?? string.Empty,
                Description = artistObject["desc"]?.Value<string>() ?? string.Empty,
                IsSubscribed = artistObject["followed"]?.Value<bool?>()
                    ?? artistObject["follow"]?.Value<bool?>()
                    ?? false
            };

            if (string.IsNullOrWhiteSpace(artistInfo.PicUrl))
            {
                artistInfo.PicUrl = artistObject["avatarUrl"]?.Value<string>()
                    ?? artistObject["img1v1"]?.Value<string>()
                    ?? string.Empty;
            }

            var aliasArray = artistObject["alias"] as JArray;
            if (aliasArray != null && aliasArray.Count > 0)
            {
                var aliasList = aliasArray
                    .Select(a => a?.Value<string>())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();

                if (aliasList.Count > 0)
                {
                    artistInfo.Alias = string.Join("/", aliasList);
                }
            }

            if (string.IsNullOrWhiteSpace(artistInfo.Alias))
            {
                var translated = artistObject["trans"]?.Value<string>()
                    ?? artistObject["tns"]?.FirstOrDefault()?.Value<string>();
                if (!string.IsNullOrWhiteSpace(translated))
                {
                    artistInfo.Alias = translated;
                }
            }

            artistInfo.AreaName = ArtistMetadataHelper.ResolveAreaName(artistInfo.AreaCode);
            artistInfo.TypeName = ArtistMetadataHelper.ResolveTypeName(artistInfo.TypeCode);
            artistInfo.BriefDesc = NormalizeSummary(artistInfo.BriefDesc);
            artistInfo.Description = NormalizeDescription(artistInfo.Description, artistInfo.BriefDesc);

            return artistInfo;
        }

        /// <summary>
        /// 解析歌曲列表
        /// </summary>
        private List<SongInfo> ParseSongList(JArray songs)
        {
            var result = new List<SongInfo>();
            if (songs == null) return result;

            int successCount = 0;
            int failCount = 0;

            foreach (var songToken in songs)
            {
                try
                {
                    if (songToken == null || songToken.Type != JTokenType.Object)
                    {
                        failCount++;
                        System.Diagnostics.Debug.WriteLine($"[API] 跳过非对象类型的歌曲条目: 类型={songToken?.Type}");
                        continue;
                    }

                    var song = (JObject)songToken;

                    // 检查歌曲是否可用（参考网易云API，status=0表示正常，-200表示下架等）
                    var status = song["st"]?.Value<int>() ?? song["status"]?.Value<int>() ?? 0;
                    var id = song["id"]?.Value<string>();

                    // 跳过无效歌曲（下架、版权失效等）
                    if (status < 0)
                    {
                        System.Diagnostics.Debug.WriteLine($"[API] 跳过无效歌曲 ID={id}, status={status}");
                        failCount++;
                        continue;
                    }

                    // 跳过没有 ID 或名称的歌曲
                    var name = song["name"]?.Value<string>();
                    if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
                    {
                        System.Diagnostics.Debug.WriteLine($"[API] 跳过缺失字段的歌曲 ID={id}, Name={name}");
                        failCount++;
                        continue;
                    }

                    var albumToken = song["al"] as JObject ?? song["album"] as JObject;
                    string albumName = albumToken?["name"]?.Value<string>();
                    string albumId = albumToken?["id"]?.Value<string>();
                    string albumPic = albumToken?["picUrl"]?.Value<string>();

                    if (string.IsNullOrEmpty(albumName))
                    {
                        if (song["al"] != null && song["al"].Type == JTokenType.String)
                        {
                            albumName = song["al"].Value<string>();
                        }
                        else if (song["album"] != null && song["album"].Type == JTokenType.String)
                        {
                            albumName = song["album"].Value<string>();
                        }
                    }

                    var songInfo = new SongInfo
                    {
                        Id = id,
                        Name = name,
                        Duration = (song["dt"]?.Value<int>() ?? song["duration"]?.Value<int>() ?? 0) / 1000,
                        Album = albumName,
                        AlbumId = albumId,
                        PicUrl = albumPic
                    };

                    // 解析艺术家
                    var artists = song["ar"] as JArray ?? song["artists"] as JArray;
                    if (artists != null && artists.Count > 0)
                    {
                        var artistNames = new List<string>();
                        var artistIds = new List<long>();

                        foreach (var artistToken in artists)
                        {
                            if (artistToken == null || artistToken.Type != JTokenType.Object)
                            {
                                continue;
                            }

                            var artistObj = (JObject)artistToken;

                            var artistName = artistObj["name"]?.Value<string>();
                            if (!string.IsNullOrWhiteSpace(artistName))
                            {
                                artistNames.Add(artistName);
                            }

                            var artistIdValue = artistObj["id"]?.Value<long>() ?? 0;
                            if (artistIdValue > 0)
                            {
                                artistIds.Add(artistIdValue);
                            }
                        }

                        if (artistNames.Count > 0)
                        {
                            songInfo.ArtistNames = new List<string>(artistNames);
                            songInfo.Artist = string.Join("/", artistNames);
                        }

                        if (artistIds.Count > 0)
                        {
                            songInfo.ArtistIds = new List<long>(artistIds);
                        }
                    }

                    // 发布时间
                    var publishTime = song["publishTime"]?.Value<long>();
                    if (publishTime.HasValue)
                    {
                        songInfo.PublishTime = DateTimeOffset.FromUnixTimeMilliseconds(publishTime.Value)
                            .DateTime.ToString("yyyy-MM-dd");
                    }

                    result.Add(songInfo);
                    successCount++;
                }
                catch (Exception ex)
                {
                    failCount++;
                    System.Diagnostics.Debug.WriteLine($"[API] 解析歌曲失败: {ex.Message}");
                }
            }

            if (failCount > 0)
            {
                System.Diagnostics.Debug.WriteLine($"[API] 解析完成: 成功 {successCount} 首, 失败/跳过 {failCount} 首");
            }

            return result;
        }

        /// <summary>
        /// 解析歌单列表
        /// </summary>
        private List<PlaylistInfo> ParsePlaylistList(JArray playlists)
        {
            var result = new List<PlaylistInfo>();
            if (playlists == null)
            {
                return result;
            }

            foreach (var playlistToken in playlists)
            {
                if (playlistToken is not JObject playlistObject)
                {
                    continue;
                }

                try
                {
                    var playlistInfo = CreatePlaylistInfo(playlistObject);
                    PopulatePlaylistOwnershipDefaults(playlistInfo, playlistObject);
                    result.Add(playlistInfo);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[API] 解析歌单失败: {ex.Message}");
                }
            }

            return result;
        }

        private PlaylistInfo CreatePlaylistInfo(JObject playlistToken)
        {
            if (playlistToken == null)
            {
                return new PlaylistInfo();
            }

            var playlistInfo = new PlaylistInfo
            {
                Id = playlistToken["id"]?.Value<string>()
                    ?? playlistToken["playlistId"]?.Value<string>()
                    ?? playlistToken["resourceId"]?.Value<string>()
                    ?? string.Empty,
                Name = playlistToken["name"]?.Value<string>()
                    ?? playlistToken["title"]?.Value<string>()
                    ?? string.Empty,
                CoverUrl = playlistToken["coverImgUrl"]?.Value<string>()
                    ?? playlistToken["coverUrl"]?.Value<string>()
                    ?? playlistToken["picUrl"]?.Value<string>()
                    ?? string.Empty,
                Description = playlistToken["description"]?.Value<string>()
                    ?? playlistToken["desc"]?.Value<string>()
                    ?? string.Empty,
                TrackCount = ResolveTrackCount(playlistToken),
                Creator = playlistToken["creator"]?["nickname"]?.Value<string>()
                    ?? playlistToken["creatorName"]?.Value<string>()
                    ?? string.Empty,
                CreatorId = playlistToken["creator"]?["userId"]?.Value<long?>() ?? 0,
                OwnerUserId = playlistToken["userId"]?.Value<long?>()
                    ?? playlistToken["ownerId"]?.Value<long?>()
                    ?? 0
            };

            return playlistInfo;
        }

        private static int ResolveTrackCount(JObject playlistToken)
        {
            if (playlistToken == null)
            {
                return 0;
            }

            int count =
                SafeToInt(playlistToken["trackCount"]) ??
                SafeToInt(playlistToken["songCount"]) ??
                SafeToInt(playlistToken["size"]) ??
                SafeToInt(playlistToken["trackNumber"]) ??
                0;

            if (count > 0)
            {
                return count;
            }

            var trackIds = playlistToken["trackIds"] as JArray;
            if (trackIds != null && trackIds.Count > 0)
            {
                return trackIds.Count;
            }

            var tracks = playlistToken["tracks"] as JArray;
            if (tracks != null && tracks.Count > 0)
            {
                return tracks.Count;
            }

            return 0;
        }

        private static int? SafeToInt(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            try
            {
                switch (token.Type)
                {
                    case JTokenType.Integer:
                        var integerValue = token.Value<long>();
                        if (integerValue < 0)
                        {
                            return 0;
                        }
                        if (integerValue > int.MaxValue)
                        {
                            return int.MaxValue;
                        }
                        return (int)integerValue;

                    case JTokenType.Float:
                        var floatValue = token.Value<double>();
                        if (double.IsNaN(floatValue))
                        {
                            return null;
                        }
                        if (floatValue < 0)
                        {
                            return 0;
                        }
                        if (floatValue > int.MaxValue)
                        {
                            return int.MaxValue;
                        }
                        return (int)Math.Round(floatValue);

                    case JTokenType.String:
                        var stringValue = token.Value<string>();
                        if (string.IsNullOrWhiteSpace(stringValue))
                        {
                            return null;
                        }

                        if (int.TryParse(stringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt))
                        {
                            return Math.Max(0, parsedInt);
                        }

                        if (long.TryParse(stringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLong))
                        {
                            if (parsedLong < 0)
                            {
                                return 0;
                            }

                            return parsedLong > int.MaxValue ? int.MaxValue : (int)parsedLong;
                        }

                        break;
                }
            }
            catch
            {
                // ignore parsing exceptions, fall back to null
            }

            return null;
        }

        private void PopulatePlaylistOwnershipDefaults(PlaylistInfo playlistInfo, JObject? playlistToken)
        {
            if (playlistInfo == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(playlistInfo.Id))
            {
                var idToken = playlistToken?["id"] ?? playlistToken?["playlistId"] ?? playlistToken?["resourceId"];
                if (idToken != null)
                {
                    playlistInfo.Id = idToken.ToString();
                }
            }

            long tokenCreatorId = playlistToken?["creator"]?["userId"]?.Value<long?>() ?? 0;
            long tokenOwnerId = playlistToken?["userId"]?.Value<long?>()
                ?? playlistToken?["ownerId"]?.Value<long?>()
                ?? 0;

            long currentUserId = GetCurrentUserId();

            if (playlistInfo.CreatorId == 0)
            {
                playlistInfo.CreatorId = tokenCreatorId != 0 ? tokenCreatorId : currentUserId;
            }

            if (playlistInfo.OwnerUserId == 0)
            {
                playlistInfo.OwnerUserId = tokenOwnerId != 0 ? tokenOwnerId : playlistInfo.CreatorId;
            }

            if (string.IsNullOrWhiteSpace(playlistInfo.Creator))
            {
                var creatorName = playlistToken?["creator"]?["nickname"]?.Value<string>()
                    ?? playlistToken?["creatorName"]?.Value<string>();

                if (!string.IsNullOrWhiteSpace(creatorName))
                {
                    playlistInfo.Creator = creatorName;
                }
                else
                {
                    var accountState = _authContext?.CurrentAccountState;
                    if (accountState != null &&
                        playlistInfo.CreatorId != 0 &&
                        long.TryParse(accountState.UserId, out var userId) &&
                        userId == playlistInfo.CreatorId &&
                        !string.IsNullOrWhiteSpace(accountState.Nickname))
                    {
                        playlistInfo.Creator = accountState.Nickname;
                    }
                }
            }

            if (playlistInfo.TrackCount < 0)
            {
                playlistInfo.TrackCount = 0;
            }
        }

        /// <summary>
        /// 解析专辑列表
        /// </summary>
        private List<AlbumInfo> ParseAlbumList(JArray albums)
        {
            var result = new List<AlbumInfo>();
            if (albums == null) return result;

            foreach (var album in albums)
            {
                try
                {
                    var albumInfo = new AlbumInfo
                    {
                        Id = album["id"]?.Value<string>(),
                        Name = album["name"]?.Value<string>(),
                        PicUrl = album["picUrl"]?.Value<string>(),
                        Artist = album["artist"]?["name"]?.Value<string>(),
                        TrackCount = album["size"]?.Value<int>() ?? 0
                    };

                    var publishTime = album["publishTime"]?.Value<long>();
                    if (publishTime.HasValue)
                    {
                        albumInfo.PublishTime = DateTimeOffset.FromUnixTimeMilliseconds(publishTime.Value)
                            .DateTime.ToString("yyyy-MM-dd");
                    }

                    result.Add(albumInfo);
                }
                catch { }
            }

            return result;
        }

        /// <summary>
        /// 解析歌手介绍段落。
        /// </summary>
        private List<ArtistIntroductionSection> ParseArtistIntroductionSections(JArray introductionArray)
        {
            var sections = new List<ArtistIntroductionSection>();
            if (introductionArray == null)
            {
                return sections;
            }

            foreach (var item in introductionArray)
            {
                if (item is not JObject section)
                {
                    continue;
                }

                var title = section["ti"]?.Value<string>() ?? string.Empty;
                var content = section["txt"]?.Value<string>() ?? string.Empty;

                if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(content))
                {
                    continue;
                }

                sections.Add(new ArtistIntroductionSection
                {
                    Title = title,
                    Content = content.Replace("\r\n", "\n").Trim()
                });
            }

            return sections;
        }

        private static string BuildIntroductionSummary(IEnumerable<ArtistIntroductionSection> sections, int maxLength = 320)
        {
            if (sections == null)
            {
                return string.Empty;
            }

            var printable = sections
                .Select(section =>
                {
                    if (string.IsNullOrWhiteSpace(section.Content))
                    {
                        return string.Empty;
                    }

                    if (string.IsNullOrWhiteSpace(section.Title))
                    {
                        return section.Content;
                    }

                    return $"{section.Title}\n{section.Content}";
                })
                .Where(s => !string.IsNullOrWhiteSpace(s));

            string combined = string.Join("\n\n", printable);
            if (string.IsNullOrWhiteSpace(combined))
            {
                return string.Empty;
            }

            combined = Regex.Replace(combined.Trim(), "\\s+", " ");
            return TrimToLength(combined, maxLength);
        }

        private static string NormalizeSummary(string? source, int maxLength = 140)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return string.Empty;
            }

            string condensed = Regex.Replace(source.Trim(), "\\s+", " ");
            return TrimToLength(condensed, maxLength);
        }

        private static string NormalizeDescription(string? description, string? fallback = null, int maxLength = 240)
        {
            string baseText = !string.IsNullOrWhiteSpace(description) ? description : fallback ?? string.Empty;
            if (string.IsNullOrWhiteSpace(baseText))
            {
                return string.Empty;
            }

            string condensed = Regex.Replace(baseText.Trim(), "\\s+", " ");
            return TrimToLength(condensed, maxLength);
        }

        private static string TrimToLength(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || maxLength <= 0)
            {
                return string.Empty;
            }

            if (value.Length <= maxLength)
            {
                return value;
            }

            string truncated = value.Substring(0, Math.Min(maxLength, value.Length)).TrimEnd();
            return truncated.Length < value.Length ? truncated + "…" : truncated;
        }

        /// <summary>
        /// 解析歌单详情
        /// </summary>
        private PlaylistInfo ParsePlaylistDetail(JObject playlist)
        {
            if (playlist == null)
            {
                return new PlaylistInfo();
            }

            var playlistInfo = CreatePlaylistInfo(playlist);
            PopulatePlaylistOwnershipDefaults(playlistInfo, playlist);

            var tracks = playlist["tracks"] as JArray;
            if (tracks != null && tracks.Count > 0)
            {
                var songs = ParseSongList(tracks);
                playlistInfo.Songs = songs;
                playlistInfo.TrackCount = Math.Max(playlistInfo.TrackCount, songs?.Count ?? 0);
            }
            else
            {
                var trackIds = playlist["trackIds"] as JArray;
                if (trackIds != null && trackIds.Count > 0 && playlistInfo.TrackCount <= 0)
                {
                    playlistInfo.TrackCount = Math.Max(playlistInfo.TrackCount, trackIds.Count);
                }
            }

            return playlistInfo;
        }

        private long GetCurrentUserId()
        {
            var accountState = _authContext?.CurrentAccountState;
            if (accountState == null)
            {
                return 0;
            }

            if (!string.IsNullOrWhiteSpace(accountState.UserId) &&
                long.TryParse(accountState.UserId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }

            if (accountState.AccountDetail?.UserId > 0)
            {
                return accountState.AccountDetail.UserId;
            }

            return 0;
        }

        private static string? ExtractPlaylistId(JObject? response)
        {
            if (response == null)
            {
                return null;
            }

            string? id =
                response["id"]?.ToString() ??
                response["playlistId"]?.ToString() ??
                response["resourceId"]?.ToString() ??
                response["data"]?["id"]?.ToString() ??
                response["result"]?["playlistId"]?.ToString() ??
                response["playlist"]?["id"]?.ToString();

            return string.IsNullOrWhiteSpace(id) ? null : id;
        }

        /// <summary>
        /// 解析歌词
        /// </summary>
        private LyricInfo ParseLyric(JObject lyricData)
        {
            if (lyricData == null) return null;

            return new LyricInfo
            {
                Lyric = lyricData["lrc"]?["lyric"]?.Value<string>(),
                TLyric = lyricData["tlyric"]?["lyric"]?.Value<string>(),
                RomaLyric = lyricData["romalrc"]?["lyric"]?.Value<string>(),
                YrcLyric = lyricData["yrc"]?["lyric"]?.Value<string>()
            };
        }

        private static bool HasLyricContent(LyricInfo lyric)
        {
            if (lyric == null)
            {
                return false;
            }

            return !string.IsNullOrWhiteSpace(lyric.Lyric) ||
                   !string.IsNullOrWhiteSpace(lyric.TLyric) ||
                   !string.IsNullOrWhiteSpace(lyric.RomaLyric) ||
                   !string.IsNullOrWhiteSpace(lyric.YrcLyric);
        }

        /// <summary>
        /// 解析评论
        /// </summary>
        private CommentResult ParseComments(JObject commentData)
        {
            var result = new CommentResult
            {
                TotalCount = commentData["data"]?["totalCount"]?.Value<int>() ?? 0,
                Comments = new List<CommentInfo>()
            };

            var comments = commentData["data"]?["comments"] as JArray;
            if (comments != null)
            {
                foreach (var comment in comments)
                {
                    try
                    {
                        var commentInfo = new CommentInfo
                        {
                            CommentId = comment["commentId"]?.Value<string>(),
                            UserId = comment["user"]?["userId"]?.Value<string>(),
                            UserName = comment["user"]?["nickname"]?.Value<string>(),
                            AvatarUrl = comment["user"]?["avatarUrl"]?.Value<string>(),
                            Content = comment["content"]?.Value<string>(),
                            LikedCount = comment["likedCount"]?.Value<int>() ?? 0,
                            Liked = comment["liked"]?.Value<bool>() ?? false,
                            IpLocation = comment["ipLocation"]?["location"]?.Value<string>()
                        };

                        var timeValue = comment["time"]?.Value<long>();
                        if (timeValue.HasValue)
                        {
                            commentInfo.Time = DateTimeOffset.FromUnixTimeMilliseconds(timeValue.Value).DateTime;
                        }

                        // 被回复的评论
                        var beReplied = comment["beReplied"] as JArray;
                        if (beReplied != null && beReplied.Count > 0)
                        {
                            commentInfo.BeRepliedId = beReplied[0]["beRepliedCommentId"]?.Value<string>();
                            commentInfo.BeRepliedUserName = beReplied[0]["user"]?["nickname"]?.Value<string>();
                        }

                        result.Comments.Add(commentInfo);
                    }
                    catch { }
                }
            }

            return result;
        }

        #endregion

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

        #region IDisposable

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _httpClient?.Dispose();
                    _simplifiedClient?.Dispose();
                    _eapiClient?.Dispose();
                    _iOSLoginClient?.Dispose();
                }
                _disposed = true;
            }
        }

        #endregion
    }

    #region 枚举定义

    /// <summary>
    /// 音质级别
    /// </summary>
    public enum QualityLevel
    {
        /// <summary>标准</summary>
        Standard,
        /// <summary>极高</summary>
        High,
        /// <summary>无损</summary>
        Lossless,
        /// <summary>Hi-Res</summary>
        HiRes,
        /// <summary>高清环绕声</summary>
        SurroundHD,
        /// <summary>沉浸环绕声</summary>
        Dolby,
        /// <summary>超清母带</summary>
        Master
    }

    /// <summary>
    /// 评论资源类型
    /// </summary>
    public enum CommentType
    {
        /// <summary>歌曲</summary>
        Song = 0,
        /// <summary>MV</summary>
        MV = 1,
        /// <summary>歌单</summary>
        Playlist = 2,
        /// <summary>专辑</summary>
        Album = 3,
        /// <summary>电台</summary>
        DJRadio = 4,
        /// <summary>视频</summary>
        Video = 5
    }

    /// <summary>
    /// 评论排序类型
    /// </summary>
    public enum CommentSortType
    {
        /// <summary>热度</summary>
        Hot = 1,
        /// <summary>时间</summary>
        Time = 2
    }

    #endregion

    #region 辅助类

    /// <summary>
    /// 登录结果
    /// </summary>
    public class LoginResult
    {
        public int Code { get; set; }
        public string? Message { get; set; }
        public string? Cookie { get; set; }
        public string? UserId { get; set; }
        public string? Nickname { get; set; }
        public int VipType { get; set; }
        public string? AvatarUrl { get; set; }
    }

    /// <summary>
    /// 用户信息
    /// </summary>
    public class UserInfo
    {
        public string? UserId { get; set; }
        public string? Nickname { get; set; }
        public int VipType { get; set; }
        public string? AvatarUrl { get; set; }
    }

    /// <summary>
    /// 歌曲URL信息
    /// </summary>
    public class SongUrlInfo
    {
        public string? Id { get; set; }
        public string? Url { get; set; }
        public string? Level { get; set; }
        public long Size { get; set; }
        public int Br { get; set; }
        public string? Type { get; set; }
        public string? Md5 { get; set; }

        /// <summary>
        /// 费用类型（0=免费, 1=VIP, 8=付费专辑）
        /// </summary>
        public int Fee { get; set; }

        /// <summary>
        /// 试听信息（非VIP用户会员歌曲时存在）
        /// </summary>
        public FreeTrialInfo? FreeTrialInfo { get; set; }
    }

    /// <summary>
    /// 试听信息
    /// </summary>
    public class FreeTrialInfo
    {
        /// <summary>
        /// 试听片段开始时间（毫秒）
        /// </summary>
        public long Start { get; set; }

        /// <summary>
        /// 试听片段结束时间（毫秒）
        /// </summary>
        public long End { get; set; }
    }

    /// <summary>
    /// 专辑信息
    /// </summary>
    public class AlbumInfo
    {
        public string? Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string PicUrl { get; set; } = string.Empty;
        public string PublishTime { get; set; } = string.Empty;
        public int TrackCount { get; set; }
    }

    /// <summary>
    /// 歌词信息
    /// </summary>
    public class LyricInfo
    {
        /// <summary>原文歌词</summary>
        public string Lyric { get; set; } = string.Empty;
        /// <summary>翻译歌词</summary>
        public string TLyric { get; set; } = string.Empty;
        /// <summary>罗马音歌词</summary>
        public string RomaLyric { get; set; } = string.Empty;
        /// <summary>逐字歌词（yrc格式，包含每个字的时间信息）</summary>
        public string YrcLyric { get; set; } = string.Empty;
    }

    /// <summary>
    /// 评论结果
    /// </summary>
    public class CommentResult
    {
        public int TotalCount { get; set; }
        public List<CommentInfo> Comments { get; set; } = new List<CommentInfo>();
    }

    #endregion
}

#pragma warning restore CS8600, CS8601, CS8602, CS8603, CS8604, CS8625






















