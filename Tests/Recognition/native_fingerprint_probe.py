"""Compare known PCM through the running Android client's own JNI extractor.

No APK/DEX parsing. Supply a PID from adb; output contains fingerprints, never account data.
"""
import argparse
import base64
import json
import struct
import sys
from pathlib import Path
import frida

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'Runtime'))
from frida_support import add_runtime_arguments, compile_java


def main():
    p = argparse.ArgumentParser()
    p.add_argument('--pid', type=int, required=True)
    p.add_argument('--pcm')
    p.add_argument('--out')
    p.add_argument('--manifest')
    add_runtime_arguments(p)
    args = p.parse_args()
    if not args.manifest and not (args.pcm and args.out):
        p.error('Supply --manifest, or both --pcm and --out')
    source = r'''
rpc.exports.extract = function(samples) {
 return new Promise((resolve,reject)=>Java.perform(()=>{
  let obj,handle;
  try {
   const C=Java.use('com.netease.music.jni.FPExtractorHyaiJni');
   obj=C.$new();handle=obj.native_init();
   const results=[];
   for(let offset=0;offset<samples.length;offset+=24000){
    obj.native_addPCM(handle,Java.array('short',samples.slice(offset,offset+24000)));
    const bytes=obj.native_extract_query_fp(handle);
    results.push({duration:obj.native_duration_by_now(handle),
      fingerprint:String(Java.use('android.util.Base64').encodeToString(bytes,2))});
   }
   resolve(results);
  } catch(e){reject(String(e));}
  finally { if(obj){if(handle)obj.native_release(handle);obj.$dispose();} }
 }));
};
'''
    source = compile_java(source, args)
    device = frida.get_device_manager().add_remote_device(args.remote)
    session = device.attach(args.pid)
    try:
        script = session.create_script(source)
        script.load()
        cases = json.loads(Path(args.manifest).read_text(encoding='utf-8')) if args.manifest else [dict(name='sample', pcm=args.pcm, native=args.out)]
        for case in cases:
            data = Path(case['pcm']).read_bytes()
            samples = struct.unpack('<%dh' % (len(data) // 2), data)
            results = script.exports_sync.extract(samples)
            Path(case['native']).write_text(json.dumps(results, indent=2), encoding='utf-8')
            print(json.dumps(dict(case=case['name'], durations=[r['duration'] for r in results],
                bytes=[len(base64.b64decode(r['fingerprint'])) for r in results])), flush=True)
    finally:
        session.detach()


if __name__ == '__main__':
    main()
