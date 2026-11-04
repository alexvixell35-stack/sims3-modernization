// Names the frames of a ts3native crash: the first-chance `exception` lines of a ts3native.log (eip + stack code refs) or
// a [CrashReport] report (fault + stack, docs/135). One line per frame (docs/135 "Symbolizing a crash").
//   node tools/crash-symbolize.ts <ts3native.log | crash report .txt> [--map <ts3native.map>] [--build <dir>] [--all] [--json]
//   node tools/crash-symbolize.ts --self-check
// Module ranges come from the log: TS3W.exe at its fixed base (`host TS3W.exe base`, image size 0xE5A000), ts3native from
// the `crash log: ... (module 0x..-0x..)` line; report frames are already `module+0xoffset`. TS3W.exe frames are named
// from tools/ghidra/symbols/ts3w-1.67.symbols.csv (nearest named function below: its end is not in the CSV, so the name is
// a hint past 1 KB) or exactly from game-copy/analysis/functions.csv when it is there (local), plus exact address hits in
// docs/data/*.csv (IC, hook, Mono glue tables). ts3native frames need the link map of the same build: `--build <dir>` takes
// ts3native.asi + ts3native.map from a folder (native/ts3native/out, or the CI artifacts once build.yml uploads the map) and
// checks the asi's version against the log; `--map` alone is used unchecked. Other modules stay name+offset.
// Short output: every distinct exception with its first 8 frames; --all every frame; last line `needs review: ...`.
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { parseArgs } from 'node:util';

const ROOT = resolve(import.meta.dirname, '..');
const TS3W_BASE = 0x400000, TS3W_SIZE = 0xe5a000;   // SizeOfImage of the 1.67 exe [verified: PE header of a contributor's copy]
const NEAR = 0x400;                                  // past this distance from the nearest named function: say it may be another
// 0x00XX00YY with XX, YY printable: two UTF-16 characters on the stack, not a return address (stack scans pick them up)
export const looksUtf16 = (a: number) => (a >>> 24) === 0 && (a & 0xff00) === 0 && [(a >>> 16) & 0xff, a & 0xff].every(c => c >= 0x20 && c < 0x7f);
const hex = (n: number) => '0x' + (n >>> 0).toString(16).toUpperCase().padStart(8, '0');
const off = (n: number) => '0x' + n.toString(16).toUpperCase();

export interface Sym { at: number; end?: number; name: string }
export interface Names { ts3w: Sym[]; exact: Map<number, string[]>; map: Sym[] | null; mapBase: number }
export interface Crash { title: string; count: number; frames: { a: number | null; mod?: string; modOff?: number; note?: string }[] }

// nearest symbol at or below a (sorted by at)
export function nearest(syms: Sym[], a: number): Sym | undefined {
  let lo = 0, hi = syms.length - 1;
  while (lo <= hi) { const m = (lo + hi) >> 1; if (syms[m].at <= a) lo = m + 1; else hi = m - 1; }
  return syms[hi];
}

// MSVC /MAP: "Preferred load address is 10000000" and " 0001:00000a40  ?XcptLog@@YGJPAU_EXCEPTION_POINTERS@@@Z  10001a40 f  ts3native.obj"
export function parseMap(text: string): { base: number; syms: Sym[] } {
  const base = parseInt(text.match(/Preferred load address is ([0-9a-f]+)/i)?.[1] ?? '10000000', 16), syms: Sym[] = [];
  for (const m of text.matchAll(/^\s*[0-9a-f]{4}:[0-9a-f]{8}\s+(\S+)\s+([0-9a-f]{8})\s+f\b/gim)) syms.push({ at: parseInt(m[2], 16) - base, name: undecorate(m[1]) });
  return { base, syms: syms.sort((x, y) => x.at - y.at) };
}
// ?Name@@... -> Name, ?Name@Class@@... -> Class::Name, _Name@12 -> Name, _Name -> Name
export function undecorate(s: string): string {
  let m = s.match(/^\?([A-Za-z_$][\w$]*)@([A-Za-z_$][\w$]*)@@/);
  if (m) return `${m[2]}::${m[1]}`;
  if ((m = s.match(/^\?([A-Za-z_$][\w$]*)@@/))) return m[1];
  return s.replace(/^_/, '').replace(/@\d+$/, '');
}

