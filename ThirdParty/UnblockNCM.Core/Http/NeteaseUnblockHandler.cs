using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnblockNCM.Core.Config;
using UnblockNCM.Core.Crypto;
using UnblockNCM.Core.Logging;
using UnblockNCM.Core.Models;
using UnblockNCM.Core.Net;
using UnblockNCM.Core.Services;
using UnblockNCM.Core.Utils;

namespace UnblockNCM.Core.Http
{
    /// <summary>
    /// DelegatingHandler that rewrites NetEase Music requests/responses in-process (no proxy needed).
    /// Attach to HttpClient used for NetEase API traffic.
    /// </summary>
    public class NeteaseUnblockHandler : DelegatingHandler
    {
        private readonly UnblockOptions _opt;
        private readonly ProviderManager _provider;
        private readonly Regex[] _targetHosts;
        private readonly HashSet<string> _targetPaths;
        private static readonly HttpRequestOptionsKey<NeteaseContext> NeteaseContextKey = new HttpRequestOptionsKey<NeteaseContext>("neteaseCtx");

        public NeteaseUnblockHandler(UnblockOptions opt, ProviderManager provider, HttpMessageHandler inner = null)
            : base(inner ?? new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                UseCookies = true
            })
        {
            _opt = opt;
            _provider = provider;
            _targetHosts = new[]
            {
                "music.163.com",
                "interface.music.163.com",
                "interface3.music.163.com",
                "interfacepc.music.163.com",
                "apm.music.163.com",
                "apm3.music.163.com",
                "interface.music.163.com.163jiasu.com",
                "interface3.music.163.com.163jiasu.com"
            }.Select(h => new Regex($"^{Regex.Escape(h)}$", RegexOptions.IgnoreCase)).ToArray();
            _targetPaths = new HashSet<string>(NeteaseTargets.Paths);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // only handle target hosts
            if (!IsTargetHost(request.RequestUri.Host))
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

            // Prepare context & adjust outbound request
            var ctx = await PrepareRequestAsync(request).ConfigureAwait(false);

            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (ctx == null || !_targetPaths.Contains(ctx.Path) || response.Content == null)
                return response;

            // Process response
            response = await ProcessResponseAsync(response, ctx).ConfigureAwait(false);
            return response;
        }

        private bool IsTargetHost(string host) => _targetHosts.Any(r => r.IsMatch(host));

        private async Task<NeteaseContext> PrepareRequestAsync(HttpRequestMessage req)
        {
            var path = req.RequestUri.AbsolutePath;
            // web api plain; still mark context for later
            if (path.StartsWith("/weapi/") || path.StartsWith("/api/"))
            {
                var ctx = new NeteaseContext
                {
                    Web = path.StartsWith("/weapi/"),
                    Path = path.Replace("/weapi/", "/api/").Split('?').First()
                };
                req.Headers.Remove("Accept-Encoding");
                req.Headers.Add("X-Real-IP", "118.88.88.88");
                req.Options.Set(NeteaseContextKey, ctx);
                return ctx;
            }

            if (!path.StartsWith("/eapi/") && !path.StartsWith("/api/linux/forward") && !path.StartsWith("/api/"))
                return null;

            var bodyStr = req.Content == null ? string.Empty : await req.Content.ReadAsStringAsync().ConfigureAwait(false);
            var ctxNet = new NeteaseContext { Web = false };
            string pad = string.Empty;
            if (!string.IsNullOrEmpty(bodyStr))
            {
                var matchPad = Regex.Match(bodyStr, "%0+$");
                pad = matchPad.Success ? matchPad.Value : string.Empty;
            }
            ctxNet.Pad = pad;

            if (path.StartsWith("/api/linux/forward"))
            {
                ctxNet.CryptoKind = NeteaseCryptoKind.LinuxApi;
                var hex = bodyStr.Substring(8);
                var decrypted = NeteaseCrypto.LinuxDecrypt(StringToByteArray(hex));
                var json = JObject.Parse(Encoding.UTF8.GetString(decrypted));
                ctxNet.Path = TrimTailDigits(new Uri(json.Value<string>("url")).AbsolutePath);
                ctxNet.Param = (JObject)json["params"];
            }
            else if (path.StartsWith("/eapi/"))
            {
                ctxNet.CryptoKind = NeteaseCryptoKind.EApi;
                var hex = bodyStr.Substring(7);
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
                var parsed = FormUrlEncodedParser.Parse(bodyStr ?? string.Empty);
                var obj = new JObject();
                foreach (var item in parsed) obj[item.Key] = item.Value;
                ctxNet.Path = TrimTailDigits(path);
                ctxNet.Param = obj;
            }

            req.Headers.Remove("Accept-Encoding");
            req.Headers.Remove("X-Real-IP");
            req.Headers.Add("X-Real-IP", "118.88.88.88");
            req.Options.Set(NeteaseContextKey, ctxNet);

            // Pretend download to play
            if (ctxNet.Path == "/api/song/enhance/download/url")
            {
                PretendPlay(ctxNet, req, "http://music.163.com/api/song/enhance/player/url");
            }
            if (ctxNet.Path == "/api/song/enhance/download/url/v1")
            {
                PretendPlay(ctxNet, req, "http://music.163.com/api/song/enhance/player/url/v1");
            }

            return ctxNet;
        }

