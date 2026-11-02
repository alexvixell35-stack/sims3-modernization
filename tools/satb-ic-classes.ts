// docs/206 §7.3, §9: EA internal calls whose native body can write managed memory through an argument (a byref/out parameter
// or an array), for [AutShadow] Satb. While a barrier span is on, a sim-thread call of one of these logs the range each such
// argument can write (byref: the parameter type's size; array: its elements; byref array: the slot and the current array's
// elements) before the body runs, so the worker keeps reading cut values. Only arguments into shared memory are logged.
// Input: the managed call-graph index's methods.csv (game-copy/analysis/callgraph, `node tools/managed-callgraph.ts`) and
// game-copy/analysis/internal-calls-map.csv (the EA IC names the guard thunks cover). Output: native/ts3native/satb-ics.h
// (IC name -> (argument dword, kind, size) per byref/array parameter). Names and sizes only, no game code.
// None of the 180 ICs returns a struct (game-copy/monohost/hostdata.txt kind 's': 0), so no hidden result pointer comes first.
// Usage: node tools/satb-ic-classes.ts [--out <file>] | --self-check
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import assert from 'node:assert/strict';

const ROOT = join(import.meta.dirname, '..');
// by-value sizes in bytes [verified for Vector3/LotLocation/ResourceKey in docs/122 §14.8; the rest from the SimIFace field
// lists (managed-callgraph typeModel, 2026-10-30): RouteAnimationSpec 4 x uint, PackageId 2 x ulong, CASPart ResourceKey +
// 4 x 4 bytes + 2 x ResourceKey + long, PathData uint + ulong + Vector3, PondInfo 9 x Vector3 + float + uint + ulong; a 64-bit
// field after a 4-byte one sits at the next 8-byte boundary (MSVC layout of the native side that writes it) [inferred]]
export const STRUCT_B: Record<string, number> = {
  'Sims3.SimIFace.Vector3': 12, 'Sims3.SimIFace.Vector2': 8, 'Sims3.SimIFace.Quaternion': 16, 'Sims3.SimIFace.ResourceKey': 16,
  'Sims3.SimIFace.LotLocation': 12, 'Sims3.SimIFace.ObjectGuid': 8, 'Sims3.SimIFace.Rect': 16, 'Sims3.SimIFace.Matrix44': 64,
  'System.Decimal': 16, 'Sims3.SimIFace.RouteAnimationSpec': 16, 'Sims3.SimIFace.PackageId': 16, 'Sims3.SimIFace.CAS.CASPart': 72,
  'Sims3.SimIFace.PathData': 32, 'Sims3.SimIFace.PondInfo': 128,
};
export const PRIM_B: Record<string, number> = { bool: 1, byte: 1, sbyte: 1, short: 2, ushort: 2, char: 2, int: 4, uint: 4, float: 4,
  long: 8, ulong: 8, double: 8, string: 4, object: 4, IntPtr: 4, UIntPtr: 4, 'System.IntPtr': 4 };
const REF_TYPES = /^(System\.(Type|Delegate|Array|Object|String|Exception)|.*(Delegate|Callback|Handler))$/;
const ENUM_LIKE = /^Sims3\.SimIFace\.(eRoomDefinition|FloorFeatureFlags|LotDetailPriority|ThumbnailSize|ThumbnailSizeMask|ThumbnailTechnique|SACS\.DriverRequestFlags|TelemetryModuleId|TelemetryGroupId|CustomContent\.ExportContentType)$|^ScriptCore\..*\+e[A-Z]|^Sims3Launcher\./;

// kinds (satb.cpp SatbIcSim): 1 byref of `size` bytes, 2 array (size = element bytes, 0 = ask Mono), 3 byref array (the slot,
// then the current array's elements; size = element bytes), 4 layout unknown (an unsized by-value struct before it: wait)
export const K_BYREF = 1, K_ARRAY = 2, K_BYREF_ARRAY = 3, K_UNKNOWN = 4;
export type IcArg = { at: number; kind: number; size: number };
export type IcRow = { name: string; args: IcArg[]; params: string[] };
const sizeOf = (t: string): number | undefined => PRIM_B[t] ?? STRUCT_B[t] ?? (REF_TYPES.test(t) || ENUM_LIKE.test(t) ? 4 : undefined);
const dwords = (b: number) => Math.ceil(b / 4);

// the byref/array arguments of one parameter list; null = none
export function classify(params: string[]): IcArg[] | null {
  const out: IcArg[] = [];
  let at = 0, lost = false;
  for (const p of params) {
    const arrRef = p.endsWith('[]&'), arr = p.endsWith('[]'), ref = p.endsWith('&');
    if (arrRef || arr || ref) {
      const base = p.replace(/\[\]&$|\[\]$|&$/, '');
      const s = sizeOf(base);
      if (lost) out.push({ at: 0, kind: K_UNKNOWN, size: 0 });
      else if (arrRef) out.push({ at, kind: K_BYREF_ARRAY, size: s ?? 0 });
      else if (arr) out.push({ at, kind: K_ARRAY, size: s ?? 0 });
      else if (s !== undefined) out.push({ at, kind: K_BYREF, size: s });
      else out.push({ at, kind: K_UNKNOWN, size: 0 });
      at += 1;
      continue;
    }
    const s = sizeOf(p);
    if (s === undefined) lost = true; else at += dwords(s);
  }
  return out.length ? out : null;
}
export function parseSig(sig: string): string[] {
  const inner = sig.slice(sig.indexOf('(') + 1, sig.lastIndexOf(')'));
  return inner ? inner.split(',') : [];
}

