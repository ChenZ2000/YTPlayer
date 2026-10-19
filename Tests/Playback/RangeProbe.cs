using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using YTPlayer.Core.Playback.Cache;
using YTPlayer.Core.Streaming;

internal static class RangeProbe
{
    [DllImport("bass.dll")] private static extern bool BASS_Init(int device,int freq,int flags,IntPtr win,IntPtr cls);
    [DllImport("bass.dll")] private static extern bool BASS_Free();
    [DllImport("bass.dll",CharSet=CharSet.Unicode)] private static extern int BASS_StreamCreateFile(bool mem,string path,long offset,long length,uint flags);
    [DllImport("bass.dll")] private static extern bool BASS_StreamFree(int stream);
    [DllImport("bass.dll")] private static extern long BASS_ChannelGetLength(int stream,int mode);
    [DllImport("bass.dll")] private static extern long BASS_ChannelGetPosition(int stream,int mode);
    [DllImport("bass.dll")] private static extern long BASS_ChannelSeconds2Bytes(int stream,double seconds);
    [DllImport("bass.dll")] private static extern double BASS_ChannelBytes2Seconds(int stream,long bytes);
    [DllImport("bass.dll")] private static extern bool BASS_ChannelSetPosition(int stream,long bytes,int mode);
    [DllImport("bass.dll")] private static extern int BASS_ChannelGetData(int stream,byte[] data,int length);
    [DllImport("bass.dll")] private static extern int BASS_ErrorGetCode();
    internal static async Task RunAsync(string metadata,string output)
    {
        var song=JObject.Parse(File.ReadAllText(metadata)); string url=song.Value<string>("url")!; long size=song.Value<long>("size");
        using var log=new RangeHandler(); using var http=new HttpClient(log){Timeout=TimeSpan.FromSeconds(8)};
        Program.Check(BASS_Init(0,44100,0,IntPtr.Zero,IntPtr.Zero),"decoder init");
        var captures=new List<(double Time,byte[] Pcm,JObject Result)>();
        try
        {
            for(int i=0;i<3;i++)
            {
                int before=log.Ranges.Count; long beforeBytes=log.Bytes;
                using var provider=new SparseRangeFile(http,url,size);
                int stream=await Task.Run(()=>provider.CreateStream());
                if(stream==0)throw new Exception("Sparse decoder create error "+BASS_ErrorGetCode());
                try
                {
                    double duration=BASS_ChannelBytes2Seconds(stream,BASS_ChannelGetLength(stream,0));
                    double target=duration*new[]{.2,.55,.85}[i];
                    long readyBefore=log.Bytes-beforeBytes;
                    var watch=Stopwatch.StartNew();
                    bool ok=await Task.Run(()=>BASS_ChannelSetPosition(stream,BASS_ChannelSeconds2Bytes(stream,target),0));
                    if(!ok)throw new Exception("Sparse seek error "+BASS_ErrorGetCode());
                    double actual=BASS_ChannelBytes2Seconds(stream,BASS_ChannelGetPosition(stream,0));
                    byte[] pcm=new byte[32768]; int n=await Task.Run(()=>BASS_ChannelGetData(stream,pcm,pcm.Length));
                    if(n<=0)throw new Exception("Sparse decode error "+BASS_ErrorGetCode());
                    Array.Resize(ref pcm,n); watch.Stop();
                    var result=new JObject{["target"]=target,["actual"]=actual,["milliseconds"]=watch.Elapsed.TotalMilliseconds,["fileBytes"]=size,["startupBytes"]=readyBefore,["totalFetchedBytes"]=log.Bytes-beforeBytes,["pcmBytes"]=n,["requests"]=JArray.FromObject(log.Ranges.Skip(before))};
                    Program.Check(log.Bytes-beforeBytes<size,"cold seek does not download whole file");
                    Program.Check(log.Bytes-beforeBytes>readyBefore,"uncached seek requires additional target bytes");
                    captures.Add((target,pcm,result)); Console.WriteLine(result.ToString());
                }
                finally{BASS_StreamFree(stream);}
            }
            // Fetch a full reference only AFTER all sparse/cold seeks have completed.
            string reference=Path.ChangeExtension(output,"reference.mp3");
            await File.WriteAllBytesAsync(reference,await http.GetByteArrayAsync(url));
            int full=BASS_StreamCreateFile(false,reference,0,0,0x80000000|0x200000);
            if(full==0)throw new Exception("Reference open failed");
            try
            {
                foreach(var c in captures)
                {
                    BASS_ChannelSetPosition(full,BASS_ChannelSeconds2Bytes(full,c.Time),0);
                    byte[] pcm=new byte[c.Pcm.Length]; int n=BASS_ChannelGetData(full,pcm,pcm.Length);
                    bool matches=n==pcm.Length&&pcm.SequenceEqual(c.Pcm);
                    c.Result["referencePcmIdentical"]=matches;
                    c.Result["pcmSha256"]=Convert.ToHexString(SHA256.HashData(c.Pcm));
                    Program.Check(matches,"sparse remote decode equals complete-file decode at target");
                }
            }finally{BASS_StreamFree(full);}
            await File.WriteAllTextAsync(output,JArray.FromObject(captures.Select(c=>c.Result)).ToString());
            Console.WriteLine("PASS: 3 cold Range seeks, PCM identical to full reference");
        }finally{BASS_Free();}
    }
    private sealed class SparseRangeFile:IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Close(IntPtr user);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate long Length(IntPtr user);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Read(IntPtr buffer,int size,IntPtr user);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] [return:MarshalAs(UnmanagedType.Bool)] private delegate bool Seek(long offset,IntPtr user);
        [StructLayout(LayoutKind.Sequential)] private struct Procs {public Close Close;public Length Length;public Read Read;public Seek Seek;}
        [DllImport("bass.dll")] private static extern int BASS_StreamCreateFileUser(int system,uint flags,ref Procs procs,IntPtr user);
        private Procs _procs; private readonly HttpClient _http; private readonly string _url; private readonly long _size;
        private long _position; private readonly Dictionary<long,byte[]> _pages=new();
        internal SparseRangeFile(HttpClient http,string url,long size)
        {
            _http=http;_url=url;_size=size;
            _procs=new(){Close=_=>{},Length=_=>size,Seek=(p,_)=>{if(p<0||p>size)return false;_position=p;return true;},Read=ReadBytes};
        }
        internal int CreateStream()=>BASS_StreamCreateFileUser(0,0x200000,ref _procs,IntPtr.Zero);
        private int ReadBytes(IntPtr buffer,int length,IntPtr user)
        {
            try
            {
                if(_position>=_size)return 0;
                const int pageSize=32768; long page=_position/pageSize;
                if(!_pages.TryGetValue(page,out var data))
                {
                    long begin=page*pageSize,end=Math.Min(_size-1,begin+pageSize-1);
                    using var request=new HttpRequestMessage(HttpMethod.Get,_url);
                    request.Headers.Range=new System.Net.Http.Headers.RangeHeaderValue(begin,end);
                    using var response=_http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
                    var range=response.Content.Headers.ContentRange;
                    if(response.StatusCode!=HttpStatusCode.PartialContent||range?.From!=begin||range.To!=end||range.Length!=_size)return -1;
                    data=response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                    if(data.Length!=end-begin+1)return -1;
                    _pages[page]=data;
                }
                int offset=(int)(_position%pageSize),take=Math.Min(length,data.Length-offset);
                Marshal.Copy(data,offset,buffer,take);_position+=take;return take;
            }catch{return -1;}
        }
        public void Dispose(){GC.KeepAlive(_procs);}
    }
    private sealed class RangeHandler:DelegatingHandler
    {
        internal readonly List<object> Ranges=new(); internal long Bytes;
        internal RangeHandler():base(new HttpClientHandler()){}
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            var response=await base.SendAsync(request,token);
            if(request.Headers.Range!=null)
            {
                if(response.StatusCode!=HttpStatusCode.PartialContent)throw new Exception("CDN did not honor Range");
                lock(Ranges){Ranges.Add(new{requested=request.Headers.Range.ToString(),received=response.Content.Headers.ContentRange?.ToString(),status=(int)response.StatusCode}); Bytes+=response.Content.Headers.ContentLength??0;}
            }
            return response;
        }
    }
}
