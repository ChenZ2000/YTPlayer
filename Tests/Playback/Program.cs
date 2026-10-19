using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Newtonsoft.Json.Linq;
using YTPlayer.Core;
using YTPlayer.Core.Playback;

internal static class Program
{
    internal static int Passed;
    internal static void Check(bool condition, string name)
    { if (!condition) throw new Exception(name); Passed++; }
    private static async Task<int> Main(string[] args)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "test-data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); Environment.SetEnvironmentVariable("YTPLAYER_ROOT", root);
        try
        {
            if (args.Length == 0) { await RegressionTests.RunAsync(); Console.WriteLine($"PASS {Passed} assertions"); return 0; }
            if (args[0] == "--range") { await RangeProbe.RunAsync(args[1], args[2]); return 0; }
            if (args[0] == "--seek-tests") { await SeekTests.RunAsync(args[1]); Console.WriteLine($"PASS {Passed} seek assertions"); return 0; }
            using var client = new NeteaseApiClient();
            JObject profile = JObject.Parse(File.ReadAllText(args[1]));
            client.SetCookieString(profile.Value<string>("Cookie")!);
            if (args[0] == "--counts") { await Counts(client); return 0; }
            if (args[0] == "--audio")
            {
                var header = Cookies(profile);
                var data = await client.PostEApiAsync<JObject>("/api/song/enhance/player/url/v1", new JObject {
                    ["ids"] = "[" + args[2] + "]", ["level"] = "standard", ["encodeType"] = "mp3", ["header"] = header, ["e_r"] = true
                }, useIosHeaders:false, retryCount:4, skipErrorHandling:true);
                Directory.CreateDirectory(args[3]);
                foreach(var item in (data["data"] as JArray ?? new()).OfType<JObject>())
                {
                    string id = item.Value<string>("id")!;
                    Console.WriteLine(new JObject { ["id"]=id, ["code"]=item["code"], ["size"]=item["size"], ["time"]=item["time"], ["trial"]=item["freeTrialInfo"]!=null && item["freeTrialInfo"]!.Type!=JTokenType.Null, ["type"]=item["type"] });
                    if(item.Value<string>("url") is {Length:>0} url)
                        await File.WriteAllTextAsync(Path.Combine(args[3],id+".json"), item.ToString());
                }
                return 0;
            }
            if (args[0] == "--listen")
            {
                await Counts(client);
                await LiveListen.RunAsync(client,args[2],int.Parse(args[3]));
                await Counts(client); return 0;
            }
            throw new ArgumentException("Unknown mode");
        }
        catch(Exception e) { Console.Error.WriteLine(e.GetType().Name+": "+e.Message); return 1; }
        // SetCookieString may persist a session through production account handling.
        // Remove the isolated root even when a live probe fails.
        finally { Directory.Delete(root, recursive: true); }
    }
    internal static JObject Cookies(JObject profile)
    {
        var map=new JObject(); foreach(string c in profile.Value<string>("Cookie")!.Split(';'))
        { int i=c.IndexOf('='); if(i>0)map[c[..i].Trim()]=c[(i+1)..].Trim(); } return map;
    }
    internal static async Task Counts(NeteaseApiClient client)
    {
        var user=await client.GetUserAccountAsync();
        var level=await client.PostEApiAsync<JObject>("/api/user/level",new {}, useIosHeaders:false,retryCount:4,skipErrorHandling:true);
        Console.WriteLine(new JObject { ["profileListens"]=user.ListenSongs, ["levelCode"]=level["code"], ["levelListens"]=level["data"]?["nowPlayCount"] });
    }
}
