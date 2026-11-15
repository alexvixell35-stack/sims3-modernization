#!/usr/bin/env python3
"""Coverage map of EA's managed gameplay code (docs/586).

One row per top-level type (one decompiled .cs file) in Sims3GameplaySystems,
Sims3GameplayObjects and Sims3StoreObjects (EP/SP code ships inside these DLLs).

  methods      method count from the call graph (nested types folded in)
  swept-by     scanners with hits on the type (every scanner ran on every type;
               "none" = scanned, no hit): semgrep, resharper, infer, lifecycle,
               foreach-mutate, random-empty
  hits         weighted bug-signal hits (noise rules dropped)
  hand-read    yes when a doc names the type as code (`Type`, Type.Member,
               Type::, Type.cs) or ts3fixes patches it
  read-in      which docs / fix sources name it
  reach        0-3 gameplay reach (daily-life systems players hit often)
  rank         hits * (1 + reach), uncovered types only

Usage: python3 -I tools/static/function-coverage.py <repo> <analysis-dir> <out.csv>
           [--docs DIR ...] [--fixsrc DIR ...]
Inputs are local only (game-copy/ is git-ignored); the CSV holds type names
and counts, no EA code.
"""
import csv, json, os, re, sys
from collections import defaultdict

ASMS = ["Sims3GameplaySystems", "Sims3GameplayObjects", "Sims3StoreObjects"]

# Bug-signal rules only; style/complexity rules are noise for this question.
RESHARPER_RULES = {
    "ConditionIsAlwaysTrueOrFalse": 1, "HeuristicUnreachableCode": 1,
    "PossibleUnintendedReferenceComparison": 2, "ExpressionIsAlwaysNull": 2,
    "FunctionNeverReturns": 3, "EqualExpressionComparison": 3,
    "UnassignedField.Compiler": 1, "CollectionNeverQueried.Local": 1,
    "PossibleInvalidCastExceptionInForeachLoop": 1, "UnassignedReadonlyField": 1,
    "CollectionNeverUpdated.Local": 1, "FunctionRecursiveOnAllPaths": 3,
    "PossibleLossOfFraction": 1, "AccessToModifiedClosure": 1,
    "PossibleNullReferenceException": 0.25,
}
SEMGREP_WEIGHT = {"foreach-returns-first": 0.5, "self-compare": 1}  # default 2

REACH = [  # (regex on namespace/type, score); first match wins
    (r"Socializing|SocialInteraction|Relationship|Gossip|Romance|Flirt|Conversation", 3),
    (r"Autonomy|Motive|ActorSystems(\.|$)|Moodlet|Buff|InteractionQueue|Sims3\.Gameplay\.Interactions", 3),
    (r"Pregnan|Children|Toddler|Baby|Family|Genealogy|Household|AgeUp|Aging|Wedding|Marriage", 3),
    (r"Pets|Horse|Dog|Cat\b|MinorPet|PetPool", 3),
    (r"Occult|Vampire|Werewolf|Fairy|Witch|Ghost|Zombie|PlantSim|Genie|Mermaid|ImaginaryFriend|Spell|Supernatural|Moon", 3),
    (r"EventSystem|Situation|Party|Holiday|Season|Celebration|Opportunit|Dreams|Wish", 2),
    (r"Plumbing|Appliance|Food|Cooking|Fridge|Stove|Bed|Seating|Sofa|Chair|Electronics|TV|Computer|Phone|Shower|Toilet|Bathtub|Sink|Entertainment|Mailbox|Door|Stereo|Bookshelf|Easel", 2),
    (r"Career|Academic|School|Skill|Service|Bill|Restaurant|RabbitHole|Lot|Route|Vehicle|Car", 2),
    (r"Store|Tutorial|Debug|Online|CAS|UI|Cheat|Photograph|Challenge", 0),
]


def load_types(dec):
    types = {}
    for asm in ASMS:
        root = os.path.join(dec, asm)
        if not os.path.isdir(root):
            continue
        for ns in sorted(os.listdir(root)):
            d = os.path.join(root, ns)
            if not os.path.isdir(d) or ns == "Properties":
                continue
            for f in sorted(os.listdir(d)):
                if f.endswith(".cs"):
                    name = f[:-3]
                    types[f"{asm}/{ns}/{f}"] = {"asm": asm, "ns": ns, "name": name,
                                                "fq": f"{ns}.{name}"}
    return types


def method_counts(path):
    counts = defaultdict(int)
    with open(path, newline="") as fh:
        for row in csv.reader(fh):
            if len(row) < 2 or "::" not in row[0]:
                continue
            owner = row[0].split("::", 1)[0].split("/", 1)[0]
            owner = re.sub(r"`\d+$", "", owner)
            counts[owner] += 1
    return counts


def add(hits, key, scanner, w):
    if key in hits:
        hits[key]["w"] += w
        hits[key]["by"][scanner] += 1


