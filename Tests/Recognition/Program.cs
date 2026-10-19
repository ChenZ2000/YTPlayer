using System.Reflection;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using YTPlayer.Core;
using YTPlayer.Core.Recognition;

internal static class Program
{
    internal static int Passed;
    internal static void Check(bool condition, string name)
    { if (!condition) throw new Exception(name); Passed++; }
    internal static async Task Throws<T>(Func<Task> action, string name) where T : Exception
    { try { await action(); } catch (T) { Check(true, name); return; } throw new Exception("Expected " + typeof(T).Name + ": " + name); }
    [STAThread]
    private static int Main(string[] args)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "test-data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); Environment.SetEnvironmentVariable("YTPLAYER_ROOT", root);
        try
        {
            if (args.Length == 0) DialogTests.Run();
            SynchronizationContext.SetSynchronizationContext(null);
            RunAsync(args).GetAwaiter().GetResult(); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e.ToString()); return 1; }
        finally { Directory.Delete(root, true); }
    }
    private static async Task RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            await RegressionTests.RunAsync();
            Console.WriteLine($"PASS: {Passed} assertions; no account or live network used."); return;
        }
        if (args[0] == "--devices")
        {
            foreach (var d in new AudioCaptureService().EnumerateDevices()) Console.WriteLine($"{d.BassDeviceIndex}: loopback={d.IsLoopback}, {d.DisplayName}");
            return;
        }
        if (args[0] == "--stress")
        {
            using var stressGenerator = new AudioFingerprintGenerator();
            var cases = JArray.Parse(File.ReadAllText(args[1])).OfType<JObject>().ToArray();
            for (int i = 0; i < 120; i++)
            {
                byte[] data = File.ReadAllBytes(cases[i % cases.Length].Value<string>("pcm")!);
                string result = await stressGenerator.GenerateAsync(SongRecognitionCoordinator.SelectFingerprintWindow(data), 8000, 1, default);
                Check(Convert.FromBase64String(result).Length > 0, "stress fingerprint");
                if ((i + 1) % 20 == 0) Console.WriteLine($"PASS fingerprint stress: {i + 1}/120");
            }
            return;
        }
        if (args[0] == "--memory")
        {
            using var g = new AudioFingerprintGenerator();
            dynamic engine = typeof(AudioFingerprintGenerator).GetMethod("CreateEngine", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(g, null)!;
            try
            {
                Console.WriteLine(await (Task<object>)engine.Evaluate("instantiateRuntime().then(r => { const n = r.asm.B.buffer.byteLength; try { return JSON.stringify({ bytes:n, previousPages:r.asm.B.grow(256), grownBytes:r.asm.B.buffer.byteLength }); } catch(e) { return JSON.stringify({ bytes:n, error:String(e) }); } })"));
            }
            finally { ((IDisposable)engine).Dispose(); }
            return;
        }
        if (args[0] == "--capture") { await CaptureProbe.RunAsync(args.Skip(1).ToArray()); return; }
        if (args[0] == "--benchmark") { await Benchmark.RunAsync(args[1], args[2], args[3]); return; }
        if (args[0] == "--details")
        {
            using var client = AccountClient(args[1]);
            var songs = await client.GetSongDetailAsync(args[2].Split(','));
            foreach (var song in songs) Console.WriteLine($"{song.Id}: {song.Name} / {song.Artist} / {song.Album}");
            return;
        }
        if (args[0] == "--download")
        {
            using var client = AccountClient(args[1]);
            using var http = new HttpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var profile = JObject.Parse(File.ReadAllText(args[1]));
            var headers = new JObject();
            foreach (string cookie in profile.Value<string>("Cookie")!.Split(';'))
            { int equal = cookie.IndexOf('='); if (equal > 0) headers[cookie[..equal].Trim()] = cookie[(equal + 1)..].Trim(); }
            var responseData = await client.PostEApiAsync<JObject>("/api/song/enhance/player/url/v1",
                new JObject { ["ids"] = "[186016,347230]", ["level"] = "standard", ["encodeType"] = "mp3", ["header"] = headers, ["e_r"] = "true" },
                useIosHeaders: false, retryCount: 4, skipErrorHandling: true, cancellationToken: timeout.Token);
            if (responseData.Value<int>("code") != 200) throw new Exception("Audio fixture endpoint code=" + responseData.Value<int>("code"));
            foreach (var item in (responseData["data"] as JArray ?? new()).OfType<JObject>())
            {
                string? url = item.Value<string>("url"), id = item.Value<string>("id");
                if (string.IsNullOrWhiteSpace(url)) continue;
                using var response = await http.GetAsync(url, timeout.Token); response.EnsureSuccessStatusCode();
                await File.WriteAllBytesAsync(Path.Combine(args[2], id + ".audio"), await response.Content.ReadAsByteArrayAsync(timeout.Token), timeout.Token);
                Console.WriteLine("downloaded audio fixture: " + id);
            }
            return;
        }
        using var generator = new AudioFingerprintGenerator();
        byte[] pcm = File.ReadAllBytes(args[1]);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string fp = await generator.GenerateAsync(pcm, 8000, 1, deadline.Token);
        File.WriteAllText(args[2], fp);
        Console.WriteLine($"fingerprint bytes={Convert.FromBase64String(fp).Length}, sha256={Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(fp)))}");
        if (args.Length > 3)
        {
            using var client = AccountClient(args[3]);
            var matches = await client.RecognizeSongAsync(fp, pcm.Length / 16000, deadline.Token);
            Console.WriteLine("matches=" + string.Join(",", matches.Select(s => s.Id)));
        }
    }
    internal static NeteaseApiClient AccountClient(string file)
    {
        var client = new NeteaseApiClient();
        client.SetCookieString(JObject.Parse(File.ReadAllText(file)).Value<string>("Cookie")!);
        return client;
    }
}
