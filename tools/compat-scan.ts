// Compatibility scan (PLAN.md "Compatibility track" step 1): what other mods touch that our mods also touch. Read-only.
//   node tools/compat-scan.ts [<folder>...] [--json] [--matrix docs/data/compat-matrix.csv]
//        [--game "<Steam>\The Sims 3"] [--asm game-copy/assemblies] [--ts3fixes build/ts3fixes.package]
//   node tools/compat-scan.ts --self-check
// Folders: a Mods folder or a folder of mod archives / packages, scanned recursively (default game-copy/compat-mods and
// <Documents>\Electronic Arts\The Sims 3\Mods\Packages; env TS3_USER / TS3_GAME as in offload-override-scan.ts).
// Lists (a) every game method our ts3fixes patches (static scan of mods/ts3fixes/src Patch(...) calls, with the feature =
// source file and the ShortcutOn switches in it) and every ts3native hook address (tools/hook-sites.ts scan of
// native/ts3native/*.cpp; both only when the repo sources are there); (b) per mod file: script assemblies (name, MVID),
// mod types that derive from a patched type or override a patched method, replaced game modules, resource keys shared
// with ts3fixes.package; (c) a verdict: no overlap / overlap (feature, switch) / unknown (archive not readable, ASI).
// Archives: .package directly, .zip read in memory (tools/mts-zip.ts unzip), .rar / .7z = "unpack needed".
// Game assemblies for the type hierarchy: --asm (default game-copy/assemblies), else the S3SA images of <game>\Game\Bin\*.package.
import { closeSync, existsSync, mkdtempSync, openSync, readdirSync, readFileSync, readSync, rmSync, writeFileSync } from 'node:fs';
import { homedir, tmpdir } from 'node:os';
import { basename, extname, join, relative } from 'node:path';
import { moduleName, mvidOf, readIndex, s3saImages } from './extract-assemblies.ts';
import { scan as hookScan } from './hook-sites.ts';
import { unzip, zip } from './mts-zip.ts';
import { TYPE_S3SA, fnv64, writePackage } from './dbpf.ts';

const REPO = join(import.meta.dirname, '..');
const argv = process.argv.slice(2);
const opt = (n: string, d: string) => { const i = argv.indexOf(n); return i >= 0 ? argv[i + 1] : d; };
const USER = process.env.TS3_USER ?? join(homedir(), 'Documents', 'Electronic Arts', 'The Sims 3');
const GAME = opt('--game', process.env.TS3_GAME ?? 'C:\\Program Files (x86)\\Steam\\steamapps\\common\\The Sims 3');
const OURS = /^(ts3fixes|ts3agent)$/i; // our own assemblies / packages are not "other mods"
const TUNING = new Set([0x0333406c, 0x03b33ddf]); // _XML, ITUN

// ---------- (a) what we patch ----------
export type Target = { type: string; method: string; feature: string; switches: string[]; where: string; hook?: string };

/** First argument of a call: text up to the top-level comma. */
function firstArg(s: string, at: number): [string, number] {
  let depth = 0, i = at, q = false;
  for (; i < s.length; i++) {
    const c = s[i];
    if (q) { if (c === '\\') i++; else if (c === '"') q = false; continue; }
    if (c === '"') q = true;
    else if (c === '(') depth++;
    else if (c === ')') { if (!depth) break; depth--; }
    else if (c === ',' && !depth) break;
  }
  return [s.slice(at, i).trim(), i];
}

