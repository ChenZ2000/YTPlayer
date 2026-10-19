using System.Diagnostics;
using Newtonsoft.Json.Linq;
using YTPlayer.Core;
using YTPlayer.Core.Playback;
using YTPlayer.Models;

internal static class LiveListen
{
    internal static async Task RunAsync(NeteaseApiClient client,string metadata,int seconds)
    {
        var data=JObject.Parse(File.ReadAllText(metadata));
        if(data["freeTrialInfo"]?.Type is not (null or JTokenType.Null))throw new Exception("Use a legitimately accessible full song for count validation");
        string id=data.Value<string>("id")!;
        var detail=(await client.GetSongDetailAsync(new[]{id})).Single();
        detail.Url=data.Value<string>("url")!; detail.Size=data.Value<long>("size");
        using var engine=new BassAudioEngine();
        var context=new PlaybackReportContext(id,detail.Name,detail.Artist,new("album",detail.AlbumId),detail.Duration,false,"song");
        engine.PositionChanged+=(_,p)=>context.Observe(p.TotalSeconds,engine.IsPlaying,Stopwatch.GetTimestamp());
        using var service=new PlaybackReportingService(client);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(seconds+40));
        if(!await engine.PlayAsync(detail,timeout.Token))throw new Exception("Playback failed");
        service.ReportSongStarted(context);
        Console.WriteLine("Actual playback started; seconds="+seconds);
        var watch=Stopwatch.StartNew();
        while(watch.Elapsed.TotalSeconds<seconds)
        {await Task.Delay(1000,timeout.Token); if((int)watch.Elapsed.TotalSeconds%20==0)Console.WriteLine($"position={engine.GetPosition():F1}, listened={context.PlayedSeconds:F1}");}
        engine.Stop(); service.ReportSongCompleted(context,PlaybackEndReason.Stopped);
        await Task.Delay(3000);
        Console.WriteLine($"Actual listened seconds={context.PlayedSeconds:F3}");
    }
}
