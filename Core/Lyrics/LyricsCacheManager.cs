using System;
using System.Collections.Generic;
using System.Threading;

namespace YTPlayer.Core.Lyrics
{
    /// <summary>
    /// 高性能歌词缓存管理器
    /// 使用二分查找 + 索引缓存实现 O(1) 到 O(log n) 的查找性能
    /// </summary>
    public sealed class LyricsCacheManager
    {
        private readonly object _lock = new object();
        private LyricsData? _currentLyrics;
        private int _lastLineIndex = -1;
        private int _lastWordIndex = -1;
        private TimeSpan _lastPosition = TimeSpan.Zero;

        /// <summary>
        /// 当前歌词数据
        /// </summary>
        public LyricsData? CurrentLyrics
        {
            get
            {
                lock (_lock)
                {
                    return _currentLyrics;
                }
            }
        }

        /// <summary>
        /// 是否有歌词
        /// </summary>
        public bool HasLyrics
        {
            get
            {
                lock (_lock)
                {
                    return _currentLyrics != null && !_currentLyrics.IsEmpty;
                }
            }
        }

        /// <summary>
        /// 加载新的歌词数据
        /// </summary>
        public void LoadLyrics(LyricsData? lyricsData)
        {
            lock (_lock)
            {
                _currentLyrics = lyricsData;
                _lastLineIndex = -1;
                _lastWordIndex = -1;
                _lastPosition = TimeSpan.Zero;
            }
        }

