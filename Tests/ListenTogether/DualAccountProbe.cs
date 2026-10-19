using System.Diagnostics;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YTPlayer.Core;
using YTPlayer.Core.ListenTogether;

internal static class DualAccountProbe
{
    public static async Task WorkerAsync(string accountPath)
    {
        var saved = JObject.Parse(File.ReadAllText(accountPath));
        using var client = new NeteaseApiClient();
        client.SetCookieString(saved.Value<string>("Cookie") ?? throw new Exception("Missing Cookie"));
        var originalApi = client.CreateListenTogetherApi();
        var send = (Func<string, JObject, CancellationToken, Task<JObject>>)typeof(ListenTogetherApi)
            .GetField("_send", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(originalApi)!;
        var trace = new JArray();
        var api = new ListenTogetherApi(async (path, payload, token) =>
        {
            JObject result = await send(path, payload, token);
            trace.Add(new JObject { ["path"] = path, ["command"] = payload["commandInfo"] == null ? null : JObject.Parse(payload.Value<string>("commandInfo")!),
                ["code"] = result["code"], ["result"] = result["data"]?["result"], ["message"] = result["message"] ?? result["data"]?["message"] });
            return result;
        });
        string user = saved.Value<string>("UserId") ?? throw new Exception("Missing UserId");
        var session = new ListenTogetherSession(api, user);
        var local = Program.Local("186016", 0, false, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), new[] { "186016", "347230" });
        local.SeekVersion = 0;
        int applied = 0;
        ListenTogetherLocalState Capture() => new() {
            Playback = new() { SongId = local.Playback.SongId, IsPlaying = local.Playback.IsPlaying,
                PositionSeconds = local.Playback.EstimatePositionSeconds(), UpdatedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
            Queue = local.Queue, PlayMode = local.PlayMode, SeekVersion = local.SeekVersion
        };
        Task Apply(ListenTogetherRemoteState update, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (update.Playback != null) { local.Playback = update.Playback; applied++; }
            if (update.Queue != null) local.Queue = update.Queue;
            if (update.PlayMode != null) local.PlayMode = update.PlayMode;
            return Task.CompletedTask;
        }
        Console.WriteLine(new JObject { ["ok"] = true, ["userId"] = user }.ToString(Formatting.None));
        try
        {
            while (await Console.In.ReadLineAsync() is string line)
            {
                var request = JObject.Parse(line);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                var token = deadline.Token;
                try
                {
                    JObject result = new();
                    switch (request.Value<string>("op"))
                    {
                        case "status": result = await api.StatusAsync(token); break;
                        case "trace": result["requests"] = trace.DeepClone(); break;
                        case "start":
                            session = new ListenTogetherSession(api, user);
                            local.PlayMode = request.Value<string>("mode") ?? local.PlayMode;
                            var invitation = request.Value<string>("invite");
                            await session.StartAsync(invitation == null ? null : ListenTogetherInvitation.Parse(invitation), Capture, Apply, token);
                            result = new JObject { ["roomId"] = session.RoomId, ["invite"] = session.InviteUrl(local.Playback.SongId) }; break;
                        case "tick": await session.TickAsync(Capture, Apply, token); break;
                        case "set":
                            local = Capture();
                            local.Playback.SongId = request.Value<string>("songId") ?? local.Playback.SongId;
                            local.Playback.IsPlaying = request.Value<bool?>("playing") ?? local.Playback.IsPlaying;
                            if (request.Value<double?>("position") is double position) { local.Playback.PositionSeconds = position; local.SeekVersion++; }
                            if (request["queue"] is JArray list) local.Queue = list.Values<string>().Select(x => x!).ToArray();
                            local.PlayMode = request.Value<string>("mode") ?? local.PlayMode;
                            await session.TickAsync(Capture, Apply, token); break;
                        case "snapshot": result = JObject.FromObject(Capture()); result["applied"] = applied; break;
                        case "playlist": result = await api.PlaylistAsync(session.RoomId, token); break;
                        case "leave": await session.LeaveAsync(token); break;
                        case "exit": return;
                        default: throw new ArgumentException("Unknown worker operation");
                    }
                    Console.WriteLine(new JObject { ["ok"] = true, ["data"] = result }.ToString(Formatting.None));
                }
                catch (Exception e)
                {
                    Console.WriteLine(new JObject { ["ok"] = false, ["error"] = e.Message,
                        ["code"] = (e as ListenTogetherApiException)?.Code, ["operation"] = (e as ListenTogetherApiException)?.Operation }.ToString(Formatting.None));
                }
            }
        }
        finally
        {
            if (session.IsActive)
            {
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    var status = await api.StatusAsync(cleanup.Token);
                    if (status["roomInfo"]?.Value<string>("roomId") == session.RoomId) await session.LeaveAsync(cleanup.Token);
                }
                catch (Exception e) { Console.Error.WriteLine("Cleanup failed: " + e.GetType().Name); }
            }
        }
    }