        private async Task<HttpResponseMessage> ProcessResponseAsync(HttpResponseMessage resp, NeteaseContext ctx)
        {
            var buffer = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            if (buffer == null || buffer.Length == 0) return resp;

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

            if (_opt.EnableLocalVip) ApplyLocalVip(ctx);
            if (ctx.Path.Contains("url"))
                await TryMatchAsync(ctx).ConfigureAwait(false);
            if (ctx.Path.Contains("/usertool/sound/")) UnblockSoundEffects(json);
            if (ctx.Path.Contains("/vipauth/app/auth/query")) UnblockLyricsEffects(json);

            var bodyStr = json.ToString(Formatting.None);
            ByteArrayContent newContent;
            if (ctx.CryptoKind == NeteaseCryptoKind.EApi && ctx.ER)
            {
                var enc = NeteaseCrypto.EApiEncrypt(Encoding.UTF8.GetBytes(bodyStr));
                newContent = new ByteArrayContent(enc);
            }
            else
            {
                newContent = new ByteArrayContent(Encoding.UTF8.GetBytes(bodyStr));
            }

            // copy headers except length/encoding
            foreach (var h in resp.Content.Headers.Where(h => h.Key.ToLower() != "content-length" && h.Key.ToLower() != "content-encoding"))
                newContent.Headers.TryAddWithoutValidation(h.Key, h.Value);
            newContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            resp.Content = newContent;
            return resp;
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
                    if (_opt.EnableLocalSvip)
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

        private async Task TryMatchAsync(NeteaseContext ctx)
        {
            var dataToken = ctx.JsonBody["data"];
            if (dataToken == null) return;

            async Task ProcessItem(JObject item)
            {
                var code = item.Value<int?>("code") ?? 0;
                var br = item.Value<int?>("br") ?? 0;
                var need = code != 200 || item["freeTrialInfo"] != null || br < _opt.MinBr;
                if (!need && ctx.Web)
                {
                    if (item["url"] != null)
                        item["url"] = Regex.Replace(item["url"].ToString(), "(m\\d+?)(?!c)\\.music\\.126\\.net", "$1c.music.126.net");
                    return;
                }
                var id = item.Value<long?>("id")?.ToString();
                if (string.IsNullOrEmpty(id)) return;

                var audio = await _provider.MatchAsync(id, ctx.Param as JObject, CancellationToken.None).ConfigureAwait(false);
                var type = audio.Type ?? (audio.BitRate == 999000 ? "flac" : "mp3");
                string url = string.IsNullOrEmpty(_opt.Endpoint)
                    ? audio.Url
                    : $"{_opt.Endpoint}/package/{NeteaseCrypto.Base64UrlEncode(audio.Url)}/{id}.{type}";

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
                    await ProcessItem(obj).ConfigureAwait(false);
                }
            }
            else if (dataToken is JObject obj)
            {
                await ProcessItem(obj).ConfigureAwait(false);
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

        private void PretendPlay(NeteaseContext ctxNet, HttpRequestMessage req, string turnUrl)
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
            req.RequestUri = new Uri(url);
            req.Content = new StringContent(body ?? string.Empty, Encoding.UTF8, "application/x-www-form-urlencoded");
        }

        private static string TrimTailDigits(string path)
        {
            return Regex.Replace(path ?? string.Empty, "/\\d*$", string.Empty);
        }

        private static byte[] StringToByteArray(string hex)
        {
            int NumberChars = hex.Length;
            byte[] bytes = new byte[NumberChars / 2];
            for (int i = 0; i < NumberChars; i += 2)
                bytes[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);
            return bytes;
        }
    }
}
