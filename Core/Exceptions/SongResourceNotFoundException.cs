using System;
using System.Collections.Generic;
using System.Linq;

namespace YTPlayer.Core
{
    /// <summary>
    /// 当网易云官方曲库缺少请求的歌曲资源时抛出的异常。
    /// </summary>
    public sealed class SongResourceNotFoundException : Exception
    {
        private const string DefaultMessage = "请求的歌曲资源不存在或已被移除。";

        public SongResourceNotFoundException(IEnumerable<string>? songIds)
            : this(DefaultMessage, songIds, null)
        {
        }

        public SongResourceNotFoundException(string message, IEnumerable<string>? songIds)
            : this(message, songIds, null)
        {
        }

        public SongResourceNotFoundException(string message, IEnumerable<string>? songIds, Exception? innerException = null)
            : base(string.IsNullOrWhiteSpace(message) ? DefaultMessage : message, innerException)
        {
            SongIds = (songIds ?? Array.Empty<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        /// <summary>
        /// 缺失的歌曲 ID 列表。
        /// </summary>
        public IReadOnlyList<string> SongIds { get; }
    }
}