    public static async Task RunAsync(string[] accounts)
    {
        if (accounts.Length != 2) throw new ArgumentException("--dual <account1.json> <account2.json>");
        var hashes = accounts.Select(p => SHA256.HashData(File.ReadAllBytes(p))).ToArray();
        await using var a = await Peer.StartAsync(accounts[0]);
        await using var b = await Peer.StartAsync(accounts[1]);
        Program.Check(a.UserId != b.UserId, "two distinct accounts");
        Console.WriteLine("PASS dual: two separate processes with distinct account sessions.");
        for (int round = 0; round < 2; round++)
        {
            var host = round == 0 ? a : b; var guest = round == 0 ? b : a;
            Program.Check((await host.Call("status")).Value<bool?>("inRoom") == false && (await guest.Call("status")).Value<bool?>("inRoom") == false,
                "do not disturb existing rooms");
            var room = await host.Call("start", new JObject { ["mode"] = "ORDER_LOOP" });
            Console.WriteLine($"PASS dual round {round + 1}: host created and initialized room.");
            if (round == 1)
            {
                await host.Call("set", new JObject { ["playing"] = true, ["position"] = 10.0 });
                await Task.Delay(6000);
            }
            await guest.Call("start", new JObject { ["invite"] = room.Value<string>("invite"), ["mode"] = "RANDOM" });
            Program.Check((await guest.Call("snapshot")).Value<string>("PlayMode") == "ORDER_LOOP", "join applies the room's initial play mode");
            var hostStatus = await host.Call("status");
            var guestStatus = await guest.Call("status");
            Program.Check(hostStatus["roomInfo"]?.Value<string>("roomId") == guestStatus["roomInfo"]?.Value<string>("roomId"), "same room");
            Program.Check((hostStatus["roomInfo"]?["roomUsers"] as JArray)?.Count == 2, "room has two members");
            Console.WriteLine($"PASS dual round {round + 1}: host created, guest joined, server reports two members in same room.");
            if (round == 1)
            {
                var hostPlayback = (await host.Call("snapshot"))["Playback"]!;
                var guestPlayback = (await guest.Call("snapshot"))["Playback"]!;
                Program.Check(guestPlayback.Value<bool>("IsPlaying") &&
                    Math.Abs(hostPlayback.Value<double>("PositionSeconds") - guestPlayback.Value<double>("PositionSeconds")) < 2,
                    "joining a playing room uses current room time");
                Console.WriteLine("PASS dual: initial join catches up with already-playing room.");
                await AssertChange(host, guest, new JObject { ["playing"] = false }, "186016", false, null, "prepare paused state");
            }
            await AssertChange(host, guest, new JObject { ["playing"] = true }, "186016", true, null, "host play -> guest");
            await AssertChange(guest, host, new JObject { ["playing"] = false }, "186016", false, null, "guest pause -> host");
            await AssertChange(host, guest, new JObject { ["position"] = 42.125 }, "186016", false, 42.125, "host seek -> guest");
            await AssertChange(guest, host, new JObject { ["songId"] = "347230", ["playing"] = true, ["position"] = 10.0 }, "347230", true, 10.0, "guest track change -> host");
            await AssertChange(host, guest, new JObject { ["songId"] = "186016", ["playing"] = false, ["position"] = 6.25 }, "186016", false, 6.25, "host track change -> guest");
            await host.Call("set", new JObject { ["queue"] = new JArray("186016", "347230", "185924") });
            await guest.Call("tick");
            await Task.Delay(2000); await guest.Call("tick");
            Program.Check((await guest.Call("snapshot"))["Queue"]?.Count() == 3, "queue synchronized");
            Console.WriteLine("PASS dual: queue synchronized.");
            foreach (var (sender, receiver, mode) in new[] { (host, guest, "SINGLE_LOOP"), (guest, host, "RANDOM"), (host, guest, "ORDER_LOOP") })
            {
                await sender.Call("set", new JObject { ["mode"] = mode });
                await Task.Delay(2000); await receiver.Call("tick");
                Program.Check((await receiver.Call("snapshot")).Value<string>("PlayMode") == mode, "mode synchronized: " + mode);
                Console.WriteLine("PASS dual: play mode synchronized: " + mode);
            }
            if (round == 0)
            {
                for (int tick = 0; tick < 18; tick++)
                {
                    await Task.Delay(2000);
                    await host.Call("tick"); await guest.Call("tick");
                }
                Program.Check((await host.Call("status"))["roomInfo"]?["roomUsers"]?.Count() == 2, "both members survive heartbeat interval");
                Console.WriteLine("PASS dual: both members remain connected across heartbeat interval.");
            }
            await guest.Call("leave");
            var afterHost = await host.Call("status");
            if (afterHost.Value<bool?>("inRoom") == true) await host.Call("leave");
            Program.Check((await host.Call("status")).Value<bool?>("inRoom") == false && (await guest.Call("status")).Value<bool?>("inRoom") == false, "both left");
            Console.WriteLine("PASS dual: both accounts out of test room.");
        }
        for (int i = 0; i < accounts.Length; i++) Program.Check(hashes[i].SequenceEqual(SHA256.HashData(File.ReadAllBytes(accounts[i]))), "profile unchanged");
        Console.WriteLine("PASS dual: original account files unchanged.");
    }

