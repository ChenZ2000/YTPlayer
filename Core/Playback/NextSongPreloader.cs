using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using YTPlayer.Core.Playback.Cache;
using YTPlayer.Core.Download;
using YTPlayer.Core.Streaming;
using YTPlayer.Models;
using YTPlayer.Utils;

namespace YTPlayer.Core.Playback
{
    /// <summary>Owns each preload until it is transferred once to the playback engine.</summary>
    public sealed class NextSongPreloader : IDisposable
    {
        private readonly object _lock = new();
        private readonly Func<SongInfo, QualityLevel, CancellationToken, Task<SongResolveResult>> _resolvePlaybackAsync;
        private readonly HttpClient _httpClient;
        private readonly Dictionary<string, PreloadedData> _preloadedData = new(StringComparer.Ordinal);
        private CancellationTokenSource? _preloadCts;
        private bool _disposed;

        public NextSongPreloader(NeteaseApiClient apiClient, Func<SongInfo, QualityLevel, CancellationToken, Task<SongResolveResult>> resolvePlaybackAsync)
        {
            ArgumentNullException.ThrowIfNull(apiClient);
            _resolvePlaybackAsync = resolvePlaybackAsync ?? throw new ArgumentNullException(nameof(resolvePlaybackAsync));
            _httpClient = OptimizedHttpClientFactory.CreateForMainPlayback(TimeSpan.FromSeconds(60));
        }

        private static bool PreferSequentialFull(SongInfo song) =>
            (song.Duration > 0 && song.Size * 8.0 / song.Duration / 1000.0 >= 512) || song.Size >= 12 * 1024 * 1024;

