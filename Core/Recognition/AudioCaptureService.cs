using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using YTPlayer.Models;

namespace YTPlayer.Core.Recognition
{
    internal interface IAudioCaptureSource
    {
        IReadOnlyList<AudioInputDeviceInfo> EnumerateDevices();
        IAsyncEnumerable<byte[]> CaptureProgressiveAsync(AudioInputDeviceInfo device, int durationSeconds, CancellationToken token);
    }

    /// <summary>Continuous PCM capture. Recognition consumes cumulative, sample-counted snapshots.</summary>
    internal sealed class AudioCaptureService : IAudioCaptureSource
    {
        private const int Enabled = 1, Default = 2, Loopback = 8;
        private static readonly SemaphoreSlim CaptureGate = new(1, 1);
        [DllImport("bass.dll")] private static extern bool BASS_RecordGetDeviceInfo(int device, out DeviceInfo info);
        [DllImport("bass.dll")] private static extern bool BASS_GetDeviceInfo(int device, out DeviceInfo info);
        [DllImport("bass.dll")] private static extern bool BASS_SetConfig(int option, int value);
        [DllImport("bass.dll")] private static extern int BASS_GetConfig(int option);
        [DllImport("bass.dll")] private static extern bool BASS_RecordInit(int device);
        [DllImport("bass.dll")] private static extern int BASS_RecordGetDevice();
        [DllImport("bass.dll")] private static extern bool BASS_RecordSetDevice(int device);
        [DllImport("bass.dll")] private static extern bool BASS_RecordFree();
        [DllImport("bass.dll")] private static extern int BASS_RecordStart(int freq, int chans, int flags, RecordProc proc, IntPtr user);
        [DllImport("bass.dll")] private static extern bool BASS_ChannelStop(int handle);
        [DllImport("bass.dll")] private static extern int BASS_ChannelIsActive(int handle);
        [DllImport("bass.dll")] private static extern int BASS_ErrorGetCode();
        private delegate bool RecordProc(int handle, IntPtr buffer, int length, IntPtr user);
        [StructLayout(LayoutKind.Sequential)]
        private struct DeviceInfo { public IntPtr Name, Driver; public int Flags; }
        private static string Text(IntPtr value) => (BASS_GetConfig(42) != 0 ? Marshal.PtrToStringUTF8(value) : Marshal.PtrToStringAnsi(value)) ?? "";

        public IReadOnlyList<AudioInputDeviceInfo> EnumerateDevices()
        {
            BASS_SetConfig(28, 1); // BASS_CONFIG_REC_LOOPBACK: expose output capture devices.
            var result = new List<AudioInputDeviceInfo>();
            string defaultOutput = "";
            for (int i = 1; BASS_GetDeviceInfo(i, out var info); i++)
                if ((info.Flags & (Enabled | Default)) == (Enabled | Default) && !string.IsNullOrEmpty(Text(info.Driver)))
                { defaultOutput = Text(info.Driver); break; }
            for (int i = 0; BASS_RecordGetDeviceInfo(i, out var info); i++)
            {
                if ((info.Flags & Enabled) == 0) continue;
                string name = Text(info.Name), endpoint = Text(info.Driver);
                bool loopback = (info.Flags & Loopback) != 0;
                bool isDefault = !loopback && (info.Flags & Default) != 0;
                if (isDefault && result.All(d => d.DeviceId != AudioInputDeviceInfo.WindowsDefaultId))
                    result.Insert(0, new AudioInputDeviceInfo { DeviceId = AudioInputDeviceInfo.WindowsDefaultId,
                        DisplayName = "麦克风：Windows 默认输入设备", BassDeviceIndex = -1, IsDefault = true });
                if (name.Equals("Default", StringComparison.OrdinalIgnoreCase) || endpoint.Equals("Default", StringComparison.OrdinalIgnoreCase)) continue;
                if (loopback && endpoint == defaultOutput)
                    result.Add(new AudioInputDeviceInfo { DeviceId = AudioInputDeviceInfo.LoopbackId,
                        DisplayName = "系统内录：Windows 默认播放设备（" + name + "）", BassDeviceIndex = i, IsLoopback = true });
                result.Add(new AudioInputDeviceInfo { DeviceId = string.IsNullOrEmpty(endpoint) ? $"rec:{i}" : "endpoint:" + endpoint,
                    DisplayName = (loopback ? "系统内录：" : "录音输入：") + name,
                    BassDeviceIndex = i, IsDefault = isDefault, IsLoopback = loopback });
            }
            return result;
        }

