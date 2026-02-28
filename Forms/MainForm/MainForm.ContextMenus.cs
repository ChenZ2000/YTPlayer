#nullable disable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using YTPlayer.Core;
using YTPlayer.Core.Playback;
using YTPlayer.Models;
using YTPlayer.Utils;
#pragma warning disable CS8632

namespace YTPlayer
{
public partial class MainForm
{
	private const string CurrentPlayingMenuCaptionBaseText = "当前播放";
	private const string ViewSourceMenuCaptionBaseText = "查看来源";

	private readonly ConcurrentDictionary<string, string> _currentPlayingMenuCaptionCache = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
	private readonly ConcurrentDictionary<string, string> _viewSourceMenuCaptionCache = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

	private int _currentPlayingMenuCaptionRequestToken;
	private int _viewSourceMenuCaptionRequestToken;
	private CancellationTokenSource? _currentPlayingMenuCaptionCts;
	private CancellationTokenSource? _viewSourceMenuCaptionCts;
	private CancellationTokenSource? _viewSourceMenuCaptionPrefetchCts;

	private static string BuildCurrentPlayingMenuCaption(string payload)
	{
		return string.IsNullOrWhiteSpace(payload) ? CurrentPlayingMenuCaptionBaseText : (CurrentPlayingMenuCaptionBaseText + "：" + payload.Trim());
	}

	private static string BuildViewSourceMenuCaption(string payload)
	{
		return string.IsNullOrWhiteSpace(payload) ? ViewSourceMenuCaptionBaseText : (ViewSourceMenuCaptionBaseText + "：" + payload.Trim());
	}

	private void InvalidateCurrentPlayingMenuCaptionRequests()
	{
		Interlocked.Increment(ref _currentPlayingMenuCaptionRequestToken);
		CancelCurrentPlayingMenuCaptionRequest();
	}

	private void InvalidateViewSourceMenuCaptionRequests()
	{
		Interlocked.Increment(ref _viewSourceMenuCaptionRequestToken);
		CancelViewSourceMenuCaptionRequest();
	}

	private CancellationToken ReplaceCurrentPlayingMenuCaptionRequest()
	{
		CancellationTokenSource next = new CancellationTokenSource();
		CancellationTokenSource previous = Interlocked.Exchange(ref _currentPlayingMenuCaptionCts, next);
		try
		{
			previous?.Cancel();
		}
		catch (ObjectDisposedException)
		{
		}
		finally
		{
			previous?.Dispose();
		}
		return next.Token;
	}

	private CancellationToken ReplaceViewSourceMenuCaptionRequest()
	{
		CancellationTokenSource next = new CancellationTokenSource();
		CancellationTokenSource previous = Interlocked.Exchange(ref _viewSourceMenuCaptionCts, next);
		try
		{
			previous?.Cancel();
		}
		catch (ObjectDisposedException)
		{
		}
		finally
		{
			previous?.Dispose();
		}
		return next.Token;
	}

	private CancellationToken ReplaceViewSourceMenuCaptionPrefetchRequest()
	{
		CancellationTokenSource next = new CancellationTokenSource();
		CancellationTokenSource previous = Interlocked.Exchange(ref _viewSourceMenuCaptionPrefetchCts, next);
		try
		{
			previous?.Cancel();
		}
		catch (ObjectDisposedException)
		{
		}
		finally
		{
			previous?.Dispose();
		}
		return next.Token;
	}

	private void CancelCurrentPlayingMenuCaptionRequest()
	{
		CancellationTokenSource previous = Interlocked.Exchange(ref _currentPlayingMenuCaptionCts, null);
		if (previous == null)
		{
			return;
		}
		try
		{
			previous.Cancel();
		}
		catch (ObjectDisposedException)
		{
		}
		finally
		{
			previous.Dispose();
		}
	}

	private void CancelViewSourceMenuCaptionRequest()
	{
		CancellationTokenSource previous = Interlocked.Exchange(ref _viewSourceMenuCaptionCts, null);
		if (previous == null)
		{
			return;
		}
		try
		{
			previous.Cancel();
		}
		catch (ObjectDisposedException)
		{
		}
		finally
		{
			previous.Dispose();
		}
	}

	private void CancelViewSourceMenuCaptionPrefetchRequest()
	{
		CancellationTokenSource previous = Interlocked.Exchange(ref _viewSourceMenuCaptionPrefetchCts, null);
		if (previous == null)
		{
			return;
		}
		try
		{
			previous.Cancel();
		}
		catch (ObjectDisposedException)
		{
		}
		finally
		{
			previous.Dispose();
		}
	}

	private async Task<string?> ResolveFirstNonEmptyCaptionAsync(IEnumerable<Func<CancellationToken, Task<string?>>> providers, CancellationToken cancellationToken)
	{
		if (providers == null)
		{
			return null;
		}
		using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		List<Task<string?>> runningTasks = new List<Task<string?>>();
		foreach (Func<CancellationToken, Task<string?>> provider in providers)
		{
			if (provider != null)
			{
				runningTasks.Add(InvokeCaptionProviderSafeAsync(provider, linkedCts.Token));
			}
		}
		while (runningTasks.Count > 0)
		{
			Task<string?> completed = await Task.WhenAny(runningTasks).ConfigureAwait(false);
			runningTasks.Remove(completed);
			if (cancellationToken.IsCancellationRequested)
			{
				return null;
			}
			string? result = null;
			try
			{
				result = await completed.ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				continue;
			}
			if (!string.IsNullOrWhiteSpace(result))
			{
				try
				{
					linkedCts.Cancel();
				}
				catch (ObjectDisposedException)
				{
				}
				return result.Trim();
			}
		}
		return null;
	}

	private async Task<string?> InvokeCaptionProviderSafeAsync(Func<CancellationToken, Task<string?>> provider, CancellationToken cancellationToken)
	{
		if (provider == null || cancellationToken.IsCancellationRequested)
		{
			return null;
		}
		try
		{
			return await provider(cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			return null;
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"[MenuCaption] 来源提供器执行失败: {ex.Message}");
			return null;
		}
	}

	private void ResetCurrentPlayingMenuCaptionToBase()
	{
		if (currentPlayingMenuItem != null)
		{
			currentPlayingMenuItem.Text = CurrentPlayingMenuCaptionBaseText;
		}
	}

	private void ResetViewSourceMenuCaptionToBase(bool invalidatePendingRequest)
	{
		if (invalidatePendingRequest)
		{
			InvalidateViewSourceMenuCaptionRequests();
		}
		if (viewSourceMenuItem != null)
		{
			viewSourceMenuItem.Text = ViewSourceMenuCaptionBaseText;
		}
	}

	private void PrefetchCurrentPlayingContextMenuCaptions(SongInfo? song)
	{
		if (song == null)
		{
			CancelViewSourceMenuCaptionPrefetchRequest();
			return;
		}
		CancellationToken cancellationToken = ReplaceViewSourceMenuCaptionPrefetchRequest();
		_ = PrefetchCurrentPlayingViewSourceCaptionAsync(song, cancellationToken);
	}

	private async Task PrefetchCurrentPlayingViewSourceCaptionAsync(SongInfo song, CancellationToken cancellationToken)
	{
		if (song == null || cancellationToken.IsCancellationRequested)
		{
			return;
		}
		SongInfo effectiveSong = song;
		string? viewSource = ResolveCurrentPlayingViewSource(effectiveSong);
		if (string.IsNullOrWhiteSpace(viewSource))
		{
			try
			{
				await Task.Delay(350, cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				return;
			}
			if (cancellationToken.IsCancellationRequested)
			{
				return;
			}
			SongInfo currentSong = _audioEngine?.CurrentSong;
			if (currentSong != null && AreSameSongForMenuCaption(currentSong, song))
			{
				effectiveSong = currentSong;
			}
			viewSource = ResolveCurrentPlayingViewSource(effectiveSong);
			if (string.IsNullOrWhiteSpace(viewSource))
			{
				return;
			}
		}
		string cacheKey = NormalizeViewSourceCaptionCacheKey(viewSource);
		if (!string.IsNullOrWhiteSpace(cacheKey) && _viewSourceMenuCaptionCache.ContainsKey(cacheKey))
		{
			return;
		}
		await EnsureViewSourceMenuCaptionPrefetchedAsync(viewSource, effectiveSong, cacheKey, cancellationToken).ConfigureAwait(false);
	}

	private async Task EnsureViewSourceMenuCaptionPrefetchedAsync(string viewSource, SongInfo song, string? cacheKey, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(viewSource) || cancellationToken.IsCancellationRequested)
		{
			return;
		}
		string resolvedTitle = null;
		try
		{
			resolvedTitle = await ResolveViewSourceCaptionAsync(viewSource, song, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			return;
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"[MenuCaption] 预热来源视图标题失败: source={viewSource}, error={ex.Message}");
		}
		if (cancellationToken.IsCancellationRequested || string.IsNullOrWhiteSpace(resolvedTitle))
		{
			return;
		}
		if (!string.IsNullOrWhiteSpace(cacheKey))
		{
			_viewSourceMenuCaptionCache[cacheKey] = resolvedTitle;
		}
	}

	private void RefreshCurrentPlayingMenuCaption(SongInfo? song)
	{
		ResetCurrentPlayingMenuCaptionToBase();
		int requestToken = Interlocked.Increment(ref _currentPlayingMenuCaptionRequestToken);
		if (song == null)
		{
			CancelCurrentPlayingMenuCaptionRequest();
			return;
		}
		string songKey = ResolveSongFocusKey(song) ?? string.Empty;
		if (!string.IsNullOrWhiteSpace(songKey) && _currentPlayingMenuCaptionCache.TryGetValue(songKey, out var cachedPayload) && !string.IsNullOrWhiteSpace(cachedPayload))
		{
			CancelCurrentPlayingMenuCaptionRequest();
			currentPlayingMenuItem.Text = BuildCurrentPlayingMenuCaption(cachedPayload);
			return;
		}
		CancellationToken cancellationToken = ReplaceCurrentPlayingMenuCaptionRequest();
		_ = EnsureCurrentPlayingMenuCaptionLoadedAsync(song, requestToken, songKey, cancellationToken);
	}

