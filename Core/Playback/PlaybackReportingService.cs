using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
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
        public string AccountKey { get; set; } = string.Empty;
        internal bool HasCompleted { get; set; }
        internal bool CompleteQueued { get; set; }
        private double _lastPosition;
        private long _lastTick;
        private bool _wasPlaying;

        // Count audible progress, never the seek target or wall time spent paused.
        internal void Observe(double position, bool playing, long timestamp)
        {
            if (!double.IsFinite(position) || position < 0) { _wasPlaying = false; return; }
            double elapsed = _lastTick == 0 ? 0 : (timestamp - _lastTick) / (double)Stopwatch.Frequency;
            double advance = position - _lastPosition;
            if (_wasPlaying && playing && elapsed > 0 && elapsed < 3 && advance >= 0 && advance <= elapsed + 0.75)
                PlayedSeconds += Math.Min(advance, elapsed);
            _lastPosition = position;
            _lastTick = timestamp;
            _wasPlaying = playing;
        }
    }

    internal enum PlaybackReportOperationKind
    {
        Start,
        Complete
    }

    internal sealed class PlaybackReportOperation
    {
        public PlaybackReportOperation(PlaybackReportOperationKind kind, PlaybackReportContext context, PlaybackEndReason endReason = PlaybackEndReason.Completed, string accountKey = "")
        {
            Kind = kind;
            Context = context;
            EndReason = endReason;
            AccountKey = accountKey;
        }

        public PlaybackReportOperationKind Kind { get; }
        public PlaybackReportContext Context { get; }
        public PlaybackEndReason EndReason { get; }
        public string AccountKey { get; }
    }

    internal sealed class PlaybackReportOperationSnapshot
    {
        public PlaybackReportOperationKind Kind { get; set; }
        public PlaybackEndReason EndReason { get; set; }
        public string AccountKey { get; set; } = string.Empty;
        public PlaybackReportContext? Context { get; set; }

        public static PlaybackReportOperationSnapshot FromOperation(PlaybackReportOperation op)
        {
            return new PlaybackReportOperationSnapshot
            {
                Kind = op.Kind,
                EndReason = op.EndReason,
                AccountKey = op.AccountKey,
                Context = op.Context
            };
        }

        public PlaybackReportOperation? ToOperation()
        {
            if (Context == null)
            {
                return null;
            }

            return new PlaybackReportOperation(Kind, Context, EndReason, AccountKey);
        }
    }

    public sealed class PlaybackReportingService : IDisposable
    {

        private readonly NeteaseApiClient _apiClient;
        private readonly BlockingCollection<PlaybackReportOperation> _queue;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Task _workerTask;
        private bool _disposed;
        private readonly string _retryFilePath;
        private readonly object _persistenceLock = new object();
        private const int MaxPersistedOperations = 50;

        public PlaybackReportingService(NeteaseApiClient apiClient, bool enabled = true)
        {
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            _queue = new BlockingCollection<PlaybackReportOperation>(new ConcurrentQueue<PlaybackReportOperation>(), 200);
            _retryFilePath = Path.Combine(Utils.PathHelper.ApplicationRootDirectory, "Logs", "playback_retry.json");
            IsEnabled = enabled;
            if (enabled) RestorePersistedOperations();
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
            context.CompleteQueued = false;
            context.AccountKey = _apiClient.PlaybackReportingAccountKey;
            Enqueue(new PlaybackReportOperation(PlaybackReportOperationKind.Start, context, accountKey: _apiClient.PlaybackReportingAccountKey));
        }

        public void ReportSongCompleted(PlaybackReportContext context, PlaybackEndReason reason)
        {
            if (context == null)
            {
                return;
            }

            lock (context)
            {
                if (context.CompleteQueued) return;
                context.CompleteQueued = true;
                context.HasCompleted = true;
            }
            Enqueue(new PlaybackReportOperation(PlaybackReportOperationKind.Complete, context, reason, context.AccountKey));
        }

        private void Enqueue(PlaybackReportOperation operation)
        {
            if (_disposed || !IsEnabled || string.IsNullOrEmpty(operation.AccountKey)) return;

            try
            {
                if (!_queue.TryAdd(operation)) PersistFailedOperation(operation);
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
                    if (!IsEnabled || operation.AccountKey != _apiClient.PlaybackReportingAccountKey)
                    {
                        continue;
                    }

                    try
                    {
                        switch (operation.Kind)
                        {
                            case PlaybackReportOperationKind.Start:
                                bool startSuccess = await SendStartLogsAsync(operation.Context, token).ConfigureAwait(false);
                                if (!startSuccess)
                                {
                                    PersistFailedOperation(operation);
                                }
                                break;
                            case PlaybackReportOperationKind.Complete:
                                bool completeSuccess = await SendCompleteLogAsync(operation.Context, operation.EndReason, token).ConfigureAwait(false);
                                if (!completeSuccess)
                                {
                                    PersistFailedOperation(operation);
                                }
                                break;
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        PersistFailedOperation(operation);
                        break;
                    }
                    catch (Exception ex)
                    {
                        PersistFailedOperation(operation);
                        System.Diagnostics.Debug.WriteLine($"[PlaybackReporting] 发送失败: {ex.GetType().Name}");
                    }
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                while (_queue.TryTake(out var pending)) PersistFailedOperation(pending);
            }
        }

        private void RestorePersistedOperations()
        {
            try
            {
                if (!File.Exists(_retryFilePath))
                {
                    return;
                }

                var json = File.ReadAllText(_retryFilePath);
                var list = JsonConvert.DeserializeObject<List<PlaybackReportOperationSnapshot>>(json);
                if (list != null)
                {
                    foreach (var snapshot in list)
                    {
                        var op = snapshot.ToOperation();
                        if (op != null && !string.IsNullOrEmpty(op.AccountKey) && op.AccountKey == _apiClient.PlaybackReportingAccountKey)
                        {
                            _queue.TryAdd(op);
                        }
                    }
                }

                File.Delete(_retryFilePath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PlaybackReporting] 恢复持久化操作失败: {ex.Message}");
            }
        }

        private void PersistFailedOperation(PlaybackReportOperation operation)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_retryFilePath)!);
                List<PlaybackReportOperationSnapshot> list;
                lock (_persistenceLock)
                {
                    list = File.Exists(_retryFilePath)
                        ? JsonConvert.DeserializeObject<List<PlaybackReportOperationSnapshot>>(File.ReadAllText(_retryFilePath)) ?? new List<PlaybackReportOperationSnapshot>()
                        : new List<PlaybackReportOperationSnapshot>();

                    list.Add(PlaybackReportOperationSnapshot.FromOperation(operation));
                    if (list.Count > MaxPersistedOperations)
                    {
                        list = list.Skip(list.Count - MaxPersistedOperations).ToList();
                    }

                    File.WriteAllText(_retryFilePath, JsonConvert.SerializeObject(list, Formatting.Indented));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PlaybackReporting] 持久化失败操作时异常: {ex.Message}");
            }
        }

        private Task<bool> SendStartLogsAsync(PlaybackReportContext context, CancellationToken cancellationToken)
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
                        { "mainsiteWeb", "1" },
                        { "content", content }
                    }
                }
            });

            return SendLogsAsync(logs, context, cancellationToken);
        }

        private async Task<bool> SendCompleteLogAsync(PlaybackReportContext context, PlaybackEndReason reason, CancellationToken cancellationToken)
        {
            int reportedSeconds = double.IsFinite(context.PlayedSeconds)
                ? (int)Math.Clamp(Math.Floor(context.PlayedSeconds), 0, int.MaxValue) : 0;
            // Server decides eligibility. Do not invent a half-song/240-second rule,
            // and do not substitute total duration when the listener skipped immediately.
            if (reportedSeconds == 0) return true;

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
                        { "mainsiteWeb", "1" },
                        { "content", BuildContent(context) }
                    }
                }
            };

            var logs = new List<Dictionary<string, object>> { log };

            return await SendLogsAsync(logs, context, cancellationToken).ConfigureAwait(false);
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

        private async Task<bool> SendLogsAsync(IEnumerable<Dictionary<string, object>> logs, PlaybackReportContext context, CancellationToken cancellationToken)
        {
            try
            {
                return await _apiClient.SendPlaybackLogsEapiAsync(logs, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PlaybackReporting] weblog 发送失败: {ex.Message} | song={context?.SongId}");
                return false;
            }
        }

        public async Task DrainAsync(TimeSpan budget)
        {
            if (_disposed) return;
            _queue.CompleteAdding();
            _cts.CancelAfter(budget);
            // Cancellation persists the unfinished operation and drains queued work to
            // disk before normal application shutdown disposes the API client.
            await _workerTask.WaitAsync(budget + TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _queue.CompleteAdding();
            _cts.CancelAfter(TimeSpan.FromSeconds(2));

            if (_workerTask.IsCompleted)
            {
                _cts.Dispose();
                _queue.Dispose();
                return;
            }

            _ = _workerTask.ContinueWith(_ =>
            {
                try
                {
                    _cts.Dispose();
                    _queue.Dispose();
                }
                catch (ObjectDisposedException)
                {
                }
            }, TaskScheduler.Default);
        }
    }
}
