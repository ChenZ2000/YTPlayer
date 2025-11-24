using System;
using System.Collections.Generic;

namespace YTPlayer.Models
{
    /// <summary>
    /// 听歌识曲结果包装。
    /// </summary>
    public sealed class SongRecognitionResult
    {
        public SongRecognitionResult(string sessionId, IReadOnlyList<SongInfo> matches, int durationSeconds)
        {
            SessionId = sessionId;
            Matches = matches ?? Array.Empty<SongInfo>();
            DurationSeconds = durationSeconds;
        }

        /// <summary>
        /// 一次识别会话的标识。
        /// </summary>
        public string SessionId { get; }

        /// <summary>
        /// 识别到的歌曲列表。
        /// </summary>
        public IReadOnlyList<SongInfo> Matches { get; }

        /// <summary>
        /// 录制时长（秒）。
        /// </summary>
        public int DurationSeconds { get; }

        /// <summary>
        /// 可选的指纹摘要，便于日志。
        /// </summary>
        public string? FingerprintPreview { get; set; }
    }
}
