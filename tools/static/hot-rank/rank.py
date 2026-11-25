import json,re,collections,csv,bisect
S='/tmp/claude-1000/-home-deck/d5b7f581-35b9-45ca-8755-11e11015efee/scratchpad/hot/'
d=json.load(open('game-copy/analysis/callgraph/index.json'));M=d['methods'];K=d['kinds']
N=len(M)
def callees(i):
    c=M[i]['c']; return [(c[j],K[c[j+1]]) for j in range(0,len(c),2)]
def frame(k):
    t,m=k.split('::',1); m=m.split('(')[0]; return t.replace('+','.')+'.'+m
SCAN=re.compile(r'(::GetObjects|::GetObjectsInRadius|::GetObjectsOnLot|::GetSims\b|::GetAllActors|::GetActorsOfType|::FindObjects|::get_AllActors|::get_AllSimDescriptions|::get_AllLots|LotManager::get_Actors|::GetSimDescriptions|::GetObjectsOfType|::GetNearbySims|::GetSimsInRadius|::GetInRadiusSims|::GetLotsOfType|::GetSimsOnLot|::GetAllObjectsOnLot|Household::get_Sims|Household::get_AllHouseholds|::get_AllHumanSims|::GetObjectsInRoom|::get_SimDescriptions|Sims3.Gameplay.Queries::|Sims3.SimIFace.Sims3_Queries|::GetAllSims|::get_AllSims)')
REFL=re.compile(r'(System\.Reflection\.|System\.Type::(Get(Method|Field|Property|Members|CustomAttributes|Interfaces|Fields|Methods|Properties|Type\()|IsAssignableFrom|IsSubclassOf|InvokeMember|get_Name|get_FullName|IsInstanceOfType)|System\.Activator::|System\.Enum::(GetValues|GetNames|ToString|Parse|IsDefined|GetName)|System\.Attribute::)')
LINQ=re.compile(r'System\.Linq\.')
COPY=re.compile(r'(::ToArray\(|::ToList\(|System\.Collections\.Generic\.List`1::\.ctor\(System\.Collections\.Generic\.IEnumerable|::AddRange\()')
NEWCOLL=re.compile(r'System\.Collections\.Generic\.(List|Dictionary|HashSet|Queue)`\d::\.ctor')
STR=re.compile(r'System\.String::(Format|Concat|Join|Split|Replace|ToLower|ToUpper)')
TRANS=re.compile(r'(Sims3\.SimIFace\.Simulator::Sleep|Sims3\.SimIFace\.Simulator::Yield)')
own=[None]*N
for i,x in enumerate(M):
    f=collections.Counter()
    for t,k in callees(i):
        tk=M[t]['k']
        if SCAN.search(tk): f['scan']+=1
        if REFL.search(tk): f['refl']+=1
        if LINQ.search(tk): f['linq']+=1
        if COPY.search(tk): f['copy']+=1
        if k=='newobj' and NEWCOLL.search(tk): f['newcoll']+=1
        if STR.search(tk): f['str']+=1
    own[i]=f
# walker hits & HAA counts by method frame
walk=collections.Counter()
for l in open('game-copy/analysis/hotlogic/all.tsv'):
    p=l.rstrip('\n').split('\t')
    if p[0] in('NESTEDSCAN','REPEATCALL','INFLOOP'): walk[p[3]]+=1
rng=collections.defaultdict(list)
for l in open('game-copy/analysis/hotlogic/all.tsv.ranges'):
    p=l.rstrip('\n').split('\t'); rng[p[0]].append((int(p[1]),int(p[2]),p[3]))
for v in rng.values(): v.sort()
haa=collections.Counter()
for l in open('game-copy/analysis/roslyn/bug-rules-all3.tsv'):
    p=l.split('\t')
    if not p[1].startswith('HAA'): continue
    f=p[2]; ln=int(p[3]); L=rng.get(f)
    if not L: continue
    j=bisect.bisect_right(L,(ln,10**9,''))-1
    while j>=0 and L[j][1]<ln: j-=1
    if j>=0 and L[j][0]<=ln<=L[j][1]: haa[L[j][2]]+=1
