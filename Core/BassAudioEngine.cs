using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using YTPlayer.Core.Playback;
using YTPlayer.Core.Playback.Cache;
using YTPlayer.Core.Streaming;
using YTPlayer.Models;
using YTPlayer.Utils;

namespace YTPlayer.Core
{
    /// <summary>
    /// BASS 音频引擎的轻量封装，提供播放、暂停、跳转与缓存集成。
    /// </summary>
    public sealed class BassAudioEngine : IDisposable
    {
        #region BASS 常量与 P/Invoke

        private const uint DeviceFrequency = 44100;
        private const int BASS_DEVICE_STEREO = 0x8000;
        private const int BASS_DEVICE_ENABLED_FLAG = 0x1;
        private const int BASS_DEVICE_DEFAULT_FLAG = 0x2;

        private const int BASS_POS_BYTE = 0;
        private const int BASS_ATTRIB_VOL = 2;
        private const int BASS_CONFIG_BUFFER = 0;
        private const int BASS_CONFIG_UPDATEPERIOD = 1;

        private const int BASS_ACTIVE_STOPPED = 0;
        private const int BASS_ACTIVE_PLAYING = 1;
        private const int BASS_ACTIVE_STALLED = 2;
        private const int BASS_ACTIVE_PAUSED = 3;
        private const int BASS_ACTIVE_PAUSED_DEVICE = 4;

        private const int BASS_SYNC_END = 2;
        private const int BASS_SYNC_POS = 0;
        private const uint BASS_SYNC_MIXTIME = 0x40000000;  // ⭐ 混音时间同步 - 在混音缓冲区时立即触发，无延迟
        private const uint BASS_UNICODE = 0x80000000;  // UTF-16 flag for Unicode strings

        [DllImport("bass.dll")]
        private static extern bool BASS_Init(int device, uint freq, uint flags, IntPtr win, IntPtr clsid);

        [DllImport("bass.dll")]
        private static extern bool BASS_Free();

        [DllImport("bass.dll")]
        private static extern bool BASS_GetDeviceInfo(int device, out BASS_DEVICEINFO info);

        [DllImport("bass.dll")]
        private static extern bool BASS_SetDevice(int device);

        [DllImport("bass.dll")]
        private static extern int BASS_GetDevice();

        [DllImport("bass.dll", CharSet = CharSet.Unicode)]
        private static extern int BASS_PluginLoad(string file, uint flags);

        [DllImport("bass.dll")]
        private static extern bool BASS_SetConfig(int option, int value);

        [DllImport("bass.dll")]
        private static extern int BASS_ErrorGetCode();

        [DllImport("bass.dll")]
        private static extern bool BASS_StreamFree(int handle);

        [DllImport("bass.dll")]
        private static extern bool BASS_ChannelPlay(int handle, bool restart);

        [DllImport("bass.dll")]
        private static extern bool BASS_ChannelPause(int handle);

        [DllImport("bass.dll")]
        private static extern bool BASS_Start();

        [DllImport("bass.dll")]
        private static extern bool BASS_ChannelStop(int handle);

        [DllImport("bass.dll")]
        private static extern int BASS_ChannelIsActive(int handle);

        [DllImport("bass.dll")]
        private static extern long BASS_ChannelGetLength(int handle, int mode);

        [DllImport("bass.dll")]
        private static extern long BASS_ChannelGetPosition(int handle, int mode);

        [DllImport("bass.dll")]
        private static extern bool BASS_ChannelSetPosition(int handle, long position, int mode);

        [DllImport("bass.dll")]
        private static extern double BASS_ChannelBytes2Seconds(int handle, long position);

        [DllImport("bass.dll")]
        private static extern long BASS_ChannelSeconds2Bytes(int handle, double seconds);

        [DllImport("bass.dll")]
        private static extern bool BASS_ChannelSetAttribute(int handle, int attrib, float value);

        [DllImport("bass.dll")]
        private static extern bool BASS_ChannelSlideAttribute(int handle, int attrib, float value, int time);

        [DllImport("bass.dll")]
        private static extern int BASS_ChannelSetSync(int handle, int type, long param, SYNCPROC proc, IntPtr user);

        [DllImport("bass.dll")]
        private static extern bool BASS_ChannelRemoveSync(int handle, int sync);

        [DllImport("bass.dll")]
        private static extern bool BASS_ChannelGetInfo(int handle, ref BASS_CHANNELINFO info);

        [DllImport("bass.dll")]
        private static extern bool BASS_ChannelSetDevice(int handle, int device);

        [StructLayout(LayoutKind.Sequential)]
        private struct BASS_DEVICEINFO
        {
            public IntPtr name;
            public IntPtr driver;
            public uint flags;
            public IntPtr guid;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BASS_CHANNELINFO
        {
            public int freq;        // 采样率
            public int chans;       // 声道数
            public int flags;       // BASS_SAMPLE/STREAM/MUSIC/SPEAKER 标志
            public int ctype;       // channel类型
            public int origres;     // 原始分辨率
            public int plugin;      // 插件
            public int sample;      // 样本
            public IntPtr filename; // 文件名
        }

        private delegate void SYNCPROC(int handle, int channel, int data, IntPtr user);

        #endregion

        private sealed class GaplessPreloadEntry
        {
            public GaplessPreloadEntry(SongInfo song, Playback.PreloadedData data)
            {
                Song = song;
                Data = data;
            }

            public SongInfo Song { get; }
            public Playback.PreloadedData Data { get; }
        }

        private sealed class BassDeviceDescriptor
        {
            public BassDeviceDescriptor(int index, string name, string driver, Guid? guid, bool isEnabled, bool isDefault)
            {
                Index = index;
                Name = name;
                Driver = driver;
                Guid = guid;
                IsEnabled = isEnabled;
                IsDefault = isDefault;
            }

            public int Index { get; }
            public string Name { get; }
            public string Driver { get; }
            public Guid? Guid { get; }
            public bool IsEnabled { get; }
            public bool IsDefault { get; }
        }

        #region 字段

        private readonly object _syncRoot = new object();
        private readonly HttpClient _httpClient;
        private readonly PlaybackStateMachine _stateMachine;
        private readonly SemaphoreSlim _playSemaphore = new SemaphoreSlim(1, 1);
        private readonly SYNCPROC _endSyncProc;
        private readonly object _gaplessPreloadLock = new object();
        private readonly HashSet<int> _initializedDeviceIndices = new HashSet<int>();

        private CancellationTokenSource? _positionMonitorCts;
        private Task? _positionMonitorTask;

        private SmartCacheManager? _currentCacheManager;
        private BassStreamProvider? _currentStreamProvider;

        private SongInfo? _currentSong;

        private int _currentStream;
        private int _endSyncHandle;
        private int _nearEndSyncHandle;
        private GaplessPreloadEntry? _gaplessPreload;

        private float _volume = 0.8f;
        private PlayMode _playMode = PlayMode.Loop;
        private bool _isInitialized;
        private bool _disposed;
        private bool _bassFlacLoaded;
        private int _currentDeviceIndex;
        private string _activeOutputDeviceId = AudioOutputDeviceInfo.WindowsDefaultId;
        private DateTime _lastDeviceResumeAttemptUtc = DateTime.MinValue;
        private static readonly TimeSpan MaxBufferingDuration = TimeSpan.FromSeconds(45);
        private DateTime _bufferingStateSinceUtc = DateTime.MinValue;
        private bool _bufferingTimeoutTriggered;

        #endregion

        #region 事件

        public event EventHandler<SongInfo>? PlaybackStarted;
        public event EventHandler<SongInfo>? PlaybackEnded;
        public event EventHandler<TimeSpan>? PositionChanged;
        public event EventHandler<PlaybackState>? StateChanged;
        public event EventHandler<string>? PlaybackError;
        public event EventHandler? PlaybackStopped;
        public event EventHandler<BufferingState>? BufferingStateChanged;
        public event EventHandler<GaplessTransitionEventArgs>? GaplessTransitionCompleted;

        #endregion

        #region 构造与属性

        public BassAudioEngine(string? preferredDeviceId = null)
        {
            // 使用专为播放优化的 HttpClient（高并发/长超时/keep-alive）
            _httpClient = Core.Streaming.OptimizedHttpClientFactory.CreateForMainPlayback(TimeSpan.FromSeconds(60));

            _stateMachine = new PlaybackStateMachine();
            _stateMachine.StateChanged += StateMachineOnStateChanged;

            _endSyncProc = OnStreamEnded;

            InitializeBass(preferredDeviceId);
        }

        public bool IsInitialized => _isInitialized;

        public bool IsPlaying => _currentStream != 0 && BASS_ChannelIsActive(_currentStream) == BASS_ACTIVE_PLAYING;

        public bool IsPaused
        {
            get
            {
                if (_currentStream == 0)
                {
                    return false;
                }

                int activeState = BASS_ChannelIsActive(_currentStream);
                return activeState == BASS_ACTIVE_PAUSED || activeState == BASS_ACTIVE_PAUSED_DEVICE;
            }
        }

        public SongInfo? CurrentSong => _currentSong;

        public PlayMode PlayMode
        {
            get => _playMode;
            set => _playMode = value;
        }

        public string ActiveOutputDeviceId => _activeOutputDeviceId;

        public SmartCacheManager? CurrentCacheManager => _currentCacheManager;

        #endregion

        #region 公共 API

        public IReadOnlyList<AudioOutputDeviceInfo> GetOutputDevices()
        {
            lock (_syncRoot)
            {
                return EnumerateOutputDevicesInternal();
            }
        }

