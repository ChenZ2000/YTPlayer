using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YTPlayer.Models;
using YTPlayer.Utils;
using YTPlayer.Models.Auth;
using YTPlayer.Core;
using BrotliSharpLib;

#pragma warning disable CS8600, CS8601, CS8602, CS8603, CS8604, CS8625

namespace YTPlayer.Core.Auth
{
    /// <summary>
    /// 登录与指纹常量（与官方客户端抓包保持一致）。
    /// </summary>
    internal static class AuthConstants
    {
        internal const string DesktopAppVersion = "2.10.13";
        internal const string DesktopUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36 NeteaseMusic/2.10.13";

        internal const string PcChannel = "netease-desktop";

        // 移动端指纹（参考 Python 版本 & 抓包数据）
        internal const string MobileOs = "iPhone OS";
        internal const string MobileOsVersion = "18.6";
        internal const string MobileAppVersion = "9.3.82";
        internal const string MobileBuildVersion = "6272";
        internal const string MobileMachineId = "iPhone17,1";
        internal const string MobileCustomMark = "nm_Cronet";
        internal const string MobileMConfigInfo =
            "{\"IuRPVVmc3WWul9fT\":{\"version\":95633408,\"appver\":\"9.3.82\"},\"zr4bw6pKFDIZScpo\":{\"version\":3227648,\"appver\":\"9.3.82\"},\"tPJJnts2H31BZXmp\":{\"version\":4050944,\"appver\":\"2.0.30\"}}";

        internal static readonly string MobileUserAgent = BuildMobileUserAgent(MobileAppVersion, MobileBuildVersion, MobileOsVersion);

        internal const string DefaultMobileVersionCode = "140";
        internal const string DefaultMobileResolution = "1920x1080";
        internal const string DefaultMobileChannel = "appstore";
        internal const string DefaultMobileName = "iPhone";

        internal static readonly string[] DesktopUserAgentPool =
        {
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:122.0) Gecko/20100101 Firefox/122.0",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2.1 Safari/605.1.15"
        };

        internal static readonly DevicePreset[] MobileDevicePresets =
        {
            new DevicePreset("iPhone OS", "9.3.60", "6160", "17.2", "iPhone17,1", "NeteaseMusic 9.3.60/6160 (iPhone; iOS 17.2; zh_CN)", "140", "1170x2532", "appstore", "iPhone17,1"),
            new DevicePreset("iPhone OS", "9.3.50", "6150", "16.7", "iPhone15,3", "NeteaseMusic 9.3.50/6150 (iPhone; iOS 16.7; zh_CN)", "140", "1170x2532", "appstore", "iPhone15,3"),
            new DevicePreset("Android", "9.2.80", "6280", "14", "SM-G998B", "NeteaseMusic 9.2.80/6280 (Android 14; zh_CN)", "140", "1080x2400", "xiaomi", "SM-G998B"),
            new DevicePreset("Android", "9.2.70", "6270", "13", "Pixel 7", "NeteaseMusic 9.2.70/6270 (Android 13; zh_CN)", "140", "1080x2400", "google", "Pixel 7"),
            new DevicePreset("iPhone OS", "9.1.65", "6165", "17.1", "iPhone16,2", "NeteaseMusic 9.1.65/6165 (iPhone; iOS 17.1; zh_CN)", "140", "1170x2532", "appstore", "iPhone16,2")
        };

        internal static readonly string[] DeviceMarks = { "nm_Cronet", "ne_AFN" };

        // 与 Node 版本 util/config.json 中的 anonymous_token 保持一致
        internal const string AnonymousToken =
            "bf8bfeabb1aa84f9c8c3906c04a04fb864322804c83f5d607e91a04eae463c9436bd1a17ec353cf780b396507a3f7464e8a60f4bbc019437993166e004087dd32d1490298caf655c2353e58daa0bc13cc7d5c198250968580b12c1b8817e3f5c807e650dd04abd3fb8130b7ae43fcc5b";

        internal static readonly TimeSpan AntiCheatTokenLifetime = TimeSpan.FromMinutes(90);

        internal static readonly string[] BackupAntiCheatTokens =
        {
            "9ca16ae2e6eed3d46a9a8cb9d9cc72a89a8bb2c15b979b9bb0c63b9aaaa1d6e774b397fdb2ed2af0febec3b940f0feacc3b92aa5f5bb9adf25b598bcbdae50969e8ba7d84b978aafa5b342b3acfbbbb343b3afada4a177",
            "9ca16ae2e6eeb4ef4488bb8eaaf365b0bc8ea2d45b928b8aacd27d8af5bd92ea39af9197bdae40e2f4ee93a132f8f4ee85a132e2b9e596fc58eda98895df25888a8ea6c55f838b9a82c73c8aafbcd6d93c8bafbf84c22abf",
            "9ca16ae2e6eeb9f345f89fbfb6d621fcb08fa6d44b939f9fb1c26d8af5bd92ea69ae9597bdae40e2f4ee93a132f8f4ee85a132e2b9e596fc58eda98895df25888a8ea6c55f838b9a82c73c8aafbcd6d93c8bafbf84c22abf"
        };

        internal static string BuildMobileUserAgent(string appVer, string buildVer, string osVer)
        {
            appVer = string.IsNullOrWhiteSpace(appVer) ? MobileAppVersion : appVer;
            buildVer = string.IsNullOrWhiteSpace(buildVer) ? MobileBuildVersion : buildVer;
            osVer = string.IsNullOrWhiteSpace(osVer) ? MobileOsVersion : osVer;
            return $"NeteaseMusic {appVer}/{buildVer} (iPhone; iOS {osVer}; zh_CN)";
        }

        internal static string DetectOsVersion()
        {
            try
            {
                var version = Environment.OSVersion;
                return version.VersionString.Replace("Microsoft ", string.Empty);
            }
            catch
            {
                return "Windows 10";
            }
        }

        internal static string GetDesktopUserAgent(string seed)
        {
            if (DesktopUserAgentPool == null || DesktopUserAgentPool.Length == 0)
            {
                return DesktopUserAgent;
            }

            int index = GetStableIndex(seed, DesktopUserAgentPool.Length);
            return DesktopUserAgentPool[index];
        }

        internal static DevicePreset GetMobilePreset(string seed)
        {
            if (MobileDevicePresets == null || MobileDevicePresets.Length == 0)
            {
                return new DevicePreset(MobileOs, MobileAppVersion, MobileBuildVersion, MobileOsVersion, MobileMachineId, MobileUserAgent, AuthConstants.DefaultMobileVersionCode, AuthConstants.DefaultMobileResolution, AuthConstants.DefaultMobileChannel, AuthConstants.DefaultMobileName);
            }

            int index = GetStableIndex(seed, MobileDevicePresets.Length);
            return MobileDevicePresets[index];
        }

        internal static string GetDeviceMark(string seed)
        {
            if (DeviceMarks == null || DeviceMarks.Length == 0)
            {
                return MobileCustomMark;
            }

            int index = GetStableIndex(seed, DeviceMarks.Length);
            return DeviceMarks[index];
        }

