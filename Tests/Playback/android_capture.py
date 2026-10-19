"""Observe the running official app; no APK parsing and no credential output."""
import argparse, json, sys, time
from pathlib import Path
import frida

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'Tests/Runtime'))
from frida_support import add_runtime_arguments, compile_java

def main():
    p = argparse.ArgumentParser()
    p.add_argument('--pid', type=int, required=True)
    p.add_argument('--seconds', type=int, default=600)
    add_runtime_arguments(p)
    a = p.parse_args()
    source = compile_java(Path(__file__).with_suffix('.js').read_text(encoding='utf-8'), a)
    out = ROOT / 'artifacts/playback-protocol/android-events.jsonl'
    out.parent.mkdir(parents=True, exist_ok=True)
    session = frida.get_device_manager().add_remote_device(a.remote).attach(a.pid)
    try:
        with out.open('a', encoding='utf-8') as f:
            def receive(m, data):
                value = m.get('payload', {'kind':'error', 'description':m.get('description')})
                f.write(json.dumps(value, ensure_ascii=False) + '\n'); f.flush()
                print(json.dumps({k:v for k,v in value.items() if k in ('kind','path','name','error')}, ensure_ascii=False), flush=True)
            script = session.create_script(source)
            script.on('message', receive); script.load()
            deadline = time.monotonic() + a.seconds
            while time.monotonic() < deadline: time.sleep(.25)
    finally: session.detach()

if __name__ == '__main__': main()
