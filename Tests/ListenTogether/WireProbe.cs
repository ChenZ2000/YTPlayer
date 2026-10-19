using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using YTPlayer.Core;

internal static class WireProbe
{
    private static object Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    public static async Task RunAsync(string[] accounts)
    {
        for (int i = 0; i < accounts.Length; i++)
        {
            var saved = JObject.Parse(File.ReadAllText(accounts[i]));
            using var client = new NeteaseApiClient();
            client.SetCookieString(saved.Value<string>("Cookie") ?? throw new Exception("Missing Cookie"));
            var cookies = (CookieContainer)Field(client, "_cookieContainer");
            var records = new JArray();
            Replace(client, "_httpClient", new HttpClientHandler { CookieContainer = cookies, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate }, records);
            Replace(client, "_eapiClient", new HttpClientHandler { UseCookies = false }, records);
            var header = new Dictionary<string, object>();
            foreach (Cookie cookie in cookies.GetCookies(new Uri("https://interface3.music.163.com"))) header[cookie.Name] = cookie.Value;
            string cookieHeader = cookies.GetCookieHeader(new Uri("https://interface3.music.163.com"));
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var result = await client.PostWeApiAsync<JObject>("/weapi/listen/together/status/get", new { }, retryCount: 4, skipErrorHandling: true);
                    Console.WriteLine($"ACCOUNT {i + 1} WEAPI_STATUS code={result.Value<int?>("code")} inRoom={result["data"]?.Value<bool?>("inRoom")}");
                }
                catch (Exception ex) { Console.WriteLine($"ACCOUNT {i + 1} WEAPI_STATUS_ERROR {ex.Message}"); }
            }
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var result = await client.PostEApiAsync<JObject>("/api/listen/together/status/get",
                        new Dictionary<string, object> { ["header"] = header, ["e_r"] = "true" }, useIosHeaders: false,
                        retryCount: 4, skipErrorHandling: true, cookieHeaderOverride: cookieHeader);
                    Console.WriteLine($"ACCOUNT {i + 1} EAPI_STATUS code={result.Value<int?>("code")} inRoom={result["data"]?.Value<bool?>("inRoom")}");
                }
                catch (Exception ex) { Console.WriteLine($"ACCOUNT {i + 1} EAPI_STATUS_ERROR {ex.Message}"); }
            }
            Console.WriteLine(records.ToString(Newtonsoft.Json.Formatting.None));
        }
    }

    private static void Replace(NeteaseApiClient client, string name, HttpMessageHandler inner, JArray records)
    {
        var field = typeof(NeteaseApiClient).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        var original = (HttpClient)field.GetValue(client)!;
        var replacement = new HttpClient(new RecordingHandler(inner, records)) { Timeout = original.Timeout };
        foreach (var h in original.DefaultRequestHeaders) replacement.DefaultRequestHeaders.TryAddWithoutValidation(h.Key, h.Value);
        field.SetValue(client, replacement); original.Dispose();
    }

    private sealed class RecordingHandler(HttpMessageHandler inner, JArray records) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = await base.SendAsync(request, token);
            byte[] bytes = await response.Content.ReadAsByteArrayAsync(token);
            records.Add(new JObject { ["path"] = request.RequestUri!.AbsolutePath, ["http"] = (int)response.StatusCode,
                ["encoding"] = string.Join(",", response.Content.Headers.ContentEncoding), ["bytes"] = bytes.Length,
                ["prefixHex"] = Convert.ToHexString(bytes.Take(8).ToArray()), ["jsonPrefix"] = Encoding.UTF8.GetString(bytes.Take(1).ToArray()) == "{" });
            return response;
        }
    }
}
