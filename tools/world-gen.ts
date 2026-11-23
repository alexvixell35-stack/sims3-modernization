// Test tool (docs/320 §1.5): grows a template .world to a larger square size so the first load of a > 2048 m world can
// show which TS3W size site breaks. Research prototype only; lifting the caps is a separate project (docs/320 §0).
// The template is a local world file (a shipped or CAW world); the output stays outside git (game data inside).
//
// What it grows [verified field positions, file decode of 3 worlds + loader reads, docs/320 §1.2]:
//   0x90624C1B world layer: u32 size x / z in metres at +10 / +14 (read by 0xC65E00 into WM+0xCC / +0xD0)
//   0x06CE4804: u32 size x / z in metres at +28 / +32
//   0x2AD195F2 heightmap: u32 w, u32 h (size + 1), f32 scale, f32, u32, then u16 samples row by row
//   0x05CD4BB3 routing graph R: 32 m chunks; template chunks keep their place, new chunks are empty
//   0x044735DD: u32 2, u32 0, u32 n, n x (u32 cell, u32 0), 8-byte trailer; n = (size / 256)^2
//   0x06DC847E inst 1 (when present): u32 1, u32 w, u32 h, u32 0, then a u32 and a u8 plane of 4 m cells
//   0x033B2B66 / 0xAE39399F per-256 m-cell resources (instance low 32 bits = z << 16 | x in metres, the same u32 at
//     body +20 for 0x033B2B66): --cells clone copies the smallest template cell for every new cell, none adds nothing
//   0x9063660D terrain paint (WTXT): u32 size x / z at +6 / +10 only; they size the terrain cell grid (TerrainData
//     0xC2DA80 / 0xC2E550: cols / rows = ceil(size / 256)); --wtxt keep leaves it at the template size (cell lookups
//     0xC2A470 / 0xC2A4B0 then return null past it). The paint data itself is not grown.
// What it leaves at template size: blend maps 0x3D8632D0 / 0x05DA8AF6 (the reader 0xC21AE0 bounds-checks x / z against
// the map's own w / h, so a smaller map reads 0 outside), lots and every other resource (copied byte for byte).
//
// Usage: node tools/world-gen.ts grow <template.world> <out.world> --size 4096 [--cells clone|none] [--wtxt size|keep]
//          [--fill median|edge]
//        node tools/world-gen.ts check <file.world>      decode the size fields and validate R refs
//        node tools/world-gen.ts install <file.world> [--uninstall]   copy to / remove from <Documents>\...\InstalledWorlds
//        node tools/world-gen.ts --self-check            synthetic template, no game data
import assert from 'node:assert/strict';
import { readFileSync, writeFileSync, copyFileSync, rmSync, mkdirSync, existsSync } from 'node:fs';
import { homedir } from 'node:os';
import { join, basename } from 'node:path';
import { parseArgs } from 'node:util';
import { readIndex, refpack } from './extract-assemblies.ts';
import { writePackage, type Resource } from './dbpf.ts';
import { parse as parseR, memoryImage, decodeRef, type RTData } from './rtdata-decode.ts';

export const T = {
  layer: 0x90624c1b, desc: 0x06ce4804, height: 0x2ad195f2, R: 0x05cd4bb3, cells: 0x044735dd, grid4: 0x06dc847e,
  cellA: 0x033b2b66, cellB: 0xae39399f, wtxt: 0x9063660d, blend: 0x3d8632d0, blend0: 0x05da8af6,
};
const CELL = 256, CHUNK = 32, MAX_SIZE = 4096; // 4096: 0x3D8632D0 tile index is u16 (0xC23BD4), 256 x 256 tiles of 16 m
type Res = Resource & { raw?: boolean }; // raw: data still compressed as in the template (memSize set)

