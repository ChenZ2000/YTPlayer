import os
needle='新碟上架'
for root, _, files in os.walk('.'):
    for name in files:
        if name.endswith(('.cs','.xaml','.json','.csproj','.txt','.md')):
            path=os.path.join(root,name)
            try:
                with open(path,'r',encoding='utf-8') as fh:
                    for idx,line in enumerate(fh,1):
                        if needle in line:
                            print(f"{path}:{idx}")
            except Exception:
                pass
