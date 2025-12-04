using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnblockNCM.Core.Logging;

namespace UnblockNCM.Core.Net
{
    public class HttpHelper : IDisposable
    {
        private readonly HttpClient _client;
        private readonly HttpClientHandler _handler;
        private readonly string _scope = "http";

        public HttpHelper(IWebProxy proxy = null, TimeSpan? timeout = null)
        {
            _handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                Proxy = proxy,
                UseProxy = proxy != null,
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
            _client = new HttpClient(_handler);
            if (timeout.HasValue) _client.Timeout = timeout.Value;
            _client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) UnblockNCM.NET");
            _client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
            _client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9");
        }

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
        {
            Log.Debug(_scope, $"{request.Method} {request.RequestUri}");
            var resp = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            return resp;
        }

        public Task<HttpResponseMessage> GetAsync(string url, IDictionary<string, string> headers = null, CancellationToken ct = default)
            => SendWithBodyAsync(HttpMethod.Get, url, headers, null, ct);

        public Task<HttpResponseMessage> PostAsync(string url, IDictionary<string, string> headers, string body, CancellationToken ct = default)
            => SendWithBodyAsync(HttpMethod.Post, url, headers, body, ct);

        public async Task<HttpResponseMessage> SendWithBodyAsync(HttpMethod method, string url, IDictionary<string, string> headers, string body, CancellationToken ct = default)
        {
            var req = new HttpRequestMessage(method, url);
            if (headers != null)
            {
                foreach (var kv in headers)
                {
                    if (string.Equals(kv.Key, "content-type", StringComparison.OrdinalIgnoreCase))
                        continue;
                    req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                }
            }

            if (body != null)
            {
                req.Content = new StringContent(body, Encoding.UTF8, headers != null && headers.TryGetValue("content-type", out var ctHeader) ? ctHeader : "application/x-www-form-urlencoded");
            }

            return await SendAsync(req, ct).ConfigureAwait(false);
        }

        public static async Task<byte[]> ReadBytesAsync(HttpResponseMessage resp)
        {
            return await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        }

        public static async Task<string> ReadStringAsync(HttpResponseMessage resp)
        {
            return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        }

        public void Dispose()
        {
            _client?.Dispose();
            _handler?.Dispose();
        }
    }
}
