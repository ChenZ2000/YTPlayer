using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace YTPlayer.Core.ListenTogether
{
    public sealed class ListenTogetherClient : IDisposable
    {
        private readonly HttpClient _httpClient;
        private CancellationTokenSource? _cts;
        private Task? _pollTask;
        private Uri? _stateUri;
        private bool _disposed;

        public ListenTogetherClient()
        {
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(6)
            };
        }

        public event EventHandler<ListenTogetherPlaybackSnapshot>? SnapshotReceived;

        public event EventHandler<string>? StatusChanged;

        public bool IsConnected => _pollTask != null && _cts != null && !_cts.IsCancellationRequested;

        public Uri? StateUri => _stateUri;

        public void Connect(string inviteUrl)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(inviteUrl))
            {
                throw new ArgumentException("一起听 URL 不能为空。", nameof(inviteUrl));
            }

            Disconnect();
            _stateUri = BuildStateUri(inviteUrl);
            _cts = new CancellationTokenSource();
            _pollTask = Task.Run(() => PollLoopAsync(_cts.Token));
            StatusChanged?.Invoke(this, "正在连接一起听房间...");
        }

        public void Disconnect()
        {
            CancellationTokenSource? cts = _cts;
            _cts = null;
            _pollTask = null;
            if (cts == null)
            {
                return;
            }

            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                cts.Dispose();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Disconnect();
            _httpClient.Dispose();
        }

        private async Task PollLoopAsync(CancellationToken token)
        {
            int failureCount = 0;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    Uri? uri = _stateUri;
                    if (uri == null)
                    {
                        return;
                    }

                    string json = await _httpClient.GetStringAsync(uri, token).ConfigureAwait(false);
                    ListenTogetherPlaybackSnapshot? snapshot = JsonConvert.DeserializeObject<ListenTogetherPlaybackSnapshot>(json);
                    if (snapshot != null)
                    {
                        failureCount = 0;
                        SnapshotReceived?.Invoke(this, snapshot);
                        StatusChanged?.Invoke(this, snapshot.HasSong ? $"已同步：{snapshot.SongName}" : "已连接，等待房主播放");
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    failureCount++;
                    string status = failureCount >= 3
                        ? "一起听连接异常：" + ex.Message
                        : "正在等待一起听房主...";
                    StatusChanged?.Invoke(this, status);
                    Debug.WriteLine("[ListenTogether] Poll error: " + ex.Message);
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        private static Uri BuildStateUri(string inviteUrl)
        {
            string trimmed = inviteUrl.Trim();
            if (!trimmed.Contains("://"))
            {
                trimmed = "http://" + trimmed;
            }

            var builder = new UriBuilder(trimmed);
            string path = builder.Path ?? string.Empty;
            path = path.TrimEnd('/');
            if (path.Length == 0)
            {
                path = "/ytplayer-together";
            }

            if (!path.EndsWith("/state", StringComparison.OrdinalIgnoreCase))
            {
                path += "/state";
            }

            builder.Path = path;
            if (string.IsNullOrWhiteSpace(builder.Query))
            {
                throw new FormatException("一起听 URL 缺少 token 参数。");
            }

            return builder.Uri;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ListenTogetherClient));
            }
        }
    }
}
