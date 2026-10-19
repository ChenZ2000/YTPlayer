using System.Media;
using YTPlayer.Core.Recognition;
using YTPlayer.Models;

internal static class CaptureProbe
{
    internal static async Task RunAsync(string[] args)
    {
        var service = new AudioCaptureService();
        var devices = service.EnumerateDevices();
        var input = devices.Single(d => d.DeviceId == AudioInputDeviceInfo.WindowsDefaultId);
        var loop = devices.Single(d => d.DeviceId == AudioInputDeviceInfo.LoopbackId);
        foreach (var device in new[] { input, loop })
            for (int i = 0; i < 2; i++)
            {
                using var cancel = new CancellationTokenSource(150);
                await Program.Throws<OperationCanceledException>(() => service.CaptureAsync(device, 3, cancel.Token), "capture cancellation");
                Console.WriteLine($"PASS capture: {(device.IsLoopback ? "loopback" : "microphone")} cancellation/reopen {i + 1}");
            }
        byte[] mic = await service.CaptureAsync(input, 3, default);
        Program.Check(mic.Length == 48000, "microphone exact sample count");
        Console.WriteLine("PASS capture: microphone delivered exactly 24000 samples; discarded without upload.");
        byte[] pcm = File.ReadAllBytes(args[0]);
        string wav = Path.Combine(AppContext.BaseDirectory, "loopback-fixture.wav");
        using (var output = new BinaryWriter(File.Create(wav)))
        {
            output.Write("RIFF"u8); output.Write(36 + pcm.Length); output.Write("WAVEfmt "u8);
            output.Write(16); output.Write((short)1); output.Write((short)1); output.Write(8000); output.Write(16000); output.Write((short)2); output.Write((short)16);
            output.Write("data"u8); output.Write(pcm.Length); output.Write(pcm);
        }
        using var player = new SoundPlayer(wav);
        byte[] recorded;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Task<byte[]> recording = service.CaptureAsync(loop, 6, deadline.Token);
            player.PlayLooping();
            recorded = await recording;
        }
        finally { player.Stop(); }
        Program.Check(recorded.Length == 96000 && SongRecognitionCoordinator.HasSignal(recorded), "loopback sample count and signal");
        using var fp = new AudioFingerprintGenerator();
        string fingerprint = await fp.GenerateAsync(recorded, 8000, 1, default);
        using var client = Program.AccountClient(args[1]);
        var matches = await client.RecognizeSongAsync(fingerprint, 6, default);
        Program.Check(matches.Any(s => s.Id == "3399839173"), "known audio survives actual Windows loopback and matches");
        Console.WriteLine("PASS capture: actual Windows loopback -> PCM -> WASM -> live service matched fixture song.");
    }
}
