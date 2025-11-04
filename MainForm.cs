using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using YTPlayer.Core;
using YTPlayer.Core.Playback;
using YTPlayer.Core.Playback.Cache;
using YTPlayer.Core.Lyrics;
using YTPlayer.Models;
using YTPlayer.Models.Auth;

#pragma warning disable CS8600, CS8601, CS8602, CS8603, CS8604, CS8625

namespace YTPlayer
{
    public partial class MainForm : Form
    {
        #region 字段声明

        protected NeteaseApiClient _apiClient = null!;  // Changed to protected for partial class access
        private BassAudioEngine _audioEngine = null!;
        private SeekManager _seekManager = null!;  // ⭐ 新增：Seek 管理器
        protected ConfigManager _configManager = null!;  // Changed to protected for partial class access
        private ConfigModel _config = null!;
        private AccountState _accountState = null!;
        protected List<SongInfo> _currentSongs = new List<SongInfo>();  // Changed to protected for partial class access
        private List<PlaylistInfo> _currentPlaylists = new List<PlaylistInfo>();
        private PlaylistInfo? _currentPlaylist = null;  // 当前打开的歌单
        private PlaylistInfo? _userLikedPlaylist = null;  // 缓存的"喜欢的音乐"歌单对象
        private List<AlbumInfo> _currentAlbums = new List<AlbumInfo>();
        private List<ListItemInfo> _currentListItems = new List<ListItemInfo>(); // 统一的列表项
        private List<LyricLine> _currentLyrics = new List<LyricLine>();  // 保留用于向后兼容

        // ⭐ 新的歌词系统
        private LyricsCacheManager _lyricsCacheManager = null!;
        private LyricsDisplayManager _lyricsDisplayManager = null!;
        private LyricsLoader _lyricsLoader = null!;
        private bool _autoReadLyrics = false;  // 自动朗读歌词开关

        private System.Windows.Forms.Timer? _updateTimer;
        private System.Windows.Forms.NotifyIcon? _trayIcon;
        private Utils.ContextMenuHost? _contextMenuHost;  // ⭐ 自定义菜单宿主窗口
        private bool _isExitingFromTrayMenu = false;  // ⭐ 标志：是否正在从托盘菜单退出
        private DateTime _appStartTime = DateTime.Now;  // ⭐ 应用启动时间（用于冷启动风控检测）
        private bool _isUserDragging = false;
        private int _currentPage = 1;
        private int _resultsPerPage = 100;
        private int _maxPage = 1;
        private bool _hasNextSearchPage = false;
        private int _lastListViewFocusedIndex = -1;  // 记录列表最后聚焦的索引
        private string _lastKeyword = "";
        private readonly PlaybackQueueManager _playbackQueue = new PlaybackQueueManager();
        private bool _suppressAutoAdvance = false;
        // 当前浏览列表的来源标识
        private string _currentViewSource = "";
        private long _loggedInUserId = 0;

        // 标识当前是否在主页状态
        private bool _isHomePage = false;

        // 导航历史栈（用于后退功能）
        private Stack<NavigationHistoryItem> _navigationHistory = new Stack<NavigationHistoryItem>();
        private DateTime _lastBackTime = DateTime.MinValue;           // 上次后退时间
        private const int MIN_BACK_INTERVAL_MS = 300;                 // 最小后退间隔（毫秒）
        private bool _isNavigating = false;                            // 是否正在执行导航操作

        private CancellationTokenSource? _availabilityCheckCts;        // 列表可用性检查取消令牌

        // 播放请求取消和防抖控制
        private const int CloudPageSize = 50;
        private int _cloudPage = 1;
        private bool _cloudHasMore = false;
        private int _cloudTotalCount = 0;
        private long _cloudUsedSize = 0;
        private long _cloudMaxSize = 0;
        private bool _cloudLoading = false;
        private string? _pendingCloudFocusId = null;
        private string? _lastSelectedCloudSongId = null;
        private Guid? _lastNotifiedUploadFailureTaskId = null;

        private System.Threading.CancellationTokenSource? _playbackCancellation = null; // 当前播放请求的取消令牌
        private DateTime _lastPlayRequestTime = DateTime.MinValue;                     // 上次播放请求时间
        private const int MIN_PLAY_REQUEST_INTERVAL_MS = 200;                         // 最小播放请求间隔（毫秒）

        // ⭐ 旧的 Seek 控制已移除，现在由 SeekManager 统一管理

        private DateTime _lastSyncButtonTextTime = DateTime.MinValue;
        private const int MIN_SYNC_BUTTON_INTERVAL_MS = 50;

        // 异步状态缓存（避免UI线程阻塞）
        private double _cachedPosition = 0;                                            // 缓存的播放位置
        private double _cachedDuration = 0;                                            // 缓存的歌曲时长
        private PlaybackState _cachedPlaybackState = PlaybackState.Stopped;           // 缓存的播放状态
        private readonly object _stateCacheLock = new object();                        // 状态缓存锁
        private System.Threading.CancellationTokenSource? _stateUpdateCancellation = null; // 状态更新取消令牌
        private bool _stateUpdateLoopRunning = false;                                  // 状态更新循环是否运行中

        private bool _isPlaybackLoading = false;
        private string? _playButtonTextBeforeLoading = null;
        private string? _statusTextBeforeLoading = null;

        // 下一首歌曲预加载器（新）
        private NextSongPreloader? _nextSongPreloader = null;

        // 键盘 Scrub 控制
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
        private const int SONG_URL_CACHE_MINUTES = 30; // URL缓存时间延长到30分钟

        #endregion

        #region 异步状态缓存系统

        /// <summary>
        /// 启动异步状态更新循环（在后台线程持续更新播放状态，避免UI线程阻塞）
        /// </summary>
        private void StartStateUpdateLoop()
        {
            if (_stateUpdateLoopRunning)
            {
                System.Diagnostics.Debug.WriteLine("[StateCache] 状态更新循环已在运行中");
                return;
            }

            _stateUpdateCancellation?.Cancel();
            _stateUpdateCancellation?.Dispose();
            _stateUpdateCancellation = new System.Threading.CancellationTokenSource();

            var cancellationToken = _stateUpdateCancellation.Token;
            _stateUpdateLoopRunning = true;

            _ = Task.Run(async () =>
            {
                System.Diagnostics.Debug.WriteLine("[StateCache] ✓ 异步状态更新循环已启动");

                try
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        if (_audioEngine != null)
                        {
                            // 在后台线程调用BASS API（可能阻塞，但不影响UI）
                            double position = 0;
                            double duration = 0;
                            PlaybackState state = PlaybackState.Stopped;

                            try
                            {
                                position = _audioEngine.GetPosition();
                                duration = _audioEngine.GetDuration();
                                state = _audioEngine.GetPlaybackState();
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[StateCache] 获取状态异常: {ex.Message}");
                            }

                            // 更新缓存（加锁保证线程安全）
                            lock (_stateCacheLock)
                            {
                                _cachedPosition = position;
                                _cachedDuration = duration;
                                _cachedPlaybackState = state;
                            }
                        }

                        // 每50ms更新一次（比Timer更快，确保UI有新数据）
                        await Task.Delay(50);
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        System.Diagnostics.Debug.WriteLine("[StateCache] 状态更新循环收到取消请求");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[StateCache ERROR] 状态更新循环异常: {ex.Message}");
                }
                finally
                {
                    _stateUpdateLoopRunning = false;
                    System.Diagnostics.Debug.WriteLine("[StateCache] 状态更新循环已停止");
                }
            });
        }

        /// <summary>
        /// 停止异步状态更新循环
        /// </summary>
        private void StopStateUpdateLoop()
        {
            if (_stateUpdateCancellation != null)
            {
                _stateUpdateCancellation.Cancel();
                _stateUpdateCancellation.Dispose();
                _stateUpdateCancellation = null;
            }

            // 重置缓存
            lock (_stateCacheLock)
            {
                _cachedPosition = 0;
                _cachedDuration = 0;
                _cachedPlaybackState = PlaybackState.Stopped;
            }
        }

        /// <summary>
        /// 获取缓存的播放位置（线程安全，不阻塞UI）
        /// </summary>
        private double GetCachedPosition()
        {
            lock (_stateCacheLock)
            {
                return _cachedPosition;
            }
        }

        /// <summary>
        /// 获取缓存的歌曲时长（线程安全，不阻塞UI）
        /// </summary>
        private double GetCachedDuration()
        {
            lock (_stateCacheLock)
            {
                return _cachedDuration;
            }
        }

        /// <summary>
        /// 获取缓存的播放状态（线程安全，不阻塞UI）
        /// </summary>
        private PlaybackState GetCachedPlaybackState()
        {
            lock (_stateCacheLock)
            {
                return _cachedPlaybackState;
            }
        }

        #endregion

        #region 构造函数

        public MainForm()
        {
            InitializeComponent();
            InitializeServices();
            SetupEventHandlers();
            LoadConfig();
            // ⭐ 托盘图标初始化（使用自定义宿主窗口方案）
            _trayIcon = new System.Windows.Forms.NotifyIcon();
            _trayIcon.Icon = this.Icon;                      // 复用窗体图标
            _trayIcon.Text = "易听";
            _trayIcon.Visible = true;  // ⭐ 启动时就显示，且保持常驻
            _trayIcon.MouseClick += TrayIcon_MouseClick;       // 鼠标单击（左键/右键/中键）手动处理
            _trayIcon.DoubleClick += TrayIcon_DoubleClick;     // 兼容保留双击

            // ⭐ 创建自定义菜单宿主窗口（防止虚拟窗口被 Alt+F4 关闭）
            _contextMenuHost = new Utils.ContextMenuHost();

            // ⭐ 绑定托盘菜单的事件，确保焦点正确管理
            trayContextMenu.Opening += TrayContextMenu_Opening;
            trayContextMenu.Opened += TrayContextMenu_Opened;
            trayContextMenu.Closed += TrayContextMenu_Closed;


            SyncPlayPauseButtonText();

            // Phase 2: 窗体加载事件
            this.Load += MainForm_Load;
        }

        /// <summary>
        /// 窗体加载事件
        /// </summary>
        private async void MainForm_Load(object sender, EventArgs e)
        {
            // ⭐ 方案1：会话热身，避免冷启动风控（在后台静默执行）
            // 不阻塞UI，允许用户立即操作，但实际API请求会等待热身完成
            _ = Task.Run(async () =>
            {
                try
                {
                    await _apiClient.WarmupSessionAsync();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MainForm] 热身失败（忽略）: {ex.Message}");
                }
            });

