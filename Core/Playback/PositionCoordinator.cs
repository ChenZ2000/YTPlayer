using System;

namespace YTPlayer.Core.Playback
{
    public sealed class PositionCoordinator
    {
        private readonly object _lock = new object();

        private double _lastRequestedTarget = -1;
        private DateTime _lastRequestAtUtc = DateTime.MinValue;
        private bool _lastRequestPreview = false;
        private long _lastRequestVersion = 0;

        private double _lastExecutedTarget = -1;
        private DateTime _lastExecuteAtUtc = DateTime.MinValue;
        private bool _lastExecutePreview = false;
        private long _lastExecuteVersion = 0;
        private bool _lastExecuteSuccess = false;

        private double _lastEnginePosition = 0;
        private DateTime _lastEngineUpdateUtc = DateTime.MinValue;

        private string? _trackId;

        private const int REQUEST_WINDOW_MS = 1200;
        private const int PREVIEW_WINDOW_MS = 350;
        private const int EXECUTE_WINDOW_MS = 1500;
        private const double ENGINE_JUMP_GUARD_SECONDS = 6.0;

        public void ResetForTrack(string? trackId)
        {
            lock (_lock)
            {
                if (!string.IsNullOrWhiteSpace(_trackId) && string.Equals(_trackId, trackId, StringComparison.Ordinal))
                {
                    return;
                }

                _trackId = trackId;
                _lastRequestedTarget = -1;
                _lastRequestAtUtc = DateTime.MinValue;
                _lastRequestPreview = false;
                _lastRequestVersion = 0;
                _lastExecutedTarget = -1;
                _lastExecuteAtUtc = DateTime.MinValue;
                _lastExecutePreview = false;
                _lastExecuteVersion = 0;
                _lastExecuteSuccess = false;
                _lastEnginePosition = 0;
                _lastEngineUpdateUtc = DateTime.MinValue;
            }
        }

        public void OnSeekRequested(double targetSeconds, bool isPreview, long version)
        {
            lock (_lock)
            {
                _lastRequestedTarget = targetSeconds;
                _lastRequestAtUtc = DateTime.UtcNow;
                _lastRequestPreview = isPreview;
                _lastRequestVersion = version;
            }
        }

        public void OnSeekExecuted(double targetSeconds, bool success, bool isPreview, long version)
        {
            lock (_lock)
            {
                _lastExecuteSuccess = success;
                _lastExecutePreview = isPreview;
                _lastExecuteVersion = version;
                _lastExecuteAtUtc = DateTime.UtcNow;

                if (success)
                {
                    _lastExecutedTarget = targetSeconds;
                }
            }
        }

        public double GetSeekBasePosition(double enginePosition, double durationSeconds, bool isPlaying, bool preferSeekTarget = false)
        {
            DateTime now = DateTime.UtcNow;
            double basePosition = enginePosition;

            lock (_lock)
            {
                if (preferSeekTarget && _lastRequestedTarget >= 0)
                {
                    basePosition = AdjustForPlayback(_lastRequestedTarget, now - _lastRequestAtUtc, isPlaying);
                }
                else if (IsRecentRequest(now))
                {
                    basePosition = AdjustForPlayback(_lastRequestedTarget, now - _lastRequestAtUtc, isPlaying);
                }
                else if (IsRecentExecute(now))
                {
                    basePosition = AdjustForPlayback(_lastExecutedTarget, now - _lastExecuteAtUtc, isPlaying);
                }
            }

            return ClampPosition(basePosition, durationSeconds);
        }

        public double GetEffectivePosition(double enginePosition, double durationSeconds, bool isPlaying)
        {
            DateTime now = DateTime.UtcNow;
            double basePosition = enginePosition;
            bool shouldUseBase = false;

            lock (_lock)
            {
                if (IsRecentRequest(now))
                {
                    basePosition = AdjustForPlayback(_lastRequestedTarget, now - _lastRequestAtUtc, isPlaying);
                    if (IsEnginePositionUnstable(enginePosition, basePosition))
                    {
                        shouldUseBase = true;
                    }
                }
                else if (IsRecentExecute(now))
                {
                    basePosition = AdjustForPlayback(_lastExecutedTarget, now - _lastExecuteAtUtc, isPlaying);
                    if (IsEnginePositionUnstable(enginePosition, basePosition))
                    {
                        shouldUseBase = true;
                    }
                }

                if (enginePosition <= 0 && basePosition > 0)
                {
                    shouldUseBase = true;
                }

                double effectivePosition = shouldUseBase ? basePosition : enginePosition;
                effectivePosition = ClampPosition(effectivePosition, durationSeconds);

                _lastEnginePosition = effectivePosition;
                _lastEngineUpdateUtc = now;

                return effectivePosition;
            }
        }

        private bool IsRecentRequest(DateTime now)
        {
            if (_lastRequestedTarget < 0)
            {
                return false;
            }

            int window = _lastRequestPreview ? PREVIEW_WINDOW_MS : REQUEST_WINDOW_MS;
            return (now - _lastRequestAtUtc).TotalMilliseconds <= window;
        }

        private bool IsRecentExecute(DateTime now)
        {
            if (!_lastExecuteSuccess || _lastExecutedTarget < 0)
            {
                return false;
            }

            return (now - _lastExecuteAtUtc).TotalMilliseconds <= EXECUTE_WINDOW_MS;
        }

        private static double AdjustForPlayback(double targetSeconds, TimeSpan elapsed, bool isPlaying)
        {
            if (!isPlaying)
            {
                return targetSeconds;
            }

            double adjusted = targetSeconds + elapsed.TotalSeconds;
            return adjusted;
        }

        private static bool IsEnginePositionUnstable(double enginePosition, double basePosition)
        {
            if (enginePosition <= 0 || basePosition <= 0)
            {
                return true;
            }

            return Math.Abs(enginePosition - basePosition) >= ENGINE_JUMP_GUARD_SECONDS;
        }

        private static double ClampPosition(double positionSeconds, double durationSeconds)
        {
            if (durationSeconds <= 0)
            {
                return Math.Max(0, positionSeconds);
            }

            double maxTarget = Math.Max(0, durationSeconds - 0.05);
            if (positionSeconds < 0)
            {
                return 0;
            }
            if (positionSeconds > maxTarget)
            {
                return maxTarget;
            }
            return positionSeconds;
        }
    }
}
