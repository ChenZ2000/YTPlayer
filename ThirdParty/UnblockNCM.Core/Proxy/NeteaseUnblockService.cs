using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;
using UnblockNCM.Core.Config;
using UnblockNCM.Core.Crypto;
using UnblockNCM.Core.Logging;
using UnblockNCM.Core.Models;
using UnblockNCM.Core.Net;
using UnblockNCM.Core.Providers;
using UnblockNCM.Core.Services;
using UnblockNCM.Core.Utils;
using UnblockNCM.Core.Http;

namespace UnblockNCM.Core.Proxy
{
    public class NeteaseUnblockService : IDisposable
    {
        private readonly ProxyServer _proxyServer;
        private ExplicitProxyEndPoint _httpEndPoint;
        private ExplicitProxyEndPoint _httpsEndPoint;
        private readonly UnblockOptions _options;
        private readonly HttpHelper _http;
        private readonly ProviderManager _providerManager;
        private readonly CacheStorage _hookCache = new CacheStorage { AliveDuration = TimeSpan.FromDays(7) };
        private readonly Timer _cleanupTimer;
        private readonly Regex[] _whitelist;
        private readonly Regex[] _blacklist;

        private readonly HashSet<string> _targetHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "music.163.com",
            "interface.music.163.com",
            "interface3.music.163.com",
            "interfacepc.music.163.com",
            "apm.music.163.com",
            "apm3.music.163.com",
            "interface.music.163.com.163jiasu.com",
            "interface3.music.163.com.163jiasu.com"
        };

        private readonly HashSet<string> _targetPaths = new HashSet<string>
        {
            "/api/v3/playlist/detail",
            "/api/v3/song/detail",
            "/api/v6/playlist/detail",
            "/api/album/play",
            "/api/artist/privilege",
            "/api/album/privilege",
            "/api/v1/artist",
            "/api/v1/artist/songs",
            "/api/v2/artist/songs",
            "/api/artist/top/song",
            "/api/v1/album",
            "/api/album/v3/detail",
            "/api/playlist/privilege",
            "/api/song/enhance/player/url",
            "/api/song/enhance/player/url/v1",
            "/api/song/enhance/download/url",
            "/api/song/enhance/download/url/v1",
            "/api/song/enhance/privilege",
            "/api/ad",
            "/batch",
            "/api/batch",
            "/api/listen/together/privilege/get",
            "/api/playmode/intelligence/list",
            "/api/v1/search/get",
            "/api/v1/search/song/get",
            "/api/search/complex/get",
            "/api/search/complex/page",
            "/api/search/pc/complex/get",
            "/api/search/pc/complex/page",
            "/api/search/song/list/page",
            "/api/search/song/page",
            "/api/cloudsearch/pc",
            "/api/v1/playlist/manipulate/tracks",
            "/api/song/like",
            "/api/v1/play/record",
            "/api/playlist/v4/detail",
            "/api/v1/radio/get",
            "/api/v1/discovery/recommend/songs",
            "/api/usertool/sound/mobile/promote",
            "/api/usertool/sound/mobile/theme",
            "/api/usertool/sound/mobile/animationList",
            "/api/usertool/sound/mobile/all",
            "/api/usertool/sound/mobile/detail",
            "/api/vipauth/app/auth/query",
            "/api/music-vip-membership/client/vip/info",
        };

