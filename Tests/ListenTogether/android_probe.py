"""Dynamic Java/native gateway capture for the existing test emulator. No APK parsing."""
import argparse, hashlib, json, re, subprocess, sys, time, urllib.request
from pathlib import Path
import frida
from urllib.parse import unquote

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'Tests/Runtime'))
from frida_support import add_runtime_arguments, compile_java
OUT = ROOT / 'artifacts' / 'listen-together'

def adb(options, *args):
    return subprocess.check_output([options.adb, '-s', options.serial, *args], timeout=30).decode('utf-8', 'replace').strip()

def clean(value, key=''):
    if re.search(r'cookie|token|csrf|header|encrypt|password', key, re.I): return '<redacted>'
    if re.search(r'^(userId|creatorId|roomId|inviterId|chatRoomId|agoraChannelId|sessionId|nickname|avatarUrl)$',key,re.I):
        return 'id:' + hashlib.sha256(str(value).encode()).hexdigest()[:8]
    if isinstance(value, dict): return {k:clean(v,k) for k,v in value.items()}
    if isinstance(value,list): return [clean(v) for v in value]
    if isinstance(value,str):
        if value.startswith(('{','[')):
            try: return clean(json.loads(value))
            except (ValueError,TypeError): pass
        if value.startswith(('https://','http://')): return value.split('?',1)[0]
    return value