export function readWorld(pkg: Buffer): { res: Res[]; get: (type: number, inst?: bigint, group?: number) => Res | undefined; body: (r: Res) => Buffer } {
  const res: Res[] = readIndex(pkg).map(e => {
    if (e.compressed !== 0 && e.compressed !== 0xffff) throw new Error(`entry 0x${e.type.toString(16)} has compression 0x${e.compressed.toString(16)}`);
    const data = Buffer.from(pkg.subarray(e.offset, e.offset + e.fileSize));
    return e.compressed === 0xffff ? { type: e.type, group: e.group, instance: e.inst, data, memSize: e.memSize, raw: true }
      : { type: e.type, group: e.group, instance: e.inst, data };
  });
  const body = (r: Res) => (r.raw ? refpack(r.data) : r.data);
  return { res, body, get: (type, inst = 1n, group = 0) => res.find(r => r.type === type && r.group === group && r.instance === inst) };
}

// World size in metres from the layer resource; both axes must match (square worlds only, as the tool grows them).
export function worldSize(layer: Buffer): number {
  const x = layer.readUInt32LE(10), z = layer.readUInt32LE(14);
  if (x !== z || x % CELL) throw new Error(`layer size ${x} x ${z}: only square worlds in 256 m steps`);
  return x;
}

export function growHeightmap(b: Buffer, from: number, to: number, fill: 'median' | 'edge'): Buffer {
  const w = b.readUInt32LE(0), h = b.readUInt32LE(4);
  if (w !== from + 1 || h !== from + 1 || b.length !== 20 + w * h * 2) throw new Error(`heightmap ${w} x ${h} (${b.length} bytes) does not match a ${from} m world`);
  const n = to + 1, out = Buffer.alloc(20 + n * n * 2);
  b.copy(out, 0, 0, 20); out.writeUInt32LE(n, 0); out.writeUInt32LE(n, 4);
  const sample = (x: number, z: number) => b.readUInt16LE(20 + (z * w + x) * 2);
  let flat = 0;
  if (fill === 'median') {
    const all = new Uint16Array(w * h); for (let i = 0; i < all.length; i++) all[i] = b.readUInt16LE(20 + i * 2);
    flat = all.sort()[all.length >> 1];
  }
  for (let z = 0; z < n; z++) for (let x = 0; x < n; x++) {
    const v = x < w && z < h ? sample(x, z) : fill === 'edge' ? sample(Math.min(x, w - 1), Math.min(z, h - 1)) : flat;
    out.writeUInt16LE(v, 20 + (z * n + x) * 2);
  }
  return out;
}

// Bad refs of R: target outside the chunk grid (the game's decode wraps rows with no check, docs/99) or past the
// target chunk's waypoint count. Any hit is a crash in game, so grow refuses and check reports it.
export function badRefs(d: RTData): string[] {
  const bad: string[] = [];
  d.chunks.forEach((ch, c) => ch.forEach((wp, i) => wp.refs.forEach(r => {
    const t = decodeRef(r, c, d.w), x = (c % d.w) + t.dx, z = Math.floor(c / d.w) + t.dz;
    if (x < 0 || z < 0 || x >= d.w || z >= d.h || !d.chunks[t.chunk]?.[t.wp]) bad.push(`chunk ${c} wp ${i} -> dx ${t.dx} dz ${t.dz} wp ${t.wp}`);
  })));
  return bad;
}

export function growR(b: Buffer, to: number): { data: Buffer; d: RTData } {
  const d = parseR(b), n = to / CHUNK;
  if (n * n > 0x10000) throw new Error(`${n * n} chunks: the waypoint ref keeps a 16-bit chunk index (max 65,536)`);
  const bad = badRefs(d);
  if (bad.length) throw new Error(`template R has ${bad.length} refs that leave its own grid, first: ${bad[0]}`);
  const chunks: RTData['chunks'] = Array.from({ length: n * n }, () => []);
  d.chunks.forEach((ch, c) => { chunks[Math.floor(c / d.w) * n + (c % d.w)] = ch; }); // refs are relative: they stay valid
  const g: RTData = { version: d.version, w: n, h: n, chunks };
  const after = badRefs(g);
  if (after.length) throw new Error(`grown R has ${after.length} bad refs, first: ${after[0]}`);
  const im = memoryImage(g, d.version), h = Buffer.alloc(12);
  h.writeUInt32LE(d.version, 0); h.writeUInt32LE(n, 4); h.writeUInt32LE(n, 8);
  return { data: Buffer.concat([h, im.offsets, im.raw]), d: g };
}

