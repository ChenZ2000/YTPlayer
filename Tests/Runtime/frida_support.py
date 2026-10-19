"""Compile Java hooks using an explicit bridge package, without a private bundle."""
import json
import os
import re
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def add_runtime_arguments(parser):
    parser.add_argument('--remote', default='127.0.0.1:27043', help='Forwarded Frida server address')
    parser.add_argument('--java-bridge', default=os.environ.get('FRIDA_JAVA_BRIDGE', str(
        ROOT / 'artifacts/frida/node_modules/frida-java-bridge/index.js')),
        help='frida-java-bridge/index.js (or set FRIDA_JAVA_BRIDGE)')


def compile_java(source, args):
    import frida
    bridge = Path(args.java_bridge).resolve(strict=True)
    # Accept a standalone hook or an existing session script with a Java import.
    source = re.sub(r"^\s*import\s+Java\s+from\s+['\"][^'\"]+['\"];?\s*$", '', source, flags=re.M)
    source = 'import Java from ' + json.dumps(bridge.as_posix()) + ';\n' + source
    output = ROOT / 'artifacts/frida'
    output.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='compile-', dir=output) as folder:
        entry = Path(folder) / 'hook.js'
        entry.write_text(source, encoding='utf-8')
        return frida.Compiler().build(str(entry), project_root=folder)
