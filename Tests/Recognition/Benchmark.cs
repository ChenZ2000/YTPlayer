using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using YTPlayer.Core.Recognition;

internal static class Benchmark
{
    internal static async Task RunAsync(string account, string manifest, string output)
    {
        byte[] originalHash = SHA256.HashData(File.ReadAllBytes(account));
        using var client = Program.AccountClient(account);
        using var generator = new AudioFingerprintGenerator();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var report = new JArray();
        try
        {
            foreach (var test in JArray.Parse(File.ReadAllText(manifest)).OfType<JObject>())
            {
                string expected = test.Value<string>("songId")!;
                byte[] pcm = File.ReadAllBytes(test.Value<string>("pcm")!);
                var native = JArray.Parse(File.ReadAllText(test.Value<string>("native")!));
                foreach (string mode in new[] { "previous-six-second", "android-jni", "improved-progressive" })
                {
                    var row = new JObject { ["case"] = test.Value<string>("name"), ["mode"] = mode, ["expected"] = expected };
                    string session = Guid.NewGuid().ToString("N");
                    var attempts = new JArray(); row["attempts"] = attempts;
                    try
                    {
                        if (mode == "improved-progressive")
                        {
                            var source = new FixtureCapture(pcm);
                            var coordinator = new SongRecognitionCoordinator(source, generator, async (fp, duration, sessionId, attempt, token) =>
                            {
                                var found = await client.RecognizeSongAsync(fp, duration, sessionId, attempt, token);
                                attempts.Add(new JObject { ["seconds"] = source.Captured, ["windowSeconds"] = duration,
                                    ["times"] = attempt, ["ids"] = new JArray(found.Select(s => s.Id)) });
                                return found;
                            });
                            var result = await coordinator.RecognizeAsync(new(), pcm.Length / 16000, default);
                            row["matched"] = result.Matches.Any(s => s.Id == expected);
                            row["top1"] = result.Matches.FirstOrDefault()?.Id == expected;
                        }
                        else for (int i = mode == "previous-six-second" ? 1 : 0; i < native.Count; i++)
                        {
                            int seconds = (i + 1) * 3;
                            string fp = mode == "android-jni" ? native[i].Value<string>("fingerprint")! :
                                await generator.GenerateAsync(pcm.AsSpan(0, seconds * 16000).ToArray(), 8000, 1, default);
                            List<YTPlayer.Models.SongInfo> songs;
                            if (mode == "previous-six-second")
                            {
                                using var response = await http.GetAsync("https://interface.music.163.com/api/music/audio/match?sessionId=" + session +
                                    "&algorithmCode=shazam_v2&duration=6&rawdata=" + Uri.EscapeDataString(fp) + "&times=1&decrypt=1");
                                response.EnsureSuccessStatusCode();
                                songs = client.ParseRecognitionResponse(JObject.Parse(await response.Content.ReadAsStringAsync()));
                            }
                            else songs = await client.RecognizeSongAsync(fp, seconds, session, i + 1, default);
                            attempts.Add(new JObject { ["seconds"] = seconds, ["ids"] = new JArray(songs.Select(s => s.Id)) });
                            row["matched"] = songs.Any(s => s.Id == expected);
                            row["top1"] = songs.FirstOrDefault()?.Id == expected;
                            if (songs.Count > 0 || mode == "previous-six-second") break;
                            await Task.Delay(250);
                        }
                    }
                    catch (Exception e) { row["error"] = e.GetType().Name + ": " + e.Message; }
                    report.Add(row); File.WriteAllText(output, report.ToString());
                    Console.WriteLine($"{test.Value<string>("name")} / {mode}: matched={row["matched"]}, top1={row["top1"]}, attempts={attempts.Count}, error={row["error"]}");
                    await Task.Delay(250);
                }
            }
        }
        finally
        {
            if (!originalHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(account)))) throw new Exception("Original account file changed");
            Console.WriteLine("PASS: original account file unchanged.");
        }
    }
    private sealed class FixtureCapture(byte[] pcm) : IAudioCaptureSource
    {
        public int Captured;
        public IReadOnlyList<YTPlayer.Models.AudioInputDeviceInfo> EnumerateDevices() => Array.Empty<YTPlayer.Models.AudioInputDeviceInfo>();
        public async IAsyncEnumerable<byte[]> CaptureProgressiveAsync(YTPlayer.Models.AudioInputDeviceInfo device, int duration,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            for (int seconds = 3; seconds <= duration; seconds += 3)
            {
                token.ThrowIfCancellationRequested(); await Task.Yield(); Captured = seconds;
                yield return pcm.AsSpan(0, seconds * 16000).ToArray();
            }
        }
    }
}
