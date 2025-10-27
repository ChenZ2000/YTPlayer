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
using YTPlayer.Core.Auth;
using YTPlayer.Models;
using YTPlayer.Utils;
using YTPlayer.Models.Auth;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using BrotliSharpLib;
using System.Reflection;

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
        private const int MAX_RETRY_COUNT = 3;
        private const int RETRY_DELAY_MS = 1000;  // 保留作为 fallback
        private const int MIN_RETRY_DELAY_MS = 50;   // 最小延迟
        private const int MAX_RETRY_DELAY_MS = 500;  // 最大延迟

        #endregion

        #region 字段和属性

        private readonly HttpClient _httpClient;
        private readonly HttpClient _simplifiedClient;
        private readonly HttpClient _eapiClient;  // 专用于EAPI请求，不使用CookieContainer
        private readonly CookieContainer _cookieContainer;
        private readonly object _cookieLock = new object();
        private readonly ConfigManager _configManager;
        private readonly ConfigModel _config;
        private readonly AuthContext _authContext;
        private string _musicU;
        private string _csrfToken;
        private bool _disposed;
        private readonly Random _random = new Random();
        private readonly string _deviceId;
        private readonly string _desktopUserAgent;

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
        public string MusicU
        {
            get => _musicU;
            set
            {
                _musicU = value;
                if (_config != null)
                {
                    _config.MusicU = value;
                }
                UpdateCookies();
            }
        }

        /// <summary>
        /// CSRF Token
        /// </summary>
        public string CsrfToken
        {
            get => _csrfToken;
            set
            {
                _csrfToken = value;
                if (_config != null)
                {
                    _config.CsrfToken = value;
                }
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

            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                config.DeviceId = deviceId;
            }

            if (!string.IsNullOrWhiteSpace(musicU))
            {
                config.MusicU = musicU;
            }

            if (!string.IsNullOrWhiteSpace(csrfToken))
            {
                config.CsrfToken = csrfToken;
            }

            return config;
        }

        public NeteaseApiClient(ConfigModel config = null)
        {
            _configManager = ConfigManager.Instance;
            _config = config ?? _configManager.Load();
            _authContext = new AuthContext(_configManager, _config);

            _deviceId = _authContext.Config.DeviceId;
            _desktopUserAgent = _authContext?.Config?.DesktopUserAgent ?? AuthConstants.DesktopUserAgent;

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

            SetupDefaultHeaders();

            _musicU = _config?.MusicU;
            _csrfToken = _config?.CsrfToken;

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
                    if (_config != null)
                    {
                        _config.MusicU = persistedState.MusicU;
                    }
                }

                if (!string.IsNullOrEmpty(persistedState.CsrfToken))
                {
                    _csrfToken = persistedState.CsrfToken;
                    if (_config != null)
                    {
                        _config.CsrfToken = persistedState.CsrfToken;
                    }
                }
            }

            _authContext.GetActiveAntiCheatToken();
            ApplyBaseCookies(includeAnonymousToken: string.IsNullOrEmpty(_musicU));
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
        /// </summary>
        private void UpdateCookies()
        {
            if (_disposed)
            {
                return;
            }

            ApplyBaseCookies(includeAnonymousToken: string.IsNullOrEmpty(_musicU));

            if (!string.IsNullOrEmpty(_musicU))
            {
                UpsertCookie("MUSIC_U", _musicU);
                if (string.IsNullOrEmpty(_csrfToken) && _musicU.Length > 10)
                {
                    _csrfToken = EncryptionHelper.ComputeMd5(_musicU).Substring(0, Math.Min(32, _musicU.Length));
                }

                if (_config != null)
                {
                    _config.MusicU = _musicU;
                }
            }
            else if (!string.IsNullOrEmpty(_config?.MusicA))
            {
                UpsertCookie("MUSIC_A", _config.MusicA);
            }

            var csrfValue = !string.IsNullOrEmpty(_csrfToken) ? _csrfToken : _config?.CsrfToken;
            if (!string.IsNullOrEmpty(csrfValue))
            {
                UpsertCookie("__csrf", csrfValue);
                _config.CsrfToken = csrfValue;
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
                        if (_config != null)
                        {
                            _config.MusicA = value;
                        }
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
        /// </summary>
        public void ClearCookies()
        {
            try
            {
                var field = typeof(CookieContainer).GetField("m_domainTable", BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null)
                {
                    if (field.GetValue(_cookieContainer) is Hashtable table)
                    {
                        table.Clear();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[COOKIE] 清空Cookie失败: {ex.Message}");
            }

            _musicU = null;
            _csrfToken = null;
            if (_config != null)
            {
                _config.MusicU = null;
                _config.CsrfToken = null;
            }
            UpdateCookies();

            _authContext?.ClearLoginProfile();
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
                        if (_config != null)
                        {
                            _config.MusicU = music.Value;
                        }
                    }

                    var csrf = cookies["__csrf"];
                    if (csrf != null && !string.IsNullOrEmpty(csrf.Value))
                    {
                        _csrfToken = csrf.Value;
                        if (_config != null)
                        {
                            _config.CsrfToken = csrf.Value;
                        }
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
        public async Task<T> PostWeApiAsync<T>(string path, object payload, int retryCount = 0, bool skipErrorHandling = false)
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

                // 构造URL（Python源码：7583-7593行）
                string url = $"{OFFICIAL_API_BASE}/weapi{path}";

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
                var cookies = _cookieContainer.GetCookies(new Uri(OFFICIAL_API_BASE));
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
                var response = await _httpClient.PostAsync(url, content);

                // 读取响应（二进制 -> 自动探测编码解码）
                byte[] rawBytes = await response.Content.ReadAsByteArrayAsync();
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
                // ⭐ 使用自适应延迟策略（参考 netease-music-simple-player）
                int delayMs = GetAdaptiveRetryDelay(retryCount + 1);
                await Task.Delay(delayMs);
                return await PostWeApiAsync<T>(path, payload, retryCount + 1, skipErrorHandling);
            }
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

                    byte[] rawBytes = await response.Content.ReadAsByteArrayAsync();
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
                // ⭐ 使用自适应延迟策略（参考 netease-music-simple-player）
                int delayMs = GetAdaptiveRetryDelay(retryCount + 1);
                await Task.Delay(delayMs);
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
            var authConfig = _authContext?.Config;
            var header = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["osver"] = authConfig?.DeviceOsVersion ?? "13.0",
                ["deviceId"] = _deviceId ?? authConfig?.DeviceId ?? EncryptionHelper.GenerateDeviceId(),
                ["appver"] = authConfig?.DeviceAppVersion ?? "8.10.90",
                ["versioncode"] = authConfig?.DeviceVersionCode ?? "8010090",
                ["mobilename"] = authConfig?.DeviceMobileName ?? "Xiaomi 2211133C",
                ["buildver"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
                ["resolution"] = authConfig?.DeviceResolution ?? "1080x2400",
                ["__csrf"] = _csrfToken ?? authConfig?.CsrfToken ?? string.Empty,
                ["os"] = authConfig?.DeviceOs ?? "android",
                ["channel"] = authConfig?.DeviceChannel ?? "xiaomi",
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
            else if (_authContext?.Config?.MusicA != null)
            {
                cookieMap["MUSIC_A"] = _authContext.Config.MusicA;
            }

            if (!string.IsNullOrEmpty(_csrfToken))
            {
                cookieMap["__csrf"] = _csrfToken;
            }
            else if (_authContext?.Config?.CsrfToken != null && !cookieMap.ContainsKey("__csrf"))
            {
                cookieMap["__csrf"] = _authContext.Config.CsrfToken;
            }

            return cookieMap.Count == 0
                ? string.Empty
                : string.Join("; ", cookieMap.Select(kvp => $"{kvp.Key}={Uri.EscapeDataString(kvp.Value)}"));
        }

        /// <summary>
        /// 简化API GET 请求（降级策略）
        /// </summary>
        private async Task<T> GetSimplifiedApiAsync<T>(string endpoint, Dictionary<string, string> parameters = null)
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

                string responseText = await response.Content.ReadAsStringAsync();
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
            var result = await PostWeApiAsync<JObject>("/login/qrcode/unikey", payload);

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
                result = await PostWeApiAsync<JObject>("/login/qrcode/client/login", payload, retryCount: 0, skipErrorHandling: true);
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
        /// 退出登录。
        /// </summary>
        public async Task LogoutAsync()
        {
            try
            {
                await PostWeApiAsync<JObject>("/logout", new Dictionary<string, object>());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Auth] LogoutAsync 调用失败: {ex.Message}");
            }
            finally
            {
                ClearCookies();
            }
        }

        /// <summary>
        /// 发送短信验证码（手机号登录）
        /// </summary>
        public async Task<bool> SendCaptchaAsync(string phone, string ctcode = "86")
        {
            var payload = new Dictionary<string, object>
            {
                { "cellphone", phone },  // 使用cellphone而不是phone
                { "ctcode", ctcode }
            };

            System.Diagnostics.Debug.WriteLine($"[SMS] 发送验证码请求: phone={phone}, ctcode={ctcode}");

            // ⭐ 修复：使用正确的 API endpoint（参考 NeteaseCloudMusicApi/module/captcha_sent.js line 8）
            var result = await PostWeApiAsync<JObject>("/sms/captcha/sent", payload);

            int code = result["code"]?.Value<int>() ?? -1;
            string message = result["message"]?.Value<string>() ?? result["msg"]?.Value<string>() ?? "未知错误";

            System.Diagnostics.Debug.WriteLine($"[SMS] 发送验证码结果: code={code}, msg={message}");
            System.Diagnostics.Debug.WriteLine($"[SMS] 完整响应: {result.ToString(Newtonsoft.Json.Formatting.Indented)}");

            if (code != 200)
            {
                // ⭐ 修复：抛出异常而不是返回 false
                throw new Exception($"发送验证码失败: {message} (code={code})");
            }

            return true;
        }

        /// <summary>
        /// 验证短信验证码并登录
        /// </summary>
        public async Task<LoginResult> LoginByCaptchaAsync(string phone, string captcha, string ctcode = "86")
        {
            // ⭐ 参考 NeteaseCloudMusicApi/module/login_cellphone.js
            var payload = new Dictionary<string, object>
            {
                { "phone", phone },
                { "captcha", captcha },
                { "countrycode", ctcode },  // 使用countrycode
                { "rememberLogin", "true" }
            };

            System.Diagnostics.Debug.WriteLine($"[LOGIN] 短信登录请求: phone={phone}, captcha={captcha}, countrycode={ctcode}");

            var result = await PostWeApiAsync<JObject>("/login/cellphone", payload);

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

                        string returnedLevel = item["level"]?.Value<string>();
                        if (!string.IsNullOrEmpty(returnedLevel) && !returnedLevel.Equals(currentLevel, StringComparison.OrdinalIgnoreCase))
                        {
                            int requestedIdx = Array.IndexOf(qualityOrder, currentLevel);
                            int returnedIdx = Array.IndexOf(qualityOrder, returnedLevel);
                            if (returnedIdx > requestedIdx)
                            {
                                System.Diagnostics.Debug.WriteLine($"[EAPI] ⚠️ 服务器降级: 请求={currentLevel}, 返回={returnedLevel}, 自动尝试下一个音质");
                                fallbackToLowerQuality = true;
                                break;
                            }
                        }

                        result[id] = new SongUrlInfo
                        {
                            Id = id,
                            Url = url,
                            Level = returnedLevel ?? currentLevel,
                            Size = item["size"]?.Value<long>() ?? 0,
                            Br = item["br"]?.Value<int>() ?? 0,
                            Type = item["type"]?.Value<string>(),
                            Md5 = item["md5"]?.Value<string>()
                        };

                        System.Diagnostics.Debug.WriteLine($"[EAPI] ✓ 歌曲{id}: level={result[id].Level}, br={result[id].Br}, URL={url.Substring(0, Math.Min(50, url.Length))}...");
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
                    string responseText = await response.Content.ReadAsStringAsync();

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
                response = await PostWeApiAsync<JObject>("/song/enhance/player/url", payload, retryCount: 0, skipErrorHandling: true)
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
                System.Diagnostics.Debug.WriteLine($"[StreamCheck] 📦 批次 {batchNumber}: 并发检查 {batch.Length} 首歌曲...");

                try
                {
                    // 🚀 关键优化：并发发送所有请求，每个完成后立即回调
                    var tasks = batch.Select(async songId =>
                    {
                        try
                        {
                            // 调用单曲检查
                            var singleResult = await CheckSingleBatchAvailabilityAsync(new[] { songId }, quality).ConfigureAwait(false);

                            if (cancellationToken.IsCancellationRequested)
                            {
                                return;
                            }

                            // 立即回调填入结果
                            if (singleResult.TryGetValue(songId, out bool isAvailable))
                            {
                                onSongChecked(songId, isAvailable);
                                System.Diagnostics.Debug.WriteLine($"[StreamCheck] ⚡ 实时填入: {songId}, 可用={isAvailable}");
                            }
                            else
                            {
                                // 检查失败，默认可用
                                onSongChecked(songId, true);
                                System.Diagnostics.Debug.WriteLine($"[StreamCheck] ⚡ 实时填入（默认）: {songId}, 可用=True");
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[StreamCheck] 单曲检查异常: {songId}, {ex.Message}");
                            // 异常时默认可用
                            onSongChecked(songId, true);
                        }
                    }).ToArray();

                    // 等待当前批次所有请求完成（但每个完成时已经回调了）
                    await Task.WhenAll(tasks).ConfigureAwait(false);

                    System.Diagnostics.Debug.WriteLine($"[StreamCheck] ✅ 批次 {batchNumber} 完成");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[StreamCheck] 批次 {batchNumber} 失败: {ex.Message}");
                }
            }

            System.Diagnostics.Debug.WriteLine($"[StreamCheck] 🎉 流式检查全部完成");
        }

        /// <summary>
        /// 检查单批歌曲的可用性
        /// </summary>
        private async Task<Dictionary<string, bool>> CheckSingleBatchAvailabilityAsync(string[] ids, QualityLevel quality)
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

            var payload = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["ids"] = JsonConvert.SerializeObject(numericIds),
                ["br"] = GetBitrateForQualityLevel(quality)
            };

            JObject response;
            try
            {
                response = await PostWeApiAsync<JObject>("/song/enhance/player/url", payload, retryCount: 0, skipErrorHandling: true)
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
        public async Task<List<SongInfo>> GetPlaylistSongsAsync(string playlistId)
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
                var infoResponse = await PostWeApiAsync<JObject>("/v3/playlist/detail", infoData);

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
                var allSongs = await GetSongsByIdsAsync(allIds);

                System.Diagnostics.Debug.WriteLine($"[API] 歌单歌曲获取完成，共 {allSongs.Count}/{total} 首");
                return allSongs;
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
        private async Task<List<SongInfo>> GetSongsByIdsAsync(List<string> ids)
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
                    await Task.Delay(delayMs);
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
                        var response = await PostWeApiAsync<JObject>("/song/detail", data);
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
                    catch (Exception ex)
                    {
                        retryCount++;
                        System.Diagnostics.Debug.WriteLine($"[API ERROR] 第 {batchNum} 批获取失败（重试 {retryCount}/3）: {ex.Message}");

                        if (retryCount < 3)
                        {
                            // 重试前等待更长时间
                            int retryDelay = 2000 * retryCount;
                            System.Diagnostics.Debug.WriteLine($"[API] 等待 {retryDelay}ms 后重试...");
                            await Task.Delay(retryDelay);
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
                var jsonString = await response.Content.ReadAsStringAsync();
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
                var jsonString = await response.Content.ReadAsStringAsync();
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

            // 2. 尝试 WEAPI
            try
            {
                var payload = new Dictionary<string, object>
                {
                    { "id", songId },
                    { "lv", -1 },
                    { "tv", -1 }
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
        public async Task<List<PlaylistInfo>> GetUserPlaylistsAsync(long userId, int limit = 1000, int offset = 0)
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
                return new List<PlaylistInfo>();
            }

            var playlists = response["playlist"] as JArray;
            return ParsePlaylistList(playlists);
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

            return ids.Select(id => id.Value<string>()).Where(id => !string.IsNullOrEmpty(id)).ToList();
        }

        /// <summary>
        /// 获取用户收藏的专辑列表
        /// 参考: NeteaseCloudMusicApi/module/album_sublist.js
        /// </summary>
        public async Task<List<AlbumInfo>> GetUserAlbumsAsync(int limit = 100, int offset = 0)
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
                return new List<AlbumInfo>();
            }

            var data = response["data"] as JArray;
            if (data == null)
            {
                return new List<AlbumInfo>();
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

            return result;
        }

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
                        var artistNames = artists
                            .Where(a => a != null && a.Type == JTokenType.Object)
                            .Select(a => a["name"]?.Value<string>())
                            .Where(n => !string.IsNullOrEmpty(n))
                            .ToList();

                        if (artistNames.Count > 0)
                        {
                            songInfo.Artist = string.Join("/", artistNames);
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
            if (playlists == null) return result;

            foreach (var playlist in playlists)
            {
                try
                {
                    long creatorId = playlist["creator"]?["userId"]?.Value<long>() ?? 0;
                    long ownerUserId = playlist["userId"]?.Value<long>() ?? 0;

                    var playlistInfo = new PlaylistInfo
                    {
                        Id = playlist["id"]?.Value<string>(),
                        Name = playlist["name"]?.Value<string>(),
                        CoverUrl = playlist["coverImgUrl"]?.Value<string>(),
                        Description = playlist["description"]?.Value<string>(),
                        TrackCount = playlist["trackCount"]?.Value<int>() ?? 0,
                        Creator = playlist["creator"]?["nickname"]?.Value<string>(),
                        CreatorId = creatorId,
                        OwnerUserId = ownerUserId > 0 ? ownerUserId : creatorId
                    };

                    result.Add(playlistInfo);
                }
                catch { }
            }

            return result;
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
                        Artist = album["artist"]?["name"]?.Value<string>()
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
        /// 解析歌单详情
        /// </summary>
        private PlaylistInfo ParsePlaylistDetail(JObject playlist)
        {
            if (playlist == null) return null;

            long creatorId = playlist["creator"]?["userId"]?.Value<long>() ?? 0;
            long ownerUserId = playlist["userId"]?.Value<long>() ?? 0;

            var playlistInfo = new PlaylistInfo
            {
                Id = playlist["id"]?.Value<string>(),
                Name = playlist["name"]?.Value<string>(),
                CoverUrl = playlist["coverImgUrl"]?.Value<string>(),
                Description = playlist["description"]?.Value<string>(),
                TrackCount = playlist["trackCount"]?.Value<int>() ?? 0,
                Creator = playlist["creator"]?["nickname"]?.Value<string>(),
                CreatorId = creatorId,
                OwnerUserId = ownerUserId > 0 ? ownerUserId : creatorId
            };

            // 解析歌曲列表
            var tracks = playlist["tracks"] as JArray ?? playlist["trackIds"] as JArray;
            if (tracks != null)
            {
                playlistInfo.Songs = ParseSongList(tracks);
            }

            return playlistInfo;
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
                RomaLyric = lyricData["romalrc"]?["lyric"]?.Value<string>()
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
                   !string.IsNullOrWhiteSpace(lyric.RomaLyric);
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
        public string Message { get; set; }
        public string Cookie { get; set; }
        public string UserId { get; set; }
        public string Nickname { get; set; }
        public int VipType { get; set; }
        public string AvatarUrl { get; set; }
    }

    /// <summary>
    /// 用户信息
    /// </summary>
    public class UserInfo
    {
        public string UserId { get; set; }
        public string Nickname { get; set; }
        public int VipType { get; set; }
        public string AvatarUrl { get; set; }
    }

    /// <summary>
    /// 歌曲URL信息
    /// </summary>
    public class SongUrlInfo
    {
        public string Id { get; set; }
        public string Url { get; set; }
        public string Level { get; set; }
        public long Size { get; set; }
        public int Br { get; set; }
        public string Type { get; set; }
        public string Md5 { get; set; }
    }

    /// <summary>
    /// 专辑信息
    /// </summary>
    public class AlbumInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Artist { get; set; }
        public string PicUrl { get; set; }
        public string PublishTime { get; set; }
    }

    /// <summary>
    /// 歌词信息
    /// </summary>
    public class LyricInfo
    {
        /// <summary>原文歌词</summary>
        public string Lyric { get; set; }
        /// <summary>翻译歌词</summary>
        public string TLyric { get; set; }
        /// <summary>罗马音歌词</summary>
        public string RomaLyric { get; set; }
    }

    /// <summary>
    /// 评论结果
    /// </summary>
    public class CommentResult
    {
        public int TotalCount { get; set; }
        public List<CommentInfo> Comments { get; set; }
    }

    #endregion
}





