using System;
using System.Collections.Generic;
using System.Linq;
using YTPlayer.Models;

#pragma warning disable CS8600, CS8601, CS8602, CS8603, CS8604, CS8625

namespace YTPlayer.Core.Playback
{
    /// <summary>
    /// 播放流向（原队列/插播/返回原队列等）
    /// </summary>
    public enum PlaybackRoute
    {
        None,
        Queue,
        Injection,
        PendingInjection,
        ReturnToQueue
    }

    /// <summary>
    /// 手动播放选择的结果
    /// </summary>
    public class PlaybackSelectionResult
    {
        private PlaybackSelectionResult(PlaybackRoute route, SongInfo song, int queueIndex, int injectionIndex, bool queueChanged, bool clearedInjection)
        {
            Route = route;
            Song = song;
            QueueIndex = queueIndex;
            InjectionIndex = injectionIndex;
            QueueChanged = queueChanged;
            ClearedInjection = clearedInjection;
        }

        public PlaybackRoute Route { get; }

        public SongInfo Song { get; }

        public int QueueIndex { get; }

        public int InjectionIndex { get; }

        public bool QueueChanged { get; }

        public bool ClearedInjection { get; }

        public static PlaybackSelectionResult ForQueue(SongInfo song, int queueIndex, bool queueChanged, bool clearedInjection)
        {
            return new PlaybackSelectionResult(PlaybackRoute.Queue, song, queueIndex, -1, queueChanged, clearedInjection);
        }

        public static PlaybackSelectionResult ForInjection(SongInfo song, int injectionIndex)
        {
            return new PlaybackSelectionResult(PlaybackRoute.Injection, song, -1, injectionIndex, false, false);
        }

        public static PlaybackSelectionResult None { get; } =
            new PlaybackSelectionResult(PlaybackRoute.None, null, -1, -1, false, false);
    }

    /// <summary>
    /// 播放跳转的结果（上一首/下一首）
    /// </summary>
    public class PlaybackMoveResult
    {
        private PlaybackMoveResult(
            PlaybackRoute route,
            SongInfo song,
            int queueIndex,
            int injectionIndex,
            bool wrapped,
            bool reachedBoundary,
            bool queueEmpty,
            bool clearedInjection)
        {
            Route = route;
            Song = song;
            QueueIndex = queueIndex;
            InjectionIndex = injectionIndex;
            Wrapped = wrapped;
            ReachedBoundary = reachedBoundary;
            QueueEmpty = queueEmpty;
            ClearedInjection = clearedInjection;
        }

        public PlaybackRoute Route { get; }

        public SongInfo Song { get; }

        public int QueueIndex { get; }

        public int InjectionIndex { get; }

        public bool Wrapped { get; }

        public bool ReachedBoundary { get; }

        public bool QueueEmpty { get; }

        public bool ClearedInjection { get; }

        public bool HasSong => Song != null;

        public static PlaybackMoveResult ForQueue(SongInfo song, int queueIndex, bool wrapped, bool clearedInjection = false)
        {
            return new PlaybackMoveResult(PlaybackRoute.Queue, song, queueIndex, -1, wrapped, false, false, clearedInjection);
        }

        public static PlaybackMoveResult ForReturnToQueue(SongInfo song, int queueIndex)
        {
            return new PlaybackMoveResult(PlaybackRoute.ReturnToQueue, song, queueIndex, -1, false, false, false, true);
        }

        public static PlaybackMoveResult ForInjection(SongInfo song, int injectionIndex, PlaybackRoute route)
        {
            return new PlaybackMoveResult(route, song, -1, injectionIndex, false, false, false, false);
        }

        public static PlaybackMoveResult Boundary(bool queueEmpty = false)
        {
            return new PlaybackMoveResult(PlaybackRoute.None, null, -1, -1, false, true, queueEmpty, false);
        }

        public static PlaybackMoveResult None { get; } =
            new PlaybackMoveResult(PlaybackRoute.None, null, -1, -1, false, false, false, false);
    }

    /// <summary>
    /// 播放队列状态快照
    /// </summary>
    public class PlaybackSnapshot
    {
        public PlaybackSnapshot(
            IReadOnlyList<SongInfo> queue,
            int queueIndex,
            string queueSource,
            IReadOnlyList<SongInfo> injectionChain,
            int injectionIndex,
            IReadOnlyDictionary<string, string> injectionSources,
            SongInfo pendingInjection)
        {
            Queue = queue;
            QueueIndex = queueIndex;
            QueueSource = queueSource;
            InjectionChain = injectionChain;
            InjectionIndex = injectionIndex;
            InjectionSources = injectionSources;
            PendingInjection = pendingInjection;
        }

        public IReadOnlyList<SongInfo> Queue { get; }

        public int QueueIndex { get; }

        public string QueueSource { get; }