	private async Task EnsureCurrentPlayingMenuCaptionLoadedAsync(SongInfo song, int requestToken, string songKey, CancellationToken cancellationToken)
	{
		string payload = null;
		try
		{
			payload = await ResolveCurrentPlayingMenuPayloadAsync(song, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			return;
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"[MenuCaption] 拉取当前播放标题失败: {ex.Message}");
		}
		if (cancellationToken.IsCancellationRequested || string.IsNullOrWhiteSpace(payload))
		{
			return;
		}
		if (!string.IsNullOrWhiteSpace(songKey))
		{
			_currentPlayingMenuCaptionCache[songKey] = payload;
		}
		SafeInvoke(delegate
		{
			if (cancellationToken.IsCancellationRequested || currentPlayingMenuItem == null || requestToken != _currentPlayingMenuCaptionRequestToken)
			{
				return;
			}
			if (!AreSameSongForMenuCaption(song, _audioEngine?.CurrentSong))
			{
				return;
			}
			currentPlayingMenuItem.Text = BuildCurrentPlayingMenuCaption(payload);
			currentPlayingMenuItem.Owner?.PerformLayout();
		});
	}

	private async Task<string?> ResolveCurrentPlayingMenuPayloadAsync(SongInfo song, CancellationToken cancellationToken)
	{
		if (song == null || cancellationToken.IsCancellationRequested)
		{
			return null;
		}
		List<Func<CancellationToken, Task<string?>>> providers = new List<Func<CancellationToken, Task<string?>>>
		{
			(ct => Task.FromResult(TryBuildCurrentPlayingPayloadFromSong(song))),
			(ct => Task.FromResult(TryBuildCurrentPlayingPayloadFromSong(_audioEngine?.CurrentSong))),
			(ct => Task.FromResult(TryBuildCurrentPlayingPayloadFromPlaybackQueue(song))),
			(ct => Task.FromResult(TryBuildCurrentPlayingPayloadFromCurrentView(song))),
			(ct => Task.FromResult(TryBuildCurrentPlayingPayloadFromPlaybackTextSources())),
			(ct => ResolveCurrentPlayingPayloadFromApiAsync(song, ct))
		};
		string resolved = await ResolveFirstNonEmptyCaptionAsync(providers, cancellationToken).ConfigureAwait(false);
		if (!string.IsNullOrWhiteSpace(resolved))
		{
			return resolved;
		}
		return TryBuildCurrentPlayingPayloadFromSong(song);
	}

	private string TryBuildCurrentPlayingPayloadFromPlaybackQueue(SongInfo song)
	{
		if (song == null)
		{
			return string.Empty;
		}
		PlaybackSnapshot snapshot = _playbackQueue?.CaptureSnapshot();
		if (snapshot == null)
		{
			return string.Empty;
		}
		SongInfo candidate = null;
		if (snapshot.Queue != null && snapshot.QueueIndex >= 0 && snapshot.QueueIndex < snapshot.Queue.Count)
		{
			candidate = snapshot.Queue[snapshot.QueueIndex];
		}
		if (candidate == null && snapshot.Queue != null)
		{
			int queueIndex = FindSongIndexInList(snapshot.Queue, song);
			if (queueIndex >= 0 && queueIndex < snapshot.Queue.Count)
			{
				candidate = snapshot.Queue[queueIndex];
			}
		}
		if (candidate == null && snapshot.InjectionChain != null && snapshot.InjectionIndex >= 0 && snapshot.InjectionIndex < snapshot.InjectionChain.Count)
		{
			candidate = snapshot.InjectionChain[snapshot.InjectionIndex];
		}
		if (candidate == null && snapshot.InjectionChain != null)
		{
			int injectionIndex = FindSongIndexInList(snapshot.InjectionChain, song);
			if (injectionIndex >= 0 && injectionIndex < snapshot.InjectionChain.Count)
			{
				candidate = snapshot.InjectionChain[injectionIndex];
			}
		}
		if (candidate == null && snapshot.PendingInjection != null && AreSameSongForMenuCaption(song, snapshot.PendingInjection))
		{
			candidate = snapshot.PendingInjection;
		}
		return TryBuildCurrentPlayingPayloadFromSong(candidate);
	}

	private string TryBuildCurrentPlayingPayloadFromCurrentView(SongInfo song)
	{
		if (song == null || _currentSongs == null || _currentSongs.Count == 0)
		{
			return string.Empty;
		}
		int index = FindSongIndexInList(_currentSongs, song);
		if (index < 0 || index >= _currentSongs.Count)
		{
			return string.Empty;
		}
		return TryBuildCurrentPlayingPayloadFromSong(_currentSongs[index]);
	}

	private string TryBuildCurrentPlayingPayloadFromPlaybackTextSources()
	{
		if (base.IsDisposed || base.InvokeRequired)
		{
			return string.Empty;
		}
		string[] candidates = new string[4]
		{
			playPauseButton?.AccessibleDescription,
			playPauseButton?.AccessibleName,
			Text,
			base.AccessibleName
		};
		for (int i = 0; i < candidates.Length; i++)
		{
			string payload = NormalizePlaybackPayloadCandidate(candidates[i]);
			if (!string.IsNullOrWhiteSpace(payload))
			{
				return payload;
			}
		}
		return string.Empty;
	}

	private string TryBuildCurrentPlayingPayloadFromSong(SongInfo? song)
	{
		if (song == null)
		{
			return string.Empty;
		}
		return BuildCurrentPlaybackPayload(song.Name, ResolveCurrentPlaybackOwnerName(song));
	}

	private static string NormalizePlaybackPayloadCandidate(string? rawText)
	{
		if (string.IsNullOrWhiteSpace(rawText))
		{
			return string.Empty;
		}
		string text = rawText.Trim();
		if (string.Equals(text, BaseWindowTitle, StringComparison.OrdinalIgnoreCase) || string.Equals(text, "播放/暂停", StringComparison.OrdinalIgnoreCase))
		{
			return string.Empty;
		}
		string basePrefix = BaseWindowTitle + " - ";
		if (text.StartsWith(basePrefix, StringComparison.OrdinalIgnoreCase))
		{
			text = text.Substring(basePrefix.Length).Trim();
		}
		int commaIndex = text.IndexOf('，');
		if (commaIndex >= 0 && commaIndex < text.Length - 1)
		{
			text = text.Substring(commaIndex + 1).Trim();
		}
		int qualitySeparator = text.IndexOf(" | ", StringComparison.Ordinal);
		if (qualitySeparator > 0)
		{
			text = text.Substring(0, qualitySeparator).Trim();
		}
		if (text.EndsWith("]", StringComparison.Ordinal))
		{
			int albumSeparator = text.LastIndexOf(" [", StringComparison.Ordinal);
			if (albumSeparator > 0)
			{
				text = text.Substring(0, albumSeparator).Trim();
			}
		}
		if (string.Equals(text, BaseWindowTitle, StringComparison.OrdinalIgnoreCase) || string.Equals(text, "播放/暂停", StringComparison.OrdinalIgnoreCase))
		{
			return string.Empty;
		}
		return text;
	}

	private async Task<string?> ResolveCurrentPlayingPayloadFromApiAsync(SongInfo song, CancellationToken cancellationToken)
	{
		if (_apiClient == null || song == null || cancellationToken.IsCancellationRequested)
		{
			return null;
		}
		if (song.IsPodcastEpisode)
		{
			long programId = song.PodcastProgramId;
			if (programId <= 0 && long.TryParse(song.Id, out var parsedProgramId))
			{
				programId = parsedProgramId;
			}
			if (programId <= 0)
			{
				return null;
			}
			PodcastEpisodeInfo detail = await _apiClient.GetPodcastEpisodeDetailAsync(programId).ConfigureAwait(false);
			if (cancellationToken.IsCancellationRequested)
			{
				return null;
			}
			if (detail == null)
			{
				return null;
			}
			string payload = BuildCurrentPlaybackPayload(detail.Name, ResolvePodcastOwnerName(detail, song));
			return string.IsNullOrWhiteSpace(payload) ? null : payload.Trim();
		}
		string resolvedSongId = ResolveSongIdForLibraryState(song) ?? song.Id;
		if (string.IsNullOrWhiteSpace(resolvedSongId))
		{
			return null;
		}
		SongInfo detailSong = (await _apiClient.GetSongDetailAsync(new string[1] { resolvedSongId }).ConfigureAwait(false))?.FirstOrDefault();
		if (cancellationToken.IsCancellationRequested || detailSong == null)
		{
			return null;
		}
		string payload2 = TryBuildCurrentPlayingPayloadFromSong(detailSong);
		return string.IsNullOrWhiteSpace(payload2) ? null : payload2.Trim();
	}

	private static string ResolveCurrentPlaybackOwnerName(SongInfo? song)
	{
		if (song == null)
		{
			return string.Empty;
		}
		if (song.IsPodcastEpisode)
		{
			if (!string.IsNullOrWhiteSpace(song.PodcastRadioName))
			{
				return song.PodcastRadioName.Trim();
			}
			if (!string.IsNullOrWhiteSpace(song.PodcastDjName))
			{
				return song.PodcastDjName.Trim();
			}
		}
		if (!string.IsNullOrWhiteSpace(song.Artist))
		{
			return song.Artist.Trim();
		}
		if (!string.IsNullOrWhiteSpace(song.PrimaryArtistName))
		{
			return song.PrimaryArtistName.Trim();
		}
		return string.Empty;
	}

	private static string ResolvePodcastOwnerName(PodcastEpisodeInfo episode, SongInfo fallbackSong)
	{
		if (episode != null)
		{
			if (!string.IsNullOrWhiteSpace(episode.RadioName))
			{
				return episode.RadioName.Trim();
			}
			if (!string.IsNullOrWhiteSpace(episode.DjName))
			{
				return episode.DjName.Trim();
			}
		}
		return ResolveCurrentPlaybackOwnerName(fallbackSong);
	}

	private static string BuildCurrentPlaybackPayload(string title, string owner)
	{
		string safeTitle = (title ?? string.Empty).Trim();
		string safeOwner = (owner ?? string.Empty).Trim();
		if (string.IsNullOrWhiteSpace(safeTitle) && string.IsNullOrWhiteSpace(safeOwner))
		{
			return string.Empty;
		}
		if (string.IsNullOrWhiteSpace(safeTitle))
		{
			return safeOwner;
		}
		if (string.IsNullOrWhiteSpace(safeOwner))
		{
			return safeTitle;
		}
		return safeTitle + " - " + safeOwner;
	}

