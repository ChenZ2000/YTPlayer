using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
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
    public partial class NeteaseApiClient : IDisposable
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
        private static readonly string[] DomainFallbackOrder = new[]
        {
            EAPI_BASE_URL.TrimEnd('/'),
            INTERFACE_URI.ToString().TrimEnd('/'),
            OFFICIAL_API_BASE.TrimEnd('/')
        };
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
        private readonly object _throttleLock = new object();
        private readonly Dictionary<string, DateTime> _lastActionTimestamps = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, CommentCursorCache> _commentCursorCaches =
            new ConcurrentDictionary<string, CommentCursorCache>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _commentCursorLocks =
            new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan CommentCursorCacheTtl = TimeSpan.FromMinutes(5);

        private static readonly (uint Start, uint End)[] ChineseIpRanges = new (uint, uint)[]
        {
            (607649792u, 608174079u),      // 36.56.0.0 - 36.63.255.255
            (1038614528u, 1039007743u),    // 61.232.0.0 - 61.237.255.255
            (1783627776u, 1784676351u),    // 106.80.0.0 - 106.95.255.255
            (2035023872u, 2035154943u),    // 121.76.0.0 - 121.77.255.255
            (2078801920u, 2079064063u),    // 123.232.0.0 - 123.235.255.255
            (2079064064u, 2079598335u),    // 123.236.0.0 - 123.243.255.255
            (3054197760u, 3054263295u)     // 182.80.0.0 - 182.87.255.255
        };

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
        /// 刷新移动端访客会话，获取最新 MUSIC_A/NMTID 等匿名令牌，降低 -462 风控概率。
        /// </summary>
        private async Task EnsureMobileVisitorSessionAsync()
        {
            try
            {
                var payload = new Dictionary<string, object>();
                // 使用 iOS UA，零 Cookie，模拟手机首次启动
                var result = await PostWeApiWithiOSAsync<JObject>("/register/anonimous", payload, maxRetries: 2, sendCookies: false).ConfigureAwait(false);
                int code = result?["code"]?.Value<int>() ?? -1;
                System.Diagnostics.Debug.WriteLine($"[VisitorSession] register/anonimous code={code}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VisitorSession] 刷新访客会话失败: {ex.Message}");
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
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                MaxConnectionsPerServer = 100
            };

            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(15)  // 优化：降低超时时间，配合音质fallback机制加快加载
            };
            _httpClient.DefaultRequestHeaders.ExpectContinue = false;

            var simplifiedHandler = new HttpClientHandler
            {
                MaxConnectionsPerServer = 100
            };
            _simplifiedClient = new HttpClient(simplifiedHandler)
            {
                Timeout = TimeSpan.FromSeconds(8)   // 优化：降低公共API超时时间
            };
            _simplifiedClient.DefaultRequestHeaders.ExpectContinue = false;

            // EAPI专用客户端：不使用CookieContainer，避免Cookie冲突
            var eapiHandler = new HttpClientHandler
            {
                UseCookies = false,  // 关键：不自动处理Cookie
                MaxConnectionsPerServer = 100
                // EAPI 返回的是 AES 密文，不能启用自动解压缩，否则密文会被破坏
            };
            _eapiClient = new HttpClient(eapiHandler)
            {
                Timeout = TimeSpan.FromSeconds(15)
            };
            _eapiClient.DefaultRequestHeaders.ExpectContinue = false;

            // iOS登录专用客户端：模拟参考项目 netease-music-simple-player (UseCookies=false)
            // 关键修复：避免 HttpClientHandler 自动注入 _cookieContainer 中的访客Cookie
            // 参考项目使用 UseCookies=false + 手动Cookie管理，确保首次登录时发送零Cookie
            var iOSLoginHandler = new HttpClientHandler
            {
                UseCookies = false,  // ⭐ 核心：禁用自动Cookie管理，完全手动控制Cookie
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                MaxConnectionsPerServer = 100
            };
            _iOSLoginClient = new HttpClient(iOSLoginHandler)
            {
                Timeout = TimeSpan.FromSeconds(15)
            };
            _iOSLoginClient.DefaultRequestHeaders.ExpectContinue = false;

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
        /// <summary>推荐</summary>
        Recommend = 0,
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
        public List<SongInfo> Songs { get; set; } = new List<SongInfo>();
        public string Description { get; set; } = string.Empty;
        public bool IsSubscribed { get; set; }
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
        /// <summary>逐字翻译歌词（ytlrc/ytlyric）</summary>
        public string YTLyric { get; set; } = string.Empty;
        /// <summary>逐字罗马音歌词（yromalrc）</summary>
        public string YRomaLyric { get; set; } = string.Empty;
        /// <summary>KRC/卡拉OK歌词（部分接口返回）</summary>
        public string KLyric { get; set; } = string.Empty;
    }

    /// <summary>
    /// 评论结果
    /// </summary>
    public class CommentResult
    {
        public int TotalCount { get; set; }
        public bool HasMore { get; set; }
        public int PageNumber { get; set; } = 1;
        public int PageSize { get; set; } = 20;
        public CommentSortType SortType { get; set; } = CommentSortType.Hot;
        public string? Cursor { get; set; }
        public List<CommentInfo> Comments { get; set; } = new List<CommentInfo>();
    }

    public class CommentFloorResult
    {
        public string? ParentCommentId { get; set; }
        public List<CommentInfo> Comments { get; set; } = new List<CommentInfo>();
        public bool HasMore { get; set; }
        public long? NextTime { get; set; }
        public int TotalCount { get; set; }
    }

    public class CommentMutationResult
    {
        public bool Success { get; set; }
        public int Code { get; set; }
        public string? Message { get; set; }
        public string? CommentId { get; set; }
        public CommentInfo? Comment { get; set; }
        public string? RiskTitle { get; set; }
        public string? RiskSubtitle { get; set; }
        public string? RiskUrl { get; set; }
    }

    #endregion
}

#pragma warning restore CS8600, CS8601, CS8602, CS8603, CS8604, CS8625




























