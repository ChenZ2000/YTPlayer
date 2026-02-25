import pathlib
needle = '/api/discovery/newAlbum'
root = pathlib.Path('D:/GitHub/api-enhanced')
for path in root.rglob('*.*'):
    if path.is_file():
        try:
            text = path.read_text(encoding='utf-8')
        except Exception:
            continue
        if needle in text:
            print(path.relative_to(root))