        public NeteaseUnblockService(UnblockOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _options.Normalize();

            SelectHelper.EnableFlac = options.EnableFlac;

            IExternalProxy upstreamProxy = null;
            IWebProxy httpClientProxy = null;
            if (!string.IsNullOrWhiteSpace(options.UpstreamProxy))
            {
                var uri = new Uri(options.UpstreamProxy);
                NetworkCredential cred = null;
                if (!string.IsNullOrEmpty(uri.UserInfo))
                {
                    var parts = uri.UserInfo.Split(':');
                    cred = new NetworkCredential(parts[0], parts.Length > 1 ? parts[1] : string.Empty);
                }
                upstreamProxy = new ExternalProxy(uri.Host, uri.Port, cred?.UserName, cred?.Password);
                httpClientProxy = new WebProxy(uri) { Credentials = cred };
            }
            _http = new HttpHelper(httpClientProxy, TimeSpan.FromSeconds(15));

            var cacheCommon = new CacheStorage();
            var find = new FindService(_http, cacheCommon, Environment.GetEnvironmentVariable("SEARCH_ALBUM") == "true", options.NoCache);
            var providers = new Dictionary<string, IProvider>(StringComparer.OrdinalIgnoreCase)
            {
                { "kugou", new KugouProvider(_http, cacheCommon, options.NoCache) },
                { "bodian", new BodianProvider(_http, cacheCommon, options.NoCache) },
                { "migu", new MiguProvider(_http, cacheCommon, Environment.GetEnvironmentVariable("MIGU_COOKIE"), options.NoCache) },
                { "kuwo", new KuwoProvider(_http, cacheCommon, options.NoCache) },
                { "qq", new QQProvider(_http, cacheCommon, options.NoCache) },
                { "joox", new JooxProvider(_http, cacheCommon, options.NoCache) },
                { "bilibili", new BilibiliProvider(_http, cacheCommon, options.NoCache) },
                { "bilivideo", new BilivideoProvider(_http, cacheCommon, options.NoCache) },
                { "pyncmd", new PyncmdProvider(_http, cacheCommon, options.NoCache) },
            };
            _providerManager = new ProviderManager(providers, find, options);

            _proxyServer = new ProxyServer();
            _proxyServer.ForwardToUpstreamGateway = upstreamProxy != null;
            if (upstreamProxy != null)
            {
                _proxyServer.UpStreamHttpProxy = upstreamProxy;
                _proxyServer.UpStreamHttpsProxy = upstreamProxy;
            }
            _proxyServer.EnableConnectionPool = true;
            _proxyServer.ReuseSocket = true;
            _proxyServer.ConnectionTimeOutSeconds = 10;
            _proxyServer.ExceptionFunc = ex => Log.Error("proxy", "Unhandled proxy exception", ex);

            _proxyServer.BeforeRequest += OnBeforeRequest;
            _proxyServer.BeforeResponse += OnBeforeResponse;

            _whitelist = _options.Whitelist.Select(p => new Regex(p, RegexOptions.IgnoreCase)).ToArray();
            _blacklist = _options.Blacklist.Select(p => new Regex(p, RegexOptions.IgnoreCase)).ToArray();

            _cleanupTimer = new Timer(_ => _hookCache.Cleanup(), null, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15));
        }

        private X509Certificate2 LoadCertificate()
        {
            try
            {
                var pfx = _options.SignCertPath;
                if (!string.IsNullOrWhiteSpace(pfx) && File.Exists(pfx))
                {
                    return new X509Certificate2(pfx, string.Empty, X509KeyStorageFlags.Exportable);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("cert", "Failed to load provided certificate, falling back to generated one", ex);
            }
            return null;
        }

        public void Start()
        {
            var cert = LoadCertificate();
            if (cert != null)
            {
                _proxyServer.CertificateManager.RootCertificate = cert;
            }
            else
            {
                var generated = _proxyServer.CertificateManager.CreateRootCertificate();
                _proxyServer.CertificateManager.TrustRootCertificate(false);
            }

            var any = string.IsNullOrWhiteSpace(_options.Address) ? IPAddress.Any : IPAddress.Parse(_options.Address);
            _httpEndPoint = new ExplicitProxyEndPoint(any, _options.HttpPort, false);
            _httpsEndPoint = new ExplicitProxyEndPoint(any, _options.HttpsPort, true)
            {
                GenericCertificate = cert
            };
            _httpEndPoint.BeforeTunnelConnectRequest += OnBeforeTunnelConnectRequest;
            _httpsEndPoint.BeforeTunnelConnectRequest += OnBeforeTunnelConnectRequest;
            _proxyServer.AddEndPoint(_httpEndPoint);
            _proxyServer.AddEndPoint(_httpsEndPoint);
            _proxyServer.Start();
            Log.Info("proxy", $"HTTP proxy listening on {any}:{_options.HttpPort}");
            if (_options.HttpsPort > 0) Log.Info("proxy", $"HTTPS proxy listening on {any}:{_options.HttpsPort}");
        }

        public void Stop()
        {
            _cleanupTimer?.Dispose();
            _proxyServer?.Stop();
        }

        private bool IsTargetHost(string host) => _targetHosts.Contains(host ?? string.Empty);

        private string TrimTailDigits(string path)
        {
            return Regex.Replace(path ?? string.Empty, "/\\d*$", string.Empty);
        }

        private bool Authenticate(SessionEventArgs e)
        {
            if (string.IsNullOrEmpty(_options.Token)) return true;
            var header = e.HttpClient.Request.Headers.GetFirstHeader("Proxy-Authorization")?.Value;
            if (!string.IsNullOrEmpty(header) && header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                var credential = Encoding.UTF8.GetString(Convert.FromBase64String(header.Substring(6)));
                if (credential == _options.Token)
                {
                    e.HttpClient.Request.Headers.RemoveHeader("Proxy-Authorization");
                    return true;
                }
            }
            var headers = new Dictionary<string, HttpHeader>
            {
                { "Proxy-Authenticate", new HttpHeader("Proxy-Authenticate", "Basic realm=\"realm\"") }
            };
            e.GenericResponse("Proxy Auth Required", HttpStatusCode.ProxyAuthenticationRequired, headers, true);
            e.TerminateSession();
            return false;
        }

        private bool Authenticate(TunnelConnectSessionEventArgs e)
        {
            if (string.IsNullOrEmpty(_options.Token)) return true;
            var header = e.HttpClient.ConnectRequest?.Headers?.GetFirstHeader("Proxy-Authorization")?.Value
                         ?? e.HttpClient.Request.Headers.GetFirstHeader("Proxy-Authorization")?.Value;
            if (!string.IsNullOrEmpty(header) && header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                var credential = Encoding.UTF8.GetString(Convert.FromBase64String(header.Substring(6)));
                if (credential == _options.Token)
                {
                    e.HttpClient.Request.Headers.RemoveHeader("Proxy-Authorization");
                    e.HttpClient.ConnectRequest?.Headers?.RemoveHeader("Proxy-Authorization");
                    return true;
                }
            }
            e.DenyConnect = true;
            e.TerminateSession();
            return false;
        }

        private bool Filter(SessionEventArgs e)
        {
            var url = e.HttpClient.Request.RequestUri.ToString();
            var allow = _whitelist.Any(r => r.IsMatch(url));
            var deny = _blacklist.Any(r => r.IsMatch(url));
            if (!allow && deny)
            {
                e.GenericResponse("Forbidden", HttpStatusCode.Forbidden, Enumerable.Empty<HttpHeader>(), true);
                e.TerminateSession();
                return false;
            }
            if (_options.Strict && !allow)
            {
                e.GenericResponse("Forbidden", HttpStatusCode.Forbidden, Enumerable.Empty<HttpHeader>(), true);
                e.TerminateSession();
                return false;
            }
            return true;
        }

        private bool Filter(TunnelConnectSessionEventArgs e)
        {
            var url = $"https://{e.HttpClient.ConnectRequest.Host}";
            var allow = _whitelist.Any(r => r.IsMatch(url));
            var deny = _blacklist.Any(r => r.IsMatch(url));
            if (!allow && deny)
            {
                e.DenyConnect = true;
                e.TerminateSession();
                return false;
            }
            if (_options.Strict && !allow)
            {
                e.DenyConnect = true;
                e.TerminateSession();
                return false;
            }
            return true;
        }

        private Task OnBeforeTunnelConnectRequest(object sender, TunnelConnectSessionEventArgs e)
        {
            if (!Authenticate(e)) return Task.CompletedTask;
            if (!Filter(e)) return Task.CompletedTask;

            var host = e.HttpClient.ConnectRequest?.Host ?? e.HttpClient.Request.RequestUri.Host;
            if (IsTargetHost(host) && _options.HttpsPort > 0)
            {
                e.DecryptSsl = true;
            }
            return Task.CompletedTask;
        }

        private async Task OnBeforeRequest(object sender, SessionEventArgs e)
        {
            if (!Authenticate(e)) return;
            if (!Filter(e)) return;

            if (e.HttpClient.Request.Url.EndsWith("/proxy.pac", StringComparison.OrdinalIgnoreCase))
            {
                var pac = $"function FindProxyForURL(url, host) {{ if ({string.Join(" || ", _targetHosts.Select(h => $"host=='{h}'"))}) return 'PROXY {_options.Address ?? "127.0.0.1"}:{_options.HttpPort}'; return 'DIRECT'; }}";
                var headers = new Dictionary<string, HttpHeader>
                {
                    { "Content-Type", new HttpHeader("Content-Type", "application/x-ns-proxy-autoconfig") }
                };
                e.Ok(pac, headers, true);
                return;
            }

            if (!e.HttpClient.Request.HasBody) return;

            var uri = e.HttpClient.Request.RequestUri;
            var host = uri.Host;
            if (!IsTargetHost(host)) return;

            var path = uri.AbsolutePath;
            if (path.StartsWith("/weapi/") || path.StartsWith("/api/"))
            {
                e.HttpClient.Request.Headers.RemoveHeader("Accept-Encoding");
                var ctx = new NeteaseContext
                {
                    Web = path.StartsWith("/weapi/"),
                    Path = path.Replace("/weapi/", "/api/").Split('?').First(),
                };
                e.UserData = ctx;
                return;
            }

            if (!path.StartsWith("/eapi/") && !path.StartsWith("/api/linux/forward") && !path.StartsWith("/api/"))
                return;

            var body = await e.GetRequestBodyAsString();
            var ctxNet = new NeteaseContext();
            string pad = string.Empty;
            if (body != null)
            {
                var matchPad = Regex.Match(body, "%0+$");
                pad = matchPad.Success ? matchPad.Value : string.Empty;
            }
            ctxNet.Pad = pad;
            if (path.StartsWith("/api/linux/forward"))
            {
                ctxNet.CryptoKind = NeteaseCryptoKind.LinuxApi;
                var hex = body.Substring(8);
                var decrypted = NeteaseCrypto.LinuxDecrypt(StringToByteArray(hex));
                var json = JObject.Parse(Encoding.UTF8.GetString(decrypted));
                ctxNet.Path = TrimTailDigits(new Uri(json.Value<string>("url")).AbsolutePath);
                ctxNet.Param = (JObject)json["params"];
            }
            else if (path.StartsWith("/eapi/"))
            {
                ctxNet.CryptoKind = NeteaseCryptoKind.EApi;
                var hex = body.Substring(7);
                var decrypted = NeteaseCrypto.EApiDecrypt(StringToByteArray(hex));
                var parts = Encoding.UTF8.GetString(decrypted).Split(new[] { "-36cd479b6b5-" }, StringSplitOptions.None);
                ctxNet.Path = TrimTailDigits(parts[0]);
                ctxNet.Param = JObject.Parse(parts[1]);
                if (ctxNet.Param.TryGetValue("e_r", out var er) && (er.Type == JTokenType.Boolean && er.Value<bool>() || er.Type == JTokenType.String && er.ToString() == "true"))
                    ctxNet.ER = true;
            }
            else if (path.StartsWith("/api/"))
            {
                ctxNet.CryptoKind = NeteaseCryptoKind.Api;
                var parsed = System.Web.HttpUtility.ParseQueryString(body);
                var obj = new JObject();
                foreach (string key in parsed.Keys) obj[key] = parsed[key];
                ctxNet.Path = TrimTailDigits(path);
                ctxNet.Param = obj;
            }
            ctxNet.Web = false;
            e.UserData = ctxNet;

            e.HttpClient.Request.Headers.RemoveHeader("Accept-Encoding");
            e.HttpClient.Request.Headers.RemoveHeader("X-Real-IP");
            e.HttpClient.Request.Headers.AddHeader("X-Real-IP", "118.88.88.88");
            if (e.HttpClient.Request.Headers.HeaderExists("x-aeapi"))
            {
                e.HttpClient.Request.Headers.RemoveHeader("x-aeapi");
                e.HttpClient.Request.Headers.AddHeader("x-aeapi", "false");
            }

            // pretent download as play
            if (ctxNet.Path == "/api/song/enhance/download/url")
            {
                PretendPlay(ctxNet, e, "http://music.163.com/api/song/enhance/player/url");
            }
            if (ctxNet.Path == "/api/song/enhance/download/url/v1")
            {
                PretendPlay(ctxNet, e, "http://music.163.com/api/song/enhance/player/url/v1");
            }
        }

        private static byte[] StringToByteArray(string hex)
        {
            int NumberChars = hex.Length;
            byte[] bytes = new byte[NumberChars / 2];
            for (int i = 0; i < NumberChars; i += 2)
                bytes[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);
            return bytes;
        }

        private void PretendPlay(NeteaseContext ctxNet, SessionEventArgs e, string turnUrl)
        {
            JObject param = ctxNet.Param ?? new JObject();
            string url = turnUrl;
            string body = null;
            switch (ctxNet.CryptoKind)
            {
                case NeteaseCryptoKind.LinuxApi:
                    param = new JObject
                    {
                        ["ids"] = $"[\"{ctxNet.Param?["id"]}\"]",
                        ["br"] = ctxNet.Param?["br"]
                    };
                    var linux = NeteaseCrypto.LinuxEncryptRequest(new Uri(url), param);
                    url = linux.url;
                    body = linux.body + ctxNet.Pad;
                    break;
                case NeteaseCryptoKind.EApi:
                    param = new JObject
                    {
                        ["ids"] = $"[\"{ctxNet.Param?["id"]}\"]",
                        ["br"] = ctxNet.Param?["br"],
                        ["e_r"] = ctxNet.Param?["e_r"],
                        ["header"] = ctxNet.Param?["header"]
                    };
                    var eapi = NeteaseCrypto.EApiEncryptRequest(new Uri(url), param);
                    url = eapi.url;
                    body = eapi.body + ctxNet.Pad;
                    break;
                case NeteaseCryptoKind.Api:
                    param = new JObject
                    {
                        ["ids"] = $"[\"{ctxNet.Param?["id"]}\"]",
                        ["br"] = ctxNet.Param?["br"],
                        ["e_r"] = ctxNet.Param?["e_r"],
                        ["header"] = ctxNet.Param?["header"]
                    };
                    var api = NeteaseCrypto.ApiEncryptRequest(new Uri(url), param);
                    url = api.url;
                    body = api.body + ctxNet.Pad;
                    break;
            }
            ctxNet.Param = param;
            ctxNet.Path = new Uri(url).AbsolutePath;
            e.HttpClient.Request.RequestUri = new Uri(url);
            e.SetRequestBodyString(body);
        }

        private async Task OnBeforeResponse(object sender, SessionEventArgs e)
        {
            if (e.UserData is not NeteaseContext ctx) return;
            if (!_targetPaths.Contains(ctx.Path)) return;
            if (e.HttpClient.Response.StatusCode != 200) return;

            var encoding = e.HttpClient.Response.ContentEncoding ?? string.Empty;
            var buffer = await e.GetResponseBody();
            buffer = CompressionHelper.Decompress(buffer, encoding);
            if (buffer == null || buffer.Length == 0) return;

            JObject json;
            if (ctx.CryptoKind == NeteaseCryptoKind.EApi && ctx.ER)
            {
                var dec = NeteaseCrypto.EApiDecrypt(buffer);
                json = JObject.Parse(Encoding.UTF8.GetString(dec));
            }
            else
            {
                json = JObject.Parse(Encoding.UTF8.GetString(buffer));
            }
            ctx.JsonBody = json;

            if (_options.EnableLocalVip)
            {
                ApplyLocalVip(ctx);
            }

            if (ctx.Path.Contains("url"))
            {
                await TryMatchAsync(e, ctx);
            }

            if (ctx.Path.Contains("/usertool/sound/"))
            {
                UnblockSoundEffects(json);
            }
            if (ctx.Path.Contains("/vipauth/app/auth/query"))
            {
                UnblockLyricsEffects(json);
            }

            var bodyStr = json.ToString(Formatting.None);
            if (ctx.CryptoKind == NeteaseCryptoKind.EApi && ctx.ER)
            {
                var enc = NeteaseCrypto.EApiEncrypt(Encoding.UTF8.GetBytes(bodyStr));
                e.SetResponseBody(enc);
            }
            else
            {
                e.SetResponseBodyString(bodyStr);
            }
            e.HttpClient.Response.Headers.RemoveHeader("transfer-encoding");
            e.HttpClient.Response.Headers.RemoveHeader("content-encoding");
            e.HttpClient.Response.Headers.RemoveHeader("content-length");
        }

        private void ApplyLocalVip(NeteaseContext ctx)
        {
            var vipPath = "/api/music-vip-membership/client/vip/info";
            if (ctx.Path == vipPath || ctx.Path == "/batch" || ctx.Path == "/api/batch")
            {
                JObject info;
                if (ctx.Path == vipPath) info = ctx.JsonBody;
                else info = ctx.JsonBody[vipPath] as JObject;
                if (info?["data"] is JObject data)
                {
                    var now = data.Value<long?>("now") ?? DateTimeOffset.Now.ToUnixTimeMilliseconds();
                    var expire = now + 31622400000;
                    data["redVipLevel"] = 7;
                    data["redVipAnnualCount"] = 1;
                    data["musicPackage"] = PatchVipPackage(data["musicPackage"] as JObject, 230, expire);
                    data["associator"] = PatchVipPackage(data["associator"] as JObject, 100, expire);
                    if (_options.EnableLocalSvip)
                    {
                        data["redplus"] = PatchVipPackage(data["redplus"] as JObject, 300, expire);
                        data["albumVip"] = PatchVipPackage(data["albumVip"] as JObject, 400, expire, 0);
                    }
                }
                if (ctx.Path != vipPath) ctx.JsonBody[vipPath] = info;
                else ctx.JsonBody = info;
            }
        }

        private JObject PatchVipPackage(JObject obj, int vipCode, long expireTime, int vipLevel = 7)
        {
            obj ??= new JObject();
            obj["vipCode"] = vipCode;
            obj["vipLevel"] = vipLevel;
            obj["expireTime"] = expireTime;
            obj["isSign"] = false;
            obj["isSignIap"] = false;
            obj["isSignDeduct"] = false;
            obj["isSignIapDeduct"] = false;
            return obj;
        }

        private async Task TryMatchAsync(SessionEventArgs e, NeteaseContext ctx)
        {
            var dataToken = ctx.JsonBody["data"];
            if (dataToken == null) return;
            var os = "pc";
            if (ctx.Param != null && ctx.Param.TryGetValue("header", out var headerTok))
            {
                try
                {
                    var headerObj = headerTok.Type == JTokenType.String ? JObject.Parse(headerTok.ToString()) : (JObject)headerTok;
                    os = headerObj?["os"]?.ToString() ?? os;
                }
                catch { }
            }

            async Task ProcessItem(JObject item)
            {
                var code = item.Value<int?>("code") ?? 0;
                var br = item.Value<int?>("br") ?? 0;
                var need = code != 200 || item["freeTrialInfo"] != null || br < _options.MinBr;
                if (!need && ctx.Web)
                {
                    if (item["url"] != null)
                        item["url"] = item["url"].ToString().Replace("(m\\d+?)(?!c)\\.music\\.126\\.net", "$1c.music.126.net");
                    return;
                }
                var id = item.Value<long?>("id")?.ToString();
                if (string.IsNullOrEmpty(id)) return;

                var audio = await _providerManager.MatchAsync(id, ctx.Param as JObject, CancellationToken.None);
                var type = audio.Type ?? (audio.BitRate == 999000 ? "flac" : "mp3");
                string url;
                if (!string.IsNullOrEmpty(_options.Endpoint))
                {
                    var scheme = os == "pc" || os == "uwp" ? "http" : "https";
                    url = $"{_options.Endpoint.Replace("https://", $"{scheme}://")}/package/{NeteaseCrypto.Base64UrlEncode(audio.Url)}/{id}.{type}";
                }
                else url = audio.Url;

                item["type"] = type;
                item["url"] = url;
                item["md5"] = audio.Md5 ?? NeteaseCrypto.Md5Hex(audio.Url ?? string.Empty);
                item["br"] = audio.BitRate ?? 128000;
                item["size"] = audio.Size;
                item["code"] = 200;
                item["freeTrialInfo"] = null;
                item["flag"] = 0;
            }

            if (dataToken is JArray arr)
            {
                foreach (var obj in arr.OfType<JObject>())
                {
                    await ProcessItem(obj);
                }
            }
            else if (dataToken is JObject obj)
            {
                await ProcessItem(obj);
            }
        }

        private void UnblockSoundEffects(JObject obj)
        {
            var data = obj["data"];
            if (obj.Value<int?>("code") == 200)
            {
                if (data is JArray arr)
                {
                    foreach (var item in arr.OfType<JObject>())
                    {
                        if (item["type"] != null) item["type"] = 1;
                    }
                }
                else if (data is JObject jo && jo["type"] != null) jo["type"] = 1;
            }
        }

        private void UnblockLyricsEffects(JObject obj)
        {
            var data = obj["data"];
            if (obj.Value<int?>("code") == 200 && data is JArray arr)
            {
                foreach (var item in arr.OfType<JObject>())
                {
                    item["canUse"] = true;
                    item["canNotUseReasonCode"] = 200;
                }
            }
        }

        public void Dispose()
        {
            Stop();
            _http?.Dispose();
        }
    }
}
