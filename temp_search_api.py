import os
import itertools
root = os.path.abspath(os.path.join('..','api-enhanced'))
needle = '/api/discovery/newAlbum'
for base, _, files in os.walk(root):
    for name in files:
        path = os.path.join(base, name)
        try:
            with open(path, 'r', encoding='utf-8') as fh:
                for idx, line in enumerate(fh, 1):
                    if needle in line:
                        print(f"{os.path.relpath(path, root)}:{idx}:{line.strip()}")
        except Exception:
            pass
