using System;
using System.Collections.Generic;

namespace YTPlayer.Models
{
    /// <summary>
    /// 配置模型（参考 Python 版本 Netease-music.py:2505-2525）
    /// 支持便携式存储，所有配置保存在程序目录的 config.json
    /// </summary>
    public class ConfigModel
    {
        #region 基础设置

        /// <summary>
        /// 音量（0-1）
        /// Python: cfg.setdefault("volume", 0.5)
        /// </summary>
        public double Volume { get; set; } = 0.5;

        /// <summary>
        /// 播放顺序（顺序播放/列表循环/单曲循环/随机播放）
        /// Python: cfg.setdefault("playback_order", "顺序播放")
        /// </summary>
        public string PlaybackOrder { get; set; } = "顺序播放";

        /// <summary>
        /// 默认音质
        /// Python: cfg.setdefault("default_quality", "超清母带")
        /// </summary>
        public string DefaultQuality { get; set; } = "超清母带";

        #endregion

        #region 搜索和历史

        /// <summary>
        /// 搜索历史
        /// Python: cfg.setdefault("search_history", [])
        /// </summary>
        public List<string> SearchHistory { get; set; } = new List<string>();

        #endregion

        #region 下载设置

        /// <summary>
        /// 下载路径（null 表示使用默认路径）
        /// Python: cfg.setdefault("download_path", None)
        /// </summary>
        public string DownloadPath { get; set; }

        #endregion

        #region Cookie 和登录

        /// <summary>
        /// Cookie 列表（用于详细的 Cookie 管理）
        /// Python: cfg.setdefault("cookies", [])  # [{name,value,domain,path}, ...]
        /// </summary>
        public List<CookieItem> Cookies { get; set; } = new List<CookieItem>();

        /// <summary>
        /// MUSIC_U Cookie（主要登录凭证）
        /// 注意：UsePersonalCookie 已移除，现在根据 MusicU 是否为空自动判断登录状态
        /// </summary>
        public string MusicU { get; set; }

        /// <summary>
        /// CSRF Token
        /// </summary>
        public string CsrfToken { get; set; }

        /// <summary>
        /// 登录用户 ID（可选）
        /// Python: cfg.setdefault("login_user_id", None)
        /// </summary>
        public string LoginUserId { get; set; }

        /// <summary>
        /// 登录用户昵称（用于菜单显示）
        /// </summary>
        public string LoginUserNickname { get; set; }

        /// <summary>
        /// 登录用户头像地址
        /// </summary>
        public string LoginAvatarUrl { get; set; }

        /// <summary>
        /// 登录用户 VIP 类型
        /// </summary>
        public int LoginVipType { get; set; }

        /// <summary>
        /// 匿名访问令牌（对应 MUSIC_A）
        /// </summary>
        public string MusicA { get; set; }

        /// <summary>
        /// 访客设备标识（对应 NMTID）
        /// </summary>
        public string NmtId { get; set; }

        /// <summary>
        /// 网易访客追踪ID（对应 _ntes_nuid）
        /// </summary>
        public string NtesNuid { get; set; }

        /// <summary>
        /// 网易云客户端标识（对应 WNMCID）
        /// </summary>
        public string WnmCid { get; set; }

        /// <summary>
        /// 反作弊令牌（用于部分接口校验）
        /// </summary>
        public string AntiCheatToken { get; set; }

        /// <summary>
        /// 反作弊令牌生成时间
        /// </summary>
        public DateTimeOffset? AntiCheatTokenGeneratedAt { get; set; }

        /// <summary>
        /// 反作弊令牌过期时间
        /// </summary>
        public DateTimeOffset? AntiCheatTokenExpiresAt { get; set; }

        /// <summary>
        /// 指纹最近刷新时间
        /// </summary>
        public DateTimeOffset? FingerprintLastUpdated { get; set; }

        #endregion

        #region 设备信息

        /// <summary>
        /// 设备 ID（用于 EAPI，降低"脚本公共指纹"）
        /// Python: cfg.setdefault("device_id", uuid.uuid4().hex)
        /// </summary>
        public string DeviceId { get; set; }

