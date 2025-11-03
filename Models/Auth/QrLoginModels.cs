using System;
using System.Collections.Generic;
using YTPlayer.Models;

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


