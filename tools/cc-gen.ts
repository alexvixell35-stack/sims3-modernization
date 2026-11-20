// Synthetic custom content for memory stress tests (docs/191, docs/200): clones base-game Buy/Build objects (OBJD), CAS parts
// (CASP) and Build-mode catalogue items (wall/floor patterns CWAL, doors/windows = OBJD, fences, terrain paints, railings, stairs)
// as NEW items, each with its own copy of every mesh, texture, footprint, slot, light and thumbnail it uses, so the game
// cannot share one load between clones. Output holds EA-derived bytes: written only under game-copy/cc-synth/ (git-ignored),
// never committed. One .package per item (as downloaded CC), plus manifest.json.
//
//   node tools/cc-gen.ts --count 1000 --kind both --seed 1 [--out game-copy/cc-synth/1k] [--source objd:350,casp:7a27...]
//   node tools/cc-gen.ts --count 200 --kind wall:5,floor:5,door:3,window:3,fence,terrain,railing,stairs --out game-copy/cc-synth/cc-build-200
//   node tools/cc-gen.ts --count 200 --first 2000 --append --kind inv --out game-copy/cc-synth/cc-2k-v2   (Sim-inventory objects added to a set)
//   node tools/cc-gen.ts --list-sources [--kind objects|cas|build|inv]   default source picks with closure and bytes per item
//   node tools/cc-gen.ts --count 300 --kind makeup --out game-copy/cc-synth/cc-makeup-300      (CAS makeup + costume makeup, docs/200 §9)
//   node tools/cc-gen.ts --count 200 --kind presets --presets 6 --out game-copy/cc-synth/cc-presets-200x6   (objects with >= 6 design presets, own textures per preset)
// --kind tiny --count 2000 --out game-copy/cc-synth/pkgs-2000: docs/17 M4 tiny packages (one 16-byte resource each, no EA bytes).
// --kind: objects | cas | both (objects+cas) | build (wall floor door window fence terrain railing stairs) | all (both + build) |
// a comma list of kinds, each with an optional :weight (items are dealt round-robin over the expanded list). inv, makeup, presets: by name only.
//   node tools/cc-gen.ts --self-check                          synthetic fixture (no EA bytes): closure, rewrite, read-back
// Game folder: --game or TS3_GAME (default Steam path). Sources: base game Full/DeltaBuild0-2 + Thumbnails (Delta wins).
//
// How references are found [verified on base-game OBJD/OBJK/VPXY/CASP 2026-10-26]: TS3 resources store keys as 16-byte
// (type, group, instance) in TGI, ITG, TIG or IGT order (OBJD TGI block = TGI, CASP = ITG, material lists IGT/ITG). Every container body is scanned at
// every byte for a 16-byte key that exists in the game index with a followed type; each hit is a reference. A clone gets
// new instances (FNV-64 of seed/kind/item/old instance; the same old instance maps to the same new one inside an item, so
// OBJD/OBJK and thumbnail families stay paired) and every hit whose target is cloned is rewritten. Shared types (tuning XML,
// icons, BOND, BGEO, clips) keep pointing at the EA original. Leaf resources (textures, thumbnails) are copied with the
// game's own RefPack bytes, so no compressor is needed.
// Catalogue name/description: the OBJD holds two English STBL keys back to back [verified: 5 OBJDs]; they get new keys and the
// item package an English STBL (locale byte 0x00) "CCGen <name> #i". CASPs get no string (CAS shows no part name).
// Build kinds [verified on base-game data 2026-10-26; type names community: simswiki Sims_3:PackedFileTypes]: CWAL 0x515CA4CD
// "Catalog Wall/Floor Pattern" (241 in the base game, 230 with the OBJD-style name + description key pair; wall or floor told
// apart by the description text, "wall covering" / "floor tiles"; references IMG textures (cloned) and complate XML 0x0333406C
// (shared); thumbnails 0x0589DC44-46 at the same instance, groups 0/4-9). Doors and windows are OBJDs whose internal model name
// starts with door / window (doorSingleCountry, windowIntl). CFEN 0x0418FE2A fence, CTPT 0x04ED4BB2 terrain paint, CRAL
// 0x04C58103 railing, CSTR 0x049CA4CD stairs. Whether the game lists a cloned CWAL in the Build catalogue and draws its own
// cloned IMGs (the complate XML also names textures by text) is [inferred]: the build-cc-cycle load smoke checks the listing.
// Makeup (docs/200 §9) [verified on base-game data 2026-10-26]: CASPs whose body type (u32 after the name, a float and a byte) is
// Lipstick 17, EyeShadow 18, EyeLiner 19, Blush 20 or CostumeMakeup 21 (face paint; SimIFace BodyTypes); 208 base-game CASPs
// carry makeup-like names, ~2.4 KB each, preset XML = complate CasOverlayMultiTintable naming Face Overlay / Face Specular / Mask
// IMGs as text keys ("key:00B2D882:00000000:<inst>", UTF-16LE), also listed in the binary ITG list. Every CASP clone now rewrites
// those text keys too (before 2026-10-26 a cas clone's XML still named the EA textures). Tattoos are left out: the game wears
// them through TattooTemplate layers (CASUtils DesignPresetMergeTattooTemplates), not as a plain part.
// Object design presets (docs/200 §9) [verified: all 1,614 base-game group-0 OBJDs parse; format: s3pi CatalogResource.Material,
// docs/24]: the OBJD material list after its u32 version / TGI offset / TGI size is the preset list (n materials -> n + 1
// thumbnail groups on most); each material holds its own complate block and its own TGI list, which the block's TGI-index
// values point into. kind presets clones every IMG a material's list names once per material (fnv64 of seed/kind/item/p<m>/old),
// so each preset loads its own textures; the game listing every material as a preset is [inferred] (preset_cc_check reads
// BuildBuyProduct.PresetAtIndex live).
import assert from 'node:assert/strict';
import { closeSync, existsSync, mkdirSync, mkdtempSync, openSync, readdirSync, readSync, rmSync, writeFileSync, readFileSync } from 'node:fs';
import { join, resolve, sep } from 'node:path';
import { tmpdir } from 'node:os';
import { parseArgs } from 'node:util';
import { readIndex, refpack } from './extract-assemblies.ts';
import { fnv64, writePackage, type Resource } from './dbpf.ts';

export const T = {
  OBJD: 0x319e4f1d, OBJK: 0x02dc343f, VPXY: 0x736884f1, MODL: 0x01661233, MLOD: 0x01d10f34, GEOM: 0x015a1849, IMG: 0x00b2d882,
  LITE: 0x03b4c61d, FTPT: 0xd382bf57, RSLT: 0xd3044521, RIG: 0x8eaf13de, TXTC: 0x033a1435, CASP: 0x034aeecb, STBL: 0x220557da,
  CWAL: 0x515ca4cd, CFEN: 0x0418fe2a, CTPT: 0x04ed4bb2, CRAL: 0x04c58103, CSTR: 0x049ca4cd,
};
// inv: objects a Sim inventory takes (manifest inventoriable: true), see inventoriableClasses
export type Kind = 'object' | 'inv' | 'cas' | 'wall' | 'floor' | 'door' | 'window' | 'fence' | 'terrain' | 'railing' | 'stairs' | 'makeup' | 'presets';
export const BUILD_KINDS: Kind[] = ['wall', 'floor', 'door', 'window', 'fence', 'terrain', 'railing', 'stairs'];
const ROOT_TYPE: Record<Kind, number> = { object: T.OBJD, inv: T.OBJD, door: T.OBJD, window: T.OBJD, cas: T.CASP, wall: T.CWAL, floor: T.CWAL,
  fence: T.CFEN, terrain: T.CTPT, railing: T.CRAL, stairs: T.CSTR, makeup: T.CASP, presets: T.OBJD };