            // 加载主页内容（用户歌单和官方歌单）
            await LoadHomePageAsync();
        }

        #endregion

        #region 初始化

        /// <summary>
        /// 初始化服务
        /// </summary>
        private void InitializeServices()
        {
            try
            {
                // 初始化配置管理器
                _configManager = ConfigManager.Instance;
                _config = _configManager.Load();

                // 初始化 API 客户端
                _apiClient = new NeteaseApiClient(_config);
                _apiClient.UseSimplifiedApi = false; // 禁用简化API
                ApplyAccountStateOnStartup();

                // 初始化音频引擎
                _audioEngine = new BassAudioEngine();

                // ⭐⭐⭐ 订阅缓冲状态变化事件
                _audioEngine.BufferingStateChanged += OnBufferingStateChanged;

                // ⭐ 初始化歌词系统
                _lyricsCacheManager = new LyricsCacheManager();
                _lyricsDisplayManager = new LyricsDisplayManager(_lyricsCacheManager);
                _lyricsLoader = new LyricsLoader(_apiClient);

                // 订阅歌词更新事件
                _lyricsDisplayManager.LyricUpdated += OnLyricUpdated;

                // 订阅播放进度事件（用于歌词同步）
                _audioEngine.PositionChanged += OnAudioPositionChanged;

                // 初始化下一首歌曲预加载器（新）
                _nextSongPreloader = new NextSongPreloader(_apiClient);

                // ⭐ 初始化 Seek 管理器（丢弃式非阻塞模式）
                _seekManager = new SeekManager(_audioEngine);
                _seekManager.SeekCompleted += OnSeekCompleted;

                // 初始化更新定时器
                _updateTimer = new System.Windows.Forms.Timer();
                _updateTimer.Interval = 100;
                _updateTimer.Tick += UpdateTimer_Tick;
                _updateTimer.Start();

                _scrubKeyTimer = new System.Windows.Forms.Timer();
                _scrubKeyTimer.Interval = KEY_SCRUB_INTERVAL_MS;
                _scrubKeyTimer.Tick += ScrubKeyTimer_Tick;

                // 启动异步状态更新循环（避免UI线程阻塞）
                StartStateUpdateLoop();

                // ✅ 初始化命令队列系统（新架构）
                InitializeCommandQueueSystem();

                // 设置搜索类型下拉框默认值
                if (searchTypeComboBox.Items.Count > 0)
                {
                    searchTypeComboBox.SelectedIndex = 0; // 默认选择"歌曲"
                }

                // 初始化下载功能
                InitializeDownload();

                UpdateStatusBar("就绪");
            }
            catch (Exception ex)
            {
                // ⭐ 关键修复：即使初始化失败，也要确保核心组件可用
                System.Diagnostics.Debug.WriteLine($"[MainForm] 初始化异常: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[MainForm] 异常堆栈: {ex.StackTrace}");

                // 尝试创建最小可用配置
                try
                {
                    if (_configManager == null)
                    {
                        _configManager = ConfigManager.Instance;
                    }

                    if (_config == null)
                    {
                        _config = _configManager.CreateDefaultConfig();
                        System.Diagnostics.Debug.WriteLine("[MainForm] 使用默认配置");
                    }

                    // ⭐ 确保 API 客户端一定被初始化（即使是基本配置）
                    if (_apiClient == null)
                    {
                        _apiClient = new NeteaseApiClient(_config);
                        _apiClient.UseSimplifiedApi = false;
                        System.Diagnostics.Debug.WriteLine("[MainForm] 已使用默认配置初始化 API 客户端");
                    }
                }
                catch (Exception fallbackEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[MainForm] 后备初始化失败: {fallbackEx.Message}");
                }

                MessageBox.Show($"初始化失败: {ex.Message}\n\n音频功能可能不可用，但登录功能仍可使用。", "警告",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                UpdateStatusBar("初始化失败（部分功能可用）");
            }
        }

        /// <summary>
        /// 设置事件处理器
        /// </summary>
        private void SetupEventHandlers()
        {
            // 音频引擎事件
            if (_audioEngine != null)
            {
                _audioEngine.PlaybackStopped += AudioEngine_PlaybackStopped;
                // ⭐ 移除 PlaybackReachedHalfway 事件订阅（由新的统一预加载机制替代）
                _audioEngine.PlaybackEnded += AudioEngine_PlaybackEnded; // ⭐ 播放完成事件
                _audioEngine.GaplessTransitionCompleted += AudioEngine_GaplessTransitionCompleted;
            }

            // 窗体事件
            this.KeyPreview = true;
            this.KeyDown += MainForm_KeyDown;
            this.KeyUp += MainForm_KeyUp; // ⭐ 新增：监听按键松开（用于Scrubbing模式）
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
                System.Diagnostics.Debug.WriteLine($"[Config] 加载配置失败，尝试重置: {ex.Message}");
                try
                {
                    _config = _configManager?.Reset();
                }
                catch (Exception resetEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[Config] 重置配置失败: {resetEx.Message}");
                    _config = new ConfigModel();
                }
            }

            if (_config == null)
            {
                _config = new ConfigModel();
            }

            // Note: Cookies are now managed by AccountState, not ConfigModel

            return _config;
        }

        private void LoadConfig()
        {
            var config = EnsureConfigInitialized();

            // Note: Cookies, MusicU, and CsrfToken are now managed by AccountState and AuthContext
            // The API client will get these from AuthContext automatically

            // 设置音量
            if (_audioEngine != null)
            {
                volumeTrackBar.Value = (int)(config.Volume * 100);
                _audioEngine.SetVolume((float)config.Volume);
                volumeLabel.Text = $"{volumeTrackBar.Value}%";
            }

            // 设置播放模式
            PlayMode playMode = PlayMode.Sequential;
            if (config.PlaybackOrder == "列表循环")
                playMode = PlayMode.Loop;
            else if (config.PlaybackOrder == "单曲循环")
                playMode = PlayMode.LoopOne;
            else if (config.PlaybackOrder == "随机播放")
                playMode = PlayMode.Random;

            if (_audioEngine != null)
            {
                _audioEngine.PlayMode = playMode;
            }

            // 更新菜单选中状态
            UpdatePlaybackOrderMenuCheck();
            UpdateQualityMenuCheck();

            // 更新登录菜单项文本
            UpdateLoginMenuItemText();

            // 刷新音质菜单可用性
            RefreshQualityMenuAvailability();

            // 加载歌词朗读状态
            _autoReadLyrics = config.LyricsReadingEnabled;
            try
            {
                autoReadLyricsMenuItem.Checked = _autoReadLyrics;
                autoReadLyricsMenuItem.Text = _autoReadLyrics ? "关闭歌词朗读\tF11" : "打开歌词朗读\tF11";
            }
            catch
            {
                // 忽略菜单更新错误
            }
            System.Diagnostics.Debug.WriteLine($"[CONFIG] LyricsReadingEnabled={_autoReadLyrics}");

            // UsePersonalCookie 现在根据 MusicU 是否为空自动判断，无需手动设置
            System.Diagnostics.Debug.WriteLine($"[CONFIG] UsePersonalCookie={_apiClient.UsePersonalCookie} (自动检测)");
            System.Diagnostics.Debug.WriteLine($"[CONFIG] AccountState.IsLoggedIn={_accountState?.IsLoggedIn}");
            System.Diagnostics.Debug.WriteLine($"[CONFIG] AccountState.MusicU={(string.IsNullOrEmpty(_accountState?.MusicU) ? "未设置" : "已设置")}");
            System.Diagnostics.Debug.WriteLine($"[CONFIG] AccountState.CsrfToken={(string.IsNullOrEmpty(_accountState?.CsrfToken) ? "未设置" : "已设置")}");

            // 如果已登录，异步刷新用户资料
            if (_apiClient.UsePersonalCookie)
            {
                _ = Task.Run(async () => await EnsureLoginProfileAsync());
            }
        }

        private bool IsUserLoggedIn()
        {
            if (_apiClient?.UsePersonalCookie == true)
            {
                return true;
            }

            return _accountState?.IsLoggedIn == true;
        }

        private void SyncConfigFromApiClient(Forms.LoginSuccessEventArgs? args = null, bool persist = false)
        {
            // Note: Account-related fields (MusicU, CsrfToken, LoginUserId, etc.) are now managed by AccountState
            // This method is no longer needed for account synchronization, but kept for potential future config updates

            if (persist)
            {
                SaveConfig();
            }
        }

        private void ClearLoginState(bool persist)
        {
            _apiClient?.ClearCookies();

            // Note: Account-related fields are now managed by AccountState
            // Login state clearing is handled by AccountStateStore and AuthContext

            if (persist)
            {
                SaveConfig(refreshCookieFromClient: false);
            }

            _accountState = _apiClient?.GetAccountStateSnapshot() ?? new AccountState { IsLoggedIn = false };
            UpdateUiFromAccountState(reapplyCookies: false);
        }

        /// <summary>
        /// 启动时读取 account.json 并初始化登录态
        /// </summary>
        private void ApplyAccountStateOnStartup()
        {
            if (_apiClient == null)
            {
                _accountState = new AccountState { IsLoggedIn = false };
                UpdateUiFromAccountState(reapplyCookies: false);
                return;
            }

            _accountState = _apiClient.GetAccountStateSnapshot();
            bool shouldReapplyCookies = _accountState?.IsLoggedIn == true;
            UpdateUiFromAccountState(reapplyCookies: shouldReapplyCookies);
        }

        private void ReloadAccountState(bool reapplyCookies = false)
        {
            if (_apiClient == null)
            {
                _accountState = new AccountState { IsLoggedIn = false };
            }
            else
            {
                _accountState = _apiClient.GetAccountStateSnapshot();
            }

            UpdateUiFromAccountState(reapplyCookies);
        }

        private void UpdateUiFromAccountState(bool reapplyCookies)
        {
            // Note: Account-related fields are now managed directly from _accountState
            // No need to sync to config

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
                        System.Diagnostics.Debug.WriteLine($"[AccountState] 重新应用Cookie失败: {ex.Message}");
                    }
                }

                UpdateLoginMenuItemText();
            }
            else
            {
                UpdateLoginMenuItemText();
            }
        }
        /// 保存配置
        /// </summary>
        private void SaveConfig(bool refreshCookieFromClient = true)
        {
            try
            {
                var config = EnsureConfigInitialized();
                if (config == null || _configManager == null || _apiClient == null)
                {
                    return;
                }

                if (volumeTrackBar != null)
                {
                    int volumeValue;
                    if (volumeTrackBar.InvokeRequired)
                    {
                        if (volumeTrackBar.IsHandleCreated)
                        {
                            volumeValue = (int)volumeTrackBar.Invoke(new Func<int>(() => volumeTrackBar.Value));
                        }
                        else
                        {
                            volumeValue = volumeTrackBar.Value;
                        }
                    }
                    else
                    {
                        volumeValue = volumeTrackBar.Value;
                    }
                    config.Volume = volumeValue / 100.0;
                }

                // Note: MusicU, CsrfToken, and Cookies are now managed by AccountState, not ConfigModel

                // 保存歌词朗读状态
                config.LyricsReadingEnabled = _autoReadLyrics;

                _configManager.Save(config);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"保存配置失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 启动阶段刷新一次登录资料，确保昵称与服务器同步。
        /// </summary>
        private async Task EnsureLoginProfileAsync()
        {
            if (_apiClient == null || !_apiClient.UsePersonalCookie)
            {
                return;
            }

            try
            {
                var status = await _apiClient.GetLoginStatusAsync();
                if (status == null || !status.IsLoggedIn)
                {
                    System.Diagnostics.Debug.WriteLine("[LoginState] GetLoginStatusAsync 返回未登录状态");
                    return;
                }

                var accountDetail = status.AccountDetail;
                var config = EnsureConfigInitialized();
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
                bool vipChanged = _accountState?.VipType != vipType;

                // Note: Account info is now stored in AccountState, will be updated via ApplyLoginProfile

                if ((nicknameChanged || userIdChanged || avatarChanged) && !IsDisposed)
                {
                    if (IsHandleCreated)
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

                long parsedUserId;
                long? profileId = null;
                if (long.TryParse(userIdString, out parsedUserId))
                {
                    profileId = parsedUserId;
                }

                var profile = new UserAccountInfo
                {
                    UserId = profileId ?? 0,
                    Nickname = nickname,
                    AvatarUrl = avatarUrl,
                    VipType = vipType
                };

                _apiClient?.ApplyLoginProfile(profile);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LoginState] 初始化登录状态失败: {ex.Message}");
            }
        }

        /// <summary>
        /// ⭐ 窗体按键松开事件（用于退出Scrubbing模式）
        /// </summary>
        private void MainForm_KeyUp(object sender, KeyEventArgs e)
        {
            bool handled = false;

            if (e.KeyCode == Keys.Left)
            {
                _leftKeyPressed = false;
                _leftScrubActive = false;
                _leftKeyDownTime = DateTime.MinValue;
                handled = true;
            }
            else if (e.KeyCode == Keys.Right)
            {
                _rightKeyPressed = false;
                _rightScrubActive = false;
                _rightKeyDownTime = DateTime.MinValue;
                handled = true;
            }

            if (!handled)
            {
                return;
            }

            StopScrubKeyTimerIfIdle();

            // ⭐ 如果两个方向键都已松开，通知 SeekManager 完成 Seek 序列
            if (!_leftKeyPressed && !_rightKeyPressed && _seekManager != null)
            {
                _seekManager.FinishSeek();
            }

            // Scrubbing 机制已移除（基于缓存层的新架构不需要）
        }

        #endregion

        #region 搜索功能

        /// <summary>
        /// 搜索按钮点击
        /// </summary>
        private async void searchButton_Click(object sender, EventArgs e)
        {
            await PerformSearch();
        }

        /// <summary>
        /// 重写 ProcessCmdKey 方法，在 Form 层面拦截 Enter 键
        /// 这是解决 TextBox Enter 键焦点跳转问题的标准方法
        /// </summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // 检查是否按下 Enter 键
            if (keyData == Keys.Enter)
            {
                // 如果焦点在搜索框或搜索类型组合框
                if (searchTextBox.Focused || searchTypeComboBox.Focused)
                {
                    // 🎯 触发搜索，阻止默认的焦点导航
                    _ = PerformSearch();
                    return true;  // 返回 true 表示已处理，阻止默认行为
                }
            }

            // 其他情况调用基类方法
            return base.ProcessCmdKey(ref msg, keyData);
        }

        /// <summary>
        /// 搜索文本框回车（保留用于其他用途，主要逻辑已移至 ProcessCmdKey）
        /// </summary>
        private void searchTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                // 实际搜索由 ProcessCmdKey 触发
            }
        }

        /// <summary>
        /// 搜索类型下拉框回车（保留用于其他用途，主要逻辑已移至 ProcessCmdKey）
        /// </summary>
        private void searchTypeComboBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                // 实际搜索由 ProcessCmdKey 触发
            }
        }

        /// <summary>
        /// 获取当前选中的搜索类型（从 ComboBox）
        /// </summary>
        private string GetSelectedSearchType()
        {
            if (searchTypeComboBox.SelectedIndex >= 0 && searchTypeComboBox.SelectedIndex < searchTypeComboBox.Items.Count)
            {
                return searchTypeComboBox.Items[searchTypeComboBox.SelectedIndex].ToString();
            }
            return "歌曲";  // 默认返回歌曲
        }

        /// <summary>
        /// 执行搜索
        /// </summary>
        private async Task PerformSearch()
        {
            string keyword = searchTextBox.Text.Trim();
            if (string.IsNullOrEmpty(keyword))
            {
                MessageBox.Show("请输入搜索关键词", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                searchTextBox.Focus();
                return;
            }

            // 新搜索时重置页码并保存导航状态
            if (keyword != _lastKeyword)
            {
                _currentPage = 1;
                _lastKeyword = keyword;

                // 保存当前状态到导航历史
                SaveNavigationState();
            }

            try
            {
                searchButton.Enabled = false;
                searchTextBox.Enabled = false;
                UpdateStatusBar($"正在搜索: {keyword}...");

                // 标记离开主页
                _isHomePage = false;

                string searchType = GetSelectedSearchType();

                if (searchType == "歌曲")
                {
                    int offset = (_currentPage - 1) * _resultsPerPage;
                    var songResult = await _apiClient.SearchSongsAsync(keyword, _resultsPerPage, offset);
                    _currentSongs = songResult?.Items ?? new List<SongInfo>();

                    int totalPages = 1;
                    if (songResult != null)
                    {
                        totalPages = Math.Max(1, (int)Math.Ceiling(songResult.TotalCount / (double)Math.Max(1, _resultsPerPage)));
                    }
                    _maxPage = totalPages;
                    _hasNextSearchPage = songResult?.HasMore ?? false;

                    // 更新当前浏览列表的来源标识
                    string songsViewSource = $"search:{keyword}:page{_currentPage}";
                    System.Diagnostics.Debug.WriteLine($"[MainForm] 更新浏览列表来源: {songsViewSource}");

                    if (_currentSongs == null || _currentSongs.Count == 0)
                    {
                        int startIndex = (_currentPage - 1) * _resultsPerPage + 1;
                        DisplaySongs(
                            new List<SongInfo>(),
                            showPagination: true,
                            hasNextPage: false,
                            startIndex: startIndex,
                            viewSource: songsViewSource,
                            accessibleName: $"搜索: {keyword}");
                        _hasNextSearchPage = false;
                        _maxPage = Math.Max(1, _currentPage);

                        if (_currentPage == 1)
                        {
                            MessageBox.Show($"未找到相关歌曲: {keyword}", "搜索结果",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                        UpdateStatusBar("未找到结果");
                    }
                    else
                    {
                        int startIndex = (_currentPage - 1) * _resultsPerPage + 1;
                        DisplaySongs(
                            _currentSongs,
                            showPagination: true,
                            hasNextPage: _hasNextSearchPage,
                            startIndex: startIndex,
                            viewSource: songsViewSource,
                            accessibleName: $"搜索: {keyword}");
                        int totalCount = songResult?.TotalCount ?? _currentSongs.Count;
                        UpdateStatusBar($"第 {_currentPage}/{_maxPage} 页，本页 {_currentSongs.Count} 首 / 总 {totalCount} 首");

                        // 焦点自动跳转到列表
                        if (resultListView.Items.Count > 0)
                        {
                            resultListView.Items[0].Selected = true;
                            resultListView.Items[0].Focused = true;
                            resultListView.Focus();
                        }
                    }
                }
                else if (searchType == "歌单")
                {
                    var playlistResult = await _apiClient.SearchPlaylistsAsync(keyword, 100);
                    _currentPlaylists = playlistResult?.Items ?? new List<PlaylistInfo>();
                    _hasNextSearchPage = false;
                    _maxPage = 1;

                    // 更新当前浏览列表的来源标识（歌单列表）
                    string playlistViewSource = $"search:playlist:{keyword}";
                    System.Diagnostics.Debug.WriteLine($"[MainForm] 更新浏览列表来源: {playlistViewSource}");

                    if (_currentPlaylists == null || _currentPlaylists.Count == 0)
                    {
                        MessageBox.Show($"未找到相关歌单: {keyword}", "搜索结果",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                        UpdateStatusBar("未找到结果");
                    }
                    else
                    {
                        DisplayPlaylists(
                            _currentPlaylists,
                            viewSource: playlistViewSource,
                            accessibleName: $"搜索歌单: {keyword}");
                        int totalCount = playlistResult?.TotalCount ?? _currentPlaylists.Count;
                        UpdateStatusBar($"找到 {_currentPlaylists.Count} 个歌单（总计 {totalCount} 个）");
                    }
                }
                else if (searchType == "专辑")
                {
                    var albumResult = await _apiClient.SearchAlbumsAsync(keyword, 100);
                    _currentAlbums = albumResult?.Items ?? new List<AlbumInfo>();
                    _hasNextSearchPage = false;
                    _maxPage = 1;

                    // 更新当前浏览列表的来源标识（专辑列表）
                    string albumViewSource = $"search:album:{keyword}";
                    System.Diagnostics.Debug.WriteLine($"[MainForm] 更新浏览列表来源: {albumViewSource}");

                    if (_currentAlbums == null || _currentAlbums.Count == 0)
                    {
                        MessageBox.Show($"未找到相关专辑: {keyword}", "搜索结果",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                        UpdateStatusBar("未找到结果");
                    }
                    else
                    {
                        DisplayAlbums(
                            _currentAlbums,
                            viewSource: albumViewSource,
                            accessibleName: $"搜索专辑: {keyword}");
                        int totalCount = albumResult?.TotalCount ?? _currentAlbums.Count;
                        UpdateStatusBar($"找到 {_currentAlbums.Count} 个专辑（总计 {totalCount} 个）");
                    }
                }
                else if (searchType == "歌手")
                {
                    int offset = (_currentPage - 1) * _resultsPerPage;
                    var artistResult = await _apiClient.SearchArtistsAsync(keyword, _resultsPerPage, offset);
                    _currentArtists = artistResult?.Items ?? new List<ArtistInfo>();

                    int totalPages = 1;
                    if (artistResult != null)
                    {
                        totalPages = Math.Max(1, (int)Math.Ceiling(artistResult.TotalCount / (double)Math.Max(1, _resultsPerPage)));
                    }
                    _maxPage = totalPages;
                    _hasNextSearchPage = artistResult?.HasMore ?? false;

                    string artistViewSource = $"search:artist:{keyword}:page{_currentPage}";
                    System.Diagnostics.Debug.WriteLine($"[MainForm] 更新浏览列表来源: {artistViewSource}");

                    if (_currentArtists.Count == 0)
                    {
                        DisplayArtists(
                            new List<ArtistInfo>(),
                            showPagination: true,
                            hasNextPage: false,
                            startIndex: offset + 1,
                            viewSource: artistViewSource,
                            accessibleName: $"搜索歌手: {keyword}");

                        if (_currentPage == 1)
                        {
                            MessageBox.Show($"未找到相关歌手: {keyword}", "搜索结果",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }

                        UpdateStatusBar("未找到结果");
                    }
                    else
                    {
                        DisplayArtists(
                            _currentArtists,
                            showPagination: true,
                            hasNextPage: _hasNextSearchPage,
                            startIndex: offset + 1,
                            viewSource: artistViewSource,
                            accessibleName: $"搜索歌手: {keyword}");

                        int totalCount = artistResult?.TotalCount ?? _currentArtists.Count;
                        UpdateStatusBar($"第 {_currentPage}/{_maxPage} 页，本页 {_currentArtists.Count} 位 / 总 {totalCount} 位");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"搜索异常: {ex}");
                string detailedMessage = ex.InnerException != null
                    ? ex.InnerException.ToString()
                    : ex.ToString();
                MessageBox.Show($"搜索失败: {ex.Message}\n\n详细信息: {detailedMessage}", "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatusBar("搜索失败");
            }
            finally
            {
                searchButton.Enabled = true;
                searchTextBox.Enabled = true;
            }
        }
        /// <summary>
        /// 加载主页列表（包含推荐歌单、用户歌单、排行榜等）
        /// 使用分类结构，避免一次加载过多资源
        /// </summary>
        /// <param name="skipSave">是否跳过保存状态（用于后退时）</param>
        private async Task LoadHomePageAsync(bool skipSave = false)
        {
            try
            {
                if (!skipSave)
                {
                    // 主页是起始页，清空导航历史
                    _navigationHistory.Clear();
                    System.Diagnostics.Debug.WriteLine("[Navigation] 加载主页，清空导航历史");
                }

                UpdateStatusBar("正在加载主页...");
                resultListView.Items.Clear();

                var homeItems = new List<ListItemInfo>();
                bool isLoggedIn = _accountState?.IsLoggedIn == true;

                // 如果已登录，预先加载用户数据以获取数量信息
                int userPlaylistCount = 0;
                int userAlbumCount = 0;
                PlaylistInfo? likedPlaylist = null;

                if (isLoggedIn)
                {
                    try
                    {
                        var userInfo = await _apiClient.GetUserAccountAsync();
                        if (userInfo != null && userInfo.UserId > 0)
                        {
                            _loggedInUserId = userInfo.UserId;

                            // 获取用户歌单列表与总数
                            var (playlists, totalCount) = await _apiClient.GetUserPlaylistsAsync(userInfo.UserId);
                            if (playlists != null && playlists.Count > 0)
                            {
                                likedPlaylist = playlists.FirstOrDefault(p =>
                                    !string.IsNullOrEmpty(p.Name) &&
                                    p.Name.IndexOf("喜欢的音乐", StringComparison.OrdinalIgnoreCase) >= 0);

                                userPlaylistCount = totalCount;
                                if (likedPlaylist != null && userPlaylistCount > 0)
                                {
                                    userPlaylistCount = Math.Max(0, userPlaylistCount - 1);
                                }

                                System.Diagnostics.Debug.WriteLine($"[HomePage] 用户歌单总数: {totalCount}, 排除喜欢的音乐后: {userPlaylistCount}");
                            }

                            // 获取收藏专辑总数
                            try
                            {
                                var (_, albumCount) = await _apiClient.GetUserAlbumsAsync(1, 0);
                                userAlbumCount = albumCount;
                                System.Diagnostics.Debug.WriteLine($"[HomePage] 收藏专辑数量: {userAlbumCount}");
                            }
                            catch (Exception albumEx)
                            {
                                System.Diagnostics.Debug.WriteLine($"[HomePage] 获取收藏专辑数量失败: {albumEx.Message}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[HomePage] 预加载用户数据失败: {ex.Message}");
                    }
                }
                else
                {
                    _loggedInUserId = 0;
                }

                _userLikedPlaylist = likedPlaylist;

                // 如果已登录，添加个人资源分类（在前面）
                if (isLoggedIn)
                {
                    CloudSongPageResult? cloudSummary = null;
                    try
                    {
                        _cloudTotalCount = 0;
                        _cloudUsedSize = 0;
                        _cloudMaxSize = 0;

                        cloudSummary = await _apiClient.GetCloudSongsAsync(limit: 1, offset: 0);
                        _cloudTotalCount = cloudSummary.TotalCount;
                        _cloudUsedSize = cloudSummary.UsedSize;
                        _cloudMaxSize = cloudSummary.MaxSize;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Home] 获取云盘摘要失败: {ex.Message}");
                    }

                    // 1. 喜欢的音乐
                    homeItems.Add(new ListItemInfo
                    {
                        Type = ListItemType.Category,
                        CategoryId = "user_liked_songs",
                        CategoryName = "喜欢的音乐",
                        CategoryDescription = "您收藏的所有歌曲",
                        ItemCount = _userLikedPlaylist?.TrackCount ?? likedPlaylist?.TrackCount ?? 0,
                        ItemUnit = "首"
                    });

                    // 2. 我的歌单
                    homeItems.Add(new ListItemInfo
                    {
                        Type = ListItemType.Category,
                        CategoryId = "user_playlists",
                        CategoryName = "我的歌单",
                        CategoryDescription = "您创建和收藏的歌单",
                        ItemCount = userPlaylistCount,
                        ItemUnit = "个"
                    });

                    // 3. 收藏的专辑
                    homeItems.Add(new ListItemInfo
                    {
                        Type = ListItemType.Category,
                        CategoryId = "user_albums",
                        CategoryName = "收藏的专辑",
                        CategoryDescription = "您收藏的专辑",
                        ItemCount = userAlbumCount,
                        ItemUnit = "个"
                    });

                    // 3.5 云盘
                    homeItems.Add(new ListItemInfo
                    {
                        Type = ListItemType.Category,
                        CategoryId = "user_cloud",
                        CategoryName = "云盘",
                        CategoryDescription = cloudSummary != null
                            ? $"已用 {FormatSize(_cloudUsedSize)} / {FormatSize(_cloudMaxSize)}"
                            : "上传和管理您的私人音乐",
                        ItemCount = _cloudTotalCount,
                        ItemUnit = "首"
                    });

                    // 3.6 收藏的歌手
                    homeItems.Add(new ListItemInfo
                    {
                        Type = ListItemType.Category,
                        CategoryId = "artist_favorites",
                        CategoryName = "收藏的歌手",
                        CategoryDescription = "管理您收藏的歌手",
                        ItemUnit = "位"
                    });

                    // 4. 每日推荐
                    homeItems.Add(new ListItemInfo
                    {
                        Type = ListItemType.Category,
                        CategoryId = "daily_recommend",
                        CategoryName = "每日推荐",
                        CategoryDescription = "根据您的听歌习惯推荐的歌曲和歌单",
                        ItemCount = 2,
                        ItemUnit = "个"
                    });

                    // 5. 为您推荐
                    homeItems.Add(new ListItemInfo
                    {
                        Type = ListItemType.Category,
                        CategoryId = "personalized",
                        CategoryName = "为您推荐",
                        CategoryDescription = "个性化推荐歌单和新歌",
                        ItemCount = 2,
                        ItemUnit = "个"
                    });

                    // 6. 精品歌单
                    homeItems.Add(new ListItemInfo
                    {
                        Type = ListItemType.Category,
                        CategoryId = "highquality_playlists",
                        CategoryName = "精品歌单",
                        CategoryDescription = "网易云官方精选歌单"
                    });

                    // 7. 新歌速递
                    homeItems.Add(new ListItemInfo
                    {
                        Type = ListItemType.Category,
                        CategoryId = "new_songs",
                        CategoryName = "新歌速递",
                        CategoryDescription = "全网最新发布歌曲",
                        ItemCount = 5,
                        ItemUnit = "个"
                    });

                    // 8. 歌单分类
                    homeItems.Add(new ListItemInfo
                    {
                        Type = ListItemType.Category,
                        CategoryId = "playlist_category",
                        CategoryName = "歌单分类",
                        CategoryDescription = "按分类浏览歌单"
                    });

                    // 9. 歌手分类
                    homeItems.Add(new ListItemInfo
                    {
                        Type = ListItemType.Category,
                        CategoryId = "artist_categories",
                        CategoryName = "歌手分类",
                        CategoryDescription = "按类型和地区浏览歌手"
                    });

                    // 12. 新碟上架（新增）
                    homeItems.Add(new ListItemInfo
                    {
                        Type = ListItemType.Category,
                        CategoryId = "new_albums",
                        CategoryName = "新碟上架",
                        CategoryDescription = "最新发布的专辑"
                    });

                    // 13. 官方排行榜
                    homeItems.Add(new ListItemInfo
                    {
                        Type = ListItemType.Category,
                        CategoryId = "toplist",
                        CategoryName = "官方排行榜",
                        CategoryDescription = "查看各类音乐排行榜"
                    });
                }
                else
                {
                    _cloudTotalCount = 0;
                    _cloudUsedSize = 0;
                    _cloudMaxSize = 0;

                    // 未登录用户显示的分类

                    // 1. 精品歌单
                    homeItems.Add(new ListItemInfo
                    {
                        Type = ListItemType.Category,
                        CategoryId = "highquality_playlists",
                        CategoryName = "精品歌单",
                        CategoryDescription = "网易云官方精选歌单"
                    });

                    // 2. 新歌速递
                    homeItems.Add(new ListItemInfo
                    {
                        Type = ListItemType.Category,
                        CategoryId = "new_songs",
                        CategoryName = "新歌速递",
                        CategoryDescription = "全网最新发布歌曲",
                        ItemCount = 5,
                        ItemUnit = "个"
                    });

                    // 3. 歌单分类
                    homeItems.Add(new ListItemInfo
                    {
                        Type = ListItemType.Category,
                        CategoryId = "playlist_category",
                        CategoryName = "歌单分类",
                        CategoryDescription = "按分类浏览歌单"
                    });

                    // 4. 歌手分类
                    homeItems.Add(new ListItemInfo
                    {
                        Type = ListItemType.Category,
                        CategoryId = "artist_categories",
                        CategoryName = "歌手分类",
                        CategoryDescription = "按类型和地区浏览歌手"
                    });

                    // 5. 新碟上架
                    homeItems.Add(new ListItemInfo
                    {
                        Type = ListItemType.Category,
                        CategoryId = "new_albums",
                        CategoryName = "新碟上架",
                        CategoryDescription = "最新发布的专辑"
                    });

                    // 6. 官方排行榜
                    homeItems.Add(new ListItemInfo
                    {
                        Type = ListItemType.Category,
                        CategoryId = "toplist",
                        CategoryName = "官方排行榜",
                        CategoryDescription = "查看各类音乐排行榜"
                    });
                }

                // 显示主页列表
                DisplayListItems(
                    homeItems,
                    viewSource: "homepage",
                    accessibleName: "主页");

                // 清空其他列表缓存
                _currentSongs.Clear();
                _currentPlaylists.Clear();
                _currentAlbums.Clear();
                _currentPlaylist = null;

                UpdateStatusBar($"主页加载完成");

                // 焦点跳转到列表
                if (resultListView.Items.Count > 0)
                {
                    resultListView.Items[0].Selected = true;
                    resultListView.Items[0].Focused = true;
                    resultListView.Focus();
                }

                System.Diagnostics.Debug.WriteLine($"[LoadHomePage] 主页加载完成，共 {homeItems.Count} 个分类");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LoadHomePage] 异常: {ex}");
                MessageBox.Show($"加载主页失败: {ex.Message}\n\n请检查网络连接或稍后再试。", "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatusBar("加载主页失败");
            }
        }

        /// <summary>
        /// 处理 ListItemInfo 的激活（双击或回车）
        /// </summary>
        private async Task HandleListItemActivate(ListItemInfo listItem)
        {
            switch (listItem.Type)
            {
                case ListItemType.Song:
                    // 播放歌曲
                    if (listItem.Song != null)
                    {
                        await PlaySong(listItem.Song);
                    }
                    break;

                case ListItemType.Playlist:
                    // 打开歌单
                    if (listItem.Playlist != null)
                    {
                        await OpenPlaylist(listItem.Playlist);
                    }
                    break;

                case ListItemType.Album:
                    // 打开专辑
                    if (listItem.Album != null)
                    {
                        await OpenAlbum(listItem.Album);
                    }
                    break;

                case ListItemType.Artist:
                    if (listItem.Artist != null)
                    {
                        await OpenArtistAsync(listItem.Artist);
                    }
                    break;

                case ListItemType.Category:
                    // 加载分类内容
                    await LoadCategoryContent(listItem.CategoryId);
                    break;
            }
        }

        /// <summary>
        /// 加载分类内容
        /// </summary>
        /// <param name="categoryId">分类ID</param>
        /// <param name="skipSave">是否跳过保存状态（用于后退时）</param>
        private async Task LoadCategoryContent(string categoryId, bool skipSave = false)
        {
            try
            {
                // ⭐ 方案2：冷启动保护 - 如果应用刚启动不到3秒，延迟请求
                var timeSinceStartup = (DateTime.Now - _appStartTime).TotalSeconds;
                if (timeSinceStartup < 3.0)
                {
                    int delayMs = (int)((3.0 - timeSinceStartup) * 1000);
                    System.Diagnostics.Debug.WriteLine($"[ColdStartProtection] 应用启动仅 {timeSinceStartup:F1}秒，延迟 {delayMs}ms 以避免风控");
                    await Task.Delay(Math.Min(delayMs, 2000));  // 最多延迟2秒
                }

                UpdateStatusBar($"正在加载 {categoryId}...");

                // 保存当前状态到导航历史
                if (!skipSave)
                {
                    SaveNavigationState();
                }

                _isHomePage = false;

            switch (categoryId)
            {
                case "user_liked_songs":
                    await LoadUserLikedSongs();
                    break;

                case "user_playlists":
                    await LoadUserPlaylists();
                    break;

                case "user_albums":
                    await LoadUserAlbums();
                    break;

                case "user_cloud":
                    _cloudPage = 1;
                    await LoadCloudSongsAsync();
                    break;

                case "daily_recommend":
                    await LoadDailyRecommend();
                    break;

                case "personalized":
                    await LoadPersonalized();
                    break;

                case "toplist":
                    await LoadToplist();
                    break;

                case "daily_recommend_songs":
                    await LoadDailyRecommendSongs();
                    break;

                case "daily_recommend_playlists":
                    await LoadDailyRecommendPlaylists();
                    break;

                case "personalized_playlists":
                    await LoadPersonalizedPlaylists();
                    break;

                case "personalized_newsongs":
                    await LoadPersonalizedNewSongs();
                    break;

                case "highquality_playlists":
                    await LoadHighQualityPlaylists();
                    break;

                case "new_songs":
                    await LoadNewSongs();
                    break;

                case "new_songs_all":
                    await LoadNewSongsAll();
                    break;

                case "new_songs_chinese":
                    await LoadNewSongsChinese();
                    break;

                case "new_songs_western":
                    await LoadNewSongsWestern();
                    break;

                case "new_songs_japan":
                    await LoadNewSongsJapan();
                    break;

                case "new_songs_korea":
                    await LoadNewSongsKorea();
                    break;

                case "personalized_newsong":
                    await LoadPersonalizedNewSong();
                    break;

                case "playlist_category":
                    await LoadPlaylistCategory();
                    break;

                case "new_albums":
                    await LoadNewAlbums();
                    break;

                case "artist_favorites":
                    await LoadArtistFavoritesAsync(skipSave: true);
                    break;

                case "artist_categories":
                    await LoadArtistCategoryTypesAsync(skipSave: true);
                    break;

                default:
                    if (categoryId.StartsWith("playlist_cat_", StringComparison.OrdinalIgnoreCase))
                    {
                        string catName = categoryId.Substring("playlist_cat_".Length);
                        if (!string.IsNullOrWhiteSpace(catName))
                        {
                            await LoadPlaylistsByCat(catName);
                        }
                        else
                        {
                            MessageBox.Show($"未知的分类: {categoryId}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    }
                    else if (categoryId.StartsWith("artist_top_", StringComparison.OrdinalIgnoreCase) &&
                             long.TryParse(categoryId.Substring("artist_top_".Length), out var artistTopId))
                    {
                        await LoadArtistTopSongsAsync(artistTopId, skipSave: true);
                    }
                    else if (categoryId.StartsWith("artist_songs_", StringComparison.OrdinalIgnoreCase) &&
                             long.TryParse(categoryId.Substring("artist_songs_".Length), out var artistSongsId))
                    {
                        await LoadArtistSongsAsync(artistSongsId, skipSave: true);
                    }
                    else if (categoryId.StartsWith("artist_albums_", StringComparison.OrdinalIgnoreCase) &&
                             long.TryParse(categoryId.Substring("artist_albums_".Length), out var artistAlbumsId))
                    {
                        await LoadArtistAlbumsAsync(artistAlbumsId, skipSave: true);
                    }
                    else if (categoryId.StartsWith("artist_type_", StringComparison.OrdinalIgnoreCase) &&
                             int.TryParse(categoryId.Substring("artist_type_".Length), out var typeCode))
                    {
                        await LoadArtistCategoryAreasAsync(typeCode, skipSave: true);
                    }
                    else if (categoryId.StartsWith("artist_area_", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = categoryId.Split('_');
                        if (parts.Length == 4 &&
                            int.TryParse(parts[2], out var typeFilter) &&
                            int.TryParse(parts[3], out var areaFilter))
                        {
                            await LoadArtistsByCategoryAsync(typeFilter, areaFilter, skipSave: true);
                        }
                        else
                        {
                            MessageBox.Show($"未知的歌手分类: {categoryId}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    }
                    else
                    {
                        MessageBox.Show($"未知的分类: {categoryId}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    break;
            }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LoadCategoryContent] 异常: {ex}");
                MessageBox.Show($"加载分类内容失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatusBar("加载失败");
            }
        }

        /// <summary>
        /// 加载最近播放的歌曲
        /// </summary>
        private async Task LoadRecentPlayedSongs(bool preserveSelection = false)
        {
            try
            {
                UpdateStatusBar("正在加载最近播放...");

                var recentSongs = await _apiClient.GetRecentPlayedSongsAsync(100);

                if (recentSongs == null || recentSongs.Count == 0)
                {
                    MessageBox.Show("暂无最近播放记录", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    UpdateStatusBar("就绪");
                    return;
                }

                DisplaySongs(
                    recentSongs,
                    preserveSelection: preserveSelection,
                    viewSource: "recent_played",
                    accessibleName: "最近听过");
                _currentPlaylist = null;  // 清空当前歌单
                UpdateStatusBar($"加载完成，共 {recentSongs.Count} 首歌曲");

                System.Diagnostics.Debug.WriteLine($"[LoadRecentPlayedSongs] 成功加载 {recentSongs.Count} 首最近播放歌曲");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LoadRecentPlayedSongs] 异常: {ex}");
                MessageBox.Show($"加载最近播放失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatusBar("加载失败");
            }
        }

        #region 新增主页入口Load方法

        /// <summary>
        /// 加载精品歌单
        /// </summary>
        private async Task LoadHighQualityPlaylists()
        {
            try
            {
                UpdateStatusBar("正在加载精品歌单...");

                var result = await _apiClient.GetHighQualityPlaylistsAsync("全部", 50, 0);
                var playlists = result.Item1;
                var lasttime = result.Item2;
                var more = result.Item3;

                if (playlists == null || playlists.Count == 0)
                {
                    MessageBox.Show("暂无精品歌单", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    UpdateStatusBar("就绪");
                    return;
                }

                DisplayPlaylists(
                    playlists,
                    viewSource: "highquality_playlists",
                    accessibleName: "精品歌单");
                UpdateStatusBar($"加载完成，共 {playlists.Count} 个精品歌单");

                System.Diagnostics.Debug.WriteLine($"[LoadHighQualityPlaylists] 成功加载 {playlists.Count} 个精品歌单, lasttime={lasttime}, more={more}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LoadHighQualityPlaylists] 异常: {ex}");
                MessageBox.Show($"加载精品歌单失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatusBar("加载失败");
            }
        }

        /// <summary>
        /// 加载新歌速递（显示地区子分类）
        /// </summary>
        private Task LoadNewSongs()
        {
            try
            {
                UpdateStatusBar("正在加载新歌速递...");

                // 显示地区子分类选项
                var subcategories = new List<ListItemInfo>
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
                };

                DisplayListItems(
                    subcategories,
                    viewSource: "new_songs",
                    accessibleName: "新歌速递");
                UpdateStatusBar("请选择地区");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LoadNewSongs] 异常: {ex}");
                MessageBox.Show($"加载新歌速递失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatusBar("加载失败");
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// 加载全部新歌
        /// </summary>
        private async Task LoadNewSongsAll()
        {
            await LoadNewSongsByArea(0, "全部");
        }

        /// <summary>
        /// 加载华语新歌
        /// </summary>
        private async Task LoadNewSongsChinese()
        {
            await LoadNewSongsByArea(7, "华语");
        }

        /// <summary>
        /// 加载欧美新歌
        /// </summary>
        private async Task LoadNewSongsWestern()
        {
            await LoadNewSongsByArea(96, "欧美");
        }

        /// <summary>
        /// 加载日本新歌
        /// </summary>
        private async Task LoadNewSongsJapan()
        {
            await LoadNewSongsByArea(8, "日本");
        }

        /// <summary>
        /// 加载韩国新歌
        /// </summary>
        private async Task LoadNewSongsKorea()
        {
            await LoadNewSongsByArea(16, "韩国");
        }

        /// <summary>
        /// 加载新歌（通用方法）
        /// </summary>
        private async Task LoadNewSongsByArea(int areaType, string areaName)
        {
            try
            {
                UpdateStatusBar($"正在加载{areaName}新歌...");

                var songs = await _apiClient.GetNewSongsAsync(areaType);

                if (songs == null || songs.Count == 0)
                {
                    MessageBox.Show($"暂无{areaName}新歌", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    UpdateStatusBar("就绪");
                    return;
                }

                string areaViewSource = $"new_songs_{areaName.ToLower()}";
                DisplaySongs(
                    songs,
                    viewSource: areaViewSource,
                    accessibleName: $"{areaName}新歌速递");
                _currentPlaylist = null;
                UpdateStatusBar($"加载完成，共 {songs.Count} 首{areaName}新歌");

                System.Diagnostics.Debug.WriteLine($"[LoadNewSongs] 成功加载 {songs.Count} 首{areaName}新歌");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LoadNewSongsByArea] 异常: {ex}");
                MessageBox.Show($"加载{areaName}新歌失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatusBar("加载失败");
            }
        }

        /// <summary>
        /// 加载推荐新歌（个性化）
        /// </summary>
        private async Task LoadPersonalizedNewSong()
        {
            try
            {
                UpdateStatusBar("正在加载推荐新歌...");

                var songs = await _apiClient.GetPersonalizedNewSongsAsync();

                if (songs == null || songs.Count == 0)
                {
                    MessageBox.Show("暂无推荐新歌", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    UpdateStatusBar("就绪");
                    return;
                }

                DisplaySongs(
                    songs,
                    viewSource: "personalized_newsong",
                    accessibleName: "推荐新歌");
                _currentPlaylist = null;
                UpdateStatusBar($"加载完成，共 {songs.Count} 首推荐新歌");

                System.Diagnostics.Debug.WriteLine($"[LoadPersonalizedNewSong] 成功加载 {songs.Count} 首推荐新歌");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LoadPersonalizedNewSong] 异常: {ex}");
                MessageBox.Show($"加载推荐新歌失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatusBar("加载失败");
            }
        }

        /// <summary>
        /// 加载歌单分类（显示分类列表）
        /// </summary>
        private Task LoadPlaylistCategory()
        {
            try
            {
                UpdateStatusBar("正在加载歌单分类...");

                // 显示常用分类列表
                var categories = new List<ListItemInfo>
                {
                    new ListItemInfo
                    {
                        Type = ListItemType.Category,
                        CategoryId = "playlist_cat_华语",
                        CategoryName = "华语",
                        CategoryDescription = "华语歌单"
                    },
                    new ListItemInfo
                    {
                        Type = ListItemType.Category,
                        CategoryId = "playlist_cat_流行",
                        CategoryName = "流行",
                        CategoryDescription = "流行歌单"
                    },
                    new ListItemInfo
                    {
                        Type = ListItemType.Category,
                        CategoryId = "playlist_cat_摇滚",
                        CategoryName = "摇滚",
                        CategoryDescription = "摇滚歌单"
                    },
                    new ListItemInfo
                    {
                        Type = ListItemType.Category,
                        CategoryId = "playlist_cat_民谣",
                        CategoryName = "民谣",
                        CategoryDescription = "民谣歌单"
                    },
                    new ListItemInfo
                    {
                        Type = ListItemType.Category,
                        CategoryId = "playlist_cat_电子",
                        CategoryName = "电子",
                        CategoryDescription = "电子音乐歌单"
                    },
                    new ListItemInfo
                    {
                        Type = ListItemType.Category,
                        CategoryId = "playlist_cat_轻音乐",
                        CategoryName = "轻音乐",
                        CategoryDescription = "轻音乐歌单"
                    },
                    new ListItemInfo
                    {
                        Type = ListItemType.Category,
                        CategoryId = "playlist_cat_影视原声",
                        CategoryName = "影视原声",
                        CategoryDescription = "影视原声歌单"
                    },
                    new ListItemInfo
                    {
                        Type = ListItemType.Category,
                        CategoryId = "playlist_cat_ACG",
                        CategoryName = "ACG",
                        CategoryDescription = "ACG歌单"
                    },
                    new ListItemInfo
                    {
                        Type = ListItemType.Category,
                        CategoryId = "playlist_cat_怀旧",
                        CategoryName = "怀旧",
                        CategoryDescription = "怀旧歌单"
                    },
                    new ListItemInfo
                    {
                        Type = ListItemType.Category,
                        CategoryId = "playlist_cat_治愈",
                        CategoryName = "治愈",
                        CategoryDescription = "治愈歌单"
                    }
                };

                DisplayListItems(
                    categories,
                    viewSource: "playlist_category",
                    accessibleName: "歌单分类");
                UpdateStatusBar("请选择歌单分类");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LoadPlaylistCategory] 异常: {ex}");
                MessageBox.Show($"加载歌单分类失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatusBar("加载失败");
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// 加载指定分类的歌单
        /// </summary>
        private async Task LoadPlaylistsByCat(string cat)
        {
            try
            {
                UpdateStatusBar($"正在加载{cat}歌单...");

                var result = await _apiClient.GetPlaylistsByCategoryAsync(cat, "hot", 50, 0);
                var playlists = result.Item1;
                var total = result.Item2;
                var more = result.Item3;

                if (playlists == null || playlists.Count == 0)
                {
                    MessageBox.Show($"暂无{cat}歌单", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    UpdateStatusBar("就绪");
                    return;
                }

                DisplayPlaylists(
                    playlists,
                    viewSource: $"playlist_cat_{cat}",
                    accessibleName: $"{cat}歌单");
                UpdateStatusBar($"加载完成，共 {playlists.Count} 个{cat}歌单");

                System.Diagnostics.Debug.WriteLine($"[LoadPlaylistsByCat] 成功加载 {playlists.Count} 个{cat}歌单, total={total}, more={more}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LoadPlaylistsByCat] 异常: {ex}");
                MessageBox.Show($"加载{cat}歌单失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatusBar("加载失败");
            }
        }

        /// <summary>
        /// 加载新碟上架
        /// </summary>
        private async Task LoadNewAlbums()
        {
            try
            {
                UpdateStatusBar("正在加载新碟上架...");

                var albums = await _apiClient.GetNewAlbumsAsync();

                if (albums == null || albums.Count == 0)
                {
                    MessageBox.Show("暂无新碟上架", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    UpdateStatusBar("就绪");
                    return;
                }

                DisplayAlbums(
                    albums,
                    viewSource: "new_albums",
                    accessibleName: "新碟上架");
                UpdateStatusBar($"加载完成，共 {albums.Count} 个新专辑");

                System.Diagnostics.Debug.WriteLine($"[LoadNewAlbums] 成功加载 {albums.Count} 个新专辑");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LoadNewAlbums] 异常: {ex}");
                MessageBox.Show($"加载新碟上架失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatusBar("加载失败");
            }
        }

        #endregion

        /// <summary>
        /// 加载用户喜欢的歌曲
        /// </summary>
        private async Task LoadUserLikedSongs(bool preserveSelection = false)
        {
            try
            {
                // 优先使用缓存的歌单对象（主页加载时已获取）
                if (_userLikedPlaylist != null)
                {
                    System.Diagnostics.Debug.WriteLine($"[LoadUserLikedSongs] 使用缓存的歌单对象: {_userLikedPlaylist.Name}");
                    await OpenPlaylist(_userLikedPlaylist, skipSave: false, preserveSelection: preserveSelection);
                    return;
                }

                // 如果缓存为空，则重新获取（fallback逻辑）
                System.Diagnostics.Debug.WriteLine("[LoadUserLikedSongs] 缓存为空，重新获取歌单列表");
                var userInfo = await _apiClient.GetUserAccountAsync();
                if (userInfo == null || userInfo.UserId <= 0)
                {
                    MessageBox.Show("获取用户信息失败", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                if (userInfo.UserId > 0)
                {
                    _loggedInUserId = userInfo.UserId;
                }

                var (playlists, _) = await _apiClient.GetUserPlaylistsAsync(userInfo.UserId);
                if (playlists == null || playlists.Count == 0)
                {
                    MessageBox.Show("获取歌单列表失败", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                var likedPlaylist = playlists.FirstOrDefault(p =>
                    !string.IsNullOrEmpty(p.Name) &&
                    p.Name.IndexOf("喜欢的音乐", StringComparison.OrdinalIgnoreCase) >= 0);

                if (likedPlaylist == null)
                {
                    MessageBox.Show("未找到喜欢的音乐歌单", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                // 缓存歌单对象
                _userLikedPlaylist = likedPlaylist;
                await OpenPlaylist(likedPlaylist, skipSave: false, preserveSelection: preserveSelection);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LoadUserLikedSongs] 异常: {ex}");
                MessageBox.Show($"加载喜欢的音乐失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// 加载用户歌单
        /// </summary>
        private async Task LoadUserPlaylists(bool preserveSelection = false)
        {
            try
            {
                var userInfo = await _apiClient.GetUserAccountAsync();
                if (userInfo == null || userInfo.UserId <= 0)
                {
                    MessageBox.Show("获取用户信息失败", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                if (userInfo.UserId > 0)
                {
                    _loggedInUserId = userInfo.UserId;
                }

                var (playlists, _) = await _apiClient.GetUserPlaylistsAsync(userInfo.UserId);
                if (playlists == null || playlists.Count == 0)
                {
                    MessageBox.Show("您还没有歌单", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                // 过滤掉"喜欢的音乐"歌单（ID等于用户ID的歌单）
                var filteredPlaylists = playlists.Where(p => p.Id != userInfo.UserId.ToString()).ToList();

                if (filteredPlaylists.Count == 0)
                {
                    MessageBox.Show("您还没有创建或收藏歌单", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                DisplayPlaylists(
                    filteredPlaylists,
                    preserveSelection: preserveSelection,
                    viewSource: "user_playlists",
                    accessibleName: "我的歌单");
                _currentPlaylist = null;  // 清空当前歌单
                UpdateStatusBar($"加载完成，共 {filteredPlaylists.Count} 个歌单");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LoadUserPlaylists] 异常: {ex}");
                throw;
            }
        }

        /// <summary>
        /// 加载收藏的专辑
        /// </summary>
        private async Task LoadUserAlbums(bool preserveSelection = false)
        {
            try
            {
                var (albums, totalCount) = await _apiClient.GetUserAlbumsAsync();
                if (albums == null || albums.Count == 0)
                {
                    MessageBox.Show("您还没有收藏专辑", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                DisplayAlbums(
                    albums,
                    preserveSelection: preserveSelection,
                    viewSource: "user_albums",
                    accessibleName: "收藏的专辑");
                UpdateStatusBar($"加载完成，共 {totalCount} 个专辑");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LoadUserAlbums] 异常: {ex}");
                throw;
            }
        }

        /// <summary>
        /// 加载每日推荐
        /// </summary>
        private Task LoadDailyRecommend()
        {
            try
            {
                var items = new List<ListItemInfo>();

                // 添加每日推荐歌曲
                items.Add(new ListItemInfo
                {
                    Type = ListItemType.Category,
                    CategoryId = "daily_recommend_songs",
                    CategoryName = "每日推荐歌曲",
                    CategoryDescription = "根据您的听歌习惯推荐的歌曲"
                });

                // 添加每日推荐歌单
                items.Add(new ListItemInfo
                {
                    Type = ListItemType.Category,
                    CategoryId = "daily_recommend_playlists",
                    CategoryName = "每日推荐歌单",
                    CategoryDescription = "根据您的听歌习惯推荐的歌单"
                });

                DisplayListItems(
                    items,
                    viewSource: "daily_recommend",
                    accessibleName: "每日推荐");
                UpdateStatusBar("每日推荐");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LoadDailyRecommend] 异常: {ex}");
                throw;
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// 加载个性化推荐
        /// </summary>
        private Task LoadPersonalized()
        {
            try
            {
                var items = new List<ListItemInfo>();

                // 添加推荐歌单
                items.Add(new ListItemInfo
                {
                    Type = ListItemType.Category,
                    CategoryId = "personalized_playlists",
                    CategoryName = "推荐歌单",
                    CategoryDescription = "根据您的听歌习惯推荐的歌单"
                });

                // 添加推荐新歌
                items.Add(new ListItemInfo
                {
                    Type = ListItemType.Category,
                    CategoryId = "personalized_newsongs",
                    CategoryName = "推荐新歌",
                    CategoryDescription = "最新发行的歌曲推荐"
                });

                DisplayListItems(
                    items,
                    viewSource: "personalized",
                    accessibleName: "为您推荐");
                UpdateStatusBar("为您推荐");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LoadPersonalized] 异常: {ex}");
                throw;
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// 加载排行榜
        /// </summary>
        private async Task LoadToplist()
        {
            try
            {
                var toplists = await _apiClient.GetToplistAsync();
                if (toplists == null || toplists.Count == 0)
                {
                    MessageBox.Show("获取排行榜失败", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                DisplayPlaylists(
                    toplists,
                    viewSource: "toplist",
                    accessibleName: "官方排行榜");
                UpdateStatusBar($"加载完成，共 {toplists.Count} 个排行榜");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LoadToplist] 异常: {ex}");
                throw;
            }
        }

        /// <summary>
        /// 加载每日推荐歌曲
        /// </summary>
        private async Task LoadDailyRecommendSongs()
        {
            try
            {
                var songs = await _apiClient.GetDailyRecommendSongsAsync();
                if (songs == null || songs.Count == 0)
                {
                    MessageBox.Show("获取每日推荐歌曲失败", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                DisplaySongs(
                    songs,
                    viewSource: "daily_recommend_songs",
                    accessibleName: "每日推荐歌曲");
                UpdateStatusBar($"加载完成，共 {songs.Count} 首歌曲");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LoadDailyRecommendSongs] 异常: {ex}");
                throw;
            }
        }

        /// <summary>
        /// 加载每日推荐歌单
        /// </summary>
        private async Task LoadDailyRecommendPlaylists()
        {
            try
            {
                var playlists = await _apiClient.GetDailyRecommendPlaylistsAsync();
                if (playlists == null || playlists.Count == 0)
                {
                    MessageBox.Show("获取每日推荐歌单失败", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                DisplayPlaylists(
                    playlists,
                    viewSource: "daily_recommend_playlists",
                    accessibleName: "每日推荐歌单");
                UpdateStatusBar($"加载完成，共 {playlists.Count} 个歌单");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LoadDailyRecommendPlaylists] 异常: {ex}");
                throw;
            }
        }

        /// <summary>
        /// 加载推荐歌单
        /// </summary>
        private async Task LoadPersonalizedPlaylists()
        {
            try
            {
                var playlists = await _apiClient.GetPersonalizedPlaylistsAsync(30);
                if (playlists == null || playlists.Count == 0)
                {
                    MessageBox.Show("获取推荐歌单失败", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                DisplayPlaylists(
                    playlists,
                    viewSource: "personalized_playlists",
                    accessibleName: "推荐歌单");
                UpdateStatusBar($"加载完成，共 {playlists.Count} 个歌单");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LoadPersonalizedPlaylists] 异常: {ex}");
                throw;
            }
        }

        /// <summary>
        /// 加载推荐新歌
        /// </summary>
        private async Task LoadPersonalizedNewSongs()
        {
            try
            {
                var songs = await _apiClient.GetPersonalizedNewSongsAsync(20);
                if (songs == null || songs.Count == 0)
                {
                    MessageBox.Show("获取推荐新歌失败", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                DisplaySongs(
                    songs,
                    viewSource: "personalized_newsongs",
                    accessibleName: "推荐新歌");
                UpdateStatusBar($"加载完成，共 {songs.Count} 首歌曲");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LoadPersonalizedNewSongs] 异常: {ex}");
                throw;
            }
        }

        /// <summary>
        /// 显示歌曲列表
        /// </summary>
        /// <param name="startIndex">起始序号（默认为1，分页时应传入正确的起始序号）</param>
        private void DisplaySongs(
            List<SongInfo> songs,
            bool showPagination = false,
            bool hasNextPage = false,
            int startIndex = 1,
            bool preserveSelection = false,
            string? viewSource = null,
            string? accessibleName = null,
            bool skipAvailabilityCheck = false)
        {
            ConfigureListViewDefault();

            int previousSelectedIndex = -1;
            if (preserveSelection && resultListView.SelectedIndices.Count > 0)
            {
                previousSelectedIndex = resultListView.SelectedIndices[0];
            }

            // 清空所有列表（确保只有一种类型的数据）
            _currentSongs = songs ?? new List<SongInfo>();
            _currentPlaylists.Clear();
            _currentAlbums.Clear();
            _currentArtists.Clear();
            _currentListItems.Clear();

            resultListView.BeginUpdate();
            resultListView.Items.Clear();

            if (songs == null || songs.Count == 0)
            {
                resultListView.EndUpdate();
                SetViewContext(viewSource, accessibleName ?? "歌曲列表");
                return;
            }

            // 使用 startIndex 来支持分页序号连续累加
            int displayNumber = startIndex;  // 显示序号（从 startIndex 开始）
            int index = 0;  // 内部索引（从0开始，用于Tag）
            foreach (var song in songs)
            {
                var item = new ListViewItem(new[]
                {
                    displayNumber.ToString(),  // 使用连续的显示序号
                    song.Name ?? "未知",
                    song.Artist ?? "未知",
                    song.Album ?? "未知",
                    song.FormattedDuration
                });
                item.Tag = index;  // 使用索引作为 Tag
                resultListView.Items.Add(item);
                displayNumber++;  // 显示序号递增
                index++;  // 内部索引递增
            }

            if (showPagination)
            {
                if (_currentPage > 1)
                {
                    var prevItem = resultListView.Items.Add("上一页");
                    prevItem.Tag = -2;  // 特殊标记：上一页
                }

                if (hasNextPage)
                {
                    var nextItem = resultListView.Items.Add("下一页");
                    nextItem.Tag = -3;  // 特殊标记：下一页
                }
            }

            resultListView.EndUpdate();

            string defaultAccessibleName = accessibleName;
            if (string.IsNullOrWhiteSpace(defaultAccessibleName))
            {
                bool isSearchView = !string.IsNullOrEmpty(viewSource) &&
                                    viewSource.StartsWith("search:", StringComparison.OrdinalIgnoreCase);
                defaultAccessibleName = isSearchView ? "搜索结果" : "歌曲列表";
            }

            SetViewContext(viewSource, defaultAccessibleName);

            if (resultListView.Items.Count > 0)
            {
                int targetIndex = previousSelectedIndex >= 0
                    ? Math.Min(previousSelectedIndex, resultListView.Items.Count - 1)
                    : 0;

                RestoreListViewFocus(targetIndex);
            }

            if (!skipAvailabilityCheck)
            {
                ScheduleAvailabilityCheck(songs);
            }
        }

        /// <summary>
        /// 统一设置视图上下文（来源标识与无障碍名称）
        /// </summary>
        /// <param name="viewSource">视图来源标识（如 homepage、playlist:123）</param>
        /// <param name="accessibleName">无障碍描述文本</param>
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

            if (!string.IsNullOrWhiteSpace(accessibleName))
            {
                resultListView.AccessibleName = accessibleName;
            }
            else if (string.IsNullOrWhiteSpace(resultListView.AccessibleName))
            {
                resultListView.AccessibleName = "列表内容";
            }
        }

        /// <summary>
        /// 安排歌曲可用性检查任务（带取消与异常保护）
        /// </summary>
        private void ScheduleAvailabilityCheck(List<SongInfo> songs)
        {
            _availabilityCheckCts?.Cancel();
            _availabilityCheckCts?.Dispose();
            _availabilityCheckCts = null;

            if (songs == null || songs.Count == 0)
            {
                return;
            }

            var availabilityCts = new CancellationTokenSource();
            _availabilityCheckCts = availabilityCts;

            _ = BatchCheckSongsAvailabilityAsync(songs, availabilityCts.Token)
                .ContinueWith(task =>
                {
                    if (task.IsFaulted && task.Exception != null)
                    {
                        foreach (var ex in task.Exception.Flatten().InnerExceptions)
                        {
                            System.Diagnostics.Debug.WriteLine($"[StreamCheck] 可用性检查任务异常: {ex.Message}");
                        }
                    }
                }, TaskScheduler.Default);
        }

        /// <summary>
        /// 显示歌单列表
        /// </summary>
        private void DisplayPlaylists(
            List<PlaylistInfo> playlists,
            bool preserveSelection = false,
            string? viewSource = null,
            string? accessibleName = null)
        {
            ConfigureListViewDefault();

            int previousSelectedIndex = -1;
            if (preserveSelection && resultListView.SelectedIndices.Count > 0)
            {
                previousSelectedIndex = resultListView.SelectedIndices[0];
            }

            // 清空所有列表（确保只有一种类型的数据）
            _currentSongs.Clear();
            _currentPlaylists = playlists ?? new List<PlaylistInfo>();
            _currentAlbums.Clear();
            _currentArtists.Clear();
            _currentListItems.Clear();

            resultListView.BeginUpdate();
            resultListView.Items.Clear();

            if (playlists == null || playlists.Count == 0)
            {
                resultListView.EndUpdate();
                SetViewContext(viewSource, accessibleName ?? "歌单列表");
                return;
            }

            int index = 1;
            foreach (var playlist in playlists)
            {
                var item = new ListViewItem(new[]
                {
                    "",  // 歌单列表不显示索引号
                    playlist.Name ?? "未知",
                    "",  // 不显示创建者
                    playlist.TrackCount.ToString() + " 首",
                    playlist.Description ?? ""
                });
                item.Tag = playlist;
                resultListView.Items.Add(item);
                index++;
            }

            resultListView.EndUpdate();

            string defaultAccessibleName = accessibleName;
            if (string.IsNullOrWhiteSpace(defaultAccessibleName))
            {
                defaultAccessibleName = "歌单列表";
            }

            SetViewContext(viewSource, defaultAccessibleName);

            if (resultListView.Items.Count > 0)
            {
                int targetIndex = previousSelectedIndex >= 0
                    ? Math.Min(previousSelectedIndex, resultListView.Items.Count - 1)
                    : 0;

                RestoreListViewFocus(targetIndex);
            }
        }

        /// <summary>
        /// 显示统一的列表项（支持歌曲、歌单、专辑、分类混合显示）
        /// </summary>
        private void DisplayListItems(
            List<ListItemInfo> items,
            string? viewSource = null,
            string? accessibleName = null)
        {
            ConfigureListViewDefault();

            // 清空所有列表（确保只有一种类型的数据）
            _currentSongs.Clear();
            _currentPlaylists.Clear();
            _currentAlbums.Clear();
            _currentArtists.Clear();
            _currentListItems = items ?? new List<ListItemInfo>();

            resultListView.BeginUpdate();
            resultListView.Items.Clear();

            if (items == null || items.Count == 0)
            {
                resultListView.EndUpdate();
                SetViewContext(viewSource, accessibleName ?? "分类列表");
                return;
            }

            int index = 1;
            foreach (var listItem in items)
            {
                string title = listItem.Name ?? "未知";
                string creator = listItem.Creator ?? "";
                string extra = listItem.ExtraInfo ?? "";
                string description = listItem.Description ?? string.Empty;

                // 根据类型设置描述
                switch (listItem.Type)
                {
                    case ListItemType.Category:
                        description = listItem.CategoryDescription ?? "";
                        break;
                    case ListItemType.Playlist:
                        description = listItem.Playlist?.Description ?? "";
                        break;
                    case ListItemType.Album:
                        if (string.IsNullOrWhiteSpace(description))
                        {
                            description = extra;
                        }
                        break;
                    case ListItemType.Song:
                        description = string.IsNullOrWhiteSpace(description)
                            ? listItem.Song?.FormattedDuration ?? ""
                            : description;
                        break;
                    case ListItemType.Artist:
                        if (string.IsNullOrWhiteSpace(description) && listItem.Artist != null)
                        {
                            description = listItem.Artist.Description ?? listItem.Artist.BriefDesc;
                        }
                        break;
                }

                var item = new ListViewItem(new[]
                {
                    "",  // 主页分类列表不显示索引号
                    title,
                    creator,
                    extra,
                    description
                });
                item.Tag = listItem;
                resultListView.Items.Add(item);
                index++;
            }

            resultListView.EndUpdate();

            if (resultListView.Items.Count > 0)
            {
                resultListView.Items[0].Selected = true;
                resultListView.Items[0].Focused = true;
                resultListView.Focus();
            }

            string defaultAccessibleName = accessibleName;
            if (string.IsNullOrWhiteSpace(defaultAccessibleName))
            {
                defaultAccessibleName = "分类列表";
            }

            SetViewContext(viewSource, defaultAccessibleName);
        }

        /// <summary>
        /// 显示专辑列表
        /// </summary>
        private void DisplayAlbums(
            List<AlbumInfo> albums,
            bool preserveSelection = false,
            string? viewSource = null,
            string? accessibleName = null,
            int startIndex = 1,
            bool showPagination = false,
            bool hasNextPage = false)
        {
            ConfigureListViewDefault();

            int previousSelectedIndex = -1;
            if (preserveSelection && resultListView.SelectedIndices.Count > 0)
            {
                previousSelectedIndex = resultListView.SelectedIndices[0];
            }

            // 清空所有列表（确保只有一种类型的数据）
            _currentSongs.Clear();
            _currentPlaylists.Clear();
            _currentAlbums = albums ?? new List<AlbumInfo>();
            _currentArtists.Clear();
            _currentListItems.Clear();

            resultListView.BeginUpdate();
            resultListView.Items.Clear();

            if (albums == null || albums.Count == 0)
            {
                resultListView.EndUpdate();
                SetViewContext(viewSource, accessibleName ?? "专辑列表");
                return;
            }

            int displayNumber = startIndex;
            foreach (var album in albums)
            {
                var item = new ListViewItem(new[]
                {
                    displayNumber.ToString(),
                    album.Name ?? "未知",
                    "",  // 不显示艺术家
                    album.TrackCount.ToString() + " 首",
                    album.PublishTime ?? ""
                });
                item.Tag = album;
                resultListView.Items.Add(item);
                displayNumber++;
            }

            if (showPagination)
            {
                if (startIndex > 1)
                {
                    var prevItem = resultListView.Items.Add("上一页");
                    prevItem.Tag = -2;
                }

                if (hasNextPage)
                {
                    var nextItem = resultListView.Items.Add("下一页");
                    nextItem.Tag = -3;
                }
            }

            resultListView.EndUpdate();

            string defaultAccessibleName = accessibleName;
            if (string.IsNullOrWhiteSpace(defaultAccessibleName))
            {
                defaultAccessibleName = "专辑列表";
            }

            SetViewContext(viewSource, defaultAccessibleName);

            if (resultListView.Items.Count > 0)
            {
                int targetIndex = previousSelectedIndex >= 0
                    ? Math.Min(previousSelectedIndex, resultListView.Items.Count - 1)
                    : 0;

                RestoreListViewFocus(targetIndex);
            }
        }

        /// <summary>
        /// 列表项激活事件（双击或回车）
        /// </summary>
        private async void resultListView_ItemActivate(object sender, EventArgs e)
        {
            if (resultListView.SelectedItems.Count == 0) return;

            var item = resultListView.SelectedItems[0];

            // 检查是否是 ListItemInfo（新的统一列表项）
            if (item.Tag is ListItemInfo listItem)
            {
                await HandleListItemActivate(listItem);
                return;
            }

            // 检查Tag类型，支持播放歌曲或打开专辑/歌单
            if (item.Tag is PlaylistInfo playlist)
            {
                // 打开歌单
                await OpenPlaylist(playlist);
                return;
            }
            else if (item.Tag is AlbumInfo album)
            {
                // 打开专辑
                await OpenAlbum(album);
                return;
            }
            else if (item.Tag is ArtistInfo artist)
            {
                await OpenArtistAsync(artist);
                return;
            }

            // 处理歌曲播放或翻页
            int data = item.Tag is int ? (int)item.Tag : item.Index;

            // 处理翻页
            if (data == -2)  // 上一页
            {
                OnPrevPage();
                return;
            }
            else if (data == -3)  // 下一页
            {
                OnNextPage();
                return;
            }

            // 处理播放
            if (data >= 0 && data < _currentSongs.Count)
            {
                await PlaySong(_currentSongs[data]);
            }
        }

        /// <summary>
        /// 列表双击播放
        /// </summary>
        private async void resultListView_DoubleClick(object sender, EventArgs e)
        {
            if (resultListView.SelectedItems.Count == 0) return;

            var item = resultListView.SelectedItems[0];
            System.Diagnostics.Debug.WriteLine($"[MainForm] DoubleClick, Tag={item.Tag}, Type={item.Tag?.GetType().Name}");

            // 检查是否是 ListItemInfo
            if (item.Tag is ListItemInfo listItem)
            {
                await HandleListItemActivate(listItem);
                return;
            }

            // 检查是否是歌单或专辑
            if (item.Tag is PlaylistInfo playlist)
            {
                System.Diagnostics.Debug.WriteLine($"[MainForm] 双击打开歌单: {playlist.Name}");
                await OpenPlaylist(playlist);
                return;
            }
            else if (item.Tag is AlbumInfo album)
            {
                System.Diagnostics.Debug.WriteLine($"[MainForm] 双击打开专辑: {album.Name}");
                await OpenAlbum(album);
                return;
            }
            else if (item.Tag is ArtistInfo artist)
            {
                System.Diagnostics.Debug.WriteLine($"[MainForm] 双击打开歌手: {artist.Name}");
                await OpenArtistAsync(artist);
                return;
            }

            // Tag 存储的是索引
            if (item.Tag is int index && index >= 0 && index < _currentSongs.Count)
            {
                var song = _currentSongs[index];
                System.Diagnostics.Debug.WriteLine($"[MainForm] 双击播放歌曲: {song?.Name}");
                await PlaySong(song);
            }
            else if (item.Tag is SongInfo song)
            {
                // 兼容：如果 Tag 直接是 SongInfo
                System.Diagnostics.Debug.WriteLine($"[MainForm] 双击播放歌曲(直接Tag): {song?.Name}");
                await PlaySong(song);
            }
        }

        /// <summary>
        /// 上一页
        /// </summary>
        private async void OnPrevPage()
        {
            if (!string.IsNullOrEmpty(_currentViewSource) &&
                _currentViewSource.StartsWith("search:", StringComparison.OrdinalIgnoreCase))
            {
                if (_currentPage > 1)
                {
                    _currentPage--;
                    await PerformSearch();
                }
                else
                {
                    UpdateStatusBar("已经是第一页");
                }
                return;
            }

            if (!string.IsNullOrEmpty(_currentViewSource) &&
                _currentViewSource.StartsWith("artist_songs:", StringComparison.OrdinalIgnoreCase))
            {
                ParseArtistListViewSource(_currentViewSource, out var artistId, out var offset);
                if (offset <= 0)
                {
                    UpdateStatusBar("已经是第一页");
                    return;
                }

                int newOffset = Math.Max(0, offset - ArtistSongsPageSize);
                await LoadArtistSongsAsync(artistId, newOffset, skipSave: true);
                return;
            }

            if (!string.IsNullOrEmpty(_currentViewSource) &&
                _currentViewSource.StartsWith("artist_albums:", StringComparison.OrdinalIgnoreCase))
            {
                ParseArtistListViewSource(_currentViewSource, out var artistId, out var offset);
                if (offset <= 0)
                {
                    UpdateStatusBar("已经是第一页");
                    return;
                }

                int newOffset = Math.Max(0, offset - ArtistAlbumsPageSize);
                await LoadArtistAlbumsAsync(artistId, newOffset, skipSave: true);
                return;
            }

            if (!string.IsNullOrEmpty(_currentViewSource) &&
                _currentViewSource.StartsWith("artist_category_list:", StringComparison.OrdinalIgnoreCase))
            {
                ParseArtistCategoryListViewSource(_currentViewSource, out var typeCode, out var areaCode, out var offset);
                if (offset <= 0)
                {
                    UpdateStatusBar("已经是第一页");
                    return;
                }

                int newOffset = Math.Max(0, offset - ArtistSongsPageSize);
                await LoadArtistsByCategoryAsync(typeCode, areaCode, newOffset, skipSave: true);
                return;
            }

            if (string.Equals(_currentViewSource, "user_cloud", StringComparison.OrdinalIgnoreCase))
            {
                if (_cloudPage <= 1)
                {
                    UpdateStatusBar("已经是第一页");
                    return;
                }

                _cloudPage = Math.Max(1, _cloudPage - 1);
                await LoadCloudSongsAsync(skipSave: true, preserveSelection: false);
                return;
            }

            UpdateStatusBar("当前内容不支持翻页");
        }

        /// <summary>
        /// 下一页
        /// </summary>
        private async void OnNextPage()
        {
            if (!string.IsNullOrEmpty(_currentViewSource) &&
                _currentViewSource.StartsWith("search:", StringComparison.OrdinalIgnoreCase))
            {
                if (!_hasNextSearchPage && _currentPage >= _maxPage)
                {
                    UpdateStatusBar("已经是最后一页");
                    return;
                }

                _currentPage++;
                await PerformSearch();
                return;
            }

            if (!string.IsNullOrEmpty(_currentViewSource) &&
                _currentViewSource.StartsWith("artist_songs:", StringComparison.OrdinalIgnoreCase))
            {
                if (!_currentArtistSongsHasMore)
                {
                    UpdateStatusBar("已经是最后一页");
                    return;
                }

                ParseArtistListViewSource(_currentViewSource, out var artistId, out var offset);
                int newOffset = offset + ArtistSongsPageSize;
                await LoadArtistSongsAsync(artistId, newOffset, skipSave: true);
                return;
            }

            if (!string.IsNullOrEmpty(_currentViewSource) &&
                _currentViewSource.StartsWith("artist_albums:", StringComparison.OrdinalIgnoreCase))
            {
                if (!_currentArtistAlbumsHasMore)
                {
                    UpdateStatusBar("已经是最后一页");
                    return;
                }

                ParseArtistListViewSource(_currentViewSource, out var artistId, out var offset);
                int newOffset = offset + ArtistAlbumsPageSize;
                await LoadArtistAlbumsAsync(artistId, newOffset, skipSave: true);
                return;
            }

            if (!string.IsNullOrEmpty(_currentViewSource) &&
                _currentViewSource.StartsWith("artist_category_list:", StringComparison.OrdinalIgnoreCase))
            {
                if (!_currentArtistCategoryHasMore)
                {
                    UpdateStatusBar("已经是最后一页");
                    return;
                }

                ParseArtistCategoryListViewSource(_currentViewSource, out var typeCode, out var areaCode, out var offset);
                int newOffset = offset + ArtistSongsPageSize;
                await LoadArtistsByCategoryAsync(typeCode, areaCode, newOffset, skipSave: true);
                return;
            }

            if (string.Equals(_currentViewSource, "user_cloud", StringComparison.OrdinalIgnoreCase))
            {
                if (!_cloudHasMore)
                {
                    UpdateStatusBar("已经是最后一页");
                    return;
                }

                _cloudPage++;
                await LoadCloudSongsAsync(skipSave: true, preserveSelection: false);
                return;
            }

            UpdateStatusBar("当前内容不支持翻页");
        }

        #endregion

        #region 播放功能

        /// <summary>
        /// 播放歌曲（用户主动播放，执行队列判断逻辑）
        /// </summary>
        /// <summary>
        /// 直接播放歌曲（带取消支持和防抖，内部调用，不改变队列状态）
        /// </summary>
        /// <param name="isAutoPlayback">是否是自动播放（歌曲结束自动切歌），用于优化预加载缓存验证</param>
        /// <summary>
        /// 直接播放歌曲（内部调用，不改变队列状态）
        /// </summary>
        /// <param name="isAutoPlayback">是否是自动播放（歌曲结束自动切歌）</param>
        /// <summary>
        /// 加载歌词（新版本：使用增强的歌词系统）
        /// </summary>
        private async Task LoadLyrics(string songId, System.Threading.CancellationToken cancellationToken = default)
        {
            try
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                // ⭐ 使用新的歌词加载器
                var lyricsData = await _lyricsLoader.LoadLyricsAsync(songId, cancellationToken);

                if (cancellationToken.IsCancellationRequested)
                    return;

                // 加载到显示管理器
                _lyricsDisplayManager.LoadLyrics(lyricsData);

                // ⭐ 向后兼容：保持旧的 _currentLyrics 字段（用于旧代码）
                if (lyricsData != null && !lyricsData.IsEmpty)
                {
                    _currentLyrics = lyricsData.Lines.Select(line =>
                        new LyricLine(line.Time, line.Text)).ToList();
                }
                else
                {
                    _currentLyrics.Clear();
                }
            }
            catch (TaskCanceledException)
            {
                // 忽略取消异常
                _lyricsDisplayManager.Clear();
                _currentLyrics.Clear();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Lyrics] 加载失败: {ex.Message}");
                _lyricsDisplayManager.Clear();
                _currentLyrics.Clear();
            }
        }

/// <summary>
/// 同步播放/暂停按钮文本（防抖 + 延迟验证）
/// </summary>
private void SyncPlayPauseButtonText()
{
    // ⭐ 防抖：避免过于频繁的调用
    var now = DateTime.Now;
    if ((now - _lastSyncButtonTextTime).TotalMilliseconds < MIN_SYNC_BUTTON_INTERVAL_MS)
    {
        System.Diagnostics.Debug.WriteLine("[SyncPlayPauseButtonText] 调用过快，跳过");
        return;
    }
    _lastSyncButtonTextTime = now;

    if (this.InvokeRequired)
    {
        try
        {
            // ⭐ 使用 BeginInvoke（异步）避免死锁
            this.BeginInvoke(new Action(SyncPlayPauseButtonText));
        }
        catch (ObjectDisposedException)
        {
            // 窗体已释放，忽略
        }
        return;
    }

    if (_audioEngine == null || playPauseButton == null || playPauseButton.IsDisposed)
        return;

    var state = _audioEngine.GetPlaybackState();
    string expectedText = state == PlaybackState.Playing ? "暂停" : "播放";

    if (playPauseButton.Text != expectedText)
    {
        playPauseButton.Text = expectedText;
        System.Diagnostics.Debug.WriteLine($"[SyncPlayPauseButtonText] 按钮文本已更新: {expectedText} (状态={state})");
    }

    // ⭐ 同步更新托盘菜单的播放/暂停文本
    if (trayPlayPauseMenuItem != null && !trayPlayPauseMenuItem.IsDisposed)
    {
        string trayMenuText = state == PlaybackState.Playing ? "暂停(&P)" : "播放(&P)";
        if (trayPlayPauseMenuItem.Text != trayMenuText)
        {
            trayPlayPauseMenuItem.Text = trayMenuText;
            System.Diagnostics.Debug.WriteLine($"[SyncPlayPauseButtonText] 托盘菜单文本已更新: {trayMenuText}");
        }
    }
}

#pragma warning restore CS8600, CS8601, CS8602, CS8603, CS8604, CS8625
/// <summary>
/// 播放/暂停切换（异步版本，避免UI阻塞）
/// </summary>
private void TogglePlayPause()
{
    if (_audioEngine == null)
    {
        return;
    }

    var state = _audioEngine.GetPlaybackState();

    switch (state)
    {
        case PlaybackState.Playing:
            _audioEngine.Pause();
            break;
        case PlaybackState.Paused:
            _audioEngine.Resume();
            break;
    }
}

        /// <summary>
        /// 停止播放
        /// </summary>
        private void StopPlayback()
        {
            if (_audioEngine == null) return;
            _suppressAutoAdvance = true;
            _audioEngine.Stop();
            currentSongLabel.Text = "未播放";
            UpdateStatusBar("已停止");
            UpdatePlayButtonDescription(null);  // 清除描述
            SyncPlayPauseButtonText();
            UpdateTrayIconTooltip(null);
        }

        /// <summary>
        /// 播放/暂停按钮点击
        /// </summary>
        private void playPauseButton_Click(object sender, EventArgs e)
        {
            TogglePlayPause();
        }

        /// <summary>
        /// 上一首
        /// </summary>
        /// <param name="isManual">是否为手动切歌（F5/菜单），手动切歌时边界不循环</param>
        #endregion

        #region UI更新和事件

        /// <summary>
        /// 计算删除项后的目标索引（统一焦点管理逻辑）
        /// </summary>
        /// <param name="deletedIndex">被删除项的索引</param>
        /// <param name="newListCount">删除后列表的新长度</param>
        /// <returns>应该聚焦的目标索引，如果列表为空则返回-1</returns>
        private int CalculateTargetIndexAfterDeletion(int deletedIndex, int newListCount)
        {
            if (newListCount == 0)
                return -1;

            // 如果删除的是最后一项，目标索引为 deletedIndex - 1
            // 否则目标索引保持为 deletedIndex（因为后面的项会前移）
            int targetIndex = deletedIndex >= newListCount ? newListCount - 1 : deletedIndex;

            // 确保索引在有效范围内
            return Math.Max(0, Math.Min(targetIndex, newListCount - 1));
        }

        /// <summary>
        /// 恢复列表焦点到指定索引（统一焦点管理逻辑）
        /// </summary>
        /// <param name="targetIndex">目标索引，-1表示不设置焦点</param>
        private void RestoreListViewFocus(int targetIndex)
        {
            if (targetIndex < 0 || resultListView.Items.Count == 0)
                return;

            // 确保索引在有效范围内
            targetIndex = Math.Max(0, Math.Min(targetIndex, resultListView.Items.Count - 1));

            resultListView.Items[targetIndex].Selected = true;
            resultListView.Items[targetIndex].Focused = true;
            resultListView.Items[targetIndex].EnsureVisible();
            resultListView.Focus();
        }

/// <summary>
/// 列表选中项变化事件（用于保存用户手动选择的索引）
/// </summary>
private void resultListView_SelectedIndexChanged(object sender, EventArgs e)
{
    // 只在窗口可见时保存（避免恢复过程中的中间状态干扰）
    if (this.Visible && resultListView.SelectedItems.Count > 0)
    {
        int newIndex = resultListView.SelectedIndices[0];
        if (_lastListViewFocusedIndex != newIndex)
        {
            _lastListViewFocusedIndex = newIndex;
            System.Diagnostics.Debug.WriteLine($"[MainForm] 用户选择变化，保存索引={newIndex}");
        }

        if (string.Equals(_currentViewSource, "user_cloud", StringComparison.OrdinalIgnoreCase))
        {
            var song = GetSongFromListViewItem(resultListView.SelectedItems[0]);
            if (song != null && song.IsCloudSong && !string.IsNullOrEmpty(song.CloudSongId))
            {
                _lastSelectedCloudSongId = song.CloudSongId;
            }
        }
    }
}

/// <summary>
/// 使用反射调用控件的 AccessibilityNotifyClients 方法（protected 成员的外部调用）
/// </summary>
private void NotifyAccessibilityClients(System.Windows.Forms.Control control, System.Windows.Forms.AccessibleEvents accEvent, int childID)
{
    if (control == null) return;

    try
    {
        // 获取 Control 类的 AccessibilityNotifyClients 方法
        var method = typeof(System.Windows.Forms.Control).GetMethod(
            "AccessibilityNotifyClients",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null,
            new Type[] { typeof(System.Windows.Forms.AccessibleEvents), typeof(int) },
            null
        );

        if (method != null)
        {
            // 调用方法
            method.Invoke(control, new object[] { accEvent, childID });
            System.Diagnostics.Debug.WriteLine($"[AccessibilityHelper] 通知 {control.Name}: Event={accEvent}, ChildID={childID}");
        }
        else
        {
            System.Diagnostics.Debug.WriteLine("[AccessibilityHelper] 无法找到 AccessibilityNotifyClients 方法");
        }
    }
    catch (Exception ex)
    {
        System.Diagnostics.Debug.WriteLine($"[AccessibilityHelper] 反射调用失败: {ex.Message}");
    }
}

        /// <summary>
        /// 更新播放按钮的 AccessibleDescription（参考 Python 版本 12988行）
        /// </summary>
        private void UpdatePlayButtonDescription(SongInfo? song)
        {
            // ⭐ 线程安全检查：确保在 UI 线程上执行
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action<SongInfo?>(UpdatePlayButtonDescription), song);
                return;
            }

            if (song == null)
            {
                playPauseButton.AccessibleDescription = "播放/暂停";
                return;
            }

            // 构建描述文本：正在播放：歌曲名 - 艺术家 [专辑名] | X音质
            string songDisplayName = song.IsTrial ? $"{song.Name}(试听版)" : song.Name;
            string description = $"正在播放：{songDisplayName} - {song.Artist}";

            // 如果有专辑信息，添加专辑名
            if (!string.IsNullOrEmpty(song.Album))
            {
                description += $" [{song.Album}]";
            }

            // 添加实际播放的音质信息（参考 Python 版本 print(f"[PLAY] {name} - {artist_names} | {quality_name}")）
            if (!string.IsNullOrEmpty(song.Level))
            {
                string qualityName = NeteaseApiClient.GetQualityDisplayName(song.Level);
                description += $" | {qualityName}";
            }

            playPauseButton.AccessibleDescription = description;
            System.Diagnostics.Debug.WriteLine($"[MainForm] 更新播放按钮描述: {description}");
        }

        /// <summary>
        /// 定时器更新（重构版：检查 SeekManager 状态）
        /// </summary>
        private void UpdateTimer_Tick(object sender, EventArgs e)
        {
            if (_audioEngine == null || _isUserDragging) return;

            // ⭐ Plyr 优化：Seek 期间不更新进度条（防止抖动）
            if (_seekManager != null && _seekManager.IsSeeking) return;

            // ⭐ 使用缓存值，避免UI线程阻塞
            var position = GetCachedPosition(); // seconds
            var duration = GetCachedDuration(); // seconds

            if (duration > 0)
            {
                // 设置进度条最大值为歌曲总秒数（每秒一个刻度）
                int maxSeconds = (int)duration;
                if (progressTrackBar.Maximum != maxSeconds)
                {
                    progressTrackBar.Maximum = Math.Max(1, maxSeconds);
                    progressTrackBar.TickFrequency = Math.Max(1, maxSeconds / 20); // 约20个刻度线
                }

                // 设置当前值为播放秒数
                int currentSeconds = (int)position;
                if (currentSeconds >= 0 && currentSeconds <= progressTrackBar.Maximum)
                {
                    progressTrackBar.Value = currentSeconds;
                }

                string timeText = $"{FormatTimeFromSeconds(position)} / {FormatTimeFromSeconds(duration)}";
                timeLabel.Text = timeText;

                // 更新进度条的可访问性：直接显示时间
                progressTrackBar.AccessibleName = timeText;
            }
            else
            {
                // 无播放时重置
                progressTrackBar.Maximum = 1000;
                progressTrackBar.Value = 0;
                progressTrackBar.TickFrequency = 50;
                progressTrackBar.AccessibleName = "00:00 / 00:00";
            }

            // 更新歌词
            if (_currentLyrics != null && _currentLyrics.Count > 0)
            {
                var positionTimeSpan = TimeSpan.FromSeconds(position);
                var currentLyric = LyricsManager.GetCurrentLyric(_currentLyrics, positionTimeSpan);
                if (currentLyric != null)
                {
                    lyricsLabel.Text = currentLyric.Text;
                }
            }

            // ⭐ 使用缓存值，避免UI线程阻塞
            var currentState = GetCachedPlaybackState();
            string expectedButtonText = currentState == PlaybackState.Playing ? "暂停" : "播放";
    
            if (playPauseButton.Text != expectedButtonText)
            {
                playPauseButton.Text = expectedButtonText;
                System.Diagnostics.Debug.WriteLine($"[UpdateTimer_Tick] ⚠️ 检测到按钮文本不一致，已自动修正: {expectedButtonText} (状态={currentState})");
            }
        }

        /// <summary>
        /// 进度条鼠标按下
        /// </summary>
        private void progressTrackBar_MouseDown(object sender, MouseEventArgs e)
        {
            _isUserDragging = true;
            System.Diagnostics.Debug.WriteLine("[MainForm] 进度条拖动开始");
        }

        /// <summary>
        /// 进度条滚动事件（用户拖动时实时触发，50ms 执行一次）
        /// </summary>
        private void progressTrackBar_Scroll(object sender, EventArgs e)
        {
            // ⭐ 丢弃式 Seek：用户拖动进度条时实时调用 RequestSeek
            // SeekManager 以 50ms 间隔执行，新命令覆盖旧命令
            if (_audioEngine == null || _seekManager == null) return;

            var duration = GetCachedDuration();
            if (duration > 0)
            {
                double newPosition = progressTrackBar.Value;
                System.Diagnostics.Debug.WriteLine($"[MainForm] 进度条 Scroll: {newPosition:F1}s");
                _seekManager.RequestSeek(newPosition);
            }
        }

        /// <summary>
        /// 进度条鼠标抬起（完成 Seek 序列）
        /// </summary>
        private void progressTrackBar_MouseUp(object sender, MouseEventArgs e)
        {
            _isUserDragging = false;
            System.Diagnostics.Debug.WriteLine("[MainForm] 进度条拖动结束");

            // ⭐ 通知 SeekManager 拖动结束
            if (_seekManager != null)
            {
                _seekManager.FinishSeek();
            }
        }

        /// <summary>
        /// 调度 Seek 操作（重构版：使用 SeekManager）
        /// ⭐ Plyr 优化：所有 Seek 请求都通过 SeekManager，自动防抖和状态管理
        /// </summary>
        /// <param name="direction">方向（正数=快进，负数=快退）</param>
        /// <param name="enableScrubbing">是否启用音频预览（按住键盘时）</param>
        private void HandleDirectionalKeyDown(bool isRight)
        {
            // ⭐ 静默检查：如果在加载中、请求中或没有歌曲播放，直接返回
            if (_isPlaybackLoading)
            {
                System.Diagnostics.Debug.WriteLine($"[MainForm] {(isRight ? "右" : "左")}键快进快退被忽略：歌曲加载中");
                return;
            }

            if (_seekManager == null || _audioEngine == null)
            {
                System.Diagnostics.Debug.WriteLine($"[MainForm] {(isRight ? "右" : "左")}键快进快退被忽略：SeekManager或AudioEngine未初始化");
                return;
            }

            if (!_audioEngine.IsPlaying && !_audioEngine.IsPaused)
            {
                System.Diagnostics.Debug.WriteLine($"[MainForm] {(isRight ? "右" : "左")}键快进快退被忽略：没有正在播放的歌曲");
                return;
            }

            var now = DateTime.Now;

            if (isRight)
            {
                if (_rightKeyPressed)
                    return;

                _rightKeyPressed = true;
                _rightScrubActive = false;
                _rightKeyDownTime = now;
                ScheduleSeek(KEY_JUMP_STEP_SECONDS, enableScrubbing: false);
            }
            else
            {
                if (_leftKeyPressed)
                    return;

                _leftKeyPressed = true;
                _leftScrubActive = false;
                _leftKeyDownTime = now;
                ScheduleSeek(-KEY_JUMP_STEP_SECONDS, enableScrubbing: false);
            }

            StartScrubKeyTimer();
        }

        private void StartScrubKeyTimer()
        {
            if (_scrubKeyTimer == null)
                return;

            if (!_scrubKeyTimer.Enabled)
            {
                _scrubKeyTimer.Interval = KEY_SCRUB_INTERVAL_MS;
                _scrubKeyTimer.Start();
            }
        }

        private void StopScrubKeyTimerIfIdle()
        {
            if (_scrubKeyTimer == null)
                return;

            if (!_leftKeyPressed && !_rightKeyPressed && _scrubKeyTimer.Enabled)
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

            var now = DateTime.Now;

            if (_leftKeyPressed)
            {
                if (!_leftScrubActive)
                {
                    if ((now - _leftKeyDownTime).TotalMilliseconds >= KEY_SCRUB_TRIGGER_MS)
                    {
                        _leftScrubActive = true;
                        ScheduleSeek(-KEY_SCRUB_STEP_SECONDS, enableScrubbing: true);
                    }
                }
                else
                {
                    ScheduleSeek(-KEY_SCRUB_STEP_SECONDS, enableScrubbing: true);
                }
            }

            if (_rightKeyPressed)
            {
                if (!_rightScrubActive)
                {
                    if ((now - _rightKeyDownTime).TotalMilliseconds >= KEY_SCRUB_TRIGGER_MS)
                    {
                        _rightScrubActive = true;
                        ScheduleSeek(KEY_SCRUB_STEP_SECONDS, enableScrubbing: true);
                    }
                }
                else
                {
                    ScheduleSeek(KEY_SCRUB_STEP_SECONDS, enableScrubbing: true);
                }
            }
        }

        private void ScheduleSeek(double direction, bool enableScrubbing = false)
        {
            if (_seekManager == null || _audioEngine == null)
                return;

            // ⭐ 使用缓存值计算目标位置
            var currentPos = GetCachedPosition();
            var duration = GetCachedDuration();

            var targetPos = direction > 0
                ? Math.Min(duration, currentPos + Math.Abs(direction))
                : Math.Max(0, currentPos + direction);

            System.Diagnostics.Debug.WriteLine($"[MainForm] 请求 Seek: {currentPos:F1}s → {targetPos:F1}s (方向: {direction:+0;-0})");

            // 所有 Seek 请求都通过 SeekManager（自动防抖 + 缓存预加载）
            _seekManager.RequestSeek(targetPos);
        }

        /// <summary>
        /// ⭐ 旧的 ExecuteSeek 方法已废弃，所有 Seek 操作现在由 SeekManager 管理
        /// </summary>

        /// <summary>
        /// 异步执行 Seek 操作（进度条拖动使用）
        /// ⭐ 重构版：使用 SeekManager
        /// </summary>
        private void PerformSeek(double targetPosition)
        {
            if (_seekManager == null)
                return;

            System.Diagnostics.Debug.WriteLine($"[MainForm] 进度条拖动 Seek: {targetPosition:F1}s");
            _seekManager.RequestSeek(targetPosition);
        }

        /// <summary>
        /// SeekManager Seek 完成事件处理
        /// </summary>
        private void OnSeekCompleted(object sender, bool success)
        {
            System.Diagnostics.Debug.WriteLine($"[MainForm] Seek 序列完成，成功: {success}");

            // ⭐ Seek完成后更新进度条显示
            if (progressTrackBar != null && progressTrackBar.InvokeRequired)
            {
                progressTrackBar.BeginInvoke(new Action(() =>
                {
                    UpdateProgressTrackBarAccessibleName();
                }));
            }
            else if (progressTrackBar != null)
            {
                UpdateProgressTrackBarAccessibleName();
            }
        }

        /// <summary>
        /// ⭐⭐⭐ 缓冲状态变化事件处理
        /// </summary>
        private void OnBufferingStateChanged(object sender, BufferingState state)
        {
            System.Diagnostics.Debug.WriteLine($"[MainForm] 缓冲状态变化: {state}");

            // 在UI线程更新播放按钮文本
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => UpdatePlayButtonForBufferingState(state)));
            }
            else
            {
                UpdatePlayButtonForBufferingState(state);
            }
        }

        /// <summary>
        /// ⭐⭐⭐ 根据缓冲状态更新播放按钮
        /// </summary>
        private void UpdatePlayButtonForBufferingState(BufferingState state)
        {
            if (playPauseButton == null || playPauseButton.IsDisposed)
                return;

            switch (state)
            {
                case BufferingState.Buffering:
                    playPauseButton.Text = "缓冲中...";
                    playPauseButton.Enabled = true; // 允许取消
                    break;

                case BufferingState.Ready:
                    // 缓存就绪，即将开始播放
                    playPauseButton.Text = "就绪";
                    break;

                case BufferingState.Playing:
                    playPauseButton.Text = "暂停";
                    playPauseButton.Enabled = true;
                    break;

                case BufferingState.LowBuffer:
                    // 播放中但缓存不足，显示缓冲提示
                    playPauseButton.Text = "缓冲中...";
                    break;

                case BufferingState.Idle:
                default:
                    // 空闲状态，显示播放
                    if (_audioEngine != null && _audioEngine.IsPaused)
                    {
                        playPauseButton.Text = "播放";
                    }
                    break;
            }
        }

        /// <summary>
        /// ⭐ 更新进度条的AccessibleName（正常播放时显示）
        /// </summary>
        private void UpdateProgressTrackBarAccessibleName()
        {
            try
            {
                if (_audioEngine == null) return;

                double position = _audioEngine.GetPosition();
                double duration = _audioEngine.GetDuration();

                string posTime = FormatTime(TimeSpan.FromSeconds(position));
                string durTime = FormatTime(TimeSpan.FromSeconds(duration));

                progressTrackBar.AccessibleName = $"{posTime} / {durTime}";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainForm] UpdateProgressTrackBarAccessibleName 异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 音量改变
        /// </summary>
        private void volumeTrackBar_Scroll(object sender, EventArgs e)
        {
            if (_audioEngine == null) return;

            float volume = volumeTrackBar.Value / 100.0f;
            _audioEngine.SetVolume(volume);

            string volumeText = $"{volumeTrackBar.Value}%";
            volumeLabel.Text = volumeText;

            _config.Volume = volume;
            SaveConfig();
        }

        /// <summary>
        /// 音量滑块键盘事件 - 反转上下键方向
        /// </summary>
        private void volumeTrackBar_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Up)
            {
                // 上键增加音量
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
                // 下键减少音量
                e.Handled = true;
                e.SuppressKeyPress = true;
                if (volumeTrackBar.Value > 0)
                {
                    volumeTrackBar.Value = Math.Max(0, volumeTrackBar.Value - 2);
                    volumeTrackBar_Scroll(volumeTrackBar, EventArgs.Empty);
                }
            }
        }

        /// <summary>
        /// 进度条键盘事件 - 阻止方向键调整（保留 Tab 焦点用于可访问性）
        /// </summary>
        private void progressTrackBar_KeyDown(object sender, KeyEventArgs e)
        {
            // 阻止所有方向键，但保留控件在 Tab 序列中用于屏幕阅读器
            if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down ||
                e.KeyCode == Keys.Left || e.KeyCode == Keys.Right ||
                e.KeyCode == Keys.PageUp || e.KeyCode == Keys.PageDown ||
                e.KeyCode == Keys.Home || e.KeyCode == Keys.End)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        /// <summary>
        /// 音频播放进度变化事件（用于歌词同步）
        /// </summary>
        private void OnAudioPositionChanged(object? sender, TimeSpan position)
        {
            // 更新歌词显示（这是同步调用，由 BassAudioEngine 的位置监控线程调用）
            _lyricsDisplayManager?.UpdatePosition(position);
        }

        /// <summary>
        /// 歌词更新事件（在检测到歌词变化时触发）
        /// </summary>
        private void OnLyricUpdated(object? sender, LyricUpdateEventArgs e)
        {
            // 检查是否需要切换到 UI 线程
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(() => OnLyricUpdated(sender, e)));
                return;
            }

            try
            {
                // 格式化歌词文本
                string lyricText = _lyricsDisplayManager.GetFormattedLyricText(e.CurrentLine);

                // 更新 UI
                lyricsLabel.Text = lyricText;

                // ⭐ 自动朗读歌词（如果开启）
                if (_autoReadLyrics && e.IsNewLine && e.CurrentLine != null)
                {
                    // 只朗读原文歌词，不包括翻译和罗马音
                    string textToSpeak = e.CurrentLine.Text;

                    if (!string.IsNullOrWhiteSpace(textToSpeak))
                    {
                        // 在后台线程朗读，避免阻塞UI
                        System.Threading.Tasks.Task.Run(() =>
                        {
                            bool success = Utils.TtsHelper.SpeakText(textToSpeak);
                            System.Diagnostics.Debug.WriteLine($"[TTS] Speak '{textToSpeak}': {(success ? "成功" : "失败")}");
                        });
                    }
                }

                // ⭐ 更新无障碍支持（屏幕阅读器）
                if (e.IsNewLine && e.CurrentLine != null)
                {
                    // 为屏幕阅读器用户朗读新歌词
                    lyricsLabel.AccessibleName = $"当前歌词: {lyricText}";
                    System.Diagnostics.Debug.WriteLine($"[Lyrics] 歌词更新: {lyricText}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Lyrics] 更新UI失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 播放停止事件
        /// </summary>
        private void AudioEngine_PlaybackStopped(object sender, EventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("[MainForm] AudioEngine_PlaybackStopped 被调用");

            // 检查是否需要切换到 UI 线程
            if (this.InvokeRequired)
            {
                System.Diagnostics.Debug.WriteLine("[MainForm] 需要切换到 UI 线程");
                this.BeginInvoke(new Action(() => AudioEngine_PlaybackStopped(sender, e)));
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[MainForm] 当前播放模式: {_audioEngine?.PlayMode}");
            SyncPlayPauseButtonText();
            UpdateTrayIconTooltip(null);

            bool suppressAutoAdvance = _suppressAutoAdvance;
            if (suppressAutoAdvance)
            {
                System.Diagnostics.Debug.WriteLine("[MainForm] 自动跳转已被手动播放停止抑制");
                _suppressAutoAdvance = false;
                return;
            }

            // 注意：单曲循环现在由 BassAudioEngine 在播放层直接处理
            // 如果收到 PlaybackStopped 事件，说明不是单曲循环模式，或单曲循环失败（作为后备）

            // 单曲循环模式下的后备处理（通常不应该执行到这里）
            if (_audioEngine?.PlayMode == PlayMode.LoopOne)
            {
                var currentSong = _audioEngine.CurrentSong;
                System.Diagnostics.Debug.WriteLine($"[MainForm WARNING] 单曲循环后备处理被调用，歌曲: {currentSong?.Name}");
                if (currentSong != null)
                {
                    // 使用 PlaySongDirect 避免改变队列状态
                    PlaySongDirectAsync(currentSong);
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[MainForm ERROR] 单曲循环后备处理失败：CurrentSong 为 null");
                }
            }
            else if (!suppressAutoAdvance)
            {
                // 其他模式自动播放下一首（自动播放时传递 isManual = false）
                System.Diagnostics.Debug.WriteLine("[MainForm] 调用 PlayNext() (自动播放)");
                PlayNext(isManual: false);
            }
        }

        /// <summary>
        /// ⭐ 播放完成事件 - 只在无法无缝切换时触发
        /// </summary>
        private void AudioEngine_PlaybackEnded(object sender, SongInfo? e)
        {
            System.Diagnostics.Debug.WriteLine($"[MainForm] AudioEngine_PlaybackEnded 被调用，歌曲: {e?.Name}");

            // ⭐⭐⭐ 关键修复：恢复 BeginInvoke 异步非阻塞设计
            // BeginInvoke 不会阻塞 BASS 的事件回调线程，保持系统响应性
            // 虽然可能有轻微的 UI 更新延迟（<100ms），但不会阻塞音频引擎
            if (this.InvokeRequired)
            {
                try
                {
                    this.BeginInvoke(new Action(() => AudioEngine_PlaybackEnded(sender, e)));
                }
                catch (ObjectDisposedException)
                {
                    // 窗口已关闭，忽略
                    System.Diagnostics.Debug.WriteLine("[MainForm] 窗口已关闭，忽略 PlaybackEnded 事件");
                }
                catch (InvalidOperationException)
                {
                    // BeginInvoke 在窗口关闭时可能抛出此异常
                    System.Diagnostics.Debug.WriteLine("[MainForm] BeginInvoke 失败，窗口可能已关闭");
                }
                return;
            }

            var playMode = _audioEngine?.PlayMode ?? PlayMode.Loop;
            System.Diagnostics.Debug.WriteLine($"[MainForm] 播放模式: {playMode}");

            // 单曲循环模式：重新播放当前歌曲
            if (playMode == PlayMode.LoopOne && e != null)
            {
                System.Diagnostics.Debug.WriteLine("[MainForm] 单曲循环，重新播放当前歌曲");
                // ⭐ 正确的 async void 调用方式：通过 Task.Run 避免 fire-and-forget
                _ = PlaySongDirectWithCancellation(e, isAutoPlayback: true);
                return;
            }

            // 常规流程：播放下一首
            System.Diagnostics.Debug.WriteLine("[MainForm] 调用 PlayNext() (自动播放)");
            PlayNext(isManual: false);
        }

        private void AudioEngine_GaplessTransitionCompleted(object sender, GaplessTransitionEventArgs e)
        {
            if (IsDisposed)
            {
                return;
            }

            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke(new Action(() => AudioEngine_GaplessTransitionCompleted(sender, e)));
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (InvalidOperationException)
                {
                    return;
                }

                return;
            }

            if (e?.NextSong == null)
            {
                return;
            }

            var nextSong = e.NextSong;
            var playMode = _audioEngine?.PlayMode ?? PlayMode.Loop;

            // ⭐ 关键修复：捕获 AdvanceForPlayback 的返回值，用于焦点跟随
            var result = _playbackQueue.AdvanceForPlayback(nextSong, playMode, _currentViewSource);

            // ⭐⭐⭐ 修复：添加焦点跟随逻辑，使无缝切歌的行为与手动切歌保持一致
            switch (result.Route)
            {
                case PlaybackRoute.Queue:
                case PlaybackRoute.ReturnToQueue:
                    UpdateFocusForQueue(result.QueueIndex, nextSong);
                    System.Diagnostics.Debug.WriteLine($"[MainForm] 无缝切歌焦点跟随（队列）: 索引={result.QueueIndex}, 歌曲={nextSong.Name}");
                    break;

                case PlaybackRoute.Injection:
                case PlaybackRoute.PendingInjection:
                    UpdateFocusForInjection(nextSong, result.InjectionIndex);
                    System.Diagnostics.Debug.WriteLine($"[MainForm] 无缝切歌焦点跟随（插播）: 索引={result.InjectionIndex}, 歌曲={nextSong.Name}");
                    break;

                default:
                    System.Diagnostics.Debug.WriteLine($"[MainForm] 无缝切歌：未匹配焦点跟随路由，Route={result.Route}");
                    break;
            }

            string statusText = nextSong.IsTrial ? $"正在播放: {nextSong.Name} [试听版]" : $"正在播放: {nextSong.Name}";
            UpdateStatusBar(statusText);

            SafeInvoke(() =>
            {
                string songDisplayName = nextSong.IsTrial ? $"{nextSong.Name}(试听版)" : nextSong.Name;
                currentSongLabel.Text = $"{songDisplayName} - {nextSong.Artist}";
                playPauseButton.Text = "暂停";
                UpdatePlayButtonDescription(nextSong);
                UpdateTrayIconTooltip(nextSong);
                SyncPlayPauseButtonText();
            });

            _lyricsDisplayManager?.Clear();
            _currentLyrics?.Clear();
            _ = LoadLyrics(nextSong.Id);

            SafeInvoke(() => RefreshNextSongPreload());
        }

        // ⭐ AudioEngine_PlaybackAutoSwitched 方法已删除（预加载机制已移除）

        /// <summary>
        /// 异步直接播放歌曲（用于单曲循环等事件处理，不改变队列）
        /// </summary>
        private async void PlaySongDirectAsync(SongInfo song)
        {
            if (song == null)
            {
                throw new ArgumentNullException(nameof(song));
            }

            try
            {
                System.Diagnostics.Debug.WriteLine($"[MainForm] PlaySongDirectAsync 开始播放: {song.Name}");
                await PlaySongDirectWithCancellation(song);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainForm ERROR] PlaySongDirectAsync 异常: {ex.Message}");
                UpdateStatusBar($"播放失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 清除所有歌曲的URL缓存（用于音质切换）
        /// </summary>
        private void ClearAllSongUrlCache()
        {
            int clearedCount = 0;

            try
            {
                // 清除播放队列中的所有歌曲URL缓存
                var queueSongs = _playbackQueue?.CurrentQueue;
                if (queueSongs != null)
                {
                    foreach (var song in queueSongs)
                    {
                        if (song != null && !string.IsNullOrEmpty(song.Url))
                        {
                            song.Url = string.Empty;
                            song.Level = string.Empty;
                            song.Size = 0;
                            song.IsAvailable = null; // 重置可用性状态，以便重新检查
                            clearedCount++;
                        }
                    }
                }

                // 清除插播队列中的所有歌曲URL缓存
                var injectionSongs = _playbackQueue?.InjectionChain;
                if (injectionSongs != null)
                {
                    foreach (var song in injectionSongs)
                    {
                        if (song != null && !string.IsNullOrEmpty(song.Url))
                        {
                            song.Url = string.Empty;
                            song.Level = string.Empty;
                            song.Size = 0;
                            song.IsAvailable = null;
                            clearedCount++;
                        }
                    }
                }

                System.Diagnostics.Debug.WriteLine($"[Quality] 已清除 {clearedCount} 首歌曲的URL缓存");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Quality] 清除URL缓存时出错: {ex.Message}");
            }
        }

        private void RefreshNextSongPreload()
        {
            try
            {
                // ⭐ 修复：不再无条件调用 Clear()，因为：
                // 1. 调用方（如 qualityMenuItem_Click）已经在需要时调用了 Clear()
                // 2. StartPreloadAsync 内部已有音质一致性检查，会自动处理音质不匹配的情况
                // 3. 无条件 Clear() 会取消正在进行的关键下载（如当前歌曲的尾部 chunk），
                //    导致 PlaybackEnded 事件无法触发，自动切歌失效

                string defaultQualityName = _config?.DefaultQuality ?? "超清母带";
                QualityLevel quality = NeteaseApiClient.GetQualityLevelFromName(defaultQualityName);

                // 🎯 使用新的递归预加载方法，自动跳过不可用歌曲
                _ = RecursivePreloadNextAvailableAsync(quality, maxAttempts: 10);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainForm] 预加载失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 递归查找并预加载下一首可用的歌曲
        /// </summary>
        /// <param name="quality">音质等级</param>
        /// <param name="maxAttempts">最大尝试次数</param>
        private async Task<bool> RecursivePreloadNextAvailableAsync(QualityLevel quality, int maxAttempts = 10)
        {
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                // 预测下一首（会自动跳过 IsAvailable == false 的歌曲）
                var nextSong = PredictNextSong();
                if (nextSong == null)
                {
                    System.Diagnostics.Debug.WriteLine($"[MainForm] 🔍 预加载：无可用的下一首（尝试 {attempt + 1}/{maxAttempts}）");
                    return false;
                }

                System.Diagnostics.Debug.WriteLine($"[MainForm] 🔍 预加载尝试 {attempt + 1}：{nextSong.Name}, IsAvailable={nextSong.IsAvailable}");

                // 如果 IsAvailable 为 null，先检查有效性
                if (nextSong.IsAvailable == null)
                {
                    System.Diagnostics.Debug.WriteLine($"[MainForm] 🔍 歌曲未检查过（IsAvailable=null），执行有效性检查: {nextSong.Name}");

                    try
                    {
                        var urlResult = await _apiClient.GetSongUrlAsync(
                            new[] { nextSong.Id },
                            quality,
                            skipAvailabilityCheck: false).ConfigureAwait(false);  // ⚡ IsAvailable 为 null，必须检查

                        // 检查 URL 是否有效
                        if (urlResult != null &&
                            urlResult.TryGetValue(nextSong.Id, out var songUrl) &&
                            songUrl is { Url: { Length: > 0 } resolvedUrl })
                        {
                            // ⭐ 设置试听信息
                            var trialInfo = songUrl.FreeTrialInfo;
                            bool isTrial = trialInfo != null;
                            long trialStart = trialInfo?.Start ?? 0;
                            long trialEnd = trialInfo?.End ?? 0;

                            if (isTrial)
                            {
                                System.Diagnostics.Debug.WriteLine($"[MainForm] 🎵 试听版本（预加载检查）: {nextSong.Name}, 片段: {trialStart/1000}s - {trialEnd/1000}s");
                            }

                            // 歌曲可用，缓存 URL 信息
                            nextSong.IsAvailable = true;
                            nextSong.Url = resolvedUrl;
                            string resolvedLevel = songUrl.Level ?? quality.ToString().ToLowerInvariant();
                            nextSong.Level = resolvedLevel;
                            nextSong.Size = songUrl.Size;
                            nextSong.IsTrial = isTrial;
                            nextSong.TrialStart = trialStart;
                            nextSong.TrialEnd = trialEnd;

                            // ⭐⭐ 将获取的URL缓存到多音质字典中（确保多音质缓存完整性，包含试听信息）
                            string actualLevel = resolvedLevel.ToLowerInvariant();
                            nextSong.SetQualityUrl(actualLevel, resolvedUrl, songUrl.Size, true, isTrial, trialStart, trialEnd);
                            System.Diagnostics.Debug.WriteLine($"[MainForm] ✓ 歌曲可用并已缓存: {nextSong.Name}, 音质: {actualLevel}, 试听: {isTrial}");
                        }
                        else
                        {
                            // 歌曲不可用
                            nextSong.IsAvailable = false;
                            System.Diagnostics.Debug.WriteLine($"[MainForm] ✗ 歌曲不可用: {nextSong.Name}，尝试下一首");
                            continue; // 继续查找下一首
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MainForm] 检查可用性异常: {nextSong.Name}, {ex.Message}");
                        nextSong.IsAvailable = false;
                        continue; // 继续查找下一首
                    }
                }

                // 如果 IsAvailable == false，跳过并继续查找
                if (nextSong.IsAvailable == false)
                {
                    System.Diagnostics.Debug.WriteLine($"[MainForm] ⏭️ 跳过不可用歌曲: {nextSong.Name}，继续查找");
                    continue;
                }

                // 找到可用歌曲，开始预加载
                var currentSong = _audioEngine?.CurrentSong;
                if (currentSong != null)
                {
                    _nextSongPreloader?.CleanupStaleData(currentSong.Id, nextSong.Id);
                }

                System.Diagnostics.Debug.WriteLine($"[MainForm] 🎯 开始预加载可用歌曲：{nextSong.Name}");

                if (_nextSongPreloader == null)
                {
                    System.Diagnostics.Debug.WriteLine("[MainForm] ⚠️ 预加载器未初始化");
                    return false;
                }

                bool success = await _nextSongPreloader.StartPreloadAsync(nextSong, quality);

                if (success)
                {
                    var gaplessData = _nextSongPreloader.TryGetPreloadedData(nextSong.Id);
                    if (gaplessData != null)
                    {
                        _audioEngine?.RegisterGaplessPreload(nextSong, gaplessData);
                    }

                    System.Diagnostics.Debug.WriteLine($"[MainForm] ✓✓✓ 预加载成功: {nextSong.Name}");
                    return true;
                }
                else
                {
                    // 🎯 预加载失败，但不标记为不可用（可能是临时失败：网络抖动、取消等）
                    // 只有 URL 获取失败时才会在 NextSongPreloader 中标记为不可用
                    System.Diagnostics.Debug.WriteLine($"[MainForm] ⚠️ 预加载失败: {nextSong.Name}，尝试下一首（不标记不可用，允许后续重试）");

                    // 如果歌曲已被标记为不可用（URL不存在），跳过
                    if (nextSong.IsAvailable == false)
                    {
                        continue;
                    }

                    // 其他失败（初始化失败、取消等）不标记，允许后续重试
                    continue;
                }
            }

            System.Diagnostics.Debug.WriteLine($"[MainForm] ❌ 尝试了 {maxAttempts} 次，未找到可用歌曲");
            return false;
        }

        /// <summary>
        /// 批量检查歌曲资源可用性（异步非阻塞）
        /// </summary>
        private async Task BatchCheckSongsAvailabilityAsync(List<SongInfo> songs, CancellationToken cancellationToken)
        {
            if (songs == null || songs.Count == 0)
            {
                return;
            }

            try
            {
                // 只检查还没有缓存结果的歌曲
                var uncheckedSongs = songs.Where(s => s.IsAvailable == null).ToList();
                if (uncheckedSongs.Count == 0)
                {
                    System.Diagnostics.Debug.WriteLine("[StreamCheck] 所有歌曲都已检查过，跳过");
                    return;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                System.Diagnostics.Debug.WriteLine($"[StreamCheck] 🚀 开始流式检查 {uncheckedSongs.Count} 首歌曲（实时填入）");

                // 获取用户选择的音质
                string defaultQualityName = _config.DefaultQuality ?? "超清母带";
                QualityLevel selectedQuality = NeteaseApiClient.GetQualityLevelFromName(defaultQualityName);

                // 提取歌曲ID
                var ids = uncheckedSongs.Select(s => s.Id).Where(id => !string.IsNullOrWhiteSpace(id)).ToArray();

                if (ids.Length == 0)
                {
                    return;
                }

                // 创建 ID -> SongInfo 的快速查找字典（线程安全）
                var songLookup = new System.Collections.Concurrent.ConcurrentDictionary<string, SongInfo>(
                    uncheckedSongs
                        .Where(s => !string.IsNullOrWhiteSpace(s.Id))
                        .ToDictionary(s => s.Id, s => s, StringComparer.Ordinal),
                    StringComparer.Ordinal);

                // 统计计数器（线程安全）
                int available = 0;
                int unavailable = 0;

                // 🚀 调用流式API，每检查完一首就立即填入
                await _apiClient.BatchCheckSongsAvailabilityStreamAsync(
                    ids,
                    selectedQuality,
                    onSongChecked: (songId, isAvailable) =>
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            return;
                        }

                        // ⚡ 实时回调：立即填入 IsAvailable
                        if (songLookup.TryGetValue(songId, out var song))
                        {
                            song.IsAvailable = isAvailable;

                            if (isAvailable)
                            {
                                Interlocked.Increment(ref available);
                            }
                            else
                            {
                                Interlocked.Increment(ref unavailable);
                                System.Diagnostics.Debug.WriteLine($"[StreamCheck] ⚠️ 标记不可用: {song.Name}");
                            }
                        }
                    },
                    cancellationToken).ConfigureAwait(false);

                if (!cancellationToken.IsCancellationRequested)
                {
                    System.Diagnostics.Debug.WriteLine($"[StreamCheck] 🎉 流式检查全部完成：{available} 首可用，{unavailable} 首不可用");
                }
            }
            catch (OperationCanceledException)
            {
                System.Diagnostics.Debug.WriteLine("[StreamCheck] 可用性检查任务已取消");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[StreamCheck] 流式检查失败: {ex.Message}");
                // 检查失败不影响正常使用，播放时会进行实时检查
            }
        }

        private void UpdateStatusBar(string? message)
        {
            if (message == null)
            {
                return;
            }

            if (statusStrip1.InvokeRequired)
            {
                statusStrip1.Invoke(new Action<string?>(UpdateStatusBar), message);
                return;
            }

            if (statusStrip1.Items.Count > 0)
            {
                ((ToolStripStatusLabel)statusStrip1.Items[0]).Text = message;
            }
        }

        /// <summary>
        /// 获取当前播放的歌曲来源ID（用于播放上报）
        /// 优先级：歌单ID > 歌曲的专辑ID
        /// </summary>
        /// <param name="song">要获取来源的歌曲</param>
        /// <returns>歌单ID或专辑ID</returns>
        private long? GetCurrentSourceId(SongInfo song)
        {
            try
            {
                // 优先级1：当前歌单ID（如果正在浏览歌单）
                if (_currentPlaylist != null && !string.IsNullOrEmpty(_currentPlaylist.Id))
                {
                    if (long.TryParse(_currentPlaylist.Id, out long playlistId))
                    {
                        System.Diagnostics.Debug.WriteLine($"[MainForm] GetCurrentSourceId: 使用歌单ID={playlistId}");
                        return playlistId;
                    }
                }

                // 优先级2：歌曲的专辑ID
                if (song != null && !string.IsNullOrEmpty(song.AlbumId))
                {
                    if (long.TryParse(song.AlbumId, out long albumId))
                    {
                        System.Diagnostics.Debug.WriteLine($"[MainForm] GetCurrentSourceId: 使用专辑ID={albumId} (歌曲: {song.Name})");
                        return albumId;
                    }
                }

                System.Diagnostics.Debug.WriteLine("[MainForm] GetCurrentSourceId: ⚠️ 无法获取有效的 sourceId（既无歌单也无专辑）");
                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainForm] GetCurrentSourceId 异常: {ex.Message}");
                return null;
            }
        }

        private void SetPlaybackLoadingState(bool isLoading, string? statusMessage = null)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<bool, string?>(SetPlaybackLoadingState), isLoading, statusMessage);
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

                    if (statusStrip1 != null &&
                        statusStrip1.Items.Count > 0 &&
                        statusStrip1.Items[0] is ToolStripStatusLabel statusLabel)
                    {
                        _statusTextBeforeLoading = statusLabel.Text;
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

        /// <summary>
        /// 格式化时间
        /// </summary>
        private string FormatTime(TimeSpan time)
        {
            return $"{(int)time.TotalMinutes:D2}:{time.Seconds:D2}";
        }

        /// <summary>
        /// 从秒数格式化时间
        /// </summary>
        private string FormatTimeFromSeconds(double seconds)
        {
            int minutes = (int)(seconds / 60);
            int secs = (int)(seconds % 60);
            return $"{minutes:D2}:{secs:D2}";
        }

[StructLayout(LayoutKind.Sequential)]
private struct RECT { public int left, top, right, bottom; }

[StructLayout(LayoutKind.Sequential)]
private struct COMBOBOXINFO
{
    public int cbSize;
    public RECT rcItem;
    public RECT rcButton;
    public int stateButton;
    public System.IntPtr hwndCombo;
    public System.IntPtr hwndItem;  // 编辑子控件句柄
    public System.IntPtr hwndList;
}

[DllImport("user32.dll")]
private static extern bool GetComboBoxInfo(System.IntPtr hwndCombo, ref COMBOBOXINFO info);

[DllImport("user32.dll")]
private static extern System.IntPtr SetFocus(System.IntPtr hWnd);

[System.Runtime.InteropServices.DllImport("user32.dll")]
private static extern bool SetForegroundWindow(System.IntPtr hWnd);

[System.Runtime.InteropServices.DllImport("user32.dll")]
private static extern bool ShowWindow(System.IntPtr hWnd, int nCmdShow);

private const int SW_RESTORE = 9;

// 将系统焦点切到 ComboBox 的编辑子控件（NVDA 需要它来即时读出变化）
private void FocusComboEditChild(System.Windows.Forms.ComboBox combo)
{
    if (combo == null || combo.IsDisposed) return;
    var info = new COMBOBOXINFO { cbSize = Marshal.SizeOf(typeof(COMBOBOXINFO)) };
    if (GetComboBoxInfo(combo.Handle, ref info) && info.hwndItem != System.IntPtr.Zero)
    {
        SetFocus(info.hwndItem);
    }
}

// 禁止在 DropDown 样式的编辑框里输入字符，让它行为上等同 DropDownList
private void searchTypeComboBox_KeyPress(object sender, System.Windows.Forms.KeyPressEventArgs e)
{
    e.Handled = true;
}

// 选中项变化时：更新可访问名称并主动通知辅助技术
private void searchTypeComboBox_SelectedIndexChanged(object sender, System.EventArgs e)
{
    string text = this.searchTypeComboBox.SelectedItem != null
        ? this.searchTypeComboBox.SelectedItem.ToString()
        : string.Empty;

    this.searchTypeComboBox.AccessibleName = string.IsNullOrEmpty(text)
        ? "类型"
        : "类型" + text;

    // 主动广播：名称/值/选中已变化（让不同读屏路径都能收到）
    this.AccessibilityNotifyClients(System.Windows.Forms.AccessibleEvents.NameChange, -1);
    this.AccessibilityNotifyClients(System.Windows.Forms.AccessibleEvents.ValueChange, -1);
    this.AccessibilityNotifyClients(System.Windows.Forms.AccessibleEvents.Selection, -1);
}

// 下拉收起时：把焦点切到编辑子控件，并广播焦点事件
private void searchTypeComboBox_DropDownClosed(object sender, System.EventArgs e)
{
    FocusComboEditChild(this.searchTypeComboBox);
    this.AccessibilityNotifyClients(System.Windows.Forms.AccessibleEvents.Focus, -1);
}

// 获得焦点时（比如按 Tab 聚焦到该控件）：也把焦点切到编辑子控件
private void searchTypeComboBox_Enter(object sender, System.EventArgs e)
{
    FocusComboEditChild(this.searchTypeComboBox);
    this.AccessibilityNotifyClients(System.Windows.Forms.AccessibleEvents.Focus, -1);
}

        #endregion

        #region 快捷键处理

        /// <summary>
        /// 窗体按键事件
        /// </summary>

private void MainForm_KeyDown(object sender, KeyEventArgs e)
{
    // 先拦截 Shift+Esc：隐藏到托盘（即使当前焦点在文本框/下拉框）
    if (e.KeyCode == Keys.Escape && e.Shift && !e.Control && !e.Alt)
    {
        e.Handled = true;
        e.SuppressKeyPress = true;
        hideMenuItem.PerformClick();
        return;
    }

    // Backspace: 浏览器式后退（仅当列表有焦点时）
    if (e.KeyCode == Keys.Back && resultListView.Focused)
    {
        e.Handled = true;
        e.SuppressKeyPress = true;

        // 🎯 异步后退（带防抖和并发保护）
        _ = GoBackAsync();
        return;
    }

    // ⭐ 如果焦点在文本框或搜索类型下拉框，只屏蔽方向键和空格
    if (searchTextBox.Focused || searchTypeComboBox.Focused)
    {
        // 屏蔽可能干扰文本输入的快捷键
        if (e.KeyCode == Keys.Space || 
            e.KeyCode == Keys.Left || 
            e.KeyCode == Keys.Right)
        {
            return;  // 让这些键保持默认行为（文本编辑）
        }
        // 其他快捷键（F5-F8 等）继续执行
    }

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
    else if (e.KeyCode == Keys.F5)
    {
        e.Handled = true;
        e.SuppressKeyPress = true;
        // 直接调用上一曲
        PlayPrevious(isManual: true);
    }
    else if (e.KeyCode == Keys.F6)
    {
        e.Handled = true;
        e.SuppressKeyPress = true;
        // 直接调用下一曲
        PlayNext(isManual: true);
    }
        else if (e.KeyCode == Keys.F7)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
        // 音量减
            if (volumeTrackBar.Value > 0)
            {
                volumeTrackBar.Value = Math.Max(0, volumeTrackBar.Value - 2);
                volumeTrackBar_Scroll(volumeTrackBar, EventArgs.Empty);
            }
        }
        else if (e.KeyCode == Keys.F8)
        {
        e.Handled = true;
        e.SuppressKeyPress = true;
        // 音量加
            if (volumeTrackBar.Value < 100)
            {
                volumeTrackBar.Value = Math.Min(100, volumeTrackBar.Value + 2);
                volumeTrackBar_Scroll(volumeTrackBar, EventArgs.Empty);
            }
        }
    else if (e.KeyCode == Keys.F11)
    {
        e.Handled = true;
        e.SuppressKeyPress = true;
        // 切换自动朗读歌词
        ToggleAutoReadLyrics();
    }
    else if (e.KeyCode == Keys.F12)
    {
        e.Handled = true;
        e.SuppressKeyPress = true;
        // 跳转到位置
        ShowJumpToPositionDialog();
    }
}

        #endregion

        #region 菜单事件

/// <summary>
/// 更新托盘图标的气球提示（显示当前播放信息）
/// </summary>
/// <param name="song">当前歌曲信息，null 表示未播放</param>
/// <param name="isPaused">是否处于暂停状态</param>
        private void UpdateTrayIconTooltip(SongInfo? song, bool isPaused = false)
        {
            if (_trayIcon == null) return;

            // ⭐ 线程安全检查：确保在 UI 线程上执行
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action<SongInfo?, bool>(UpdateTrayIconTooltip), song, isPaused);
                return;
            }

    if (song == null)
    {
        // ⭐ 未播放状态：仅显示程序名称
        _trayIcon.Text = "易听";
        System.Diagnostics.Debug.WriteLine("[MainForm] 托盘提示已重置为未播放状态");
        return;
    }

    // 构建与播放按钮 AccessibleDescription 完全一致的文本
    string prefix = isPaused ? "已暂停：" : "正在播放：";
    string tooltipText = $"{prefix}{song.Name} - {song.Artist}";

    // 添加试听标识
    if (song.IsTrial)
    {
        tooltipText += " [试听版]";
    }

    // 添加专辑信息
    if (!string.IsNullOrEmpty(song.Album))
    {
        tooltipText += $" [{song.Album}]";
    }

    // 添加音质信息
    if (!string.IsNullOrEmpty(song.Level))
    {
        string qualityName = NeteaseApiClient.GetQualityDisplayName(song.Level);
        tooltipText += $" | {qualityName}";
    }

    // NotifyIcon.Text 有 63 字符限制，需要截断
    if (tooltipText.Length > 63)
    {
        _trayIcon.Text = tooltipText.Substring(0, 60) + "...";
    }
    else
    {
        _trayIcon.Text = tooltipText;
    }

    System.Diagnostics.Debug.WriteLine($"[MainForm] 更新托盘提示: {_trayIcon.Text}");
}

/// <summary>
/// 显示托盘气球通知（播放状态变化时）
/// </summary>
private void ShowTrayBalloonTip(SongInfo song, string state = "正在播放")
{
    if (_trayIcon == null || song == null) return;

    // ⭐ 线程安全检查：确保在 UI 线程上执行
    if (this.InvokeRequired)
    {
        this.BeginInvoke(new Action<SongInfo, string>(ShowTrayBalloonTip), song, state);
        return;
    }

    try
    {
        string balloonTitle = "易听";
        string balloonText = $"{state}：{song.Name} - {song.Artist}";

        // 添加音质信息
        if (!string.IsNullOrEmpty(song.Level))
        {
            string qualityName = NeteaseApiClient.GetQualityDisplayName(song.Level);
            balloonText += $"\n音质：{qualityName}";
        }

        _trayIcon.BalloonTipTitle = balloonTitle;
        _trayIcon.BalloonTipText = balloonText;
        _trayIcon.ShowBalloonTip(3000);  // 显示3秒
    }
    catch (Exception ex)
    {
        System.Diagnostics.Debug.WriteLine($"[MainForm] 显示气球提示失败: {ex.Message}");
    }
}

/// <summary>
/// 从托盘恢复窗口（常驻模式，不隐藏图标）
/// </summary>
private void RestoreFromTray()
{
    try
    {
        // 1) 显示并恢复窗口
        if (!this.Visible)
        {
            this.Show();
        }
        if (this.WindowState == System.Windows.Forms.FormWindowState.Minimized)
        {
            this.WindowState = System.Windows.Forms.FormWindowState.Normal;
        }

        // 2) 将窗口带到前台
        ShowWindow(this.Handle, SW_RESTORE);
        this.BringToFront();
        this.Activate();
        SetForegroundWindow(this.Handle);

        // 3) 设置窗口内控件焦点并通知辅助技术
            this.BeginInvoke(new System.Action(() =>
            {
                System.Windows.Forms.Control? target = null;

            // 焦点优先级：结果列表 > 搜索框 > 播放/暂停按钮
            if (resultListView != null && resultListView.CanFocus)
            {
                target = resultListView;
                
                // 强制刷新选中状态
                if (resultListView.Items.Count > 0)
                {
                    // ⭐ 关键修复：优先使用保存的焦点索引
                    int targetIndex = _lastListViewFocusedIndex;
                    
                    // 验证索引有效性
                    if (targetIndex < 0 || targetIndex >= resultListView.Items.Count)
                    {
                        // 索引无效，尝试从当前选中项获取
                        if (resultListView.SelectedItems.Count > 0)
                        {
                            targetIndex = resultListView.SelectedIndices[0];
                            System.Diagnostics.Debug.WriteLine($"[RestoreFromTray] 使用当前选中索引={targetIndex}");
                        }
                        else
                        {
                            // 都无效，使用默认值 0
                            targetIndex = 0;
                            System.Diagnostics.Debug.WriteLine($"[RestoreFromTray] 使用默认索引=0");
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[RestoreFromTray] 使用保存的焦点索引={targetIndex}");
                    }
                    
                    // 清除所有选中状态（强制触发变化）
                    resultListView.SelectedItems.Clear();
                    
                    // 延迟一帧再重新选中，确保触发选中事件
                    this.BeginInvoke(new System.Action(() =>
                    {
                        if (targetIndex >= 0 && targetIndex < resultListView.Items.Count)
                        {
                            resultListView.Items[targetIndex].Selected = true;
                            resultListView.Items[targetIndex].Focused = true;
                            resultListView.EnsureVisible(targetIndex);
                            
                            System.Diagnostics.Debug.WriteLine($"[RestoreFromTray] 已重新选中索引={targetIndex}，项目文本={resultListView.Items[targetIndex].Text}");
                        }
                        
                        // 设置焦点到列表
                        resultListView.Focus();
                        
                        // 使用反射调用通知辅助技术
                        NotifyAccessibilityClients(resultListView, System.Windows.Forms.AccessibleEvents.Focus, 0);
                        NotifyAccessibilityClients(resultListView, System.Windows.Forms.AccessibleEvents.Selection, targetIndex);
                        NotifyAccessibilityClients(resultListView, System.Windows.Forms.AccessibleEvents.SelectionAdd, targetIndex);
                        
                        System.Diagnostics.Debug.WriteLine($"[RestoreFromTray] 列表焦点已设置，选中项索引={targetIndex}");
                    }));
                }
                else
                {
                    // 列表为空，直接聚焦列表容器
                    resultListView.Focus();
                    NotifyAccessibilityClients(resultListView, System.Windows.Forms.AccessibleEvents.Focus, -1);
                }
            }
            else if (searchTextBox != null && searchTextBox.CanFocus)
            {
                target = searchTextBox;
                searchTextBox.Focus();
                searchTextBox.Select(searchTextBox.TextLength, 0);
                
                NotifyAccessibilityClients(searchTextBox, System.Windows.Forms.AccessibleEvents.Focus, -1);
            }
            else if (playPauseButton != null && playPauseButton.CanFocus)
            {
                target = playPauseButton;
                playPauseButton.Focus();
                
                NotifyAccessibilityClients(playPauseButton, System.Windows.Forms.AccessibleEvents.Focus, -1);
            }

            // 最后通知窗体级别的焦点变化
            if (target != null)
            {
                this.AccessibilityNotifyClients(System.Windows.Forms.AccessibleEvents.Focus, -1);
                System.Diagnostics.Debug.WriteLine($"[RestoreFromTray] 焦点已设置到: {target.Name}");
            }
        }));
    }
    catch (Exception ex)
    {
        System.Diagnostics.Debug.WriteLine($"[RestoreFromTray] 异常: {ex.Message}");
    }
}

// 托盘"鼠标单击"(MouseClick) → 手动处理左键和右键
private void TrayIcon_MouseClick(object sender, System.Windows.Forms.MouseEventArgs e)
{
    if (e.Button == System.Windows.Forms.MouseButtons.Left)
    {
        // 左键：恢复窗口
        RestoreFromTray();
    }
    else if (e.Button == System.Windows.Forms.MouseButtons.Right)
    {
        // ⭐ 右键：使用自定义宿主窗口显示菜单（防止虚拟窗口问题）
        ShowTrayContextMenu(System.Windows.Forms.Cursor.Position);
    }
}

        /// <summary>
        /// 登录
        /// </summary>
        private void loginMenuItem_Click(object sender, EventArgs e)
        {
            try
            {
                // 检查是否已登录
                bool isLoggedIn = IsUserLoggedIn();
                if (isLoggedIn)
                {
                    // 已登录，打开用户信息对话框
                    using (var userInfoForm = new Forms.UserInfoForm(_apiClient, _configManager, () =>
                    {
                        System.Diagnostics.Debug.WriteLine($"[LoginMenuItem] 退出登录回调触发");

                        // 退出登录后的回调
                        ClearLoginState(true);
                        EnsureConfigInitialized();

                        // 确保在UI线程上更新
                        if (this.InvokeRequired)
                        {
                            this.Invoke(new Action(() =>
                            {
                                UpdateLoginMenuItemText();
                                RefreshQualityMenuAvailability(); // 刷新音质菜单可用性
                                UpdateStatusBar("已退出登录");

                                // 如果当前在主页，自动刷新主页列表以隐藏需要登录的内容
                                if (_isHomePage)
                                {
                                    System.Diagnostics.Debug.WriteLine($"[LoginMenuItem] 退出登录后当前在主页，刷新主页列表");
                                    // 异步刷新主页
                                    Task.Run(async () =>
                                    {
                                        try
                                        {
                                            await (Task)this.Invoke(new Func<Task>(() => LoadHomePageAsync()));
                                        }
                                        catch (Exception homeEx)
                                        {
                                            System.Diagnostics.Debug.WriteLine($"[LoginMenuItem] 退出登录后刷新主页失败: {homeEx.Message}");
                                        }
                                    });
                                }
                                else
                                {
                                    System.Diagnostics.Debug.WriteLine($"[LoginMenuItem] 退出登录后当前不在主页，跳过自动刷新");
                                }
                            }));
                        }
                        else
                        {
                            UpdateLoginMenuItemText();
                            RefreshQualityMenuAvailability(); // 刷新音质菜单可用性
                            UpdateStatusBar("已退出登录");

                            // 如果当前在主页，自动刷新主页列表以隐藏需要登录的内容
                            if (_isHomePage)
                            {
                                System.Diagnostics.Debug.WriteLine($"[LoginMenuItem] 退出登录后当前在主页，刷新主页列表");
                                // 异步刷新主页
                                Task.Run(async () =>
                                {
                                    try
                                    {
                                        await LoadHomePageAsync();
                                    }
                                    catch (Exception homeEx)
                                    {
                                        System.Diagnostics.Debug.WriteLine($"[LoginMenuItem] 退出登录后刷新主页失败: {homeEx.Message}");
                                    }
                                });
                            }
                            else
                            {
                                System.Diagnostics.Debug.WriteLine($"[LoginMenuItem] 退出登录后当前不在主页，跳过自动刷新");
                            }
                        }
                    }))
                    {
                        userInfoForm.ShowDialog(this);
                    }
                }
                else
                {
                    // 未登录，打开登录对话框
                    System.Diagnostics.Debug.WriteLine($"[LoginMenuItem] ========== 开始登录流程 ==========");

                    // ⭐ Layer 2 防护：检查 API 客户端是否可用
                    if (_apiClient == null)
                    {
                        System.Diagnostics.Debug.WriteLine("[LoginMenuItem] ⚠️ API客户端为null，尝试重新初始化");
                        try
                        {
                            _configManager = _configManager ?? ConfigManager.Instance;
                            _config = _config ?? _configManager.Load();
                            _apiClient = new NeteaseApiClient(_config);
                            _apiClient.UseSimplifiedApi = false;
                            System.Diagnostics.Debug.WriteLine("[LoginMenuItem] ✓ API客户端重新初始化成功");
                        }
                        catch (Exception initEx)
                        {
                            System.Diagnostics.Debug.WriteLine($"[LoginMenuItem] ✗ API客户端初始化失败: {initEx.Message}");
                            MessageBox.Show($"无法初始化登录功能：\n\n{initEx.Message}\n\n请尝试重新启动应用程序。",
                                "初始化错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return;
                        }
                    }

                    using (var loginForm = new Forms.LoginForm(_apiClient))
                    {
                        // 订阅登录成功事件
                        System.Diagnostics.Debug.WriteLine($"[LoginMenuItem] 订阅LoginSuccess事件");
                        loginForm.LoginSuccess += (s, args) =>
                        {
                            try
                            {
                                ApplyLoginState(args);
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[LoginMenuItem] 事件处理异常: {ex.Message}");
                                MessageBox.Show($"更新菜单失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            }
                        };

                        System.Diagnostics.Debug.WriteLine($"[LoginMenuItem] 调用loginForm.ShowDialog()...");
                        var dialogResult = loginForm.ShowDialog(this);
                        System.Diagnostics.Debug.WriteLine($"[LoginMenuItem] ShowDialog()返回，结果={dialogResult}");
                        System.Diagnostics.Debug.WriteLine($"[LoginMenuItem] ========== 登录流程结束 ==========");
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"登录失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// 更新登录菜单项文本
        /// </summary>
        private void UpdateLoginMenuItemText()
        {
            System.Diagnostics.Debug.WriteLine($"[UpdateLoginMenuItemText] 开始更新");
            bool loggedIn = IsUserLoggedIn();

            System.Diagnostics.Debug.WriteLine($"[UpdateLoginMenuItemText] UsePersonalCookie={_apiClient.UsePersonalCookie} (自动检测)");
            System.Diagnostics.Debug.WriteLine($"[UpdateLoginMenuItemText] IsLoggedIn={_accountState?.IsLoggedIn}");
            string? stateMusicU = _accountState?.MusicU;
            System.Diagnostics.Debug.WriteLine($"[UpdateLoginMenuItemText] MusicU={(string.IsNullOrEmpty(stateMusicU) ? "未设置" : "已设置")}");
            System.Diagnostics.Debug.WriteLine($"[UpdateLoginMenuItemText] Nickname={_accountState?.Nickname ?? "null"}");
            System.Diagnostics.Debug.WriteLine($"[UpdateLoginMenuItemText] AvatarUrl={_accountState?.AvatarUrl ?? "null"}");
            System.Diagnostics.Debug.WriteLine($"[UpdateLoginMenuItemText] VipType={_accountState?.VipType ?? 0}");

            if (loggedIn)
            {
                string? nickname = _accountState?.Nickname;
                string displayName = string.IsNullOrEmpty(nickname)
                    ? "用户信息"
                    : nickname!;

                System.Diagnostics.Debug.WriteLine($"[UpdateLoginMenuItemText] 设置菜单项为: {displayName}");

                loginMenuItem.Text = displayName;
                loginMenuItem.AccessibleName = displayName;
                loginMenuItem.AccessibleDescription = $"当前登录账号: {displayName}，详细信息";
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[UpdateLoginMenuItemText] 设置菜单项为: 登录");

                loginMenuItem.Text = "登录";
                loginMenuItem.AccessibleName = "登录";
                loginMenuItem.AccessibleDescription = "点击打开登录对话框";
            }
        }

        private static string GetVipDescription(int vipType)
        {
            switch (vipType)
            {
                case 11:
                    return "黑胶VIP";
                case 10:
                    return "豪华VIP";
                default:
                    return vipType > 0 ? "普通VIP" : "普通用户";
            }
        }

        private void ApplyLoginState(Forms.LoginSuccessEventArgs args)
        {
            if (args == null)
            {
                System.Diagnostics.Debug.WriteLine("[LoginMenuItem] LoginSuccess事件参数为空");
                return;
            }

            if (this.InvokeRequired)
            {
                this.Invoke(new Action(() => ApplyLoginState(args)));
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[LoginMenuItem] ********** LoginSuccess事件被触发 **********");
            System.Diagnostics.Debug.WriteLine($"[LoginMenuItem] 线程ID={System.Threading.Thread.CurrentThread.ManagedThreadId}");
            System.Diagnostics.Debug.WriteLine($"[LoginMenuItem] 事件参数:");
            System.Diagnostics.Debug.WriteLine($"[LoginMenuItem]   Nickname={args.Nickname}");
            System.Diagnostics.Debug.WriteLine($"[LoginMenuItem]   UserId={args.UserId}");
            System.Diagnostics.Debug.WriteLine($"[LoginMenuItem]   VipType={args.VipType}");
            System.Diagnostics.Debug.WriteLine($"[LoginMenuItem]   Cookie={(string.IsNullOrEmpty(args.Cookie) ? "未提供" : $"已提供({args.Cookie.Length}字符)")}");

            if (!string.IsNullOrEmpty(args.Cookie))
            {
                try
                {
                    _apiClient.SetCookieString(args.Cookie);
                    System.Diagnostics.Debug.WriteLine("[LoginMenuItem] 已从事件Cookie刷新API客户端状态");
                }
                catch (Exception cookieEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[LoginMenuItem] 设置Cookie失败: {cookieEx.Message}");
                }
            }

            System.Diagnostics.Debug.WriteLine($"[LoginMenuItem] 从_apiClient读取Cookie:");
            string? clientMusicU = _apiClient.MusicU;
            string musicUSummary = string.IsNullOrEmpty(clientMusicU)
                ? "未设置⚠️"
                : $"已设置({clientMusicU!.Length}字符)";
            System.Diagnostics.Debug.WriteLine($"[LoginMenuItem]   _apiClient.MusicU={musicUSummary}");
            System.Diagnostics.Debug.WriteLine($"[LoginMenuItem]   _apiClient.CsrfToken={_apiClient.CsrfToken ?? "未设置⚠️"}");

            SyncConfigFromApiClient(args, persist: true);

            long parsed;
            long? profileId = null;
            if (long.TryParse(args.UserId, out parsed))
            {
                profileId = parsed;
            }

            var profile = new UserAccountInfo
            {
                UserId = profileId ?? 0,
                Nickname = args.Nickname,
                AvatarUrl = args.AvatarUrl,
                VipType = args.VipType
            };

            _apiClient.ApplyLoginProfile(profile);
            ReloadAccountState(false);

            System.Diagnostics.Debug.WriteLine($"[LoginMenuItem] 账户状态已更新:");
            System.Diagnostics.Debug.WriteLine($"[LoginMenuItem]   _accountState.IsLoggedIn={_accountState?.IsLoggedIn}");
            string? accountMusicU = _accountState?.MusicU;
            string stateMusicUSummary = string.IsNullOrEmpty(accountMusicU)
                ? "未设置⚠️"
                : $"已设置({accountMusicU!.Substring(0, Math.Min(20, accountMusicU.Length))}...)";
            System.Diagnostics.Debug.WriteLine($"[LoginMenuItem]   _accountState.MusicU={stateMusicUSummary}");
            System.Diagnostics.Debug.WriteLine($"[LoginMenuItem]   _accountState.CsrfToken={_accountState?.CsrfToken ?? "未设置⚠️"}");
            System.Diagnostics.Debug.WriteLine($"[LoginMenuItem]   _accountState.Nickname={_accountState?.Nickname}");
            System.Diagnostics.Debug.WriteLine($"[LoginMenuItem]   _accountState.UserId={_accountState?.UserId}");
            System.Diagnostics.Debug.WriteLine($"[LoginMenuItem]   _accountState.AvatarUrl={_accountState?.AvatarUrl ?? "未设置⚠️"}");
            System.Diagnostics.Debug.WriteLine($"[LoginMenuItem]   _accountState.VipType={_accountState?.VipType}");
            System.Diagnostics.Debug.WriteLine($"[LoginMenuItem]   UsePersonalCookie(自动)={_apiClient.UsePersonalCookie}");

            UpdateStatusBar($"登录成功！欢迎 {args.Nickname} ({GetVipDescription(args.VipType)})");

            UpdateLoginMenuItemText();
            RefreshQualityMenuAvailability(); // 刷新音质菜单可用性
            menuStrip1.Invalidate();
            menuStrip1.Update();
            menuStrip1.Refresh();
            Application.DoEvents();
            System.Diagnostics.Debug.WriteLine($"[LoginMenuItem] 菜单已刷新");

            if (_apiClient.UsePersonalCookie)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await EnsureLoginProfileAsync();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[LoginMenuItem] 登录后同步资料失败: {ex.Message}");
                    }
                });
            }

            if (_isHomePage)
            {
                System.Diagnostics.Debug.WriteLine($"[LoginMenuItem] 当前在主页，刷新主页列表");
                Task.Run(async () =>
                {
                    try
                    {
                        if (this.InvokeRequired)
                        {
                            await (Task)this.Invoke(new Func<Task>(async () =>
                            {
                                await LoadHomePageAsync();
                            }));
                        }
                        else
                        {
                            await LoadHomePageAsync();
                        }
                    }
                    catch (Exception homeEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"[LoginMenuItem] 刷新主页失败: {homeEx.Message}");
                    }
                });
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[LoginMenuItem] 当前不在主页，跳过自动刷新");
            }
        }

        /// <summary>
        /// 主页
        /// </summary>
        private async void homeMenuItem_Click(object sender, EventArgs e)
        {
            await LoadHomePageAsync();
        }

        /// <summary>
        /// 退出
        /// </summary>
        private void exitMenuItem_Click(object sender, EventArgs e)
        {
            Application.Exit();
        }

/// <summary>
/// 文件 → 隐藏（Shift+Esc）
/// </summary>
private void hideMenuItem_Click(object sender, EventArgs e)
{
    try
    {
        // ⭐ 图标在构造函数中已初始化为常驻，这里无需操作
        
        // 显示气球提示，告诉用户如何恢复
        if (_trayIcon != null)
        {
            _trayIcon.BalloonTipTitle = "易听";
            _trayIcon.BalloonTipText = "窗口已隐藏，单击托盘图标可恢复";
            _trayIcon.ShowBalloonTip(2000);
        }

        // 隐藏窗口（同时从任务栏消失）
        this.Hide();
    }
    catch (Exception ex)
    {
        System.Diagnostics.Debug.WriteLine($"[hideMenuItem_Click] 异常: {ex.Message}");
    }
}

// 双击托盘图标：恢复窗口
private void TrayIcon_DoubleClick(object sender, EventArgs e)
{
    RestoreFromTray();
}

        #region 托盘菜单事件处理

        /// <summary>
        /// 托盘菜单 - 显示易听
        /// </summary>
        private void trayShowMenuItem_Click(object sender, EventArgs e)
        {
            RestoreFromTray();
        }

        /// <summary>
        /// 托盘菜单 - 播放/暂停
        /// </summary>
        private void trayPlayPauseMenuItem_Click(object sender, EventArgs e)
        {
            TogglePlayPause();
        }

        /// <summary>
        /// 托盘菜单 - 上一首
        /// </summary>
        private void trayPrevMenuItem_Click(object sender, EventArgs e)
        {
            PlayPrevious();
        }

        /// <summary>
        /// 托盘菜单 - 下一首
        /// </summary>
        private void trayNextMenuItem_Click(object sender, EventArgs e)
        {
            PlayNext();
        }

        /// <summary>
        /// 托盘菜单 - 退出
        /// </summary>
        private void trayExitMenuItem_Click(object sender, EventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("[trayExitMenuItem] 退出菜单项被点击");

            // ⭐ 关键：设置退出标志，防止 Closed 事件中的操作与退出冲突
            _isExitingFromTrayMenu = true;

            // ⭐ 延迟退出，避免在菜单事件处理过程中直接操作
            this.BeginInvoke(new Action(() =>
            {
                System.Diagnostics.Debug.WriteLine("[trayExitMenuItem] 延迟执行退出...");

                // ⭐⭐⭐ 修复：不使用 Application.Exit()，而是关闭主窗体
                // 对于单窗体应用，关闭主窗体会让 Application.Run() 自然结束
                // 这避免了 Application.Exit() 遍历窗体集合时可能发生的集合修改异常
                // 原因：OnFormClosing() 中会关闭 _contextMenuHost，导致 OpenForms 集合被修改
                this.Close();
            }));
        }

        /// <summary>
        /// 显示托盘上下文菜单（使用自定义宿主窗口）
        /// </summary>
        private void ShowTrayContextMenu(System.Drawing.Point position)
        {
            if (_contextMenuHost == null || trayContextMenu == null) return;

            try
            {
                System.Diagnostics.Debug.WriteLine($"[ShowTrayContextMenu] 在位置 ({position.X}, {position.Y}) 显示菜单");

                // ⭐ 先显示宿主窗口（不可见，但提供窗口句柄）
                _contextMenuHost.ShowHost();

                // ⭐ 使用宿主窗口来显示菜单
                trayContextMenu.Show(_contextMenuHost, new System.Drawing.Point(0, 0));

                // ⭐ 立即将菜单移动到正确位置
                trayContextMenu.Left = position.X;
                trayContextMenu.Top = position.Y;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ShowTrayContextMenu] 显示菜单失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 托盘菜单打开前事件 - 预处理
        /// </summary>
        private void TrayContextMenu_Opening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("[TrayContextMenu] 菜单正在打开...");
        }

        /// <summary>
        /// 托盘菜单已打开事件 - 设置焦点到第一个菜单项（关键！）
        /// </summary>
        private void TrayContextMenu_Opened(object sender, EventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("[TrayContextMenu] 菜单已打开，设置焦点...");

            // ⭐ 关键：手动设置焦点到第一个菜单项
            // 这确保屏幕阅读器用户可以立即导航菜单
            if (trayContextMenu.Items.Count > 0)
            {
                // 延迟设置焦点，确保菜单完全显示后再设置
                this.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        // 选中第一个菜单项
                        var firstItem = trayContextMenu.Items[0];
                        if (firstItem != null && firstItem.Available && firstItem.Enabled)
                        {
                            trayContextMenu.Select();  // 先选中菜单本身
                            firstItem.Select();        // 再选中第一个项目
                            System.Diagnostics.Debug.WriteLine($"[TrayContextMenu] 焦点已设置到: {firstItem.Text}");
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[TrayContextMenu] 设置焦点失败: {ex.Message}");
                    }
                }));
            }
        }

        /// <summary>
        /// 托盘菜单关闭事件 - 隐藏宿主窗口，确保焦点正确恢复
        /// </summary>
        private void TrayContextMenu_Closed(object sender, System.Windows.Forms.ToolStripDropDownClosedEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("[TrayContextMenu] 菜单已关闭");

            // ⭐⭐⭐ 关键：如果是从退出菜单触发的，跳过所有后续操作
            // 避免与 Application.Exit() 冲突导致 "Collection was modified" 异常
            if (_isExitingFromTrayMenu)
            {
                System.Diagnostics.Debug.WriteLine("[TrayContextMenu] 检测到退出操作，跳过 Closed 事件处理");
                return;
            }

            // ⭐ 关键：隐藏宿主窗口（而非销毁，可重用）
            if (_contextMenuHost != null)
            {
                try
                {
                    this.BeginInvoke(new Action(() =>
                    {
                        _contextMenuHost.HideHost();
                        System.Diagnostics.Debug.WriteLine("[TrayContextMenu] 宿主窗口已隐藏");
                    }));
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[TrayContextMenu] 隐藏宿主窗口失败: {ex.Message}");
                }
            }

            // ⭐ 如果主窗口可见，显式将焦点设置回主窗口
            if (this.Visible && !this.IsDisposed)
            {
                try
                {
                    this.BeginInvoke(new Action(() =>
                    {
                        if (this.CanFocus)
                        {
                            this.Focus();
                            System.Diagnostics.Debug.WriteLine("[TrayContextMenu] 焦点已恢复到主窗口");
                        }
                    }));
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[TrayContextMenu] 恢复焦点失败: {ex.Message}");
                }
            }
        }

        #endregion

        /// <summary>
        /// 播放/暂停菜单
        /// </summary>
        private void playPauseMenuItem_Click(object sender, EventArgs e)
        {
            TogglePlayPause();
        }

        /// <summary>
        /// 上一曲菜单
        /// </summary>
        private void prevMenuItem_Click(object sender, EventArgs e)
        {
            PlayPrevious();
        }

        /// <summary>
        /// 下一曲菜单
        /// </summary>
        private void nextMenuItem_Click(object sender, EventArgs e)
        {
            PlayNext();
        }

        /// <summary>
        /// 跳转到位置 - 菜单项点击处理
        /// </summary>
        private void jumpToPositionMenuItem_Click(object sender, EventArgs e)
        {
            ShowJumpToPositionDialog();
        }

        /// <summary>
        /// 显示跳转到位置对话框
        /// </summary>
        private void ShowJumpToPositionDialog()
        {
            // ⭐ 静默检查：如果在加载中、请求中或没有歌曲播放，直接返回
            if (_isPlaybackLoading)
            {
                System.Diagnostics.Debug.WriteLine("[MainForm] F12跳转被忽略：歌曲加载中");
                return;
            }

            if (_audioEngine == null || (!_audioEngine.IsPlaying && !_audioEngine.IsPaused))
            {
                System.Diagnostics.Debug.WriteLine("[MainForm] F12跳转被忽略：没有正在播放的歌曲");
                return;
            }

            try
            {
                // 获取当前位置和总时长
                double currentPosition = _audioEngine.GetPosition();
                double duration = _audioEngine.GetDuration();

                if (duration <= 0)
                {
                    System.Diagnostics.Debug.WriteLine("[MainForm] F12跳转被忽略：无法获取歌曲时长");
                    return;
                }

                // 显示对话框
                using (var dialog = new Forms.JumpToPositionDialog(currentPosition, duration))
                {
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                    {
                        double targetPosition = dialog.TargetPosition;

                        // 使用 SeekManager 执行跳转（如果可用）
                        if (_seekManager != null)
                        {
                            _seekManager.RequestSeek(targetPosition);
                        }
                        else
                        {
                            // 回退到直接设置位置
                            _audioEngine.SetPosition(targetPosition);
                        }

                        System.Diagnostics.Debug.WriteLine($"[MainForm] 跳转到位置: {targetPosition:F2} 秒");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainForm] 跳转对话框错误: {ex.Message}");
                MessageBox.Show(
                    $"跳转失败: {ex.Message}",
                    "错误",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// 顺序播放
        /// </summary>
        private void sequentialMenuItem_Click(object sender, EventArgs e)
        {
            if (_audioEngine != null)
            {
                _audioEngine.PlayMode = PlayMode.Sequential;
                _config.PlaybackOrder = "顺序播放";
                SaveConfig();
                UpdatePlaybackOrderMenuCheck();

                // ⭐ 播放模式改变后，刷新预加载（下一首预测可能改变）
                RefreshNextSongPreload();
            }
        }

        /// <summary>
        /// 列表循环
        /// </summary>
        private void loopMenuItem_Click(object sender, EventArgs e)
        {
            if (_audioEngine != null)
            {
                _audioEngine.PlayMode = PlayMode.Loop;
                _config.PlaybackOrder = "列表循环";
                SaveConfig();
                UpdatePlaybackOrderMenuCheck();

                // ⭐ 播放模式改变后，刷新预加载（下一首预测可能改变）
                RefreshNextSongPreload();
            }
        }

        /// <summary>
        /// 单曲循环
        /// </summary>
        private void loopOneMenuItem_Click(object sender, EventArgs e)
        {
            if (_audioEngine != null)
            {
                _audioEngine.PlayMode = PlayMode.LoopOne;
                _config.PlaybackOrder = "单曲循环";
                SaveConfig();
                UpdatePlaybackOrderMenuCheck();

                // ⭐ 播放模式改变后，刷新预加载（下一首预测可能改变）
                RefreshNextSongPreload();
            }
        }

        /// <summary>
        /// 随机播放
        /// </summary>
        private void randomMenuItem_Click(object sender, EventArgs e)
        {
            if (_audioEngine != null)
            {
                _audioEngine.PlayMode = PlayMode.Random;
                _config.PlaybackOrder = "随机播放";
                SaveConfig();
                UpdatePlaybackOrderMenuCheck();

                // ⭐ 播放模式改变后，刷新预加载（下一首预测可能改变）
                RefreshNextSongPreload();
            }
        }

        /// <summary>
        /// 更新播放次序菜单选中状态
        /// </summary>
        private void UpdatePlaybackOrderMenuCheck()
        {
            // 顺序播放
            bool isSequential = (_config.PlaybackOrder == "顺序播放");
            sequentialMenuItem.Checked = isSequential;
            sequentialMenuItem.AccessibleName = isSequential ? "顺序播放 已选中" : "顺序播放";

            // 列表循环
            bool isLoop = (_config.PlaybackOrder == "列表循环");
            loopMenuItem.Checked = isLoop;
            loopMenuItem.AccessibleName = isLoop ? "列表循环 已选中" : "列表循环";

            // 单曲循环
            bool isLoopOne = (_config.PlaybackOrder == "单曲循环");
            loopOneMenuItem.Checked = isLoopOne;
            loopOneMenuItem.AccessibleName = isLoopOne ? "单曲循环 已选中" : "单曲循环";

            // 随机播放
            bool isRandom = (_config.PlaybackOrder == "随机播放");
            randomMenuItem.Checked = isRandom;
            randomMenuItem.AccessibleName = isRandom ? "随机播放 已选中" : "随机播放";
        }

        /// <summary>
        /// 更新播放音质菜单选中状态（参考 Python 版本 OnSelectDefaultQuality，10368-10371行）
        /// </summary>
        private void UpdateQualityMenuCheck()
        {
            string currentQuality = _config.DefaultQuality;

            // 标准音质
            bool isStandard = (currentQuality == "标准音质");
            standardQualityMenuItem.Checked = isStandard;
            standardQualityMenuItem.AccessibleName = isStandard ? "标准音质 已选中" : "标准音质";

            // 极高音质
            bool isHigh = (currentQuality == "极高音质");
            highQualityMenuItem.Checked = isHigh;
            highQualityMenuItem.AccessibleName = isHigh ? "极高音质 已选中" : "极高音质";

            // 无损音质
            bool isLossless = (currentQuality == "无损音质");
            losslessQualityMenuItem.Checked = isLossless;
            losslessQualityMenuItem.AccessibleName = isLossless ? "无损音质 已选中" : "无损音质";

            // Hi-Res音质
            bool isHiRes = (currentQuality == "Hi-Res音质");
            hiresQualityMenuItem.Checked = isHiRes;
            hiresQualityMenuItem.AccessibleName = isHiRes ? "Hi-Res音质 已选中" : "Hi-Res音质";

            // 高清环绕声
            bool isSurroundHD = (currentQuality == "高清环绕声");
            surroundHDQualityMenuItem.Checked = isSurroundHD;
            surroundHDQualityMenuItem.AccessibleName = isSurroundHD ? "高清环绕声 已选中" : "高清环绕声";

            // 沉浸环绕声
            bool isDolby = (currentQuality == "沉浸环绕声");
            dolbyQualityMenuItem.Checked = isDolby;
            dolbyQualityMenuItem.AccessibleName = isDolby ? "沉浸环绕声 已选中" : "沉浸环绕声";

            // 超清母带
            bool isMaster = (currentQuality == "超清母带");
            masterQualityMenuItem.Checked = isMaster;
            masterQualityMenuItem.AccessibleName = isMaster ? "超清母带 已选中" : "超清母带";
        }

        /// <summary>
        /// 刷新音质菜单可用性（根据登录状态和VIP等级）
        /// </summary>
        private void RefreshQualityMenuAvailability()
        {
            bool isLoggedIn = IsUserLoggedIn();
            int vipType = _accountState?.VipType ?? 0;

            if (!isLoggedIn)
            {
                // 未登录用户：仅标准和极高可用
                standardQualityMenuItem.Enabled = true;
                highQualityMenuItem.Enabled = true;
                losslessQualityMenuItem.Enabled = false;
                hiresQualityMenuItem.Enabled = false;
                surroundHDQualityMenuItem.Enabled = false;
                dolbyQualityMenuItem.Enabled = false;
                masterQualityMenuItem.Enabled = false;

                System.Diagnostics.Debug.WriteLine("[QualityMenu] 未登录状态 - 仅标准和极高可用");
            }
            else if (vipType >= 11)
            {
                // SVIP用户：所有音质可用
                standardQualityMenuItem.Enabled = true;
                highQualityMenuItem.Enabled = true;
                losslessQualityMenuItem.Enabled = true;
                hiresQualityMenuItem.Enabled = true;
                surroundHDQualityMenuItem.Enabled = true;
                dolbyQualityMenuItem.Enabled = true;
                masterQualityMenuItem.Enabled = true;

                System.Diagnostics.Debug.WriteLine($"[QualityMenu] SVIP用户 (VipType={vipType}) - 所有音质可用");
            }
            else if (vipType >= 1)
            {
                // VIP用户：up to Hi-Res
                standardQualityMenuItem.Enabled = true;
                highQualityMenuItem.Enabled = true;
                losslessQualityMenuItem.Enabled = true;
                hiresQualityMenuItem.Enabled = true;
                surroundHDQualityMenuItem.Enabled = false;
                dolbyQualityMenuItem.Enabled = false;
                masterQualityMenuItem.Enabled = false;

                System.Diagnostics.Debug.WriteLine($"[QualityMenu] VIP用户 (VipType={vipType}) - up to Hi-Res可用");
            }
            else
            {
                // 普通登录用户：标准、极高、无损
                standardQualityMenuItem.Enabled = true;
                highQualityMenuItem.Enabled = true;
                losslessQualityMenuItem.Enabled = true;
                hiresQualityMenuItem.Enabled = false;
                surroundHDQualityMenuItem.Enabled = false;
                dolbyQualityMenuItem.Enabled = false;
                masterQualityMenuItem.Enabled = false;

                System.Diagnostics.Debug.WriteLine($"[QualityMenu] 普通用户 (VipType={vipType}) - 标准/极高/无损可用");
            }
        }

        /// <summary>
        /// 音质选择事件处理（参考 Python 版本 OnSelectDefaultQuality，10368-10371行）
        /// </summary>
        private void qualityMenuItem_Click(object sender, EventArgs e)
        {
            var menuItem = sender as ToolStripMenuItem;
            if (menuItem == null) return;

            string selectedQuality = menuItem.Text;

            // 检查是否真的发生了变化
            if (_config.DefaultQuality == selectedQuality)
            {
                return; // 没有变化，无需处理
            }

            string oldQuality = _config.DefaultQuality;
            _config.DefaultQuality = selectedQuality;
            SaveConfig();
            UpdateQualityMenuCheck();

            // ⭐ 不再清除URL缓存，因为现在使用多音质缓存系统，所有音质的URL都被保留
            // 这样切换音质时，已缓存的其他音质URL可以直接使用，加速播放启动

            // ⭐⭐ 修复：不在此处调用 Clear()，因为：
            // 1. StartPreloadAsync 内部会调用 CancelCurrentPreload()，已经足够
            // 2. 外部调用 Clear() 会导致取消操作与新的预加载操作产生竞态条件
            // 3. 可能影响到当前播放歌曲的资源管理
            // 因此，只需调用 RefreshNextSongPreload()，让预加载器自己处理音质切换

            // 重新触发预加载（如果正在播放）
            if (_audioEngine?.IsPlaying == true)
            {
                RefreshNextSongPreload();
            }

            UpdateStatusBar($"已切换到 {selectedQuality}");
            System.Diagnostics.Debug.WriteLine($"[Quality] 音质已从 {oldQuality} 切换到 {selectedQuality}，多音质缓存已保留，将重新预加载下一首");
        }

        /// <summary>
        /// 关于
        /// </summary>
        private void aboutMenuItem_Click(object sender, EventArgs e)
        {
            MessageBox.Show(
                "易听 WinForms 版\n\n" +
                "基于 .NET Framework 4.8\n" +
                "音频引擎: BASS 2.4\n\n" +
                "支持快捷键:\n" +
                "  空格 - 播放/暂停\n" +
                "  左右箭头 - 快退/快进5秒\n" +
                "  F5/F6 - 上一首/下一首\n" +
                "  F7/F8 - 音量减/加\n" +
                "  F11 - 切换歌词朗读\n" +
                "  F12 - 跳转到位置",
                "关于",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        /// <summary>
        /// 切换自动朗读歌词（菜单项点击事件）
        /// </summary>
        private void autoReadLyricsMenuItem_Click(object sender, EventArgs e)
        {
            ToggleAutoReadLyrics();
        }

        /// <summary>
        /// 切换自动朗读歌词
        /// </summary>
        private void ToggleAutoReadLyrics()
        {
            _autoReadLyrics = !_autoReadLyrics;

            // 更新菜单项状态
            try
            {
                autoReadLyricsMenuItem.Checked = _autoReadLyrics;
                autoReadLyricsMenuItem.Text = _autoReadLyrics ? "关闭歌词朗读\tF11" : "打开歌词朗读\tF11";
            }
            catch
            {
                // 忽略菜单更新错误
            }

            // 朗读状态提示
            string message = _autoReadLyrics
                ? "已开启歌词朗读"
                : "已关闭歌词朗读";

            Utils.TtsHelper.SpeakText(message);
            UpdateStatusBar(message);

            System.Diagnostics.Debug.WriteLine($"[TTS] 歌词朗读: {(_autoReadLyrics ? "开启" : "关闭")}");

            // 保存配置
            SaveConfig();
        }

        /// <summary>
        /// 插播
        /// </summary>
        private void insertPlayMenuItem_Click(object sender, EventArgs e)
        {
            if (resultListView.SelectedItems.Count == 0)
                return;

            var selectedItem = resultListView.SelectedItems[0];
            System.Diagnostics.Debug.WriteLine($"[MainForm] 插播菜单, Tag={selectedItem.Tag}");

            SongInfo? song = null;

            // Tag 存储的是索引
            if (selectedItem.Tag is int index && index >= 0 && index < _currentSongs.Count)
            {
                song = _currentSongs[index];
            }
            else if (selectedItem.Tag is SongInfo songInfo)
            {
                // 兼容：如果 Tag 直接是 SongInfo
                song = songInfo;
            }

            if (song != null)
            {
                _playbackQueue.SetPendingInjection(song, _currentViewSource);
                UpdateStatusBar($"已设置下一首插播：{song.Name} - {song.Artist}");
                System.Diagnostics.Debug.WriteLine($"[MainForm] 设置插播歌曲: {song.Name}");

                // ⭐ 插播设置后，立即刷新预加载（下一首已改变）
                RefreshNextSongPreload();
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[MainForm ERROR] 无法获取选中的歌曲信息");
            }
        }

        #endregion

        #region 专辑和歌单操作

        /// <summary>
        /// 打开歌单（参考 Python 版本 fetch_playlist，11881-11916行）
        /// </summary>
        private async Task OpenPlaylist(PlaylistInfo playlist, bool skipSave = false, bool preserveSelection = false)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[MainForm] 打开歌单: {playlist.Name} (ID={playlist.Id})");
                UpdateStatusBar($"正在加载歌单: {playlist.Name}...");

                // 保存当前状态到导航历史
                if (!skipSave)
                {
                    SaveNavigationState();
                }

                // 获取歌单内的所有歌曲
                var songs = await _apiClient.GetPlaylistSongsAsync(playlist.Id);

                System.Diagnostics.Debug.WriteLine($"[MainForm] 歌单加载完成，共{songs?.Count ?? 0}首歌曲");

                if (songs == null || songs.Count == 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[MainForm] 歌单为空或无权限访问");
                    MessageBox.Show($"歌单 {playlist.Name} 暂时访问不到（可能是私密或触发风控）", "提示",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    UpdateStatusBar("就绪");
                    return;
                }

                _currentPlaylist = playlist;  // 保存当前歌单信息

                DisplaySongs(
                    songs,
                    preserveSelection: preserveSelection,
                    viewSource: $"playlist:{playlist.Id}",
                    accessibleName: playlist.Name);

                UpdateStatusBar($"歌单: {playlist.Name}，共 {songs.Count} 首歌曲");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainForm] 打开歌单失败: {ex}");
                MessageBox.Show($"加载歌单失败: {ex.Message}", "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatusBar("加载歌单失败");
            }
        }

        /// <summary>
        /// 打开专辑（参考 Python 版本）
        /// </summary>
        private async Task OpenAlbum(AlbumInfo album, bool skipSave = false)
        {
            try
            {
                UpdateStatusBar($"正在加载专辑: {album.Name}...");

                string? albumId = album.Id;
                if (string.IsNullOrEmpty(albumId))
                {
                    MessageBox.Show("无法获取专辑标识，无法加载内容。", "错误",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    UpdateStatusBar("加载专辑失败");
                    return;
                }
                string albumIdValue = albumId!;

                // 保存当前状态到导航历史
                if (!skipSave)
                {
                    SaveNavigationState();
                }

                // 获取专辑内的所有歌曲
                var songs = await _apiClient.GetAlbumSongsAsync(albumIdValue);

                if (songs == null || songs.Count == 0)
                {
                    MessageBox.Show($"专辑 {album.Name} 没有歌曲", "提示",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                _currentPlaylist = null;  // 清空当前歌单（当前是专辑视图）

                DisplaySongs(
                    songs,
                    viewSource: $"album:{albumIdValue}",
                    accessibleName: album.Name);

                UpdateStatusBar($"专辑: {album.Name}，共 {songs.Count} 首歌曲");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainForm] 打开专辑失败: {ex}");
                MessageBox.Show($"加载专辑失败: {ex.Message}", "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatusBar("加载专辑失败");
            }
        }

        /// <summary>
        /// 通过ID加载歌单（用于后退恢复）
        /// </summary>
        private async Task LoadPlaylistById(string playlistId, bool skipSave = false)
        {
            try
            {
                UpdateStatusBar($"正在加载歌单...");

                if (!skipSave)
                {
                    SaveNavigationState();
                }

                // 获取歌单详情
                var playlistDetail = await _apiClient.GetPlaylistDetailAsync(playlistId);
                if (playlistDetail == null)
                {
                    MessageBox.Show("获取歌单信息失败", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                // 获取歌单内的歌曲
                var songs = await _apiClient.GetPlaylistSongsAsync(playlistId);
                if (songs == null || songs.Count == 0)
                {
                    MessageBox.Show($"歌单 {playlistDetail.Name} 没有歌曲", "提示",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                DisplaySongs(
                    songs,
                    viewSource: $"playlist:{playlistId}",
                    accessibleName: $"歌单: {playlistDetail.Name}");
                _isHomePage = false;
                UpdateStatusBar($"歌单 {playlistDetail.Name} 加载完成，共 {songs.Count} 首歌曲");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LoadPlaylistById] 异常: {ex}");
                MessageBox.Show($"加载歌单失败: {ex.Message}", "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatusBar("加载歌单失败");
            }
        }

        /// <summary>
        /// 通过ID加载专辑（用于后退恢复）
        /// </summary>
        private async Task LoadAlbumById(string albumId, bool skipSave = false)
        {
            try
            {
                UpdateStatusBar($"正在加载专辑...");

                if (!skipSave)
                {
                    SaveNavigationState();
                }

                // 获取专辑内的歌曲
                var songs = await _apiClient.GetAlbumSongsAsync(albumId);
                if (songs == null || songs.Count == 0)
                {
                    MessageBox.Show("专辑没有歌曲", "提示",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                DisplaySongs(
                    songs,
                    viewSource: $"album:{albumId}",
                    accessibleName: "专辑");
                _isHomePage = false;
                UpdateStatusBar($"专辑加载完成，共 {songs.Count} 首歌曲");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LoadAlbumById] 异常: {ex}");
                MessageBox.Show($"加载专辑失败: {ex.Message}", "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatusBar("加载专辑失败");
            }
        }

        /// <summary>
        /// 加载搜索结果（用于后退恢复）
        /// </summary>
        private async Task LoadSearchResults(string keyword, string searchType, int page, bool skipSave = false)
        {
            try
            {
                if (!skipSave)
                {
                    SaveNavigationState();
                }

                _lastKeyword = keyword;
                _currentPage = page;
                _isHomePage = false;

                if (!string.IsNullOrEmpty(searchType))
                {
                    int index = searchTypeComboBox.Items.IndexOf(searchType);
                    if (index >= 0 && searchTypeComboBox.SelectedIndex != index)
                    {
                        searchTypeComboBox.SelectedIndex = index;
                    }
                }

                UpdateStatusBar($"正在加载搜索结果: {keyword}...");

                if (searchType == "歌曲" || string.IsNullOrEmpty(searchType))
                {
                    int offset = (page - 1) * _resultsPerPage;
                    var songResult = await _apiClient.SearchSongsAsync(keyword, _resultsPerPage, offset);
                    _currentSongs = songResult?.Items ?? new List<SongInfo>();

                    int totalPages = 1;
                    if (songResult != null)
                    {
                        totalPages = Math.Max(1, (int)Math.Ceiling(songResult.TotalCount / (double)Math.Max(1, _resultsPerPage)));
                    }
                    _maxPage = totalPages;
                    _hasNextSearchPage = songResult?.HasMore ?? false;

                    int startIndex = (page - 1) * _resultsPerPage + 1;
                    string songsViewSource = $"search:{keyword}:page{page}";
                    DisplaySongs(
                        _currentSongs,
                        showPagination: true,
                        hasNextPage: _hasNextSearchPage,
                        startIndex: startIndex,
                        viewSource: songsViewSource,
                        accessibleName: $"搜索: {keyword}");
                    int totalCount = songResult?.TotalCount ?? _currentSongs.Count;
                    UpdateStatusBar($"第 {_currentPage}/{_maxPage} 页，本页 {_currentSongs.Count} 首 / 总 {totalCount} 首");
                }
                else if (searchType == "歌单")
                {
                    var playlistResult = await _apiClient.SearchPlaylistsAsync(keyword, 50);
                    _currentPlaylists = playlistResult?.Items ?? new List<PlaylistInfo>();
                    _hasNextSearchPage = false;

                    string playlistViewSource = $"search:playlist:{keyword}";
                    DisplayPlaylists(
                        _currentPlaylists,
                        viewSource: playlistViewSource,
                        accessibleName: $"搜索歌单: {keyword}");
                    int totalCount = playlistResult?.TotalCount ?? _currentPlaylists.Count;
                    UpdateStatusBar($"找到 {_currentPlaylists.Count} 个歌单（总计 {totalCount} 个）");
                }
                else if (searchType == "专辑")
                {
                    var albumResult = await _apiClient.SearchAlbumsAsync(keyword, 50);
                    _currentAlbums = albumResult?.Items ?? new List<AlbumInfo>();
                    _hasNextSearchPage = false;

                    string albumViewSource = $"search:album:{keyword}";
                    DisplayAlbums(
                        _currentAlbums,
                        viewSource: albumViewSource,
                        accessibleName: $"搜索专辑: {keyword}");
                    int totalCount = albumResult?.TotalCount ?? _currentAlbums.Count;
                    UpdateStatusBar($"找到 {_currentAlbums.Count} 个专辑（总计 {totalCount} 个）");
                }
                else if (searchType == "歌手")
                {
                    int offset = (page - 1) * _resultsPerPage;
                    var artistResult = await _apiClient.SearchArtistsAsync(keyword, _resultsPerPage, offset);
                    _currentArtists = artistResult?.Items ?? new List<ArtistInfo>();
                    _hasNextSearchPage = artistResult?.HasMore ?? false;
                    int totalCount = artistResult?.TotalCount ?? _currentArtists.Count;

                    string artistViewSource = $"search:artist:{keyword}:page{page}";
                    DisplayArtists(
                        _currentArtists,
                        showPagination: true,
                        hasNextPage: _hasNextSearchPage,
                        startIndex: offset + 1,
                        viewSource: artistViewSource,
                        accessibleName: $"搜索歌手: {keyword}");

                    int totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)Math.Max(1, _resultsPerPage)));
                    _maxPage = totalPages;
                    UpdateStatusBar($"第 {page}/{totalPages} 页，本页 {_currentArtists.Count} 位 / 总 {totalCount} 位");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LoadSearchResults] 异常: {ex}");
                MessageBox.Show($"加载搜索结果失败: {ex.Message}", "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatusBar("加载搜索结果失败");
            }
        }

        /// <summary>
        /// 保存当前导航状态到历史栈
        /// </summary>
        private void SaveNavigationState()
        {
            // 只有当当前有内容时才保存
            if (_currentSongs.Count == 0 && _currentPlaylists.Count == 0 &&
                _currentAlbums.Count == 0 && _currentListItems.Count == 0 &&
                _currentArtists.Count == 0)
            {
                return;
            }

            var state = CreateCurrentState();
            if (_navigationHistory.Count > 0)
            {
                var lastState = _navigationHistory.Peek();
                if (IsSameNavigationState(lastState, state))
                {
                    _navigationHistory.Pop();
                    _navigationHistory.Push(state);
                    System.Diagnostics.Debug.WriteLine($"[Navigation] 合并重复状态: {state.ViewName}, 类型={state.PageType}, 历史栈深度={_navigationHistory.Count}");
                    return;
                }
            }

            _navigationHistory.Push(state);
            System.Diagnostics.Debug.WriteLine($"[Navigation] 保存状态: {state.ViewName}, 类型={state.PageType}, 历史栈深度={_navigationHistory.Count}");
        }

        /// <summary>
        /// 创建当前页面的导航状态
        /// </summary>
        private NavigationHistoryItem CreateCurrentState()
        {
            var state = new NavigationHistoryItem
            {
                ViewSource = _currentViewSource,
                ViewName = resultListView.AccessibleName,
                SelectedIndex = resultListView.SelectedItems.Count > 0 ? resultListView.SelectedItems[0].Index : -1,
            };

            if (_isHomePage || string.Equals(_currentViewSource, "homepage", StringComparison.OrdinalIgnoreCase))
            {
                state.PageType = "homepage";
            }
            else if (_currentViewSource.StartsWith("playlist:", StringComparison.OrdinalIgnoreCase))
            {
                state.PageType = "playlist";
                state.PlaylistId = _currentViewSource.Substring("playlist:".Length);
            }
            else if (_currentViewSource.StartsWith("album:", StringComparison.OrdinalIgnoreCase))
            {
                state.PageType = "album";
                state.AlbumId = _currentViewSource.Substring("album:".Length);
            }
            else if (_currentViewSource.StartsWith("search:", StringComparison.OrdinalIgnoreCase))
            {
                state.PageType = "search";
                state.SearchKeyword = _lastKeyword;
                state.SearchType = GetSelectedSearchType();
                state.CurrentPage = _currentPage;
            }
            else if (_currentViewSource.StartsWith("artist_entries:", StringComparison.OrdinalIgnoreCase))
            {
                state.PageType = "artist_entries";
                state.ArtistId = ParseArtistIdFromViewSource(_currentViewSource, "artist_entries:");
                state.ArtistName = _currentArtist?.Name ?? _currentArtistDetail?.Name ?? string.Empty;
            }
            else if (_currentViewSource.StartsWith("artist_songs_top:", StringComparison.OrdinalIgnoreCase))
            {
                state.PageType = "artist_top";
                state.ArtistId = ParseArtistIdFromViewSource(_currentViewSource, "artist_songs_top:");
                state.ArtistName = _currentArtist?.Name ?? _currentArtistDetail?.Name ?? string.Empty;
            }
            else if (_currentViewSource.StartsWith("artist_songs:", StringComparison.OrdinalIgnoreCase))
            {
                state.PageType = "artist_songs";
                ParseArtistListViewSource(_currentViewSource, out var artistId, out var offset);
                state.ArtistId = artistId;
                state.ArtistOffset = offset;
                state.ArtistName = _currentArtist?.Name ?? _currentArtistDetail?.Name ?? string.Empty;
            }
            else if (_currentViewSource.StartsWith("artist_albums:", StringComparison.OrdinalIgnoreCase))
            {
                state.PageType = "artist_albums";
                ParseArtistListViewSource(_currentViewSource, out var artistId, out var offset);
                state.ArtistId = artistId;
                state.ArtistOffset = offset;
                state.ArtistName = _currentArtist?.Name ?? _currentArtistDetail?.Name ?? string.Empty;
            }
            else if (string.Equals(_currentViewSource, "artist_favorites", StringComparison.OrdinalIgnoreCase))
            {
                state.PageType = "artist_favorites";
            }
            else if (string.Equals(_currentViewSource, "artist_category_types", StringComparison.OrdinalIgnoreCase))
            {
                state.PageType = "artist_category_types";
            }
            else if (_currentViewSource.StartsWith("artist_category_type:", StringComparison.OrdinalIgnoreCase))
            {
                state.PageType = "artist_category_type";
                state.ArtistType = (int)ParseArtistIdFromViewSource(_currentViewSource, "artist_category_type:");
            }
            else if (_currentViewSource.StartsWith("artist_category_list:", StringComparison.OrdinalIgnoreCase))
            {
                state.PageType = "artist_category_list";
                ParseArtistCategoryListViewSource(_currentViewSource, out var typeCode, out var areaCode, out var offset);
                state.ArtistType = typeCode;
                state.ArtistArea = areaCode;
                state.ArtistOffset = offset;
            }
            else
            {
                state.PageType = "category";
                state.CategoryId = _currentViewSource;
            }

            return state;

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
                    return string.Equals(a.SearchKeyword, b.SearchKeyword, StringComparison.OrdinalIgnoreCase)
                           && string.Equals(a.SearchType, b.SearchType, StringComparison.OrdinalIgnoreCase)
                           && a.CurrentPage == b.CurrentPage;
                case "artist_entries":
                case "artist_top":
                    return a.ArtistId == b.ArtistId;
                case "artist_songs":
                case "artist_albums":
                    return a.ArtistId == b.ArtistId && a.ArtistOffset == b.ArtistOffset;
                case "artist_favorites":
                case "artist_category_types":
                    return true;
                case "artist_category_type":
                    return a.ArtistType == b.ArtistType;
                case "artist_category_list":
                    return a.ArtistType == b.ArtistType && a.ArtistArea == b.ArtistArea && a.ArtistOffset == b.ArtistOffset;
                default:
                    return string.Equals(a.ViewSource, b.ViewSource, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// 后退到上一个导航状态（带防抖和并发保护）
        /// </summary>
        private async Task GoBackAsync()
        {
            // 🎯 防抖检查：防止快速连续后退
            var now = DateTime.UtcNow;
            var elapsed = (now - _lastBackTime).TotalMilliseconds;
            if (elapsed < MIN_BACK_INTERVAL_MS)
            {
                System.Diagnostics.Debug.WriteLine($"[Navigation] 🛑 防抖拦截：距上次后退仅 {elapsed:F0}ms");
                return;
            }

            // 🎯 并发保护：防止多个后退操作同时执行
            if (_isNavigating)
            {
                System.Diagnostics.Debug.WriteLine("[Navigation] 🛑 并发拦截：已有导航操作正在执行");
                return;
            }

            try
            {
                _isNavigating = true;
                _lastBackTime = now;

                if (_navigationHistory.Count == 0)
                {
                    // Stack 为空，返回主页
                    System.Diagnostics.Debug.WriteLine("[Navigation] 导航历史为空，返回主页");
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

                // 弹出历史项（单线程操作，无需锁）
                var state = _navigationHistory.Pop();
                System.Diagnostics.Debug.WriteLine($"[Navigation] 后退到: {state.ViewName}, 类型={state.PageType}, 剩余历史={_navigationHistory.Count}");

                // 根据页面类型重新加载（不保存状态，避免重复）
                await RestoreNavigationState(state);
            }
            finally
            {
                _isNavigating = false;
            }
        }

        /// <summary>
        /// 恢复导航状态（重新加载页面）
        /// </summary>
        private async Task RestoreNavigationState(NavigationHistoryItem state)
        {
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

                case "search":
                    await LoadSearchResults(state.SearchKeyword, state.SearchType, state.CurrentPage, skipSave: true);
                    break;

                case "artist_entries":
                    if (state.ArtistId > 0)
                    {
                        var artistInfo = new ArtistInfo
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
                        await LoadArtistSongsAsync(state.ArtistId, state.ArtistOffset, skipSave: true);
                    }
                    break;

                case "artist_albums":
                    if (state.ArtistId > 0)
                    {
                        await LoadArtistAlbumsAsync(state.ArtistId, state.ArtistOffset, skipSave: true);
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

                default:
                    System.Diagnostics.Debug.WriteLine($"[Navigation] 未知的页面类型: {state.PageType}");
                    UpdateStatusBar("无法恢复页面");
                    return;
                }

                // 恢复焦点
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
                    var targetItem = resultListView.Items[resolvedIndex];
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

                UpdateStatusBar($"返回到: {state.ViewName}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Navigation] 恢复状态失败: {ex}");
                MessageBox.Show($"返回失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatusBar("返回失败");
            }
        }

        #endregion

        #region 上下文菜单

        /// <summary>
        /// 上下文菜单打开前动态调整菜单项可见性
        /// </summary>
        private void songContextMenu_Opening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // 默认隐藏所有收藏菜单项
            subscribePlaylistMenuItem.Visible = false;
            unsubscribePlaylistMenuItem.Visible = false;
            deletePlaylistMenuItem.Visible = false;
            subscribeAlbumMenuItem.Visible = false;
            unsubscribeAlbumMenuItem.Visible = false;
            likeSongMenuItem.Visible = false;
            unlikeSongMenuItem.Visible = false;
            addToPlaylistMenuItem.Visible = false;
            removeFromPlaylistMenuItem.Visible = false;
            insertPlayMenuItem.Visible = true;

            // 默认隐藏所有下载菜单项
            downloadSongMenuItem.Visible = false;
            downloadPlaylistMenuItem.Visible = false;
            downloadAlbumMenuItem.Visible = false;
            batchDownloadMenuItem.Visible = false;
            downloadCategoryMenuItem.Visible = false;
            batchDownloadPlaylistsMenuItem.Visible = false;
            cloudMenuSeparator.Visible = false;
            uploadToCloudMenuItem.Visible = false;
            deleteFromCloudMenuItem.Visible = false;
            toolStripSeparatorArtist.Visible = false;
            viewArtistDetailMenuItem.Visible = false;
            shareArtistMenuItem.Visible = false;
            subscribeArtistMenuItem.Visible = false;
            unsubscribeArtistMenuItem.Visible = false;
            toolStripSeparatorView.Visible = false;
            viewSongArtistMenuItem.Visible = false;
            viewSongArtistMenuItem.Tag = null;
            viewSongAlbumMenuItem.Visible = false;
            viewSongAlbumMenuItem.Tag = null;
            shareSongMenuItem.Visible = false;
            shareSongMenuItem.Tag = null;
            shareSongWebMenuItem.Tag = null;
            shareSongDirectMenuItem.Tag = null;
            sharePlaylistMenuItem.Visible = false;
            sharePlaylistMenuItem.Tag = null;
            sharePlaylistWebMenuItem.Tag = null;
            sharePlaylistDirectMenuItem.Tag = null;
            shareAlbumMenuItem.Visible = false;
            shareAlbumMenuItem.Tag = null;
            shareAlbumWebMenuItem.Tag = null;
            shareAlbumDirectMenuItem.Tag = null;

            // ⭐ 检查登录状态 - 未登录时收藏相关菜单项保持隐藏
            bool isLoggedIn = IsUserLoggedIn();
            if (!isLoggedIn)
            {
                System.Diagnostics.Debug.WriteLine("[ContextMenu] 用户未登录，所有收藏/取消收藏菜单项保持隐藏");
            }

            bool isCloudView = string.Equals(_currentViewSource, "user_cloud", StringComparison.OrdinalIgnoreCase);
            if (isCloudView)
            {
                uploadToCloudMenuItem.Visible = true;
                cloudMenuSeparator.Visible = true;
            }

            var selectedItem = resultListView.SelectedItems.Count > 0 ? resultListView.SelectedItems[0] : null;
            if (selectedItem == null)
            {
                return;
            }

            bool isMyPlaylistsView = string.Equals(_currentViewSource, "user_playlists", StringComparison.OrdinalIgnoreCase);
            bool isUserAlbumsView = string.Equals(_currentViewSource, "user_albums", StringComparison.OrdinalIgnoreCase);
            bool showViewSection = false;
            PlaylistInfo? playlistFromListItem = null;
            AlbumInfo? albumFromListItem = null;
            SongInfo? songFromListItem = null;

            // 根据Tag类型决定显示哪些菜单项
            if (selectedItem.Tag is ArtistInfo directArtist)
            {
                ConfigureArtistContextMenu(directArtist);
                return;
            }

            if (selectedItem.Tag is ListItemInfo listItem)
            {
                if (listItem.Type == ListItemType.Artist && listItem.Artist != null)
                {
                    ConfigureArtistContextMenu(listItem.Artist);
                    return;
                }

                if (listItem.Type == ListItemType.Category)
                {
                    // 分类：不支持插播，只显示下载分类
                    insertPlayMenuItem.Visible = false;
                    downloadCategoryMenuItem.Visible = true;
                    return;
                }

                if (listItem.Type == ListItemType.Playlist && listItem.Playlist != null)
                {
                    playlistFromListItem = listItem.Playlist;
                }
                else if (listItem.Type == ListItemType.Album && listItem.Album != null)
                {
                    albumFromListItem = listItem.Album;
                }
                else if (listItem.Type == ListItemType.Song && listItem.Song != null)
                {
                    songFromListItem = listItem.Song;
                }
            }
            var playlist = selectedItem.Tag as PlaylistInfo ?? playlistFromListItem;
            var album = selectedItem.Tag as AlbumInfo ?? albumFromListItem;
            var resolvedSongFromListItem = songFromListItem;

            if (playlist != null)
            {
                // 歌单：显示收藏/取消收藏歌单（仅在登录时）
                bool isCreatedByCurrentUser = isMyPlaylistsView && IsPlaylistCreatedByCurrentUser(playlist);

                if (isLoggedIn)
                {
                    subscribePlaylistMenuItem.Visible = !isMyPlaylistsView;
                    unsubscribePlaylistMenuItem.Visible = !isCreatedByCurrentUser;
                    deletePlaylistMenuItem.Visible = isCreatedByCurrentUser;
                }
                insertPlayMenuItem.Visible = false; // 歌单项不支持插播

                // 显示下载歌单和批量下载（当视图包含多个歌单时）
                downloadPlaylistMenuItem.Visible = true;
                batchDownloadPlaylistsMenuItem.Visible = true;

                sharePlaylistMenuItem.Visible = true;
                sharePlaylistMenuItem.Tag = playlist;
                sharePlaylistWebMenuItem.Tag = playlist;
                sharePlaylistDirectMenuItem.Tag = playlist;
                showViewSection = true;
            }
            else if (album != null)
            {
                // 专辑：显示收藏/取消收藏专辑（仅在登录时）
                if (isLoggedIn)
                {
                    subscribeAlbumMenuItem.Visible = !isUserAlbumsView;
                    unsubscribeAlbumMenuItem.Visible = true;
                }
                insertPlayMenuItem.Visible = false; // 专辑项不支持插播

                // 显示下载专辑和批量下载（当视图包含多个专辑时）
                downloadAlbumMenuItem.Visible = true;
                batchDownloadPlaylistsMenuItem.Visible = true;

                shareAlbumMenuItem.Visible = true;
                shareAlbumMenuItem.Tag = album;
                shareAlbumWebMenuItem.Tag = album;
                shareAlbumDirectMenuItem.Tag = album;
                showViewSection = true;
            }
            else
            {
                // 歌曲：显示插播、收藏和下载功能
                insertPlayMenuItem.Visible = true;

                SongInfo? currentSong = null;
                if (selectedItem.Tag is int songIndex && songIndex >= 0 && songIndex < _currentSongs.Count)
                {
                    currentSong = _currentSongs[songIndex];
                }
                else if (selectedItem.Tag is SongInfo directSong)
                {
                    currentSong = directSong;
                }
                else if (resolvedSongFromListItem != null)
                {
                    currentSong = resolvedSongFromListItem;
                }

                bool isCloudSong = isCloudView && currentSong != null && currentSong.IsCloudSong;

                if (isCloudSong)
                {
                    likeSongMenuItem.Visible = false;
                    unlikeSongMenuItem.Visible = false;
                    addToPlaylistMenuItem.Visible = false;
                    removeFromPlaylistMenuItem.Visible = false;
                    downloadSongMenuItem.Visible = false;
                    batchDownloadMenuItem.Visible = false;
                    deleteFromCloudMenuItem.Visible = true;
                    cloudMenuSeparator.Visible = true;

                    viewSongArtistMenuItem.Visible = false;
                    viewSongAlbumMenuItem.Visible = false;
                }
                else
                {
                    if (isLoggedIn)
                    {
                        bool isLikedSongsView = string.Equals(_currentViewSource, "user_liked_songs", StringComparison.OrdinalIgnoreCase);

                        if (isLikedSongsView)
                        {
                            likeSongMenuItem.Visible = false;
                            unlikeSongMenuItem.Visible = true;
                        }
                        else
                        {
                            likeSongMenuItem.Visible = true;
                            unlikeSongMenuItem.Visible = false;
                        }

                        addToPlaylistMenuItem.Visible = true;

                        bool isInUserPlaylist = _currentViewSource.StartsWith("playlist:") &&
                                               _currentPlaylist != null &&
                                               IsPlaylistCreatedByCurrentUser(_currentPlaylist);
                        removeFromPlaylistMenuItem.Visible = isInUserPlaylist;
                    }

                    downloadSongMenuItem.Visible = true;
                    batchDownloadMenuItem.Visible = true;

                    if (currentSong != null)
                    {
                        viewSongArtistMenuItem.Visible = true;
                        viewSongAlbumMenuItem.Visible = true;
                        shareSongMenuItem.Visible = true;
                        viewSongArtistMenuItem.Tag = currentSong;
                        viewSongAlbumMenuItem.Tag = currentSong;
                        shareSongMenuItem.Tag = currentSong;
                        shareSongWebMenuItem.Tag = currentSong;
                        shareSongDirectMenuItem.Tag = currentSong;
                        showViewSection = true;
                    }
                }
            }

            toolStripSeparatorView.Visible = showViewSection;
        }

        /// <summary>
        /// 获取当前上下文选中的歌曲
        /// </summary>
        private SongInfo? GetSelectedSongFromContextMenu(object? sender = null)
        {
            if (sender is ToolStripItem menuItem && menuItem.Tag is SongInfo taggedSong)
            {
                return taggedSong;
            }

            if (resultListView.SelectedItems.Count == 0)
            {
                return null;
            }

            var selectedItem = resultListView.SelectedItems[0];

            if (selectedItem.Tag is int index && index >= 0 && index < _currentSongs.Count)
            {
                return _currentSongs[index];
            }

            if (selectedItem.Tag is SongInfo directSong)
            {
                return directSong;
            }

            if (selectedItem.Tag is ListItemInfo listItem && listItem.Type == ListItemType.Song)
            {
                return listItem.Song;
            }

            return null;
        }

        /// <summary>
        /// 获取当前上下文选中的歌单
        /// </summary>
        private PlaylistInfo? GetSelectedPlaylistFromContextMenu(object? sender = null)
        {
            if (sender is ToolStripItem menuItem && menuItem.Tag is PlaylistInfo taggedPlaylist)
            {
                return taggedPlaylist;
            }

            if (resultListView.SelectedItems.Count == 0)
            {
                return null;
            }

            var selectedItem = resultListView.SelectedItems[0];

            if (selectedItem.Tag is PlaylistInfo playlist)
            {
                return playlist;
            }

            if (selectedItem.Tag is ListItemInfo listItem && listItem.Type == ListItemType.Playlist)
            {
                return listItem.Playlist;
            }

            return null;
        }

        /// <summary>
        /// 获取当前上下文选中的专辑
        /// </summary>
        private AlbumInfo? GetSelectedAlbumFromContextMenu(object? sender = null)
        {
            if (sender is ToolStripItem menuItem && menuItem.Tag is AlbumInfo taggedAlbum)
            {
                return taggedAlbum;
            }

            if (resultListView.SelectedItems.Count == 0)
            {
                return null;
            }

            var selectedItem = resultListView.SelectedItems[0];

            if (selectedItem.Tag is AlbumInfo album)
            {
                return album;
            }

            if (selectedItem.Tag is ListItemInfo listItem && listItem.Type == ListItemType.Album)
            {
                return listItem.Album;
            }

            return null;
        }

        /// <summary>
        /// 解析歌曲主唱信息（若当前数据缺失则自动补全）
        /// </summary>
        private async Task<(long ArtistId, string ArtistName)> ResolvePrimaryArtistAsync(SongInfo song)
        {
            if (song == null)
            {
                return (0, string.Empty);
            }

            if (song.ArtistIds != null && song.ArtistIds.Count > 0)
            {
                string artistName = song.ArtistNames != null && song.ArtistNames.Count > 0
                    ? song.ArtistNames[0]
                    : song.Artist;
                return (song.ArtistIds[0], artistName ?? string.Empty);
            }

            if (string.IsNullOrWhiteSpace(song.Id))
            {
                return (0, string.Empty);
            }

            var details = await _apiClient.GetSongDetailAsync(new[] { song.Id });
            var detail = details?.FirstOrDefault();
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
                    string artistName = detail.ArtistNames != null && detail.ArtistNames.Count > 0
                        ? detail.ArtistNames[0]
                        : detail.Artist;
                    return (detail.ArtistIds[0], artistName ?? string.Empty);
                }
            }

            return (0, string.Empty);
        }

        /// <summary>
        /// 确保歌曲包含专辑信息
        /// </summary>
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

            var details = await _apiClient.GetSongDetailAsync(new[] { song.Id });
            var detail = details?.FirstOrDefault();
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

        /// <summary>
        /// 检查单首歌曲资源是否可用
        /// </summary>
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

            var quality = GetCurrentQuality();
            var availability = await _apiClient.BatchCheckSongsAvailabilityAsync(new[] { song.Id }, quality);
            if (availability != null && availability.TryGetValue(song.Id, out var available))
            {
                song.IsAvailable = available;
                return available;
            }

            return false;
        }

        /// <summary>
        /// 批量获取歌曲可用性映射
        /// </summary>
        private async Task<Dictionary<string, bool>> FetchSongsAvailabilityAsync(IEnumerable<SongInfo> songs)
        {
            var idList = songs
                .Where(s => s != null && !string.IsNullOrWhiteSpace(s.Id))
                .Select(s => s.Id)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (idList.Length == 0)
            {
                return new Dictionary<string, bool>(StringComparer.Ordinal);
            }

            var quality = GetCurrentQuality();
            var availability = await _apiClient.BatchCheckSongsAvailabilityAsync(idList, quality);

            foreach (var song in songs)
            {
                if (song == null || string.IsNullOrWhiteSpace(song.Id))
                {
                    continue;
                }

                if (availability.TryGetValue(song.Id, out var available))
                {
                    song.IsAvailable = available;
                }
            }

            return availability;
        }

        /// <summary>
        /// 分批获取歌曲直链信息
        /// </summary>
        private async Task<Dictionary<string, SongUrlInfo>> FetchSongUrlsInBatchesAsync(IEnumerable<string> songIds, bool skipAvailabilityCheck = true)
        {
            var ids = songIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var result = new Dictionary<string, SongUrlInfo>(StringComparer.Ordinal);
            if (ids.Count == 0)
            {
                return result;
            }

            var quality = GetCurrentQuality();
            const int batchSize = 50;

            for (int i = 0; i < ids.Count; i += batchSize)
            {
                var batch = ids.Skip(i).Take(batchSize).ToArray();
                var batchResult = await _apiClient.GetSongUrlAsync(batch, quality, skipAvailabilityCheck);
                if (batchResult == null)
                {
                    continue;
                }

                foreach (var kvp in batchResult)
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
            var song = GetSelectedSongFromContextMenu(sender);
            if (song == null || string.IsNullOrWhiteSpace(song.Id))
            {
                MessageBox.Show("无法获取当前歌曲信息。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                UpdateStatusBar("正在加载歌手信息...");
                var (artistId, artistName) = await ResolvePrimaryArtistAsync(song);
                if (artistId <= 0)
                {
                    MessageBox.Show("未找到该歌曲的歌手信息。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    UpdateStatusBar("无法打开歌手");
                    return;
                }

                if (string.IsNullOrWhiteSpace(artistName))
                {
                    artistName = song.ArtistNames.FirstOrDefault() ?? song.Artist ?? "歌手";
                }

                var artist = new ArtistInfo
                {
                    Id = artistId,
                    Name = artistName
                };

                await OpenArtistAsync(artist);
                UpdateStatusBar($"已打开歌手：{artistName}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"打开歌手失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatusBar("打开歌手失败");
            }
        }

        private async void viewSongAlbumMenuItem_Click(object sender, EventArgs e)
        {
            var song = GetSelectedSongFromContextMenu(sender);
            if (song == null || string.IsNullOrWhiteSpace(song.Id))
            {
                MessageBox.Show("无法获取当前歌曲信息。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                UpdateStatusBar("正在加载专辑...");
                var albumId = await ResolveSongAlbumIdAsync(song);
                if (string.IsNullOrWhiteSpace(albumId))
                {
                    MessageBox.Show("未找到该歌曲的专辑信息。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    UpdateStatusBar("无法打开专辑");
                    return;
                }

                var album = new AlbumInfo
                {
                    Id = albumId,
                    Name = string.IsNullOrWhiteSpace(song.Album) ? $"专辑 {albumId}" : song.Album,
                    Artist = song.Artist,
                    PicUrl = song.PicUrl
                };

                await OpenAlbum(album);
                UpdateStatusBar($"已打开专辑：{album.Name}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"打开专辑失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatusBar("打开专辑失败");
            }
        }

        private async void shareSongWebMenuItem_Click(object sender, EventArgs e)
        {
            var song = GetSelectedSongFromContextMenu(sender);
            if (song == null || string.IsNullOrWhiteSpace(song.Id))
            {
                MessageBox.Show("无法获取当前歌曲信息，无法分享。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                UpdateStatusBar("正在检查歌曲资源...");
                if (!await EnsureSongAvailabilityAsync(song))
                {
                    MessageBox.Show("该歌曲资源不可用，无法分享网页链接。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    UpdateStatusBar("歌曲资源不可用");
                    return;
                }

                string url = $"https://music.163.com/#/song?id={song.Id}";
                try
                {
                    Clipboard.SetText(url);
                }
                catch (ExternalException ex)
                {
                    MessageBox.Show($"复制链接失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    UpdateStatusBar("复制链接失败");
                    return;
                }

                UpdateStatusBar("歌曲网页链接已复制到剪贴板");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"分享歌曲失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatusBar("歌曲分享失败");
            }
        }

        private async void shareSongDirectMenuItem_Click(object sender, EventArgs e)
        {
            var song = GetSelectedSongFromContextMenu(sender);
            if (song == null || string.IsNullOrWhiteSpace(song.Id))
            {
                MessageBox.Show("无法获取当前歌曲信息，无法分享。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                UpdateStatusBar("正在生成歌曲直链...");
                if (!await EnsureSongAvailabilityAsync(song))
                {
                    MessageBox.Show("该歌曲资源不可用，无法分享直链。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    UpdateStatusBar("歌曲资源不可用");
                    return;
                }

                var urlMap = await FetchSongUrlsInBatchesAsync(new[] { song.Id });
                if (!urlMap.TryGetValue(song.Id, out var urlInfo) || string.IsNullOrWhiteSpace(urlInfo.Url))
                {
                    MessageBox.Show("未能获取歌曲直链，可能需要登录或切换音质。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    UpdateStatusBar("获取直链失败");
                    return;
                }

                try
                {
                    Clipboard.SetText(urlInfo.Url);
                }
                catch (ExternalException ex)
                {
                    MessageBox.Show($"复制链接失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    UpdateStatusBar("复制链接失败");
                    return;
                }

                UpdateStatusBar("歌曲直链已复制到剪贴板");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"分享歌曲直链失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatusBar("歌曲分享失败");
            }
        }

        private async void sharePlaylistWebMenuItem_Click(object sender, EventArgs e)
        {
            var playlist = GetSelectedPlaylistFromContextMenu(sender);
            if (playlist == null || string.IsNullOrWhiteSpace(playlist.Id))
            {
                MessageBox.Show("无法获取歌单信息，无法分享。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                UpdateStatusBar($"正在准备歌单「{playlist.Name}」分享...");
                var songs = await _apiClient.GetPlaylistSongsAsync(playlist.Id);
                if (songs == null || songs.Count == 0)
                {
                    MessageBox.Show("歌单中没有歌曲。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    UpdateStatusBar("歌单为空");
                    return;
                }

                var availability = await FetchSongsAvailabilityAsync(songs);
                var availableSongs = songs
                    .Where(s => !string.IsNullOrWhiteSpace(s.Id) && availability.TryGetValue(s.Id, out var ok) && ok)
                    .ToList();

                if (availableSongs.Count == 0)
                {
                    MessageBox.Show("歌单中没有可用的歌曲资源。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    UpdateStatusBar("无可用歌曲可分享");
                    return;
                }

                string content = string.Join(Environment.NewLine,
                    songs.Where(s => !string.IsNullOrWhiteSpace(s.Id) && availability.TryGetValue(s.Id, out var ok) && ok)
                         .Select(s => $"https://music.163.com/#/song?id={s.Id}"));

                try
                {
                    Clipboard.SetText(content);
                }
                catch (ExternalException ex)
                {
                    MessageBox.Show($"复制链接失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    UpdateStatusBar("复制链接失败");
                    return;
                }

                MessageBox.Show($"已复制 {availableSongs.Count}/{songs.Count} 首歌曲的网页链接到剪贴板。", "分享成功",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                UpdateStatusBar("歌单网页链接已复制");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"分享歌单失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatusBar("歌单分享失败");
            }
        }

        private async void sharePlaylistDirectMenuItem_Click(object sender, EventArgs e)
        {
            var playlist = GetSelectedPlaylistFromContextMenu(sender);
            if (playlist == null || string.IsNullOrWhiteSpace(playlist.Id))
            {
                MessageBox.Show("无法获取歌单信息，无法分享。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                UpdateStatusBar($"正在生成歌单「{playlist.Name}」的歌曲直链...");
                var songs = await _apiClient.GetPlaylistSongsAsync(playlist.Id);
                if (songs == null || songs.Count == 0)
                {
                    MessageBox.Show("歌单中没有歌曲。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    UpdateStatusBar("歌单为空");
                    return;
                }

                var availability = await FetchSongsAvailabilityAsync(songs);
                var availableSongs = songs
                    .Where(s => !string.IsNullOrWhiteSpace(s.Id) && availability.TryGetValue(s.Id, out var ok) && ok)
                    .ToList();

                if (availableSongs.Count == 0)
                {
                    MessageBox.Show("歌单中没有可用的歌曲资源。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    UpdateStatusBar("无可用歌曲可分享");
                    return;
                }

                var urlMap = await FetchSongUrlsInBatchesAsync(availableSongs.Select(s => s.Id));
                var directLinks = new List<string>();
                foreach (var song in availableSongs)
                {
                    if (!string.IsNullOrWhiteSpace(song.Id) &&
                        urlMap.TryGetValue(song.Id, out var urlInfo) &&
                        !string.IsNullOrWhiteSpace(urlInfo.Url))
                    {
                        directLinks.Add(urlInfo.Url);
                    }
                }

                if (directLinks.Count == 0)
                {
                    MessageBox.Show("未能获取任何歌曲直链，请稍后再试。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    UpdateStatusBar("未获取到直链");
                    return;
                }

                string content = string.Join(Environment.NewLine, directLinks);
                try
                {
                    Clipboard.SetText(content);
                }
                catch (ExternalException ex)
                {
                    MessageBox.Show($"复制链接失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    UpdateStatusBar("复制链接失败");
                    return;
                }

                MessageBox.Show($"已复制 {directLinks.Count}/{availableSongs.Count} 首歌曲的直链到剪贴板。", "分享成功",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                UpdateStatusBar("歌单直链已复制");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"分享歌单直链失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatusBar("歌单分享失败");
            }
        }

        private async void shareAlbumWebMenuItem_Click(object sender, EventArgs e)
        {
            var album = GetSelectedAlbumFromContextMenu(sender);
            if (album == null || string.IsNullOrWhiteSpace(album.Id))
            {
                MessageBox.Show("无法获取专辑信息，无法分享。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                UpdateStatusBar($"正在准备专辑「{album.Name}」分享...");
                var songs = await _apiClient.GetAlbumSongsAsync(album.Id ?? string.Empty);
                if (songs == null || songs.Count == 0)
                {
                    MessageBox.Show("专辑中没有歌曲。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    UpdateStatusBar("专辑为空");
                    return;
                }

                var availability = await FetchSongsAvailabilityAsync(songs);
                var availableSongs = songs
                    .Where(s => !string.IsNullOrWhiteSpace(s.Id) && availability.TryGetValue(s.Id, out var ok) && ok)
                    .ToList();

                if (availableSongs.Count == 0)
                {
                    MessageBox.Show("专辑中没有可用的歌曲资源。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    UpdateStatusBar("无可用歌曲可分享");
                    return;
                }

                string content = string.Join(Environment.NewLine,
                    songs.Where(s => !string.IsNullOrWhiteSpace(s.Id) && availability.TryGetValue(s.Id, out var ok) && ok)
                         .Select(s => $"https://music.163.com/#/song?id={s.Id}"));

                try
                {
                    Clipboard.SetText(content);
                }
                catch (ExternalException ex)
                {
                    MessageBox.Show($"复制链接失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    UpdateStatusBar("复制链接失败");
                    return;
                }

                MessageBox.Show($"已复制 {availableSongs.Count}/{songs.Count} 首歌曲的网页链接到剪贴板。", "分享成功",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                UpdateStatusBar("专辑网页链接已复制");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"分享专辑失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatusBar("专辑分享失败");
            }
        }

        private async void shareAlbumDirectMenuItem_Click(object sender, EventArgs e)
        {
            var album = GetSelectedAlbumFromContextMenu(sender);
            if (album == null || string.IsNullOrWhiteSpace(album.Id))
            {
                MessageBox.Show("无法获取专辑信息，无法分享。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                UpdateStatusBar($"正在生成专辑「{album.Name}」的歌曲直链...");
                var songs = await _apiClient.GetAlbumSongsAsync(album.Id ?? string.Empty);
                if (songs == null || songs.Count == 0)
                {
                    MessageBox.Show("专辑中没有歌曲。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    UpdateStatusBar("专辑为空");
                    return;
                }

                var availability = await FetchSongsAvailabilityAsync(songs);
                var availableSongs = songs
                    .Where(s => !string.IsNullOrWhiteSpace(s.Id) && availability.TryGetValue(s.Id, out var ok) && ok)
                    .ToList();

                if (availableSongs.Count == 0)
                {
                    MessageBox.Show("专辑中没有可用的歌曲资源。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    UpdateStatusBar("无可用歌曲可分享");
                    return;
                }

                var urlMap = await FetchSongUrlsInBatchesAsync(availableSongs.Select(s => s.Id));
                var directLinks = new List<string>();
                foreach (var song in availableSongs)
                {
                    if (!string.IsNullOrWhiteSpace(song.Id) &&
                        urlMap.TryGetValue(song.Id, out var urlInfo) &&
                        !string.IsNullOrWhiteSpace(urlInfo.Url))
                    {
                        directLinks.Add(urlInfo.Url);
                    }
                }

                if (directLinks.Count == 0)
                {
                    MessageBox.Show("未能获取任何歌曲直链，请稍后再试。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    UpdateStatusBar("未获取到直链");
                    return;
                }

                string content = string.Join(Environment.NewLine, directLinks);
                try
                {
                    Clipboard.SetText(content);
                }
                catch (ExternalException ex)
                {
                    MessageBox.Show($"复制链接失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    UpdateStatusBar("复制链接失败");
                    return;
                }

                MessageBox.Show($"已复制 {directLinks.Count}/{availableSongs.Count} 首歌曲的直链到剪贴板。", "分享成功",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                UpdateStatusBar("专辑直链已复制");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"分享专辑直链失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatusBar("专辑分享失败");
            }
        }

        /// <summary>
        /// 收藏歌单
        /// </summary>
        private async void subscribePlaylistMenuItem_Click(object sender, EventArgs e)
        {
            var selectedItem = resultListView.SelectedItems.Count > 0 ? resultListView.SelectedItems[0] : null;
            if (selectedItem?.Tag is PlaylistInfo playlist)
            {
                try
                {
                    UpdateStatusBar("正在收藏歌单...");
                    bool success = await _apiClient.SubscribePlaylistAsync(playlist.Id, true);
                    if (success)
                    {
                        MessageBox.Show($"已收藏歌单：{playlist.Name}", "成功",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                        UpdateStatusBar("歌单收藏成功");
                    }
                    else
                    {
                        MessageBox.Show("收藏歌单失败，请检查网络或稍后重试。", "失败",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        UpdateStatusBar("歌单收藏失败");
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"收藏歌单失败: {ex.Message}", "错误",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    UpdateStatusBar("歌单收藏失败");
                }
            }
        }

        /// <summary>
        /// 取消收藏歌单
        /// </summary>
        private async void unsubscribePlaylistMenuItem_Click(object sender, EventArgs e)
        {
            var selectedItem = resultListView.SelectedItems.Count > 0 ? resultListView.SelectedItems[0] : null;
            if (selectedItem?.Tag is PlaylistInfo playlist)
            {
                try
                {
                    UpdateStatusBar("正在取消收藏歌单...");
                    bool success = await _apiClient.SubscribePlaylistAsync(playlist.Id, false);
                    if (success)
                    {
                        MessageBox.Show($"已取消收藏歌单：{playlist.Name}", "成功",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                        UpdateStatusBar("取消收藏成功");
                        try
                        {
                            await RefreshUserPlaylistsIfActiveAsync();
                        }
                        catch (Exception refreshEx)
                        {
                            System.Diagnostics.Debug.WriteLine($"[UI] 刷新我的歌单列表失败: {refreshEx}");
                        }
                    }
                    else
                    {
                        MessageBox.Show("取消收藏失败，请检查网络或稍后重试。", "失败",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        UpdateStatusBar("取消收藏失败");
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"取消收藏失败: {ex.Message}", "错误",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    UpdateStatusBar("取消收藏失败");
                }
            }
        }

        /// <summary>
        /// 删除用户创建的歌单
        /// </summary>
        private async void deletePlaylistMenuItem_Click(object sender, EventArgs e)
        {
            var selectedItem = resultListView.SelectedItems.Count > 0 ? resultListView.SelectedItems[0] : null;
            if (selectedItem?.Tag is PlaylistInfo playlist)
            {
                var confirm = MessageBox.Show($"确定要删除歌单：{playlist.Name}？\n删除后将无法恢复。",
                    "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (confirm != DialogResult.Yes)
                {
                    return;
                }

                try
                {
                    UpdateStatusBar("正在删除歌单...");
                    bool success = await _apiClient.DeletePlaylistAsync(playlist.Id);
                    if (success)
                    {
                        MessageBox.Show($"已删除歌单：{playlist.Name}", "成功",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                        UpdateStatusBar("删除歌单成功");
                        try
                        {
                            await RefreshUserPlaylistsIfActiveAsync();
                        }
                        catch (Exception refreshEx)
                        {
                            System.Diagnostics.Debug.WriteLine($"[UI] 刷新我的歌单列表失败: {refreshEx}");
                        }
                    }
                    else
                    {
                        MessageBox.Show("删除歌单失败，请检查网络或稍后重试。", "失败",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        UpdateStatusBar("删除歌单失败");
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"删除歌单失败: {ex.Message}", "错误",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    UpdateStatusBar("删除歌单失败");
                }
            }
        }

        /// <summary>
        /// 收藏专辑
        /// </summary>
        private async void subscribeAlbumMenuItem_Click(object sender, EventArgs e)
        {
            var selectedItem = resultListView.SelectedItems.Count > 0 ? resultListView.SelectedItems[0] : null;
            if (selectedItem?.Tag is AlbumInfo album)
            {
                try
                {
                    UpdateStatusBar("正在收藏专辑...");
                    if (string.IsNullOrEmpty(album.Id))
                    {
                        MessageBox.Show("无法识别专辑信息，收藏操作已取消。", "提示",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                        UpdateStatusBar("专辑收藏失败");
                        return;
                    }

                    string albumId = album.Id!;
                    bool success = await _apiClient.SubscribeAlbumAsync(albumId);
                    if (success)
                    {
                        MessageBox.Show($"已收藏专辑：{album.Name}", "成功",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                        UpdateStatusBar("专辑收藏成功");
                    }
                    else
                    {
                        MessageBox.Show("收藏专辑失败，请检查网络或稍后重试。", "失败",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        UpdateStatusBar("专辑收藏失败");
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"收藏专辑失败: {ex.Message}", "错误",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    UpdateStatusBar("专辑收藏失败");
                }
            }
        }

        /// <summary>
        /// 取消收藏专辑
        /// </summary>
        private async void unsubscribeAlbumMenuItem_Click(object sender, EventArgs e)
        {
            var selectedItem = resultListView.SelectedItems.Count > 0 ? resultListView.SelectedItems[0] : null;
            if (selectedItem?.Tag is AlbumInfo album)
            {
                try
                {
                    UpdateStatusBar("正在取消收藏专辑...");
                    if (string.IsNullOrEmpty(album.Id))
                    {
                        MessageBox.Show("无法识别专辑信息，取消收藏操作已取消。", "提示",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                        UpdateStatusBar("取消收藏失败");
                        return;
                    }

                    string albumId = album.Id!;
                    bool success = await _apiClient.UnsubscribeAlbumAsync(albumId);
                    if (success)
                    {
                        MessageBox.Show($"已取消收藏专辑：{album.Name}", "成功",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                        UpdateStatusBar("取消收藏成功");
                        try
                        {
                            await RefreshUserAlbumsIfActiveAsync();
                        }
                        catch (Exception refreshEx)
                        {
                            System.Diagnostics.Debug.WriteLine($"[UI] 刷新收藏的专辑列表失败: {refreshEx}");
                        }
                    }
                    else
                    {
                        MessageBox.Show("取消收藏失败，请检查网络或稍后重试。", "失败",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        UpdateStatusBar("取消收藏失败");
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"取消收藏失败: {ex.Message}", "错误",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    UpdateStatusBar("取消收藏失败");
                }
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

        private long GetCurrentUserId()
        {
            if (_loggedInUserId > 0)
            {
                return _loggedInUserId;
            }

            if (_accountState != null && long.TryParse(_accountState.UserId, out var parsedId))
            {
                _loggedInUserId = parsedId;
                return _loggedInUserId;
            }

            return 0;
        }

        private bool IsPlaylistCreatedByCurrentUser(PlaylistInfo playlist)
        {
            long currentUserId = GetCurrentUserId();
            if (currentUserId <= 0 || playlist == null)
            {
                return false;
            }

            if (playlist.CreatorId > 0 && playlist.CreatorId == currentUserId)
            {
                return true;
            }

            if (playlist.OwnerUserId > 0 && playlist.OwnerUserId == currentUserId)
            {
                return true;
            }

            return false;
        }

        #endregion


        #region 窗体事件

        /// <summary>
        /// 窗体关闭
        /// </summary>
protected override void OnFormClosing(FormClosingEventArgs e)
{
    base.OnFormClosing(e);

    try
    {
        // 取消所有待处理的操作
        _playbackCancellation?.Cancel();
        _playbackCancellation?.Dispose();

        _availabilityCheckCts?.Cancel();
        _availabilityCheckCts?.Dispose();
        _availabilityCheckCts = null;

        // ⭐ 使用 SeekManager 取消
        _seekManager?.CancelPendingSeeks();
        _seekManager?.Dispose();

        if (_scrubKeyTimer != null)
        {
            _scrubKeyTimer.Stop();
            _scrubKeyTimer.Dispose();
            _scrubKeyTimer = null;
        }

        // 停止异步状态更新循环
        StopStateUpdateLoop();

        _updateTimer?.Stop();
        _nextSongPreloader?.Dispose();
        _audioEngine?.Dispose();

        _apiClient?.Dispose();

        // 🔧 修复：释放下载管理器，停止所有下载任务
        try
        {
            YTPlayer.Core.Download.DownloadManager.Instance?.Dispose();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OnFormClosing] DownloadManager释放异常: {ex.Message}");
        }

        try
        {
            var uploadManager = YTPlayer.Core.Upload.UploadManager.Instance;
            if (uploadManager != null)
            {
                uploadManager.TaskCompleted -= OnCloudUploadTaskCompleted;
                uploadManager.TaskFailed -= OnCloudUploadTaskFailed;
                uploadManager.Dispose();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OnFormClosing] UploadManager释放异常: {ex.Message}");
        }

        // ⭐ 释放托盘图标和宿主窗口
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;  // 程序退出时才隐藏
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        // ⭐ 释放菜单宿主窗口
        if (_contextMenuHost != null)
        {
            // ⭐⭐⭐ 修复：只调用 Dispose()，不调用 Close()
            // 原因：Close() 可能修改 Application.OpenForms 集合，导致集合修改异常
            // Dispose() 会自动处理资源释放，无需手动 Close()
            try
            {
                _contextMenuHost.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[OnFormClosing] 释放菜单宿主窗口异常: {ex.Message}");
            }
            _contextMenuHost = null;
        }

        SaveConfig();
    }
    catch (Exception ex)
    {
        System.Diagnostics.Debug.WriteLine($"[OnFormClosing] 异常: {ex.Message}");
    }
}

        #endregion
    }

    /// <summary>
    /// 导航历史项（用于后退功能）
    /// </summary>
    internal class NavigationHistoryItem
    {
        /// <summary>
        /// 页面类型
        /// </summary>
        public string PageType { get; set; } = string.Empty;  // "homepage", "category", "playlist", "album", "search", "songs", "playlists", "albums"

        /// <summary>
        /// 视图来源标识（如 "search", "playlist:123", "album:456"）
        /// </summary>
        public string ViewSource { get; set; } = string.Empty;

        /// <summary>
        /// 视图显示名称（如搜索关键词、歌单名、专辑名）
        /// </summary>
        public string ViewName { get; set; } = string.Empty;

        /// <summary>
        /// 当前选中的索引（用于恢复焦点）
        /// </summary>
        public int SelectedIndex { get; set; } = -1;

        // ===== 重新加载所需的参数 =====

        /// <summary>
        /// 分类ID（用于重新加载分类页面）
        /// </summary>
        public string CategoryId { get; set; } = string.Empty;

        /// <summary>
        /// 歌单ID（用于重新加载歌单）
        /// </summary>
        public string PlaylistId { get; set; } = string.Empty;

        /// <summary>
        /// 专辑ID（用于重新加载专辑）
        /// </summary>
        public string AlbumId { get; set; } = string.Empty;

        /// <summary>
        /// 搜索关键词（用于重新搜索）
        /// </summary>
        public string SearchKeyword { get; set; } = string.Empty;

        /// <summary>
        /// 搜索类型（用于重新搜索）
        /// </summary>
        public string SearchType { get; set; } = string.Empty;

        /// <summary>
        /// 当前页码（用于重新搜索）
        /// </summary>
        public int CurrentPage { get; set; } = 1;

        /// <summary>
        /// 歌手ID（用于重新加载歌手相关视图）
        /// </summary>
        public long ArtistId { get; set; }

        /// <summary>
        /// 歌手名称（用于恢复标题显示）
        /// </summary>
        public string ArtistName { get; set; } = string.Empty;

        /// <summary>
        /// 歌手列表偏移量（用于分页恢复）
        /// </summary>
        public int ArtistOffset { get; set; }

        /// <summary>
        /// 歌手类型筛选（分类视图使用）
        /// </summary>
        public int ArtistType { get; set; } = -1;

        /// <summary>
        /// 歌手地区筛选（分类视图使用）
        /// </summary>
        public int ArtistArea { get; set; } = -1;
    }
}




