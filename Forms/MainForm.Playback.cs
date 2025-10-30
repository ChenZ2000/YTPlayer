using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using YTPlayer.Core;
using YTPlayer.Core.Playback;
using YTPlayer.Core.Playback.Cache;
using YTPlayer.Models;

namespace YTPlayer
{
    public partial class MainForm
    {
        private SongInfo PredictNextSong()
        {
            try
            {
                var playMode = _audioEngine?.PlayMode ?? PlayMode.Loop;
                // 🎯 使用新的 PredictNextAvailable 方法，自动跳过不可用的歌曲
                return _playbackQueue.PredictNextAvailable(playMode, maxAttempts: 10);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainForm] 预测下一首歌曲失败: {ex.Message}");
                return null;
            }
        }

        private SongInfo PredictNextFromQueue()
        {
            var playMode = _audioEngine?.PlayMode ?? PlayMode.Loop;
            return _playbackQueue.PredictFromQueue(playMode);
        }

        private async Task<Dictionary<string, SongUrlInfo>> GetSongUrlWithTimeoutAsync(
            string[] ids,
            QualityLevel quality,
            CancellationToken cancellationToken,
            bool skipAvailabilityCheck = false)
        {
            var startTime = DateTime.Now;
            System.Diagnostics.Debug.WriteLine($"[MainForm] ⏱ 开始获取播放链接: IDs={string.Join(",", ids)}, quality={quality}, skipCheck={skipAvailabilityCheck}");

            var songUrlTask = _apiClient.GetSongUrlAsync(ids, quality, skipAvailabilityCheck, cancellationToken);

            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                var timeoutTask = Task.Delay(SONG_URL_TIMEOUT_MS, timeoutCts.Token);
                var completedTask = await Task.WhenAny(songUrlTask, timeoutTask).ConfigureAwait(false);

                if (completedTask == songUrlTask)
                {
                    timeoutCts.Cancel();
                    var result = await songUrlTask.ConfigureAwait(false);
                    var elapsed = (DateTime.Now - startTime).TotalMilliseconds;
                    System.Diagnostics.Debug.WriteLine($"[MainForm] ✓ 获取播放链接成功，耗时: {elapsed:F0}ms");
                    return result;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    var elapsed = (DateTime.Now - startTime).TotalMilliseconds;
                    System.Diagnostics.Debug.WriteLine($"[MainForm] ❌ 获取播放链接被取消，已耗时: {elapsed:F0}ms");
                    throw new OperationCanceledException(cancellationToken);
                }

                var timeoutElapsed = (DateTime.Now - startTime).TotalMilliseconds;
                System.Diagnostics.Debug.WriteLine($"[MainForm] ❌ 获取播放链接超时，耗时: {timeoutElapsed:F0}ms (超时限制: {SONG_URL_TIMEOUT_MS}ms)");
                throw new TimeoutException($"获取播放链接超时（{timeoutElapsed:F0}ms > {SONG_URL_TIMEOUT_MS}ms）");
            }
        }

        private async Task<Dictionary<string, SongUrlInfo>> GetSongUrlWithRetryAsync(
            string[] ids,
            QualityLevel quality,
            CancellationToken cancellationToken,
            int? maxAttempts = null,
            bool suppressStatusUpdates = false,
            bool skipAvailabilityCheck = false)
        {
            int attempt = 0;
            int delayMs = INITIAL_RETRY_DELAY_MS;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                attempt++;

                try
                {
                    return await GetSongUrlWithTimeoutAsync(ids, quality, cancellationToken, skipAvailabilityCheck).ConfigureAwait(false);
                }
                catch (SongResourceNotFoundException)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MainForm] 获取播放链接失败（尝试 {attempt}）: {ex.Message}");

                    if (maxAttempts.HasValue && attempt >= maxAttempts.Value)
                    {
                        throw;
                    }

                    if (!suppressStatusUpdates)
                    {
                        SetPlaybackLoadingState(true, $"获取播放链接失败，正在重试（第 {attempt} 次）...");
                    }

                    try
                    {
                        await Task.Delay(Math.Min(delayMs, MAX_RETRY_DELAY_MS), cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }

                    delayMs = Math.Min(delayMs * 2, MAX_RETRY_DELAY_MS);
                }
            }
        }

        private async Task PlaySong(SongInfo song)
        {
            System.Diagnostics.Debug.WriteLine($"[MainForm] PlaySong 被调用（用户主动播放）: song={song?.Name}");

            if (song == null)
            {
                return;
            }

            var selection = _playbackQueue.ManualSelect(song, _currentSongs, _currentViewSource);

            switch (selection.Route)
            {
                case PlaybackRoute.Queue:
                case PlaybackRoute.ReturnToQueue:
                    System.Diagnostics.Debug.WriteLine($"[MainForm] 队列播放: 来源={_playbackQueue.QueueSource}, 索引={selection.QueueIndex}, 刷新={selection.QueueChanged}");
                    UpdateFocusForQueue(selection.QueueIndex, selection.Song);
                    break;

                case PlaybackRoute.Injection:
                case PlaybackRoute.PendingInjection:
                    System.Diagnostics.Debug.WriteLine($"[MainForm] 插播播放: {song.Name}, 插播索引={selection.InjectionIndex}");
                    UpdateFocusForInjection(song, selection.InjectionIndex);
                    break;

                default:
                    System.Diagnostics.Debug.WriteLine("[MainForm] 播放选择未产生有效路由");
                    break;
            }

            await PlaySongDirectWithCancellation(song).ConfigureAwait(false);
        }

        private async Task PlaySongDirectWithCancellation(SongInfo song, bool isAutoPlayback = false)
        {
            System.Diagnostics.Debug.WriteLine($"[MainForm] PlaySongDirectWithCancellation 被调用: song={song?.Name}, isAutoPlayback={isAutoPlayback}");

            if (_audioEngine == null || song == null)
            {
                System.Diagnostics.Debug.WriteLine("[MainForm ERROR] _audioEngine is null or song is null");
                return;
            }

            _playbackCancellation?.Cancel();
            _playbackCancellation?.Dispose();

            _playbackCancellation = new CancellationTokenSource();
            var cancellationToken = _playbackCancellation.Token;

            var timeSinceLastRequest = DateTime.Now - _lastPlayRequestTime;
            if (timeSinceLastRequest.TotalMilliseconds < MIN_PLAY_REQUEST_INTERVAL_MS)
            {
                int delayMs = MIN_PLAY_REQUEST_INTERVAL_MS - (int)timeSinceLastRequest.TotalMilliseconds;
                System.Diagnostics.Debug.WriteLine($"[MainForm] 请求过快，延迟 {delayMs}ms");

                try
                {
                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    System.Diagnostics.Debug.WriteLine("[MainForm] 播放请求在延迟期间被取消");
                    return;
                }
            }

            _lastPlayRequestTime = DateTime.Now;

            await PlaySongDirect(song, cancellationToken, isAutoPlayback).ConfigureAwait(false);
        }

        private async Task PlaySongDirect(
            SongInfo song,
            CancellationToken cancellationToken,
            bool isAutoPlayback = false)
        {
            System.Diagnostics.Debug.WriteLine($"[MainForm] PlaySongDirect 被调用: song={song?.Name}, isAutoPlayback={isAutoPlayback}");

            if (_audioEngine == null)
            {
                System.Diagnostics.Debug.WriteLine("[MainForm ERROR] _audioEngine is null");
                return;
            }

            if (song == null)
            {
                System.Diagnostics.Debug.WriteLine("[MainForm ERROR] song is null");
                return;
            }

            // ⭐ 清理过期的预加载数据（只保留当前歌曲和下一首）
            var currentSongId = song.Id;
            var nextSong = PredictNextSong();
            var nextSongId = nextSong?.Id;
            _nextSongPreloader?.CleanupStaleData(currentSongId, nextSongId);

            // ⭐ 尝试获取预加载数据（含完整流对象）
            var preloadedData = _nextSongPreloader?.TryGetPreloadedData(song.Id);

            if (preloadedData != null)
            {
                System.Diagnostics.Debug.WriteLine($"[MainForm] ✓ 命中预加载缓存: {song.Name}, 流就绪: {preloadedData.IsReady}");
                song.Url = preloadedData.Url;
                song.Level = preloadedData.Level;
                song.Size = preloadedData.Size;
            }

            bool loadingStateActive = false;

            try
            {
                SetPlaybackLoadingState(true, $"正在获取歌曲数据: {song.Name}");
                loadingStateActive = true;

                // ⭐ 检查缓存的资源可用性（如果已预检过）
                if (song.IsAvailable == false)
                {
                    System.Diagnostics.Debug.WriteLine($"[MainForm] 歌曲资源不可用（预检缓存）: {song.Name}");
                    SetPlaybackLoadingState(false, "歌曲不存在，已跳过");
                    loadingStateActive = false;
                    HandleSongResourceNotFoundDuringPlayback(song, isAutoPlayback);
                    return;
                }

                // ⭐ 获取URL（支持多音质缓存 + 音质一致性检查）

                // 步骤1：确定当前选择的音质
                string defaultQualityName = _config.DefaultQuality ?? "超清母带";
                QualityLevel selectedQuality = NeteaseApiClient.GetQualityLevelFromName(defaultQualityName);
                string selectedQualityLevel = selectedQuality.ToString().ToLower();

                // 步骤2：检查是否需要重新获取URL
                bool needRefreshUrl = string.IsNullOrEmpty(song.Url);

                if (!needRefreshUrl && !string.IsNullOrEmpty(song.Level))
                {
                    // ⭐⭐ 音质一致性检查：如果缓存的音质与当前选择的不一致，必须重新获取
                    string cachedLevel = song.Level.ToLower();
                    if (cachedLevel != selectedQualityLevel)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MainForm] ⚠ 音质不一致（缓存: {song.Level}, 当前选择: {selectedQualityLevel}），重新获取URL");
                        song.Url = null;
                        song.Level = null;
                        song.Size = 0;
                        needRefreshUrl = true;
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[MainForm] ✓ 音质一致性检查通过: {song.Name}, 音质: {song.Level}");
                    }
                }

                // 步骤3：如果需要获取URL
                if (needRefreshUrl)
                {
                    // ⭐⭐ 首先检查多音质缓存
                    var cachedQuality = song.GetQualityUrl(selectedQualityLevel);
                    if (cachedQuality != null && !string.IsNullOrEmpty(cachedQuality.Url))
                    {
                        System.Diagnostics.Debug.WriteLine($"[MainForm] ✓ 命中多音质缓存: {song.Name}, 音质: {selectedQualityLevel}, 试听: {cachedQuality.IsTrial}");
                        song.Url = cachedQuality.Url;
                        song.Level = cachedQuality.Level;
                        song.Size = cachedQuality.Size;
                        song.IsTrial = cachedQuality.IsTrial;
                        song.TrialStart = cachedQuality.TrialStart;
                        song.TrialEnd = cachedQuality.TrialEnd;
                    }
                    else
                    {
                        // 没有缓存，需要获取URL
                        System.Diagnostics.Debug.WriteLine($"[MainForm] 无URL缓存，重新获取: {song.Name}, 目标音质: {defaultQualityName}");

                        if (cancellationToken.IsCancellationRequested)
                        {
                            SetPlaybackLoadingState(false, "播放已取消");
                            loadingStateActive = false;
                            return;
                        }

                        // ⭐ 如果已预检可用，跳过可用性检查以加快播放速度
                        bool skipCheck = (song.IsAvailable == true);
                        if (skipCheck)
                        {
                            System.Diagnostics.Debug.WriteLine($"[MainForm] 歌曲已预检可用，跳过可用性检查: {song.Name}");
                        }

                        Dictionary<string, SongUrlInfo> urlResult;
                        try
                        {
                            urlResult = await GetSongUrlWithRetryAsync(
                                new[] { song.Id },
                                selectedQuality,
                                cancellationToken,
                                skipAvailabilityCheck: skipCheck).ConfigureAwait(false);
                        }
                        catch (SongResourceNotFoundException missingEx)
                        {
                            System.Diagnostics.Debug.WriteLine($"[MainForm] 获取播放链接时检测到歌曲缺失: {missingEx.Message}");
                            SetPlaybackLoadingState(false, "歌曲不存在，已跳过");
                            loadingStateActive = false;
                            HandleSongResourceNotFoundDuringPlayback(song, isAutoPlayback);
                            return;
                        }
                        catch (OperationCanceledException)
                        {
                            System.Diagnostics.Debug.WriteLine("[MainForm] 播放链接获取被取消");
                            SetPlaybackLoadingState(false, "播放已取消");
                            loadingStateActive = false;
                            return;
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[MainForm] 获取播放链接失败: {ex.Message}");
                            SetPlaybackLoadingState(false, "获取播放链接失败");
                            loadingStateActive = false;
                            MessageBox.Show(
                                "无法获取播放链接，请尝试播放其他歌曲",
                                "错误",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Error);
                            UpdateStatusBar("获取播放链接失败");
                            return;
                        }

                        if (cancellationToken.IsCancellationRequested)
                        {
                            SetPlaybackLoadingState(false, "播放已取消");
                            loadingStateActive = false;
                            return;
                        }

                        if (!urlResult.TryGetValue(song.Id, out SongUrlInfo songUrl) ||
                            string.IsNullOrEmpty(songUrl?.Url))
                        {
                            System.Diagnostics.Debug.WriteLine("[MainForm ERROR] 无法获取播放链接");
                            SetPlaybackLoadingState(false, "获取播放链接失败");
                            loadingStateActive = false;
                            MessageBox.Show(
                                "无法获取播放链接，请尝试播放其他歌曲",
                                "错误",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Error);
                            UpdateStatusBar("获取播放链接失败");
                            return;
                        }

                        // ⭐ 设置试听信息
                        bool isTrial = songUrl.FreeTrialInfo != null;
                        long trialStart = songUrl.FreeTrialInfo?.Start ?? 0;
                        long trialEnd = songUrl.FreeTrialInfo?.End ?? 0;

                        if (isTrial)
                        {
                            System.Diagnostics.Debug.WriteLine($"[MainForm] 🎵 试听版本: {song.Name}, 片段: {trialStart/1000}s - {trialEnd/1000}s");
                        }

                        // ⭐⭐ 将获取的URL缓存到多音质字典中（包含试听信息）
                        string actualLevel = songUrl.Level?.ToLower() ?? selectedQualityLevel;
                        song.SetQualityUrl(actualLevel, songUrl.Url, songUrl.Size, true, isTrial, trialStart, trialEnd);
                        System.Diagnostics.Debug.WriteLine($"[MainForm] ✓ 已缓存音质URL: {song.Name}, 音质: {actualLevel}, 大小: {songUrl.Size}, 试听: {isTrial}");

                        song.Url = songUrl.Url;
                        song.Level = songUrl.Level;
                        song.Size = songUrl.Size;
                        song.IsTrial = isTrial;
                        song.TrialStart = trialStart;
                        song.TrialEnd = trialEnd;

                        // ⭐ Pre-warm HTTP connection to reduce TTFB for download
                        Core.Streaming.OptimizedHttpClientFactory.PreWarmConnection(song.Url);
                    }
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    SetPlaybackLoadingState(false, "播放已取消");
                    loadingStateActive = false;
                    System.Diagnostics.Debug.WriteLine("[MainForm] 播放请求已取消（播放前）");
                    return;
                }

                // ⭐ 立即清空旧歌词，避免在新歌词加载前输出旧歌词
                _lyricsDisplayManager?.Clear();
                _currentLyrics?.Clear();
                System.Diagnostics.Debug.WriteLine("[MainForm] 已清空旧歌词，准备播放新歌曲");

                // ⭐⭐⭐ 关键修复：直接调用 PlayAsync，避免通过 .Result 阻塞线程
                // ⭐ 传递预加载的数据（含完整流对象，如果有）
                bool playResult = await _audioEngine.PlayAsync(song, cancellationToken, preloadedData);
                System.Diagnostics.Debug.WriteLine($"[MainForm] _audioEngine.PlayAsync() 返回: {playResult}");

                if (!playResult)
                {
                    SetPlaybackLoadingState(false, "播放失败");
                    loadingStateActive = false;
                    System.Diagnostics.Debug.WriteLine("[MainForm ERROR] 播放失败");
                    return;
                }

                // ⭐ 通知 SeekManager 当前流模式（缓存流 vs 直接流）
                if (_seekManager != null)
                {
                    if (_audioEngine.CurrentCacheManager != null)
                    {
                        System.Diagnostics.Debug.WriteLine("[MainForm] ⭐⭐⭐ 设置 SeekManager 为缓存流模式 ⭐⭐⭐");
                        System.Diagnostics.Debug.WriteLine($"[MainForm] CacheManager 大小: {_audioEngine.CurrentCacheManager.TotalSize:N0} bytes");
                        _seekManager.SetCacheStream(_audioEngine.CurrentCacheManager);
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("[MainForm] ⚠️ 设置 SeekManager 为直接流模式（不支持任意跳转）");
                        _seekManager.SetDirectStream();
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[MainForm] ⚠️ SeekManager 为 null，无法设置流模式");
                }

                if (loadingStateActive)
                {
                    string statusText = song.IsTrial ? $"正在播放: {song.Name} [试听版]" : $"正在播放: {song.Name}";
                    SetPlaybackLoadingState(false, statusText);
                    loadingStateActive = false;
                }
                else
                {
                    string statusText = song.IsTrial ? $"正在播放: {song.Name} [试听版]" : $"正在播放: {song.Name}";
                    UpdateStatusBar(statusText);
                }

                // ⭐ 修复：使用 SafeInvoke 确保 UI 线程安全
                SafeInvoke(() =>
                {
                    string songDisplayName = song.IsTrial ? $"{song.Name}(试听版)" : song.Name;
                    currentSongLabel.Text = $"{songDisplayName} - {song.Artist}";
                    playPauseButton.Text = "暂停";
                    System.Diagnostics.Debug.WriteLine("[PlaySongDirect] 播放成功，按钮设置为: 暂停");
                });

                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                if (!cancellationToken.IsCancellationRequested)
                {
                    SafeInvoke(() => SyncPlayPauseButtonText());
                }

                // ⭐ 修复：使用 SafeInvoke 确保 UI 线程安全
                SafeInvoke(() =>
                {
                    UpdatePlayButtonDescription(song);
                    UpdateTrayIconTooltip(song);
                    if (!Visible && !isAutoPlayback)
                    {
                        ShowTrayBalloonTip(song, "正在播放");
                    }
                });

                // ⭐ 试听版本：始终通过 TTS 发送提示（不受歌词朗读开关控制）
                if (song.IsTrial)
                {
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        bool success = Utils.TtsHelper.SpeakText("[试听片段 30 秒]");
                        System.Diagnostics.Debug.WriteLine($"[TTS] 试听提示: {(success ? "成功" : "失败")}");
                    });
                }

                // 加载歌词
                _ = LoadLyrics(song.Id, cancellationToken);

                // ⭐ 播放成功后立即刷新预加载（新歌曲已开始，队列状态已稳定）
                SafeInvoke(() => RefreshNextSongPreload());
            }
            catch (OperationCanceledException)
            {
                System.Diagnostics.Debug.WriteLine("[MainForm] 播放请求被取消");
                SetPlaybackLoadingState(false, "播放已取消");
            }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    System.Diagnostics.Debug.WriteLine($"[MainForm ERROR] PlaySongDirect 异常: {ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"[MainForm ERROR] 堆栈: {ex.StackTrace}");
                    SetPlaybackLoadingState(false, "播放失败");
                    MessageBox.Show(
                        $"播放失败: {ex.Message}",
                        "错误",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                    UpdateStatusBar("播放失败");
                }
            }
            finally
            {
                if (loadingStateActive)
                {
                    SetPlaybackLoadingState(false);
                }
            }
        }

        private async void PlayPrevious(bool isManual = true)
        {
            System.Diagnostics.Debug.WriteLine($"[MainForm] PlayPrevious 被调用 (isManual={isManual})");

            var playMode = _audioEngine?.PlayMode ?? PlayMode.Loop;

            // ⭐ 关键修复：先同步播放队列到当前实际播放的歌曲
            var currentSong = _audioEngine?.CurrentSong;
            if (currentSong != null && isManual)
            {
                _playbackQueue.AdvanceForPlayback(currentSong, playMode, _currentViewSource);
                System.Diagnostics.Debug.WriteLine($"[MainForm] 已同步播放队列到当前歌曲: {currentSong.Name}");
            }

            if (isManual)
            {
                if (await TryPlayManualDirectionalAsync(isNext: false))
                {
                    return;
                }
            }

            var result = _playbackQueue.MovePrevious(playMode, isManual, _currentViewSource);

            if (result.QueueEmpty)
            {
                System.Diagnostics.Debug.WriteLine("[MainForm] 播放队列为空，无法播放上一首");
                UpdateStatusBar("播放队列为空");
                return;
            }

            if (!result.HasSong)
            {
                if (isManual && result.ReachedBoundary)
                {
                    UpdateStatusBar("已经是第一首");
                }
                return;
            }

            await ExecutePlayPreviousResultAsync(result).ConfigureAwait(false);
        }

        private async void PlayNext(bool isManual = true)
        {
            System.Diagnostics.Debug.WriteLine($"[MainForm] PlayNext 被调用 (isManual={isManual})");

            var playMode = _audioEngine?.PlayMode ?? PlayMode.Loop;

            // ⭐ 关键修复：先同步播放队列到当前实际播放的歌曲
            var currentSong = _audioEngine?.CurrentSong;
            if (currentSong != null && isManual)
            {
                _playbackQueue.AdvanceForPlayback(currentSong, playMode, _currentViewSource);
                System.Diagnostics.Debug.WriteLine($"[MainForm] 已同步播放队列到当前歌曲: {currentSong.Name}");
            }

            if (isManual)
            {
                if (await TryPlayManualDirectionalAsync(isNext: true))
                {
                    return;
                }
            }

            var result = _playbackQueue.MoveNext(playMode, isManual, _currentViewSource);

            if (result.QueueEmpty)
            {
                System.Diagnostics.Debug.WriteLine("[MainForm] 播放队列为空，无法播放下一首");
                UpdateTrayIconTooltip(null);
                UpdateStatusBar("播放队列为空");
                return;
            }

            if (!result.HasSong)
            {
                if (isManual && result.ReachedBoundary)
                {
                    UpdateStatusBar("已经是最后一首");
                }
                return;
            }

            await ExecutePlayNextResultAsync(result).ConfigureAwait(false);
        }

        private enum ManualNavigationAvailability
        {
            Success,
            Missing,
            Failed
        }

        private async Task<bool> TryPlayManualDirectionalAsync(bool isNext)
        {
            var playMode = _audioEngine?.PlayMode ?? PlayMode.Loop;
            var currentSong = _audioEngine?.CurrentSong;
            var attemptedIds = new HashSet<string>(StringComparer.Ordinal);
            int maxAttempts = CalculateManualNavigationLimit();

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                var result = isNext
                    ? _playbackQueue.MoveNext(playMode, isManual: true, _currentViewSource)
                    : _playbackQueue.MovePrevious(playMode, isManual: true, _currentViewSource);

                if (result.QueueEmpty)
                {
                    System.Diagnostics.Debug.WriteLine("[MainForm] 播放队列为空，手动导航停止");
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

                var candidate = result.Song;
                if (candidate == null || string.IsNullOrWhiteSpace(candidate.Id))
                {
                    System.Diagnostics.Debug.WriteLine("[MainForm] 手动导航遇到无效歌曲，继续搜索");
                    RestoreQueuePosition(currentSong, playMode);
                    continue;
                }

                if (!attemptedIds.Add(candidate.Id))
                {
                    System.Diagnostics.Debug.WriteLine("[MainForm] 手动导航检测到循环，停止搜索");
                    UpdateStatusBar("未找到可播放的歌曲");
                    RestoreQueuePosition(currentSong, playMode);
                    return true;
                }

                var availability = await PrepareSongForManualNavigationAsync(result);

                if (availability == ManualNavigationAvailability.Success)
                {
                    if (isNext)
                    {
                        await ExecutePlayNextResultAsync(result);
                    }
                    else
                    {
                        await ExecutePlayPreviousResultAsync(result);
                    }
                    return true;
                }

                string friendlyName = BuildFriendlySongName(candidate);

                if (availability == ManualNavigationAvailability.Missing)
                {
                    string message = string.IsNullOrEmpty(friendlyName)
                        ? "已跳过无法播放的歌曲（官方资源不存在）"
                        : $"已跳过：{friendlyName}（官方资源不存在）";
                    UpdateStatusBar(message);
                    System.Diagnostics.Debug.WriteLine($"[MainForm] 手动{(isNext ? "下一曲" : "上一曲")}跳过缺失歌曲: {candidate?.Id} - {friendlyName}");

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
                    UpdateStatusBar($"获取播放链接失败：{friendlyName}");
                }

                System.Diagnostics.Debug.WriteLine($"[MainForm] 手动导航获取播放链接失败: {candidate?.Id}");
                RestoreQueuePosition(currentSong, playMode);
                return true;
            }

            UpdateStatusBar("未找到可播放的歌曲");
            RestoreQueuePosition(currentSong, _audioEngine?.PlayMode ?? PlayMode.Loop);
            return true;
        }

        private void RestoreQueuePosition(SongInfo currentSong, PlayMode playMode)
        {
            if (currentSong == null || _playbackQueue == null)
            {
                return;
            }

            _playbackQueue.AdvanceForPlayback(currentSong, playMode, _currentViewSource);
        }

        private int CalculateManualNavigationLimit()
        {
            int queueCount = 0;
            int injectionCount = 0;

            if (_playbackQueue != null)
            {
                var currentQueue = _playbackQueue.CurrentQueue;
                if (currentQueue != null)
                {
                    queueCount = currentQueue.Count;
                }

                var injectionChain = _playbackQueue.InjectionChain;
                if (injectionChain != null)
                {
                    injectionCount = injectionChain.Count;
                }
            }

            int pending = (_playbackQueue != null && _playbackQueue.HasPendingInjection) ? 1 : 0;
            int limit = queueCount + injectionCount + pending + 3;
            return limit < 6 ? 6 : limit;
        }

        private async Task<ManualNavigationAvailability> PrepareSongForManualNavigationAsync(PlaybackMoveResult moveResult)
        {
            if (moveResult?.Song == null)
            {
                return ManualNavigationAvailability.Failed;
            }

            var song = moveResult.Song;
            string defaultQualityName = _config.DefaultQuality ?? "超清母带";
            QualityLevel selectedQuality = NeteaseApiClient.GetQualityLevelFromName(defaultQualityName);

            try
            {
                var urlResult = await GetSongUrlWithRetryAsync(
                    new[] { song.Id },
                    selectedQuality,
                    CancellationToken.None,
                    maxAttempts: 3,
                    suppressStatusUpdates: true);

                if (urlResult != null &&
                    urlResult.TryGetValue(song.Id, out SongUrlInfo songUrl) &&
                    !string.IsNullOrEmpty(songUrl?.Url))
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
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainForm] 手动切换检查资源失败: {ex.Message}");
                return ManualNavigationAvailability.Failed;
            }
        }

        private static string BuildFriendlySongName(SongInfo song)
        {
            if (song == null)
            {
                return string.Empty;
            }

            string name = song.Name ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(song.Artist))
            {
                return $"{name} - {song.Artist}";
            }

            return name;
        }

        private async Task ExecutePlayPreviousResultAsync(PlaybackMoveResult result)
        {
            if (result?.Song == null)
            {
                return;
            }

            switch (result.Route)
            {
                case PlaybackRoute.Injection:
                    UpdateFocusForInjection(result.Song, result.InjectionIndex);
                    break;

                case PlaybackRoute.ReturnToQueue:
                case PlaybackRoute.Queue:
                    UpdateFocusForQueue(result.QueueIndex, result.Song);
                    break;
            }

            await PlaySongDirectWithCancellation(result.Song, isAutoPlayback: true);
        }

        private async Task ExecutePlayNextResultAsync(PlaybackMoveResult result)
        {
            if (result?.Song == null)
            {
                System.Diagnostics.Debug.WriteLine("[MainForm] ExecutePlayNextResultAsync 收到空歌曲");
                return;
            }

            switch (result.Route)
            {
                case PlaybackRoute.PendingInjection:
                    UpdateFocusForInjection(result.Song, result.InjectionIndex);
                    await PlaySongDirectWithCancellation(result.Song);
                    UpdateStatusBar($"插播：{result.Song.Name} - {result.Song.Artist}");
                    break;

                case PlaybackRoute.Injection:
                    UpdateFocusForInjection(result.Song, result.InjectionIndex);
                    await PlaySongDirectWithCancellation(result.Song, isAutoPlayback: true);
                    break;

                case PlaybackRoute.ReturnToQueue:
                case PlaybackRoute.Queue:
                    UpdateFocusForQueue(result.QueueIndex, result.Song);
                    await PlaySongDirectWithCancellation(result.Song, isAutoPlayback: true);
                    break;

                default:
                    System.Diagnostics.Debug.WriteLine("[MainForm] ExecutePlayNextResultAsync 未匹配可播放路由");
                    break;
            }
        }

        private bool RemoveSongFromQueueAndCaches(SongInfo song)
        {
            if (song == null)
            {
                return false;
            }

            bool removed = _playbackQueue.RemoveSongById(song.Id);
            return removed;
        }

        private void HandleSongResourceNotFoundDuringPlayback(SongInfo song, bool isAutoPlayback)
        {
            if (song == null)
            {
                return;
            }

            void ExecuteOnUiThread()
            {
                string friendlyName = song.Name;
                if (!string.IsNullOrWhiteSpace(song.Artist))
                {
                    friendlyName = $"{song.Name} - {song.Artist}";
                }

                bool removed = RemoveSongFromQueueAndCaches(song);
                System.Diagnostics.Debug.WriteLine($"[MainForm] RemoveSongById({song.Id}) => {removed}");

                if (isAutoPlayback)
                {
                    string statusText = string.IsNullOrEmpty(friendlyName)
                        ? "已跳过无法播放的歌曲（官方资源不存在）"
                        : $"已跳过：{friendlyName}（官方资源不存在）";

                    UpdateStatusBar(statusText);
                    System.Diagnostics.Debug.WriteLine($"[MainForm] 跳过缺失歌曲(自动): {song.Id} - {friendlyName}");

                    bool hasQueueSongs = _playbackQueue.CurrentQueue.Count > 0;
                    bool hasPending = _playbackQueue.HasPendingInjection || _playbackQueue.IsInInjection;

                    if (hasQueueSongs || hasPending)
                    {
                        PlayNext(isManual: false);
                        return;
                    }

                    _audioEngine?.Stop();
                    playPauseButton.Text = "播放";
                    UpdateTrayIconTooltip(null);
                    return;
                }

                string statusManual = string.IsNullOrEmpty(friendlyName)
                    ? "该歌曲在官方曲库中不存在或已被移除"
                    : $"无法播放：{friendlyName}（官方资源不存在）";

                UpdateStatusBar(statusManual);
                System.Diagnostics.Debug.WriteLine($"[MainForm] 无法播放缺失歌曲(手动): {song.Id} - {friendlyName}");

                if (Visible)
                {
                    string dialogMessage = string.IsNullOrEmpty(friendlyName)
                        ? "该歌曲在网易云官方曲库中不存在或已被移除。"
                        : $"{friendlyName} 在网易云官方曲库中不存在或已被移除。";

                    MessageBox.Show(
                        $"{dialogMessage}\n\n请尝试播放其他歌曲。",
                        "歌曲不可播放",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }

                SyncPlayPauseButtonText();

                var currentSong = _audioEngine?.CurrentSong;
                if (currentSong != null)
                {
                    UpdateTrayIconTooltip(currentSong);
                    string statusText = currentSong.IsTrial
                        ? $"正在播放: {currentSong.Name} - {currentSong.Artist} [试听版]"
                        : $"正在播放: {currentSong.Name} - {currentSong.Artist}";
                    UpdateStatusBar(statusText);
                }
                else
                {
                    UpdateTrayIconTooltip(null);
                }
            }

            if (InvokeRequired)
            {
                BeginInvoke((Action)ExecuteOnUiThread);
            }
            else
            {
                ExecuteOnUiThread();
            }
        }

        private void UpdateFocusForQueue(int queueIndex, SongInfo expectedSong = null)
        {
            string queueSource = _playbackQueue.QueueSource;
            int resolvedIndex = queueIndex;

            if (expectedSong != null && _currentSongs != null)
            {
                for (int i = 0; i < _currentSongs.Count; i++)
                {
                    if (_currentSongs[i]?.Id == expectedSong.Id)
                    {
                        resolvedIndex = i;
                        break;
                    }
                }
            }

            if (queueSource == _currentViewSource &&
                resolvedIndex >= 0 &&
                resolvedIndex < resultListView.Items.Count)
            {
                _lastListViewFocusedIndex = resolvedIndex;

                if (Visible)
                {
                    var item = resultListView.Items[resolvedIndex];
                    item.Selected = true;
                    item.Focused = true;
                    resultListView.EnsureVisible(resolvedIndex);
                }

                System.Diagnostics.Debug.WriteLine($"[MainForm] 焦点跟随（原队列）: 队列索引={queueIndex}, 视图索引={resolvedIndex}, 来源={queueSource}, 窗口可见={Visible}");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[MainForm] 焦点不跟随: 队列来源={queueSource}, 当前浏览={_currentViewSource}, 队列索引={queueIndex}, 期望歌曲={expectedSong?.Id ?? "null"}");
            }
        }

        private void UpdateFocusForInjection(SongInfo song, int viewIndex)
        {
            if (song == null)
            {
                return;
            }

            if (_playbackQueue.TryGetInjectionSource(song.Id, out string songSource) &&
                songSource == _currentViewSource)
            {
                int indexInView = -1;
                if (_currentSongs != null)
                {
                    for (int i = 0; i < _currentSongs.Count; i++)
                    {
                        if (_currentSongs[i]?.Id == song.Id)
                        {
                            indexInView = i;
                            break;
                        }
                    }
                }

                if (indexInView >= 0 && indexInView < resultListView.Items.Count)
                {
                    _lastListViewFocusedIndex = indexInView;

                    if (Visible)
                    {
                        var item = resultListView.Items[indexInView];
                        item.Selected = true;
                        item.Focused = true;
                        resultListView.EnsureVisible(indexInView);
                    }

                    System.Diagnostics.Debug.WriteLine($"[MainForm] 焦点跟随（插播）: 索引={indexInView}, 插播索引={viewIndex}, 窗口可见={Visible}, 来源={songSource}");
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[MainForm] 插播歌曲不在当前视图: {song?.Name ?? "null"}, 当前视图={_currentViewSource}");
            }
        }

        // ⭐ 移除 AudioEngine_PlaybackReachedHalfway 方法（由新的统一预加载机制替代）

    }
}
