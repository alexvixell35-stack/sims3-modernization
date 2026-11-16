// Builds the ModTheSims upload zip (release/mts/package.md) from the release artifacts. Allow-list only: the zip holds
// exactly these four files and nothing else; inputs are checked by name and content (no ts3agent, no test packages).
//   node tools/mts-zip.ts                         defaults: build/ts3fixes.package, native/ts3native/out/ts3native.{asi,ini},
//                                                 release/mts/README.txt -> release/mts/out/Sims3Modernization_v0.1.5.zip
//   node tools/mts-zip.ts --package <p> --asi <p> --ini <p> [--readme <p>] [--version 0.1.5] [--out <dir>]
//   node tools/mts-zip.ts --final                 also require the sha256 of the three files to match the
//                                                 docs/RELEASE.md "v<version> release build" table (no [to fill] left)
//   node tools/mts-zip.ts --self-check            offline checks with synthetic files (temp dir), writes nothing here
// Deterministic: sorted entries, fixed timestamp (release day), deflate level 9, no extra fields -> same input, same bytes.
import { readFileSync, writeFileSync, mkdirSync, mkdtempSync, rmSync } from 'node:fs';
import { join, resolve, basename } from 'node:path';
import { tmpdir } from 'node:os';
import { createHash } from 'node:crypto';
import { deflateRawSync, inflateRawSync, crc32 } from 'node:zlib';
import { parseArgs } from 'node:util';

const ROOT = resolve(import.meta.dirname, '..');
const DOS_DATE = { y: 2026, m: 10, d: 23 }; // release day; the time of day is 00:00

type Kind = 'package' | 'asi' | 'ini' | 'readme';
// zip path -> [input kind, required input basename]
export const ALLOW: Record<string, [Kind, string]> = {
  'Game/Bin/ts3native.asi': ['asi', 'ts3native.asi'],
  'Game/Bin/ts3native.ini': ['ini', 'ts3native.ini'],
  'Mods/Packages/ts3fixes.package': ['package', 'ts3fixes.package'],
  'README.txt': ['readme', 'README.txt'],
};
const SHORTCUTS = ['GoHome', 'BuildingTrip', 'LotTrip', 'VirtualHome'];

// Throws with a reason when a file must not ship; returns the bytes to store.
export function vet(kind: Kind, name: string, buf: Buffer, version: string): Buffer {
  const want = Object.values(ALLOW).find(([k]) => k === kind)![1];
  if (name !== want) throw new Error(`${kind}: input must be named ${want}, got ${name} (allow-list)`);
  const s = buf.toString('latin1');
  if (kind === 'package') {
    if (s.slice(0, 4) !== 'DBPF') throw new Error('package: not a DBPF package');
    if (!s.includes('Ts3Fixes')) throw new Error('package: no Ts3Fixes assembly');
    for (const bad of ['Ts3Agent', 'PortalPlanTest'] /* the test mod's assembly Ts3PortalPlanTest; ts3fixes itself uses the game's PortalPlanEventArgs */) if (s.includes(bad)) throw new Error(`package: contains ${bad} (test-only code)`);
    for (const k of SHORTCUTS) if (!s.includes(`<k${k}Shortcut value="True"`)) throw new Error(`package: k${k}Shortcut is not True (docs/RELEASE.md: the release never ships them off)`);
    return buf;
  }
  if (kind === 'asi') {
    if (s.slice(0, 2) !== 'MZ') throw new Error('asi: not a PE file');
    const pe = buf.readUInt32LE(0x3c);
    if (buf.toString('latin1', pe, pe + 4) !== 'PE\0\0' || buf.readUInt16LE(pe + 4) !== 0x14c) throw new Error('asi: not a 32-bit (x86) PE');
    if (!s.includes('ts3native')) throw new Error('asi: no ts3native marker');
    return buf;
  }
  if (buf.includes(0)) throw new Error(`${kind}: binary data in a text file`);
  if (kind === 'ini') {
    if (!/^\[RouteAsync\]/m.test(s) || !/^\[GC\]/m.test(s)) throw new Error('ini: not a ts3native.ini (no [GC] / [RouteAsync])');
    return buf;
  }
  const txt = buf.toString('utf8');
  if (!txt.includes('{VERSION}')) throw new Error('readme: template has no {VERSION}');
  return Buffer.from(txt.replaceAll('{VERSION}', version).replace(/\r?\n/g, '\r\n'), 'utf8'); // CRLF for Notepad
}

