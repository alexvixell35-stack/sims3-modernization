// Pointer-chain reads for repro.ts "reads" (Atlas docs/040 §8: the docs/360 O2 terrain probe line at each step), read-only.
// A read is { name, chain: [start, off1, ..., offN], bytes?: 1 | 2 | 4 (default 4) }: addr = start; for each offset
// addr = u32 at addr, plus the offset; the value is `bytes` little-endian bytes at the final addr. A zero pointer on the
// way gives "null", a failed read "?". Example [[[0x11ECBC4] + 0x54] + 0x08] = ["0x11ECBC4", "0x54", "0x8"].
// Usage: node tools/harness/read-chain.ts --self-test
import assert from 'node:assert/strict';

export type Read = { name: string; chain: (string | number)[]; bytes?: 1 | 2 | 4 };
export type Reader = (addr: number, len: number) => Promise<Buffer | null>;

export async function readChain(rd: Reader, r: Read): Promise<string> {
  const num = (v: string | number) => (typeof v === 'number' ? v : Number(v));
  let addr = num(r.chain[0]);
  for (const off of r.chain.slice(1)) {
    const b = await rd(addr, 4);
    if (!b) return '?';
    const p = b.readUInt32LE(0);
    if (!p) return 'null';
    addr = (p + num(off)) >>> 0;
  }
  const n = r.bytes ?? 4, b = await rd(addr, n);
  if (!b) return '?';
  const v = n === 1 ? b[0] : n === 2 ? b.readUInt16LE(0) : b.readUInt32LE(0);
  return n === 4 ? `0x${v.toString(16).toUpperCase()}` : String(v);
}

export async function readLine(rd: Reader, reads: Read[]): Promise<string> {
  const out: string[] = [];
  for (const r of reads) out.push(`${r.name}=${await readChain(rd, r)}`);
  return out.join(' ');
}

// The ts3probe /read endpoint (hex of len bytes) as a Reader.
export const probeReader = (base = 'http://127.0.0.1:47891'): Reader => async (addr, len) => {
  try {
    const j: any = await (await fetch(`${base}/read?addr=0x${addr.toString(16)}&len=${len}`, { signal: AbortSignal.timeout(20000) })).json();
    return typeof j?.hex === 'string' && j.hex.length === len * 2 ? Buffer.from(j.hex, 'hex') : null;
  } catch { return null; }
};

async function selfTest() {
  const mem = new Map<number, number>([[0x1000, 0x2000], [0x2054, 0x3000], [0x3008, 0xABCD1234], [0x301C, 0x0102], [0x2058, 0]]);
  const rd: Reader = async (a, n) => {
    if (a === 0xDEAD) return null;
    const b = Buffer.alloc(4); b.writeUInt32LE(mem.get(a) ?? 0, 0); return b.subarray(0, n);
  };
  assert.equal(await readChain(rd, { name: 'w', chain: ['0x1000'] }), '0x2000', 'no offset: the value at start');
  assert.equal(await readChain(rd, { name: 'hm', chain: ['0x1000', '0x54', '0x8'] }), '0xABCD1234', 'two hops');
  assert.equal(await readChain(rd, { name: 'nl', chain: ['0x1000', '0x54', '0x1C'], bytes: 1 }), '2', 'one byte');
  assert.equal(await readChain(rd, { name: 'w16', chain: ['0x1000', '0x54', '0x1C'], bytes: 2 }), '258', 'two bytes');
  assert.equal(await readChain(rd, { name: 'opt', chain: ['0x1000', '0x58', '0x68'] }), 'null', 'zero pointer on the way');
  assert.equal(await readChain(rd, { name: 'bad', chain: [0xDEAD, 4] }), '?', 'failed read');
  assert.equal(await readLine(rd, [{ name: 'a', chain: ['0x1000'] }, { name: 'b', chain: ['0x1000', '0x58', '0x68'] }]), 'a=0x2000 b=null');
  console.log('read-chain self-test OK');
}

if (process.argv[1]?.endsWith('read-chain.ts') && process.argv.includes('--self-test')) await selfTest();
