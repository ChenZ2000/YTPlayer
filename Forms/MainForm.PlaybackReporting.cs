using System;
using System.Globalization;
using YTPlayer.Core.Playback;
using YTPlayer.Models;

namespace YTPlayer
{
    public partial class MainForm
    {
        private PlaybackReportContext? _activePlaybackReport;
        private readonly object _playbackReportingLock = new object();

        private void InitializePlaybackReportingService()
        {
            if (_apiClient == null)
            {
                return;
            }

            if (_playbackReportingService == null)
            {
                _playbackReportingService = new PlaybackReportingService(_apiClient);
            }

            _playbackReportingService.UpdateSettings(_config);
        }

        private void PreparePlaybackReportingForNextSong(SongInfo? nextSong)
        {
            if (nextSong == null)
            {
                return;
            }

            PlaybackReportContext? snapshot;
            lock (_playbackReportingLock)
            {
                snapshot = _activePlaybackReport;
            }

            if (snapshot == null)
            {
                return;
            }

            if (string.Equals(snapshot.SongId, nextSong.Id, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            CompleteActivePlaybackSession(PlaybackEndReason.Interrupted);
        }

        private void BeginPlaybackReportingSession(SongInfo? song)
        {
            if (song == null || !CanReportPlayback(song))
            {
                ClearPlaybackReportingSession();
                return;
            }

            var source = BuildPlaybackSourceContext(song);
            int durationSeconds = NormalizeDurationSeconds(song);
            string resourceType = song.IsPodcastEpisode ? "djprogram" : "song";
            string? contentOverride = song.IsPodcastEpisode ? BuildPodcastContentOverride(song) : null;

            var session = new PlaybackReportContext(
                song.Id,
                song.Name ?? string.Empty,
                song.Artist ?? string.Empty,
                source,
                durationSeconds,
                song.IsTrial,
                resourceType)
            {
                StartedAt = DateTimeOffset.UtcNow,
                ContentOverride = contentOverride
            };

            lock (_playbackReportingLock)
            {
                _activePlaybackReport = session;
            }

            _playbackReportingService?.ReportSongStarted(session);
        }

        private void CompleteActivePlaybackSession(PlaybackEndReason reason, string? expectedSongId = null)
        {
            PlaybackReportContext? snapshot;
            lock (_playbackReportingLock)
            {
                snapshot = _activePlaybackReport;
                if (snapshot == null)
                {
                    return;
                }

                if (!string.IsNullOrWhiteSpace(expectedSongId) &&
                    !string.Equals(snapshot.SongId, expectedSongId, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (snapshot.HasCompleted)
                {
                    _activePlaybackReport = null;
                    return;
                }

                snapshot.HasCompleted = true;
                snapshot.PlayedSeconds = DeterminePlaybackSeconds(snapshot);
                _activePlaybackReport = null;
            }

            _playbackReportingService?.ReportSongCompleted(snapshot, reason);
        }

        private double DeterminePlaybackSeconds(PlaybackReportContext session)
        {
            double playedSeconds = 0;

            try
            {
                if (_audioEngine?.CurrentSong?.Id == session.SongId)
                {
                    playedSeconds = _audioEngine.GetPosition();
                    if (playedSeconds <= 0)
                    {
                        playedSeconds = _audioEngine.GetDuration();
                    }
                }
            }
            catch
            {
                playedSeconds = 0;
            }

            if (playedSeconds <= 0)
            {
                playedSeconds = (DateTimeOffset.UtcNow - session.StartedAt).TotalSeconds;
            }

            if (playedSeconds <= 0 && session.DurationSeconds > 0)
            {
                playedSeconds = session.DurationSeconds;
            }

            if (session.DurationSeconds > 0 && playedSeconds > session.DurationSeconds)
            {
                playedSeconds = session.DurationSeconds;
            }

            return Math.Max(1, playedSeconds);
        }

        private void ClearPlaybackReportingSession()
        {
            lock (_playbackReportingLock)
            {
                _activePlaybackReport = null;
            }
        }

        private bool CanReportPlayback(SongInfo song)
        {
            if (song == null)
            {
                return false;
            }

            if (_playbackReportingService == null || !_playbackReportingService.IsEnabled)
            {
                return false;
            }

            return _accountState?.IsLoggedIn == true;
        }

        private PlaybackSourceContext BuildPlaybackSourceContext(SongInfo song)
        {
            string sourceType = "list";
            string? sourceId = null;

            bool podcastContext = song.IsPodcastEpisode || IsPodcastEpisodeView();
            if (podcastContext)
            {
                var radioId = ResolvePodcastRadioId(song);
                if (radioId > 0)
                {
                    sourceId = radioId.ToString(CultureInfo.InvariantCulture);
                }
                else
                {
                    var radioName = song.PodcastRadioName;
                    if (!string.IsNullOrWhiteSpace(radioName))
                    {
                        sourceId = radioName;
                    }
                }

                return new PlaybackSourceContext("djradio", sourceId);
            }

            if (_currentPlaylist != null && !string.IsNullOrWhiteSpace(_currentPlaylist.Id))
            {
                sourceType = "list";
                sourceId = _currentPlaylist.Id;
            }
            else if (!string.IsNullOrWhiteSpace(song.AlbumId))
            {
                sourceType = "album";
                sourceId = song.AlbumId;
            }
            else if (!string.IsNullOrWhiteSpace(_currentViewSource))
            {
                var parts = _currentViewSource.Split(new[] { ':' }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2)
                {
                    sourceId = parts[1];
                    sourceType = parts[0].IndexOf("album", StringComparison.OrdinalIgnoreCase) >= 0 ? "album" : "list";
                }
            }

            return new PlaybackSourceContext(sourceType, sourceId);
        }

        private long ResolvePodcastRadioId(SongInfo song)
        {
            if (song.PodcastRadioId > 0)
            {
                return song.PodcastRadioId;
            }

            if (_currentPodcast?.Id > 0)
            {
                return _currentPodcast.Id;
            }

            if (!string.IsNullOrWhiteSpace(_currentViewSource) &&
                _currentViewSource.StartsWith("podcast:", StringComparison.OrdinalIgnoreCase))
            {
                var segments = _currentViewSource.Split(new[] { ':' }, StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length >= 2 && long.TryParse(segments[1], out var parsedId))
                {
                    return parsedId;
                }
            }

            return 0;
        }

        private string? BuildPodcastContentOverride(SongInfo song)
        {
            if (song == null)
            {
                return null;
            }

            long programId = song.PodcastProgramId;
            if (programId <= 0 && long.TryParse(song.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedId))
            {
                programId = parsedId;
            }

            if (programId <= 0)
            {
                return null;
            }

            return $"id={programId}";
        }

        private int NormalizeDurationSeconds(SongInfo song)
        {
            if (song == null)
            {
                return 0;
            }

            int durationSeconds = song.Duration;
            if (durationSeconds > 3600 * 12)
            {
                durationSeconds /= 1000;
            }

            if (durationSeconds <= 0)
            {
                try
                {
                    double engineDuration = _audioEngine?.GetDuration() ?? 0;
                    if (engineDuration > 0)
                    {
                        durationSeconds = (int)Math.Round(engineDuration);
                    }
                }
                catch
                {
                    durationSeconds = 0;
                }
            }

            if (durationSeconds <= 0)
            {
                durationSeconds = 60;
            }

            return durationSeconds;
        }
    }
}
