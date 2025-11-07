namespace YTPlayer.Models
{
    public sealed class AudioDeviceSwitchResult
    {
        private AudioDeviceSwitchResult(bool success, bool isNoOp, string? errorMessage, AudioOutputDeviceInfo? device)
        {
            IsSuccess = success;
            IsNoOp = isNoOp;
            ErrorMessage = errorMessage;
            Device = device;
        }

        public bool IsSuccess { get; }

        public bool IsNoOp { get; }

        public string? ErrorMessage { get; }

        public AudioOutputDeviceInfo? Device { get; }

        public static AudioDeviceSwitchResult Success(AudioOutputDeviceInfo device) =>
            new AudioDeviceSwitchResult(true, false, null, device);

        public static AudioDeviceSwitchResult NoChange(AudioOutputDeviceInfo? device) =>
            new AudioDeviceSwitchResult(true, true, null, device);

        public static AudioDeviceSwitchResult Failure(string message) =>
            new AudioDeviceSwitchResult(false, false, string.IsNullOrWhiteSpace(message) ? "输出设备切换失败" : message, null);
    }
}
