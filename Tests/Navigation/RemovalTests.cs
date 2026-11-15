using System.Reflection;
using YTPlayer.Core;
using YTPlayer.Forms;
using YTPlayer.Models;
using YTPlayer.Models.Auth;
using YTPlayer.Utils;

internal static partial class Program
{
    private static void ShowRemovalSongs(string source, int count = 3)
    {
        var songs = Songs().Take(count).ToList();
        if (source == "user_cloud")
            foreach (var song in songs) { song.IsCloudSong = true; song.CloudSongId = song.Id; song.CloudFileSize = 10; }
        Call("DisplaySongs", songs, false, false, 1, false, source, "删除测试", true);
        Set("_currentPlaylist", source.StartsWith("playlist:") || source == "user_liked_songs"
            ? new PlaylistInfo { Id = source.StartsWith("playlist:") ? source[9..] : "901", Name = "测试歌单", CreatorId = 42, TrackCount = count, Songs = songs }
            : null);
        Set("_currentPlaylistOwnedByUser", source.StartsWith("playlist:"));
        if (source == "user_cloud") { Set("_cloudTotalCount", count); Set("_cloudUsedSize", 30L); }
        if (source == "recent_play") { Set("_recentSongsCache", songs.ToList()); Set("_recentPlayCount", count); }
    }

    private static void CheckRemovalSelection(int remaining, int index, string label)
    {
        Check(Get<List<SongInfo>>("_currentSongs").Count == remaining, label + " updates the song model");
        Check(list.SelectedIndices.Count == 1 && list.SelectedIndices[0] == Math.Max(0, index) && list.FocusedItem?.Index == Math.Max(0, index), label + " keeps native selection and item focus together");
        Check(list.ContainsFocus, label + " keeps keyboard focus in the list");
        if (remaining == 0) Check(list.Items.Count == 1 && !DispatchShortcut(list, Keys.Delete), label + " leaves a non-actionable empty-list placeholder");
    }

