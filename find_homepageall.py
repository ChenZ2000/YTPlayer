import pathlib
needle = 'HomePageViewData'
root = pathlib.Path('.')
for path in root.rglob('*.cs'):
    try:
        text = path.read_text(encoding='utf-8')
    except Exception:
        continue
    if needle in text:
        print(path)

