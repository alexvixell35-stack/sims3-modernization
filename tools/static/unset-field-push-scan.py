#!/usr/bin/env python3
"""Interactions pushed without a field their Run() needs (docs/586).

Shape: an interaction class has a public/internal field F and its Run() starts
by bailing out when F is null (`if (F == null) return false;` within the first
few statements). Every site that creates that interaction
(`X.Singleton.CreateInstance(...)`) and does not assign `.F =` within the next
few lines pushes an interaction that exits at once (the action silently never
happens). First case: CatJungleGym's broadcaster pushes WatchCatJungleGym
without JungleGymRef.

Usage: python3 -I tools/static/unset-field-push-scan.py <decompiled-root>
Output (TSV): interaction  field  file:line of the create site
"""
import os, re, sys

root = sys.argv[1]
ASMS = ["Sims3GameplaySystems", "Sims3GameplayObjects", "Sims3StoreObjects"]
files = []
for a in ASMS:
    for d, _, fs in os.walk(os.path.join(root, a)):
        files += [os.path.join(d, f) for f in fs if f.endswith(".cs")]

cls_re = re.compile(r"^\s*(?:public|private|internal|protected)?\s*(?:sealed\s+|abstract\s+)*class\s+(\w+)\s*:", re.M)
field_re = re.compile(r"^\s*(?:public|internal)\s+(?!static|const|override|readonly)[\w.<>\[\], ]+\s+(\w+)\s*;", re.M)
run_re = re.compile(r"protected override bool Run\(\)\s*\{(.{0,400})", re.S)

def class_body(txt, i):
    """Text of the class whose header ends at i, up to its matching brace."""
    j = txt.find("{", i)
    depth, k = 0, j
    while k < len(txt):
        c = txt[k]
        if c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
            if depth == 0:
                return txt[j + 1:k]
        k += 1
    return txt[j + 1:]


def strip_nested(body):
    out, i = [], 0
    for m in re.finditer(r"\bclass\s+\w+[^{;]*\{", body):
        if m.start() < i:
            continue
        out.append(body[i:m.start()])
        inner = class_body(body, m.start())
        i = m.start() + body[m.start():].find("{") + len(inner) + 2
    out.append(body[i:])
    return "".join(out)


needs = {}  # class -> field
for f in files:
    txt = open(f, errors="replace").read()
    for cm in cls_re.finditer(txt):
        name = cm.group(1)
        body = class_body(txt, cm.end())
        # drop nested class bodies so fields/Run belong to this class only
        own = strip_nested(body)
        fields = set(field_re.findall(own))
        m = run_re.search(own)
        if not m or not fields:
            continue
        head = m.group(1)
        for fld in fields:
            if re.search(r"if \(" + re.escape(fld) + r" == null\)\s*\{\s*return false;", head):
                needs[name] = fld

for f in files:
    lines = open(f, errors="replace").read().split("\n")
    for i, l in enumerate(lines):
        for name, fld in needs.items():
            if name + ".Singleton.CreateInstance" in l or f"new {name}.Definition" in l:
                window = "\n".join(lines[i:i + 8])
                if "." + fld + " =" not in window and fld + " = " not in window:
                    print(f"{name}\t{fld}\t{os.path.relpath(f, root)}:{i + 1}")