export function growCellList(b: Buffer, to: number): Buffer {
  const n = b.readUInt32LE(8);
  if (b.readUInt32LE(0) !== 2 || b.length !== 12 + n * 8 + 8) throw new Error(`0x044735DD layout not as decoded (${b.length} bytes, n ${n})`);
  const m = (to / CELL) ** 2, out = Buffer.alloc(12 + m * 8 + 8);
  b.copy(out, 0, 0, 12); out.writeUInt32LE(m, 8);
  for (let i = 0; i < m; i++) out.writeUInt32LE(i, 12 + i * 8);
  b.copy(out, 12 + m * 8, 12 + n * 8);
  return out;
}

export function growGrid4(b: Buffer, from: number, to: number): Buffer {
  const w = b.readUInt32LE(4), h = b.readUInt32LE(8), cw = w / 4, ch = h / 4, nc = to / 4;
  if (b.readUInt32LE(0) !== 1 || w !== from || h !== from || b.length !== 16 + cw * ch * 5) throw new Error(`0x06DC847E layout not as decoded (${w} x ${h}, ${b.length} bytes)`);
  const mode = (vals: number[]) => { const m = new Map<number, number>(); for (const v of vals) m.set(v, (m.get(v) ?? 0) + 1); return [...m].sort((a, c) => c[1] - a[1])[0][0]; };
  const a: number[] = [], u: number[] = [];
  for (let i = 0; i < cw * ch; i++) { a.push(b.readUInt32LE(16 + i * 4)); u.push(b[16 + cw * ch * 4 + i]); }
  const fa = mode(a), fu = mode(u), out = Buffer.alloc(16 + nc * nc * 5);
  b.copy(out, 0, 0, 16); out.writeUInt32LE(to, 4); out.writeUInt32LE(to, 8);
  for (let z = 0; z < nc; z++) for (let x = 0; x < nc; x++) {
    const inT = x < cw && z < ch, i = z * nc + x;
    out.writeUInt32LE(inT ? a[z * cw + x] : fa, 16 + i * 4);
    out[16 + nc * nc * 4 + i] = inT ? u[z * cw + x] : fu;
  }
  return out;
}

const cellInst = (x: number, z: number) => BigInt(((z << 16) | x) >>> 0);

// New per-cell resources for cells outside the template: a copy of the template's smallest one with the coordinates
// patched (instance; body +20 for 0x033B2B66, where the template stores its own instance).
export function cloneCells(world: ReturnType<typeof readWorld>, type: number, from: number, to: number): Res[] {
  const have = world.res.filter(r => r.type === type);
  if (!have.length) return [];
  const out: Res[] = [];
  for (const hi of new Set(have.map(r => r.instance >> 32n))) {
    const pick = have.filter(r => r.instance >> 32n === hi).sort((a, b) => (a.memSize ?? a.data.length) - (b.memSize ?? b.data.length))[0];
    const body = Buffer.from(world.body(pick));
    for (let z = 0; z < to; z += CELL) for (let x = 0; x < to; x += CELL) {
      if (x < from && z < from) continue;
      const inst = (hi << 32n) | cellInst(x, z), data = Buffer.from(body);
      if (type === T.cellA) {
        if (data.readUInt32LE(20) !== Number(pick.instance & 0xffffffffn)) throw new Error('0x033B2B66 body +20 does not hold its instance');
        data.writeUInt32LE(Number(inst & 0xffffffffn), 20);
      }
      out.push({ type, group: pick.group, instance: inst, data });
    }
  }
  return out;
}

