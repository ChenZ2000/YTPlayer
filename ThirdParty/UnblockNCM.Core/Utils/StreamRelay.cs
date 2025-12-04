using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UnblockNCM.Core.Logging;
using UnblockNCM.Core.Models;

namespace UnblockNCM.Core.Utils
{
    /// <summary>
    /// Downloads upstream audio with provider-required headers and stores to a temp file for local playback.
    /// Intended to decouple caller from anti-leech requirements.
    /// </summary>
    public class StreamRelay : IDisposable
    {
        private readonly HttpClient _http;
        private const string Scope = "relay";

        public StreamRelay()
        {
            _http = new HttpClient(new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            });
        }

        public async Task<string> DownloadToFileAsync(AudioResult result, CancellationToken ct)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            var ext = Path.GetExtension(new Uri(result.Url).AbsolutePath);
            if (string.IsNullOrWhiteSpace(ext)) ext = ".bin";
            var path = Path.Combine(Path.GetTempPath(), $"unblock_{Guid.NewGuid():N}{ext}");

            using var req = new HttpRequestMessage(HttpMethod.Get, result.Url);
            if (result.Headers != null)
            {
                foreach (var kv in result.Headers)
                {
                    // some headers like "User-Agent" must go to specific collection
                    if (kv.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
                        req.Headers.UserAgent.ParseAdd(kv.Value);
                    else if (kv.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase))
                        req.Headers.Referrer = new Uri(kv.Value);
                    else
                        req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                }
            }
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            using (var fs = File.Create(path))
            {
                await resp.Content.CopyToAsync(fs).ConfigureAwait(false);
            }
            Log.Info(Scope, $"saved {result.Source} -> {path}");
            return path;
        }

        public void Dispose()
        {
            _http?.Dispose();
        }
    }
}