    private static async Task RemovalIntegrationAsync()
    {
        var account = Get<AccountState?>("_accountState");
        long user = Get<long>("_loggedInUserId");
        try
        {
            Set("_accountState", new AccountState { IsLoggedIn = true, UserId = "42" });
            Set("_loggedInUserId", 42L);
            recentDeleteConfirmation = DialogResult.Yes;
            foreach (string source in new[] { "recent_play", "personal_fm", "daily_recommend_songs" })
            {
                foreach (var (count, deleted) in new[] { (3, 0), (3, 1), (3, 2), (1, 0) })
                {
                    ShowRemovalSongs(source, count); Select(deleted);
                    var song = Get<List<SongInfo>>("_currentSongs")[deleted];
                    if (source == "recent_play") Call("TryRemoveRecentPlayedSongLocally", song, song.Id);
                    else Call("TryRemoveSongFromCurrentRecommendationList", song);
                    CheckRemovalSelection(count - 1, Math.Min(deleted, count - 2), source + " delete " + deleted + "/" + count);
                }
            }

            foreach (var (source, endpoint, menuName) in new[] {
                ("playlist:801", "/playlist/manipulate/tracks", "removeFromPlaylistMenuItem"),
                ("user_liked_songs", "/radio/like", "removeFromPlaylistMenuItem"),
                ("user_cloud", "/cloud/del", "deleteFromCloudMenuItem") })
            {
                foreach (var (count, deleted) in new[] { (3, 0), (3, 1), (3, 2), (1, 0) })
                {
                    ShowRemovalSongs(source, count); Select(deleted);
                    var menu = Get<ToolStripMenuItem>(menuName);
                    Check(menu.ShortcutKeyDisplayString == "Delete" && Control.IsMnemonic('r', menu.Text), source + " has Delete and R");
                    int before = handler.RemovalRequests;
                    Check(DispatchShortcut(list, Keys.Delete), source + " handles Delete");
                    await Until(() => handler.RemovalRequests == before + 1 && Get<HashSet<string>>("_pendingListRemovals").Count == 0);
                    Check(handler.LastRemovalPath.EndsWith(endpoint, StringComparison.Ordinal), source + " uses its existing API");
                    await Until(() => list.ContainsFocus);
                    CheckRemovalSelection(count - 1, Math.Min(deleted, count - 2), source + " delete " + deleted + "/" + count);
                }

                ShowRemovalSongs(source); Select(1);
                handler.RemovalGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
                int requests = handler.RemovalRequests;
                Check(DispatchShortcut(list, Keys.Delete), source + " starts delayed deletion");
                await Until(() => handler.RemovalRequests == requests + 1);
                int confirmations = recentDeleteConfirmationCount;
                DispatchShortcut(list, Keys.Delete);
                Check(handler.RemovalRequests == requests + 1 && recentDeleteConfirmationCount == confirmations, source + " coalesces repeated Delete");
                Select(2);
                handler.RemovalGate.SetResult();
                await Until(() => Get<HashSet<string>>("_pendingListRemovals").Count == 0);
                CheckRemovalSelection(2, 1, source + " preserves a later user selection");
                Check(Get<List<SongInfo>>("_currentSongs")[1].Id == "103", source + " preserves selected identity");

                ShowRemovalSongs(source); Select(0);
                handler.RemovalGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
                requests = handler.RemovalRequests;
                DispatchShortcut(list, Keys.Delete);
                await Until(() => handler.RemovalRequests == requests + 1);
                var search = Get<TextBox>("searchTextBox"); search.Focus();
                handler.RemovalGate.SetResult();
                await Until(() => Get<HashSet<string>>("_pendingListRemovals").Count == 0);
                Check(search.Focused && list.SelectedIndices[0] == 0, source + " does not steal focus from the search box");

                ShowRemovalSongs(source); Select(1);
                handler.RemovalGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
                requests = handler.RemovalRequests;
                DispatchShortcut(list, Keys.Delete);
                await Until(() => handler.RemovalRequests == requests + 1);
                ShowRemovalSongs("album:42"); Select(2);
                handler.RemovalGate.SetResult();
                await Until(() => Get<HashSet<string>>("_pendingListRemovals").Count == 0);
                Check(Source == "album:42" && Get<List<SongInfo>>("_currentSongs").Count == 3 && list.SelectedIndices[0] == 2, source + " does not alter a different page");
                handler.RemovalGate = null;

                ShowRemovalSongs(source); Select(1);
                handler.RemovalCode = 400; expectRecentDeleteError = true;
                int errors = recentDeleteErrorCount;
                DispatchShortcut(list, Keys.Delete);
                await Until(() => Get<HashSet<string>>("_pendingListRemovals").Count == 0);
                Check(recentDeleteErrorCount == errors + 1 && Get<List<SongInfo>>("_currentSongs").Count == 3 && list.SelectedIndices[0] == 1, source + " rejected deletion preserves rows and selection");
                handler.RemovalCode = 200; expectRecentDeleteError = false;
            }

            // Every supported main menu advertises exactly one R mnemonic.
            var contextMenu = Get<ContextMenuStrip>("songContextMenu");
            foreach (string source in new[] { "recent_play", "playlist:801", "user_liked_songs", "user_cloud" })
            {
                ShowRemovalSongs(source); Select(1);
                contextMenu.Show(list, new System.Drawing.Point(8, 8));
                try { Check(contextMenu.Items.Cast<ToolStripItem>().Count(item => item.Available && Control.IsMnemonic('r', item.Text)) == 1, source + " has one visible R action"); }
                finally { contextMenu.Close(); }
            }
            ShowRemovalSongs("playlist:801"); Set("_currentPlaylistOwnedByUser", false); Get<PlaylistInfo>("_currentPlaylist").CreatorId = 99; Select(0);
            Check(!DispatchShortcut(list, Keys.Delete), "Delete cannot remove someone else's playlist songs");
            ShowRemovalSongs("recent_play"); Select(0);
            foreach (Keys keys in new[] { Keys.Control | Keys.Delete, Keys.Shift | Keys.Delete, Keys.Alt | Keys.Delete })
                Check(!DispatchShortcut(list, keys), "modified Delete does not trigger removal: " + keys);
            recentDeleteConfirmation = DialogResult.No;
            int confirmationsBefore = recentDeleteConfirmationCount;
            var deleteMessage = Message.Create(list.Handle, 0x0100, (IntPtr)Keys.Delete, IntPtr.Zero);
            Check(list.PreProcessMessage(ref deleteMessage) && recentDeleteConfirmationCount == confirmationsBefore + 1,
                "a plain Delete message reaches removal through the WinForms preprocessing chain");
            recentDeleteConfirmation = DialogResult.Yes;

            await PlaylistRemovalIntegrationAsync();
            await RemovalDuringBackgroundLoadAsync();
            await CommentRemovalIntegrationAsync();
            await TransferRemovalIntegrationAsync();
        }
        finally
        {
            handler.RemovalGate?.TrySetResult(); handler.RemovalGate = null; handler.RemovalCode = 200;
            handler.BackgroundReadGate?.TrySetResult(); handler.BackgroundReadGate = null;
            recentDeleteConfirmation = null; expectRecentDeleteError = false;
            Set("_accountState", account); Set("_loggedInUserId", user);
            Set("_currentPlaylist", null); Set("_currentPlaylistOwnedByUser", false);
            form.Activate();
        }
    }

