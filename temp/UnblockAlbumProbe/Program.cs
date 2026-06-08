using System.Net;
using System.Net.Http.Headers;
using Newtonsoft.Json.Linq;
using UnblockNCM.Core.Net;
using UnblockNCM.Core.Providers;
using UnblockNCM.Core.Utils;
using YTPlayer.Core.Unblock;
using YTPlayer.Models;

const string AlbumId = "154903005";

static async Task<string> ReadStringAsync(string url, CancellationToken cancellationToken)
{
    using var http = new HttpClient(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        AllowAutoRedirect = true
    });
    http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
    http.DefaultRequestHeaders.Referrer = new Uri("https://music.163.com/");
    return await http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
}

static SongInfo ParseSong(JToken item)
{
    var artists = item["ar"] ?? item["artists"] ?? new JArray();
    var artistNames = artists
        .Select(artist => artist?["name"]?.ToString())
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Select(name => name!)
        .ToList();
    var artistIds = artists
        .Select(artist => long.TryParse(artist?["id"]?.ToString(), out long id) ? id : 0L)
        .Where(id => id > 0)
        .ToList();
    var album = item["al"] ?? item["album"];
    long durationMs = item["dt"]?.Value<long?>() ?? item["duration"]?.Value<long?>() ?? 0;

    return new SongInfo
    {
        Id = item["id"]?.ToString() ?? string.Empty,
        Name = item["name"]?.ToString() ?? string.Empty,
        Artist = string.Join("/", artistNames),
        ArtistNames = artistNames,
        ArtistIds = artistIds,
        Album = album?["name"]?.ToString() ?? string.Empty,
        AlbumId = album?["id"]?.ToString() ?? string.Empty,
        Duration = durationMs > 0 ? (int)Math.Round(durationMs / 1000.0) : 0,
        IsAvailable = false
    };
}