	private static bool AreSameSongForMenuCaption(SongInfo? first, SongInfo? second)
	{
		string firstKey = ResolveSongFocusKey(first);
		string secondKey = ResolveSongFocusKey(second);
		if (!string.IsNullOrWhiteSpace(firstKey) && !string.IsNullOrWhiteSpace(secondKey))
		{
			return string.Equals(firstKey, secondKey, StringComparison.OrdinalIgnoreCase);
		}
		if (first?.IsCloudSong == true && second?.IsCloudSong == true && !string.IsNullOrWhiteSpace(first.CloudSongId) && !string.IsNullOrWhiteSpace(second.CloudSongId))
		{
			return string.Equals(first.CloudSongId, second.CloudSongId, StringComparison.OrdinalIgnoreCase);
		}
		return false;
	}

	private void PrimeCurrentPlayingSourceMenuCaptionForFileMenu(SongInfo? song)
	{
		if (viewSourceMenuItem == null)
		{
			return;
		}
		if (song == null)
		{
			viewSourceMenuItem.Tag = null;
			viewSourceMenuItem.Enabled = false;
			ResetViewSourceMenuCaptionToBase(invalidatePendingRequest: true);
			return;
		}
		string viewSource = ResolveCurrentPlayingViewSource(song);
		viewSourceMenuItem.Tag = viewSource;
		viewSourceMenuItem.Enabled = !string.IsNullOrWhiteSpace(viewSource);
		RefreshViewSourceMenuCaption(viewSource, song);
	}

	private void RefreshViewSourceMenuCaption(string? viewSource, SongInfo? song)
	{
		ResetViewSourceMenuCaptionToBase(invalidatePendingRequest: false);
		int requestToken = Interlocked.Increment(ref _viewSourceMenuCaptionRequestToken);
		if (string.IsNullOrWhiteSpace(viewSource))
		{
			CancelViewSourceMenuCaptionRequest();
			return;
		}
		string cacheKey = NormalizeViewSourceCaptionCacheKey(viewSource);
		if (!string.IsNullOrWhiteSpace(cacheKey) && _viewSourceMenuCaptionCache.TryGetValue(cacheKey, out var cachedCaption) && !string.IsNullOrWhiteSpace(cachedCaption))
		{
			CancelViewSourceMenuCaptionRequest();
			viewSourceMenuItem.Text = BuildViewSourceMenuCaption(cachedCaption);
			return;
		}
		CancellationToken cancellationToken = ReplaceViewSourceMenuCaptionRequest();
		_ = EnsureViewSourceMenuCaptionLoadedAsync(viewSource, song, requestToken, cacheKey, cancellationToken);
	}

	private async Task EnsureViewSourceMenuCaptionLoadedAsync(string viewSource, SongInfo? song, int requestToken, string? cacheKey, CancellationToken cancellationToken)
	{
		string resolvedTitle = null;
		try
		{
			resolvedTitle = await ResolveViewSourceCaptionAsync(viewSource, song, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			return;
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"[MenuCaption] 拉取来源视图标题失败: source={viewSource}, error={ex.Message}");
		}
		if (cancellationToken.IsCancellationRequested || string.IsNullOrWhiteSpace(resolvedTitle))
		{
			return;
		}
		if (!string.IsNullOrWhiteSpace(cacheKey))
		{
			_viewSourceMenuCaptionCache[cacheKey] = resolvedTitle;
		}
		SafeInvoke(delegate
		{
			if (cancellationToken.IsCancellationRequested || viewSourceMenuItem == null || requestToken != _viewSourceMenuCaptionRequestToken)
			{
				return;
			}
			string currentTagViewSource = viewSourceMenuItem.Tag as string;
			if (!string.Equals(currentTagViewSource, viewSource, StringComparison.OrdinalIgnoreCase))
			{
				return;
			}
			viewSourceMenuItem.Text = BuildViewSourceMenuCaption(resolvedTitle);
			viewSourceMenuItem.Owner?.PerformLayout();
		});
	}

	private string TryResolveViewSourceMenuCaptionFast(string viewSource)
	{
		if (string.IsNullOrWhiteSpace(viewSource))
		{
			return string.Empty;
		}
		if (!string.IsNullOrWhiteSpace(_currentViewSource) && IsSameViewSourceForFocus(_currentViewSource, viewSource))
		{
			string currentViewTitle = resultListView?.AccessibleName;
			if (!string.IsNullOrWhiteSpace(currentViewTitle))
			{
				return currentViewTitle.Trim();
			}
		}
		foreach (NavigationHistoryItem item in _navigationHistory)
		{
			if (item != null && !string.IsNullOrWhiteSpace(item.ViewSource) && !string.IsNullOrWhiteSpace(item.ViewName) && IsSameViewSourceForFocus(item.ViewSource, viewSource))
			{
				return item.ViewName.Trim();
			}
		}
		return string.Empty;
	}

	private async Task<string?> ResolveViewSourceCaptionAsync(string viewSource, SongInfo? song, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(viewSource) || cancellationToken.IsCancellationRequested)
		{
			return null;
		}
		List<Func<CancellationToken, Task<string?>>> providers = new List<Func<CancellationToken, Task<string?>>>
		{
			(ct => Task.FromResult(TryResolveViewSourceCaptionFromContextState(viewSource, song))),
			(ct => Task.FromResult(TryResolveViewSourceCaptionFromStaticRules(viewSource, song))),
			(ct => ResolveViewSourceCaptionFromApiAsync(viewSource, song, ct))
		};
		string resolved = await ResolveFirstNonEmptyCaptionAsync(providers, cancellationToken).ConfigureAwait(false);
		if (!string.IsNullOrWhiteSpace(resolved))
		{
			return resolved;
		}
		return ResolveViewSourceCaptionFallback(viewSource, song);
	}

	private string? TryResolveViewSourceCaptionFromContextState(string viewSource, SongInfo? song)
	{
		if (string.IsNullOrWhiteSpace(viewSource))
		{
			return null;
		}
		string fastTitle = TryResolveViewSourceMenuCaptionFast(viewSource);
		if (!string.IsNullOrWhiteSpace(fastTitle))
		{
			return fastTitle.Trim();
		}
		string normalized = StripOffsetSuffix(viewSource, out _);
		if (normalized.StartsWith("playlist:", StringComparison.OrdinalIgnoreCase))
		{
			string playlistId = ExtractPrimaryIdFromViewSource(normalized, "playlist:");
			string playlistName = TryResolvePlaylistNameFromCurrentState(playlistId);
			return string.IsNullOrWhiteSpace(playlistName) ? null : playlistName;
		}
		if (normalized.StartsWith("album:", StringComparison.OrdinalIgnoreCase))
		{
			string albumId = ExtractPrimaryIdFromViewSource(normalized, "album:");
			string albumName = TryResolveAlbumNameFromCurrentState(albumId);
			return string.IsNullOrWhiteSpace(albumName) ? null : albumName;
		}
		if (normalized.StartsWith("podcast:", StringComparison.OrdinalIgnoreCase))
		{
			ParsePodcastViewSource(normalized, out var radioId, out var _, out var _);
			string podcastName = TryResolvePodcastNameFromCurrentState(radioId, song);
			return string.IsNullOrWhiteSpace(podcastName) ? null : podcastName;
		}
		if (normalized.StartsWith("artist_entries:", StringComparison.OrdinalIgnoreCase))
		{
			long artistId = ParseArtistIdFromViewSource(normalized, "artist_entries:");
			string artistName = ResolveArtistNameForViewSourceFromState(artistId, song?.Artist);
			return string.IsNullOrWhiteSpace(artistName) ? null : artistName;
		}
		if (normalized.StartsWith("artist_songs_top:", StringComparison.OrdinalIgnoreCase) || normalized.StartsWith("artist_top:", StringComparison.OrdinalIgnoreCase))
		{
			string prefix = normalized.StartsWith("artist_top:", StringComparison.OrdinalIgnoreCase) ? "artist_top:" : "artist_songs_top:";
			long artistId2 = ParseArtistIdFromViewSource(normalized, prefix);
			string artistName2 = ResolveArtistNameForViewSourceFromState(artistId2, song?.Artist);
			return string.IsNullOrWhiteSpace(artistName2) ? null : (artistName2 + " 热门 50 首");
		}
		if (normalized.StartsWith("artist_songs:", StringComparison.OrdinalIgnoreCase))
		{
			ParseArtistListViewSource(normalized, out var artistId3, out var _, out var _);
			string artistName3 = ResolveArtistNameForViewSourceFromState(artistId3, song?.Artist);
			return string.IsNullOrWhiteSpace(artistName3) ? null : (artistName3 + " 的歌曲");
		}
		if (normalized.StartsWith("artist_albums:", StringComparison.OrdinalIgnoreCase))
		{
			ParseArtistListViewSource(normalized, out var artistId4, out var _, out var _, "latest");
			string artistName4 = ResolveArtistNameForViewSourceFromState(artistId4, song?.Artist);
			return string.IsNullOrWhiteSpace(artistName4) ? null : (artistName4 + " 的专辑");
		}
		if (normalized.StartsWith("artist_top_", StringComparison.OrdinalIgnoreCase))
		{
			long artistId5 = ParseArtistIdFromSuffix(normalized, "artist_top_");
			string artistName5 = ResolveArtistNameForViewSourceFromState(artistId5, song?.Artist);
			return string.IsNullOrWhiteSpace(artistName5) ? null : (artistName5 + " 热门 50 首");
		}
		if (normalized.StartsWith("artist_songs_", StringComparison.OrdinalIgnoreCase))
		{
			long artistId6 = ParseArtistIdFromSuffix(normalized, "artist_songs_");
			string artistName6 = ResolveArtistNameForViewSourceFromState(artistId6, song?.Artist);
			return string.IsNullOrWhiteSpace(artistName6) ? null : (artistName6 + " 的歌曲");
		}
		if (normalized.StartsWith("artist_albums_", StringComparison.OrdinalIgnoreCase))
		{
			long artistId7 = ParseArtistIdFromSuffix(normalized, "artist_albums_");
			string artistName7 = ResolveArtistNameForViewSourceFromState(artistId7, song?.Artist);
			return string.IsNullOrWhiteSpace(artistName7) ? null : (artistName7 + " 的专辑");
		}
		if (normalized.StartsWith("url:song:", StringComparison.OrdinalIgnoreCase))
		{
			string songIdFromUrl = ExtractPrimaryIdFromViewSource(normalized, "url:song:");
			string payload = TryResolveViewSourceSongPayloadFromState(songIdFromUrl, song);
			return string.IsNullOrWhiteSpace(payload) ? null : payload;
		}
		return null;
	}

