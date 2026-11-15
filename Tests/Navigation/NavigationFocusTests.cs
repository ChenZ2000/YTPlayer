using System.Collections;
using Newtonsoft.Json.Linq;
using YTPlayer;
using YTPlayer.Core;
using YTPlayer.Models;
using YTPlayer.Models.Auth;

internal static partial class Program
{
    private static List<ArtistInfo> FocusArtists(int count = 450) => Enumerable.Range(1, count)
        .Select(i => new ArtistInfo { Id = 1000 + i, Name = "收藏歌手 " + i, MusicCount = 10, AlbumCount = 2, BriefDesc = "简介", IsSubscribed = true }).ToList();

    private static void ShowFocusArtists(List<ArtistInfo> artists) => Call("DisplayArtists", artists, false, false, 1, false, "artist_favorites", "收藏的歌手");

    private static void QueueArtistPage(IEnumerable<ArtistInfo> artists, int total, Task? gate = null)
    {
        handler.ArtistPages.Enqueue((new JObject { ["code"] = 200, ["count"] = total,
            ["data"] = new JArray(artists.Select(artist => new JObject { ["id"] = artist.Id, ["name"] = artist.Name,
                ["musicSize"] = 10, ["albumSize"] = 2, ["briefDesc"] = "简介" })) }, gate ?? Task.CompletedTask));
    }

