using System;

namespace YTPlayer.Models
{
    /// <summary>
    /// 音频输入/聆听设备描述（用于听歌识曲录制）。
    /// </summary>
    public sealed class AudioInputDeviceInfo
    {
        public const string WindowsDefaultId = "Default";
        public const string LoopbackId = "Loopback";

        /// <summary>
        /// 设备唯一标识。默认输入设备使用 "Default"，环回使用 "Loopback"。
        /// </summary>
        public string DeviceId { get; set; } = WindowsDefaultId;

        /// <summary>
        /// 显示名称（用于 UI 组合框）。
        /// </summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>
        /// BASS 设备索引（录音设备列表索引，默认 0）。
        /// </summary>
        public int BassDeviceIndex { get; set; }

        /// <summary>
        /// 是否为系统默认录音设备。
        /// </summary>
        public bool IsDefault { get; set; }

        /// <summary>
        /// 是否为环回（系统播放输出的捕获）。
        /// </summary>
        public bool IsLoopback { get; set; }

        /// <summary>
        /// 当前选择标记。
        /// </summary>
        public bool IsCurrent { get; set; }

        public override string ToString() => DisplayName;
    }
}