static async Task<long> ProbeLengthAsync(string url, IDictionary<string, string>? headers, CancellationToken cancellationToken)
{
    using var http = new HttpClient(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        AllowAutoRedirect = true
    });
    using var req = new HttpRequestMessage(HttpMethod.Get, url);
    req.Headers.Range = new RangeHeaderValue(0, 0);
    req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
    if (headers != null)
    {
        foreach (var header in headers)
        {
            if (string.Equals(header.Key, "Range", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (string.Equals(header.Key, "User-Agent", StringComparison.OrdinalIgnoreCase))
            {
                req.Headers.UserAgent.Clear();
                req.Headers.UserAgent.ParseAdd(header.Value);
                continue;
            }
            req.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    return resp.Content.Headers.ContentRange?.Length ?? resp.Content.Headers.ContentLength ?? 0;
}

static UnblockNCM.Core.Models.SongInfo ToUnblockInfo(SongInfo song)
{
    var artists = (song.ArtistNames != null && song.ArtistNames.Count > 0)
        ? song.ArtistNames
        : (song.Artist ?? string.Empty)
            .Split(new[] { '/', '&' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .ToList();

    var info = new UnblockNCM.Core.Models.SongInfo
    {
        Id = song.Id,
        Name = song.Name,
        Duration = song.Duration * 1000L,
        Album = new UnblockNCM.Core.Models.SongInfo.AlbumInfo
        {
            Id = song.AlbumId,
            Name = song.Album
        },
        Artists = artists.Select((name, index) => new UnblockNCM.Core.Models.SongInfo.ArtistInfo
        {
            Id = song.ArtistIds != null && song.ArtistIds.Count > index ? song.ArtistIds[index].ToString() : string.Empty,
            Name = name
        }).ToList()
    };
    info.Keyword = $"{info.Name} - {string.Join(" / ", info.Artists.Select(artist => artist.Name))}";
    return info;
}

using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
string json = await ReadStringAsync($"https://music.163.com/api/v1/album/{AlbumId}", cts.Token).ConfigureAwait(false);
var root = JObject.Parse(json);
var songs = (root["songs"] as JArray ?? new JArray()).Select(ParseSong).Where(song => !string.IsNullOrWhiteSpace(song.Id)).ToList();

Console.WriteLine($"Album: {root["album"]?["name"]} ({AlbumId})");
Console.WriteLine($"Songs: {songs.Count}");

using var unblock = new UnblockService();
foreach (var song in songs)
{
    Console.WriteLine($"--- {song.Id} | {song.Name} | {song.Artist} | {song.Duration}s");
    try
    {
        using var matchCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var result = await unblock.TryMatchAsync(song, matchCts.Token).ConfigureAwait(false);
        if (result == null || string.IsNullOrWhiteSpace(result.Url))
        {
            Console.WriteLine("MISS");
            continue;
        }

        long length = 0;
        try
        {
            using var probeCts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            length = await ProbeLengthAsync(result.Url, result.Headers, probeCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"PROBE_FAILED: {ex.GetType().Name}: {ex.Message}");
        }

        string preview = result.Url.Length > 160 ? result.Url[..160] + "..." : result.Url;
        Console.WriteLine($"HIT source={result.Source} durationMs={result.DurationMs?.ToString() ?? "?"} size={result.Size} probeLength={length} bitrate={result.BitRate?.ToString() ?? "?"}");
        Console.WriteLine($"URL {preview}");
        if (result.Headers != null && result.Headers.Count > 0)
        {
            Console.WriteLine("HEADERS " + string.Join("; ", result.Headers.Select(pair => $"{pair.Key}={pair.Value}")));
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERROR {ex.GetType().Name}: {ex.Message}");
    }
}

Console.WriteLine("=== Provider diagnostics ===");
using var providerHttp = new HttpHelper(null, TimeSpan.FromSeconds(20));
var providerCache = new CacheStorage();
var providers = new Dictionary<string, IProvider>(StringComparer.OrdinalIgnoreCase)
{
    { "byfuns", new ByfunsProvider(providerCache, noCache: true) },
    { "gdmusic", new GdMusicProvider(providerCache, noCache: true) },
    { "baka", new BakaProvider(providerCache, noCache: true) },
    { "bikonoo", new BikonooProvider(providerCache, noCache: true) },
    { "msls", new MslsProvider(providerCache, noCache: true) },
    { "qijieya", new QijieyaProvider(providerCache, noCache: true) },
    { "pyncmd", new PyncmdProvider(providerHttp, providerCache, noCache: true) },
    { "bodian", new BodianProvider(providerHttp, providerCache, noCache: true) },
    { "qq", new QQProvider(providerHttp, providerCache, noCache: true) },
    { "kugou", new KugouProvider(providerHttp, providerCache, noCache: true) },
    { "kuwo", new KuwoProvider(providerHttp, providerCache, noCache: true) },
    { "migu", new MiguProvider(providerHttp, providerCache, Environment.GetEnvironmentVariable("MIGU_COOKIE"), noCache: true) },
    { "joox", new JooxProvider(providerHttp, providerCache, noCache: true) },
    { "bilibili", new BilibiliProvider(providerHttp, providerCache, noCache: true) },
    { "bilivideo", new BilivideoProvider(providerHttp, providerCache, noCache: true) }
};

foreach (var song in songs)
{
    var info = ToUnblockInfo(song);
    Console.WriteLine($"--- provider scan {song.Id} | {info.Keyword} | {info.Duration}ms");
    foreach (var pair in providers)
    {
        try
        {
            using var providerCts = new CancellationTokenSource(TimeSpan.FromSeconds(18));
            var audio = await pair.Value.CheckAsync(info, providerCts.Token).ConfigureAwait(false);
            if (audio == null || string.IsNullOrWhiteSpace(audio.Url))
            {
                Console.WriteLine($"{pair.Key}: MISS");
                continue;
            }

            long length = 0;
            try
            {
                using var probeCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                length = await ProbeLengthAsync(audio.Url, audio.Headers, probeCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{pair.Key}: HIT probeFailed={ex.GetType().Name}:{ex.Message}");
            }

            string preview = audio.Url.Length > 120 ? audio.Url[..120] + "..." : audio.Url;
            Console.WriteLine($"{pair.Key}: HIT len={length} dur={audio.DurationMs?.ToString() ?? "?"} br={audio.BitRate?.ToString() ?? "?"} title={audio.Title ?? ""} artists={audio.Artists ?? ""} url={preview}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{pair.Key}: FAIL {ex.GetType().Name}: {ex.Message}");
        }
    }
}
