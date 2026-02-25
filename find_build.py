import pathlib
path = pathlib.Path('Forms/MainForm/MainForm.HomeContent.cs')
for i,line in enumerate(path.read_text(encoding='utf-8').splitlines(),1):
    if 'BuildHomeCategoryItem' in line and '(' in line:
        print(i, line.strip())

