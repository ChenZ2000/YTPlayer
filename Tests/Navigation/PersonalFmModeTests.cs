using System.ComponentModel;
using System.Drawing;
using Newtonsoft.Json;
using YTPlayer.Core.Playback;
using YTPlayer.Models;
using YTPlayer.Models.Auth;

internal static partial class Program
{
    private static Dictionary<string, ToolStripMenuItem> FmModeItems =>
        Get<Dictionary<string, ToolStripMenuItem>>("_personalFmModeItems");

    private static void PrepareFmList()
    {
        Call("CancelViewContentOperation");
        Call("BeginViewContentOperation");
        Call("CancelNavigationFocusForUserInput");
        Set("_personalFmAppendInFlight", 1);
        try
        {
            var songs = Songs();
            Call("ResetPersonalFmCache", songs, 1);
            ShowSongs("personal_fm");
            Select(1);
            Get<Queue<SongInfo>>("_personalFmFetchedSongs").Clear();
        }
        finally { Set("_personalFmAppendInFlight", 0); }
    }

    private static void CheckFmSelection(string key)
    {
        Check(FmModeItems.Values.Count(item => item.Checked) == 1 && FmModeItems[key].Checked,
            key + " is the only checked mode across all submenus");
        Check((FmModeItems[key].AccessibilityObject.State & AccessibleStates.Checked) != 0,
            key + " exposes its checked state to screen readers");
    }

