// docs/218: per-method IL diff of replaced game script assemblies against the game's own (Steam 1.67), and which differing
// methods the [AutShadow] worker closures reach (docs/122 v0 + implementors + meta base path, docs/126 tier 2, both with the
// docs/127 overrides up to 5 hops). Input for a per-method mod guard in place of the session MVID check (docs/122 §12.4 item G).
// Reads every .package READ-ONLY (Steam install, Documents\...\Mods) plus each --pkg file, pulls S3SA assemblies, and for each
// one whose module name is a game assembly but whose MVID is not the game's, compares every method by id: hash of the
// token-normalized IL (managed-callgraph.ts ilShape: tokens as member names, short/long opcode forms folded, branch targets as
// instruction indices; max stack and init-locals left out).
// Usage:
//   node tools/mod-method-diff.ts --asm game-copy/assemblies [--index game-copy/analysis/callgraph/index.json] [--game <Steam TS3>]
//        [--user <Documents TS3>] [--pkg <file>[=<mod label>]]... [--against <file>] [--typed <edges.json>] [--csv docs/data/mod-method-diff.csv] [--all]
//   node tools/mod-method-diff.ts --self-check --asm game-copy/assemblies
// --against: compare with that package's copies of the same modules instead of the game's (e.g. a bundle against the core it
// was built over). --all: also write identical methods. Summary to stderr. Extracted assemblies go to a temp folder deleted at exit.
// --typed: the closure methods of autonomy-virtual-closure.ts --edges (docs/127 receiver-typed run), for the -typed labels.
import { createHash } from 'node:crypto';
import { existsSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { homedir, tmpdir } from 'node:os';
import { basename, join, relative } from 'node:path';
import { moduleName, mvidOf, s3saImages } from './extract-assemblies.ts';
import { defRoots, ilShape, loadStandalone, type Asm, type Method } from './managed-callgraph.ts';

const arg = (n: string, d: string) => { const i = process.argv.indexOf(n); return i > 0 ? process.argv[i + 1] : d; };
const args = (n: string) => process.argv.flatMap((v, i) => (v === n ? [process.argv[i + 1]] : []));
const ASM = arg('--asm', 'game-copy/assemblies');
const GAME = arg('--game', process.env.TS3_GAME ?? 'C:\\Program Files (x86)\\Steam\\steamapps\\common\\The Sims 3');
const USER = arg('--user', process.env.TS3_USER ?? join(homedir(), 'Documents', 'Electronic Arts', 'The Sims 3'));
// Compiler-generated names differ between two compiles of the same source (string-switch tables, cached delegates, closure
// classes, lambda bodies): folded, so a recompile alone is no change. A method whose own id carries such a name is matched to
// its renamed twin by the folded id.
const fold = (s: string) => s.replace(/<PrivateImplementationDetails>\{[0-9A-Fa-f-]+\}(::\$\$method0x[0-9a-f]+-\d+)?/g, (_, sw) => '<PrivateImplementationDetails>' + (sw ? '::$$switch' : ''))
  .replace(/(CachedAnonymousMethodDelegate|<>c__DisplayClass|CS\$<>8__locals|>b__|>c__Iterator|>d__)[0-9a-f]+/g, '$1#');
const shape = (m: Method) => fold(ilShape(m));
const hash = (m: Method | undefined) => (m ? createHash('sha256').update(shape(m)).digest('hex').slice(0, 16) : '');
const byId = (a: Asm) => new Map(a.methods.map((m) => [m.id, m]));
const tmp = mkdtempSync(join(tmpdir(), 'mdiff-'));
const load = (img: Buffer, name: string) => { const f = join(tmp, `${Math.random().toString(36).slice(2)}_${name}.dll`); writeFileSync(f, img); return loadStandalone(f, name); };

// Calls (call/callvirt/newobj) and stores (stfld/stsfld) one shape has and the other has not.
const OPS: Record<string, string> = { 28: 'call', '6f': 'call', 73: 'new', '7d': 'stfld', '80': 'stsfld' };
function delta(a: Method, b: Method): string {
  const set = (m: Method) => new Set(shape(m).split('\n').filter((l) => OPS[l.split(' ')[0]]).map((l) => `${OPS[l.split(' ')[0]]} ${l.slice(l.indexOf(' ') + 1).replace(/\(.*$/s, '')}`));
  const x = set(a), y = set(b), short = (s: string) => s.replace(/ [\w.`]+\./, ' ');
  const add = [...y].filter((s) => !x.has(s)).map((s) => '+' + short(s)), rem = [...x].filter((s) => !y.has(s)).map((s) => '-' + short(s));
  const all = [...add, ...rem];
  const n = (m: Method) => shape(m).split('\n').length;
  return (all.length ? all.slice(0, 6).join('; ') + (all.length > 6 ? `; +${all.length - 6} more` : '') : 'calls and stores unchanged: branches, constants or locals')
    + ` (IL lines ${n(a)} -> ${n(b)})`;
}

try {
  if (process.argv.includes('--self-check')) {
    // 1) the same module twice: every hash equal; 2) one opcode flipped (ldarg.0 -> ldarg.1 at the start of a tiny body):
    // exactly that method differs; 3) tokens resolve to names, not numbers.
    const src = join(ASM, 'ScriptCore.dll'), a = loadStandalone(src, 'ScriptCore'), b = load(readFileSync(src), 'ScriptCore');
    const same = a.methods.filter((m) => hash(m) !== hash(byId(b).get(m.id)));
    if (same.length) throw new Error(`same file: ${same.length} methods differ`);
    const buf = Buffer.from(readFileSync(src));
    const off = (m: Method) => { const s = a.sections.find((x) => m.rva >= x.va && m.rva < x.va + x.size)!; return m.rva - s.va + s.raw; };
    const t = a.methods.find((m) => m.rva && (buf[off(m)] & 3) === 2 && buf[off(m) + 1] === 0x02);
    if (!t) throw new Error('no tiny body starting with ldarg.0 in ScriptCore');
    buf[off(t) + 1] = 0x03;
    const c = byId(load(buf, 'ScriptCore'));
    const diff = a.methods.filter((m) => hash(m) !== hash(c.get(m.id)));
    if (diff.length !== 1 || diff[0].id !== t.id) throw new Error(`flipped opcode: ${diff.length} differ (want 1: ${t.id})`);
    const shapes = a.methods.map(ilShape).join('\n');
    if (/\?[0-9a-f]{7,8}/.test(shapes) || !/\n28 [\w.]+::\w+/.test('\n' + shapes)) throw new Error('unresolved tokens in shapes');
    if (fold('X::<>9__CachedAnonymousMethodDelegate7') !== fold('X::<>9__CachedAnonymousMethodDelegatea')
      || fold('<PrivateImplementationDetails>{E8701302-F3CD-4F23-9387-D9D17A68FF9E}::$$method0x600f99d-1') !== '<PrivateImplementationDetails>::$$switch'
      || fold('A+<>c__DisplayClass6::.ctor()') === fold('A+Other::.ctor()')) throw new Error('fold');
    console.log(`mod-method-diff self-check: PASS (${a.methods.length} methods; flipped ${t.id})`);
  } else await main();
} finally { rmSync(tmp, { recursive: true, force: true }); }

async function main() {

  // ---------- game modules and replaced copies ----------
  const game = new Map(readdirSync(ASM).filter((f) => f.endsWith('.dll')).map((f) => { const n = f.replace(/\.dll$/, ''); return [n, loadStandalone(join(ASM, f), n)] as const; }));
  // By bytes, not MVID: tiny-ui-fix's patched UI and Sims3StoreObjects keep the game's MVIDs (docs/218 §2).
  const gameBytes = new Map([...game.keys()].map((n) => [n, readFileSync(join(ASM, n + '.dll'))]));
  const walk = (dir: string): string[] => !existsSync(dir) ? [] : readdirSync(dir, { withFileTypes: true })
    .flatMap((e) => e.isDirectory() ? walk(join(dir, e.name)) : /\.package$/i.test(e.name) ? [join(dir, e.name)] : []);
  const pkgs = [...walk(GAME).map((f) => ({ f, label: `game:${relative(GAME, f).split('\\').join('/')}` })),
    ...walk(join(USER, 'Mods')).map((f) => ({ f, label: `mods:${relative(join(USER, 'Mods'), f).split('\\').join('/')}` })),
    ...args('--pkg').map((s) => { const [f, l] = s.split('='); return { f, label: l ?? basename(f) }; })];
  const replaced = (f: string) => { let imgs; try { imgs = s3saImages(f); } catch { return []; }
    return imgs.flatMap(({ img }) => { if (!img) return []; const n = (moduleName(img) ?? '').replace(/\.dll$/i, ''); return game.has(n) && !gameBytes.get(n)!.equals(img) ? [{ n, img, sameMvid: mvidOf(img) === mvidOf(gameBytes.get(n)!) }] : []; }); };
  const against = arg('--against', '');
  const base = new Map(game);
  if (against) for (const { n, img } of replaced(against)) base.set(n, load(img, n));

  // ---------- worker reach (method ids) ----------
  // autonomy-tier2.ts reads --index / --asm itself and builds the pilot union and the tier-2 candidates at import.
  const T = await import('./autonomy-tier2.ts');
  // Labels, most direct first: v0 = the docs/122 pilot union (v0 definitions, the 7 implementors, the meta base path; no
  // overrides); typed / cha = with overrides up to 5 hops (docs/127: receiver-typed run from --typed, else class hierarchy);
  // tier2 = a docs/126 ready or new-rows candidate; tier2-blocked = reached only through blocked candidates.
  const reach = new Map<string, string>(); // id -> best label
  const RANK = ['v0', 'v0-typed', 'v0-cha', 'tier2', 'tier2-typed', 'tier2-cha', 'tier2-blocked', 'tier2-blocked-cha'];
  const put = (k: string, l: string) => { const o = reach.get(k); if (!o || RANK.indexOf(l) < RANK.indexOf(o)) reach.set(k, l); };
  const mark = (ids: number[], l: string) => { for (const i of ids) put(T.ix.methods[i].k, l); };
  mark(T.pilot.ids, 'v0');
  for (const t of T.inV0) mark(T.closure(defRoots(T.ix as never, t), t, 5).ids, 'v0-cha');
  for (const t of T.IMPL) mark(T.closure(T.starts(`${t}::HowMuchItWantsSimToCome`), undefined, 5).ids, 'v0-cha');
  for (const c of T.cands) {
    if (!c.roots.length) continue;
    const r = T.closure(c.roots, c.own), ok = /^(ready|new-rows)$/.test(T.classify(c, r).verdict);
    mark(r.ids, ok ? 'tier2' : 'tier2-blocked'); mark(T.closure(c.roots, c.own, 5).ids, ok ? 'tier2-cha' : 'tier2-blocked-cha');
  }
  // autonomy-virtual-closure.ts --edges <file>: its typed run's closure methods with the candidate groups that reach them
  const typed = arg('--typed', '');
  if (typed) for (const m of JSON.parse(readFileSync(typed, 'utf8')).methods as { k: string; groups: string[] }[])
    put(m.k, m.groups.some((g) => g === 'v0' || g === 'v0-implementor') ? 'v0-typed' : 'tier2-typed');

  // ---------- compare ----------
  const esc = (s: string) => (/[",\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s);
  const rows: string[] = [];
  const sum: Record<string, Record<string, number>> = {};
  for (const p of pkgs) for (const { n, img, sameMvid } of replaced(p.f)) {
    const mod = load(img, n), ea = base.get(n)!, em = byId(ea), mm = byId(mod);
    const s = (sum[`${p.label} | ${n} | ${mod.mvid}${sameMvid ? ' (the game MVID)' : ''}`] = { methods: mm.size, same: 0, differs: 0, added: 0, removed: 0, differsReach: 0, addedReach: 0, removedReach: 0 });
    // game-only and mod-only ids that fold to the same id: one renamed compiler-generated member (first twin with the same hash)
    const twin = new Map<string, string>(), byFold = new Map<string, string[]>();
    for (const id of mm.keys()) if (!em.has(id)) byFold.set(fold(id), [...(byFold.get(fold(id)) ?? []), id]);
    for (const id of em.keys()) if (!mm.has(id)) {
      const c = byFold.get(fold(id)) ?? []; if (!c.length) continue;
      const k = Math.max(0, c.findIndex((x) => hash(mm.get(x)) === hash(em.get(id)))); twin.set(id, c[k]); c.splice(k, 1);
    }
    const paired = new Set(twin.values());
    for (const id of new Set([...em.keys(), ...mm.keys()])) {
      if (paired.has(id)) continue;
      const a = em.get(id), b = mm.get(twin.get(id) ?? id), ha = hash(a), hb = hash(b);
      const kind = !a ? 'added' : !b ? 'removed' : ha === hb ? 'same' : 'differs';
      s[kind]++;
      const r = reach.get(id) ?? 'no';
      if (kind !== 'same' && r !== 'no') s[kind + 'Reach']++;
      if (kind === 'same' && !process.argv.includes('--all')) continue;
      const tw = twin.get(id), m = (a ?? b)!;
      const note = (tw ? `renamed to ${tw.slice(tw.indexOf('::') + 2)}; ` : '') + (kind === 'differs' ? delta(a!, b!) : kind === 'added' ? 'only in the mod copy' : kind === 'removed' ? 'only in the game copy' : '');
      rows.push([p.label, n, m.type.full, id.slice(id.indexOf('::') + 2), ha, hb, kind === 'same' ? 'no' : kind === 'differs' ? 'yes' : kind, r, note].map(esc).join(','));
    }
  }
  const out = arg('--csv', '');
  const head = 'mod,assembly,type,method,il_hash_ea,il_hash_mod,differs,reachable_from_worker,notes';
  if (out) writeFileSync(out, [head, ...rows.sort()].join('\n') + '\n'); else console.log([head, ...rows].join('\n'));
  console.error(JSON.stringify({ against: against || 'game', reachIds: reach.size, byLabel: Object.fromEntries(RANK.map((l) => [l, [...reach.values()].filter((v) => v === l).length])), modules: sum }, null, 1));
}
