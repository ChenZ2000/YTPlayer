using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using YTPlayer;
using YTPlayer.Core;
using YTPlayer.Core.Recognition;
using YTPlayer.Core.Playback;
using YTPlayer.Models;
using YTPlayer.Models.Auth;
using YTPlayer.Utils;

internal static partial class Program
{
    private static readonly BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    private static int passed;
    private static MainForm form = null!;
    private static ListView list = null!;
    private static FixtureHandler handler = null!;
    private static readonly List<string> dialogs = new();
    private static DialogResult? recentDeleteConfirmation;
    private static int recentDeleteConfirmationCount;
    private static bool expectRecentDeleteError;
    private static int recentDeleteErrorCount;

    [STAThread]
    private static int Main(string[] args)
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
        dialogWatch.Tick += (_, _) =>
        {
            foreach (var dialog in Application.OpenForms.Cast<Form>().Where(window => window.GetType().Name == "ThemedMessageBoxForm" || window.Text == "删除评论").ToArray())
            {
                if ((dialog.Text == "确认删除" || dialog.Text == "删除评论") && recentDeleteConfirmation.HasValue)
                {
                    recentDeleteConfirmationCount++;
                    dialog.DialogResult = recentDeleteConfirmation.Value;
                }
                else if ((dialog.Text == "错误" || dialog.Text == "失败" || dialog.Text == "提示") && expectRecentDeleteError)
                {
                    recentDeleteErrorCount++;
                    dialog.DialogResult = DialogResult.OK;
                }
                else
                {
                    dialogs.Add(dialog.Text);
                    dialog.DialogResult = DialogResult.Cancel;
                }
                dialog.Close();
            }
            EnumThreadWindows(GetCurrentThreadId(), (window, _) =>
        {
            var name = new StringBuilder(128);
            GetClassName(window, name, name.Capacity);
            if (name.ToString() == "#32770")
            {
                GetWindowText(window, name, name.Capacity);
                if (name.ToString() == "确认删除" && recentDeleteConfirmation.HasValue)
                {
                    recentDeleteConfirmationCount++;
                    SendMessage(window, 0x0111, (IntPtr)recentDeleteConfirmation.Value, IntPtr.Zero);
                    return true;
                }
                if (name.ToString() == "错误" && expectRecentDeleteError)
                {
                    recentDeleteErrorCount++;
                    SendMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
                    return true;
                }
                dialogs.Add(name.ToString());
                SendMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
            }
            return true;
            }, IntPtr.Zero);
        };
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
                    if (args.Contains("--fm-modes", StringComparer.Ordinal))
                    {
                        await PersonalFmModesAsync();
                    }
                    else if (args.Contains("--focus-restore", StringComparer.Ordinal))
                    {
                        await NavigationFocusRestoreAsync();
                    }
                    else
                    {
                        if (!args.Contains("--recent-delete", StringComparer.Ordinal))
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
                            Console.WriteLine("Not-interested shortcut, focus guards and recommendation updates");
                            await NotInterestedShortcutAsync();
                            await NotInterestedProtocolAsync();
                            await PersonalFmModesAsync();
                        }
                        Console.WriteLine("Recent-play deletion: official protocol, confirmation, cache and failure handling");
                        await RecentPlayDeleteProtocolAsync();
                        await RecentPlayDeleteUiAsync();
                        Console.WriteLine("Removal focus, Delete routing, comments and transfer lists");
                        await RemovalIntegrationAsync();
                        Console.WriteLine("Non-song history deletion and playlist deletion scope");
                        await RecentResourceRemovalAsync();
                        await NavigationFocusRestoreAsync();
                    }
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
            var text = help.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<YTPlayer.Forms.AccessibleDocumentView>().Single().Document!.PlainText;
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
                await Until(() => Source == "homepage" && !Get<bool>("_listLoadingPlaceholderActive") && list.Focused && list.FocusedItem?.Index == 0);
                Check(clicks == previousClicks + 1 && History.Count == 0, target.Name + " triggers home once and clears history like menu click");
                Check(list.AccessibleName == "主页" && list.SelectedIndices.Contains(0), target.Name + " restores accessible home list and selection");
                Check(search.Text == "保留搜索内容", target.Name + " home shortcut preserves search text");
            }

            int beforeRepeat = clicks;
            Check(DispatchShortcut(list, Keys.Control | Keys.Shift | Keys.H), "home shortcut works while already on home");
            await Until(() => Source == "homepage" && !Get<bool>("_listLoadingPlaceholderActive") && list.FocusedItem?.Index == 0);
            Check(clicks == beforeRepeat + 1 && History.Count == 0, "repeated home shortcut keeps history empty");

            ShowSongs();
            Call("SaveNavigationState");
            homeMenu.PerformClick();
            await Until(() => Source == "homepage" && !Get<bool>("_listLoadingPlaceholderActive") && list.Focused && list.FocusedItem?.Index == 0);
            Check(History.Count == 0 && list.AccessibleName == "主页", "menu click retains same home behavior");
        }
        finally { homeMenu.Click -= onClick; }
    }

    private static async Task NotInterestedShortcutAsync()
    {
        const Keys shortcut = Keys.Control | Keys.D;
        var originalAccount = Get<AccountState>("_accountState");
        var menuItem = Get<ToolStripMenuItem>("dislikeSongMenuItem");
        var originalContextOwner = Get<ContextMenuStrip>("songContextMenu").OwnerItem;
        var originalTag = menuItem.Tag;
        try
        {
            Check(menuItem.ShortcutKeyDisplayString == "Ctrl+D" && menuItem.ShowShortcutKeys,
                "not-interested menu exposes Ctrl+D");
            Check(Control.IsMnemonic('D', menuItem.Text) && !Control.IsMnemonic('I', menuItem.Text), "not-interested access key is D");
            Call("UpdateDownloadMenuShortcutDisplay", false);
            Check(!menuItem.ShowShortcutKeys && string.IsNullOrEmpty(menuItem.ShortcutKeyDisplayString),
                "current-playing menu does not advertise a list shortcut");
            Call("UpdateDownloadMenuShortcutDisplay", true);
            Check(menuItem.ShowShortcutKeys && menuItem.ShortcutKeyDisplayString == "Ctrl+D",
                "returning to list menu restores shortcut display");
            using (var help = new YTPlayer.Forms.KeyboardShortcutsDialog())
            {
                var text = help.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<YTPlayer.Forms.AccessibleDocumentView>().Single().Document!.PlainText;
                Check(text.Contains("Ctrl + D") && text.Contains("不感兴趣"), "embedded help includes not-interested shortcut");
            }

            ShowSongs("daily_recommend_songs");
            Select(1);
            Set("_accountState", new AccountState { IsLoggedIn = false });
            Check(!DispatchShortcut(list, shortcut), "logged-out shortcut is ignored");
            Set("_accountState", new AccountState { IsLoggedIn = true, UserId = "42" });
            var search = Get<TextBox>("searchTextBox");
            search.Focus();
            Check(!DispatchShortcut(search, shortcut), "search focus does not act on the previous list selection");
            list.Focus();
            foreach (var keys in new[] { Keys.D, Keys.Control | Keys.Shift | Keys.D, Keys.Control | Keys.Alt | Keys.D })
                Check(!DispatchShortcut(list, keys), "other key combinations do not mark a song: " + keys);

            foreach (object tag in new object[] {
                new AlbumInfo { Id = "42" }, new PlaylistInfo { Id = "11" },
                new PodcastEpisodeInfo { ProgramId = 8, Song = new SongInfo { Id = "108", IsPodcastEpisode = true } },
                new SongInfo { Id = "109", IsPodcastEpisode = true },
                new SongInfo { Id = "777", IsCloudSong = true, CloudSongId = "777" }, new SongInfo()
            })
            {
                list.Items.Clear();
                list.Items.Add(new ListViewItem("不支持的项目") { Tag = tag });
                Select(0);
                Check(!DispatchShortcut(list, shortcut), "unsupported item is ignored: " + tag.GetType().Name);
            }
            list.Items.Clear();
            Check(!DispatchShortcut(list, shortcut), "empty list shortcut is ignored");
            Check(handler.DislikeRequests == 0, "ignored shortcuts never send a dislike request");

            foreach (string source in new[] { "daily_recommend_songs", "daily_recommend", "personalized_newsongs", "personalized_newsong", "personal_fm", "album:42" })
            {
                ShowSongs(source);
                Get<List<SongInfo>>("_currentSongs")[1].RecommendationAlgorithm = "fm-fixture-alg";
                if (source == "personal_fm") Call("ResetPersonalFmCache", Get<List<SongInfo>>("_currentSongs"), 1);
                Select(1);
                menuItem.Tag = Songs()[0]; // A previous menu target must not replace the focused song.
                int before = handler.DislikeRequests;
                Check(DispatchShortcut(list, shortcut), source + " handles Ctrl+D");
                await Until(() => Get<ToolStripStatusLabel>("toolStripStatusLabel1").Text?.StartsWith("已标记为不感兴趣", StringComparison.Ordinal) == true);
                Check(handler.DislikeRequests == before + 1, source + " sends exactly one dislike request");
                Check(handler.LastDislikePath == (source == "personal_fm" ? "/eapi/v1/radio/trash/add" : "/weapi/v2/discovery/recommend/dislike"), source + " uses its own feedback endpoint");
                if (source == "personal_fm")
                {
                    Check(handler.LastDislikeBody?.Value<string>("alg") == "fm-fixture-alg", "FM preserves recommendation algorithm");
                    Check(Get<List<SongInfo>>("_personalFmSongsCache").All(song => song.Id != "102"), "FM cache discards the rejected song");
                    Check(!(bool)Call("TryAppendPersonalFmSongToCache", Songs()[1], 0)!, "in-flight FM results cannot reinsert the discarded song");
                }
                var expectedIds = source == "album:42" ? new[] { "101", "102", "103" } : new[] { "101", "103" };
                var actualIds = Get<List<SongInfo>>("_currentSongs").Select(song => song.Id).ToArray();
                Check(actualIds.SequenceEqual(expectedIds),
                    source + " preserves existing menu behavior for the focused song; source=" + Source + "; ids=" + string.Join(",", actualIds));
                await Until(() => list.Focused && list.FocusedItem?.Index == 1);
                Check(list.Focused && list.FocusedItem?.Index == 1, source + " keeps list focus at the expected item");
            }

            ShowSongs("personal_fm");
            Select(1);
            var contextMenu = Get<ContextMenuStrip>("songContextMenu");
            contextMenu.Show(list, new System.Drawing.Point(8, 8));
            try
            {
                Check(menuItem.Available && contextMenu.Items.Cast<ToolStripItem>().Count(item => item.Available && Control.IsMnemonic('D', item.Text)) == 1,
                    "FM menu reserves D for not-interested without sibling mnemonic conflicts");
                int beforeMnemonic = handler.DislikeRequests;
                Check((bool)typeof(ToolStripDropDown).GetMethod("ProcessMnemonic", Instance)!.Invoke(contextMenu, new object[] { 'd' })!, "FM menu handles D mnemonic");
                await Until(() => handler.DislikeRequests == beforeMnemonic + 1 && Get<HashSet<string>>("_pendingSongDislikes").Count == 0);
                Check(handler.LastDislikePath == "/eapi/v1/radio/trash/add" && Get<List<SongInfo>>("_currentSongs").All(song => song.Id != "102"),
                    "D mnemonic uses FM feedback and removes the selected song");
            }
            finally { contextMenu.Close(); }

            ShowSongs("daily_recommend_songs");
            var playingFmSong = Songs()[1];
            playingFmSong.ViewSource = "personal_fm";
            playingFmSong.RecommendationAlgorithm = "playing-fm-alg";
            contextMenu.OwnerItem = Get<ToolStripMenuItem>("currentPlayingMenuItem");
            menuItem.Tag = playingFmSong;
            Set("_isCurrentPlayingMenuActive", true);
            Set("_currentPlayingMenuSong", playingFmSong);
            Call("songContextMenu_Closed", contextMenu, new ToolStripDropDownClosedEventArgs(ToolStripDropDownCloseReason.ItemClicked));
            int beforePlaying = handler.DislikeRequests;
            menuItem.PerformClick();
            await Until(() => handler.DislikeRequests == beforePlaying + 1 && Get<HashSet<string>>("_pendingSongDislikes").Count == 0);
            Check(handler.LastDislikePath == "/eapi/v1/radio/trash/add" && handler.LastDislikeBody?.Value<string>("alg") == "playing-fm-alg",
                "current-playing FM keeps its source even when the menu closes before Click on another page");
            Check(Get<List<SongInfo>>("_currentSongs").Count == 3, "current-playing feedback leaves unrelated visible page intact");

            ShowSongs("personal_fm");
            Call("ResetPersonalFmCache", Get<List<SongInfo>>("_currentSongs"), 1);
            Select(1);
            handler.DislikeGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int pendingBefore = handler.DislikeRequests;
            Check(DispatchShortcut(list, shortcut), "FM feedback starts before navigation");
            await Until(() => handler.DislikeRequests == pendingBefore + 1);
            DispatchShortcut(list, shortcut);
            Check(handler.DislikeRequests == pendingBefore + 1, "repeated shortcut does not duplicate an in-flight feedback");
            ShowSongs("daily_recommend_songs");
            handler.DislikeGate.SetResult();
            await Until(() => Get<HashSet<string>>("_pendingSongDislikes").Count == 0);
            Check(Get<List<SongInfo>>("_currentSongs").Count == 3, "late FM response does not remove a song from another page");
            Check(Get<List<SongInfo>>("_personalFmSongsCache").All(song => song.Id != "102"), "late FM response still updates the FM cache");
        }
        finally
        {
            handler.DislikeGate?.TrySetResult();
            handler.DislikeGate = null;
            Set("_accountState", originalAccount);
            menuItem.Tag = originalTag;
            Get<ContextMenuStrip>("songContextMenu").OwnerItem = originalContextOwner;
            Call("UpdateDownloadMenuShortcutDisplay", true);
        }
    }

    private static async Task NotInterestedProtocolAsync()
    {
        using var client = new NeteaseApiClient();
        client.SetCookieString("MUSIC_U=fixture; __csrf=csrf; os=osx; extra_session=a%2Fb");
        var sessionCookies = ((CookieContainer)typeof(NeteaseApiClient).GetField("_cookieContainer", Instance)!.GetValue(client)!).GetCookies(new Uri("https://interface3.music.163.com"));
        var fixture = new FixtureHandler();
        foreach (var field in typeof(NeteaseApiClient).GetFields(Instance).Where(f => f.FieldType == typeof(HttpClient)))
        {
            ((HttpClient?)field.GetValue(client))?.Dispose();
            field.SetValue(client, new HttpClient(fixture, disposeHandler: false));
        }
        var fmSongs = await client.GetPersonalFMAsync();
        Check(fmSongs.Count == 1 && fmSongs[0].RecommendationAlgorithm == "fm-response-alg", "FM feed parser retains alg");
        Check(await client.MarkPersonalFmSongNotInterestedAsync("101", fmSongs[0].RecommendationAlgorithm, 49640), "encrypted official-shaped FM response succeeds");
        var body = fixture.LastDislikeBody!;
        Check(fixture.LastDislikeSignaturePath == "/api/v1/radio/trash/add", "FM EAPI signs the API path rather than wire path");
        Check(body.Value<string>("songId") == "101" && body["time"]?.Type == JTokenType.String && body.Value<string>("time") == "49640", "FM song ID and milliseconds use official string fields");
        Check(body.Value<string>("alg") == "fm-response-alg" && body.Value<string>("e_r") == "true", "FM algorithm and encrypted-response flag match runtime contract");
        Check(body["header"]?.Value<string>("extra_session") == "a%2Fb" && fixture.LastDislikeCookie.Contains("extra_session=a%2Fb"), "FM preserves full session cookies without double encoding");
        Check(body["header"]?.Value<string>("os") == sessionCookies["os"]?.Value, "FM preserves the active session dialect");
        Check(await client.MarkPersonalFmSongNotInterestedAsync("101", playedMilliseconds: -1), "legacy song without algorithm remains supported");
        Check(fixture.LastDislikeBody?.Value<string>("alg") == "RT" && fixture.LastDislikeBody?.Value<string>("time") == "0", "legacy algorithm fallback and nonnegative duration");

        int before = fixture.DislikeRequests;
        fixture.DislikeCode = 432;
        try { await client.MarkPersonalFmSongNotInterestedAsync("101"); throw new Exception("Business rejection accepted"); }
        catch (InvalidOperationException e) { Check(e.Message.Contains("432"), "business rejection retains server code"); }
        Check(fixture.DislikeRequests == before + 1, "business rejection is not retried");
        fixture.DislikeCode = 200;
        fixture.FailDislikeTransport = true;
        before = fixture.DislikeRequests;
        try { await client.MarkPersonalFmSongNotInterestedAsync("101"); throw new Exception("Transport failure accepted"); }
        catch (HttpRequestException) { Check(true, "FM transport error propagates"); }
        Check(fixture.DislikeRequests == before + 1, "FM mutation has no implicit transport retries");
        fixture.FailDislikeTransport = false;
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        before = fixture.DislikeRequests;
        try { await client.MarkPersonalFmSongNotInterestedAsync("101", cancellationToken: canceled.Token); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { Check(fixture.DislikeRequests == before, "canceled FM feedback sends no packet"); }

        var report = new PlaybackReportContext("101", "", "", new PlaybackSourceContext("list", ""), 180, false, "song");
        Set("_activePlaybackReport", report);
        foreach (var (seconds, expected) in new[] { (49.64, 49640), (-1.0, 0), (double.NaN, 0), (double.PositiveInfinity, 0), (double.MaxValue, int.MaxValue) })
        {
            report.PlayedSeconds = seconds;
            Check((int)Call("GetSongDislikePlayedMilliseconds", "101")! == expected, "FM duration conversion handles " + seconds);
        }
        Check((int)Call("GetSongDislikePlayedMilliseconds", "102")! == 0, "another song cannot inherit listening time");
        Set("_activePlaybackReport", null);
        Check(fixture.Unknown.Count == 0, "feedback protocol tests use only registered mock endpoints");
    }

    private static async Task RecentPlayDeleteProtocolAsync()
    {
        using var client = new NeteaseApiClient();
        client.SetCookieString("MUSIC_U=recent-fixture; __csrf=csrf; extra_session=a%2Fb");
        var fixture = new FixtureHandler();
        foreach (var field in typeof(NeteaseApiClient).GetFields(Instance).Where(f => f.FieldType == typeof(HttpClient)))
        {
            ((HttpClient?)field.GetValue(client))?.Dispose();
            field.SetValue(client, new HttpClient(fixture, disposeHandler: false));
        }
        Check(await client.DeleteRecentPlayedSongAsync("2147483650"), "recent delete accepts encrypted success response");
        Check(fixture.RecentReadRequests == 1 && fixture.RecentDeleteRequests == 1, "recent deletion reads one cursor and sends one mutation");
        Check(fixture.LastRecentDeleteSignature == "/api/play-record/batch/delete", "recent deletion signs the captured official API path");
        var body = fixture.LastRecentDeleteBody!;
        Check(body.Value<string>("type") == "SONG" && body["resourceIds"]?.Type == JTokenType.String &&
            JArray.Parse(body.Value<string>("resourceIds")!).Single().Value<long>() == 2147483650L,
            "recent deletion uses uppercase SONG and a JSON-encoded numeric ID array without 32-bit overflow");
        Check(body["lastResourceId"]?.Type == JTokenType.Integer && body.Value<long>("lastResourceId") == 3000000003L &&
            body["lastPlayTime"]?.Type == JTokenType.Integer && body.Value<long>("lastPlayTime") == 1790000000123L,
            "recent deletion uses the raw list tail cursor and millisecond timestamp as 64-bit numbers");
        Check(body.Value<string>("e_r") == "true" && body["header"]?.Value<string>("extra_session") == "a%2Fb" &&
            fixture.LastRecentDeleteCookie.Contains("extra_session=a%2Fb"), "recent deletion keeps the session and escaping intact");
        var sessionKeys = ((CookieContainer)typeof(NeteaseApiClient).GetField("_cookieContainer", Instance)!.GetValue(client)!)
            .GetCookies(new Uri("https://interface3.music.163.com")).Cast<Cookie>().Select(cookie => cookie.Name);
        Check(sessionKeys.All(key => JToken.DeepEquals(body["header"]?[key], fixture.LastRecentReadBody?["header"]?[key])) &&
            fixture.LastRecentDeleteCookie == fixture.LastRecentReadCookie, "recent list and mutation share one session snapshot while request IDs remain independent");
        Check(!new[] { "id", "ids", "songId", "songIds", "resourceType" }.Any(key => body.ContainsKey(key)), "recent deletion no longer sends speculative parameter aliases");

        int before = fixture.RecentDeleteRequests;
        fixture.RecentDeleteCode = 400;
        try { await client.DeleteRecentPlayedSongAsync("102"); throw new Exception("Recent business rejection accepted"); }
        catch (InvalidOperationException e) { Check(e.Message.Contains("400") && e.Message.Contains("fixture rejection"), "recent deletion preserves server reason and code"); }
        Check(fixture.RecentDeleteRequests == before + 1, "recent rejection does not probe alternative endpoints");
        fixture.RecentDeleteCode = 200;
        fixture.FailRecentDeleteTransport = true;
        before = fixture.RecentDeleteRequests;
        try { await client.DeleteRecentPlayedSongAsync("102"); throw new Exception("Recent transport failure accepted"); }
        catch (HttpRequestException) { Check(fixture.RecentDeleteRequests == before + 1, "recent deletion never retries an ambiguous mutation"); }
        fixture.FailRecentDeleteTransport = false;
        before = fixture.RecentDeleteRequests;
        fixture.RecentReadCode = 301;
        try { await client.DeleteRecentPlayedSongAsync("102"); throw new Exception("Failed cursor read accepted"); }
        catch (InvalidOperationException e) { Check(e.Message.Contains("301") && fixture.RecentDeleteRequests == before, "failed list read prevents mutation"); }
        fixture.RecentReadCode = 200;
        fixture.InvalidRecentList = true;
        try { await client.DeleteRecentPlayedSongAsync("102"); throw new Exception("Malformed list accepted"); }
        catch (InvalidOperationException) { Check(fixture.RecentDeleteRequests == before, "missing list is not treated as successful deletion"); }
        fixture.InvalidRecentList = false;
        var savedRecords = (JArray)fixture.RecentRecords.DeepClone();
        ((JObject)fixture.RecentRecords.Last!).Remove("playTime");
        try { await client.DeleteRecentPlayedSongAsync("102"); throw new Exception("Missing cursor accepted"); }
        catch (InvalidOperationException) { Check(fixture.RecentDeleteRequests == before, "missing cursor never causes a guessed or unbounded delete"); }
        fixture.RecentRecords = savedRecords;

        int readsBefore = fixture.RecentReadRequests;
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try { await client.DeleteRecentPlayedSongAsync("102", canceled.Token); throw new Exception("Recent cancellation ignored"); }
        catch (OperationCanceledException) { Check(fixture.RecentReadRequests == readsBefore, "pre-canceled delete sends no request"); }
        foreach (string invalid in new[] { "", "0", "-1", "101,102", "[]", "9223372036854775808" })
            Check(!await client.DeleteRecentPlayedSongAsync(invalid), "invalid resource ID is rejected: " + invalid);
        Check(fixture.RecentReadRequests == readsBefore, "invalid IDs cannot reach the server");

        fixture.RecentReadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelDuringRead = new CancellationTokenSource();
        Task<bool> pending = client.DeleteRecentPlayedSongAsync("102", cancelDuringRead.Token);
        await Until(() => fixture.RecentReadRequests == readsBefore + 1);
        cancelDuringRead.Cancel();
        try { await pending; throw new Exception("Mid-read cancellation ignored"); }
        catch (OperationCanceledException) { Check(fixture.RecentDeleteRequests == before, "cancellation during cursor read prevents mutation"); }
        fixture.RecentReadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pending = client.DeleteRecentPlayedSongAsync("102");
        await Until(() => fixture.RecentReadRequests == readsBefore + 2);
        client.SetCookieString("MUSIC_U=different-account; __csrf=other");
        fixture.RecentReadGate.SetResult();
        try { await pending; throw new Exception("Account switch ignored"); }
        catch (InvalidOperationException e) { Check(e.Message.Contains("账号已变更") && fixture.RecentDeleteRequests == before, "account switch between read and delete aborts the mutation"); }
        fixture.RecentReadGate = null;
        fixture.RecentRecords.Clear();
        Check(await client.DeleteRecentPlayedSongAsync("102") && fixture.RecentDeleteRequests == before, "already empty server history is handled without mutation");
        Check(fixture.Unknown.Count == 0, "recent protocol tests never use the old guessed endpoints");
    }

    private static async Task RecentPlayDeleteUiAsync()
    {
        var originalAccount = Get<AccountState?>("_accountState");
        long originalUserId = Get<long>("_loggedInUserId");
        var menu = Get<ToolStripMenuItem>("deleteRecentPlayMenuItem");
        object? originalTag = menu.Tag;
        var originalEngine = Get<BassAudioEngine?>("_audioEngine");
        void ResetRecentPage()
        {
            ShowSongs("recent_play");
            Set("_recentSongsCache", Songs());
            Set("_recentPlayCount", 3);
            Select(1);
            menu.Tag = Get<List<SongInfo>>("_currentSongs")[1];
            menu.Available = true;
        }
        try
        {
            Set("_accountState", new AccountState { IsLoggedIn = true, UserId = "42" });
            Set("_loggedInUserId", 42L);
            Check(menu.Text == "从最近播放中删除(&R)" && menu.ShortcutKeyDisplayString == "Delete", "recent deletion menu exposes R and Delete");
            Check(Get<ToolStripMenuItem>("playbackMenuItem").ShortcutKeyDisplayString == "Ctrl+R", "playback menu advertises relocated shortcut");
            using (var help = new YTPlayer.Forms.KeyboardShortcutsDialog())
            {
                var text = help.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<YTPlayer.Forms.AccessibleDocumentView>().Single().Document!.PlainText;
                Check(text.Contains("Ctrl + R") && text.Contains("移除聚焦项目") && text.Contains("Delete") && !text.Contains("Ctrl + Shift + R"), "embedded help documents both R shortcuts");
            }
            ResetRecentPage();
            int readsBefore = handler.RecentReadRequests;
            recentDeleteConfirmation = DialogResult.No;
            var contextMenu = Get<ContextMenuStrip>("songContextMenu");
            contextMenu.Show(list, new System.Drawing.Point(8, 8));
            try
            {
                Check(menu.Available && contextMenu.Items.Cast<ToolStripItem>().Count(item => item.Available && Control.IsMnemonic('R', item.Text)) == 1,
                    "recent menu has exactly one R mnemonic");
                Check((bool)typeof(ToolStripDropDown).GetMethod("ProcessMnemonic", Instance)!.Invoke(contextMenu, new object[] { 'r' })!, "R mnemonic opens the deletion confirmation");
            }
            finally { contextMenu.Close(); }
            Check(handler.RecentReadRequests == readsBefore && Get<List<SongInfo>>("_currentSongs").Count == 3,
                $"canceling deletion keeps records and sends no packet; reads={handler.RecentReadRequests}/{readsBefore}, songs={Get<List<SongInfo>>("_currentSongs").Count}, confirmations={recentDeleteConfirmationCount}, dialogs={string.Join(",", dialogs)}");

            ShowSongs("album:42"); Select(1);
            Check(!DispatchShortcut(list, Keys.Delete), "Delete does not delete outside recent-play view");
            ResetRecentPage();
            var search = Get<TextBox>("searchTextBox"); search.Focus();
            Check(!DispatchShortcut(search, Keys.Delete), "Delete does not act on an unfocused list");
            Select(1);
            Set("_accountState", new AccountState { IsLoggedIn = false });
            Check(!DispatchShortcut(list, Keys.Delete), "logged-out Delete is ignored");
            Set("_accountState", new AccountState { IsLoggedIn = true, UserId = "42" });
            foreach (object tag in new object[] { new AlbumInfo { Id = "42" }, new SongInfo { Id = "102", IsPodcastEpisode = true }, new SongInfo() })
            {
                list.Items.Clear(); list.Items.Add(new ListViewItem("不支持的项目") { Tag = tag }); Select(0);
                Check(!DispatchShortcut(list, Keys.Delete), "recent shortcut rejects unsupported item " + tag.GetType().Name);
            }
            list.Items.Clear();
            Check(!DispatchShortcut(list, Keys.Delete), "empty recent list ignores Delete");
            Check(handler.RecentReadRequests == readsBefore, "guarded shortcuts never send a deletion request");
            ResetRecentPage();
            recentDeleteConfirmation = DialogResult.Yes;
            handler.RecentDeleteGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int before = handler.RecentDeleteRequests;
            menu.Tag = Songs()[0];
            Check(DispatchShortcut(list, Keys.Delete), "Delete opens the same confirmation and deletion path");
            await Until(() => handler.RecentDeleteRequests == before + 1);
            Check(handler.LastRecentDeleteBody?.Value<string>("resourceIds") == "[102]", "Delete deletes the focused song, not a stale menu target");
            int confirmations = recentDeleteConfirmationCount;
            DispatchShortcut(list, Keys.Delete);
            Check(handler.RecentDeleteRequests == before + 1 && recentDeleteConfirmationCount == confirmations, "pending deletion is coalesced before another confirmation");
            Check(Get<List<SongInfo>>("_currentSongs").Count == 3 && Get<List<SongInfo>>("_recentSongsCache").Count == 3, "UI waits for server confirmation before removing history");
            handler.RecentDeleteGate.SetResult();
            await Until(() => Get<HashSet<string>>("_pendingRecentPlayDeletes").Count == 0);
            Check(Get<List<SongInfo>>("_currentSongs").Select(song => song.Id).SequenceEqual(new[] { "101", "103" }) &&
                Get<List<SongInfo>>("_recentSongsCache").Select(song => song.Id).SequenceEqual(new[] { "101", "103" }), "successful deletion synchronizes visible list and recent cache");
            await Until(() => list.Focused && list.FocusedItem?.Index == 1);
            Check(Get<int>("_recentPlayCount") == 2 && list.Focused && list.FocusedItem?.Index == 1, "deletion updates count and preserves next-row focus");

            ResetRecentPage();
            handler.RecentDeleteGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            before = handler.RecentDeleteRequests;
            menu.PerformClick();
            await Until(() => handler.RecentDeleteRequests == before + 1);
            ShowSongs("album:42");
            handler.RecentDeleteGate.SetResult();
            await Until(() => Get<HashSet<string>>("_pendingRecentPlayDeletes").Count == 0);
            Check(Get<List<SongInfo>>("_currentSongs").Count == 3 && Get<List<SongInfo>>("_recentSongsCache").Count == 2, "late deletion updates only recent cache after navigating elsewhere");

            ResetRecentPage();
            handler.RecentDeleteGate = null;
            handler.RecentDeleteCode = 400;
            expectRecentDeleteError = true;
            int errorsBefore = recentDeleteErrorCount;
            menu.PerformClick();
            await Until(() => Get<HashSet<string>>("_pendingRecentPlayDeletes").Count == 0);
            Check(recentDeleteErrorCount == errorsBefore + 1 && Get<List<SongInfo>>("_currentSongs").Count == 3 && Get<List<SongInfo>>("_recentSongsCache").Count == 3,
                "rejected delete reports an error and preserves local records");

            handler.RecentDeleteCode = 200;
            handler.RecentDeleteGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            before = handler.RecentDeleteRequests;
            menu.PerformClick();
            await Until(() => handler.RecentDeleteRequests == before + 1);
            Set("_loggedInUserId", 43L);
            handler.RecentDeleteGate.SetResult();
            await Until(() => Get<HashSet<string>>("_pendingRecentPlayDeletes").Count == 0);
            Check(Get<List<SongInfo>>("_recentSongsCache").Count == 3 && Get<List<SongInfo>>("_currentSongs").Count == 3, "completed old-account request cannot alter new-account UI");

            // Only the mode property is needed; do not initialize BASS or an audio output device.
            var engine = (BassAudioEngine)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(BassAudioEngine));
            Set("_audioEngine", engine);
            engine.PlayMode = PlayMode.Sequential;
            ShowSongs("album:42"); Select(1);
            Check(!DispatchShortcut(list, Keys.Control | Keys.Shift | Keys.R) && engine.PlayMode == PlayMode.Sequential, "Ctrl+Shift+R no longer changes playback order");
            before = handler.RecentDeleteRequests;
            foreach (PlayMode expected in new[] { PlayMode.Loop, PlayMode.LoopOne, PlayMode.Random, PlayMode.Sequential })
                Check(DispatchShortcut(list, Keys.Control | Keys.R) && engine.PlayMode == expected, "Ctrl+R cycles playback order to " + expected);
            Check(handler.RecentDeleteRequests == before, "playback-order shortcut never deletes recent records");
        }
        finally
        {
            handler.RecentDeleteGate?.TrySetResult();
            handler.RecentDeleteGate = null;
            handler.RecentDeleteCode = 200;
            recentDeleteConfirmation = null;
            expectRecentDeleteError = false;
            Set("_accountState", originalAccount);
            Set("_loggedInUserId", originalUserId);
            Set("_audioEngine", originalEngine);
            menu.Tag = originalTag;
        }
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
        internal bool FmModeFixtureEnabled;
        internal int FmModeRequests;
        internal int FmModeCode = 200;
        internal bool FmModeEmpty;
        internal TaskCompletionSource? FmModeGate;
        internal readonly System.Collections.Concurrent.ConcurrentQueue<JObject> FmModeBodies = new();
        internal readonly System.Collections.Concurrent.ConcurrentQueue<(JObject Body, Task Gate)> ArtistPages = new();
        internal int ArtistPagesStarted;
        internal int ArtistPagesCompleted;
        internal bool BlockCategories;
        internal int DislikeRequests;
        internal string LastDislikePath = "";
        internal string LastDislikeSignaturePath = "";
        internal string LastDislikeCookie = "";
        internal JObject? LastDislikeBody;
        internal int BackgroundReadRequests;
        internal TaskCompletionSource? BackgroundReadGate;
        internal int RemovalRequests;
        internal int RemovalCode = 200;
        internal string LastRemovalPath = "";
        internal TaskCompletionSource? RemovalGate;
        internal int DislikeCode = 200;
        internal bool FailDislikeTransport;
        internal TaskCompletionSource? DislikeGate;
        internal int RecentReadRequests;
        internal int RecentDeleteRequests;
        internal string LastRecentReadPath = "";
        internal int RecentReadCode = 200;
        internal int RecentDeleteCode = 200;
        internal bool InvalidRecentList;
        internal bool FailRecentDeleteTransport;
        internal TaskCompletionSource? RecentReadGate;
        internal TaskCompletionSource? RecentDeleteGate;
        internal JObject? LastRecentReadBody;
        internal string LastRecentReadCookie = "";
        internal JObject? LastRecentDeleteBody;
        internal string LastRecentDeleteSignature = "";
        internal string LastRecentDeleteCookie = "";
        internal JArray RecentRecords = new(
            new JObject { ["resourceId"] = "101", ["playTime"] = 1790000002000L },
            new JObject { ["resourceId"] = "102", ["playTime"] = 1790000001000L },
            new JObject { ["resourceId"] = "3000000003", ["playTime"] = 1790000000123L });
        internal TaskCompletionSource CategoriesEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly System.Collections.Concurrent.ConcurrentBag<string> Unknown = new();
        private static readonly string SongJson = """{"id":101,"name":"测试歌曲","ar":[{"id":12,"name":"测试歌手"}],"al":{"id":42,"name":"测试专辑"},"dt":180000,"privilege":{"id":101,"st":0,"pl":320000,"fee":0}}""";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string path = request.RequestUri!.AbsolutePath;
            string body;
            if (path.EndsWith("/artist/sublist", StringComparison.Ordinal) && ArtistPages.TryDequeue(out var page))
            {
                Interlocked.Increment(ref ArtistPagesStarted);
                await page.Gate; // Also exercises responses delivered after view cancellation.
                Interlocked.Increment(ref ArtistPagesCompleted);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(page.Body.ToString(), Encoding.UTF8, "application/json") };
            }
            if (new[] { "/eapi/play-record/song/list", "/eapi/play-record/playlist/list", "/eapi/play-record/album/list", "/eapi/play-record/djradio/list", "/eapi/play-record/batch/delete" }.Contains(path))
            {
                string formData = await request.Content!.ReadAsStringAsync(token);
                string clear = EncryptionHelper.DecryptEapi(Convert.FromHexString(Uri.UnescapeDataString(formData.Split('=')[1])));
                var parts = clear.Split("-36cd479b6b5-");
                var payload = JObject.Parse(parts[1]);
                JObject response;
                if (path.EndsWith("/list", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref RecentReadRequests);
                    LastRecentReadPath = path;
                    LastRecentReadBody = payload;
                    LastRecentReadCookie = request.Headers.TryGetValues("Cookie", out var readCookies) ? string.Join("; ", readCookies) : "";
                    if (RecentReadGate != null) await RecentReadGate.Task.WaitAsync(token);
                    response = new JObject { ["code"] = RecentReadCode, ["data"] = InvalidRecentList ? new JObject() : new JObject { ["list"] = RecentRecords.DeepClone() } };
                }
                else
                {
                    Interlocked.Increment(ref RecentDeleteRequests);
                    LastRecentDeleteSignature = parts[0];
                    LastRecentDeleteBody = payload;
                    LastRecentDeleteCookie = request.Headers.TryGetValues("Cookie", out var values) ? string.Join("; ", values) : "";
                    if (RecentDeleteGate != null) await RecentDeleteGate.Task.WaitAsync(token);
                    if (FailRecentDeleteTransport) throw new HttpRequestException("fixture failure");
                    response = new JObject { ["code"] = RecentDeleteCode, ["data"] = new JArray(), ["message"] = RecentDeleteCode == 200 ? null : "fixture rejection" };
                }
                using var aes = Aes.Create();
                aes.Key = Encoding.UTF8.GetBytes("e82ckenh8dichen8");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(aes.EncryptEcb(Encoding.UTF8.GetBytes(response.ToString(Newtonsoft.Json.Formatting.None)), PaddingMode.PKCS7)) };
            }
            if (path.EndsWith("/v1/radio/trash/add", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref DislikeRequests);
                LastDislikePath = path;
                string formData = await request.Content!.ReadAsStringAsync(token);
                string clear = EncryptionHelper.DecryptEapi(Convert.FromHexString(Uri.UnescapeDataString(formData.Split('=')[1])));
                var parts = clear.Split("-36cd479b6b5-");
                LastDislikeSignaturePath = parts[0];
                LastDislikeBody = JObject.Parse(parts[1]);
                LastDislikeCookie = request.Headers.TryGetValues("Cookie", out var values) ? string.Join("; ", values) : "";
                if (DislikeGate != null) await DislikeGate.Task.WaitAsync(token);
                if (FailDislikeTransport) throw new HttpRequestException("fixture failure");
                using var aes = Aes.Create();
                aes.Key = Encoding.UTF8.GetBytes("e82ckenh8dichen8");
                byte[] response = Encoding.UTF8.GetBytes(new JObject { ["songs"] = new JArray(), ["count"] = 0, ["code"] = DislikeCode }.ToString(Newtonsoft.Json.Formatting.None));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(aes.EncryptEcb(response, PaddingMode.PKCS7)) };
            }
            if (BackgroundReadGate != null && new[] { "/v1/cloud/get", "/v3/playlist/detail", "/play-record/song/list", "/play-record/playlist/list", "/play-record/album/list", "/play-record/djradio/list" }.Any(suffix => path.EndsWith(suffix, StringComparison.Ordinal)))
            {
                Interlocked.Increment(ref BackgroundReadRequests);
                // Deliberately deliver a stale response even if the view has been canceled.
                await BackgroundReadGate.Task;
                body = path.EndsWith("/v1/cloud/get")
                    ? """{"code":200,"count":4,"hasMore":false,"data":[{"songId":104,"songName":"迟到的云盘歌曲"}]}"""
                    : path.EndsWith("/v3/playlist/detail")
                        ? """{"code":200,"playlist":{"id":801,"name":"旧歌单","trackCount":0}}"""
                        : "{\"code\":200,\"data\":{\"list\":[{\"data\":" + SongJson + "}]}}";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            }
            if (new[] { "/playlist/remove", "/playlist/manipulate/tracks", "/cloud/del", "/radio/like", "/resource/comments/delete" }.Any(suffix => path.EndsWith(suffix, StringComparison.Ordinal)))
            {
                Interlocked.Increment(ref RemovalRequests);
                LastRemovalPath = path;
                if (RemovalGate != null) await RemovalGate.Task.WaitAsync(token);
                body = new JObject { ["code"] = RemovalCode, ["message"] = RemovalCode == 200 ? "ok" : "fixture rejection" }.ToString();
            }
            else if (path.Contains("djradio/category/get"))
            {
                CategoriesEntered.TrySetResult();
                if (BlockCategories) await Task.Delay(Timeout.Infinite, token);
                body = """{"code":200,"categories":[{"id":3,"name":"测试播客分类"}]}""";
            }
            else if (path.EndsWith("/v2/discovery/recommend/dislike", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref DislikeRequests);
                LastDislikePath = path;
                body = """{"code":200}""";
            }
            else if (path.EndsWith("/v1/radio/get", StringComparison.Ordinal))
            {
                if (FmModeFixtureEnabled)
                {
                    string formData = await request.Content!.ReadAsStringAsync(token);
                    string clear = EncryptionHelper.DecryptEapi(Convert.FromHexString(Uri.UnescapeDataString(formData.Split('=')[1])));
                    var payload = JObject.Parse(clear.Split("-36cd479b6b5-")[1]);
                    FmModeBodies.Enqueue(payload);
                    int number = Interlocked.Increment(ref FmModeRequests);
                    var gate = FmModeGate;
                    int code = FmModeCode;
                    bool empty = FmModeEmpty;
                    if (gate != null) await gate.Task; // Intentionally return even after cancellation.
                    var songs = new JArray();
                    if (!empty)
                        for (int i = 0; i < 3; i++)
                        {
                            var item = JObject.Parse(SongJson);
                            item["id"] = 10000 + number * 3 + i;
                            item["alg"] = payload.Value<string>("mode") + ":" + payload.Value<string>("subMode");
                            songs.Add(item);
                        }
                    body = new JObject { ["code"] = code, ["data"] = songs, ["message"] = "fixture rejection" }.ToString();
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                }
                var song = JObject.Parse(SongJson);
                song["alg"] = "fm-response-alg";
                body = new JObject { ["code"] = 200, ["data"] = new JArray(song) }.ToString();
            }
            else if (path.EndsWith("/song/like/get", StringComparison.Ordinal)) body = """{"code":200,"ids":[]}""";
            else if (path.EndsWith("/song/lyric/v1", StringComparison.Ordinal) || path.EndsWith("/song/lyric", StringComparison.Ordinal))
                body = """{"code":200,"lrc":{"lyric":"[00:00.00]测试歌词"}}""";
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