// makeup body types (SimIFace BodyTypes) -> the name wear_parts body_types takes
export const MAKEUP: Record<number, string> = { 17: 'Lipstick', 18: 'EyeShadow', 19: 'EyeLiner', 20: 'Blush', 21: 'CostumeMakeup' };
let PRESETS = 6; // kind presets: sources need at least this many design presets (--presets)
export function setMinPresets(n: number) { PRESETS = n; }

// Inventoriable objects [verified static, decompiled Sims3GameplaySystems Inventory.cs]: Inventory.TryToAdd -> CanAdd ->
// ValidForThisInventory = the object's ItemComponent allows it (CanAddToInventory) and its ValidInventories hold the owner's
// type; a Sim inventory needs ItemComponent.SimInventoryItem or SimAndMailboxInventoryItem, added in the class's OnStartup.
// So: script classes whose source adds one of those, plus every class derived from them (short names; a class that
// overrides CanAddToInventory to refuse is not seen [inferred rare]: buy_object's TryToAdd stays the live check). The OBJD's
// class = the OBJK scriptClass string at the same instance [verified: 1,351 base-game OBJKs name one].
export function inventoriableClasses(sources: string[]): Set<string> {
  const parent = new Map<string, string>(), seeds = new Set<string>(), helpers = new Set<string>();
  for (const s of sources) {
    for (const m of s.matchAll(/\bclass (\w+)(?:<[^>]*>)?\s*:\s*([\w.]+)/g)) parent.set(m[1], m[2].split('.').pop()!);
    for (const a of s.matchAll(/AddComponent<ItemComponent>\(new object\[1\]\s*\{\s*ItemComponent\.Sim(AndMailbox)?InventoryItem\b/g)) {
      // the method around the add: OnStartup/OnCreation/AddComponents (or none found) seed the file's classes; a helper (Computer's
      // AddComputerInventoryInteractions, only laptops call it) seeds only the classes that call it
      const meth = [...s.slice(0, a.index).matchAll(/\n\s*(?:(?:public|protected|private|internal|static|override|virtual|new)\s+)+[\w.<>\[\]]+\s+(\w+)\s*\(/g)].pop()?.[1];
      if (!meth || ['OnStartup', 'OnCreation', 'AddComponents'].includes(meth)) for (const m of s.matchAll(/\bclass (\w+)/g)) seeds.add(m[1]); else helpers.add(meth);
    }
  }
  for (const s of sources) if ([...helpers].some(h => new RegExp(`(?<![\\w.])${h}\\(\\)\\s*;`).test(s))) for (const m of s.matchAll(/\bclass (\w+)/g)) seeds.add(m[1]);
  const out = new Set<string>();
  for (const c of parent.keys()) for (let x: string | undefined = c, n = 0; x && n < 40; x = parent.get(x), n++) if (seeds.has(x)) { out.add(c); break; }
  for (const c of seeds) out.add(c);
  return out;
}
let INV: Set<string> | undefined; // set by --decompiled (CLI) or the self-check
export function setInventoriable(s: Set<string>) { INV = s; }
export function scriptClass(ix: Index, objd: Entry): string | undefined {
  const k = (ix.byInst.get(objd.inst) ?? []).find(x => x.type === T.OBJK);
  return k && /scriptClass[^S]{0,8}(Sims3\.[\w.]+)/.exec(ix.body(k).toString('latin1'))?.[1];
}
const isInv = (ix: Index, e: Entry) => {
  if (!INV) throw new Error('kind inv needs the decompiled script DLLs (--decompiled game-copy/decompiled)');
  const c = e.type === T.OBJD ? scriptClass(ix, e) : undefined;
  return !!c && !/\.Vehicles\./.test(c) && INV.has(c.split('.').pop()!); // cars fit an inventory too, but the pool wants small items
};
const BUILD_ROOTS = [T.CWAL, T.CFEN, T.CTPT, T.CRAL, T.CSTR];
// containers: scanned for references; leaves: copied raw. Both are cloned. Anything else referenced stays shared.
const CONTAINERS = new Set([T.OBJD, T.OBJK, T.VPXY, T.MODL, T.MLOD, T.GEOM, T.LITE, T.FTPT, T.RSLT, T.TXTC, T.CASP, ...BUILD_ROOTS]);
const LEAVES = new Set([T.IMG, T.RIG]);
// thumbnails share the root's instance (groups 0/2/3/4 = sizes or presets) [verified: OBJD 0x350, CASP 704c11c8ceaea1ee]
// build roots: CWAL 0x0589DC44-46, CFEN 0x2653E3C8-CA, CTPT 0x05B1B524-26, CRAL 0x2D4284F0-F2, CSTR 0x5DE9DBA0-A2 [verified: instance match, whole family]
const THUMBS = new Set([0x0580a2b4, 0x0580a2b5, 0x0580a2b6, 0x626f60cc, 0x626f60cd, 0x626f60ce, 0x0589dc44, 0x0589dc45, 0x0589dc46,
  0x2653e3c8, 0x2653e3c9, 0x2653e3ca, 0x05b1b524, 0x05b1b525, 0x05b1b526, 0x2d4284f0, 0x2d4284f1, 0x2d4284f2, 0x5de9dba0, 0x5de9dba1, 0x5de9dba2]);
const CLONED = new Set([...CONTAINERS, ...LEAVES, ...THUMBS]);
// referenced but left shared, counted per item: tuning/material XML, icon PNG, BOND, BGEO, clip [types seen in OBJD/CASP lists]
const SHARED = new Set([0x0333406c, 0x2f7d0004, 0x0355e0a6, 0x062c8204, 0x6b20c4f3]);
const SCANNED = new Set([...CLONED, ...SHARED]);
// other items an OBJD/CASP names (parent objects, linked parts) stay EA's: following them would walk the whole catalogue
const ROOTS = new Set([T.OBJD, T.CASP, ...BUILD_ROOTS]); // stairs name their railing / fence / pattern: those stay EA's
const follow = (t: number) => CLONED.has(t) && !ROOTS.has(t);

type Entry = { type: number; group: number; inst: bigint; offset: number; fileSize: number; memSize: number; compressed: number; fd: number; file: string };
export const keyOf = (t: number, g: number, i: bigint) => `${t.toString(16)}:${g.toString(16)}:${i.toString(16)}`;
const hex = (n: number | bigint) => n.toString(16);

export class Index {
  byKey = new Map<string, Entry>();
  byInst = new Map<bigint, Entry[]>();
  types = new Set<number>();
  fds: number[] = [];
  add(file: string) { // header + index only; packages are GBs
    const fd = openSync(file, 'r'); this.fds.push(fd);
    const read = (pos: number, len: number) => { const b = Buffer.alloc(len); readSync(fd, b, 0, len, pos); return b; };
    const head = read(0, 96);
    if (head.toString('latin1', 0, 4) !== 'DBPF') throw new Error(`not DBPF: ${file}`);
    const syn = Buffer.concat([head, read(head.readUInt32LE(64), head.readUInt32LE(44))]); syn.writeUInt32LE(96, 64);
    for (const e of readIndex(syn)) {
      const x = { ...e, fd, file }, k = keyOf(e.type, e.group, e.inst);
      const old = this.byKey.get(k);
      this.byKey.set(k, x); // later package wins (Delta over Full)
      this.types.add(e.type);
      const l = this.byInst.get(e.inst) ?? this.byInst.set(e.inst, []).get(e.inst)!;
      if (old) l.splice(l.indexOf(old), 1);
      l.push(x);
    }
    return this;
  }
  raw(e: Entry) { const b = Buffer.alloc(e.fileSize); readSync(e.fd, b, 0, e.fileSize, e.offset); return b; }
  body(e: Entry) { const b = this.raw(e); return e.compressed === 0xffff ? refpack(b) : b; }
  close() { for (const fd of this.fds) closeSync(fd); this.fds = []; }
}

// every 16-byte key in b (TGI, ITG, TIG, IGT order; CASP texture lists use IGT [verified]) that the lookup knows with a type in `types`; at = offset of the 8 instance bytes
export function scanRefs(b: Buffer, types: Set<number>, has: (k: string) => boolean) {
  const out: { at: number; key: string; type: number; inst: bigint }[] = [];
  for (let i = 0; i + 16 <= b.length; i++) {
    const tries: [number, number, number][] = [[b.readUInt32LE(i), b.readUInt32LE(i + 4), i + 8], [b.readUInt32LE(i + 8), b.readUInt32LE(i + 12), i], [b.readUInt32LE(i), b.readUInt32LE(i + 12), i + 4], [b.readUInt32LE(i + 12), b.readUInt32LE(i + 8), i]];
    for (const [t, g, at] of tries) {
      if (!types.has(t)) continue;
      const inst = b.readBigUInt64LE(at), k = keyOf(t, g, inst);
      if (has(k)) out.push({ at, key: k, type: t, inst });
    }
  }
  return out;
}

// English STBL key -> string (locale byte 0x00); TS3 STBL: 'STBL', u8 version 2, u16, u32 count, 6 bytes, then u64 key, u32 chars, UTF-16LE
export function readStbl(b: Buffer): Map<bigint, string> {
  if (b.toString('latin1', 0, 4) !== 'STBL') throw new Error('not STBL');
  const m = new Map<bigint, string>(); const n = b.readUInt32LE(7); let p = 17;
  for (let i = 0; i < n; i++) { const c = b.readUInt32LE(p + 8); m.set(b.readBigUInt64LE(p), b.toString('utf16le', p + 12, p + 12 + c * 2)); p += 12 + c * 2; }
  if (p !== b.length) throw new Error(`STBL size ${p} != ${b.length}`);
  return m;
}
export function writeStbl(m: Map<bigint, string>): Buffer {
  const head = Buffer.alloc(17); head.write('STBL', 0, 'latin1'); head[4] = 2; head.writeUInt32LE(m.size, 7);
  return Buffer.concat([head, ...[...m].map(([k, s]) => { const e = Buffer.alloc(12); e.writeBigUInt64LE(k); e.writeUInt32LE(s.length, 8); return Buffer.concat([e, Buffer.from(s, 'utf16le')]); })]);
}

// CASP part name (7-bit length byte, UTF-16BE) behind the presets (u32 chars + UTF-16LE xml + u32 index each) [verified: 3 CASPs]
const caspNameAt = (b: Buffer) => { let p = 12; for (let i = 0, n = b.readUInt32LE(8); i < n; i++) p += 4 + b.readUInt32LE(p) * 2 + 4; return p; };
export function caspName(b: Buffer): string {
  const p = caspNameAt(b), len = b[p];
  return Buffer.from(b.subarray(p + 1, p + 1 + len)).swap16().toString('utf16le');
}
// body type: u32 after the name, a float sort priority and a byte [verified: 208 base-game makeup CASPs, Lipstick = 17 .. CostumeMakeup = 21]
export function caspBodyType(b: Buffer): number { const p = caspNameAt(b); return b.readUInt32LE(p + 1 + b[p] + 5); }
// text keys in the preset XMLs ("key:TTTTTTTT:GGGGGGGG:IIIIIIIIIIIIIIII", UTF-16LE); at = byte offset of the 16 instance digits
export function caspTextKeys(b: Buffer): { at: number; key: string; type: number; inst: bigint }[] {
  const out: { at: number; key: string; type: number; inst: bigint }[] = [];
  let p = 12;
  for (let k = 0, n = b.readUInt32LE(8); k < n; k++) {
    const len = b.readUInt32LE(p), s = p + 4;
    for (const m of b.toString('utf16le', s, s + len * 2).matchAll(/key:([0-9a-f]{8}):([0-9a-f]{8}):([0-9a-f]{16})/gi)) {
      const type = parseInt(m[1], 16), inst = BigInt(`0x${m[3]}`);
      out.push({ at: s + (m.index! + 22) * 2, key: keyOf(type, parseInt(m[2], 16), inst), type, inst });
    }
    p = s + len * 2 + 4;
  }
  return out;
}
// OBJD design presets = its material list (format in the header); per material: start, end of its TGI list, start of that list
export function objdMaterials(b: Buffer): { start: number; end: number; tgi: number }[] {
  const out: { start: number; end: number; tgi: number }[] = [];
  let p = 16;
  for (let i = 0, n = b.readInt32LE(12); i < n; i++) {
    const start = p;
    p += b[p] === 1 ? 1 : 5;
    const oset = b.readUInt32LE(p) + p + 4; p += 6;
    const tgi = b.readUInt32LE(p) + p + 4, count = b.readInt32LE(tgi);
    if (tgi + 4 + 16 * count !== oset) throw new Error(`OBJD material ${i}: TGI list end ${tgi + 4 + 16 * count} != ${oset}`);
    p = oset + 4;
    out.push({ start, end: oset, tgi });
  }
  return out;
}

type Node = { e: Entry; body?: Buffer; refs: ReturnType<typeof scanRefs> };
export function closure(ix: Index, root: Entry): Map<string, Node> {
  const out = new Map<string, Node>(), todo = [root];
  for (const t of ix.byInst.get(root.inst) ?? []) if (THUMBS.has(t.type)) todo.push(t);
  while (todo.length) {
    const e = todo.pop()!, k = keyOf(e.type, e.group, e.inst);
    if (out.has(k)) continue;
    if (!CONTAINERS.has(e.type)) { out.set(k, { e, refs: [] }); continue; }
    const body = ix.body(e), refs = scanRefs(body, SCANNED, x => ix.byKey.has(x));
    out.set(k, { e, body, refs });
    for (const r of refs) if (follow(r.type)) todo.push(ix.byKey.get(r.key)!);
  }
  return out;
}

export type Item = { i: number; kind: Kind; inventoriable?: true; makeup?: string; presets?: number; source: string; name: string; package: string; resources: number; diskBytes: number; memBytes: number; sharedRefs: number; root: string };
// catalogue name + description: first two adjacent English STBL keys (OBJD and every build root) [verified: 5 OBJDs, 230 CWALs]
function nameAt(b: Buffer, names: Map<bigint, string>) {
  for (let p = 0; p + 16 <= b.length; p++) if (names.has(b.readBigUInt64LE(p)) && names.has(b.readBigUInt64LE(p + 8)) && b.readBigUInt64LE(p) > 0xffffffffn) return p;
  return -1;
}
// kind of a root: OBJD door/window by the internal model name, CWAL wall/floor by the description text
export function kindOf(type: number, b: Buffer, names: Map<bigint, string>): Kind | undefined {
  if (type === T.OBJD) return (/[^A-Za-z0-9_](door|window)[A-Z][A-Za-z0-9_]*/.exec(b.toString('latin1'))?.[1] as Kind | undefined) ?? 'object';
  if (type === T.CWAL) { const at = nameAt(b, names), d = at < 0 ? '' : names.get(b.readBigUInt64LE(at + 8))!; return /wall/i.test(d) ? 'wall' : /floor/i.test(d) ? 'floor' : undefined; }
  return (Object.keys(ROOT_TYPE) as Kind[]).find(k => ROOT_TYPE[k] === type);
}
// one clone: every closure resource under a new instance, references rewritten, own STBL for a named root (all but CASP)
export function cloneItem(ix: Index, root: Entry, i: number, seed: string, names: Map<bigint, string>,
  kind: Kind = root.type === T.OBJD ? 'object' : kindOf(root.type, ix.body(root), names)!): { res: Resource[]; item: Omit<Item, 'package'> } {
  if (!kind) throw new Error(`${keyOf(root.type, root.group, root.inst)}: unknown kind`);
  const nodes = closure(ix, root);
  const newInst = (old: bigint) => fnv64(`cc-gen/${seed}/${kind}/${i}/${hex(old)}`);
  const res: Resource[] = [];
  const leaf = (e: Entry, instance: bigint): Resource => ({ type: e.type, group: e.group, instance, data: ix.raw(e), ...(e.compressed === 0xffff ? { memSize: e.memSize } : {}) });
  const rootBody = nodes.get(keyOf(root.type, root.group, root.inst))!.body!;
  let sharedRefs = 0, name = root.type === T.CASP ? caspName(rootBody) : '';
  const mats = kind === 'presets' ? objdMaterials(rootBody) : [];
  const perPreset = new Map<bigint, Entry>(); // presets: each material's own copy of every texture its TGI list names
  for (const n of nodes.values()) {
    const { e } = n;
    if (!n.body) { res.push(leaf(e, newInst(e.inst))); continue; }
    const b = Buffer.from(n.body);
    for (const r of n.refs) {
      if (!nodes.has(r.key)) { sharedRefs++; continue; }
      const m = e === root && r.type === T.IMG ? mats.findIndex(x => r.at >= x.tgi && r.at < x.end) : -1;
      const ni = m < 0 ? newInst(r.inst) : fnv64(`cc-gen/${seed}/${kind}/${i}/p${m}/${hex(r.inst)}`);
      if (m >= 0) perPreset.set(ni, nodes.get(r.key)!.e);
      b.writeBigUInt64LE(ni, r.at);
    }
    if (e.type === T.CASP) for (const r of caspTextKeys(b)) if (nodes.has(r.key)) b.write(newInst(r.inst).toString(16).toUpperCase().padStart(16, '0'), r.at, 'utf16le');
    if (e === root && root.type !== T.CASP) {
      const at = nameAt(b, names);
      if (at < 0) throw new Error(`${kind} ${hex(e.inst)}: no catalogue name key`);
      name = names.get(b.readBigUInt64LE(at))!;
      const nk = fnv64(`cc-gen/${seed}/${i}/name`), dk = fnv64(`cc-gen/${seed}/${i}/desc`);
      b.writeBigUInt64LE(nk, at); b.writeBigUInt64LE(dk, at + 8);
      res.push({ type: T.STBL, group: 0, instance: fnv64(`cc-gen/${seed}/${i}/stbl`) & 0x00ffffffffffffffn, // high byte 0x00 = English
        data: writeStbl(new Map([[nk, `CCGen ${name} #${i}`], [dk, `Synthetic CC (cc-gen seed ${seed} item ${i}), cloned from ${name}.`]])) });
    }
    res.push({ type: e.type, group: e.group, instance: newInst(e.inst), data: b });
  }
  for (const [ni, e] of perPreset) res.push(leaf(e, ni));
  const rootNew = keyOf(root.type, root.group, newInst(root.inst));
  const extra = kind === 'makeup' ? { makeup: MAKEUP[caspBodyType(rootBody)] } : kind === 'presets' ? { presets: mats.length } : {};
  return { res, item: { i, kind, ...extra, source: keyOf(root.type, root.group, root.inst), name, resources: res.length,
    diskBytes: res.reduce((s, r) => s + r.data.length, 0), memBytes: res.reduce((s, r) => s + (r.memSize ?? r.data.length), 0), sharedRefs, root: rootNew } };
}

// read back one written package: every body readable, sizes as written, cloned references stay inside the package (or the game)
export function verifyPackage(pkg: Buffer, game: Index, oldInsts: Set<bigint>): { keys: string[]; problems: string[] } {
  const ix = readIndex(pkg), own = new Set(ix.map(e => keyOf(e.type, e.group, e.inst))), problems: string[] = [];
  if (own.size !== ix.length) problems.push('duplicate key inside the package');
  for (const e of ix) {
    const raw = pkg.subarray(e.offset, e.offset + e.fileSize);
    const b = e.compressed === 0xffff ? refpack(raw) : raw;
    if (b.length !== e.memSize) problems.push(`${keyOf(e.type, e.group, e.inst)}: size ${b.length} != ${e.memSize}`);
    if (game.byKey.has(keyOf(e.type, e.group, e.inst))) problems.push(`${keyOf(e.type, e.group, e.inst)}: key already in the game`);
    if (e.type === T.STBL) readStbl(b);
    if (!CONTAINERS.has(e.type)) continue;
    const refs = scanRefs(b, SCANNED, k => game.byKey.has(k) || own.has(k)), keyed = new Set(refs.map(r => r.at));
    for (const r of refs) if (!own.has(r.key) && follow(r.type)) problems.push(`${keyOf(e.type, e.group, e.inst)} still points at EA ${r.key}`);
    if (e.type === T.CASP) for (const r of caspTextKeys(b)) if (!own.has(r.key) && game.byKey.has(r.key) && follow(r.type)) problems.push(`${keyOf(e.type, e.group, e.inst)} XML still names EA ${r.key}`);
    // a bare old instance outside any key = a reference form the scan does not know. Keys of other game types may share an
    // instance (VPXY -> 0x0C244B35 next to the RIG, MODL -> a missing MLOD at an FTPT's instance [verified: windows, fences]): type at
    // the TGI / ITG / TIG / IGT spot is one the game has = a key that stays shared, not a miss
    const typeAt = (q: number) => q >= 0 && q + 4 <= b.length && game.types.has(b.readUInt32LE(q));
    for (let p = 0; p + 8 <= b.length; p++) {
      const v = b.readBigUInt64LE(p);
      if (v > 0xffffffffn && oldInsts.has(v) && !keyed.has(p) && ![p - 8, p + 8, p - 4, p + 12].some(typeAt)) { problems.push(`${keyOf(e.type, e.group, e.inst)}: old instance ${hex(v)} left at ${p}`); break; }
    }
  }
  return { keys: [...own], problems };
}

// a source of kind k (root type checked by the caller): inv by script class, makeup by body type, presets = a plain object with >= PRESETS presets
function fitsKind(ix: Index, e: Entry, k: Kind, names: Map<bigint, string>) {
  if (k === 'inv') return isInv(ix, e);
  if (k === 'makeup') return !!MAKEUP[caspBodyType(ix.body(e))] && !/_burnt/i.test(caspName(ix.body(e))); // burnt face = the fire death state, not makeup
  if (k === 'presets') return kindOf(e.type, ix.body(e), names) === 'object' && objdMaterials(ix.body(e)).length >= PRESETS;
  return kindOf(e.type, ix.body(e), names) === k;
}

// default sources: deterministic picks of small/medium items (bytes on disk per clone in range)
// build kinds are small (a pattern = a few textures, a fence = one mesh): lower floor
export function pickSources(ix: Index, kind: Kind, names: Map<bigint, string>, want = 6,
  minB = ['object', 'inv', 'cas', 'door', 'window', 'presets'].includes(kind) ? 100_000 : 5_000, maxB = kind === 'presets' ? 12_000_000 : 3_000_000): Entry[] {
  const pool = [...ix.byKey.values()].filter(e => e.type === ROOT_TYPE[kind] && e.group === 0).sort((a, b) => (a.inst < b.inst ? -1 : 1));
  const out: Entry[] = [], body = new Map<string, number>();
  for (const e of pool) {
    if (out.length >= want) break;
    const part = kind === 'cas' ? /^[a-z]{2}(Hair|Top|Body)/.exec(caspName(ix.body(e)))?.[1] : kind === 'makeup' && fitsKind(ix, e, kind, names) ? MAKEUP[caspBodyType(ix.body(e))] : kind === 'makeup' ? undefined : 'object';
    const split = kind === 'cas' ? 3 : kind === 'makeup' ? Object.keys(MAKEUP).length : 0; // an even split of hair, top, full body / of the makeup types
    if (!part || (split && (body.get(part) ?? 0) >= want / split)) continue;
    if (!['object', 'cas', 'makeup'].includes(kind) && !fitsKind(ix, e, kind, names)) continue; // 'object' keeps its old picks (seed-stable sets)
    try {
      const { item } = cloneItem(ix, e, 0, 'probe', names, kind);
      if (item.diskBytes < minB || item.diskBytes > maxB) continue;
      body.set(part, (body.get(part) ?? 0) + 1);
      out.push(e);
    } catch { /* no catalogue name (hidden or debug object): skip */ }
  }
  return out;
}

export function englishNames(ix: Index) {
  const m = new Map<bigint, string>();
  for (const e of ix.byKey.values()) if (e.type === T.STBL && (e.inst >> 56n) === 0n) for (const [k, s] of readStbl(ix.body(e))) m.set(k, s);
  return m;
}

const GAME_PKGS = ['GameData/Shared/Packages/FullBuild0', 'GameData/Shared/Packages/FullBuild1', 'GameData/Shared/Packages/FullBuild2',
  'GameData/Shared/Packages/DeltaBuild0', 'GameData/Shared/Packages/DeltaBuild1', 'GameData/Shared/Packages/DeltaBuild2', 'Thumbnails/AllThumbnails', 'Thumbnails/CasThumbnails'];
const ROOT = resolve(import.meta.dirname, '..');

// writes the set; out must be a folder inside some game-copy/cc-synth/ (git-ignored; a worktree may write the main checkout's)
// first: index of the first item (file names, instances); append: add to the set's manifest (first must be past its last item)
export function generate(ix: Index, o: { count: number; kinds: Kind[]; seed: string; out: string; sources: Entry[]; names: Map<bigint, string>; first?: number; append?: boolean }) {
  if (!/[\\/]game-copy[\\/]cc-synth[\\/][^\\/]/i.test(resolve(o.out))) throw new Error(`--out must be under game-copy/cc-synth/ (got ${o.out})`);
  mkdirSync(o.out, { recursive: true });
  const first = o.first ?? 0, mf = join(o.out, 'manifest.json');
  const old: { seed: string; items: Item[]; problems: string[]; uniqueKeys: number; kinds: Kind[]; sources: string[] } | undefined = o.append && existsSync(mf) ? JSON.parse(readFileSync(mf, 'utf8')) : undefined;
  if (old && (old.seed !== o.seed || old.items.some(x => x.i >= first))) throw new Error(`--append: --first ${first} must be past the set's last item and --seed equal (${old.seed})`);
  const items: Item[] = [], seen = new Map<string, string>(), problems: string[] = [];
  // 'object' takes any OBJD source (old behaviour), the build kinds only sources of their own kind
  const fits = (e: Entry, k: Kind) => e.type === ROOT_TYPE[k] && (k === 'object' || k === 'cas' || fitsKind(ix, e, k, o.names));
  const byKind = new Map(o.kinds.map(k => [k, o.sources.filter(e => fits(e, k))])), used = new Map<Kind, number>();
  // as if items 0..first-1 came first with the same kind list: --first 1000 --append continues a 1000-item set exactly
  for (let i = 0; i < first; i++) used.set(o.kinds[i % o.kinds.length], (used.get(o.kinds[i % o.kinds.length]) ?? 0) + 1);
  for (let i = first; i < first + o.count; i++) {
    const k = o.kinds[i % o.kinds.length], srcs = byKind.get(k)!;
    if (!srcs.length) throw new Error(`no ${k} source`);
    const n = used.get(k) ?? 0; used.set(k, n + 1); // per-kind counter: the same picks as floor(i / kinds) for objects,cas
    const root = srcs[n % srcs.length];
    const { res, item } = cloneItem(ix, root, i, o.seed, o.names, k);
    const file = `ccgen-${o.seed}-${item.kind}-${String(i).padStart(5, '0')}.package`;
    const pkg = writePackage(res);
    writeFileSync(join(o.out, file), pkg);
    // read back from disk with our reader
    const v = verifyPackage(readFileSync(join(o.out, file)), ix, new Set([...closure(ix, root).values()].map(n => n.e.inst)));
    for (const k of v.keys) { if (seen.has(k)) problems.push(`${file}: key ${k} also in ${seen.get(k)}`); else seen.set(k, file); }
    problems.push(...v.problems.map(p => `${file}: ${p}`));
    items.push({ ...item, package: file, ...(k === 'inv' && { inventoriable: true }) });
  }
  // appended items hash their own index into every instance (newInst), so keys cannot meet the old items' keys
  const all = [...(old?.items ?? []), ...items];
  const manifest = { tool: 'tools/cc-gen.ts', seed: o.seed, count: all.length, kinds: [...(old?.kinds ?? []), ...o.kinds], time: new Date().toISOString(),
    sources: [...new Set([...(old?.sources ?? []), ...o.sources.map(e => keyOf(e.type, e.group, e.inst))])], totalDiskBytes: all.reduce((s, x) => s + x.diskBytes, 0),
    totalMemBytes: all.reduce((s, x) => s + x.memBytes, 0), uniqueKeys: (old?.uniqueKeys ?? 0) + seen.size, problems: [...(old?.problems ?? []), ...problems], items: all };
  writeFileSync(mf, JSON.stringify(manifest, null, 1));
  return manifest;
}

// --kind value -> the round-robin kind list
export function parseKinds(s: string): Kind[] {
  const all = ['object', 'cas', ...BUILD_KINDS] as Kind[];
  const named: Record<string, Kind[]> = { objects: ['object'], cas: ['cas'], both: ['object', 'cas'], build: BUILD_KINDS, all };
  if (named[s]) return named[s];
  all.push('inv', 'makeup', 'presets'); // by name only (inv needs --decompiled)
  return s.split(',').flatMap(x => {
    const [k, w = '1'] = x.split(':');
    if (!all.includes(k as Kind) || !/^[1-9]\d*$/.test(w)) throw new Error(`--kind ${x}: objects|cas|both|build|all or a list of ${all.join('|')}[:weight]`);
    return Array<Kind>(Number(w)).fill(k as Kind);
  });
}

// ---------------------------------------------------------------- self-check: synthetic fixture, no EA bytes
function selfCheck() {
  const tgi = (t: number, g: number, i: bigint) => { const b = Buffer.alloc(16); b.writeUInt32LE(t); b.writeUInt32LE(g, 4); b.writeBigUInt64LE(i, 8); return b; };
  const itg = (t: number, g: number, i: bigint) => { const b = Buffer.alloc(16); b.writeBigUInt64LE(i); b.writeUInt32LE(t, 8); b.writeUInt32LE(g, 12); return b; };
  const k8 = (v: bigint) => { const b = Buffer.alloc(8); b.writeBigUInt64LE(v); return b; };
  const I = { objd: 0x350n, vpxy: 0x6c0cf2n, modl: 0x6c0cf2n, img: 0x1234567890abcdefn, rig: 0x5718e0c685f6db7fn, xml: 0xfafb98b5b96a98c9n, casp: 0x7a278a782ebe55fcn, cimg: 0x0b65c8919121fa3bn };
  const nameK = 0xa7acf7d94e7feaf9n, descK = 0x2438c41b2b13ed32n;
  const name = 'Test Stove', pname = 'afHairTest';
  const be = Buffer.from(pname, 'utf16le').swap16();
  const casp = Buffer.concat([Buffer.from([0x12, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]), Buffer.from([be.length]), be, Buffer.alloc(9), itg(T.IMG, 0, I.cimg), itg(T.VPXY, 1, I.vpxy)]);
  const g = { modl: 1 };
  const game = writePackage([
    { type: T.OBJD, group: 0, instance: I.objd, data: Buffer.concat([Buffer.alloc(12), k8(nameK), k8(descK), tgi(T.IMG, 0, I.img), tgi(0x0333406c, 0, I.xml), tgi(T.OBJK, 0, I.objd)]) },
    { type: T.OBJK, group: 0, instance: I.objd, data: Buffer.concat([Buffer.alloc(5), tgi(T.VPXY, 1, I.vpxy)]) },
    { type: T.VPXY, group: 1, instance: I.vpxy, data: Buffer.concat([itg(T.VPXY, 1, I.vpxy), tgi(T.MODL, g.modl, I.modl), tgi(T.RIG, 0, I.rig)]) },
    { type: T.MODL, group: 1, instance: I.modl, data: Buffer.concat([Buffer.alloc(3), itg(T.IMG, 0, I.img)]) },
    { type: T.IMG, group: 0, instance: I.img, data: Buffer.from('DDS texture bytes') },
    { type: T.IMG, group: 0, instance: I.cimg, data: Buffer.from('DDS cas texture') },
    { type: T.RIG, group: 0, instance: I.rig, data: Buffer.from('rig') },
    { type: 0x0333406c, group: 0, instance: I.xml, data: Buffer.from('<xml/>') },
    { type: 0x0580a2b4, group: 2, instance: I.objd, data: Buffer.from('thumb') },
    { type: T.CASP, group: 0, instance: I.casp, data: casp },
    { type: 0x626f60cc, group: 0, instance: I.casp, data: Buffer.from('cas thumb') },
    { type: T.STBL, group: 0, instance: 0x0011223344556677n, data: writeStbl(new Map([[nameK, name], [descK, 'A stove']])) },
  ]);
  const dir = mkdtempSync(join(tmpdir(), 'cc-gen-')), gp = join(dir, 'game.package');
  try {
    writeFileSync(gp, game);
    const ix = new Index().add(gp), names = englishNames(ix);
    assert.equal(names.get(nameK), name, 'STBL round trip');
    assert.equal(caspName(casp), pname, 'CASP name');
    const root = ix.byKey.get(keyOf(T.OBJD, 0, I.objd))!;
    const c = closure(ix, root);
    assert.deepEqual([...c.values()].map(n => n.e.type).sort(), [T.OBJD, T.OBJK, T.VPXY, T.MODL, T.IMG, T.RIG, 0x0580a2b4].sort(), 'object closure: shared XML left out, thumbnail in');
    const out = join(dir, 'game-copy', 'cc-synth', 'set');
    const m = generate(ix, { count: 4, kinds: ['object', 'cas'], seed: 't', out, sources: [root, ix.byKey.get(keyOf(T.CASP, 0, I.casp))!], names });
    assert.deepEqual(m.problems, [], 'read-back, references and key uniqueness');
    assert.equal(m.items.length, 4); assert.equal(m.uniqueKeys, 2 * 8 + 2 * 7); // object: OBJD OBJK VPXY MODL IMG RIG thumb STBL; cas: CASP IMG thumb + the VPXY chain
    assert.deepEqual(m.items.map(x => x.kind), ['object', 'cas', 'object', 'cas']);
    const outA = join(dir, 'game-copy', 'cc-synth', 'split'), srcA = [root, ix.byKey.get(keyOf(T.CASP, 0, I.casp))!];
    generate(ix, { count: 3, kinds: ['object', 'cas'], seed: 't', out: outA, sources: srcA, names });
    const mA = generate(ix, { count: 1, kinds: ['object', 'cas'], seed: 't', out: outA, sources: srcA, names, first: 3, append: true });
    assert.deepEqual(mA.items.map(x => [x.package, x.root]), m.items.map(x => [x.package, x.root]), 'split + append = one run');
    assert.equal(m.items[0].sharedRefs, 1, 'XML reference kept shared');
    const pk = readFileSync(join(out, m.items[0].package)), e = readIndex(pk);
    const stbl = readStbl(pk.subarray(e.find(x => x.type === T.STBL)!.offset).subarray(0, e.find(x => x.type === T.STBL)!.fileSize));
    assert.deepEqual([...stbl.values()][0], 'CCGen Test Stove #0', 'catalogue name');
    const objd = e.find(x => x.type === T.OBJD)!, ob = pk.subarray(objd.offset, objd.offset + objd.fileSize);
    assert.equal(ob.readBigUInt64LE(12), [...stbl.keys()][0], 'OBJD name key rewritten');
    assert.equal(ob.readBigUInt64LE(52), I.xml, 'shared XML instance unchanged');
    assert.equal(e.find(x => x.type === T.OBJK)!.inst, objd.inst, 'OBJD/OBJK stay paired');
    assert.ok(e.find(x => x.type === 0x0580a2b4)!.inst === objd.inst, 'thumbnail follows the OBJD instance');
    // a bad clone is caught: point the OBJK back at the EA VPXY
    const bad = Buffer.from(pk), ok = e.find(x => x.type === T.OBJK)!;
    tgi(T.VPXY, 1, I.vpxy).copy(bad, ok.offset + 5);
    assert.ok(verifyPackage(bad, ix, new Set([I.vpxy])).problems.some(p => /still points at EA/.test(p)), 'verify catches an EA reference');
    assert.throws(() => generate(ix, { count: 1, kinds: ['object'], seed: 't', out: join(dir, 'elsewhere'), sources: [root], names }), /must be under/);
    ix.close();
    // build kinds: a wall pattern (CWAL + own IMG + shared complate XML + 0x0589DC44 thumbnail in group 5) and a door OBJD
    const B = { cwal: 0x96eee1fd819c707an, wimg: 0x2222333344445555n, door: 0x3a4n, wk: 0x1111aaaabbbbccccn, wd: 0x2222aaaabbbbccccn, dk: 0x3333aaaabbbbccccn };
    const gp2 = join(dir, 'game2.package');
    writeFileSync(gp2, Buffer.concat([writePackage([
      { type: T.CWAL, group: 0, instance: B.cwal, data: Buffer.concat([Buffer.alloc(9), tgi(T.IMG, 0, B.wimg), tgi(0x0333406c, 0, I.xml), k8(B.wk), k8(B.wd)]) },
      { type: T.IMG, group: 0, instance: B.wimg, data: Buffer.from('DDS wall texture') },
      { type: 0x0333406c, group: 0, instance: I.xml, data: Buffer.from('<complate/>') },
      { type: 0x0589dc44, group: 5, instance: B.cwal, data: Buffer.from('wall thumb') },
      // + a key of another game type at the cloned texture's instance (as VPXY -> 0x0C244B35 next to the RIG): shared, no problem
      { type: T.OBJD, group: 0, instance: B.door, data: Buffer.concat([Buffer.from('\x11doorSingleTest\x00', 'latin1'), k8(B.dk), k8(B.wd), tgi(T.IMG, 0, B.wimg), tgi(0x0c244b35, 0, B.wimg)]) },
      { type: 0x0c244b35, group: 0, instance: 9n, data: Buffer.from('other') },
      // an inventoriable object: OBJK scriptClass names a class derived from one that adds a Sim-inventory ItemComponent
      { type: T.OBJD, group: 0, instance: 0x592n, data: Buffer.concat([Buffer.alloc(4), k8(B.dk), k8(B.wd), tgi(T.OBJK, 0, 0x592n)]) },
      { type: T.OBJK, group: 0, instance: 0x592n, data: Buffer.from('\x00scriptClass\x05\x00\x00\x00\x1eSims3.Gameplay.Objects.TestBear\x00', 'latin1') },
      { type: T.STBL, group: 0, instance: 0x0011223344556678n, data: writeStbl(new Map([[B.wk, 'Test Wallpaper'], [B.wd, 'To delete a section of the same wall covering, hold Shift.'], [B.dk, 'Test Door']])) },
    ])]));
    const ix2 = new Index().add(gp2), n2 = englishNames(ix2);
    const cw = ix2.byKey.get(keyOf(T.CWAL, 0, B.cwal))!, dr = ix2.byKey.get(keyOf(T.OBJD, 0, B.door))!;
    assert.equal(kindOf(T.CWAL, ix2.body(cw), n2), 'wall', 'CWAL wall by description');
    assert.equal(kindOf(T.OBJD, ix2.body(dr), n2), 'door', 'door by model name');
    assert.equal(kindOf(T.OBJD, Buffer.from('\x10fridgeSideBySide'), n2), 'object');
    assert.deepEqual(parseKinds('wall:2,door'), ['wall', 'wall', 'door']);
    assert.equal(parseKinds('all').length, 10); assert.throws(() => parseKinds('roof'), /--kind/);
    const out2 = join(dir, 'game-copy', 'cc-synth', 'build');
    const m2 = generate(ix2, { count: 4, kinds: ['wall', 'door'], seed: 'b', out: out2, sources: [cw, dr], names: n2 });
    assert.deepEqual(m2.problems, [], 'build read-back');
    assert.deepEqual(m2.items.map(x => x.kind), ['wall', 'door', 'wall', 'door']);
    const wp = readFileSync(join(out2, m2.items[0].package)), we = readIndex(wp);
    assert.deepEqual(we.map(x => x.type).sort(), [T.CWAL, T.IMG, 0x0589dc44, T.STBL].sort(), 'wall clone: CWAL, own IMG, thumbnail, STBL; XML shared');
    assert.notEqual(we.find(x => x.type === T.IMG)!.inst, B.wimg, 'own texture key');
    const ws = we.find(x => x.type === T.STBL)!;
    assert.equal([...readStbl(wp.subarray(ws.offset, ws.offset + ws.fileSize)).values()][0], 'CCGen Test Wallpaper #0');
    assert.notEqual(readIndex(readFileSync(join(out2, m2.items[2].package))).find(x => x.type === T.IMG)!.inst, we.find(x => x.type === T.IMG)!.inst, 'textures differ per item');
    assert.throws(() => generate(ix2, { count: 1, kinds: ['floor'], seed: 'b', out: out2, sources: [cw], names: n2 }), /no floor source/);
    // inv: class rule from (own, made-up) class text, appended to the set past its last item
    const cls = inventoriableClasses(['class TestToyBase : GameObject { void OnStartup() { AddComponent<ItemComponent>(new object[1] { ItemComponent.SimInventoryItem }); } }',
      'class TestBear : TestToyBase { }', 'class TestTable : GameObject { }',
      'public abstract class TestPc : GameObject {\n\tprotected void AddPcInventory()\n\t{\n\t\tAddComponent<ItemComponent>(new object[1] { ItemComponent.SimInventoryItem });\n\t}\n}',
      'public class TestDesktop : TestPc { }', 'public class TestLaptop : TestPc {\n\tpublic override void OnStartup()\n\t{\n\t\tAddPcInventory();\n\t}\n}']);
    assert.deepEqual([...cls].sort(), ['TestBear', 'TestLaptop', 'TestToyBase'], 'seed class + derived; a helper add counts only for its callers (laptop, not desktop/base); not the table');
    setInventoriable(cls);
    const bear = ix2.byKey.get(keyOf(T.OBJD, 0, 0x592n))!;
    assert.equal(scriptClass(ix2, bear), 'Sims3.Gameplay.Objects.TestBear');
    assert.throws(() => generate(ix2, { count: 2, kinds: ['inv'], seed: 'b', out: out2, sources: [dr, bear], names: n2, first: 2, append: true }), /--append/);
    const m3 = generate(ix2, { count: 2, kinds: ['inv'], seed: 'b', out: out2, sources: [dr, bear], names: n2, first: 4, append: true });
    assert.deepEqual(m3.problems, []);
    assert.equal(m3.items.length, 6, 'appended to the manifest');
    assert.deepEqual(m3.items.slice(4).map(x => [x.i, x.kind, x.inventoriable, x.source]), [[4, 'inv', true, keyOf(T.OBJD, 0, 0x592n)], [5, 'inv', true, keyOf(T.OBJD, 0, 0x592n)]], 'only the bear, flagged');
    assert.ok(!m3.items[1].inventoriable);
    ix2.close();
    // makeup: a lipstick CASP (body type 17) naming its overlay by an XML text key and in its ITG list; presets: an OBJD whose
    // 2 materials (presets) both name one texture in their own TGI lists
    const M = { lip: 0x4444555566667777n, ov: 0x6b246d3c132af8a5n, obj: 0x5a5n, tex: 0x7777888899990000n, n1: 0x5555aaaabbbbccccn, n2: 0x6666aaaabbbbccccn };
    const i32 = (v: number) => { const b = Buffer.alloc(4); b.writeInt32LE(v); return b; };
    const xml = Buffer.from(`<preset><value key="Face Overlay" value="key:00B2D882:00000000:${M.ov.toString(16).toUpperCase().padStart(16, '0')}" /></preset>`, 'utf16le');
    const lname = Buffer.from('afMakeupLipstickTest', 'utf16le').swap16();
    const lipstick = Buffer.concat([Buffer.from([0x12, 0, 0, 0, 0, 0, 0, 0]), i32(1), i32(xml.length / 2), xml, i32(0), Buffer.from([lname.length]), lname, Buffer.alloc(5), i32(17), itg(T.IMG, 0, M.ov)]);
    const mat = () => { // u8 type 1, u32 end (rel), u16, u32 list (rel), u32 list size, block, i32 count + TGI, u32
      const blk = Buffer.from('block'), list = Buffer.concat([i32(1), tgi(T.IMG, 0, M.tex)]), h = Buffer.alloc(15), at = 15 + blk.length;
      h[0] = 1; h.writeUInt32LE(at + list.length - 5, 1); h.writeUInt32LE(at - 11, 7); h.writeUInt32LE(list.length, 11);
      return Buffer.concat([h, blk, list, Buffer.alloc(4)]);
    };
    const gp3 = join(dir, 'game3.package');
    writeFileSync(gp3, writePackage([
      { type: T.CASP, group: 0, instance: M.lip, data: lipstick },
      { type: T.IMG, group: 0, instance: M.ov, data: Buffer.from('DDS lip overlay') },
      { type: T.OBJD, group: 0, instance: M.obj, data: Buffer.concat([i32(22), i32(0), i32(0), i32(2), mat(), mat(), k8(M.n1), k8(M.n2)]) },
      { type: T.IMG, group: 0, instance: M.tex, data: Buffer.from('DDS pattern') },
      { type: T.STBL, group: 0, instance: 0x0011223344556679n, data: writeStbl(new Map([[M.n1, 'Test Sofa'], [M.n2, 'A sofa']])) },
    ]));
    const ix3 = new Index().add(gp3), n3 = englishNames(ix3);
    const lip = ix3.byKey.get(keyOf(T.CASP, 0, M.lip))!, pobj = ix3.byKey.get(keyOf(T.OBJD, 0, M.obj))!;
    assert.equal(caspBodyType(ix3.body(lip)), 17, 'CASP body type');
    assert.equal(objdMaterials(ix3.body(pobj)).length, 2, 'OBJD presets');
    setMinPresets(2);
    const out3 = join(dir, 'game-copy', 'cc-synth', 'mp');
    const m4 = generate(ix3, { count: 2, kinds: ['makeup', 'presets'], seed: 'm', out: out3, sources: [lip, pobj], names: n3 });
    assert.deepEqual(m4.problems, [], 'makeup / presets read-back');
    assert.deepEqual(m4.items.map(x => [x.kind, x.makeup, x.presets]), [['makeup', 'Lipstick', undefined], ['presets', undefined, 2]]);
    const lp = readFileSync(join(out3, m4.items[0].package)), le = readIndex(lp), lc = le.find(x => x.type === T.CASP)!, lb = lp.subarray(lc.offset, lc.offset + lc.fileSize);
    const tk = caspTextKeys(lb);
    assert.equal(tk.length, 1);
    assert.ok(tk[0].inst !== M.ov && le.some(x => x.type === T.IMG && x.inst === tk[0].inst), 'XML text key names the cloned overlay');
    assert.ok(!le.some(x => x.type === T.STBL), 'makeup: no catalogue string');
    const pp = readFileSync(join(out3, m4.items[1].package)), pe = readIndex(pp), po = pe.find(x => x.type === T.OBJD)!, pb = pp.subarray(po.offset, po.offset + po.fileSize);
    const mt = objdMaterials(pb).map(x => pb.readBigUInt64LE(x.tgi + 4 + 8));
    assert.ok(mt[0] !== mt[1] && mt.every(t => t !== M.tex && pe.some(x => x.type === T.IMG && x.inst === t)), 'each preset its own texture copy');
    const badL = Buffer.from(lp);
    badL.write(M.ov.toString(16).toUpperCase().padStart(16, '0'), lc.offset + tk[0].at, 'utf16le');
    assert.ok(verifyPackage(badL, ix3, new Set()).problems.some(p => /XML still names EA/.test(p)), 'verify catches an EA text key');
    setMinPresets(3);
    assert.throws(() => generate(ix3, { count: 1, kinds: ['presets'], seed: 'm', out: out3, sources: [pobj], names: n3 }), /no presets source/, 'too few presets');
    assert.deepEqual(parseKinds('makeup'), ['makeup']);
    ix3.close();
    // --kind tiny (docs/17 M4 pkgs-N): N packages of one 16-byte resource each, no EA bytes
    const outT = join(dir, 'pkgs-3');
    assert.equal(writeTiny(3, 's', outT), 3);
    const tf = readdirSync(outT).filter(f => f.endsWith('.package')).sort();
    assert.deepEqual(tf, ['ccgen-tiny-00000.package', 'ccgen-tiny-00001.package', 'ccgen-tiny-00002.package']);
    const ti = readIndex(readFileSync(join(outT, tf[0])));
    assert.equal(ti.length, 1); assert.equal(ti[0].type, TINY_TYPE);
    assert.notEqual(readIndex(readFileSync(join(outT, tf[1])))[0].inst, ti[0].inst, 'own instance per package');
    assert.throws(() => writeTiny(1, 's', outT), /exists/, 'never over an existing set');
  } finally { rmSync(dir, { recursive: true, force: true }); }
  console.log('cc-gen self-check done');
}

// docs/17 M4: package count apart from resource count. One resource per package, of a type no game code reads
// [inferred: 0x7FFF0001 is not in the base-game index (cc-gen --list-sources never lists it)], so only the per-package cost remains.
const TINY_TYPE = 0x7fff0001;
function writeTiny(count: number, seed: string, out: string): number {
  if (existsSync(out)) throw new Error(`${out} exists: pick a new --out`);
  mkdirSync(out, { recursive: true });
  for (let i = 0; i < count; i++) {
    const instance = fnv64(`tiny:${seed}:${i}`), data = Buffer.alloc(16);
    data.writeBigUInt64LE(instance); data.writeUInt32LE(i, 8);
    writeFileSync(join(out, `ccgen-tiny-${String(i).padStart(5, '0')}.package`), writePackage([{ type: TINY_TYPE, group: 0, instance, data }]));
  }
  writeFileSync(join(out, 'manifest.json'), JSON.stringify({ tool: 'tools/cc-gen.ts', kind: 'tiny', seed, count, type: `0x${TINY_TYPE.toString(16)}`, time: new Date().toISOString() }, null, 1));
  return count;
}

if (process.argv[1]?.endsWith('cc-gen.ts')) {
  const { values: v } = parseArgs({ options: { count: { type: 'string', default: '10' }, kind: { type: 'string', default: 'both' }, seed: { type: 'string', default: '1' },
    out: { type: 'string' }, source: { type: 'string' }, game: { type: 'string' }, 'list-sources': { type: 'boolean' }, 'self-check': { type: 'boolean' },
    first: { type: 'string', default: '0' }, append: { type: 'boolean' }, presets: { type: 'string', default: '6' }, decompiled: { type: 'string', default: join(ROOT, 'game-copy', 'decompiled') } } });
  if (v['self-check']) { selfCheck(); process.exit(0); }
  if (v.kind === 'tiny') { // no game index needed
    const out = v.out ?? join(ROOT, 'game-copy', 'cc-synth', `pkgs-${v.count}`);
    console.log(`cc-gen: ${writeTiny(Number(v.count), v.seed!, out)} tiny packages in ${out.split(sep).join('/')}`);
    process.exit(0);
  }
  const kinds = parseKinds(v.kind!);
  setMinPresets(Number(v.presets));
  if (kinds.includes('inv')) { // class hierarchy of the gameplay DLLs (decompiled, git-ignored)
    const files = (d: string): string[] => readdirSync(d, { withFileTypes: true }).flatMap(e => e.isDirectory() ? files(join(d, e.name)) : e.name.endsWith('.cs') ? [join(d, e.name)] : []);
    setInventoriable(inventoriableClasses(['Sims3GameplayObjects', 'Sims3GameplaySystems', 'Sims3StoreObjects'].flatMap(a => files(join(v.decompiled!, a))).map(f => readFileSync(f, 'utf8'))));
  }
  const gameDir = v.game ?? process.env.TS3_GAME ?? 'C:/Program Files (x86)/Steam/steamapps/common/The Sims 3';
  const ix = new Index();
  for (const p of GAME_PKGS) ix.add(join(gameDir, `${p}.package`));
  const names = englishNames(ix);
  const SRC_TYPE: Record<string, number> = { objd: T.OBJD, casp: T.CASP, cwal: T.CWAL, cfen: T.CFEN, ctpt: T.CTPT, cral: T.CRAL, cstr: T.CSTR };
  const sources = v.source ? v.source.split(',').map(s => { // objd|casp|cwal|cfen|ctpt|cral|cstr:<inst hex>
    const [t, i] = s.split(':'), e = SRC_TYPE[t] === undefined ? undefined : ix.byKey.get(keyOf(SRC_TYPE[t], 0, BigInt(`0x${i}`)));
    if (!e) throw new Error(`source ${s} not in the game index`); return e;
  }) : [...new Set(kinds)].flatMap(k => pickSources(ix, k, names, ({ inv: 16, makeup: 200, presets: 24 } as Record<string, number>)[k] ?? 6));
  if (v['list-sources']) {
    for (const e of sources) { const { item } = cloneItem(ix, e, 0, 'probe', names, new Set(kinds).size === 1 ? kinds[0] : e.type === T.OBJD ? kindOf(e.type, ix.body(e), names) : undefined); console.log(`${item.kind} ${hex(e.inst)} "${item.name}" ${item.resources} res, ${(item.diskBytes / 1e6).toFixed(2)} MB disk, ${(item.memBytes / 1e6).toFixed(2)} MB raw, ${item.sharedRefs} shared refs`); }
    process.exit(0);
  }
  const count = Number(v.count);
  const out = v.out ?? join(ROOT, 'game-copy', 'cc-synth', `${v.kind!.replace(/[^\w-]+/g, '_')}-${count}-s${v.seed}`);
  const t0 = Date.now();
  const m = generate(ix, { count, kinds, seed: v.seed!, out, sources, names, first: Number(v.first), append: v.append });
  ix.close();
  const per = (k: string) => { const x = m.items.filter(i => i.kind === k); return x.length ? `${k} ${x.length} items, ${(x.reduce((s, i) => s + i.diskBytes, 0) / x.length / 1e6).toFixed(2)} MB/item disk` : ''; };
  console.log(`cc-gen: ${m.count} packages in ${out.split(sep).join('/')} (${((Date.now() - t0) / 1000).toFixed(0)} s): ${[...new Set(kinds)].map(per).filter(Boolean).join(', ')}; total ${(m.totalDiskBytes / 1e9).toFixed(2)} GB disk, ${(m.totalMemBytes / 1e9).toFixed(2)} GB raw; ${m.uniqueKeys} unique keys; problems ${m.problems.length}`);
  for (const p of m.problems.slice(0, 10)) console.log(`  ${p}`);
  process.exit(m.problems.length ? 1 : 0);
}
