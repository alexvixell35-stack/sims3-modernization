// G-M6 (PR #112, docs/215 §4-5): content hashes of the [AutShadow] closure methods (mods/ts3fixes/src/AutShadowLists.cs
// Methods) per checked build, so ts3fixes accepts a closure method only when its body equals one the closures were
// checked on. An MVID is not content: tiny-ui-fix rewrites UI / Sims3StoreObjects / SGS / SGO and keeps their MVIDs.
// Hash = AutShadowCore.IlHash (keep both in step; --self-check pins one vector that tools/selftest/autshadow checks too):
// FNV-1a 64 over the closure key, the IL with token operands replaced by member names, the EH clauses. Hashes only, no
// game bytes in the output.
// Usage:
//   node tools/autshadow-il-hashes.ts [--set "<label>=<a.dll>[,<b.dll>]"]... [--check]
//     default builds: EA (game-copy/assemblies) and simler90 UPDATE242 (game-copy/simler90-242/dll SGS + SGO over EA);
//     --set adds a build = the given DLLs over EA's (mod copies of game assemblies: extract with tools/extract-assemblies.ts).
//     --check: compare with the committed file instead of writing it (exit 1 when they differ).
//     NRaas ErrorTrap ScriptCore: game-copy/errortrap/ScriptCore.dll (extract-assemblies from NRaas_ErrorTrap.package); 0 new
//     hashes over EA, but its MVID goes into KnownMvids (guard-fault rule).
//     ILH_TRACE=<key prefix> with --one <dir>: token names on stderr (compare with the game's "G-M6 trace" lines).
//   node tools/autshadow-il-hashes.ts --hash-file <x.dll|exe> <key>...   hashes of methods of one file (selftest e2e)
//   node tools/autshadow-il-hashes.ts --self-check
import { execFileSync } from 'node:child_process';
import { copyFileSync, existsSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..');
const OUT = join(ROOT, 'mods/ts3fixes/src/AutShadowIlHashes.cs');
const EA = join(ROOT, 'game-copy/assemblies');
const SIMLER = join(ROOT, 'game-copy/simler90-242/dll');
// AutShadow.cs kGameModules: ClosureSweep resolves keys in this order (first assembly with the type wins)
const ORDER = ['mscorlib', 'System', 'System.Xml', 'ScriptCore', 'SimIFace', 'Sims3GameplaySystems', 'Sims3GameplayObjects', 'Sims3Metadata', 'Sims3StoreObjects', 'UI'];

// ---------- hash (AutShadowCore.IlHash) ----------
const P = 1099511628211n, M64 = (1n << 64n) - 1n, BASIS = 14695981039346656037n;
const mix = (h: bigint, x: number) => ((h ^ BigInt(x >>> 0)) * P) & M64;
const mixS = (h: bigint, s: string) => { for (let i = 0; i < s.length; i++) h = mix(h, s.charCodeAt(i)); return mix(h, 0x10000); };
const mixTok = (h: bigint, n: string | null, tok: number) => (n != null ? mixS(mix(h, 0x10001), n) : mix(mix(h, 0x10002), tok));
function operand(op: number): number {
  if (op >= 0xfe00) {
    const o = op & 0xff;
    if ([0x06, 0x07, 0x15, 0x16, 0x1c].includes(o)) return -1;
    if (o === 0x12 || o === 0x19) return 1;
    return o >= 0x09 && o <= 0x0e ? 2 : 0;
  }
  if ((op >= 0x0e && op <= 0x13) || op === 0x1f || op === 0xde || (op >= 0x2b && op <= 0x37)) return 1;
  if ((op >= 0x38 && op <= 0x44) || op === 0x20 || op === 0x22 || op === 0xdd) return 4;
  if (op === 0x21 || op === 0x23) return 8;
  if (op === 0x45) return -2;
  return [0x27, 0x28, 0x29, 0x6f, 0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x79, 0x7b, 0x7c, 0x7d, 0x7e, 0x7f, 0x80, 0x81, 0x8c, 0x8d, 0x8f, 0xa3, 0xa4, 0xa5, 0xc2, 0xc6, 0xd0].includes(op) ? -1 : 0;
}
// eh: 6 numbers per clause (flags, try off, try len, handler off, handler len, filter off); ehCatch per clause
export function ilHash(key: string, il: Buffer | null, eh: number[], ehCatch: (string | null)[], name: (tok: number) => string | null): bigint {
  let h = mixS(BASIS, key);
  if (!il) return mix(h, 0x10004);
  let p = 0;
  while (p < il.length) {
    let op = il[p++];
    h = mix(h, op);
    if (op === 0xfe) { h = mix(h, il[p]); op = 0xfe00 | il[p++]; }
    let n = operand(op);
    if (n === -1) { const tok = il.readUInt32LE(p); h = mixTok(h, name(tok), tok); p += 4; continue; }
    if (n === -2) n = 4 + 4 * il.readInt32LE(p);
    for (const e = p + n; p < e; p++) h = mix(h, il[p]);
  }
  h = mix(mix(h, 0x10003), ehCatch.length);
  for (let c = 0; c < ehCatch.length; c++) {
    for (let k = 0; k < 5; k++) h = mix(h, eh[c * 6 + k]);
    if (eh[c * 6] === 0) h = ehCatch[c] != null ? mixS(mix(h, 0x10001), ehCatch[c]!) : mix(h, 0x10002);
    else if (eh[c * 6] === 1) h = mix(h, eh[c * 6 + 5]);
  }
  return h;
}

// ---------- names from metadata (AutShadowCore.MemberName / ResolveName) ----------
async function hashMethods(dir: string, keys: string[]): Promise<{ hashes: Record<string, string>; missing: string[]; stubs: string[]; mvids: string[] }> {
  const cg = await import('./managed-callgraph.ts');
  const skipped: string[] = [];
  const { asms, baseOf } = cg.typeModel(dir, skipped);
  type Asm = (typeof asms)[number]; type TypeDef = Asm['types'][number]; type Method = Asm['methods'][number];
  const generic = new Map<Asm, Set<number>>();
  for (const a of asms) {
    const s = new Set<number>();
    for (const r of a.tables[0x2a] ?? []) { const [t, i] = cg.decodeCi('TypeOrMethodDef', r[2]); if (t === 2) s.add(i); }
    generic.set(a, s);
  }
  const gen = (t: TypeDef) => generic.get(t.asm)!.has(t.row);
  const mname = (m: Method) => (m.arity || gen(m.type) ? null : `${m.type.full}::${m.name}(${m.params})`);
  const stubs = new Set<string>();
  const fieldOn = (t: TypeDef | undefined, n: string): TypeDef | undefined => { for (let c = t, k = 0; c && k < 40; c = baseOf(c), k++) if (c.fields.some((f) => f.name === n)) return c; return undefined; };
  const typeName = (a: Asm, tt: number, ti: number): string | null => {
    const t = cg.resolveTdor(a, tt, ti);
    if (t) return gen(t) ? null : t.full;
    if (tt !== 1) return null;
    const n = cg.typeRefName(a, ti);
    return n.includes('`') ? null : n;
  };
  function tokName(a: Asm, tok: number): string | null {
    const tt = tok >>> 24, ti = tok & 0xffffff;
    switch (tt) {
      case 0x70: { const [len, n] = cg.cuint(a.buf, a.usOff + ti); const s = a.usOff + ti + n; return a.buf.toString('utf16le', s, s + Math.max(0, len - 1)); }
      case 0x01: case 0x02: return typeName(a, tt, ti);
      case 0x04: { const f = a.fields[ti - 1]; return gen(f.type) ? null : f.id; }
      case 0x06: return mname(a.methods[ti - 1]);
      case 0x0a: {
        const r = a.tables[0x0a][ti - 1];
        const [pt, pi] = cg.decodeCi('MemberRefParent', r[0]);
        if (pt !== 1 && pt !== 2 && pt !== 6) return null; // TypeSpec (generic instance, array), ModuleRef
        const [bp] = cg.blob(a, r[2]);
        if ((a.buf[bp] & 0x0f) === 6) { // field
          if (pt === 6) return null;
          const fn = cg.heapString(a, r[1]);
          const own = cg.resolveTdor(a, pt, pi);
          const t = fieldOn(own, fn);
          if (t) return gen(t) ? null : `${t.full}::${fn}`;
          if (own) stubs.add(`${own.full}::${fn}`);
          const tn = typeName(a, pt, pi);
          return tn && `${tn}::${fn}`;
        }
        const res = cg.resolveMethodToken(a, tok);
        if (res.m) return mname(res.m);
        if (pt !== 1 || !res.stub || /::[^(]*`/.test(res.stub)) return null;
        if (cg.resolveTdor(a, pt, pi)) stubs.add(res.stub); // type known, method not: the game may resolve it elsewhere
        return res.stub;
      }
    }
    return null;
  }
  const VA = (a: Asm, r: number) => { for (const s of a.sections) if (r >= s.va && r < s.va + s.size) return r - s.va + s.raw; throw new Error(`${a.name}: rva 0x${r.toString(16)}`); };
  // ILH_TRACE=<key prefix>: token names on stderr, to compare with the game's "AutShadow: G-M6 trace" lines
  const trace = (key: string, t: number, n: string | null) => { if (process.env.ILH_TRACE != null && key.startsWith(process.env.ILH_TRACE)) console.error(`iltrace ${key} ${t.toString(16).padStart(8, '0')} ${n ?? '-'}`); return n; };
  function hashOne(key: string, m: Method): bigint {
    const a = m.asm, b = a.buf;
    if (!m.rva || m.impl & 0x1003 || m.flags & 0x2400) return ilHash(key, null, [], [], () => null);
    const p = VA(a, m.rva);
    if ((b[p] & 3) === 2) return ilHash(key, b.subarray(p + 1, p + 1 + (b[p] >> 2)), [], [], (t) => trace(key, t, tokName(a, t)));
    const flags = b.readUInt16LE(p), code = p + (flags >> 12) * 4, size = b.readUInt32LE(p + 4);
    const eh: number[] = [], ehCatch: (string | null)[] = [];
    if (flags & 0x08) {
      for (let q = (code + size + 3) & ~3; ;) {
        const kind = b[q], fat = kind & 0x40, len = fat ? b.readUIntLE(q + 1, 3) : b[q + 1], cs = fat ? 24 : 12;
        if (kind & 1) for (let i = 0; i < Math.floor((len - 4) / cs); i++) {
          const c = q + 4 + i * cs;
          const v = fat ? [b.readUInt32LE(c), b.readUInt32LE(c + 4), b.readUInt32LE(c + 8), b.readUInt32LE(c + 12), b.readUInt32LE(c + 16), b.readUInt32LE(c + 20)]
            : [b.readUInt16LE(c), b.readUInt16LE(c + 2), b[c + 4], b.readUInt16LE(c + 5), b[c + 7], b.readUInt32LE(c + 8)];
          eh.push(v[0], v[1], v[2], v[3], v[4], v[0] === 1 ? v[5] : 0);
          ehCatch.push(v[0] === 0 ? ((v[5] >>> 24) === 0x1b ? null : typeName(a, v[5] >>> 24, v[5] & 0xffffff)) : null);
        }
        if (!(kind & 0x80)) break;
        q = (q + len + 3) & ~3;
      }
    }
    return ilHash(key, b.subarray(code, code + size), eh, ehCatch, (t) => trace(key, t, tokName(a, t)));
  }
  const byKey = new Map<string, Method>();
  const ordered = [...asms].sort((x, y) => (ORDER.indexOf(x.name) + 1 || 99) - (ORDER.indexOf(y.name) + 1 || 99));
  for (const a of ordered) for (const m of a.methods) if (m && !byKey.has(m.id)) byKey.set(m.id, m);
  const hashes: Record<string, string> = {}, missing: string[] = [];
  for (const k of keys) { const m = byKey.get(k); if (m) hashes[k] = hashOne(k, m).toString(16).padStart(16, '0'); else missing.push(k); }
  return { hashes, missing, stubs: [...stubs], mvids: asms.filter((a) => ORDER.includes(a.name)).map((a) => a.mvid) };
}

// ---------- main ----------
const argv = process.argv.slice(2);
const listKeys = () => {
  // AutShadowLists.Methods (closures) and AutShadowChaLists.Methods (their class-hierarchy overrides, docs/218 §5, from
  // tools/autshadow-cha-lists.ts): one sorted hash table for both (the key is part of the hash)
  const part = (file: string, a: string, b: string) => {
    const src = readFileSync(join(ROOT, 'mods/ts3fixes/src', file), 'utf8');
    return [...src.slice(src.indexOf(a), src.indexOf(b)).matchAll(/"([^"]*)"/g)].map((m) => m[1]);
  };
  return [...part('AutShadowLists.cs', 'Methods = {', 'Slots = {'), ...part('AutShadowChaLists.cs', 'Methods = {', 'Owners = {')];
};
// One build per child process: managed-callgraph keeps one global type model.
function runBuild(label: string, overlay: string[]): { label: string; hashes: Record<string, string>; missing: string[]; stubs: string[]; mvids: string[] } {
  const dir = mkdtempSync(join(tmpdir(), 'ilh-'));
  try {
    for (const f of readdirSync(EA).filter((n) => n.endsWith('.dll'))) copyFileSync(join(EA, f), join(dir, f));
    for (const f of overlay) copyFileSync(f, join(dir, basename(f)));
    const out = execFileSync(process.execPath, [import.meta.filename, '--one', dir], { maxBuffer: 64 << 20, encoding: 'utf8' });
    return { label, ...JSON.parse(out) };
  } finally { rmSync(dir, { recursive: true, force: true }); }
}

if (argv[0] === '--one') {
  console.log(JSON.stringify(await hashMethods(argv[1], listKeys())));
} else if (argv[0] === '--hash-file') {
  const dir = mkdtempSync(join(tmpdir(), 'ilh-'));
  try {
    copyFileSync(argv[1], join(dir, basename(argv[1]).replace(/\.exe$/i, '.dll')));
    const r = await hashMethods(dir, argv.slice(2));
    for (const k of argv.slice(2)) console.log(`${k} ${r.hashes[k] ?? 'missing'}`);
  } finally { rmSync(dir, { recursive: true, force: true }); }
} else if (argv[0] === '--self-check') {
  // the same vector as tools/selftest/autshadow (IlHash): call, ldstr, br.s, switch, initobj (raw token), ret; a catch and a finally clause
  const il = Buffer.from([0x02, 0x28, 1, 0, 0, 0x0a, 0x72, 1, 0, 0, 0x70, 0x2b, 5, 0x45, 1, 0, 0, 0, 2, 0, 0, 0, 0xfe, 0x15, 2, 0, 0, 0x1b, 0x2a]);
  const names: Record<number, string> = { 0x0a000001: 'System.String::Concat(string,string)', 0x70000001: 'hi' };
  const h = ilHash('K::M()', il, [0, 0, 5, 5, 3, 0, 2, 0, 8, 8, 2, 0], ['System.Exception', null], (t) => names[t] ?? null);
  const want = 0x3214442e9ad01960n;
  console.assert(h === want, `vector hash ${h.toString(16)} != ${want.toString(16)}`);
  console.assert(ilHash('K::M()', null, [], [], () => null) !== ilHash('K::N()', null, [], [], () => null), 'key in the hash');
  const il2 = Buffer.from(il); il2[27] = 0x1c; // only a raw generic token changes
  console.assert(ilHash('K::M()', il2, [], [], (t) => names[t] ?? null) !== ilHash('K::M()', il, [], [], (t) => names[t] ?? null), 'raw token in the hash');
  console.assert(ilHash('K::M()', il, [], [], (t) => (t === 0x70000001 ? 'ho' : names[t] ?? null)) !== ilHash('K::M()', il, [], [], (t) => names[t] ?? null), 'string text in the hash');
  console.log(`autshadow-il-hashes self-check: ${h === want ? 'PASS' : 'FAIL'} (${h.toString(16)})`);
  if (h !== want) process.exit(1);
} else {
  const keys = listKeys();
  const builds: [string, string[]][] = [['EA Steam 1.67', []]];
  if (existsSync(join(SIMLER, 'Sims3GameplaySystems.dll'))) builds.push(['simler90 Gameplay Core Mod UPDATE242 (Steam)', ['Sims3GameplaySystems.dll', 'Sims3GameplayObjects.dll'].map((f) => join(SIMLER, f))]);
  if (existsSync(join(ROOT, 'game-copy/errortrap/ScriptCore.dll'))) builds.push(['NRaas ErrorTrap ScriptCore (VersionStamp 100)', [join(ROOT, 'game-copy/errortrap/ScriptCore.dll')]]);
  for (let i = 0; i < argv.length; i++) if (argv[i] === '--set') { const [l, fs] = argv[++i].split('='); builds.push([l, fs.split(',')]); }
  const all = new Set<string>(), mvids = new Set<string>(), lines: string[] = [];
  for (const [label, overlay] of builds) {
    const r = runBuild(label, overlay);
    const before = all.size;
    for (const h of Object.values(r.hashes)) all.add(h);
    for (const m of r.mvids) mvids.add(m);
    lines.push(`${label}: ${Object.keys(r.hashes).length} of ${keys.length} methods, ${all.size - before} new hashes`);
    if (r.missing.length) lines.push(`  missing: ${r.missing.length} (${r.missing.slice(0, 3).join('; ')})`);
    if (r.stubs.length) lines.push(`  unresolved refs to known types: ${r.stubs.length} (${r.stubs.slice(0, 3).join('; ')})`);
  }
  console.log(lines.join('\n'));
  const sorted = [...all].map((h) => BigInt('0x' + h)).sort((x, y) => (x < y ? -1 : x > y ? 1 : 0));
  const body = [];
  for (let i = 0; i < sorted.length; i += 6) body.push('            ' + sorted.slice(i, i + 6).map((h) => `0x${h.toString(16).padStart(16, '0')}UL`).join(', ') + ',');
  const cs = `// Generated by tools/autshadow-il-hashes.ts (G-M6, PR #112 / docs/215). Do not edit by hand. Hashes only, no game code.
// Builds: ${builds.map((b) => b[0]).join('; ')}.
namespace Ts3Fixes
{
    public static class AutShadowIlHashes
    {
        // AutShadowCore.IlHash of every AutShadowLists.Methods entry per build, sorted (Array.BinarySearch)
        public static readonly ulong[] Known = {
${body.join('\n')}
        };
        // module MVIDs of those builds (raw metadata hex): > 3 misses on these = G-M6 guard fault (our hash), not a mod
        public static readonly string[] KnownMvids = {
${[...mvids].sort().map((m) => `            "${m}",`).join('\n')}
        };
    }
}
`.replace(/\n/g, '\r\n');
  if (argv.includes('--check')) {
    const same = existsSync(OUT) && readFileSync(OUT, 'utf8') === cs;
    console.log(same ? 'AutShadowIlHashes.cs up to date' : 'AutShadowIlHashes.cs differs: re-run without --check');
    if (!same) process.exit(1);
  } else { writeFileSync(OUT, cs); console.log(`wrote ${OUT} (${sorted.length} hashes)`); }
}
