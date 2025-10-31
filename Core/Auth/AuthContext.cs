using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using YTPlayer.Models;
using YTPlayer.Utils;
using YTPlayer.Models.Auth;
using YTPlayer.Core;

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
        /// 从 account.json 恢复设备指纹到 config.json。
        /// 这样可以确保同一设备在多次登录后保持一致的指纹。
        /// </summary>
        private void RestoreDeviceFingerprintFromAccountState()
        {
            if (_accountState == null)
            {
                return;
            }

            bool changed = false;

            // 恢复设备指纹字段
            if (!string.IsNullOrEmpty(_accountState.DeviceId) && string.IsNullOrEmpty(_config.DeviceId))
            {
                _config.DeviceId = _accountState.DeviceId;
                changed = true;
            }

            if (!string.IsNullOrEmpty(_accountState.SDeviceId) && string.IsNullOrEmpty(_config.SDeviceId))
            {
                _config.SDeviceId = _accountState.SDeviceId;
                changed = true;
            }

            if (!string.IsNullOrEmpty(_accountState.DeviceMachineId) && string.IsNullOrEmpty(_config.DeviceMachineId))
            {
                _config.DeviceMachineId = _accountState.DeviceMachineId;
                changed = true;
            }

            if (!string.IsNullOrEmpty(_accountState.DeviceOs) && string.IsNullOrEmpty(_config.DeviceOs))
            {
                _config.DeviceOs = _accountState.DeviceOs;
                changed = true;
            }

            if (!string.IsNullOrEmpty(_accountState.DeviceOsVersion) && string.IsNullOrEmpty(_config.DeviceOsVersion))
            {
                _config.DeviceOsVersion = _accountState.DeviceOsVersion;
                changed = true;
            }

            if (!string.IsNullOrEmpty(_accountState.DeviceAppVersion) && string.IsNullOrEmpty(_config.DeviceAppVersion))
            {
                _config.DeviceAppVersion = _accountState.DeviceAppVersion;
                changed = true;
            }

            if (!string.IsNullOrEmpty(_accountState.DeviceBuildVersion) && string.IsNullOrEmpty(_config.DeviceBuildVersion))
            {
                _config.DeviceBuildVersion = _accountState.DeviceBuildVersion;
                changed = true;
            }

            if (!string.IsNullOrEmpty(_accountState.NmtId) && string.IsNullOrEmpty(_config.NmtId))
            {
                _config.NmtId = _accountState.NmtId;
                changed = true;
            }

            if (!string.IsNullOrEmpty(_accountState.NtesNuid) && string.IsNullOrEmpty(_config.NtesNuid))
            {
                _config.NtesNuid = _accountState.NtesNuid;
                changed = true;
            }

            if (!string.IsNullOrEmpty(_accountState.WnmCid) && string.IsNullOrEmpty(_config.WnmCid))
            {
                _config.WnmCid = _accountState.WnmCid;
                changed = true;
            }

            if (!string.IsNullOrEmpty(_accountState.AntiCheatToken) && string.IsNullOrEmpty(_config.AntiCheatToken))
            {
                _config.AntiCheatToken = _accountState.AntiCheatToken;
                _config.AntiCheatTokenGeneratedAt = _accountState.AntiCheatTokenGeneratedAt;
                _config.AntiCheatTokenExpiresAt = _accountState.AntiCheatTokenExpiresAt;
                changed = true;
            }

            if (changed)
            {
                System.Diagnostics.Debug.WriteLine("[AuthContext] 已从 account.json 恢复设备指纹到 config.json");
                _configManager.Save(_config);
            }
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
                            IsLoggedIn = false,
                            AntiCheatToken = _config.AntiCheatToken,
                            AntiCheatTokenExpiresAt = _config.AntiCheatTokenExpiresAt
                        };
                    }
                    else if (string.IsNullOrEmpty(_accountState.AntiCheatToken) && AntiCheatTokenUtility.IsValid(_config.AntiCheatToken))
                    {
                        _accountState.AntiCheatToken = _config.AntiCheatToken;
                        _accountState.AntiCheatTokenExpiresAt = _config.AntiCheatTokenExpiresAt;
                    }

                    return _accountState;
                }
            }
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
        /// 更新 config.json（用于向后兼容）和 account.json。
        /// </summary>
        internal void ApplyLoginProfile(UserAccountInfo profile, string musicU, string csrfToken)
        {
            lock (_syncRoot)
            {
                bool configChanged = false;

                // 更新 config.json 中的登录信息（向后兼容）
                if (!string.IsNullOrWhiteSpace(musicU) && !string.Equals(_config.MusicU, musicU, StringComparison.Ordinal))
                {
                    _config.MusicU = musicU;
                    configChanged = true;
                }

                if (!string.IsNullOrWhiteSpace(csrfToken) && !string.Equals(_config.CsrfToken, csrfToken, StringComparison.Ordinal))
                {
                    _config.CsrfToken = csrfToken;
                    configChanged = true;
                }

                if (profile != null)
                {
                    if (profile.UserId > 0)
                    {
                        string userId = profile.UserId.ToString();
                        if (!string.Equals(_config.LoginUserId, userId, StringComparison.Ordinal))
                        {
                            _config.LoginUserId = userId;
                            configChanged = true;
                        }
                    }

                    if (!string.IsNullOrEmpty(profile.Nickname) && !string.Equals(_config.LoginUserNickname, profile.Nickname, StringComparison.Ordinal))
                    {
                        _config.LoginUserNickname = profile.Nickname;
                        configChanged = true;
                    }

                    if (!string.IsNullOrEmpty(profile.AvatarUrl) && !string.Equals(_config.LoginAvatarUrl, profile.AvatarUrl, StringComparison.Ordinal))
                    {
                        _config.LoginAvatarUrl = profile.AvatarUrl;
                        configChanged = true;
                    }

                    if (profile.VipType != 0 && _config.LoginVipType != profile.VipType)
                    {
                        _config.LoginVipType = profile.VipType;
                        configChanged = true;
                    }
                }

                if (configChanged)
                {
                    _configManager.Save(_config);
                }

                // 同时更新 account.json
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
        /// 清理 config.json 和 account.json 中的登录信息，但保留设备指纹。
        /// </summary>
        internal void ClearLoginProfile()
        {
            lock (_syncRoot)
            {
                // 清空 config.json 中的登录信息
                _config.MusicU = null;
                _config.CsrfToken = null;
                _config.LoginUserId = null;
                _config.LoginUserNickname = null;
                _config.LoginAvatarUrl = null;
                _config.LoginVipType = 0;
                _configManager.Save(_config);
                System.Diagnostics.Debug.WriteLine("[AuthContext] 已清空 config.json 中的登录信息");

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

                    // 用户信息
                    UserId = _config.LoginUserId,
                    Nickname = _config.LoginUserNickname,
                    AvatarUrl = _config.LoginAvatarUrl,
                    VipType = _config.LoginVipType,

                    // Cookie 和认证凭证
                    Cookie = cookieSnapshot ?? string.Empty,
                    MusicU = _config.MusicU,
                    CsrfToken = _config.CsrfToken,
                    Cookies = cookies != null ? new List<CookieItem>(cookies) : new List<CookieItem>(),

                    // 设备指纹和会话信息
                    DeviceId = _config.DeviceId,
                    SDeviceId = _config.SDeviceId,
                    DeviceMachineId = _config.DeviceMachineId,
                    DeviceOs = _config.DeviceOs,
                    DeviceOsVersion = _config.DeviceOsVersion,
                    DeviceAppVersion = _config.DeviceAppVersion,
                    DeviceBuildVersion = _config.DeviceBuildVersion,
                    DeviceVersionCode = _config.DeviceVersionCode,
                    DeviceUserAgent = _config.DeviceUserAgent,
                    DeviceResolution = _config.DeviceResolution,
                    DeviceChannel = _config.DeviceChannel,
                    DeviceMobileName = _config.DeviceMobileName,
                    DesktopUserAgent = _config.DesktopUserAgent,
                    DeviceMark = _config.DeviceMark,
                    DeviceMConfigInfo = _config.DeviceMConfigInfo,
                    MusicA = _config.MusicA,
                    NmtId = _config.NmtId,
                    NtesNuid = _config.NtesNuid,
                    WnmCid = _config.WnmCid,
                    AntiCheatToken = _config.AntiCheatToken,
                    AntiCheatTokenGeneratedAt = _config.AntiCheatTokenGeneratedAt,
                    AntiCheatTokenExpiresAt = _config.AntiCheatTokenExpiresAt,

                    // 元数据
                    LastUpdated = DateTimeOffset.UtcNow,
                    FingerprintLastUpdated = _config.FingerprintLastUpdated
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
            bool changed = false;

            lock (_syncRoot)
            {
                EnsureDeviceProfileInternal(ref changed);

                if (string.IsNullOrWhiteSpace(_config.NmtId))
                {
                    _config.NmtId = EncryptionHelper.GenerateRandomHex(32);
                    changed = true;
                }

                if (string.IsNullOrWhiteSpace(_config.NtesNuid))
                {
                    _config.NtesNuid = EncryptionHelper.GenerateRandomHex(32);
                    changed = true;
                }

                if (string.IsNullOrWhiteSpace(_config.WnmCid))
                {
                    _config.WnmCid = EncryptionHelper.GenerateWNMCID();
                    changed = true;
                }

                if (string.IsNullOrWhiteSpace(_config.MusicA))
                {
                    _config.MusicA = AuthConstants.AnonymousToken;
                    changed = true;
                }

                if (!AntiCheatTokenUtility.IsValid(_config.AntiCheatToken))
                {
                    _config.AntiCheatToken = AntiCheatTokenUtility.Generate();
                    _config.AntiCheatTokenGeneratedAt = DateTimeOffset.UtcNow;
                    _config.AntiCheatTokenExpiresAt = _config.AntiCheatTokenGeneratedAt.Value + AuthConstants.AntiCheatTokenLifetime;
                    changed = true;
                }
                else
                {
                    if (!_config.AntiCheatTokenGeneratedAt.HasValue)
                    {
                        _config.AntiCheatTokenGeneratedAt = DateTimeOffset.UtcNow;
                        changed = true;
                    }

                    if (!_config.AntiCheatTokenExpiresAt.HasValue && _config.AntiCheatTokenGeneratedAt.HasValue)
                    {
                        _config.AntiCheatTokenExpiresAt = _config.AntiCheatTokenGeneratedAt.Value + AuthConstants.AntiCheatTokenLifetime;
                        changed = true;
                    }
                }

                if (!_config.FingerprintLastUpdated.HasValue)
                {
                    _config.FingerprintLastUpdated = DateTimeOffset.UtcNow;
                    changed = true;
                }

                if (changed)
                {
                    _config.FingerprintLastUpdated = DateTimeOffset.UtcNow;
                    _configManager.Save(_config);
                }
            }
        }

        internal string GetActiveAntiCheatToken()
        {
            lock (_syncRoot)
            {
                var expiresAt = _config.AntiCheatTokenExpiresAt ??
                                (_config.AntiCheatTokenGeneratedAt?.Add(AuthConstants.AntiCheatTokenLifetime));

                bool needRefresh = !AntiCheatTokenUtility.IsValid(_config.AntiCheatToken) ||
                                   !expiresAt.HasValue ||
                                   DateTimeOffset.UtcNow >= expiresAt.Value;

                if (needRefresh)
                {
                    var newToken = AntiCheatTokenUtility.Generate();
                    ApplyAntiCheatTokenInternal(newToken, AuthConstants.AntiCheatTokenLifetime);
                }
                else if (!_config.AntiCheatTokenExpiresAt.HasValue && expiresAt.HasValue)
                {
                    _config.AntiCheatTokenExpiresAt = expiresAt;
                    _configManager.Save(_config);
                }

                return _config.AntiCheatToken;
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
        /// ⭐⭐⭐ 移动端重构：只返回登录凭证，设备参数通过 EAPI header 传递
        /// 参照参考项目：config.cookies 初始为空，登录后只保存 MUSIC_U 和 __csrf
        /// </summary>
        internal IReadOnlyDictionary<string, string> BuildBaseCookieMap(bool includeAnonymousToken)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // ⭐ 只保留 CSRF token (如果存在)
            // 移除所有设备指纹 Cookie：os, osver, appver, buildver, channel, deviceId, sDeviceId, NMTID, _ntes_nuid, WNMCID
            // 这些参数现在通过 EAPI header 传递，不再混入 Cookie
            if (!string.IsNullOrEmpty(_config.CsrfToken))
            {
                map["__csrf"] = _config.CsrfToken;
            }

            // ⭐ 移除匿名令牌 (MUSIC_A) - 不再需要
            // 移动端 EAPI 不依赖此令牌

            return map;
        }

        internal void SyncFromCookies(CookieCollection cookies)
        {
            if (cookies == null || cookies.Count == 0)
            {
                return;
            }

            bool changed = false;

            lock (_syncRoot)
            {
                string UpdateIfChanged(string name, string current)
                {
                    var value = cookies[name]?.Value;
                    if (!string.IsNullOrEmpty(value) && !string.Equals(value, current, StringComparison.Ordinal))
                    {
                        changed = true;
                        return value;
                    }

                    return current;
                }

                _config.NmtId = UpdateIfChanged("NMTID", _config.NmtId);
                _config.NtesNuid = UpdateIfChanged("_ntes_nuid", _config.NtesNuid);
                _config.WnmCid = UpdateIfChanged("WNMCID", _config.WnmCid);
                _config.MusicA = UpdateIfChanged("MUSIC_A", _config.MusicA);
                _config.CsrfToken = UpdateIfChanged("__csrf", _config.CsrfToken);
                _config.DeviceId = UpdateIfChanged("deviceId", _config.DeviceId);
                _config.SDeviceId = UpdateIfChanged("sDeviceId", _config.SDeviceId);

                if (changed)
                {
                    _config.FingerprintLastUpdated = DateTimeOffset.UtcNow;
                    _configManager.Save(_config);
                }
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
                    _config.FingerprintLastUpdated = DateTimeOffset.UtcNow;
                    _configManager.Save(_config);
                }

                if (useMobileMode)
                {
                    var osFlag = (_config.DeviceOs ?? AuthConstants.MobileOs).Equals("Android", StringComparison.OrdinalIgnoreCase)
                        ? "android"
                        : "ios";
                    return new Dictionary<string, object>
                    {
                        { "os", osFlag },
                        { "appver", _config.DeviceAppVersion ?? AuthConstants.MobileAppVersion },
                        { "osver", _config.DeviceOsVersion ?? AuthConstants.MobileOsVersion },
                        { "deviceId", _config.DeviceId },
                        { "sDeviceId", _config.SDeviceId ?? _config.DeviceId },
                        { "buildver", _config.DeviceBuildVersion ?? AuthConstants.MobileBuildVersion },
                        { "versioncode", _config.DeviceVersionCode ?? AuthConstants.DefaultMobileVersionCode },
                        { "resolution", _config.DeviceResolution ?? AuthConstants.DefaultMobileResolution },
                        { "channel", _config.DeviceChannel ?? AuthConstants.DefaultMobileChannel },
                        { "machine", _config.DeviceMachineId ?? AuthConstants.MobileMachineId },
                        { "mobilename", _config.DeviceMobileName ?? _config.DeviceMachineId ?? AuthConstants.DefaultMobileName },
                        { "requestId", EncryptionHelper.GenerateRequestId() },
                        { "__csrf", _config.CsrfToken ?? string.Empty }
                    };
                }

                return new Dictionary<string, object>
                {
                    { "os", "pc" },
                    { "appver", AuthConstants.DesktopAppVersion },
                    { "osver", _resolvedOsVersion },
                    { "deviceId", _config.DeviceId },
                    { "requestId", EncryptionHelper.GenerateRequestId() },
                    { "__csrf", _config.CsrfToken ?? string.Empty }
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
                    _config.FingerprintLastUpdated = DateTimeOffset.UtcNow;
                    _configManager.Save(_config);
                }

                var deviceOs = _config.DeviceOs ?? AuthConstants.MobileOs;
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Accept"] = "*/*",
                    ["Accept-Encoding"] = "gzip, deflate, br",
                    ["Accept-Language"] = "zh-Hans-CN;q=1, en-CN;q=0.9",
                    ["Connection"] = "keep-alive",
                    ["User-Agent"] = _config.DeviceUserAgent ?? AuthConstants.MobileUserAgent,
                    ["x-appver"] = _config.DeviceAppVersion ?? AuthConstants.MobileAppVersion,
                    ["x-buildver"] = _config.DeviceBuildVersion ?? AuthConstants.MobileBuildVersion,
                    ["x-os"] = deviceOs,
                    ["x-osver"] = _config.DeviceOsVersion ?? AuthConstants.MobileOsVersion,
                    ["x-deviceId"] = _config.DeviceId,
                    ["x-sDeviceId"] = _config.SDeviceId ?? _config.DeviceId,
                    ["x-versioncode"] = _config.DeviceVersionCode ?? AuthConstants.DefaultMobileVersionCode,
                    ["x-resolution"] = _config.DeviceResolution ?? AuthConstants.DefaultMobileResolution,
                    ["x-channel"] = _config.DeviceChannel ?? AuthConstants.DefaultMobileChannel,
                    ["x-aeapi"] = "true",
                    ["x-netlib"] = "Cronet",
                    ["X-MAM-CustomMark"] = _config.DeviceMark ?? AuthConstants.MobileCustomMark,
                    ["MConfig-Info"] = _config.DeviceMConfigInfo ?? AuthConstants.MobileMConfigInfo,
                    ["x-machineid"] = _config.DeviceMachineId ?? AuthConstants.MobileMachineId,
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
            if (string.IsNullOrWhiteSpace(_config.DeviceId))
            {
                _config.DeviceId = Guid.NewGuid().ToString("N");
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(_config.SDeviceId))
            {
                _config.SDeviceId = Guid.NewGuid().ToString("N");
                changed = true;
            }

            string presetSeed = !string.IsNullOrEmpty(_config.DeviceId)
                ? _config.DeviceId
                : _config.SDeviceId ?? Guid.NewGuid().ToString("N");

            var preset = AuthConstants.GetMobilePreset(presetSeed);

            if (string.IsNullOrWhiteSpace(_config.DeviceMachineId))
            {
                _config.DeviceMachineId = preset.MachineId;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(_config.DeviceOs))
            {
                _config.DeviceOs = preset.Platform;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(_config.DeviceOsVersion))
            {
                _config.DeviceOsVersion = preset.OsVersion;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(_config.DeviceAppVersion))
            {
                _config.DeviceAppVersion = preset.AppVersion;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(_config.DeviceBuildVersion))
            {
                _config.DeviceBuildVersion = preset.BuildVersion;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(_config.DeviceVersionCode))
            {
                _config.DeviceVersionCode = preset.VersionCode ?? AuthConstants.DefaultMobileVersionCode;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(_config.DeviceResolution))
            {
                _config.DeviceResolution = preset.Resolution ?? AuthConstants.DefaultMobileResolution;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(_config.DeviceChannel))
            {
                _config.DeviceChannel = preset.Channel ?? AuthConstants.DefaultMobileChannel;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(_config.DeviceMobileName))
            {
                _config.DeviceMobileName = preset.MobileName ?? _config.DeviceMachineId ?? AuthConstants.DefaultMobileName;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(_config.DeviceMark))
            {
                _config.DeviceMark = AuthConstants.GetDeviceMark(presetSeed);
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(_config.DeviceMConfigInfo))
            {
                _config.DeviceMConfigInfo = AuthConstants.MobileMConfigInfo;
                changed = true;
            }

            var desiredUa = !string.IsNullOrEmpty(preset.UserAgent)
                ? preset.UserAgent
                : AuthConstants.BuildMobileUserAgent(
                    _config.DeviceAppVersion,
                    _config.DeviceBuildVersion,
                    _config.DeviceOsVersion);

            if (string.IsNullOrWhiteSpace(_config.DeviceUserAgent) ||
                !string.Equals(_config.DeviceUserAgent, desiredUa, StringComparison.Ordinal))
            {
                _config.DeviceUserAgent = desiredUa;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(_config.DesktopUserAgent))
            {
                _config.DesktopUserAgent = AuthConstants.GetDesktopUserAgent(presetSeed);
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

            _config.AntiCheatToken = token;
            _config.AntiCheatTokenGeneratedAt = now;
            _config.AntiCheatTokenExpiresAt = expiresAt;
            _config.FingerprintLastUpdated = now;

            try
            {
                _configManager.Save(_config);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AuthContext] 保存配置中的反作弊令牌失败: {ex.Message}");
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
}
