// Read-only decoder for the world waypoint graph R ("RTData", DBPF resource type 0x05CD4BB3) in a .world package.
// Layout and rules: docs/99. Prints stats; the game copy is never written.
// --blend0 also decodes world-builder blend layer 0 (type 0x05DA8AF6), the layer World_IsPointRoutableTerrain 0xC6BAB0
// tests (docs/107 §10).
// Usage: node tools/rtdata-decode.ts <file.world> [--survey] [--checksum] [--chunk N] [--blend0] [--dump nodes|edges] [--mem-image <out>]
//        node tools/rtdata-decode.ts --self-check
import { readFileSync, writeFileSync } from 'node:fs';
import { readIndex, refpack } from './extract-assemblies.ts';

export const TYPE_RTDATA = 0x05cd4bb3;
const CHUNK = 32; // metres per chunk side
const POS = 0.125; // metres per position step inside a chunk (0xFE8278)

export type Waypoint = {
  edges: number; flags: number; type: number; blocked: boolean; edgeBytes: boolean;
  surface: number | null; // stored only in version >= 2; the loader computes it for version 1
  bx: number; bz: number; refs: number[]; edgeByte: number[];
};
export type RTData = { version: number; w: number; h: number; chunks: Waypoint[][] };

// One packed neighbour reference: bits 0-9 waypoint, 10-12 chunk dx+3, 13-15 chunk dz+3.
export function decodeRef(ref: number, chunk: number, w: number) {
  const dx = ((ref >> 10) & 7) - 3, dz = (ref >> 13) - 3;
  return { wp: ref & 0x3ff, dx, dz, chunk: chunk + dz * w + dx }; // no wrap check, as in the game
}

export function parse(b: Buffer): RTData {
  const version = b.readUInt32LE(0), w = b.readUInt32LE(4), h = b.readUInt32LE(8);
  const table = 12, raw = table + w * h * 4;
  if (raw > b.length) throw new Error(`chunk table runs past the resource (${w}x${h})`);
  const head = version < 2 ? 4 : 5; // version 1 has no surface byte
  const chunks: Waypoint[][] = [];
  for (let c = 0; c < w * h; c++) {
    const base = raw + b.readUInt32LE(table + c * 4);
    const count = b.readUInt16LE(base);
    const list: Waypoint[] = [];
    for (let i = 0; i < count; i++) {
      const p = base + b.readUInt16LE(base + 2 + i * 2);
      const n = b[p], flags = b[p + 1];
      const edgeBytes = (flags & 0x40) !== 0;
      const r = p + head, refs: number[] = [], edgeByte: number[] = [];
      for (let k = 0; k < n; k++) refs.push(b.readUInt16LE(r + k * 2));
      for (let k = 0; k < n; k++) edgeByte.push(edgeBytes ? b[r + n * 2 + k] : 0);
      list.push({
        edges: n, flags, type: flags & 0x1f, blocked: (flags & 0x80) !== 0, edgeBytes,
        surface: version < 2 ? null : b[p + 2], bx: b[p + head - 2], bz: b[p + head - 1], refs, edgeByte,
      });
    }
    chunks.push(list);
  }
  return { version, w, h, chunks };
}

// In-memory (and version-2 file) layout the loader 0x66D110 builds: per chunk u16 count + u16 offsets,
// then 5-byte headers (n, flags, surface, bx, bz) + refs + edge bytes. Size check against docs/91 §3.1.
export function memorySize(d: RTData): number {
  let s = 0;
  for (const ch of d.chunks) { s += 2 + 2 * ch.length; for (const wp of ch) s += 5 + 3 * wp.edges; }
  return s;
}

