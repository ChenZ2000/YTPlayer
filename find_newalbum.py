import pathlib
root = pathlib.Path('D:/GitHub/api-enhanced')
needle = 'newAlbum'
for path in root.rglob('*.*'):
    if path.is_file():
        try:
            for i,line in enumerate(path.read_text(encoding='utf-8').splitlines(),1):
                if needle in line:
                    print(f'{path.relative_to(root)}:{i}:{line.strip()}')
        except Exception:
            continue

