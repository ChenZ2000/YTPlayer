import pathlib
text = pathlib.Path('D:/GitHub/api-enhanced/server.js').read_text(encoding='utf-8')
pos = text.find('discovery')
print(pos)
if pos != -1:
    start = max(0, pos-200)
    end = min(len(text), pos+600)
    print(text[start:end])