// Connected components the way RTData_BuildComponents 0x66CE10 floods them ("Routing/seeds").
export function components(d: RTData): { color: Int16Array[]; count: number } {
  const color = d.chunks.map(ch => new Int16Array(ch.length).fill(-2));
  d.chunks.forEach((ch, c) => ch.forEach((wp, i) => { if (wp.type === 0xe) color[c][i] = -1; }));
  let next = 0;
  for (let c = 0; c < d.chunks.length; c++) for (let i = 0; i < d.chunks[c].length; i++) {
    if (color[c][i] !== -2) continue;
    const stack = [[c, i]];
    while (stack.length) {
      const [sc, si] = stack.pop()!;
      if (color[sc][si] !== -2) continue;
      color[sc][si] = next;
      for (const ref of d.chunks[sc][si].refs) {
        const t = decodeRef(ref, sc, d.w);
        if (color[t.chunk]?.[t.wp] === -2) stack.push([t.chunk, t.wp]);
      }
    }
    next++; // shortcut: the game stores the colour in a signed byte and stops at colour -2 (254 floods); we count past it
  }
  return { color, count: next };
}

export function stats(d: RTData) {
  const types = new Map<number, number>(), bytes = new Map<number, number>();
  let wps = 0, edges = 0, blocked = 0, noEdgeBytes = 0, bad = 0, oneWay = 0, maxPerChunk = 0, unknownBit = 0;
  const has = (c: number, i: number, back: number) => d.chunks[c]?.[i]?.refs.some(r => {
    const t = decodeRef(r, c, d.w); return t.chunk * 1024 + t.wp === back;
  });
  d.chunks.forEach((ch, c) => {
    maxPerChunk = Math.max(maxPerChunk, ch.length);
    ch.forEach((wp, i) => {
      wps++; edges += wp.edges;
      types.set(wp.type, (types.get(wp.type) ?? 0) + 1);
      if (wp.blocked) blocked++;
      if (wp.flags & 0x20) unknownBit++;
      if (!wp.edgeBytes) noEdgeBytes++;
      wp.refs.forEach((r, k) => {
        bytes.set(wp.edgeByte[k], (bytes.get(wp.edgeByte[k]) ?? 0) + 1);
        const t = decodeRef(r, c, d.w);
        if (!d.chunks[t.chunk]?.[t.wp]) { bad++; return; }
        if (!has(t.chunk, t.wp, c * 1024 + i)) oneWay++;
      });
    });
  });
  const comp = components(d);
  return { wps, edges, blocked, unknownBit, noEdgeBytes, bad, oneWay, maxPerChunk, types, bytes, components: comp.count, memory: memorySize(d) };
}

// Edge counts per "edgeByte:fromType-toType" (types in hex), the cross-tab docs/99 §1.5/§10.4 name the types from.
export function pairs(d: RTData): Map<string, number> {
  const m = new Map<string, number>();
  d.chunks.forEach((ch, c) => ch.forEach(wp => wp.refs.forEach((r, k) => {
    const t = decodeRef(r, c, d.w), q = d.chunks[t.chunk]?.[t.wp];
    if (!q) return;
    const key = `${wp.edgeByte[k]}:${wp.type.toString(16)}-${q.type.toString(16)}`;
    m.set(key, (m.get(key) ?? 0) + 1);
  })));
  return m;
}

// Closed cycles formed by waypoints of one type through same-type references (type 0xE: obstacle outlines, docs/99 §10.4).
// Counts each connected same-type group once; a group is closed when every member has exactly two same-type neighbours.
export function typeLoops(d: RTData, type: number): { groups: number; closed: number; sizes: number[] } {
  const seen = new Set<number>(), sizes: number[] = [];
  let closed = 0;
  d.chunks.forEach((ch, c) => ch.forEach((wp, i) => {
    if (wp.type !== type || seen.has(c * 1024 + i)) return;
    const stack = [[c, i]]; let n = 0, ring = true;
    seen.add(c * 1024 + i);
    while (stack.length) {
      const [sc, si] = stack.pop()!; n++;
      const same = d.chunks[sc][si].refs.map(r => decodeRef(r, sc, d.w)).filter(t => d.chunks[t.chunk]?.[t.wp]?.type === type);
      if (same.length !== 2) ring = false;
      for (const t of same) if (!seen.has(t.chunk * 1024 + t.wp)) { seen.add(t.chunk * 1024 + t.wp); stack.push([t.chunk, t.wp]); }
    }
    sizes.push(n); if (ring) closed++;
  }));
  return { groups: sizes.length, closed, sizes };
}

