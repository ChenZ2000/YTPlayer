using System;
using System.Threading;
using System.Threading.Tasks;
using YTPlayer.Core.Playback.Cache;
using YTPlayer.Models;

namespace YTPlayer.Core.Playback
{
    /// <summary>
    /// 播放协调器：负责统一的播放会话管理、取消令牌控制和事件转发。
    /// </summary>
    public sealed class PlaybackOrchestrator : IDisposable
    {
        private readonly BassAudioEngine _audioEngine;

        private CancellationTokenSource? _sessionCts;
        private CancellationTokenSource? _seekCts;
        private SmartCacheManager? _attachedCache;
        private bool _disposed;

        public PlaybackOrchestrator(BassAudioEngine audioEngine)
        {
            _audioEngine = audioEngine ?? throw new ArgumentNullException(nameof(audioEngine));

            _audioEngine.BufferingStateChanged += AudioEngineOnBufferingStateChanged;
            _audioEngine.PlaybackStopped += AudioEngineOnPlaybackStopped;
            _audioEngine.PlaybackEnded += AudioEngineOnPlaybackEnded;
            // ⭐ 移除 PlaybackReachedHalfway 事件订阅（由新的统一预加载机制替代）
            _audioEngine.PlaybackError += AudioEngineOnPlaybackError;
        }

        public event EventHandler? BufferingStarted;
        public event EventHandler? BufferingCompleted;
        public event EventHandler<int>? BufferingProgress;
        public event EventHandler<SongInfo>? PlaybackStarted;
        public event EventHandler<SongInfo>? PlaybackEnded;
        public event EventHandler<double>? SeekCompleted;
        public event EventHandler<string>? ErrorOccurred;

        public async Task<bool> PlayAsync(SongInfo song, CancellationToken externalToken)
        {
            CancelCurrentSession();

            _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
            var token = _sessionCts.Token;

            BufferingStarted?.Invoke(this, EventArgs.Empty);

            bool success = await _audioEngine.PlayAsync(song, token).ConfigureAwait(false);

            if (!success)
            {
                BufferingCompleted?.Invoke(this, EventArgs.Empty);
                return false;
            }

            AttachToCache(_audioEngine.CurrentCacheManager);

            BufferingCompleted?.Invoke(this, EventArgs.Empty);
            PlaybackStarted?.Invoke(this, song);
            return true;
        }

        public Task<bool> PauseAsync(int fadeMilliseconds, CancellationToken token)
        {
            return _audioEngine.PauseWithFadeAsync(fadeMilliseconds, token);
        }

        public Task<bool> ResumeAsync(int fadeMilliseconds, CancellationToken token)
        {
            return _audioEngine.ResumeWithFadeAsync(fadeMilliseconds, token);
        }

        public async Task<bool> SeekAsync(double seconds, int fadeMilliseconds, CancellationToken externalToken)
        {
            if (_sessionCts == null)
            {
                return false;
            }

            _seekCts?.Cancel();
            _seekCts?.Dispose();
            _seekCts = CancellationTokenSource.CreateLinkedTokenSource(_sessionCts.Token, externalToken);
            var token = _seekCts.Token;

            SmartCacheManager? cache = _audioEngine.CurrentCacheManager;
            if (cache != null)
            {
                long targetBytes = _audioEngine.GetBytesFromSeconds(seconds);
                await cache.WaitForCacheReadyAsync(targetBytes, true, token).ConfigureAwait(false);
            }

            bool success = await _audioEngine.SetPositionWithFadeAsync(seconds, fadeMilliseconds, token).ConfigureAwait(false);

            if (success)
            {
                SeekCompleted?.Invoke(this, seconds);
            }

            return success;
        }

        public Task<bool> StopAsync()
        {
            CancelCurrentSession();
            AttachToCache(null);
            return Task.FromResult(_audioEngine.Stop());
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            CancelCurrentSession();
            AttachToCache(null);

            _audioEngine.BufferingStateChanged -= AudioEngineOnBufferingStateChanged;
            _audioEngine.PlaybackStopped -= AudioEngineOnPlaybackStopped;
            _audioEngine.PlaybackEnded -= AudioEngineOnPlaybackEnded;
            // ⭐ 移除 PlaybackReachedHalfway 事件订阅（由新的统一预加载机制替代）
            _audioEngine.PlaybackError -= AudioEngineOnPlaybackError;
        }

        private void CancelCurrentSession()
        {
            _seekCts?.Cancel();
            _seekCts?.Dispose();
            _seekCts = null;

            _sessionCts?.Cancel();
            _sessionCts?.Dispose();
            _sessionCts = null;
        }

        private void AttachToCache(SmartCacheManager? cache)
        {
            if (_attachedCache != null)
            {
                _attachedCache.BufferingProgressChanged -= CacheOnBufferingProgressChanged;
            }

            _attachedCache = cache;

            if (_attachedCache != null)
            {
                _attachedCache.BufferingProgressChanged += CacheOnBufferingProgressChanged;
            }
        }

        private void CacheOnBufferingProgressChanged(object? sender, int progress)
        {
            BufferingProgress?.Invoke(this, progress);
        }

        private void AudioEngineOnBufferingStateChanged(object? sender, BufferingState state)
        {
            switch (state)
            {
                case BufferingState.Buffering:
                    BufferingStarted?.Invoke(this, EventArgs.Empty);
                    break;
                case BufferingState.Ready:
                case BufferingState.Playing:
                    BufferingCompleted?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }

        private void AudioEngineOnPlaybackStopped(object? sender, EventArgs e)
        {
            AttachToCache(null);
            CancelCurrentSession();
        }

        private void AudioEngineOnPlaybackEnded(object? sender, SongInfo e)
        {
            PlaybackEnded?.Invoke(this, e);
        }

        private void AudioEngineOnPlaybackError(object? sender, string e)
        {
            ErrorOccurred?.Invoke(this, e);
        }
    }
}