def main():
    p=argparse.ArgumentParser();p.add_argument('action',choices=['import','capture']);p.add_argument('--account');p.add_argument('--seconds',type=int,default=300)
    p.add_argument('--adb', default='adb')
    p.add_argument('--serial', default='emulator-5554')
    p.add_argument('--session-script', help='Optional version-specific JS exporting importsession; required for import')
    add_runtime_arguments(p)
    args=p.parse_args();OUT.mkdir(parents=True,exist_ok=True)
    if args.action == 'import' and not (args.account and args.session_script):
        p.error('import requires --account and --session-script; alternatively log in on the emulator')
    device=frida.get_device_manager().add_remote_device(args.remote)
    source=Path(args.session_script).read_text(encoding='utf-8-sig') if args.action == 'import' else ''
    source += r'''
rpc.exports.startcapture=function(){Java.perform(()=>{
 const R=Java.use('org.chromium.net.impl.CronetUrlRequest');
 const start=R.start.overload();start.implementation=function(){
  try{
   const field=n=>{const f=this.getClass().getDeclaredField(n);f.setAccessible(true);return f.get(this);};
   const url=String(field('mInitialUrl'));
   if(url.includes('/listen/together/')){
    const headers={};const list=Java.cast(field('mRequestHeaders'),Java.use('java.util.List'));
    for(let i=0;i<list.size();i++){
     const item=list.get(i);if(String(item.getClass().getName())==='android.util.Pair'){
      const p=Java.cast(item,Java.use('android.util.Pair'));headers[String(p.first.value).toLowerCase()]=String(p.second.value);
     }else{const p=Java.cast(item,Java.use('java.util.Map$Entry'));headers[String(p.getKey()).toLowerCase()]=String(p.getValue());}
    }
    send({kind:'wire-request',path:url.split('?')[0],headers,time:Date.now()});
   }
  }catch(e){send({kind:'wire-hook-error',message:String(e)});}
  return start.call(this);
 };
 const C=Java.use('com.netease.cloudmusic.utils.NeteaseMusicUtils');
 for(const name of ['serialdata','serialdatanew','deserialdata','deserialdata2','deserialdatanew','deserialdata2new']){
  if(!C[name])continue;
  for(const ov of C[name].overloads){
   const types=ov.argumentTypes.map(t=>t.className);
   ov.implementation=function(...args){
    let path=null,body=null;
    if(name.startsWith('serial'))for(let i=0;i<args.length;i++)if(types[i]==='java.lang.String'){
      const s=String(args[i]);if(s.startsWith('/'))path=s;else try{body=JSON.parse(s);}catch(_){}
    }
    if(path&&path.includes('/listen/together/'))send({kind:'request',path,body,time:Date.now()});
    const result=ov.call(this,...args);
    if(name.startsWith('deserial'))for(let i=0;i<args.length;i++)if(types[i]==='[B'){
      try{
       const n=ov.returnType.className==='int'?Number(result):args[i].length;
       if(n>0&&n<=args[i].length){
        const text=String(Java.use('java.lang.String').$new(args[i],0,n,'UTF-8'));
        const data=JSON.parse(text);const d=data.data||{};
        if(data.code!==200||d.roomInfo||d.playCommand||d.playlist||d.timeSpan||d.joinable!==undefined||d.success!==undefined)
          send({kind:'response',body:data,time:Date.now()});
       }
      }catch(_){}
    }
    return result;
   };
  }
 }
 send({kind:'ready'});
});};
'''
    bundle=compile_java(source,args)
    session=device.attach(int(adb(args,'shell','pidof','com.netease.cloudmusic')))
    try:
        script=session.create_script(bundle); script.load()
        if args.action=='import':
            saved=json.loads(Path(args.account).read_text(encoding='utf-8-sig'))
            cookies=dict(v.strip().split('=',1) for v in saved['Cookie'].split(';') if '=' in v)
            request=urllib.request.Request('https://music.163.com/api/nuser/account/get',headers={'Cookie':'; '.join(k+'='+v for k,v in cookies.items()),'User-Agent':'Mozilla/5.0','Referer':'https://music.163.com/'})
            payload=json.load(urllib.request.urlopen(request,timeout=20))
            assert payload.get('code')==200 and str(payload['account']['id'])==str(saved['UserId']), 'Profile validation failed'
            payload['cookies']=cookies
            result=script.exports_sync.importsession(payload)
            if result.get('error'): raise RuntimeError(result['error'])
            print(json.dumps({k:v for k,v in result.items() if k!='cookiesImported'}))
        else:
            log=(OUT/'android-events.jsonl').open('a',encoding='utf-8')
            expected = json.loads(Path(args.account).read_text(encoding='utf-8-sig')) if args.account else None
            def receive(message,data):
                if message.get('type')!='send':
                    print('FRIDA_ERROR',message.get('description','')); return
                raw=message['payload']
                value=clean(raw)
                if expected and raw.get('kind')=='request':
                    header=raw.get('body',{}).get('header',{})
                    if isinstance(header,str): header=json.loads(header)
                    expected_cookies=dict(v.strip().split('=',1) for v in expected['Cookie'].split(';') if '=' in v)
                    value['sessionMatches']=header.get('MUSIC_U')==expected_cookies.get('MUSIC_U')
                    value['client']={k:header.get(k) for k in ['os','appver','versioncode']}
                if expected and raw.get('kind')=='wire-request':
                    expected_cookies=dict(v.strip().split('=',1) for v in expected['Cookie'].split(';') if '=' in v)
                    headers=raw.get('headers',{})
                    cookies=dict(v.strip().split('=',1) for v in headers.get('cookie','').split(';') if '=' in v)
                    value['sessionMatches']=cookies.get('MUSIC_U')==expected_cookies.get('MUSIC_U')
                    value['decodedSessionMatches']=unquote(cookies.get('MUSIC_U',''))==unquote(expected_cookies.get('MUSIC_U',''))
                    value['cookieNames']=list(cookies)
                    value['userAgent']=headers.get('user-agent')
                log.write(json.dumps(value,ensure_ascii=False)+'\n');log.flush()
                print(json.dumps({k:value[k] for k in ['kind','path'] if k in value}),flush=True)
            script.on('message',receive); script.exports_sync.startcapture()
            print('CAPTURE_READY',flush=True)
            until=time.monotonic()+args.seconds
            while time.monotonic()<until:time.sleep(.25)
            log.close()
    finally:
        if 'log' in locals(): log.close()
        session.detach()

if __name__=='__main__':main()