// Stored surface bytes (version >= 2) against the RTData_WaypointSurfaceClass 0x66CB90 rule for the fixed types.
const FIXED_SURFACE: Record<number, number> = { 1: 0x28, 7: 0x0a, 0xd: 0x1f };
export function surfaceCheck(d: RTData): { fixedOk: number; fixedBad: number; other: Map<number, number> } | null {
  if (d.version < 2) return null;
  let fixedOk = 0, fixedBad = 0; const other = new Map<number, number>();
  for (const ch of d.chunks) for (const wp of ch) {
    if (wp.type in FIXED_SURFACE) { if (wp.surface === FIXED_SURFACE[wp.type]) fixedOk++; else fixedBad++; }
    else other.set(wp.surface!, (other.get(wp.surface!) ?? 0) + 1);
  }
  return { fixedOk, fixedBad, other };
}

// The checksum [ProbeValues] computes over the live R (native/ts3native/values.cpp PvRSum, docs/108): FNV-1a 32 over, per
// chunk, the u16 count and per record n, flags & keep, bx, bz, the refs (u16 LE) and, with flag 0x40, the edge bytes.
// Surface byte and record offsets are left out, so a version-1 file and the loader's converted memory form agree.
// keep 0x7F masks the blocked bit (the one runtime writer, docs/91 §6.6); 0x60 also masks the type bits.
export function rtChecksum(d: RTData, keep = 0x7f): { sum: number; wps: number; blocked: number } {
  let h = 0x811c9dc5, wps = 0, blocked = 0;
  const fnv = (...bytes: number[]) => { for (const x of bytes) h = Math.imul(h ^ (x & 0xff), 16777619) >>> 0; };
  for (const ch of d.chunks) {
    fnv(ch.length, ch.length >> 8);
    for (const wp of ch) {
      fnv(wp.edges, wp.flags & keep, wp.bx, wp.bz);
      for (const r of wp.refs) fnv(r, r >> 8);
      if (wp.edgeBytes) fnv(...wp.edgeByte);
      wps++;
      if (wp.blocked) blocked++;
    }
  }
  return { sum: h >>> 0, wps, blocked };
}

// The loader's in-memory form (= a version-2 resource body after the 12-byte header): u32 offset per chunk relative to the
// record area, then per chunk u16 count + u16 record offsets + 5-byte-head records. Surface byte: the stored one, else the
// 0x66CB90 rule for the fixed types and 0 for the rest (shortcut: the loader samples terrain there; no cost mode reads it).
// version 1 writes the file form without the surface byte (world-gen.ts keeps a version-1 template version 1).
export function memoryImage(d: RTData, version = 2): { offsets: Buffer; raw: Buffer } {
  const offsets = Buffer.alloc(4 * d.chunks.length), parts: Buffer[] = [];
  let at = 0;
  d.chunks.forEach((ch, c) => {
    const recs = ch.map(wp => Buffer.from([wp.edges, wp.flags, ...(version >= 2 ? [wp.surface ?? FIXED_SURFACE[wp.type] ?? 0] : []), wp.bx, wp.bz,
      ...wp.refs.flatMap(r => [r & 0xff, r >> 8]), ...(wp.edgeBytes ? wp.edgeByte : [])]));
    const head = Buffer.alloc(2 + 2 * ch.length); head.writeUInt16LE(ch.length, 0);
    let o = head.length; recs.forEach((r, i) => { head.writeUInt16LE(o, 2 + 2 * i); o += r.length; });
    offsets.writeUInt32LE(at, 4 * c); at += o;
    parts.push(head, ...recs);
  });
  return { offsets, raw: Buffer.concat(parts) };
}

// World position of a waypoint in the cost functions' frame (docs/99 §10.1: no world origin, chunk x = c mod w, z = c div w).
export const wpPos = (d: RTData, c: number, wp: Waypoint) => [(c % d.w) * CHUNK + wp.bx * POS, Math.floor(c / d.w) * CHUNK + wp.bz * POS];

