using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using YTPlayer.Models;

namespace YTPlayer.Core.Recognition
{
    /// <summary>
    /// 基于 BASS 的轻量录制服务，用于听歌识曲采样。
    /// </summary>
    internal sealed class AudioCaptureService
    {
        #region BASS P/Invoke

        private const int BASS_ERROR_OK = 0;
        private const int BASS_ERROR_DEVICE = 3;
        private const int BASS_ERROR_ALREADY = 14;
        private const int BASS_ERROR_FORMAT = 6;

        private const int BASS_DEVICE_ENABLED_FLAG = 0x1;
        private const int BASS_DEVICE_DEFAULT_FLAG = 0x2;

        [DllImport("bass.dll")]
        private static extern bool BASS_RecordGetDeviceInfo(int device, out BASS_DEVICEINFO info);

        [DllImport("bass.dll")]
        private static extern bool BASS_RecordInit(int device);

        [DllImport("bass.dll")]
        private static extern bool BASS_RecordFree();

        [DllImport("bass.dll")]
        private static extern int BASS_RecordStart(int freq, int chans, int flags, RECORDPROC proc, IntPtr user);

        [DllImport("bass.dll")]
        private static extern bool BASS_ChannelStop(int handle);

        [DllImport("bass.dll")]
        private static extern int BASS_ErrorGetCode();

        private delegate bool RECORDPROC(int handle, IntPtr buffer, int length, IntPtr user);

        [StructLayout(LayoutKind.Sequential)]
        private struct BASS_DEVICEINFO
        {
            public IntPtr name;
            public IntPtr driver;
            public int flags;
        }

        #endregion

        /// <summary>
        /// 枚举录音设备，附带默认项。
        /// </summary>
        public IReadOnlyList<AudioInputDeviceInfo> EnumerateDevices()
        {
            var descriptors = new List<(int index, string name, string driver, bool isEnabled, bool isDefault)>();

            int index = 0;
            while (BASS_RecordGetDeviceInfo(index, out var info))
            {
                string name = Marshal.PtrToStringAnsi(info.name) ?? $"设备 {index}";
                string driver = Marshal.PtrToStringAnsi(info.driver) ?? string.Empty;
                bool isEnabled = (info.flags & BASS_DEVICE_ENABLED_FLAG) == BASS_DEVICE_ENABLED_FLAG;
                bool isDefault = (info.flags & BASS_DEVICE_DEFAULT_FLAG) == BASS_DEVICE_DEFAULT_FLAG;

                if (isEnabled)
                {
                    descriptors.Add((index, name, driver, isEnabled, isDefault));
                }

                index++;
            }

            var result = new List<AudioInputDeviceInfo>();

            if (descriptors.Count > 0)
            {
                var defaultDescriptor = descriptors.FirstOrDefault(d => d.isDefault);
                if (defaultDescriptor == default)
                {
                    defaultDescriptor = descriptors[0];
                }

                string defaultName = string.IsNullOrWhiteSpace(defaultDescriptor.name)
                    ? "Windows 默认"
                    : $"Windows 默认（当前：{defaultDescriptor.name})";

                result.Add(new AudioInputDeviceInfo
                {
                    DeviceId = AudioInputDeviceInfo.WindowsDefaultId,
                    DisplayName = defaultName,
                    BassDeviceIndex = -1, // 让 BASS 选择当前 Windows 默认
                    IsDefault = true,
                    IsLoopback = false,
                    IsCurrent = true
                });
            }

            foreach (var descriptor in descriptors)
            {
                if (descriptor.isDefault && IsPlaceholderDefaultRecordDevice(descriptor.name, descriptor.driver))
                {
                    continue; // 合并“Default (默认)”占位项
                }

                result.Add(new AudioInputDeviceInfo
                {
                    DeviceId = $"rec:{descriptor.index}",
                    DisplayName = descriptor.isDefault ? $"{descriptor.name} (默认)" : descriptor.name,
                    BassDeviceIndex = descriptor.index,
                    IsDefault = descriptor.isDefault,
                    IsLoopback = false
                });
            }

            return result;
        }

        /// <summary>
        /// 录制指定时长的 16-bit PCM（单声道），默认 8000 Hz。
        /// </summary>
        public async Task<byte[]> CaptureAsync(AudioInputDeviceInfo? device, int durationSeconds, CancellationToken cancellationToken)
        {
            if (durationSeconds <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(durationSeconds), "录制时长必须大于 0");
            }

            int deviceIndex = device?.BassDeviceIndex ?? -1;
            if (!BASS_RecordInit(deviceIndex))
            {
                throw new InvalidOperationException($"录音设备初始化失败: {GetErrorMessage()}");
            }

            var buffer = new MemoryStream();
            bool stopRequested = false;

            RECORDPROC callback = (handle, ptr, len, user) =>
            {
                if (stopRequested || cancellationToken.IsCancellationRequested)
                {
                    return false;
                }

                if (ptr != IntPtr.Zero && len > 0)
                {
                    var tmp = new byte[len];
                    Marshal.Copy(ptr, tmp, 0, len);
                    lock (buffer)
                    {
                        buffer.Write(tmp, 0, tmp.Length);
                    }
                }

                return true;
            };

            // 目标 8kHz / mono / 16-bit
            int handleId = BASS_RecordStart(8000, 1, 0, callback, IntPtr.Zero);
            if (handleId == 0)
            {
                var code = BASS_ErrorGetCode();
                BASS_RecordFree();
                throw new InvalidOperationException($"开始录制失败: {GetErrorMessage(code)}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(durationSeconds), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 允许取消，继续向下走停止录制
            }
            finally
            {
                stopRequested = true;
                BASS_ChannelStop(handleId);
                BASS_RecordFree();
            }

            lock (buffer)
            {
                return buffer.ToArray();
            }
        }

        private static string GetErrorMessage(int? codeOverride = null)
        {
            int code = codeOverride ?? BASS_ErrorGetCode();
            switch (code)
            {
                case BASS_ERROR_OK: return "成功";
                case BASS_ERROR_DEVICE: return "录音设备不可用或不存在";
                case BASS_ERROR_ALREADY: return "录音设备已初始化";
                case BASS_ERROR_FORMAT: return "录音参数不被设备支持";
                default: return $"未知错误代码 {code}";
            }
        }

        private static bool IsPlaceholderDefaultRecordDevice(string name, string driver)
        {
            string n = name?.Trim() ?? string.Empty;
            string d = driver?.Trim() ?? string.Empty;

            if (string.IsNullOrEmpty(n) && string.IsNullOrEmpty(d))
            {
                return true;
            }

            if (string.Equals(n, "default", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(d, "default", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }
    }
}