        public IReadOnlyList<SongInfo> InjectionChain { get; }

        public int InjectionIndex { get; }

        public IReadOnlyDictionary<string, string> InjectionSources { get; }

        public SongInfo PendingInjection { get; }

        public bool HasQueue => Queue != null && Queue.Count > 0;

        public bool IsInInjection => InjectionIndex >= 0 && InjectionChain != null && InjectionIndex < InjectionChain.Count;
    }

    /// <summary>
    /// 播放队列管理器，统一管理原队列、插播链表和候选插播。
    /// </summary>
    public sealed class PlaybackQueueManager
    {
        private readonly object _syncRoot = new object();
        private readonly List<SongInfo> _queue = new List<SongInfo>();
        private int _queueIndex = -1;
        private string _queueSource = string.Empty;

        private readonly List<SongInfo> _injectionChain = new List<SongInfo>();
        private int _injectionIndex = -1;
        private readonly Dictionary<string, string> _injectionSources = new Dictionary<string, string>();

        private PendingInjectionInfo? _pendingInjection;
        private readonly Random _random = new Random();
        private const int RandomTimelineLimit = 8192;
        private readonly List<int> _randomTimeline = new List<int>();
        private int _randomTimelineCursor = -1;
        private readonly List<int> _randomCycleRemaining = new List<int>();

        public PlaybackSelectionResult ManualSelect(SongInfo song, IReadOnlyList<SongInfo> viewSongs, string viewSource)
        {
            if (song == null)
            {
                return PlaybackSelectionResult.None;
            }

            lock (_syncRoot)
            {
                int indexInView = FindSongIndex(viewSongs, song.Id);
                int indexInQueue = FindSongIndex(_queue, song.Id);

                if (indexInView >= 0)
                {
                    RebuildQueueFromView(viewSongs, viewSource, indexInView);
                    return PlaybackSelectionResult.ForQueue(song, _queueIndex, true, true);
                }

                if (indexInQueue >= 0)
                {
                    _queueIndex = indexInQueue;
                    bool cleared = ClearInjectionInternal();
                    SyncRandomHistoryWithQueueIndex();
                    return PlaybackSelectionResult.ForQueue(song, _queueIndex, false, cleared);
                }

                int injectionIndex = AppendInjection(song, viewSource);
                return PlaybackSelectionResult.ForInjection(song, injectionIndex);
            }
        }

        public bool TrySyncQueueWithView(IReadOnlyList<SongInfo> viewSongs, string viewSource, int anchorIndex)
        {
            if (viewSongs == null || viewSongs.Count == 0 || anchorIndex < 0 || anchorIndex >= viewSongs.Count)
            {
                return false;
            }

            lock (_syncRoot)
            {
                RebuildQueueFromView(viewSongs, viewSource, anchorIndex);
                return _queue.Count > 0;
            }
        }

