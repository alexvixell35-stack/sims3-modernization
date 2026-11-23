// Compatibility scan (PLAN.md "Compatibility track" step 1): what other mods touch that our mods also touch. Read-only.
//   node tools/compat-scan.ts [<folder>...] [--json] [--matrix docs/data/compat-matrix.csv]
//        [--game "<Steam>\The Sims 3"] [--asm game-copy/assemblies] [--ts3fixes build/ts3fixes.package]
//        [--ini game-copy/ts3native.ini.hf031] [--ea-ini game-copy/ts3native-ea169.ini.hf031]   (release inis: native gates, IniUnseen)
//   node tools/compat-scan.ts --self-check
// Folders: a Mods folder or a folder of mod archives / packages, scanned recursively (default game-copy/compat-mods and
// <Documents>\Electronic Arts\The Sims 3\Mods\Packages; env TS3_USER / TS3_GAME as in offload-override-scan.ts).
// Lists (a) every game method our ts3fixes patches (static scan of mods/ts3fixes/src Patch(...) calls, with the feature =
// source file and the ShortcutOn switches in it; AutShadow's Hook(failed, ...) swaps too), each marked live or off with the
// release defaults (switchDefaults: Fixes.SwDefault -> Instantiator / build-fixes.ts TUNING_XML / ini), and every ts3native hook address (tools/hook-sites.ts scan of
// native/ts3native/*.cpp, with its module's gate in the Steam and EA release inis; both only when the repo sources are there); (b) per mod file: script assemblies (name, MVID),
// mod types that derive from a patched type or override a patched method, replaced game modules, resource keys shared
// with ts3fixes.package; (c) a verdict: no overlap / overlap (a live feature, switch) / overlap, off by default (only features
// whose switches are off) / unknown (archive not readable, ASI).
// Archives: .package directly, .zip read in memory (tools/mts-zip.ts unzip), .rar / .7z = "unpack needed" unless <archive>.unpacked/ sits next to it.
// Game assemblies for the type hierarchy: --asm (default game-copy/assemblies), else the S3SA images of <game>\Game\Bin\*.package.
import { closeSync, existsSync, mkdtempSync, openSync, readdirSync, readFileSync, readSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { homedir, tmpdir } from 'node:os';
import { basename, extname, join, relative } from 'node:path';
import { moduleName, mvidOf, readIndex, refpack, s3saImages } from './extract-assemblies.ts';
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
export type Target = { type: string; method: string; feature: string; switches: string[]; where: string; hook?: string; on?: boolean }; // on: live with the release defaults

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
  if ((m = /^typeof\(([\w.]+)\)$/.exec(e))) return new RegExp(`^using\\s+${m[1]}\\s*=\\s*([\\w.]+)\\s*;`, 'm').exec(src)?.[1] ?? m[1]; // using alias
  if (/^\w+$/.test(e) && depth < 3) { // local: Type x = <expr>;
    const d = new RegExp(`\\b${e}\\s*=\\s*([^;]+);`).exec(src);
    if (d) return typeExpr(d[1].trim(), src, depth + 1);
  }
  return null;
}