export type GrowOpts = { size: number; cells: 'clone' | 'none'; wtxt: 'size' | 'keep'; fill: 'median' | 'edge' };
export function grow(pkg: Buffer, o: GrowOpts): { out: Buffer; log: string[] } {
  const world = readWorld(pkg), log: string[] = [];
  const need = (type: number) => { const r = world.get(type); if (!r) throw new Error(`template has no 0x${type.toString(16).toUpperCase()} instance 1`); return r; };
  const layer = need(T.layer), from = worldSize(world.body(layer)), to = o.size;
  if (to % CELL || to < from) throw new Error(`--size ${to}: must be a multiple of ${CELL} and >= the template's ${from}`);
  if (to > MAX_SIZE) throw new Error(`--size ${to}: over ${MAX_SIZE} m the 0x3D8632D0 tile index (u16, 0xC23BD4) overflows; needs a format change (docs/320)`);
  const set = (r: Res, data: Buffer) => { r.data = data; delete r.memSize; delete r.raw; };
  const sizes = (r: Res, at: number[], what: string) => {
    const b = Buffer.from(world.body(r));
    for (const p of at) { if (b.readUInt32LE(p) !== from) throw new Error(`${what} +${p} = ${b.readUInt32LE(p)}, expected ${from}`); b.writeUInt32LE(to, p); }
    set(r, b); log.push(`${what}: ${from} -> ${to} m`);
  };
  sizes(layer, [10, 14], '0x90624C1B size');
  sizes(need(T.desc), [28, 32], '0x06CE4804 size');
  const hm = need(T.height); set(hm, growHeightmap(world.body(hm), from, to, o.fill)); log.push(`heightmap ${to + 1} x ${to + 1} (${o.fill} fill)`);
  const r = need(T.R), g = growR(world.body(r), to); set(r, g.data); log.push(`R ${g.d.w} x ${g.d.h} chunks, refs checked`);
  const cl = need(T.cells); set(cl, growCellList(world.body(cl), to)); log.push(`0x044735DD ${(to / CELL) ** 2} cells`);
  const g4 = world.get(T.grid4);
  if (g4) { set(g4, growGrid4(world.body(g4), from, to)); log.push(`0x06DC847E ${to / 4} x ${to / 4} cells`); }
  if (o.wtxt === 'size') { const w = need(T.wtxt); const b = Buffer.from(world.body(w)); // u16 version, u32 0, u32 x, u32 z
    for (const p of [6, 10]) { if (b.readUInt32LE(p) !== from) throw new Error(`0x9063660D +${p} = ${b.readUInt32LE(p)}`); b.writeUInt32LE(to, p); }
    set(w, b); log.push('0x9063660D size fields patched (paint data unchanged)'); }
  if (o.cells === 'clone') for (const type of [T.cellA, T.cellB]) {
    const add = cloneCells(world, type, from, to); world.res.push(...add); log.push(`0x${type.toString(16).toUpperCase()} +${add.length} cloned cells`);
  }
  return { out: writePackage(world.res), log };
}