export function zip(entries: [string, Buffer][]): Buffer {
  const time = 0, date = ((DOS_DATE.y - 1980) << 9) | (DOS_DATE.m << 5) | DOS_DATE.d;
  const locals: Buffer[] = [], centrals: Buffer[] = [];
  let off = 0;
  for (const [name, data] of [...entries].sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0))) {
    const n = Buffer.from(name, 'utf8'), comp = deflateRawSync(data, { level: 9 }), crc = crc32(data);
    const lh = Buffer.alloc(30);
    lh.writeUInt32LE(0x04034b50, 0); lh.writeUInt16LE(20, 4); lh.writeUInt16LE(0x0800, 6); lh.writeUInt16LE(8, 8);
    lh.writeUInt16LE(time, 10); lh.writeUInt16LE(date, 12); lh.writeUInt32LE(crc, 14);
    lh.writeUInt32LE(comp.length, 18); lh.writeUInt32LE(data.length, 22); lh.writeUInt16LE(n.length, 26);
    const ch = Buffer.alloc(46);
    ch.writeUInt32LE(0x02014b50, 0); ch.writeUInt16LE(20, 4); ch.writeUInt16LE(20, 6); ch.writeUInt16LE(0x0800, 8); ch.writeUInt16LE(8, 10);
    ch.writeUInt16LE(time, 12); ch.writeUInt16LE(date, 14); ch.writeUInt32LE(crc, 16);
    ch.writeUInt32LE(comp.length, 20); ch.writeUInt32LE(data.length, 24); ch.writeUInt16LE(n.length, 28); ch.writeUInt32LE(off, 42);
    locals.push(lh, n, comp); centrals.push(ch, n);
    off += 30 + n.length + comp.length;
  }
  const cd = Buffer.concat(centrals), end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0); end.writeUInt16LE(entries.length, 8); end.writeUInt16LE(entries.length, 10);
  end.writeUInt32LE(cd.length, 12); end.writeUInt32LE(off, 16);
  return Buffer.concat([...locals, cd, end]);
}

// Reads our own zips back (central directory -> local data), checks CRC; used by the self-check and after each build.
export function unzip(z: Buffer): Map<string, Buffer> {
  const e = z.lastIndexOf(Buffer.from([0x50, 0x4b, 0x05, 0x06]));
  if (e < 0) throw new Error('unzip: no end record');
  const count = z.readUInt16LE(e + 10);
  let p = z.readUInt32LE(e + 16);
  const out = new Map<string, Buffer>();
  for (let i = 0; i < count; i++) {
    if (z.readUInt32LE(p) !== 0x02014b50) throw new Error('unzip: bad central header');
    const crc = z.readUInt32LE(p + 16), csize = z.readUInt32LE(p + 20), nlen = z.readUInt16LE(p + 28), xlen = z.readUInt16LE(p + 30), clen = z.readUInt16LE(p + 32);
    const name = z.toString('utf8', p + 46, p + 46 + nlen), lo = z.readUInt32LE(p + 42);
    const ldata = lo + 30 + z.readUInt16LE(lo + 26) + z.readUInt16LE(lo + 28);
    const raw = z.subarray(ldata, ldata + csize), data = z.readUInt16LE(p + 10) === 0 ? Buffer.from(raw) : inflateRawSync(raw); // 0 = stored
    if (crc32(data) !== crc) throw new Error(`unzip: CRC mismatch in ${name}`);
    out.set(name, data);
    p += 46 + nlen + xlen + clen;
  }
  return out;
}

const sha256 = (b: Buffer) => createHash('sha256').update(b).digest('hex');

