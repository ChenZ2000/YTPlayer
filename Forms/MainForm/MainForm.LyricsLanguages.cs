#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using YTPlayer.Core;
using YTPlayer.Core.Lyrics;
using YTPlayer.Models;

namespace YTPlayer
{
public partial class MainForm
{
	private readonly object _lyricsContextLock = new object();
	private readonly Dictionary<string, LoadedLyricsContext> _lyricsContextCache = new Dictionary<string, LoadedLyricsContext>(StringComparer.OrdinalIgnoreCase);
	private const int LyricsContextCacheLimit = 120;
	private LoadedLyricsContext _currentLyricsContext;
	private int _lyricsLanguageMenuRequestToken;

	private sealed class LyricLanguageMenuTag
	{
		public string SongId { get; }
		public string LanguageKey { get; }

		public LyricLanguageMenuTag(string songId, string languageKey)
		{
			SongId = songId ?? string.Empty;
			LanguageKey = languageKey ?? string.Empty;
		}
	}

	private void CacheLoadedLyricsContext(LoadedLyricsContext context)
	{
		if (context == null || string.IsNullOrWhiteSpace(context.SongId))
		{
			return;
		}

		lock (_lyricsContextLock)
		{
			_lyricsContextCache[context.SongId] = context;
			if (_lyricsContextCache.Count <= LyricsContextCacheLimit)
			{
				return;
			}

			string currentSongId = _audioEngine?.CurrentSong?.Id;
			string removable = _lyricsContextCache.Keys
				.FirstOrDefault(id => !string.Equals(id, currentSongId, StringComparison.OrdinalIgnoreCase));
			if (!string.IsNullOrWhiteSpace(removable))
			{
				_lyricsContextCache.Remove(removable);
			}
		}
	}

	private LoadedLyricsContext TryGetCachedLyricsContext(string songId)
	{
		if (string.IsNullOrWhiteSpace(songId))
		{
			return null;
		}

		lock (_lyricsContextLock)
		{
			return _lyricsContextCache.TryGetValue(songId, out var context) ? context : null;
		}
	}

	private async Task<LoadedLyricsContext> GetOrLoadLyricsContextAsync(
		string songId,
		CancellationToken cancellationToken = default(CancellationToken))
	{
		if (string.IsNullOrWhiteSpace(songId) || _apiClient == null)
		{
			return null;
		}

		var cached = TryGetCachedLyricsContext(songId);
		if (cached != null)
		{
			return cached;
		}

		var loaded = await _apiClient
			.GetResolvedLyricsAsync(songId, cancellationToken: cancellationToken)
			.ConfigureAwait(false);
		if (loaded != null)
		{
			CacheLoadedLyricsContext(loaded);
		}
		return loaded;
	}

	private List<LyricLine> BuildLegacyLyricLines(LyricsData lyricsData)
	{
		if (lyricsData == null || lyricsData.IsEmpty)
		{
			return new List<LyricLine>();
		}

		return lyricsData.Lines
			.Select(line =>
			{
				var texts = LyricLanguagePipeline.GetOrderedLineTexts(line, lyricsData.SelectedLanguageKeys);
				string mergedText = texts.Count > 0 ? string.Join(" | ", texts) : (line?.Text ?? string.Empty);
				return new LyricLine(line.Time, mergedText);
			})
			.ToList();
	}

	private void ApplyLoadedLyricsContext(LoadedLyricsContext context, bool forceImmediateUpdate = false)
	{
		_currentLyricsContext = context;
		if (context == null || context.LyricsData == null || context.LyricsData.IsEmpty)
		{
			_lyricsDisplayManager?.Clear();
			_currentLyrics?.Clear();
			return;
		}

		CacheLoadedLyricsContext(context);
		_lyricsDisplayManager.LoadLyrics(context.LyricsData);
		_currentLyrics = BuildLegacyLyricLines(context.LyricsData);

		if (forceImmediateUpdate)
		{
			TimeSpan currentPosition = TimeSpan.FromSeconds(GetCachedPosition());
			_lyricsDisplayManager.ForceUpdate(currentPosition);
		}
	}

	private void ConfigureLyricsLanguageMenuForSong(SongInfo songInfo, bool canShow)
	{
		if (lyricsLanguageMenuItem == null || downloadLyricsMenuItem == null)
		{
			return;
		}

		downloadLyricsMenuItem.Visible = false;
		downloadLyricsMenuItem.Tag = null;
		lyricsLanguageMenuItem.Visible = false;
		lyricsLanguageMenuItem.Tag = songInfo;
		lyricsLanguageMenuItem.DropDownItems.Clear();
		lyricsLanguageMenuItem.Text = "歌词翻译";

		int requestToken = Interlocked.Increment(ref _lyricsLanguageMenuRequestToken);

		if (!canShow || songInfo == null || string.IsNullOrWhiteSpace(songInfo.Id))
		{
			return;
		}

		var context = TryGetCachedLyricsContext(songInfo.Id);
		if (context != null)
		{
			RenderLyricsMenuState(songInfo, context);
			return;
		}

		_ = EnsureLyricsLanguageMenuLoadedAsync(songInfo, requestToken);
	}

