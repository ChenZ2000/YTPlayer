using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using YTPlayer.Core;
using YTPlayer.Core.Playback.Cache;
using YTPlayer.Models;

internal static class SeekTests
{
    internal static async Task RunAsync(string fixture)
    {
        byte[] bytes=await File.ReadAllBytesAsync(fixture);
        using var handler=new SlowRangeHandler(bytes);
        using var engine=new BassAudioEngine(); engine.SetVolume(0);
        var field=typeof(BassAudioEngine).GetField("_httpClient",BindingFlags.NonPublic|BindingFlags.Instance)!;
        ((HttpClient)field.GetValue(engine)!).Dispose(); field.SetValue(engine,new HttpClient(handler){Timeout=TimeSpan.FromSeconds(30)});
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var song=new SongInfo{Id="seek-fixture",Url="https://fixture.invalid/seek.mp3",Name="Synthetic seek fixture",Duration=360,Size=bytes.Length};
        Program.Check(await engine.PlayAsync(song,deadline.Token),"actual BASS/cache playback initializes with sparse slow server");
        engine.Pause();
        var cache=engine.CurrentCacheManager!;
        long target=engine.GetEstimatedFileOffset(180);
        Program.Check(!cache.IsChunkCached((int)(target/(256*1024))),"middle target not cached before seek");
        Program.Check(target < bytes.Length && engine.GetBytesFromSeconds(180)>bytes.Length,"compressed bytes differ from PCM bytes");
        handler.Allow(target);
        var watch=Stopwatch.StartNew();
        var seek=engine.SetPositionWithCacheWaitAsync(180,3000,deadline.Token,true);
        Program.Check(watch.ElapsedMilliseconds<200,"async seek returns control immediately");
        Program.Check(await seek.WaitAsync(TimeSpan.FromSeconds(5)),"uncached middle seek succeeds");
        Program.Check(Math.Abs(engine.GetPosition()-180)<.1,"BASS position reaches target");
        Program.Check(engine.IsPaused,"seek preserves paused state");
        Console.WriteLine($"cold seek to 180s: {watch.ElapsedMilliseconds}ms, cached fraction={cache.CacheFillFraction:F3}");

        using(var cancel=new CancellationTokenSource(100))
        {
            watch.Restart();
            Program.Check(!await engine.SetPositionWithCacheWaitAsync(110,10000,cancel.Token,true).WaitAsync(TimeSpan.FromSeconds(2)),"cancel slow missing chunk");
            Program.Check(watch.ElapsedMilliseconds<1500,"cancellation bounded");
        }
        watch.Restart();
        Program.Check(!await engine.SetPositionWithCacheWaitAsync(110,150,deadline.Token,true).WaitAsync(TimeSpan.FromSeconds(2)),"entire ensure/wait has deadline");
        Program.Check(watch.ElapsedMilliseconds<1500,"deadline covers initial download, not just readiness poll");
        using(var manager=new SeekManager(engine))
        {
            manager.SetCacheStream(cache);
            manager.RequestSeek(110); await Task.Delay(100);
            long last=engine.GetEstimatedFileOffset(220);handler.Allow(last);
            var completion=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            manager.SeekExecuted+=(_,e)=>{if(Math.Abs(e.TargetSeconds-220)<.1)completion.TrySetResult(e.Success);};
            manager.RequestSeek(220); manager.FinishSeek();
            Program.Check(await completion.Task.WaitAsync(TimeSpan.FromSeconds(8)),"latest pending drag survives FinishSeek and canceled predecessor");
            Program.Check(Math.Abs(engine.GetPosition()-220)<.1,"latest target wins");
            for(int i=0;i<50&&manager.IsSeeking;i++)await Task.Delay(20);
            Program.Check(!manager.IsSeeking,"paused forward seek monitor terminates");
            var failed=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            manager.SeekExecuted+=(_,e)=>{if(Math.Abs(e.TargetSeconds-115)<.1)failed.TrySetResult(e.Success);};
            manager.RequestSeek(115); await Task.Delay(100); manager.CancelPendingSeeks();
            Program.Check(!await failed.Task.WaitAsync(TimeSpan.FromSeconds(2)),"cancelled pending seek cannot publish success");
        }
        for(int i=0;i<30;i++)
        {
            double position=i%2==0?180:220;
            Program.Check(await engine.SetPositionWithCacheWaitAsync(position,1000,deadline.Token,true),"repeated seek works after cancellation");
        }
        using(var cancel=new CancellationTokenSource())
        {
            var pending=engine.SetPositionWithCacheWaitAsync(115,10000,cancel.Token,true);
            await Task.Delay(50); engine.Stop(); cancel.Cancel();
            Program.Check(!await pending.WaitAsync(TimeSpan.FromSeconds(2)),"stop invalidates pending old-stream seek");
        }
        Console.WriteLine("slow network, cancellation, deadline, paused state, newest drag and stop verified");
    }
    private sealed class SlowRangeHandler:HttpMessageHandler
    {
        private readonly byte[] _data; private readonly List<(long Start,long End)> _allowed=new();
        internal SlowRangeHandler(byte[] data){_data=data;}
        internal void Allow(long center){lock(_allowed)_allowed.Add((Math.Max(0,center-512*1024),center+1024*1024));}
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            if(request.Method==HttpMethod.Head){var h=new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(Array.Empty<byte>())};h.Content.Headers.ContentLength=_data.Length;h.Headers.AcceptRanges.Add("bytes");return h;}
            var range=request.Headers.Range?.Ranges.Single()??throw new Exception("Unexpected full download in range test");
            long from=range.From??0,to=range.To??_data.Length-1;
            bool Allowed(){lock(_allowed)return from<2*1024*1024||from>=_data.Length-2*1024*1024||_allowed.Any(x=>from>=x.Start&&from<=x.End);}
            while(!Allowed())await Task.Delay(25,token);
            await Task.Delay(75,token);
            var response=new HttpResponseMessage(HttpStatusCode.PartialContent){Content=new ByteArrayContent(_data[(int)from..((int)to+1)])};
            response.Content.Headers.ContentRange=new ContentRangeHeaderValue(from,to,_data.Length);return response;
        }
    }
}
