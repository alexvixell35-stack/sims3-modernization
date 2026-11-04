// CC mesh scan (docs/67 §28 crash X, §29 issue #80): lists the CAS meshes (GEOM) in mod packages that have the shapes
// behind those two Sim builder crashes. Read-only. Reads each package's index and only its GEOM and VPXY bodies, with
// positioned reads, so multi-GB merged packages are not loaded whole.
//   node tools/cc-mesh-scan.ts [<folder or .package>...]   default <Documents>\Electronic Arts\The Sims 3\Mods (env TS3_USER)
//   node tools/cc-mesh-scan.ts --repro <package> <GEOM instance hex> <out.package>
//   node tools/cc-mesh-scan.ts --self-check
// Shapes:
//   no-material  shader 0 (no embedded material) with vertices, named by a LOD entry of a part's mesh VPXY (one that
//                also names a mesh with a material) in the same package: the per-part step 0x5DDB30 reads the null
//                material at 0x5DDFBD (§29, a morph mesh used as a LOD; `[EaCrashFixes] GeomNoMaterial`). Morph VPXYs
//                (fat/fit/thin/special) name only shader-0 meshes (position, normal, vertex id) by design: not listed.
//   bone-past    a bone-assignment index past the mesh's own bone list: the skin pass 0x5D1010 reads past the list
//                (crash X, §28; `GeomBoneIndex`). "weighted" = vertices whose past-the-list index has weight > 0; 0 means
//                filler in unused slots, the only kind the §28.4 scan found.
// --repro (TEST ONLY): writes a one-resource package with that GEOM's material removed and shader 0, the no-material
// shape, for a guard on/off check in game. Put it in Mods\Overrides\<dir>\ for one session and delete it afterwards.
import assert from 'node:assert/strict';
import { closeSync, existsSync, mkdtempSync, openSync, readdirSync, readSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { homedir, tmpdir } from 'node:os';
import { join } from 'node:path';
import { readIndex, refpack } from './extract-assemblies.ts';
import { writePackage } from './dbpf.ts';

const GEOM = 0x015a1849, VPXY = 0x736884f1;
const hex = (v: bigint) => v.toString(16).padStart(16, '0');

type Entry = ReturnType<typeof readIndex>[number];
export type Geom = { shader: number; verts: number; bones: number; top: number; weighted: number; matAt: number; chunk: number };

// the index alone: header + index bytes, index position rewritten so readIndex can walk it
function entries(fd: number): Entry[] {
  const hdr = Buffer.alloc(96);
  readSync(fd, hdr, 0, 96, 0);
  if (hdr.toString('latin1', 0, 4) !== 'DBPF') throw new Error('not DBPF');
  const idx = Buffer.alloc(hdr.readUInt32LE(44));
  readSync(fd, idx, 0, idx.length, hdr.readUInt32LE(64));
  hdr.writeUInt32LE(96, 64);
  return readIndex(Buffer.concat([hdr, idx]));
}
function body(fd: number, e: Entry): Buffer {
  const b = Buffer.alloc(e.fileSize);
  readSync(fd, b, 0, e.fileSize, e.offset);
  return e.compressed === 0xffff ? refpack(b) : b;
}
// RCOL wrapper: version, public chunks, unused, external count, internal count, TGIs, then {position, length} per chunk
const chunk0 = (b: Buffer) => { const at = 20 + 16 * (b.readUInt32LE(12) + b.readUInt32LE(16)); return [b.readUInt32LE(at), b.readUInt32LE(at + 4), at] as const; };

// GEOM chunk: version, TGI offset (from after the field), embedded id = shader, [MTNF size + MTNF when shader != 0],
// merge group, sort order, vertex count, formats {usage, type, size byte}, vertices, sub-meshes (1), faces, [skin
// controller index, version 5], bone count + hashes ending at the TGI block. null = not that layout.
export function parseGeom(b: Buffer): Geom | null {
  const [pos] = chunk0(b);
  if (b.toString('latin1', pos, pos + 4) !== 'GEOM') return null;
  const shader = b.readUInt32LE(pos + 16), end = pos + 12 + b.readUInt32LE(pos + 8);
  let o = pos + 20;
  if (shader) o += 4 + b.readUInt32LE(o);
  o += 8;
  const verts = b.readUInt32LE(o), nf = b.readUInt32LE(o + 4);
  o += 8;
  let stride = 0, bo = -1, wo = -1, ws = 0;
  for (let i = 0; i < nf; i++, o += 9) {
    const usage = b.readUInt32LE(o), size = b[o + 8];
    if (usage === 4) bo = stride;  // bone assignment: 4 byte indices
    if (usage === 5) { wo = stride; ws = size; }  // weights: 4 floats or 4 bytes
    stride += size;
  }
  const vs = o;
  o += verts * stride;
  if (b.readUInt32LE(o) !== 1) return null;
  o += 4;
  o += 5 + b.readUInt32LE(o + 1) * b[o];
  let bones = -1;
  for (const skin of [4, 0]) if (bones < 0 && o + skin + 4 + 4 * b.readUInt32LE(o + skin) === end) bones = b.readUInt32LE(o + skin);
  if (bones < 0) return null;
  let top = -1, weighted = 0;
  for (let v = 0; bo >= 0 && v < verts; v++) {
    const p = vs + v * stride;
    let bad = false;
    for (let k = 0; k < 4; k++) {
      const i = b[p + bo + k];
      top = Math.max(top, i);
      if (i >= bones && (wo < 0 || (ws === 16 ? b.readFloatLE(p + wo + 4 * k) : b[p + wo + k]) > 0)) bad = true;
    }
    if (bad) weighted++;
  }
  return { shader, verts, bones, top, weighted, matAt: pos + 20, chunk: pos };
}

// VPXY chunk: version, TGI offset (from after the field), TGI size, entry count byte, entries (type 0: LOD id, count,
// int32 TGI indices; type 1: one int32), ..., TGI block {count, type, group, instance}. -> GEOMs named by a LOD entry.
export function vpxyLods(b: Buffer): { lod: number; inst: bigint }[] | null {
  const [pos, len] = chunk0(b);
  if (b.toString('latin1', pos, pos + 4) !== 'VPXY') return null;
  if (b.readUInt32LE(pos + 12) === 0) return [];  // TGI size 0: an empty proxy (no entries, no TGI block)
  const tgi = pos + 12 + b.readUInt32LE(pos + 8), n = b.readUInt32LE(tgi);
  if (tgi + 4 + 16 * n !== pos + len) return null;
  const out: { lod: number; inst: bigint }[] = [];
  let p = pos + 16;
  for (let i = 0, count = b[p++]; i < count; i++) {
    if (b[p++] !== 0) { p += 4; continue; }
    const lod = b[p], c = b[p + 1];
    p += 2;
    for (let j = 0; j < c; j++, p += 4) {
      const t = b.readInt32LE(p);
      if (t >= 0 && t < n && b.readUInt32LE(tgi + 4 + 16 * t) === GEOM) out.push({ lod, inst: b.readBigUInt64LE(tgi + 12 + 16 * t) });
    }
  }
  return out;
}

export type Hit = { shape: 'no-material' | 'bone-past'; inst: bigint; group: number; g: Geom; lods: string[] };
// shortcut: a VPXY in another package than its GEOM is not seen; CC ships them together, merged packages keep both
export function scanPackage(path: string): { geoms: number; errors: number; hits: Hit[] } {
  const fd = openSync(path, 'r');
  try {
    const idx = entries(fd), vpxys: [bigint, { lod: number; inst: bigint }[]][] = [], geoms: [Entry, Geom][] = [];
    let errors = 0;
    for (const e of idx) {
      if (e.type !== GEOM && e.type !== VPXY) continue;
      try {
        const b = body(fd, e);
        if (e.type === VPXY) { vpxys.push([e.inst, vpxyLods(b) ?? []]); continue; }
        const g = parseGeom(b);
        if (g) geoms.push([e, g]); else errors++;
      } catch { errors++; }
    }
    // a part's mesh VPXY names meshes with a material; morph VPXYs (fat/fit/thin/special) name only shader-0 meshes
    const shader = new Map(geoms.map(([e, g]) => [e.inst, g.shader])), lods = new Map<bigint, string[]>(), inMesh = new Set<bigint>();
    for (const [v, ls] of vpxys) {
      const mesh = ls.some(l => shader.get(l.inst));
      for (const l of ls) {
        lods.set(l.inst, [...(lods.get(l.inst) ?? []), `LOD ${l.lod} of ${mesh ? '' : 'morph '}VPXY ${hex(v)}`]);
        if (mesh) inMesh.add(l.inst);
      }
    }
    const hits: Hit[] = [];
    for (const [e, g] of geoms) {
      const l = lods.get(e.inst) ?? [];
      if (g.shader === 0 && g.verts > 0 && inMesh.has(e.inst)) hits.push({ shape: 'no-material', inst: e.inst, group: e.group, g, lods: l });
      if (g.top >= g.bones) hits.push({ shape: 'bone-past', inst: e.inst, group: e.group, g, lods: l });
    }
    return { geoms: geoms.length, errors, hits };
  } finally { closeSync(fd); }
}

// the GEOM without its material: shader 0, MTNF size field and block cut, TGI offset and chunk length shortened
export function stripMaterial(b: Buffer): Buffer {
  const g = parseGeom(b);
  if (!g || !g.shader) throw new Error(g ? 'already shader 0' : 'not a GEOM');
  const [pos, len, at] = chunk0(b);
  if (pos + len !== b.length) throw new Error('GEOM chunk is not last');
  const cut = 4 + b.readUInt32LE(g.matAt);
  const out = Buffer.concat([b.subarray(0, g.matAt), b.subarray(g.matAt + cut)]);
  out.writeUInt32LE(0, pos + 16);
  out.writeUInt32LE(out.readUInt32LE(pos + 8) - cut, pos + 8);
  out.writeUInt32LE(len - cut, at + 4);
  return out;
}

function packages(root: string): string[] {
  if (statSync(root).isFile()) return [root];
  return readdirSync(root, { withFileTypes: true, recursive: true })
    .filter(d => d.isFile() && d.name.toLowerCase().endsWith('.package')).map(d => join(d.parentPath, d.name));
}

// ---- self-check: synthetic GEOMs and a VPXY in a written package ----
function fakeGeom(o: { shader: number; bones: number; idx: number[][]; w: number[][] }): Buffer {
  const u32 = (...v: number[]) => { const b = Buffer.alloc(4 * v.length); v.forEach((x, i) => b.writeUInt32LE(x, 4 * i)); return b; };
  const fmt = (usage: number, size: number) => { const b = Buffer.alloc(9); b.writeUInt32LE(usage, 0); b[8] = size; return b; };
  const verts = o.idx.map((ix, v) => Buffer.from([...ix, ...o.w[v]]));
  const mat = o.shader ? Buffer.concat([u32(12), Buffer.from('MTNFmaterial')]) : Buffer.alloc(0);
  const tail = Buffer.concat([u32(1), Buffer.from([2]), u32(0), u32(0), u32(o.bones, ...Array.from({ length: o.bones }, (_, i) => 0x1000 + i))]);
  const pre = Buffer.concat([u32(o.shader), mat, u32(0, 0, verts.length, 2), fmt(4, 4), fmt(5, 4), ...verts, tail]);
  const chunk = Buffer.concat([Buffer.from('GEOM'), u32(5, 4 + pre.length, 4), pre, u32(0)]);  // version, TGI offset, TGI size
  return Buffer.concat([u32(3, 1, 0, 0, 1), Buffer.alloc(16), u32(44, chunk.length), chunk]);
}
function fakeVpxy(lods: [number, bigint][]): Buffer {
  const ents = Buffer.concat([Buffer.from([1 + lods.length, 1, 0, 0, 0, 0]), ...lods.map(([lod], i) => { const b = Buffer.alloc(7); b[0] = 0; b[1] = lod; b[2] = 1; b.writeInt32LE(i + 1, 3); return b; })]);
  const tgis = Buffer.alloc(4 + 16 * (1 + lods.length));
  tgis.writeUInt32LE(1 + lods.length, 0);
  tgis.writeUInt32LE(0x00ae6c67, 4);
  lods.forEach(([, inst], i) => { tgis.writeUInt32LE(GEOM, 20 + 16 * i); tgis.writeBigUInt64LE(inst, 28 + 16 * i); });
  const head = Buffer.alloc(16);
  head.write('VPXY', 0, 'latin1');
  head.writeUInt32LE(4, 4);
  head.writeUInt32LE(4 + ents.length, 8);
  head.writeUInt32LE(tgis.length, 12);
  const chunk = Buffer.concat([head, ents, tgis]);
  const rc = Buffer.alloc(44);
  [3, 1, 0, 0, 1].forEach((v, i) => rc.writeUInt32LE(v, 4 * i));
  rc.writeUInt32LE(44, 36);
  rc.writeUInt32LE(chunk.length, 40);
  return Buffer.concat([rc, chunk]);
}
function selfCheck() {
  const ok = fakeGeom({ shader: 0x84fd7152, bones: 2, idx: [[0, 1, 0, 0], [1, 0, 0, 0]], w: [[200, 55, 0, 0], [255, 0, 0, 0]] });
  const g = parseGeom(ok)!;
  assert.deepEqual([g.shader, g.verts, g.bones, g.top, g.weighted], [0x84fd7152, 2, 2, 1, 0]);
  const stripped = stripMaterial(ok), s = parseGeom(stripped)!;
  assert.deepEqual([s.shader, s.verts, s.bones, s.top, s.weighted], [0, 2, 2, 1, 0]);
  assert.equal(stripped.length, ok.length - 16);
  const filler = fakeGeom({ shader: 1, bones: 5, idx: [[0, 115, 0, 0]], w: [[255, 0, 0, 0]] });
  const weighted = fakeGeom({ shader: 1, bones: 5, idx: [[9, 0, 0, 0], [0, 1, 0, 0]], w: [[100, 155, 0, 0], [255, 0, 0, 0]] });
  const res = [
    { type: GEOM, group: 7, instance: 0xa1n, data: ok },
    { type: GEOM, group: 7, instance: 0xa2n, data: stripped },  // shader 0, named by LOD 2: no-material
    { type: GEOM, group: 7, instance: 0xa3n, data: stripped },  // shader 0, only in a morph VPXY: not listed
    { type: GEOM, group: 7, instance: 0xa4n, data: filler },
    { type: GEOM, group: 7, instance: 0xa5n, data: weighted },
    { type: VPXY, group: 7, instance: 0xb1n, data: fakeVpxy([[0, 0xa1n], [2, 0xa2n], [3, 0xa4n]]) },
    { type: VPXY, group: 7, instance: 0xb2n, data: fakeVpxy([[1, 0xa3n]]) },
  ];
  assert.deepEqual(vpxyLods(res[5].data), [{ lod: 0, inst: 0xa1n }, { lod: 2, inst: 0xa2n }, { lod: 3, inst: 0xa4n }]);
  const dir = mkdtempSync(join(tmpdir(), 'ccmesh-'));
  try {
    const p = join(dir, 'x.package');
    writeFileSync(p, writePackage(res));
    const r = scanPackage(p);
    assert.equal(r.geoms, 5);
    assert.equal(r.errors, 0);
    assert.deepEqual(r.hits.map(h => [h.shape, h.inst, h.g.weighted, h.lods.length]),
      [['no-material', 0xa2n, 0, 1], ['bone-past', 0xa4n, 0, 1], ['bone-past', 0xa5n, 1, 0]]);
    assert.equal(r.hits[0].lods[0], `LOD 2 of VPXY ${hex(0xb1n)}`);
  } finally { rmSync(dir, { recursive: true, force: true }); }
  console.log('cc-mesh-scan self-check ok');
}

function main(argv: string[]) {
  if (argv[0] === '--self-check') selfCheck();
  else if (argv[0] === '--repro') {
    const [src, instHex, out] = argv.slice(1);
    if (!src || !instHex || !out) throw new Error('usage: --repro <package> <GEOM instance hex> <out.package>');
    if (existsSync(out)) throw new Error(`${out} exists`);
    const inst = BigInt(`0x${instHex}`), fd = openSync(src, 'r');
    try {
      const e = entries(fd).find(x => x.type === GEOM && x.inst === inst);
      if (!e) throw new Error(`no GEOM ${hex(inst)} in ${src}`);
      const b = body(fd, e), g = parseGeom(b), data = stripMaterial(b), s = parseGeom(data)!;
      assert.deepEqual([s.verts, s.bones, s.top, s.weighted], [g!.verts, g!.bones, g!.top, g!.weighted]);
      writeFileSync(out, writePackage([{ type: GEOM, group: e.group, instance: inst, data }]));
      console.log(`wrote ${out}: GEOM ${hex(inst)} group ${e.group.toString(16).padStart(8, '0')}, shader ${g!.shader.toString(16)} -> 0, ` +
        `${b.length} -> ${data.length} bytes. TEST ONLY: one session in Mods\\Overrides\\<dir>\\, then delete it`);
    } finally { closeSync(fd); }
  } else {
    const roots = argv.length ? argv : [join(process.env.TS3_USER ?? join(homedir(), 'Documents', 'Electronic Arts', 'The Sims 3'), 'Mods')];
    let n = 0, geoms = 0, errors = 0, noMat = 0, past = 0, pastW = 0;
    for (const root of roots) for (const p of packages(root)) {
      n++;
      let r;
      try { r = scanPackage(p); } catch (e) { console.log(`ERR ${p}: ${(e as Error).message}`); continue; }
      geoms += r.geoms; errors += r.errors;
      if (r.hits.length) console.log(`== ${p}`);
      for (const h of r.hits) {
        const where = h.lods.length ? h.lods.slice(0, 3).join(', ') : 'no VPXY LOD in this package';
        if (h.shape === 'no-material') noMat++; else { past++; if (h.g.weighted) pastW++; }
        console.log(h.shape === 'no-material'
          ? `   no-material  GEOM ${hex(h.inst)} group ${h.group.toString(16).padStart(8, '0')}  ${h.g.verts} vertices, shader 0  (${where})`
          : `   bone-past    GEOM ${hex(h.inst)} group ${h.group.toString(16).padStart(8, '0')}  bones ${h.g.bones}, max index ${h.g.top}, weighted ${h.g.weighted} of ${h.g.verts} vertices  (${where})`);
      }
    }
    console.log(`packages ${n}, GEOMs ${geoms} (unreadable GEOM or VPXY ${errors}): no-material ${noMat}, bone-past ${past} (weighted ${pastW})`);
  }
}
if (import.meta.main ?? process.argv[1]?.endsWith('cc-mesh-scan.ts')) main(process.argv.slice(2));
