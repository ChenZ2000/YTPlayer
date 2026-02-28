using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using YTPlayer.Core;

namespace YTPlayer.Core.Lyrics
{
    /// <summary>
    /// 单条可选歌词轨道（例如原文、翻译、罗马音）。
    /// </summary>
    public sealed class LyricLanguageTrack
    {
        public string Key { get; }
        public string DisplayName { get; }
        public string Content { get; }

        public LyricLanguageTrack(string key, string displayName, string content)
        {
            Key = key ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
            Content = content ?? string.Empty;
        }
    }

    /// <summary>
    /// 某首歌可用歌词语言配置。
    /// </summary>
    public sealed class LyricLanguageProfile
    {
        public string SongId { get; }
        public List<LyricLanguageTrack> Tracks { get; }
        public List<string> DefaultLanguageKeys { get; }

        public bool HasLyrics => Tracks != null && Tracks.Count > 0;

        public LyricLanguageProfile(string songId, IEnumerable<LyricLanguageTrack>? tracks, IEnumerable<string>? defaultLanguageKeys)
        {
            SongId = songId ?? string.Empty;
            Tracks = tracks?.ToList() ?? new List<LyricLanguageTrack>();
            DefaultLanguageKeys = defaultLanguageKeys?.ToList() ?? new List<string>();
        }

        public bool TryGetTrack(string key, out LyricLanguageTrack? track)
        {
            track = Tracks.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
            return track != null;
        }
    }

    /// <summary>
    /// 统一歌词链路上下文（UI/TTS/下载共用）。
    /// </summary>
    public sealed class LoadedLyricsContext
    {
        public string SongId { get; }
        public LyricInfo RawLyricInfo { get; }
        public LyricLanguageProfile LanguageProfile { get; }
        public List<string> SelectedLanguageKeys { get; set; }
        public LyricsData LyricsData { get; set; }
        public string ExportLyricContent { get; set; }

        public bool HasLyrics => LyricsData != null && !LyricsData.IsEmpty;

        public LoadedLyricsContext(
            string songId,
            LyricInfo rawLyricInfo,
            LyricLanguageProfile languageProfile,
            IEnumerable<string> selectedLanguageKeys,
            LyricsData lyricsData,
            string exportLyricContent)
        {
            string resolvedSongId = songId ?? string.Empty;
            SongId = resolvedSongId;
            RawLyricInfo = rawLyricInfo ?? new LyricInfo();
            LanguageProfile = languageProfile ?? new LyricLanguageProfile(resolvedSongId, Array.Empty<LyricLanguageTrack>(), Array.Empty<string>());
            SelectedLanguageKeys = selectedLanguageKeys?.ToList() ?? new List<string>();
            LyricsData = lyricsData ?? new LyricsData(resolvedSongId);
            ExportLyricContent = exportLyricContent ?? string.Empty;
        }
    }

    public static class LyricLanguagePipeline
    {
        private static readonly Regex TimeStampRegex = new Regex(@"\[(\d{1,2}):(\d{2})\.(\d{2,3})\]", RegexOptions.Compiled);
        private static readonly Regex MultiLanguageSeparatorRegex = new Regex(@"\s*(?:/|／|\||｜)\s*", RegexOptions.Compiled);