# measured
meas=collections.defaultdict(float)
for r in csv.DictReader(open('docs/data/compile-candidates.csv')): meas[r['frame']]+=float(r['ms_s'])
# roots
roots={}
def addroot(pat,w,tag):
    for i,x in enumerate(M):
        if re.search(pat,x['k']): roots[i]=max(roots.get(i,(0,''))[0],w),tag
addroot(r'^Sims3\.Gameplay\.Actors\.SimUpdate::Simulate\(\)',1.0,'perSim')
addroot(r'^Sims3\.Gameplay\.Actors\.Sim::Simulate\(\)',1.0,'perSim')
addroot(r'^Sims3\.Gameplay\.Autonomy\.Motives::UpdateMotives\(',1.0,'perSim')
addroot(r'^Sims3\.Gameplay\.ActorSystems\.BuffManager::UpdateBuffs\(',1.0,'perSim')
addroot(r'^Sims3\.Gameplay\.Autonomy\.Autonomy::(Update|RunAutonomy|FindBestAction)\(',0.8,'perSimAut')
addroot(r'^Sims3\.Gameplay\.Core\.Lot::Simulate\(\)',0.8,'perLot')
addroot(r'^Sims3\.Gameplay\.Autonomy\.ReactionBroadcaster::Simulate\(',0.8,'perLot')
addroot(r'^Sims3\.Gameplay\.Utilities\.AlarmManager::Simulate\(\)',0.6,'global')
addroot(r'::Simulate\(\)$',0.4,'task')
addroot(r'^Ts3Fixes\.',0.0,'')  # none
for l in open(S+'repcb.txt'):
    i=None
for k in open(S+'repcb.txt').read().split('\n'):
    i=next((j for j,x in enumerate(M) if x['k']==k),None) if False else None
idx={x['k']:i for i,x in enumerate(M)}
for k in open(S+'repcb.txt').read().split('\n'):
    if k in idx and idx[k] not in roots: roots[idx[k]]=(0.25,'alarmRepeat')
# BFS exposure
expo=[0.0]*N; via=['']*N; dep=[99]*N
import heapq
pq=[(-w,i,0,tag) for i,(w,tag) in roots.items() if w>0]
heapq.heapify(pq)
while pq:
    nw,i,dp,tag=heapq.heappop(pq); w=-nw
    if w<=expo[i]: continue
    expo[i]=w; via[i]=tag; dep[i]=dp
    if dp>=6: continue
    cs=callees(i); nN=sum(1 for _,k in cs if k=='ifaceN')
    for t,k in cs:
        if not M[t].get('c') and not SCAN.search(M[t]['k']) and not REFL.search(M[t]['k']): pass
        ww=w*0.6*(0.3 if k in('ifaceN','virt','dinv') else 1.0)
        if ww>expo[t] and ww>0.01: heapq.heappush(pq,(-ww,t,dp+1,tag))
rows=[]
for i,x in enumerate(M):
    if expo[i]<=0: continue
    k=x['k']
    if not re.match(r'(Sims3|ScriptCore|Ts3Fixes|NRaas)',k): continue
    f=own[i]; fr=frame(k)
    c=10*f['scan']+8*f['refl']+3*f['linq']+2*f['copy']+1*f['newcoll']+1*f['str']+15*walk.get(fr,0)+0.2*haa.get(fr,0)
    if c<=0 and fr not in meas: continue
    sc=c*expo[i]+3*meas.get(fr,0)
    rows.append((sc,fr,expo[i],dep[i],via[i],dict(f),walk.get(fr,0),haa.get(fr,0),meas.get(fr,0),k))
rows.sort(key=lambda r:-r[0])
with open(S+'rank.tsv','w') as o:
    for r in rows: o.write('\t'.join(map(str,r))+'\n')
print(len(rows))
for r in rows[:70]: print(f"{r[0]:6.1f} e{r[2]:.2f} d{r[3]} {r[4]:10s} {r[5]} w{r[6]} haa{r[7]} ms{r[8]:.1f} {r[1]}")
