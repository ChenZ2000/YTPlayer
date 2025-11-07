using System;

namespace YTPlayer.Models
{
    public sealed class AudioOutputDeviceInfo
    {
        public const string WindowsDefaultId = "Default";

        public string DeviceId { get; set; } = WindowsDefaultId;

        public string DisplayName { get; set; } = string.Empty;

        public bool IsWindowsDefault { get; set; }

        public bool IsEnabled { get; set; } = true;

        public bool IsCurrent { get; set; }

        public int BassDeviceIndex { get; set; }

        public Guid? DeviceGuid { get; set; }

        public string? Driver { get; set; }

        public override string ToString() => DisplayName;
    }
}