        public async Task<AudioDeviceSwitchResult> SwitchOutputDeviceAsync(
            AudioOutputDeviceInfo? selection,
            CancellationToken cancellationToken = default)
        {
            if (selection == null)
            {
                return AudioDeviceSwitchResult.Failure("请选择有效的输出设备");
            }

            if (!await TryEnterPlaySemaphoreAsync(cancellationToken).ConfigureAwait(false))
            {
                return AudioDeviceSwitchResult.Failure("音频引擎已释放");
            }

            bool lockTaken = true;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                AudioDeviceSwitchResult result;

                lock (_syncRoot)
                {
                    var descriptors = EnumerateBassDevices();
                    var resolution = ResolveTargetDevice(selection, descriptors);

                    if (!resolution.success)
                    {
                        result = AudioDeviceSwitchResult.Failure(resolution.errorMessage ?? "输出设备不可用");
                    }
                    else if (resolution.deviceIndex == _currentDeviceIndex &&
                             string.Equals(_activeOutputDeviceId, resolution.resolvedId, StringComparison.OrdinalIgnoreCase))
                    {
                        result = AudioDeviceSwitchResult.NoChange(selection);
                    }
                    else if (!EnsureDeviceInitialized(resolution.deviceIndex, out var initError))
                    {
                        result = AudioDeviceSwitchResult.Failure(initError ?? "输出设备初始化失败");
                    }
                    else if (_currentStream != 0 && !BASS_ChannelSetDevice(_currentStream, resolution.deviceIndex))
                    {
                        int errorCode = BASS_ErrorGetCode();
                        result = AudioDeviceSwitchResult.Failure($"无法迁移当前播放流: {GetErrorMessage(errorCode)}");
                    }
                    else
                    {
                        int previousDeviceIndex = _currentDeviceIndex;
                        _currentDeviceIndex = resolution.deviceIndex;
                        _activeOutputDeviceId = resolution.resolvedId;

                        var snapshot = FindDeviceSnapshot(_activeOutputDeviceId, _currentDeviceIndex) ?? selection;

                        if (previousDeviceIndex != 0 && previousDeviceIndex != _currentDeviceIndex)
                        {
                            ReleaseDevice(previousDeviceIndex);
                        }

                        result = AudioDeviceSwitchResult.Success(snapshot);
                    }
                }

                EnsureDeviceContext();
                return result;
            }
            finally
            {
                if (lockTaken)
                {
                    try
                    {
                        _playSemaphore.Release();
                    }
                    catch (ObjectDisposedException)
                    {
                        // 已释放时忽略
                    }
                }
            }
        }