export function loadSymbolsCsv(text: string): Sym[] {
  const out: Sym[] = [];
  for (const l of text.split(/\r?\n/).slice(1)) {
    const m = l.match(/^0x([0-9A-Fa-f]+),([^,]+),function\b/);
    if (m) out.push({ at: parseInt(m[1], 16), name: m[2] });
  }
  return out.sort((x, y) => x.at - y.at);
}
// game-copy/analysis/functions.csv: entry,end,name (hex)
const loadFunctionsCsv = (text: string): Sym[] => text.split(/\r?\n/).slice(1).filter(Boolean)
  .map(l => { const [e, end, name] = l.split(','); return { at: parseInt(e, 16), end: parseInt(end, 16), name }; }).sort((x, y) => x.at - y.at);

// docs/data CSVs with an address column and a name-like column: exact hits only
const NAME_COLS = ['name', 'function_name_or_role', 'mono_function', 'apex_name', 'ic'];
export function loadExact(files: [string, string][]): Map<number, string[]> {
  const out = new Map<number, string[]>();
  for (const [file, text] of files) {
    const L = text.split(/\r?\n/), head = L[0]?.split(',') ?? [];
    const ai = head.findIndex(h => h === 'address' || h === 'addr'), ni = NAME_COLS.map(n => head.indexOf(n)).find(i => i >= 0) ?? -1;
    if (ai < 0 || ni < 0) continue;
    for (const l of L.slice(1)) {
      const c = l.match(/("([^"]|"")*"|[^,]*)/g)?.filter((_, i) => i % 2 === 0) ?? [];
      const a = c[ai]?.match(/^"?0x([0-9A-Fa-f]{6,8})"?$/), n = c[ni]?.replace(/^"|"$/g, '');
      if (a && n) { const k = parseInt(a[1], 16), v = `${file}: ${n}`; out.set(k, [...new Set([...(out.get(k) ?? []), v])]); }
    }
  }
  return out;
}

export function parseInput(text: string): { crashes: Crash[]; version?: string; self?: [number, number]; ts3wBase: number } {
  const L = text.split(/\r?\n/);
  const version = (text.match(/^ts3native (\d+\.\d+\.\d+) \d{4}-/m) ?? text.match(/^ts3native: (\d+\.\d+\.\d+)/m))?.[1];
  const r = text.match(/^crash log: first \d+ first-chance exceptions.*?\(module 0x([0-9A-F]+)-0x([0-9A-F]+)\)/m);
  const self: [number, number] | undefined = r ? [parseInt(r[1], 16), parseInt(r[2], 16)] : undefined;
  const ts3wBase = parseInt(text.match(/^host TS3W\.exe base 0x([0-9A-F]+)/m)?.[1] ?? '400000', 16);
  const crashes: Crash[] = [], seen = new Map<string, Crash>();
  if (/^ts3native crash report \d+/m.test(text)) {  // [CrashReport] report: fault + stack as module+0xoffset
    const frame = (s: string) => { const m = s.trim().match(/^([\w.-]+)\+0x([0-9A-F]+)$/i); return m ? { a: null, mod: m[1], modOff: parseInt(m[2], 16) } : { a: /^0x[0-9A-F]+$/i.test(s.trim()) ? parseInt(s.trim(), 16) : null, note: s.trim() }; };
    const fault = text.match(/^fault: (.*)$/m)?.[1], code = text.match(/^code: (.*)$/m)?.[1] ?? '?';
    const i = L.findIndex(l => l.startsWith('stack ')), frames = [];
    if (fault) frames.push(frame(fault));
    for (let k = i + 1; i >= 0 && k < L.length && /^\s+\S/.test(L[k]); k++) frames.push(frame(L[k]));
    crashes.push({ title: `crash report: ${code}, fault ${fault ?? '?'}`, count: 1, frames });
  }
  for (const l of L) {
    const m = l.match(/^exception ([0-9A-F]{8}) at 0x([0-9A-F]{8})( \(([^)]*\+0x[0-9A-F]+)\))? \(info [^)]*\) thread (\d+) eip ([0-9A-F]{8}) esp [0-9A-F]{8}; stack code refs:(.*)$/);
    if (!m) continue;
    const refs = m[7].trim().split(/\s+/).filter(Boolean), key = `${m[1]} ${m[2]} ${refs.join(' ')}`;
    const hit = seen.get(key);
    if (hit) { hit.count++; continue; }
    const named = m[4]?.match(/^(.*)\+0x([0-9A-F]+)$/i);
    const c: Crash = { title: `exception ${m[1]} at 0x${m[2]}${m[4] ? ` (${m[4]})` : ''}, thread ${m[5]}`, count: 1,
      frames: [named ? { a: parseInt(m[2], 16), mod: named[1], modOff: parseInt(named[2], 16) } : { a: parseInt(m[2], 16) }, ...refs.map(x => ({ a: parseInt(x, 16) }))] };
    seen.set(key, c); crashes.push(c);
  }
  return { crashes, version, self, ts3wBase };
}