// CSV dumps of R for offline work (docs/99 §11): one row per waypoint or per directed edge, record order kept.
export function dumpRows(d: RTData, what: 'nodes' | 'edges'): string[] {
  const rows = [what === 'nodes' ? 'chunk,wp,type,flags,blocked,x,z,edges,firstRef'
    : 'chunk,wp,k,toChunk,toWp,edgeByte,fromType,toType,length'];
  d.chunks.forEach((ch, c) => ch.forEach((wp, i) => {
    const [x, z] = wpPos(d, c, wp);
    if (what === 'nodes') { rows.push(`${c},${i},${wp.type},${wp.flags},${wp.blocked ? 1 : 0},${x},${z},${wp.edges},${wp.refs[0] ?? ''}`); return; }
    wp.refs.forEach((r, k) => {
      const t = decodeRef(r, c, d.w), q = d.chunks[t.chunk]?.[t.wp];
      const [qx, qz] = q ? wpPos(d, t.chunk, q) : [NaN, NaN];
      rows.push(`${c},${i},${k},${t.chunk},${t.wp},${wp.edgeByte[k]},${wp.type},${q?.type ?? ''},${Math.hypot(qx - x, qz - z).toFixed(4)}`);
    });
  }));
  return rows;
}

export function loadFromPackage(path: string, type = TYPE_RTDATA): Buffer {
  const pkg = readFileSync(path);
  const e = readIndex(pkg).find(x => x.type === type);
  if (!e) throw new Error(`no resource 0x${type.toString(16).toUpperCase().padStart(8, '0')} in ${path}`);
  const body = pkg.subarray(e.offset, e.offset + e.fileSize);
  return e.compressed === 0xffff ? refpack(body) : Buffer.from(body);
}

// World-builder blend layer 0 (type 0x05DA8AF6, instance 1), as the loader 0xC29CE0 -> 0xC23CB0 -> 0xC21C70 reads it:
// u32 magic (low half = version, high half 0xA5FF), u32 w, u32 h (1 m cells), version >= 0x300: u32 record w, u32 record h,
// u32 present, u32 data size, u32 record count, u8 layer index, then records {u8 block hi, u8 block lo, u8 kind,
// kind 1: u8 value for the whole 16x16 block | kind 0: 256 bytes row by row}. Blocks not listed stay 0. Weight = byte / 255.
export const TYPE_BLEND0 = 0x05da8af6;
export type BlendLayer = { version: number; w: number; h: number; bx: number; bz: number; layer: number; blocks: (number | Buffer)[]; uniform: number; raw: number };
export function parseBlendLayer(b: Buffer): BlendLayer {
  const magic = b.readUInt32LE(0);
  if (magic >>> 16 !== 0xa5ff) throw new Error('bad blend map magic 0x' + magic.toString(16));
  const version = magic & 0xffff, w = b.readUInt32LE(4), h = b.readUInt32LE(8);
  const bx = ((w - 1) >>> 4) + 1, bz = ((h - 1) >>> 4) + 1;
  const blocks: (number | Buffer)[] = new Array(bx * bz).fill(0);
  const out = { version, w, h, bx, bz, layer: 0, blocks, uniform: 0, raw: 0 };
  if (version < 0x200) return out; // the loader returns an empty layer
  // shortcut: only 16x16 records (all shipped data seen so far); the game also takes larger square records, decode them when a world has one
  if (version < 0x300 || b.readUInt32LE(12) !== 16 || b.readUInt32LE(16) !== 16) throw new Error('blend layer records other than 16x16 are not decoded');
  if (b.readUInt32LE(20) === 0) return out;
  const count = b.readUInt32LE(28);
  out.layer = b[32];
  let p = 33;
  for (let i = 0; i < count; i++) {
    const idx = (b[p] << 8) | b[p + 1], kind = b[p + 2]; p += 3;
    if (idx >= blocks.length || kind > 1) break; // the loader stops reading here too
    if (kind === 1) { blocks[idx] = b[p++]; out.uniform++; } else { blocks[idx] = b.subarray(p, p + 256); p += 256; out.raw++; }
  }
  return out;
}
// Byte at a 1 m cell, as TerrainBlendMap_GetLayerWeight 0xC22050 -> 0xC21AE0 reads it; -1 outside the map.
export function blendByte(m: BlendLayer, x: number, z: number): number {
  if (x < 0 || z < 0 || x >= m.w || z >= m.h) return -1;
  const v = m.blocks[(z >>> 4) * m.bx + (x >>> 4)];
  return typeof v === 'number' ? v : v[(x & 15) + (z & 15) * 16];
}
// Cells with weight >= 0.5 (byte >= 128): the off-lot cells 0xC6BAB0 rejects. Also counts R waypoints that stand on one.
export function blendStats(m: BlendLayer, r?: RTData) {
  let hi = 0, any = 0, x0 = Infinity, z0 = Infinity, x1 = -1, z1 = -1;
  for (let z = 0; z < m.h; z++) for (let x = 0; x < m.w; x++) {
    const v = blendByte(m, x, z);
    if (v > 0) any++;
    if (v >= 128) { hi++; x0 = Math.min(x0, x); z0 = Math.min(z0, z); x1 = Math.max(x1, x); z1 = Math.max(z1, z); }
  }
  let wps = 0, onHi = 0;
  if (r) r.chunks.forEach((ch, c) => ch.forEach(wp => {
    wps++;
    if (blendByte(m, Math.floor((c % r.w) * CHUNK + wp.bx * POS), Math.floor(Math.floor(c / r.w) * CHUNK + wp.bz * POS)) >= 128) onHi++;
  }));
  return { cells: m.w * m.h, any, hi, box: hi ? [x0, z0, x1, z1] : null, wps, onHi };
}