export function check(pkg: Buffer): string[] {
  const w = readWorld(pkg), out: string[] = [];
  const b = (type: number) => { const r = w.get(type); return r && w.body(r); };
  const layer = b(T.layer), desc = b(T.desc), hm = b(T.height), r = b(T.R), cl = b(T.cells), g4 = b(T.grid4), wt = b(T.wtxt);
  if (layer) out.push(`layer v${layer.readUInt16LE(0)} min ${layer.readFloatLE(2)},${layer.readFloatLE(6)} size ${layer.readUInt32LE(10)} x ${layer.readUInt32LE(14)}`);
  if (desc?.length === 36) out.push(`0x06CE4804 size ${desc.readUInt32LE(28)} x ${desc.readUInt32LE(32)}`);
  if (hm) out.push(`heightmap ${hm.readUInt32LE(0)} x ${hm.readUInt32LE(4)}`);
  if (cl) out.push(`0x044735DD cells ${cl.readUInt32LE(8)}`);
  if (g4) out.push(`0x06DC847E ${g4.readUInt32LE(4)} x ${g4.readUInt32LE(8)}`);
  if (wt) out.push(`0x9063660D v${wt.readUInt16LE(0)} size ${wt.readUInt32LE(6)} x ${wt.readUInt32LE(10)}`);
  for (const t of [T.blend, T.blend0]) { const m = b(t); if (m) out.push(`0x${t.toString(16).toUpperCase()} ${m.readUInt32LE(4)} x ${m.readUInt32LE(8)}`); }
  for (const t of [T.cellA, T.cellB]) out.push(`0x${t.toString(16).toUpperCase()} ${w.res.filter(x => x.type === t).length} resources`);
  if (r) { const d = parseR(r), bad = badRefs(d); out.push(`R ${d.w} x ${d.h} chunks, ${d.chunks.reduce((s, c) => s + c.length, 0)} waypoints, bad refs ${bad.length}${bad.length ? ', first ' + bad[0] : ''}`); }
  return out;
}

const installDir = () => join(process.env.TS3_USER ?? join(homedir(), 'Documents', 'Electronic Arts', 'The Sims 3'), 'InstalledWorlds');

// Synthetic 512 m template (no game data): layer, desc, heightmap, R 16 x 16 chunks, cell list, 4 m grid, cell resources.
function synthWorld(opts: { wrapRef?: boolean } = {}): Buffer {
  const S = 512, res: Resource[] = [];
  const add = (type: number, data: Buffer, instance = 1n, group = 0) => res.push({ type, group, instance, data });
  const layer = Buffer.alloc(39); layer.writeUInt16LE(14, 0); layer.writeUInt32LE(S, 10); layer.writeUInt32LE(S, 14); add(T.layer, layer);
  const desc = Buffer.alloc(36); desc.writeUInt32LE(1, 0); desc.writeUInt32LE(S, 28); desc.writeUInt32LE(S, 32); add(T.desc, desc);
  const hm = Buffer.alloc(20 + 513 * 513 * 2); hm.writeUInt32LE(513, 0); hm.writeUInt32LE(513, 4); hm.writeFloatLE(0.01, 8); hm.writeFloatLE(1, 12); hm.writeUInt32LE(1, 16);
  for (let i = 0; i < 513 * 513; i++) hm.writeUInt16LE(i % 7 === 0 ? 900 : 1000 + (i % 3), 20 + i * 2);
  add(T.height, hm);
  // R: waypoint 0 in chunk 0 <-> waypoint 0 in chunk 1; chunk 15 (right edge) points right (dx +1) when wrapRef.
  const ref = (wp: number, dx: number, dz: number) => wp | ((dx + 3) << 10) | ((dz + 3) << 13);
  const rec = (refs: number[]) => Buffer.from([refs.length, 0x42, 0x28, 10, 20, ...refs.flatMap(r => [r & 0xff, r >> 8]), ...refs.map(() => 7)]);
  const chunk = (recs: Buffer[]) => { const h = Buffer.alloc(2 + 2 * recs.length); h.writeUInt16LE(recs.length, 0); let o = h.length; recs.forEach((x, i) => { h.writeUInt16LE(o, 2 + 2 * i); o += x.length; }); return Buffer.concat([h, ...recs]); };
  const chunks = Array.from({ length: 256 }, (_, c) => c === 0 ? chunk([rec([ref(0, 1, 0), ref(0, 0, 1)])]) : c === 1 ? chunk([rec([ref(0, -1, 0)])])
    : c === 16 ? chunk([rec([ref(0, 0, -1)])]) : c === 15 && opts.wrapRef ? chunk([rec([ref(0, 1, 0)])]) : chunk([]));
  const off = Buffer.alloc(256 * 4); let at = 0; chunks.forEach((c, i) => { off.writeUInt32LE(at, i * 4); at += c.length; });
  const rh = Buffer.alloc(12); rh.writeUInt32LE(2, 0); rh.writeUInt32LE(16, 4); rh.writeUInt32LE(16, 8); add(T.R, Buffer.concat([rh, off, ...chunks]));
  const cl = Buffer.alloc(12 + 4 * 8 + 8); cl.writeUInt32LE(2, 0); cl.writeUInt32LE(4, 8); for (let i = 0; i < 4; i++) cl.writeUInt32LE(i, 12 + i * 8); cl.writeUInt32LE(1, 12 + 32); add(T.cells, cl);
  const n4 = 128 * 128, g4 = Buffer.alloc(16 + n4 * 5); g4.writeUInt32LE(1, 0); g4.writeUInt32LE(S, 4); g4.writeUInt32LE(S, 8);
  for (let i = 0; i < n4; i++) g4.writeUInt32LE(i === 5 ? 7 : 0x80000000, 16 + i * 4); add(T.grid4, g4);
  for (let z = 0; z < S; z += CELL) for (let x = 0; x < S; x += CELL) {
    const b = Buffer.alloc(222 + x / 8); b.writeUInt32LE((z << 16) | x, 20); add(T.cellA, b, cellInst(x, z), 1);
    add(T.cellB, Buffer.alloc(100 + x), cellInst(x, z), 1); add(T.cellB, Buffer.alloc(50 + x), (1n << 48n) | cellInst(x, z), 1);
  }
  const wt = Buffer.alloc(64); wt.writeUInt16LE(12, 0); wt.writeUInt32LE(S, 6); wt.writeUInt32LE(S, 10); add(T.wtxt, wt);
  add(0x12345678, Buffer.from('untouched'), 7n);
  return writePackage(res);
}