	private string TryResolvePlaylistNameFromCurrentState(string playlistId)
	{
		if (string.IsNullOrWhiteSpace(playlistId))
		{
			return string.Empty;
		}
		if (_currentPlaylist != null && string.Equals(_currentPlaylist.Id, playlistId, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(_currentPlaylist.Name))
		{
			return _currentPlaylist.Name.Trim();
		}
		PlaylistInfo matched = _currentPlaylists?.FirstOrDefault(p => p != null && string.Equals(p.Id, playlistId, StringComparison.OrdinalIgnoreCase));
		if (!string.IsNullOrWhiteSpace(matched?.Name))
		{
			return matched.Name.Trim();
		}
		return string.Empty;
	}

	private string TryResolveAlbumNameFromCurrentState(string albumId)
	{
		if (string.IsNullOrWhiteSpace(albumId))
		{
			return string.Empty;
		}
		AlbumInfo matched = _currentAlbums?.FirstOrDefault(a => a != null && string.Equals(a.Id, albumId, StringComparison.OrdinalIgnoreCase));
		if (!string.IsNullOrWhiteSpace(matched?.Name))
		{
			return matched.Name.Trim();
		}
		return string.Empty;
	}

	private string TryResolvePodcastNameFromCurrentState(long radioId, SongInfo? song)
	{
		if (radioId <= 0)
		{
			return string.Empty;
		}
		if (_currentPodcast != null && _currentPodcast.Id == radioId && !string.IsNullOrWhiteSpace(_currentPodcast.Name))
		{
			return _currentPodcast.Name.Trim();
		}
		PodcastRadioInfo matched = _currentPodcasts?.FirstOrDefault(p => p != null && p.Id == radioId);
		if (!string.IsNullOrWhiteSpace(matched?.Name))
		{
			return matched.Name.Trim();
		}
		if (song != null && song.IsPodcastEpisode && song.PodcastRadioId == radioId)
		{
			if (!string.IsNullOrWhiteSpace(song.PodcastRadioName))
			{
				return song.PodcastRadioName.Trim();
			}
			if (!string.IsNullOrWhiteSpace(song.PodcastDjName))
			{
				return song.PodcastDjName.Trim();
			}
		}
		return string.Empty;
	}

	private string ResolveArtistNameForViewSourceFromState(long artistId, string? fallbackName)
	{
		if (artistId > 0)
		{
			if (_currentArtist != null && _currentArtist.Id == artistId && !string.IsNullOrWhiteSpace(_currentArtist.Name))
			{
				return _currentArtist.Name.Trim();
			}
			if (_currentArtistDetail != null && _currentArtistDetail.Id == artistId && !string.IsNullOrWhiteSpace(_currentArtistDetail.Name))
			{
				return _currentArtistDetail.Name.Trim();
			}
			ArtistInfo matched = _currentArtists?.FirstOrDefault(a => a != null && a.Id == artistId && !string.IsNullOrWhiteSpace(a.Name));
			if (!string.IsNullOrWhiteSpace(matched?.Name))
			{
				return matched.Name.Trim();
			}
		}
		return (fallbackName ?? string.Empty).Trim();
	}

	private string TryResolveViewSourceSongPayloadFromState(string songId, SongInfo? contextSong)
	{
		if (string.IsNullOrWhiteSpace(songId))
		{
			return string.Empty;
		}
		if (contextSong != null)
		{
			string contextSongId = ResolveSongIdForLibraryState(contextSong) ?? contextSong.Id;
			if (!string.IsNullOrWhiteSpace(contextSongId) && string.Equals(contextSongId, songId, StringComparison.OrdinalIgnoreCase))
			{
				return TryBuildCurrentPlayingPayloadFromSong(contextSong);
			}
		}
		SongInfo engineSong = _audioEngine?.CurrentSong;
		if (engineSong != null)
		{
			string engineSongId = ResolveSongIdForLibraryState(engineSong) ?? engineSong.Id;
			if (!string.IsNullOrWhiteSpace(engineSongId) && string.Equals(engineSongId, songId, StringComparison.OrdinalIgnoreCase))
			{
				return TryBuildCurrentPlayingPayloadFromSong(engineSong);
			}
		}
		if (_currentSongs != null)
		{
			for (int i = 0; i < _currentSongs.Count; i++)
			{
				SongInfo item = _currentSongs[i];
				if (item != null)
				{
					string itemId = ResolveSongIdForLibraryState(item) ?? item.Id;
					if (!string.IsNullOrWhiteSpace(itemId) && string.Equals(itemId, songId, StringComparison.OrdinalIgnoreCase))
					{
						return TryBuildCurrentPlayingPayloadFromSong(item);
					}
				}
			}
		}
		PlaybackSnapshot snapshot = _playbackQueue?.CaptureSnapshot();
		if (snapshot?.Queue != null)
		{
			for (int j = 0; j < snapshot.Queue.Count; j++)
			{
				SongInfo queueSong = snapshot.Queue[j];
				if (queueSong != null)
				{
					string queueSongId = ResolveSongIdForLibraryState(queueSong) ?? queueSong.Id;
					if (!string.IsNullOrWhiteSpace(queueSongId) && string.Equals(queueSongId, songId, StringComparison.OrdinalIgnoreCase))
					{
						return TryBuildCurrentPlayingPayloadFromSong(queueSong);
					}
				}
			}
		}
		if (snapshot?.InjectionChain != null)
		{
			for (int k = 0; k < snapshot.InjectionChain.Count; k++)
			{
				SongInfo injectionSong = snapshot.InjectionChain[k];
				if (injectionSong != null)
				{
					string injectionSongId = ResolveSongIdForLibraryState(injectionSong) ?? injectionSong.Id;
					if (!string.IsNullOrWhiteSpace(injectionSongId) && string.Equals(injectionSongId, songId, StringComparison.OrdinalIgnoreCase))
					{
						return TryBuildCurrentPlayingPayloadFromSong(injectionSong);
					}
				}
			}
		}
		return string.Empty;
	}

	private string? TryResolveViewSourceCaptionFromStaticRules(string viewSource, SongInfo? song)
	{
		if (string.IsNullOrWhiteSpace(viewSource))
		{
			return null;
		}
		string normalized = StripOffsetSuffix(viewSource, out _);
		if (normalized.StartsWith("search:", StringComparison.OrdinalIgnoreCase))
		{
			ParseSearchViewSource(normalized, out var searchType, out var keyword, out var _);
			if (string.IsNullOrWhiteSpace(keyword))
			{
				return "搜索";
			}
			if (string.Equals(searchType, "歌曲", StringComparison.OrdinalIgnoreCase))
			{
				return "搜索: " + keyword;
			}
			return "搜索" + searchType + ": " + keyword;
		}
		if (normalized.StartsWith("playlist_cat_", StringComparison.OrdinalIgnoreCase))
		{
			ParsePlaylistCategoryViewSource(normalized, out var catName, out var _);
			return string.IsNullOrWhiteSpace(catName) ? "歌单分类" : (catName + "歌单");
		}
		if (normalized.StartsWith("podcast_cat_", StringComparison.OrdinalIgnoreCase))
		{
			ParsePodcastCategoryViewSource(normalized, out var categoryId, out var _);
			string categoryName = ResolvePodcastCategoryName(categoryId);
			return string.IsNullOrWhiteSpace(categoryName) ? "播客列表" : (categoryName + " 播客");
		}
		if (normalized.StartsWith("artist_type_", StringComparison.OrdinalIgnoreCase))
		{
			return "歌手地区筛选";
		}
		if (normalized.StartsWith("artist_area_", StringComparison.OrdinalIgnoreCase))
		{
			return "歌手分类列表";
		}
		if (normalized.StartsWith("new_album_period_", StringComparison.OrdinalIgnoreCase))
		{
			return "新碟地区筛选";
		}
		if (normalized.StartsWith("new_album_area_", StringComparison.OrdinalIgnoreCase))
		{
			return "新碟分类列表";
		}
		if (normalized.StartsWith("new_album_category_period:", StringComparison.OrdinalIgnoreCase))
		{
			return "新碟地区筛选";
		}
		if (normalized.StartsWith("new_album_category_list:", StringComparison.OrdinalIgnoreCase))
		{
			return "新碟分类列表";
		}
		if (normalized.StartsWith("url:mixed:", StringComparison.OrdinalIgnoreCase))
		{
			return "聚合链接";
		}
		if (TryParseNewSongsViewSource(normalized, out var _, out var areaName, out var _, out var _))
		{
			return string.IsNullOrWhiteSpace(areaName) ? "新歌速递" : (areaName + "新歌速递");
		}
		string staticTitle = ResolveStaticViewSourceCaption(normalized);
		return string.IsNullOrWhiteSpace(staticTitle) ? null : staticTitle.Trim();
	}

	private string? ResolveViewSourceCaptionFallback(string viewSource, SongInfo? song)
	{
		if (string.IsNullOrWhiteSpace(viewSource))
		{
			return null;
		}
		string normalized = StripOffsetSuffix(viewSource, out _);
		if (normalized.StartsWith("playlist:", StringComparison.OrdinalIgnoreCase))
		{
			string playlistId = ExtractPrimaryIdFromViewSource(normalized, "playlist:");
			return string.IsNullOrWhiteSpace(playlistId) ? "歌单" : ("歌单 " + playlistId);
		}
		if (normalized.StartsWith("album:", StringComparison.OrdinalIgnoreCase))
		{
			string albumId = ExtractPrimaryIdFromViewSource(normalized, "album:");
			return string.IsNullOrWhiteSpace(albumId) ? "专辑" : ("专辑 " + albumId);
		}
		if (normalized.StartsWith("podcast:", StringComparison.OrdinalIgnoreCase))
		{
			ParsePodcastViewSource(normalized, out var radioId, out var _, out var _);
			return (radioId > 0) ? $"播客 {radioId}" : "播客";
		}
		if (normalized.StartsWith("artist_entries:", StringComparison.OrdinalIgnoreCase))
		{
			long artistId = ParseArtistIdFromViewSource(normalized, "artist_entries:");
			string artistName = ResolveArtistNameForViewSourceFromState(artistId, song?.Artist);
			return string.IsNullOrWhiteSpace(artistName) ? "歌手主页" : artistName;
		}
		if (normalized.StartsWith("artist_songs_top:", StringComparison.OrdinalIgnoreCase) || normalized.StartsWith("artist_top:", StringComparison.OrdinalIgnoreCase))
		{
			string prefix = normalized.StartsWith("artist_top:", StringComparison.OrdinalIgnoreCase) ? "artist_top:" : "artist_songs_top:";
			long artistId2 = ParseArtistIdFromViewSource(normalized, prefix);
			string artistName2 = ResolveArtistNameForViewSourceFromState(artistId2, song?.Artist);
			return string.IsNullOrWhiteSpace(artistName2) ? "热门 50 首" : (artistName2 + " 热门 50 首");
		}
		if (normalized.StartsWith("artist_songs:", StringComparison.OrdinalIgnoreCase))
		{
			ParseArtistListViewSource(normalized, out var artistId3, out var _, out var _);
			string artistName3 = ResolveArtistNameForViewSourceFromState(artistId3, song?.Artist);
			return string.IsNullOrWhiteSpace(artistName3) ? "歌手歌曲" : (artistName3 + " 的歌曲");
		}
		if (normalized.StartsWith("artist_albums:", StringComparison.OrdinalIgnoreCase))
		{
			ParseArtistListViewSource(normalized, out var artistId4, out var _, out var _, "latest");
			string artistName4 = ResolveArtistNameForViewSourceFromState(artistId4, song?.Artist);
			return string.IsNullOrWhiteSpace(artistName4) ? "歌手专辑" : (artistName4 + " 的专辑");
		}
		if (normalized.StartsWith("artist_top_", StringComparison.OrdinalIgnoreCase))
		{
			long artistId5 = ParseArtistIdFromSuffix(normalized, "artist_top_");
			string artistName5 = ResolveArtistNameForViewSourceFromState(artistId5, song?.Artist);
			return string.IsNullOrWhiteSpace(artistName5) ? "热门 50 首" : (artistName5 + " 热门 50 首");
		}
		if (normalized.StartsWith("artist_songs_", StringComparison.OrdinalIgnoreCase))
		{
			long artistId6 = ParseArtistIdFromSuffix(normalized, "artist_songs_");
			string artistName6 = ResolveArtistNameForViewSourceFromState(artistId6, song?.Artist);
			return string.IsNullOrWhiteSpace(artistName6) ? "歌手歌曲" : (artistName6 + " 的歌曲");
		}
		if (normalized.StartsWith("artist_albums_", StringComparison.OrdinalIgnoreCase))
		{
			long artistId7 = ParseArtistIdFromSuffix(normalized, "artist_albums_");
			string artistName7 = ResolveArtistNameForViewSourceFromState(artistId7, song?.Artist);
			return string.IsNullOrWhiteSpace(artistName7) ? "歌手专辑" : (artistName7 + " 的专辑");
		}
		if (normalized.StartsWith("url:song:", StringComparison.OrdinalIgnoreCase))
		{
			string songIdFromUrl = ExtractPrimaryIdFromViewSource(normalized, "url:song:");
			string payload = TryResolveViewSourceSongPayloadFromState(songIdFromUrl, song);
			return string.IsNullOrWhiteSpace(payload) ? "歌曲" : payload;
		}
		return null;
	}

	private async Task<string?> ResolveViewSourceCaptionFromApiAsync(string viewSource, SongInfo? song, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(viewSource) || _apiClient == null || cancellationToken.IsCancellationRequested)
		{
			return null;
		}
		string normalized = StripOffsetSuffix(viewSource, out _);
		if (normalized.StartsWith("playlist:", StringComparison.OrdinalIgnoreCase))
		{
			string playlistId = ExtractPrimaryIdFromViewSource(normalized, "playlist:");
			if (string.IsNullOrWhiteSpace(playlistId))
			{
				return null;
			}
			PlaylistInfo detail = await _apiClient.GetPlaylistDetailAsync(playlistId).ConfigureAwait(false);
			if (cancellationToken.IsCancellationRequested || string.IsNullOrWhiteSpace(detail?.Name))
			{
				return null;
			}
			return detail.Name.Trim();
		}
		if (normalized.StartsWith("album:", StringComparison.OrdinalIgnoreCase))
		{
			string albumId = ExtractPrimaryIdFromViewSource(normalized, "album:");
			if (string.IsNullOrWhiteSpace(albumId))
			{
				return null;
			}
			AlbumInfo detail2 = await _apiClient.GetAlbumDetailAsync(albumId).ConfigureAwait(false);
			if (cancellationToken.IsCancellationRequested || string.IsNullOrWhiteSpace(detail2?.Name))
			{
				return null;
			}
			return detail2.Name.Trim();
		}
		if (normalized.StartsWith("podcast:", StringComparison.OrdinalIgnoreCase))
		{
			ParsePodcastViewSource(normalized, out var radioId, out var _, out var _);
			if (radioId <= 0)
			{
				return null;
			}
			PodcastRadioInfo detail3 = await _apiClient.GetPodcastRadioDetailAsync(radioId).ConfigureAwait(false);
			if (cancellationToken.IsCancellationRequested || string.IsNullOrWhiteSpace(detail3?.Name))
			{
				return null;
			}
			return detail3.Name.Trim();
		}
		if (normalized.StartsWith("artist_entries:", StringComparison.OrdinalIgnoreCase))
		{
			long artistId = ParseArtistIdFromViewSource(normalized, "artist_entries:");
			string artistName = await ResolveArtistNameForViewSourceAsync(artistId, song?.Artist).ConfigureAwait(false);
			if (cancellationToken.IsCancellationRequested || string.IsNullOrWhiteSpace(artistName))
			{
				return null;
			}
			return artistName.Trim();
		}
		if (normalized.StartsWith("artist_songs_top:", StringComparison.OrdinalIgnoreCase) || normalized.StartsWith("artist_top:", StringComparison.OrdinalIgnoreCase))
		{
			string prefix = normalized.StartsWith("artist_top:", StringComparison.OrdinalIgnoreCase) ? "artist_top:" : "artist_songs_top:";
			long artistId2 = ParseArtistIdFromViewSource(normalized, prefix);
			string artistName2 = await ResolveArtistNameForViewSourceAsync(artistId2, song?.Artist).ConfigureAwait(false);
			if (cancellationToken.IsCancellationRequested || string.IsNullOrWhiteSpace(artistName2))
			{
				return null;
			}
			return artistName2.Trim() + " 热门 50 首";
		}
		if (normalized.StartsWith("artist_songs:", StringComparison.OrdinalIgnoreCase))
		{
			ParseArtistListViewSource(normalized, out var artistId3, out var _, out var _);
			string artistName3 = await ResolveArtistNameForViewSourceAsync(artistId3, song?.Artist).ConfigureAwait(false);
			if (cancellationToken.IsCancellationRequested || string.IsNullOrWhiteSpace(artistName3))
			{
				return null;
			}
			return artistName3.Trim() + " 的歌曲";
		}
		if (normalized.StartsWith("artist_albums:", StringComparison.OrdinalIgnoreCase))
		{
			ParseArtistListViewSource(normalized, out var artistId4, out var _, out var _, "latest");
			string artistName4 = await ResolveArtistNameForViewSourceAsync(artistId4, song?.Artist).ConfigureAwait(false);
			if (cancellationToken.IsCancellationRequested || string.IsNullOrWhiteSpace(artistName4))
			{
				return null;
			}
			return artistName4.Trim() + " 的专辑";
		}
		if (normalized.StartsWith("artist_top_", StringComparison.OrdinalIgnoreCase))
		{
			long artistId5 = ParseArtistIdFromSuffix(normalized, "artist_top_");
			string artistName5 = await ResolveArtistNameForViewSourceAsync(artistId5, song?.Artist).ConfigureAwait(false);
			if (cancellationToken.IsCancellationRequested || string.IsNullOrWhiteSpace(artistName5))
			{
				return null;
			}
			return artistName5.Trim() + " 热门 50 首";
		}
		if (normalized.StartsWith("artist_songs_", StringComparison.OrdinalIgnoreCase))
		{
			long artistId6 = ParseArtistIdFromSuffix(normalized, "artist_songs_");
			string artistName6 = await ResolveArtistNameForViewSourceAsync(artistId6, song?.Artist).ConfigureAwait(false);
			if (cancellationToken.IsCancellationRequested || string.IsNullOrWhiteSpace(artistName6))
			{
				return null;
			}
			return artistName6.Trim() + " 的歌曲";
		}
		if (normalized.StartsWith("artist_albums_", StringComparison.OrdinalIgnoreCase))
		{
			long artistId7 = ParseArtistIdFromSuffix(normalized, "artist_albums_");
			string artistName7 = await ResolveArtistNameForViewSourceAsync(artistId7, song?.Artist).ConfigureAwait(false);
			if (cancellationToken.IsCancellationRequested || string.IsNullOrWhiteSpace(artistName7))
			{
				return null;
			}
			return artistName7.Trim() + " 的专辑";
		}
		if (normalized.StartsWith("url:song:", StringComparison.OrdinalIgnoreCase))
		{
			string songIdFromUrl = ExtractPrimaryIdFromViewSource(normalized, "url:song:");
			if (string.IsNullOrWhiteSpace(songIdFromUrl))
			{
				return null;
			}
			SongInfo detailSong = (await _apiClient.GetSongDetailAsync(new string[1] { songIdFromUrl }).ConfigureAwait(false))?.FirstOrDefault();
			if (cancellationToken.IsCancellationRequested || detailSong == null)
			{
				return null;
			}
			string payload = TryBuildCurrentPlayingPayloadFromSong(detailSong);
			return string.IsNullOrWhiteSpace(payload) ? null : payload.Trim();
		}
		return null;
	}