function hist(m: Map<number, number>) {
  return [...m].sort((a, b) => a[0] - b[0]).map(([k, v]) => `0x${k.toString(16)}:${v}`).join(' ');
}

// Synthetic 2x1-chunk graph in both file versions: checks offsets, ref packing, edge bytes, blocked bit, flood.
function selfCheck() {
  const wp = (n: number, flags: number, bx: number, bz: number, refs: number[], eb: number[], v: number) =>
    Buffer.from([n, flags, ...(v >= 2 ? [0x28] : []), bx, bz, ...refs.flatMap(r => [r & 0xff, r >> 8]), ...((flags & 0x40) ? eb : [])]);
  const ref = (wpi: number, dx: number, dz: number) => wpi | ((dx + 3) << 10) | ((dz + 3) << 13);
  for (const v of [1, 2]) {
    const c0 = [wp(1, 0x41, 8, 16, [ref(0, 1, 0)], [4], v), wp(0, 0x8e, 1, 2, [], [], v)];
    const c1 = [wp(1, 0x42, 200, 255, [ref(0, -1, 0)], [3], v)];
    const chunk = (list: Buffer[]) => {
      const head = Buffer.alloc(2 + 2 * list.length); head.writeUInt16LE(list.length, 0);
      let o = head.length; list.forEach((x, i) => { head.writeUInt16LE(o, 2 + i * 2); o += x.length; });
      return Buffer.concat([head, ...list]);
    };
    const a = chunk(c0), b = chunk(c1);
    const hdr = Buffer.alloc(12 + 8); hdr.writeUInt32LE(v, 0); hdr.writeUInt32LE(2, 4); hdr.writeUInt32LE(1, 8);
    hdr.writeUInt32LE(0, 12); hdr.writeUInt32LE(a.length, 16);
    const d = parse(Buffer.concat([hdr, a, b]));
    console.assert(d.chunks[0].length === 2 && d.chunks[1].length === 1, 'chunk counts');
    const w0 = d.chunks[0][0];
    console.assert(w0.type === 1 && w0.edgeBytes && w0.edgeByte[0] === 4 && w0.bx === 8 && w0.bz === 16, 'record fields v' + v);
    console.assert(decodeRef(w0.refs[0], 0, d.w).chunk === 1 && decodeRef(d.chunks[1][0].refs[0], 1, d.w).chunk === 0, 'ref packing');
    console.assert(d.chunks[0][1].blocked && d.chunks[0][1].type === 0xe, 'blocked bit and type');
    console.assert(d.chunks[0][0].surface === (v >= 2 ? 0x28 : null), 'surface byte');
    const s = stats(d);
    console.assert(s.components === 1 && s.oneWay === 0 && s.bad === 0, 'flood: type 0xE waypoint excluded, one component');
    console.assert(s.memory === (2 + 4 + 5 + 3 + 5) + (2 + 2 + 5 + 3), 'memory size');
    console.assert(pairs(d).get('4:1-2') === 1 && pairs(d).get('3:2-1') === 1, 'pair cross-tab');
    const cs = rtChecksum(d); // the same constants are asserted for PvRSum in native/ts3native/selftest.cpp
    console.assert(cs.sum === 0xe6f9d31a && rtChecksum(d, 0x60).sum === 0x1750a4d1 && cs.wps === 3 && cs.blocked === 1, 'checksum v' + v);
    const im = memoryImage(d), h2 = Buffer.alloc(12); h2.writeUInt32LE(2, 0); h2.writeUInt32LE(d.w, 4); h2.writeUInt32LE(d.h, 8);
    const d2 = parse(Buffer.concat([h2, im.offsets, im.raw])), strip = (x: RTData) => JSON.stringify(x.chunks.map(ch => ch.map(w => ({ ...w, surface: 0 }))));
    console.assert(strip(d2) === strip(d) && im.raw.length === s.memory && d2.chunks[0][0].surface === 0x28, 'memory image round trip v' + v);
    console.assert(dumpRows(d, 'nodes').length === 4 && dumpRows(d, 'edges').length === 3 && dumpRows(d, 'edges')[1] === `0,0,0,1,0,4,1,2,${Math.hypot(56, 29.875).toFixed(4)}`, 'dumps v' + v);
  }
  // A 4-waypoint type-0xE ring (edge byte 6) in one version-2 chunk: one closed loop.
  const ring = [0, 1, 2, 3].map(k => {
    const a = (k + 1) % 4, b = (k + 3) % 4, r = (x: number) => x | (3 << 10) | (3 << 13);
    return Buffer.from([2, 0x4e, 0, k * 8, 0, r(a) & 0xff, r(a) >> 8, r(b) & 0xff, r(b) >> 8, 6, 6]);
  });
  const head = Buffer.alloc(2 + 8); head.writeUInt16LE(4, 0);
  let o = head.length; ring.forEach((x, i) => { head.writeUInt16LE(o, 2 + i * 2); o += x.length; });
  const hdr = Buffer.alloc(16); hdr.writeUInt32LE(2, 0); hdr.writeUInt32LE(1, 4); hdr.writeUInt32LE(1, 8);
  const rd = parse(Buffer.concat([hdr, head, ...ring]));
  const lp = typeLoops(rd, 0xe);
  console.assert(lp.groups === 1 && lp.closed === 1 && lp.sizes[0] === 4, 'type 0xE ring');
  console.assert(surfaceCheck(rd)?.other.get(0) === 4, 'surface check');
  // Blend layer: 32x16 m map = 2 blocks; block 0 uniform 0xFF, block 1 raw with one byte 0x80 at (17, 3).
  const bh = Buffer.alloc(33); bh.writeUInt32LE(0xa5ff0300, 0); bh.writeUInt32LE(32, 4); bh.writeUInt32LE(16, 8);
  bh.writeUInt32LE(16, 12); bh.writeUInt32LE(16, 16); bh.writeUInt32LE(1, 20); bh.writeUInt32LE(2, 28);
  const rawBlock = Buffer.alloc(256); rawBlock[1 + 3 * 16] = 0x80;
  const bl = parseBlendLayer(Buffer.concat([bh, Buffer.from([0, 0, 1, 0xff, 0, 1, 0]), rawBlock]));
  console.assert(bl.uniform === 1 && bl.raw === 1 && blendByte(bl, 5, 5) === 255 && blendByte(bl, 17, 3) === 0x80 && blendByte(bl, 18, 3) === 0 && blendByte(bl, 32, 0) === -1, 'blend layer decode');
  console.assert(blendStats(bl).hi === 257, 'blend layer >= 0.5 count');
  console.log('rtdata-decode self-check done');
}

