using System.Globalization;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Newtonsoft.Json.Linq;
using YTPlayer.Core;
using YTPlayer.Core.Recognition;
using YTPlayer.Models;
using YTPlayer.Utils;

internal static class RegressionTests
{
    private static void Check(bool value, string name) => Program.Check(value, name);
    internal static byte[] Tone(int seconds)
    {
        byte[] pcm = new byte[seconds * 16000];
        for (int i = 0; i < pcm.Length / 2; i++) System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2, 2), (short)(10000 * Math.Sin(i * .14)));
        return pcm;
    }
    internal static async Task RunAsync()
    {
        await FingerprintAsync();
        await TransportAsync();
        await CoordinatorAsync();
        var merge = typeof(YTPlayer.MainForm).GetMethod("MergeRecognitionSongs", BindingFlags.NonPublic | BindingFlags.Static)!;
        object?[] args = { new[] { new SongInfo { Id = "1" }, new SongInfo { Id = "2" }, new SongInfo { Id = "2" } }, new[] { new SongInfo { Id = "1" } }, null };
        var merged = (List<SongInfo>)merge.Invoke(null, args)!;
        Check(merged.Count == 2 && Equals(args[2], 1), "recognition result merge counts only newly added songs");
    }
    private static async Task FingerprintAsync()
    {
        using var fp = new AudioFingerprintGenerator();
        byte[] pcm = Tone(3);
        string first = await fp.GenerateAsync(pcm, 8000, 1, default);
        Check(Convert.FromBase64String(first).Length > 0, "actual embedded WASM produces fingerprint");
        for (int i = 0; i < 5; i++) Check(await fp.GenerateAsync(pcm, 8000, 1, default) == first, "repeated engines produce same fingerprint and release vectors");
        await Program.Throws<ArgumentException>(() => fp.GenerateAsync(new byte[3], 8000, 1, default), "partial PCM sample rejected");
        await Program.Throws<ArgumentException>(() => fp.GenerateAsync(pcm, 44100, 2, default), "wrong format rejected");
        await Program.Throws<ArgumentException>(() => fp.GenerateAsync(new byte[31 * 16000], 8000, 1, default), "unbounded input rejected");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Program.Throws<OperationCanceledException>(() => fp.GenerateAsync(pcm, 8000, 1, cancelled.Token), "pre-cancelled fingerprint");
        using var during = new CancellationTokenSource(1);
        await Program.Throws<OperationCanceledException>(() => fp.GenerateAsync(Tone(9), 8000, 1, during.Token), "cancel fingerprint during initialization/work");
        Check(await fp.GenerateAsync(pcm, 8000, 1, default) == first, "fingerprint works after cancellation");
        fp.Dispose();
        await Program.Throws<ObjectDisposedException>(() => fp.GenerateAsync(pcm, 8000, 1, default), "disposed generator rejects work");
        Check(!SongRecognitionCoordinator.HasSignal(new byte[48000]), "digital silence rejected");
        byte[] dc = Enumerable.Repeat((byte)100, 48000).ToArray();
        Check(!SongRecognitionCoordinator.HasSignal(dc), "DC is not music");
        Check(SongRecognitionCoordinator.HasSignal(pcm), "signal retained");
    }
    private static void SetClient(NeteaseApiClient client, string name, HttpMessageHandler handler)
    {
        var field = typeof(NeteaseApiClient).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!;
        ((HttpClient)field.GetValue(client)!).Dispose(); field.SetValue(client, new HttpClient(handler));
    }
    private static async Task TransportAsync()
    {
        using var client = new NeteaseApiClient();
        var primary = new Handler(); var fallback = new Handler();
        SetClient(client, "_eapiClient", primary); SetClient(client, "_httpClient", fallback);
        primary.Response = "{\"code\":200,\"data\":{\"result\":null,\"noMatchReason\":1}}";
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            await client.RecognizeSongAsync("AQIDBA==", 3.125, "fixture-session", 2, default);
        }
        finally { CultureInfo.CurrentCulture = culture; }
        var request = primary.Payload!;
        Check(primary.Path == "/eapi/music/audio/match", "recognition uses EAPI path");
        Check(request.Value<string>("duration") == "3.125" && request.Value<string>("times") == "2", "actual duration and attempt, independent of culture");
        Check(request.Value<string>("sessionId") == "fixture-session" && request.Value<string>("sceneParams") == "{\"code\":2}", "captured client scene and session retained");
        Check(fallback.Count == 0, "valid no-match never triggers other endpoints");
        primary.Response = "{\"code\":491,\"message\":\"fixture rejection\"}";
        await Program.Throws<SongRecognitionException>(() => client.RecognizeSongAsync("AQIDBA==", 3, default), "business rejection is error, not empty match");
        Check(fallback.Count == 0, "business error not bypassed through fallback");
        primary.Fail = true;
        await client.RecognizeSongAsync("AQIDBA==", 6, "same-session", 2, default);
        Check(fallback.Count == 1 && fallback.Query.Contains("same-session") && fallback.Query.Contains("times=2"), "single first-party fallback preserves session");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        int before = primary.Count + fallback.Count;
        await Program.Throws<OperationCanceledException>(() => client.RecognizeSongAsync("AQIDBA==", 3, cancelled.Token), "API cancellation is preserved");
        Check(primary.Count + fallback.Count == before, "cancelled request never sends or falls back");
        foreach (string bad in new[] { "{}", "{\"code\":200}", "{\"code\":200,\"data\":{}}", "{\"code\":200,\"data\":{\"result\":{}}}", "{\"code\":200,\"data\":{\"result\":[{}]}}" })
            await Program.Throws<SongRecognitionException>(() => Task.FromResult(client.ParseRecognitionResponse(JObject.Parse(bad))), "malformed response is not no-match");
        var parsed = client.ParseRecognitionResponse(JObject.Parse("""
            {"code":200,"data":{"result":[{"song":{"name":"missing id"},"startTime":1},
            {"song":{"id":123,"name":"valid","artists":[{"name":"artist"}],"album":{"name":"album"},"duration":123000},"startTime":42000,"score":0.8},
            {"song":{"id":123,"name":"duplicate"},"startTime":10}]}}
            """));
        Check(parsed.Count == 1 && parsed[0].MatchStartMs == 42000 && parsed[0].MatchScore == .8, "skipped/duplicate results do not shift match metadata");
        Check(parsed[0].Artist == "artist" && parsed[0].Album == "album" && parsed[0].Duration == 123, "legacy song fields retained");
    }
    private static async Task CoordinatorAsync()
    {
        var capture = new Capture(); var fingerprint = new Fingerprint();
        var requests = new List<(double Duration, string Session, int Attempt)>();
        var coordinator = new SongRecognitionCoordinator(capture, fingerprint, (fp, duration, session, attempt, token) =>
        { requests.Add((duration, session, attempt)); return Task.FromResult(attempt == 2 ? new List<SongInfo> { new() { Id = "123" } } : new List<SongInfo>()); });
        var result = await coordinator.RecognizeAsync(new(), 15, default);
        Check(requests.Select(r => r.Duration).SequenceEqual(new[] { 3.0, 6.0 }), "progressive cumulative samples, early match stops capture");
        Check(requests.Select(r => r.Attempt).SequenceEqual(new[] { 1, 2 }) && requests.Select(r => r.Session).Distinct().Count() == 1, "incremental attempts reuse session");
        Check(capture.Starts == 1 && capture.Disposed && result.DurationSeconds == 6 && result.SessionId == requests[0].Session, "continuous capture disposed after success and actual duration returned");
        var longCapture = new Capture(); var durations = new List<double>();
        var longRecognition = new SongRecognitionCoordinator(longCapture, new Fingerprint(), (_, duration, _, _, _) =>
        { durations.Add(duration); return Task.FromResult(new List<SongInfo>()); });
        await longRecognition.RecognizeAsync(new(), 15, default);
        Check(durations.SequenceEqual(new[] { 3.0, 6.0, 9.0, 9.0, 9.0 }), "long listening uses overlapping windows and true upload duration");
        byte[] longPcm = Tone(15);
        Check(SongRecognitionCoordinator.SelectFingerprintWindow(longPcm).SequenceEqual(longPcm.Skip(6 * 16000)), "window contains newest audio, not repeated old intro");
        durations.Clear(); await longRecognition.RecognizeAsync(new(), 24, default);
        Check(durations.Count == 8 && durations.Max() == 9, "24-second mode keeps all eight attempts within the safe window");
        capture = new Capture { Silent = true }; fingerprint = new Fingerprint();
        coordinator = new(capture, fingerprint, (_, _, _, _, _) => throw new Exception("silence sent"));
        await Program.Throws<InvalidOperationException>(() => coordinator.RecognizeAsync(new(), 15, default), "silence reports device guidance");
        Check(fingerprint.Calls == 0 && capture.Disposed, "silence never fingerprinted/uploaded");
        capture = new Capture(); int failures = 0;
        coordinator = new(capture, new Fingerprint(), (_, _, _, _, _) => { failures++; throw new HttpRequestException("offline"); });
        await Program.Throws<HttpRequestException>(() => coordinator.RecognizeAsync(new(), 15, default), "network failure remains a failure");
        Check(failures == 2 && capture.Disposed, "fail fast after two consecutive failures and free capture");
        capture = new Capture(); using var cancelled = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator = new(capture, new Fingerprint(), async (_, _, _, _, token) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return new(); });
        var pending = coordinator.RecognizeAsync(new(), 15, cancelled.Token); await entered.Task;
        await Program.Throws<InvalidOperationException>(() => coordinator.RecognizeAsync(new(), 15, default), "concurrent recognition rejected");
        cancelled.Cancel();
        await Program.Throws<OperationCanceledException>(() => pending, "cancel in-flight recognition");
        Check(capture.Disposed, "capture disposed on cancelled network request");
    }
    private sealed class Capture : IAudioCaptureSource
    {
        public int Starts; public bool Disposed, Silent;
        public IReadOnlyList<AudioInputDeviceInfo> EnumerateDevices() => new[] { new AudioInputDeviceInfo() };
        public async IAsyncEnumerable<byte[]> CaptureProgressiveAsync(AudioInputDeviceInfo device, int duration, [EnumeratorCancellation] CancellationToken token)
        {
            Starts++;
            try { for (int seconds = 3; seconds <= duration; seconds += 3) { token.ThrowIfCancellationRequested(); await Task.Yield(); yield return Silent ? new byte[seconds * 16000] : Tone(seconds); } }
            finally { Disposed = true; }
        }
    }
    private sealed class Fingerprint : IAudioFingerprintGenerator
    {
        public int Calls;
        public Task<string> GenerateAsync(byte[] pcm, int rate, int channels, CancellationToken token) { token.ThrowIfCancellationRequested(); Calls++; return Task.FromResult("AQIDBA=="); }
    }
    private sealed class Handler : HttpMessageHandler
    {
        public int Count; public bool Fail; public string Path = "", Query = "";
        public JObject? Payload;
        public string Response = "{\"code\":200,\"data\":{\"result\":null}}";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Count++; Path = request.RequestUri!.AbsolutePath; Query = request.RequestUri.Query;
            if (Fail) throw new HttpRequestException("fixture transport failure");
            if (request.Content != null)
            {
                string form = await request.Content.ReadAsStringAsync(token);
                string clear = EncryptionHelper.DecryptEapi(Convert.FromHexString(Uri.UnescapeDataString(form.Split('=')[1])));
                Payload = JObject.Parse(clear.Split("-36cd479b6b5-")[1]);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Response, Encoding.UTF8, "application/json") };
        }
    }
}