        public static LyricLanguageProfile BuildProfile(string songId, LyricInfo lyricInfo)
        {
            lyricInfo ??= new LyricInfo();

            var tracks = new List<LyricLanguageTrack>();
            var normalizedContents = new HashSet<string>(StringComparer.Ordinal);

            string primaryContent = SelectPrimaryLyricContent(songId, lyricInfo);

            AddTrackIfValid(tracks, normalizedContents, "orig", "原文", primaryContent);

            string translationContent = !string.IsNullOrWhiteSpace(lyricInfo.YTLyric)
                ? lyricInfo.YTLyric
                : lyricInfo.TLyric;
            string romaContent = !string.IsNullOrWhiteSpace(lyricInfo.YRomaLyric)
                ? lyricInfo.YRomaLyric
                : lyricInfo.RomaLyric;

            bool hasNamedSecondary = !string.IsNullOrWhiteSpace(translationContent) || !string.IsNullOrWhiteSpace(romaContent);

            if (hasNamedSecondary)
            {
                AddTrackIfValid(tracks, normalizedContents, "trans", "翻译", translationContent);
                AddTrackIfValid(tracks, normalizedContents, "roma", "罗马音", romaContent);
            }
            else if (!string.IsNullOrWhiteSpace(primaryContent) &&
                     TrySplitCombinedPrimaryTrack(primaryContent, out var splitTracks) &&
                     splitTracks.Count > 1)
            {
                tracks.Clear();
                normalizedContents.Clear();
                foreach (var splitTrack in splitTracks)
                {
                    AddTrackIfValid(tracks, normalizedContents, splitTrack.Key, splitTrack.DisplayName, splitTrack.Content);
                }
            }

            if (tracks.Count == 0)
            {
                AddTrackIfValid(tracks, normalizedContents, "trans", "翻译", translationContent);
                AddTrackIfValid(tracks, normalizedContents, "roma", "罗马音", romaContent);
            }

            var defaults = new List<string>();
            if (tracks.Count > 0)
            {
                defaults.Add(tracks[0].Key);
            }

            return new LyricLanguageProfile(songId, tracks, defaults);
        }

        public static List<string> NormalizeSelection(LyricLanguageProfile profile, IEnumerable<string>? requestedLanguageKeys)
        {
            var selected = new List<string>();
            if (profile == null || !profile.HasLyrics)
            {
                return selected;
            }

            var availableKeys = new HashSet<string>(profile.Tracks.Select(track => track.Key), StringComparer.OrdinalIgnoreCase);
            var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (requestedLanguageKeys != null)
            {
                foreach (var key in requestedLanguageKeys)
                {
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        continue;
                    }

                    string normalizedKey = key.Trim();
                    if (!availableKeys.Contains(normalizedKey))
                    {
                        continue;
                    }

                    if (added.Add(normalizedKey))
                    {
                        selected.Add(normalizedKey);
                    }
                }
            }

            if (selected.Count == 0)
            {
                foreach (var defaultKey in profile.DefaultLanguageKeys)
                {
                    if (availableKeys.Contains(defaultKey) && added.Add(defaultKey))
                    {
                        selected.Add(defaultKey);
                    }
                }
            }

            if (selected.Count == 0 && profile.Tracks.Count > 0)
            {
                selected.Add(profile.Tracks[0].Key);
            }

            return selected;
        }

