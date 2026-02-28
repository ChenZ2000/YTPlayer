using System;
using System.Collections.Generic;

namespace YTPlayer.Core.Lyrics
{
    /// <summary>
    /// 歌词语言选项
    /// </summary>
    public sealed class LyricLanguageOption
    {
        public string Key { get; }
        public string DisplayName { get; }

        public LyricLanguageOption(string key, string displayName)
        {
            Key = key ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
        }
    }

    /// <summary>
    /// 逐字歌词中的单个字/词
    /// </summary>
    public class LyricWord
    {
        /// <summary>
        /// 文字内容
        /// </summary>
        public string Text { get; set; }

        /// <summary>
        /// 相对于歌词行开始的偏移时间
        /// </summary>
        public TimeSpan OffsetTime { get; set; }

        /// <summary>
        /// 持续时间
        /// </summary>
        public TimeSpan Duration { get; set; }

        /// <summary>
        /// 绝对时间（歌词行时间 + 偏移时间）
        /// </summary>
        public TimeSpan AbsoluteTime { get; set; }

        public LyricWord(string text, TimeSpan offsetTime, TimeSpan duration, TimeSpan absoluteTime)
        {
            Text = text ?? string.Empty;
            OffsetTime = offsetTime;
            Duration = duration;
            AbsoluteTime = absoluteTime;
        }
    }

    /// <summary>
    /// 增强的歌词行，支持逐字歌词、翻译、罗马音
    /// </summary>
    public class EnhancedLyricLine
    {
        /// <summary>
        /// 时间戳
        /// </summary>
        public TimeSpan Time { get; set; }

        /// <summary>
        /// 原文歌词文本
        /// </summary>
        public string Text { get; set; }

        /// <summary>
        /// 逐字歌词（null 表示没有逐字时间信息）
        /// </summary>
        public List<LyricWord>? Words { get; set; }

        /// <summary>
        /// 翻译歌词（null 表示没有翻译）
        /// </summary>
        public string? Translation { get; set; }

        /// <summary>
        /// 罗马音歌词（null 表示没有罗马音）
        /// </summary>
        public string? RomaLyric { get; set; }

        /// <summary>
        /// 按语言键存储的歌词文本（用于多语言选择）
        /// </summary>
        public Dictionary<string, string> LanguageTexts { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 是否有逐字时间信息
        /// </summary>
        public bool HasWordTimings => Words != null && Words.Count > 0;

        public EnhancedLyricLine(TimeSpan time, string text)
        {
            Time = time;
            Text = text ?? string.Empty;
        }
    }

    /// <summary>
    /// 完整的歌词数据容器
    /// </summary>
    public class LyricsData
    {
        /// <summary>
        /// 歌曲ID
        /// </summary>
        public string SongId { get; set; }

        /// <summary>
        /// 歌词行列表（已按时间排序）
        /// </summary>
        public List<EnhancedLyricLine> Lines { get; set; }

        /// <summary>
        /// 是否有任何逐字时间信息
        /// </summary>
        public bool HasWordTimings { get; set; }

        /// <summary>
        /// 是否有翻译
        /// </summary>
        public bool HasTranslation { get; set; }

        /// <summary>
        /// 是否有罗马音
        /// </summary>
        public bool HasRomaLyric { get; set; }

        /// <summary>
        /// 可用歌词语言（按展示顺序）
        /// </summary>
        public List<LyricLanguageOption> AvailableLanguages { get; set; } = new List<LyricLanguageOption>();

        /// <summary>
        /// 当前生效的歌词语言选择（按输出顺序）
        /// </summary>
        public List<string> SelectedLanguageKeys { get; set; } = new List<string>();

        /// <summary>
        /// 默认歌词语言选择（通常仅首选语言）
        /// </summary>
        public List<string> DefaultLanguageKeys { get; set; } = new List<string>();

        /// <summary>
        /// 是否为空（没有任何歌词）
        /// </summary>
        public bool IsEmpty => Lines == null || Lines.Count == 0;

        public LyricsData(string songId)
        {
            SongId = songId ?? string.Empty;
            Lines = new List<EnhancedLyricLine>();
        }
    }

    /// <summary>
    /// 歌词显示选项
    /// </summary>
    public enum LyricDisplayMode
    {
        /// <summary>仅原文</summary>
        OriginalOnly,
        /// <summary>原文 + 翻译</summary>
        OriginalWithTranslation,
        /// <summary>原文 + 罗马音</summary>
        OriginalWithRoma,
        /// <summary>原文 + 翻译 + 罗马音</summary>
        All
    }

    /// <summary>
    /// 歌词更新事件参数
    /// </summary>
    public class LyricUpdateEventArgs : EventArgs
    {
        /// <summary>当前歌词行</summary>
        public EnhancedLyricLine? CurrentLine { get; set; }

        /// <summary>当前高亮的字/词（逐字歌词）</summary>
        public LyricWord? CurrentWord { get; set; }

        /// <summary>播放位置</summary>
        public TimeSpan Position { get; set; }

        /// <summary>是否是新的歌词行（用于触发动画）</summary>
        public bool IsNewLine { get; set; }

        public LyricUpdateEventArgs(EnhancedLyricLine? currentLine, LyricWord? currentWord, TimeSpan position, bool isNewLine)
        {
            CurrentLine = currentLine;
            CurrentWord = currentWord;
            Position = position;
            IsNewLine = isNewLine;
        }
    }
}