    private static async Task AssertChange(Peer sender, Peer receiver, JObject change, string song, bool playing, double? position, string label)
    {
        var elapsed = Stopwatch.StartNew();
        await sender.Call("set", change); await receiver.Call("tick");
        var snapshot = (await receiver.Call("snapshot"))["Playback"]!;
        if (snapshot.Value<string>("SongId") != song || snapshot.Value<bool>("IsPlaying") != playing ||
            (position.HasValue && Math.Abs(snapshot.Value<double>("PositionSeconds") - position.Value - (playing ? elapsed.Elapsed.TotalSeconds : 0)) >= 2))
        {
            await Task.Delay(2000);
            await receiver.Call("tick");
            snapshot = (await receiver.Call("snapshot"))["Playback"]!;
        }
        if (snapshot.Value<string>("SongId") != song || snapshot.Value<bool>("IsPlaying") != playing)
        {
            var command = (await sender.Call("playlist"))["playCommand"] as JObject;
            Console.WriteLine("DIAGNOSTIC " + new JObject {
                ["step"] = label, ["sender"] = (await sender.Call("snapshot"))["Playback"], ["receiver"] = snapshot,
                ["senderTrace"] = await sender.Call("trace"), ["receiverTrace"] = await receiver.Call("trace"),
                ["command"] = command == null ? null : new JObject(command.Properties().Where(p => new[] { "commandType", "playStatus", "targetSongId", "progress", "clientSeq", "serverSeq" }.Contains(p.Name)).Select(p => new JProperty(p.Name, p.Value.DeepClone())))
            }.ToString(Formatting.None));
        }
        Program.Check(snapshot.Value<string>("SongId") == song && snapshot.Value<bool>("IsPlaying") == playing, label);
        if (position.HasValue) Program.Check(Math.Abs(snapshot.Value<double>("PositionSeconds") - position.Value - (playing ? elapsed.Elapsed.TotalSeconds : 0)) < 2, label + " position");
        Console.WriteLine("PASS dual: " + label);
    }

    internal sealed class Peer : IAsyncDisposable
    {
        private readonly Process _process;
        public string UserId { get; private set; } = "";
        private Peer(Process process) { _process = process; }
        public static async Task<Peer> StartAsync(string path)
        {
            var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add("--worker"); info.ArgumentList.Add(Path.GetFullPath(path));
            var peer = new Peer(Process.Start(info)!);
            var greeting = await peer.Read(); peer.UserId = greeting.Value<string>("userId") ?? throw new Exception("Worker did not initialize");
            return peer;
        }
        private async Task<JObject> Read()
        {
            string? line = await _process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(35));
            return line == null ? throw new Exception("Worker ended unexpectedly") : JObject.Parse(line);
        }
        public async Task<JObject> Call(string op, JObject? payload = null)
        {
            payload ??= new JObject(); payload["op"] = op;
            await _process.StandardInput.WriteLineAsync(payload.ToString(Formatting.None)); await _process.StandardInput.FlushAsync();
            var response = await Read();
            if (response.Value<bool?>("ok") != true) throw new Exception(op + " / " + response.Value<string>("operation") + ": " + response.Value<string>("error"));
            return (JObject)response["data"]!;
        }
        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited)
            {
                await _process.StandardInput.WriteLineAsync("{\"op\":\"exit\"}");
                await _process.StandardInput.FlushAsync();
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            }
            _process.Dispose();
        }
    }
}
