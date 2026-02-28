#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using YTPlayer.Core;
using YTPlayer.Core.Download;
using YTPlayer.Core.Playback;
using YTPlayer.Core.Streaming;
using YTPlayer.Core.Unblock;
using YTPlayer.Forms;
using YTPlayer.Models;
using YTPlayer.Utils;
#pragma warning disable CS0219, CS8600, CS8601, CS8602, CS8603, CS8604, CS8618, CS8622, CS8625, CS8632, CS4014

namespace YTPlayer
{
public partial class MainForm
{
	private void DisposeAudioEngineInBackground(BassAudioEngine? audioEngine)
	{
		if (audioEngine == null)
		{
			return;
		}

		_ = Task.Run(() =>
		{
			try
			{
				audioEngine.Dispose();
			}
			catch (Exception ex)
			{
				Debug.WriteLine("[OnFormClosing] 音频引擎后台释放异常: " + ex.Message);
			}
		});
	}

	private SongInfo PredictNextSong()
	{
		try
		{
			SongInfo currentSong = _audioEngine?.CurrentSong;
			PlayMode playMode = ResolveEffectivePlayModeForPlayback(_audioEngine?.PlayMode ?? PlayMode.Loop, currentSong);
			if (playMode != PlayMode.LoopOne)
			{
				EnsurePersonalFmQueueExpandedInBackground(currentSong, requireImmediateNext: false);
			}
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
		SongInfo currentSong = _audioEngine?.CurrentSong;
		PlayMode playMode = ResolveEffectivePlayModeForPlayback(_audioEngine?.PlayMode ?? PlayMode.Loop, currentSong);
		return _playbackQueue.PredictFromQueue(playMode);
	}

	private async Task<Dictionary<string, SongUrlInfo>> GetSongUrlWithTimeoutAsync(string[] ids, QualityLevel quality, CancellationToken cancellationToken, bool skipAvailabilityCheck = false)
{
	DateTime startTime = DateTime.Now;
	Debug.WriteLine(string.Format("[MainForm] [SongUrl] start ids={0}, quality={1}, skipCheck={2}", string.Join(",", ids), quality, skipAvailabilityCheck));
	using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
	timeoutCts.CancelAfter(TimeSpan.FromSeconds(12.0));
	try
	{
		Dictionary<string, SongUrlInfo> result = await _apiClient.GetSongUrlAsync(ids, quality, skipAvailabilityCheck, timeoutCts.Token).ConfigureAwait(continueOnCapturedContext: false);
		double elapsed = (DateTime.Now - startTime).TotalMilliseconds;
		Debug.WriteLine($"[MainForm] [SongUrl] success in {elapsed:F0}ms");
		return result;
	}
	catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
	{
		double elapsed2 = (DateTime.Now - startTime).TotalMilliseconds;
		Debug.WriteLine($"[MainForm] [SongUrl] canceled by caller after {elapsed2:F0}ms");
		throw;
	}
	catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
	{
		double timeoutElapsed = (DateTime.Now - startTime).TotalMilliseconds;
		Debug.WriteLine($"[MainForm] [SongUrl] timeout after {timeoutElapsed:F0}ms (limit=12000ms)");
		throw new TimeoutException($"Get song URL timed out ({timeoutElapsed:F0}ms > 12000ms)");
	}
}

private async Task<Dictionary<string, SongUrlInfo>> GetSongUrlWithRetryAsync(string[] ids, QualityLevel quality, CancellationToken cancellationToken, int? maxAttempts = 3, bool suppressStatusUpdates = false, bool skipAvailabilityCheck = false)
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

	private static string GetQualityLevelString(QualityLevel quality)
	{
		return quality switch
		{
			QualityLevel.Standard => "standard", 
			QualityLevel.High => "exhigh", 
			QualityLevel.Lossless => "lossless", 
			QualityLevel.HiRes => "hires", 
			QualityLevel.SurroundHD => "jyeffect", 
			QualityLevel.Dolby => "sky", 
			QualityLevel.Master => "jymaster", 
			_ => "standard", 
		};
	}

	private static bool TryGetQualityLevelFromSongLevel(string? songLevel, out QualityLevel quality)
	{
		switch (songLevel?.Trim().ToLowerInvariant())
		{
			case "standard":
				quality = QualityLevel.Standard;
				return true;
			case "exhigh":
				quality = QualityLevel.High;
				return true;
			case "lossless":
				quality = QualityLevel.Lossless;
				return true;
			case "hires":
				quality = QualityLevel.HiRes;
				return true;
			case "jyeffect":
				quality = QualityLevel.SurroundHD;
				return true;
			case "sky":
				quality = QualityLevel.Dolby;
				return true;
			case "jymaster":
				quality = QualityLevel.Master;
				return true;
			default:
				quality = QualityLevel.Standard;
				return false;
		}
	}

	private async Task<bool> TryApplyUnblockAsync(SongInfo song, string targetQualityLevel, CancellationToken cancellationToken)
	{
		if (_unblockService == null || song == null || string.IsNullOrWhiteSpace(song.Id) || song.IsPodcastEpisode)
		{
			return false;
		}
		checked
		{
			using CancellationTokenSource unblockTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(8L));
			CancellationToken unblockToken = unblockTimeoutCts.Token;
			bool shouldRefreshDetail = song.IsAvailable == false ||
			                           song.Duration <= 0 ||
			                           string.IsNullOrWhiteSpace(song.AlbumId) ||
			                           song.ArtistNames == null ||
			                           song.ArtistNames.Count == 0;
			if (shouldRefreshDetail && _apiClient != null)
			{
				try
				{
					SongInfo detail = (await _apiClient.GetSongDetailAsync(new string[1] { song.Id }).ConfigureAwait(continueOnCapturedContext: false))?.FirstOrDefault();
					if (detail != null)
					{
						if (detail.Duration > 0 && (song.Duration <= 0 || Math.Abs(detail.Duration - song.Duration) > 1))
						{
							song.Duration = detail.Duration;
						}
						if (string.IsNullOrWhiteSpace(song.AlbumId) && !string.IsNullOrWhiteSpace(detail.AlbumId))
						{
							song.AlbumId = detail.AlbumId;
						}
						if (string.IsNullOrWhiteSpace(song.Album) && !string.IsNullOrWhiteSpace(detail.Album))
						{
							song.Album = detail.Album;
						}
						if ((song.ArtistNames == null || song.ArtistNames.Count == 0) && detail.ArtistNames != null && detail.ArtistNames.Count > 0)
						{
							song.ArtistNames = new List<string>(detail.ArtistNames);
							song.Artist = string.Join("/", song.ArtistNames);
						}
						if ((song.ArtistIds == null || song.ArtistIds.Count == 0) && detail.ArtistIds != null && detail.ArtistIds.Count > 0)
						{
							song.ArtistIds = new List<long>(detail.ArtistIds);
						}
						if (string.IsNullOrWhiteSpace(song.PicUrl) && !string.IsNullOrWhiteSpace(detail.PicUrl))
						{
							song.PicUrl = detail.PicUrl;
						}
					}
				}
				catch (Exception ex)
				{
					Exception ex2 = ex;
					Debug.WriteLine("[Unblock] å\u008f–æ\u00a0·æ\u008d¢è\u00af\u0086ç»†èŠ‚æ•°æ\u008d®å¤±è\u00b4¥ï¼ˆå¿½ç•¥ï¼‰: " + ex2.Message);
				}
			}
			UnblockService.UnblockMatchResult result;
			try
			{
				result = await _unblockService.TryMatchAsync(song, unblockToken).ConfigureAwait(continueOnCapturedContext: false);
			}
			catch (Exception ex3)
			{
				Debug.WriteLine("[Unblock] 解封异常（忽略继续官方流程）: " + ex3.Message);
				return false;
			}
			if (result == null || string.IsNullOrWhiteSpace(result.Url))
			{
				return false;
			}
			Dictionary<string, string> headers = ((result.Headers != null) ? new Dictionary<string, string>(result.Headers, StringComparer.OrdinalIgnoreCase) : null);
			string level = (string.IsNullOrWhiteSpace(targetQualityLevel) ? GetQualityLevelString(GetCurrentQuality()) : targetQualityLevel);
			int durationSeconds = StreamSizeEstimator.NormalizeDurationSeconds(song.Duration);
			if (result.DurationMs.HasValue && result.DurationMs.Value > 0)
			{
				int resultSeconds = StreamSizeEstimator.NormalizeDurationSeconds((int)Math.Round((double)result.DurationMs.Value / 1000.0));
				if (resultSeconds > 0 && (durationSeconds <= 0 || Math.Abs(resultSeconds - durationSeconds) > 1))
				{
					song.Duration = resultSeconds;
					durationSeconds = resultSeconds;
					Debug.WriteLine($"[Unblock] 使用解封时长: {durationSeconds}s");
				}
			}
			long size = ((result.Size > 0) ? result.Size : song.Size);
			if (size <= 0)
			{
				using CancellationTokenSource sizeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
				sizeCts.CancelAfter(TimeSpan.FromSeconds(10L));
				long contentLength = await HttpRangeHelper.TryGetContentLengthAsync(result.Url, headers, sizeCts.Token, TimeSpan.FromSeconds(8L)).ConfigureAwait(continueOnCapturedContext: false);
				if (contentLength > 0)
				{
					size = contentLength;
					Debug.WriteLine($"[Unblock] Content-Length: {contentLength}");
				}
			}
			if (size <= 0)
			{
				int bitrate = result.BitRate.GetValueOrDefault();
				if (bitrate <= 0)
				{
					bitrate = StreamSizeEstimator.GetApproxBitrateForLevel(level);
				}
				size = StreamSizeEstimator.EstimateSizeFromBitrate(bitrate, durationSeconds);
				if (size > 0)
				{
					Debug.WriteLine($"[Unblock] 估算大小: {size} (bitrate={bitrate}, duration={durationSeconds}s)");
				}
			}
			if (size <= 0)
			{
				size = ((song.Size > 0) ? song.Size : 16777216);
				Debug.WriteLine($"[Unblock] 大小兜底: {size}");
			}
			song.IsAvailable = true;
			song.IsTrial = false;
			song.TrialStart = 0L;
			song.TrialEnd = 0L;
			song.IsUnblocked = true;
			song.UnblockSource = result.Source ?? string.Empty;
			song.CustomHeaders = headers;
			song.SetQualityUrl(level, result.Url, size, isAvailable: true, isTrial: false, 0L, 0L);
			song.Url = result.Url;
			song.Level = level;
			song.Size = size;
			OptimizedHttpClientFactory.PreWarmConnection(song.Url);
			RefreshAvailabilityIndicatorsInCurrentView(song.Id, song);
			Debug.WriteLine($"[Unblock] ✓ 解封成功: {song.Name} ({song.UnblockSource}), level={level}, size={size}");
			return true;
		}
	}

