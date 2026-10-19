"""Create deterministic local recognition cases from authorized 8 kHz mono s16le clips.

Example: --pcm 3399839173=artifacts/recognition/test-audio.s16le
No audio or credentials are embedded in source control.
"""
import argparse
import json
import math
import random
import struct
from pathlib import Path


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--pcm', action='append', required=True, help='source song ID=PCM path; at least 15 seconds')
    parser.add_argument('--out', default='artifacts/recognition')
    args = parser.parse_args()
    root = Path(args.out).resolve()
    root.mkdir(parents=True, exist_ok=True)
    manifest = []

    def add(name, song, values):
        pcm = root / (name + '.pcm')
        pcm.write_bytes(struct.pack('<%dh' % len(values), *[max(-32768, min(32767, round(x))) for x in values]))
        manifest.append(dict(name=name, songId=song, pcm=str(pcm), native=str(root / (name + '.native.json'))))

    for source in args.pcm:
        song, filename = source.split('=', 1)
        if not song.isdigit():
            raise ValueError('A numeric source song ID is required')
        data = Path(filename).read_bytes()[:240000]
        if len(data) != 240000:
            raise ValueError('At least 15 seconds of 8 kHz mono signed PCM16 are required')
        samples = struct.unpack('<120000h', data)
        rms = math.sqrt(sum(x * x for x in samples) / len(samples))
        rng = random.Random(20261019)
        variants = {
            'clean': samples,
            'quiet-40db': [x * .01 for x in samples],
            'noise-10db': [x + rng.gauss(0, rms / math.sqrt(10)) for x in samples],
            'noise-0db': [x + rng.gauss(0, rms) for x in samples],
            'leading-silence-6s': [0] * 48000 + list(samples[:72000]),
            'clipped': [max(-1400, min(1400, x)) for x in samples],
        }
        for variant, values in variants.items():
            add(song + '-' + variant, song, values)
    rng = random.Random(42)
    add('negative-white-noise', '', [rng.gauss(0, 3000) for _ in range(120000)])
    add('negative-tone', '', [8000 * math.sin(i * 2 * math.pi * 1000 / 8000) for i in range(120000)])
    (root / 'benchmark-complete-manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    print('Prepared', len(manifest), 'local cases.')


if __name__ == '__main__':
    main()