        /// <summary>
        /// 清空歌词
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                _currentLyrics = null;
                _lastLineIndex = -1;
                _lastWordIndex = -1;
                _lastPosition = TimeSpan.Zero;
            }
        }

        /// <summary>
        /// 获取当前播放位置的歌词行
        /// 高性能实现：O(1) 缓存命中，O(log n) 二分查找
        /// </summary>
        public EnhancedLyricLine? GetCurrentLine(TimeSpan position, out bool isNewLine)
        {
            isNewLine = false;

            lock (_lock)
            {
                if (_currentLyrics == null || _currentLyrics.IsEmpty)
                {
                    return null;
                }

                var lines = _currentLyrics.Lines;

                // 快速路径：检查缓存的索引是否仍然有效
                if (_lastLineIndex >= 0 && _lastLineIndex < lines.Count)
                {
                    var cachedLine = lines[_lastLineIndex];

                    // 如果当前位置仍在缓存行的范围内
                    if (position >= cachedLine.Time)
                    {
                        // 检查是否需要移动到下一行
                        if (_lastLineIndex < lines.Count - 1)
                        {
                            var nextLine = lines[_lastLineIndex + 1];
                            if (position < nextLine.Time)
                            {
                                // 仍在当前行
                                _lastPosition = position;
                                return cachedLine;
                            }
                            else
                            {
                                // 移动到下一行
                                _lastLineIndex++;
                                isNewLine = true;
                                _lastPosition = position;
                                _lastWordIndex = -1;  // 重置单词索引
                                return lines[_lastLineIndex];
                            }
                        }
                        else
                        {
                            // 已经是最后一行
                            _lastPosition = position;
                            return cachedLine;
                        }
                    }
                    else if (position < cachedLine.Time && _lastLineIndex > 0)
                    {
                        // 向后查找（可能是 seek 操作）
                        var prevLine = lines[_lastLineIndex - 1];
                        if (position >= prevLine.Time)
                        {
                            _lastLineIndex--;
                            isNewLine = true;
                            _lastPosition = position;
                            _lastWordIndex = -1;
                            return prevLine;
                        }
                    }
                }

                // 慢速路径：二分查找
                int index = BinarySearchLyricLine(lines, position);

                if (index >= 0)
                {
                    isNewLine = (index != _lastLineIndex);
                    _lastLineIndex = index;
                    _lastPosition = position;

                    if (isNewLine)
                    {
                        _lastWordIndex = -1;  // 重置单词索引
                    }

                    return lines[index];
                }

                return null;
            }
        }

        /// <summary>
        /// 获取当前播放位置的逐字歌词
        /// </summary>
        public LyricWord? GetCurrentWord(TimeSpan position, EnhancedLyricLine? currentLine)
        {
            if (currentLine == null || !currentLine.HasWordTimings)
            {
                return null;
            }

            lock (_lock)
            {
                var words = currentLine.Words;
                if (words == null || words.Count == 0)
                {
                    return null;
                }

                // 快速路径：检查缓存的单词索引
                if (_lastWordIndex >= 0 && _lastWordIndex < words.Count)
                {
                    var cachedWord = words[_lastWordIndex];

                    if (position >= cachedWord.AbsoluteTime &&
                        position < cachedWord.AbsoluteTime + cachedWord.Duration)
                    {
                        return cachedWord;
                    }

                    // 检查是否移动到下一个单词
                    if (_lastWordIndex < words.Count - 1)
                    {
                        var nextWord = words[_lastWordIndex + 1];
                        if (position >= nextWord.AbsoluteTime &&
                            position < nextWord.AbsoluteTime + nextWord.Duration)
                        {
                            _lastWordIndex++;
                            return nextWord;
                        }
                    }
                }

                // 慢速路径：线性查找单词（单词数量通常很少，不需要二分）
                for (int i = 0; i < words.Count; i++)
                {
                    var word = words[i];
                    if (position >= word.AbsoluteTime &&
                        position < word.AbsoluteTime + word.Duration)
                    {
                        _lastWordIndex = i;
                        return word;
                    }
                }

                return null;
            }
        }

        /// <summary>
        /// 获取指定时间点的所有歌词行（处理同一时间点多条歌词的情况）
        /// </summary>
        public List<EnhancedLyricLine> GetLinesAtTime(TimeSpan position)
        {
            var result = new List<EnhancedLyricLine>();

            lock (_lock)
            {
                if (_currentLyrics == null || _currentLyrics.IsEmpty)
                {
                    return result;
                }

                var lines = _currentLyrics.Lines;

                // 找到第一个匹配的行
                int startIndex = BinarySearchLyricLine(lines, position);
                if (startIndex < 0)
                {
                    return result;
                }

                // 向前查找所有相同时间戳的行
                int i = startIndex;
                while (i >= 0 && lines[i].Time == lines[startIndex].Time)
                {
                    i--;
                }
                i++;  // 回到第一个匹配的位置

                // 向后收集所有相同时间戳的行
                while (i < lines.Count && lines[i].Time == lines[startIndex].Time)
                {
                    result.Add(lines[i]);
                    i++;
                }

                return result;
            }
        }

        /// <summary>
        /// 获取指定时间点开始、在容差范围内的歌词簇（用于同一时间或极短间隔的歌词）
        /// </summary>
        public List<EnhancedLyricLine> GetLineCluster(TimeSpan anchorTime, TimeSpan tolerance)
        {
            var cluster = new List<EnhancedLyricLine>();

            if (tolerance < TimeSpan.Zero)
            {
                tolerance = TimeSpan.Zero;
            }

            lock (_lock)
            {
                if (_currentLyrics == null || _currentLyrics.IsEmpty)
                {
                    return cluster;
                }

                var lines = _currentLyrics.Lines;
                if (lines.Count == 0)
                {
                    return cluster;
                }

                int index = BinarySearchLyricLine(lines, anchorTime);
                if (index < 0)
                {
                    return cluster;
                }

                TimeSpan clusterStartTime = lines[index].Time;

                // 回溯到该时间点的第一条歌词，用于处理同一时间戳多条歌词
                while (index > 0 && lines[index - 1].Time == clusterStartTime)
                {
                    index--;
                }

                for (int i = index; i < lines.Count; i++)
                {
                    var line = lines[i];
                    if (line.Time < clusterStartTime)
                    {
                        continue;
                    }

                    var diff = line.Time - clusterStartTime;
                    if (diff <= tolerance)
                    {
                        cluster.Add(line);
                    }
                    else
                    {
                        break;
                    }
                }
            }

            return cluster;
        }

        /// <summary>
        /// 获取下一句歌词（用于预显示）
        /// </summary>
        public EnhancedLyricLine? GetNextLine(TimeSpan position)
        {
            lock (_lock)
            {
                if (_currentLyrics == null || _currentLyrics.IsEmpty)
                {
                    return null;
                }

                var lines = _currentLyrics.Lines;
                int currentIndex = BinarySearchLyricLine(lines, position);

                if (currentIndex >= 0 && currentIndex < lines.Count - 1)
                {
                    return lines[currentIndex + 1];
                }

                return null;
            }
        }

        /// <summary>
        /// 二分查找歌词行
        /// 返回：小于或等于指定位置的最后一个歌词行的索引，如果没有找到返回 -1
        /// </summary>
        private static int BinarySearchLyricLine(List<EnhancedLyricLine> lines, TimeSpan position)
        {
            if (lines.Count == 0)
            {
                return -1;
            }

            int left = 0;
            int right = lines.Count - 1;
            int result = -1;

            while (left <= right)
            {
                int mid = left + (right - left) / 2;
                var midLine = lines[mid];

                if (midLine.Time <= position)
                {
                    result = mid;  // 找到一个候选
                    left = mid + 1;  // 继续在右半部分查找更大的
                }
                else
                {
                    right = mid - 1;  // 在左半部分查找
                }
            }

            return result;
        }

        /// <summary>
        /// 获取歌词统计信息（用于调试）
        /// </summary>
        public string GetStatistics()
        {
            lock (_lock)
            {
                if (_currentLyrics == null || _currentLyrics.IsEmpty)
                {
                    return "没有歌词";
                }

                return $"歌词行数: {_currentLyrics.Lines.Count}, " +
                       $"逐字: {(_currentLyrics.HasWordTimings ? "是" : "否")}, " +
                       $"翻译: {(_currentLyrics.HasTranslation ? "是" : "否")}, " +
                       $"罗马音: {(_currentLyrics.HasRomaLyric ? "是" : "否")}";
            }
        }
    }
}