        public bool TryAppendQueueSongs(IReadOnlyList<SongInfo> songs, string expectedQueueSource)
        {
            if (songs == null || songs.Count == 0 || string.IsNullOrWhiteSpace(expectedQueueSource))
            {
                return false;
            }

            lock (_syncRoot)
            {
                if (!string.Equals(_queueSource, expectedQueueSource, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                HashSet<string> existingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (SongInfo item in _queue)
                {
                    string itemKey = BuildQueueAppendKey(item);
                    if (!string.IsNullOrWhiteSpace(itemKey))
                    {
                        existingKeys.Add(itemKey);
                    }
                }

                bool appended = false;
                foreach (SongInfo song in songs)
                {
                    if (song == null)
                    {
                        continue;
                    }

                    string songKey = BuildQueueAppendKey(song);
                    if (!string.IsNullOrWhiteSpace(songKey) && existingKeys.Contains(songKey))
                    {
                        continue;
                    }

                    AssignSongViewSource(song, expectedQueueSource);
                    _queue.Add(song);
                    appended = true;

                    if (!string.IsNullOrWhiteSpace(songKey))
                    {
                        existingKeys.Add(songKey);
                    }
                }

                if (appended && _queueIndex < 0 && _queue.Count > 0)
                {
                    _queueIndex = 0;
                    SyncRandomHistoryWithQueueIndex();
                }

                return appended;
            }
        }

        private static string BuildQueueAppendKey(SongInfo? song)
        {
            if (song == null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(song.Id))
            {
                return "id:" + song.Id.Trim();
            }

            string name = (song.Name ?? string.Empty).Trim();
            string artist = (song.Artist ?? string.Empty).Trim();
            string album = (song.Album ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name) &&
                string.IsNullOrWhiteSpace(artist) &&
                string.IsNullOrWhiteSpace(album))
            {
                return string.Empty;
            }

            return $"meta:{name}|{artist}|{album}";
        }

        public void SetPendingInjection(SongInfo song, string source)
        {
            lock (_syncRoot)
            {
                if (song == null)
                {
                    _pendingInjection = null;
                }
                else
                {
                    _pendingInjection = new PendingInjectionInfo(song, source);
                }
            }
        }

        public bool HasPendingInjection
        {
            get
            {
                lock (_syncRoot)
                {
                    return _pendingInjection != null && _pendingInjection.Song != null;
                }
            }
        }

        public PlaybackMoveResult MoveNext(PlayMode playMode, bool isManual, string currentViewSource)
        {
            lock (_syncRoot)
            {
                var pending = ConsumePendingInjection(currentViewSource);
                if (pending.HasSong)
                {
                    return pending;
                }

                if (_injectionIndex >= 0)
                {
                    int nextIndex = _injectionIndex + 1;
                    if (nextIndex < _injectionChain.Count)
                    {
                        _injectionIndex = nextIndex;
                        var nextSong = _injectionChain[nextIndex];
                        return PlaybackMoveResult.ForInjection(nextSong, nextIndex, PlaybackRoute.Injection);
                    }

                    // 插播链表结束 → 清除并尝试原队列
                    ClearInjectionInternal();
                }

                if (_queue.Count == 0)
                {
                    return PlaybackMoveResult.Boundary(queueEmpty: true);
                }

                if (_queueIndex < 0)
                {
                    _queueIndex = 0;
                    SyncRandomHistoryWithQueueIndex();
                    var currentSong = _queue[_queueIndex];
                    return PlaybackMoveResult.ForQueue(currentSong, _queueIndex, false);
                }

                bool wrapped;
                bool reachedBoundary;
                int? candidate = GetNextQueueIndex(playMode, isManual, out wrapped, out reachedBoundary);
                if (!candidate.HasValue)
                {
                    return reachedBoundary
                        ? PlaybackMoveResult.Boundary()
                        : PlaybackMoveResult.None;
                }

                _queueIndex = candidate.Value;
                SyncRandomHistoryWithQueueIndex();
                var queueSong = _queue[_queueIndex];
                return PlaybackMoveResult.ForQueue(queueSong, _queueIndex, wrapped);
            }
        }

        public PlaybackMoveResult MovePrevious(PlayMode playMode, bool isManual, string currentViewSource)
        {
            lock (_syncRoot)
            {
                if (_injectionIndex >= 0)
                {
                    int prevIndex = _injectionIndex - 1;
                    if (prevIndex >= 0)
                    {
                        _injectionIndex = prevIndex;
                        var injectionSong = _injectionChain[prevIndex];
                        return PlaybackMoveResult.ForInjection(injectionSong, prevIndex, PlaybackRoute.Injection);
                    }

                    // 回到原队列位置
                    bool cleared = ClearInjectionInternal();
                    if (_queue.Count > 0 && _queueIndex >= 0 && _queueIndex < _queue.Count)
                    {
                        SyncRandomHistoryWithQueueIndex();
                        var currentQueueSong = _queue[_queueIndex];
                        return PlaybackMoveResult.ForReturnToQueue(currentQueueSong, _queueIndex);
                    }

                    return PlaybackMoveResult.Boundary(queueEmpty: _queue.Count == 0);
                }

                if (_queue.Count == 0)
                {
                    return PlaybackMoveResult.Boundary(queueEmpty: true);
                }

                if (_queueIndex < 0)
                {
                    _queueIndex = 0;
                    SyncRandomHistoryWithQueueIndex();
                }

                if (playMode == PlayMode.Random)
                {
                    if (!TryGetRandomPreviousQueueIndex(allowExpand: isManual, out int randomPreviousIndex))
                    {
                        return isManual
                            ? PlaybackMoveResult.Boundary()
                            : PlaybackMoveResult.None;
                    }

                    _queueIndex = randomPreviousIndex;
                    SyncRandomHistoryWithQueueIndex();
                    var randomPreviousSong = _queue[_queueIndex];
                    return PlaybackMoveResult.ForQueue(randomPreviousSong, _queueIndex, wrapped: false);
                }

                int prevCandidate = _queueIndex - 1;
                bool wrapped = false;

                if (prevCandidate < 0)
                {
                    if (isManual)
                    {
                        return PlaybackMoveResult.Boundary();
                    }

                    prevCandidate = Math.Max(_queue.Count - 1, 0);
                    wrapped = true;
                }

                _queueIndex = prevCandidate;
                SyncRandomHistoryWithQueueIndex();
                var previousQueueSong = _queue[_queueIndex];
                return PlaybackMoveResult.ForQueue(previousQueueSong, _queueIndex, wrapped);
            }
        }

        public SongInfo PredictNext(PlayMode playMode)
        {
            lock (_syncRoot)
            {
                if (_pendingInjection != null && _pendingInjection.Song != null)
                {
                    return _pendingInjection.Song;
                }

                if (_injectionIndex >= 0 && _injectionIndex + 1 < _injectionChain.Count)
                {
                    return _injectionChain[_injectionIndex + 1];
                }

                return PredictFromQueue(playMode);
            }
        }

        public SongInfo PredictFromQueue(PlayMode playMode)
        {
            if (_queue.Count == 0)
                return null;

            int nextIndex = -1;

            switch (playMode)
            {
                case PlayMode.Sequential:
                    nextIndex = _queueIndex + 1;
                    if (nextIndex >= _queue.Count)
                        return null;
                    break;

                case PlayMode.Loop:
                case PlayMode.LoopOne:
                    nextIndex = _queueIndex + 1;
                    if (nextIndex >= _queue.Count)
                        nextIndex = 0;
                    break;

                case PlayMode.Random:
                    if (_queue.Count > 1)
                    {
                        int candidate = _random.Next(_queue.Count);
                        if (candidate == _queueIndex)
                        {
                            candidate = (candidate + 1) % _queue.Count;
                        }
                        nextIndex = candidate;
                    }
                    else
                    {
                        nextIndex = 0;
                    }
                    break;

                default:
                    nextIndex = _queueIndex + 1;
                    if (nextIndex >= _queue.Count)
                        nextIndex = 0;
                    break;
            }

            if (nextIndex >= 0 && nextIndex < _queue.Count)
            {
                return _queue[nextIndex];
            }

            return null;
        }

        /// <summary>
        /// 预测下一首可用的歌曲（跳过不可用的歌曲）
        /// </summary>
        /// <param name="playMode">播放模式</param>
        /// <param name="maxAttempts">最大尝试次数（防止无限循环）</param>
        /// <returns>下一首可用的歌曲，如果没有则返回 null</returns>
        public SongInfo PredictNextAvailable(PlayMode playMode, int maxAttempts = 10)
        {
            lock (_syncRoot)
            {
                // 首先检查 pending injection 和 injection chain
                if (_pendingInjection != null && _pendingInjection.Song != null)
                {
                    // Pending injection 总是返回，不检查可用性（因为这是手动触发的）
                    return _pendingInjection.Song;
                }

                if (_injectionIndex >= 0 && _injectionIndex + 1 < _injectionChain.Count)
                {
                    // Injection chain 中的歌曲也直接返回
                    return _injectionChain[_injectionIndex + 1];
                }

                // 从队列中查找可用歌曲
                if (_queue.Count == 0)
                    return null;

                // 对于随机模式，不进行多次尝试（因为每次预测的结果可能不同）
                if (playMode == PlayMode.Random)
                {
                    return PredictFromQueue(playMode);
                }

                int startIndex = _queueIndex;
                int attempts = 0;
                int checkedIndex = startIndex;

                while (attempts < maxAttempts && attempts < _queue.Count)
                {
                    attempts++;

                    // 计算下一个索引
                    int nextIndex = -1;
                    switch (playMode)
                    {
                        case PlayMode.Sequential:
                            nextIndex = checkedIndex + 1;
                            if (nextIndex >= _queue.Count)
                                return null; // 顺序播放到末尾，没有下一首
                            break;

                        case PlayMode.Loop:
                        case PlayMode.LoopOne:
                            nextIndex = checkedIndex + 1;
                            if (nextIndex >= _queue.Count)
                                nextIndex = 0; // 循环回到开头
                            break;

                        default:
                            nextIndex = checkedIndex + 1;
                            if (nextIndex >= _queue.Count)
                                nextIndex = 0;
                            break;
                    }

                    // 防止无限循环（已经回到起点）
                    if (nextIndex == startIndex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[PlaybackQueue] PredictNextAvailable 已循环回到起点，停止查找");
                        break;
                    }

                    if (nextIndex >= 0 && nextIndex < _queue.Count)
                    {
                        var song = _queue[nextIndex];

                        // 检查歌曲是否可用
                        // IsAvailable == null 表示未检查，认为可能可用
                        // IsAvailable == true 表示可用
                        // IsAvailable == false means previously marked unavailable.
                        // Do not hard-skip here; unified resolve may recover via unblock.
                        if (song.IsAvailable == false)
                        {
                            System.Diagnostics.Debug.WriteLine($"[PlaybackQueue] Candidate is marked unavailable, but keep it for unified resolve: {song.Name} (index {nextIndex})");
                        }

                        System.Diagnostics.Debug.WriteLine($"[PlaybackQueue] Predict candidate: {song.Name} (index {nextIndex}, attempt {attempts}, IsAvailable={song.IsAvailable})");
                        return song;
                    }

                    break; // 超出范围，停止
                }

                System.Diagnostics.Debug.WriteLine($"[PlaybackQueue] PredictNextAvailable 未找到可用歌曲（尝试了 {attempts} 次）");
                return null;
            }
        }

        public PlaybackMoveResult AdvanceForPlayback(SongInfo actualSong, PlayMode playMode, string currentViewSource)
        {
            if (actualSong == null)
            {
                return PlaybackMoveResult.None;
            }

            _ = playMode;

            lock (_syncRoot)
            {
                if (_pendingInjection != null && _pendingInjection.Song != null &&
                    _pendingInjection.Song.Id == actualSong.Id)
                {
                    int injectionIndex = AppendInjection(actualSong, currentViewSource);
                    _pendingInjection = null;
                    return PlaybackMoveResult.ForInjection(actualSong, injectionIndex, PlaybackRoute.PendingInjection);
                }

                if (_injectionIndex >= 0)
                {
                    int nextIndex = _injectionIndex + 1;
                    if (nextIndex < _injectionChain.Count && _injectionChain[nextIndex]?.Id == actualSong.Id)
                    {
                        _injectionIndex = nextIndex;
                        UpdateInjectionSource(actualSong, currentViewSource);
                        return PlaybackMoveResult.ForInjection(actualSong, nextIndex, PlaybackRoute.Injection);
                    }

                    if (_injectionIndex < _injectionChain.Count && _injectionChain[_injectionIndex]?.Id == actualSong.Id)
                    {
                        UpdateInjectionSource(actualSong, currentViewSource);
                        return PlaybackMoveResult.ForInjection(actualSong, _injectionIndex, PlaybackRoute.Injection);
                    }

                    ClearInjectionInternal();
                }

                int indexInQueue = FindSongIndex(_queue, actualSong.Id);
                if (indexInQueue >= 0)
                {
                    _queueIndex = indexInQueue;
                    SyncRandomHistoryWithQueueIndex();
                    return PlaybackMoveResult.ForQueue(actualSong, _queueIndex, false);
                }

                int appendedIndex = AppendInjection(actualSong, currentViewSource);
                return PlaybackMoveResult.ForInjection(actualSong, appendedIndex, PlaybackRoute.Injection);
            }
        }

        public PlaybackSnapshot CaptureSnapshot()
        {
            lock (_syncRoot)
            {
                return new PlaybackSnapshot(
                    _queue.ToList(),
                    _queueIndex,
                    _queueSource,
                    _injectionChain.ToList(),
                    _injectionIndex,
                    new Dictionary<string, string>(_injectionSources),
                    _pendingInjection?.Song);
            }
        }

        public string QueueSource
        {
            get
            {
                lock (_syncRoot)
                {
                    return _queueSource;
                }
            }
        }

        public int QueueIndex
        {
            get
            {
                lock (_syncRoot)
                {
                    return _queueIndex;
                }
            }
        }

        public bool TryGetInjectionSource(string songId, out string source)
        {
            lock (_syncRoot)
            {
                if (songId != null && _injectionSources.TryGetValue(songId, out source))
                {
                    return true;
                }

                source = null;
                return false;
            }
        }

        public bool IsInInjection
        {
            get
            {
                lock (_syncRoot)
                {
                    return _injectionIndex >= 0 && _injectionIndex < _injectionChain.Count;
                }
            }
        }

        public IReadOnlyList<SongInfo> CurrentQueue
        {
            get
            {
                lock (_syncRoot)
                {
                    return _queue.ToList();
                }
            }
        }

        public IReadOnlyList<SongInfo> InjectionChain
        {
            get
            {
                lock (_syncRoot)
                {
                    return _injectionChain.ToList();
                }
            }
        }

        /// <summary>
        /// 移除所有匹配指定歌曲 ID 的队列/插播项，用于处理无法播放的歌曲。
        /// </summary>
        public bool RemoveSongById(string songId)
        {
            if (string.IsNullOrWhiteSpace(songId))
            {
                return false;
            }

            lock (_syncRoot)
            {
                bool removed = false;
                bool removedFromQueue = false;

                if (_pendingInjection?.Song?.Id == songId)
                {
                    _pendingInjection = null;
                    removed = true;
                }

                bool removedFromInjection = false;
                for (int i = _injectionChain.Count - 1; i >= 0; i--)
                {
                    if (_injectionChain[i]?.Id == songId)
                    {
                        _injectionChain.RemoveAt(i);
                        removedFromInjection = true;
                    }
                }

                if (removedFromInjection)
                {
                    _injectionSources.Remove(songId);
                    ClearInjectionInternal();
                    removed = true;
                }
                else
                {
                    // 确保插播来源表不会无限增长
                    _injectionSources.Remove(songId);
                }

                for (int i = _queue.Count - 1; i >= 0; i--)
                {
                    if (_queue[i]?.Id == songId)
                    {
                        _queue.RemoveAt(i);
                        removed = true;
                        removedFromQueue = true;

                        if (_queueIndex >= i)
                        {
                            _queueIndex--;
                        }
                    }
                }

                if (_queue.Count == 0)
                {
                    _queueIndex = -1;
                    _queueSource = string.Empty;
                }
                else
                {
                    if (_queueIndex < 0)
                    {
                        _queueIndex = 0;
                    }
                    else if (_queueIndex >= _queue.Count)
                    {
                        _queueIndex = _queue.Count - 1;
                    }
                }

                if (removedFromQueue)
                {
                    ResetRandomNavigationState();
                    SyncRandomHistoryWithQueueIndex();
                }

                return removed;
            }
        }

        public bool TryUpdateSongDetail(SongInfo updated)
        {
            if (updated == null || string.IsNullOrWhiteSpace(updated.Id))
            {
                return false;
            }
            bool changed = false;
            lock (_syncRoot)
            {
                changed |= ReplaceSongIfMatch(_queue, updated);
                changed |= ReplaceSongIfMatch(_injectionChain, updated);
                if (_pendingInjection?.Song != null && _pendingInjection.Song.Id == updated.Id)
                {
                    _pendingInjection = new PendingInjectionInfo(updated, _pendingInjection.Source);
                    changed = true;
                }
            }
            return changed;
        }


        public bool TryReplaceQueueSongAt(int queueIndex, SongInfo updated, string? expectedQueueSource = null)
        {
            if (updated == null || queueIndex < 0)
            {
                return false;
            }

            lock (_syncRoot)
            {
                if (queueIndex < 0 || queueIndex >= _queue.Count)
                {
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(expectedQueueSource) &&
                    !string.Equals(_queueSource, expectedQueueSource, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                string effectiveSource = string.IsNullOrWhiteSpace(updated.ViewSource)
                    ? _queueSource
                    : updated.ViewSource;
                AssignSongViewSource(updated, effectiveSource);
                _queue[queueIndex] = updated;

                if (!string.IsNullOrWhiteSpace(updated.Id))
                {
                    ReplaceSongIfMatch(_injectionChain, updated);
                    if (_pendingInjection?.Song != null &&
                        string.Equals(_pendingInjection.Song.Id, updated.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        _pendingInjection = new PendingInjectionInfo(updated, _pendingInjection.Source);
                    }
                }

                return true;
            }
        }

        private static bool ReplaceSongIfMatch(List<SongInfo> list, SongInfo updated)
        {
            if (list == null || list.Count == 0)
            {
                return false;
            }
            bool changed = false;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i]?.Id == updated.Id)
                {
                    list[i] = updated;
                    changed = true;
                }
            }
            return changed;
        }

        public SongInfo PendingInjection
        {
            get
            {
                lock (_syncRoot)
                {
                    return _pendingInjection?.Song;
                }
            }
        }

        private PlaybackMoveResult ConsumePendingInjection(string currentViewSource)
        {
            if (_pendingInjection == null || _pendingInjection.Song == null)
            {
                return PlaybackMoveResult.None;
            }

            SongInfo song = _pendingInjection.Song;
            string pendingSource = _pendingInjection.Source;
            _pendingInjection = null;

            string effectiveSource = string.IsNullOrWhiteSpace(pendingSource) ? currentViewSource : pendingSource;
            int injectionIndex = AppendInjection(song, effectiveSource);
            return PlaybackMoveResult.ForInjection(song, injectionIndex, PlaybackRoute.PendingInjection);
        }

        private void RebuildQueueFromView(IReadOnlyList<SongInfo> viewSongs, string viewSource, int selectedIndex)
        {
            _queue.Clear();

            if (viewSongs != null)
            {
                foreach (var item in viewSongs)
                {
                    if (item != null)
                    {
                        AssignSongViewSource(item, viewSource);
                        _queue.Add(item);
                    }
                }
            }

            if (_queue.Count == 0 && viewSongs != null && viewSongs.Count > 0)
            {
                // 防御：至少把选中歌曲加入队列
                var selected = viewSongs[selectedIndex];
                AssignSongViewSource(selected, viewSource);
                _queue.Add(selected);
            }

            _queueSource = viewSource ?? string.Empty;
            _queueIndex = Math.Max(0, Math.Min(selectedIndex, _queue.Count - 1));
            ResetRandomNavigationState();
            SyncRandomHistoryWithQueueIndex();
            ClearInjectionInternal();
        }

        private int AppendInjection(SongInfo song, string viewSource)
        {
            if (song == null)
            {
                return -1;
            }

            if (_injectionIndex >= 0)
            {
                TrimInjectionTail();
            }
            else
            {
                _injectionChain.Clear();
                _injectionSources.Clear();
            }

            AssignSongViewSource(song, viewSource);
            _injectionChain.Add(song);
            _injectionIndex = _injectionChain.Count - 1;
            UpdateInjectionSource(song, viewSource);
            return _injectionIndex;
        }

        private void TrimInjectionTail()
        {
            if (_injectionIndex + 1 >= _injectionChain.Count)
            {
                return;
            }

            for (int i = _injectionIndex + 1; i < _injectionChain.Count; i++)
            {
                var removed = _injectionChain[i];
                if (removed != null && !string.IsNullOrEmpty(removed.Id))
                {
                    _injectionSources.Remove(removed.Id);
                }
            }

            _injectionChain.RemoveRange(_injectionIndex + 1, _injectionChain.Count - _injectionIndex - 1);
        }

        private bool ClearInjectionInternal()
        {
            if (_injectionChain.Count == 0 && _injectionIndex < 0 && _injectionSources.Count == 0)
            {
                return false;
            }

            _injectionChain.Clear();
            _injectionIndex = -1;
            _injectionSources.Clear();
            return true;
        }

        private int? GetNextQueueIndex(PlayMode playMode, bool isManual, out bool wrapped, out bool reachedBoundary)
        {
            wrapped = false;
            reachedBoundary = false;

            if (_queue.Count == 0)
            {
                reachedBoundary = true;
                return null;
            }

            int nextIndex;

            switch (playMode)
            {
                case PlayMode.Sequential:
                    nextIndex = _queueIndex + 1;
                    if (nextIndex >= _queue.Count)
                    {
                        reachedBoundary = true;
                        return null;
                    }
                    break;

                case PlayMode.Loop:
                case PlayMode.LoopOne:
                    nextIndex = _queueIndex + 1;
                    if (nextIndex >= _queue.Count)
                    {
                        if (isManual)
                        {
                            reachedBoundary = true;
                            return null;
                        }

                        wrapped = true;
                        nextIndex = 0;
                    }
                    break;

                case PlayMode.Random:
                    int? randomNext = GetRandomNextQueueIndex();
                    if (!randomNext.HasValue)
                    {
                        reachedBoundary = isManual && _queue.Count <= 1;
                        return null;
                    }
                    nextIndex = randomNext.Value;
                    break;

                default:
                    nextIndex = _queueIndex + 1;
                    if (nextIndex >= _queue.Count)
                    {
                        wrapped = true;
                        nextIndex = 0;
                    }
                    break;
            }

            return nextIndex;
        }

        private void ResetRandomNavigationState()
        {
            _randomTimeline.Clear();
            _randomTimelineCursor = -1;
            _randomCycleRemaining.Clear();
        }

        private void SyncRandomHistoryWithQueueIndex()
        {
            if (_queue.Count == 0 || _queueIndex < 0 || _queueIndex >= _queue.Count)
            {
                ResetRandomNavigationState();
                return;
            }

            if (_randomTimelineCursor >= 0 &&
                _randomTimelineCursor < _randomTimeline.Count &&
                _randomTimeline[_randomTimelineCursor] == _queueIndex)
            {
                return;
            }

            _randomTimeline.Clear();
            _randomTimeline.Add(_queueIndex);
            _randomTimelineCursor = 0;
            RebuildRandomCycleRemaining();
            System.Diagnostics.Debug.WriteLine($"[PlaybackQueue][Random] timeline reset to queueIndex={_queueIndex}, queueCount={_queue.Count}, remaining={_randomCycleRemaining.Count}");
        }

        private void RebuildRandomCycleRemaining()
        {
            _randomCycleRemaining.Clear();
            if (_queue.Count <= 1)
            {
                return;
            }

            for (int i = 0; i < _queue.Count; i++)
            {
                if (i != _queueIndex)
                {
                    _randomCycleRemaining.Add(i);
                }
            }
        }

        private int? GetRandomNextQueueIndex()
        {
            SyncRandomHistoryWithQueueIndex();
            if (_queue.Count == 0)
            {
                return null;
            }

            if (_queue.Count == 1)
            {
                return 0;
            }

            if (_randomTimelineCursor >= 0 && _randomTimelineCursor + 1 < _randomTimeline.Count)
            {
                _randomTimelineCursor++;
                int historyIndex = _randomTimeline[_randomTimelineCursor];
                System.Diagnostics.Debug.WriteLine($"[PlaybackQueue][Random] next from timeline: index={historyIndex}, cursor={_randomTimelineCursor}/{_randomTimeline.Count - 1}");
                return historyIndex;
            }

            if (_randomCycleRemaining.Count == 0)
            {
                RebuildRandomCycleRemaining();
            }

            if (_randomCycleRemaining.Count == 0)
            {
                return null;
            }

            int slot = _random.Next(_randomCycleRemaining.Count);
            int nextIndex = _randomCycleRemaining[slot];
            _randomCycleRemaining.RemoveAt(slot);
            AppendRandomTimeline(nextIndex);
            System.Diagnostics.Debug.WriteLine($"[PlaybackQueue][Random] next from cycle: index={nextIndex}, slot={slot}, remaining={_randomCycleRemaining.Count}, queueCount={_queue.Count}");
            return nextIndex;
        }

        private bool TryGetRandomPreviousQueueIndex(bool allowExpand, out int previousIndex)
        {
            previousIndex = -1;
            SyncRandomHistoryWithQueueIndex();

            if (_queue.Count <= 1)
            {
                return false;
            }

            if (_randomTimelineCursor > 0)
            {
                _randomTimelineCursor--;
                previousIndex = _randomTimeline[_randomTimelineCursor];
                System.Diagnostics.Debug.WriteLine($"[PlaybackQueue][Random] previous from timeline: index={previousIndex}, cursor={_randomTimelineCursor}/{_randomTimeline.Count - 1}");
                return previousIndex >= 0 && previousIndex < _queue.Count;
            }

            if (!allowExpand || !TryExpandRandomPreviousQueueIndex(out previousIndex))
            {
                return false;
            }

            return previousIndex >= 0 && previousIndex < _queue.Count;
        }

        private bool TryExpandRandomPreviousQueueIndex(out int previousIndex)
        {
            previousIndex = -1;
            if (!TryTakeRandomIndexFromCycle(out int candidateIndex))
            {
                return false;
            }

            PrependRandomTimeline(candidateIndex);
            previousIndex = candidateIndex;
            System.Diagnostics.Debug.WriteLine($"[PlaybackQueue][Random] previous expanded from cycle: index={candidateIndex}, remaining={_randomCycleRemaining.Count}, queueCount={_queue.Count}");
            return true;
        }

        private bool TryTakeRandomIndexFromCycle(out int queueIndex)
        {
            queueIndex = -1;
            if (_randomCycleRemaining.Count == 0)
            {
                RebuildRandomCycleRemaining();
            }

            if (_randomCycleRemaining.Count == 0)
            {
                return false;
            }

            int slot = _random.Next(_randomCycleRemaining.Count);
            queueIndex = _randomCycleRemaining[slot];
            _randomCycleRemaining.RemoveAt(slot);
            return queueIndex >= 0 && queueIndex < _queue.Count;
        }

        private void PrependRandomTimeline(int queueIndex)
        {
            if (_randomTimelineCursor > 0 && _randomTimelineCursor < _randomTimeline.Count)
            {
                _randomTimeline.RemoveRange(0, _randomTimelineCursor);
                _randomTimelineCursor = 0;
            }

            _randomTimeline.Insert(0, queueIndex);
            _randomTimelineCursor = 0;

            int overflow = _randomTimeline.Count - RandomTimelineLimit;
            if (overflow <= 0)
            {
                return;
            }

            _randomTimeline.RemoveRange(_randomTimeline.Count - overflow, overflow);
        }

        private void AppendRandomTimeline(int queueIndex)
        {
            if (_randomTimelineCursor >= 0 && _randomTimelineCursor < _randomTimeline.Count - 1)
            {
                _randomTimeline.RemoveRange(_randomTimelineCursor + 1, _randomTimeline.Count - _randomTimelineCursor - 1);
            }

            _randomTimeline.Add(queueIndex);
            _randomTimelineCursor = _randomTimeline.Count - 1;

            int overflow = _randomTimeline.Count - RandomTimelineLimit;
            if (overflow <= 0)
            {
                return;
            }

            _randomTimeline.RemoveRange(0, overflow);
            _randomTimelineCursor = Math.Max(0, _randomTimelineCursor - overflow);
        }

        private static int FindSongIndex(IReadOnlyList<SongInfo> list, string songId)
        {
            if (list == null || list.Count == 0 || string.IsNullOrEmpty(songId))
            {
                return -1;
            }

            for (int i = 0; i < list.Count; i++)
            {
                if (list[i]?.Id == songId)
                {
                    return i;
                }
            }

            return -1;
        }

        private void UpdateInjectionSource(SongInfo song, string viewSource)
        {
            if (song == null || string.IsNullOrEmpty(song.Id))
            {
                return;
            }

            if (_injectionSources.TryGetValue(song.Id, out var existing) &&
                !string.IsNullOrWhiteSpace(existing))
            {
                return;
            }

            _injectionSources[song.Id] = viewSource ?? string.Empty;
        }

        private static void AssignSongViewSource(SongInfo song, string viewSource)
        {
            if (song == null)
            {
                return;
            }

            song.ViewSource = string.IsNullOrWhiteSpace(viewSource) ? string.Empty : viewSource;
        }

        private class PendingInjectionInfo
        {
            public PendingInjectionInfo(SongInfo song, string source)
            {
                Song = song;
                Source = source;
            }

            public SongInfo Song { get; }

            public string Source { get; }
        }
    }
}

#pragma warning restore CS8600, CS8601, CS8602, CS8603, CS8604, CS8625








