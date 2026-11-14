"""Observe the official app's login boundaries; emit schema/codes, never credentials.

Does not navigate the UI, send an SMS, enter a code, or change a device proxy.
"""
import argparse
import json
import sys
import time
from pathlib import Path

import frida

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'Tests/Runtime'))
from frida_support import add_runtime_arguments, compile_java


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pid', type=int, required=True)
    parser.add_argument('--seconds', type=int, default=120)
    parser.add_argument('--out', type=Path, default=ROOT / 'artifacts/login/android-events.jsonl')
    add_runtime_arguments(parser)
    args = parser.parse_args()
    args.out.parent.mkdir(parents=True, exist_ok=True)
    source = compile_java(Path(__file__).with_suffix('.js').read_text(encoding='utf-8'), args)
    session = frida.get_device_manager().add_remote_device(args.remote).attach(args.pid)
    try:
        with args.out.open('w', encoding='utf-8') as output:
            def receive(message, data):
                record = message.get('payload', {'kind': 'hook-error', 'error': message.get('description')})
                output.write(json.dumps(record, ensure_ascii=False) + '\n')
                output.flush()
                print(json.dumps(record, ensure_ascii=False), flush=True)

            script = session.create_script(source)
            script.on('message', receive)
            script.load()
            time.sleep(1)
            native = script.exports_sync.vector()['params']
            vector = json.loads((ROOT / 'Tests/Auth/crypto-vector.json').read_text(encoding='utf-8-sig'))
            result = {'kind': 'native-vector', 'matchesUpstream': native.upper() == vector['eapi']['params'].upper()}
            output.write(json.dumps(result) + '\n')
            output.flush()
            print(json.dumps(result), flush=True)
            deadline = time.monotonic() + args.seconds
            while time.monotonic() < deadline:
                time.sleep(.25)
    finally:
        session.detach()


if __name__ == '__main__':
    main()