/** A C# Type expression -> type name ('A.B' short or dotted, 'X+Nested', or a full name from Assembly.GetType); null = dynamic. */
export function typeExpr(e: string, src: string, depth = 0): string | null {
  let m = /^typeof\(([\w.]+)\)\.GetNestedType\("(\w+)"/.exec(e);
  if (m) return `${m[1]}+${m[2]}`;
  if ((m = /^typeof\([\w.]+\)\.Assembly\.GetType\("([\w.+]+)"/.exec(e))) return m[1];
  if ((m = /^typeof\(([\w.]+)\)$/.exec(e))) return m[1];
  if (/^\w+$/.test(e) && depth < 3) { // local: Type x = <expr>;
    const d = new RegExp(`\\b${e}\\s*=\\s*([^;]+);`).exec(src);
    if (d) return typeExpr(d[1].trim(), src, depth + 1);
  }
  return null;
}

/** Patch(<type>, "<method>", ...) calls of one ts3fixes source file. */
export function patchCalls(src: string, feature: string): { targets: Target[]; dynamic: number } {
  const code = src.replace(/\/\/.*$/gm, '');
  const switches = [...new Set([...code.matchAll(/ShortcutOn\("(\w+)"\)/g)].map((x) => x[1]))].sort();
  const targets: Target[] = [];
  let dynamic = 0;
  for (const m of code.matchAll(/\bPatch\(/g)) {
    if (/\bbool\s+$|\bstatic\s+\w+\s+$/.test(code.slice(Math.max(0, m.index - 30), m.index))) continue; // the helper's own definition
    const [a1, end] = firstArg(code, m.index + 6);
    const meth = /^\s*,\s*"(\w+)"/.exec(code.slice(end));
    const type = typeExpr(a1, code);
    if (!meth || !type) { dynamic++; continue; }
    const line = code.slice(0, m.index).split('\n').length;
    let hook: string | undefined;
    for (let p = end + meth[0].length; code[p] === ',' && !hook;) { const [a, e] = firstArg(code, p + 1); hook = /^typeof\((\w+)\)$/.exec(a)?.[1]; p = e; }
    targets.push({ type, method: meth[1], feature, switches, where: `${feature}.cs:${line}`, hook });
  }
  return { targets, dynamic };
}

export function ourPatches(dir = join(REPO, 'mods', 'ts3fixes', 'src')) {
  const all = { targets: [] as Target[], dynamic: 0, usings: new Map<string, string[]>(), named: [] as Target[] };
  if (!existsSync(dir)) return all;
  const srcs = readdirSync(dir).filter((n) => n.endsWith('.cs')).sort().map((f) => [f.replace(/\.cs$/, ''), readFileSync(join(dir, f), 'utf8')]);
  for (const [feat, src] of srcs) {
    const r = patchCalls(src, feat);
    all.targets.push(...r.targets); all.dynamic += r.dynamic;
    all.usings.set(feat, [...src.matchAll(/^using\s+([\w.]+)\s*;/gm)].map((x) => x[1]));
    all.named.push(...namedTypes(src, feat));
  }
  hookSwitches(all.targets, srcs.map(([, s]) => s).join('\n'));
  return all;
}

/** Other mods' types ts3fixes looks up by name (GetType("NRaas.Overwatch") ...): its run-time patches and stand-downs on them.
 *  Game namespaces are left out (Patch calls cover those). */
export function namedTypes(src: string, feature: string): Target[] {
  const code = src.replace(/\/\/.*$/gm, '');
  const switches = [...new Set([...code.matchAll(/ShortcutOn\("(\w+)"\)/g)].map((x) => x[1]))].sort();
  return [...new Set([...code.matchAll(/"([A-Za-z_]\w*(?:\.\w+)+(?:\+\w+)*)"/g)].map((m) => m[1]))]
    .filter((n) => !/^(Sims3|ScriptCore|System|UI|Ts3Fixes|Ts3Agent)\./.test(n) && !/\.(cs|dll|package|xml|txt|log|ini|asi)$/i.test(n))
    .map((n) => ({ type: n, method: '', feature, switches, where: `${feature}.cs` }));
}

/** Switches = the ShortcutOn names inside the hook class (its header to the next class header), else the whole file's. */
export function hookSwitches(targets: Target[], allSrc: string) {
  const text = allSrc.replace(/\/\/.*$/gm, '');
  for (const t of targets) {
    const at = t.hook ? text.search(new RegExp(`\\bclass\\s+${t.hook}\\b`)) : -1;
    if (at < 0) continue;
    const next = text.slice(at + 6).search(/\bclass\s+\w+/), body = text.slice(at, next < 0 ? undefined : at + 6 + next);
    const sw = [...new Set([...body.matchAll(/ShortcutOn\("(\w+)"\)/g)].map((x) => x[1]))].sort();
    if (sw.length) t.switches = sw;
  }
}

export function nativeHooks(dir = join(REPO, 'native', 'ts3native')) {
  if (!existsSync(dir)) return [];
  const hits = readdirSync(dir).filter((f) => f.endsWith('.cpp') && !/test/.test(f))
    .flatMap((f) => hookScan(readFileSync(join(dir, f), 'utf8'), f.replace(/\.cpp$/, '')));
  return [...new Map(hits.map((h) => [h.addr, h])).values()].sort((a, b) => a.addr - b.addr)
    .map((h) => ({ addr: `0x${h.addr.toString(16).toUpperCase().padStart(8, '0')}`, module: h.module, label: h.label }));
}

// ---------- (b) per mod file ----------
export type ModType = { asm: string; full: string; chain: string[]; virtuals: string[] }; // chain = base types, nearest first
export type Resolved = Target & { full: string; module: string; virtual: boolean };
type Finding = { kind: 'replaces' | 'named' | 'overrides' | 'derives' | 'key'; text: string; feature?: string; switches?: string[] };

/** Short / dotted / nested names from Patch calls -> game types (full name, module, virtual method), using the file's usings. */
export function resolveTargets(targets: Target[], usings: Map<string, string[]>,
  game: { full: string; module: string; methods: { name: string; virtual: boolean }[] }[]): { resolved: Resolved[]; unresolved: Target[] } {
  const resolved: Resolved[] = [], unresolved: Target[] = [];
  for (const t of targets) {
    const want = t.type.replace(/\+/g, '.');
    let c = game.filter((g) => { const f = g.full.replace(/\+/g, '.'); return f === want || f.endsWith(`.${want}`); });
    const withM = c.filter((g) => g.methods.some((m) => m.name === t.method));
    if (withM.length) c = withM;
    if (c.length > 1) { const u = usings.get(t.feature) ?? []; const inU = c.filter((g) => u.some((n) => g.full.startsWith(`${n}.`) && !g.full.slice(n.length + 1).includes('.'))); if (inU.length) c = inU; }
    if (!c.length) { unresolved.push(t); continue; }
    for (const g of c) resolved.push({ ...t, full: g.full, module: g.module, virtual: g.methods.some((m) => m.name === t.method && m.virtual) });
  }
  return { resolved, unresolved };
}

/** What one mod's types and assemblies do to the patched targets. */
export function typeFindings(types: ModType[], replaced: string[], patched: Resolved[], named: Target[] = []): Finding[] {
  const out: Finding[] = [];
  for (const t of types) for (const n of named.filter((x) => x.type === t.full))
    out.push({ kind: 'named', text: `ts3fixes ${n.feature} looks up ${t.full} by name (hook or stand-down)`, feature: n.feature, switches: n.switches });
  for (const mod of replaced) for (const p of patched.filter((x) => x.module === mod))
    out.push({ kind: 'replaces', text: `replaces ${mod} (patched ${p.full}::${p.method})`, feature: p.feature, switches: p.switches });
  for (const t of types) for (const p of patched) {
    if (!t.chain.includes(p.full)) continue;
    if (p.virtual && t.virtuals.includes(p.method)) out.push({ kind: 'overrides', text: `${t.full} overrides ${p.full}::${p.method}`, feature: p.feature, switches: p.switches });
    else out.push({ kind: 'derives', text: `${t.full} derives ${p.full} (patched ${p.method})`, feature: p.feature, switches: p.switches });
  }
  return out;
}

type Unit = { mod: string; file: string; kind: Set<string>; asms: { name: string; mvid: string }[]; findings: Finding[]; unknown?: string; keys: Set<string> };
const keyOf = (e: { type: number; group: number; inst: bigint }) => `${e.type.toString(16).padStart(8, '0')}:${e.group.toString(16).padStart(8, '0')}:${e.inst.toString(16).padStart(16, '0')}`;

/** Index entries of a package from a buffer or from disk (header + index only: some packages are GBs). */
function packageIndex(file: string | Buffer) {
  if (Buffer.isBuffer(file)) return readIndex(file);
  const fd = openSync(file, 'r');
  try {
    const read = (pos: number, len: number) => { const b = Buffer.alloc(len); readSync(fd, b, 0, len, pos); return b; };
    const head = read(0, 96);
    if (head.toString('latin1', 0, 4) !== 'DBPF') throw new Error('not DBPF');
    const syn = Buffer.concat([head, read(head.readUInt32LE(64), head.readUInt32LE(44))]);
    syn.writeUInt32LE(96, 64);
    return readIndex(syn);
  } finally { closeSync(fd); }
}

// derives only = no overlap: the subclass inherits the patched method, so our hook runs for it too (kept as evidence)
export const verdict = (u: Pick<Unit, 'findings' | 'unknown'>) => u.unknown ? 'unknown' : u.findings.some((f) => f.kind !== 'derives') ? 'overlap' : 'no overlap';
const overlapText = (u: Unit) => {
  const by = new Map<string, Set<string>>();
  for (const f of u.findings.filter((f) => f.kind !== 'derives')) { const s = by.get(f.feature ?? 'resource') ?? new Set(); f.switches?.forEach((x) => s.add(x)); by.set(f.feature ?? 'resource', s); }
  return [...by].map(([k, s]) => `${k}${s.size ? ` (${[...s].sort().join('/')})` : ''}`).join('; ');
};
const RANK = { replaces: 0, named: 1, key: 2, overrides: 3, derives: 4 };
const evidenceText = (u: Unit) => {
  const f = [...u.findings].sort((a, b) => RANK[a.kind] - RANK[b.kind]);
  return f.map((x) => x.text).slice(0, 12).join('; ') + (f.length > 12 ? `; +${f.length - 12} more` : '');
};
const kindText = (u: Unit) => [...u.kind].sort().join('+') || (u.unknown ? 'archive' : 'empty');

/** CSV text -> rows of fields; quotes as this tool writes them (\") or as spreadsheets do (""). */
export function parseCsv(text: string): string[][] {
  const rows: string[][] = [];
  let row: string[] = [], cell = '', q = false, was = false;
  const end = () => { row.push(cell); cell = ''; was = false; };
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (q) {
      if (c === '\\' && text[i + 1] === '"') { cell += '"'; i++; }
      else if (c === '\\' && text[i + 1] === '\\') { cell += '\\'; i++; }
      else if (c === '"' && text[i + 1] === '"') { cell += '"'; i++; }
      else if (c === '"') q = false;
      else cell += c;
    } else if (c === '"') { q = true; was = true; }
    else if (c === ',') end();
    else if (c === '\n') { end(); rows.push(row); row = []; }
    else if (c !== '\r') cell += c;
  }
  if (cell || was || row.length) { end(); rows.push(row); }
  return rows;
}

const MATRIX_COLS = ['mod', 'file', 'kind', 'overlap', 'verdict', 'evidence', 'status', 'note'];
/** The matrix after a scan: scanned rows keep the status and note of their old row (key: file); old rows of files this scan did
 *  not see stay as they are (other scans, e.g. a player's Mods folder).
 *  shortcut: a mod file deleted for good stays in the matrix, drop its row by hand when that matters. */
export function matrixCsv(rows: { mod: string; file: string; kind: string; overlap: string; verdict: string; evidence: string }[], old: string): string {
  const [head, ...oldRows] = parseCsv(old);
  const at = (r: string[], c: string) => r[head.indexOf(c)] ?? '';
  const scanned = new Set(rows.map((r) => r.file)), byFile = new Map(oldRows.map((r) => [at(r, 'file'), r]));
  const line = (r: string[]) => r.map((v) => JSON.stringify(v)).join(',');
  const fresh = rows.map((r) => { const o = byFile.get(r.file); return line([r.mod, r.file, r.kind, r.overlap, r.verdict, r.evidence, o ? at(o, 'status') : '', o ? at(o, 'note') : '']); });
  const kept = oldRows.filter((r) => !scanned.has(at(r, 'file'))).map((r) => line(MATRIX_COLS.map((c) => at(r, c))));
  return `${MATRIX_COLS.join(',')}\n${[...fresh, ...kept].join('\n')}\n`;
}

function walk(dir: string): string[] {
  if (!existsSync(dir)) return [];
  return readdirSync(dir, { withFileTypes: true }).flatMap((e) => e.isDirectory() ? walk(join(dir, e.name)) : [join(dir, e.name)]);
}

// ---------- main scan ----------
function run() {
  const pos = argv.filter((a, i) => !a.startsWith('--') && !(i && argv[i - 1].startsWith('--') && !['--json', '--self-check'].includes(argv[i - 1])));
  const roots = (pos.length ? pos : [join(REPO, 'game-copy', 'compat-mods'), join(USER, 'Mods', 'Packages')]).filter(existsSync);
  const label = (root: string, f: string) => `${basename(root)}/${relative(root, f).split('\\').join('/')}`;
  const ours = ourPatches(), hooks = nativeHooks();

  // ts3fixes.package resource keys
  const fixPkg = opt('--ts3fixes', join(REPO, 'build', 'ts3fixes.package'));
  const fixKeys = existsSync(fixPkg) ? new Set(packageIndex(fixPkg).map(keyOf)) : new Set<string>();

  const tmp = mkdtempSync(join(tmpdir(), 'compat-'));
  try {
    // game assemblies: --asm, else the install's script packages
    const asmDir = opt('--asm', join(REPO, 'game-copy', 'assemblies'));
    const gameDlls = new Map<string, Buffer>();
    if (existsSync(join(asmDir, 'Sims3GameplaySystems.dll'))) for (const f of readdirSync(asmDir).filter((n) => n.endsWith('.dll'))) gameDlls.set(f.replace(/\.dll$/, ''), readFileSync(join(asmDir, f)));
    else for (const f of walk(join(GAME, 'Game', 'Bin')).filter((n) => /\.package$/i.test(n))) {
      try { for (const { img } of s3saImages(f)) if (img) gameDlls.set((moduleName(img) ?? '').replace(/\.dll$/i, ''), img); } catch { /* not a package */ }
    }
    const gameMvid = new Map([...gameDlls].map(([n, b]) => [n, (() => { try { return mvidOf(b); } catch { return ''; } })()]));

    const units: Unit[] = [];
    const modDlls: { file: string; unit: Unit; name: string }[] = [];
    const addPackage = (u: Unit, src: string | Buffer, tag: string) => {
      u.kind.add('cc');
      let entries;
      try { entries = packageIndex(src); } catch (e) { u.unknown = `${tag}: not a readable package (${(e as Error).message})`; return; }
      if (entries.some((e) => TUNING.has(e.type))) { u.kind.delete('cc'); u.kind.add('tuning'); }
      for (const e of entries) u.keys.add(keyOf(e));
      if (!entries.some((e) => e.type === TYPE_S3SA)) return;
      u.kind.delete('cc'); u.kind.add('script');
      let file = src as string;
      if (Buffer.isBuffer(src)) { file = join(tmp, `pkg${units.length}_${u.asms.length}.package`); writeFileSync(file, src); }
      for (const { img, inst } of s3saImages(file)) {
        if (!img) { u.asms.push({ name: `${inst.toString(16)} (no PE)`, mvid: '' }); continue; }
        const name = (moduleName(img) ?? `${inst.toString(16)}.dll`).replace(/\.dll$/i, '');
        let mvid = ''; try { mvid = mvidOf(img); } catch { /* unparsed */ }
        u.asms.push({ name, mvid });
        if (OURS.test(name)) { u.kind.add('ours'); continue; }
        if (gameMvid.has(name)) { if (gameMvid.get(name) !== mvid) (u as any).replaced = [...((u as any).replaced ?? []), name]; continue; }
        const dll = `zz${String(modDlls.length).padStart(4, '0')}_${name}.dll`; // sorts after every game assembly
        writeFileSync(join(tmp, dll), img);
        modDlls.push({ file: dll.replace(/\.dll$/, ''), unit: u, name });
      }
    };
    for (const root of roots) for (const f of walk(root)) {
      const ext = extname(f).toLowerCase();
      if (!['.package', '.zip', '.rar', '.7z', '.asi', '.dbc'].includes(ext)) continue;
      const u: Unit = { mod: basename(f, extname(f)), file: label(root, f), kind: new Set(), asms: [], findings: [], keys: new Set() };
      if (OURS.test(u.mod)) continue;
      if (ext === '.rar' || ext === '.7z') { u.unknown = `${ext.slice(1)} archive: unpack needed`; }
      else if (ext === '.asi') { u.kind.add('asi'); u.unknown = 'ASI: native hooks, compare its sites with node tools/hook-sites.ts and docs/68'; }
      else if (ext === '.zip') {
        try {
          for (const [n, b] of unzip(readFileSync(f))) {
            const e = extname(n).toLowerCase();
            if (e === '.package') addPackage(u, b, n);
            else if (e === '.asi') { u.kind.add('asi'); u.unknown = 'ASI inside: native hooks, compare with hook-sites.ts / docs/68'; }
            else if (e === '.rar' || e === '.7z' || e === '.zip') u.unknown = `nested ${e.slice(1)} (${n}): unpack needed`;
          }
        } catch (e) { u.unknown = `zip not readable here (${(e as Error).message})`; }
      } else addPackage(u, f, basename(f));
      if (u.kind.has('ours')) continue;
      units.push(u);
    }

    // type model: game + mod assemblies in one folder (managed-callgraph keeps one global model per process)
    let resolved: Resolved[] = [], unresolved: Target[] = ours.targets, modelNote = '';
    if (gameDlls.size && ours.targets.length) {
      for (const [n, b] of gameDlls) writeFileSync(join(tmp, `${n}.dll`), b);
      const { typeModel } = await_import_callgraph();
      const skipped: string[] = [];
      const M = typeModel(tmp, skipped);
      const isMod = (a: string) => a.startsWith('zz');
      const all = M.asms.flatMap((a: any) => a.types);
      const game = all.filter((t: any) => !isMod(t.asm.name)).map((t: any) => ({ full: t.full, module: t.asm.name,
        methods: t.methods.map((m: any) => ({ name: m.name, virtual: !!(m.flags & M.VIRT) })) }));
      ({ resolved, unresolved } = resolveTargets(ours.targets, ours.usings, game));
      const byFile = new Map(modDlls.map((d) => [d.file, d]));
      const perUnit = new Map<Unit, ModType[]>();
      for (const t of all.filter((t: any) => isMod(t.asm.name) && t.name !== '<Module>')) {
        const chain: string[] = []; for (let b = M.baseOf(t); b; b = M.baseOf(b)) chain.push(b.full);
        const d = byFile.get(t.asm.name)!;
        perUnit.set(d.unit, [...(perUnit.get(d.unit) ?? []), { asm: d.name, full: t.full, chain,
          virtuals: t.methods.filter((m: any) => m.flags & M.VIRT && !(m.flags & M.NEWSLOT)).map((m: any) => m.name) }]);
      }
      for (const u of units) u.findings.push(...typeFindings(perUnit.get(u) ?? [], (u as any).replaced ?? [], resolved, ours.named));
      if (skipped.length) modelNote = `unparsed assemblies: ${skipped.length}`;
    } else modelNote = gameDlls.size ? 'no ts3fixes source: patched methods unknown' : 'no game assemblies (--asm or --game): type checks skipped';
    for (const u of units) {
      for (const k of u.keys) if (fixKeys.has(k)) u.findings.push({ kind: 'key', text: `same resource key as ts3fixes.package ${k}` });
      if (!u.unknown && u.kind.has('script') && !gameDlls.size) u.unknown = 'script mod: no game assemblies to check against';
    }

    // ---------- output ----------
    const rows = units.map((u) => ({ mod: u.mod, file: u.file, kind: kindText(u), assemblies: u.asms, overlap: overlapText(u), verdict: verdict(u),
      evidence: u.unknown ?? evidenceText(u) }));
    const summary = {
      roots: roots.map((r) => basename(r)), files: rows.length, byVerdict: Object.fromEntries(['no overlap', 'overlap', 'unknown'].map((v) => [v, rows.filter((r) => r.verdict === v).length])),
      patchedMethods: ours.targets.length, resolved: new Set(resolved.map((r) => `${r.full}::${r.method}`)).size, unresolved: unresolved.map((t) => `${t.type}::${t.method}`),
      dynamicPatchCalls: ours.dynamic, nativeHookAddresses: hooks.length, ts3fixesKeys: fixKeys.size, note: modelNote,
    };
    if (argv.includes('--json')) console.log(JSON.stringify({ summary, patched: resolved.map(({ full, method, feature, switches, where }) => ({ type: full, method, feature, switches, where })), nativeHooks: hooks, mods: rows }, null, 1));
    else {
      console.log(`(a) ts3fixes patches ${summary.resolved} game methods (${ours.targets.length} Patch calls, ${ours.dynamic} built at run time, ${unresolved.length} unresolved); ts3native names ${hooks.length} hook addresses (node tools/hook-sites.ts)`);
      console.log(`(b,c) ${rows.length} mod files in ${summary.roots.join(', ')}: ${Object.entries(summary.byVerdict).map(([k, v]) => `${k} ${v}`).join(', ')}${modelNote ? `; ${modelNote}` : ''}\n`);
      console.log('| mod | kind | verdict | overlap | evidence |\n|---|---|---|---|---|');
      for (const r of rows.filter((r) => r.verdict !== 'no overlap')) console.log(`| ${r.mod} | ${r.kind} | ${r.verdict} | ${r.overlap || '-'} | ${r.evidence.slice(0, 160)} |`);
      console.log(`\nno overlap: ${rows.filter((r) => r.verdict === 'no overlap').map((r) => r.mod).join(', ') || '-'}`);
    }
    const matrix = opt('--matrix', '');
    // CC-only packages without overlap stay out of the matrix: nothing to check, and a player's CC list is personal
    if (matrix) writeFileSync(matrix, matrixCsv(rows.filter((r) => r.kind !== 'cc' || r.verdict !== 'no overlap'), existsSync(matrix) ? readFileSync(matrix, 'utf8') : ''));
  } finally { rmSync(tmp, { recursive: true, force: true }); }
}

// managed-callgraph reads process.argv at load; import it only when the scan needs the type model.
let cg: any;
const await_import_callgraph = () => cg;

function selfCheck() {
  const assert = (cond: unknown, msg: string) => { if (!cond) throw new Error(`compat-scan self-check: ${msg}`); };
  const src = `using Sims3.Gameplay.Core;\nType helper = typeof(LotManager).GetNestedType("RunStuckCheckHelper", BindingFlags.NonPublic);
    static bool Patch(Type t, string m) { return true; }
    void A() { if (ShortcutOn("easpreadlots")) {} // ShortcutOn("commented")
      Patch(typeof(GoHome), "Run", typeof(H), "RunHook", "RunOrig");
      Patch(typeof(Sim.DestroyMyself), "Run", Type.EmptyTypes, typeof(H), "a", "b");
      Patch(helper, "Run", typeof(H), "x", null);
      Patch(typeof(X).GetNestedType("Y", BindingFlags.NonPublic), "Compare", typeof(H), "c", null);
      Patch(target, method, args); }`;
  const r = patchCalls(src, 'EaSpread');
  assert(r.targets.map((t) => `${t.type}::${t.method}`).join(' ') === 'GoHome::Run Sim.DestroyMyself::Run LotManager+RunStuckCheckHelper::Run X+Y::Compare', `patch calls ${JSON.stringify(r.targets.map((t) => t.type))}`);
  assert(r.dynamic === 1 && r.targets[0].switches.join() === 'easpreadlots' && r.targets[1].hook === 'H', 'dynamic count / switches / hook');
  hookSwitches(r.targets, `${src}\nclass H { bool R() { return ShortcutOn("gohome"); } }\nclass Z { bool S() { return ShortcutOn("other"); } }`);
  assert(r.targets[0].switches.join() === 'gohome', `hook class switches ${r.targets[0].switches}`);
  const game = [
    { full: 'Sims3.Gameplay.Actors.Sim+GoHome', module: 'Sims3GameplaySystems', methods: [{ name: 'Run', virtual: true }] },
    { full: 'Sims3.Gameplay.Interactions.GoHome', module: 'Sims3GameplaySystems', methods: [{ name: 'Run', virtual: true }] },
    { full: 'Sims3.Gameplay.Actors.Sim+DestroyMyself', module: 'Sims3GameplaySystems', methods: [{ name: 'Run', virtual: false }] },
    { full: 'Sims3.Gameplay.Core.LotManager+RunStuckCheckHelper', module: 'Sims3GameplaySystems', methods: [{ name: 'Run', virtual: false }] }];
  const res = resolveTargets(r.targets, new Map([['EaSpread', ['Sims3.Gameplay.Interactions']]]), game);
  assert(res.resolved.map((x) => x.full).join() === 'Sims3.Gameplay.Interactions.GoHome,Sims3.Gameplay.Actors.Sim+DestroyMyself,Sims3.Gameplay.Core.LotManager+RunStuckCheckHelper', `resolve ${res.resolved.map((x) => x.full)}`);
  assert(res.unresolved.length === 1 && res.unresolved[0].type === 'X+Y', 'unresolved');
  const f = typeFindings([
    { asm: 'M', full: 'Mod.MyGoHome', chain: ['Sims3.Gameplay.Interactions.GoHome', 'X'], virtuals: ['Run'] },
    { asm: 'M', full: 'Mod.Kid', chain: ['Sims3.Gameplay.Actors.Sim+DestroyMyself'], virtuals: ['Run'] },
    { asm: 'M', full: 'Mod.Other', chain: ['Y'], virtuals: ['Run'] }], ['Sims3GameplaySystems'], res.resolved);
  const nm = namedTypes('Type a = asm.GetType("NRaas.Overwatch"); x = "Sims3.Gameplay.Core.Lot"; f = "ts3native.log"; ShortcutOn("owspread");', 'OverwatchSpread');
  assert(nm.length === 1 && nm[0].type === 'NRaas.Overwatch' && nm[0].switches.join() === 'owspread', `named ${JSON.stringify(nm)}`);
  assert(typeFindings([{ asm: 'N', full: 'NRaas.Overwatch', chain: [], virtuals: [] }], [], [], nm)[0]?.kind === 'named', 'named finding');
  assert(f.filter((x) => x.kind === 'overrides').length === 1 && f.filter((x) => x.kind === 'derives').length === 1 && f.filter((x) => x.kind === 'replaces').length === 3, `findings ${f.map((x) => x.kind)}`);
  assert(verdict({ findings: [] }) === 'no overlap' && verdict({ findings: f.filter((x) => x.kind === 'derives') }) === 'no overlap' && verdict({ findings: f }) === 'overlap' && verdict({ findings: [], unknown: 'x' }) === 'unknown', 'verdicts');
  const oldCsv = 'mod,file,kind,overlap,verdict,evidence,status,note\n"A","a.package","script","","overlap","say \\"hi\\", ok","in our test setup","limit, known"\n"P","player/p.package","script","","no overlap","e","",""\n';
  const out = parseCsv(matrixCsv([{ mod: 'A', file: 'a.package', kind: 'script', overlap: 'X', verdict: 'overlap', evidence: 'new "x"' }, { mod: 'N', file: 'n.package', kind: 'script', overlap: '', verdict: 'no overlap', evidence: '' }], oldCsv));
  assert(out[0].length === 8 && out.length === 4, `matrix shape ${JSON.stringify(out)}`);
  assert(out[1].join('|') === 'A|a.package|script|X|overlap|new "x"|in our test setup|limit, known', `matrix keeps status and note ${out[1]}`);
  assert(out[2][1] === 'n.package' && out[2][6] === '' && out[3][1] === 'player/p.package', 'matrix new row, rows of other scans kept');
  assert(parseCsv('a,"b ""c"""\n')[0][1] === 'b "c"' && parseCsv(matrixCsv([], ''))[0].length === 8, 'matrix csv quotes / empty old file');
  // package index from disk and from a zip, stored and deflated
  const pkg = writePackage([{ type: 0x0333406c, group: 0, instance: 7n, data: Buffer.from('<x/>') }, { type: TYPE_S3SA, group: 0, instance: fnv64('A'), data: Buffer.from('not an assembly') }]);
  const tmp = mkdtempSync(join(tmpdir(), 'compat-sc-'));
  try {
    writeFileSync(join(tmp, 'a.package'), pkg);
    assert(packageIndex(join(tmp, 'a.package')).map(keyOf).includes('0333406c:00000000:0000000000000007'), 'index from disk');
    const z = unzip(zip([['Mods/a.package', pkg]]));
    assert(packageIndex(z.get('Mods/a.package')!).length === 2, 'index from zip');
  } finally { rmSync(tmp, { recursive: true, force: true }); }
  // the repo's own sources, when present
  const o = ourPatches();
  if (existsSync(join(REPO, 'mods', 'ts3fixes', 'src'))) assert(o.targets.some((t) => t.type === 'GoHome' && t.method === 'Run' && t.feature === 'Fixes'), 'ts3fixes GoHome::Run');
  if (existsSync(join(REPO, 'native', 'ts3native'))) assert(nativeHooks().length > 50, 'ts3native hook addresses');
  console.log('compat-scan self-check ok');
}

if (import.meta.filename === process.argv[1] || /compat-scan\.ts$/.test(process.argv[1] ?? '')) {
  if (argv.includes('--self-check')) selfCheck();
  else { cg = await import('./managed-callgraph.ts'); run(); }
}
