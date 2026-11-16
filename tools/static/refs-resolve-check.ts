// refs-resolve-check: what Mono can only find out in the game, checked against the game's own assemblies (local-only:
// needs game-copy/assemblies and, for the user's setup, game-copy/simler90/ref; both gitignored).
//  1. Unresolved references: every TypeRef and MemberRef of a mod assembly must resolve (a member simler90 UPDATE242
//     changed, a type the reference DLLs have and the game does not). Mono throws when it compiles the method using it.
//  2. Ambiguous name-only lookups: `GetMethod("n", flags)` / `GetMethod("n")` with no argument types throws
//     AmbiguousMatchException when the type (or a base) declares two methods named n. In a static initializer that kills
//     the whole partial class Fixes (r43, 2026-11-16: RomanceVisibilityState.LocalizeReputationKey has (bool, ...) and
//     (bool[], ...); every ts3fixes fix was dead and an unwrapped world-load call then hung the load); in an Apply* body it
//     stops every later Apply. The source is scanned for `typeof(T).GetMethod("n", ...)` and `x.GetMethod("n", ...)` where
//     x is a `Type` field set from `typeof(T)` (optionally `.GetNestedType("N", ...)`, or Assembly.GetType("full")).
// Usage: node tools/static/refs-resolve-check.ts [<mod.dll>] [--src <dir>...] [--game <dir>...]   (defaults: build/Ts3Fixes.dll,
//        mods/ts3fixes/src + mods/ts3agent/src, game-copy/assemblies then game-copy/simler90/ref; later dirs win)
//        node tools/static/refs-resolve-check.ts --self-check
// Exit 1 when anything is found.
import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs';
import { join, resolve } from 'node:path';
import assert from 'node:assert/strict';
import { loadStandalone, tokenText, type Asm } from '../managed-callgraph.ts';

const ROOT = resolve(import.meta.dirname, '..', '..');
type T = { methods: Map<string, Set<string>>; fields: Set<string>; base?: string };

