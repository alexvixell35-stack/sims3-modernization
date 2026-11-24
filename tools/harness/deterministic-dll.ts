// Makes a csc-built .NET DLL reproducible. The .NET Framework 4 csc.exe (C# 5, the only compiler every machine has) has no
// /deterministic: every compile differs in the PE TimeDateStamp, the module MVID and the random GUID in the type name
// `<PrivateImplementationDetails>{GUID}`, so the ts3agent package hash changed on every rebuild and wrote noise into
// docs/data/change-ledger.jsonl (Lenovo 2026-11-23). Same code -> same bytes: those three fields are zeroed, a hash of the
// rest is taken, and a GUID derived from it goes back into the MVID and the type name (unique per content, as before).
//   node tools/harness/deterministic-dll.ts --self-check
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';

const PID_RE = /<PrivateImplementationDetails>\{[0-9A-Fa-f]{8}(?:-[0-9A-Fa-f]{4}){3}-[0-9A-Fa-f]{12}\}/g;
const ZERO_GUID = '00000000-0000-0000-0000-000000000000';

// file offset of the #GUID stream's first GUID (the MVID), or -1
function mvidOffset(b: Buffer): number {
  const m = b.indexOf(Buffer.from('BSJB', 'latin1'));
  if (m < 0) return -1;
  const vlen = b.readUInt32LE(m + 12), n = b.readUInt16LE(m + 16 + vlen + 2);
  let o = m + 16 + vlen + 4;
  for (let i = 0; i < n; i++) {
    const off = b.readUInt32LE(o), size = b.readUInt32LE(o + 4);
    let e = o + 8; while (b[e] !== 0) e++;
    if (b.toString('latin1', o + 8, e) === '#GUID') return size >= 16 ? m + off : -1;
    o += 8 + ((e - (o + 8) + 1 + 3) & ~3); // the name is NUL-terminated and padded to 4 bytes
  }
  return -1;
}

export function deterministicDll(dll: Buffer): Buffer {
  const b = Buffer.from(dll);
  const pe = b.length > 0x40 ? b.readUInt32LE(0x3c) : 0;
  if (pe < 4 || pe + 12 > b.length || b.toString('latin1', pe, pe + 4) !== 'PE\0\0') throw new Error('not a PE file');
  b.writeUInt32LE(0, pe + 8); // TimeDateStamp
  const mv = mvidOffset(b); if (mv >= 0) b.fill(0, mv, mv + 16);
  const z = Buffer.from(b.toString('latin1').replace(PID_RE, `<PrivateImplementationDetails>{${ZERO_GUID}}`), 'latin1');
  const g = Buffer.from(createHash('sha256').update(z).digest().subarray(0, 16));
  g[7] = (g[7] & 0x0f) | 0x40; g[8] = (g[8] & 0x3f) | 0x80; // version-4 shape
  const h = g.toString('hex'), text = `${h.slice(0, 8)}-${h.slice(8, 12)}-${h.slice(12, 16)}-${h.slice(16, 20)}-${h.slice(20)}`.toUpperCase();
  const out = Buffer.from(z.toString('latin1').split(ZERO_GUID).join(text), 'latin1');
  if (mv >= 0) g.copy(out, mv);
  return out;
}

if (process.argv[1] === fileURLToPath(import.meta.url) && process.argv.includes('--self-check')) {
  const assert = (await import('node:assert')).strict;
  // a minimal fake DLL: DOS header, PE signature + timestamp, a BSJB metadata root with a #GUID stream, the type name
  const fake = (ts: number, mvid: string, guid: string, extra = '') => {
    const b = Buffer.alloc(512, 0x20); b.write('MZ', 0, 'latin1'); b.writeUInt32LE(128, 0x3c); b.write('PE\0\0', 128, 'latin1'); b.writeUInt32LE(ts, 136);
    b.write('BSJB', 200, 'latin1'); b.writeUInt32LE(4, 212); b.write('v4\0\0', 216, 'latin1'); b.writeUInt16LE(0, 220); b.writeUInt16LE(1, 222);
    b.writeUInt32LE(100, 224); b.writeUInt32LE(16, 228); b.write('#GUID\0\0\0', 232, 'latin1'); // the stream at 200 + 100
    Buffer.from(mvid, 'hex').copy(b, 300); b.write(`<PrivateImplementationDetails>{${guid}}${extra}`, 340, 'latin1'); return b;
  };
  const a = deterministicDll(fake(111, '00112233445566778899aabbccddeeff', 'A082CCB6-EB7A-4C55-BFA3-6368624001E2'));
  const c = deterministicDll(fake(222, 'ffeeddccbbaa99887766554433221100', '1076BE53-9688-4032-AC46-9ABBA9876F8D'));
  assert.ok(a.equals(c), 'timestamp, MVID and type-name GUID differences vanish');
  assert.equal(a.readUInt32LE(136), 0); assert.ok(!a.subarray(300, 316).equals(Buffer.alloc(16)), 'MVID is derived, not zero');
  assert.match(a.toString('latin1', 340, 340 + 30 + 38), /^<PrivateImplementationDetails>\{[0-9A-F-]{36}\}/);
  assert.ok(!a.equals(deterministicDll(fake(111, '00112233445566778899aabbccddeeff', 'A082CCB6-EB7A-4C55-BFA3-6368624001E2', 'X'))), 'other content -> other bytes');
  assert.throws(() => deterministicDll(Buffer.alloc(300)), /not a PE/);
  console.log('deterministic-dll self-check: OK');
}
