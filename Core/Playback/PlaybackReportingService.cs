using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using YTPlayer.Models;

namespace YTPlayer.Core.Playback
{
    public enum PlaybackEndReason
    {
        Completed,
        Interrupted,
        Stopped
    }

    public sealed class PlaybackSourceContext
    {
        public PlaybackSourceContext(string? source, string? sourceId)
        {
            Source = string.IsNullOrWhiteSpace(source) ? "list" : source!.Trim();
            if (!string.IsNullOrWhiteSpace(sourceId))
            {
                SourceId = sourceId!.Trim();
                if (long.TryParse(SourceId, out var parsed))
                {
                    SourceIdLong = parsed;
                }
            }
        }

        public string Source { get; }
        public string? SourceId { get; }
        public long? SourceIdLong { get; }

        public string Content
        {
            get
            {
                if (SourceIdLong.HasValue)
                {
                    return $"id={SourceIdLong.Value}";
                }

                if (!string.IsNullOrWhiteSpace(SourceId))
                {
                    return $"id={SourceId}";
                }

                return string.Empty;
            }
        }
    }

    public sealed class PlaybackReportContext
    {
        public PlaybackReportContext(
            string songId,
            string songName,
            string artist,
            PlaybackSourceContext source,
            int durationSeconds,
            bool isTrial,
            string resourceType)
        {
            if (string.IsNullOrWhiteSpace(songId))
            {
                throw new ArgumentNullException(nameof(songId));
            }

            SongId = songId;
            SongName = songName ?? string.Empty;
            Artist = artist ?? string.Empty;
            Source = source ?? throw new ArgumentNullException(nameof(source));
            DurationSeconds = durationSeconds;
            IsTrial = isTrial;
            ResourceType = string.IsNullOrWhiteSpace(resourceType) ? "song" : resourceType.Trim();
            StartedAt = DateTimeOffset.UtcNow;
        }

        public string SongId { get; }
        public string SongName { get; }
        public string Artist { get; }
        public PlaybackSourceContext Source { get; }
        public int DurationSeconds { get; }
        public bool IsTrial { get; }
        public string ResourceType { get; }
        public string? ContentOverride { get; set; }
        public DateTimeOffset StartedAt { get; set; }
        public double PlayedSeconds { get; set; }
        internal bool HasCompleted { get; set; }
    }

    internal enum PlaybackReportOperationKind
    {
        Start,
        Complete
    }

    internal sealed class PlaybackReportOperation
    {
        public PlaybackReportOperation(PlaybackReportOperationKind kind, PlaybackReportContext context, PlaybackEndReason endReason = PlaybackEndReason.Completed)
        {
            Kind = kind;
            Context = context;
            EndReason = endReason;
        }

        public PlaybackReportOperationKind Kind { get; }
        public PlaybackReportContext Context { get; }
        public PlaybackEndReason EndReason { get; }
    }

    public sealed class PlaybackReportingService : IDisposable
    {
        private readonly NeteaseApiClient _apiClient;
        private readonly BlockingCollection<PlaybackReportOperation> _queue;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Task _workerTask;
        private bool _disposed;

        public PlaybackReportingService(NeteaseApiClient apiClient)
        {
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            _queue = new BlockingCollection<PlaybackReportOperation>(new ConcurrentQueue<PlaybackReportOperation>());
            _workerTask = Task.Run(() => ProcessQueueAsync(_cts.Token));
        }

        public bool IsEnabled { get; private set; } = true;

        public void UpdateSettings(ConfigModel? config)
        {
            if (config == null)
            {
                IsEnabled = true;
                return;
            }

            IsEnabled = config.PlaybackReportingEnabled;
        }

        public void ReportSongStarted(PlaybackReportContext context)
        {
            if (context == null)
            {
                return;
            }

            context.StartedAt = DateTimeOffset.UtcNow;
            context.HasCompleted = false;
            Enqueue(new PlaybackReportOperation(PlaybackReportOperationKind.Start, context));
        }

        public void ReportSongCompleted(PlaybackReportContext context, PlaybackEndReason reason)
        {
            if (context == null)
            {
                return;
            }

            context.HasCompleted = true;
            Enqueue(new PlaybackReportOperation(PlaybackReportOperationKind.Complete, context, reason));
        }