        /// <summary>
        /// 辅助设备 ID（sDeviceId，用于移动端指纹）
        /// </summary>
        public string SDeviceId { get; set; }

        /// <summary>
        /// 设备型号标识（例如 iPhone17,1）
        /// </summary>
        public string DeviceMachineId { get; set; }

        /// <summary>
        /// 设备操作系统（iPhone OS / Android 等）
        /// </summary>
        public string DeviceOs { get; set; }

        /// <summary>
        /// 设备操作系统版本
        /// </summary>
        public string DeviceOsVersion { get; set; }

        /// <summary>
        /// 客户端版本号（AppVer）
        /// </summary>
        public string DeviceAppVersion { get; set; }

        /// <summary>
        /// 客户端构建号（BuildVer）
        /// </summary>
        public string DeviceBuildVersion { get; set; }

        /// <summary>
        /// 客户端版本代码（VersionCode）
        /// </summary>
        public string DeviceVersionCode { get; set; }

        /// <summary>
        /// 客户端 User-Agent
        /// </summary>
        public string DeviceUserAgent { get; set; }

        /// <summary>
        /// 客户端分辨率（用于移动端指纹）
        /// </summary>
        public string DeviceResolution { get; set; }

        /// <summary>
        /// 客户端渠道标识
        /// </summary>
        public string DeviceChannel { get; set; }

        /// <summary>
        /// 设备型号名称（移动端header中的 mobilename）
        /// </summary>
        public string DeviceMobileName { get; set; }

        /// <summary>
        /// 桌面端 User-Agent（用于PC模式请求）
        /// </summary>
        public string DesktopUserAgent { get; set; }

        /// <summary>
        /// 自定义标记（X-MAM-CustomMark）
        /// </summary>
        public string DeviceMark { get; set; }

        /// <summary>
        /// 移动配置描述（MConfig-Info）
        /// </summary>
        public string DeviceMConfigInfo { get; set; }

        #endregion

        #region UI 和交互设置

        /// <summary>
        /// 光标跟随播放
        /// Python: self.config['follow_cursor'] = True
        /// </summary>
        public bool FollowCursor { get; set; } = true;

        /// <summary>
        /// 跳转最小间隔（毫秒）
        /// Python: cfg.setdefault("seek_min_interval_ms", 30)
        /// </summary>
        public int SeekMinIntervalMs { get; set; } = 30;

        /// <summary>
        /// 窗口位置 X
        /// </summary>
        public int WindowX { get; set; } = -1;

        /// <summary>
        /// 窗口位置 Y
        /// </summary>
        public int WindowY { get; set; } = -1;

        /// <summary>
        /// 窗口宽度
        /// </summary>
        public int WindowWidth { get; set; } = 1200;

        /// <summary>
        /// 窗口高度
        /// </summary>
        public int WindowHeight { get; set; } = 800;

        /// <summary>
        /// 窗口是否最大化
        /// </summary>
        public bool WindowMaximized { get; set; } = false;

        #endregion

        #region 其他设置

        /// <summary>
        /// 上次播放的歌曲 ID（用于恢复播放）
        /// </summary>
        public string LastPlayingSongId { get; set; }

        /// <summary>
        /// 上次播放位置（秒）
        /// </summary>
        public double LastPlayingPosition { get; set; } = 0;

        /// <summary>
        /// 歌词显示设置
        /// </summary>
        public bool ShowLyrics { get; set; } = true;

        /// <summary>
        /// 歌词字体大小
        /// </summary>
        public int LyricsFontSize { get; set; } = 12;

        #endregion
    }

    /// <summary>
    /// Cookie 项（用于详细的 Cookie 管理）
    /// Python: {name, value, domain, path}
    /// </summary>
    public class CookieItem
    {
        public string Name { get; set; }
        public string Value { get; set; }
        public string Domain { get; set; }
        public string Path { get; set; }
    }
}
