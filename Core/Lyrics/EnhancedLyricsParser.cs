using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace YTPlayer.Core.Lyrics
{
    /// <summary>
    /// 增强的歌词解析器，支持逐字歌词、翻译、罗马音
    /// </summary>
    public static class EnhancedLyricsParser
    {
        // 正则表达式：匹配 [mm:ss.xx] 或 [mm:ss.xxx] 格式
        private static readonly Regex TimeStampRegex = new Regex(
            @"\[(\d{1,2}):(\d{2})\.(\d{2,3})\]",
            RegexOptions.Compiled
        );

        // 正则表达式：匹配 YRC 行 [startMs,durationMs]...
        private static readonly Regex YrcLineRegex = new Regex(
            @"^\[(\d+),(\d+)\](.*)$",
            RegexOptions.Compiled
        );

        // 正则表达式：匹配 YRC 逐字片段 (start,duration,0)word
        private static readonly Regex YrcWordRegex = new Regex(
            @"\((\d+),(\d+),\d+\)([^\(\)]*)",
            RegexOptions.Compiled
        );

        // 正则表达式：匹配逐字歌词 word(offset,duration)
        private static readonly Regex WordTimingRegex = new Regex(
            @"([^\(]+)\((\d+),(\d+)\)",
            RegexOptions.Compiled
        );

        /// <summary>
        /// 解析完整的歌词数据（原文 + 翻译 + 罗马音）
        /// </summary>
        public static LyricsData ParseLyricsData(string songId, string? lrcContent, string? translationContent, string? romaContent)
        {
            var data = new LyricsData(songId);

            // 解析原文歌词
            var originalLines = ParseLrcLines(lrcContent);

            // 解析翻译歌词
            var translationMap = ParseLrcDictionary(translationContent);

            // 解析罗马音歌词
            var romaMap = ParseLrcDictionary(romaContent);

            // 合并所有信息
            foreach (var line in originalLines)
            {
                if (!string.IsNullOrWhiteSpace(line.Text))
                {
                    line.LanguageTexts["orig"] = line.Text;
                }

                // 查找对应的翻译
                if (translationMap.TryGetValue(line.Time, out var translation))
                {
                    line.Translation = translation;
                    if (!string.IsNullOrWhiteSpace(translation))
                    {
                        line.LanguageTexts["trans"] = translation;
                    }
                    data.HasTranslation = true;
                }

                // 查找对应的罗马音
                if (romaMap.TryGetValue(line.Time, out var roma))
                {
                    line.RomaLyric = roma;
                    if (!string.IsNullOrWhiteSpace(roma))
                    {
                        line.LanguageTexts["roma"] = roma;
                    }
                    data.HasRomaLyric = true;
                }

                // 检查是否有逐字信息
                if (line.HasWordTimings)
                {
                    data.HasWordTimings = true;
                }

                data.Lines.Add(line);
            }

            data.AvailableLanguages = new List<LyricLanguageOption>
            {
                new LyricLanguageOption("orig", "原文")
            };
            if (data.HasTranslation)
            {
                data.AvailableLanguages.Add(new LyricLanguageOption("trans", "翻译"));
            }
            if (data.HasRomaLyric)
            {
                data.AvailableLanguages.Add(new LyricLanguageOption("roma", "罗马音"));
            }

            data.DefaultLanguageKeys = new List<string> { "orig" };
            data.SelectedLanguageKeys = new List<string> { "orig" };

            return data;
        }

        /// <summary>
        /// 解析 LRC 格式歌词，返回歌词行列表
        /// </summary>
        private static List<EnhancedLyricLine> ParseLrcLines(string? lrcContent)
        {
            var result = new List<EnhancedLyricLine>();

            if (string.IsNullOrWhiteSpace(lrcContent))
            {
                return result;
            }

            var lines = lrcContent!.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                // 查找所有时间戳
                var timeMatches = TimeStampRegex.Matches(line);
                if (timeMatches.Count == 0)
                {
                    if (TryParseYrcLine(line, out var yrcLine))
                    {
                        result.Add(yrcLine);
                    }
                    continue;
                }

                // 提取歌词文本（移除所有时间戳后的内容）
                var textWithTimings = TimeStampRegex.Replace(line, "").Trim();

                // 尝试解析逐字歌词
                var (text, words) = ParseWordTimings(textWithTimings);

                // 为每个时间戳创建歌词行
                foreach (Match timeMatch in timeMatches)
                {
                    try
                    {
                        var time = ParseTimeStamp(timeMatch);
                        var lyricLine = new EnhancedLyricLine(time, text);

                        // 如果有逐字信息，计算绝对时间
                        if (words != null && words.Count > 0)
                        {
                            var absoluteWords = new List<LyricWord>();
                            foreach (var word in words)
                            {
                                absoluteWords.Add(new LyricWord(
                                    word.Text,
                                    word.OffsetTime,
                                    word.Duration,
                                    time + word.OffsetTime  // 绝对时间 = 行时间 + 偏移
                                ));
                            }
                            lyricLine.Words = absoluteWords;
                        }

                        result.Add(lyricLine);
                    }
                    catch
                    {
                        // 忽略解析失败的行
                        continue;
                    }
                }
            }

            // 按时间排序
            result.Sort((a, b) => a.Time.CompareTo(b.Time));

            return result;
        }

        /// <summary>
        /// 解析 LRC 格式歌词为字典（时间 -> 文本）
        /// </summary>
        public static Dictionary<TimeSpan, string> ParseLrcDictionary(string? lrcContent)
        {
            var result = new Dictionary<TimeSpan, string>();

            if (string.IsNullOrWhiteSpace(lrcContent))
            {
                return result;
            }

            var lines = lrcContent!.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                var timeMatches = TimeStampRegex.Matches(line);
                if (timeMatches.Count == 0)
                {
                    if (TryParseYrcLine(line, out var yrcLine) &&
                        !string.IsNullOrWhiteSpace(yrcLine.Text))
                    {
                        result[yrcLine.Time] = yrcLine.Text;
                    }
                    continue;
                }

                var text = TimeStampRegex.Replace(line, "").Trim();

                foreach (Match timeMatch in timeMatches)
                {
                    try
                    {
                        var time = ParseTimeStamp(timeMatch);
                        result[time] = text;  // 如果有重复时间戳，后面的会覆盖前面的
                    }
                    catch
                    {
                        continue;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// 解析时间戳
        /// </summary>
        private static TimeSpan ParseTimeStamp(Match match)
        {
            int minutes = int.Parse(match.Groups[1].Value);
            int seconds = int.Parse(match.Groups[2].Value);
            string millisecondsStr = match.Groups[3].Value;

            // 处理毫秒：如果是两位数则需要乘以10，三位数直接使用
            int milliseconds = millisecondsStr.Length == 2
                ? int.Parse(millisecondsStr) * 10
                : int.Parse(millisecondsStr);

            return new TimeSpan(0, 0, minutes, seconds, milliseconds);
        }

        /// <summary>
        /// 解析逐字歌词，返回纯文本和单词列表
        /// </summary>
        private static (string text, List<LyricWord>? words) ParseWordTimings(string textWithTimings)
        {
            if (string.IsNullOrWhiteSpace(textWithTimings))
            {
                return (string.Empty, null);
            }

            // 检查是否包含逐字时间信息
            if (!textWithTimings.Contains("(") || !textWithTimings.Contains(")"))
            {
                return (textWithTimings, null);
            }

            var words = new List<LyricWord>();
            var matches = WordTimingRegex.Matches(textWithTimings);

            if (matches.Count == 0)
            {
                return (textWithTimings, null);
            }

            var textBuilder = new System.Text.StringBuilder();

            foreach (Match match in matches)
            {
                try
                {
                    string wordText = match.Groups[1].Value.Trim();
                    int offsetMs = int.Parse(match.Groups[2].Value);
                    int durationMs = int.Parse(match.Groups[3].Value);

                    if (!string.IsNullOrEmpty(wordText))
                    {
                        words.Add(new LyricWord(
                            wordText,
                            TimeSpan.FromMilliseconds(offsetMs),
                            TimeSpan.FromMilliseconds(durationMs),
                            TimeSpan.Zero  // 绝对时间将在外层设置
                        ));

                        textBuilder.Append(wordText);
                    }
                }
                catch
                {
                    continue;
                }
            }

            // 如果解析出了单词，返回单词列表；否则返回原始文本
            if (words.Count > 0)
            {
                return (textBuilder.ToString(), words);
            }
            else
            {
                return (textWithTimings, null);
            }
        }

        private static bool TryParseYrcLine(string rawLine, out EnhancedLyricLine yrcLine)
        {
            yrcLine = null!;
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                return false;
            }

            var lineMatch = YrcLineRegex.Match(rawLine.Trim());
            if (!lineMatch.Success || !int.TryParse(lineMatch.Groups[1].Value, out int lineStartMs))
            {
                return false;
            }

            _ = int.TryParse(lineMatch.Groups[2].Value, out int lineDurationMs);
            string payload = lineMatch.Groups[3].Value ?? string.Empty;
            var wordMatches = YrcWordRegex.Matches(payload);
            var lineTime = TimeSpan.FromMilliseconds(Math.Max(0, lineStartMs));
            var words = new List<LyricWord>();
            var textBuilder = new StringBuilder();
            var segments = new List<(int StartMs, int DurationMs, string Text)>();

            foreach (Match wordMatch in wordMatches)
            {
                if (!int.TryParse(wordMatch.Groups[1].Value, out int wordStartMs) ||
                    !int.TryParse(wordMatch.Groups[2].Value, out int wordDurationMs))
                {
                    continue;
                }

                string wordText = wordMatch.Groups[3].Value;
                if (string.IsNullOrEmpty(wordText))
                {
                    continue;
                }

                segments.Add((wordStartMs, wordDurationMs, wordText));
                textBuilder.Append(wordText);
            }

            bool useRelativeStart = segments.Count > 0 && (
                segments.Any(segment => segment.StartMs < lineStartMs) ||
                (lineDurationMs > 0 && segments.Max(segment => segment.StartMs) <= lineDurationMs + 20)
            );

            foreach (var segment in segments)
            {
                int relativeStartMs = useRelativeStart
                    ? Math.Max(0, segment.StartMs)
                    : Math.Max(0, segment.StartMs - lineStartMs);
                var offset = TimeSpan.FromMilliseconds(relativeStartMs);
                var duration = TimeSpan.FromMilliseconds(Math.Max(0, segment.DurationMs));
                var absolute = lineTime + offset;

                words.Add(new LyricWord(segment.Text, offset, duration, absolute));
            }

            string text = textBuilder.ToString().Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                text = YrcWordRegex.Replace(payload, string.Empty).Trim();
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            yrcLine = new EnhancedLyricLine(lineTime, text);
            if (words.Count > 0)
            {
                yrcLine.Words = words;
            }

            return true;
        }

        /// <summary>
        /// 将 LyricsData 转换为纯文本（用于兼容旧代码）
        /// </summary>
        public static string ConvertToPlainText(LyricsData lyricsData, bool includeTranslation = false)
        {
            if (lyricsData == null || lyricsData.IsEmpty)
            {
                return string.Empty;
            }

            var lines = new List<string>();

            foreach (var line in lyricsData.Lines)
            {
                if (!string.IsNullOrWhiteSpace(line.Text))
                {
                    lines.Add(line.Text);

                    if (includeTranslation && !string.IsNullOrWhiteSpace(line.Translation))
                    {
                        lines.Add($"  {line.Translation}");
                    }
                }
            }

            return string.Join(Environment.NewLine, lines.Distinct());
        }
    }
}
