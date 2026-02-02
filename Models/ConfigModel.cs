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

        /// <summary>
        /// 输出设备（Default 表示 Windows 默认）
        /// </summary>
        public string OutputDevice { get; set; } = AudioOutputDeviceInfo.WindowsDefaultId;

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
        /// 下载目录路径（便携式设计：默认保存在程序目录\downloads）
        /// Python: cfg.setdefault("download_path", None)
        /// </summary>
        public string DownloadDirectory { get; set; } = string.Empty;

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

        #endregion

        #region 其他设置

        /// <summary>
        /// 上次播放的歌曲 ID（用于恢复播放）
        /// </summary>
        public string LastPlayingSongId { get; set; } = string.Empty;

        /// <summary>
        /// 上次播放来源（视图来源标识，便于恢复队列/上下文）
        /// </summary>
        public string LastPlayingSource { get; set; } = string.Empty;

        /// <summary>
        /// 上次播放的歌曲名称（用于可读的提示，可选）
        /// </summary>
        public string LastPlayingSongName { get; set; } = string.Empty;

        /// <summary>
        /// 上次播放的总时长（秒）
        /// </summary>
        public double LastPlayingDuration { get; set; } = 0;

        /// <summary>
        /// 上次播放时的队列歌曲 ID 列表（按顺序）
        /// </summary>
        public List<string> LastPlayingQueue { get; set; } = new List<string>();

        /// <summary>
        /// 上次播放时队列的当前索引（基于 LastPlayingQueue）
        /// </summary>
        public int LastPlayingQueueIndex { get; set; } = -1;

        /// <summary>
        /// 上次播放歌曲在来源列表中的索引（基于 LastPlayingSource）
        /// </summary>
        public int LastPlayingSourceIndex { get; set; } = -1;

        /// <summary>
        /// 听歌识曲默认输入设备 ID（Default / Loopback / 设备标识）。
        /// </summary>
        public string RecognitionInputDeviceId { get; set; } = AudioInputDeviceInfo.WindowsDefaultId;

        /// <summary>
        /// 识曲完成后自动关闭对话框。
        /// </summary>
        public bool RecognitionAutoCloseDialog { get; set; } = true;

        /// <summary>
        /// 识曲调用的后端基础地址（默认使用内置增强 API）。
        /// </summary>
        public string RecognitionApiBaseUrl { get; set; } = string.Empty;

        /// <summary>
        /// 评论 API 基础地址（/comment/new 等，留空则使用内置默认）。
        /// </summary>
        public string CommentApiBaseUrl { get; set; } = string.Empty;

        /// <summary>
        /// 歌词字体大小
        /// </summary>
        public int LyricsFontSize { get; set; } = 12;

        /// <summary>
        /// 歌词朗读开关状态（默认关闭）
        /// </summary>
        public bool LyricsReadingEnabled { get; set; } = false;

        /// <summary>
        /// 是否隐藏列表序号（默认显示）
        /// </summary>
        public bool SequenceNumberHidden { get; set; } = false;

        /// <summary>
        /// 是否隐藏评论序号（默认显示）
        /// </summary>
        public bool CommentSequenceNumberHidden { get; set; } = false;

        /// <summary>
        /// 是否隐藏控制栏（默认显示）
        /// </summary>
        public bool ControlBarHidden { get; set; } = false;

        /// <summary>
        /// 主窗口左上角 X 坐标（像素）。
        /// </summary>
        public int? WindowX { get; set; }

        /// <summary>
        /// 主窗口左上角 Y 坐标（像素）。
        /// </summary>
        public int? WindowY { get; set; }

        /// <summary>
        /// 主窗口宽度（像素）。
        /// </summary>
        public int? WindowWidth { get; set; }

        /// <summary>
        /// 主窗口高度（像素）。
        /// </summary>
        public int? WindowHeight { get; set; }

        /// <summary>
        /// 主窗口状态（Normal/Maximized）。
        /// </summary>
        public string? WindowState { get; set; }

        /// <summary>
        /// 是否启用播放数据上报，保持“最近播放/听歌排行”与官方账号同步
        /// </summary>
        public bool PlaybackReportingEnabled { get; set; } = true;

        /// <summary>
        /// 播放时禁止系统睡眠（默认开启）
        /// </summary>
        public bool PreventSleepDuringPlayback { get; set; } = true;

        #endregion
    }

    /// <summary>
    /// Cookie 项（用于详细的 Cookie 管理）
    /// Python: {name, value, domain, path}
    /// </summary>
    public class CookieItem
    {
        public string Name { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public string Domain { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
    }
}