// sha256 per file name from the docs/RELEASE.md "v<version> release build" table; null while it says [to fill].
export function releaseHashes(md: string, version: string): Record<string, string | null> {
  const i = md.indexOf(`### v${version} release build`);
  if (i < 0) throw new Error(`docs/RELEASE.md has no "### v${version} release build" table`);
  const table = md.slice(i).split(/\n(?=#)/)[0];
  const out: Record<string, string | null> = {};
  for (const name of ['ts3native.asi', 'ts3native.ini', 'ts3fixes.package']) {
    const row = table.split('\n').find((l) => l.startsWith('|') && l.includes(name));
    const h = row && /`([0-9a-f]{64})`/.exec(row);
    out[name] = h ? h[1] : null;
  }
  return out;
}

export function build(inputs: Record<Kind, string>, version: string): { zip: Buffer; files: [string, Buffer][] } {
  const files: [string, Buffer][] = Object.entries(ALLOW).map(([zp, [kind]]) => [zp, vet(kind, basename(inputs[kind]), readFileSync(inputs[kind]), version)]);
  const z = zip(files);
  const back = unzip(z);
  if (back.size !== files.length || files.some(([n, b]) => !back.get(n)?.equals(b))) throw new Error('zip read-back mismatch');
  if ([...back.keys()].some((n) => !(n in ALLOW))) throw new Error('zip holds a file outside the allow-list');
  return { zip: z, files };
}

function selfCheck() {
  const dir = mkdtempSync(join(tmpdir(), 'mts-zip-'));
  try {
    const w = (name: string, b: Buffer | string) => { const p = join(dir, name); writeFileSync(p, b); return p; };
    const pe = Buffer.alloc(0x200); pe.write('MZ', 0, 'latin1'); pe.writeUInt32LE(0x80, 0x3c); pe.write('PE\0\0', 0x80, 'latin1'); pe.writeUInt16LE(0x14c, 0x84); pe.write('ts3native 0.0.0', 0x100, 'latin1');
    const pkg = 'DBPF....Ts3Fixes' + SHORTCUTS.map((k) => `<k${k}Shortcut value="True" />`).join('');
    const inputs: Record<Kind, string> = { package: w('ts3fixes.package', pkg), asi: w('ts3native.asi', pe), ini: w('ts3native.ini', '[GC]\nEnabled=1\n[RouteAsync]\nEnabled=1\n'), readme: w('README.txt', 'v{VERSION}\nline\n') };
    const a = build(inputs, '9.9.9'), b = build(inputs, '9.9.9');
    if (!a.zip.equals(b.zip)) throw new Error('self-check: not deterministic');
    const back = unzip(a.zip);
    if ([...back.keys()].join() !== Object.keys(ALLOW).sort().join()) throw new Error('self-check: entry list');
    if (back.get('README.txt')!.toString() !== 'v9.9.9\r\nline\r\n') throw new Error('self-check: readme version/CRLF');
    const refuse = (what: string, f: () => unknown) => { try { f(); } catch { return; } throw new Error(`self-check: accepted ${what}`); };
    refuse('ts3agent.package', () => vet('package', 'ts3agent.package', Buffer.from(pkg), '1'));
    refuse('package with Ts3Agent', () => vet('package', 'ts3fixes.package', Buffer.from(pkg + 'Ts3Agent'), '1'));
    refuse('package with PortalPlanTest', () => vet('package', 'ts3fixes.package', Buffer.from(pkg + 'PortalPlanTest'), '1'));
    refuse('shortcut off', () => vet('package', 'ts3fixes.package', Buffer.from(pkg.replace('GoHomeShortcut value="True"', 'GoHomeShortcut value="False"')), '1'));
    const pe64 = Buffer.from(pe); pe64.writeUInt16LE(0x8664, 0x84);
    refuse('64-bit asi', () => vet('asi', 'ts3native.asi', pe64, '1'));
    refuse('other asi name', () => vet('asi', 'ts3agent.asi', pe, '1'));
    refuse('binary ini', () => vet('ini', 'ts3native.ini', Buffer.from('[GC]\n[RouteAsync]\n\0'), '1'));
    const md = '### v9.9.9 release build\n\n| File | V | sha256 |\n|---|---|---|\n| `x/ts3native.asi` | a | `' + 'a'.repeat(64) + '` |\n| `x/ts3native.ini` | b | `[to fill]` |\n\n## Next\n| `ts3fixes.package` | `' + 'b'.repeat(64) + '` |\n';
    const h = releaseHashes(md, '9.9.9');
    if (h['ts3native.asi'] !== 'a'.repeat(64) || h['ts3native.ini'] !== null || h['ts3fixes.package'] !== null) throw new Error('self-check: releaseHashes ' + JSON.stringify(h));
    console.log('mts-zip self-check ok');
  } finally { rmSync(dir, { recursive: true, force: true }); }
}

// only as a script: tools/publish-public.ts imports unzip/zip
if (import.meta.filename === resolve(process.argv[1] ?? '')) {
const { values: o } = parseArgs({ options: {
  package: { type: 'string', default: join(ROOT, 'build', 'ts3fixes.package') },
  asi: { type: 'string', default: join(ROOT, 'native', 'ts3native', 'out', 'ts3native.asi') },
  ini: { type: 'string', default: join(ROOT, 'native', 'ts3native', 'out', 'ts3native.ini') },
  readme: { type: 'string', default: join(ROOT, 'release', 'mts', 'README.txt') },
  version: { type: 'string', default: '0.1.5' },
  out: { type: 'string', default: join(ROOT, 'release', 'mts', 'out') },
  final: { type: 'boolean' }, 'self-check': { type: 'boolean' },
} });

if (o['self-check']) selfCheck();
else {
  const { zip: z, files } = build({ package: o.package!, asi: o.asi!, ini: o.ini!, readme: o.readme! }, o.version!);
  const want = releaseHashes(readFileSync(join(ROOT, 'docs', 'RELEASE.md'), 'utf8'), o.version!);
  let bad = 0;
  for (const [zp, data] of files) {
    const name = basename(zp), h = sha256(data), exp = want[name];
    const note = exp === undefined ? '' : exp === null ? '  (RELEASE.md: [to fill])' : exp === h ? '  = RELEASE.md' : `  MISMATCH: RELEASE.md has ${exp}`;
    if (exp !== undefined && exp !== h) bad++;
    console.log(`${zp.padEnd(32)} ${String(data.length).padStart(9)} B  ${h}${note}`);
  }
  mkdirSync(o.out!, { recursive: true });
  const outFile = join(o.out!, `Sims3Modernization_v${o.version}.zip`);
  writeFileSync(outFile, z);
  console.log(`wrote ${outFile} (${z.length} B, sha256 ${sha256(z)})`);
  if (o.final && bad) { console.error(`--final: ${bad} file(s) not matching docs/RELEASE.md`); process.exit(1); }
}
}