/** Patch(<type>, "<method>", ...) calls of one ts3fixes source file, and AutShadow's Hook(<failed list>, <type>, "<method>", ...). */
export function patchCalls(src: string, feature: string): { targets: Target[]; dynamic: number } {
  const code = src.replace(/\/\/.*$/gm, '');
  const switches = [...new Set([...code.matchAll(/ShortcutOn\("(\w+)"\)/g)].map((x) => x[1]))].sort();
  const targets: Target[] = [];
  let dynamic = 0;
  for (const m of code.matchAll(/\b(Patch|Hook)\(/g)) {
    if (/\bbool\s+$|\bstatic\s+\w+\s+$/.test(code.slice(Math.max(0, m.index - 30), m.index))) continue; // the helper's own definition
    let at = m.index + m[1].length + 1;
    if (m[1] === 'Hook') { // Hook(failed, typeof(T), "M", ...): other Hook(...) calls (e.g. Career hooks) are not swaps
      const [lst, e] = firstArg(code, at);
      if (!/^\w+$/.test(lst) || code[e] !== ',' || !/^\s*(typeof\(|\w+\s*,\s*")/.test(code.slice(e + 1))) continue;
      at = e + 1;
    }
    const [a1, end] = firstArg(code, at);
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
  if (!existsSync(dir)) return { ...all, defaults: new Map<string, boolean>() };
  const srcs = readdirSync(dir).filter((n) => n.endsWith('.cs')).sort().map((f) => [f.replace(/\.cs$/, ''), readFileSync(join(dir, f), 'utf8')]);
  for (const [feat, src] of srcs) {
    const r = patchCalls(src, feat);
    all.targets.push(...r.targets); all.dynamic += r.dynamic;
    all.usings.set(feat, [...src.matchAll(/^using\s+([\w.]+)\s*;/gm)].map((x) => x[1]));
    all.named.push(...namedTypes(src, feat));
  }
  hookSwitches(all.targets, srcs.map(([, s]) => s).join('\n'));
  const get = (n: string) => srcs.find(([f]) => f === n)?.[1] ?? '';
  const tuning = existsSync(BUILD_FIXES) ? readFileSync(BUILD_FIXES, 'utf8') : '';
  const iniText = existsSync(INI) ? readFileSync(INI, 'utf8') : '';
  const defs = switchDefaults(get('Fixes'), get('Instantiator'), tuning, iniText);
  for (const t of [...all.targets, ...all.named]) t.on = featureOn(t.switches, defs);
  return { ...all, defaults: defs };
}

const BUILD_FIXES = join(REPO, 'tools', 'setup', 'build-fixes.ts');
const INI = opt('--ini', join(REPO, 'game-copy', 'ts3native.ini.hf031'));        // v0.3.1 Steam release ini
const EA_INI = opt('--ea-ini', join(REPO, 'game-copy', 'ts3native-ea169.ini.hf031')); // v0.3.1 EA app 1.69 ini

/** ini text -> "section.key" (lower case) -> value; `;` comments dropped. */
export function parseIni(text: string): Map<string, string> {
  const out = new Map<string, string>();
  let sec = '';
  for (const raw of text.split(/\r?\n/)) {
    const l = raw.replace(/\s*;.*$/, '').trim();
    const s = /^\[([^\]]+)\]$/.exec(l);
    if (s) { sec = s[1].trim().toLowerCase(); continue; }
    const kv = /^([^=]+?)\s*=\s*(.*)$/.exec(l);
    if (kv) out.set(`${sec}.${kv[1].toLowerCase()}`, kv[2]);
  }
  return out;
}

/** Release default of every ts3fixes switch name, derived the way ShortcutOn answers with no test override: Fixes.SwDefault's
 *  case -> return expression (true / false / Instantiator.kX / IniUnseen(), joined by ||), kX = its initializer in
 *  Instantiator.cs unless build-fixes.ts TUNING_XML sets it, IniUnseen() = ts3native ini [AutShadow] Unseen=1.
 *  A name with no case is off (SwDefault's default). */
export function switchDefaults(fixesSrc: string, instSrc: string, buildFixesSrc: string, iniText: string): Map<string, boolean> {
  const tun = new Map<string, boolean>();
  for (const m of instSrc.matchAll(/static\s+bool\s+(k\w+)\s*=\s*(true|false)\s*;/g)) tun.set(m[1], m[2] === 'true');
  const xml = /const TUNING_XML = `([\s\S]*?)`;/.exec(buildFixesSrc)?.[1] ?? '';
  for (const m of xml.matchAll(/<(k\w+)\s+value="(True|False)"/g)) tun.set(m[1], m[2] === 'True');
  const unseen = parseIni(iniText).get('autshadow.unseen') === '1';
  const body = /static bool SwDefault\(string name\)[\s\S]*?default:\s*return false;/.exec(fixesSrc)?.[0] ?? '';
  const term = (t: string) => t === 'true' ? true : t === 'false' ? false : t === 'IniUnseen()' ? unseen
    : /^Instantiator\.(k\w+)$/.test(t) ? tun.get(t.slice(13)) ?? false : false; // unknown term: off (conservative for "default on")
  const out = new Map<string, boolean>();
  let names: string[] = [];
  for (const m of body.replace(/\/\/.*$/gm, '').matchAll(/case\s+"(\w+)"\s*:|return\s+([^;]+);/g)) {
    if (m[1]) { names.push(m[1]); continue; }
    const v = m[2].split('||').some((t) => term(t.trim()));
    for (const n of names) out.set(n, v);
    names = [];
  }
  return out;
}

/** A patch is live by default when its feature has no switch (always on) or any of its switches is on by default. */
export const featureOn = (switches: string[], defs: Map<string, boolean>) => !switches.length || switches.some((s) => defs.get(s) === true);

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

export type Gate = 'on' | 'off' | 'ungated';
/** Whether a ts3native module installs with an ini: its own reads of an `Enabled` or `Mode` key (ini value, else the read's
 *  default) decide; a module that reads neither is gated by a caller or always on ('ungated').
 *  shortcut: inferred from the module's own ini reads, not from the install order; upgrade if a module's gate lives elsewhere. */
export function moduleGate(cpp: string, ini: Map<string, string>): Gate {
  const reads = [...cpp.replace(/\/\/.*$/gm, '').matchAll(/GetPrivateProfileIntW\(L"([^"]+)",\s*L"(Enabled|Mode)",\s*([^,]+),/g)];
  if (!reads.length) return 'ungated';
  return reads.some((r) => (parseInt(ini.get(`${r[1].toLowerCase()}.${r[2].toLowerCase()}`) ?? r[3], 10) || 0) !== 0) ? 'on' : 'off';
}

export function nativeHooks(dir = join(REPO, 'native', 'ts3native'), iniText = '') {
  if (!existsSync(dir)) return [];
  const ini = parseIni(iniText);
  const hits = readdirSync(dir).filter((f) => f.endsWith('.cpp') && !/test/.test(f)).flatMap((f) => {
    const text = readFileSync(join(dir, f), 'utf8'), gate = moduleGate(text, ini);
    return hookScan(text, f.replace(/\.cpp$/, '')).map((h) => ({ ...h, gate }));
  });
  return [...new Map(hits.sort((a, b) => +(a.gate === 'on') - +(b.gate === 'on')).map((h) => [h.addr, h])).values()] // an 'on' module wins a shared address
   .sort((a, b) => a.addr - b.addr)
    .map((h) => ({ addr: `0x${h.addr.toString(16).toUpperCase().padStart(8, '0')}`, module: h.module, label: h.label, gate: h.gate }));
}

// ---------- (b) per mod file ----------
// chain = base types, nearest first; hides = name(params) of every method the type declares that is no override (non-virtual or newslot); baseCalls = names of its methods whose IL
// calls the same-named method of a type in its chain (undefined: IL not read); ilUnread = methods whose IL did not decode
// statics = name(params) of every static method the type declares (static-copy rule)
export type ModType = { asm: string; full: string; chain: string[]; virtuals: string[]; hides?: string[]; baseCalls?: string[]; ilUnread?: string[]; statics?: string[]; forwards?: string[]; staticCalls?: Record<string, string[]> };
// staticCalls = name(params) of a static -> the Type::method its IL calls (only statics named like a patched method)
export type Resolved = Target & { full: string; module: string; virtual: boolean; sigs?: string[]; calls?: string[] }; // sigs: params of the game's overloads; calls: Type::method its overloads' IL calls
// Rule facts (docs/328): base = the override calls the base method; hides = a same-named non-override method; current / changed =
// replaced module built for this game version / the mod changed this patched method; same / script / tuning = same-key content;
// standDown / unguarded = an ASI we step aside for / one that hooks our addresses with no stand-down. undefined = not read.
export type Finding = { kind: 'replaces' | 'named' | 'overrides' | 'derives' | 'key' | 'copies'; text: string; feature?: string; switches?: string[]; on?: boolean;
  base?: boolean; hides?: boolean; current?: boolean; changed?: boolean; same?: boolean; script?: boolean; tuning?: 'disjoint' | 'overlap'; standDown?: boolean; unguarded?: string };
export type ModuleFacts = { current: boolean; missing: number; changed: Set<string> };

/** Short / dotted / nested names from Patch calls -> game types (full name, module, virtual method), using the file's usings. */
export function resolveTargets(targets: Target[], usings: Map<string, string[]>,
  game: { full: string; module: string; methods: { name: string; virtual: boolean; params?: string }[] }[]): { resolved: Resolved[]; unresolved: Target[] } {
  const resolved: Resolved[] = [], unresolved: Target[] = [];
  for (const t of targets) {
    const want = t.type.replace(/\+/g, '.');
    let c = game.filter((g) => { const f = g.full.replace(/\+/g, '.'); return f === want || f.endsWith(`.${want}`); });
    const withM = c.filter((g) => g.methods.some((m) => m.name === t.method));
    if (withM.length) c = withM;
    if (c.length > 1) { const u = usings.get(t.feature) ?? []; const inU = c.filter((g) => u.some((n) => g.full.startsWith(`${n}.`) && !g.full.slice(n.length + 1).includes('.'))); if (inU.length) c = inU; }
    if (!c.length) { unresolved.push(t); continue; }
    for (const g of c) resolved.push({ ...t, full: g.full, module: g.module, virtual: g.methods.some((m) => m.name === t.method && m.virtual),
      sigs: g.methods.filter((m) => m.name === t.method && m.params !== undefined).map((m) => m.params!) });
  }
  return { resolved, unresolved };
}

/** What one mod's types and assemblies do to the patched targets. */
export function typeFindings(types: ModType[], replaced: string[], patched: Resolved[], named: Target[] = [], modules = new Map<string, ModuleFacts>()): Finding[] {
  const out: Finding[] = [];
  for (const t of types) for (const n of named.filter((x) => x.type === t.full))
    out.push({ kind: 'named', text: `ts3fixes ${n.feature} looks up ${t.full} by name (hook or stand-down)`, feature: n.feature, switches: n.switches, on: n.on });
  for (const mod of replaced) for (const p of patched.filter((x) => x.module === mod)) {
    const m = modules.get(mod), changed = m ? m.changed.has(`${p.full}::${p.method}`) : undefined;
    out.push({ kind: 'replaces', text: `replaces ${mod} (patched ${p.full}::${p.method}${m && !m.current ? ', copy for an older patch' : changed ? ', changed by the mod' : ''})`,
      feature: p.feature, switches: p.switches, on: p.on, current: m?.current, changed });
  }
  for (const t of types) for (const p of patched) {
    if (!t.chain.includes(p.full)) continue;
    const base = t.ilUnread?.includes(p.method) || !t.baseCalls ? undefined : t.baseCalls.includes(p.method);
    if (p.virtual && t.virtuals.includes(p.method)) out.push({ kind: 'overrides', text: `${t.full} overrides ${p.full}::${p.method}${base === false ? ' (no base call)' : ''}`, feature: p.feature, switches: p.switches, on: p.on, base });
    else { // a same-named non-override with one of the game overloads' params hides a non-virtual patched method (no sigs known:
      // by name); a newslot copy of a virtual one is not hiding here: the game's virtual calls still dispatch to the patched base
      const hides = !p.virtual && t.hides?.some((h) => h.startsWith(`${p.method}(`) && (!p.sigs?.length || p.sigs.includes(h.slice(p.method.length + 1, -1)))); out.push({ kind: 'derives', text: `${t.full} ${hides ? 'hides' : 'derives'} ${p.full} (patched ${p.method})`, feature: p.feature, switches: p.switches, on: p.on, hides }); }
  }
  // static-copy (docs/328): a static method with the patched method's name and its params (an instance method's copy takes the
  // declaring type first), on a type outside the patched type's chain, that does not call a same-named method (a wrapper such as
  // a mod's own LocalizeString forwards to the game's, so our patch runs), e.g. NRaas Overwatch's HarvestPlantEx.DoHarvest.
  // shortcut: name + signature only, no IL similarity; upgrade if a same-named static helper turns up as a false hit.
  for (const t of types) for (const p of patched) {
    if (t.full === p.full || t.chain.includes(p.full) || !p.sigs?.length) continue;
    const want = new Set(p.sigs.flatMap((g) => [...(g ? [`${p.method}(${g})`] : []), `${p.method}(${p.full}${g ? ',' + g : ''})`])); // no-param statics (Startup(), Shutdown()) are too common to count
    // call-graph match: when both call lists are read, the copy must make at least half of the game method's calls
    const like = (x: string) => { const c = t.staticCalls?.[x]; if (!c || !p.calls?.length) return true; const m = new Set(c); return p.calls.filter((k) => m.has(k)).length * 2 >= p.calls.length; };
    if (t.statics?.some((x) => want.has(x) && !t.forwards?.includes(x) && like(x))) out.push({ kind: 'copies', text: `${t.full} has a static copy of ${p.full}::${p.method}`, feature: p.feature, switches: p.switches, on: p.on });
  }
  return out;
}

/** ASI plugins ts3native knows by name: a stand-down instead of an unknown verdict, plus what still shares our hooks. */
export function asiFinding(file: string): Finding[] {
  if (/^sims3monomodder\.asi$/i.test(basename(file))) return [
    { kind: 'named', text: 'ts3native detects Sims3MonoModder.asi and stands down [MonoJit], [MonoFast] CallPath, [AutShadow] (docs/291 §6)', feature: 'S3mmStandDown', switches: ['[Compat] S3mmStandDown'], standDown: true },
    { kind: 'named', text: 'S3MM profiler (always installed) hooks Mono 0xE525C0 / 0xE59FD0 that ts3native also hooks, no stand-down (docs/291 §11)', feature: 'S3mmStandDown', unguarded: 'S3MM profiler' }];
  return [];
}

// ---------- (d) compatibility rules (docs/328, docs/data/compat-rules.json) ----------
export type Outcome = 'compatible' | 'caveat' | 'incompatible' | 'not decided';
export type Rule = { id: string; kind: string; condition: string; outcome: Outcome | 'flag'; wording: string; evidence: string; counterExamples: string[] };
export const RULES: Rule[] = (() => { try { return JSON.parse(readFileSync(join(REPO, 'docs', 'data', 'compat-rules.json'), 'utf8')).rules; } catch { return []; } })();
const OUTCOME_RANK: Record<Outcome, number> = { compatible: 0, caveat: 1, 'not decided': 2, incompatible: 3 };
export const outcomeOf = (id: string): Outcome => { const o = RULES.find((r) => r.id === id)?.outcome; return !o || o === 'flag' ? 'not decided' : o; };

/** The rule one finding falls under. Order matters: an old core module is incompatible whatever our switches say; an ASI that
 *  hooks our addresses with no stand-down too; then features off in the release; then the kind's own facts. */
export function findingRule(f: Finding): string {
  if (f.kind === 'replaces' && f.current === false) return 'module-old';
  if (f.unguarded) return 'asi-unguarded';
  if (f.on === false) return 'off-by-default';
  switch (f.kind) {
    case 'derives': return f.hides ? 'derives-hides' : 'derives';
    case 'overrides': return f.base === true ? 'override-calls-base' : f.base === false ? 'override-no-base' : 'override-unread';
    case 'replaces': return f.current === undefined ? 'module-unread' : f.changed ? 'module-changed' : 'module-current';
    case 'named': return f.standDown ? 'asi-standdown' : 'named';
    case 'copies': return 'static-copy';
    case 'key': return f.same === undefined ? 'key-unread' : f.same ? 'key-same' : f.script ? 'key-script'
      : f.tuning === 'overlap' ? 'tuning-overlap' : f.tuning === 'disjoint' ? 'tuning-disjoint' : 'key-different';
  }
}

const OWN_RE = /^(known-bad|hang-seen|audit-confirmed): /;
/** Rule column: the worst outcome over the findings, then the rule ids (worst first, count when > 1); `own issues` when the
 *  known-bad list (incl. audit-confirmed crash / hang findings, docs/318) names the mod: separate from compatibility. */
export function rowRule(u: { findings: Finding[]; unknown?: string; extras?: string[] }): string {
  const ids = u.unknown ? ['unknown'] : u.findings.length ? u.findings.map(findingRule) : ['no-overlap'];
  const n = new Map<string, number>(); for (const i of ids) n.set(i, (n.get(i) ?? 0) + 1);
  const order = [...n].sort((a, b) => OUTCOME_RANK[outcomeOf(b[0])] - OUTCOME_RANK[outcomeOf(a[0])] || b[1] - a[1] || a[0].localeCompare(b[0]));
  const worst = outcomeOf(order[0][0]);
  const own = (u.extras ?? []).some((e) => OWN_RE.test(e)) ? '; own issues' : '';
  return `${worst}: ${order.map(([i, c]) => c > 1 ? `${i} x${c}` : i).join(', ')}${own}`;
}

/** Mod-vs-mod pairs (two other mods, as Apex ModConflicts reports them): the rule for one reported pair. Same-key pairs use
 *  keyFinding + findingRule like ours. */
export type Pair = { kind: 'core'; sameModule: boolean; current: [boolean, boolean] } | { kind: 'assembly'; identical: boolean }
  | { kind: 'interaction' } | { kind: 'hook'; coordinated: boolean } | { kind: 'area' };
export function pairRule(p: Pair): string {
  switch (p.kind) {
    case 'core': return !p.current[0] || !p.current[1] ? 'module-old' : p.sameModule ? 'pair-core-same-module' : 'pair-core-other-module';
    case 'assembly': return p.identical ? 'pair-identical' : 'pair-assembly-copies';
    case 'interaction': return 'pair-interaction';
    case 'hook': return p.coordinated ? 'asi-standdown' : 'pair-hook-conflict';
    case 'area': return 'pair-area-hint';
  }
}

/** Does a method body call the same-named method of a type in its base chain (`base.M()`, IL `call`)? Calls as [opcode, owner, name]. */
export const callsBase = (calls: [number, string, string][], method: string, chain: string[]) =>
  calls.some(([op, owner, name]) => op === 0x28 && name === method && chain.includes(owner));

// Compiler-generated names differ between two compiles of one source (as tools/mod-method-diff.ts folds them).
export const foldGenerated = (s: string) => s.replace(/<PrivateImplementationDetails>\{[0-9A-Fa-f-]+\}(::\$\$method0x[0-9a-f]+-\d+)?/g, (_, sw) => '<PrivateImplementationDetails>' + (sw ? '::$$switch' : ''))
  .replace(/(CachedAnonymousMethodDelegate|<>c__DisplayClass|CS\$<>8__locals|>b__|>c__Iterator|>d__)[0-9a-f]+/g, '$1#');
/** A replaced game module vs the game's own: built for this game version when it has every type::method the game's has
 *  (compiler-generated names left out; the assembly version is 1.0.0.0 in every patch, so it cannot tell). Changed = patched
 *  type::method keys whose IL shapes (all overloads, folded) differ or are missing. Members: key -> sorted shapes ('' = not shaped). */
export function moduleFacts(game: Map<string, string[]>, mod: Map<string, string[]>, patched: string[]): ModuleFacts {
  let missing = 0;
  for (const k of game.keys()) if (!k.includes('<') && !mod.has(k)) missing++;
  const changed = new Set(patched.filter((k) => game.has(k) && (game.get(k)!.join('\n') !== (mod.get(k) ?? []).join('\n'))));
  return { current: missing === 0, missing, changed };
}

/** Same resource key in two packages: identical bytes, a script (S3SA) clash, or tuning fields that overlap or not. */
export function keyFinding(key: string, a: Buffer, b: Buffer, base?: Buffer): Finding {
  const type = parseInt(key.slice(0, 8), 16), same = a.equals(b);
  const f: Finding = { kind: 'key', text: `same resource key as ts3fixes.package ${key}${same ? ' (same bytes)' : ''}`, same };
  if (!same && type === TYPE_S3SA) f.script = true;
  if (!same && TUNING.has(type)) f.tuning = tuningOverlap(a.toString('utf8'), b.toString('utf8'), base?.toString('utf8'));
  return f;
}

// One XML token: a tag (close mark, name, attributes, self-close) or text.
export const XML_TOKEN_RE = /<(\/?)([\w:.-]+)((?:\s+[\w:.-]+\s*=\s*"[^"]*")*)\s*(\/?)>|<[?!][^>]*>|([^<]+)/g;
export const XML_ATTR_RE = /([\w:.-]+)\s*=\s*"([^"]*)"/g;
/** Tuning XML -> leaf field path (tag[i]/tag[j]@attr or .../#text) -> value. */
export function xmlFields(xml: string): Map<string, string> {
  const out = new Map<string, string>(), stack: string[] = [], seen: Map<string, number>[] = [new Map()];
  for (const m of xml.matchAll(XML_TOKEN_RE)) {
    if (m[5] !== undefined) { if (m[5].trim() && stack.length) out.set(`${stack.join('/')}#text`, m[5].trim()); continue; }
    if (!m[2]) continue; // <?xml ...?>, comments
    if (m[1]) { stack.pop(); seen.pop(); continue; }
    const sib = seen[seen.length - 1], i = sib.get(m[2]) ?? 0; sib.set(m[2], i + 1);
    stack.push(`${m[2]}[${i}]`);
    for (const a of (m[3] ?? '').matchAll(XML_ATTR_RE)) out.set(`${stack.join('/')}@${a[1]}`, a[2]);
    if (m[4]) stack.pop(); else seen.push(new Map());
  }
  return out;
}
/** Disjoint = no field both sides change. With the game's base: each side's changes vs base. Without: any field both set to
 *  different values (shortcut: no base means a field only one side has counts as that side's own; upgrade when a game base is read). */
export function tuningOverlap(a: string, b: string, base?: string): 'disjoint' | 'overlap' {
  const fa = xmlFields(a), fb = xmlFields(b);
  if (base !== undefined) {
    const f0 = xmlFields(base), ch = (f: Map<string, string>) => new Set([...new Set([...f.keys(), ...f0.keys()])].filter((k) => f.get(k) !== f0.get(k)));
    const ca = ch(fa), cb = ch(fb);
    return [...ca].some((k) => cb.has(k) && fa.get(k) !== fb.get(k)) ? 'overlap' : 'disjoint';
  }
  return [...fa].some(([k, v]) => fb.has(k) && fb.get(k) !== v) ? 'overlap' : 'disjoint';
}

/** A matrix row this scan did not read (another scan, e.g. a player's folder): findings rebuilt from its evidence text. Facts the
 *  text does not carry (base call, module build, key content) stay unread, so those rows get the *-unread rules. */
export const EVIDENCE_RES: [Finding['kind'], RegExp][] = [
  ['replaces', /^replaces \w+ \(patched [^)]+\)$/], ['named', /^ts3fixes \w+ looks up \S+ by name/], ['key', /^same resource key as ts3fixes\.package \S+/],
  ['overrides', /^\S+ overrides \S+::\w+/], ['derives', /^\S+ (derives|hides) \S+ \(patched \w+\)$/], ['copies', /^\S+ has a static copy of \S+::\w+$/]];
export function findingsFromEvidence(evidence: string, verdict: string, mod: string): Finding[] {
  if (/^sims3monomodder$/i.test(mod)) return asiFinding(`${mod}.asi`);
  const off = verdict === 'overlap, off by default';
  return evidence.replace(/^\[[^\]]*\]\s*/, '').split('; ').flatMap((t): Finding[] => {
    const k = EVIDENCE_RES.find(([, re]) => re.test(t))?.[0];
    if (!k) return [];
    return [{ kind: k, text: t, ...(off ? { on: false } : {}), ...(k === 'overrides' && /\(no base call\)/.test(t) ? { base: false } : {}), ...(k === 'derives' && /^\S+ hides /.test(t) ? { hides: true } : {}) }];
  });
}
type Unit = { extras?: string[]; size?: number; mod: string; file: string; kind: Set<string>; asms: { name: string; mvid: string }[]; findings: Finding[]; unknown?: string; keys: Set<string>;
  replaced?: string[]; replacedImgs?: Map<string, Buffer>; keyData?: Map<string, Buffer> };
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
// overlap = touches a feature that is live with the release defaults; 'overlap, off by default' = only features whose switches are off
export const verdict = (u: Pick<Unit, 'findings' | 'unknown'>) => {
  if (u.unknown) return 'unknown';
  const f = u.findings.filter((x) => x.kind !== 'derives' && x.kind !== 'copies'); // copies: rule column only (verdict as before docs/328)
  return f.some((x) => x.on !== false) ? 'overlap' : f.length ? 'overlap, off by default' : 'no overlap';
};
export const VERDICTS = ['no overlap', 'overlap', 'overlap, off by default', 'unknown'];
const overlapText = (u: Unit) => {
  const by = new Map<string, Set<string>>(), live = new Set<string>();
  for (const f of u.findings.filter((f) => f.kind !== 'derives' && f.kind !== 'copies')) {
    const k = f.feature ?? 'resource', s = by.get(k) ?? new Set();
    f.switches?.forEach((x) => s.add(x)); by.set(k, s); if (f.on !== false) live.add(k);
  }
  return [...by].map(([k, s]) => `${k}${s.size ? ` (${[...s].sort().join('/')})` : ''}${live.has(k) ? '' : ' [off by default]'}`).join('; ');
};
const RANK = { replaces: 0, named: 1, key: 2, overrides: 3, copies: 3, derives: 4 };
const evidenceText = (u: Unit) => {
  // the findings that decide the rule column first (worst outcome), then by kind, so the 12 shown carry the reason
  const worse = (x: Finding) => OUTCOME_RANK[outcomeOf(findingRule(x))];
  const f = [...u.findings].sort((a, b) => worse(b) - worse(a) || RANK[a.kind] - RANK[b.kind]);
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

export type KnownBad = { name?: string; key?: string; status: 'known-bad' | 'outdated' | 'hang-seen' | 'audit-confirmed'; reason: string; source?: string };
const GB = 1024 ** 3;
/** Player-folder extras (docs/23 §8): entries of the known-bad list that match the file name or a resource key, and the package
 *  size check (same limits as tools/setup/content-health.ts: > 2 GB warns, >= 4 GB is over the package format cap).
 *  shortcut: size is of the file on disk, a mod inside a zip is not size-checked; upgrade when a zipped merge shows up. */
export function modExtras(mod: string, size: number, keys: Iterable<string>, list: KnownBad[]): string[] {
  const out: string[] = [];
  const ks = new Set(keys);
  for (const e of list) {
    let hit = false;
    if (e.name) { try { hit = new RegExp(e.name, 'i').test(mod); } catch { /* bad pattern in the list: no match */ } }
    if (!hit && e.key) hit = ks.has(e.key.toLowerCase());
    if (hit) out.push(`${e.status}: ${e.reason}${e.source ? ` (${e.source})` : ''}`);
  }
  if (size >= 4 * GB) out.push(`${(size / GB).toFixed(2)} GB: over the 4 GB package format cap, unreadable`);
  else if (size > 2 * GB) out.push(`${(size / GB).toFixed(2)} GB: merged package over 2 GB, split it`);
  return out;
}
/** CC String Merge (SmellyMods, docs/304) output packages, by the names its source writes. */
export const CCSM_RE = /^CCStringMerge_(Strings|Merged_\d{3})$/i;
export function ccStringMergeNote(mod: string): string | undefined {
  const m = CCSM_RE.exec(mod);
  if (!m) return undefined;
  return m[1].toLowerCase() === 'strings'
    ? 'CC String Merge strings package: the STBLs of the processed CC, one per language (docs/304); offline tool, no overlap'
    : 'CC String Merge merged part (--merge): many CC packages in one file, so known-bad matches by file name cannot hit; keys still do (docs/304)';
}
const TYPE_STBL = 0x220557da;
const isStbl = (k: string) => k.startsWith('220557da:');
/** STBL keys of a package that are also game STBL keys: a by-key replacement of an EA string table. */
export function stblOverrides(keys: Iterable<string>, gameStbl: Set<string>): string[] {
  return [...keys].filter((k) => isStbl(k) && gameStbl.has(k)).sort();
}
export function stblOverrideNote(hits: string[]): string | undefined {
  if (!hits.length) return undefined;
  return `replaces ${hits.length} EA string table(s) by key (${hits.slice(0, 2).join(', ')}${hits.length > 2 ? ', ...' : ''}): `
    + 'CC String Merge renumbers them, so the EA table loads too: the mod text still wins while Mods outrank the game, keys the mod lacks show EA text instead of blank (docs/304 C1)';
}
const loadKnownBad = (f: string): KnownBad[] => { try { return JSON.parse(readFileSync(f, 'utf8')).entries ?? []; } catch { return []; } };

const MATRIX_COLS = ['mod', 'file', 'kind', 'overlap', 'verdict', 'evidence', 'status', 'note', 'rule'];
/** Rule column of a matrix row this scan did not read: from its evidence text (findingsFromEvidence) and the known-bad list. */
export const matrixRule = (mod: string, verdict: string, evidence: string, known: KnownBad[] = []) =>
  rowRule({ findings: findingsFromEvidence(evidence, verdict, mod), unknown: verdict === 'unknown' ? evidence || 'unknown' : undefined, extras: modExtras(mod, 0, [], known) });
/** The matrix after a scan: scanned rows keep the status and note of their old row (key: file); old rows of files this scan did
 *  not see stay as they are (other scans, e.g. a player's Mods folder), except rows under a root this scan read whole
 *  (`<root label>/...`): those files are gone or now left out (CC without overlap), so their rows go. A kept row without a
 *  rule gets one from its evidence text (matrixRule). The verdict column is never changed by the rules. */
export function matrixCsv(rows: { mod: string; file: string; kind: string; overlap: string; verdict: string; evidence: string; rule?: string }[], old: string, roots: string[] = [], known: KnownBad[] = []): string {
  const [head, ...oldRows] = parseCsv(old);
  const at = (r: string[], c: string) => r[head.indexOf(c)] ?? '';
  const scanned = new Set(rows.map((r) => r.file)), byFile = new Map(oldRows.map((r) => [at(r, 'file'), r]));
  const line = (r: string[]) => r.map((v) => JSON.stringify(v)).join(',');
  const fresh = rows.map((r) => { const o = byFile.get(r.file); return line([r.mod, r.file, r.kind, r.overlap, r.verdict, r.evidence, o ? at(o, 'status') : '', o ? at(o, 'note') : '', r.rule ?? '']); });
  const kept = oldRows.filter((r) => !scanned.has(at(r, 'file')) && !roots.some((x) => at(r, 'file').startsWith(`${x}/`)))
    .map((r) => line(MATRIX_COLS.map((c) => c === 'rule' ? at(r, c) || matrixRule(at(r, 'mod'), at(r, 'verdict'), at(r, 'evidence'), known) : at(r, c))));
  return `${MATRIX_COLS.join(',')}\n${[...fresh, ...kept].join('\n')}\n`;
}

/** Files of a zip, one nested zip level opened in place (`outer.zip!inner.zip/x.package`); folder entries ("x.zip/") skipped. */
export function zipEntries(buf: Buffer): [string, Buffer][] {
  return [...unzip(buf)].filter(([n]) => !n.endsWith('/')).flatMap(([n, b]): [string, Buffer][] =>
    /\.zip$/i.test(n) ? [...unzip(b)].filter(([m]) => !m.endsWith('/')).map(([m, c]) => [`${n}!${m}`, c]) : [[n, b]]);
}

function walk(dir: string): string[] {
  if (!existsSync(dir)) return [];
  return readdirSync(dir, { withFileTypes: true }).flatMap((e) => e.isDirectory() ? walk(join(dir, e.name)) : [join(dir, e.name)]);
}

/** One resource's bytes (RefPack-decompressed) from a package buffer or a package on disk. */
function resBytes(src: string | Buffer, e: { offset: number; fileSize: number; compressed: number }): Buffer {
  let raw: Buffer;
  if (Buffer.isBuffer(src)) raw = src.subarray(e.offset, e.offset + e.fileSize);
  else { const fd = openSync(src, 'r'); try { raw = Buffer.alloc(e.fileSize); readSync(fd, raw, 0, e.fileSize, e.offset); } finally { closeSync(fd); } }
  return e.compressed === 0xffff ? refpack(raw) : raw;
}
/** type::method -> folded IL shapes (only for keys; '' for the rest) plus type:: for every type, of one standalone assembly. */
function members(a: any, keys: Set<string>): Map<string, string[]> {
  const out = new Map<string, string[]>();
  for (const t of a.types) if (!out.has(`${t.full}::`)) out.set(`${t.full}::`, []);
  for (const m of a.methods) {
    const k = `${m.type.full}::${m.name}`;
    let s = ''; if (keys.has(k)) { try { s = foldGenerated(cg.ilShape(m)); } catch { s = 'unshaped'; } }
    out.set(k, [...(out.get(k) ?? []), s].sort());
  }
  return out;
}
/** Type::method targets of a method's call / callvirt / newobj instructions (throws when the IL does not decode). */
function callTargets(m: any): string[] {
  if (!m.rva || m.flags & 0x400 || m.impl & 0x1000 || m.flags & 0x2000) return [];
  const b = m.asm.buf, p = cg.rva(m.asm, m.rva);
  const ins = (b[p] & 3) === 2 ? cg.decodeIl(b, p + 1, b[p] >> 2) : cg.decodeIl(b, p + (b.readUInt16LE(p) >> 12) * 4, b.readUInt32LE(p + 4));
  return [...new Set(ins.filter((x: any) => x.op === 0x28 || x.op === 0x6f || x.op === 0x73).map((x: any) => {
    const r = cg.resolveMethodToken(m.asm, x.tok); if (r.m) return `${r.m.type.full}::${r.m.name}`;
    const s = /^(.+)::([^:(]+)/.exec(r.stub ?? ''); return s ? `${s[1]}::${s[2]}` : '';
  }).filter(Boolean))] as string[];
}
/** Which of a mod type's methods call their base method (IL `call` to the same name on a type in the chain). */
// forwards = name(params) of its static methods that call a same-named method of another type (a wrapper, no copy: static-copy rule)
function baseCallsOf(t: any, chain: string[]): { baseCalls: string[]; ilUnread: string[]; forwards: string[] } {
  const baseCalls: string[] = [], ilUnread: string[] = [], forwards: string[] = [];
  for (const m of t.methods) {
    if (!m.rva || m.flags & 0x400 || m.impl & 0x1000 || m.flags & 0x2000) continue; // abstract / internal call / pinvoke: no body
    try {
      const b = m.asm.buf, p = cg.rva(m.asm, m.rva);
      const ins = (b[p] & 3) === 2 ? cg.decodeIl(b, p + 1, b[p] >> 2) : cg.decodeIl(b, p + (b.readUInt16LE(p) >> 12) * 4, b.readUInt32LE(p + 4));
      const calls = ins.filter((x: any) => x.op === 0x28 || x.op === 0x6f).map((x: any): [number, string, string] => {
        const r = cg.resolveMethodToken(m.asm, x.tok);
        if (r.m) return [x.op, r.m.type.full, r.m.name];
        const s = /^(.+)::([^:(]+)/.exec(r.stub ?? ''); return [x.op, s?.[1] ?? '', s?.[2] ?? ''];
      });
      if (callsBase(calls.filter((c) => c[0] === 0x28), m.name, chain)) baseCalls.push(m.name);
      if (m.flags & 0x10 && calls.some((c) => c[2] === m.name && c[1] !== t.full)) forwards.push(`${m.name}(${m.params})`);
    } catch { ilUnread.push(m.name); }
  }
  return { baseCalls, ilUnread, forwards };
}

// ---------- main scan ----------
function run() {
  const pos = argv.filter((a, i) => !a.startsWith('--') && !(i && argv[i - 1].startsWith('--') && !['--json', '--self-check'].includes(argv[i - 1])));
  const roots = (pos.length ? pos : [join(REPO, 'game-copy', 'compat-mods'), join(USER, 'Mods', 'Packages')]).filter(existsSync);
  const label = (root: string, f: string) => `${basename(root)}/${relative(root, f).split('\\').join('/')}`;
  const ours = ourPatches();
  const readOr = (f: string) => existsSync(f) ? readFileSync(f, 'utf8') : '';
  const hooks = nativeHooks(undefined, readOr(INI)), eaHooks = nativeHooks(undefined, readOr(EA_INI));
  const gates = (h: { gate: Gate }[]) => Object.fromEntries((['on', 'ungated', 'off'] as Gate[]).map((g) => [g, h.filter((x) => x.gate === g).length]));

  // ts3fixes.package resource keys
  const fixPkg = opt('--ts3fixes', join(REPO, 'build', 'ts3fixes.package'));
  const fixEntries = existsSync(fixPkg) ? packageIndex(fixPkg) : [];
  const fixKeys = new Set(fixEntries.map(keyOf));

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

    const knownBad = loadKnownBad(opt('--known-bad', join(REPO, 'docs', 'data', 'known-bad-mods.json')));
    const units: Unit[] = [];
    const modDlls: { file: string; unit: Unit; name: string }[] = [];
    const addPackage = (u: Unit, src: string | Buffer, tag: string) => {
      u.kind.add('cc');
      let entries;
      try { entries = packageIndex(src); } catch (e) { u.unknown = `${tag}: not a readable package (${(e as Error).message})`; return; }
      if (entries.some((e) => TUNING.has(e.type))) { u.kind.delete('cc'); u.kind.add('tuning'); }
      for (const e of entries) { const k = keyOf(e); u.keys.add(k); if (fixKeys.has(k)) (u.keyData ??= new Map()).set(k, resBytes(src, e)); }
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
        if (gameMvid.has(name)) { if (gameMvid.get(name) !== mvid) { (u.replaced ??= []).push(name); (u.replacedImgs ??= new Map()).set(name, img); } continue; }
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
      if (ext === '.rar' && existsSync(`${firstVolume(f)}.unpacked`)) continue; // later volume of a multi-part .rar unpacked from part 1
      if ((ext === '.rar' || ext === '.7z') && existsSync(`${f}.unpacked`)) continue; // its unpacked copy (<archive>.unpacked/) is scanned instead
      if (ext === '.rar' || ext === '.7z') { u.unknown = `${ext.slice(1)} archive: unpack needed (tar -xf <archive> -C <archive>.unpacked)`; }
      else if (ext === '.asi') {
        u.kind.add('asi');
        const k = asiFinding(f);
        if (k.length) u.findings.push(...k); else u.unknown = 'ASI: native hooks, check it with the Apex Radiance mod conflict scan (hook table) or node tools/hook-sites.ts and docs/68';
      }
      else if (ext === '.zip') {
        try {
          for (const [n, b] of zipEntries(readFileSync(f))) {
            const e = extname(n).toLowerCase();
            if (e === '.package') addPackage(u, b, n);
            else if (e === '.asi') {
              u.kind.add('asi');
              const k = asiFinding(n);
              if (k.length) u.findings.push(...k); else u.unknown = 'ASI inside: native hooks, check it with the Apex Radiance mod conflict scan (hook table) or hook-sites.ts / docs/68';
            }
            else if (e === '.rar' || e === '.7z') u.unknown = `nested ${e.slice(1)} (${n}): unpack needed`;
          }
        } catch (e) { u.unknown = `zip not readable here (${(e as Error).message})`; }
      } else addPackage(u, f, basename(f));
      if (u.kind.has('ours')) continue;
      u.extras = modExtras(u.mod, statSync(f).size, u.keys, knownBad);
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
        methods: t.methods.map((m: any) => ({ name: m.name, virtual: !!(m.flags & M.VIRT), params: m.params })) }));
      ({ resolved, unresolved } = resolveTargets(ours.targets, ours.usings, game));
      const gameByFull = new Map(all.filter((t: any) => !isMod(t.asm.name)).map((t: any) => [t.full, t]));
      for (const r of resolved) { try { r.calls = [...new Set(((gameByFull.get(r.full) as any)?.methods ?? []).filter((m: any) => m.name === r.method).flatMap(callTargets))] as string[]; } catch { r.calls = undefined; } }
      const patchedNames = new Set(resolved.map((r) => r.method));
      const byFile = new Map(modDlls.map((d) => [d.file, d]));
      const perUnit = new Map<Unit, ModType[]>();
      for (const t of all.filter((t: any) => isMod(t.asm.name) && t.name !== '<Module>')) {
        const chain: string[] = []; for (let b = M.baseOf(t); b; b = M.baseOf(b)) chain.push(b.full);
        const d = byFile.get(t.asm.name)!;
        const il = baseCallsOf(t, chain);
        perUnit.set(d.unit, [...(perUnit.get(d.unit) ?? []), { asm: d.name, full: t.full, chain, hides: t.methods.filter((m: any) => !(m.flags & M.VIRT) || m.flags & M.NEWSLOT).map((m: any) => `${m.name}(${m.params})`), ...il,
          statics: t.methods.filter((m: any) => m.flags & 0x10).map((m: any) => `${m.name}(${m.params})`),
          staticCalls: Object.fromEntries(t.methods.filter((m: any) => m.flags & 0x10 && patchedNames.has(m.name)).flatMap((m: any) => { try { return [[`${m.name}(${m.params})`, callTargets(m)]]; } catch { return []; } })),
          virtuals: t.methods.filter((m: any) => m.flags & M.VIRT && !(m.flags & M.NEWSLOT)).map((m: any) => m.name) }]);
      }
      const gameMembers = new Map<string, Map<string, string[]>>();
      for (const u of units) {
        const modules = new Map<string, ModuleFacts>();
        for (const [name, img] of u.replacedImgs ?? []) {
          const keys = new Set(resolved.filter((r) => r.module === name).map((r) => `${r.full}::${r.method}`));
          try {
            if (!gameMembers.has(name)) gameMembers.set(name, members(cg.loadStandalone(join(tmp, `${name}.dll`), `${name}.dll`), keys));
            const f = join(tmp, `rep${units.indexOf(u)}_${name}.dll`); writeFileSync(f, img);
            modules.set(name, moduleFacts(gameMembers.get(name)!, members(cg.loadStandalone(f, `${name}.dll`), keys), [...keys]));
          } catch { /* not decodable: module-unread */ }
        }
        u.findings.push(...typeFindings(perUnit.get(u) ?? [], u.replaced ?? [], resolved, ours.named, modules));
      }
      if (skipped.length) modelNote = `unparsed assemblies: ${skipped.length}`;
    } else modelNote = gameDlls.size ? 'no ts3fixes source: patched methods unknown' : 'no game assemblies (--asm or --game): type checks skipped';
    // EA string tables a non-script package replaces by key (docs/304 C1); game indexes read only when such a package has STBLs
    const stblUnits = units.filter((u) => !u.kind.has('script') && [...u.keys].some(isStbl));
    if (stblUnits.length) {
      const gameStbl = new Set<string>();
      for (const f of walk(GAME).filter((n) => /\.package$/i.test(n) && !/[\\/]Caches[\\/]/i.test(n))) {
        try { for (const e of packageIndex(f)) if (e.type === TYPE_STBL) gameStbl.add(keyOf(e)); } catch { /* not a package */ }
      }
      for (const u of stblUnits) { const n = stblOverrideNote(stblOverrides(u.keys, gameStbl)); if (n) (u.extras ??= []).push(n); }
    }
    for (const u of units) {
      const ccsm = ccStringMergeNote(u.mod);
      if (ccsm) (u.extras ??= []).push(ccsm);
      for (const [k, b] of u.keyData ?? []) { const e = fixEntries.find((x) => keyOf(x) === k)!; u.findings.push(keyFinding(k, b, resBytes(fixPkg, e))); }
      if (!u.unknown && u.kind.has('script') && !gameDlls.size) u.unknown = 'script mod: no game assemblies to check against';
    }

    // ---------- output ----------
    const rows = units.map((u) => ({ mod: u.mod, file: u.file, kind: kindText(u), assemblies: u.asms, overlap: overlapText(u), verdict: verdict(u),
      evidence: u.unknown ?? evidenceText(u), extras: u.extras ?? [], rule: rowRule(u) }));
    const summary = {
      roots: roots.map((r) => basename(r)), files: rows.length, byVerdict: Object.fromEntries(VERDICTS.map((v) => [v, rows.filter((r) => r.verdict === v).length])),
      byOutcome: Object.fromEntries((['compatible', 'caveat', 'incompatible', 'not decided'] as Outcome[]).map((o) => [o, rows.filter((r) => r.rule.startsWith(`${o}:`)).length])),
      patchedMethods: ours.targets.length, resolved: new Set(resolved.map((r) => `${r.full}::${r.method}`)).size, unresolved: unresolved.map((t) => `${t.type}::${t.method}`),
      dynamicPatchCalls: ours.dynamic, nativeHookAddresses: hooks.length, nativeGateSteam: { ini: existsSync(INI) ? basename(INI) : 'none', ...gates(hooks) },
      nativeGateEa: { ini: existsSync(EA_INI) ? basename(EA_INI) : 'none', ...gates(eaHooks) },
      liveTargets: new Set(resolved.filter((r) => r.on).map((r) => `${r.full}::${r.method}`)).size,
      switchesOn: [...ours.defaults.values()].filter(Boolean).length, switchesKnown: ours.defaults.size, ts3fixesKeys: fixKeys.size, note: modelNote,
    };
    if (argv.includes('--json')) console.log(JSON.stringify({ summary, patched: resolved.map(({ full, method, feature, switches, where, on }) => ({ type: full, method, feature, switches, where, on })), nativeHooks: hooks, mods: rows }, null, 1));
    else {
      console.log(`(a) ts3fixes patches ${summary.resolved} game methods (${summary.liveTargets} live with the release defaults; ${ours.targets.length} Patch/Hook calls, ${ours.dynamic} built at run time, ${unresolved.length} unresolved; switches on by default ${summary.switchesOn} of ${summary.switchesKnown})`);
      console.log(`    ts3native names ${hooks.length} hook addresses (node tools/hook-sites.ts); by module gate, ${summary.nativeGateSteam.ini}: on ${summary.nativeGateSteam.on}, ungated ${summary.nativeGateSteam.ungated}, off ${summary.nativeGateSteam.off}; ${summary.nativeGateEa.ini}: on ${summary.nativeGateEa.on}, ungated ${summary.nativeGateEa.ungated}, off ${summary.nativeGateEa.off}`);
      console.log(`(b,c) ${rows.length} mod files in ${summary.roots.join(', ')}: ${Object.entries(summary.byVerdict).map(([k, v]) => `${k} ${v}`).join(', ')}${modelNote ? `; ${modelNote}` : ''}\n`);
      console.log(`rules (docs/328): ${Object.entries(summary.byOutcome).map(([k, v]) => `${k} ${v}`).join(', ')}\n`);
      console.log('| mod | kind | verdict | rule | overlap | evidence |\n|---|---|---|---|---|---|');
      for (const r of rows.filter((r) => r.verdict !== 'no overlap' || !r.rule.startsWith('compatible:'))) console.log(`| ${r.mod} | ${r.kind} | ${r.verdict} | ${r.rule} | ${r.overlap || '-'} | ${r.evidence.slice(0, 160)} |`);
      const flagged = rows.filter((r) => r.extras.length);
      if (flagged.length) { console.log('\nknown-bad / size / notes:'); for (const r of flagged) console.log(`- ${r.mod}: ${r.extras.join('; ')}`); }
      console.log(`\nno overlap: ${rows.filter((r) => r.verdict === 'no overlap').map((r) => r.mod).join(', ') || '-'}`);
    }
    const matrix = opt('--matrix', '');
    // CC-only packages without overlap stay out of the matrix: nothing to check, and a player's CC list is personal
    if (matrix) writeFileSync(matrix, matrixCsv(rows.filter((r) => r.kind !== 'cc' || r.verdict !== 'no overlap'), existsSync(matrix) ? readFileSync(matrix, 'utf8') : '', roots.map((r) => basename(r)), knownBad));
  } finally { rmSync(tmp, { recursive: true, force: true }); }
}

// managed-callgraph reads process.argv at load; import it only when the scan needs the type model.
let cg: any;
const await_import_callgraph = () => cg;

/** Part 1 of a multi-volume rar (x.part2.rar / x.part02.rar -> x.part1.rar / x.part01.rar); other files unchanged. */
export function firstVolume(f: string): string {
  const m = /^(.*\.part)(\d+)\.rar$/i.exec(f);
  return m && Number(m[2]) > 1 ? `${m[1]}${'1'.padStart(m[2].length, '0')}.rar` : f;
}

/** Fixtures for every rule of docs/data/compat-rules.json (no game or mod content): each id must come out of findingRule /
 *  pairRule / rowRule with the outcome the file gives it, and the file must name every id the code can return. */
function rulesCheck(assert: (c: unknown, m: string) => void) {
  const seen = new Set<string>();
  const want = (id: string, got: string, outcome: Outcome | 'flag') => {
    assert(got === id, `rule ${id}: got ${got}`);
    assert(RULES.find((r) => r.id === id)?.outcome === outcome, `rule ${id}: outcome in compat-rules.json is not ${outcome}`);
    seen.add(id);
  };
  const F = (x: Partial<Finding>): Finding => ({ kind: 'overrides', text: 't', ...x });
  // typeFindings carries the facts: base call, hide, module currency / change
  const pt: Resolved[] = [{ type: 'GoHome', method: 'Run', feature: 'Fixes', switches: [], where: '', full: 'G.GoHome', module: 'Sims3GameplaySystems', virtual: true, on: true },
    { type: 'Lot', method: 'Clean', feature: 'Fixes', switches: [], where: '', full: 'G.Lot', module: 'Sims3GameplaySystems', virtual: false, on: true, sigs: ['Sim', ''] }];
  const tf = typeFindings([
    { asm: 'M', full: 'M.A', chain: ['G.GoHome'], virtuals: ['Run'], hides: [], baseCalls: ['Run'], ilUnread: [] },
    { asm: 'M', full: 'M.B', chain: ['G.GoHome'], virtuals: ['Run'], hides: [], baseCalls: [], ilUnread: [] },
    { asm: 'M', full: 'M.C', chain: ['G.GoHome'], virtuals: ['Run'], hides: [], baseCalls: [], ilUnread: ['Run'] },
    { asm: 'M', full: 'M.F', chain: ['G.GoHome'], virtuals: [], hides: ['Run()'] }, // newslot copy of a virtual: game calls reach the base
    { asm: 'M', full: 'M.D', chain: ['G.Lot'], virtuals: [], hides: ['Clean(Sim)'] },
    { asm: 'M', full: 'M.E', chain: ['G.Lot'], virtuals: [], hides: ['Other(Sim)', 'Clean(int)', 'CleanUp(Sim)'] }], ['Sims3GameplaySystems'], pt, [],
    new Map([['Sims3GameplaySystems', { current: true, missing: 0, changed: new Set(['G.Lot::Clean']) }]]));
  const by = (s: string) => tf.find((x) => x.text.startsWith(s))!;
  want('override-calls-base', findingRule(by('M.A overrides')), 'compatible');
  want('override-no-base', findingRule(by('M.B overrides')), 'caveat');
  want('override-unread', findingRule(by('M.C overrides')), 'not decided');
  want('derives-hides', findingRule(by('M.D hides')), 'caveat');
  want('derives', findingRule(by('M.E derives')), 'compatible');
  // static-copy: a static with the patched name and params (instance copy: declaring type first); other names / params / the
  // patched type itself do not count
  const ptc: Resolved[] = pt.map((p) => p.method === 'Clean' ? { ...p, calls: ['G.X::A', 'G.X::B', 'G.Lot::Dirt', 'G.Y::C'] } : p);
  const sc = typeFindings([{ asm: 'M', full: 'M.Ex', chain: [], virtuals: [], statics: ['Clean(G.Lot,Sim)', 'Clean(int)'], staticCalls: { 'Clean(G.Lot,Sim)': ['G.X::A', 'G.X::B', 'G.Z::D'] } },
    { asm: 'M', full: 'M.Other', chain: [], virtuals: [], statics: ['Clean(G.Lot,int)', 'Cleaner(G.Lot,Sim)', 'Run()', 'Clean()'] },
    { asm: 'M', full: 'M.Wrap', chain: [], virtuals: [], statics: ['Clean(G.Lot,Sim)'], forwards: ['Clean(G.Lot,Sim)'] },
    { asm: 'M', full: 'M.Unlike', chain: [], virtuals: [], statics: ['Clean(G.Lot,Sim)'], staticCalls: { 'Clean(G.Lot,Sim)': ['G.X::A', 'G.Lot::Other'] } },
    { asm: 'M', full: 'G.Lot', chain: [], virtuals: [], statics: ['Clean(G.Lot,Sim)'] }], [], ptc);
  // M.Unlike: 1 of the game's 4 calls (below half): no copy; M.Ex: 2 of 4 (half): copy
  assert(sc.filter((x) => x.kind === 'copies').length === 1, `static-copy: one hit, got ${sc.map((x) => x.text)}`);
  want('static-copy', findingRule(sc.find((x) => x.kind === 'copies')!), 'caveat');
  assert(sc.find((x) => x.kind === 'copies')!.text === 'M.Ex has a static copy of G.Lot::Clean', 'static-copy text');
  assert(findingsFromEvidence('M.Ex has a static copy of G.Lot::Clean', 'overlap', 'x').map(findingRule).join() === 'static-copy', 'static-copy from evidence text');
  assert(findingRule(by('M.F derives')) === 'derives', 'a newslot copy of a virtual patched method is derives');
  want('module-current', findingRule(by('replaces Sims3GameplaySystems (patched G.GoHome')), 'compatible');
  want('module-changed', findingRule(by('replaces Sims3GameplaySystems (patched G.Lot')), 'caveat');
  assert(by('M.B overrides').text.endsWith('(no base call)') && !by('M.A overrides').text.includes('no base'), 'override text marks a missing base call');
  // base call in IL: `call` to the same name on a chain type; callvirt, other names and other types do not count
  assert(callsBase([[0x28, 'G.GoHome', 'Run']], 'Run', ['G.GoHome', 'G.Base']) && callsBase([[0x28, 'G.Base', 'Run']], 'Run', ['G.GoHome', 'G.Base'])
    && !callsBase([[0x6f, 'G.GoHome', 'Run']], 'Run', ['G.GoHome']) && !callsBase([[0x28, 'G.GoHome', 'Cleanup']], 'Run', ['G.GoHome'])
    && !callsBase([[0x28, 'X.Other', 'Run']], 'Run', ['G.GoHome']) && !callsBase([], 'Run', ['G.GoHome']), 'callsBase');
  // module facts: a missing type::method = older patch; compiler-generated names and changed shapes of unpatched methods do not count
  const g = new Map([['G.A::', []], ['G.A::M', ['s1']], ['G.A::P', ['p1']], ['G.<>c__DisplayClass1::', []]]);
  const cur = moduleFacts(g, new Map([['G.A::', []], ['G.A::M', ['s2']], ['G.A::P', ['p1']]]), ['G.A::P']);
  const chg = moduleFacts(g, new Map([['G.A::', []], ['G.A::M', ['s1']], ['G.A::P', ['p2']]]), ['G.A::P']);
  const old = moduleFacts(g, new Map([['G.A::', []], ['G.A::P', ['p1']]]), ['G.A::P']);
  assert(cur.current && !cur.changed.size && chg.current && chg.changed.has('G.A::P') && !old.current && old.missing === 1, `moduleFacts ${JSON.stringify([cur, chg, old].map((x) => [x.current, x.missing, [...x.changed]]))}`);
  want('module-old', findingRule(F({ kind: 'replaces', current: false, on: false })), 'incompatible'); // old module wins over off-by-default
  want('module-unread', findingRule(F({ kind: 'replaces' })), 'not decided');
  assert(foldGenerated('<>c__DisplayClass1f.x CS$<>8__locals2a <Run>b__3') === foldGenerated('<>c__DisplayClass7.x CS$<>8__locals9 <Run>b__ff'), 'compiler names fold');
  want('named', findingRule(F({ kind: 'named' })), 'compatible');
  // keys: same bytes, script, tuning fields disjoint / overlapping (with and without the game's base), other
  const K = (t: number) => `${t.toString(16).padStart(8, '0')}:00000000:0000000000000001`, B = (s: string) => Buffer.from(s);
  want('key-same', findingRule(keyFinding(K(0x2f7d0004), B('img'), B('img'))), 'compatible');
  want('key-different', findingRule(keyFinding(K(0x2f7d0004), B('img1'), B('img2'))), 'caveat');
  want('key-script', findingRule(keyFinding(K(TYPE_S3SA), B('asm1'), B('asm2'))), 'incompatible');
  const base = '<?xml version="1.0"?><base><Current_Tuning><T name="a" value="1"/><T name="b" value="1"/></Current_Tuning></base>';
  const xa = base.replace('name="a" value="1"', 'name="a" value="5"'), xb = base.replace('name="b" value="1"', 'name="b" value="7"');
  want('tuning-disjoint', findingRule(keyFinding(K(0x0333406c), B(xa), B(xb), B(base))), 'caveat');
  want('tuning-overlap', findingRule(keyFinding(K(0x03b33ddf), B(xa), B(base.replace('name="a" value="1"', 'name="a" value="9"')), B(base))), 'incompatible');
  assert(tuningOverlap('<r><x v="1"/></r>', '<r><x v="1"/><y v="2"/></r>') === 'disjoint' && tuningOverlap('<r><x v="1"/></r>', '<r><x v="2"/></r>') === 'overlap'
    && tuningOverlap('<r><x>1</x></r>', '<r><x>2</x></r>') === 'overlap' && tuningOverlap('<r><x v="1"/><x v="2"/></r>', '<r><x v="1"/><x v="3"/></r>') === 'overlap', 'tuning fields without base');
  want('key-unread', findingRule(F({ kind: 'key' })), 'not decided');
  // XML tokenizer regex cases: tags with attributes, self-close, close, text, declaration / comment; attributes
  const tok = (s: string) => [...s.matchAll(XML_TOKEN_RE)].map((m) => m[5] !== undefined ? `#${m[5]}` : m[2] ? `${m[1]}${m[2]}${m[4]}` : '?');
  assert(tok('<?xml v="1"?><a x="1" y = "2"><b/>hi</a><!-- c -->') .join() === '?,a,b/,#hi,/a,?', `xml tokens ${tok('<?xml v="1"?><a x="1" y = "2"><b/>hi</a><!-- c -->')}`);
  assert(tok('<a.b-c:d e="">').join() === 'a.b-c:d' && tok('plain').join() === '#plain', 'xml token names / text');
  assert([...'k1="v" k2 = "w x" bad=y'.matchAll(XML_ATTR_RE)].map((m) => `${m[1]}=${m[2]}`).join() === 'k1=v,k2=w x', 'xml attributes (unquoted values ignored)');
  const xf = xmlFields('<r><T name="a" value="1"/><T name="b">t</T></r>');
  assert(xf.get('r[0]/T[0]@value') === '1' && xf.get('r[0]/T[1]@name') === 'b' && xf.get('r[0]/T[1]#text') === 't' && xf.size === 4, `xml fields ${[...xf]}`);
  // ASIs: the S3MM stand-down is compatible, its profiler (no stand-down) incompatible, and the row takes the worse
  const s3 = asiFinding('Sims3MonoModder.asi');
  want('asi-standdown', findingRule(s3[0]), 'compatible');
  want('asi-unguarded', findingRule({ ...s3[1], on: false }), 'incompatible'); // unguarded wins over off-by-default
  assert(rowRule({ findings: s3 }) === 'incompatible: asi-unguarded, asi-standdown', `S3MM row ${rowRule({ findings: s3 })}`);
  want('off-by-default', findingRule(F({ on: false, base: false })), 'compatible');
  // rows: none, unknown, worst first with counts, own issues apart from the outcome
  want('no-overlap', rowRule({ findings: [] }).replace(/^compatible: /, ''), 'compatible');
  want('unknown', rowRule({ findings: [], unknown: 'rar' }).replace(/^not decided: /, ''), 'not decided');
  assert(rowRule({ findings: [F({ base: true }), F({ base: true }), F({ base: false }), F({ kind: 'derives' })] }) === 'caveat: override-no-base, override-calls-base x2, derives', `row order ${rowRule({ findings: [F({ base: true }), F({ base: true }), F({ base: false }), F({ kind: 'derives' })] })}`);
  const own = rowRule({ findings: [], extras: ['audit-confirmed: x (docs/318)', '3.00 GB: merged package over 2 GB, split it'] });
  want('own-issues', own === 'compatible: no-overlap; own issues' ? 'own-issues' : own, 'flag');
  assert(!rowRule({ findings: [], extras: ['outdated: pre-1.67'] }).includes('own issues'), 'outdated is not own issues');
  // mod-vs-mod pairs (Apex ModConflicts kinds)
  want('pair-core-same-module', pairRule({ kind: 'core', sameModule: true, current: [true, true] }), 'incompatible');
  want('pair-core-other-module', pairRule({ kind: 'core', sameModule: false, current: [true, true] }), 'compatible');
  assert(pairRule({ kind: 'core', sameModule: false, current: [true, false] }) === 'module-old', 'pair: an old core module');
  want('pair-assembly-copies', pairRule({ kind: 'assembly', identical: false }), 'incompatible');
  want('pair-identical', pairRule({ kind: 'assembly', identical: true }), 'compatible');
  want('pair-interaction', pairRule({ kind: 'interaction' }), 'caveat');
  want('pair-hook-conflict', pairRule({ kind: 'hook', coordinated: false }), 'incompatible');
  assert(pairRule({ kind: 'hook', coordinated: true }) === 'asi-standdown', 'pair: coordinated hook');
  want('pair-area-hint', pairRule({ kind: 'area' }), 'not decided');
  // evidence text of older scans -> findings (regex cases, match and non-match)
  const ev = '[static scan 2026-11-22, MTS 1] replaces Sims3GameplaySystems (patched G.Lot::Clean); ts3fixes OverwatchNotice looks up NRaas.Common by name (hook or stand-down); '
    + 'same resource key as ts3fixes.package 0333406c:00000000:0000000000000001; M.A overrides G.GoHome::Run; M.B overrides G.GoHome::Run (no base call); M.E derives G.Lot (patched Clean); M.D hides G.Lot (patched Clean); +3 more';
  assert(findingsFromEvidence(ev, 'overlap', 'x').map(findingRule).join() === 'module-unread,named,key-unread,override-unread,override-no-base,derives,derives-hides', `evidence ${findingsFromEvidence(ev, 'overlap', 'x').map(findingRule)}`);
  assert(findingsFromEvidence('M.A overrides G.GoHome::Run', 'overlap, off by default', 'x').map(findingRule).join() === 'off-by-default', 'evidence: off by default');
  for (const t of ['replaces UI', 'replaces UI (patched A::B) extra', 'looks up X by name', 'overrides G::Run', 'M derives G', 'same resource key as other.package k', 'rar archive: unpack needed'])
    assert(!findingsFromEvidence(t, 'overlap', 'x').length, `evidence non-match ${t}`);
  assert(matrixRule('Sims3MonoModder', 'overlap', '', []) === 'incompatible: asi-unguarded, asi-standdown' && matrixRule('a', 'unknown', 'rar', []) === 'not decided: unknown', 'matrixRule');
  assert(matrixRule('EnhancedWitches', 'no overlap', '', loadKnownBad(join(REPO, 'docs', 'data', 'known-bad-mods.json'))) === 'compatible: no-overlap; own issues', 'matrixRule own issues');
  const missing = RULES.map((r) => r.id).filter((id) => !seen.has(id));
  assert(RULES.length > 0 && !missing.length, `rules without a fixture: ${missing}`);
  assert(RULES.every((r) => r.wording && r.condition && ['static', 'in-game proven', 'community-reported'].includes(r.evidence) && Array.isArray(r.counterExamples)), 'rule fields');
}

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
  // off-by-default features: overlap only with live ones
  const offF = [{ kind: 'overrides' as const, text: 'x', feature: 'Off', on: false }], onF = [{ kind: 'replaces' as const, text: 'y', feature: 'On', on: true }];
  assert(verdict({ findings: offF }) === 'overlap, off by default' && verdict({ findings: [...offF, ...onF] }) === 'overlap'
    && verdict({ findings: [{ kind: 'key', text: 'k' }] }) === 'overlap' && verdict({ findings: [{ kind: 'derives', text: 'd', on: true }] }) === 'no overlap', 'verdict by default state');
  // AutShadow's Hook(failed, typeof(T), "M", ...) is a swap; other Hook(...) calls and the helper definition are not
  assert(typeExpr('typeof(AutonomyT)', 'using AutonomyT = Sims3.Gameplay.Autonomy.Autonomy;') === 'Sims3.Gameplay.Autonomy.Autonomy' && typeExpr('typeof(Lot)', 'using X = Y;') === 'Lot', 'using alias');
  const hk = patchCalls('static bool Hook(List<string> failed, Type target, string method) { }\n ok &= Hook(failed, typeof(SimUpdate), "Calc", null, typeof(EH), "A", "B");\n TakeARisk.Hook(c);\n x.Hook(failed, sType, "Run");\n Type sType = typeof(Lot);', 'AutOffload');
  assert(hk.targets.map((t) => `${t.type}::${t.method}`).join() === 'SimUpdate::Calc,Lot::Run' && hk.dynamic === 0, `Hook calls ${JSON.stringify(hk)}`);
  // switch defaults: SwDefault case -> Instantiator initializer, TUNING_XML wins, IniUnseen = [AutShadow] Unseen
  const fx = 'static bool SwDefault(string name) { switch (name) {\n case "a": return Instantiator.kA;\n case "b": case "b2": return Instantiator.kB; // "c" in a comment\n case "v": return false;\n case "u": return IniUnseen();\n case "w": return Instantiator.kW || IniUnseen();\n case "t": return true;\n default: return false; } }';
  const inst = '[Tunable] public static bool kA = true;\n[Tunable] public static bool kB = false;\n[Tunable] public static bool kW = false;';
  const d0 = switchDefaults(fx, inst, 'const TUNING_XML = `<base>\n<kA value="False" />\n<kB value="True" />\n</base>`;', '[AutShadow]\nUnseen=0 ; off\n');
  assert([...d0].map(([k, v]) => `${k}=${+v}`).join() === 'a=0,b=1,b2=1,v=0,u=0,w=0,t=1', `switch defaults ${[...d0]}`);
  const d1 = switchDefaults(fx, inst, '', '[autshadow]\nUnseen = 1\n');
  assert(d1.get('a') === true && d1.get('b') === false && d1.get('u') === true && d1.get('w') === true && !d1.has('c'), 'switch defaults: initializers, ini Unseen=1');
  assert(featureOn([], d0) && featureOn(['v', 'b'], d0) && !featureOn(['v', 'a'], d0) && !featureOn(['nosuch'], d0), 'featureOn');
  // ts3native module gates: Enabled / Mode reads, ini value else the read's default
  const ini = parseIni('[Alloc]\nEnabled=1\n[CasTrim]\nMode=0\n');
  assert(moduleGate('x = GetPrivateProfileIntW(L"Alloc", L"Enabled", 0, ini);', ini) === 'on' && moduleGate('GetPrivateProfileIntW(L"CasTrim", L"Mode", 1, ini)', ini) === 'off'
    && moduleGate('GetPrivateProfileIntW(L"Other", L"Enabled", 1, ini)', ini) === 'on' && moduleGate('GetPrivateProfileIntW(L"Other", L"Log", 1, ini)', ini) === 'ungated'
    && moduleGate('// GetPrivateProfileIntW(L"Alloc", L"Enabled", 0, ini)', ini) === 'ungated', 'module gates');
  const oldCsv = 'mod,file,kind,overlap,verdict,evidence,status,note\n"A","a.package","script","","overlap","say \\"hi\\", ok","in our test setup","limit, known"\n"P","player/p.package","script","","no overlap","e","",""\n';
  const out = parseCsv(matrixCsv([{ mod: 'A', file: 'a.package', kind: 'script', overlap: 'X', verdict: 'overlap', evidence: 'new "x"' }, { mod: 'N', file: 'n.package', kind: 'script', overlap: '', verdict: 'no overlap', evidence: '' }], oldCsv));
  assert(out[0].length === 9 && out.length === 4 && out[0][8] === 'rule', `matrix shape ${JSON.stringify(out)}`);
  assert(out[1].slice(0, 8).join('|') === 'A|a.package|script|X|overlap|new "x"|in our test setup|limit, known', `matrix keeps status and note ${out[1]}`);
  assert(out[2][1] === 'n.package' && out[2][6] === '' && out[3][1] === 'player/p.package' && out[3][4] === 'no overlap' && out[3][8] === 'compatible: no-overlap', 'matrix new row, rows of other scans kept, rule from evidence');
  const dropped = parseCsv(matrixCsv([{ mod: 'A', file: 'a.package', kind: 'script', overlap: '', verdict: 'no overlap', evidence: '' }],
    'mod,file,kind,overlap,verdict,evidence,status,note\n"G","compat-mods/gone.package","script","","overlap","","",""\n"K","player/p.package","script","","overlap","","",""\n', ['compat-mods']));
  assert(dropped.length === 3 && dropped[2][1] === 'player/p.package', `matrix drops rows of a scanned root ${JSON.stringify(dropped)}`);
  assert(parseCsv('a,"b ""c"""\n')[0][1] === 'b "c"' && parseCsv(matrixCsv([], ''))[0].length === 9, 'matrix csv quotes / empty old file');
  const kb: KnownBad[] = [{ name: '^old_?mod', status: 'outdated', reason: 'pre-1.67', source: 'docs/x' }, { key: '0333406c:00000000:0000000000000007', status: 'known-bad', reason: 'breaks saves' }, { name: '(', status: 'known-bad', reason: 'bad regex' }];
  assert(modExtras('OldMod_v1', 10, [], kb).join() === 'outdated: pre-1.67 (docs/x)', 'known-bad by name');
  assert(modExtras('fine', 10, ['0333406c:00000000:0000000000000007'], kb).join() === 'known-bad: breaks saves', 'known-bad by key');
  assert(modExtras('fine', 10, [], kb).length === 0 && modExtras('x', 3 * GB, [], []).join().includes('over 2 GB') && modExtras('x', 4 * GB, [], []).join().includes('unreadable'), 'no match / size limits');
  assert(asiFinding('Bin/Sims3MonoModder.asi')[0]?.feature === 'S3mmStandDown' && asiFinding('sims3monomodder.ASI').length === 2 && !asiFinding('Sims3MonoModder.asi.bak').length && !asiFinding('xSims3MonoModder.asi').length, 'known ASI by name');
  // the shipped list: the two hangs seen on our setup match their package names only; audit-confirmed entries by build name
  const real = loadKnownBad(join(REPO, 'docs', 'data', 'known-bad-mods.json'));
  assert(modExtras('EnhancedWitches', 10, [], real)[0]?.startsWith('hang-seen: ') && modExtras('velocitygrass_AddAnyLotSize', 10, [], real).length === 1
    && !modExtras('EnhancedWitches (flavor 2)', 10, [], real).length && !modExtras('velocitygrass_AddAnyLotSize103', 10, [], real).length, 'known-bad list: hang-seen names');
  for (const m of ['Buzz_MakeNPCDoorFix', 'Buzz_BuildBuyRestrictionChoker', 'ani_GetDrunk_1.63', 'TSS_MoreRomanticInteractions', 'ani_Dexter', 'Desecrate_WeaponsLib', 'Enhanced Vampires', 'icarusallsorts.PoolLifeguard', 'icarusallsorts.PoolLifeguard.package'])
    assert(modExtras(m, 10, [], real)[0]?.startsWith('audit-confirmed: '), `audit-confirmed match ${m}`);
  for (const m of ['Buzz_AutoRoofDisabler', 'ani_GetDrunk_1x63', 'ani_GetDrunk_1.55', 'TSS_MoreRomanticInteractions_NRaasWoohooer_Compatibility', 'ani_Dexter_150', 'Enhanced Vampires 2', 'NoThirst'])
    assert(!modExtras(m, 10, [], real).length, `audit-confirmed non-match ${m}`);
  rulesCheck(assert);
  // CC String Merge output names (docs/304): regex cases, match and non-match
  for (const m of ['CCStringMerge_Strings', 'ccstringmerge_strings', 'CCStringMerge_Merged_001', 'CCStringMerge_Merged_123']) assert(CCSM_RE.test(m) && ccStringMergeNote(m), `ccsm match ${m}`);
  for (const m of ['CCStringMerge_Strings_old', 'MyCCStringMerge_Strings', 'CCStringMerge_Merged_1', 'CCStringMerge_Merged_0001', 'CCStringMerge', 'Strings']) assert(!CCSM_RE.test(m) && !ccStringMergeNote(m), `ccsm non-match ${m}`);
  assert(ccStringMergeNote('CCStringMerge_Merged_002')!.includes('file name') && ccStringMergeNote('CCStringMerge_Strings')!.includes('one per language'), 'ccsm note per kind');
  // package index from disk and from a zip, stored and deflated; synthetic STBLs (English 0x00, German 0x01 language byte)
  const stbl = (lang: bigint, low: bigint) => ({ type: TYPE_STBL, group: 0, instance: (lang << 56n) | low, data: Buffer.from('STBL\x02\0\0\0\0\0\0\0\0\0\0\0\0', 'latin1') });
  const pkg = writePackage([{ type: 0x0333406c, group: 0, instance: 7n, data: Buffer.from('<x/>') }, { type: TYPE_S3SA, group: 0, instance: fnv64('A'), data: Buffer.from('not an assembly') }]);
  const gamePkg = writePackage([stbl(0n, 0x1234n), stbl(1n, 0x1234n), { type: 0x0333406c, group: 0, instance: 0x1234n, data: Buffer.from('<x/>') }]);
  const cc = writePackage([stbl(0n, 0x1234n), stbl(1n, 0x9999n), { type: 0x0333406c, group: 0, instance: 0x1234n, data: Buffer.from('<y/>') }]);
  const tmp = mkdtempSync(join(tmpdir(), 'compat-sc-'));
  try {
    writeFileSync(join(tmp, 'a.package'), pkg);
    assert(packageIndex(join(tmp, 'a.package')).map(keyOf).includes('0333406c:00000000:0000000000000007'), 'index from disk');
    const z = unzip(zip([['Mods/a.package', pkg]]));
    const nz = zipEntries(zip([['x.zip/', Buffer.alloc(0)], ['in.zip', zip([['b.package', pkg]])], ['c.package', pkg]]));
    assert(nz.map(([n]) => n).sort().join() === 'c.package,in.zip!b.package' && packageIndex(nz.find(([n]) => n.includes('!'))![1]).length === 2, `nested zip ${nz.map(([n]) => n)}`);
    assert(packageIndex(z.get('Mods/a.package')!).length === 2, 'index from zip');
    const gameStbl = new Set(packageIndex(gamePkg).filter((e) => e.type === TYPE_STBL).map(keyOf));
    const hits = stblOverrides(packageIndex(cc).map(keyOf), gameStbl);
    assert(hits.join() === '220557da:00000000:0000000000001234', `stbl override: same TGI only, other types ignored ${hits}`);
    assert(stblOverrideNote(hits)!.includes('1 EA string table') && !stblOverrideNote([]), 'stbl override note');
    assert(!stblOverrides(packageIndex(pkg).map(keyOf), gameStbl).length, 'no STBL, no override');
  } finally { rmSync(tmp, { recursive: true, force: true }); }
  // the repo's own sources, when present
  const o = ourPatches();
  if (existsSync(join(REPO, 'mods', 'ts3fixes', 'src'))) assert(o.targets.some((t) => t.type === 'GoHome' && t.method === 'Run' && t.feature === 'Fixes'), 'ts3fixes GoHome::Run');
  // shipped defaults: gohome on, a verify switch off; AutShadow Hook() swaps found; some patches live, some off
  if (existsSync(join(REPO, 'mods', 'ts3fixes', 'src'))) assert(o.defaults.get('gohome') === true && o.defaults.get('venuememoverify') === false && o.defaults.size > 500
    && o.targets.some((t) => t.feature === 'AutOffload' && t.method === 'CalculateSimEnvironmentScore') && o.targets.some((t) => t.on) && o.targets.some((t) => t.on === false), 'ts3fixes switch defaults / Hook swaps');
  if (existsSync(join(REPO, 'native', 'ts3native'))) assert(nativeHooks().length > 50, 'ts3native hook addresses');
  assert(firstVolume('a/x.part2.rar') === 'a/x.part1.rar' && firstVolume('x.part03.rar') === 'x.part01.rar' && firstVolume('x.part1.rar') === 'x.part1.rar' && firstVolume('y.rar') === 'y.rar', 'multi-volume rar part 1');
  console.log('compat-scan self-check ok');
}

if (import.meta.filename === process.argv[1] || /compat-scan\.ts$/.test(process.argv[1] ?? '')) {
  if (argv.includes('--self-check')) selfCheck();
  else { cg = await import('./managed-callgraph.ts'); run(); }
}
