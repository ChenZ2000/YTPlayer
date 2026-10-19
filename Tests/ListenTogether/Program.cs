using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using YTPlayer.Core;
using YTPlayer.Core.ListenTogether;

internal static class Program
{
    private static int _passed;
    [STAThread]
    private static int Main(string[] args)
    {
        // All production auth/config persistence is redirected into disposable test output.
        string root = Path.Combine(AppContext.BaseDirectory, "test-data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("YTPLAYER_ROOT", root);
        try
        {
            if (args.Length > 0 && args[0] == "--worker") DualAccountProbe.WorkerAsync(args[1]).GetAwaiter().GetResult();
            else if (args.Length > 0 && args[0] == "--dual") DualAccountProbe.RunAsync(args.Skip(1).ToArray()).GetAwaiter().GetResult();
            else if (args.Length > 0 && args[0] == "--diagnose") WireProbe.RunAsync(args.Skip(1).ToArray()).GetAwaiter().GetResult();
            else if (args.Length > 0 && args[0] == "--live") LiveProbe.RunAsync(args).GetAwaiter().GetResult();
            else
            {
                Invitations();
                DialogControls();
                SynchronizationContext.SetSynchronizationContext(null);
                RunAsync().GetAwaiter().GetResult();
                Console.WriteLine($"PASS: {_passed} assertions; no account file or live network used.");
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex.GetType().Name + ": " + ex.Message); return 1; }
        finally { Directory.Delete(root, recursive: true); }
    }

    internal static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        _passed++;
    }

    private static async Task ThrowsAsync<T>(Func<Task> action, string name) where T : Exception
    {
        try { await action(); }
        catch (T) { Check(true, name); return; }
        throw new Exception("Expected " + typeof(T).Name + ": " + name);
    }

    private static void Invitations()
    {
        var invite = new ListenTogetherInvitation("room_abc-123", "123456", "186016");
        var parsed = ListenTogetherInvitation.Parse("来一起听！ " + invite + "。 ");
        Check(parsed.RoomId == invite.RoomId && parsed.InviterId == invite.InviterId && parsed.SongId == invite.SongId, "official share text round trip");
        Check(ListenTogetherInvitation.Parse("https://st.music.163.com/listen-together/share/?roomId=abc%2D123&inviterId=12").RoomId == "abc-123", "encoded room id");
        foreach (string url in new[] {
            "http://192.168.1.2:8080/ytplayer-together/?token=x",
            "https://st.music.163.com.evil.example/listen-together/share/?roomId=x&inviterId=1",
            "https://evil.example/?roomId=x&inviterId=1", "https://st.music.163.com/listen-together/share/?roomId=x",
            "https://st.music.163.com/listen-together/share/?roomId=x&inviterId=1&roomId=y",
            "https://user@st.music.163.com/listen-together/share/?roomId=x&inviterId=1" })
        {
            try { ListenTogetherInvitation.Parse(url); throw new Exception("Invalid link accepted"); }
            catch (FormatException) { Check(true, "reject malformed or non-official invite"); }
        }
    }

