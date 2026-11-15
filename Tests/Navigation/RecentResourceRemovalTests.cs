using Newtonsoft.Json.Linq;
using YTPlayer.Core;
using YTPlayer.Models;
using YTPlayer.Models.Auth;

internal static partial class Program
{
    private static readonly RecentPlayResourceType[] RecentResourceTypes = { RecentPlayResourceType.Playlist, RecentPlayResourceType.Album, RecentPlayResourceType.Podcast };

    private static (string Source, string Endpoint, string WireType, string Cache, string Count) RecentResourceInfo(RecentPlayResourceType type) => type switch
    {
        RecentPlayResourceType.Playlist => ("recent_playlists", "playlist", "PLAYLIST", "_recentPlaylistsCache", "_recentPlaylistCount"),
        RecentPlayResourceType.Album => ("recent_albums", "album", "ALBUM", "_recentAlbumsCache", "_recentAlbumCount"),
        _ => ("recent_podcasts", "djradio", "DJ_RADIO", "_recentPodcastsCache", "_recentPodcastCount")
    };

    private static void ShowRecentResources(RecentPlayResourceType type, int count = 3, string? overrideSource = null)
    {
        var info = RecentResourceInfo(type);
        string source = overrideSource ?? info.Source;
        switch (type)
        {
            case RecentPlayResourceType.Playlist:
                var playlists = Enumerable.Range(0, count).Select(i => new PlaylistInfo { Id = (101 + i).ToString(), Name = "自建歌单" + i, CreatorId = 42 }).ToList();
                Call("DisplayPlaylists", playlists, false, source, "最近歌单测试");
                Set(info.Cache, playlists.ToList());
                break;
            case RecentPlayResourceType.Album:
                var albums = Enumerable.Range(0, count).Select(i => new AlbumInfo { Id = (101 + i).ToString(), Name = "专辑" + i, Artist = "歌手" }).ToList();
                Call("DisplayAlbums", albums, false, source, "最近专辑测试");
                Set(info.Cache, albums.ToList());
                break;
            default:
                var podcasts = Enumerable.Range(0, count).Select(i => new PodcastRadioInfo { Id = 101 + i, Name = "播客" + i }).ToList();
                Call("DisplayPodcasts", podcasts, false, false, 1, false, source, "最近播客测试");
                Set(info.Cache, podcasts.ToList());
                break;
        }
        Set(info.Count, count);
        if (count > 0) Select(Math.Min(count - 1, 1));
    }

    private static string[] VisibleRecentResourceIds(RecentPlayResourceType type) => type switch
    {
        RecentPlayResourceType.Playlist => Get<List<PlaylistInfo>>("_currentPlaylists").Select(p => p.Id ?? string.Empty).ToArray(),
        RecentPlayResourceType.Album => Get<List<AlbumInfo>>("_currentAlbums").Select(p => p.Id ?? string.Empty).ToArray(),
        _ => Get<List<PodcastRadioInfo>>("_currentPodcasts").Select(p => p.Id.ToString()).ToArray()
    };