        internal static int GetStableIndex(string seed, int count)
        {
            if (count <= 0)
            {
                return 0;
            }

            if (string.IsNullOrEmpty(seed))
            {
                seed = Guid.NewGuid().ToString("N");
            }

            string md5 = EncryptionHelper.ComputeMd5(seed);
            string slice = md5.Substring(0, Math.Min(8, md5.Length));
            if (!int.TryParse(slice, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value))
            {
                value = slice.GetHashCode();
            }

            if (value < 0)
            {
                value = -value;
            }

            return value % count;
        }
    }

    internal sealed class DevicePreset
    {
        internal DevicePreset(
            string platform,
            string appVer,
            string buildVer,
            string osVer,
            string machineId,
            string userAgent,
            string versionCode,
            string resolution,
            string channel,
            string mobileName)
        {
            Platform = platform;
            AppVersion = appVer;
            BuildVersion = buildVer;
            OsVersion = osVer;
            MachineId = machineId;
            UserAgent = userAgent;
            VersionCode = versionCode;
            Resolution = resolution;
            Channel = channel;
            MobileName = mobileName;
        }

        internal string Platform { get; }
        internal string AppVersion { get; }
        internal string BuildVersion { get; }
        internal string OsVersion { get; }
        internal string MachineId { get; }
        internal string UserAgent { get; }
        internal string VersionCode { get; }
        internal string Resolution { get; }
        internal string Channel { get; }
        internal string MobileName { get; }
    }

    internal static class AntiCheatTokenUtility
    {
        private static readonly Random _random = new Random();

        internal static bool IsValid(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            string trimmed = token.Trim();
            if (trimmed.Length < 160 || trimmed.Length > 200)
            {
                return false;
            }

            foreach (char c in trimmed)
            {
                bool isHex = (c >= '0' && c <= '9') ||
                             (c >= 'a' && c <= 'f') ||
                             (c >= 'A' && c <= 'F');
                if (!isHex)
                {
                    return false;
                }
            }

            return true;
        }

        internal static string Generate()
        {
            if (AuthConstants.BackupAntiCheatTokens != null &&
                AuthConstants.BackupAntiCheatTokens.Length > 0)
            {
                lock (_random)
                {
                    int index = _random.Next(AuthConstants.BackupAntiCheatTokens.Length);
                    return AuthConstants.BackupAntiCheatTokens[index];
                }
            }

            return EncryptionHelper.GenerateRandomHex(176);
        }
    }

    /// <summary>
    /// 负责维护登录指纹、反作弊令牌以及默认 Cookie。
    /// </summary>
    internal sealed class AuthContext
    {
        private readonly ConfigManager _configManager;
        private readonly ConfigModel _config;
        private readonly AccountStateStore _accountStore;
        private AccountState _accountState;
        private readonly object _syncRoot = new object();
        private readonly string _resolvedOsVersion;

        internal AuthContext(ConfigManager configManager, ConfigModel config)
        {
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _resolvedOsVersion = AuthConstants.DetectOsVersion();
            _accountStore = new AccountStateStore();
            _accountState = _accountStore.Load() ?? new AccountState { IsLoggedIn = false };

            // 如果 account.json 存在且包含设备指纹，优先使用它
            RestoreDeviceFingerprintFromAccountState();

            EnsureFingerprintInitialized();
        }

        /// <summary>
        /// 从 account.json 恢复设备指纹（不再保存到 config.json）。
        /// 设备指纹现在只存储在 AccountState 中。
        /// </summary>
        private void RestoreDeviceFingerprintFromAccountState()
        {
            // 设备指纹已完全移到 AccountState，不再需要同步到 config
            // 此方法保留用于兼容性，但不执行任何操作
        }

        internal ConfigModel Config => _config;

        internal AccountState CurrentAccountState
        {
            get
            {
                lock (_syncRoot)
                {
                    if (_accountState == null)
                    {
                        _accountState = new AccountState
                        {
                            IsLoggedIn = false
                        };
                    }

                    return _accountState;
                }
            }
        }

        internal FingerprintSnapshot GetFingerprintSnapshot()
        {
            var state = CurrentAccountState;
            EnsureFingerprintInitialized();
            return new FingerprintSnapshot
            {
                DeviceId = state.DeviceId ?? EncryptionHelper.GenerateDeviceId(),
                SDeviceId = state.SDeviceId,
                NmtId = state.NmtId,
                NtesNuid = state.NtesNuid,
                WnmCid = state.WnmCid,
                MusicA = state.MusicA,
                DeviceOs = state.DeviceOs ?? "pc",
                DeviceOsVersion = state.DeviceOsVersion ?? _resolvedOsVersion,
                DeviceAppVersion = state.DeviceAppVersion ?? AuthConstants.DesktopAppVersion,
                DeviceBuildVersion = state.DeviceBuildVersion,
                DeviceVersionCode = state.DeviceVersionCode,
                DeviceChannel = state.DeviceChannel ?? "netease",
                DeviceMobileName = state.DeviceMobileName ?? "PC",
                DeviceResolution = state.DeviceResolution ?? "1920x1080",
                DesktopUserAgent = state.DesktopUserAgent ?? AuthConstants.DesktopUserAgent,
                DeviceUserAgent = state.DeviceUserAgent
            };
        }

        internal void UpdateAccountState(AccountState state)
        {
            if (state == null)
            {
                state = new AccountState { IsLoggedIn = false };
            }

            lock (_syncRoot)
            {
                _accountState = state;
                try
                {
                    _accountStore.Save(_accountState);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[AuthContext] 保存 account.json 失败: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 应用登录资料（用户信息和认证凭证）。
        /// 只更新 account.json（账户数据不再保存在 config.json 中）。
        /// </summary>
        internal void ApplyLoginProfile(UserAccountInfo profile, string musicU, string csrfToken)
        {
            lock (_syncRoot)
            {
                // 更新 account.json
                if (_accountState != null)
                {
                    _accountState.MusicU = musicU;
                    _accountState.CsrfToken = csrfToken;

                    if (profile != null)
                    {
                        _accountState.UserId = profile.UserId > 0 ? profile.UserId.ToString() : _accountState.UserId;
                        _accountState.Nickname = profile.Nickname ?? _accountState.Nickname;
                        _accountState.AvatarUrl = profile.AvatarUrl ?? _accountState.AvatarUrl;
                        _accountState.VipType = profile.VipType != 0 ? profile.VipType : _accountState.VipType;
                    }

                    try
                    {
                        _accountStore.Save(_accountState);
                        System.Diagnostics.Debug.WriteLine("[AuthContext] 登录资料已同步到 account.json");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[AuthContext] 保存登录资料到 account.json 失败: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// 清空登录资料。
        /// 清理 account.json 中的登录信息，但保留设备指纹。
        /// </summary>
        internal void ClearLoginProfile()
        {
            lock (_syncRoot)
            {
                // 清空 account.json 中的登录信息，但保留设备指纹
                try
                {
                    _accountStore.Clear();  // 使用 Clear 方法，保留设备指纹
                    _accountState = _accountStore.Load();  // 重新加载清空后的状态
                    System.Diagnostics.Debug.WriteLine("[AuthContext] 已清空 account.json 中的登录信息");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[AuthContext] 清理 account.json 失败: {ex.Message}");
                    _accountState = new AccountState { IsLoggedIn = false };
                }
            }
        }

        /// <summary>
        /// 创建登录状态快照，用于保存到 account.json。
        /// 包含完整的用户信息、认证凭证和设备指纹。
        /// </summary>
        internal AccountState CreateLoginStateSnapshot(string cookieSnapshot, IReadOnlyCollection<CookieItem> cookies, UserAccountInfo profile)
        {
            lock (_syncRoot)
            {
                // 确保设备指纹已初始化
                bool changed = false;
                EnsureDeviceProfileInternal(ref changed);

                var state = new AccountState
                {
                    IsLoggedIn = !string.IsNullOrEmpty(cookieSnapshot),

                    // 用户信息（从 profile 参数获取，如果为空则保留当前 accountState 中的数据）
                    UserId = profile?.UserId > 0 ? profile.UserId.ToString() : _accountState?.UserId,
                    Nickname = profile?.Nickname ?? _accountState?.Nickname,
                    AvatarUrl = profile?.AvatarUrl ?? _accountState?.AvatarUrl,
                    VipType = profile?.VipType ?? _accountState?.VipType ?? 0,

                    // Cookie 和认证凭证
                    Cookie = cookieSnapshot ?? string.Empty,
                    MusicU = _accountState?.MusicU,
                    CsrfToken = _accountState?.CsrfToken,
                    Cookies = cookies != null ? new List<CookieItem>(cookies) : new List<CookieItem>(),

                    // 设备指纹和会话信息（从 accountState 保留，确保设备指纹持久化）
                    DeviceId = _accountState?.DeviceId,
                    SDeviceId = _accountState?.SDeviceId,
                    DeviceMachineId = _accountState?.DeviceMachineId,
                    DeviceOs = _accountState?.DeviceOs,
                    DeviceOsVersion = _accountState?.DeviceOsVersion,
                    DeviceAppVersion = _accountState?.DeviceAppVersion,
                    DeviceBuildVersion = _accountState?.DeviceBuildVersion,
                    DeviceVersionCode = _accountState?.DeviceVersionCode,
                    DeviceUserAgent = _accountState?.DeviceUserAgent,
                    DeviceResolution = _accountState?.DeviceResolution,
                    DeviceChannel = _accountState?.DeviceChannel,
                    DeviceMobileName = _accountState?.DeviceMobileName,
                    DesktopUserAgent = _accountState?.DesktopUserAgent,
                    DeviceMark = _accountState?.DeviceMark,
                    DeviceMConfigInfo = _accountState?.DeviceMConfigInfo,

                    // 访客令牌和反作弊令牌（从当前 accountState 保留，不再从 config 读取）
                    MusicA = _accountState?.MusicA,
                    NmtId = _accountState?.NmtId,
                    NtesNuid = _accountState?.NtesNuid,
                    WnmCid = _accountState?.WnmCid,
                    AntiCheatToken = _accountState?.AntiCheatToken,
                    AntiCheatTokenGeneratedAt = _accountState?.AntiCheatTokenGeneratedAt,
                    AntiCheatTokenExpiresAt = _accountState?.AntiCheatTokenExpiresAt,

                    // 元数据
                    LastUpdated = DateTimeOffset.UtcNow,
                    FingerprintLastUpdated = _accountState?.FingerprintLastUpdated ?? DateTimeOffset.UtcNow
                };

                // 如果提供了 profile，使用 profile 中的信息覆盖
                if (profile != null)
                {
                    if (profile.UserId > 0)
                    {
                        state.UserId = profile.UserId.ToString();
                    }
                    if (!string.IsNullOrEmpty(profile.Nickname))
                    {
                        state.Nickname = profile.Nickname;
                    }
                    if (!string.IsNullOrEmpty(profile.AvatarUrl))
                    {
                        state.AvatarUrl = profile.AvatarUrl;
                    }
                    if (profile.VipType != 0)
                    {
                        state.VipType = profile.VipType;
                    }
                }

                return state;
            }
        }

        private void EnsureFingerprintInitialized()
        {
            lock (_syncRoot)
            {
                bool accountChanged = false;

                // 初始化设备指纹（DeviceId, SDeviceId, DeviceOs 等15个字段）
                EnsureDeviceProfileInternal(ref accountChanged);

                if (string.IsNullOrWhiteSpace(_accountState.NmtId))
                {
                    _accountState.NmtId = EncryptionHelper.GenerateRandomHex(32);
                    accountChanged = true;
                }

                if (string.IsNullOrWhiteSpace(_accountState.NtesNuid))
                {
                    _accountState.NtesNuid = EncryptionHelper.GenerateRandomHex(32);
                    accountChanged = true;
                }

                if (string.IsNullOrWhiteSpace(_accountState.WnmCid))
                {
                    _accountState.WnmCid = EncryptionHelper.GenerateWNMCID();
                    accountChanged = true;
                }

                if (string.IsNullOrWhiteSpace(_accountState.MusicA))
                {
                    _accountState.MusicA = AuthConstants.AnonymousToken;
                    accountChanged = true;
                }

                if (!AntiCheatTokenUtility.IsValid(_accountState.AntiCheatToken))
                {
                    _accountState.AntiCheatToken = AntiCheatTokenUtility.Generate();
                    _accountState.AntiCheatTokenGeneratedAt = DateTimeOffset.UtcNow;
                    _accountState.AntiCheatTokenExpiresAt = _accountState.AntiCheatTokenGeneratedAt.Value + AuthConstants.AntiCheatTokenLifetime;
                    accountChanged = true;
                }
                else
                {
                    if (!_accountState.AntiCheatTokenGeneratedAt.HasValue)
                    {
                        _accountState.AntiCheatTokenGeneratedAt = DateTimeOffset.UtcNow;
                        accountChanged = true;
                    }

                    if (!_accountState.AntiCheatTokenExpiresAt.HasValue && _accountState.AntiCheatTokenGeneratedAt.HasValue)
                    {
                        _accountState.AntiCheatTokenExpiresAt = _accountState.AntiCheatTokenGeneratedAt.Value + AuthConstants.AntiCheatTokenLifetime;
                        accountChanged = true;
                    }
                }

                if (!_accountState.FingerprintLastUpdated.HasValue)
                {
                    _accountState.FingerprintLastUpdated = DateTimeOffset.UtcNow;
                    accountChanged = true;
                }

                // 设备指纹不再保存到 config.json，只保存到 account.json
                if (accountChanged)
                {
                    _accountState.FingerprintLastUpdated = DateTimeOffset.UtcNow;
                    _accountStore.Save(_accountState);
                }
            }
        }

        internal string GetActiveAntiCheatToken()
        {
            lock (_syncRoot)
            {
                var expiresAt = _accountState.AntiCheatTokenExpiresAt ??
                                (_accountState.AntiCheatTokenGeneratedAt?.Add(AuthConstants.AntiCheatTokenLifetime));

                bool needRefresh = !AntiCheatTokenUtility.IsValid(_accountState.AntiCheatToken) ||
                                   !expiresAt.HasValue ||
                                   DateTimeOffset.UtcNow >= expiresAt.Value;

                if (needRefresh)
                {
                    var newToken = AntiCheatTokenUtility.Generate();
                    ApplyAntiCheatTokenInternal(newToken, AuthConstants.AntiCheatTokenLifetime);
                }
                else if (!_accountState.AntiCheatTokenExpiresAt.HasValue && expiresAt.HasValue)
                {
                    _accountState.AntiCheatTokenExpiresAt = expiresAt;
                    _accountStore.Save(_accountState);
                }

                return _accountState.AntiCheatToken;
            }
        }

        internal void ProvideAntiCheatToken(string token, TimeSpan ttl)
        {
            token = token?.Trim();
            if (!AntiCheatTokenUtility.IsValid(token))
            {
                System.Diagnostics.Debug.WriteLine("[AuthContext] 提供的 X-antiCheatToken 无效，已忽略。");
                return;
            }

            if (ttl <= TimeSpan.Zero)
            {
                ttl = AuthConstants.AntiCheatTokenLifetime;
            }

            lock (_syncRoot)
            {
                ApplyAntiCheatTokenInternal(token, ttl);
            }
        }

        internal string LoadAntiCheatTokenFromFile(string path, TimeSpan ttl)
        {
            if (ttl <= TimeSpan.Zero)
            {
                ttl = AuthConstants.AntiCheatTokenLifetime;
            }

            try
            {
                string token = null;

                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    string ext = Path.GetExtension(path)?.ToLowerInvariant();
                    if (string.Equals(ext, ".saz", StringComparison.OrdinalIgnoreCase))
                    {
                        using (var zipStream = File.OpenRead(path))
                        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: false))
                        {
                            foreach (var entry in archive.Entries)
                            {
                                if (entry.Length == 0)
                                {
                                    continue;
                                }

                                // 聚焦在 request/response 文本
                                if (!entry.FullName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) &&
                                    !entry.FullName.EndsWith(".log", StringComparison.OrdinalIgnoreCase) &&
                                    entry.FullName.IndexOf("request", StringComparison.OrdinalIgnoreCase) < 0 &&
                                    entry.FullName.IndexOf("response", StringComparison.OrdinalIgnoreCase) < 0)
                                {
                                    continue;
                                }

                                using (var stream = entry.Open())
                                {
                                    string text = ReadStreamToString(stream);
                                    token = ExtractAntiCheatTokenFromText(text);
                                    if (!string.IsNullOrEmpty(token))
                                    {
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        string text;
                        try
                        {
                            text = File.ReadAllText(path, Encoding.UTF8);
                        }
                        catch (DecoderFallbackException)
                        {
                            text = File.ReadAllText(path, Encoding.Default);
                        }
                        token = ExtractAntiCheatTokenFromText(text);
                    }
                }
                else
                {
                    token = ExtractAntiCheatTokenFromText(path);
                }

                if (!string.IsNullOrEmpty(token))
                {
                    ProvideAntiCheatToken(token, ttl);
                    return token;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AuthContext] 解析反作弊令牌失败: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// 构建基础 Cookie 映射
        /// ⭐⭐⭐ 核心修复：返回完整的桌面客户端设备指纹Cookie，避免8821风控错误
        /// 参考备份版本成功实现，WEAPI请求（包括二维码登录）必须包含完整设备指纹
        /// </summary>
        internal IReadOnlyDictionary<string, string> BuildBaseCookieMap(bool includeAnonymousToken)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // ⭐ 桌面客户端设备指纹Cookie（8个必需字段，避免8821风控）
                ["__remember_me"] = "true",
                ["os"] = "pc",
                ["osver"] = _resolvedOsVersion,
                ["appver"] = AuthConstants.DesktopAppVersion,
                ["buildver"] = _accountState.DeviceBuildVersion ?? AuthConstants.MobileBuildVersion,
                ["channel"] = AuthConstants.PcChannel,
                ["deviceId"] = _accountState.DeviceId,
                ["sDeviceId"] = _accountState.SDeviceId ?? _accountState.DeviceId,

                // 访客设备标识（WEAPI请求验证关键字段）
                ["NMTID"] = _accountState.NmtId ?? "",
                ["_ntes_nuid"] = _accountState.NtesNuid ?? ""
            };

            // WNMCID（可选）
            if (!string.IsNullOrEmpty(_accountState.WnmCid))
            {
                map["WNMCID"] = _accountState.WnmCid;
            }

            // CSRF Token（如果存在）
            if (!string.IsNullOrEmpty(_accountState.CsrfToken))
            {
                map["__csrf"] = _accountState.CsrfToken;
            }

            // 匿名访问令牌（未登录状态必需）
            if (includeAnonymousToken && !string.IsNullOrEmpty(_accountState.MusicA))
            {
                map["MUSIC_A"] = _accountState.MusicA;
            }

            return map;
        }

        /// <summary>
        /// 构建移动端（iOS/Android）访客 Cookie 映射，专供短信验证码登录链路使用。
        /// 保证 Cookie 字段与设备指纹一致，避免 UA 与 Cookie 混用导致的 -462/10004。
        /// </summary>
        internal IReadOnlyDictionary<string, string> BuildMobileCookieMap(bool includeAnonymousToken)
        {
            lock (_syncRoot)
            {
                bool changed = false;
                EnsureDeviceProfileInternal(ref changed);
                if (changed)
                {
                    _accountStore.Save(_accountState);
                }

                var nowSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["os"] = (_accountState.DeviceOs ?? AuthConstants.MobileOs).Equals("Android", StringComparison.OrdinalIgnoreCase) ? "android" : "ios",
                    ["osver"] = _accountState.DeviceOsVersion ?? AuthConstants.MobileOsVersion,
                    ["appver"] = _accountState.DeviceAppVersion ?? AuthConstants.MobileAppVersion,
                    ["buildver"] = _accountState.DeviceBuildVersion ?? nowSeconds,
                    ["versioncode"] = _accountState.DeviceVersionCode ?? AuthConstants.DefaultMobileVersionCode,
                    ["channel"] = _accountState.DeviceChannel ?? AuthConstants.DefaultMobileChannel,
                    ["resolution"] = _accountState.DeviceResolution ?? AuthConstants.DefaultMobileResolution,
                    ["deviceId"] = _accountState.DeviceId,
                    ["sDeviceId"] = _accountState.SDeviceId ?? _accountState.DeviceId,
                    ["mobilename"] = _accountState.DeviceMobileName ?? _accountState.DeviceMachineId ?? AuthConstants.DefaultMobileName,
                    ["machine"] = _accountState.DeviceMachineId ?? AuthConstants.MobileMachineId
                };

                // 访客标识 & CSRF
                if (!string.IsNullOrEmpty(_accountState.NmtId))
                {
                    map["NMTID"] = _accountState.NmtId;
                }
                if (!string.IsNullOrEmpty(_accountState.NtesNuid))
                {
                    map["_ntes_nuid"] = _accountState.NtesNuid;
                }
                if (!string.IsNullOrEmpty(_accountState.WnmCid))
                {
                    map["WNMCID"] = _accountState.WnmCid;
                }
                if (!string.IsNullOrEmpty(_accountState.CsrfToken))
                {
                    map["__csrf"] = _accountState.CsrfToken;
                }
                if (includeAnonymousToken && !string.IsNullOrEmpty(_accountState.MusicA))
                {
                    map["MUSIC_A"] = _accountState.MusicA;
                }

                return map;
            }
        }

        internal void SyncFromCookies(CookieCollection cookies)
        {
            if (cookies == null || cookies.Count == 0)
            {
                return;
            }

            bool accountChanged = false;

            lock (_syncRoot)
            {
                string UpdateIfChanged(string name, string current, ref bool changedFlag)
                {
                    var value = cookies[name]?.Value;
                    if (!string.IsNullOrEmpty(value) && !string.Equals(value, current, StringComparison.Ordinal))
                    {
                        changedFlag = true;
                        return value;
                    }

                    return current;
                }

                // 同步访客令牌和反作弊令牌到 accountState（不再保存到 config）
                if (_accountState != null)
                {
                    _accountState.NmtId = UpdateIfChanged("NMTID", _accountState.NmtId, ref accountChanged);
                    _accountState.NtesNuid = UpdateIfChanged("_ntes_nuid", _accountState.NtesNuid, ref accountChanged);
                    _accountState.WnmCid = UpdateIfChanged("WNMCID", _accountState.WnmCid, ref accountChanged);
                    _accountState.MusicA = UpdateIfChanged("MUSIC_A", _accountState.MusicA, ref accountChanged);
                    _accountState.CsrfToken = UpdateIfChanged("__csrf", _accountState.CsrfToken, ref accountChanged);

                    if (accountChanged)
                    {
                        _accountStore.Save(_accountState);
                    }
                }

                // 设备指纹现在完全由 AccountState 管理，不再同步到 config
            }
        }

        internal IDictionary<string, object> BuildEapiHeaderPayload(bool useMobileMode)
        {
            lock (_syncRoot)
            {
                bool changed = false;
                EnsureDeviceProfileInternal(ref changed);
                if (changed)
                {
                    _accountStore.Save(_accountState);
                }

                if (useMobileMode)
                {
                    var osFlag = (_accountState.DeviceOs ?? AuthConstants.MobileOs).Equals("Android", StringComparison.OrdinalIgnoreCase)
                        ? "android"
                        : "ios";
                    return new Dictionary<string, object>
                    {
                        { "os", osFlag },
                        { "appver", _accountState.DeviceAppVersion ?? AuthConstants.MobileAppVersion },
                        { "osver", _accountState.DeviceOsVersion ?? AuthConstants.MobileOsVersion },
                        { "deviceId", _accountState.DeviceId },
                        { "sDeviceId", _accountState.SDeviceId ?? _accountState.DeviceId },
                        { "buildver", _accountState.DeviceBuildVersion ?? AuthConstants.MobileBuildVersion },
                        { "versioncode", _accountState.DeviceVersionCode ?? AuthConstants.DefaultMobileVersionCode },
                        { "resolution", _accountState.DeviceResolution ?? AuthConstants.DefaultMobileResolution },
                        { "channel", _accountState.DeviceChannel ?? AuthConstants.DefaultMobileChannel },
                        { "machine", _accountState.DeviceMachineId ?? AuthConstants.MobileMachineId },
                        { "mobilename", _accountState.DeviceMobileName ?? _accountState.DeviceMachineId ?? AuthConstants.DefaultMobileName },
                        { "requestId", EncryptionHelper.GenerateRequestId() },
                        { "__csrf", _accountState.CsrfToken ?? string.Empty }
                    };
                }

                return new Dictionary<string, object>
                {
                    { "os", "pc" },
                    { "appver", AuthConstants.DesktopAppVersion },
                    { "osver", _resolvedOsVersion },
                    { "deviceId", _accountState.DeviceId },
                    { "requestId", EncryptionHelper.GenerateRequestId() },
                    { "__csrf", _accountState.CsrfToken ?? string.Empty }
                };
            }
        }

        internal IDictionary<string, string> BuildMobileRequestHeaders(string musicU, string antiCheatToken)
        {
            lock (_syncRoot)
            {
                bool changed = false;
                EnsureDeviceProfileInternal(ref changed);
                if (changed)
                {
                    _accountStore.Save(_accountState);
                }

                var deviceOs = _accountState.DeviceOs ?? AuthConstants.MobileOs;
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Accept"] = "*/*",
                    ["Accept-Encoding"] = "gzip, deflate, br",
                    ["Accept-Language"] = "zh-Hans-CN;q=1, en-CN;q=0.9",
                    ["Connection"] = "keep-alive",
                    ["User-Agent"] = _accountState.DeviceUserAgent ?? AuthConstants.MobileUserAgent,
                    ["x-appver"] = _accountState.DeviceAppVersion ?? AuthConstants.MobileAppVersion,
                    ["x-buildver"] = _accountState.DeviceBuildVersion ?? AuthConstants.MobileBuildVersion,
                    ["x-os"] = deviceOs,
                    ["x-osver"] = _accountState.DeviceOsVersion ?? AuthConstants.MobileOsVersion,
                    ["x-deviceId"] = _accountState.DeviceId,
                    ["x-sDeviceId"] = _accountState.SDeviceId ?? _accountState.DeviceId,
                    ["x-versioncode"] = _accountState.DeviceVersionCode ?? AuthConstants.DefaultMobileVersionCode,
                    ["x-resolution"] = _accountState.DeviceResolution ?? AuthConstants.DefaultMobileResolution,
                    ["x-channel"] = _accountState.DeviceChannel ?? AuthConstants.DefaultMobileChannel,
                    ["x-aeapi"] = "true",
                    ["x-netlib"] = "Cronet",
                    ["X-MAM-CustomMark"] = _accountState.DeviceMark ?? AuthConstants.MobileCustomMark,
                    ["MConfig-Info"] = _accountState.DeviceMConfigInfo ?? AuthConstants.MobileMConfigInfo,
                    ["x-machineid"] = _accountState.DeviceMachineId ?? AuthConstants.MobileMachineId,
                    ["Origin"] = "https://music.163.com",
                    ["Referer"] = "https://music.163.com"
                };

                if (!string.IsNullOrEmpty(musicU))
                {
                    headers["x-music-u"] = musicU;
                }

                if (!string.IsNullOrEmpty(antiCheatToken))
                {
                    headers["X-antiCheatToken"] = antiCheatToken;
                }

                return headers;
            }
        }

        private void EnsureDeviceProfileInternal(ref bool changed)
        {
            if (string.IsNullOrWhiteSpace(_accountState.DeviceId))
            {
                _accountState.DeviceId = Guid.NewGuid().ToString("N");
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(_accountState.SDeviceId))
            {
                _accountState.SDeviceId = Guid.NewGuid().ToString("N");
                changed = true;
            }

            string presetSeed = !string.IsNullOrEmpty(_accountState.DeviceId)
                ? _accountState.DeviceId
                : _accountState.SDeviceId ?? Guid.NewGuid().ToString("N");

            var preset = AuthConstants.GetMobilePreset(presetSeed);

            if (string.IsNullOrWhiteSpace(_accountState.DeviceMachineId))
            {
                _accountState.DeviceMachineId = preset.MachineId;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(_accountState.DeviceOs))
            {
                _accountState.DeviceOs = preset.Platform;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(_accountState.DeviceOsVersion))
            {
                _accountState.DeviceOsVersion = preset.OsVersion;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(_accountState.DeviceAppVersion))
            {
                _accountState.DeviceAppVersion = preset.AppVersion;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(_accountState.DeviceBuildVersion))
            {
                _accountState.DeviceBuildVersion = preset.BuildVersion;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(_accountState.DeviceVersionCode))
            {
                _accountState.DeviceVersionCode = preset.VersionCode ?? AuthConstants.DefaultMobileVersionCode;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(_accountState.DeviceResolution))
            {
                _accountState.DeviceResolution = preset.Resolution ?? AuthConstants.DefaultMobileResolution;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(_accountState.DeviceChannel))
            {
                _accountState.DeviceChannel = preset.Channel ?? AuthConstants.DefaultMobileChannel;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(_accountState.DeviceMobileName))
            {
                _accountState.DeviceMobileName = preset.MobileName ?? _accountState.DeviceMachineId ?? AuthConstants.DefaultMobileName;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(_accountState.DeviceMark))
            {
                _accountState.DeviceMark = AuthConstants.GetDeviceMark(presetSeed);
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(_accountState.DeviceMConfigInfo))
            {
                _accountState.DeviceMConfigInfo = AuthConstants.MobileMConfigInfo;
                changed = true;
            }

            var desiredUa = !string.IsNullOrEmpty(preset.UserAgent)
                ? preset.UserAgent
                : AuthConstants.BuildMobileUserAgent(
                    _accountState.DeviceAppVersion,
                    _accountState.DeviceBuildVersion,
                    _accountState.DeviceOsVersion);

            if (string.IsNullOrWhiteSpace(_accountState.DeviceUserAgent) ||
                !string.Equals(_accountState.DeviceUserAgent, desiredUa, StringComparison.Ordinal))
            {
                _accountState.DeviceUserAgent = desiredUa;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(_accountState.DesktopUserAgent))
            {
                _accountState.DesktopUserAgent = AuthConstants.GetDesktopUserAgent(presetSeed);
                changed = true;
            }
        }

        private void ApplyAntiCheatTokenInternal(string token, TimeSpan ttl)
        {
            if (!AntiCheatTokenUtility.IsValid(token))
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var effectiveTtl = ttl <= TimeSpan.Zero ? AuthConstants.AntiCheatTokenLifetime : ttl;
            var expiresAt = now.Add(effectiveTtl);

            _accountState.AntiCheatToken = token;
            _accountState.AntiCheatTokenGeneratedAt = now;
            _accountState.AntiCheatTokenExpiresAt = expiresAt;
            _accountState.FingerprintLastUpdated = now;

            try
            {
                _accountStore.Save(_accountState);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AuthContext] 保存账户状态中的反作弊令牌失败: {ex.Message}");
            }

            if (_accountState == null)
            {
                _accountState = new AccountState { IsLoggedIn = false };
            }

            _accountState.AntiCheatToken = token;
            _accountState.AntiCheatTokenExpiresAt = expiresAt;

            try
            {
                _accountStore.Save(_accountState);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AuthContext] 保存 account.json 中的反作弊令牌失败: {ex.Message}");
            }
        }

        private static string ExtractAntiCheatTokenFromText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var headerPattern = new Regex(@"X-antiCheatToken\s*[:=]\s*([0-9a-zA-Z\-_.:+/=]{64,})", RegexOptions.IgnoreCase);
            var match = headerPattern.Match(text);
            if (match.Success && AntiCheatTokenUtility.IsValid(match.Groups[1].Value))
            {
                return match.Groups[1].Value.Trim();
            }

            var hexPattern = new Regex(@"[0-9a-fA-F]{160,200}");
            match = hexPattern.Match(text);
            if (match.Success && AntiCheatTokenUtility.IsValid(match.Value))
            {
                return match.Value.Trim();
            }

            return null;
        }

        private static string ReadStreamToString(Stream stream)
        {
            if (stream == null)
            {
                return null;
            }

            using (var ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                var buffer = ms.ToArray();
                try
                {
                    return Encoding.UTF8.GetString(buffer);
                }
                catch (DecoderFallbackException)
                {
                    try
                    {
                        return Encoding.GetEncoding("GBK").GetString(buffer);
                    }
                    catch (Exception)
                    {
                        return Encoding.Default.GetString(buffer);
                    }
                }
            }
        }
    }

    internal sealed class FingerprintSnapshot
    {
        public string DeviceId { get; set; } = EncryptionHelper.GenerateDeviceId();
        public string? SDeviceId { get; set; }
        public string? NmtId { get; set; }
        public string? NtesNuid { get; set; }
        public string? WnmCid { get; set; }
        public string? MusicA { get; set; }
        public string? DeviceOs { get; set; }
        public string? DeviceOsVersion { get; set; }
        public string? DeviceAppVersion { get; set; }
        public string? DeviceBuildVersion { get; set; }
        public string? DeviceVersionCode { get; set; }
        public string? DeviceChannel { get; set; }
        public string? DeviceMobileName { get; set; }
        public string? DeviceResolution { get; set; }
        public string? DesktopUserAgent { get; set; }
        public string? DeviceUserAgent { get; set; }
    }
}


namespace YTPlayer.Core
{
    /// <summary>
    /// 登录相关逻辑（从备份项目复刻），集中放置在单文件中便于维护。
    /// </summary>
    public partial class NeteaseApiClient
    {
        public void ClearCookies()
        {
            System.Diagnostics.Debug.WriteLine("[Cookie] ?? 开始清理所有Cookie和认证数据...");

            try
            {
                var field = typeof(CookieContainer).GetField("m_domainTable", BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null)
                {
                    if (field.GetValue(_cookieContainer) is Hashtable table)
                    {
                        int cookieCount = table.Count;
                        table.Clear();
                        System.Diagnostics.Debug.WriteLine($"[Cookie] ? 已清空 CookieContainer ({cookieCount} 个域)");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Cookie] ?? 清空 CookieContainer 失败: {ex.Message}");
            }

            // 清理登录凭证
            _musicU = null;
            _csrfToken = null;

            System.Diagnostics.Debug.WriteLine("[Cookie] ? 已清理 MUSIC_U 和 __csrf");

            // ??? 移除 UpdateCookies() 调用 - 已全部清空，无需更新
            // ??? 移除 ClearLoginProfile() 调用 - LogoutAsync 已经调用过了
            // 原代码：UpdateCookies();
            // 原代码：_authContext?.ClearLoginProfile();

            System.Diagnostics.Debug.WriteLine("[Cookie] ??? Cookie清理完成");
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
            if (string.IsNullOrEmpty(_musicU) || string.IsNullOrEmpty(_csrfToken))
            {
                return false;
            }

            var cookies = _cookieContainer.GetCookies(MUSIC_URI);
            bool hasMusicU = cookies["MUSIC_U"] != null || cookies["MUSIC_U"]?.Value == _musicU;
            bool hasCsrf = cookies["__csrf"] != null || cookies["__csrf"]?.Value == _csrfToken;
            return hasMusicU && hasCsrf;
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
            // ? 核心修复：使用标准 _httpClient，自动发送CookieContainer中的所有Cookie
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

            // 确保访客指纹 Cookie 存在（NMTID/_ntes_nuid/MUSIC_A），避免服务器返回空响应
            try
            {
                var cookies = _cookieContainer.GetCookies(MUSIC_URI);
                bool needBaseCookies = cookies == null || cookies.Count == 0 ||
                                       cookies["NMTID"] == null || cookies["_ntes_nuid"] == null;
                if (needBaseCookies)
                {
                    ApplyBaseCookies(includeAnonymousToken: true);
                    UpdateCookies();
                }
            }
            catch
            {
                // 忽略补充失败，继续流程
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
                // ? 核心修复：使用标准 _httpClient，自动发送CookieContainer中的所有Cookie
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
                    var finalizedCookie = FinalizeLoginCookies(cookieString);
                    if (!string.IsNullOrEmpty(finalizedCookie))
                    {
                        pollResult.Cookie = finalizedCookie;
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
                var profileToken = responseData["profile"];
                var accountToken = responseData["account"];
                bool hasProfile = profileToken != null && profileToken.Type == JTokenType.Object;
                bool hasAccount = accountToken != null && accountToken.Type == JTokenType.Object;

                var status = new LoginStatusResult
                {
                    RawJson = result.ToString(Formatting.None),
                    IsLoggedIn = code == 200 && (hasProfile || hasAccount)
                };

                if (status.IsLoggedIn)
                {
                    if (hasProfile && profileToken is JObject profile)
                    {
                        status.Nickname = profile["nickname"]?.Value<string>();
                        status.AccountId = profile["userId"]?.Value<long?>();
                        status.AvatarUrl = profile["avatarUrl"]?.Value<string>();
                        status.VipType = profile["vipType"]?.Value<int>() ?? 0;
                    }
                    else if (hasAccount && accountToken is JObject account)
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
        /// ??? 完全清理当前账户的所有数据，确保下次登录时使用全新状态
        /// </summary>
        public async Task LogoutAsync()
        {
            System.Diagnostics.Debug.WriteLine("[Logout] 开始退出登录...");

            try
            {
                // 1. 调用服务器退出接口
                await PostWeApiAsync<JObject>("/logout", new Dictionary<string, object>());
                System.Diagnostics.Debug.WriteLine("[Logout] ? 服务器退出成功");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Logout] ?? 服务器退出失败（继续清理）: {ex.Message}");
            }
            finally
            {
                // 2. 清理本地所有数据
                ClearCookies();

                // 3. 清理账户状态
                _authContext?.ClearLoginProfile();

                System.Diagnostics.Debug.WriteLine("[Logout] ??? 退出登录完成，所有数据已清理");
            }
        }

        /// <summary>
        /// 发送短信验证码（手机号登录）
        /// 1.7.3 行为：iOS UA + 访客 Cookie（过滤桌面字段）
        /// </summary>
        public async Task<bool> SendCaptchaAsync(string phone, string ctcode = "86")
        {
            var payload = new Dictionary<string, object>
            {
                { "cellphone", phone },
                { "ctcode", ctcode }
            };

            System.Diagnostics.Debug.WriteLine($"[SMS] 发送验证码请求: phone={phone}, ctcode={ctcode}");

            // 使用 iOS 通道，发送纯移动端访客 Cookie，避免桌面指纹干扰
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
        /// ? 参考 netease-music-simple-player/Net/NetClasses.cs:2521-2541
        /// 关键: 使用 iOS User-Agent 避免 -462 风控错误
        /// </summary>
        public async Task<LoginResult> LoginByCaptchaAsync(string phone, string captcha, string ctcode = "86")
        {
            // ? 重要：不再在 Cookie 中设置 os=ios 和 appver=8.7.01
            // 因为 PostWeApiWithiOSAsync 已经使用 iOS User-Agent
            // 在 Cookie 中设置 os=ios 会与桌面系统的其他 Cookie 冲突，触发风控

            // ? 核心修复：完全模拟参考项目的payload，只发送3个字段
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

            // ? 核心修复：登录请求使用零Cookie模式（sendCookies默认为false）
            // 模拟真实iPhone首次登录场景，避免桌面Cookie与iOS UA的设备指纹不匹配
            // 1.7.3 行为：登录阶段零 Cookie（sendCookies=false）
            var result = await PostWeApiWithiOSAsync<JObject>("/login/cellphone", payload, maxRetries: 3, sendCookies: false);

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
                    System.Diagnostics.Debug.WriteLine("[LOGIN] ?? 登录成功但未能捕获MUSIC_U");
                }

                if (string.IsNullOrEmpty(cookieString))
                {
                    System.Diagnostics.Debug.WriteLine("[LOGIN] ?? Cookie 快照为空，后续请求可能无法使用登录态");
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
        /// ??? 在登录成功后调用，确保Cookie完全同步并进行会话预热
        /// </summary>
        /// <param name="loginResult">登录结果</param>
        public async Task CompleteLoginAsync(LoginResult loginResult)
        {
            if (loginResult == null || loginResult.Code != 200)
            {
                System.Diagnostics.Debug.WriteLine("[CompleteLogin] ?? 登录未成功，跳过初始化");
                return;
            }

            System.Diagnostics.Debug.WriteLine("[CompleteLogin] 开始登录后初始化...");

            try
            {
                // 1. 确保Cookie已完全更新（通常已在 FinalizeLoginCookies 中完成）
                UpdateCookies();
                System.Diagnostics.Debug.WriteLine("[CompleteLogin] ? Cookie已同步");

                // 2. 会话预热 - 向服务器发送当前账户数据，避免后续风控
                // 注意：账户信息保存由 LoginForm 调用 ApplyLoginProfile 完成，这里只做预热
                System.Diagnostics.Debug.WriteLine("[CompleteLogin] 开始会话预热...");
                await WarmupSessionAsync();
                System.Diagnostics.Debug.WriteLine("[CompleteLogin] ? 会话预热完成");

                System.Diagnostics.Debug.WriteLine("[CompleteLogin] ??? 登录初始化全部完成！");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CompleteLogin] ?? 初始化过程出现异常（不影响登录）: {ex.Message}");
            }
        }
    }

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
}
#pragma warning restore CS8600, CS8601, CS8602, CS8603, CS8604, CS8625




namespace YTPlayer.Core
{
    /// <summary>
    /// 负责 account.json 的读写，统一管理登录状态和会话数据。
    /// account.json 包含：登录状态、用户信息、Cookie、设备指纹等。
    /// 用户可手动修改 IsLoggedIn 字段来测试不同登录状态。
    /// </summary>
    public class AccountStateStore
    {
        private readonly string _accountFilePath;

        public AccountStateStore()
        {
            string applicationDirectory = GetApplicationDirectory();
            _accountFilePath = Path.Combine(applicationDirectory, "account.json");
        }

        /// <summary>
        /// 加载 account.json。
        /// 如果文件不存在或读取失败，返回未登录状态。
        /// 如果 IsLoggedIn=false，程序将忽略其他账号数据，以未登录模式运行。
        /// </summary>
        public AccountState Load()
        {
            try
            {
                if (!File.Exists(_accountFilePath))
                {
                    System.Diagnostics.Debug.WriteLine("[AccountStateStore] account.json 不存在，返回未登录状态");
                    return CreateEmptyState();
                }

                string json = File.ReadAllText(_accountFilePath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    System.Diagnostics.Debug.WriteLine("[AccountStateStore] account.json 为空，返回未登录状态");
                    return CreateEmptyState();
                }

                var state = JsonConvert.DeserializeObject<AccountState>(json);
                if (state == null)
                {
                    System.Diagnostics.Debug.WriteLine("[AccountStateStore] account.json 反序列化失败，返回未登录状态");
                    return CreateEmptyState();
                }

                // 确保集合不为空
                state.Cookies = state.Cookies ?? new List<CookieItem>();

                // 如果 IsLoggedIn=false，清空敏感数据（允许用户手动设置此标志来测试）
                if (!state.IsLoggedIn)
                {
                    System.Diagnostics.Debug.WriteLine("[AccountStateStore] IsLoggedIn=false，忽略账号数据");
                    // 保留设备指纹，但清空用户和认证信息
                    state.UserId = null;
                    state.Nickname = null;
                    state.AvatarUrl = null;
                    state.VipType = 0;
                    state.Cookie = null;
                    state.MusicU = null;
                    state.CsrfToken = null;
                    state.Cookies.Clear();
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[AccountStateStore] 加载登录状态成功：用户={state.Nickname}, ID={state.UserId}, VipType={state.VipType}");
                }

                return state;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AccountStateStore] 读取 account.json 失败: {ex.Message}");
                return CreateEmptyState();
            }
        }

        /// <summary>
        /// 保存 AccountState 到 account.json。
        /// 使用原子写入方式（先写临时文件，再替换），并保留备份。
        /// </summary>
        public void Save(AccountState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            try
            {
                // 更新时间戳
                state.LastUpdated = DateTimeOffset.UtcNow;
                state.Cookies = state.Cookies ?? new List<CookieItem>();

                // 确保目录存在
                string directory = Path.GetDirectoryName(_accountFilePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // 序列化配置
                var settings = new JsonSerializerSettings
                {
                    Formatting = Formatting.Indented,
                    NullValueHandling = NullValueHandling.Include  // 包含 null 值，以便用户清楚看到字段结构
                };

                string json = JsonConvert.SerializeObject(state, settings);

                // 原子写入：先写临时文件，再覆盖，取消 .bak 生成以精简目录
                string tempFile = _accountFilePath + ".tmp";
                File.WriteAllText(tempFile, json);

                File.Copy(tempFile, _accountFilePath, true);
                File.Delete(tempFile);

                // 清理历史 .bak
                string bakFile = _accountFilePath + ".bak";
                if (File.Exists(bakFile))
                {
                    File.Delete(bakFile);
                }

                if (state.IsLoggedIn)
                {
                    System.Diagnostics.Debug.WriteLine($"[AccountStateStore] 保存登录状态成功：用户={state.Nickname}");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[AccountStateStore] 保存未登录状态");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AccountStateStore] 保存 account.json 失败: {ex.Message}");
                throw;  // 抛出异常，让调用者知道保存失败
            }
        }

        /// <summary>
        /// 清空登录状态。
        /// 将 IsLoggedIn 设置为 false，清空用户和认证信息，但保留设备指纹。
        /// 不删除文件，允许保留设备指纹以便下次登录使用。
        /// </summary>
        public void Clear()
        {
            System.Diagnostics.Debug.WriteLine("[AccountStateStore] 清空登录状态");
            try
            {
                // 尝试加载现有状态以保留设备指纹
                AccountState state;
                if (File.Exists(_accountFilePath))
                {
                    try
                    {
                        string json = File.ReadAllText(_accountFilePath);
                        state = JsonConvert.DeserializeObject<AccountState>(json) ?? CreateEmptyState();
                    }
                    catch
                    {
                        state = CreateEmptyState();
                    }
                }
                else
                {
                    state = CreateEmptyState();
                }

                // 清空登录状态和用户信息，但保留设备指纹
                state.IsLoggedIn = false;
                state.UserId = null;
                state.Nickname = null;
                state.AvatarUrl = null;
                state.VipType = 0;
                state.Cookie = null;
                state.MusicU = null;
                state.CsrfToken = null;
                state.Cookies?.Clear();

                // 保留设备指纹字段：
                // DeviceId, SDeviceId, DeviceMachineId, DeviceOs, DeviceOsVersion,
                // DeviceAppVersion, DeviceBuildVersion, DeviceVersionCode, DeviceUserAgent,
                // DeviceResolution, DeviceChannel, DeviceMobileName, DesktopUserAgent,
                // DeviceMark, DeviceMConfigInfo, MusicA, NmtId, NtesNuid, WnmCid,
                // AntiCheatToken, AntiCheatTokenGeneratedAt, AntiCheatTokenExpiresAt

                Save(state);
                System.Diagnostics.Debug.WriteLine("[AccountStateStore] 登录状态已清空，设备指纹已保留");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AccountStateStore] 清空登录状态失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 检查 account.json 是否存在
        /// </summary>
        public bool Exists()
        {
            return File.Exists(_accountFilePath);
        }

        /// <summary>
        /// 获取 account.json 文件路径
        /// </summary>
        public string GetFilePath()
        {
            return _accountFilePath;
        }

        /// <summary>
        /// 创建空的登录状态（未登录）
        /// </summary>
        private static AccountState CreateEmptyState()
        {
            return new AccountState
            {
                IsLoggedIn = false,
                Cookies = new List<CookieItem>()
            };
        }

        /// <summary>
        /// 获取应用程序目录。
        /// 使用启动器传入的根目录，确保 account.json 与程序放置在同一目录。
        /// </summary>
        private static string GetApplicationDirectory()
        {
            try
            {
                return YTPlayer.Utils.PathHelper.ApplicationRootDirectory;
            }
            catch
            {
                return Directory.GetCurrentDirectory();
            }
        }
    }
}




namespace YTPlayer.Models.Auth
{
    /// <summary>
    /// 表示一次二维码登录会话。
    /// </summary>
    public class QrLoginSession
    {
        public string? Key { get; set; }
        public string? Url { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public int? ExpireInSeconds { get; set; }
    }

    /// <summary>
    /// 二维码轮询状态。
    /// </summary>
    public enum QrLoginState
    {
        WaitingForScan,
        AwaitingConfirmation,
        Authorized,
        Expired,
        Canceled,
        RiskControl,
        Error
    }

    /// <summary>
    /// 二维码轮询结果。
    /// </summary>
    public class QrLoginPollResult
    {
        public QrLoginState State { get; set; }
        public string? Message { get; set; }
        public string? Cookie { get; set; }
        public string? RedirectUrl { get; set; }
        public int RawCode { get; set; }
    }

    /// <summary>
    /// 登录状态查询结果。
    /// </summary>
    public class LoginStatusResult
    {
        public bool IsLoggedIn { get; set; }
        public long? AccountId { get; set; }
        public string? Nickname { get; set; }
        public int VipType { get; set; }
        public string? AvatarUrl { get; set; }
        public UserAccountInfo? AccountDetail { get; set; }
        public string? RawJson { get; set; }
    }

    /// <summary>
    /// 本地账户状态存档（account.json）。
    /// 包含登录状态、用户信息、Cookie、设备指纹等所有会话相关数据。
    /// 用户可以手动修改 IsLoggedIn 来测试不同登录状态（false时忽略账号数据）。
    /// </summary>
    public class AccountState
    {
        /// <summary>
        /// 登录状态标志。
        /// 用户可手动设置为 false 来测试未登录模式（程序将忽略本文件的其他账号数据）。
        /// </summary>
        public bool IsLoggedIn { get; set; }

        #region 用户信息

        /// <summary>用户ID</summary>
        public string? UserId { get; set; }

        /// <summary>用户昵称</summary>
        public string? Nickname { get; set; }

        /// <summary>头像URL</summary>
        public string? AvatarUrl { get; set; }

        /// <summary>VIP类型（0=普通，1=VIP，11=黑胶VIP）</summary>
        public int VipType { get; set; }

        /// <summary>
        /// 最近一次同步的完整账号详情。
        /// 如果可用，可用于获取用户 ID、昵称等冗余信息。
        /// </summary>
        public UserAccountInfo? AccountDetail { get; set; }

        #endregion

        #region Cookie 和认证凭证

        /// <summary>完整的 Cookie 字符串</summary>
        public string? Cookie { get; set; }

        /// <summary>MUSIC_U Cookie（主要登录凭证）</summary>
        public string? MusicU { get; set; }

        /// <summary>CSRF Token</summary>
        public string? CsrfToken { get; set; }

        /// <summary>详细的 Cookie 列表</summary>
        public List<CookieItem> Cookies { get; set; } = new List<CookieItem>();

        #endregion

        #region 设备指纹和会话信息

        /// <summary>设备ID</summary>
        public string? DeviceId { get; set; }

        /// <summary>辅助设备ID</summary>
        public string? SDeviceId { get; set; }

        /// <summary>设备型号标识</summary>
        public string? DeviceMachineId { get; set; }

        /// <summary>设备操作系统</summary>
        public string? DeviceOs { get; set; }

        /// <summary>设备操作系统版本</summary>
        public string? DeviceOsVersion { get; set; }

        /// <summary>客户端版本号</summary>
        public string? DeviceAppVersion { get; set; }

        /// <summary>客户端构建号</summary>
        public string? DeviceBuildVersion { get; set; }

        /// <summary>客户端版本代码</summary>
        public string? DeviceVersionCode { get; set; }

        /// <summary>客户端 User-Agent</summary>
        public string? DeviceUserAgent { get; set; }

        /// <summary>客户端分辨率</summary>
        public string? DeviceResolution { get; set; }

        /// <summary>客户端渠道标识</summary>
        public string? DeviceChannel { get; set; }

        /// <summary>设备型号名称</summary>
        public string? DeviceMobileName { get; set; }

        /// <summary>桌面端 User-Agent</summary>
        public string? DesktopUserAgent { get; set; }

        /// <summary>自定义标记</summary>
        public string? DeviceMark { get; set; }

        /// <summary>移动配置描述</summary>
        public string? DeviceMConfigInfo { get; set; }

        /// <summary>匿名访问令牌（MUSIC_A）</summary>
        public string? MusicA { get; set; }

        /// <summary>访客设备标识（NMTID）</summary>
        public string? NmtId { get; set; }

        /// <summary>网易访客追踪ID（_ntes_nuid）</summary>
        public string? NtesNuid { get; set; }

        /// <summary>网易云客户端标识（WNMCID）</summary>
        public string? WnmCid { get; set; }

        /// <summary>反作弊令牌</summary>
        public string? AntiCheatToken { get; set; }

        /// <summary>反作弊令牌生成时间</summary>
        public DateTimeOffset? AntiCheatTokenGeneratedAt { get; set; }

        /// <summary>反作弊令牌过期时间</summary>
        public DateTimeOffset? AntiCheatTokenExpiresAt { get; set; }

        #endregion

        #region 元数据

        /// <summary>最后更新时间</summary>
        public DateTimeOffset? LastUpdated { get; set; }

        /// <summary>指纹最近刷新时间</summary>
        public DateTimeOffset? FingerprintLastUpdated { get; set; }

        #endregion
    }
}