if (process.argv[1]?.endsWith('rtdata-decode.ts')) {
  const args = process.argv.slice(2);
  if (args[0] === '--self-check' || !args.length) selfCheck();
  else {
    const d = parse(loadFromPackage(args[0]));
    const s = stats(d);
    const say = args.includes('--dump') ? console.error : console.log; // keep stdout pure CSV for --dump
    say(`version ${d.version}, ${d.w}x${d.h} chunks of ${CHUNK} m (${d.w * CHUNK} x ${d.h * CHUNK} m)`);
    say(`waypoints ${s.wps}, directed edges ${s.edges}, max per chunk ${s.maxPerChunk}, blocked ${s.blocked}, flag 0x20 set ${s.unknownBit}, without edge bytes ${s.noEdgeBytes}`);
    say(`refs out of range ${s.bad}, one-way edges ${s.oneWay}, components ${s.components}, in-memory size ${s.memory} bytes`);
    say('waypoint types', hist(s.types));
    say('edge bytes', hist(s.bytes));
    if (args.includes('--survey')) {
      console.log('pairs', [...pairs(d)].sort().map(([k, v]) => `${k}=${v}`).join(' '));
      for (const t of [0xe, 0x11]) {
        const lp = typeLoops(d, t);
        if (lp.groups) console.log(`type 0x${t.toString(16)} groups ${lp.groups}, closed rings ${lp.closed}, largest ${Math.max(...lp.sizes)}`);
      }
      const sc = surfaceCheck(d);
      if (sc) console.log(`stored surface: fixed types ok ${sc.fixedOk} bad ${sc.fixedBad}; other types`, hist(sc.other));
    }
    if (args.includes('--checksum')) { // compare with pvR / the "values world ... R sum A / B" log line of [ProbeValues]
      const a = rtChecksum(d), b = rtChecksum(d, 0x60);
      console.log(`R sum 0x${a.sum.toString(16).toUpperCase().padStart(8, '0')} / 0x${b.sum.toString(16).toUpperCase().padStart(8, '0')}, ${a.wps} waypoints, ${a.blocked} blocked`);
    }
    if (args.includes('--blend0')) {
      const m = parseBlendLayer(loadFromPackage(args[0], TYPE_BLEND0)), bs = blendStats(m, d);
      console.log(`blend layer ${m.layer}: version 0x${m.version.toString(16)}, ${m.w}x${m.h} m, ${m.uniform} uniform and ${m.raw} raw blocks of ${m.bx * m.bz}`);
      console.log(`cells > 0: ${bs.any} (${(100 * bs.any / bs.cells).toFixed(2)} %), weight >= 0.5: ${bs.hi} (${(100 * bs.hi / bs.cells).toFixed(2)} %), box ${bs.box?.join(',') ?? 'none'}`);
      console.log(`R waypoints on weight >= 0.5 cells: ${bs.onHi} of ${bs.wps} (lots not excluded: 0xC6BAB0 accepts those points when a lot is found)`);
    }
    const di = args.indexOf('--dump'); // CSV to stdout; redirect it outside the repo (game data)
    if (di >= 0) for (const r of dumpRows(d, args[di + 1] === 'edges' ? 'edges' : 'nodes')) console.log(r);
    const mi = args.indexOf('--mem-image'); // u32 w, u32 h, offset table, record area: input of tools/emu/specs/world_cost_mode1.py
    if (mi >= 0) {
      const im = memoryImage(d), h = Buffer.alloc(8); h.writeUInt32LE(d.w, 0); h.writeUInt32LE(d.h, 4);
      writeFileSync(args[mi + 1], Buffer.concat([h, im.offsets, im.raw]));
    }
    const ci = args.indexOf('--chunk');
    if (ci >= 0) {
      const c = Number(args[ci + 1]);
      d.chunks[c].forEach((wp, i) => console.log(i, `type 0x${wp.type.toString(16)} flags 0x${wp.flags.toString(16)}`,
        `pos ${((c % d.w) * CHUNK + wp.bx * POS).toFixed(3)},${(Math.floor(c / d.w) * CHUNK + wp.bz * POS).toFixed(3)}`,
        wp.refs.map((r, k) => { const t = decodeRef(r, c, d.w); return `${t.chunk}/${t.wp}:${wp.edgeByte[k]}`; }).join(' ')));
    }
  }
}
