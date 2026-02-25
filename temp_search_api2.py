import os
root = os.path.abspath('D:/GitHub/api-enhanced')
needle='album_newest'
for base, _, files in os.walk(root):
    for name in files:
        path = os.path.join(base,name)
        try:
            with open(path,'r',encoding='utf-8') as fh:
                for idx,line in enumerate(fh,1):
                    if needle in line:
                        print(f"{os.path.relpath(path, root)}:{idx}:{line.strip()}")
        except Exception:
            pass