        public static LyricsData BuildLyricsData(
            string songId,
            LyricLanguageProfile profile,
            IEnumerable<string>? selectedLanguageKeys)
        {
            if (profile == null || !profile.HasLyrics)
            {
                return new LyricsData(songId);
            }

            var selectedKeys = NormalizeSelection(profile, selectedLanguageKeys);
            if (selectedKeys.Count == 0)
            {
                return new LyricsData(songId);
            }

            if (!profile.TryGetTrack(selectedKeys[0], out var primaryTrack) || primaryTrack == null)
            {
                return new LyricsData(songId);
            }

            var data = EnhancedLyricsParser.ParseLyricsData(songId, primaryTrack.Content, null, null) ?? new LyricsData(songId);
            data.AvailableLanguages = profile.Tracks
                .Select(track => new LyricLanguageOption(track.Key, track.DisplayName))
                .ToList();
            data.DefaultLanguageKeys = profile.DefaultLanguageKeys.ToList();
            data.SelectedLanguageKeys = selectedKeys.ToList();

            var lineLookup = new Dictionary<TimeSpan, EnhancedLyricLine>();
            foreach (var line in data.Lines)
            {
                if (line == null)
                {
                    continue;
                }

                if (!lineLookup.ContainsKey(line.Time))
                {
                    lineLookup[line.Time] = line;
                }

                if (!string.IsNullOrWhiteSpace(line.Text))
                {
                    line.LanguageTexts[primaryTrack.Key] = line.Text;
                }
            }

            foreach (var key in selectedKeys)
            {
                if (!profile.TryGetTrack(key, out var track) || track == null)
                {
                    continue;
                }

                if (string.Equals(key, primaryTrack.Key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var textMap = EnhancedLyricsParser.ParseLrcDictionary(track.Content);
                foreach (var pair in textMap)
                {
                    if (!lineLookup.TryGetValue(pair.Key, out var line))
                    {
                        line = new EnhancedLyricLine(pair.Key, string.Empty);
                        data.Lines.Add(line);
                        lineLookup[pair.Key] = line;
                    }

                    if (!string.IsNullOrWhiteSpace(pair.Value))
                    {
                        line.LanguageTexts[key] = pair.Value;
                    }
                }
            }

            data.Lines.Sort((left, right) => left.Time.CompareTo(right.Time));

            data.HasTranslation = false;
            data.HasRomaLyric = false;
            foreach (var line in data.Lines)
            {
                if (line == null)
                {
                    continue;
                }

                if (line.LanguageTexts.TryGetValue("trans", out var translation) &&
                    !string.IsNullOrWhiteSpace(translation))
                {
                    line.Translation = translation;
                    data.HasTranslation = true;
                }
                else
                {
                    line.Translation = null;
                }

                if (line.LanguageTexts.TryGetValue("roma", out var roma) &&
                    !string.IsNullOrWhiteSpace(roma))
                {
                    line.RomaLyric = roma;
                    data.HasRomaLyric = true;
                }
                else
                {
                    line.RomaLyric = null;
                }

                var orderedTexts = GetOrderedLineTexts(line, selectedKeys);
                line.Text = orderedTexts.Count > 0 ? orderedTexts[0] : string.Empty;
            }

            return data;
        }

        public static string BuildExportLyricContent(
            string songId,
            LyricLanguageProfile profile,
            IEnumerable<string>? selectedLanguageKeys)
        {
            if (profile == null || !profile.HasLyrics)
            {
                return string.Empty;
            }

            var selectedKeys = NormalizeSelection(profile, selectedLanguageKeys);
            if (selectedKeys.Count == 0)
            {
                return string.Empty;
            }

            var lyricsData = BuildLyricsData(songId, profile, selectedKeys);
            if (lyricsData.IsEmpty)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            foreach (var line in lyricsData.Lines)
            {
                if (line == null)
                {
                    continue;
                }

                var texts = GetOrderedLineTexts(line, selectedKeys);
                if (texts.Count == 0)
                {
                    continue;
                }

                builder
                    .Append('[')
                    .Append(FormatLrcTimestamp(line.Time))
                    .Append(']')
                    .Append(string.Join(" / ", texts))
                    .AppendLine();
            }

            return builder.ToString().TrimEnd();
        }

        public static List<string> GetOrderedLineTexts(EnhancedLyricLine? line, IEnumerable<string>? selectedLanguageKeys)
        {
            var texts = new List<string>();
            if (line == null)
            {
                return texts;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (selectedLanguageKeys != null)
            {
                foreach (var key in selectedLanguageKeys)
                {
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        continue;
                    }

                    if (!line.LanguageTexts.TryGetValue(key.Trim(), out var text) ||
                        string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    string normalizedText = text.Trim();
                    if (seen.Add(normalizedText))
                    {
                        texts.Add(normalizedText);
                    }
                }
            }

            if (texts.Count > 0)
            {
                return texts;
            }

            if (!string.IsNullOrWhiteSpace(line.Text))
            {
                texts.Add(line.Text.Trim());
            }

            if (!string.IsNullOrWhiteSpace(line.Translation))
            {
                string translation = line.Translation.Trim();
                if (seen.Add(translation))
                {
                    texts.Add(translation);
                }
            }

            if (!string.IsNullOrWhiteSpace(line.RomaLyric))
            {
                string roma = line.RomaLyric.Trim();
                if (seen.Add(roma))
                {
                    texts.Add(roma);
                }
            }

            return texts;
        }

        private static void AddTrackIfValid(
            List<LyricLanguageTrack> tracks,
            HashSet<string> normalizedContents,
            string key,
            string displayName,
            string? content)
        {
            if (tracks == null || normalizedContents == null || string.IsNullOrWhiteSpace(content))
            {
                return;
            }

            string normalizedContent = NormalizeLyricContent(content);
            if (string.IsNullOrWhiteSpace(normalizedContent))
            {
                return;
            }

            if (!normalizedContents.Add(normalizedContent))
            {
                return;
            }

            tracks.Add(new LyricLanguageTrack(key, displayName, content));
        }

        private static string SelectPrimaryLyricContent(string songId, LyricInfo lyricInfo)
        {
            string yrcContent = lyricInfo?.YrcLyric ?? string.Empty;
            string lrcContent = lyricInfo?.Lyric ?? string.Empty;

            if (IsParsableLyricContent(songId, yrcContent))
            {
                return yrcContent;
            }

            if (IsParsableLyricContent(songId, lrcContent))
            {
                return lrcContent;
            }

            if (!string.IsNullOrWhiteSpace(lrcContent))
            {
                return lrcContent;
            }

            return yrcContent;
        }

        private static bool IsParsableLyricContent(string songId, string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return false;
            }

            try
            {
                var data = EnhancedLyricsParser.ParseLyricsData(songId, content, null, null);
                return data != null && !data.IsEmpty;
            }
            catch
            {
                return false;
            }
        }

        private static string NormalizeLyricContent(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return string.Empty;
            }

            var lines = content
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line));
            return string.Join("\n", lines);
        }

        private static bool TrySplitCombinedPrimaryTrack(string content, out List<LyricLanguageTrack> tracks)
        {
            tracks = new List<LyricLanguageTrack>();
            var lineEntries = ParseLrcLineEntries(content);
            if (lineEntries.Count == 0)
            {
                return false;
            }

            var countFrequency = new Dictionary<int, int>();
            var lineSegments = new List<(LrcLineEntry Entry, List<string> Segments)>();

            foreach (var lineEntry in lineEntries)
            {
                var segments = SplitCombinedText(lineEntry.Text);
                if (segments.Count >= 2)
                {
                    lineSegments.Add((lineEntry, segments));
                    countFrequency[segments.Count] = countFrequency.TryGetValue(segments.Count, out var existing)
                        ? existing + 1
                        : 1;
                }
            }

            if (countFrequency.Count == 0)
            {
                return false;
            }

            var target = countFrequency
                .OrderByDescending(pair => pair.Value)
                .ThenByDescending(pair => pair.Key)
                .First();
            int targetCount = target.Key;
            int matchedLineCount = target.Value;
            int nonEmptyTimedLineCount = lineEntries.Count(entry => !string.IsNullOrWhiteSpace(entry.Text));

            if (targetCount < 2)
            {
                return false;
            }

            if (matchedLineCount < 3 && matchedLineCount * 2 < nonEmptyTimedLineCount)
            {
                return false;
            }

            var builders = new StringBuilder[targetCount];
            var samples = new List<string>[targetCount];
            for (int i = 0; i < targetCount; i++)
            {
                builders[i] = new StringBuilder();
                samples[i] = new List<string>();
            }

            foreach (var lineEntry in lineEntries)
            {
                var segments = SplitCombinedText(lineEntry.Text);
                bool validSplit = segments.Count == targetCount;
                for (int i = 0; i < targetCount; i++)
                {
                    string segmentText = validSplit ? segments[i] : (i == 0 ? lineEntry.Text : string.Empty);
                    if (!string.IsNullOrWhiteSpace(segmentText))
                    {
                        samples[i].Add(segmentText);
                    }

                    if (lineEntry.TimeStamps.Count == 0)
                    {
                        continue;
                    }

                    foreach (var timeStamp in lineEntry.TimeStamps)
                    {
                        builders[i].Append(timeStamp).Append(segmentText ?? string.Empty).AppendLine();
                    }
                }
            }

            for (int i = 0; i < targetCount; i++)
            {
                string contentPerTrack = builders[i].ToString().TrimEnd();
                if (string.IsNullOrWhiteSpace(contentPerTrack))
                {
                    continue;
                }

                string displayName = DetectLanguageLabel(samples[i], i);
                tracks.Add(new LyricLanguageTrack($"split{i + 1}", displayName, contentPerTrack));
            }

            return tracks.Count > 1;
        }

        private static string DetectLanguageLabel(IEnumerable<string> samples, int index)
        {
            string sample = string.Join(" ", samples ?? Array.Empty<string>());
            if (string.IsNullOrWhiteSpace(sample))
            {
                return $"语言{index + 1}";
            }

            int latin = 0;
            int cjk = 0;
            int hiraganaKatakana = 0;
            int hangul = 0;

            foreach (char ch in sample)
            {
                if (char.IsWhiteSpace(ch) || char.IsPunctuation(ch))
                {
                    continue;
                }

                int code = ch;
                if ((code >= 0x0041 && code <= 0x007A) ||
                    (code >= 0x00C0 && code <= 0x024F))
                {
                    latin++;
                }
                else if (code >= 0x4E00 && code <= 0x9FFF)
                {
                    cjk++;
                }
                else if ((code >= 0x3040 && code <= 0x30FF))
                {
                    hiraganaKatakana++;
                }
                else if (code >= 0xAC00 && code <= 0xD7AF)
                {
                    hangul++;
                }
            }

            if (hiraganaKatakana > 0)
            {
                return "日文";
            }

            if (hangul > 0)
            {
                return "韩文";
            }

            if (latin > cjk && latin > 0)
            {
                return index > 0 ? "罗马音" : "拉丁";
            }

            if (cjk > 0)
            {
                return "中文";
            }

            return $"语言{index + 1}";
        }

        private static List<string> SplitCombinedText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return new List<string>();
            }

            var segments = MultiLanguageSeparatorRegex
                .Split(text.Trim())
                .Select(item => item?.Trim() ?? string.Empty)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToList();

            if (segments.Count < 2)
            {
                return new List<string>();
            }

            return segments;
        }

        private static string FormatLrcTimestamp(TimeSpan time)
        {
            int minutes = (int)time.TotalMinutes;
            int seconds = time.Seconds;
            int centiseconds = time.Milliseconds / 10;
            return string.Format(CultureInfo.InvariantCulture, "{0:D2}:{1:D2}.{2:D2}", minutes, seconds, centiseconds);
        }

        private sealed class LrcLineEntry
        {
            public List<string> TimeStamps { get; } = new List<string>();
            public string Text { get; set; } = string.Empty;
        }

        private static List<LrcLineEntry> ParseLrcLineEntries(string content)
        {
            var entries = new List<LrcLineEntry>();
            if (string.IsNullOrWhiteSpace(content))
            {
                return entries;
            }

            var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var rawLine in lines)
            {
                var matches = TimeStampRegex.Matches(rawLine);
                if (matches.Count == 0)
                {
                    continue;
                }

                var entry = new LrcLineEntry
                {
                    Text = TimeStampRegex.Replace(rawLine, "").Trim()
                };
                foreach (Match match in matches)
                {
                    entry.TimeStamps.Add(match.Value);
                }

                entries.Add(entry);
            }

            return entries;
        }
    }
}