        public async Task<byte[]> CaptureAsync(AudioInputDeviceInfo device, int durationSeconds, CancellationToken token)
        {
            byte[] result = Array.Empty<byte>();
            await foreach (var pcm in CaptureProgressiveAsync(device, durationSeconds, token).ConfigureAwait(false)) result = pcm;
            return result;
        }

        public async IAsyncEnumerable<byte[]> CaptureProgressiveAsync(AudioInputDeviceInfo device, int durationSeconds,
            [EnumeratorCancellation] CancellationToken token)
        {
            if (durationSeconds < 1 || durationSeconds > 30) throw new ArgumentOutOfRangeException(nameof(durationSeconds));
            token.ThrowIfCancellationRequested();
            await CaptureGate.WaitAsync(token).ConfigureAwait(false);
            int handle = 0, recordingDevice = -1, stopping = 0, written = 0;
            bool ownsDevice = false;
            byte[] buffer = new byte[durationSeconds * 16000];
            object bufferLock = new();
            Exception? callbackError = null;
            RecordProc callback = (_, pointer, length, _) =>
            {
                if (Volatile.Read(ref stopping) != 0 || token.IsCancellationRequested) return false;
                try
                {
                    lock (bufferLock)
                    {
                        int count = Math.Min(length, buffer.Length - written) & ~1;
                        if (pointer != IntPtr.Zero && count > 0) { Marshal.Copy(pointer, buffer, written, count); written += count; }
                        return written < buffer.Length;
                    }
                }
                catch (Exception ex) { lock (bufferLock) callbackError = ex; return false; }
            };
            try
            {
                // Resolve endpoint identity afresh after USB/default-device changes.
                var current = EnumerateDevices().FirstOrDefault(d => d.DeviceId == device.DeviceId)
                    ?? throw new InvalidOperationException("所选音频设备已断开，请重新选择聆听设备。");
                if (!BASS_RecordInit(current.BassDeviceIndex)) throw CaptureError("录音设备初始化失败");
                ownsDevice = true;
                recordingDevice = BASS_RecordGetDevice();
                handle = BASS_RecordStart(8000, 1, 0, callback, IntPtr.Zero);
                if (handle == 0) throw CaptureError("开始录制失败");
                var elapsed = Stopwatch.StartNew();
                int target = Math.Min(3, durationSeconds) * 16000;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    byte[]? snapshot = null;
                    lock (bufferLock)
                    {
                        if (callbackError != null) throw new InvalidOperationException("读取音频数据失败。", callbackError);
                        if (written >= target) snapshot = buffer.AsSpan(0, target).ToArray();
                    }
                    if (snapshot != null)
                    {
                        yield return snapshot;
                        if (target == buffer.Length) break;
                        target = Math.Min(buffer.Length, target + 3 * 16000);
                        continue;
                    }
                    if (BASS_ChannelIsActive(handle) == 0) throw new InvalidOperationException("录音设备已停止，请检查设备连接或麦克风权限。");
                    if (elapsed.Elapsed.TotalSeconds > durationSeconds + 5)
                        throw new InvalidOperationException(current.IsLoopback ? "没有收到足够的系统声音，请确认音乐正在所选播放设备上播放。" : "没有收到足够的录音，请检查麦克风权限和输入设备。");
                    await Task.Delay(40, token).ConfigureAwait(false);
                }
            }
            finally
            {
                Interlocked.Exchange(ref stopping, 1);
                if (handle != 0) BASS_ChannelStop(handle);
                // BASS recording-device selection is thread-local; await may change threads.
                if (ownsDevice && BASS_RecordSetDevice(recordingDevice)) BASS_RecordFree();
                GC.KeepAlive(callback);
                CaptureGate.Release();
            }
        }

        private static Exception CaptureError(string operation)
        {
            int code = BASS_ErrorGetCode();
            string reason = code switch { 3 or 23 => "设备不可用或已断开", 6 => "设备不支持所需的录音格式", 14 or 46 => "设备正在被占用", 49 => "未获得录音权限，请检查 Windows 麦克风隐私设置", _ => $"音频错误 {code}" };
            return new InvalidOperationException(operation + "：" + reason);
        }
    }
}
