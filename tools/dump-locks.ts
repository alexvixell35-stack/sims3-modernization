// Which thread waits on which critical section, and who owns it, read from a TS3W.exe kill/hang minidump (x86, full memory).
//   node tools/dump-locks.ts <file.dmp>      one line per waiting thread, then the cycle verdict (exit 0; 3 = dump unreadable)
//   node tools/dump-locks.ts --self-check    synthetic dumps, no game
// A waiting thread is one whose EIP is inside ntdll and one of whose registers (edi first: RtlEnterCriticalSection keeps the
// CS there, docs/539 §13.8) points at a CRITICAL_SECTION owned by another thread of the dump. A cycle of owner edges is a deadlock.
// The EA package-lock ABBA (docs/539 §13.8, docs/67 §23): main holds object+0xC0 and waits for +0x48, a worker holds +0x48 and
// waits for +0xC0, seen from 0x735358 (main) and 0x734725 (worker); the signature needs both CS 0x78 apart and both callers.
// Reads the dump with positioned reads only (dumps are 2 GB); dumps hold personal data and are never committed.
import { closeSync, mkdtempSync, openSync, readSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

export const EA_PACKAGE_LOCK = 'EA package-lock deadlock (docs/539 §13.8)';
const MAIN_CALLER = 0x735358, WORKER_CALLER = 0x734725, CS_GAP = 0x78;

interface Thread { tid: number; stackStart: number; stackSize: number; stackRva: number; ctxRva: number; ctxSize: number }
interface Range { start: number; size: number; rva: number }
export interface Wait { tid: number; cs: number; owner: number; recursion: number; reg: string; stack: Set<number> }
export interface Analysis { waits: Wait[]; cycle: number[] | null; deadlock: string; lines: string[]; summary: string }

class Dump {
  fd: number; threads: Thread[] = []; ranges: Range[] = []; ntdll: [number, number] | null = null; arch = -1;
  file: string;
  constructor(file: string) {
    this.file = file;
    this.fd = openSync(file, 'r');
    const h = this.at(0, 32);
    if (h.toString('latin1', 0, 4) !== 'MDMP') throw new Error('not a minidump (no MDMP signature)');
    const n = h.readUInt32LE(8), dir = h.readUInt32LE(12);
    const d = this.at(dir, 12 * n);
    for (let i = 0; i < n; i++) {
      const type = d.readUInt32LE(i * 12), size = d.readUInt32LE(i * 12 + 4), rva = d.readUInt32LE(i * 12 + 8);
      if (type === 7) this.arch = this.at(rva, 2).readUInt16LE(0);
      else if (type === 3) this.readThreads(rva);
      else if (type === 4) this.readModules(rva);
      else if (type === 9) this.readMemory64(rva);
      else if (type === 5) this.readMemoryList(rva, size);
    }
    this.ranges.sort((a, b) => a.start - b.start);
  }
  at(pos: number, len: number): Buffer {
    const b = Buffer.alloc(len);
    if (readSync(this.fd, b, 0, len, pos) !== len) throw new Error(`short read at ${pos}`);
    return b;
  }
  private readThreads(rva: number) {
    const n = this.at(rva, 4).readUInt32LE(0), b = this.at(rva + 4, 48 * n);
    for (let i = 0; i < n; i++) {
      const o = i * 48;
      this.threads.push({ tid: b.readUInt32LE(o), stackStart: b.readUInt32LE(o + 24), stackSize: b.readUInt32LE(o + 32), stackRva: b.readUInt32LE(o + 36), ctxSize: b.readUInt32LE(o + 40), ctxRva: b.readUInt32LE(o + 44) });
    }
  }
  private readModules(rva: number) {
    const n = this.at(rva, 4).readUInt32LE(0), b = this.at(rva + 4, 108 * n);
    for (let i = 0; i < n; i++) {
      const base = b.readUInt32LE(i * 108), size = b.readUInt32LE(i * 108 + 8), nameRva = b.readUInt32LE(i * 108 + 20);
      const len = this.at(nameRva, 4).readUInt32LE(0), name = this.at(nameRva + 4, Math.min(len, 1024)).toString('utf16le');
      if (/(^|\\)ntdll\.dll$/i.test(name) && (!this.ntdll || base < 0x80000000)) this.ntdll = [base, base + size]; // the 32-bit one (the x64 ntdll lies above 2 GB)
    }
  }
  private readMemory64(rva: number) {
    const h = this.at(rva, 16), n = Number(h.readBigUInt64LE(0)); let off = Number(h.readBigUInt64LE(8));
    const b = this.at(rva + 16, 16 * n);
    for (let i = 0; i < n; i++) {
      const start = Number(b.readBigUInt64LE(i * 16)), size = Number(b.readBigUInt64LE(i * 16 + 8));
      this.ranges.push({ start, size, rva: off }); off += size;
    }
  }
  private readMemoryList(rva: number, _size: number) {
    const n = this.at(rva, 4).readUInt32LE(0), b = this.at(rva + 4, 16 * n);
    for (let i = 0; i < n; i++) this.ranges.push({ start: Number(b.readBigUInt64LE(i * 16)), size: b.readUInt32LE(i * 16 + 8), rva: b.readUInt32LE(i * 16 + 12) });
  }
  // bytes at a virtual address inside one range, or null
  mem(addr: number, len: number): Buffer | null { return this.span(addr, len, true); }
  // up to max bytes from addr to the end of its range (a stack above ESP), or null
  span(addr: number, max: number, exact = false): Buffer | null {
    let lo = 0, hi = this.ranges.length - 1;
    while (lo <= hi) {
      const m = (lo + hi) >> 1, r = this.ranges[m];
      if (addr < r.start) hi = m - 1; else if (addr >= r.start + r.size) lo = m + 1;
      else { const left = r.start + r.size - addr; return exact && max > left ? null : this.at(r.rva + (addr - r.start), Math.min(max, left)); }
    }
    return null;
  }
  close() { closeSync(this.fd); }
}

const REGS: [string, number][] = [['edi', 0x9C], ['esi', 0xA0], ['ebx', 0xA4], ['ecx', 0xAC], ['edx', 0xA8]];
const hex = (n: number) => `0x${n.toString(16).toUpperCase().padStart(8, '0')}`;

// the signature of the EA package-lock ABBA (pure: covered by --self-check)
export function matchEaPackageLock(cycle: number[] | null, waits: Wait[]): string {
  if (!cycle || cycle.length !== 2) return '';
  const [a, b] = cycle.map(t => waits.find(w => w.tid === t)!);
  if (!a || !b || Math.abs(a.cs - b.cs) !== CS_GAP) return '';
  const ok = (m: Wait, w: Wait) => m.stack.has(MAIN_CALLER) && w.stack.has(WORKER_CALLER);
  return ok(a, b) || ok(b, a) ? EA_PACKAGE_LOCK : '';
}

// the thread-to-owner edge walk: the first repeated thread closes a cycle (pure)
export function findCycle(waits: Wait[]): number[] | null {
  const next = new Map(waits.map(w => [w.tid, w.owner]));
  for (const w of waits) {
    const seen: number[] = [];
    for (let t: number | undefined = w.tid; t !== undefined && seen.length <= waits.length; t = next.get(t)) {
      const i = seen.indexOf(t);
      if (i >= 0) return seen.slice(i);
      seen.push(t);
    }
  }
  return null;
}

export function analyzeDump(file: string): Analysis {
  const d = new Dump(file);
  try {
    if (d.arch !== 0) return fail(`dump architecture ${d.arch} (need x86 = 0): lock owners not read`);
    const tids = new Set(d.threads.map(t => t.tid)), waits: Wait[] = [];
    for (const t of d.threads) {
      if (t.ctxSize < 0xC8) continue;
      const c = d.at(t.ctxRva, t.ctxSize), eip = c.readUInt32LE(0xB8);
      if (d.ntdll && !(eip >= d.ntdll[0] && eip < d.ntdll[1])) continue; // not inside a wait
      for (const [reg, off] of REGS) {
        const p = c.readUInt32LE(off), cs = p % 4 === 0 ? d.mem(p, 24) : null;
        if (!cs) continue;
        const lock = cs.readInt32LE(4), rec = cs.readInt32LE(8), owner = cs.readUInt32LE(12);
        if (lock === -1 || rec < 1 || rec > 4096 || owner === t.tid || !tids.has(owner)) continue;
        const sb = d.span(c.readUInt32LE(0xC4), 1 << 20) ?? d.at(t.stackRva, t.stackSize), stack = new Set<number>();
        for (let i = 0; i + 4 <= sb.length; i += 4) stack.add(sb.readUInt32LE(i));
        waits.push({ tid: t.tid, cs: p, owner, recursion: rec, reg, stack });
        break;
      }
    }
    const cycle = findCycle(waits), deadlock = matchEaPackageLock(cycle, waits);
    const lines = waits.map(w => `thread ${w.tid} waits CS ${hex(w.cs)} (via ${w.reg}) owned by thread ${w.owner} (recursion ${w.recursion})`);
    if (cycle) lines.push(`cycle: ${[...cycle, cycle[0]].join(' -> ')}${deadlock ? `: ${deadlock}` : ''}`);
    const summary = `lock waits: ${waits.length} of ${d.threads.length} threads${cycle ? `; deadlock cycle ${[...cycle, cycle[0]].join('>')}` : '; no cycle'}${deadlock ? `; ${deadlock}` : ''}`;
    return { waits, cycle, deadlock, lines, summary };
  } finally { d.close(); }
  function fail(why: string): Analysis { return { waits: [], cycle: null, deadlock: '', lines: [why], summary: why }; }
}

if (process.argv[1]?.endsWith('dump-locks.ts') && !process.argv.includes('--self-check')) {
  const f = process.argv[2];
  if (!f) { console.error('usage: node tools/dump-locks.ts <file.dmp> | --self-check'); process.exit(2); }
  try { const a = analyzeDump(f); console.log([...a.lines, a.summary].join('\n')); } catch (e) { console.error(`dump-locks: ${String(e).split('\n')[0]}`); process.exit(3); }
}

// ---- self-check: synthetic x86 full-memory dumps
interface FakeThread { tid: number; eip: number; edi: number; stack: number[] }
function fakeDump(file: string, threads: FakeThread[], cs: { addr: number; lock: number; rec: number; owner: number }[]) {
  const T = 48 * threads.length + 4, M = 4 + 108, S = 56, G = 16 + 16 * (threads.length + cs.length);
  const dirRva = 32, nStreams = 4, sysRva = dirRva + 12 * nStreams, thrRva = sysRva + S, modRva = thrRva + T, nameRva = modRva + M, memRva = nameRva + 4 + 24;
  const dataRva = memRva + G, ctxSize = 0x2CC;
  const parts: Buffer[] = [], ranges: { start: number; buf: Buffer }[] = [];
  const buf = Buffer.alloc(dataRva); buf.write('MDMP', 0, 'latin1'); buf.writeUInt32LE(nStreams, 8); buf.writeUInt32LE(dirRva, 12);
  [[7, S, sysRva], [3, T, thrRva], [4, M, modRva], [9, G, memRva]].forEach(([t, s, r], i) => { buf.writeUInt32LE(t, dirRva + i * 12); buf.writeUInt32LE(s, dirRva + i * 12 + 4); buf.writeUInt32LE(r, dirRva + i * 12 + 8); });
  buf.writeUInt16LE(0, sysRva); // x86
  buf.writeUInt32LE(threads.length, thrRva);
  buf.writeUInt32LE(1, modRva); buf.writeUInt32LE(0x7000_0000, modRva + 4); buf.writeUInt32LE(0x100000, modRva + 12); buf.writeUInt32LE(nameRva, modRva + 24);
  buf.writeUInt32LE(18, nameRva); buf.write('ntdll.dll', nameRva + 4, 'utf16le');
  let off = dataRva;
  threads.forEach((t, i) => {
    const sb = Buffer.alloc(4 * t.stack.length); t.stack.forEach((v, k) => sb.writeUInt32LE(v, 4 * k));
    const ctx = Buffer.alloc(ctxSize); ctx.writeUInt32LE(t.eip, 0xB8); ctx.writeUInt32LE(t.edi, 0x9C); ctx.writeUInt32LE(0x00100000 * (i + 1), 0xC4);
    const o = thrRva + 4 + 48 * i, start = 0x00100000 * (i + 1);
    buf.writeUInt32LE(t.tid, o); buf.writeUInt32LE(start, o + 24); buf.writeUInt32LE(sb.length, o + 32); buf.writeUInt32LE(off, o + 36);
    buf.writeUInt32LE(ctxSize, o + 40); buf.writeUInt32LE(dataRva + 0, o + 44); // ctx rva patched below
    ranges.push({ start, buf: sb }); parts.push(sb); off += sb.length;
    (t as any).ctx = ctx;
  });
  cs.forEach(c => { const b = Buffer.alloc(24); b.writeInt32LE(c.lock, 4); b.writeInt32LE(c.rec, 8); b.writeUInt32LE(c.owner, 12); ranges.push({ start: c.addr, buf: b }); parts.push(b); off += 24; });
  threads.forEach((t, i) => { buf.writeUInt32LE(off, thrRva + 4 + 48 * i + 44); parts.push((t as any).ctx); off += ctxSize; });
  buf.writeBigUInt64LE(BigInt(ranges.length), memRva); buf.writeBigUInt64LE(BigInt(dataRva), memRva + 8);
  ranges.forEach((r, i) => { buf.writeBigUInt64LE(BigInt(r.start), memRva + 16 + 16 * i); buf.writeBigUInt64LE(BigInt(r.buf.length), memRva + 24 + 16 * i); });
  writeFileSync(file, Buffer.concat([buf, ...parts]));
}

if (process.argv[1]?.endsWith('dump-locks.ts') && process.argv.includes('--self-check')) {
  const assert = (await import('node:assert')).strict, dir = mkdtempSync(join(tmpdir(), 'dump-locks-'));
  try {
    const WAIT = 0x7000_1000, A = 0x0DD8FDA0, B = A - CS_GAP; // A = object+0xC0, B = object+0x48
    const main: FakeThread = { tid: 10, eip: WAIT, edi: B, stack: [1, MAIN_CALLER, 2] }, worker: FakeThread = { tid: 20, eip: WAIT, edi: A, stack: [WORKER_CALLER, 3] };
    const sim: FakeThread = { tid: 30, eip: WAIT, edi: 0x0DC73F48, stack: [4] }, idle: FakeThread = { tid: 40, eip: 0x00401000, edi: B, stack: [5] };
    const locks = [{ addr: A, lock: -2, rec: 2, owner: 10 }, { addr: B, lock: -4, rec: 1, owner: 20 }, { addr: 0x0DC73F48, lock: -2, rec: 1, owner: 20 }];
    const f1 = join(dir, 'abba.dmp'); fakeDump(f1, [main, worker, sim, idle], locks);
    const a = analyzeDump(f1);
    assert.deepEqual(a.waits.map(w => [w.tid, w.owner]), [[10, 20], [20, 10], [30, 20]]); // idle (eip outside ntdll) is no waiter
    assert.deepEqual(a.cycle, [10, 20]);
    assert.equal(a.deadlock, EA_PACKAGE_LOCK);
    assert.match(a.lines[0], /thread 10 waits CS 0x0DD8FD28 \(via edi\) owned by thread 20/);
    assert.match(a.summary, /deadlock cycle 10>20>10; EA package-lock deadlock/);
    // same cycle, other callers: a deadlock, not the EA signature
    const f2 = join(dir, 'other.dmp'); fakeDump(f2, [{ ...main, stack: [1] }, worker], locks);
    const o = analyzeDump(f2); assert.deepEqual(o.cycle, [10, 20]); assert.equal(o.deadlock, '');
    // no cycle: a chain
    const f3 = join(dir, 'chain.dmp'); fakeDump(f3, [sim, { ...worker, edi: 0 }], locks);
    const c = analyzeDump(f3); assert.equal(c.cycle, null); assert.match(c.summary, /no cycle/);
    writeFileSync(join(dir, 'bad.dmp'), 'not a dump at all, long enough for a header read.....');
    assert.throws(() => analyzeDump(join(dir, 'bad.dmp')), /not a minidump/);
    assert.deepEqual(findCycle([{ tid: 1, owner: 2 }, { tid: 2, owner: 3 }, { tid: 3, owner: 1 }] as Wait[]), [1, 2, 3]);
    console.log('dump-locks self-check: ok');
  } finally { rmSync(dir, { recursive: true, force: true }); }
}
