import pathlib
root = pathlib.Path('.')
needle = 'PageSize'
for path in root.rglob('*.cs'):
    try:
        text = path.read_text(encoding='utf-8')
    except Exception:
        continue
    if needle in text:
        print(path)

