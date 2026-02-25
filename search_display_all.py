import pathlib
root = pathlib.Path('.')
needle = 'DisplayAlbums'
for path in root.rglob('*.cs'):
    try:
        text = path.read_text(encoding='utf-8')
    except Exception:
        continue
    for i,line in enumerate(text.splitlines(),1):
        if needle in line:
            print(f'{path}:{i}:{line.strip()}')