export function build(methodsCsv: string, mapCsv: string): IcRow[] {
  const ea = new Set(mapCsv.split('\n').slice(1).map(l => l.split(',')[0]).filter(Boolean));
  const rows: IcRow[] = [];
  for (const l of methodsCsv.split('\n')) {
    const m = /^"([^"]+)",([^,]*),([^,]*),/.exec(l);
    if (!m || !m[3] || !ea.has(m[3])) continue;
    const params = parseSig(m[1]), args = classify(params);
    if (args && !rows.some(r => r.name === m[3])) rows.push({ name: m[3], args, params });
  }
  return rows.sort((a, b) => a.name < b.name ? -1 : 1);
}

const SATB_IC_MAXARGS = 8;
function header(rows: IcRow[]): string {
  assert.ok(rows.every(r => r.args.length <= SATB_IC_MAXARGS));
  const arg = (a: IcArg) => `{${a.at}, ${a.kind}, ${a.size}}`;
  return `// Generated by tools/satb-ic-classes.ts (docs/206 §7.3, §9): EA ICs with byref/out or array parameters; per parameter the
// argument dword it sits at, the kind (1 byref, 2 array, 3 byref array, 4 layout unknown: wait) and the size in bytes (byref:
// the type; array kinds: one element, 0 = ask Mono). Do not edit by hand.
struct SatbIcArg { u8 at, kind; WORD size; };
struct SatbIcRow { const char* name; u8 n; SatbIcArg a[${SATB_IC_MAXARGS}]; };
static const SatbIcRow kSatbIcs[] = {
${rows.map(r => `    {"${r.name}", ${r.args.length}, {${r.args.map(arg).join(', ')}}},`).join('\n')}
};
static const int SATB_NICS = ${rows.length};
`;
}

function selfCheck() {
  assert.deepEqual(classify(['Sims3.SimIFace.Vector3', 'Sims3.SimIFace.LotLocation&', 'Sims3.SimIFace.eRoomDefinition']), [{ at: 3, kind: K_BYREF, size: 12 }]);
  assert.equal(classify(['ulong', 'uint']), null);
  assert.deepEqual(classify(['Sims3.SimIFace.Unknown', 'float&']), [{ at: 0, kind: K_UNKNOWN, size: 0 }]);
  assert.deepEqual(classify(['byte[]', 'int']), [{ at: 0, kind: K_ARRAY, size: 1 }]);
  // ulong = 2 dwords, the old table's false positive (an object id scanned as a pointer) cannot happen: only the byref's dword
  assert.deepEqual(classify(['ulong', 'Sims3.SimIFace.Vector3&', 'ulong[]&', 'Sims3.SimIFace.RouteAnimationSpec', 'bool&']),
    [{ at: 2, kind: K_BYREF, size: 12 }, { at: 3, kind: K_BYREF_ARRAY, size: 8 }, { at: 8, kind: K_BYREF, size: 1 }]);
  assert.deepEqual(classify(['Sims3.SimIFace.PathData&']), [{ at: 0, kind: K_BYREF, size: 32 }]);
  assert.deepEqual(classify(['Sims3.SimIFace.Foo[]']), [{ at: 0, kind: K_ARRAY, size: 0 }]);
  assert.deepEqual(parseSig('A::B(int,Sims3.SimIFace.Vector3&)'), ['int', 'Sims3.SimIFace.Vector3&']);
  const csv = '"ScriptCore.World::World_GetLotLocationImpl(Sims3.SimIFace.Vector3,Sims3.SimIFace.LotLocation&,Sims3.SimIFace.eRoomDefinition)",ScriptCore,ScriptCore.World::World_GetLotLocationImpl,0\n' +
    '"ScriptCore.World::World_GetSeaLevelImpl()",ScriptCore,ScriptCore.World::World_GetSeaLevelImpl,0\n';
  const rows = build(csv, 'name,address\nScriptCore.World::World_GetLotLocationImpl,0x1\nScriptCore.World::World_GetSeaLevelImpl,0x2\n');
  assert.deepEqual(rows.map(r => [r.name, r.args]), [['ScriptCore.World::World_GetLotLocationImpl', [{ at: 3, kind: K_BYREF, size: 12 }]]]);
  assert.match(header(rows), /\{"ScriptCore\.World::World_GetLotLocationImpl", 1, \{\{3, 1, 12\}\}\},/);
  console.log('self-check ok');
}

if (process.argv.includes('--self-check')) selfCheck();
else {
  const gc = join(process.env.TS3_REPO ?? ROOT, 'game-copy', 'analysis');  // a worktree: TS3_REPO=<main checkout>
  const oi = process.argv.indexOf('--out');
  const out = oi >= 0 ? process.argv[oi + 1] : join(ROOT, 'native', 'ts3native', 'satb-ics.h');
  const rows = build(readFileSync(join(gc, 'callgraph', 'methods.csv'), 'utf8'), readFileSync(join(gc, 'internal-calls-map.csv'), 'utf8'));
  writeFileSync(out, header(rows));
  const k = (n: number) => rows.reduce((t, r) => t + r.args.filter(a => a.kind === n).length, 0);
  console.log(`${rows.length} ICs with byref/array parameters: ${k(K_BYREF)} byref, ${k(K_ARRAY)} array, ${k(K_BYREF_ARRAY)} byref array, ` +
    `${k(K_UNKNOWN)} layout unknown (wait) -> ${out}`);
}