// one frame -> text
export function name(f: Crash['frames'][number], n: Names, self: [number, number] | undefined, ts3wBase: number): { text: string; ours: boolean; unknown: boolean } {
  let mod = f.mod?.replace(/\.(exe|dll|asi)$/i, ''), o = f.modOff, a = f.a;
  if (mod && /^ts3w$/i.test(mod) && o !== undefined) a = ts3wBase + o;
  if (a !== null && a !== undefined && !mod) {
    if (a >= ts3wBase && a < ts3wBase + TS3W_SIZE) mod = 'TS3W';
    else if (self && a >= self[0] && a < self[1]) { mod = 'ts3native'; o = a - self[0]; }
  }
  const where = a !== null && a !== undefined ? `${hex(a)}  ` : '';
  if (a !== null && a !== undefined && looksUtf16(a)) return { text: `${where}UTF-16 text "${String.fromCharCode(a & 0xff, (a >>> 16) & 0xff)}" on the stack, not a code address`, ours: false, unknown: false };
  if (mod === 'TS3W' && a !== null && a !== undefined) {
    const s = nearest(n.ts3w, a), ex = n.exact.get(a), d = s ? a - s.at : 0;
    const label = !s ? 'no symbol below' : s.end !== undefined ? (a < s.end ? `${s.name}+${off(d)}` : `after ${s.name} (outside it)`)
      : d <= NEAR ? `${s.name}+${off(d)}` : `${s.name}+${off(d)} (nearest named function below, ${(d / 1024).toFixed(1)} KB: may be another function)`;
    return { text: `${where}TS3W+${off(a - ts3wBase)}  ${label}${ex ? `  [${ex.join('; ')}]` : ''}`, ours: false, unknown: !s };
  }
  if (mod && /^ts3native$/i.test(mod) && o !== undefined) {
    if (!n.map) return { text: `${where}ts3native+${off(o)}  (no map: --build <dir> or --map)`, ours: true, unknown: true };
    const s = nearest(n.map, o);
    return { text: `${where}ts3native+${off(o)}  ${s ? `${s.name}+${off(o - s.at)}` : 'before the first function'}`, ours: true, unknown: !s };
  }
  if (mod) return { text: `${where}${mod}+${off(o ?? 0)}`, ours: false, unknown: false };
  return { text: `${where}${f.note && a === null ? f.note : 'other module (no range in the log)'}`, ours: false, unknown: true };
}

export function render(file: string, input: ReturnType<typeof parseInput>, n: Names, all: boolean, note: string[]): string[] {
  const o = [`crash-symbolize: ${file}: ${input.crashes.length} distinct exception(s)${input.version ? `, ts3native ${input.version}` : ''}`, ...note.map(x => `  ${x}`)];
  let ours = 0;
  for (const c of input.crashes) {
    o.push(`${c.title}${c.count > 1 ? `, ${c.count} times` : ''}`);
    c.frames.forEach((f, i) => {
      const r = name(f, n, input.self, input.ts3wBase);
      if (i === 0 && r.ours) ours++;
      if (all || i < 8) o.push(`  #${i} ${r.text}`);
    });
    if (!all && c.frames.length > 8) o.push(`  ... ${c.frames.length - 8} more frames (--all)`);
  }
  const why = [ours && `${ours} fault(s) inside ts3native`, !n.map && input.crashes.some(c => c.frames.some(f => name(f, n, input.self, input.ts3wBase).text.includes('(no map'))) && 'ts3native frames without a map'].filter(Boolean);
  o.push(`needs review: ${why.length ? why.join(', ') : 'none'}`);
  return o;
}

