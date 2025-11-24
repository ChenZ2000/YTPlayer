using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using YTPlayer.Models;

namespace YTPlayer.Core.Recognition
{
    /// <summary>
    /// 串联录制、指纹与 API 调用的协调器。
    /// </summary>
    internal sealed class SongRecognitionCoordinator
    {
        private readonly NeteaseApiClient _apiClient;
        private readonly AudioCaptureService _captureService;
        private readonly AudioFingerprintGenerator _fingerprintGenerator;

        public SongRecognitionCoordinator(
            NeteaseApiClient apiClient,
            AudioCaptureService captureService,
            AudioFingerprintGenerator fingerprintGenerator)
        {
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            _captureService = captureService ?? throw new ArgumentNullException(nameof(captureService));
            _fingerprintGenerator = fingerprintGenerator ?? throw new ArgumentNullException(nameof(fingerprintGenerator));
        }

        public IReadOnlyList<AudioInputDeviceInfo> EnumerateDevices() => _captureService.EnumerateDevices();

        /// <summary>
        /// 执行一次识曲。
        /// </summary>
        public async Task<SongRecognitionResult> RecognizeAsync(
            AudioInputDeviceInfo device,
            int durationSeconds,
            CancellationToken cancellationToken)
        {
            if (device == null) throw new ArgumentNullException(nameof(device));
            if (durationSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(durationSeconds));

            // 1) 采集音频
            var pcm = await _captureService.CaptureAsync(device, durationSeconds, cancellationToken).ConfigureAwait(false);

            // 2) 生成指纹
            var fingerprint = await _fingerprintGenerator.GenerateAsync(pcm, 8000, 1, cancellationToken)
                .ConfigureAwait(false);

            // 3) 调用后端识别
            var matches = await _apiClient.RecognizeSongAsync(fingerprint, durationSeconds, cancellationToken)
                .ConfigureAwait(false);

            return new SongRecognitionResult(Guid.NewGuid().ToString("N"), matches, durationSeconds)
            {
                FingerprintPreview = fingerprint != null && fingerprint.Length > 16
                    ? fingerprint.Substring(0, 16) + "..."
                    : fingerprint
            };
        }
    }
}
