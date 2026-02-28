using System;
using System.Threading;
using System.Threading.Tasks;
using YTPlayer.Utils;

namespace YTPlayer.Core.Lyrics
{
    /// <summary>
    /// 歌词实时推送管理器
    /// 精确同步播放进度，推送歌词更新事件
    /// </summary>
    public sealed class LyricsDisplayManager : IDisposable
    {
        private readonly LyricsCacheManager _cacheManager;
        private readonly object _lock = new object();

        private CancellationTokenSource? _updateLoopCts;
        private bool _disposed;

        // 上次推送的歌词行，用于检测变化
        private EnhancedLyricLine? _lastLine;
        private LyricWord? _lastWord;

        // ⭐ 位置跳跃检测：用于自动检测 seek 并重置状态
        private TimeSpan _lastPosition = TimeSpan.Zero;
        private const double POSITION_JUMP_THRESHOLD_SECONDS = 2.0; // 位置跳跃阈值：2秒

        /// <summary>
        /// 歌词更新事件（在检测到歌词变化时触发）
        /// </summary>
        public event EventHandler<LyricUpdateEventArgs>? LyricUpdated;

        /// <summary>
        /// 显示模式
        /// </summary>
        public LyricDisplayMode DisplayMode { get; set; } = LyricDisplayMode.OriginalWithTranslation;

        public LyricsDisplayManager(LyricsCacheManager cacheManager)
        {
            _cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
        }

        /// <summary>
        /// 加载新的歌词数据
        /// </summary>
        public void LoadLyrics(LyricsData? lyricsData)
        {
            lock (_lock)
            {
                _cacheManager.LoadLyrics(lyricsData);
                _lastLine = null;
                _lastWord = null;
                _lastPosition = TimeSpan.Zero;

                // 触发清空事件
                if (lyricsData == null || lyricsData.IsEmpty)
                {
                    LyricUpdated?.Invoke(this, new LyricUpdateEventArgs(null, null, TimeSpan.Zero, true));
                }
            }
        }