    private static async Task RemovalDuringBackgroundLoadAsync()
    {
        foreach (string source in new[] { "recent_play", "playlist:801", "user_cloud" })
        {
            ShowRemovalSongs(source); Select(1);
            Call("BeginViewContentOperation", false);
            var oldToken = (CancellationToken)Call("GetCurrentViewContentToken")!;
            handler.BackgroundReadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int reads = handler.BackgroundReadRequests;
            Task loading = source switch
            {
                "recent_play" => Run("EnrichRecentSongsAsync", source, "后台刷新测试"),
                "user_cloud" => Run("LoadRemainingCloudSongsAsync", Get<List<SongInfo>>("_currentSongs").ToList(), 3, 4, 30L, 100L),
                _ => Run("EnrichCurrentPlaylistSongsAsync", Get<PlaylistInfo>("_currentPlaylist"), Get<List<SongInfo>>("_currentSongs").ToList(), source, "后台刷新测试")
            };
            await Until(() => handler.BackgroundReadRequests == reads + 1);
            Check(DispatchShortcut(list, Keys.Delete), source + " allows removal while a refresh is pending");
            await Until(() => Get<HashSet<string>>("_pendingListRemovals").Count == 0 && Get<HashSet<string>>("_pendingRecentPlayDeletes").Count == 0 && Get<List<SongInfo>>("_currentSongs").Count == 2);
            Check(oldToken.IsCancellationRequested, source + " invalidates the old load after confirmed removal");
            handler.BackgroundReadGate.SetResult();
            await loading;
            Check(Get<List<SongInfo>>("_currentSongs").Select(song => song.Id).SequenceEqual(new[] { "101", "103" }) && list.SelectedIndices[0] == 1, source + " ignores a late response containing the old list");
            if (source == "recent_play") Check(Get<List<SongInfo>>("_recentSongsCache").Count == 2, "late history refresh cannot overwrite the updated cache");
            handler.BackgroundReadGate = null;
        }

        ShowRemovalSongs("playlist:801"); Select(1);
        handler.RemovalGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = handler.RemovalRequests;
        DispatchShortcut(list, Keys.Delete);
        await Until(() => handler.RemovalRequests == requests + 1);
        Set("_loggedInUserId", 43L);
        handler.RemovalGate.SetResult();
        await Until(() => Get<HashSet<string>>("_pendingListRemovals").Count == 0);
        Check(Get<List<SongInfo>>("_currentSongs").Count == 3, "old-account playlist deletion leaves the new-account UI unchanged");
        Set("_loggedInUserId", 42L); handler.RemovalGate = null;

        ShowRemovalSongs("recent_play"); Select(0);
        var removedSong = Get<List<SongInfo>>("_currentSongs")[1];
        Call("TryRemoveRecentPlayedSongLocally", removedSong, removedSong.Id);
        Check(Get<List<SongInfo>>("_currentSongs")[list.SelectedIndices[0]].Id == "101", "removing a later row preserves the earlier selected song");
    }