        public async Task<bool> PlayAsync(SongInfo song, CancellationToken cancellationToken = default, Playback.PreloadedData? preloadedData = null)
        {
            if (song == null || string.IsNullOrEmpty(song.Url))
            {
                OnPlaybackError("无效的歌曲信息或 URL");
                return false;
            }

            preloadedData ??= ConsumeGaplessPreload(song.Id);

            if (!await TryEnterPlaySemaphoreAsync(cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            bool lockTaken = true;
            bool transitionedToLoading = false;
            bool committed = false;
            int preparedStream = 0;
            BassStreamProvider? preparedStreamProvider = null;
            SmartCacheManager? preparedCacheManager = null;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                bool hadCurrentStream = HasCurrentStream();
                if (!hadCurrentStream)
                {
                    transitionedToLoading = _stateMachine.TransitionTo(PlaybackState.Loading);
                }
                else
                {
                    Debug.WriteLine("[BassAudioEngine] 当前已有播放流，延迟到新流启动成功后再切换");
                }

                // 检查是否有预加载的完整流对象（就绪状态）。旧流会一直保留到预加载流成功启动后。
                if (preloadedData != null && preloadedData.IsReady && preloadedData.StreamHandle != 0)
                {
                    if (preloadedData.CacheManager == null || preloadedData.StreamProvider == null)
                    {
                        OnPlaybackError("预加载播放资源不完整");
                        RestoreIdleAfterFailedPlayback(transitionedToLoading);
                        return false;
                    }

                    preparedStream = preloadedData.StreamHandle;
                    preparedStreamProvider = preloadedData.StreamProvider;
                    preparedCacheManager = preloadedData.CacheManager;

                    Debug.WriteLine($"[BassAudioEngine] ✓✓✓ 使用预加载的完整流对象，句柄: {preparedStream}");
                    Debug.WriteLine($"[BassAudioEngine] ⭐ 准备播放预加载流: stream={preparedStream}");

                    EnsureDeviceContext();
                    BASS_ChannelSetPosition(preparedStream, 0, BASS_POS_BYTE);
                    BASS_ChannelSetAttribute(preparedStream, BASS_ATTRIB_VOL, _volume);
                    Debug.WriteLine($"[BassAudioEngine] ✓ ApplyVolume完成，音量={_volume}");

                    if (!ValidatePreparedStream(preparedStream, preloadedData.Size > 0 ? preloadedData.Size : song.Size))
                    {
                        ReleasePreparedPlaybackResources(preparedStream, preparedStreamProvider, preparedCacheManager);
                        preparedStream = 0;
                        preparedStreamProvider = null;
                        preparedCacheManager = null;
                        RestoreIdleAfterFailedPlayback(transitionedToLoading);
                        return false;
                    }

                    Debug.WriteLine($"[BassAudioEngine] ⭐⭐⭐ 调用 BASS_ChannelPlay (预加载流, stream={preparedStream})");
                    bool preloadPlayResult = BASS_ChannelPlay(preparedStream, false);
                    Debug.WriteLine($"[BassAudioEngine] BASS_ChannelPlay 返回: {preloadPlayResult}");

                    if (!preloadPlayResult)
                    {
                        int errorCode = BASS_ErrorGetCode();
                        Debug.WriteLine($"[BassAudioEngine] ❌ BASS_ChannelPlay失败: errorCode={errorCode}, msg={GetErrorMessage(errorCode)}");
                        OnPlaybackError($"播放失败: {GetErrorMessage(errorCode)}");
                        ReleasePreparedPlaybackResources(preparedStream, preparedStreamProvider, preparedCacheManager);
                        preparedStream = 0;
                        preparedStreamProvider = null;
                        preparedCacheManager = null;
                        RestoreIdleAfterFailedPlayback(transitionedToLoading);
                        return false;
                    }

                    int preloadChannelState = BASS_ChannelIsActive(preparedStream);
                    Debug.WriteLine($"[BassAudioEngine] BASS_ChannelIsActive 返回: {preloadChannelState} (1=Playing)");

                    if (preloadChannelState != BASS_ACTIVE_PLAYING)
                    {
                        int errorCode = BASS_ErrorGetCode();
                        Debug.WriteLine($"[BassAudioEngine] ❌❌❌ Channel未在播放状态！ errorCode={errorCode}");
                        OnPlaybackError($"播放启动失败: Channel状态异常 (state={preloadChannelState})");
                        ReleasePreparedPlaybackResources(preparedStream, preparedStreamProvider, preparedCacheManager);
                        preparedStream = 0;
                        preparedStreamProvider = null;
                        preparedCacheManager = null;
                        RestoreIdleAfterFailedPlayback(transitionedToLoading);
                        return false;
                    }

                    CommitPreparedPlayback(song, preparedStream, preparedStreamProvider, preparedCacheManager);
                    committed = true;
                    preparedStream = 0;
                    preparedStreamProvider = null;
                    preparedCacheManager = null;

                    bool preloadStateChanged = TransitionToPlayingAfterCommit();
                    Debug.WriteLine($"[BassAudioEngine] 状态转换结果: {preloadStateChanged}");

                    OnPlaybackStarted(song);
                    Debug.WriteLine("[BassAudioEngine] ✓ PlayAsync完成（预加载流），返回true");
                    return true;
                }

                // 回退：使用常规流程准备缓存和 BASS stream。准备阶段不改动当前播放流。
                Debug.WriteLine("[BassAudioEngine] 无预加载完整流，使用延迟提交流程");

                long totalSize = song.Size;
                if (totalSize <= 0)
                {
                    var (_, contentLength) = await HttpRangeHelper.CheckRangeSupportAsync(
                        song.Url,
                        _httpClient,
                        cancellationToken,
                        song.CustomHeaders).ConfigureAwait(false);
                    totalSize = contentLength;
                }

                if (totalSize <= 0)
                {
                    OnPlaybackError("无法获取音频文件大小");
                    RestoreIdleAfterFailedPlayback(transitionedToLoading);
                    return false;
                }

                cancellationToken.ThrowIfCancellationRequested();

                if (preloadedData != null && preloadedData.CacheManager != null)
                {
                    Debug.WriteLine("[BassAudioEngine] ✓ 使用预加载的缓存管理器");
                    preparedCacheManager = preloadedData.CacheManager;
                }
                else
                {
                    Debug.WriteLine("[BassAudioEngine] 创建新的缓存管理器");
                    preparedCacheManager = await GetOrCreateCacheManagerAsync(song, totalSize, cancellationToken, song.CustomHeaders).ConfigureAwait(false);
                }

                bool ready = await preparedCacheManager.WaitForCacheReadyAsync(0, true, cancellationToken).ConfigureAwait(false);
                if (!ready)
                {
                    OnPlaybackError("缓存加载超时");
                    ReleasePreparedPlaybackResources(0, null, preparedCacheManager);
                    preparedCacheManager = null;
                    RestoreIdleAfterFailedPlayback(transitionedToLoading);
                    return false;
                }

                cancellationToken.ThrowIfCancellationRequested();

                preparedStreamProvider = new BassStreamProvider(preparedCacheManager);
                EnsureDeviceContext();
                preparedStream = preparedStreamProvider.CreateStream();

                // BASS stream 句柄是 DWORD（无符号32位），C# 声明为 int；只有 0 表示失败。
                if (preparedStream == 0)
                {
                    int errorCode = BASS_ErrorGetCode();
                    DebugLogger.Log(
                        DebugLogger.LogLevel.Error,
                        "BassAudioEngine",
                        $"创建播放流失败: handle={preparedStream}, error={GetErrorMessage(errorCode)}");
                    OnPlaybackError($"创建播放流失败: {GetErrorMessage(errorCode)}");
                    ReleasePreparedPlaybackResources(preparedStream, preparedStreamProvider, preparedCacheManager);
                    preparedStream = 0;
                    preparedStreamProvider = null;
                    preparedCacheManager = null;
                    RestoreIdleAfterFailedPlayback(transitionedToLoading);
                    return false;
                }

                Debug.WriteLine($"[BassAudioEngine] ⭐ 准备播放: stream={preparedStream}");

                BASS_ChannelSetAttribute(preparedStream, BASS_ATTRIB_VOL, _volume);
                Debug.WriteLine($"[BassAudioEngine] ✓ ApplyVolume完成，音量={_volume}");

                if (!ValidatePreparedStream(preparedStream, totalSize))
                {
                    ReleasePreparedPlaybackResources(preparedStream, preparedStreamProvider, preparedCacheManager);
                    preparedStream = 0;
                    preparedStreamProvider = null;
                    preparedCacheManager = null;
                    RestoreIdleAfterFailedPlayback(transitionedToLoading);
                    return false;
                }

                Debug.WriteLine($"[BassAudioEngine] ⭐⭐⭐ 调用 BASS_ChannelPlay (stream={preparedStream})");
                bool playResult = BASS_ChannelPlay(preparedStream, false);
                Debug.WriteLine($"[BassAudioEngine] BASS_ChannelPlay 返回: {playResult}");

                if (!playResult)
                {
                    int errorCode = BASS_ErrorGetCode();
                    Debug.WriteLine($"[BassAudioEngine] ❌ BASS_ChannelPlay失败: errorCode={errorCode}, msg={GetErrorMessage(errorCode)}");
                    OnPlaybackError($"播放失败: {GetErrorMessage(errorCode)}");
                    ReleasePreparedPlaybackResources(preparedStream, preparedStreamProvider, preparedCacheManager);
                    preparedStream = 0;
                    preparedStreamProvider = null;
                    preparedCacheManager = null;
                    RestoreIdleAfterFailedPlayback(transitionedToLoading);
                    return false;
                }

                int channelState = BASS_ChannelIsActive(preparedStream);
                Debug.WriteLine($"[BassAudioEngine] BASS_ChannelIsActive 返回: {channelState} (1=Playing, 0=Stopped, 3=Paused)");

                if (channelState != BASS_ACTIVE_PLAYING)
                {
                    int errorCode = BASS_ErrorGetCode();
                    Debug.WriteLine($"[BassAudioEngine] ❌❌❌ Channel未在播放状态！ errorCode={errorCode}, msg={GetErrorMessage(errorCode)}");
                    OnPlaybackError($"播放启动失败: Channel状态异常 (state={channelState})");
                    ReleasePreparedPlaybackResources(preparedStream, preparedStreamProvider, preparedCacheManager);
                    preparedStream = 0;
                    preparedStreamProvider = null;
                    preparedCacheManager = null;
                    RestoreIdleAfterFailedPlayback(transitionedToLoading);
                    return false;
                }

                CommitPreparedPlayback(song, preparedStream, preparedStreamProvider, preparedCacheManager);
                committed = true;
                preparedStream = 0;
                preparedStreamProvider = null;
                preparedCacheManager = null;

                bool stateChanged = TransitionToPlayingAfterCommit();
                Debug.WriteLine($"[BassAudioEngine] 状态转换结果: {stateChanged}");

                OnPlaybackStarted(song);
                Debug.WriteLine("[BassAudioEngine] ✓ PlayAsync完成，返回true");
                return true;
            }
            catch (OperationCanceledException)
            {
                if (!committed)
                {
                    ReleasePreparedPlaybackResources(preparedStream, preparedStreamProvider, preparedCacheManager);
                    RestoreIdleAfterFailedPlayback(transitionedToLoading);
                }

                return false;
            }
            catch (Exception ex)
            {
                if (!committed)
                {
                    ReleasePreparedPlaybackResources(preparedStream, preparedStreamProvider, preparedCacheManager);
                    RestoreIdleAfterFailedPlayback(transitionedToLoading);
                }

                DebugLogger.LogException("BassAudioEngine", ex, "PlayAsync failure");
                OnPlaybackError($"播放错误: {ex.Message}");
                return false;
            }
            finally
            {
                if (lockTaken)
                {
                    try
                    {
                        _playSemaphore.Release();
                    }
                    catch (ObjectDisposedException)
                    {
                        // 已释放时忽略
                    }
                }
            }
        }

        private bool HasCurrentStream()
        {
            lock (_syncRoot)
            {
                return _currentStream != 0;
            }
        }

        private void RestoreIdleAfterFailedPlayback(bool shouldRestore)
        {
            if (!shouldRestore)
            {
                return;
            }

            lock (_syncRoot)
            {
                if (_currentStream != 0)
                {
                    return;
                }
            }

            if (_stateMachine.CurrentState != PlaybackState.Idle)
            {
                _stateMachine.TransitionTo(PlaybackState.Idle);
            }
        }

        private bool TransitionToPlayingAfterCommit()
        {
            PlaybackState currentState = _stateMachine.CurrentState;
            if (currentState == PlaybackState.Idle || currentState == PlaybackState.Stopped)
            {
                _stateMachine.TransitionTo(PlaybackState.Loading);
            }

            return _stateMachine.TransitionTo(PlaybackState.Playing);
        }

        private bool ValidatePreparedStream(int stream, long expectedSize)
        {
            BASS_CHANNELINFO channelInfo = new BASS_CHANNELINFO();
            bool infoSuccess = BASS_ChannelGetInfo(stream, ref channelInfo);
            if (infoSuccess)
            {
                Debug.WriteLine("[BassAudioEngine] Channel信息:");
                Debug.WriteLine($"[BassAudioEngine]   采样率: {channelInfo.freq} Hz");
                Debug.WriteLine($"[BassAudioEngine]   声道数: {channelInfo.chans}");
                Debug.WriteLine($"[BassAudioEngine]   类型: 0x{channelInfo.ctype:X8}");
                Debug.WriteLine($"[BassAudioEngine]   插件: {channelInfo.plugin}");
                Debug.WriteLine($"[BassAudioEngine]   标志: 0x{channelInfo.flags:X8}");
            }
            else
            {
                Debug.WriteLine("[BassAudioEngine] ⚠️ 无法获取Channel信息");
            }

            long channelLength = BASS_ChannelGetLength(stream, BASS_POS_BYTE);
            Debug.WriteLine($"[BassAudioEngine] Channel长度: {channelLength} bytes");

            if (channelLength <= 0)
            {
                Debug.WriteLine($"[BassAudioEngine] ❌❌❌ Channel长度无效: {channelLength}，无法播放");
                OnPlaybackError($"音频格式识别失败: stream长度无效 ({channelLength} bytes)");
                return false;
            }

            if (expectedSize > 0 && channelLength < expectedSize * 0.5)
            {
                Debug.WriteLine($"[BassAudioEngine] ⚠️ Channel长度({channelLength})明显小于预期({expectedSize})，可能识别不完整");
                Debug.WriteLine("[BassAudioEngine] ⚠️ 继续播放，但可能提前结束");
            }

            return true;
        }

        private void CommitPreparedPlayback(
            SongInfo song,
            int newStream,
            BassStreamProvider newStreamProvider,
            SmartCacheManager newCacheManager)
        {
            BassStreamProvider? oldStreamProvider = null;
            SmartCacheManager? oldCacheManager = null;
            int oldStream = 0;
            int oldEndSyncHandle = 0;
            int oldNearEndSyncHandle = 0;
            var pendingPreload = TakeGaplessPreload();

            if (pendingPreload != null && pendingPreload.Data.StreamHandle != newStream)
            {
                ReleasePreloadedResources(pendingPreload);
            }

            lock (_syncRoot)
            {
                _positionMonitorCts?.Cancel();
                _positionMonitorCts?.Dispose();
                _positionMonitorCts = null;
                _positionMonitorTask = null;

                oldStream = _currentStream;
                oldStreamProvider = _currentStreamProvider;
                if (oldStreamProvider != null)
                {
                    UnregisterStreamProviderCallbacks(oldStreamProvider);
                }

                oldCacheManager = _currentCacheManager;
                oldEndSyncHandle = _endSyncHandle;
                oldNearEndSyncHandle = _nearEndSyncHandle;

                _endSyncHandle = 0;
                _nearEndSyncHandle = 0;
                _currentStream = newStream;
                _currentStreamProvider = newStreamProvider;
                _currentCacheManager = newCacheManager;
                _currentSong = song;
            }

            RegisterStreamProviderCallbacks(newStreamProvider);
            AttachCacheManager(newCacheManager);

            SetupSyncs();
            Debug.WriteLine("[BassAudioEngine] ✓ SetupSyncs完成");

            StartPositionMonitor();
            Debug.WriteLine("[BassAudioEngine] ✓ StartPositionMonitor完成");

            newCacheManager.SetPlayingState();

            ReleaseDetachedPlaybackResources(
                oldStream,
                oldStreamProvider,
                oldCacheManager,
                oldEndSyncHandle,
                oldNearEndSyncHandle,
                "CommitPreparedPlayback");

            _bufferingStateSinceUtc = DateTime.MinValue;
            _bufferingTimeoutTriggered = false;
        }

        private void ReleasePreparedPlaybackResources(int stream, BassStreamProvider? streamProvider, SmartCacheManager? cacheManager)
        {
            if (streamProvider != null)
            {
                try
                {
                    streamProvider.AbortPendingReads();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[BassAudioEngine] Prepared AbortPendingReads failed: {ex.Message}");
                }
            }

            if (stream != 0)
            {
                try
                {
                    BASS_ChannelStop(stream);
                    BASS_StreamFree(stream);
                    Debug.WriteLine($"[BassAudioEngine] Released prepared stream: {stream}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[BassAudioEngine] Release prepared stream failed: {ex.Message}");
                }
            }

            if (streamProvider != null)
            {
                try
                {
                    UnregisterStreamProviderCallbacks(streamProvider);
                    streamProvider.Dispose();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[BassAudioEngine] Dispose prepared StreamProvider failed: {ex.Message}");
                }
            }

            if (cacheManager != null)
            {
                try
                {
                    DetachCacheManager(cacheManager);
                    cacheManager.Dispose();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[BassAudioEngine] Dispose prepared CacheManager failed: {ex.Message}");
                }
            }
        }

        private void ReleaseDetachedPlaybackResources(
            int oldStream,
            BassStreamProvider? oldStreamProvider,
            SmartCacheManager? oldCacheManager,
            int oldEndSyncHandle,
            int oldNearEndSyncHandle,
            string reason)
        {
            if (oldStreamProvider != null)
            {
                try
                {
                    oldStreamProvider.AbortPendingReads();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[BassAudioEngine] {reason}: AbortPendingReads failed: {ex.Message}");
                }
            }

            if (oldStream != 0)
            {
                if (oldEndSyncHandle != 0)
                {
                    BASS_ChannelRemoveSync(oldStream, oldEndSyncHandle);
                }

                if (oldNearEndSyncHandle != 0)
                {
                    BASS_ChannelRemoveSync(oldStream, oldNearEndSyncHandle);
                }

                Debug.WriteLine($"[BassAudioEngine] {reason}: stopping detached stream outside lock: {oldStream}");
                BASS_ChannelStop(oldStream);
            }

            if (oldStream != 0 || oldStreamProvider != null || oldCacheManager != null)
            {
                Task.Run(async () =>
                {
                    try
                    {
                        Debug.WriteLine($"[BassAudioEngine] {reason}: async release start (stream={oldStream})");

                        if (oldStream != 0)
                        {
                            BASS_StreamFree(oldStream);
                            Debug.WriteLine($"[BassAudioEngine] {reason}: BASS_StreamFree called: {oldStream}");
                        }

                        await Task.Delay(200);

                        if (oldStreamProvider != null)
                        {
                            oldStreamProvider.Dispose();
                            Debug.WriteLine($"[BassAudioEngine] {reason}: disposed StreamProvider");
                        }

                        if (oldCacheManager != null)
                        {
                            DetachCacheManager(oldCacheManager);
                            oldCacheManager.Dispose();
                            Debug.WriteLine($"[BassAudioEngine] {reason}: disposed CacheManager");
                        }

                        Debug.WriteLine($"[BassAudioEngine] {reason}: async release completed");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[BassAudioEngine] {reason}: release error: {ex.Message}");
                    }
                });
            }
        }

        public bool Stop()
        {
            StopInternal(raiseEvent: true);
            _stateMachine.TransitionTo(PlaybackState.Stopped);
            return true;
        }

        public void Pause()
        {
            if (_currentStream == 0)
            {
                return;
            }

            if (BASS_ChannelPause(_currentStream))
            {
                _stateMachine.TransitionTo(PlaybackState.Paused);
            }
        }

        public void Resume()
        {
            if (_currentStream == 0)
            {
                return;
            }

            if (BASS_ChannelPlay(_currentStream, false))
            {
                _stateMachine.TransitionTo(PlaybackState.Playing);
            }
        }

        public async Task<bool> PauseWithFadeAsync(int fadeMilliseconds, CancellationToken cancellationToken)
        {
            if (_currentStream == 0)
            {
                return false;
            }

            if (!BASS_ChannelSlideAttribute(_currentStream, BASS_ATTRIB_VOL, 0f, fadeMilliseconds))
            {
                Pause();
                return true;
            }

            try
            {
                await Task.Delay(fadeMilliseconds, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            Pause();
            ApplyVolume();
            return true;
        }

        public async Task<bool> ResumeWithFadeAsync(int fadeMilliseconds, CancellationToken cancellationToken)
        {
            if (_currentStream == 0)
            {
                return false;
            }

            BASS_ChannelSetAttribute(_currentStream, BASS_ATTRIB_VOL, 0f);
            if (!BASS_ChannelPlay(_currentStream, false))
            {
                return false;
            }

            if (!BASS_ChannelSlideAttribute(_currentStream, BASS_ATTRIB_VOL, _volume, fadeMilliseconds))
            {
                ApplyVolume();
                return true;
            }

            try
            {
                await Task.Delay(fadeMilliseconds, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            _stateMachine.TransitionTo(PlaybackState.Playing);
            return true;
        }

        public async Task<bool> SetPositionWithFadeAsync(double seconds, int fadeMilliseconds, CancellationToken cancellationToken)
        {
            bool wasPlaying = IsPlaying;

            if (wasPlaying)
            {
                await PauseWithFadeAsync(fadeMilliseconds / 2, cancellationToken).ConfigureAwait(false);
            }

            bool setSuccess = SetPosition(seconds);

            if (wasPlaying)
            {
                await ResumeWithFadeAsync(fadeMilliseconds / 2, cancellationToken).ConfigureAwait(false);
            }

            return setSuccess;
        }

        public bool SetPosition(double seconds)
        {
            if (_currentStream == 0 || !double.IsFinite(seconds) || seconds < 0) return false;

            long targetBytes = BASS_ChannelSeconds2Bytes(_currentStream, seconds);
            bool success = BASS_ChannelSetPosition(_currentStream, targetBytes, BASS_POS_BYTE);

            if (success && _currentCacheManager != null)
            {
                _currentCacheManager.UpdatePlaybackPosition(GetEstimatedFileOffset(seconds));
            }

            return success;
        }

        /// <summary>
        /// 跳转到指定位置（智能等待缓存数据就绪）
        /// 用于远距离跳转场景，确保数据可用后再执行跳转
        /// </summary>
        /// <param name="seconds">目标位置（秒）</param>
        /// <param name="timeoutMs">等待超时时间（毫秒），默认 60 秒</param>
        /// <param name="cancellationToken">取消令牌（支持取消旧的跳转命令）</param>
        /// <returns>是否成功跳转</returns>
        public async Task<bool> SetPositionWithCacheWaitAsync(
            double seconds,
            int timeoutMs = 12000,
            CancellationToken cancellationToken = default,
            bool waitTargetOnly = false)
        {
            int stream = _currentStream;
            var cache = _currentCacheManager;
            if (stream == 0 || !double.IsFinite(seconds) || seconds < 0 || cancellationToken.IsCancellationRequested) return false;
            // Decoder positions are PCM bytes. Cache positions are encoded file bytes.
            long pcmPosition = BASS_ChannelSeconds2Bytes(stream, seconds);
            long filePosition = GetEstimatedFileOffset(seconds);
            if (pcmPosition < 0) return false;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(Math.Clamp(timeoutMs, 1, 60000));
            try
            {
                if (cache != null)
                {
                    cache.UpdatePlaybackPosition(filePosition);
                    // This estimate only warms the cache. The decoder's FILESEEK/READ
                    // callbacks determine the real encoded offset, including VBR/indexes.
                    await cache.EnsurePositionAsync(filePosition, deadline.Token, allowRangeRescue: true).ConfigureAwait(false);
                    if (!await cache.WaitForPositionReadyAsync(filePosition, Math.Max(1, timeoutMs), deadline.Token,
                        forPlayback: false).ConfigureAwait(false)) return false;
                }
                // A cache hit must not cause synchronous native I/O on the UI thread.
                return await Task.Run(() =>
                {
                    if (deadline.IsCancellationRequested || _currentStream != stream || !ReferenceEquals(_currentCacheManager, cache)) return false;
                    bool success = BASS_ChannelSetPosition(stream, pcmPosition, BASS_POS_BYTE);
                    if (success && !deadline.IsCancellationRequested && _currentStream == stream)
                    {
                        if (cache != null && _currentStreamProvider != null)
                            cache.UpdatePlaybackPosition(Math.Clamp(_currentStreamProvider.CurrentPosition, 0, cache.TotalSize - 1));
                        return true;
                    }
                    return false;
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return false; }
        }

        public double GetPosition()
        {
            if (_currentStream == 0)
            {
                return 0;
            }

            long position = BASS_ChannelGetPosition(_currentStream, BASS_POS_BYTE);
            return BASS_ChannelBytes2Seconds(_currentStream, position);
        }

        public double GetDuration()
        {
            if (_currentStream == 0)
            {
                return 0;
            }

            long length = BASS_ChannelGetLength(_currentStream, BASS_POS_BYTE);
            return BASS_ChannelBytes2Seconds(_currentStream, length);
        }

        public PlaybackState GetPlaybackState()
        {
            return _stateMachine.CurrentState;
        }

        /// <summary>Encoded-byte estimate for prefetch only; never pass this to BASS_ChannelSetPosition.</summary>
        public long GetEstimatedFileOffset(double seconds)
        {
            var cache = _currentCacheManager;
            return EstimateFileOffset(seconds, GetDuration(), cache?.TotalSize ?? 0);
        }

        internal static long EstimateFileOffset(double seconds, double duration, long fileSize)
        {
            if (!double.IsFinite(seconds) || !double.IsFinite(duration) || seconds <= 0 || duration <= 0 || fileSize <= 0) return 0;
            return (long)Math.Clamp(seconds / duration * fileSize, 0, fileSize - 1);
        }

        public long GetBytesFromSeconds(double seconds)
        {
            if (_currentStream == 0)
            {
                return 0;
            }

            return BASS_ChannelSeconds2Bytes(_currentStream, seconds);
        }

        public void SetVolume(float volume)
        {
            _volume = Math.Max(0f, Math.Min(volume, 1f)); // 为保持兼容，避免依赖 Math.Clamp
            ApplyVolume();
        }

        /// <summary>
        /// 应用快速 Seek 淡入效果（10ms）
        /// 用于快进/快退时减少声音突变
        /// </summary>
        public void ApplySeekFadeIn()
        {
            if (_currentStream == 0)
            {
                return;
            }

            // 立即将音量设为 0
            BASS_ChannelSetAttribute(_currentStream, BASS_ATTRIB_VOL, 0f);

            // 10ms 淡入到目标音量
            BASS_ChannelSlideAttribute(_currentStream, BASS_ATTRIB_VOL, _volume, 10);
        }

        #endregion

        #region 释放

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            StopInternal(raiseEvent: false);

            _httpClient.Dispose();

            lock (_syncRoot)
            {
                var devices = _initializedDeviceIndices.ToList();
                foreach (var deviceIndex in devices)
                {
                    try
                    {
                        ReleaseDevice(deviceIndex);
                    }
                    catch
                    {
                        // ignore release exceptions during dispose
                    }
                }

                _initializedDeviceIndices.Clear();
                _currentDeviceIndex = 0;
                _isInitialized = false;
            }

            _stateMachine.StateChanged -= StateMachineOnStateChanged;
            _playSemaphore.Dispose();
        }

        #endregion

        #region 私有实现

        private void InitializeBass(string? preferredDeviceId)
        {
            Debug.WriteLine("═══════════════════════════════════════════════════════");
            Debug.WriteLine("[BassAudioEngine] 开始初始化BASS音频引擎 (64-bit)");
            Debug.WriteLine("═══════════════════════════════════════════════════════");

            lock (_syncRoot)
            {
                var descriptors = EnumerateBassDevices();
                if (descriptors.Count == 0)
                {
                    throw new InvalidOperationException("系统未检测到可用的声音输出设备");
                }

                var selectedDescriptor = ResolvePreferredDescriptor(preferredDeviceId, descriptors, out string resolvedId);

                if (!EnsureDeviceInitialized(selectedDescriptor.Index, out var error))
                {
                    throw new InvalidOperationException($"BASS 初始化失败: {error ?? "未知错误"}");
                }

                _currentDeviceIndex = selectedDescriptor.Index;
                _activeOutputDeviceId = resolvedId;
                _isInitialized = true;

                Debug.WriteLine($"[BassAudioEngine] ✓ 绑定输出设备: {selectedDescriptor.Name} (#{selectedDescriptor.Index})");
            }

            Debug.WriteLine("═══════════════════════════════════════════════════════");
            Debug.WriteLine("[BassAudioEngine] ✓ BASS音频引擎初始化完成");
            Debug.WriteLine("═══════════════════════════════════════════════════════");
        }

        private async Task<bool> TryEnterPlaySemaphoreAsync(CancellationToken cancellationToken)
        {
            if (_disposed)
            {
                return false;
            }

            try
            {
                await _playSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        private void LoadBassFlacPlugin()
        {
            try
            {
                // 优先从 libs 目录加载（新的依赖布局），找不到时回退到根目录
                string searchRoot = Directory.Exists(PathHelper.LibsDirectory)
                    ? PathHelper.LibsDirectory
                    : PathHelper.ApplicationRootDirectory;
                string bassflacPath = PathHelper.ResolveFromLibsOrBase("bassflac.dll");
                Debug.WriteLine($"[BassAudioEngine]   查找路径: {bassflacPath}");
                Debug.WriteLine($"[BassAudioEngine]   搜索根目录: {searchRoot}");

                // 检查文件是否存在
                if (!File.Exists(bassflacPath))
                {
                    Debug.WriteLine("[BassAudioEngine] ⚠️ bassflac.dll 文件不存在！");
                    Debug.WriteLine("[BassAudioEngine]   FLAC格式播放将不可用");
                    return;
                }

                // 验证文件信息
                var fileInfo = new FileInfo(bassflacPath);
                Debug.WriteLine($"[BassAudioEngine]   文件大小: {fileInfo.Length:N0} bytes");
                Debug.WriteLine($"[BassAudioEngine]   修改时间: {fileInfo.LastWriteTime:yyyy-MM-dd HH:mm:ss}");

                // 加载插件 (必须传递BASS_UNICODE标志，因为使用CharSet.Unicode)
                string fullPath = Path.GetFullPath(bassflacPath);
                int pluginHandle = BASS_PluginLoad(fullPath, BASS_UNICODE);

                if (pluginHandle == 0)
                {
                    int error = BASS_ErrorGetCode();
                    string errorMsg = GetErrorMessage(error);
                    Debug.WriteLine($"[BassAudioEngine] ❌ BASSFLAC插件加载失败");
                    Debug.WriteLine($"[BassAudioEngine]   错误码: {error}");
                    Debug.WriteLine($"[BassAudioEngine]   错误信息: {errorMsg}");
                    Debug.WriteLine($"[BassAudioEngine]   提示: 请确保bassflac.dll与程序架构匹配(64-bit)");
                    return;
                }

                Debug.WriteLine($"[BassAudioEngine] ✓ BASSFLAC插件加载成功");
                Debug.WriteLine($"[BassAudioEngine]   插件句柄: {pluginHandle}");
                Debug.WriteLine($"[BassAudioEngine]   支持格式: FLAC (无损音频)");
                _bassFlacLoaded = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BassAudioEngine] ❌ 加载BASSFLAC插件异常");
                Debug.WriteLine($"[BassAudioEngine]   异常类型: {ex.GetType().Name}");
                Debug.WriteLine($"[BassAudioEngine]   异常消息: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Debug.WriteLine($"[BassAudioEngine]   内部异常: {ex.InnerException.Message}");
                }
            }
        }

        private async Task<SmartCacheManager> GetOrCreateCacheManagerAsync(SongInfo song, long totalSize, CancellationToken token, IDictionary<string, string>? headers)
        {
            var manager = new SmartCacheManager(song.Id, song.Url, totalSize, _httpClient, PreferSequentialFull(song, totalSize), headers);
            bool initialized = await manager.InitializeAsync(token).ConfigureAwait(false);

            if (!initialized)
            {
                manager.Dispose();
                throw new InvalidOperationException("缓存初始化失败");
            }

            return manager;
        }

        private bool PreferSequentialFull(SongInfo song, long totalSize)
        {
            // 估算码率：size(bytes)*8 / duration(s) /1000 = kbps
            if (song != null && song.Duration > 0 && totalSize > 0)
            {
                double kbps = (totalSize * 8.0) / song.Duration / 1000.0;
                if (kbps >= 512) // 高码率或无损
                {
                    return true;
                }
            }

            // 大文件也倾向顺序整流
            return totalSize >= 12 * 1024 * 1024; // 12MB+
        }

        private void AttachCacheManager(SmartCacheManager manager)
        {
            manager.BufferingStateChanged += CacheManagerOnBufferingStateChanged;
        }

        private void DetachCacheManager(SmartCacheManager manager)
        {
            manager.BufferingStateChanged -= CacheManagerOnBufferingStateChanged;
        }

        private void CacheManagerOnBufferingStateChanged(object? sender, BufferingState state)
        {
            DispatchEvent(BufferingStateChanged, this, state);
        }

        private void SetupSyncs()
        {
            if (_currentStream == 0)
            {
                return;
            }

            if (_endSyncHandle != 0)
            {
                BASS_ChannelRemoveSync(_currentStream, _endSyncHandle);
                _endSyncHandle = 0;
            }

            if (_nearEndSyncHandle != 0)
            {
                BASS_ChannelRemoveSync(_currentStream, _nearEndSyncHandle);
                _nearEndSyncHandle = 0;
            }

            // ⭐⭐⭐ 使用 BASS_SYNC_MIXTIME 实现真正的 gapless - 在混音时立即触发，立即切换
            // 配合 OnStreamEnded 中的立即停止逻辑，实现无缝播放
            _endSyncHandle = BASS_ChannelSetSync(_currentStream,
                (int)(BASS_SYNC_END | BASS_SYNC_MIXTIME),
                0,
                _endSyncProc,
                IntPtr.Zero);

            Debug.WriteLine($"[BassAudioEngine] ⭐ 设置 MIXTIME sync，stream={_currentStream}, handle={_endSyncHandle}");

            // ⭐ 不再设置 NearEnd sync - 改用进度监控触发预加载（播放到50%时）
        }

        private void StartPositionMonitor()
        {
            _positionMonitorCts?.Cancel();
            _positionMonitorCts?.Dispose();

            _positionMonitorCts = new CancellationTokenSource();
            var token = _positionMonitorCts.Token;
            _bufferingStateSinceUtc = DateTime.MinValue;
            _bufferingTimeoutTriggered = false;

            bool endChunksRequested = false;

            _positionMonitorTask = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    if (_currentStream != 0)
                    {
                        int activeState = BASS_ChannelIsActive(_currentStream);

                        if (activeState == BASS_ACTIVE_STALLED || activeState == BASS_ACTIVE_PAUSED_DEVICE)
                        {
                            if (_bufferingStateSinceUtc == DateTime.MinValue)
                            {
                                _bufferingStateSinceUtc = DateTime.UtcNow;
                            }

                            _stateMachine.TransitionTo(PlaybackState.Buffering);

                            if (activeState == BASS_ACTIVE_PAUSED_DEVICE && DateTime.UtcNow - _lastDeviceResumeAttemptUtc > TimeSpan.FromSeconds(2))
                            {
                                _lastDeviceResumeAttemptUtc = DateTime.UtcNow;
                                EnsureDeviceContext();
                                bool resumed = BASS_Start();
                                Debug.WriteLine($"[BassAudioEngine] Attempted BASS_Start after device pause: {resumed}");
                            }

                            if (!_bufferingTimeoutTriggered && DateTime.UtcNow - _bufferingStateSinceUtc >= MaxBufferingDuration)
                            {
                                _bufferingTimeoutTriggered = true;
                                Debug.WriteLine($"[BassAudioEngine] Buffering timeout exceeded {MaxBufferingDuration.TotalSeconds:F0}s, stopping playback");
                                StopInternal(raiseEvent: false);
                                _stateMachine.TransitionTo(PlaybackState.Idle);
                                OnPlaybackError($"缓冲超时（{MaxBufferingDuration.TotalSeconds:F0} 秒）");
                                break;
                            }
                        }
                        else
                        {
                            _bufferingStateSinceUtc = DateTime.MinValue;
                            _bufferingTimeoutTriggered = false;

                            if (activeState == BASS_ACTIVE_PLAYING && _stateMachine.CurrentState == PlaybackState.Buffering)
                            {
                                _stateMachine.TransitionTo(PlaybackState.Playing);
                            }
                        }

                        long positionBytes = BASS_ChannelGetPosition(_currentStream, BASS_POS_BYTE);
                        long totalBytes = BASS_ChannelGetLength(_currentStream, BASS_POS_BYTE);
                        double seconds = BASS_ChannelBytes2Seconds(_currentStream, positionBytes);

                        var positionCache = _currentCacheManager;
                        var positionProvider = _currentStreamProvider;
                        if (positionCache != null && positionProvider != null)
                            positionCache.UpdatePlaybackPosition(Math.Clamp(positionProvider.CurrentPosition, 0, positionCache.TotalSize - 1));
                        PositionChanged?.Invoke(this, TimeSpan.FromSeconds(seconds));

                        if (!endChunksRequested && totalBytes > 0 && positionBytes > 0)
                        {
                            double progress = (double)positionBytes / totalBytes;
                            if (progress >= 0.85)
                            {
                                endChunksRequested = true;
                                Debug.WriteLine($"[BassAudioEngine] Playback progress {progress:P1}, proactively requesting tail chunks");

                                _ = Task.Run(async () =>
                                {
                                    try
                                    {
                                        var cacheManager = _currentCacheManager;
                                        if (cacheManager == null)
                                        {
                                            return;
                                        }

                                        long nearEndPosition = Math.Max(0, cacheManager.TotalSize - 2L * 1024 * 1024);
                                        cacheManager.UpdatePlaybackPosition(nearEndPosition);
                                        await cacheManager.PrefetchAroundAsync(nearEndPosition, aheadChunks: 8, token, allowRangeRescue: true).ConfigureAwait(false);
                                        Debug.WriteLine($"[BassAudioEngine] Requested near-end cache position: {nearEndPosition}/{totalBytes}");
                                    }
                                    catch (OperationCanceledException)
                                    {
                                        // Playback was stopped or switched.
                                    }
                                    catch (Exception ex)
                                    {
                                        Debug.WriteLine($"[BassAudioEngine] Failed to request near-end cache: {ex.Message}");
                                    }
                                }, token);
                            }
                        }
                    }

                    try
                    {
                        await Task.Delay(200, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }, token);
        }

        private void StopInternal(bool raiseEvent)
        {
            BassStreamProvider? oldStreamProvider = null;
            SmartCacheManager? oldCacheManager = null;
            int oldStream = 0;
            int oldEndSyncHandle = 0;
            int oldNearEndSyncHandle = 0;
            var pendingPreload = TakeGaplessPreload();

            if (pendingPreload != null)
            {
                ReleasePreloadedResources(pendingPreload);
            }

            lock (_syncRoot)
            {
                _positionMonitorCts?.Cancel();
                _positionMonitorCts?.Dispose();
                _positionMonitorCts = null;
                _positionMonitorTask = null;

                oldStream = _currentStream;
                oldStreamProvider = _currentStreamProvider;
                if (oldStreamProvider != null)
                {
                    UnregisterStreamProviderCallbacks(oldStreamProvider);
                }

                oldCacheManager = _currentCacheManager;
                oldEndSyncHandle = _endSyncHandle;
                oldNearEndSyncHandle = _nearEndSyncHandle;

                _endSyncHandle = 0;
                _nearEndSyncHandle = 0;
                _currentStream = 0;
                _currentStreamProvider = null;
                _currentCacheManager = null;
                _currentSong = null;
            }

            if (oldStreamProvider != null)
            {
                try
                {
                    oldStreamProvider.AbortPendingReads();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[BassAudioEngine] AbortPendingReads failed: {ex.Message}");
                }
            }

            if (oldStream != 0)
            {
                if (oldEndSyncHandle != 0)
                {
                    BASS_ChannelRemoveSync(oldStream, oldEndSyncHandle);
                }

                if (oldNearEndSyncHandle != 0)
                {
                    BASS_ChannelRemoveSync(oldStream, oldNearEndSyncHandle);
                }

                Debug.WriteLine($"[BassAudioEngine] StopInternal stopping stream outside lock: {oldStream}");
                BASS_ChannelStop(oldStream);
            }

            // Release resources asynchronously so native callbacks can unwind safely.
            if (oldStream != 0 || oldStreamProvider != null || oldCacheManager != null)
            {
                Task.Run(async () =>
                {
                    try
                    {
                        Debug.WriteLine($"[BassAudioEngine] StopInternal: async release start (stream={oldStream})");

                        if (oldStream != 0)
                        {
                            BASS_StreamFree(oldStream);
                            Debug.WriteLine($"[BassAudioEngine] StopInternal: BASS_StreamFree called: {oldStream}");
                        }

                        Debug.WriteLine("[BassAudioEngine] StopInternal: waiting 200ms before managed dispose");
                        await Task.Delay(200);
                        Debug.WriteLine("[BassAudioEngine] StopInternal: disposing managed resources");

                        if (oldStreamProvider != null)
                        {
                            oldStreamProvider.Dispose();
                            Debug.WriteLine("[BassAudioEngine] StopInternal: disposed StreamProvider");
                        }

                        if (oldCacheManager != null)
                        {
                            DetachCacheManager(oldCacheManager);
                            oldCacheManager.Dispose();
                            Debug.WriteLine("[BassAudioEngine] StopInternal: disposed CacheManager");
                        }

                        Debug.WriteLine("[BassAudioEngine] StopInternal: async release completed");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[BassAudioEngine] StopInternal release error: {ex.Message}");
                    }
                });
            }

            if (raiseEvent)
            {
                DispatchEvent(PlaybackStopped, this, EventArgs.Empty);
            }

            _bufferingStateSinceUtc = DateTime.MinValue;
            _bufferingTimeoutTriggered = false;
        }


        private void RemoveSyncs()
        {
            if (_currentStream != 0)
            {
                if (_endSyncHandle != 0)
                {
                    BASS_ChannelRemoveSync(_currentStream, _endSyncHandle);
                    _endSyncHandle = 0;
                }

                if (_nearEndSyncHandle != 0)
                {
                    BASS_ChannelRemoveSync(_currentStream, _nearEndSyncHandle);
                    _nearEndSyncHandle = 0;
                }
            }
        }

        private void StopPositionMonitor()
        {
            _positionMonitorCts?.Cancel();
            _positionMonitorCts?.Dispose();
            _positionMonitorCts = null;
            _positionMonitorTask = null;
        }

        private void ApplyVolume()
        {
            if (_currentStream != 0)
            {
                BASS_ChannelSetAttribute(_currentStream, BASS_ATTRIB_VOL, _volume);
            }
        }

        /// <summary>
        /// ⭐ BASS sync 回调 - 歌曲播放结束时触发
        /// </summary>
        private void OnStreamEnded(int handle, int channel, int data, IntPtr user)
        {
            Debug.WriteLine($"[BassAudioEngine] SYNC_END callback fired (stream={channel})");

            int oldStream;
            BassStreamProvider? oldStreamProvider;
            SmartCacheManager? oldCacheManager;
            SongInfo? finishedSong;

            lock (_syncRoot)
            {
                if (_currentStream == 0 || channel == 0 || channel != _currentStream)
                {
                    Debug.WriteLine($"[BassAudioEngine] Ignoring stale SYNC_END callback: callback={channel}, current={_currentStream}");
                    return;
                }

                oldStream = _currentStream;
                oldStreamProvider = _currentStreamProvider;
                if (oldStreamProvider != null)
                {
                    UnregisterStreamProviderCallbacks(oldStreamProvider);
                }
                oldCacheManager = _currentCacheManager;
                finishedSong = _currentSong;

                _positionMonitorCts?.Cancel();
                _positionMonitorCts?.Dispose();
                _positionMonitorCts = null;
                _positionMonitorTask = null;

                _currentStream = 0;
                _currentStreamProvider = null;
                _currentCacheManager = null;
                _currentSong = null;
                _endSyncHandle = 0;
                _nearEndSyncHandle = 0;
            }

            bool loopOneMode = _playMode == PlayMode.LoopOne;
            var gaplessEntry = TakeGaplessPreload();
            if (loopOneMode && gaplessEntry != null)
            {
                ReleasePreloadedResources(gaplessEntry);
                gaplessEntry = null;
            }

            bool gaplessStarted = false;
            SongInfo? nextSong = null;

            if (gaplessEntry != null)
            {
                gaplessStarted = TryStartGaplessPlayback(gaplessEntry, finishedSong, out nextSong);
                if (!gaplessStarted)
                {
                    RestoreGaplessPreload(gaplessEntry);
                }
            }

            if (oldStream != 0)
            {
                Debug.WriteLine($"[BassAudioEngine] Stopping finished stream immediately: {oldStream}");
                oldStreamProvider?.AbortPendingReads();
                BASS_ChannelStop(oldStream);
                Debug.WriteLine("[BassAudioEngine] Finished stream stopped");
            }

            if (!gaplessStarted)
            {
                _stateMachine.TransitionTo(PlaybackState.Idle);

                if (finishedSong != null)
                {
                    Debug.WriteLine($"[BassAudioEngine] Raising PlaybackEnded: {finishedSong.Name}");
                    DispatchEvent(PlaybackEnded, this, finishedSong);
                }
            }

            Task.Run(async () =>
            {
                try
                {
                    Debug.WriteLine($"[BassAudioEngine] Async release for ended stream start (stream={oldStream})");

                    if (oldStream != 0)
                    {
                        BASS_StreamFree(oldStream);
                        Debug.WriteLine($"[BassAudioEngine] BASS_StreamFree called for ended stream: {oldStream}");
                    }

                    Debug.WriteLine("[BassAudioEngine] Waiting 200ms before releasing managed ended-stream resources");
                    await Task.Delay(200);
                    Debug.WriteLine("[BassAudioEngine] Releasing managed ended-stream resources");

                    if (oldStreamProvider != null)
                    {
                        oldStreamProvider.Dispose();
                        Debug.WriteLine("[BassAudioEngine] Disposed ended StreamProvider");
                    }

                    if (oldCacheManager != null)
                    {
                        DetachCacheManager(oldCacheManager);
                        oldCacheManager.Dispose();
                        Debug.WriteLine("[BassAudioEngine] Disposed ended CacheManager");
                    }

                    Debug.WriteLine("[BassAudioEngine] Async release for ended stream completed");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[BassAudioEngine] Ended stream release error: {ex.Message}");
                }
            });
        }


        private bool TryStartGaplessPlayback(GaplessPreloadEntry entry, SongInfo? previousSong, out SongInfo? nextSong)
        {
            nextSong = null;
            if (entry == null || entry.Data == null || entry.Data.StreamHandle == 0 || entry.Data.StreamProvider == null || entry.Data.CacheManager == null)
            {
                return false;
            }

            var data = entry.Data;
            int newStreamHandle = data.StreamHandle;

            lock (_syncRoot)
            {
                _currentStream = newStreamHandle;
                _currentStreamProvider = data.StreamProvider;
                _currentCacheManager = data.CacheManager;
                _currentSong = entry.Song;
            }

            RegisterStreamProviderCallbacks(data.StreamProvider);
            nextSong = entry.Song;

            AttachCacheManager(data.CacheManager);

            BASS_ChannelSetPosition(newStreamHandle, 0, BASS_POS_BYTE);
            ApplyVolume();

            _endSyncHandle = 0;
            _nearEndSyncHandle = 0;
            SetupSyncs();
            StartPositionMonitor();

            bool playResult = BASS_ChannelPlay(newStreamHandle, true);
            if (!playResult)
            {
                int error = BASS_ErrorGetCode();
                Debug.WriteLine($"[BassAudioEngine] ❌ Gapless 启动失败: error={error}");

                lock (_syncRoot)
                {
                    _currentStream = 0;
                    UnregisterStreamProviderCallbacks(_currentStreamProvider);
                    _currentStreamProvider = null;
                    _currentCacheManager = null;
                    _currentSong = null;
                }

                DetachCacheManager(data.CacheManager);
                return false;
            }

            data.CacheManager.SetPlayingState();
            _stateMachine.TransitionTo(PlaybackState.Playing);
            OnPlaybackStarted(entry.Song);

            DispatchEvent(GaplessTransitionCompleted, this, new GaplessTransitionEventArgs(previousSong, entry.Song));

            return true;
        }

        private GaplessPreloadEntry? TakeGaplessPreload()
        {
            lock (_gaplessPreloadLock)
            {
                var entry = _gaplessPreload;
                _gaplessPreload = null;
                return entry;
            }
        }

        private void RestoreGaplessPreload(GaplessPreloadEntry entry)
        {
            if (entry == null)
            {
                return;
            }

            lock (_gaplessPreloadLock)
            {
                if (_gaplessPreload == null)
                {
                    _gaplessPreload = entry;
                }
                else
                {
                    ReleasePreloadedResources(entry);
                }
            }
        }

        private void ReleasePreloadedResources(GaplessPreloadEntry entry)
        {
            try
            {
                if (entry.Data.StreamHandle != 0)
                {
                    BASS_StreamFree(entry.Data.StreamHandle);
                }

                if (entry.Data.StreamProvider != null)
                {
                    UnregisterStreamProviderCallbacks(entry.Data.StreamProvider);
                    entry.Data.StreamProvider.Dispose();
                }
                entry.Data.CacheManager?.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BassAudioEngine] 释放预加载资源异常: {ex.Message}");
            }
        }

        public void RegisterGaplessPreload(SongInfo song, Playback.PreloadedData data)
        {
            if (song == null || data == null)
            {
                return;
            }

            if (_playMode == PlayMode.LoopOne)
            {
                // 单曲循环不需要预加载下一首，立即释放预加载资源
                ReleasePreloadedResources(new GaplessPreloadEntry(song, data));
                return;
            }

            lock (_gaplessPreloadLock)
            {
                if (_gaplessPreload != null)
                {
                    ReleasePreloadedResources(_gaplessPreload);
                }

                _gaplessPreload = new GaplessPreloadEntry(song, data);
            }
        }

        private BassDeviceDescriptor ResolvePreferredDescriptor(
            string? preferredDeviceId,
            IReadOnlyList<BassDeviceDescriptor> descriptors,
            out string resolvedId)
        {
            if (!string.IsNullOrWhiteSpace(preferredDeviceId) &&
                !string.Equals(preferredDeviceId, AudioOutputDeviceInfo.WindowsDefaultId, StringComparison.OrdinalIgnoreCase))
            {
                var match = descriptors.FirstOrDefault(d =>
                    d.IsEnabled && BuildDeviceIdentifier(d).Equals(preferredDeviceId, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    resolvedId = preferredDeviceId!;
                    return match;
                }
            }

            var fallback = descriptors.FirstOrDefault(d => d.IsEnabled && d.IsDefault) ??
                           descriptors.FirstOrDefault(d => d.IsEnabled) ??
                           throw new InvalidOperationException("系统未检测到可用的声音输出设备");

            resolvedId = AudioOutputDeviceInfo.WindowsDefaultId;
            return fallback;
        }

        private List<BassDeviceDescriptor> EnumerateBassDevices()
        {
            var devices = new List<BassDeviceDescriptor>();
            int deviceIndex = 1;

            while (BASS_GetDeviceInfo(deviceIndex, out var info))
            {
                string name = Marshal.PtrToStringAnsi(info.name) ?? $"设备 {deviceIndex}";
                string driver = Marshal.PtrToStringAnsi(info.driver) ?? string.Empty;
                Guid? guid = info.guid != IntPtr.Zero ? Marshal.PtrToStructure<Guid>(info.guid) : (Guid?)null;
                bool enabled = (info.flags & BASS_DEVICE_ENABLED_FLAG) != 0;
                bool isDefault = (info.flags & BASS_DEVICE_DEFAULT_FLAG) != 0;

                devices.Add(new BassDeviceDescriptor(deviceIndex, name, driver, guid, enabled, isDefault));
                deviceIndex++;
            }

            return devices;
        }

        private List<AudioOutputDeviceInfo> EnumerateOutputDevicesInternal()
        {
            var descriptors = EnumerateBassDevices();
            var result = new List<AudioOutputDeviceInfo>();

            var defaultDescriptor = descriptors.FirstOrDefault(d => d.IsEnabled && d.IsDefault) ??
                                    descriptors.FirstOrDefault(d => d.IsEnabled);

            if (defaultDescriptor != null)
            {
                result.Add(CreateDefaultDeviceInfo(defaultDescriptor));
            }

            foreach (var descriptor in descriptors)
            {
                if (!descriptor.IsEnabled || IsPlaceholderDefaultDevice(descriptor))
                {
                    continue;
                }

                result.Add(CreateDeviceInfo(descriptor));
            }

            return result;
        }

        private AudioOutputDeviceInfo CreateDefaultDeviceInfo(BassDeviceDescriptor descriptor)
        {
            string displayName = string.IsNullOrWhiteSpace(descriptor.Name)
                ? "Windows 默认"
                : $"Windows 默认（当前：{descriptor.Name}）";

            return new AudioOutputDeviceInfo
            {
                DeviceId = AudioOutputDeviceInfo.WindowsDefaultId,
                DisplayName = displayName,
                IsWindowsDefault = true,
                IsEnabled = descriptor.IsEnabled,
                BassDeviceIndex = descriptor.Index,
                DeviceGuid = descriptor.Guid,
                Driver = descriptor.Driver,
                IsCurrent = _activeOutputDeviceId == AudioOutputDeviceInfo.WindowsDefaultId
            };
        }

        private AudioOutputDeviceInfo CreateDeviceInfo(BassDeviceDescriptor descriptor)
        {
            string displayName = string.IsNullOrWhiteSpace(descriptor.Name)
                ? $"设备 {descriptor.Index}"
                : descriptor.Name;

            return new AudioOutputDeviceInfo
            {
                DeviceId = BuildDeviceIdentifier(descriptor),
                DisplayName = displayName,
                IsWindowsDefault = false,
                IsEnabled = descriptor.IsEnabled,
                BassDeviceIndex = descriptor.Index,
                DeviceGuid = descriptor.Guid,
                Driver = descriptor.Driver,
                IsCurrent = descriptor.Index == _currentDeviceIndex &&
                            _activeOutputDeviceId != AudioOutputDeviceInfo.WindowsDefaultId
            };
        }

        private AudioOutputDeviceInfo? FindDeviceSnapshot(string deviceId, int deviceIndex)
        {
            var devices = EnumerateOutputDevicesInternal();

            var match = devices.FirstOrDefault(d =>
                string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));

            if (match != null)
            {
                return match;
            }

            return devices.FirstOrDefault(d => d.BassDeviceIndex == deviceIndex);
        }

        private (bool success, int deviceIndex, string resolvedId, string? errorMessage) ResolveTargetDevice(
            AudioOutputDeviceInfo selection,
            IReadOnlyList<BassDeviceDescriptor> descriptors)
        {
            if (selection.IsWindowsDefault)
            {
                var descriptor = descriptors.FirstOrDefault(d => d.IsEnabled && d.IsDefault) ??
                                 descriptors.FirstOrDefault(d => d.IsEnabled);
                if (descriptor == null)
                {
                    return (false, 0, AudioOutputDeviceInfo.WindowsDefaultId, "系统没有可用的声音输出设备");
                }

                return (true, descriptor.Index, AudioOutputDeviceInfo.WindowsDefaultId, null);
            }

            if (selection.BassDeviceIndex > 0)
            {
                return (true, selection.BassDeviceIndex, selection.DeviceId, null);
            }

            var match = descriptors.FirstOrDefault(d =>
                BuildDeviceIdentifier(d).Equals(selection.DeviceId, StringComparison.OrdinalIgnoreCase));

            if (match == null || !match.IsEnabled)
            {
                return (false, 0, selection.DeviceId, "所选输出设备不可用");
            }

            return (true, match.Index, selection.DeviceId, null);
        }

        private static string BuildDeviceIdentifier(BassDeviceDescriptor descriptor)
        {
            if (descriptor.Guid.HasValue && descriptor.Guid.Value != Guid.Empty)
            {
                return descriptor.Guid.Value.ToString("D");
            }

            return $"NAME:{descriptor.Name}|DRIVER:{descriptor.Driver}";
        }

        private static bool IsPlaceholderDefaultDevice(BassDeviceDescriptor descriptor)
        {
            if (!descriptor.IsDefault)
            {
                return false;
            }

            string name = descriptor.Name?.Trim() ?? string.Empty;
            string driver = descriptor.Driver?.Trim() ?? string.Empty;

            if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(driver))
            {
                return true;
            }

            if (string.Equals(name, "default", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(driver) &&
                string.Equals(driver, "default", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        private bool EnsureDeviceInitialized(int deviceIndex, out string? errorMessage)
        {
            if (_initializedDeviceIndices.Contains(deviceIndex))
            {
                ConfigureDeviceSettings(deviceIndex);
                errorMessage = null;
                return true;
            }

            Debug.WriteLine("[BassAudioEngine] [1/3] 初始化BASS核心库...");
            bool initialized = BASS_Init(
                deviceIndex,
                DeviceFrequency,
                BASS_DEVICE_STEREO,
                IntPtr.Zero,
                IntPtr.Zero);

            if (!initialized)
            {
                int errorCode = BASS_ErrorGetCode();
                errorMessage = GetErrorMessage(errorCode);
                Debug.WriteLine($"[BassAudioEngine] ❌ BASS_Init 失败: {errorMessage} (错误码: {errorCode})");
                return false;
            }

            Debug.WriteLine($"[BassAudioEngine] ✓ BASS_Init 成功 (设备: {deviceIndex}, 频率: {DeviceFrequency} Hz)");
            _initializedDeviceIndices.Add(deviceIndex);

            Debug.WriteLine("[BassAudioEngine] [2/3] 配置播放缓冲区...");
            ConfigureDeviceSettings(deviceIndex);
            Debug.WriteLine("[BassAudioEngine] ✓ 缓冲区配置完成 (buffer=500ms, update=50ms)");

            if (!_bassFlacLoaded)
            {
                Debug.WriteLine("[BassAudioEngine] [3/3] 加载BASSFLAC插件...");
                LoadBassFlacPlugin();
            }

            errorMessage = null;
            return true;
        }

        private void ConfigureDeviceSettings(int deviceIndex)
        {
            if (deviceIndex <= 0)
            {
                return;
            }

            if (!BASS_SetDevice(deviceIndex))
            {
                int errorCode = BASS_ErrorGetCode();
                Debug.WriteLine($"[BassAudioEngine] ⚠️ BASS_SetDevice({deviceIndex}) 失败: {GetErrorMessage(errorCode)}");
                return;
            }

            BASS_SetConfig(BASS_CONFIG_BUFFER, 500);
            BASS_SetConfig(BASS_CONFIG_UPDATEPERIOD, 50);
        }

        private void ReleaseDevice(int deviceIndex)
        {
            if (deviceIndex <= 0 || !_initializedDeviceIndices.Contains(deviceIndex))
            {
                return;
            }

            if (!BASS_SetDevice(deviceIndex))
            {
                return;
            }

            if (BASS_Free())
            {
                _initializedDeviceIndices.Remove(deviceIndex);
                Debug.WriteLine($"[BassAudioEngine] ✓ 已释放输出设备 #{deviceIndex}");
            }
        }

        private void EnsureDeviceContext()
        {
            int deviceIndex;
            lock (_syncRoot)
            {
                deviceIndex = _currentDeviceIndex;
            }

            if (deviceIndex <= 0)
            {
                return;
            }

            int current = BASS_GetDevice();
            if (current == deviceIndex)
            {
                return;
            }

            if (!BASS_SetDevice(deviceIndex))
            {
                int errorCode = BASS_ErrorGetCode();
                Debug.WriteLine($"[BassAudioEngine] ⚠️ BASS_SetDevice({deviceIndex}) 失败: {GetErrorMessage(errorCode)}");
            }
        }

        public Playback.PreloadedData? ConsumeGaplessPreload(string songId)
        {
            if (string.IsNullOrWhiteSpace(songId))
            {
                return null;
            }

            lock (_gaplessPreloadLock)
            {
                if (_gaplessPreload != null && _gaplessPreload.Song != null && string.Equals(_gaplessPreload.Song.Id, songId, StringComparison.Ordinal))
                {
                    var data = _gaplessPreload.Data;
                    _gaplessPreload = null;
                    return data;
                }
            }

            return null;
        }

        private void RegisterStreamProviderCallbacks(BassStreamProvider? streamProvider)
        {
            if (streamProvider == null)
            {
                return;
            }

            streamProvider.ReadFailure -= OnStreamProviderReadFailure;
            streamProvider.ReadFailure += OnStreamProviderReadFailure;
        }

        private void UnregisterStreamProviderCallbacks(BassStreamProvider? streamProvider)
        {
            if (streamProvider == null)
            {
                return;
            }

            streamProvider.ReadFailure -= OnStreamProviderReadFailure;
        }

        private void OnStreamProviderReadFailure(object? sender, StreamReadFailureEventArgs e)
        {
            bool isActiveProvider;
            lock (_syncRoot)
            {
                isActiveProvider = ReferenceEquals(sender, _currentStreamProvider);
            }

            if (!isActiveProvider)
            {
                return;
            }

            Debug.WriteLine($"[BassAudioEngine] Stream read failure at {e.Position}: {e.Reason}");
            StopInternal(raiseEvent: false);
            _stateMachine.TransitionTo(PlaybackState.Idle);
            OnPlaybackError($"音频流读取失败: {e.Reason}");
        }

        private void OnPlaybackStarted(SongInfo song)
        {
            DispatchEvent(PlaybackStarted, this, song);
        }

        private void OnPlaybackError(string message)
        {
            DispatchEvent(PlaybackError, this, message);
        }

        private void StateMachineOnStateChanged(object? sender, StateTransitionEventArgs e)
        {
            DispatchEvent(StateChanged, this, e.NewState);
        }

        private static string GetErrorMessage(int errorCode)
        {
            return errorCode switch
            {
                0 => "OK",
                1 => "内存不足",
                2 => "无法打开文件",
                3 => "驱动不可用",
                4 => "缓冲区不足",
                5 => "无效句柄",
                6 => "不支持的格式",
                7 => "无效位置",
                18 => "未知错误",
                _ => $"错误代码 {errorCode}"
            };
        }

        private static void DispatchEvent<TEventArgs>(EventHandler<TEventArgs>? handler, object sender, TEventArgs args)
        {
            var captured = handler;
            if (captured == null)
            {
                return;
            }

            _ = Task.Run(() =>
            {
                try
                {
                    captured(sender, args);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[BassAudioEngine] 事件处理程序异常: {ex}");
                }
            });
        }

        private static void DispatchEvent(EventHandler? handler, object sender, EventArgs args)
        {
            var captured = handler;
            if (captured == null)
            {
                return;
            }

            _ = Task.Run(() =>
            {
                try
                {
                    captured(sender, args);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[BassAudioEngine] 事件处理程序异常: {ex}");
                }
            });
        }

        #endregion
    }

    public sealed class GaplessTransitionEventArgs : EventArgs
    {
        public GaplessTransitionEventArgs(SongInfo? previousSong, SongInfo nextSong)
        {
            PreviousSong = previousSong;
            NextSong = nextSong ?? throw new ArgumentNullException(nameof(nextSong));
        }

        public SongInfo? PreviousSong { get; }
        public SongInfo NextSong { get; }
    }

    /// <summary>
    /// 播放模式定义。
    /// </summary>
    public enum PlayMode
    {
        Sequential,
        Loop,
        LoopOne,
        Random
    }
}