        /// <summary>
        /// 清空歌词
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                _cacheManager.Clear();
                _lastLine = null;
                _lastWord = null;
                _lastPosition = TimeSpan.Zero;
                LyricUpdated?.Invoke(this, new LyricUpdateEventArgs(null, null, TimeSpan.Zero, true));
            }
        }

        /// <summary>
        /// 更新播放位置并推送歌词（同步调用，由播放进度监控调用）
        /// </summary>
        public void UpdatePosition(TimeSpan position)
        {
            if (_disposed)
            {
                return;
            }

            lock (_lock)
            {
                if (!_cacheManager.HasLyrics)
                {
                    return;
                }

                // ⭐ 检测位置跳跃（seek 操作）
                double positionDiff = Math.Abs((position - _lastPosition).TotalSeconds);
                bool isPositionJump = positionDiff > POSITION_JUMP_THRESHOLD_SECONDS;

                if (isPositionJump)
                {
                    // 检测到位置跳跃，重置状态以避免输出中间歌词
                    _lastLine = null;
                    _lastWord = null;

                    DebugLogger.Log(
                        DebugLogger.LogLevel.Info,
                        "LyricsDisplay",
                        $"检测到位置跳跃: {_lastPosition:mm\\:ss\\.ff} → {position:mm\\:ss\\.ff} (跳跃 {positionDiff:F1}s)，重置歌词状态");
                }

                // 更新上次位置
                _lastPosition = position;

                // 获取当前歌词行
                var currentLine = _cacheManager.GetCurrentLine(position, out bool isNewLine);

                if (currentLine == null)
                {
                    // 没有歌词（可能在第一句之前或最后一句之后）
                    if (_lastLine != null)
                    {
                        _lastLine = null;
                        _lastWord = null;
                        // 不触发事件，保持显示最后一句歌词
                    }
                    return;
                }

                // 检测歌词行变化
                if (isNewLine || _lastLine != currentLine)
                {
                    _lastLine = currentLine;
                    _lastWord = null;

                    // 触发新歌词行事件
                    var args = new LyricUpdateEventArgs(currentLine, null, position, true);
                    LyricUpdated?.Invoke(this, args);

                    DebugLogger.Log(
                        DebugLogger.LogLevel.Info,
                        "LyricsDisplay",
                        $"歌词更新: [{position:mm\\:ss\\.ff}] {currentLine.Text}");
                }

                // 如果有逐字歌词，获取当前高亮的字
                if (currentLine.HasWordTimings)
                {
                    var currentWord = _cacheManager.GetCurrentWord(position, currentLine);

                    if (currentWord != _lastWord)
                    {
                        _lastWord = currentWord;

                        // 触发逐字更新事件（isNewLine=false，因为还是同一行）
                        var args = new LyricUpdateEventArgs(currentLine, currentWord, position, false);
                        LyricUpdated?.Invoke(this, args);

                        if (currentWord != null)
                        {
                            DebugLogger.Log(
                                DebugLogger.LogLevel.Info,
                                "LyricsDisplay",
                                $"逐字更新: [{position:mm\\:ss\\.ff}] 高亮 '{currentWord.Text}'");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 手动触发歌词更新（用于 seek 操作后立即更新UI）
        /// </summary>
        public void ForceUpdate(TimeSpan position)
        {
            lock (_lock)
            {
                _lastLine = null;
                _lastWord = null;
                _lastPosition = TimeSpan.Zero; // 重置位置以避免误判为跳跃
                UpdatePosition(position);
            }
        }

        /// <summary>
        /// 获取当前显示的歌词文本（根据显示模式格式化）
        /// </summary>
        public string GetFormattedLyricText(EnhancedLyricLine? line)
        {
            if (line == null)
            {
                return "暂无歌词";
            }

            var selectedKeys = _cacheManager.CurrentLyrics?.SelectedLanguageKeys;
            var languageTexts = LyricLanguagePipeline.GetOrderedLineTexts(line, selectedKeys);
            if (languageTexts.Count > 0)
            {
                return string.Join(" | ", languageTexts);
            }

            var parts = new System.Collections.Generic.List<string>();

            switch (DisplayMode)
            {
                case LyricDisplayMode.OriginalOnly:
                    return line.Text ?? string.Empty;

                case LyricDisplayMode.OriginalWithTranslation:
                    string originalText = line.Text ?? string.Empty;
                    parts.Add(originalText);
                    var translation = line.Translation;
                    if (!string.IsNullOrWhiteSpace(translation))
                    {
                        parts.Add(translation!);
                    }
                    break;

                case LyricDisplayMode.OriginalWithRoma:
                    var roma = line.RomaLyric;
                    if (!string.IsNullOrWhiteSpace(roma))
                    {
                        parts.Add(roma!);
                    }
                    parts.Add(line.Text ?? string.Empty);
                    break;

                case LyricDisplayMode.All:
                    var romaLyric = line.RomaLyric;
                    if (!string.IsNullOrWhiteSpace(romaLyric))
                    {
                        parts.Add(romaLyric!);
                    }
                    parts.Add(line.Text ?? string.Empty);
                    var translated = line.Translation;
                    if (!string.IsNullOrWhiteSpace(translated))
                    {
                        parts.Add(translated!);
                    }
                    break;
            }

            return string.Join(" | ", parts);
        }

        public System.Collections.Generic.List<string> GetSpeechSegments(EnhancedLyricLine? line)
        {
            var selectedKeys = _cacheManager.CurrentLyrics?.SelectedLanguageKeys;
            return LyricLanguagePipeline.GetOrderedLineTexts(line, selectedKeys);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            lock (_lock)
            {
                _updateLoopCts?.Cancel();
                _updateLoopCts?.Dispose();
                _updateLoopCts = null;
            }
        }
    }
}