	private async Task<SongResolveResult> ResolveSongPlaybackAsync(SongInfo song, QualityLevel selectedQuality, CancellationToken cancellationToken, bool suppressStatusUpdates)
	{
		if (song == null || string.IsNullOrWhiteSpace(song.Id))
		{
			return SongResolveResult.Failed();
		}

		string selectedQualityLevel = selectedQuality.ToString().ToLower();
		bool needRefreshUrl = string.IsNullOrEmpty(song.Url) || song.IsTrial;

		void ApplyOfficialUrl(SongUrlInfo songUrl, long resolvedSize, bool isTrial, long trialStart, long trialEnd)
		{
			string actualLevel = songUrl.Level?.ToLower() ?? selectedQualityLevel;
			song.SetQualityUrl(actualLevel, songUrl.Url, resolvedSize, isAvailable: true, isTrial, trialStart, trialEnd);
			Debug.WriteLine($"[MainForm] Official URL cached: {song.Name}, level={actualLevel}, size={resolvedSize}, trial={isTrial}");
			song.IsAvailable = true;
			song.Url = songUrl.Url;
			song.Level = songUrl.Level;
			song.Size = resolvedSize;
			song.IsTrial = isTrial;
			song.TrialStart = trialStart;
			song.TrialEnd = trialEnd;
			OptimizedHttpClientFactory.PreWarmConnection(song.Url);
			RefreshAvailabilityIndicatorsInCurrentView(song.Id, song);
		}

		if (song.IsAvailable == false)
		{
			Debug.WriteLine("[MainForm] 歌曲资源不可用（预检缓存）: " + song.Name);
			if (await TryApplyUnblockAsync(song, GetQualityLevelString(GetCurrentQuality()), cancellationToken).ConfigureAwait(continueOnCapturedContext: false))
			{
				return SongResolveResult.Success(usedUnblock: true);
			}
			return SongResolveResult.NotAvailable();
		}

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

		if (!needRefreshUrl)
		{
			return SongResolveResult.Success();
		}

		QualityUrlInfo cachedQuality = song.GetQualityUrl(selectedQualityLevel);
		if (cachedQuality != null && !string.IsNullOrEmpty(cachedQuality.Url) && cachedQuality.IsTrial)
		{
			Debug.WriteLine("[MainForm] ⚠\ufe0f 命中试听版缓存，播放前尝试解封: " + song.Name + ", 音质: " + selectedQualityLevel);
			if (await TryApplyUnblockAsync(song, selectedQualityLevel, cancellationToken).ConfigureAwait(continueOnCapturedContext: false))
			{
				return SongResolveResult.Success(usedUnblock: true);
			}
			Debug.WriteLine("[MainForm] 试听版缓存未解封成功，丢弃缓存并重新拉取官方链接");
			cachedQuality = null;
		}

		if (cachedQuality != null && !string.IsNullOrEmpty(cachedQuality.Url))
		{
			Debug.WriteLine($"[MainForm] ✓ 命中多音质缓存: {song.Name}, 音质: {selectedQualityLevel}, 试听: {cachedQuality.IsTrial}");
			song.IsAvailable = true;
			song.Url = cachedQuality.Url;
			song.Level = cachedQuality.Level;
			song.Size = cachedQuality.Size;
			song.IsTrial = cachedQuality.IsTrial;
			song.TrialStart = cachedQuality.TrialStart;
			song.TrialEnd = cachedQuality.TrialEnd;
			RefreshAvailabilityIndicatorsInCurrentView(song.Id, song);
			return SongResolveResult.Success();
		}

		if (cancellationToken.IsCancellationRequested)
		{
			return SongResolveResult.Canceled();
		}

		if (!song.IsAvailable.HasValue && !(await EnsureSongAvailabilityAsync(song, selectedQuality, cancellationToken).ConfigureAwait(continueOnCapturedContext: false)))
		{
			Debug.WriteLine("[MainForm] Single-song availability check returned unavailable: " + song.Name);
			song.IsAvailable = false;
			return SongResolveResult.NotAvailable();
		}

		Dictionary<string, SongUrlInfo> urlResult;
		try
		{
			urlResult = await GetSongUrlWithRetryAsync(new string[1] { song.Id }, selectedQuality, cancellationToken, 3, suppressStatusUpdates: suppressStatusUpdates).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (SongResourceNotFoundException ex)
		{
			Debug.WriteLine("[MainForm] 获取播放链接时检测到歌曲缺失: " + ex.Message);
			song.IsAvailable = false;
			if (await TryApplyUnblockAsync(song, selectedQualityLevel, cancellationToken).ConfigureAwait(continueOnCapturedContext: false))
			{
				return SongResolveResult.Success(usedUnblock: true);
			}
			return SongResolveResult.NotAvailable();
		}
		catch (PaidAlbumNotPurchasedException ex2)
		{
			Debug.WriteLine("[MainForm] 歌曲属于付费专辑且未购买: " + ex2.Message);
			return SongResolveResult.PaidAlbumNotPurchased();
		}
		catch (OperationCanceledException)
		{
			Debug.WriteLine("[MainForm] 播放链接获取被取消");
			return SongResolveResult.Canceled();
		}
		catch (Exception ex3)
		{
			Debug.WriteLine("[MainForm] 获取播放链接失败: " + ex3.Message);
			if (await TryApplyUnblockAsync(song, selectedQualityLevel, cancellationToken).ConfigureAwait(continueOnCapturedContext: false))
			{
				return SongResolveResult.Success(usedUnblock: true);
			}
			return SongResolveResult.Failed();
		}

		if (cancellationToken.IsCancellationRequested)
		{
			return SongResolveResult.Canceled();
		}

		if (!urlResult.TryGetValue(song.Id, out SongUrlInfo songUrl) || string.IsNullOrEmpty(songUrl?.Url))
		{
			Debug.WriteLine("[MainForm] 官方返回 URL 为空，尝试解封: " + song.Name);
			if (await TryApplyUnblockAsync(song, selectedQualityLevel, cancellationToken).ConfigureAwait(continueOnCapturedContext: false))
			{
				return SongResolveResult.Success(usedUnblock: true);
			}
			return SongResolveResult.Failed();
		}

		long resolvedSize = songUrl.Size;
		if (resolvedSize <= 0)
		{
			long contentLength = (await HttpRangeHelper.CheckRangeSupportAsync(songUrl.Url, null, cancellationToken, song.CustomHeaders).ConfigureAwait(continueOnCapturedContext: false)).Item2;
			if (contentLength > 0)
			{
				resolvedSize = contentLength;
			}
		}
		if (resolvedSize <= 0)
		{
			resolvedSize = StreamSizeEstimator.EstimateSizeFromBitrate(songUrl.Br, song.Duration);
		}

		bool isTrial = songUrl.FreeTrialInfo != null;
		long trialStart = songUrl.FreeTrialInfo?.Start ?? 0;
		long trialEnd = songUrl.FreeTrialInfo?.End ?? 0;
		if (isTrial)
		{
			Debug.WriteLine($"[MainForm] \ud83c\udfb5 试听版本: {song.Name}, 片段: {trialStart / 1000}s - {trialEnd / 1000}s");
			if (await TryApplyUnblockAsync(song, selectedQualityLevel, cancellationToken).ConfigureAwait(continueOnCapturedContext: false))
			{
				return SongResolveResult.Success(usedUnblock: true);
			}
			if (!(trialEnd > trialStart && resolvedSize > 0))
			{
				Debug.WriteLine($"[MainForm] ⚠\ufe0f 试听片段无效（start={trialStart}, end={trialEnd}, size={resolvedSize}），视为资源不可用");
				song.IsAvailable = false;
				if (await TryApplyUnblockAsync(song, selectedQualityLevel, cancellationToken).ConfigureAwait(continueOnCapturedContext: false))
				{
					return SongResolveResult.Success(usedUnblock: true);
				}
				return SongResolveResult.NotAvailable();
			}
		}

		ApplyOfficialUrl(songUrl, resolvedSize, isTrial, trialStart, trialEnd);
		return SongResolveResult.Success();
	}

	private async Task<(SongResolveResult Result, string? Url)> ResolveShareUrlAsync(SongInfo song, QualityLevel quality, CancellationToken cancellationToken)
	{
		if (song == null || string.IsNullOrWhiteSpace(song.Id))
		{
			return (SongResolveResult.Failed(), null);
		}
		SongResolveResult resolve = await ResolveSongPlaybackAsync(song, quality, cancellationToken, suppressStatusUpdates: true).ConfigureAwait(continueOnCapturedContext: false);
		if (resolve.Status != SongResolveStatus.Success)
		{
			return (resolve, null);
		}
		string url = (!string.IsNullOrWhiteSpace(song.Url) ? song.Url : null);
		return (resolve, url);
	}

	private async Task PlaySong(SongInfo song)
	{
        Debug.WriteLine("[MainForm] PlaySong 被调用（用户主动播放）: song=" + song?.Name);
        if (song == null)
        {
                return;
        }
        CancelPendingPlaceholderPlayback("new play request");
        if (TryQueuePlaceholderPlayback(song))
        {
                return;
        }
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

private void AnnounceSongLoadingForActivation(SongInfo song)
{
        if (song == null)
        {
                return;
        }
        string statusText;
        if (IsPlaceholderSong(song))
        {
                statusText = "歌曲正在加载中，请稍候...";
        }
        else
        {
                string name = (string.IsNullOrWhiteSpace(song.Name) ? "歌曲" : song.Name);
                statusText = "正在获取歌曲数据: " + name;
        }
        AnnounceStatusBarMessage(statusText);
}

private Task PlaySongByIndex(int index)
{
        if (_currentSongs == null || index < 0 || index >= _currentSongs.Count)
        {
                return Task.CompletedTask;
        }
        SongInfo song = _currentSongs[index];
        AnnounceSongLoadingForActivation(song);
        CancelPendingPlaceholderPlayback("new play request");
        if (TryQueuePlaceholderPlayback(song, index))
        {
                return Task.CompletedTask;
        }
        return PlaySong(song);
}

	private bool IsSwitchingTrack => Volatile.Read(ref _switchingTrackCount) > 0;

	private (CancellationToken Token, long Version) BeginPlaybackRequestScope()
	{
		CancellationTokenSource newCts = new CancellationTokenSource();
		CancellationTokenSource? previousCts = null;

		lock (_playbackCancellationLock)
		{
			previousCts = _playbackCancellation;
			_playbackCancellation = newCts;
		}

		CancelAndDisposePlaybackRequest(previousCts, "superseded");

		long requestVersion = Interlocked.Increment(ref _playRequestVersion);
		Debug.WriteLine($"[MainForm] New playback request created: version={requestVersion}");
		return (newCts.Token, requestVersion);
	}

	private void CancelAndDisposePlaybackRequest(CancellationTokenSource? cts, string reason)
	{
		if (cts == null)
		{
			return;
		}

		try
		{
			cts.Cancel();
		}
		catch (ObjectDisposedException)
		{
		}
		finally
		{
			try
			{
				cts.Dispose();
			}
			catch (ObjectDisposedException)
			{
			}
		}

		if (!string.IsNullOrWhiteSpace(reason))
		{
			Debug.WriteLine("[MainForm] Previous playback request cancelled: " + reason);
		}
	}

	private void CancelActivePlaybackRequest(string reason)
	{
		CancellationTokenSource? ctsToCancel = null;
		lock (_playbackCancellationLock)
		{
			ctsToCancel = _playbackCancellation;
			_playbackCancellation = null;
		}

		CancelAndDisposePlaybackRequest(ctsToCancel, reason);
	}

	private bool IsPlaybackRequestCurrent(long requestVersion, CancellationToken cancellationToken)
	{
		if (!IsCurrentPlayRequest(requestVersion))
		{
			return false;
		}

		lock (_playbackCancellationLock)
		{
			return _playbackCancellation != null && _playbackCancellation.Token == cancellationToken;
		}
	}

	private async Task PlaySongDirectWithCancellation(SongInfo song, bool isAutoPlayback = false, QualityLevel? requestedQuality = null, int? queueIndexHint = null)
	{
		Debug.WriteLine($"[MainForm] PlaySongDirectWithCancellation called: song={song?.Name}, isAutoPlayback={isAutoPlayback}");
		if (_isApplicationExitRequested)
		{
			Debug.WriteLine("[MainForm] Exit requested, skipping play request");
			return;
		}

		if (_audioEngine == null || song == null)
		{
			Debug.WriteLine("[MainForm ERROR] _audioEngine is null or song is null");
			return;
		}

		CancelPendingPlaceholderPlayback("new playback");

		ResetBufferingRecoveryTracking();

		if (_seekManager != null)
		{
			try
			{
				_seekManager.CancelPendingSeeks();
			}
			catch (Exception ex)
			{
				Debug.WriteLine("[MainForm] CancelPendingSeeks failed before switch: " + ex.Message);
			}
		}

		try
		{
			_suppressAutoAdvance = true;
			_audioEngine.Stop();
		}
		catch (Exception ex)
		{
			Debug.WriteLine("[MainForm] Stop before switch failed: " + ex.Message);
		}

		(CancellationToken cancellationToken, long requestVersion) = BeginPlaybackRequestScope();

		TimeSpan timeSinceLastRequest = DateTime.Now - _lastPlayRequestTime;
		if (timeSinceLastRequest.TotalMilliseconds < MIN_PLAY_REQUEST_INTERVAL_MS)
		{
			int delayMs = checked(MIN_PLAY_REQUEST_INTERVAL_MS - (int)timeSinceLastRequest.TotalMilliseconds);
			Debug.WriteLine($"[MainForm] Play request throttled by {delayMs}ms");
			try
			{
				await Task.Delay(delayMs, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			}
			catch (TaskCanceledException)
			{
				Debug.WriteLine("[MainForm] Play request canceled during throttle delay");
				return;
			}
		}

		_lastPlayRequestTime = DateTime.Now;
		if (_isApplicationExitRequested || cancellationToken.IsCancellationRequested || !IsPlaybackRequestCurrent(requestVersion, cancellationToken))
		{
			Debug.WriteLine($"[MainForm] Play flow skipped due exit/cancel/stale request: version={requestVersion}");
			return;
		}

		if (IsPlaceholderSong(song))
		{
			SongInfo? resolvedPlaceholderSong = await TryResolvePlaceholderSongForPlaybackAsync(song, queueIndexHint, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			if (resolvedPlaceholderSong == null || IsPlaceholderSong(resolvedPlaceholderSong))
			{
				if (cancellationToken.IsCancellationRequested || !IsPlaybackRequestCurrent(requestVersion, cancellationToken))
				{
					Debug.WriteLine("[MainForm] Placeholder play request canceled before queueing pending playback");
					return;
				}

				Debug.WriteLine("[MainForm] Placeholder unresolved, queue pending playback");
				TryQueuePlaceholderPlayback(song, queueIndexHint, ResolvePlaceholderPlaybackSource());
				return;
			}

			song = resolvedPlaceholderSong;
		}

		Interlocked.Increment(ref _switchingTrackCount);
		try
		{
			await PlaySongDirect(song, cancellationToken, isAutoPlayback, requestVersion, requestedQuality).ConfigureAwait(continueOnCapturedContext: false);
		}
		finally
		{
			Interlocked.Decrement(ref _switchingTrackCount);
		}
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
		}
		else
		{
			SetPlaybackLoadingState(isLoading, statusMessage);
			UpdateStatusBar(statusMessage);
		}
	}

	private async Task<bool> ShowPurchaseLinkDialogAsync(SongInfo song, CancellationToken cancellationToken)
	{
		if (song == null || string.IsNullOrWhiteSpace(song.Id) || base.IsDisposed || cancellationToken.IsCancellationRequested)
		{
			return false;
		}
		string encodedSongId = Uri.EscapeDataString(song.Id);
		string purchaseUrl = "https://music.163.com/#/payfee?songId=" + encodedSongId;
		TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		PurchaseLinkDialog? activeDialog = null;
		void ShowDialog()
		{
			if (cancellationToken.IsCancellationRequested || base.IsDisposed)
			{
				tcs.TrySetCanceled(cancellationToken);
				return;
			}
			try
			{
				using PurchaseLinkDialog purchaseLinkDialog = new PurchaseLinkDialog(song.Name, song.Album, purchaseUrl);
				activeDialog = purchaseLinkDialog;
				DialogResult dialogResult = purchaseLinkDialog.ShowDialog(this);
				tcs.TrySetResult(dialogResult == DialogResult.OK && purchaseLinkDialog.PurchaseRequested);
			}
			catch (Exception exception)
			{
				tcs.TrySetException(exception);
			}
			finally
			{
				activeDialog = null;
			}
		}
		if (base.IsHandleCreated == false)
		{
			return false;
		}
		BeginInvoke((Action)ShowDialog);
		using (cancellationToken.Register(delegate
		{
			try
			{
				if (activeDialog is not null && activeDialog.IsDisposed == false && base.IsHandleCreated)
				{
					BeginInvoke((Action)delegate
					{
						try
						{
							if (activeDialog is not null && activeDialog.IsDisposed == false)
							{
								activeDialog.Close();
							}
						}
						catch
						{
						}
					});
				}
			}
			catch
			{
			}
			tcs.TrySetCanceled(cancellationToken);
		}))
		{
			try
			{
				return await tcs.Task.ConfigureAwait(continueOnCapturedContext: false);
			}
			catch (OperationCanceledException)
			{
				return false;
			}
		}
	}

private async Task PlaySongDirect(SongInfo song, CancellationToken cancellationToken, bool isAutoPlayback = false, long playRequestVersion = 0L, QualityLevel? requestedQuality = null)
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
		_positionCoordinator?.ResetForTrack(song.Id);
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
			song.IsTrial = preloadedData.IsTrial;
			song.TrialStart = preloadedData.TrialStart;
			song.TrialEnd = preloadedData.TrialEnd;
			song.IsUnblocked = preloadedData.IsUnblocked;
			song.UnblockSource = preloadedData.UnblockSource ?? string.Empty;
			if (preloadedData.CustomHeaders != null)
			{
				song.CustomHeaders = new Dictionary<string, string>(preloadedData.CustomHeaders, StringComparer.OrdinalIgnoreCase);
			}
		}
		bool loadingStateActive = false;
		try
		{
			UpdateLoadingState(isLoading: true, "正在获取歌曲数据: " + song.Name, playRequestVersion);
			loadingStateActive = true;
			string defaultQualityName = _config.DefaultQuality ?? "超清母带";
			QualityLevel selectedQuality = requestedQuality ?? NeteaseApiClient.GetQualityLevelFromName(defaultQualityName);
			SongResolveResult resolveResult = await ResolveSongPlaybackAsync(song, selectedQuality, cancellationToken, suppressStatusUpdates: false).ConfigureAwait(continueOnCapturedContext: false);
			switch (resolveResult.Status)
			{
			case SongResolveStatus.Success:
				break;
			case SongResolveStatus.Canceled:
				UpdateLoadingState(isLoading: false, "播放已取消", playRequestVersion);
				loadingStateActive = false;
				return;
			case SongResolveStatus.NotAvailable:
				UpdateLoadingState(isLoading: false, "歌曲不存在，已跳过", playRequestVersion);
				loadingStateActive = false;
				HandleSongResourceNotFoundDuringPlayback(song, isAutoPlayback);
				return;
			case SongResolveStatus.PaidAlbumNotPurchased:
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
			default:
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
				Debug.WriteLine("[MainForm] 播放请求已取消（播放前）");
				return;
			}
			ApplyLoadedLyricsContext(null);
			Debug.WriteLine("[MainForm] 已清空旧歌词，准备播放新歌曲");
			bool playResult = await _audioEngine.PlayAsync(song, cancellationToken, preloadedData);
			Debug.WriteLine($"[MainForm] _audioEngine.PlayAsync() 返回: {playResult}");
                        if (!playResult)
                        {
                                UpdateLoadingState(isLoading: false, "播放失败", playRequestVersion);
                                loadingStateActive = false;
                                Debug.WriteLine("[MainForm ERROR] 播放失败");
                                AnnouncePlaybackFailure();
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
			PersistPlaybackState();
		}
		catch (OperationCanceledException)
		{
			Debug.WriteLine("[MainForm] 播放请求被取消");
			UpdateLoadingState(isLoading: false, "播放已取消", playRequestVersion);
		}
		catch (Exception ex6)
		{
			Exception ex7 = ex6;
			Exception ex8 = ex7;
			if (!cancellationToken.IsCancellationRequested)
			{
                                Debug.WriteLine("[MainForm ERROR] PlaySongDirect 异常: " + ex8.Message);
                                Debug.WriteLine("[MainForm ERROR] 堆栈: " + ex8.StackTrace);
                                UpdateLoadingState(isLoading: false, "播放失败", playRequestVersion);
                                MessageBox.Show("播放失败: " + ex8.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                                AnnouncePlaybackFailure(ex8.Message);
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

        private async Task PlayPreviousAsync(bool isManual = true)
        {
		Debug.WriteLine($"[MainForm] PlayPrevious 被调用 (isManual={isManual})");
		PlayMode playMode = _audioEngine?.PlayMode ?? PlayMode.Loop;
		SongInfo currentSong = _audioEngine?.CurrentSong;
		PlayMode effectivePlayMode = ResolveEffectivePlayModeForPlayback(playMode, currentSong);
		if (effectivePlayMode == PlayMode.Random && !EnsureRandomQueueReadyBeforeMove(isNext: false, isManual))
		{
			return;
		}
		if (currentSong != null && isManual)
		{
			_playbackQueue.AdvanceForPlayback(currentSong, effectivePlayMode, _currentViewSource);
			Debug.WriteLine("[MainForm] 已同步播放队列到当前歌曲: " + currentSong.Name);
		}
		bool flag = isManual;
		bool flag2 = flag;
		if (flag2)
		{
			flag2 = await TryPlayManualDirectionalAsync(isNext: false, effectivePlayMode);
		}
		if (flag2)
		{
			return;
		}
		PlaybackMoveResult result = _playbackQueue.MovePrevious(effectivePlayMode, isManual, _currentViewSource);
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

        private async Task PlayNextAsync(bool isManual = true)
        {
		Debug.WriteLine($"[MainForm] PlayNext 被调用 (isManual={isManual})");
		PlayMode playMode = _audioEngine?.PlayMode ?? PlayMode.Loop;
		SongInfo currentSong = _audioEngine?.CurrentSong;
		PlayMode effectivePlayMode = ResolveEffectivePlayModeForPlayback(playMode, currentSong);
		if (effectivePlayMode != PlayMode.LoopOne)
		{
			await EnsurePersonalFmQueueExpandedForNextAsync(currentSong, requireImmediateNext: true).ConfigureAwait(continueOnCapturedContext: false);
		}
		if (effectivePlayMode == PlayMode.Random && !EnsureRandomQueueReadyBeforeMove(isNext: true, isManual))
		{
			return;
		}
		if (currentSong != null && isManual)
		{
			_playbackQueue.AdvanceForPlayback(currentSong, effectivePlayMode, _currentViewSource);
			Debug.WriteLine("[MainForm] 已同步播放队列到当前歌曲: " + currentSong.Name);
		}
		bool flag = isManual;
		bool flag2 = flag;
		if (flag2)
		{
			flag2 = await TryPlayManualDirectionalAsync(isNext: true, effectivePlayMode);
		}
		if (flag2)
		{
			return;
		}
		PlaybackMoveResult result = _playbackQueue.MoveNext(effectivePlayMode, isManual, _currentViewSource);
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
			bool isPersonalFmSequential = effectivePlayMode == PlayMode.Sequential && IsPersonalFmPlaybackActive(currentSong);
			if (result.ReachedBoundary && effectivePlayMode == PlayMode.Sequential && IsPersonalFmPlaybackActive(currentSong))
			{
				await EnsurePersonalFmQueueExpandedForNextAsync(currentSong, requireImmediateNext: true).ConfigureAwait(continueOnCapturedContext: false);
				PlaybackMoveResult retryResult = _playbackQueue.MoveNext(effectivePlayMode, isManual, _currentViewSource);
				if (retryResult.HasSong)
				{
					await ExecutePlayNextResultAsync(retryResult).ConfigureAwait(continueOnCapturedContext: false);
					return;
				}
				result = retryResult;
			}
			if (isManual && result.ReachedBoundary)
			{
				UpdateStatusBar(isPersonalFmSequential ? "私人 FM 正在扩展，请稍后重试" : "已经是最后一首");
			}
			else if (!isManual && effectivePlayMode == PlayMode.Sequential && result.ReachedBoundary)
			{
				if (isPersonalFmSequential)
				{
					UpdateStatusBar("私人 FM 下一首加载失败，可手动重试切歌");
				}
				else
				{
					HandleSequentialPlaybackCompleted();
				}
			}
		}
		else
		{
			await ExecutePlayNextResultAsync(result).ConfigureAwait(continueOnCapturedContext: false);
		}
	}

	private async Task<bool> TryPlayManualDirectionalAsync(bool isNext, PlayMode? effectivePlayMode = null)
	{
		PlayMode playMode = effectivePlayMode ?? ResolveEffectivePlayModeForPlayback(_audioEngine?.PlayMode ?? PlayMode.Loop, _audioEngine?.CurrentSong);
		SongInfo currentSong = _audioEngine?.CurrentSong;
		HashSet<string> attemptedCandidates = new HashSet<string>(StringComparer.Ordinal);
		int maxAttempts = CalculateManualNavigationLimit();
		for (int attempt = 0; attempt < maxAttempts; attempt = checked(attempt + 1))
		{
			PlaybackMoveResult result = (isNext ? _playbackQueue.MoveNext(playMode, isManual: true, _currentViewSource) : _playbackQueue.MovePrevious(playMode, isManual: true, _currentViewSource));
			if (result.QueueEmpty)
			{
				Debug.WriteLine("[MainForm] Manual navigation stopped: queue is empty");
				if (isNext)
				{
					UpdateTrayIconTooltip(null);
				}
				UpdateStatusBar("Playback queue is empty");
				return true;
			}
			if (!result.HasSong)
			{
				if (result.ReachedBoundary)
				{
					UpdateStatusBar(isNext ? "Already at the last song" : "Already at the first song");
				}
				RestoreQueuePosition(currentSong, playMode);
				return true;
			}

			SongInfo candidate = result.Song;
			if (candidate == null)
			{
				Debug.WriteLine("[MainForm] Manual navigation encountered null candidate, continue searching");
				RestoreQueuePosition(currentSong, playMode);
				continue;
			}

			string attemptKey = BuildManualNavigationAttemptKey(result);
			if (!attemptedCandidates.Add(attemptKey))
			{
				Debug.WriteLine("[MainForm] Manual navigation loop detected, stop searching: " + attemptKey);
				UpdateStatusBar("No playable song found");
				RestoreQueuePosition(currentSong, playMode);
				return true;
			}

			if (IsPlaceholderSong(candidate))
			{
				Debug.WriteLine($"[MainForm] Manual navigation hit placeholder candidate, resolve by index: key={attemptKey}");
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

			ManualNavigationAvailability availability = await PrepareSongForManualNavigationAsync(result);
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
				string message = (string.IsNullOrEmpty(friendlyName) ? "Skipped unavailable song (official resource missing)" : ("Skipped: " + friendlyName + " (official resource missing)"));
				UpdateStatusBar(message);
				Debug.WriteLine("[MainForm] Manual " + (isNext ? "next" : "previous") + " skipped missing song: " + candidate?.Id + " - " + friendlyName);
				RemoveSongFromQueueAndCaches(candidate);
				RestoreQueuePosition(currentSong, playMode);
				continue;
			}
			if (string.IsNullOrEmpty(friendlyName))
			{
				UpdateStatusBar("Failed to get playback URL");
			}
			else
			{
				UpdateStatusBar("Failed to get playback URL: " + friendlyName);
			}
			Debug.WriteLine("[MainForm] Manual navigation failed to get playback URL: " + candidate?.Id);
			RestoreQueuePosition(currentSong, playMode);
			return true;
		}
		UpdateStatusBar("No playable song found");
		RestoreQueuePosition(currentSong, playMode);
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

	private static string BuildManualNavigationAttemptKey(PlaybackMoveResult moveResult)
	{
		if (moveResult?.Song == null)
		{
			return "none";
		}

		if (!string.IsNullOrWhiteSpace(moveResult.Song.Id))
		{
			return "id:" + moveResult.Song.Id;
		}

		return moveResult.Route switch
		{
			PlaybackRoute.Queue => "queue:" + moveResult.QueueIndex,
			PlaybackRoute.ReturnToQueue => "return:" + moveResult.QueueIndex,
			PlaybackRoute.Injection => "injection:" + moveResult.InjectionIndex,
			PlaybackRoute.PendingInjection => "pending:" + moveResult.InjectionIndex,
			_ => "song:" + (moveResult.Song.Name ?? string.Empty)
		};
	}

	private async Task<ManualNavigationAvailability> PrepareSongForManualNavigationAsync(PlaybackMoveResult moveResult)
	{
		if (moveResult?.Song == null)
		{
			return ManualNavigationAvailability.Failed;
		}

		SongInfo song = moveResult.Song;
		QualityLevel selectedQuality = GetCurrentQuality();

		using CancellationTokenSource resolveCts = new CancellationTokenSource(TimeSpan.FromSeconds(20.0));
		try
		{
			SongResolveResult resolveResult = await ResolveSongPlaybackAsync(song, selectedQuality, resolveCts.Token, suppressStatusUpdates: true).ConfigureAwait(continueOnCapturedContext: false);
			switch (resolveResult.Status)
			{
			case SongResolveStatus.Success:
				Debug.WriteLine($"[MainForm] Manual navigation resolve success: {song.Id}, unblock={resolveResult.UsedUnblock}");
				return ManualNavigationAvailability.Success;
			case SongResolveStatus.NotAvailable:
				return ManualNavigationAvailability.Missing;
			case SongResolveStatus.PaidAlbumNotPurchased:
				Debug.WriteLine("[MainForm] Manual navigation stopped by paid album restriction: " + song.Id);
				return ManualNavigationAvailability.Failed;
			case SongResolveStatus.Canceled:
				Debug.WriteLine("[MainForm] Manual navigation resolve canceled: " + song.Id);
				return ManualNavigationAvailability.Failed;
			default:
				Debug.WriteLine("[MainForm] Manual navigation resolve failed: " + song.Id);
				return ManualNavigationAvailability.Failed;
			}
		}
		catch (OperationCanceledException)
		{
			Debug.WriteLine("[MainForm] Manual navigation resolve timeout/canceled: " + song.Id);
			return ManualNavigationAvailability.Failed;
		}
		catch (Exception ex)
		{
			Debug.WriteLine("[MainForm] Manual navigation resolve exception: " + ex.Message);
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
			int? queueIndexHint = null;
			switch (result.Route)
			{
			case PlaybackRoute.Injection:
				UpdateFocusForInjection(result.Song, result.InjectionIndex);
				break;
			case PlaybackRoute.Queue:
			case PlaybackRoute.ReturnToQueue:
				queueIndexHint = result.QueueIndex;
				UpdateFocusForQueue(result.QueueIndex, result.Song);
				break;
			}
			await PlaySongDirectWithCancellation(result.Song, isAutoPlayback: true, requestedQuality: null, queueIndexHint: queueIndexHint);
		}
	}

	private async Task ExecutePlayNextResultAsync(PlaybackMoveResult result)
	{
		if (result?.Song == null)
		{
			Debug.WriteLine("[MainForm] ExecutePlayNextResultAsync received null song");
			return;
		}

		int? queueIndexHint = null;
		switch (result.Route)
		{
		case PlaybackRoute.PendingInjection:
			UpdateFocusForInjection(result.Song, result.InjectionIndex);
			await PlaySongDirectWithCancellation(result.Song);
			UpdateStatusBar("Injection: " + result.Song.Name + " - " + result.Song.Artist);
			break;
		case PlaybackRoute.Injection:
			UpdateFocusForInjection(result.Song, result.InjectionIndex);
			await PlaySongDirectWithCancellation(result.Song, isAutoPlayback: true);
			break;
		case PlaybackRoute.Queue:
		case PlaybackRoute.ReturnToQueue:
			queueIndexHint = result.QueueIndex;
			UpdateFocusForQueue(result.QueueIndex, result.Song);
			await PlaySongDirectWithCancellation(result.Song, isAutoPlayback: true, requestedQuality: null, queueIndexHint: queueIndexHint);
			break;
		default:
			Debug.WriteLine("[MainForm] ExecutePlayNextResultAsync unmatched route");
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
			song.IsAvailable = false;
			if (base.InvokeRequired)
			{
				BeginInvoke(ExecuteOnUiThread);
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
			bool value = RemoveSongFromQueueAndCaches(song);
			Debug.WriteLine($"[MainForm] RemoveSongById({song.Id}) => {value}");
			if (isAutoPlayback)
			{
				string message = (string.IsNullOrEmpty(text) ? "已跳过无法播放的歌曲（官方资源不存在）" : ("已跳过：" + text + "（官方资源不存在）"));
				UpdateStatusBar(message);
				Debug.WriteLine("[MainForm] 跳过缺失歌曲(自动): " + song.Id + " - " + text);
				bool flag = _playbackQueue.CurrentQueue.Count > 0;
				bool flag2 = _playbackQueue.HasPendingInjection || _playbackQueue.IsInInjection;
				if (flag || flag2)
				{
                                        PlayNextAsync(isManual: false).SafeFireAndForget("Auto skip missing");
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
		string queueSource = _playbackQueue?.QueueSource ?? string.Empty;
		if (IsPersonalFmViewSource(queueSource))
		{
			UpdatePersonalFmPlaybackFocus(expectedSong, queueIndex, triggerBackgroundExpansion: true);
		}
		if (!IsFocusFollowPlaybackEnabled())
		{
			return;
		}
		bool viewMatched = IsSameViewSourceForFocus(queueSource, _currentViewSource);
		if (expectedSong != null)
		{
			RememberPlaybackFocusForSource(queueSource, expectedSong);
		}

		int focusIndex = queueIndex;
		if (expectedSong != null && _currentSongs != null && _currentSongs.Count > 0)
		{
			bool queueIndexInRange = queueIndex >= 0 && queueIndex < _currentSongs.Count;
			if (queueIndexInRange)
			{
				SongInfo queueSong = _currentSongs[queueIndex];
				if (IsPlaceholderSong(expectedSong))
				{
					focusIndex = queueIndex;
				}
				else if (!string.IsNullOrWhiteSpace(expectedSong.Id) && queueSong != null && string.Equals(queueSong.Id, expectedSong.Id, StringComparison.OrdinalIgnoreCase))
				{
					focusIndex = queueIndex;
				}
				else if (expectedSong.IsCloudSong && !string.IsNullOrWhiteSpace(expectedSong.CloudSongId) && queueSong != null && queueSong.IsCloudSong && string.Equals(queueSong.CloudSongId, expectedSong.CloudSongId, StringComparison.OrdinalIgnoreCase))
				{
					focusIndex = queueIndex;
				}
				else
				{
					focusIndex = FindSongIndexInList(_currentSongs, expectedSong);
				}
			}
			else
			{
				focusIndex = FindSongIndexInList(_currentSongs, expectedSong);
			}
		}

		if (viewMatched && resultListView != null && focusIndex >= 0 && focusIndex < resultListView.Items.Count)
		{
			_lastListViewFocusedIndex = focusIndex;
			if (base.Visible)
			{
				ListViewItem listViewItem = resultListView.Items[focusIndex];
				listViewItem.Selected = true;
				listViewItem.Focused = true;
				resultListView.EnsureVisible(focusIndex);
			}
			Debug.WriteLine($"[MainForm] Focus followed (queue): queueIndex={queueIndex}, viewIndex={focusIndex}, source={queueSource}, visible={base.Visible}");
		}
		else
		{
			if (expectedSong != null && queueIndex >= 0)
			{
				RememberPlaybackFocusForSource(queueSource, expectedSong, queueIndex);
			}

			Debug.WriteLine(string.Format("[MainForm] Focus not followed: queueSource={0}, currentView={1}, queueIndex={2}, expectedSong={3}", queueSource, _currentViewSource, queueIndex, expectedSong?.Id ?? expectedSong?.CloudSongId ?? "null"));
		}
	}

	private void UpdateFocusForInjection(SongInfo song, int viewIndex)
	{
		if (song == null)
		{
			return;
		}

		string source = string.Empty;
		if (!_playbackQueue.TryGetInjectionSource(song.Id, out source) || string.IsNullOrWhiteSpace(source))
		{
			source = song.ViewSource ?? string.Empty;
		}
		if (IsPersonalFmViewSource(source))
		{
			UpdatePersonalFmPlaybackFocus(song, queueIndex: -1, triggerBackgroundExpansion: true);
		}
		if (!IsFocusFollowPlaybackEnabled())
		{
			return;
		}

		RememberPlaybackFocusForSource(source, song);
		if (IsSameViewSourceForFocus(source, _currentViewSource))
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

			if (num >= 0 && resultListView != null && num < resultListView.Items.Count)
			{
				_lastListViewFocusedIndex = num;
				if (base.Visible)
				{
					ListViewItem listViewItem = resultListView.Items[num];
					listViewItem.Selected = true;
					listViewItem.Focused = true;
					resultListView.EnsureVisible(num);
				}
				Debug.WriteLine($"[MainForm] Focus followed (injection): viewIndex={num}, injectionIndex={viewIndex}, visible={base.Visible}, source={source}");
			}
			else
			{
				Debug.WriteLine($"[MainForm] Focus pending (injection): source={source}, currentView={_currentViewSource}, song={(song.Id ?? song.CloudSongId ?? "null")}");
			}
		}
		else
		{
			Debug.WriteLine($"[MainForm] Focus not followed (injection): source={source}, currentView={_currentViewSource}, song={(song.Id ?? song.CloudSongId ?? "null")}");
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
		PodcastRadioInfo currentPodcast = _currentPodcast;
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
					Invoke(delegate
					{
                                        PlayNextAsync().SafeFireAndForget("Command queue next");
						success = true;
					});
				}
				else
				{
                                PlayNextAsync().SafeFireAndForget("Command queue next");
					success = true;
				}
			}, ct).ConfigureAwait(continueOnCapturedContext: false);
			return success ? CommandResult.Success : CommandResult.Error("下一曲执行失败");
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Exception ex3 = ex2;
			return CommandResult.Error(ex3);
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
					Invoke(delegate
					{
                                        PlayPreviousAsync().SafeFireAndForget("Command queue previous");
						success = true;
					});
				}
				else
				{
                                PlayPreviousAsync().SafeFireAndForget("Command queue previous");
					success = true;
				}
			}, ct).ConfigureAwait(continueOnCapturedContext: false);
			return success ? CommandResult.Success : CommandResult.Error("上一曲执行失败");
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Exception ex3 = ex2;
			return CommandResult.Error(ex3);
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

	private void OnAudioEngineStateChanged(object sender, PlaybackState newState)
	{
		PlaybackState oldState;
		lock (_stateCacheLock)
		{
			oldState = _cachedPlaybackState;
			_cachedPlaybackState = newState;
		}

		Debug.WriteLine($"[MainForm] Playback state changed: {oldState} -> {newState}");

		SafeInvoke(delegate
		{
			UpdateUIForPlaybackState(newState);
			bool isPlaying = IsPlaybackActiveState(newState);
			DownloadBandwidthCoordinator.Instance.NotifyPlaybackStateChanged(isPlaying);
			UpdatePlaybackPowerRequests(isPlaying);
		});
	}

	private void OnPlaybackStateChanged(object sender, StateTransitionEventArgs e)
	{
		OnAudioEngineStateChanged(sender, e.NewState);
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
		UpdateTrayPlayPauseMenuTextForState(state);
	}

	private static bool IsPlaybackActiveState(PlaybackState state)
	{
		return state == PlaybackState.Playing
			|| state == PlaybackState.Buffering
			|| state == PlaybackState.Loading;
	}

	private void UpdatePlaybackPowerRequests(bool isPlaying)
	{
		bool shouldPreventSleep = isPlaying && _preventSleepDuringPlayback;

		if (!shouldPreventSleep)
		{
			ClearPlaybackPowerRequests();
			return;
		}

		UpdateThreadExecutionState(shouldPreventSleep);

		if (!EnsurePlaybackPowerRequestHandle())
		{
			return;
		}

		UpdatePowerRequest(PowerRequestType.PowerRequestExecutionRequired, shouldPreventSleep, ref _powerRequestSleepActive);
		UpdatePowerRequest(PowerRequestType.PowerRequestDisplayRequired, shouldPreventSleep, ref _powerRequestDisplayActive);
		UpdatePowerRequest(PowerRequestType.PowerRequestSystemRequired, shouldPreventSleep, ref _powerRequestSystemActive);

		if (!_powerRequestSleepActive && !_powerRequestDisplayActive && !_powerRequestSystemActive)
		{
			Debug.WriteLine("[PowerRequest] No active request flags after update; keeping thread execution state only");
		}
	}

	private bool EnsurePlaybackPowerRequestHandle()
	{
		if (_powerRequestHandle != IntPtr.Zero)
		{
			return true;
		}

		if (_powerRequestRetryNotBeforeUtc > DateTime.UtcNow)
		{
			return false;
		}

		const string reason = "Audio playback in progress";
		_powerRequestReasonBuffer = Marshal.StringToHGlobalUni(reason);
		ReasonContext context = new ReasonContext
		{
			Version = POWER_REQUEST_CONTEXT_VERSION,
			Flags = POWER_REQUEST_CONTEXT_SIMPLE_STRING,
			ReasonString = _powerRequestReasonBuffer
		};

		_powerRequestHandle = PowerCreateRequest(ref context);
		if (_powerRequestHandle == IntPtr.Zero || _powerRequestHandle == new IntPtr(-1))
		{
			int error = Marshal.GetLastWin32Error();
			_powerRequestHandle = IntPtr.Zero;
			ReleasePowerRequestReasonBuffer();
			_lastPowerRequestRefreshUtc = DateTime.MinValue;
			RecordPowerRequestInitFailure(error);
			return false;
		}

		_powerRequestInitFailureCount = 0;
		_powerRequestRetryNotBeforeUtc = DateTime.MinValue;
		return true;
	}

	private void RecordPowerRequestInitFailure(int error)
	{
		_powerRequestInitFailureCount = Math.Min(_powerRequestInitFailureCount + 1, 8);
		double backoffSeconds = Math.Min(60.0, Math.Pow(2.0, _powerRequestInitFailureCount - 1));
		_powerRequestRetryNotBeforeUtc = DateTime.UtcNow.AddSeconds(backoffSeconds);
		Debug.WriteLine($"[PowerRequest] Create request failed: {error}, retry in {backoffSeconds:F0}s (attempt {_powerRequestInitFailureCount})");
	}

	private void UpdatePowerRequest(PowerRequestType requestType, bool enable, ref bool activeFlag)
	{
		if (_powerRequestHandle == IntPtr.Zero)
		{
			return;
		}
		if (enable)
		{
			if (activeFlag)
			{
				return;
			}
			if (PowerSetRequest(_powerRequestHandle, requestType))
			{
				activeFlag = true;
				Debug.WriteLine($"[PowerRequest] 已启用 {requestType}");
			}
			else
			{
				int error = Marshal.GetLastWin32Error();
				Debug.WriteLine($"[PowerRequest] 启用 {requestType} 失败: {error}");
			}
		}
		else if (activeFlag)
		{
			if (PowerClearRequest(_powerRequestHandle, requestType))
			{
				activeFlag = false;
				Debug.WriteLine($"[PowerRequest] 已关闭 {requestType}");
			}
			else
			{
				int error = Marshal.GetLastWin32Error();
				Debug.WriteLine($"[PowerRequest] 关闭 {requestType} 失败: {error}");
			}
		}
	}

	private void ClearPlaybackPowerRequests()
	{
		if (_powerRequestHandle == IntPtr.Zero)
		{
			_powerRequestSleepActive = false;
			_powerRequestDisplayActive = false;
			_powerRequestSystemActive = false;
			_lastPowerRequestRefreshUtc = DateTime.MinValue;
			UpdateThreadExecutionState(isPlaying: false);
			return;
		}
		try
		{
			if (_powerRequestSleepActive)
			{
				PowerClearRequest(_powerRequestHandle, PowerRequestType.PowerRequestExecutionRequired);
				_powerRequestSleepActive = false;
			}
			if (_powerRequestDisplayActive)
			{
				PowerClearRequest(_powerRequestHandle, PowerRequestType.PowerRequestDisplayRequired);
				_powerRequestDisplayActive = false;
			}
			if (_powerRequestSystemActive)
			{
				PowerClearRequest(_powerRequestHandle, PowerRequestType.PowerRequestSystemRequired);
				_powerRequestSystemActive = false;
			}
		}
		catch (Exception ex)
		{
			Debug.WriteLine("[PowerRequest] 清理失败: " + ex.Message);
		}
		finally
		{
			UpdateThreadExecutionState(isPlaying: false);
			CloseHandle(_powerRequestHandle);
			_powerRequestHandle = IntPtr.Zero;
			ReleasePowerRequestReasonBuffer();
			_lastPowerRequestRefreshUtc = DateTime.MinValue;
		}
	}

	private void UpdateThreadExecutionState(bool isPlaying)
	{
		try
		{
			uint flags = isPlaying
				? (ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED)
				: ES_CONTINUOUS;
			uint result = SetThreadExecutionState(flags);
			if (result == 0)
			{
				int error = Marshal.GetLastWin32Error();
				Debug.WriteLine($"[ExecutionState] 设置失败: {error}");
			}
		}
		catch (Exception ex)
		{
			Debug.WriteLine("[ExecutionState] 设置异常: " + ex.Message);
		}
	}

	private void ReleasePowerRequestReasonBuffer()
	{
		if (_powerRequestReasonBuffer != IntPtr.Zero)
		{
			Marshal.FreeHGlobal(_powerRequestReasonBuffer);
			_powerRequestReasonBuffer = IntPtr.Zero;
		}
	}

}
}
