using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace YTPlayer.Core.ListenTogether
{
    public sealed class ListenTogetherHttpServer : IDisposable
    {
        private const string RouteRoot = "/ytplayer-together";
        private const string RouteState = "/ytplayer-together/state";

        private readonly Func<ListenTogetherPlaybackSnapshot?> _snapshotProvider;
        private readonly string _token;
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private Task? _acceptLoopTask;
        private bool _disposed;

        public ListenTogetherHttpServer(Func<ListenTogetherPlaybackSnapshot?> snapshotProvider)
        {
            _snapshotProvider = snapshotProvider ?? throw new ArgumentNullException(nameof(snapshotProvider));
            _token = CreateToken();
        }

        public int Port { get; private set; }

        public string InviteUrl { get; private set; } = string.Empty;

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            if (_listener != null)
            {
                return Task.CompletedTask;
            }

            _listener = new TcpListener(IPAddress.Any, 0);
            _listener.Start(32);
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            InviteUrl = BuildInviteUrl(Port, _token);
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
            return Task.CompletedTask;
        }

        public void Stop()
        {
            if (_listener == null && _cts == null)
            {
                return;
            }

            try
            {
                _cts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                _listener?.Stop();
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }

            _listener = null;
            _cts?.Dispose();
            _cts = null;
            _acceptLoopTask = null;
            Port = 0;
            InviteUrl = string.Empty;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Stop();
        }

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient? client = null;
                try
                {
                    TcpListener? listener = _listener;
                    if (listener == null)
                    {
                        return;
                    }

                    client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                    _ = Task.Run(() => HandleClientAsync(client, token), CancellationToken.None);
                    client = null;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException ex) when (token.IsCancellationRequested)
                {
                    Debug.WriteLine("[ListenTogether] Accept loop stopped: " + ex.Message);
                    return;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[ListenTogether] Accept loop error: " + ex.Message);
                    client?.Dispose();
                    await Task.Delay(500, token).ConfigureAwait(false);
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken token)
        {
            using (client)
            {
                try
                {
                    client.NoDelay = true;
                    using NetworkStream stream = client.GetStream();
                    string request = await ReadRequestHeaderAsync(stream, token).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(request))
                    {
                        return;
                    }

                    string? rawPath = TryGetRequestPath(request);
                    if (string.IsNullOrWhiteSpace(rawPath))
                    {
                        await WriteTextAsync(stream, 400, "Bad Request", "Bad Request", token).ConfigureAwait(false);
                        return;
                    }

                    Uri requestUri = new Uri("http://localhost" + rawPath);
                    string path = requestUri.AbsolutePath.TrimEnd('/');
                    if (string.IsNullOrEmpty(path))
                    {
                        path = "/";
                    }

                    IDictionary<string, string> query = ParseQuery(requestUri.Query);
                    if (!query.TryGetValue("token", out string? tokenValue) ||
                        !string.Equals(tokenValue, _token, StringComparison.Ordinal))
                    {
                        await WriteTextAsync(stream, 403, "Forbidden", "Invalid together-listen token.", token).ConfigureAwait(false);
                        return;
                    }

                    if (string.Equals(path, RouteState, StringComparison.OrdinalIgnoreCase))
                    {
                        ListenTogetherPlaybackSnapshot snapshot = _snapshotProvider() ?? new ListenTogetherPlaybackSnapshot
                        {
                            UpdatedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        };
                        await WriteJsonAsync(stream, 200, "OK", snapshot, token).ConfigureAwait(false);
                    }
                    else if (string.Equals(path, RouteRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteTextAsync(stream, 200, "OK", "YTPlayer listen together endpoint is running.", token).ConfigureAwait(false);
                    }
                    else
                    {
                        await WriteTextAsync(stream, 404, "Not Found", "Not Found", token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[ListenTogether] Client handling error: " + ex.Message);
                }
            }
        }

        private static async Task<string> ReadRequestHeaderAsync(NetworkStream stream, CancellationToken token)
        {
            byte[] buffer = new byte[8192];
            int total = 0;
            while (total < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer, total, buffer.Length - total, token).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                total += read;
                if (ContainsHeaderTerminator(buffer, total))
                {
                    break;
                }
            }

            return total <= 0 ? string.Empty : Encoding.ASCII.GetString(buffer, 0, total);
        }

        private static bool ContainsHeaderTerminator(byte[] buffer, int length)
        {
            for (int i = 3; i < length; i++)
            {
                if (buffer[i - 3] == '\r' &&
                    buffer[i - 2] == '\n' &&
                    buffer[i - 1] == '\r' &&
                    buffer[i] == '\n')
                {
                    return true;
                }
            }

            return false;
        }

        private static string? TryGetRequestPath(string request)
        {
            string firstLine = request.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).FirstOrDefault() ?? string.Empty;
            string[] parts = firstLine.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !parts[0].Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return parts[1];
        }

        private static IDictionary<string, string> ParseQuery(string query)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(query))
            {
                return values;
            }

            string normalized = query[0] == '?' ? query.Substring(1) : query;
            string[] pairs = normalized.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string pair in pairs)
            {
                int separator = pair.IndexOf('=');
                string key = separator >= 0 ? pair.Substring(0, separator) : pair;
                string value = separator >= 0 ? pair.Substring(separator + 1) : string.Empty;
                key = Uri.UnescapeDataString(key.Replace("+", " "));
                value = Uri.UnescapeDataString(value.Replace("+", " "));
                if (!string.IsNullOrWhiteSpace(key))
                {
                    values[key] = value;
                }
            }

            return values;
        }

        private static async Task WriteJsonAsync(NetworkStream stream, int statusCode, string statusText, object payload, CancellationToken token)
        {
            string json = JsonConvert.SerializeObject(payload, Formatting.None);
            byte[] body = Encoding.UTF8.GetBytes(json);
            await WriteResponseAsync(stream, statusCode, statusText, "application/json; charset=utf-8", body, token).ConfigureAwait(false);
        }

        private static async Task WriteTextAsync(NetworkStream stream, int statusCode, string statusText, string text, CancellationToken token)
        {
            byte[] body = Encoding.UTF8.GetBytes(text ?? string.Empty);
            await WriteResponseAsync(stream, statusCode, statusText, "text/plain; charset=utf-8", body, token).ConfigureAwait(false);
        }

        private static async Task WriteResponseAsync(NetworkStream stream, int statusCode, string statusText, string contentType, byte[] body, CancellationToken token)
        {
            string header =
                $"HTTP/1.1 {statusCode} {statusText}\r\n" +
                $"Content-Type: {contentType}\r\n" +
                $"Content-Length: {body.Length}\r\n" +
                "Cache-Control: no-store\r\n" +
                "Access-Control-Allow-Origin: *\r\n" +
                "Connection: close\r\n\r\n";
            byte[] headerBytes = Encoding.ASCII.GetBytes(header);
            await stream.WriteAsync(headerBytes, 0, headerBytes.Length, token).ConfigureAwait(false);
            if (body.Length > 0)
            {
                await stream.WriteAsync(body, 0, body.Length, token).ConfigureAwait(false);
            }
        }

        private static string BuildInviteUrl(int port, string token)
        {
            string host = ResolveLanIpAddress();
            return $"http://{host}:{port}{RouteRoot}/?token={Uri.EscapeDataString(token)}";
        }

        private static string ResolveLanIpAddress()
        {
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up ||
                        nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    {
                        continue;
                    }

                    IPInterfaceProperties properties = nic.GetIPProperties();
                    foreach (UnicastIPAddressInformation address in properties.UnicastAddresses)
                    {
                        IPAddress ip = address.Address;
                        if (ip.AddressFamily != AddressFamily.InterNetwork ||
                            IPAddress.IsLoopback(ip))
                        {
                            continue;
                        }

                        byte[] bytes = ip.GetAddressBytes();
                        if (bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254)
                        {
                            continue;
                        }

                        return ip.ToString();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ListenTogether] Failed to resolve LAN IP: " + ex.Message);
            }

            return "127.0.0.1";
        }

        private static string CreateToken()
        {
            byte[] bytes = RandomNumberGenerator.GetBytes(18);
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ListenTogetherHttpServer));
            }
        }
    }
}