	private async Task EnsureLyricsLanguageMenuLoadedAsync(SongInfo songInfo, int requestToken)
	{
		if (songInfo == null || string.IsNullOrWhiteSpace(songInfo.Id))
		{
			return;
		}

		LoadedLyricsContext context = null;
		try
		{
			context = await GetOrLoadLyricsContextAsync(songInfo.Id).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"[Lyrics] 异步拉取歌词失败: songId={songInfo.Id}, error={ex.Message}");
			return;
		}

		if (context == null || context.LanguageProfile == null || !context.LanguageProfile.HasLyrics)
		{
			return;
		}

		SafeInvoke(delegate
		{
			if (lyricsLanguageMenuItem == null || downloadLyricsMenuItem == null || songContextMenu == null)
			{
				return;
			}

			if (requestToken != _lyricsLanguageMenuRequestToken)
			{
				return;
			}

			if (!songContextMenu.Visible)
			{
				return;
			}

			if (lyricsLanguageMenuItem.Tag is not SongInfo menuSong ||
			    !string.Equals(menuSong.Id, songInfo.Id, StringComparison.OrdinalIgnoreCase))
			{
				return;
			}

			RenderLyricsMenuState(menuSong, context);
		});
	}

	private void RenderLyricsMenuState(SongInfo songInfo, LoadedLyricsContext context)
	{
		if (downloadLyricsMenuItem == null || lyricsLanguageMenuItem == null || songInfo == null)
		{
			return;
		}

		bool hasLyrics = context != null && context.LanguageProfile != null && context.LanguageProfile.HasLyrics;
		downloadLyricsMenuItem.Visible = hasLyrics;
		downloadLyricsMenuItem.Tag = hasLyrics ? songInfo : null;

		if (!hasLyrics)
		{
			lyricsLanguageMenuItem.Visible = false;
			lyricsLanguageMenuItem.Tag = songInfo;
			lyricsLanguageMenuItem.DropDownItems.Clear();
			lyricsLanguageMenuItem.Text = "歌词翻译";
			return;
		}

		if (!HasTranslationTrack(context.LanguageProfile))
		{
			lyricsLanguageMenuItem.Visible = false;
			lyricsLanguageMenuItem.Tag = songInfo;
			lyricsLanguageMenuItem.DropDownItems.Clear();
			lyricsLanguageMenuItem.Text = "歌词翻译";
			return;
		}

		lyricsLanguageMenuItem.DropDownItems.Clear();
		lyricsLanguageMenuItem.Tag = songInfo;

		var selectedSet = new HashSet<string>(context.SelectedLanguageKeys ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
		bool hasOrigTrack = context.LanguageProfile.TryGetTrack("orig", out var originalTrack) && originalTrack != null;
		bool hasTransTrack = context.LanguageProfile.TryGetTrack("trans", out var translationTrack) && translationTrack != null;
		bool selectedOrig = selectedSet.Contains("orig");
		bool selectedTrans = selectedSet.Contains("trans");
		if (!selectedOrig && !selectedTrans)
		{
			if (hasOrigTrack)
			{
				selectedOrig = true;
				selectedSet.Add("orig");
			}
			else if (hasTransTrack)
			{
				selectedTrans = true;
				selectedSet.Add("trans");
			}
		}

		lyricsLanguageMenuItem.Text = BuildLyricsTranslationMenuText(selectedOrig, selectedTrans);

		var tracksForMenu = new List<LyricLanguageTrack>();
		if (hasOrigTrack)
		{
			tracksForMenu.Add(originalTrack);
		}
		if (hasTransTrack)
		{
			tracksForMenu.Add(translationTrack);
		}

		foreach (var track in tracksForMenu)
		{
			if (track == null || string.IsNullOrWhiteSpace(track.Key))
			{
				continue;
			}

			ToolStripMenuItem item = new ToolStripMenuItem(track.DisplayName)
			{
				CheckOnClick = true,
				Checked = selectedSet.Contains(track.Key),
				Tag = new LyricLanguageMenuTag(songInfo.Id, track.Key)
			};
			item.Click += LyricsLanguageOptionMenuItem_Click;
			lyricsLanguageMenuItem.DropDownItems.Add(item);
		}

		if (lyricsLanguageMenuItem.DropDownItems.Count == 0)
		{
			return;
		}

		lyricsLanguageMenuItem.Visible = true;
		lyricsLanguageMenuItem.Owner?.PerformLayout();
		songContextMenu?.PerformLayout();
		songContextMenu?.Refresh();
	}

	private static bool HasTranslationTrack(LyricLanguageProfile profile)
	{
		if (profile == null || profile.Tracks == null || profile.Tracks.Count == 0)
		{
			return false;
		}

		return profile.Tracks.Any(track =>
			track != null &&
			string.Equals(track.Key, "trans", StringComparison.OrdinalIgnoreCase) &&
			!string.IsNullOrWhiteSpace(track.Content));
	}

	private static string BuildLyricsTranslationMenuText(bool selectedOrig, bool selectedTrans)
	{
		string selectionText;
		if (selectedOrig && selectedTrans)
		{
			selectionText = "原文及翻译";
		}
		else if (selectedTrans)
		{
			selectionText = "翻译";
		}
		else
		{
			selectionText = "原文";
		}

		return $"歌词翻译：{selectionText}";
	}

	private async void LyricsLanguageOptionMenuItem_Click(object sender, EventArgs e)
	{
		if (sender is not ToolStripMenuItem clickedItem ||
		    clickedItem.Tag is not LyricLanguageMenuTag tag ||
		    lyricsLanguageMenuItem == null)
		{
			return;
		}

		var selectedKeys = lyricsLanguageMenuItem.DropDownItems
			.OfType<ToolStripMenuItem>()
			.Where(item => item.Checked && item.Tag is LyricLanguageMenuTag)
			.Select(item => ((LyricLanguageMenuTag)item.Tag).LanguageKey)
			.Where(key => !string.IsNullOrWhiteSpace(key))
			.ToList();

		if (selectedKeys.Count == 0)
		{
			clickedItem.Checked = true;
			selectedKeys.Add(tag.LanguageKey);
		}

		await ApplyLyricLanguageSelectionAsync(tag.SongId, selectedKeys).ConfigureAwait(true);
		RefreshLyricsLanguageMenuChecks(tag.SongId);
	}

	private void RefreshLyricsLanguageMenuChecks(string songId)
	{
		if (lyricsLanguageMenuItem == null || string.IsNullOrWhiteSpace(songId))
		{
			return;
		}

		var context = TryGetCachedLyricsContext(songId);
		if (context == null)
		{
			return;
		}

		if (!HasTranslationTrack(context.LanguageProfile))
		{
			lyricsLanguageMenuItem.Visible = false;
			lyricsLanguageMenuItem.DropDownItems.Clear();
			lyricsLanguageMenuItem.Text = "歌词翻译";
			return;
		}

		var selectedSet = new HashSet<string>(context.SelectedLanguageKeys ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
		bool hasOrigItem = lyricsLanguageMenuItem.DropDownItems
			.OfType<ToolStripMenuItem>()
			.Any(item => item.Tag is LyricLanguageMenuTag tag && string.Equals(tag.LanguageKey, "orig", StringComparison.OrdinalIgnoreCase));
		bool hasTransItem = lyricsLanguageMenuItem.DropDownItems
			.OfType<ToolStripMenuItem>()
			.Any(item => item.Tag is LyricLanguageMenuTag tag && string.Equals(tag.LanguageKey, "trans", StringComparison.OrdinalIgnoreCase));
		bool selectedOrig = selectedSet.Contains("orig");
		bool selectedTrans = selectedSet.Contains("trans");
		if (!selectedOrig && !selectedTrans)
		{
			if (hasOrigItem)
			{
				selectedOrig = true;
				selectedSet.Add("orig");
			}
			else if (hasTransItem)
			{
				selectedTrans = true;
				selectedSet.Add("trans");
			}
		}

		lyricsLanguageMenuItem.Text = BuildLyricsTranslationMenuText(selectedOrig, selectedTrans);
		foreach (var item in lyricsLanguageMenuItem.DropDownItems.OfType<ToolStripMenuItem>())
		{
			if (item.Tag is LyricLanguageMenuTag itemTag)
			{
				item.Checked = selectedSet.Contains(itemTag.LanguageKey);
			}
		}
	}

	private async Task ApplyLyricLanguageSelectionAsync(string songId, IEnumerable<string> selectedLanguageKeys)
	{
		if (string.IsNullOrWhiteSpace(songId) || _apiClient == null)
		{
			return;
		}

		var context = TryGetCachedLyricsContext(songId);
		if (context == null)
		{
			context = await GetOrLoadLyricsContextAsync(songId).ConfigureAwait(true);
		}

		if (context == null || context.LanguageProfile == null || !context.LanguageProfile.HasLyrics)
		{
			return;
		}

		var normalizedSelection = LyricLanguagePipeline.NormalizeSelection(context.LanguageProfile, selectedLanguageKeys);
		if (normalizedSelection.Count == 0)
		{
			return;
		}

		_apiClient.SetSongLyricLanguagePreference(songId, normalizedSelection, context.LanguageProfile.DefaultLanguageKeys);

		context.SelectedLanguageKeys = normalizedSelection.ToList();
		context.LyricsData = LyricLanguagePipeline.BuildLyricsData(songId, context.LanguageProfile, context.SelectedLanguageKeys);
		context.ExportLyricContent = LyricLanguagePipeline.BuildExportLyricContent(songId, context.LanguageProfile, context.SelectedLanguageKeys);
		CacheLoadedLyricsContext(context);

		SongInfo currentSong = _audioEngine?.CurrentSong;
		if (currentSong != null && string.Equals(currentSong.Id, songId, StringComparison.OrdinalIgnoreCase))
		{
			CancelPendingLyricSpeech(resetSuppression: true, stopGlobalTts: false);
			ApplyLoadedLyricsContext(context, forceImmediateUpdate: true);
		}
	}
}
}