function selfCheck() {
  const tpl = synthWorld();
  const { out, log } = grow(tpl, { size: 1024, cells: 'clone', wtxt: 'size', fill: 'median' });
  const w = readWorld(out), b = (t: number, i = 1n) => w.body(w.get(t, i)!);
  assert.equal(worldSize(b(T.layer)), 1024);
  assert.deepEqual([b(T.desc).readUInt32LE(28), b(T.desc).readUInt32LE(32)], [1024, 1024]);
  const hm = b(T.height); assert.equal(hm.readUInt32LE(0), 1025); assert.equal(hm.length, 20 + 1025 * 1025 * 2);
  assert.equal(hm.readUInt16LE(20 + (3 * 1025 + 4) * 2), 1000 + ((3 * 513 + 4) % 3), 'template sample kept in place');
  assert.equal(hm.readUInt16LE(20 + (1024 * 1025 + 1024) * 2), 1001, 'median fill outside the template');
  const tw = readWorld(tpl), thm = tw.body(tw.get(T.height)!), edge = growHeightmap(thm, 512, 1024, 'edge');
  assert.equal(edge.readUInt16LE(20 + 900 * 2), thm.readUInt16LE(20 + 512 * 2), 'edge fill clamps to the last template column');
  const r = parseR(b(T.R)); assert.equal(r.w, 32); assert.equal(r.chunks.length, 1024);
  assert.equal(r.chunks[1].length, 1); assert.equal(r.chunks[32].length, 1, 'template row 1 moves to new row 1'); assert.equal(r.chunks[16].length, 0);
  assert.equal(decodeRef(r.chunks[0][0].refs[1], 0, r.w).chunk, 32, 'dz ref follows the new row width');
  assert.equal(badRefs(r).length, 0);
  assert.equal(b(T.cells).readUInt32LE(8), 16); assert.equal(b(T.cells).readUInt32LE(12 + 15 * 8), 15); assert.equal(b(T.cells).readUInt32LE(12 + 16 * 8), 1, 'trailer kept');
  const g4 = b(T.grid4); assert.equal(g4.readUInt32LE(4), 1024); assert.equal(g4.readUInt32LE(16 + 5 * 4), 7); assert.equal(g4.readUInt32LE(16 + (256 * 256 - 1) * 4), 0x80000000, 'mode fill');
  assert.equal(g4.length, 16 + 256 * 256 * 5);
  assert.equal(w.res.filter(x => x.type === T.cellA).length, 16); assert.equal(w.res.filter(x => x.type === T.cellB).length, 32);
  const c = w.body(w.get(T.cellA, cellInst(768, 512), 1)!); assert.equal(c.readUInt32LE(20), (512 << 16) | 768); assert.equal(c.length, 222, 'smallest template cell cloned');
  assert.equal(b(T.wtxt).readUInt32LE(6), 1024);
  assert.equal(b(0x12345678, 7n).toString(), 'untouched');
  assert.ok(log.length >= 7);
  const same = readWorld(grow(tpl, { size: 512, cells: 'none', wtxt: 'keep', fill: 'median' }).out);
  for (const t of [T.layer, T.height, T.R, T.cells, T.grid4]) assert.ok(same.body(same.get(t)!).equals(tw.body(tw.get(t)!)), `control: 0x${t.toString(16)} unchanged at the same size`);
  const r2 = parseR(tw.body(tw.get(T.R)!)), v1 = memoryImage(r2, 1), h1 = Buffer.alloc(12); h1.writeUInt32LE(1, 0); h1.writeUInt32LE(16, 4); h1.writeUInt32LE(16, 8);
  const f1 = Buffer.concat([h1, v1.offsets, v1.raw]);
  assert.ok(growR(f1, 512).data.equals(f1), 'version-1 R stays version 1, byte for byte');
  assert.throws(() => grow(tpl, { size: 1000, cells: 'none', wtxt: 'keep', fill: 'median' }), /multiple of 256/);
  assert.throws(() => grow(tpl, { size: 4352, cells: 'none', wtxt: 'keep', fill: 'median' }), /tile index/);
  assert.throws(() => grow(synthWorld({ wrapRef: true }), { size: 1024, cells: 'none', wtxt: 'keep', fill: 'median' }), /leave its own grid/);
  assert.equal(check(synthWorld({ wrapRef: true })).at(-1)!.includes('bad refs 1'), true, 'check reports a wrapping ref');
  console.log('world-gen self-check done');
}

