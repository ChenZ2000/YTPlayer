function clean(v,k='') {
 if(/cookie|token|csrf|header|password|userid|nickname|session/i.test(k))return '<redacted>';
 if(v&&typeof v==='object') {const o=Array.isArray(v)?[]:{}; for(const n in v)o[n]=clean(v[n],n);return o;}
 if(typeof v==='string') {if(v.startsWith('{')||v.startsWith('['))try{return clean(JSON.parse(v));}catch(_){} if(/^https?:/.test(v))return v.split('?')[0];}
 return v;
}
function emit(v){if(v.path&&!v.path.startsWith('/')&&!/music\.163\.com\/(?:e?api|weapi)\/|music\.126\.net\//.test(v.path))return;v.time=Date.now();v.pid=Process.id;send(v);}
function hook(name,fn){try{fn();emit({kind:'hook',name});}catch(e){emit({kind:'hook-error',name,error:String(e)});}}
Java.perform(()=>{
 const S=Java.use('java.lang.String');
 hook('gzip-log-buffer',()=>{
  const C=Java.use('java.util.zip.GZIPOutputStream'),ov=C.write.overload('[B','int','int');
  ov.implementation=function(b,off,len){
   if(len>20&&len<262144)try{
    const s=String(S.$new(b,off,len,'UTF-8'));
    if(/_pld|_plv|playend/.test(s))
     emit({kind:'log-buffer',body:clean(JSON.parse(s))});
   }catch(_){}
   return ov.call(this,b,off,len);
  };
 });
 hook('json-log',()=>{
  const C=Java.use('org.json.JSONObject'),ov=C.toString.overload();
  ov.implementation=function(){const r=ov.call(this),s=String(r);if(s.length<65536&&/_pld|_plv|playend/.test(s))try{emit({kind:'log-json',body:clean(JSON.parse(s))});}catch(_){}return r;};
 });
 hook('gateway',()=>{
  const C=Java.use('com.netease.cloudmusic.utils.NeteaseMusicUtils');
  for(const name of ['serialdata','serialdatanew']) for(const ov of C[name].overloads) {
   const types=ov.argumentTypes.map(x=>x.className);
   ov.implementation=function(...args){
    let path='',body=null;
    for(let i=0;i<args.length;i++)if(types[i]==='java.lang.String'){
     const s=String(args[i]);if(s.startsWith('/'))path=s;else try{body=JSON.parse(s);}catch(_){}
    }
    if(/feedback|weblog|clientlog|player\/url|play\/state/.test(path))emit({kind:'gateway',path,body:clean(body)});
    return ov.call(this,...args);
   };
  }
 });
 hook('cronet',()=>{
  const C=Java.use('org.chromium.net.impl.CronetUrlRequest'),ov=C.start.overload();
  ov.implementation=function(){
   try{
    const field=n=>{const f=this.getClass().getDeclaredField(n);f.setAccessible(true);return f.get(this);};
    const path=String(field('mInitialUrl')).split('?')[0], headers={};
    const list=Java.cast(field('mRequestHeaders'),Java.use('java.util.List'));
    for(let i=0;i<list.size();i++){
     const item=list.get(i),pair=String(item.getClass().getName())==='android.util.Pair';
     const p=Java.cast(item,Java.use(pair?'android.util.Pair':'java.util.Map$Entry'));
     const name=String(pair?p.first.value:p.getKey()).toLowerCase();
     if(['range','user-agent','content-type','accept-encoding'].includes(name))headers[name]=String(pair?p.second.value:p.getValue());
    }
    emit({kind:'wire',path,headers});
   }catch(e){emit({kind:'capture-error',error:String(e)});}
   return ov.call(this);
  };
 });
 hook('okhttp',()=>{
  const C=Java.use('okhttp3.internal.http.BridgeInterceptor'),ov=C.intercept.overload('okhttp3.Interceptor$Chain');
  ov.implementation=function(chain){
   const r=chain.request(),path=String(r.url()).split('?')[0];
   emit({kind:'wire',path,method:String(r.method()),range:String(r.header('Range'))});
   const result=ov.call(this,chain);
   emit({kind:'wire-response',path,status:result.code(),range:String(result.header('Content-Range')),length:String(result.header('Content-Length'))});
   return result;
  };
 });
 hook('media-seek',()=>{
  const C=Java.use('android.media.MediaPlayer');
  for(const ov of C.seekTo.overloads)ov.implementation=function(...args){emit({kind:'seek',position:String(args[0])});return ov.call(this,...args);};
 });
 emit({kind:'ready'});
});