// --build <dir>: ts3native.asi version must equal the log's; map + asi from the same link (PE timestamp in the map header)
function fromBuild(dir: string, logVersion: string | undefined): { map: string; notes: string[]; problem?: string } {
  const asi = join(dir, 'ts3native.asi'), map = join(dir, 'ts3native.map');
  if (!existsSync(map)) return { map: '', notes: [], problem: `no ts3native.map in ${dir}` };
  const notes: string[] = [];
  if (existsSync(asi)) {
    const b = readFileSync(asi), v = b.toString('latin1').match(/ts3native (\d+\.\d+\.\d+)/)?.[1];
    if (logVersion && v && v !== logVersion) return { map: '', notes, problem: `${dir} holds ts3native ${v}, the log ran ${logVersion}: wrong map` };
    const pe = b.readUInt32LE(0x3c), ts = b.readUInt32LE(pe + 8).toString(16), mts = readFileSync(map, 'latin1').match(/Timestamp is ([0-9a-f]+)/i)?.[1];
    if (mts && mts.replace(/^0+/, '') !== ts.replace(/^0+/, '')) return { map: '', notes, problem: 'ts3native.map and ts3native.asi are from different links' };
    notes.push(`map: ${map} (ts3native ${v ?? '?'}, same link as the asi)`);
  } else notes.push(`map: ${map} (no asi next to it: version not checked)`);
  return { map, notes };
}

function loadNames(mapText: string | null): Names {
  const fc = join(ROOT, 'game-copy', 'analysis', 'functions.csv');
  const ts3w = existsSync(fc) ? loadFunctionsCsv(readFileSync(fc, 'utf8')) : loadSymbolsCsv(readFileSync(join(ROOT, 'tools', 'ghidra', 'symbols', 'ts3w-1.67.symbols.csv'), 'utf8'));
  const dd = join(ROOT, 'docs', 'data');
  const exact = loadExact(readdirSync(dd).filter(f => f.endsWith('.csv')).map(f => [`docs/data/${f}`, readFileSync(join(dd, f), 'utf8')] as [string, string]));
  const m = mapText ? parseMap(mapText) : null;
  return { ts3w, exact, map: m?.syms ?? null, mapBase: m?.base ?? 0 };
}

