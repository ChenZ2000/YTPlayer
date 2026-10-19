using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using YTPlayer.Core;
using YTPlayer.Core.ListenTogether;

internal static class LiveProbe
{
    public static async Task RunAsync(string[] args)
    {
        if (args.Length != 3 || args[2] != "--create")
            throw new ArgumentException("Usage: --live <account.json> --create (creates and closes one test room)");
        string accountPath = Path.GetFullPath(args[1]);
        byte[] originalHash = SHA256.HashData(File.ReadAllBytes(accountPath));
        var account = JObject.Parse(File.ReadAllText(accountPath));
        string userId = account.Value<string>("UserId") ?? throw new Exception("Missing UserId");
        using var client = new NeteaseApiClient();
        client.SetCookieString(account.Value<string>("Cookie") ?? throw new Exception("Missing Cookie"));
        var api = client.CreateListenTogetherApi();
        var session = new ListenTogetherSession(api, userId);
        var local = Program.Local("186016", 0, true, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), new[] { "186016", "347230" });
        int incoming = 0;
        ListenTogetherLocalState Capture() => Program.Local(local.Playback.SongId, local.Playback.PositionSeconds,
            local.Playback.IsPlaying, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), local.Queue, local.PlayMode);
        Task Apply(ListenTogetherRemoteState update, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (update.Playback != null) { local.Playback = update.Playback; incoming++; }
            if (update.Queue != null) local.Queue = update.Queue;
            return Task.CompletedTask;
        }
        try
        {
            // StartAsync refuses to disturb a room already active on this account.
            await session.StartAsync(null, Capture, Apply, CancellationToken.None);
            Console.WriteLine("PASS live: official room created, initial queue/play command and heartbeat accepted.");
            Program.Check(ListenTogetherInvitation.Parse(session.InviteUrl("186016")).RoomId == session.RoomId, "live invitation");
            foreach (var change in new[] { ("186016", false, 0.0, "PAUSE"), ("186016", false, 23.125, "seek"), ("347230", true, 0.0, "GOTO") })
            {
                local.Playback.SongId = change.Item1; local.Playback.IsPlaying = change.Item2; local.Playback.PositionSeconds = change.Item3;
                await session.TickAsync(Capture, Apply, CancellationToken.None);
                var data = await api.PlaylistAsync(session.RoomId, CancellationToken.None);
                var cmd = data["playCommand"]!;
                Program.Check(cmd.Value<string>("commandType") == change.Item4 && cmd.Value<string>("targetSongId") == change.Item1,
                    "live command readback: " + change.Item4);
                Program.Check(cmd.Value<long>("progress") == (long)(change.Item3 * 1000), "live millisecond progress");
                Console.WriteLine("PASS live: " + change.Item4 + " returned by official snapshot.");
            }
            // Inject another command using the same authorized account to exercise receive/apply.
            // This is an API round trip, not a second participant or an official-client compatibility test.
            await api.CommandAsync(session.RoomId, new JObject {
                ["commandType"] = "PAUSE", ["progress"] = 32125, ["playStatus"] = "PAUSE",
                ["formerSongId"] = "347230", ["targetSongId"] = "186016", ["clientSeq"] = 1000000
            }, CancellationToken.None);
            await session.TickAsync(Capture, Apply, CancellationToken.None);
            Program.Check(incoming == 1 && local.Playback.SongId == "186016" && !local.Playback.IsPlaying && local.Playback.PositionSeconds == 32.125,
                "live receive/apply");
            await session.TickAsync(Capture, Apply, CancellationToken.None);
            Program.Check(incoming == 1, "live deduplication");
            Console.WriteLine("PASS live: incoming snapshot applied once; no echo or duplicate playback.");
        }
        finally
        {
            if (session.IsActive)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await session.LeaveAsync(cleanup.Token);
                Console.WriteLine("PASS live: test room ended.");
            }
            Program.Check(originalHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(accountPath))), "source account file unchanged");
        }
        // The read-only status endpoint occasionally returns an empty response; retry one read only.
        JObject status;
        try { status = await api.StatusAsync(CancellationToken.None); }
        catch (ListenTogetherApiException e) when (e.Code == -1)
        {
            await Task.Delay(1500);
            status = await api.StatusAsync(CancellationToken.None);
        }
        Program.Check(status.Value<bool?>("inRoom") == false, "account no longer in test room");
        Console.WriteLine("PASS live: account is out of room; original account file unchanged. Two-account official-client acceptance remains pending.");
    }
}
