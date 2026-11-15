// EA allocation tag strings ("CAS/GeometryResource", "ResourceLoad/PackedStream/...") in TS3W.exe and the code that
// pushes them (push imm32 / mov r32, imm32 / mov [esp+d8], imm32), grouped into owner buckets per 16 KB of code and merged
// into ranges: the evidence for the [MemDiag] AllocSitesOwners table (native/ts3native/allocsites.cpp kAsOwn, docs/190 §5.6).
//   node tools/alloc-owner-tags.ts <TS3W.exe>              ranges: owner, lo-hi, pushes of that owner / all, purity, runners-up
//   node tools/alloc-owner-tags.ts <TS3W.exe> --tags CAS/  every push of tags with that prefix (address, tag)
//   node tools/alloc-owner-tags.ts --self-check
// Static scan of immediates, not a disassembly: a dword that equals a tag string's VA after one of those opcodes counts
// [inferred: false hits are rare at 6,000 strings in 13 MB]. Purity = share of the range's pushes that map to its owner.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

export const OWNERS: [RegExp, string][] = [
  [/^(ResourceSystem|ResourceLoad|Services\/EAIO|Services\/Resource)/, 'res'], [/^Scratch\/(DownloadContent|ContentPatchFile|StoreItemsManager|LotContentManager)/, 'content'],
  [/^CAS\//, 'cas'], [/^(Renderer|Graphics|GraphicsSys|Shader|Lighting|GlobalLightSettings|ThumbnailRenderer)\//, 'gfx'],
  [/^(Routing|PathPlan|PathPlannerWorld|Locomotion)\//, 'route'], [/^(Animation|SACS)\//, 'anim'], [/^Audio\//, 'audio'],
  [/^(Scripting|ManagedHeap)\//, 'script'], [/^(UI|Ui|UTFSockets|ConsoleWindow|Scratch\/UI|Services\/UTF)/, 'ui'], [/^String\//, 'str'],
  [/^SpeedTree\//, 'tree'], [/^Swarm\//, 'fx'], [/^(Objects|ObjDesSrv)\//, 'obj'],
  [/^(Lot|LotRenderer|LotLODCreator|FloorFeature|FloorRenderManager|RoofRenderManager|UserTool|UserToolHost|BlueprintHelper|Scratch\/GableRoof|World|Terrain|TerrainBuilder|RoadNetwork|Misc\/Pond|Misc\/Weather)\//, 'world'],
  [/^(SIGS|Sims3Web|HTTP|IGS)\//, 'online'], [/^(Video|VideoRecording|VideoCaptureEngine)\//, 'video'], [/^Physics\//, 'phys'],
  [/^(Services|Threads|Debug|GameHost|Misc|JobManager|SplashManager|QueryHost|TSLHost)\//, 'core'],
];
export const ownerOf = (tag: string) => OWNERS.find(([r]) => r.test(tag))?.[1] ?? null;

export function tagPushes(b: Buffer): { at: number; tag: string }[] {
  const pe = b.readUInt32LE(0x3c), nsec = b.readUInt16LE(pe + 6), opt = pe + 24, base = b.readUInt32LE(opt + 28);
  const secs = [];
  for (let i = 0, s = opt + b.readUInt16LE(pe + 20); i < nsec; i++, s += 40)
    secs.push({ name: b.toString('latin1', s, s + 8).replace(/\0.*/s, ''), va: b.readUInt32LE(s + 12), raw: b.readUInt32LE(s + 20), rs: b.readUInt32LE(s + 16) });
  const text = secs.find(s => s.name === '.text');
  assert(text, 'no .text section');
  const strs = new Map<number, string>();
  const re = /^[A-Z][A-Za-z0-9]+(\/[A-Za-z0-9_ .:-]+)+$/;
  for (const s of secs) {
    if (s === text) continue;
    for (let o = s.raw, st = -1; o < s.raw + s.rs; o++) {
      const c = b[o];
      if (c >= 32 && c < 127) { if (st < 0) st = o; continue; }
      if (c === 0 && st >= 0 && o - st >= 5) { const t = b.toString('latin1', st, o); if (re.test(t)) strs.set(base + s.va + st - s.raw, t); }
      st = -1;
    }
  }
  const out: { at: number; tag: string }[] = [];
  const va = (o: number) => base + text.va + o - text.raw;
  for (let o = text.raw; o < text.raw + text.rs - 8; o++) {
    const op = b[o];
    if ((op === 0x68 || (op >= 0xb8 && op <= 0xbf)) && strs.has(b.readUInt32LE(o + 1))) out.push({ at: va(o), tag: strs.get(b.readUInt32LE(o + 1))! });
    else if (op === 0xc7 && b[o + 1] === 0x44 && strs.has(b.readUInt32LE(o + 4))) out.push({ at: va(o), tag: strs.get(b.readUInt32LE(o + 4))! });
  }
  return out;
}

// dominant owner per 16 KB bin; neighbouring bins (gap <= 64 KB) of one owner merge
export function ranges(pushes: { at: number; tag: string }[]) {
  const B = 14, bins = new Map<number, Record<string, number>>();
  for (const p of pushes) { const o = ownerOf(p.tag); if (!o) continue; const k = p.at >>> B; const c = bins.get(k) ?? {}; c[o] = (c[o] ?? 0) + 1; bins.set(k, c); }
  const out: { own: string; lo: number; hi: number; c: Record<string, number> }[] = [];
  for (const k of [...bins.keys()].sort((a, b) => a - b)) {
    const c = bins.get(k)!, own = Object.entries(c).sort((a, b) => b[1] - a[1])[0][0], last = out.at(-1);
    if (last && last.own === own && (k << B) - last.hi <= 0x10000) { last.hi = (k + 1) << B; for (const [x, y] of Object.entries(c)) last.c[x] = (last.c[x] ?? 0) + y; }
    else out.push({ own, lo: k << B, hi: (k + 1) << B, c: { ...c } });
  }
  return out;
}
const hex = (n: number) => '0x' + (n >>> 0).toString(16).toUpperCase();

function selfCheck() {
  // a 2-section PE: .text with push / mov / mov [esp+8] of the tags, .rdata with three strings (one not a tag path)
  const b = Buffer.alloc(0x3000);
  b.writeUInt32LE(0x80, 0x3c), b.writeUInt16LE(2, 0x86), b.writeUInt16LE(0xe0, 0x94), b.writeUInt32LE(0x400000, 0x98 + 28);
  const sec = (i: number, name: string, va: number, raw: number, size: number) => {
    const s = 0x98 + 0xe0 + i * 40;
    b.write(name, s, 'latin1'), b.writeUInt32LE(size, s + 8), b.writeUInt32LE(va, s + 12), b.writeUInt32LE(size, s + 16), b.writeUInt32LE(raw, s + 20);
  };
  sec(0, '.text', 0x1000, 0x1000, 0x1000), sec(1, '.rdata', 0x2000, 0x2000, 0x1000);
  b.write('CAS/GeometryResource\0Audio/ResourceBinary/Data\0not a tag\0', 0x2010, 'latin1');
  const cas = 0x402010, aud = 0x402010 + 21, plain = 0x402010 + 47;
  b[0x1100] = 0x68, b.writeUInt32LE(cas, 0x1101);                    // push imm32
  b[0x1200] = 0xb9, b.writeUInt32LE(aud, 0x1201);                    // mov ecx, imm32
  b.set([0xc7, 0x44, 0x24, 0x08], 0x1300), b.writeUInt32LE(cas, 0x1304);  // mov [esp+8], imm32
  b[0x1400] = 0x68, b.writeUInt32LE(plain, 0x1401);                  // not a tag path: ignored
  const p = tagPushes(b);
  assert.deepEqual(p.map(x => [x.at, x.tag]), [[0x401100, 'CAS/GeometryResource'], [0x401200, 'Audio/ResourceBinary/Data'], [0x401300, 'CAS/GeometryResource']]);
  assert.equal(ownerOf('ResourceLoad/PackedStream/DecompressedRecordData'), 'res');
  assert.equal(ownerOf('Lot/FloorManager'), 'world');
  assert.equal(ownerOf('Scripting/Simulator'), 'script');
  assert.equal(ownerOf('Nothing/Here'), null);
  const r = ranges([...p, { at: 0x420000, tag: 'Audio/X' }, { at: 0x424000, tag: 'Audio/Y' }, { at: 0x500000, tag: 'Audio/Z' }]);
  assert.deepEqual(r.map(x => [x.own, x.lo, x.hi]), [['cas', 0x400000, 0x404000], ['audio', 0x420000, 0x428000], ['audio', 0x500000, 0x504000]]);
  console.log('alloc-owner-tags self-check ok');
}

if (import.meta.main ?? process.argv[1]?.endsWith('alloc-owner-tags.ts')) {
  const a = process.argv.slice(2);
  if (a[0] === '--self-check') selfCheck();
  else if (!a[0]) { console.error('usage: node tools/alloc-owner-tags.ts <TS3W.exe> [--tags <prefix>] | --self-check'); process.exit(2); }
  else {
    const p = tagPushes(readFileSync(a[0]));
    const ti = a.indexOf('--tags');
    if (ti >= 0) for (const x of p.filter(x => x.tag.startsWith(a[ti + 1] ?? '')).sort((x, y) => x.at - y.at)) console.log(hex(x.at), x.tag);
    else {
      console.log(`tag pushes ${p.length}, owned ${p.filter(x => ownerOf(x.tag)).length}`);
      for (const r of ranges(p)) {
        const tot = Object.values(r.c).reduce((s, v) => s + v, 0), n = r.c[r.own];
        const rest = Object.entries(r.c).filter(([x]) => x !== r.own).sort((x, y) => y[1] - x[1]).slice(0, 2).map(([x, y]) => `${x}:${y}`).join(',');
        console.log(`${r.own.padEnd(8)} ${hex(r.lo)}-${hex(r.hi)} ${n}/${tot} ${Math.round(100 * n / tot)}% ${rest}`);
      }
    }
  }
}
