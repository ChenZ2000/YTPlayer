using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using YTPlayer.Core;
using YTPlayer.Models;
using YTPlayer.Utils;

internal static class Program
{
    private static int passed;
    private static readonly BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

    private static async Task<int> Main(string[] args)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "test-data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("YTPLAYER_ROOT", root);
        try
        {
            if (args.Length == 2 && args[0] == "--live") return await LiveAsync(args[1]);
            await OfflineAsync();
            Console.WriteLine($"PASS: {passed} assertions; no live account or network used.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message); return 1; }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        passed++;
    }

    private static async Task OfflineAsync()
    {
        using var client = new NeteaseApiClient();
        client.SetCookieString("MUSIC_U=fm-fixture; __csrf=fm-csrf; extra_session=a%2Fb");
        using var fixture = new FmFixture();
        var field = typeof(NeteaseApiClient).GetField("_eapiClient", Fields)!;
        ((HttpClient)field.GetValue(client)!).Dispose();
        field.SetValue(client, new HttpClient(fixture));
        Check(PersonalFmMode.All.Count == 46 && PersonalFmMode.All.Select(m => m.Key).Distinct().Count() == 46, "46 distinct choices");
        Check(PersonalFmMode.FromKey("INVALID") == PersonalFmMode.Default, "invalid saved preference falls back to default");
        foreach (PersonalFmMode mode in PersonalFmMode.All)
        {
            fixture.Expected = mode;
            var songs = await client.GetPersonalFMAsync(mode);
            Check(songs.Count == 1 && songs[0].Id == "101", mode.Key + " parses decoded song data");
            Check(songs[0].RecommendationAlgorithm == "fixture-algorithm", mode.Key + " preserves feedback algorithm");
        }
        fixture.Expected = PersonalFmMode.Default;
        await client.GetPersonalFMAsync();
        fixture.Code = 500;
        try { await client.GetPersonalFMAsync(); throw new Exception("business error was accepted"); }
        catch (InvalidOperationException) { Check(true, "business error is not an empty-success response"); }
        fixture.Code = 200; fixture.InvalidShape = true;
        try { await client.GetPersonalFMAsync(); throw new Exception("malformed list was accepted"); }
        catch (InvalidOperationException) { Check(true, "malformed list rejected"); }
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        int before = fixture.Requests;
        try { await client.GetPersonalFMAsync(cancellationToken: canceled.Token); throw new Exception("cancellation ignored"); }
        catch (OperationCanceledException) { Check(fixture.Requests == before, "cancellation stops request submission"); }
    }

    // Explicit opt-in, read-only live verification. Credentials remain in the supplied file.
    private static async Task<int> LiveAsync(string accountPath)
    {
        JObject account = JObject.Parse(await File.ReadAllTextAsync(accountPath));
        using var client = new NeteaseApiClient();
        client.SetCookieString(account.Value<string>("Cookie") ?? "");
        var user = await client.GetUserAccountAsync();
        bool sameUser = user != null && user.UserId.ToString() == account.Value<string>("UserId");
        Console.WriteLine(new JObject { ["kind"] = "session", ["sameUser"] = sameUser }.ToString(Newtonsoft.Json.Formatting.None));
        if (!sameUser) return 2;
        int valid = 0;
        foreach (PersonalFmMode mode in PersonalFmMode.All)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                var songs = await client.GetPersonalFMAsync(mode, timeout.Token);
                bool usable = songs.Count > 0 && songs.All(s => !string.IsNullOrWhiteSpace(s.Id) && !string.IsNullOrWhiteSpace(s.Name));
                if (usable) valid++;
                Console.WriteLine(new JObject { ["kind"] = "mode", ["mode"] = mode.Key, ["name"] = mode.Name,
                    ["count"] = songs.Count, ["usable"] = usable,
                    ["algorithms"] = new JArray(songs.Select(s => s.RecommendationAlgorithm)) }.ToString(Newtonsoft.Json.Formatting.None));
            }
            catch (Exception ex)
            {
                Console.WriteLine(new JObject { ["kind"] = "mode", ["mode"] = mode.Key, ["usable"] = false,
                    ["errorType"] = ex.GetType().Name }.ToString(Newtonsoft.Json.Formatting.None));
            }
            await Task.Delay(350);
        }
        Console.WriteLine(new JObject { ["kind"] = "summary", ["valid"] = valid, ["total"] = PersonalFmMode.All.Count }.ToString(Newtonsoft.Json.Formatting.None));
        return valid == PersonalFmMode.All.Count ? 0 : 1;
    }

    private sealed class FmFixture : HttpMessageHandler
    {
        internal PersonalFmMode Expected = PersonalFmMode.Default;
        internal int Requests;
        internal int Code = 200;
        internal bool InvalidShape;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            Check(request.RequestUri!.AbsolutePath == "/eapi/v1/radio/get", "official EAPI wire path");
            string form = await request.Content!.ReadAsStringAsync(token);
            string clear = EncryptionHelper.DecryptEapi(Convert.FromHexString(Uri.UnescapeDataString(form.Split('=')[1])));
            string[] parts = clear.Split("-36cd479b6b5-");
            Check(parts[0] == "/api/v1/radio/get", "official EAPI signature path");
            JObject payload = JObject.Parse(parts[1]);
            Check(payload.Value<string>("mode") == Expected.Mode && payload.Value<string>("subMode") == Expected.SubMode, "mode and subMode case/types");
            Check(payload["limit"]?.Type == JTokenType.String && payload.Value<string>("limit") == "3", "limit format");
            Check(payload.Value<string>("e_r") == "true", "encrypted response requested");
            Check(payload["header"]?["MUSIC_U"]?.Value<string>() == "fm-fixture", "inner session header");
            Check(request.Headers.GetValues("Cookie").Single().Contains("extra_session=a%2Fb"), "full outer cookie preserved");
            JObject body = new() { ["code"] = Code, ["data"] = InvalidShape ? new JObject() : new JArray(new JObject
                { ["id"] = 101, ["name"] = "协议测试", ["alg"] = "fixture-algorithm", ["artists"] = new JArray(new JObject { ["name"] = "测试歌手" }), ["duration"] = 180000 }) };
            byte[] bytes = Encoding.UTF8.GetBytes(body.ToString());
            if (Requests % 4 == 1 || Requests % 4 == 3)
            {
                using var aes = Aes.Create();
                aes.Key = Encoding.UTF8.GetBytes("e82ckenh8dichen8");
                bytes = aes.EncryptEcb(bytes, PaddingMode.PKCS7);
            }
            if (Requests % 4 == 2 || Requests % 4 == 3)
            {
                using var memory = new MemoryStream();
                using (var gzip = new GZipStream(memory, CompressionMode.Compress, leaveOpen: true)) gzip.Write(bytes);
                bytes = memory.ToArray();
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }
    }
}