        public async Task<bool> StartPreloadAsync(SongInfo nextSong, QualityLevel quality)
        {
            if (nextSong == null || string.IsNullOrWhiteSpace(nextSong.Id)) return false;
            CancellationTokenSource owner;
            lock (_lock)
            {
                if (_disposed) return false;
                _preloadCts?.Cancel(); // The running operation alone disposes its source.
                _preloadCts = owner = new CancellationTokenSource();
                DownloadBandwidthCoordinator.Instance.NotifyPrecacheStateChanged(true);
            }
            var token = owner.Token;
            PreloadedData? prepared = null;
            bool published = false;
            try
            {
                var result = await _resolvePlaybackAsync(nextSong, quality, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (result.Status != SongResolveStatus.Success || string.IsNullOrEmpty(nextSong.Url))
                {
                    DebugLogger.Log(DebugLogger.LogLevel.Info, "Preload", $"Resolve stopped: song={nextSong.Id}, status={result.Status}");
                    return false;
                }
                prepared = new PreloadedData
                {
                    Url = nextSong.Url, Level = nextSong.Level, Size = nextSong.Size,
                    IsTrial = nextSong.IsTrial, TrialStart = nextSong.TrialStart, TrialEnd = nextSong.TrialEnd,
                    IsUnblocked = nextSong.IsUnblocked, UnblockSource = nextSong.UnblockSource ?? string.Empty,
                    CustomHeaders = nextSong.CustomHeaders != null ? new(nextSong.CustomHeaders, StringComparer.OrdinalIgnoreCase) : null,
                    CacheManager = new SmartCacheManager(nextSong.Id, nextSong.Url, nextSong.Size, _httpClient,
                        PreferSequentialFull(nextSong), nextSong.CustomHeaders)
                };
                if (!await prepared.CacheManager.InitializeAsync(token, isPreload: true).ConfigureAwait(false)) return false;
                token.ThrowIfCancellationRequested();
                prepared.StreamProvider = new BassStreamProvider(prepared.CacheManager);
                using (token.Register(prepared.StreamProvider.AbortPendingReads))
                    prepared.StreamHandle = await Task.Run(() => prepared.StreamProvider.CreateStream(), token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (prepared.StreamHandle == 0) return false;
                prepared.IsReady = true;
                PreloadedData? replaced;
                lock (_lock)
                {
                    // Clear, Dispose or a newer candidate must invalidate publication atomically.
                    if (_disposed || token.IsCancellationRequested || !ReferenceEquals(_preloadCts, owner)) return false;
                    _preloadedData.Remove(nextSong.Id, out replaced);
                    _preloadedData[nextSong.Id] = prepared;
                    published = true;
                }
                if (replaced != null) ReleaseInBackground(new[] { replaced });
                DebugLogger.Log(DebugLogger.LogLevel.Info, "Preload", $"Ready: song={nextSong.Id}");
                return true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
            catch (Exception ex)
            {
                DebugLogger.Log(DebugLogger.LogLevel.Warning, "Preload", $"Failed: song={nextSong.Id}, error={ex.GetType().Name}");
                return false;
            }
            finally
            {
                if (!published && prepared != null) await Task.Run(prepared.Dispose).ConfigureAwait(false);
                lock (_lock)
                {
                    if (ReferenceEquals(_preloadCts, owner))
                    {
                        _preloadCts = null;
                        DownloadBandwidthCoordinator.Instance.NotifyPrecacheStateChanged(false);
                    }
                    owner.Dispose();
                }
            }
        }

        public PreloadedData? TryGetPreloadedData(string songId)
        {
            if (string.IsNullOrWhiteSpace(songId)) return null;
            lock (_lock) return _preloadedData.Remove(songId, out var data) ? data : null;
        }

        public void Clear() => ClearCore(disposing: false);

        public void Dispose()
        {
            ClearCore(disposing: true);
            _httpClient.Dispose();
        }

        private void ClearCore(bool disposing)
        {
            PreloadedData[] snapshot;
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = disposing;
                _preloadCts?.Cancel();
                _preloadCts = null;
                snapshot = _preloadedData.Values.ToArray();
                _preloadedData.Clear();
                DownloadBandwidthCoordinator.Instance.NotifyPrecacheStateChanged(false);
            }
            ReleaseInBackground(snapshot);
        }

        public void CleanupStaleData(string currentSongId, string nextSongId)
        {
            var stale = new List<PreloadedData>();
            lock (_lock)
            {
                foreach (string id in _preloadedData.Keys.Where(id => id != currentSongId && id != nextSongId).ToArray())
                    if (_preloadedData.Remove(id, out var data)) stale.Add(data);
            }
            ReleaseInBackground(stale);
        }

        private static void ReleaseInBackground(IEnumerable<PreloadedData> entries)
        {
            _ = Task.Run(() => { foreach (var entry in entries) entry.Dispose(); });
        }
    }

    /// <summary>
    /// 预加载数据（返回给调用者）
    /// </summary>
    public class PreloadedData : IDisposable
    {
        [DllImport("bass.dll")]
        private static extern bool BASS_StreamFree(int handle);
        private int _disposed;

        // Call only while this object owns the resources, before handing them to the engine.
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                StreamProvider?.AbortPendingReads();
                if (StreamHandle != 0) BASS_StreamFree(StreamHandle);
            }
            catch (Exception ex)
            {
                DebugLogger.Log(DebugLogger.LogLevel.Warning, "Preload", $"Native release failed: {ex.GetType().Name}");
            }
            finally
            {
                StreamProvider?.Dispose();
                CacheManager?.Dispose();
                StreamHandle = 0;
            }
        }

        public string Url { get; set; } = string.Empty;
        public string Level { get; set; } = string.Empty;
        public long Size { get; set; }
        public bool IsTrial { get; set; }
        public long TrialStart { get; set; }
        public long TrialEnd { get; set; }
        public bool IsUnblocked { get; set; }
        public string UnblockSource { get; set; } = string.Empty;
        public Dictionary<string, string>? CustomHeaders { get; set; }
        public SmartCacheManager CacheManager { get; set; } = null!;

        // ⭐ 新增：完整的流对象信息
        public BassStreamProvider StreamProvider { get; set; } = null!;
        public int StreamHandle { get; set; }
        public bool IsReady { get; set; }

        // ⭐ 新增：歌词数据
        public YTPlayer.Core.Lyrics.LyricsData LyricsData { get; set; } = null!;
    }
}
