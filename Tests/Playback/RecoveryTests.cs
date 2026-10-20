using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using YTPlayer.Core;
using YTPlayer.Core.Playback;
using YTPlayer.Core.Playback.Cache;
using YTPlayer.Core.Streaming;
using YTPlayer.Models;

internal static class RecoveryTests
{
    [DllImport("bass.dll")] private static extern bool BASS_StreamFree(int handle);
    private static readonly byte[] Data = Enumerable.Range(0, 1024).Select(i => (byte)(i % 251)).ToArray();

    internal static async Task RunAsync(string fixture)
    {
        var failures = new List<string>();
        async Task Case(string name, Func<Task> action)
        {
            try { await action(); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failures.Add(name + ": " + ex.Message); Console.WriteLine("FAIL " + name + ": " + ex.GetType().Name); }
        }
        await Case("HTTP timeout is recoverable", async () =>
        {
            using var h = new Handler((r, n) => n == 1 ? throw new TaskCanceledException("simulated HttpClient timeout") : Range(r, Data));
            using var http = new HttpClient(h);
            var result = await new ChunkDownloadManager("https://timeout.invalid/audio", 1024, 256, http).DownloadChunkAsync(1, default);
            Program.Check(result!.SequenceEqual(Data[256..512]) && h.Count == 2, "timeout retries exact block once");
        });
        await Case("503 recovers", async () =>
        {
            using var h = new Handler((r, n) => n == 1 ? new(HttpStatusCode.ServiceUnavailable) : Range(r, Data));
            using var http = new HttpClient(h);
            var result = await new ChunkDownloadManager("https://503.invalid/audio", 1024, 256, http).DownloadChunkAsync(0, default);
            Program.Check(result!.SequenceEqual(Data[..256]) && h.Count == 2, "503 retries without corrupting bytes");
        });
        await Case("permanent HTTP rejection fails promptly", async () =>
        {
            using var h = new Handler((_, _) => new(HttpStatusCode.NotFound));
            using var http = new HttpClient(h);
            Program.Check(await new ChunkDownloadManager("https://404.invalid/audio", 1024, 256, http).DownloadChunkAsync(0, default) == null, "404 rejects block");
            Program.Check(h.Count == 1, "404 does not waste five requests");
        });
        await Case("truncated sequential transfer resumes", async () =>
        {
            using var h = new Handler((r, n) => n == 1 ? Full(Data[..384]) : Range(r, Data));
            using var http = new HttpClient(h);
            var chunks = new SortedDictionary<int, byte[]>();
            await new ChunkDownloadManager("https://resume.invalid/audio", 1024, 256, http)
                .DownloadAllChunksSequentialAsync((i, b) => { chunks.Add(i, b); return Task.CompletedTask; }, null, default);
            Program.Check(chunks.Values.SelectMany(x => x).SequenceEqual(Data), "only complete validated blocks are published");
            Program.Check(h.Starts.SequenceEqual(new long?[] { null, 256 }), "resume begins after last complete block");
        });
        await Case("server ignoring resume Range stays aligned", async () =>
        {
            using var h = new Handler((_, n) => Full(n == 1 ? Data[..384] : Data));
            using var http = new HttpClient(h);
            var chunks = new SortedDictionary<int, byte[]>();
            await new ChunkDownloadManager("https://no-range.invalid/audio", 1024, 256, http)
                .DownloadAllChunksSequentialAsync((i, b) => { chunks.Add(i, b); return Task.CompletedTask; }, null, default);
            Program.Check(chunks.Values.SelectMany(x => x).SequenceEqual(Data), "200 resume skips verified-length prefix");
            Program.Check(h.Count == 2, "one bounded full-response fallback");
        });
        await Case("caller cancellation suppresses retry", async () =>
        {
            using var stop = new CancellationTokenSource();
            using var h = new Handler((_, _) => { stop.Cancel(); throw new TaskCanceledException(); });
            using var http = new HttpClient(h);
            try { await new ChunkDownloadManager("https://cancel.invalid/audio", 1024, 256, http).DownloadChunkAsync(0, stop.Token); throw new Exception("Cancellation swallowed"); }
            catch (OperationCanceledException) { Program.Check(h.Count == 1, "caller cancellation never retries"); }
        });
        await Case("truncated cache is not marked complete", async () =>
        {
            byte[] data = new byte[5 * 256 * 1024];
            using var h = new Handler((r, _) => r.Method == HttpMethod.Head ? Head(data.Length) :
                r.Headers.Range?.Ranges.Single().To == 0 ? Range(r, data) :
                r.Headers.Range == null ? Full(data[..(3 * 256 * 1024 + 10)]) : new(HttpStatusCode.Forbidden));
            using var http = new HttpClient(h);
            using var cache = new SmartCacheManager("broken", "https://broken-cache.invalid/audio", data.Length, http, true);
            await cache.InitializeAsync(default);
            var work = (Task)typeof(SmartCacheManager).GetField("_mainDownloadTask", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cache)!;
            await work.WaitAsync(TimeSpan.FromSeconds(12));
            Program.Check(!cache.IsFullyCached, "early EOF cannot report a complete song");
            Program.Check(!cache.IsChunkCached(3), "partial middle block is never readable as complete");
        });
        await Case("strategy cache respects track requirements", async () =>
        {
            using var h = new Handler((r, _) => r.Method == HttpMethod.Head ? Head(Data.Length) : Range(r, Data));
            using var http = new HttpClient(h);
            using var ranged = new SmartCacheManager("a", "https://strategy.invalid/a", Data.Length, http);
            await ranged.InitializeAsync(default, true);
            using var sequential = new SmartCacheManager("b", "https://strategy.invalid/b", Data.Length, http, true);
            await sequential.InitializeAsync(default, true);
            Program.Check(ranged.Strategy == DownloadStrategy.Range && sequential.Strategy == DownloadStrategy.SequentialFull,
                "same CDN host cannot force the preceding track's transfer strategy");
        });
        await Case("stale preload falls back to fresh playback", async () =>
        {
            byte[] bytes = await File.ReadAllBytesAsync(fixture);
            using var h = new Handler((r, _) => r.Method == HttpMethod.Head ? Head(bytes.Length) : Range(r, bytes));
            using var http = new HttpClient(h);
            using var engine = new BassAudioEngine(); engine.SetVolume(0);
            ReplaceClient(engine, http);
            var song = new SongInfo { Id = "preload-recovery", Name = "Synthetic fixture", Url = "https://preload.invalid/audio", Size = bytes.Length, Duration = 6 };
            var cache = new SmartCacheManager(song.Id, song.Url, song.Size, http);
            await cache.InitializeAsync(default, true);
            var provider = new BassStreamProvider(cache);
            int stream = provider.CreateStream();
            Program.Check(stream != 0, "real BASS preload created");
            BASS_StreamFree(stream);
            var stale = new PreloadedData { Url = song.Url, Size = song.Size, IsReady = true, CacheManager = cache, StreamProvider = provider, StreamHandle = stream };
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            bool success = await engine.PlayAsync(song, deadline.Token, stale);
            Program.Check(success && engine.CurrentSong?.Id == song.Id, "invalid native preload cannot kill the next track");
            engine.Stop();
        });
        foreach (bool stale in new[] { false, true })
            await Case(stale ? "natural end recovers invalid gapless preload" : "natural end commits gapless preload once", async () =>
            {
                byte[] bytes = await File.ReadAllBytesAsync(fixture);
                using var h = new Handler((r, _) => r.Method == HttpMethod.Head ? Head(bytes.Length) : Range(r, bytes));
                using var http = new HttpClient(h);
                using var engine = new BassAudioEngine(); engine.SetVolume(0); ReplaceClient(engine, http);
                var first = Song("gapless-first", bytes.Length);
                var next = Song("gapless-next", bytes.Length);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                Program.Check(await engine.PlayAsync(first, deadline.Token), "first track starts");
                var cache = new SmartCacheManager(next.Id, next.Url, next.Size, http);
                await cache.InitializeAsync(deadline.Token, true);
                var provider = new BassStreamProvider(cache);
                int stream = provider.CreateStream();
                Program.Check(stream != 0, "next track prepared with real BASS");
                if (stale) BASS_StreamFree(stream);
                int ended = 0, transitioned = 0, errors = 0;
                var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                engine.PlaybackError += (_, _) => Interlocked.Increment(ref errors);
                engine.GaplessTransitionCompleted += (_, _) => { Interlocked.Increment(ref transitioned); finished.TrySetResult(true); };
                engine.PlaybackEnded += async (_, song) =>
                {
                    if (song.Id != first.Id) return;
                    Interlocked.Increment(ref ended);
                    try { finished.TrySetResult(await engine.PlayAsync(next, deadline.Token)); }
                    catch (Exception ex) { finished.TrySetException(ex); }
                };
                engine.RegisterGaplessPreload(next, new PreloadedData { Url = next.Url, Size = next.Size, IsReady = true,
                    CacheManager = cache, StreamProvider = provider, StreamHandle = stream });
                Program.Check(await engine.SetPositionWithCacheWaitAsync(5, 2000, deadline.Token), "first track seeks near natural end");
                Program.Check(await finished.Task.WaitAsync(TimeSpan.FromSeconds(6)), "next track starts through actual end callback");
                Program.Check(engine.CurrentSong?.Id == next.Id && errors == 0 && ended == (stale ? 1 : 0) && transitioned == (stale ? 0 : 1),
                    "exactly one advance, no false playback error");
                engine.Stop(); await Task.Delay(250);
            });
        await Case("cancelled replacement preserves active track", async () =>
        {
            byte[] bytes = await File.ReadAllBytesAsync(fixture);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var h = new AsyncHandler(async (r, token) =>
            {
                if (r.RequestUri!.AbsolutePath.EndsWith("blocked")) { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
                return r.Method == HttpMethod.Head ? Head(bytes.Length) : Range(r, bytes);
            });
            using var http = new HttpClient(h);
            using var engine = new BassAudioEngine(); engine.SetVolume(0); ReplaceClient(engine, http);
            int errors = 0; engine.PlaybackError += (_, _) => Interlocked.Increment(ref errors);
            Program.Check(await engine.PlayAsync(Song("kept", bytes.Length)), "original playback starts");
            engine.Pause();
            using var cancel = new CancellationTokenSource();
            Task<bool> blocked = engine.PlayAsync(Song("blocked", bytes.Length), cancel.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var watch = Stopwatch.StartNew(); cancel.Cancel();
            Program.Check(!await blocked.WaitAsync(TimeSpan.FromSeconds(2)), "blocked startup is cancelled promptly");
            Program.Check(engine.CurrentSong?.Id == "kept" && engine.IsPaused && errors == 0 && watch.ElapsedMilliseconds < 1500,
                "cancel neither stops old song nor emits failure");
            Program.Check(await engine.PlayAsync(Song("newest", bytes.Length)).WaitAsync(TimeSpan.FromSeconds(3)), "newest playback proceeds after cancelled predecessor");
            engine.Stop(); await Task.Delay(250);
        });
        await Case("disposing sequential cache releases initialization waiter", async () =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var h = new AsyncHandler(async (r, token) =>
            {
                if (r.Method == HttpMethod.Head) return Head(Data.Length);
                if (r.Headers.Range != null) return Range(r, Data);
                entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return Full(Data);
            });
            using var http = new HttpClient(h);
            using var cache = new SmartCacheManager("dispose-init", "https://dispose-init.invalid/audio", Data.Length, http, true);
            var initialization = cache.InitializeAsync(default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); cache.Dispose();
            try { Program.Check(!await initialization.WaitAsync(TimeSpan.FromSeconds(2)), "disposed cache cannot become ready"); }
            catch (OperationCanceledException) { Program.Check(true, "dispose cancels initialization"); }
        });
        await Case("retry budget is finite", async () =>
        {
            using var h = new Handler((_, _) => new(HttpStatusCode.ServiceUnavailable));
            using var http = new HttpClient(h);
            Program.Check(await new ChunkDownloadManager("https://budget.invalid/audio", 1024, 256, http).DownloadChunkAsync(0, default) == null && h.Count == 5,
                "persistent 503 exhausts exactly five requests");
        });
        await Case("Retry-After can be cancelled", async () =>
        {
            using var h = new Handler((_, _) => { var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests); response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(1)); return response; });
            using var http = new HttpClient(h); using var cancel = new CancellationTokenSource(150);
            var watch = Stopwatch.StartNew();
            try { await new ChunkDownloadManager("https://retry-after.invalid/audio", 1024, 256, http).DownloadChunkAsync(0, cancel.Token); throw new Exception("Cancellation swallowed"); }
            catch (OperationCanceledException) { Program.Check(h.Count == 1 && watch.ElapsedMilliseconds < 1000, "cancel interrupts server-directed backoff"); }
        });
        await Case("new preload invalidates delayed predecessor", async () =>
        {
            byte[] bytes = await File.ReadAllBytesAsync(fixture);
            using var h = new Handler((r, _) => r.Method == HttpMethod.Head ? Head(bytes.Length) : Range(r, bytes));
            using var http = new HttpClient(h);
            using var engine = new BassAudioEngine(); engine.SetVolume(0);
            using var api = new NeteaseApiClient();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var loader = new NextSongPreloader(api, async (s, _, _) =>
            {
                if (s.Id == "older") { entered.TrySetResult(); await release.Task; }
                return SongResolveResult.Success();
            });
            ReplaceClient(loader, http);
            Task<bool> older = loader.StartPreloadAsync(Song("older", bytes.Length), default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Task<bool> newer = loader.StartPreloadAsync(Song("newer", bytes.Length), default);
            release.TrySetResult();
            Program.Check(!await older.WaitAsync(TimeSpan.FromSeconds(2)), "superseded resolver cannot publish even if it ignores cancellation");
            Program.Check(await newer.WaitAsync(TimeSpan.FromSeconds(5)), "newest preload succeeds");
            Program.Check(loader.TryGetPreloadedData("older") == null, "no obsolete preload remains");
            using var prepared = loader.TryGetPreloadedData("newer");
            Program.Check(prepared?.IsReady == true && loader.TryGetPreloadedData("newer") == null, "ready resources transfer exactly once");
        });
        await Case("dispose invalidates pending preload", async () =>
        {
            using var api = new NeteaseApiClient();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var loader = new NextSongPreloader(api, async (_, _, _) => { entered.TrySetResult(); await release.Task; return SongResolveResult.Success(); });
            Task<bool> pending = loader.StartPreloadAsync(Song("disposed", 1024), default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); loader.Dispose(); release.TrySetResult();
            Program.Check(!await pending.WaitAsync(TimeSpan.FromSeconds(2)) && loader.TryGetPreloadedData("disposed") == null,
                "disposed loader cannot publish resources later");
            Program.Check(!await loader.StartPreloadAsync(Song("after-dispose", 1024), default), "disposed loader rejects further work");
        });
        await Case("invalid resume interval never reaches cache", async () =>
        {
            using var h = new Handler((r, n) =>
            {
                if (n == 1) return Full(Data[..384]);
                var response = Range(r, Data);
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 767, 1024); return response;
            });
            using var http = new HttpClient(h);
            var chunks = new List<int>();
            try
            {
                await new ChunkDownloadManager("https://bad-resume.invalid/audio", 1024, 256, http)
                    .DownloadAllChunksSequentialAsync((i, _) => { chunks.Add(i); return Task.CompletedTask; }, null, default);
                throw new Exception("Bad range accepted");
            }
            catch (InvalidDataException) { Program.Check(h.Count == 2 && chunks.SequenceEqual(new[] { 0 }), "reject mismatched response before publishing any corrupt block"); }
        });
        await Case("cancel before acquiring play gate releases consumed preload", async () =>
        {
            using var engine = new BassAudioEngine(); engine.SetVolume(0);
            using var http = new HttpClient(new Handler((r, _) => Range(r, Data)));
            var song = Song("cancel-before-gate", Data.Length);
            var cache = new SmartCacheManager(song.Id, song.Url, song.Size, http);
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            Program.Check(!await engine.PlayAsync(song, cancel.Token, new PreloadedData { CacheManager = cache }), "cancelled queued play returns without failure");
            Program.Check(cache.IsDisposed, "queued cancellation releases transferred cache");
        });
        await Case("late preload cannot attach to a different current track", async () =>
        {
            using var engine = new BassAudioEngine(); engine.SetVolume(0);
            using var http = new HttpClient(new Handler((r, _) => Range(r, Data)));
            var song = Song("late-preload", Data.Length);
            var cache = new SmartCacheManager(song.Id, song.Url, song.Size, http);
            engine.RegisterGaplessPreload(song, new PreloadedData { CacheManager = cache }, Song("obsolete-parent", Data.Length));
            Program.Check(engine.ConsumeGaplessPreload(song.Id) == null, "parent mismatch rejects preload");
            for (int i = 0; i < 100 && !cache.IsDisposed; i++) await Task.Delay(10);
            Program.Check(cache.IsDisposed, "rejected preload is released asynchronously");
        });
        await Case("real HTTP connection closing mid-body resumes exact bytes", async () =>
        {
            using var server = new TcpListener(IPAddress.Loopback, 0); server.Start();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var starts = new List<string>();
            Task serving = Task.Run(async () =>
            {
                for (int n = 0; n < 2; n++)
                {
                    using var connection = await server.AcceptTcpClientAsync(deadline.Token);
                    await using var stream = connection.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    string? line;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(deadline.Token)))
                        if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) starts.Add(line);
                    string headers = n == 0 ? "HTTP/1.1 200 OK\r\nContent-Length: 1024\r\n" :
                        "HTTP/1.1 206 Partial Content\r\nContent-Length: 768\r\nContent-Range: bytes 256-1023/1024\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(headers + "Connection: close\r\n\r\n"), deadline.Token);
                    await stream.WriteAsync(n == 0 ? Data[..384] : Data[256..], deadline.Token);
                    // First response deliberately closes with 640 advertised bytes missing.
                }
            });
            try
            {
                using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
                var chunks = new SortedDictionary<int, byte[]>();
                int port = ((IPEndPoint)server.LocalEndpoint).Port;
                bool complete = await new ChunkDownloadManager($"http://127.0.0.1:{port}/fixture", 1024, 256, http)
                    .DownloadAllChunksSequentialAsync((i, b) => { chunks.Add(i, b); return Task.CompletedTask; }, null, deadline.Token);
                await serving;
                Program.Check(complete && chunks.Values.SelectMany(x => x).SequenceEqual(Data), "real socket truncation reconstructs the original file exactly");
                Program.Check(starts.SequenceEqual(new[] { "Range: bytes=256-" }), "actual HTTP resume starts at last validated block");
            }
            finally { deadline.Cancel(); try { await serving; } catch (OperationCanceledException) { } }
        });
        if (failures.Count > 0) throw new Exception(string.Join("; ", failures));
    }

    private static SongInfo Song(string id, int size) => new() { Id = id, Name = "Synthetic fixture", Url = "https://fixture-recovery.invalid/" + id, Size = size, Duration = 6 };
    private static void ReplaceClient(object owner, HttpClient client)
    {
        var field = owner.GetType().GetField("_httpClient", BindingFlags.NonPublic | BindingFlags.Instance)!;
        ((HttpClient)field.GetValue(owner)!).Dispose(); field.SetValue(owner, client);
    }

    private static HttpResponseMessage Head(int length)
    {
        var result = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
        result.Content.Headers.ContentLength = length; result.Headers.AcceptRanges.Add("bytes"); return result;
    }
    private static HttpResponseMessage Full(byte[] bytes) => new(HttpStatusCode.OK)
        { Content = new StreamContent(new NonSeekableStream(bytes)) };
    private static HttpResponseMessage Range(HttpRequestMessage request, byte[] bytes)
    {
        if (request.Headers.Range == null) return Full(bytes);
        var range = request.Headers.Range.Ranges.Single();
        int start = (int)(range.From ?? 0), end = (int)(range.To ?? bytes.Length - 1);
        var result = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(bytes[start..(end + 1)]) };
        result.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, bytes.Length); return result;
    }
    private sealed class Handler(Func<HttpRequestMessage, int, HttpResponseMessage> send) : HttpMessageHandler
    {
        internal int Count;
        internal readonly List<long?> Starts = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            int n = Interlocked.Increment(ref Count);
            lock (Starts) Starts.Add(request.Headers.Range?.Ranges.Single().From);
            return Task.FromResult(send(request, n));
        }
    }
    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
}
