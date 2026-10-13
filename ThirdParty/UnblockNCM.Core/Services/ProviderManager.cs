using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
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
        public static readonly string[] DefaultSources = new[]
        {
            "byfuns", "gdmusic", "baka", "bikonoo", "msls", "qijieya",
            "pyncmd", "bodian", "qq",
            "kugou", "kuwo", "migu", "joox", "bilibili", "bilivideo"
        };

        private const int ProbeBytes = 8192;
        private const int MinAverageBitRate = 56000;
        private const long AbsoluteMinimumAudioBytes = 96 * 1024;
        private const long PreferredDurationToleranceMs = 1000;

        private readonly Dictionary<string, IProvider> _providers;
        private readonly FindService _find;
        private readonly UnblockOptions _options;
        private readonly string _scope = "provider/match";
        private readonly int _providerTimeoutMs;
        private readonly int _bilibiliProviderTimeoutMs;
        private readonly int _validationTimeoutMs;
        private readonly int _firstValidGraceMs;
        private readonly int _trialOnlyGraceMs;
        private readonly int _bilibiliTrialGraceMs;

        private static readonly HttpClient ValidateHttp = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = true
        });

        private sealed class ProviderAttempt
        {
            public bool Ok { get; set; }
            public AudioResult Result { get; set; }
            public string Name { get; set; }
            public Exception Exception { get; set; }
        }

        private sealed class PendingProviderAttempt
        {
            public string Name { get; set; }
            public Task<ProviderAttempt> Task { get; set; }
        }

        private sealed class AudioValidationResult
        {
            public bool IsValid { get; set; }
            public string Reason { get; set; }

            public static AudioValidationResult Valid()
            {
                return new AudioValidationResult { IsValid = true, Reason = string.Empty };
            }

            public static AudioValidationResult Invalid(string reason)
            {
                return new AudioValidationResult { IsValid = false, Reason = reason ?? "unknown" };
            }
        }

        public ProviderManager(Dictionary<string, IProvider> providers, FindService find, UnblockOptions options)
        {
            _providers = providers;
            _find = find;
            _options = options;
            _providerTimeoutMs = ReadPositiveIntEnvironment("PROVIDER_TIMEOUT_MS", 12000);
            _bilibiliProviderTimeoutMs = ReadPositiveIntEnvironment("BILIBILI_PROVIDER_TIMEOUT_MS", Math.Max(_providerTimeoutMs, 30000));
            _validationTimeoutMs = ReadPositiveIntEnvironment("UNBLOCK_VALIDATE_TIMEOUT_MS", 5000);
            _firstValidGraceMs = ReadPositiveIntEnvironment("UNBLOCK_FIRST_VALID_GRACE_MS", 2500);
            _trialOnlyGraceMs = ReadPositiveIntEnvironment("UNBLOCK_TRIAL_ONLY_GRACE_MS", 7000);
            _bilibiliTrialGraceMs = ReadPositiveIntEnvironment("BILIBILI_TRIAL_GRACE_MS", Math.Max(_trialOnlyGraceMs, _bilibiliProviderTimeoutMs));
        }

        private static int ReadPositiveIntEnvironment(string name, int fallback)
        {
            return int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0
                ? value
                : fallback;
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
            var info = await _find.GetAsync(id, rawData, ct).ConfigureAwait(false);
            var candidates = BuildCandidate().ToList();
            if (!candidates.Any())
                throw new InvalidOperationException("no provider candidates");

            if (_options.FollowSourceOrder)
            {
                return await MatchBySourceOrderAsync(info, candidates, ct).ConfigureAwait(false);
            }

            if (_options.SelectMaxBr)
            {
                return await MatchAfterCollectingAsync(info, candidates, ct).ConfigureAwait(false);
            }

            return await MatchFastAsync(info, candidates, ct).ConfigureAwait(false);
        }

        private async Task<AudioResult> MatchBySourceOrderAsync(SongInfo info, IReadOnlyList<string> candidates, CancellationToken ct)
        {
            var fallback = new List<AudioResult>();
            var trialFallback = new List<AudioResult>();

            foreach (var source in candidates)
            {
                var attempt = await RunProviderAsync(source, info, ct).ConfigureAwait(false);
                if (!attempt.Ok || attempt.Result == null)
                {
                    if (attempt.Exception != null)
                        Log.Debug(_scope, $"{attempt.Name} failed: {attempt.Exception.Message}");
                    continue;
                }

                fallback.Add(attempt.Result);
                var validation = await ValidateWithTimeoutAsync(attempt.Result, info, ct).ConfigureAwait(false);
                if (validation.IsValid)
                {
                    if (!attempt.Result.IsTrial)
                    {
                        return attempt.Result;
                    }

                    trialFallback.Add(attempt.Result);
                    continue;
                }

                Log.Debug(_scope, $"{attempt.Result.Source} url validation failed: {validation.Reason}");
            }

            if (trialFallback.Count > 0)
            {
                return ChooseBest(trialFallback, info, candidates);
            }

            if (_options.AllowUnvalidatedFallback && fallback.Count > 0)
            {
                return fallback.First();
            }

            throw new InvalidOperationException("all provider urls failed validation");
        }

        private async Task<AudioResult> MatchAfterCollectingAsync(SongInfo info, IReadOnlyList<string> candidates, CancellationToken ct)
        {
            var tasks = candidates.Select(name => RunProviderAsync(name, info, ct)).ToArray();
            var attempts = await Task.WhenAll(tasks).ConfigureAwait(false);

            foreach (var fail in attempts.Where(r => !r.Ok && r.Exception != null))
                Log.Debug(_scope, $"{fail.Name} failed: {fail.Exception.Message}");

            var success = attempts.Where(r => r.Ok && r.Result != null).Select(r => r.Result).ToList();
            if (!success.Any()) throw new InvalidOperationException("all providers failed");

            var ordered = success
                .OrderBy(r => Score(info, r))
                .ThenByDescending(r => NormalizeBitRate(r.BitRate).GetValueOrDefault())
                .ThenBy(r => SourceIndex(candidates, r.Source))
                .ToList();

            return await PickValidAsync(ordered, info, candidates, ct).ConfigureAwait(false);
        }

        private async Task<AudioResult> MatchFastAsync(SongInfo info, IReadOnlyList<string> candidates, CancellationToken ct)
        {
            using var matchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var pending = candidates
                .Select(name => new PendingProviderAttempt
                {
                    Name = name,
                    Task = RunProviderAsync(name, info, matchCts.Token)
                })
                .ToList();
            var fallback = new List<AudioResult>();
            var valid = new List<AudioResult>();
            DateTimeOffset? firstFullValidAt = null;
            DateTimeOffset? firstTrialValidAt = null;

            try
            {
                while (pending.Count > 0)
                {
                    Task<ProviderAttempt> finished;

                    if (valid.Count > 0)
                    {
                        bool hasFullValid = valid.Any(r => !r.IsTrial);
                        var startAt = hasFullValid ? firstFullValidAt : firstTrialValidAt;
                        int graceMs = hasFullValid ? _firstValidGraceMs : GetTrialOnlyGraceMs(pending);

                        if (graceMs <= 0 || !startAt.HasValue)
                        {
                            break;
                        }

                        var elapsed = DateTimeOffset.UtcNow - startAt.Value;
                        var remaining = TimeSpan.FromMilliseconds(graceMs) - elapsed;
                        if (remaining <= TimeSpan.Zero)
                        {
                            break;
                        }

                        var anyTask = Task.WhenAny(pending.Select(p => p.Task));
                        var delayTask = Task.Delay(remaining, ct);
                        var completed = await Task.WhenAny(anyTask, delayTask).ConfigureAwait(false);
                        ct.ThrowIfCancellationRequested();
                        if (completed == delayTask)
                        {
                            break;
                        }

                        finished = await anyTask.ConfigureAwait(false);
                    }
                    else
                    {
                        finished = await Task.WhenAny(pending.Select(p => p.Task)).ConfigureAwait(false);
                    }

                    var pendingAttempt = pending.FirstOrDefault(p => ReferenceEquals(p.Task, finished));
                    if (pendingAttempt != null)
                    {
                        pending.Remove(pendingAttempt);
                    }
                    var attempt = await finished.ConfigureAwait(false);
                    if (!attempt.Ok || attempt.Result == null)
                    {
                        if (attempt.Exception != null)
                            Log.Debug(_scope, $"{attempt.Name} failed: {attempt.Exception.Message}");
                        continue;
                    }

                    fallback.Add(attempt.Result);
                    var validation = await ValidateWithTimeoutAsync(attempt.Result, info, ct).ConfigureAwait(false);
                    if (validation.IsValid)
                    {
                        if (attempt.Result.IsTrial)
                        {
                            if (firstTrialValidAt == null)
                            {
                                firstTrialValidAt = DateTimeOffset.UtcNow;
                            }
                        }
                        else if (firstFullValidAt == null)
                        {
                            firstFullValidAt = DateTimeOffset.UtcNow;
                        }

                        valid.Add(attempt.Result);
                    }
                    else
                    {
                        Log.Debug(_scope, $"{attempt.Result.Source} url validation failed: {validation.Reason}");
                    }
                }
            }
            finally
            {
                matchCts.Cancel();
            }

            if (valid.Count > 0)
            {
                return ChooseBest(valid, info, candidates);
            }

            if (_options.AllowUnvalidatedFallback && fallback.Count > 0)
            {
                return ChooseBest(fallback, info, candidates);
            }

            throw new InvalidOperationException("all provider urls failed validation");
        }

        private async Task<ProviderAttempt> RunProviderAsync(string name, SongInfo info, CancellationToken ct)
        {
            try
            {
                using var providerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                providerCts.CancelAfter(GetProviderTimeoutMs(name));
                var res = await _providers[name].CheckAsync(info, providerCts.Token).ConfigureAwait(false);
                if (res != null)
                {
                    res.Source = name;
                }

                return new ProviderAttempt { Ok = res != null, Result = res, Name = name };
            }
            catch (Exception ex)
            {
                return new ProviderAttempt { Ok = false, Result = null, Name = name, Exception = ex };
            }
        }

        private int GetProviderTimeoutMs(string name)
        {
            return IsBilibiliSource(name) ? _bilibiliProviderTimeoutMs : _providerTimeoutMs;
        }

        private int GetTrialOnlyGraceMs(IReadOnlyList<PendingProviderAttempt> pending)
        {
            return pending != null && pending.Any(p => IsBilibiliSource(p.Name))
                ? Math.Max(_trialOnlyGraceMs, _bilibiliTrialGraceMs)
                : _trialOnlyGraceMs;
        }

        private static bool IsBilibiliSource(string name)
        {
            return string.Equals(name, "bilibili", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "bilivideo", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<AudioResult> PickValidAsync(IReadOnlyList<AudioResult> ordered, SongInfo info, IReadOnlyList<string> candidates, CancellationToken ct)
        {
            var trialFallback = new List<AudioResult>();

            foreach (var r in ordered)
            {
                var validation = await ValidateWithTimeoutAsync(r, info, ct).ConfigureAwait(false);
                if (validation.IsValid)
                {
                    if (!r.IsTrial)
                    {
                        return r;
                    }

                    trialFallback.Add(r);
                    continue;
                }

                Log.Debug(_scope, $"{r.Source} url validation failed: {validation.Reason}");
            }

            if (trialFallback.Count > 0)
            {
                return ChooseBest(trialFallback, info, candidates);
            }

            if (_options.AllowUnvalidatedFallback)
            {
                return ChooseBest(ordered, info, candidates);
            }

            throw new InvalidOperationException("all provider urls failed validation");
        }

        private async Task<AudioValidationResult> ValidateWithTimeoutAsync(AudioResult result, SongInfo info, CancellationToken ct)
        {
            try
            {
                using var validationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                validationCts.CancelAfter(_validationTimeoutMs);
                return await ValidateAsync(result, info, validationCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return AudioValidationResult.Invalid("validation timeout");
            }
            catch (Exception ex)
            {
                return AudioValidationResult.Invalid(ex.Message);
            }
        }

        private async Task<AudioValidationResult> ValidateAsync(AudioResult result, SongInfo info, CancellationToken ct)
        {
            if (result == null || string.IsNullOrWhiteSpace(result.Url))
            {
                return AudioValidationResult.Invalid("empty url");
            }

            if (!Uri.TryCreate(result.Url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return AudioValidationResult.Invalid("non-http url");
            }

            long targetDurationMs = info?.Duration ?? 0;
            bool metadataLooksTrial = false;
            long? metadataTrialDurationMs = null;
            if (targetDurationMs > 0 && result.DurationMs.HasValue && result.DurationMs.Value > 0)
            {
                long diff = Math.Abs(result.DurationMs.Value - targetDurationMs);
                long tolerance = GetDurationToleranceMs(targetDurationMs);
                if (diff > tolerance)
                {
                    if (result.DurationMs.Value < targetDurationMs)
                    {
                        metadataLooksTrial = true;
                        metadataTrialDurationMs = result.DurationMs.Value;
                    }
                    else
                    {
                        return AudioValidationResult.Invalid($"metadata duration mismatch diff={diff}ms tolerance={tolerance}ms");
                    }
                }
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, result.Url);
            req.Headers.Range = new RangeHeaderValue(0, ProbeBytes - 1);
            req.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
            ApplyAudioRequestHeaders(req, result.Headers);

            using var resp = await ValidateHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return AudioValidationResult.Invalid($"http {(int)resp.StatusCode}");
            }

            string mediaType = resp.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            byte[] probe = new byte[ProbeBytes];
            int read = await ReadProbeAsync(resp, probe, ct).ConfigureAwait(false);
            if (read <= 0)
            {
                return AudioValidationResult.Invalid("empty response body");
            }

            if (!LooksLikeAudioPayload(probe, read, mediaType))
            {
                return AudioValidationResult.Invalid($"non-audio payload content-type={mediaType ?? "none"}");
            }

            long totalLength = GetTotalLength(resp);
            if (totalLength <= 0 && result.Size > 0)
            {
                totalLength = result.Size;
            }

            long? actualDurationMs = TryReadContainerDurationMs(probe, read);
            if (targetDurationMs > 0 && actualDurationMs.HasValue && actualDurationMs.Value > 0)
            {
                long diff = Math.Abs(actualDurationMs.Value - targetDurationMs);
                long tolerance = GetDurationToleranceMs(targetDurationMs);
                if (diff > tolerance)
                {
                    if (actualDurationMs.Value < targetDurationMs)
                    {
                        MarkTrial(result, totalLength, bitRate: null, actualTrialDurationMs: actualDurationMs.Value, targetDurationMs: targetDurationMs);
                        return AudioValidationResult.Valid();
                    }

                    return AudioValidationResult.Invalid($"container duration mismatch diff={diff}ms tolerance={tolerance}ms");
                }

                metadataLooksTrial = false;
                metadataTrialDurationMs = null;
            }

            int? bitRate = NormalizeBitRate(result.BitRate)
                ?? TryDecodeBitRate(probe, read)
                ?? InferBitRateFromUrl(result.Url);

            if (!SizeLooksFullLength(totalLength, targetDurationMs, bitRate, out var sizeReason))
            {
                MarkTrial(result, totalLength, bitRate, actualDurationMs ?? metadataTrialDurationMs, targetDurationMs);
                Log.Debug(_scope, $"{result.Source} accepted as trial audio: {sizeReason}");
                return AudioValidationResult.Valid();
            }

            if (metadataLooksTrial)
            {
                MarkTrial(result, totalLength, bitRate, metadataTrialDurationMs, targetDurationMs);
                Log.Debug(_scope, $"{result.Source} accepted as trial audio: metadata duration shorter than target");
                return AudioValidationResult.Valid();
            }

            if (totalLength > 0)
            {
                result.Size = totalLength;
            }

            if (bitRate.HasValue && bitRate.Value > 0)
            {
                result.BitRate = bitRate.Value;
            }

            if (actualDurationMs.HasValue && actualDurationMs.Value > 0)
            {
                result.DurationMs = actualDurationMs.Value;
            }

            return AudioValidationResult.Valid();
        }

        private static void MarkTrial(AudioResult result, long totalLength, int? bitRate, long? actualTrialDurationMs, long targetDurationMs)
        {
            result.IsTrial = true;

            if (totalLength > 0)
            {
                result.Size = totalLength;
            }

            int? normalizedBitRate = NormalizeBitRate(bitRate);
            if (normalizedBitRate.HasValue && normalizedBitRate.Value > 0)
            {
                result.BitRate = normalizedBitRate.Value;
            }

            long trialDurationMs = actualTrialDurationMs.GetValueOrDefault();
            if (trialDurationMs <= 0 && totalLength > 0 && normalizedBitRate.HasValue && normalizedBitRate.Value > 0)
            {
                trialDurationMs = (long)Math.Round(totalLength * 8d * 1000d / normalizedBitRate.Value);
            }

            if (trialDurationMs <= 0)
            {
                trialDurationMs = 30000;
            }

            if (targetDurationMs > 0)
            {
                trialDurationMs = Math.Min(trialDurationMs, targetDurationMs);
            }

            if (trialDurationMs <= 0)
            {
                trialDurationMs = 30000;
            }

            result.TrialStartMs = 0;
            result.TrialEndMs = trialDurationMs;
        }

        private static void ApplyAudioRequestHeaders(HttpRequestMessage req, IDictionary<string, string> headers)
        {
            bool hasUserAgent = false;

            if (headers != null)
            {
                foreach (var header in headers)
                {
                    if (string.IsNullOrWhiteSpace(header.Key) || string.IsNullOrWhiteSpace(header.Value))
                    {
                        continue;
                    }

                    if (string.Equals(header.Key, "Range", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(header.Key, "Accept-Encoding", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (string.Equals(header.Key, "User-Agent", StringComparison.OrdinalIgnoreCase))
                    {
                        req.Headers.UserAgent.TryParseAdd(header.Value);
                        hasUserAgent = true;
                        continue;
                    }

                    req.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            if (!hasUserAgent)
            {
                req.Headers.UserAgent.TryParseAdd("Mozilla/5.0");
            }
        }

        private static async Task<int> ReadProbeAsync(HttpResponseMessage response, byte[] buffer, CancellationToken ct)
        {
            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            int total = 0;
            while (total < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer, total, buffer.Length - total, ct).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                total += read;
            }

            return total;
        }

        private static long GetTotalLength(HttpResponseMessage response)
        {
            var range = response.Content.Headers.ContentRange;
            if (range != null && range.Length.HasValue)
            {
                return range.Length.Value;
            }

            if (response.StatusCode != HttpStatusCode.PartialContent &&
                response.Content.Headers.ContentLength.HasValue)
            {
                return response.Content.Headers.ContentLength.Value;
            }

            return 0;
        }

        private static bool LooksLikeAudioPayload(byte[] data, int length, string mediaType)
        {
            int first = FirstNonWhitespaceOffset(data, length);
            if (first >= 0)
            {
                byte marker = data[first];
                if (marker == (byte)'{' || marker == (byte)'[' || marker == (byte)'<')
                {
                    return false;
                }
            }

            if (!string.IsNullOrWhiteSpace(mediaType) &&
                (mediaType.Contains("json") ||
                 mediaType.Contains("text") ||
                 mediaType.Contains("html") ||
                 mediaType.Contains("xml")))
            {
                return false;
            }

            if (HasAscii(data, length, 0, "ID3") ||
                HasAscii(data, length, 0, "fLaC") ||
                HasAscii(data, length, 0, "OggS") ||
                HasAscii(data, length, 0, "RIFF") ||
                HasAscii(data, length, 4, "ftyp") ||
                FindMp3HeaderOffset(data, length) >= 0)
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(mediaType) &&
                   (mediaType.Contains("audio") ||
                    mediaType.Contains("video") ||
                    mediaType.Contains("octet"));
        }

        private static int FirstNonWhitespaceOffset(byte[] data, int length)
        {
            for (int i = 0; i < length; i++)
            {
                byte c = data[i];
                if (c != (byte)' ' && c != (byte)'\t' && c != (byte)'\r' && c != (byte)'\n')
                {
                    return i;
                }
            }

            return -1;
        }

        private static bool HasAscii(byte[] data, int length, int offset, string value)
        {
            if (data == null || value == null || offset < 0 || offset + value.Length > length)
            {
                return false;
            }

            for (int i = 0; i < value.Length; i++)
            {
                if (data[offset + i] != (byte)value[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static int? TryDecodeBitRate(byte[] data, int length)
        {
            if (HasAscii(data, length, 0, "fLaC"))
            {
                return 999000;
            }

            int offset = FindMp3HeaderOffset(data, length);
            if (offset < 0 || offset + 4 > length)
            {
                return null;
            }

            int version = (data[offset + 1] >> 3) & 0x3;
            int layer = (data[offset + 1] >> 1) & 0x3;
            int index = data[offset + 2] >> 4;
            int kbps = LookupMp3BitRateKbps(version, layer, index);
            return kbps > 0 ? kbps * 1000 : (int?)null;
        }

        private static int FindMp3HeaderOffset(byte[] data, int length)
        {
            int start = 0;
            if (HasAscii(data, length, 0, "ID3") && length >= 10)
            {
                int tagSize =
                    ((data[6] & 0x7f) << 21) |
                    ((data[7] & 0x7f) << 14) |
                    ((data[8] & 0x7f) << 7) |
                    (data[9] & 0x7f);
                start = Math.Min(10 + tagSize, Math.Max(0, length - 4));
            }

            if (IsMp3FrameHeaderAt(data, length, start))
            {
                return start;
            }

            int max = Math.Min(length - 4, 4096);
            for (int i = 0; i <= max; i++)
            {
                if (IsMp3FrameHeaderAt(data, length, i))
                {
                    return i;
                }
            }

            return -1;
        }

        private static bool IsMp3FrameHeaderAt(byte[] data, int length, int offset)
        {
            if (offset < 0 || offset + 4 > length)
            {
                return false;
            }

            return data[offset] == 0xff &&
                   ((data[offset + 1] >> 5) & 0x7) == 0x7 &&
                   ((data[offset + 1] >> 1) & 0x3) != 0 &&
                   ((data[offset + 2] >> 4) & 0xf) != 0xf &&
                   ((data[offset + 2] >> 2) & 0x3) != 0x3;
        }

        private static int LookupMp3BitRateKbps(int version, int layer, int index)
        {
            if (index <= 0 || index >= 15)
            {
                return 0;
            }

            if (version == 3)
            {
                if (layer == 3)
                    return new[] { 0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448 }[index];
                if (layer == 2)
                    return new[] { 0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384 }[index];
                if (layer == 1)
                    return new[] { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 }[index];
            }
            else
            {
                if (layer == 3)
                    return new[] { 0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256 }[index];
                if (layer == 2 || layer == 1)
                    return new[] { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 }[index];
            }

            return 0;
        }

        private static long? TryReadContainerDurationMs(byte[] data, int length)
        {
            return TryReadFlacDurationMs(data, length);
        }

        private static long? TryReadFlacDurationMs(byte[] data, int length)
        {
            if (!HasAscii(data, length, 0, "fLaC"))
            {
                return null;
            }

            int offset = 4;
            while (offset + 4 <= length)
            {
                int blockType = data[offset] & 0x7f;
                int blockLength = (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
                offset += 4;

                if (blockType == 0 && blockLength >= 34 && offset + 34 <= length)
                {
                    ulong value = 0;
                    for (int i = 0; i < 8; i++)
                    {
                        value = (value << 8) | data[offset + 10 + i];
                    }

                    int sampleRate = (int)((value >> 44) & 0xfffff);
                    ulong totalSamples = value & 0xfffffffffUL;
                    if (sampleRate <= 0 || totalSamples == 0)
                    {
                        return null;
                    }

                    return (long)Math.Round(totalSamples * 1000d / sampleRate);
                }

                if ((data[offset - 4] & 0x80) != 0)
                {
                    break;
                }

                offset += blockLength;
            }

            return null;
        }

        private static int? NormalizeBitRate(int? bitRate)
        {
            if (!bitRate.HasValue || bitRate.Value <= 0)
            {
                return null;
            }

            int value = bitRate.Value;
            if (value < 1000)
            {
                value *= 1000;
            }

            return value;
        }

        private static int? InferBitRateFromUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            string lower = url.ToLowerInvariant();
            if (lower.Contains(".flac") || lower.Contains("flac"))
            {
                return 999000;
            }

            var match = Regex.Match(lower, @"(?:^|[^0-9])(?<br>96|128|192|256|320)k(?:[^0-9]|$)");
            if (match.Success && int.TryParse(match.Groups["br"].Value, out var kbps))
            {
                return kbps * 1000;
            }

            if (lower.Contains("m800") || lower.Contains("320"))
            {
                return 320000;
            }

            if (lower.Contains("m500") || lower.Contains("128"))
            {
                return 128000;
            }

            if (lower.Contains("c400") || lower.Contains("c200") || lower.Contains(".m4a"))
            {
                return 96000;
            }

            return null;
        }

        private static bool SizeLooksFullLength(long totalLength, long targetDurationMs, int? bitRate, out string reason)
        {
            if (totalLength <= 0)
            {
                reason = "missing total content length";
                return false;
            }

            if (totalLength < AbsoluteMinimumAudioBytes)
            {
                reason = $"too small length={totalLength}";
                return false;
            }

            if (targetDurationMs <= 0)
            {
                reason = string.Empty;
                return true;
            }

            double seconds = targetDurationMs / 1000d;
            double absoluteMinimum = seconds * MinAverageBitRate / 8d;
            if (totalLength < absoluteMinimum)
            {
                reason = $"below minimum average bitrate length={totalLength} min={absoluteMinimum:F0}";
                return false;
            }

            int? normalizedBitRate = NormalizeBitRate(bitRate);
            if (normalizedBitRate.HasValue && normalizedBitRate.Value > 0)
            {
                double expected = seconds * normalizedBitRate.Value / 8d;
                double required = expected * GetBitRateSizeRatio(normalizedBitRate.Value);
                if (totalLength < required)
                {
                    reason = $"below bitrate duration estimate length={totalLength} required={required:F0} bitrate={normalizedBitRate.Value}";
                    return false;
                }
            }

            reason = string.Empty;
            return true;
        }

        private static double GetBitRateSizeRatio(int bitRate)
        {
            if (bitRate >= 900000)
            {
                return 0.30d;
            }

            if (bitRate >= 96000)
            {
                return 0.55d;
            }

            return 0.45d;
        }

        private AudioResult ChooseBest(IReadOnlyList<AudioResult> results, SongInfo info, IReadOnlyList<string> candidates)
        {
            if (_options.SelectMaxBr)
            {
                return results
                    .OrderBy(r => r.IsTrial ? 1 : 0)
                    .ThenBy(r => Score(info, r))
                    .ThenByDescending(r => NormalizeBitRate(r.BitRate).GetValueOrDefault())
                    .ThenBy(r => SourceIndex(candidates, r.Source))
                    .First();
            }

            return results
                .OrderBy(r => r.IsTrial ? 1 : 0)
                .ThenBy(r => Score(info, r))
                .ThenByDescending(r => NormalizeBitRate(r.BitRate).GetValueOrDefault())
                .ThenBy(r => SourceIndex(candidates, r.Source))
                .First();
        }

        private static int SourceIndex(IReadOnlyList<string> candidates, string source)
        {
            if (candidates == null || string.IsNullOrWhiteSpace(source))
            {
                return int.MaxValue;
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                if (string.Equals(candidates[i], source, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return int.MaxValue;
        }

        private static double Score(SongInfo info, AudioResult r)
        {
            long targetDur = info?.Duration ?? 0;
            var dur = (r.DurationMs.HasValue && r.DurationMs.Value > 0) ? r.DurationMs.Value : targetDur;
            var durDiff = targetDur > 0 ? Math.Abs(dur - targetDur) : 0;
            double durationScore = 0;
            if (targetDur > 0)
            {
                if (durDiff <= PreferredDurationToleranceMs)
                {
                    durationScore = -100000d + durDiff * 0.1d;
                }
                else
                {
                    long tolerance = GetDurationToleranceMs(targetDur);
                    durationScore = durDiff <= tolerance
                        ? 60000d + (durDiff - PreferredDurationToleranceMs) * 1.5d
                        : 140000d + (durDiff - tolerance) * 3d;
                }
            }

            var titleScore = Similarity(info?.Name, r.Title) * 0.6 +
                             Similarity(info?.Artists != null ? string.Join(" / ", info.Artists.Select(a => a.Name)) : string.Empty, r.Artists) * 0.4;

            // Smaller score is better. Trial resources are sorted after full resources before this score is evaluated.
            return durationScore - titleScore * 1000;
        }

        private static long GetDurationToleranceMs(long durationMs)
        {
            if (durationMs <= 0)
            {
                return 5000;
            }

            return Math.Max(3000, Math.Min(8000, durationMs / 50));
        }

        private static double Similarity(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return 0;
            var tokensA = a.ToLowerInvariant().Split(new[] { ' ', '/', '-', '_', '·' }, StringSplitOptions.RemoveEmptyEntries).Distinct().ToHashSet();
            var tokensB = b.ToLowerInvariant().Split(new[] { ' ', '/', '-', '_', '·' }, StringSplitOptions.RemoveEmptyEntries).Distinct().ToHashSet();
            var inter = tokensA.Intersect(tokensB).Count();
            var union = tokensA.Union(tokensB).Count();
            return union == 0 ? 0 : (double)inter / union;
        }
    }
}
