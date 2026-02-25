import os
needle='newAlbum'
for root, _, files in os.walk('.'):
    for name in files:
        path=os.path.join(root,name)
        try:
            with open(path,'r',encoding='utf-8') as fh:
                for idx,line in enumerate(fh,1):
                    if needle in line:
                        print(f"{path}:{idx}:{line.strip()}" )
        except Exception:
            pass
