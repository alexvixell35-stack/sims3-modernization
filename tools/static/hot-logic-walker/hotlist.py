#!/usr/bin/env python3
"""Hot-method list for hot-logic-walker (docs/604 §1).

Usage: hotlist.py <out.tsv> [top-N=150] [callee-depth=1] [runs-dir=../ts3-runs] [phase-glob-regex=.]
Ranks EA methods by sampled sim-thread time (docs/data/compile-candidates.csv, docs/data/jit-hot.csv), then the
ts3fixes callcensus counts in <runs-dir>/phase-*/ts3native.log ("callcensus: <method> calls N"), then the Test methods
of the 40 interaction types with the most autonomy time (docs/data/usage-rank-1115.csv). Takes the top N, adds their
callees to the given depth from the managed call graph (game-copy/analysis/callgraph/index.json), and writes
"<Namespace.Type[.Nested]>\t<method>" per line. Output names EA methods: keep it in game-copy/ or a scratchpad.
"""
import csv,json,re,glob,collections,sys,os
R=os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)),'..','..','..'))+os.sep
score=collections.defaultdict(float)
for r in csv.DictReader(open(R+'docs/data/compile-candidates.csv')):
    a=r['assembly']
    if not re.search(r'Sims3|ScriptCore|SimIFace|UI',a): continue
    f=r['frame']
    if f.startswith(('Enumerator.','OnNativeEventDelegate')): continue
    score[f]+=float(r['ms_s'])
for r in csv.DictReader(open(R+'docs/data/jit-hot.csv')):
    if re.search(r'Sims3|ScriptCore|SimIFace|UI',r['assembly']) and not r['frame'].startswith(('Enumerator.','OnNativeEventDelegate')):
        score[r['frame']]=max(score[r['frame']],float(r['ms_s_mean']))
aut=[]
for r in csv.DictReader(open(R+'docs/data/usage-rank-1115.csv')):
    if float(r['autonomy_ms'] or 0)>0: aut.append((r['type'],float(r['autonomy_ms'])))
aut.sort(key=lambda x:-x[1])
cen=collections.Counter()
runs=sys.argv[4] if len(sys.argv)>4 else os.path.join(R,'..','ts3-runs')
pick=sys.argv[5] if len(sys.argv)>5 else '.'
for p in glob.glob(os.path.join(runs,'phase-*','ts3native.log')):
    if not re.search(pick,p): continue
    for l in open(p,errors='ignore'):
        m=re.search(r'callcensus: (\S+) calls (\d+)',l)
        if m: cen[m.group(1)]+=int(m.group(2))
d=json.load(open(R+'game-copy/analysis/callgraph/index.json'))
M=d['methods']
def short(k):  # 'A.B.C+D::M(args)' -> ('C+D' full, 'D', 'M')
    t,m=k.split('::',1); m=m.split('(')[0]; return t,t.split('.')[-1].split('+')[-1],m
bykey=collections.defaultdict(list)
for i,x in enumerate(M):
    t,s,m=short(x['k']); bykey[t+'.'+m].append(i); bykey[s+'.'+m].append(i)
    bykey[t.replace('+','.')+'.'+m].append(i)
hot=[];seen=set()
ranked=sorted(score.items(),key=lambda x:-x[1])+[(k,v/1e5) for k,v in cen.most_common()]
# autonomy-tested interaction types: their Definition.Test (called per object per autonomy pass)
for t,ms in aut[:40]:
    for i,x in enumerate(M):
        k=x['k']
        if k.startswith(t+'+') and '::Test(' in k and k.count('+')==1: ranked.append(('@'+str(i),ms))

for f,v in ranked:
    ids=[int(f[1:])] if f.startswith('@') else bykey.get(f) or bykey.get(f.split('`')[0]) or []
    for i in ids:
        if i not in seen: seen.add(i); hot.append((i,v,'hot'))
top=[i for i,_,_ in hot][:int(sys.argv[2]) if len(sys.argv)>2 else 150]
out=set(top); front=list(top)
for _ in range(int(sys.argv[3]) if len(sys.argv)>3 else 1):
    nxt=[]
    for i in front:
        c=M[i].get('c',[])
        for j in range(0,len(c),2):
            ci=c[j]
            if ci<len(M) and ci not in out: out.add(ci); nxt.append(ci)
    front=nxt
names=set()
for i in out:
    t,s,m=short(M[i]['k'])
    if not re.match(r'(Sims3|ScriptCore|UI)',t) or t.startswith('System'): continue
    names.add(t.replace('+','.')+'\t'+m)
open(sys.argv[1],'w').write('\n'.join(sorted(names))+'\n')
print('frames',len(score),'census',len(cen),'hot methods',len(top),'with callees',len(names))