    private static async Task PlaylistRemovalIntegrationAsync()
    {
        foreach (var (count, deleted) in new[] { (3, 0), (3, 1), (3, 2), (1, 0) })
        {
            var playlists = Enumerable.Range(0, count).Select(i => new PlaylistInfo { Id = (501 + i).ToString(), Name = "普通歌单" + i, CreatorId = 42 }).ToList();
            Call("DisplayPlaylists", playlists, false, "user_playlists", "歌单测试");
            Set("_currentUserPlaylistsHasMore", false); Set("_currentUserPlaylistsNextOffset", count + 1); Set("_currentUserPlaylistsTotalCount", count);
            Select(deleted);
            int requests = handler.RemovalRequests;
            Check(DispatchShortcut(list, Keys.Delete), "owned playlist accepts Delete");
            await Until(() => handler.RemovalRequests == requests + 1 && Get<HashSet<string>>("_pendingListRemovals").Count == 0);
            Check(handler.LastRemovalPath.EndsWith("/playlist/remove") && Get<List<PlaylistInfo>>("_currentPlaylists").Count == count - 1, "owned playlist uses confirmed removal");
            Check(list.SelectedIndices[0] == Math.Max(0, Math.Min(deleted, count - 2)) && list.FocusedItem?.Index == list.SelectedIndices[0], "owned playlist restores position " + deleted + "/" + count);
            Check(Get<int>("_currentUserPlaylistsNextOffset") == count && Get<int>("_currentUserPlaylistsTotalCount") == count - 1, "playlist pagination is adjusted after deletion");
        }
        var other = new PlaylistInfo { Id = "888", Name = "其他歌单", CreatorId = 99 };
        Call("DisplayPlaylists", new List<PlaylistInfo> { other }, false, "user_playlists", "歌单测试"); Select(0);
        Check(!DispatchShortcut(list, Keys.Delete), "Delete cannot delete another user's playlist");
        var liked = new PlaylistInfo { Id = "42", Name = "喜欢的音乐", CreatorId = 42 };
        Call("DisplayPlaylists", new List<PlaylistInfo> { liked }, false, "user_playlists", "歌单测试"); Select(0);
        Check(!DispatchShortcut(list, Keys.Delete), "Delete cannot delete the built-in liked playlist");
    }

    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, Instance)!.GetValue(instance)!;
    private static object? Method(object instance, string name, params object?[] args) => Invoke(instance.GetType().GetMethod(name, Instance)!, instance, args);

    private static async Task CommentRemovalIntegrationAsync()
    {
        using var comments = new CommentsDialog(Get<NeteaseApiClient>("_apiClient"), new CommentTarget("101", CommentType.Song, "测试歌曲", "歌手"), "42", true);
        comments.Load -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), comments, typeof(CommentsDialog).GetMethod("OnDialogLoad", Instance)!);
        comments.StartPosition = FormStartPosition.Manual; comments.Location = new(-32000, -32000);
        comments.Show(form);
        var tree = Field<TreeView>(comments, "_commentTree");
        tree.BeforeExpand -= (TreeViewCancelEventHandler)Delegate.CreateDelegate(typeof(TreeViewCancelEventHandler), comments, typeof(CommentsDialog).GetMethod("OnCommentTreeBeforeExpand", Instance)!);
        TreeNode Node(string id, bool root = true, string user = "42") => new("评论" + id) { Tag = new CommentNodeTag { IsTopLevel = root, CommentId = id, Comment = new CommentInfo { CommentId = id, UserId = user, Content = "评论" + id } } };
        var menu = Field<ContextMenuStrip>(comments, "_treeMenu");
        var delete = menu.Items.OfType<ToolStripMenuItem>().Single(item => Control.IsMnemonic('r', item.Text));
        Check(delete.ShortcutKeyDisplayString == "Delete", "comment menu exposes Delete and R");
        foreach (var (count, index) in new[] { (3, 0), (3, 1), (3, 2), (1, 0) })
        {
            tree.Nodes.Clear();
            for (int i = 0; i < count; i++) tree.Nodes.Add(Node((600 + i).ToString()));
            tree.SelectedNode = tree.Nodes[index]; tree.Focus();
            int before = handler.RemovalRequests;
            var key = new KeyEventArgs(Keys.Delete);
            Method(comments, "OnCommentTreeKeyDown", tree, key);
            await Until(() => handler.RemovalRequests == before + 1 && Field<HashSet<string>>(comments, "_pendingCommentDeletes").Count == 0);
            Check(key.Handled && key.SuppressKeyPress && tree.Nodes.Count == count - 1, "comment Delete removes only the selected node");
            Check(count == 1 ? tree.SelectedNode == null : tree.SelectedNode?.Index == Math.Min(index, count - 2), "comment deletion preserves sibling position " + index + "/" + count);
        }
        tree.Nodes.Clear(); var parent = Node("701"); var next = Node("702"); tree.Nodes.AddRange(new[] { parent, next });
        parent.Nodes.Add(Node("703", false)); parent.Expand(); tree.SelectedNode = parent;
        Method(comments, "RemoveDeletedCommentNode", parent, (CommentNodeTag)parent.Tag!);
        Check(tree.SelectedNode == next && next.TreeView == tree, "deleting an expanded root never selects its removed child");
        tree.Nodes.Clear(); parent = Node("710"); tree.Nodes.Add(parent); var reply = Node("711", false); parent.Nodes.Add(reply); parent.Expand(); tree.SelectedNode = reply;
        Method(comments, "RemoveDeletedCommentNode", reply, (CommentNodeTag)reply.Tag!);
        Check(tree.SelectedNode == parent, "deleting the only reply returns to the parent comment");
        tree.Nodes.Clear(); var first = Node("720"); var second = Node("721"); tree.Nodes.AddRange(new[] { first, second }); tree.SelectedNode = second;
        Method(comments, "RemoveDeletedCommentNode", first, (CommentNodeTag)first.Tag!);
        Check(tree.SelectedNode == second, "comment removal preserves a different selected comment");
        var other = Node("730", user: "99"); tree.Nodes.Add(other); tree.SelectedNode = other;
        Method(comments, "UpdateContextMenuState");
        Check(!delete.Enabled, "R mnemonic retains comment ownership guard");
        int requests = handler.RemovalRequests;
        Method(comments, "OnCommentTreeKeyDown", tree, new KeyEventArgs(Keys.Delete));
        Check(handler.RemovalRequests == requests, "Delete cannot remove another user's comment");
        tree.SelectedNode = second;
        var modified = new KeyEventArgs(Keys.Control | Keys.Delete);
        Method(comments, "OnCommentTreeKeyDown", tree, modified);
        Check(!modified.Handled && handler.RemovalRequests == requests, "modified Delete does not delete a comment");
        comments.Close();
        form.Activate();
    }

    private static async Task TransferRemovalIntegrationAsync()
    {
        using var transfers = new YTPlayer.Forms.Download.DownloadManagerForm { StartPosition = FormStartPosition.Manual, Location = new(-32000, -32000), ShowInTaskbar = false };
        var downloads = YTPlayer.Core.Download.DownloadManager.Instance;
        var uploads = YTPlayer.Core.Upload.UploadManager.Instance;
        var downloadQueue = Field<List<YTPlayer.Models.Download.DownloadTask>>(downloads, "_completedQueue");
        var uploadQueue = Field<List<YTPlayer.Models.Upload.UploadTask>>(uploads, "_completedQueue");
        try
        {
            transfers.Show(form);
            var tabs = Field<TabControl>(transfers, "tabControl"); tabs.SelectedIndex = 2;
            var transferList = Field<ListView>(transfers, "lvCompleted");
            foreach (var (count, index) in new[] { (3, 0), (3, 1), (3, 2), (1, 0) })
            {
                downloadQueue.Clear(); uploadQueue.Clear();
                for (int i = 0; i < count; i++) downloadQueue.Add(new YTPlayer.Models.Download.DownloadTask { Song = Songs()[i], Status = YTPlayer.Models.Download.DownloadStatus.Completed });
                Method(transfers, "RefreshCompletedList");
                transferList.SelectedIndices.Clear(); transferList.Items[index].Selected = true; transferList.Items[index].Focused = true; transferList.Focus();
                var key = new KeyEventArgs(Keys.Delete); Method(transfers, "TransferListView_KeyDown", transferList, key);
                await Until(() => downloadQueue.Count == count - 1);
                Method(transfers, "RefreshCompletedList");
                Check(key.Handled && key.SuppressKeyPress, "transfer Delete uses the existing task-removal path");
                Check(transferList.SelectedIndices.Count == 1 && transferList.SelectedIndices[0] == Math.Max(0, Math.Min(index, count - 2)) && transferList.FocusedItem?.Index == transferList.SelectedIndices[0], "transfer removal keeps position " + index + "/" + count);
            }
            var pendingDownloads = Field<List<YTPlayer.Models.Download.DownloadTask>>(downloads, "_pendingQueue");
            var pendingUploads = Field<List<YTPlayer.Models.Upload.UploadTask>>(uploads, "_pendingQueue");
            foreach (string kind in new[] { "download", "upload", "completed-upload" })
            {
                for (int i = 0; i < 3; i++)
                {
                    if (kind == "download") pendingDownloads.Add(new YTPlayer.Models.Download.DownloadTask { Song = Songs()[i], Status = YTPlayer.Models.Download.DownloadStatus.Paused,
                        DestinationPath = Path.Combine(Environment.GetEnvironmentVariable("YTPLAYER_ROOT")!, "unused" + i + ".mp3") });
                    else
                    {
                        var upload = new YTPlayer.Models.Upload.UploadTask { FileName = "测试上传" + i, Status = kind == "upload" ? YTPlayer.Models.Upload.UploadStatus.Paused : YTPlayer.Models.Upload.UploadStatus.Completed };
                        (kind == "upload" ? pendingUploads : uploadQueue).Add(upload);
                    }
                }
                tabs.SelectedIndex = kind == "download" ? 0 : kind == "upload" ? 1 : 2;
                Method(transfers, "RefreshLists");
                var view = Field<ListView>(transfers, kind == "download" ? "lvActive" : kind == "upload" ? "lvUpload" : "lvCompleted");
                view.SelectedIndices.Clear(); view.Items[1].Selected = true; view.Items[1].Focused = true; view.Focus();
                var key = new KeyEventArgs(Keys.Delete); Method(transfers, "TransferListView_KeyDown", view, key);
                Method(transfers, "RefreshLists");
                Check(key.Handled && view.Items.Count == 2 && view.SelectedIndices.Count == 1 && view.SelectedIndices[0] == 1 && view.FocusedItem?.Index == 1, kind + " Delete removes its task and restores position");
                pendingDownloads.Clear(); pendingUploads.Clear(); uploadQueue.Clear();
            }
        }
        finally
        {
            downloadQueue.Clear(); uploadQueue.Clear(); transfers.Close();
            downloads.Dispose(); uploads.Dispose(); form.Activate();
        }
    }
}
