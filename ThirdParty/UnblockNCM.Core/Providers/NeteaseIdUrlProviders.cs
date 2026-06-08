using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnblockNCM.Core.Models;
using UnblockNCM.Core.Utils;

namespace UnblockNCM.Core.Providers
{
    public abstract class NeteaseIdUrlProviderBase : IProvider
    {
        private const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";

        private static readonly HttpClient NoRedirectHttp = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        });

        private readonly CacheStorage _cache;
        private readonly bool _noCache;

        protected NeteaseIdUrlProviderBase(CacheStorage cache, bool noCache)
        {
            _cache = cache;
            _noCache = noCache;
        }

        protected abstract string Name { get; }

        protected abstract Task<string> ResolveUrlAsync(string id, CancellationToken ct);

        public async Task<AudioResult> CheckAsync(SongInfo info, CancellationToken ct)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.Id))
            {
                throw new InvalidOperationException($"{Name} empty id");
            }

            string url = await _cache.GetOrAddAsync(
                $"{Name}:{info.Id}",
                () => ResolveUrlAsync(info.Id, ct),
                TimeSpan.FromMinutes(5),
                _noCache).ConfigureAwait(false);

            if (!IsHttpUrl(url))
            {
                throw new InvalidOperationException($"{Name} no url");
            }

            return new AudioResult
            {
                Url = url,
                Source = Name,
                Size = 0,
                DurationMs = info.Duration,
                Title = info.Name,
                Artists = info.Artists != null
                    ? string.Join(" / ", info.Artists.Select(a => a.Name))
                    : info.Keyword,
                Headers = new Dictionary<string, string>
                {
                    { "User-Agent", BrowserUserAgent },
                    { "Referer", "https://music.163.com/" }
                }
            };
        }

        protected static async Task<string> GetUrlFromEndpointAsync(string requestUrl, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
            request.Headers.TryAddWithoutValidation("Referer", "https://music.163.com/");

            using var response = await NoRedirectHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400)
            {
                string redirected = ResolveRedirectUrl(requestUrl, response.Headers.Location);
                if (IsHttpUrl(redirected))
                {
                    return redirected;
                }
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"endpoint returned {(int)response.StatusCode}");
            }

            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            string url = ExtractUrl(body);
            if (!IsHttpUrl(url))
            {
                throw new InvalidOperationException("endpoint response has no url");
            }

            return url;
        }

        private static string ResolveRedirectUrl(string requestUrl, Uri location)
        {
            if (location == null)
            {
                return null;
            }

            if (location.IsAbsoluteUri)
            {
                return location.ToString();
            }

            return new Uri(new Uri(requestUrl), location).ToString();
        }

        protected static string ExtractUrl(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            string trimmed = body.Trim().Trim('"');
            if (IsHttpUrl(trimmed))
            {
                return trimmed.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            }

            try
            {
                JToken token = JToken.Parse(body);
                string direct = ExtractUrl(token);
                if (IsHttpUrl(direct))
                {
                    return direct;
                }
            }
            catch
            {
                // Some endpoints return plain text on success and HTML on failure.
            }

            return null;
        }

        private static string ExtractUrl(JToken token)
        {
            if (token == null)
            {
                return null;
            }

            if (token.Type == JTokenType.String)
            {
                string value = token.Value<string>()?.Trim();
                return IsHttpUrl(value) ? value : null;
            }

            foreach (JProperty property in SelfAndDescendants(token).OfType<JProperty>())
            {
                if (!string.Equals(property.Name, "url", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string value = property.Value.Type == JTokenType.String
                    ? property.Value.Value<string>()?.Trim()
                    : property.Value.ToString().Trim();
                if (IsHttpUrl(value))
                {
                    return value;
                }
            }

            foreach (JToken child in SelfAndDescendants(token))
            {
                if (child.Type != JTokenType.String)
                {
                    continue;
                }

                string value = child.Value<string>()?.Trim();
                if (IsHttpUrl(value))
                {
                    return value;
                }
            }

            return null;
        }

        private static IEnumerable<JToken> SelfAndDescendants(JToken token)
        {
            if (token == null)
            {
                yield break;
            }

            yield return token;
            if (token is not JContainer container)
            {
                yield break;
            }

            foreach (JToken child in container.Children())
            {
                foreach (JToken nested in SelfAndDescendants(child))
                {
                    yield return nested;
                }
            }
        }

        protected static bool IsHttpUrl(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    value.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        }
    }

    public sealed class ByfunsProvider : NeteaseIdUrlProviderBase
    {
        public ByfunsProvider(CacheStorage cache, bool noCache)
            : base(cache, noCache)
        {
        }

        protected override string Name => "byfuns";

        protected override Task<string> ResolveUrlAsync(string id, CancellationToken ct)
        {
            return GetUrlFromEndpointAsync($"https://api.byfuns.top/1/?id={Uri.EscapeDataString(id)}&level=lossless", ct);
        }
    }

    public sealed class GdMusicProvider : NeteaseIdUrlProviderBase
    {
        public GdMusicProvider(CacheStorage cache, bool noCache)
            : base(cache, noCache)
        {
        }

        protected override string Name => "gdmusic";

        protected override Task<string> ResolveUrlAsync(string id, CancellationToken ct)
        {
            return GetUrlFromEndpointAsync($"https://music-api.gdstudio.xyz/api.php?types=url&source=netease&id={Uri.EscapeDataString(id)}&br=999", ct);
        }
    }

    public sealed class BakaProvider : NeteaseIdUrlProviderBase
    {
        public BakaProvider(CacheStorage cache, bool noCache)
            : base(cache, noCache)
        {
        }

        protected override string Name => "baka";

        protected override Task<string> ResolveUrlAsync(string id, CancellationToken ct)
        {
            return GetUrlFromEndpointAsync($"https://api.baka.plus/meting/?type=url&id={Uri.EscapeDataString(id)}&br=999", ct);
        }
    }

    public sealed class BikonooProvider : NeteaseIdUrlProviderBase
    {
        public BikonooProvider(CacheStorage cache, bool noCache)
            : base(cache, noCache)
        {
        }

        protected override string Name => "bikonoo";

        protected override Task<string> ResolveUrlAsync(string id, CancellationToken ct)
        {
            return GetUrlFromEndpointAsync($"https://ncm.bikonoo.com/api/163_music.php?ids={Uri.EscapeDataString(id)}&level=lossless&type=json", ct);
        }
    }

    public sealed class MslsProvider : NeteaseIdUrlProviderBase
    {
        public MslsProvider(CacheStorage cache, bool noCache)
            : base(cache, noCache)
        {
        }

        protected override string Name => "msls";

        protected override Task<string> ResolveUrlAsync(string id, CancellationToken ct)
        {
            return GetUrlFromEndpointAsync($"https://api.msls1441.com/?type=url&id={Uri.EscapeDataString(id)}", ct);
        }
    }

    public sealed class QijieyaProvider : NeteaseIdUrlProviderBase
    {
        public QijieyaProvider(CacheStorage cache, bool noCache)
            : base(cache, noCache)
        {
        }

        protected override string Name => "qijieya";

        protected override Task<string> ResolveUrlAsync(string id, CancellationToken ct)
        {
            return GetUrlFromEndpointAsync($"https://api.qijieya.cn/meting/?type=url&id={Uri.EscapeDataString(id)}", ct);
        }
    }
}
