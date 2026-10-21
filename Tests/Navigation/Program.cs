using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json.Linq;
using YTPlayer;
using YTPlayer.Core;
using YTPlayer.Core.Recognition;
using YTPlayer.Models;
using YTPlayer.Utils;

internal static class Program
{
    private static readonly BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    private static int passed;
    private static MainForm form = null!;
    private static ListView list = null!;
    private static FixtureHandler handler = null!;
    private static readonly List<string> dialogs = new();

    [STAThread]
    private static int Main()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "test-data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("YTPLAYER_ROOT", root);
        Control.CheckForIllegalCrossThreadCalls = true;
        int exitCode = 1;
        using var context = new ApplicationContext();
        using var client = new NeteaseApiClient();
        handler = new FixtureHandler();
        foreach (var field in typeof(NeteaseApiClient).GetFields(Instance).Where(f => f.FieldType == typeof(HttpClient)))
        {
            ((HttpClient?)field.GetValue(client))?.Dispose();
            field.SetValue(client, new HttpClient(handler, disposeHandler: false));
        }
        using var dialogWatch = new System.Windows.Forms.Timer { Interval = 50 };
        dialogWatch.Tick += (_, _) => EnumThreadWindows(GetCurrentThreadId(), (window, _) =>
        {
            var name = new StringBuilder(128);
            GetClassName(window, name, name.Capacity);
            if (name.ToString() == "#32770")
            {
                GetWindowText(window, name, name.Capacity);
                dialogs.Add(name.ToString());
                SendMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
            }
            return true;
        }, IntPtr.Zero);
        try
        {
            form = new MainForm(initializeRuntime: false)
            {
                ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
                Location = new System.Drawing.Point(-32000, -32000)
            };
            Set("_apiClient", client);
            Set("_config", new ConfigModel());
            Set("_appStartTime", DateTime.Now.AddMinutes(-1));
            list = Get<ListView>("resultListView");
            form.Show();
            dialogWatch.Start();
            form.BeginInvoke(async () =>
            {
                try
                {
                    Console.WriteLine("Navigation state and route identity matrix");
                    StateMatrix();
                    Console.WriteLine("Recognition / album / Backspace / F5 integration");
                    await RecognitionNavigationAsync();
                    Console.WriteLine("Category and source navigation integration");
                    await CategoryNavigationAsync();
                    Console.WriteLine("All search types and mixed URL results");
                    await SearchNavigationAsync();
                    Console.WriteLine("Cancellation, pending Backspace and history changes");
                    await ConcurrentNavigationAsync();
                    Console.WriteLine("Home shortcut, menu, focus and embedded help");
                    await HomeShortcutAsync();
                    Check(dialogs.Count == 0, "no unexpected dialogs: " + string.Join(", ", dialogs));
                    Check(handler.Unknown.Count == 0, "all HTTP requests use registered fixtures: " + string.Join(", ", handler.Unknown));
                    Console.WriteLine($"PASS: {passed} assertions; real WinForms controls, mocked HTTP, no account/audio/live network.");
                    exitCode = 0;
                }
                catch (Exception e) { Console.Error.WriteLine(e); }
                finally
                {
                    Call("CancelViewContentOperation");
                    Call("CancelNavigationOperation");
                    dialogWatch.Stop();
                    form.Dispose();
                    context.ExitThread();
                }
            });
            Application.Run(context);
        }
        finally { Directory.Delete(root, recursive: true); }
        return exitCode;
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        passed++;
    }
    private static T Get<T>(string name) => (T)typeof(MainForm).GetField(name, Instance)!.GetValue(form)!;
    private static void Set(string name, object? value) => typeof(MainForm).GetField(name, Instance)!.SetValue(form, value);
    private static object? Invoke(MethodInfo method, object? target, object?[] args)
    {
        var parameters = method.GetParameters();
        return method.Invoke(target, parameters.Select((p, i) => i < args.Length ? args[i] : p.DefaultValue).ToArray());
    }
    private static object? Call(string name, params object?[] args) => Invoke(typeof(MainForm).GetMethod(name, Instance)!, form, args);
    private static T StaticCall<T>(string name, params object?[] args) => (T)Invoke(typeof(MainForm).GetMethod(name, Static)!, null, args)!;
    private static Task Run(string name, params object?[] args) => ((Task)Call(name, args)!).WaitAsync(TimeSpan.FromSeconds(15));
    private static async Task<bool> RunBool(string name, params object?[] args) => await ((Task<bool>)Call(name, args)!).WaitAsync(TimeSpan.FromSeconds(15));
    private static MainForm.NavigationHistoryItem State() => (MainForm.NavigationHistoryItem)Call("CreateCurrentState")!;
    private static Stack<MainForm.NavigationHistoryItem> History => Get<Stack<MainForm.NavigationHistoryItem>>("_navigationHistory");
    private static string Source => Get<string>("_currentViewSource");
    private static void Context(string source) => Call("SetViewContext", source, "测试页面");
    private static void Select(int index)
    {
        Call("EnsureListSelectionWithoutFocus", index);
        list.Focus();
    }
    private static List<SongInfo> Songs() => Enumerable.Range(1, 3).Select(i => new SongInfo
    { Id = (100 + i).ToString(), Name = "识曲测试 " + i, Artist = "测试歌手", Album = "测试专辑", AlbumId = "42", Duration = 180 }).ToList();
    private static void ShowSongs(string source = "album:42") => Call("DisplaySongs", Songs(), false, false, 1, false, source, "测试页面", true);
    private static async Task Back()
    {
        Set("_lastBackTime", DateTime.MinValue);
        await Run("GoBackAsync");
    }

    private static void StateMatrix()
    {
        ShowSongs();
        Select(2);
        string[] categories = {
            "user_liked_songs", "user_playlists", "user_albums", "user_podcasts", "user_cloud",
            "recent_listened", "recent_play", "recent_playlists", "recent_albums", "recent_podcasts",
            "daily_recommend", "daily_recommend_songs", "daily_recommend_playlists", "personalized",
            "personalized_playlists", "personalized_newsongs", "personalized_newsong", "personal_fm",
            "toplist:offset0", "toplist:offset100", "highquality_playlists", "highquality_playlists:offset50",
            "new_songs", "new_songs_all:offset0", "new_songs_chinese:offset100", "new_songs_western:offset0",
            "new_songs_japan:offset0", "new_songs_korea:offset0", "playlist_category", "playlist_cat_华语:offset50",
            "podcast_categories", "podcast_cat_3:offset50", "new_albums", "listen-match", "listen-match:legacy"
        };
        var routes = categories.Select(s => (s, "category")).Concat(new[] {
            ("homepage", "homepage"), ("playlist:11", "playlist"), ("album:42", "album"),
            ("search:song:测试:page1", "search"), ("search:playlist:测试:page2", "search"),
            ("search:album:测试:page1", "search"), ("search:artist:测试:page1", "search"),
            ("search:podcast:测试:page1", "search"), ("artist_entries:12", "artist_entries"),
            ("artist_songs_top:12", "artist_top"), ("artist_top:12", "artist_top"),
            ("artist_songs:12:ordertime:offset100", "artist_songs"),
            ("artist_albums:12:orderoldest:offset100", "artist_albums"), ("artist_favorites", "artist_favorites"),
            ("artist_category_types", "artist_category_types"), ("artist_category_type:2", "artist_category_type"),
            ("artist_category_list:2:16:offset100", "artist_category_list"),
            ("new_album_category_periods", "new_album_category_periods"),
            ("new_album_category_period:1", "new_album_category_period"),
            ("new_album_category_list:1:8:offset50", "new_album_category_list"),
            ("podcast:8:offset0:asc1", "podcast"), ("url:song:101", "url_song"),
            ("url:mixed:Song:101;Album:42", "url_mixed")
        });
        foreach (var (source, type) in routes)
        {
            Context(source);
            var state = State();
            Check(state.PageType == type, source + " capture page type");
            Check(state.SelectedIndex == 2 && state.SelectedDataIndex == 2, source + " captures selected row/data");
            Check((bool)Call("IsNavigationStateApplied", state)!, source + " matches own page");
            Context("album:999");
            Check(!(bool)Call("IsNavigationStateApplied", state)!, source + " rejects unrelated page");
            Context(source);
            Check(StaticCall<bool>("IsSameNavigationState", state, State()), source + " duplicate state comparison");
        }
        Context("artist_songs:12:ordertime:offset100");
        var artistSongs = State();
        Check(artistSongs.ArtistId == 12 && artistSongs.ArtistOrder == "time" && artistSongs.ArtistOffset == 100, "artist song parameters survive capture");
        Context("artist_songs:12:orderhot:offset100");
        Check(!StaticCall<bool>("IsSameNavigationState", artistSongs, State()), "different artist sort not merged");
        Context("artist_songs:12:offset100");
        Check(!(bool)Call("IsNavigationStateApplied", artistSongs)!, "legacy default song order cannot satisfy time order");
        Context("artist_albums:12:orderoldest:offset100");
        var albums = State();
        Check(albums.ArtistAlbumSort == "oldest" && albums.ArtistOffset == 100, "album sort and offset captured");
        Context("artist_albums:12:offset100");
        Check(!(bool)Call("IsNavigationStateApplied", albums)!, "legacy latest album order cannot satisfy oldest order");
        Context("podcast:8:offset50:asc1");
        var podcast = State();
        Check(podcast.PodcastRadioId == 8 && podcast.PodcastOffset == 50 && podcast.PodcastAscending, "podcast sort and offset captured");
        Context("podcast:8:offset50");
        Check(!StaticCall<bool>("IsSameNavigationState", podcast, State()), "different podcast sort not merged");
        Context("artist_entries:12");
        var entry = State();
        foreach (string wrong in new[] { "album:312", "artist_entries:312", "artist_songs_top:12" })
        {
            Context(wrong);
            Check(!(bool)Call("IsNavigationStateApplied", entry)!, "artist ID substring is not a matching route: " + wrong);
        }
        foreach (string keyword in new[] { "album:回忆", "artist:歌手", "playlist:歌单", "podcast:访谈", "song:歌名", "中文:page7:offset50" })
        {
            string source = $"search:song:{keyword}:page3";
            Context(source);
            var search = State();
            Check(search.SearchType == "歌曲" && search.SearchKeyword == keyword && search.CurrentPage == 3, "reserved tokens in song keyword: " + keyword);
            string persisted = (string)Call("NormalizePlaybackSourceForPersistence", source)!;
            Context(persisted);
            Check(State().SearchKeyword == keyword && State().SearchType == "歌曲", "playback persistence preserves search type/keyword");
        }
        Check(StaticCall<bool>("IsSameViewSourceForFocus", "search:旧歌:page1", "search:song:旧歌:page1"), "legacy song-search focus compatibility");
        Check(StaticCall<bool>("IsSameViewSourceForFocus", "listen-match:session", "listen-match"), "legacy recognition focus compatibility");
        Check(!StaticCall<bool>("IsListenMatchViewSource", "listen-match-other"), "recognition prefix boundary enforced");
        Context("listen-match");
        History.Clear();
        Call("SaveNavigationState");
        Select(1); Call("SaveNavigationState");
        Check(History.Count == 1 && History.Peek().SelectedIndex == 1, "duplicate history retains newest focus");
        Context("listen-match:legacy"); Call("SaveNavigationState");
        Check(History.Count == 1, "canonical recognition alias does not duplicate history");
        History.Clear();
        Call("DisplaySongs", new List<SongInfo>(), false, false, 1, false, "listen-match", "听歌识曲", true);
        Call("SaveNavigationState");
        Check(History.Count == 1 && History.Peek().CategoryId == "listen-match", "empty result remains navigable");
        Context("toplist"); var basePage = State(); Context("toplist:offset0");
        Check((bool)Call("IsNavigationStateApplied", basePage)!, "implicit and explicit first offset match");
        Context("toplist:offset100");
        Check(!(bool)Call("IsNavigationStateApplied", basePage)!, "different offsets do not match");
        History.Clear();
    }

    private static async Task RecognitionNavigationAsync()
    {
        Context("new_songs");
        await Run("LoadNewSongs");
        Select(3);
        await Run("ShowRecognitionResultsAsync", new SongRecognitionResult("fixture", Songs(), 3));
        Check(Source == "listen-match" && list.AccessibleName == "听歌识曲", "recognition canonical route and accessible list name");
        Select(2);
        await Run("OpenAlbum", new AlbumInfo { Id = "42", Name = "测试专辑" });
        Check(Source == "album:42" && History.Count == 2, "actual album entry saves recognition history");
        Set("_lastBackTime", DateTime.MinValue);
        var key = new KeyEventArgs(Keys.Back);
        Call("MainForm_KeyDown", list, key);
        await Until(() => !Get<bool>("_isNavigating") && Source == "listen-match");
        Check(key.Handled && key.SuppressKeyPress, "Backspace keyboard handler consumes key");
        Check(list.Items.Count == 3 && list.SelectedIndices.Contains(2) && list.FocusedItem?.Index == 2, "Backspace restores recognition results and selected/focused row");
        Check(list.AccessibleName == "听歌识曲" && History.Count == 1, "Backspace preserves accessible name and pops exactly once");
        await Run("RefreshCurrentViewAsync", false);
        Check(Source == "listen-match" && list.SelectedIndices.Contains(2) && History.Count == 1, "F5 refresh preserves recognition and history/focus");
        var legacy = new MainForm.NavigationHistoryItem { PageType = "category", ViewSource = "listen-match:old", CategoryId = "listen-match:old", ViewName = "听歌识曲", SelectedIndex = 1, SelectedDataIndex = 1 };
        ShowSongs();
        Check(await RunBool("RestoreNavigationStateAsync", legacy) && Source == "listen-match" && list.SelectedIndices.Contains(1), "old session route restores normalized context and focus");
        await Run("ShowRecognitionResultsAsync", new SongRecognitionResult("repeat", new[] { Songs()[0] }, 3));
        Check(History.Count == 1 && list.Items.Count == 3, "repeat recognition merges without self-history");
        await Back();
        Check(Source == "new_songs" && list.SelectedIndices.Contains(3) && History.Count == 0, "second Back returns to original category and focus");
        Check(dialogs.Count == 0, "recognition/album round-trip has no unknown-category dialog");
    }

    private static async Task CategoryNavigationAsync()
    {
        foreach (var (source, expected) in new[] {
            ("new_songs", "new_songs"), ("playlist_category", "playlist_category"),
            ("artist_categories", "artist_category_types"), ("artist_type_2", "artist_category_type:2"),
            ("new_album_categories", "new_album_category_periods"), ("new_album_period_1", "new_album_category_period:1"),
            ("podcast_categories", "podcast_categories"), ("podcast_cat_3:offset0", "podcast_cat_3:offset0"),
            ("personalized_newsong", "personalized_newsong"), ("listen-match", "listen-match")
        })
        {
            History.Clear();
            ShowSongs(); Select(1);
            Check(await RunBool("NavigateByViewSourceAsync", source, Songs()[0]), source + " source dispatch accepted");
            Check(Source == expected, source + " correct source target");
            Check(History.Count == 1 && History.Peek().ViewSource == "album:42", source + " source navigation saves once");
            var captured = State();
            ShowSongs("album:99");
            Check(await RunBool("RestoreNavigationStateAsync", captured), source + " return route accepted");
            Check(Source == expected && History.Count == 1, source + " restore preserves history");
        }
        Check(!await RunBool("TryLoadCategoryContentAsync", "not-a-category"), "unknown source declines without error dialog");
        Check(!await RunBool("TryLoadCategoryContentAsync", null, -1), "null category safely declines");
        History.Clear(); ShowSongs();
        Check(!await RunBool("NavigateByViewSourceAsync", "not-a-category", Songs()[0]) && History.Count == 0 && Source == "album:42",
            "unsupported source leaves current page and history intact");
        Check(dialogs.Count == 0, "category routes have no unexpected dialogs");
    }

    private static async Task ConcurrentNavigationAsync()
    {
        History.Clear();
        Context("podcast_categories"); Call("SaveNavigationState");
        ShowSongs();
        handler.BlockCategories = true;
        handler.CategoriesEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Set("_lastBackTime", DateTime.MinValue);
        Task back = Run("GoBackAsync");
        await handler.CategoriesEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        handler.BlockCategories = false;
        await Run("GoBackAsync"); // queues/cancels in-flight restore even inside 300 ms
        await back;
        Check(Source == "podcast_categories" && History.Count == 0, "pending Back completes on UI thread and removes only restored entry");
        Check(!Get<bool>("_isNavigating") && !Get<bool>("_pendingBackNavigation"), "navigation flags reset");
        History.Clear();
        Context("podcast_categories"); Call("SaveNavigationState"); ShowSongs();
        handler.BlockCategories = true;
        handler.CategoriesEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Set("_lastBackTime", DateTime.MinValue);
        back = Run("GoBackAsync");
        await handler.CategoriesEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Call("CancelViewContentOperation");
        await back;
        Check(History.Count == 1, "cancelled restore retains history entry despite loading context");
        handler.BlockCategories = false;
        await Back();
        Check(History.Count == 0, "cancelled restore can be retried");
        Context("podcast_categories"); Call("SaveNavigationState"); ShowSongs();
        handler.BlockCategories = true;
        handler.CategoriesEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Set("_lastBackTime", DateTime.MinValue);
        back = Run("GoBackAsync");
        await handler.CategoriesEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        History.Clear();
        Context("new_songs"); Call("SaveNavigationState");
        Call("CancelViewContentOperation");
        await back;
        Check(History.Count == 1 && History.Peek().ViewSource == "new_songs", "late restore never pops newer history");
        handler.BlockCategories = false;
    }

    private static async Task SearchNavigationAsync()
    {
        foreach (string type in new[] { "歌曲", "歌单", "专辑", "歌手", "播客" })
        {
            History.Clear(); ShowSongs();
            await Run("LoadSearchResults", "album:测试:page7", type, 1, false, 0);
            await Until(() => !Get<bool>("_listLoadingPlaceholderActive") && list.Items.Count > 0);
            var state = State();
            Check(state.SearchType == type && state.SearchKeyword == "album:测试:page7", type + " real search retains keyword and type");
            ShowSongs();
            Check(await RunBool("RestoreNavigationStateAsync", state), type + " search restores successfully");
            await Until(() => !Get<bool>("_listLoadingPlaceholderActive") && list.Items.Count > 0);
            Check(State().SearchType == type && list.SelectedIndices.Contains(0), type + " search restores selection and type");
            Check(History.Count == 1, type + " search restore never pushes history");
        }

        var matches = new List<NeteaseUrlMatch> {
            new(NeteaseUrlType.Song, "101", "https://music.163.com/song?id=101"),
            new(NeteaseUrlType.Album, "42", "https://music.163.com/album?id=42")
        };
        string key = (string)Call("BuildMixedQueryKey", matches)!;
        foreach (string candidate in new[] { key, "1:101;3:42", "song:101;album:42" })
        {
            object?[] args = { candidate, null };
            bool parsed = (bool)typeof(MainForm).GetMethod("TryParseMixedQueryKey", Instance)!.Invoke(form, args)!;
            var result = (List<NeteaseUrlMatch>)args[1]!;
            Check(parsed && result.Count == 2 && result[1].Type == NeteaseUrlType.Album, "mixed URL key round trip: " + candidate);
        }
        foreach (string candidate in new[] { "999:101", "Unknown:101", "Song:", "Album:-1", "not-a-key" })
        {
            object?[] args = { candidate, null };
            Check(!(bool)typeof(MainForm).GetMethod("TryParseMixedQueryKey", Instance)!.Invoke(form, args)!, "malformed mixed URL key rejected: " + candidate);
        }
        History.Clear(); ShowSongs();
        await Run("HandleMultipleNeteaseUrlSearchAsync", matches, CancellationToken.None);
        Check(Source == "url:mixed:" + key && list.Items.Count == 2, "real mixed link result renders both entity types");
        Select(1);
        await Run("OpenAlbum", new AlbumInfo { Id = "42", Name = "测试专辑" });
        await Back();
        Check(Source == "url:mixed:" + key && list.Items.Count == 2 && list.SelectedIndices.Contains(1), "album back to mixed URL results and focus");
        await Run("RefreshCurrentViewAsync", false);
        Check(Source == "url:mixed:" + key && History.Count == 1 && list.AccessibleName == "结果", "mixed URL F5 preserves history and accessible name");
    }

    private static async Task HomeShortcutAsync()
    {
        var homeMenu = Get<ToolStripMenuItem>("homeMenuItem");
        Check(homeMenu.ShortcutKeys == (Keys.Control | Keys.Shift | Keys.H) && homeMenu.ShowShortcutKeys,
            "home menu exposes Ctrl+Shift+H");
        Check(homeMenu.Text == "主页(&H)", "home menu retains its name and mnemonic");
        using (var help = new YTPlayer.Forms.KeyboardShortcutsDialog())
        {
            var text = help.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<RichTextBox>().Single().Text;
            Check(text.Contains("Ctrl + Shift + H") && text.Contains("返回主页"), "embedded shortcut help includes home action");
        }

        int clicks = 0;
        EventHandler onClick = (_, _) => clicks++;
        homeMenu.Click += onClick;
        try
        {
            var search = Get<TextBox>("searchTextBox");
            search.Text = "保留搜索内容";
            foreach (Control target in new Control[] { list, search, Get<ComboBox>("searchTypeComboBox") })
            {
                History.Clear();
                ShowSongs();
                Call("SaveNavigationState");
                Select(2);
                target.Focus();
                int previousClicks = clicks;
                foreach (Keys unrelated in new[] { Keys.H, Keys.Control | Keys.H, Keys.Control | Keys.Alt | Keys.Shift | Keys.H })
                    DispatchShortcut(target, unrelated);
                Check(clicks == previousClicks && Source == "album:42", target.Name + " other key combinations do not return home");
                Check(DispatchShortcut(target, Keys.Control | Keys.Shift | Keys.H), target.Name + " routes home shortcut through menu");
                await Until(() => Source == "homepage" && list.Focused && list.FocusedItem?.Index == 0);
                Check(clicks == previousClicks + 1 && History.Count == 0, target.Name + " triggers home once and clears history like menu click");
                Check(list.AccessibleName == "主页" && list.SelectedIndices.Contains(0), target.Name + " restores accessible home list and selection");
                Check(search.Text == "保留搜索内容", target.Name + " home shortcut preserves search text");
            }

            int beforeRepeat = clicks;
            Check(DispatchShortcut(list, Keys.Control | Keys.Shift | Keys.H), "home shortcut works while already on home");
            await Until(() => Source == "homepage" && list.FocusedItem?.Index == 0);
            Check(clicks == beforeRepeat + 1 && History.Count == 0, "repeated home shortcut keeps history empty");

            ShowSongs();
            Call("SaveNavigationState");
            homeMenu.PerformClick();
            await Until(() => Source == "homepage" && list.Focused && list.FocusedItem?.Index == 0);
            Check(History.Count == 0 && list.AccessibleName == "主页", "menu click retains same home behavior");
        }
        finally { homeMenu.Click -= onClick; }
    }

    private static bool DispatchShortcut(Control target, Keys keys)
    {
        var message = Message.Create(target.Handle, 0x0100, (IntPtr)(keys & Keys.KeyCode), IntPtr.Zero);
        return (bool)target.GetType().GetMethod("ProcessCmdKey", Instance)!.Invoke(target, new object[] { message, keys })!;
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        internal bool BlockCategories;
        internal TaskCompletionSource CategoriesEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly System.Collections.Concurrent.ConcurrentBag<string> Unknown = new();
        private static readonly string SongJson = """{"id":101,"name":"测试歌曲","ar":[{"id":12,"name":"测试歌手"}],"al":{"id":42,"name":"测试专辑"},"dt":180000,"privilege":{"id":101,"st":0,"pl":320000,"fee":0}}""";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string path = request.RequestUri!.AbsolutePath;
            string body;
            if (path.Contains("djradio/category/get"))
            {
                CategoriesEntered.TrySetResult();
                if (BlockCategories) await Task.Delay(Timeout.Infinite, token);
                body = """{"code":200,"categories":[{"id":3,"name":"测试播客分类"}]}""";
            }
            else if (path.Contains("djradio/hot")) body = """{"code":200,"djRadios":[{"id":8,"name":"测试播客","categoryId":3,"programCount":1}],"count":1,"hasMore":false}""";
            else if (path.Contains("artist/head/info/get")) body = """{"code":200,"data":{"artist":{"id":12,"name":"测试歌手","musicSize":1,"albumSize":1}}}""";
            else if (path.Contains("artist/introduction")) body = """{"code":200,"briefDesc":"测试简介","introduction":[]}""";
            else if (path.EndsWith("/toplist")) body = """{"code":200,"list":[]}""";
            else if (path.Contains("playlist/highquality/list")) body = """{"code":200,"playlists":[],"more":false,"total":0}""";
            else if (path.Contains("discovery/newAlbum")) body = """{"code":200,"albums":[]}""";
            else if (path.Contains("search")) body = "{\"code\":200,\"result\":{\"songs\":[" + SongJson + "],\"songCount\":1,\"playlists\":[{\"id\":11,\"name\":\"测试歌单\"}],\"playlistCount\":1,\"albums\":[{\"id\":42,\"name\":\"测试专辑\"}],\"albumCount\":1,\"artists\":[{\"id\":12,\"name\":\"测试歌手\"}],\"artistCount\":1,\"djRadios\":[{\"id\":8,\"name\":\"测试播客\"}],\"djRadiosCount\":1}}";
            else if (path.Contains("album")) body = "{\"code\":200,\"album\":{\"id\":42,\"name\":\"测试专辑\",\"size\":1},\"songs\":[" + SongJson + "]}";
            else if (path.Contains("personalized/newsong")) body = "{\"code\":200,\"result\":[{\"song\":" + SongJson + "}]}";
            else if (path.Contains("song/detail")) body = "{\"code\":200,\"songs\":[" + SongJson + "],\"privileges\":[{\"id\":101,\"st\":0,\"pl\":320000}]}";
            else { Unknown.Add(path); body = "{\"code\":200,\"data\":[],\"result\":[]}"; }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private delegate bool WindowCallback(IntPtr window, IntPtr param);
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint threadId, WindowCallback callback, IntPtr param);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
