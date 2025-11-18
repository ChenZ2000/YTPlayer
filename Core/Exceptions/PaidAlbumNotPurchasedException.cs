using System;
using System.Collections.Generic;
using System.Linq;

namespace YTPlayer.Core
{
    /// <summary>
    /// 当歌曲属于付费数字专辑且当前账号未购买时抛出的异常。
    /// </summary>
    public sealed class PaidAlbumNotPurchasedException : Exception
    {
        private const string DefaultMessage = "该歌曲属于付费数字专辑，未购买无法播放。";

        public PaidAlbumNotPurchasedException(IEnumerable<string>? songIds = null, string? message = null, Exception? innerException = null)
            : base(string.IsNullOrWhiteSpace(message) ? DefaultMessage : message, innerException)
        {
            SongIds = (songIds ?? Array.Empty<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        /// <summary>
        /// 需要购买的歌曲ID列表
        /// </summary>
        public IReadOnlyList<string> SongIds { get; }
    }
}
