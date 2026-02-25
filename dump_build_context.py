import pathlib
path = pathlib.Path('Forms/MainForm/MainForm.HomeContent.cs')
text = path.read_text(encoding='utf-8')
needle = 'BuildHomeCategoryItem'
idx = text.find(needle)
print('first idx', idx)
print(text[idx-200:idx+400])