    private static async Task NavigationFocusRestoreAsync()
    {
        Console.WriteLine("Incremental lists: Backspace restores past the first batch");
        var account = Get<AccountState?>("_accountState");
        long user = Get<long>("_loggedInUserId");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Set("_accountState", new AccountState { IsLoggedIn = true, UserId = "42" });
            Set("_loggedInUserId", 42L);
            var cache = Get<IDictionary>("_libraryCacheTimestamps");
            foreach (object key in cache.Keys.Cast<object>().ToArray()) cache[key] = DateTime.UtcNow;
            History.Clear();
            var artists = FocusArtists();
            ShowFocusArtists(artists); Select(417);
            await Run("OpenArtistAsync", artists[417], false);
            Check(History.Peek().SelectedIndex == 417, "artist navigation stores the original 418th row");
            QueueArtistPage(artists.Take(100), 450);
            QueueArtistPage(artists.Skip(100).Take(100), 450, gate.Task);
            QueueArtistPage(artists.Skip(200).Take(100), 450);
            QueueArtistPage(artists.Skip(300).Take(100), 450);
            QueueArtistPage(artists.Skip(400), 450);
            await Back();
            Check(Source == "artist_favorites" && list.Items.Count == 100, "Back returns while only the first 100 artists are available");
            Check(State().SelectedIndex == 417 && State().SelectedItemKey == "artist:1418",
                "leaving an incomplete restore preserves its original target in navigation history");
            Console.WriteLine("First batch selection=" + (list.SelectedIndices.Count == 0 ? -1 : list.SelectedIndices[0]));
            gate.TrySetResult();
            await Until(() => list.Items.Count == 450);
            await Task.Delay(100);
            Check(list.SelectedIndices.Count == 1 && list.SelectedIndices[0] == 417 && list.FocusedItem?.Index == 417,
                "Back must restore the 418th artist after loading, not stay at the end of the first batch");
            await Until(() => !Get<bool>("_artistFavoritesProactiveLoadRunning"));
            await NavigationFocusRenderersAsync();
            await NavigationFocusEdgeCasesAsync();
            await NavigationFocusLateResponseAsync();
        }
        finally
        {
            gate.TrySetResult();
            Call("CancelViewContentOperation");
            Set("_accountState", account); Set("_loggedInUserId", user);
        }
    }

    private static int FocusSelection => list.SelectedIndices.Count == 0 ? -1 : list.SelectedIndices[0];
    private static bool FocusPending => (bool)Call("HasPendingNavigationRestoreFocus", Source)!;
    private static void PatchFocusArtists(List<ArtistInfo> artists) => Call("PatchArtists", artists, 1, false, false, false, -1, false, false);

    private static IDisposable BeginFocusRestore(int index, string? key)
    {
        Call("BeginViewContentOperation");
        Call("SetPendingNavigationRestoreFocus", Source, index, key);
        Call("BindPendingNavigationFocus", Source, Call("GetCurrentViewContentToken"));
        return (IDisposable)Call("TrackListPopulation")!;
    }

    private static async Task NavigationFocusRenderersAsync()
    {
        Console.WriteLine("Incremental restore across all shared list renderers");
        var artists = FocusArtists();
        var songs = Enumerable.Range(1, 450).Select(i => new SongInfo { Id = i.ToString(), Name = "歌曲 " + i, Artist = "歌手", Duration = 180 }).ToList();
        var playlists = Enumerable.Range(1, 450).Select(i => new PlaylistInfo { Id = i.ToString(), Name = "歌单 " + i }).ToList();
        var albums = Enumerable.Range(1, 450).Select(i => new AlbumInfo { Id = i.ToString(), Name = "专辑 " + i, Artist = "歌手" }).ToList();
        var podcasts = Enumerable.Range(1, 450).Select(i => new PodcastRadioInfo { Id = i, Name = "播客 " + i }).ToList();
        var episodes = Enumerable.Range(1, 450).Select(i => new PodcastEpisodeInfo { ProgramId = i, Name = "节目 " + i, RadioId = 8 }).ToList();
        var mixed = artists.Select(artist => new ListItemInfo { Type = ListItemType.Artist, Artist = artist }).ToList();
        var renderers = new (string Name, Action<int> Show, Action<int> Patch)[]
        {
            ("artists", n => ShowFocusArtists(artists.Take(n).ToList()), n => PatchFocusArtists(artists.Take(n).ToList())),
            ("songs", n => Call("DisplaySongs", songs.Take(n).ToList(), false, false, 1, false, "user_cloud", "歌曲", true),
                n => Call("PatchSongs", songs.Take(n).ToList(), 1, true, false, false, false, -1, false)),
            ("playlists", n => Call("DisplayPlaylists", playlists.Take(n).ToList(), false, "user_playlists", "歌单"),
                n => Call("PatchPlaylists", playlists.Take(n).ToList(), 1, false, false, false, -1, false)),
            ("albums", n => Call("DisplayAlbums", albums.Take(n).ToList(), false, "user_albums", "专辑"),
                n => Call("PatchAlbums", albums.Take(n).ToList(), 1, false, false, false, -1, false)),
            ("podcasts", n => Call("DisplayPodcasts", podcasts.Take(n).ToList(), false, false, 1, false, "user_podcasts", "播客"),
                n => Call("PatchPodcasts", podcasts.Take(n).ToList(), 1, false, false, false, -1, false)),
            ("episodes", n => Call("DisplayPodcastEpisodes", episodes.Take(n).ToList(), false, false, 1, false, "podcast:8:offset0:asc1", "节目"),
                n => Call("PatchPodcastEpisodes", episodes.Take(n).ToList(), 1, -1, false)),
            ("mixed", n => Call("DisplayListItems", mixed.Take(n).ToList(), "homepage", "混合列表"),
                n => Call("PatchListItems", mixed.Take(n).ToList(), false, false, false, -1, true))
        };
        foreach (var renderer in renderers)
        {
            Call("ClearPendingNavigationRestoreFocus");
            Call("BeginViewContentOperation");
            renderer.Show(450); Select(417);
            var state = State();
            Check(state.SelectedItemKey != null, renderer.Name + " captures resource identity");
            using (BeginFocusRestore(417, state.SelectedItemKey))
            {
                renderer.Show(100);
                await Task.Delay(25);
                Check(FocusSelection == -1 && FocusPending, renderer.Name + " does not select temporary batch end");
                renderer.Patch(200);
                await Task.Delay(25);
                Check(FocusSelection == -1 && FocusPending, renderer.Name + " keeps target through another incomplete batch");
                renderer.Patch(450);
                await Until(() => FocusSelection == 417);
                Check(list.FocusedItem?.Index == 417, renderer.Name + " restores native item focus");
            }
            await Until(() => !FocusPending);
            Select(25);
            Check(FocusSelection == 25, renderer.Name + " accepts a new manual selection");
            renderer.Patch(450);
            await Task.Delay(25);
            Check(FocusSelection == 25, renderer.Name + " later updates preserve manual selection; actual=" + FocusSelection);
        }

        // The virtual renderer is retained in the app although currently disabled by default.
        Call("BeginViewContentOperation");
        Call("DisableVirtualSongList");
        list.Items.Clear(); Context("user_cloud");
        Call("ConfigureVirtualSongList", songs, 1, false, false, false);
        Select(417);
        string? virtualKey = State().SelectedItemKey;
        using (BeginFocusRestore(417, virtualKey))
        {
            Call("ConfigureVirtualSongList", songs.Take(100).ToList(), 1, false, false, false);
            await Task.Delay(25);
            Check(FocusPending, "virtual list waits for unavailable target");
            Call("ConfigureVirtualSongList", songs, 1, false, false, false);
            await Until(() => FocusSelection == 417);
            Check((string?)Call("GetNavigationItemKey", 417) == virtualKey, "virtual list restores correct resource");
        }
        Call("DisableVirtualSongList");
    }

    private static async Task NavigationFocusEdgeCasesAsync()
    {
        Console.WriteLine("Restore identity, shrinkage, user intent and cancellation");
        var artists = FocusArtists();
        foreach (int index in new[] { 0, 99, 100, 199, 200, 399, 449 })
        {
            Call("ClearPendingNavigationRestoreFocus"); Call("BeginViewContentOperation");
            ShowFocusArtists(artists); Select(index);
            string? key = State().SelectedItemKey;
            using (BeginFocusRestore(index, key))
            {
                ShowFocusArtists(artists.Take(100).ToList());
                await Task.Delay(25);
                Check(index < 100 ? FocusSelection == index : FocusPending && FocusSelection == -1, "batch boundary " + index);
                PatchFocusArtists(artists);
                await Until(() => FocusSelection == index);
            }
        }

        foreach (var scenario in new[] { "reorder", "delete-middle", "shrink-tail", "empty" })
        {
            Call("ClearPendingNavigationRestoreFocus"); Call("BeginViewContentOperation");
            ShowFocusArtists(artists); Select(417);
            string? key = State().SelectedItemKey;
            var changed = artists.ToList();
            int expected = 417;
            if (scenario == "reorder") { changed.RemoveAt(417); changed.Insert(440, artists[417]); expected = 440; }
            if (scenario == "delete-middle") changed.RemoveAt(417);
            if (scenario == "shrink-tail") { changed = changed.Take(150).ToList(); expected = 149; }
            if (scenario == "empty") { changed.Clear(); expected = 0; }
            using (BeginFocusRestore(417, key))
            {
                ShowFocusArtists(changed.Take(100).ToList());
                await Task.Delay(25);
                Check(FocusPending, scenario + " waits until full population can determine absence");
                PatchFocusArtists(changed);
                await Task.Delay(25);
                if (scenario != "reorder") Check(FocusPending && FocusSelection == -1, scenario + " never treats partial data as deletion");
            }
            await Until(() => FocusSelection == expected && !FocusPending);
            Check(list.FocusedItem?.Index == expected, scenario + " restores valid final row");
        }

        Call("BeginViewContentOperation"); ShowFocusArtists(artists); Select(50);
        using (BeginFocusRestore(50, State().SelectedItemKey))
        {
            ShowFocusArtists(artists.Take(100).ToList());
            await Until(() => FocusSelection == 50);
            var reordered = artists.ToList();
            reordered.RemoveAt(50); reordered.Insert(300, artists[50]);
            PatchFocusArtists(reordered);
            await Until(() => FocusSelection == 300);
            Check(FocusPending, "later batch reorder keeps the same restored resource");
            Call("resultListView_KeyDown", list, new KeyEventArgs(Keys.End));
            Select(7); PatchFocusArtists(artists);
            await Task.Delay(25);
            Check(FocusSelection == 7 && !FocusPending, "manual navigation cancels resource following during remaining batches");
        }

        foreach (string intent in new[] { "keyboard", "mouse", "type-search", "tab-away" })
        {
            Call("BeginViewContentOperation"); ShowFocusArtists(artists); Select(417);
            string? key = State().SelectedItemKey;
            using (BeginFocusRestore(417, key))
            {
                ShowFocusArtists(artists.Take(100).ToList());
                list.Focus();
                if (intent == "keyboard") Call("resultListView_KeyDown", list, new KeyEventArgs(Keys.End));
                if (intent == "mouse") Call("resultListView_MouseDown", list, new MouseEventArgs(MouseButtons.Right, 1, 5, 5, 0));
                if (intent == "type-search") Call("resultListView_KeyPress", list, new KeyPressEventArgs('z'));
                var search = Get<TextBox>("searchTextBox");
                if (intent == "tab-away") { search.Focus(); Check(search.Focused, "focus moved to search input"); }
                else { Check(!FocusPending, intent + " cancels automatic restoration"); Select(7); }
                PatchFocusArtists(artists);
                await Task.Delay(50);
                if (intent == "tab-away") Check(search.Focused && FocusSelection == 417, "late page restores row without stealing control focus");
                else Check(FocusSelection == 7, intent + " manual choice survives late page");
            }
        }

        Call("BeginViewContentOperation"); ShowFocusArtists(artists); Select(417);
        using (var oldPopulation = BeginFocusRestore(417, State().SelectedItemKey))
        {
            ShowFocusArtists(artists.Take(100).ToList());
            Call("BeginViewContentOperation");
            Call("BindPendingNavigationFocus", Source, Call("GetCurrentViewContentToken"));
            Check(!FocusPending, "same-source reload discards previous generation target");
            ShowFocusArtists(artists.Take(25).ToList()); Select(10);
            oldPopulation.Dispose();
            await Task.Delay(25);
            Check(FocusSelection == 10, "canceled population completion cannot restore old selection");
        }

        // A positive explicit focus request, such as a refreshed search result, shares the same waiting logic.
        Call("BeginViewContentOperation"); ShowFocusArtists(artists); Select(417);
        using (var population = (IDisposable)Call("TrackListPopulation")!)
        {
            Call("RequestListFocus", Source, 417, false);
            ShowFocusArtists(artists.Take(100).ToList());
            await Task.Delay(25);
            Check(FocusPending && FocusSelection == -1, "explicit focus request does not clamp during loading");
            PatchFocusArtists(artists);
            await Until(() => FocusSelection == 417);
        }
        await Until(() => !FocusPending);
        Check(Get<IDictionary>("_listPopulations").Count == 0, "population scopes are released after all operations");
    }

    private static async Task NavigationFocusLateResponseAsync()
    {
        Console.WriteLine("Repeated Backspace with a delayed response from the previous visit");
        var artists = FocusArtists();
        var oldGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            History.Clear(); Call("BeginViewContentOperation");
            ShowFocusArtists(artists); Select(417);
            await Run("OpenArtistAsync", artists[417], false);
            int started = handler.ArtistPagesStarted;
            QueueArtistPage(artists.Take(100), 450);
            QueueArtistPage(artists.Skip(100).Take(100), 450, oldGate.Task);
            await Back();
            await Until(() => handler.ArtistPagesStarted == started + 2);
            Check(FocusPending && list.Items.Count == 100, "previous visit has an outstanding second page");

            Call("resultListView_KeyDown", list, new KeyEventArgs(Keys.End));
            Select(10);
            await Run("OpenArtistAsync", artists[10], false);
            QueueArtistPage(artists.Take(25), 25);
            await Back();
            await Until(() => FocusSelection == 10 && list.Items.Count == 25);
            Check(!FocusPending, "new visit completes its own shorter list and focus target");

            oldGate.TrySetResult();
            await Until(() => handler.ArtistPagesCompleted == handler.ArtistPagesStarted);
            await Until(() => Get<IDictionary>("_listPopulations").Count == 0);
            await Task.Delay(50);
            Check(list.Items.Count == 25 && FocusSelection == 10 && list.FocusedItem?.Index == 10,
                "late HTTP result cannot replace the new same-source list or focus");
            Check(handler.ArtistPages.IsEmpty, "all staged artist pages were requested exactly once");
        }
        finally { oldGate.TrySetResult(); }
    }
}
