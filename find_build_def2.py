import pathlib
path = pathlib.Path('Forms/MainForm/MainForm.HomeContent.cs')
text = path.read_text(encoding='utf-8')
needle = 'private ListItemInfo BuildHomeCategoryItem'
idx = text.find(needle)
print(idx)
if idx != -1:
    print(text[idx-200:idx+400])