function loadGame(dirs: string[]): Map<string, T> {
  const byName = new Map<string, Asm>();
  for (const d of dirs) for (const f of readdirSync(d).filter((n) => n.endsWith('.dll'))) byName.set(f, loadStandalone(join(d, f), f));
  const types = new Map<string, T>();
  for (const a of byName.values()) for (const t of a.types) {
    const methods = new Map<string, Set<string>>();
    for (const m of t.methods) { const k = `${m.name}${m.arity ? '`' + m.arity : ''}`; if (!methods.has(k)) methods.set(k, new Set()); methods.get(k)!.add(`(${m.params})`); }
    const tag = t.extendsCi & 3, idx = t.extendsCi >>> 2;
    const base = idx ? tokenText(a, ([0x02, 0x01, 0x1b][tag] << 24) | idx).replace(/<.*$/, '') : undefined;
    types.set(t.full, { methods, fields: new Set(t.fields.map((f) => f.name)), base });
  }
  return types;
}

function chain(types: Map<string, T>, owner: string): T[] {
  const out: T[] = [];
  for (let c: string | undefined = owner, h = 0; c && h < 40; c = types.get(c)?.base, h++) { const t = types.get(c); if (t) out.push(t); }
  return out;
}

export function unresolved(mod: Asm, types: Map<string, T>): string[] {
  const own = new Set(mod.types.map((t) => t.full));
  const missing = new Set<string>();
  const tabs = (mod as unknown as { tables: number[][][] }).tables;
  for (let i = 1; i <= (tabs[1]?.length ?? 0); i++) { const n = tokenText(mod, (0x01 << 24) | i); if (!types.has(n) && !own.has(n)) missing.add(`type ${n}`); }
  for (let i = 1; i <= (tabs[0x0a]?.length ?? 0); i++) {
    const s = tokenText(mod, (0x0a << 24) | i);
    const k = s.indexOf('::'); if (k < 0) continue;
    const owner = s.slice(0, k).replace(/<.*$/, '');
    if (own.has(owner) || owner.endsWith('[]') || owner.startsWith('!') || !types.has(owner)) continue;
    const rest = s.slice(k + 2), p = rest.indexOf('(');
    if (p < 0) { const name = rest.replace(/:.*$/, ''); if (!chain(types, owner).some((t) => t.fields.has(name))) missing.add(`field ${owner}::${name}`); continue; }
    const name = rest.slice(0, p), sig = rest.slice(p, rest.lastIndexOf(')') + 1);
    if (!chain(types, owner).some((t) => t.methods.get(name)?.has(sig))) missing.add(`method ${owner}::${name}${sig}`);
  }
  return [...missing].sort();
}

// Short or full C# type name -> full metadata names it can mean (nested types as Outer+Inner).
function finder(types: Map<string, T>): (cs: string) => string[] {
  const byTail = new Map<string, string[]>();
  for (const full of types.keys()) { const tail = full.replace(/^.*[.]/, '').replace(/\+/g, '.'); for (const k of [tail, tail.split('.').pop()!]) { if (!byTail.has(k)) byTail.set(k, []); byTail.get(k)!.push(full); } }
  return (cs) => {
    const n = cs.replace(/<.*>/, '').replace(/^global::/, '');
    if (types.has(n)) return [n];
    const dotted = n.replace(/\+/g, '.');
    const parts = dotted.split('.');
    for (let i = 0; i < parts.length; i++) { const tail = parts.slice(i).join('.'); const hit = (byTail.get(tail) ?? []).filter((f) => f.replace(/\+/g, '.').endsWith(dotted) || dotted.endsWith(f.replace(/^.*?([A-Za-z0-9_]+(\+[A-Za-z0-9_]+)*)$/, '$1').replace(/\+/g, '.'))); if (hit.length) return hit; }
    return [];
  };
}

export interface Amb { file: string; line: number; owner: string; name: string; overloads: number }
export function ambiguous(files: { file: string; src: string }[], types: Map<string, T>): Amb[] {
  const find = finder(types);
  // Type fields: `sX = typeof(T)` / `typeof(T).GetNestedType("N"` / `.Assembly.GetType("full")`
  const vars = new Map<string, string[]>();
  const tv = /\b(s[A-Za-z0-9_]+)\s*=\s*typeof\(([A-Za-z0-9_.+]+)\)(?:\.GetNestedType\("([A-Za-z0-9_]+)"|\.Assembly\.GetType\("([A-Za-z0-9_.+]+)"\))?/g;
  for (const { src } of files) for (const m of src.matchAll(tv)) {
    if (m[4]) { if (types.has(m[4])) vars.set(m[1], [m[4]]); continue; }
    const base = find(m[2]); if (!base.length) continue;
    vars.set(m[1], m[3] ? base.map((b) => `${b}+${m[3]}`).filter((x) => types.has(x)) : base);
  }
  const out: Amb[] = [];
  // a lookup by name with only binding flags (or nothing) after the name: no argument types
  const re = /(typeof\(([A-Za-z0-9_.+<>, ]+?)\)|\b(s[A-Za-z0-9_]+))\.GetMethod\("([A-Za-z0-9_]+)"\s*(?:,\s*([A-Za-z0-9_.|() ]+?))?\)/g;
  for (const { file, src } of files) src.split('\n').forEach((l, i) => {
    for (const m of l.matchAll(re)) {
      if (m[5] && /Type(\.EmptyTypes|\[)|new\s/.test(m[5])) continue;
      const owners = m[2] ? find(m[2]) : vars.get(m[3]) ?? [];
      for (const o of owners) {
        // name-only GetMethod sees the type and, without DeclaredOnly, its bases: count distinct signatures
        const sigs = new Set<string>();
        const declaredOnly = (m[5] ?? '').includes('DeclaredOnly');
        for (const t of declaredOnly ? chain(types, o).slice(0, 1) : chain(types, o)) for (const s of t.methods.get(m[4]) ?? []) sigs.add(s);
        if (sigs.size > 1) { out.push({ file, line: i + 1, owner: o, name: m[4], overloads: sigs.size }); break; }
      }
    }
  });
  return out;
}

// Type fields as the source sets them: typeof(T), .GetNestedType("N") on typeof or on another field, B13Nested(owner, "A",
// "B"), Assembly.GetType("full"). A nested type the game lacks is reported (the field is null in game, every lookup on it
// fails quietly). Several passes so a field built from another field resolves.
function typeVars(files: { file: string; src: string }[], types: Map<string, T>, out: Missing[]): Map<string, string[]> {
  const find = finder(types);
  const vars = new Map<string, string[]>();
  const owner = (e: string): string[] | undefined => { const t = /^typeof\(([A-Za-z0-9_.+]+)\)$/.exec(e); return t ? find(t[1]) : vars.get(e); };
  const nest = (sink: Missing[], file: string, line: number, base: string[], names: string[]): string[] => {
    let cur = base;
    for (const n of names) {
      const next = cur.map((b) => `${b}+${n}`).filter((x) => types.has(x));
      if (!next.length) { sink.push({ file, line, owner: [...new Set(cur)].join(' | '), name: n, kind: 'nested type' }); return []; }
      cur = next;
    }
    return cur;
  };
  const re = /\b(s[A-Z][A-Za-z0-9_]*)\s*=\s*(?:[^;=]*?\?\s*null\s*:\s*)?(?:(typeof\([A-Za-z0-9_.+]+\)|s[A-Z][A-Za-z0-9_]*)\.GetNestedType\("([A-Za-z0-9_]+)"|B13Nested\((typeof\([A-Za-z0-9_.+]+\)|s[A-Z][A-Za-z0-9_]*)((?:\s*,\s*"[A-Za-z0-9_]+")+)\)|typeof\(([A-Za-z0-9_.+]+)\)(?:\.Assembly\.GetType\("([A-Za-z0-9_.+]+)"\))?)/g;
  // passes 0-1 resolve fields built from fields; the last pass reports missing nested types once
  for (let pass = 0; pass < 3; pass++) {
    const sink: Missing[] = pass === 2 ? out : [];
    for (const { file, src } of files) src.split('\n').forEach((l, i) => {
      for (const m of l.matchAll(re)) {
        let got: string[] = [];
        if (m[6] !== undefined) got = m[7] ? (types.has(m[7]) ? [m[7]] : []) : find(m[6]);
        else {
          const base = owner(m[2] ?? m[4]); if (!base || !base.length) continue;
          got = nest(sink, file, i + 1, base, m[3] ? [m[3]] : [...m[5].matchAll(/"([A-Za-z0-9_]+)"/g)].map((x) => x[1]));
        }
        if (got.length) vars.set(m[1], got);
      }
    });
  }
  return vars;
}

// Members looked up by name on a game type the source names (typeof(T) or a Type field above), through GetMethod /
// GetField / GetProperty or our helpers B12F / B12FUp / B12M and Patch: the name must exist on the type or a base. A
// wrong owner type or name only shows in game as "not found" or a swap that never went in (s58: tempforecast looked on
// WeatherManager, lottotooltip's LocalizeString on the nested BuyLottoTicket; both are elsewhere).
export interface Missing { file: string; line: number; owner: string; name: string; kind: string }
export function missingMembers(files: { file: string; src: string }[], types: Map<string, T>): Missing[] {
  const out: Missing[] = [];
  const vars = typeVars(files, types, out);
  const find = finder(types);
  const re = /(?:(typeof\([A-Za-z0-9_.+]+\)|\bs[A-Z][A-Za-z0-9_]*)\.Get(Method|Field|Property)\("([A-Za-z0-9_]+)"|\b(B12FUp|B12F|B12M|Patch)\(\s*(typeof\([A-Za-z0-9_.+]+\)|s[A-Z][A-Za-z0-9_]*)\s*,\s*"([A-Za-z0-9_]+)")/g;
  for (const { file, src } of files) src.split('\n').forEach((l, i) => {
    if (l.includes('refs: optional')) return;  // a lookup that may miss on purpose (another build's member, null-checked)
    for (const m of l.matchAll(re)) {
      const oe = m[1] ?? m[5], name = m[3] ?? m[6];
      const kind = m[2] ? m[2].toLowerCase() : m[4].startsWith('B12F') ? 'field' : 'method';
      const t = /^typeof\(([A-Za-z0-9_.+]+)\)$/.exec(oe);
      const owners = [...new Set(t ? find(t[1]) : vars.get(oe) ?? [])];
      if (!owners.length) continue;
      const has = (o: string) => chain(types, o).some((c) => kind === 'field' ? c.fields.has(name)
        : kind === 'property' ? c.methods.has(`get_${name}`) || c.methods.has(`set_${name}`)
        : [...c.methods.keys()].some((k) => k.replace(/`\d+$/, '') === name));
      if (!owners.some(has)) out.push({ file, line: i + 1, owner: owners.join(' | '), name, kind });
    }
  });
  return out;
}

function walk(dir: string, acc: string[] = []): string[] {
  for (const n of readdirSync(dir)) { const p = join(dir, n); if (statSync(p).isDirectory()) walk(p, acc); else if (n.endsWith('.cs')) acc.push(p); }
  return acc;
}

function selfCheck(): void {
  const types = new Map<string, T>([
    ['N.Base', { methods: new Map([['Loc', new Set(['(bool)'])]]), fields: new Set() }],
    ['N.R', { methods: new Map([['Loc', new Set(['(bool[])'])], ['One', new Set(['()'])]]), fields: new Set(), base: 'N.Base' }],
    ['N.R+Inner', { methods: new Map([['Two', new Set(['(int)', '(string)'])]]), fields: new Set() }],
  ]);
  const src = [
    'static readonly MethodInfo a = typeof(R).GetMethod("Loc", BindingFlags.Static | BindingFlags.NonPublic);',  // base + own: ambiguous
    'static readonly MethodInfo b = typeof(R).GetMethod("Loc", kAll, null, new Type[] { typeof(bool[]) }, null);',  // typed: fine
    'static readonly MethodInfo c = typeof(R).GetMethod("One", kAll);',  // single: fine
    'static readonly Type sIn = typeof(R).GetNestedType("Inner", kAll);',
    'static readonly MethodInfo d = sIn == null ? null : sIn.GetMethod("Two", kAll);',  // through a Type field: ambiguous
    'static readonly MethodInfo e = typeof(R).GetMethod("Loc", kAll | BindingFlags.DeclaredOnly);',  // own only: fine
  ].join('\n');
  const got = ambiguous([{ file: 'x.cs', src }], types).map((x) => `${x.line}:${x.owner}.${x.name}`);
  assert.deepEqual(got, ['1:N.R.Loc', '5:N.R+Inner.Two'], 'ambiguous lookups found, typed / single / DeclaredOnly ones not');
  const msrc = [
    'static readonly MethodInfo f = typeof(R).GetMethod("One", kAll);',  // own: fine
    'static readonly MethodInfo g = B12M(typeof(R), "Loc", typeof(bool));',  // on the base: fine
    'static readonly MethodInfo h = B12M(sIn, "Loc", typeof(bool));',  // nested type does not see the outer: missing
    'static readonly Type sWrong = B13Nested(typeof(R), "Inner", "Deeper");',  // nested type missing
    'static readonly FieldInfo k = typeof(R).GetField("kNope", kAll); // refs: optional',  // opted out
    '            Patch(sIn, "Gone", Type.EmptyTypes, typeof(H), "X", "Y");',  // swap target missing
  ].join('\n');
  const mg = missingMembers([{ file: 'x.cs', src: src + '\n' + msrc }], types).map((x) => `${x.line}:${x.kind}:${x.name}`);
  assert.deepEqual(mg.sort(), ['12:method:Gone', '9:method:Loc', '10:nested type:Deeper'].sort(), 'missing members and nested types found, base / own / opted-out ones not');
  console.log('refs-resolve-check self-check: ok');
}

if (process.argv[1]?.endsWith('refs-resolve-check.ts')) {
  const a = process.argv.slice(2);
  if (a.includes('--self-check')) { selfCheck(); process.exit(0); }
  const list = (flag: string, def: string[]) => { const o: string[] = []; for (let i = 0; i < a.length; i++) if (a[i] === flag) while (a[i + 1] && !a[i + 1].startsWith('--')) o.push(a[++i]); return o.length ? o : def; };
  const dll = a[0] && !a[0].startsWith('--') ? a[0] : join(ROOT, 'build', 'Ts3Fixes.dll');
  const game = list('--game', [join(ROOT, 'game-copy', 'assemblies'), join(ROOT, 'game-copy', 'simler90', 'ref')]).filter(existsSync);
  const srcs = list('--src', [join(ROOT, 'mods', 'ts3fixes', 'src'), join(ROOT, 'mods', 'ts3agent', 'src')]).filter(existsSync);
  if (!game.length || !existsSync(dll)) { console.log('refs-resolve-check: skipped (needs game-copy assemblies and a built ' + dll + ')'); process.exit(0); }
  const types = loadGame(game);
  const un = unresolved(loadStandalone(dll, 'mod'), types);
  const amb = ambiguous(srcs.flatMap((d) => walk(d)).map((f) => ({ file: f.slice(ROOT.length + 1), src: readFileSync(f, 'utf8') })), types);
  for (const u of un) console.log(`unresolved ${u}`);
  const files = srcs.flatMap((d) => walk(d)).map((f) => ({ file: f.slice(ROOT.length + 1), src: readFileSync(f, 'utf8') }));
  const miss = missingMembers(files, types);
  for (const x of amb) console.log(`ambiguous ${x.file}:${x.line}: ${x.owner}.${x.name} has ${x.overloads} signatures; pass the argument types`);
  for (const x of miss) console.log(`missing ${x.file}:${x.line}: ${x.kind} ${x.name} is not on ${x.owner} or its bases`);
  console.log(`refs-resolve-check: ${un.length} unresolved, ${amb.length} ambiguous name-only GetMethod, ${miss.length} by-name lookups of missing members (game: ${game.map((g) => g.slice(ROOT.length + 1)).join(' + ')})`);
  process.exit(un.length + amb.length + miss.length ? 1 : 0);
}