def main():
    args = sys.argv[1:]
    repo, ana, out = args[0], args[1], args[2]
    docs, fixsrc, i = [], [], 3
    while i < len(args):
        if args[i] == "--docs":
            docs.append(args[i + 1]); i += 2
        elif args[i] == "--fixsrc":
            fixsrc.append(args[i + 1]); i += 2
        else:
            sys.exit(f"unknown arg {args[i]}")
    dec = os.path.join(repo, "game-copy", "decompiled")
    types = load_types(dec)
    hits = {k: {"w": 0.0, "by": defaultdict(int)} for k in types}

    # semgrep / grep shapes (gameplay-scan.sh)
    p = os.path.join(ana, "gameplay-scan", "ranked.tsv")
    if os.path.exists(p):
        for line in open(p, errors="replace"):
            f = line.rstrip("\n").split("\t")
            if len(f) >= 3:
                add(hits, f[2], "semgrep", SEMGREP_WEIGHT.get(f[1], 2))
    # ReSharper InspectCode (resharper-scan.sh)
    p = os.path.join(ana, "roslyn", "resharper-gameplay.tsv")
    if os.path.exists(p):
        for line in open(p, errors="replace"):
            f = line.split("\t")
            if len(f) >= 2 and f[0] in RESHARPER_RULES:
                add(hits, f[1], "resharper", RESHARPER_RULES[f[0]])
    # InferSharp null/resource
    for sub, asm in (("sims3gameplaysystems", "Sims3GameplaySystems"),
                     ("sims3gameplayobjects", "Sims3GameplayObjects"),
                     ("store", "Sims3StoreObjects")):
        p = os.path.join(ana, "infersharp", sub, "report.txt")
        if os.path.exists(p):
            for m in re.finditer(r"^(\S+\.cs):\d+: error:", open(p, errors="replace").read(), re.M):
                add(hits, f"{asm}/{m.group(1)}", "infer", 1)
    # managed lifecycle (acquire without release per type)
    for kind in ("alarm", "listener", "uselist"):
        p = os.path.join(ana, "managed-lifecycle", f"{kind}.tsv")
        if os.path.exists(p):
            for line in list(open(p))[1:]:
                f = line.rstrip("\n").split("\t")
                if len(f) >= 7:
                    add(hits, f[6], "lifecycle", min(float(f[0]) / 5.0, 6))
    for name, scanner in (("foreach-mutate.tsv", "foreach-mutate"), ("random-empty.tsv", "random-empty")):
        p = os.path.join(ana, "managed-lifecycle", name)
        if os.path.exists(p):
            for line in open(p, errors="replace"):
                for k in re.findall(r"(Sims3\w+/[\w.]+/\w+\.cs)", line):
                    add(hits, k, scanner, 1)

    mc = method_counts(os.path.join(ana, "callgraph", "methods.csv"))

    # hand-read: code-style mentions in docs, or patched by ts3fixes
    text_by_src = {}
    seen = {}
    for d in docs:
        for root, _, files in os.walk(d):
            for f in files:
                if f.endswith(".md"):
                    rel = os.path.relpath(os.path.join(root, f), d)
                    if rel not in seen:  # first docs dir wins (newest checkout first)
                        seen[rel] = 1
                        text_by_src["docs/" + rel] = open(os.path.join(root, f), errors="replace").read()
    fix_text = {}
    for d in fixsrc:
        for root, _, files in os.walk(d):
            for f in files:
                if f.endswith(".cs") and f not in fix_text:
                    fix_text[f] = open(os.path.join(root, f), errors="replace").read()
    names = defaultdict(list)
    for k, t in types.items():
        names[t["name"]].append(k)
    word = re.compile(r"[A-Za-z_]\w+")
    mentioned = defaultdict(set)
    for src, txt in text_by_src.items():
        short = re.sub(r"^docs/(\d+)-.*", r"docs/\1", src)
        if not re.match(r"^docs/\d+$", short):  # results/data files: folder only (names can hold user ids)
            short = src.rsplit("/", 1)[0] if "/" in src[5:] else src
        for m in re.finditer(r"`([^`\n]{2,200})`|\b([A-Z]\w+)(?=\.\w|::|\.cs\b)", txt):
            span = m.group(1) or m.group(2)
            for w in word.findall(span):
                if w in names:
                    mentioned[w].add(short)
    fixed = defaultdict(set)
    for f, txt in fix_text.items():
        for w in set(re.findall(r"typeof\((?:[\w.]+\.)?(\w+)\)|\"(?:Sims3\.[\w.]+\.)?(\w+)\"", txt)):
            for x in w:
                if x in names:
                    fixed[x].add("ts3fixes/" + f[:-3])

    rows = []
    for k, t in types.items():
        h = hits[k]
        reach = 1
        probe = t["fq"]
        for rx, score in REACH:
            if re.search(rx, probe):
                reach = score
                break
        by = ";".join(f"{s}:{n}" for s, n in sorted(h["by"].items())) or "none"
        srcs = sorted(mentioned.get(t["name"], ())) + sorted(fixed.get(t["name"], ()))
        hand = "yes" if srcs else "no"
        rank = 0 if hand == "yes" else round(h["w"] * (1 + reach), 1)
        rows.append([t["fq"], t["asm"], mc.get(t["fq"], 0), by, hand,
                     " ".join(srcs[:6]) + (" +" if len(srcs) > 6 else ""),
                     round(h["w"], 1), reach, rank])
    rows.sort(key=lambda r: (-r[8], r[0]))
    with open(out, "w", newline="") as fh:
        w = csv.writer(fh)
        w.writerow(["class", "assembly", "methods", "swept-by", "hand-read", "read-in",
                    "hits", "reach", "rank"])
        w.writerows(rows)
    total = len(rows)
    read = sum(1 for r in rows if r[4] == "yes")
    hit = sum(1 for r in rows if r[3] != "none")
    print(json.dumps({"types": total, "hand_read": read, "with_hits": hit,
                      "methods": sum(r[2] for r in rows)}))


if __name__ == "__main__":
    main()