    private static async Task PersonalFmModesAsync()
    {
        Console.WriteLine("Personal FM: mode menus, buffers, queues, cancellation and rollback");
        var account = Get<AccountState?>("_accountState");
        long user = Get<long>("_loggedInUserId");
        var config = Get<ConfigModel>("_config");
        string savedMode = config.PersonalFmModeKey;
        var savedQueue = Get<PlaybackQueueManager?>("_playbackQueue");
        var savedCache = Get<List<SongInfo>>("_personalFmSongsCache").ToList();
        var contextMenu = Get<ContextMenuStrip>("songContextMenu");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool savedExpectError = expectRecentDeleteError;
        try
        {
            handler.FmModeFixtureEnabled = true;
            Set("_accountState", new AccountState { IsLoggedIn = true, UserId = "42" });
            Set("_loggedInUserId", 42L);
            config.PersonalFmModeKey = "DEFAULT";
            Set("_playbackQueue", new PlaybackQueueManager());
            var home = Call("BuildHomePageSkeletonViewData")!;
            var homeItems = (List<ListItemInfo>)home.GetType().GetProperty("Items")!.GetValue(home)!;
            int fmIndex = homeItems.FindIndex(item => item.CategoryId == "personal_fm");
            Check(fmIndex >= 0 && homeItems[fmIndex + 1].CategoryId == "user_cloud", "FM home entry immediately precedes cloud");
            PrepareFmList();
            var queue = Get<PlaybackQueueManager>("_playbackQueue");
            queue.ManualSelect(Songs()[0], Songs(), "personal_fm");
            contextMenu.Show(list, new Point(8, 8));
            var root = Get<ToolStripMenuItem>("_personalFmModeMenuItem");
            Check(contextMenu.Visible && root.Available && root.Enabled, "FM context exposes an enabled mode submenu");
            Check(FmModeItems.Count == 46, "all 46 supported choices have menu entries");
            CheckFmSelection("DEFAULT");
            root.ShowDropDown();
            Check(root.DropDown.Visible && ((ToolStripDropDownMenu)root.DropDown).ShowCheckMargin, "mode submenu opens with native check margin");
            var scene = (ToolStripMenuItem)FmModeItems["SCENE_RCMD:EXERCISE"].OwnerItem!;
            scene.ShowDropDown();
            Check(scene.DropDown.Visible && FmModeItems["SCENE_RCMD:EXERCISE"].Available, "scene submenu is navigable");
            contextMenu.Close();

            foreach (PersonalFmMode mode in PersonalFmMode.All)
            {
                config.PersonalFmModeKey = mode.Key;
                Call("UpdatePersonalFmModeMenu", true);
                CheckFmSelection(mode.Key);
            }
            Check(FmModeItems["SCENE_RCMD:RHYTHM_BLUES"].Text == "R&&B"
                && FmModeItems["SCENE_RCMD:RHYTHM_BLUES"].AccessibleName == "R&B", "R&B label is not parsed as an access key");
            config.PersonalFmModeKey = "DEFAULT";
            Call("UpdatePersonalFmModeMenu", true);
            int before = handler.FmModeRequests;
            FmModeItems["FAMILIAR"].PerformClick();
            await Until(() => Get<int>("_personalFmModeSwitchInFlight") == 0);
            Check(handler.FmModeRequests == before + 1 && config.PersonalFmModeKey == "FAMILIAR", "click requests and commits selected mode exactly once");
            CheckFmSelection("FAMILIAR");
            Check(Get<List<SongInfo>>("_currentSongs").Count == 6 && list.Items.Count == 6, "successful switch appends the entire response");
            Check(FocusSelection == 1 && list.FocusedItem?.Index == 1, "switch preserves original list selection and focused item");
            var snapshot = queue.CaptureSnapshot();
            Check(snapshot.Queue.Count == 6 && snapshot.QueueIndex == 0 && snapshot.Queue[0].Id == "101", "switch extends playback queue without changing the playing song");
            Check(snapshot.Queue.Skip(3).All(song => song.RecommendationAlgorithm == "FAMILIAR:"), "appended songs retain the selected mode's feedback algorithm");
            Check(JsonConvert.DeserializeObject<ConfigModel>(JsonConvert.SerializeObject(config))!.PersonalFmModeKey == "FAMILIAR", "selected mode survives configuration serialization");
            await Run("SwitchPersonalFmModeAsync", PersonalFmMode.FromKey("FAMILIAR"));
            Check(handler.FmModeRequests == before + 1, "reselecting the active mode sends no request");

            before = handler.FmModeRequests;
            await Run("AppendPersonalFmSongsToCacheAndQueueAsync", 2, CancellationToken.None);
            Check(handler.FmModeRequests == before + 1 && Get<List<SongInfo>>("_currentSongs").Count == 8,
                "automatic continuation uses one selected-mode batch for two songs");
            await Run("AppendPersonalFmSongsToCacheAndQueueAsync", 1, CancellationToken.None);
            Check(handler.FmModeRequests == before + 1 && Get<List<SongInfo>>("_currentSongs").Count == 9,
                "unused response song is buffered and reused without an extra request");
            Check(handler.FmModeBodies.Last().Value<string>("mode") == "FAMILIAR", "continuation requests preserve current mode");
            handler.FmModeEmpty = true;
            before = handler.FmModeRequests;
            await Run("AppendPersonalFmSongsToCacheAndQueueAsync", 1, CancellationToken.None);
            Check(handler.FmModeRequests - before <= 4 && list.Items.Count == 9,
                "empty recommendation batches cannot trigger nested retries or add placeholder songs");
            handler.FmModeEmpty = false;

            expectRecentDeleteError = true;
            int errorCount = recentDeleteErrorCount;
            handler.FmModeCode = 500;
            await Run("SwitchPersonalFmModeAsync", PersonalFmMode.FromKey("EXPLORE"));
            handler.FmModeCode = 200;
            handler.FmModeEmpty = true;
            await Run("SwitchPersonalFmModeAsync", PersonalFmMode.FromKey("EXPLORE"));
            handler.FmModeEmpty = false;
            Check(recentDeleteErrorCount == errorCount + 2 && config.PersonalFmModeKey == "FAMILIAR"
                && list.Items.Count == 9 && queue.CaptureSnapshot().Queue.Count == 9, "failed and empty switches preserve mode, list and queue");
            CheckFmSelection("FAMILIAR");
            expectRecentDeleteError = savedExpectError;

            handler.FmModeGate = gate;
            before = handler.FmModeRequests;
            Task switchTask = Run("SwitchPersonalFmModeAsync", PersonalFmMode.FromKey("EXPLORE"));
            await Until(() => handler.FmModeRequests == before + 1);
            Check(!root.Enabled && config.PersonalFmModeKey == "FAMILIAR", "pending switch disables its menu and retains committed mode");
            await Run("SwitchPersonalFmModeAsync", PersonalFmMode.FromKey("PUZZLE_MODE_RCMD"));
            Check(handler.FmModeRequests == before + 1, "rapid repeated selection cannot overlap requests");
            handler.FmModeGate = null;
            gate.TrySetResult();
            await switchTask;
            CheckFmSelection("EXPLORE");

            // A transport may deliver an old reply after Cancel. It must not commit any rows.
            gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            handler.FmModeGate = gate;
            Get<Queue<SongInfo>>("_personalFmFetchedSongs").Clear();
            before = handler.FmModeRequests;
            Task appendTask = Run("AppendPersonalFmSongsToCacheAndQueueAsync", 2, CancellationToken.None);
            await Until(() => handler.FmModeRequests == before + 1);
            int rows = list.Items.Count;
            handler.FmModeGate = null;
            await Run("SwitchPersonalFmModeAsync", PersonalFmMode.FromKey("SCENE_RCMD:FOCUS"));
            gate.TrySetResult();
            await appendTask;
            Check(list.Items.Count == rows + 3 && Get<List<SongInfo>>("_personalFmSongsCache").Count == rows + 3,
                "late old-mode append cannot change cache or visible list");
            Check(Get<List<SongInfo>>("_currentSongs").Skip(rows).All(song => song.RecommendationAlgorithm == "SCENE_RCMD:FOCUS")
                && queue.CaptureSnapshot().Queue.Count == rows + 3, "only new scene songs are committed to the queue");
            CheckFmSelection("SCENE_RCMD:FOCUS");

            gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            handler.FmModeGate = gate;
            before = handler.FmModeRequests;
            switchTask = Run("SwitchPersonalFmModeAsync", PersonalFmMode.FromKey("PUZZLE_MODE_RCMD"));
            await Until(() => handler.FmModeRequests == before + 1);
            Call("CancelViewContentOperation");
            ShowSongs("album:42"); Select(1);
            handler.FmModeGate = null;
            gate.TrySetResult();
            await switchTask;
            Check(config.PersonalFmModeKey == "SCENE_RCMD:FOCUS" && Source == "album:42" && list.Items.Count == 3
                && FocusSelection == 1 && !root.Available, "leaving during a switch does not change preferences or the new page");
            contextMenu.Show(list, new Point(8, 8));
            Check(!root.Available, "non-FM song menu hides the mode entry");
            contextMenu.Close();

            PrepareFmList();
            Set("_personalFmAppendInFlight", 1);
            for (int i = 0; i < 3; i++)
            {
                Select(i);
                contextMenu.Show(list, new Point(8, 8));
                Check(root.Available && root.Enabled, "every FM song exposes the mode menu: " + i);
                contextMenu.Close();
            }
            Set("_personalFmAppendInFlight", 0);
            Call("ResetPersonalFmCache", null, -1);
            Call("ShowListRetryPlaceholderCore", "personal_fm", "私人 FM", "私人 FM", false, false);
            var opening = new CancelEventArgs();
            Call("songContextMenu_Opening", contextMenu, opening);
            Check(!opening.Cancel && root.Available && contextMenu.Items.Cast<ToolStripItem>().Count(item => item.Available) == 1,
                "empty or failed FM list still exposes a mode-only recovery menu");
            Call("songContextMenu_Closed", contextMenu, new ToolStripDropDownClosedEventArgs(ToolStripDropDownCloseReason.CloseCalled));
            await Run("SwitchPersonalFmModeAsync", PersonalFmMode.FromKey("SCENE_RCMD:EXERCISE"));
            Check(list.Items.Count == 3 && Get<List<SongInfo>>("_currentSongs").Count == 3
                && Get<List<SongInfo>>("_currentSongs").All(song => song.RecommendationAlgorithm == "SCENE_RCMD:EXERCISE"),
                "switching mode recovers an empty list with valid new songs");
            contextMenu.Show(list, new Point(8, 8));
            Check(contextMenu.Items.Cast<ToolStripItem>().Count(item => item.Available) > 1, "normal song actions are restored after mode-only menu closes");
            contextMenu.Close();
            CheckFmSelection("SCENE_RCMD:EXERCISE");

            Call("ResetPersonalFmSession");
            gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            handler.FmModeGate = gate;
            before = handler.FmModeRequests;
            Task initialLoad = Run("LoadPersonalFm", -1);
            await Until(() => handler.FmModeRequests == before + 1);
            handler.FmModeGate = null;
            await Run("SwitchPersonalFmModeAsync", PersonalFmMode.FromKey("FAMILIAR"));
            gate.TrySetResult();
            await initialLoad;
            Check(list.Items.Count == 3 && Get<List<SongInfo>>("_personalFmSongsCache").Count == 3
                && Get<List<SongInfo>>("_currentSongs").All(song => song.RecommendationAlgorithm == "FAMILIAR:"),
                "superseded initial load cannot replace the switched mode's rows");
            Check(Get<ToolStripStatusLabel>("toolStripStatusLabel1").Text?.Contains("熟悉模式") == true,
                "late initial-load cancellation cannot overwrite the new mode status");

            gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            handler.FmModeGate = gate;
            before = handler.FmModeRequests;
            appendTask = Run("AppendPersonalFmSongsToCacheAndQueueAsync", 1, CancellationToken.None);
            await Until(() => handler.FmModeRequests == before + 1);
            Set("_accountState", new AccountState { IsLoggedIn = true, UserId = "43" });
            Call("CacheLoggedInUserId", 43L);
            ShowSongs("album:42");
            handler.FmModeGate = null;
            gate.TrySetResult();
            await appendTask;
            Check(Get<List<SongInfo>>("_personalFmSongsCache").Count == 0 && Get<Queue<SongInfo>>("_personalFmFetchedSongs").Count == 0,
                "account switch cancels pending recommendations and clears previous session buffers");
            Check(Source == "album:42" && list.Items.Count == 3, "account switch's late reply cannot alter the current page");

            using var disposableForm = new YTPlayer.MainForm(initializeRuntime: false);
            disposableForm.Dispose();
            disposableForm.Dispose();
            Check(disposableForm.IsDisposed, "FM request cleanup allows repeated form disposal");
        }
        finally
        {
            gate.TrySetResult(); handler.FmModeGate = null;
            handler.FmModeCode = 200; handler.FmModeEmpty = false; handler.FmModeFixtureEnabled = false;
            expectRecentDeleteError = savedExpectError;
            contextMenu.Close();
            Call("CancelViewContentOperation");
            Call("RestorePersonalFmMenuItems");
            ShowSongs("album:42");
            Set("_accountState", account); Set("_loggedInUserId", user);
            Set("_playbackQueue", savedQueue);
            config.PersonalFmModeKey = savedMode;
            Get<Queue<SongInfo>>("_personalFmFetchedSongs").Clear();
            Call("ResetPersonalFmCache", savedCache, -1);
            Set("_personalFmAppendInFlight", 0);
            Call("UpdatePersonalFmModeMenu", false);
        }
    }
}