    private static void DialogControls()
    {
        using var dialog = new YTPlayer.ListenTogetherDialog();
        static IEnumerable<System.Windows.Forms.Control> All(System.Windows.Forms.Control c) =>
            c.Controls.Cast<System.Windows.Forms.Control>().SelectMany(x => new[] { x }.Concat(All(x)));
        var buttons = All(dialog).OfType<System.Windows.Forms.Button>().ToArray();
        Check(buttons.All(b => !string.IsNullOrWhiteSpace(b.AccessibleName)), "buttons have accessible names");
        Check(All(dialog).All(c => !c.Text.Contains("官方") && !(c.AccessibleName?.Contains("官方") ?? false)), "together UI omits official qualifier");
        dialog.SetSession(false, false, true, "");
        Check(buttons.Where(b => b.DialogResult != System.Windows.Forms.DialogResult.Cancel).All(b => !b.Enabled), "pending connection disables duplicate actions");
        Check(((System.Windows.Forms.Button)dialog.CancelButton!).Enabled, "close remains enabled during connection");
        dialog.SetSession(true, false, false, "https://st.music.163.com/listen-together/share/?roomId=x&inviterId=1");
        Check(buttons.Single(b => b.Text.Contains("加入")).Enabled == false, "join disabled only after connection");
        Check(buttons.Single(b => b.Text.Contains("退出房间")).Enabled, "guest can leave");
        dialog.SetSession(false, false, false, "");
        dialog.StartPosition = System.Windows.Forms.FormStartPosition.Manual;
        dialog.Location = new System.Drawing.Point(-32000, -32000);
        dialog.Show(); System.Windows.Forms.Application.DoEvents();
        using var bitmap = new System.Drawing.Bitmap(dialog.Width, dialog.Height);
        dialog.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height));
        bitmap.Save(Path.Combine(AppContext.BaseDirectory, "listen-together-dialog.png"));
        dialog.Hide();
        foreach (bool busy in new[] { false, true })
        {
            using var closing = new YTPlayer.ListenTogetherDialog();
            int exits = 0;
            closing.StopHostingRequested += (_, _) => exits++;
            closing.DisconnectRequested += (_, _) => exits++;
            closing.SetSession(!busy, false, busy, "fixture");
            closing.SetPlaybackSummary("当前歌曲：测试歌曲\r\n播放状态：已暂停；进度：00:42 / 03:00");
            var summary = All(closing).OfType<System.Windows.Forms.TextBox>().Single(t => t.AccessibleName == "房间播放状态");
            Check(summary.ReadOnly && summary.Text.Contains("00:42"), "room summary exposes current playback read-only");
            closing.StartPosition = System.Windows.Forms.FormStartPosition.Manual;
            closing.Location = new System.Drawing.Point(-32000, -32000);
            closing.Show(); summary.Focus(); System.Windows.Forms.Application.DoEvents();
            var handled = typeof(System.Windows.Forms.Form).GetMethod("ProcessDialogKey", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(closing, new object[] { System.Windows.Forms.Keys.Escape });
            Check(Equals(handled, true) && closing.IsDisposed && exits == 0, "Escape closes modeless dialog without leaving or cancelling room");
        }
    }

    private static async Task RunAsync()
    {
        int redirects = 0;
        var shortInvite = await ListenTogetherInvitation.ResolveAsync("一起听 http://163cn.tv/fixture。", default, (uri, _) =>
        {
            redirects++;
            Check(uri.Scheme == "https", "short links upgraded to https");
            return Task.FromResult<Uri?>(new Uri("https://st.music.163.com/listen-together/share/?roomId=fixture&inviterId=123"));
        });
        Check(redirects == 1 && shortInvite.RoomId == "fixture", "official short link expanded");
        redirects = 0;
        await ThrowsAsync<FormatException>(() => ListenTogetherInvitation.ResolveAsync("https://163cn.tv/fixture", default, (_, _) =>
        {
            redirects++; return Task.FromResult<Uri?>(new Uri("https://example.com/private"));
        }), "foreign redirect rejected");
        Check(redirects == 1, "no request sent to foreign redirect target");
        redirects = 0;
        await ThrowsAsync<FormatException>(() => ListenTogetherInvitation.ResolveAsync("https://163cn.tv/fixture", default, (uri, _) =>
        {
            redirects++; return Task.FromResult<Uri?>(uri);
        }), "redirect loop bounded");
        Check(redirects == 5, "maximum redirect hop count");
        foreach (int code in new[] { 301, 491, 500 })
        {
            var bad = new ListenTogetherApi((_, _, _) => Task.FromResult(new JObject { ["code"] = code, ["message"] = "fixture" }));
            try { await bad.CreateAsync(default); throw new Exception("Error response accepted"); }
            catch (ListenTogetherApiException e) { Check(e.Code == code, "preserve server error code"); }
        }
        foreach (string flag in new[] { "success", "result" })
        {
            var bad = new ListenTogetherApi((_, _, _) => Task.FromResult(Ok(new JObject { [flag] = false })));
            await ThrowsAsync<ListenTogetherApiException>(() => bad.EndAsync("r", default), "code200 false must fail");
        }
        var missing = new ListenTogetherApi((_, _, _) => Task.FromResult(Ok(new JObject())));
        await ThrowsAsync<ListenTogetherApiException>(() => missing.StatusAsync(default), "missing status cannot authorize a new room");
        await ThrowsAsync<ListenTogetherApiException>(() => missing.EndAsync("fixture", default), "missing exit confirmation is not success");
        await TransportAsync();
        await SessionAsync();
        await DriftAndCancellationAsync();
        await PlaybackUiTests.RunAsync();
        await LocalInteractionAsync();
    }

    internal static JObject Ok(JObject data) => new JObject { ["code"] = 200, ["data"] = data };

    private static async Task TransportAsync()
    {
        byte[] json = Encoding.UTF8.GetBytes("{\"code\":200,\"data\":{\"roomInfo\":{\"roomId\":\"fixture\"},\"text\":\"一起听\"}}");
        static byte[] Encrypt(byte[] b)
        {
            using var aes = Aes.Create();
            aes.Key = Encoding.ASCII.GetBytes("e82ckenh8dichen8");
            return aes.EncryptEcb(b, PaddingMode.PKCS7);
        }
        static byte[] Compress(byte[] b, bool brotli)
        {
            using var output = new MemoryStream();
            using (Stream compressor = brotli ? new BrotliStream(output, CompressionLevel.Optimal, true)
                : new GZipStream(output, CompressionLevel.Optimal, true)) compressor.Write(b);
            return output.ToArray();
        }
        foreach (var (bytes, encoding, name) in new[] {
            (json, "", "plain JSON"), (Encrypt(json), "", "raw ciphertext"),
            (Compress(Encrypt(json), true), "br", "Brotli compressed ciphertext"),
            (Compress(Encrypt(json), false), "gzip", "gzip compressed ciphertext"),
            (Compress(json, true), "br", "compressed plain JSON"),
            (Encrypt(Compress(json, false)), "", "encrypted compressed JSON") })
        {
            using var client = new NeteaseApiClient();
            var field = typeof(NeteaseApiClient).GetField("_eapiClient", BindingFlags.NonPublic | BindingFlags.Instance)!;
            ((HttpClient)field.GetValue(client)!).Dispose();
            var handler = new FixtureHandler(bytes, encoding);
            field.SetValue(client, new HttpClient(handler));
            var result = await client.PostEApiAsync<JObject>("/api/listen/together/room/create", new { refer = "songplay_more" }, retryCount: 4, skipErrorHandling: true);
            Check(result.Value<int>("code") == 200 && result["data"]?.Value<string>("text") == "一起听", name);
            Check(handler.Count == 1, "room mutation is not retried");
        }
        using var authenticated = new NeteaseApiClient();
        authenticated.SetCookieString("MUSIC_U=fixture-not-a-real-session; __csrf=fixture; extra_session=a%2Fb");
        var transportField = typeof(NeteaseApiClient).GetField("_eapiClient", BindingFlags.NonPublic | BindingFlags.Instance)!;
        ((HttpClient)transportField.GetValue(authenticated)!).Dispose();
        var denied = new FixtureHandler(Encoding.UTF8.GetBytes("{\"code\":491,\"message\":\"fixture\"}"), "");
        transportField.SetValue(authenticated, new HttpClient(denied));
        await ThrowsAsync<ListenTogetherApiException>(() => authenticated.CreateListenTogetherApi().CreateAsync(default), "adapter preserves rejection");
        Check(denied.Count == 1, "rejected create never automatically retried");
        Check(denied.Cookie.Contains("extra_session=a%2Fb") && !denied.Cookie.Contains("a%252Fb"), "full saved cookies not double encoded");
        string payload = YTPlayer.Utils.EncryptionHelper.DecryptEapi(Convert.FromHexString(denied.EncryptedPayload));
        var request = JObject.Parse(payload.Split("-36cd479b6b5-")[1]);
        Check(request["header"]?.Value<string>("extra_session") == "a%2Fb", "encrypted header retains extra session cookies");
        var status = new FixtureHandler(Compress(Encrypt(Encoding.UTF8.GetBytes("{\"code\":200,\"data\":{\"inRoom\":false}}")), true), "br");
        ((HttpClient)transportField.GetValue(authenticated)!).Dispose();
        transportField.SetValue(authenticated, new HttpClient(status));
        Check((await authenticated.CreateListenTogetherApi().StatusAsync(default)).Value<bool?>("inRoom") == false, "compressed EAPI room status decoded");
        Check(status.Path == "/eapi/listen/together/status/get", "room status never uses empty-response WEAPI route");
    }

    private static async Task SessionAsync()
    {
        var fake = new FakeRoom();
        var session = new ListenTogetherSession(fake.Api, "123", () => fake.Now);
        ListenTogetherLocalState local = Local("186016", 0, true, fake.Now);
        int applied = 0;
        Task Apply(ListenTogetherRemoteState update, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); applied++;
            if (update.Playback != null) local.Playback = update.Playback;
            if (update.Queue != null) local.Queue = update.Queue;
            if (update.PlayMode != null) local.PlayMode = update.PlayMode;
            return Task.CompletedTask;
        }
        ListenTogetherLocalState Capture() => Local(local.Playback.SongId, local.Playback.PositionSeconds,
            local.Playback.IsPlaying, fake.Now, local.Queue, local.PlayMode);
        await session.StartAsync(null, Capture, Apply, default);
        Check(session.IsActive && session.IsHost, "host active after successful create");
        Check(fake.Calls.Count(x => x.Path.EndsWith("room/create")) == 1, "create once");
        var sent = JObject.Parse(fake.Calls.Last(x => x.Path.EndsWith("play/command/report")).Data.Value<string>("commandInfo")!);
        Check(sent.Value<string>("playStatus") == "PLAY" && sent.Value<long>("progress") == 0, "initial playback published");
        await session.TickAsync(Capture, Apply, default);
        Check(applied == 0, "own acknowledgment is not replayed");

        fake.Now += 2000; local.Playback.PositionSeconds = 2;
        int commands = fake.Commands;
        await session.TickAsync(Capture, Apply, default);
        Check(fake.Commands == commands, "normal playback does not flood commands");
        local.Playback.IsPlaying = false;
        await session.TickAsync(Capture, Apply, default);
        Check(fake.Command.Value<string>("commandType") == "PAUSE", "local pause published");
        local.Playback.PositionSeconds = 42.125;
        await session.TickAsync(Capture, Apply, default);
        Check(fake.Command.Value<string>("commandType") == "seek" && fake.Command.Value<long>("progress") == 42125, "seek uses milliseconds");
        local.Playback.SongId = "347230";
        await session.TickAsync(Capture, Apply, default);
        Check(fake.Command.Value<string>("commandType") == "GOTO", "track change published");

        fake.Now += 10000;
        fake.Command = FakeRoom.Remote("186016", true, fake.Now - 3000, 10000);
        commands = fake.Commands;
        await session.TickAsync(Capture, Apply, default);
        Check(applied > 0 && local.Playback.SongId == "186016", "remote track applied");
        Check(Math.Abs(local.Playback.PositionSeconds - 13) < 0.01, "server time advances playing position");
        Check(fake.Commands == commands, "remote command is not echoed");
        int before = applied;
        await session.TickAsync(Capture, Apply, default);
        Check(applied == before, "duplicate server sequence ignored");
        fake.Command["serverSeq"] = fake.Now - 5000;
        await session.TickAsync(Capture, Apply, default);
        Check(applied == before, "out-of-order server sequence ignored");
        fake.Now += 2000;
        fake.Command = FakeRoom.Remote("186016", false, fake.Now, 45000);
        await session.TickAsync(Capture, Apply, default);
        Check(!local.Playback.IsPlaying && local.Playback.PositionSeconds == 45, "remote pause does not extrapolate");
        fake.Now += 2000;
        fake.Command = FakeRoom.Remote("186016", true, fake.Now, 46000);
        await ThrowsAsync<InvalidOperationException>(() => session.TickAsync(Capture,
            (_, _) => throw new InvalidOperationException("audio unavailable"), default), "failed playback application remains retryable");
        await session.TickAsync(Capture, Apply, default);
        Check(local.Playback.IsPlaying && local.Playback.PositionSeconds == 46, "same failed remote state retried");

        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        int calls = fake.Calls.Count;
        await ThrowsAsync<OperationCanceledException>(() => session.TickAsync(Capture, Apply, cancel.Token), "cancelled tick");
        Check(fake.Calls.Count == calls, "no network after cancellation");
        fake.FailEnd = true;
        await ThrowsAsync<ListenTogetherApiException>(() => session.LeaveAsync(default), "failed exit");
        Check(session.IsActive, "failed exit can be retried");
        fake.FailEnd = false; await session.LeaveAsync(default);
        Check(!session.IsActive && !fake.InRoom, "room leaves successfully");
        calls = fake.Calls.Count; await session.TickAsync(Capture, Apply, default);
        Check(fake.Calls.Count == calls, "no polling after leave");

        fake = new FakeRoom { InRoom = true };
        session = new ListenTogetherSession(fake.Api, "123", () => fake.Now);
        await ThrowsAsync<InvalidOperationException>(() => session.StartAsync(null, Capture, Apply, default), "existing account room preserved");
        Check(!fake.Calls.Any(x => x.Path.EndsWith("room/create") || x.Path.EndsWith("end/v2")), "do not mutate preexisting room");

        fake = new FakeRoom();
        fake.Command = FakeRoom.Remote("186016", false, fake.Now, 12000);
        local = Local("", 0, false, fake.Now, Array.Empty<string>(), "RANDOM");
        session = new ListenTogetherSession(fake.Api, "123", () => fake.Now);
        await session.StartAsync(new ListenTogetherInvitation("fixture", "456"), Capture, Apply, default);
        Check(!session.IsHost && local.Playback.PositionSeconds == 12, "joining consumes remote state");
        Check(local.PlayMode == "ORDER_LOOP", "first room snapshot replaces guest play mode even when server uses default mode");
        Check(fake.Calls.Where(x => x.Path.EndsWith("heartbeat")).All(x => x.Data.Value<string>("songId") == "186016"), "join heartbeat always uses room song, never -1");
        Check(fake.Commands == 0 && !fake.Calls.Any(x => x.Path.EndsWith("sync/list/command/report")), "join never overwrites host queue or playback");
        fake.InRoom = false; fake.Now += 2000;
        await session.TickAsync(Capture, Apply, default);
        Check(session.IsReady, "initial membership propagation does not prematurely close joined room");
        fake.InRoom = true; fake.Members = 2; fake.Now += 5000;
        await session.TickAsync(Capture, Apply, default);
        Check(session.StatusText.Contains("2 人"), "membership refresh exposes both room members");
        fake.Members = 1; fake.Now += 5000;
        await session.TickAsync(Capture, Apply, default);
        Check(session.StatusText.Contains("等待对方加入"), "peer leaving refreshes waiting status");
        fake.InRoom = false; fake.FailPlaylist = true; fake.Now += 5000;
        await ThrowsAsync<ListenTogetherRoomClosedException>(() => session.TickAsync(Capture, Apply, default), "confirmed membership loss detected before failing playlist request");

        fake = new FakeRoom();
        fake.Command = FakeRoom.Remote("186016", false, fake.Now, 12000);
        local = Local("", 0, false, fake.Now, Array.Empty<string>());
        session = new ListenTogetherSession(fake.Api, "123", () => fake.Now);
        await session.StartAsync(new ListenTogetherInvitation("fixture", "456"), Capture, Apply, default);
        fake.InRoom = false; fake.FailPlaylist = true; fake.Now += 20000;
        await ThrowsAsync<ListenTogetherRoomClosedException>(() => session.TickAsync(Capture, Apply, default), "server ended room detected");

        fake = new FakeRoom();
        fake.Command = FakeRoom.Remote("186016", true, fake.Now - 90000, 12000);
        local = Local("", 0, false, fake.Now, Array.Empty<string>());
        session = new ListenTogetherSession(fake.Api, "123", () => fake.Now);
        await session.StartAsync(new ListenTogetherInvitation("fixture", "456"), Capture, Apply, default);
        Check(Math.Abs(local.Playback.PositionSeconds - 102) < .01, "first join extrapolates old playing command with server clock");

        fake = new FakeRoom();
        local = Local("", 0, false, fake.Now, Array.Empty<string>());
        session = new ListenTogetherSession(fake.Api, "123", () => fake.Now);
        await session.StartAsync(new ListenTogetherInvitation("fixture", "456"), Capture, Apply, default);
        await session.TickAsync(Capture, Apply, default);
        Check(!fake.Calls.Any(x => x.Path.EndsWith("heartbeat")), "waiting for first room command does not send an invalid heartbeat");

        fake = new FakeRoom { Joinable = false };
        session = new ListenTogetherSession(fake.Api, "123", () => fake.Now);
        await ThrowsAsync<InvalidOperationException>(() => session.StartAsync(new ListenTogetherInvitation("fixture", "456"), Capture, Apply, default), "invalid invitation");
        Check(!session.IsActive && !fake.Calls.Any(x => x.Path.EndsWith("invitation/accept")), "failed join never reports connected");
    }

    internal static ListenTogetherLocalState Local(string id, double position, bool playing, long now, string[]? queue = null, string mode = "ORDER_LOOP") => new()
    {
        Playback = new() { SongId = id, PositionSeconds = position, IsPlaying = playing, UpdatedUnixMilliseconds = now },
        Queue = queue ?? new[] { id }, PlayMode = mode
    };

    private static async Task DriftAndCancellationAsync()
    {
        var fake = new FakeRoom();
        var session = new ListenTogetherSession(fake.Api, "123", () => fake.Now);
        var local = Local("186016", 0, true, fake.Now); local.SeekVersion = 0;
        ListenTogetherLocalState Capture()
        {
            var value = Local(local.Playback.SongId, local.Playback.PositionSeconds, local.Playback.IsPlaying, fake.Now, local.Queue);
            value.SeekVersion = local.SeekVersion; value.Playback.IsBuffering = local.Playback.IsBuffering; return value;
        }
        int corrections = 0;
        Task Apply(ListenTogetherRemoteState update, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (update.Playback != null) { local.Playback = update.Playback; corrections++; }
            return Task.CompletedTask;
        }
        await session.StartAsync(null, Capture, Apply, default);
        await session.TickAsync(Capture, Apply, default);
        int commands = fake.Commands;
        fake.Now += 5000; local.Playback.IsBuffering = true;
        await session.TickAsync(Capture, Apply, default);
        Check(fake.Commands == commands && corrections == 0, "buffer stall does not seek peers backwards");
        local.Playback.IsBuffering = false;
        await session.TickAsync(Capture, Apply, default);
        Check(corrections == 1 && local.Playback.PositionSeconds == 5, "buffer recovery corrects to room clock");
        Check(fake.Commands == commands, "drift correction does not echo");
        local.Playback.PositionSeconds = 30; local.SeekVersion++;
        await session.TickAsync(Capture, Apply, default);
        Check(fake.Commands == commands + 1 && fake.Command.Value<long>("progress") == 30000, "explicit user seek is published");

        fake.Now += 2000;
        fake.Command = FakeRoom.Remote("186016", true, fake.Now - 1000, 31000);
        local.Playback.IsPlaying = false;
        await session.TickAsync(Capture, Apply, default);
        Check(!local.Playback.IsPlaying && fake.Command.Value<string>("playStatus") == "PAUSE", "incoming earlier play cannot erase pending local pause");

        fake.Now += 2000;
        fake.Command = FakeRoom.Remote("186016", false, fake.Now - 1000, 32000);
        local.Playback.PositionSeconds = 60; local.SeekVersion++;
        await session.TickAsync(Capture, Apply, default);
        Check(local.Playback.PositionSeconds == 60 && fake.Command.Value<long>("progress") == 60000, "incoming earlier seek cannot erase pending local seek");

        bool delay = false;
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayedApi = new ListenTogetherApi(async (path, data, token) =>
        {
            if (delay && path.EndsWith("sync/playlist/get"))
            {
                arrived.SetResult(); await release.Task;
                return Ok(new JObject { ["playCommand"] = FakeRoom.Remote("347230", true, fake.Now + 100, 0) });
            }
            return await fake.Send(path, data, token);
        });
        fake.InRoom = false;
        session = new ListenTogetherSession(delayedApi, "123", () => fake.Now);
        await session.StartAsync(null, Capture, Apply, default);
        delay = true;
        using var cancel = new CancellationTokenSource();
        int previousCorrections = corrections;
        Task tick = session.TickAsync(Capture, Apply, cancel.Token);
        await arrived.Task; cancel.Cancel(); release.SetResult();
        await ThrowsAsync<OperationCanceledException>(() => tick, "late response after disconnect cancelled");
        Check(corrections == previousCorrections, "late response never changes playback");
    }

    private static async Task LocalInteractionAsync()
    {
        var fake = new FakeRoom();
        var session = new ListenTogetherSession(fake.Api, "123", () => fake.Now);
        var local = Local("186016", 0, true, fake.Now); local.SeekVersion = 0;
        int applications = 0;
        ListenTogetherLocalState Capture() => new()
        {
            Playback = new() { SongId = local.Playback.SongId, IsPlaying = local.Playback.IsPlaying,
                PositionSeconds = local.Playback.EstimatePositionSeconds(fake.Now), UpdatedUnixMilliseconds = fake.Now },
            Queue = local.Queue.ToArray(), PlayMode = local.PlayMode, SeekVersion = local.SeekVersion,
            IsChangingPlayback = local.IsChangingPlayback
        };
        Task Apply(ListenTogetherRemoteState update, CancellationToken token)
        {
            applications++;
            if (update.Playback != null) local.Playback = update.Playback;
            if (update.Queue != null) local.Queue = update.Queue;
            if (update.PlayMode != null) local.PlayMode = update.PlayMode;
            return Task.CompletedTask;
        }
        local.IsChangingPlayback = true;
        await ThrowsAsync<InvalidOperationException>(() => session.StartAsync(null, Capture, Apply, default), "cannot create while local playback is changing");
        Check(!fake.Calls.Any(x => x.Path.EndsWith("room/create")), "busy create does not create an orphan room");
        local.IsChangingPlayback = false;
        await session.StartAsync(null, Capture, Apply, default);
        int commands = fake.Commands;
        local.IsChangingPlayback = true;
        local.Playback.IsPlaying = false; // temporary pause while a seek waits for cached data
        fake.Now += 31000;
        fake.Command = FakeRoom.Remote("347230", true, fake.Now, 5000);
        int reads = fake.Calls.Count(x => x.Path.EndsWith("sync/playlist/get"));
        await session.TickAsync(Capture, Apply, default);
        Check(fake.Commands == commands && applications == 0 && fake.Calls.Count(x => x.Path.EndsWith("sync/playlist/get")) == reads,
            "local seek/cache transition neither publishes temporary pause nor consumes remote snapshot");
        Check(fake.Calls.Last().Path.EndsWith("heartbeat"), "room heartbeat continues during local interaction");
        local.IsChangingPlayback = false;
        local.Playback.IsPlaying = true;
        await session.TickAsync(Capture, Apply, default);
        Check(applications == 1 && local.Playback.SongId == "347230", "deferred remote sequence remains available after local interaction");

        fake.PlayMode = "SINGLE_LOOP";
        await session.TickAsync(Capture, Apply, default);
        Check(local.PlayMode == "SINGLE_LOOP" && fake.Commands == commands, "mode-only room change applies without a playback echo");
        local.PlayMode = "RANDOM";
        await session.TickAsync(Capture, Apply, default);
        Check(fake.PlayMode == "RANDOM", "local mode choice is published instead of overwritten by room snapshot");

        fake.Now += 1000;
        fake.Command = FakeRoom.Remote("186016", true, fake.Now, 10000);
        await session.TickAsync(Capture, (_, _) =>
        {
            local.Playback.IsPlaying = false; // a new user action during asynchronous apply
            throw new ListenTogetherLocalChangeException();
        }, default);
        Check(fake.Command.Value<string>("playStatus") == "PAUSE" && !local.Playback.IsPlaying,
            "local action superseding remote load is published without a synchronization error");
        await session.LeaveAsync(default);
    }

    private sealed class FixtureHandler(byte[] bytes, string encoding) : HttpMessageHandler
    {
        public int Count { get; private set; }
        public string Cookie { get; private set; } = "";
        public string EncryptedPayload { get; private set; } = "";
        public string Path { get; private set; } = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            Path = request.RequestUri!.AbsolutePath;
            Cookie = request.Headers.TryGetValues("Cookie", out var cookies) ? string.Join("; ", cookies) : "";
            string form = await request.Content!.ReadAsStringAsync(cancellationToken);
            EncryptedPayload = Uri.UnescapeDataString(form.Split('=')[1]);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            if (encoding.Length > 0) response.Content.Headers.ContentEncoding.Add(encoding);
            return response;
        }
    }

    private sealed class FakeRoom
    {
        public long Now = 1792000000000;
        public bool InRoom, FailEnd, FailPlaylist;
        public bool Joinable = true;
        public int Members = 1;
        public JObject Command = new();
        public JArray Queue = new("186016");
        public string PlayMode = "ORDER_LOOP";
        public List<(string Path, JObject Data)> Calls = new();
        public int Commands => Calls.Count(x => x.Path.EndsWith("play/command/report"));
        public ListenTogetherApi Api => new(Send);
        public static JObject Remote(string id, bool playing, long seq, long progress) => new()
        {
            ["userId"] = "456", ["targetSongId"] = id, ["playStatus"] = playing ? "PLAY" : "PAUSE",
            ["serverSeq"] = seq, ["progress"] = progress, ["clientSeq"] = 100
        };
        public Task<JObject> Send(string path, JObject data, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls.Add((path, (JObject)data.DeepClone()));
            if (FailPlaylist && path.EndsWith("sync/playlist/get")) throw new HttpRequestException("fixture: ended room has no playlist");
            JObject result = new();
            if (path.EndsWith("status/get")) result = new JObject { ["inRoom"] = InRoom, ["roomInfo"] = new JObject { ["roomId"] = "fixture", ["roomUsers"] = new JArray(Enumerable.Range(0, Members).Select(_ => new JObject())) } };
            else if (path.EndsWith("room/check")) result["joinable"] = Joinable;
            else if (path.EndsWith("room/create") || path.EndsWith("invitation/accept"))
            {
                InRoom = true; result["roomInfo"] = new JObject { ["roomId"] = "fixture" };
            }
            else if (path.EndsWith("heartbeat"))
            {
                if (!ListenTogetherInvitation.IsSongId(data.Value<string>("songId")))
                    return Task.FromResult(new JObject { ["code"] = 400, ["message"] = "invalid heartbeat song" });
                result = new JObject { ["result"] = true, ["time"] = Now, ["timeSpan"] = 30 };
            }
            else if (path.EndsWith("play/command/report"))
            {
                Command = JObject.Parse(data.Value<string>("commandInfo")!);
                Command["serverSeq"] = Now; Command["userId"] = "123"; result["result"] = true;
            }
            else if (path.EndsWith("sync/list/command/report"))
            {
                var list = JObject.Parse(data.Value<string>("playlistParam")!);
                Queue = (JArray)list["displayList"]!;
                PlayMode = list.Value<string>("playMode")!;
            }
            else if (path.EndsWith("sync/playlist/get")) result = new JObject { ["playCommand"] = Command.DeepClone(), ["playlist"] = new JObject { ["displayList"] = new JObject { ["result"] = Queue.DeepClone() }, ["playMode"] = PlayMode } };
            else if (path.EndsWith("end/v2")) { result["success"] = !FailEnd; if (!FailEnd) InRoom = false; }
            else throw new Exception("Unexpected endpoint: " + path);
            return Task.FromResult(Ok(result));
        }
    }
}
