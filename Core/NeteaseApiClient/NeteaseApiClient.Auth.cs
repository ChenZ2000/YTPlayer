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
                { "type", 3 }
            };

            var antiCheatToken = _authContext?.GetActiveAntiCheatToken();
            if (!string.IsNullOrEmpty(antiCheatToken))
            {
                payload["antiCheatToken"] = antiCheatToken;
            }

            System.Diagnostics.Debug.WriteLine("[QR LOGIN] 请求新的二维码登录会话 (type=3)");
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
                Url = BuildQrLoginUrl(unikey),
                CreatedAt = DateTimeOffset.UtcNow,
                ExpireInSeconds = result["endTime"]?.Value<int?>()
            };

            System.Diagnostics.Debug.WriteLine($"[QR LOGIN] 二维码会话创建成功, key={session.Key}");
            return session;
        }

        private string BuildQrLoginUrl(string unikey)
        {
            string url = $"https://music.163.com/login?codekey={unikey}";
            string chainId = BuildQrChainId();
            if (!string.IsNullOrWhiteSpace(chainId))
            {
                url = $"{url}&chainId={Uri.EscapeDataString(chainId)}";
            }
            return url;
        }

        private string BuildQrChainId()
        {
            const string version = "v1";
            string deviceId = GetCookieValue("sDeviceId")
                ?? _authContext?.CurrentAccountState?.SDeviceId
                ?? _authContext?.CurrentAccountState?.DeviceId
                ?? string.Empty;
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                deviceId = $"unknown-{GetRandomChainSuffix()}";
            }

            const string platform = "web";
            const string action = "login";
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return $"{version}_{deviceId}_{platform}_{action}_{timestamp}";
        }

        private int GetRandomChainSuffix()
        {
            lock (_random)
            {
                return _random.Next(1, 1_000_000);
            }
        }

        private string? GetCookieValue(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            try
            {
                var cookies = _cookieContainer.GetCookies(MUSIC_URI);
                if (cookies == null || cookies.Count == 0)
                {
                    return null;
                }

                foreach (Cookie cookie in cookies)
                {
                    if (cookie == null)
                    {
                        continue;
                    }

                    if (string.Equals(cookie.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return cookie.Value;
                    }
                }
            }
            catch
            {
            }

            return null;
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
                { "type", 3 }
            };

            var antiCheatToken = _authContext?.GetActiveAntiCheatToken();
            if (!string.IsNullOrEmpty(antiCheatToken))
            {
                payload["antiCheatToken"] = antiCheatToken;
            }

            System.Diagnostics.Debug.WriteLine($"[QR LOGIN] 轮询二维码状态 (WEAPI type=3), key={key}");

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
                // 2. 清理本地登录状态并重建匿名会话
                ResetToAnonymousSession(clearAccountState: true);

                System.Diagnostics.Debug.WriteLine("[Logout] ✅✅✅ 退出登录完成，已切换到匿名会话");
            }
        }

        /// <summary>
        /// 发送短信验证码（手机号登录）
        /// 1.7.3 行为：iOS UA + 访客 Cookie（过滤桌面字段）
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
        /// ⭐ 参考 netease-music-simple-player/Net/NetClasses.cs:2521-2541
        /// 关键: 使用 iOS User-Agent 避免 -462 风控错误
        /// </summary>
        public async Task<LoginResult> LoginByCaptchaAsync(string phone, string captcha, string ctcode = "86")
        {
            // 刷新移动端访客会话，确保 MUSIC_A/NMTID 最新，降低 -462
            try
            {
                await EnsureMobileVisitorSessionAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LOGIN] 获取访客会话失败（忽略继续）: {ex.Message}");
            }
            // ⭐ 重要：不再在 Cookie 中设置 os=ios 和 appver=8.7.01
            // 因为 PostWeApiWithiOSAsync 已经使用 iOS User-Agent
            // 在 Cookie 中设置 os=ios 会与桌面系统的其他 Cookie 冲突，触发风控

            // ⭐ 核心修复：使用与发码阶段一致的访客指纹和 Cookie，保持会话连续性
            // 参考项目 netease-music-simple-player/Net/NetClasses.cs:2525-2530
            // 附带 rememberLogin 与官方行为一致，避免服务端强制登出
            var payload = new Dictionary<string, object>
            {
                { "phone", phone },
                { "countrycode", ctcode },
                { "captcha", captcha },
                { "rememberLogin", true }
            };

            System.Diagnostics.Debug.WriteLine($"[LOGIN] 短信登录请求: phone={phone}, captcha={captcha}, countrycode={ctcode}");
            System.Diagnostics.Debug.WriteLine("[LOGIN] 使用 iOS User-Agent + 移动访客Cookie模式，保持验证码链路一致");

            // ⭐ 核心修复：登录请求发送移动端访客Cookie（sendCookies=true）
            // 模拟真实iPhone首次登录场景，避免桌面Cookie与iOS UA的设备指纹不匹配
            var result = await PostWeApiWithiOSAsync<JObject>("/login/cellphone", payload, maxRetries: 3, sendCookies: true);

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
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GetUserInfo] Failed: {ex.Message}");
            }

            return null;
        }

        #endregion

    }
}
