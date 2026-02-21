#define DEBUG
#nullable disable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using YTPlayer.Core;
using YTPlayer.Core.Download;
using YTPlayer.Core.Lyrics;
using YTPlayer.Core.Playback;
using YTPlayer.Core.Playback.Cache;
using YTPlayer.Core.Recognition;
using YTPlayer.Core.Streaming;
using YTPlayer.Core.Unblock;
using YTPlayer.Core.Upload;
using YTPlayer.Forms;
using YTPlayer.Forms.Download;
using YTPlayer.Models;
using YTPlayer.Models.Auth;
using YTPlayer.Models.Download;
using YTPlayer.Models.Upload;
using YTPlayer.Update;
using YTPlayer.Utils;
#pragma warning disable CS0219, CS8600, CS8601, CS8602, CS8603, CS8604, CS8618, CS8622, CS8625, CS8632, CS4014

namespace YTPlayer
{

public partial class MainForm : Form
{
	private readonly struct NormalizedUrlMatch
	{
		public NeteaseUrlType Type { get; }

		public string EntityName { get; }

		public long NumericId { get; }

		public string IdText { get; }

		public NormalizedUrlMatch(NeteaseUrlType type, string entityName, long numericId)
		{
			Type = type;
			EntityName = entityName;
			NumericId = numericId;
			IdText = numericId.ToString(CultureInfo.InvariantCulture);
		}
	}

	private sealed class SearchViewData<T>
	{
		public List<T> Items { get; }

		public int TotalCount { get; }

		public bool HasMore { get; }

		public int StartIndex { get; }

		public SearchViewData(List<T> items, int totalCount, bool hasMore, int startIndex)
		{
			Items = items ?? new List<T>();
			TotalCount = totalCount;
			HasMore = hasMore;
			StartIndex = startIndex;
		}
	}

	private sealed class MultiUrlViewData
	{
		public List<ListItemInfo> Items { get; }

		public List<SongInfo> AggregatedSongs { get; }

		public List<string> Failures { get; }

		public MultiUrlViewData(List<ListItemInfo> items, List<SongInfo> aggregatedSongs, List<string> failures)
		{
			Items = items ?? new List<ListItemInfo>();
			AggregatedSongs = aggregatedSongs ?? new List<SongInfo>();
			Failures = failures ?? new List<string>();
		}
	}

	private sealed class HomePageViewData
	{
		public List<ListItemInfo> Items { get; }

		public string StatusText { get; }

		public HomePageViewData(List<ListItemInfo> items, string statusText)
		{
			Items = items;
			StatusText = statusText;
		}
	}

	private enum LibraryEntityType
	{
		Songs,
		Playlists,
		Albums,
		Artists,
		Podcasts,
		All
	}

	private enum ManualNavigationAvailability
	{
		Success,
		Missing,
		Failed
	}

	private sealed class ArtistEntryViewData
	{
		public ArtistInfo Artist { get; }

		public ArtistDetail? Detail { get; }

		public List<ListItemInfo> Items { get; }

		public string StatusText { get; }

		public ArtistEntryViewData(ArtistInfo artist, ArtistDetail? detail, List<ListItemInfo> items, string statusText)
		{
			Artist = artist;
			Detail = detail;
			Items = items ?? new List<ListItemInfo>();
			StatusText = statusText;
		}
	}

	private sealed class ArtistSongsViewData
	{
		public List<SongInfo> Songs { get; }

		public bool HasMore { get; }

		public int Offset { get; }

		public int TotalCount { get; }

		public string StatusText { get; }

		public ArtistSongsViewData(List<SongInfo> songs, bool hasMore, int offset, int totalCount, string statusText)
		{
			Songs = songs ?? new List<SongInfo>();
			HasMore = hasMore;
			Offset = offset;
			TotalCount = totalCount;
			StatusText = statusText;
		}
	}

	private sealed class ArtistSongIndexCache
	{
		public List<SongInfo> Songs { get; } = new List<SongInfo>();

		public HashSet<string> SeenIds { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		public List<AlbumInfo> Albums { get; set; } = new List<AlbumInfo>();

		public int AlbumCursor { get; set; }

		public bool IsComplete { get; set; }

		public DateTime LastAccessUtc { get; set; } = DateTime.UtcNow;
	}

	private sealed class ArtistAlbumsViewData
	{
		public List<AlbumInfo> Albums { get; }

		public bool HasMore { get; }

		public int Offset { get; }

		public int TotalCount { get; }

		public ArtistAlbumSortOption Sort { get; }

		public string StatusText { get; }

		public ArtistAlbumsViewData(List<AlbumInfo> albums, bool hasMore, int offset, int totalCount, ArtistAlbumSortOption sort, string statusText)
		{
			Albums = albums ?? new List<AlbumInfo>();
			HasMore = hasMore;
			Offset = offset;
			TotalCount = totalCount;
			Sort = sort;
			StatusText = statusText;
		}
	}

	private sealed class CloudPageViewData
	{
		public List<SongInfo> Songs { get; }

		public bool HasMore { get; }

		public int TotalCount { get; }

		public long UsedSize { get; }

		public long MaxSize { get; }

		public int Offset { get; }

		public CloudPageViewData(List<SongInfo> songs, bool hasMore, int totalCount, long usedSize, long maxSize, int offset)
		{
			Songs = songs;
			HasMore = hasMore;
			TotalCount = totalCount;
			UsedSize = usedSize;
			MaxSize = maxSize;
			Offset = offset;
		}
	}

	private readonly struct ViewLoadResult<T>
	{
		public bool IsCanceled { get; }

		public T Value { get; }

		public ViewLoadResult(bool isCanceled, T value)
		{
			IsCanceled = isCanceled;
			Value = value;
		}
	}

	private sealed class ViewLoadRequest
	{
		public string? ViewSource { get; }

		public string AccessibleName { get; }

		public string LoadingText { get; }

		public bool CancelActiveNavigation { get; }

		public int PendingFocusIndex { get; }

		public bool SuppressLoadingFocus { get; }

		public ViewLoadRequest(string? viewSource, string? accessibleName, string loadingText, bool cancelActiveNavigation = true, int pendingFocusIndex = 0, bool suppressLoadingFocus = true)
		{
			ViewSource = (string.IsNullOrWhiteSpace(viewSource) ? null : viewSource);
			AccessibleName = (string.IsNullOrWhiteSpace(accessibleName) ? "列表" : accessibleName);
			LoadingText = (string.IsNullOrWhiteSpace(loadingText) ? "正在加载..." : loadingText);
			CancelActiveNavigation = cancelActiveNavigation;
			PendingFocusIndex = Math.Max(-1, pendingFocusIndex);
			SuppressLoadingFocus = suppressLoadingFocus;
		}
	}

	private enum MenuInvocationSource
	{
		ViewSelection,
		CurrentPlayback
	}

	private enum MenuEntityKind
	{
		None,
		Song,
		Playlist,
		Album,
		Artist,
		Podcast,
		PodcastEpisode,
		Category
	}

	private class MenuContextSnapshot
	{
		public bool IsValid { get; set; }

		public MenuInvocationSource InvocationSource { get; set; }

		public MenuEntityKind PrimaryEntity { get; set; }

		public SongInfo? Song { get; set; }

		public PlaylistInfo? Playlist { get; set; }

		public AlbumInfo? Album { get; set; }

		public ArtistInfo? Artist { get; set; }

		public PodcastRadioInfo? Podcast { get; set; }

		public PodcastEpisodeInfo? PodcastEpisode { get; set; }

		public ListItemInfo? ListItem { get; set; }

		public ListViewItem? SelectedListItem { get; set; }

		public bool IsLoggedIn { get; set; }

		public bool IsCloudView { get; set; }

		public bool IsMyPlaylistsView { get; set; }

		public bool IsUserAlbumsView { get; set; }

		public bool IsPodcastEpisodeView { get; set; }

		public bool IsArtistSongsView { get; set; }

		public bool IsArtistAlbumsView { get; set; }

		public string ViewSource { get; set; } = string.Empty;

		public bool IsCurrentPlayback => InvocationSource == MenuInvocationSource.CurrentPlayback;

		public bool HasPrimaryEntity => PrimaryEntity != MenuEntityKind.None;
	}

	private sealed class SortState<T> where T : notnull
	{
		private readonly Dictionary<T, string> _accessibleDescriptions;

		private readonly IEqualityComparer<T> _comparer;

		public T CurrentOption { get; private set; }

		public string AccessibleDescription
		{
			get
			{
				string value;
				return _accessibleDescriptions.TryGetValue(CurrentOption, out value) ? value : string.Empty;
			}
		}

		public SortState(T initialOption, IDictionary<T, string> accessibleDescriptions)
		{
			_comparer = EqualityComparer<T>.Default;
			CurrentOption = initialOption;
			_accessibleDescriptions = new Dictionary<T, string>(accessibleDescriptions, _comparer);
		}

		public void SetOption(T option)
		{
			CurrentOption = option;
		}

		public bool EqualsOption(T option)
		{
			return _comparer.Equals(CurrentOption, option);
		}

		public string GetDescription(T option)
		{
			string value;
			return _accessibleDescriptions.TryGetValue(option, out value) ? value : string.Empty;
		}
	}

        private sealed class AccessibleGroupPanel : GroupBox
        {
                public AccessibleGroupPanel()
                {
                        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, value: true);
                }

                protected override void OnPaint(PaintEventArgs e)
                {
                        e.Graphics.Clear(BackColor);
                        using (var pen = new Pen(ThemeManager.Current.Border))
                        {
                                e.Graphics.DrawLine(pen, 0, 0, Width, 0);
                        }
                }
        }

        private sealed class MainFormAccessibleObject : Control.ControlAccessibleObject
        {
                private readonly MainForm _owner;

                public MainFormAccessibleObject(MainForm owner)
                        : base(owner)
                {
                        _owner = owner;
                }

                public override AccessibleRole Role => AccessibleRole.Window;

                public override string Name
                {
                        get
                        {
                                if (_owner != null && !_owner.IsDisposed)
                                {
                                        string title = _owner.Text;
                                        if (!string.IsNullOrWhiteSpace(title))
                                        {
                                                return title;
                                        }
                                }
                                return base.Name ?? BaseWindowTitle;
                        }
                        set
                        {
                                base.Name = value;
                        }
                }

                public override Rectangle Bounds
                {
                        get
                        {
                                try
                                {
                                        if (_owner != null && !_owner.IsDisposed)
                                        {
                                                return _owner.RectangleToScreen(_owner.ClientRectangle);
                                        }
                                }
                                catch
                                {
                                }
                                return base.Bounds;
                        }
                }
        }

	internal sealed class NavigationHistoryItem
	{
		public string ViewSource { get; set; } = string.Empty;

		public string ViewName { get; set; } = string.Empty;

		public int SelectedIndex { get; set; }

		public int SelectedDataIndex { get; set; }

		public string PageType { get; set; } = string.Empty;

		public string CategoryId { get; set; } = string.Empty;

		public string PlaylistId { get; set; } = string.Empty;

		public string AlbumId { get; set; } = string.Empty;

		public string SearchType { get; set; } = string.Empty;

		public string SearchKeyword { get; set; } = string.Empty;

		public int CurrentPage { get; set; }

		public long ArtistId { get; set; }

		public string ArtistName { get; set; } = string.Empty;

		public int ArtistOffset { get; set; }

		public string ArtistOrder { get; set; } = string.Empty;

		public string ArtistAlbumSort { get; set; } = string.Empty;

		public int ArtistType { get; set; }

		public int ArtistArea { get; set; }

		public long PodcastRadioId { get; set; }

		public int PodcastOffset { get; set; }

		public bool PodcastAscending { get; set; }

		public string PodcastRadioName { get; set; } = string.Empty;

		public string MixedQueryKey { get; set; } = string.Empty;

		public string SongId { get; set; } = string.Empty;
	}

	internal enum ArtistSongSortOption
	{
		Hot,
		Time
	}

	internal enum ArtistAlbumSortOption
	{
		Latest,
		Oldest
	}

	protected NeteaseApiClient _apiClient = null;

	private UnblockService _unblockService = null;

	private BassAudioEngine _audioEngine = null;

	private SeekManager _seekManager = null;
	private PositionCoordinator _positionCoordinator = null;

	protected ConfigManager _configManager = null;

	private ConfigModel _config = null;

	private AccountState _accountState = null;

	protected List<SongInfo> _currentSongs = new List<SongInfo>();

	private List<PlaylistInfo> _currentPlaylists = new List<PlaylistInfo>();

	private PlaylistInfo? _currentPlaylist = null;

	private bool _currentPlaylistOwnedByUser = false;

	private PlaylistInfo? _userLikedPlaylist = null;

	private List<AlbumInfo> _currentAlbums = new List<AlbumInfo>();

	private readonly object _podcastCategoryLock = new object();

	private readonly Dictionary<int, PodcastCategoryInfo> _podcastCategories = new Dictionary<int, PodcastCategoryInfo>();

	private List<PodcastRadioInfo> _currentPodcasts = new List<PodcastRadioInfo>();

	private List<PodcastEpisodeInfo> _currentPodcastSounds = new List<PodcastEpisodeInfo>();

	private PodcastRadioInfo? _currentPodcast = null;

	private int _currentPodcastSoundOffset = 0;

	private bool _currentPodcastHasMore = false;

	private int _currentPodcastCategoryId = 0;

	private string _currentPodcastCategoryName = string.Empty;

	private int _currentPodcastCategoryOffset = 0;

	private bool _currentPodcastCategoryHasMore = false;

	private List<ListItemInfo> _currentListItems = new List<ListItemInfo>();

	private readonly HashSet<string> _likedSongIds = new HashSet<string>(StringComparer.Ordinal);

	private readonly HashSet<string> _subscribedPlaylistIds = new HashSet<string>(StringComparer.Ordinal);

	private readonly HashSet<string> _ownedPlaylistIds = new HashSet<string>(StringComparer.Ordinal);

	private readonly HashSet<string> _subscribedAlbumIds = new HashSet<string>(StringComparer.Ordinal);

	private readonly HashSet<long> _subscribedPodcastIds = new HashSet<long>();

	private readonly HashSet<long> _subscribedArtistIds = new HashSet<long>();

	private bool _likedSongsCacheValid;

	private readonly object _libraryStateLock = new object();

	private const string RecentListenedCategoryId = "recent_listened";

	private const string RecentPodcastsCategoryId = "recent_podcasts";

	private const string PersonalFmCategoryId = "personal_fm";

	private const string PersonalFmAccessibleName = "私人 FM";

	private readonly object _personalFmStateLock = new object();

	private readonly List<SongInfo> _personalFmSongsCache = new List<SongInfo>();

	private readonly HashSet<string> _personalFmSongKeySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	private int _personalFmLastFocusedIndex = -1;

	private int _personalFmAppendInFlight = 0;

        private readonly Dictionary<string, int> _homeItemIndexMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, Dictionary<string, int>> _playlistTrackIndexMapCache = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, Dictionary<int, string>> _playlistTrackIdByIndexMapCache = new Dictionary<string, Dictionary<int, string>>(StringComparer.OrdinalIgnoreCase);

        private readonly Queue<string> _playlistTrackIndexMapOrder = new Queue<string>();

        private const int PlaylistTrackIndexMapCacheLimit = 4;

	private const string DownloadSongMenuText = "下载歌曲(&D)";

	private const string DownloadSoundMenuText = "下载声音(&D)";

	private const string CurrentPlayingMenuContextTag = "current_playing_context";

	private int _recentPlayCount = 0;

	private int _recentPlaylistCount = 0;

	private int _recentAlbumCount = 0;

	private int _recentPodcastCount = 0;

	private bool _recentSummaryReady = false;

	private List<SongInfo> _recentSongsCache = new List<SongInfo>();

	private List<PlaylistInfo> _recentPlaylistsCache = new List<PlaylistInfo>();

	private List<AlbumInfo> _recentAlbumsCache = new List<AlbumInfo>();

	private List<PodcastRadioInfo> _recentPodcastsCache = new List<PodcastRadioInfo>();

	private DateTime _recentSummaryLastUpdatedUtc = DateTime.MinValue;

	private int? _homeCachedUserPlaylistCount;

	private int? _homeCachedUserAlbumCount;

	private int? _homeCachedArtistFavoritesCount;

	private int? _homeCachedPodcastFavoritesCount;

	private int? _homeCachedPodcastCategoryCount;

	private int? _homeCachedHighQualityCount;

	private int? _homeCachedToplistCount;

	private int? _homeCachedNewAlbumCount;

	private int? _homeCachedDailyRecommendSongCount;

	private int? _homeCachedDailyRecommendPlaylistCount;

	private int? _homeCachedPersonalizedSongCount;

	private int? _homeCachedPersonalizedPlaylistCount;

	private List<SongInfo>? _dailyRecommendSongsCache;

	private List<PlaylistInfo>? _dailyRecommendPlaylistsCache;

	private DateTime _dailyRecommendCacheFetchedUtc = DateTime.MinValue;

	private DateTime _dailyRecommendSongsFetchedUtc = DateTime.MinValue;

	private DateTime _dailyRecommendPlaylistsFetchedUtc = DateTime.MinValue;

	private List<PlaylistInfo>? _personalizedPlaylistsCache;

	private List<SongInfo>? _personalizedNewSongsCache;

	private DateTime _personalizedCacheFetchedUtc = DateTime.MinValue;

	private DateTime _personalizedPlaylistsFetchedUtc = DateTime.MinValue;

	private DateTime _personalizedSongsFetchedUtc = DateTime.MinValue;

	private static readonly TimeSpan RecommendationCacheTtl = TimeSpan.FromMinutes(10.0);

	private DateTime _highQualityCountFetchedUtc = DateTime.MinValue;

	private static readonly TimeSpan HighQualityCountCacheTtl = TimeSpan.FromMinutes(20.0);

	private SortState<bool> _podcastSortState = new SortState<bool>(initialOption: false, new Dictionary<bool, string>
	{
		{ false, "当前排序：按最新" },
		{ true, "当前排序：节目顺序" }
	});

	private List<LyricLine> _currentLyrics = new List<LyricLine>();

	private PlaybackReportingService? _playbackReportingService;

	private LyricsCacheManager _lyricsCacheManager = null;

	private LyricsDisplayManager _lyricsDisplayManager = null;

	private LyricsLoader _lyricsLoader = null;

	private bool _autoReadLyrics = false;

	private bool _hideSequenceNumbers = false;

	private bool _hideControlBar = false;

	private bool _preventSleepDuringPlayback = true;

	private bool _focusFollowPlayback = true;

	private CancellationTokenSource? _lyricsSpeechCts;

	private readonly object _lyricsSpeechLock = new object();

	private TimeSpan? _lastLyricSpeechAnchor;

	private TimeSpan? _lastLyricPlaybackPosition;

	private bool _suppressLyricSpeech;

	private double? _resumeLyricSpeechAtSeconds;

	private static readonly TimeSpan LyricsSpeechClusterTolerance = TimeSpan.FromMilliseconds(320.0);

	private static readonly TimeSpan LyricJumpThreshold = TimeSpan.FromSeconds(1.5);

	private System.Windows.Forms.Timer? _updateTimer;

	private NotifyIcon? _trayIcon;

	private ContextMenuHost? _contextMenuHost;

	private bool _isApplicationExitRequested = false;

	private bool _isFormClosing = false;

	private IntPtr _powerRequestHandle = IntPtr.Zero;

	private IntPtr _powerRequestReasonBuffer = IntPtr.Zero;

	private bool _powerRequestSleepActive = false;
	private bool _powerRequestDisplayActive = false;
	private bool _powerRequestSystemActive = false;

	private int _powerRequestInitFailureCount = 0;

	private DateTime _powerRequestRetryNotBeforeUtc = DateTime.MinValue;

	private DateTime _lastPowerRequestRefreshUtc = DateTime.MinValue;

	private DateTime _appStartTime = DateTime.Now;

	private CancellationTokenSource? _autoUpdateCheckCts;

	private bool _autoUpdateCheckScheduled;

        private bool _autoUpdatePromptShown;

        private bool _isUserDragging = false;

	private int _currentPage = 1;

	private string _currentSearchType = "歌曲";

	private int _resultsPerPage = 100;

	private int _maxPage = 1;

	private bool _hasNextSearchPage = false;

	private readonly object _paginationLimitLock = new object();

	private readonly Dictionary<string, int> _paginationOffsetCaps = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

	private readonly object _artistSongCacheLock = new object();

	private readonly Dictionary<string, ArtistSongIndexCache> _artistSongIndexCache = new Dictionary<string, ArtistSongIndexCache>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, SemaphoreSlim> _artistSongIndexLocks = new Dictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, List<SongInfo>> _albumSongsCache = new Dictionary<string, List<SongInfo>>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, SemaphoreSlim> _albumSongsLocks = new Dictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, int> _artistSongsTotalCountCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

	private const int ArtistSongIndexCacheLimit = 4;

	private const int AlbumSongsCacheLimit = 64;

	private const int ArtistSongsAlbumFetchConcurrency = 4;

	private bool _isCurrentPlayingMenuActive = false;

	private SongInfo? _currentPlayingMenuSong;

	private int _lastListViewFocusedIndex = -1;

        private int _lastListViewSpokenIndex = -1;

        private string? _lastListViewSpokenViewSource;

        private string? _lastListViewSpokenText;

        private DateTime _lastListViewSpokenAt = DateTime.MinValue;

        private CancellationTokenSource? _listViewFocusSpeechCts;

        private string? _pendingListHeaderPrefix;

        private string? _pendingListHeaderViewSource;

        private CancellationTokenSource? _listViewHeaderFocusCts;

        private const int ListViewFocusSpeakDelayMs = 120;
        private const int ListViewLayoutDebounceDelayMs = 500;
        private const int ListViewFocusStableDelayMs = 500;
        private const int ListViewSelectionStableDelayMs = 90;
        private const int ListViewAccessibilityPartialRefreshThreshold = 160;
        private const int ListViewAccessibilityNameSyncItemLimit = 120;
        private const int ListViewLayoutFastPathItemThreshold = 300;
        private const int ListViewRowHeightFastPathItemThreshold = 1200;
        private const int ListViewSelectionBurstThresholdMs = 150;
        private const int ListViewSelectionNvdaBurstUpdateMinIntervalMs = 260;
        private const int ListViewBurstNavigationFlushIntervalMs = 45;
        private const int ListViewBurstNavigationFlushMaxPerPass = 96;
        private const int ListViewBurstPropertySyncMaxUpdates = 8;
        private const int ListViewNvdaNavigationKeyMinIntervalMs = 150;

        private const int ListViewRepeatCooldownMs = 200;
        private const int ListViewTypeSearchTimeoutMs = 900;

        private bool _isApplyingListViewLayout;
        private long _listViewLayoutDataVersion;
        private ListViewLayoutSignature? _lastListViewLayoutSignature;
        private DateTime _lastListViewFocusChangeUtc = DateTime.MinValue;
        private DateTime _lastListViewLayoutRequestUtc = DateTime.MinValue;
        private DateTime _lastNvdaListViewNavigationUtc = DateTime.MinValue;
        private bool _listViewLayoutPending;
        private bool _listViewRedrawDeferred;
#if DEBUG
        private const int UiThreadWatchdogIntervalMs = 250;
        private const int UiThreadWatchdogBlockThresholdMs = 2000;
        private const int AccessibilityDiagPulseIntervalMs = 1000;
        private const int AccessibilityBurstNotifyThrottleMs = 90;
        private const int AccessibilityBurstUpdateMinIntervalMs = 180;
        private const int DiagnosticsDumpCooldownSeconds = 120;
        private const int DiagnosticsHandleDumpThreshold = 26000;
        private const int DiagnosticsUserObjectDumpThreshold = 9000;
        private const int DiagnosticsGdiObjectDumpThreshold = 9000;
        private const int NvdaResourceProbeFailureBackoffSeconds = 30;
        private System.Threading.Timer? _uiThreadWatchdogTimer;
        private System.Threading.Timer? _accessibilityDiagnosticsTimer;
        private bool _uiThreadWatchdogInStall;
        private string? _uiThreadWatchdogLastMarker;
        private DateTime _uiThreadWatchdogLastMarkerUtc = DateTime.MinValue;
        private DateTime _uiThreadWatchdogLastHeartbeatUtc = DateTime.MinValue;
        private DateTime _lastDiagnosticsDumpUtc = DateTime.MinValue;
        private DateTime _lastBurstListViewNotifyUtc = DateTime.MinValue;
        private long _accessNotifyClientCallCount;
        private long _accessNotifyWinEventCallCount;
        private long _accessSetPropertyCallCount;
        private long _accessSelectionChangedCount;
        private long _accessSelectionApplyCount;
        private long _accessBurstSuppressedNotifyCount;
        private long _accessBurstNavFlushRemovedCount;
        private long _accessNvdaNavKeySuppressedCount;
        private long _accessNvdaNavAppliedStepCount;
        private long _diagLastNotifyClientCallCount;
        private long _diagLastNotifyWinEventCallCount;
        private long _diagLastSetPropertyCallCount;
        private long _diagLastSelectionChangedCount;
        private long _diagLastSelectionApplyCount;
        private long _diagLastBurstSuppressedNotifyCount;
        private long _diagLastBurstNavFlushRemovedCount;
        private long _diagLastNvdaNavKeySuppressedCount;
        private long _diagLastNvdaNavAppliedStepCount;
        private int _nvdaResourceProbeConsecutiveFailures;
        private DateTime _nvdaResourceProbeBlockedUntilUtc = DateTime.MinValue;
#endif

        private System.Windows.Forms.Timer? _listViewAccessibilityDebounceTimer;
        private bool _listViewAccessibilityPending;
        private DateTime _lastListViewAccessibilityRequestUtc = DateTime.MinValue;

        private const int ListViewMaxRowHeight = 512;
        private const int ListViewMultiLineMaxLines = 2;
        private int _listViewShortInfoColumnIndex = 4;
        private const int ListViewTextHorizontalPadding = 12;
        private const int ListViewAutoWidthSampleLimit = 240;
        private const double ListViewCountSummaryShortInfoThreshold = 0.5;
        private const int ListViewAutoShrinkPadding = 24;
        private const int ListViewAutoShrinkThreshold = 120;
        private const int ListViewAutoShrinkMinDelta = 6;
        private const int ListViewRowResizeGripHeight = 4;
        private const int ListViewRowResizeMinHeight = 20;
        private bool _isListViewRowResizing;
        private int[]? _listViewColumnWidthSnapshot;
        private int _listViewRowResizeStartY;
        private int _listViewRowResizeStartHeight;
        private Cursor? _listViewRowResizeOriginalCursor;
        private int? _customListViewRowHeight;
        private int? _customListViewIndexWidth;
        private int? _customListViewNameWidth;
        private int? _customListViewCreatorWidth;
        private int? _customListViewExtraWidth;
        private int? _customListViewDescriptionWidth;
        private bool _isUserResizingListViewColumns;
        private bool _isListViewAutoShrinkActive;
        private int _lastListViewAutoWidth = -1;
        private readonly struct ListViewLayoutSignature : IEquatable<ListViewLayoutSignature>
        {
                public readonly long DataVersion;
                public readonly string ViewSource;
                public readonly ListViewDataMode DataMode;
                public readonly int HostWidth;
                public readonly int AvailableWidth;
                public readonly bool HideSequence;
                public readonly int ItemCount;
                public readonly bool VirtualMode;
                public readonly int VirtualListSize;
                public readonly int RowHeight;
                public readonly int FontHeight;
                public readonly int? CustomIndexWidth;
                public readonly int? CustomNameWidth;
                public readonly int? CustomCreatorWidth;
                public readonly int? CustomExtraWidth;
                public readonly int? CustomDescriptionWidth;
                public readonly int? CustomRowHeight;
                public readonly bool IsUserResizingColumns;

                public ListViewLayoutSignature(
                        long dataVersion,
                        string viewSource,
                        ListViewDataMode dataMode,
                        int hostWidth,
                        int availableWidth,
                        bool hideSequence,
                        int itemCount,
                        bool virtualMode,
                        int virtualListSize,
                        int rowHeight,
                        int fontHeight,
                        int? customIndexWidth,
                        int? customNameWidth,
                        int? customCreatorWidth,
                        int? customExtraWidth,
                        int? customDescriptionWidth,
                        int? customRowHeight,
                        bool isUserResizingColumns)
                {
                        DataVersion = dataVersion;
                        ViewSource = viewSource ?? string.Empty;
                        DataMode = dataMode;
                        HostWidth = hostWidth;
                        AvailableWidth = availableWidth;
                        HideSequence = hideSequence;
                        ItemCount = itemCount;
                        VirtualMode = virtualMode;
                        VirtualListSize = virtualListSize;
                        RowHeight = rowHeight;
                        FontHeight = fontHeight;
                        CustomIndexWidth = customIndexWidth;
                        CustomNameWidth = customNameWidth;
                        CustomCreatorWidth = customCreatorWidth;
                        CustomExtraWidth = customExtraWidth;
                        CustomDescriptionWidth = customDescriptionWidth;
                        CustomRowHeight = customRowHeight;
                        IsUserResizingColumns = isUserResizingColumns;
                }

                public bool Equals(ListViewLayoutSignature other)
                {
                        return DataVersion == other.DataVersion &&
                               DataMode == other.DataMode &&
                               HostWidth == other.HostWidth &&
                               AvailableWidth == other.AvailableWidth &&
                               HideSequence == other.HideSequence &&
                               ItemCount == other.ItemCount &&
                               VirtualMode == other.VirtualMode &&
                               VirtualListSize == other.VirtualListSize &&
                               RowHeight == other.RowHeight &&
                               FontHeight == other.FontHeight &&
                               CustomIndexWidth == other.CustomIndexWidth &&
                               CustomNameWidth == other.CustomNameWidth &&
                               CustomCreatorWidth == other.CustomCreatorWidth &&
                               CustomExtraWidth == other.CustomExtraWidth &&
                               CustomDescriptionWidth == other.CustomDescriptionWidth &&
                               CustomRowHeight == other.CustomRowHeight &&
                               IsUserResizingColumns == other.IsUserResizingColumns &&
                               string.Equals(ViewSource, other.ViewSource, StringComparison.OrdinalIgnoreCase);
                }

                public override bool Equals(object? obj)
                {
                        return obj is ListViewLayoutSignature other && Equals(other);
                }

                public override int GetHashCode()
                {
                        var hash = new HashCode();
                        hash.Add(DataVersion);
                        hash.Add(ViewSource, StringComparer.OrdinalIgnoreCase);
                        hash.Add(DataMode);
                        hash.Add(HostWidth);
                        hash.Add(AvailableWidth);
                        hash.Add(HideSequence);
                        hash.Add(ItemCount);
                        hash.Add(VirtualMode);
                        hash.Add(VirtualListSize);
                        hash.Add(RowHeight);
                        hash.Add(FontHeight);
                        hash.Add(CustomIndexWidth);
                        hash.Add(CustomNameWidth);
                        hash.Add(CustomCreatorWidth);
                        hash.Add(CustomExtraWidth);
                        hash.Add(CustomDescriptionWidth);
                        hash.Add(CustomRowHeight);
                        hash.Add(IsUserResizingColumns);
                        return hash.ToHashCode();
                }
        }
        private enum ListViewColumnRole
        {
                Sequence,
                Title,
                Creator,
                ShortInfo,
                Description,
                Hidden
        }

        private enum ListViewDataMode
        {
                Unknown,
                Songs,
                Playlists,
                Albums,
                Podcasts,
                PodcastEpisodes,
                Artists,
                ListItems
        }
        private bool _listViewLayoutInitialized;
        private FormWindowState _lastWindowState = FormWindowState.Normal;
        private bool _isApplyingWindowLayout;
        private System.Windows.Forms.Timer? _windowLayoutPersistTimer;
        private System.Windows.Forms.Timer? _playlistOrderAutoSaveTimer;
        private System.Windows.Forms.Timer? _songOrderAutoSaveTimer;
        private System.Windows.Forms.Timer? _listViewLayoutDebounceTimer;
        private bool _playlistOrderSaving;
        private bool _songOrderSaving;
        private List<string>? _pendingPlaylistOrderIds;
        private List<string>? _pendingSongOrderIds;
        private string? _pendingSongOrderPlaylistId;
        private enum ReorderDragMode
        {
                None,
                PlaylistList,
                PlaylistSongs
        }

        private ReorderDragMode _reorderDragMode = ReorderDragMode.None;
        private int _reorderDragStartIndex = -1;
        private string? _reorderDragPlaylistId;

	private DateTime _lastNarratorCheckAt = DateTime.MinValue;

	private bool _isNarratorRunningCached = false;

	private const int NarratorCheckIntervalMs = 1000;

        private DateTime _lastNvdaCheckAt = DateTime.MinValue;

        private bool _isNvdaRunningCached = false;

        private const int NvdaCheckIntervalMs = 1000;

        private DateTime _lastZdsrCheckAt = DateTime.MinValue;

        private bool _isZdsrRunningCached = false;

        private const int ZdsrCheckIntervalMs = 1000;

	private const int ListViewSpeechColumnIndex = 0;

	private const int ListViewFirstDataColumnIndex = 1;

	private const int ListViewDataColumnCount = 5;

	private const int ListViewTotalColumnCount = 6;

	private const int VirtualSongListThreshold = 600;

	private const int LongPlaylistSongThreshold = 1000;

	private const int LongPlaylistBatchSize = 200;

	private const int LongPlaylistBatchDelayMs = 1500;

	private const string ListLoadingPlaceholderText = "正在加载 ...";
	private const string ListRetryPlaceholderText = "暂无内容，刷新重试";
	private const int ListRetryPlaceholderTag = -5;

	private bool _isVirtualSongListActive = false;

	private List<SongInfo> _virtualSongs = new List<SongInfo>();

	private int _virtualStartIndex = 1;

	private int _currentSequenceStartIndex = 1;

	private bool _virtualShowPagination = false;

	private bool _virtualHasPreviousPage = false;

	private bool _virtualHasNextPage = false;

	private int _virtualCacheStartIndex = -1;

	private int _virtualCacheEndIndex = -1;

	private ListViewItem[]? _virtualItemCache;

	private string _lastKeyword = "";

	private readonly PlaybackQueueManager _playbackQueue = new PlaybackQueueManager();

	private bool _suppressAutoAdvance = false;

	private string _currentViewSource = "";

        private const string MixedSearchTypeDisplayName = "混合";

        private const int DefaultControlCornerRadius = 10;

        private readonly Dictionary<Control, int> _roundedControls = new Dictionary<Control, int>();

        private bool _isMixedSearchTypeActive = false;

        private string _lastExplicitSearchType = "歌曲";

        private bool _suppressSearchTypeComboEvents = false;

        private string? _currentMixedQueryKey = null;

	private const int MaxSearchHistoryEntries = 50;

	private int _searchHistoryIndex = -1;

        private string _searchHistoryDraft = string.Empty;

        private AutoCompleteStringCollection? _searchHistoryAutoComplete;

        private bool _searchAutoCompleteDisabled;

        private bool _searchAutoCompletePending;

        private bool _isApplyingSearchHistoryText = false;

	private bool _lastPlaybackRestored = false;

	private const string LastPlaybackViewSource = "resume:last_playback";

	private CancellationTokenSource? _initialHomeLoadCts;

	private bool _initialHomeLoadCompleted = false;

	private int _autoFocusSuppressionDepth = 0;

	private const int InitialHomeRetryDelayMs = 1500;

	private long _loggedInUserId = 0L;

	private bool _isHomePage = false;

	private bool _listLoadingPlaceholderActive = false;

	private string _listViewTypeSearchBuffer = string.Empty;

	private DateTime _listViewTypeSearchLastInputUtc = DateTime.MinValue;

	private Stack<NavigationHistoryItem> _navigationHistory = new Stack<NavigationHistoryItem>();

	private DateTime _lastBackTime = DateTime.MinValue;

	private const int MIN_BACK_INTERVAL_MS = 300;

	private bool _isNavigating = false;

	private bool _pendingBackNavigation = false;

	private const string BaseWindowTitle = "易听";

	private CancellationTokenSource? _availabilityCheckCts;

	private CancellationTokenSource? _searchCts;

	private int _cloudPage = 1;

	private bool _cloudHasMore = false;

	private int _cloudTotalCount = 0;

	private long _cloudUsedSize = 0L;

	private long _cloudMaxSize = 0L;

	private bool _cloudLoading = false;

	private string? _pendingCloudFocusId = null;

	private string? _lastSelectedCloudSongId = null;

	private Guid? _lastNotifiedUploadFailureTaskId = null;

	private static readonly (string Cat, string DisplayName, string Description)[] _homePlaylistCategoryPresets = new(string, string, string)[10]
	{
		("华语", "华语", "华语歌单"),
		("流行", "流行", "流行歌单"),
		("摇滚", "摇滚", "摇滚歌单"),
		("民谣", "民谣", "民谣歌单"),
		("电子", "电子", "电子音乐歌单"),
		("轻音乐", "轻音乐", "轻音乐歌单"),
		("影视原声", "影视原声", "影视原声歌单"),
		("ACG", "ACG", "ACG歌单"),
		("怀旧", "怀旧", "怀旧歌单"),
		("治愈", "治愈", "治愈歌单")
	};

	private readonly object _playbackCancellationLock = new object();

	private CancellationTokenSource? _playbackCancellation = null;

	private DateTime _lastPlayRequestTime = DateTime.MinValue;

	private const int MIN_PLAY_REQUEST_INTERVAL_MS = 200;

	private long _playRequestVersion = 0L;

	private DateTime _lastSyncButtonTextTime = DateTime.MinValue;

	private const int MIN_SYNC_BUTTON_INTERVAL_MS = 50;

	private double _cachedPosition = 0.0;

	private double _cachedDuration = 0.0;

	private PlaybackState _cachedPlaybackState = PlaybackState.Stopped;

	private readonly object _stateCacheLock = new object();

	private CancellationTokenSource? _stateUpdateCancellation = null;

	private bool _stateUpdateLoopRunning = false;

	private bool _isPlaybackLoading = false;

	private int _switchingTrackCount = 0;

	private readonly object _bufferingRecoveryLock = new object();

	private string? _bufferingRecoverySongId = null;

	private DateTime _bufferingRecoverySinceUtc = DateTime.MinValue;

	private DateTime _bufferingRecoveryNoProgressSinceUtc = DateTime.MinValue;

	private DateTime _bufferingRecoveryLastAttemptUtc = DateTime.MinValue;

	private double _bufferingRecoveryLastPositionSeconds = -1.0;

	private int _bufferingRecoveryInFlight = 0;

	private int _playPauseCommandInFlight = 0;

	private string? _playButtonTextBeforeLoading = null;

	private string? _statusTextBeforeLoading = null;

	private NextSongPreloader? _nextSongPreloader = null;

	private bool _leftKeyPressed = false;

	private bool _rightKeyPressed = false;

	private bool _leftScrubActive = false;

	private bool _rightScrubActive = false;

	private DateTime _leftKeyDownTime = DateTime.MinValue;

	private DateTime _rightKeyDownTime = DateTime.MinValue;

	private System.Windows.Forms.Timer? _scrubKeyTimer;

	private const int KEY_SCRUB_TRIGGER_MS = 350;

	private const int KEY_SCRUB_INTERVAL_MS = 200;

	private const double KEY_SCRUB_STEP_SECONDS = 1.0;

	private const double KEY_JUMP_STEP_SECONDS = 5.0;

	private static readonly TimeSpan BufferingRecoveryTriggerDuration = TimeSpan.FromSeconds(12.0);

	private static readonly TimeSpan BufferingRecoveryRetryCooldown = TimeSpan.FromSeconds(20.0);

	private const double BufferingRecoveryProgressEpsilonSeconds = 0.15;

	private const int SONG_URL_TIMEOUT_MS = 12000;

	private const int INITIAL_RETRY_DELAY_MS = 1200;

	private const int MAX_RETRY_DELAY_MS = 5000;

	private const int SONG_URL_CACHE_MINUTES = 30;

	private const int RecentPlayFetchLimit = 300;

	private const int RecentPlaylistFetchLimit = 100;

	private const int RecentAlbumFetchLimit = 100;

	private const int RecentPodcastFetchLimit = 100;

	private const int PodcastSoundPageSize = 50;

	private const int PodcastCategoryPageSize = 50;

	private const int CloudPageSize = 50;

	private const int PlaylistCategoryPageSize = 50;

	private const int NewSongsPageSize = 100;

	private const int HighQualityPlaylistsPageSize = 100;

	private const int HighQualityPlaylistsCacheLimit = 6;

	private const int ToplistPageSize = 50;

	private readonly bool _enableHighQualityPlaylistsAll = true;

	private readonly bool _enableArtistCategoryAll = true;

	private static readonly char[] MultiUrlSeparators = new char[2] { ';', '；' };

	private readonly Dictionary<LibraryEntityType, DateTime> _libraryCacheTimestamps = new Dictionary<LibraryEntityType, DateTime>
	{
		[LibraryEntityType.Songs] = DateTime.MinValue,
		[LibraryEntityType.Playlists] = DateTime.MinValue,
		[LibraryEntityType.Albums] = DateTime.MinValue,
		[LibraryEntityType.Artists] = DateTime.MinValue,
		[LibraryEntityType.Podcasts] = DateTime.MinValue
	};

	private readonly Dictionary<string, List<SongInfo>> _listenMatchCache = new Dictionary<string, List<SongInfo>>(StringComparer.OrdinalIgnoreCase);

	private static readonly TimeSpan LibraryRefreshInterval = TimeSpan.FromSeconds(35.0);

	private const int SW_RESTORE = 9;

	private PlaybackReportContext? _activePlaybackReport;

	private readonly object _playbackReportingLock = new object();

	private DownloadManager? _downloadManager;

	private DownloadManagerForm? _downloadManagerForm;

	private List<ArtistInfo> _currentArtists = new List<ArtistInfo>();

	private ArtistInfo? _currentArtist;

	private ArtistDetail? _currentArtistDetail;

	private int _currentArtistSongsOffset;

	private bool _currentArtistSongsHasMore;

	private int _currentArtistAlbumsOffset;

	private bool _currentArtistAlbumsHasMore;

	private int _currentArtistSongsTotalCount;

	private int _currentArtistAlbumsTotalCount;

	private SortState<ArtistSongSortOption> _artistSongSortState = new SortState<ArtistSongSortOption>(ArtistSongSortOption.Hot, new Dictionary<ArtistSongSortOption, string>
	{
		{
			ArtistSongSortOption.Hot,
			"当前排序：按热度"
		},
		{
			ArtistSongSortOption.Time,
			"当前排序：按发布时间"
		}
	});

	private SortState<ArtistAlbumSortOption> _artistAlbumSortState = new SortState<ArtistAlbumSortOption>(ArtistAlbumSortOption.Latest, new Dictionary<ArtistAlbumSortOption, string>
	{
		{
			ArtistAlbumSortOption.Latest,
			"当前排序：按最新发布"
		},
		{
			ArtistAlbumSortOption.Oldest,
			"当前排序：按最早发布"
		}
	});

	private readonly Dictionary<long, List<AlbumInfo>> _artistAlbumsAscendingCache = new Dictionary<long, List<AlbumInfo>>();

	private const int ArtistAlbumsAscendingCacheLimit = 4;

	private int _currentArtistTypeFilter = -1;

	private int _currentArtistAreaFilter = -1;

	private bool _currentArtistCategoryHasMore;

	private int _currentArtistCategoryTotalCount;

	private readonly Dictionary<long, (int MusicCount, int AlbumCount)> _artistStatsCache = new Dictionary<long, (int, int)>();

	private readonly HashSet<long> _artistStatsInFlight = new HashSet<long>();

	private CancellationTokenSource? _artistStatsRefreshCts;

	private readonly Dictionary<long, string> _artistIntroCache = new Dictionary<long, string>();

	private const int ArtistSongsPageSize = 100;

	private const int ArtistAlbumsPageSize = 100;

	private const int ArtistDetailFetchConcurrency = 8;

	private static int ArtistDetailFetchDelayMs = 0;

	private int _currentPodcastEpisodeTotalCount;

	private int _currentPodcastCategoryTotalCount;

	private readonly Dictionary<string, int> _podcastCategoryFetchOffsets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

	private int _currentPlaylistCategoryTotalCount;

	private int _currentPlaylistCategoryOffset;

	private bool _currentPlaylistCategoryHasMore;

	private string _currentPlaylistCategoryName = string.Empty;

	private bool _currentHighQualityLoadedAll;

	private int _currentHighQualityTotalCount;

	private int _currentHighQualityOffset;

	private bool _currentHighQualityHasMore;

	private bool _currentArtistCategoryLoadedAll;

	private int _currentNewSongsTotalCount;

	private int _currentNewSongsOffset;

	private bool _currentNewSongsHasMore;

	private int _currentNewSongsAreaType;

	private string _currentNewSongsAreaName = string.Empty;

	private int _currentToplistTotalCount;

	private int _currentToplistOffset;

	private bool _currentToplistHasMore;

	private readonly Dictionary<int, List<PlaylistInfo>> _highQualityPlaylistsCache = new Dictionary<int, List<PlaylistInfo>>();

	private readonly Dictionary<int, long> _highQualityBeforeByOffset = new Dictionary<int, long>();

	private readonly Dictionary<int, bool> _highQualityHasMoreByOffset = new Dictionary<int, bool>();

	private readonly object _highQualityCacheLock = new object();

	private readonly Queue<int> _highQualityCacheOrder = new Queue<int>();

	private readonly Dictionary<int, List<SongInfo>> _newSongsCacheByArea = new Dictionary<int, List<SongInfo>>();

	private readonly object _newSongsCacheLock = new object();

	private readonly List<PlaylistInfo> _toplistCache = new List<PlaylistInfo>();

	private readonly object _toplistCacheLock = new object();

	private SongRecognitionCoordinator? _songRecognitionCoordinator;

	private ToolStripMenuItem? _listenRecognitionMenuItem;

	private readonly object _viewContentLock = new object();

	private CancellationTokenSource? _viewContentCts;

	private CancellationTokenSource? _navigationCts;

        private string? _pendingListFocusViewSource;

        private int _pendingListFocusIndex = -1;

        private bool _pendingListFocusFromPlayback = false;

        private string? _deferredPlaybackFocusViewSource;

        private string? _pendingSongFocusId;

	private string? _pendingSongFocusViewSource;

	private bool _skipCloudRestoreOnce = false;

        private bool _pendingSongFocusSatisfied = false;

        private string? _pendingSongFocusSatisfiedViewSource;

        private int _pendingPlaceholderPlaybackIndex = -1;

        private string? _pendingPlaceholderPlaybackViewSource;

        private string? _lastListenMatchSessionId;

	private List<SongInfo> _listenMatchAggregate = new List<SongInfo>();

	private string? _lastAnnouncedViewSource;

	private string? _lastAnnouncedHeader;

	private Label? _accessibilityAnnouncementLabel;

	private string? _lastAnnouncementText;

	private DateTime _lastAnnouncementAt = DateTime.MinValue;

	private bool _focusedRefreshAnnouncementQueued = false;

	private int _pendingFocusedRefreshAnnouncementIndex = -1;

	private static readonly TimeSpan AnnouncementRepeatCooldown = TimeSpan.FromMilliseconds(350.0);

	private IContainer components = null;

	private MenuStrip menuStrip1;

	private ToolStripMenuItem fileMenuItem;

	private ToolStripMenuItem homeMenuItem;

	private ToolStripMenuItem loginMenuItem;

	private ToolStripSeparator toolStripSeparatorDownload1;

	private ToolStripMenuItem openDownloadDirMenuItem;

	private ToolStripMenuItem changeDownloadDirMenuItem;

	private ToolStripMenuItem downloadManagerMenuItem;

	private ToolStripMenuItem currentPlayingMenuItem;

	private ToolStripMenuItem viewSourceMenuItem;

	private ToolStripSeparator toolStripSeparatorDownload2;

	private ToolStripMenuItem hideMenuItem;

	private ToolStripMenuItem exitMenuItem;

	private ToolStripMenuItem playControlMenuItem;

	private ToolStripMenuItem playPauseMenuItem;

	private ToolStripSeparator toolStripSeparator1;

	private ToolStripMenuItem playbackMenuItem;

	private ToolStripMenuItem sequentialMenuItem;

	private ToolStripMenuItem loopMenuItem;

	private ToolStripMenuItem loopOneMenuItem;

	private ToolStripMenuItem randomMenuItem;

	private ToolStripMenuItem qualityMenuItem;

	private ToolStripMenuItem outputDeviceMenuItem;

	private ToolStripMenuItem standardQualityMenuItem;

	private ToolStripMenuItem highQualityMenuItem;

	private ToolStripMenuItem losslessQualityMenuItem;

	private ToolStripMenuItem hiresQualityMenuItem;

	private ToolStripMenuItem surroundHDQualityMenuItem;

	private ToolStripMenuItem dolbyQualityMenuItem;

	private ToolStripMenuItem masterQualityMenuItem;

	private ToolStripMenuItem prevMenuItem;

	private ToolStripMenuItem nextMenuItem;

	private ToolStripMenuItem jumpToPositionMenuItem;

	private ToolStripMenuItem autoReadLyricsMenuItem;

	private ToolStripMenuItem focusFollowPlaybackMenuItem;

	private ToolStripMenuItem preventSleepDuringPlaybackMenuItem;

	private ToolStripMenuItem hideSequenceMenuItem;

	private ToolStripMenuItem hideControlBarMenuItem;

	private ToolStripMenuItem themeMenuItem;

	private ToolStripMenuItem themeGrassFreshMenuItem;

	private ToolStripMenuItem themeGrassSoftMenuItem;

	private ToolStripMenuItem themeGrassWarmMenuItem;

	private ToolStripMenuItem themeGrassMutedMenuItem;

	private ToolStripMenuItem helpMenuItem;

	private ToolStripMenuItem checkUpdateMenuItem;

	private ToolStripMenuItem donateMenuItem;

	private ToolStripMenuItem shortcutsMenuItem;

	private ToolStripMenuItem aboutMenuItem;

	private Panel searchPanel;

	private ComboBox searchTypeComboBox;

	private Label searchTypeLabel;

	private Button searchButton;

        private Button backButton;

        private SafeTextBox searchTextBox;

	private Label searchLabel;

	private bool _searchPanelPaintHooked;

	private ContextMenuStrip searchTextContextMenu;

	private ToolStripMenuItem searchCopyMenuItem;

	private ToolStripMenuItem searchCutMenuItem;

	private ToolStripMenuItem searchPasteMenuItem;

	private ToolStripMenuItem searchClearHistoryMenuItem;

	private ToolStripSeparator searchHistorySeparator;

        private System.Windows.Forms.Panel resultListPanel;
        private SafeListView resultListView;

	private ColumnHeader columnHeader0;

	private ColumnHeader columnHeader1;

	private ColumnHeader columnHeader2;

	private ColumnHeader columnHeader3;

	private ColumnHeader columnHeader4;

	private ColumnHeader columnHeader5;

	private GroupBox controlPanel;

        private Label lyricsLabel;

	private Label volumeLabel;

        private AccessibleTrackBar volumeTrackBar;

	private Label timeLabel;

        private AccessibleTrackBar progressTrackBar;

	private Button playPauseButton;

        private Label currentSongLabel;

        private StatusStrip statusStrip1;

	private ToolStripStatusLabel toolStripStatusLabel1;

	private ContextMenuStrip songContextMenu;

	private ToolStripMenuItem insertPlayMenuItem;

	private ToolStripMenuItem likeSongMenuItem;

	private ToolStripMenuItem unlikeSongMenuItem;

	private ToolStripMenuItem addToPlaylistMenuItem;

	private ToolStripMenuItem removeFromPlaylistMenuItem;

	private ToolStripMenuItem refreshMenuItem;

	private ToolStripSeparator toolStripSeparatorCollection;

	private ToolStripMenuItem subscribePlaylistMenuItem;

	private ToolStripMenuItem unsubscribePlaylistMenuItem;

	private ToolStripMenuItem deletePlaylistMenuItem;

	private ToolStripMenuItem createPlaylistMenuItem;

	private ToolStripMenuItem subscribeSongAlbumMenuItem;

	private ToolStripMenuItem subscribeSongArtistMenuItem;

	private ToolStripMenuItem subscribeAlbumMenuItem;

	private ToolStripMenuItem unsubscribeAlbumMenuItem;

	private ToolStripMenuItem subscribePodcastMenuItem;

	private ToolStripMenuItem unsubscribePodcastMenuItem;

	private ToolStripSeparator toolStripSeparatorView;

	private ToolStripMenuItem viewSongArtistMenuItem;

	private ToolStripMenuItem viewSongAlbumMenuItem;

	private ToolStripMenuItem viewPodcastMenuItem;

	private ToolStripSeparator toolStripSeparatorArtist;

	private ToolStripMenuItem shareSongMenuItem;

	private ToolStripMenuItem shareSongWebMenuItem;

	private ToolStripMenuItem shareSongDirectMenuItem;

	private ToolStripMenuItem sharePlaylistMenuItem;

	private ToolStripMenuItem shareAlbumMenuItem;

	private ToolStripMenuItem sharePodcastMenuItem;

	private ToolStripMenuItem sharePodcastEpisodeMenuItem;

	private ToolStripMenuItem sharePodcastEpisodeWebMenuItem;

	private ToolStripMenuItem sharePodcastEpisodeDirectMenuItem;

	private ToolStripMenuItem artistSongsSortMenuItem;

	private ToolStripMenuItem artistSongsSortHotMenuItem;

	private ToolStripMenuItem artistSongsSortTimeMenuItem;

	private ToolStripMenuItem artistAlbumsSortMenuItem;

	private ToolStripMenuItem artistAlbumsSortLatestMenuItem;

	private ToolStripMenuItem artistAlbumsSortOldestMenuItem;

	private ToolStripMenuItem podcastSortMenuItem;

	private ToolStripMenuItem podcastSortLatestMenuItem;

	private ToolStripMenuItem podcastSortSerialMenuItem;

	private ToolStripSeparator commentMenuSeparator;

	private ToolStripMenuItem commentMenuItem;

	private ToolStripMenuItem shareArtistMenuItem;

	private ToolStripMenuItem subscribeArtistMenuItem;

	private ToolStripMenuItem unsubscribeArtistMenuItem;

	private ToolStripSeparator toolStripSeparatorDownload3;

	private ToolStripMenuItem downloadSongMenuItem;

	private ToolStripMenuItem downloadLyricsMenuItem;

	private ToolStripMenuItem downloadPlaylistMenuItem;

	private ToolStripMenuItem downloadAlbumMenuItem;

	private ToolStripMenuItem downloadPodcastMenuItem;

	private ToolStripMenuItem batchDownloadMenuItem;

	private ToolStripMenuItem downloadCategoryMenuItem;

	private ToolStripMenuItem batchDownloadPlaylistsMenuItem;

	private ToolStripSeparator cloudMenuSeparator;

	private ToolStripMenuItem uploadToCloudMenuItem;

	private ToolStripMenuItem deleteFromCloudMenuItem;

	private ContextMenuStrip trayContextMenu;

	private ToolStripMenuItem trayShowMenuItem;

	private ToolStripMenuItem trayPlayPauseMenuItem;

	private ToolStripMenuItem trayPrevMenuItem;

	private ToolStripMenuItem trayNextMenuItem;

	private ToolStripSeparator trayMenuSeparator;

	private ToolStripMenuItem trayExitMenuItem;

	private static readonly Random _fetchRetryRandom = new Random();

	private bool IsListAutoFocusSuppressed => _autoFocusSuppressionDepth > 0;

	private void StartStateUpdateLoop()
	{
		if (_stateUpdateLoopRunning)
		{
			Debug.WriteLine("[StateCache] 状态更新循环已在运行中");
			return;
		}
		_stateUpdateCancellation?.Cancel();
		_stateUpdateCancellation?.Dispose();
		_stateUpdateCancellation = new CancellationTokenSource();
		CancellationToken cancellationToken = _stateUpdateCancellation.Token;
		_stateUpdateLoopRunning = true;
		Task.Run(async delegate
		{
			Debug.WriteLine("[StateCache] ✓ 异步状态更新循环已启动");
			try
			{
				while (!cancellationToken.IsCancellationRequested)
				{
					if (_audioEngine != null)
					{
						double position = 0.0;
						double duration = 0.0;
						PlaybackState state = PlaybackState.Stopped;
						try
						{
							position = _audioEngine.GetPosition();
							duration = _audioEngine.GetDuration();
							state = _audioEngine.GetPlaybackState();
						}
						catch (Exception ex)
						{
							Exception ex2 = ex;
							Exception ex3 = ex2;
							Debug.WriteLine("[StateCache] 获取状态异常: " + ex3.Message);
						}
						double effectivePosition = position;
						if (_positionCoordinator != null)
						{
							effectivePosition = _positionCoordinator.GetEffectivePosition(position, duration, state == PlaybackState.Playing);
						}
						PlaybackState previousState;
						bool stateChanged;
						lock (_stateCacheLock)
						{
							previousState = _cachedPlaybackState;
							_cachedPosition = effectivePosition;
							_cachedDuration = duration;
							_cachedPlaybackState = state;
							stateChanged = previousState != state;
						}

						if (stateChanged)
						{
							OnAudioEngineStateChanged(_audioEngine, state);
						}

						if (IsPlaybackActiveState(state) && DateTime.UtcNow - _lastPowerRequestRefreshUtc > TimeSpan.FromSeconds(5.0))
						{
							_lastPowerRequestRefreshUtc = DateTime.UtcNow;
							UpdatePlaybackPowerRequests(isPlaying: true);
						}
					}
					await Task.Delay(50);
				}
				if (cancellationToken.IsCancellationRequested)
				{
					Debug.WriteLine("[StateCache] 状态更新循环收到取消请求");
				}
			}
			catch (Exception ex)
			{
				Exception ex4 = ex;
				Exception ex5 = ex4;
				Debug.WriteLine("[StateCache ERROR] 状态更新循环异常: " + ex5.Message);
			}
			finally
			{
				_stateUpdateLoopRunning = false;
				Debug.WriteLine("[StateCache] 状态更新循环已停止");
			}
		});
	}

	private void StopStateUpdateLoop()
	{
		if (_stateUpdateCancellation != null)
		{
			_stateUpdateCancellation.Cancel();
			_stateUpdateCancellation.Dispose();
			_stateUpdateCancellation = null;
		}
		lock (_stateCacheLock)
		{
			_cachedPosition = 0.0;
			_cachedDuration = 0.0;
			_cachedPlaybackState = PlaybackState.Stopped;
		}
	}

	private double GetCachedPosition()
	{
		lock (_stateCacheLock)
		{
			return _cachedPosition;
		}
	}

	private double GetCachedDuration()
	{
		lock (_stateCacheLock)
		{
			return _cachedDuration;
		}
	}

	private PlaybackState GetCachedPlaybackState()
	{
		lock (_stateCacheLock)
		{
			return _cachedPlaybackState;
		}
	}

        public MainForm()
        {
                InitializeComponent();
                IconAssetProvider.TryApplyFormIcon(this);
                ApplySearchPanelStyle();
                ApplyResultListViewStyle();
                ApplyControlPanelStyle();
                UpdateThemeMenuChecks();
                ThemeManager.ApplyTheme(trayContextMenu);
                InitializeAccessibilityAnnouncementLabel();
                TtsHelper.NarratorFallbackSpeaker = SpeakNarratorAnnouncement;
                UpdateStatusStripAccessibility(toolStripStatusLabel1?.Text);
                UpdateWindowTitle(null);
                if (songContextMenu != null)
		{
			songContextMenu.ShowCheckMargin = true;
                        songContextMenu.Opened += SongContextMenu_Opened;
                        ContextMenuAccessibilityHelper.PrimeForAccessibility(songContextMenu);
		}
                if (searchTextContextMenu != null)
                {
                        searchTextContextMenu.Opened += SearchTextContextMenu_Opened;
                        ContextMenuAccessibilityHelper.PrimeForAccessibility(searchTextContextMenu);
                }
		EnsureSortMenuCheckMargins();
		InitializeServices();
		SetupEventHandlers();
		LoadConfig();
		_trayIcon = new NotifyIcon();
		_trayIcon.Icon = IconAssetProvider.GetAppIcon();
		_trayIcon.Text = "易听";
		_trayIcon.Visible = true;
		_trayIcon.MouseClick += TrayIcon_MouseClick;
		_trayIcon.DoubleClick += TrayIcon_DoubleClick;
		_contextMenuHost = new ContextMenuHost();
		trayContextMenu.Opening += TrayContextMenu_Opening;
                trayContextMenu.Opened += TrayContextMenu_Opened;
                trayContextMenu.Closed += TrayContextMenu_Closed;
                ContextMenuAccessibilityHelper.PrimeForAccessibility(trayContextMenu);
                SyncPlayPauseButtonText();
#if DEBUG
                InitializeUiThreadWatchdog();
#endif
                base.Load += MainForm_Load;
        }

        protected override AccessibleObject CreateAccessibilityInstance()
        {
                return new MainFormAccessibleObject(this);
        }

        protected override void WndProc(ref Message m)
        {
                const int WM_EXITSIZEMOVE = 0x0232;
                const int WM_POWERBROADCAST = 0x0218;
                const int WM_DEVICECHANGE = 0x0219;
                const int PBT_APMSUSPEND = 0x0004;
                const int PBT_APMRESUMESUSPEND = 0x0007;
                const int PBT_APMRESUMEAUTOMATIC = 0x0012;
                const int DBT_DEVNODES_CHANGED = 0x0007;

                if (m.Msg == WM_EXITSIZEMOVE)
                {
                        _windowLayoutPersistTimer?.Stop();
                        PersistWindowLayoutConfig();
                }
                else if (m.Msg == WM_POWERBROADCAST)
                {
                        int eventCode = unchecked((int)m.WParam.ToInt64());
                        switch (eventCode)
                        {
                                case PBT_APMSUSPEND:
                                        Debug.WriteLine("[Power] Suspend broadcast received");
                                        ClearPlaybackPowerRequests();
                                        break;
                                case PBT_APMRESUMEAUTOMATIC:
                                case PBT_APMRESUMESUSPEND:
                                        Debug.WriteLine("[Power] Resume broadcast received, reapplying playback power request");
                                        UpdatePlaybackPowerRequests(IsPlaybackActiveState(GetCachedPlaybackState()));
                                        break;
                        }
                }
                else if (m.Msg == WM_DEVICECHANGE)
                {
                        int eventCode = unchecked((int)m.WParam.ToInt64());
                        if (eventCode == DBT_DEVNODES_CHANGED)
                        {
                                Debug.WriteLine("[Power] Device topology changed");
                                UpdatePlaybackPowerRequests(IsPlaybackActiveState(GetCachedPlaybackState()));
                        }
                }

                base.WndProc(ref m);
        }

        private void ApplySearchPanelStyle()
        {
                if (searchPanel == null)
                {
                        return;
                }

                ThemePalette palette = ThemeManager.Current;
                Color panelSurface = palette.SurfaceAlt;
                Color inputSurface = palette.SurfaceBackground;
                Color textPrimary = palette.TextPrimary;
                Color border = palette.Border;
                Color hover = palette.Highlight;
                Color pressed = palette.Focus;

                searchPanel.BackColor = panelSurface;
                EnsureSearchPanelDividerPaint();

                if (searchLabel != null)
                {
                        searchLabel.ForeColor = textPrimary;
                }

                if (searchTypeLabel != null)
                {
                        searchTypeLabel.ForeColor = textPrimary;
                }

                if (searchTextBox != null)
                {
                        searchTextBox.BackColor = inputSurface;
                        searchTextBox.ForeColor = textPrimary;
                        searchTextBox.BorderStyle = BorderStyle.FixedSingle;
                        RegisterRoundedControl(searchTextBox, DefaultControlCornerRadius);
                }

                if (searchTypeComboBox != null)
                {
                        searchTypeComboBox.BackColor = inputSurface;
                        searchTypeComboBox.ForeColor = textPrimary;
                        searchTypeComboBox.FlatStyle = FlatStyle.Flat;
                        RegisterRoundedControl(searchTypeComboBox, DefaultControlCornerRadius);
                }

                if (searchButton != null)
                {
                        searchButton.BackColor = palette.Accent;
                        searchButton.ForeColor = palette.AccentText;
                        searchButton.FlatStyle = FlatStyle.Flat;
                        searchButton.FlatAppearance.BorderSize = 1;
                        searchButton.FlatAppearance.BorderColor = palette.Accent;
                        searchButton.FlatAppearance.MouseOverBackColor = palette.AccentHover;
                        searchButton.FlatAppearance.MouseDownBackColor = palette.AccentPressed;
                        RegisterRoundedControl(searchButton, DefaultControlCornerRadius);
                }

                if (backButton != null)
                {
                        backButton.BackColor = inputSurface;
                        backButton.ForeColor = textPrimary;
                        backButton.FlatStyle = FlatStyle.Flat;
                        backButton.FlatAppearance.BorderSize = 1;
                        backButton.FlatAppearance.BorderColor = border;
                        backButton.FlatAppearance.MouseOverBackColor = hover;
                        backButton.FlatAppearance.MouseDownBackColor = pressed;
                        backButton.TextAlign = ContentAlignment.MiddleCenter;
                        backButton.AccessibleName = "返回";
                        backButton.AccessibleDescription = "返回到上一层";
                        RegisterCircularControl(backButton);
                }
        }

        private void ApplyControlPanelStyle()
        {
                ThemePalette palette = ThemeManager.Current;
                if (controlPanel != null)
                {
                        controlPanel.BackColor = palette.SurfaceAlt;
                }

                if (lyricsLabel != null)
                {
                        lyricsLabel.BackColor = palette.SurfaceBackground;
                        lyricsLabel.ForeColor = palette.TextPrimary;
                }

                if (currentSongLabel != null)
                {
                        currentSongLabel.ForeColor = palette.TextPrimary;
                }

                if (timeLabel != null)
                {
                        timeLabel.ForeColor = palette.TextSecondary;
                }

                if (volumeLabel != null)
                {
                        volumeLabel.ForeColor = palette.TextSecondary;
                }

                RegisterRoundedControl(playPauseButton, DefaultControlCornerRadius);
                RegisterRoundedControl(progressTrackBar, DefaultControlCornerRadius);
                RegisterRoundedControl(volumeTrackBar, DefaultControlCornerRadius);
                RegisterRoundedControl(lyricsLabel, DefaultControlCornerRadius);
        }

        private void EnsureSearchPanelDividerPaint()
        {
                if (searchPanel == null || _searchPanelPaintHooked)
                {
                        return;
                }

                searchPanel.Paint += SearchPanel_Paint;
                _searchPanelPaintHooked = true;
        }

        private void SearchPanel_Paint(object sender, PaintEventArgs e)
        {
                if (searchPanel == null)
                {
                        return;
                }

                using (var pen = new Pen(ThemeManager.Current.Border))
                {
                        int y = searchPanel.Height - 1;
                        e.Graphics.DrawLine(pen, 0, y, searchPanel.Width, y);
                }
        }

        private void ApplyResultListViewStyle()
        {
                if (resultListView == null)
                {
                        return;
                }

                ThemePalette palette = ThemeManager.Current;
                if (resultListPanel != null)
                {
                        resultListPanel.BackColor = palette.SurfaceBackground;
                }
                resultListView.BackColor = palette.SurfaceBackground;
                resultListView.ForeColor = palette.TextPrimary;
                resultListView.BorderStyle = BorderStyle.None;
                resultListView.GridLines = false;
                RegisterRoundedControl(resultListView, DefaultControlCornerRadius);
                ScheduleResultListViewLayoutUpdate();
        }

        private void MarkListViewLayoutDataChanged()
        {
                _listViewLayoutDataVersion++;
                _lastListViewLayoutSignature = null;
        }

        private bool TryGetListViewLayoutSignature(out ListViewLayoutSignature signature)
        {
                signature = default;
                if (resultListView == null)
                {
                        return false;
                }

                int hostWidth = GetResultListViewHostWidth();
                int availableWidth = GetResultListViewAvailableWidth(hostWidth);
                bool hideSequence = _hideSequenceNumbers || IsAlwaysSequenceHiddenView();
                int itemCount = GetResultListViewItemCount();
                int rowHeight = GetListViewCurrentRowHeight();
                int fontHeight = resultListView.Font?.Height ?? 0;
                bool virtualMode = resultListView.VirtualMode;
                int virtualListSize = virtualMode ? resultListView.VirtualListSize : 0;

                signature = new ListViewLayoutSignature(
                        _listViewLayoutDataVersion,
                        _currentViewSource ?? string.Empty,
                        GetCurrentListViewDataMode(),
                        hostWidth,
                        availableWidth,
                        hideSequence,
                        itemCount,
                        virtualMode,
                        virtualListSize,
                        rowHeight,
                        fontHeight,
                        _customListViewIndexWidth,
                        _customListViewNameWidth,
                        _customListViewCreatorWidth,
                        _customListViewExtraWidth,
                        _customListViewDescriptionWidth,
                        _customListViewRowHeight,
                        _isUserResizingListViewColumns);
                return true;
        }

        private void ApplyResultListViewLayout()
        {
                if (resultListView == null || _isApplyingListViewLayout)
                {
                        return;
                }
                if (!IsResultListViewLayoutReady())
                {
                        return;
                }
                if (TryGetListViewLayoutSignature(out var signature) &&
                        _lastListViewLayoutSignature.HasValue &&
                        signature.Equals(_lastListViewLayoutSignature.Value))
                {
                        return;
                }
#if DEBUG
                TouchUiThreadMarker("ApplyListViewLayout");
#endif
                ApplyResultListViewLayoutCore();
        }

        private void ApplyResultListViewLayoutCore()
        {
                bool success = false;
                try
                {
                        _isApplyingListViewLayout = true;
                        try
                        {
                                UpdateResultListViewColumnWidths();
                                ApplyResultListViewColumnAlignment();
                                UpdateResultListViewRowHeight();
                                _listViewLayoutInitialized = true;
                                success = true;
                        }
                        catch (InvalidOperationException ex)
                        {
                                Debug.WriteLine("[ListViewLayout] 应用列布局失败: " + ex.Message);
                        }
                }
                finally
                {
                        _isApplyingListViewLayout = false;
                }

                if (success && TryGetListViewLayoutSignature(out var signature))
                {
                        _lastListViewLayoutSignature = signature;
                }
        }

        private void SetListViewVisualAdjustDeferred(bool deferred)
        {
                if (resultListView == null)
                {
                        return;
                }
                if (resultListView.DeferMultiLineLayout == deferred)
                {
                        return;
                }
                resultListView.DeferMultiLineLayout = deferred;

                if (deferred)
                {
                        _lastListViewFocusChangeUtc = DateTime.UtcNow;
                        if (_listViewLayoutDebounceTimer == null)
                        {
                                _listViewLayoutDebounceTimer = new System.Windows.Forms.Timer();
                                _listViewLayoutDebounceTimer.Tick += (_, _) => HandleListViewLayoutTimerTick();
                        }
                        ScheduleListViewLayoutTimer();
                }
        }

        private void SetListViewRedrawDeferred(bool deferred)
        {
                if (resultListView == null)
                {
                        _listViewRedrawDeferred = false;
                        return;
                }
                if (_listViewRedrawDeferred == deferred)
                {
                        return;
                }
                _listViewRedrawDeferred = deferred;
#if DEBUG
                DebugLogger.Log(DebugLogger.LogLevel.Info, "ListViewLayout",
                        $"DeferRedraw={(deferred ? "ON" : "OFF")}");
#endif
                if (deferred)
                {
                        resultListView.BeginUpdate();
                        return;
                }
                try
                {
                        resultListView.EndUpdate();
                }
                catch
                {
                }
                resultListView.Invalidate();
        }

        private bool IsListViewVisualAdjustPending()
        {
                if (resultListView == null || !resultListView.DeferMultiLineLayout)
                {
                        return false;
                }
                DateTime baseUtc = _lastListViewFocusChangeUtc;
                if (baseUtc == DateTime.MinValue)
                {
                        baseUtc = DateTime.UtcNow;
                }
                DateTime dueUtc = baseUtc.AddMilliseconds(ListViewFocusStableDelayMs);
                if (DateTime.UtcNow < dueUtc)
                {
                        return true;
                }
                SetListViewVisualAdjustDeferred(false);
                return false;
        }

        private void NotifyListViewFocusChanged()
        {
                _lastListViewFocusChangeUtc = DateTime.UtcNow;
                SetListViewVisualAdjustDeferred(true);
                if (_listViewLayoutDebounceTimer == null)
                {
                        _listViewLayoutDebounceTimer = new System.Windows.Forms.Timer();
                        _listViewLayoutDebounceTimer.Tick += (_, _) => HandleListViewLayoutTimerTick();
                }
                ScheduleListViewLayoutTimer();
        }

#if DEBUG
        private void InitializeUiThreadWatchdog()
        {
                if (_uiThreadWatchdogTimer != null)
                {
                        return;
                }
                _uiThreadWatchdogLastMarkerUtc = DateTime.UtcNow;
                _uiThreadWatchdogLastHeartbeatUtc = _uiThreadWatchdogLastMarkerUtc;
                _uiThreadWatchdogTimer = new System.Threading.Timer(_ =>
                {
                        if (IsDisposed)
                        {
                                return;
                        }
                        DateTime nowUtc = DateTime.UtcNow;
                        if (_uiThreadWatchdogLastHeartbeatUtc != DateTime.MinValue)
                        {
                                double sinceHeartbeatMs = (nowUtc - _uiThreadWatchdogLastHeartbeatUtc).TotalMilliseconds;
                                if (sinceHeartbeatMs >= UiThreadWatchdogBlockThresholdMs && !_uiThreadWatchdogInStall)
                                {
                                        _uiThreadWatchdogInStall = true;
                                        string marker = _uiThreadWatchdogLastMarker ?? "unknown";
                                        DateTime markerUtc = _uiThreadWatchdogLastMarkerUtc;
                                        DebugLogger.LogUIThreadBlock("UIWatchdog",
                                                $"UI heartbeat stalled {sinceHeartbeatMs:F0}ms lastMarker={marker} at {markerUtc:HH:mm:ss.fff}", (long)sinceHeartbeatMs);
                                        TryCaptureDiagnosticsDump("UIWatchdogHeartbeat", $"marker={marker}", (long)sinceHeartbeatMs, includeNvda: true);
                                }
                        }
                        long scheduledTicks = Stopwatch.GetTimestamp();
                        try
                        {
                                BeginInvoke(new Action<long>(HandleUiThreadWatchdogTick), scheduledTicks);
                        }
                        catch
                        {
                        }
                }, null, UiThreadWatchdogIntervalMs, UiThreadWatchdogIntervalMs);

                if (_accessibilityDiagnosticsTimer == null)
                {
                        _accessibilityDiagnosticsTimer = new System.Threading.Timer(_ =>
                        {
                                if (IsDisposed)
                                {
                                        return;
                                }
                                try
                                {
                                        CollectAccessibilityDiagnosticsPulse();
                                }
                                catch (Exception ex)
                                {
                                        DebugLogger.LogException("AccessibilityDiag", ex, "CollectPulse");
                                }
                        }, null, AccessibilityDiagPulseIntervalMs, AccessibilityDiagPulseIntervalMs);
                }
        }

        private void HandleUiThreadWatchdogTick(long scheduledTicks)
        {
                _uiThreadWatchdogLastHeartbeatUtc = DateTime.UtcNow;
                long nowTicks = Stopwatch.GetTimestamp();
                double delayMs = (nowTicks - scheduledTicks) * 1000.0 / Stopwatch.Frequency;
                if (delayMs >= UiThreadWatchdogBlockThresholdMs)
                {
                        if (!_uiThreadWatchdogInStall)
                        {
                                _uiThreadWatchdogInStall = true;
                                string marker = _uiThreadWatchdogLastMarker ?? "unknown";
                                DateTime markerUtc = _uiThreadWatchdogLastMarkerUtc;
                                DebugLogger.LogUIThreadBlock("UIWatchdog",
                                        $"UI stall {delayMs:F0}ms lastMarker={marker} at {markerUtc:HH:mm:ss.fff}", (long)delayMs);
                                TryCaptureDiagnosticsDump("UIWatchdogDelay", $"marker={marker}", (long)delayMs, includeNvda: true);
                        }
                        return;
                }
                _uiThreadWatchdogInStall = false;
        }

        private void TouchUiThreadMarker(string marker)
        {
                _uiThreadWatchdogLastMarker = marker;
                _uiThreadWatchdogLastMarkerUtc = DateTime.UtcNow;
        }

        private readonly struct ProcessResourceSample
        {
                public readonly bool IsValid;
                public readonly string Name;
                public readonly int ProcessId;
                public readonly int HandleCount;
                public readonly int ThreadCount;
                public readonly int UserObjects;
                public readonly int GdiObjects;
                public readonly long PrivateMemoryMb;
                public readonly long WorkingSetMb;

                public ProcessResourceSample(bool isValid, string name, int processId, int handleCount, int threadCount, int userObjects, int gdiObjects, long privateMemoryMb, long workingSetMb)
                {
                        IsValid = isValid;
                        Name = name ?? string.Empty;
                        ProcessId = processId;
                        HandleCount = handleCount;
                        ThreadCount = threadCount;
                        UserObjects = userObjects;
                        GdiObjects = gdiObjects;
                        PrivateMemoryMb = privateMemoryMb;
                        WorkingSetMb = workingSetMb;
                }

                public override string ToString()
                {
                        if (!IsValid)
                        {
                                return "n/a";
                        }
                        return $"{Name}(pid={ProcessId}) handle={HandleCount} threads={ThreadCount} user={UserObjects} gdi={GdiObjects} privMB={PrivateMemoryMb} wsMB={WorkingSetMb}";
                }
        }

        [Flags]
        private enum MiniDumpType : uint
        {
                Normal = 0x00000000,
                WithDataSegs = 0x00000001,
                WithHandleData = 0x00000004,
                WithUnloadedModules = 0x00000020,
                WithProcessThreadData = 0x00000100,
                WithThreadInfo = 0x00001000,
                IgnoreInaccessibleMemory = 0x00020000
        }

        private static class DiagnosticsNativeMethods
        {
                [DllImport("user32.dll")]
                public static extern int GetGuiResources(IntPtr hProcess, int uiFlags);

                [DllImport("Dbghelp.dll", SetLastError = true)]
                public static extern bool MiniDumpWriteDump(
                        IntPtr hProcess,
                        int processId,
                        IntPtr hFile,
                        uint dumpType,
                        IntPtr exceptionParam,
                        IntPtr userStreamParam,
                        IntPtr callbackParam);
        }

        private void CollectAccessibilityDiagnosticsPulse()
        {
                long notifyCalls = Interlocked.Read(ref _accessNotifyClientCallCount);
                long notifyWinEventCalls = Interlocked.Read(ref _accessNotifyWinEventCallCount);
                long setPropertyCalls = Interlocked.Read(ref _accessSetPropertyCallCount);
                long selectionChangedCalls = Interlocked.Read(ref _accessSelectionChangedCount);
                long selectionApplyCalls = Interlocked.Read(ref _accessSelectionApplyCount);
                long suppressedNotifyCalls = Interlocked.Read(ref _accessBurstSuppressedNotifyCount);
                long burstNavFlushRemovedCount = Interlocked.Read(ref _accessBurstNavFlushRemovedCount);
                long nvdaNavSuppressedCalls = Interlocked.Read(ref _accessNvdaNavKeySuppressedCount);
                long nvdaNavAppliedSteps = Interlocked.Read(ref _accessNvdaNavAppliedStepCount);

                long deltaNotifyCalls = notifyCalls - _diagLastNotifyClientCallCount;
                long deltaNotifyWinEventCalls = notifyWinEventCalls - _diagLastNotifyWinEventCallCount;
                long deltaSetPropertyCalls = setPropertyCalls - _diagLastSetPropertyCallCount;
                long deltaSelectionChangedCalls = selectionChangedCalls - _diagLastSelectionChangedCount;
                long deltaSelectionApplyCalls = selectionApplyCalls - _diagLastSelectionApplyCount;
                long deltaSuppressedNotifyCalls = suppressedNotifyCalls - _diagLastBurstSuppressedNotifyCount;
                long deltaBurstNavFlushRemovedCount = burstNavFlushRemovedCount - _diagLastBurstNavFlushRemovedCount;
                long deltaNvdaNavSuppressedCalls = nvdaNavSuppressedCalls - _diagLastNvdaNavKeySuppressedCount;
                long deltaNvdaNavAppliedSteps = nvdaNavAppliedSteps - _diagLastNvdaNavAppliedStepCount;

                _diagLastNotifyClientCallCount = notifyCalls;
                _diagLastNotifyWinEventCallCount = notifyWinEventCalls;
                _diagLastSetPropertyCallCount = setPropertyCalls;
                _diagLastSelectionChangedCount = selectionChangedCalls;
                _diagLastSelectionApplyCount = selectionApplyCalls;
                _diagLastBurstSuppressedNotifyCount = suppressedNotifyCalls;
                _diagLastBurstNavFlushRemovedCount = burstNavFlushRemovedCount;
                _diagLastNvdaNavKeySuppressedCount = nvdaNavSuppressedCalls;
                _diagLastNvdaNavAppliedStepCount = nvdaNavAppliedSteps;

                bool interesting = _listViewSelectionBurstActive || _uiThreadWatchdogInStall ||
                        deltaNotifyCalls >= 60 || deltaNotifyWinEventCalls >= 120 || deltaSetPropertyCalls >= 50 ||
                        deltaSelectionChangedCalls >= 80 || deltaSelectionApplyCalls >= 20 ||
                        deltaSuppressedNotifyCalls > 0 || deltaBurstNavFlushRemovedCount > 0 ||
                        deltaNvdaNavSuppressedCalls > 0 || deltaNvdaNavAppliedSteps > 0;
                if (!interesting)
                {
                        return;
                }

                bool captureResourceSnapshot = _uiThreadWatchdogInStall || !_listViewSelectionBurstActive;
                ProcessResourceSample self = captureResourceSnapshot ? CaptureCurrentProcessResourceSample() : default;
                ProcessResourceSample nvda = default;
                bool nvdaProbeError = false;
                bool shouldProbeNvda = captureResourceSnapshot && _isNvdaRunningCached &&
                        (_nvdaResourceProbeBlockedUntilUtc == DateTime.MinValue || DateTime.UtcNow >= _nvdaResourceProbeBlockedUntilUtc);
                if (shouldProbeNvda)
                {
                        nvda = CaptureFirstProcessResourceSample("nvda", out nvdaProbeError);
                        if (nvdaProbeError)
                        {
                                _nvdaResourceProbeConsecutiveFailures++;
                                if (_nvdaResourceProbeConsecutiveFailures >= 3)
                                {
                                        _nvdaResourceProbeBlockedUntilUtc = DateTime.UtcNow.AddSeconds(NvdaResourceProbeFailureBackoffSeconds);
                                }
                        }
                        else
                        {
                                _nvdaResourceProbeConsecutiveFailures = 0;
                                _nvdaResourceProbeBlockedUntilUtc = DateTime.MinValue;
                        }
                }

                string marker = _uiThreadWatchdogLastMarker ?? "unknown";
                string nvdaProbeState = shouldProbeNvda
                        ? (nvdaProbeError ? "error" : "on")
                        : (captureResourceSnapshot
                                ? ((_nvdaResourceProbeBlockedUntilUtc != DateTime.MinValue && DateTime.UtcNow < _nvdaResourceProbeBlockedUntilUtc) ? "backoff" : "skip")
                                : "off");
                DebugLogger.Log(DebugLogger.LogLevel.Info, "AccessibilityDiag",
                        $"Pulse burst={_listViewSelectionBurstActive} stall={_uiThreadWatchdogInStall} marker={marker} " +
                        $"delta[notify={deltaNotifyCalls}, winEvent={deltaNotifyWinEventCalls}, setProp={deltaSetPropertyCalls}, selChanged={deltaSelectionChangedCalls}, selApply={deltaSelectionApplyCalls}, suppressed={deltaSuppressedNotifyCalls}, flushNav={deltaBurstNavFlushRemovedCount}, nvdaDrop={deltaNvdaNavSuppressedCalls}, nvdaStep={deltaNvdaNavAppliedSteps}] " +
                        $"probeNvda={nvdaProbeState} self={self} nvda={nvda}");

                if (self.IsValid &&
                        (self.HandleCount >= DiagnosticsHandleDumpThreshold ||
                         self.UserObjects >= DiagnosticsUserObjectDumpThreshold ||
                         self.GdiObjects >= DiagnosticsGdiObjectDumpThreshold))
                {
                        TryCaptureDiagnosticsDump("ResourceThreshold", self.ToString(), 0L, includeNvda: true);
                }
                if (nvda.IsValid &&
                        (nvda.HandleCount >= DiagnosticsHandleDumpThreshold ||
                         nvda.UserObjects >= DiagnosticsUserObjectDumpThreshold ||
                         nvda.GdiObjects >= DiagnosticsGdiObjectDumpThreshold))
                {
                        TryCaptureDiagnosticsDump("NvdaResourceThreshold", nvda.ToString(), 0L, includeNvda: true);
                }
        }

        private ProcessResourceSample CaptureCurrentProcessResourceSample()
        {
                using (Process current = Process.GetCurrentProcess())
                {
                        return CaptureProcessResourceSample(current, "YTPlayer");
                }
        }

        private ProcessResourceSample CaptureFirstProcessResourceSample(string processName, out bool hadError)
        {
                hadError = false;
                if (string.IsNullOrWhiteSpace(processName))
                {
                        return default;
                }

                Process[] processes;
                try
                {
                        processes = Process.GetProcessesByName(processName);
                }
                catch
                {
                        hadError = true;
                        return default;
                }

                if (processes == null || processes.Length == 0)
                {
                        return default;
                }

                bool encounteredError = false;
                ProcessResourceSample sample = default;
                foreach (Process process in processes)
                {
                        try
                        {
                                sample = CaptureProcessResourceSample(process, processName);
                                if (sample.IsValid)
                                {
                                        break;
                                }
                        }
                        catch
                        {
                                encounteredError = true;
                        }
                        finally
                        {
                                process.Dispose();
                        }
                }
                hadError = !sample.IsValid && encounteredError;
                return sample;
        }

        private static ProcessResourceSample CaptureProcessResourceSample(Process process, string fallbackName)
        {
                if (process == null)
                {
                        return default;
                }

                try
                {
                        process.Refresh();
                        IntPtr handle = process.Handle;
                        if (handle == IntPtr.Zero)
                        {
                                return default;
                        }

                        int gdiCount = DiagnosticsNativeMethods.GetGuiResources(handle, 0);
                        int userCount = DiagnosticsNativeMethods.GetGuiResources(handle, 1);
                        string name = string.IsNullOrWhiteSpace(process.ProcessName) ? (fallbackName ?? string.Empty) : process.ProcessName;
                        int threadCount = 0;
                        try
                        {
                                threadCount = process.Threads?.Count ?? 0;
                        }
                        catch
                        {
                                threadCount = 0;
                        }

                        long privateMb = process.PrivateMemorySize64 / (1024L * 1024L);
                        long workingMb = process.WorkingSet64 / (1024L * 1024L);
                        return new ProcessResourceSample(true, name, process.Id, process.HandleCount, threadCount, userCount, gdiCount, privateMb, workingMb);
                }
                catch
                {
                        return default;
                }
        }

        private void TryCaptureDiagnosticsDump(string reason, string detail, long metricMs, bool includeNvda)
        {
                DateTime nowUtc = DateTime.UtcNow;
                if (_lastDiagnosticsDumpUtc != DateTime.MinValue &&
                        (nowUtc - _lastDiagnosticsDumpUtc).TotalSeconds < DiagnosticsDumpCooldownSeconds)
                {
                        return;
                }

                _lastDiagnosticsDumpUtc = nowUtc;
                string marker = _uiThreadWatchdogLastMarker ?? "unknown";
                DebugLogger.Log(DebugLogger.LogLevel.Warning, "DiagnosticsDump",
                        $"Capture requested reason={reason}, metric={metricMs}ms, marker={marker}, detail={detail}");

                try
                {
                        string dumpDirectory = Path.Combine(PathHelper.ApplicationRootDirectory, "Logs", "Dumps");
                        Directory.CreateDirectory(dumpDirectory);
                        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");

                        using (Process current = Process.GetCurrentProcess())
                        {
                                string selfDumpPath = Path.Combine(dumpDirectory, $"YTPlayer_{reason}_{timestamp}_{current.Id}.dmp");
                                if (TryWriteMiniDump(current, selfDumpPath, out int selfError))
                                {
                                        DebugLogger.Log(DebugLogger.LogLevel.Warning, "DiagnosticsDump", $"Self dump created: {selfDumpPath}");
                                }
                                else
                                {
                                        DebugLogger.Log(DebugLogger.LogLevel.Error, "DiagnosticsDump", $"Self dump failed: error={selfError}");
                                }
                        }

                        if (!includeNvda)
                        {
                                return;
                        }

                        Process[] nvdaProcesses = Process.GetProcessesByName("nvda");
                        foreach (Process process in nvdaProcesses)
                        {
                                try
                                {
                                        string nvdaDumpPath = Path.Combine(dumpDirectory, $"NVDA_{reason}_{timestamp}_{process.Id}.dmp");
                                        if (TryWriteMiniDump(process, nvdaDumpPath, out int nvdaError))
                                        {
                                                DebugLogger.Log(DebugLogger.LogLevel.Warning, "DiagnosticsDump", $"NVDA dump created: {nvdaDumpPath}");
                                        }
                                        else
                                        {
                                                DebugLogger.Log(DebugLogger.LogLevel.Warning, "DiagnosticsDump", $"NVDA dump failed(pid={process.Id}): error={nvdaError}");
                                        }
                                }
                                catch (Exception ex)
                                {
                                        DebugLogger.LogException("DiagnosticsDump", ex, $"NVDA dump exception pid={process.Id}");
                                }
                                finally
                                {
                                        process.Dispose();
                                }
                        }
                }
                catch (Exception ex)
                {
                        DebugLogger.LogException("DiagnosticsDump", ex, $"reason={reason}, detail={detail}");
                }
        }

        private static bool TryWriteMiniDump(Process process, string dumpPath, out int errorCode)
        {
                errorCode = 0;
                if (process == null || string.IsNullOrWhiteSpace(dumpPath))
                {
                        return false;
                }

                try
                {
                        using (FileStream stream = new FileStream(dumpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                                MiniDumpType dumpType = MiniDumpType.WithHandleData |
                                        MiniDumpType.WithUnloadedModules |
                                        MiniDumpType.WithProcessThreadData |
                                        MiniDumpType.WithThreadInfo |
                                        MiniDumpType.IgnoreInaccessibleMemory;
                                bool success = DiagnosticsNativeMethods.MiniDumpWriteDump(
                                        process.Handle,
                                        process.Id,
                                        stream.SafeFileHandle.DangerousGetHandle(),
                                        (uint)dumpType,
                                        IntPtr.Zero,
                                        IntPtr.Zero,
                                        IntPtr.Zero);
                                if (!success)
                                {
                                        errorCode = Marshal.GetLastWin32Error();
                                }
                                return success;
                        }
                }
                catch
                {
                        errorCode = Marshal.GetLastWin32Error();
                        return false;
                }
        }
#endif

        private void RecordAccessibilityNotifyCall(bool suppressed = false)
        {
#if DEBUG
                Interlocked.Increment(ref _accessNotifyClientCallCount);
                if (suppressed)
                {
                        Interlocked.Increment(ref _accessBurstSuppressedNotifyCount);
                }
#endif
        }

        private void RecordAccessibilityWinEventCall(int count = 1)
        {
#if DEBUG
                if (count <= 0)
                {
                        return;
                }
                Interlocked.Add(ref _accessNotifyWinEventCallCount, count);
#endif
        }

        private void RecordAccessibilitySetPropertyCall()
        {
#if DEBUG
                Interlocked.Increment(ref _accessSetPropertyCallCount);
#endif
        }

        private void RecordAccessibilitySelectionChangedCall()
        {
#if DEBUG
                Interlocked.Increment(ref _accessSelectionChangedCount);
#endif
        }

        private void RecordAccessibilitySelectionApplyCall()
        {
#if DEBUG
                Interlocked.Increment(ref _accessSelectionApplyCount);
#endif
        }

        private void RecordAccessibilityNavigationFlushCall(int removedCount)
        {
#if DEBUG
                if (removedCount <= 0)
                {
                        return;
                }
                Interlocked.Add(ref _accessBurstNavFlushRemovedCount, removedCount);
#endif
        }

        private void RecordAccessibilityNvdaNavSuppressedCall()
        {
#if DEBUG
                Interlocked.Increment(ref _accessNvdaNavKeySuppressedCount);
#endif
        }

        private void RecordAccessibilityNvdaNavAppliedStepCall(int stepCount)
        {
#if DEBUG
                if (stepCount <= 0)
                {
                        return;
                }
                Interlocked.Add(ref _accessNvdaNavAppliedStepCount, stepCount);
#endif
        }

        private bool ShouldThrottleBurstListViewAccessibilityEvent(Control control, AccessibleEvents accEvent)
        {
#if DEBUG
                if (!_listViewSelectionBurstActive || control == null || !ReferenceEquals(control, resultListView))
                {
                        return false;
                }
                if (accEvent != AccessibleEvents.Focus && accEvent != AccessibleEvents.Selection &&
                        accEvent != AccessibleEvents.SelectionAdd && accEvent != AccessibleEvents.NameChange)
                {
                        return false;
                }
                DateTime nowUtc = DateTime.UtcNow;
                if (_lastBurstListViewNotifyUtc != DateTime.MinValue &&
                        (nowUtc - _lastBurstListViewNotifyUtc).TotalMilliseconds < AccessibilityBurstNotifyThrottleMs)
                {
                        return true;
                }
                _lastBurstListViewNotifyUtc = nowUtc;
#endif
                return false;
        }

        private void TryCaptureDiagnosticsDumpOnAccessibilityError(string operation, Exception ex)
        {
#if DEBUG
                if (ex == null)
                {
                        return;
                }
                Exception current = ex;
                while (current != null)
                {
                        if (current is Win32Exception win32Exception && win32Exception.NativeErrorCode == 1816)
                        {
                                TryCaptureDiagnosticsDump("Win32Quota", operation, 0L, includeNvda: true);
                                return;
                        }
                        if (current.HResult == unchecked((int)0x80070718) ||
                                (current.Message?.IndexOf("Not enough quota", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0)
                        {
                                TryCaptureDiagnosticsDump("QuotaMessage", operation, 0L, includeNvda: true);
                                return;
                        }
                        current = current.InnerException;
                }
#endif
        }

        private void ScheduleResultListViewLayoutUpdate()
        {
                if (resultListView == null)
                {
                        return;
                }

                _listViewLayoutPending = true;
                _lastListViewLayoutRequestUtc = DateTime.UtcNow;

                if (_listViewLayoutDebounceTimer == null)
                {
                        _listViewLayoutDebounceTimer = new System.Windows.Forms.Timer();
                        _listViewLayoutDebounceTimer.Tick += (_, _) => HandleListViewLayoutTimerTick();
                }

                ScheduleListViewLayoutTimer();
        }

        private void HandleListViewLayoutTimerTick()
        {
                if (_listViewLayoutDebounceTimer != null)
                {
                        _listViewLayoutDebounceTimer.Stop();
                }
#if DEBUG
                TouchUiThreadMarker("ListViewLayoutTimerTick");
#endif

                bool visualPending = IsListViewVisualAdjustPending();
                if (!_listViewLayoutPending)
                {
                        if (visualPending)
                        {
                                ScheduleListViewLayoutTimer();
                        }
                        return;
                }

                if (_isApplyingListViewLayout || !IsResultListViewLayoutReady())
                {
                        ScheduleListViewLayoutTimer(ListViewFocusStableDelayMs);
                        return;
                }

                DateTime dueUtc = GetListViewLayoutDueUtc();
                if (DateTime.UtcNow < dueUtc)
                {
                        ScheduleListViewLayoutTimer();
                        return;
                }

                _listViewLayoutPending = false;
                ApplyResultListViewLayout();
                visualPending = IsListViewVisualAdjustPending();

                if (_listViewLayoutPending || visualPending)
                {
                        ScheduleListViewLayoutTimer();
                }
        }

        private void ScheduleListViewLayoutTimer(int? overrideDelayMs = null)
        {
                if (_listViewLayoutDebounceTimer == null)
                {
                        return;
                }

                int delayMs;
                if (overrideDelayMs.HasValue)
                {
                        delayMs = overrideDelayMs.Value;
                }
                else
                {
                        DateTime dueUtc = GetListViewLayoutDueUtc();
                        delayMs = (int)Math.Max(0, (dueUtc - DateTime.UtcNow).TotalMilliseconds);
                }
                delayMs = Math.Max(1, delayMs);
                _listViewLayoutDebounceTimer.Interval = delayMs;
                _listViewLayoutDebounceTimer.Stop();
                _listViewLayoutDebounceTimer.Start();
        }

        private DateTime GetListViewLayoutDueUtc()
        {
                DateTime baseUtc = _lastListViewLayoutRequestUtc;
                if (_lastListViewFocusChangeUtc > baseUtc)
                {
                        baseUtc = _lastListViewFocusChangeUtc;
                }
                if (baseUtc == DateTime.MinValue)
                {
                        baseUtc = DateTime.UtcNow;
                }
                return baseUtc.AddMilliseconds(ListViewFocusStableDelayMs);
        }

        private bool IsResultListViewLayoutReady()
        {
                if (resultListView == null || !resultListView.IsHandleCreated)
                {
                        return false;
                }
                if (resultListView.View != View.Details)
                {
                        return false;
                }
                if (resultListView.Columns.Count < ListViewTotalColumnCount)
                {
                        return false;
                }
                if (columnHeader0.Index < 0 || columnHeader1.Index < 0 || columnHeader2.Index < 0 || columnHeader3.Index < 0 || columnHeader4.Index < 0 || columnHeader5.Index < 0)
                {
                        return false;
                }
                return true;
        }

        private void ApplyResultListViewColumnAlignment()
        {
                if (resultListView == null || resultListView.View != View.Details)
                {
                        return;
                }
                ListViewColumnRole[] roles = ResolveResultListViewColumnRoles();
                int shortInfoColumnIndex = _listViewShortInfoColumnIndex;
                if (shortInfoColumnIndex <= 0)
                {
                        shortInfoColumnIndex = Array.IndexOf(roles, ListViewColumnRole.ShortInfo) + 1;
                }
                if (shortInfoColumnIndex > 0)
                {
                        NormalizeShortInfoRole(roles, shortInfoColumnIndex);
                }
                columnHeader0.TextAlign = HorizontalAlignment.Left;
                columnHeader1.TextAlign = ResolveColumnAlignment(1, roles[0]);
                columnHeader2.TextAlign = ResolveColumnAlignment(2, roles[1]);
                columnHeader3.TextAlign = ResolveColumnAlignment(3, roles[2]);
                columnHeader4.TextAlign = ResolveColumnAlignment(4, roles[3]);
                columnHeader5.TextAlign = ResolveColumnAlignment(5, roles[4]);
#if DEBUG
                DebugLogger.Log(DebugLogger.LogLevel.Info, "ListViewAlign",
                        $"Apply alignment: viewSource={_currentViewSource ?? ""} " +
                        $"[0]={columnHeader0.TextAlign},[1]={columnHeader1.TextAlign},[2]={columnHeader2.TextAlign}," +
                        $"[3]={columnHeader3.TextAlign},[4]={columnHeader4.TextAlign},[5]={columnHeader5.TextAlign}");
#endif
        }

        private static HorizontalAlignment ResolveColumnAlignment(int columnIndex, ListViewColumnRole role)
        {
                return HorizontalAlignment.Left;
        }

        private void UpdateResultListViewColumnWidths()
        {
                if (resultListView == null)
                {
                        return;
                }

                int hostWidth = GetResultListViewHostWidth();
                int availableWidth = GetResultListViewAvailableWidth(hostWidth);
                if (availableWidth <= 0)
                {
                        DebugListViewLayout("Skip: availableWidth <= 0");
                        return;
                }

#if DEBUG
                Stopwatch layoutSw = Stopwatch.StartNew();
                int layoutRowCount = -1;
#endif
                bool hideSequence = _hideSequenceNumbers || IsAlwaysSequenceHiddenView();
                int totalItemCount = GetResultListViewItemCount();
                if (ShouldUseResultListViewLayoutFastPath(totalItemCount))
                {
                        ApplyResultListViewAutoShrinkLayout(availableWidth, desiredContentWidth: 0, allowAutoShrink: false);
                        int[] fastWidths = BuildFallbackColumnWidths(availableWidth, hideSequence);
                        ApplyResultListViewColumnWidths(fastWidths);
                        DebugListViewLayout($"FastPath: rows={totalItemCount}, availableWidth={availableWidth}, hideSequence={hideSequence}, widths=[{string.Join(",", fastWidths)}]");
#if DEBUG
                        layoutRowCount = totalItemCount;
                        TouchUiThreadMarker($"UpdateColumnWidthsFastPath rows={layoutRowCount}");
                        if (layoutSw != null)
                        {
                                layoutSw.Stop();
                                long layoutMs = layoutSw.ElapsedMilliseconds;
                                if (layoutRowCount >= 0 && layoutMs >= 200)
                                {
                                        DebugLogger.LogUIThreadBlock("ListViewLayout",
                                                $"UpdateColumnWidthsFastPath rows={layoutRowCount} availableWidth={availableWidth}", layoutMs);
                                }
                        }
#endif
                        return;
                }

                List<ListViewRowSnapshot> rows = EnumerateResultListViewRows().ToList();
#if DEBUG
                layoutRowCount = rows.Count;
                TouchUiThreadMarker($"UpdateColumnWidths rows={layoutRowCount}");
#endif
                DebugListViewLayout($"Start: viewSource={_currentViewSource ?? ""}, mode={GetCurrentListViewDataMode()}, rows={rows.Count}, hostWidth={hostWidth}, availableWidth={availableWidth}, hideSequence={hideSequence}");
                ListViewColumnRole[] roles = ResolveResultListViewColumnRoles();

                bool[] hasContent = new bool[5];
                foreach (ListViewRowSnapshot row in rows)
                {
                        if (!hasContent[0] && !string.IsNullOrWhiteSpace(row.IndexText))
                        {
                                hasContent[0] = true;
                        }
                        if (!hasContent[1] && !string.IsNullOrWhiteSpace(row.Column2))
                        {
                                hasContent[1] = true;
                        }
                        if (!hasContent[2] && !string.IsNullOrWhiteSpace(row.Column3))
                        {
                                hasContent[2] = true;
                        }
                        if (!hasContent[3] && !string.IsNullOrWhiteSpace(row.Column4))
                        {
                                hasContent[3] = true;
                        }
                        if (!hasContent[4] && !string.IsNullOrWhiteSpace(row.Column5))
                        {
                                hasContent[4] = true;
                        }
                }

                int minFlexWidth = CalculateThreeCharColumnWidth();
                ColumnAutoSizeStats[] autoStats = BuildColumnAutoSizeStats(rows, ListViewAutoWidthSampleLimit);
                int[] maxSingleLineWidths = BuildMaxSingleLineWidths(autoStats, minFlexWidth);
                int[] maxCountSummaryWidths = BuildMaxCountSummaryWidths(autoStats, minFlexWidth);
                int shortInfoColumnIndex = ResolveShortInfoColumnIndex(roles, autoStats, maxCountSummaryWidths);
                NormalizeShortInfoRole(roles, shortInfoColumnIndex);
                _listViewShortInfoColumnIndex = shortInfoColumnIndex;
                UpdateResultListViewLineSettings(shortInfoColumnIndex);
                DebugListViewLayout($"Roles: [{string.Join(",", roles.Select(r => r.ToString()))}], shortInfo={shortInfoColumnIndex}");

                bool[] hasCustom = new bool[5]
                {
                        _customListViewIndexWidth.HasValue,
                        _customListViewNameWidth.HasValue,
                        _customListViewCreatorWidth.HasValue,
                        _customListViewExtraWidth.HasValue,
                        _customListViewDescriptionWidth.HasValue
                };
                bool hasCustomAny = hasCustom.Any(v => v);

                bool[] hide = new bool[5];
                bool allowAutoHide = rows.Count > 0;
                for (int i = 0; i < hide.Length; i++)
                {
                        bool roleHidden = roles[i] == ListViewColumnRole.Hidden;
                        bool emptyColumn = allowAutoHide && !hasContent[i];
                        if (i == 0)
                        {
                                hide[i] = hideSequence || emptyColumn || (!hasCustom[i] && roleHidden);
                        }
                        else
                        {
                                hide[i] = emptyColumn || (!hasCustom[i] && roleHidden);
                        }
                }
                DebugListViewLayout($"hasContent=[{string.Join(",", hasContent.Select(v => v ? "1" : "0"))}], hasCustom=[{string.Join(",", hasCustom.Select(v => v ? "1" : "0"))}], hide=[{string.Join(",", hide.Select(v => v ? "1" : "0"))}]");
                bool allHidden = true;
                for (int i = 0; i < hide.Length; i++)
                {
                        if (!hide[i])
                        {
                                allHidden = false;
                                break;
                        }
                }
                if (allHidden)
                {
                        hide[1] = false;
                }

                int desiredContentWidth = CalculateDesiredListViewContentWidth(hideSequence, hide, roles, shortInfoColumnIndex, minFlexWidth, maxSingleLineWidths, maxCountSummaryWidths);
                bool allowAutoShrink = rows.Count > 0 && !hasCustomAny && !_isUserResizingListViewColumns;
                int layoutAvailableWidth = ApplyResultListViewAutoShrinkLayout(availableWidth, desiredContentWidth, allowAutoShrink);
                if (layoutAvailableWidth <= 0)
                {
                        DebugListViewLayout("Skip: layoutAvailableWidth <= 0");
                        return;
                }
                availableWidth = layoutAvailableWidth;

                int[] widths = new int[5];
                int[] minFixedWidths = new int[5];
                bool[] isFixed = new bool[5];
                int totalFixed = 0;
                List<int> flexColumns = new List<int>();

                for (int i = 0; i < widths.Length; i++)
                {
                        int columnIndex = i + 1;
                        if (hide[i])
                        {
                                widths[i] = 0;
                                continue;
                        }

                        int? customWidth = GetCustomListViewColumnWidth(columnIndex);
                        if (columnIndex == 1)
                        {
                                int seqWidth = hideSequence ? 0 : CalculateSequenceColumnWidth();
                                if (customWidth.HasValue)
                                {
                                        seqWidth = Math.Max(seqWidth, customWidth.Value);
                                }
                                widths[i] = seqWidth;
                                isFixed[i] = true;
                                minFixedWidths[i] = seqWidth;
                                totalFixed += widths[i];
                                continue;
                        }

                        if (columnIndex == shortInfoColumnIndex || roles[i] == ListViewColumnRole.ShortInfo)
                        {
                                int shortInfoWidth = maxCountSummaryWidths[i] > 0 ? maxCountSummaryWidths[i] : maxSingleLineWidths[i];
                                int targetWidth = Math.Max(minFlexWidth, shortInfoWidth);
                                if (customWidth.HasValue)
                                {
                                        targetWidth = Math.Max(targetWidth, customWidth.Value);
                                }
                                widths[i] = targetWidth;
                                isFixed[i] = true;
                                minFixedWidths[i] = targetWidth;
                                totalFixed += widths[i];
                                continue;
                        }

                        if (customWidth.HasValue)
                        {
                                widths[i] = Math.Max(0, customWidth.Value);
                                isFixed[i] = true;
                                minFixedWidths[i] = Math.Min(widths[i], minFlexWidth);
                                totalFixed += widths[i];
                                continue;
                        }

                        if (roles[i] == ListViewColumnRole.Hidden)
                        {
                                widths[i] = 0;
                                continue;
                        }

                        flexColumns.Add(columnIndex);
                }
                DebugListViewLayout($"FixedWidths=[{string.Join(",", widths)}], totalFixed={totalFixed}, flexColumns=[{string.Join(",", flexColumns)}]");

                int minFlexSum = flexColumns.Count * minFlexWidth;
                int maxFixedTotal = Math.Max(0, availableWidth - minFlexSum);
                if (totalFixed > maxFixedTotal)
                {
                        int over = totalFixed - maxFixedTotal;
                        int totalExtra = 0;
                        int[] extras = new int[5];
                        for (int i = 0; i < widths.Length; i++)
                        {
                                if (!isFixed[i])
                                {
                                        continue;
                                }
                                int extra = widths[i] - minFixedWidths[i];
                                if (extra > 0)
                                {
                                        extras[i] = extra;
                                        totalExtra += extra;
                                }
                        }

                        if (totalExtra > 0)
                        {
                                int remaining = over;
                                for (int i = 0; i < widths.Length; i++)
                                {
                                        if (extras[i] <= 0)
                                        {
                                                continue;
                                        }
                                        int reduce = (int)Math.Floor((double)over * extras[i] / totalExtra);
                                        if (reduce <= 0)
                                        {
                                                continue;
                                        }
                                        int current = widths[i];
                                        int next = Math.Max(minFixedWidths[i], current - reduce);
                                        int actualReduce = current - next;
                                        if (actualReduce <= 0)
                                        {
                                                continue;
                                        }
                                        widths[i] = next;
                                        remaining -= actualReduce;
                                }

                                if (remaining > 0)
                                {
                                        int guard = remaining + widths.Length;
                                        while (remaining > 0 && guard-- > 0)
                                        {
                                                bool reduced = false;
                                                for (int i = 0; i < widths.Length && remaining > 0; i++)
                                                {
                                                        if (extras[i] > 0 && widths[i] > minFixedWidths[i])
                                                        {
                                                                widths[i]--;
                                                                remaining--;
                                                                reduced = true;
                                                        }
                                                }
                                                if (!reduced)
                                                {
                                                        break;
                                                }
                                        }
                                }

                                totalFixed = 0;
                                for (int i = 0; i < widths.Length; i++)
                                {
                                        if (isFixed[i])
                                        {
                                                totalFixed += widths[i];
                                        }
                                }
                                DebugListViewLayout($"Fixed: clamped over={over}, maxFixed={maxFixedTotal}, totalFixed={totalFixed}");
                        }
                        else
                        {
                                DebugListViewLayout($"Fixed: cannot clamp over={over}, maxFixed={maxFixedTotal}");
                        }
                }

                if (flexColumns.Count > 0)
                {
                        int flexWidth = availableWidth - totalFixed;
                        minFlexSum = flexColumns.Count * minFlexWidth;
                        DebugListViewLayout($"Flex: flexWidth={flexWidth}, minFlexWidth={minFlexWidth}, minFlexSum={minFlexSum}");
                        bool appliedOptimizedPlaylists = false;
                        if (rows.Count > 0 &&
                                !hasCustomAny &&
                                !_isUserResizingListViewColumns &&
                                GetCurrentListViewDataMode() == ListViewDataMode.Playlists)
                        {
                                appliedOptimizedPlaylists = TryApplyPlaylistsFlexLayout(widths, hide, roles, flexColumns, flexWidth, minFlexWidth, maxSingleLineWidths, autoStats);
                        }

                        if (appliedOptimizedPlaylists)
                        {
                                DebugListViewLayout("Flex: playlists optimized");
                        }
                        else if (flexWidth <= 0 || flexWidth < minFlexSum)
                        {
                                foreach (int columnIndex in flexColumns)
                                {
                                        widths[columnIndex - 1] = minFlexWidth;
                                }
                                DebugListViewLayout("Flex: fallback to min width for all flex columns");
                        }
                        else
                        {
                                int[] required = new int[5];
                                int requiredSum = 0;
                                foreach (int columnIndex in flexColumns)
                                {
                                        int requiredWidth = Math.Max(minFlexWidth, maxSingleLineWidths[columnIndex - 1]);
                                        required[columnIndex - 1] = requiredWidth;
                                        requiredSum += requiredWidth;
                                }

                                if (requiredSum <= flexWidth)
                                {
                                        foreach (int columnIndex in flexColumns)
                                        {
                                                widths[columnIndex - 1] = required[columnIndex - 1];
                                        }
                                        int slack = flexWidth - requiredSum;
                                        if (slack > 0)
                                        {
                                                DistributeSlack(widths, flexColumns, roles, slack);
                                        }
                                        DebugListViewLayout("Flex: used single-line widths");
                                }
                                else
                                {
                                        int extra = Math.Max(0, flexWidth - minFlexSum);
                                        double weightSum = 0.0;
                                        double[] weights = new double[flexColumns.Count];
                                        for (int i = 0; i < flexColumns.Count; i++)
                                        {
                                                int columnIndex = flexColumns[i];
                                                double roleWeight = GetRoleWeight(roles[columnIndex - 1]);
                                                double widthWeight = Math.Max(1.0, required[columnIndex - 1]);
                                                weights[i] = roleWeight * widthWeight;
                                                weightSum += weights[i];
                                        }

                                        int assigned = 0;
                                        for (int i = 0; i < flexColumns.Count; i++)
                                        {
                                                int columnIndex = flexColumns[i];
                                                int add = (int)Math.Floor(extra * weights[i] / Math.Max(1.0, weightSum));
                                                widths[columnIndex - 1] = minFlexWidth + add;
                                                assigned += add;
                                        }
                                        int remainder = extra - assigned;
                                        int index = 0;
                                        while (remainder > 0 && flexColumns.Count > 0)
                                        {
                                                int columnIndex = flexColumns[index % flexColumns.Count];
                                                widths[columnIndex - 1]++;
                                                remainder--;
                                                index++;
                                        }
                                        DebugListViewLayout("Flex: used weighted widths");
                                }
                        }
                }

                if (!widths.Any(width => width > 0))
                {
                        widths = BuildFallbackColumnWidths(availableWidth, hideSequence);
                        DebugListViewLayout($"Fallback widths: [{string.Join(",", widths)}]");
                }

                ApplyResultListViewColumnWidths(widths);
                DebugListViewLayout($"Final widths: [{string.Join(",", widths)}]");

#if DEBUG
                if (layoutSw != null)
                {
                        layoutSw.Stop();
                        long layoutMs = layoutSw.ElapsedMilliseconds;
                        if (layoutRowCount >= 0 && layoutMs >= 200)
                        {
                                DebugLogger.LogUIThreadBlock("ListViewLayout",
                                        $"UpdateColumnWidths rows={layoutRowCount} availableWidth={availableWidth}", layoutMs);
                        }
                }
#endif
        }

        private sealed class ColumnAutoSizeStats
        {
                public int NonEmptyCount { get; set; }
                public int CountSummaryHits { get; set; }
                public int MaxTextLength { get; set; }
                public string MaxTextSample { get; set; } = string.Empty;
                public int MaxCountSummaryLength { get; set; }
                public string MaxCountSummarySample { get; set; } = string.Empty;
                public bool HasContent => NonEmptyCount > 0;
                public double CountSummaryRatio => NonEmptyCount == 0 ? 0.0 : (double)CountSummaryHits / NonEmptyCount;
        }

        private ColumnAutoSizeStats[] BuildColumnAutoSizeStats(IReadOnlyList<ListViewRowSnapshot> rows, int maxSamples)
        {
                ColumnAutoSizeStats[] stats = new ColumnAutoSizeStats[5]
                {
                        new ColumnAutoSizeStats(),
                        new ColumnAutoSizeStats(),
                        new ColumnAutoSizeStats(),
                        new ColumnAutoSizeStats(),
                        new ColumnAutoSizeStats()
                };

                if (rows == null || rows.Count == 0)
                {
                        return stats;
                }

                int total = rows.Count;
                int sampleTarget = Math.Min(total, Math.Max(1, maxSamples));
                int step = Math.Max(1, total / sampleTarget);
                for (int i = 0; i < total; i += step)
                {
                        ListViewRowSnapshot row = rows[i];
                        UpdateAutoSizeStats(stats[0], row.IndexText);
                        UpdateAutoSizeStats(stats[1], row.Column2);
                        UpdateAutoSizeStats(stats[2], row.Column3);
                        UpdateAutoSizeStats(stats[3], row.Column4);
                        UpdateAutoSizeStats(stats[4], row.Column5);
                }
                int lastIndex = total - 1;
                if (lastIndex >= 0 && lastIndex % step != 0)
                {
                        ListViewRowSnapshot row = rows[lastIndex];
                        UpdateAutoSizeStats(stats[0], row.IndexText);
                        UpdateAutoSizeStats(stats[1], row.Column2);
                        UpdateAutoSizeStats(stats[2], row.Column3);
                        UpdateAutoSizeStats(stats[3], row.Column4);
                        UpdateAutoSizeStats(stats[4], row.Column5);
                }

                return stats;
        }

        private static void UpdateAutoSizeStats(ColumnAutoSizeStats stats, string text)
        {
                if (stats == null || string.IsNullOrWhiteSpace(text))
                {
                        return;
                }
                string value = text.Trim();
                if (value.Length == 0)
                {
                        return;
                }
                stats.NonEmptyCount++;
                int length = value.Length;
                if (length > stats.MaxTextLength)
                {
                        stats.MaxTextLength = length;
                        stats.MaxTextSample = value;
                }
                if (LooksLikeCountSummary(value))
                {
                        stats.CountSummaryHits++;
                        if (length > stats.MaxCountSummaryLength)
                        {
                                stats.MaxCountSummaryLength = length;
                                stats.MaxCountSummarySample = value;
                        }
                }
        }

        private int[] BuildMaxSingleLineWidths(ColumnAutoSizeStats[] stats, int minWidth)
        {
                int[] widths = new int[5];
                for (int i = 0; i < widths.Length; i++)
                {
                        string sample = stats[i].MaxTextSample;
                        if (string.IsNullOrWhiteSpace(sample))
                        {
                                widths[i] = minWidth;
                                continue;
                        }
                        int measured = MeasureSingleLineWidth(sample) + ListViewTextHorizontalPadding;
                        widths[i] = Math.Max(minWidth, measured);
                }
                return widths;
        }

        private int[] BuildMaxCountSummaryWidths(ColumnAutoSizeStats[] stats, int minWidth)
        {
                int[] widths = new int[5];
                for (int i = 0; i < widths.Length; i++)
                {
                        string sample = stats[i].MaxCountSummarySample;
                        if (string.IsNullOrWhiteSpace(sample))
                        {
                                widths[i] = 0;
                                continue;
                        }
                        int measured = MeasureSingleLineWidth(sample) + ListViewTextHorizontalPadding;
                        widths[i] = Math.Max(minWidth, measured);
                }
                return widths;
        }

        private int ResolveShortInfoColumnIndex(ListViewColumnRole[] roles, ColumnAutoSizeStats[] stats, int[] maxCountSummaryWidths)
        {
                int roleIndex = 0;
                for (int i = 0; i < roles.Length; i++)
                {
                        if (roles[i] == ListViewColumnRole.ShortInfo)
                        {
                                roleIndex = i + 1;
                                break;
                        }
                }

                int bestIndex = 0;
                double bestRatio = 0.0;
                int bestWidth = 0;
                for (int i = 0; i < stats.Length; i++)
                {
                        if (!stats[i].HasContent)
                        {
                                continue;
                        }
                        double ratio = stats[i].CountSummaryRatio;
                        if (ratio <= 0)
                        {
                                continue;
                        }
                        int width = maxCountSummaryWidths[i];
                        if (ratio > bestRatio || (Math.Abs(ratio - bestRatio) < 0.001 && width > bestWidth))
                        {
                                bestRatio = ratio;
                                bestIndex = i + 1;
                                bestWidth = width;
                        }
                }

                if (bestIndex > 0 && bestRatio >= ListViewCountSummaryShortInfoThreshold)
                {
                        return bestIndex;
                }

                return roleIndex;
        }

        private static void NormalizeShortInfoRole(ListViewColumnRole[] roles, int shortInfoColumnIndex)
        {
                if (roles == null || shortInfoColumnIndex <= 0 || shortInfoColumnIndex > roles.Length)
                {
                        return;
                }
                for (int i = 0; i < roles.Length; i++)
                {
                        if (roles[i] == ListViewColumnRole.ShortInfo && i + 1 != shortInfoColumnIndex)
                        {
                                roles[i] = ListViewColumnRole.Description;
                        }
                }
                roles[shortInfoColumnIndex - 1] = ListViewColumnRole.ShortInfo;
        }

        private static int GetRoleWeight(ListViewColumnRole role)
        {
                return role switch
                {
                        ListViewColumnRole.Description => 3,
                        ListViewColumnRole.Title => 2,
                        ListViewColumnRole.Creator => 1,
                        _ => 1
                };
        }

        private void DistributeSlack(int[] widths, IReadOnlyList<int> flexColumns, ListViewColumnRole[] roles, int slack)
        {
                if (slack <= 0 || flexColumns == null || flexColumns.Count == 0)
                {
                        return;
                }
                int weightSum = 0;
                int[] weights = new int[flexColumns.Count];
                for (int i = 0; i < flexColumns.Count; i++)
                {
                        int columnIndex = flexColumns[i];
                        int weight = GetRoleWeight(roles[columnIndex - 1]);
                        weights[i] = weight;
                        weightSum += weight;
                }
                int assigned = 0;
                for (int i = 0; i < flexColumns.Count; i++)
                {
                        int columnIndex = flexColumns[i];
                        int add = (int)Math.Floor((double)slack * weights[i] / Math.Max(1, weightSum));
                        widths[columnIndex - 1] += add;
                        assigned += add;
                }
                int remainder = slack - assigned;
                int index = 0;
                while (remainder > 0)
                {
                        int columnIndex = flexColumns[index % flexColumns.Count];
                        widths[columnIndex - 1]++;
                        remainder--;
                        index++;
                }
        }

        private bool TryApplyPlaylistsFlexLayout(int[] widths, bool[] hide, ListViewColumnRole[] roles, IReadOnlyList<int> flexColumns, int flexWidth, int minFlexWidth, int[] maxSingleLineWidths, ColumnAutoSizeStats[] autoStats)
        {
                if (resultListView == null || widths == null || hide == null || roles == null || flexColumns == null || maxSingleLineWidths == null || autoStats == null)
                {
                        return false;
                }

                if (flexWidth <= 0 || minFlexWidth <= 0)
                {
                        return false;
                }

                if (GetCurrentListViewDataMode() != ListViewDataMode.Playlists)
                {
                        return false;
                }

                int titleColumnIndex = FindColumnIndexByRole(roles, ListViewColumnRole.Title);
                int creatorColumnIndex = FindColumnIndexByRole(roles, ListViewColumnRole.Creator);
                int descriptionColumnIndex = FindColumnIndexByRole(roles, ListViewColumnRole.Description);
                if (titleColumnIndex <= 0 || creatorColumnIndex <= 0 || descriptionColumnIndex <= 0)
                {
                        return false;
                }

                if (!flexColumns.Contains(titleColumnIndex) || !flexColumns.Contains(creatorColumnIndex) || !flexColumns.Contains(descriptionColumnIndex))
                {
                        return false;
                }

                int titleIndex = titleColumnIndex - 1;
                int creatorIndex = creatorColumnIndex - 1;
                int descriptionIndex = descriptionColumnIndex - 1;

                if (titleIndex < 0 || titleIndex >= widths.Length || creatorIndex < 0 || creatorIndex >= widths.Length || descriptionIndex < 0 || descriptionIndex >= widths.Length)
                {
                        return false;
                }

                if (hide[titleIndex] || hide[creatorIndex] || hide[descriptionIndex])
                {
                        return false;
                }

                int titleMin = minFlexWidth;
                int creatorMin = CalculatePlaylistsCreatorMinWidth(minFlexWidth);
                int descriptionMin = minFlexWidth;

                int minSum = checked(titleMin + creatorMin + descriptionMin);
                if (flexWidth < minSum)
                {
                        widths[titleIndex] = titleMin;
                        widths[creatorIndex] = minFlexWidth;
                        widths[descriptionIndex] = descriptionMin;
                        DebugListViewLayout($"Flex: playlists clamp flexWidth={flexWidth} < minSum={minSum}");
                        return true;
                }

                int titleIdeal = Math.Max(titleMin, maxSingleLineWidths[titleIndex]);
                int creatorIdeal = Math.Max(creatorMin, maxSingleLineWidths[creatorIndex]);

                int descriptionMax = Math.Max(descriptionMin, Math.Min(maxSingleLineWidths[descriptionIndex], flexWidth));
                int lineHeight = MeasureListViewLineHeight();
                int descriptionIdeal = CalculatePlaylistsDescriptionIdealWidth(autoStats[descriptionIndex], descriptionMin, descriptionMax, lineHeight, maxLines: ListViewMultiLineMaxLines);

                int titleWidth = titleMin;
                int creatorWidth = creatorMin;
                int descriptionWidth = descriptionMin;
                int remaining = checked(flexWidth - titleWidth - creatorWidth - descriptionWidth);
                TakeWidthUpTo(ref titleWidth, titleIdeal, ref remaining);
                TakeWidthUpTo(ref creatorWidth, creatorIdeal, ref remaining);
                TakeWidthUpTo(ref descriptionWidth, descriptionIdeal, ref remaining);
                if (remaining > 0)
                {
                        DistributeRemainingWidthTitleCreator(ref titleWidth, ref creatorWidth, remaining);
                        remaining = 0;
                }

                widths[titleIndex] = titleWidth;
                widths[creatorIndex] = creatorWidth;
                widths[descriptionIndex] = descriptionWidth;

                DebugListViewLayout($"Flex: playlists title=[{titleWidth}/{titleIdeal}] creator=[{creatorWidth}/{creatorIdeal}] desc=[{descriptionWidth}/{descriptionIdeal}] flexWidth={flexWidth}");
                return true;
        }

        private static int FindColumnIndexByRole(ListViewColumnRole[] roles, ListViewColumnRole target)
        {
                if (roles == null)
                {
                        return -1;
                }

                for (int i = 0; i < roles.Length; i++)
                {
                        if (roles[i] == target)
                        {
                                return i + 1;
                        }
                }

                return -1;
        }

        private int CalculatePlaylistsCreatorMinWidth(int minFlexWidth)
        {
                int min = minFlexWidth;
                int chinese = MeasureSingleLineWidth("中中中中") + ListViewTextHorizontalPadding;
                int digits = MeasureSingleLineWidth("88888888") + ListViewTextHorizontalPadding;
                min = Math.Max(min, Math.Max(chinese, digits));
                return min;
        }

        private int CalculatePlaylistsDescriptionIdealWidth(ColumnAutoSizeStats stats, int minWidth, int maxWidth, int lineHeight, int maxLines)
        {
                if (resultListView == null || stats == null)
                {
                        return minWidth;
                }

                string sample = stats.MaxTextSample ?? string.Empty;
                int probeChars = checked(18 * Math.Max(1, maxLines));
                string probe = BuildPlaylistsDescriptionProbeText(sample, probeChars);
                if (string.IsNullOrWhiteSpace(probe))
                {
                        return minWidth;
                }

                int ideal = FindMinColumnWidthToFitLines(probe, maxLines, minWidth, maxWidth, lineHeight);
                ideal = Math.Max(minWidth, Math.Min(maxWidth, ideal));
                return ideal;
        }

        private static string BuildPlaylistsDescriptionProbeText(string sample, int maxChars)
        {
                if (string.IsNullOrWhiteSpace(sample) || maxChars <= 0)
                {
                        return string.Empty;
                }

                string text = sample.Replace("\r", " ").Replace("\n", " ").Trim();
                if (text.Length <= maxChars)
                {
                        return text;
                }

                return text.Substring(0, maxChars).TrimEnd();
        }

        private int FindMinColumnWidthToFitLines(string text, int maxLines, int minWidth, int maxWidth, int lineHeight)
        {
                if (resultListView == null)
                {
                        return minWidth;
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                        return minWidth;
                }

                maxLines = Math.Max(1, maxLines);
                int maxHeight = Math.Max(1, checked(lineHeight * maxLines));

                minWidth = Math.Max(0, minWidth);
                maxWidth = Math.Max(minWidth, maxWidth);

                int left = Math.Max(1, minWidth - ListViewTextHorizontalPadding);
                int right = Math.Max(left, maxWidth - ListViewTextHorizontalPadding);
                int best = right;
                TextFormatFlags flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPadding;

                while (left <= right)
                {
                        int mid = left + (right - left) / 2;
                        Size measured = TextRenderer.MeasureText(text, resultListView.Font, new Size(mid, int.MaxValue), flags);
                        if (measured.Height <= maxHeight)
                        {
                                best = mid;
                                right = mid - 1;
                        }
                        else
                        {
                                left = mid + 1;
                        }
                }

                int columnWidth = checked(best + ListViewTextHorizontalPadding);
                columnWidth = Math.Max(minWidth, Math.Min(maxWidth, columnWidth));
                return columnWidth;
        }

        private int MeasureListViewLineHeight()
        {
                if (resultListView == null)
                {
                        return 0;
                }

                Size measured = TextRenderer.MeasureText("A", resultListView.Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                return Math.Max(1, measured.Height);
        }

        private static void TakeWidthUpTo(ref int width, int target, ref int remaining)
        {
                if (remaining <= 0 || width >= target)
                {
                        return;
                }

                int add = Math.Min(remaining, target - width);
                if (add <= 0)
                {
                        return;
                }

                width = checked(width + add);
                remaining -= add;
        }

        private static void DistributeRemainingWidthTitleCreator(ref int titleWidth, ref int creatorWidth, int remaining)
        {
                if (remaining <= 0)
                {
                        return;
                }

                int titleAdd = (int)Math.Floor(remaining * 2.0 / 3.0);
                int creatorAdd = remaining - titleAdd;
                titleWidth = checked(titleWidth + titleAdd);
                creatorWidth = checked(creatorWidth + creatorAdd);
        }


        [System.Diagnostics.Conditional("DEBUG")]
        private void DebugListViewLayout(string message)
        {
                DebugLogger.Log(DebugLogger.LogLevel.Info, "ListViewLayout", message);
        }

        private int CalculateDesiredListViewContentWidth(bool hideSequence, bool[] hide, ListViewColumnRole[] roles, int shortInfoColumnIndex, int minFlexWidth, int[] maxSingleLineWidths, int[] maxCountSummaryWidths)
        {
                if (hide == null || roles == null || maxSingleLineWidths == null || maxCountSummaryWidths == null)
                {
                        return 0;
                }

                int total = 0;
                for (int i = 0; i < hide.Length; i++)
                {
                        if (hide[i])
                        {
                                continue;
                        }

                        int columnIndex = i + 1;
                        if (columnIndex == 1)
                        {
                                if (!hideSequence)
                                {
                                        total += CalculateSequenceColumnWidth();
                                }
                                continue;
                        }

                        if (columnIndex == shortInfoColumnIndex || roles[i] == ListViewColumnRole.ShortInfo)
                        {
                                int shortInfoWidth = maxCountSummaryWidths[i] > 0 ? maxCountSummaryWidths[i] : maxSingleLineWidths[i];
                                total += Math.Max(minFlexWidth, shortInfoWidth);
                                continue;
                        }

                        if (roles[i] == ListViewColumnRole.Hidden)
                        {
                                continue;
                        }

                        int desired = Math.Max(minFlexWidth, maxSingleLineWidths[i]);
                        total += desired;
                }

                return total;
        }

        private int ApplyResultListViewAutoShrinkLayout(int hostAvailableWidth, int desiredContentWidth, bool allowAutoShrink)
        {
                if (resultListView == null)
                {
                        return hostAvailableWidth;
                }

                int scrollBarWidth = ShouldReserveListViewScrollBarSpace() ? SystemInformation.VerticalScrollBarWidth : 0;
                int hostTotalWidth = Math.Max(0, hostAvailableWidth + scrollBarWidth);
                int targetAvailableWidth = hostAvailableWidth;
                bool active = false;

                if (allowAutoShrink && desiredContentWidth > 0)
                {
                        int paddedDesired = desiredContentWidth + ListViewAutoShrinkPadding;
                        if (paddedDesired < hostAvailableWidth && hostAvailableWidth - paddedDesired >= ListViewAutoShrinkThreshold)
                        {
                                targetAvailableWidth = Math.Max(0, paddedDesired);
                                active = true;
                        }
                }

                int targetTotalWidth = Math.Max(0, targetAvailableWidth + scrollBarWidth);
                if (targetTotalWidth <= 0)
                {
                        return targetAvailableWidth;
                }

                if (Math.Abs(resultListView.Width - targetTotalWidth) >= ListViewAutoShrinkMinDelta)
                {
                        resultListView.Width = targetTotalWidth;
                }
                int targetLeft = 0;
                if (active)
                {
                        targetLeft = Math.Max(0, (hostTotalWidth - targetTotalWidth) / 2);
                }
                if (resultListView.Left != targetLeft)
                {
                        resultListView.Left = targetLeft;
                }

                _isListViewAutoShrinkActive = active;
                _lastListViewAutoWidth = targetTotalWidth;
                DebugListViewLayout($"AutoShrink: host={hostAvailableWidth}, desired={desiredContentWidth}, target={targetAvailableWidth}, active={active}");
                return targetAvailableWidth;
        }

        private int GetResultListViewHostWidth()
        {
                if (resultListPanel != null)
                {
                        return resultListPanel.ClientSize.Width;
                }
                if (resultListView == null)
                {
                        return 0;
                }
                return resultListView.ClientSize.Width;
        }

        private int GetResultListViewAvailableWidth(int totalWidth)
        {
                int width = totalWidth;
                if (width <= 0)
                {
                        return width;
                }

                if (ShouldReserveListViewScrollBarSpace())
                {
                        width -= SystemInformation.VerticalScrollBarWidth;
                }

                return Math.Max(0, width);
        }

        private bool ShouldUseResultListViewLayoutFastPath(int itemCount)
        {
                if (itemCount < ListViewLayoutFastPathItemThreshold)
                {
                        return false;
                }

                if (_isUserResizingListViewColumns)
                {
                        return false;
                }

                return !_customListViewIndexWidth.HasValue &&
                        !_customListViewNameWidth.HasValue &&
                        !_customListViewCreatorWidth.HasValue &&
                        !_customListViewExtraWidth.HasValue &&
                        !_customListViewDescriptionWidth.HasValue;
        }

        private int[] BuildFallbackColumnWidths(int availableWidth, bool hideSequence)
        {
                int[] widths = new int[5];
                if (availableWidth <= 0)
                {
                        return widths;
                }

                int minWidth = CalculateThreeCharColumnWidth();
                int seqWidth = hideSequence ? 0 : CalculateSequenceColumnWidth();
                int remaining = Math.Max(0, availableWidth - seqWidth);
                int[] weights = new int[4] { 40, 20, 20, 20 };
                int weightSum = weights.Sum();
                int assigned = 0;
                int[] flex = new int[4];
                for (int i = 0; i < weights.Length; i++)
                {
                        flex[i] = (int)Math.Floor((double)remaining * weights[i] / Math.Max(1, weightSum));
                        assigned += flex[i];
                }
                int remainder = remaining - assigned;
                int index = 0;
                while (remainder > 0)
                {
                        flex[index % flex.Length]++;
                        remainder--;
                        index++;
                }
                for (int i = 0; i < flex.Length; i++)
                {
                        flex[i] = Math.Max(minWidth, flex[i]);
                }
                widths[0] = seqWidth;
                widths[1] = flex[0];
                widths[2] = flex[1];
                widths[3] = flex[2];
                widths[4] = flex[3];
                return widths;
        }

        private void ApplyResultListViewColumnWidths(int[] widths)
        {
                if (widths == null || widths.Length < 5)
                {
                        return;
                }

                columnHeader0.Width = 0;
                columnHeader1.Width = Math.Max(0, widths[0]);
                columnHeader2.Width = Math.Max(0, widths[1]);
                columnHeader3.Width = Math.Max(0, widths[2]);
                columnHeader4.Width = Math.Max(0, widths[3]);
                columnHeader5.Width = Math.Max(0, widths[4]);
        }

        private int? GetCustomListViewColumnWidth(int columnIndex)
        {
                return columnIndex switch
                {
                        1 => _customListViewIndexWidth,
                        2 => _customListViewNameWidth,
                        3 => _customListViewCreatorWidth,
                        4 => _customListViewExtraWidth,
                        5 => _customListViewDescriptionWidth,
                        _ => null
                };
        }

        private ListViewColumnRole[] ResolveResultListViewColumnRoles()
        {
                ListViewDataMode mode = GetCurrentListViewDataMode();
                return BuildRolesForMode(mode);
        }

        private ListViewColumnRole[] BuildRolesForMode(ListViewDataMode mode)
        {
                return mode switch
                {
                        ListViewDataMode.Songs => new ListViewColumnRole[5]
                        {
                                ListViewColumnRole.Sequence,
                                ListViewColumnRole.Title,
                                ListViewColumnRole.Creator,
                                ListViewColumnRole.Description,
                                ListViewColumnRole.ShortInfo
                        },
                        ListViewDataMode.Playlists => new ListViewColumnRole[5]
                        {
                                ListViewColumnRole.Sequence,
                                ListViewColumnRole.Title,
                                ListViewColumnRole.Creator,
                                ListViewColumnRole.ShortInfo,
                                ListViewColumnRole.Description
                        },
                        ListViewDataMode.Albums => new ListViewColumnRole[5]
                        {
                                ListViewColumnRole.Sequence,
                                ListViewColumnRole.Title,
                                ListViewColumnRole.Creator,
                                ListViewColumnRole.ShortInfo,
                                ListViewColumnRole.Description
                        },
                        ListViewDataMode.Podcasts => new ListViewColumnRole[5]
                        {
                                ListViewColumnRole.Sequence,
                                ListViewColumnRole.Title,
                                ListViewColumnRole.Creator,
                                ListViewColumnRole.ShortInfo,
                                ListViewColumnRole.Description
                        },
                        ListViewDataMode.PodcastEpisodes => new ListViewColumnRole[5]
                        {
                                ListViewColumnRole.Sequence,
                                ListViewColumnRole.Title,
                                ListViewColumnRole.Creator,
                                ListViewColumnRole.ShortInfo,
                                ListViewColumnRole.Description
                        },
                        ListViewDataMode.Artists => new ListViewColumnRole[5]
                        {
                                ListViewColumnRole.Sequence,
                                ListViewColumnRole.Title,
                                ListViewColumnRole.ShortInfo,
                                ListViewColumnRole.Description,
                                ListViewColumnRole.Hidden
                        },
                        ListViewDataMode.ListItems => new ListViewColumnRole[5]
                        {
                                ListViewColumnRole.Sequence,
                                ListViewColumnRole.Title,
                                ListViewColumnRole.Creator,
                                ListViewColumnRole.ShortInfo,
                                ListViewColumnRole.Description
                        },
                        _ => new ListViewColumnRole[5]
                        {
                                ListViewColumnRole.Sequence,
                                ListViewColumnRole.Title,
                                ListViewColumnRole.Creator,
                                ListViewColumnRole.Description,
                                ListViewColumnRole.Description
                        }
                };
        }

        private ListViewDataMode GetCurrentListViewDataMode()
        {
                if (_currentPodcastSounds != null && _currentPodcastSounds.Count > 0)
                {
                        return ListViewDataMode.PodcastEpisodes;
                }
                if (_currentSongs != null && _currentSongs.Count > 0)
                {
                        return ListViewDataMode.Songs;
                }
                if (_currentPlaylists != null && _currentPlaylists.Count > 0)
                {
                        return ListViewDataMode.Playlists;
                }
                if (_currentAlbums != null && _currentAlbums.Count > 0)
                {
                        return ListViewDataMode.Albums;
                }
                if (_currentArtists != null && _currentArtists.Count > 0)
                {
                        return ListViewDataMode.Artists;
                }
                if (_currentPodcasts != null && _currentPodcasts.Count > 0)
                {
                        return ListViewDataMode.Podcasts;
                }
                if (_currentListItems != null && _currentListItems.Count > 0)
                {
                        return ListViewDataMode.ListItems;
                }

                return ResolveDataModeFromViewSource(_currentViewSource);
        }

        private ListViewDataMode ResolveDataModeFromViewSource(string? viewSource)
        {
                if (string.IsNullOrWhiteSpace(viewSource))
                {
                        return ListViewDataMode.Unknown;
                }

                string source = viewSource.Trim();
                if (source.Equals("homepage", StringComparison.OrdinalIgnoreCase))
                {
                        return ListViewDataMode.ListItems;
                }
                if (source.StartsWith("playlist:", StringComparison.OrdinalIgnoreCase) || source.IndexOf("playlist", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                        return ListViewDataMode.Playlists;
                }
                if (source.StartsWith("album:", StringComparison.OrdinalIgnoreCase) || source.IndexOf("album", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                        return ListViewDataMode.Albums;
                }
                if (source.StartsWith("artist:", StringComparison.OrdinalIgnoreCase) || source.StartsWith("artist_category_list:", StringComparison.OrdinalIgnoreCase) || source.IndexOf("artist", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                        return ListViewDataMode.Artists;
                }
                if (source.IndexOf("podcast_episode", StringComparison.OrdinalIgnoreCase) >= 0 || source.IndexOf("program", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                        return ListViewDataMode.PodcastEpisodes;
                }
                if (source.StartsWith("podcast", StringComparison.OrdinalIgnoreCase) || source.IndexOf("podcast", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                        return ListViewDataMode.Podcasts;
                }
                if (source.StartsWith("search:", StringComparison.OrdinalIgnoreCase) || source.StartsWith("song:", StringComparison.OrdinalIgnoreCase))
                {
                        return ListViewDataMode.Songs;
                }
                if (source.Equals(PersonalFmCategoryId, StringComparison.OrdinalIgnoreCase))
                {
                        return ListViewDataMode.Songs;
                }
                return ListViewDataMode.Unknown;
        }

        private bool IsHomePageView()
        {
                return _isHomePage || string.Equals(_currentViewSource, "homepage", StringComparison.OrdinalIgnoreCase);
        }

        private void UpdateResultListViewLineSettings(int shortColumnIndex)
        {
                if (resultListView is SafeListView safeListView)
                {
                        safeListView.MultiLineMaxLines = Math.Max(1, ListViewMultiLineMaxLines);
                        safeListView.ShortInfoColumnIndex = Math.Max(0, shortColumnIndex);
                }
        }

        private static bool LooksLikeShortInfo(string text)
        {
                if (string.IsNullOrWhiteSpace(text))
                {
                        return false;
                }

                string value = text.Trim();
                if (LooksLikeTime(value) || LooksLikeDate(value))
                {
                        return true;
                }

                if (LooksLikeCountSummary(value))
                {
                        return true;
                }

                if (value.Length <= 20 && ContainsDigit(value) && ContainsUnit(value))
                {
                        return true;
                }

                return false;
        }

        private static bool LooksLikeCountSummary(string value)
        {
                if (string.IsNullOrWhiteSpace(value))
                {
                        return false;
                }
                if (!ContainsDigit(value) || !ContainsUnit(value))
                {
                        return false;
                }

                int unitHits = 0;
                unitHits += CountSubstring(value, "首");
                unitHits += CountSubstring(value, "个");
                unitHits += CountSubstring(value, "张");
                unitHits += CountSubstring(value, "类");
                unitHits += CountSubstring(value, "位");
                unitHits += CountSubstring(value, "节目");
                unitHits += CountSubstring(value, "集");
                unitHits += CountSubstring(value, "期");
                unitHits += CountSubstring(value, "条");
                unitHits += CountSubstring(value, "人");

                if (unitHits >= 2)
                {
                        return true;
                }

                return value.Contains("|", StringComparison.Ordinal) || value.Contains("/", StringComparison.Ordinal);
        }

        private static int CountSubstring(string source, string value)
        {
                if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(value))
                {
                        return 0;
                }
                int count = 0;
                int index = 0;
                while (true)
                {
                        index = source.IndexOf(value, index, StringComparison.Ordinal);
                        if (index < 0)
                        {
                                break;
                        }
                        count++;
                        index += value.Length;
                }
                return count;
        }

        private static bool LooksLikeTime(string value)
        {
                if (string.IsNullOrWhiteSpace(value))
                {
                        return false;
                }

                bool hasDigit = false;
                for (int i = 0; i < value.Length; i++)
                {
                        char ch = value[i];
                        if (char.IsDigit(ch))
                        {
                                hasDigit = true;
                        }
                }
                return hasDigit && value.IndexOf(':') >= 0;
        }

        private static bool LooksLikeDate(string value)
        {
                if (string.IsNullOrWhiteSpace(value))
                {
                        return false;
                }

                int dashCount = 0;
                int slashCount = 0;
                bool hasDigit = false;
                for (int i = 0; i < value.Length; i++)
                {
                        char ch = value[i];
                        if (char.IsDigit(ch))
                        {
                                hasDigit = true;
                        }
                        else if (ch == '-')
                        {
                                dashCount++;
                        }
                        else if (ch == '/')
                        {
                                slashCount++;
                        }
                }
                return hasDigit && (dashCount >= 2 || slashCount >= 2);
        }

        private static bool ContainsDigit(string value)
        {
                for (int i = 0; i < value.Length; i++)
                {
                        if (char.IsDigit(value[i]))
                        {
                                return true;
                        }
                }
                return false;
        }

        private static bool ContainsUnit(string value)
        {
                return value.Contains("首", StringComparison.Ordinal) ||
                       value.Contains("个", StringComparison.Ordinal) ||
                       value.Contains("张", StringComparison.Ordinal) ||
                       value.Contains("类", StringComparison.Ordinal) ||
                       value.Contains("位", StringComparison.Ordinal) ||
                       value.Contains("节目", StringComparison.Ordinal) ||
                       value.Contains("集", StringComparison.Ordinal) ||
                       value.Contains("期", StringComparison.Ordinal) ||
                       value.Contains("条", StringComparison.Ordinal) ||
                       value.Contains("人", StringComparison.Ordinal);
        }

        private int CalculateThreeCharColumnWidth()
        {
                int maxWidth = 0;
                maxWidth = Math.Max(maxWidth, MeasureSingleLineWidth("中中中"));
                maxWidth = Math.Max(maxWidth, MeasureSingleLineWidth("888"));
                if (maxWidth <= 0)
                {
                        maxWidth = MeasureSingleLineWidth("AAA");
                }
                return Math.Max(36, maxWidth + ListViewTextHorizontalPadding);
        }


        private int GetResultListViewAvailableWidth()
        {
                if (resultListView == null)
                {
                        return 0;
                }

                return GetResultListViewAvailableWidth(resultListView.ClientSize.Width);
        }

        private bool ShouldReserveListViewScrollBarSpace()
        {
                if (resultListView == null)
                {
                        return false;
                }

                int itemCount = GetResultListViewItemCount();
                if (itemCount <= 0)
                {
                        return false;
                }

                int rowHeight = GetListViewCurrentRowHeight();
                int visibleRows = Math.Max(1, resultListView.ClientSize.Height / Math.Max(1, rowHeight));
                return itemCount > visibleRows;
        }

        private int GetListViewCurrentRowHeight()
        {
                if (resultListView is SafeListView safeListView)
                {
                        return safeListView.GetRowHeight();
                }
                return Math.Max(1, resultListView?.Font?.Height ?? 1);
        }

        private int GetResultListViewItemCount()
        {
                if (resultListView == null)
                {
                        return 0;
                }
                return resultListView.VirtualMode ? resultListView.VirtualListSize : resultListView.Items.Count;
        }

        private int CalculateSequenceColumnWidth()
        {
                bool hideSequence = _hideSequenceNumbers || IsAlwaysSequenceHiddenView();
                bool hasIndexText = !hideSequence;
                if (hideSequence)
                {
                        hasIndexText = HasListViewIndexText();
                }

                if (!hasIndexText)
                {
                        return 0;
                }

                int maxWidth = 0;
                int padding = ListViewTextHorizontalPadding;
                Func<string, int> measure = MeasureSingleLineWidth;
                int digitCount = 1;
                SafeListView? safeListView = resultListView as SafeListView;
                if (safeListView != null)
                {
                        measure = safeListView.MeasureSequenceTextWidth;
                        padding = safeListView.GetSequenceTextPaddingTotal(digitCount);
                }
                if (!hideSequence)
                {
                        int maxIndex = Math.Max(1, _currentSequenceStartIndex + Math.Max(0, GetResultListViewItemCount() - 1));
                        digitCount = maxIndex.ToString().Length;
                        string digits = new string('8', digitCount);
                        if (safeListView != null)
                        {
                                padding = safeListView.GetSequenceTextPaddingTotal(digitCount);
                        }
                        maxWidth = Math.Max(maxWidth, measure(digits));
                }

                maxWidth = Math.Max(maxWidth, measure("上一页"));
                maxWidth = Math.Max(maxWidth, measure("下一页"));
                maxWidth = Math.Max(maxWidth, measure("跳转"));
                return Math.Max(36, maxWidth + padding);
        }

        private bool HasListViewIndexText()
        {
                if (resultListView == null)
                {
                        return false;
                }

                if (resultListView.VirtualMode && _isVirtualSongListActive)
                {
                        return _virtualShowPagination && (_virtualHasPreviousPage || _virtualHasNextPage);
                }

                foreach (ListViewRowSnapshot row in EnumerateResultListViewRows())
                {
                        if (!string.IsNullOrWhiteSpace(row.IndexText))
                        {
                                return true;
                        }
                }
                return false;
        }

        private int[] GetResultListViewMinimumColumnWidths()
        {
                if (resultListView == null)
                {
                        return new int[4];
                }

                int baseMin = GetResultListViewMinimumColumnWidth();
                int nameMin = baseMin + 40;
                int artistMin = baseMin + 20;
                int albumMin = baseMin + 20;
                int extraMin = baseMin;
                return new int[4] { nameMin, artistMin, albumMin, extraMin };
        }

        private int GetResultListViewMinimumColumnWidth()
        {
                if (resultListView == null)
                {
                        return 0;
                }

                Size size = TextRenderer.MeasureText("8888", resultListView.Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                return Math.Max(80, size.Width + 12);
        }

        private static int[] DistributeListViewColumnWidths(int totalWidth, int[] minWidths, int[] weightHints, int maxExtraWidth)
        {
                int count = minWidths.Length;
                int[] widths = new int[count];
                if (totalWidth <= 0)
                {
                        return widths;
                }

                int minTotal = 0;
                for (int i = 0; i < count; i++)
                {
                        minTotal += Math.Max(0, minWidths[i]);
                }

                if (totalWidth <= minTotal)
                {
                        int assigned = 0;
                        for (int i = 0; i < count; i++)
                        {
                                widths[i] = (int)Math.Floor((double)totalWidth * minWidths[i] / minTotal);
                                assigned += widths[i];
                        }
                        int remainder = totalWidth - assigned;
                        for (int i = 0; remainder > 0; i = (i + 1) % count)
                        {
                                widths[i]++;
                                remainder--;
                        }
                        return widths;
                }

                int extra = totalWidth - minTotal;
                int weightSum = 0;
                for (int i = 0; i < count; i++)
                {
                        weightSum += Math.Max(1, weightHints[i]);
                }

                int extraAssigned = 0;
                for (int i = 0; i < count; i++)
                {
                        int weight = Math.Max(1, weightHints[i]);
                        int add = (int)Math.Floor((double)extra * weight / weightSum);
                        widths[i] = minWidths[i] + add;
                        extraAssigned += add;
                }

                int extraRemainder = extra - extraAssigned;
                for (int i = 0; extraRemainder > 0; i = (i + 1) % count)
                {
                        widths[i]++;
                        extraRemainder--;
                }

                if (count >= 4 && maxExtraWidth > 0 && widths[3] > maxExtraWidth)
                {
                        int overflow = widths[3] - maxExtraWidth;
                        widths[3] = maxExtraWidth;
                        int[] redistributeWeights = new int[3]
                        {
                                Math.Max(1, weightHints[0]),
                                Math.Max(1, weightHints[1]),
                                Math.Max(1, weightHints[2])
                        };
                        int redistributeSum = redistributeWeights[0] + redistributeWeights[1] + redistributeWeights[2];
                        int distributed = 0;
                        for (int i = 0; i < 3; i++)
                        {
                                int add = (int)Math.Floor((double)overflow * redistributeWeights[i] / redistributeSum);
                                widths[i] += add;
                                distributed += add;
                        }
                        int remaining = overflow - distributed;
                        for (int i = 0; remaining > 0; i = (i + 1) % 3)
                        {
                                widths[i]++;
                                remaining--;
                        }
                }
                return widths;
        }

        private int MeasureSingleLineWidth(string text)
        {
                if (resultListView == null || string.IsNullOrEmpty(text))
                {
                        return 0;
                }
                Size size = TextRenderer.MeasureText(text, resultListView.Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl);
                return size.Width;
        }

        private void UpdateResultListViewRowHeight()
        {
                if (resultListView == null)
                {
                        return;
                }

#if DEBUG
                Stopwatch rowHeightSw = Stopwatch.StartNew();
                int rowHeightCount = 0;
#endif
                if (_customListViewRowHeight.HasValue)
                {
                        int minHeight = Math.Max(ListViewRowResizeMinHeight, resultListView.Font?.Height ?? ListViewRowResizeMinHeight);
                        int target = Math.Max(minHeight, Math.Min(ListViewMaxRowHeight, _customListViewRowHeight.Value));
                        if (resultListView is SafeListView customSafeListView)
                        {
                                customSafeListView.SetRowHeight(target);
                        }
                        return;
                }

                int lineHeight = TextRenderer.MeasureText("A", resultListView.Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Height;
                int defaultHeight = Math.Max(30, lineHeight + 10);
                int maxAllowedHeight = Math.Max(defaultHeight, lineHeight * ListViewMultiLineMaxLines + 10);
                int maxHeight = 0;
                int[] columnWidths = new int[5]
                {
                        columnHeader1.Width,
                        columnHeader2.Width,
                        columnHeader3.Width,
                        columnHeader4.Width,
                        columnHeader5.Width
                };
#if DEBUG
                TouchUiThreadMarker("UpdateRowHeight");
#endif

                int totalItemCount = GetResultListViewItemCount();
                if (totalItemCount >= ListViewRowHeightFastPathItemThreshold)
                {
                        int estimated = Math.Min(maxAllowedHeight, Math.Max(defaultHeight, lineHeight * ListViewMultiLineMaxLines + 4));
                        if (resultListView is SafeListView largeSafeListView)
                        {
                                largeSafeListView.SetRowHeight(estimated);
                        }
#if DEBUG
                        rowHeightCount = totalItemCount;
                        DebugListViewLayout($"RowHeightFastPath: rows={totalItemCount}, target={estimated}");
#endif
                        return;
                }

                foreach (ListViewRowSnapshot row in EnumerateResultListViewRows())
                {
#if DEBUG
                        rowHeightCount++;
#endif
                        int rowHeight = MeasureRowHeight(row, columnWidths, lineHeight, maxAllowedHeight);
                        if (rowHeight > maxHeight)
                        {
                                maxHeight = rowHeight;
                        }
                }

                maxHeight = Math.Min(maxHeight, maxAllowedHeight);
                int targetHeight = Math.Min(maxAllowedHeight, Math.Max(defaultHeight, maxHeight + 4));
                if (resultListView is SafeListView safeListView)
                {
                        safeListView.SetRowHeight(targetHeight);
                }
#if DEBUG
                if (rowHeightSw != null)
                {
                        rowHeightSw.Stop();
                        long rowHeightMs = rowHeightSw.ElapsedMilliseconds;
                        if (rowHeightMs >= 200)
                        {
                                DebugLogger.LogUIThreadBlock("ListViewLayout",
                                        $"UpdateRowHeight rows={rowHeightCount} target={targetHeight}", rowHeightMs);
                        }
                }
#endif
        }

        private int MeasureRowHeight(ListViewRowSnapshot row, int[] columnWidths, int lineHeight, int maxAllowedHeight)
        {
                int height = 0;
                height = Math.Max(height, MeasureTextHeight(row.IndexText, columnWidths[0], lineHeight, maxAllowedHeight, GetColumnMaxLines(1)));
                height = Math.Max(height, MeasureTextHeight(row.Column2, columnWidths[1], lineHeight, maxAllowedHeight, GetColumnMaxLines(2)));
                height = Math.Max(height, MeasureTextHeight(row.Column3, columnWidths[2], lineHeight, maxAllowedHeight, GetColumnMaxLines(3)));
                height = Math.Max(height, MeasureTextHeight(row.Column4, columnWidths[3], lineHeight, maxAllowedHeight, GetColumnMaxLines(4)));
                height = Math.Max(height, MeasureTextHeight(row.Column5, columnWidths[4], lineHeight, maxAllowedHeight, GetColumnMaxLines(5)));
                return height;
        }

        private int GetColumnMaxLines(int columnIndex)
        {
                if (columnIndex <= 1)
                {
                        return 1;
                }
                if (_listViewShortInfoColumnIndex > 1 && columnIndex == _listViewShortInfoColumnIndex)
                {
                        return 1;
                }
                return ListViewMultiLineMaxLines;
        }

        private int MeasureTextHeight(string text, int columnWidth, int lineHeight, int maxAllowedHeight, int maxLines)
        {
                if (resultListView == null || string.IsNullOrWhiteSpace(text) || columnWidth <= 0)
                {
                        return 0;
                }
                int availableWidth = Math.Max(1, columnWidth - ListViewTextHorizontalPadding);
                int boundedHeight = Math.Max(1, maxAllowedHeight);
                if (maxLines <= 1)
                {
                        return Math.Min(lineHeight, boundedHeight);
                }

                int maxHeight = Math.Min(boundedHeight, lineHeight * maxLines);
                TextFormatFlags flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPadding;
                Size measured = TextRenderer.MeasureText(text, resultListView.Font, new Size(availableWidth, maxHeight), flags);
                return Math.Min(measured.Height, maxHeight);
        }

        private IEnumerable<ListViewRowSnapshot> EnumerateResultListViewRows()
        {
                if (resultListView == null)
                {
                        yield break;
                }

                if (resultListView.VirtualMode && _isVirtualSongListActive)
                {
                        int songCount = _virtualSongs?.Count ?? 0;
                        for (int i = 0; i < songCount; i++)
                        {
                                SongInfo song = _virtualSongs[i];
                                if (song == null)
                                {
                                        continue;
                                }
                                string title = string.IsNullOrWhiteSpace(song.Name) ? "未知" : song.Name;
                                if (song.RequiresVip)
                                {
                                        title += "  [VIP]";
                                }
                                string duration = song.FormattedDuration ?? string.Empty;
                                if (song.IsAvailable == false)
                                {
                                        duration = string.IsNullOrWhiteSpace(duration) ? "不可播放" : duration + " (不可播放)";
                                }
                                yield return new ListViewRowSnapshot(
                                        FormatIndex(checked(_virtualStartIndex + i)),
                                        title,
                                        song.Artist ?? string.Empty,
                                        song.Album ?? string.Empty,
                                        duration);
                        }

                        if (_virtualShowPagination)
                        {
                                if (_virtualHasPreviousPage)
                                {
                                        yield return new ListViewRowSnapshot("上一页", string.Empty, string.Empty, string.Empty, string.Empty);
                                }
                                if (_virtualHasNextPage)
                                {
                                        yield return new ListViewRowSnapshot("下一页", string.Empty, string.Empty, string.Empty, string.Empty);
                                }
                                if (_virtualHasPreviousPage || _virtualHasNextPage)
                                {
                                        yield return new ListViewRowSnapshot("跳转", string.Empty, string.Empty, string.Empty, string.Empty);
                                }
                        }
                        yield break;
                }

                foreach (ListViewItem item in resultListView.Items)
                {
                        if (item == null)
                        {
                                continue;
                        }
                        string indexText = (item.SubItems.Count > 1 ? item.SubItems[1].Text : item.Text) ?? string.Empty;
                        string col2 = (item.SubItems.Count > 2 ? item.SubItems[2].Text : string.Empty) ?? string.Empty;
                        string col3 = (item.SubItems.Count > 3 ? item.SubItems[3].Text : string.Empty) ?? string.Empty;
                        string col4 = (item.SubItems.Count > 4 ? item.SubItems[4].Text : string.Empty) ?? string.Empty;
                        string col5 = (item.SubItems.Count > 5 ? item.SubItems[5].Text : string.Empty) ?? string.Empty;
                        yield return new ListViewRowSnapshot(indexText, col2, col3, col4, col5);
                }
        }

        private readonly struct ListViewRowSnapshot
        {
                public string IndexText { get; }

                public string Column2 { get; }

                public string Column3 { get; }

                public string Column4 { get; }

                public string Column5 { get; }

                public ListViewRowSnapshot(string indexText, string column2, string column3, string column4, string column5)
                {
                        IndexText = indexText ?? string.Empty;
                        Column2 = column2 ?? string.Empty;
                        Column3 = column3 ?? string.Empty;
                        Column4 = column4 ?? string.Empty;
                        Column5 = column5 ?? string.Empty;
                }
        }

        private void RegisterRoundedControl(Control control, int radius)
        {
                if (control == null)
                {
                        return;
                }

                _roundedControls[control] = radius;
                control.SizeChanged -= RoundedControl_SizeChanged;
                control.SizeChanged += RoundedControl_SizeChanged;
                ApplyRoundedRegion(control, radius);
        }

        private void RoundedControl_SizeChanged(object? sender, EventArgs e)
        {
                if (sender is Control control && _roundedControls.TryGetValue(control, out int radius))
                {
                        ApplyRoundedRegion(control, radius);
                }
        }

        private void RegisterCircularControl(Control control)
        {
                if (control == null)
                {
                        return;
                }

                control.SizeChanged -= CircularControl_SizeChanged;
                control.SizeChanged += CircularControl_SizeChanged;
                CircularControl_SizeChanged(control, EventArgs.Empty);
        }

        private void CircularControl_SizeChanged(object? sender, EventArgs e)
        {
                if (sender is Control control)
                {
                        int radius = Math.Min(control.Width, control.Height) / 2;
                        ApplyRoundedRegion(control, radius);
                }
        }

        private static void ApplyRoundedRegion(Control control, int radius)
        {
                if (control == null || control.Width <= 0 || control.Height <= 0)
                {
                        return;
                }

                int safeRadius = Math.Max(0, Math.Min(radius, Math.Min(control.Width, control.Height) / 2));
                Rectangle bounds = new Rectangle(0, 0, control.Width, control.Height);
                using (GraphicsPath path = BuildRoundedRectanglePath(bounds, safeRadius))
                {
                        control.Region = new Region(path);
                }
        }

        private static GraphicsPath BuildRoundedRectanglePath(Rectangle bounds, int radius)
        {
                GraphicsPath path = new GraphicsPath();
                if (radius <= 0)
                {
                        path.AddRectangle(bounds);
                        return path;
                }

                int diameter = radius * 2;
                Rectangle arc = new Rectangle(bounds.Location, new Size(diameter, diameter));
                path.AddArc(arc, 180, 90);
                arc.X = bounds.Right - diameter;
                path.AddArc(arc, 270, 90);
                arc.Y = bounds.Bottom - diameter;
                path.AddArc(arc, 0, 90);
                arc.X = bounds.Left;
                path.AddArc(arc, 90, 90);
                path.CloseFigure();
                return path;
        }

        private async void MainForm_Load(object sender, EventArgs e)
        {
                Task.Run(async delegate
                {
			try
			{
				await _apiClient.WarmupSessionAsync();
			}
			catch (Exception ex)
			{
				Exception ex2 = ex;
				Exception ex3 = ex2;
				Debug.WriteLine("[MainForm] 热身失败（忽略）: " + ex3.Message);
			}
		});
		ScheduleBackgroundUpdateCheck();
		await EnsureInitialHomePageLoadedAsync();
	}

	private async Task EnsureInitialHomePageLoadedAsync()
	{
		if (_initialHomeLoadCts != null)
		{
			StopInitialHomeLoadLoop("重启初始主页加载");
		}
		_initialHomeLoadCompleted = false;
		CancellationToken token = (_initialHomeLoadCts = new CancellationTokenSource()).Token;
		int attempt = 0;
		bool showErrorDialog = true;
		checked
		{
			while (!token.IsCancellationRequested)
			{
				attempt++;
				try
				{
					if (await LoadHomePageAsync(skipSave: false, showErrorDialog, isInitialLoad: true, token))
					{
						StopInitialHomeLoadLoop("初始主页加载完成", cancelToken: false);
						break;
					}
				}
				catch (OperationCanceledException) when (token.IsCancellationRequested)
				{
					Debug.WriteLine("[HomePage] 初始主页加载被取消");
					break;
				}
				showErrorDialog = false;
				try
				{
					UpdateStatusBar($"主页加载失败，{1.5:F1} 秒后重试（第 {attempt + 1} 次）...");
					await Task.Delay(1500, token);
				}
				catch (OperationCanceledException) when (token.IsCancellationRequested)
				{
					Debug.WriteLine("[HomePage] 主页加载重试等待被取消");
					break;
				}
			}
		}
	}

	private void StopInitialHomeLoadLoop(string reason, bool cancelToken = true)
	{
		CancellationTokenSource initialHomeLoadCts = _initialHomeLoadCts;
		if (initialHomeLoadCts == null)
		{
			return;
		}
		Debug.WriteLine("[HomePage] " + (cancelToken ? "取消" : "清理") + "初始加载: " + reason);
		_initialHomeLoadCts = null;
		if (cancelToken)
		{
			try
			{
				initialHomeLoadCts.Cancel();
			}
			catch (ObjectDisposedException)
			{
			}
		}
		initialHomeLoadCts.Dispose();
	}

	private void InitializeServices()
	{
		try
		{
			_configManager = ConfigManager.Instance;
			_config = _configManager.Load();
			_apiClient = new NeteaseApiClient(_config);
			_apiClient.UseSimplifiedApi = false;
			_unblockService = new UnblockService();
			ApplyAccountStateOnStartup();
			string preferredDeviceId = _config?.OutputDevice;
			_audioEngine = new BassAudioEngine(preferredDeviceId);
			if (_config != null && !string.Equals(_config.OutputDevice, _audioEngine.ActiveOutputDeviceId, StringComparison.OrdinalIgnoreCase))
			{
				_config.OutputDevice = _audioEngine.ActiveOutputDeviceId;
				_configManager?.Save(_config);
			}
			_audioEngine.BufferingStateChanged += OnBufferingStateChanged;
			_audioEngine.StateChanged += OnAudioEngineStateChanged;
			_lyricsCacheManager = new LyricsCacheManager();
			_lyricsDisplayManager = new LyricsDisplayManager(_lyricsCacheManager);
			_lyricsLoader = new LyricsLoader(_apiClient);
			_lyricsDisplayManager.LyricUpdated += OnLyricUpdated;
			_audioEngine.PositionChanged += OnAudioPositionChanged;
			_nextSongPreloader = new NextSongPreloader(_apiClient, (song, quality, token) => ResolveSongPlaybackAsync(song, quality, token, suppressStatusUpdates: true));
			_positionCoordinator = new PositionCoordinator();
			_seekManager = new SeekManager(_audioEngine);
			_seekManager.SeekCompleted += OnSeekCompleted;
			_seekManager.SeekRequested += OnSeekRequested;
			_seekManager.SeekExecuted += OnSeekExecuted;
			_updateTimer = new System.Windows.Forms.Timer();
			_updateTimer.Interval = 100;
			_updateTimer.Tick += UpdateTimer_Tick;
			_updateTimer.Start();
			_scrubKeyTimer = new System.Windows.Forms.Timer();
			_scrubKeyTimer.Interval = 50;
			_scrubKeyTimer.Tick += ScrubKeyTimer_Tick;
			InitializeOrderAutoSaveTimers();
			StartStateUpdateLoop();
			InitializeCommandQueueSystem();
			if (searchTypeComboBox.Items.Count > 0)
			{
				searchTypeComboBox.SelectedIndex = 0;
			}
			InitializeDownload();
			InitializePlaybackReportingService();
			InitializeRecognitionFeature();
			UpdateStatusBar("就绪");
		}
		catch (Exception ex)
		{
			Debug.WriteLine("[MainForm] 初始化异常: " + ex.Message);
			Debug.WriteLine("[MainForm] 异常堆栈: " + ex.StackTrace);
			try
			{
				if (_configManager == null)
				{
					_configManager = ConfigManager.Instance;
				}
				if (_config == null)
				{
					_config = _configManager.CreateDefaultConfig();
					Debug.WriteLine("[MainForm] 使用默认配置");
				}
				if (_apiClient == null)
				{
					_apiClient = new NeteaseApiClient(_config);
					_apiClient.UseSimplifiedApi = false;
					Debug.WriteLine("[MainForm] 已使用默认配置初始化 API 客户端");
				}
			}
			catch (Exception ex2)
			{
				Debug.WriteLine("[MainForm] 后备初始化失败: " + ex2.Message);
			}
			MessageBox.Show("初始化失败: " + ex.Message + "\n\n音频功能可能不可用，但登录功能仍可使用。", "警告", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
			UpdateStatusBar("初始化失败（部分功能可用）");
		}
	}

	private void InitializeOrderAutoSaveTimers()
	{
		_playlistOrderAutoSaveTimer = new System.Windows.Forms.Timer();
		_playlistOrderAutoSaveTimer.Interval = 1000;
		_playlistOrderAutoSaveTimer.Tick += PlaylistOrderAutoSaveTimer_Tick;

		_songOrderAutoSaveTimer = new System.Windows.Forms.Timer();
		_songOrderAutoSaveTimer.Interval = 1000;
		_songOrderAutoSaveTimer.Tick += SongOrderAutoSaveTimer_Tick;
	}

	private async void PlaylistOrderAutoSaveTimer_Tick(object? sender, EventArgs e)
	{
		_playlistOrderAutoSaveTimer?.Stop();
		await TryAutoSavePlaylistOrderAsync();
	}

	private async void SongOrderAutoSaveTimer_Tick(object? sender, EventArgs e)
	{
		_songOrderAutoSaveTimer?.Stop();
		await TryAutoSaveSongOrderAsync();
	}

	private void SetupEventHandlers()
	{
		if (_audioEngine != null)
		{
			_audioEngine.PlaybackStopped += AudioEngine_PlaybackStopped;
			_audioEngine.PlaybackEnded += AudioEngine_PlaybackEnded;
			_audioEngine.GaplessTransitionCompleted += AudioEngine_GaplessTransitionCompleted;
		}
		base.KeyPreview = true;
		base.KeyDown += MainForm_KeyDown;
		base.KeyUp += MainForm_KeyUp;
		base.Deactivate += MainForm_Deactivate;
		base.SizeChanged += MainForm_SizeChanged;
		base.LocationChanged += MainForm_LocationChanged;
	}

	private ConfigModel EnsureConfigInitialized()
	{
		if (_config != null)
		{
			return _config;
		}
		if (_configManager == null)
		{
			_configManager = ConfigManager.Instance;
		}
		try
		{
			_config = _configManager?.Load();
		}
		catch (Exception ex)
		{
			Debug.WriteLine("[Config] 加载配置失败，尝试重置: " + ex.Message);
			try
			{
				_config = _configManager?.Reset();
			}
			catch (Exception ex2)
			{
				Debug.WriteLine("[Config] 重置配置失败: " + ex2.Message);
				_config = new ConfigModel();
			}
		}
		if (_config == null)
		{
			_config = new ConfigModel();
		}
		return _config;
	}

	private void LoadConfig()
	{
		ConfigModel configModel = EnsureConfigInitialized();
		if (configModel.SearchHistory == null)
		{
			configModel.SearchHistory = new List<string>();
		}
		InitializeSearchHistoryUi(configModel.SearchHistory);
        if (_audioEngine != null)
        {
                volumeTrackBar.Value = checked((int)(configModel.Volume * 100.0));
                _audioEngine.SetVolume((float)configModel.Volume);
                volumeLabel.Text = $"{volumeTrackBar.Value}%";
        }
		PlayMode playMode = PlayMode.Sequential;
		if (configModel.PlaybackOrder == "列表循环")
		{
			playMode = PlayMode.Loop;
		}
		else if (configModel.PlaybackOrder == "单曲循环")
		{
			playMode = PlayMode.LoopOne;
		}
		else if (configModel.PlaybackOrder == "随机播放")
		{
			playMode = PlayMode.Random;
		}
		if (_audioEngine != null)
		{
			_audioEngine.PlayMode = playMode;
		}
		UpdatePlaybackOrderMenuCheck();
		UpdateQualityMenuCheck();
		UpdateLoginMenuItemText();
		RefreshQualityMenuAvailability();
		_autoReadLyrics = configModel.LyricsReadingEnabled;
		_hideSequenceNumbers = configModel.SequenceNumberHidden;
		_hideControlBar = configModel.ControlBarHidden;
		_preventSleepDuringPlayback = configModel.PreventSleepDuringPlayback;
		_focusFollowPlayback = configModel.FocusFollowPlayback;
		if (!_focusFollowPlayback)
		{
			ClearPlaybackFollowPendingState();
		}
		ApplyWindowLayoutFromConfig(configModel);
		try
		{
            SetMenuItemCheckedState(autoReadLyricsMenuItem, _autoReadLyrics);
            autoReadLyricsMenuItem.Text = (_autoReadLyrics ? "关闭歌词朗读\tF11" : "打开歌词朗读\tF11");
			UpdatePreventSleepDuringPlaybackMenuItemText();
			UpdateFocusFollowPlaybackMenuItemText();
			UpdateHideSequenceMenuItemText();
			UpdateHideControlBarMenuItemText();
			if (_hideSequenceNumbers)
			{
				RefreshSequenceDisplayInPlace();
			}
			ApplyControlBarVisibility();
			UpdatePlaybackPowerRequests(IsPlaybackActiveState(GetCachedPlaybackState()));
		}
		catch
		{
		}
		Debug.WriteLine($"[CONFIG] LyricsReadingEnabled={_autoReadLyrics}");
		Debug.WriteLine($"[CONFIG] SequenceNumberHidden={_hideSequenceNumbers}");
		Debug.WriteLine($"[CONFIG] ControlBarHidden={_hideControlBar}");
		Debug.WriteLine($"[CONFIG] PreventSleepDuringPlayback={_preventSleepDuringPlayback}");
		Debug.WriteLine($"[CONFIG] FocusFollowPlayback={_focusFollowPlayback}");
		_playbackReportingService?.UpdateSettings(_config);
        Debug.WriteLine($"[CONFIG] UsePersonalCookie={_apiClient.UsePersonalCookie} (自动检测)");
        Debug.WriteLine($"[CONFIG] AccountState.IsLoggedIn={_accountState?.IsLoggedIn}");
        Debug.WriteLine("[CONFIG] AccountState.MusicU=" + (string.IsNullOrEmpty(_accountState?.MusicU) ? "未设置" : "已设置"));
        Debug.WriteLine("[CONFIG] AccountState.CsrfToken=" + (string.IsNullOrEmpty(_accountState?.CsrfToken) ? "未设置" : "已设置"));
        if (_apiClient.UsePersonalCookie)
        {
			Task.Run(async delegate
			{
				await EnsureLoginProfileAsync();
            });
        }
    }

    private void ApplyWindowLayoutFromConfig(ConfigModel configModel)
    {
        if (configModel == null || base.IsDisposed)
        {
            return;
        }

        bool hasWidth = configModel.WindowWidth.HasValue && configModel.WindowWidth.Value > 0;
        bool hasHeight = configModel.WindowHeight.HasValue && configModel.WindowHeight.Value > 0;
        bool hasLocation = configModel.WindowX.HasValue && configModel.WindowY.HasValue;
        bool hasState = !string.IsNullOrWhiteSpace(configModel.WindowState);

        if (!hasWidth && !hasHeight && !hasLocation && !hasState)
        {
            return;
        }

        _isApplyingWindowLayout = true;
        try
        {
            if (hasWidth || hasHeight || hasLocation)
            {
                int width = hasWidth ? configModel.WindowWidth!.Value : base.Width;
                int height = hasHeight ? configModel.WindowHeight!.Value : base.Height;
                int x = hasLocation ? configModel.WindowX!.Value : base.Location.X;
                int y = hasLocation ? configModel.WindowY!.Value : base.Location.Y;
                Rectangle targetBounds = EnsureWindowBoundsVisible(new Rectangle(x, y, width, height));
                StartPosition = FormStartPosition.Manual;
                Bounds = targetBounds;
            }

            if (hasState)
            {
                string state = configModel.WindowState!.Trim();
                if (string.Equals(state, "Maximized", StringComparison.OrdinalIgnoreCase))
                {
                    WindowState = FormWindowState.Maximized;
                }
                else if (string.Equals(state, "Normal", StringComparison.OrdinalIgnoreCase))
                {
                    WindowState = FormWindowState.Normal;
                }
            }

            _lastWindowState = WindowState;
        }
        finally
        {
            _isApplyingWindowLayout = false;
        }
    }

    private static Rectangle EnsureWindowBoundsVisible(Rectangle bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return bounds;
        }

        Rectangle workingArea = Screen.FromRectangle(bounds).WorkingArea;
        int width = Math.Min(bounds.Width, workingArea.Width);
        int height = Math.Min(bounds.Height, workingArea.Height);
        int x = Math.Max(workingArea.Left, Math.Min(bounds.X, workingArea.Right - width));
        int y = Math.Max(workingArea.Top, Math.Min(bounds.Y, workingArea.Bottom - height));
        return new Rectangle(x, y, width, height);
    }

    private void UpdateWindowLayoutConfig(ConfigModel configModel)
    {
        if (configModel == null || WindowState == FormWindowState.Minimized)
        {
            return;
        }

        Rectangle bounds = (WindowState == FormWindowState.Normal) ? Bounds : RestoreBounds;
        configModel.WindowX = bounds.X;
        configModel.WindowY = bounds.Y;
        configModel.WindowWidth = bounds.Width;
        configModel.WindowHeight = bounds.Height;
        configModel.WindowState = (WindowState == FormWindowState.Maximized) ? "Maximized" : "Normal";
    }

    private void ScheduleWindowLayoutPersist()
    {
        if (_isApplyingWindowLayout || WindowState == FormWindowState.Minimized)
        {
            return;
        }

        if (_windowLayoutPersistTimer == null)
        {
            _windowLayoutPersistTimer = new System.Windows.Forms.Timer();
            _windowLayoutPersistTimer.Interval = 400;
            _windowLayoutPersistTimer.Tick += (_, _) =>
            {
                _windowLayoutPersistTimer.Stop();
                PersistWindowLayoutConfig();
            };
        }

        _windowLayoutPersistTimer.Stop();
        _windowLayoutPersistTimer.Start();
    }

    private void PersistWindowLayoutConfig()
    {
        if (_isApplyingWindowLayout || WindowState == FormWindowState.Minimized)
        {
            return;
        }

        try
        {
            ConfigModel configModel = EnsureConfigInitialized();
            if (configModel == null || _configManager == null)
            {
                return;
            }
            UpdateWindowLayoutConfig(configModel);
            _configManager.Save(configModel);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[Config] 保存窗口布局失败: " + ex.Message);
        }
    }

	private void InitializeSearchHistoryUi(IReadOnlyCollection<string> history)
	{
		if (searchTextBox != null)
		{
			EnsureSearchHistoryAutoCompleteSource();
			RefreshSearchHistoryAutoComplete(history);
			ResetSearchHistoryNavigation();
		}
	}

        private void RefreshSearchHistoryAutoComplete(IEnumerable<string> history)
        {
                if (searchTextBox == null)
                {
                        return;
                }
                EnsureSearchHistoryAutoCompleteSource();
                if (_searchHistoryAutoComplete == null)
                {
                        return;
                }
                _searchHistoryAutoComplete.Clear();
                if (history == null)
                {
                        return;
                }
		foreach (string item in history)
		{
			if (!string.IsNullOrWhiteSpace(item))
			{
				_searchHistoryAutoComplete.Add(item);
			}
		}
	}

	private void ResetSearchHistoryNavigation()
	{
		_searchHistoryIndex = -1;
		_searchHistoryDraft = string.Empty;
	}

	private void ApplySearchHistoryText(string text)
	{
		if (searchTextBox == null)
		{
			return;
		}
		_isApplyingSearchHistoryText = true;
		try
		{
			searchTextBox.Text = text;
			searchTextBox.Focus();
			BeginInvoke((System.Windows.Forms.MethodInvoker)delegate
			{
				searchTextBox.SelectAll();
			});
		}
		finally
		{
			_isApplyingSearchHistoryText = false;
		}
	}

	private bool NavigateSearchHistory(bool moveUp)
	{
		ConfigModel configModel = EnsureConfigInitialized();
		if (configModel == null)
		{
			return false;
		}
		List<string> searchHistory = configModel.SearchHistory;
		if (searchHistory == null || searchHistory.Count == 0 || searchTextBox == null)
		{
			return false;
		}
		if (_searchHistoryIndex == -1)
		{
			_searchHistoryDraft = searchTextBox.Text;
		}
		checked
		{
			if (moveUp)
			{
				if (_searchHistoryIndex + 1 >= searchHistory.Count)
				{
					return true;
				}
				_searchHistoryIndex++;
			}
			else
			{
				if (_searchHistoryIndex <= -1)
				{
					return true;
				}
				_searchHistoryIndex--;
			}
			string text = ((_searchHistoryIndex >= 0 && _searchHistoryIndex < searchHistory.Count) ? searchHistory[_searchHistoryIndex] : _searchHistoryDraft);
			ApplySearchHistoryText(text);
			return true;
		}
	}

	private void RecordSearchHistory(string keyword)
	{
		if (string.IsNullOrWhiteSpace(keyword))
		{
			return;
		}
		ConfigModel configModel = EnsureConfigInitialized();
		if (configModel == null)
		{
			return;
		}
		if (configModel.SearchHistory == null)
		{
			configModel.SearchHistory = new List<string>();
		}
		List<string> searchHistory = configModel.SearchHistory;
		searchHistory.RemoveAll((string h) => string.Equals(h, keyword, StringComparison.OrdinalIgnoreCase));
		searchHistory.Insert(0, keyword);
		if (searchHistory.Count > 50)
		{
			searchHistory.RemoveRange(50, checked(searchHistory.Count - 50));
		}
		if (_searchHistoryAutoComplete == null)
		{
			InitializeSearchHistoryUi(searchHistory);
		}
		else
		{
			RefreshSearchHistoryAutoComplete(searchHistory);
		}
		ResetSearchHistoryNavigation();
		try
		{
			_configManager?.Save(configModel);
		}
		catch (Exception ex)
		{
			Debug.WriteLine("[SearchHistory] 保存搜索历史失败: " + ex.Message);
		}
	}

	private void ClearSearchHistory()
	{
		ConfigModel configModel = EnsureConfigInitialized();
		if (configModel?.SearchHistory == null || configModel.SearchHistory.Count == 0)
		{
			return;
		}
		configModel.SearchHistory.Clear();
		RefreshSearchHistoryAutoComplete(configModel.SearchHistory);
		ResetSearchHistoryNavigation();
		try
		{
			_configManager?.Save(configModel);
		}
		catch (Exception ex)
		{
			Debug.WriteLine("[SearchHistory] 清空搜索历史失败: " + ex.Message);
		}
	}

	private void PersistPlaybackState(bool includeQueue = true, bool persistToDisk = true)
	{
		try
		{
			ConfigModel configModel = EnsureConfigInitialized();
			if (configModel == null || _audioEngine == null)
			{
				return;
			}
			SongInfo currentSong = _audioEngine.CurrentSong;
			if (currentSong == null)
			{
				return;
			}
                        configModel.LastPlayingSongId = currentSong.Id ?? string.Empty;
                        configModel.LastPlayingSongName = currentSong.Name ?? string.Empty;
                        configModel.LastPlayingDuration = currentSong.Duration;
                        configModel.LastPlayingSource = string.Empty;
                        configModel.LastPlayingSourceIndex = -1;
                        if (includeQueue && _playbackQueue != null)
                        {
				PlaybackSnapshot playbackSnapshot = _playbackQueue.CaptureSnapshot();
				if (playbackSnapshot != null && playbackSnapshot.Queue != null && playbackSnapshot.Queue.Count > 0)
				{
					configModel.LastPlayingQueue = (from s in playbackSnapshot.Queue
						where s != null && !string.IsNullOrWhiteSpace(s.Id)
						select s.Id).Take(300).ToList();
					configModel.LastPlayingQueueIndex = Math.Max(0, Math.Min(playbackSnapshot.QueueIndex, checked(configModel.LastPlayingQueue.Count - 1)));
					if (string.IsNullOrWhiteSpace(configModel.LastPlayingSource))
					{
						configModel.LastPlayingSource = playbackSnapshot.QueueSource ?? string.Empty;
					}
				}
				else
				{
					configModel.LastPlayingQueue = new List<string>();
					if (!string.IsNullOrWhiteSpace(currentSong.Id))
					{
						configModel.LastPlayingQueue.Add(currentSong.Id);
						configModel.LastPlayingQueueIndex = 0;
					}
					else
					{
						configModel.LastPlayingQueueIndex = -1;
					}
				}
			}
			if (string.IsNullOrWhiteSpace(configModel.LastPlayingSource))
			{
				configModel.LastPlayingSource = _playbackQueue?.QueueSource ?? string.Empty;
			}
			if (string.IsNullOrWhiteSpace(configModel.LastPlayingSource))
			{
				configModel.LastPlayingSource = currentSong.ViewSource ?? string.Empty;
			}
                        if (string.IsNullOrWhiteSpace(configModel.LastPlayingSource) && IsSongInCurrentView(currentSong))
                        {
                                configModel.LastPlayingSource = _currentViewSource ?? string.Empty;
                        }
                        int resolvedSourceIndex = -1;
                        if (!string.IsNullOrWhiteSpace(configModel.LastPlayingSource))
                        {
                                resolvedSourceIndex = ResolveSongIndexFromCache(configModel.LastPlayingSource, currentSong);
                                if (resolvedSourceIndex < 0 && !string.IsNullOrWhiteSpace(_currentViewSource) && string.Equals(_currentViewSource, configModel.LastPlayingSource, StringComparison.OrdinalIgnoreCase) && _currentSongs != null && _currentSongs.Count > 0)
                                {
                                        resolvedSourceIndex = FindSongIndexInList(_currentSongs, currentSong);
                                }
                        }
                        configModel.LastPlayingSourceIndex = resolvedSourceIndex;
                        if (persistToDisk)
                        {
                                _configManager?.Save(configModel);
                        }
		}
		catch (Exception ex)
		{
			Debug.WriteLine("[PlaybackState] 持久化失败: " + ex.Message);
		}
	}

	private async Task<bool> TryResumeLastPlaybackAsync()
	{
		if (_lastPlaybackRestored)
		{
			return false;
		}
		ConfigModel configModel = EnsureConfigInitialized();
		if (configModel == null || _apiClient == null || _audioEngine == null)
		{
			return false;
		}
		if (string.IsNullOrWhiteSpace(configModel.LastPlayingSongId))
		{
			return false;
		}
		try
		{
			List<string> queueIds = new List<string>();
			if (configModel.LastPlayingQueue != null && configModel.LastPlayingQueue.Count > 0)
			{
				queueIds.AddRange(configModel.LastPlayingQueue.Where((string value) => !string.IsNullOrWhiteSpace(value)));
			}
			if (!queueIds.Contains<string>(configModel.LastPlayingSongId, StringComparer.OrdinalIgnoreCase))
			{
				queueIds.Insert(Math.Max(0, Math.Min(configModel.LastPlayingQueueIndex, queueIds.Count)), configModel.LastPlayingSongId);
			}
			if (queueIds.Count == 0)
			{
				queueIds.Add(configModel.LastPlayingSongId);
			}
			List<SongInfo> songs = await _apiClient.GetSongDetailAsync(queueIds.ToArray());
			if (songs == null || songs.Count == 0)
			{
				return false;
			}
			Dictionary<string, SongInfo> map = songs.Where((SongInfo songInfo) => songInfo != null && !string.IsNullOrWhiteSpace(songInfo.Id)).GroupBy<SongInfo, string>((SongInfo songInfo) => songInfo.Id, StringComparer.OrdinalIgnoreCase).ToDictionary<IGrouping<string, SongInfo>, string, SongInfo>((IGrouping<string, SongInfo> g) => g.Key, (IGrouping<string, SongInfo> g) => g.First(), StringComparer.OrdinalIgnoreCase);
			List<SongInfo> ordered = new List<SongInfo>();
			foreach (string id in queueIds)
			{
				if (map.TryGetValue(id, out var s))
				{
					ordered.Add(s);
				}
				s = null;
			}
			if (ordered.Count == 0)
			{
				ordered = songs;
			}
			string resumeViewSource = (string.IsNullOrWhiteSpace(configModel.LastPlayingSource) ? "resume:last_playback" : configModel.LastPlayingSource);
			SongInfo target = ordered.FirstOrDefault((SongInfo songInfo) => string.Equals(songInfo.Id, configModel.LastPlayingSongId, StringComparison.OrdinalIgnoreCase)) ?? ordered.First();
			_playbackQueue.ManualSelect(target, ordered, resumeViewSource);
			await PlaySongDirectWithCancellation(target, isAutoPlayback: true);
			_lastPlaybackRestored = true;
			return true;
		}
		catch (Exception ex)
		{
			Debug.WriteLine("[PlaybackState] 恢复上次播放失败: " + ex.Message);
			return false;
		}
	}

        private void EnsureSearchHistoryAutoCompleteSource()
        {
                if (searchTextBox == null || _searchAutoCompleteDisabled)
                {
                        return;
                }

                if (_searchHistoryAutoComplete == null)
                {
                        _searchHistoryAutoComplete = new AutoCompleteStringCollection();
                }

                if (!searchTextBox.IsHandleCreated)
                {
                        if (!_searchAutoCompletePending)
                        {
                                searchTextBox.HandleCreated += SearchTextBox_HandleCreated;
                                _searchAutoCompletePending = true;
                        }
                        return;
                }

                TryApplySearchHistoryAutoComplete();
        }

        private void SearchTextBox_HandleCreated(object? sender, EventArgs e)
        {
                if (searchTextBox != null)
                {
                        searchTextBox.HandleCreated -= SearchTextBox_HandleCreated;
                }
                _searchAutoCompletePending = false;
                TryApplySearchHistoryAutoComplete();
        }

        private void TryApplySearchHistoryAutoComplete()
        {
                if (searchTextBox == null || _searchAutoCompleteDisabled)
                {
                        return;
                }

                if (searchTextBox is SafeTextBox safeTextBox && safeTextBox.IsAutoCompleteDisabled)
                {
                        _searchAutoCompleteDisabled = true;
                        return;
                }

                try
                {
                        searchTextBox.AutoCompleteCustomSource = _searchHistoryAutoComplete;
                        if (searchTextBox.AutoCompleteMode != AutoCompleteMode.Suggest)
                        {
                                searchTextBox.AutoCompleteMode = AutoCompleteMode.Suggest;
                        }
                        if (searchTextBox.AutoCompleteSource != AutoCompleteSource.CustomSource)
                        {
                                searchTextBox.AutoCompleteSource = AutoCompleteSource.CustomSource;
                        }
                }
                catch (Exception ex)
                {
                        _searchAutoCompleteDisabled = true;
                        DebugLogger.LogException("SearchHistory", ex, "搜索框自动完成初始化失败，已禁用");
                        DisableSearchHistoryAutoComplete();
                }
        }

        private void DisableSearchHistoryAutoComplete()
        {
                if (searchTextBox == null)
                {
                        return;
                }

                try
                {
                        searchTextBox.AutoCompleteMode = AutoCompleteMode.None;
                        searchTextBox.AutoCompleteSource = AutoCompleteSource.None;
                        searchTextBox.AutoCompleteCustomSource = null;
                }
                catch
                {
                }
        }

	private bool IsUserLoggedIn()
	{
		AccountState accountState = _accountState;
		if (accountState != null && accountState.IsLoggedIn)
		{
			return true;
		}
		NeteaseApiClient apiClient = _apiClient;
		if (apiClient != null && apiClient.UsePersonalCookie)
		{
			return GetCurrentUserId() > 0;
		}
		return false;
	}

	private void SyncConfigFromApiClient(LoginSuccessEventArgs? args = null, bool persist = false)
	{
		if (persist)
		{
			SaveConfig();
		}
	}

	private void ClearLoginState(bool persist, bool alreadyLoggedOut = false)
	{
		if (!alreadyLoggedOut && _apiClient != null)
		{
			try
			{
                                _apiClient.LogoutAsync().SafeFireAndForget("Logout");
			}
			catch (Exception ex)
			{
				Debug.WriteLine("[Login] 远端退出登录失败（继续清理本地）: " + ex.Message);
			}
		}
		else
		{
			_apiClient?.ResetToAnonymousSession(clearAccountState: false);
		}
		InvalidateLibraryCaches();
		if (persist)
		{
			SaveConfig(refreshCookieFromClient: false);
		}
		_accountState = _apiClient?.GetAccountStateSnapshot() ?? new AccountState
		{
			IsLoggedIn = false
		};
		_loggedInUserId = 0L;
		UpdateUiFromAccountState(reapplyCookies: false);
		ClearPlaybackReportingSession();
	}

	private void ApplyAccountStateOnStartup()
	{
		if (_apiClient == null)
		{
			_accountState = new AccountState
			{
				IsLoggedIn = false
			};
			UpdateUiFromAccountState(reapplyCookies: false);
			return;
		}
		_accountState = _apiClient.GetAccountStateSnapshot();
		CacheLoggedInUserId(ParseUserIdFromAccountState(_accountState));
		bool flag = _accountState?.IsLoggedIn ?? false;
		UpdateUiFromAccountState(flag);
		if (flag)
		{
			ScheduleLibraryStateRefresh();
		}
	}

	private void ReloadAccountState(bool reapplyCookies = false)
	{
		if (_apiClient == null)
		{
			_accountState = new AccountState
			{
				IsLoggedIn = false
			};
		}
		else
		{
			_accountState = _apiClient.GetAccountStateSnapshot();
		}
		long num = ParseUserIdFromAccountState(_accountState);
		if (num > 0)
		{
			CacheLoggedInUserId(num);
		}
		UpdateUiFromAccountState(reapplyCookies);
	}

	private void UpdateUiFromAccountState(bool reapplyCookies)
	{
		if (_accountState != null && _accountState.IsLoggedIn)
		{
			if (reapplyCookies && _accountState.Cookies != null && _accountState.Cookies.Count > 0)
			{
				try
				{
					_apiClient.ApplyCookies(_accountState.Cookies);
				}
				catch (Exception ex)
				{
					Debug.WriteLine("[AccountState] 重新应用Cookie失败: " + ex.Message);
				}
			}
			UpdateLoginMenuItemText();
			ScheduleLibraryStateRefresh();
		}
		else
		{
			UpdateLoginMenuItemText();
			InvalidateLibraryCaches();
		}

		RefreshQualityMenuAvailability();
	}

	private void SaveConfig(bool refreshCookieFromClient = true)
	{
		try
		{
			ConfigModel configModel = EnsureConfigInitialized();
			if (configModel == null || _configManager == null || _apiClient == null)
			{
				return;
			}
			if (volumeTrackBar != null)
			{
				int num = ((!volumeTrackBar.InvokeRequired) ? volumeTrackBar.Value : ((!volumeTrackBar.IsHandleCreated) ? volumeTrackBar.Value : volumeTrackBar.Invoke(() => volumeTrackBar.Value)));
				configModel.Volume = (double)num / 100.0;
			}
			configModel.LyricsReadingEnabled = _autoReadLyrics;
			configModel.SequenceNumberHidden = _hideSequenceNumbers;
			configModel.ControlBarHidden = _hideControlBar;
			configModel.PreventSleepDuringPlayback = _preventSleepDuringPlayback;
			configModel.FocusFollowPlayback = _focusFollowPlayback;
			UpdateWindowLayoutConfig(configModel);
			PersistPlaybackState(includeQueue: true, persistToDisk: false);
			_configManager.Save(configModel);
		}
		catch (Exception ex)
		{
			Debug.WriteLine("保存配置失败: " + ex.Message);
		}
	}

	private async Task EnsureLoginProfileAsync()
	{
		if (_apiClient == null || !_apiClient.UsePersonalCookie)
		{
			return;
		}
		try
		{
			LoginStatusResult status = await _apiClient.GetLoginStatusAsync();
			if (status == null || !status.IsLoggedIn)
			{
				Debug.WriteLine("[LoginState] GetLoginStatusAsync 返回未登录状态");
				return;
			}
			UserAccountInfo accountDetail = status.AccountDetail;
			ConfigModel config = EnsureConfigInitialized();
			if (config == null)
			{
				return;
			}
			string userIdString = status.AccountId?.ToString();
			if (string.IsNullOrEmpty(userIdString) && accountDetail != null && accountDetail.UserId != 0)
			{
				userIdString = accountDetail.UserId.ToString();
			}
			if (string.IsNullOrEmpty(userIdString))
			{
				userIdString = _accountState?.UserId;
			}
			string nickname = status.Nickname ?? accountDetail?.Nickname ?? _accountState?.Nickname;
			string avatarUrl = status.AvatarUrl ?? accountDetail?.AvatarUrl ?? _accountState?.AvatarUrl;
			int vipType = accountDetail?.VipType ?? status.VipType;
			bool nicknameChanged = !string.Equals(_accountState?.Nickname, nickname, StringComparison.Ordinal);
			bool userIdChanged = !string.Equals(_accountState?.UserId, userIdString, StringComparison.Ordinal);
			bool avatarChanged = !string.Equals(_accountState?.AvatarUrl, avatarUrl, StringComparison.Ordinal);
			AccountState accountState = _accountState;
			bool vipChanged = accountState == null || accountState.VipType != vipType;
			if ((nicknameChanged || userIdChanged || avatarChanged) && !base.IsDisposed)
			{
				if (base.IsHandleCreated)
				{
					BeginInvoke(UpdateLoginMenuItemText);
				}
				else
				{
					UpdateLoginMenuItemText();
				}
			}
			if (nicknameChanged || userIdChanged || avatarChanged || vipChanged)
			{
				SaveConfig();
			}
			long? profileId = null;
			if (long.TryParse(userIdString, out var parsedUserId))
			{
				profileId = parsedUserId;
			}
			UserAccountInfo profile = new UserAccountInfo
			{
				UserId = profileId.GetValueOrDefault(),
				Nickname = nickname,
				AvatarUrl = avatarUrl,
				VipType = vipType
			};
			if (profileId.HasValue)
			{
				CacheLoggedInUserId(profileId.Value);
			}
			_apiClient?.ApplyLoginProfile(profile);
		}
		catch (Exception ex)
		{
			Debug.WriteLine("[LoginState] 初始化登录状态失败: " + ex.Message);
		}
	}

	private void MainForm_KeyUp(object sender, KeyEventArgs e)
	{
		bool flag = false;
		if (e.KeyCode == Keys.Left)
		{
			_leftKeyPressed = false;
			_leftScrubActive = false;
			_leftKeyDownTime = DateTime.MinValue;
			flag = true;
		}
		else if (e.KeyCode == Keys.Right)
		{
			_rightKeyPressed = false;
			_rightScrubActive = false;
			_rightKeyDownTime = DateTime.MinValue;
			flag = true;
		}
		if (flag)
		{
			StopScrubKeyTimerIfIdle();
			if (!_leftKeyPressed && !_rightKeyPressed && _seekManager != null)
			{
				_seekManager.FinishSeek();
			}
		}
	}

	private void MainForm_Deactivate(object? sender, EventArgs e)
	{
		_leftKeyPressed = false;
		_rightKeyPressed = false;
		_leftScrubActive = false;
		_rightScrubActive = false;
		_leftKeyDownTime = DateTime.MinValue;
		_rightKeyDownTime = DateTime.MinValue;
		StopScrubKeyTimerIfIdle();
		if (!_isFormClosing)
		{
			_seekManager?.FinishSeek();
		}
	}

	private async void searchButton_Click(object sender, EventArgs e)
	{
		await PerformSearch();
	}

        private void backButton_Click(object sender, EventArgs e)
        {
                var args = new KeyEventArgs(Keys.Back);
                MainForm_KeyDown(this, args);
                if (!args.Handled)
                {
                        GoBackAsync();
                }
        }

	protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
	{
		if (keyData == (Keys.Control | Keys.Alt | Keys.Up))
		{
			if (TryHandleReorderShortcut(moveUp: true))
			{
				return true;
			}
		}
		if (keyData == (Keys.Control | Keys.Alt | Keys.Down))
		{
			if (TryHandleReorderShortcut(moveUp: false))
			{
				return true;
			}
		}
		if (keyData == (Keys.Control | Keys.Return))
		{
			if (TryHandleDownloadShortcut())
			{
				return true;
			}
		}
		if (keyData == Keys.Return)
		{
			TextBox textBox = searchTextBox;
			if (textBox == null || !textBox.ContainsFocus)
			{
				ComboBox comboBox = searchTypeComboBox;
				if (comboBox == null || !comboBox.ContainsFocus)
				{
					goto IL_0054;
				}
			}
			PerformSearch();
			return true;
		}
		goto IL_0054;
		IL_0054:
		return base.ProcessCmdKey(ref msg, keyData);
	}

	private void searchTextBox_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.KeyCode == Keys.Return && !e.Control && !e.Shift && !e.Alt)
		{
			e.Handled = true;
			e.SuppressKeyPress = true;
		}
		else if ((e.KeyCode == Keys.Up || e.KeyCode == Keys.Down) && NavigateSearchHistory(e.KeyCode == Keys.Up))
		{
			e.Handled = true;
			e.SuppressKeyPress = true;
		}
	}

	private void searchTextBox_TextChanged(object sender, EventArgs e)
	{
		if (!_isApplyingSearchHistoryText)
		{
			ResetSearchHistoryNavigation();
		}
	}

	private void searchTextContextMenu_Opening(object sender, CancelEventArgs e)
	{
		try
		{
			bool enabled = searchTextBox != null && searchTextBox.SelectionLength > 0;
			bool enabled2 = false;
			try
			{
				enabled2 = Clipboard.ContainsText();
			}
			catch (Exception ex)
			{
				Debug.WriteLine("[SearchHistory] Clipboard.ContainsText 异常: " + ex.Message);
			}
			ConfigModel configModel = _config ?? EnsureConfigInitialized();
			bool enabled3 = configModel?.SearchHistory != null && configModel.SearchHistory.Count > 0;
			if (searchCopyMenuItem != null)
			{
				searchCopyMenuItem.Enabled = enabled;
			}
			if (searchCutMenuItem != null)
			{
				searchCutMenuItem.Enabled = enabled;
			}
			if (searchPasteMenuItem != null)
			{
				searchPasteMenuItem.Enabled = enabled2;
			}
			if (searchClearHistoryMenuItem != null)
			{
				searchClearHistoryMenuItem.Enabled = enabled3;
			}
		}
		catch (Exception ex2)
		{
			Debug.WriteLine("[SearchHistory] 上下文菜单 Opening 异常: " + ex2.Message);
		}
	}

	private void searchCopyMenuItem_Click(object sender, EventArgs e)
	{
		try
		{
			if (searchTextBox != null && searchTextBox.SelectionLength > 0)
			{
				searchTextBox.Copy();
			}
		}
		catch (Exception ex)
		{
			Debug.WriteLine("[SearchHistory] 复制失败: " + ex.Message);
		}
	}

	private void searchCutMenuItem_Click(object sender, EventArgs e)
	{
		try
		{
			if (searchTextBox != null && searchTextBox.SelectionLength > 0)
			{
				searchTextBox.Cut();
				ResetSearchHistoryNavigation();
			}
		}
		catch (Exception ex)
		{
			Debug.WriteLine("[SearchHistory] 剪切失败: " + ex.Message);
		}
	}

	private void searchPasteMenuItem_Click(object sender, EventArgs e)
	{
		try
		{
			if (searchTextBox != null && Clipboard.ContainsText())
			{
				searchTextBox.Paste();
				ResetSearchHistoryNavigation();
			}
		}
		catch (Exception ex)
		{
			Debug.WriteLine("[SearchHistory] 粘贴失败: " + ex.Message);
		}
	}

	private void searchClearHistoryMenuItem_Click(object sender, EventArgs e)
	{
		ClearSearchHistory();
	}


	private bool ShouldUseVirtualSongList(List<SongInfo> songs)
	{
		return false;
	}

	private void DisableVirtualSongList()
	{
		MarkListViewLayoutDataChanged();
		_isVirtualSongListActive = false;
		_virtualSongs.Clear();
		_virtualStartIndex = 1;
		_virtualShowPagination = false;
		_virtualHasPreviousPage = false;
		_virtualHasNextPage = false;
		ResetVirtualItemCache();
		if (resultListView != null)
		{
			ResetListViewSelectionState();
			if (resultListView.VirtualMode)
			{
				resultListView.VirtualMode = false;
			}
			resultListView.VirtualListSize = 0;
		}
	}

	private void ConfigureVirtualSongList(List<SongInfo> songs, int startIndex, bool showPagination, bool hasPreviousPage, bool hasNextPage)
	{
		MarkListViewLayoutDataChanged();
		_isVirtualSongListActive = true;
		_virtualSongs = songs ?? new List<SongInfo>();
		_virtualStartIndex = Math.Max(1, startIndex);
		_virtualShowPagination = showPagination;
		_virtualHasPreviousPage = showPagination && hasPreviousPage;
		_virtualHasNextPage = showPagination && hasNextPage;
		ResetVirtualItemCache();
		if (resultListView != null)
		{
			int virtualSongListSize = GetVirtualSongListSize();
			if (virtualSongListSize <= 0)
			{
				ResetListViewSelectionState();
			}
			if (!resultListView.VirtualMode)
			{
				resultListView.VirtualMode = true;
			}
			resultListView.VirtualListSize = virtualSongListSize;
			resultListView.Invalidate();
			ScheduleResultListViewLayoutUpdate();
		}
	}

	private int GetVirtualSongListSize()
	{
		int num = _virtualSongs?.Count ?? 0;
		checked
		{
			if (_virtualShowPagination)
			{
				if (_virtualHasPreviousPage)
				{
					num++;
				}
				if (_virtualHasNextPage)
				{
					num++;
				}
				if (_virtualHasPreviousPage || _virtualHasNextPage)
				{
					num++;
				}
			}
			return num;
		}
	}

	private static string BuildPodcastCategoryFetchOffsetKey(int categoryId, int logicalOffset)
	{
		return categoryId.ToString() + ":" + logicalOffset.ToString();
	}

	private int ResolvePodcastCategoryFetchOffset(int categoryId, int logicalOffset)
	{
		if (_podcastCategoryFetchOffsets.TryGetValue(BuildPodcastCategoryFetchOffsetKey(categoryId, logicalOffset), out var fetchOffset))
		{
			return fetchOffset;
		}
		return logicalOffset;
	}

	private void SetPodcastCategoryFetchOffset(int categoryId, int logicalOffset, int fetchOffset)
	{
		_podcastCategoryFetchOffsets[BuildPodcastCategoryFetchOffsetKey(categoryId, logicalOffset)] = fetchOffset;
	}

	private void ClearPodcastCategoryFetchOffsets(int categoryId)
	{
		string prefix = categoryId.ToString() + ":";
		List<string> keysToRemove = new List<string>();
		foreach (string key in _podcastCategoryFetchOffsets.Keys)
		{
			if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
			{
				keysToRemove.Add(key);
			}
		}
		foreach (string key in keysToRemove)
		{
			_podcastCategoryFetchOffsets.Remove(key);
		}
	}

	private void ResetVirtualItemCache()
	{
		_virtualCacheStartIndex = -1;
		_virtualCacheEndIndex = -1;
		_virtualItemCache = null;
	}

	private ListViewItem? TryGetCachedVirtualItem(int index)
	{
		if (_virtualItemCache == null)
		{
			return null;
		}
		if (index < _virtualCacheStartIndex || index > _virtualCacheEndIndex)
		{
			return null;
		}
		int num = checked(index - _virtualCacheStartIndex);
		if (num < 0 || num >= _virtualItemCache.Length)
		{
			return null;
		}
		return _virtualItemCache[num];
	}

	private ListViewItem BuildVirtualItemByIndex(int index)
	{
		int num = _virtualSongs?.Count ?? 0;
		if (index >= 0 && index < num)
		{
			return BuildVirtualSongItem(index);
		}
		int num2 = num;
		if (_virtualShowPagination && _virtualHasPreviousPage)
		{
			if (index == num2)
			{
				return BuildPaginationItem("上一页", -2);
			}
			num2 = checked(num2 + 1);
		}
		if (_virtualShowPagination && _virtualHasNextPage)
		{
			if (index == num2)
			{
				return BuildPaginationItem("下一页", -3);
			}
			num2 = checked(num2 + 1);
		}
		if (_virtualShowPagination && (_virtualHasPreviousPage || _virtualHasNextPage) && index == num2)
		{
			return BuildPaginationItem("跳转", -4);
		}
		return new ListViewItem(new string[6])
		{
			Tag = null
		};
	}

	private ListViewItem BuildVirtualSongItem(int songIndex)
	{
		SongInfo songInfo = ((_virtualSongs != null && songIndex >= 0 && songIndex < _virtualSongs.Count) ? _virtualSongs[songIndex] : null);
		if (songInfo == null)
		{
			return new ListViewItem(new string[6])
			{
				Tag = null
			};
		}
		string text = BuildSongPrimaryText(songInfo);
		string formattedDuration = songInfo.FormattedDuration;
		ListViewItem listViewItem = new ListViewItem(new string[6]
		{
			string.Empty,
			FormatIndex(checked(_virtualStartIndex + songIndex)),
			text,
			string.IsNullOrWhiteSpace(songInfo.Artist) ? string.Empty : songInfo.Artist,
			string.IsNullOrWhiteSpace(songInfo.Album) ? string.Empty : songInfo.Album,
			formattedDuration
		})
		{
			Tag = songIndex
		};
		SetListViewItemPrimaryText(listViewItem, text);
		if (songInfo.IsAvailable == false)
		{
			listViewItem.ForeColor = SystemColors.GrayText;
			string text2 = (string.IsNullOrWhiteSpace(formattedDuration) ? "不可播放" : (formattedDuration + " (不可播放)"));
			listViewItem.SubItems[5].Text = text2;
			listViewItem.ToolTipText = "歌曲已下架或暂不可播放";
		}
		else
		{
			listViewItem.ForeColor = SystemColors.WindowText;
			listViewItem.ToolTipText = null;
		}
		return listViewItem;
	}

	private static ListViewItem BuildPaginationItem(string text, int tag)
	{
		ListViewItem item = new ListViewItem(new string[6]
		{
			string.Empty,
			text,
			string.Empty,
			string.Empty,
			string.Empty,
			string.Empty
		})
		{
			Tag = tag
		};
		SetListViewItemPrimaryText(item, text);
		return item;
	}

	private string GetVirtualItemPrimaryText(int index)
	{
		bool useSequenceCandidate = !(_hideSequenceNumbers || IsAlwaysSequenceHiddenView());
		return GetVirtualItemPrimaryText(index, useSequenceCandidate);
	}

	private string GetVirtualItemPrimaryText(int index, bool useSequenceCandidate)
	{
		if (index < 0)
		{
			return string.Empty;
		}
		int songCount = _virtualSongs?.Count ?? 0;
		if (index < songCount)
		{
			if (useSequenceCandidate)
			{
				return FormatIndex(checked(_virtualStartIndex + index));
			}
			SongInfo song = (_virtualSongs != null && index >= 0 && index < _virtualSongs.Count) ? _virtualSongs[index] : null;
			if (song == null)
			{
				return string.Empty;
			}
			if (string.Equals(song.Name, ListLoadingPlaceholderText, StringComparison.Ordinal))
			{
				return string.Empty;
			}
			return BuildSongPrimaryText(song);
		}
		int cursor = songCount;
		if (_virtualShowPagination && _virtualHasPreviousPage)
		{
			if (index == cursor)
			{
				return useSequenceCandidate ? "上一页" : string.Empty;
			}
			cursor = checked(cursor + 1);
		}
		if (_virtualShowPagination && _virtualHasNextPage)
		{
			if (index == cursor)
			{
				return useSequenceCandidate ? "下一页" : string.Empty;
			}
			cursor = checked(cursor + 1);
		}
		if (_virtualShowPagination && (_virtualHasPreviousPage || _virtualHasNextPage) && index == cursor)
		{
			return useSequenceCandidate ? "跳转" : string.Empty;
		}
		return string.Empty;
	}


	private int FindVirtualItemIndexByPrimaryText(string search, int startIndex, bool forward)
	{
		int total = GetVirtualSongListSize();
		if (total <= 0)
		{
			return -1;
		}
		bool useSequenceCandidate = !(_hideSequenceNumbers || IsAlwaysSequenceHiddenView());
		int start = Math.Max(0, Math.Min(startIndex, total - 1));
		StringComparison comparison = StringComparison.OrdinalIgnoreCase;
		if (forward)
		{
			for (int i = start; i < total; i++)
			{
				string text = GetVirtualItemPrimaryText(i, useSequenceCandidate);
				if (!string.IsNullOrEmpty(text) && text.StartsWith(search, comparison))
				{
					return i;
				}
			}
			for (int i = 0; i < start; i++)
			{
				string text = GetVirtualItemPrimaryText(i, useSequenceCandidate);
				if (!string.IsNullOrEmpty(text) && text.StartsWith(search, comparison))
				{
					return i;
				}
			}
			return -1;
		}
		for (int i = start; i >= 0; i--)
		{
			string text = GetVirtualItemPrimaryText(i, useSequenceCandidate);
			if (!string.IsNullOrEmpty(text) && text.StartsWith(search, comparison))
			{
				return i;
			}
		}
		for (int i = total - 1; i > start; i--)
		{
			string text = GetVirtualItemPrimaryText(i, useSequenceCandidate);
			if (!string.IsNullOrEmpty(text) && text.StartsWith(search, comparison))
			{
				return i;
			}
		}
		return -1;
	}



	private static string ResolveListViewPrimaryText(ListViewItem? item, bool useSequenceCandidate)
	{
		if (item == null)
		{
			return string.Empty;
		}
		if (useSequenceCandidate)
		{
			string sequence = (item.SubItems.Count > 1 ? item.SubItems[1].Text : item.Text) ?? string.Empty;
			return sequence.Trim();
		}

		string leftMostText = (item.SubItems.Count > 2 ? item.SubItems[2].Text : string.Empty) ?? string.Empty;
		string leftMostTrimmed = leftMostText.Trim();
		if (string.Equals(leftMostTrimmed, ListLoadingPlaceholderText, StringComparison.Ordinal))
		{
			return string.Empty;
		}
		if (!string.IsNullOrEmpty(leftMostTrimmed))
		{
			return leftMostTrimmed;
		}

		string fallback = (item.SubItems.Count > 1 ? item.SubItems[1].Text : item.Text) ?? string.Empty;
		return fallback.Trim();
	}


	private async Task<bool> ReloadCurrentSearchPageAsync(int targetPage)
	{
		if (string.IsNullOrWhiteSpace(_currentViewSource) || !_currentViewSource.StartsWith("search:", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		ParseSearchViewSource(_currentViewSource, out var parsedType, out var parsedKeyword, out var parsedPage);
		string keyword = ((!string.IsNullOrWhiteSpace(parsedKeyword)) ? parsedKeyword : ((!string.IsNullOrWhiteSpace(_lastKeyword)) ? _lastKeyword : searchTextBox.Text.Trim()));
		if (string.IsNullOrWhiteSpace(keyword))
		{
			return false;
		}
		string searchType = ((!string.IsNullOrWhiteSpace(parsedType)) ? parsedType : ((!string.IsNullOrWhiteSpace(_currentSearchType)) ? _currentSearchType : GetSelectedSearchType()));
		if (targetPage < 1)
		{
			targetPage = ((parsedPage <= 0) ? 1 : parsedPage);
		}
		string paginationKey = BuildSearchPaginationKey(keyword, searchType);
		bool clamped;
		int cappedMaxPage;
		int normalizedPage = NormalizePageWithCap(paginationKey, _resultsPerPage, targetPage, Math.Max(1, _maxPage), out clamped, out cappedMaxPage);
		if (clamped)
		{
			UpdateStatusBar($"页码过大，已跳到第 {normalizedPage} 页");
		}
		targetPage = normalizedPage;
		CancellationTokenSource searchCts = null;
		try
		{
			searchCts = BeginSearchOperation();
			await ExecuteSearchAsync(keyword, searchType, targetPage, skipSaveNavigation: true, showEmptyPrompt: false, searchCts.Token, 0).ConfigureAwait(continueOnCapturedContext: true);
			return true;
		}
		catch (OperationCanceledException)
		{
			UpdateStatusBar("搜索已取消");
			return false;
		}
		catch (Exception ex2)
		{
			Exception ex3 = ex2;
			Exception ex4 = ex3;
			if (!TryHandleOperationCancelled(ex4, "搜索加载已取消"))
			{
				Debug.WriteLine($"[Search] 重新加载搜索结果失败: {ex4}");
				MessageBox.Show("无法重新加载搜索结果: " + ex4.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("搜索加载失败");
			}
			return false;
		}
		finally
		{
			if (searchCts != null)
			{
				if (_searchCts == searchCts)
				{
					_searchCts = null;
				}
				searchCts.Dispose();
			}
		}
	}

	private async Task LoadLyrics(string songId, CancellationToken cancellationToken = default(CancellationToken))
	{
		try
		{
			if (cancellationToken.IsCancellationRequested)
			{
				return;
			}
			LyricsData lyricsData = await _lyricsLoader.LoadLyricsAsync(songId, cancellationToken);
			if (cancellationToken.IsCancellationRequested)
			{
				return;
			}
			_lyricsDisplayManager.LoadLyrics(lyricsData);
			CancelPendingLyricSpeech(resetSuppression: true, stopGlobalTts: false);
			if (lyricsData != null && !lyricsData.IsEmpty)
			{
				_currentLyrics = lyricsData.Lines.Select((EnhancedLyricLine line) => new LyricLine(line.Time, line.Text)).ToList();
			}
			else
			{
				_currentLyrics.Clear();
			}
		}
		catch (TaskCanceledException)
		{
			_lyricsDisplayManager.Clear();
			_currentLyrics.Clear();
			CancelPendingLyricSpeech(resetSuppression: true, stopGlobalTts: false);
		}
		catch (Exception ex2)
		{
			Exception ex3 = ex2;
			Exception ex4 = ex3;
			Debug.WriteLine("[Lyrics] 加载失败: " + ex4.Message);
			_lyricsDisplayManager.Clear();
			_currentLyrics.Clear();
			CancelPendingLyricSpeech(resetSuppression: true, stopGlobalTts: false);
		}
	}

	private void SyncPlayPauseButtonText()
	{
		DateTime now = DateTime.Now;
		if ((now - _lastSyncButtonTextTime).TotalMilliseconds < 50.0)
		{
			Debug.WriteLine("[SyncPlayPauseButtonText] 调用过快，跳过");
			return;
		}
		_lastSyncButtonTextTime = now;
		if (base.InvokeRequired)
		{
			try
			{
				BeginInvoke(SyncPlayPauseButtonText);
				return;
			}
			catch (ObjectDisposedException)
			{
				return;
			}
		}
		if (_audioEngine == null || playPauseButton == null || playPauseButton.IsDisposed)
		{
			return;
		}
		PlaybackState playbackState = _audioEngine.GetPlaybackState();
		string text = ((playbackState == PlaybackState.Playing) ? "暂停" : "播放");
		if (playPauseButton.Text != text)
		{
			playPauseButton.Text = text;
			Debug.WriteLine($"[SyncPlayPauseButtonText] 按钮文本已更新: {text} (状态={playbackState})");
		}
		UpdateTrayPlayPauseMenuTextForState(playbackState);
	}

	private void UpdateTrayPlayPauseMenuTextForState(PlaybackState playbackState)
	{
		if (trayPlayPauseMenuItem == null || trayPlayPauseMenuItem.IsDisposed)
		{
			return;
		}

		bool shouldShowPause = playbackState == PlaybackState.Playing
			|| playbackState == PlaybackState.Buffering
			|| playbackState == PlaybackState.Loading;
		string text = shouldShowPause ? "暂停(&P)" : "播放(&P)";
		if (trayPlayPauseMenuItem.Text != text)
		{
			trayPlayPauseMenuItem.Text = text;
			Debug.WriteLine("[UpdateTrayPlayPauseMenuText] 托盘菜单文本已更新: " + text + " (状态=" + playbackState + ")");
		}
	}

        private async Task TogglePlayPauseAsync()
        {
		if (_audioEngine == null)
		{
			return;
		}
		if (Interlocked.CompareExchange(ref _playPauseCommandInFlight, 1, 0) != 0)
		{
			Debug.WriteLine("[TogglePlayPause] 播放/暂停命令执行中，忽略重复请求");
			return;
		}
		try
		{
			switch (_audioEngine.GetPlaybackState())
			{
			case PlaybackState.Playing:
				TryCancelPendingLongSeek(false);
				PersistPlaybackState();
				await Task.Run(delegate
				{
					_audioEngine.Pause();
				}).ConfigureAwait(continueOnCapturedContext: true);
				break;
			case PlaybackState.Paused:
				TryCancelPendingLongSeek(true);
				await Task.Run(delegate
				{
					_audioEngine.Resume();
				}).ConfigureAwait(continueOnCapturedContext: true);
				break;
			default:
				if (!(await TryResumeLastPlaybackAsync()))
				{
					UpdateStatusBar("没有可恢复的播放记录");
				}
				break;
			}
		}
		finally
		{
			Interlocked.Exchange(ref _playPauseCommandInFlight, 0);
		}
	}

	private void StopPlayback()
	{
		if (_audioEngine != null)
		{
			_suppressAutoAdvance = true;
			_audioEngine.Stop();
			currentSongLabel.Text = "未播放";
			UpdateStatusBar("已停止");
			UpdatePlayButtonDescription(null);
			SyncPlayPauseButtonText();
			UpdateTrayIconTooltip(null);
		}
	}

        private void playPauseButton_Click(object sender, EventArgs e)
        {
                TogglePlayPauseAsync().SafeFireAndForget("PlayPause button");
        }

	private void UpdatePlayButtonDescription(SongInfo? song)
	{
		if (base.InvokeRequired)
		{
			BeginInvoke(new Action<SongInfo>(UpdatePlayButtonDescription), song);
			return;
		}
		bool flag = IsNarratorRunningCached();
		string text = ((!string.IsNullOrWhiteSpace(playPauseButton.Text)) ? playPauseButton.Text : "播放/暂停");
		if (song == null)
		{
			string text2 = "播放/暂停";
			if (playPauseButton.AccessibleDescription != text2)
			{
				playPauseButton.AccessibleDescription = text2;
				NotifyAccessibilityClients(playPauseButton, AccessibleEvents.DescriptionChange, -1);
			}
			string text3 = (flag ? text : null);
			if (playPauseButton.AccessibleName != text3)
			{
				playPauseButton.AccessibleName = text3;
				if (flag)
				{
					NotifyAccessibilityClients(playPauseButton, AccessibleEvents.NameChange, -1);
				}
			}
			UpdateWindowTitle(null);
			UpdateCurrentPlayingMenuItem(null);
			return;
		}
		string text4 = (song.IsTrial ? (song.Name + "(试听版)") : song.Name);
		string text5 = text4 + " - " + song.Artist;
		if (!string.IsNullOrEmpty(song.Album))
		{
			text5 = text5 + " [" + song.Album + "]";
		}
		if (!string.IsNullOrEmpty(song.Level))
		{
			string qualityDisplayName = NeteaseApiClient.GetQualityDisplayName(song.Level);
			text5 = text5 + " | " + qualityDisplayName;
		}
		if (playPauseButton.AccessibleDescription != text5)
		{
			playPauseButton.AccessibleDescription = text5;
			NotifyAccessibilityClients(playPauseButton, AccessibleEvents.DescriptionChange, -1);
		}
		string text6 = (flag ? (text + "，" + text5) : null);
		if (playPauseButton.AccessibleName != text6)
		{
			playPauseButton.AccessibleName = text6;
			if (flag)
			{
				NotifyAccessibilityClients(playPauseButton, AccessibleEvents.NameChange, -1);
			}
		}
		UpdateWindowTitle(text5);
		UpdateCurrentPlayingMenuItem(song);
		Debug.WriteLine("[MainForm] 更新播放按钮描述: " + text5);
	}

	private void UpdateCurrentPlayingMenuItem(SongInfo? song)
	{
		if (currentPlayingMenuItem != null)
		{
			if (base.InvokeRequired)
			{
				BeginInvoke(new Action<SongInfo>(UpdateCurrentPlayingMenuItem), song);
			}
			else
			{
				currentPlayingMenuItem.Visible = song != null;
			}
		}
	}

	private void UpdateWindowTitle(string? playbackDescription)
	{
		if (base.IsDisposed)
		{
			return;
		}
		if (base.InvokeRequired)
		{
			try
			{
				BeginInvoke(new Action<string>(UpdateWindowTitle), playbackDescription);
				return;
			}
			catch (ObjectDisposedException)
			{
				return;
			}
			catch (InvalidOperationException)
			{
				return;
			}
		}
		string text = ((string.IsNullOrWhiteSpace(playbackDescription) || playbackDescription == "\u64AD\u653E/\u6682\u505C") ? BaseWindowTitle : (BaseWindowTitle + " - " + playbackDescription));
		bool flag = !string.Equals(Text, text, StringComparison.Ordinal);
		if (flag)
		{
			Text = text;
		}
		bool flag2 = !string.Equals(base.AccessibleName, text, StringComparison.Ordinal);
		if (flag2)
		{
			base.AccessibleName = text;
		}
		if (base.IsHandleCreated)
		{
			try
			{
				NativeMethods.SetWindowText(base.Handle, text);
				NativeMethods.NotifyWinEvent(NativeMethods.EVENT_OBJECT_NAMECHANGE, base.Handle, NativeMethods.OBJID_WINDOW, NativeMethods.CHILDID_SELF);
			}
			catch (Exception ex)
			{
				Debug.WriteLine("[WindowTitle] Failed to sync native title: " + ex.Message);
			}
		}
		if (flag || flag2)
		{
			NotifyAccessibilityClients(this, AccessibleEvents.NameChange, -1);
		}
	}


        private void UpdateTimer_Tick(object sender, EventArgs e)
        {
		if (_audioEngine == null || _isUserDragging)
		{
			return;
		}
		double cachedPosition = GetCachedPosition();
		double cachedDuration = GetCachedDuration();
		if (cachedDuration > 0.0)
		{
			int num = checked((int)cachedDuration);
			if (progressTrackBar.Maximum != num)
			{
				progressTrackBar.Maximum = Math.Max(1, num);
				progressTrackBar.TickFrequency = Math.Max(1, num / 20);
			}
			int num2 = checked((int)cachedPosition);
                if (num2 >= 0 && num2 <= progressTrackBar.Maximum)
                {
                        progressTrackBar.Value = num2;
                }
                string text = FormatTimeFromSeconds(cachedPosition) + " / " + FormatTimeFromSeconds(cachedDuration);
                timeLabel.Text = text;
                progressTrackBar.AccessibleName = text;
        }
        else
        {
                progressTrackBar.Maximum = 1000;
                progressTrackBar.Value = 0;
                progressTrackBar.TickFrequency = 50;
                progressTrackBar.AccessibleName = "00:00 / 00:00";
        }
		if (_currentLyrics != null && _currentLyrics.Count > 0)
		{
			TimeSpan position = TimeSpan.FromSeconds(cachedPosition);
			LyricLine currentLyric = LyricsManager.GetCurrentLyric(_currentLyrics, position);
			if (currentLyric != null)
			{
				lyricsLabel.Text = currentLyric.Text;
			}
		}
		PlaybackState cachedPlaybackState = GetCachedPlaybackState();
		string text2 = ((cachedPlaybackState == PlaybackState.Playing) ? "暂停" : "播放");
		if (playPauseButton.Text != text2)
		{
			playPauseButton.Text = text2;
			Debug.WriteLine($"[UpdateTimer_Tick] ⚠\ufe0f 检测到按钮文本不一致，已自动修正: {text2} (状态={cachedPlaybackState})");
		}
		UpdateTrayPlayPauseMenuTextForState(cachedPlaybackState);
	}

        private void progressTrackBar_MouseDown(object sender, MouseEventArgs e)
        {
                _isUserDragging = true;
                Debug.WriteLine("[MainForm] 进度条拖动开始");
        }

        private void progressTrackBar_Scroll(object sender, EventArgs e)
        {
                if (_audioEngine != null)
                {
                        double cachedDuration = GetCachedDuration();
                        if (cachedDuration > 0.0)
                        {
                                double num = progressTrackBar.Value;
                                Debug.WriteLine($"[MainForm] 进度条 Scroll: {num:F1}s");
                                RequestSeekAndResetLyrics(num);
                        }
                }
        }

        private void progressTrackBar_MouseUp(object sender, MouseEventArgs e)  
        {
                _isUserDragging = false;
                Debug.WriteLine("[MainForm] 进度条拖动结束");
                if (_seekManager != null)
                {
                        _seekManager.FinishSeek();
                }
        }

	private void HandleDirectionalKeyDown(bool isRight)
	{
		PlaybackState playbackState = (_audioEngine != null) ? _audioEngine.GetPlaybackState() : PlaybackState.Stopped;
		bool canSeekInCurrentState = playbackState == PlaybackState.Playing
			|| playbackState == PlaybackState.Paused
			|| playbackState == PlaybackState.Buffering
			|| playbackState == PlaybackState.Loading;
		bool flag = !canSeekInCurrentState;
		if ((_isPlaybackLoading || IsSwitchingTrack) && flag)
		{
			Debug.WriteLine("[MainForm] " + (isRight ? "右" : "左") + "键快进快退被忽略：歌曲加载中");
			return;
		}
		if (_seekManager == null || _audioEngine == null)
		{
			Debug.WriteLine("[MainForm] " + (isRight ? "右" : "左") + "键快进快退被忽略：SeekManager或AudioEngine未初始化");
			return;
		}
		if (!canSeekInCurrentState)
		{
			Debug.WriteLine("[MainForm] " + (isRight ? "右" : "左") + "键快进快退被忽略：没有正在播放的歌曲");
			return;
		}
		DateTime now = DateTime.Now;
		if (isRight)
		{
			if (_rightKeyPressed)
			{
				return;
			}
			_rightKeyPressed = true;
			_rightScrubActive = false;
			_rightKeyDownTime = now;
			ScheduleSeek(5.0);
		}
		else
		{
			if (_leftKeyPressed)
			{
				return;
			}
			_leftKeyPressed = true;
			_leftScrubActive = false;
			_leftKeyDownTime = now;
			ScheduleSeek(-5.0);
		}
		StartScrubKeyTimer();
	}

	private void StartScrubKeyTimer()
	{
		if (_scrubKeyTimer != null && !_scrubKeyTimer.Enabled)
		{
			_scrubKeyTimer.Interval = 50;
			_scrubKeyTimer.Start();
		}
	}

	private void StopScrubKeyTimerIfIdle()
	{
		if (_scrubKeyTimer != null && !_leftKeyPressed && !_rightKeyPressed && _scrubKeyTimer.Enabled)
		{
			_scrubKeyTimer.Stop();
		}
	}

	private void ScrubKeyTimer_Tick(object sender, EventArgs e)
	{
		if (_scrubKeyTimer == null)
		{
			return;
		}
		if (!_leftKeyPressed && !_rightKeyPressed)
		{
			_scrubKeyTimer.Stop();
			return;
		}
		DateTime now = DateTime.Now;
		if (_leftKeyPressed)
		{
			if (!_leftScrubActive)
			{
				if ((now - _leftKeyDownTime).TotalMilliseconds >= 350.0)
				{
					_leftScrubActive = true;
					ScheduleSeek(-1.0, enableScrubbing: true);
				}
			}
			else
			{
				ScheduleSeek(-1.0, enableScrubbing: true);
			}
		}
		if (!_rightKeyPressed)
		{
			return;
		}
		if (!_rightScrubActive)
		{
			if ((now - _rightKeyDownTime).TotalMilliseconds >= 350.0)
			{
				_rightScrubActive = true;
				ScheduleSeek(1.0, enableScrubbing: true);
			}
		}
		else
		{
			ScheduleSeek(1.0, enableScrubbing: true);
		}
	}

	private void ScheduleSeek(double direction, bool enableScrubbing = false)
	{
		if (_audioEngine != null)
		{
			TryCancelPendingLongSeek();
			double enginePosition = _audioEngine.GetPosition();
			double engineDuration = _audioEngine.GetDuration();
			double cachedDuration = GetCachedDuration();
			double duration = (engineDuration > 0.0) ? engineDuration : cachedDuration;
			bool preferSeekTarget = _seekManager != null && (_seekManager.IsSeekingLong || _seekManager.HasPendingDeferredSeek);
			double basePosition = enginePosition;
			if (_positionCoordinator != null)
			{
				basePosition = _positionCoordinator.GetSeekBasePosition(enginePosition, duration, _audioEngine.IsPlaying, preferSeekTarget);
			}
			else if (enginePosition <= 0.0)
			{
				basePosition = GetCachedPosition();
			}
			double num = ((direction > 0.0) ? Math.Min(duration, basePosition + Math.Abs(direction)) : Math.Max(0.0, basePosition + direction));
			Debug.WriteLine($"[MainForm] 请求 Seek: {basePosition:F1}s → {num:F1}s (方向: {direction:+0;-0})");
			RequestSeekAndResetLyrics(num, enableScrubbing);
		}
	}

	private void PerformSeek(double targetPosition)
	{
		Debug.WriteLine($"[MainForm] 进度条拖动 Seek: {targetPosition:F1}s");
		RequestSeekAndResetLyrics(targetPosition);
	}

	private void OnSeekCompleted(object sender, bool success)
	{
		Debug.WriteLine($"[MainForm] Seek 序列完成，成功: {success}");
		if (progressTrackBar != null && progressTrackBar.InvokeRequired)
		{
			progressTrackBar.BeginInvoke(delegate
			{
				UpdateProgressTrackBarAccessibleName();
			});
		}
		else if (progressTrackBar != null)
		{
			UpdateProgressTrackBarAccessibleName();
		}
	}

	private void OnSeekRequested(object? sender, SeekManager.SeekRequestEventArgs e)
	{
		_positionCoordinator?.OnSeekRequested(e.TargetSeconds, e.IsPreview, e.Version);
	}

	private void OnSeekExecuted(object? sender, SeekManager.SeekExecutionEventArgs e)
	{
		_positionCoordinator?.OnSeekExecuted(e.TargetSeconds, e.Success, e.IsPreview, e.Version);
	}

	private void OnBufferingStateChanged(object sender, BufferingState state)
	{
		Debug.WriteLine($"[MainForm] 缓冲状态变化: {state}");
		HandleBufferingRecoveryStateChanged(state);
		if (base.InvokeRequired)
		{
			BeginInvoke(delegate
			{
				UpdatePlayButtonForBufferingState(state);
			});
		}
		else
		{
			UpdatePlayButtonForBufferingState(state);
		}
	}

	private void UpdatePlayButtonForBufferingState(BufferingState state)
	{
		if (playPauseButton == null || playPauseButton.IsDisposed)
		{
			return;
		}
		switch (state)
		{
		case BufferingState.Buffering:
			playPauseButton.Text = "缓冲中...";
			playPauseButton.Enabled = true;
			return;
		case BufferingState.Ready:
			playPauseButton.Text = "就绪";
			return;
		case BufferingState.Playing:
			playPauseButton.Text = "暂停";
			playPauseButton.Enabled = true;
			return;
		case BufferingState.LowBuffer:
			playPauseButton.Text = "缓冲中...";
			return;
		}
		if (_audioEngine != null && _audioEngine.IsPaused)
		{
			playPauseButton.Text = "播放";
		}
	}

	private void HandleBufferingRecoveryStateChanged(BufferingState state)
	{
		if (_audioEngine == null)
		{
			return;
		}

		string? songId = _audioEngine.CurrentSong?.Id;
		if (string.IsNullOrWhiteSpace(songId))
		{
			ResetBufferingRecoveryTracking();
			return;
		}

		DateTime nowUtc = DateTime.UtcNow;
		if (state == BufferingState.Buffering || state == BufferingState.LowBuffer)
		{
			bool shouldAttemptRecovery = false;
			lock (_bufferingRecoveryLock)
			{
				if (!string.Equals(_bufferingRecoverySongId, songId, StringComparison.Ordinal))
				{
					_bufferingRecoverySongId = songId;
					_bufferingRecoverySinceUtc = nowUtc;
					_bufferingRecoveryNoProgressSinceUtc = nowUtc;
					_bufferingRecoveryLastAttemptUtc = DateTime.MinValue;
					_bufferingRecoveryLastPositionSeconds = -1.0;
				}

				if (_bufferingRecoverySinceUtc == DateTime.MinValue)
				{
					_bufferingRecoverySinceUtc = nowUtc;
				}

				if (nowUtc - _bufferingRecoverySinceUtc >= BufferingRecoveryTriggerDuration
					&& nowUtc - _bufferingRecoveryLastAttemptUtc >= BufferingRecoveryRetryCooldown)
				{
					_bufferingRecoveryLastAttemptUtc = nowUtc;
					shouldAttemptRecovery = true;
				}
			}

			if (shouldAttemptRecovery)
			{
				TryScheduleBufferingRecovery(songId, $"buffering-state {state} persisted");
			}
		}
		else
		{
			lock (_bufferingRecoveryLock)
			{
				if (string.Equals(_bufferingRecoverySongId, songId, StringComparison.Ordinal))
				{
					_bufferingRecoverySinceUtc = DateTime.MinValue;
					_bufferingRecoveryNoProgressSinceUtc = DateTime.UtcNow;
				}
			}
		}
	}

	private void EvaluatePositionStallRecovery(TimeSpan position)
	{
		if (_audioEngine == null || _isApplicationExitRequested || IsSwitchingTrack || _isPlaybackLoading)
		{
			return;
		}

		SongInfo? currentSong = _audioEngine.CurrentSong;
		string? songId = currentSong?.Id;
		if (string.IsNullOrWhiteSpace(songId))
		{
			ResetBufferingRecoveryTracking();
			return;
		}

		PlaybackState state = _audioEngine.GetPlaybackState();
		if (state != PlaybackState.Playing && state != PlaybackState.Buffering && state != PlaybackState.Loading)
		{
			lock (_bufferingRecoveryLock)
			{
				if (string.Equals(_bufferingRecoverySongId, songId, StringComparison.Ordinal))
				{
					_bufferingRecoveryNoProgressSinceUtc = DateTime.MinValue;
					_bufferingRecoveryLastPositionSeconds = -1.0;
				}
			}
			return;
		}

		double currentSeconds = Math.Max(0.0, position.TotalSeconds);
		double durationSeconds = _audioEngine.GetDuration();
		if (durationSeconds > 0.0 && currentSeconds >= Math.Max(0.0, durationSeconds - 1.0))
		{
			return;
		}

		DateTime nowUtc = DateTime.UtcNow;
		bool shouldAttemptRecovery = false;
		lock (_bufferingRecoveryLock)
		{
			if (!string.Equals(_bufferingRecoverySongId, songId, StringComparison.Ordinal))
			{
				_bufferingRecoverySongId = songId;
				_bufferingRecoverySinceUtc = DateTime.MinValue;
				_bufferingRecoveryNoProgressSinceUtc = nowUtc;
				_bufferingRecoveryLastAttemptUtc = DateTime.MinValue;
				_bufferingRecoveryLastPositionSeconds = currentSeconds;
				return;
			}

			if (_bufferingRecoveryLastPositionSeconds < 0.0)
			{
				_bufferingRecoveryLastPositionSeconds = currentSeconds;
				_bufferingRecoveryNoProgressSinceUtc = nowUtc;
				return;
			}

			double delta = currentSeconds - _bufferingRecoveryLastPositionSeconds;
			_bufferingRecoveryLastPositionSeconds = currentSeconds;

			if (Math.Abs(delta) >= BufferingRecoveryProgressEpsilonSeconds)
			{
				_bufferingRecoveryNoProgressSinceUtc = nowUtc;
				return;
			}

			if (_bufferingRecoveryNoProgressSinceUtc == DateTime.MinValue)
			{
				_bufferingRecoveryNoProgressSinceUtc = nowUtc;
				return;
			}

			if (nowUtc - _bufferingRecoveryNoProgressSinceUtc >= BufferingRecoveryTriggerDuration
				&& nowUtc - _bufferingRecoveryLastAttemptUtc >= BufferingRecoveryRetryCooldown)
			{
				_bufferingRecoveryLastAttemptUtc = nowUtc;
				shouldAttemptRecovery = true;
			}
		}

		if (shouldAttemptRecovery)
		{
			TryScheduleBufferingRecovery(songId, $"position stalled at {currentSeconds:F1}s (state={state})");
		}
	}

	private void TryScheduleBufferingRecovery(string songId, string reason)
	{
		if (string.IsNullOrWhiteSpace(songId) || _isApplicationExitRequested || IsSwitchingTrack || _isPlaybackLoading)
		{
			return;
		}

		Debug.WriteLine($"[MainForm] Buffering recovery scheduled: songId={songId}, reason={reason}");
		AttemptBufferingRecoveryAsync(songId, reason).SafeFireAndForget("Buffering recovery");
	}

	private async Task AttemptBufferingRecoveryAsync(string songId, string reason)
	{
		if (string.IsNullOrWhiteSpace(songId) || _isApplicationExitRequested)
		{
			return;
		}

		if (Interlocked.CompareExchange(ref _bufferingRecoveryInFlight, 1, 0) != 0)
		{
			return;
		}

		try
		{
			if (_audioEngine == null)
			{
				return;
			}

			SongInfo? activeSong = _audioEngine.CurrentSong;
			if (activeSong == null || !string.Equals(activeSong.Id, songId, StringComparison.Ordinal))
			{
				return;
			}

			if (IsSwitchingTrack || _isPlaybackLoading)
			{
				Debug.WriteLine("[MainForm] Buffering recovery skipped: user playback switch in progress");
				return;
			}

			PlaybackState state = _audioEngine.GetPlaybackState();
			if (state == PlaybackState.Paused || state == PlaybackState.Stopped || state == PlaybackState.Idle)
			{
				return;
			}

			double resumePosition = Math.Max(0.0, _audioEngine.GetPosition());
			QualityLevel? lockedQuality = null;
			if (TryGetQualityLevelFromSongLevel(activeSong.Level, out QualityLevel parsedQuality))
			{
				lockedQuality = parsedQuality;
			}

			Debug.WriteLine($"[MainForm] Buffering recovery triggered: song={activeSong.Name}, state={state}, reason={reason}, resume={resumePosition:F1}s, quality={lockedQuality?.ToString() ?? "default"}");

			if (_seekManager != null)
			{
				try
				{
					_seekManager.CancelPendingSeeks();
				}
				catch (Exception ex)
				{
					Debug.WriteLine("[MainForm] Buffering recovery seek cancellation failed: " + ex.Message);
				}
			}

			activeSong.Url = string.Empty;
			activeSong.Size = 0;

			SafeInvoke(delegate
			{
				UpdateStatusBar("网络缓冲过久，正在尝试重新连接...");
			});

			await PlaySongDirectWithCancellation(activeSong, isAutoPlayback: true, requestedQuality: lockedQuality).ConfigureAwait(continueOnCapturedContext: false);

			if (_isApplicationExitRequested || _audioEngine == null || _audioEngine.CurrentSong == null || !string.Equals(_audioEngine.CurrentSong.Id, songId, StringComparison.Ordinal))
			{
				return;
			}

			if (resumePosition > 1.0)
			{
				double duration = _audioEngine.GetDuration();
				double targetPosition = duration > 0.0 ? Math.Min(resumePosition, Math.Max(0.0, duration - 1.0)) : resumePosition;
				SafeInvoke(delegate
				{
					RequestSeekAndResetLyrics(targetPosition);
				});
			}
		}
		catch (OperationCanceledException)
		{
			Debug.WriteLine("[MainForm] Buffering recovery canceled");
		}
		catch (Exception ex)
		{
			Debug.WriteLine("[MainForm] Buffering recovery failed: " + ex.Message);
		}
		finally
		{
			Interlocked.Exchange(ref _bufferingRecoveryInFlight, 0);
		}
	}

	private void ResetBufferingRecoveryTracking()
	{
		lock (_bufferingRecoveryLock)
		{
			_bufferingRecoverySongId = null;
			_bufferingRecoverySinceUtc = DateTime.MinValue;
			_bufferingRecoveryNoProgressSinceUtc = DateTime.MinValue;
			_bufferingRecoveryLastAttemptUtc = DateTime.MinValue;
			_bufferingRecoveryLastPositionSeconds = -1.0;
		}
	}


	private void UpdateProgressTrackBarAccessibleName()
        {
                try
                {
                        if (_audioEngine != null)
                        {
                                double position = _audioEngine.GetPosition();   
                                double duration = _audioEngine.GetDuration();   
                                string text = FormatTime(TimeSpan.FromSeconds(position));
                                string text2 = FormatTime(TimeSpan.FromSeconds(duration));
                                progressTrackBar.AccessibleName = text + " / " + text2;
                        }
                }
                catch (Exception ex)
                {
                        Debug.WriteLine("[MainForm] UpdateProgressTrackBarAccessibleName 异常: " + ex.Message);
                }
        }

        private void volumeTrackBar_Scroll(object sender, EventArgs e)
        {
                if (_audioEngine != null)
                {
                        float num = (float)volumeTrackBar.Value / 100f;
                        _audioEngine.SetVolume(num);
                        string text = $"{volumeTrackBar.Value}%";
                        volumeLabel.Text = text;
                        _config.Volume = num;
                        SaveConfig();
                }
        }

	private void volumeTrackBar_KeyDown(object sender, KeyEventArgs e)
	{
		checked
		{
			if (e.KeyCode == Keys.Up)
			{
				e.Handled = true;
				e.SuppressKeyPress = true;
				if (volumeTrackBar.Value < 100)
				{
					volumeTrackBar.Value = Math.Min(100, volumeTrackBar.Value + 2);
					volumeTrackBar_Scroll(volumeTrackBar, EventArgs.Empty);
				}
			}
			else if (e.KeyCode == Keys.Down)
			{
				e.Handled = true;
				e.SuppressKeyPress = true;
				if (volumeTrackBar.Value > 0)
				{
					volumeTrackBar.Value = Math.Max(0, volumeTrackBar.Value - 2);
					volumeTrackBar_Scroll(volumeTrackBar, EventArgs.Empty);
				}
			}
		}
	}

        private void progressTrackBar_KeyDown(object sender, KeyEventArgs e)
        {
                if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down || e.KeyCode == Keys.Left || e.KeyCode == Keys.Right || e.KeyCode == Keys.Prior || e.KeyCode == Keys.Next || e.KeyCode == Keys.Home || e.KeyCode == Keys.End)
                {
                        e.Handled = true;
                        e.SuppressKeyPress = true;
                }
        }

	private void OnAudioPositionChanged(object? sender, TimeSpan position)
	{
		EvaluatePositionStallRecovery(position);
		DetectLyricPositionJump(position);
		_lyricsDisplayManager?.UpdatePosition(position);
	}

	private void OnLyricUpdated(object? sender, LyricUpdateEventArgs e)
	{
		if (base.InvokeRequired)
		{
			BeginInvoke(delegate
			{
				OnLyricUpdated(sender, e);
			});
			return;
		}
		try
		{
			string formattedLyricText = _lyricsDisplayManager.GetFormattedLyricText(e.CurrentLine);
			lyricsLabel.Text = formattedLyricText;
			if (_autoReadLyrics && e.IsNewLine && e.CurrentLine != null)
			{
				HandleLyricAutoRead(e.CurrentLine);
			}
			if (e.IsNewLine && e.CurrentLine != null)
			{
				lyricsLabel.AccessibleName = "当前歌词: " + formattedLyricText;
				Debug.WriteLine("[Lyrics] 歌词更新: " + formattedLyricText);
			}
		}
		catch (Exception ex)
		{
			Debug.WriteLine("[Lyrics] 更新UI失败: " + ex.Message);
		}
	}

	private void HandleLyricAutoRead(EnhancedLyricLine currentLine)
	{
		if (_lyricsCacheManager == null || currentLine == null)
		{
			return;
		}
		if (_suppressLyricSpeech)
		{
			double num = _resumeLyricSpeechAtSeconds ?? double.MaxValue;
			double totalSeconds = currentLine.Time.TotalSeconds;
			if (!(totalSeconds + 0.05 >= num))
			{
				return;
			}
			_suppressLyricSpeech = false;
			_resumeLyricSpeechAtSeconds = null;
		}
		List<EnhancedLyricLine> list = _lyricsCacheManager.GetLineCluster(currentLine.Time, LyricsSpeechClusterTolerance);
		if (list == null || list.Count == 0)
		{
			list = new List<EnhancedLyricLine> { currentLine };
		}
		TimeSpan time = list[0].Time;
		if (_lastLyricSpeechAnchor.HasValue)
		{
			TimeSpan timeSpan = (time - _lastLyricSpeechAnchor.Value).Duration();
			if (timeSpan <= LyricsSpeechClusterTolerance)
			{
				return;
			}
		}
		List<string> list2 = (from line in list
			select line.Text into text
			where !string.IsNullOrWhiteSpace(text)
			select text).Distinct<string>(StringComparer.Ordinal).ToList();
		if (list2.Count != 0)
		{
			_lastLyricSpeechAnchor = time;
			QueueLyricSpeech(list2);
		}
	}

	private void QueueLyricSpeech(List<string> segments)
	{
		if (segments == null || segments.Count == 0)
		{
			return;
		}
		string textToSpeak = string.Join("，", segments);
		CancellationToken token;
		lock (_lyricsSpeechLock)
		{
			if (_lyricsSpeechCts == null)
			{
				_lyricsSpeechCts = new CancellationTokenSource();
			}
			token = _lyricsSpeechCts.Token;
		}
		Task.Run(delegate
		{
			try
			{
				if (!token.IsCancellationRequested && !string.IsNullOrWhiteSpace(textToSpeak))
				{
					bool flag = TtsHelper.SpeakText(textToSpeak, interrupt: false);
					Debug.WriteLine("[TTS] Speak '" + textToSpeak + "': " + (flag ? "成功" : "失败"));
				}
			}
			catch (OperationCanceledException)
			{
				Debug.WriteLine("[TTS] 歌词朗读任务被取消");
			}
		}, token);
	}

	private void CancelPendingLyricSpeech(bool resetSuppression = true, bool stopGlobalTts = true)
	{
		lock (_lyricsSpeechLock)
		{
			if (_lyricsSpeechCts != null)
			{
				_lyricsSpeechCts.Cancel();
				_lyricsSpeechCts.Dispose();
				_lyricsSpeechCts = null;
			}
		}
		if (stopGlobalTts)
		{
			TtsHelper.StopSpeaking();
		}
		_lastLyricSpeechAnchor = null;
		_lastLyricPlaybackPosition = null;
		if (resetSuppression)
		{
			_suppressLyricSpeech = false;
			_resumeLyricSpeechAtSeconds = null;
		}
	}

	private void DetectLyricPositionJump(TimeSpan position)
	{
		if (!_autoReadLyrics)
		{
			_lastLyricPlaybackPosition = position;
			return;
		}
		if (_lastLyricPlaybackPosition.HasValue)
		{
			double num = Math.Abs((position - _lastLyricPlaybackPosition.Value).TotalSeconds);
			if (num >= LyricJumpThreshold.TotalSeconds)
			{
				CancelPendingLyricSpeech(resetSuppression: false);
				BeginLyricSeekSuppression(position.TotalSeconds);
			}
		}
		_lastLyricPlaybackPosition = position;
	}

	private void BeginLyricSeekSuppression(double targetPosition)
	{
		_suppressLyricSpeech = true;
		_resumeLyricSpeechAtSeconds = targetPosition;
	}

	private void RequestSeekAndResetLyrics(double targetPosition, bool preview = false)
	{
		TryCancelPendingLongSeek();
		CancelPendingLyricSpeech(resetSuppression: false);
		BeginLyricSeekSuppression(targetPosition);
		_lastLyricPlaybackPosition = TimeSpan.FromSeconds(targetPosition);
		if (_seekManager != null)
		{
			_seekManager.RequestSeek(targetPosition, preview);
		}
		else
		{
			_audioEngine?.SetPosition(targetPosition);
		}
	}

	private bool TryCancelPendingLongSeek(bool? resumePlayback = null)
	{
		if (_seekManager == null || _audioEngine == null)
		{
			return false;
		}
		if (!_seekManager.IsSeekingLong && !_seekManager.HasPendingDeferredSeek)
		{
			return false;
		}
		Debug.WriteLine("[MainForm] 检测到长跳等待，执行取消/恢复播放状态");
		return _seekManager.CancelPendingLongSeek(resumePlayback);
	}

	private void AudioEngine_PlaybackStopped(object sender, EventArgs e)
	{
		Debug.WriteLine("[MainForm] AudioEngine_PlaybackStopped 被调用");
		if (base.InvokeRequired)
		{
			Debug.WriteLine("[MainForm] 需要切换到 UI 线程");
			BeginInvoke(delegate
			{
				AudioEngine_PlaybackStopped(sender, e);
			});
			return;
		}
		Debug.WriteLine($"[MainForm] 当前播放模式: {_audioEngine?.PlayMode}");
		CompleteActivePlaybackSession(PlaybackEndReason.Stopped);
		SyncPlayPauseButtonText();
		UpdateTrayIconTooltip(null);
		bool suppressAutoAdvance = _suppressAutoAdvance;
		if (suppressAutoAdvance)
		{
			Debug.WriteLine("[MainForm] 自动跳转已被手动播放停止抑制");
			_suppressAutoAdvance = false;
			return;
		}
		BassAudioEngine audioEngine = _audioEngine;
		if (audioEngine != null && audioEngine.PlayMode == PlayMode.LoopOne)
		{
			SongInfo currentSong = _audioEngine.CurrentSong;
			Debug.WriteLine("[MainForm WARNING] 单曲循环后备处理被调用，歌曲: " + currentSong?.Name);
			if (currentSong != null)
			{
                                PlaySongDirectAsync(currentSong).SafeFireAndForget("Loop one fallback");
			}
			else
			{
				Debug.WriteLine("[MainForm ERROR] 单曲循环后备处理失败：CurrentSong 为 null");
			}
		}
		else if (!suppressAutoAdvance)
		{
			Debug.WriteLine("[MainForm] 调用 PlayNext() (自动播放)");
                        PlayNextAsync(isManual: false).SafeFireAndForget("Auto advance");
		}
	}

	private void AudioEngine_PlaybackEnded(object sender, SongInfo? e)
	{
		Debug.WriteLine("[MainForm] AudioEngine_PlaybackEnded 被调用，歌曲: " + e?.Name);
		if (base.InvokeRequired)
		{
			try
			{
				BeginInvoke(delegate
				{
					AudioEngine_PlaybackEnded(sender, e);
				});
				return;
			}
			catch (ObjectDisposedException)
			{
				Debug.WriteLine("[MainForm] 窗口已关闭，忽略 PlaybackEnded 事件");
				return;
			}
			catch (InvalidOperationException)
			{
				Debug.WriteLine("[MainForm] BeginInvoke 失败，窗口可能已关闭");
				return;
			}
		}
		PlayMode playMode = _audioEngine?.PlayMode ?? PlayMode.Loop;
		Debug.WriteLine($"[MainForm] 播放模式: {playMode}");
		if (e != null)
		{
			CompleteActivePlaybackSession(PlaybackEndReason.Completed, e.Id);
		}
		if (playMode == PlayMode.LoopOne && e != null)
		{
			Debug.WriteLine("[MainForm] 单曲循环，重新播放当前歌曲");
			PlaySongDirectWithCancellation(e, isAutoPlayback: true);
		}
		else
		{
			Debug.WriteLine("[MainForm] 调用 PlayNext() (自动播放)");
                        PlayNextAsync(isManual: false).SafeFireAndForget("Auto advance");
		}
	}

	private void AudioEngine_GaplessTransitionCompleted(object sender, GaplessTransitionEventArgs e)
	{
		if (base.IsDisposed)
		{
			return;
		}
		if (base.InvokeRequired)
		{
			try
			{
				BeginInvoke(delegate
				{
					AudioEngine_GaplessTransitionCompleted(sender, e);
				});
				return;
			}
			catch (ObjectDisposedException)
			{
				return;
			}
			catch (InvalidOperationException)
			{
				return;
			}
		}
		if (e?.NextSong != null)
		{
			if (e.PreviousSong != null)
			{
				CompleteActivePlaybackSession(PlaybackEndReason.Completed, e.PreviousSong.Id);
			}
			BeginPlaybackReportingSession(e.NextSong);
			SongInfo nextSong = e.NextSong;
			PlayMode playMode = _audioEngine?.PlayMode ?? PlayMode.Loop;
			PlaybackMoveResult playbackMoveResult = _playbackQueue.AdvanceForPlayback(nextSong, playMode, _currentViewSource);
			switch (playbackMoveResult.Route)
			{
			case PlaybackRoute.Queue:
			case PlaybackRoute.ReturnToQueue:
				UpdateFocusForQueue(playbackMoveResult.QueueIndex, nextSong);
				Debug.WriteLine($"[MainForm] 无缝切歌焦点跟随（队列）: 索引={playbackMoveResult.QueueIndex}, 歌曲={nextSong.Name}");
				break;
			case PlaybackRoute.Injection:
			case PlaybackRoute.PendingInjection:
				UpdateFocusForInjection(nextSong, playbackMoveResult.InjectionIndex);
				Debug.WriteLine($"[MainForm] 无缝切歌焦点跟随（插播）: 索引={playbackMoveResult.InjectionIndex}, 歌曲={nextSong.Name}");
				break;
			default:
				Debug.WriteLine($"[MainForm] 无缝切歌：未匹配焦点跟随路由，Route={playbackMoveResult.Route}");
				break;
			}
			string message = (nextSong.IsTrial ? ("正在播放: " + nextSong.Name + " [试听版]") : ("正在播放: " + nextSong.Name));
			UpdateStatusBar(message);
			SafeInvoke(delegate
			{
				string text = (nextSong.IsTrial ? (nextSong.Name + "(试听版)") : nextSong.Name);
				currentSongLabel.Text = text + " - " + nextSong.Artist;
				playPauseButton.Text = "暂停";
				UpdatePlayButtonDescription(nextSong);
				UpdateTrayIconTooltip(nextSong);
				SyncPlayPauseButtonText();
			});
			_lyricsDisplayManager?.Clear();
			_currentLyrics?.Clear();
			LoadLyrics(nextSong.Id);
			SafeInvoke(delegate
			{
				RefreshNextSongPreload();
			});
		}
	}

        private async Task PlaySongDirectAsync(SongInfo song)
        {
		if (song == null)
		{
			throw new ArgumentNullException("song");
		}
		try
		{
			Debug.WriteLine("[MainForm] PlaySongDirectAsync 开始播放: " + song.Name);
			await PlaySongDirectWithCancellation(song);
		}
                catch (Exception ex)
                {
                        Debug.WriteLine("[MainForm ERROR] PlaySongDirectAsync 异常: " + ex.Message);
                        AnnouncePlaybackFailure(ex.Message);
                }
        }

	private void ClearAllSongUrlCache()
	{
		int num = 0;
		checked
		{
			try
			{
				IReadOnlyList<SongInfo> readOnlyList = _playbackQueue?.CurrentQueue;
				if (readOnlyList != null)
				{
					foreach (SongInfo item in readOnlyList)
					{
						if (item != null && !string.IsNullOrEmpty(item.Url))
						{
							item.Url = string.Empty;
							item.Level = string.Empty;
							item.Size = 0L;
							item.IsAvailable = null;
							num++;
						}
					}
				}
				IReadOnlyList<SongInfo> readOnlyList2 = _playbackQueue?.InjectionChain;
				if (readOnlyList2 != null)
				{
					foreach (SongInfo item2 in readOnlyList2)
					{
						if (item2 != null && !string.IsNullOrEmpty(item2.Url))
						{
							item2.Url = string.Empty;
							item2.Level = string.Empty;
							item2.Size = 0L;
							item2.IsAvailable = null;
							num++;
						}
					}
				}
				Debug.WriteLine($"[Quality] 已清除 {num} 首歌曲的URL缓存");
			}
			catch (Exception ex)
			{
				Debug.WriteLine("[Quality] 清除URL缓存时出错: " + ex.Message);
			}
		}
	}

	private void RefreshNextSongPreload()
	{
		try
		{
			string qualityName = _config?.DefaultQuality ?? "超清母带";
			QualityLevel qualityLevelFromName = NeteaseApiClient.GetQualityLevelFromName(qualityName);
			RecursivePreloadNextAvailableAsync(qualityLevelFromName);
		}
		catch (Exception ex)
		{
			Debug.WriteLine("[MainForm] 预加载失败: " + ex.Message);
		}
	}

	private async Task<bool> RecursivePreloadNextAvailableAsync(QualityLevel quality, int maxAttempts = 10)
	{
		for (int attempt = 0; attempt < maxAttempts; attempt++)
		{
			SongInfo nextSong = PredictNextSong();
			if (nextSong == null)
			{
				Debug.WriteLine($"[MainForm] Preload: no candidate song ({attempt + 1}/{maxAttempts})");
				return false;
			}
			Debug.WriteLine($"[MainForm] Preload attempt {attempt + 1}: {nextSong.Name}, IsAvailable={nextSong.IsAvailable}");
			if (nextSong.IsAvailable != true)
			{
				try
				{
					using CancellationTokenSource resolveCts = new CancellationTokenSource(TimeSpan.FromSeconds(20.0));
					SongResolveResult resolveResult = await ResolveSongPlaybackAsync(nextSong, quality, resolveCts.Token, suppressStatusUpdates: true).ConfigureAwait(continueOnCapturedContext: false);
					if (resolveResult.Status == SongResolveStatus.NotAvailable)
					{
						nextSong.IsAvailable = false;
						Debug.WriteLine("[MainForm] Preload skip not-available song (after unblock flow): " + nextSong.Name);
						continue;
					}
					if (resolveResult.Status != SongResolveStatus.Success)
					{
						Debug.WriteLine("[MainForm] Preload resolve failed, try next song: " + nextSong.Name + ", status=" + resolveResult.Status);
						continue;
					}
				}
				catch (OperationCanceledException)
				{
					Debug.WriteLine("[MainForm] Preload resolve canceled/timeout, try next song: " + nextSong.Name);
					continue;
				}
				catch (Exception ex)
				{
					Debug.WriteLine("[MainForm] Preload resolve exception: " + nextSong.Name + ", " + ex.Message);
					continue;
				}
			}
			if (nextSong.IsAvailable == false)
			{
				Debug.WriteLine("[MainForm] Preload skip unavailable song: " + nextSong.Name);
				continue;
			}
			SongInfo currentSong = _audioEngine?.CurrentSong;
			if (currentSong != null)
			{
				_nextSongPreloader?.CleanupStaleData(currentSong.Id, nextSong.Id);
			}
			Debug.WriteLine("[MainForm] Start preloading candidate: " + nextSong.Name);
			if (_nextSongPreloader == null)
			{
				Debug.WriteLine("[MainForm] Preloader is not initialized");
				return false;
			}
			if (await _nextSongPreloader.StartPreloadAsync(nextSong, quality))
			{
				PreloadedData gaplessData = _nextSongPreloader.TryGetPreloadedData(nextSong.Id);
				if (gaplessData != null)
				{
					_audioEngine?.RegisterGaplessPreload(nextSong, gaplessData);
				}
				Debug.WriteLine("[MainForm] Preload succeeded: " + nextSong.Name);
				return true;
			}
			Debug.WriteLine("[MainForm] Preload failed, continue: " + nextSong.Name);
		}
		Debug.WriteLine($"[MainForm] Preload exhausted after {maxAttempts} attempts");
		return false;
	}

	private async Task BatchCheckSongsAvailabilityAsync(List<SongInfo> songs, CancellationToken cancellationToken)
	{
		if (songs == null || songs.Count == 0)
		{
			return;
		}
		try
		{
			List<SongInfo> uncheckedSongs = songs.Where((SongInfo s) => !s.IsAvailable.HasValue).ToList();
			if (uncheckedSongs.Count == 0)
			{
				Debug.WriteLine("[StreamCheck] All songs are already checked, skip");
			}
			else
			{
				if (cancellationToken.IsCancellationRequested)
				{
					return;
				}
				Debug.WriteLine($"[StreamCheck] Start stream availability check for {uncheckedSongs.Count} songs");
				QualityLevel selectedQuality = GetCurrentQuality();
				string[] ids = (from s in uncheckedSongs
					select s.Id into id
					where !string.IsNullOrWhiteSpace(id)
					select id).ToArray();
				if (ids.Length == 0)
				{
					return;
				}
				ConcurrentDictionary<string, SongInfo> songLookup = new ConcurrentDictionary<string, SongInfo>(uncheckedSongs.Where((SongInfo s) => !string.IsNullOrWhiteSpace(s.Id)).ToDictionary<SongInfo, string, SongInfo>((SongInfo s) => s.Id, (SongInfo s) => s, StringComparer.Ordinal), StringComparer.Ordinal);
				ConcurrentDictionary<string, SongInfo> unblockCandidates = new ConcurrentDictionary<string, SongInfo>(StringComparer.Ordinal);
				int available = 0;
				int unavailable = 0;
				await _apiClient.BatchCheckSongsAvailabilityStreamAsync(ids, selectedQuality, delegate(string songId, bool isAvailable)
				{
					if (!cancellationToken.IsCancellationRequested && songLookup.TryGetValue(songId, out var value))
					{
						if (isAvailable)
						{
							value.IsAvailable = true;
							Interlocked.Increment(ref available);
						}
						else
						{
							value.IsAvailable = null;
							unblockCandidates.TryAdd(songId, value);
						}
					}
				}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				if (!cancellationToken.IsCancellationRequested && unblockCandidates.Count > 0)
				{
					int recoveredByUnblock = 0;
					foreach (SongInfo candidate in unblockCandidates.Values)
					{
						cancellationToken.ThrowIfCancellationRequested();
						if (await TryRecoverSongAvailabilityByUnblockAsync(candidate, selectedQuality, cancellationToken).ConfigureAwait(continueOnCapturedContext: false))
						{
							recoveredByUnblock++;
							continue;
						}
						candidate.IsAvailable = false;
						unavailable++;
						Debug.WriteLine("[StreamCheck] Mark unavailable after unblock fallback: " + candidate.Name);
					}
					available += recoveredByUnblock;
					Debug.WriteLine($"[StreamCheck] Unblock fallback recovered {recoveredByUnblock} songs");
				}
				if (!cancellationToken.IsCancellationRequested)
				{
					Debug.WriteLine($"[StreamCheck] Completed: {available} available, {unavailable} unavailable");
					RefreshAvailabilityIndicatorsInCurrentView();
				}
			}
		}
		catch (OperationCanceledException)
		{
			Debug.WriteLine("[StreamCheck] Availability check canceled");
		}
		catch (Exception ex2)
		{
			Debug.WriteLine("[StreamCheck] Availability stream check failed: " + ex2.Message);
		}
	}

	private void UpdateStatusBar(string? message)
        {
                if (message != null)
                {
                        if (statusStrip1.InvokeRequired)
                        {
                                statusStrip1.Invoke(new Action<string>(UpdateStatusBar), message);
                        }
                        else if (toolStripStatusLabel1 != null)
                        {
                                toolStripStatusLabel1.Text = message;
                                UpdateStatusStripAccessibility(message);
                        }
                }
        }

        private void AnnounceStatusBarMessage(string? message)
        {
                if (message == null)
                {
                        return;
                }
                if (statusStrip1.InvokeRequired)
                {
                        statusStrip1.Invoke(new Action<string>(AnnounceStatusBarMessage), message);
                        return;
                }
                UpdateStatusBar(message);
                AnnounceStatusStripAccessibility(message);
        }

        private void AnnouncePlaybackFailure(string? reason = null)
        {
                string text = string.IsNullOrWhiteSpace(reason) ? "播放失败" : ("播放失败: " + reason);
                AnnounceStatusBarMessage(text);
        }

	private long? GetCurrentSourceId(SongInfo song)
	{
		try
		{
			if (_currentPlaylist != null && !string.IsNullOrEmpty(_currentPlaylist.Id) && long.TryParse(_currentPlaylist.Id, out var result))
			{
				Debug.WriteLine($"[MainForm] GetCurrentSourceId: 使用歌单ID={result}");
				return result;
			}
			if (song != null && !string.IsNullOrEmpty(song.AlbumId) && long.TryParse(song.AlbumId, out var result2))
			{
				Debug.WriteLine($"[MainForm] GetCurrentSourceId: 使用专辑ID={result2} (歌曲: {song.Name})");
				return result2;
			}
			Debug.WriteLine("[MainForm] GetCurrentSourceId: ⚠\ufe0f 无法获取有效的 sourceId（既无歌单也无专辑）");
			return null;
		}
		catch (Exception ex)
		{
			Debug.WriteLine("[MainForm] GetCurrentSourceId 异常: " + ex.Message);
			return null;
		}
	}

	private void SetPlaybackLoadingState(bool isLoading, string? statusMessage = null)
	{
		if (base.InvokeRequired)
		{
			BeginInvoke(new Action<bool, string>(SetPlaybackLoadingState), isLoading, statusMessage);
			return;
		}
		if (isLoading)
		{
			if (!_isPlaybackLoading)
			{
				_isPlaybackLoading = true;
				_playButtonTextBeforeLoading = playPauseButton?.Text;
				if (playPauseButton != null)
				{
					playPauseButton.Text = "加载中...";
					playPauseButton.Enabled = false;
				}
                                if (toolStripStatusLabel1 != null)
                                {
                                        _statusTextBeforeLoading = toolStripStatusLabel1.Text;
                                }
			}
			if (!string.IsNullOrEmpty(statusMessage))
			{
				UpdateStatusBar(statusMessage);
			}
			return;
		}
		if (!_isPlaybackLoading)
		{
			if (!string.IsNullOrEmpty(statusMessage))
			{
				UpdateStatusBar(statusMessage);
			}
			return;
		}
		_isPlaybackLoading = false;
		if (!string.IsNullOrEmpty(statusMessage))
		{
			UpdateStatusBar(statusMessage);
		}
		else if (!string.IsNullOrEmpty(_statusTextBeforeLoading))
		{
			UpdateStatusBar(_statusTextBeforeLoading);
		}
		if (playPauseButton != null)
		{
			if (!string.IsNullOrEmpty(_playButtonTextBeforeLoading))
			{
				playPauseButton.Text = _playButtonTextBeforeLoading;
			}
			else
			{
				SyncPlayPauseButtonText();
			}
			playPauseButton.Enabled = true;
		}
		_playButtonTextBeforeLoading = null;
		_statusTextBeforeLoading = null;
	}

	private string FormatTime(TimeSpan time)
	{
		return $"{checked((int)time.TotalMinutes):D2}:{time.Seconds:D2}";
	}

	private string FormatTimeFromSeconds(double seconds)
	{
		checked
		{
			int value = (int)(seconds / 60.0);
			int value2 = (int)(seconds % 60.0);
			return $"{value:D2}:{value2:D2}";
		}
	}

[DllImport("user32.dll")]
private static extern bool SetForegroundWindow(nint hWnd);

	[DllImport("user32.dll")]
	private static extern bool ShowWindow(nint hWnd, int nCmdShow);

	private const uint POWER_REQUEST_CONTEXT_VERSION = 0;

	private const uint POWER_REQUEST_CONTEXT_SIMPLE_STRING = 1;

	private enum PowerRequestType
	{
		PowerRequestDisplayRequired = 0,
		PowerRequestSystemRequired = 1,
		PowerRequestAwayModeRequired = 2,
		PowerRequestExecutionRequired = 3
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct ReasonContext
	{
		public uint Version;
		public uint Flags;
		public IntPtr ReasonString;
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr PowerCreateRequest(ref ReasonContext context);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool PowerSetRequest(IntPtr powerRequest, PowerRequestType requestType);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool PowerClearRequest(IntPtr powerRequest, PowerRequestType requestType);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool CloseHandle(IntPtr handle);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern uint SetThreadExecutionState(uint esFlags);

	private const uint ES_CONTINUOUS = 0x80000000;
	private const uint ES_SYSTEM_REQUIRED = 0x00000001;
	private const uint ES_DISPLAY_REQUIRED = 0x00000002;

private void searchTypeComboBox_SelectedIndexChanged(object sender, EventArgs e)
{
        if (_suppressSearchTypeComboEvents)
        {
                return;
        }
        if (searchTypeComboBox.SelectedIndex >= 0)
        {
                string text = searchTypeComboBox.SelectedItem?.ToString() ?? string.Empty;
                if (string.Equals(text, MixedSearchTypeDisplayName, StringComparison.OrdinalIgnoreCase))
                {
                        _isMixedSearchTypeActive = true;
                }
                else
                {
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                                _lastExplicitSearchType = text;
                        }
                        _isMixedSearchTypeActive = false;
                        HideMixedSearchTypeOption();
                }
        }
}

	private void MainForm_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.KeyCode == Keys.Escape && e.Shift && !e.Control && !e.Alt)
		{
			e.Handled = true;
			e.SuppressKeyPress = true;
			hideMenuItem.PerformClick();
			return;
		}
		if (e.KeyCode == Keys.Back && resultListView.Focused)
		{
			e.Handled = true;
			e.SuppressKeyPress = true;
			GoBackAsync();
			return;
		}
		TextBox textBox = searchTextBox;
		if (textBox == null || !textBox.ContainsFocus)
		{
			ComboBox comboBox = searchTypeComboBox;
			if (comboBox == null || !comboBox.ContainsFocus)
			{
				goto IL_00f2;
			}
		}
		if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Left || e.KeyCode == Keys.Right)
		{
			return;
		}
		goto IL_00f2;
		IL_00f2:
		checked
		{
			if (e.KeyCode == Keys.Space)
			{
				e.Handled = true;
				e.SuppressKeyPress = true;
                                TogglePlayPauseAsync().SafeFireAndForget("KeyDown Space");
			}
			else if (e.KeyCode == Keys.Left)
			{
				e.Handled = true;
				e.SuppressKeyPress = true;
				HandleDirectionalKeyDown(isRight: false);
			}
			else if (e.KeyCode == Keys.Right)
			{
				e.Handled = true;
				e.SuppressKeyPress = true;
				HandleDirectionalKeyDown(isRight: true);
			}
			else if (e.KeyCode == Keys.F1)
			{
				e.Handled = true;
				e.SuppressKeyPress = true;
				if (volumeTrackBar.Value > 0)
				{
					volumeTrackBar.Value = Math.Max(0, volumeTrackBar.Value - 2);
					volumeTrackBar_Scroll(volumeTrackBar, EventArgs.Empty);
				}
			}
			else if (e.KeyCode == Keys.F2)
			{
				e.Handled = true;
				e.SuppressKeyPress = true;
				if (volumeTrackBar.Value < 100)
				{
					volumeTrackBar.Value = Math.Min(100, volumeTrackBar.Value + 2);
					volumeTrackBar_Scroll(volumeTrackBar, EventArgs.Empty);
				}
			}
			else if (e.KeyCode == Keys.F3)
			{
				e.Handled = true;
				e.SuppressKeyPress = true;
                                PlayPreviousAsync().SafeFireAndForget("KeyDown MediaPrevious");
			}
			else if (e.KeyCode == Keys.F4)
			{
				e.Handled = true;
				e.SuppressKeyPress = true;
                                PlayNextAsync().SafeFireAndForget("KeyDown MediaNext");
			}
			else if (e.KeyCode == Keys.F5)
			{
				e.Handled = true;
				e.SuppressKeyPress = true;
				RefreshCurrentViewAsync();
			}
			else if (e.KeyCode == Keys.F9)
			{
				e.Handled = true;
				e.SuppressKeyPress = true;
				ShowOutputDeviceDialogAsync();
			}
			else if (e.KeyCode == Keys.F11)
			{
				e.Handled = true;
				e.SuppressKeyPress = true;
				ToggleAutoReadLyrics();
			}
			else if (e.KeyCode == Keys.F12)
			{
				e.Handled = true;
				e.SuppressKeyPress = true;
				ShowJumpToPositionDialog();
			}
			else if (e.KeyCode == Keys.Next)
			{
				e.Handled = true;
				e.SuppressKeyPress = true;
                                OnNextPageAsync().SafeFireAndForget("KeyDown PageDown");
			}
			else if (e.KeyCode == Keys.Prior)
			{
				e.Handled = true;
				e.SuppressKeyPress = true;
                                OnPrevPageAsync().SafeFireAndForget("KeyDown PageUp");
			}
		}
	}

	private bool TryHandleDownloadShortcut()
	{
		if (_isCurrentPlayingMenuActive)
		{
			return false;
		}
		if (resultListView == null)
		{
			return false;
		}
		if (!resultListView.ContainsFocus && !HasListViewSelection())
		{
			return false;
		}
		MenuContextSnapshot menuContextSnapshot = BuildMenuContextSnapshot(isCurrentPlayingRequest: false);
		if (!menuContextSnapshot.IsValid)
		{
			return false;
		}
		switch (menuContextSnapshot.PrimaryEntity)
		{
		case MenuEntityKind.Song:
		case MenuEntityKind.PodcastEpisode:
			if (menuContextSnapshot.IsCloudView && menuContextSnapshot.Song != null && menuContextSnapshot.Song.IsCloudSong)
			{
				return false;
			}
			DownloadSong_Click(downloadSongMenuItem, EventArgs.Empty);
			return true;
		case MenuEntityKind.Playlist:
			DownloadPlaylist_Click(downloadPlaylistMenuItem, EventArgs.Empty);
			return true;
		case MenuEntityKind.Album:
			DownloadAlbum_Click(downloadAlbumMenuItem, EventArgs.Empty);
			return true;
		case MenuEntityKind.Podcast:
			if (menuContextSnapshot.Podcast == null || menuContextSnapshot.Podcast.Id <= 0)
			{
				return false;
			}
			DownloadPodcast_Click(downloadPodcastMenuItem, EventArgs.Empty);
			return true;
		case MenuEntityKind.Category:
			DownloadCategory_Click(downloadCategoryMenuItem, EventArgs.Empty);
			return true;
		default:
			return false;
		}
	}

	private void UpdateDownloadMenuShortcutDisplay(bool showShortcuts)
	{
		if (downloadSongMenuItem != null)
		{
			downloadSongMenuItem.ShowShortcutKeys = showShortcuts;
		}
		if (downloadPlaylistMenuItem != null)
		{
			downloadPlaylistMenuItem.ShowShortcutKeys = showShortcuts;
		}
		if (downloadAlbumMenuItem != null)
		{
			downloadAlbumMenuItem.ShowShortcutKeys = showShortcuts;
		}
		if (downloadPodcastMenuItem != null)
		{
			downloadPodcastMenuItem.ShowShortcutKeys = showShortcuts;
		}
		if (downloadCategoryMenuItem != null)
		{
			downloadCategoryMenuItem.ShowShortcutKeys = showShortcuts;
		}
	}

	private void UpdateTrayIconTooltip(SongInfo? song, bool isPaused = false)
	{
		if (_trayIcon == null)
		{
			return;
		}
		if (base.InvokeRequired)
		{
			BeginInvoke(new Action<SongInfo, bool>(UpdateTrayIconTooltip), song, isPaused);
			return;
		}
		if (song == null)
		{
			_trayIcon.Text = "易听";
			Debug.WriteLine("[MainForm] 托盘提示已重置为未播放状态");
			return;
		}
		string text = song.Name + " - " + song.Artist;
		if (song.IsTrial)
		{
			text += " [试听版]";
		}
		if (!string.IsNullOrEmpty(song.Album))
		{
			text = text + " [" + song.Album + "]";
		}
		if (!string.IsNullOrEmpty(song.Level))
		{
			string qualityDisplayName = NeteaseApiClient.GetQualityDisplayName(song.Level);
			text = text + " | " + qualityDisplayName;
		}
		if (text.Length > 63)
		{
			_trayIcon.Text = text.Substring(0, 60) + "...";
		}
		else
		{
			_trayIcon.Text = text;
		}
		Debug.WriteLine("[MainForm] 更新托盘提示: " + _trayIcon.Text);
	}

	private void ShowTrayBalloonTip(SongInfo song, string state = "正在播放")
	{
		if (_trayIcon == null || song == null)
		{
			return;
		}
		if (base.InvokeRequired)
		{
			BeginInvoke(new Action<SongInfo, string>(ShowTrayBalloonTip), song, state);
			return;
		}
		try
		{
			string balloonTipTitle = "易听";
			string text = state + "：" + song.Name + " - " + song.Artist;
			if (!string.IsNullOrEmpty(song.Level))
			{
				string qualityDisplayName = NeteaseApiClient.GetQualityDisplayName(song.Level);
				text = text + "\n音质：" + qualityDisplayName;
			}
			_trayIcon.BalloonTipTitle = balloonTipTitle;
			_trayIcon.BalloonTipText = text;
			_trayIcon.ShowBalloonTip(3000);
		}
		catch (Exception ex)
		{
			Debug.WriteLine("[MainForm] 显示气球提示失败: " + ex.Message);
		}
	}

	private void RestoreFromTray()
	{
		try
		{
			if (!base.Visible)
			{
				Show();
			}
			if (base.WindowState == FormWindowState.Minimized)
			{
				base.WindowState = FormWindowState.Normal;
			}
			ShowWindow(base.Handle, 9);
			BringToFront();
			Activate();
			SetForegroundWindow(base.Handle);
			BeginInvoke(delegate
			{
				Control control = null;
				if (resultListView != null && resultListView.CanFocus)
				{
					control = resultListView;
					if (resultListView.Items.Count > 0)
					{
						int targetIndex = _lastListViewFocusedIndex;
						if (targetIndex < 0 || targetIndex >= resultListView.Items.Count)
						{
							if (HasListViewSelection())
							{
								targetIndex = GetSelectedListViewIndex();
								Debug.WriteLine($"[RestoreFromTray] 使用当前选中索引={targetIndex}");
							}
							else
							{
								targetIndex = 0;
								Debug.WriteLine("[RestoreFromTray] 使用默认索引=0");
							}
						}
						else
						{
							Debug.WriteLine($"[RestoreFromTray] 使用保存的焦点索引={targetIndex}");
						}
						ClearListViewSelection();
						BeginInvoke(delegate
						{
							if (targetIndex >= 0 && targetIndex < resultListView.Items.Count)
							{
								resultListView.Items[targetIndex].Selected = true;
								resultListView.Items[targetIndex].Focused = true;
								resultListView.EnsureVisible(targetIndex);
								Debug.WriteLine($"[RestoreFromTray] 已重新选中索引={targetIndex}，项目文本={resultListView.Items[targetIndex].Text}");
							}
							resultListView.Focus();
							NotifyAccessibilityClients(resultListView, AccessibleEvents.Focus, 0);
							NotifyAccessibilityClients(resultListView, AccessibleEvents.Selection, targetIndex);
							NotifyAccessibilityClients(resultListView, AccessibleEvents.SelectionAdd, targetIndex);
							Debug.WriteLine($"[RestoreFromTray] 列表焦点已设置，选中项索引={targetIndex}");
						});
					}
					else
					{
						resultListView.Focus();
						NotifyAccessibilityClients(resultListView, AccessibleEvents.Focus, -1);
					}
				}
				else if (searchTextBox != null && searchTextBox.CanFocus)
				{
					control = searchTextBox;
					searchTextBox.Focus();
					searchTextBox.Select(searchTextBox.TextLength, 0);
					NotifyAccessibilityClients(searchTextBox, AccessibleEvents.Focus, -1);
				}
				else if (playPauseButton != null && playPauseButton.CanFocus)
				{
					control = playPauseButton;
					playPauseButton.Focus();
					NotifyAccessibilityClients(playPauseButton, AccessibleEvents.Focus, -1);
				}
					if (control != null)
					{
						Debug.WriteLine("[RestoreFromTray] 焦点已设置到: " + control.Name);
					}
			});
		}
		catch (Exception ex)
		{
			Debug.WriteLine("[RestoreFromTray] 异常: " + ex.Message);
		}
	}

	private void TrayIcon_MouseClick(object sender, MouseEventArgs e)
	{
		if (e.Button == MouseButtons.Left)
		{
			RestoreFromTray();
		}
		else if (e.Button == MouseButtons.Right)
		{
			if (!base.Visible)
			{
				ShowTrayContextMenu(System.Windows.Forms.Cursor.Position);
			}
			else
			{
				RestoreFromTray();
			}
		}
	}

	private void loginMenuItem_Click(object sender, EventArgs e)
	{
		try
		{
			if (IsUserLoggedIn())
			{
				using (UserInfoForm userInfoForm = new UserInfoForm(_apiClient, _configManager, delegate
				{
					Debug.WriteLine("[LoginMenuItem] 退出登录回调触发");
					ClearLoginState(persist: true, alreadyLoggedOut: true);
					EnsureConfigInitialized();
					if (base.InvokeRequired)
					{
						Invoke(delegate
						{
							UpdateLoginMenuItemText();
							RefreshQualityMenuAvailability();
							UpdateStatusBar("已退出登录");
							if (_isHomePage)
							{
								Debug.WriteLine("[LoginMenuItem] 退出登录后当前在主页，刷新主页");
								Task.Run(async delegate
								{
									try
									{
										await Invoke((Func<Task>)(() => LoadHomePageAsync()));
									}
									catch (Exception ex3)
									{
										Exception ex4 = ex3;
										Exception homeEx = ex4;
										Debug.WriteLine("[LoginMenuItem] 退出登录后刷新主页失败: " + homeEx.Message);
									}
								});
							}
							else
							{
								Debug.WriteLine("[LoginMenuItem] 退出登录后当前不在主页，跳过自动刷新");
							}
						});
					}
					else
					{
						UpdateLoginMenuItemText();
						RefreshQualityMenuAvailability();
						UpdateStatusBar("已退出登录");
						if (_isHomePage)
						{
							Debug.WriteLine("[LoginMenuItem] 退出登录后当前在主页，刷新主页");
							Task.Run(async delegate
							{
								try
								{
									await LoadHomePageAsync();
								}
								catch (Exception ex3)
								{
									Exception ex4 = ex3;
									Exception homeEx = ex4;
									Debug.WriteLine("[LoginMenuItem] 退出登录后刷新主页失败: " + homeEx.Message);
								}
							});
						}
						else
						{
							Debug.WriteLine("[LoginMenuItem] 退出登录后当前不在主页，跳过自动刷新");
						}
					}
				}))
				{
					userInfoForm.ShowDialog(this);
					return;
				}
			}
			Debug.WriteLine("[LoginMenuItem] ========== 开始登录流程 ==========");
			if (_apiClient == null)
			{
				Debug.WriteLine("[LoginMenuItem] ⚠\ufe0f API客户端为null，尝试重新初始化");
				try
				{
					_configManager = _configManager ?? ConfigManager.Instance;
					_config = _config ?? _configManager.Load();
					_apiClient = new NeteaseApiClient(_config);
					_apiClient.UseSimplifiedApi = false;
					Debug.WriteLine("[LoginMenuItem] ✓ API客户端重新初始化成功");
				}
				catch (Exception ex)
				{
					Debug.WriteLine("[LoginMenuItem] ✗ API客户端初始化失败: " + ex.Message);
					MessageBox.Show("无法初始化登录功能：\n\n" + ex.Message + "\n\n请尝试重新启动应用程序。", "初始化错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
					return;
				}
			}
			using LoginForm loginForm = new LoginForm(_apiClient);
			Debug.WriteLine("[LoginMenuItem] 订阅LoginSuccess事件");
			loginForm.LoginSuccess += delegate(object s, LoginSuccessEventArgs args)
			{
				try
				{
					ApplyLoginState(args);
				}
				catch (Exception ex3)
				{
					Debug.WriteLine("[LoginMenuItem] 事件处理异常: " + ex3.Message);
					MessageBox.Show("更新菜单失败: " + ex3.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				}
			};
			Debug.WriteLine("[LoginMenuItem] 调用loginForm.ShowDialog()...");
			DialogResult value = loginForm.ShowDialog(this);
			Debug.WriteLine($"[LoginMenuItem] ShowDialog()返回，结果={value}");
			Debug.WriteLine("[LoginMenuItem] ========== 登录流程结束 ==========");
		}
		catch (Exception ex2)
		{
			MessageBox.Show("登录失败: " + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
		}
	}

	private void UpdateLoginMenuItemText()
	{
		Debug.WriteLine("[UpdateLoginMenuItemText] 开始更新");
		bool flag = IsUserLoggedIn();
		Debug.WriteLine($"[UpdateLoginMenuItemText] UsePersonalCookie={_apiClient.UsePersonalCookie} (自动检测)");
		Debug.WriteLine($"[UpdateLoginMenuItemText] IsLoggedIn={_accountState?.IsLoggedIn}");
		string value = _accountState?.MusicU;
		Debug.WriteLine("[UpdateLoginMenuItemText] MusicU=" + (string.IsNullOrEmpty(value) ? "未设置" : "已设置"));
		Debug.WriteLine("[UpdateLoginMenuItemText] Nickname=" + (_accountState?.Nickname ?? "null"));
		Debug.WriteLine("[UpdateLoginMenuItemText] AvatarUrl=" + (_accountState?.AvatarUrl ?? "null"));
		Debug.WriteLine($"[UpdateLoginMenuItemText] VipType={_accountState?.VipType ?? 0}");
                if (flag)
                {
                        string text = _accountState?.Nickname;
                        string text2 = (string.IsNullOrEmpty(text) ? "用户信息" : text);
                        Debug.WriteLine("[UpdateLoginMenuItemText] 设置菜单项为: " + text2);
                        loginMenuItem.Text = text2;
                }
                else
                {
                        Debug.WriteLine("[UpdateLoginMenuItemText] 设置菜单项为: 登录");
                        loginMenuItem.Text = "登录";
                }
	}

	private static string GetVipDescription(int vipType)
	{
		if (1 == 0)
		{
		}
		string result = ((vipType == 10) ? "豪华VIP" : ((vipType != 11) ? ((vipType > 0) ? "普通VIP" : "普通用户") : "黑胶VIP"));
		if (1 == 0)
		{
		}
		return result;
	}

	private void ApplyLoginState(LoginSuccessEventArgs args)
	{
		if (args == null)
		{
			Debug.WriteLine("[LoginMenuItem] LoginSuccess事件参数为空");
			return;
		}
		if (base.InvokeRequired)
		{
			Invoke(delegate
			{
				ApplyLoginState(args);
			});
			return;
		}
		Debug.WriteLine("[LoginMenuItem] ********** LoginSuccess事件被触发 **********");
		Debug.WriteLine($"[LoginMenuItem] 线程ID={Thread.CurrentThread.ManagedThreadId}");
		Debug.WriteLine("[LoginMenuItem] 事件参数:");
		Debug.WriteLine("[LoginMenuItem]   Nickname=" + args.Nickname);
		Debug.WriteLine("[LoginMenuItem]   UserId=" + args.UserId);
		Debug.WriteLine($"[LoginMenuItem]   VipType={args.VipType}");
		Debug.WriteLine("[LoginMenuItem]   Cookie=" + (string.IsNullOrEmpty(args.Cookie) ? "未提供" : $"已提供({args.Cookie.Length}字符)"));
		if (!string.IsNullOrEmpty(args.Cookie))
		{
			try
			{
				_apiClient.PrepareForLogin();
				_apiClient.SetCookieString(args.Cookie);
				Debug.WriteLine("[LoginMenuItem] 已从事件Cookie刷新API客户端状态");
			}
			catch (Exception ex)
			{
				Debug.WriteLine("[LoginMenuItem] 设置Cookie失败: " + ex.Message);
			}
		}
		Debug.WriteLine("[LoginMenuItem] 从_apiClient读取Cookie:");
		string musicU = _apiClient.MusicU;
		string text = (string.IsNullOrEmpty(musicU) ? "未设置⚠\ufe0f" : $"已设置({musicU.Length}字符)");
		Debug.WriteLine("[LoginMenuItem]   _apiClient.MusicU=" + text);
		Debug.WriteLine("[LoginMenuItem]   _apiClient.CsrfToken=" + (_apiClient.CsrfToken ?? "未设置⚠\ufe0f"));
		SyncConfigFromApiClient(args, persist: true);
		long? num = null;
		if (long.TryParse(args.UserId, out var result))
		{
			num = result;
			CacheLoggedInUserId(result);
		}
		UserAccountInfo profile = new UserAccountInfo
		{
			UserId = num.GetValueOrDefault(),
			Nickname = args.Nickname,
			AvatarUrl = args.AvatarUrl,
			VipType = args.VipType
		};
		_apiClient.ApplyLoginProfile(profile);
		ReloadAccountState();
		Debug.WriteLine("[LoginMenuItem] 账户状态已更新:");
		Debug.WriteLine($"[LoginMenuItem]   _accountState.IsLoggedIn={_accountState?.IsLoggedIn}");
		string text2 = _accountState?.MusicU;
		string text3 = (string.IsNullOrEmpty(text2) ? "未设置⚠\ufe0f" : ("已设置(" + text2.Substring(0, Math.Min(20, text2.Length)) + "...)"));
		Debug.WriteLine("[LoginMenuItem]   _accountState.MusicU=" + text3);
		Debug.WriteLine("[LoginMenuItem]   _accountState.CsrfToken=" + (_accountState?.CsrfToken ?? "未设置⚠\ufe0f"));
		Debug.WriteLine("[LoginMenuItem]   _accountState.Nickname=" + _accountState?.Nickname);
		Debug.WriteLine("[LoginMenuItem]   _accountState.UserId=" + _accountState?.UserId);
		Debug.WriteLine("[LoginMenuItem]   _accountState.AvatarUrl=" + (_accountState?.AvatarUrl ?? "未设置⚠\ufe0f"));
		Debug.WriteLine($"[LoginMenuItem]   _accountState.VipType={_accountState?.VipType}");
		Debug.WriteLine($"[LoginMenuItem]   UsePersonalCookie(自动)={_apiClient.UsePersonalCookie}");
		UpdateStatusBar("登录成功！欢迎 " + args.Nickname + " (" + GetVipDescription(args.VipType) + ")");
		UpdateLoginMenuItemText();
		RefreshQualityMenuAvailability();
		menuStrip1.Invalidate();
		menuStrip1.Update();
		menuStrip1.Refresh();
		Application.DoEvents();
		Debug.WriteLine("[LoginMenuItem] 菜单已刷新");
		if (_apiClient.UsePersonalCookie)
		{
			Task.Run(async delegate
			{
				try
				{
					await EnsureLoginProfileAsync();
				}
				catch (Exception ex2)
				{
					Exception ex3 = ex2;
					Exception ex4 = ex3;
					Debug.WriteLine("[LoginMenuItem] 登录后同步资料失败: " + ex4.Message);
				}
			});
		}
		ScheduleLibraryStateRefresh();
		if (_isHomePage)
		{
			Debug.WriteLine("[LoginMenuItem] 当前在主页，刷新主页");
			Task.Run(async delegate
			{
				try
				{
					if (base.InvokeRequired)
					{
						await Invoke((Func<Task>)async delegate
						{
							await LoadHomePageAsync();
						});
					}
					else
					{
						await LoadHomePageAsync();
					}
				}
				catch (Exception ex2)
				{
					Exception ex3 = ex2;
					Exception homeEx = ex3;
					Debug.WriteLine("[LoginMenuItem] 刷新主页失败: " + homeEx.Message);
				}
			});
		}
		else
		{
			Debug.WriteLine("[LoginMenuItem] 当前不在主页，跳过自动刷新");
		}
	}

	private async void homeMenuItem_Click(object sender, EventArgs e)
	{
		if (await LoadHomePageAsync())
		{
			FocusListAfterEnrich(0);
		}
	}

	private void exitMenuItem_Click(object sender, EventArgs e)
	{
		_isApplicationExitRequested = true;
		Close();
	}

	private void hideMenuItem_Click(object sender, EventArgs e)
	{
		try
		{
			if (_trayIcon != null)
			{
				_trayIcon.BalloonTipTitle = "易听";
				_trayIcon.BalloonTipText = "窗口已隐藏，单击托盘图标可恢复";
				_trayIcon.ShowBalloonTip(2000);
			}
			Hide();
		}
		catch (Exception ex)
		{
			Debug.WriteLine("[hideMenuItem_Click] 异常: " + ex.Message);
		}
	}

	private void TrayIcon_DoubleClick(object sender, EventArgs e)
	{
		RestoreFromTray();
	}

	private void trayShowMenuItem_Click(object sender, EventArgs e)
	{
		RestoreFromTray();
	}

        private void trayPlayPauseMenuItem_Click(object sender, EventArgs e)    
        {
                TogglePlayPauseAsync().SafeFireAndForget("Tray play/pause");
        }

        private void trayPrevMenuItem_Click(object sender, EventArgs e)
        {
                PlayPreviousAsync().SafeFireAndForget("Tray previous");
        }

        private void trayNextMenuItem_Click(object sender, EventArgs e)
        {
                PlayNextAsync().SafeFireAndForget("Tray next");
        }

	private void trayExitMenuItem_Click(object sender, EventArgs e)
	{
		Debug.WriteLine("[trayExitMenuItem] 退出菜单项被点击");
		_isApplicationExitRequested = true;
		BeginInvoke(delegate
		{
			Debug.WriteLine("[trayExitMenuItem] 延迟执行退出...");
			Close();
		});
	}

	private void ShowTrayContextMenu(Point position)
	{
		if (_contextMenuHost == null || trayContextMenu == null)
		{
			return;
		}
		if (base.Visible)
		{
			RestoreFromTray();
			return;
		}
		try
		{
			Debug.WriteLine($"[ShowTrayContextMenu] 在位置 ({position.X}, {position.Y}) 显示菜单");
			_contextMenuHost.ShowHost();
			trayContextMenu.Show(_contextMenuHost, new Point(0, 0));
			trayContextMenu.Left = position.X;
			trayContextMenu.Top = position.Y;
		}
		catch (Exception ex)
		{
			Debug.WriteLine("[ShowTrayContextMenu] 显示菜单失败: " + ex.Message);
		}
	}

	private void TrayContextMenu_Opening(object sender, CancelEventArgs e)
	{
		Debug.WriteLine("[TrayContextMenu] 菜单正在打开...");
	}

        private void SearchTextContextMenu_Opened(object sender, EventArgs e)
        {
                FocusFirstContextMenuItemDeferred(searchTextContextMenu, "SearchTextContextMenu");
        }

        private void SongContextMenu_Opened(object sender, EventArgs e)
        {
                FocusFirstContextMenuItemDeferred(songContextMenu, "SongContextMenu");
        }

        private bool IsAnyMainContextMenuVisible()
        {
                return (songContextMenu != null && songContextMenu.Visible) ||
                        (searchTextContextMenu != null && searchTextContextMenu.Visible) ||
                        (trayContextMenu != null && trayContextMenu.Visible);
        }

        private void FocusFirstContextMenuItemDeferred(ContextMenuStrip menu, string menuName)
        {
                if (menu == null || menu.Items.Count == 0 || base.IsDisposed)
                {
                        return;
                }

                ContextMenuAccessibilityHelper.EnsureFirstItemFocusedOnOpen(this, menu, menuName, message => Debug.WriteLine(message));
        }

	private void TrayContextMenu_Opened(object sender, EventArgs e)
	{
		Debug.WriteLine("[TrayContextMenu] 菜单已打开，设置焦点...");
                FocusFirstContextMenuItemDeferred(trayContextMenu, "TrayContextMenu");
	}

	private void TrayContextMenu_Closed(object sender, ToolStripDropDownClosedEventArgs e)
	{
		Debug.WriteLine("[TrayContextMenu] 菜单已关闭");
		if (_isApplicationExitRequested)
		{
			Debug.WriteLine("[TrayContextMenu] 检测到退出操作，跳过 Closed 事件处理");
			return;
		}
		if (_contextMenuHost != null)
		{
			try
			{
				BeginInvoke(delegate
				{
					_contextMenuHost.HideHost();
					Debug.WriteLine("[TrayContextMenu] 宿主窗口已隐藏");
				});
			}
			catch (Exception ex)
			{
				Debug.WriteLine("[TrayContextMenu] 隐藏宿主窗口失败: " + ex.Message);
			}
		}
		if (!base.Visible || base.IsDisposed)
		{
			return;
		}
		try
		{
			BeginInvoke(delegate
			{
				if (base.CanFocus)
				{
					Focus();
					Debug.WriteLine("[TrayContextMenu] 焦点已恢复到主窗口");
				}
			});
		}
		catch (Exception ex2)
		{
			Debug.WriteLine("[TrayContextMenu] 恢复焦点失败: " + ex2.Message);
		}
	}

	private void playPauseMenuItem_Click(object sender, EventArgs e)
	{
		TogglePlayPauseAsync().SafeFireAndForget("Menu play/pause");
	}

	private void prevMenuItem_Click(object sender, EventArgs e)
	{
		PlayPreviousAsync().SafeFireAndForget("Menu previous");
	}

	private void nextMenuItem_Click(object sender, EventArgs e)
	{
		PlayNextAsync().SafeFireAndForget("Menu next");
	}

	private void jumpToPositionMenuItem_Click(object sender, EventArgs e)
	{
		ShowJumpToPositionDialog();
	}

	private async void outputDeviceMenuItem_Click(object sender, EventArgs e)
	{
		await ShowOutputDeviceDialogAsync();
	}

	private void ShowJumpToPositionDialog()
	{
		PlaybackState playbackState = (_audioEngine != null) ? _audioEngine.GetPlaybackState() : PlaybackState.Stopped;
		bool canSeekInCurrentState = playbackState == PlaybackState.Playing
			|| playbackState == PlaybackState.Paused
			|| playbackState == PlaybackState.Buffering
			|| playbackState == PlaybackState.Loading;
		bool flag = !canSeekInCurrentState;
		if ((_isPlaybackLoading || IsSwitchingTrack) && flag)
		{
			Debug.WriteLine("[MainForm] F12跳转被忽略：歌曲加载中");
			return;
		}
		if (_audioEngine == null || !canSeekInCurrentState)
		{
			Debug.WriteLine("[MainForm] F12跳转被忽略：没有正在播放的歌曲");
			return;
		}
		try
		{
			TryCancelPendingLongSeek();
			double position = _audioEngine.GetPosition();
			double duration = _audioEngine.GetDuration();
			if (duration <= 0.0)
			{
				Debug.WriteLine("[MainForm] F12跳转被忽略：无法获取歌曲时长");
				return;
			}
			using JumpToPositionDialog jumpToPositionDialog = new JumpToPositionDialog(position, duration);
			if (jumpToPositionDialog.ShowDialog(this) == DialogResult.OK)
			{
				double targetPosition = jumpToPositionDialog.TargetPosition;
				RequestSeekAndResetLyrics(targetPosition);
				Debug.WriteLine($"[MainForm] 跳转到位置: {targetPosition:F2} 秒");
			}
		}
		catch (Exception ex)
		{
			Debug.WriteLine("[MainForm] 跳转对话框错误: " + ex.Message);
			MessageBox.Show("跳转失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
		}
	}

	private async Task ShowOutputDeviceDialogAsync()
	{
		if (_audioEngine == null)
		{
			MessageBox.Show(this, "音频引擎尚未初始化。", "输出设备", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			return;
		}
		List<AudioOutputDeviceInfo> devices;
		try
		{
			devices = _audioEngine.GetOutputDevices().ToList();
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Exception ex3 = ex2;
			MessageBox.Show(this, "无法获取输出设备列表: " + ex3.Message, "输出设备", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			return;
		}
		if (devices.Count == 0)
		{
			MessageBox.Show(this, "未检测到可用的声音输出设备。", "输出设备", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			return;
		}
		using OutputDeviceDialog dialog = new OutputDeviceDialog(devices, _audioEngine.ActiveOutputDeviceId);
		if (dialog.ShowDialog(this) != DialogResult.OK || dialog.SelectedDevice == null)
		{
			return;
		}
		AudioOutputDeviceInfo selectedDevice = dialog.SelectedDevice;
		AudioDeviceSwitchResult switchResult;
		try
		{
			using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(5.0));
			switchResult = await _audioEngine.SwitchOutputDeviceAsync(selectedDevice, cts.Token).ConfigureAwait(continueOnCapturedContext: true);
		}
		catch (OperationCanceledException)
		{
			MessageBox.Show(this, "切换输出设备超时。", "输出设备", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
			return;
		}
		catch (Exception ex)
		{
			Exception ex5 = ex;
			MessageBox.Show(this, "切换输出设备失败: " + ex5.Message, "输出设备", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
			return;
		}
		if (!switchResult.IsSuccess)
		{
			MessageBox.Show(this, "切换输出设备失败: " + switchResult.ErrorMessage, "输出设备", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
			return;
		}
		AudioOutputDeviceInfo appliedDevice = switchResult.Device ?? selectedDevice;
		if (_config != null)
		{
			_config.OutputDevice = appliedDevice.DeviceId;
			_configManager?.Save(_config);
		}
		UpdateStatusBar("输出设备已切换到: " + appliedDevice.DisplayName);
	}

	private void sequentialMenuItem_Click(object sender, EventArgs e)
	{
		if (_audioEngine != null)
		{
			_audioEngine.PlayMode = PlayMode.Sequential;
			_config.PlaybackOrder = "顺序播放";
			SaveConfig();
			UpdatePlaybackOrderMenuCheck();
			RefreshNextSongPreload();
		}
	}

	private void loopMenuItem_Click(object sender, EventArgs e)
	{
		if (_audioEngine != null)
		{
			_audioEngine.PlayMode = PlayMode.Loop;
			_config.PlaybackOrder = "列表循环";
			SaveConfig();
			UpdatePlaybackOrderMenuCheck();
			RefreshNextSongPreload();
		}
	}

	private void loopOneMenuItem_Click(object sender, EventArgs e)
	{
		if (_audioEngine != null)
		{
			_audioEngine.PlayMode = PlayMode.LoopOne;
			_config.PlaybackOrder = "单曲循环";
			SaveConfig();
			UpdatePlaybackOrderMenuCheck();
			RefreshNextSongPreload();
		}
	}

	private void randomMenuItem_Click(object sender, EventArgs e)
	{
		if (_audioEngine != null)
		{
			_audioEngine.PlayMode = PlayMode.Random;
			_config.PlaybackOrder = "随机播放";
			SaveConfig();
			UpdatePlaybackOrderMenuCheck();
			RefreshNextSongPreload();
		}
	}

	private void UpdatePlaybackOrderMenuCheck()
	{
        SetMenuItemCheckedState(sequentialMenuItem, _config.PlaybackOrder == "顺序播放");
        SetMenuItemCheckedState(loopMenuItem, _config.PlaybackOrder == "列表循环");
        SetMenuItemCheckedState(loopOneMenuItem, _config.PlaybackOrder == "单曲循环");
        SetMenuItemCheckedState(randomMenuItem, _config.PlaybackOrder == "随机播放");
	}

	private void UpdateQualityMenuCheck()
	{
		string defaultQuality = _config.DefaultQuality;
        SetMenuItemCheckedState(standardQualityMenuItem, defaultQuality == "标准音质");
        SetMenuItemCheckedState(highQualityMenuItem, defaultQuality == "极高音质");
        SetMenuItemCheckedState(losslessQualityMenuItem, defaultQuality == "无损音质");
        SetMenuItemCheckedState(hiresQualityMenuItem, defaultQuality == "Hi-Res音质");
        SetMenuItemCheckedState(surroundHDQualityMenuItem, defaultQuality == "高清环绕声");
        SetMenuItemCheckedState(dolbyQualityMenuItem, defaultQuality == "沉浸环绕声");
        SetMenuItemCheckedState(masterQualityMenuItem, defaultQuality == "超清母带");
	}

	private void RefreshQualityMenuAvailability()
	{
		bool flag = IsUserLoggedIn();
		int num = _accountState?.VipType ?? 0;
		if (!flag)
		{
			standardQualityMenuItem.Enabled = true;
			highQualityMenuItem.Enabled = true;
			losslessQualityMenuItem.Enabled = false;
			hiresQualityMenuItem.Enabled = false;
			surroundHDQualityMenuItem.Enabled = false;
			dolbyQualityMenuItem.Enabled = false;
			masterQualityMenuItem.Enabled = false;
			Debug.WriteLine("[QualityMenu] 未登录状态 - 仅标准和极高可用");
		}
		else if (num >= 11)
		{
			standardQualityMenuItem.Enabled = true;
			highQualityMenuItem.Enabled = true;
			losslessQualityMenuItem.Enabled = true;
			hiresQualityMenuItem.Enabled = true;
			surroundHDQualityMenuItem.Enabled = true;
			dolbyQualityMenuItem.Enabled = true;
			masterQualityMenuItem.Enabled = true;
			Debug.WriteLine($"[QualityMenu] SVIP用户 (VipType={num}) - 所有音质可用");
		}
		else if (num >= 1)
		{
			standardQualityMenuItem.Enabled = true;
			highQualityMenuItem.Enabled = true;
			losslessQualityMenuItem.Enabled = true;
			hiresQualityMenuItem.Enabled = true;
			surroundHDQualityMenuItem.Enabled = false;
			dolbyQualityMenuItem.Enabled = false;
			masterQualityMenuItem.Enabled = false;
			Debug.WriteLine($"[QualityMenu] VIP用户 (VipType={num}) - up to Hi-Res可用");
		}
		else
		{
			standardQualityMenuItem.Enabled = true;
			highQualityMenuItem.Enabled = true;
			losslessQualityMenuItem.Enabled = true;
			hiresQualityMenuItem.Enabled = false;
			surroundHDQualityMenuItem.Enabled = false;
			dolbyQualityMenuItem.Enabled = false;
			masterQualityMenuItem.Enabled = false;
			Debug.WriteLine($"[QualityMenu] 普通用户 (VipType={num}) - 标准/极高/无损可用");
		}
		EnsureDefaultQualityAvailability();
	}

	private void EnsureDefaultQualityAvailability()
	{
		EnsureConfigInitialized();
		string currentQuality = _config.DefaultQuality;
		if (string.IsNullOrWhiteSpace(currentQuality))
		{
			currentQuality = string.Empty;
		}

		bool currentEnabled = IsQualityMenuItemEnabled(currentQuality);
		if (!currentEnabled)
		{
			string fallback = GetHighestAvailableQualityName();
			if (!string.Equals(currentQuality, fallback, StringComparison.Ordinal))
			{
				_config.DefaultQuality = fallback;
				SaveConfig(refreshCookieFromClient: false);
				Debug.WriteLine($"[QualityMenu] 当前音质不可用，已降级为: {fallback}");
			}
		}

		UpdateQualityMenuCheck();
	}

	private bool IsQualityMenuItemEnabled(string qualityName)
	{
		return qualityName switch
		{
			"标准音质" => standardQualityMenuItem.Enabled,
			"极高音质" => highQualityMenuItem.Enabled,
			"无损音质" => losslessQualityMenuItem.Enabled,
			"Hi-Res音质" => hiresQualityMenuItem.Enabled,
			"高清环绕声" => surroundHDQualityMenuItem.Enabled,
			"沉浸环绕声" => dolbyQualityMenuItem.Enabled,
			"超清母带" => masterQualityMenuItem.Enabled,
			_ => false
		};
	}

	private string GetHighestAvailableQualityName()
	{
		if (masterQualityMenuItem.Enabled) return masterQualityMenuItem.Text;
		if (dolbyQualityMenuItem.Enabled) return dolbyQualityMenuItem.Text;
		if (surroundHDQualityMenuItem.Enabled) return surroundHDQualityMenuItem.Text;
		if (hiresQualityMenuItem.Enabled) return hiresQualityMenuItem.Text;
		if (losslessQualityMenuItem.Enabled) return losslessQualityMenuItem.Text;
		if (highQualityMenuItem.Enabled) return highQualityMenuItem.Text;
		return standardQualityMenuItem.Text;
	}

	private void qualityMenuItem_Click(object sender, EventArgs e)
	{
		if (!(sender is ToolStripMenuItem toolStripMenuItem))
		{
			return;
		}
		string text = toolStripMenuItem.Text;
		if (!(_config.DefaultQuality == text))
		{
			string defaultQuality = _config.DefaultQuality;
			_config.DefaultQuality = text;
			SaveConfig();
			UpdateQualityMenuCheck();
			BassAudioEngine audioEngine = _audioEngine;
			if (audioEngine != null && audioEngine.IsPlaying)
			{
				RefreshNextSongPreload();
			}
			UpdateStatusBar("已切换到 " + text);
			Debug.WriteLine("[Quality] 音质已从 " + defaultQuality + " 切换到 " + text + "，多音质缓存已保留，将重新预加载下一首");
		}
	}

	private void donateMenuItem_Click(object sender, EventArgs e)
	{
		using DonateDialog donateDialog = new DonateDialog();
		donateDialog.ShowDialog(this);
	}

	private void checkUpdateMenuItem_Click(object sender, EventArgs e)
	{
		using UpdateCheckDialog updateCheckDialog = new UpdateCheckDialog();
		updateCheckDialog.UpdateLauncher = ExecuteUpdatePlan;
		updateCheckDialog.ShowDialog(this);
	}

	private bool ExecuteUpdatePlan(UpdatePlan plan)
	{
		if (plan == null)
		{
			return false;
		}
                try
                {
                        string baseDirectory = Utils.PathHelper.ApplicationRootDirectory;
                        string installRoot = ResolveUpdateRootDirectory(baseDirectory);

                        string updaterPath = Path.Combine(installRoot, "YTPlayer.Updater.exe");
                        if (!File.Exists(updaterPath) && !string.Equals(installRoot, baseDirectory, StringComparison.OrdinalIgnoreCase))
                        {
                                updaterPath = Path.Combine(baseDirectory, "YTPlayer.Updater.exe");
                        }

                        if (!File.Exists(updaterPath))
                        {
                                MessageBox.Show(this, "未找到更新程序 YTPlayer.Updater.exe，请重新安装或修复。", "更新失败", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                                return false;
                        }

                        string text2 = CreateUpdateSessionDirectory();
                        string text3 = Path.Combine(text2, Path.GetFileName(updaterPath));
                        File.Copy(updaterPath, text3, overwrite: true);

                        CopyUpdaterDependency(Path.Combine(installRoot, "libs", "YTPlayer.Updater.exe.config"), text2);
                        string text4 = Path.Combine(text2, "libs");
                        CopyUpdaterDependency("Newtonsoft.Json.dll", text4);
                        CopyUpdaterDependency("Newtonsoft.Json.xml", text4);
                        CopyUpdaterLibDirectory(text4, installRoot);

                        string text5 = Path.Combine(text2, "update-plan.json");
                        plan.SaveTo(text5);
                        string text6 = SerializeCommandLineArguments();
                        StringBuilder stringBuilder = new StringBuilder();
                        stringBuilder.Append("--plan \"" + text5 + "\" ");
                        stringBuilder.Append("--target \"" + installRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + "\" ");
                        string currentExe = Process.GetCurrentProcess().MainModule?.FileName ?? Path.Combine(baseDirectory, "YTPlayer.exe");
                        stringBuilder.Append("--main \"" + currentExe + "\" ");
                        stringBuilder.Append($"--pid {Process.GetCurrentProcess().Id} ");
                        if (!string.IsNullOrEmpty(text6))
                        {
                                stringBuilder.Append("--main-args \"" + text6 + "\" ");
                        }
                        ProcessStartInfo startInfo = new ProcessStartInfo
                        {
                                FileName = text3,
                                Arguments = stringBuilder.ToString(),
                                UseShellExecute = false,
                                WorkingDirectory = text2
                        };
                        startInfo.EnvironmentVariables["YTPLAYER_ROOT"] = installRoot;
                        startInfo.EnvironmentVariables["YTPLAYER_TARGET"] = installRoot;
                        Process process = Process.Start(startInfo);
                        if (process == null)
                        {
                                throw new InvalidOperationException("无法启动更新程序。");
                        }
                        _isApplicationExitRequested = true;
                        string planVersionLabel = GetPlanVersionLabel(plan);
                        UpdateStatusBar("正在准备更新至 " + planVersionLabel);
                        Task.Run(async delegate
                        {
                                await Task.Delay(300).ConfigureAwait(false);
                                SafeInvoke(delegate
                                {
                                        Close();
                                });
                        });
                        return true;
                }
                catch (Exception ex)
                {
                        MessageBox.Show(this, "启动更新程序失败：" + ex.Message, "更新失败", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                        return false;
                }
        }

        private static string ResolveUpdateRootDirectory(string baseDirectory)
        {
                if (string.IsNullOrWhiteSpace(baseDirectory))
                {
                        return baseDirectory;
                }
                try
                {
                        string trimmed = baseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        string leaf = Path.GetFileName(trimmed);
                        if (string.Equals(leaf, "libs", StringComparison.OrdinalIgnoreCase))
                        {
                                string? parent = Path.GetDirectoryName(trimmed);
                                if (!string.IsNullOrWhiteSpace(parent))
                                {
                                        return parent;
                                }
                        }
                }
                catch
                {
                }
                return baseDirectory;
        }

	private static string CreateUpdateSessionDirectory()
	{
		string path = Path.Combine(Path.GetTempPath(), "YTPlayerUpdater");
		string text = Path.Combine(path, $"{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}");
		Directory.CreateDirectory(text);
		return text;
	}

	private static void CopyUpdaterDependency(string sourceFileOrName, string destinationDirectory)
	{
		string text = sourceFileOrName;
		if (!File.Exists(text))
		{
			text = PathHelper.ResolveFromLibsOrBase(Path.GetFileName(sourceFileOrName));
		}
		if (!File.Exists(text))
		{
			Debug.WriteLine("[Updater] 依赖未找到: " + sourceFileOrName);
			return;
		}
		Directory.CreateDirectory(destinationDirectory);
		string destFileName = Path.Combine(destinationDirectory, Path.GetFileName(text));
		File.Copy(text, destFileName, overwrite: true);
	}

        private static void CopyUpdaterLibDirectory(string destinationLibs, string sourceRoot)
        {
                try
                {
                        string text = Path.Combine(sourceRoot, "libs");
                        if (!Directory.Exists(text))
                        {
                                text = Path.Combine(Utils.PathHelper.ApplicationRootDirectory, "libs");
                        }
                        if (Directory.Exists(text))
                        {
                                string[] files = Directory.GetFiles(text, "*.*", SearchOption.AllDirectories);
                                foreach (string text2 in files)
                                {
                                        string path = text2.Substring(text.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                                        string text3 = Path.Combine(destinationLibs, path);
                                        Directory.CreateDirectory(Path.GetDirectoryName(text3));
                                        File.Copy(text2, text3, overwrite: true);
                                }
                        }
                }
                catch (Exception ex)
                {
                        Debug.WriteLine("[Updater] 复制 libs 目录失败: " + ex.Message);
                }
	}

	private static string SerializeCommandLineArguments()
	{
		string[] commandLineArgs = Environment.GetCommandLineArgs();
		if (commandLineArgs == null || commandLineArgs.Length <= 1)
		{
			return string.Empty;
		}
		string text = string.Join("\u001f", commandLineArgs.Skip(1));
		if (string.IsNullOrEmpty(text))
		{
			return string.Empty;
		}
		return Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
	}

	private static string GetPlanVersionLabel(UpdatePlan plan)
	{
		if (plan == null)
		{
			return "最新版本";
		}
		string text = plan.DisplayVersion;
		if (string.IsNullOrWhiteSpace(text))
		{
			text = plan.TargetTag;
		}
		if (string.IsNullOrWhiteSpace(text))
		{
			return "最新版本";
		}
		return text.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? text : ("v" + text);
	}

	private void ScheduleBackgroundUpdateCheck()
	{
		if (_autoUpdateCheckScheduled || base.DesignMode)
		{
			return;
		}
		_autoUpdateCheckScheduled = true;
		_autoUpdateCheckCts?.Cancel();
		_autoUpdateCheckCts?.Dispose();
		_autoUpdateCheckCts = new CancellationTokenSource();
		CancellationToken token = _autoUpdateCheckCts.Token;
		Task.Run(async delegate
		{
			try
			{
				await Task.Delay(TimeSpan.FromSeconds(8.0), token).ConfigureAwait(continueOnCapturedContext: false);
				await CheckForUpdatesSilentlyAsync(token).ConfigureAwait(continueOnCapturedContext: false);
			}
			catch (OperationCanceledException)
			{
			}
			catch (Exception ex2)
			{
				Exception ex3 = ex2;
				Exception ex4 = ex3;
				DebugLogger.LogException("Update", ex4, "自动检查更新失败（忽略）");
			}
		}, token);
	}

        private async Task CheckForUpdatesSilentlyAsync(CancellationToken cancellationToken)
        {
                string currentVersion = VersionInfo.Version;
                using UpdateServiceClient client = new UpdateServiceClient("https://yt.chenz.cloud/update.php", "YTPlayer", currentVersion);
                UpdateCheckResult result = await PollUpdateStatusSilentlyAsync(client, currentVersion, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
                UpdateAsset asset = UpdateFormatting.SelectPreferredAsset(result.Response.Data?.Assets);
                if ((result.Response.Data?.UpdateAvailable ?? false) && asset != null)
                {
                        UpdatePlan plan = UpdatePlan.FromResponse(result.Response, asset, currentVersion);
                        string versionLabel = UpdateFormatting.FormatVersionLabel(plan, result.Response.Data?.Latest?.SemanticVersion);
                        ShowAutoUpdatePrompt(plan, versionLabel);
                }
        }

	private void ShowAutoUpdatePrompt(UpdatePlan plan, string? versionLabel)
	{
		if (plan == null || _autoUpdatePromptShown)
		{
			return;
		}
		_autoUpdatePromptShown = true;
		SafeInvoke(delegate
		{
			if (base.IsDisposed || _isFormClosing)
			{
				return;
			}
			using UpdateAvailablePromptDialog updateAvailablePromptDialog = new UpdateAvailablePromptDialog(plan, versionLabel);
			updateAvailablePromptDialog.UpdateLauncher = ExecuteUpdatePlan;
			updateAvailablePromptDialog.ShowDialog(this);
		});
	}

        private static async Task<UpdateCheckResult> PollUpdateStatusSilentlyAsync(UpdateServiceClient client, string currentVersion, CancellationToken cancellationToken)
        {
                UpdateCheckResult result;
                while (true)
                {
                        result = await client.CheckForUpdatesAsync(currentVersion, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
                        if (!result.ShouldPollForCompletion)
                        {
                                break;
                        }
			int delaySeconds = NormalizeUpdatePollDelay(result.GetRecommendedPollDelaySeconds());
			await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		}
		return result;
	}

	private static int NormalizeUpdatePollDelay(int seconds)
	{
		if (seconds < 2)
		{
			return 2;
		}
		if (seconds > 30)
		{
			return 30;
		}
		return seconds;
	}

	private void shortcutsMenuItem_Click(object sender, EventArgs e)
	{
		using KeyboardShortcutsDialog keyboardShortcutsDialog = new KeyboardShortcutsDialog();
		keyboardShortcutsDialog.ShowDialog(this);
	}

	private void aboutMenuItem_Click(object sender, EventArgs e)
	{
		using AboutDialog aboutDialog = new AboutDialog();
		aboutDialog.ShowDialog(this);
	}

	private void autoReadLyricsMenuItem_Click(object sender, EventArgs e)
	{
		ToggleAutoReadLyrics();
	}

	private void focusFollowPlaybackMenuItem_Click(object sender, EventArgs e)
	{
		ToggleFocusFollowPlayback();
	}

	private void preventSleepDuringPlaybackMenuItem_Click(object sender, EventArgs e)
	{
		TogglePreventSleepDuringPlayback();
	}

	private async void hideSequenceMenuItem_Click(object sender, EventArgs e)
	{
		await ToggleSequenceNumberHiddenAsync();
	}

	private void hideControlBarMenuItem_Click(object sender, EventArgs e)
	{
		ToggleControlBarHidden();
	}

	private void themeGrassFreshMenuItem_Click(object sender, EventArgs e)
	{
		ApplyThemeScheme(ThemeScheme.GrassFresh);
	}

	private void themeGrassSoftMenuItem_Click(object sender, EventArgs e)
	{
		ApplyThemeScheme(ThemeScheme.GrassSoft);
	}

	private void themeGrassWarmMenuItem_Click(object sender, EventArgs e)
	{
		ApplyThemeScheme(ThemeScheme.GrassWarm);
	}

	private void themeGrassMutedMenuItem_Click(object sender, EventArgs e)
	{
		ApplyThemeScheme(ThemeScheme.GrassMuted);
	}

	private void ApplyThemeScheme(ThemeScheme scheme)
	{
		ThemeManager.SetScheme(scheme);
		ApplySearchPanelStyle();
		ApplyResultListViewStyle();
		ApplyControlPanelStyle();
		ThemeManager.ApplyTheme(this);
		ThemeManager.ApplyTheme(trayContextMenu);
		UpdateThemeMenuChecks();
	}

	private void UpdateThemeMenuChecks()
	{
		ThemeScheme scheme = ThemeManager.Current.Scheme;
		SetMenuItemCheckedState(themeGrassFreshMenuItem, scheme == ThemeScheme.GrassFresh);
		SetMenuItemCheckedState(themeGrassSoftMenuItem, scheme == ThemeScheme.GrassSoft);
		SetMenuItemCheckedState(themeGrassWarmMenuItem, scheme == ThemeScheme.GrassWarm);
		SetMenuItemCheckedState(themeGrassMutedMenuItem, scheme == ThemeScheme.GrassMuted);
	}

private void ToggleAutoReadLyrics()
{
        _autoReadLyrics = !_autoReadLyrics;
        if (!_autoReadLyrics)
        {
                CancelPendingLyricSpeech();
        }
        try
        {
                SetMenuItemCheckedState(autoReadLyricsMenuItem, _autoReadLyrics);
                autoReadLyricsMenuItem.Text = (_autoReadLyrics ? "关闭歌词朗读\tF11" : "打开歌词朗读\tF11");
        }
        catch
        {
        }
		string text = (_autoReadLyrics ? "已开启歌词朗读" : "已关闭歌词朗读");
		AnnounceUiMessage(text, interrupt: true);
		UpdateStatusBar(text);
		Debug.WriteLine("[TTS] 歌词朗读: " + (_autoReadLyrics ? "开启" : "关闭"));
		SaveConfig();
	}

private void TogglePreventSleepDuringPlayback()
{
	_preventSleepDuringPlayback = !_preventSleepDuringPlayback;
	UpdatePreventSleepDuringPlaybackMenuItemText();
	UpdatePlaybackPowerRequests(IsPlaybackActiveState(GetCachedPlaybackState()));
	string message = (_preventSleepDuringPlayback ? "已开启播放时禁止睡眠/息屏" : "已关闭播放时禁止睡眠/息屏");
	AnnounceUiMessage(message, interrupt: true);
	UpdateStatusBar(message);
	Debug.WriteLine("[Power] 播放时禁止睡眠/息屏: " + (_preventSleepDuringPlayback ? "开启" : "关闭"));
	SaveConfig();
}

private void ToggleFocusFollowPlayback()
{
	_focusFollowPlayback = !_focusFollowPlayback;
	UpdateFocusFollowPlaybackMenuItemText();
	if (!_focusFollowPlayback)
	{
		ClearPlaybackFollowPendingState();
	}
	string message = (_focusFollowPlayback ? "\u5DF2\u5F00\u542F\u64AD\u653E\u7126\u70B9\u8DDF\u968F" : "\u5DF2\u5173\u95ED\u64AD\u653E\u7126\u70B9\u8DDF\u968F");
	AnnounceUiMessage(message, interrupt: true);
	UpdateStatusBar(message);
	Debug.WriteLine("[FocusFollow] \u64AD\u653E\u7126\u70B9\u8DDF\u968F: " + (_focusFollowPlayback ? "\u5F00\u542F" : "\u5173\u95ED"));
	SaveConfig();
}

private void UpdateFocusFollowPlaybackMenuItemText()
{
	try
	{
		SetMenuItemCheckedState(focusFollowPlaybackMenuItem, _focusFollowPlayback);
	}
	catch
	{
	}
}

private void UpdatePreventSleepDuringPlaybackMenuItemText()
{
	try
	{
		SetMenuItemCheckedState(preventSleepDuringPlaybackMenuItem, _preventSleepDuringPlayback);
	}
	catch
	{
	}
}

private void UpdateHideSequenceMenuItemText()
{
        try
        {
                SetMenuItemCheckedState(hideSequenceMenuItem, _hideSequenceNumbers);
                hideSequenceMenuItem.Text = (_hideSequenceNumbers ? "显示序号\tF8" : "隐藏序号\tF8");
        }
        catch
        {
        }
}

private void UpdateHideControlBarMenuItemText()
{
        try
        {
                SetMenuItemCheckedState(hideControlBarMenuItem, _hideControlBar);
                hideControlBarMenuItem.Text = (_hideControlBar ? "显示控制栏\tF7" : "隐藏控制栏\tF7");
        }
        catch
        {
        }
}

private void AnnounceFocusedListViewItemAfterSequenceToggle()
{
	if (resultListView == null || !resultListView.IsHandleCreated || !resultListView.ContainsFocus)
	{
		return;
	}
	int focusedIndex = GetFocusedListViewIndex();
	if (focusedIndex >= 0)
	{
		QueueFocusedListViewItemRefreshAnnouncement(focusedIndex, interruptAnnouncement: false);
	}
}

	private async Task ToggleSequenceNumberHiddenAsync()
	{
		_hideSequenceNumbers = !_hideSequenceNumbers;
		UpdateHideSequenceMenuItemText();
		RefreshSequenceDisplayInPlace();
		AnnounceFocusedListViewItemAfterSequenceToggle();
		string message = (_hideSequenceNumbers ? "已隐藏序号" : "已显示序号");
		AnnounceUiMessage(message, interrupt: true);
		UpdateStatusBar(message);
		Debug.WriteLine("[TTS] 序号隐藏: " + (_hideSequenceNumbers ? "开启" : "关闭"));
		SaveConfig();
		await Task.CompletedTask;
	}

	private void ApplyControlBarVisibility()
	{
		if (controlPanel == null)
		{
			return;
		}
		bool containsFocus = controlPanel.ContainsFocus;
		controlPanel.Visible = !_hideControlBar;
		if (_hideControlBar && containsFocus)
		{
			if (resultListView != null && resultListView.CanFocus)
			{
				resultListView.Focus();
			}
			else if (searchTextBox != null && searchTextBox.CanFocus)
			{
				searchTextBox.Focus();
			}
		}
	}

	private void ToggleControlBarHidden()
	{
		_hideControlBar = !_hideControlBar;
		UpdateHideControlBarMenuItemText();
		ApplyControlBarVisibility();
		string text = (_hideControlBar ? "已隐藏控制栏" : "已显示控制栏");
		AnnounceUiMessage(text, interrupt: true);
		UpdateStatusBar(text);
		Debug.WriteLine("[TTS] 控制栏隐藏: " + (_hideControlBar ? "开启" : "关闭"));
		SaveConfig();
	}

	private void insertPlayMenuItem_Click(object sender, EventArgs e)
	{
		SongInfo selectedSongFromContextMenu = GetSelectedSongFromContextMenu(sender);
		if (selectedSongFromContextMenu == null)
		{
			ShowContextSongMissingMessage("插播的歌曲");
			return;
		}
		_playbackQueue.SetPendingInjection(selectedSongFromContextMenu, _currentViewSource);
		UpdateStatusBar("已设置下一首插播：" + selectedSongFromContextMenu.Name + " - " + selectedSongFromContextMenu.Artist);
		Debug.WriteLine("[MainForm] 设置插播歌曲: " + selectedSongFromContextMenu.Name);
		RefreshNextSongPreload();
	}

	private async Task<(PlaylistInfo Playlist, List<SongInfo> Songs, string StatusText)?> BuildPlaylistSkeletonViewDataAsync(Func<CancellationToken, Task<PlaylistInfo?>> playlistFactory, string operationName, CancellationToken cancellationToken)
	{
		if (playlistFactory == null)
		{
			throw new ArgumentNullException("playlistFactory");
		}
		cancellationToken.ThrowIfCancellationRequested();
		PlaylistInfo playlist = await playlistFactory(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		if (playlist == null)
		{
			return null;
		}
		if (string.IsNullOrWhiteSpace(playlist.Id))
		{
			throw new InvalidOperationException("\ufffd赥ȱ\ufffd\ufffd\ufffd\ufffdЧ\ufffdı\ufffdʶ\ufffd\ufffd");
		}
		string playlistId = playlist.Id;
		string playlistName = (string.IsNullOrWhiteSpace(playlist.Name) ? ("\ufffd赥 " + playlistId) : playlist.Name);
		          string viewSource = (string.IsNullOrWhiteSpace(operationName) ? ("playlist:" + playlistId) : operationName);
          List<SongInfo> skeletonSongs = playlist.Songs ?? new List<SongInfo>();
          int loadedCount = skeletonSongs.Count;
          foreach (SongInfo song in skeletonSongs)
          {
                  if (string.IsNullOrWhiteSpace(song.ViewSource))
                  {
                          song.ViewSource = viewSource;
                  }
          }
          if (TryGetLongPlaylistPlaceholderState(playlist, skeletonSongs, out List<SongInfo> placeholderSongs))
          {
                  skeletonSongs = placeholderSongs;
                  playlist.Songs = skeletonSongs;
          }
          string statusText;
          if (loadedCount > 0)
          {
                  if (playlist.TrackCount > 0 && loadedCount < playlist.TrackCount)
                  {
                          statusText = $"歌单: {playlistName} · 已加载 {loadedCount}/{playlist.TrackCount} 首";
                  }
                  else
                  {
                          statusText = $"歌单: {playlistName} · 共 {loadedCount} 首";
                  }
          }
          else if (playlist.TrackCount > 0)
          {
                  statusText = $"歌单: {playlistName} · 共 {playlist.TrackCount} 首";
          }
          else
          {
                  statusText = "歌单: " + playlistName + " 暂无歌曲";
          }
          return (playlist, skeletonSongs, statusText);
	}

	private async Task<(PlaylistInfo Playlist, List<SongInfo> Songs, string StatusText)?> BuildPlaylistSongsViewDataAsync(Func<CancellationToken, Task<PlaylistInfo?>> playlistFactory, string operationName, CancellationToken cancellationToken)
	{
		if (playlistFactory == null)
		{
			throw new ArgumentNullException("playlistFactory");
		}
		cancellationToken.ThrowIfCancellationRequested();
		PlaylistInfo playlist = await playlistFactory(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		if (playlist == null)
		{
			return null;
		}
		if (string.IsNullOrWhiteSpace(playlist.Id))
		{
			throw new InvalidOperationException("歌单缺少有效的标识。");
		}
		string playlistId = playlist.Id;
		List<SongInfo> resolvedSongs = (await FetchWithRetryUntilCancel((CancellationToken ct) => _apiClient.GetPlaylistSongsAsync(playlistId), string.IsNullOrWhiteSpace(operationName) ? ("playlist:" + playlistId) : operationName, cancellationToken, delegate(int attempt, Exception _)
		{
			SafeInvoke(delegate
			{
				UpdateStatusBar($"加载歌单失败，正在重试（第 {attempt} 次）...");
			});
		}).ConfigureAwait(continueOnCapturedContext: false)) ?? new List<SongInfo>();
		string playlistName = (string.IsNullOrWhiteSpace(playlist.Name) ? ("歌单 " + playlistId) : playlist.Name);
		string statusText = ((resolvedSongs.Count == 0) ? (playlistName + " 暂无歌曲") : $"歌单: {playlistName}，共 {resolvedSongs.Count} 首歌曲");
		return (playlist, resolvedSongs, statusText);
	}

	private async Task<(AlbumInfo Album, List<SongInfo> Songs, string StatusText)?> BuildAlbumSkeletonViewDataAsync(Func<CancellationToken, Task<AlbumInfo?>> albumFactory, string operationName, CancellationToken cancellationToken)
	{
		if (albumFactory == null)
		{
			throw new ArgumentNullException("albumFactory");
		}
		cancellationToken.ThrowIfCancellationRequested();
		AlbumInfo album = await albumFactory(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		if (album == null)
		{
			return null;
		}
		if (string.IsNullOrWhiteSpace(album.Id))
		{
			throw new InvalidOperationException("ר\ufffd\ufffdȱ\ufffd\ufffd\ufffd\ufffdЧ\ufffdı\ufffdʶ\ufffd\ufffd");
		}
		string albumId = album.Id;
		string albumName = (string.IsNullOrWhiteSpace(album.Name) ? ("ר\ufffd\ufffd " + albumId) : album.Name);
		string viewSource = (string.IsNullOrWhiteSpace(operationName) ? ("album:" + albumId) : operationName);
		List<SongInfo> skeletonSongs = album.Songs ?? new List<SongInfo>();
		foreach (SongInfo song in skeletonSongs)
		{
			if (string.IsNullOrWhiteSpace(song.ViewSource))
			{
				song.ViewSource = viewSource;
			}
		}
		string statusText = ((skeletonSongs.Count != 0) ? $"ר\ufffd\ufffd: {albumName}\ufffd\ufffd\ufffd\ufffd {skeletonSongs.Count} \ufffd\u05f8\ufffd\ufffd\ufffd (\ufffdǼ\ufffd)" : ((album.TrackCount > 0) ? $"ר\ufffd\ufffd: {albumName}\ufffd\ufffd\ufffd\ufffd {album.TrackCount} \ufffd\u05f8\ufffd\ufffd\ufffd (\ufffdǼ\ufffd, \ufffd\ufffdȱ\ufffd\ufffd\ufffd\ufffd)" : ("ר\ufffd\ufffd: " + albumName + "\ufffd\ufffd\u05fc\ufffd\ufffd\ufffd\ufffd\ufffd\ufffd")));
		return (album, skeletonSongs, statusText);
	}

	private async Task<(AlbumInfo Album, List<SongInfo> Songs, string StatusText)?> BuildAlbumSongsViewDataAsync(Func<CancellationToken, Task<AlbumInfo?>> albumFactory, string operationName, CancellationToken cancellationToken)
	{
		if (albumFactory == null)
		{
			throw new ArgumentNullException("albumFactory");
		}
		cancellationToken.ThrowIfCancellationRequested();
		AlbumInfo album = await albumFactory(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		if (album == null)
		{
			return null;
		}
		if (string.IsNullOrWhiteSpace(album.Id))
		{
			throw new InvalidOperationException("专辑缺少有效的标识。");
		}
		string albumId = album.Id;
		List<SongInfo> resolvedSongs = (await FetchWithRetryUntilCancel((CancellationToken ct) => _apiClient.GetAlbumSongsAsync(albumId), string.IsNullOrWhiteSpace(operationName) ? ("album:" + albumId) : operationName, cancellationToken, delegate(int attempt, Exception _)
		{
			SafeInvoke(delegate
			{
				UpdateStatusBar($"加载专辑失败，正在重试（第 {attempt} 次）...");
			});
		}).ConfigureAwait(continueOnCapturedContext: false)) ?? new List<SongInfo>();
		string albumName = (string.IsNullOrWhiteSpace(album.Name) ? ("专辑 " + albumId) : album.Name);
		string statusText = ((resolvedSongs.Count == 0) ? (albumName + " 暂无歌曲") : $"专辑: {albumName}，共 {resolvedSongs.Count} 首歌曲");
		return (album, resolvedSongs, statusText);
	}

	private static string BuildPlaylistStatusText(PlaylistInfo playlist, int songCount, bool enriched)
	{
		string text = (string.IsNullOrWhiteSpace(playlist?.Name) ? ("歌单 " + playlist?.Id) : playlist.Name);
		string value = (enriched ? "（已丰富）" : string.Empty);
		return (songCount <= 0) ? (text + " 暂无歌曲") : $"歌单: {text} · {songCount} 首{value}";
	}

	private static string BuildAlbumStatusText(AlbumInfo album, int songCount, bool enriched)
	{
		string text = (string.IsNullOrWhiteSpace(album?.Name) ? ("专辑 " + album?.Id) : album.Name);
		string value = (enriched ? "（已丰富）" : string.Empty);
		return (songCount <= 0) ? (text + " 暂无歌曲") : $"专辑: {text} · {songCount} 首{value}";
	}

	private static List<SongInfo> MergeSongsById(IReadOnlyList<SongInfo> skeleton, List<SongInfo> enriched, string? viewSource)
	{
		if (enriched == null)
		{
			enriched = new List<SongInfo>();
		}
		Dictionary<string, SongInfo> dictionary = new Dictionary<string, SongInfo>(StringComparer.OrdinalIgnoreCase);
		foreach (SongInfo item in enriched)
		{
			if (item != null && !string.IsNullOrWhiteSpace(item.Id) && !dictionary.ContainsKey(item.Id))
			{
				dictionary[item.Id] = item;
			}
		}
		List<SongInfo> list = new List<SongInfo>(Math.Max(skeleton?.Count ?? 0, enriched.Count));
		if (skeleton != null)
		{
			foreach (SongInfo item2 in skeleton)
			{
				if (item2 != null && !string.IsNullOrWhiteSpace(item2.Id) && dictionary.TryGetValue(item2.Id, out var value))
				{
					list.Add(MergeSongFields(item2, value, viewSource));
				}
				else if (item2 != null)
				{
					list.Add(MergeSongFields(item2, item2, viewSource));
				}
			}
		}
		foreach (SongInfo song in dictionary.Values)
		{
			if (skeleton == null || !skeleton.Any((SongInfo s) => !string.IsNullOrWhiteSpace(s?.Id) && string.Equals(s?.Id, song.Id, StringComparison.OrdinalIgnoreCase)))
			{
				list.Add(MergeSongFields(song, song, viewSource));
			}
		}
		return list;
	}

	private static SongInfo MergeSongFields(SongInfo skeleton, SongInfo enriched, string? viewSource)
	{
		SongInfo songInfo = enriched ?? new SongInfo();
		if (skeleton == null)
		{
			return songInfo;
		}
		if (!string.IsNullOrWhiteSpace(viewSource))
		{
			songInfo.ViewSource = viewSource;
		}
		else if (string.IsNullOrWhiteSpace(songInfo.ViewSource))
		{
			songInfo.ViewSource = skeleton.ViewSource ?? string.Empty;
		}
		if (string.IsNullOrWhiteSpace(songInfo.Artist) && !string.IsNullOrWhiteSpace(skeleton.Artist))
		{
			songInfo.Artist = skeleton.Artist;
			songInfo.ArtistNames = new List<string>(skeleton.ArtistNames ?? new List<string>());
			songInfo.ArtistIds = new List<long>(skeleton.ArtistIds ?? new List<long>());
		}
		if (string.IsNullOrWhiteSpace(songInfo.Album))
		{
			songInfo.Album = skeleton.Album;
			songInfo.AlbumId = skeleton.AlbumId;
		}
		if (songInfo.Duration <= 0)
		{
			songInfo.Duration = skeleton.Duration;
		}
		if (string.IsNullOrWhiteSpace(songInfo.Name))
		{
			songInfo.Name = skeleton.Name;
		}
		return songInfo;
	}

	private static List<SongInfo> BuildPlaylistPlaceholderSongs(int totalCount, string? viewSource)
	{
		int num = Math.Max(0, totalCount);
		List<SongInfo> list = new List<SongInfo>(num);
		for (int i = 0; i < num; i = checked(i + 1))
		{
			list.Add(CreatePlaceholderSong(viewSource));
		}
		return list;
	}

	private bool TryGetLongPlaylistPlaceholderState(PlaylistInfo playlist, List<SongInfo> skeletonSongs, out List<SongInfo> placeholderSongs)
	{
		placeholderSongs = new List<SongInfo>();
		if (playlist == null)
		{
			return false;
		}
		int trackCount = playlist.TrackCount;
		if (trackCount < LongPlaylistSongThreshold || trackCount <= 0)
		{
			return false;
		}
		int num = skeletonSongs?.Count ?? 0;
		bool flag = false;
		if (skeletonSongs != null && skeletonSongs.Count > 0)
		{
			foreach (SongInfo skeletonSong in skeletonSongs)
			{
				if (IsPlaceholderSong(skeletonSong))
				{
					flag = true;
					break;
				}
			}
		}
		if (num >= trackCount && !flag)
		{
			return false;
		}
		placeholderSongs = BuildPlaylistPlaceholderSongs(trackCount, skeletonSongs?.FirstOrDefault()?.ViewSource);
		if (skeletonSongs != null && skeletonSongs.Count > 0)
		{
			int num2 = Math.Min(trackCount, skeletonSongs.Count);
			for (int i = 0; i < num2; i = checked(i + 1))
			{
				SongInfo songInfo = skeletonSongs[i];
				if (songInfo != null && !string.IsNullOrWhiteSpace(songInfo.Id))
				{
					placeholderSongs[i] = songInfo;
				}
			}
		}
		return true;
	}

	private void ApplyPlaceholderSongsToCurrentPlaylist(PlaylistInfo playlist, List<SongInfo> placeholders)
	{
		if (playlist != null && placeholders != null)
		{
			playlist.Songs = placeholders;
		}
	}

	private bool TryApplyLongPlaylistPlaceholdersInPlace(PlaylistInfo playlist, List<SongInfo> placeholders)
	{
		if (placeholders == null || resultListView == null)
		{
			return false;
		}
		int count = resultListView.Items.Count;
		if (count <= 0 || placeholders.Count < count)
		{
			return false;
		}
		if (_currentSongs == null || _currentSongs.Count < count)
		{
			return false;
		}
		checked
		{
			for (int i = 0; i < count; i++)
			{
				string text = placeholders[i]?.Id ?? string.Empty;
				string text2 = _currentSongs[i]?.Id ?? string.Empty;
				if ((!string.IsNullOrWhiteSpace(text) || !string.IsNullOrWhiteSpace(text2)) && !string.Equals(text, text2, StringComparison.OrdinalIgnoreCase))
				{
					return false;
				}
			}
			int selectedListViewIndex = GetSelectedListViewIndex();
			int num = -1;
			try
			{
				num = resultListView.TopItem?.Index ?? (-1);
			}
			catch
			{
				num = -1;
			}
			_currentSongs = placeholders;
			ApplySongLikeStates(_currentSongs);
			if (playlist != null && playlist.Songs != _currentSongs)
			{
				playlist.Songs = _currentSongs;
			}
			resultListView.BeginUpdate();
			try
			{
				for (int j = count; j < placeholders.Count; j++)
				{
					ListViewItem listViewItem = new ListViewItem(new string[6]);
					FillListViewItemFromSongInfo(listViewItem, placeholders[j], j + 1);
					listViewItem.Tag = j;
					resultListView.Items.Add(listViewItem);
				}
			}
			finally
			{
				EndListViewUpdateAndRefreshAccessibility();
			}
			if (selectedListViewIndex >= 0 && selectedListViewIndex < resultListView.Items.Count && !HasListViewSelection())
			{
				try
				{
					ListViewItem listViewItem2 = resultListView.Items[selectedListViewIndex];
					listViewItem2.Selected = true;
					listViewItem2.Focused = true;
				}
				catch
				{
				}
			}
			if (num >= 0 && num < resultListView.Items.Count)
			{
				try
				{
					resultListView.Items[num].EnsureVisible();
				}
				catch
				{
				}
			}
			ListViewItem selectedListViewItemSafe = GetSelectedListViewItemSafe();
			if (selectedListViewItemSafe != null)
			{
				UpdateListViewItemAccessibilityProperties(selectedListViewItemSafe, IsNvdaRunningCached());
			}
			return true;
		}
	}

	private async Task<List<string>> FetchPlaylistTrackIdsAsync(string playlistId, CancellationToken cancellationToken)
	{
		List<string> ids = new List<string>();
		if (string.IsNullOrWhiteSpace(playlistId))
		{
			return ids;
		}
		Dictionary<string, object> infoData = new Dictionary<string, object>
		{
			{ "id", playlistId },
			{ "n", 1 },
			{ "s", 8 }
		};
		JObject infoResponse = await _apiClient.PostWeApiAsync<JObject>("/v3/playlist/detail", infoData, 0, skipErrorHandling: false, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		int code = infoResponse["code"]?.Value<int>() ?? 0;
		if (code != 200)
		{
			string msg = infoResponse["message"]?.Value<string>() ?? "未知错误";
			throw new Exception($"获取歌单详情失败: code={code}, message={msg}");
		}
		JArray trackIds = infoResponse["playlist"]?["trackIds"] as JArray;
		if (trackIds == null || trackIds.Count == 0)
		{
			return ids;
		}
		foreach (JToken item in trackIds)
		{
			string id = item?["id"]?.ToString();
			if (!string.IsNullOrWhiteSpace(id))
			{
				ids.Add(id);
			}
		}
		return ids;
	}

	private async Task<List<SongInfo>> FetchPlaylistSongsByIdsBatchAsync(List<string> ids, CancellationToken cancellationToken)
	{
		if (ids == null || ids.Count == 0)
		{
			return new List<SongInfo>();
		}
		List<string> filtered = ids.Where((string id) => !string.IsNullOrWhiteSpace(id)).ToList();
		if (filtered.Count == 0)
		{
			return new List<SongInfo>();
		}
		return await _apiClient.GetSongsByIdsAsync(filtered, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
	}

	private async Task EnrichCurrentPlaylistSongsAsync(PlaylistInfo playlist, List<SongInfo> skeleton, string viewSource, string accessibleName)
	{
		CancellationToken viewToken = GetCurrentViewContentToken();
		checked
		{
			try
			{
				if (playlist == null || string.IsNullOrWhiteSpace(playlist.Id))
				{
					return;
				}
				string playlistName = (string.IsNullOrWhiteSpace(playlist.Name) ? ("歌单 " + playlist.Id) : playlist.Name);
				if (TryGetLongPlaylistPlaceholderState(playlist, skeleton, out var _))
				{
					List<string> trackIds = new List<string>();
					try
					{
						trackIds = await FetchWithRetryUntilCancel((CancellationToken ct) => FetchPlaylistTrackIdsAsync(playlist.Id, ct), "playlist:trackIds:" + playlist.Id, viewToken, delegate(int attempt, Exception _)
						{
							SafeInvoke(delegate
							{
								UpdateStatusBar($"重新获取歌单曲目失败，正在重试第 {attempt} 次...");
							});
						}).ConfigureAwait(continueOnCapturedContext: false);
					}
					catch (Exception ex)
					{
						Exception ex2 = ex;
						if (!TryHandleOperationCancelled(ex2, "打开歌单已取消"))
						{
							Debug.WriteLine($"[EnrichPlaylist] 获取歌单 trackIds 失败: {ex2}");
						}
					}
					if (viewToken.IsCancellationRequested)
					{
						return;
					}
					if (trackIds != null && trackIds.Count > 0)
					{
						int totalCount = ((trackIds.Count > 0) ? trackIds.Count : playlist.TrackCount);
						if (totalCount > 0)
						{
                                                        Dictionary<string, int> idIndexMap = new Dictionary<string, int>(totalCount, StringComparer.OrdinalIgnoreCase);
                                                        for (int i = 0; i < trackIds.Count; i++)
                                                        {
                                                                string id = trackIds[i];
                                                                if (!string.IsNullOrWhiteSpace(id) && !idIndexMap.ContainsKey(id))
                                                                {
                                                                        idIndexMap.Add(id, i);
                                                                }
                                                        }
                                                        CachePlaylistTrackIndexMap(viewSource, idIndexMap);
												int pendingFocusIndexFromIds = -1;
												if (CanApplyPendingSongFocusForView(viewSource))
												{
													idIndexMap.TryGetValue(_pendingSongFocusId, out pendingFocusIndexFromIds);
												}
												List<SongInfo> placeholders = BuildPlaylistPlaceholderSongs(totalCount, skeleton?.FirstOrDefault()?.ViewSource ?? viewSource);
							if (skeleton != null && skeleton.Count > 0)
							{
								foreach (SongInfo song in skeleton)
								{
									if (song != null && !string.IsNullOrWhiteSpace(song.Id) && idIndexMap.TryGetValue(song.Id, out var index))
									{
										placeholders[index] = MergeSongFields(song, song, viewSource);
									}
								}
							}
							bool isCurrentView = true;
							await ExecuteOnUiThreadAsync(delegate
							{
								if (!string.Equals(_currentViewSource, viewSource, StringComparison.OrdinalIgnoreCase))
								{
									isCurrentView = false;
								}
								else
								{
									ApplyPlaceholderSongsToCurrentPlaylist(playlist, placeholders);
									if (!TryApplyLongPlaylistPlaceholdersInPlace(playlist, placeholders))
									{
										RequestListFocus(null, -1);
										DisplaySongs(placeholders, showPagination: false, hasNextPage: false, 1, preserveSelection: true, viewSource, accessibleName, skipAvailabilityCheck: true, announceHeader: false);
									}
																if (playlist.Songs != _currentSongs)
																{
																	playlist.Songs = _currentSongs;
																}
																EnsurePlaybackQueueCoversCurrentView();
																if (pendingFocusIndexFromIds >= 0 && resultListView != null && !IsListAutoFocusSuppressed && pendingFocusIndexFromIds < resultListView.Items.Count)
																{
																	int focusedIndex = GetFocusedListViewIndex();
																	if (focusedIndex != pendingFocusIndexFromIds)
																	{
																		EnsureListSelectionWithoutFocus(pendingFocusIndexFromIds);
																		if (resultListView.CanFocus)
																		{
																			resultListView.Focus();
																		}
																	}
																	_pendingSongFocusId = null;
																	_pendingSongFocusViewSource = null;
																	_pendingSongFocusSatisfied = true;
                                                                                _pendingSongFocusSatisfiedViewSource = viewSource;
                                                                                _deferredPlaybackFocusViewSource = null;
																}
															}
														}).ConfigureAwait(continueOnCapturedContext: false);
							if (!isCurrentView || viewToken.IsCancellationRequested)
							{
								return;
							}
							List<SongInfo> currentSongs = _currentSongs ?? placeholders;
							bool[] loadedFlags = new bool[totalCount];
							int loadedCount = 0;
							for (int i2 = 0; i2 < totalCount; i2++)
							{
								string expectedId = trackIds[i2];
								if (i2 < currentSongs.Count)
								{
									SongInfo existing = currentSongs[i2];
									if (existing != null && !string.IsNullOrWhiteSpace(existing.Id) && string.Equals(existing.Id, expectedId, StringComparison.OrdinalIgnoreCase))
									{
										loadedFlags[i2] = true;
										loadedCount++;
									}
								}
							}
							UpdateStatusBar($"歌单: {playlistName} · 已加载 {loadedCount}/{totalCount} 首");
							List<int> pendingBatchStarts = new List<int>();
							for (int start = 0; start < totalCount; start += 200)
							{
								int end = Math.Min(start + 200, totalCount);
								bool hasMissing = false;
								for (int i3 = start; i3 < end; i3++)
								{
									if (!loadedFlags[i3])
									{
										hasMissing = true;
										break;
									}
								}
								if (hasMissing)
								{
									pendingBatchStarts.Add(start);
								}
							}
							int batchFetched = 0;
							while (pendingBatchStarts.Count > 0)
							{
								viewToken.ThrowIfCancellationRequested();
								int selectedIndex = await GetSelectedListViewIndexAsync().ConfigureAwait(continueOnCapturedContext: false);
								int selectedBatchStart = ((selectedIndex >= 0) ? (unchecked(selectedIndex / 200) * 200) : (-1));
								int batchStart = ((selectedBatchStart >= 0 && pendingBatchStarts.Contains(selectedBatchStart)) ? selectedBatchStart : pendingBatchStarts[0]);
								pendingBatchStarts.Remove(batchStart);
								int batchEnd = Math.Min(batchStart + 200, totalCount);
								List<string> batchIds = new List<string>();
								for (int i4 = batchStart; i4 < batchEnd; i4++)
								{
									if (!loadedFlags[i4])
									{
										string id2 = trackIds[i4];
										if (!string.IsNullOrWhiteSpace(id2))
										{
											batchIds.Add(id2);
										}
									}
								}
								if (batchIds.Count == 0)
								{
									continue;
								}
								if (batchFetched > 0)
								{
									await Task.Delay(1500, viewToken).ConfigureAwait(continueOnCapturedContext: false);
								}
								batchFetched++;
								List<SongInfo> fetchedSongs = await FetchPlaylistSongsByIdsBatchAsync(batchIds, viewToken).ConfigureAwait(continueOnCapturedContext: false);
								if (viewToken.IsCancellationRequested || fetchedSongs == null || fetchedSongs.Count == 0)
								{
									continue;
								}
								Dictionary<int, SongInfo> fetchedByIndex = new Dictionary<int, SongInfo>();
								foreach (SongInfo song2 in fetchedSongs)
								{
									if (song2 != null && !string.IsNullOrWhiteSpace(song2.Id) && idIndexMap.TryGetValue(song2.Id, out var index2) && index2 >= 0 && index2 < totalCount)
									{
										fetchedByIndex[index2] = song2;
									}
								}
								if (fetchedByIndex.Count == 0)
								{
									continue;
								}
								ApplySongLikeStates(fetchedByIndex.Values);
								List<SongInfo> mergedSongs = new List<SongInfo>();
								Dictionary<int, SongInfo> mergedSongsByIndex = new Dictionary<int, SongInfo>();
								bool viewStillCurrent = true;
								await ExecuteOnUiThreadAsync(delegate
								{
									if (!string.Equals(_currentViewSource, viewSource, StringComparison.OrdinalIgnoreCase))
									{
										viewStillCurrent = false;
									}
									else
									{
										Dictionary<int, SongInfo> dictionary = new Dictionary<int, SongInfo>();
										foreach (KeyValuePair<int, SongInfo> item in fetchedByIndex)
										{
											int key = item.Key;
											if (key >= 0 && key < _currentSongs.Count)
											{
												SongInfo skeleton2 = _currentSongs[key];
												SongInfo value = MergeSongFields(skeleton2, item.Value, viewSource);
												_currentSongs[key] = value;
												dictionary[key] = value;
												if (!loadedFlags[key])
												{
													loadedFlags[key] = true;
													int num = loadedCount;
													loadedCount = num + 1;
												}
											}
										}
										if (dictionary.Count > 0)
										{
											PatchSongItemsInPlace(dictionary);
											TryDispatchPendingPlaceholderPlayback(dictionary);
											mergedSongs = dictionary.Values.ToList();
											mergedSongsByIndex = new Dictionary<int, SongInfo>(dictionary);
										}
										if (playlist.Songs != _currentSongs)
										{
											playlist.Songs = _currentSongs;
										}
									}
								}).ConfigureAwait(continueOnCapturedContext: false);
								if (!viewStillCurrent)
								{
									return;
								}
								if (mergedSongsByIndex.Count > 0)
								{
									UpdatePlaybackQueueSongs(mergedSongsByIndex, viewSource);
								}
								else if (mergedSongs.Count > 0)
								{
									UpdatePlaybackQueueSongs(mergedSongs);
								}
								UpdateStatusBar($"歌单: {playlistName} · 已加载 {loadedCount}/{totalCount} 首");
							}
							if (viewToken.IsCancellationRequested)
							{
								return;
							}
							await ExecuteOnUiThreadAsync(delegate
							{
								if (string.Equals(_currentViewSource, viewSource, StringComparison.OrdinalIgnoreCase))
								{
									UpdateStatusBar(BuildPlaylistStatusText(playlist, _currentSongs.Count, enriched: true));
									ScheduleAvailabilityCheck(_currentSongs);
								}
							}).ConfigureAwait(continueOnCapturedContext: false);
							return;
						}
					}
				}
				List<SongInfo> songs = await FetchWithRetryUntilCancel((CancellationToken ct) => _apiClient.GetPlaylistSongsAsync(playlist.Id, ct), viewSource, viewToken, delegate(int attempt, Exception _)
				{
					SafeInvoke(delegate
					{
						UpdateStatusBar($"重新获取歌单失败，正在重试第 {attempt} 次...");
					});
				}).ConfigureAwait(continueOnCapturedContext: false);
				if (viewToken.IsCancellationRequested || songs == null)
				{
					return;
				}
				List<SongInfo> merged = MergeSongsById(skeleton ?? new List<SongInfo>(), songs, viewSource);
				await ExecuteOnUiThreadAsync(delegate
				{
					if (string.Equals(_currentViewSource, viewSource, StringComparison.OrdinalIgnoreCase))
					{
						int pendingFocusIndex = ((resultListView.SelectedIndices.Count > 0) ? resultListView.SelectedIndices[0] : (-1));
						bool flag = _currentSongs != null && _currentSongs.Count == merged.Count;
						if (flag)
						{
							for (int j = 0; j < merged.Count; j++)
							{
								string a = _currentSongs[j]?.Id;
								string b = merged[j]?.Id;
								if (!string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
								{
									flag = false;
									break;
								}
							}
						}
						if (flag)
						{
							PatchSongs(merged, 1, skipAvailabilityCheck: true, showPagination: false, hasPreviousPage: false, hasNextPage: false, pendingFocusIndex);
						}
						else
						{
							DisplaySongs(merged, showPagination: false, hasNextPage: false, 1, preserveSelection: true, viewSource, accessibleName, skipAvailabilityCheck: true);
						}
						UpdateStatusBar(BuildPlaylistStatusText(playlist, merged.Count, enriched: true));
						ScheduleAvailabilityCheck(merged);
					}
				}).ConfigureAwait(continueOnCapturedContext: false);
			}
			catch (Exception ex)
			{
				Exception ex3 = ex;
				Exception ex4 = ex3;
				if (!TryHandleOperationCancelled(ex4, "打开歌单已取消"))
				{
					Debug.WriteLine($"[EnrichPlaylist] 丰富歌单失败: {ex4}");
				}
			}
		}
	}

	private async Task EnrichCurrentAlbumSongsAsync(AlbumInfo album, List<SongInfo> skeleton, string viewSource, string accessibleName)
	{
		CancellationToken viewToken = GetCurrentViewContentToken();
		try
		{
			List<SongInfo> songs = await FetchWithRetryUntilCancel((CancellationToken ct) => _apiClient.GetAlbumSongsAsync(album.Id), viewSource, viewToken, delegate(int attempt, Exception _)
			{
				SafeInvoke(delegate
				{
					UpdateStatusBar($"重新获取专辑失败，正在重试第 {attempt} 次...");
				});
			}).ConfigureAwait(continueOnCapturedContext: false);
			if (viewToken.IsCancellationRequested || songs == null)
			{
				return;
			}
			List<SongInfo> merged = MergeSongsById(skeleton ?? new List<SongInfo>(), songs, viewSource);
			await ExecuteOnUiThreadAsync(delegate
			{
				if (string.Equals(_currentViewSource, viewSource, StringComparison.OrdinalIgnoreCase))
				{
					int pendingFocusIndex = ((resultListView.SelectedIndices.Count > 0) ? resultListView.SelectedIndices[0] : (-1));
					bool flag = _currentSongs != null && _currentSongs.Count == merged.Count;
					if (flag)
					{
						for (int i = 0; i < merged.Count; i = checked(i + 1))
						{
							string a = _currentSongs[i]?.Id;
							string b = merged[i]?.Id;
							if (!string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
							{
								flag = false;
								break;
							}
						}
					}
					if (flag)
					{
						PatchSongs(merged, 1, skipAvailabilityCheck: true, showPagination: false, hasPreviousPage: false, hasNextPage: false, pendingFocusIndex);
					}
					else
					{
						DisplaySongs(merged, showPagination: false, hasNextPage: false, 1, preserveSelection: true, viewSource, accessibleName, skipAvailabilityCheck: true, announceHeader: false);
					}
					UpdateStatusBar(BuildAlbumStatusText(album, merged.Count, enriched: true));
					ScheduleAvailabilityCheck(merged);
				}
			}).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Exception ex3 = ex2;
			if (!TryHandleOperationCancelled(ex3, "打开专辑已取消"))
			{
				Debug.WriteLine($"[EnrichAlbum] 丰富专辑失败: {ex3}");
			}
		}
	}

	private async Task OpenPlaylist(PlaylistInfo playlist, bool skipSave = false, bool preserveSelection = false)
	{
		try
		{
			if (playlist == null || string.IsNullOrWhiteSpace(playlist.Id))
			{
				MessageBox.Show("无法打开歌单，缺少有效信息。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				UpdateStatusBar("打开歌单失败");
				return;
			}
			int pendingIndex = ((preserveSelection && resultListView.SelectedIndices.Count > 0) ? resultListView.SelectedIndices[0] : 0);
			string playlistId = playlist.Id;
			string viewSource = "playlist:" + playlistId;
			string accessibleName = (string.IsNullOrWhiteSpace(playlist.Name) ? ("歌单 " + playlistId) : playlist.Name);
			if (!skipSave)
			{
				SaveNavigationState();
			}
			ViewLoadRequest request = new ViewLoadRequest(viewSource, accessibleName, "正在加载歌单: " + accessibleName + "...", !skipSave, pendingIndex);
			ViewLoadResult<(PlaylistInfo Playlist, List<SongInfo> Songs, string StatusText)?> loadResult = await RunViewLoadAsync(request, (CancellationToken token) => BuildPlaylistSkeletonViewDataAsync(async delegate(CancellationToken ct)
			{
				PlaylistInfo detail = await _apiClient.GetPlaylistDetailAsync(playlistId).ConfigureAwait(continueOnCapturedContext: false);
				ct.ThrowIfCancellationRequested();
				return detail ?? playlist;
			}, viewSource, token), "打开歌单已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				(PlaylistInfo Playlist, List<SongInfo> Songs, string StatusText)? data = loadResult.Value;
				if (!data.HasValue)
				{
					MessageBox.Show("歌单 " + accessibleName + " 当前不可用，可能已被删除或权限受限。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
					UpdateStatusBar("打开歌单失败");
					return;
				}
				(PlaylistInfo Playlist, List<SongInfo> Songs, string StatusText) viewData = data.Value;
				(PlaylistInfo, List<SongInfo>, string) tuple = viewData;
				_currentPlaylist = tuple.Item1;
				_currentPlaylistOwnedByUser = IsPlaylistOwnedByUser(_currentPlaylist, GetCurrentUserId());
				if (HasSkeletonItems(viewData.Songs))
				{
					DisplaySongs(viewData.Songs, showPagination: false, hasNextPage: false, 1, preserveSelection, viewSource, accessibleName, skipAvailabilityCheck: true);
				}
				UpdateStatusBar(viewData.StatusText);
				EnrichCurrentPlaylistSongsAsync(viewData.Playlist, viewData.Songs, viewSource, accessibleName);
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Exception ex3 = ex2;
			if (!TryHandleOperationCancelled(ex3, "打开歌单已取消"))
			{
				Debug.WriteLine($"[MainForm] 打开歌单失败: {ex3}");
				MessageBox.Show("打开歌单失败: " + ex3.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("打开歌单失败");
			}
		}
	}

	private async Task OpenAlbum(AlbumInfo album, bool skipSave = false)
	{
		try
		{
			if (album == null || string.IsNullOrWhiteSpace(album.Id))
			{
				MessageBox.Show("无法获取专辑标识，无法加载数据。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("打开专辑失败");
				return;
			}
			if (!skipSave)
			{
				SaveNavigationState();
			}
			string albumId = album.Id;
			string viewSource = "album:" + albumId;
			string accessibleName = (string.IsNullOrWhiteSpace(album.Name) ? ("专辑 " + albumId) : album.Name);
			ViewLoadRequest request = new ViewLoadRequest(viewSource, accessibleName, "正在加载专辑: " + accessibleName + "...", !skipSave);
			ViewLoadResult<(AlbumInfo Album, List<SongInfo> Songs, string StatusText)?> loadResult = await RunViewLoadAsync(request, (CancellationToken token) => BuildAlbumSkeletonViewDataAsync(async delegate(CancellationToken ct)
			{
				AlbumInfo detail = await _apiClient.GetAlbumDetailAsync(albumId).ConfigureAwait(continueOnCapturedContext: false);
				ct.ThrowIfCancellationRequested();
				return detail ?? album;
			}, viewSource, token), "打开专辑已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				(AlbumInfo Album, List<SongInfo> Songs, string StatusText)? data = loadResult.Value;
				if (!data.HasValue)
				{
					MessageBox.Show("专辑 " + accessibleName + " 暂时不可用。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
					UpdateStatusBar("打开专辑失败");
					return;
				}
				(AlbumInfo Album, List<SongInfo> Songs, string StatusText) viewData = data.Value;
				_currentPlaylist = null;
				if (HasSkeletonItems(viewData.Songs))
				{
					DisplaySongs(viewData.Songs, showPagination: false, hasNextPage: false, 1, preserveSelection: false, viewSource, accessibleName, skipAvailabilityCheck: true);
				}
				UpdateStatusBar(viewData.StatusText);
				EnrichCurrentAlbumSongsAsync(viewData.Album, viewData.Songs, viewSource, accessibleName);
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Exception ex3 = ex2;
			if (!TryHandleOperationCancelled(ex3, "打开专辑已取消"))
			{
				Debug.WriteLine($"[MainForm] 打开专辑失败: {ex3}");
				MessageBox.Show("打开专辑失败: " + ex3.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("打开专辑失败");
			}
		}
	}

	private async Task LoadPlaylistById(string playlistId, bool skipSave = false)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(playlistId))
			{
				MessageBox.Show("缺少歌单标识", "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("加载歌单失败");
				return;
			}
			string viewSource = "playlist:" + playlistId;
			if (!skipSave)
			{
				SaveNavigationState();
			}
			ViewLoadRequest request = new ViewLoadRequest(viewSource, "歌单 " + playlistId, "正在加载歌单...", !skipSave);
			ViewLoadResult<(PlaylistInfo Playlist, List<SongInfo> Songs, string StatusText)?> loadResult = await RunViewLoadAsync(request, (CancellationToken token) => BuildPlaylistSkeletonViewDataAsync(async delegate(CancellationToken ct)
			{
				PlaylistInfo detail = await _apiClient.GetPlaylistDetailAsync(playlistId).ConfigureAwait(continueOnCapturedContext: false);
				ct.ThrowIfCancellationRequested();
				return detail;
			}, viewSource, token), "加载歌单已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				(PlaylistInfo Playlist, List<SongInfo> Songs, string StatusText)? data = loadResult.Value;
				if (!data.HasValue)
				{
					MessageBox.Show("歌单暂时无法访问。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
					UpdateStatusBar("加载歌单失败");
					return;
				}
				(PlaylistInfo Playlist, List<SongInfo> Songs, string StatusText) viewData = data.Value;
				_currentPlaylist = viewData.Playlist;
				_currentPlaylistOwnedByUser = IsPlaylistOwnedByUser(_currentPlaylist, GetCurrentUserId());
				_isHomePage = false;
				if (HasSkeletonItems(viewData.Songs))
				{
					DisplaySongs(viewData.Songs, showPagination: false, hasNextPage: false, 1, preserveSelection: false, viewSource, viewData.Playlist.Name ?? ("歌单 " + playlistId), skipAvailabilityCheck: true);
				}
				UpdateStatusBar(viewData.StatusText);
				EnrichCurrentPlaylistSongsAsync(viewData.Playlist, viewData.Songs, viewSource, viewData.Playlist.Name ?? ("歌单 " + playlistId));
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Exception ex3 = ex2;
			if (!TryHandleOperationCancelled(ex3, "加载歌单已取消"))
			{
				Debug.WriteLine($"[LoadPlaylistById] 异常: {ex3}");
				MessageBox.Show("加载歌单失败: " + ex3.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("加载歌单失败");
			}
		}
	}

	private async Task LoadAlbumById(string albumId, bool skipSave = false)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(albumId))
			{
				MessageBox.Show("缺少专辑标识", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				UpdateStatusBar("打开专辑失败");
				return;
			}
			string viewSource = "album:" + albumId;
			if (!skipSave)
			{
				SaveNavigationState();
			}
			ViewLoadRequest request = new ViewLoadRequest(viewSource, "专辑 " + albumId, "正在加载专辑...", !skipSave);
			ViewLoadResult<(AlbumInfo Album, List<SongInfo> Songs, string StatusText)?> loadResult = await RunViewLoadAsync(request, (CancellationToken token) => BuildAlbumSkeletonViewDataAsync(async delegate(CancellationToken ct)
			{
				AlbumInfo detail = await _apiClient.GetAlbumDetailAsync(albumId).ConfigureAwait(continueOnCapturedContext: false);
				ct.ThrowIfCancellationRequested();
				return detail;
			}, viewSource, token), "打开专辑已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				(AlbumInfo Album, List<SongInfo> Songs, string StatusText)? data = loadResult.Value;
				if (!data.HasValue)
				{
					MessageBox.Show("专辑暂时无法访问。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
					UpdateStatusBar("打开专辑失败");
					return;
				}
				(AlbumInfo Album, List<SongInfo> Songs, string StatusText) viewData = data.Value;
				_currentPlaylist = null;
				_isHomePage = false;
				if (HasSkeletonItems(viewData.Songs))
				{
					DisplaySongs(viewData.Songs, showPagination: false, hasNextPage: false, 1, preserveSelection: false, viewSource, viewData.Album.Name ?? ("专辑 " + albumId), skipAvailabilityCheck: true);
				}
				UpdateStatusBar(viewData.StatusText);
				EnrichCurrentAlbumSongsAsync(viewData.Album, viewData.Songs, viewSource, viewData.Album.Name ?? ("专辑 " + albumId));
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Exception ex3 = ex2;
			if (!TryHandleOperationCancelled(ex3, "打开专辑已取消"))
			{
				Debug.WriteLine($"[LoadAlbumById] 异常: {ex3}");
				MessageBox.Show("打开专辑失败: " + ex3.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("打开专辑失败");
			}
		}
	}

	private async Task LoadSearchResults(string keyword, string searchType, int page, bool skipSave = false, int pendingFocusIndex = -1)
	{
		CancellationTokenSource searchCts = null;
		try
		{
			string normalizedKeyword = keyword?.Trim();
			if (string.IsNullOrWhiteSpace(normalizedKeyword))
			{
				UpdateStatusBar("无法加载搜索结果：关键词为空");
				return;
			}
			searchCts = BeginSearchOperation();
			await ExecuteSearchAsync(normalizedKeyword, searchType, page, skipSave, showEmptyPrompt: false, searchCts.Token, pendingFocusIndex).ConfigureAwait(continueOnCapturedContext: true);
		}
		catch (OperationCanceledException)
		{
			UpdateStatusBar("搜索已取消");
		}
		catch (Exception ex2)
		{
			Exception ex3 = ex2;
			Exception ex4 = ex3;
			if (!TryHandleOperationCancelled(ex4, "搜索加载已取消"))
			{
				Debug.WriteLine($"[LoadSearchResults] 异常: {ex4}");
				MessageBox.Show("加载搜索结果失败: " + ex4.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("加载搜索结果失败");
			}
		}
		finally
		{
			if (searchCts != null)
			{
				if (_searchCts == searchCts)
				{
					_searchCts = null;
				}
				searchCts.Dispose();
			}
		}
	}


	private async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> operation, int maxAttempts = 3, int initialDelayMs = 500, string? operationName = null, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (maxAttempts <= 0)
		{
			maxAttempts = 1;
		}
		Exception lastException = null;
		checked
		{
			for (int attempt = 1; attempt <= maxAttempts; attempt++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				try
				{
					return await operation();
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (Exception ex2)
				{
					lastException = ex2;
					Debug.WriteLine(string.Format("[Retry] {0} 第 {1}/{2} 次失败: {3}", operationName ?? "操作", attempt, maxAttempts, ex2.Message));
					if (attempt >= maxAttempts)
					{
						break;
					}
					int delay = ((initialDelayMs <= 0) ? 300 : ((int)((double)initialDelayMs * Math.Pow(1.5, attempt - 1))));
					try
					{
						await Task.Delay(delay, cancellationToken);
					}
					catch (TaskCanceledException)
					{
						throw;
					}
					continue;
				}
			}
			throw lastException ?? new Exception("操作失败");
		}
	}

	private void currentPlayingMenuItem_DropDownOpening(object sender, EventArgs e)
	{
		SongInfo songInfo = _audioEngine?.CurrentSong;
		if (songInfo == null)
		{
			_isCurrentPlayingMenuActive = false;
			_currentPlayingMenuSong = null;
			currentPlayingMenuItem.Visible = false;
			return;
		}
		_isCurrentPlayingMenuActive = true;
		_currentPlayingMenuSong = songInfo;
		if (songContextMenu != null)
		{
			songContextMenu.Tag = "current_playing_context";
		}
	}

	private void songContextMenu_Opening(object sender, CancelEventArgs e)
	{
		if (songContextMenu != null && !songContextMenu.ShowCheckMargin)
		{
			songContextMenu.ShowCheckMargin = true;
		}
		EnsureSortMenuCheckMargins();
		bool flag = songContextMenu?.OwnerItem == currentPlayingMenuItem || string.Equals(songContextMenu?.Tag as string, "current_playing_context", StringComparison.Ordinal);
		UpdateDownloadMenuShortcutDisplay(!flag);
		MenuContextSnapshot menuContextSnapshot = BuildMenuContextSnapshot(flag);
		if (!menuContextSnapshot.IsValid)
		{
			viewSourceMenuItem.Visible = false;
			viewSourceMenuItem.Tag = null;
			if (songContextMenu != null)
			{
				songContextMenu.Tag = null;
			}
			if (flag)
			{
				_isCurrentPlayingMenuActive = false;
				_currentPlayingMenuSong = null;
				if (currentPlayingMenuItem != null)
				{
					currentPlayingMenuItem.Visible = false;
				}
			}
			e.Cancel = true;
			return;
		}
		if (menuContextSnapshot.IsCurrentPlayback)
		{
			string text = ResolveCurrentPlayingViewSource(menuContextSnapshot.Song);
			viewSourceMenuItem.Visible = true;
			viewSourceMenuItem.Enabled = !string.IsNullOrWhiteSpace(text);
			viewSourceMenuItem.Tag = text;
		}
		else
		{
			viewSourceMenuItem.Visible = false;
			viewSourceMenuItem.Tag = null;
		}
		_isCurrentPlayingMenuActive = menuContextSnapshot.IsCurrentPlayback;
		if (!menuContextSnapshot.IsCurrentPlayback)
		{
			if (songContextMenu != null && string.Equals(songContextMenu.Tag as string, "current_playing_context", StringComparison.Ordinal))
			{
				songContextMenu.Tag = null;
			}
		}
		else if (songContextMenu != null)
		{
			songContextMenu.Tag = "current_playing_context";
		}
		ResetSongContextMenuState();
		bool showViewSection = false;
		CommentTarget contextCommentTarget = null;
		PodcastRadioInfo contextPodcastForEpisode = null;
		PodcastEpisodeInfo effectiveEpisode = null;
		bool isPodcastEpisodeContext = false;
		ApplyViewContextFlags(menuContextSnapshot, ref showViewSection);
		if (!menuContextSnapshot.IsCurrentPlayback && menuContextSnapshot.PrimaryEntity == MenuEntityKind.Artist && menuContextSnapshot.Artist != null)
		{
			ConfigureArtistContextMenu(menuContextSnapshot.Artist);
			return;
		}
		if (!menuContextSnapshot.IsCurrentPlayback && menuContextSnapshot.PrimaryEntity == MenuEntityKind.Category)
		{
			ConfigureCategoryMenu();
			return;
		}
		switch (menuContextSnapshot.PrimaryEntity)
		{
		case MenuEntityKind.Playlist:
			ConfigurePlaylistMenu(menuContextSnapshot, menuContextSnapshot.IsLoggedIn, ref showViewSection, ref contextCommentTarget);
			break;
		case MenuEntityKind.Album:
			ConfigureAlbumMenu(menuContextSnapshot, menuContextSnapshot.IsLoggedIn, ref showViewSection, ref contextCommentTarget);
			break;
		case MenuEntityKind.Podcast:
			ConfigurePodcastMenu(menuContextSnapshot, menuContextSnapshot.IsLoggedIn, ref showViewSection);
			break;
		case MenuEntityKind.Song:
		case MenuEntityKind.PodcastEpisode:
			ConfigureSongOrEpisodeMenu(menuContextSnapshot, menuContextSnapshot.IsLoggedIn, menuContextSnapshot.IsCloudView, ref showViewSection, ref contextCommentTarget, ref contextPodcastForEpisode, ref effectiveEpisode, ref isPodcastEpisodeContext);
			break;
		default:
			if (!menuContextSnapshot.IsCurrentPlayback)
			{
				e.Cancel = true;
			}
			return;
		}
		if (contextCommentTarget != null && !isPodcastEpisodeContext)
		{
			commentMenuItem.Visible = true;
			commentMenuItem.Tag = contextCommentTarget;
			commentMenuSeparator.Visible = true;
		}
		if (!podcastSortMenuItem.Visible)
		{
			ToolStripMenuItem toolStripMenuItem = artistSongsSortMenuItem;
			if (toolStripMenuItem == null || !toolStripMenuItem.Visible)
			{
				ToolStripMenuItem toolStripMenuItem2 = artistAlbumsSortMenuItem;
				if (toolStripMenuItem2 == null || !toolStripMenuItem2.Visible)
				{
					goto IL_03a8;
				}
			}
		}
		showViewSection = true;
		goto IL_03a8;
		IL_03a8:
		toolStripSeparatorView.Visible = showViewSection;
	}

	private void songContextMenu_Closed(object sender, ToolStripDropDownClosedEventArgs e)
	{
		_isCurrentPlayingMenuActive = false;
		_currentPlayingMenuSong = null;
		if (songContextMenu != null && string.Equals(songContextMenu.Tag as string, "current_playing_context", StringComparison.Ordinal))
		{
			songContextMenu.Tag = null;
		}
		viewSourceMenuItem.Visible = false;
		viewSourceMenuItem.Tag = null;
	}

	private void commentMenuItem_Click(object sender, EventArgs e)
	{
		if (sender is ToolStripItem { Tag: CommentTarget tag })
		{
			ShowCommentsDialog(tag);
		}
	}

	private async void viewSourceMenuItem_Click(object sender, EventArgs e)
	{
		await NavigateToCurrentPlayingSourceAsync();
	}

	private void ShowCommentsDialog(CommentTarget target)
	{
		if (_apiClient == null)
		{
			return;
		}
		using CommentsDialog commentsDialog = new CommentsDialog(_apiClient, target, _accountState?.UserId, IsUserLoggedIn());
		commentsDialog.ShowDialog(this);
	}

	private async Task NavigateToCurrentPlayingSourceAsync()
	{
		SongInfo song = _audioEngine?.CurrentSong;
		if (song == null)
		{
			UpdateStatusBar("暂无正在播放的歌曲");
			return;
		}
		string viewSource = viewSourceMenuItem.Tag as string;
		if (string.IsNullOrWhiteSpace(viewSource))
		{
			viewSource = ResolveCurrentPlayingViewSource(song);
		}
		if (string.IsNullOrWhiteSpace(viewSource))
		{
			MessageBox.Show("无法确定当前歌曲的来源", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
		}
		else if (!(await NavigateByViewSourceAsync(viewSource, song)))
		{
			UpdateStatusBar("跳转到来源失败");
		}
	}

	private string? ResolveCurrentPlayingViewSource(SongInfo? song)
	{
		if (song == null)
		{
			return null;
		}
		if (!string.IsNullOrWhiteSpace(song.ViewSource))
		{
			return song.ViewSource;
		}
		PlaybackSnapshot playbackSnapshot = _playbackQueue?.CaptureSnapshot();
		if (playbackSnapshot != null && !string.IsNullOrWhiteSpace(song.Id))
		{
			if (playbackSnapshot.InjectionSources != null && playbackSnapshot.InjectionSources.TryGetValue(song.Id, out string value) && !string.IsNullOrWhiteSpace(value))
			{
				return value;
			}
			if (!string.IsNullOrWhiteSpace(playbackSnapshot.QueueSource))
			{
				return playbackSnapshot.QueueSource;
			}
		}
		if (!string.IsNullOrWhiteSpace(_currentViewSource) && IsSongInCurrentView(song))
		{
			return _currentViewSource;
		}
		return null;
	}

        private bool IsSongInCurrentView(SongInfo? song)
        {
                if (song == null || _currentSongs == null || _currentSongs.Count == 0)
                {
                        return false;
                }
		checked
		{
			if (!string.IsNullOrWhiteSpace(song.Id))
			{
				for (int i = 0; i < _currentSongs.Count; i++)
				{
					if (string.Equals(_currentSongs[i]?.Id, song.Id, StringComparison.OrdinalIgnoreCase))
					{
						return true;
					}
				}
			}
			if (song.IsCloudSong && !string.IsNullOrWhiteSpace(song.CloudSongId))
			{
				for (int j = 0; j < _currentSongs.Count; j++)
				{
					SongInfo songInfo = _currentSongs[j];
					if (songInfo != null && songInfo.IsCloudSong && !string.IsNullOrWhiteSpace(songInfo.CloudSongId) && string.Equals(songInfo.CloudSongId, song.CloudSongId, StringComparison.OrdinalIgnoreCase))
					{
						return true;
					}
				}
			}
                        return false;
                }
        }

        private void EnsurePlaybackQueueCoversCurrentView()
        {
                if (_playbackQueue == null || _currentSongs == null || _currentSongs.Count == 0)
                {
                        return;
                }

                PlaybackSnapshot snapshot = _playbackQueue.CaptureSnapshot();
                if (snapshot == null)
                {
                        return;
                }

                int queueCount = snapshot.Queue?.Count ?? 0;
                int viewCount = _currentSongs.Count;
                if (queueCount == viewCount)
                {
                        return;
                }

                TrySyncQueueWithCurrentView(snapshot, viewCount, "view-coverage");
        }

        private bool TrySyncQueueWithCurrentView(PlaybackSnapshot snapshot, int requiredCount, string reason)
        {
                if (_playbackQueue == null || snapshot == null)
                {
                        return false;
                }

                if (_currentSongs == null || _currentSongs.Count == 0)
                {
                        return false;
                }

                if (!string.Equals(snapshot.QueueSource, _currentViewSource, StringComparison.OrdinalIgnoreCase))
                {
                        return false;
                }

                if (requiredCount > 0 && _currentSongs.Count < requiredCount)
                {
                        return false;
                }

                int anchorIndex = ResolvePlaybackQueueAnchorIndex(snapshot);
                if (anchorIndex < 0 || anchorIndex >= _currentSongs.Count)
                {
                        return false;
                }

                bool synced = _playbackQueue.TrySyncQueueWithView(_currentSongs, _currentViewSource, anchorIndex);
                if (synced)
                {
                        int oldCount = snapshot.Queue?.Count ?? 0;
                        Debug.WriteLine($"[MainForm] Playback queue synced: reason={reason}, source={_currentViewSource}, old={oldCount}, new={_currentSongs.Count}, anchorIndex={anchorIndex}");
                }

                return synced;
        }

        private int ResolvePlaybackQueueAnchorIndex(PlaybackSnapshot snapshot)
        {
                if (_currentSongs == null || _currentSongs.Count == 0)
                {
                        return -1;
                }

                if (snapshot != null && snapshot.QueueIndex >= 0 && snapshot.QueueIndex < _currentSongs.Count)
                {
                        return snapshot.QueueIndex;
                }

                SongInfo currentSong = _audioEngine?.CurrentSong;
                int currentSongIndex = FindSongIndexInList(_currentSongs, currentSong);
                if (currentSongIndex >= 0 && currentSongIndex < _currentSongs.Count)
                {
                        return currentSongIndex;
                }

                int selectedIndex = GetSelectedListViewIndex();
                if (selectedIndex >= 0 && selectedIndex < _currentSongs.Count)
                {
                        return selectedIndex;
                }

                return 0;
        }

        private bool EnsureRandomQueueReadyBeforeMove(bool isNext, bool isManual)
        {
                if (_playbackQueue == null || (_audioEngine?.PlayMode ?? PlayMode.Loop) != PlayMode.Random)
                {
                        return true;
                }

                PlaybackSnapshot snapshot = _playbackQueue.CaptureSnapshot();
                int queueCount = snapshot?.Queue?.Count ?? 0;
                if (snapshot == null || queueCount <= 0)
                {
                        return true;
                }

                int expectedCount = ResolveExpectedRandomQueueCount(snapshot.QueueSource, queueCount);
                if (expectedCount <= queueCount)
                {
                        return true;
                }

                bool syncAttempted = TrySyncQueueWithCurrentView(snapshot, expectedCount, "random-preflight");
                if (syncAttempted)
                {
                        snapshot = _playbackQueue.CaptureSnapshot();
                        queueCount = snapshot?.Queue?.Count ?? 0;
                        if (queueCount >= expectedCount)
                        {
                                return true;
                        }
                }

                string directionText = isNext ? "下一首" : "上一首";
                string statusText = $"随机队列正在初始化（{queueCount}/{expectedCount}），{directionText}稍后可用";
                if (isManual)
                {
                        UpdateStatusBar(statusText);
                }

                Debug.WriteLine($"[MainForm][RandomGate] blocked move: direction={(isNext ? "next" : "previous")}, manual={isManual}, source={snapshot?.QueueSource}, queueCount={queueCount}, expectedCount={expectedCount}, currentView={_currentViewSource}, syncAttempted={syncAttempted}");
                return false;
        }

        private int ResolveExpectedRandomQueueCount(string? queueSource, int fallbackCount)
        {
                int expectedCount = Math.Max(0, fallbackCount);
                if (string.IsNullOrWhiteSpace(queueSource))
                {
                        return expectedCount;
                }

                if (TryGetCachedPlaylistTrackCount(queueSource, out int cachedCount))
                {
                        expectedCount = Math.Max(expectedCount, cachedCount);
                }

                if (string.Equals(queueSource, _currentViewSource, StringComparison.OrdinalIgnoreCase))
                {
                        if (_currentSongs != null && _currentSongs.Count > 0)
                        {
                                expectedCount = Math.Max(expectedCount, _currentSongs.Count);
                        }

                        if (queueSource.StartsWith("playlist:", StringComparison.OrdinalIgnoreCase) &&
                                _currentPlaylist != null &&
                                _currentPlaylist.TrackCount > 0)
                        {
                                expectedCount = Math.Max(expectedCount, _currentPlaylist.TrackCount);
                        }
                }

                return expectedCount;
        }

        private bool TryGetCachedPlaylistTrackCount(string queueSource, out int trackCount)
        {
                trackCount = 0;
                if (string.IsNullOrWhiteSpace(queueSource))
                {
                        return false;
                }

                if (TryGetCachedPlaylistTrackCountCore(queueSource, out trackCount))
                {
                        return true;
                }

                string normalizedQueueSource = StripOffsetSuffix(queueSource, out _);
                if (!string.Equals(normalizedQueueSource, queueSource, StringComparison.OrdinalIgnoreCase))
                {
                        return TryGetCachedPlaylistTrackCountCore(normalizedQueueSource, out trackCount);
                }

                return false;
        }

        private bool TryGetCachedPlaylistTrackCountCore(string queueSource, out int trackCount)
        {
                trackCount = 0;
                if (string.IsNullOrWhiteSpace(queueSource))
                {
                        return false;
                }

                if (_playlistTrackIdByIndexMapCache.TryGetValue(queueSource, out var reverseMap) &&
                        reverseMap != null &&
                        reverseMap.Count > 0)
                {
                        trackCount = reverseMap.Count;
                        return true;
                }

                if (_playlistTrackIndexMapCache.TryGetValue(queueSource, out var indexMap) &&
                        indexMap != null &&
                        indexMap.Count > 0)
                {
                        trackCount = indexMap.Count;
                        return true;
                }

                return false;
        }

        private void CachePlaylistTrackIndexMap(string? viewSource, Dictionary<string, int> indexMap)
        {
                if (string.IsNullOrWhiteSpace(viewSource) || indexMap == null || indexMap.Count == 0)
                {
                        return;
                }

                Dictionary<int, string> reverseMap = new Dictionary<int, string>();
                foreach (KeyValuePair<string, int> entry in indexMap)
                {
                        if (entry.Value >= 0 && !string.IsNullOrWhiteSpace(entry.Key) && !reverseMap.ContainsKey(entry.Value))
                        {
                                reverseMap[entry.Value] = entry.Key;
                        }
                }

                if (!_playlistTrackIndexMapCache.ContainsKey(viewSource))
                {
                        if (_playlistTrackIndexMapOrder.Count >= PlaylistTrackIndexMapCacheLimit)
                        {
                                string oldest = _playlistTrackIndexMapOrder.Dequeue();
                                _playlistTrackIndexMapCache.Remove(oldest);
                                _playlistTrackIdByIndexMapCache.Remove(oldest);
                        }
                        _playlistTrackIndexMapOrder.Enqueue(viewSource);
                }

                _playlistTrackIndexMapCache[viewSource] = indexMap;
                _playlistTrackIdByIndexMapCache[viewSource] = reverseMap;
        }

        private int ResolveSongIndexFromCache(string? viewSource, SongInfo? song)
        {
                if (song == null || string.IsNullOrWhiteSpace(viewSource) || _playlistTrackIndexMapCache.Count == 0)
                {
                        return -1;
                }
                if (!string.IsNullOrWhiteSpace(song.Id) && _playlistTrackIndexMapCache.TryGetValue(viewSource, out var map) && map != null && map.TryGetValue(song.Id, out var index))
                {
                        return index;
                }
                return -1;
        }

        private bool TryResolveSongIdFromCache(string? viewSource, int songIndex, out string songId)
        {
                songId = string.Empty;
                if (string.IsNullOrWhiteSpace(viewSource) || songIndex < 0)
                {
                        return false;
                }

                if (TryResolveSongIdFromCacheCore(viewSource, songIndex, out songId))
                {
                        return true;
                }

                string normalizedViewSource = StripOffsetSuffix(viewSource, out _);
                if (!string.Equals(normalizedViewSource, viewSource, StringComparison.OrdinalIgnoreCase))
                {
                        return TryResolveSongIdFromCacheCore(normalizedViewSource, songIndex, out songId);
                }

                return false;
        }

        private bool TryResolveSongIdFromCacheCore(string viewSource, int songIndex, out string songId)
        {
                songId = string.Empty;
                if (string.IsNullOrWhiteSpace(viewSource))
                {
                        return false;
                }

                if (_playlistTrackIdByIndexMapCache.TryGetValue(viewSource, out var reverseMap) &&
                        reverseMap != null &&
                        reverseMap.TryGetValue(songIndex, out songId) &&
                        !string.IsNullOrWhiteSpace(songId))
                {
                        return true;
                }

                if (_playlistTrackIndexMapCache.TryGetValue(viewSource, out var forwardMap) && forwardMap != null)
                {
                        foreach (KeyValuePair<string, int> entry in forwardMap)
                        {
                                if (entry.Value == songIndex && !string.IsNullOrWhiteSpace(entry.Key))
                                {
                                        songId = entry.Key;
                                        return true;
                                }
                        }
                }

                return false;
        }

        private int FindSongIndexInList(IReadOnlyList<SongInfo> songs, SongInfo? song)
        {
                if (songs == null || song == null)
                {
                        return -1;
                }
                if (!string.IsNullOrWhiteSpace(song.Id))
                {
                        for (int i = 0; i < songs.Count; i++)
                        {
                                if (string.Equals(songs[i]?.Id, song.Id, StringComparison.OrdinalIgnoreCase))
                                {
                                        return i;
                                }
                        }
                }
                if (song.IsCloudSong && !string.IsNullOrWhiteSpace(song.CloudSongId))
                {
                        for (int j = 0; j < songs.Count; j++)
                        {
                                SongInfo songInfo = songs[j];
                                if (songInfo != null && songInfo.IsCloudSong && !string.IsNullOrWhiteSpace(songInfo.CloudSongId) && string.Equals(songInfo.CloudSongId, song.CloudSongId, StringComparison.OrdinalIgnoreCase))
                                {
                                        return j;
                                }
                        }
                }
                return -1;
        }

        private bool IsPersonalFmViewSource(string? viewSource)
        {
                if (string.IsNullOrWhiteSpace(viewSource))
                {
                        return false;
                }
                string normalized = StripOffsetSuffix(viewSource, out _);
                return string.Equals(normalized, PersonalFmCategoryId, StringComparison.OrdinalIgnoreCase);
        }

        private bool IsPersonalFmPlaybackActive(SongInfo? currentSong = null)
        {
                PlaybackSnapshot snapshot = _playbackQueue?.CaptureSnapshot();
                if (snapshot != null && !string.IsNullOrWhiteSpace(snapshot.QueueSource))
                {
                        return IsPersonalFmViewSource(snapshot.QueueSource);
                }
                if (currentSong != null && IsPersonalFmViewSource(currentSong.ViewSource))
                {
                        return true;
                }
                SongInfo song = currentSong ?? _audioEngine?.CurrentSong;
                return song != null && IsPersonalFmViewSource(song.ViewSource);
        }

        private PlayMode ResolveEffectivePlayModeForPlayback(PlayMode configuredMode, SongInfo? currentSong = null)
        {
                if (configuredMode == PlayMode.LoopOne)
                {
                        return configuredMode;
                }
                return IsPersonalFmPlaybackActive(currentSong) ? PlayMode.Sequential : configuredMode;
        }

        private void EnsurePersonalFmQueueExpandedInBackground(SongInfo? currentSong, bool requireImmediateNext = false)
        {
                EnsurePersonalFmQueueExpandedForNextAsync(currentSong, requireImmediateNext).SafeFireAndForget("Personal FM background expand");
        }

        private async Task WaitForPersonalFmAppendCompletionAsync(CancellationToken cancellationToken)
        {
                const int maxWaitRounds = 160;
                for (int waitRound = 0; waitRound < maxWaitRounds; waitRound++)
                {
                        if (Volatile.Read(ref _personalFmAppendInFlight) == 0)
                        {
                                return;
                        }
                        await Task.Delay(25, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
                }
        }

        private static void ApplyViewSourceToSongs(IEnumerable<SongInfo>? songs, string viewSource)
        {
                if (songs == null)
                {
                        return;
                }
                string effectiveViewSource = viewSource ?? string.Empty;
                foreach (SongInfo song in songs)
                {
                        if (song != null)
                        {
                                song.ViewSource = effectiveViewSource;
                        }
                }
        }

        private static bool IsPersonalFmSongCandidate(SongInfo? song)
        {
                return song != null && (!string.IsNullOrWhiteSpace(song.Id) || !string.IsNullOrWhiteSpace(song.Name));
        }

        private static string BuildPersonalFmSongKey(SongInfo? song)
        {
                if (!IsPersonalFmSongCandidate(song))
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
                return $"meta:{name}|{artist}|{album}";
        }

        private List<SongInfo> GetPersonalFmCachedSongsSnapshot()
        {
                lock (_personalFmStateLock)
                {
                        List<SongInfo> snapshot = CloneList(_personalFmSongsCache);
                        ApplyViewSourceToSongs(snapshot, PersonalFmCategoryId);
                        return snapshot;
                }
        }

        private int ResolvePersonalFmFocusIndexForEntry(int requestedIndex, int cacheCountOverride = -1)
        {
                int cacheCount = cacheCountOverride;
                int cachedFocusIndex;
                lock (_personalFmStateLock)
                {
                        cachedFocusIndex = _personalFmLastFocusedIndex;
                        if (cacheCount < 0)
                        {
                                cacheCount = _personalFmSongsCache.Count;
                        }
                }
                if (cacheCount <= 0)
                {
                        return 0;
                }
                int targetIndex = (requestedIndex >= 0) ? requestedIndex : cachedFocusIndex;
                if (targetIndex < 0)
                {
                        targetIndex = 0;
                }
                return Math.Max(0, Math.Min(targetIndex, cacheCount - 1));
        }

        private int ResolvePersonalFmFocusIndexForViewSourceNavigation(SongInfo? currentSong)
        {
                List<SongInfo> cachedSongs;
                int cachedFocusIndex;
                lock (_personalFmStateLock)
                {
                        cachedSongs = CloneList(_personalFmSongsCache);
                        cachedFocusIndex = _personalFmLastFocusedIndex;
                }
                if (cachedSongs.Count <= 0)
                {
                        return 0;
                }
                int matchedIndex = FindSongIndexInList(cachedSongs, currentSong);
                if (matchedIndex >= 0)
                {
                        return matchedIndex;
                }
                if (cachedFocusIndex >= 0)
                {
                        return Math.Max(0, Math.Min(cachedFocusIndex, cachedSongs.Count - 1));
                }
                return 0;
        }

        private bool TryAppendPersonalFmSongToCache(SongInfo song, out int dataIndex)
        {
                dataIndex = -1;
                if (!IsPersonalFmSongCandidate(song))
                {
                        return false;
                }
                song.ViewSource = PersonalFmCategoryId;
                string key = BuildPersonalFmSongKey(song);
                lock (_personalFmStateLock)
                {
                        if (!string.IsNullOrWhiteSpace(key) && !_personalFmSongKeySet.Add(key))
                        {
                                return false;
                        }
                        _personalFmSongsCache.Add(song);
                        if (_personalFmLastFocusedIndex < 0)
                        {
                                _personalFmLastFocusedIndex = 0;
                        }
                        _personalFmLastFocusedIndex = Math.Max(0, Math.Min(_personalFmLastFocusedIndex, _personalFmSongsCache.Count - 1));
                        dataIndex = _personalFmSongsCache.Count - 1;
                }
                return true;
        }

        private void ResetPersonalFmCache(IEnumerable<SongInfo>? songs, int focusedIndex)
        {
                lock (_personalFmStateLock)
                {
                        _personalFmSongsCache.Clear();
                        _personalFmSongKeySet.Clear();
                        if (songs != null)
                        {
                                foreach (SongInfo song in songs)
                                {
                                        if (!IsPersonalFmSongCandidate(song))
                                        {
                                                continue;
                                        }
                                        song.ViewSource = PersonalFmCategoryId;
                                        string key = BuildPersonalFmSongKey(song);
                                        if (string.IsNullOrWhiteSpace(key) || _personalFmSongKeySet.Add(key))
                                        {
                                                _personalFmSongsCache.Add(song);
                                        }
                                }
                        }
                        if (_personalFmSongsCache.Count <= 0)
                        {
                                _personalFmLastFocusedIndex = -1;
                                return;
                        }
                        int targetIndex = (focusedIndex >= 0) ? focusedIndex : _personalFmLastFocusedIndex;
                        if (targetIndex < 0)
                        {
                                targetIndex = 0;
                        }
                        _personalFmLastFocusedIndex = Math.Max(0, Math.Min(targetIndex, _personalFmSongsCache.Count - 1));
                }
        }

        private void UpdatePersonalFmFocusedIndexFromSelection(int focusedDataIndex)
        {
                if (focusedDataIndex < 0)
                {
                        return;
                }
                int upperBound = _currentSongs?.Count ?? 0;
                lock (_personalFmStateLock)
                {
                        upperBound = Math.Max(upperBound, _personalFmSongsCache.Count);
                        if (upperBound <= 0)
                        {
                                return;
                        }
                        _personalFmLastFocusedIndex = Math.Max(0, Math.Min(focusedDataIndex, upperBound - 1));
                }
        }

        private void UpdatePersonalFmPlaybackFocus(SongInfo? song, int queueIndex, bool triggerBackgroundExpansion = true)
        {
                if (song == null)
                {
                        return;
                }
                if (!IsPersonalFmPlaybackActive(song))
                {
                        return;
                }
                int resolvedIndex = queueIndex;
                if (TryAppendPersonalFmSongToCache(song, out int appendedIndex))
                {
                        resolvedIndex = appendedIndex;
                }
                else if (resolvedIndex < 0)
                {
                        List<SongInfo> snapshot = GetPersonalFmCachedSongsSnapshot();
                        resolvedIndex = FindSongIndexInList(snapshot, song);
                }
                if (resolvedIndex >= 0)
                {
                        UpdatePersonalFmFocusedIndexFromSelection(resolvedIndex);
                }
                if (triggerBackgroundExpansion)
                {
                        EnsurePersonalFmQueueExpandedInBackground(song, requireImmediateNext: false);
                }
        }

        private int ResolvePersonalFmFocusedIndexFromUi(int totalCount)
        {
                if (totalCount <= 0)
                {
                        return -1;
                }
                int focusedIndex = GetSelectedListViewIndex();
                if (resultListView != null && resultListView.FocusedItem != null)
                {
                        focusedIndex = resultListView.FocusedItem.Index;
                }
                if (focusedIndex < 0)
                {
                        focusedIndex = _lastListViewFocusedIndex;
                }
                if (focusedIndex < 0)
                {
                        lock (_personalFmStateLock)
                        {
                                focusedIndex = _personalFmLastFocusedIndex;
                        }
                }
                if (focusedIndex < 0)
                {
                        focusedIndex = 0;
                }
                return Math.Max(0, Math.Min(focusedIndex, totalCount - 1));
        }

        private void CapturePersonalFmSnapshotOnViewLeave(string? nextViewSource)
        {
                if (!IsPersonalFmViewSource(_currentViewSource))
                {
                        return;
                }
                if (IsPersonalFmViewSource(nextViewSource))
                {
                        return;
                }
                if (_currentSongs == null || _currentSongs.Count <= 0)
                {
                        return;
                }
                List<SongInfo> snapshot = new List<SongInfo>();
                foreach (SongInfo song in _currentSongs)
                {
                        if (!IsPersonalFmSongCandidate(song))
                        {
                                continue;
                        }
                        song.ViewSource = PersonalFmCategoryId;
                        snapshot.Add(song);
                }
                if (snapshot.Count <= 0)
                {
                        return;
                }
                int focusedIndex = ResolvePersonalFmFocusedIndexFromUi(snapshot.Count);
                ResetPersonalFmCache(snapshot, focusedIndex);
                Debug.WriteLine($"[PersonalFM] 快照保存: songs={snapshot.Count}, focus={focusedIndex}, next={nextViewSource ?? "<null>"}");
        }

        private async Task<SongInfo?> FetchNextPersonalFmSongAsync(CancellationToken cancellationToken, bool allowDuplicateFallback = false)
        {
                SongInfo fallbackSong = null;
                const int maxUniqueAttempts = 4;
                for (int attempt = 1; attempt <= maxUniqueAttempts; attempt++)
                {
                        cancellationToken.ThrowIfCancellationRequested();
                        List<SongInfo> fetchedSongs = (await FetchWithRetryUntilCancel((CancellationToken _) => _apiClient.GetPersonalFMAsync(), $"personal_fm:next:{attempt}", cancellationToken, maxAttempts: 2).ConfigureAwait(continueOnCapturedContext: false)) ?? new List<SongInfo>();
                        cancellationToken.ThrowIfCancellationRequested();
                        if (fetchedSongs.Count <= 0)
                        {
                                continue;
                        }
                        ApplyViewSourceToSongs(fetchedSongs, PersonalFmCategoryId);
                        HashSet<string> existingKeys;
                        lock (_personalFmStateLock)
                        {
                                existingKeys = new HashSet<string>(_personalFmSongKeySet, StringComparer.OrdinalIgnoreCase);
                        }
                        foreach (SongInfo song in fetchedSongs)
                        {
                                if (!IsPersonalFmSongCandidate(song))
                                {
                                        continue;
                                }
                                if (fallbackSong == null)
                                {
                                        fallbackSong = song;
                                }
                                string key = BuildPersonalFmSongKey(song);
                                if (string.IsNullOrWhiteSpace(key) || !existingKeys.Contains(key))
                                {
                                        return song;
                                }
                        }
                        if (attempt < maxUniqueAttempts)
                        {
                                await Task.Delay(120, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
                        }
                }
                return allowDuplicateFallback ? fallbackSong : null;
        }

        private async Task EnsurePersonalFmQueueExpandedForNextAsync(SongInfo? currentSong, bool requireImmediateNext, CancellationToken cancellationToken = default(CancellationToken))
        {
                if (!IsPersonalFmPlaybackActive(currentSong))
                {
                        return;
                }
                PlayMode configuredMode = _audioEngine?.PlayMode ?? PlayMode.Loop;
                if (configuredMode == PlayMode.LoopOne)
                {
                        return;
                }
                PlaybackSnapshot snapshot = _playbackQueue?.CaptureSnapshot();
                if (snapshot == null || !IsPersonalFmViewSource(snapshot.QueueSource))
                {
                        return;
                }
                int requiredRemaining = requireImmediateNext ? 1 : 2;
                int maxRounds = requireImmediateNext ? 3 : 1;
                for (int round = 0; round < maxRounds; round++)
                {
                        cancellationToken.ThrowIfCancellationRequested();
                        snapshot = _playbackQueue?.CaptureSnapshot();
                        if (snapshot == null || !IsPersonalFmViewSource(snapshot.QueueSource))
                        {
                                return;
                        }
                        int queueCount = snapshot.Queue?.Count ?? 0;
                        if (queueCount <= 0)
                        {
                                return;
                        }
                        int queueIndex = snapshot.QueueIndex;
                        if (queueIndex < 0 && currentSong != null && snapshot.Queue != null)
                        {
                                queueIndex = FindSongIndexInList(snapshot.Queue, currentSong);
                        }
                        if (queueIndex < 0)
                        {
                                queueIndex = 0;
                        }
                        int remaining = queueCount - queueIndex - 1;
                        if (remaining >= requiredRemaining)
                        {
                                return;
                        }
                        int needAppend = requiredRemaining - remaining;
                        if (needAppend <= 0)
                        {
                                return;
                        }
                        int appended = await AppendPersonalFmSongsToCacheAndQueueAsync(needAppend, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
                        if (appended > 0)
                        {
                                continue;
                        }
                        if (!requireImmediateNext)
                        {
                                return;
                        }
                        await WaitForPersonalFmAppendCompletionAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
                }
        }

        private async Task<int> AppendPersonalFmSongsToCacheAndQueueAsync(int needAppendCount, CancellationToken cancellationToken = default(CancellationToken))
        {
                if (needAppendCount <= 0)
                {
                        return 0;
                }
                if (Interlocked.CompareExchange(ref _personalFmAppendInFlight, 1, 0) != 0)
                {
                        return 0;
                }
                List<(SongInfo Song, int DataIndex)> addedSongs = new List<(SongInfo, int)>();
                try
                {
                        int attempts = 0;
                        int maxAttempts = Math.Max(6, needAppendCount * 4);
                        while (addedSongs.Count < needAppendCount && attempts < maxAttempts)
                        {
                                attempts++;
                                cancellationToken.ThrowIfCancellationRequested();
                                SongInfo nextSong = await FetchNextPersonalFmSongAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
                                if (nextSong == null)
                                {
                                        continue;
                                }
                                if (TryAppendPersonalFmSongToCache(nextSong, out int dataIndex))
                                {
                                        addedSongs.Add((nextSong, dataIndex));
                                }
                        }
                        if (addedSongs.Count <= 0)
                        {
                                return 0;
                        }
                        addedSongs.Sort((left, right) => left.DataIndex.CompareTo(right.DataIndex));
                        List<SongInfo> queueAppendSongs = new List<SongInfo>(addedSongs.Count);
                        foreach ((SongInfo Song, int DataIndex) in addedSongs)
                        {
                                queueAppendSongs.Add(Song);
                        }
                        _playbackQueue?.TryAppendQueueSongs(queueAppendSongs, PersonalFmCategoryId);
                        await ExecuteOnUiThreadAsync(delegate
                        {
                                if (!IsPersonalFmViewSource(_currentViewSource) || resultListView == null || _currentSongs == null)
                                {
                                        return;
                                }
                                foreach ((SongInfo song, int dataIndex) in addedSongs)
                                {
                                        if (dataIndex < 0)
                                        {
                                                continue;
                                        }
                                        song.ViewSource = PersonalFmCategoryId;
                                        if (dataIndex > _currentSongs.Count)
                                        {
                                                continue;
                                        }
                                        if (dataIndex == _currentSongs.Count)
                                        {
                                                _currentSongs.Add(song);
                                        }
                                        else if (_currentSongs[dataIndex] == null)
                                        {
                                                _currentSongs[dataIndex] = song;
                                        }
                                        if (dataIndex >= resultListView.Items.Count)
                                        {
                                                AppendPersonalFmListViewItem(song, dataIndex);
                                        }
                                }
                                UpdateStatusBar($"私人 FM，已扩展到 {_currentSongs.Count} 首");
                        }).ConfigureAwait(continueOnCapturedContext: false);
                        return addedSongs.Count;
                }
                catch (OperationCanceledException)
                {
                        return 0;
                }
                catch (Exception ex)
                {
                        Debug.WriteLine("[PersonalFM] 缓存扩展失败: " + ex);
                        return 0;
                }
                finally
                {
                        Interlocked.Exchange(ref _personalFmAppendInFlight, 0);
                }
        }

        private async Task AppendPersonalFmSongIfNeededAsync()
        {
                CancellationToken viewToken = GetCurrentViewContentToken();
                if (viewToken == default(CancellationToken))
                {
                        viewToken = CancellationToken.None;
                }
                await AppendPersonalFmSongsToCacheAndQueueAsync(1, viewToken).ConfigureAwait(continueOnCapturedContext: false);
        }

        private void AppendPersonalFmListViewItem(SongInfo song, int dataIndex)
        {
                if (resultListView == null || song == null || dataIndex < 0)
                {
                        return;
                }
                MarkListViewLayoutDataChanged();
                int displayIndex = checked(Math.Max(1, _currentSequenceStartIndex) + dataIndex);
                resultListView.BeginUpdate();
                try
                {
                        ListViewItem listViewItem = new ListViewItem(new string[6]);
                        FillListViewItemFromSongInfo(listViewItem, song, displayIndex);
                        listViewItem.Tag = dataIndex;
                        resultListView.Items.Add(listViewItem);
                }
                finally
                {
                        EndListViewUpdateAndRefreshAccessibility();
                }
        }

        private void HandlePersonalFmSelectionChanged(int selectedListViewIndex)
        {
                if (!IsPersonalFmViewSource(_currentViewSource) || selectedListViewIndex < 0 || resultListView == null)
                {
                        return;
                }
                int focusedDataIndex = selectedListViewIndex;
                if (selectedListViewIndex < resultListView.Items.Count && resultListView.Items[selectedListViewIndex]?.Tag is int tagIndex && tagIndex >= 0)
                {
                        focusedDataIndex = tagIndex;
                }
                UpdatePersonalFmFocusedIndexFromSelection(focusedDataIndex);
                if (_currentSongs == null || _currentSongs.Count <= 0)
                {
                        return;
                }
                int lastDataIndex = _currentSongs.Count - 1;
                if (focusedDataIndex != lastDataIndex)
                {
                        return;
                }
                _ = AppendPersonalFmSongIfNeededAsync();
        }

        private static bool IsSameViewSourceForFocus(string? first, string? second)
        {
                if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
                {
                        return false;
                }

                if (string.Equals(first, second, StringComparison.OrdinalIgnoreCase))
                {
                        return true;
                }

                string normalizedFirst = StripOffsetSuffix(first, out _);
                string normalizedSecond = StripOffsetSuffix(second, out _);
                return string.Equals(normalizedFirst, normalizedSecond, StringComparison.OrdinalIgnoreCase);
        }

        private static string? ResolveSongFocusKey(SongInfo? song)
        {
                if (song == null)
                {
                        return null;
                }

                if (!string.IsNullOrWhiteSpace(song.Id))
                {
                        return song.Id;
                }

                if (song.IsCloudSong && !string.IsNullOrWhiteSpace(song.CloudSongId))
                {
                        return song.CloudSongId;
                }

                return null;
        }

        private bool IsFocusFollowPlaybackEnabled()
        {
                return _focusFollowPlayback;
        }

        private void ClearPlaybackFollowPendingState()
        {
                _pendingSongFocusId = null;
                _pendingSongFocusViewSource = null;
                _pendingSongFocusSatisfied = false;
                _pendingSongFocusSatisfiedViewSource = null;
                _deferredPlaybackFocusViewSource = null;
                if (_pendingListFocusFromPlayback)
                {
                        _pendingListFocusViewSource = null;
                        _pendingListFocusIndex = -1;
                        _pendingListFocusFromPlayback = false;
                }
        }

        private bool CanApplyPendingSongFocusForView(string? viewSource)
        {
                if (!IsFocusFollowPlaybackEnabled())
                {
                        return false;
                }

                if (string.IsNullOrWhiteSpace(_pendingSongFocusId) || string.IsNullOrWhiteSpace(_pendingSongFocusViewSource))
                {
                        return false;
                }

                return IsSameViewSourceForFocus(_pendingSongFocusViewSource, viewSource ?? _currentViewSource);
        }

        private void RememberPlaybackFocusForSource(string? sourceView, SongInfo? song, int listIndexHint = -1)
        {
                if (!IsFocusFollowPlaybackEnabled())
                {
                        return;
                }

                if (string.IsNullOrWhiteSpace(sourceView) || song == null)
                {
                        return;
                }

                string? focusKey = ResolveSongFocusKey(song);
                if (!string.IsNullOrWhiteSpace(focusKey))
                {
                        _pendingSongFocusId = focusKey;
                        _pendingSongFocusViewSource = sourceView;
                        _pendingSongFocusSatisfied = false;
                        _pendingSongFocusSatisfiedViewSource = null;
                }

                if (listIndexHint >= 0)
                {
                        _deferredPlaybackFocusViewSource = null;
                        RequestListFocus(sourceView, listIndexHint, fromPlayback: true);
                }
        }

        private void CapturePlaybackFocusBeforeViewChanged(string? previousViewSource, string? nextViewSource)
        {
                if (!IsFocusFollowPlaybackEnabled())
                {
                        return;
                }

                if (string.IsNullOrWhiteSpace(previousViewSource) || string.IsNullOrWhiteSpace(nextViewSource) || IsSameViewSourceForFocus(previousViewSource, nextViewSource))
                {
                        return;
                }

                SongInfo currentSong = _audioEngine?.CurrentSong;
                if (currentSong == null)
                {
                        return;
                }

                string? playbackSource = ResolveCurrentPlayingViewSource(currentSong);
                if (!IsSameViewSourceForFocus(previousViewSource, playbackSource))
                {
                        return;
                }

                int indexHint = -1;
                PlaybackSnapshot snapshot = _playbackQueue?.CaptureSnapshot();
                if (snapshot != null && snapshot.QueueIndex >= 0 && IsSameViewSourceForFocus(snapshot.QueueSource, playbackSource))
                {
                        indexHint = snapshot.QueueIndex;
                }

                RememberPlaybackFocusForSource(playbackSource, currentSong, indexHint);
                Debug.WriteLine($"[MainForm] Playback focus cached before view change: source={playbackSource}, index={indexHint}, song={ResolveSongFocusKey(currentSong) ?? "null"}");
        }

        private void RequestSongFocus(string? viewSource, SongInfo? song)
        {
                if (!IsFocusFollowPlaybackEnabled())
                {
                        return;
                }

                if (song == null || string.IsNullOrWhiteSpace(viewSource))
                {
                        return;
                }

                _pendingSongFocusSatisfied = false;
                _pendingSongFocusSatisfiedViewSource = null;

                string? focusKey = ResolveSongFocusKey(song);
                if (!string.IsNullOrWhiteSpace(focusKey))
                {
                        _pendingSongFocusId = focusKey;
                        _pendingSongFocusViewSource = viewSource;
                }

			if (song.IsCloudSong && !string.IsNullOrEmpty(song.CloudSongId) && string.Equals(viewSource, "user_cloud", StringComparison.OrdinalIgnoreCase))
			{
				_pendingCloudFocusId = song.CloudSongId;
				_lastSelectedCloudSongId = song.CloudSongId;
			}
	}

	private async Task<bool> JumpToCloudSongAsync(SongInfo song, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (_apiClient == null || song == null || string.IsNullOrEmpty(song.CloudSongId))
		{
			return false;
		}
		int page = 1;
		int offset = 0;
		checked
		{
			do
			{
				cancellationToken.ThrowIfCancellationRequested();
				CloudSongPageResult pageResult = await _apiClient.GetCloudSongsAsync(50, offset, cancellationToken);
				if (pageResult?.Songs != null)
				{
					int idx = pageResult.Songs.FindIndex((SongInfo s) => s != null && s.IsCloudSong && string.Equals(s.CloudSongId, song.CloudSongId, StringComparison.OrdinalIgnoreCase));
					if (idx >= 0)
					{
						_cloudPage = page;
						_pendingCloudFocusId = song.CloudSongId;
						_lastSelectedCloudSongId = song.CloudSongId;
						return true;
					}
				}
				if (pageResult == null || !pageResult.HasMore)
				{
					break;
				}
				page++;
				offset += 50;
			}
			while (page <= 200);
			return false;
		}
	}

	private async Task<bool> NavigateByViewSourceAsync(string viewSource, SongInfo currentSong)
	{
		if (string.IsNullOrWhiteSpace(viewSource))
		{
			return false;
		}
                try
                {
                        SaveNavigationState();
			string normalizedViewSource = StripOffsetSuffix(viewSource, out _);
                bool isPersonalFmSource = string.Equals(normalizedViewSource, PersonalFmCategoryId, StringComparison.OrdinalIgnoreCase);
                int personalFmFocusIndex = -1;
                RequestSongFocus(viewSource, currentSong);
                if (isPersonalFmSource)
                {
                        personalFmFocusIndex = ResolvePersonalFmFocusIndexForViewSourceNavigation(currentSong);
                        _deferredPlaybackFocusViewSource = null;
                        RequestListFocus(PersonalFmCategoryId, personalFmFocusIndex, fromPlayback: true);
                }
                PlaybackSnapshot playbackSnapshot = _playbackQueue?.CaptureSnapshot();
                if (!isPersonalFmSource && playbackSnapshot != null && playbackSnapshot.QueueIndex >= 0 && !string.IsNullOrWhiteSpace(playbackSnapshot.QueueSource) && string.Equals(playbackSnapshot.QueueSource, viewSource, StringComparison.OrdinalIgnoreCase))
                {
                        int pendingIndex = playbackSnapshot.QueueIndex;
                        ConfigModel configModel = _config ?? EnsureConfigInitialized();
                        bool resolved = false;
                        if (configModel != null && configModel.LastPlayingSourceIndex >= 0 && !string.IsNullOrWhiteSpace(configModel.LastPlayingSource) && !string.IsNullOrWhiteSpace(configModel.LastPlayingSongId) && currentSong != null && string.Equals(configModel.LastPlayingSource, viewSource, StringComparison.OrdinalIgnoreCase) && string.Equals(configModel.LastPlayingSongId, currentSong.Id, StringComparison.OrdinalIgnoreCase))
                        {
                                pendingIndex = configModel.LastPlayingSourceIndex;
                                resolved = true;
                        }
                        if (!resolved && currentSong != null)
                        {
                                int cachedIndex = ResolveSongIndexFromCache(viewSource, currentSong);
                                if (cachedIndex >= 0)
                                {
                                        pendingIndex = cachedIndex;
                                        resolved = true;
                                }
                        }
                        if (resolved)
                        {
                                _deferredPlaybackFocusViewSource = null;
                                RequestListFocus(viewSource, pendingIndex, fromPlayback: true);
                        }
                        else if (viewSource.StartsWith("playlist:", StringComparison.OrdinalIgnoreCase) && currentSong != null && !string.IsNullOrWhiteSpace(currentSong.Id))
                        {
                                _deferredPlaybackFocusViewSource = viewSource;
                                RequestListFocus(null, -1, fromPlayback: true);
                        }
                        else
                        {
                                _deferredPlaybackFocusViewSource = null;
                                RequestListFocus(viewSource, pendingIndex, fromPlayback: true);
                        }
                }
			if (string.Equals(viewSource, "homepage", StringComparison.OrdinalIgnoreCase))
			{
				await LoadHomePageAsync(skipSave: true);
			}
			else if (viewSource.StartsWith("playlist:", StringComparison.OrdinalIgnoreCase))
			{
				await LoadPlaylistById(viewSource.Substring("playlist:".Length), skipSave: true);
			}
			else if (viewSource.StartsWith("album:", StringComparison.OrdinalIgnoreCase))
			{
				await LoadAlbumById(viewSource.Substring("album:".Length), skipSave: true);
			}
			else if (viewSource.StartsWith("listen-match:", StringComparison.OrdinalIgnoreCase) || string.Equals(viewSource, "listen-match", StringComparison.OrdinalIgnoreCase))
			{
				string normalized = NormalizeListenMatchViewSource(viewSource);
				await LoadListenMatchAsync(normalized, skipSave: true);
			}
			else if (viewSource.StartsWith("artist_entries:", StringComparison.OrdinalIgnoreCase))
			{
				long artistId = ParseArtistIdFromViewSource(viewSource, "artist_entries:");
				if (artistId > 0)
				{
					await OpenArtistAsync(new ArtistInfo
					{
						Id = artistId,
						Name = (currentSong.Artist ?? string.Empty)
					}, skipSave: true);
				}
				else
				{
					await LoadArtistCategoryTypesAsync(skipSave: true);
				}
			}
			else if (viewSource.StartsWith("artist_songs_top:", StringComparison.OrdinalIgnoreCase) || viewSource.StartsWith("artist_top:", StringComparison.OrdinalIgnoreCase))
			{
				long artistId2 = ParseArtistIdFromViewSource(viewSource, viewSource.StartsWith("artist_top:", StringComparison.OrdinalIgnoreCase) ? "artist_top:" : "artist_songs_top:");
				if (artistId2 > 0)
				{
					await LoadArtistTopSongsAsync(artistId2, skipSave: true);
				}
			}
			else if (viewSource.StartsWith("artist_songs:", StringComparison.OrdinalIgnoreCase))
			{
				ParseArtistListViewSource(viewSource, out var artistId3, out var offset, out var order);
				await LoadArtistSongsAsync(artistId3, offset, skipSave: true, ResolveArtistSongsOrder(order));
			}
			else if (viewSource.StartsWith("artist_albums:", StringComparison.OrdinalIgnoreCase))
			{
				ParseArtistListViewSource(viewSource, out var artistId4, out var offset2, out var order2, "latest");
				await LoadArtistAlbumsAsync(artistId4, offset2, skipSave: true, ResolveArtistAlbumSort(order2));
			}
			else if (string.Equals(viewSource, "artist_favorites", StringComparison.OrdinalIgnoreCase))
			{
				await LoadArtistFavoritesAsync(skipSave: true);
			}
			else if (string.Equals(viewSource, "artist_category_types", StringComparison.OrdinalIgnoreCase))
			{
				await LoadArtistCategoryTypesAsync(skipSave: true);
			}
			else if (viewSource.StartsWith("artist_category_type:", StringComparison.OrdinalIgnoreCase))
			{
				if (int.TryParse(viewSource.Substring("artist_category_type:".Length), out var artistType))
				{
					await LoadArtistCategoryAreasAsync(artistType, skipSave: true);
				}
			}
			else if (viewSource.StartsWith("artist_category_list:", StringComparison.OrdinalIgnoreCase))
			{
				string[] parts = viewSource.Split(':');
				int typeVal;
				int artistType2 = ((parts.Length > 1 && int.TryParse(parts[1], out typeVal)) ? typeVal : (-1));
				int areaVal;
				int artistArea = ((parts.Length > 2 && int.TryParse(parts[2], out areaVal)) ? areaVal : (-1));
				int offset3 = 0;
				string offsetToken = parts.FirstOrDefault((string p) => p.StartsWith("offset", StringComparison.OrdinalIgnoreCase));
				if (!string.IsNullOrWhiteSpace(offsetToken))
				{
					int.TryParse(offsetToken.Substring("offset".Length), out offset3);
				}
				await LoadArtistsByCategoryAsync(artistType2, artistArea, offset3, skipSave: true);
			}
			else if (viewSource.StartsWith("podcast:", StringComparison.OrdinalIgnoreCase))
			{
				ParsePodcastViewSource(viewSource, out var radioId, out var offset4, out var ascending);
				if (radioId > 0)
				{
					await LoadPodcastEpisodesAsync(radioId, offset4, skipSave: true, null, ascending);
				}
			}
			else if (viewSource.StartsWith("search:", StringComparison.OrdinalIgnoreCase))
			{
				ParseSearchViewSource(viewSource, out var searchType, out var keyword, out var page);
				if (string.IsNullOrWhiteSpace(keyword))
				{
					keyword = _lastKeyword;
				}
				int targetPage = ((page > 0) ? page : _currentPage);
				await LoadSearchResults(keyword, searchType, (targetPage <= 0) ? 1 : targetPage, skipSave: true);
			}
			else if (string.Equals(normalizedViewSource, PersonalFmCategoryId, StringComparison.OrdinalIgnoreCase))
			{
				await LoadPersonalFm((personalFmFocusIndex >= 0) ? personalFmFocusIndex : ResolvePersonalFmFocusIndexForViewSourceNavigation(currentSong));
			}
			else if (string.Equals(normalizedViewSource, "recent_play", StringComparison.OrdinalIgnoreCase) || string.Equals(normalizedViewSource, "recent_playlists", StringComparison.OrdinalIgnoreCase) || string.Equals(normalizedViewSource, "recent_albums", StringComparison.OrdinalIgnoreCase) || string.Equals(normalizedViewSource, "recent_listened", StringComparison.OrdinalIgnoreCase) || string.Equals(normalizedViewSource, "recent_podcasts", StringComparison.OrdinalIgnoreCase) || string.Equals(normalizedViewSource, "daily_recommend", StringComparison.OrdinalIgnoreCase) || string.Equals(normalizedViewSource, "personalized", StringComparison.OrdinalIgnoreCase) || string.Equals(normalizedViewSource, "toplist", StringComparison.OrdinalIgnoreCase) || string.Equals(normalizedViewSource, "daily_recommend_songs", StringComparison.OrdinalIgnoreCase) || string.Equals(normalizedViewSource, "daily_recommend_playlists", StringComparison.OrdinalIgnoreCase) || string.Equals(normalizedViewSource, "personalized_playlists", StringComparison.OrdinalIgnoreCase) || string.Equals(normalizedViewSource, "personalized_newsongs", StringComparison.OrdinalIgnoreCase) || string.Equals(normalizedViewSource, PersonalFmCategoryId, StringComparison.OrdinalIgnoreCase) || string.Equals(normalizedViewSource, "highquality_playlists", StringComparison.OrdinalIgnoreCase) || normalizedViewSource.StartsWith("playlist_cat_", StringComparison.OrdinalIgnoreCase) || string.Equals(normalizedViewSource, "playlist_category", StringComparison.OrdinalIgnoreCase) || string.Equals(normalizedViewSource, "new_songs", StringComparison.OrdinalIgnoreCase) || string.Equals(normalizedViewSource, "new_songs_all", StringComparison.OrdinalIgnoreCase) || string.Equals(normalizedViewSource, "new_songs_chinese", StringComparison.OrdinalIgnoreCase) || string.Equals(normalizedViewSource, "new_songs_western", StringComparison.OrdinalIgnoreCase) || string.Equals(normalizedViewSource, "new_songs_japan", StringComparison.OrdinalIgnoreCase) || string.Equals(normalizedViewSource, "new_songs_korea", StringComparison.OrdinalIgnoreCase) || string.Equals(normalizedViewSource, "new_albums", StringComparison.OrdinalIgnoreCase) || string.Equals(normalizedViewSource, "user_liked_songs", StringComparison.OrdinalIgnoreCase) || string.Equals(normalizedViewSource, "user_playlists", StringComparison.OrdinalIgnoreCase) || string.Equals(normalizedViewSource, "user_albums", StringComparison.OrdinalIgnoreCase) || string.Equals(normalizedViewSource, "user_podcasts", StringComparison.OrdinalIgnoreCase) || string.Equals(normalizedViewSource, "user_cloud", StringComparison.OrdinalIgnoreCase) || string.Equals(normalizedViewSource, "artist_favorites", StringComparison.OrdinalIgnoreCase) || string.Equals(normalizedViewSource, "artist_categories", StringComparison.OrdinalIgnoreCase) || normalizedViewSource.StartsWith("artist_top_", StringComparison.OrdinalIgnoreCase) || normalizedViewSource.StartsWith("artist_songs_", StringComparison.OrdinalIgnoreCase) || normalizedViewSource.StartsWith("artist_albums_", StringComparison.OrdinalIgnoreCase) || normalizedViewSource.StartsWith("artist_type_", StringComparison.OrdinalIgnoreCase) || normalizedViewSource.StartsWith("artist_area_", StringComparison.OrdinalIgnoreCase))
			{
				if (!string.Equals(viewSource, "user_cloud", StringComparison.OrdinalIgnoreCase))
				{
					await LoadCategoryContent(viewSource, skipSave: true);
				}
				else
				{
					await LoadCloudSongsAsync(skipSave: true, await JumpToCloudSongAsync(currentSong));
				}
			}
			else if (viewSource.StartsWith("url:song:", StringComparison.OrdinalIgnoreCase))
			{
				await LoadSongFromUrlAsync(viewSource.Substring("url:song:".Length), skipSave: true);
			}
			else
			{
				if (!viewSource.StartsWith("url:mixed:", StringComparison.OrdinalIgnoreCase))
				{
					UpdateStatusBar("未找到匹配的来源页面");
					return false;
				}
				await RestoreMixedUrlStateAsync(viewSource.Substring("url:mixed:".Length));
			}
			FocusSongInCurrentView(currentSong);
			return true;
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"[ViewSource] 跳转失败: {ex}");
			MessageBox.Show("无法跳转到来源: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			return false;
		}
	}

	private void FocusSongInCurrentView(SongInfo song)
	{
		if ((_pendingSongFocusSatisfied && !string.IsNullOrWhiteSpace(_currentViewSource) && IsSameViewSourceForFocus(_pendingSongFocusSatisfiedViewSource, _currentViewSource)) || song == null || resultListView == null || _currentSongs == null || _currentSongs.Count == 0)
		{
			return;
		}
		int num = _currentSongs.FindIndex((SongInfo s) => s != null && string.Equals(s.Id, song.Id, StringComparison.OrdinalIgnoreCase));
		if (num < 0 && song.IsCloudSong && !string.IsNullOrEmpty(song.CloudSongId))
		{
			num = _currentSongs.FindIndex((SongInfo s) => s != null && s.IsCloudSong && !string.IsNullOrEmpty(s.CloudSongId) && string.Equals(s.CloudSongId, song.CloudSongId, StringComparison.OrdinalIgnoreCase));
		}
		if (num >= 0 && num < resultListView.Items.Count)
		{
			resultListView.BeginUpdate();
			try
			{
				ClearListViewSelection();
				ListViewItem listViewItem = resultListView.Items[num];
				listViewItem.Selected = true;
				listViewItem.Focused = true;
				listViewItem.EnsureVisible();
				_lastListViewFocusedIndex = num;
			}
			finally
			{
				EndListViewUpdateAndRefreshAccessibility();
			}
			if (resultListView.CanFocus && !IsListAutoFocusSuppressed)
			{
				resultListView.Focus();
			}
		}
	}


	private async Task<(long ArtistId, string ArtistName)> ResolvePrimaryArtistAsync(SongInfo song)
	{
		if (song == null)
		{
			return (ArtistId: 0L, ArtistName: string.Empty);
		}
		if (song.ArtistIds != null && song.ArtistIds.Count > 0)
		{
			string item = ((song.ArtistNames != null && song.ArtistNames.Count > 0) ? song.ArtistNames[0] : song.Artist) ?? string.Empty;
			return (ArtistId: song.ArtistIds[0], ArtistName: item);
		}
		if (string.IsNullOrWhiteSpace(song.Id))
		{
			return (ArtistId: 0L, ArtistName: string.Empty);
		}
		SongInfo detail = (await _apiClient.GetSongDetailAsync(new string[1] { song.Id }))?.FirstOrDefault();
		if (detail != null)
		{
			song.ArtistIds = new List<long>(detail.ArtistIds ?? new List<long>());
			song.ArtistNames = new List<string>(detail.ArtistNames ?? new List<string>());
			if (string.IsNullOrWhiteSpace(song.Artist) && song.ArtistNames.Count > 0)
			{
				song.Artist = string.Join("/", song.ArtistNames);
			}
			if (string.IsNullOrWhiteSpace(song.Album))
			{
				song.Album = detail.Album;
			}
			if (string.IsNullOrWhiteSpace(song.AlbumId))
			{
				song.AlbumId = detail.AlbumId;
			}
			if (!string.IsNullOrWhiteSpace(detail.PicUrl))
			{
				song.PicUrl = detail.PicUrl;
			}
			if (detail.ArtistIds != null && detail.ArtistIds.Count > 0)
			{
				string item = ((detail.ArtistNames != null && detail.ArtistNames.Count > 0) ? detail.ArtistNames[0] : detail.Artist) ?? string.Empty;
				return (ArtistId: detail.ArtistIds[0], ArtistName: item);
			}
		}
		return (ArtistId: 0L, ArtistName: string.Empty);
	}

	private static bool HasValidAlbumId(string? albumId)
	{
		if (string.IsNullOrWhiteSpace(albumId))
		{
			return false;
		}
		return long.TryParse(albumId, out var result) && result > 0;
	}

	private static bool IsCloudSongContext(SongInfo? song, bool isCloudView = false)
	{
		return song != null && (song.IsCloudSong || !string.IsNullOrWhiteSpace(song.CloudSongId) || string.Equals(song.ViewSource, "user_cloud", StringComparison.OrdinalIgnoreCase) || isCloudView);
	}

	private static AlbumInfo? TryCreateAlbumInfoFromSong(SongInfo? song)
	{
		if (song == null || !HasValidAlbumId(song.AlbumId))
		{
			return null;
		}
		if (IsCloudSongContext(song) && string.IsNullOrWhiteSpace(song.Album))
		{
			return null;
		}
		return new AlbumInfo
		{
			Id = song.AlbumId,
			Name = (string.IsNullOrWhiteSpace(song.Album) ? "\u4E13\u8F91" : song.Album),
			Artist = song.Artist ?? string.Empty,
			PicUrl = song.PicUrl ?? string.Empty
		};
	}

	private static ArtistInfo? TryCreatePrimaryArtistInfoFromSong(SongInfo? song)
	{
		if (song == null)
		{
			return null;
		}
		long num = 0L;
		string text = string.Empty;
		if (song.ArtistIds != null && song.ArtistIds.Count > 0)
		{
			num = song.ArtistIds[0];
			text = ((song.ArtistNames != null && song.ArtistNames.Count > 0) ? song.ArtistNames[0] : song.Artist) ?? string.Empty;
		}
		else if (song.PrimaryArtistId > 0)
		{
			num = song.PrimaryArtistId;
			text = song.PrimaryArtistName ?? string.Empty;
		}
		if (num <= 0)
		{
			return null;
		}
		if (string.IsNullOrWhiteSpace(text))
		{
			text = (string.IsNullOrWhiteSpace(song.Artist) ? ("\u6B4C\u624B " + num) : song.Artist);
		}
		return new ArtistInfo
		{
			Id = num,
			Name = text
		};
	}

	private async Task<string?> ResolveSongAlbumIdAsync(SongInfo song)
	{
		if (song == null)
		{
			return null;
		}
		if (HasValidAlbumId(song.AlbumId))
		{
			return song.AlbumId;
		}
		string text = ResolveSongIdForLibraryState(song);
		if (string.IsNullOrWhiteSpace(text))
		{
			return null;
		}
		SongInfo detail = (await _apiClient.GetSongDetailAsync(new string[1] { text }))?.FirstOrDefault();
		if (detail != null)
		{
			song.AlbumId = detail.AlbumId;
			if (string.IsNullOrWhiteSpace(song.Album))
			{
				song.Album = detail.Album;
			}
			if (detail.ArtistIds != null && detail.ArtistIds.Count > 0 && (song.ArtistIds == null || song.ArtistIds.Count == 0))
			{
				song.ArtistIds = new List<long>(detail.ArtistIds);
			}
			if (detail.ArtistNames != null && detail.ArtistNames.Count > 0 && (song.ArtistNames == null || song.ArtistNames.Count == 0))
			{
				song.ArtistNames = new List<string>(detail.ArtistNames);
				song.Artist = string.Join("/", song.ArtistNames);
			}
			if (!string.IsNullOrWhiteSpace(detail.PicUrl))
			{
				song.PicUrl = detail.PicUrl;
			}
		}
		return HasValidAlbumId(song.AlbumId) ? song.AlbumId : null;
	}

	private async Task<ArtistInfo?> ResolveSongArtistForSubscriptionAsync(SongInfo? song, bool isCloudSongContext)
	{
		ArtistInfo artistInfo = TryCreatePrimaryArtistInfoFromSong(song);
		if (artistInfo != null)
		{
			return artistInfo;
		}
		if (song == null || isCloudSongContext)
		{
			return null;
		}
		var (artistId, artistName) = await ResolvePrimaryArtistAsync(song);
		if (artistId <= 0)
		{
			return null;
		}
		return new ArtistInfo
		{
			Id = artistId,
			Name = (string.IsNullOrWhiteSpace(artistName) ? ("\u6B4C\u624B " + artistId) : artistName)
		};
	}

	private async Task<bool> EnsureSongAvailabilityAsync(SongInfo song)
	{
		if (song == null || string.IsNullOrWhiteSpace(song.Id))
		{
			return false;
		}
		if (song.IsAvailable == true)
		{
			return true;
		}
		QualityLevel quality = GetCurrentQuality();
		bool available = false;
		if ((await _apiClient.BatchCheckSongsAvailabilityAsync(new string[1] { song.Id }, quality))?.TryGetValue(song.Id, out available) ?? false)
		{
			if (available)
			{
				song.IsAvailable = true;
				return true;
			}
			using CancellationTokenSource unblockCts = new CancellationTokenSource(TimeSpan.FromSeconds(12.0));
			if (await TryRecoverSongAvailabilityByUnblockAsync(song, quality, unblockCts.Token))
			{
				return true;
			}
			song.IsAvailable = false;
			return false;
		}
		return false;
	}

	private async Task<bool> TryRecoverSongAvailabilityByUnblockAsync(SongInfo song, QualityLevel quality, CancellationToken cancellationToken)
	{
		if (song == null || string.IsNullOrWhiteSpace(song.Id))
		{
			return false;
		}
		try
		{
			if (await TryApplyUnblockAsync(song, GetQualityLevelString(quality), cancellationToken).ConfigureAwait(continueOnCapturedContext: false))
			{
				Debug.WriteLine("[Unblock] Availability recovered: " + song.Name + " (" + song.Id + ")");
				return true;
			}
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			Debug.WriteLine("[Unblock] Availability recover check failed: " + ex.Message);
		}
		return false;
	}

	private async Task<Dictionary<string, bool>> FetchSongsAvailabilityAsync(IEnumerable<SongInfo> songs)
	{
		string[] idList = (from s in songs
			where s != null && !string.IsNullOrWhiteSpace(s.Id)
			select s.Id).Distinct<string>(StringComparer.Ordinal).ToArray();
		if (idList.Length == 0)
		{
			return new Dictionary<string, bool>(StringComparer.Ordinal);
		}
		QualityLevel quality = GetCurrentQuality();
		Dictionary<string, bool> availability = await _apiClient.BatchCheckSongsAvailabilityAsync(idList, quality) ?? new Dictionary<string, bool>(StringComparer.Ordinal);
		List<SongInfo> unblockCandidates = new List<SongInfo>();
		foreach (SongInfo song in songs)
		{
			if (song != null && !string.IsNullOrWhiteSpace(song.Id) && availability.TryGetValue(song.Id, out var available))
			{
				if (available)
				{
					song.IsAvailable = true;
				}
				else
				{
					song.IsAvailable = null;
					unblockCandidates.Add(song);
				}
			}
		}
		foreach (SongInfo candidate in unblockCandidates)
		{
			using CancellationTokenSource unblockCts = new CancellationTokenSource(TimeSpan.FromSeconds(12.0));
			if (await TryRecoverSongAvailabilityByUnblockAsync(candidate, quality, unblockCts.Token))
			{
				availability[candidate.Id] = true;
			}
			else
			{
				candidate.IsAvailable = false;
				availability[candidate.Id] = false;
			}
		}
		return availability;
	}

	private async Task<Dictionary<string, SongUrlInfo>> FetchSongUrlsInBatchesAsync(IEnumerable<string> songIds, bool skipAvailabilityCheck = true)
	{
		List<string> ids = songIds.Where((string id) => !string.IsNullOrWhiteSpace(id)).Distinct<string>(StringComparer.Ordinal).ToList();
		Dictionary<string, SongUrlInfo> result = new Dictionary<string, SongUrlInfo>(StringComparer.Ordinal);
		if (ids.Count == 0)
		{
			return result;
		}
		QualityLevel quality = GetCurrentQuality();
		for (int i = 0; i < ids.Count; i = checked(i + 50))
		{
			string[] batch = ids.Skip(i).Take(50).ToArray();
			Dictionary<string, SongUrlInfo> batchResult = await _apiClient.GetSongUrlAsync(batch, quality, skipAvailabilityCheck);
			if (batchResult == null)
			{
				continue;
			}
			foreach (KeyValuePair<string, SongUrlInfo> kvp in batchResult)
			{
				if (kvp.Value != null && !string.IsNullOrWhiteSpace(kvp.Value.Url))
				{
					result[kvp.Key] = kvp.Value;
				}
			}
		}
		return result;
	}

	private async void viewSongArtistMenuItem_Click(object sender, EventArgs e)
	{
		SongInfo song = GetSelectedSongFromContextMenu(sender);
		if (song == null || string.IsNullOrWhiteSpace(song.Id))
		{
			MessageBox.Show("无法获取当前歌曲信息。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			return;
		}
		try
		{
			UpdateStatusBar("正在加载歌手信息...");
			var (artistId, artistName) = await ResolvePrimaryArtistAsync(song);
			if (artistId <= 0)
			{
				MessageBox.Show("未找到该歌曲的歌手信息。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				UpdateStatusBar("无法打开歌手");
				return;
			}
			if (string.IsNullOrWhiteSpace(artistName))
			{
				artistName = song.ArtistNames.FirstOrDefault() ?? song.Artist ?? "歌手";
			}
			ArtistInfo artist = new ArtistInfo
			{
				Id = artistId,
				Name = artistName
			};
			await OpenArtistAsync(artist);
			UpdateStatusBar("已打开歌手：" + artistName);
		}
		catch (Exception ex)
		{
			MessageBox.Show("打开歌手失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("打开歌手失败");
		}
	}

	private async void viewSongAlbumMenuItem_Click(object sender, EventArgs e)
	{
		SongInfo song = GetSelectedSongFromContextMenu(sender);
		if (song == null || string.IsNullOrWhiteSpace(song.Id))
		{
			MessageBox.Show("无法获取当前歌曲信息。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			return;
		}
		try
		{
			UpdateStatusBar("正在加载专辑...");
			string albumId = await ResolveSongAlbumIdAsync(song);
			if (string.IsNullOrWhiteSpace(albumId))
			{
				MessageBox.Show("未找到该歌曲的专辑信息。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				UpdateStatusBar("无法打开专辑");
				return;
			}
			AlbumInfo album = new AlbumInfo
			{
				Id = albumId,
				Name = (string.IsNullOrWhiteSpace(song.Album) ? ("专辑 " + albumId) : song.Album),
				Artist = song.Artist,
				PicUrl = song.PicUrl
			};
			await OpenAlbum(album);
			UpdateStatusBar("已打开专辑：" + album.Name);
		}
		catch (Exception ex)
		{
			MessageBox.Show("打开专辑失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("打开专辑失败");
		}
	}

	private async void viewPodcastMenuItem_Click(object sender, EventArgs e)
	{
		PodcastRadioInfo podcast = GetSelectedPodcastFromContextMenu(sender);
		if (podcast == null || podcast.Id <= 0)
		{
			MessageBox.Show("无法获取播客信息。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			return;
		}
		try
		{
			string displayName = (string.IsNullOrWhiteSpace(podcast.Name) ? $"播客 {podcast.Id}" : podcast.Name);
			UpdateStatusBar("正在打开播客...");
			await OpenPodcastRadioAsync(podcast);
			UpdateStatusBar("已打开播客：" + displayName);
		}
		catch (Exception ex)
		{
			MessageBox.Show("打开播客失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("打开播客失败");
		}
	}

	private async void shareSongWebMenuItem_Click(object sender, EventArgs e)
	{
		SongInfo song = GetSelectedSongFromContextMenu(sender);
		if (song == null || string.IsNullOrWhiteSpace(song.Id))
		{
			MessageBox.Show("无法获取当前歌曲信息，无法分享。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			return;
		}
		try
		{
			UpdateStatusBar("正在检查歌曲资源...");
			if (!(await EnsureSongAvailabilityAsync(song)))
			{
				MessageBox.Show("该歌曲资源不可用，无法分享网页链接。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				UpdateStatusBar("歌曲资源不可用");
				return;
			}
			string url = "https://music.163.com/#/song?id=" + song.Id;
			try
			{
				Clipboard.SetText(url);
			}
			catch (ExternalException ex)
			{
				ExternalException ex2 = ex;
				ExternalException ex3 = ex2;
				MessageBox.Show("复制链接失败：" + ex3.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("复制链接失败");
				return;
			}
			UpdateStatusBar("歌曲网页链接已复制到剪贴板");
		}
		catch (Exception ex4)
		{
			MessageBox.Show("分享歌曲失败：" + ex4.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("歌曲分享失败");
		}
	}

	private async void shareSongDirectMenuItem_Click(object sender, EventArgs e)
	{
		SongInfo song = GetSelectedSongFromContextMenu(sender);
		if (song == null || string.IsNullOrWhiteSpace(song.Id))
		{
			MessageBox.Show("无法获取当前歌曲信息，无法分享。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			return;
		}
		try
		{
			UpdateStatusBar("正在生成歌曲直链...");
			var (resolve, url) = await ResolveShareUrlAsync(song, GetCurrentQuality(), CancellationToken.None);
			Debug.WriteLine($"[ShareDirect] song={song.Name}, id={song.Id}, usedUnblock={resolve.UsedUnblock}, level={song.Level}, url={(string.IsNullOrWhiteSpace(url) ? "<null>" : url)}");
			if (resolve.Status == SongResolveStatus.NotAvailable)
			{
				MessageBox.Show("该歌曲资源不可用，无法分享直链。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				UpdateStatusBar("歌曲资源不可用");
				return;
			}
			if (resolve.Status == SongResolveStatus.PaidAlbumNotPurchased)
			{
				MessageBox.Show("该歌曲属于付费数字专辑，未购买无法分享直链。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				UpdateStatusBar("付费专辑不可用");
				return;
			}
			if (string.IsNullOrWhiteSpace(url))
			{
				MessageBox.Show("未能获取歌曲直链，可能需要登录或切换音质。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				UpdateStatusBar("获取直链失败");
				return;
			}
			try
			{
				Clipboard.SetText(url);
			}
			catch (ExternalException ex)
			{
				ExternalException ex2 = ex;
				ExternalException ex3 = ex2;
				MessageBox.Show("复制链接失败：" + ex3.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("复制链接失败");
				return;
			}
			UpdateStatusBar("歌曲直链已复制到剪贴板");
		}
		catch (Exception ex4)
		{
			MessageBox.Show("分享歌曲直链失败：" + ex4.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("歌曲分享失败");
		}
	}

	private void sharePlaylistMenuItem_Click(object sender, EventArgs e)
	{
		PlaylistInfo selectedPlaylistFromContextMenu = GetSelectedPlaylistFromContextMenu(sender);
		if (selectedPlaylistFromContextMenu == null || string.IsNullOrWhiteSpace(selectedPlaylistFromContextMenu.Id))
		{
			MessageBox.Show("无法获取歌单信息，无法分享。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			return;
		}
		try
		{
			string text = "https://music.163.com/#/playlist?id=" + selectedPlaylistFromContextMenu.Id;
			Clipboard.SetText(text);
			UpdateStatusBar("歌单链接已复制到剪贴板");
		}
		catch (Exception ex)
		{
			MessageBox.Show("复制链接失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("复制链接失败");
		}
	}

	private void shareAlbumMenuItem_Click(object sender, EventArgs e)
	{
		AlbumInfo selectedAlbumFromContextMenu = GetSelectedAlbumFromContextMenu(sender);
		if (selectedAlbumFromContextMenu == null || string.IsNullOrWhiteSpace(selectedAlbumFromContextMenu.Id))
		{
			MessageBox.Show("无法获取专辑信息，无法分享。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			return;
		}
		try
		{
			string text = "https://music.163.com/#/album?id=" + selectedAlbumFromContextMenu.Id;
			Clipboard.SetText(text);
			UpdateStatusBar("专辑链接已复制到剪贴板");
		}
		catch (Exception ex)
		{
			MessageBox.Show("复制链接失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("复制链接失败");
		}
	}

	private async void createPlaylistMenuItem_Click(object sender, EventArgs e)
	{
		if (!IsUserLoggedIn())
		{
			MessageBox.Show("请先登录后再新建歌单", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			return;
		}
		string playlistName;
		using (NewPlaylistDialog dialog = new NewPlaylistDialog())
		{
			if (dialog.ShowDialog(this) != DialogResult.OK)
			{
				return;
			}
			playlistName = dialog.PlaylistName;
		}
		if (string.IsNullOrWhiteSpace(playlistName))
		{
			return;
		}
		try
		{
			UpdateStatusBar("正在创建歌单...");
			PlaylistInfo created = await _apiClient.CreatePlaylistAsync(playlistName);
			if (created != null && !string.IsNullOrWhiteSpace(created.Id))
			{
				UpdatePlaylistOwnershipState(created.Id, isOwned: true);
				MessageBox.Show("已新建歌单：" + created.Name, "成功", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				UpdateStatusBar("歌单创建成功");
				try
				{
					await RefreshUserPlaylistsIfActiveAsync();
				}
				catch (Exception ex)
				{
					Exception ex2 = ex;
					Exception refreshEx = ex2;
					Debug.WriteLine($"[UI] 刷新创建和收藏的歌单列表失败: {refreshEx}");
				}
			}
			else
			{
				MessageBox.Show("创建歌单失败，请稍后重试。", "失败", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("创建歌单失败");
			}
		}
		catch (Exception ex3)
		{
			MessageBox.Show("创建歌单失败: " + ex3.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("创建歌单失败");
		}
	}

	private async void subscribePlaylistMenuItem_Click(object sender, EventArgs e)
	{
		PlaylistInfo playlist = GetSelectedPlaylistFromContextMenu(sender);
		if (playlist == null || string.IsNullOrWhiteSpace(playlist.Id))
		{
			MessageBox.Show("无法获取歌单信息，无法收藏。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			return;
		}
		try
		{
			UpdateStatusBar("正在收藏歌单...");
			if (await _apiClient.SubscribePlaylistAsync(playlist.Id, subscribe: true))
			{
				playlist.IsSubscribed = true;
				UpdatePlaylistSubscriptionState(playlist.Id, isSubscribed: true);
				MessageBox.Show("已收藏歌单：" + playlist.Name, "成功", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				UpdateStatusBar("歌单收藏成功");
			}
			else
			{
				MessageBox.Show("收藏歌单失败，请检查网络或稍后重试。", "失败", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("歌单收藏失败");
			}
		}
		catch (Exception ex)
		{
			MessageBox.Show("收藏歌单失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("歌单收藏失败");
		}
	}

	private async void unsubscribePlaylistMenuItem_Click(object sender, EventArgs e)
	{
		PlaylistInfo playlist = GetSelectedPlaylistFromContextMenu(sender);
		if (playlist == null || string.IsNullOrWhiteSpace(playlist.Id))
		{
			MessageBox.Show("无法获取歌单信息，无法取消收藏。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			return;
		}
		try
		{
			UpdateStatusBar("正在取消收藏歌单...");
			if (await _apiClient.SubscribePlaylistAsync(playlist.Id, subscribe: false))
			{
				playlist.IsSubscribed = false;
				UpdatePlaylistSubscriptionState(playlist.Id, isSubscribed: false);
				MessageBox.Show("已取消收藏歌单：" + playlist.Name, "成功", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				UpdateStatusBar("取消收藏成功");
				try
				{
					await RefreshUserPlaylistsIfActiveAsync();
					return;
				}
				catch (Exception ex)
				{
					Exception ex2 = ex;
					Exception refreshEx = ex2;
					Debug.WriteLine($"[UI] 刷新创建和收藏的歌单列表失败: {refreshEx}");
					return;
				}
			}
			MessageBox.Show("取消收藏失败，请检查网络或稍后重试。", "失败", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("取消收藏失败");
		}
		catch (Exception ex3)
		{
			MessageBox.Show("取消收藏失败: " + ex3.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("取消收藏失败");
		}
	}

	private async void deletePlaylistMenuItem_Click(object sender, EventArgs e)
	{
		object obj = GetSelectedListViewItemSafe()?.Tag;
		if (!(obj is PlaylistInfo playlist))
		{
			return;
		}
		DialogResult confirm = MessageBox.Show("确定要删除歌单：" + playlist.Name + "？\n删除后将无法恢复。", "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Exclamation);
		if (confirm != DialogResult.Yes)
		{
			return;
		}
		try
		{
			UpdateStatusBar("正在删除歌单...");
			if (await _apiClient.DeletePlaylistAsync(playlist.Id))
			{
				UpdatePlaylistOwnershipState(playlist.Id, isOwned: false);
				UpdatePlaylistSubscriptionState(playlist.Id, isSubscribed: false);
				MessageBox.Show("已删除歌单：" + playlist.Name, "成功", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				UpdateStatusBar("删除歌单成功");
				try
				{
					await RefreshUserPlaylistsIfActiveAsync();
					return;
				}
				catch (Exception ex)
				{
					Exception ex2 = ex;
					Exception refreshEx = ex2;
					Debug.WriteLine($"[UI] 刷新创建和收藏的歌单列表失败: {refreshEx}");
					return;
				}
			}
			MessageBox.Show("删除歌单失败，请检查网络或稍后重试。", "失败", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("删除歌单失败");
		}
		catch (Exception ex3)
		{
			MessageBox.Show("删除歌单失败: " + ex3.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("删除歌单失败");
		}
	}

	private async void subscribeAlbumMenuItem_Click(object sender, EventArgs e)
	{
		AlbumInfo album = GetSelectedAlbumFromContextMenu(sender);
		if (album == null || string.IsNullOrWhiteSpace(album.Id))
		{
			MessageBox.Show("\u65E0\u6CD5\u8BC6\u522B\u4E13\u8F91\u4FE1\u606F\uFF0C\u6536\u85CF\u64CD\u4F5C\u5DF2\u53D6\u6D88\u3002", "\u63D0\u793A", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			return;
		}
		await SubscribeAlbumAsync(album);
	}

	private async void subscribeSongArtistMenuItem_Click(object sender, EventArgs e)
	{
		ArtistInfo artistInfo = null;
		SongInfo song = null;
		if (sender is ToolStripItem toolStripItem)
		{
			artistInfo = toolStripItem.Tag as ArtistInfo;
			song = toolStripItem.Tag as SongInfo;
		}
		if (song == null)
		{
			song = GetSelectedSongFromContextMenu(sender);
		}
		bool isCloudSongContext = IsCloudSongContext(song);
		if (!isCloudSongContext && _isCurrentPlayingMenuActive)
		{
			string text2 = ResolveCurrentPlayingViewSource(song);
			isCloudSongContext = !string.IsNullOrWhiteSpace(text2) && text2.StartsWith("user_cloud", StringComparison.OrdinalIgnoreCase);
		}
		if (artistInfo == null)
		{
			artistInfo = await ResolveSongArtistForSubscriptionAsync(song, isCloudSongContext);
		}
		if (artistInfo == null || artistInfo.Id <= 0)
		{
			MessageBox.Show("\u65E0\u6CD5\u8BC6\u522B\u6B4C\u624B\u4FE1\u606F\uFF0C\u6536\u85CF\u64CD\u4F5C\u5DF2\u53D6\u6D88\u3002", "\u63D0\u793A", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			return;
		}
		if (IsArtistSubscribed(artistInfo))
		{
			UpdateStatusBar("\u8BE5\u6B4C\u66F2\u6B4C\u624B\u5DF2\u5728\u6536\u85CF\u4E2D");
			return;
		}
		await SubscribeArtistAsync(artistInfo);
	}

	private async void subscribeSongAlbumMenuItem_Click(object sender, EventArgs e)
	{
		AlbumInfo album = null;
		SongInfo song = null;
		if (sender is ToolStripItem toolStripItem)
		{
			album = toolStripItem.Tag as AlbumInfo;
			song = toolStripItem.Tag as SongInfo;
		}
		if (song == null)
		{
			song = GetSelectedSongFromContextMenu(sender);
		}
		bool isCloudSongContext = IsCloudSongContext(song);
		if (!isCloudSongContext && _isCurrentPlayingMenuActive)
		{
			string text2 = ResolveCurrentPlayingViewSource(song);
			isCloudSongContext = !string.IsNullOrWhiteSpace(text2) && text2.StartsWith("user_cloud", StringComparison.OrdinalIgnoreCase);
		}
		if ((album == null || string.IsNullOrWhiteSpace(album.Id)) && song != null && !isCloudSongContext)
		{
			string text = await ResolveSongAlbumIdAsync(song);
			if (!string.IsNullOrWhiteSpace(text))
			{
				album = TryCreateAlbumInfoFromSong(song) ?? new AlbumInfo
				{
					Id = text,
					Name = (string.IsNullOrWhiteSpace(song.Album) ? "\u4E13\u8F91" : song.Album),
					Artist = song.Artist ?? string.Empty,
					PicUrl = song.PicUrl ?? string.Empty
				};
			}
		}
		if (album == null || string.IsNullOrWhiteSpace(album.Id))
		{
			MessageBox.Show("\u65E0\u6CD5\u8BC6\u522B\u4E13\u8F91\u4FE1\u606F\uFF0C\u6536\u85CF\u64CD\u4F5C\u5DF2\u53D6\u6D88\u3002", "\u63D0\u793A", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			return;
		}
		if (IsAlbumSubscribed(album))
		{
			UpdateStatusBar("\u8BE5\u6B4C\u66F2\u4E13\u8F91\u5DF2\u5728\u6536\u85CF\u4E2D");
			return;
		}
		await SubscribeAlbumAsync(album);
	}

	private async Task SubscribeAlbumAsync(AlbumInfo album)
	{
		if (album == null || string.IsNullOrWhiteSpace(album.Id))
		{
			MessageBox.Show("\u65E0\u6CD5\u8BC6\u522B\u4E13\u8F91\u4FE1\u606F\uFF0C\u6536\u85CF\u64CD\u4F5C\u5DF2\u53D6\u6D88\u3002", "\u63D0\u793A", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			return;
		}
		try
		{
			UpdateStatusBar("\u6B63\u5728\u6536\u85CF\u4E13\u8F91...");
			if (await _apiClient.SubscribeAlbumAsync(album.Id))
			{
				album.IsSubscribed = true;
				UpdateAlbumSubscriptionState(album.Id, isSubscribed: true);
				MessageBox.Show("\u5DF2\u6536\u85CF\u4E13\u8F91\uFF1A" + album.Name, "\u6210\u529F", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				UpdateStatusBar("\u4E13\u8F91\u6536\u85CF\u6210\u529F");
			}
			else
			{
				MessageBox.Show("\u6536\u85CF\u4E13\u8F91\u5931\u8D25\uFF0C\u8BF7\u68C0\u67E5\u7F51\u7EDC\u6216\u7A0D\u540E\u91CD\u8BD5\u3002", "\u5931\u8D25", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("\u4E13\u8F91\u6536\u85CF\u5931\u8D25");
			}
		}
		catch (Exception ex)
		{
			MessageBox.Show("\u6536\u85CF\u4E13\u8F91\u5931\u8D25: " + ex.Message, "\u9519\u8BEF", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("\u4E13\u8F91\u6536\u85CF\u5931\u8D25");
		}
	}

	private void sharePodcastMenuItem_Click(object sender, EventArgs e)
	{
		PodcastRadioInfo selectedPodcastFromContextMenu = GetSelectedPodcastFromContextMenu(sender);
		if (selectedPodcastFromContextMenu == null || selectedPodcastFromContextMenu.Id <= 0)
		{
			MessageBox.Show("无法获取播客信息，无法分享。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			return;
		}
		try
		{
			string text = $"https://music.163.com/#/djradio?id={selectedPodcastFromContextMenu.Id}";
			Clipboard.SetText(text);
			UpdateStatusBar("播客链接已复制到剪贴板");
		}
		catch (ExternalException ex)
		{
			MessageBox.Show("复制链接失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("复制链接失败");
		}
	}

	private void sharePodcastEpisodeWebMenuItem_Click(object sender, EventArgs e)
	{
		PodcastEpisodeInfo selectedPodcastEpisodeFromContextMenu = GetSelectedPodcastEpisodeFromContextMenu(sender);
		if (selectedPodcastEpisodeFromContextMenu == null || selectedPodcastEpisodeFromContextMenu.ProgramId <= 0)
		{
			MessageBox.Show("无法获取节目详情，无法分享。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			return;
		}
		try
		{
			string text = $"https://music.163.com/#/program?id={selectedPodcastEpisodeFromContextMenu.ProgramId}";
			Clipboard.SetText(text);
			UpdateStatusBar("节目网页链接已复制到剪贴板");
		}
		catch (ExternalException ex)
		{
			MessageBox.Show("复制链接失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("复制链接失败");
		}
	}

	private async void sharePodcastEpisodeDirectMenuItem_Click(object sender, EventArgs e)
	{
		PodcastEpisodeInfo episode = GetSelectedPodcastEpisodeFromContextMenu(sender);
		if (episode == null || episode.ProgramId <= 0)
		{
			MessageBox.Show("无法获取节目详情，无法分享。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			return;
		}
		SongInfo song = EnsurePodcastEpisodeSong(episode);
		if (song == null || string.IsNullOrWhiteSpace(song.Id))
		{
			MessageBox.Show("该节目缺少可用的音频资源，无法分享直链。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			return;
		}
		try
		{
			UpdateStatusBar("正在生成节目直链...");
			var (resolve, url) = await ResolveShareUrlAsync(song, GetCurrentQuality(), CancellationToken.None);
			Debug.WriteLine($"[ShareDirect] episode={episode.Name}, id={song.Id}, usedUnblock={resolve.UsedUnblock}, level={song.Level}, url={(string.IsNullOrWhiteSpace(url) ? "<null>" : url)}");
			if (resolve.Status == SongResolveStatus.NotAvailable)
			{
				MessageBox.Show("该节目资源不可用，无法分享直链。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				UpdateStatusBar("节目资源不可用");
			}
			else if (resolve.Status == SongResolveStatus.PaidAlbumNotPurchased)
			{
				MessageBox.Show("该节目属于付费数字专辑，未购买无法分享直链。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				UpdateStatusBar("付费专辑不可用");
			}
			else if (string.IsNullOrWhiteSpace(url))
			{
				MessageBox.Show("未能获取节目直链，可能需要登录或稍后重试。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				UpdateStatusBar("获取直链失败");
			}
			else
			{
				Clipboard.SetText(url);
				UpdateStatusBar("节目直链已复制到剪贴板");
			}
		}
		catch (ExternalException ex)
		{
			MessageBox.Show("复制链接失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("复制链接失败");
		}
		catch (Exception ex2)
		{
			MessageBox.Show("分享节目直链失败：" + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("节目分享失败");
		}
	}

	private async Task RefreshCurrentViewAsync(bool forceLibraryRefresh = true)
	{
		if (string.IsNullOrWhiteSpace(_currentViewSource))
		{
			UpdateStatusBar("当前没有可刷新的内容");
			return;
		}
		NavigationHistoryItem state = CreateCurrentState();
		if (state == null)
		{
			UpdateStatusBar("当前视图不支持刷新");
			return;
		}
		try
		{
			if (forceLibraryRefresh)
			{
				LibraryEntityType? entity = ResolveLibraryEntityFromState(state);
				if (entity.HasValue)
				{
					await RefreshLibraryStateAsync(entity.Value, forceRefresh: true, CancellationToken.None);
				}
			}
			if (await RestoreNavigationStateAsync(state))
			{
				UpdateStatusBar("页面已刷新");
			}
		}
		catch (Exception value)
		{
			Debug.WriteLine($"[Refresh] 刷新失败: {value}");
			UpdateStatusBar("刷新失败");
		}
	}

	private LibraryEntityType? ResolveLibraryEntityFromState(NavigationHistoryItem state)
	{
		string a = state.ViewSource ?? string.Empty;
		if (string.Equals(a, "user_liked_songs", StringComparison.OrdinalIgnoreCase))
		{
			return LibraryEntityType.Songs;
		}
		if (string.Equals(a, "user_playlists", StringComparison.OrdinalIgnoreCase))
		{
			return LibraryEntityType.Playlists;
		}
		if (string.Equals(a, "user_albums", StringComparison.OrdinalIgnoreCase))
		{
			return LibraryEntityType.Albums;
		}
		if (string.Equals(a, "user_podcasts", StringComparison.OrdinalIgnoreCase))
		{
			return LibraryEntityType.Podcasts;
		}
		if (string.Equals(a, "artist_favorites", StringComparison.OrdinalIgnoreCase) || string.Equals(state.PageType, "artist_favorites", StringComparison.OrdinalIgnoreCase))
		{
			return LibraryEntityType.Artists;
		}
		return null;
	}

	private async void refreshMenuItem_Click(object sender, EventArgs e)
	{
		await RefreshCurrentViewAsync();
	}

	private async void artistSongsSortHotMenuItem_Click(object sender, EventArgs e)
	{
		await ChangeArtistSongsSortAsync(ArtistSongSortOption.Hot);
	}

	private async void artistSongsSortTimeMenuItem_Click(object sender, EventArgs e)
	{
		await ChangeArtistSongsSortAsync(ArtistSongSortOption.Time);
	}

	private async void artistAlbumsSortLatestMenuItem_Click(object sender, EventArgs e)
	{
		await ChangeArtistAlbumsSortAsync(ArtistAlbumSortOption.Latest);
	}

	private async void artistAlbumsSortOldestMenuItem_Click(object sender, EventArgs e)
	{
		await ChangeArtistAlbumsSortAsync(ArtistAlbumSortOption.Oldest);
	}

	private async Task ChangeArtistAlbumsSortAsync(ArtistAlbumSortOption targetSort)
	{
		if (!string.IsNullOrWhiteSpace(_currentViewSource) && _currentViewSource.StartsWith("artist_albums:", StringComparison.OrdinalIgnoreCase))
		{
			ParseArtistListViewSource(_currentViewSource, out var artistId, out var _, out var currentOrderToken, "latest");
			ArtistAlbumSortOption currentSort = ResolveArtistAlbumSort(currentOrderToken);
			if (_artistAlbumSortState.EqualsOption(targetSort) && currentSort == targetSort)
			{
				UpdateArtistAlbumsSortMenuChecks();
				return;
			}
			_artistAlbumSortState.SetOption(targetSort);
			await LoadArtistAlbumsAsync(artistId, 0, skipSave: true, targetSort);
			UpdateArtistAlbumsSortMenuChecks();
		}
	}

	private async Task ChangeArtistSongsSortAsync(ArtistSongSortOption targetOrder)
	{
		if (!string.IsNullOrWhiteSpace(_currentViewSource) && _currentViewSource.StartsWith("artist_songs:", StringComparison.OrdinalIgnoreCase))
		{
			ParseArtistListViewSource(_currentViewSource, out var artistId, out var _, out var currentOrderToken);
			ArtistSongSortOption currentOrder = ResolveArtistSongsOrder(currentOrderToken);
			if (_artistSongSortState.EqualsOption(targetOrder) && currentOrder == targetOrder)
			{
				UpdateArtistSongsSortMenuChecks();
				return;
			}
			_artistSongSortState.SetOption(targetOrder);
			await LoadArtistSongsAsync(artistId, 0, skipSave: true, targetOrder);
			UpdateArtistSongsSortMenuChecks();
		}
	}

	private async void podcastSortLatestMenuItem_Click(object sender, EventArgs e)
	{
		await ChangePodcastEpisodeSortAsync(ascending: false);
	}

	private async void podcastSortSerialMenuItem_Click(object sender, EventArgs e)
	{
		await ChangePodcastEpisodeSortAsync(ascending: true);
	}

	private async Task ChangePodcastEpisodeSortAsync(bool ascending)
	{
		if (string.IsNullOrWhiteSpace(_currentViewSource) || !_currentViewSource.StartsWith("podcast:", StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		ParsePodcastViewSource(_currentViewSource, out var podcastId, out var _, out var currentAscending);
		if (podcastId > 0)
		{
			if (_podcastSortState.EqualsOption(ascending) && currentAscending == ascending)
			{
				UpdatePodcastSortMenuChecks();
				return;
			}
			_podcastSortState.SetOption(ascending);
			await LoadPodcastEpisodesAsync(podcastId, 0, skipSave: true, _currentPodcast, ascending);
			UpdatePodcastSortMenuChecks();
		}
	}

	private async void subscribePodcastMenuItem_Click(object sender, EventArgs e)
	{
		PodcastRadioInfo podcast = GetSelectedPodcastFromContextMenu(sender);
		if (podcast == null || podcast.Id <= 0)
		{
			MessageBox.Show("无法识别播客信息，收藏操作已取消。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			UpdateStatusBar("收藏播客失败");
			return;
		}
		try
		{
			UpdateStatusBar("正在收藏播客...");
			if (await _apiClient.SubscribePodcastAsync(podcast.Id))
			{
				podcast.Subscribed = true;
				if (_currentPodcast != null && _currentPodcast.Id == podcast.Id)
				{
					_currentPodcast.Subscribed = true;
				}
				UpdatePodcastSubscriptionState(podcast.Id, isSubscribed: true);
				MessageBox.Show("已收藏播客：" + podcast.Name, "成功", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				UpdateStatusBar("播客收藏成功");
				await RefreshUserPodcastsIfActiveAsync();
			}
			else
			{
				MessageBox.Show("收藏播客失败，请稍后重试。", "失败", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("收藏播客失败");
			}
		}
		catch (Exception ex)
		{
			MessageBox.Show("收藏播客失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("收藏播客失败");
		}
	}

	private async void unsubscribePodcastMenuItem_Click(object sender, EventArgs e)
	{
		PodcastRadioInfo podcast = GetSelectedPodcastFromContextMenu(sender);
		if (podcast == null || podcast.Id <= 0)
		{
			MessageBox.Show("无法识别播客信息，取消收藏操作已取消。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			UpdateStatusBar("取消收藏播客失败");
			return;
		}
		try
		{
			UpdateStatusBar("正在取消收藏播客...");
			if (await _apiClient.UnsubscribePodcastAsync(podcast.Id))
			{
				podcast.Subscribed = false;
				if (_currentPodcast != null && _currentPodcast.Id == podcast.Id)
				{
					_currentPodcast.Subscribed = false;
				}
				UpdatePodcastSubscriptionState(podcast.Id, isSubscribed: false);
				MessageBox.Show("已取消收藏播客：" + podcast.Name, "成功", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				UpdateStatusBar("取消收藏播客成功");
				await RefreshUserPodcastsIfActiveAsync();
			}
			else
			{
				MessageBox.Show("取消收藏播客失败，请稍后重试。", "失败", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("取消收藏播客失败");
			}
		}
		catch (Exception ex)
		{
			MessageBox.Show("取消收藏播客失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("取消收藏播客失败");
		}
	}

	private async void unsubscribeAlbumMenuItem_Click(object sender, EventArgs e)
	{
		AlbumInfo album = GetSelectedAlbumFromContextMenu(sender);
		if (album == null || string.IsNullOrWhiteSpace(album.Id))
		{
			MessageBox.Show("无法识别专辑信息，取消收藏操作已取消。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			return;
		}
		try
		{
			UpdateStatusBar("正在取消收藏专辑...");
			if (await _apiClient.UnsubscribeAlbumAsync(album.Id))
			{
				album.IsSubscribed = false;
				UpdateAlbumSubscriptionState(album.Id, isSubscribed: false);
				MessageBox.Show("已取消收藏专辑：" + album.Name, "成功", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				UpdateStatusBar("取消收藏成功");
				try
				{
					await RefreshUserAlbumsIfActiveAsync();
					return;
				}
				catch (Exception ex)
				{
					Exception ex2 = ex;
					Exception refreshEx = ex2;
					Debug.WriteLine($"[UI] 刷新收藏的专辑列表失败: {refreshEx}");
					return;
				}
			}
			MessageBox.Show("取消收藏失败，请检查网络或稍后重试。", "失败", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("取消收藏失败");
		}
		catch (Exception ex3)
		{
			MessageBox.Show("取消收藏失败: " + ex3.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("取消收藏失败");
		}
	}

	private async Task RefreshUserPlaylistsIfActiveAsync()
	{
		if (string.Equals(_currentViewSource, "user_playlists", StringComparison.OrdinalIgnoreCase))
		{
			await LoadUserPlaylists(preserveSelection: true);
		}
	}

	private async Task RefreshUserAlbumsIfActiveAsync()
	{
		if (string.Equals(_currentViewSource, "user_albums", StringComparison.OrdinalIgnoreCase))
		{
			await LoadUserAlbums(preserveSelection: true);
		}
	}

	private async Task RefreshUserPodcastsIfActiveAsync()
	{
		if (string.Equals(_currentViewSource, "user_podcasts", StringComparison.OrdinalIgnoreCase))
		{
			await LoadUserPodcasts(preserveSelection: true);
		}
	}

	private long GetCurrentUserId()
	{
		if (_loggedInUserId > 0)
		{
			return _loggedInUserId;
		}
		long num = ParseUserIdFromAccountState(_accountState);
		if (num > 0)
		{
			CacheLoggedInUserId(num);
			return _loggedInUserId;
		}
		return 0L;
	}

	private static long ParseUserIdFromAccountState(AccountState? state)
	{
		if (state == null)
		{
			return 0L;
		}
		if (long.TryParse(state.UserId, out var result) && result > 0)
		{
			return result;
		}
		UserAccountInfo? accountDetail = state.AccountDetail;
		if (accountDetail != null && accountDetail.UserId > 0)
		{
			return state.AccountDetail.UserId;
		}
		return 0L;
	}

	private void CacheLoggedInUserId(long userId)
	{
		if (userId > 0)
		{
			_loggedInUserId = userId;
		}
	}

	private bool IsPlaylistCreatedByCurrentUser(PlaylistInfo playlist)
	{
		long currentUserId = GetCurrentUserId();
		return IsPlaylistOwnedByUser(playlist, currentUserId);
	}

	private bool IsCurrentPlaylistOwnedByUser(string? playlistId = null)
	{
		long currentUserId = GetCurrentUserId();
		if (currentUserId <= 0)
		{
			return false;
		}
		if (_currentPlaylist != null && IsPlaylistOwnedByUser(_currentPlaylist, currentUserId))
		{
			return true;
		}
		if (!string.IsNullOrWhiteSpace(playlistId))
		{
			PlaylistInfo playlistInfo = _currentPlaylists?.FirstOrDefault((PlaylistInfo p) => string.Equals(p?.Id, playlistId, StringComparison.OrdinalIgnoreCase)) ?? null;
			if (playlistInfo != null && IsPlaylistOwnedByUser(playlistInfo, currentUserId))
			{
				return true;
			}
		}
		return false;
	}

	private bool IsCurrentLikedSongsView()
	{
		if (string.Equals(_currentViewSource, "user_liked_songs", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		if (_currentPlaylist == null)
		{
			return false;
		}
		if (_userLikedPlaylist != null && !string.IsNullOrWhiteSpace(_userLikedPlaylist.Id) && string.Equals(_currentPlaylist.Id, _userLikedPlaylist.Id, StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		long currentUserId = GetCurrentUserId();
		return currentUserId > 0 && IsLikedMusicPlaylist(_currentPlaylist, currentUserId);
	}

	private bool CanReorderPlaylistListView()
	{
		return string.Equals(_currentViewSource, "user_playlists", StringComparison.OrdinalIgnoreCase);
	}

	private bool CanReorderPlaylistSongsView()
	{
		if (IsCurrentLikedSongsView())
		{
			return false;
		}
		if (_currentViewSource == null || !_currentViewSource.StartsWith("playlist:", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		return IsCurrentPlaylistOwnedByUser();
	}

	private static bool AreIdListsEqual(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
	{
		if (ReferenceEquals(left, right))
		{
			return true;
		}
		if (left == null || right == null || left.Count != right.Count)
		{
			return false;
		}
		for (int i = 0; i < left.Count; i++)
		{
			if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
			{
				return false;
			}
		}
		return true;
	}

	private List<PlaylistInfo> NormalizeUserPlaylists(List<PlaylistInfo> playlists)
	{
		if (playlists == null || playlists.Count == 0)
		{
			return playlists ?? new List<PlaylistInfo>();
		}
		long userId = GetCurrentUserId();
		if (userId <= 0)
		{
			return new List<PlaylistInfo>(playlists);
		}
		List<PlaylistInfo> owned = new List<PlaylistInfo>();
		List<PlaylistInfo> subscribed = new List<PlaylistInfo>();
		foreach (PlaylistInfo playlist in playlists)
		{
			if (IsPlaylistOwnedByUser(playlist, userId))
			{
				owned.Add(playlist);
			}
			else
			{
				subscribed.Add(playlist);
			}
		}
		owned.AddRange(subscribed);
		return owned;
	}

	private bool TryEnsureUserPlaylistGrouping()
	{
		if (!CanReorderPlaylistListView() || _currentPlaylists == null || _currentPlaylists.Count == 0)
		{
			return false;
		}
		long userId = GetCurrentUserId();
		if (userId <= 0)
		{
			return false;
		}
		bool seenNonOwned = false;
		bool mixed = false;
		foreach (PlaylistInfo playlist in _currentPlaylists)
		{
			bool owned = IsPlaylistOwnedByUser(playlist, userId);
			if (!owned)
			{
				seenNonOwned = true;
			}
			else if (seenNonOwned)
			{
				mixed = true;
				break;
			}
		}
		if (!mixed)
		{
			return false;
		}

		List<PlaylistInfo> normalized = NormalizeUserPlaylists(_currentPlaylists);
		string? selectedId = null;
		if (resultListView != null && resultListView.SelectedIndices.Count > 0)
		{
			int selectedIndex = resultListView.SelectedIndices[0];
			if (selectedIndex >= 0 && selectedIndex < _currentPlaylists.Count)
			{
				selectedId = _currentPlaylists[selectedIndex]?.Id;
			}
		}
		DisplayPlaylists(normalized, preserveSelection: false, _currentViewSource, "创建和收藏的歌单", announceHeader: false, suppressFocus: true);
		if (!string.IsNullOrWhiteSpace(selectedId))
		{
			int newIndex = _currentPlaylists.FindIndex((PlaylistInfo p) => string.Equals(p?.Id, selectedId, StringComparison.OrdinalIgnoreCase));
			if (newIndex >= 0)
			{
				EnsureListSelectionWithoutFocus(newIndex);
			}
		}
		return true;
	}

	private bool TryGetDragDropIndex(Point clientPoint, out int index)
	{
		index = -1;
		if (resultListView == null)
		{
			return false;
		}
		ListViewItem item = resultListView.GetItemAt(clientPoint.X, clientPoint.Y);
		if (item != null)
		{
			index = item.Index;
			return true;
		}
		if (resultListView.Items.Count == 0)
		{
			return false;
		}
		Rectangle first = resultListView.Items[0].Bounds;
		Rectangle last = resultListView.Items[resultListView.Items.Count - 1].Bounds;
		if (clientPoint.Y < first.Top)
		{
			index = 0;
			return true;
		}
		if (clientPoint.Y > last.Bottom)
		{
			index = resultListView.Items.Count - 1;
			return true;
		}
		return false;
	}

	private bool IsPlaylistReorderTargetValid(int oldIndex, int newIndex, out string? boundaryStatus)
	{
		boundaryStatus = null;
		if (_currentPlaylists == null || _currentPlaylists.Count == 0)
		{
			return false;
		}
		long userId = GetCurrentUserId();
		if (userId <= 0)
		{
			return false;
		}
		if (oldIndex < 0 || oldIndex >= _currentPlaylists.Count)
		{
			return false;
		}
		bool isOwned = IsPlaylistOwnedByUser(_currentPlaylists[oldIndex], userId);
		int boundary = _currentPlaylists.Count;
		for (int i = 0; i < _currentPlaylists.Count; i++)
		{
			if (!IsPlaylistOwnedByUser(_currentPlaylists[i], userId))
			{
				boundary = i;
				break;
			}
		}
		int groupStart = isOwned ? 0 : boundary;
		int groupEnd = isOwned ? Math.Max(boundary - 1, 0) : _currentPlaylists.Count - 1;
		if (newIndex < groupStart)
		{
			boundaryStatus = "已经到顶了";
			return false;
		}
		if (newIndex > groupEnd)
		{
			boundaryStatus = "已经到底了";
			return false;
		}
		return true;
	}

	private static bool TryMoveItem<T>(List<T> list, int oldIndex, int newIndex)
	{
		if (list == null || oldIndex < 0 || oldIndex >= list.Count || newIndex < 0 || newIndex >= list.Count)
		{
			return false;
		}
		if (oldIndex == newIndex)
		{
			return false;
		}
		T item = list[oldIndex];
		list.RemoveAt(oldIndex);
		list.Insert(newIndex, item);
		return true;
	}

	private void MoveSelectedPlaylist(int delta)
	{
		if (!CanReorderPlaylistListView() || _currentPlaylists == null || _currentPlaylists.Count == 0)
		{
			return;
		}
		if (TryEnsureUserPlaylistGrouping())
		{
			return;
		}
		if (resultListView == null || resultListView.SelectedIndices.Count == 0)
		{
			return;
		}
		int oldIndex = resultListView.SelectedIndices[0];
		if (oldIndex < 0 || oldIndex >= _currentPlaylists.Count)
		{
			return;
		}
		long userId = GetCurrentUserId();
		if (userId <= 0)
		{
			return;
		}
		PlaylistInfo selected = _currentPlaylists[oldIndex];
		bool isOwned = IsPlaylistOwnedByUser(selected, userId);
		int boundary = _currentPlaylists.Count;
		for (int i = 0; i < _currentPlaylists.Count; i++)
		{
			if (!IsPlaylistOwnedByUser(_currentPlaylists[i], userId))
			{
				boundary = i;
				break;
			}
		}
		int groupStart = isOwned ? 0 : boundary;
		int groupEnd = isOwned ? Math.Max(boundary - 1, 0) : _currentPlaylists.Count - 1;
		int newIndex = oldIndex + delta;
		if (newIndex < groupStart)
		{
			UpdateStatusBar("已经到顶了");
			return;
		}
		if (newIndex > groupEnd)
		{
			UpdateStatusBar("已经到底了");
			return;
		}
		if (!TryMoveItem(_currentPlaylists, oldIndex, newIndex))
		{
			return;
		}
		DisplayPlaylists(_currentPlaylists, preserveSelection: false, _currentViewSource, "创建和收藏的歌单", announceHeader: false, suppressFocus: true);
		EnsureListSelectionWithoutFocus(newIndex);
		UpdateStatusBar("歌单顺序已调整（自动保存中）");
		SchedulePlaylistOrderAutoSave();
	}

	private void MoveSelectedSongInPlaylist(int delta)
	{
		if (!CanReorderPlaylistSongsView() || _currentSongs == null || _currentSongs.Count == 0)
		{
			return;
		}
		if (resultListView == null || resultListView.SelectedIndices.Count == 0)
		{
			return;
		}
		int oldIndex = resultListView.SelectedIndices[0];
		if (oldIndex < 0 || oldIndex >= _currentSongs.Count)
		{
			return;
		}
		int newIndex = Math.Max(0, Math.Min(_currentSongs.Count - 1, oldIndex + delta));
		if (!TryMoveItem(_currentSongs, oldIndex, newIndex))
		{
			return;
		}
		DisplaySongs(_currentSongs, showPagination: false, hasNextPage: false, 1, preserveSelection: false, _currentViewSource, _currentPlaylist?.Name ?? "歌单", skipAvailabilityCheck: true, announceHeader: false, suppressFocus: true);
		EnsureListSelectionWithoutFocus(newIndex);
		UpdateStatusBar("歌曲顺序已调整（自动保存中）");
		ScheduleSongOrderAutoSave();
	}

	private void SchedulePlaylistOrderAutoSave()
	{
		if (!CanReorderPlaylistListView() || _currentPlaylists == null || _currentPlaylists.Count == 0)
		{
			return;
		}
		_pendingPlaylistOrderIds = _currentPlaylists.Select((PlaylistInfo p) => p.Id).ToList();
		if (_playlistOrderAutoSaveTimer == null)
		{
			return;
		}
		_playlistOrderAutoSaveTimer.Stop();
		_playlistOrderAutoSaveTimer.Start();
	}

	private void ScheduleSongOrderAutoSave()
	{
		if (!CanReorderPlaylistSongsView() || _currentSongs == null || _currentSongs.Count == 0)
		{
			return;
		}
		if (_currentPlaylist == null || string.IsNullOrWhiteSpace(_currentPlaylist.Id))
		{
			return;
		}
		_pendingSongOrderPlaylistId = _currentPlaylist.Id;
		_pendingSongOrderIds = _currentSongs.Select((SongInfo s) => s.Id).ToList();
		if (_songOrderAutoSaveTimer == null)
		{
			return;
		}
		_songOrderAutoSaveTimer.Stop();
		_songOrderAutoSaveTimer.Start();
	}

	private async Task TryAutoSavePlaylistOrderAsync()
	{
		if (_playlistOrderSaving)
		{
			_playlistOrderAutoSaveTimer?.Stop();
			_playlistOrderAutoSaveTimer?.Start();
			return;
		}
		if (!IsUserLoggedIn())
		{
			MessageBox.Show("请先登录后再保存歌单顺序。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			UpdateStatusBar("保存歌单顺序失败");
			return;
		}
		if (_pendingPlaylistOrderIds == null || _pendingPlaylistOrderIds.Count == 0)
		{
			return;
		}

		var idsSnapshot = new List<string>(_pendingPlaylistOrderIds);
		_playlistOrderSaving = true;
		UpdateStatusBar("正在保存歌单顺序...");
		bool ok = await _apiClient.UpdatePlaylistOrderAsync(idsSnapshot).ConfigureAwait(true);
		_playlistOrderSaving = false;

		if (!ok)
		{
			MessageBox.Show("保存歌单顺序失败，请稍后再试。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			UpdateStatusBar("保存歌单顺序失败");
			return;
		}

		if (!AreIdListsEqual(_pendingPlaylistOrderIds, idsSnapshot))
		{
			_playlistOrderAutoSaveTimer?.Stop();
			_playlistOrderAutoSaveTimer?.Start();
			return;
		}

		_pendingPlaylistOrderIds = null;
		UpdateStatusBar("歌单顺序已自动保存");
	}

	private async Task TryAutoSaveSongOrderAsync()
	{
		if (_songOrderSaving)
		{
			_songOrderAutoSaveTimer?.Stop();
			_songOrderAutoSaveTimer?.Start();
			return;
		}
		if (!IsUserLoggedIn())
		{
			MessageBox.Show("请先登录后再保存歌曲顺序。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			UpdateStatusBar("保存歌曲顺序失败");
			return;
		}
		if (string.IsNullOrWhiteSpace(_pendingSongOrderPlaylistId) || _pendingSongOrderIds == null || _pendingSongOrderIds.Count == 0)
		{
			return;
		}

		string playlistIdSnapshot = _pendingSongOrderPlaylistId;
		var idsSnapshot = new List<string>(_pendingSongOrderIds);
		_songOrderSaving = true;
		UpdateStatusBar("正在保存歌曲顺序...");
		bool ok = await _apiClient.UpdatePlaylistTrackOrderAsync(playlistIdSnapshot, idsSnapshot).ConfigureAwait(true);
		_songOrderSaving = false;

		if (!ok)
		{
			MessageBox.Show("保存歌曲顺序失败，请稍后再试。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			UpdateStatusBar("保存歌曲顺序失败");
			return;
		}

		if (!string.Equals(_pendingSongOrderPlaylistId, playlistIdSnapshot, StringComparison.Ordinal) ||
		    !AreIdListsEqual(_pendingSongOrderIds, idsSnapshot))
		{
			_songOrderAutoSaveTimer?.Stop();
			_songOrderAutoSaveTimer?.Start();
			return;
		}

		_pendingSongOrderIds = null;
		_pendingSongOrderPlaylistId = null;
		UpdateStatusBar("歌曲顺序已自动保存");
	}

	private bool TryHandleReorderShortcut(bool moveUp)
	{
		if (resultListView == null || !resultListView.ContainsFocus)
		{
			return false;
		}
		int delta = moveUp ? -1 : 1;
		if (CanReorderPlaylistListView())
		{
			MoveSelectedPlaylist(delta);
			return true;
		}
		if (CanReorderPlaylistSongsView())
		{
			MoveSelectedSongInPlaylist(delta);
			return true;
		}
		return false;
	}

	private void resultListView_ItemDrag(object sender, ItemDragEventArgs e)
	{
		if (resultListView == null || resultListView.VirtualMode || resultListView.SelectedIndices.Count == 0)
		{
			return;
		}
		if (CanReorderPlaylistListView())
		{
			if (TryEnsureUserPlaylistGrouping())
			{
				return;
			}
			_reorderDragMode = ReorderDragMode.PlaylistList;
			_reorderDragPlaylistId = null;
		}
		else if (CanReorderPlaylistSongsView())
		{
			_reorderDragMode = ReorderDragMode.PlaylistSongs;
			_reorderDragPlaylistId = _currentPlaylist?.Id;
		}
		else
		{
			return;
		}
		_reorderDragStartIndex = resultListView.SelectedIndices[0];
		if (_reorderDragStartIndex < 0)
		{
			return;
		}
		DoDragDrop(resultListView.SelectedItems[0], DragDropEffects.Move);
	}

	private void resultListView_DragEnter(object sender, DragEventArgs e)
	{
		if (_reorderDragMode == ReorderDragMode.None)
		{
			e.Effect = DragDropEffects.None;
			return;
		}
		e.Effect = DragDropEffects.Move;
	}

	private void resultListView_DragOver(object sender, DragEventArgs e)
	{
		if (resultListView == null || _reorderDragMode == ReorderDragMode.None)
		{
			e.Effect = DragDropEffects.None;
			return;
		}
		if (_reorderDragMode == ReorderDragMode.PlaylistList && !CanReorderPlaylistListView())
		{
			e.Effect = DragDropEffects.None;
			return;
		}
		if (_reorderDragMode == ReorderDragMode.PlaylistSongs)
		{
			if (!CanReorderPlaylistSongsView() || !string.Equals(_reorderDragPlaylistId, _currentPlaylist?.Id, StringComparison.OrdinalIgnoreCase))
			{
				e.Effect = DragDropEffects.None;
				return;
			}
		}
		Point clientPoint = resultListView.PointToClient(new Point(e.X, e.Y));
		if (!TryGetDragDropIndex(clientPoint, out int targetIndex))
		{
			e.Effect = DragDropEffects.None;
			return;
		}
		if (_reorderDragMode == ReorderDragMode.PlaylistList)
		{
			if (!IsPlaylistReorderTargetValid(_reorderDragStartIndex, targetIndex, out _))
			{
				e.Effect = DragDropEffects.None;
				return;
			}
		}
		e.Effect = DragDropEffects.Move;
		if (targetIndex >= 0 && targetIndex < resultListView.Items.Count)
		{
			resultListView.Items[targetIndex].Selected = true;
			resultListView.EnsureVisible(targetIndex);
		}
	}

	private void resultListView_DragLeave(object sender, EventArgs e)
	{
		_reorderDragMode = ReorderDragMode.None;
		_reorderDragStartIndex = -1;
		_reorderDragPlaylistId = null;
	}

	private void resultListView_DragDrop(object sender, DragEventArgs e)
	{
		if (resultListView == null || _reorderDragMode == ReorderDragMode.None)
		{
			return;
		}
		Point clientPoint = resultListView.PointToClient(new Point(e.X, e.Y));
		if (!TryGetDragDropIndex(clientPoint, out int targetIndex))
		{
			resultListView_DragLeave(sender, e);
			return;
		}
		int oldIndex = _reorderDragStartIndex;
		_reorderDragMode = ReorderDragMode.None;
		_reorderDragStartIndex = -1;
		_reorderDragPlaylistId = null;

		if (oldIndex < 0 || targetIndex < 0 || oldIndex == targetIndex)
		{
			return;
		}

		if (CanReorderPlaylistListView())
		{
			if (!IsPlaylistReorderTargetValid(oldIndex, targetIndex, out string? boundaryStatus))
			{
				if (!string.IsNullOrWhiteSpace(boundaryStatus))
				{
					UpdateStatusBar(boundaryStatus);
				}
				return;
			}
			if (!TryMoveItem(_currentPlaylists, oldIndex, targetIndex))
			{
				return;
			}
			DisplayPlaylists(_currentPlaylists, preserveSelection: false, _currentViewSource, "创建和收藏的歌单", announceHeader: false, suppressFocus: true);
			EnsureListSelectionWithoutFocus(targetIndex);
			UpdateStatusBar("歌单顺序已调整（自动保存中）");
			SchedulePlaylistOrderAutoSave();
			return;
		}

		if (CanReorderPlaylistSongsView())
		{
			if (!TryMoveItem(_currentSongs, oldIndex, targetIndex))
			{
				return;
			}
			DisplaySongs(_currentSongs, showPagination: false, hasNextPage: false, 1, preserveSelection: false, _currentViewSource, _currentPlaylist?.Name ?? "歌单", skipAvailabilityCheck: true, announceHeader: false, suppressFocus: true);
			EnsureListSelectionWithoutFocus(targetIndex);
			UpdateStatusBar("歌曲顺序已调整（自动保存中）");
			ScheduleSongOrderAutoSave();
		}
	}

	protected override void OnFormClosing(FormClosingEventArgs e)
	{
		_isFormClosing = true;
		_isApplicationExitRequested = true;
		PersistPlaybackState();
		StopInitialHomeLoadLoop("窗口关闭");
                _autoUpdateCheckCts?.Cancel();
                _autoUpdateCheckCts?.Dispose();
                _autoUpdateCheckCts = null;
                CancelPendingLyricSpeech();
                base.OnFormClosing(e);
                CompleteActivePlaybackSession(PlaybackEndReason.Stopped);
		try
		{
			ClearPlaybackPowerRequests();
			CancelActivePlaybackRequest("form closing");
			_availabilityCheckCts?.Cancel();
			_availabilityCheckCts?.Dispose();
			_availabilityCheckCts = null;
			_seekManager?.CancelPendingSeeks();
			_seekManager?.Dispose();
			_seekManager = null;
			_artistStatsRefreshCts?.Cancel();
			_artistStatsRefreshCts?.Dispose();
			_artistStatsRefreshCts = null;
			if (_scrubKeyTimer != null)
			{
				_scrubKeyTimer.Stop();
				_scrubKeyTimer.Dispose();
				_scrubKeyTimer = null;
			}
			if (_playlistOrderAutoSaveTimer != null)
			{
				_playlistOrderAutoSaveTimer.Stop();
				_playlistOrderAutoSaveTimer.Dispose();
				_playlistOrderAutoSaveTimer = null;
			}
			if (_songOrderAutoSaveTimer != null)
			{
				_songOrderAutoSaveTimer.Stop();
				_songOrderAutoSaveTimer.Dispose();
				_songOrderAutoSaveTimer = null;
			}
			if (_listViewLayoutDebounceTimer != null)
			{
				_listViewLayoutDebounceTimer.Stop();
				_listViewLayoutDebounceTimer.Dispose();
				_listViewLayoutDebounceTimer = null;
			}
                        if (_listViewSelectionDebounceTimer != null)
                        {
                                _listViewSelectionDebounceTimer.Stop();
                                _listViewSelectionDebounceTimer.Dispose();
                                _listViewSelectionDebounceTimer = null;
                        }
                        if (_listViewAccessibilityDebounceTimer != null)
                        {
                                _listViewAccessibilityDebounceTimer.Stop();
                                _listViewAccessibilityDebounceTimer.Dispose();
                                _listViewAccessibilityDebounceTimer = null;
                        }
#if DEBUG
                        if (_uiThreadWatchdogTimer != null)
                        {
                                _uiThreadWatchdogTimer.Dispose();
                                _uiThreadWatchdogTimer = null;
                        }
                        if (_accessibilityDiagnosticsTimer != null)
                        {
                                _accessibilityDiagnosticsTimer.Dispose();
                                _accessibilityDiagnosticsTimer = null;
                        }
#endif
                        StopStateUpdateLoop();
			_updateTimer?.Stop();
			_nextSongPreloader?.Dispose();
			BassAudioEngine? audioEngineToDispose = _audioEngine;
			if (audioEngineToDispose != null)
			{
				audioEngineToDispose.StateChanged -= OnAudioEngineStateChanged;
				audioEngineToDispose.BufferingStateChanged -= OnBufferingStateChanged;
				audioEngineToDispose.PositionChanged -= OnAudioPositionChanged;
				audioEngineToDispose.PlaybackStopped -= AudioEngine_PlaybackStopped;
				audioEngineToDispose.PlaybackEnded -= AudioEngine_PlaybackEnded;
				audioEngineToDispose.GaplessTransitionCompleted -= AudioEngine_GaplessTransitionCompleted;
			}
			_audioEngine = null;
			DisposeAudioEngineInBackground(audioEngineToDispose);
			_playbackReportingService?.Dispose();
			_playbackReportingService = null;
			_apiClient?.Dispose();
			_unblockService?.Dispose();
			try
			{
				DownloadManager.Instance?.Dispose();
			}
			catch (Exception ex)
			{
				Debug.WriteLine("[OnFormClosing] DownloadManager释放异常: " + ex.Message);
			}
			try
			{
				UploadManager instance = UploadManager.Instance;
				if (instance != null)
				{
					instance.TaskCompleted -= OnCloudUploadTaskCompleted;
					instance.TaskFailed -= OnCloudUploadTaskFailed;
					instance.Dispose();
				}
			}
			catch (Exception ex2)
			{
				Debug.WriteLine("[OnFormClosing] UploadManager释放异常: " + ex2.Message);
			}
			if (_trayIcon != null)
			{
				_trayIcon.Visible = false;
				_trayIcon.Dispose();
				_trayIcon = null;
			}
			if (_contextMenuHost != null)
			{
				try
				{
					_contextMenuHost.Dispose();
				}
				catch (Exception ex3)
				{
					Debug.WriteLine("[OnFormClosing] 释放菜单宿主窗口异常: " + ex3.Message);
				}
				_contextMenuHost = null;
			}
			SaveConfig();
		}
		catch (Exception ex4)
		{
			Debug.WriteLine("[OnFormClosing] 异常: " + ex4.Message);
		}
	}

	private void SafeInvoke(Action action)
	{
		if (base.InvokeRequired)
		{
			BeginInvoke(action);
		}
		else
		{
			action();
		}
	}

	private async void likeSongMenuItem_Click(object sender, EventArgs e)
	{
		if (!IsUserLoggedIn())
		{
			MessageBox.Show("请先登录后再收藏歌曲", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			return;
		}
		SongInfo song = GetSelectedSongFromContextMenu(sender);
		if (song == null)
		{
			ShowContextSongMissingMessage("收藏的歌曲");
		}
		else
		{
			if (!TryResolveSongIdForLibraryActions(song, "收藏歌曲", out var targetSongId))
			{
				return;
			}
			try
			{
				UpdateStatusBar("正在收藏歌曲...");
				if (await _apiClient.LikeSongAsync(targetSongId, like: true))
				{
					UpdateSongLikeState(song, isLiked: true);
					MessageBox.Show("已收藏歌曲：" + song.Name + " - " + song.Artist, "成功", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
					UpdateStatusBar("歌曲收藏成功");
				}
				else
				{
					MessageBox.Show("收藏歌曲失败，请检查网络或稍后重试。", "失败", MessageBoxButtons.OK, MessageBoxIcon.Hand);
					UpdateStatusBar("歌曲收藏失败");
				}
			}
			catch (Exception ex)
			{
				Exception ex2 = ex;
				MessageBox.Show("收藏歌曲失败: " + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("歌曲收藏失败");
			}
		}
	}

	private async void unlikeSongMenuItem_Click(object sender, EventArgs e)
	{
		await UnlikeSelectedSongAsync(sender);
	}

	private async Task<bool> UnlikeSelectedSongAsync(object? sender = null)
	{
		if (!IsUserLoggedIn())
		{
			MessageBox.Show("请先登录", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			return false;
		}
		SongInfo song = GetSelectedSongFromContextMenu(sender);
		if (song == null)
		{
			ShowContextSongMissingMessage("取消收藏的歌曲");
			return false;
		}
		if (!TryResolveSongIdForLibraryActions(song, "取消收藏歌曲", out var targetSongId))
		{
			return false;
		}
		try
		{
			UpdateStatusBar("正在取消收藏...");
			if (await _apiClient.LikeSongAsync(targetSongId, like: false))
			{
				UpdateSongLikeState(song, isLiked: false);
				MessageBox.Show("已取消收藏：" + song.Name + " - " + song.Artist, "成功", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				UpdateStatusBar("取消收藏成功");
				if (IsCurrentLikedSongsView())
				{
					try
					{
						await LoadUserLikedSongs(preserveSelection: true, skipSaveNavigation: true);
					}
					catch (Exception ex)
					{
						Exception ex2 = ex;
						Exception refreshEx = ex2;
						Debug.WriteLine($"[UI] 刷新我喜欢的音乐列表失败: {refreshEx}");
					}
				}
				return true;
			}
			MessageBox.Show("取消收藏失败，请检查网络或稍后重试。", "失败", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("取消收藏失败");
		}
		catch (Exception ex3)
		{
			MessageBox.Show("取消收藏失败: " + ex3.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("取消收藏失败");
		}
		return false;
	}

	private async void removeFromPlaylistMenuItem_Click(object sender, EventArgs e)
	{
		if (IsCurrentLikedSongsView())
		{
			await UnlikeSelectedSongAsync(sender);
			return;
		}
		if (!IsUserLoggedIn())
		{
			MessageBox.Show("请先登录", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			return;
		}
		string playlistId = _currentPlaylist?.Id;
		if (string.IsNullOrEmpty(playlistId) && _currentViewSource.StartsWith("playlist:"))
		{
			playlistId = _currentViewSource.Substring("playlist:".Length);
		}
		if (string.IsNullOrEmpty(playlistId))
		{
			MessageBox.Show("无法获取歌单信息", "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			return;
		}
		string playlistIdValue = playlistId;
		ListViewItem selectedItem = GetSelectedListViewItemSafe();
		if (selectedItem == null)
		{
			return;
		}
		SongInfo song = null;
		object tag = selectedItem.Tag;
		int index = 0;
		int num;
		if (tag is int)
		{
			index = (int)tag;
			if (index >= 0)
			{
				num = ((index < _currentSongs.Count) ? 1 : 0);
				goto IL_0215;
			}
		}
		num = 0;
		goto IL_0215;
		IL_0215:
		if (num != 0)
		{
			song = _currentSongs[index];
		}
		else
		{
			tag = selectedItem.Tag;
			if (tag is SongInfo songInfo)
			{
				song = songInfo;
			}
		}
		if (song == null || !TryResolveSongIdForLibraryActions(song, "从歌单中移除歌曲", out var targetSongId))
		{
			return;
		}
		try
		{
			UpdateStatusBar("正在从歌单中移除歌曲...");
			if (await _apiClient.RemoveTracksFromPlaylistAsync(playlistIdValue, new string[1] { targetSongId }))
			{
				MessageBox.Show("已将歌曲 \"" + song.Name + "\" 从歌单中移除", "成功", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				UpdateStatusBar("移除成功");
				if (_currentPlaylist != null)
				{
					try
					{
						await OpenPlaylist(_currentPlaylist, skipSave: true, preserveSelection: true);
						return;
					}
					catch (Exception value)
					{
						Debug.WriteLine($"[UI] 刷新歌单失败: {value}");
						return;
					}
				}
			}
			else
			{
				MessageBox.Show("从歌单中移除歌曲失败，请稍后重试。", "失败", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("移除失败");
			}
		}
		catch (Exception ex)
		{
			MessageBox.Show("从歌单中移除歌曲失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("移除失败");
		}
	}

	private async void addToPlaylistMenuItem_Click(object sender, EventArgs e)
	{
		if (!IsUserLoggedIn())
		{
			MessageBox.Show("请先登录后再添加歌曲到歌单", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			return;
		}
		SongInfo song = GetSelectedSongFromContextMenu(sender);
		if (song == null)
		{
			ShowContextSongMissingMessage("添加到歌单的歌曲");
		}
		else
		{
			if (!TryResolveSongIdForLibraryActions(song, "添加到歌单", out var targetSongId))
			{
				return;
			}
			try
			{
				UserInfo userInfo = await _apiClient.GetUserInfoAsync();
				if (userInfo == null)
				{
					MessageBox.Show("获取用户信息失败", "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
					return;
				}
				using AddToPlaylistDialog dialog = new AddToPlaylistDialog(userId: long.Parse(userInfo.UserId), apiClient: _apiClient, songId: targetSongId);
				if (dialog.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(dialog.SelectedPlaylistId))
				{
					UpdateStatusBar("正在添加歌曲到歌单...");
					string targetPlaylistId = dialog.SelectedPlaylistId;
					if (await _apiClient.AddTracksToPlaylistAsync(targetPlaylistId, new string[1] { targetSongId }))
					{
						MessageBox.Show("已将歌曲 \"" + song.Name + "\" 添加到歌单", "成功", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
						UpdateStatusBar("添加成功");
					}
					else
					{
						MessageBox.Show("添加歌曲到歌单失败，请稍后重试。", "失败", MessageBoxButtons.OK, MessageBoxIcon.Hand);
						UpdateStatusBar("添加失败");
					}
				}
			}
			catch (Exception ex)
			{
				Exception ex2 = ex;
				MessageBox.Show("添加歌曲到歌单失败: " + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("添加失败");
			}
		}
	}

	private bool TryResolveSongIdForLibraryActions(SongInfo? song, string actionDescription, out string resolvedSongId)
	{
		resolvedSongId = string.Empty;
		if (!CanSongUseLibraryFeatures(song))
		{
			if (song != null && song.IsCloudSong)
			{
				MessageBox.Show("当前云盘歌曲尚未匹配到网易云曲库，无法执行“" + actionDescription + "”。", "暂不支持", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			}
			else
			{
				MessageBox.Show("无法执行“" + actionDescription + "”，歌曲缺少必要的ID信息。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			}
			return false;
		}
		if (song == null)
		{
			return false;
		}
		resolvedSongId = ((song.IsCloudSong && !string.IsNullOrWhiteSpace(song.CloudMatchedSongId)) ? song.CloudMatchedSongId : song.Id);
		return !string.IsNullOrWhiteSpace(resolvedSongId);
	}

	private string? ResolveSongIdForLibraryState(SongInfo? song)
	{
		if (!CanSongUseLibraryFeatures(song) || song == null)
		{
			return null;
		}
		return (song.IsCloudSong && !string.IsNullOrWhiteSpace(song.CloudMatchedSongId)) ? song.CloudMatchedSongId : song.Id;
	}

	private static bool CanSongUseLibraryFeatures(SongInfo? song)
	{
		if (song == null)
		{
			return false;
		}
		if (!song.IsCloudSong)
		{
			return !string.IsNullOrWhiteSpace(song.Id);
		}
		if (!string.IsNullOrWhiteSpace(song.CloudMatchedSongId))
		{
			return true;
		}
		if (string.IsNullOrWhiteSpace(song.Id) || string.IsNullOrWhiteSpace(song.CloudSongId))
		{
			return false;
		}
		return !string.Equals(song.Id, song.CloudSongId, StringComparison.Ordinal);
	}


	private void InitializeRecognitionFeature()
	{
		try
		{
			_songRecognitionCoordinator = new SongRecognitionCoordinator(_apiClient, new AudioCaptureService(), new AudioFingerprintGenerator());
			AddRecognitionMenuEntry();
		}
		catch (Exception ex)
		{
			Debug.WriteLine("[Recognition] 初始化失败: " + ex.Message);
		}
	}

	private void AddRecognitionMenuEntry()
	{
		checked
		{
			if (fileMenuItem != null && (_listenRecognitionMenuItem == null || !fileMenuItem.DropDownItems.Contains(_listenRecognitionMenuItem)))
			{
                                _listenRecognitionMenuItem = new ToolStripMenuItem
                                {
                                        Text = "听歌识曲(&L)",
                                        Name = "listenRecognitionMenuItem",
                                        ShortcutKeys = (Keys.L | Keys.Control)
                                };
				_listenRecognitionMenuItem.Click += async delegate
				{
					await StartSongRecognitionAsync();
				};
				int num = fileMenuItem.DropDownItems.IndexOf(currentPlayingMenuItem);
				if (num < 0)
				{
					num = fileMenuItem.DropDownItems.IndexOf(loginMenuItem);
				}
				num = ((num < 0) ? Math.Max(0, fileMenuItem.DropDownItems.IndexOf(refreshMenuItem) + 1) : (num + 1));
				fileMenuItem.DropDownItems.Insert(num, _listenRecognitionMenuItem);
				if (downloadManagerMenuItem != null && fileMenuItem.DropDownItems.Contains(downloadManagerMenuItem))
				{
					fileMenuItem.DropDownItems.Remove(downloadManagerMenuItem);
					int val = fileMenuItem.DropDownItems.IndexOf(_listenRecognitionMenuItem) + 1;
					fileMenuItem.DropDownItems.Insert(Math.Max(val, 0), downloadManagerMenuItem);
				}
			}
		}
	}

	private async Task StartSongRecognitionAsync()
	{
		if (_songRecognitionCoordinator == null)
		{
			MessageBox.Show(this, "识曲服务初始化失败，请重启应用。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			return;
		}
		IReadOnlyList<AudioInputDeviceInfo> devices;
		try
		{
			devices = _songRecognitionCoordinator.EnumerateDevices();
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Exception ex3 = ex2;
			MessageBox.Show(this, "枚举录音设备失败：" + ex3.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			return;
		}
		if (devices == null || devices.Count == 0)
		{
			MessageBox.Show(this, "未找到可用的录音设备。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			return;
		}
		SongRecognitionDialog dialog = new SongRecognitionDialog(devices, _config?.RecognitionInputDeviceId);
		if (dialog.ShowDialog(this) != DialogResult.OK)
		{
			return;
		}
		AudioInputDeviceInfo selectedDevice = dialog.SelectedDevice ?? devices.First();
		int durationSeconds = 6;
		if (_config != null)
		{
			_config.RecognitionInputDeviceId = selectedDevice.DeviceId;
			_configManager.Save(_config);
		}
		UpdateStatusBar("正在聆听环境音...");
		using CancellationTokenSource cts = new CancellationTokenSource();
		SongRecognitionResult result;
		try
		{
			result = await _songRecognitionCoordinator.RecognizeAsync(selectedDevice, durationSeconds, cts.Token).ConfigureAwait(continueOnCapturedContext: true);
		}
		catch (NotSupportedException ex4)
		{
			NotSupportedException ex5 = ex4;
			NotSupportedException nse = ex5;
			MessageBox.Show(this, "指纹生成器尚未就绪：" + nse.Message + "\n请按设计文档接入 ncm-afp 库后重试。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			UpdateStatusBar("听歌识曲组件未就绪");
			return;
		}
		catch (OperationCanceledException)
		{
			UpdateStatusBar("听歌识曲已取消");
			return;
		}
		catch (Exception ex)
		{
			Exception ex7 = ex;
			Exception ex8 = ex7;
			Debug.WriteLine($"[Recognition] 识曲失败: {ex8}");
			MessageBox.Show(this, "听歌识曲失败：" + ex8.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("听歌识曲失败");
			return;
		}
		if (result == null || result.Matches.Count == 0)
		{
			UpdateStatusBar("未能识别出歌曲");
			MessageBox.Show(this, "未能识别出歌曲，建议更长时间或更靠近音源重试。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			return;
		}
		await ShowRecognitionResultsAsync(result).ConfigureAwait(continueOnCapturedContext: true);
		if ((_config?.RecognitionAutoCloseDialog ?? false) && _listenRecognitionMenuItem != null)
		{
		}
	}

	private async Task ShowRecognitionResultsAsync(SongRecognitionResult result)
	{
		List<SongInfo> newSongs = result.Matches?.ToList() ?? new List<SongInfo>();
		bool isListeningView = !string.IsNullOrWhiteSpace(_currentViewSource) && _currentViewSource.StartsWith("listen-match", StringComparison.OrdinalIgnoreCase) && _currentSongs != null && _currentSongs.Count > 0;
		if (!isListeningView)
		{
			SaveNavigationState();
		}
		List<SongInfo> aggregateExisting = _listenMatchAggregate ?? new List<SongInfo>();
		int aggregateAdded;
		List<SongInfo> mergedAggregate = MergeRecognitionSongs(newSongs, aggregateExisting, out aggregateAdded);
		_listenMatchAggregate = CloneList(mergedAggregate);
		if (!string.IsNullOrWhiteSpace(result.SessionId))
		{
			_lastListenMatchSessionId = result.SessionId;
		}
		string viewSource = NormalizeListenMatchViewSource("listen-match");
		foreach (SongInfo song in mergedAggregate)
		{
			if (song != null && (string.IsNullOrWhiteSpace(song.ViewSource) || song.ViewSource.StartsWith("listen-match", StringComparison.OrdinalIgnoreCase)))
			{
				song.ViewSource = viewSource;
			}
		}
		_listenMatchCache["listen-match"] = CloneList(mergedAggregate);
		EnsureSearchTypeSelection("歌曲");
		_currentSearchType = "歌曲";
		_lastKeyword = "听歌识曲";
		_currentPage = 1;
		_maxPage = 1;
		_hasNextSearchPage = false;
		_isHomePage = false;
		ViewLoadRequest request = new ViewLoadRequest(viewSource, "听歌识曲", isListeningView ? "正在更新听歌识曲结果..." : "正在载入听歌识曲结果...", cancelActiveNavigation: true, -1);
		if ((await RunViewLoadAsync(request, (CancellationToken _) => Task.FromResult(mergedAggregate), "听歌识曲已取消").ConfigureAwait(continueOnCapturedContext: true)).IsCanceled)
		{
			return;
		}
		_currentSongs = mergedAggregate;
		_currentPlaylist = null;
		_currentMixedQueryKey = null;
		await ExecuteOnUiThreadAsync(delegate
		{
			DisplaySongs(_currentSongs, showPagination: false, hasNextPage: false, 1, preserveSelection: false, viewSource, "听歌识曲");
			string message = ((mergedAggregate.Count <= 0) ? "未能识别出歌曲" : (isListeningView ? $"识别到 {aggregateAdded} 首新歌曲，累计 {mergedAggregate.Count} 首" : $"识别到 {mergedAggregate.Count} 首歌曲"));
			UpdateStatusBar(message);
			AnnounceListViewHeaderIfNeeded("听歌识曲");
			if (!IsListAutoFocusSuppressed && resultListView != null && resultListView.Items.Count > 0 && !resultListView.Focused && resultListView.CanFocus)
			{
				int val = ((resultListView.SelectedIndices.Count > 0) ? resultListView.SelectedIndices[0] : 0);
				RestoreListViewFocus(Math.Max(0, Math.Min(val, checked(resultListView.Items.Count - 1))));
			}
		}).ConfigureAwait(continueOnCapturedContext: true);
	}

	private async Task LoadListenMatchAsync(string viewSource, bool skipSave = false)
	{
		string normalized = NormalizeListenMatchViewSource(viewSource);
		ViewLoadRequest request = new ViewLoadRequest(normalized, "听歌识曲", "正在载入听歌识曲结果...", !skipSave, -1);
		List<SongInfo> cached;
		List<SongInfo> songs = (_listenMatchCache.TryGetValue("listen-match", out cached) ? CloneList(cached) : CloneList(_listenMatchAggregate));
		if ((await RunViewLoadAsync(request, (CancellationToken _) => Task.FromResult(songs), "听歌识曲已取消").ConfigureAwait(continueOnCapturedContext: true)).IsCanceled)
		{
			return;
		}
		_currentSongs = songs;
		_currentPlaylist = null;
		_currentMixedQueryKey = null;
		await ExecuteOnUiThreadAsync(delegate
		{
			DisplaySongs(_currentSongs, showPagination: false, hasNextPage: false, 1, preserveSelection: false, viewSource, "听歌识曲");
			string message = ((_currentSongs.Count <= 0) ? "未能识别出歌曲" : $"听歌识曲结果：{_currentSongs.Count} 首");
			UpdateStatusBar(message);
			AnnounceListViewHeaderIfNeeded("听歌识曲");
			if (!IsListAutoFocusSuppressed && resultListView != null && resultListView.Items.Count > 0 && !resultListView.Focused && resultListView.CanFocus)
			{
				int val = ((resultListView.SelectedIndices.Count > 0) ? resultListView.SelectedIndices[0] : 0);
				RestoreListViewFocus(Math.Max(0, Math.Min(val, checked(resultListView.Items.Count - 1))));
			}
		}).ConfigureAwait(continueOnCapturedContext: true);
	}

	private static List<SongInfo> MergeRecognitionSongs(IEnumerable<SongInfo> newSongs, IEnumerable<SongInfo> existingSongs, out int addedCount)
	{
		List<SongInfo> merged = new List<SongInfo>();
		HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (SongInfo item2 in newSongs ?? Array.Empty<SongInfo>())
		{
			AddIfNew(item2);
		}
		int count = merged.Count;
		foreach (SongInfo item3 in existingSongs ?? Array.Empty<SongInfo>())
		{
			AddIfNew(item3);
		}
		addedCount = count;
		return merged;
		void AddIfNew(SongInfo s)
		{
			if (s != null)
			{
				string item = BuildKey(s);
				if (seen.Add(item))
				{
					merged.Add(s);
				}
			}
		}
		static string BuildKey(SongInfo song)
		{
			if (!string.IsNullOrWhiteSpace(song.Id))
			{
				return "id:" + song.Id;
			}
			string text = song.Name ?? string.Empty;
			string text2 = song.Artist ?? string.Empty;
			return "na:" + text + "|ar:" + text2;
		}
	}

	private CancellationToken BeginViewContentOperation(bool cancelNavigation = true)
	{
		CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
		CancellationTokenSource viewContentCts;
		lock (_viewContentLock)
		{
			viewContentCts = _viewContentCts;
			_viewContentCts = cancellationTokenSource;
		}
		try
		{
			viewContentCts?.Cancel();
		}
		catch (ObjectDisposedException)
		{
		}
		finally
		{
			viewContentCts?.Dispose();
		}
		if (cancelNavigation)
		{
			CancelNavigationOperation();
		}
		return cancellationTokenSource.Token;
	}

	private string NormalizeListenMatchViewSource(string viewSource)
	{
		if (string.IsNullOrWhiteSpace(viewSource))
		{
			return "listen-match";
		}
		if (viewSource.StartsWith("listen-match", StringComparison.OrdinalIgnoreCase))
		{
			return "listen-match";
		}
		return viewSource;
	}

	private CancellationToken BeginNavigationOperation()
	{
		CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
		CancellationTokenSource cancellationTokenSource2 = Interlocked.Exchange(ref _navigationCts, cancellationTokenSource);
		try
		{
			cancellationTokenSource2?.Cancel();
		}
		catch (ObjectDisposedException)
		{
		}
		finally
		{
			cancellationTokenSource2?.Dispose();
		}
		return cancellationTokenSource.Token;
	}

	private void CancelViewContentOperation()
	{
		CancellationTokenSource viewContentCts;
		lock (_viewContentLock)
		{
			viewContentCts = _viewContentCts;
			_viewContentCts = null;
		}
		if (viewContentCts == null)
		{
			return;
		}
		try
		{
			viewContentCts.Cancel();
		}
		catch (ObjectDisposedException)
		{
		}
		finally
		{
			viewContentCts.Dispose();
		}
	}

	private void CancelNavigationOperation()
	{
		CancellationTokenSource cancellationTokenSource = Interlocked.Exchange(ref _navigationCts, null);
		if (cancellationTokenSource == null)
		{
			return;
		}
		try
		{
			cancellationTokenSource.Cancel();
		}
		catch (ObjectDisposedException)
		{
		}
		finally
		{
			cancellationTokenSource.Dispose();
		}
	}

	private CancellationToken GetNavigationToken()
	{
		return _navigationCts?.Token ?? CancellationToken.None;
	}

	private CancellationToken GetCurrentViewContentToken()
	{
		lock (_viewContentLock)
		{
			return _viewContentCts?.Token ?? CancellationToken.None;
		}
	}

	private bool ShouldAbortViewRender(CancellationToken token, string context)
	{
		if (!token.CanBeCanceled)
		{
			return false;
		}
		if (!token.IsCancellationRequested)
		{
			return false;
		}
		Debug.WriteLine("[View] 中止 " + context + " 渲染，视图已切换。");
		return true;
	}

	private void ShowLoadingPlaceholderCore(string? viewSource, string? accessibleName, string loadingText)
	{
		MarkListViewLayoutDataChanged();
		UpdateStatusBar(loadingText);
		DisableVirtualSongList();
		_listLoadingPlaceholderActive = true;
		resultListView.BeginUpdate();
		try
		{
			ResetListViewSelectionState();
			resultListView.Items.Clear();
			ListViewItem value = new ListViewItem(new string[6]
			{
				string.Empty,
				string.Empty,
				ListLoadingPlaceholderText,
				string.Empty,
				string.Empty,
				string.Empty
			})
			{
				Tag = null
			};
			SetListViewItemPrimaryText(value, ListLoadingPlaceholderText);
			resultListView.Items.Add(value);
		}
		finally
		{
			EndListViewUpdateAndRefreshAccessibility();
		}
        ApplyListViewContext(viewSource, accessibleName, "列表", announceHeader: true);
	}


	private void ShowListErrorRowCore(string? viewSource, string? accessibleName, string message)
	{
		MarkListViewLayoutDataChanged();
		DisableVirtualSongList();
		_listLoadingPlaceholderActive = false;
		resultListView.BeginUpdate();
		try
		{
			ResetListViewSelectionState();
			resultListView.Items.Clear();
			ListViewItem value = new ListViewItem(new string[6]
			{
				string.Empty,
				message,
				string.Empty,
				string.Empty,
				string.Empty,
				string.Empty
			})
			{
				Tag = -4
			};
			SetListViewItemPrimaryText(value, message);
			resultListView.Items.Add(value);
		}
		finally
		{
			EndListViewUpdateAndRefreshAccessibility();
		}
		ApplyListViewContext(viewSource, accessibleName, "列表", announceHeader: true);
		ApplyStandardListViewSelection(0, allowSelection: true, suppressFocus: false);
	}

	private void ShowListRetryPlaceholderCore(string? viewSource, string? accessibleName, string fallbackName, bool announceHeader, bool suppressFocus = false)
	{
		MarkListViewLayoutDataChanged();
		DisableVirtualSongList();
		_listLoadingPlaceholderActive = false;
		resultListView.BeginUpdate();
		try
		{
			ResetListViewSelectionState();
			resultListView.Items.Clear();
			ListViewItem value = new ListViewItem(new string[6]
			{
				string.Empty,
				string.Empty,
				ListRetryPlaceholderText,
				string.Empty,
				string.Empty,
				string.Empty
			})
			{
				Tag = ListRetryPlaceholderTag
			};
			SetListViewItemPrimaryText(value, ListRetryPlaceholderText);
			resultListView.Items.Add(value);
		}
		finally
		{
			EndListViewUpdateAndRefreshAccessibility();
		}
		ApplyListViewContext(viewSource, accessibleName, fallbackName, announceHeader);
		EnsureListSelectionWithoutFocus(0);
	}


	private void ShowListRetryPlaceholderIfEmpty(string? viewSource, string? accessibleName, string fallbackName, bool announceHeader = true)
	{
		if (resultListView == null)
		{
			return;
		}
		if (resultListView.Items.Count > 0)
		{
			if (resultListView.Items.Count != 1)
			{
				return;
			}
			ListViewItem item = resultListView.Items[0];
			if (!IsListViewLoadingPlaceholderItem(item))
			{
				return;
			}
		}
		ShowListRetryPlaceholderCore(viewSource, accessibleName, fallbackName, announceHeader, suppressFocus: IsSearchViewSource(viewSource));
	}

	private void ShowListErrorRow(string? viewSource, string? accessibleName, string message)
	{
		SafeInvoke(delegate
		{
			ShowListErrorRowCore(viewSource, accessibleName, message);
		});
	}

	private void ResetCurrentViewDataForLoading(string? nextViewSource = null)
	{
		CapturePersonalFmSnapshotOnViewLeave(nextViewSource);
		_currentSongs.Clear();
		_currentPlaylists.Clear();
		_currentAlbums.Clear();
		_currentArtists.Clear();
		_currentListItems.Clear();
		_currentPodcasts.Clear();
		_currentPodcastSounds.Clear();
		_currentPlaylist = null;
		_currentPlaylistOwnedByUser = false;
		_currentPodcast = null;
	}

	private void ShowLoadingPlaceholder(string? viewSource, string? accessibleName, string loadingText)
	{
		SafeInvoke(delegate
		{
			ShowLoadingPlaceholderCore(viewSource, accessibleName, loadingText);
		});
	}

        private Task ShowLoadingStateAsync(ViewLoadRequest request)
        {
                return ExecuteOnUiThreadAsync(delegate
                {
                        bool preserveExistingFocus = false;
                        if (request.PendingFocusIndex == 0 && !string.IsNullOrWhiteSpace(request.ViewSource))
                        {
                                preserveExistingFocus = _pendingListFocusIndex >= 0 && !string.IsNullOrWhiteSpace(_pendingListFocusViewSource) && string.Equals(_pendingListFocusViewSource, request.ViewSource, StringComparison.OrdinalIgnoreCase);
                        }
                        if (!preserveExistingFocus)
                        {
                                RequestListFocus(request.ViewSource, request.PendingFocusIndex);
                        }
			ResetCurrentViewDataForLoading(request.ViewSource);
                        ShowLoadingPlaceholderCore(request.ViewSource, request.AccessibleName, request.LoadingText);
					if (resultListView != null && resultListView.Items.Count != 0)
					{
						int targetIndex = ((request.PendingFocusIndex >= 0) ? Math.Max(0, Math.Min(request.PendingFocusIndex, checked(resultListView.Items.Count - 1))) : 0);
						EnsureListSelectionWithoutFocus(targetIndex);
						if (!request.SuppressLoadingFocus && !IsListAutoFocusSuppressed && resultListView.CanFocus)
						{
							resultListView.Focus();
						}
					}
		});
	}

        private void RequestListFocus(string? viewSource, int pendingIndex, bool fromPlayback = false)
        {
                if (fromPlayback && !IsFocusFollowPlaybackEnabled())
                {
                        ClearPlaybackFollowPendingState();
                        return;
                }

                if (string.IsNullOrWhiteSpace(viewSource) || pendingIndex < 0)
                {
                        _pendingListFocusViewSource = null;
                        _pendingListFocusIndex = -1;
                        _pendingListFocusFromPlayback = false;
                }
                else
                {
                        _pendingListFocusViewSource = viewSource;
                        _pendingListFocusIndex = pendingIndex;
                        _pendingListFocusFromPlayback = fromPlayback;
                }
        }

	private int ResolvePendingListFocusIndex(int fallbackIndex)
	{
		if (_pendingSongFocusSatisfied && !string.IsNullOrWhiteSpace(_pendingSongFocusSatisfiedViewSource) && IsSameViewSourceForFocus(_pendingSongFocusSatisfiedViewSource, _currentViewSource))
		{
			return fallbackIndex;
		}
                if (_pendingListFocusIndex >= 0 && !string.IsNullOrWhiteSpace(_pendingListFocusViewSource) && IsSameViewSourceForFocus(_pendingListFocusViewSource, _currentViewSource))
                {
                        int pendingListFocusIndex = _pendingListFocusIndex;
                        bool pendingFromPlayback = _pendingListFocusFromPlayback;
                        _pendingListFocusIndex = -1;
                        _pendingListFocusViewSource = null;
                        _pendingListFocusFromPlayback = false;
                        if (pendingFromPlayback && CanApplyPendingSongFocusForView(_currentViewSource))
                        {
                                _pendingSongFocusId = null;
                                _pendingSongFocusViewSource = null;
                                _pendingSongFocusSatisfied = true;
                                _pendingSongFocusSatisfiedViewSource = _currentViewSource;
                        }
                        return pendingListFocusIndex;
                }
                return fallbackIndex;
        }

        private void ResetPendingListFocusIfViewChanged(string? viewSource)
        {
                if (!string.IsNullOrWhiteSpace(viewSource) && !string.Equals(_pendingListFocusViewSource, viewSource, StringComparison.OrdinalIgnoreCase))
                {
                        _pendingListFocusIndex = -1;
                        _pendingListFocusViewSource = viewSource;
                        _pendingListFocusFromPlayback = false;
                }
        }

	private CancellationToken LinkCancellationTokens(CancellationToken viewToken, CancellationToken externalToken, out CancellationTokenSource? linkedCts)
	{
		linkedCts = null;
		if (!externalToken.CanBeCanceled)
		{
			return viewToken;
		}
		if (!viewToken.CanBeCanceled)
		{
			return externalToken;
		}
		linkedCts = CancellationTokenSource.CreateLinkedTokenSource(viewToken, externalToken);
		return linkedCts.Token;
	}

	private CancellationTokenSource BeginSearchOperation()
	{
		CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
		CancellationTokenSource cancellationTokenSource2 = Interlocked.Exchange(ref _searchCts, cancellationTokenSource);
		try
		{
			cancellationTokenSource2?.Cancel();
		}
		catch (ObjectDisposedException)
		{
		}
		finally
		{
			cancellationTokenSource2?.Dispose();
		}
		return cancellationTokenSource;
	}

	private bool TryHandleOperationCancelled(Exception ex, string statusText)
	{
		if (ex is OperationCanceledException || ex is TaskCanceledException)
		{
			UpdateStatusBar(statusText);
			return true;
		}
		if (ex.InnerException is OperationCanceledException || ex.InnerException is TaskCanceledException)
		{
			UpdateStatusBar(statusText);
			return true;
		}
		return false;
	}

	private string ResolveRetryPlaceholderFallbackName(ViewLoadRequest request)
	{
		if (request == null)
		{
			return "列表";
		}
		if (!string.IsNullOrWhiteSpace(request.AccessibleName))
		{
			return request.AccessibleName;
		}
		string viewSource = request.ViewSource ?? string.Empty;
		if (viewSource.StartsWith("search:", StringComparison.OrdinalIgnoreCase))
		{
			return "搜索结果";
		}
		return "列表";
	}

	private async Task PromoteLoadingPlaceholderToRetryOnFailureAsync(ViewLoadRequest request)
	{
		if (request == null || IsDisposed)
		{
			return;
		}
		try
		{
			await ExecuteOnUiThreadAsync(delegate
			{
				if (IsDisposed || resultListView == null)
				{
					return;
				}
				string fallbackName = ResolveRetryPlaceholderFallbackName(request);
				ShowListRetryPlaceholderIfEmpty(request.ViewSource, request.AccessibleName, fallbackName, announceHeader: true);
			}).ConfigureAwait(continueOnCapturedContext: true);
		}
		catch (Exception ex)
		{
			Debug.WriteLine("[ViewLoad] 升级重试占位符失败: " + ex.Message);
		}
	}

	private async Task<ViewLoadResult<T>> RunViewLoadAsync<T>(ViewLoadRequest request, Func<CancellationToken, Task<T>> loader, string cancellationStatusText)
	{
		if (loader == null)
		{
			throw new ArgumentNullException("loader");
		}
		CancellationToken viewToken = BeginViewContentOperation(request.CancelActiveNavigation);
		CancellationToken navigationToken = BeginNavigationOperation();
		CancellationTokenSource linkedCts = null;
		CancellationToken effectiveToken = LinkCancellationTokens(viewToken, navigationToken, out linkedCts);
		await ShowLoadingStateAsync(request).ConfigureAwait(continueOnCapturedContext: true);
		await Task.Yield();
		try
		{
			T result = await Task.Run(async () => await loader(effectiveToken).ConfigureAwait(continueOnCapturedContext: false), effectiveToken).ConfigureAwait(continueOnCapturedContext: true);
			if (effectiveToken.IsCancellationRequested)
			{
				SafeInvoke(delegate
				{
					UpdateStatusBar(cancellationStatusText);
				});
				return new ViewLoadResult<T>(isCanceled: true, default(T));
			}
			return new ViewLoadResult<T>(isCanceled: false, result);
		}
		catch (OperationCanceledException)
		{
			SafeInvoke(delegate
			{
				UpdateStatusBar(cancellationStatusText);
			});
			return new ViewLoadResult<T>(isCanceled: true, default(T));
		}
		catch (Exception ex2) when (TryHandleOperationCancelled(ex2, cancellationStatusText))
		{
			return new ViewLoadResult<T>(isCanceled: true, default(T));
		}
		catch
		{
			await PromoteLoadingPlaceholderToRetryOnFailureAsync(request).ConfigureAwait(continueOnCapturedContext: true);
			throw;
		}
		finally
		{
			linkedCts?.Dispose();
		}
	}

	private Task ExecuteOnUiThreadAsync(Action action)
	{
		if (!base.InvokeRequired)
		{
			action();
			return Task.CompletedTask;
		}
		TaskCompletionSource<object?> tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
		BeginInvoke(delegate
		{
			try
			{
				action();
				tcs.SetResult(null);
			}
			catch (Exception exception)
			{
				tcs.SetException(exception);
			}
		});
		return tcs.Task;
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing && components != null)
		{
			components.Dispose();
		}
		base.Dispose(disposing);
	}

	private void InitializeComponent()
	{
		this.menuStrip1 = new System.Windows.Forms.MenuStrip();
		this.fileMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.homeMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.loginMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.currentPlayingMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.viewSourceMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.refreshMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.toolStripSeparatorDownload1 = new System.Windows.Forms.ToolStripSeparator();
		this.openDownloadDirMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.changeDownloadDirMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.downloadManagerMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.toolStripSeparatorDownload2 = new System.Windows.Forms.ToolStripSeparator();
		this.exitMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.playControlMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.playPauseMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.toolStripSeparator1 = new System.Windows.Forms.ToolStripSeparator();
		this.playbackMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.sequentialMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.loopMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.loopOneMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.randomMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.qualityMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.standardQualityMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.highQualityMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.losslessQualityMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.hiresQualityMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.surroundHDQualityMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.dolbyQualityMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.masterQualityMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.outputDeviceMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.prevMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.nextMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.jumpToPositionMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.autoReadLyricsMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.focusFollowPlaybackMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.preventSleepDuringPlaybackMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.hideSequenceMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.hideControlBarMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.themeMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.themeGrassFreshMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.themeGrassSoftMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.themeGrassWarmMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.themeGrassMutedMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.helpMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.checkUpdateMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.donateMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.shortcutsMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.aboutMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.searchPanel = new System.Windows.Forms.Panel();
		this.searchTypeComboBox = new System.Windows.Forms.ComboBox();
		this.searchTypeLabel = new System.Windows.Forms.Label();
		this.searchButton = new System.Windows.Forms.Button();
                this.backButton = new System.Windows.Forms.Button();
          this.searchTextBox = new SafeTextBox();
		this.searchTextContextMenu = new System.Windows.Forms.ContextMenuStrip();
		this.searchCopyMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.searchCutMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.searchPasteMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.searchHistorySeparator = new System.Windows.Forms.ToolStripSeparator();
                this.searchClearHistoryMenuItem = new System.Windows.Forms.ToolStripMenuItem();
                this.searchLabel = new System.Windows.Forms.Label();
                this.resultListPanel = new System.Windows.Forms.Panel();
                this.resultListView = new YTPlayer.SafeListView();
		this.columnHeader0 = new System.Windows.Forms.ColumnHeader();
		this.columnHeader1 = new System.Windows.Forms.ColumnHeader();
		this.columnHeader2 = new System.Windows.Forms.ColumnHeader();
		this.columnHeader3 = new System.Windows.Forms.ColumnHeader();
		this.columnHeader4 = new System.Windows.Forms.ColumnHeader();
		this.columnHeader5 = new System.Windows.Forms.ColumnHeader();
                this.controlPanel = new YTPlayer.MainForm.AccessibleGroupPanel();
                this.lyricsLabel = new System.Windows.Forms.Label();
		this.songContextMenu = new System.Windows.Forms.ContextMenuStrip();
		this.insertPlayMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.likeSongMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.unlikeSongMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.addToPlaylistMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.removeFromPlaylistMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.cloudMenuSeparator = new System.Windows.Forms.ToolStripSeparator();
		this.uploadToCloudMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.deleteFromCloudMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.toolStripSeparatorCollection = new System.Windows.Forms.ToolStripSeparator();
		this.subscribePlaylistMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.unsubscribePlaylistMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.deletePlaylistMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.createPlaylistMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.subscribeSongAlbumMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.subscribeSongArtistMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.subscribeAlbumMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.unsubscribeAlbumMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.subscribePodcastMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.unsubscribePodcastMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.toolStripSeparatorView = new System.Windows.Forms.ToolStripSeparator();
		this.viewSongArtistMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.viewSongAlbumMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.viewPodcastMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.shareSongMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.shareSongWebMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.shareSongDirectMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.sharePlaylistMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.shareAlbumMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.sharePodcastMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.sharePodcastEpisodeMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.sharePodcastEpisodeWebMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.sharePodcastEpisodeDirectMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.viewPodcastMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.artistSongsSortMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.artistSongsSortHotMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.artistSongsSortTimeMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.artistAlbumsSortMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.artistAlbumsSortLatestMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.artistAlbumsSortOldestMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.podcastSortMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.podcastSortLatestMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.podcastSortSerialMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.commentMenuSeparator = new System.Windows.Forms.ToolStripSeparator();
		this.commentMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.toolStripSeparatorArtist = new System.Windows.Forms.ToolStripSeparator();
		this.shareArtistMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.subscribeArtistMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.unsubscribeArtistMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.toolStripSeparatorDownload3 = new System.Windows.Forms.ToolStripSeparator();
		this.downloadSongMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.downloadLyricsMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.downloadPlaylistMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.downloadAlbumMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.downloadPodcastMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.batchDownloadMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.downloadCategoryMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.batchDownloadPlaylistsMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.trayContextMenu = new System.Windows.Forms.ContextMenuStrip();
		this.trayShowMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.trayPlayPauseMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.trayPrevMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.trayNextMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.trayMenuSeparator = new System.Windows.Forms.ToolStripSeparator();
		this.trayExitMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.volumeLabel = new System.Windows.Forms.Label();
        this.volumeTrackBar = new YTPlayer.AccessibleTrackBar();
		this.timeLabel = new System.Windows.Forms.Label();
        this.progressTrackBar = new YTPlayer.AccessibleTrackBar();
		this.playPauseButton = new System.Windows.Forms.Button();
		this.currentSongLabel = new System.Windows.Forms.Label();
        this.statusStrip1 = new System.Windows.Forms.StatusStrip();
		this.toolStripStatusLabel1 = new System.Windows.Forms.ToolStripStatusLabel();
		this.menuStrip1.SuspendLayout();
		this.searchPanel.SuspendLayout();
		this.searchTextContextMenu.SuspendLayout();
		this.resultListPanel.SuspendLayout();
		this.controlPanel.SuspendLayout();
		((System.ComponentModel.ISupportInitialize)this.volumeTrackBar).BeginInit();
		((System.ComponentModel.ISupportInitialize)this.progressTrackBar).BeginInit();
		this.statusStrip1.SuspendLayout();
		this.songContextMenu.SuspendLayout();
		this.trayContextMenu.SuspendLayout();
		base.SuspendLayout();
		this.menuStrip1.ImageScalingSize = new System.Drawing.Size(20, 20);
		this.menuStrip1.Items.AddRange(this.fileMenuItem, this.playControlMenuItem, this.helpMenuItem);
		this.menuStrip1.Location = new System.Drawing.Point(0, 0);
		this.menuStrip1.Name = "menuStrip1";
		this.menuStrip1.Size = new System.Drawing.Size(1200, 28);
		this.menuStrip1.TabIndex = 0;
		this.menuStrip1.Text = "menuStrip1";
		this.hideMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.hideMenuItem.Name = "hideMenuItem";
		this.hideMenuItem.Size = new System.Drawing.Size(178, 26);
		this.hideMenuItem.Text = "隐藏";
		this.hideMenuItem.ShortcutKeyDisplayString = "Shift+Esc";
		this.hideMenuItem.Click += new System.EventHandler(hideMenuItem_Click);
		this.fileMenuItem.DropDownItems.AddRange(this.homeMenuItem, this.loginMenuItem, this.currentPlayingMenuItem, this.toolStripSeparatorDownload1, this.openDownloadDirMenuItem, this.changeDownloadDirMenuItem, this.downloadManagerMenuItem, this.refreshMenuItem, this.toolStripSeparatorDownload2, this.hideMenuItem, this.exitMenuItem);
		this.fileMenuItem.Name = "fileMenuItem";
		this.fileMenuItem.Size = new System.Drawing.Size(98, 24);
		this.fileMenuItem.Text = "文件/操作(&F)";
		this.fileMenuItem.DropDownItems.AddRange(this.homeMenuItem, this.loginMenuItem, this.currentPlayingMenuItem, this.toolStripSeparatorDownload1, this.openDownloadDirMenuItem, this.changeDownloadDirMenuItem, this.downloadManagerMenuItem, this.refreshMenuItem, this.toolStripSeparatorDownload2, this.hideMenuItem, this.exitMenuItem);
		this.homeMenuItem.Name = "homeMenuItem";
		this.homeMenuItem.Size = new System.Drawing.Size(178, 26);
		this.homeMenuItem.Text = "主页(&H)";
		this.homeMenuItem.Click += new System.EventHandler(homeMenuItem_Click);
		this.loginMenuItem.Name = "loginMenuItem";
		this.loginMenuItem.Size = new System.Drawing.Size(178, 26);
		this.loginMenuItem.Text = "登录";
		this.loginMenuItem.Click += new System.EventHandler(loginMenuItem_Click);
		this.currentPlayingMenuItem.Name = "currentPlayingMenuItem";
		this.currentPlayingMenuItem.Size = new System.Drawing.Size(200, 26);
		this.currentPlayingMenuItem.Text = "当前播放";
		this.currentPlayingMenuItem.Visible = false;
		this.currentPlayingMenuItem.DropDownOpening += new System.EventHandler(currentPlayingMenuItem_DropDownOpening);
		this.refreshMenuItem.Name = "refreshMenuItem";
		this.refreshMenuItem.ShortcutKeys = System.Windows.Forms.Keys.F5;
		this.refreshMenuItem.Size = new System.Drawing.Size(200, 26);
		this.refreshMenuItem.Text = "刷新(&F5)";
		this.refreshMenuItem.Click += new System.EventHandler(refreshMenuItem_Click);
		this.openDownloadDirMenuItem.Name = "openDownloadDirMenuItem";
		this.openDownloadDirMenuItem.Size = new System.Drawing.Size(200, 26);
		this.openDownloadDirMenuItem.Text = "打开下载目录(&O)";
		this.openDownloadDirMenuItem.Click += new System.EventHandler(OpenDownloadDirectory_Click);
		this.changeDownloadDirMenuItem.Name = "changeDownloadDirMenuItem";
		this.changeDownloadDirMenuItem.Size = new System.Drawing.Size(200, 26);
		this.changeDownloadDirMenuItem.Text = "更改下载目录(&C)";
		this.changeDownloadDirMenuItem.Click += new System.EventHandler(ChangeDownloadDirectory_Click);
		this.downloadManagerMenuItem.Name = "downloadManagerMenuItem";
		this.downloadManagerMenuItem.Size = new System.Drawing.Size(200, 26);
		this.downloadManagerMenuItem.Text = "传输管理(&D)";
		this.downloadManagerMenuItem.Click += new System.EventHandler(OpenDownloadManager_Click);
		this.exitMenuItem.Name = "exitMenuItem";
		this.exitMenuItem.ShortcutKeys = System.Windows.Forms.Keys.F4 | System.Windows.Forms.Keys.Alt;
		this.exitMenuItem.Size = new System.Drawing.Size(178, 26);
		this.exitMenuItem.Text = "退出";
		this.exitMenuItem.Click += new System.EventHandler(exitMenuItem_Click);
		this.playControlMenuItem.DropDownItems.AddRange(this.playPauseMenuItem, this.toolStripSeparator1, this.playbackMenuItem, this.qualityMenuItem, this.outputDeviceMenuItem, this.prevMenuItem, this.nextMenuItem, this.jumpToPositionMenuItem, this.autoReadLyricsMenuItem, this.focusFollowPlaybackMenuItem, this.preventSleepDuringPlaybackMenuItem, this.hideSequenceMenuItem, this.hideControlBarMenuItem, this.themeMenuItem);
		this.playControlMenuItem.Name = "playControlMenuItem";
		this.playControlMenuItem.Size = new System.Drawing.Size(98, 24);
		this.playControlMenuItem.Text = "播放/控制(&M)";
		this.playPauseMenuItem.Name = "playPauseMenuItem";
		this.playPauseMenuItem.Size = new System.Drawing.Size(180, 26);
		this.playPauseMenuItem.Text = "播放/暂停\tSpace";
		this.playPauseMenuItem.Click += new System.EventHandler(playPauseMenuItem_Click);
		this.toolStripSeparator1.Name = "toolStripSeparator1";
		this.toolStripSeparator1.Size = new System.Drawing.Size(177, 6);
		this.playbackMenuItem.DropDownItems.AddRange(this.sequentialMenuItem, this.loopMenuItem, this.loopOneMenuItem, this.randomMenuItem);
		this.playbackMenuItem.Name = "playbackMenuItem";
		this.playbackMenuItem.Size = new System.Drawing.Size(180, 26);
		this.playbackMenuItem.Text = "播放次序";
		this.sequentialMenuItem.CheckOnClick = true;
		this.sequentialMenuItem.Name = "sequentialMenuItem";
		this.sequentialMenuItem.Size = new System.Drawing.Size(152, 26);
		this.sequentialMenuItem.Text = "顺序播放";
		this.sequentialMenuItem.Click += new System.EventHandler(sequentialMenuItem_Click);
		this.loopMenuItem.CheckOnClick = true;
		this.loopMenuItem.Name = "loopMenuItem";
		this.loopMenuItem.Size = new System.Drawing.Size(152, 26);
		this.loopMenuItem.Text = "列表循环";
		this.loopMenuItem.Click += new System.EventHandler(loopMenuItem_Click);
		this.loopOneMenuItem.CheckOnClick = true;
		this.loopOneMenuItem.Name = "loopOneMenuItem";
		this.loopOneMenuItem.Size = new System.Drawing.Size(152, 26);
		this.loopOneMenuItem.Text = "单曲循环";
		this.loopOneMenuItem.Click += new System.EventHandler(loopOneMenuItem_Click);
		this.randomMenuItem.CheckOnClick = true;
		this.randomMenuItem.Name = "randomMenuItem";
		this.randomMenuItem.Size = new System.Drawing.Size(152, 26);
		this.randomMenuItem.Text = "随机播放";
		this.randomMenuItem.Click += new System.EventHandler(randomMenuItem_Click);
		this.qualityMenuItem.DropDownItems.AddRange(this.standardQualityMenuItem, this.highQualityMenuItem, this.losslessQualityMenuItem, this.hiresQualityMenuItem, this.surroundHDQualityMenuItem, this.dolbyQualityMenuItem, this.masterQualityMenuItem);
		this.qualityMenuItem.Name = "qualityMenuItem";
		this.qualityMenuItem.Size = new System.Drawing.Size(180, 26);
		this.qualityMenuItem.Text = "音质";
		this.outputDeviceMenuItem.Name = "outputDeviceMenuItem";
		this.outputDeviceMenuItem.Size = new System.Drawing.Size(180, 26);
		this.outputDeviceMenuItem.Text = "输出设备...\tF9";
		this.outputDeviceMenuItem.Click += new System.EventHandler(outputDeviceMenuItem_Click);
		this.standardQualityMenuItem.Name = "standardQualityMenuItem";
          this.standardQualityMenuItem.Size = new System.Drawing.Size(180, 26);
          this.standardQualityMenuItem.Text = "标准音质";
          this.standardQualityMenuItem.CheckOnClick = true;
          this.standardQualityMenuItem.Click += new System.EventHandler(qualityMenuItem_Click);
          this.highQualityMenuItem.Name = "highQualityMenuItem";
          this.highQualityMenuItem.Size = new System.Drawing.Size(180, 26);
          this.highQualityMenuItem.Text = "极高音质";
          this.highQualityMenuItem.CheckOnClick = true;
          this.highQualityMenuItem.Click += new System.EventHandler(qualityMenuItem_Click);
          this.losslessQualityMenuItem.Name = "losslessQualityMenuItem";
          this.losslessQualityMenuItem.Size = new System.Drawing.Size(180, 26);
          this.losslessQualityMenuItem.Text = "无损音质";
          this.losslessQualityMenuItem.CheckOnClick = true;
          this.losslessQualityMenuItem.Click += new System.EventHandler(qualityMenuItem_Click);
          this.hiresQualityMenuItem.Name = "hiresQualityMenuItem";
          this.hiresQualityMenuItem.Size = new System.Drawing.Size(180, 26);
          this.hiresQualityMenuItem.Text = "Hi-Res音质";
          this.hiresQualityMenuItem.CheckOnClick = true;
          this.hiresQualityMenuItem.Click += new System.EventHandler(qualityMenuItem_Click);
          this.surroundHDQualityMenuItem.Name = "surroundHDQualityMenuItem";
          this.surroundHDQualityMenuItem.Size = new System.Drawing.Size(180, 26);
          this.surroundHDQualityMenuItem.Text = "高清环绕声";
          this.surroundHDQualityMenuItem.CheckOnClick = true;
          this.surroundHDQualityMenuItem.Click += new System.EventHandler(qualityMenuItem_Click);
          this.dolbyQualityMenuItem.Name = "dolbyQualityMenuItem";
          this.dolbyQualityMenuItem.Size = new System.Drawing.Size(180, 26);
          this.dolbyQualityMenuItem.Text = "沉浸环绕声";
          this.dolbyQualityMenuItem.CheckOnClick = true;
          this.dolbyQualityMenuItem.Click += new System.EventHandler(qualityMenuItem_Click);
          this.masterQualityMenuItem.Name = "masterQualityMenuItem";
          this.masterQualityMenuItem.Size = new System.Drawing.Size(180, 26);
          this.masterQualityMenuItem.Text = "超清母带";
          this.masterQualityMenuItem.CheckOnClick = true;
          this.masterQualityMenuItem.Click += new System.EventHandler(qualityMenuItem_Click);
		this.prevMenuItem.Name = "prevMenuItem";
		this.prevMenuItem.ShortcutKeys = System.Windows.Forms.Keys.F3;
		this.prevMenuItem.Size = new System.Drawing.Size(180, 26);
		this.prevMenuItem.Text = "上一曲";
		this.prevMenuItem.Click += new System.EventHandler(prevMenuItem_Click);
		this.nextMenuItem.Name = "nextMenuItem";
		this.nextMenuItem.ShortcutKeys = System.Windows.Forms.Keys.F4;
		this.nextMenuItem.Size = new System.Drawing.Size(180, 26);
		this.nextMenuItem.Text = "下一曲";
		this.nextMenuItem.Click += new System.EventHandler(nextMenuItem_Click);
		this.jumpToPositionMenuItem.Name = "jumpToPositionMenuItem";
		this.jumpToPositionMenuItem.ShortcutKeys = System.Windows.Forms.Keys.F12;
		this.jumpToPositionMenuItem.Size = new System.Drawing.Size(180, 26);
		this.jumpToPositionMenuItem.Text = "跳转到位置(&J)...";
		this.jumpToPositionMenuItem.Click += new System.EventHandler(jumpToPositionMenuItem_Click);
		this.autoReadLyricsMenuItem.Name = "autoReadLyricsMenuItem";
		this.autoReadLyricsMenuItem.ShortcutKeys = System.Windows.Forms.Keys.F11;
          this.autoReadLyricsMenuItem.Size = new System.Drawing.Size(180, 26);
          this.autoReadLyricsMenuItem.Text = "打开歌词朗读\tF11";
          this.autoReadLyricsMenuItem.CheckOnClick = true;
          this.autoReadLyricsMenuItem.Click += new System.EventHandler(autoReadLyricsMenuItem_Click);
		this.focusFollowPlaybackMenuItem.Name = "focusFollowPlaybackMenuItem";
		this.focusFollowPlaybackMenuItem.Size = new System.Drawing.Size(180, 26);
		this.focusFollowPlaybackMenuItem.Text = "\u7126\u70B9\u8DDF\u968F\u64AD\u653E";
		this.focusFollowPlaybackMenuItem.CheckOnClick = true;
		this.focusFollowPlaybackMenuItem.Click += new System.EventHandler(focusFollowPlaybackMenuItem_Click);
		this.preventSleepDuringPlaybackMenuItem.Name = "preventSleepDuringPlaybackMenuItem";
		this.preventSleepDuringPlaybackMenuItem.Size = new System.Drawing.Size(180, 26);
		this.preventSleepDuringPlaybackMenuItem.Text = "禁止播放时睡眠/息屏";
		this.preventSleepDuringPlaybackMenuItem.CheckOnClick = true;
		this.preventSleepDuringPlaybackMenuItem.Click += new System.EventHandler(preventSleepDuringPlaybackMenuItem_Click);
          this.hideSequenceMenuItem.Name = "hideSequenceMenuItem";
          this.hideSequenceMenuItem.ShortcutKeys = System.Windows.Forms.Keys.F8;
          this.hideSequenceMenuItem.Size = new System.Drawing.Size(180, 26);
          this.hideSequenceMenuItem.Text = "隐藏序号\tF8";
          this.hideSequenceMenuItem.CheckOnClick = true;
          this.hideSequenceMenuItem.Click += new System.EventHandler(hideSequenceMenuItem_Click);
          this.hideControlBarMenuItem.Name = "hideControlBarMenuItem";
          this.hideControlBarMenuItem.ShortcutKeys = System.Windows.Forms.Keys.F7;
          this.hideControlBarMenuItem.Size = new System.Drawing.Size(180, 26);
          this.hideControlBarMenuItem.Text = "隐藏控制栏\tF7";
          this.hideControlBarMenuItem.CheckOnClick = true;
          this.hideControlBarMenuItem.Click += new System.EventHandler(hideControlBarMenuItem_Click);
		this.themeMenuItem.DropDownItems.AddRange(this.themeGrassFreshMenuItem, this.themeGrassSoftMenuItem, this.themeGrassWarmMenuItem, this.themeGrassMutedMenuItem);
		this.themeMenuItem.Name = "themeMenuItem";
		this.themeMenuItem.Size = new System.Drawing.Size(180, 26);
		this.themeMenuItem.Text = "配色方案";
		this.themeGrassFreshMenuItem.Name = "themeGrassFreshMenuItem";
		this.themeGrassFreshMenuItem.Size = new System.Drawing.Size(180, 26);
		this.themeGrassFreshMenuItem.Text = "草绿清新";
		this.themeGrassFreshMenuItem.Click += new System.EventHandler(themeGrassFreshMenuItem_Click);
		this.themeGrassSoftMenuItem.Name = "themeGrassSoftMenuItem";
		this.themeGrassSoftMenuItem.Size = new System.Drawing.Size(180, 26);
		this.themeGrassSoftMenuItem.Text = "草绿柔和";
		this.themeGrassSoftMenuItem.Click += new System.EventHandler(themeGrassSoftMenuItem_Click);
		this.themeGrassWarmMenuItem.Name = "themeGrassWarmMenuItem";
		this.themeGrassWarmMenuItem.Size = new System.Drawing.Size(180, 26);
		this.themeGrassWarmMenuItem.Text = "草绿暖意";
		this.themeGrassWarmMenuItem.Click += new System.EventHandler(themeGrassWarmMenuItem_Click);
		this.themeGrassMutedMenuItem.Name = "themeGrassMutedMenuItem";
		this.themeGrassMutedMenuItem.Size = new System.Drawing.Size(180, 26);
		this.themeGrassMutedMenuItem.Text = "草绿静雅";
		this.themeGrassMutedMenuItem.Click += new System.EventHandler(themeGrassMutedMenuItem_Click);
		this.helpMenuItem.DropDownItems.AddRange(this.checkUpdateMenuItem, this.donateMenuItem, this.shortcutsMenuItem, this.aboutMenuItem);
		this.helpMenuItem.Name = "helpMenuItem";
		this.helpMenuItem.Size = new System.Drawing.Size(73, 24);
		this.helpMenuItem.Text = "帮助(&H)";
		this.checkUpdateMenuItem.Name = "checkUpdateMenuItem";
		this.checkUpdateMenuItem.Size = new System.Drawing.Size(224, 26);
		this.checkUpdateMenuItem.Text = "检查更新(&U)...";
		this.checkUpdateMenuItem.Click += new System.EventHandler(checkUpdateMenuItem_Click);
		this.donateMenuItem.Name = "donateMenuItem";
		this.donateMenuItem.Size = new System.Drawing.Size(224, 26);
		this.donateMenuItem.Text = "捐赠(&D)...";
		this.donateMenuItem.Click += new System.EventHandler(donateMenuItem_Click);
		this.shortcutsMenuItem.Name = "shortcutsMenuItem";
		this.shortcutsMenuItem.Size = new System.Drawing.Size(224, 26);
		this.shortcutsMenuItem.Text = "快捷键(&K)...";
		this.shortcutsMenuItem.Click += new System.EventHandler(shortcutsMenuItem_Click);
		this.aboutMenuItem.Name = "aboutMenuItem";
		this.aboutMenuItem.Size = new System.Drawing.Size(224, 26);
		this.aboutMenuItem.Text = "关于(&A)...";
		this.aboutMenuItem.Click += new System.EventHandler(aboutMenuItem_Click);
		this.searchPanel.Controls.Add(this.searchTypeComboBox);
		this.searchPanel.Controls.Add(this.searchTypeLabel);
		this.searchPanel.Controls.Add(this.searchButton);
                this.searchPanel.Controls.Add(this.backButton);
		this.searchPanel.Controls.Add(this.searchTextBox);
		this.searchPanel.Controls.Add(this.searchLabel);
		this.searchPanel.Dock = System.Windows.Forms.DockStyle.Top;
		this.searchPanel.Location = new System.Drawing.Point(0, 28);
		this.searchPanel.Name = "searchPanel";
		this.searchPanel.Padding = new System.Windows.Forms.Padding(16, 16, 16, 16);
		this.searchPanel.Size = new System.Drawing.Size(1200, 84);
        this.searchPanel.TabIndex = 0;
          this.searchTypeComboBox.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
		this.searchTypeComboBox.Font = new System.Drawing.Font("Microsoft YaHei UI", 11f);
		this.searchTypeComboBox.FormattingEnabled = true;
        this.searchTypeComboBox.Items.AddRange("歌曲", "歌单", "专辑", "歌手", "播客");
        this.searchTypeComboBox.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right;
		this.searchTypeComboBox.Location = new System.Drawing.Point(888, 22);
		this.searchTypeComboBox.Name = "searchTypeComboBox";
		this.searchTypeComboBox.Size = new System.Drawing.Size(180, 40);
		this.searchTypeComboBox.TabIndex = 2;
          this.searchTypeComboBox.SelectedIndexChanged += new System.EventHandler(searchTypeComboBox_SelectedIndexChanged);
		this.searchTypeLabel.AutoSize = true;
        this.searchTypeLabel.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right;
		this.searchTypeLabel.Location = new System.Drawing.Point(846, 22);
		this.searchTypeLabel.Name = "searchTypeLabel";
		this.searchTypeLabel.Size = new System.Drawing.Size(84, 20);
		this.searchTypeLabel.TabIndex = 0;
		this.searchTypeLabel.Text = "类型:";
		this.searchButton.Font = new System.Drawing.Font("Microsoft YaHei UI", 12f, System.Drawing.FontStyle.Bold);
        this.searchButton.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right;
		this.searchButton.Location = new System.Drawing.Point(1080, 22);
		this.searchButton.Name = "searchButton";
		this.searchButton.Size = new System.Drawing.Size(104, 40);
		this.searchButton.TabIndex = 3;
		this.searchButton.Text = "搜索";
		this.searchButton.UseVisualStyleBackColor = false;
		this.searchButton.Tag = "Primary";
                this.searchButton.Click += new System.EventHandler(searchButton_Click);
                this.backButton.Font = new System.Drawing.Font("Microsoft YaHei UI", 14f, System.Drawing.FontStyle.Bold);
        this.backButton.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left;
                this.backButton.Location = new System.Drawing.Point(16, 20);
                this.backButton.Name = "backButton";
                this.backButton.Size = new System.Drawing.Size(44, 44);
                this.backButton.TabStop = false;
                this.backButton.Text = "⬅️";
                this.backButton.UseVisualStyleBackColor = false;
                this.backButton.Click += new System.EventHandler(backButton_Click);
		this.searchTextContextMenu.ImageScalingSize = new System.Drawing.Size(20, 20);
		this.searchTextContextMenu.Items.AddRange(this.searchCopyMenuItem, this.searchCutMenuItem, this.searchPasteMenuItem, this.searchHistorySeparator, this.searchClearHistoryMenuItem);
		this.searchTextContextMenu.Name = "searchTextContextMenu";
		this.searchTextContextMenu.Size = new System.Drawing.Size(169, 114);
		this.searchTextContextMenu.Opening += new System.ComponentModel.CancelEventHandler(searchTextContextMenu_Opening);
		this.searchCopyMenuItem.Name = "searchCopyMenuItem";
		this.searchCopyMenuItem.Size = new System.Drawing.Size(168, 24);
		this.searchCopyMenuItem.Text = "复制";
		this.searchCopyMenuItem.Click += new System.EventHandler(searchCopyMenuItem_Click);
		this.searchCutMenuItem.Name = "searchCutMenuItem";
		this.searchCutMenuItem.Size = new System.Drawing.Size(168, 24);
		this.searchCutMenuItem.Text = "剪切";
		this.searchCutMenuItem.Click += new System.EventHandler(searchCutMenuItem_Click);
		this.searchPasteMenuItem.Name = "searchPasteMenuItem";
		this.searchPasteMenuItem.Size = new System.Drawing.Size(168, 24);
		this.searchPasteMenuItem.Text = "粘贴";
		this.searchPasteMenuItem.Click += new System.EventHandler(searchPasteMenuItem_Click);
		this.searchHistorySeparator.Name = "searchHistorySeparator";
		this.searchHistorySeparator.Size = new System.Drawing.Size(165, 6);
		this.searchClearHistoryMenuItem.Name = "searchClearHistoryMenuItem";
		this.searchClearHistoryMenuItem.Size = new System.Drawing.Size(168, 24);
		this.searchClearHistoryMenuItem.Text = "清空历史";
		this.searchClearHistoryMenuItem.Click += new System.EventHandler(searchClearHistoryMenuItem_Click);
		this.searchTextBox.Font = new System.Drawing.Font("Microsoft YaHei UI", 12f);
        this.searchTextBox.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
		this.searchTextBox.Location = new System.Drawing.Point(136, 22);
          this.searchTextBox.Name = "searchTextBox";
          this.searchTextBox.AccessibleDescription = "搜索或粘贴网易云网页 URL ，多 URL 以分号分隔";
          this.searchTextBox.Size = new System.Drawing.Size(736, 40);
          this.searchTextBox.TabIndex = 1;
          this.searchTextBox.ContextMenuStrip = this.searchTextContextMenu;
		this.searchTextBox.TextChanged += new System.EventHandler(searchTextBox_TextChanged);
		this.searchTextBox.KeyDown += new System.Windows.Forms.KeyEventHandler(searchTextBox_KeyDown);
		this.searchLabel.AutoSize = true;
		this.searchLabel.Font = new System.Drawing.Font("Microsoft YaHei UI", 12f);
        this.searchLabel.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left;
		this.searchLabel.Location = new System.Drawing.Point(72, 26);
                this.searchLabel.Name = "searchLabel";
                this.searchLabel.Size = new System.Drawing.Size(111, 27);
                this.searchLabel.TabIndex = 0;
                this.searchLabel.Text = "搜索";
                this.resultListPanel.Dock = System.Windows.Forms.DockStyle.Fill;
                this.resultListPanel.Location = new System.Drawing.Point(0, 112);
                this.resultListPanel.Margin = new System.Windows.Forms.Padding(0);
                this.resultListPanel.Name = "resultListPanel";
                this.resultListPanel.Padding = new System.Windows.Forms.Padding(0);
                this.resultListPanel.Size = new System.Drawing.Size(1200, 368);
                this.resultListPanel.TabIndex = 1;
                this.resultListPanel.TabStop = false;
                this.resultListPanel.Controls.Add(this.resultListView);
                this.resultListView.Columns.AddRange(this.columnHeader0, this.columnHeader1, this.columnHeader2, this.columnHeader3, this.columnHeader4, this.columnHeader5);
		this.resultListView.Dock = System.Windows.Forms.DockStyle.None;
		this.resultListView.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Bottom;
		this.resultListView.FullRowSelect = true;
		this.resultListView.GridLines = false;
		this.resultListView.HideSelection = false;
		this.resultListView.Location = new System.Drawing.Point(0, 0);
        this.resultListView.MultiSelect = false;
        this.resultListView.Name = "resultListView";
		this.resultListView.AllowDrop = true;
        this.resultListView.Size = new System.Drawing.Size(1200, 368);
        this.resultListView.Font = new System.Drawing.Font("Microsoft YaHei UI", 11.5f);
        this.resultListView.TabIndex = 1;
        this.resultListView.UseCompatibleStateImageBehavior = false;
        this.resultListView.View = System.Windows.Forms.View.Details;
		this.resultListView.ContextMenuStrip = this.songContextMenu;
		this.resultListView.ItemActivate += new System.EventHandler(resultListView_ItemActivate);
		this.resultListView.Enter += new System.EventHandler(resultListView_Enter);
		this.resultListView.GotFocus += new System.EventHandler(resultListView_GotFocus);
		this.resultListView.MouseUp += new System.Windows.Forms.MouseEventHandler(resultListView_MouseUp);
		this.resultListView.MouseDown += new System.Windows.Forms.MouseEventHandler(resultListView_MouseDown);
		this.resultListView.MouseMove += new System.Windows.Forms.MouseEventHandler(resultListView_MouseMove);
		this.resultListView.ItemDrag += new System.Windows.Forms.ItemDragEventHandler(resultListView_ItemDrag);
		this.resultListView.DragEnter += new System.Windows.Forms.DragEventHandler(resultListView_DragEnter);
		this.resultListView.DragOver += new System.Windows.Forms.DragEventHandler(resultListView_DragOver);
		this.resultListView.DragDrop += new System.Windows.Forms.DragEventHandler(resultListView_DragDrop);
		this.resultListView.DragLeave += new System.EventHandler(resultListView_DragLeave);
		this.resultListView.KeyDown += new System.Windows.Forms.KeyEventHandler(resultListView_KeyDown);
		this.resultListView.KeyUp += new System.Windows.Forms.KeyEventHandler(resultListView_KeyUp);
		this.resultListView.KeyPress += new System.Windows.Forms.KeyPressEventHandler(resultListView_KeyPress);
		this.resultListView.HandleCreated += new System.EventHandler(resultListView_HandleCreated);
		this.resultListView.CacheVirtualItems += new System.Windows.Forms.CacheVirtualItemsEventHandler(resultListView_CacheVirtualItems);
		this.resultListView.RetrieveVirtualItem += new System.Windows.Forms.RetrieveVirtualItemEventHandler(resultListView_RetrieveVirtualItem);
		this.resultListView.SearchForVirtualItem += new System.Windows.Forms.SearchForVirtualItemEventHandler(resultListView_SearchForVirtualItem);
		this.resultListView.VirtualItemsSelectionRangeChanged += new System.Windows.Forms.ListViewVirtualItemsSelectionRangeChangedEventHandler(resultListView_VirtualItemsSelectionRangeChanged);
		this.resultListView.SelectedIndexChanged += new System.EventHandler(resultListView_SelectedIndexChanged);
		this.resultListView.ColumnWidthChanging += new System.Windows.Forms.ColumnWidthChangingEventHandler(resultListView_ColumnWidthChanging);
		this.resultListView.ColumnWidthChanged += new System.Windows.Forms.ColumnWidthChangedEventHandler(resultListView_ColumnWidthChanged);
		this.resultListView.SizeChanged += new System.EventHandler(resultListView_SizeChanged);
		this.columnHeader0.Text = string.Empty;
		this.columnHeader0.Width = 0;
		this.columnHeader1.Text = string.Empty;
		this.columnHeader1.Width = 50;
		this.columnHeader2.Text = string.Empty;
		this.columnHeader2.Width = 350;
		this.columnHeader3.Text = string.Empty;
		this.columnHeader3.Width = 200;
		this.columnHeader4.Text = string.Empty;
		this.columnHeader4.Width = 250;
		this.columnHeader5.Text = string.Empty;
		this.columnHeader5.Width = 150;
                this.controlPanel.Controls.Add(this.lyricsLabel);
                this.controlPanel.Controls.Add(this.volumeLabel);
                this.controlPanel.Controls.Add(this.volumeTrackBar);
                this.controlPanel.Controls.Add(this.timeLabel);
                this.controlPanel.Controls.Add(this.progressTrackBar);
                this.controlPanel.Controls.Add(this.playPauseButton);
                this.controlPanel.Controls.Add(this.currentSongLabel);
		this.controlPanel.Dock = System.Windows.Forms.DockStyle.Bottom;
		this.controlPanel.Location = new System.Drawing.Point(0, 480);
		this.controlPanel.Name = "controlPanel";
		this.controlPanel.Size = new System.Drawing.Size(1200, 170);
                this.controlPanel.TabIndex = 3;
		this.controlPanel.TabStop = false;
		this.controlPanel.Text = string.Empty;
		this.controlPanel.AccessibleRole = System.Windows.Forms.AccessibleRole.Grouping;
		this.lyricsLabel.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
		this.lyricsLabel.BackColor = System.Drawing.Color.FromArgb(245, 245, 245);
		this.lyricsLabel.Font = new System.Drawing.Font("Microsoft YaHei UI", 12f, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, 0);
		this.lyricsLabel.ForeColor = System.Drawing.Color.FromArgb(51, 51, 51);
		this.lyricsLabel.Location = new System.Drawing.Point(12, 120);
		this.lyricsLabel.Name = "lyricsLabel";
		this.lyricsLabel.Padding = new System.Windows.Forms.Padding(10, 8, 10, 8);
		this.lyricsLabel.Size = new System.Drawing.Size(1176, 44);
                this.lyricsLabel.TabIndex = 7;
                this.lyricsLabel.Text = "暂无歌词";
		this.lyricsLabel.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
		this.lyricsLabel.AccessibleRole = System.Windows.Forms.AccessibleRole.Text;
		this.volumeLabel.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right;
		this.volumeLabel.Location = new System.Drawing.Point(1130, 74);
		this.volumeLabel.Name = "volumeLabel";
		this.volumeLabel.Size = new System.Drawing.Size(58, 23);
                this.volumeLabel.TabIndex = 6;
                this.volumeLabel.Text = "100%";
                this.volumeLabel.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
                this.volumeTrackBar.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right;
                this.volumeTrackBar.Location = new System.Drawing.Point(1000, 64);
                this.volumeTrackBar.AutoSize = false;
                this.volumeTrackBar.Maximum = 100;
                this.volumeTrackBar.Name = "volumeTrackBar";
        this.volumeTrackBar.Size = new System.Drawing.Size(124, 30);
        this.volumeTrackBar.TabIndex = 4;
        this.volumeTrackBar.TickFrequency = 10;
        this.volumeTrackBar.TickStyle = System.Windows.Forms.TickStyle.None;
        this.volumeTrackBar.Value = 100;
        this.volumeTrackBar.AccessibleName = "音量";
        this.volumeTrackBar.KeyDown += new System.Windows.Forms.KeyEventHandler(volumeTrackBar_KeyDown);
        this.volumeTrackBar.Scroll += new System.EventHandler(volumeTrackBar_Scroll);
                this.timeLabel.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right;
                this.timeLabel.Location = new System.Drawing.Point(1050, 34);
                this.timeLabel.Name = "timeLabel";
                this.timeLabel.Size = new System.Drawing.Size(138, 23);
                this.timeLabel.TabIndex = 5;
                this.timeLabel.Text = "00:00 / 00:00";
                this.timeLabel.TextAlign = System.Drawing.ContentAlignment.MiddleRight;
                this.progressTrackBar.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
                this.progressTrackBar.Location = new System.Drawing.Point(120, 34);
                this.progressTrackBar.AutoSize = false;
                this.progressTrackBar.Maximum = 1000;
                this.progressTrackBar.Name = "progressTrackBar";
                this.progressTrackBar.Size = new System.Drawing.Size(924, 30);
        this.progressTrackBar.TabIndex = 3;
        this.progressTrackBar.TickFrequency = 50;
        this.progressTrackBar.TickStyle = System.Windows.Forms.TickStyle.None;
        this.progressTrackBar.LargeChange = 10;
        this.progressTrackBar.SmallChange = 1;
        this.progressTrackBar.Scroll += new System.EventHandler(progressTrackBar_Scroll);
        this.progressTrackBar.KeyDown += new System.Windows.Forms.KeyEventHandler(progressTrackBar_KeyDown);
        this.progressTrackBar.MouseDown += new System.Windows.Forms.MouseEventHandler(progressTrackBar_MouseDown);
        this.progressTrackBar.MouseUp += new System.Windows.Forms.MouseEventHandler(progressTrackBar_MouseUp);
		this.playPauseButton.Font = new System.Drawing.Font("Microsoft YaHei UI", 11f, System.Drawing.FontStyle.Bold);
		this.playPauseButton.Location = new System.Drawing.Point(12, 32);
		this.playPauseButton.Name = "playPauseButton";
		this.playPauseButton.Size = new System.Drawing.Size(100, 60);
		this.playPauseButton.TabIndex = 1;
		this.playPauseButton.Text = "播放";
		this.playPauseButton.UseVisualStyleBackColor = true;
		this.playPauseButton.Tag = "Primary";
          this.playPauseButton.Click += new System.EventHandler(playPauseButton_Click);
		this.currentSongLabel.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
		this.currentSongLabel.Font = new System.Drawing.Font("Microsoft YaHei UI", 12f, System.Drawing.FontStyle.Bold);
		this.currentSongLabel.Location = new System.Drawing.Point(12, 8);
		this.currentSongLabel.Name = "currentSongLabel";
		this.currentSongLabel.Size = new System.Drawing.Size(1176, 23);
		this.currentSongLabel.TabIndex = 0;
		this.currentSongLabel.Text = "未播放";
		this.currentSongLabel.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
        this.statusStrip1.ImageScalingSize = new System.Drawing.Size(20, 20);
        this.statusStrip1.AccessibleRole = System.Windows.Forms.AccessibleRole.StatusBar;
        this.statusStrip1.AccessibleName = "状态栏";
        this.statusStrip1.Items.AddRange(this.toolStripStatusLabel1);
        this.statusStrip1.Location = new System.Drawing.Point(0, 650);
        this.statusStrip1.Name = "statusStrip1";
        this.statusStrip1.Size = new System.Drawing.Size(1200, 26);
        this.statusStrip1.TabIndex = 5;
        this.statusStrip1.Text = "";
        this.toolStripStatusLabel1.Name = "toolStripStatusLabel1";
        this.toolStripStatusLabel1.Size = new System.Drawing.Size(39, 20);
        this.toolStripStatusLabel1.Text = "就绪";
        this.toolStripStatusLabel1.AccessibleRole = System.Windows.Forms.AccessibleRole.StaticText;
        this.toolStripStatusLabel1.AccessibleName = "";
		this.songContextMenu.ImageScalingSize = new System.Drawing.Size(20, 20);
		this.songContextMenu.Items.AddRange(this.viewSourceMenuItem, this.insertPlayMenuItem, this.likeSongMenuItem, this.unlikeSongMenuItem, this.addToPlaylistMenuItem, this.removeFromPlaylistMenuItem, this.cloudMenuSeparator, this.uploadToCloudMenuItem, this.deleteFromCloudMenuItem, this.toolStripSeparatorCollection, this.subscribePlaylistMenuItem, this.unsubscribePlaylistMenuItem, this.deletePlaylistMenuItem, this.createPlaylistMenuItem, this.subscribeAlbumMenuItem, this.unsubscribeAlbumMenuItem, this.subscribePodcastMenuItem, this.unsubscribePodcastMenuItem, this.toolStripSeparatorView, this.viewSongArtistMenuItem, this.viewSongAlbumMenuItem, this.subscribeSongArtistMenuItem, this.subscribeSongAlbumMenuItem, this.viewPodcastMenuItem, this.shareSongMenuItem, this.sharePlaylistMenuItem, this.shareAlbumMenuItem, this.sharePodcastMenuItem, this.sharePodcastEpisodeMenuItem, this.artistSongsSortMenuItem, this.artistAlbumsSortMenuItem, this.podcastSortMenuItem, this.commentMenuSeparator, this.commentMenuItem, this.toolStripSeparatorArtist, this.shareArtistMenuItem, this.subscribeArtistMenuItem, this.unsubscribeArtistMenuItem, this.toolStripSeparatorDownload3, this.downloadSongMenuItem, this.downloadLyricsMenuItem, this.downloadPlaylistMenuItem, this.downloadAlbumMenuItem, this.downloadPodcastMenuItem, this.batchDownloadMenuItem, this.downloadCategoryMenuItem, this.batchDownloadPlaylistsMenuItem);
		this.songContextMenu.Name = "songContextMenu";
		this.songContextMenu.Size = new System.Drawing.Size(211, 320);
		this.songContextMenu.Opening += new System.ComponentModel.CancelEventHandler(songContextMenu_Opening);
		this.songContextMenu.Closed += new System.Windows.Forms.ToolStripDropDownClosedEventHandler(songContextMenu_Closed);
		this.currentPlayingMenuItem.DropDown = this.songContextMenu;
		this.viewSourceMenuItem.Name = "viewSourceMenuItem";
		this.viewSourceMenuItem.Size = new System.Drawing.Size(210, 24);
		this.viewSourceMenuItem.Text = "查看来源(&S)";
		this.viewSourceMenuItem.Visible = false;
		this.viewSourceMenuItem.Click += new System.EventHandler(viewSourceMenuItem_Click);
		this.insertPlayMenuItem.Name = "insertPlayMenuItem";
		this.insertPlayMenuItem.Size = new System.Drawing.Size(210, 24);
		this.insertPlayMenuItem.Text = "插播(&I)";
		this.insertPlayMenuItem.Click += new System.EventHandler(insertPlayMenuItem_Click);
		this.likeSongMenuItem.Name = "likeSongMenuItem";
		this.likeSongMenuItem.Size = new System.Drawing.Size(210, 24);
		this.likeSongMenuItem.Text = "收藏歌曲(&L)";
		this.likeSongMenuItem.Click += new System.EventHandler(likeSongMenuItem_Click);
		this.unlikeSongMenuItem.Name = "unlikeSongMenuItem";
		this.unlikeSongMenuItem.Size = new System.Drawing.Size(210, 24);
		this.unlikeSongMenuItem.Text = "取消收藏歌曲(&U)";
		this.unlikeSongMenuItem.Click += new System.EventHandler(unlikeSongMenuItem_Click);
		this.addToPlaylistMenuItem.Name = "addToPlaylistMenuItem";
		this.addToPlaylistMenuItem.Size = new System.Drawing.Size(210, 24);
		this.addToPlaylistMenuItem.Text = "添加到歌单(&A)...";
		this.addToPlaylistMenuItem.Click += new System.EventHandler(addToPlaylistMenuItem_Click);
		this.removeFromPlaylistMenuItem.Name = "removeFromPlaylistMenuItem";
		this.removeFromPlaylistMenuItem.Size = new System.Drawing.Size(210, 24);
		this.removeFromPlaylistMenuItem.Text = "从歌单中移除(&R)";
		this.removeFromPlaylistMenuItem.Click += new System.EventHandler(removeFromPlaylistMenuItem_Click);
		this.cloudMenuSeparator.Name = "cloudMenuSeparator";
		this.cloudMenuSeparator.Size = new System.Drawing.Size(207, 6);
		this.cloudMenuSeparator.Visible = false;
		this.uploadToCloudMenuItem.Name = "uploadToCloudMenuItem";
		this.uploadToCloudMenuItem.Size = new System.Drawing.Size(210, 24);
		this.uploadToCloudMenuItem.Text = "上传到云盘(&U)...";
		this.uploadToCloudMenuItem.Visible = false;
		this.uploadToCloudMenuItem.Click += new System.EventHandler(uploadToCloudMenuItem_Click);
		this.deleteFromCloudMenuItem.Name = "deleteFromCloudMenuItem";
		this.deleteFromCloudMenuItem.Size = new System.Drawing.Size(210, 24);
		this.deleteFromCloudMenuItem.Text = "从云盘删除(&D)";
		this.deleteFromCloudMenuItem.Visible = false;
		this.deleteFromCloudMenuItem.Click += new System.EventHandler(deleteFromCloudMenuItem_Click);
		this.toolStripSeparatorCollection.Name = "toolStripSeparatorCollection";
		this.toolStripSeparatorCollection.Size = new System.Drawing.Size(207, 6);
		this.subscribePlaylistMenuItem.Name = "subscribePlaylistMenuItem";
		this.subscribePlaylistMenuItem.Size = new System.Drawing.Size(210, 24);
		this.subscribePlaylistMenuItem.Text = "收藏歌单(&S)";
		this.subscribePlaylistMenuItem.Click += new System.EventHandler(subscribePlaylistMenuItem_Click);
		this.unsubscribePlaylistMenuItem.Name = "unsubscribePlaylistMenuItem";
		this.unsubscribePlaylistMenuItem.Size = new System.Drawing.Size(210, 24);
		this.unsubscribePlaylistMenuItem.Text = "取消收藏歌单(&U)";
		this.unsubscribePlaylistMenuItem.Click += new System.EventHandler(unsubscribePlaylistMenuItem_Click);
		this.deletePlaylistMenuItem.Name = "deletePlaylistMenuItem";
		this.deletePlaylistMenuItem.Size = new System.Drawing.Size(210, 24);
		this.deletePlaylistMenuItem.Text = "删除歌单(&D)";
		this.deletePlaylistMenuItem.Click += new System.EventHandler(deletePlaylistMenuItem_Click);
		this.createPlaylistMenuItem.Name = "createPlaylistMenuItem";
		this.createPlaylistMenuItem.Size = new System.Drawing.Size(210, 24);
		this.createPlaylistMenuItem.Text = "新建歌单(&N)...";
		this.createPlaylistMenuItem.Visible = false;
		this.createPlaylistMenuItem.Click += new System.EventHandler(createPlaylistMenuItem_Click);
		this.subscribeSongAlbumMenuItem.Name = "subscribeSongAlbumMenuItem";
		this.subscribeSongAlbumMenuItem.Size = new System.Drawing.Size(210, 24);
		this.subscribeSongAlbumMenuItem.Text = "\u6536\u85CF\u4E13\u8F91(&J)";
		this.subscribeSongAlbumMenuItem.Visible = false;
		this.subscribeSongAlbumMenuItem.Click += new System.EventHandler(subscribeSongAlbumMenuItem_Click);
		this.subscribeAlbumMenuItem.Name = "subscribeAlbumMenuItem";
		this.subscribeAlbumMenuItem.Size = new System.Drawing.Size(210, 24);
		this.subscribeAlbumMenuItem.Text = "收藏专辑(&A)";
		this.subscribeAlbumMenuItem.Click += new System.EventHandler(subscribeAlbumMenuItem_Click);
		this.unsubscribeAlbumMenuItem.Name = "unsubscribeAlbumMenuItem";
		this.unsubscribeAlbumMenuItem.Size = new System.Drawing.Size(210, 24);
		this.unsubscribeAlbumMenuItem.Text = "取消收藏专辑(&R)";
		this.unsubscribeAlbumMenuItem.Click += new System.EventHandler(unsubscribeAlbumMenuItem_Click);
		this.subscribePodcastMenuItem.Name = "subscribePodcastMenuItem";
		this.subscribePodcastMenuItem.Size = new System.Drawing.Size(210, 24);
		this.subscribePodcastMenuItem.Text = "收藏播客(&O)";
		this.subscribePodcastMenuItem.Click += new System.EventHandler(subscribePodcastMenuItem_Click);
		this.unsubscribePodcastMenuItem.Name = "unsubscribePodcastMenuItem";
		this.unsubscribePodcastMenuItem.Size = new System.Drawing.Size(210, 24);
		this.unsubscribePodcastMenuItem.Text = "取消收藏播客(&C)";
		this.unsubscribePodcastMenuItem.Click += new System.EventHandler(unsubscribePodcastMenuItem_Click);
		this.toolStripSeparatorView.Name = "toolStripSeparatorView";
		this.toolStripSeparatorView.Size = new System.Drawing.Size(207, 6);
		this.toolStripSeparatorView.Visible = false;
		this.viewSongArtistMenuItem.Name = "viewSongArtistMenuItem";
		this.viewSongArtistMenuItem.Size = new System.Drawing.Size(210, 24);
		this.viewSongArtistMenuItem.Text = "查看歌手(&A)";
		this.viewSongArtistMenuItem.Visible = false;
		this.viewSongArtistMenuItem.Click += new System.EventHandler(viewSongArtistMenuItem_Click);
		this.viewSongAlbumMenuItem.Name = "viewSongAlbumMenuItem";
		this.viewSongAlbumMenuItem.Size = new System.Drawing.Size(210, 24);
		this.viewSongAlbumMenuItem.Text = "查看专辑(&B)";
		this.viewSongAlbumMenuItem.Visible = false;
		this.viewSongAlbumMenuItem.Click += new System.EventHandler(viewSongAlbumMenuItem_Click);
		this.subscribeSongArtistMenuItem.Name = "subscribeSongArtistMenuItem";
		this.subscribeSongArtistMenuItem.Size = new System.Drawing.Size(210, 24);
		this.subscribeSongArtistMenuItem.Text = "\u6536\u85CF\u6B4C\u624B(&F)";
		this.subscribeSongArtistMenuItem.Visible = false;
		this.subscribeSongArtistMenuItem.Click += new System.EventHandler(subscribeSongArtistMenuItem_Click);
		this.viewPodcastMenuItem.Name = "viewPodcastMenuItem";
		this.viewPodcastMenuItem.Size = new System.Drawing.Size(210, 24);
		this.viewPodcastMenuItem.Text = "查看播客(&P)";
		this.viewPodcastMenuItem.Visible = false;
		this.viewPodcastMenuItem.Click += new System.EventHandler(viewPodcastMenuItem_Click);
		this.toolStripSeparatorArtist.Name = "toolStripSeparatorArtist";
		this.toolStripSeparatorArtist.Size = new System.Drawing.Size(207, 6);
		this.toolStripSeparatorArtist.Visible = false;
		this.shareSongMenuItem.DropDownItems.AddRange(this.shareSongWebMenuItem, this.shareSongDirectMenuItem);
		this.shareSongMenuItem.Name = "shareSongMenuItem";
		this.shareSongMenuItem.Size = new System.Drawing.Size(210, 24);
		this.shareSongMenuItem.Text = "分享歌曲(&H)";
		this.shareSongMenuItem.Visible = false;
		this.shareSongWebMenuItem.Name = "shareSongWebMenuItem";
		this.shareSongWebMenuItem.Size = new System.Drawing.Size(210, 26);
		this.shareSongWebMenuItem.Text = "分享网页(&W)";
		this.shareSongWebMenuItem.Click += new System.EventHandler(shareSongWebMenuItem_Click);
		this.shareSongDirectMenuItem.Name = "shareSongDirectMenuItem";
		this.shareSongDirectMenuItem.Size = new System.Drawing.Size(210, 26);
		this.shareSongDirectMenuItem.Text = "分享直链(&L)";
		this.shareSongDirectMenuItem.Click += new System.EventHandler(shareSongDirectMenuItem_Click);
		this.sharePlaylistMenuItem.Name = "sharePlaylistMenuItem";
		this.sharePlaylistMenuItem.Size = new System.Drawing.Size(210, 24);
		this.sharePlaylistMenuItem.Text = "分享歌单(&J)";
		this.sharePlaylistMenuItem.Visible = false;
		this.sharePlaylistMenuItem.Click += new System.EventHandler(sharePlaylistMenuItem_Click);
		this.shareAlbumMenuItem.Name = "shareAlbumMenuItem";
		this.shareAlbumMenuItem.Size = new System.Drawing.Size(210, 24);
		this.shareAlbumMenuItem.Text = "分享专辑(&K)";
		this.shareAlbumMenuItem.Visible = false;
		this.shareAlbumMenuItem.Click += new System.EventHandler(shareAlbumMenuItem_Click);
		this.sharePodcastMenuItem.Name = "sharePodcastMenuItem";
		this.sharePodcastMenuItem.Size = new System.Drawing.Size(210, 24);
		this.sharePodcastMenuItem.Text = "分享播客(&P)";
		this.sharePodcastMenuItem.Visible = false;
		this.sharePodcastMenuItem.Click += new System.EventHandler(sharePodcastMenuItem_Click);
		this.sharePodcastEpisodeMenuItem.DropDownItems.AddRange(this.sharePodcastEpisodeWebMenuItem, this.sharePodcastEpisodeDirectMenuItem);
		this.sharePodcastEpisodeMenuItem.Name = "sharePodcastEpisodeMenuItem";
		this.sharePodcastEpisodeMenuItem.Size = new System.Drawing.Size(210, 24);
		this.sharePodcastEpisodeMenuItem.Text = "分享节目(&E)";
		this.sharePodcastEpisodeMenuItem.Visible = false;
		this.sharePodcastEpisodeWebMenuItem.Name = "sharePodcastEpisodeWebMenuItem";
		this.sharePodcastEpisodeWebMenuItem.Size = new System.Drawing.Size(210, 26);
		this.sharePodcastEpisodeWebMenuItem.Text = "分享网页(&W)";
		this.sharePodcastEpisodeWebMenuItem.Click += new System.EventHandler(sharePodcastEpisodeWebMenuItem_Click);
		this.sharePodcastEpisodeDirectMenuItem.Name = "sharePodcastEpisodeDirectMenuItem";
		this.sharePodcastEpisodeDirectMenuItem.Size = new System.Drawing.Size(210, 26);
		this.sharePodcastEpisodeDirectMenuItem.Text = "分享直链(&L)";
		this.sharePodcastEpisodeDirectMenuItem.Click += new System.EventHandler(sharePodcastEpisodeDirectMenuItem_Click);
		this.artistSongsSortMenuItem.DropDownItems.AddRange(this.artistSongsSortHotMenuItem, this.artistSongsSortTimeMenuItem);
		this.artistSongsSortMenuItem.Name = "artistSongsSortMenuItem";
		this.artistSongsSortMenuItem.Size = new System.Drawing.Size(210, 24);
		this.artistSongsSortMenuItem.Text = "排序(&S)";
		this.artistSongsSortMenuItem.Visible = false;
		this.artistSongsSortHotMenuItem.Name = "artistSongsSortHotMenuItem";
		this.artistSongsSortHotMenuItem.Size = new System.Drawing.Size(250, 26);
		this.artistSongsSortHotMenuItem.Text = "按热度(&H)";
		this.artistSongsSortHotMenuItem.CheckOnClick = true;
		this.artistSongsSortHotMenuItem.Click += new System.EventHandler(artistSongsSortHotMenuItem_Click);
		this.artistSongsSortTimeMenuItem.Name = "artistSongsSortTimeMenuItem";
		this.artistSongsSortTimeMenuItem.Size = new System.Drawing.Size(250, 26);
		this.artistSongsSortTimeMenuItem.Text = "按发布时间(&T)";
		this.artistSongsSortTimeMenuItem.CheckOnClick = true;
		this.artistSongsSortTimeMenuItem.Click += new System.EventHandler(artistSongsSortTimeMenuItem_Click);
		this.artistAlbumsSortMenuItem.DropDownItems.AddRange(this.artistAlbumsSortLatestMenuItem, this.artistAlbumsSortOldestMenuItem);
		this.artistAlbumsSortMenuItem.Name = "artistAlbumsSortMenuItem";
		this.artistAlbumsSortMenuItem.Size = new System.Drawing.Size(210, 24);
		this.artistAlbumsSortMenuItem.Text = "排序(&B)";
		this.artistAlbumsSortMenuItem.Visible = false;
		this.artistAlbumsSortLatestMenuItem.Name = "artistAlbumsSortLatestMenuItem";
		this.artistAlbumsSortLatestMenuItem.Size = new System.Drawing.Size(240, 26);
		this.artistAlbumsSortLatestMenuItem.Text = "按最新发布排序(&N)";
		this.artistAlbumsSortLatestMenuItem.CheckOnClick = true;
		this.artistAlbumsSortLatestMenuItem.Click += new System.EventHandler(artistAlbumsSortLatestMenuItem_Click);
		this.artistAlbumsSortOldestMenuItem.Name = "artistAlbumsSortOldestMenuItem";
		this.artistAlbumsSortOldestMenuItem.Size = new System.Drawing.Size(240, 26);
		this.artistAlbumsSortOldestMenuItem.Text = "按最早发布排序(&O)";
		this.artistAlbumsSortOldestMenuItem.CheckOnClick = true;
		this.artistAlbumsSortOldestMenuItem.Click += new System.EventHandler(artistAlbumsSortOldestMenuItem_Click);
		this.podcastSortMenuItem.DropDownItems.AddRange(this.podcastSortLatestMenuItem, this.podcastSortSerialMenuItem);
		this.podcastSortMenuItem.Name = "podcastSortMenuItem";
		this.podcastSortMenuItem.Size = new System.Drawing.Size(210, 24);
		this.podcastSortMenuItem.Text = "排序(&T)";
		this.podcastSortMenuItem.Visible = false;
		this.podcastSortLatestMenuItem.Name = "podcastSortLatestMenuItem";
		this.podcastSortLatestMenuItem.Size = new System.Drawing.Size(210, 26);
		this.podcastSortLatestMenuItem.Text = "按最新(&N)";
		this.podcastSortLatestMenuItem.CheckOnClick = true;
		this.podcastSortLatestMenuItem.Click += new System.EventHandler(podcastSortLatestMenuItem_Click);
		this.podcastSortSerialMenuItem.Name = "podcastSortSerialMenuItem";
		this.podcastSortSerialMenuItem.Size = new System.Drawing.Size(210, 26);
		this.podcastSortSerialMenuItem.Text = "按节目顺序(&S)";
		this.podcastSortSerialMenuItem.CheckOnClick = true;
		this.podcastSortSerialMenuItem.Click += new System.EventHandler(podcastSortSerialMenuItem_Click);
		this.commentMenuSeparator.Name = "commentMenuSeparator";
		this.commentMenuSeparator.Size = new System.Drawing.Size(207, 6);
		this.commentMenuSeparator.Visible = false;
		this.commentMenuItem.Name = "commentMenuItem";
		this.commentMenuItem.Size = new System.Drawing.Size(210, 24);
		this.commentMenuItem.Text = "评论(&C)";
		this.commentMenuItem.Visible = false;
		this.commentMenuItem.Click += new System.EventHandler(commentMenuItem_Click);
		this.shareArtistMenuItem.Name = "shareArtistMenuItem";
		this.shareArtistMenuItem.Size = new System.Drawing.Size(210, 24);
		this.shareArtistMenuItem.Text = "分享歌手(&S)";
		this.shareArtistMenuItem.Visible = false;
		this.shareArtistMenuItem.Click += new System.EventHandler(shareArtistMenuItem_Click);
		this.subscribeArtistMenuItem.Name = "subscribeArtistMenuItem";
		this.subscribeArtistMenuItem.Size = new System.Drawing.Size(210, 24);
		this.subscribeArtistMenuItem.Text = "收藏歌手(&C)";
		this.subscribeArtistMenuItem.Visible = false;
		this.subscribeArtistMenuItem.Click += new System.EventHandler(subscribeArtistMenuItem_Click);
		this.unsubscribeArtistMenuItem.Name = "unsubscribeArtistMenuItem";
		this.unsubscribeArtistMenuItem.Size = new System.Drawing.Size(210, 24);
		this.unsubscribeArtistMenuItem.Text = "取消收藏歌手(&Z)";
		this.unsubscribeArtistMenuItem.Visible = false;
		this.unsubscribeArtistMenuItem.Click += new System.EventHandler(unsubscribeArtistMenuItem_Click);
		this.toolStripSeparatorDownload3.Name = "toolStripSeparatorDownload3";
		this.toolStripSeparatorDownload3.Size = new System.Drawing.Size(207, 6);
		this.downloadSongMenuItem.Name = "downloadSongMenuItem";
		this.downloadSongMenuItem.Size = new System.Drawing.Size(210, 24);
		this.downloadSongMenuItem.ShortcutKeys = System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.Return;
		this.downloadSongMenuItem.Text = "下载歌曲(&D)";
		this.downloadSongMenuItem.Click += new System.EventHandler(DownloadSong_Click);
		this.downloadLyricsMenuItem.Name = "downloadLyricsMenuItem";
		this.downloadLyricsMenuItem.Size = new System.Drawing.Size(210, 24);
		this.downloadLyricsMenuItem.Text = "下载歌词(&L)";
		this.downloadLyricsMenuItem.Click += new System.EventHandler(DownloadLyrics_Click);
		this.downloadPlaylistMenuItem.Name = "downloadPlaylistMenuItem";
		this.downloadPlaylistMenuItem.Size = new System.Drawing.Size(210, 24);
		this.downloadPlaylistMenuItem.ShortcutKeys = System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.Return;
		this.downloadPlaylistMenuItem.Text = "下载歌单(&D)";
		this.downloadPlaylistMenuItem.Click += new System.EventHandler(DownloadPlaylist_Click);
		this.downloadAlbumMenuItem.Name = "downloadAlbumMenuItem";
		this.downloadAlbumMenuItem.Size = new System.Drawing.Size(210, 24);
		this.downloadAlbumMenuItem.ShortcutKeys = System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.Return;
		this.downloadAlbumMenuItem.Text = "下载专辑(&D)";
		this.downloadAlbumMenuItem.Click += new System.EventHandler(DownloadAlbum_Click);
		this.downloadPodcastMenuItem.Name = "downloadPodcastMenuItem";
		this.downloadPodcastMenuItem.Size = new System.Drawing.Size(210, 24);
		this.downloadPodcastMenuItem.ShortcutKeys = System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.Return;
		this.downloadPodcastMenuItem.Text = "下载播客全部节目(&R)...";
		this.downloadPodcastMenuItem.Click += new System.EventHandler(DownloadPodcast_Click);
		this.batchDownloadMenuItem.Name = "batchDownloadMenuItem";
		this.batchDownloadMenuItem.Size = new System.Drawing.Size(210, 24);
		this.batchDownloadMenuItem.Text = "批量下载(&B)...";
		this.batchDownloadMenuItem.Click += new System.EventHandler(BatchDownloadSongs_Click);
		this.downloadCategoryMenuItem.Name = "downloadCategoryMenuItem";
		this.downloadCategoryMenuItem.Size = new System.Drawing.Size(210, 24);
		this.downloadCategoryMenuItem.ShortcutKeys = System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.Return;
		this.downloadCategoryMenuItem.Text = "下载分类(&C)...";
		this.downloadCategoryMenuItem.Click += new System.EventHandler(DownloadCategory_Click);
		this.batchDownloadPlaylistsMenuItem.Name = "batchDownloadPlaylistsMenuItem";
		this.batchDownloadPlaylistsMenuItem.Size = new System.Drawing.Size(210, 24);
		this.batchDownloadPlaylistsMenuItem.Text = "批量下载(&B)...";
		this.batchDownloadPlaylistsMenuItem.Click += new System.EventHandler(BatchDownloadPlaylistsOrAlbums_Click);
		this.trayContextMenu.ImageScalingSize = new System.Drawing.Size(20, 20);
		this.trayContextMenu.Items.AddRange(this.trayShowMenuItem, this.trayPlayPauseMenuItem, this.trayPrevMenuItem, this.trayNextMenuItem, this.trayMenuSeparator, this.trayExitMenuItem);
		this.trayContextMenu.Name = "trayContextMenu";
		this.trayContextMenu.Size = new System.Drawing.Size(161, 136);
		this.trayShowMenuItem.Name = "trayShowMenuItem";
		this.trayShowMenuItem.Size = new System.Drawing.Size(160, 24);
		this.trayShowMenuItem.Text = "显示易听(&S)";
		this.trayShowMenuItem.Click += new System.EventHandler(trayShowMenuItem_Click);
		this.trayPlayPauseMenuItem.Name = "trayPlayPauseMenuItem";
		this.trayPlayPauseMenuItem.Size = new System.Drawing.Size(160, 24);
		this.trayPlayPauseMenuItem.Text = "播放/暂停(&P)";
		this.trayPlayPauseMenuItem.Click += new System.EventHandler(trayPlayPauseMenuItem_Click);
		this.trayPrevMenuItem.Name = "trayPrevMenuItem";
		this.trayPrevMenuItem.Size = new System.Drawing.Size(160, 24);
		this.trayPrevMenuItem.Text = "上一首(&R)";
		this.trayPrevMenuItem.Click += new System.EventHandler(trayPrevMenuItem_Click);
		this.trayNextMenuItem.Name = "trayNextMenuItem";
		this.trayNextMenuItem.Size = new System.Drawing.Size(160, 24);
		this.trayNextMenuItem.Text = "下一首(&N)";
		this.trayNextMenuItem.Click += new System.EventHandler(trayNextMenuItem_Click);
		this.trayMenuSeparator.Name = "trayMenuSeparator";
		this.trayMenuSeparator.Size = new System.Drawing.Size(157, 6);
		this.trayExitMenuItem.Name = "trayExitMenuItem";
		this.trayExitMenuItem.Size = new System.Drawing.Size(160, 24);
		this.trayExitMenuItem.Text = "退出(&X)";
		this.trayExitMenuItem.Click += new System.EventHandler(trayExitMenuItem_Click);
		base.AutoScaleDimensions = new System.Drawing.SizeF(120f, 120f);
		base.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Dpi;
		base.ClientSize = new System.Drawing.Size(1200, 676);
        base.Controls.Add(this.resultListPanel);
		base.Controls.Add(this.searchPanel);
		base.Controls.Add(this.controlPanel);
		base.Controls.Add(this.statusStrip1);
		base.Controls.Add(this.menuStrip1);
		this.Font = new System.Drawing.Font("Microsoft YaHei UI", 10.5f);
		base.MainMenuStrip = this.menuStrip1;
		this.MinimumSize = new System.Drawing.Size(1000, 700);
		base.Name = "MainForm";
		base.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
		this.Text = "易听";
		this.menuStrip1.ResumeLayout(false);
		this.menuStrip1.PerformLayout();
		this.searchPanel.ResumeLayout(false);
		this.searchPanel.PerformLayout();
		this.searchTextContextMenu.ResumeLayout(false);
		this.resultListPanel.ResumeLayout(false);
		this.controlPanel.ResumeLayout(false);
		this.controlPanel.PerformLayout();
		((System.ComponentModel.ISupportInitialize)this.volumeTrackBar).EndInit();
		((System.ComponentModel.ISupportInitialize)this.progressTrackBar).EndInit();
		this.statusStrip1.ResumeLayout(false);
		this.statusStrip1.PerformLayout();
		this.songContextMenu.ResumeLayout(false);
		this.trayContextMenu.ResumeLayout(false);
		base.ResumeLayout(false);
		base.PerformLayout();
	}

	private async Task<T> FetchWithRetryUntilCancel<T>(Func<CancellationToken, Task<T>> operation, string operationName, CancellationToken cancellationToken, Action<int, Exception>? onRetry = null, int? maxAttempts = null, Action<int>? onRecovery = null, Action<int, Exception>? onGiveUp = null)
	{
		if (operation == null)
		{
			throw new ArgumentNullException("operation");
		}
		if (string.IsNullOrWhiteSpace(operationName))
		{
			operationName = "fetch";
		}
		int attempt = 0;
		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();
			try
			{
				T result = await operation(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				if (attempt > 0)
				{
					onRecovery?.Invoke(attempt);
				}
				return result;
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex2)
			{
				if (IsNonRecoverableFetchError(ex2))
				{
					onGiveUp?.Invoke(attempt, ex2);
					throw;
				}
				attempt = checked(attempt + 1);
				if (maxAttempts.HasValue && attempt >= maxAttempts.Value)
				{
					onGiveUp?.Invoke(attempt, ex2);
					throw;
				}
				onRetry?.Invoke(attempt, ex2);
				int delayMs;
				lock (_fetchRetryRandom)
				{
					delayMs = _fetchRetryRandom.Next(500, 1501);
				}
				Debug.WriteLine($"[Retry] {operationName} 失败（尝试 {attempt}）：{ex2.Message}，延迟 {delayMs}ms 后重试");
				try
				{
					await Task.Delay(delayMs, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				catch (OperationCanceledException)
				{
					onGiveUp?.Invoke(attempt, ex2);
					throw;
				}
			}
		}
	}

	private static bool IsNonRecoverableFetchError(Exception ex)
	{
		if (ex is UnauthorizedAccessException)
		{
			return true;
		}
		if (ex is ArgumentException)
		{
			return true;
		}
		string text = ex.Message ?? string.Empty;
		if (text.IndexOf("不存在", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("下架", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("被移除", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("版权", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			return true;
		}
		if (text.IndexOf("请求参数错误", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("参数错误", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			return true;
		}
		return false;
	}
}
}










