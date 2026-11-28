// Reference-only copy of a .NET assembly with named nested private types made nested public, so csc lets ts3fixes
// implement EA's private `Lot.IObjectCache` (docs/274 §11). Only the TypeDef visibility bits change (ECMA-335 II.22.37);
// the copy is a compile reference in build/ (git-ignored), never shipped: the game loads its own DLL, and its Mono does
// not check type visibility [community-reported: core mods compile against "unprotected" DLLs; docs/51 §518].
//   node tools/setup/publicize.ts <in.dll> <out.dll> <TypeName>...
//   node tools/setup/publicize.ts --self-check     (Windows .NET Framework csc: fixture compiled, patched, consumed)
import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';

// each name must match exactly one TypeDef, and that one must be nested private (3); returns the patched bytes
export function publicize(dll: Buffer, names: string[]): Buffer {
  const b = Buffer.from(dll);
  const pe = b.readUInt32LE(0x3c), opt = pe + 24, nSec = b.readUInt16LE(pe + 6);
  const pe32plus = b.readUInt16LE(opt) === 0x20b, dirs = opt + (pe32plus ? 112 : 96);
  const sec = opt + b.readUInt16LE(pe + 20);
  const off = (rva: number) => {
    for (let i = 0; i < nSec; i++) {
      const s = sec + i * 40, va = b.readUInt32LE(s + 12), size = Math.max(b.readUInt32LE(s + 8), b.readUInt32LE(s + 16));
      if (rva >= va && rva < va + size) return rva - va + b.readUInt32LE(s + 20);
    }
    throw new Error(`rva 0x${rva.toString(16)} in no section`);
  };
  const cli = off(b.readUInt32LE(dirs + 14 * 8));
  if (!b.readUInt32LE(dirs + 14 * 8)) throw new Error('not a .NET assembly (no CLI header)');
  const md = off(b.readUInt32LE(cli + 8));
  if (b.readUInt32LE(md) !== 0x424a5342) throw new Error('no BSJB metadata root');
  let p = md + 16 + b.readUInt32LE(md + 12);
  const nStreams = b.readUInt16LE(p + 2);
  p += 4;
  let tables = -1, strings = -1;
  for (let i = 0; i < nStreams; i++) {
    const o = b.readUInt32LE(p);
    let e = p + 8;
    while (b[e]) e++;
    const name = b.toString('latin1', p + 8, e);
    if (name === '#~') tables = md + o;
    if (name === '#Strings') strings = md + o;
    p = (e + 4) & ~3;
  }
  if (tables < 0 || strings < 0) throw new Error('no #~ or #Strings stream (unoptimized #- metadata is not handled)');
  const heap = b[tables + 6], valid = b.readBigUInt64LE(tables + 8);
  const rows: number[] = [];
  let q = tables + 24;
  for (let t = 0; t < 64; t++) rows[t] = valid >> BigInt(t) & 1n ? (q += 4, b.readUInt32LE(q - 4)) : 0;
  const str = heap & 1 ? 4 : 2, guid = heap & 2 ? 4 : 2;
  const idx = (t: number) => rows[t] > 0xffff ? 4 : 2;
  const coded = (bits: number, ts: number[]) => Math.max(...ts.map(t => rows[t])) >= 1 << (16 - bits) ? 4 : 2;
  const moduleRow = 2 + str + 3 * guid;
  const typeRefRow = coded(2, [0x00, 0x1a, 0x23, 0x01]) + 2 * str;
  const typeDefRow = 4 + 2 * str + coded(2, [0x02, 0x01, 0x1b]) + idx(0x04) + idx(0x06);
  const typeDef = q + rows[0] * moduleRow + rows[1] * typeRefRow;
  const nameAt = (o: number) => { let e = strings + o; while (b[e]) e++; return b.toString('utf8', strings + o, e); };
  for (const want of names) {
    const hits: number[] = [];
    for (let i = 0; i < rows[2]; i++) {
      const r = typeDef + i * typeDefRow;
      if (nameAt(str === 4 ? b.readUInt32LE(r + 4) : b.readUInt16LE(r + 4)) === want) hits.push(r);
    }
    if (hits.length !== 1) throw new Error(`${want}: ${hits.length} TypeDef rows, want exactly 1`);
    const flags = b.readUInt32LE(hits[0]);
    if ((flags & 7) !== 3) throw new Error(`${want}: visibility ${flags & 7}, want 3 (nested private)`);
    b.writeUInt32LE((flags & ~7 | 2) >>> 0, hits[0]);
  }
  return b;
}

if (process.argv[1] && resolve(process.argv[1]) === resolve(import.meta.filename)) {
  if (process.argv[2] === '--self-check') {
    const CSC = process.env.TS3_CSC ?? 'C:/Windows/Microsoft.NET/Framework/v4.0.30319/csc.exe';
    if (!existsSync(CSC)) { console.log('skip: no csc'); process.exit(0); }
    const dir = join(resolve(import.meta.dirname, '..', '..'), 'build', 'selftest', 'publicize');
    mkdirSync(dir, { recursive: true });
    const w = (n: string, s: string) => (writeFileSync(join(dir, n), s), n);
    const csc = (args: string[]) => execFileSync(CSC, ['/nologo', '/target:library', ...args], { cwd: dir, stdio: 'pipe' });
    csc(['/out:Fix.dll', w('Fix.cs', 'namespace N { public class Lot { private interface IObjectCache { int Version { get; } } private class Other { } public interface IOpen { } } public class IObjectCacheUser { } }')]);
    const user = w('User.cs', 'class C : N.Lot.IObjectCache { public int Version { get { return 1; } } }');
    let refused = false;
    try { csc(['/out:User0.dll', '/reference:Fix.dll', user]); } catch { refused = true; }
    console.assert(refused, 'csc must refuse the private interface before the patch');
    const fix = readFileSync(join(dir, 'Fix.dll'));
    const out = publicize(fix, ['IObjectCache']);
    console.assert(out.length === fix.length && fix.compare(out) !== 0, 'same size, flags changed');
    let diff = 0;
    for (let i = 0; i < fix.length; i++) if (fix[i] !== out[i]) diff++;
    console.assert(diff === 1, `one byte differs (the visibility bits), got ${diff}`);
    writeFileSync(join(dir, 'FixPub.dll'), out);
    csc(['/out:User1.dll', '/reference:FixPub.dll', user]);  // throws if csc still refuses
    for (const [bad, why] of [['Nope', 'no such type'], ['IOpen', 'not nested private'], ['Lot', 'not nested private']]) {
      let threw = false;
      try { publicize(fix, [bad]); } catch { threw = true; }
      console.assert(threw, `${bad} must throw (${why})`);
    }
    console.log('publicize self-check OK');
  } else {
    const [src, dst, ...names] = process.argv.slice(2);
    if (!src || !dst || !names.length) { console.error('usage: node tools/setup/publicize.ts <in.dll> <out.dll> <TypeName>...'); process.exit(1); }
    mkdirSync(dirname(resolve(dst)), { recursive: true });
    writeFileSync(dst, publicize(readFileSync(src), names));
    console.log(`${dst}: ${names.join(', ')} nested public (reference only)`);
  }
}