if (process.argv[1]?.endsWith('world-gen.ts')) {
  const [cmd, ...rest] = process.argv.slice(2);
  if (!cmd || cmd === '--self-check') selfCheck();
  else if (cmd === 'grow') {
    const { values, positionals } = parseArgs({ args: rest, allowPositionals: true, options: { size: { type: 'string' }, cells: { type: 'string', default: 'clone' }, wtxt: { type: 'string', default: 'size' }, fill: { type: 'string', default: 'median' } } });
    const [src, dst] = positionals;
    if (!src || !dst || !values.size) throw new Error('usage: grow <template.world> <out.world> --size N');
    if (!['clone', 'none'].includes(values.cells!) || !['median', 'edge'].includes(values.fill!) || !['size', 'keep'].includes(values.wtxt!)) throw new Error('--cells clone|none, --fill median|edge, --wtxt size|keep');
    const r = grow(readFileSync(src), { size: Number(values.size), cells: values.cells as 'clone', wtxt: values.wtxt as 'size', fill: values.fill as 'median' });
    writeFileSync(dst, r.out);
    for (const l of [...r.log, `wrote ${dst} (${(r.out.length / 1048576).toFixed(1)} MB)`, ...check(r.out)]) console.log(l);
  } else if (cmd === 'check') for (const l of check(readFileSync(rest[0]))) console.log(l);
  else if (cmd === 'install') {
    const dir = installDir(), dst = join(dir, basename(rest[0]));
    if (rest.includes('--uninstall')) { if (existsSync(dst)) rmSync(dst); console.log(`removed ${dst}`); }
    else { mkdirSync(dir, { recursive: true }); copyFileSync(rest[0], dst); console.log(`installed ${dst}`); }
  } else throw new Error(`unknown command ${cmd}`);
}