function selfCheck() {
  let k = 0;
  const t = (c: unknown, m: string) => { console.assert(c, m); if (!c) process.exitCode = 1; k++; };
  t(undecorate('?XcptLog@@YGJPAU_EXCEPTION_POINTERS@@@Z') === 'XcptLog' && undecorate('_DllMain@12') === 'DllMain' && undecorate('?Run@Worker@@QAEXXZ') === 'Worker::Run', 'undecorate');
  const map = parseMap([' ts3native', '', ' Timestamp is 6723a1b2 (Thu Oct 31 10:00:00 2026)', '', ' Preferred load address is 10000000', '',
    '  Address         Publics by Value              Rva+Base       Lib:Object', '',
    ' 0000:00000000       ___guard_flags             00000000     <absolute>',
    ' 0001:00000000       _DllMain@12                10001000 f   ts3native.obj',
    ' 0001:00000a40       ?XcptLog@@YGJPAU_EXCEPTION_POINTERS@@@Z 10001a40 f   ts3native.obj',
    ' 0002:00000010       ?g_data@@3HA               10050010     ts3native.obj'].join('\r\n'));
  t(map.base === 0x10000000 && map.syms.length === 2 && map.syms[1].at === 0x1a40, 'map: functions only, RVA');
  const sym = loadSymbolsCsv('address,name,type,comment\n0x004331F0,zlib_inflate,function,x\n0x004A1BD0,ResourceConfig_ParseDirectives,function,"a, b"\n0x011CB920,g_tmp,data,x\n');
  t(sym.length === 2 && nearest(sym, 0x4331f5)?.name === 'zlib_inflate', 'symbols csv + nearest');
  const exact = loadExact([['docs/data/x.csv', 'ic,name,address,verdict\n7,"World_Foo, Bar",0x004331F0,ok\n'], ['docs/data/y.csv', 'rank,owner\n1,z\n']]);
  t(exact.get(0x4331f0)?.[0] === 'docs/data/x.csv: World_Foo, Bar', 'docs/data exact hit with a quoted comma');
  const N: Names = { ts3w: sym, exact, map: map.syms, mapBase: map.base };
  const log = ['ts3native 0.36.148 2026-10-31 10:00:00', 'host TS3W.exe base 0x00400000', 'crash log: first 16 first-chance exceptions (module 0x66000000-0x66200000)',
    'exception C0000005 at 0x66001A50 (info 00000000 00000004) thread 7 eip 66001A50 esp 0019F000; stack code refs: 004331F8 004A9BD0 01300000',
    '  registers eax 0',
    'exception C0000005 at 0x66001A50 (info 00000000 00000004) thread 7 eip 66001A50 esp 0019F000; stack code refs: 004331F8 004A9BD0 01300000',
    'exception C0000005 at 0x678A3B48 (ApexRadiance.asi+0x1A3B48) (info 00000000 00000000) thread 8 eip 678A3B48 esp 0019E000; stack code refs: 004331F0'].join('\r\n');
  const p = parseInput(log);
  t(p.version === '0.36.148' && p.self?.[0] === 0x66000000 && p.crashes.length === 2 && p.crashes[0].count === 2, 'log: dedupe, version, range');
  const out = render('t.log', p, N, false, []);
  t(out.includes('  #0 0x66001A50  ts3native+0x1A50  XcptLog+0x10'), 'ts3native frame via map');
  t(out.includes('  #1 0x004331F8  TS3W+0x331F8  zlib_inflate+0x8'), 'TS3W frame');
  t(out.some(l => l.startsWith('  #2 0x004A9BD0  TS3W+0xA9BD0  ResourceConfig_ParseDirectives+0x8000 (nearest named function below, 32.0 KB')), 'far symbol flagged');
  t(looksUtf16(0x00430044) && !looksUtf16(0x00420000) && !looksUtf16(0x004331f8) && name({ a: 0x00610043 }, N, undefined, TS3W_BASE).text.includes('UTF-16 text "Ca"'), 'UTF-16 text on the stack');
  t(out.includes('  #3 0x01300000  other module (no range in the log)'), 'outside every range');
  t(out.includes('  #0 0x678A3B48  ApexRadiance+0x1A3B48') && out.includes('  #1 0x004331F0  TS3W+0x331F0  zlib_inflate+0x0  [docs/data/x.csv: World_Foo, Bar]'), 'other module named in the log + exact docs/data hit');
  t(out.at(-1) === 'needs review: 1 fault(s) inside ts3native', 'needs review');
  t(render('t.log', p, { ...N, map: null }, false, []).some(l => l.includes('ts3native+0x1A50  (no map')), 'no map said');
  const rep = parseInput(['ts3native crash report 1', 'ts3native: 0.36.143', 'code: C0000005 (access violation, read 0x00000004)', 'fault: TS3W+0x331F2',
    'stack (return addresses found on the stack, top first):', '  ntdll+0x6919C', '  ts3native+0x1A44', '  TS3W+0xA1BD4', 'exe sha256: x'].join('\n'));
  const ro = render('r.txt', rep, N, true, []);
  t(rep.version === '0.36.143' && ro.includes('  #0 0x004331F2  TS3W+0x331F2  zlib_inflate+0x2') && ro.includes('  #1 ntdll+0x6919C') && ro.includes('  #2 ts3native+0x1A44  XcptLog+0x4') && ro.includes('  #3 0x004A1BD4  TS3W+0xA1BD4  ResourceConfig_ParseDirectives+0x4'), 'crash report frames');
  console.log(`crash-symbolize self-check: ${k} checks${process.exitCode ? ', FAILED' : ', ok'}`);
}

if (import.meta.filename === resolve(process.argv[1] ?? '')) {
  const { values: v, positionals } = parseArgs({ allowPositionals: true, options: {
    map: { type: 'string' }, build: { type: 'string' }, all: { type: 'boolean' }, json: { type: 'boolean' }, 'self-check': { type: 'boolean' } } });
  if (v['self-check']) selfCheck();
  else if (positionals.length !== 1) { console.error('usage: node tools/crash-symbolize.ts <ts3native.log | crash report> [--map <file>] [--build <dir>] [--all] [--json]'); process.exit(2); }
  else {
    const input = parseInput(readFileSync(positionals[0], 'utf8').replace(/^﻿/, '')), notes: string[] = [];
    let mapPath = v.map ?? '';
    if (v.build) {
      const b = fromBuild(v.build, input.version);
      if (b.problem) { console.error(b.problem); process.exit(1); }
      mapPath = b.map; notes.push(...b.notes);
    } else if (mapPath) notes.push(`map: ${mapPath} (not checked against the log's build: --build checks it)`);
    const n = loadNames(mapPath ? readFileSync(mapPath, 'latin1') : null);
    if (v.json) console.log(JSON.stringify(input.crashes.map(c => ({ ...c, frames: c.frames.map(f => name(f, n, input.self, input.ts3wBase).text) })), null, 2));
    else {
      const out = render(positionals[0], input, n, !!v.all, notes);
      console.log(out.join('\n'));
      if (!out.at(-1)!.endsWith('none')) process.exitCode = 1;
    }
  }
}
