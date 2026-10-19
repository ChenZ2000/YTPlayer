using System.Diagnostics;
using System.Net;
using System.Reflection;
using Newtonsoft.Json.Linq;
using YTPlayer.Core;
using YTPlayer.Core.Playback;
using YTPlayer.Core.Playback.Cache;
using YTPlayer.Core.Streaming;
using YTPlayer.Utils;

internal static class RegressionTests
{
    internal static void Set(object obj,string field,object value) => obj.GetType().GetField(field,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(obj,value);
    internal static async Task RunAsync()
    {
        var context=new PlaybackReportContext("1","","",new("album","2"),300,false,"song");
        long t=Stopwatch.GetTimestamp();
        void Observe(double pos,bool playing,double seconds) => context.Observe(pos,playing,t+(long)(seconds*Stopwatch.Frequency));
        Observe(0,true,0); Observe(1,true,1); Observe(2,true,2);
        Program.Check(Math.Abs(context.PlayedSeconds-2)<.001,"actual advancing playback");
        Observe(200,true,3); Program.Check(context.PlayedSeconds==2,"seek target is not listened time");
        Observe(201,true,4); Observe(201,false,5); Observe(201,false,65); Observe(201,true,66); Observe(202,true,67);
        Program.Check(context.PlayedSeconds==4,"pause and long gaps excluded");
        Observe(50,true,68); Observe(51,true,69); Program.Check(context.PlayedSeconds==5,"backward seek adds only newly listened time");
        Observe(51,true,70); Observe(double.NaN,true,71); Program.Check(context.PlayedSeconds==5,"stalls and invalid positions excluded");
        using var client=new NeteaseApiClient(); client.SetCookieString("MUSIC_U=fixture; __csrf=csrf; os=pc");
        var handler=new Handler(); var field=typeof(NeteaseApiClient).GetField("_eapiClient",BindingFlags.Instance|BindingFlags.NonPublic)!;
        ((HttpClient)field.GetValue(client)!).Dispose(); field.SetValue(client,new HttpClient(handler));
        using(var service=new PlaybackReportingService(client))
        {
            service.ReportSongStarted(context); service.ReportSongCompleted(context,PlaybackEndReason.Interrupted);
            service.ReportSongCompleted(context,PlaybackEndReason.Interrupted);
            for(int i=0;i<100&&handler.Requests.Count<2;i++)await Task.Delay(20);
            Program.Check(handler.Requests.Count==2,"one start and one end, duplicate completion ignored");
            var requests=handler.Requests.ToArray();
            Program.Check(requests.All(x=>x.Host=="clientlog.music.163.com"),"both events use ingestion host");
            Program.Check(requests.All(x=>x.Body["header"]?.Value<string>("os")=="osx"),"report-specific client dialect");
            var start=JArray.Parse(requests[0].Body.Value<string>("logs")!);
            var end=JArray.Parse(requests[1].Body.Value<string>("logs")!);
            Program.Check(start.Count==1 && start[0].Value<string>("action")=="startplay","no zero-duration play at start");
            Program.Check(end.Count==1 && end[0]["json"]?.Value<int>("time")==5,"actual 5 seconds submitted without invented half-song threshold");
            Program.Check(end[0]["json"]?.Value<string>("end")=="interrupt","actual termination reason retained");
            Program.Check(end[0]["json"]?.Value<string>("mainsiteWeb")=="1","desktop reporting flag present");
        }
        handler.Response="{\"code\":500}";
        Program.Check(!await client.SendScrobbleEapiAsync(1,2,5),"rejection is failure");
        var cancelled=new CancellationTokenSource(); cancelled.Cancel();
        try { await client.SendScrobbleEapiAsync(1,2,5,cancellationToken:cancelled.Token); throw new Exception("Cancellation swallowed"); }
        catch(OperationCanceledException) { Program.Check(true,"cancellation preserved"); }
        await Durability(client,handler);
        await Ranges();
        Program.Check(BassAudioEngine.EstimateFileOffset(50,100,1000000)==500000,"encoded midpoint uses file size");
        Program.Check(BassAudioEngine.EstimateFileOffset(100,100,1000000)==999999,"EOF prefetch stays inside file");
        Program.Check(BassAudioEngine.EstimateFileOffset(double.NaN,100,1000000)==0,"invalid seek cannot overflow chunk index");
        Program.Check(BassAudioEngine.EstimateFileOffset(50,0,1000000)==0,"unknown duration has safe estimate");
    }
    private static async Task Durability(NeteaseApiClient client,Handler handler)
    {
        var context=new PlaybackReportContext("9","","",new("album","2"),300,false,"song"){PlayedSeconds=65};
        handler.Response="{\"code\":200}";handler.Block=true;
        int before=handler.Requests.Count;
        using(var service=new PlaybackReportingService(client))
        {
            service.ReportSongStarted(context);service.ReportSongCompleted(context,PlaybackEndReason.Stopped);
            await service.DrainAsync(TimeSpan.FromMilliseconds(100));
        }
        string retry=Path.Combine(PathHelper.ApplicationRootDirectory,"Logs","playback_retry.json");
        var persisted=JArray.Parse(File.ReadAllText(retry));
        Program.Check(persisted.Count==2,"shutdown persists inflight and queued operations");
        Program.Check(!File.ReadAllText(retry).Contains("fixture"),"retry journal contains no session token");
        handler.Block=false;
        using(var restored=new PlaybackReportingService(client)) await restored.DrainAsync(TimeSpan.FromSeconds(1));
        Program.Check(handler.Requests.Count==before+2,"same-account pending events are restored");

        using(var service=new PlaybackReportingService(client))
        {
            var switched=new PlaybackReportContext("10","","",new("album","2"),300,false,"song"){PlayedSeconds=60};
            service.ReportSongStarted(switched);
            for(int i=0;i<100&&handler.Requests.Count<before+3;i++)await Task.Delay(10);
            client.SetCookieString("MUSIC_U=different-account; __csrf=csrf;");
            service.ReportSongCompleted(switched,PlaybackEndReason.Stopped);
            await service.DrainAsync(TimeSpan.FromSeconds(1));
        }
        Program.Check(handler.Requests.Count==before+3,"old listen is never reported under new account");
        using(var disabled=new PlaybackReportingService(client,false))
        {
            disabled.ReportSongStarted(context);await disabled.DrainAsync(TimeSpan.FromMilliseconds(100));
        }
        Program.Check(handler.Requests.Count==before+3,"disabled reporting submits nothing");
    }
    private static async Task Ranges()
    {
        using var handler=new RangeHandler(); using var http=new HttpClient(handler);
        var downloader=new ChunkDownloadManager("https://fixture.invalid/audio",1024,256,http);
        handler.Mode=0;
        Program.Check(await downloader.DownloadChunkAsync(1,default)==null,"whole-file 200 cannot become a middle chunk");
        handler.Mode=1;
        Program.Check(await downloader.DownloadChunkAsync(1,default)==null,"wrong Content-Range cannot corrupt cache");
        handler.Mode=2;
        Program.Check((await downloader.DownloadChunkAsync(1,default))?.Length==256,"exact 206 range accepted");
        Program.Check(await downloader.DownloadChunkAsync(-1,default)==null,"invalid chunk never downloads");
        handler.Mode=0;
        var probe=await HttpRangeHelper.CheckRangeSupportAsync("https://fixture.invalid/audio",http);
        Program.Check(!probe.supportsRange,"Accept-Ranges none is not byte support");
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        try{await HttpRangeHelper.CheckRangeSupportAsync("https://fixture.invalid/audio",http,cancel.Token);throw new Exception("probe swallowed cancellation");}
        catch(OperationCanceledException){Program.Check(true,"range probe respects cancellation");}
    }
    private sealed class RangeHandler:HttpMessageHandler
    {
        internal int Mode;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if(request.Method==HttpMethod.Head){var h=new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(new byte[1024])};h.Headers.AcceptRanges.Add("none");return Task.FromResult(h);}
            long from=request.Headers.Range!.Ranges.Single().From!.Value;
            long to=request.Headers.Range.Ranges.Single().To!.Value;
            var response=new HttpResponseMessage(Mode==0?HttpStatusCode.OK:HttpStatusCode.PartialContent){Content=new ByteArrayContent(new byte[to-from+1])};
            if(Mode!=0)response.Content.Headers.ContentRange=new System.Net.Http.Headers.ContentRangeHeaderValue(Mode==1?0:from,Mode==1?to-from:to,1024);
            return Task.FromResult(response);
        }
    }
    internal sealed class Handler:HttpMessageHandler
    {
        internal readonly System.Collections.Concurrent.ConcurrentQueue<(string Host,JObject Body)> Requests=new();
        internal string Response="{\"code\":200,\"data\":\"success\"}";
        internal bool Block;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); string form=await request.Content!.ReadAsStringAsync(token);
            if(Block)await Task.Delay(Timeout.Infinite,token);
            string clear=EncryptionHelper.DecryptEapi(Convert.FromHexString(Uri.UnescapeDataString(form.Split('=')[1])));
            Requests.Enqueue((request.RequestUri!.Host,JObject.Parse(clear.Split("-36cd479b6b5-")[1])));
            return new(HttpStatusCode.OK){Content=new StringContent(Response)};
        }
    }
}
