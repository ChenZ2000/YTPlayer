#define DEBUG
#nullable disable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using YTPlayer.Core;
using YTPlayer.Core.Download;
using YTPlayer.Core.Lyrics;
using YTPlayer.Core.Playback;
using YTPlayer.Core.Playback.Cache;
using YTPlayer.Core.Recognition;
using YTPlayer.Core.Streaming;
using YTPlayer.Core.Upload;
using YTPlayer.Forms;
using YTPlayer.Forms.Download;
using YTPlayer.Models;
using YTPlayer.Models.Auth;
using YTPlayer.Models.Download;
using YTPlayer.Models.Upload;
using YTPlayer.Update;
using YTPlayer.Utils;
#pragma warning disable CS8600, CS8601, CS8602, CS8603, CS8604, CS8618, CS8625, CS8632, CS4014

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

	private struct RECT
	{
		public int left;

		public int top;

		public int right;

		public int bottom;
	}

	private struct COMBOBOXINFO
	{
		public int cbSize;

		public RECT rcItem;

		public RECT rcButton;

		public int stateButton;

		public IntPtr hwndCombo;

		public IntPtr hwndItem;

		public IntPtr hwndList;
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

	private sealed class SortState<T>
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

	protected NeteaseApiClient _apiClient = null;

	private BassAudioEngine _audioEngine = null;

	private SeekManager _seekManager = null;

	protected ConfigManager _configManager = null;

	private ConfigModel _config = null;

	private AccountState _accountState = null;

	protected List<SongInfo> _currentSongs = new List<SongInfo>();

	private List<PlaylistInfo> _currentPlaylists = new List<PlaylistInfo>();

	private PlaylistInfo? _currentPlaylist = null;
	private bool _currentPlaylistOwnedByUser = false;

	private PlaylistInfo? _userLikedPlaylist = null;

	private List<AlbumInfo> _currentAlbums = new List<AlbumInfo>();

	private List<PodcastRadioInfo> _currentPodcasts = new List<PodcastRadioInfo>();

	private List<PodcastEpisodeInfo> _currentPodcastSounds = new List<PodcastEpisodeInfo>();

	private PodcastRadioInfo? _currentPodcast = null;

	private int _currentPodcastSoundOffset = 0;

	private bool _currentPodcastHasMore = false;

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

	private readonly Dictionary<string, int> _homeItemIndexMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

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

	private bool _isCurrentPlayingMenuActive = false;

	private SongInfo? _currentPlayingMenuSong;

	private int _lastListViewFocusedIndex = -1;

	private string _lastKeyword = "";

	private readonly PlaybackQueueManager _playbackQueue = new PlaybackQueueManager();

	private bool _suppressAutoAdvance = false;

	private string _currentViewSource = "";

	private const string MixedSearchTypeDisplayName = "混合";

	private bool _isMixedSearchTypeActive = false;

	private string _lastExplicitSearchType = "歌曲";

	private string? _currentMixedQueryKey = null;

	private CancellationTokenSource? _initialHomeLoadCts;
	private bool _initialHomeLoadCompleted = false;

	private int _autoFocusSuppressionDepth = 0;

	private const int InitialHomeRetryDelayMs = 1500;

	private long _loggedInUserId = 0L;

	private bool _isHomePage = false;

	private Stack<NavigationHistoryItem> _navigationHistory = new Stack<NavigationHistoryItem>();

	private DateTime _lastBackTime = DateTime.MinValue;

	private const int MIN_BACK_INTERVAL_MS = 300;

	private bool _isNavigating = false;

	private const string BaseWindowTitle = "易听";

	private CancellationTokenSource? _availabilityCheckCts;

	private CancellationTokenSource? _searchCts;

	private const int CloudPageSize = 50;

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

	private const int SONG_URL_TIMEOUT_MS = 12000;

	private const int INITIAL_RETRY_DELAY_MS = 1200;

	private const int MAX_RETRY_DELAY_MS = 5000;

	private const int SONG_URL_CACHE_MINUTES = 30;

	private const int RecentPlayFetchLimit = 300;

	private const int RecentPlaylistFetchLimit = 100;

	private const int RecentAlbumFetchLimit = 100;

	private const int RecentPodcastFetchLimit = 100;

	private const int PodcastSoundPageSize = 50;

	private static readonly char[] MultiUrlSeparators = new char[2] { ';', '；' };

	private readonly Dictionary<LibraryEntityType, DateTime> _libraryCacheTimestamps = new Dictionary<LibraryEntityType, DateTime>
	{
		[LibraryEntityType.Songs] = DateTime.MinValue,
		[LibraryEntityType.Playlists] = DateTime.MinValue,
		[LibraryEntityType.Albums] = DateTime.MinValue,
		[LibraryEntityType.Artists] = DateTime.MinValue,
		[LibraryEntityType.Podcasts] = DateTime.MinValue
	};

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

	private int _currentArtistAlbumsTotalCount;

	private SortState<ArtistSongSortOption> _artistSongSortState = new SortState<ArtistSongSortOption>(ArtistSongSortOption.Hot, new Dictionary<ArtistSongSortOption, string>
	{
		{
			ArtistSongSortOption.Hot,
			"当前排序：按热门"
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

	private readonly Dictionary<long, (int MusicCount, int AlbumCount)> _artistStatsCache = new Dictionary<long, (int, int)>();

	private readonly HashSet<long> _artistStatsInFlight = new HashSet<long>();

	private CancellationTokenSource? _artistStatsRefreshCts;

	private const int ArtistSongsPageSize = 100;

	private const int ArtistAlbumsPageSize = 100;

	private SongRecognitionCoordinator? _songRecognitionCoordinator;

	private ToolStripMenuItem? _listenRecognitionMenuItem;

	private readonly object _viewContentLock = new object();

	private CancellationTokenSource? _viewContentCts;

	private CancellationTokenSource? _navigationCts;

	private string? _pendingListFocusViewSource;

	private int _pendingListFocusIndex = -1;

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

	private ToolStripMenuItem helpMenuItem;

	private ToolStripMenuItem checkUpdateMenuItem;

	private ToolStripMenuItem donateMenuItem;

	private ToolStripMenuItem shortcutsMenuItem;

	private ToolStripMenuItem aboutMenuItem;

	private Panel searchPanel;

	private ComboBox searchTypeComboBox;

	private Label searchTypeLabel;

	private Button searchButton;

	private TextBox searchTextBox;

	private Label searchLabel;

	private ListView resultListView;

	private ColumnHeader columnHeader1;

	private ColumnHeader columnHeader2;

	private ColumnHeader columnHeader3;

	private ColumnHeader columnHeader4;

	private ColumnHeader columnHeader5;

	private Panel controlPanel;

	private Label lyricsLabel;

	private Label volumeLabel;

	private TrackBar volumeTrackBar;

	private Label timeLabel;

	private TrackBar progressTrackBar;

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
							Debug.WriteLine("[StateCache] 获取状态异常: " + ex2.Message);
						}
						lock (_stateCacheLock)
						{
							_cachedPosition = position;
							_cachedDuration = duration;
							_cachedPlaybackState = state;
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
				Exception ex3 = ex;
				Debug.WriteLine("[StateCache ERROR] 状态更新循环异常: " + ex3.Message);
			}
			finally
			{
				_stateUpdateLoopRunning = false;
				Debug.WriteLine("[StateCache] 状态更新循环已停止");
			}
		});
	}

	private HomePageViewData BuildHomePageSkeletonViewData()
	{
		bool flag = _accountState?.IsLoggedIn ?? false;
		List<ListItemInfo> list = new List<ListItemInfo>();
		int value = _homePlaylistCategoryPresets.Length;
		int count = ArtistMetadataHelper.GetTypeOptions().Count;
		checked
		{
			if (flag)
			{
				ListItemInfo obj = new ListItemInfo
				{
					Type = ListItemType.Category,
					CategoryId = "user_liked_songs",
					CategoryName = "喜欢的音乐",
					ItemCount = (_userLikedPlaylist?.TrackCount > 0) ? _userLikedPlaylist?.TrackCount : null
				};
				PlaylistInfo? userLikedPlaylist = _userLikedPlaylist;
				obj.ItemUnit = ((userLikedPlaylist != null && userLikedPlaylist.TrackCount > 0) ? "首" : null);
				list.Add(obj);
				list.Add(new ListItemInfo
				{
					Type = ListItemType.Category,
					CategoryId = "recent_listened",
					CategoryName = "最近听过",
					CategoryDescription = (string.IsNullOrWhiteSpace(BuildRecentListenedDescription()) ? null : BuildRecentListenedDescription())
				});
				list.Add(new ListItemInfo
				{
					Type = ListItemType.Category,
					CategoryId = "user_playlists",
					CategoryName = "我的歌单",
					ItemCount = _homeCachedUserPlaylistCount,
					ItemUnit = (_homeCachedUserPlaylistCount.HasValue ? "个" : null)
				});
				list.Add(new ListItemInfo
				{
					Type = ListItemType.Category,
					CategoryId = "user_albums",
					CategoryName = "收藏的专辑",
					ItemCount = _homeCachedUserAlbumCount,
					ItemUnit = (_homeCachedUserAlbumCount.HasValue ? "张" : null)
				});
				list.Add(new ListItemInfo
				{
					Type = ListItemType.Category,
					CategoryId = "artist_favorites",
					CategoryName = "收藏的歌手",
					ItemCount = _homeCachedArtistFavoritesCount,
					ItemUnit = (_homeCachedArtistFavoritesCount.HasValue ? "位" : null)
				});
				list.Add(new ListItemInfo
				{
					Type = ListItemType.Category,
					CategoryId = "user_podcasts",
					CategoryName = "收藏的播客",
					ItemCount = _homeCachedPodcastFavoritesCount,
					ItemUnit = (_homeCachedPodcastFavoritesCount.HasValue ? "个" : null)
				});
				list.Add(new ListItemInfo
				{
					Type = ListItemType.Category,
					CategoryId = "user_cloud",
					CategoryName = "云盘",
					CategoryDescription = ((_cloudMaxSize > 0 || _cloudTotalCount > 0) ? ("已用 " + FormatSize(_cloudUsedSize) + " / " + FormatSize(_cloudMaxSize)) : null),
					ItemCount = ((_cloudTotalCount > 0) ? new int?(_cloudTotalCount) : ((int?)null)),
					ItemUnit = ((_cloudTotalCount > 0) ? "首" : null)
				});
				list.Add(new ListItemInfo
				{
					Type = ListItemType.Category,
					CategoryId = "daily_recommend",
					CategoryName = "每日推荐",
					CategoryDescription = ((_homeCachedDailyRecommendSongCount.HasValue || _homeCachedDailyRecommendPlaylistCount.HasValue) ? $"歌曲 {_homeCachedDailyRecommendSongCount ?? 0} 首 / 歌单 {_homeCachedDailyRecommendPlaylistCount ?? 0} 个" : null),
					ItemCount = (_homeCachedDailyRecommendSongCount.HasValue || _homeCachedDailyRecommendPlaylistCount.HasValue) ? (_homeCachedDailyRecommendSongCount.GetValueOrDefault() + _homeCachedDailyRecommendPlaylistCount.GetValueOrDefault()) : ((int?)null),
					ItemUnit = ((_homeCachedDailyRecommendSongCount.HasValue || _homeCachedDailyRecommendPlaylistCount.HasValue) ? "项" : null)
				});
				list.Add(new ListItemInfo
				{
					Type = ListItemType.Category,
					CategoryId = "personalized",
					CategoryName = "为您推荐",
					CategoryDescription = ((_homeCachedPersonalizedPlaylistCount.HasValue || _homeCachedPersonalizedSongCount.HasValue) ? $"歌单 {_homeCachedPersonalizedPlaylistCount ?? 0} 个 / 歌曲 {_homeCachedPersonalizedSongCount ?? 0} 首" : null),
					ItemCount = (_homeCachedPersonalizedPlaylistCount.HasValue || _homeCachedPersonalizedSongCount.HasValue) ? (_homeCachedPersonalizedPlaylistCount.GetValueOrDefault() + _homeCachedPersonalizedSongCount.GetValueOrDefault()) : ((int?)null),
					ItemUnit = ((_homeCachedPersonalizedPlaylistCount.HasValue || _homeCachedPersonalizedSongCount.HasValue) ? "项" : null)
				});
			}
			list.Add(new ListItemInfo
			{
				Type = ListItemType.Category,
				CategoryId = "highquality_playlists",
				CategoryName = "精品歌单",
				ItemCount = 50,
				ItemUnit = "个"
			});
			list.Add(new ListItemInfo
			{
				Type = ListItemType.Category,
				CategoryId = "new_songs",
				CategoryName = "新歌速递",
				ItemCount = 5,
				ItemUnit = "类"
			});
			list.Add(new ListItemInfo
			{
				Type = ListItemType.Category,
				CategoryId = "playlist_category",
				CategoryName = "歌单分类",
				ItemCount = value,
				ItemUnit = "类"
			});
			list.Add(new ListItemInfo
			{
				Type = ListItemType.Category,
				CategoryId = "artist_categories",
				CategoryName = "歌手分类",
				ItemCount = count,
				ItemUnit = "类"
			});
			list.Add(new ListItemInfo
			{
				Type = ListItemType.Category,
				CategoryId = "new_albums",
				CategoryName = "新碟上架",
				ItemCount = _homeCachedNewAlbumCount,
				ItemUnit = (_homeCachedNewAlbumCount.HasValue ? "张" : null)
			});
			list.Add(new ListItemInfo
			{
				Type = ListItemType.Category,
				CategoryId = "toplist",
				CategoryName = "官方榜单",
				ItemCount = _homeCachedToplistCount,
				ItemUnit = (_homeCachedToplistCount.HasValue ? "个" : null)
			});
			string statusText = (flag ? "主页骨架已加载，正在同步数据..." : "欢迎使用，登录后解锁更多入口");
			return new HomePageViewData(list, statusText);
		}
	}

	private async Task EnrichHomePageAsync(bool isInitialLoad, CancellationToken externalToken)
	{
		CancellationToken viewToken = GetCurrentViewContentToken();
		CancellationTokenSource linkedCts = null;
		CancellationToken effectiveToken = LinkCancellationTokens(viewToken, externalToken, out linkedCts);
		try
		{
			await RunHomeProvidersAsync(isInitialLoad, effectiveToken).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			if (!TryHandleOperationCancelled(ex2, "主页丰富已取消"))
			{
				Debug.WriteLine($"[HomePage] 丰富失败: {ex2}");
			}
		}
		finally
		{
			linkedCts?.Dispose();
		}
	}

	private static bool IsRecommendationCacheFresh(DateTime fetchedUtc)
	{
		return fetchedUtc != DateTime.MinValue && DateTime.UtcNow - fetchedUtc < RecommendationCacheTtl;
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
		UpdateWindowTitle(null);
		if (songContextMenu != null)
		{
			songContextMenu.ShowCheckMargin = true;
		}
		EnsureSortMenuCheckMargins();
		InitializeServices();
		SetupEventHandlers();
		LoadConfig();
		_trayIcon = new NotifyIcon();
		_trayIcon.Icon = base.Icon;
		_trayIcon.Text = "易听";
		_trayIcon.Visible = true;
		_trayIcon.MouseClick += TrayIcon_MouseClick;
		_trayIcon.DoubleClick += TrayIcon_DoubleClick;
		_contextMenuHost = new ContextMenuHost();
		trayContextMenu.Opening += TrayContextMenu_Opening;
		trayContextMenu.Opened += TrayContextMenu_Opened;
		trayContextMenu.Closed += TrayContextMenu_Closed;
		SyncPlayPauseButtonText();
		base.Load += MainForm_Load;
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
				Debug.WriteLine("[MainForm] 热身失败（忽略）: " + ex2.Message);
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
			ApplyAccountStateOnStartup();
			string preferredDeviceId = _config?.OutputDevice;
			_audioEngine = new BassAudioEngine(preferredDeviceId);
			if (_config != null && !string.Equals(_config.OutputDevice, _audioEngine.ActiveOutputDeviceId, StringComparison.OrdinalIgnoreCase))
			{
				_config.OutputDevice = _audioEngine.ActiveOutputDeviceId;
				_configManager?.Save(_config);
			}
			_audioEngine.BufferingStateChanged += OnBufferingStateChanged;
			_lyricsCacheManager = new LyricsCacheManager();
			_lyricsDisplayManager = new LyricsDisplayManager(_lyricsCacheManager);
			_lyricsLoader = new LyricsLoader(_apiClient);
			_lyricsDisplayManager.LyricUpdated += OnLyricUpdated;
			_audioEngine.PositionChanged += OnAudioPositionChanged;
			_nextSongPreloader = new NextSongPreloader(_apiClient);
			_seekManager = new SeekManager(_audioEngine);
			_seekManager.SeekCompleted += OnSeekCompleted;
			_updateTimer = new System.Windows.Forms.Timer();
			_updateTimer.Interval = 100;
			_updateTimer.Tick += UpdateTimer_Tick;
			_updateTimer.Start();
			_scrubKeyTimer = new System.Windows.Forms.Timer();
			_scrubKeyTimer.Interval = 200;
			_scrubKeyTimer.Tick += ScrubKeyTimer_Tick;
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
		try
		{
			autoReadLyricsMenuItem.Checked = _autoReadLyrics;
			autoReadLyricsMenuItem.Text = (_autoReadLyrics ? "关闭歌词朗读\tF11" : "打开歌词朗读\tF11");
		}
		catch
		{
		}
		Debug.WriteLine($"[CONFIG] LyricsReadingEnabled={_autoReadLyrics}");
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

	private bool IsUserLoggedIn()
	{
		if (_accountState?.IsLoggedIn == true)
		{
			return true;
		}
		if (_apiClient?.UsePersonalCookie == true)
		{
			// 只有拿到有效用户ID才视为已登录，避免“看起来登录但没有用户ID”的假阳性
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

	private void ClearLoginState(bool persist)
	{
		_apiClient?.ClearCookies();
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
		long parsedId = ParseUserIdFromAccountState(_accountState);
		if (parsedId > 0)
		{
			CacheLoggedInUserId(parsedId);
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
				int num = ((!volumeTrackBar.InvokeRequired) ? volumeTrackBar.Value : ((!volumeTrackBar.IsHandleCreated) ? volumeTrackBar.Value : ((int)volumeTrackBar.Invoke((Func<int>)(() => volumeTrackBar.Value)))));
				configModel.Volume = (double)num / 100.0;
			}
			configModel.LyricsReadingEnabled = _autoReadLyrics;
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
					BeginInvoke(new Action(UpdateLoginMenuItemText));
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

	protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
	{
		if (keyData == Keys.Return)
		{
			TextBox textBox = searchTextBox;
			if (textBox == null || !textBox.ContainsFocus)
			{
				ComboBox comboBox = searchTypeComboBox;
				if (comboBox == null || !comboBox.ContainsFocus)
				{
					goto IL_0047;
				}
			}
			PerformSearch();
			return true;
		}
		goto IL_0047;
		IL_0047:
		return base.ProcessCmdKey(ref msg, keyData);
	}

	private void searchTextBox_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.KeyCode == Keys.Return)
		{
			e.Handled = true;
			e.SuppressKeyPress = true;
		}
	}

	private void searchTypeComboBox_KeyDown(object sender, KeyEventArgs e)
	{
		if (_isMixedSearchTypeActive && (e.KeyCode == Keys.Down || e.KeyCode == Keys.Up))
		{
			e.Handled = true;
			e.SuppressKeyPress = true;
			if (searchTypeComboBox.Items.Count > 0)
			{
				int index = ((e.KeyCode != Keys.Down) ? checked(searchTypeComboBox.Items.Count - 1) : 0);
				string targetType = searchTypeComboBox.Items[index]?.ToString() ?? _lastExplicitSearchType;
				DeactivateMixedSearchTypeOption(targetType);
			}
		}
		else if (e.KeyCode == Keys.Return)
		{
			e.Handled = true;
			e.SuppressKeyPress = true;
		}
	}

	private string GetSelectedSearchType()
	{
		if (_isMixedSearchTypeActive)
		{
			return _lastExplicitSearchType;
		}
		if (searchTypeComboBox.SelectedIndex >= 0 && searchTypeComboBox.SelectedIndex < searchTypeComboBox.Items.Count)
		{
			string text = searchTypeComboBox.Items[searchTypeComboBox.SelectedIndex]?.ToString();
			if (!string.IsNullOrWhiteSpace(text))
			{
				return text;
			}
		}
		string text2 = searchTypeComboBox.Text;
		if (!string.IsNullOrWhiteSpace(text2))
		{
			return text2.Trim();
		}
		return _lastExplicitSearchType;
	}

	private string NormalizeSearchTypeName(string? searchType)
	{
		string text = (string.IsNullOrWhiteSpace(searchType) ? "歌曲" : searchType.Trim());
		if (string.Equals(text, "混合", StringComparison.OrdinalIgnoreCase))
		{
			text = (string.IsNullOrWhiteSpace(_lastExplicitSearchType) ? "歌曲" : _lastExplicitSearchType);
		}
		switch (text)
		{
		case "歌单":
		case "专辑":
		case "歌手":
		case "播客":
		case "歌曲":
			return text;
		default:
			return "歌曲";
		}
	}

	private void EnsureSearchTypeSelection(string searchType)
	{
		if (string.IsNullOrWhiteSpace(searchType))
		{
			return;
		}
		if (string.Equals(searchType, "混合", StringComparison.OrdinalIgnoreCase))
		{
			ActivateMixedSearchTypeOption();
			return;
		}
		_lastExplicitSearchType = searchType;
		if (_isMixedSearchTypeActive)
		{
			_isMixedSearchTypeActive = false;
		}
		int num = searchTypeComboBox.Items.IndexOf(searchType);
		if (num >= 0)
		{
			if (searchTypeComboBox.SelectedIndex != num)
			{
				searchTypeComboBox.SelectedIndex = num;
			}
			else
			{
				UpdateSearchTypeAccessibleAnnouncement(searchType);
			}
		}
		else
		{
			searchTypeComboBox.SelectedIndex = -1;
			searchTypeComboBox.Text = searchType;
			UpdateSearchTypeAccessibleAnnouncement(searchType);
		}
	}

	private void ActivateMixedSearchTypeOption()
	{
		_isMixedSearchTypeActive = true;
		searchTypeComboBox.SelectedIndex = -1;
		searchTypeComboBox.Text = "混合";
		UpdateSearchTypeAccessibleAnnouncement("混合");
	}

	private void DeactivateMixedSearchTypeOption(string? targetType = null)
	{
		if (_isMixedSearchTypeActive)
		{
			_isMixedSearchTypeActive = false;
			string value = targetType ?? _lastExplicitSearchType;
			if (string.IsNullOrWhiteSpace(value))
			{
				value = (_lastExplicitSearchType = "歌曲");
			}
			int num = searchTypeComboBox.Items.IndexOf(value);
			if (num >= 0)
			{
				searchTypeComboBox.SelectedIndex = num;
				return;
			}
			searchTypeComboBox.SelectedIndex = -1;
			searchTypeComboBox.Text = value;
			UpdateSearchTypeAccessibleAnnouncement(value);
		}
	}

	private void UpdateSearchTypeAccessibleAnnouncement(string? text)
	{
		string accessibleName = (string.IsNullOrEmpty(text) ? "类型" : ("类型" + text));
		searchTypeComboBox.AccessibleName = accessibleName;
		AccessibilityNotifyClients(AccessibleEvents.NameChange, -1);
		AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
		AccessibilityNotifyClients(AccessibleEvents.Selection, -1);
	}

	private static List<string> SplitMultiSearchInput(string? rawInput)
	{
		if (string.IsNullOrWhiteSpace(rawInput))
		{
			return new List<string>();
		}
		return (from part in rawInput.Split(MultiUrlSeparators, StringSplitOptions.RemoveEmptyEntries)
			select part.Trim() into part
			where !string.IsNullOrEmpty(part)
			select part).ToList();
	}

	private bool TryParseMultiUrlInput(List<string> segments, out List<NeteaseUrlMatch> matches, out string errorMessage)
	{
		matches = new List<NeteaseUrlMatch>();
		errorMessage = string.Empty;
		if (segments == null || segments.Count == 0)
		{
			return false;
		}
		List<string> list = new List<string>();
		foreach (string segment in segments)
		{
			if (!NeteaseUrlParser.TryParse(segment, out NeteaseUrlMatch match) || match == null)
			{
				list.Add(segment);
			}
			else
			{
				matches.Add(match);
			}
		}
		if (list.Count > 0)
		{
			IEnumerable<string> values = list.Take(5).Select((string value, int index) => $"{checked(index + 1)}. {value}");
			string text = ((list.Count > 5) ? "\n..." : string.Empty);
			errorMessage = "以下链接无法解析：\n" + string.Join("\n", values) + text;
			matches.Clear();
			return false;
		}
		return matches.Count > 0;
	}

	private string ResolveSearchTypeForMatches(IReadOnlyCollection<NeteaseUrlMatch> matches)
	{
		if (matches == null || matches.Count == 0)
		{
			return "歌曲";
		}
		List<NeteaseUrlType> list = matches.Select((NeteaseUrlMatch m) => m.Type).Distinct().Take(2)
			.ToList();
		if (list.Count > 1)
		{
			return "混合";
		}
		return MapUrlTypeToSearchType(list[0]);
	}

	private void ApplySearchTypeDisplayForMatches(IReadOnlyCollection<NeteaseUrlMatch> matches)
	{
		string text = ResolveSearchTypeForMatches(matches);
		if (string.Equals(text, "混合", StringComparison.OrdinalIgnoreCase))
		{
			ActivateMixedSearchTypeOption();
		}
		else
		{
			EnsureSearchTypeSelection(text);
		}
	}

	private string BuildMixedQueryKey(IEnumerable<NeteaseUrlMatch> matches)
	{
		if (matches == null)
		{
			return string.Empty;
		}
		return string.Join(";", matches.Select((NeteaseUrlMatch m) => $"{(int)m.Type}:{m.ResourceId}"));
	}

	private bool TryParseMixedQueryKey(string? key, out List<NeteaseUrlMatch> matches)
	{
		matches = new List<NeteaseUrlMatch>();
		if (string.IsNullOrWhiteSpace(key))
		{
			return false;
		}
		string[] array = key.Split(new char[1] { ';' }, StringSplitOptions.RemoveEmptyEntries);
		string[] array2 = array;
		foreach (string text in array2)
		{
			string[] array3 = text.Split(new char[1] { ':' }, 2);
			if (array3.Length != 2)
			{
				return false;
			}
			if (!int.TryParse(array3[0], out var result) || !Enum.IsDefined(typeof(NeteaseUrlType), result))
			{
				return false;
			}
			string text2 = array3[1];
			matches.Add(new NeteaseUrlMatch((NeteaseUrlType)result, text2, text2));
		}
		return matches.Count > 0;
	}

	private static string GetEntityDisplayName(NeteaseUrlType type)
	{
		return type switch
		{
			NeteaseUrlType.Playlist => "歌单", 
			NeteaseUrlType.Album => "专辑", 
			NeteaseUrlType.Artist => "歌手", 
			NeteaseUrlType.Podcast => "播客", 
			NeteaseUrlType.PodcastEpisode => "播客节目", 
			_ => "歌曲", 
		};
	}

	private async Task<List<SongInfo>> FetchRecentSongsAsync(int limit, CancellationToken cancellationToken = default(CancellationToken))
	{
		return await ExecuteWithRetryAsync(async () => (await _apiClient.GetRecentPlayedSongsAsync(limit)) ?? new List<SongInfo>(), 3, 600, "RecentSongs", cancellationToken);
	}

	private async Task<List<PlaylistInfo>> FetchRecentPlaylistsAsync(int limit, CancellationToken cancellationToken = default(CancellationToken))
	{
		return await ExecuteWithRetryAsync(async () => (await _apiClient.GetRecentPlaylistsAsync(limit)) ?? new List<PlaylistInfo>(), 3, 600, "RecentPlaylists", cancellationToken);
	}

	private async Task<List<AlbumInfo>> FetchRecentAlbumsAsync(int limit, CancellationToken cancellationToken = default(CancellationToken))
	{
		return await ExecuteWithRetryAsync(async () => (await _apiClient.GetRecentAlbumsAsync(limit)) ?? new List<AlbumInfo>(), 3, 600, "RecentAlbums", cancellationToken);
	}

	private async Task<List<PodcastRadioInfo>> FetchRecentPodcastsAsync(int limit, CancellationToken cancellationToken = default(CancellationToken))
	{
		return await ExecuteWithRetryAsync(async () => (await _apiClient.GetRecentPodcastsAsync(limit)) ?? new List<PodcastRadioInfo>(), 3, 600, "RecentPodcasts", cancellationToken);
	}

	private async Task RefreshRecentSummariesAsync(bool forceRefresh, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (!IsUserLoggedIn())
		{
			_recentSongsCache.Clear();
			_recentPlaylistsCache.Clear();
			_recentAlbumsCache.Clear();
			_recentPodcastsCache.Clear();
			_recentPlayCount = 0;
			_recentPlaylistCount = 0;
			_recentAlbumCount = 0;
			_recentPodcastCount = 0;
			_recentSummaryLastUpdatedUtc = DateTime.MinValue;
			_recentSummaryReady = false;
		}
		else
		{
			if (!forceRefresh && !(_recentSummaryLastUpdatedUtc == DateTime.MinValue) && !(DateTime.UtcNow - _recentSummaryLastUpdatedUtc > TimeSpan.FromSeconds(30.0)))
			{
				return;
			}
			Task<List<SongInfo>> songsTask = FetchRecentSongsAsync(300, cancellationToken);
			Task<List<PlaylistInfo>> playlistsTask = FetchRecentPlaylistsAsync(100, cancellationToken);
			Task<List<AlbumInfo>> albumsTask = FetchRecentAlbumsAsync(100, cancellationToken);
			Task<List<PodcastRadioInfo>> podcastsTask = FetchRecentPodcastsAsync(100, cancellationToken);
			try
			{
				_recentSongsCache = await songsTask;
			}
			catch (Exception ex)
			{
				Exception ex2 = ex;
				Debug.WriteLine($"[RecentSummary] 获取最近歌曲失败: {ex2}");
				if (forceRefresh)
				{
					_recentSongsCache = new List<SongInfo>();
				}
			}
			_recentPlayCount = _recentSongsCache.Count;
			try
			{
				_recentPlaylistsCache = await playlistsTask;
			}
			catch (Exception ex)
			{
				Exception ex3 = ex;
				Debug.WriteLine($"[RecentSummary] 获取最近歌单失败: {ex3}");
				if (forceRefresh)
				{
					_recentPlaylistsCache = new List<PlaylistInfo>();
				}
			}
			_recentPlaylistCount = _recentPlaylistsCache.Count;
			try
			{
				_recentAlbumsCache = await albumsTask;
			}
			catch (Exception ex)
			{
				Exception ex4 = ex;
				Debug.WriteLine($"[RecentSummary] 获取最近专辑失败: {ex4}");
				if (forceRefresh)
				{
					_recentAlbumsCache = new List<AlbumInfo>();
				}
			}
			_recentAlbumCount = _recentAlbumsCache.Count;
			try
			{
				_recentPodcastsCache = await podcastsTask;
			}
			catch (Exception ex)
			{
				Exception ex5 = ex;
				Debug.WriteLine($"[RecentSummary] 获取最近播客失败: {ex5}");
				if (forceRefresh)
				{
					_recentPodcastsCache = new List<PodcastRadioInfo>();
				}
			}
			_recentPodcastCount = _recentPodcastsCache.Count;
			_recentSummaryLastUpdatedUtc = DateTime.UtcNow;
			_recentSummaryReady = true;
		}
	}

	private bool IsSameSearchContext(string keyword, string searchType, int page)
	{
		ParseSearchViewSource(_currentViewSource, out string searchType2, out string keyword2, out int page2);
		return string.Equals(keyword2, keyword, StringComparison.OrdinalIgnoreCase) && string.Equals(searchType2, searchType, StringComparison.OrdinalIgnoreCase) && page2 == page;
	}

	private SearchViewData<T> BuildSearchSkeletonViewData<T>(string keyword, string searchType, int page, int startIndex, IReadOnlyList<T> cachedItems)
	{
		return new SearchViewData<T>(new List<T>(), 0, hasMore: false, startIndex);
	}

	private string BuildSearchSkeletonStatus(string keyword, int cachedCount, string resourceName)
	{
		return (cachedCount > 0) ? $"正在刷新{resourceName}搜索结果（缓存 {cachedCount} 条）..." : ("正在搜索 " + keyword + "...");
	}

	private async Task PerformSearch()
	{
		string keyword = searchTextBox.Text.Trim();
		if (string.IsNullOrEmpty(keyword))
		{
			MessageBox.Show("请输入搜索关键词", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			searchTextBox.Focus();
			return;
		}
		List<string> multiSegments = SplitMultiSearchInput(keyword);
		List<NeteaseUrlMatch> multiMatches = null;
		bool isMultiUrlSearch = false;
		if (multiSegments.Count > 1)
		{
			if (!TryParseMultiUrlInput(multiSegments, out List<NeteaseUrlMatch> parsedMatches, out string parseError))
			{
				MessageBox.Show(parseError, "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				return;
			}
			if (parsedMatches.Count > 1)
			{
				isMultiUrlSearch = true;
				multiMatches = parsedMatches;
			}
		}
		bool singleUrlSearch = false;
		NeteaseUrlMatch parsedUrl = null;
		if (!isMultiUrlSearch)
		{
			singleUrlSearch = NeteaseUrlParser.TryParse(keyword, out parsedUrl);
		}
		bool isUrlSearch = isMultiUrlSearch || singleUrlSearch;
		string searchType;
		if (isMultiUrlSearch && multiMatches != null)
		{
			searchType = ResolveSearchTypeForMatches(multiMatches);
			ApplySearchTypeDisplayForMatches(multiMatches);
		}
		else if (singleUrlSearch && parsedUrl != null)
		{
			searchType = MapUrlTypeToSearchType(parsedUrl.Type);
			EnsureSearchTypeSelection(searchType);
		}
		else
		{
			searchType = GetSelectedSearchType();
		}
		bool isNewKeyword = !string.Equals(keyword, _lastKeyword, StringComparison.OrdinalIgnoreCase);
		bool isTypeChanged = !string.Equals(searchType, _currentSearchType, StringComparison.OrdinalIgnoreCase);
		bool shouldSaveNavigation = !isUrlSearch && (isNewKeyword || isTypeChanged);
		CancellationTokenSource currentSearchCts = BeginSearchOperation();
		CancellationToken searchToken = currentSearchCts.Token;
		try
		{
			UpdateStatusBar("正在搜索: " + keyword + "...");
			_isHomePage = false;
			if (isMultiUrlSearch && multiMatches != null)
			{
				await HandleMultipleNeteaseUrlSearchAsync(multiMatches, searchToken).ConfigureAwait(continueOnCapturedContext: true);
				_lastKeyword = keyword;
			}
			else if (singleUrlSearch && parsedUrl != null)
			{
				await HandleNeteaseUrlSearchAsync(parsedUrl, searchToken).ConfigureAwait(continueOnCapturedContext: true);
				_lastKeyword = keyword;
			}
			else
			{
				await ExecuteSearchAsync(keyword, searchType, 1, !shouldSaveNavigation, showEmptyPrompt: true, searchToken).ConfigureAwait(continueOnCapturedContext: true);
			}
		}
		catch (OperationCanceledException) when (searchToken.IsCancellationRequested)
		{
			UpdateStatusBar("搜索已取消");
		}
		catch (Exception ex2)
		{
			Exception ex3 = ex2;
			Debug.WriteLine($"[Search] 执行搜索失败: {ex3}");
			MessageBox.Show("搜索失败: " + ex3.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("搜索失败");
		}
		finally
		{
			if (_searchCts == currentSearchCts)
			{
				_searchCts = null;
			}
			currentSearchCts.Dispose();
		}
	}

	private async Task ExecuteSearchAsync(string keyword, string searchType, int page, bool skipSaveNavigation, bool showEmptyPrompt, CancellationToken searchToken, int pendingFocusIndex = -1)
	{
		string normalizedSearchType = NormalizeSearchTypeName(searchType);
		if (!skipSaveNavigation)
		{
			SaveNavigationState();
		}
		_lastKeyword = keyword;
		_currentPage = Math.Max(1, page);
		_currentSearchType = normalizedSearchType;
		_isHomePage = false;
		_currentMixedQueryKey = null;
		EnsureSearchTypeSelection(normalizedSearchType);
		switch (normalizedSearchType)
		{
		case "歌单":
			await ShowPlaylistSearchResultsAsync(keyword, _currentPage, skipSaveNavigation, showEmptyPrompt, searchToken, pendingFocusIndex).ConfigureAwait(continueOnCapturedContext: true);
			break;
		case "专辑":
			await ShowAlbumSearchResultsAsync(keyword, _currentPage, skipSaveNavigation, showEmptyPrompt, searchToken, pendingFocusIndex).ConfigureAwait(continueOnCapturedContext: true);
			break;
		case "歌手":
			await ShowArtistSearchResultsAsync(keyword, _currentPage, skipSaveNavigation, showEmptyPrompt, searchToken, pendingFocusIndex).ConfigureAwait(continueOnCapturedContext: true);
			break;
		case "播客":
			await ShowPodcastSearchResultsAsync(keyword, _currentPage, skipSaveNavigation, showEmptyPrompt, searchToken, pendingFocusIndex).ConfigureAwait(continueOnCapturedContext: true);
			break;
		default:
			await ShowSongSearchResultsAsync(keyword, _currentPage, skipSaveNavigation, showEmptyPrompt, searchToken, pendingFocusIndex).ConfigureAwait(continueOnCapturedContext: true);
			break;
		}
	}

	private async Task ShowSongSearchResultsAsync(string keyword, int page, bool skipSaveNavigation, bool showEmptyPrompt, CancellationToken searchToken, int pendingFocusIndex = -1)
	{
		checked
		{
			int offset = Math.Max(0, (page - 1) * _resultsPerPage);
			string viewSource = $"search:{keyword}:page{page}";
			string accessibleName = "搜索: " + keyword;
			ViewLoadRequest request = new ViewLoadRequest(viewSource, accessibleName, "正在搜索 " + keyword + "...", !skipSaveNavigation, (pendingFocusIndex >= 0) ? pendingFocusIndex : 0);
			ViewLoadResult<SearchViewData<SongInfo>> loadResult = await RunViewLoadAsync(request, delegate
			{
				using (WorkScopes.BeginSkeleton("SearchSong", viewSource))
				{
					return Task.FromResult(BuildSearchSkeletonViewData(keyword, "歌曲", page, offset + 1, _currentSongs));
				}
			}, "搜索歌曲已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				SearchViewData<SongInfo> skeleton = loadResult.Value;
				string statusText = BuildSearchSkeletonStatus(keyword, skeleton.Items.Count, "歌曲");
				DisplaySongs(skeleton.Items, showPagination: false, hasNextPage: false, skeleton.StartIndex, preserveSelection: false, viewSource, accessibleName, skipAvailabilityCheck: true, announceHeader: true, suppressFocus: false, allowSelection: true);
				_currentPlaylist = null;
				UpdateStatusBar(statusText);
				EnrichSongSearchResultsAsync(keyword, page, offset, viewSource, accessibleName, showEmptyPrompt, searchToken, pendingFocusIndex);
			}
		}
	}

	private async Task EnrichSongSearchResultsAsync(string keyword, int page, int offset, string viewSource, string accessibleName, bool showEmptyPrompt, CancellationToken searchToken, int pendingFocusIndex = -1)
	{
		CancellationToken viewToken = GetCurrentViewContentToken();
		checked
		{
			using (WorkScopes.BeginEnrichment("SearchSong", viewSource))
			{
				CancellationTokenSource linkedCts = null;
				CancellationToken effectiveToken = LinkCancellationTokens(viewToken, searchToken, out linkedCts);
				try
				{
					SearchResult<SongInfo> songResult = await FetchWithRetryUntilCancel((CancellationToken ct) => _apiClient.SearchSongsAsync(keyword, _resultsPerPage, offset), $"search:song:{keyword}:page{page}", effectiveToken, delegate(int attempt, Exception _)
					{
						SafeInvoke(delegate
						{
							UpdateStatusBar($"搜索歌曲失败，正在重试（第 {attempt} 次）...");
						});
					}).ConfigureAwait(continueOnCapturedContext: true);
					List<SongInfo> songs = songResult?.Items ?? new List<SongInfo>();
					int totalCount = songResult?.TotalCount ?? songs.Count;
					bool hasMore = songResult?.HasMore ?? false;
					int maxPage = Math.Max(1, (int)Math.Ceiling((double)Math.Max(1, totalCount) / (double)Math.Max(1, _resultsPerPage)));
					if (effectiveToken.IsCancellationRequested)
					{
						return;
					}
					await ExecuteOnUiThreadAsync(delegate
					{
						if (!ShouldAbortViewRender(viewToken, "搜索歌曲"))
						{
							_currentSongs = songs;
							_currentPlaylist = null;
							_maxPage = maxPage;
							_hasNextSearchPage = hasMore;
							PatchSongs(_currentSongs, offset + 1, skipAvailabilityCheck: false, showPagination: true, page > 1, hasMore, pendingFocusIndex, allowSelection: true);
							FocusListAfterEnrich(pendingFocusIndex);
							if (_currentSongs.Count == 0)
							{
								UpdateStatusBar("未找到结果");
								if (showEmptyPrompt)
								{
									MessageBox.Show("未找到相关歌曲: " + keyword, "搜索结果", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
								}
							}
							else
							{
								UpdateStatusBar($"第 {_currentPage}/{_maxPage} 页，本页 {_currentSongs.Count} 首 / 总 {totalCount} 首");
							}
						}
					}).ConfigureAwait(continueOnCapturedContext: false);
					await EnsureLibraryStateFreshAsync(LibraryEntityType.Songs);
				}
				catch (Exception ex)
				{
					Exception ex2 = ex;
					if (TryHandleOperationCancelled(ex2, "搜索歌曲已取消"))
					{
						return;
					}
					Debug.WriteLine($"[Search] 搜索歌曲失败: {ex2}");
					await ExecuteOnUiThreadAsync(delegate
					{
						if (!ShouldAbortViewRender(viewToken, "搜索歌曲"))
						{
							MessageBox.Show("搜索歌曲失败: " + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
							UpdateStatusBar("搜索歌曲失败");
						}
					}).ConfigureAwait(continueOnCapturedContext: false);
				}
				finally
				{
					linkedCts?.Dispose();
				}
			}
		}
	}

	private async Task ShowPlaylistSearchResultsAsync(string keyword, int page, bool skipSaveNavigation, bool showEmptyPrompt, CancellationToken searchToken, int pendingFocusIndex = -1)
	{
		checked
		{
			int offset = Math.Max(0, (page - 1) * _resultsPerPage);
			string viewSource = $"search:playlist:{keyword}:page{page}";
			string accessibleName = "搜索歌单: " + keyword;
			ViewLoadRequest request = new ViewLoadRequest(viewSource, accessibleName, "正在搜索 " + keyword + "...", !skipSaveNavigation, (pendingFocusIndex >= 0) ? pendingFocusIndex : 0);
			ViewLoadResult<SearchViewData<PlaylistInfo>> loadResult = await RunViewLoadAsync(request, delegate
			{
				using (WorkScopes.BeginSkeleton("SearchPlaylist", viewSource))
				{
					return Task.FromResult(BuildSearchSkeletonViewData(keyword, "歌单", page, offset + 1, _currentPlaylists));
				}
			}, "搜索歌单已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				SearchViewData<PlaylistInfo> skeleton = loadResult.Value;
				string statusText = BuildSearchSkeletonStatus(keyword, skeleton.Items.Count, "歌单");
				DisplayPlaylists(skeleton.Items, preserveSelection: false, viewSource, accessibleName, skeleton.StartIndex, showPagination: false, hasNextPage: false, announceHeader: true, suppressFocus: false, allowSelection: true);
				UpdateStatusBar(statusText);
				EnrichPlaylistSearchResultsAsync(keyword, page, offset, viewSource, accessibleName, showEmptyPrompt, searchToken, pendingFocusIndex);
			}
		}
	}

	private async Task EnrichPlaylistSearchResultsAsync(string keyword, int page, int offset, string viewSource, string accessibleName, bool showEmptyPrompt, CancellationToken searchToken, int pendingFocusIndex = -1)
	{
		CancellationToken viewToken = GetCurrentViewContentToken();
		checked
		{
			using (WorkScopes.BeginEnrichment("SearchPlaylist", viewSource))
			{
				CancellationTokenSource linkedCts = null;
				CancellationToken effectiveToken = LinkCancellationTokens(viewToken, searchToken, out linkedCts);
				try
				{
					SearchResult<PlaylistInfo> playlistResult = await FetchWithRetryUntilCancel((CancellationToken ct) => _apiClient.SearchPlaylistsAsync(keyword, _resultsPerPage, offset), $"search:playlist:{keyword}:page{page}", effectiveToken, delegate(int attempt, Exception _)
					{
						SafeInvoke(delegate
						{
							UpdateStatusBar($"搜索歌单失败，正在重试（第 {attempt} 次）...");
						});
					}).ConfigureAwait(continueOnCapturedContext: true);
					List<PlaylistInfo> playlists = playlistResult?.Items ?? new List<PlaylistInfo>();
					int totalCount = playlistResult?.TotalCount ?? playlists.Count;
					bool hasMore = playlistResult?.HasMore ?? false;
					int maxPage = Math.Max(1, (int)Math.Ceiling((double)Math.Max(1, totalCount) / (double)Math.Max(1, _resultsPerPage)));
					if (effectiveToken.IsCancellationRequested)
					{
						return;
					}
					await ExecuteOnUiThreadAsync(delegate
					{
						if (!ShouldAbortViewRender(viewToken, "搜索歌单"))
						{
							_currentPlaylists = playlists;
							_maxPage = maxPage;
							_hasNextSearchPage = hasMore;
							PatchPlaylists(_currentPlaylists, offset + 1, showPagination: true, page > 1, hasMore, pendingFocusIndex, allowSelection: true);
							FocusListAfterEnrich(pendingFocusIndex);
							if (_currentPlaylists.Count == 0)
							{
								UpdateStatusBar("未找到结果");
								if (showEmptyPrompt)
								{
									MessageBox.Show("未找到相关歌单: " + keyword, "搜索结果", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
								}
							}
							else
							{
								UpdateStatusBar($"第 {_currentPage}/{_maxPage} 页，本页 {_currentPlaylists.Count} 个 / 总 {totalCount} 个");
							}
						}
					}).ConfigureAwait(continueOnCapturedContext: false);
					await EnsureLibraryStateFreshAsync(LibraryEntityType.Playlists);
				}
				catch (Exception ex)
				{
					Exception ex2 = ex;
					if (TryHandleOperationCancelled(ex2, "搜索歌单已取消"))
					{
						return;
					}
					Debug.WriteLine($"[Search] 搜索歌单失败: {ex2}");
					await ExecuteOnUiThreadAsync(delegate
					{
						if (!ShouldAbortViewRender(viewToken, "搜索歌单"))
						{
							MessageBox.Show("搜索歌单失败: " + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
							UpdateStatusBar("搜索歌单失败");
						}
					}).ConfigureAwait(continueOnCapturedContext: false);
				}
				finally
				{
					linkedCts?.Dispose();
				}
			}
		}
	}

	private async Task ShowAlbumSearchResultsAsync(string keyword, int page, bool skipSaveNavigation, bool showEmptyPrompt, CancellationToken searchToken, int pendingFocusIndex = -1)
	{
		checked
		{
			int offset = Math.Max(0, (page - 1) * _resultsPerPage);
			string viewSource = $"search:album:{keyword}:page{page}";
			string accessibleName = "搜索专辑: " + keyword;
			ViewLoadRequest request = new ViewLoadRequest(viewSource, accessibleName, "正在搜索 " + keyword + "...", !skipSaveNavigation, (pendingFocusIndex >= 0) ? pendingFocusIndex : 0);
			ViewLoadResult<SearchViewData<AlbumInfo>> loadResult = await RunViewLoadAsync(request, delegate
			{
				using (WorkScopes.BeginSkeleton("SearchAlbum", viewSource))
				{
					return Task.FromResult(BuildSearchSkeletonViewData(keyword, "专辑", page, offset + 1, _currentAlbums));
				}
			}, "搜索专辑已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				SearchViewData<AlbumInfo> skeleton = loadResult.Value;
				string statusText = BuildSearchSkeletonStatus(keyword, skeleton.Items.Count, "专辑");
				DisplayAlbums(skeleton.Items, preserveSelection: false, viewSource, accessibleName, skeleton.StartIndex, showPagination: false, hasNextPage: false, announceHeader: true, suppressFocus: false, allowSelection: true);
				UpdateStatusBar(statusText);
				EnrichAlbumSearchResultsAsync(keyword, page, offset, viewSource, accessibleName, showEmptyPrompt, searchToken, pendingFocusIndex);
			}
		}
	}

	private async Task EnrichAlbumSearchResultsAsync(string keyword, int page, int offset, string viewSource, string accessibleName, bool showEmptyPrompt, CancellationToken searchToken, int pendingFocusIndex = -1)
	{
		CancellationToken viewToken = GetCurrentViewContentToken();
		checked
		{
			using (WorkScopes.BeginEnrichment("SearchAlbum", viewSource))
			{
				CancellationTokenSource linkedCts = null;
				CancellationToken effectiveToken = LinkCancellationTokens(viewToken, searchToken, out linkedCts);
				try
				{
					SearchResult<AlbumInfo> albumResult = await FetchWithRetryUntilCancel((CancellationToken ct) => _apiClient.SearchAlbumsAsync(keyword, _resultsPerPage, offset), $"search:album:{keyword}:page{page}", effectiveToken, delegate(int attempt, Exception _)
					{
						SafeInvoke(delegate
						{
							UpdateStatusBar($"搜索专辑失败，正在重试（第 {attempt} 次）...");
						});
					}).ConfigureAwait(continueOnCapturedContext: true);
					List<AlbumInfo> albums = albumResult?.Items ?? new List<AlbumInfo>();
					int totalCount = albumResult?.TotalCount ?? albums.Count;
					bool hasMore = albumResult?.HasMore ?? false;
					int maxPage = Math.Max(1, (int)Math.Ceiling((double)Math.Max(1, totalCount) / (double)Math.Max(1, _resultsPerPage)));
					if (effectiveToken.IsCancellationRequested)
					{
						return;
					}
					await ExecuteOnUiThreadAsync(delegate
					{
						if (!ShouldAbortViewRender(viewToken, "搜索专辑"))
						{
							_currentAlbums = albums;
							_maxPage = maxPage;
							_hasNextSearchPage = hasMore;
							PatchAlbums(_currentAlbums, offset + 1, showPagination: true, page > 1, hasMore, pendingFocusIndex, allowSelection: true);
							FocusListAfterEnrich(pendingFocusIndex);
							if (_currentAlbums.Count == 0)
							{
								UpdateStatusBar("未找到结果");
								if (showEmptyPrompt)
								{
									MessageBox.Show("未找到相关专辑: " + keyword, "搜索结果", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
								}
							}
							else
							{
								UpdateStatusBar($"第 {_currentPage}/{_maxPage} 页，本页 {_currentAlbums.Count} 张 / 总 {totalCount} 张");
							}
						}
					}).ConfigureAwait(continueOnCapturedContext: false);
					await EnsureLibraryStateFreshAsync(LibraryEntityType.Albums);
				}
				catch (Exception ex)
				{
					Exception ex2 = ex;
					if (TryHandleOperationCancelled(ex2, "搜索专辑已取消"))
					{
						return;
					}
					Debug.WriteLine($"[Search] 搜索专辑失败: {ex2}");
					await ExecuteOnUiThreadAsync(delegate
					{
						if (!ShouldAbortViewRender(viewToken, "搜索专辑"))
						{
							MessageBox.Show("搜索专辑失败: " + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
							UpdateStatusBar("搜索专辑失败");
						}
					}).ConfigureAwait(continueOnCapturedContext: false);
				}
				finally
				{
					linkedCts?.Dispose();
				}
			}
		}
	}

	private async Task ShowArtistSearchResultsAsync(string keyword, int page, bool skipSaveNavigation, bool showEmptyPrompt, CancellationToken searchToken, int pendingFocusIndex = -1)
	{
		checked
		{
			int offset = Math.Max(0, (page - 1) * _resultsPerPage);
			string viewSource = $"search:artist:{keyword}:page{page}";
			string accessibleName = "搜索歌手: " + keyword;
			ViewLoadRequest request = new ViewLoadRequest(viewSource, accessibleName, "正在搜索 " + keyword + "...", !skipSaveNavigation, (pendingFocusIndex >= 0) ? pendingFocusIndex : 0);
			ViewLoadResult<SearchViewData<ArtistInfo>> loadResult = await RunViewLoadAsync(request, delegate
			{
				using (WorkScopes.BeginSkeleton("SearchArtist", viewSource))
				{
					return Task.FromResult(BuildSearchSkeletonViewData(keyword, "歌手", page, offset + 1, _currentArtists));
				}
			}, "搜索歌手已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				SearchViewData<ArtistInfo> skeleton = loadResult.Value;
				string statusText = BuildSearchSkeletonStatus(keyword, skeleton.Items.Count, "歌手");
				DisplayArtists(skeleton.Items, showPagination: false, hasNextPage: false, skeleton.StartIndex, preserveSelection: false, viewSource, accessibleName, announceHeader: true, suppressFocus: false, allowSelection: true);
				UpdateStatusBar(statusText);
				EnrichArtistSearchResultsAsync(keyword, page, offset, viewSource, accessibleName, showEmptyPrompt, searchToken, pendingFocusIndex);
			}
		}
	}

	private async Task EnrichArtistSearchResultsAsync(string keyword, int page, int offset, string viewSource, string accessibleName, bool showEmptyPrompt, CancellationToken searchToken, int pendingFocusIndex = -1)
	{
		CancellationToken viewToken = GetCurrentViewContentToken();
		checked
		{
			using (WorkScopes.BeginEnrichment("SearchArtist", viewSource))
			{
				CancellationTokenSource linkedCts = null;
				CancellationToken effectiveToken = LinkCancellationTokens(viewToken, searchToken, out linkedCts);
				try
				{
					SearchResult<ArtistInfo> artistResult = await FetchWithRetryUntilCancel((CancellationToken ct) => _apiClient.SearchArtistsAsync(keyword, _resultsPerPage, offset), $"search:artist:{keyword}:page{page}", effectiveToken, delegate(int attempt, Exception _)
					{
						SafeInvoke(delegate
						{
							UpdateStatusBar($"搜索歌手失败，正在重试（第 {attempt} 次）...");
						});
					}).ConfigureAwait(continueOnCapturedContext: true);
					List<ArtistInfo> artists = artistResult?.Items ?? new List<ArtistInfo>();
					int totalCount = artistResult?.TotalCount ?? artists.Count;
					bool hasMore = artistResult?.HasMore ?? false;
					int maxPage = Math.Max(1, (int)Math.Ceiling((double)Math.Max(1, totalCount) / (double)Math.Max(1, _resultsPerPage)));
					if (effectiveToken.IsCancellationRequested)
					{
						return;
					}
					await ExecuteOnUiThreadAsync(delegate
					{
						if (!ShouldAbortViewRender(viewToken, "搜索歌手"))
						{
							_currentArtists = artists;
							_maxPage = maxPage;
							_hasNextSearchPage = hasMore;
							PatchArtists(_currentArtists, offset + 1, showPagination: true, page > 1, hasMore, pendingFocusIndex, allowSelection: true);
							FocusListAfterEnrich(pendingFocusIndex);
							if (_currentArtists.Count == 0)
							{
								UpdateStatusBar("未找到结果");
								if (showEmptyPrompt)
								{
									MessageBox.Show("未找到相关歌手: " + keyword, "搜索结果", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
								}
							}
							else
							{
								UpdateStatusBar($"第 {_currentPage}/{_maxPage} 页，本页 {_currentArtists.Count} 位 / 总 {totalCount} 位");
							}
						}
					}).ConfigureAwait(continueOnCapturedContext: false);
					await EnsureLibraryStateFreshAsync(LibraryEntityType.Artists);
				}
				catch (Exception ex)
				{
					Exception ex2 = ex;
					if (TryHandleOperationCancelled(ex2, "搜索歌手已取消"))
					{
						return;
					}
					Debug.WriteLine($"[Search] 搜索歌手失败: {ex2}");
					await ExecuteOnUiThreadAsync(delegate
					{
						if (!ShouldAbortViewRender(viewToken, "搜索歌手"))
						{
							MessageBox.Show("搜索歌手失败: " + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
							UpdateStatusBar("搜索歌手失败");
						}
					}).ConfigureAwait(continueOnCapturedContext: false);
				}
				finally
				{
					linkedCts?.Dispose();
				}
			}
		}
	}

	private async Task ShowPodcastSearchResultsAsync(string keyword, int page, bool skipSaveNavigation, bool showEmptyPrompt, CancellationToken searchToken, int pendingFocusIndex = -1)
	{
		checked
		{
			int offset = Math.Max(0, (page - 1) * _resultsPerPage);
			string viewSource = $"search:podcast:{keyword}:page{page}";
			string accessibleName = "搜索播客: " + keyword;
			ViewLoadRequest request = new ViewLoadRequest(viewSource, accessibleName, "正在搜索 " + keyword + "...", !skipSaveNavigation, (pendingFocusIndex >= 0) ? pendingFocusIndex : 0);
			ViewLoadResult<SearchViewData<PodcastRadioInfo>> loadResult = await RunViewLoadAsync(request, delegate
			{
				using (WorkScopes.BeginSkeleton("SearchPodcast", viewSource))
				{
					return Task.FromResult(BuildSearchSkeletonViewData(keyword, "播客", page, offset + 1, _currentPodcasts));
				}
			}, "搜索播客已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				SearchViewData<PodcastRadioInfo> skeleton = loadResult.Value;
				string statusText = BuildSearchSkeletonStatus(keyword, skeleton.Items.Count, "播客");
				DisplayPodcasts(skeleton.Items, showPagination: false, hasNextPage: false, skeleton.StartIndex, preserveSelection: false, viewSource, accessibleName, announceHeader: true, suppressFocus: false, allowSelection: true);
				UpdateStatusBar(statusText);
				EnrichPodcastSearchResultsAsync(keyword, page, offset, viewSource, accessibleName, showEmptyPrompt, searchToken, pendingFocusIndex);
			}
		}
	}

	private async Task EnrichPodcastSearchResultsAsync(string keyword, int page, int offset, string viewSource, string accessibleName, bool showEmptyPrompt, CancellationToken searchToken, int pendingFocusIndex = -1)
	{
		CancellationToken viewToken = GetCurrentViewContentToken();
		checked
		{
			using (WorkScopes.BeginEnrichment("SearchPodcast", viewSource))
			{
				CancellationTokenSource linkedCts = null;
				CancellationToken effectiveToken = LinkCancellationTokens(viewToken, searchToken, out linkedCts);
				try
				{
					SearchResult<PodcastRadioInfo> podcastResult = await FetchWithRetryUntilCancel((CancellationToken ct) => _apiClient.SearchPodcastsAsync(keyword, _resultsPerPage, offset), $"search:podcast:{keyword}:page{page}", effectiveToken, delegate(int attempt, Exception _)
					{
						SafeInvoke(delegate
						{
							UpdateStatusBar($"搜索播客失败，正在重试（第 {attempt} 次）...");
						});
					}).ConfigureAwait(continueOnCapturedContext: true);
					List<PodcastRadioInfo> podcasts = podcastResult?.Items ?? new List<PodcastRadioInfo>();
					int totalCount = podcastResult?.TotalCount ?? podcasts.Count;
					bool hasMore = podcastResult?.HasMore ?? false;
					int maxPage = Math.Max(1, (int)Math.Ceiling((double)Math.Max(1, totalCount) / (double)Math.Max(1, _resultsPerPage)));
					if (effectiveToken.IsCancellationRequested)
					{
						return;
					}
					await ExecuteOnUiThreadAsync(delegate
					{
						if (!ShouldAbortViewRender(viewToken, "搜索播客"))
						{
							_currentPodcasts = podcasts;
							_maxPage = maxPage;
							_hasNextSearchPage = hasMore;
							PatchPodcasts(_currentPodcasts, offset + 1, showPagination: true, page > 1, hasMore, pendingFocusIndex, allowSelection: false);
							FocusListAfterEnrich(pendingFocusIndex);
							if (_currentPodcasts.Count == 0)
							{
								UpdateStatusBar("未找到结果");
								if (showEmptyPrompt)
								{
									MessageBox.Show("未找到相关播客: " + keyword, "搜索结果", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
								}
							}
							else
							{
								UpdateStatusBar($"第 {_currentPage}/{_maxPage} 页，本页 {_currentPodcasts.Count} 个 / 总 {totalCount} 个");
							}
						}
					}).ConfigureAwait(continueOnCapturedContext: false);
					await EnsureLibraryStateFreshAsync(LibraryEntityType.Podcasts);
				}
				catch (Exception ex)
				{
					Exception ex2 = ex;
					if (TryHandleOperationCancelled(ex2, "搜索播客已取消"))
					{
						return;
					}
					Debug.WriteLine($"[Search] 搜索播客失败: {ex2}");
					await ExecuteOnUiThreadAsync(delegate
					{
						if (!ShouldAbortViewRender(viewToken, "搜索播客"))
						{
							MessageBox.Show("搜索播客失败: " + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
							UpdateStatusBar("搜索播客失败");
						}
					}).ConfigureAwait(continueOnCapturedContext: false);
				}
				finally
				{
					linkedCts?.Dispose();
				}
			}
		}
	}

	private async Task HandleNeteaseUrlSearchAsync(NeteaseUrlMatch match, CancellationToken cancellationToken)
	{
		switch (match.Type)
		{
		case NeteaseUrlType.Song:
			await HandleSongUrlAsync(match, cancellationToken);
			UpdateStatusBar("已定位歌曲");
			break;
		case NeteaseUrlType.Playlist:
			await HandlePlaylistUrlAsync(match, cancellationToken);
			break;
		case NeteaseUrlType.Album:
			await HandleAlbumUrlAsync(match, cancellationToken);
			break;
		case NeteaseUrlType.Artist:
			await HandleArtistUrlAsync(match, cancellationToken);
			break;
		case NeteaseUrlType.Podcast:
			await HandlePodcastUrlAsync(match, cancellationToken);
			break;
		case NeteaseUrlType.PodcastEpisode:
			await HandlePodcastEpisodeUrlAsync(match, cancellationToken);
			break;
		default:
			MessageBox.Show("暂不支持该链接类型。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			UpdateStatusBar("不支持的链接类型");
			break;
		}
	}

	private async Task HandleSongUrlAsync(NeteaseUrlMatch match, CancellationToken cancellationToken)
	{
		if (TryValidateNeteaseResourceId(match.ResourceId, "歌曲", out var parsedSongId))
		{
			string resolvedSongId = parsedSongId.ToString();
			List<SongInfo> songs = await _apiClient.GetSongDetailAsync(new string[1] { resolvedSongId });
			cancellationToken.ThrowIfCancellationRequested();
			SongInfo song = songs?.FirstOrDefault();
			if (song == null)
			{
				MessageBox.Show("未能找到该链接指向的歌曲。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				UpdateStatusBar("未找到歌曲");
			}
			else
			{
				DisplaySongFromUrl(song, resolvedSongId, skipSave: false);
			}
		}
	}

	private async Task<bool> LoadSongFromUrlAsync(string songId, bool skipSave = false)
	{
		if (string.IsNullOrWhiteSpace(songId))
		{
			Debug.WriteLine("[Navigation] 无法加载歌曲视图，缺少歌曲ID");
			return false;
		}
		try
		{
			SongInfo song = (await _apiClient.GetSongDetailAsync(new string[1] { songId }))?.FirstOrDefault();
			if (song == null)
			{
				MessageBox.Show("未能找到该链接指向的歌曲。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				UpdateStatusBar("未找到歌曲");
				return false;
			}
			return DisplaySongFromUrl(song, songId, skipSave);
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"[Navigation] 加载歌曲失败: {ex}");
			MessageBox.Show("加载歌曲失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("加载歌曲失败");
			return false;
		}
	}

	private bool DisplaySongFromUrl(SongInfo song, string? fallbackSongId, bool skipSave)
	{
		if (song == null)
		{
			return false;
		}
		string text = ((!string.IsNullOrWhiteSpace(song.Id)) ? song.Id : (fallbackSongId ?? string.Empty));
		if (string.IsNullOrWhiteSpace(text))
		{
			MessageBox.Show("无法显示该歌曲，缺少有效的歌曲ID。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			return false;
		}
		if (!skipSave)
		{
			SaveNavigationState();
		}
		_isHomePage = false;
		_currentSongs = new List<SongInfo> { song };
		_currentPlaylist = null;
		_currentPage = 1;
		_maxPage = 1;
		_hasNextSearchPage = false;
		string viewSource = (_currentViewSource = "url:song:" + text);
		string accessibleName = (string.IsNullOrWhiteSpace(song.Name) ? ("歌曲: " + text) : ("歌曲: " + song.Name));
		DisplaySongs(_currentSongs, showPagination: false, hasNextPage: false, 1, preserveSelection: false, viewSource, accessibleName);
		FocusListAfterEnrich(0);
		return true;
	}

	private async Task HandlePlaylistUrlAsync(NeteaseUrlMatch match, CancellationToken cancellationToken)
	{
		if (TryValidateNeteaseResourceId(match.ResourceId, "歌单", out var parsedPlaylistId))
		{
			await LoadPlaylistById(parsedPlaylistId.ToString());
			FocusListAfterEnrich(0);
		}
	}

	private async Task HandleAlbumUrlAsync(NeteaseUrlMatch match, CancellationToken cancellationToken)
	{
		if (TryValidateNeteaseResourceId(match.ResourceId, "专辑", out var parsedAlbumId))
		{
			await LoadAlbumById(parsedAlbumId.ToString());
			FocusListAfterEnrich(0);
		}
	}

	private async Task HandleArtistUrlAsync(NeteaseUrlMatch match, CancellationToken cancellationToken)
	{
		if (TryValidateNeteaseResourceId(match.ResourceId, "歌手", out var artistId))
		{
			ArtistInfo artist = new ArtistInfo
			{
				Id = artistId,
				Name = $"歌手 {artistId}"
			};
			await OpenArtistAsync(artist);
		}
	}

	private async Task HandlePodcastUrlAsync(NeteaseUrlMatch match, CancellationToken cancellationToken)
	{
		if (TryValidateNeteaseResourceId(match.ResourceId, "播客", out var podcastId))
		{
			PodcastRadioInfo podcast = await _apiClient.GetPodcastRadioDetailAsync(podcastId);
			cancellationToken.ThrowIfCancellationRequested();
			if (podcast == null)
			{
				MessageBox.Show("未能找到该播客。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				UpdateStatusBar("未找到播客");
			}
			else
			{
				await OpenPodcastRadioAsync(podcast);
				FocusListAfterEnrich(0);
			}
		}
	}

	private async Task HandlePodcastEpisodeUrlAsync(NeteaseUrlMatch match, CancellationToken cancellationToken)
	{
		if (TryValidateNeteaseResourceId(match.ResourceId, "播客节目", out var programId))
		{
			PodcastEpisodeInfo episode = await _apiClient.GetPodcastEpisodeDetailAsync(programId);
			cancellationToken.ThrowIfCancellationRequested();
			if (episode == null)
			{
				MessageBox.Show("未能找到该播客节目。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				UpdateStatusBar("未找到播客节目");
			}
			else if (episode.Song != null)
			{
				await PlaySong(episode.Song);
			}
			else
			{
				MessageBox.Show("该播客节目暂无可播放的音频。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				UpdateStatusBar("无法播放播客节目");
			}
		}
	}

	private async Task HandleMultipleNeteaseUrlSearchAsync(List<NeteaseUrlMatch> matches, CancellationToken cancellationToken, bool skipSave = false, string? mixedQueryKeyOverride = null)
	{
		if (matches == null || matches.Count == 0)
		{
			return;
		}
		List<NormalizedUrlMatch> normalizedMatches = new List<NormalizedUrlMatch>();
		List<string> parseFailures = new List<string>();
		foreach (NeteaseUrlMatch match in matches)
		{
			string entityName = GetEntityDisplayName(match.Type);
			if (!TryValidateNeteaseResourceId(match.ResourceId, entityName, out var parsedId))
			{
				parseFailures.Add(entityName + "（" + match.ResourceId + "）");
			}
			else
			{
				normalizedMatches.Add(new NormalizedUrlMatch(match.Type, entityName, parsedId));
			}
		}
		if (normalizedMatches.Count == 0)
		{
			string failureMessage = ((parseFailures.Count > 0) ? ("以下链接无法解析：\n" + string.Join("\n", parseFailures.Take(5))) : "未能解析任何有效的链接。");
			MessageBox.Show(failureMessage, "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			UpdateStatusBar("链接解析失败");
			return;
		}
		if (!skipSave)
		{
			SaveNavigationState();
		}
		ApplySearchTypeDisplayForMatches(matches);
		List<NeteaseUrlMatch> normalizedForKey = normalizedMatches.Select((NormalizedUrlMatch n) => new NeteaseUrlMatch(n.Type, n.IdText, n.IdText)).ToList();
		string targetMixedKey = mixedQueryKeyOverride ?? BuildMixedQueryKey(normalizedForKey);
		string viewSource = "url:mixed:" + targetMixedKey;
		ViewLoadRequest request = new ViewLoadRequest(viewSource, "结果", "正在解析链接...", !skipSave);
		ViewLoadResult<MultiUrlViewData?> loadResult = await RunViewLoadAsync(request, async delegate(CancellationToken viewToken)
		{
			CancellationTokenSource linkedCts = null;
			CancellationToken effectiveToken = LinkCancellationTokens(viewToken, cancellationToken, out linkedCts);
			try
			{
				return await BuildMultiUrlViewDataAsync(normalizedMatches, parseFailures, effectiveToken).ConfigureAwait(continueOnCapturedContext: true);
			}
			finally
			{
				linkedCts?.Dispose();
			}
		}, "链接解析已取消").ConfigureAwait(continueOnCapturedContext: true);
		if (loadResult.IsCanceled)
		{
			return;
		}
		MultiUrlViewData data = loadResult.Value;
		if (data == null || data.Items.Count == 0)
		{
			string failureMessage2 = ((data != null && data.Failures?.Count > 0) ? ("未能加载任何结果：\n" + string.Join("\n", data.Failures.Take(5))) : "未能加载任何结果。");
			MessageBox.Show(failureMessage2, "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			UpdateStatusBar("链接加载失败");
			return;
		}
		_currentMixedQueryKey = targetMixedKey;
		foreach (SongInfo song in data.AggregatedSongs)
		{
			if (song != null)
			{
				song.ViewSource = viewSource;
			}
		}
		foreach (ListItemInfo item in data.Items)
		{
			if (item?.Song != null)
			{
				item.Song.ViewSource = viewSource;
			}
			else if (item?.PodcastEpisode?.Song != null)
			{
				item.PodcastEpisode.Song.ViewSource = viewSource;
			}
		}
		DisplayListItems(data.Items, viewSource, "结果");
		_currentSongs.Clear();
		if (data.AggregatedSongs.Count > 0)
		{
			_currentSongs.AddRange(data.AggregatedSongs);
		}
		UpdateStatusBar($"已加载 {data.Items.Count} 个链接结果");
		if (data.Failures.Count > 0)
		{
			IEnumerable<string> preview = data.Failures.Take(5);
			MessageBox.Show(string.Concat(str2: (data.Failures.Count > 5) ? "\n..." : string.Empty, str0: "部分链接未能加载：\n", str1: string.Join("\n", preview)), "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
		}
		FocusListAfterEnrich(0);
	}

	private async Task<MultiUrlViewData?> BuildMultiUrlViewDataAsync(List<NormalizedUrlMatch> normalizedMatches, List<string> initialFailures, CancellationToken cancellationToken)
	{
		List<ListItemInfo> listItems = new List<ListItemInfo>();
		List<SongInfo> aggregatedSongs = new List<SongInfo>();
		Dictionary<string, PlaylistInfo> playlistCache = new Dictionary<string, PlaylistInfo>(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, AlbumInfo> albumCache = new Dictionary<string, AlbumInfo>(StringComparer.OrdinalIgnoreCase);
		Dictionary<long, ArtistInfo> artistCache = new Dictionary<long, ArtistInfo>();
		List<string> failures = new List<string>(initialFailures);
		Dictionary<string, SongInfo> songMap = null;
		List<string> songIds = (from n in normalizedMatches
			where n.Type == NeteaseUrlType.Song
			select n.IdText).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToList();
		if (songIds.Count > 0)
		{
			List<SongInfo> songDetails = await _apiClient.GetSongDetailAsync(songIds.ToArray()).ConfigureAwait(continueOnCapturedContext: true);
			cancellationToken.ThrowIfCancellationRequested();
			if (songDetails != null)
			{
				songMap = songDetails.Where((SongInfo s) => !string.IsNullOrWhiteSpace(s.Id)).GroupBy<SongInfo, string>((SongInfo s) => s.Id, StringComparer.OrdinalIgnoreCase).ToDictionary((IGrouping<string, SongInfo> g) => g.Key, (IGrouping<string, SongInfo> g) => g.First(), StringComparer.OrdinalIgnoreCase);
			}
		}
		foreach (NormalizedUrlMatch normalized in normalizedMatches)
		{
			cancellationToken.ThrowIfCancellationRequested();
			SongInfo song;
			PlaylistInfo playlist;
			AlbumInfo album;
			ArtistInfo artist;
			switch (normalized.Type)
			{
			case NeteaseUrlType.Song:
				if (songMap != null && songMap.TryGetValue(normalized.IdText, out song) && song != null)
				{
					listItems.Add(new ListItemInfo
					{
						Type = ListItemType.Song,
						Song = song
					});
					aggregatedSongs.Add(song);
				}
				else
				{
					failures.Add(normalized.EntityName + "（" + normalized.IdText + "）");
				}
				break;
			case NeteaseUrlType.Playlist:
				if (!playlistCache.TryGetValue(normalized.IdText, out playlist) || playlist == null)
				{
					playlist = await _apiClient.GetPlaylistDetailAsync(normalized.IdText).ConfigureAwait(continueOnCapturedContext: true);
					cancellationToken.ThrowIfCancellationRequested();
					if (playlist != null)
					{
						playlistCache[normalized.IdText] = playlist;
					}
				}
				if (playlist != null)
				{
					listItems.Add(new ListItemInfo
					{
						Type = ListItemType.Playlist,
						Playlist = playlist
					});
				}
				else
				{
					failures.Add(normalized.EntityName + "（" + normalized.IdText + "）");
				}
				break;
			case NeteaseUrlType.Album:
				if (!albumCache.TryGetValue(normalized.IdText, out album) || album == null)
				{
					album = await _apiClient.GetAlbumDetailAsync(normalized.IdText).ConfigureAwait(continueOnCapturedContext: true);
					cancellationToken.ThrowIfCancellationRequested();
					if (album != null)
					{
						albumCache[normalized.IdText] = album;
					}
				}
				if (album != null)
				{
					listItems.Add(new ListItemInfo
					{
						Type = ListItemType.Album,
						Album = album
					});
				}
				else
				{
					failures.Add(normalized.EntityName + "（" + normalized.IdText + "）");
				}
				break;
			case NeteaseUrlType.Artist:
				if (!artistCache.TryGetValue(normalized.NumericId, out artist) || artist == null)
				{
					ArtistDetail detail = await _apiClient.GetArtistDetailAsync(normalized.NumericId).ConfigureAwait(continueOnCapturedContext: true);
					cancellationToken.ThrowIfCancellationRequested();
					if (detail != null)
					{
						artist = new ArtistInfo
						{
							Id = normalized.NumericId,
							Name = (string.IsNullOrWhiteSpace(detail.Name) ? $"歌手 {normalized.NumericId}" : detail.Name)
						};
						ApplyArtistDetailToArtist(artist, detail);
						artistCache[normalized.NumericId] = artist;
					}
				}
				if (artist != null)
				{
					listItems.Add(new ListItemInfo
					{
						Type = ListItemType.Artist,
						Artist = artist
					});
				}
				else
				{
					failures.Add(normalized.EntityName + "（" + normalized.IdText + "）");
				}
				break;
			case NeteaseUrlType.Podcast:
			{
				PodcastRadioInfo podcastDetail = await _apiClient.GetPodcastRadioDetailAsync(normalized.NumericId).ConfigureAwait(continueOnCapturedContext: true);
				cancellationToken.ThrowIfCancellationRequested();
				if (podcastDetail != null)
				{
					listItems.Add(new ListItemInfo
					{
						Type = ListItemType.Podcast,
						Podcast = podcastDetail
					});
				}
				else
				{
					failures.Add(normalized.EntityName + "（" + normalized.IdText + "）");
				}
				break;
			}
			case NeteaseUrlType.PodcastEpisode:
			{
				PodcastEpisodeInfo episodeDetail = await _apiClient.GetPodcastEpisodeDetailAsync(normalized.NumericId).ConfigureAwait(continueOnCapturedContext: true);
				cancellationToken.ThrowIfCancellationRequested();
				if (episodeDetail != null)
				{
					listItems.Add(new ListItemInfo
					{
						Type = ListItemType.PodcastEpisode,
						PodcastEpisode = episodeDetail
					});
					if (episodeDetail.Song != null)
					{
						aggregatedSongs.Add(episodeDetail.Song);
					}
				}
				else
				{
					failures.Add(normalized.EntityName + "（" + normalized.IdText + "）");
				}
				break;
			}
			default:
				failures.Add(normalized.EntityName + "（" + normalized.IdText + "）");
				break;
			}
			song = null;
			playlist = null;
			album = null;
			artist = null;
		}
		return new MultiUrlViewData(listItems, aggregatedSongs, failures);
	}

	private async Task<bool> RestoreMixedUrlStateAsync(string mixedQueryKey)
	{
		if (!TryParseMixedQueryKey(mixedQueryKey, out List<NeteaseUrlMatch> matches) || matches.Count == 0)
		{
			MessageBox.Show("无法恢复混合链接结果。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			return false;
		}
		await HandleMultipleNeteaseUrlSearchAsync(matches, CancellationToken.None, skipSave: true, mixedQueryKey);
		return true;
	}

	private string MapUrlTypeToSearchType(NeteaseUrlType type)
	{
		switch (type)
		{
		case NeteaseUrlType.Playlist:
			return "歌单";
		case NeteaseUrlType.Album:
			return "专辑";
		case NeteaseUrlType.Artist:
			return "歌手";
		case NeteaseUrlType.Podcast:
		case NeteaseUrlType.PodcastEpisode:
			return "播客";
		default:
			return "歌曲";
		}
	}

	private bool TryValidateNeteaseResourceId(string? resourceId, string entityName, out long parsedId)
	{
		parsedId = 0L;
		if (string.IsNullOrWhiteSpace(resourceId) || !long.TryParse(resourceId, out parsedId) || parsedId <= 0)
		{
			MessageBox.Show(entityName + "链接格式不正确。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			UpdateStatusBar("无法解析" + entityName + "链接");
			return false;
		}
		return true;
	}

	private async Task<bool> LoadHomePageAsync(bool skipSave = false, bool showErrorDialog = true, bool isInitialLoad = false, CancellationToken cancellationToken = default(CancellationToken))
	{
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!skipSave)
			{
				_navigationHistory.Clear();
				Debug.WriteLine("[Navigation] 加载主页，清空导航历史");
			}
			ViewLoadRequest request = new ViewLoadRequest("homepage", "主页", "正在加载主页骨架...", !skipSave);
			ViewLoadResult<HomePageViewData?> loadResult = await RunViewLoadAsync(request, delegate(CancellationToken viewToken)
			{
				CancellationTokenSource linkedCts = null;
				CancellationToken cancellationToken2 = LinkCancellationTokens(viewToken, cancellationToken, out linkedCts);
				try
				{
					cancellationToken2.ThrowIfCancellationRequested();
					return Task.FromResult(BuildHomePageSkeletonViewData());
				}
				finally
				{
					linkedCts?.Dispose();
				}
			}, "加载主页已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (loadResult.IsCanceled)
			{
				return false;
			}
			HomePageViewData data = loadResult.Value ?? BuildHomePageSkeletonViewData();
			DisplayListItems(data.Items, "homepage", "主页");
			_currentSongs.Clear();
			_currentPlaylists.Clear();
			_currentAlbums.Clear();
			_currentPlaylist = null;
			_isHomePage = true;
			UpdateStatusBar(data.StatusText);
			EnrichHomePageAsync(isInitialLoad, cancellationToken);
			if (isInitialLoad)
			{
				_initialHomeLoadCompleted = true;
			}
			return true;
		}
		catch (OperationCanceledException)
		{
			UpdateStatusBar("加载主页已取消");
			return false;
		}
		catch (Exception ex2)
		{
			Exception ex3 = ex2;
			Debug.WriteLine($"[HomePage] 加载失败: {ex3}");
			if (showErrorDialog)
			{
				MessageBox.Show("加载主页失败: " + ex3.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			}
			UpdateStatusBar("加载主页失败");
			return false;
		}
	}

	private async Task<HomePageViewData> BuildHomePageEnrichedViewDataAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		List<ListItemInfo> homeItems = new List<ListItemInfo>();
		bool isLoggedIn = _accountState?.IsLoggedIn ?? false;
		int userPlaylistCount = 0;
		int userAlbumCount = 0;
		int artistFavoritesCount = 0;
		int podcastFavoritesCount = 0;
		PlaylistInfo likedPlaylist = null;
		int playlistCategoryCount = _homePlaylistCategoryPresets.Length;
		int artistCategoryTypeCount = ArtistMetadataHelper.GetTypeOptions().Count;
		Task<List<PlaylistInfo>> toplistTask = _apiClient.GetToplistAsync();
		Task<List<AlbumInfo>> newAlbumsTask = _apiClient.GetNewAlbumsAsync();
		int toplistCount = 0;
		int newAlbumCount = 0;
		int dailyRecommendSongCount = 0;
		int dailyRecommendPlaylistCount = 0;
		int personalizedPlaylistCount = 0;
		int personalizedSongCount = 0;
		Task<(List<SongInfo> Songs, List<PlaylistInfo> Playlists)> dailyRecommendTask = null;
		Task<(List<PlaylistInfo> Playlists, List<SongInfo> Songs)> personalizedTask = null;
		if (isLoggedIn)
		{
			dailyRecommendTask = FetchDailyRecommendBundleAsync(cancellationToken);
			personalizedTask = FetchPersonalizedBundleAsync(cancellationToken);
		}
		checked
		{
			if (isLoggedIn)
			{
				try
				{
					UserAccountInfo userInfo = await _apiClient.GetUserAccountAsync();
					ThrowIfHomeLoadCancelled();
					if (userInfo != null && userInfo.UserId > 0)
					{
						_loggedInUserId = userInfo.UserId;
						List<PlaylistInfo> playlists;
						int totalCount;
						(playlists, totalCount) = await _apiClient.GetUserPlaylistsAsync(userInfo.UserId);
						ThrowIfHomeLoadCancelled();
						if (playlists != null && playlists.Count > 0)
						{
							likedPlaylist = playlists.FirstOrDefault((PlaylistInfo p) => !string.IsNullOrEmpty(p.Name) && p.Name.IndexOf("喜欢的音乐", StringComparison.OrdinalIgnoreCase) >= 0);
							userPlaylistCount = totalCount;
							if (likedPlaylist != null && userPlaylistCount > 0)
							{
								userPlaylistCount = Math.Max(0, userPlaylistCount - 1);
							}
						}
						try
						{
							int albumCount = (await _apiClient.GetUserAlbumsAsync(1)).Item2;
							ThrowIfHomeLoadCancelled();
							userAlbumCount = albumCount;
						}
						catch (Exception ex)
						{
							Debug.WriteLine("[HomePage] 获取收藏专辑数量失败: " + ex.Message);
						}
						try
						{
							SearchResult<ArtistInfo> favoriteArtists = await _apiClient.GetArtistSubscriptionsAsync(1);
							ThrowIfHomeLoadCancelled();
							artistFavoritesCount = favoriteArtists?.TotalCount ?? favoriteArtists?.Items.Count ?? 0;
						}
						catch (Exception ex2)
						{
							Debug.WriteLine("[HomePage] 获取收藏歌手数量失败: " + ex2.Message);
						}
						try
						{
							int podcastCount = (await _apiClient.GetSubscribedPodcastsAsync(1)).Item2;
							ThrowIfHomeLoadCancelled();
							podcastFavoritesCount = podcastCount;
						}
						catch (Exception ex3)
						{
							Debug.WriteLine("[HomePage] 获取收藏播客数量失败: " + ex3.Message);
						}
					}
				}
				catch (Exception ex4)
				{
					Exception ex5 = ex4;
					Debug.WriteLine("[HomePage] 预加载用户数据失败: " + ex5.Message);
				}
			}
			else
			{
				_loggedInUserId = 0L;
				_recentSongsCache.Clear();
				_recentPlaylistsCache.Clear();
				_recentAlbumsCache.Clear();
				_recentPodcastsCache.Clear();
				_recentPlayCount = 0;
				_recentPlaylistCount = 0;
				_recentAlbumCount = 0;
				_recentPodcastCount = 0;
				_recentSummaryReady = false;
			}
			try
			{
				List<PlaylistInfo> toplist = await toplistTask;
				ThrowIfHomeLoadCancelled();
				toplistCount = toplist?.Count ?? 0;
			}
			catch (Exception ex6)
			{
				Debug.WriteLine("[HomePage] 获取排行榜数量失败: " + ex6.Message);
			}
			try
			{
				List<AlbumInfo> newAlbums = await newAlbumsTask;
				ThrowIfHomeLoadCancelled();
				newAlbumCount = newAlbums?.Count ?? 0;
			}
			catch (Exception ex7)
			{
				Debug.WriteLine("[HomePage] 获取新碟数量失败: " + ex7.Message);
			}
			if (dailyRecommendTask != null)
			{
				try
				{
					(List<SongInfo> Songs, List<PlaylistInfo> Playlists) dailyData = await dailyRecommendTask.ConfigureAwait(continueOnCapturedContext: false);
					ThrowIfHomeLoadCancelled();
					dailyRecommendSongCount = dailyData.Songs?.Count ?? 0;
					dailyRecommendPlaylistCount = dailyData.Playlists?.Count ?? 0;
				}
				catch (Exception ex4)
				{
					Exception ex8 = ex4;
					Debug.WriteLine("[HomePage] 获取每日推荐摘要失败: " + ex8.Message);
				}
			}
			if (personalizedTask != null)
			{
				try
				{
					(List<PlaylistInfo> Playlists, List<SongInfo> Songs) personalizedData = await personalizedTask.ConfigureAwait(continueOnCapturedContext: false);
					ThrowIfHomeLoadCancelled();
					personalizedPlaylistCount = personalizedData.Playlists?.Count ?? 0;
					personalizedSongCount = personalizedData.Songs?.Count ?? 0;
				}
				catch (Exception ex4)
				{
					Exception ex9 = ex4;
					Debug.WriteLine("[HomePage] 获取为您推荐摘要失败: " + ex9.Message);
				}
			}
			_userLikedPlaylist = likedPlaylist;
			await RefreshRecentSummariesAsync(isLoggedIn, cancellationToken);
			if (isLoggedIn)
			{
				CloudSongPageResult cloudSummary = null;
				try
				{
					_cloudTotalCount = 0;
					_cloudUsedSize = 0L;
					_cloudMaxSize = 0L;
					cloudSummary = await _apiClient.GetCloudSongsAsync(1);
					ThrowIfHomeLoadCancelled();
					_cloudTotalCount = cloudSummary.TotalCount;
					_cloudUsedSize = cloudSummary.UsedSize;
					_cloudMaxSize = cloudSummary.MaxSize;
				}
				catch (Exception ex4)
				{
					Exception ex10 = ex4;
					Debug.WriteLine("[Home] 获取云盘摘要失败: " + ex10.Message);
				}
				homeItems.Add(new ListItemInfo
				{
					Type = ListItemType.Category,
					CategoryId = "user_liked_songs",
					CategoryName = "喜欢的音乐",
					CategoryDescription = "您收藏的所有歌曲",
					ItemCount = (_userLikedPlaylist?.TrackCount ?? likedPlaylist?.TrackCount ?? 0),
					ItemUnit = "首"
				});
				homeItems.Add(new ListItemInfo
				{
					Type = ListItemType.Category,
					CategoryId = "recent_listened",
					CategoryName = "最近听过",
					CategoryDescription = BuildRecentListenedDescription()
				});
				homeItems.Add(new ListItemInfo
				{
					Type = ListItemType.Category,
					CategoryId = "user_playlists",
					CategoryName = "我的歌单",
					ItemCount = userPlaylistCount,
					ItemUnit = "个"
				});
				homeItems.Add(new ListItemInfo
				{
					Type = ListItemType.Category,
					CategoryId = "user_albums",
					CategoryName = "收藏的专辑",
					ItemCount = userAlbumCount,
					ItemUnit = "张"
				});
				homeItems.Add(new ListItemInfo
				{
					Type = ListItemType.Category,
					CategoryId = "artist_favorites",
					CategoryName = "收藏的歌手",
					ItemCount = artistFavoritesCount,
					ItemUnit = "位"
				});
				homeItems.Add(new ListItemInfo
				{
					Type = ListItemType.Category,
					CategoryId = "user_podcasts",
					CategoryName = "收藏的播客",
					ItemCount = podcastFavoritesCount,
					ItemUnit = "个"
				});
				homeItems.Add(new ListItemInfo
				{
					Type = ListItemType.Category,
					CategoryId = "user_cloud",
					CategoryName = "云盘",
					CategoryDescription = ((cloudSummary != null) ? ("已用 " + FormatSize(_cloudUsedSize) + " / " + FormatSize(_cloudMaxSize)) : "上传和管理您的私人音乐"),
					ItemCount = _cloudTotalCount,
					ItemUnit = "首"
				});
				homeItems.Add(new ListItemInfo
				{
					Type = ListItemType.Category,
					CategoryId = "daily_recommend",
					CategoryName = "每日推荐",
					CategoryDescription = ((dailyRecommendSongCount + dailyRecommendPlaylistCount > 0) ? $"歌曲 {dailyRecommendSongCount} 首 / 歌单 {dailyRecommendPlaylistCount} 个" : "每日 6:00 更新"),
					ItemCount = dailyRecommendSongCount + dailyRecommendPlaylistCount,
					ItemUnit = "项"
				});
				homeItems.Add(new ListItemInfo
				{
					Type = ListItemType.Category,
					CategoryId = "personalized",
					CategoryName = "为您推荐",
					CategoryDescription = ((personalizedPlaylistCount + personalizedSongCount > 0) ? $"歌单 {personalizedPlaylistCount} 个 / 歌曲 {personalizedSongCount} 首" : "为你推荐歌单和新歌"),
					ItemCount = personalizedPlaylistCount + personalizedSongCount,
					ItemUnit = "项"
				});
				homeItems.Add(new ListItemInfo
				{
					Type = ListItemType.Category,
					CategoryId = "highquality_playlists",
					CategoryName = "精品歌单",
					ItemCount = 50,
					ItemUnit = "个"
				});
				homeItems.Add(new ListItemInfo
				{
					Type = ListItemType.Category,
					CategoryId = "new_songs",
					CategoryName = "新歌速递",
					ItemCount = 5,
					ItemUnit = "个"
				});
				homeItems.Add(new ListItemInfo
				{
					Type = ListItemType.Category,
					CategoryId = "playlist_category",
					CategoryName = "歌单分类",
					ItemCount = playlistCategoryCount,
					ItemUnit = "个"
				});
				homeItems.Add(new ListItemInfo
				{
					Type = ListItemType.Category,
					CategoryId = "artist_categories",
					CategoryName = "歌手分类",
					ItemCount = artistCategoryTypeCount,
					ItemUnit = "个"
				});
				homeItems.Add(new ListItemInfo
				{
					Type = ListItemType.Category,
					CategoryId = "new_albums",
					CategoryName = "新碟上架",
					ItemCount = newAlbumCount,
					ItemUnit = "张"
				});
				homeItems.Add(new ListItemInfo
				{
					Type = ListItemType.Category,
					CategoryId = "toplist",
					CategoryName = "官方排行榜",
					ItemCount = toplistCount,
					ItemUnit = "个"
				});
			}
			else
			{
				_cloudTotalCount = 0;
				_cloudUsedSize = 0L;
				_cloudMaxSize = 0L;
				homeItems.Add(new ListItemInfo
				{
					Type = ListItemType.Category,
					CategoryId = "highquality_playlists",
					CategoryName = "精品歌单",
					ItemCount = 50,
					ItemUnit = "个"
				});
				homeItems.Add(new ListItemInfo
				{
					Type = ListItemType.Category,
					CategoryId = "new_songs",
					CategoryName = "新歌速递",
					ItemCount = 5,
					ItemUnit = "个"
				});
				homeItems.Add(new ListItemInfo
				{
					Type = ListItemType.Category,
					CategoryId = "playlist_category",
					CategoryName = "歌单分类",
					ItemCount = playlistCategoryCount,
					ItemUnit = "个"
				});
				homeItems.Add(new ListItemInfo
				{
					Type = ListItemType.Category,
					CategoryId = "artist_categories",
					CategoryName = "歌手分类",
					ItemCount = artistCategoryTypeCount,
					ItemUnit = "个"
				});
				homeItems.Add(new ListItemInfo
				{
					Type = ListItemType.Category,
					CategoryId = "new_albums",
					CategoryName = "新碟上架",
					ItemCount = newAlbumCount,
					ItemUnit = "张"
				});
				homeItems.Add(new ListItemInfo
				{
					Type = ListItemType.Category,
					CategoryId = "toplist",
					CategoryName = "官方排行榜",
					ItemCount = toplistCount,
					ItemUnit = "个"
				});
			}
			_homeCachedToplistCount = toplistCount;
			_homeCachedNewAlbumCount = newAlbumCount;
			if (isLoggedIn)
			{
				_homeCachedUserPlaylistCount = userPlaylistCount;
				_homeCachedUserAlbumCount = userAlbumCount;
				_homeCachedArtistFavoritesCount = artistFavoritesCount;
				_homeCachedPodcastFavoritesCount = podcastFavoritesCount;
				_homeCachedDailyRecommendSongCount = dailyRecommendSongCount;
				_homeCachedDailyRecommendPlaylistCount = dailyRecommendPlaylistCount;
				_homeCachedPersonalizedPlaylistCount = personalizedPlaylistCount;
				_homeCachedPersonalizedSongCount = personalizedSongCount;
			}
			else
			{
				_homeCachedUserPlaylistCount = null;
				_homeCachedUserAlbumCount = null;
				_homeCachedArtistFavoritesCount = null;
				_homeCachedPodcastFavoritesCount = null;
				_homeCachedDailyRecommendSongCount = null;
				_homeCachedDailyRecommendPlaylistCount = null;
				_homeCachedPersonalizedPlaylistCount = null;
				_homeCachedPersonalizedSongCount = null;
			}
			string statusText = (isLoggedIn ? $"主页加载完成，共 {homeItems.Count} 个入口" : "欢迎访问主页，登录后可查看更多内容");
			return new HomePageViewData(homeItems, statusText);
		}
		void ThrowIfHomeLoadCancelled()
		{
			cancellationToken.ThrowIfCancellationRequested();
		}
	}

	private async Task HandleListItemActivate(ListItemInfo listItem)
	{
		switch (listItem.Type)
		{
		case ListItemType.Song:
			if (listItem.Song != null)
			{
				await PlaySong(listItem.Song);
			}
			break;
		case ListItemType.Playlist:
			if (listItem.Playlist != null)
			{
				await OpenPlaylist(listItem.Playlist);
			}
			break;
		case ListItemType.Album:
			if (listItem.Album != null)
			{
				await OpenAlbum(listItem.Album);
			}
			break;
		case ListItemType.Artist:
			if (listItem.Artist != null)
			{
				if (IsArtistIntroEntryContext(listItem.Artist))
				{
					await ShowArtistIntroductionDialog(listItem.Artist);
				}
				else
				{
					await OpenArtistAsync(listItem.Artist);
				}
			}
			break;
		case ListItemType.Podcast:
			if (listItem.Podcast != null)
			{
				await OpenPodcastRadioAsync(listItem.Podcast);
			}
			break;
		case ListItemType.PodcastEpisode:
			if (listItem.PodcastEpisode?.Song != null)
			{
				await PlaySong(listItem.PodcastEpisode.Song);
			}
			break;
		case ListItemType.Category:
			await LoadCategoryContent(listItem.CategoryId);
			break;
		}
	}

	private bool IsArtistIntroEntryContext(ArtistInfo artist)
	{
		if (artist == null || string.IsNullOrWhiteSpace(_currentViewSource))
		{
			return false;
		}
		if (!_currentViewSource.StartsWith("artist_entries:", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		long num = ParseArtistIdFromViewSource(_currentViewSource, "artist_entries:");
		if (num > 0)
		{
			return num == artist.Id;
		}
		if (_currentArtist != null && _currentArtist.Id == artist.Id)
		{
			return true;
		}
		if (_currentArtistDetail != null && _currentArtistDetail.Id == artist.Id)
		{
			return true;
		}
		return false;
	}

	private async Task ShowArtistIntroductionDialog(ArtistInfo artist)
	{
		try
		{
			ArtistDetail detail = ((_currentArtistDetail == null || _currentArtistDetail.Id != artist.Id) ? (await _apiClient.GetArtistDetailAsync(artist.Id)) : _currentArtistDetail);
			if (detail == null)
			{
				MessageBox.Show("暂时无法获取该歌手的详细介绍。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				return;
			}
			if (string.IsNullOrWhiteSpace(detail.Name))
			{
				detail.Name = artist.Name;
			}
			if (string.IsNullOrWhiteSpace(detail.Alias))
			{
				detail.Alias = artist.Alias;
			}
			using ArtistDetailDialog dialog = new ArtistDetailDialog(detail);
			dialog.ShowDialog(this);
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			MessageBox.Show("加载歌手介绍失败: " + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
		}
	}

	private async Task LoadCategoryContent(string categoryId, bool skipSave = false)
	{
		try
		{
			double timeSinceStartup = (DateTime.Now - _appStartTime).TotalSeconds;
			if (timeSinceStartup < 3.0)
			{
				int delayMs = checked((int)((3.0 - timeSinceStartup) * 1000.0));
				Debug.WriteLine($"[ColdStartProtection] 应用启动仅 {timeSinceStartup:F1}秒，延迟 {delayMs}ms 以避免风控");
				await Task.Delay(Math.Min(delayMs, 2000));
			}
			UpdateStatusBar("正在加载 " + categoryId + "...");
			if (!skipSave)
			{
				SaveNavigationState();
			}
			_isHomePage = false;
			switch (categoryId)
			{
			case "user_liked_songs":
				await LoadUserLikedSongs();
				return;
			case "user_playlists":
				await LoadUserPlaylists();
				return;
			case "user_albums":
				await LoadUserAlbums();
				return;
			case "user_podcasts":
				await LoadUserPodcasts();
				return;
			case "user_cloud":
				_cloudPage = 1;
				await LoadCloudSongsAsync();
				return;
			case "recent_play":
				await LoadRecentPlayedSongsAsync();
				return;
			case "recent_listened":
				await LoadRecentListenedCategoryAsync(skipSave);
				return;
			case "recent_playlists":
				await LoadRecentPlaylistsAsync();
				return;
			case "recent_albums":
				await LoadRecentAlbumsAsync();
				return;
			case "recent_podcasts":
				await LoadRecentPodcastsAsync();
				return;
			case "daily_recommend":
				await LoadDailyRecommend();
				return;
			case "personalized":
				await LoadPersonalized();
				return;
			case "toplist":
				await LoadToplist();
				return;
			case "daily_recommend_songs":
				await LoadDailyRecommendSongs();
				return;
			case "daily_recommend_playlists":
				await LoadDailyRecommendPlaylists();
				return;
			case "personalized_playlists":
				await LoadPersonalizedPlaylists();
				return;
			case "personalized_newsongs":
				await LoadPersonalizedNewSongs();
				return;
			case "highquality_playlists":
				await LoadHighQualityPlaylists();
				return;
			case "new_songs":
				await LoadNewSongs();
				return;
			case "new_songs_all":
				await LoadNewSongsAll();
				return;
			case "new_songs_chinese":
				await LoadNewSongsChinese();
				return;
			case "new_songs_western":
				await LoadNewSongsWestern();
				return;
			case "new_songs_japan":
				await LoadNewSongsJapan();
				return;
			case "new_songs_korea":
				await LoadNewSongsKorea();
				return;
			case "personalized_newsong":
				await LoadPersonalizedNewSong();
				return;
			case "playlist_category":
				await LoadPlaylistCategory();
				return;
			case "new_albums":
				await LoadNewAlbums();
				return;
			case "artist_favorites":
				await LoadArtistFavoritesAsync(skipSave: true);
				return;
			case "artist_categories":
				await LoadArtistCategoryTypesAsync(skipSave: true);
				return;
			}
			long artistTopId;
			long artistSongsId;
			long artistAlbumsId;
			int typeCode;
			if (categoryId.StartsWith("playlist_cat_", StringComparison.OrdinalIgnoreCase))
			{
				string catName = categoryId.Substring("playlist_cat_".Length);
				if (!string.IsNullOrWhiteSpace(catName))
				{
					await LoadPlaylistsByCat(catName);
				}
				else
				{
					MessageBox.Show("未知的分类: " + categoryId, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				}
			}
			else if (categoryId.StartsWith("artist_top_", StringComparison.OrdinalIgnoreCase) && long.TryParse(categoryId.Substring("artist_top_".Length), out artistTopId))
			{
				await LoadArtistTopSongsAsync(artistTopId, skipSave: true);
			}
			else if (categoryId.StartsWith("artist_songs_", StringComparison.OrdinalIgnoreCase) && long.TryParse(categoryId.Substring("artist_songs_".Length), out artistSongsId))
			{
				await LoadArtistSongsAsync(artistSongsId, 0, skipSave: true, ArtistSongSortOption.Hot);
			}
			else if (categoryId.StartsWith("artist_albums_", StringComparison.OrdinalIgnoreCase) && long.TryParse(categoryId.Substring("artist_albums_".Length), out artistAlbumsId))
			{
				await LoadArtistAlbumsAsync(artistAlbumsId, 0, skipSave: true, ArtistAlbumSortOption.Latest);
			}
			else if (categoryId.StartsWith("artist_type_", StringComparison.OrdinalIgnoreCase) && int.TryParse(categoryId.Substring("artist_type_".Length), out typeCode))
			{
				await LoadArtistCategoryAreasAsync(typeCode, skipSave: true);
			}
			else if (categoryId.StartsWith("artist_area_", StringComparison.OrdinalIgnoreCase))
			{
				string[] parts = categoryId.Split('_');
				if (parts.Length == 4 && int.TryParse(parts[2], out var typeFilter) && int.TryParse(parts[3], out var areaFilter))
				{
					await LoadArtistsByCategoryAsync(typeFilter, areaFilter, 0, skipSave: true);
				}
				else
				{
					MessageBox.Show("未知的歌手分类: " + categoryId, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				}
			}
			else
			{
				MessageBox.Show("未知的分类: " + categoryId, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Debug.WriteLine($"[LoadCategoryContent] 异常: {ex2}");
			MessageBox.Show("加载分类内容失败: " + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("加载失败");
		}
	}

	private Task LoadRecentPlayedSongs(bool preserveSelection = false)
	{
		return LoadRecentPlayedSongsAsync();
	}

	private async Task LoadHighQualityPlaylists()
	{
		try
		{
			ViewLoadRequest request = new ViewLoadRequest("highquality_playlists", "精品歌单", "正在加载精品歌单...");
			ViewLoadResult<(List<PlaylistInfo> Items, string StatusText)?> loadResult = await RunViewLoadAsync(request, (Func<CancellationToken, Task<(List<PlaylistInfo>, string)?>>)async delegate(CancellationToken token)
			{
				(List<PlaylistInfo>, long, bool) result = await _apiClient.GetHighQualityPlaylistsAsync("全部", 50, 0L).ConfigureAwait(continueOnCapturedContext: false);
				token.ThrowIfCancellationRequested();
				List<PlaylistInfo> playlists2 = result.Item1 ?? new List<PlaylistInfo>();
				string status = ((playlists2.Count == 0) ? "暂无精品歌单" : $"加载完成，共 {playlists2.Count} 个精品歌单");
				Debug.WriteLine($"[LoadHighQualityPlaylists] 成功加载 {playlists2.Count} 个精品歌单, lasttime={result.Item2}, more={result.Item3}");
				return (playlists2, status);
			}, "加载精品歌单已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				(List<PlaylistInfo> Items, string StatusText)? data = loadResult.Value;
				List<PlaylistInfo> playlists = data?.Items ?? new List<PlaylistInfo>();
				DisplayPlaylists(playlists, preserveSelection: false, request.ViewSource, request.AccessibleName, 1, showPagination: false, hasNextPage: false, announceHeader: true, suppressFocus: true);
				FocusListAfterEnrich(0);
				if (playlists.Count == 0)
				{
					MessageBox.Show("暂无精品歌单", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				}
				UpdateStatusBar(data?.StatusText ?? "暂无精品歌单");
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			if (!TryHandleOperationCancelled(ex2, "加载精品歌单已取消"))
			{
				Debug.WriteLine($"[LoadHighQualityPlaylists] 异常: {ex2}");
				MessageBox.Show("加载精品歌单失败: " + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("加载失败");
			}
		}
	}

	private Task LoadNewSongs()
	{
		try
		{
			UpdateStatusBar("正在加载新歌速递...");
			List<ListItemInfo> items = new List<ListItemInfo>
			{
				new ListItemInfo
				{
					Type = ListItemType.Category,
					CategoryId = "new_songs_all",
					CategoryName = "全部"
				},
				new ListItemInfo
				{
					Type = ListItemType.Category,
					CategoryId = "new_songs_chinese",
					CategoryName = "华语"
				},
				new ListItemInfo
				{
					Type = ListItemType.Category,
					CategoryId = "new_songs_western",
					CategoryName = "欧美"
				},
				new ListItemInfo
				{
					Type = ListItemType.Category,
					CategoryId = "new_songs_japan",
					CategoryName = "日本"
				},
				new ListItemInfo
				{
					Type = ListItemType.Category,
					CategoryId = "new_songs_korea",
					CategoryName = "韩国"
				}
			};
			DisplayListItems(items, "new_songs", "新歌速递分类");
			UpdateStatusBar("请选择地区");
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"[LoadNewSongs] 异常: {ex}");
			MessageBox.Show("加载新歌速递失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("加载失败");
		}
		return Task.CompletedTask;
	}

	private async Task LoadNewSongsAll()
	{
		await LoadNewSongsByArea(0, "全部");
	}

	private async Task LoadNewSongsChinese()
	{
		await LoadNewSongsByArea(7, "华语");
	}

	private async Task LoadNewSongsWestern()
	{
		await LoadNewSongsByArea(96, "欧美");
	}

	private async Task LoadNewSongsJapan()
	{
		await LoadNewSongsByArea(8, "日本");
	}

	private async Task LoadNewSongsKorea()
	{
		await LoadNewSongsByArea(16, "韩国");
	}

	private async Task LoadNewSongsByArea(int areaType, string areaName)
	{
		try
		{
			string viewSource = "new_songs_" + areaName.ToLower();
			ViewLoadRequest request = new ViewLoadRequest(viewSource, areaName + "新歌速递", "正在加载" + areaName + "新歌...");
			ViewLoadResult<List<SongInfo>?> loadResult = await RunViewLoadAsync(request, async delegate(CancellationToken token)
			{
				List<SongInfo> songs = await _apiClient.GetNewSongsAsync(areaType).ConfigureAwait(continueOnCapturedContext: false);
				token.ThrowIfCancellationRequested();
				return songs ?? new List<SongInfo>();
			}, "加载" + areaName + "新歌已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				List<SongInfo> songsResult = loadResult.Value ?? new List<SongInfo>();
				DisplaySongs(songsResult, showPagination: false, hasNextPage: false, 1, preserveSelection: false, viewSource, request.AccessibleName);
				_currentPlaylist = null;
				if (songsResult.Count == 0)
				{
					MessageBox.Show("暂无" + areaName + "新歌", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				}
				UpdateStatusBar((songsResult.Count == 0) ? ("暂无" + areaName + "新歌") : $"加载完成，共 {songsResult.Count} 首{areaName}新歌");
				Debug.WriteLine($"[LoadNewSongs] 成功加载 {songsResult.Count} 首{areaName}新歌");
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			if (!TryHandleOperationCancelled(ex2, "加载" + areaName + "新歌已取消"))
			{
				Debug.WriteLine($"[LoadNewSongsByArea] 异常: {ex2}");
				MessageBox.Show("加载" + areaName + "新歌失败: " + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("加载失败");
			}
		}
	}

	private async Task LoadPersonalizedNewSong()
	{
		try
		{
			ViewLoadRequest request = new ViewLoadRequest("personalized_newsong", "推荐新歌", "正在加载推荐新歌...");
			if (!(await RunViewLoadAsync(request, delegate
			{
				using (WorkScopes.BeginSkeleton("PersonalizedNewSong", request.ViewSource))
				{
					return Task.FromResult(result: true);
				}
			}, "加载推荐新歌已取消").ConfigureAwait(continueOnCapturedContext: true)).IsCanceled)
			{
				UpdateStatusBar(request.LoadingText);
				EnrichPersonalizedNewSongAsync(request.ViewSource, request.AccessibleName);
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			if (!TryHandleOperationCancelled(ex2, "加载推荐新歌已取消"))
			{
				Debug.WriteLine($"[LoadPersonalizedNewSong] 异常: {ex2}");
				MessageBox.Show("加载推荐新歌失败: " + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("加载失败");
			}
		}
	}

	private async Task EnrichPersonalizedNewSongAsync(string viewSource, string accessibleName)
	{
		CancellationToken viewToken = GetCurrentViewContentToken();
		using (WorkScopes.BeginEnrichment("PersonalizedNewSong", viewSource))
		{
			try
			{
				List<SongInfo> songsResult = (await FetchWithRetryUntilCancel((CancellationToken ct) => _apiClient.GetPersonalizedNewSongsAsync(), "personalized:new_song", viewToken, delegate(int attempt, Exception _)
				{
					SafeInvoke(delegate
					{
						UpdateStatusBar($"加载推荐新歌失败，正在重试（第 {attempt} 次）...");
					});
				}).ConfigureAwait(continueOnCapturedContext: true)) ?? new List<SongInfo>();
				_dailyRecommendSongsCache = songsResult;
				_dailyRecommendSongsFetchedUtc = DateTime.UtcNow;
				if (viewToken.IsCancellationRequested)
				{
					return;
				}
				await ExecuteOnUiThreadAsync(delegate
				{
					if (!ShouldAbortViewRender(viewToken, "推荐新歌"))
					{
						DisplaySongs(songsResult, showPagination: false, hasNextPage: false, 1, preserveSelection: true, viewSource, accessibleName, skipAvailabilityCheck: false, announceHeader: false, suppressFocus: false);
						_currentPlaylist = null;
						FocusListAfterEnrich(0);
						if (songsResult.Count == 0)
						{
							MessageBox.Show("暂无推荐新歌", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
							UpdateStatusBar("暂无推荐新歌");
						}
						else
						{
							UpdateStatusBar($"加载完成，共 {songsResult.Count} 首推荐新歌");
						}
					}
				}).ConfigureAwait(continueOnCapturedContext: false);
				await EnsureLibraryStateFreshAsync(LibraryEntityType.Songs);
				Debug.WriteLine($"[LoadPersonalizedNewSong] 成功加载 {songsResult.Count} 首推荐新歌");
			}
			catch (Exception ex)
			{
				Exception ex2 = ex;
				if (TryHandleOperationCancelled(ex2, "加载推荐新歌已取消"))
				{
					return;
				}
				Debug.WriteLine($"[LoadPersonalizedNewSong] 异常: {ex2}");
				await ExecuteOnUiThreadAsync(delegate
				{
					if (!ShouldAbortViewRender(viewToken, "推荐新歌"))
					{
						MessageBox.Show("加载推荐新歌失败: " + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
						UpdateStatusBar("加载失败");
					}
				}).ConfigureAwait(continueOnCapturedContext: false);
			}
		}
	}

	private Task LoadPlaylistCategory()
	{
		try
		{
			UpdateStatusBar("正在加载歌单分类...");
			List<ListItemInfo> items = _homePlaylistCategoryPresets.Select(((string Cat, string DisplayName, string Description) preset) => new ListItemInfo
			{
				Type = ListItemType.Category,
				CategoryId = "playlist_cat_" + preset.Cat,
				CategoryName = preset.DisplayName
			}).ToList();
			DisplayListItems(items, "playlist_category", "歌单分类");
			UpdateStatusBar("请选择歌单分类");
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"[LoadPlaylistCategory] 异常: {ex}");
			MessageBox.Show("加载歌单分类失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("加载失败");
		}
		return Task.CompletedTask;
	}

	private async Task LoadPlaylistsByCat(string cat)
	{
		try
		{
			string viewSource = "playlist_cat_" + cat;
			ViewLoadRequest request = new ViewLoadRequest(viewSource, cat + "歌单", "正在加载" + cat + "歌单...");
			ViewLoadResult<(List<PlaylistInfo> Items, string StatusText)?> loadResult = await RunViewLoadAsync(request, (Func<CancellationToken, Task<(List<PlaylistInfo>, string)?>>)async delegate(CancellationToken token)
			{
				(List<PlaylistInfo>, long, bool) result = await _apiClient.GetPlaylistsByCategoryAsync(cat).ConfigureAwait(continueOnCapturedContext: false);
				token.ThrowIfCancellationRequested();
				List<PlaylistInfo> playlists2 = result.Item1 ?? new List<PlaylistInfo>();
				string status = ((playlists2.Count == 0) ? ("暂无" + cat + "歌单") : $"加载完成，共 {playlists2.Count} 个{cat}歌单");
				Debug.WriteLine($"[LoadPlaylistsByCat] 成功加载 {playlists2.Count} 个{cat}歌单, total={result.Item2}, more={result.Item3}");
				return (playlists2, status);
			}, "加载" + cat + "歌单已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				(List<PlaylistInfo> Items, string StatusText)? data = loadResult.Value;
				List<PlaylistInfo> playlists = data?.Items ?? new List<PlaylistInfo>();
				DisplayPlaylists(playlists, preserveSelection: false, viewSource, request.AccessibleName);
				if (playlists.Count == 0)
				{
					MessageBox.Show("暂无" + cat + "歌单", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				}
				UpdateStatusBar(data?.StatusText ?? ("暂无" + cat + "歌单"));
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			if (!TryHandleOperationCancelled(ex2, "加载" + cat + "歌单已取消"))
			{
				Debug.WriteLine($"[LoadPlaylistsByCat] 异常: {ex2}");
				MessageBox.Show("加载" + cat + "歌单失败: " + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("加载失败");
			}
		}
	}

	private async Task LoadNewAlbums()
	{
		try
		{
			ViewLoadRequest request = new ViewLoadRequest("new_albums", "新碟上架", "正在加载新碟上架...");
			ViewLoadResult<List<AlbumInfo>?> loadResult = await RunViewLoadAsync(request, async delegate(CancellationToken token)
			{
				List<AlbumInfo> albums = await _apiClient.GetNewAlbumsAsync().ConfigureAwait(continueOnCapturedContext: false);
				token.ThrowIfCancellationRequested();
				return albums ?? new List<AlbumInfo>();
			}, "加载新碟上架已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				List<AlbumInfo> albumsResult = loadResult.Value ?? new List<AlbumInfo>();
				DisplayAlbums(albumsResult, preserveSelection: false, request.ViewSource, request.AccessibleName);
				if (albumsResult.Count == 0)
				{
					MessageBox.Show("暂无新碟上架", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				}
				UpdateStatusBar((albumsResult.Count == 0) ? "暂无新碟上架" : $"加载完成，共 {albumsResult.Count} 个新专辑");
				Debug.WriteLine($"[LoadNewAlbums] 成功加载 {albumsResult.Count} 个新专辑");
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			if (!TryHandleOperationCancelled(ex2, "加载新碟上架已取消"))
			{
				Debug.WriteLine($"[LoadNewAlbums] 异常: {ex2}");
				MessageBox.Show("加载新碟上架失败: " + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("加载失败");
			}
		}
	}

	private async Task LoadUserLikedSongs(bool preserveSelection = false, bool skipSaveNavigation = false)
	{
		try
		{
			await EnsureLibraryStateFreshAsync(LibraryEntityType.Songs);
			int pendingIndex = ((preserveSelection && resultListView.SelectedIndices.Count > 0) ? resultListView.SelectedIndices[0] : 0);
			if (!skipSaveNavigation)
			{
				ResetPendingListFocusIfViewChanged("user_liked_songs");
			}
			ViewLoadRequest request = new ViewLoadRequest("user_liked_songs", "喜欢的音乐", "正在加载喜欢的音乐...", !skipSaveNavigation, pendingIndex);
			ViewLoadResult<(PlaylistInfo Playlist, List<SongInfo> Songs, string StatusText)?> loadResult = await RunViewLoadAsync(request, (CancellationToken token) => BuildPlaylistSkeletonViewDataAsync(async delegate(CancellationToken ct)
			{
				ct.ThrowIfCancellationRequested();
				if (_userLikedPlaylist != null)
				{
					return _userLikedPlaylist;
				}
				UserAccountInfo userInfo = await _apiClient.GetUserAccountAsync().ConfigureAwait(continueOnCapturedContext: false);
				ct.ThrowIfCancellationRequested();
				if (userInfo == null || userInfo.UserId <= 0)
				{
					throw new InvalidOperationException("请先登录有效账号。");
				}
				_loggedInUserId = userInfo.UserId;
				List<PlaylistInfo> playlists;
				(playlists, _) = await _apiClient.GetUserPlaylistsAsync(userInfo.UserId).ConfigureAwait(continueOnCapturedContext: false);
				ct.ThrowIfCancellationRequested();
				PlaylistInfo likedPlaylist = playlists?.FirstOrDefault((PlaylistInfo p) => IsLikedMusicPlaylist(p, userInfo.UserId));
				if (likedPlaylist != null)
				{
					_userLikedPlaylist = likedPlaylist;
				}
				return likedPlaylist;
			}, "user_liked_songs", token), "加载喜欢的音乐已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				(PlaylistInfo Playlist, List<SongInfo> Songs, string StatusText)? data = loadResult.Value;
				if (!data.HasValue || data.Value.Playlist == null)
				{
					MessageBox.Show("未找到喜欢的音乐歌单或歌单为空。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
					UpdateStatusBar("未找到喜欢的音乐");
					return;
				}
				(PlaylistInfo Playlist, List<SongInfo> Songs, string StatusText) viewData = data.Value;
				(_currentPlaylist, _, _) = viewData;
				DisplaySongs(viewData.Songs, showPagination: false, hasNextPage: false, 1, preserveSelection, request.ViewSource, request.AccessibleName, skipAvailabilityCheck: true, announceHeader: true, suppressFocus: false, allowSelection: true);
				UpdateStatusBar(viewData.StatusText);
				EnrichCurrentPlaylistSongsAsync(viewData.Playlist, viewData.Songs, request.ViewSource, request.AccessibleName);
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			if (!TryHandleOperationCancelled(ex2, "加载喜欢的音乐已取消"))
			{
				Debug.WriteLine($"[LoadUserLikedSongs] 异常: {ex2}");
				MessageBox.Show("加载喜欢的音乐失败: " + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("加载喜欢的音乐失败");
			}
		}
	}

	private async Task LoadUserPlaylists(bool preserveSelection = false)
	{
		try
		{
			await EnsureLibraryStateFreshAsync(LibraryEntityType.Playlists);
			int pendingIndex = ((preserveSelection && resultListView.SelectedIndices.Count > 0) ? resultListView.SelectedIndices[0] : 0);
			ViewLoadRequest request = new ViewLoadRequest("user_playlists", "我的歌单", "正在加载我的歌单...", cancelActiveNavigation: true, pendingIndex);
			ViewLoadResult<(List<PlaylistInfo> Items, string StatusText)?> loadResult = await RunViewLoadAsync(request, (Func<CancellationToken, Task<(List<PlaylistInfo>, string)?>>)async delegate(CancellationToken token)
			{
				UserAccountInfo userInfo = await _apiClient.GetUserAccountAsync().ConfigureAwait(continueOnCapturedContext: false);
				token.ThrowIfCancellationRequested();
				if (userInfo == null || userInfo.UserId <= 0)
				{
					throw new InvalidOperationException("请先登录网易云账号。");
				}
				_loggedInUserId = userInfo.UserId;
				List<PlaylistInfo> playlists;
				(playlists, _) = await _apiClient.GetUserPlaylistsAsync(userInfo.UserId).ConfigureAwait(continueOnCapturedContext: false);
				token.ThrowIfCancellationRequested();
				List<PlaylistInfo> filtered = (playlists ?? new List<PlaylistInfo>()).Where((PlaylistInfo p) => !IsLikedMusicPlaylist(p, userInfo.UserId)).ToList();
				string status = ((filtered.Count == 0) ? "暂无歌单" : $"加载完成，共 {filtered.Count} 个歌单");
				return (filtered, status);
			}, "加载我的歌单已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				(List<PlaylistInfo> Items, string StatusText)? data = loadResult.Value;
				if (!data.HasValue || data.Value.Items.Count == 0)
				{
					MessageBox.Show("您还没有创建或收藏歌单。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
					UpdateStatusBar("暂无歌单");
				}
				else
				{
					DisplayPlaylists(data.Value.Items, preserveSelection, request.ViewSource, request.AccessibleName);
					_currentPlaylist = null;
					UpdateStatusBar(data.Value.StatusText);
				}
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			if (TryHandleOperationCancelled(ex2, "加载我的歌单已取消"))
			{
				return;
			}
			Debug.WriteLine($"[LoadUserPlaylists] 异常: {ex2}");
			throw;
		}
	}

	private static bool IsLikedMusicPlaylist(PlaylistInfo? playlist, long userId)
	{
		if (playlist == null)
		{
			return false;
		}
		string b = userId.ToString();
		if (!string.IsNullOrWhiteSpace(playlist.Id) && string.Equals(playlist.Id, b, StringComparison.Ordinal))
		{
			return true;
		}
		if (!string.IsNullOrWhiteSpace(playlist.Name) && playlist.Name.IndexOf("喜欢的音乐", StringComparison.OrdinalIgnoreCase) >= 0 && (playlist.OwnerUserId == userId || playlist.CreatorId == userId))
		{
			return true;
		}
		return false;
	}

	private async Task LoadUserAlbums(bool preserveSelection = false)
	{
		try
		{
			await EnsureLibraryStateFreshAsync(LibraryEntityType.Albums);
			int pendingIndex = ((preserveSelection && resultListView.SelectedIndices.Count > 0) ? resultListView.SelectedIndices[0] : 0);
			ViewLoadRequest request = new ViewLoadRequest("user_albums", "收藏的专辑", "正在加载收藏的专辑...", cancelActiveNavigation: true, pendingIndex);
			ViewLoadResult<(List<AlbumInfo> Items, string StatusText)?> loadResult = await RunViewLoadAsync(request, (Func<CancellationToken, Task<(List<AlbumInfo>, string)?>>)async delegate(CancellationToken token)
			{
				List<AlbumInfo> albums;
				int totalCount;
				(albums, totalCount) = await _apiClient.GetUserAlbumsAsync().ConfigureAwait(continueOnCapturedContext: false);
				token.ThrowIfCancellationRequested();
				List<AlbumInfo> normalized = albums ?? new List<AlbumInfo>();
				string status = ((normalized.Count == 0) ? "暂无收藏的专辑" : $"加载完成，共 {totalCount} 个专辑");
				return (normalized, status);
			}, "加载收藏的专辑已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				(List<AlbumInfo> Items, string StatusText)? data = loadResult.Value;
				if (!data.HasValue || data.Value.Items.Count == 0)
				{
					MessageBox.Show("您还没有收藏专辑。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
					UpdateStatusBar("暂无收藏的专辑");
				}
				else
				{
					DisplayAlbums(data.Value.Items, preserveSelection, request.ViewSource, request.AccessibleName);
					UpdateStatusBar(data.Value.StatusText);
				}
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			if (TryHandleOperationCancelled(ex2, "加载收藏的专辑已取消"))
			{
				return;
			}
			Debug.WriteLine($"[LoadUserAlbums] 异常: {ex2}");
			throw;
		}
	}

	private async Task LoadRecentListenedCategoryAsync(bool skipSave = false)
	{
		if (!IsUserLoggedIn())
		{
			MessageBox.Show("请先登录网易云账号以查看最近听过内容。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			await LoadHomePageAsync(skipSave: true, showErrorDialog: false);
			return;
		}
		try
		{
			ViewLoadRequest request = new ViewLoadRequest("recent_listened", "最近听过", "正在加载最近听过骨架...", !skipSave);
			ViewLoadResult<(List<ListItemInfo> Items, string StatusText)?> loadResult = await RunViewLoadAsync(request, (CancellationToken _) => Task.FromResult(((List<ListItemInfo>, string)?)BuildRecentListenedSkeletonViewData()), "加载最近听过已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				(List<ListItemInfo> Items, string StatusText) data = loadResult.Value ?? BuildRecentListenedSkeletonViewData();
				DisplayListItems(data.Items, request.ViewSource, request.AccessibleName, preserveSelection: true, announceHeader: false);
				UpdateStatusBar(data.StatusText);
				FocusListAfterEnrich(0);
				EnrichRecentListenedAsync(request.ViewSource, request.AccessibleName);
			}
		}
		catch (Exception ex)
		{
			if (!TryHandleOperationCancelled(ex, "加载最近听过已取消"))
			{
				Debug.WriteLine($"[RecentListened] 加载失败: {ex}");
				MessageBox.Show("加载最近听过失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("加载最近听过失败");
			}
		}
	}

	private (List<ListItemInfo> Items, string StatusText) BuildRecentListenedSkeletonViewData()
	{
		List<ListItemInfo> item = BuildRecentListenedEntries();
		string status = (_recentSummaryReady ? (BuildRecentListenedStatus() + "（缓存）") : "最近听过");
		return (Items: item, StatusText: status);
	}

	private async Task EnrichRecentListenedAsync(string viewSource, string accessibleName)
	{
		CancellationToken viewToken = GetCurrentViewContentToken();
		try
		{
			await RefreshRecentSummariesAsync(forceRefresh: true, viewToken).ConfigureAwait(continueOnCapturedContext: false);
			if (viewToken.IsCancellationRequested)
			{
				return;
			}
			List<ListItemInfo> items = BuildRecentListenedEntries();
			string status = BuildRecentListenedStatus();
			await ExecuteOnUiThreadAsync(delegate
			{
				if (string.Equals(_currentViewSource, viewSource, StringComparison.OrdinalIgnoreCase))
				{
					DisplayListItems(items, viewSource, accessibleName, preserveSelection: true, announceHeader: false);
					FocusListAfterEnrich(0);
					UpdateStatusBar(status);
				}
			}).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			if (!TryHandleOperationCancelled(ex2, "加载最近听过已取消"))
			{
				Debug.WriteLine($"[RecentListened] 丰富失败: {ex2}");
			}
		}
	}

	private async Task LoadRecentPlayedSongsAsync()
	{
		if (!IsUserLoggedIn())
		{
			MessageBox.Show("请先登录网易云账号以查看最近播放记录。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			await LoadHomePageAsync(skipSave: true, showErrorDialog: false);
			return;
		}
		try
		{
			ViewLoadRequest request = new ViewLoadRequest("recent_play", "最近播放", "正在加载最近播放骨架...");
			ViewLoadResult<(List<SongInfo> Items, string StatusText)?> loadResult = await RunViewLoadAsync(request, (CancellationToken _) => Task.FromResult(((List<SongInfo>, string)?)BuildRecentSongsSkeletonViewData()), "加载最近播放已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				(List<SongInfo> Items, string StatusText) data = loadResult.Value ?? BuildRecentSongsSkeletonViewData();
				List<SongInfo> songs;
				(songs, _) = data;
				DisplaySongs(songs, showPagination: false, hasNextPage: false, 1, preserveSelection: true, request.ViewSource, request.AccessibleName, skipAvailabilityCheck: true, announceHeader: true, suppressFocus: false, allowSelection: true);
				_currentPlaylist = null;
				UpdateStatusBar(data.StatusText);
				EnrichRecentSongsAsync(request.ViewSource, request.AccessibleName);
			}
		}
		catch (Exception ex)
		{
			if (TryHandleOperationCancelled(ex, "加载最近播放已取消"))
			{
				return;
			}
			Debug.WriteLine($"[LoadRecentPlayedSongs] 异常: {ex}");
			MessageBox.Show("加载最近播放失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			throw;
		}
	}

	private (List<SongInfo> Items, string StatusText) BuildRecentSongsSkeletonViewData()
	{
		List<SongInfo> list = ((_recentSongsCache.Count > 0) ? new List<SongInfo>(_recentSongsCache) : new List<SongInfo>());
		string item = ((list.Count > 0) ? $"最近播放（缓存）共 {list.Count} 首，正在刷新..." : "正在刷新最近播放...");
		return (Items: list, StatusText: item);
	}

	private async Task EnrichRecentSongsAsync(string viewSource, string accessibleName)
	{
		CancellationToken viewToken = GetCurrentViewContentToken();
		try
		{
			List<SongInfo> list = await FetchRecentSongsAsync(300, viewToken).ConfigureAwait(continueOnCapturedContext: false);
			if (viewToken.IsCancellationRequested || list == null)
			{
				return;
			}
			_recentSongsCache = new List<SongInfo>(list);
			_recentPlayCount = list.Count;
			string status = ((list.Count == 0) ? "暂无最近播放记录" : $"最近播放，共 {list.Count} 首歌曲");
			await ExecuteOnUiThreadAsync(delegate
			{
				if (string.Equals(_currentViewSource, viewSource, StringComparison.OrdinalIgnoreCase))
				{
					DisplaySongs(list, showPagination: false, hasNextPage: false, 1, preserveSelection: true, viewSource, accessibleName, skipAvailabilityCheck: false, announceHeader: false, suppressFocus: false);
					_currentPlaylist = null;
					FocusListAfterEnrich(0);
					if (list.Count == 0)
					{
						MessageBox.Show("暂时没有最近播放记录。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
					}
					UpdateStatusBar(status);
				}
			}).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			if (!TryHandleOperationCancelled(ex2, "加载最近播放已取消"))
			{
				Debug.WriteLine($"[RecentPlay] 丰富最近播放失败: {ex2}");
			}
		}
	}

	private async Task LoadUserPodcasts(bool preserveSelection = false)
	{
		try
		{
			await EnsureLibraryStateFreshAsync(LibraryEntityType.Podcasts);
			int pendingIndex = ((preserveSelection && resultListView.SelectedIndices.Count > 0) ? resultListView.SelectedIndices[0] : 0);
			ViewLoadRequest request = new ViewLoadRequest("user_podcasts", "收藏的电台", "正在加载收藏的电台...", cancelActiveNavigation: true, pendingIndex);
			ViewLoadResult<(List<PodcastRadioInfo> Items, string StatusText)?> loadResult = await RunViewLoadAsync(request, (Func<CancellationToken, Task<(List<PodcastRadioInfo>, string)?>>)async delegate(CancellationToken token)
			{
				List<PodcastRadioInfo> podcasts;
				int totalCount;
				(podcasts, totalCount) = await _apiClient.GetSubscribedPodcastsAsync(300).ConfigureAwait(continueOnCapturedContext: false);
				token.ThrowIfCancellationRequested();
				List<PodcastRadioInfo> normalized = podcasts ?? new List<PodcastRadioInfo>();
				string status = ((normalized.Count == 0) ? "暂无收藏的电台" : $"加载完成，共 {totalCount} 个电台");
				return (normalized, status);
			}, "加载收藏的电台已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				(List<PodcastRadioInfo> Items, string StatusText)? data = loadResult.Value;
				if (!data.HasValue || data.Value.Items.Count == 0)
				{
					MessageBox.Show("您还没有收藏电台。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
					UpdateStatusBar("暂无收藏的电台");
				}
				else
				{
					DisplayPodcasts(data.Value.Items, showPagination: false, hasNextPage: false, 1, preserveSelection, request.ViewSource, request.AccessibleName);
					UpdateStatusBar(data.Value.StatusText);
				}
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			if (TryHandleOperationCancelled(ex2, "加载收藏的电台已取消"))
			{
				return;
			}
			Debug.WriteLine($"[LoadUserPodcasts] 异常: {ex2}");
			throw;
		}
	}

	private async Task LoadRecentPodcastsAsync()
	{
		if (!IsUserLoggedIn())
		{
			MessageBox.Show("请先登录网易云账号以查看最近播客。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			await LoadHomePageAsync(skipSave: true, showErrorDialog: false);
			return;
		}
		try
		{
			ViewLoadRequest request = new ViewLoadRequest("recent_podcasts", "最近播客", "正在加载最近播客骨架...");
			ViewLoadResult<(List<PodcastRadioInfo> Items, string StatusText)?> loadResult = await RunViewLoadAsync(request, (CancellationToken _) => Task.FromResult(((List<PodcastRadioInfo>, string)?)BuildRecentPodcastsSkeletonViewData()), "加载最近播客已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				(List<PodcastRadioInfo> Items, string StatusText) data = loadResult.Value ?? BuildRecentPodcastsSkeletonViewData();
				List<PodcastRadioInfo> podcasts = data.Items ?? new List<PodcastRadioInfo>();
				DisplayPodcasts(podcasts, showPagination: false, hasNextPage: false, 1, preserveSelection: true, request.ViewSource, request.AccessibleName, announceHeader: true, suppressFocus: false, allowSelection: true);
				UpdateStatusBar(data.StatusText);
				EnrichRecentPodcastsAsync(request.ViewSource, request.AccessibleName);
			}
		}
		catch (Exception ex)
		{
			if (TryHandleOperationCancelled(ex, "加载最近播客已取消"))
			{
				return;
			}
			Debug.WriteLine($"[LoadRecentPodcasts] 异常: {ex}");
			MessageBox.Show("加载最近播客失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			throw;
		}
	}

	private (List<PodcastRadioInfo> Items, string StatusText) BuildRecentPodcastsSkeletonViewData()
	{
		List<PodcastRadioInfo> list = ((_recentPodcastsCache.Count > 0) ? new List<PodcastRadioInfo>(_recentPodcastsCache) : new List<PodcastRadioInfo>());
		string item = ((list.Count > 0) ? $"最近播客（缓存）共 {list.Count} 个，正在刷新..." : "正在刷新最近播客...");
		return (Items: list, StatusText: item);
	}

	private async Task EnrichRecentPodcastsAsync(string viewSource, string accessibleName)
	{
		CancellationToken viewToken = GetCurrentViewContentToken();
		try
		{
			List<PodcastRadioInfo> list = await FetchRecentPodcastsAsync(100, viewToken).ConfigureAwait(continueOnCapturedContext: false);
			if (viewToken.IsCancellationRequested || list == null)
			{
				return;
			}
			_recentPodcastsCache = new List<PodcastRadioInfo>(list);
			_recentPodcastCount = list.Count;
			string status = ((list.Count == 0) ? "暂无最近播放的播客" : $"最近播客，共 {list.Count} 个");
			await ExecuteOnUiThreadAsync(delegate
			{
				if (string.Equals(_currentViewSource, viewSource, StringComparison.OrdinalIgnoreCase))
				{
					DisplayPodcasts(list, showPagination: false, hasNextPage: false, 1, preserveSelection: true, viewSource, accessibleName, announceHeader: false, suppressFocus: false);
					FocusListAfterEnrich(0);
					if (list.Count == 0)
					{
						MessageBox.Show("暂时没有最近播放的播客。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
					}
					UpdateStatusBar(status);
				}
			}).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			if (!TryHandleOperationCancelled(ex2, "加载最近播客已取消"))
			{
				Debug.WriteLine($"[RecentPodcasts] 丰富最近播客失败: {ex2}");
			}
		}
	}

	private async Task OpenPodcastRadioAsync(PodcastRadioInfo podcast, bool skipSave = false)
	{
		if (podcast == null)
		{
			MessageBox.Show("无法打开播客，缺少有效信息。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
		}
		else
		{
			await LoadPodcastEpisodesAsync(podcast.Id, 0, skipSave, podcast);
		}
	}

	private async Task LoadPodcastEpisodesAsync(long radioId, int offset, bool skipSave = false, PodcastRadioInfo? podcastInfo = null, bool? sortAscendingOverride = null)
	{
		if (radioId <= 0)
		{
			MessageBox.Show("无法加载播客节目，缺少电台标识。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			return;
		}
		checked
		{
			try
			{
				UpdateStatusBar("正在加载播客...");
				if (!skipSave)
				{
					SaveNavigationState();
				}
				bool isDifferentRadio = _currentPodcast == null || _currentPodcast.Id != radioId;
				if (podcastInfo != null)
				{
					_currentPodcast = podcastInfo;
				}
				else if (_currentPodcast == null || _currentPodcast.Id != radioId)
				{
					PodcastRadioInfo detail = await _apiClient.GetPodcastRadioDetailAsync(radioId);
					if (detail != null)
					{
						_currentPodcast = detail;
					}
				}
				if (isDifferentRadio && !sortAscendingOverride.HasValue)
				{
					_podcastSortState.SetOption(option: false);
				}
				if (sortAscendingOverride.HasValue)
				{
					_podcastSortState.SetOption(sortAscendingOverride.Value);
				}
				bool isAscending = _podcastSortState.CurrentOption;
				(List<PodcastEpisodeInfo> Episodes, bool HasMore, int TotalCount) tuple = await _apiClient.GetPodcastEpisodesAsync(radioId, 50, Math.Max(0, offset), isAscending);
				List<PodcastEpisodeInfo> episodes = tuple.Episodes;
				bool hasMore = tuple.HasMore;
				int totalCount = tuple.TotalCount;
				string accessibleName = _currentPodcast?.Name ?? "播客节目";
				string viewSource = $"podcast:{radioId}:offset{Math.Max(0, offset)}";
				if (isAscending)
				{
					viewSource += ":asc1";
				}
				_currentPodcastSoundOffset = Math.Max(0, offset);
				_currentPodcastHasMore = hasMore;
				DisplayPodcastEpisodes(episodes, unchecked(_currentPodcastSoundOffset > 0 || hasMore), hasMore, _currentPodcastSoundOffset + 1, preserveSelection: false, viewSource, accessibleName);
				UpdatePodcastSortMenuChecks();
				if (episodes == null || episodes.Count == 0)
				{
					UpdateStatusBar(accessibleName + "，暂无节目");
					return;
				}
				int currentPage = unchecked(_currentPodcastSoundOffset / 50) + 1;
				int totalPages = Math.Max(1, (int)Math.Ceiling((double)totalCount / 50.0));
				UpdateStatusBar($"{accessibleName}：第 {currentPage}/{totalPages} 页，本页 {episodes.Count} 个节目");
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"[Podcast] 加载播客失败: {ex}");
				MessageBox.Show("加载播客失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("加载播客失败");
			}
		}
	}

	private static void ParsePodcastViewSource(string? viewSource, out long radioId, out int offset, out bool ascending)
	{
		radioId = 0L;
		offset = 0;
		ascending = false;
		if (string.IsNullOrWhiteSpace(viewSource) || !viewSource.StartsWith("podcast:", StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		string[] array = viewSource.Split(new char[1] { ':' }, StringSplitOptions.RemoveEmptyEntries);
		if (array.Length >= 2)
		{
			long.TryParse(array[1], out radioId);
		}
		foreach (string item in array.Skip(2))
		{
			if (item.StartsWith("offset", StringComparison.OrdinalIgnoreCase) && int.TryParse(item.Substring("offset".Length), out var result))
			{
				offset = result;
			}
			else if (item.StartsWith("asc", StringComparison.OrdinalIgnoreCase))
			{
				string text = item.Substring("asc".Length);
				int result2;
				if (string.IsNullOrEmpty(text))
				{
					ascending = true;
				}
				else if (int.TryParse(text, out result2))
				{
					ascending = result2 != 0;
				}
			}
		}
	}

	private async Task LoadRecentPlaylistsAsync()
	{
		if (!IsUserLoggedIn())
		{
			MessageBox.Show("请先登录网易云账号以查看最近播放的歌单。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			await LoadHomePageAsync(skipSave: true, showErrorDialog: false);
			return;
		}
		try
		{
			ViewLoadRequest request = new ViewLoadRequest("recent_playlists", "最近歌单", "正在加载最近歌单骨架...");
			ViewLoadResult<(List<PlaylistInfo> Items, string StatusText)?> loadResult = await RunViewLoadAsync(request, (CancellationToken _) => Task.FromResult(((List<PlaylistInfo>, string)?)BuildRecentPlaylistsSkeletonViewData()), "加载最近歌单已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				(List<PlaylistInfo> Items, string StatusText) data = loadResult.Value ?? BuildRecentPlaylistsSkeletonViewData();
				List<PlaylistInfo> playlists;
				(playlists, _) = data;
				DisplayPlaylists(playlists, preserveSelection: true, request.ViewSource, request.AccessibleName, 1, showPagination: false, hasNextPage: false, announceHeader: true, suppressFocus: false, allowSelection: true);
				UpdateStatusBar(data.StatusText);
				EnrichRecentPlaylistsAsync(request.ViewSource, request.AccessibleName);
			}
		}
		catch (Exception ex)
		{
			if (TryHandleOperationCancelled(ex, "加载最近歌单已取消"))
			{
				return;
			}
			Debug.WriteLine($"[LoadRecentPlaylists] 异常: {ex}");
			MessageBox.Show("加载最近歌单失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			throw;
		}
	}

	private (List<PlaylistInfo> Items, string StatusText) BuildRecentPlaylistsSkeletonViewData()
	{
		List<PlaylistInfo> list = ((_recentPlaylistsCache.Count > 0) ? new List<PlaylistInfo>(_recentPlaylistsCache) : new List<PlaylistInfo>());
		string item = ((list.Count > 0) ? $"最近歌单（缓存）共 {list.Count} 个，正在刷新..." : "正在刷新最近歌单...");
		return (Items: list, StatusText: item);
	}

	private async Task EnrichRecentPlaylistsAsync(string viewSource, string accessibleName)
	{
		CancellationToken viewToken = GetCurrentViewContentToken();
		try
		{
			List<PlaylistInfo> list = await FetchRecentPlaylistsAsync(100, viewToken).ConfigureAwait(continueOnCapturedContext: false);
			if (viewToken.IsCancellationRequested || list == null)
			{
				return;
			}
			_recentPlaylistsCache = new List<PlaylistInfo>(list);
			_recentPlaylistCount = list.Count;
			string status = ((list.Count == 0) ? "暂无最近播放的歌单" : $"最近歌单，共 {list.Count} 个");
			await ExecuteOnUiThreadAsync(delegate
			{
				if (string.Equals(_currentViewSource, viewSource, StringComparison.OrdinalIgnoreCase))
				{
					DisplayPlaylists(list, preserveSelection: true, viewSource, accessibleName, 1, showPagination: false, hasNextPage: false, announceHeader: false, suppressFocus: false);
					FocusListAfterEnrich(0);
					if (list.Count == 0)
					{
						MessageBox.Show("暂时没有最近播放的歌单。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
					}
					UpdateStatusBar(status);
				}
			}).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			if (!TryHandleOperationCancelled(ex2, "加载最近歌单已取消"))
			{
				Debug.WriteLine($"[RecentPlaylists] 丰富最近歌单失败: {ex2}");
			}
		}
	}

	private async Task LoadRecentAlbumsAsync()
	{
		if (!IsUserLoggedIn())
		{
			MessageBox.Show("请先登录网易云账号以查看最近播放的专辑。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			await LoadHomePageAsync(skipSave: true, showErrorDialog: false);
			return;
		}
		try
		{
			ViewLoadRequest request = new ViewLoadRequest("recent_albums", "最近专辑", "正在加载最近专辑骨架...");
			ViewLoadResult<(List<AlbumInfo> Items, string StatusText)?> loadResult = await RunViewLoadAsync(request, (CancellationToken _) => Task.FromResult(((List<AlbumInfo>, string)?)BuildRecentAlbumsSkeletonViewData()), "加载最近专辑已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				(List<AlbumInfo> Items, string StatusText) data = loadResult.Value ?? BuildRecentAlbumsSkeletonViewData();
				List<AlbumInfo> albums;
				(albums, _) = data;
				DisplayAlbums(albums, preserveSelection: true, request.ViewSource, request.AccessibleName, 1, showPagination: false, hasNextPage: false, announceHeader: true, suppressFocus: false, allowSelection: true);
				UpdateStatusBar(data.StatusText);
				EnrichRecentAlbumsAsync(request.ViewSource, request.AccessibleName);
			}
		}
		catch (Exception ex)
		{
			if (TryHandleOperationCancelled(ex, "加载最近专辑已取消"))
			{
				return;
			}
			Debug.WriteLine($"[LoadRecentAlbums] 异常: {ex}");
			MessageBox.Show("加载最近专辑失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			throw;
		}
	}

	private (List<AlbumInfo> Items, string StatusText) BuildRecentAlbumsSkeletonViewData()
	{
		List<AlbumInfo> list = ((_recentAlbumsCache.Count > 0) ? new List<AlbumInfo>(_recentAlbumsCache) : new List<AlbumInfo>());
		string item = ((list.Count > 0) ? $"最近专辑（缓存）共 {list.Count} 张，正在刷新..." : "正在刷新最近专辑...");
		return (Items: list, StatusText: item);
	}

	private async Task EnrichRecentAlbumsAsync(string viewSource, string accessibleName)
	{
		CancellationToken viewToken = GetCurrentViewContentToken();
		try
		{
			List<AlbumInfo> list = await FetchRecentAlbumsAsync(100, viewToken).ConfigureAwait(continueOnCapturedContext: false);
			if (viewToken.IsCancellationRequested || list == null)
			{
				return;
			}
			_recentAlbumsCache = new List<AlbumInfo>(list);
			_recentAlbumCount = list.Count;
			string status = ((list.Count == 0) ? "暂无最近播放的专辑" : $"最近专辑，共 {list.Count} 张");
			await ExecuteOnUiThreadAsync(delegate
			{
				if (string.Equals(_currentViewSource, viewSource, StringComparison.OrdinalIgnoreCase))
				{
					DisplayAlbums(list, preserveSelection: true, viewSource, accessibleName, 1, showPagination: false, hasNextPage: false, announceHeader: false, suppressFocus: false);
					FocusListAfterEnrich(0);
					if (list.Count == 0)
					{
						MessageBox.Show("暂时没有最近播放的专辑。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
					}
					UpdateStatusBar(status);
				}
			}).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			if (!TryHandleOperationCancelled(ex2, "加载最近专辑已取消"))
			{
				Debug.WriteLine($"[RecentAlbums] 丰富最近专辑失败: {ex2}");
			}
		}
	}

	private async Task<(List<SongInfo> Songs, List<PlaylistInfo> Playlists)> FetchDailyRecommendBundleAsync(CancellationToken token, bool allowCache = true)
	{
		if (!IsUserLoggedIn())
		{
			return (Songs: new List<SongInfo>(), Playlists: new List<PlaylistInfo>());
		}
		if (allowCache && _dailyRecommendSongsCache != null && _dailyRecommendPlaylistsCache != null && IsRecommendationCacheFresh(_dailyRecommendSongsFetchedUtc) && IsRecommendationCacheFresh(_dailyRecommendPlaylistsFetchedUtc))
		{
			return (Songs: _dailyRecommendSongsCache, Playlists: _dailyRecommendPlaylistsCache);
		}
		Task<List<SongInfo>> songsTask = _apiClient.GetDailyRecommendSongsAsync();
		Task<List<PlaylistInfo>> playlistsTask = _apiClient.GetDailyRecommendPlaylistsAsync();
		await Task.WhenAll(songsTask, playlistsTask).ConfigureAwait(continueOnCapturedContext: false);
		token.ThrowIfCancellationRequested();
		_dailyRecommendSongsCache = songsTask.Result ?? new List<SongInfo>();
		_dailyRecommendPlaylistsCache = playlistsTask.Result ?? new List<PlaylistInfo>();
		_dailyRecommendSongsFetchedUtc = (_dailyRecommendPlaylistsFetchedUtc = DateTime.UtcNow);
		_dailyRecommendCacheFetchedUtc = _dailyRecommendSongsFetchedUtc;
		return (Songs: _dailyRecommendSongsCache, Playlists: _dailyRecommendPlaylistsCache);
	}

	private async Task<(List<PlaylistInfo> Playlists, List<SongInfo> Songs)> FetchPersonalizedBundleAsync(CancellationToken token, bool allowCache = true)
	{
		if (allowCache && _personalizedPlaylistsCache != null && _personalizedNewSongsCache != null && IsRecommendationCacheFresh(_personalizedCacheFetchedUtc))
		{
			return (Playlists: _personalizedPlaylistsCache, Songs: _personalizedNewSongsCache);
		}
		Task<List<PlaylistInfo>> playlistsTask = _apiClient.GetPersonalizedPlaylistsAsync();
		Task<List<SongInfo>> songsTask = _apiClient.GetPersonalizedNewSongsAsync(20);
		await Task.WhenAll(playlistsTask, songsTask).ConfigureAwait(continueOnCapturedContext: false);
		token.ThrowIfCancellationRequested();
		_personalizedPlaylistsCache = playlistsTask.Result ?? new List<PlaylistInfo>();
		_personalizedNewSongsCache = songsTask.Result ?? new List<SongInfo>();
		_personalizedCacheFetchedUtc = DateTime.UtcNow;
		return (Playlists: _personalizedPlaylistsCache, Songs: _personalizedNewSongsCache);
	}

	private async Task LoadDailyRecommend()
	{
		try
		{
			ViewLoadRequest request = new ViewLoadRequest("daily_recommend", "每日推荐", "正在加载每日推荐...");
			ViewLoadResult<(List<ListItemInfo> Items, string StatusText)> loadResult = await RunViewLoadAsync(request, async delegate(CancellationToken viewToken)
			{
				(List<SongInfo> Songs, List<PlaylistInfo> Playlists) bundle = await FetchDailyRecommendBundleAsync(viewToken);
				viewToken.ThrowIfCancellationRequested();
				int songCount = bundle.Songs?.Count ?? 0;
				int playlistCount = bundle.Playlists?.Count ?? 0;
				_homeCachedDailyRecommendSongCount = songCount;
				_homeCachedDailyRecommendPlaylistCount = playlistCount;
				List<ListItemInfo> items = new List<ListItemInfo>
				{
					new ListItemInfo
					{
						Type = ListItemType.Category,
						CategoryId = "daily_recommend_songs",
						CategoryName = "每日推荐歌曲",
						ItemCount = songCount,
						ItemUnit = "首"
					},
					new ListItemInfo
					{
						Type = ListItemType.Category,
						CategoryId = "daily_recommend_playlists",
						CategoryName = "每日推荐歌单",
						ItemCount = playlistCount,
						ItemUnit = "个"
					}
				};
				string status = ((checked(songCount + playlistCount) > 0) ? $"每日推荐：歌曲 {songCount} 首 / 歌单 {playlistCount} 个" : "每日推荐");
				return (Items: items, StatusText: status);
			}, "加载每日推荐已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				(List<ListItemInfo> Items, string StatusText) data = loadResult.Value;
				DisplayListItems(data.Items, request.ViewSource, request.AccessibleName, preserveSelection: true);
				UpdateStatusBar(data.StatusText);
				FocusListAfterEnrich(0);
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Debug.WriteLine($"[LoadDailyRecommend] 异常: {ex2}");
			throw;
		}
	}

	private async Task LoadPersonalized()
	{
		try
		{
			ViewLoadRequest request = new ViewLoadRequest("personalized", "为您推荐", "正在加载为您推荐...");
			ViewLoadResult<(List<ListItemInfo> Items, string StatusText)> loadResult = await RunViewLoadAsync(request, async delegate(CancellationToken viewToken)
			{
				(List<PlaylistInfo> Playlists, List<SongInfo> Songs) bundle = await FetchPersonalizedBundleAsync(viewToken);
				viewToken.ThrowIfCancellationRequested();
				int playlistCount = bundle.Playlists?.Count ?? 0;
				int songCount = bundle.Songs?.Count ?? 0;
				_homeCachedPersonalizedPlaylistCount = playlistCount;
				_homeCachedPersonalizedSongCount = songCount;
				List<ListItemInfo> items = new List<ListItemInfo>
				{
					new ListItemInfo
					{
						Type = ListItemType.Category,
						CategoryId = "personalized_newsongs",
						CategoryName = "推荐新歌",
						ItemCount = songCount,
						ItemUnit = "首"
					},
					new ListItemInfo
					{
						Type = ListItemType.Category,
						CategoryId = "personalized_playlists",
						CategoryName = "推荐歌单",
						ItemCount = playlistCount,
						ItemUnit = "个"
					}
				};
				string status = ((checked(playlistCount + songCount) > 0) ? $"为您推荐：歌单 {playlistCount} 个 / 歌曲 {songCount} 首" : "为您推荐");
				return (Items: items, StatusText: status);
			}, "加载个性化推荐已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				(List<ListItemInfo> Items, string StatusText) data = loadResult.Value;
				DisplayListItems(data.Items, request.ViewSource, request.AccessibleName, preserveSelection: true);
				UpdateStatusBar(data.StatusText);
				FocusListAfterEnrich(0);
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Debug.WriteLine($"[LoadPersonalized] 异常: {ex2}");
			throw;
		}
	}

	private async Task LoadToplist()
	{
		try
		{
			ViewLoadRequest request = new ViewLoadRequest("toplist", "官方排行榜", "正在加载官方排行榜...");
			if (!(await RunViewLoadAsync(request, delegate
			{
				using (WorkScopes.BeginSkeleton("Toplist", request.ViewSource))
				{
					return Task.FromResult(result: true);
				}
			}, "加载排行榜已取消").ConfigureAwait(continueOnCapturedContext: true)).IsCanceled)
			{
				UpdateStatusBar(request.LoadingText);
				EnrichToplistAsync(request.ViewSource, request.AccessibleName);
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			if (TryHandleOperationCancelled(ex2, "加载排行榜已取消"))
			{
				return;
			}
			Debug.WriteLine($"[LoadToplist] 异常: {ex2}");
			throw;
		}
	}

	private async Task EnrichToplistAsync(string viewSource, string accessibleName)
	{
		CancellationToken viewToken = GetCurrentViewContentToken();
		using (WorkScopes.BeginEnrichment("Toplist", viewSource))
		{
			try
			{
				List<PlaylistInfo> playlists = (await FetchWithRetryUntilCancel((CancellationToken ct) => _apiClient.GetToplistAsync(), "toplist", viewToken, delegate(int attempt, Exception _)
				{
					SafeInvoke(delegate
					{
						UpdateStatusBar($"加载排行榜失败，正在重试（第 {attempt} 次）...");
					});
				}).ConfigureAwait(continueOnCapturedContext: true)) ?? new List<PlaylistInfo>();
				if (viewToken.IsCancellationRequested)
				{
					return;
				}
				await ExecuteOnUiThreadAsync(delegate
				{
					if (!ShouldAbortViewRender(viewToken, "加载排行榜"))
					{
						DisplayPlaylists(playlists, preserveSelection: true, viewSource, accessibleName, 1, showPagination: false, hasNextPage: false, announceHeader: false, suppressFocus: false);
						FocusListAfterEnrich(0);
						if (playlists.Count == 0)
						{
							MessageBox.Show("暂无排行榜数据。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
							UpdateStatusBar("暂无排行榜");
						}
						else
						{
							UpdateStatusBar($"加载完成，共 {playlists.Count} 个排行榜");
						}
					}
				}).ConfigureAwait(continueOnCapturedContext: false);
				await EnsureLibraryStateFreshAsync(LibraryEntityType.Playlists);
			}
			catch (Exception ex)
			{
				Exception ex2 = ex;
				if (TryHandleOperationCancelled(ex2, "加载排行榜已取消"))
				{
					return;
				}
				Debug.WriteLine($"[LoadToplist] 异常: {ex2}");
				await ExecuteOnUiThreadAsync(delegate
				{
					if (!ShouldAbortViewRender(viewToken, "加载排行榜"))
					{
						MessageBox.Show("加载排行榜失败: " + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
						UpdateStatusBar("加载排行榜失败");
					}
				}).ConfigureAwait(continueOnCapturedContext: false);
			}
		}
	}

	private async Task LoadDailyRecommendSongs()
	{
		try
		{
			ViewLoadRequest request = new ViewLoadRequest("daily_recommend_songs", "每日推荐歌曲", "正在加载每日推荐歌曲...");
			if (!(await RunViewLoadAsync(request, delegate
			{
				using (WorkScopes.BeginSkeleton("DailyRecommendSongs", request.ViewSource))
				{
					return Task.FromResult(result: true);
				}
			}, "加载每日推荐歌曲已取消").ConfigureAwait(continueOnCapturedContext: true)).IsCanceled)
			{
				List<SongInfo> cachedSongs = (IsRecommendationCacheFresh(_dailyRecommendSongsFetchedUtc) ? _dailyRecommendSongsCache : null);
				if (cachedSongs != null && cachedSongs.Count > 0)
				{
					DisplaySongs(cachedSongs, showPagination: false, hasNextPage: false, 1, preserveSelection: true, request.ViewSource, request.AccessibleName, skipAvailabilityCheck: true);
					UpdateStatusBar($"每日推荐歌曲（缓存）共 {cachedSongs.Count} 首，正在刷新...");
					FocusListAfterEnrich(0);
				}
				else
				{
					DisplaySongs(new List<SongInfo>(), showPagination: false, hasNextPage: false, 1, preserveSelection: false, request.ViewSource, request.AccessibleName, skipAvailabilityCheck: true);
					UpdateStatusBar(request.LoadingText);
				}
				EnrichDailyRecommendSongsAsync(request.ViewSource, request.AccessibleName);
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			if (TryHandleOperationCancelled(ex2, "加载每日推荐歌曲已取消"))
			{
				return;
			}
			Debug.WriteLine($"[LoadDailyRecommendSongs] 异常: {ex2}");
			throw;
		}
	}

	private async Task EnrichDailyRecommendSongsAsync(string viewSource, string accessibleName)
	{
		CancellationToken viewToken = GetCurrentViewContentToken();
		using (WorkScopes.BeginEnrichment("DailyRecommendSongs", viewSource))
		{
			try
			{
				if (_dailyRecommendSongsCache != null && IsRecommendationCacheFresh(_dailyRecommendSongsFetchedUtc))
				{
					List<SongInfo> cachedSongs = _dailyRecommendSongsCache;
					await ExecuteOnUiThreadAsync(delegate
					{
						if (!ShouldAbortViewRender(viewToken, "每日推荐歌曲"))
						{
							DisplaySongs(cachedSongs, showPagination: false, hasNextPage: false, 1, preserveSelection: true, viewSource, accessibleName, skipAvailabilityCheck: false, announceHeader: false, suppressFocus: false);
							FocusListAfterEnrich(0);
							UpdateStatusBar((cachedSongs.Count == 0) ? "暂无每日推荐歌曲" : $"每日推荐歌曲（缓存）共 {cachedSongs.Count} 首");
						}
					}).ConfigureAwait(continueOnCapturedContext: false);
					return;
				}
				List<SongInfo> songsResult = (await FetchWithRetryUntilCancel((CancellationToken ct) => _apiClient.GetDailyRecommendSongsAsync(), "daily_recommend:songs", viewToken, delegate(int attempt, Exception _)
				{
					SafeInvoke(delegate
					{
						UpdateStatusBar($"加载每日推荐歌曲失败，正在重试（第 {attempt} 次）...");
					});
				}).ConfigureAwait(continueOnCapturedContext: true)) ?? new List<SongInfo>();
				_personalizedNewSongsCache = songsResult;
				_personalizedCacheFetchedUtc = DateTime.UtcNow;
				if (viewToken.IsCancellationRequested)
				{
					return;
				}
				await ExecuteOnUiThreadAsync(delegate
				{
					if (!ShouldAbortViewRender(viewToken, "每日推荐歌曲"))
					{
						DisplaySongs(songsResult, showPagination: false, hasNextPage: false, 1, preserveSelection: true, viewSource, accessibleName, skipAvailabilityCheck: false, announceHeader: false, suppressFocus: false);
						FocusListAfterEnrich(0);
						if (songsResult.Count == 0)
						{
							MessageBox.Show("今日暂无推荐歌曲。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
							UpdateStatusBar("暂无每日推荐歌曲");
						}
						else
						{
							UpdateStatusBar($"加载完成，共 {songsResult.Count} 首歌曲");
						}
					}
				}).ConfigureAwait(continueOnCapturedContext: false);
				await EnsureLibraryStateFreshAsync(LibraryEntityType.Songs);
			}
			catch (Exception ex)
			{
				Exception ex2 = ex;
				if (TryHandleOperationCancelled(ex2, "加载每日推荐歌曲已取消"))
				{
					return;
				}
				Debug.WriteLine($"[LoadDailyRecommendSongs] 异常: {ex2}");
				await ExecuteOnUiThreadAsync(delegate
				{
					if (!ShouldAbortViewRender(viewToken, "每日推荐歌曲"))
					{
						MessageBox.Show("加载每日推荐歌曲失败: " + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
						UpdateStatusBar("加载每日推荐歌曲失败");
					}
				}).ConfigureAwait(continueOnCapturedContext: false);
			}
		}
	}

	private async Task LoadDailyRecommendPlaylists()
	{
		try
		{
			ViewLoadRequest request = new ViewLoadRequest("daily_recommend_playlists", "每日推荐歌单", "正在加载每日推荐歌单...");
			if (!(await RunViewLoadAsync(request, delegate
			{
				using (WorkScopes.BeginSkeleton("DailyRecommendPlaylists", request.ViewSource))
				{
					return Task.FromResult(result: true);
				}
			}, "加载每日推荐歌单已取消").ConfigureAwait(continueOnCapturedContext: true)).IsCanceled)
			{
				List<PlaylistInfo> cachedPlaylists = (IsRecommendationCacheFresh(_dailyRecommendPlaylistsFetchedUtc) ? _dailyRecommendPlaylistsCache : null);
				if (cachedPlaylists != null && cachedPlaylists.Count > 0)
				{
					DisplayPlaylists(cachedPlaylists, preserveSelection: true, request.ViewSource, request.AccessibleName, 1, showPagination: false, hasNextPage: false, announceHeader: false);
					UpdateStatusBar($"每日推荐歌单（缓存）共 {cachedPlaylists.Count} 个，正在刷新...");
					FocusListAfterEnrich(0);
				}
				else
				{
					DisplayPlaylists(new List<PlaylistInfo>(), preserveSelection: false, request.ViewSource, request.AccessibleName, 1, showPagination: false, hasNextPage: false, announceHeader: false);
					UpdateStatusBar(request.LoadingText);
				}
				EnrichDailyRecommendPlaylistsAsync(request.ViewSource, request.AccessibleName);
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			if (TryHandleOperationCancelled(ex2, "加载每日推荐歌单已取消"))
			{
				return;
			}
			Debug.WriteLine($"[LoadDailyRecommendPlaylists] 异常: {ex2}");
			throw;
		}
	}

	private async Task EnrichDailyRecommendPlaylistsAsync(string viewSource, string accessibleName)
	{
		CancellationToken viewToken = GetCurrentViewContentToken();
		using (WorkScopes.BeginEnrichment("DailyRecommendPlaylists", viewSource))
		{
			try
			{
				if (_dailyRecommendPlaylistsCache != null && IsRecommendationCacheFresh(_dailyRecommendPlaylistsFetchedUtc))
				{
					List<PlaylistInfo> cachedPlaylists = _dailyRecommendPlaylistsCache;
					await ExecuteOnUiThreadAsync(delegate
					{
						if (!ShouldAbortViewRender(viewToken, "每日推荐歌单"))
						{
							DisplayPlaylists(cachedPlaylists, preserveSelection: true, viewSource, accessibleName, 1, showPagination: false, hasNextPage: false, announceHeader: false, suppressFocus: false);
							FocusListAfterEnrich(0);
							UpdateStatusBar((cachedPlaylists.Count == 0) ? "暂无每日推荐歌单" : $"每日推荐歌单（缓存）共 {cachedPlaylists.Count} 个");
						}
					}).ConfigureAwait(continueOnCapturedContext: false);
					return;
				}
				List<PlaylistInfo> playlistsResult = (await FetchWithRetryUntilCancel((CancellationToken ct) => _apiClient.GetDailyRecommendPlaylistsAsync(), "daily_recommend:playlists", viewToken, delegate(int attempt, Exception _)
				{
					SafeInvoke(delegate
					{
						UpdateStatusBar($"加载每日推荐歌单失败，正在重试（第 {attempt} 次）...");
					});
				}).ConfigureAwait(continueOnCapturedContext: true)) ?? new List<PlaylistInfo>();
				_dailyRecommendPlaylistsCache = playlistsResult;
				_dailyRecommendPlaylistsFetchedUtc = DateTime.UtcNow;
				if (viewToken.IsCancellationRequested)
				{
					return;
				}
				await ExecuteOnUiThreadAsync(delegate
				{
					if (!ShouldAbortViewRender(viewToken, "每日推荐歌单"))
					{
						DisplayPlaylists(playlistsResult, preserveSelection: true, viewSource, accessibleName, 1, showPagination: false, hasNextPage: false, announceHeader: false, suppressFocus: false);
						FocusListAfterEnrich(0);
						if (playlistsResult.Count == 0)
						{
							MessageBox.Show("今日暂无推荐歌单。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
							UpdateStatusBar("暂无每日推荐歌单");
						}
						else
						{
							UpdateStatusBar($"加载完成，共 {playlistsResult.Count} 个歌单");
						}
					}
				}).ConfigureAwait(continueOnCapturedContext: false);
				await EnsureLibraryStateFreshAsync(LibraryEntityType.Playlists);
			}
			catch (Exception ex)
			{
				Exception ex2 = ex;
				if (TryHandleOperationCancelled(ex2, "加载每日推荐歌单已取消"))
				{
					return;
				}
				Debug.WriteLine($"[LoadDailyRecommendPlaylists] 异常: {ex2}");
				await ExecuteOnUiThreadAsync(delegate
				{
					if (!ShouldAbortViewRender(viewToken, "每日推荐歌单"))
					{
						MessageBox.Show("加载每日推荐歌单失败: " + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
						UpdateStatusBar("加载每日推荐歌单失败");
					}
				}).ConfigureAwait(continueOnCapturedContext: false);
			}
		}
	}

	private async Task LoadPersonalizedPlaylists()
	{
		try
		{
			ViewLoadRequest request = new ViewLoadRequest("personalized_playlists", "推荐歌单", "正在加载推荐歌单...");
			if (!(await RunViewLoadAsync(request, delegate
			{
				using (WorkScopes.BeginSkeleton("PersonalizedPlaylists", request.ViewSource))
				{
					return Task.FromResult(result: true);
				}
			}, "加载推荐歌单已取消").ConfigureAwait(continueOnCapturedContext: true)).IsCanceled)
			{
				List<PlaylistInfo> cachedPlaylists = (IsRecommendationCacheFresh(_personalizedCacheFetchedUtc) ? _personalizedPlaylistsCache : null);
				if (cachedPlaylists != null && cachedPlaylists.Count > 0)
				{
					DisplayPlaylists(cachedPlaylists, preserveSelection: true, request.ViewSource, request.AccessibleName, 1, showPagination: false, hasNextPage: false, announceHeader: false);
					UpdateStatusBar($"推荐歌单（缓存）共 {cachedPlaylists.Count} 个，正在刷新...");
					FocusListAfterEnrich(0);
				}
				else
				{
					DisplayPlaylists(new List<PlaylistInfo>(), preserveSelection: false, request.ViewSource, request.AccessibleName, 1, showPagination: false, hasNextPage: false, announceHeader: false);
					UpdateStatusBar(request.LoadingText);
				}
				EnrichPersonalizedPlaylistsAsync(request.ViewSource, request.AccessibleName);
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			if (TryHandleOperationCancelled(ex2, "加载推荐歌单已取消"))
			{
				return;
			}
			Debug.WriteLine($"[LoadPersonalizedPlaylists] 异常: {ex2}");
			throw;
		}
	}

	private async Task EnrichPersonalizedPlaylistsAsync(string viewSource, string accessibleName)
	{
		CancellationToken viewToken = GetCurrentViewContentToken();
		using (WorkScopes.BeginEnrichment("PersonalizedPlaylists", viewSource))
		{
			try
			{
				if (_personalizedPlaylistsCache != null && IsRecommendationCacheFresh(_personalizedCacheFetchedUtc))
				{
					List<PlaylistInfo> cachedPlaylists = _personalizedPlaylistsCache;
					await ExecuteOnUiThreadAsync(delegate
					{
						if (!ShouldAbortViewRender(viewToken, "推荐歌单"))
						{
							DisplayPlaylists(cachedPlaylists, preserveSelection: true, viewSource, accessibleName, 1, showPagination: false, hasNextPage: false, announceHeader: false, suppressFocus: false);
							FocusListAfterEnrich(0);
							UpdateStatusBar((cachedPlaylists.Count == 0) ? "暂无推荐歌单" : $"推荐歌单（缓存）共 {cachedPlaylists.Count} 个");
						}
					}).ConfigureAwait(continueOnCapturedContext: false);
					return;
				}
				List<PlaylistInfo> playlistsResult = (await FetchWithRetryUntilCancel((CancellationToken ct) => _apiClient.GetPersonalizedPlaylistsAsync(), "personalized:playlists", viewToken, delegate(int attempt, Exception _)
				{
					SafeInvoke(delegate
					{
						UpdateStatusBar($"加载推荐歌单失败，正在重试（第 {attempt} 次）...");
					});
				}).ConfigureAwait(continueOnCapturedContext: true)) ?? new List<PlaylistInfo>();
				_personalizedPlaylistsCache = playlistsResult;
				_personalizedCacheFetchedUtc = DateTime.UtcNow;
				if (viewToken.IsCancellationRequested)
				{
					return;
				}
				await ExecuteOnUiThreadAsync(delegate
				{
					if (!ShouldAbortViewRender(viewToken, "推荐歌单"))
					{
						DisplayPlaylists(playlistsResult, preserveSelection: true, viewSource, accessibleName, 1, showPagination: false, hasNextPage: false, announceHeader: false, suppressFocus: false);
						FocusListAfterEnrich(0);
						if (playlistsResult.Count == 0)
						{
							MessageBox.Show("暂无推荐歌单。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
							UpdateStatusBar("暂无推荐歌单");
						}
						else
						{
							UpdateStatusBar($"加载完成，共 {playlistsResult.Count} 个歌单");
						}
					}
				}).ConfigureAwait(continueOnCapturedContext: false);
				await EnsureLibraryStateFreshAsync(LibraryEntityType.Playlists);
			}
			catch (Exception ex)
			{
				Exception ex2 = ex;
				if (TryHandleOperationCancelled(ex2, "加载推荐歌单已取消"))
				{
					return;
				}
				Debug.WriteLine($"[LoadPersonalizedPlaylists] 异常: {ex2}");
				await ExecuteOnUiThreadAsync(delegate
				{
					if (!ShouldAbortViewRender(viewToken, "推荐歌单"))
					{
						MessageBox.Show("加载推荐歌单失败: " + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
						UpdateStatusBar("加载推荐歌单失败");
					}
				}).ConfigureAwait(continueOnCapturedContext: false);
			}
		}
	}

	private async Task LoadPersonalizedNewSongs()
	{
		try
		{
			ViewLoadRequest request = new ViewLoadRequest("personalized_newsongs", "推荐新歌", "正在加载推荐新歌...");
			if (!(await RunViewLoadAsync(request, delegate
			{
				using (WorkScopes.BeginSkeleton("PersonalizedNewSongs", request.ViewSource))
				{
					return Task.FromResult(result: true);
				}
			}, "加载推荐新歌已取消").ConfigureAwait(continueOnCapturedContext: true)).IsCanceled)
			{
				List<SongInfo> cachedSongs = (IsRecommendationCacheFresh(_personalizedCacheFetchedUtc) ? _personalizedNewSongsCache : null);
				if (cachedSongs != null && cachedSongs.Count > 0)
				{
					DisplaySongs(cachedSongs, showPagination: false, hasNextPage: false, 1, preserveSelection: true, request.ViewSource, request.AccessibleName, skipAvailabilityCheck: false, announceHeader: false);
					UpdateStatusBar($"推荐新歌（缓存）共 {cachedSongs.Count} 首，正在刷新...");
					FocusListAfterEnrich(0);
				}
				else
				{
					DisplaySongs(new List<SongInfo>(), showPagination: false, hasNextPage: false, 1, preserveSelection: false, request.ViewSource, request.AccessibleName, skipAvailabilityCheck: true, announceHeader: false);
					UpdateStatusBar(request.LoadingText);
				}
				EnrichPersonalizedNewSongsAsync(request.ViewSource, request.AccessibleName);
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			if (TryHandleOperationCancelled(ex2, "加载推荐新歌已取消"))
			{
				return;
			}
			Debug.WriteLine($"[LoadPersonalizedNewSongs] 异常: {ex2}");
			throw;
		}
	}

	private async Task EnrichPersonalizedNewSongsAsync(string viewSource, string accessibleName)
	{
		CancellationToken viewToken = GetCurrentViewContentToken();
		using (WorkScopes.BeginEnrichment("PersonalizedNewSongs", viewSource))
		{
			try
			{
				if (_personalizedNewSongsCache != null && IsRecommendationCacheFresh(_personalizedCacheFetchedUtc))
				{
					List<SongInfo> cachedSongs = _personalizedNewSongsCache;
					await ExecuteOnUiThreadAsync(delegate
					{
						if (!ShouldAbortViewRender(viewToken, "推荐新歌"))
						{
							DisplaySongs(cachedSongs, showPagination: false, hasNextPage: false, 1, preserveSelection: true, viewSource, accessibleName, skipAvailabilityCheck: false, announceHeader: false, suppressFocus: false);
							FocusListAfterEnrich(0);
							UpdateStatusBar((cachedSongs.Count == 0) ? "暂无推荐新歌" : $"推荐新歌（缓存）共 {cachedSongs.Count} 首");
						}
					}).ConfigureAwait(continueOnCapturedContext: false);
					return;
				}
				List<SongInfo> songsResult = (await FetchWithRetryUntilCancel((CancellationToken ct) => _apiClient.GetPersonalizedNewSongsAsync(20), "personalized:newsongs", viewToken, delegate(int attempt, Exception _)
				{
					SafeInvoke(delegate
					{
						UpdateStatusBar($"加载推荐新歌失败，正在重试（第 {attempt} 次）...");
					});
				}).ConfigureAwait(continueOnCapturedContext: true)) ?? new List<SongInfo>();
				if (viewToken.IsCancellationRequested)
				{
					return;
				}
				await ExecuteOnUiThreadAsync(delegate
				{
					if (!ShouldAbortViewRender(viewToken, "推荐新歌"))
					{
						DisplaySongs(songsResult, showPagination: false, hasNextPage: false, 1, preserveSelection: true, viewSource, accessibleName, skipAvailabilityCheck: false, announceHeader: false, suppressFocus: false);
						FocusListAfterEnrich(0);
						if (songsResult.Count == 0)
						{
							MessageBox.Show("暂无推荐新歌。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
							UpdateStatusBar("暂无推荐新歌");
						}
						else
						{
							UpdateStatusBar($"加载完成，共 {songsResult.Count} 首歌曲");
						}
					}
				}).ConfigureAwait(continueOnCapturedContext: false);
				await EnsureLibraryStateFreshAsync(LibraryEntityType.Songs);
			}
			catch (Exception ex)
			{
				Exception ex2 = ex;
				if (TryHandleOperationCancelled(ex2, "加载推荐新歌已取消"))
				{
					return;
				}
				Debug.WriteLine($"[LoadPersonalizedNewSongs] 异常: {ex2}");
				await ExecuteOnUiThreadAsync(delegate
				{
					if (!ShouldAbortViewRender(viewToken, "推荐新歌"))
					{
						MessageBox.Show("加载推荐新歌失败: " + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
						UpdateStatusBar("加载推荐新歌失败");
					}
				}).ConfigureAwait(continueOnCapturedContext: false);
			}
		}
	}

	private void DisplaySongs(List<SongInfo> songs, bool showPagination = false, bool hasNextPage = false, int startIndex = 1, bool preserveSelection = false, string? viewSource = null, string? accessibleName = null, bool skipAvailabilityCheck = false, bool announceHeader = true, bool suppressFocus = false, bool allowSelection = true)
	{
		ConfigureListViewDefault();
		int num = -1;
		if (preserveSelection && resultListView.SelectedIndices.Count > 0)
		{
			num = resultListView.SelectedIndices[0];
		}
		bool isPlaylistView = viewSource != null && viewSource.StartsWith("playlist:", StringComparison.OrdinalIgnoreCase);
		_currentPlaylistOwnedByUser = isPlaylistView && _currentPlaylist != null && IsPlaylistOwnedByUser(_currentPlaylist, GetCurrentUserId());
		_currentSongs = songs ?? new List<SongInfo>();
		ApplySongLikeStates(_currentSongs);
		_currentPlaylists.Clear();
		_currentAlbums.Clear();
		_currentArtists.Clear();
		_currentListItems.Clear();
		_currentPodcasts.Clear();
		_currentPodcastSounds.Clear();
		_currentPodcast = null;
		resultListView.BeginUpdate();
		resultListView.Items.Clear();
		if (songs == null || songs.Count == 0)
		{
			resultListView.EndUpdate();
			_currentPlaylistOwnedByUser = viewSource != null && viewSource.StartsWith("playlist:", StringComparison.OrdinalIgnoreCase) && _currentPlaylist != null && IsPlaylistOwnedByUser(_currentPlaylist, GetCurrentUserId());
			SetViewContext(viewSource, accessibleName ?? "歌曲列表");
			if (announceHeader)
			{
				AnnounceListViewHeaderIfNeeded(accessibleName ?? "歌曲列表");
			}
			return;
		}
		int num2 = startIndex;
		int num3 = 0;
		checked
		{
			foreach (SongInfo song in songs)
			{
				string text = (string.IsNullOrWhiteSpace(song.Name) ? "未知" : song.Name);
				if (song.RequiresVip)
				{
					text += "  [VIP]";
				}
				ListViewItem listViewItem = new ListViewItem(new string[5]
				{
					num2.ToString(),
					text,
					string.IsNullOrWhiteSpace(song.Artist) ? string.Empty : song.Artist,
					string.IsNullOrWhiteSpace(song.Album) ? string.Empty : song.Album,
					song.FormattedDuration
				});
				listViewItem.Tag = num3;
				if (song != null && song.IsAvailable == false)
				{
					listViewItem.ForeColor = SystemColors.GrayText;
					string formattedDuration = song.FormattedDuration;
					listViewItem.SubItems[4].Text = (string.IsNullOrWhiteSpace(formattedDuration) ? "不可播放" : (formattedDuration + " (不可播放)"));
					listViewItem.ToolTipText = "歌曲已下架或暂不可播放";
				}
				resultListView.Items.Add(listViewItem);
				num2++;
				num3++;
			}
			bool flag = _currentPage > 1 || startIndex > 1;
			if (showPagination)
			{
				if (flag)
				{
					ListViewItem listViewItem2 = resultListView.Items.Add("上一页");
					listViewItem2.Tag = -2;
				}
				if (hasNextPage)
				{
					ListViewItem listViewItem3 = resultListView.Items.Add("下一页");
					listViewItem3.Tag = -3;
				}
			}
			resultListView.EndUpdate();
			string text2 = accessibleName;
			if (string.IsNullOrWhiteSpace(text2))
			{
				text2 = ((!string.IsNullOrEmpty(viewSource) && viewSource.StartsWith("search:", StringComparison.OrdinalIgnoreCase)) ? "搜索结果" : "歌曲列表");
			}
			SetViewContext(viewSource, text2);
			if (announceHeader)
			{
				AnnounceListViewHeaderIfNeeded(text2);
			}
			if (allowSelection && !suppressFocus && !IsListAutoFocusSuppressed && resultListView.Items.Count > 0)
			{
				int targetIndex = ((num >= 0) ? Math.Min(num, resultListView.Items.Count - 1) : 0);
				targetIndex = ResolvePendingListFocusIndex(targetIndex);
				EnsureListSelectionWithoutFocus(targetIndex);
			}
			if (!skipAvailabilityCheck)
			{
				ScheduleAvailabilityCheck(songs);
			}
		}
	}

	private static void EnsureSubItemCount(ListViewItem item, int desiredCount)
	{
		while (item.SubItems.Count < desiredCount)
		{
			item.SubItems.Add(string.Empty);
		}
	}

	private void PatchSongs(List<SongInfo> songs, int startIndex, bool skipAvailabilityCheck = false, bool showPagination = false, bool hasPreviousPage = false, bool hasNextPage = false, int pendingFocusIndex = -1, bool allowSelection = true)
	{
		int num = ((resultListView.SelectedIndices.Count > 0) ? resultListView.SelectedIndices[0] : pendingFocusIndex);
		_currentSongs = songs ?? new List<SongInfo>();
		ApplySongLikeStates(_currentSongs);
		_currentPlaylists.Clear();
		_currentAlbums.Clear();
		_currentArtists.Clear();
		_currentListItems.Clear();
		_currentPodcasts.Clear();
		_currentPodcastSounds.Clear();
		_currentPodcast = null;
		resultListView.BeginUpdate();
		int count = _currentSongs.Count;
		int count2 = resultListView.Items.Count;
		int num2 = Math.Min(count, count2);
		checked
		{
			for (int i = 0; i < num2; i++)
			{
				SongInfo songInfo = _currentSongs[i];
				ListViewItem listViewItem = resultListView.Items[i];
				EnsureSubItemCount(listViewItem, 5);
				string text = (string.IsNullOrWhiteSpace(songInfo.Name) ? "未知" : songInfo.Name);
				if (songInfo.RequiresVip)
				{
					text += "  [VIP]";
				}
				listViewItem.Text = (startIndex + i).ToString();
				listViewItem.SubItems[1].Text = text;
				listViewItem.SubItems[2].Text = (string.IsNullOrWhiteSpace(songInfo.Artist) ? string.Empty : songInfo.Artist);
				listViewItem.SubItems[3].Text = (string.IsNullOrWhiteSpace(songInfo.Album) ? string.Empty : songInfo.Album);
				listViewItem.SubItems[4].Text = songInfo.FormattedDuration;
				listViewItem.Tag = i;
				if (songInfo != null && songInfo.IsAvailable == false)
				{
					listViewItem.ForeColor = SystemColors.GrayText;
					string formattedDuration = songInfo.FormattedDuration;
					listViewItem.SubItems[4].Text = (string.IsNullOrWhiteSpace(formattedDuration) ? "不可播放" : (formattedDuration + " (不可播放)"));
					listViewItem.ToolTipText = "歌曲已下架或暂不可播放";
				}
				else
				{
					listViewItem.ForeColor = SystemColors.WindowText;
					listViewItem.ToolTipText = null;
				}
			}
			for (int j = count2; j < count; j++)
			{
				SongInfo songInfo2 = _currentSongs[j];
				string text2 = (string.IsNullOrWhiteSpace(songInfo2.Name) ? "未知" : songInfo2.Name);
				if (songInfo2.RequiresVip)
				{
					text2 += "  [VIP]";
				}
				ListViewItem listViewItem2 = new ListViewItem(new string[5]
				{
					(startIndex + j).ToString(),
					text2,
					string.IsNullOrWhiteSpace(songInfo2.Artist) ? string.Empty : songInfo2.Artist,
					string.IsNullOrWhiteSpace(songInfo2.Album) ? string.Empty : songInfo2.Album,
					songInfo2.FormattedDuration
				})
				{
					Tag = j
				};
				if (songInfo2 != null && songInfo2.IsAvailable == false)
				{
					listViewItem2.ForeColor = SystemColors.GrayText;
					string formattedDuration2 = songInfo2.FormattedDuration;
					listViewItem2.SubItems[4].Text = (string.IsNullOrWhiteSpace(formattedDuration2) ? "不可播放" : (formattedDuration2 + " (不可播放)"));
					listViewItem2.ToolTipText = "歌曲已下架或暂不可播放";
				}
				resultListView.Items.Add(listViewItem2);
			}
			for (int num3 = resultListView.Items.Count - 1; num3 >= count; num3--)
			{
				resultListView.Items.RemoveAt(num3);
			}
			if (showPagination)
			{
				if (hasPreviousPage)
				{
					ListViewItem value = new ListViewItem("上一页")
					{
						Tag = -2
					};
					resultListView.Items.Add(value);
				}
				if (hasNextPage)
				{
					ListViewItem value2 = new ListViewItem("下一页")
					{
						Tag = -3
					};
					resultListView.Items.Add(value2);
				}
			}
			resultListView.EndUpdate();
			if (allowSelection && resultListView.Items.Count > 0)
			{
				int fallbackIndex = ((num >= 0) ? Math.Min(num, resultListView.Items.Count - 1) : 0);
				fallbackIndex = ResolvePendingListFocusIndex(fallbackIndex);
				EnsureListSelectionWithoutFocus(fallbackIndex);
			}
			if (!skipAvailabilityCheck)
			{
				ScheduleAvailabilityCheck(_currentSongs);
			}
		}
	}

	private void SetViewContext(string? viewSource, string? accessibleName)
	{
		if (!string.IsNullOrWhiteSpace(viewSource))
		{
			_currentViewSource = viewSource;
			_isHomePage = string.Equals(viewSource, "homepage", StringComparison.OrdinalIgnoreCase);
		}
		else if (string.IsNullOrEmpty(_currentViewSource))
		{
			_isHomePage = false;
		}
		if (string.IsNullOrWhiteSpace(_currentViewSource) || !_currentViewSource.StartsWith("url:mixed", StringComparison.OrdinalIgnoreCase))
		{
			_currentMixedQueryKey = null;
		}
		if (!string.IsNullOrWhiteSpace(accessibleName))
		{
			resultListView.AccessibleName = accessibleName;
		}
		else if (string.IsNullOrWhiteSpace(resultListView.AccessibleName))
		{
			resultListView.AccessibleName = "列表内容";
		}
	}

	private void AnnounceListViewHeaderIfNeeded(string? accessibleName)
	{
		// 仅在列表实际拥有焦点时才朗读，避免应用启动时无意朗读标题
		if (resultListView != null && resultListView.ContainsFocus)
		{
			string value = ((!string.IsNullOrWhiteSpace(accessibleName)) ? accessibleName : ((!string.IsNullOrWhiteSpace(resultListView.AccessibleName)) ? resultListView.AccessibleName : string.Empty));
			if (!string.IsNullOrWhiteSpace(value))
			{
				TtsHelper.SpeakText(value);
			}
		}
	}

	private void ScheduleAvailabilityCheck(List<SongInfo> songs)
	{
		_availabilityCheckCts?.Cancel();
		_availabilityCheckCts?.Dispose();
		_availabilityCheckCts = null;
		if (songs == null || songs.Count == 0)
		{
			return;
		}
		BatchCheckSongsAvailabilityAsync(songs, (_availabilityCheckCts = new CancellationTokenSource()).Token).ContinueWith(delegate(Task task)
		{
			if (task.IsFaulted && task.Exception != null)
			{
				foreach (Exception innerException in task.Exception.Flatten().InnerExceptions)
				{
					Debug.WriteLine("[StreamCheck] 可用性检查任务异常: " + innerException.Message);
				}
			}
		}, TaskScheduler.Default);
	}

	private void DisplayPlaylists(List<PlaylistInfo> playlists, bool preserveSelection = false, string? viewSource = null, string? accessibleName = null, int startIndex = 1, bool showPagination = false, bool hasNextPage = false, bool announceHeader = true, bool suppressFocus = false, bool allowSelection = true)
	{
		ConfigureListViewDefault();
		int num = -1;
		if (preserveSelection && resultListView.SelectedIndices.Count > 0)
		{
			num = resultListView.SelectedIndices[0];
		}
		_currentSongs.Clear();
		_currentPlaylists = playlists ?? new List<PlaylistInfo>();
		_currentPlaylist = null;
		_currentPlaylistOwnedByUser = false;
		_currentAlbums.Clear();
		_currentArtists.Clear();
		_currentListItems.Clear();
		_currentPodcasts.Clear();
		_currentPodcastSounds.Clear();
		ApplyPlaylistSubscriptionState(_currentPlaylists);
		resultListView.BeginUpdate();
		resultListView.Items.Clear();
		if (playlists == null || playlists.Count == 0)
		{
			resultListView.EndUpdate();
			SetViewContext(viewSource, accessibleName ?? "歌单列表");
			if (announceHeader)
			{
				AnnounceListViewHeaderIfNeeded(accessibleName ?? "歌单列表");
			}
			return;
		}
		int num2 = Math.Max(1, startIndex);
		checked
		{
			foreach (PlaylistInfo playlist in playlists)
			{
				string text = (string.IsNullOrWhiteSpace(playlist.Creator) ? string.Empty : playlist.Creator);
				ListViewItem listViewItem = new ListViewItem(new string[5]
				{
					num2.ToString(),
					playlist.Name ?? "未知",
					text,
					(playlist.TrackCount > 0) ? $"{playlist.TrackCount} 首" : string.Empty,
					playlist.Description ?? string.Empty
				});
				listViewItem.Tag = playlist;
				resultListView.Items.Add(listViewItem);
				num2++;
			}
			if (showPagination)
			{
				if (startIndex > 1)
				{
					ListViewItem listViewItem2 = resultListView.Items.Add("上一页");
					listViewItem2.Tag = -2;
				}
				if (hasNextPage)
				{
					ListViewItem listViewItem3 = resultListView.Items.Add("下一页");
					listViewItem3.Tag = -3;
				}
			}
			resultListView.EndUpdate();
			string text2 = accessibleName;
			if (string.IsNullOrWhiteSpace(text2))
			{
				text2 = "歌单列表";
			}
			SetViewContext(viewSource, text2);
			if (announceHeader)
			{
				AnnounceListViewHeaderIfNeeded(text2);
			}
			if (allowSelection && !suppressFocus && !IsListAutoFocusSuppressed && resultListView.Items.Count > 0)
			{
				int targetIndex = ((num >= 0) ? Math.Min(num, resultListView.Items.Count - 1) : 0);
				targetIndex = ResolvePendingListFocusIndex(targetIndex);
				EnsureListSelectionWithoutFocus(targetIndex);
			}
		}
	}

	private void PatchPlaylists(List<PlaylistInfo> playlists, int startIndex, bool showPagination = false, bool hasPreviousPage = false, bool hasNextPage = false, int pendingFocusIndex = -1, bool allowSelection = true)
	{
		int num = ((resultListView.SelectedIndices.Count > 0) ? resultListView.SelectedIndices[0] : pendingFocusIndex);
		_currentSongs.Clear();
		_currentPlaylists = playlists ?? new List<PlaylistInfo>();
		_currentAlbums.Clear();
		_currentArtists.Clear();
		_currentListItems.Clear();
		_currentPodcasts.Clear();
		_currentPodcastSounds.Clear();
		ApplyPlaylistSubscriptionState(_currentPlaylists);
		resultListView.BeginUpdate();
		int count = _currentPlaylists.Count;
		int count2 = resultListView.Items.Count;
		int num2 = Math.Min(count, count2);
		checked
		{
			for (int i = 0; i < num2; i++)
			{
				PlaylistInfo playlistInfo = _currentPlaylists[i];
				ListViewItem listViewItem = resultListView.Items[i];
				EnsureSubItemCount(listViewItem, 5);
				listViewItem.Text = (startIndex + i).ToString();
				listViewItem.SubItems[1].Text = playlistInfo.Name ?? "未知";
				listViewItem.SubItems[2].Text = (string.IsNullOrWhiteSpace(playlistInfo.Creator) ? string.Empty : playlistInfo.Creator);
				listViewItem.SubItems[3].Text = ((playlistInfo.TrackCount > 0) ? $"{playlistInfo.TrackCount} 首" : string.Empty);
				listViewItem.SubItems[4].Text = playlistInfo.Description ?? string.Empty;
				listViewItem.Tag = playlistInfo;
				listViewItem.ForeColor = SystemColors.WindowText;
				listViewItem.ToolTipText = null;
			}
			for (int j = count2; j < count; j++)
			{
				PlaylistInfo playlistInfo2 = _currentPlaylists[j];
				ListViewItem value = new ListViewItem(new string[5]
				{
					(startIndex + j).ToString(),
					playlistInfo2.Name ?? "未知",
					string.IsNullOrWhiteSpace(playlistInfo2.Creator) ? string.Empty : playlistInfo2.Creator,
					(playlistInfo2.TrackCount > 0) ? $"{playlistInfo2.TrackCount} 首" : string.Empty,
					playlistInfo2.Description ?? string.Empty
				})
				{
					Tag = playlistInfo2
				};
				resultListView.Items.Add(value);
			}
			for (int num3 = resultListView.Items.Count - 1; num3 >= count; num3--)
			{
				resultListView.Items.RemoveAt(num3);
			}
			if (showPagination)
			{
				if (hasPreviousPage)
				{
					ListViewItem value2 = new ListViewItem("上一页")
					{
						Tag = -2
					};
					resultListView.Items.Add(value2);
				}
				if (hasNextPage)
				{
					ListViewItem value3 = new ListViewItem("下一页")
					{
						Tag = -3
					};
					resultListView.Items.Add(value3);
				}
			}
			resultListView.EndUpdate();
			if (allowSelection && resultListView.Items.Count > 0)
			{
				int fallbackIndex = ((num >= 0) ? Math.Min(num, resultListView.Items.Count - 1) : 0);
				fallbackIndex = ResolvePendingListFocusIndex(fallbackIndex);
				EnsureListSelectionWithoutFocus(fallbackIndex);
			}
		}
	}

	private void DisplayListItems(List<ListItemInfo> items, string? viewSource = null, string? accessibleName = null, bool preserveSelection = false, bool announceHeader = true, bool suppressFocus = false, bool allowSelection = true)
	{
		ConfigureListViewDefault();
		int num = -1;
		if (preserveSelection && resultListView.SelectedIndices.Count > 0)
		{
			num = resultListView.SelectedIndices[0];
		}
		_currentSongs.Clear();
		_currentPlaylists.Clear();
		_currentAlbums.Clear();
		_currentArtists.Clear();
		_currentListItems = items ?? new List<ListItemInfo>();
		_currentPodcasts.Clear();
		_currentPodcastSounds.Clear();
		_currentPodcast = null;
		ApplyListItemLibraryStates(_currentListItems);
		_homeItemIndexMap.Clear();
		checked
		{
			if (string.Equals(viewSource, "homepage", StringComparison.OrdinalIgnoreCase))
			{
				Debug.WriteLine($"[HomeIndex] init 构建主页索引，项数={_currentListItems.Count}");
				for (int i = 0; i < _currentListItems.Count; i++)
				{
					string text = _currentListItems[i].CategoryId ?? _currentListItems[i].Id ?? _currentListItems[i].Name;
					if (!string.IsNullOrWhiteSpace(text) && !_homeItemIndexMap.ContainsKey(text))
					{
						_homeItemIndexMap[text] = i;
						Debug.WriteLine($"[HomeIndex] init {text} -> {i}");
					}
				}
			}
			resultListView.BeginUpdate();
			resultListView.Items.Clear();
			if (items == null || items.Count == 0)
			{
				resultListView.EndUpdate();
				SetViewContext(viewSource, accessibleName ?? "分类列表");
				if (announceHeader)
				{
					AnnounceListViewHeaderIfNeeded(accessibleName ?? "分类列表");
				}
				return;
			}
			int num2 = 1;
			foreach (ListItemInfo item in items)
			{
				string text2 = item.Name ?? "未知";
				string text3 = item.Creator ?? "";
				string text4 = item.ExtraInfo ?? "";
				string text5 = item.Description ?? string.Empty;
				if (item.Type == ListItemType.Song)
				{
					SongInfo? song = item.Song;
					if (song != null && song.RequiresVip)
					{
						text2 += "  [VIP]";
					}
				}
				switch (item.Type)
				{
				case ListItemType.Playlist:
					text5 = item.Playlist?.Description ?? "";
					break;
				case ListItemType.Album:
					(text3, text4, text5) = BuildAlbumDisplayLabels(item.Album);
					break;
				case ListItemType.Song:
					text5 = ((!string.IsNullOrWhiteSpace(text5)) ? text5 : (item.Song?.FormattedDuration ?? ""));
					break;
				case ListItemType.Artist:
					if (string.IsNullOrWhiteSpace(text5) && item.Artist != null)
					{
						text5 = item.Artist.Description ?? item.Artist.BriefDesc;
					}
					break;
				case ListItemType.Podcast:
				{
					text3 = item.Podcast?.DjName ?? text3;
					PodcastRadioInfo? podcast = item.Podcast;
					text4 = ((podcast != null && podcast.ProgramCount > 0) ? $"{item.Podcast.ProgramCount} 个节目" : text4);
					text5 = ((!string.IsNullOrWhiteSpace(text5)) ? text5 : (item.Podcast?.Description ?? string.Empty));
					break;
				}
				case ListItemType.PodcastEpisode:
				{
					text3 = ((!string.IsNullOrWhiteSpace(text3)) ? text3 : ((!string.IsNullOrWhiteSpace(item.PodcastEpisode?.DjName)) ? (item.PodcastEpisode.RadioName + " / " + item.PodcastEpisode.DjName) : (item.PodcastEpisode?.RadioName ?? string.Empty)));
					PodcastEpisodeInfo? podcastEpisode = item.PodcastEpisode;
					if (podcastEpisode != null && podcastEpisode.PublishTime.HasValue)
					{
						text4 = item.PodcastEpisode.PublishTime.Value.ToString("yyyy-MM-dd");
					}
					if (string.IsNullOrWhiteSpace(text5))
					{
						text5 = item.PodcastEpisode?.Description ?? string.Empty;
					}
					break;
				}
				}
				ListViewItem listViewItem = new ListViewItem(new string[5] { "", text2, text3, text4, text5 });
				listViewItem.Tag = item;
				resultListView.Items.Add(listViewItem);
				num2++;
			}
			resultListView.EndUpdate();
			string text6 = accessibleName;
			if (string.IsNullOrWhiteSpace(text6))
			{
				text6 = "分类列表";
			}
			SetViewContext(viewSource, text6);
			if (announceHeader)
			{
				AnnounceListViewHeaderIfNeeded(text6);
			}
			if (allowSelection && !suppressFocus && !IsListAutoFocusSuppressed && resultListView.Items.Count > 0)
			{
				int targetIndex = ((num >= 0) ? Math.Min(num, resultListView.Items.Count - 1) : 0);
				targetIndex = ResolvePendingListFocusIndex(targetIndex);
				EnsureListSelectionWithoutFocus(targetIndex);
			}
		}
	}

	private void PatchListItems(List<ListItemInfo> items, bool showPagination = false, bool hasPreviousPage = false, bool hasNextPage = false, int pendingFocusIndex = -1, bool incremental = false, bool preserveDisplayIndex = false)
	{
		int num = ((resultListView.SelectedIndices.Count > 0) ? resultListView.SelectedIndices[0] : pendingFocusIndex);
		checked
		{
			if (incremental && items != null && resultListView.Items.Count == _currentListItems.Count && _currentListItems.Count == items.Count)
			{
				resultListView.BeginUpdate();
				try
				{
					for (int i = 0; i < items.Count; i++)
					{
						ListItemInfo listItemInfo = items[i];
						ListItemInfo a = _currentListItems[i];
						if (IsListItemDifferent(a, listItemInfo))
						{
							_currentListItems[i] = listItemInfo;
							ListViewItem item = resultListView.Items[i];
							FillListViewItemFromListItemInfo(item, listItemInfo, i + 1, preserveDisplayIndex);
							if (!string.IsNullOrWhiteSpace(listItemInfo.CategoryId))
							{
								_homeItemIndexMap[listItemInfo.CategoryId] = i;
							}
						}
					}
					return;
				}
				finally
				{
					resultListView.EndUpdate();
				}
			}
			_currentSongs.Clear();
			_currentPlaylists.Clear();
			_currentAlbums.Clear();
			_currentArtists.Clear();
			_currentListItems = items ?? new List<ListItemInfo>();
			_currentPodcasts.Clear();
			_currentPodcastSounds.Clear();
			_currentPodcast = null;
			ApplyListItemLibraryStates(_currentListItems);
			_homeItemIndexMap.Clear();
			if (string.Equals(_currentViewSource, "homepage", StringComparison.OrdinalIgnoreCase))
			{
				Debug.WriteLine($"[HomeIndex] init 构建主页索引，项数={_currentListItems.Count}");
				for (int j = 0; j < _currentListItems.Count; j++)
				{
					string text = _currentListItems[j].CategoryId ?? _currentListItems[j].Id ?? _currentListItems[j].Name;
					if (!string.IsNullOrWhiteSpace(text) && !_homeItemIndexMap.ContainsKey(text))
					{
						_homeItemIndexMap[text] = j;
						Debug.WriteLine($"[HomeIndex] init {text} -> {j}");
					}
				}
			}
			resultListView.BeginUpdate();
			int count = _currentListItems.Count;
			int count2 = resultListView.Items.Count;
			int num2 = Math.Min(count, count2);
			for (int k = 0; k < num2; k++)
			{
				ListItemInfo listItem = _currentListItems[k];
				ListViewItem item2 = resultListView.Items[k];
				EnsureSubItemCount(item2, 5);
				FillListViewItemFromListItemInfo(item2, listItem, k + 1, preserveDisplayIndex);
			}
			for (int l = count2; l < count; l++)
			{
				ListItemInfo listItem2 = _currentListItems[l];
				ListViewItem listViewItem = new ListViewItem(new string[5]
				{
					string.Empty,
					string.Empty,
					string.Empty,
					string.Empty,
					string.Empty
				});
				FillListViewItemFromListItemInfo(listViewItem, listItem2, l + 1, preserveDisplayIndex);
				resultListView.Items.Add(listViewItem);
			}
			for (int num3 = resultListView.Items.Count - 1; num3 >= count; num3--)
			{
				resultListView.Items.RemoveAt(num3);
			}
			if (showPagination)
			{
				if (hasPreviousPage)
				{
					ListViewItem value = new ListViewItem("上一页")
					{
						Tag = -2
					};
					resultListView.Items.Add(value);
				}
				if (hasNextPage)
				{
					ListViewItem value2 = new ListViewItem("下一页")
					{
						Tag = -3
					};
					resultListView.Items.Add(value2);
				}
			}
			resultListView.EndUpdate();
			if (resultListView.Items.Count > 0)
			{
				int fallbackIndex = ((num >= 0) ? Math.Min(num, resultListView.Items.Count - 1) : 0);
				fallbackIndex = ResolvePendingListFocusIndex(fallbackIndex);
				EnsureListSelectionWithoutFocus(fallbackIndex);
			}
		}
	}

	private static bool IsListItemDifferent(ListItemInfo? a, ListItemInfo? b)
	{
		if (a == null || b == null)
		{
			return true;
		}
		if (diff(a.Id, b.Id))
		{
			return true;
		}
		if (diff(a.Name, b.Name))
		{
			return true;
		}
		if (diff(a.Creator, b.Creator))
		{
			return true;
		}
		if (diff(a.ExtraInfo, b.ExtraInfo))
		{
			return true;
		}
		if (diff(a.Description, b.Description))
		{
			return true;
		}
		return false;
		static bool diff(string x, string y)
		{
			return !string.Equals(x ?? string.Empty, y ?? string.Empty, StringComparison.Ordinal);
		}
	}

	private string BuildCloudSummaryDescription(long used, long max)
	{
		if (used <= 0 || max <= 0)
		{
			return "上传和管理您的私人音乐";
		}
		return "已用 " + FormatSize(used) + " / " + FormatSize(max);
	}

	private async Task RunHomeProvidersAsync(bool isInitialLoad, CancellationToken token)
	{
		if (!string.Equals(_currentViewSource, "homepage", StringComparison.OrdinalIgnoreCase))
		{
			Debug.WriteLine("[HomeProviders] 当前视图=" + _currentViewSource + "，跳过丰富");
			return;
		}
		List<Task> tasks = new List<Task>();
		SemaphoreSlim semaphore = new SemaphoreSlim(6);
		bool isLoggedIn = _accountState?.IsLoggedIn ?? false;
		tasks.Add(Wrap("user_playlists", async delegate
		{
			if (!isLoggedIn)
			{
				return true;
			}
			Debug.WriteLine("[HomeProviders] fetch user playlists");
			UserAccountInfo userInfo = await _apiClient.GetUserAccountAsync();
			token.ThrowIfCancellationRequested();
			if (userInfo == null || userInfo.UserId <= 0)
			{
				return false;
			}
			_loggedInUserId = userInfo.UserId;
			List<PlaylistInfo> playlists;
			int totalCount;
			(playlists, totalCount) = await _apiClient.GetUserPlaylistsAsync(userInfo.UserId);
			token.ThrowIfCancellationRequested();
			PlaylistInfo liked = playlists?.FirstOrDefault((PlaylistInfo p) => !string.IsNullOrEmpty(p.Name) && p.Name.IndexOf("喜欢的音乐", StringComparison.OrdinalIgnoreCase) >= 0);
			int playlistCount = totalCount;
			if (liked != null && playlistCount > 0)
			{
				playlistCount = Math.Max(0, checked(playlistCount - 1));
			}
			ApplyHomeItemUpdate("user_playlists", delegate(ListItemInfo info)
			{
				info.ItemCount = playlistCount;
				info.ItemUnit = ((playlistCount > 0) ? "个" : null);
				info.CategoryDescription = null;
			});
			if (liked != null)
			{
				ApplyHomeItemUpdate("user_liked_songs", delegate(ListItemInfo info)
				{
					info.ItemCount = liked.TrackCount;
					info.ItemUnit = ((liked.TrackCount > 0) ? "首" : null);
					info.CategoryDescription = null;
				});
			}
			return true;
		}));
		tasks.Add(Wrap("user_albums", async delegate
		{
			if (!isLoggedIn)
			{
				return true;
			}
			Debug.WriteLine("[HomeProviders] fetch user albums");
			int albumCount = (await _apiClient.GetUserAlbumsAsync(1)).Item2;
			token.ThrowIfCancellationRequested();
			ApplyHomeItemUpdate("user_albums", delegate(ListItemInfo info)
			{
				info.ItemCount = albumCount;
				info.ItemUnit = ((albumCount > 0) ? "张" : null);
				info.CategoryDescription = null;
			});
			return true;
		}));
		tasks.Add(Wrap("artist_favorites", async delegate
		{
			if (!isLoggedIn)
			{
				return true;
			}
			Debug.WriteLine("[HomeProviders] fetch artist subs");
			SearchResult<ArtistInfo> artists = await _apiClient.GetArtistSubscriptionsAsync(1);
			token.ThrowIfCancellationRequested();
			int count = artists?.TotalCount ?? artists?.Items.Count ?? 0;
			ApplyHomeItemUpdate("artist_favorites", delegate(ListItemInfo info)
			{
				info.ItemCount = count;
				info.ItemUnit = ((count > 0) ? "位" : null);
				info.CategoryDescription = null;
			});
			return true;
		}));
		tasks.Add(Wrap("user_podcasts", async delegate
		{
			if (!isLoggedIn)
			{
				return true;
			}
			Debug.WriteLine("[HomeProviders] fetch podcast subs");
			int podcastCount = (await _apiClient.GetSubscribedPodcastsAsync(1)).Item2;
			token.ThrowIfCancellationRequested();
			ApplyHomeItemUpdate("user_podcasts", delegate(ListItemInfo info)
			{
				info.ItemCount = podcastCount;
				info.ItemUnit = ((podcastCount > 0) ? "个" : null);
				info.CategoryDescription = null;
			});
			return true;
		}));
		tasks.Add(Wrap("user_cloud", async delegate
		{
			if (!isLoggedIn)
			{
				return true;
			}
			Debug.WriteLine("[HomeProviders] fetch cloud summary");
			try
			{
				CloudSongPageResult cloudSummary = await _apiClient.GetCloudSongsAsync(1, 0, token);
				token.ThrowIfCancellationRequested();
				_cloudTotalCount = cloudSummary?.TotalCount ?? 0;
				_cloudUsedSize = cloudSummary?.UsedSize ?? 0;
				_cloudMaxSize = cloudSummary?.MaxSize ?? 0;
			}
			catch (Exception ex)
			{
				Debug.WriteLine("[HomeProvider] cloud query failed: " + ex.Message);
			}
			ApplyHomeItemUpdate("user_cloud", delegate(ListItemInfo info)
			{
				info.ItemCount = _cloudTotalCount;
				info.ItemUnit = ((_cloudTotalCount > 0) ? "首" : null);
				info.CategoryDescription = BuildCloudSummaryDescription(_cloudUsedSize, _cloudMaxSize);
			});
			return true;
		}));
		tasks.Add(Wrap("recent_listened", async delegate
		{
			Debug.WriteLine("[HomeProviders] fetch recent summary");
			await RefreshRecentSummariesAsync(isLoggedIn, token).ConfigureAwait(continueOnCapturedContext: false);
			token.ThrowIfCancellationRequested();
			ApplyHomeItemUpdate("recent_listened", delegate(ListItemInfo info)
			{
				info.CategoryDescription = BuildRecentListenedDescription();
			});
			return true;
		}));
		tasks.Add(Wrap("toplist", async delegate
		{
			Debug.WriteLine("[HomeProviders] fetch toplist");
			List<PlaylistInfo> toplist = await _apiClient.GetToplistAsync();
			token.ThrowIfCancellationRequested();
			int count = toplist?.Count ?? 0;
			ApplyHomeItemUpdate("toplist", delegate(ListItemInfo info)
			{
				info.ItemCount = count;
				info.ItemUnit = ((count > 0) ? "个" : null);
				info.CategoryDescription = null;
			});
			return true;
		}));
		tasks.Add(Wrap("new_albums", async delegate
		{
			Debug.WriteLine("[HomeProviders] fetch new albums");
			List<AlbumInfo> newAlbums = await _apiClient.GetNewAlbumsAsync();
			token.ThrowIfCancellationRequested();
			int count = newAlbums?.Count ?? 0;
			ApplyHomeItemUpdate("new_albums", delegate(ListItemInfo info)
			{
				info.ItemCount = count;
				info.ItemUnit = ((count > 0) ? "张" : null);
				info.CategoryDescription = null;
			});
			return true;
		}));
		tasks.Add(Wrap("daily_recommend", async delegate
		{
			if (!isLoggedIn)
			{
				return true;
			}
			Debug.WriteLine("[HomeProviders] fetch daily recommend");
			(List<SongInfo> Songs, List<PlaylistInfo> Playlists) daily = await FetchDailyRecommendBundleAsync(token);
			token.ThrowIfCancellationRequested();
			int songCount = daily.Songs?.Count ?? 0;
			int playlistCount = daily.Playlists?.Count ?? 0;
			ApplyHomeItemUpdate("daily_recommend", delegate(ListItemInfo info)
			{
				info.CategoryDescription = $"{songCount} 首单曲 | {playlistCount} 个歌单";
			});
			return true;
		}));
		tasks.Add(Wrap("personalized", async delegate
		{
			if (!isLoggedIn)
			{
				return true;
			}
			Debug.WriteLine("[HomeProviders] fetch personalized");
			(List<PlaylistInfo> Playlists, List<SongInfo> Songs) personalized = await FetchPersonalizedBundleAsync(token);
			token.ThrowIfCancellationRequested();
			int playlistCount = personalized.Playlists?.Count ?? 0;
			int songCount = personalized.Songs?.Count ?? 0;
			ApplyHomeItemUpdate("personalized", delegate(ListItemInfo info)
			{
				info.CategoryDescription = $"{songCount} 首单曲 | {playlistCount} 个歌单 ";
			});
			return true;
		}));
		await Task.WhenAll(tasks).ConfigureAwait(continueOnCapturedContext: false);
		if (token.IsCancellationRequested)
		{
			return;
		}
		await ExecuteOnUiThreadAsync(delegate
		{
			if (string.Equals(_currentViewSource, "homepage", StringComparison.OrdinalIgnoreCase))
			{
				_isHomePage = true;
				UpdateStatusBar(isInitialLoad ? "主页加载完成，数据持续同步中" : "主页已更新");
				if (isInitialLoad && !_initialHomeLoadCompleted)
				{
					_initialHomeLoadCompleted = true;
				}
			}
		}).ConfigureAwait(continueOnCapturedContext: false);
		async Task ExecuteWithRetryAsync(string name, Func<Task<bool>> work, int maxRetry = 10)
		{
			checked
			{
				for (int attempt = 1; attempt <= maxRetry; attempt++)
				{
					token.ThrowIfCancellationRequested();
					try
					{
						if (await work().ConfigureAwait(continueOnCapturedContext: false))
						{
							return;
						}
						Debug.WriteLine($"[HomeRetry] {name} attempt {attempt} 未返回有效数据");
					}
					catch (Exception ex)
					{
						Debug.WriteLine($"[HomeRetry] {name} attempt {attempt} 异常: {ex.Message}");
					}
					if (attempt < maxRetry)
					{
						await Task.Delay(TimeSpan.FromMilliseconds(200 * Math.Min(attempt, 10)), token).ConfigureAwait(continueOnCapturedContext: false);
					}
				}
				Debug.WriteLine("[HomeRetry] " + name + " 达到最大重试次数，放弃更新");
			}
		}
		async Task Wrap(string name, Func<Task<bool>> work)
		{
			await semaphore.WaitAsync(token).ConfigureAwait(continueOnCapturedContext: false);
			try
			{
				await ExecuteWithRetryAsync(name, work).ConfigureAwait(continueOnCapturedContext: false);
			}
			finally
			{
				semaphore.Release();
			}
		}
	}

	private void ApplyHomeItemUpdate(string categoryId, Action<ListItemInfo> updater)
	{
		int index;
		if (string.IsNullOrWhiteSpace(categoryId))
		{
			Debug.WriteLine("[HomeUpdate] categoryId为空，跳过");
		}
		else if (!string.Equals(_currentViewSource, "homepage", StringComparison.OrdinalIgnoreCase))
		{
			Debug.WriteLine("[HomeUpdate] 当前视图非主页(" + _currentViewSource + "), 跳过 " + categoryId);
		}
		else if (!_homeItemIndexMap.TryGetValue(categoryId, out index))
		{
			Debug.WriteLine($"[HomeUpdate] 找不到索引: {categoryId}（map条目={_homeItemIndexMap.Count}）");
		}
		else
		{
			if (index < 0 || index >= _currentListItems.Count || index >= resultListView.Items.Count)
			{
				return;
			}
			ListItemInfo listItemInfo = _currentListItems[index];
			ListItemInfo clone = new ListItemInfo
			{
				Type = listItemInfo.Type,
				Song = listItemInfo.Song,
				Playlist = listItemInfo.Playlist,
				Album = listItemInfo.Album,
				Artist = listItemInfo.Artist,
				Podcast = listItemInfo.Podcast,
				PodcastEpisode = listItemInfo.PodcastEpisode,
				CategoryId = listItemInfo.CategoryId,
				CategoryName = listItemInfo.CategoryName,
				CategoryDescription = listItemInfo.CategoryDescription,
				ItemCount = listItemInfo.ItemCount,
				ItemUnit = listItemInfo.ItemUnit
			};
			updater(clone);
			_currentListItems[index] = clone;
			SafeInvoke(delegate
			{
				Debug.WriteLine($"[HomeUpdate] {categoryId} -> row {index}: count={clone.ItemCount}, desc={clone.CategoryDescription}");
				if (index >= 0 && index < resultListView.Items.Count)
				{
					ListViewItem item = resultListView.Items[index];
					FillListViewItemFromListItemInfo(item, clone, checked(index + 1), preserveDisplayIndex: true);
				}
			});
		}
	}

	private void FillListViewItemFromListItemInfo(ListViewItem item, ListItemInfo listItem, int displayIndex, bool preserveDisplayIndex = false)
	{
		string text = listItem.Name ?? "未知";
		string text2 = listItem.Creator ?? "";
		string text3 = listItem.ExtraInfo ?? "";
		string text4 = listItem.Description ?? string.Empty;
		if (listItem.Type == ListItemType.Song)
		{
			SongInfo? song = listItem.Song;
			if (song != null && song.RequiresVip)
			{
				text += "  [VIP]";
			}
		}
		bool flag = string.Equals(_currentViewSource, "homepage", StringComparison.OrdinalIgnoreCase);
		if (!flag && (!preserveDisplayIndex || string.IsNullOrWhiteSpace(item.Text)))
		{
			item.Text = displayIndex.ToString();
		}
		else if (flag)
		{
			item.Text = string.Empty;
		}
		item.SubItems[1].Text = text;
		item.SubItems[2].Text = text2;
		item.SubItems[3].Text = text3;
		item.SubItems[4].Text = text4;
		item.Tag = listItem;
	}

	private void ScheduleLibraryStateRefresh(bool includeLikedSongs = true, bool includePlaylists = true, bool includeAlbums = true, bool includePodcasts = true, bool includeArtists = true)
	{
		if (!IsUserLoggedIn() || _apiClient == null)
		{
			return;
		}
		List<LibraryEntityType> list = new List<LibraryEntityType>();
		if (includeLikedSongs)
		{
			list.Add(LibraryEntityType.Songs);
		}
		if (includePlaylists)
		{
			list.Add(LibraryEntityType.Playlists);
		}
		if (includeAlbums)
		{
			list.Add(LibraryEntityType.Albums);
		}
		if (includePodcasts)
		{
			list.Add(LibraryEntityType.Podcasts);
		}
		if (includeArtists)
		{
			list.Add(LibraryEntityType.Artists);
		}
		foreach (LibraryEntityType item in list)
		{
			RequestLibraryRefresh(item);
		}
	}

	private void RequestLibraryRefresh(LibraryEntityType entity, bool forceRefresh = false)
	{
		if (IsUserLoggedIn() && _apiClient != null)
		{
			Task.Run(() => RefreshLibraryStateAsync(entity, forceRefresh, CancellationToken.None));
		}
	}

	private Task EnsureLibraryStateFreshAsync(LibraryEntityType entity, bool forceRefresh = false, CancellationToken cancellationToken = default(CancellationToken))
	{
		RequestLibraryRefresh(entity, forceRefresh);
		return Task.CompletedTask;
	}

	private async Task RefreshLibraryStateAsync(LibraryEntityType entity, bool forceRefresh, CancellationToken cancellationToken)
	{
		List<LibraryEntityType> targets = ExpandLibraryEntities(entity).ToList();
		if (targets.Count == 0)
		{
			return;
		}
		double allocation = DownloadBandwidthCoordinator.Instance.GetDownloadBandwidthAllocation();
		if (allocation >= 0.6 && targets.Count > 1)
		{
			IEnumerable<Task> tasks = targets.Select((LibraryEntityType t) => RefreshLibraryEntityAsync(t, forceRefresh, cancellationToken));
			await Task.WhenAll(tasks);
			return;
		}
		foreach (LibraryEntityType target in targets)
		{
			await RefreshLibraryEntityAsync(target, forceRefresh, cancellationToken);
		}
	}

	private IEnumerable<LibraryEntityType> ExpandLibraryEntities(LibraryEntityType entity)
	{
		if (entity == LibraryEntityType.All)
		{
			yield return LibraryEntityType.Songs;
			yield return LibraryEntityType.Playlists;
			yield return LibraryEntityType.Albums;
			yield return LibraryEntityType.Artists;
			yield return LibraryEntityType.Podcasts;
		}
		else
		{
			yield return entity;
		}
	}

	private async Task RefreshLibraryEntityAsync(LibraryEntityType entity, bool forceRefresh, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (!forceRefresh && IsLibraryCacheFresh(entity))
		{
			NotifyLibraryStateUpdated(entity);
			return;
		}
		switch (entity)
		{
		case LibraryEntityType.Songs:
			await RefreshLikedSongsCacheAsync(cancellationToken);
			break;
		case LibraryEntityType.Playlists:
			await RefreshPlaylistSubscriptionCacheAsync(cancellationToken);
			break;
		case LibraryEntityType.Albums:
			await RefreshAlbumSubscriptionCacheAsync(cancellationToken);
			break;
		case LibraryEntityType.Artists:
			await RefreshArtistSubscriptionCacheAsync(cancellationToken);
			break;
		case LibraryEntityType.Podcasts:
			await RefreshPodcastSubscriptionCacheAsync(cancellationToken);
			break;
		}
		lock (_libraryStateLock)
		{
			_libraryCacheTimestamps[entity] = DateTime.UtcNow;
		}
		NotifyLibraryStateUpdated(entity);
	}

	private void NotifyLibraryStateUpdated(LibraryEntityType entity)
	{
		SafeInvoke(delegate
		{
			ApplyLibraryStatePatch(entity);
		});
	}

	private void ApplyLibraryStatePatch(LibraryEntityType entity)
	{
		switch (entity)
		{
		case LibraryEntityType.Songs:
			ApplySongLikeStates(_currentSongs);
			break;
		case LibraryEntityType.Playlists:
			ApplyPlaylistSubscriptionState(_currentPlaylists);
			break;
		case LibraryEntityType.Albums:
			ApplyAlbumSubscriptionState(_currentAlbums);
			break;
		case LibraryEntityType.Artists:
			ApplyArtistSubscriptionStates(_currentArtists);
			break;
		case LibraryEntityType.Podcasts:
			ApplyPodcastSubscriptionState(_currentPodcasts);
			break;
		case LibraryEntityType.All:
			ApplySongLikeStates(_currentSongs);
			ApplyPlaylistSubscriptionState(_currentPlaylists);
			ApplyAlbumSubscriptionState(_currentAlbums);
			ApplyArtistSubscriptionStates(_currentArtists);
			ApplyPodcastSubscriptionState(_currentPodcasts);
			break;
		}
		if (_currentListItems != null && _currentListItems.Count > 0)
		{
			ApplyListItemLibraryStates(_currentListItems);
		}
		resultListView?.Invalidate();
	}

	private bool IsLibraryCacheFresh(LibraryEntityType entity)
	{
		lock (_libraryStateLock)
		{
			DateTime value;
			return _libraryCacheTimestamps.TryGetValue(entity, out value) && DateTime.UtcNow - value < LibraryRefreshInterval;
		}
	}

	private void InvalidateLibraryCaches()
	{
		lock (_libraryStateLock)
		{
			_likedSongIds.Clear();
			_subscribedPlaylistIds.Clear();
			_ownedPlaylistIds.Clear();
			_subscribedAlbumIds.Clear();
			_subscribedPodcastIds.Clear();
			_subscribedArtistIds.Clear();
			_likedSongsCacheValid = false;
			foreach (LibraryEntityType item in _libraryCacheTimestamps.Keys.ToList())
			{
				_libraryCacheTimestamps[item] = DateTime.MinValue;
			}
		}
	}

	private async Task RefreshLikedSongsCacheAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		long userId = GetCurrentUserId();
		if (userId <= 0)
		{
			return;
		}
		try
		{
			List<string> ids = await _apiClient.GetUserLikedSongsAsync(userId);
			cancellationToken.ThrowIfCancellationRequested();
			lock (_libraryStateLock)
			{
				_likedSongIds.Clear();
				foreach (string id in ids)
				{
					if (!string.IsNullOrWhiteSpace(id))
					{
						_likedSongIds.Add(id);
					}
				}
				_likedSongsCacheValid = true;
			}
		}
		catch (Exception arg)
		{
			Debug.WriteLine($"[LibraryCache] 刷新喜欢的歌曲失败: {arg}");
		}
	}

	private async Task RefreshPlaylistSubscriptionCacheAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		long userId = GetCurrentUserId();
		if (userId <= 0)
		{
			return;
		}
		try
		{
			int offset = 0;
			List<PlaylistInfo> aggregated = new List<PlaylistInfo>();
			while (true)
			{
				List<PlaylistInfo> playlists;
				int total;
				(playlists, total) = await _apiClient.GetUserPlaylistsAsync(userId, 1000, offset);
				cancellationToken.ThrowIfCancellationRequested();
				if (playlists == null || playlists.Count == 0)
				{
					break;
				}
				aggregated.AddRange(playlists);
				if (playlists.Count < 1000 || aggregated.Count >= total)
				{
					break;
				}
				offset = checked(offset + playlists.Count);
			}
			lock (_libraryStateLock)
			{
				_subscribedPlaylistIds.Clear();
				_ownedPlaylistIds.Clear();
				foreach (PlaylistInfo playlist in aggregated)
				{
					if (playlist != null && !string.IsNullOrWhiteSpace(playlist.Id))
					{
						if (IsPlaylistOwnedByUser(playlist, userId))
						{
							_ownedPlaylistIds.Add(playlist.Id);
						}
						else
						{
							_subscribedPlaylistIds.Add(playlist.Id);
						}
					}
				}
			}
		}
		catch (Exception arg)
		{
			Debug.WriteLine($"[LibraryCache] 刷新歌单收藏状态失败: {arg}");
		}
	}

	private async Task RefreshAlbumSubscriptionCacheAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		if (!IsUserLoggedIn())
		{
			return;
		}
		try
		{
			int offset = 0;
			List<AlbumInfo> aggregated = new List<AlbumInfo>();
			while (true)
			{
				List<AlbumInfo> albums;
				int total;
				(albums, total) = await _apiClient.GetUserAlbumsAsync(100, offset);
				cancellationToken.ThrowIfCancellationRequested();
				if (albums == null || albums.Count == 0)
				{
					break;
				}
				aggregated.AddRange(albums);
				if (albums.Count < 100 || aggregated.Count >= total)
				{
					break;
				}
				offset = checked(offset + albums.Count);
			}
			lock (_libraryStateLock)
			{
				_subscribedAlbumIds.Clear();
				foreach (AlbumInfo album in aggregated)
				{
					if (!string.IsNullOrWhiteSpace(album?.Id))
					{
						_subscribedAlbumIds.Add(album.Id);
					}
				}
			}
		}
		catch (Exception arg)
		{
			Debug.WriteLine($"[LibraryCache] 刷新收藏专辑失败: {arg}");
		}
	}

	private async Task RefreshPodcastSubscriptionCacheAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		if (!IsUserLoggedIn())
		{
			return;
		}
		try
		{
			int offset = 0;
			List<PodcastRadioInfo> aggregated = new List<PodcastRadioInfo>();
			while (true)
			{
				List<PodcastRadioInfo> podcasts;
				int total;
				(podcasts, total) = await _apiClient.GetSubscribedPodcastsAsync(300, offset);
				cancellationToken.ThrowIfCancellationRequested();
				if (podcasts == null || podcasts.Count == 0)
				{
					break;
				}
				aggregated.AddRange(podcasts);
				if (podcasts.Count < 300 || aggregated.Count >= total)
				{
					break;
				}
				offset = checked(offset + podcasts.Count);
			}
			lock (_libraryStateLock)
			{
				_subscribedPodcastIds.Clear();
				foreach (PodcastRadioInfo podcast in aggregated)
				{
					if (podcast != null && podcast.Id > 0)
					{
						_subscribedPodcastIds.Add(podcast.Id);
					}
				}
			}
		}
		catch (Exception arg)
		{
			Debug.WriteLine($"[LibraryCache] 刷新收藏播客失败: {arg}");
		}
	}

	private async Task RefreshArtistSubscriptionCacheAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		if (!IsUserLoggedIn())
		{
			return;
		}
		try
		{
			int offset = 0;
			List<ArtistInfo> aggregated = new List<ArtistInfo>();
			while (true)
			{
				SearchResult<ArtistInfo> result = await _apiClient.GetArtistSubscriptionsAsync(200, offset);
				cancellationToken.ThrowIfCancellationRequested();
				if (result?.Items == null || result.Items.Count == 0)
				{
					break;
				}
				aggregated.AddRange(result.Items);
				if (!result.HasMore)
				{
					break;
				}
				offset = checked(offset + result.Items.Count);
			}
			lock (_libraryStateLock)
			{
				_subscribedArtistIds.Clear();
				foreach (ArtistInfo artist in aggregated)
				{
					if (artist != null && artist.Id > 0)
					{
						_subscribedArtistIds.Add(artist.Id);
					}
				}
			}
		}
		catch (Exception arg)
		{
			Debug.WriteLine($"[LibraryCache] 刷新收藏歌手失败: {arg}");
		}
	}

	private void ApplySongLikeStates(IEnumerable<SongInfo?>? songs)
	{
		if (songs == null)
		{
			return;
		}
		lock (_libraryStateLock)
		{
			if (_likedSongIds.Count == 0 && !_likedSongsCacheValid)
			{
				return;
			}
			foreach (SongInfo song in songs)
			{
				if (song != null)
				{
					string text = ResolveSongIdForLibraryState(song);
					if (!string.IsNullOrEmpty(text) && _likedSongIds.Contains(text))
					{
						song.IsLiked = true;
					}
				}
			}
		}
	}

	private void ApplyPlaylistSubscriptionState(IEnumerable<PlaylistInfo?>? playlists)
	{
		if (playlists == null)
		{
			return;
		}
		lock (_libraryStateLock)
		{
			foreach (PlaylistInfo playlist in playlists)
			{
				if (playlist != null && !string.IsNullOrWhiteSpace(playlist.Id))
				{
					if (_ownedPlaylistIds.Contains(playlist.Id))
					{
						playlist.IsSubscribed = false;
					}
					else if (_subscribedPlaylistIds.Contains(playlist.Id))
					{
						playlist.IsSubscribed = true;
					}
				}
			}
		}
	}

	private void ApplyAlbumSubscriptionState(IEnumerable<AlbumInfo?>? albums)
	{
		if (albums == null)
		{
			return;
		}
		lock (_libraryStateLock)
		{
			foreach (AlbumInfo album in albums)
			{
				if (album != null && !string.IsNullOrWhiteSpace(album.Id) && _subscribedAlbumIds.Contains(album.Id))
				{
					album.IsSubscribed = true;
				}
			}
		}
	}

	private void ApplyArtistSubscriptionStates(IEnumerable<ArtistInfo?>? artists)
	{
		if (artists == null)
		{
			return;
		}
		lock (_libraryStateLock)
		{
			foreach (ArtistInfo artist in artists)
			{
				if (artist != null && artist.Id > 0 && _subscribedArtistIds.Contains(artist.Id))
				{
					artist.IsSubscribed = true;
				}
			}
		}
	}

	private void ApplyPodcastSubscriptionState(IEnumerable<PodcastRadioInfo?>? podcasts)
	{
		if (podcasts == null)
		{
			return;
		}
		lock (_libraryStateLock)
		{
			foreach (PodcastRadioInfo podcast in podcasts)
			{
				if (podcast != null && podcast.Id > 0 && !podcast.Subscribed && _subscribedPodcastIds.Contains(podcast.Id))
				{
					podcast.Subscribed = true;
				}
			}
		}
	}

	private void ApplyListItemLibraryStates(IEnumerable<ListItemInfo>? items)
	{
		if (items != null)
		{
			ApplySongLikeStates(from i in items
				where i?.Song != null
				select i.Song);
			ApplyPlaylistSubscriptionState(from i in items
				where i?.Playlist != null
				select i.Playlist);
			ApplyAlbumSubscriptionState(from i in items
				where i?.Album != null
				select i.Album);
			ApplyArtistSubscriptionStates(from i in items
				where i?.Artist != null
				select i.Artist);
			ApplyPodcastSubscriptionState(from i in items
				where i?.Podcast != null
				select i.Podcast);
		}
	}

	private bool IsSongLiked(SongInfo? song)
	{
		if (song == null)
		{
			return false;
		}
		if (song.IsLiked)
		{
			return true;
		}
		string text = ResolveSongIdForLibraryState(song);
		if (string.IsNullOrEmpty(text))
		{
			return false;
		}
		lock (_libraryStateLock)
		{
			if (_likedSongIds.Contains(text))
			{
				song.IsLiked = true;
				return true;
			}
		}
		return false;
	}

	private bool IsPlaylistSubscribed(PlaylistInfo? playlist)
	{
		if (playlist == null || string.IsNullOrWhiteSpace(playlist.Id))
		{
			return false;
		}
		if (IsPlaylistOwnedByUser(playlist, GetCurrentUserId()))
		{
			return false;
		}
		if (playlist.IsSubscribed)
		{
			return true;
		}
		lock (_libraryStateLock)
		{
			if (_subscribedPlaylistIds.Contains(playlist.Id))
			{
				playlist.IsSubscribed = true;
				return true;
			}
		}
		return false;
	}

	private bool IsAlbumSubscribed(AlbumInfo? album)
	{
		if (album == null || string.IsNullOrWhiteSpace(album.Id))
		{
			return false;
		}
		if (album.IsSubscribed)
		{
			return true;
		}
		lock (_libraryStateLock)
		{
			if (_subscribedAlbumIds.Contains(album.Id))
			{
				album.IsSubscribed = true;
				return true;
			}
		}
		return false;
	}

	private bool IsArtistSubscribed(ArtistInfo? artist)
	{
		if (artist == null || artist.Id <= 0)
		{
			return false;
		}
		if (artist.IsSubscribed)
		{
			return true;
		}
		lock (_libraryStateLock)
		{
			if (_subscribedArtistIds.Contains(artist.Id))
			{
				artist.IsSubscribed = true;
				return true;
			}
		}
		return false;
	}

	private void UpdateArtistSubscriptionState(long artistId, bool isSubscribed)
	{
		if (artistId <= 0)
		{
			return;
		}
		lock (_libraryStateLock)
		{
			if (isSubscribed)
			{
				_subscribedArtistIds.Add(artistId);
			}
			else
			{
				_subscribedArtistIds.Remove(artistId);
			}
		}
	}

	private static bool IsPlaylistOwnedByUser(PlaylistInfo? playlist, long userId)
	{
		if (playlist == null || userId <= 0)
		{
			return false;
		}
		if (playlist.CreatorId > 0 && playlist.CreatorId == userId)
		{
			return true;
		}
		if (playlist.OwnerUserId > 0 && playlist.OwnerUserId == userId)
		{
			return true;
		}
		return IsLikedMusicPlaylist(playlist, userId);
	}

	private void UpdateSongLikeState(SongInfo? song, bool isLiked)
	{
		if (song == null)
		{
			return;
		}
		song.IsLiked = isLiked;
		string text = ResolveSongIdForLibraryState(song);
		if (string.IsNullOrEmpty(text))
		{
			return;
		}
		lock (_libraryStateLock)
		{
			if (isLiked)
			{
				_likedSongIds.Add(text);
			}
			else
			{
				_likedSongIds.Remove(text);
			}
		}
	}

	private void UpdatePlaylistSubscriptionState(string? playlistId, bool isSubscribed)
	{
		if (string.IsNullOrWhiteSpace(playlistId))
		{
			return;
		}
		lock (_libraryStateLock)
		{
			if (isSubscribed)
			{
				_subscribedPlaylistIds.Add(playlistId);
			}
			else
			{
				_subscribedPlaylistIds.Remove(playlistId);
			}
		}
	}

	private void UpdatePlaylistOwnershipState(string? playlistId, bool isOwned)
	{
		if (string.IsNullOrWhiteSpace(playlistId))
		{
			return;
		}
		lock (_libraryStateLock)
		{
			if (isOwned)
			{
				_ownedPlaylistIds.Add(playlistId);
				_subscribedPlaylistIds.Remove(playlistId);
			}
			else
			{
				_ownedPlaylistIds.Remove(playlistId);
			}
		}
	}

	private void UpdateAlbumSubscriptionState(string? albumId, bool isSubscribed)
	{
		if (string.IsNullOrWhiteSpace(albumId))
		{
			return;
		}
		lock (_libraryStateLock)
		{
			if (isSubscribed)
			{
				_subscribedAlbumIds.Add(albumId);
			}
			else
			{
				_subscribedAlbumIds.Remove(albumId);
			}
		}
	}

	private void UpdatePodcastSubscriptionState(long podcastId, bool isSubscribed)
	{
		if (podcastId <= 0)
		{
			return;
		}
		lock (_libraryStateLock)
		{
			if (isSubscribed)
			{
				_subscribedPodcastIds.Add(podcastId);
			}
			else
			{
				_subscribedPodcastIds.Remove(podcastId);
			}
		}
	}

	private List<ListItemInfo> BuildRecentListenedEntries()
	{
		return new List<ListItemInfo>
		{
			new ListItemInfo
			{
				Type = ListItemType.Category,
				CategoryId = "recent_play",
				CategoryName = "最近歌曲",
				CategoryDescription = (_recentSummaryReady ? $"{_recentPlayCount} 首" : null)
			},
			new ListItemInfo
			{
				Type = ListItemType.Category,
				CategoryId = "recent_playlists",
				CategoryName = "最近歌单",
				CategoryDescription = (_recentSummaryReady ? $"{_recentPlaylistCount} 个" : null)
			},
			new ListItemInfo
			{
				Type = ListItemType.Category,
				CategoryId = "recent_albums",
				CategoryName = "最近专辑",
				CategoryDescription = (_recentSummaryReady ? $"{_recentAlbumCount} 张" : null)
			},
			new ListItemInfo
			{
				Type = ListItemType.Category,
				CategoryId = "recent_podcasts",
				CategoryName = "最近播客",
				CategoryDescription = (_recentSummaryReady ? $"{_recentPodcastCount} 个" : null)
			}
		};
	}

	private string BuildRecentListenedDescription()
	{
		if (!_recentSummaryReady)
		{
			return string.Empty;
		}
		return $"歌曲 {_recentPlayCount} 首 | 歌单 {_recentPlaylistCount} 个 | 专辑 {_recentAlbumCount} 张 | 播客 {_recentPodcastCount} 个";
	}

	private string BuildRecentListenedStatus()
	{
		if (!_recentSummaryReady)
		{
			return "最近听过";
		}
		return $"最近听过：歌曲 {_recentPlayCount} 首 / 歌单 {_recentPlaylistCount} 个 / 专辑 {_recentAlbumCount} 张 / 播客 {_recentPodcastCount} 个";
	}

	private static (string ArtistLabel, string TrackLabel, string DescriptionLabel) BuildAlbumDisplayLabels(AlbumInfo? album)
	{
		if (album == null)
		{
			return (ArtistLabel: "未知歌手", TrackLabel: "未知曲目数", DescriptionLabel: string.Empty);
		}
		string text = (string.IsNullOrWhiteSpace(album.Artist) ? "未知" : album.Artist.Trim());
		string text2 = AlbumDisplayHelper.BuildTrackAndYearLabel(album);
		if (string.IsNullOrWhiteSpace(text2))
		{
			text2 = ((album.TrackCount > 0) ? $"{album.TrackCount} 首" : "未知");
		}
		string item = (string.IsNullOrWhiteSpace(album.Description) ? string.Empty : (album.Description ?? ""));
		return (ArtistLabel: text ?? "", TrackLabel: text2 ?? "", DescriptionLabel: item);
	}

	private void DisplayAlbums(List<AlbumInfo> albums, bool preserveSelection = false, string? viewSource = null, string? accessibleName = null, int startIndex = 1, bool showPagination = false, bool hasNextPage = false, bool announceHeader = true, bool suppressFocus = false, bool allowSelection = true)
	{
		ConfigureListViewDefault();
		int num = -1;
		if (preserveSelection && resultListView.SelectedIndices.Count > 0)
		{
			num = resultListView.SelectedIndices[0];
		}
		_currentSongs.Clear();
		_currentPlaylists.Clear();
		_currentAlbums = albums ?? new List<AlbumInfo>();
		_currentArtists.Clear();
		_currentListItems.Clear();
		_currentPodcasts.Clear();
		_currentPodcastSounds.Clear();
		_currentPodcast = null;
		ApplyAlbumSubscriptionState(_currentAlbums);
		resultListView.BeginUpdate();
		resultListView.Items.Clear();
		if (albums == null || albums.Count == 0)
		{
			resultListView.EndUpdate();
			SetViewContext(viewSource, accessibleName ?? "专辑列表");
			if (announceHeader)
			{
				AnnounceListViewHeaderIfNeeded(accessibleName ?? "专辑列表");
			}
			return;
		}
		int num2 = startIndex;
		checked
		{
			foreach (AlbumInfo album in albums)
			{
				(string, string, string) tuple = BuildAlbumDisplayLabels(album);
				ListViewItem listViewItem = new ListViewItem(new string[5]
				{
					num2.ToString(),
					album.Name ?? "未知",
					tuple.Item1,
					tuple.Item2,
					tuple.Item3
				});
				listViewItem.Tag = album;
				resultListView.Items.Add(listViewItem);
				num2++;
			}
			if (showPagination)
			{
				if (startIndex > 1)
				{
					ListViewItem listViewItem2 = resultListView.Items.Add("上一页");
					listViewItem2.Tag = -2;
				}
				if (hasNextPage)
				{
					ListViewItem listViewItem3 = resultListView.Items.Add("下一页");
					listViewItem3.Tag = -3;
				}
			}
			resultListView.EndUpdate();
			string text = accessibleName;
			if (string.IsNullOrWhiteSpace(text))
			{
				text = "专辑列表";
			}
			SetViewContext(viewSource, text);
			if (announceHeader)
			{
				AnnounceListViewHeaderIfNeeded(text);
			}
			if (allowSelection && !suppressFocus && !IsListAutoFocusSuppressed && resultListView.Items.Count > 0)
			{
				int targetIndex = ((num >= 0) ? Math.Min(num, resultListView.Items.Count - 1) : 0);
				targetIndex = ResolvePendingListFocusIndex(targetIndex);
				EnsureListSelectionWithoutFocus(targetIndex);
			}
		}
	}

	private void PatchAlbums(List<AlbumInfo> albums, int startIndex, bool showPagination = false, bool hasPreviousPage = false, bool hasNextPage = false, int pendingFocusIndex = -1, bool allowSelection = true)
	{
		int num = ((resultListView.SelectedIndices.Count > 0) ? resultListView.SelectedIndices[0] : pendingFocusIndex);
		_currentSongs.Clear();
		_currentPlaylists.Clear();
		_currentAlbums = albums ?? new List<AlbumInfo>();
		_currentArtists.Clear();
		_currentListItems.Clear();
		_currentPodcasts.Clear();
		_currentPodcastSounds.Clear();
		_currentPodcast = null;
		ApplyAlbumSubscriptionState(_currentAlbums);
		resultListView.BeginUpdate();
		int count = _currentAlbums.Count;
		int count2 = resultListView.Items.Count;
		int num2 = Math.Min(count, count2);
		checked
		{
			for (int i = 0; i < num2; i++)
			{
				AlbumInfo albumInfo = _currentAlbums[i];
				ListViewItem listViewItem = resultListView.Items[i];
				EnsureSubItemCount(listViewItem, 5);
				(string, string, string) tuple = BuildAlbumDisplayLabels(albumInfo);
				listViewItem.Text = (startIndex + i).ToString();
				listViewItem.SubItems[1].Text = albumInfo.Name ?? "未知";
				listViewItem.SubItems[2].Text = tuple.Item1;
				listViewItem.SubItems[3].Text = tuple.Item2;
				listViewItem.SubItems[4].Text = tuple.Item3;
				listViewItem.Tag = albumInfo;
			}
			for (int j = count2; j < count; j++)
			{
				AlbumInfo albumInfo2 = _currentAlbums[j];
				(string, string, string) tuple2 = BuildAlbumDisplayLabels(albumInfo2);
				ListViewItem value = new ListViewItem(new string[5]
				{
					(startIndex + j).ToString(),
					albumInfo2.Name ?? "未知",
					tuple2.Item1,
					tuple2.Item2,
					tuple2.Item3
				})
				{
					Tag = albumInfo2
				};
				resultListView.Items.Add(value);
			}
			for (int num3 = resultListView.Items.Count - 1; num3 >= count; num3--)
			{
				resultListView.Items.RemoveAt(num3);
			}
			if (showPagination)
			{
				if (hasPreviousPage)
				{
					ListViewItem value2 = new ListViewItem("上一页")
					{
						Tag = -2
					};
					resultListView.Items.Add(value2);
				}
				if (hasNextPage)
				{
					ListViewItem value3 = new ListViewItem("下一页")
					{
						Tag = -3
					};
					resultListView.Items.Add(value3);
				}
			}
			resultListView.EndUpdate();
			if (allowSelection && resultListView.Items.Count > 0)
			{
				int fallbackIndex = ((num >= 0) ? Math.Min(num, resultListView.Items.Count - 1) : 0);
				fallbackIndex = ResolvePendingListFocusIndex(fallbackIndex);
				EnsureListSelectionWithoutFocus(fallbackIndex);
			}
		}
	}

	private void ConfigureListViewForPodcasts()
	{
        columnHeader1.Text = string.Empty;
        columnHeader2.Text = string.Empty;
        columnHeader3.Text = string.Empty;
        columnHeader4.Text = string.Empty;
        columnHeader5.Text = string.Empty;
	}

	private void ConfigureListViewForPodcastEpisodes()
	{
        columnHeader1.Text = string.Empty;
        columnHeader2.Text = string.Empty;
        columnHeader3.Text = string.Empty;
        columnHeader4.Text = string.Empty;
        columnHeader5.Text = string.Empty;
	}

	private void DisplayPodcasts(List<PodcastRadioInfo> podcasts, bool showPagination = false, bool hasNextPage = false, int startIndex = 1, bool preserveSelection = false, string? viewSource = null, string? accessibleName = null, bool announceHeader = true, bool suppressFocus = false, bool allowSelection = true)
	{
		ConfigureListViewForPodcasts();
		int num = -1;
		if (preserveSelection && resultListView.SelectedIndices.Count > 0)
		{
			num = resultListView.SelectedIndices[0];
		}
		_currentSongs.Clear();
		_currentPlaylists.Clear();
		_currentAlbums.Clear();
		_currentArtists.Clear();
		_currentListItems.Clear();
		_currentPodcasts = podcasts ?? new List<PodcastRadioInfo>();
		_currentPodcastSounds.Clear();
		_currentPodcast = null;
		ApplyPodcastSubscriptionState(_currentPodcasts);
		resultListView.BeginUpdate();
		resultListView.Items.Clear();
		if (_currentPodcasts.Count == 0)
		{
			resultListView.EndUpdate();
			SetViewContext(viewSource, accessibleName ?? "播客列表");
			if (announceHeader)
			{
				AnnounceListViewHeaderIfNeeded(accessibleName ?? "播客列表");
			}
			return;
		}
		int num2 = startIndex;
		checked
		{
			foreach (PodcastRadioInfo currentPodcast in _currentPodcasts)
			{
				string text = currentPodcast?.DjName ?? string.Empty;
				if (!string.IsNullOrWhiteSpace(currentPodcast?.SecondCategory))
				{
					text = (string.IsNullOrWhiteSpace(text) ? currentPodcast.SecondCategory : (text + " / " + currentPodcast.SecondCategory));
				}
				else if (!string.IsNullOrWhiteSpace(currentPodcast?.Category))
				{
					text = (string.IsNullOrWhiteSpace(text) ? currentPodcast.Category : (text + " / " + currentPodcast.Category));
				}
				string text2 = ((currentPodcast != null && currentPodcast.ProgramCount > 0) ? $"{currentPodcast.ProgramCount} 个节目" : string.Empty);
				ListViewItem value = new ListViewItem(new string[5]
				{
					num2.ToString(),
					currentPodcast?.Name ?? "未知",
					text,
					text2,
					currentPodcast?.Description ?? string.Empty
				})
				{
					Tag = currentPodcast
				};
				resultListView.Items.Add(value);
				num2++;
			}
			if (showPagination)
			{
				if (startIndex > 1)
				{
					ListViewItem listViewItem = resultListView.Items.Add("上一页");
					listViewItem.Tag = -2;
				}
				if (hasNextPage)
				{
					ListViewItem listViewItem2 = resultListView.Items.Add("下一页");
					listViewItem2.Tag = -3;
				}
			}
			resultListView.EndUpdate();
			string accessibleName2 = accessibleName ?? "播客列表";
			SetViewContext(viewSource, accessibleName2);
			if (announceHeader)
			{
				AnnounceListViewHeaderIfNeeded(accessibleName2);
			}
			if (allowSelection && !suppressFocus && !IsListAutoFocusSuppressed && resultListView.Items.Count > 0)
			{
				int targetIndex = ((num >= 0) ? Math.Min(num, resultListView.Items.Count - 1) : 0);
				targetIndex = ResolvePendingListFocusIndex(targetIndex);
				EnsureListSelectionWithoutFocus(targetIndex);
			}
		}
	}

	private void PatchPodcasts(List<PodcastRadioInfo> podcasts, int startIndex, bool showPagination = false, bool hasPreviousPage = false, bool hasNextPage = false, int pendingFocusIndex = -1, bool allowSelection = true)
	{
		int num = ((resultListView.SelectedIndices.Count > 0) ? resultListView.SelectedIndices[0] : pendingFocusIndex);
		_currentSongs.Clear();
		_currentPlaylists.Clear();
		_currentAlbums.Clear();
		_currentArtists.Clear();
		_currentListItems.Clear();
		_currentPodcasts = podcasts ?? new List<PodcastRadioInfo>();
		_currentPodcastSounds.Clear();
		_currentPodcast = null;
		ApplyPodcastSubscriptionState(_currentPodcasts);
		resultListView.BeginUpdate();
		int count = _currentPodcasts.Count;
		int count2 = resultListView.Items.Count;
		int num2 = Math.Min(count, count2);
		checked
		{
			for (int i = 0; i < num2; i++)
			{
				PodcastRadioInfo podcastRadioInfo = _currentPodcasts[i];
				ListViewItem listViewItem = resultListView.Items[i];
				EnsureSubItemCount(listViewItem, 5);
				string text = podcastRadioInfo?.DjName ?? string.Empty;
				if (!string.IsNullOrWhiteSpace(podcastRadioInfo?.SecondCategory))
				{
					text = (string.IsNullOrWhiteSpace(text) ? podcastRadioInfo.SecondCategory : (text + " / " + podcastRadioInfo.SecondCategory));
				}
				else if (!string.IsNullOrWhiteSpace(podcastRadioInfo?.Category))
				{
					text = (string.IsNullOrWhiteSpace(text) ? podcastRadioInfo.Category : (text + " / " + podcastRadioInfo.Category));
				}
				string text2 = ((podcastRadioInfo != null && podcastRadioInfo.ProgramCount > 0) ? $"{podcastRadioInfo.ProgramCount} 个节目" : string.Empty);
				listViewItem.Text = (startIndex + i).ToString();
				listViewItem.SubItems[1].Text = podcastRadioInfo?.Name ?? "未知";
				listViewItem.SubItems[2].Text = text;
				listViewItem.SubItems[3].Text = text2;
				listViewItem.SubItems[4].Text = podcastRadioInfo?.Description ?? string.Empty;
				listViewItem.Tag = podcastRadioInfo;
			}
			for (int j = count2; j < count; j++)
			{
				PodcastRadioInfo podcastRadioInfo2 = _currentPodcasts[j];
				string text3 = podcastRadioInfo2?.DjName ?? string.Empty;
				if (!string.IsNullOrWhiteSpace(podcastRadioInfo2?.SecondCategory))
				{
					text3 = (string.IsNullOrWhiteSpace(text3) ? podcastRadioInfo2.SecondCategory : (text3 + " / " + podcastRadioInfo2.SecondCategory));
				}
				else if (!string.IsNullOrWhiteSpace(podcastRadioInfo2?.Category))
				{
					text3 = (string.IsNullOrWhiteSpace(text3) ? podcastRadioInfo2.Category : (text3 + " / " + podcastRadioInfo2.Category));
				}
				string text4 = ((podcastRadioInfo2 != null && podcastRadioInfo2.ProgramCount > 0) ? $"{podcastRadioInfo2.ProgramCount} 个节目" : string.Empty);
				ListViewItem value = new ListViewItem(new string[5]
				{
					(startIndex + j).ToString(),
					podcastRadioInfo2?.Name ?? "未知",
					text3,
					text4,
					podcastRadioInfo2?.Description ?? string.Empty
				})
				{
					Tag = podcastRadioInfo2
				};
				resultListView.Items.Add(value);
			}
			for (int num3 = resultListView.Items.Count - 1; num3 >= count; num3--)
			{
				resultListView.Items.RemoveAt(num3);
			}
			if (showPagination)
			{
				if (hasPreviousPage)
				{
					ListViewItem value2 = new ListViewItem("上一页")
					{
						Tag = -2
					};
					resultListView.Items.Add(value2);
				}
				if (hasNextPage)
				{
					ListViewItem value3 = new ListViewItem("下一页")
					{
						Tag = -3
					};
					resultListView.Items.Add(value3);
				}
			}
			resultListView.EndUpdate();
			if (allowSelection && resultListView.Items.Count > 0)
			{
				int fallbackIndex = ((num >= 0) ? Math.Min(num, resultListView.Items.Count - 1) : 0);
				fallbackIndex = ResolvePendingListFocusIndex(fallbackIndex);
				EnsureListSelectionWithoutFocus(fallbackIndex);
			}
		}
	}

	private void DisplayPodcastEpisodes(List<PodcastEpisodeInfo> episodes, bool showPagination = false, bool hasNextPage = false, int startIndex = 1, bool preserveSelection = false, string? viewSource = null, string? accessibleName = null)
	{
		ConfigureListViewForPodcastEpisodes();
		int num = -1;
		if (preserveSelection && resultListView.SelectedIndices.Count > 0)
		{
			num = resultListView.SelectedIndices[0];
		}
		List<PodcastEpisodeInfo> list = new List<PodcastEpisodeInfo>();
		if (episodes != null)
		{
			foreach (PodcastEpisodeInfo episode in episodes)
			{
				if (episode != null)
				{
					EnsurePodcastEpisodeSong(episode);
					list.Add(episode);
				}
			}
		}
		_currentPodcastSounds = list;
		_currentSongs = _currentPodcastSounds.Select((PodcastEpisodeInfo e) => e.Song ?? new SongInfo()).ToList();
		_currentPlaylists.Clear();
		_currentAlbums.Clear();
		_currentArtists.Clear();
		_currentListItems.Clear();
		_currentPodcasts.Clear();
		resultListView.BeginUpdate();
		resultListView.Items.Clear();
		if (_currentPodcastSounds.Count == 0)
		{
			resultListView.EndUpdate();
			SetViewContext(viewSource, accessibleName ?? "播客节目");
			AnnounceListViewHeaderIfNeeded(accessibleName ?? "播客节目");
			return;
		}
		int num2 = startIndex;
		checked
		{
			foreach (PodcastEpisodeInfo currentPodcastSound in _currentPodcastSounds)
			{
				string text = string.Empty;
				if (!string.IsNullOrWhiteSpace(currentPodcastSound.RadioName))
				{
					text = currentPodcastSound.RadioName;
				}
				if (!string.IsNullOrWhiteSpace(currentPodcastSound.DjName))
				{
					text = (string.IsNullOrWhiteSpace(text) ? currentPodcastSound.DjName : (text + " / " + currentPodcastSound.DjName));
				}
				string text2 = currentPodcastSound.PublishTime?.ToString("yyyy-MM-dd") ?? string.Empty;
				if (currentPodcastSound.Duration > TimeSpan.Zero)
				{
					string text3 = $"{currentPodcastSound.Duration:mm\\:ss}";
					text2 = (string.IsNullOrEmpty(text2) ? text3 : (text2 + " | " + text3));
				}
				ListViewItem value = new ListViewItem(new string[5]
				{
					num2.ToString(),
					currentPodcastSound.Name ?? "未知",
					text,
					text2,
					currentPodcastSound.Description ?? string.Empty
				})
				{
					Tag = currentPodcastSound
				};
				resultListView.Items.Add(value);
				num2++;
			}
			if (showPagination)
			{
				if (startIndex > 1)
				{
					ListViewItem listViewItem = resultListView.Items.Add("上一页");
					listViewItem.Tag = -2;
				}
				if (hasNextPage)
				{
					ListViewItem listViewItem2 = resultListView.Items.Add("下一页");
					listViewItem2.Tag = -3;
				}
			}
			resultListView.EndUpdate();
			string accessibleName2 = accessibleName ?? "播客节目";
			SetViewContext(viewSource, accessibleName2);
			AnnounceListViewHeaderIfNeeded(accessibleName2);
			if (!IsListAutoFocusSuppressed && resultListView.Items.Count > 0)
			{
				int targetIndex = ((num >= 0) ? Math.Min(num, resultListView.Items.Count - 1) : 0);
				RestoreListViewFocus(targetIndex);
			}
		}
	}

	private async void resultListView_ItemActivate(object sender, EventArgs e)
	{
		if (resultListView.SelectedItems.Count == 0)
		{
			return;
		}
		ListViewItem item = resultListView.SelectedItems[0];
		object tag = item.Tag;
		if (tag is ListItemInfo listItem)
		{
			await HandleListItemActivate(listItem);
			return;
		}
		tag = item.Tag;
		if (tag is PlaylistInfo playlist)
		{
			await OpenPlaylist(playlist);
			return;
		}
		tag = item.Tag;
		if (tag is AlbumInfo album)
		{
			await OpenAlbum(album);
			return;
		}
		tag = item.Tag;
		if (tag is ArtistInfo artist)
		{
			await OpenArtistAsync(artist);
			return;
		}
		tag = item.Tag;
		if (tag is PodcastRadioInfo podcast)
		{
			await OpenPodcastRadioAsync(podcast);
			return;
		}
		tag = item.Tag;
		if (tag is PodcastEpisodeInfo episodeInfo)
		{
			if (episodeInfo?.Song != null)
			{
				await PlaySong(episodeInfo.Song);
			}
			return;
		}
		int data = ((item.Tag is int) ? ((int)item.Tag) : item.Index);
		if (data == -2)
		{
			OnPrevPage();
		}
		else if (data == -3)
		{
			OnNextPage();
		}
		else if (data >= 0 && data < _currentSongs.Count)
		{
			await PlaySong(_currentSongs[data]);
		}
	}

	private async void resultListView_DoubleClick(object sender, EventArgs e)
	{
		if (resultListView.SelectedItems.Count == 0)
		{
			return;
		}
		ListViewItem item = resultListView.SelectedItems[0];
		Debug.WriteLine($"[MainForm] DoubleClick, Tag={item.Tag}, Type={item.Tag?.GetType().Name}");
		object tag = item.Tag;
		if (tag is ListItemInfo listItem)
		{
			await HandleListItemActivate(listItem);
			return;
		}
		tag = item.Tag;
		if (tag is PlaylistInfo playlist)
		{
			Debug.WriteLine("[MainForm] 双击打开歌单: " + playlist.Name);
			await OpenPlaylist(playlist);
			return;
		}
		tag = item.Tag;
		if (tag is AlbumInfo album)
		{
			Debug.WriteLine("[MainForm] 双击打开专辑: " + album.Name);
			await OpenAlbum(album);
			return;
		}
		tag = item.Tag;
		if (tag is ArtistInfo artist)
		{
			Debug.WriteLine("[MainForm] 双击打开歌手: " + artist.Name);
			await OpenArtistAsync(artist);
			return;
		}
		tag = item.Tag;
		if (tag is PodcastRadioInfo podcast)
		{
			await OpenPodcastRadioAsync(podcast);
			return;
		}
		tag = item.Tag;
		if (tag is PodcastEpisodeInfo episode)
		{
			if (episode?.Song != null)
			{
				await PlaySong(episode.Song);
			}
			return;
		}
		tag = item.Tag;
		int index = default(int);
		int num;
		if (tag is int)
		{
			index = (int)tag;
			if (index >= 0)
			{
				num = ((index < _currentSongs.Count) ? 1 : 0);
				goto IL_0556;
			}
		}
		num = 0;
		goto IL_0556;
		IL_0556:
		if (num != 0)
		{
			SongInfo song = _currentSongs[index];
			Debug.WriteLine("[MainForm] 双击播放歌曲: " + song?.Name);
			await PlaySong(song);
			return;
		}
		tag = item.Tag;
		if (tag is SongInfo song2)
		{
			Debug.WriteLine("[MainForm] 双击播放歌曲(直接Tag): " + song2?.Name);
			await PlaySong(song2);
		}
	}

	private async void OnPrevPage()
	{
		checked
		{
			if (!string.IsNullOrEmpty(_currentViewSource) && _currentViewSource.StartsWith("search:", StringComparison.OrdinalIgnoreCase))
			{
				if (_currentPage > 1)
				{
					int targetPage = _currentPage - 1;
					if (!(await ReloadCurrentSearchPageAsync(targetPage)))
					{
						UpdateStatusBar("没有可用的上一页数据");
					}
				}
				else
				{
					UpdateStatusBar("已经是第一页");
				}
			}
			else if (!string.IsNullOrEmpty(_currentViewSource) && _currentViewSource.StartsWith("artist_songs:", StringComparison.OrdinalIgnoreCase))
			{
				ParseArtistListViewSource(_currentViewSource, out long artistId, out int offset, out string order);
				if (offset <= 0)
				{
					UpdateStatusBar("已经是第一页");
				}
				else
				{
					await LoadArtistSongsAsync(offset: Math.Max(0, offset - 100), artistId: artistId, skipSave: true, orderOverride: ResolveArtistSongsOrder(order));
				}
			}
			else if (!string.IsNullOrEmpty(_currentViewSource) && _currentViewSource.StartsWith("artist_albums:", StringComparison.OrdinalIgnoreCase))
			{
				ParseArtistListViewSource(_currentViewSource, out long artistId2, out int offset2, out string order2, "latest");
				if (offset2 <= 0)
				{
					UpdateStatusBar("已经是第一页");
				}
				else
				{
					await LoadArtistAlbumsAsync(offset: Math.Max(0, offset2 - 100), artistId: artistId2, skipSave: true, sortOverride: ResolveArtistAlbumSort(order2));
				}
			}
			else if (!string.IsNullOrEmpty(_currentViewSource) && _currentViewSource.StartsWith("artist_category_list:", StringComparison.OrdinalIgnoreCase))
			{
				ParseArtistCategoryListViewSource(_currentViewSource, out var typeCode, out var areaCode, out var offset3);
				if (offset3 <= 0)
				{
					UpdateStatusBar("已经是第一页");
				}
				else
				{
					await LoadArtistsByCategoryAsync(offset: Math.Max(0, offset3 - 100), typeCode: typeCode, areaCode: areaCode, skipSave: true);
				}
			}
			else if (!string.IsNullOrEmpty(_currentViewSource) && _currentViewSource.StartsWith("podcast:", StringComparison.OrdinalIgnoreCase))
			{
				ParsePodcastViewSource(_currentViewSource, out var podcastId, out var offset4, out var ascending);
				if (podcastId <= 0)
				{
					UpdateStatusBar("无法定位播客页码");
				}
				else if (offset4 <= 0)
				{
					UpdateStatusBar("已经是第一页");
				}
				else
				{
					await LoadPodcastEpisodesAsync(offset: Math.Max(0, offset4 - 50), radioId: podcastId, skipSave: true, podcastInfo: null, sortAscendingOverride: ascending);
				}
			}
			else if (string.Equals(_currentViewSource, "user_cloud", StringComparison.OrdinalIgnoreCase))
			{
				if (_cloudPage <= 1)
				{
					UpdateStatusBar("已经是第一页");
					return;
				}
				_cloudPage = Math.Max(1, _cloudPage - 1);
				await LoadCloudSongsAsync(skipSave: true);
			}
			else
			{
				UpdateStatusBar("当前内容不支持翻页");
			}
		}
	}

	private async void OnNextPage()
	{
		checked
		{
			if (!string.IsNullOrEmpty(_currentViewSource) && _currentViewSource.StartsWith("search:", StringComparison.OrdinalIgnoreCase))
			{
				if (!_hasNextSearchPage && _currentPage >= _maxPage)
				{
					UpdateStatusBar("已经是最后一页");
					return;
				}
				int targetPage = _currentPage + 1;
				if (_maxPage > 0)
				{
					targetPage = Math.Min(targetPage, _maxPage);
				}
				if (!(await ReloadCurrentSearchPageAsync(targetPage)))
				{
					UpdateStatusBar("无法加载下一页数据");
				}
			}
			else if (!string.IsNullOrEmpty(_currentViewSource) && _currentViewSource.StartsWith("artist_songs:", StringComparison.OrdinalIgnoreCase))
			{
				if (!_currentArtistSongsHasMore)
				{
					UpdateStatusBar("已经是最后一页");
					return;
				}
				ParseArtistListViewSource(_currentViewSource, out long artistId, out int offset, out string order);
				int newOffset = offset + 100;
				await LoadArtistSongsAsync(artistId, newOffset, skipSave: true, ResolveArtistSongsOrder(order));
			}
			else if (!string.IsNullOrEmpty(_currentViewSource) && _currentViewSource.StartsWith("artist_albums:", StringComparison.OrdinalIgnoreCase))
			{
				if (!_currentArtistAlbumsHasMore)
				{
					UpdateStatusBar("已经是最后一页");
					return;
				}
				ParseArtistListViewSource(_currentViewSource, out long artistId2, out int offset2, out string order2, "latest");
				int newOffset2 = offset2 + 100;
				await LoadArtistAlbumsAsync(artistId2, newOffset2, skipSave: true, ResolveArtistAlbumSort(order2));
			}
			else if (!string.IsNullOrEmpty(_currentViewSource) && _currentViewSource.StartsWith("artist_category_list:", StringComparison.OrdinalIgnoreCase))
			{
				if (!_currentArtistCategoryHasMore)
				{
					UpdateStatusBar("已经是最后一页");
					return;
				}
				ParseArtistCategoryListViewSource(_currentViewSource, out var typeCode, out var areaCode, out var offset3);
				int newOffset3 = offset3 + 100;
				await LoadArtistsByCategoryAsync(typeCode, areaCode, newOffset3, skipSave: true);
			}
			else if (!string.IsNullOrEmpty(_currentViewSource) && _currentViewSource.StartsWith("podcast:", StringComparison.OrdinalIgnoreCase))
			{
				if (!_currentPodcastHasMore)
				{
					UpdateStatusBar("已经是最后一页");
					return;
				}
				ParsePodcastViewSource(_currentViewSource, out var podcastId, out var offset4, out var ascending);
				int newOffset4 = offset4 + 50;
				await LoadPodcastEpisodesAsync(podcastId, newOffset4, skipSave: true, null, ascending);
			}
			else if (string.Equals(_currentViewSource, "user_cloud", StringComparison.OrdinalIgnoreCase))
			{
				if (!_cloudHasMore)
				{
					UpdateStatusBar("已经是最后一页");
					return;
				}
				_cloudPage++;
				await LoadCloudSongsAsync(skipSave: true);
			}
			else
			{
				UpdateStatusBar("当前内容不支持翻页");
			}
		}
	}

	private async Task<bool> ReloadCurrentSearchPageAsync(int targetPage)
	{
		if (string.IsNullOrWhiteSpace(_currentViewSource) || !_currentViewSource.StartsWith("search:", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		ParseSearchViewSource(_currentViewSource, out string parsedType, out string parsedKeyword, out int parsedPage);
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
			if (!TryHandleOperationCancelled(ex3, "搜索加载已取消"))
			{
				Debug.WriteLine($"[Search] 重新加载搜索结果失败: {ex3}");
				MessageBox.Show("无法重新加载搜索结果: " + ex3.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
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
			Debug.WriteLine("[Lyrics] 加载失败: " + ex3.Message);
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
				BeginInvoke(new Action(SyncPlayPauseButtonText));
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
		if (trayPlayPauseMenuItem != null && !trayPlayPauseMenuItem.IsDisposed)
		{
			string text2 = ((playbackState == PlaybackState.Playing) ? "暂停(&P)" : "播放(&P)");
			if (trayPlayPauseMenuItem.Text != text2)
			{
				trayPlayPauseMenuItem.Text = text2;
				Debug.WriteLine("[SyncPlayPauseButtonText] 托盘菜单文本已更新: " + text2);
			}
		}
	}

	private void TogglePlayPause()
	{
		if (_audioEngine != null)
		{
			switch (_audioEngine.GetPlaybackState())
			{
			case PlaybackState.Playing:
				_audioEngine.Pause();
				break;
			case PlaybackState.Paused:
				_audioEngine.Resume();
				break;
			}
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
		TogglePlayPause();
	}

	private int CalculateTargetIndexAfterDeletion(int deletedIndex, int newListCount)
	{
		if (newListCount == 0)
		{
			return -1;
		}
		checked
		{
			int val = ((deletedIndex >= newListCount) ? (newListCount - 1) : deletedIndex);
			return Math.Max(0, Math.Min(val, newListCount - 1));
		}
	}

	private void EnsureListSelectionWithoutFocus(int targetIndex)
	{
		if (targetIndex < 0 || resultListView.Items.Count == 0)
		{
			return;
		}
		targetIndex = Math.Max(0, Math.Min(targetIndex, checked(resultListView.Items.Count - 1)));
		resultListView.BeginUpdate();
		try
		{
			resultListView.SelectedItems.Clear();
			ListViewItem listViewItem = resultListView.Items[targetIndex];
			listViewItem.Selected = true;
			listViewItem.Focused = true;
			listViewItem.EnsureVisible();
		}
		finally
		{
			resultListView.EndUpdate();
		}
	}

	private void RestoreListViewFocus(int targetIndex)
	{
		targetIndex = ResolvePendingListFocusIndex(targetIndex);
		EnsureListSelectionWithoutFocus(targetIndex);
		if (resultListView.CanFocus)
		{
			resultListView.Focus();
		}
	}

	private void FocusListAfterEnrich(int pendingFocusIndex)
	{
		if (!IsListAutoFocusSuppressed && resultListView.Items.Count != 0)
		{
			int targetIndex = ((pendingFocusIndex >= 0) ? pendingFocusIndex : ((resultListView.SelectedIndices.Count > 0) ? resultListView.SelectedIndices[0] : 0));
			RestoreListViewFocus(targetIndex);
		}
	}

	private void resultListView_SelectedIndexChanged(object sender, EventArgs e)
	{
		if (!base.Visible || resultListView.SelectedItems.Count <= 0)
		{
			return;
		}
		int num = resultListView.SelectedIndices[0];
		if (_lastListViewFocusedIndex != num)
		{
			_lastListViewFocusedIndex = num;
			Debug.WriteLine($"[MainForm] 用户选择变化，保存索引={num}");
		}
		if (string.Equals(_currentViewSource, "user_cloud", StringComparison.OrdinalIgnoreCase))
		{
			SongInfo songFromListViewItem = GetSongFromListViewItem(resultListView.SelectedItems[0]);
			if (songFromListViewItem != null && songFromListViewItem.IsCloudSong && !string.IsNullOrEmpty(songFromListViewItem.CloudSongId))
			{
				_lastSelectedCloudSongId = songFromListViewItem.CloudSongId;
			}
		}
	}

	private void NotifyAccessibilityClients(Control control, AccessibleEvents accEvent, int childID)
	{
		if (control == null)
		{
			return;
		}
		try
		{
			MethodInfo method = typeof(Control).GetMethod("AccessibilityNotifyClients", BindingFlags.Instance | BindingFlags.NonPublic, null, new Type[2]
			{
				typeof(AccessibleEvents),
				typeof(int)
			}, null);
			if (method != null)
			{
				method.Invoke(control, new object[2] { accEvent, childID });
				Debug.WriteLine($"[AccessibilityHelper] 通知 {control.Name}: Event={accEvent}, ChildID={childID}");
			}
			else
			{
				Debug.WriteLine("[AccessibilityHelper] 无法找到 AccessibilityNotifyClients 方法");
			}
		}
		catch (Exception ex)
		{
			Debug.WriteLine("[AccessibilityHelper] 反射调用失败: " + ex.Message);
		}
	}

	private void UpdatePlayButtonDescription(SongInfo? song)
	{
		if (base.InvokeRequired)
		{
			BeginInvoke(new Action<SongInfo>(UpdatePlayButtonDescription), song);
			return;
		}
		if (song == null)
		{
			playPauseButton.AccessibleDescription = "播放/暂停";
			UpdateWindowTitle(null);
			UpdateCurrentPlayingMenuItem(null);
			return;
		}
		string text = (song.IsTrial ? (song.Name + "(试听版)") : song.Name);
		string text2 = text + " - " + song.Artist;
		if (!string.IsNullOrEmpty(song.Album))
		{
			text2 = text2 + " [" + song.Album + "]";
		}
		if (!string.IsNullOrEmpty(song.Level))
		{
			string qualityDisplayName = NeteaseApiClient.GetQualityDisplayName(song.Level);
			text2 = text2 + " | " + qualityDisplayName;
		}
		playPauseButton.AccessibleDescription = text2;
		UpdateWindowTitle(text2);
		UpdateCurrentPlayingMenuItem(song);
		Debug.WriteLine("[MainForm] 更新播放按钮描述: " + text2);
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
		string b = ((string.IsNullOrWhiteSpace(playbackDescription) || playbackDescription == "播放/暂停") ? "易听" : ("易听 - " + playbackDescription));
		if (!string.Equals(Text, b, StringComparison.Ordinal))
		{
			Text = b;
		}
	}

	private void UpdateTimer_Tick(object sender, EventArgs e)
	{
		if (_audioEngine == null || _isUserDragging || (_seekManager != null && _seekManager.IsSeeking))
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
			string accessibleName = FormatTimeFromSeconds(cachedPosition) + " / " + FormatTimeFromSeconds(cachedDuration);
			timeLabel.Text = accessibleName;
			progressTrackBar.AccessibleName = accessibleName;
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
		string text = ((cachedPlaybackState == PlaybackState.Playing) ? "暂停" : "播放");
		if (playPauseButton.Text != text)
		{
			playPauseButton.Text = text;
			Debug.WriteLine($"[UpdateTimer_Tick] ⚠\ufe0f 检测到按钮文本不一致，已自动修正: {text} (状态={cachedPlaybackState})");
		}
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
		if (_isPlaybackLoading)
		{
			Debug.WriteLine("[MainForm] " + (isRight ? "右" : "左") + "键快进快退被忽略：歌曲加载中");
			return;
		}
		if (_seekManager == null || _audioEngine == null)
		{
			Debug.WriteLine("[MainForm] " + (isRight ? "右" : "左") + "键快进快退被忽略：SeekManager或AudioEngine未初始化");
			return;
		}
		if (!_audioEngine.IsPlaying && !_audioEngine.IsPaused)
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
			_scrubKeyTimer.Interval = 200;
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
			double cachedPosition = GetCachedPosition();
			double cachedDuration = GetCachedDuration();
			double num = ((direction > 0.0) ? Math.Min(cachedDuration, cachedPosition + Math.Abs(direction)) : Math.Max(0.0, cachedPosition + direction));
			Debug.WriteLine($"[MainForm] 请求 Seek: {cachedPosition:F1}s → {num:F1}s (方向: {direction:+0;-0})");
			RequestSeekAndResetLyrics(num);
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
			progressTrackBar.BeginInvoke((Action)delegate
			{
				UpdateProgressTrackBarAccessibleName();
			});
		}
		else if (progressTrackBar != null)
		{
			UpdateProgressTrackBarAccessibleName();
		}
	}

	private void OnBufferingStateChanged(object sender, BufferingState state)
	{
		Debug.WriteLine($"[MainForm] 缓冲状态变化: {state}");
		if (base.InvokeRequired)
		{
			BeginInvoke((Action)delegate
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
		DetectLyricPositionJump(position);
		_lyricsDisplayManager?.UpdatePosition(position);
	}

	private void OnLyricUpdated(object? sender, LyricUpdateEventArgs e)
	{
		if (base.InvokeRequired)
		{
			BeginInvoke((Action)delegate
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
			select text).Distinct(StringComparer.Ordinal).ToList();
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

	private void RequestSeekAndResetLyrics(double targetPosition)
	{
		CancelPendingLyricSpeech(resetSuppression: false);
		BeginLyricSeekSuppression(targetPosition);
		_lastLyricPlaybackPosition = TimeSpan.FromSeconds(targetPosition);
		if (_seekManager != null)
		{
			_seekManager.RequestSeek(targetPosition);
		}
		else
		{
			_audioEngine?.SetPosition(targetPosition);
		}
	}

	private void AudioEngine_PlaybackStopped(object sender, EventArgs e)
	{
		Debug.WriteLine("[MainForm] AudioEngine_PlaybackStopped 被调用");
		if (base.InvokeRequired)
		{
			Debug.WriteLine("[MainForm] 需要切换到 UI 线程");
			BeginInvoke((Action)delegate
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
				PlaySongDirectAsync(currentSong);
			}
			else
			{
				Debug.WriteLine("[MainForm ERROR] 单曲循环后备处理失败：CurrentSong 为 null");
			}
		}
		else if (!suppressAutoAdvance)
		{
			Debug.WriteLine("[MainForm] 调用 PlayNext() (自动播放)");
			PlayNext(isManual: false);
		}
	}

	private void AudioEngine_PlaybackEnded(object sender, SongInfo? e)
	{
		Debug.WriteLine("[MainForm] AudioEngine_PlaybackEnded 被调用，歌曲: " + e?.Name);
		if (base.InvokeRequired)
		{
			try
			{
				BeginInvoke((Action)delegate
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
			PlayNext(isManual: false);
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
				BeginInvoke((Action)delegate
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

	private async void PlaySongDirectAsync(SongInfo song)
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
			UpdateStatusBar("播放失败: " + ex.Message);
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
		checked
		{
			SongUrlInfo songUrl = default(SongUrlInfo);
			string resolvedUrl = default(string);
			for (int attempt = 0; attempt < maxAttempts; attempt++)
			{
				SongInfo nextSong = PredictNextSong();
				if (nextSong == null)
				{
					Debug.WriteLine($"[MainForm] \ud83d\udd0d 预加载：无可用的下一首（尝试 {attempt + 1}/{maxAttempts}）");
					return false;
				}
				Debug.WriteLine($"[MainForm] \ud83d\udd0d 预加载尝试 {attempt + 1}：{nextSong.Name}, IsAvailable={nextSong.IsAvailable}");
				if (!nextSong.IsAvailable.HasValue)
				{
					Debug.WriteLine("[MainForm] \ud83d\udd0d 歌曲未检查过（IsAvailable=null），执行有效性检查: " + nextSong.Name);
					try
					{
						Dictionary<string, SongUrlInfo> urlResult = await _apiClient.GetSongUrlAsync(new string[1] { nextSong.Id }, quality).ConfigureAwait(continueOnCapturedContext: false);
						int num;
						if (urlResult != null && urlResult.TryGetValue(nextSong.Id, out songUrl))
						{
							if (songUrl != null)
							{
								resolvedUrl = songUrl.Url;
								if (resolvedUrl != null)
								{
									num = ((resolvedUrl.Length > 0) ? 1 : 0);
									goto IL_0203;
								}
							}
							num = 0;
						}
						else
						{
							num = 0;
						}
						goto IL_0203;
						IL_0203:
						if (num != 0)
						{
							FreeTrialInfo trialInfo = songUrl.FreeTrialInfo;
							bool isTrial = trialInfo != null;
							long trialStart = trialInfo?.Start ?? 0;
							long trialEnd = trialInfo?.End ?? 0;
							if (isTrial)
							{
								Debug.WriteLine(unchecked($"[MainForm] \ud83c\udfb5 试听版本（预加载检查）: {nextSong.Name}, 片段: {trialStart / 1000}s - {trialEnd / 1000}s"));
							}
							nextSong.IsAvailable = true;
							nextSong.Url = resolvedUrl;
							string resolvedLevel = (nextSong.Level = songUrl.Level ?? quality.ToString().ToLowerInvariant());
							nextSong.Size = songUrl.Size;
							nextSong.IsTrial = isTrial;
							nextSong.TrialStart = trialStart;
							nextSong.TrialEnd = trialEnd;
							string actualLevel = resolvedLevel.ToLowerInvariant();
							nextSong.SetQualityUrl(actualLevel, resolvedUrl, songUrl.Size, isAvailable: true, isTrial, trialStart, trialEnd);
							Debug.WriteLine($"[MainForm] ✓ 歌曲可用并已缓存: {nextSong.Name}, 音质: {actualLevel}, 试听: {isTrial}");
							songUrl = null;
							resolvedUrl = null;
							goto IL_0483;
						}
						nextSong.IsAvailable = false;
						Debug.WriteLine("[MainForm] ✗ 歌曲不可用: " + nextSong.Name + "，尝试下一首");
					}
					catch (Exception ex)
					{
						Exception ex2 = ex;
						Debug.WriteLine("[MainForm] 检查可用性异常: " + nextSong.Name + ", " + ex2.Message);
						nextSong.IsAvailable = false;
					}
					continue;
				}
				goto IL_0483;
				IL_0483:
				if (nextSong.IsAvailable == false)
				{
					Debug.WriteLine("[MainForm] ⏭\ufe0f 跳过不可用歌曲: " + nextSong.Name + "，继续查找");
					continue;
				}
				SongInfo currentSong = _audioEngine?.CurrentSong;
				if (currentSong != null)
				{
					_nextSongPreloader?.CleanupStaleData(currentSong.Id, nextSong.Id);
				}
				Debug.WriteLine("[MainForm] \ud83c\udfaf 开始预加载可用歌曲：" + nextSong.Name);
				if (_nextSongPreloader == null)
				{
					Debug.WriteLine("[MainForm] ⚠\ufe0f 预加载器未初始化");
					return false;
				}
				if (await _nextSongPreloader.StartPreloadAsync(nextSong, quality))
				{
					PreloadedData gaplessData = _nextSongPreloader.TryGetPreloadedData(nextSong.Id);
					if (gaplessData != null)
					{
						_audioEngine?.RegisterGaplessPreload(nextSong, gaplessData);
					}
					Debug.WriteLine("[MainForm] ✓✓✓ 预加载成功: " + nextSong.Name);
					return true;
				}
				Debug.WriteLine("[MainForm] ⚠\ufe0f 预加载失败: " + nextSong.Name + "，尝试下一首（不标记不可用，允许后续重试）");
				if (nextSong.IsAvailable != false)
				{
				}
			}
			Debug.WriteLine($"[MainForm] ❌ 尝试了 {maxAttempts} 次，未找到可用歌曲");
			return false;
		}
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
				Debug.WriteLine("[StreamCheck] 所有歌曲都已检查过，跳过");
			}
			else
			{
				if (cancellationToken.IsCancellationRequested)
				{
					return;
				}
				Debug.WriteLine($"[StreamCheck] \ud83d\ude80 开始流式检查 {uncheckedSongs.Count} 首歌曲（实时填入）");
				string defaultQualityName = _config.DefaultQuality ?? "超清母带";
				QualityLevel selectedQuality = NeteaseApiClient.GetQualityLevelFromName(defaultQualityName);
				string[] ids = (from s in uncheckedSongs
					select s.Id into id
					where !string.IsNullOrWhiteSpace(id)
					select id).ToArray();
				if (ids.Length == 0)
				{
					return;
				}
				ConcurrentDictionary<string, SongInfo> songLookup = new ConcurrentDictionary<string, SongInfo>(uncheckedSongs.Where((SongInfo s) => !string.IsNullOrWhiteSpace(s.Id)).ToDictionary<SongInfo, string, SongInfo>((SongInfo s) => s.Id, (SongInfo s) => s, StringComparer.Ordinal), StringComparer.Ordinal);
				int available = 0;
				int unavailable = 0;
				await _apiClient.BatchCheckSongsAvailabilityStreamAsync(ids, selectedQuality, delegate(string songId, bool isAvailable)
				{
					if (!cancellationToken.IsCancellationRequested && songLookup.TryGetValue(songId, out SongInfo value))
					{
						value.IsAvailable = isAvailable;
						if (isAvailable)
						{
							Interlocked.Increment(ref available);
						}
						else
						{
							Interlocked.Increment(ref unavailable);
							Debug.WriteLine("[StreamCheck] ⚠\ufe0f 标记不可用: " + value.Name);
						}
					}
				}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				if (!cancellationToken.IsCancellationRequested)
				{
					Debug.WriteLine($"[StreamCheck] \ud83c\udf89 流式检查全部完成：{available} 首可用，{unavailable} 首不可用");
				}
			}
		}
		catch (OperationCanceledException)
		{
			Debug.WriteLine("[StreamCheck] 可用性检查任务已取消");
		}
		catch (Exception ex2)
		{
			Debug.WriteLine("[StreamCheck] 流式检查失败: " + ex2.Message);
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
			else if (statusStrip1.Items.Count > 0)
			{
				((ToolStripStatusLabel)statusStrip1.Items[0]).Text = message;
			}
		}
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
				if (statusStrip1 != null && statusStrip1.Items.Count > 0 && statusStrip1.Items[0] is ToolStripStatusLabel toolStripStatusLabel)
				{
					_statusTextBeforeLoading = toolStripStatusLabel.Text;
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
			int num = (int)(seconds / 60.0);
			int num2 = (int)(seconds % 60.0);
			return $"{num:D2}:{num2:D2}";
		}
	}

	[DllImport("user32.dll")]
	private static extern bool GetComboBoxInfo(IntPtr hwndCombo, ref COMBOBOXINFO info);

	[DllImport("user32.dll")]
	private static extern IntPtr SetFocus(IntPtr hWnd);

	[DllImport("user32.dll")]
	private static extern bool SetForegroundWindow(IntPtr hWnd);

	[DllImport("user32.dll")]
	private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

	private void FocusComboEditChild(ComboBox combo)
	{
		if (combo != null && !combo.IsDisposed)
		{
			COMBOBOXINFO info = new COMBOBOXINFO
			{
				cbSize = Marshal.SizeOf(typeof(COMBOBOXINFO))
			};
			if (GetComboBoxInfo(combo.Handle, ref info) && info.hwndItem != IntPtr.Zero)
			{
				SetFocus(info.hwndItem);
			}
		}
	}

	private void searchTypeComboBox_KeyPress(object sender, KeyPressEventArgs e)
	{
		e.Handled = true;
	}

	private void searchTypeComboBox_SelectedIndexChanged(object sender, EventArgs e)
	{
		if (searchTypeComboBox.SelectedIndex >= 0)
		{
			string text = searchTypeComboBox.SelectedItem?.ToString() ?? string.Empty;
			if (!string.IsNullOrWhiteSpace(text))
			{
				_lastExplicitSearchType = text;
			}
			_isMixedSearchTypeActive = false;
			UpdateSearchTypeAccessibleAnnouncement(text);
		}
	}

	private void searchTypeComboBox_DropDownClosed(object sender, EventArgs e)
	{
		FocusComboEditChild(searchTypeComboBox);
		AccessibilityNotifyClients(AccessibleEvents.Focus, -1);
	}

	private void searchTypeComboBox_Enter(object sender, EventArgs e)
	{
		FocusComboEditChild(searchTypeComboBox);
		AccessibilityNotifyClients(AccessibleEvents.Focus, -1);
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
				goto IL_00df;
			}
		}
		if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Left || e.KeyCode == Keys.Right)
		{
			return;
		}
		goto IL_00df;
		IL_00df:
		checked
		{
			if (e.KeyCode == Keys.Space)
			{
				e.Handled = true;
				e.SuppressKeyPress = true;
				TogglePlayPause();
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
				PlayPrevious();
			}
			else if (e.KeyCode == Keys.F4)
			{
				e.Handled = true;
				e.SuppressKeyPress = true;
				PlayNext();
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
			BeginInvoke((Action)delegate
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
							if (resultListView.SelectedItems.Count > 0)
							{
								targetIndex = resultListView.SelectedIndices[0];
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
						resultListView.SelectedItems.Clear();
						BeginInvoke((Action)delegate
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
					AccessibilityNotifyClients(AccessibleEvents.Focus, -1);
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
			ShowTrayContextMenu(Cursor.Position);
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
					ClearLoginState(persist: true);
					EnsureConfigInitialized();
					if (base.InvokeRequired)
					{
						Invoke((Action)delegate
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
										await (Task)Invoke((Func<Task>)(() => LoadHomePageAsync()));
									}
									catch (Exception ex3)
									{
										Exception homeEx = ex3;
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
									Exception homeEx = ex3;
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
			DialogResult dialogResult = loginForm.ShowDialog(this);
			Debug.WriteLine($"[LoginMenuItem] ShowDialog()返回，结果={dialogResult}");
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
			loginMenuItem.AccessibleName = text2;
			loginMenuItem.AccessibleDescription = "当前登录账号: " + text2 + "，详细信息";
		}
		else
		{
			Debug.WriteLine("[UpdateLoginMenuItemText] 设置菜单项为: 登录");
			loginMenuItem.Text = "登录";
			loginMenuItem.AccessibleName = "登录";
			loginMenuItem.AccessibleDescription = "点击打开登录对话框";
		}
	}

	private static string GetVipDescription(int vipType)
	{
		return vipType switch
		{
			11 => "黑胶VIP", 
			10 => "豪华VIP", 
			_ => (vipType > 0) ? "普通VIP" : "普通用户", 
		};
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
			Invoke((Action)delegate
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
					Debug.WriteLine("[LoginMenuItem] 登录后同步资料失败: " + ex3.Message);
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
						await (Task)Invoke((Func<Task>)async delegate
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
					Exception homeEx = ex2;
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
		TogglePlayPause();
	}

	private void trayPrevMenuItem_Click(object sender, EventArgs e)
	{
		PlayPrevious();
	}

	private void trayNextMenuItem_Click(object sender, EventArgs e)
	{
		PlayNext();
	}

	private void trayExitMenuItem_Click(object sender, EventArgs e)
	{
		Debug.WriteLine("[trayExitMenuItem] 退出菜单项被点击");
		_isApplicationExitRequested = true;
		BeginInvoke((Action)delegate
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

	private void TrayContextMenu_Opened(object sender, EventArgs e)
	{
		Debug.WriteLine("[TrayContextMenu] 菜单已打开，设置焦点...");
		if (trayContextMenu.Items.Count <= 0)
		{
			return;
		}
		BeginInvoke((Action)delegate
		{
			try
			{
				ToolStripItem toolStripItem = trayContextMenu.Items[0];
				if (toolStripItem != null && toolStripItem.Available && toolStripItem.Enabled)
				{
					trayContextMenu.Select();
					toolStripItem.Select();
					Debug.WriteLine("[TrayContextMenu] 焦点已设置到: " + toolStripItem.Text);
				}
			}
			catch (Exception ex)
			{
				Debug.WriteLine("[TrayContextMenu] 设置焦点失败: " + ex.Message);
			}
		});
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
				BeginInvoke((Action)delegate
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
			BeginInvoke((Action)delegate
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
		TogglePlayPause();
	}

	private void prevMenuItem_Click(object sender, EventArgs e)
	{
		PlayPrevious();
	}

	private void nextMenuItem_Click(object sender, EventArgs e)
	{
		PlayNext();
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
		if (_isPlaybackLoading)
		{
			Debug.WriteLine("[MainForm] F12跳转被忽略：歌曲加载中");
			return;
		}
		if (_audioEngine == null || (!_audioEngine.IsPlaying && !_audioEngine.IsPaused))
		{
			Debug.WriteLine("[MainForm] F12跳转被忽略：没有正在播放的歌曲");
			return;
		}
		try
		{
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
			MessageBox.Show(this, "无法获取输出设备列表: " + ex2.Message, "输出设备", MessageBoxButtons.OK, MessageBoxIcon.Hand);
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
		catch (Exception ex4)
		{
			MessageBox.Show(this, "切换输出设备失败: " + ex4.Message, "输出设备", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
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
		SetMenuItemCheckedState(sequentialMenuItem, _config.PlaybackOrder == "顺序播放", "顺序播放");
		SetMenuItemCheckedState(loopMenuItem, _config.PlaybackOrder == "列表循环", "列表循环");
		SetMenuItemCheckedState(loopOneMenuItem, _config.PlaybackOrder == "单曲循环", "单曲循环");
		SetMenuItemCheckedState(randomMenuItem, _config.PlaybackOrder == "随机播放", "随机播放");
	}

	private void UpdateQualityMenuCheck()
	{
		string defaultQuality = _config.DefaultQuality;
		SetMenuItemCheckedState(standardQualityMenuItem, defaultQuality == "标准音质", "标准音质");
		SetMenuItemCheckedState(highQualityMenuItem, defaultQuality == "极高音质", "极高音质");
		SetMenuItemCheckedState(losslessQualityMenuItem, defaultQuality == "无损音质", "无损音质");
		SetMenuItemCheckedState(hiresQualityMenuItem, defaultQuality == "Hi-Res音质", "Hi-Res音质");
		SetMenuItemCheckedState(surroundHDQualityMenuItem, defaultQuality == "高清环绕声", "高清环绕声");
		SetMenuItemCheckedState(dolbyQualityMenuItem, defaultQuality == "沉浸环绕声", "沉浸环绕声");
		SetMenuItemCheckedState(masterQualityMenuItem, defaultQuality == "超清母带", "超清母带");
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
	}

	private void qualityMenuItem_Click(object sender, EventArgs e)
	{
		if (sender is ToolStripMenuItem { Text: var text } && !(_config.DefaultQuality == text))
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
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string text = Path.Combine(baseDirectory, "YTPlayer.Updater.exe");
            if (!File.Exists(text))
            {
				MessageBox.Show(this, "未找到更新程序 YTPlayer.Updater.exe，请重新安装或修复。", "更新失败", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
				return false;
			}
            string text2 = CreateUpdateSessionDirectory();
            string text3 = Path.Combine(text2, Path.GetFileName(text));
            File.Copy(text, text3, overwrite: true);
            CopyUpdaterDependency(Path.Combine(baseDirectory, "YTPlayer.Updater.exe.config"), text2);
            string sessionLibs = Path.Combine(text2, "libs");
            CopyUpdaterDependency("Newtonsoft.Json.dll", sessionLibs);
            CopyUpdaterDependency("Newtonsoft.Json.xml", sessionLibs);
            CopyUpdaterLibDirectory(sessionLibs);
            string text4 = Path.Combine(text2, "update-plan.json");
            plan.SaveTo(text4);
            string text5 = SerializeCommandLineArguments();
            StringBuilder stringBuilder = new StringBuilder();
            stringBuilder.Append("--plan \"" + text4 + "\" ");
			stringBuilder.Append("--target \"" + baseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + "\" ");
			stringBuilder.Append("--main \"" + Application.ExecutablePath + "\" ");
			stringBuilder.Append($"--pid {Process.GetCurrentProcess().Id} ");
			if (!string.IsNullOrEmpty(text5))
			{
				stringBuilder.Append("--main-args \"" + text5 + "\" ");
			}
			ProcessStartInfo startInfo = new ProcessStartInfo
			{
				FileName = text3,
				Arguments = stringBuilder.ToString(),
				UseShellExecute = false,
				WorkingDirectory = text2
			};
			Process process = Process.Start(startInfo);
			if (process == null)
			{
				throw new InvalidOperationException("无法启动更新程序。");
			}
			_isApplicationExitRequested = true;
			string planVersionLabel = GetPlanVersionLabel(plan);
			UpdateStatusBar("正在准备更新至 " + planVersionLabel);
			Task.Run(delegate
			{
				Thread.Sleep(300);
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

	private static string CreateUpdateSessionDirectory()
	{
		string path = Path.Combine(Path.GetTempPath(), "YTPlayerUpdater");
		string text = Path.Combine(path, $"{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}");
		Directory.CreateDirectory(text);
		return text;
	}

    private static void CopyUpdaterDependency(string sourceFileOrName, string destinationDirectory)
    {
        string resolvedSource = sourceFileOrName;
        if (!File.Exists(resolvedSource))
        {
            // 允许直接传入文件名，优先尝试 libs 目录
            resolvedSource = PathHelper.ResolveFromLibsOrBase(Path.GetFileName(sourceFileOrName));
        }

        if (!File.Exists(resolvedSource))
        {
            Debug.WriteLine($"[Updater] 依赖未找到: {sourceFileOrName}");
            return;
        }

        Directory.CreateDirectory(destinationDirectory);
        string destFileName = Path.Combine(destinationDirectory, Path.GetFileName(resolvedSource));
        File.Copy(resolvedSource, destFileName, overwrite: true);
    }

    /// <summary>
    /// 将当前安装目录下的 libs 目录完整复制到临时更新会话，保证更新器在独立工作目录下也能解析所有依赖。
    /// </summary>
    private static void CopyUpdaterLibDirectory(string destinationLibs)
    {
        try
        {
            string sourceLibs = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "libs");
            if (!Directory.Exists(sourceLibs))
            {
                return;
            }

            foreach (string sourcePath in Directory.GetFiles(sourceLibs, "*.*", SearchOption.AllDirectories))
            {
                string relative = sourcePath.Substring(sourceLibs.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string destPath = Path.Combine(destinationLibs, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                File.Copy(sourcePath, destPath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Updater] 复制 libs 目录失败: {ex.Message}");
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
				DebugLogger.LogException("Update", ex3, "自动检查更新失败（忽略）");
			}
		}, token);
	}

    private async Task CheckForUpdatesSilentlyAsync(CancellationToken cancellationToken)
    {
        using UpdateServiceClient client = new UpdateServiceClient(UpdateConstants.DefaultEndpoint, "YTPlayer", VersionInfo.Version);
        UpdateCheckResult result = await PollUpdateStatusSilentlyAsync(client, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        UpdateAsset asset = UpdateFormatting.SelectPreferredAsset(result.Response.Data?.Assets);
        if ((result.Response.Data?.UpdateAvailable ?? false) && asset != null)
        {
            UpdatePlan plan = UpdatePlan.FromResponse(result.Response, asset, VersionInfo.Version);
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

    private static async Task<UpdateCheckResult> PollUpdateStatusSilentlyAsync(UpdateServiceClient client, CancellationToken cancellationToken)
    {
        UpdateCheckResult result;
        while (true)
        {
            result = await client.CheckForUpdatesAsync(VersionInfo.Version, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
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

	private void ToggleAutoReadLyrics()
	{
		_autoReadLyrics = !_autoReadLyrics;
		if (!_autoReadLyrics)
		{
			CancelPendingLyricSpeech();
		}
		try
		{
			autoReadLyricsMenuItem.Checked = _autoReadLyrics;
			autoReadLyricsMenuItem.Text = (_autoReadLyrics ? "关闭歌词朗读\tF11" : "打开歌词朗读\tF11");
		}
		catch
		{
		}
		string message = (_autoReadLyrics ? "已开启歌词朗读" : "已关闭歌词朗读");
		TtsHelper.SpeakText(message);
		UpdateStatusBar(message);
		Debug.WriteLine("[TTS] 歌词朗读: " + (_autoReadLyrics ? "开启" : "关闭"));
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
		foreach (SongInfo song in skeletonSongs)
		{
			if (string.IsNullOrWhiteSpace(song.ViewSource))
			{
				song.ViewSource = viewSource;
			}
		}
		string statusText = ((skeletonSongs.Count != 0) ? $"\ufffd赥: {playlistName}\ufffd\ufffd\ufffd\ufffd {skeletonSongs.Count} \ufffd\u05f8\ufffd\ufffd\ufffd (\ufffdǼ\ufffd)" : ((playlist.TrackCount > 0) ? $"\ufffd赥: {playlistName}\ufffd\ufffd\ufffd\ufffd {playlist.TrackCount} \ufffd\u05f8\ufffd\ufffd\ufffd (\ufffdǼ\ufffd, \ufffd\ufffdȱ\ufffd\ufffd\ufffd\ufffd)" : ("\ufffd赥: " + playlistName + "\ufffd\ufffdû\ufffd\ufffd\ufffdκν\ufffd\ufffd\ufffd")));
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
		List<SongInfo> resolvedSongs = (await FetchWithRetryUntilCancel<List<SongInfo>>(operationName: string.IsNullOrWhiteSpace(operationName) ? ("playlist:" + playlistId) : operationName, operation: (CancellationToken ct) => _apiClient.GetPlaylistSongsAsync(playlistId), cancellationToken: cancellationToken, onRetry: delegate(int attempt, Exception _)
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
		List<SongInfo> resolvedSongs = (await FetchWithRetryUntilCancel<List<SongInfo>>(operationName: string.IsNullOrWhiteSpace(operationName) ? ("album:" + albumId) : operationName, operation: (CancellationToken ct) => _apiClient.GetAlbumSongsAsync(albumId), cancellationToken: cancellationToken, onRetry: delegate(int attempt, Exception _)
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
		string arg = (enriched ? "（已丰富）" : string.Empty);
		return (songCount <= 0) ? (text + " 暂无歌曲") : $"歌单: {text} · {songCount} 首{arg}";
	}

	private static string BuildAlbumStatusText(AlbumInfo album, int songCount, bool enriched)
	{
		string text = (string.IsNullOrWhiteSpace(album?.Name) ? ("专辑 " + album?.Id) : album.Name);
		string arg = (enriched ? "（已丰富）" : string.Empty);
		return (songCount <= 0) ? (text + " 暂无歌曲") : $"专辑: {text} · {songCount} 首{arg}";
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

	private async Task EnrichCurrentPlaylistSongsAsync(PlaylistInfo playlist, List<SongInfo> skeleton, string viewSource, string accessibleName)
	{
		CancellationToken viewToken = GetCurrentViewContentToken();
		try
		{
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
					int pendingFocusIndex = (resultListView.SelectedIndices.Count > 0) ? resultListView.SelectedIndices[0] : -1;
					bool sameIds = _currentSongs != null && _currentSongs.Count == merged.Count;
					if (sameIds)
					{
						for (int i = 0; i < merged.Count; i++)
						{
							string? a = _currentSongs[i]?.Id;
							string? b = merged[i]?.Id;
							if (!string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
							{
								sameIds = false;
								break;
							}
						}
					}
					if (sameIds)
					{
						// 局部刷新，保留选择和滚动位置
						PatchSongs(merged, 1, skipAvailabilityCheck: true, showPagination: false, hasPreviousPage: false, hasNextPage: false, pendingFocusIndex: pendingFocusIndex, allowSelection: true);
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
			Exception ex2 = ex;
			if (!TryHandleOperationCancelled(ex2, "打开歌单已取消"))
			{
				Debug.WriteLine($"[EnrichPlaylist] 丰富歌单失败: {ex2}");
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
					DisplaySongs(merged, showPagination: false, hasNextPage: false, 1, preserveSelection: false, viewSource, accessibleName, skipAvailabilityCheck: true);
					UpdateStatusBar(BuildAlbumStatusText(album, merged.Count, enriched: true));
					ScheduleAvailabilityCheck(merged);
				}
			}).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			if (!TryHandleOperationCancelled(ex2, "打开专辑已取消"))
			{
				Debug.WriteLine($"[EnrichAlbum] 丰富专辑失败: {ex2}");
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
				(_currentPlaylist, _, _) = viewData;
				_currentPlaylistOwnedByUser = IsPlaylistOwnedByUser(_currentPlaylist, GetCurrentUserId());
				DisplaySongs(viewData.Songs, showPagination: false, hasNextPage: false, 1, preserveSelection, viewSource, accessibleName, skipAvailabilityCheck: true);
				UpdateStatusBar(viewData.StatusText);
				EnrichCurrentPlaylistSongsAsync(viewData.Playlist, viewData.Songs, viewSource, accessibleName);
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			if (!TryHandleOperationCancelled(ex2, "打开歌单已取消"))
			{
				Debug.WriteLine($"[MainForm] 打开歌单失败: {ex2}");
				MessageBox.Show("打开歌单失败: " + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
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
				DisplaySongs(viewData.Songs, showPagination: false, hasNextPage: false, 1, preserveSelection: false, viewSource, accessibleName, skipAvailabilityCheck: true);
				UpdateStatusBar(viewData.StatusText);
				EnrichCurrentAlbumSongsAsync(viewData.Album, viewData.Songs, viewSource, accessibleName);
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			if (!TryHandleOperationCancelled(ex2, "打开专辑已取消"))
			{
				Debug.WriteLine($"[MainForm] 打开专辑失败: {ex2}");
				MessageBox.Show("打开专辑失败: " + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
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
				DisplaySongs(viewData.Songs, showPagination: false, hasNextPage: false, 1, preserveSelection: false, viewSource, viewData.Playlist.Name ?? ("歌单 " + playlistId), skipAvailabilityCheck: true);
				UpdateStatusBar(viewData.StatusText);
				EnrichCurrentPlaylistSongsAsync(viewData.Playlist, viewData.Songs, viewSource, viewData.Playlist.Name ?? ("歌单 " + playlistId));
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			if (!TryHandleOperationCancelled(ex2, "加载歌单已取消"))
			{
				Debug.WriteLine($"[LoadPlaylistById] 异常: {ex2}");
				MessageBox.Show("加载歌单失败: " + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
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
				DisplaySongs(viewData.Songs, showPagination: false, hasNextPage: false, 1, preserveSelection: false, viewSource, viewData.Album.Name ?? ("专辑 " + albumId), skipAvailabilityCheck: true);
				UpdateStatusBar(viewData.StatusText);
				EnrichCurrentAlbumSongsAsync(viewData.Album, viewData.Songs, viewSource, viewData.Album.Name ?? ("专辑 " + albumId));
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			if (!TryHandleOperationCancelled(ex2, "打开专辑已取消"))
			{
				Debug.WriteLine($"[LoadAlbumById] 异常: {ex2}");
				MessageBox.Show("打开专辑失败: " + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
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
			if (!TryHandleOperationCancelled(ex3, "搜索加载已取消"))
			{
				Debug.WriteLine($"[LoadSearchResults] 异常: {ex3}");
				MessageBox.Show("加载搜索结果失败: " + ex3.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
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

	private void SaveNavigationState()
	{
		if (_initialHomeLoadCts != null)
		{
			StopInitialHomeLoadLoop("保存导航状态前中断主页加载");
		}
		if (_currentSongs.Count == 0 && _currentPlaylists.Count == 0 && _currentAlbums.Count == 0 && _currentListItems.Count == 0 && _currentArtists.Count == 0 && _currentPodcasts.Count == 0 && _currentPodcastSounds.Count == 0)
		{
			return;
		}
		NavigationHistoryItem navigationHistoryItem = CreateCurrentState();
		if (_navigationHistory.Count > 0)
		{
			NavigationHistoryItem a = _navigationHistory.Peek();
			if (IsSameNavigationState(a, navigationHistoryItem))
			{
				_navigationHistory.Pop();
				_navigationHistory.Push(navigationHistoryItem);
				Debug.WriteLine($"[Navigation] 合并重复状态: {navigationHistoryItem.ViewName}, 类型={navigationHistoryItem.PageType}, 历史栈深度={_navigationHistory.Count}");
				return;
			}
		}
		_navigationHistory.Push(navigationHistoryItem);
		Debug.WriteLine($"[Navigation] 保存状态: {navigationHistoryItem.ViewName}, 类型={navigationHistoryItem.PageType}, 历史栈深度={_navigationHistory.Count}");
	}

	private NavigationHistoryItem CreateCurrentState()
	{
		NavigationHistoryItem navigationHistoryItem = new NavigationHistoryItem
		{
			ViewSource = _currentViewSource,
			ViewName = resultListView.AccessibleName,
			SelectedIndex = ((resultListView.SelectedItems.Count > 0) ? resultListView.SelectedItems[0].Index : (-1)),
			SelectedDataIndex = -1
		};
		if (resultListView.SelectedItems.Count > 0 && resultListView.SelectedItems[0].Tag is int num && num >= 0)
		{
			navigationHistoryItem.SelectedDataIndex = num;
		}
		if (_isHomePage || string.Equals(_currentViewSource, "homepage", StringComparison.OrdinalIgnoreCase))
		{
			navigationHistoryItem.PageType = "homepage";
		}
		else if (_currentViewSource.StartsWith("playlist:", StringComparison.OrdinalIgnoreCase))
		{
			navigationHistoryItem.PageType = "playlist";
			navigationHistoryItem.PlaylistId = _currentViewSource.Substring("playlist:".Length);
		}
		else if (_currentViewSource.StartsWith("album:", StringComparison.OrdinalIgnoreCase))
		{
			navigationHistoryItem.PageType = "album";
			navigationHistoryItem.AlbumId = _currentViewSource.Substring("album:".Length);
		}
		else if (_currentViewSource.StartsWith("search:", StringComparison.OrdinalIgnoreCase))
		{
			navigationHistoryItem.PageType = "search";
			ParseSearchViewSource(_currentViewSource, out string searchType, out string keyword, out int page);
			navigationHistoryItem.SearchType = ((!string.IsNullOrWhiteSpace(searchType)) ? searchType : _currentSearchType);
			navigationHistoryItem.SearchKeyword = ((!string.IsNullOrWhiteSpace(keyword)) ? keyword : _lastKeyword);
			navigationHistoryItem.CurrentPage = ((page > 0) ? page : _currentPage);
		}
		else if (_currentViewSource.StartsWith("artist_entries:", StringComparison.OrdinalIgnoreCase))
		{
			navigationHistoryItem.PageType = "artist_entries";
			navigationHistoryItem.ArtistId = ParseArtistIdFromViewSource(_currentViewSource, "artist_entries:");
			navigationHistoryItem.ArtistName = _currentArtist?.Name ?? _currentArtistDetail?.Name ?? string.Empty;
		}
		else if (_currentViewSource.StartsWith("artist_songs_top:", StringComparison.OrdinalIgnoreCase))
		{
			navigationHistoryItem.PageType = "artist_top";
			navigationHistoryItem.ArtistId = ParseArtistIdFromViewSource(_currentViewSource, "artist_songs_top:");
			navigationHistoryItem.ArtistName = _currentArtist?.Name ?? _currentArtistDetail?.Name ?? string.Empty;
		}
		else if (_currentViewSource.StartsWith("artist_songs:", StringComparison.OrdinalIgnoreCase))
		{
			navigationHistoryItem.PageType = "artist_songs";
			ParseArtistListViewSource(_currentViewSource, out long artistId, out int offset, out string order);
			navigationHistoryItem.ArtistId = artistId;
			navigationHistoryItem.ArtistOffset = offset;
			navigationHistoryItem.ArtistOrder = order;
			navigationHistoryItem.ArtistName = _currentArtist?.Name ?? _currentArtistDetail?.Name ?? string.Empty;
		}
		else if (_currentViewSource.StartsWith("artist_albums:", StringComparison.OrdinalIgnoreCase))
		{
			navigationHistoryItem.PageType = "artist_albums";
			ParseArtistListViewSource(_currentViewSource, out long artistId2, out int offset2, out string order2, "latest");
			navigationHistoryItem.ArtistId = artistId2;
			navigationHistoryItem.ArtistOffset = offset2;
			navigationHistoryItem.ArtistAlbumSort = order2;
			navigationHistoryItem.ArtistName = _currentArtist?.Name ?? _currentArtistDetail?.Name ?? string.Empty;
		}
		else if (string.Equals(_currentViewSource, "artist_favorites", StringComparison.OrdinalIgnoreCase))
		{
			navigationHistoryItem.PageType = "artist_favorites";
		}
		else if (string.Equals(_currentViewSource, "artist_category_types", StringComparison.OrdinalIgnoreCase))
		{
			navigationHistoryItem.PageType = "artist_category_types";
		}
		else if (_currentViewSource.StartsWith("artist_category_type:", StringComparison.OrdinalIgnoreCase))
		{
			navigationHistoryItem.PageType = "artist_category_type";
			navigationHistoryItem.ArtistType = checked((int)ParseArtistIdFromViewSource(_currentViewSource, "artist_category_type:"));
		}
		else if (_currentViewSource.StartsWith("artist_category_list:", StringComparison.OrdinalIgnoreCase))
		{
			navigationHistoryItem.PageType = "artist_category_list";
			ParseArtistCategoryListViewSource(_currentViewSource, out var typeCode, out var areaCode, out var offset3);
			navigationHistoryItem.ArtistType = typeCode;
			navigationHistoryItem.ArtistArea = areaCode;
			navigationHistoryItem.ArtistOffset = offset3;
		}
		else if (_currentViewSource.StartsWith("podcast:", StringComparison.OrdinalIgnoreCase))
		{
			navigationHistoryItem.PageType = "podcast";
			ParsePodcastViewSource(_currentViewSource, out var radioId, out var offset4, out var ascending);
			navigationHistoryItem.PodcastRadioId = radioId;
			navigationHistoryItem.PodcastOffset = offset4;
			navigationHistoryItem.PodcastRadioName = _currentPodcast?.Name ?? string.Empty;
			navigationHistoryItem.PodcastAscending = ascending;
		}
		else if (_currentViewSource.StartsWith("url:mixed", StringComparison.OrdinalIgnoreCase))
		{
			navigationHistoryItem.PageType = "url_mixed";
			navigationHistoryItem.MixedQueryKey = _currentMixedQueryKey ?? string.Empty;
		}
		else if (_currentViewSource.StartsWith("url:song:", StringComparison.OrdinalIgnoreCase))
		{
			navigationHistoryItem.PageType = "url_song";
			navigationHistoryItem.SongId = _currentViewSource.Substring("url:song:".Length);
		}
		else
		{
			navigationHistoryItem.PageType = "category";
			navigationHistoryItem.CategoryId = _currentViewSource;
		}
		return navigationHistoryItem;
	}

	private static void ParseSearchViewSource(string? viewSource, out string searchType, out string keyword, out int page)
	{
		searchType = string.Empty;
		keyword = string.Empty;
		page = 1;
		if (string.IsNullOrWhiteSpace(viewSource))
		{
			return;
		}
		if (!viewSource.StartsWith("search:", StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		string text = viewSource.Substring("search:".Length);
		string text2 = null;
		if (text.StartsWith("artist:", StringComparison.OrdinalIgnoreCase))
		{
			text2 = "artist";
			text = text.Substring("artist:".Length);
		}
		else if (text.StartsWith("album:", StringComparison.OrdinalIgnoreCase))
		{
			text2 = "album";
			text = text.Substring("album:".Length);
		}
		else if (text.StartsWith("playlist:", StringComparison.OrdinalIgnoreCase))
		{
			text2 = "playlist";
			text = text.Substring("playlist:".Length);
		}
		else if (text.StartsWith("podcast:", StringComparison.OrdinalIgnoreCase))
		{
			text2 = "podcast";
			text = text.Substring("podcast:".Length);
		}
		if (1 == 0)
		{
		}
		string text3 = text2 switch
		{
			"artist" => "歌手", 
			"album" => "专辑", 
			"playlist" => "歌单", 
			"podcast" => "播客", 
			_ => "歌曲", 
		};
		if (1 == 0)
		{
		}
		searchType = text3;
		int num = text.LastIndexOf(":page", StringComparison.OrdinalIgnoreCase);
		checked
		{
			if (num >= 0 && num + 5 < text.Length)
			{
				string s = text.Substring(num + 5);
				if (int.TryParse(s, out var result) && result > 0)
				{
					page = result;
					text = text.Substring(0, num);
				}
			}
			keyword = (string.IsNullOrWhiteSpace(text) ? string.Empty : text);
		}
	}

	private static bool IsSameNavigationState(NavigationHistoryItem a, NavigationHistoryItem b)
	{
		if (a == null || b == null)
		{
			return false;
		}
		if (!string.Equals(a.PageType, b.PageType, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		switch (a.PageType)
		{
		case "homepage":
			return true;
		case "category":
			return string.Equals(a.CategoryId, b.CategoryId, StringComparison.OrdinalIgnoreCase);
		case "playlist":
			return string.Equals(a.PlaylistId, b.PlaylistId, StringComparison.OrdinalIgnoreCase);
		case "album":
			return string.Equals(a.AlbumId, b.AlbumId, StringComparison.OrdinalIgnoreCase);
		case "search":
			return string.Equals(a.SearchKeyword, b.SearchKeyword, StringComparison.OrdinalIgnoreCase) && string.Equals(a.SearchType, b.SearchType, StringComparison.OrdinalIgnoreCase) && a.CurrentPage == b.CurrentPage;
		case "artist_entries":
		case "artist_top":
			return a.ArtistId == b.ArtistId;
		case "artist_songs":
			return a.ArtistId == b.ArtistId && a.ArtistOffset == b.ArtistOffset && string.Equals(a.ArtistOrder, b.ArtistOrder, StringComparison.OrdinalIgnoreCase);
		case "artist_albums":
			return a.ArtistId == b.ArtistId && a.ArtistOffset == b.ArtistOffset && string.Equals(a.ArtistAlbumSort, b.ArtistAlbumSort, StringComparison.OrdinalIgnoreCase);
		case "artist_favorites":
		case "artist_category_types":
			return true;
		case "artist_category_type":
			return a.ArtistType == b.ArtistType;
		case "artist_category_list":
			return a.ArtistType == b.ArtistType && a.ArtistArea == b.ArtistArea && a.ArtistOffset == b.ArtistOffset;
		case "podcast":
			return a.PodcastRadioId == b.PodcastRadioId && a.PodcastOffset == b.PodcastOffset && a.PodcastAscending == b.PodcastAscending;
		case "url_song":
			return string.Equals(a.SongId, b.SongId, StringComparison.OrdinalIgnoreCase);
		case "url_mixed":
			return string.Equals(a.MixedQueryKey, b.MixedQueryKey, StringComparison.OrdinalIgnoreCase);
		default:
			return string.Equals(a.ViewSource, b.ViewSource, StringComparison.OrdinalIgnoreCase);
		}
	}

	private async Task GoBackAsync()
	{
		DateTime now = DateTime.UtcNow;
		double elapsed = (now - _lastBackTime).TotalMilliseconds;
		if (elapsed < 300.0)
		{
			Debug.WriteLine($"[Navigation] \ud83d\uded1 防抖拦截：距上次后退仅 {elapsed:F0}ms");
			return;
		}
		if (_isNavigating)
		{
			Debug.WriteLine("[Navigation] \ud83d\uded1 并发拦截：已有导航操作正在执行");
			return;
		}
		try
		{
			_isNavigating = true;
			_lastBackTime = now;
			if (_navigationHistory.Count == 0)
			{
				Debug.WriteLine("[Navigation] 导航历史为空，返回主页");
				if (!_isHomePage)
				{
					await LoadHomePageAsync();
				}
				else
				{
					UpdateStatusBar("已经在主页了");
				}
				return;
			}
			NavigationHistoryItem state = _navigationHistory.Peek();
			Debug.WriteLine($"[Navigation] 尝试后退到: {state.ViewName}, 类型={state.PageType}, 当前历史={_navigationHistory.Count}");
			if (await RestoreNavigationStateAsync(state))
			{
				_navigationHistory.Pop();
				Debug.WriteLine($"[Navigation] 后退成功: {state.ViewName}, 剩余历史={_navigationHistory.Count}");
			}
			else
			{
				UpdateStatusBar("返回失败，已保持当前页面");
			}
		}
		finally
		{
			_isNavigating = false;
		}
	}

	private async Task<bool> RestoreNavigationStateAsync(NavigationHistoryItem state)
	{
		string previousViewSource = _currentViewSource ?? string.Empty;
		int previousAutoFocusDepth = _autoFocusSuppressionDepth;
		checked
		{
			_autoFocusSuppressionDepth++;
			bool handledByView = false;
			try
			{
				switch (state.PageType)
				{
				case "homepage":
					await LoadHomePageAsync(skipSave: true);
					break;
				case "category":
					await LoadCategoryContent(state.CategoryId, skipSave: true);
					break;
				case "playlist":
					await LoadPlaylistById(state.PlaylistId, skipSave: true);
					break;
				case "album":
					await LoadAlbumById(state.AlbumId, skipSave: true);
					break;
				case "url_song":
					if (!(await LoadSongFromUrlAsync(state.SongId, skipSave: true)))
					{
						return false;
					}
					break;
				case "url_mixed":
					if (!(await RestoreMixedUrlStateAsync(state.MixedQueryKey)))
					{
						return false;
					}
					break;
				case "search":
					await LoadSearchResults(state.SearchKeyword, state.SearchType, state.CurrentPage, skipSave: true, (state.SelectedDataIndex >= 0) ? state.SelectedDataIndex : state.SelectedIndex);
					handledByView = true;
					break;
				case "artist_entries":
					if (state.ArtistId > 0)
					{
						ArtistInfo artistInfo = new ArtistInfo
						{
							Id = state.ArtistId,
							Name = state.ArtistName
						};
						await OpenArtistAsync(artistInfo, skipSave: true);
					}
					else
					{
						await LoadArtistCategoryTypesAsync(skipSave: true);
					}
					break;
				case "artist_top":
					if (state.ArtistId > 0)
					{
						await LoadArtistTopSongsAsync(state.ArtistId, skipSave: true);
					}
					break;
				case "artist_songs":
					if (state.ArtistId > 0)
					{
						await LoadArtistSongsAsync(orderOverride: ResolveArtistSongsOrder(state.ArtistOrder), artistId: state.ArtistId, offset: state.ArtistOffset, skipSave: true);
					}
					break;
				case "artist_albums":
					if (state.ArtistId > 0)
					{
						await LoadArtistAlbumsAsync(sortOverride: ResolveArtistAlbumSort(state.ArtistAlbumSort), artistId: state.ArtistId, offset: state.ArtistOffset, skipSave: true);
					}
					break;
				case "artist_favorites":
					await LoadArtistFavoritesAsync(skipSave: true);
					break;
				case "artist_category_types":
					await LoadArtistCategoryTypesAsync(skipSave: true);
					break;
				case "artist_category_type":
					await LoadArtistCategoryAreasAsync(state.ArtistType, skipSave: true);
					break;
				case "artist_category_list":
					await LoadArtistsByCategoryAsync(state.ArtistType, state.ArtistArea, state.ArtistOffset, skipSave: true);
					break;
				case "podcast":
					if (state.PodcastRadioId <= 0)
					{
						return false;
					}
					await LoadPodcastEpisodesAsync(state.PodcastRadioId, state.PodcastOffset, skipSave: true, null, state.PodcastAscending);
					break;
				default:
					Debug.WriteLine("[Navigation] 未知的页面类型: " + state.PageType);
					UpdateStatusBar("无法恢复页面");
					return false;
				}
				if (!IsNavigationStateApplied(state))
				{
					Debug.WriteLine("[Navigation] 页面状态未切换，当前 view=" + _currentViewSource + ", 期望=" + state.ViewSource);
					return false;
				}
				if (handledByView)
				{
					UpdateStatusBar("返回到: " + state.ViewName);
					return true;
				}
				int resolvedIndex = -1;
				if (state.SelectedIndex >= 0 && state.SelectedIndex < resultListView.Items.Count)
				{
					resolvedIndex = state.SelectedIndex;
				}
				else if (resultListView.Items.Count > 0)
				{
					resolvedIndex = Math.Min(Math.Max(state.SelectedIndex, 0), resultListView.Items.Count - 1);
				}
				if (resolvedIndex >= 0 && resolvedIndex < resultListView.Items.Count)
				{
					resultListView.BeginUpdate();
					resultListView.SelectedItems.Clear();
					ListViewItem targetItem = resultListView.Items[resolvedIndex];
					targetItem.Selected = true;
					targetItem.Focused = true;
					targetItem.EnsureVisible();
					resultListView.EndUpdate();
					resultListView.Focus();
					_lastListViewFocusedIndex = resolvedIndex;
				}
				else
				{
					resultListView.Focus();
				}
				UpdateStatusBar("返回到: " + state.ViewName);
				return true;
			}
			catch (Exception ex)
			{
				Exception ex2 = ex;
				Debug.WriteLine($"[Navigation] 恢复状态失败: {ex2}");
				MessageBox.Show("返回失败: " + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("返回失败");
				_currentViewSource = previousViewSource;
				return false;
			}
			finally
			{
				_autoFocusSuppressionDepth = previousAutoFocusDepth;
			}
		}
	}

	private bool IsNavigationStateApplied(NavigationHistoryItem state)
	{
		if (state == null)
		{
			return false;
		}
		if (!string.IsNullOrWhiteSpace(state.ViewSource) && string.Equals(_currentViewSource, state.ViewSource, StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		switch (state.PageType)
		{
		case "homepage":
			return _isHomePage || string.Equals(_currentViewSource, "homepage", StringComparison.OrdinalIgnoreCase);
		case "category":
			return string.Equals(_currentViewSource, state.CategoryId, StringComparison.OrdinalIgnoreCase);
		case "playlist":
			return string.Equals(_currentViewSource, "playlist:" + state.PlaylistId, StringComparison.OrdinalIgnoreCase);
		case "album":
			return string.Equals(_currentViewSource, "album:" + state.AlbumId, StringComparison.OrdinalIgnoreCase);
		case "artist_entries":
		case "artist_top":
			return state.ArtistId > 0 && (_currentViewSource ?? string.Empty).IndexOf(state.ArtistId.ToString(), StringComparison.OrdinalIgnoreCase) >= 0;
		case "artist_songs":
		{
			if (state.ArtistId <= 0)
			{
				return false;
			}
			string b = $"artist_songs:{state.ArtistId}:order{state.ArtistOrder}:offset{state.ArtistOffset}";
			if (string.Equals(_currentViewSource, b, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
			return string.Equals(_currentViewSource, $"artist_songs:{state.ArtistId}:offset{state.ArtistOffset}", StringComparison.OrdinalIgnoreCase);
		}
		case "artist_albums":
		{
			if (state.ArtistId <= 0)
			{
				return false;
			}
			string b2 = $"artist_albums:{state.ArtistId}:order{state.ArtistAlbumSort}:offset{state.ArtistOffset}";
			if (string.Equals(_currentViewSource, b2, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
			return string.Equals(_currentViewSource, $"artist_albums:{state.ArtistId}:offset{state.ArtistOffset}", StringComparison.OrdinalIgnoreCase);
		}
		case "artist_favorites":
			return string.Equals(_currentViewSource, "artist_favorites", StringComparison.OrdinalIgnoreCase);
		case "artist_category_types":
			return string.Equals(_currentViewSource, "artist_category_types", StringComparison.OrdinalIgnoreCase);
		case "artist_category_type":
			return string.Equals(_currentViewSource, $"artist_category_type:{state.ArtistType}", StringComparison.OrdinalIgnoreCase);
		case "artist_category_list":
			return string.Equals(_currentViewSource, $"artist_category_list:{state.ArtistType}:{state.ArtistArea}:offset{state.ArtistOffset}", StringComparison.OrdinalIgnoreCase);
		case "podcast":
		{
			ParsePodcastViewSource(_currentViewSource, out var radioId, out var offset, out var ascending);
			return radioId == state.PodcastRadioId && offset == state.PodcastOffset && ascending == state.PodcastAscending;
		}
		case "url_song":
			return string.Equals(_currentViewSource, "url:song:" + state.SongId, StringComparison.OrdinalIgnoreCase);
		case "url_mixed":
			return string.Equals(_currentMixedQueryKey, state.MixedQueryKey, StringComparison.OrdinalIgnoreCase);
		default:
			return string.Equals(_currentViewSource, state.ViewSource, StringComparison.OrdinalIgnoreCase);
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
					goto IL_038b;
				}
			}
		}
		showViewSection = true;
		goto IL_038b;
		IL_038b:
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
		if (!string.IsNullOrWhiteSpace(_currentViewSource))
		{
			return _currentViewSource;
		}
		return null;
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
			else if (viewSource.StartsWith("artist_entries:", StringComparison.OrdinalIgnoreCase))
			{
				long artistId = ParseArtistIdFromViewSource(viewSource, "artist_entries:");
				if (artistId <= 0)
				{
					await LoadArtistCategoryTypesAsync(skipSave: true);
				}
				else
				{
					await OpenArtistAsync(new ArtistInfo
					{
						Id = artistId,
						Name = (currentSong.Artist ?? string.Empty)
					}, skipSave: true);
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
				ParseArtistListViewSource(viewSource, out long artistId3, out int offset, out string order);
				await LoadArtistSongsAsync(artistId3, offset, skipSave: true, ResolveArtistSongsOrder(order));
			}
			else if (viewSource.StartsWith("artist_albums:", StringComparison.OrdinalIgnoreCase))
			{
				ParseArtistListViewSource(viewSource, out long artistId4, out int offset2, out string order2, "latest");
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
				ParseSearchViewSource(viewSource, out string searchType, out string keyword, out int page);
				if (string.IsNullOrWhiteSpace(keyword))
				{
					keyword = _lastKeyword;
				}
				int targetPage = ((page > 0) ? page : _currentPage);
				await LoadSearchResults(keyword, searchType, (targetPage <= 0) ? 1 : targetPage, skipSave: true);
			}
			else if (string.Equals(viewSource, "recent_play", StringComparison.OrdinalIgnoreCase) || string.Equals(viewSource, "recent_playlists", StringComparison.OrdinalIgnoreCase) || string.Equals(viewSource, "recent_albums", StringComparison.OrdinalIgnoreCase) || string.Equals(viewSource, "recent_listened", StringComparison.OrdinalIgnoreCase) || string.Equals(viewSource, "recent_podcasts", StringComparison.OrdinalIgnoreCase) || string.Equals(viewSource, "daily_recommend", StringComparison.OrdinalIgnoreCase) || string.Equals(viewSource, "personalized", StringComparison.OrdinalIgnoreCase) || string.Equals(viewSource, "toplist", StringComparison.OrdinalIgnoreCase) || string.Equals(viewSource, "daily_recommend_songs", StringComparison.OrdinalIgnoreCase) || string.Equals(viewSource, "daily_recommend_playlists", StringComparison.OrdinalIgnoreCase) || string.Equals(viewSource, "personalized_playlists", StringComparison.OrdinalIgnoreCase) || string.Equals(viewSource, "personalized_newsongs", StringComparison.OrdinalIgnoreCase) || string.Equals(viewSource, "highquality_playlists", StringComparison.OrdinalIgnoreCase) || viewSource.StartsWith("playlist_cat_", StringComparison.OrdinalIgnoreCase) || string.Equals(viewSource, "playlist_category", StringComparison.OrdinalIgnoreCase) || string.Equals(viewSource, "new_songs", StringComparison.OrdinalIgnoreCase) || string.Equals(viewSource, "new_songs_all", StringComparison.OrdinalIgnoreCase) || string.Equals(viewSource, "new_songs_chinese", StringComparison.OrdinalIgnoreCase) || string.Equals(viewSource, "new_songs_western", StringComparison.OrdinalIgnoreCase) || string.Equals(viewSource, "new_songs_japan", StringComparison.OrdinalIgnoreCase) || string.Equals(viewSource, "new_songs_korea", StringComparison.OrdinalIgnoreCase) || string.Equals(viewSource, "new_albums", StringComparison.OrdinalIgnoreCase) || string.Equals(viewSource, "user_liked_songs", StringComparison.OrdinalIgnoreCase) || string.Equals(viewSource, "user_playlists", StringComparison.OrdinalIgnoreCase) || string.Equals(viewSource, "user_albums", StringComparison.OrdinalIgnoreCase) || string.Equals(viewSource, "user_podcasts", StringComparison.OrdinalIgnoreCase) || string.Equals(viewSource, "user_cloud", StringComparison.OrdinalIgnoreCase) || string.Equals(viewSource, "artist_favorites", StringComparison.OrdinalIgnoreCase) || string.Equals(viewSource, "artist_categories", StringComparison.OrdinalIgnoreCase) || viewSource.StartsWith("artist_top_", StringComparison.OrdinalIgnoreCase) || viewSource.StartsWith("artist_songs_", StringComparison.OrdinalIgnoreCase) || viewSource.StartsWith("artist_albums_", StringComparison.OrdinalIgnoreCase) || viewSource.StartsWith("artist_type_", StringComparison.OrdinalIgnoreCase) || viewSource.StartsWith("artist_area_", StringComparison.OrdinalIgnoreCase))
			{
				await LoadCategoryContent(viewSource, skipSave: true);
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
		if (song == null || resultListView == null || _currentSongs == null || _currentSongs.Count == 0)
		{
			return;
		}
		int num = _currentSongs.FindIndex((SongInfo s) => string.Equals(s.Id, song.Id, StringComparison.OrdinalIgnoreCase));
		if (num >= 0 && num < resultListView.Items.Count)
		{
			resultListView.BeginUpdate();
			try
			{
				resultListView.SelectedItems.Clear();
				ListViewItem listViewItem = resultListView.Items[num];
				listViewItem.Selected = true;
				listViewItem.Focused = true;
				listViewItem.EnsureVisible();
				_lastListViewFocusedIndex = num;
			}
			finally
			{
				resultListView.EndUpdate();
			}
			if (resultListView.CanFocus && !IsListAutoFocusSuppressed)
			{
				resultListView.Focus();
			}
		}
	}

	private SongInfo? GetSelectedSongFromContextMenu(object? sender = null)
	{
		if (_isCurrentPlayingMenuActive && _currentPlayingMenuSong != null)
		{
			return _currentPlayingMenuSong;
		}
		if (sender is ToolStripItem { Tag: SongInfo tag })
		{
			return tag;
		}
		if (resultListView.SelectedItems.Count == 0)
		{
			return null;
		}
		ListViewItem listViewItem = resultListView.SelectedItems[0];
		if (listViewItem.Tag is int num && num >= 0 && num < _currentSongs.Count)
		{
			return _currentSongs[num];
		}
		if (listViewItem.Tag is SongInfo result)
		{
			return result;
		}
		if (listViewItem.Tag is ListItemInfo listItemInfo)
		{
			if (listItemInfo.Type == ListItemType.Song)
			{
				return listItemInfo.Song;
			}
			if (listItemInfo.Type == ListItemType.PodcastEpisode)
			{
				return listItemInfo.PodcastEpisode?.Song;
			}
		}
		if (listViewItem.Tag is PodcastEpisodeInfo podcastEpisodeInfo)
		{
			return podcastEpisodeInfo.Song;
		}
		return null;
	}

	private void ShowContextSongMissingMessage(string actionDescription)
	{
		string text = (_isCurrentPlayingMenuActive ? "当前没有正在播放的歌曲" : ("请先选择要" + actionDescription + "的歌曲"));
		MessageBox.Show(text, "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
	}

	private PlaylistInfo? GetSelectedPlaylistFromContextMenu(object? sender = null)
	{
		if (sender is ToolStripItem { Tag: PlaylistInfo tag })
		{
			return tag;
		}
		if (resultListView.SelectedItems.Count == 0)
		{
			return null;
		}
		ListViewItem listViewItem = resultListView.SelectedItems[0];
		if (listViewItem.Tag is PlaylistInfo result)
		{
			return result;
		}
		if (listViewItem.Tag is ListItemInfo { Type: ListItemType.Playlist } listItemInfo)
		{
			return listItemInfo.Playlist;
		}
		return null;
	}

	private AlbumInfo? GetSelectedAlbumFromContextMenu(object? sender = null)
	{
		if (sender is ToolStripItem { Tag: AlbumInfo tag })
		{
			return tag;
		}
		if (resultListView.SelectedItems.Count == 0)
		{
			return null;
		}
		ListViewItem listViewItem = resultListView.SelectedItems[0];
		if (listViewItem.Tag is AlbumInfo result)
		{
			return result;
		}
		if (listViewItem.Tag is ListItemInfo { Type: ListItemType.Album } listItemInfo)
		{
			return listItemInfo.Album;
		}
		return null;
	}

	private PodcastRadioInfo? GetSelectedPodcastFromContextMenu(object? sender = null)
	{
		if (sender is ToolStripItem { Tag: PodcastRadioInfo tag })
		{
			return tag;
		}
		if (_isCurrentPlayingMenuActive)
		{
			SongInfo? currentPlayingMenuSong = _currentPlayingMenuSong;
			if (currentPlayingMenuSong != null && currentPlayingMenuSong.IsPodcastEpisode)
			{
				PodcastRadioInfo podcastRadioInfo = ResolvePodcastFromSong(_currentPlayingMenuSong);
				if (podcastRadioInfo != null)
				{
					return podcastRadioInfo;
				}
			}
		}
		if (resultListView.SelectedItems.Count > 0)
		{
			ListViewItem listViewItem = resultListView.SelectedItems[0];
			if (listViewItem.Tag is PodcastRadioInfo result)
			{
				return result;
			}
			if (listViewItem.Tag is PodcastEpisodeInfo episode)
			{
				PodcastRadioInfo podcastRadioInfo2 = ResolvePodcastFromEpisode(episode);
				if (podcastRadioInfo2 != null)
				{
					return podcastRadioInfo2;
				}
			}
			if (listViewItem.Tag is ListItemInfo listItemInfo)
			{
				if (listItemInfo.Type == ListItemType.Podcast && listItemInfo.Podcast != null)
				{
					return listItemInfo.Podcast;
				}
				if (listItemInfo.Type == ListItemType.PodcastEpisode && listItemInfo.PodcastEpisode != null)
				{
					PodcastRadioInfo podcastRadioInfo3 = ResolvePodcastFromEpisode(listItemInfo.PodcastEpisode);
					if (podcastRadioInfo3 != null)
					{
						return podcastRadioInfo3;
					}
				}
			}
			if (listViewItem.Tag is SongInfo { IsPodcastEpisode: not false } songInfo)
			{
				PodcastRadioInfo podcastRadioInfo4 = ResolvePodcastFromSong(songInfo);
				if (podcastRadioInfo4 != null)
				{
					return podcastRadioInfo4;
				}
			}
			if (listViewItem.Tag is int num && num >= 0 && num < _currentSongs.Count)
			{
				SongInfo songInfo2 = _currentSongs[num];
				if (songInfo2 != null && songInfo2.IsPodcastEpisode)
				{
					PodcastRadioInfo podcastRadioInfo5 = ResolvePodcastFromSong(songInfo2);
					if (podcastRadioInfo5 != null)
					{
						return podcastRadioInfo5;
					}
				}
			}
		}
		if (_currentPodcast != null)
		{
			return _currentPodcast;
		}
		return null;
	}

	private PodcastEpisodeInfo? GetSelectedPodcastEpisodeFromContextMenu(object? sender = null)
	{
		if (sender is ToolStripItem { Tag: PodcastEpisodeInfo tag })
		{
			return tag;
		}
		if (_isCurrentPlayingMenuActive)
		{
			SongInfo? currentPlayingMenuSong = _currentPlayingMenuSong;
			if (currentPlayingMenuSong != null && currentPlayingMenuSong.IsPodcastEpisode)
			{
				return ResolvePodcastEpisodeFromSong(_currentPlayingMenuSong);
			}
		}
		if (resultListView.SelectedItems.Count == 0)
		{
			return null;
		}
		ListViewItem listViewItem = resultListView.SelectedItems[0];
		if (listViewItem.Tag is PodcastEpisodeInfo result)
		{
			return result;
		}
		if (listViewItem.Tag is ListItemInfo listItemInfo)
		{
			if (listItemInfo.Type == ListItemType.PodcastEpisode && listItemInfo.PodcastEpisode != null)
			{
				return listItemInfo.PodcastEpisode;
			}
			if (listItemInfo.Type == ListItemType.Song)
			{
				SongInfo? song = listItemInfo.Song;
				if (song != null && song.IsPodcastEpisode)
				{
					return ResolvePodcastEpisodeFromSong(listItemInfo.Song);
				}
			}
		}
		if (listViewItem.Tag is SongInfo { IsPodcastEpisode: not false } songInfo)
		{
			return ResolvePodcastEpisodeFromSong(songInfo);
		}
		if (listViewItem.Tag is int num && num >= 0 && num < _currentSongs.Count)
		{
			SongInfo songInfo2 = _currentSongs[num];
			if (songInfo2 != null && songInfo2.IsPodcastEpisode)
			{
				return ResolvePodcastEpisodeFromSong(songInfo2);
			}
		}
		return GetPodcastEpisodeBySelectedIndex();
	}

	private void ConfigurePodcastMenuItems(PodcastRadioInfo? podcast, bool isLoggedIn, bool allowShare = true)
	{
		if (podcast != null)
		{
			bool flag = podcast.Id > 0;
			if (flag)
			{
				downloadPodcastMenuItem.Visible = true;
				downloadPodcastMenuItem.Tag = podcast;
				sharePodcastMenuItem.Visible = allowShare;
				sharePodcastMenuItem.Tag = (allowShare ? podcast : null);
			}
			else
			{
				sharePodcastMenuItem.Visible = false;
				sharePodcastMenuItem.Tag = null;
			}
			if (isLoggedIn && flag)
			{
				bool flag2 = ResolvePodcastSubscriptionState(podcast);
				subscribePodcastMenuItem.Visible = !flag2;
				unsubscribePodcastMenuItem.Visible = flag2;
				subscribePodcastMenuItem.Tag = podcast;
				unsubscribePodcastMenuItem.Tag = podcast;
				subscribePodcastMenuItem.Enabled = true;
				unsubscribePodcastMenuItem.Enabled = true;
			}
		}
	}

	private bool ResolvePodcastSubscriptionState(PodcastRadioInfo? podcast)
	{
		if (podcast == null)
		{
			return false;
		}
		if (podcast.Subscribed)
		{
			return true;
		}
		if (_currentPodcast != null && _currentPodcast.Id == podcast.Id)
		{
			return _currentPodcast.Subscribed;
		}
		lock (_libraryStateLock)
		{
			return _subscribedPodcastIds.Contains(podcast.Id);
		}
	}

	private void ConfigurePodcastEpisodeShareMenu(PodcastEpisodeInfo? episode)
	{
		if (episode == null || episode.ProgramId <= 0)
		{
			sharePodcastEpisodeMenuItem.Visible = false;
			sharePodcastEpisodeMenuItem.Tag = null;
			sharePodcastEpisodeWebMenuItem.Tag = null;
			sharePodcastEpisodeDirectMenuItem.Tag = null;
		}
		else
		{
			sharePodcastEpisodeMenuItem.Visible = true;
			sharePodcastEpisodeMenuItem.Tag = episode;
			sharePodcastEpisodeWebMenuItem.Tag = episode;
			sharePodcastEpisodeDirectMenuItem.Tag = episode;
		}
	}

	private PodcastRadioInfo? ResolvePodcastFromEpisode(PodcastEpisodeInfo? episode)
	{
		if (episode == null || episode.RadioId <= 0)
		{
			return null;
		}
		if (_currentPodcast != null && _currentPodcast.Id == episode.RadioId)
		{
			return _currentPodcast;
		}
		return new PodcastRadioInfo
		{
			Id = episode.RadioId,
			Name = (string.IsNullOrWhiteSpace(episode.RadioName) ? $"播客 {episode.RadioId}" : episode.RadioName),
			DjName = episode.DjName,
			DjUserId = episode.DjUserId
		};
	}

	private PodcastRadioInfo? ResolvePodcastFromSong(SongInfo? song)
	{
		if (song == null || song.PodcastRadioId <= 0)
		{
			return null;
		}
		if (_currentPodcast != null && _currentPodcast.Id == song.PodcastRadioId)
		{
			return _currentPodcast;
		}
		return new PodcastRadioInfo
		{
			Id = song.PodcastRadioId,
			Name = (string.IsNullOrWhiteSpace(song.PodcastRadioName) ? $"播客 {song.PodcastRadioId}" : song.PodcastRadioName),
			DjName = song.PodcastDjName
		};
	}

	private PodcastEpisodeInfo? ResolvePodcastEpisodeFromSong(SongInfo? song)
	{
		if (song == null || song.PodcastProgramId <= 0)
		{
			return null;
		}
		PodcastEpisodeInfo podcastEpisodeInfo = _currentPodcastSounds.FirstOrDefault((PodcastEpisodeInfo e) => e.ProgramId == song.PodcastProgramId);
		if (podcastEpisodeInfo != null)
		{
			if (podcastEpisodeInfo.Song == null)
			{
				podcastEpisodeInfo.Song = song;
			}
			return podcastEpisodeInfo;
		}
		return new PodcastEpisodeInfo
		{
			ProgramId = song.PodcastProgramId,
			Name = (string.IsNullOrWhiteSpace(song.Name) ? $"节目 {song.PodcastProgramId}" : song.Name),
			RadioId = song.PodcastRadioId,
			RadioName = song.PodcastRadioName,
			DjName = song.PodcastDjName,
			Song = song
		};
	}

	private SongInfo? EnsurePodcastEpisodeSong(PodcastEpisodeInfo? episode)
	{
		if (episode == null)
		{
			return null;
		}
		if (episode.Song != null)
		{
			return episode.Song;
		}
		if (episode.ProgramId <= 0)
		{
			return null;
		}
		return episode.Song = new SongInfo
		{
			Id = episode.ProgramId.ToString(CultureInfo.InvariantCulture),
			Name = (string.IsNullOrWhiteSpace(episode.Name) ? $"节目 {episode.ProgramId}" : episode.Name),
			Artist = (string.IsNullOrWhiteSpace(episode.DjName) ? (episode.RadioName ?? string.Empty) : episode.DjName),
			Album = (string.IsNullOrWhiteSpace(episode.RadioName) ? (episode.DjName ?? string.Empty) : (episode.RadioName ?? string.Empty)),
			PicUrl = episode.CoverUrl,
			Duration = ((episode.Duration > TimeSpan.Zero) ? checked((int)episode.Duration.TotalSeconds) : 0),
			IsAvailable = true,
			IsPodcastEpisode = true,
			PodcastProgramId = episode.ProgramId,
			PodcastRadioId = episode.RadioId,
			PodcastRadioName = (episode.RadioName ?? string.Empty),
			PodcastDjName = (episode.DjName ?? string.Empty),
			PodcastPublishTime = episode.PublishTime,
			PodcastEpisodeDescription = episode.Description,
			PodcastSerialNumber = episode.SerialNumber
		};
	}

	private bool IsPodcastEpisodeView()
	{
		if (string.IsNullOrWhiteSpace(_currentViewSource))
		{
			return false;
		}
		return _currentViewSource.StartsWith("podcast:", StringComparison.OrdinalIgnoreCase);
	}

	private PodcastEpisodeInfo? GetPodcastEpisodeBySelectedIndex()
	{
		if (!IsPodcastEpisodeView() || resultListView.SelectedItems.Count == 0)
		{
			return null;
		}
		ListViewItem listViewItem = resultListView.SelectedItems[0];
		if (listViewItem.Tag is int num && num < 0)
		{
			return null;
		}
		int index = listViewItem.Index;
		if (index >= 0 && index < _currentPodcastSounds.Count)
		{
			return _currentPodcastSounds[index];
		}
		return null;
	}

	private void UpdatePodcastSortMenuChecks()
	{
		if (podcastSortLatestMenuItem != null && podcastSortSerialMenuItem != null)
		{
			SetMenuItemCheckedState(podcastSortLatestMenuItem, !_podcastSortState.CurrentOption, "按最新排序");
			SetMenuItemCheckedState(podcastSortSerialMenuItem, _podcastSortState.CurrentOption, "按节目顺序排序");
			if (podcastSortMenuItem != null)
			{
				string text = (_podcastSortState.CurrentOption ? "节目顺序" : "按最新");
				podcastSortMenuItem.Text = "排序（" + text + "）";
				podcastSortMenuItem.AccessibleDescription = _podcastSortState.AccessibleDescription;
			}
		}
	}

	private void EnsureSortMenuCheckMargins()
	{
		EnsureSortMenuCheckMargin(artistSongsSortMenuItem);
		EnsureSortMenuCheckMargin(artistAlbumsSortMenuItem);
		EnsureSortMenuCheckMargin(podcastSortMenuItem);
	}

	private void EnsureSortMenuCheckMargin(ToolStripMenuItem? menuItem)
	{
		if (menuItem?.DropDown is ToolStripDropDownMenu { ShowCheckMargin: false } toolStripDropDownMenu)
		{
			toolStripDropDownMenu.ShowCheckMargin = true;
		}
	}

	private void UpdateArtistSongsSortMenuChecks()
	{
		if (artistSongsSortHotMenuItem != null && artistSongsSortTimeMenuItem != null)
		{
			SetMenuItemCheckedState(artistSongsSortHotMenuItem, _artistSongSortState.EqualsOption(ArtistSongSortOption.Hot), "按热门排序");
			SetMenuItemCheckedState(artistSongsSortTimeMenuItem, _artistSongSortState.EqualsOption(ArtistSongSortOption.Time), "按发布时间排序");
			if (artistSongsSortMenuItem != null)
			{
				string text = (_artistSongSortState.EqualsOption(ArtistSongSortOption.Hot) ? "按热门" : "按发布时间");
				artistSongsSortMenuItem.Text = "单曲排序（" + text + "）";
				artistSongsSortMenuItem.AccessibleDescription = _artistSongSortState.AccessibleDescription;
			}
		}
	}

	private void UpdateArtistAlbumsSortMenuChecks()
	{
		if (artistAlbumsSortLatestMenuItem != null && artistAlbumsSortOldestMenuItem != null)
		{
			SetMenuItemCheckedState(artistAlbumsSortLatestMenuItem, _artistAlbumSortState.EqualsOption(ArtistAlbumSortOption.Latest), "按最新发布排序");
			SetMenuItemCheckedState(artistAlbumsSortOldestMenuItem, _artistAlbumSortState.EqualsOption(ArtistAlbumSortOption.Oldest), "按最早发布排序");
			if (artistAlbumsSortMenuItem != null)
			{
				string text = (_artistAlbumSortState.EqualsOption(ArtistAlbumSortOption.Latest) ? "按最新" : "按最早");
				artistAlbumsSortMenuItem.Text = "专辑排序（" + text + "）";
				artistAlbumsSortMenuItem.AccessibleDescription = _artistAlbumSortState.AccessibleDescription;
			}
		}
	}

	private static void SetMenuItemCheckedState(ToolStripMenuItem? menuItem, bool isChecked, string baseAccessibleName)
	{
		if (menuItem != null)
		{
			menuItem.Checked = isChecked;
			menuItem.CheckState = (isChecked ? CheckState.Checked : CheckState.Unchecked);
			if (!string.IsNullOrWhiteSpace(baseAccessibleName))
			{
				menuItem.AccessibleName = (isChecked ? (baseAccessibleName + " 已选中") : baseAccessibleName);
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
			return new ValueTuple<long, string>(item2: ((song.ArtistNames != null && song.ArtistNames.Count > 0) ? song.ArtistNames[0] : song.Artist) ?? string.Empty, item1: song.ArtistIds[0]);
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
				return new ValueTuple<long, string>(item2: ((detail.ArtistNames != null && detail.ArtistNames.Count > 0) ? detail.ArtistNames[0] : detail.Artist) ?? string.Empty, item1: detail.ArtistIds[0]);
			}
		}
		return (ArtistId: 0L, ArtistName: string.Empty);
	}

	private async Task<string?> ResolveSongAlbumIdAsync(SongInfo song)
	{
		if (song == null)
		{
			return null;
		}
		if (!string.IsNullOrWhiteSpace(song.AlbumId))
		{
			return song.AlbumId;
		}
		if (string.IsNullOrWhiteSpace(song.Id))
		{
			return null;
		}
		SongInfo detail = (await _apiClient.GetSongDetailAsync(new string[1] { song.Id }))?.FirstOrDefault();
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
		return song.AlbumId;
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
		bool available = default(bool);
		if ((await _apiClient.BatchCheckSongsAvailabilityAsync(new string[1] { song.Id }, quality))?.TryGetValue(song.Id, out available) ?? false)
		{
			song.IsAvailable = available;
			return available;
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
		Dictionary<string, bool> availability = await _apiClient.BatchCheckSongsAvailabilityAsync(idList, quality);
		foreach (SongInfo song in songs)
		{
			if (song != null && !string.IsNullOrWhiteSpace(song.Id) && availability.TryGetValue(song.Id, out var available))
			{
				song.IsAvailable = available;
			}
		}
		return availability;
	}

	private async Task<Dictionary<string, SongUrlInfo>> FetchSongUrlsInBatchesAsync(IEnumerable<string> songIds, bool skipAvailabilityCheck = true)
	{
		List<string> ids = songIds.Where((string id) => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
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
				MessageBox.Show("复制链接失败：" + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("复制链接失败");
				return;
			}
			UpdateStatusBar("歌曲网页链接已复制到剪贴板");
		}
		catch (Exception ex3)
		{
			MessageBox.Show("分享歌曲失败：" + ex3.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
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
			if (!(await EnsureSongAvailabilityAsync(song)))
			{
				MessageBox.Show("该歌曲资源不可用，无法分享直链。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				UpdateStatusBar("歌曲资源不可用");
				return;
			}
			if (!(await FetchSongUrlsInBatchesAsync(new string[1] { song.Id })).TryGetValue(song.Id, out var urlInfo) || string.IsNullOrWhiteSpace(urlInfo.Url))
			{
				MessageBox.Show("未能获取歌曲直链，可能需要登录或切换音质。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				UpdateStatusBar("获取直链失败");
				return;
			}
			try
			{
				Clipboard.SetText(urlInfo.Url);
			}
			catch (ExternalException ex)
			{
				ExternalException ex2 = ex;
				MessageBox.Show("复制链接失败：" + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("复制链接失败");
				return;
			}
			UpdateStatusBar("歌曲直链已复制到剪贴板");
		}
		catch (Exception ex3)
		{
			MessageBox.Show("分享歌曲直链失败：" + ex3.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
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
					Exception refreshEx = ex;
					Debug.WriteLine($"[UI] 刷新我的歌单列表失败: {refreshEx}");
				}
			}
			else
			{
				MessageBox.Show("创建歌单失败，请稍后重试。", "失败", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("创建歌单失败");
			}
		}
		catch (Exception ex2)
		{
			MessageBox.Show("创建歌单失败: " + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
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
					Exception refreshEx = ex;
					Debug.WriteLine($"[UI] 刷新我的歌单列表失败: {refreshEx}");
					return;
				}
			}
			MessageBox.Show("取消收藏失败，请检查网络或稍后重试。", "失败", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("取消收藏失败");
		}
		catch (Exception ex2)
		{
			MessageBox.Show("取消收藏失败: " + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("取消收藏失败");
		}
	}

	private async void deletePlaylistMenuItem_Click(object sender, EventArgs e)
	{
		object obj = ((resultListView.SelectedItems.Count > 0) ? resultListView.SelectedItems[0] : null)?.Tag;
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
					Exception refreshEx = ex;
					Debug.WriteLine($"[UI] 刷新我的歌单列表失败: {refreshEx}");
					return;
				}
			}
			MessageBox.Show("删除歌单失败，请检查网络或稍后重试。", "失败", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("删除歌单失败");
		}
		catch (Exception ex2)
		{
			MessageBox.Show("删除歌单失败: " + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("删除歌单失败");
		}
	}

	private async void subscribeAlbumMenuItem_Click(object sender, EventArgs e)
	{
		AlbumInfo album = GetSelectedAlbumFromContextMenu(sender);
		if (album == null || string.IsNullOrWhiteSpace(album.Id))
		{
			MessageBox.Show("无法识别专辑信息，收藏操作已取消。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			return;
		}
		try
		{
			UpdateStatusBar("正在收藏专辑...");
			if (await _apiClient.SubscribeAlbumAsync(album.Id))
			{
				album.IsSubscribed = true;
				UpdateAlbumSubscriptionState(album.Id, isSubscribed: true);
				MessageBox.Show("已收藏专辑：" + album.Name, "成功", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				UpdateStatusBar("专辑收藏成功");
			}
			else
			{
				MessageBox.Show("收藏专辑失败，请检查网络或稍后重试。", "失败", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("专辑收藏失败");
			}
		}
		catch (Exception ex)
		{
			MessageBox.Show("收藏专辑失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("专辑收藏失败");
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
			SongUrlInfo urlInfo;
			if (!(await EnsureSongAvailabilityAsync(song)))
			{
				MessageBox.Show("该节目资源不可用，无法分享直链。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				UpdateStatusBar("节目资源不可用");
			}
			else if (!(await FetchSongUrlsInBatchesAsync(new string[1] { song.Id })).TryGetValue(song.Id, out urlInfo) || string.IsNullOrWhiteSpace(urlInfo.Url))
			{
				MessageBox.Show("未能获取节目直链，可能需要登录或稍后重试。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				UpdateStatusBar("获取直链失败");
			}
			else
			{
				Clipboard.SetText(urlInfo.Url);
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
		catch (Exception arg)
		{
			Debug.WriteLine($"[Refresh] 刷新失败: {arg}");
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
			ParseArtistListViewSource(_currentViewSource, out long artistId, out int _, out string currentOrderToken, "latest");
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
			ParseArtistListViewSource(_currentViewSource, out long artistId, out int _, out string currentOrderToken);
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
					Exception refreshEx = ex;
					Debug.WriteLine($"[UI] 刷新收藏的专辑列表失败: {refreshEx}");
					return;
				}
			}
			MessageBox.Show("取消收藏失败，请检查网络或稍后重试。", "失败", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("取消收藏失败");
		}
		catch (Exception ex2)
		{
			MessageBox.Show("取消收藏失败: " + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
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
		long parsedId = ParseUserIdFromAccountState(_accountState);
		if (parsedId > 0)
		{
			CacheLoggedInUserId(parsedId);
			return _loggedInUserId;
		}
		return 0L;
	}

	private static long ParseUserIdFromAccountState(AccountState? state)
	{
		if (state == null)
		{
			return 0;
		}
		if (long.TryParse(state.UserId, out var parsed) && parsed > 0)
		{
			return parsed;
		}
		if (state.AccountDetail?.UserId > 0)
		{
			return state.AccountDetail.UserId;
		}
		return 0;
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
			PlaylistInfo match = _currentPlaylists?.FirstOrDefault((PlaylistInfo p) => string.Equals(p?.Id, playlistId, StringComparison.OrdinalIgnoreCase)) ?? null;
			if (match != null && IsPlaylistOwnedByUser(match, currentUserId))
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

	protected override void OnFormClosing(FormClosingEventArgs e)
	{
		_isFormClosing = true;
		_isApplicationExitRequested = true;
		StopInitialHomeLoadLoop("窗口关闭");
		_autoUpdateCheckCts?.Cancel();
		_autoUpdateCheckCts?.Dispose();
		_autoUpdateCheckCts = null;
		CancelPendingLyricSpeech();
		base.OnFormClosing(e);
		CompleteActivePlaybackSession(PlaybackEndReason.Stopped);
		try
		{
			_playbackCancellation?.Cancel();
			_playbackCancellation?.Dispose();
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
			StopStateUpdateLoop();
			_updateTimer?.Stop();
			_nextSongPreloader?.Dispose();
			_audioEngine?.Dispose();
			_apiClient?.Dispose();
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
			_playbackReportingService?.Dispose();
			SaveConfig();
		}
		catch (Exception ex4)
		{
			Debug.WriteLine("[OnFormClosing] 异常: " + ex4.Message);
		}
	}

	private SongInfo PredictNextSong()
	{
		try
		{
			PlayMode playMode = _audioEngine?.PlayMode ?? PlayMode.Loop;
			return _playbackQueue.PredictNextAvailable(playMode);
		}
		catch (Exception ex)
		{
			Debug.WriteLine("[MainForm] 预测下一首歌曲失败: " + ex.Message);
			return null;
		}
	}

	private SongInfo PredictNextFromQueue()
	{
		PlayMode playMode = _audioEngine?.PlayMode ?? PlayMode.Loop;
		return _playbackQueue.PredictFromQueue(playMode);
	}

	private async Task<Dictionary<string, SongUrlInfo>> GetSongUrlWithTimeoutAsync(string[] ids, QualityLevel quality, CancellationToken cancellationToken, bool skipAvailabilityCheck = false)
	{
		DateTime startTime = DateTime.Now;
		Debug.WriteLine(string.Format("[MainForm] ⏱ 开始获取播放链接: IDs={0}, quality={1}, skipCheck={2}", string.Join(",", ids), quality, skipAvailabilityCheck));
		Task<Dictionary<string, SongUrlInfo>> songUrlTask = _apiClient.GetSongUrlAsync(ids, quality, skipAvailabilityCheck, cancellationToken);
		using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		Task timeoutTask = Task.Delay(12000, timeoutCts.Token);
		if (await Task.WhenAny(songUrlTask, timeoutTask).ConfigureAwait(continueOnCapturedContext: false) == songUrlTask)
		{
			timeoutCts.Cancel();
			Dictionary<string, SongUrlInfo> result = await songUrlTask.ConfigureAwait(continueOnCapturedContext: false);
			double elapsed = (DateTime.Now - startTime).TotalMilliseconds;
			Debug.WriteLine($"[MainForm] ✓ 获取播放链接成功，耗时: {elapsed:F0}ms");
			return result;
		}
		if (cancellationToken.IsCancellationRequested)
		{
			double elapsed2 = (DateTime.Now - startTime).TotalMilliseconds;
			Debug.WriteLine($"[MainForm] ❌ 获取播放链接被取消，已耗时: {elapsed2:F0}ms");
			throw new OperationCanceledException(cancellationToken);
		}
		double timeoutElapsed = (DateTime.Now - startTime).TotalMilliseconds;
		Debug.WriteLine($"[MainForm] ❌ 获取播放链接超时，耗时: {timeoutElapsed:F0}ms (超时限制: {12000}ms)");
		throw new TimeoutException($"获取播放链接超时（{timeoutElapsed:F0}ms > {12000}ms）");
	}

	private async Task<Dictionary<string, SongUrlInfo>> GetSongUrlWithRetryAsync(string[] ids, QualityLevel quality, CancellationToken cancellationToken, int? maxAttempts = null, bool suppressStatusUpdates = false, bool skipAvailabilityCheck = false)
	{
		int attempt = 0;
		int delayMs = 1200;
		checked
		{
			while (true)
			{
				cancellationToken.ThrowIfCancellationRequested();
				attempt++;
				try
				{
					return await GetSongUrlWithTimeoutAsync(ids, quality, cancellationToken, skipAvailabilityCheck).ConfigureAwait(continueOnCapturedContext: false);
				}
				catch (SongResourceNotFoundException)
				{
					throw;
				}
				catch (PaidAlbumNotPurchasedException)
				{
					throw;
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (Exception ex4)
				{
					Debug.WriteLine($"[MainForm] 获取播放链接失败（尝试 {attempt}）: {ex4.Message}");
					if (maxAttempts.HasValue && attempt >= maxAttempts.Value)
					{
						throw;
					}
					if (!suppressStatusUpdates)
					{
						SetPlaybackLoadingState(isLoading: true, $"获取播放链接失败，正在重试（第 {attempt} 次）...");
					}
					try
					{
						await Task.Delay(Math.Min(delayMs, 5000), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
					}
					catch (OperationCanceledException)
					{
						throw;
					}
					delayMs = Math.Min(delayMs * 2, 5000);
				}
			}
		}
	}

	private async Task PlaySong(SongInfo song)
	{
		Debug.WriteLine("[MainForm] PlaySong 被调用（用户主动播放）: song=" + song?.Name);
		if (song != null)
		{
			PlaybackSelectionResult selection = _playbackQueue.ManualSelect(song, _currentSongs, _currentViewSource);
			switch (selection.Route)
			{
			case PlaybackRoute.Queue:
			case PlaybackRoute.ReturnToQueue:
				Debug.WriteLine($"[MainForm] 队列播放: 来源={_playbackQueue.QueueSource}, 索引={selection.QueueIndex}, 刷新={selection.QueueChanged}");
				UpdateFocusForQueue(selection.QueueIndex, selection.Song);
				break;
			case PlaybackRoute.Injection:
			case PlaybackRoute.PendingInjection:
				Debug.WriteLine($"[MainForm] 插播播放: {song.Name}, 插播索引={selection.InjectionIndex}");
				UpdateFocusForInjection(song, selection.InjectionIndex);
				break;
			default:
				Debug.WriteLine("[MainForm] 播放选择未产生有效路由");
				break;
			}
			await PlaySongDirectWithCancellation(song).ConfigureAwait(continueOnCapturedContext: false);
		}
	}

	private async Task PlaySongDirectWithCancellation(SongInfo song, bool isAutoPlayback = false)
	{
		Debug.WriteLine($"[MainForm] PlaySongDirectWithCancellation 被调用: song={song?.Name}, isAutoPlayback={isAutoPlayback}");
		if (_audioEngine == null || song == null)
		{
			Debug.WriteLine("[MainForm ERROR] _audioEngine is null or song is null");
			return;
		}
		_playbackCancellation?.Cancel();
		_playbackCancellation?.Dispose();
		_playbackCancellation = new CancellationTokenSource();
		CancellationToken cancellationToken = _playbackCancellation.Token;
		long requestVersion = Interlocked.Increment(ref _playRequestVersion);
		TimeSpan timeSinceLastRequest = DateTime.Now - _lastPlayRequestTime;
		if (timeSinceLastRequest.TotalMilliseconds < 200.0)
		{
			int delayMs = checked(200 - (int)timeSinceLastRequest.TotalMilliseconds);
			Debug.WriteLine($"[MainForm] 请求过快，延迟 {delayMs}ms");
			try
			{
				await Task.Delay(delayMs, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			}
			catch (TaskCanceledException)
			{
				Debug.WriteLine("[MainForm] 播放请求在延迟期间被取消");
				return;
			}
		}
		_lastPlayRequestTime = DateTime.Now;
		await PlaySongDirect(song, cancellationToken, isAutoPlayback, requestVersion).ConfigureAwait(continueOnCapturedContext: false);
	}

	private bool IsCurrentPlayRequest(long requestVersion)
	{
		return Interlocked.Read(ref _playRequestVersion) == requestVersion;
	}

	private void UpdateLoadingState(bool isLoading, string? statusMessage = null, long playRequestVersion = 0L)
	{
		if (playRequestVersion != 0L && !IsCurrentPlayRequest(playRequestVersion))
		{
			Debug.WriteLine($"[MainForm] 忽略过期播放请求的加载状态更新: version={playRequestVersion}");
			return;
		}
		SetPlaybackLoadingState(isLoading, statusMessage);
		UpdateStatusBar(statusMessage);
	}

	private async Task<bool> ShowPurchaseLinkDialogAsync(SongInfo song, CancellationToken cancellationToken)
	{
		if (song == null || string.IsNullOrWhiteSpace(song.Id))
		{
			return false;
		}
		string encodedSongId = Uri.EscapeDataString(song.Id);
		string purchaseUrl = "https://music.163.com/#/payfee?songId=" + encodedSongId;
		TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>();
		if (base.IsDisposed)
		{
			return false;
		}
		if (base.InvokeRequired)
		{
			BeginInvoke(new Action(ShowDialog));
		}
		else
		{
			ShowDialog();
		}
		using (cancellationToken.Register(delegate
		{
			tcs.TrySetCanceled();
		}))
		{
			return await tcs.Task.ConfigureAwait(continueOnCapturedContext: false);
		}
		void ShowDialog()
		{
			try
			{
				using PurchaseLinkDialog purchaseLinkDialog = new PurchaseLinkDialog(song.Name, song.Album, purchaseUrl);
				DialogResult dialogResult = purchaseLinkDialog.ShowDialog(this);
				tcs.TrySetResult(dialogResult == DialogResult.OK && purchaseLinkDialog.PurchaseRequested);
			}
			catch (Exception exception)
			{
				tcs.TrySetException(exception);
			}
		}
	}

	private async Task PlaySongDirect(SongInfo song, CancellationToken cancellationToken, bool isAutoPlayback = false, long playRequestVersion = 0L)
	{
		Debug.WriteLine($"[MainForm] PlaySongDirect 被调用: song={song?.Name}, isAutoPlayback={isAutoPlayback}");
		if (_audioEngine == null)
		{
			Debug.WriteLine("[MainForm ERROR] _audioEngine is null");
			return;
		}
		if (song == null)
		{
			Debug.WriteLine("[MainForm ERROR] song is null");
			return;
		}
		PreparePlaybackReportingForNextSong(song);
		string currentSongId = song.Id;
		string nextSongId = PredictNextSong()?.Id;
		_nextSongPreloader?.CleanupStaleData(currentSongId, nextSongId);
		PreloadedData preloadedData = _audioEngine?.ConsumeGaplessPreload(song.Id) ?? _nextSongPreloader?.TryGetPreloadedData(song.Id);
		if (preloadedData != null)
		{
			Debug.WriteLine($"[MainForm] ✓ 命中预加载缓存: {song.Name}, 流就绪: {preloadedData.IsReady}");
			song.Url = preloadedData.Url;
			song.Level = preloadedData.Level;
			song.Size = preloadedData.Size;
		}
		bool loadingStateActive = false;
		try
		{
			UpdateLoadingState(isLoading: true, "正在获取歌曲数据: " + song.Name, playRequestVersion);
			loadingStateActive = true;
			if (song.IsAvailable == false)
			{
				Debug.WriteLine("[MainForm] 歌曲资源不可用（预检缓存）: " + song.Name);
				UpdateLoadingState(isLoading: false, "歌曲不存在，已跳过", playRequestVersion);
				loadingStateActive = false;
				HandleSongResourceNotFoundDuringPlayback(song, isAutoPlayback);
				return;
			}
			string defaultQualityName = _config.DefaultQuality ?? "超清母带";
			QualityLevel selectedQuality = NeteaseApiClient.GetQualityLevelFromName(defaultQualityName);
			string selectedQualityLevel = selectedQuality.ToString().ToLower();
			bool needRefreshUrl = string.IsNullOrEmpty(song.Url);
			if (!needRefreshUrl && !string.IsNullOrEmpty(song.Level))
			{
				string cachedLevel = song.Level.ToLower();
				if (cachedLevel != selectedQualityLevel)
				{
					Debug.WriteLine("[MainForm] ⚠ 音质不一致（缓存: " + song.Level + ", 当前选择: " + selectedQualityLevel + "），重新获取URL");
					song.Url = null;
					song.Level = null;
					song.Size = 0L;
					needRefreshUrl = true;
				}
				else
				{
					Debug.WriteLine("[MainForm] ✓ 音质一致性检查通过: " + song.Name + ", 音质: " + song.Level);
				}
			}
			if (needRefreshUrl)
			{
				QualityUrlInfo cachedQuality = song.GetQualityUrl(selectedQualityLevel);
				if (cachedQuality != null && !string.IsNullOrEmpty(cachedQuality.Url))
				{
					Debug.WriteLine($"[MainForm] ✓ 命中多音质缓存: {song.Name}, 音质: {selectedQualityLevel}, 试听: {cachedQuality.IsTrial}");
					song.Url = cachedQuality.Url;
					song.Level = cachedQuality.Level;
					song.Size = cachedQuality.Size;
					song.IsTrial = cachedQuality.IsTrial;
					song.TrialStart = cachedQuality.TrialStart;
					song.TrialEnd = cachedQuality.TrialEnd;
				}
				else
				{
					Debug.WriteLine("[MainForm] 无URL缓存，重新获取: " + song.Name + ", 目标音质: " + defaultQualityName);
					if (cancellationToken.IsCancellationRequested)
					{
						UpdateLoadingState(isLoading: false, "播放已取消", playRequestVersion);
						loadingStateActive = false;
						return;
					}

					// 若尚未检查可用性，先执行一次（登录/未登录都检查）
					if (!song.IsAvailable.HasValue)
					{
						bool available = await EnsureSongAvailabilityAsync(song, selectedQuality, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
						if (!available)
						{
							Debug.WriteLine("[MainForm] 单曲可用性检查判定为不可用: " + song.Name);
							UpdateLoadingState(isLoading: false, "歌曲不存在，已跳过", playRequestVersion);
							loadingStateActive = false;
							HandleSongResourceNotFoundDuringPlayback(song, isAutoPlayback);
							return;
						}
					}

					Dictionary<string, SongUrlInfo> urlResult;
					try
					{
						urlResult = await GetSongUrlWithRetryAsync(new string[1] { song.Id }, selectedQuality, cancellationToken, null, suppressStatusUpdates: false, skipAvailabilityCheck: false).ConfigureAwait(continueOnCapturedContext: false);
					}
					catch (SongResourceNotFoundException ex)
					{
						Debug.WriteLine("[MainForm] 获取播放链接时检测到歌曲缺失: " + ex.Message);
						song.IsAvailable = false;
						UpdateLoadingState(isLoading: false, "歌曲不存在，已跳过", playRequestVersion);
						loadingStateActive = false;
						HandleSongResourceNotFoundDuringPlayback(song, isAutoPlayback);
						return;
					}
					catch (PaidAlbumNotPurchasedException ex2)
					{
						Debug.WriteLine("[MainForm] 歌曲属于付费专辑且未购买: " + ex2.Message);
						UpdateLoadingState(isLoading: false, "此歌曲属于付费数字专辑，需购买后才能播放。", playRequestVersion);
						loadingStateActive = false;
						if (!(_accountState?.IsLoggedIn ?? false) || string.IsNullOrWhiteSpace(song.Id))
						{
							SafeInvoke(delegate
							{
								MessageBox.Show(this, "该歌曲属于付费数字专辑，你需要登录/注册网易云音乐官方客户端购买后，登录本应用重试。", "需要购买", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
							});
							UpdateStatusBar("无法播放：未购买付费专辑");
						}
						else if (await ShowPurchaseLinkDialogAsync(song, cancellationToken).ConfigureAwait(continueOnCapturedContext: false))
						{
							UpdateStatusBar("已打开官方购买页面，请完成购买后重新播放。");
						}
						else
						{
							UpdateStatusBar("购买已取消");
						}
						return;
					}
					catch (OperationCanceledException)
					{
						Debug.WriteLine("[MainForm] 播放链接获取被取消");
						UpdateLoadingState(isLoading: false, "播放已取消", playRequestVersion);
						loadingStateActive = false;
						return;
					}
					catch (Exception ex4)
					{
						Debug.WriteLine("[MainForm] 获取播放链接失败: " + ex4.Message);
						UpdateLoadingState(isLoading: false, "获取播放链接失败", playRequestVersion);
						loadingStateActive = false;
						SafeInvoke(delegate
						{
							MessageBox.Show(this, "无法获取播放链接，请尝试播放其他歌曲", "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
						});
						UpdateStatusBar("获取播放链接失败");
						return;
					}
					if (cancellationToken.IsCancellationRequested)
					{
						UpdateLoadingState(isLoading: false, "播放已取消", playRequestVersion);
						loadingStateActive = false;
						return;
					}
					if (!urlResult.TryGetValue(song.Id, out SongUrlInfo songUrl) || string.IsNullOrEmpty(songUrl?.Url))
					{
						Debug.WriteLine("[MainForm ERROR] 无法获取播放链接");
						UpdateLoadingState(isLoading: false, "获取播放链接失败", playRequestVersion);
						loadingStateActive = false;
						MessageBox.Show("无法获取播放链接，请尝试播放其他歌曲", "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
						UpdateStatusBar("获取播放链接失败");
						return;
					}
					bool isTrial = songUrl.FreeTrialInfo != null;
					long trialStart = songUrl.FreeTrialInfo?.Start ?? 0;
					long trialEnd = songUrl.FreeTrialInfo?.End ?? 0;
					if (isTrial)
					{
						Debug.WriteLine($"[MainForm] \ud83c\udfb5 试听版本: {song.Name}, 片段: {trialStart / 1000}s - {trialEnd / 1000}s");
					}
					string actualLevel = songUrl.Level?.ToLower() ?? selectedQualityLevel;
					song.SetQualityUrl(actualLevel, songUrl.Url, songUrl.Size, isAvailable: true, isTrial, trialStart, trialEnd);
					Debug.WriteLine($"[MainForm] ✓ 已缓存音质URL: {song.Name}, 音质: {actualLevel}, 大小: {songUrl.Size}, 试听: {isTrial}");
					song.Url = songUrl.Url;
					song.Level = songUrl.Level;
					song.Size = songUrl.Size;
					song.IsTrial = isTrial;
					song.TrialStart = trialStart;
					song.TrialEnd = trialEnd;
					OptimizedHttpClientFactory.PreWarmConnection(song.Url);
				}
			}
			if (cancellationToken.IsCancellationRequested)
			{
				UpdateLoadingState(isLoading: false, "播放已取消", playRequestVersion);
				loadingStateActive = false;
				Debug.WriteLine("[MainForm] 播放请求已取消（播放前）");
				return;
			}
			_lyricsDisplayManager?.Clear();
			_currentLyrics?.Clear();
			Debug.WriteLine("[MainForm] 已清空旧歌词，准备播放新歌曲");
			bool playResult = await _audioEngine.PlayAsync(song, cancellationToken, preloadedData);
			Debug.WriteLine($"[MainForm] _audioEngine.PlayAsync() 返回: {playResult}");
			if (!playResult)
			{
				UpdateLoadingState(isLoading: false, "播放失败", playRequestVersion);
				loadingStateActive = false;
				Debug.WriteLine("[MainForm ERROR] 播放失败");
				return;
			}
			if (_seekManager != null)
			{
				if (_audioEngine.CurrentCacheManager != null)
				{
					Debug.WriteLine("[MainForm] ⭐⭐⭐ 设置 SeekManager 为缓存流模式 ⭐⭐⭐");
					Debug.WriteLine($"[MainForm] CacheManager 大小: {_audioEngine.CurrentCacheManager.TotalSize:N0} bytes");
					_seekManager.SetCacheStream(_audioEngine.CurrentCacheManager);
				}
				else
				{
					Debug.WriteLine("[MainForm] ⚠\ufe0f 设置 SeekManager 为直接流模式（不支持任意跳转）");
					_seekManager.SetDirectStream();
				}
			}
			else
			{
				Debug.WriteLine("[MainForm] ⚠\ufe0f SeekManager 为 null，无法设置流模式");
			}
			if (loadingStateActive)
			{
				string statusText = (song.IsTrial ? ("正在播放: " + song.Name + " [试听版]") : ("正在播放: " + song.Name));
				UpdateLoadingState(isLoading: false, statusText, playRequestVersion);
				loadingStateActive = false;
			}
			else
			{
				string statusText2 = (song.IsTrial ? ("正在播放: " + song.Name + " [试听版]") : ("正在播放: " + song.Name));
				UpdateStatusBar(statusText2);
			}
			SafeInvoke(delegate
			{
				string text = (song.IsTrial ? (song.Name + "(试听版)") : song.Name);
				currentSongLabel.Text = text + " - " + song.Artist;
				playPauseButton.Text = "暂停";
				Debug.WriteLine("[PlaySongDirect] 播放成功，按钮设置为: 暂停");
			});
			await Task.Delay(50, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			if (!cancellationToken.IsCancellationRequested)
			{
				SafeInvoke(delegate
				{
					SyncPlayPauseButtonText();
				});
			}
			SafeInvoke(delegate
			{
				UpdatePlayButtonDescription(song);
				UpdateTrayIconTooltip(song);
				if (!base.Visible && !isAutoPlayback)
				{
					ShowTrayBalloonTip(song);
				}
			});
			BeginPlaybackReportingSession(song);
			if (song.IsTrial)
			{
				Task.Run(delegate
				{
					bool flag = TtsHelper.SpeakText("[试听片段 30 秒]", interrupt: true, suppressGlobalInterrupt: true);
					Debug.WriteLine("[TTS] 试听提示: " + (flag ? "成功" : "失败"));
				});
			}
			LoadLyrics(song.Id, cancellationToken);
			SafeInvoke(delegate
			{
				RefreshNextSongPreload();
			});
		}
		catch (OperationCanceledException)
		{
			Debug.WriteLine("[MainForm] 播放请求被取消");
			UpdateLoadingState(isLoading: false, "播放已取消", playRequestVersion);
		}
		catch (Exception ex6)
		{
			Exception ex7 = ex6;
			if (!cancellationToken.IsCancellationRequested)
			{
				Debug.WriteLine("[MainForm ERROR] PlaySongDirect 异常: " + ex7.Message);
				Debug.WriteLine("[MainForm ERROR] 堆栈: " + ex7.StackTrace);
				UpdateLoadingState(isLoading: false, "播放失败", playRequestVersion);
				MessageBox.Show("播放失败: " + ex7.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("播放失败");
			}
		}
		finally
		{
			if (loadingStateActive)
			{
				UpdateLoadingState(isLoading: false, null, playRequestVersion);
			}
		}
	}

	private async void PlayPrevious(bool isManual = true)
	{
		Debug.WriteLine($"[MainForm] PlayPrevious 被调用 (isManual={isManual})");
		PlayMode playMode = _audioEngine?.PlayMode ?? PlayMode.Loop;
		SongInfo currentSong = _audioEngine?.CurrentSong;
		if (currentSong != null && isManual)
		{
			_playbackQueue.AdvanceForPlayback(currentSong, playMode, _currentViewSource);
			Debug.WriteLine("[MainForm] 已同步播放队列到当前歌曲: " + currentSong.Name);
		}
		if (isManual && await TryPlayManualDirectionalAsync(isNext: false))
		{
			return;
		}
		PlaybackMoveResult result = _playbackQueue.MovePrevious(playMode, isManual, _currentViewSource);
		if (result.QueueEmpty)
		{
			Debug.WriteLine("[MainForm] 播放队列为空，无法播放上一首");
			UpdateStatusBar("播放队列为空");
		}
		else if (!result.HasSong)
		{
			if (isManual && result.ReachedBoundary)
			{
				UpdateStatusBar("已经是第一首");
			}
		}
		else
		{
			await ExecutePlayPreviousResultAsync(result).ConfigureAwait(continueOnCapturedContext: false);
		}
	}

	private async void PlayNext(bool isManual = true)
	{
		Debug.WriteLine($"[MainForm] PlayNext 被调用 (isManual={isManual})");
		PlayMode playMode = _audioEngine?.PlayMode ?? PlayMode.Loop;
		SongInfo currentSong = _audioEngine?.CurrentSong;
		if (currentSong != null && isManual)
		{
			_playbackQueue.AdvanceForPlayback(currentSong, playMode, _currentViewSource);
			Debug.WriteLine("[MainForm] 已同步播放队列到当前歌曲: " + currentSong.Name);
		}
		if (isManual && await TryPlayManualDirectionalAsync(isNext: true))
		{
			return;
		}
		PlaybackMoveResult result = _playbackQueue.MoveNext(playMode, isManual, _currentViewSource);
		if (result.QueueEmpty)
		{
			Debug.WriteLine("[MainForm] 播放队列为空，无法播放下一首");
			UpdateTrayIconTooltip(null);
			UpdateStatusBar("播放队列为空");
			currentSongLabel.Text = "未播放";
			UpdatePlayButtonDescription(null);
		}
		else if (!result.HasSong)
		{
			if (isManual && result.ReachedBoundary)
			{
				UpdateStatusBar("已经是最后一首");
			}
			else if (!isManual && playMode == PlayMode.Sequential && result.ReachedBoundary)
			{
				HandleSequentialPlaybackCompleted();
			}
		}
		else
		{
			await ExecutePlayNextResultAsync(result).ConfigureAwait(continueOnCapturedContext: false);
		}
	}

	private async Task<bool> TryPlayManualDirectionalAsync(bool isNext)
	{
		PlayMode playMode = _audioEngine?.PlayMode ?? PlayMode.Loop;
		SongInfo currentSong = _audioEngine?.CurrentSong;
		HashSet<string> attemptedIds = new HashSet<string>(StringComparer.Ordinal);
		int maxAttempts = CalculateManualNavigationLimit();
		for (int attempt = 0; attempt < maxAttempts; attempt = checked(attempt + 1))
		{
			PlaybackMoveResult result = (isNext ? _playbackQueue.MoveNext(playMode, isManual: true, _currentViewSource) : _playbackQueue.MovePrevious(playMode, isManual: true, _currentViewSource));
			if (result.QueueEmpty)
			{
				Debug.WriteLine("[MainForm] 播放队列为空，手动导航停止");
				if (isNext)
				{
					UpdateTrayIconTooltip(null);
				}
				UpdateStatusBar("播放队列为空");
				return true;
			}
			if (!result.HasSong)
			{
				if (result.ReachedBoundary)
				{
					UpdateStatusBar(isNext ? "已经是最后一首" : "已经是第一首");
				}
				RestoreQueuePosition(currentSong, playMode);
				return true;
			}
			SongInfo candidate = result.Song;
			if (candidate == null || string.IsNullOrWhiteSpace(candidate.Id))
			{
				Debug.WriteLine("[MainForm] 手动导航遇到无效歌曲，继续搜索");
				RestoreQueuePosition(currentSong, playMode);
				continue;
			}
			if (!attemptedIds.Add(candidate.Id))
			{
				Debug.WriteLine("[MainForm] 手动导航检测到循环，停止搜索");
				UpdateStatusBar("未找到可播放的歌曲");
				RestoreQueuePosition(currentSong, playMode);
				return true;
			}
			ManualNavigationAvailability availability = await PrepareSongForManualNavigationAsync(result);
			if (availability == ManualNavigationAvailability.Success)
			{
				if (!isNext)
				{
					await ExecutePlayPreviousResultAsync(result);
				}
				else
				{
					await ExecutePlayNextResultAsync(result);
				}
				return true;
			}
			string friendlyName = BuildFriendlySongName(candidate);
			if (availability == ManualNavigationAvailability.Missing)
			{
				string message = (string.IsNullOrEmpty(friendlyName) ? "已跳过无法播放的歌曲（官方资源不存在）" : ("已跳过：" + friendlyName + "（官方资源不存在）"));
				UpdateStatusBar(message);
				Debug.WriteLine("[MainForm] 手动" + (isNext ? "下一曲" : "上一曲") + "跳过缺失歌曲: " + candidate?.Id + " - " + friendlyName);
				RemoveSongFromQueueAndCaches(candidate);
				RestoreQueuePosition(currentSong, playMode);
				continue;
			}
			if (string.IsNullOrEmpty(friendlyName))
			{
				UpdateStatusBar("获取播放链接失败");
			}
			else
			{
				UpdateStatusBar("获取播放链接失败：" + friendlyName);
			}
			Debug.WriteLine("[MainForm] 手动导航获取播放链接失败: " + candidate?.Id);
			RestoreQueuePosition(currentSong, playMode);
			return true;
		}
		UpdateStatusBar("未找到可播放的歌曲");
		RestoreQueuePosition(currentSong, _audioEngine?.PlayMode ?? PlayMode.Loop);
		return true;
	}

	private void RestoreQueuePosition(SongInfo currentSong, PlayMode playMode)
	{
		if (currentSong != null && _playbackQueue != null)
		{
			_playbackQueue.AdvanceForPlayback(currentSong, playMode, _currentViewSource);
		}
	}

	private int CalculateManualNavigationLimit()
	{
		int num = 0;
		int num2 = 0;
		if (_playbackQueue != null)
		{
			IReadOnlyList<SongInfo> currentQueue = _playbackQueue.CurrentQueue;
			if (currentQueue != null)
			{
				num = currentQueue.Count;
			}
			IReadOnlyList<SongInfo> injectionChain = _playbackQueue.InjectionChain;
			if (injectionChain != null)
			{
				num2 = injectionChain.Count;
			}
		}
		int num3 = ((_playbackQueue != null && _playbackQueue.HasPendingInjection) ? 1 : 0);
		int num4 = checked(num + num2 + num3 + 3);
		return (num4 < 6) ? 6 : num4;
	}

	private async Task<ManualNavigationAvailability> PrepareSongForManualNavigationAsync(PlaybackMoveResult moveResult)
	{
		if (moveResult?.Song == null)
		{
			return ManualNavigationAvailability.Failed;
		}
		SongInfo song = moveResult.Song;
		string defaultQualityName = _config.DefaultQuality ?? "超清母带";
		QualityLevel selectedQuality = NeteaseApiClient.GetQualityLevelFromName(defaultQualityName);
		try
		{
			Dictionary<string, SongUrlInfo> urlResult = await GetSongUrlWithRetryAsync(new string[1] { song.Id }, selectedQuality, CancellationToken.None, 3, suppressStatusUpdates: true);
			if (urlResult != null && urlResult.TryGetValue(song.Id, out SongUrlInfo songUrl) && !string.IsNullOrEmpty(songUrl?.Url))
			{
				song.Url = songUrl.Url;
				song.Level = songUrl.Level;
				song.Size = songUrl.Size;
				return ManualNavigationAvailability.Success;
			}
			return ManualNavigationAvailability.Missing;
		}
		catch (SongResourceNotFoundException)
		{
			return ManualNavigationAvailability.Missing;
		}
		catch (Exception ex2)
		{
			Debug.WriteLine("[MainForm] 手动切换检查资源失败: " + ex2.Message);
			return ManualNavigationAvailability.Failed;
		}
	}

	private static string BuildFriendlySongName(SongInfo song)
	{
		if (song == null)
		{
			return string.Empty;
		}
		string text = song.Name ?? string.Empty;
		if (!string.IsNullOrWhiteSpace(song.Artist))
		{
			return text + " - " + song.Artist;
		}
		return text;
	}

	private async Task ExecutePlayPreviousResultAsync(PlaybackMoveResult result)
	{
		if (result?.Song != null)
		{
			switch (result.Route)
			{
			case PlaybackRoute.Injection:
				UpdateFocusForInjection(result.Song, result.InjectionIndex);
				break;
			case PlaybackRoute.Queue:
			case PlaybackRoute.ReturnToQueue:
				UpdateFocusForQueue(result.QueueIndex, result.Song);
				break;
			}
			await PlaySongDirectWithCancellation(result.Song, isAutoPlayback: true);
		}
	}

	private async Task ExecutePlayNextResultAsync(PlaybackMoveResult result)
	{
		if (result?.Song == null)
		{
			Debug.WriteLine("[MainForm] ExecutePlayNextResultAsync 收到空歌曲");
			return;
		}
		switch (result.Route)
		{
		case PlaybackRoute.PendingInjection:
			UpdateFocusForInjection(result.Song, result.InjectionIndex);
			await PlaySongDirectWithCancellation(result.Song);
			UpdateStatusBar("插播：" + result.Song.Name + " - " + result.Song.Artist);
			break;
		case PlaybackRoute.Injection:
			UpdateFocusForInjection(result.Song, result.InjectionIndex);
			await PlaySongDirectWithCancellation(result.Song, isAutoPlayback: true);
			break;
		case PlaybackRoute.Queue:
		case PlaybackRoute.ReturnToQueue:
			UpdateFocusForQueue(result.QueueIndex, result.Song);
			await PlaySongDirectWithCancellation(result.Song, isAutoPlayback: true);
			break;
		default:
			Debug.WriteLine("[MainForm] ExecutePlayNextResultAsync 未匹配可播放路由");
			break;
		}
	}

	private void HandleSequentialPlaybackCompleted()
	{
		SafeInvoke(delegate
		{
			currentSongLabel.Text = "未播放";
			UpdateStatusBar("顺序播放已完成");
			UpdatePlayButtonDescription(null);
			UpdateTrayIconTooltip(null);
		});
	}

	private bool RemoveSongFromQueueAndCaches(SongInfo song)
	{
		if (song == null)
		{
			return false;
		}
		return _playbackQueue.RemoveSongById(song.Id);
	}

	private void HandleSongResourceNotFoundDuringPlayback(SongInfo song, bool isAutoPlayback)
	{
		if (song != null)
		{
			// 缓存不可用状态，防止后续重复尝试
			song.IsAvailable = false;
			if (base.InvokeRequired)
			{
				BeginInvoke(new Action(ExecuteOnUiThread));
			}
			else
			{
				ExecuteOnUiThread();
			}
		}
		void ExecuteOnUiThread()
		{
			string text = song.Name;
			if (!string.IsNullOrWhiteSpace(song.Artist))
			{
				text = song.Name + " - " + song.Artist;
			}
			bool flag = RemoveSongFromQueueAndCaches(song);
			Debug.WriteLine($"[MainForm] RemoveSongById({song.Id}) => {flag}");
			if (isAutoPlayback)
			{
				string message = (string.IsNullOrEmpty(text) ? "已跳过无法播放的歌曲（官方资源不存在）" : ("已跳过：" + text + "（官方资源不存在）"));
				UpdateStatusBar(message);
				Debug.WriteLine("[MainForm] 跳过缺失歌曲(自动): " + song.Id + " - " + text);
				bool flag2 = _playbackQueue.CurrentQueue.Count > 0;
				bool flag3 = _playbackQueue.HasPendingInjection || _playbackQueue.IsInInjection;
				if (flag2 || flag3)
				{
					PlayNext(isManual: false);
				}
				else
				{
					_audioEngine?.Stop();
					playPauseButton.Text = "播放";
					UpdateTrayIconTooltip(null);
				}
			}
			else
			{
				string message2 = (string.IsNullOrEmpty(text) ? "该歌曲在官方曲库中不存在或已被移除" : ("无法播放：" + text + "（官方资源不存在）"));
				UpdateStatusBar(message2);
				Debug.WriteLine("[MainForm] 无法播放缺失歌曲(手动): " + song.Id + " - " + text);
				if (base.Visible)
				{
					string text2 = (string.IsNullOrEmpty(text) ? "该歌曲在网易云官方曲库中不存在或已被移除。" : (text + " 在网易云官方曲库中不存在或已被移除。"));
					MessageBox.Show(text2 + "\n\n请尝试播放其他歌曲。", "歌曲不可播放", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				}
				SyncPlayPauseButtonText();
				SongInfo songInfo = _audioEngine?.CurrentSong;
				if (songInfo != null)
				{
					UpdateTrayIconTooltip(songInfo);
					string message3 = (songInfo.IsTrial ? ("正在播放: " + songInfo.Name + " - " + songInfo.Artist + " [试听版]") : ("正在播放: " + songInfo.Name + " - " + songInfo.Artist));
					UpdateStatusBar(message3);
				}
				else
				{
					UpdateTrayIconTooltip(null);
				}
			}
		}
	}

	private void UpdateFocusForQueue(int queueIndex, SongInfo? expectedSong = null)
	{
		string queueSource = _playbackQueue.QueueSource;
		int num = queueIndex;
		if (expectedSong != null && _currentSongs != null)
		{
			for (int i = 0; i < _currentSongs.Count; i = checked(i + 1))
			{
				if (_currentSongs[i]?.Id == expectedSong.Id)
				{
					num = i;
					break;
				}
			}
		}
		if (queueSource == _currentViewSource && num >= 0 && num < resultListView.Items.Count)
		{
			_lastListViewFocusedIndex = num;
			if (base.Visible)
			{
				ListViewItem listViewItem = resultListView.Items[num];
				listViewItem.Selected = true;
				listViewItem.Focused = true;
				resultListView.EnsureVisible(num);
			}
			Debug.WriteLine($"[MainForm] 焦点跟随（原队列）: 队列索引={queueIndex}, 视图索引={num}, 来源={queueSource}, 窗口可见={base.Visible}");
		}
		else
		{
			Debug.WriteLine(string.Format("[MainForm] 焦点不跟随: 队列来源={0}, 当前浏览={1}, 队列索引={2}, 期望歌曲={3}", queueSource, _currentViewSource, queueIndex, expectedSong?.Id ?? "null"));
		}
	}

	private void UpdateFocusForInjection(SongInfo song, int viewIndex)
	{
		if (song == null)
		{
			return;
		}
		if (_playbackQueue.TryGetInjectionSource(song.Id, out string source) && source == _currentViewSource)
		{
			int num = -1;
			if (_currentSongs != null)
			{
				for (int i = 0; i < _currentSongs.Count; i = checked(i + 1))
				{
					if (_currentSongs[i]?.Id == song.Id)
					{
						num = i;
						break;
					}
				}
			}
			if (num >= 0 && num < resultListView.Items.Count)
			{
				_lastListViewFocusedIndex = num;
				if (base.Visible)
				{
					ListViewItem listViewItem = resultListView.Items[num];
					listViewItem.Selected = true;
					listViewItem.Focused = true;
					resultListView.EnsureVisible(num);
				}
				Debug.WriteLine($"[MainForm] 焦点跟随（插播）: 索引={num}, 插播索引={viewIndex}, 窗口可见={base.Visible}, 来源={source}");
			}
		}
		else
		{
			Debug.WriteLine("[MainForm] 插播歌曲不在当前视图: " + (song?.Name ?? "null") + ", 当前视图=" + _currentViewSource);
		}
	}

	private void InitializePlaybackReportingService()
	{
		if (_apiClient != null)
		{
			if (_playbackReportingService == null)
			{
				_playbackReportingService = new PlaybackReportingService(_apiClient);
			}
			_playbackReportingService.UpdateSettings(_config);
		}
	}

	private void PreparePlaybackReportingForNextSong(SongInfo? nextSong)
	{
		if (nextSong != null)
		{
			PlaybackReportContext activePlaybackReport;
			lock (_playbackReportingLock)
			{
				activePlaybackReport = _activePlaybackReport;
			}
			if (activePlaybackReport != null && !string.Equals(activePlaybackReport.SongId, nextSong.Id, StringComparison.OrdinalIgnoreCase))
			{
				CompleteActivePlaybackSession(PlaybackEndReason.Interrupted);
			}
		}
	}

	private void BeginPlaybackReportingSession(SongInfo? song)
	{
		if (song == null || !CanReportPlayback(song))
		{
			ClearPlaybackReportingSession();
			return;
		}
		PlaybackSourceContext source = BuildPlaybackSourceContext(song);
		int durationSeconds = NormalizeDurationSeconds(song);
		string resourceType = (song.IsPodcastEpisode ? "djprogram" : "song");
		string contentOverride = (song.IsPodcastEpisode ? BuildPodcastContentOverride(song) : null);
		PlaybackReportContext playbackReportContext = new PlaybackReportContext(song.Id, song.Name ?? string.Empty, song.Artist ?? string.Empty, source, durationSeconds, song.IsTrial, resourceType)
		{
			StartedAt = DateTimeOffset.UtcNow,
			ContentOverride = contentOverride
		};
		lock (_playbackReportingLock)
		{
			_activePlaybackReport = playbackReportContext;
		}
		_playbackReportingService?.ReportSongStarted(playbackReportContext);
	}

	private void CompleteActivePlaybackSession(PlaybackEndReason reason, string? expectedSongId = null)
	{
		PlaybackReportContext activePlaybackReport;
		lock (_playbackReportingLock)
		{
			activePlaybackReport = _activePlaybackReport;
			if (activePlaybackReport == null || (!string.IsNullOrWhiteSpace(expectedSongId) && !string.Equals(activePlaybackReport.SongId, expectedSongId, StringComparison.OrdinalIgnoreCase)))
			{
				return;
			}
			if (activePlaybackReport.HasCompleted)
			{
				_activePlaybackReport = null;
				return;
			}
			activePlaybackReport.HasCompleted = true;
			activePlaybackReport.PlayedSeconds = DeterminePlaybackSeconds(activePlaybackReport);
			_activePlaybackReport = null;
		}
		_playbackReportingService?.ReportSongCompleted(activePlaybackReport, reason);
	}

	private double DeterminePlaybackSeconds(PlaybackReportContext session)
	{
		double num = 0.0;
		try
		{
			if (_audioEngine?.CurrentSong?.Id == session.SongId)
			{
				num = _audioEngine.GetPosition();
				if (num <= 0.0)
				{
					num = _audioEngine.GetDuration();
				}
			}
		}
		catch
		{
			num = 0.0;
		}
		if (num <= 0.0)
		{
			num = (DateTimeOffset.UtcNow - session.StartedAt).TotalSeconds;
		}
		if (num <= 0.0 && session.DurationSeconds > 0)
		{
			num = session.DurationSeconds;
		}
		if (session.DurationSeconds > 0 && num > (double)session.DurationSeconds)
		{
			num = session.DurationSeconds;
		}
		return Math.Max(1.0, num);
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
		return _accountState?.IsLoggedIn ?? false;
	}

	private PlaybackSourceContext BuildPlaybackSourceContext(SongInfo song)
	{
		string source = "list";
		string sourceId = null;
		if (song.IsPodcastEpisode || IsPodcastEpisodeView())
		{
			long num = ResolvePodcastRadioId(song);
			if (num > 0)
			{
				sourceId = num.ToString(CultureInfo.InvariantCulture);
			}
			else
			{
				string podcastRadioName = song.PodcastRadioName;
				if (!string.IsNullOrWhiteSpace(podcastRadioName))
				{
					sourceId = podcastRadioName;
				}
			}
			return new PlaybackSourceContext("djradio", sourceId);
		}
		if (_currentPlaylist != null && !string.IsNullOrWhiteSpace(_currentPlaylist.Id))
		{
			source = "list";
			sourceId = _currentPlaylist.Id;
		}
		else if (!string.IsNullOrWhiteSpace(song.AlbumId))
		{
			source = "album";
			sourceId = song.AlbumId;
		}
		else if (!string.IsNullOrWhiteSpace(_currentViewSource))
		{
			string[] array = _currentViewSource.Split(new char[1] { ':' }, 2, StringSplitOptions.RemoveEmptyEntries);
			if (array.Length == 2)
			{
				sourceId = array[1];
				source = ((array[0].IndexOf("album", StringComparison.OrdinalIgnoreCase) >= 0) ? "album" : "list");
			}
		}
		return new PlaybackSourceContext(source, sourceId);
	}

	private long ResolvePodcastRadioId(SongInfo song)
	{
		if (song.PodcastRadioId > 0)
		{
			return song.PodcastRadioId;
		}
		PodcastRadioInfo? currentPodcast = _currentPodcast;
		if (currentPodcast != null && currentPodcast.Id > 0)
		{
			return _currentPodcast.Id;
		}
		if (!string.IsNullOrWhiteSpace(_currentViewSource) && _currentViewSource.StartsWith("podcast:", StringComparison.OrdinalIgnoreCase))
		{
			string[] array = _currentViewSource.Split(new char[1] { ':' }, StringSplitOptions.RemoveEmptyEntries);
			if (array.Length >= 2 && long.TryParse(array[1], out var result))
			{
				return result;
			}
		}
		return 0L;
	}

	private string? BuildPodcastContentOverride(SongInfo song)
	{
		if (song == null)
		{
			return null;
		}
		long num = song.PodcastProgramId;
		if (num <= 0 && long.TryParse(song.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
		{
			num = result;
		}
		if (num <= 0)
		{
			return null;
		}
		return $"id={num}";
	}

	private int NormalizeDurationSeconds(SongInfo song)
	{
		if (song == null)
		{
			return 0;
		}
		int num = song.Duration;
		if (num > 43200)
		{
			num /= 1000;
		}
		if (num <= 0)
		{
			try
			{
				double num2 = _audioEngine?.GetDuration() ?? 0.0;
				if (num2 > 0.0)
				{
					num = checked((int)Math.Round(num2));
				}
			}
			catch
			{
				num = 0;
			}
		}
		if (num <= 0)
		{
			num = 60;
		}
		return num;
	}

	private void InitializeCommandQueueSystem()
	{
	}

	private void DisposeCommandQueueSystem()
	{
	}

	private async Task<CommandResult> ExecuteNextCommandAsync(PlaybackCommand command, CancellationToken ct)
	{
		try
		{
			bool success = false;
			await Task.Run(delegate
			{
				if (base.InvokeRequired)
				{
					Invoke((Action)delegate
					{
						PlayNext();
						success = true;
					});
				}
				else
				{
					PlayNext();
					success = true;
				}
			}, ct).ConfigureAwait(continueOnCapturedContext: false);
			return success ? CommandResult.Success : CommandResult.Error("下一曲执行失败");
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			return CommandResult.Error(ex2);
		}
	}

	private async Task<CommandResult> ExecutePreviousCommandAsync(PlaybackCommand command, CancellationToken ct)
	{
		try
		{
			bool success = false;
			await Task.Run(delegate
			{
				if (base.InvokeRequired)
				{
					Invoke((Action)delegate
					{
						PlayPrevious();
						success = true;
					});
				}
				else
				{
					PlayPrevious();
					success = true;
				}
			}, ct).ConfigureAwait(continueOnCapturedContext: false);
			return success ? CommandResult.Success : CommandResult.Error("上一曲执行失败");
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			return CommandResult.Error(ex2);
		}
	}

	private void OnCommandStateChanged(object sender, CommandStateChangedEventArgs e)
	{
		Debug.WriteLine($"[MainForm] 命令状态变更: {e.Command.Type} - {e.State}");
		SafeInvoke(delegate
		{
			switch (e.State)
			{
			case CommandState.Executing:
				UpdateStatusBarForCommandExecuting(e.Command);
				break;
			case CommandState.Completed:
				UpdateStatusBarForCommandCompleted(e.Command);
				break;
			case CommandState.Cancelled:
				UpdateStatusBar("操作已取消");
				break;
			case CommandState.Failed:
				UpdateStatusBar("操作失败: " + (e.Message ?? "未知错误"));
				break;
			}
		});
	}

	private void OnPlaybackStateChanged(object sender, StateTransitionEventArgs e)
	{
		Debug.WriteLine($"[MainForm] 播放状态变更: {e.OldState} → {e.NewState}");
		SafeInvoke(delegate
		{
			UpdateUIForPlaybackState(e.NewState);
			bool isPlaying = e.NewState == PlaybackState.Playing || e.NewState == PlaybackState.Buffering || e.NewState == PlaybackState.Loading;
			DownloadBandwidthCoordinator.Instance.NotifyPlaybackStateChanged(isPlaying);
		});
	}

	private void UpdateStatusBarForCommandExecuting(PlaybackCommand command)
	{
		switch (command.Type)
		{
		case CommandType.Play:
			if (command.Payload is SongInfo songInfo)
			{
				UpdateStatusBar("正在播放: " + songInfo.Name);
			}
			else
			{
				UpdateStatusBar("正在播放...");
			}
			break;
		case CommandType.Pause:
			UpdateStatusBar("暂停中...");
			break;
		case CommandType.Resume:
			UpdateStatusBar("恢复播放...");
			break;
		case CommandType.Seek:
			if (command.Payload is double seconds)
			{
				UpdateStatusBar("跳转到 " + FormatTimeFromSeconds(seconds));
			}
			else
			{
				UpdateStatusBar("跳转中...");
			}
			break;
		case CommandType.Next:
			UpdateStatusBar("切换下一曲...");
			break;
		case CommandType.Previous:
			UpdateStatusBar("切换上一曲...");
			break;
		case CommandType.Stop:
			break;
		}
	}

	private void UpdateStatusBarForCommandCompleted(PlaybackCommand command)
	{
		switch (command.Type)
		{
		case CommandType.Play:
			if (command.Payload is SongInfo songInfo)
			{
				UpdateStatusBar("正在播放: " + songInfo.Name + " - " + songInfo.Artist);
			}
			else
			{
				UpdateStatusBar("正在播放");
			}
			break;
		case CommandType.Pause:
			UpdateStatusBar("已暂停");
			break;
		case CommandType.Resume:
			UpdateStatusBar("正在播放");
			break;
		case CommandType.Seek:
			UpdateStatusBar("跳转完成");
			break;
		case CommandType.Next:
		case CommandType.Previous:
			break;
		case CommandType.Stop:
			break;
		}
	}

	private void UpdateUIForPlaybackState(PlaybackState state)
	{
		switch (state)
		{
		case PlaybackState.Idle:
			playPauseButton.Text = "播放";
			playPauseButton.Enabled = true;
			break;
		case PlaybackState.Loading:
			playPauseButton.Text = "加载中...";
			playPauseButton.Enabled = false;
			break;
		case PlaybackState.Buffering:
			playPauseButton.Text = "缓冲中...";
			playPauseButton.Enabled = false;
			break;
		case PlaybackState.Playing:
			playPauseButton.Text = "暂停";
			playPauseButton.Enabled = true;
			break;
		case PlaybackState.Paused:
			playPauseButton.Text = "播放";
			playPauseButton.Enabled = true;
			break;
		case PlaybackState.Stopped:
			playPauseButton.Text = "播放";
			playPauseButton.Enabled = true;
			break;
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
			if (!TryResolveSongIdForLibraryActions(song, "收藏歌曲", out string targetSongId))
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
				MessageBox.Show("收藏歌曲失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
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
		if (!TryResolveSongIdForLibraryActions(song, "取消收藏歌曲", out string targetSongId))
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
						Exception refreshEx = ex;
						Debug.WriteLine($"[UI] 刷新我喜欢的音乐列表失败: {refreshEx}");
					}
				}
				return true;
			}
			MessageBox.Show("取消收藏失败，请检查网络或稍后重试。", "失败", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("取消收藏失败");
		}
		catch (Exception ex2)
		{
			MessageBox.Show("取消收藏失败: " + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
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
		ListViewItem selectedItem = ((resultListView.SelectedItems.Count > 0) ? resultListView.SelectedItems[0] : null);
		if (selectedItem == null)
		{
			return;
		}
		SongInfo song = null;
		object tag = selectedItem.Tag;
		int index = default(int);
		int num;
		if (tag is int)
		{
			index = (int)tag;
			if (index >= 0)
			{
				num = ((index < _currentSongs.Count) ? 1 : 0);
				goto IL_0202;
			}
		}
		num = 0;
		goto IL_0202;
		IL_0202:
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
		if (song == null || !TryResolveSongIdForLibraryActions(song, "从歌单中移除歌曲", out string targetSongId))
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
					}
					catch (Exception ex)
					{
						Exception refreshEx = ex;
						Debug.WriteLine($"[UI] 刷新歌单失败: {refreshEx}");
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
			Exception ex2 = ex;
			MessageBox.Show("从歌单中移除歌曲失败: " + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
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
			if (!TryResolveSongIdForLibraryActions(song, "添加到歌单", out string targetSongId))
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
				MessageBox.Show("添加歌曲到歌单失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
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

	private void InitializeDownload()
	{
		_downloadManager = DownloadManager.Instance;
		_downloadManager.Initialize(_apiClient);
		UploadManager instance = UploadManager.Instance;
		instance.Initialize(_apiClient);
		instance.TaskCompleted -= OnCloudUploadTaskCompleted;
		instance.TaskCompleted += OnCloudUploadTaskCompleted;
		instance.TaskFailed -= OnCloudUploadTaskFailed;
		instance.TaskFailed += OnCloudUploadTaskFailed;
	}

	internal void OpenDownloadDirectory_Click(object? sender, EventArgs e)
	{
		try
		{
			ConfigModel configModel = _configManager.Load();
			string fullDownloadPath = ConfigManager.GetFullDownloadPath(configModel.DownloadDirectory);
			if (!Directory.Exists(fullDownloadPath))
			{
				Directory.CreateDirectory(fullDownloadPath);
			}
			Process.Start("explorer.exe", fullDownloadPath);
		}
		catch (Exception ex)
		{
			MessageBox.Show("无法打开下载目录：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
		}
	}

	internal void ChangeDownloadDirectory_Click(object? sender, EventArgs e)
	{
		try
		{
			ConfigModel configModel = _configManager.Load();
			string fullDownloadPath = ConfigManager.GetFullDownloadPath(configModel.DownloadDirectory);
			using FolderBrowserDialog folderBrowserDialog = new FolderBrowserDialog();
			folderBrowserDialog.Description = "选择下载目录";
			folderBrowserDialog.SelectedPath = fullDownloadPath;
			if (folderBrowserDialog.ShowDialog() == DialogResult.OK)
			{
				string text = (configModel.DownloadDirectory = folderBrowserDialog.SelectedPath);
				_configManager.Save(configModel);
				MessageBox.Show("下载目录已更改为：\n" + text, "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			}
		}
		catch (Exception ex)
		{
			MessageBox.Show("更改下载目录失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
		}
	}

	internal void OpenDownloadManager_Click(object? sender, EventArgs e)
	{
		try
		{
			if (_downloadManagerForm != null && !_downloadManagerForm.IsDisposed)
			{
				if (_downloadManagerForm.WindowState == FormWindowState.Minimized)
				{
					_downloadManagerForm.WindowState = FormWindowState.Normal;
				}
				_downloadManagerForm.BringToFront();
				_downloadManagerForm.Activate();
				_downloadManagerForm.Focus();
			}
			else
			{
				_downloadManagerForm = new DownloadManagerForm();
				_downloadManagerForm.FormClosed += DownloadManagerForm_FormClosed;
				_downloadManagerForm.Show(this);
				_downloadManagerForm.BringToFront();
				_downloadManagerForm.Activate();
			}
		}
		catch (Exception ex)
		{
			MessageBox.Show("无法打开下载管理器：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
		}
	}

	private void DownloadManagerForm_FormClosed(object? sender, FormClosedEventArgs e)
	{
		if (_downloadManagerForm != null)
		{
			_downloadManagerForm.FormClosed -= DownloadManagerForm_FormClosed;
			_downloadManagerForm = null;
		}
	}

	internal async void DownloadSong_Click(object? sender, EventArgs e)
	{
		SongInfo song = GetSelectedSongFromContextMenu(sender);
		if (song == null)
		{
			ShowContextSongMissingMessage("下载的歌曲");
			return;
		}
		try
		{
			QualityLevel quality = GetCurrentQuality();
			string sourceList = GetCurrentViewName();
			if (await _downloadManager.AddSongDownloadAsync(song, quality, sourceList) != null)
			{
				MessageBox.Show("已添加到下载队列：\n" + song.Name + " - " + song.Artist, "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			}
			else
			{
				MessageBox.Show("添加下载任务失败", "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			}
		}
		catch (Exception ex)
		{
			MessageBox.Show("下载失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
		}
	}

	internal async void DownloadLyrics_Click(object? sender, EventArgs e)
	{
		SongInfo song = GetSelectedSongFromContextMenu(sender);
		if (song == null || string.IsNullOrWhiteSpace(song.Id))
		{
			ShowContextSongMissingMessage("下载歌词的歌曲");
			return;
		}
		try
		{
			LyricInfo lyricInfo = await _apiClient.GetLyricsAsync(song.Id);
			if (lyricInfo == null || string.IsNullOrWhiteSpace(lyricInfo.Lyric))
			{
				MessageBox.Show("该歌曲没有歌词", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				return;
			}
			string sourceList = GetCurrentViewName();
			if (await _downloadManager.AddLyricDownloadAsync(song, sourceList, lyricInfo.Lyric) != null)
			{
				MessageBox.Show("已添加歌词下载任务：\n" + song.Name + " - " + song.Artist, "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			}
			else
			{
				MessageBox.Show("歌词下载任务创建失败", "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			}
		}
		catch (Exception ex)
		{
			MessageBox.Show("下载歌词失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
		}
	}

	internal async void BatchDownloadSongs_Click(object? sender, EventArgs e)
	{
		if (_currentSongs == null || _currentSongs.Count == 0)
		{
			MessageBox.Show("当前列表为空", "提示", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
			return;
		}
		checked
		{
			try
			{
				List<SongInfo> songs = new List<SongInfo>(_currentSongs);
				List<string> displayNames = new List<string>();
				for (int i = 0; i < songs.Count; i++)
				{
					SongInfo song = songs[i];
					displayNames.Add($"{i + 1}. {song.Name} - {song.Artist}");
				}
				if (songs.Count == 0)
				{
					MessageBox.Show("当前列表中没有可下载的歌曲", "提示", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
					return;
				}
				string viewName = GetCurrentViewName();
				BatchDownloadDialog dialog = new BatchDownloadDialog(displayNames, "批量下载 - " + viewName);
				if (dialog.ShowDialog() != DialogResult.OK)
				{
					return;
				}
				List<int> selectedIndices = dialog.SelectedIndices;
				if (selectedIndices.Count != 0)
				{
					List<SongInfo> selectedSongs = selectedIndices.Select((int index) => songs[index]).ToList();
					List<int> originalIndices = selectedIndices.Select((int num) => num + 1).ToList();
					QualityLevel quality = GetCurrentQuality();
					MessageBox.Show($"已添加 {(await _downloadManager.AddBatchDownloadAsync(selectedSongs, quality, viewName, viewName, originalIndices)).Count}/{selectedSongs.Count} 个下载任务", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				}
			}
			catch (Exception ex)
			{
				MessageBox.Show("批量下载失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			}
		}
	}

	internal async void DownloadPlaylist_Click(object? sender, EventArgs e)
	{
		if (resultListView.SelectedItems.Count == 0)
		{
			return;
		}
		ListViewItem selectedItem = resultListView.SelectedItems[0];
		object tag = selectedItem.Tag;
		if (!(tag is PlaylistInfo playlist))
		{
			return;
		}
		checked
		{
			try
			{
				Cursor originalCursor = Cursor.Current;
				Cursor.Current = Cursors.WaitCursor;
				try
				{
					PlaylistInfo playlistDetail = await _apiClient.GetPlaylistDetailAsync(playlist.Id);
					if (playlistDetail == null || playlistDetail.Songs == null || playlistDetail.Songs.Count == 0)
					{
						MessageBox.Show("无法获取歌单歌曲列表或歌单为空", "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
						return;
					}
					List<string> displayNames = new List<string>();
					for (int i = 0; i < playlistDetail.Songs.Count; i++)
					{
						SongInfo song = playlistDetail.Songs[i];
						displayNames.Add($"{i + 1}. {song.Name} - {song.Artist}");
					}
					BatchDownloadDialog dialog = new BatchDownloadDialog(displayNames, "下载歌单 - " + playlist.Name);
					if (dialog.ShowDialog() == DialogResult.OK)
					{
						List<int> selectedIndices = dialog.SelectedIndices;
						if (selectedIndices.Count == 0)
						{
							return;
						}
						List<SongInfo> selectedSongs = selectedIndices.Select((int index) => playlistDetail.Songs[index]).ToList();
						List<int> originalIndices = selectedIndices.Select((int num) => num + 1).ToList();
						QualityLevel quality = GetCurrentQuality();
						MessageBox.Show($"已添加 {(await _downloadManager.AddBatchDownloadAsync(selectedSongs, quality, playlist.Name, playlist.Name, originalIndices)).Count}/{selectedSongs.Count} 个下载任务\n歌单：{playlist.Name}", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
					}
				}
				finally
				{
					Cursor.Current = originalCursor;
				}
			}
			catch (Exception ex)
			{
				MessageBox.Show("下载歌单失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			}
		}
	}

	internal async void DownloadAlbum_Click(object? sender, EventArgs e)
	{
		if (resultListView.SelectedItems.Count == 0)
		{
			return;
		}
		ListViewItem selectedItem = resultListView.SelectedItems[0];
		object tag = selectedItem.Tag;
		if (!(tag is AlbumInfo album))
		{
			return;
		}
		checked
		{
			try
			{
				Cursor originalCursor = Cursor.Current;
				Cursor.Current = Cursors.WaitCursor;
				try
				{
					List<SongInfo> songs = await _apiClient.GetAlbumSongsAsync(album.Id);
					if (songs == null || songs.Count == 0)
					{
						MessageBox.Show("无法获取专辑歌曲列表或专辑为空", "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
						return;
					}
					List<string> displayNames = new List<string>();
					for (int i = 0; i < songs.Count; i++)
					{
						SongInfo song = songs[i];
						displayNames.Add($"{i + 1}. {song.Name} - {song.Artist}");
					}
					BatchDownloadDialog dialog = new BatchDownloadDialog(displayNames, "下载专辑 - " + album.Name);
					if (dialog.ShowDialog() == DialogResult.OK)
					{
						List<int> selectedIndices = dialog.SelectedIndices;
						if (selectedIndices.Count == 0)
						{
							return;
						}
						List<SongInfo> selectedSongs = selectedIndices.Select((int index) => songs[index]).ToList();
						List<int> originalIndices = selectedIndices.Select((int num) => num + 1).ToList();
						QualityLevel quality = GetCurrentQuality();
						List<DownloadTask> tasks = await _downloadManager.AddBatchDownloadAsync(selectedSongs, quality, album.Name + " - " + album.Artist, album.Name + " - " + album.Artist, originalIndices);
						MessageBox.Show($"已添加 {tasks.Count}/{selectedSongs.Count} 个下载任务\n专辑：{album.Name} - {album.Artist}", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
					}
				}
				finally
				{
					Cursor.Current = originalCursor;
				}
			}
			catch (Exception ex)
			{
				MessageBox.Show("下载专辑失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			}
		}
	}

	internal async void DownloadPodcast_Click(object? sender, EventArgs e)
	{
		PodcastRadioInfo podcast = GetSelectedPodcastFromContextMenu(sender);
		if (podcast == null || podcast.Id <= 0)
		{
			MessageBox.Show("请选择要下载的播客。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			return;
		}
		checked
		{
			try
			{
				Cursor originalCursor = Cursor.Current;
				Cursor.Current = Cursors.WaitCursor;
				try
				{
					List<PodcastEpisodeInfo> episodes = await FetchAllPodcastEpisodesAsync(podcast.Id);
					if (episodes == null || episodes.Count == 0)
					{
						MessageBox.Show("该播客暂无可下载的节目。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
						return;
					}
					List<string> displayNames = new List<string>();
					for (int i = 0; i < episodes.Count; i++)
					{
						PodcastEpisodeInfo episode = episodes[i];
						string meta = string.Empty;
						if (episode.PublishTime.HasValue)
						{
							meta = episode.PublishTime.Value.ToString("yyyy-MM-dd");
						}
						if (episode.Duration > TimeSpan.Zero)
						{
							string durationLabel = $"{episode.Duration:mm\\:ss}";
							meta = (string.IsNullOrEmpty(meta) ? durationLabel : (meta + " | " + durationLabel));
						}
						string hostLabel = string.Empty;
						if (!string.IsNullOrWhiteSpace(episode.RadioName))
						{
							hostLabel = episode.RadioName;
						}
						if (!string.IsNullOrWhiteSpace(episode.DjName))
						{
							hostLabel = (string.IsNullOrWhiteSpace(hostLabel) ? episode.DjName : (hostLabel + " / " + episode.DjName));
						}
						string line = $"{i + 1}. {episode.Name}";
						if (!string.IsNullOrWhiteSpace(meta))
						{
							line = line + " (" + meta + ")";
						}
						if (!string.IsNullOrWhiteSpace(hostLabel))
						{
							line = line + " - " + hostLabel;
						}
						displayNames.Add(line);
					}
					string safeName = (string.IsNullOrWhiteSpace(podcast.Name) ? $"播客_{podcast.Id}" : podcast.Name);
					BatchDownloadDialog dialog = new BatchDownloadDialog(displayNames, "下载播客 - " + safeName);
					if (dialog.ShowDialog() != DialogResult.OK || dialog.SelectedIndices.Count == 0)
					{
						return;
					}
					List<int> selectedIndices = dialog.SelectedIndices;
					List<SongInfo> selectedSongs = new List<SongInfo>();
					List<int> originalIndices = new List<int>();
					foreach (int index in selectedIndices)
					{
						if (index >= 0 && index < episodes.Count)
						{
							SongInfo song = episodes[index].Song;
							if (song != null)
							{
								selectedSongs.Add(song);
								originalIndices.Add(index + 1);
							}
						}
					}
					if (selectedSongs.Count == 0)
					{
						MessageBox.Show("选中的节目缺少可下载的音频信息。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
						return;
					}
					QualityLevel quality = GetCurrentQuality();
					MessageBox.Show(string.Format("已添加 {0}/{1} 个下载任务\n播客：{2}", (await _downloadManager.AddBatchDownloadAsync(selectedSongs, quality, "播客 - " + safeName, safeName, originalIndices)).Count, selectedSongs.Count, safeName), "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				}
				finally
				{
					Cursor.Current = originalCursor;
				}
			}
			catch (Exception ex)
			{
				MessageBox.Show("下载播客失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			}
		}
	}

	private QualityLevel GetCurrentQuality()
	{
		ConfigModel configModel = _configManager.Load();
		string defaultQuality = configModel.DefaultQuality;
		return NeteaseApiClient.GetQualityLevelFromName(defaultQuality);
	}

	private string GetCurrentViewName()
	{
		if (!string.IsNullOrWhiteSpace(resultListView.AccessibleName))
		{
			return resultListView.AccessibleName.Trim();
		}
		return "下载";
	}

	internal async void DownloadCategory_Click(object? sender, EventArgs e)
	{
		if (resultListView.SelectedItems.Count == 0)
		{
			return;
		}
		ListViewItem selectedItem = resultListView.SelectedItems[0];
		object tag = selectedItem.Tag;
		ListItemInfo listItem = tag as ListItemInfo;
		if (listItem == null || listItem.Type != ListItemType.Category)
		{
			return;
		}
		try
		{
			Cursor originalCursor = Cursor.Current;
			Cursor.Current = Cursors.WaitCursor;
			try
			{
				string categoryId = listItem.CategoryId;
				string categoryName = listItem.CategoryName ?? listItem.CategoryId;
				QualityLevel quality = GetCurrentQuality();
				int totalTasks;
				switch (categoryId)
				{
				case "user_liked_songs":
					totalTasks = await DownloadSongListCategory(categoryName, async delegate
					{
						UserAccountInfo userInfo = await _apiClient.GetUserAccountAsync();
						if (userInfo == null || userInfo.UserId <= 0)
						{
							throw new Exception("获取用户信息失败");
						}
						List<string> likedIds = await _apiClient.GetUserLikedSongsAsync(userInfo.UserId);
						if (likedIds == null || likedIds.Count == 0)
						{
							throw new Exception("您还没有喜欢的歌曲");
						}
						List<SongInfo> allSongs = new List<SongInfo>();
						for (int i = 0; i < likedIds.Count; i = checked(i + 100))
						{
							string[] batchIds = likedIds.Skip(i).Take(100).ToArray();
							List<SongInfo> songs = await _apiClient.GetSongDetailAsync(batchIds);
							if (songs != null)
							{
								allSongs.AddRange(songs);
							}
						}
						return allSongs;
					}, quality);
					break;
				case "daily_recommend_songs":
					totalTasks = await DownloadSongListCategory(categoryName, async delegate
					{
						List<SongInfo> songs = await _apiClient.GetDailyRecommendSongsAsync();
						if (songs == null || songs.Count == 0)
						{
							throw new Exception("获取每日推荐歌曲失败");
						}
						return songs;
					}, quality);
					break;
				case "personalized_newsongs":
					totalTasks = await DownloadSongListCategory(categoryName, async delegate
					{
						List<SongInfo> songs = await _apiClient.GetPersonalizedNewSongsAsync(20);
						if (songs == null || songs.Count == 0)
						{
							throw new Exception("获取推荐新歌失败");
						}
						return songs;
					}, quality);
					break;
				case "user_playlists":
					totalTasks = await DownloadPlaylistListCategory(categoryName, async delegate
					{
						if (string.IsNullOrEmpty(_accountState?.UserId))
						{
							throw new Exception("请先登录");
						}
						long userId = long.Parse(_accountState.UserId);
						List<PlaylistInfo> playlists;
						(playlists, _) = await _apiClient.GetUserPlaylistsAsync(userId);
						if (playlists == null || playlists.Count == 0)
						{
							throw new Exception("您还没有歌单");
						}
						return playlists;
					}, quality);
					break;
				case "toplist":
					totalTasks = await DownloadPlaylistListCategory(categoryName, async delegate
					{
						List<PlaylistInfo> toplists = await _apiClient.GetToplistAsync();
						if (toplists == null || toplists.Count == 0)
						{
							throw new Exception("获取排行榜失败");
						}
						return toplists;
					}, quality);
					break;
				case "daily_recommend_playlists":
					totalTasks = await DownloadPlaylistListCategory(categoryName, async delegate
					{
						List<PlaylistInfo> playlists = await _apiClient.GetDailyRecommendPlaylistsAsync();
						if (playlists == null || playlists.Count == 0)
						{
							throw new Exception("获取每日推荐歌单失败");
						}
						return playlists;
					}, quality);
					break;
				case "personalized_playlists":
					totalTasks = await DownloadPlaylistListCategory(categoryName, async delegate
					{
						List<PlaylistInfo> playlists = await _apiClient.GetPersonalizedPlaylistsAsync();
						if (playlists == null || playlists.Count == 0)
						{
							throw new Exception("获取推荐歌单失败");
						}
						return playlists;
					}, quality);
					break;
				case "user_albums":
					totalTasks = await DownloadAlbumListCategory(categoryName, async delegate
					{
						(List<AlbumInfo>, int) tuple = await _apiClient.GetUserAlbumsAsync();
						List<AlbumInfo> albums;
						(albums, _) = tuple;
						_ = tuple.Item2;
						if (albums == null || albums.Count == 0)
						{
							throw new Exception("您还没有收藏专辑");
						}
						return albums;
					}, quality);
					break;
				case "daily_recommend":
					await DownloadMixedCategory(categoryName, () => new List<ListItemInfo>
					{
						new ListItemInfo
						{
							Type = ListItemType.Category,
							CategoryId = "daily_recommend_songs",
							CategoryName = "每日推荐歌曲",
							CategoryDescription = "根据您的听歌习惯推荐的歌曲"
						},
						new ListItemInfo
						{
							Type = ListItemType.Category,
							CategoryId = "daily_recommend_playlists",
							CategoryName = "每日推荐歌单",
							CategoryDescription = "根据您的听歌习惯推荐的歌单"
						}
					}, quality);
					return;
				case "personalized":
					await DownloadMixedCategory(categoryName, () => new List<ListItemInfo>
					{
						new ListItemInfo
						{
							Type = ListItemType.Category,
							CategoryId = "personalized_playlists",
							CategoryName = "推荐歌单",
							CategoryDescription = "根据您的听歌习惯推荐的歌单"
						},
						new ListItemInfo
						{
							Type = ListItemType.Category,
							CategoryId = "personalized_newsongs",
							CategoryName = "推荐新歌",
							CategoryDescription = "最新发行的歌曲推荐"
						}
					}, quality);
					return;
				case "user_play_record":
					await DownloadMixedCategory(categoryName, () => new List<ListItemInfo>
					{
						new ListItemInfo
						{
							Type = ListItemType.Category,
							CategoryId = "user_play_record_week",
							CategoryName = "周榜单",
							CategoryDescription = "最近一周的听歌排行"
						},
						new ListItemInfo
						{
							Type = ListItemType.Category,
							CategoryId = "user_play_record_all",
							CategoryName = "全部时间",
							CategoryDescription = "所有时间的听歌排行"
						}
					}, quality);
					return;
				case "user_play_record_week":
					totalTasks = await DownloadSongListCategory(categoryName, async delegate
					{
						UserAccountInfo userInfo = await _apiClient.GetUserAccountAsync();
						if (userInfo == null || userInfo.UserId <= 0)
						{
							throw new Exception("获取用户信息失败");
						}
						List<(SongInfo song, int playCount)> playRecords = await _apiClient.GetUserPlayRecordAsync(userInfo.UserId, 1);
						if (playRecords == null || playRecords.Count == 0)
						{
							throw new Exception("暂无周榜单听歌记录");
						}
						return playRecords.Select<(SongInfo, int), SongInfo>(((SongInfo song, int playCount) r) => r.song).ToList();
					}, quality);
					break;
				case "user_play_record_all":
					totalTasks = await DownloadSongListCategory(categoryName, async delegate
					{
						UserAccountInfo userInfo = await _apiClient.GetUserAccountAsync();
						if (userInfo == null || userInfo.UserId <= 0)
						{
							throw new Exception("获取用户信息失败");
						}
						List<(SongInfo song, int playCount)> playRecords = await _apiClient.GetUserPlayRecordAsync(userInfo.UserId);
						if (playRecords == null || playRecords.Count == 0)
						{
							throw new Exception("暂无全部时间听歌记录");
						}
						return playRecords.Select<(SongInfo, int), SongInfo>(((SongInfo song, int playCount) r) => r.song).ToList();
					}, quality);
					break;
				case "highquality_playlists":
					totalTasks = await DownloadPlaylistListCategory(categoryName, async delegate
					{
						List<PlaylistInfo> playlists;
						(playlists, _, _) = await _apiClient.GetHighQualityPlaylistsAsync("全部", 50, 0L);
						if (playlists == null || playlists.Count == 0)
						{
							throw new Exception("获取精品歌单失败");
						}
						return playlists;
					}, quality);
					break;
				case "new_songs":
					await DownloadMixedCategory(categoryName, () => new List<ListItemInfo>
					{
						new ListItemInfo
						{
							Type = ListItemType.Category,
							CategoryId = "new_songs_all",
							CategoryName = "全部",
							CategoryDescription = "全部地区新歌"
						},
						new ListItemInfo
						{
							Type = ListItemType.Category,
							CategoryId = "new_songs_chinese",
							CategoryName = "华语",
							CategoryDescription = "华语新歌"
						},
						new ListItemInfo
						{
							Type = ListItemType.Category,
							CategoryId = "new_songs_western",
							CategoryName = "欧美",
							CategoryDescription = "欧美新歌"
						},
						new ListItemInfo
						{
							Type = ListItemType.Category,
							CategoryId = "new_songs_japan",
							CategoryName = "日本",
							CategoryDescription = "日本新歌"
						},
						new ListItemInfo
						{
							Type = ListItemType.Category,
							CategoryId = "new_songs_korea",
							CategoryName = "韩国",
							CategoryDescription = "韩国新歌"
						}
					}, quality);
					return;
				case "new_songs_all":
					totalTasks = await DownloadSongListCategory(categoryName, async delegate
					{
						List<SongInfo> songs = await _apiClient.GetNewSongsAsync();
						if (songs == null || songs.Count == 0)
						{
							throw new Exception("获取全部新歌失败");
						}
						return songs;
					}, quality);
					break;
				case "new_songs_chinese":
					totalTasks = await DownloadSongListCategory(categoryName, async delegate
					{
						List<SongInfo> songs = await _apiClient.GetNewSongsAsync(7);
						if (songs == null || songs.Count == 0)
						{
							throw new Exception("获取华语新歌失败");
						}
						return songs;
					}, quality);
					break;
				case "new_songs_western":
					totalTasks = await DownloadSongListCategory(categoryName, async delegate
					{
						List<SongInfo> songs = await _apiClient.GetNewSongsAsync(96);
						if (songs == null || songs.Count == 0)
						{
							throw new Exception("获取欧美新歌失败");
						}
						return songs;
					}, quality);
					break;
				case "new_songs_japan":
					totalTasks = await DownloadSongListCategory(categoryName, async delegate
					{
						List<SongInfo> songs = await _apiClient.GetNewSongsAsync(8);
						if (songs == null || songs.Count == 0)
						{
							throw new Exception("获取日本新歌失败");
						}
						return songs;
					}, quality);
					break;
				case "new_songs_korea":
					totalTasks = await DownloadSongListCategory(categoryName, async delegate
					{
						List<SongInfo> songs = await _apiClient.GetNewSongsAsync(16);
						if (songs == null || songs.Count == 0)
						{
							throw new Exception("获取韩国新歌失败");
						}
						return songs;
					}, quality);
					break;
				case "recent_playlists":
					totalTasks = await DownloadPlaylistListCategory(categoryName, async delegate
					{
						List<PlaylistInfo> playlists = await _apiClient.GetRecentPlaylistsAsync();
						if (playlists == null || playlists.Count == 0)
						{
							throw new Exception("暂无最近播放的歌单");
						}
						return playlists;
					}, quality);
					break;
				case "recent_albums":
					totalTasks = await DownloadAlbumListCategory(categoryName, async delegate
					{
						List<AlbumInfo> albums = await _apiClient.GetRecentAlbumsAsync();
						if (albums == null || albums.Count == 0)
						{
							throw new Exception("暂无最近播放的专辑");
						}
						return albums;
					}, quality);
					break;
				case "user_podcasts":
					MessageBox.Show("请在“收藏的电台”列表中选择播客，使用右键菜单下载其节目。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
					return;
				case "recent_podcasts":
					MessageBox.Show("请进入播客详情页，使用右键菜单中的“下载播客全部节目”来下载具体播客内容。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
					return;
				case "recent_listened":
					await DownloadMixedCategory(categoryName, BuildRecentListenedEntries, quality);
					return;
				case "playlist_category":
					await DownloadMixedCategory(categoryName, delegate
					{
						List<ListItemInfo> list = new List<ListItemInfo>();
						string[] array = new string[10] { "华语", "流行", "摇滚", "民谣", "电子", "轻音乐", "影视原声", "ACG", "怀旧", "治愈" };
						string[] array2 = array;
						foreach (string text in array2)
						{
							list.Add(new ListItemInfo
							{
								Type = ListItemType.Category,
								CategoryId = "playlist_cat_" + text,
								CategoryName = text,
								CategoryDescription = text + "歌单"
							});
						}
						return list;
					}, quality);
					return;
				case "new_albums":
					totalTasks = await DownloadAlbumListCategory(categoryName, async delegate
					{
						List<AlbumInfo> albums = await _apiClient.GetNewAlbumsAsync();
						if (albums == null || albums.Count == 0)
						{
							throw new Exception("暂无新碟上架");
						}
						return albums;
					}, quality);
					break;
				case "recent_played":
					totalTasks = await DownloadSongListCategory(categoryName, async delegate
					{
						List<SongInfo> songs = await _apiClient.GetRecentPlayedSongsAsync();
						if (songs == null || songs.Count == 0)
						{
							throw new Exception("暂无最近播放记录");
						}
						return songs;
					}, quality);
					break;
				default:
					if (categoryId.StartsWith("playlist_cat_"))
					{
						string catName = categoryId.Substring("playlist_cat_".Length);
						totalTasks = await DownloadPlaylistListCategory(catName, async delegate
						{
							List<PlaylistInfo> playlists;
							(playlists, _, _) = await _apiClient.GetPlaylistsByCategoryAsync(catName);
							if (playlists == null || playlists.Count == 0)
							{
								throw new Exception("获取" + catName + "歌单失败");
							}
							return playlists;
						}, quality);
						break;
					}
					MessageBox.Show("暂不支持下载该分类: " + categoryName, "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
					return;
				}
				if (totalTasks > 0)
				{
					MessageBox.Show($"已添加 {totalTasks} 个下载任务\n分类：{categoryName}", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				}
			}
			finally
			{
				Cursor.Current = originalCursor;
			}
		}
		catch (Exception ex)
		{
			MessageBox.Show("下载分类失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
		}
	}

	private async Task<int> DownloadSongListCategory(string categoryName, Func<Task<List<SongInfo>>> getSongsFunc, QualityLevel quality, string? parentDirectory = null, bool showDialog = true)
	{
		List<SongInfo> songs = await getSongsFunc();
		checked
		{
			List<SongInfo> selectedSongs;
			List<int> originalIndices;
			if (showDialog)
			{
				List<string> displayNames = new List<string>();
				for (int i = 0; i < songs.Count; i++)
				{
					SongInfo song = songs[i];
					displayNames.Add($"{i + 1}. {song.Name} - {song.Artist}");
				}
				BatchDownloadDialog dialog = new BatchDownloadDialog(displayNames, "下载分类 - " + categoryName);
				if (dialog.ShowDialog() != DialogResult.OK || dialog.SelectedIndices.Count == 0)
				{
					return 0;
				}
				List<int> selectedIndicesList = dialog.SelectedIndices;
				selectedSongs = selectedIndicesList.Select((int index) => songs[index]).ToList();
				originalIndices = selectedIndicesList.Select((int num) => num + 1).ToList();
			}
			else
			{
				selectedSongs = songs;
				originalIndices = Enumerable.Range(1, songs.Count).ToList();
			}
			string fullDirectory = (string.IsNullOrEmpty(parentDirectory) ? categoryName : Path.Combine(parentDirectory, categoryName));
			return (await _downloadManager.AddBatchDownloadAsync(selectedSongs, quality, categoryName, fullDirectory, originalIndices)).Count;
		}
	}

	private async Task<int> DownloadPlaylistListCategory(string categoryName, Func<Task<List<PlaylistInfo>>> getPlaylistsFunc, QualityLevel quality, string? parentDirectory = null, bool showDialog = true)
	{
		List<PlaylistInfo> playlists = await getPlaylistsFunc();
		List<PlaylistInfo> selectedPlaylists;
		if (showDialog)
		{
			List<string> displayNames = playlists.Select((PlaylistInfo p) => p.Name).ToList();
			BatchDownloadDialog dialog = new BatchDownloadDialog(displayNames, "下载分类 - " + categoryName);
			if (dialog.ShowDialog() != DialogResult.OK || dialog.SelectedIndices.Count == 0)
			{
				return 0;
			}
			List<int> selectedIndices = dialog.SelectedIndices;
			selectedPlaylists = selectedIndices.Select((int i) => playlists[i]).ToList();
		}
		else
		{
			selectedPlaylists = playlists;
		}
		int totalTasks = 0;
		foreach (PlaylistInfo playlist in selectedPlaylists)
		{
			PlaylistInfo playlistDetail = await _apiClient.GetPlaylistDetailAsync(playlist.Id);
			if (playlistDetail?.Songs != null && playlistDetail.Songs.Count > 0)
			{
				string baseDirectory = (string.IsNullOrEmpty(parentDirectory) ? categoryName : Path.Combine(parentDirectory, categoryName));
				string subDirectory = Path.Combine(baseDirectory, playlist.Name);
				List<int> originalIndices = Enumerable.Range(1, playlistDetail.Songs.Count).ToList();
				totalTasks = checked(totalTasks + (await _downloadManager.AddBatchDownloadAsync(playlistDetail.Songs, quality, playlist.Name, subDirectory, originalIndices)).Count);
			}
		}
		return totalTasks;
	}

	private async Task<int> DownloadAlbumListCategory(string categoryName, Func<Task<List<AlbumInfo>>> getAlbumsFunc, QualityLevel quality, string? parentDirectory = null, bool showDialog = true)
	{
		List<AlbumInfo> albums = await getAlbumsFunc();
		List<AlbumInfo> selectedAlbums;
		if (showDialog)
		{
			List<string> displayNames = albums.Select((AlbumInfo a) => a.Name + " - " + a.Artist).ToList();
			BatchDownloadDialog dialog = new BatchDownloadDialog(displayNames, "下载分类 - " + categoryName);
			if (dialog.ShowDialog() != DialogResult.OK || dialog.SelectedIndices.Count == 0)
			{
				return 0;
			}
			List<int> selectedIndices = dialog.SelectedIndices;
			selectedAlbums = selectedIndices.Select((int i) => albums[i]).ToList();
		}
		else
		{
			selectedAlbums = albums;
		}
		int totalTasks = 0;
		foreach (AlbumInfo album in selectedAlbums)
		{
			List<SongInfo> songs = await _apiClient.GetAlbumSongsAsync(album.Id);
			if (songs != null && songs.Count > 0)
			{
				string baseDirectory = (string.IsNullOrEmpty(parentDirectory) ? categoryName : Path.Combine(parentDirectory, categoryName));
				string albumFolderName = album.Name + " - " + album.Artist;
				string subDirectory = Path.Combine(baseDirectory, albumFolderName);
				List<int> originalIndices = Enumerable.Range(1, songs.Count).ToList();
				totalTasks = checked(totalTasks + (await _downloadManager.AddBatchDownloadAsync(songs, quality, albumFolderName, subDirectory, originalIndices)).Count);
			}
		}
		return totalTasks;
	}

	private async Task DownloadMixedCategory(string categoryName, Func<List<ListItemInfo>> getSubCategoriesFunc, QualityLevel quality)
	{
		List<ListItemInfo> subCategories = getSubCategoriesFunc();
		if (subCategories == null || subCategories.Count == 0)
		{
			MessageBox.Show("该分类下没有可用的子分类", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			return;
		}
		List<string> displayNames = subCategories.Select((ListItemInfo item) => item.CategoryName ?? item.CategoryId ?? "未命名分类").ToList();
		BatchDownloadDialog dialog = new BatchDownloadDialog(displayNames, "下载分类 - " + categoryName);
		if (dialog.ShowDialog() != DialogResult.OK || dialog.SelectedIndices.Count == 0)
		{
			return;
		}
		List<ListItemInfo> selectedSubCategories = dialog.SelectedIndices.Select((int i) => subCategories[i]).ToList();
		int totalTasks = 0;
		Cursor originalCursor = Cursor.Current;
		Cursor.Current = Cursors.WaitCursor;
		try
		{
			foreach (ListItemInfo subCategory in selectedSubCategories)
			{
				string subCategoryId = subCategory.CategoryId;
				string subCategoryName = subCategory.CategoryName ?? subCategoryId;
				try
				{
					int taskCount;
					switch (subCategoryId)
					{
					case "daily_recommend_songs":
						taskCount = await DownloadSongListCategory(subCategoryName, async delegate
						{
							List<SongInfo> songs = await _apiClient.GetDailyRecommendSongsAsync();
							if (songs == null || songs.Count == 0)
							{
								throw new Exception("获取每日推荐歌曲失败");
							}
							return songs;
						}, quality, categoryName, showDialog: false);
						break;
					case "personalized_newsongs":
						taskCount = await DownloadSongListCategory(subCategoryName, async delegate
						{
							List<SongInfo> songs = await _apiClient.GetPersonalizedNewSongsAsync(20);
							if (songs == null || songs.Count == 0)
							{
								throw new Exception("获取推荐新歌失败");
							}
							return songs;
						}, quality, categoryName, showDialog: false);
						break;
					case "daily_recommend_playlists":
						taskCount = await DownloadPlaylistListCategory(subCategoryName, async delegate
						{
							List<PlaylistInfo> playlists = await _apiClient.GetDailyRecommendPlaylistsAsync();
							if (playlists == null || playlists.Count == 0)
							{
								throw new Exception("获取每日推荐歌单失败");
							}
							return playlists;
						}, quality, categoryName, showDialog: false);
						break;
					case "personalized_playlists":
						taskCount = await DownloadPlaylistListCategory(subCategoryName, async delegate
						{
							List<PlaylistInfo> playlists = await _apiClient.GetPersonalizedPlaylistsAsync();
							if (playlists == null || playlists.Count == 0)
							{
								throw new Exception("获取推荐歌单失败");
							}
							return playlists;
						}, quality, categoryName, showDialog: false);
						break;
					case "user_play_record_week":
						taskCount = await DownloadSongListCategory(subCategoryName, async delegate
						{
							UserAccountInfo userInfo = await _apiClient.GetUserAccountAsync();
							if (userInfo == null || userInfo.UserId <= 0)
							{
								throw new Exception("获取用户信息失败");
							}
							List<(SongInfo song, int playCount)> playRecords = await _apiClient.GetUserPlayRecordAsync(userInfo.UserId, 1);
							if (playRecords == null || playRecords.Count == 0)
							{
								throw new Exception("暂无周榜单听歌记录");
							}
							return playRecords.Select<(SongInfo, int), SongInfo>(((SongInfo song, int playCount) r) => r.song).ToList();
						}, quality, categoryName, showDialog: false);
						break;
					case "user_play_record_all":
						taskCount = await DownloadSongListCategory(subCategoryName, async delegate
						{
							UserAccountInfo userInfo = await _apiClient.GetUserAccountAsync();
							if (userInfo == null || userInfo.UserId <= 0)
							{
								throw new Exception("获取用户信息失败");
							}
							List<(SongInfo song, int playCount)> playRecords = await _apiClient.GetUserPlayRecordAsync(userInfo.UserId);
							if (playRecords == null || playRecords.Count == 0)
							{
								throw new Exception("暂无全部时间听歌记录");
							}
							return playRecords.Select<(SongInfo, int), SongInfo>(((SongInfo song, int playCount) r) => r.song).ToList();
						}, quality, categoryName, showDialog: false);
						break;
					case "new_songs_all":
						taskCount = await DownloadSongListCategory(subCategoryName, async delegate
						{
							List<SongInfo> songs = await _apiClient.GetNewSongsAsync();
							if (songs == null || songs.Count == 0)
							{
								throw new Exception("获取全部新歌失败");
							}
							return songs;
						}, quality, categoryName, showDialog: false);
						break;
					case "new_songs_chinese":
						taskCount = await DownloadSongListCategory(subCategoryName, async delegate
						{
							List<SongInfo> songs = await _apiClient.GetNewSongsAsync(7);
							if (songs == null || songs.Count == 0)
							{
								throw new Exception("获取华语新歌失败");
							}
							return songs;
						}, quality, categoryName, showDialog: false);
						break;
					case "new_songs_western":
						taskCount = await DownloadSongListCategory(subCategoryName, async delegate
						{
							List<SongInfo> songs = await _apiClient.GetNewSongsAsync(96);
							if (songs == null || songs.Count == 0)
							{
								throw new Exception("获取欧美新歌失败");
							}
							return songs;
						}, quality, categoryName, showDialog: false);
						break;
					case "new_songs_japan":
						taskCount = await DownloadSongListCategory(subCategoryName, async delegate
						{
							List<SongInfo> songs = await _apiClient.GetNewSongsAsync(8);
							if (songs == null || songs.Count == 0)
							{
								throw new Exception("获取日本新歌失败");
							}
							return songs;
						}, quality, categoryName, showDialog: false);
						break;
					case "new_songs_korea":
						taskCount = await DownloadSongListCategory(subCategoryName, async delegate
						{
							List<SongInfo> songs = await _apiClient.GetNewSongsAsync(16);
							if (songs == null || songs.Count == 0)
							{
								throw new Exception("获取韩国新歌失败");
							}
							return songs;
						}, quality, categoryName, showDialog: false);
						break;
					default:
						if (subCategoryId.StartsWith("playlist_cat_"))
						{
							string catName = subCategoryId.Substring("playlist_cat_".Length);
							taskCount = await DownloadPlaylistListCategory(catName, async delegate
							{
								List<PlaylistInfo> playlists;
								(playlists, _, _) = await _apiClient.GetPlaylistsByCategoryAsync(catName);
								if (playlists == null || playlists.Count == 0)
								{
									throw new Exception("获取" + catName + "歌单失败");
								}
								return playlists;
							}, quality, categoryName, showDialog: false);
							break;
						}
						MessageBox.Show("暂不支持下载该子分类: " + subCategoryName, "提示", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
						goto end_IL_0237;
					}
					totalTasks = checked(totalTasks + taskCount);
					end_IL_0237:;
				}
				catch (Exception ex)
				{
					MessageBox.Show("下载子分类 '" + subCategoryName + "' 失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				}
			}
			if (totalTasks > 0)
			{
				MessageBox.Show($"已添加 {totalTasks} 个下载任务\n分类：{categoryName}", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			}
		}
		finally
		{
			Cursor.Current = originalCursor;
		}
	}

	internal async void BatchDownloadPlaylistsOrAlbums_Click(object? sender, EventArgs e)
	{
		checked
		{
			try
			{
				if (resultListView.Items.Count == 0)
				{
					MessageBox.Show("当前列表为空", "提示", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
					return;
				}
				bool isPlaylistView = resultListView.Items.Count > 0 && resultListView.Items[0].Tag is PlaylistInfo;
				bool isAlbumView = resultListView.Items.Count > 0 && resultListView.Items[0].Tag is AlbumInfo;
				if (!isPlaylistView && !isAlbumView)
				{
					MessageBox.Show("当前视图不是歌单或专辑列表", "提示", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
					return;
				}
				List<string> displayNames = new List<string>();
				List<object> items = new List<object>();
				foreach (ListViewItem listViewItem in resultListView.Items)
				{
					object tag = listViewItem.Tag;
					if (tag is PlaylistInfo playlist)
					{
						displayNames.Add(playlist.Name);
						items.Add(playlist);
						continue;
					}
					tag = listViewItem.Tag;
					if (tag is AlbumInfo album)
					{
						displayNames.Add(album.Name + " - " + album.Artist);
						items.Add(album);
					}
				}
				if (items.Count == 0)
				{
					MessageBox.Show("当前列表中没有可下载的歌单或专辑", "提示", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
					return;
				}
				string viewName = GetCurrentViewName();
				BatchDownloadDialog dialog = new BatchDownloadDialog(displayNames, "批量下载 - " + viewName);
				if (dialog.ShowDialog() != DialogResult.OK)
				{
					return;
				}
				List<int> selectedIndices = dialog.SelectedIndices;
				if (selectedIndices.Count == 0)
				{
					return;
				}
				Cursor originalCursor = Cursor.Current;
				Cursor.Current = Cursors.WaitCursor;
				try
				{
					QualityLevel quality = GetCurrentQuality();
					int totalTasks = 0;
					foreach (int index in selectedIndices)
					{
						object item = items[index];
						if (item is PlaylistInfo playlist2)
						{
							PlaylistInfo playlistDetail = await _apiClient.GetPlaylistDetailAsync(playlist2.Id);
							if (playlistDetail?.Songs != null && playlistDetail.Songs.Count > 0)
							{
								List<int> originalIndices = Enumerable.Range(1, playlistDetail.Songs.Count).ToList();
								totalTasks += (await _downloadManager.AddBatchDownloadAsync(playlistDetail.Songs, quality, playlist2.Name, playlist2.Name, originalIndices)).Count;
							}
						}
						else if (item is AlbumInfo album2)
						{
							List<SongInfo> songs = await _apiClient.GetAlbumSongsAsync(album2.Id);
							if (songs != null && songs.Count > 0)
							{
								string albumName = album2.Name + " - " + album2.Artist;
								List<int> originalIndices2 = Enumerable.Range(1, songs.Count).ToList();
								totalTasks += (await _downloadManager.AddBatchDownloadAsync(songs, quality, albumName, albumName, originalIndices2)).Count;
							}
						}
					}
					MessageBox.Show($"已添加 {totalTasks} 个下载任务", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				}
				finally
				{
					Cursor.Current = originalCursor;
				}
			}
			catch (Exception ex)
			{
				Exception ex2 = ex;
				MessageBox.Show("批量下载失败：" + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			}
		}
	}

	private async Task<List<PodcastEpisodeInfo>> FetchAllPodcastEpisodesAsync(long podcastId)
	{
		List<PodcastEpisodeInfo> result = new List<PodcastEpisodeInfo>();
		if (podcastId <= 0)
		{
			return result;
		}
		int offset = 0;
		bool hasMore;
		int totalCount;
		do
		{
			List<PodcastEpisodeInfo> episodes;
			(episodes, hasMore, totalCount) = await _apiClient.GetPodcastEpisodesAsync(podcastId, 100, offset);
			if (episodes == null || episodes.Count == 0)
			{
				break;
			}
			result.AddRange(episodes);
			offset = checked(offset + episodes.Count);
		}
		while (hasMore && offset < totalCount);
		return result;
	}

	private void ConfigureListViewDefault()
	{
        columnHeader1.Text = string.Empty;
        columnHeader2.Text = string.Empty;
        columnHeader3.Text = string.Empty;
        columnHeader4.Text = string.Empty;
        columnHeader5.Text = string.Empty;
	}

	private void ConfigureListViewForArtists()
	{
        columnHeader1.Text = string.Empty;
        columnHeader2.Text = string.Empty;
        columnHeader3.Text = string.Empty;
        columnHeader4.Text = string.Empty;
        columnHeader5.Text = string.Empty;
	}

	private static string BuildArtistStatsLabel(int musicCount, int albumCount)
	{
		bool hasMusic = musicCount > 0;
		bool hasAlbum = albumCount > 0;
		if (hasMusic && hasAlbum)
		{
			return $"歌曲 {musicCount} / 专辑 {albumCount}";
		}
		if (hasMusic)
		{
			return $"歌曲 {musicCount}";
		}
		if (hasAlbum)
		{
			return $"专辑 {albumCount}";
		}
		return string.Empty;
	}

	private void DisplayArtists(List<ArtistInfo> artists, bool showPagination = false, bool hasNextPage = false, int startIndex = 1, bool preserveSelection = false, string? viewSource = null, string? accessibleName = null, bool announceHeader = true, bool suppressFocus = false, bool allowSelection = true)
	{
		CancellationToken currentViewContentToken = GetCurrentViewContentToken();
		if (ShouldAbortViewRender(currentViewContentToken, "DisplayArtists"))
		{
			return;
		}
		ResetPendingListFocusIfViewChanged(viewSource);
		int num = -1;
		if (preserveSelection && resultListView.SelectedIndices.Count > 0)
		{
			num = resultListView.SelectedIndices[0];
		}
		_currentArtists = artists ?? new List<ArtistInfo>();
		_currentSongs.Clear();
		_currentPlaylists.Clear();
		_currentAlbums.Clear();
		_currentListItems.Clear();
		_currentPodcasts.Clear();
		_currentPodcastSounds.Clear();
		_currentPodcast = null;
		ApplyArtistSubscriptionStates(_currentArtists);
		bool flag = _currentArtists.Count > 0;
		resultListView.BeginUpdate();
		checked
		{
			try
			{
				resultListView.Items.Clear();
				if (ShouldAbortViewRender(currentViewContentToken, "DisplayArtists"))
				{
					return;
				}
				if (flag)
				{
					int num2 = startIndex;
					foreach (ArtistInfo currentArtist in _currentArtists)
					{
						if (ShouldAbortViewRender(currentViewContentToken, "DisplayArtists"))
						{
							return;
						}
						if ((currentArtist.MusicCount <= 0 || currentArtist.AlbumCount <= 0) && TryGetCachedArtistStats(currentArtist.Id, out (int, int) stats))
						{
							if (currentArtist.MusicCount <= 0)
							{
								(currentArtist.MusicCount, _) = stats;
							}
							if (currentArtist.AlbumCount <= 0)
						{
							currentArtist.AlbumCount = stats.Item2;
						}
					}
					string text = BuildArtistStatsLabel(currentArtist.MusicCount, currentArtist.AlbumCount);
					string text2 = ((!string.IsNullOrWhiteSpace(currentArtist.Description)) ? currentArtist.Description : currentArtist.BriefDesc);
					ListViewItem value = new ListViewItem(new string[5]
					{
						num2.ToString(),
						currentArtist.Name ?? "未知",
							text,
							text2 ?? string.Empty,
							string.Empty
						})
						{
							Tag = currentArtist
						};
						resultListView.Items.Add(value);
						num2++;
					}
					if (showPagination)
					{
						if (startIndex > 1)
						{
							ListViewItem listViewItem = resultListView.Items.Add("上一页");
							listViewItem.Tag = -2;
						}
						if (hasNextPage)
						{
							ListViewItem listViewItem2 = resultListView.Items.Add("下一页");
							listViewItem2.Tag = -3;
						}
					}
				}
			}
			finally
			{
				resultListView.EndUpdate();
			}
			if (ShouldAbortViewRender(currentViewContentToken, "DisplayArtists"))
			{
				return;
			}
			ConfigureListViewForArtists();
			if (!flag)
			{
				SetViewContext(viewSource, accessibleName ?? "歌手列表");
				return;
			}
			ScheduleArtistStatsRefresh(_currentArtists);
			string text3 = accessibleName ?? string.Empty;
			if (string.IsNullOrWhiteSpace(text3))
			{
				text3 = "歌手列表";
			}
			SetViewContext(viewSource, text3);
			if (allowSelection && !suppressFocus && !IsListAutoFocusSuppressed && resultListView.Items.Count > 0)
			{
				int targetIndex = ((num >= 0) ? Math.Min(num, resultListView.Items.Count - 1) : 0);
				targetIndex = ResolvePendingListFocusIndex(targetIndex);
				EnsureListSelectionWithoutFocus(targetIndex);
			}
		}
	}

	/// <summary>
	/// 确保单曲的可用性已被检查并写入缓存（登录/未登录均执行）。
	/// </summary>
	private async Task<bool> EnsureSongAvailabilityAsync(SongInfo song, QualityLevel quality, CancellationToken cancellationToken)
	{
		if (song == null || string.IsNullOrWhiteSpace(song.Id))
		{
			return false;
		}

		if (song.IsAvailable.HasValue)
		{
			return song.IsAvailable.Value;
		}

		try
		{
			var result = await _apiClient.BatchCheckSongsAvailabilityAsync(new[] { song.Id }, quality).ConfigureAwait(false);
			if (result != null && result.TryGetValue(song.Id, out bool isAvailable))
			{
				song.IsAvailable = isAvailable;
				return isAvailable;
			}
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"[StreamCheck] 单曲可用性检查失败（忽略，按可用继续）: {ex.Message}");
		}

		// 未能确认时，保持未知，返回 true 让后续流程继续尝试
		return true;
	}

	private void PatchArtists(List<ArtistInfo> artists, int startIndex = 1, bool showPagination = false, bool hasPreviousPage = false, bool hasNextPage = false, int pendingFocusIndex = -1, bool allowSelection = true)
	{
		CancellationToken currentViewContentToken = GetCurrentViewContentToken();
		if (ShouldAbortViewRender(currentViewContentToken, "PatchArtists"))
		{
			return;
		}
		ResetPendingListFocusIfViewChanged(_currentViewSource);
		int num = ((resultListView.SelectedIndices.Count > 0) ? resultListView.SelectedIndices[0] : pendingFocusIndex);
		_currentArtists = artists ?? new List<ArtistInfo>();
		_currentSongs.Clear();
		_currentPlaylists.Clear();
		_currentAlbums.Clear();
		_currentListItems.Clear();
		_currentPodcasts.Clear();
		_currentPodcastSounds.Clear();
		_currentPodcast = null;
		ApplyArtistSubscriptionStates(_currentArtists);
		bool flag = _currentArtists.Count > 0;
		resultListView.BeginUpdate();
		checked
		{
			try
			{
				int count = _currentArtists.Count;
				int count2 = resultListView.Items.Count;
				int num2 = Math.Min(count, count2);
				for (int i = 0; i < num2; i++)
				{
					ArtistInfo artistInfo = _currentArtists[i];
					ListViewItem listViewItem = resultListView.Items[i];
					EnsureSubItemCount(listViewItem, 5);
					if ((artistInfo.MusicCount <= 0 || artistInfo.AlbumCount <= 0) && TryGetCachedArtistStats(artistInfo.Id, out (int, int) stats))
					{
						if (artistInfo.MusicCount <= 0)
						{
							(artistInfo.MusicCount, _) = stats;
						}
						if (artistInfo.AlbumCount <= 0)
						{
							artistInfo.AlbumCount = stats.Item2;
						}
					}
					string text = BuildArtistStatsLabel(artistInfo.MusicCount, artistInfo.AlbumCount);
					string text2 = ((!string.IsNullOrWhiteSpace(artistInfo.Description)) ? artistInfo.Description : artistInfo.BriefDesc);
					listViewItem.Text = (startIndex + i).ToString();
					listViewItem.SubItems[1].Text = artistInfo.Name ?? "未知";
					listViewItem.SubItems[2].Text = text;
					listViewItem.SubItems[3].Text = text2 ?? string.Empty;
					listViewItem.SubItems[4].Text = string.Empty;
					listViewItem.Tag = artistInfo;
				}
				for (int j = count2; j < count; j++)
				{
					ArtistInfo artistInfo2 = _currentArtists[j];
					if ((artistInfo2.MusicCount <= 0 || artistInfo2.AlbumCount <= 0) && TryGetCachedArtistStats(artistInfo2.Id, out (int, int) stats2))
					{
						if (artistInfo2.MusicCount <= 0)
						{
							(artistInfo2.MusicCount, _) = stats2;
						}
						if (artistInfo2.AlbumCount <= 0)
						{
							artistInfo2.AlbumCount = stats2.Item2;
						}
					}
					string text3 = BuildArtistStatsLabel(artistInfo2.MusicCount, artistInfo2.AlbumCount);
					string text4 = ((!string.IsNullOrWhiteSpace(artistInfo2.Description)) ? artistInfo2.Description : artistInfo2.BriefDesc);
					ListViewItem value = new ListViewItem(new string[5]
					{
						(startIndex + j).ToString(),
						artistInfo2.Name ?? "未知",
						text3,
						text4 ?? string.Empty,
						string.Empty
					})
					{
						Tag = artistInfo2
					};
					resultListView.Items.Add(value);
				}
				for (int num3 = resultListView.Items.Count - 1; num3 >= count; num3--)
				{
					resultListView.Items.RemoveAt(num3);
				}
				if (showPagination)
				{
					if (hasPreviousPage)
					{
						ListViewItem value2 = new ListViewItem("上一页")
						{
							Tag = -2
						};
						resultListView.Items.Add(value2);
					}
					if (hasNextPage)
					{
						ListViewItem value3 = new ListViewItem("下一页")
						{
							Tag = -3
						};
						resultListView.Items.Add(value3);
					}
				}
			}
			finally
			{
				resultListView.EndUpdate();
			}
			// 补齐歌手统计（搜索结果的歌曲/专辑数初始通常为 0）
			if (!ShouldAbortViewRender(currentViewContentToken, "PatchArtists") && flag)
			{
				ScheduleArtistStatsRefresh(_currentArtists);
			}
			if (!ShouldAbortViewRender(currentViewContentToken, "PatchArtists") && allowSelection && resultListView.Items.Count > 0)
			{
				int fallbackIndex = ((num >= 0) ? Math.Min(num, resultListView.Items.Count - 1) : 0);
				fallbackIndex = ResolvePendingListFocusIndex(fallbackIndex);
				EnsureListSelectionWithoutFocus(fallbackIndex);
			}
		}
	}

	private async Task OpenArtistAsync(ArtistInfo artist, bool skipSave = false)
	{
		if (artist == null)
		{
			return;
		}
		try
		{
			if (!skipSave)
			{
				SaveNavigationState();
			}
			_artistSongSortState.SetOption(ArtistSongSortOption.Hot);
			_artistAlbumSortState.SetOption(ArtistAlbumSortOption.Latest);
			string displayName = (string.IsNullOrWhiteSpace(artist.Name) ? $"歌手 {artist.Id}" : artist.Name);
			string viewSource = $"artist_entries:{artist.Id}";
			ViewLoadRequest request = new ViewLoadRequest(viewSource, displayName, "正在加载歌手：" + displayName + "...", !skipSave);
			ViewLoadResult<ArtistEntryViewData?> loadResult = await RunViewLoadAsync(request, async delegate(CancellationToken token)
			{
				_currentArtist = artist;
				_currentArtistSongsOffset = 0;
				_currentArtistAlbumsOffset = 0;
				_currentArtistSongsHasMore = false;
				_currentArtistAlbumsHasMore = false;
				_currentArtistAlbumsTotalCount = 0;
				ClearArtistAlbumsAscendingCache(artist.Id);
				ArtistDetail currentArtistDetail = await _apiClient.GetArtistDetailAsync(artist.Id);
				_currentArtistDetail = currentArtistDetail;
				token.ThrowIfCancellationRequested();
				if (_currentArtistDetail != null)
				{
					ApplyArtistDetailToArtist(artist, _currentArtistDetail);
				}
				return new ArtistEntryViewData(items: BuildArtistEntryItems(artist, _currentArtistDetail), statusText: "已打开歌手：" + displayName, artist: artist, detail: _currentArtistDetail);
			}, "加载歌手已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				ArtistEntryViewData data = loadResult.Value;
				if (data == null)
				{
					UpdateStatusBar("加载歌手失败");
					return;
				}
				_currentArtist = data.Artist;
				_currentArtistDetail = data.Detail;
				_artistSongSortState.SetOption(ArtistSongSortOption.Hot);
				_artistAlbumSortState.SetOption(ArtistAlbumSortOption.Latest);
				DisplayListItems(data.Items, viewSource, displayName, preserveSelection: false, announceHeader: true, suppressFocus: true);
				FocusListAfterEnrich(0);
				UpdateStatusBar(data.StatusText);
			}
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"[Artist] 打开歌手失败: {ex}");
			MessageBox.Show("加载歌手信息失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("加载歌手失败");
		}
	}

	private List<ListItemInfo> BuildArtistEntryItems(ArtistInfo artist, ArtistDetail? detail)
	{
		List<ListItemInfo> list = new List<ListItemInfo>();
		ArtistInfo artistInfo = detail ?? artist;
		list.Add(new ListItemInfo
		{
			Type = ListItemType.Artist,
			Artist = new ArtistInfo
			{
				Id = artist.Id,
				Name = artistInfo.Name,
				Alias = artistInfo.Alias,
				PicUrl = artistInfo.PicUrl,
				AreaCode = artistInfo.AreaCode,
				AreaName = artistInfo.AreaName,
				TypeCode = artistInfo.TypeCode,
				TypeName = artistInfo.TypeName,
				MusicCount = artistInfo.MusicCount,
				AlbumCount = artistInfo.AlbumCount,
				MvCount = artistInfo.MvCount,
				BriefDesc = artistInfo.BriefDesc,
				Description = artistInfo.Description,
				IsSubscribed = artistInfo.IsSubscribed
			}
		});
		list.Add(new ListItemInfo
		{
			Type = ListItemType.Category,
			CategoryId = $"artist_top_{artist.Id}",
			CategoryName = "热门 50 首",
			CategoryDescription = "网易云热门 50 首歌曲",
			ItemCount = Math.Min(50, (artistInfo.MusicCount > 0) ? artistInfo.MusicCount : 50),
			ItemUnit = "首"
		});
		list.Add(new ListItemInfo
		{
			Type = ListItemType.Category,
			CategoryId = $"artist_songs_{artist.Id}",
			CategoryName = "全部单曲",
			CategoryDescription = "按热度排序，可分页浏览",
			ItemCount = artistInfo.MusicCount,
			ItemUnit = "首"
		});
		list.Add(new ListItemInfo
		{
			Type = ListItemType.Category,
			CategoryId = $"artist_albums_{artist.Id}",
			CategoryName = "全部专辑",
			CategoryDescription = "歌手专辑列表",
			ItemCount = artistInfo.AlbumCount,
			ItemUnit = "张"
		});
		return list;
	}

	private async Task<string> ResolveArtistDisplayNameAsync(long artistId)
	{
		if (artistId <= 0)
		{
			return "歌手";
		}
		if (_currentArtist != null && _currentArtist.Id == artistId && !string.IsNullOrWhiteSpace(_currentArtist.Name))
		{
			return _currentArtist.Name;
		}
		if (_currentArtistDetail != null && _currentArtistDetail.Id == artistId && !string.IsNullOrWhiteSpace(_currentArtistDetail.Name))
		{
			return _currentArtistDetail.Name;
		}
		try
		{
			ArtistDetail detail = await _apiClient.GetArtistDetailAsync(artistId, includeIntroduction: false);
			if (detail != null)
			{
				_currentArtistDetail = detail;
				if (_currentArtist == null || _currentArtist.Id == artistId)
				{
					_currentArtist = new ArtistInfo
					{
						Id = artistId,
						Name = detail.Name,
						PicUrl = detail.PicUrl
					};
				}
				return string.IsNullOrWhiteSpace(detail.Name) ? $"歌手 {artistId}" : detail.Name;
			}
		}
		catch (Exception arg)
		{
			Debug.WriteLine($"[Artist] 获取歌手信息失败: {arg}");
		}
		return $"歌手 {artistId}";
	}

	private async Task LoadArtistTopSongsAsync(long artistId, bool skipSave = false)
	{
		try
		{
			string artistName = await ResolveArtistDisplayNameAsync(artistId);
			if (!skipSave)
			{
				SaveNavigationState();
			}
			ViewLoadRequest request = new ViewLoadRequest($"artist_songs_top:{artistId}", artistName + " 热门 50 首", "正在加载 " + artistName + " 的热门歌曲...", !skipSave);
			ViewLoadResult<ArtistSongsViewData?> loadResult = await RunViewLoadAsync(request, async delegate(CancellationToken token)
			{
				List<SongInfo> songs = await _apiClient.GetArtistTopSongsAsync(artistId).ConfigureAwait(continueOnCapturedContext: false);
				token.ThrowIfCancellationRequested();
				List<SongInfo> normalized = songs ?? new List<SongInfo>();
				return new ArtistSongsViewData(statusText: (normalized.Count == 0) ? (artistName + " 暂无热门歌曲") : $"已加载 {artistName} 热门 {normalized.Count} 首", songs: normalized, hasMore: false, offset: 0, totalCount: normalized.Count);
			}, "加载歌手热门歌曲已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				ArtistSongsViewData data = loadResult.Value ?? new ArtistSongsViewData(new List<SongInfo>(), hasMore: false, 0, 0, artistName + " 暂无热门歌曲");
				_currentArtistSongsOffset = 0;
				_currentArtistSongsHasMore = false;
				DisplaySongs(data.Songs, showPagination: false, hasNextPage: false, 1, preserveSelection: false, request.ViewSource, request.AccessibleName, skipAvailabilityCheck: true, announceHeader: true, suppressFocus: true);
				FocusListAfterEnrich(0);
				UpdateStatusBar(data.StatusText);
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Debug.WriteLine($"[Artist] 加载热门歌曲失败: {ex2}");
			MessageBox.Show("加载热门歌曲失败：" + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("加载热门歌曲失败");
		}
	}

	private async Task LoadArtistSongsAsync(long artistId, int offset = 0, bool skipSave = false, ArtistSongSortOption? orderOverride = null)
	{
		checked
		{
			try
			{
				string artistName = await ResolveArtistDisplayNameAsync(artistId);
				if (!skipSave)
				{
					SaveNavigationState();
				}
				if (orderOverride.HasValue)
				{
					_artistSongSortState.SetOption(orderOverride.Value);
				}
				string orderToken = MapArtistSongsOrder(_artistSongSortState.CurrentOption);
				string viewSource = $"artist_songs:{artistId}:order{orderToken}:offset{offset}";
				string accessibleName = artistName + " 的歌曲";
				ViewLoadRequest request = new ViewLoadRequest(viewSource, accessibleName, "正在加载 " + artistName + " 的歌曲...", !skipSave);
				ViewLoadResult<ArtistSongsViewData?> loadResult = await RunViewLoadAsync(request, async delegate(CancellationToken token)
				{
					(List<SongInfo> Songs, bool HasMore, int TotalCount) result = await _apiClient.GetArtistSongsAsync(artistId, 100, offset, orderToken).ConfigureAwait(continueOnCapturedContext: false);
					List<SongInfo> songs = result.Songs ?? new List<SongInfo>();
					token.ThrowIfCancellationRequested();
					return new ArtistSongsViewData(statusText: (songs.Count == 0) ? (artistName + " 暂无歌曲") : $"已加载 {artistName} 的歌曲 {offset + 1}-{offset + songs.Count} / {result.TotalCount}首", songs: songs, hasMore: result.HasMore, offset: offset, totalCount: result.TotalCount);
				}, "加载歌手歌曲已取消").ConfigureAwait(continueOnCapturedContext: true);
				if (!loadResult.IsCanceled)
				{
					ArtistSongsViewData data = loadResult.Value ?? new ArtistSongsViewData(new List<SongInfo>(), hasMore: false, offset, 0, artistName + " 暂无歌曲");
					_currentArtistSongsOffset = data.Offset;
					_currentArtistSongsHasMore = data.HasMore;
					DisplaySongs(data.Songs, showPagination: true, data.HasMore, data.Offset + 1, preserveSelection: false, viewSource, accessibleName, skipAvailabilityCheck: false, announceHeader: true, suppressFocus: true);
					UpdateArtistSongsSortMenuChecks();
					FocusListAfterEnrich(0);
					UpdateStatusBar(data.StatusText);
				}
			}
			catch (Exception ex)
			{
				Exception ex2 = ex;
				Debug.WriteLine($"[Artist] 加载歌曲失败: {ex2}");
				MessageBox.Show("加载歌手的歌曲失败：" + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("加载歌手的歌曲失败");
			}
		}
	}

	private async Task LoadArtistAlbumsAsync(long artistId, int offset = 0, bool skipSave = false, ArtistAlbumSortOption? sortOverride = null)
	{
		checked
		{
			try
			{
				string artistName = await ResolveArtistDisplayNameAsync(artistId);
				if (!skipSave)
				{
					SaveNavigationState();
				}
				if (sortOverride.HasValue)
				{
					_artistAlbumSortState.SetOption(sortOverride.Value);
				}
				ArtistAlbumSortOption sortOption = _artistAlbumSortState.CurrentOption;
				string viewSource = string.Format(arg1: MapArtistAlbumSort(sortOption), format: "artist_albums:{0}:order{1}:offset{2}", arg0: artistId, arg2: offset);
				string accessibleName = artistName + " 的专辑";
				ViewLoadRequest request = new ViewLoadRequest(viewSource, accessibleName, "正在加载 " + artistName + " 的专辑...", !skipSave);
				ViewLoadResult<ArtistAlbumsViewData?> loadResult = await RunViewLoadAsync(request, async delegate(CancellationToken token)
				{
					int normalizedOffset = offset;
					List<AlbumInfo> albums;
					bool hasMore;
					int totalCount;
					if (sortOption == ArtistAlbumSortOption.Oldest)
					{
						(List<AlbumInfo> Albums, int NormalizedOffset, bool HasMore) ascendingResult = await LoadArtistAlbumsAscendingPageAsync(artistId, offset);
						albums = ascendingResult.Albums;
						normalizedOffset = ascendingResult.NormalizedOffset;
						hasMore = ascendingResult.HasMore;
						totalCount = _currentArtistAlbumsTotalCount;
					}
					else
					{
						(List<AlbumInfo> Albums, bool HasMore, int TotalCount) result = await _apiClient.GetArtistAlbumsAsync(artistId, 100, offset).ConfigureAwait(continueOnCapturedContext: false);
						albums = result.Albums ?? new List<AlbumInfo>();
						hasMore = result.HasMore;
						totalCount = result.TotalCount;
						_currentArtistAlbumsTotalCount = totalCount;
					}
					token.ThrowIfCancellationRequested();
					return new ArtistAlbumsViewData(statusText: (albums.Count == 0) ? (artistName + " 暂无专辑") : $"已加载 {artistName} 专辑 {normalizedOffset + 1}-{normalizedOffset + albums.Count} / {totalCount}张", albums: albums, hasMore: hasMore, offset: normalizedOffset, totalCount: totalCount, sort: sortOption);
				}, "加载歌手专辑已取消").ConfigureAwait(continueOnCapturedContext: true);
				if (!loadResult.IsCanceled)
				{
					ArtistAlbumsViewData data = loadResult.Value ?? new ArtistAlbumsViewData(new List<AlbumInfo>(), hasMore: false, offset, 0, sortOption, artistName + " 暂无专辑");
					_currentArtistAlbumsOffset = data.Offset;
					_currentArtistAlbumsHasMore = data.HasMore;
					_currentArtistAlbumsTotalCount = data.TotalCount;
					DisplayAlbums(data.Albums, preserveSelection: false, viewSource, accessibleName, data.Offset + 1, showPagination: true, data.HasMore, announceHeader: true, suppressFocus: true);
					UpdateArtistAlbumsSortMenuChecks();
					FocusListAfterEnrich(0);
					UpdateStatusBar(data.StatusText);
				}
			}
			catch (Exception ex)
			{
				Exception ex2 = ex;
				Debug.WriteLine($"[Artist] 加载专辑失败: {ex2}");
				MessageBox.Show("加载歌手专辑失败：" + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("加载歌手专辑失败");
			}
		}
	}

	private async Task LoadArtistFavoritesAsync(bool skipSave = false, bool preserveSelection = false)
	{
		try
		{
			if (!IsUserLoggedIn())
			{
				MessageBox.Show("请先登录后查看收藏的歌手", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				UpdateStatusBar("请先登录");
				return;
			}
			if (!skipSave)
			{
				SaveNavigationState();
			}
			await EnsureLibraryStateFreshAsync(LibraryEntityType.Artists);
			ViewLoadRequest request = new ViewLoadRequest("artist_favorites", "收藏的歌手", "正在加载收藏的歌手...");
			ViewLoadResult<(List<ArtistInfo> Items, bool HasMore, int Offset)?> result = await RunViewLoadAsync(request, (Func<CancellationToken, Task<(List<ArtistInfo>, bool, int)?>>)async delegate(CancellationToken token)
			{
				SearchResult<ArtistInfo> fetchResult = await FetchWithRetryUntilCancel((CancellationToken ct) => _apiClient.GetArtistSubscriptionsAsync(200), "artist_favorites", token, delegate(int attempt, Exception _)
				{
					SafeInvoke(delegate
					{
						UpdateStatusBar($"加载收藏歌手失败，正在重试（第 {attempt} 次）...");
					});
				}).ConfigureAwait(continueOnCapturedContext: true);
				List<ArtistInfo> favoriteArtists = fetchResult?.Items ?? new List<ArtistInfo>();
				foreach (ArtistInfo artist in favoriteArtists)
				{
					artist.IsSubscribed = true;
				}
				return (favoriteArtists, fetchResult?.HasMore ?? false, fetchResult?.Offset ?? 0);
			}, "加载收藏的歌手已取消");
			if (!result.IsCanceled)
			{
				(List<ArtistInfo> Items, bool HasMore, int Offset)? favoritesData = result.Value;
				if (favoritesData.HasValue)
				{
					DisplayArtists(favoritesData.Value.Items, favoritesData.Value.HasMore, favoritesData.Value.HasMore, checked(favoritesData.Value.Offset + 1), preserveSelection, "artist_favorites", "收藏的歌手");
					UpdateStatusBar($"收藏的歌手：{favoritesData.Value.Items.Count} 位");
				}
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Debug.WriteLine($"[Artist] 加载收藏歌手失败: {ex2}");
			MessageBox.Show("加载收藏的歌手失败：" + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("加载收藏歌手失败");
		}
	}

	private async Task LoadArtistCategoryTypesAsync(bool skipSave = false)
	{
		if (!skipSave)
		{
			SaveNavigationState();
		}
		ViewLoadRequest request = new ViewLoadRequest("artist_category_types", "歌手类型分类", "正在加载歌手类型...", !skipSave);
		ViewLoadResult<List<ListItemInfo>> loadResult = await RunViewLoadAsync(request, (CancellationToken _) => Task.FromResult((from option in ArtistMetadataHelper.GetTypeOptions()
			select new ListItemInfo
			{
				Type = ListItemType.Category,
				CategoryId = $"artist_type_{option.Code}",
				CategoryName = option.DisplayName
			}).ToList()), "加载歌手类型已取消").ConfigureAwait(continueOnCapturedContext: true);
		if (!loadResult.IsCanceled)
		{
			DisplayListItems(loadResult.Value, request.ViewSource, request.AccessibleName, preserveSelection: false, announceHeader: true, suppressFocus: true);
			FocusListAfterEnrich(0);
			UpdateStatusBar("请选择歌手类型");
		}
	}

	private async Task LoadArtistCategoryAreasAsync(int typeCode, bool skipSave = false)
	{
		if (!skipSave)
		{
			SaveNavigationState();
		}
		_currentArtistTypeFilter = typeCode;
		ViewLoadRequest request = new ViewLoadRequest($"artist_category_type:{typeCode}", "歌手地区筛选", "正在加载歌手地区...", !skipSave);
		ViewLoadResult<List<ListItemInfo>> loadResult = await RunViewLoadAsync(request, (CancellationToken _) => Task.FromResult((from option in ArtistMetadataHelper.GetAreaOptions()
			select new ListItemInfo
			{
				Type = ListItemType.Category,
				CategoryId = $"artist_area_{typeCode}_{option.Code}",
				CategoryName = option.DisplayName
			}).ToList()), "加载歌手地区已取消").ConfigureAwait(continueOnCapturedContext: true);
		if (!loadResult.IsCanceled)
		{
			DisplayListItems(loadResult.Value, request.ViewSource, request.AccessibleName, preserveSelection: false, announceHeader: true, suppressFocus: true);
			FocusListAfterEnrich(0);
			UpdateStatusBar("请选择歌手地区");
		}
	}

	private async Task LoadArtistsByCategoryAsync(int typeCode, int areaCode, int offset = 0, bool skipSave = false)
	{
		try
		{
			if (!skipSave)
			{
				SaveNavigationState();
			}
			_currentArtistTypeFilter = typeCode;
			_currentArtistAreaFilter = areaCode;
			string placeholderSource = $"artist_category_list:{typeCode}:{areaCode}";
			string viewSource = $"{placeholderSource}:offset{offset}";
			ViewLoadRequest request = new ViewLoadRequest(placeholderSource, "歌手分类列表", "正在加载歌手列表...", !skipSave);
			ViewLoadResult<SearchResult<ArtistInfo>?> loadResult = await RunViewLoadAsync(request, async delegate(CancellationToken token)
			{
				SearchResult<ArtistInfo> result2 = await FetchWithRetryUntilCancel((CancellationToken ct) => _apiClient.GetArtistsByCategoryAsync(typeCode, areaCode, 100, offset), $"artist_category:{typeCode}:{areaCode}:offset{offset}", token, delegate(int attempt, Exception _)
				{
					SafeInvoke(delegate
					{
						UpdateStatusBar($"加载歌手分类失败，正在重试（第 {attempt} 次）...");
					});
				}).ConfigureAwait(continueOnCapturedContext: true);
				token.ThrowIfCancellationRequested();
				return result2;
			}, "加载歌手分类已取消").ConfigureAwait(continueOnCapturedContext: true);
			if (!loadResult.IsCanceled)
			{
				SearchResult<ArtistInfo> result = loadResult.Value;
				if (result == null)
				{
					UpdateStatusBar("加载歌手分类失败");
					return;
				}
				_currentArtistCategoryHasMore = result.HasMore;
				List<ArtistInfo> artists = result.Items ?? new List<ArtistInfo>();
				DisplayArtists(artists, result.HasMore, result.HasMore, checked(result.Offset + 1), preserveSelection: false, viewSource, request.AccessibleName);
				UpdateStatusBar($"歌手分类列表：{artists.Count} 位");
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Debug.WriteLine($"[Artist] 加载分类歌手失败: {ex2}");
			MessageBox.Show("加载歌手分类失败：" + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("加载歌手分类失败");
		}
	}

	private void ConfigureArtistContextMenu(ArtistInfo artist)
	{
		insertPlayMenuItem.Visible = false;
		likeSongMenuItem.Visible = false;
		unlikeSongMenuItem.Visible = false;
		addToPlaylistMenuItem.Visible = false;
		removeFromPlaylistMenuItem.Visible = false;
		downloadSongMenuItem.Visible = false;
		downloadPlaylistMenuItem.Visible = false;
		downloadAlbumMenuItem.Visible = false;
		batchDownloadMenuItem.Visible = false;
		downloadCategoryMenuItem.Visible = false;
		batchDownloadPlaylistsMenuItem.Visible = false;
		cloudMenuSeparator.Visible = false;
		subscribePlaylistMenuItem.Visible = false;
		unsubscribePlaylistMenuItem.Visible = false;
		deletePlaylistMenuItem.Visible = false;
		subscribeAlbumMenuItem.Visible = false;
		unsubscribeAlbumMenuItem.Visible = false;
		shareArtistMenuItem.Visible = true;
		shareArtistMenuItem.Tag = artist;
		subscribeArtistMenuItem.Tag = artist;
		unsubscribeArtistMenuItem.Tag = artist;
		bool flag = shareArtistMenuItem.Visible;
		if (IsUserLoggedIn())
		{
			bool flag2 = IsArtistSubscribed(artist);
			subscribeArtistMenuItem.Visible = !flag2;
			unsubscribeArtistMenuItem.Visible = flag2;
			flag |= subscribeArtistMenuItem.Visible || unsubscribeArtistMenuItem.Visible;
		}
		else
		{
			subscribeArtistMenuItem.Visible = false;
			unsubscribeArtistMenuItem.Visible = false;
		}
		toolStripSeparatorArtist.Visible = flag;
	}

	private ArtistInfo? GetArtistFromMenuSender(object sender)
	{
		if (sender is ToolStripMenuItem { Tag: ArtistInfo tag })
		{
			return tag;
		}
		return GetSelectedArtistFromSelection();
	}

	private ArtistInfo? GetSelectedArtistFromSelection()
	{
		if (resultListView.SelectedItems.Count == 0)
		{
			return null;
		}
		ListViewItem listViewItem = resultListView.SelectedItems[0];
		if (listViewItem.Tag is ArtistInfo result)
		{
			return result;
		}
		if (listViewItem.Tag is ListItemInfo { Type: ListItemType.Artist } listItemInfo)
		{
			return listItemInfo.Artist;
		}
		return null;
	}

	private void shareArtistMenuItem_Click(object sender, EventArgs e)
	{
		ArtistInfo artistFromMenuSender = GetArtistFromMenuSender(sender);
		if (artistFromMenuSender == null)
		{
			return;
		}
		try
		{
			string text = $"https://music.163.com/#/artist?id={artistFromMenuSender.Id}";
			Clipboard.SetText(text);
			UpdateStatusBar("歌手链接已复制到剪贴板");
		}
		catch (Exception ex)
		{
			MessageBox.Show("复制链接失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("复制链接失败");
		}
	}

	private async void subscribeArtistMenuItem_Click(object sender, EventArgs e)
	{
		ArtistInfo artist = GetArtistFromMenuSender(sender);
		if (artist == null)
		{
			return;
		}
		if (!IsUserLoggedIn())
		{
			MessageBox.Show("请先登录后再收藏歌手", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			return;
		}
		try
		{
			UpdateStatusBar("正在收藏歌手...");
			if (await _apiClient.SetArtistSubscriptionAsync(artist.Id, subscribe: true))
			{
				artist.IsSubscribed = true;
				UpdateArtistSubscriptionState(artist.Id, isSubscribed: true);
				MessageBox.Show("已收藏歌手：" + artist.Name, "成功", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				UpdateStatusBar("收藏歌手成功");
				await RefreshArtistListAfterSubscriptionAsync(artist.Id);
			}
			else
			{
				MessageBox.Show("收藏歌手失败，请稍后重试。", "失败", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("收藏歌手失败");
			}
		}
		catch (Exception ex)
		{
			MessageBox.Show("收藏歌手失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("收藏歌手失败");
		}
	}

	private async void unsubscribeArtistMenuItem_Click(object sender, EventArgs e)
	{
		ArtistInfo artist = GetArtistFromMenuSender(sender);
		if (artist == null)
		{
			return;
		}
		if (!IsUserLoggedIn())
		{
			MessageBox.Show("请先登录后再取消收藏歌手", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			return;
		}
		try
		{
			UpdateStatusBar("正在取消收藏歌手...");
			if (await _apiClient.SetArtistSubscriptionAsync(artist.Id, subscribe: false))
			{
				artist.IsSubscribed = false;
				UpdateArtistSubscriptionState(artist.Id, isSubscribed: false);
				MessageBox.Show("已取消收藏歌手：" + artist.Name, "成功", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
				UpdateStatusBar("取消收藏歌手成功");
				await RefreshArtistListAfterSubscriptionAsync(artist.Id);
			}
			else
			{
				MessageBox.Show("取消收藏歌手失败，请稍后重试。", "失败", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("取消收藏歌手失败");
			}
		}
		catch (Exception ex)
		{
			MessageBox.Show("取消收藏歌手失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			UpdateStatusBar("取消收藏歌手失败");
		}
	}

	private void ApplyArtistDetailToArtist(ArtistInfo artist, ArtistDetail detail)
	{
		if (artist != null && detail != null)
		{
			bool flag = !string.IsNullOrWhiteSpace(detail.Name);
			bool flag2 = string.IsNullOrWhiteSpace(artist.Name) || string.Equals(artist.Name, "歌手", StringComparison.OrdinalIgnoreCase) || (artist.Id > 0 && string.Equals(artist.Name, $"歌手 {artist.Id}", StringComparison.OrdinalIgnoreCase));
			if (flag && (flag2 || !string.Equals(artist.Name, detail.Name, StringComparison.OrdinalIgnoreCase)))
			{
				artist.Name = detail.Name;
			}
			artist.MusicCount = detail.MusicCount;
			artist.AlbumCount = detail.AlbumCount;
			artist.MvCount = detail.MvCount;
			artist.BriefDesc = (string.IsNullOrWhiteSpace(detail.BriefDesc) ? artist.BriefDesc : detail.BriefDesc);
			artist.Description = (string.IsNullOrWhiteSpace(detail.Description) ? artist.Description : detail.Description);
			artist.IsSubscribed = detail.IsSubscribed;
			UpdateArtistStatsCache(artist.Id, detail.MusicCount, detail.AlbumCount);
			UpdateArtistStatsInView(artist.Id, detail.MusicCount, detail.AlbumCount);
		}
	}

	private bool TryGetCachedArtistStats(long artistId, out (int MusicCount, int AlbumCount) stats)
	{
		lock (_artistStatsCache)
		{
			return _artistStatsCache.TryGetValue(artistId, out stats);
		}
	}

	private void UpdateArtistStatsCache(long artistId, int musicCount, int albumCount)
	{
		if (artistId <= 0)
		{
			return;
		}
		lock (_artistStatsCache)
		{
			_artistStatsCache[artistId] = (musicCount, albumCount);
		}
	}

	private void UpdateArtistStatsInView(long artistId, int musicCount, int albumCount)
	{
		SafeInvoke(delegate
		{
			foreach (ListViewItem item in resultListView.Items)
			{
				if (item.Tag is ArtistInfo artistInfo && artistInfo.Id == artistId)
				{
					artistInfo.MusicCount = musicCount;
					artistInfo.AlbumCount = albumCount;
					if (item.SubItems.Count > 2)
					{
						item.SubItems[2].Text = BuildArtistStatsLabel(musicCount, albumCount);
					}
					break;
				}
			}
		});
	}

	private void ScheduleArtistStatsRefresh(IEnumerable<ArtistInfo> artists)
	{
		if (_apiClient == null)
		{
			return;
		}
		// 这里不能只刷前 20 条，否则搜索结果（通常 100 条）的大部分歌手永远停留在 0。
		// 统一收集当前视图里所有缺少统计的歌手，逐个异步刷新并写回列表。
		List<ArtistInfo> pending = (from a in artists?.Where((ArtistInfo a) => a != null && a.Id > 0 && (a.MusicCount <= 0 || a.AlbumCount <= 0))
			group a by a.Id into g
			select g.First()).ToList();
		if (pending == null || pending.Count == 0)
		{
			return;
		}
		_artistStatsRefreshCts?.Cancel();
		_artistStatsRefreshCts?.Dispose();
		CancellationToken token = (_artistStatsRefreshCts = new CancellationTokenSource()).Token;
		lock (_artistStatsInFlight)
		{
			_artistStatsInFlight.Clear();
		}
		Task.Run(async delegate
		{
			try
			{
				foreach (ArtistInfo artist in pending)
				{
					if (token.IsCancellationRequested)
					{
						break;
					}
					if (TryGetCachedArtistStats(artist.Id, out var cachedStats))
					{
						UpdateArtistStatsInView(artist.Id, cachedStats.MusicCount, cachedStats.AlbumCount);
					}
					else
					{
						bool shouldFetch;
						lock (_artistStatsInFlight)
						{
							shouldFetch = _artistStatsInFlight.Add(artist.Id);
						}
						if (shouldFetch)
						{
							try
							{
								ArtistDetail detail = await FetchWithRetryUntilCancel((CancellationToken ct) => _apiClient.GetArtistDetailAsync(artist.Id, includeIntroduction: false), $"artist_stats:{artist.Id}", token, delegate(int attempt, Exception ex6)
								{
									Debug.WriteLine($"[Artist] 刷新歌手统计失败（第 {attempt} 次）: {ex6.Message}");
								}).ConfigureAwait(continueOnCapturedContext: false);
								if (detail != null)
								{
									UpdateArtistStatsCache(artist.Id, detail.MusicCount, detail.AlbumCount);
									UpdateArtistStatsInView(artist.Id, detail.MusicCount, detail.AlbumCount);
								}
							}
							catch (OperationCanceledException)
							{
								break;
							}
							catch (Exception ex2)
							{
								Exception ex3 = ex2;
								Debug.WriteLine("[Artist] 刷新歌手统计放弃: " + ex3.Message);
							}
							finally
							{
								lock (_artistStatsInFlight)
								{
									_artistStatsInFlight.Remove(artist.Id);
								}
							}
							try
							{
								await Task.Delay(150, token).ConfigureAwait(continueOnCapturedContext: false);
							}
							catch (OperationCanceledException)
							{
								break;
							}
						}
					}
				}
			}
			catch (OperationCanceledException)
			{
			}
		}, token);
	}

	private async Task RefreshArtistListAfterSubscriptionAsync(long artistId)
	{
		try
		{
			if (string.Equals(_currentViewSource, "artist_favorites", StringComparison.OrdinalIgnoreCase))
			{
				await LoadArtistFavoritesAsync(skipSave: true, preserveSelection: true);
			}
			else if (_currentViewSource != null && _currentViewSource.StartsWith("artist_songs:", StringComparison.OrdinalIgnoreCase))
			{
				await LoadArtistSongsAsync(artistId, _currentArtistSongsOffset, skipSave: true, _artistSongSortState.CurrentOption);
			}
			else if (_currentViewSource != null && _currentViewSource.StartsWith("artist_albums:", StringComparison.OrdinalIgnoreCase))
			{
				await LoadArtistAlbumsAsync(artistId, _currentArtistAlbumsOffset, skipSave: true, _artistAlbumSortState.CurrentOption);
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Debug.WriteLine($"[Artist] 刷新列表失败: {ex2}");
		}
	}

	private static long ParseArtistIdFromViewSource(string source, string prefix)
	{
		if (!source.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
		{
			return 0L;
		}
		string text = source.Substring(prefix.Length);
		int num = text.IndexOf(':');
		if (num >= 0)
		{
			text = text.Substring(0, num);
		}
		long result;
		return long.TryParse(text, out result) ? result : 0;
	}

	private static void ParseArtistListViewSource(string source, out long artistId, out int offset, out string order, string defaultOrder = "hot")
	{
		artistId = 0L;
		offset = 0;
		order = defaultOrder;
		string[] array = source.Split(':');
		if (array.Length >= 2)
		{
			long.TryParse(array[1], out artistId);
		}
		string text = array.LastOrDefault((string p) => p.StartsWith("offset", StringComparison.OrdinalIgnoreCase));
		if (!string.IsNullOrEmpty(text))
		{
			int.TryParse(text.Substring("offset".Length), out offset);
		}
		checked
		{
			for (int num = 0; num < array.Length; num++)
			{
				string text2 = array[num];
				if (text2.StartsWith("order", StringComparison.OrdinalIgnoreCase))
				{
					if (text2.Length > "order".Length)
					{
						order = text2.Substring("order".Length);
						break;
					}
					if (num + 1 < array.Length)
					{
						order = array[num + 1];
						break;
					}
				}
			}
		}
	}

	private static void ParseArtistCategoryListViewSource(string source, out int typeCode, out int areaCode, out int offset)
	{
		typeCode = -1;
		areaCode = -1;
		offset = 0;
		string[] array = source.Split(':');
		if (array.Length >= 3)
		{
			int.TryParse(array[1], out typeCode);
			int.TryParse(array[2], out areaCode);
		}
		string text = array.LastOrDefault((string p) => p.StartsWith("offset", StringComparison.OrdinalIgnoreCase));
		if (!string.IsNullOrEmpty(text))
		{
			int.TryParse(text.Substring("offset".Length), out offset);
		}
	}

	private static string MapArtistSongsOrder(ArtistSongSortOption order)
	{
		return (order == ArtistSongSortOption.Time) ? "time" : "hot";
	}

	private static ArtistSongSortOption ResolveArtistSongsOrder(string? order)
	{
		return string.Equals(order, "time", StringComparison.OrdinalIgnoreCase) ? ArtistSongSortOption.Time : ArtistSongSortOption.Hot;
	}

	private static string MapArtistAlbumSort(ArtistAlbumSortOption sort)
	{
		return (sort == ArtistAlbumSortOption.Oldest) ? "oldest" : "latest";
	}

	private static ArtistAlbumSortOption ResolveArtistAlbumSort(string? sort)
	{
		return string.Equals(sort, "oldest", StringComparison.OrdinalIgnoreCase) ? ArtistAlbumSortOption.Oldest : ArtistAlbumSortOption.Latest;
	}

	private async Task EnsureArtistAlbumsTotalCountAsync(long artistId)
	{
		if (_currentArtistAlbumsTotalCount <= 0)
		{
			int totalCount = (await _apiClient.GetArtistAlbumsAsync(artistId, 1)).Item3;
			_currentArtistAlbumsTotalCount = totalCount;
		}
	}

	private void ClearArtistAlbumsAscendingCache(long? artistId = null)
	{
		if (artistId.HasValue)
		{
			_artistAlbumsAscendingCache.Remove(artistId.Value);
		}
		else
		{
			_artistAlbumsAscendingCache.Clear();
		}
	}

	private void TrimArtistAlbumsAscendingCache()
	{
		while (_artistAlbumsAscendingCache.Count > 4)
		{
			long num = _artistAlbumsAscendingCache.Keys.FirstOrDefault();
			if (num != 0)
			{
				_artistAlbumsAscendingCache.Remove(num);
				continue;
			}
			break;
		}
	}

	private async Task<List<AlbumInfo>> LoadArtistAlbumsAscendingListAsync(long artistId)
	{
		List<AlbumInfo> allAlbums = new List<AlbumInfo>();
		int offset = 0;
		bool hasMore = true;
		int safety = 0;
		checked
		{
			while (hasMore && safety < 200)
			{
				List<AlbumInfo> albums;
				bool more;
				int total;
				(albums, more, total) = await _apiClient.GetArtistAlbumsAsync(artistId, 100, offset);
				if (albums == null || albums.Count == 0)
				{
					break;
				}
				allAlbums.AddRange(albums);
				offset += albums.Count;
				hasMore = more;
				_currentArtistAlbumsTotalCount = total;
				safety++;
			}
			allAlbums.Reverse();
			return allAlbums;
		}
	}

	private async Task<(List<AlbumInfo> Albums, int NormalizedOffset, bool HasMore)> LoadArtistAlbumsAscendingPageAsync(long artistId, int offset)
	{
		if (!_artistAlbumsAscendingCache.TryGetValue(artistId, out List<AlbumInfo> cachedAlbums))
		{
			cachedAlbums = await LoadArtistAlbumsAscendingListAsync(artistId);
			_artistAlbumsAscendingCache[artistId] = cachedAlbums;
			TrimArtistAlbumsAscendingCache();
		}
		int totalCount = cachedAlbums.Count;
		if (totalCount == 0)
		{
			return (Albums: new List<AlbumInfo>(), NormalizedOffset: 0, HasMore: false);
		}
		checked
		{
			int normalizedOffset = Math.Max(0, Math.Min(offset, Math.Max(0, totalCount - 1)));
			int remaining = Math.Max(0, totalCount - normalizedOffset);
			int takeCount = Math.Min(100, remaining);
			if (takeCount <= 0)
			{
				return (Albums: new List<AlbumInfo>(), NormalizedOffset: normalizedOffset, HasMore: false);
			}
			List<AlbumInfo> page = cachedAlbums.Skip(normalizedOffset).Take(takeCount).ToList();
			bool hasMore = normalizedOffset + takeCount < totalCount;
			return (Albums: page, NormalizedOffset: normalizedOffset, HasMore: hasMore);
		}
	}

	private async Task LoadCloudSongsAsync(bool skipSave = false, bool preserveSelection = false)
	{
		if (_apiClient == null)
		{
			return;
		}
		checked
		{
			if (!IsUserLoggedIn())
			{
				MessageBox.Show("请先登录后再访问云盘", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			}
			else
			{
				if (_cloudLoading)
				{
					return;
				}
				try
				{
					_cloudLoading = true;
					int pendingIndex = 0;
					int num;
					if (preserveSelection)
					{
						ListView listView = resultListView;
						num = ((listView != null && listView.SelectedIndices.Count > 0) ? 1 : 0);
					}
					else
					{
						num = 0;
					}
					if (num != 0)
					{
						pendingIndex = Math.Max(0, resultListView.SelectedIndices[0]);
					}
					if (preserveSelection)
					{
						CacheCurrentCloudSelection();
					}
					if (!skipSave)
					{
						SaveNavigationState();
					}
					_isHomePage = false;
					ViewLoadRequest request = new ViewLoadRequest("user_cloud", "云盘歌曲", "正在加载云盘歌曲...", cancelActiveNavigation: true, pendingIndex);
					ViewLoadResult<CloudPageViewData?> loadResult = await RunViewLoadAsync(request, async delegate(CancellationToken token)
					{
						int offset = Math.Max(0, (_cloudPage - 1) * 50);
						CloudSongPageResult pageResult = await FetchWithRetryUntilCancel((CancellationToken ct) => _apiClient.GetCloudSongsAsync(50, offset), $"user_cloud:page{_cloudPage}", token, delegate(int attempt, Exception _)
						{
							SafeInvoke(delegate
							{
								UpdateStatusBar($"加载云盘失败，正在重试（第 {attempt} 次）...");
							});
						}).ConfigureAwait(continueOnCapturedContext: true);
						return (pageResult == null) ? new CloudPageViewData(new List<SongInfo>(), hasMore: false, 0, 0L, 0L, offset) : new CloudPageViewData(pageResult.Songs ?? new List<SongInfo>(), pageResult.HasMore, pageResult.TotalCount, pageResult.UsedSize, pageResult.MaxSize, offset);
					}, "加载云盘已取消");
					if (!loadResult.IsCanceled)
					{
						CloudPageViewData data = loadResult.Value ?? new CloudPageViewData(new List<SongInfo>(), hasMore: false, 0, 0L, 0L, Math.Max(0, (_cloudPage - 1) * 50));
						_cloudHasMore = data.HasMore;
						_cloudTotalCount = data.TotalCount;
						_cloudUsedSize = data.UsedSize;
						_cloudMaxSize = data.MaxSize;
						_currentPage = _cloudPage;
						_maxPage = (_cloudHasMore ? (_cloudPage + 1) : _cloudPage);
						_hasNextSearchPage = data.HasMore;
						DisplaySongs(data.Songs, showPagination: true, data.HasMore, data.Offset + 1, preserveSelection, "user_cloud", "云盘歌曲", skipAvailabilityCheck: true);
						RestoreCloudSelection();
						UpdateStatusBar(BuildCloudStatusText(data.Songs.Count));
					}
				}
				catch (Exception ex)
				{
					Exception ex2 = ex;
					Debug.WriteLine($"[Cloud] 加载云盘失败: {ex2}");
					MessageBox.Show("加载云盘失败: " + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
					UpdateStatusBar("加载云盘失败");
				}
				finally
				{
					_cloudLoading = false;
				}
			}
		}
	}

	private string BuildCloudStatusText(int currentCount)
	{
		string text = FormatSize(_cloudUsedSize);
		string text2 = ((_cloudMaxSize > 0) ? FormatSize(_cloudMaxSize) : "未知");
		return $"云盘 - 第 {_cloudPage} 页，本页 {currentCount} 首 / 总 {_cloudTotalCount} 首，已用 {text} / {text2}";
	}

	private static string FormatSize(long bytes)
	{
		if (bytes <= 0)
		{
			return "0 B";
		}
		string[] array = new string[5] { "B", "KB", "MB", "GB", "TB" };
		int num = 0;
		double num2 = bytes;
		checked
		{
			while (num2 >= 1024.0 && num < array.Length - 1)
			{
				num++;
				num2 /= 1024.0;
			}
			return $"{num2:0.##} {array[num]}";
		}
	}

	private Task UploadCloudSongsAsync(string[] filePaths)
	{
		if (filePaths == null || filePaths.Length == 0)
		{
			return Task.CompletedTask;
		}
		if (!IsUserLoggedIn())
		{
			MessageBox.Show("请先登录后再上传云盘歌曲", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			return Task.CompletedTask;
		}
		string[] array = filePaths.Where((string path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
		if (array.Length == 0)
		{
			MessageBox.Show("未找到可上传的音频文件", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			return Task.CompletedTask;
		}
		UploadManager instance = UploadManager.Instance;
		List<UploadTask> list = instance.AddBatchUploadTasks(array, "云盘");
		UpdateStatusBar($"已添加 {list.Count} 个上传任务到传输管理器");
		return Task.CompletedTask;
	}

	private async Task DeleteSelectedCloudSongAsync()
	{
		SongInfo song = GetSelectedCloudSong();
		if (song == null)
		{
			MessageBox.Show("请选择要删除的云盘歌曲", "提示", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
		}
		else if (string.IsNullOrEmpty(song.CloudSongId))
		{
			MessageBox.Show("无法删除选中的歌曲", "提示", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
		}
		else
		{
			if (MessageBox.Show("确定要从云盘删除歌曲：\n" + song.Name + " - " + song.Artist, "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
			{
				return;
			}
			string fallbackFocusId = DetermineNeighborCloudSongId(song);
			try
			{
				UpdateStatusBar("正在删除云盘歌曲...");
				if (await _apiClient.DeleteCloudSongsAsync(new string[1] { song.CloudSongId }))
				{
					UpdateStatusBar("云盘歌曲已删除");
					_lastSelectedCloudSongId = fallbackFocusId;
					RequestCloudRefresh(fallbackFocusId, preserveSelection: false);
				}
				else
				{
					MessageBox.Show("删除云盘歌曲失败，请稍后重试。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
					UpdateStatusBar("删除云盘歌曲失败");
				}
			}
			catch (Exception ex)
			{
				MessageBox.Show("删除云盘歌曲失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
				UpdateStatusBar("删除云盘歌曲失败");
			}
		}
	}

	private SongInfo? GetSelectedCloudSong()
	{
		if (resultListView.SelectedItems.Count == 0)
		{
			return null;
		}
		return GetSongFromListViewItem(resultListView.SelectedItems[0]);
	}

	private SongInfo? GetSongFromListViewItem(ListViewItem? item)
	{
		if (item?.Tag is int num && num >= 0 && num < _currentSongs.Count)
		{
			SongInfo songInfo = _currentSongs[num];
			return (songInfo != null && songInfo.IsCloudSong) ? songInfo : null;
		}
		return null;
	}

	private void CacheCurrentCloudSelection()
	{
		SongInfo selectedCloudSong = GetSelectedCloudSong();
		if (selectedCloudSong != null && !string.IsNullOrEmpty(selectedCloudSong.CloudSongId))
		{
			_pendingCloudFocusId = selectedCloudSong.CloudSongId;
			_lastSelectedCloudSongId = selectedCloudSong.CloudSongId;
		}
	}

	private void RestoreCloudSelection()
	{
		if (!string.Equals(_currentViewSource, "user_cloud", StringComparison.OrdinalIgnoreCase))
		{
			_pendingCloudFocusId = null;
			return;
		}
		string text = _pendingCloudFocusId ?? _lastSelectedCloudSongId;
		if (string.IsNullOrEmpty(text))
		{
			return;
		}
		for (int i = 0; i < resultListView.Items.Count; i = checked(i + 1))
		{
			if (resultListView.Items[i].Tag is int num && num >= 0 && num < _currentSongs.Count)
			{
				SongInfo songInfo = _currentSongs[num];
				if (songInfo != null && songInfo.IsCloudSong && string.Equals(songInfo.CloudSongId, text, StringComparison.Ordinal))
				{
					resultListView.Items[i].Selected = true;
					resultListView.FocusedItem = resultListView.Items[i];
					resultListView.EnsureVisible(i);
					_lastSelectedCloudSongId = songInfo.CloudSongId;
					break;
				}
			}
		}
		_pendingCloudFocusId = null;
	}

	private void RequestCloudRefresh(string? focusCloudSongId = null, bool preserveSelection = true)
	{
		SafeInvoke(Runner);
		async void RefreshImpl()
		{
			try
			{
				if (!string.Equals(_currentViewSource, "user_cloud", StringComparison.OrdinalIgnoreCase))
				{
					if (!string.IsNullOrEmpty(focusCloudSongId))
					{
						_pendingCloudFocusId = focusCloudSongId;
						_lastSelectedCloudSongId = focusCloudSongId;
					}
				}
				else
				{
					if (!string.IsNullOrEmpty(focusCloudSongId))
					{
						_pendingCloudFocusId = focusCloudSongId;
					}
					else if (preserveSelection)
					{
						CacheCurrentCloudSelection();
					}
					int waitAttempts = 0;
					while (_cloudLoading && waitAttempts < 10)
					{
						await Task.Delay(200);
						waitAttempts = checked(waitAttempts + 1);
					}
					await LoadCloudSongsAsync(skipSave: true, preserveSelection);
				}
			}
			catch (Exception ex)
			{
				Exception ex2 = ex;
				Debug.WriteLine("[Cloud] 刷新云盘失败: " + ex2.Message);
			}
		}
		void Runner()
		{
			RefreshImpl();
		}
	}

	private async void uploadToCloudMenuItem_Click(object sender, EventArgs e)
	{
		using OpenFileDialog dialog = new OpenFileDialog
		{
			Multiselect = true,
			Filter = "音频文件|*.mp3;*.flac;*.wav;*.m4a;*.ogg;*.ape;*.wma|所有文件|*.*"
		};
		int num;
		if (dialog.ShowDialog(this) == DialogResult.OK)
		{
			string[] fileNames = dialog.FileNames;
			num = ((fileNames != null && fileNames.Length != 0) ? 1 : 0);
		}
		else
		{
			num = 0;
		}
		if (num != 0)
		{
			await UploadCloudSongsAsync(dialog.FileNames);
		}
	}

	private async void deleteFromCloudMenuItem_Click(object sender, EventArgs e)
	{
		await DeleteSelectedCloudSongAsync();
	}

	private string? DetermineNeighborCloudSongId(SongInfo currentSong)
	{
		if (currentSong == null || _currentSongs == null || _currentSongs.Count == 0)
		{
			return null;
		}
		int num = _currentSongs.IndexOf(currentSong);
		if (num < 0)
		{
			return null;
		}
		checked
		{
			for (int i = num + 1; i < _currentSongs.Count; i++)
			{
				SongInfo songInfo = _currentSongs[i];
				if (songInfo != null && songInfo.IsCloudSong && !string.IsNullOrEmpty(songInfo.CloudSongId))
				{
					return songInfo.CloudSongId;
				}
			}
			for (int num2 = num - 1; num2 >= 0; num2--)
			{
				SongInfo songInfo2 = _currentSongs[num2];
				if (songInfo2 != null && songInfo2.IsCloudSong && !string.IsNullOrEmpty(songInfo2.CloudSongId))
				{
					return songInfo2.CloudSongId;
				}
			}
			return null;
		}
	}

	private void OnCloudUploadTaskCompleted(UploadTask task)
	{
		if (task != null)
		{
			SafeInvoke(Handler);
		}
		void Handler()
		{
			if (IsUserLoggedIn())
			{
				string text = ((!string.IsNullOrEmpty(task.CloudSongId)) ? task.CloudSongId : null);
				if (!string.IsNullOrEmpty(text))
				{
					_lastSelectedCloudSongId = text;
					_cloudPage = 1;
				}
				RequestCloudRefresh(text, string.IsNullOrEmpty(text));
			}
		}
	}

	private void OnCloudUploadTaskFailed(UploadTask task)
	{
		if (task == null)
		{
			return;
		}
		SafeInvoke(delegate
		{
			if (!(_lastNotifiedUploadFailureTaskId == task.TaskId))
			{
				_lastNotifiedUploadFailureTaskId = task.TaskId;
				string text = ((!string.IsNullOrWhiteSpace(task.ErrorMessage)) ? task.ErrorMessage : ((!string.IsNullOrWhiteSpace(task.StageMessage)) ? task.StageMessage : "未知错误"));
				UpdateStatusBar("云盘上传失败：" + text);
				MessageBox.Show("云盘上传失败：" + text + "\n\n文件：" + task.FileName, "云盘上传失败", MessageBoxButtons.OK, MessageBoxIcon.Hand);
			}
		});
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
					AccessibleName = "听歌识曲",
					AccessibleDescription = "录制环境音并识别当前播放的歌曲",
					ShortcutKeys = (Keys.L | Keys.Control)
				};
				_listenRecognitionMenuItem.Click += async delegate
				{
					await StartSongRecognitionAsync();
				};
				int num = fileMenuItem.DropDownItems.IndexOf(loginMenuItem);
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
			MessageBox.Show(this, "枚举录音设备失败：" + ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
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
		catch (NotSupportedException ex3)
		{
			NotSupportedException nse = ex3;
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
			Exception ex5 = ex;
			Debug.WriteLine($"[Recognition] 识曲失败: {ex5}");
			MessageBox.Show(this, "听歌识曲失败：" + ex5.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
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
		if (!(_config?.RecognitionAutoCloseDialog ?? false) || _listenRecognitionMenuItem == null)
		{
		}
	}

	private async Task ShowRecognitionResultsAsync(SongRecognitionResult result)
	{
		List<SongInfo> newSongs = result.Matches?.ToList() ?? new List<SongInfo>();
		bool isListeningView = !string.IsNullOrWhiteSpace(_currentViewSource) && _currentViewSource.StartsWith("listen-match:", StringComparison.OrdinalIgnoreCase) && _currentSongs != null && _currentSongs.Count > 0;
		List<SongInfo> existingSongs = (isListeningView ? _currentSongs.ToList() : new List<SongInfo>());
		int addedCount;
		List<SongInfo> mergedSongs = MergeRecognitionSongs(newSongs, existingSongs, out addedCount);
		string viewSource = (isListeningView ? _currentViewSource : ("listen-match:" + result.SessionId));
		EnsureSearchTypeSelection("歌曲");
		_currentSearchType = "歌曲";
		_lastKeyword = "听歌识曲";
		_currentPage = 1;
		_maxPage = 1;
		_hasNextSearchPage = false;
		_isHomePage = false;
		ViewLoadRequest request = new ViewLoadRequest(viewSource, "听歌识曲", isListeningView ? "正在更新听歌识曲结果..." : "正在载入听歌识曲结果...", cancelActiveNavigation: true, 0, suppressLoadingFocus: false);
		if (!(await RunViewLoadAsync(request, (CancellationToken _) => Task.FromResult(mergedSongs), "听歌识曲已取消").ConfigureAwait(continueOnCapturedContext: true)).IsCanceled)
		{
			_currentSongs = mergedSongs;
			_currentPlaylist = null;
			_currentMixedQueryKey = null;
			await ExecuteOnUiThreadAsync(delegate
			{
				DisplaySongs(_currentSongs, showPagination: false, hasNextPage: false, 1, preserveSelection: false, viewSource, "听歌识曲");
				string message = ((mergedSongs.Count <= 0) ? "未能识别出歌曲" : (isListeningView ? $"识别到 {addedCount} 首新歌曲，累计 {mergedSongs.Count} 首" : $"识别到 {mergedSongs.Count} 首歌曲"));
				UpdateStatusBar(message);
				AnnounceListViewHeaderIfNeeded("听歌识曲");
			}).ConfigureAwait(continueOnCapturedContext: true);
		}
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
		UpdateStatusBar(loadingText);
		resultListView.BeginUpdate();
		try
		{
			resultListView.Items.Clear();
			ListViewItem value = new ListViewItem(loadingText)
			{
				Tag = null
			};
			resultListView.Items.Add(value);
		}
		finally
		{
			resultListView.EndUpdate();
		}
		string accessibleName2 = (string.IsNullOrWhiteSpace(accessibleName) ? "列表" : accessibleName);
		SetViewContext(viewSource, accessibleName2);
		AnnounceListViewHeaderIfNeeded(accessibleName2);
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
			RequestListFocus(request.ViewSource, request.PendingFocusIndex);
			ShowLoadingPlaceholderCore(request.ViewSource, request.AccessibleName, request.LoadingText);
			if (!request.SuppressLoadingFocus && !IsListAutoFocusSuppressed && resultListView != null && resultListView.Items.Count != 0)
			{
				int targetIndex = ((request.PendingFocusIndex >= 0) ? Math.Max(0, Math.Min(request.PendingFocusIndex, checked(resultListView.Items.Count - 1))) : 0);
				EnsureListSelectionWithoutFocus(targetIndex);
				if (resultListView.CanFocus)
				{
					resultListView.Focus();
				}
			}
		});
	}

	private void RequestListFocus(string? viewSource, int pendingIndex)
	{
		if (string.IsNullOrWhiteSpace(viewSource) || pendingIndex < 0)
		{
			_pendingListFocusViewSource = null;
			_pendingListFocusIndex = -1;
		}
		else
		{
			_pendingListFocusViewSource = viewSource;
			_pendingListFocusIndex = pendingIndex;
		}
	}

	private int ResolvePendingListFocusIndex(int fallbackIndex)
	{
		if (_pendingListFocusIndex >= 0 && !string.IsNullOrWhiteSpace(_pendingListFocusViewSource) && string.Equals(_pendingListFocusViewSource, _currentViewSource, StringComparison.OrdinalIgnoreCase))
		{
			int pendingListFocusIndex = _pendingListFocusIndex;
			_pendingListFocusIndex = -1;
			_pendingListFocusViewSource = null;
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

	private async Task<ViewLoadResult<T>> RunViewLoadAsync<T>(ViewLoadRequest request, Func<CancellationToken, Task<T>> loader, string cancellationStatusText)
	{
		if (loader == null)
		{
			throw new ArgumentNullException("loader");
		}
		CancellationToken viewToken = BeginViewContentOperation(request.CancelActiveNavigation);
		await ShowLoadingStateAsync(request).ConfigureAwait(continueOnCapturedContext: true);
		await Task.Yield();
		try
		{
			T result = await Task.Run(async () => await loader(viewToken).ConfigureAwait(continueOnCapturedContext: false), CancellationToken.None).ConfigureAwait(continueOnCapturedContext: true);
			if (viewToken.IsCancellationRequested)
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
	}

	private Task ExecuteOnUiThreadAsync(Action action)
	{
		if (!base.InvokeRequired)
		{
			action();
			return Task.CompletedTask;
		}
		TaskCompletionSource<object?> tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
		BeginInvoke((Action)delegate
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
		this.helpMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.checkUpdateMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.donateMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.shortcutsMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.aboutMenuItem = new System.Windows.Forms.ToolStripMenuItem();
		this.searchPanel = new System.Windows.Forms.Panel();
		this.searchTypeComboBox = new System.Windows.Forms.ComboBox();
		this.searchTypeLabel = new System.Windows.Forms.Label();
		this.searchButton = new System.Windows.Forms.Button();
		this.searchTextBox = new System.Windows.Forms.TextBox();
		this.searchLabel = new System.Windows.Forms.Label();
		this.resultListView = new System.Windows.Forms.ListView();
		this.columnHeader1 = new System.Windows.Forms.ColumnHeader();
		this.columnHeader2 = new System.Windows.Forms.ColumnHeader();
		this.columnHeader3 = new System.Windows.Forms.ColumnHeader();
		this.columnHeader4 = new System.Windows.Forms.ColumnHeader();
		this.columnHeader5 = new System.Windows.Forms.ColumnHeader();
		this.controlPanel = new System.Windows.Forms.Panel();
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
		this.volumeTrackBar = new System.Windows.Forms.TrackBar();
		this.timeLabel = new System.Windows.Forms.Label();
		this.progressTrackBar = new System.Windows.Forms.TrackBar();
		this.playPauseButton = new System.Windows.Forms.Button();
		this.currentSongLabel = new System.Windows.Forms.Label();
		this.statusStrip1 = new System.Windows.Forms.StatusStrip();
		this.toolStripStatusLabel1 = new System.Windows.Forms.ToolStripStatusLabel();
		this.menuStrip1.SuspendLayout();
		this.searchPanel.SuspendLayout();
		this.controlPanel.SuspendLayout();
		((System.ComponentModel.ISupportInitialize)this.volumeTrackBar).BeginInit();
		((System.ComponentModel.ISupportInitialize)this.progressTrackBar).BeginInit();
		this.statusStrip1.SuspendLayout();
		this.songContextMenu.SuspendLayout();
		this.trayContextMenu.SuspendLayout();
		base.SuspendLayout();
		this.menuStrip1.ImageScalingSize = new System.Drawing.Size(20, 20);
		this.menuStrip1.Items.AddRange(new System.Windows.Forms.ToolStripItem[3] { this.fileMenuItem, this.playControlMenuItem, this.helpMenuItem });
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
		this.fileMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[11]
		{
			this.homeMenuItem, this.loginMenuItem, this.currentPlayingMenuItem, this.toolStripSeparatorDownload1, this.openDownloadDirMenuItem, this.changeDownloadDirMenuItem, this.downloadManagerMenuItem, this.refreshMenuItem, this.toolStripSeparatorDownload2, this.hideMenuItem,
			this.exitMenuItem
		});
		this.fileMenuItem.Name = "fileMenuItem";
		this.fileMenuItem.Size = new System.Drawing.Size(98, 24);
		this.fileMenuItem.Text = "文件/操作(&F)";
		this.fileMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[11]
		{
			this.homeMenuItem, this.loginMenuItem, this.currentPlayingMenuItem, this.toolStripSeparatorDownload1, this.openDownloadDirMenuItem, this.changeDownloadDirMenuItem, this.downloadManagerMenuItem, this.refreshMenuItem, this.toolStripSeparatorDownload2, this.hideMenuItem,
			this.exitMenuItem
		});
		this.homeMenuItem.Name = "homeMenuItem";
		this.homeMenuItem.Size = new System.Drawing.Size(178, 26);
		this.homeMenuItem.Text = "主页(&H)";
		this.homeMenuItem.Click += new System.EventHandler(homeMenuItem_Click);
		this.homeMenuItem.AccessibleName = "主页";
		this.homeMenuItem.AccessibleDescription = "返回主页，显示推荐歌单、用户歌单和排行榜";
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
		this.playControlMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[9] { this.playPauseMenuItem, this.toolStripSeparator1, this.playbackMenuItem, this.qualityMenuItem, this.outputDeviceMenuItem, this.prevMenuItem, this.nextMenuItem, this.jumpToPositionMenuItem, this.autoReadLyricsMenuItem });
		this.playControlMenuItem.Name = "playControlMenuItem";
		this.playControlMenuItem.Size = new System.Drawing.Size(98, 24);
		this.playControlMenuItem.Text = "播放/控制(&M)";
		this.playPauseMenuItem.Name = "playPauseMenuItem";
		this.playPauseMenuItem.Size = new System.Drawing.Size(180, 26);
		this.playPauseMenuItem.Text = "播放/暂停\tSpace";
		this.playPauseMenuItem.Click += new System.EventHandler(playPauseMenuItem_Click);
		this.toolStripSeparator1.Name = "toolStripSeparator1";
		this.toolStripSeparator1.Size = new System.Drawing.Size(177, 6);
		this.playbackMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[4] { this.sequentialMenuItem, this.loopMenuItem, this.loopOneMenuItem, this.randomMenuItem });
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
		this.qualityMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[7] { this.standardQualityMenuItem, this.highQualityMenuItem, this.losslessQualityMenuItem, this.hiresQualityMenuItem, this.surroundHDQualityMenuItem, this.dolbyQualityMenuItem, this.masterQualityMenuItem });
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
		this.standardQualityMenuItem.Click += new System.EventHandler(qualityMenuItem_Click);
		this.highQualityMenuItem.Name = "highQualityMenuItem";
		this.highQualityMenuItem.Size = new System.Drawing.Size(180, 26);
		this.highQualityMenuItem.Text = "极高音质";
		this.highQualityMenuItem.Click += new System.EventHandler(qualityMenuItem_Click);
		this.losslessQualityMenuItem.Name = "losslessQualityMenuItem";
		this.losslessQualityMenuItem.Size = new System.Drawing.Size(180, 26);
		this.losslessQualityMenuItem.Text = "无损音质";
		this.losslessQualityMenuItem.Click += new System.EventHandler(qualityMenuItem_Click);
		this.hiresQualityMenuItem.Name = "hiresQualityMenuItem";
		this.hiresQualityMenuItem.Size = new System.Drawing.Size(180, 26);
		this.hiresQualityMenuItem.Text = "Hi-Res音质";
		this.hiresQualityMenuItem.Click += new System.EventHandler(qualityMenuItem_Click);
		this.surroundHDQualityMenuItem.Name = "surroundHDQualityMenuItem";
		this.surroundHDQualityMenuItem.Size = new System.Drawing.Size(180, 26);
		this.surroundHDQualityMenuItem.Text = "高清环绕声";
		this.surroundHDQualityMenuItem.Click += new System.EventHandler(qualityMenuItem_Click);
		this.dolbyQualityMenuItem.Name = "dolbyQualityMenuItem";
		this.dolbyQualityMenuItem.Size = new System.Drawing.Size(180, 26);
		this.dolbyQualityMenuItem.Text = "沉浸环绕声";
		this.dolbyQualityMenuItem.Click += new System.EventHandler(qualityMenuItem_Click);
		this.masterQualityMenuItem.Name = "masterQualityMenuItem";
		this.masterQualityMenuItem.Size = new System.Drawing.Size(180, 26);
		this.masterQualityMenuItem.Text = "超清母带";
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
		this.autoReadLyricsMenuItem.Click += new System.EventHandler(autoReadLyricsMenuItem_Click);
		this.helpMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[4] { this.checkUpdateMenuItem, this.donateMenuItem, this.shortcutsMenuItem, this.aboutMenuItem });
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
		this.searchPanel.Controls.Add(this.searchTextBox);
		this.searchPanel.Controls.Add(this.searchLabel);
		this.searchPanel.Dock = System.Windows.Forms.DockStyle.Top;
		this.searchPanel.Location = new System.Drawing.Point(0, 28);
		this.searchPanel.Name = "searchPanel";
		this.searchPanel.Padding = new System.Windows.Forms.Padding(10);
		this.searchPanel.Size = new System.Drawing.Size(1200, 72);
		this.searchPanel.TabIndex = 0;
		this.searchTypeComboBox.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDown;
		this.searchTypeComboBox.Font = new System.Drawing.Font("Microsoft YaHei UI", 10f);
		this.searchTypeComboBox.FormattingEnabled = true;
		this.searchTypeComboBox.Items.AddRange(new object[5] { "歌曲", "歌单", "专辑", "歌手", "播客" });
		this.searchTypeComboBox.Location = new System.Drawing.Point(620, 40);
		this.searchTypeComboBox.Name = "searchTypeComboBox";
		this.searchTypeComboBox.Size = new System.Drawing.Size(240, 31);
		this.searchTypeComboBox.TabIndex = 2;
		this.searchTypeComboBox.AccessibleName = "类型";
		this.searchTypeComboBox.AccessibleDescription = "选择要搜索的内容类型";
		this.searchTypeComboBox.AccessibleRole = System.Windows.Forms.AccessibleRole.ComboBox;
		this.searchTypeComboBox.KeyDown += new System.Windows.Forms.KeyEventHandler(searchTypeComboBox_KeyDown);
		this.searchTypeComboBox.SelectedIndexChanged += new System.EventHandler(searchTypeComboBox_SelectedIndexChanged);
		this.searchTypeComboBox.DropDownClosed += new System.EventHandler(searchTypeComboBox_DropDownClosed);
		this.searchTypeComboBox.Enter += new System.EventHandler(searchTypeComboBox_Enter);
		this.searchTypeComboBox.KeyPress += new System.Windows.Forms.KeyPressEventHandler(searchTypeComboBox_KeyPress);
		this.searchTypeLabel.AutoSize = true;
		this.searchTypeLabel.Location = new System.Drawing.Point(620, 18);
		this.searchTypeLabel.Name = "searchTypeLabel";
		this.searchTypeLabel.Size = new System.Drawing.Size(84, 20);
		this.searchTypeLabel.TabIndex = 0;
		this.searchTypeLabel.Text = "类型:";
		this.searchButton.Font = new System.Drawing.Font("Microsoft YaHei UI", 12f, System.Drawing.FontStyle.Bold);
		this.searchButton.Location = new System.Drawing.Point(880, 15);
		this.searchButton.Name = "searchButton";
		this.searchButton.Size = new System.Drawing.Size(100, 73);
		this.searchButton.TabIndex = 3;
		this.searchButton.Text = "搜索";
		this.searchButton.UseVisualStyleBackColor = true;
		this.searchButton.AccessibleName = "搜索";
		this.searchButton.Click += new System.EventHandler(searchButton_Click);
		this.searchTextBox.Font = new System.Drawing.Font("Microsoft YaHei UI", 12f);
		this.searchTextBox.Location = new System.Drawing.Point(130, 15);
		this.searchTextBox.Name = "searchTextBox";
		this.searchTextBox.Size = new System.Drawing.Size(470, 33);
		this.searchTextBox.TabIndex = 1;
		this.searchTextBox.AccessibleName = "搜索关键词";
		this.searchTextBox.AccessibleDescription = "输入要搜索的歌曲、歌单、专辑名称或链接，多个链接用分号分隔";
		this.searchTextBox.KeyDown += new System.Windows.Forms.KeyEventHandler(searchTextBox_KeyDown);
		this.searchLabel.AutoSize = true;
		this.searchLabel.Font = new System.Drawing.Font("Microsoft YaHei UI", 12f);
		this.searchLabel.Location = new System.Drawing.Point(13, 18);
		this.searchLabel.Name = "searchLabel";
		this.searchLabel.Size = new System.Drawing.Size(111, 27);
		this.searchLabel.TabIndex = 0;
		this.searchLabel.Text = "搜索关键词:";
		this.resultListView.Columns.AddRange(new System.Windows.Forms.ColumnHeader[5] { this.columnHeader1, this.columnHeader2, this.columnHeader3, this.columnHeader4, this.columnHeader5 });
		this.resultListView.Dock = System.Windows.Forms.DockStyle.Fill;
		this.resultListView.FullRowSelect = true;
		this.resultListView.GridLines = true;
		this.resultListView.HideSelection = false;
		this.resultListView.Location = new System.Drawing.Point(0, 100);
		this.resultListView.MultiSelect = false;
		this.resultListView.Name = "resultListView";
		this.resultListView.Size = new System.Drawing.Size(1200, 408);
		this.resultListView.TabIndex = 1;
		this.resultListView.UseCompatibleStateImageBehavior = false;
		this.resultListView.View = System.Windows.Forms.View.Details;
		this.resultListView.AccessibleName = "正在加载";
		this.resultListView.ContextMenuStrip = this.songContextMenu;
		this.resultListView.ItemActivate += new System.EventHandler(resultListView_ItemActivate);
		this.resultListView.DoubleClick += new System.EventHandler(resultListView_DoubleClick);
		this.resultListView.SelectedIndexChanged += new System.EventHandler(resultListView_SelectedIndexChanged);
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
		this.controlPanel.Location = new System.Drawing.Point(0, 500);
		this.controlPanel.Name = "controlPanel";
		this.controlPanel.Size = new System.Drawing.Size(1200, 150);
		this.controlPanel.TabIndex = 3;
		this.lyricsLabel.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
		this.lyricsLabel.BackColor = System.Drawing.Color.FromArgb(245, 245, 245);
		this.lyricsLabel.Font = new System.Drawing.Font("Microsoft YaHei UI", 11f, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, 0);
		this.lyricsLabel.ForeColor = System.Drawing.Color.FromArgb(51, 51, 51);
		this.lyricsLabel.Location = new System.Drawing.Point(12, 90);
		this.lyricsLabel.Name = "lyricsLabel";
		this.lyricsLabel.Padding = new System.Windows.Forms.Padding(10, 8, 10, 8);
		this.lyricsLabel.Size = new System.Drawing.Size(1176, 50);
		this.lyricsLabel.TabIndex = 7;
		this.lyricsLabel.Text = "暂无歌词";
		this.lyricsLabel.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
		this.lyricsLabel.AccessibleName = "歌词显示";
		this.lyricsLabel.AccessibleRole = System.Windows.Forms.AccessibleRole.Text;
		this.volumeLabel.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right;
		this.volumeLabel.Location = new System.Drawing.Point(1130, 70);
		this.volumeLabel.Name = "volumeLabel";
		this.volumeLabel.Size = new System.Drawing.Size(58, 23);
		this.volumeLabel.TabIndex = 6;
		this.volumeLabel.Text = "100%";
		this.volumeLabel.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
		this.volumeTrackBar.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right;
		this.volumeTrackBar.Location = new System.Drawing.Point(1000, 60);
		this.volumeTrackBar.Maximum = 100;
		this.volumeTrackBar.Name = "volumeTrackBar";
		this.volumeTrackBar.Size = new System.Drawing.Size(124, 56);
		this.volumeTrackBar.TabIndex = 4;
		this.volumeTrackBar.TickFrequency = 10;
		this.volumeTrackBar.Value = 100;
		this.volumeTrackBar.AccessibleName = "音量";
		this.volumeTrackBar.KeyDown += new System.Windows.Forms.KeyEventHandler(volumeTrackBar_KeyDown);
		this.volumeTrackBar.Scroll += new System.EventHandler(volumeTrackBar_Scroll);
		this.timeLabel.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right;
		this.timeLabel.Location = new System.Drawing.Point(1050, 30);
		this.timeLabel.Name = "timeLabel";
		this.timeLabel.Size = new System.Drawing.Size(138, 23);
		this.timeLabel.TabIndex = 5;
		this.timeLabel.Text = "00:00 / 00:00";
		this.timeLabel.TextAlign = System.Drawing.ContentAlignment.MiddleRight;
		this.progressTrackBar.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
		this.progressTrackBar.Location = new System.Drawing.Point(120, 30);
		this.progressTrackBar.Maximum = 1000;
		this.progressTrackBar.Name = "progressTrackBar";
		this.progressTrackBar.Size = new System.Drawing.Size(924, 56);
		this.progressTrackBar.TabIndex = 3;
		this.progressTrackBar.TickFrequency = 50;
		this.progressTrackBar.LargeChange = 10;
		this.progressTrackBar.SmallChange = 1;
		this.progressTrackBar.AccessibleName = "00:00 / 00:00";
		this.progressTrackBar.Scroll += new System.EventHandler(progressTrackBar_Scroll);
		this.progressTrackBar.KeyDown += new System.Windows.Forms.KeyEventHandler(progressTrackBar_KeyDown);
		this.progressTrackBar.MouseDown += new System.Windows.Forms.MouseEventHandler(progressTrackBar_MouseDown);
		this.progressTrackBar.MouseUp += new System.Windows.Forms.MouseEventHandler(progressTrackBar_MouseUp);
		this.playPauseButton.Font = new System.Drawing.Font("Microsoft YaHei UI", 10f, System.Drawing.FontStyle.Bold);
		this.playPauseButton.Location = new System.Drawing.Point(12, 30);
		this.playPauseButton.Name = "playPauseButton";
		this.playPauseButton.Size = new System.Drawing.Size(100, 56);
		this.playPauseButton.TabIndex = 1;
		this.playPauseButton.Text = "播放";
		this.playPauseButton.UseVisualStyleBackColor = true;
		this.playPauseButton.Click += new System.EventHandler(playPauseButton_Click);
		this.currentSongLabel.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
		this.currentSongLabel.Font = new System.Drawing.Font("Microsoft YaHei UI", 11f, System.Drawing.FontStyle.Bold);
		this.currentSongLabel.Location = new System.Drawing.Point(12, 5);
		this.currentSongLabel.Name = "currentSongLabel";
		this.currentSongLabel.Size = new System.Drawing.Size(1176, 23);
		this.currentSongLabel.TabIndex = 0;
		this.currentSongLabel.Text = "未播放";
		this.currentSongLabel.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
		this.statusStrip1.ImageScalingSize = new System.Drawing.Size(20, 20);
		this.statusStrip1.Items.AddRange(new System.Windows.Forms.ToolStripItem[1] { this.toolStripStatusLabel1 });
		this.statusStrip1.Location = new System.Drawing.Point(0, 650);
		this.statusStrip1.Name = "statusStrip1";
		this.statusStrip1.Size = new System.Drawing.Size(1200, 26);
		this.statusStrip1.TabIndex = 4;
		this.statusStrip1.Text = "statusStrip1";
		this.toolStripStatusLabel1.Name = "toolStripStatusLabel1";
		this.toolStripStatusLabel1.Size = new System.Drawing.Size(39, 20);
		this.toolStripStatusLabel1.Text = "就绪";
		this.songContextMenu.ImageScalingSize = new System.Drawing.Size(20, 20);
		this.songContextMenu.Items.AddRange(new System.Windows.Forms.ToolStripItem[45]
		{
			this.viewSourceMenuItem, this.insertPlayMenuItem, this.likeSongMenuItem, this.unlikeSongMenuItem, this.addToPlaylistMenuItem, this.removeFromPlaylistMenuItem, this.cloudMenuSeparator, this.uploadToCloudMenuItem, this.deleteFromCloudMenuItem, this.toolStripSeparatorCollection, this.subscribePlaylistMenuItem,
			this.unsubscribePlaylistMenuItem, this.deletePlaylistMenuItem, this.createPlaylistMenuItem, this.subscribeAlbumMenuItem, this.unsubscribeAlbumMenuItem, this.subscribePodcastMenuItem, this.unsubscribePodcastMenuItem, this.toolStripSeparatorView, this.viewSongArtistMenuItem, this.viewSongAlbumMenuItem,
			this.viewPodcastMenuItem, this.shareSongMenuItem, this.sharePlaylistMenuItem, this.shareAlbumMenuItem, this.sharePodcastMenuItem, this.sharePodcastEpisodeMenuItem, this.artistSongsSortMenuItem, this.artistAlbumsSortMenuItem, this.podcastSortMenuItem, this.commentMenuSeparator,
			this.commentMenuItem, this.toolStripSeparatorArtist, this.shareArtistMenuItem, this.subscribeArtistMenuItem, this.unsubscribeArtistMenuItem, this.toolStripSeparatorDownload3, this.downloadSongMenuItem, this.downloadLyricsMenuItem, this.downloadPlaylistMenuItem, this.downloadAlbumMenuItem,
			this.downloadPodcastMenuItem, this.batchDownloadMenuItem, this.downloadCategoryMenuItem, this.batchDownloadPlaylistsMenuItem
		});
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
		this.viewPodcastMenuItem.Name = "viewPodcastMenuItem";
		this.viewPodcastMenuItem.Size = new System.Drawing.Size(210, 24);
		this.viewPodcastMenuItem.Text = "查看播客(&P)";
		this.viewPodcastMenuItem.Visible = false;
		this.viewPodcastMenuItem.Click += new System.EventHandler(viewPodcastMenuItem_Click);
		this.toolStripSeparatorArtist.Name = "toolStripSeparatorArtist";
		this.toolStripSeparatorArtist.Size = new System.Drawing.Size(207, 6);
		this.toolStripSeparatorArtist.Visible = false;
		this.shareSongMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[2] { this.shareSongWebMenuItem, this.shareSongDirectMenuItem });
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
		this.sharePodcastEpisodeMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[2] { this.sharePodcastEpisodeWebMenuItem, this.sharePodcastEpisodeDirectMenuItem });
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
		this.artistSongsSortMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[2] { this.artistSongsSortHotMenuItem, this.artistSongsSortTimeMenuItem });
		this.artistSongsSortMenuItem.Name = "artistSongsSortMenuItem";
		this.artistSongsSortMenuItem.Size = new System.Drawing.Size(210, 24);
		this.artistSongsSortMenuItem.Text = "单曲排序(&S)";
		this.artistSongsSortMenuItem.Visible = false;
		this.artistSongsSortHotMenuItem.Name = "artistSongsSortHotMenuItem";
		this.artistSongsSortHotMenuItem.Size = new System.Drawing.Size(250, 26);
		this.artistSongsSortHotMenuItem.Text = "按热门排序(&H)";
		this.artistSongsSortHotMenuItem.CheckOnClick = true;
		this.artistSongsSortHotMenuItem.Click += new System.EventHandler(artistSongsSortHotMenuItem_Click);
		this.artistSongsSortTimeMenuItem.Name = "artistSongsSortTimeMenuItem";
		this.artistSongsSortTimeMenuItem.Size = new System.Drawing.Size(250, 26);
		this.artistSongsSortTimeMenuItem.Text = "按发布时间排序(&T)";
		this.artistSongsSortTimeMenuItem.CheckOnClick = true;
		this.artistSongsSortTimeMenuItem.Click += new System.EventHandler(artistSongsSortTimeMenuItem_Click);
		this.artistAlbumsSortMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[2] { this.artistAlbumsSortLatestMenuItem, this.artistAlbumsSortOldestMenuItem });
		this.artistAlbumsSortMenuItem.Name = "artistAlbumsSortMenuItem";
		this.artistAlbumsSortMenuItem.Size = new System.Drawing.Size(210, 24);
		this.artistAlbumsSortMenuItem.Text = "专辑排序(&B)";
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
		this.podcastSortMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[2] { this.podcastSortLatestMenuItem, this.podcastSortSerialMenuItem });
		this.podcastSortMenuItem.Name = "podcastSortMenuItem";
		this.podcastSortMenuItem.Size = new System.Drawing.Size(210, 24);
		this.podcastSortMenuItem.Text = "排序(&T)";
		this.podcastSortMenuItem.Visible = false;
		this.podcastSortLatestMenuItem.Name = "podcastSortLatestMenuItem";
		this.podcastSortLatestMenuItem.Size = new System.Drawing.Size(210, 26);
		this.podcastSortLatestMenuItem.Text = "按最新排序(&N)";
		this.podcastSortLatestMenuItem.CheckOnClick = true;
		this.podcastSortLatestMenuItem.Click += new System.EventHandler(podcastSortLatestMenuItem_Click);
		this.podcastSortSerialMenuItem.Name = "podcastSortSerialMenuItem";
		this.podcastSortSerialMenuItem.Size = new System.Drawing.Size(210, 26);
		this.podcastSortSerialMenuItem.Text = "按节目顺序排序(&S)";
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
		this.downloadSongMenuItem.Text = "下载歌曲(&D)";
		this.downloadSongMenuItem.Click += new System.EventHandler(DownloadSong_Click);
		this.downloadLyricsMenuItem.Name = "downloadLyricsMenuItem";
		this.downloadLyricsMenuItem.Size = new System.Drawing.Size(210, 24);
		this.downloadLyricsMenuItem.Text = "下载歌词(&L)";
		this.downloadLyricsMenuItem.Click += new System.EventHandler(DownloadLyrics_Click);
		this.downloadPlaylistMenuItem.Name = "downloadPlaylistMenuItem";
		this.downloadPlaylistMenuItem.Size = new System.Drawing.Size(210, 24);
		this.downloadPlaylistMenuItem.Text = "下载歌单(&D)";
		this.downloadPlaylistMenuItem.Click += new System.EventHandler(DownloadPlaylist_Click);
		this.downloadAlbumMenuItem.Name = "downloadAlbumMenuItem";
		this.downloadAlbumMenuItem.Size = new System.Drawing.Size(210, 24);
		this.downloadAlbumMenuItem.Text = "下载专辑(&D)";
		this.downloadAlbumMenuItem.Click += new System.EventHandler(DownloadAlbum_Click);
		this.downloadPodcastMenuItem.Name = "downloadPodcastMenuItem";
		this.downloadPodcastMenuItem.Size = new System.Drawing.Size(210, 24);
		this.downloadPodcastMenuItem.Text = "下载播客全部节目(&R)...";
		this.downloadPodcastMenuItem.Click += new System.EventHandler(DownloadPodcast_Click);
		this.batchDownloadMenuItem.Name = "batchDownloadMenuItem";
		this.batchDownloadMenuItem.Size = new System.Drawing.Size(210, 24);
		this.batchDownloadMenuItem.Text = "批量下载(&B)...";
		this.batchDownloadMenuItem.Click += new System.EventHandler(BatchDownloadSongs_Click);
		this.downloadCategoryMenuItem.Name = "downloadCategoryMenuItem";
		this.downloadCategoryMenuItem.Size = new System.Drawing.Size(210, 24);
		this.downloadCategoryMenuItem.Text = "下载分类(&C)...";
		this.downloadCategoryMenuItem.Click += new System.EventHandler(DownloadCategory_Click);
		this.batchDownloadPlaylistsMenuItem.Name = "batchDownloadPlaylistsMenuItem";
		this.batchDownloadPlaylistsMenuItem.Size = new System.Drawing.Size(210, 24);
		this.batchDownloadPlaylistsMenuItem.Text = "批量下载(&B)...";
		this.batchDownloadPlaylistsMenuItem.Click += new System.EventHandler(BatchDownloadPlaylistsOrAlbums_Click);
		this.trayContextMenu.ImageScalingSize = new System.Drawing.Size(20, 20);
		this.trayContextMenu.Items.AddRange(new System.Windows.Forms.ToolStripItem[6] { this.trayShowMenuItem, this.trayPlayPauseMenuItem, this.trayPrevMenuItem, this.trayNextMenuItem, this.trayMenuSeparator, this.trayExitMenuItem });
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
		base.Controls.Add(this.resultListView);
		base.Controls.Add(this.searchPanel);
		base.Controls.Add(this.controlPanel);
		base.Controls.Add(this.statusStrip1);
		base.Controls.Add(this.menuStrip1);
		this.Font = new System.Drawing.Font("Microsoft YaHei UI", 9f);
		base.MainMenuStrip = this.menuStrip1;
		this.MinimumSize = new System.Drawing.Size(1000, 700);
		base.Name = "MainForm";
		base.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
		this.Text = "易听";
		this.menuStrip1.ResumeLayout(false);
		this.menuStrip1.PerformLayout();
		this.searchPanel.ResumeLayout(false);
		this.searchPanel.PerformLayout();
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

	private MenuContextSnapshot BuildMenuContextSnapshot(bool isCurrentPlayingRequest)
	{
		string text = _currentViewSource ?? string.Empty;
		bool flag = !string.IsNullOrWhiteSpace(text);
		MenuContextSnapshot menuContextSnapshot = new MenuContextSnapshot
		{
			InvocationSource = (isCurrentPlayingRequest ? MenuInvocationSource.CurrentPlayback : MenuInvocationSource.ViewSelection),
			ViewSource = text,
			IsLoggedIn = IsUserLoggedIn(),
			IsCloudView = string.Equals(text, "user_cloud", StringComparison.OrdinalIgnoreCase),
			IsMyPlaylistsView = string.Equals(text, "user_playlists", StringComparison.OrdinalIgnoreCase),
			IsUserAlbumsView = string.Equals(text, "user_albums", StringComparison.OrdinalIgnoreCase),
			IsPodcastEpisodeView = IsPodcastEpisodeView(),
			IsArtistSongsView = (flag && text.StartsWith("artist_songs:", StringComparison.OrdinalIgnoreCase)),
			IsArtistAlbumsView = (flag && text.StartsWith("artist_albums:", StringComparison.OrdinalIgnoreCase)),
			PrimaryEntity = MenuEntityKind.None,
			IsValid = true
		};
		if (isCurrentPlayingRequest)
		{
			SongInfo songInfo = _audioEngine?.CurrentSong;
			if (songInfo == null)
			{
				menuContextSnapshot.IsValid = false;
				return menuContextSnapshot;
			}
			menuContextSnapshot.Song = songInfo;
			if (songInfo.IsPodcastEpisode)
			{
				menuContextSnapshot.PrimaryEntity = MenuEntityKind.PodcastEpisode;
				menuContextSnapshot.PodcastEpisode = ResolvePodcastEpisodeFromSong(songInfo);
			}
			else
			{
				menuContextSnapshot.PrimaryEntity = MenuEntityKind.Song;
			}
			return menuContextSnapshot;
		}
		if (resultListView.SelectedItems.Count == 0)
		{
			menuContextSnapshot.IsValid = false;
			return menuContextSnapshot;
		}
		ListViewItem listViewItem = (menuContextSnapshot.SelectedListItem = resultListView.SelectedItems[0]);
		if (menuContextSnapshot.IsPodcastEpisodeView && listViewItem.Tag is int num && num < 0)
		{
			menuContextSnapshot.IsValid = false;
			return menuContextSnapshot;
		}
		object tag = listViewItem.Tag;
		object obj = tag;
		if (!(obj is PlaylistInfo playlist))
		{
			if (!(obj is AlbumInfo album))
			{
				if (!(obj is ArtistInfo artist))
				{
					if (!(obj is PodcastRadioInfo podcast))
					{
						if (!(obj is PodcastEpisodeInfo podcastEpisodeInfo))
						{
							if (!(obj is SongInfo song))
							{
								if (!(obj is ListItemInfo listItem))
								{
									if (obj is int num2)
									{
										if (num2 >= 0 && num2 < _currentSongs.Count)
										{
											menuContextSnapshot.PrimaryEntity = MenuEntityKind.Song;
											menuContextSnapshot.Song = _currentSongs[num2];
											return menuContextSnapshot;
										}
										int num3 = num2;
										if (num3 < 0)
										{
											menuContextSnapshot.IsValid = false;
											return menuContextSnapshot;
										}
									}
									menuContextSnapshot.IsValid = false;
									return menuContextSnapshot;
								}
								menuContextSnapshot.ListItem = listItem;
								return ResolveListItemSnapshot(menuContextSnapshot, listItem);
							}
							menuContextSnapshot.PrimaryEntity = MenuEntityKind.Song;
							menuContextSnapshot.Song = song;
							return menuContextSnapshot;
						}
						menuContextSnapshot.PrimaryEntity = MenuEntityKind.PodcastEpisode;
						menuContextSnapshot.PodcastEpisode = podcastEpisodeInfo;
						menuContextSnapshot.Song = podcastEpisodeInfo.Song;
						return menuContextSnapshot;
					}
					menuContextSnapshot.PrimaryEntity = MenuEntityKind.Podcast;
					menuContextSnapshot.Podcast = podcast;
					return menuContextSnapshot;
				}
				menuContextSnapshot.PrimaryEntity = MenuEntityKind.Artist;
				menuContextSnapshot.Artist = artist;
				return menuContextSnapshot;
			}
			menuContextSnapshot.PrimaryEntity = MenuEntityKind.Album;
			menuContextSnapshot.Album = album;
			return menuContextSnapshot;
		}
		menuContextSnapshot.PrimaryEntity = MenuEntityKind.Playlist;
		menuContextSnapshot.Playlist = playlist;
		return menuContextSnapshot;
	}

	private MenuContextSnapshot ResolveListItemSnapshot(MenuContextSnapshot snapshot, ListItemInfo listItem)
	{
		switch (listItem.Type)
		{
		case ListItemType.Playlist:
			if (listItem.Playlist == null)
			{
				goto default;
			}
			snapshot.PrimaryEntity = MenuEntityKind.Playlist;
			snapshot.Playlist = listItem.Playlist;
			break;
		case ListItemType.Album:
			if (listItem.Album == null)
			{
				goto default;
			}
			snapshot.PrimaryEntity = MenuEntityKind.Album;
			snapshot.Album = listItem.Album;
			break;
		case ListItemType.Artist:
			if (listItem.Artist == null)
			{
				goto default;
			}
			snapshot.PrimaryEntity = MenuEntityKind.Artist;
			snapshot.Artist = listItem.Artist;
			break;
		case ListItemType.Podcast:
			if (listItem.Podcast == null)
			{
				goto default;
			}
			snapshot.PrimaryEntity = MenuEntityKind.Podcast;
			snapshot.Podcast = listItem.Podcast;
			break;
		case ListItemType.PodcastEpisode:
			if (listItem.PodcastEpisode == null)
			{
				goto default;
			}
			snapshot.PrimaryEntity = MenuEntityKind.PodcastEpisode;
			snapshot.PodcastEpisode = listItem.PodcastEpisode;
			snapshot.Song = listItem.PodcastEpisode.Song;
			break;
		case ListItemType.Song:
			if (listItem.Song == null)
			{
				goto default;
			}
			snapshot.PrimaryEntity = MenuEntityKind.Song;
			snapshot.Song = listItem.Song;
			break;
		case ListItemType.Category:
			snapshot.PrimaryEntity = MenuEntityKind.Category;
			break;
		default:
			snapshot.PrimaryEntity = MenuEntityKind.None;
			snapshot.IsValid = false;
			break;
		}
		return snapshot;
	}

	private void ResetSongContextMenuState()
	{
		subscribePlaylistMenuItem.Visible = false;
		unsubscribePlaylistMenuItem.Visible = false;
		deletePlaylistMenuItem.Visible = false;
		createPlaylistMenuItem.Visible = false;
		subscribeAlbumMenuItem.Visible = false;
		unsubscribeAlbumMenuItem.Visible = false;
		subscribePodcastMenuItem.Visible = false;
		subscribePodcastMenuItem.Enabled = true;
		subscribePodcastMenuItem.Tag = null;
		unsubscribePodcastMenuItem.Visible = false;
		unsubscribePodcastMenuItem.Enabled = true;
		unsubscribePodcastMenuItem.Tag = null;
		likeSongMenuItem.Visible = false;
		likeSongMenuItem.Tag = null;
		unlikeSongMenuItem.Visible = false;
		unlikeSongMenuItem.Tag = null;
		addToPlaylistMenuItem.Visible = false;
		addToPlaylistMenuItem.Tag = null;
		removeFromPlaylistMenuItem.Visible = false;
		removeFromPlaylistMenuItem.Tag = null;
		insertPlayMenuItem.Visible = true;
		insertPlayMenuItem.Tag = null;
		if (refreshMenuItem != null)
		{
			refreshMenuItem.Visible = true;
			refreshMenuItem.Enabled = true;
		}
		downloadSongMenuItem.Visible = false;
		downloadSongMenuItem.Tag = null;
		downloadSongMenuItem.Text = "下载歌曲(&D)";
		downloadPlaylistMenuItem.Visible = false;
		downloadAlbumMenuItem.Visible = false;
		batchDownloadMenuItem.Visible = false;
		downloadCategoryMenuItem.Visible = false;
		batchDownloadPlaylistsMenuItem.Visible = false;
		downloadPodcastMenuItem.Visible = false;
		downloadPodcastMenuItem.Tag = null;
		downloadLyricsMenuItem.Visible = false;
		downloadLyricsMenuItem.Tag = null;
		cloudMenuSeparator.Visible = false;
		uploadToCloudMenuItem.Visible = false;
		deleteFromCloudMenuItem.Visible = false;
		toolStripSeparatorArtist.Visible = false;
		shareArtistMenuItem.Visible = false;
		subscribeArtistMenuItem.Visible = false;
		unsubscribeArtistMenuItem.Visible = false;
		toolStripSeparatorView.Visible = false;
		commentMenuItem.Visible = false;
		commentMenuItem.Tag = null;
		commentMenuSeparator.Visible = false;
		viewSongArtistMenuItem.Visible = false;
		viewSongArtistMenuItem.Tag = null;
		viewSongAlbumMenuItem.Visible = false;
		viewSongAlbumMenuItem.Tag = null;
		if (viewPodcastMenuItem != null)
		{
			viewPodcastMenuItem.Visible = false;
			viewPodcastMenuItem.Tag = null;
		}
		shareSongMenuItem.Visible = false;
		shareSongMenuItem.Tag = null;
		shareSongWebMenuItem.Tag = null;
		shareSongDirectMenuItem.Tag = null;
		sharePlaylistMenuItem.Visible = false;
		sharePlaylistMenuItem.Tag = null;
		shareAlbumMenuItem.Visible = false;
		shareAlbumMenuItem.Tag = null;
		sharePodcastMenuItem.Visible = false;
		sharePodcastMenuItem.Tag = null;
		sharePodcastEpisodeMenuItem.Visible = false;
		sharePodcastEpisodeMenuItem.Tag = null;
		sharePodcastEpisodeWebMenuItem.Tag = null;
		sharePodcastEpisodeDirectMenuItem.Tag = null;
		podcastSortMenuItem.Visible = false;
		if (artistSongsSortMenuItem != null)
		{
			artistSongsSortMenuItem.Visible = false;
		}
		if (artistAlbumsSortMenuItem != null)
		{
			artistAlbumsSortMenuItem.Visible = false;
		}
	}

	private void ConfigureSortMenus(MenuContextSnapshot snapshot, ref bool showViewSection)
	{
		bool flag = !snapshot.IsCurrentPlayback && snapshot.IsPodcastEpisodeView && !string.IsNullOrWhiteSpace(snapshot.ViewSource) && snapshot.ViewSource.StartsWith("podcast:", StringComparison.OrdinalIgnoreCase);
		podcastSortMenuItem.Visible = flag;
		if (flag)
		{
			UpdatePodcastSortMenuChecks();
		}
		if (artistSongsSortMenuItem != null)
		{
			bool flag2 = !snapshot.IsCurrentPlayback && snapshot.IsArtistSongsView;
			artistSongsSortMenuItem.Visible = flag2;
			if (flag2)
			{
				UpdateArtistSongsSortMenuChecks();
			}
		}
		if (artistAlbumsSortMenuItem != null)
		{
			bool flag3 = !snapshot.IsCurrentPlayback && snapshot.IsArtistAlbumsView;
			artistAlbumsSortMenuItem.Visible = flag3;
			if (flag3)
			{
				UpdateArtistAlbumsSortMenuChecks();
			}
		}
		if (!podcastSortMenuItem.Visible)
		{
			ToolStripMenuItem toolStripMenuItem = artistSongsSortMenuItem;
			if (toolStripMenuItem == null || !toolStripMenuItem.Visible)
			{
				ToolStripMenuItem toolStripMenuItem2 = artistAlbumsSortMenuItem;
				if (toolStripMenuItem2 == null || !toolStripMenuItem2.Visible)
				{
					return;
				}
			}
		}
		showViewSection = true;
	}

	private void ConfigureCategoryMenu()
	{
		insertPlayMenuItem.Visible = false;
		downloadCategoryMenuItem.Visible = true;
	}

	private void ApplyViewContextFlags(MenuContextSnapshot snapshot, ref bool showViewSection)
	{
		if (snapshot.IsCloudView)
		{
			uploadToCloudMenuItem.Visible = true;
			cloudMenuSeparator.Visible = true;
		}
		if (!snapshot.IsCurrentPlayback && snapshot.IsMyPlaylistsView && snapshot.IsLoggedIn)
		{
			createPlaylistMenuItem.Visible = true;
		}
		ConfigureSortMenus(snapshot, ref showViewSection);
	}

	private void ConfigurePlaylistMenu(MenuContextSnapshot snapshot, bool isLoggedIn, ref bool showViewSection, ref CommentTarget? contextCommentTarget)
	{
		PlaylistInfo playlist = snapshot.Playlist;
		if (playlist != null)
		{
			bool flag = IsPlaylistCreatedByCurrentUser(playlist);
			bool flag2 = !flag && IsPlaylistSubscribed(playlist);
			if (isLoggedIn)
			{
				subscribePlaylistMenuItem.Visible = !flag && !flag2;
				unsubscribePlaylistMenuItem.Visible = !flag && flag2;
				deletePlaylistMenuItem.Visible = flag;
			}
			else
			{
				subscribePlaylistMenuItem.Visible = false;
				unsubscribePlaylistMenuItem.Visible = false;
				deletePlaylistMenuItem.Visible = false;
			}
			insertPlayMenuItem.Visible = false;
			downloadPlaylistMenuItem.Visible = true;
			batchDownloadPlaylistsMenuItem.Visible = true;
			sharePlaylistMenuItem.Visible = true;
			sharePlaylistMenuItem.Tag = playlist;
			showViewSection = true;
			if (!string.IsNullOrWhiteSpace(playlist.Id))
			{
				contextCommentTarget = new CommentTarget(playlist.Id, CommentType.Playlist, string.IsNullOrWhiteSpace(playlist.Name) ? "歌单" : playlist.Name, playlist.Creator);
			}
		}
	}

	private void ConfigureAlbumMenu(MenuContextSnapshot snapshot, bool isLoggedIn, ref bool showViewSection, ref CommentTarget? contextCommentTarget)
	{
		AlbumInfo album = snapshot.Album;
		if (album != null)
		{
			if (isLoggedIn)
			{
				bool flag = IsAlbumSubscribed(album);
				subscribeAlbumMenuItem.Visible = !flag;
				unsubscribeAlbumMenuItem.Visible = flag;
			}
			else
			{
				subscribeAlbumMenuItem.Visible = false;
				unsubscribeAlbumMenuItem.Visible = false;
			}
			insertPlayMenuItem.Visible = false;
			downloadAlbumMenuItem.Visible = true;
			batchDownloadPlaylistsMenuItem.Visible = true;
			shareAlbumMenuItem.Visible = true;
			shareAlbumMenuItem.Tag = album;
			showViewSection = true;
			if (!string.IsNullOrWhiteSpace(album.Id))
			{
				contextCommentTarget = new CommentTarget(album.Id, CommentType.Album, string.IsNullOrWhiteSpace(album.Name) ? "专辑" : album.Name, album.Artist);
			}
		}
	}

	private void ConfigurePodcastMenu(MenuContextSnapshot snapshot, bool isLoggedIn, ref bool showViewSection)
	{
		PodcastRadioInfo podcast = snapshot.Podcast;
		if (podcast != null)
		{
			insertPlayMenuItem.Visible = false;
			ConfigurePodcastMenuItems(podcast, isLoggedIn);
			if (sharePodcastMenuItem.Visible)
			{
				showViewSection = true;
			}
		}
	}

	private void ConfigureSongOrEpisodeMenu(MenuContextSnapshot snapshot, bool isLoggedIn, bool isCloudView, ref bool showViewSection, ref CommentTarget? contextCommentTarget, ref PodcastRadioInfo? contextPodcastForEpisode, ref PodcastEpisodeInfo? effectiveEpisode, ref bool isPodcastEpisodeContext)
	{
		insertPlayMenuItem.Visible = true;
		SongInfo songInfo = snapshot.Song;
		if (snapshot.IsCurrentPlayback && _currentPlayingMenuSong != null)
		{
			songInfo = _currentPlayingMenuSong;
		}
		PodcastEpisodeInfo podcastEpisode = snapshot.PodcastEpisode;
		if (songInfo == null && podcastEpisode?.Song != null)
		{
			songInfo = podcastEpisode.Song;
		}
		if (songInfo == null && podcastEpisode != null)
		{
			songInfo = EnsurePodcastEpisodeSong(podcastEpisode);
		}
		if (podcastEpisode != null)
		{
			effectiveEpisode = podcastEpisode;
			isPodcastEpisodeContext = true;
		}
		else if (songInfo != null && songInfo.IsPodcastEpisode)
		{
			isPodcastEpisodeContext = true;
			effectiveEpisode = ResolvePodcastEpisodeFromSong(songInfo);
		}
		if (effectiveEpisode != null)
		{
			contextPodcastForEpisode = ResolvePodcastFromEpisode(effectiveEpisode);
			songInfo = EnsurePodcastEpisodeSong(effectiveEpisode);
		}
		insertPlayMenuItem.Tag = songInfo;
		if (songInfo != null && !string.IsNullOrWhiteSpace(songInfo.Id) && !songInfo.IsCloudSong && !isPodcastEpisodeContext)
		{
			contextCommentTarget = new CommentTarget(songInfo.Id, CommentType.Song, string.IsNullOrWhiteSpace(songInfo.Name) ? "歌曲" : songInfo.Name, songInfo.Artist);
		}
		bool flag = !isPodcastEpisodeContext && CanSongUseLibraryFeatures(songInfo);
		if (isCloudView && songInfo != null && songInfo.IsCloudSong)
		{
			deleteFromCloudMenuItem.Visible = true;
			cloudMenuSeparator.Visible = true;
		}
		else
		{
			deleteFromCloudMenuItem.Visible = false;
		}
		if (isLoggedIn)
		{
			bool flag2 = IsCurrentLikedSongsView();
			bool flag3 = flag2;
			if (flag && songInfo != null && !flag3)
			{
				flag3 = IsSongLiked(songInfo);
			}
			likeSongMenuItem.Visible = flag && !flag3;
			unlikeSongMenuItem.Visible = flag && flag3;
			likeSongMenuItem.Tag = (flag ? songInfo : null);
			unlikeSongMenuItem.Tag = (flag ? songInfo : null);
			addToPlaylistMenuItem.Visible = flag;
			addToPlaylistMenuItem.Tag = (flag ? songInfo : null);
			string playlistIdFromView = snapshot.ViewSource.StartsWith("playlist:", StringComparison.OrdinalIgnoreCase) ? snapshot.ViewSource.Substring("playlist:".Length) : null;
			bool isPlaylistContext = snapshot.ViewSource.StartsWith("playlist:", StringComparison.OrdinalIgnoreCase) || (_currentPlaylist != null && !string.IsNullOrWhiteSpace(_currentPlaylist.Id));
			bool isUserOwnedPlaylist = _currentPlaylistOwnedByUser || IsCurrentPlaylistOwnedByUser(playlistIdFromView) || (snapshot.Playlist != null && IsPlaylistCreatedByCurrentUser(snapshot.Playlist));

			bool flag4 = isPlaylistContext && isUserOwnedPlaylist;
			if (snapshot.IsCurrentPlayback)
			{
				removeFromPlaylistMenuItem.Visible = false;
				removeFromPlaylistMenuItem.Tag = null;
				removeFromPlaylistMenuItem.Text = "从歌单中移除(&R)";
			}
			else if (flag2)
			{
				removeFromPlaylistMenuItem.Text = "取消收藏(&R)";
				removeFromPlaylistMenuItem.Visible = flag;
				removeFromPlaylistMenuItem.Tag = (flag ? songInfo : null);
			}
			else
			{
				removeFromPlaylistMenuItem.Text = "从歌单中移除(&R)";
				removeFromPlaylistMenuItem.Visible = flag && flag4;
				removeFromPlaylistMenuItem.Tag = (removeFromPlaylistMenuItem.Visible ? songInfo : null);
			}
		}
		else
		{
			likeSongMenuItem.Visible = false;
			unlikeSongMenuItem.Visible = false;
			addToPlaylistMenuItem.Visible = false;
			removeFromPlaylistMenuItem.Visible = false;
			removeFromPlaylistMenuItem.Text = "从歌单中移除(&R)";
			likeSongMenuItem.Tag = null;
			unlikeSongMenuItem.Tag = null;
			addToPlaylistMenuItem.Tag = null;
			removeFromPlaylistMenuItem.Tag = null;
		}
		bool flag5 = isCloudView && songInfo != null && songInfo.IsCloudSong;
		downloadSongMenuItem.Visible = !flag5;
		downloadSongMenuItem.Tag = songInfo;
		downloadSongMenuItem.Text = (isPodcastEpisodeContext ? "下载声音(&D)" : "下载歌曲(&D)");
		bool flag6 = !flag5 && !isPodcastEpisodeContext;
		downloadLyricsMenuItem.Visible = flag6;
		downloadLyricsMenuItem.Tag = (flag6 ? songInfo : null);
		batchDownloadMenuItem.Visible = !flag5 && !snapshot.IsCurrentPlayback;
		bool flag7 = songInfo != null && (!songInfo.IsCloudSong || !string.IsNullOrWhiteSpace(songInfo?.Artist));
		bool flag8 = songInfo != null && (!songInfo.IsCloudSong || !string.IsNullOrWhiteSpace(songInfo?.Album));
		bool flag9 = songInfo != null && flag;
		if (isPodcastEpisodeContext)
		{
			flag7 = false;
			flag8 = false;
			flag9 = false;
		}
		viewSongArtistMenuItem.Visible = flag7;
		viewSongArtistMenuItem.Tag = (flag7 ? songInfo : null);
		viewSongAlbumMenuItem.Visible = flag8;
		viewSongAlbumMenuItem.Tag = (flag8 ? songInfo : null);
		shareSongMenuItem.Visible = flag9;
		if (flag9)
		{
			shareSongMenuItem.Tag = songInfo;
			shareSongWebMenuItem.Tag = songInfo;
			shareSongDirectMenuItem.Tag = songInfo;
		}
		else
		{
			shareSongMenuItem.Tag = null;
			shareSongWebMenuItem.Tag = null;
			shareSongDirectMenuItem.Tag = null;
		}
		if (contextPodcastForEpisode == null && effectiveEpisode == null && songInfo != null && songInfo.IsPodcastEpisode)
		{
			contextPodcastForEpisode = ResolvePodcastFromSong(songInfo);
		}
		if (isPodcastEpisodeContext)
		{
			ConfigurePodcastEpisodeShareMenu(effectiveEpisode ?? ResolvePodcastEpisodeFromSong(songInfo));
		}
		else
		{
			ConfigurePodcastEpisodeShareMenu(null);
		}
		bool flag10 = false;
		if (viewPodcastMenuItem != null)
		{
			bool flag11 = contextPodcastForEpisode != null && contextPodcastForEpisode.Id > 0;
			viewPodcastMenuItem.Visible = flag11;
			viewPodcastMenuItem.Tag = (flag11 ? contextPodcastForEpisode : null);
			flag10 = flag11;
		}
		bool visible = sharePodcastMenuItem.Visible;
		bool visible2 = sharePodcastEpisodeMenuItem.Visible;
		showViewSection = showViewSection || flag7 || flag8 || flag9 || flag10 || visible || visible2;
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
		string text = ex.Message ?? string.Empty;
		if (text.IndexOf("不存在", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("下架", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("被移除", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("版权", StringComparison.OrdinalIgnoreCase) >= 0)
	{
		return true;
	}
	return false;
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

}
}
