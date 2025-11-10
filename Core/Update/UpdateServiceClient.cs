using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace YTPlayer.Update
{
    public sealed class UpdateServiceClient : IDisposable
    {
        private static readonly string ProgressHeaderName = "X-YT-Update-Progress";
        private readonly HttpClient _httpClient;
        private readonly string _endpoint;

        static UpdateServiceClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        public UpdateServiceClient(string endpoint, string productName, string productVersion)
        {
            _endpoint = string.IsNullOrWhiteSpace(endpoint) ? UpdateConstants.DefaultEndpoint : endpoint;

            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };

            _httpClient = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };

            _httpClient.DefaultRequestHeaders.UserAgent.Clear();
            string name = string.IsNullOrWhiteSpace(productName) ? "YTPlayer" : productName;
            string version = string.IsNullOrWhiteSpace(productVersion) ? "0.0.0" : productVersion;
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(name, version));
            _httpClient.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
            _httpClient.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("deflate"));
        }

        public async Task<UpdateCheckResult> CheckForUpdatesAsync(string currentVersion, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(currentVersion))
            {
                throw new ArgumentNullException(nameof(currentVersion));
            }

            string requestUri = $"{_endpoint}?action=check&version={Uri.EscapeDataString(currentVersion)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            IReadOnlyList<UpdateProgressStage> headerProgress = ParseProgressHeader(response);

            string payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (!response.IsSuccessStatusCode)
            {
                throw new UpdateServiceException($"检查更新失败 ({(int)response.StatusCode})", response.StatusCode, payload);
            }

            UpdateCheckResponse? parsed = null;
            try
            {
                parsed = JsonConvert.DeserializeObject<UpdateCheckResponse>(payload);
            }
            catch (JsonException ex)
            {
                throw new UpdateServiceException($"解析更新响应失败: {ex.Message}", response.StatusCode, payload);
            }

            if (parsed == null)
            {
                throw new UpdateServiceException("更新服务器返回了空响应");
            }

            if (!string.Equals(parsed.Status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                string errorMessage = parsed.Error?.Message ?? parsed.Message ?? "未知错误";
                throw new UpdateServiceException($"更新服务返回错误: {errorMessage}");
            }

            return new UpdateCheckResult(parsed, headerProgress);
        }

        public async Task DownloadAssetAsync(string downloadUrl, string destinationFilePath, IProgress<UpdateDownloadProgress>? progress, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                throw new ArgumentNullException(nameof(downloadUrl));
            }

            if (string.IsNullOrWhiteSpace(destinationFilePath))
            {
                throw new ArgumentNullException(nameof(destinationFilePath));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationFilePath)!);

            using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
            using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                string errorPayload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                throw new UpdateServiceException($"下载更新包失败 ({(int)response.StatusCode})", response.StatusCode, errorPayload);
            }

            long? contentLength = response.Content.Headers.ContentLength;
            long totalRead = 0;

            using Stream httpStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var fileStream = new FileStream(destinationFilePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true);
            progress?.Report(new UpdateDownloadProgress(0, contentLength));

            var buffer = new byte[81920];
            int read;
            while ((read = await httpStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                totalRead += read;
                progress?.Report(new UpdateDownloadProgress(totalRead, contentLength));
            }

            await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }

        private static IReadOnlyList<UpdateProgressStage> ParseProgressHeader(HttpResponseMessage response)
        {
            if (!response.Headers.TryGetValues(ProgressHeaderName, out IEnumerable<string>? values))
            {
                return Array.Empty<UpdateProgressStage>();
            }

            string raw = values.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return Array.Empty<UpdateProgressStage>();
            }

            try
            {
                var stages = JsonConvert.DeserializeObject<List<UpdateProgressStage>>(raw);
                return stages ?? (IReadOnlyList<UpdateProgressStage>)Array.Empty<UpdateProgressStage>();
            }
            catch (JsonException)
            {
                return Array.Empty<UpdateProgressStage>();
            }
        }
    }
}
