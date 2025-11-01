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
        /// 下载目录路径（便携式设计：默认保存在程序目录\downloads）
        /// Python: cfg.setdefault("download_path", None)
        /// </summary>
        public string DownloadDirectory { get; set; }

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
        public string LastPlayingSongId { get; set; }

        /// <summary>
        /// 上次播放位置（秒）
        /// </summary>
        public double LastPlayingPosition { get; set; } = 0;

        /// <summary>
        /// 歌词字体大小
        /// </summary>
        public int LyricsFontSize { get; set; } = 12;

        /// <summary>
        /// 歌词朗读开关状态（默认关闭）
        /// </summary>
        public bool LyricsReadingEnabled { get; set; } = false;

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