    private static async Task RecentResourceRemovalAsync()
    {
        var account = Get<AccountState?>("_accountState");
        long user = Get<long>("_loggedInUserId");
        var context = Get<ContextMenuStrip>("songContextMenu");
        var historyMenu = Get<ToolStripMenuItem>("deleteRecentPlayMenuItem");
        try
        {
            Set("_accountState", new AccountState { IsLoggedIn = true, UserId = "42" }); Set("_loggedInUserId", 42L);
            foreach (var type in RecentResourceTypes)
            {
                var info = RecentResourceInfo(type);
                int before = handler.RecentDeleteRequests;
                Check(await Get<NeteaseApiClient>("_apiClient").DeleteRecentPlayedResourceAsync(type, "2147483650"), type + " protocol accepts encrypted success");
                Check(handler.LastRecentReadPath == "/eapi/play-record/" + info.Endpoint + "/list", type + " reads its own cursor");
                var body = handler.LastRecentDeleteBody!;
                Check(body.Value<string>("type") == info.WireType && body.Value<string>("resourceIds") == "[2147483650]" && body["resourceIds"]!.Type == JTokenType.String, type + " uses captured resource type and JSON-in-string IDs");
                Check(body.Value<long>("lastResourceId") == 3000000003L && body.Value<long>("lastPlayTime") == 1790000000123L && handler.RecentDeleteRequests == before + 1, type + " preserves 64-bit tail cursor and sends a single mutation");
                int reads = handler.RecentReadRequests;
                Check(!await Get<NeteaseApiClient>("_apiClient").DeleteRecentPlayedResourceAsync(type, "bad-id") && handler.RecentReadRequests == reads, type + " validates ID before any request");

                ShowRecentResources(type);
                if (type == RecentPlayResourceType.Album) Call("UpdateAlbumSubscriptionState", "102", true);
                recentDeleteConfirmation = DialogResult.No;
                context.Show(list, new System.Drawing.Point(8, 8));
                try
                {
                    Check(historyMenu.Available && historyMenu.ShortcutKeyDisplayString == "Delete", type + " has a visible history removal entry");
                    Check(context.Items.Cast<ToolStripItem>().Count(item => item.Available && Control.IsMnemonic('r', item.Text)) == 1, type + " reserves exactly one R mnemonic for history");
                    if (type == RecentPlayResourceType.Playlist) Check(!Get<ToolStripMenuItem>("deletePlaylistMenuItem").Available, "recent owned playlists cannot expose DeletePlaylist");
                    Check((bool)typeof(ToolStripDropDown).GetMethod("ProcessMnemonic", Instance)!.Invoke(context, new object[] { 'r' })!, type + " R invokes the history confirmation");
                }
                finally { context.Close(); }
                Check(handler.RecentReadRequests == reads, type + " cancel sends no request");

                recentDeleteConfirmation = DialogResult.Yes;
                foreach (var (count, index) in new[] { (3, 0), (3, 1), (3, 2), (1, 0) })
                {
                    ShowRecentResources(type, count); Select(index);
                    before = handler.RecentDeleteRequests;
                    int destructiveBefore = handler.RemovalRequests;
                    Check(DispatchShortcut(list, Keys.Delete), type + " handles Delete");
                    await Until(() => handler.RecentDeleteRequests == before + 1 && Get<HashSet<string>>("_pendingRecentPlayDeletes").Count == 0);
                    Check(handler.LastRecentDeleteBody!.Value<string>("resourceIds") == "[" + (101 + index) + "]" && handler.LastRecentDeleteBody!.Value<string>("type") == info.WireType, type + " targets the focused record");
                    Check(handler.RemovalRequests == destructiveBefore, type + " never deletes a resource or changes subscriptions");
                    Check(VisibleRecentResourceIds(type).Length == count - 1 && Get<int>(info.Count) == count - 1, type + " synchronizes list and cached count");
                    Check(list.SelectedIndices.Count == 1 && list.SelectedIndices[0] == Math.Max(0, Math.Min(index, count - 2)) && list.FocusedItem?.Index == list.SelectedIndices[0], type + " keeps position after removal " + index + "/" + count);
                    await Until(() => list.ContainsFocus);
                    if (count == 1) Check(!DispatchShortcut(list, Keys.Delete), type + " empty placeholder cannot be removed");
                }

                ShowRecentResources(type); Select(1);
                handler.RecentDeleteGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
                before = handler.RecentDeleteRequests;
                DispatchShortcut(list, Keys.Delete);
                await Until(() => handler.RecentDeleteRequests == before + 1);
                int confirmations = recentDeleteConfirmationCount;
                DispatchShortcut(list, Keys.Delete);
                Check(handler.RecentDeleteRequests == before + 1 && recentDeleteConfirmationCount == confirmations, type + " coalesces repeated removal");
                Select(2); handler.RecentDeleteGate.SetResult();
                await Until(() => Get<HashSet<string>>("_pendingRecentPlayDeletes").Count == 0);
                Check(VisibleRecentResourceIds(type)[list.SelectedIndices[0]] == "103", type + " preserves a later user selection");
                handler.RecentDeleteGate = null;

                ShowRecentResources(type); Select(1);
                handler.RecentDeleteCode = 400; expectRecentDeleteError = true;
                int errors = recentDeleteErrorCount;
                DispatchShortcut(list, Keys.Delete);
                await Until(() => Get<HashSet<string>>("_pendingRecentPlayDeletes").Count == 0);
                Check(recentDeleteErrorCount == errors + 1 && VisibleRecentResourceIds(type).Length == 3 && Get<int>(info.Count) == 3 && list.SelectedIndices[0] == 1, type + " rejected deletion leaves the list intact");
                handler.RecentDeleteCode = 200; expectRecentDeleteError = false;

                ShowRecentResources(type);
                handler.RecentDeleteGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
                before = handler.RecentDeleteRequests; DispatchShortcut(list, Keys.Delete);
                await Until(() => handler.RecentDeleteRequests == before + 1);
                ShowSongs("album:42"); Select(2);
                handler.RecentDeleteGate.SetResult();
                await Until(() => Get<HashSet<string>>("_pendingRecentPlayDeletes").Count == 0);
                Check(Source == "album:42" && Get<List<SongInfo>>("_currentSongs").Count == 3 && Get<int>(info.Count) == 2 && list.SelectedIndices[0] == 2, type + " late completion updates only its cache after navigation");
                handler.RecentDeleteGate = null;

                ShowRecentResources(type);
                handler.RecentDeleteGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
                before = handler.RecentDeleteRequests; DispatchShortcut(list, Keys.Delete);
                await Until(() => handler.RecentDeleteRequests == before + 1);
                Set("_loggedInUserId", 43L); handler.RecentDeleteGate.SetResult();
                await Until(() => Get<HashSet<string>>("_pendingRecentPlayDeletes").Count == 0);
                Check(Get<int>(info.Count) == 3 && VisibleRecentResourceIds(type).Length == 3, type + " old-account completion cannot change new-account data");
                Set("_loggedInUserId", 42L); handler.RecentDeleteGate = null;

                ShowRecentResources(type);
                Get<TextBox>("searchTextBox").Focus();
                Check(!DispatchShortcut(Get<TextBox>("searchTextBox"), Keys.Delete), type + " Delete remains text editing outside the list");
                Select(1); Set("_accountState", new AccountState { IsLoggedIn = false });
                Check(!DispatchShortcut(list, Keys.Delete), type + " logged-out Delete is ignored");
                context.Show(list, new System.Drawing.Point(8, 8));
                Check(!historyMenu.Available, type + " logged-out context menu hides removal"); context.Close();
                Set("_accountState", new AccountState { IsLoggedIn = true, UserId = "42" });
            }

            foreach (string source in new[] { "recent_playlists", "search:playlist:test", "highquality_playlists", "toplist", "user_playlists" })
            {
                ShowRecentResources(RecentPlayResourceType.Playlist, overrideSource: source);
                context.Show(list, new System.Drawing.Point(8, 8));
                Check(Get<ToolStripMenuItem>("deletePlaylistMenuItem").Available == (source == "user_playlists"), "delete-playlist menu is confined to user_playlists: " + source);
                context.Close();
                if (source != "user_playlists")
                {
                    int before = handler.RemovalRequests;
                    Call("deletePlaylistMenuItem_Click", Get<List<PlaylistInfo>>("_currentPlaylists")[1], EventArgs.Empty);
                    Check(handler.RemovalRequests == before, "direct playlist deletion handler rejects other views: " + source);
                    if (source != "recent_playlists") Check(!DispatchShortcut(list, Keys.Delete), "Delete cannot delete owned playlists outside user_playlists: " + source);
                }
            }
            await RecentResourceBackgroundRaceAsync();
        }
        finally
        {
            context.Close(); handler.RecentDeleteGate?.TrySetResult(); handler.RecentDeleteGate = null;
            handler.BackgroundReadGate?.TrySetResult(); handler.BackgroundReadGate = null;
            handler.RecentDeleteCode = 200; recentDeleteConfirmation = null; expectRecentDeleteError = false;
            Set("_accountState", account); Set("_loggedInUserId", user);
        }
    }

    private static async Task RecentResourceBackgroundRaceAsync()
    {
        foreach (var type in RecentResourceTypes)
        {
            var info = RecentResourceInfo(type);
            ShowRecentResources(type);
            Call("BeginViewContentOperation", false);
            handler.BackgroundReadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int before = handler.BackgroundReadRequests;
            Task refresh = Run("EnrichRecent" + (type == RecentPlayResourceType.Playlist ? "Playlists" : type == RecentPlayResourceType.Album ? "Albums" : "Podcasts") + "Async", info.Source, "后台刷新测试");
            await Until(() => handler.BackgroundReadRequests == before + 1);
            DispatchShortcut(list, Keys.Delete);
            await Until(() => Get<HashSet<string>>("_pendingRecentPlayDeletes").Count == 0 && VisibleRecentResourceIds(type).Length == 2);
            handler.BackgroundReadGate.SetResult(); await refresh;
            Check(VisibleRecentResourceIds(type).SequenceEqual(new[] { "101", "103" }) && Get<int>(info.Count) == 2, type + " canceled background response cannot resurrect a deleted record");
            handler.BackgroundReadGate = null;
        }
    }
}
