using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace YTPlayer.Core
{
    /// <summary>
    /// 歌词行类
    /// </summary>
    public class LyricLine
    {
        /// <summary>
        /// 时间戳
        /// </summary>
        public TimeSpan Time { get; set; }

        /// <summary>
        /// 歌词文本
        /// </summary>
        public string Text { get; set; }

        public LyricLine(TimeSpan time, string text)
        {
            Time = time;
            Text = text;
        }
    }

    /// <summary>
    /// 歌词管理器
    /// </summary>
    public class LyricsManager
    {
        // 正则表达式：匹配 [mm:ss.xx] 或 [mm:ss.xxx] 格式
        private static readonly Regex TimeStampRegex = new Regex(
            @"\[(\d{1,2}):(\d{2})\.(\d{2,3})\]",
            RegexOptions.Compiled
        );

        /// <summary>
        /// 解析 LRC 格式歌词
        /// </summary>
        /// <param name="lrcContent">LRC 歌词内容</param>
        /// <returns>歌词行列表</returns>
        public static List<LyricLine> ParseLyrics(string lrcContent)
        {
            if (string.IsNullOrWhiteSpace(lrcContent))
            {
                return new List<LyricLine>();
            }

            var lyrics = new List<LyricLine>();
            var lines = lrcContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                // 查找所有时间戳
                var matches = TimeStampRegex.Matches(line);
                if (matches.Count == 0)
                {
                    continue;
                }

                // 提取歌词文本（移除所有时间戳后的内容）
                var text = TimeStampRegex.Replace(line, "").Trim();

                // 为每个时间戳创建歌词行
                foreach (Match match in matches)
                {
                    try
                    {
                        int minutes = int.Parse(match.Groups[1].Value);
                        int seconds = int.Parse(match.Groups[2].Value);
                        string millisecondsStr = match.Groups[3].Value;

                        // 处理毫秒：如果是两位数则需要乘以10，三位数直接使用
                        int milliseconds = millisecondsStr.Length == 2
                            ? int.Parse(millisecondsStr) * 10
                            : int.Parse(millisecondsStr);

                        var time = new TimeSpan(0, 0, minutes, seconds, milliseconds);
                        lyrics.Add(new LyricLine(time, text));
                    }
                    catch
                    {
                        // 忽略解析失败的行
                        continue;
                    }
                }
            }

            // 按时间排序
            lyrics.Sort((a, b) => a.Time.CompareTo(b.Time));

            return lyrics;
        }

        /// <summary>
        /// 根据播放位置获取当前歌词
        /// </summary>
        /// <param name="lyrics">歌词列表</param>
        /// <param name="position">当前播放位置</param>
        /// <returns>当前歌词行，如果没有则返回 null</returns>
        public static LyricLine GetCurrentLyric(List<LyricLine> lyrics, TimeSpan position)
        {
            if (lyrics == null || lyrics.Count == 0)
            {
                return null;
            }

            // 找到最后一个时间小于或等于当前位置的歌词
            LyricLine currentLyric = null;
            foreach (var lyric in lyrics)
            {
                if (lyric.Time <= position)
                {
                    currentLyric = lyric;
                }
                else
                {
                    break;
                }
            }

            return currentLyric;
        }

        /// <summary>
        /// 获取当前歌词索引
        /// </summary>
        /// <param name="lyrics">歌词列表</param>
        /// <param name="position">当前播放位置</param>
        /// <returns>当前歌词索引，如果没有则返回 -1</returns>
        public static int GetCurrentLyricIndex(List<LyricLine> lyrics, TimeSpan position)
        {
            if (lyrics == null || lyrics.Count == 0)
            {
                return -1;
            }

            // 找到最后一个时间小于或等于当前位置的歌词索引
            int currentIndex = -1;
            for (int i = 0; i < lyrics.Count; i++)
            {
                if (lyrics[i].Time <= position)
                {
                    currentIndex = i;
                }
                else
                {
                    break;
                }
            }

            return currentIndex;
        }

        /// <summary>
        /// 将 LRC 歌词转换为纯文本
        /// </summary>
        /// <param name="lrcContent">LRC 歌词内容</param>
        /// <returns>纯文本歌词</returns>
        public static string ConvertLrcToPlainText(string lrcContent)
        {
            if (string.IsNullOrWhiteSpace(lrcContent))
            {
                return string.Empty;
            }

            var lyrics = ParseLyrics(lrcContent);
            var plainTextLines = lyrics
                .Where(l => !string.IsNullOrWhiteSpace(l.Text))
                .Select(l => l.Text)
                .Distinct() // 去除重复行
                .ToList();

            return string.Join(Environment.NewLine, plainTextLines);
        }

        /// <summary>
        /// 移除时间戳
        /// </summary>
        /// <param name="lrcContent">LRC 歌词内容</param>
        /// <returns>移除时间戳后的文本</returns>
        public static string RemoveTimeStamps(string lrcContent)
        {
            if (string.IsNullOrWhiteSpace(lrcContent))
            {
                return string.Empty;
            }

            var lines = lrcContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var cleanedLines = new List<string>();

            foreach (var line in lines)
            {
                // 移除所有时间戳
                var cleanedLine = TimeStampRegex.Replace(line, "").Trim();
                if (!string.IsNullOrWhiteSpace(cleanedLine))
                {
                    cleanedLines.Add(cleanedLine);
                }
            }

            return string.Join(Environment.NewLine, cleanedLines);
        }
    }
}
