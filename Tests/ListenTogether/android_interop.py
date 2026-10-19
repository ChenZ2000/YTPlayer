"""Drive the production .NET session against the authorized Android test account.

Commands are local JSON lines in artifacts/listen-together/interop-commands.jsonl.
No credentials or invitation identifiers are printed.
"""
import argparse, json, subprocess, time
from pathlib import Path
from android_probe import ROOT, OUT, clean

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--account', required=True)
    parser.add_argument('--invite-file')
    parser.add_argument('--seconds', type=int, default=900)
    parser.add_argument('--adb', default='adb')
    parser.add_argument('--serial', default='emulator-5554')
    args = parser.parse_args()
    OUT.mkdir(parents=True, exist_ok=True)
    commands = OUT / 'interop-commands.jsonl'
    commands.write_text('', encoding='utf-8')
    executable = ROOT / 'Tests/ListenTogether/bin/x64/Debug/net10.0-windows/ListenTogether.Tests.exe'
    peer = subprocess.Popen([str(executable), '--worker', args.account], stdin=subprocess.PIPE,
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding='utf-8', cwd=ROOT)
    def call(op, **values):
        peer.stdin.write(json.dumps(dict(op=op, **values)) + '\n'); peer.stdin.flush()
        line = peer.stdout.readline()
        if not line: raise RuntimeError('Worker exited')
        result = json.loads(line)
        if not result.get('ok'): raise RuntimeError(op + ': ' + str(result.get('operation')) + ': ' + result.get('error', ''))
        return result.get('data', {})
    try:
        assert json.loads(peer.stdout.readline())['ok']
        assert not call('status').get('inRoom'), 'Account already in a room'
        if args.invite_file:
            room = call('start', invite=Path(args.invite_file).read_text(encoding='utf-8').strip())
        else:
            room = call('start')
            # Only open on the local test emulator; never send the invite to another person.
            subprocess.run([args.adb, '-s', args.serial, 'shell', 'am', 'start', '-a',
                            'android.intent.action.VIEW', '-d', "'" + room['invite'] + "'", '-p',
                            'com.netease.cloudmusic'], stdout=subprocess.DEVNULL, check=True)
        print('INTEROP_READY', flush=True)
        consumed = 0; previous = None; until = time.monotonic() + args.seconds
        while time.monotonic() < until:
            lines = commands.read_text(encoding='utf-8-sig').splitlines()
            stop = False
            for line in lines[consumed:]:
                if not line.strip(): continue
                request = json.loads(line)
                if request['op'] == 'exit': stop = True; break
                data = call(**request)
                print(json.dumps({'command': request['op'], 'data': clean(data)}, ensure_ascii=False), flush=True)
            consumed = len(lines)
            if stop: break
            call('tick')
            state = call('snapshot')
            changed = (state['Playback']['SongId'], state['Playback']['IsPlaying'], state['applied'])
            if changed != previous:
                print(json.dumps({'snapshot': clean(state)}, ensure_ascii=False), flush=True); previous = changed
            time.sleep(2)
    finally:
        if peer.poll() is None:
            peer.stdin.write('{"op":"exit"}\n'); peer.stdin.flush()
            try: peer.wait(timeout=20)
            except subprocess.TimeoutExpired: peer.kill(); peer.wait()
        print('INTEROP_CLOSED', flush=True)

if __name__ == '__main__': main()