	private async Task<string> ResolveArtistNameForViewSourceAsync(long artistId, string? fallbackName)
	{
		if (artistId <= 0)
		{
			return (fallbackName ?? string.Empty).Trim();
		}
		string localName = ResolveArtistNameForViewSourceFromState(artistId, fallbackName);
		if (!string.IsNullOrWhiteSpace(localName))
		{
			return localName;
		}
		if (_apiClient != null)
		{
			ArtistDetail detail = await _apiClient.GetArtistDetailAsync(artistId, includeIntroduction: false).ConfigureAwait(false);
			if (!string.IsNullOrWhiteSpace(detail?.Name))
			{
				return detail.Name.Trim();
			}
		}
		return (fallbackName ?? string.Empty).Trim();
	}

	private static string NormalizeViewSourceCaptionCacheKey(string viewSource)
	{
		if (string.IsNullOrWhiteSpace(viewSource))
		{
			return string.Empty;
		}
		string normalized = StripOffsetSuffix(viewSource, out _);
		return string.IsNullOrWhiteSpace(normalized) ? viewSource.Trim() : normalized.Trim();
	}

	private static string ExtractPrimaryIdFromViewSource(string viewSource, string prefix)
	{
		if (string.IsNullOrWhiteSpace(viewSource) || string.IsNullOrWhiteSpace(prefix) || !viewSource.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
		{
			return string.Empty;
		}
		string suffix = viewSource.Substring(prefix.Length);
		int separatorIndex = suffix.IndexOf(':');
		if (separatorIndex >= 0)
		{
			suffix = suffix.Substring(0, separatorIndex);
		}
		return (suffix ?? string.Empty).Trim();
	}

	private static long ParseArtistIdFromSuffix(string source, string prefix)
	{
		if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(prefix) || !source.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
		{
			return 0L;
		}
		string suffix = source.Substring(prefix.Length);
		int separatorIndex = suffix.IndexOf(':');
		if (separatorIndex >= 0)
		{
			suffix = suffix.Substring(0, separatorIndex);
		}
		return long.TryParse(suffix, out var parsed) ? parsed : 0L;
	}

	private static string ResolveStaticViewSourceCaption(string normalizedViewSource)
	{
		if (string.IsNullOrWhiteSpace(normalizedViewSource))
		{
			return string.Empty;
		}
		if (normalizedViewSource.StartsWith("listen-match", StringComparison.OrdinalIgnoreCase))
		{
			return "听歌识曲";
		}
		return normalizedViewSource switch
		{
			"homepage" => "首页",
			"user_liked_songs" => "喜欢的音乐",
			"user_playlists" => "创建和收藏的歌单",
			"user_albums" => "收藏的专辑",
			"user_podcasts" => "收藏的播客",
			"user_cloud" => "云盘歌曲",
			"recent_play" => "最近播放",
			"recent_listened" => "最近听过",
			"recent_playlists" => "最近歌单",
			"recent_albums" => "最近专辑",
			"recent_podcasts" => "最近播客",
			"daily_recommend" => "每日推荐",
			"daily_recommend_songs" => "每日推荐歌曲",
			"daily_recommend_playlists" => "每日推荐歌单",
			"personalized" => "个性推荐",
			"personalized_playlists" => "推荐歌单",
			"personalized_newsongs" => "推荐新歌",
			"personalized_newsong" => "推荐新歌",
			"toplist" => "官方排行榜",
			"highquality_playlists" => "精品歌单",
			"playlist_category" => "歌单分类",
			"podcast_categories" => "播客分类",
			"new_songs" => "新歌速递",
			"new_songs_all" => "全部新歌速递",
			"new_songs_chinese" => "华语新歌速递",
			"new_songs_western" => "欧美新歌速递",
			"new_songs_japan" => "日本新歌速递",
			"new_songs_korea" => "韩国新歌速递",
			"new_albums" => "新碟上架",
			"artist_favorites" => "收藏的歌手",
			"artist_categories" => "歌手类型分类",
			"artist_category_types" => "歌手类型分类",
			"new_album_categories" => "新碟时间筛选",
			"new_album_category_periods" => "新碟时间筛选",
			PersonalFmCategoryId => PersonalFmAccessibleName,
			_ => string.Empty,
		};
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
		ListViewItem selectedListViewItemSafe = GetSelectedListViewItemSafe();
		if (selectedListViewItemSafe == null)
		{
			return null;
		}
		if (selectedListViewItemSafe.Tag is int num && num >= 0 && num < _currentSongs.Count)
		{
			return _currentSongs[num];
		}
		if (selectedListViewItemSafe.Tag is SongInfo result)
		{
			return result;
		}
		if (selectedListViewItemSafe.Tag is ListItemInfo listItemInfo)
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
		if (selectedListViewItemSafe.Tag is PodcastEpisodeInfo podcastEpisodeInfo)
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
		ListViewItem selectedListViewItemSafe = GetSelectedListViewItemSafe();
		if (selectedListViewItemSafe == null)
		{
			return null;
		}
		if (selectedListViewItemSafe.Tag is PlaylistInfo result)
		{
			return result;
		}
		if (selectedListViewItemSafe.Tag is ListItemInfo { Type: ListItemType.Playlist } listItemInfo)
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
		ListViewItem selectedListViewItemSafe = GetSelectedListViewItemSafe();
		if (selectedListViewItemSafe == null)
		{
			return null;
		}
		if (selectedListViewItemSafe.Tag is AlbumInfo result)
		{
			return result;
		}
		if (selectedListViewItemSafe.Tag is ListItemInfo { Type: ListItemType.Album } listItemInfo)
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
			SongInfo currentPlayingMenuSong = _currentPlayingMenuSong;
			if (currentPlayingMenuSong != null && currentPlayingMenuSong.IsPodcastEpisode)
			{
				PodcastRadioInfo podcastRadioInfo = ResolvePodcastFromSong(_currentPlayingMenuSong);
				if (podcastRadioInfo != null)
				{
					return podcastRadioInfo;
				}
			}
		}
		ListViewItem selectedListViewItemSafe = GetSelectedListViewItemSafe();
		if (selectedListViewItemSafe != null)
		{
			if (selectedListViewItemSafe.Tag is PodcastRadioInfo result)
			{
				return result;
			}
			if (selectedListViewItemSafe.Tag is PodcastEpisodeInfo episode)
			{
				PodcastRadioInfo podcastRadioInfo2 = ResolvePodcastFromEpisode(episode);
				if (podcastRadioInfo2 != null)
				{
					return podcastRadioInfo2;
				}
			}
			if (selectedListViewItemSafe.Tag is ListItemInfo listItemInfo)
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
			if (selectedListViewItemSafe.Tag is SongInfo { IsPodcastEpisode: not false } songInfo)
			{
				PodcastRadioInfo podcastRadioInfo4 = ResolvePodcastFromSong(songInfo);
				if (podcastRadioInfo4 != null)
				{
					return podcastRadioInfo4;
				}
			}
			if (selectedListViewItemSafe.Tag is int num && num >= 0 && num < _currentSongs.Count)
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
			SongInfo currentPlayingMenuSong = _currentPlayingMenuSong;
			if (currentPlayingMenuSong != null && currentPlayingMenuSong.IsPodcastEpisode)
			{
				return ResolvePodcastEpisodeFromSong(_currentPlayingMenuSong);
			}
		}
		ListViewItem selectedListViewItemSafe = GetSelectedListViewItemSafe();
		if (selectedListViewItemSafe == null)
		{
			return null;
		}
		if (selectedListViewItemSafe.Tag is PodcastEpisodeInfo result)
		{
			return result;
		}
		if (selectedListViewItemSafe.Tag is ListItemInfo listItemInfo)
		{
			if (listItemInfo.Type == ListItemType.PodcastEpisode && listItemInfo.PodcastEpisode != null)
			{
				return listItemInfo.PodcastEpisode;
			}
			if (listItemInfo.Type == ListItemType.Song)
			{
				SongInfo song = listItemInfo.Song;
				if (song != null && song.IsPodcastEpisode)
				{
					return ResolvePodcastEpisodeFromSong(listItemInfo.Song);
				}
			}
		}
		if (selectedListViewItemSafe.Tag is SongInfo { IsPodcastEpisode: not false } songInfo)
		{
			return ResolvePodcastEpisodeFromSong(songInfo);
		}
		if (selectedListViewItemSafe.Tag is int num && num >= 0 && num < _currentSongs.Count)
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
				sharePodcastCopyWebMenuItem.Tag = (allowShare ? podcast : null);
				sharePodcastOpenWebMenuItem.Tag = (allowShare ? podcast : null);
			}
			else
			{
				sharePodcastMenuItem.Visible = false;
				sharePodcastMenuItem.Tag = null;
				sharePodcastCopyWebMenuItem.Tag = null;
				sharePodcastOpenWebMenuItem.Tag = null;
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
			sharePodcastEpisodeOpenWebMenuItem.Tag = null;
		}
		else
		{
			sharePodcastEpisodeMenuItem.Visible = true;
			sharePodcastEpisodeMenuItem.Tag = episode;
			sharePodcastEpisodeWebMenuItem.Tag = episode;
			sharePodcastEpisodeDirectMenuItem.Tag = episode;
			sharePodcastEpisodeOpenWebMenuItem.Tag = episode;
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
		if (!IsPodcastEpisodeView())
		{
			return null;
		}
		ListViewItem selectedListViewItemSafe = GetSelectedListViewItemSafe();
		if (selectedListViewItemSafe == null)
		{
			return null;
		}
		if (selectedListViewItemSafe.Tag is int num && num < 0)
		{
			return null;
		}
		int index = selectedListViewItemSafe.Index;
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
			SetMenuItemCheckedState(podcastSortLatestMenuItem, !_podcastSortState.CurrentOption);
			SetMenuItemCheckedState(podcastSortSerialMenuItem, _podcastSortState.CurrentOption);
			if (podcastSortMenuItem != null)
			{
				string text = (_podcastSortState.CurrentOption ? "节目顺序" : "按最新");
				podcastSortMenuItem.Text = "排序（" + text + "）";
			}
		}
	}

	private void EnsureSortMenuCheckMargins()
	{
		EnsureSortMenuCheckMargin(artistSongsSortMenuItem);
		EnsureSortMenuCheckMargin(artistAlbumsSortMenuItem);
		EnsureSortMenuCheckMargin(podcastSortMenuItem);
		EnsureSortMenuCheckMargin(playbackMenuItem);
		EnsureSortMenuCheckMargin(qualityMenuItem);
		EnsureSortMenuCheckMargin(playControlMenuItem);
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
			SetMenuItemCheckedState(artistSongsSortHotMenuItem, _artistSongSortState.EqualsOption(ArtistSongSortOption.Hot));
			SetMenuItemCheckedState(artistSongsSortTimeMenuItem, _artistSongSortState.EqualsOption(ArtistSongSortOption.Time));
			if (artistSongsSortMenuItem != null)
			{
				string text = (_artistSongSortState.EqualsOption(ArtistSongSortOption.Hot) ? "按热度" : "按发布时间");
				artistSongsSortMenuItem.Text = "排序（" + text + "）";
			}
		}
	}

	private void UpdateArtistAlbumsSortMenuChecks()
	{
		if (artistAlbumsSortLatestMenuItem != null && artistAlbumsSortOldestMenuItem != null)
		{
			SetMenuItemCheckedState(artistAlbumsSortLatestMenuItem, _artistAlbumSortState.EqualsOption(ArtistAlbumSortOption.Latest));
			SetMenuItemCheckedState(artistAlbumsSortOldestMenuItem, _artistAlbumSortState.EqualsOption(ArtistAlbumSortOption.Oldest));
			if (artistAlbumsSortMenuItem != null)
			{
				string text = (_artistAlbumSortState.EqualsOption(ArtistAlbumSortOption.Latest) ? "按最新" : "按最早");
				artistAlbumsSortMenuItem.Text = "排序（" + text + "）";
			}
		}
	}

	private static void SetMenuItemCheckedState(ToolStripMenuItem? menuItem, bool isChecked)
	{
		if (menuItem != null)
		{
			menuItem.Checked = isChecked;
			menuItem.CheckState = (isChecked ? CheckState.Checked : CheckState.Unchecked);
		}
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
			IsCloudView = (flag && text.StartsWith("user_cloud", StringComparison.OrdinalIgnoreCase)),
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
		ListViewItem selectedListViewItemSafe = GetSelectedListViewItemSafe();
		if (selectedListViewItemSafe == null)
		{
			menuContextSnapshot.IsValid = false;
			return menuContextSnapshot;
		}
		menuContextSnapshot.SelectedListItem = selectedListViewItemSafe;
		if (menuContextSnapshot.IsPodcastEpisodeView && selectedListViewItemSafe.Tag is int num && num < 0)
		{
			menuContextSnapshot.IsValid = false;
			return menuContextSnapshot;
		}
		object tag = selectedListViewItemSafe.Tag;
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
		subscribeSongAlbumMenuItem.Visible = false;
		subscribeSongAlbumMenuItem.Tag = null;
		subscribeSongArtistMenuItem.Visible = false;
		subscribeSongArtistMenuItem.Tag = null;
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
		downloadCategoryMenuItem.Text = "下载分类(&C)...";
		batchDownloadPlaylistsMenuItem.Visible = false;
		downloadPodcastMenuItem.Visible = false;
		downloadPodcastMenuItem.Tag = null;
		downloadLyricsMenuItem.Visible = false;
		downloadLyricsMenuItem.Tag = null;
		if (lyricsLanguageMenuItem != null)
		{
			lyricsLanguageMenuItem.Visible = false;
			lyricsLanguageMenuItem.Tag = null;
			lyricsLanguageMenuItem.DropDownItems.Clear();
			lyricsLanguageMenuItem.Text = "歌词翻译";
		}
		cloudMenuSeparator.Visible = false;
		uploadToCloudMenuItem.Visible = false;
		deleteFromCloudMenuItem.Visible = false;
		toolStripSeparatorArtist.Visible = false;
		shareArtistMenuItem.Visible = false;
		shareArtistMenuItem.Tag = null;
		subscribeArtistMenuItem.Visible = false;
		unsubscribeArtistMenuItem.Visible = false;
		toolStripSeparatorView.Visible = false;
		commentMenuItem.Visible = false;
		commentMenuItem.Tag = null;
		commentMenuSeparator.Visible = false;
		if (viewSongArtistMenuItem != null)
		{
			viewSongArtistMenuItem.Visible = false;
			viewSongArtistMenuItem.Tag = null;
			viewSongArtistMenuItem.Text = "歌手(&A)";
			viewSongArtistMenuItem.DropDownItems.Clear();
		}
		if (viewSongAlbumMenuItem != null)
		{
			viewSongAlbumMenuItem.Visible = false;
			viewSongAlbumMenuItem.Tag = null;
			viewSongAlbumMenuItem.Text = "专辑(&B)";
			viewSongAlbumMenuItem.DropDownItems.Clear();
		}
		if (viewPodcastMenuItem != null)
		{
			viewPodcastMenuItem.Visible = false;
			viewPodcastMenuItem.Tag = null;
		}
		shareSongMenuItem.Visible = false;
		shareSongMenuItem.Tag = null;
		shareSongWebMenuItem.Text = "复制歌曲网页链接(&W)";
		shareSongDirectMenuItem.Text = "复制歌曲直链(&L)";
		shareSongWebMenuItem.Tag = null;
		shareSongDirectMenuItem.Tag = null;
		shareSongOpenWebMenuItem.Tag = null;
		sharePlaylistMenuItem.Visible = false;
		sharePlaylistMenuItem.Tag = null;
		sharePlaylistCopyWebMenuItem.Tag = null;
		sharePlaylistOpenWebMenuItem.Tag = null;
		shareAlbumMenuItem.Visible = false;
		shareAlbumMenuItem.Tag = null;
		shareAlbumCopyWebMenuItem.Tag = null;
		shareAlbumOpenWebMenuItem.Tag = null;
		sharePodcastMenuItem.Visible = false;
		sharePodcastMenuItem.Tag = null;
		sharePodcastCopyWebMenuItem.Tag = null;
		sharePodcastOpenWebMenuItem.Tag = null;
		sharePodcastEpisodeMenuItem.Visible = false;
		sharePodcastEpisodeMenuItem.Tag = null;
		sharePodcastEpisodeWebMenuItem.Tag = null;
		sharePodcastEpisodeDirectMenuItem.Tag = null;
		sharePodcastEpisodeOpenWebMenuItem.Tag = null;
		shareArtistCopyWebMenuItem.Tag = null;
		shareArtistOpenWebMenuItem.Tag = null;
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

	private void ConfigureCategoryMenu(MenuContextSnapshot snapshot, ref bool showViewSection, ref CommentTarget? contextCommentTarget)
	{
		insertPlayMenuItem.Visible = false;
		downloadCategoryMenuItem.Visible = true;
		if (snapshot?.ListItem == null || !string.Equals(snapshot.ListItem.CategoryId, "user_liked_songs", StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		downloadCategoryMenuItem.Text = "下载歌单(&C)...";
		PlaylistInfo playlistInfo = _userLikedPlaylist;
		if (playlistInfo == null || string.IsNullOrWhiteSpace(playlistInfo.Id))
		{
			long currentUserId = GetCurrentUserId();
			if (currentUserId > 0)
			{
				playlistInfo = new PlaylistInfo
				{
					Id = currentUserId.ToString(CultureInfo.InvariantCulture),
					Name = "喜欢的音乐"
				};
			}
			else
			{
				return;
			}
		}
		sharePlaylistMenuItem.Visible = true;
		sharePlaylistMenuItem.Tag = playlistInfo;
		sharePlaylistCopyWebMenuItem.Tag = playlistInfo;
		sharePlaylistOpenWebMenuItem.Tag = playlistInfo;
		showViewSection = true;
		contextCommentTarget = new CommentTarget(playlistInfo.Id, CommentType.Playlist, string.IsNullOrWhiteSpace(playlistInfo.Name) ? "喜欢的音乐" : playlistInfo.Name, playlistInfo.Creator);
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
			sharePlaylistCopyWebMenuItem.Tag = playlist;
			sharePlaylistOpenWebMenuItem.Tag = playlist;
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
			shareAlbumCopyWebMenuItem.Tag = album;
			shareAlbumOpenWebMenuItem.Tag = album;
			bool flag2 = ConfigureAlbumArtistMenu(album, isLoggedIn);
			if (flag2)
			{
				showViewSection = true;
			}
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

	private static string BuildArtistSummaryText(List<ArtistInfo> artists)
	{
		if (artists == null || artists.Count == 0)
		{
			return "未知歌手";
		}
		List<string> list = (from artist in artists
			select (artist?.Name ?? string.Empty).Trim() into name
			where !string.IsNullOrWhiteSpace(name)
			select name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		if (list.Count == 0)
		{
			return "未知歌手";
		}
		return string.Join("/", list);
	}

	private bool ConfigureArtistOwnerMenu(List<ArtistInfo> artists, bool allowSubscribe, object ownerTag)
	{
		if (viewSongArtistMenuItem == null)
		{
			return false;
		}
		viewSongArtistMenuItem.DropDownItems.Clear();
		List<ArtistInfo> list = (from artist in artists
			where artist != null && (artist.Id > 0 || !string.IsNullOrWhiteSpace(artist.Name))
			select artist).ToList();
		if (list.Count == 0)
		{
			viewSongArtistMenuItem.Visible = false;
			viewSongArtistMenuItem.Tag = null;
			return false;
		}
		viewSongArtistMenuItem.Text = "歌手：" + BuildArtistSummaryText(list);
		foreach (ArtistInfo item in list)
		{
			string text = (string.IsNullOrWhiteSpace(item.Name) ? ("歌手 " + item.Id) : item.Name.Trim());
			ArtistInfo artistInfo = new ArtistInfo
			{
				Id = item.Id,
				Name = text,
				PicUrl = item.PicUrl ?? string.Empty
			};
			ToolStripMenuItem toolStripMenuItem = new ToolStripMenuItem(text);
			toolStripMenuItem.Tag = artistInfo;
			ToolStripMenuItem toolStripMenuItem2 = new ToolStripMenuItem("查看歌手");
			toolStripMenuItem2.Tag = artistInfo;
			toolStripMenuItem2.Click += viewSongArtistMenuItem_Click;
			toolStripMenuItem.DropDownItems.Add(toolStripMenuItem2);
			if (allowSubscribe)
			{
				bool flag = IsArtistSubscribed(artistInfo);
				ToolStripMenuItem toolStripMenuItem3 = new ToolStripMenuItem(flag ? "收藏歌手（已收藏）" : "收藏歌手");
				toolStripMenuItem3.Tag = artistInfo;
				toolStripMenuItem3.Enabled = !flag;
				toolStripMenuItem3.Click += subscribeSongArtistMenuItem_Click;
				toolStripMenuItem.DropDownItems.Add(toolStripMenuItem3);
			}
			viewSongArtistMenuItem.DropDownItems.Add(toolStripMenuItem);
		}
		viewSongArtistMenuItem.Visible = viewSongArtistMenuItem.DropDownItems.Count > 0;
		viewSongArtistMenuItem.Tag = (viewSongArtistMenuItem.Visible ? ownerTag : null);
		return viewSongArtistMenuItem.Visible;
	}

	private bool ConfigureSongArtistMenu(SongInfo songInfo, bool allowSubscribe)
	{
		return ConfigureArtistOwnerMenu(BuildSongArtistInfoList(songInfo), allowSubscribe, songInfo);
	}

	private bool ConfigureAlbumArtistMenu(AlbumInfo albumInfo, bool allowSubscribe)
	{
		return ConfigureArtistOwnerMenu(BuildAlbumArtistInfoList(albumInfo), allowSubscribe, albumInfo);
	}

	private bool ConfigureSongAlbumMenu(SongInfo songInfo, AlbumInfo albumInfo, bool allowSubscribe)
	{
		if (viewSongAlbumMenuItem == null)
		{
			return false;
		}
		viewSongAlbumMenuItem.DropDownItems.Clear();
		string text = (albumInfo?.Name ?? string.Empty).Trim();
		if (string.IsNullOrWhiteSpace(text))
		{
			text = (songInfo?.Album ?? string.Empty).Trim();
		}
		if (string.IsNullOrWhiteSpace(text))
		{
			text = "未知专辑";
		}
		viewSongAlbumMenuItem.Text = "专辑：" + text;
		if (songInfo != null || (albumInfo != null && !string.IsNullOrWhiteSpace(albumInfo.Id)))
		{
			ToolStripMenuItem toolStripMenuItem = new ToolStripMenuItem("查看专辑");
			toolStripMenuItem.Tag = ((albumInfo != null && !string.IsNullOrWhiteSpace(albumInfo.Id)) ? ((object)albumInfo) : songInfo);
			toolStripMenuItem.Click += viewSongAlbumMenuItem_Click;
			viewSongAlbumMenuItem.DropDownItems.Add(toolStripMenuItem);
		}
		if (allowSubscribe)
		{
			object obj = ((albumInfo != null && !string.IsNullOrWhiteSpace(albumInfo.Id)) ? ((object)albumInfo) : songInfo);
			if (obj != null)
			{
				bool flag = albumInfo != null && !string.IsNullOrWhiteSpace(albumInfo.Id) && IsAlbumSubscribed(albumInfo);
				ToolStripMenuItem toolStripMenuItem2 = new ToolStripMenuItem(flag ? "收藏专辑（已收藏）" : "收藏专辑");
				toolStripMenuItem2.Tag = obj;
				toolStripMenuItem2.Enabled = !flag;
				toolStripMenuItem2.Click += subscribeSongAlbumMenuItem_Click;
				viewSongAlbumMenuItem.DropDownItems.Add(toolStripMenuItem2);
			}
		}
		object obj2 = ((songInfo != null) ? ((object)songInfo) : albumInfo);
		viewSongAlbumMenuItem.Visible = viewSongAlbumMenuItem.DropDownItems.Count > 0;
		viewSongAlbumMenuItem.Tag = (viewSongAlbumMenuItem.Visible ? obj2 : null);
		return viewSongAlbumMenuItem.Visible;
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
		AlbumInfo albumInfo = ((!isPodcastEpisodeContext) ? TryCreateAlbumInfoFromSong(songInfo) : null);
		bool isCloudSongContext = IsCloudSongContext(songInfo, isCloudView);
		if (!isCloudSongContext && snapshot.IsCurrentPlayback)
		{
			string text2 = ResolveCurrentPlayingViewSource(songInfo);
			isCloudSongContext = !string.IsNullOrWhiteSpace(text2) && text2.StartsWith("user_cloud", StringComparison.OrdinalIgnoreCase);
		}
		bool flag2 = isLoggedIn && !isPodcastEpisodeContext;
		if (flag2)
		{
			if (isCloudSongContext)
			{
				flag2 = albumInfo != null && !IsAlbumSubscribed(albumInfo);
			}
			else
			{
				flag2 = !string.IsNullOrWhiteSpace(ResolveSongIdForLibraryState(songInfo)) && (albumInfo == null || !IsAlbumSubscribed(albumInfo));
			}
		}
		subscribeSongAlbumMenuItem.Visible = false;
		subscribeSongAlbumMenuItem.Tag = null;
		bool canSubscribeSongArtist = isLoggedIn && !isPodcastEpisodeContext && songInfo != null;
		subscribeSongArtistMenuItem.Visible = false;
		subscribeSongArtistMenuItem.Tag = null;
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
			bool flag3 = IsCurrentLikedSongsView();
			bool flag4 = flag3;
			if (flag && songInfo != null && !flag4)
			{
				flag4 = IsSongLiked(songInfo);
			}
			likeSongMenuItem.Visible = flag && !flag4;
			unlikeSongMenuItem.Visible = flag && flag4;
			likeSongMenuItem.Tag = (flag ? songInfo : null);
			unlikeSongMenuItem.Tag = (flag ? songInfo : null);
			addToPlaylistMenuItem.Visible = flag;
			addToPlaylistMenuItem.Tag = (flag ? songInfo : null);
			string playlistId = (snapshot.ViewSource.StartsWith("playlist:", StringComparison.OrdinalIgnoreCase) ? snapshot.ViewSource.Substring("playlist:".Length) : null);
			bool flag5 = snapshot.ViewSource.StartsWith("playlist:", StringComparison.OrdinalIgnoreCase) || (_currentPlaylist != null && !string.IsNullOrWhiteSpace(_currentPlaylist.Id));
			bool flag6 = _currentPlaylistOwnedByUser;
			if (!flag6 && !string.IsNullOrWhiteSpace(playlistId) && _currentPlaylist != null && string.Equals(_currentPlaylist.Id, playlistId, StringComparison.OrdinalIgnoreCase))
			{
				flag6 = IsPlaylistOwnedByUser(_currentPlaylist, GetCurrentUserId());
			}
			bool flag7 = flag5 && flag6;
			if (snapshot.IsCurrentPlayback)
			{
				removeFromPlaylistMenuItem.Visible = false;
				removeFromPlaylistMenuItem.Tag = null;
				removeFromPlaylistMenuItem.Text = "从歌单中移除(&R)";
			}
			else
			{
				removeFromPlaylistMenuItem.Text = "从歌单中移除(&R)";
				removeFromPlaylistMenuItem.Visible = flag && flag7 && !flag3;
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
		bool flag8 = isCloudView && songInfo != null && songInfo.IsCloudSong;
		downloadSongMenuItem.Visible = !flag8;
		downloadSongMenuItem.Tag = songInfo;
		downloadSongMenuItem.Text = (isPodcastEpisodeContext ? "下载声音(&D)" : "下载歌曲(&D)");
		bool flag9 = !flag8 && !isPodcastEpisodeContext;
		ConfigureLyricsLanguageMenuForSong(songInfo, flag9);
		batchDownloadMenuItem.Visible = !flag8 && !snapshot.IsCurrentPlayback;
		bool flag10 = !isPodcastEpisodeContext && ConfigureSongArtistMenu(songInfo, canSubscribeSongArtist);
		bool flag11 = false;
		if (!isPodcastEpisodeContext && songInfo != null)
		{
			bool canShowAlbumOwnerMenu = !songInfo.IsCloudSong || !string.IsNullOrWhiteSpace(songInfo?.Album);
			flag11 = ConfigureSongAlbumMenu(songInfo, canShowAlbumOwnerMenu ? albumInfo : null, flag2);
		}
		bool flag12 = songInfo != null && flag;
		if (isPodcastEpisodeContext)
		{
			flag11 = false;
			flag12 = false;
		}
		shareSongMenuItem.Visible = flag12;
		if (flag12)
		{
			shareSongMenuItem.Tag = songInfo;
			shareSongWebMenuItem.Tag = songInfo;
			shareSongDirectMenuItem.Tag = songInfo;
			shareSongOpenWebMenuItem.Tag = songInfo;
			bool flag13 = songInfo != null && songInfo.IsCloudSong;
			shareSongWebMenuItem.Text = (flag13 ? "复制音乐网页链接(&W)" : "复制歌曲网页链接(&W)");
			shareSongDirectMenuItem.Text = (flag13 ? "复制音乐直链(&L)" : "复制歌曲直链(&L)");
		}
		else
		{
			shareSongMenuItem.Tag = null;
			shareSongWebMenuItem.Tag = null;
			shareSongDirectMenuItem.Tag = null;
			shareSongOpenWebMenuItem.Tag = null;
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
		bool flag14 = false;
		if (viewPodcastMenuItem != null)
		{
			bool flag15 = contextPodcastForEpisode != null && contextPodcastForEpisode.Id > 0;
			viewPodcastMenuItem.Visible = flag15;
			viewPodcastMenuItem.Tag = (flag15 ? contextPodcastForEpisode : null);
			flag14 = flag15;
		}
		bool visible = sharePodcastMenuItem.Visible;
		bool visible2 = sharePodcastEpisodeMenuItem.Visible;
		showViewSection = showViewSection || flag10 || flag11 || flag12 || flag2 || flag14 || visible || visible2;
	}


}
}
