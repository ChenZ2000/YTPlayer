using System;

namespace YTPlayer.Models.Playback
{
    /// <summary>
    /// 音质信息
    /// </summary>
    public sealed class QualityInfo
    {
        /// <summary>
        /// 音质标识（如 "standard", "exhigh", "lossless" 等）
        /// </summary>
        public string Level { get; set; }

        /// <summary>
        /// 该音质是否可用（资源存在且有权限访问）
        /// </summary>
        public bool IsAvailable { get; set; }

        /// <summary>
        /// 需要的 VIP 等级（0=普通用户, 1=VIP, 11=SVIP）
        /// </summary>
        public int RequiredVipLevel { get; set; }

        /// <summary>
        /// 比特率（kbps）
        /// </summary>
        public int Bitrate { get; set; }

        public QualityInfo()
        {
        }

        public QualityInfo(string level, bool isAvailable, int requiredVipLevel = 0, int bitrate = 0)
        {
            Level = level;
            IsAvailable = isAvailable;
            RequiredVipLevel = requiredVipLevel;
            Bitrate = bitrate;
        }

        public override string ToString()
        {
            return $"{Level} ({Bitrate}kbps) - {(IsAvailable ? "可用" : "不可用")}";
        }
    }
}
