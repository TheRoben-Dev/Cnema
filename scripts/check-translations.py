#!/usr/bin/env python3
"""Lists UI strings that a translation is missing. Usage: scripts/check-translations.py [sv]
Finds T("..."), F("..."), P(n, "...", "..."), chip/choice labels, shortcut labels and svc:Tr.* in XAML.
A few non-text strings (units, file names) show up as false positives; ignore those."""
import re,glob,json,html,sys,os
import pathlib
root=str(pathlib.Path(__file__).resolve().parent.parent/'src'/'Cnema')
keys={}
def add(k,src):
    keys.setdefault(k,src)
for f in glob.glob(root+'/**/*.axaml',recursive=True):
    s=open(f).read()
    for m in re.finditer(r'svc:Tr\.\w+="([^"]*)"',s):
        add(html.unescape(m.group(1)),os.path.basename(f))
def cs_unescape(lit):
    return lit.encode().decode('unicode_escape').encode('latin1').decode('utf-8') if '\\' in lit else lit
lit=r'"((?:[^"\\]|\\.)*)"'
for f in glob.glob(root+'/**/*.cs',recursive=True):
    if '/obj/' in f or '/bin/' in f: continue
    s=open(f).read()
    for m in re.finditer(r'(?<![\w.])(?:Loc\.)?(?:T|F)\(\s*'+lit,s):
        add(cs_unescape(m.group(1)),os.path.basename(f))
    # T(cond ? "a" : "b"), F(cond ? "a" : "b", ...), P(n, "a", "b")
    for m in re.finditer(r'(?<![\w.])(?:Loc\.)?(?:T|F)\(([^;]*?\?\s*'+lit+r'\s*:\s*'+lit+')',s,re.S):
        add(cs_unescape(m.group(2)),os.path.basename(f)); add(cs_unescape(m.group(3)),os.path.basename(f))
    for m in re.finditer(r'(?<![\w.])P\([^,]+,\s*'+lit+r'\s*,\s*'+lit,s):
        add(cs_unescape(m.group(1)),os.path.basename(f)); add(cs_unescape(m.group(2)),os.path.basename(f))
    # ChipGroup labels/tips and Choice labels, ShortcutAction labels
    for m in re.finditer(r'\(\s*'+lit+r'\s*,\s*[\w.]+(?:\s*,\s*'+lit+r')?\s*\)',s):
        for g in (1,2):
            if m.group(g) and re.search('[A-Za-z]{2}',m.group(g)): add(cs_unescape(m.group(g)),os.path.basename(f))
    for m in re.finditer(r'new(?: Choice)?\(\s*'+lit+r'\s*,\s*-?\d+\)',s):
        add(cs_unescape(m.group(1)),os.path.basename(f))
    for m in re.finditer(r'new\("\w+", (?:Library|Player), '+lit,s): add(cs_unescape(m.group(1)),'Shortcuts')
    for m in re.finditer(r'\("General", '+lit,s): add(cs_unescape(m.group(1)),'Shortcuts')
add('General','Shortcuts'); add('Library','Shortcuts'); add('Player','Shortcuts')
for k in ['Recycle Bin','Trash','10 MB for Discord','50 MB for Discord','Vertical 9:16']: add(k,'misc')
keys={k:v for k,v in keys.items() if re.search('[A-Za-z]{2}',k) and k not in ('Cnema',)}

code=sys.argv[1] if len(sys.argv)>1 else 'sv'
have=json.load(open(f"{root}/Assets/Lang/{code}.json",encoding='utf-8'))
missing=[(v,k) for k,v in keys.items() if k not in have]
for v,k in sorted(missing): print(f"{v}: {k}")
print(f"{len(missing)} of {len(keys)} strings missing from {code}.json", file=sys.stderr)
