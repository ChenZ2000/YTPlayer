using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using YTPlayer.Models;

namespace YTPlayer.Core.Recognition
{
    internal sealed record SongRecognitionProgress(string Message, int CapturedSeconds, int MaximumSeconds, int Attempt);
    internal sealed class SongRecognitionException : Exception
    {
        public int Code { get; }
        public SongRecognitionException(int code, string message) : base($"{message}（错误码 {code}）") { Code = code; }
    }
    internal interface IAudioFingerprintGenerator
    {
        Task<string> GenerateAsync(byte[] pcmData, int sampleRate, int channels, CancellationToken token);
    }

    internal sealed class SongRecognitionCoordinator
    {
        private readonly IAudioCaptureSource _capture;
        private readonly IAudioFingerprintGenerator _fingerprint;
        private readonly Func<string, double, string, int, CancellationToken, Task<List<SongInfo>>> _match;
        private int _running;

        public SongRecognitionCoordinator(NeteaseApiClient api, AudioCaptureService capture, AudioFingerprintGenerator fingerprint)
            : this(capture, fingerprint, api.RecognizeSongAsync) { }
        internal SongRecognitionCoordinator(IAudioCaptureSource capture, IAudioFingerprintGenerator fingerprint,
            Func<string, double, string, int, CancellationToken, Task<List<SongInfo>>> match)
        { _capture = capture; _fingerprint = fingerprint; _match = match; }
        public IReadOnlyList<AudioInputDeviceInfo> EnumerateDevices() => _capture.EnumerateDevices();

        public async Task<SongRecognitionResult> RecognizeAsync(AudioInputDeviceInfo device, int durationSeconds,
            CancellationToken token, IProgress<SongRecognitionProgress>? progress = null)
        {
            ArgumentNullException.ThrowIfNull(device);
            if (durationSeconds < 3 || durationSeconds > 30) throw new ArgumentOutOfRangeException(nameof(durationSeconds));
            token.ThrowIfCancellationRequested();
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) throw new InvalidOperationException("正在听歌识曲，请等待完成或先取消。");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(50));
            try
            {
                string sessionId = Guid.NewGuid().ToString("N");
                int attempt = 0, failures = 0, captured = 0;
                bool heardSignal = false;
                Exception? lastFailure = null;
                progress?.Report(new(device.IsLoopback ? "正在聆听电脑播放的声音…" : "正在聆听麦克风声音…", 0, durationSeconds, 0));
                await foreach (byte[] pcm in _capture.CaptureProgressiveAsync(device, durationSeconds, deadline.Token).ConfigureAwait(false))
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    captured = pcm.Length / 16000;
                    byte[] window = SelectFingerprintWindow(pcm);
                    if (!HasSignal(window))
                    {
                        progress?.Report(new("尚未收到可识别的声音，请检查音源和音量…", captured, durationSeconds, attempt));
                        continue;
                    }
                    heardSignal = true;
                    progress?.Report(new($"已聆听 {captured} 秒，正在识别…", captured, durationSeconds, attempt + 1));
                    string fp = await _fingerprint.GenerateAsync(window, 8000, 1, deadline.Token).ConfigureAwait(false);
                    deadline.Token.ThrowIfCancellationRequested();
                    List<SongInfo> matches;
                    try
                    {
                        matches = await _match(fp, window.Length / 16000.0, sessionId, ++attempt, deadline.Token).ConfigureAwait(false);
                        deadline.Token.ThrowIfCancellationRequested();
                        failures = 0; lastFailure = null;
                    }
                    catch (Exception e) when (!deadline.IsCancellationRequested &&
                        (e is HttpRequestException || e is TimeoutException || e is SongRecognitionException { Code: -1 or >= 500 }))
                    {
                        lastFailure = e;
                        if (++failures >= 2) throw;
                        progress?.Report(new("识曲服务暂时未响应，继续聆听后重试…", captured, durationSeconds, attempt));
                        continue;
                    }
                    if (matches.Count > 0) return new SongRecognitionResult(sessionId, matches, captured);
                    progress?.Report(new($"已聆听 {captured} 秒，暂未匹配，继续聆听…", captured, durationSeconds, attempt));
                }
                deadline.Token.ThrowIfCancellationRequested();
                if (!heardSignal) throw new InvalidOperationException(device.IsLoopback
                    ? "没有检测到有效的系统声音，请确认音乐正在所选播放设备上播放。" : "没有检测到有效声音，请检查麦克风权限、静音开关和输入音量。");
                if (lastFailure != null) throw lastFailure;
                return new SongRecognitionResult(sessionId, Array.Empty<SongInfo>(), captured);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
            { throw new TimeoutException("听歌识曲超时，请检查音频设备或网络后重试。"); }
            finally { Interlocked.Exchange(ref _running, 0); }
        }

        internal static byte[] SelectFingerprintWindow(byte[] pcm) => pcm.Length <= AudioFingerprintGenerator.MaximumWindowSeconds * 16000
            ? pcm : pcm.AsSpan(pcm.Length - AudioFingerprintGenerator.MaximumWindowSeconds * 16000).ToArray();

        internal static bool HasSignal(byte[] pcm)
        {
            if (pcm.Length < 2 || pcm.Length % 2 != 0) return false;
            double sum = 0, sumSquares = 0;
            int samples = pcm.Length / 2;
            for (int i = 0; i < pcm.Length; i += 2)
            {
                short sample = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i, 2));
                sum += sample; sumSquares += (double)sample * sample;
            }
            // Reject digital silence/DC only; preserve quiet music instead of noise-gating it.
            return sumSquares / samples - Math.Pow(sum / samples, 2) >= 1;
        }
    }
}
