using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnblockNCM.Core.Config;
using UnblockNCM.Core.Net;
using UnblockNCM.Core.Services;
using UnblockNCM.Core.Providers;
using UnblockNCM.Core.Utils;

namespace UnblockNCM.Core.Tests
{
    public static class DirectTest
    {
        public static async Task RunAsync(IEnumerable<string> songIds)
        {
            var opt = new UnblockOptions
            {
                EnableLocalVip = true,
                EnableFlac = true,
                MinBr = 0,
                MatchOrder = new System.Collections.Generic.List<string> { "pyncmd", "kuwo", "kugou", "qq", "migu" }
            };

            Logging.Log.MinLevel = Logging.Log.Level.Debug;

            var http = new HttpHelper();
            var cache = new CacheStorage();
            var find = new FindService(http, cache, false, opt.NoCache);

            var providers = new Dictionary<string, IProvider>(StringComparer.OrdinalIgnoreCase)
            {
                { "kugou", new KugouProvider(http, cache, opt.NoCache) },
                { "kuwo", new KuwoProvider(http, cache, opt.NoCache) },
                { "qq", new QQProvider(http, cache, opt.NoCache) },
                { "bodian", new BodianProvider(http, cache, opt.NoCache) },
                { "migu", new MiguProvider(http, cache, Environment.GetEnvironmentVariable("MIGU_COOKIE"), opt.NoCache) },
                { "joox", new JooxProvider(http, cache, opt.NoCache) },
                { "bilibili", new BilibiliProvider(http, cache, opt.NoCache) },
                { "bilivideo", new BilivideoProvider(http, cache, opt.NoCache) },
                { "pyncmd", new PyncmdProvider(http, cache, opt.NoCache) },
            };
            var pm = new ProviderManager(providers, find, opt);
            using var relay = new StreamRelay();
            var outputFile = Environment.GetEnvironmentVariable("OUTPUT_FILE");

            foreach (var id in songIds)
            {
                try
                {
                    Console.WriteLine($"---- {id} all providers ----");
                    foreach (var kv in providers)
                    {
                        try
                        {
                            var res = await kv.Value.CheckAsync(await find.GetAsync(id, null, CancellationToken.None), CancellationToken.None);
                            Console.WriteLine($"{kv.Key,-10} ok  dur={res.DurationMs} br={res.BitRate} title={res.Title} artists={res.Artists} url={res.Url}");
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine($"{kv.Key,-10} fail {e.Message}");
                        }
                    }

                    var audio = await pm.MatchAsync(id, null, CancellationToken.None);
                    Console.WriteLine($"best[{id}] source={audio.Source} dur={audio.DurationMs} br={audio.BitRate} url={audio.Url}");

                    // relay download for playback verification
                    var path = await relay.DownloadToFileAsync(audio, CancellationToken.None);
                    Console.WriteLine($"relayed[{id}] saved to {path}");
                    if (!string.IsNullOrWhiteSpace(outputFile))
                    {
                        System.IO.File.Copy(path, outputFile, overwrite: true);
                        Console.WriteLine($"copied to {outputFile}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{id}] FAILED: {ex.Message}");
                }
            }
        }
    }
}
