import re
from pathlib import Path
lines = Path('MainForm.cs').read_text(encoding='utf-8').splitlines()
method_re = re.compile(r'^\s*(public|private|internal|protected)\s+(?:static\s+|async\s+|sealed\s+|override\s+|virtual\s+|extern\s+|unsafe\s+|new\s+|partial\s+)*(?:[\w<>\[\],\s]+)\s+([\w`]+)\s*\(')
linecount = len(lines)
i = 0
methods = []
while i < linecount:
    line = lines[i]
    m = method_re.match(line)
    if m:
        idx_paren = line.find('(')
        idx_eq = line.find('=')
        if idx_paren != -1 and (idx_eq == -1 or idx_paren < idx_eq):
            name = m.group(2)
            start_line = i + 1
            brace_balance = 0
            started = False
            j = i
            while j < linecount:
                l = lines[j]
                for ch in l:
                    if ch == '{':
                        brace_balance += 1
                        started = True
                    elif ch == '}':
                        brace_balance -= 1
                j += 1
                if started and brace_balance == 0:
                    end_line = j
                    methods.append((name, start_line, end_line))
                    break
            i = j
            continue
    i += 1
for name,start,end in methods:
    if 2520 <= start <= 2660:
        print(f"{name} {start} {end}")
