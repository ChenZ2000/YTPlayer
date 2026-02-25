import pathlib
text = pathlib.Path('Forms/MainForm/MainForm.HomeContent.cs').read_text(encoding='utf-8')
needle = 'BuildHomeCategoryItem('
start = 0
count = 0
while True:
    idx = text.find(needle, start)
    if idx == -1:
        break
    print('occ', count, 'idx', idx)
    snippet = text[idx:idx+200]
    snippet = snippet.replace('\n','\\n')
    print(snippet[:200])
    start = idx + len(needle)
    count += 1
    if count >= 25:
        break