        private void Enqueue(PlaybackReportOperation operation)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _queue.Add(operation);
            }
            catch (InvalidOperationException)
            {
                // queue completed, ignore
            }
        }

        private async Task ProcessQueueAsync(CancellationToken token)
        {
            try
            {
                foreach (var operation in _queue.GetConsumingEnumerable(token))
                {
                    if (!IsEnabled)
                    {
                        continue;
                    }

                    try
                    {
                        switch (operation.Kind)
                        {
                            case PlaybackReportOperationKind.Start:
                                await SendStartLogsAsync(operation.Context).ConfigureAwait(false);
                                break;
                            case PlaybackReportOperationKind.Complete:
                                await SendCompleteLogAsync(operation.Context, operation.EndReason).ConfigureAwait(false);
                                break;
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[PlaybackReporting] 发送失败: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
        }

        private Task<bool> SendStartLogsAsync(PlaybackReportContext context)
        {
            var logs = new List<Dictionary<string, object>>();
            string content = BuildContent(context);

            logs.Add(new Dictionary<string, object>
            {
                { "action", "startplay" },
                {
                    "json", new Dictionary<string, object>
                    {
                        { "id", context.SongId },
                        { "type", context.ResourceType },
                        { "mainsite", "1" },
                        { "content", content }
                    }
                }
            });

            logs.Add(new Dictionary<string, object>
            {
                { "action", "play" },
                {
                    "json", new Dictionary<string, object>
                    {
                        { "id", context.SongId },
                        { "type", context.ResourceType },
                        { "source", string.IsNullOrWhiteSpace(context.Source.Source) ? "list" : context.Source.Source },
                        { "sourceid", context.Source.SourceIdLong ?? (object)(context.Source.SourceId ?? string.Empty) },
                        { "mainsite", "1" },
                        { "content", content }
                    }
                }
            });

            return _apiClient.SendPlaybackLogsAsync(logs);
        }

        private Task<bool> SendCompleteLogAsync(PlaybackReportContext context, PlaybackEndReason reason)
        {
            int reportedSeconds = (int)Math.Max(1, Math.Round(context.PlayedSeconds > 0 ? context.PlayedSeconds : context.DurationSeconds));
            if (context.DurationSeconds > 0)
            {
                reportedSeconds = Math.Min(context.DurationSeconds, reportedSeconds);
            }

            var log = new Dictionary<string, object>
            {
                { "action", "play" },
                {
                    "json", new Dictionary<string, object>
                    {
                        { "type", context.ResourceType },
                        { "wifi", 0 },
                        { "download", 0 },
                        { "id", context.SongId },
                        { "time", reportedSeconds },
                        { "end", MapEndReason(reason) },
                        { "source", string.IsNullOrWhiteSpace(context.Source.Source) ? "list" : context.Source.Source },
                        { "sourceId", context.Source.SourceIdLong ?? (object)(context.Source.SourceId ?? string.Empty) },
                        { "mainsite", "1" },
                        { "content", BuildContent(context) }
                    }
                }
            };

            return _apiClient.SendPlaybackLogsAsync(new[] { log });
        }

        private static string MapEndReason(PlaybackEndReason reason)
        {
            switch (reason)
            {
                case PlaybackEndReason.Completed:
                    return "playend";
                case PlaybackEndReason.Stopped:
                    return "ui";
                default:
                    return "interrupt";
            }
        }

        private static string BuildContent(PlaybackReportContext context)
        {
            if (!string.IsNullOrWhiteSpace(context.ContentOverride))
            {
                return context.ContentOverride!;
            }

            return BuildContent(context.Source);
        }

        private static string BuildContent(PlaybackSourceContext source)
        {
            if (source == null)
            {
                return "id=0";
            }

            string content = source.Content;
            if (string.IsNullOrWhiteSpace(content))
            {
                return "id=0";
            }

            return content;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cts.Cancel();
            _queue.CompleteAdding();

            try
            {
                _workerTask.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
                // ignored
            }
            catch (ObjectDisposedException)
            {
            }

            _cts.Dispose();
            _queue.Dispose();
        }
    }
}
