using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnblockNCM.Core.Config;
using UnblockNCM.Core.Logging;
using UnblockNCM.Core.Models;
using UnblockNCM.Core.Providers;

namespace UnblockNCM.Core.Services
{
    public class ProviderManager
    {
        public static readonly string[] DefaultSources = new[] { "kugou", "kuwo", "qq", "bodian", "migu", "joox", "bilibili", "bilivideo", "pyncmd" };

        private readonly Dictionary<string, IProvider> _providers;
        private readonly FindService _find;
        private readonly UnblockOptions _options;
        private readonly string _scope = "provider/match";
        private readonly int _providerTimeoutMs;
        private readonly string _defaultYtDlp;
        private static readonly HttpClient ValidateHttp = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = true
        });

        public ProviderManager(Dictionary<string, IProvider> providers, FindService find, UnblockOptions options)
        {
            _providers = providers;
            _find = find;
            _options = options;
            _providerTimeoutMs = int.TryParse(Environment.GetEnvironmentVariable("PROVIDER_TIMEOUT_MS"), out var ms) ? ms : 8000;
            _defaultYtDlp = System.IO.Path.Combine(AppContext.BaseDirectory, "yt-dlp.exe");
        }

        private IEnumerable<string> BuildCandidate()
        {
            var list = (_options.MatchOrder != null && _options.MatchOrder.Count > 0)
                ? _options.MatchOrder
                : DefaultSources.ToList();
            return list.Where(name => _providers.ContainsKey(name));
        }

        public async Task<AudioResult> MatchAsync(string id, JObject rawData, CancellationToken ct)
        {
            var info = await _find.GetAsync(id, rawData, ct);
            var candidates = BuildCandidate().ToList();
            if (!candidates.Any())
                throw new InvalidOperationException("no provider candidates");

            var tasks = candidates.Select(async name =>
            {
                try
                {
                    var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(_providerTimeoutMs);
                    var res = await _providers[name].CheckAsync(info, cts.Token);
                    res.Source = name;
                    return (ok: true, res, name, ex: (Exception)null);
                }
                catch (Exception ex)
                {
                    return (ok: false, res: (AudioResult)null, name, ex);
                }
            }).ToList();

            var results = await Task.WhenAll(tasks);
            var success = results.Where(r => r.ok && r.res != null).Select(r => r.res).ToList();
            foreach (var fail in results.Where(r => !r.ok && r.ex != null))
                Log.Debug(_scope, $"{fail.name} failed: {fail.ex.Message}");

            if (!success.Any()) throw new InvalidOperationException("all providers failed");

            long targetDur = info.Duration;
            double Score(AudioResult r)
            {
                var dur = r.DurationMs ?? targetDur;
                var durDiff = targetDur > 0 ? Math.Abs(dur - targetDur) : 0;
                var titleScore = Similarity(info.Name, r.Title) * 0.6 + Similarity(string.Join(" / ", info.Artists.Select(a => a.Name)), r.Artists) * 0.4;
                // Smaller score is better: duration diff minus bonus for similarity
                return durDiff - titleScore * 1000;
            }

            if (_options.SelectMaxBr)
            {
                success = success
                    .OrderByDescending(r => r.BitRate ?? 0)
                    .ThenBy(Score)
                    .ToList();
                return await PickValidAsync(success, ct);
            }
            if (_options.FollowSourceOrder)
            {
                // still evaluate all but preserve order priority when scores tie
                var ordered = success
                    .OrderBy(Score)
                    .ThenBy(r => candidates.IndexOf(r.Source))
                    .ToList();
                return await PickValidAsync(ordered, ct);
            }
            // default: choose best score
            return await PickValidAsync(success.OrderBy(Score).ToList(), ct);
        }

        private static double Similarity(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return 0;
            var tokensA = a.ToLower().Split(new[] { ' ', '/', '-', '_', '·' }, StringSplitOptions.RemoveEmptyEntries).Distinct().ToHashSet();
            var tokensB = b.ToLower().Split(new[] { ' ', '/', '-', '_', '·' }, StringSplitOptions.RemoveEmptyEntries).Distinct().ToHashSet();
            var inter = tokensA.Intersect(tokensB).Count();
            var union = tokensA.Union(tokensB).Count();
            return union == 0 ? 0 : (double)inter / union;
        }

        private async Task<AudioResult> PickValidAsync(IReadOnlyList<AudioResult> ordered, CancellationToken ct)
        {
            foreach (var r in ordered)
            {
                if (await ValidateAsync(r, ct))
                    return r;
                Log.Debug(_scope, $"{r.Source} url validation failed, try next.");
            }
            // if none validate, fall back to first to keep behavior consistent with node
            return ordered.First();
        }

        private async Task<bool> ValidateAsync(AudioResult result, CancellationToken ct)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, result.Url);
                req.Headers.Range = new RangeHeaderValue(0, 0);
                req.Headers.UserAgent.ParseAdd("Mozilla/5.0");
                using var resp = await ValidateHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return false;
                var ctType = resp.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
                if (ctType == null) return false;
                if (ctType.Contains("audio") || ctType.Contains("video") || ctType.Contains("octet"))
                    return true;
                // try to read small chunk to detect JSON error body
                var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                if (!stream.CanRead) return false;
                byte[] buf = new byte[32];
                var read = await stream.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false);
                if (read == 0) return false;
                var head = System.Text.Encoding.UTF8.GetString(buf, 0, read);
                // if starts with {errorcode:...} from QQ or similar, treat as invalid
                if (head.TrimStart().StartsWith("{")) return false;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
