// Rewind feasibility census (docs/270 §3; design input: Dalexlol's rewind notes 2026-11-14). Read-only snapshot of a running
// process and a diff of two snapshots, to judge whether an in-process rewind (threads + handles stay, memory goes back) is realistic.
//   node tools/harness/rewind-census.ts snap --name TS3W.exe [--out F] [--no-hash]   snapshot (default out: probe-out/rewind/<name>-<time>.json)
//   node tools/harness/rewind-census.ts snap --pid N ...
//   node tools/harness/rewind-census.ts diff A.json B.json [--json]                  churn between two snapshots
//   node tools/harness/rewind-census.ts watch --log <queue .out> [--tag T] [--start-s 130] [--end-s 560] [--names TS3W.exe,NvRemixBridge.exe]
//       waits for the next job's `ready: {"in_world":true` line in the log, snaps every running named process at ready+start-s
//       and ready+end-s (inside the 10 min profile window), writes the diffs to probe-out/rewind/<tag>-<name>-diff.json, exits
//   node tools/harness/rewind-census.ts --self-check                                 toy 32-bit process (SysWOW64 ping), no game
// Rights: PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, THREAD_QUERY_INFORMATION, Toolhelp, VirtualQueryEx, ReadProcessMemory,
// NtQuerySystemInformation (tools/harness/rewind/RewindCensus.cs). Nothing suspends, writes, injects or debugs. Windows only.
// Snapshot: thread [tid, Win32 start address, creation time], handle [value, type name, access] (per type, from
// SystemExtendedHandleInformation + ObjectTypesInformation), region [base, size, allocation base, C/R, protect, P/M/I, file],
// totals, and per-4 KB-page FNV hashes of committed writable pages (bytes changed = pages whose hash differs).
import assert from 'node:assert/strict';
import { execFileSync, spawn } from 'node:child_process';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..');
const CS = resolve(import.meta.dirname, 'rewind', 'RewindCensus.cs');
const MB = 1024 * 1024;

export type Region = [string, number, string, 'C' | 'R', number, 'P' | 'M' | 'I', string | null];
export type Snap = {
  pid: number; wow64: boolean; utc: string; handleCount: number; handlesOk: boolean; ms: number;
  totals: { private: number; private32: number; image: number; mapped: number; writable: number; regions: number; hashedPages: number; unreadPages: number };
  handles: [number, string, number][]; threads: [number, string, number][]; regions: Region[]; hashes: Record<string, string>;
};

const shell = () => { try { execFileSync('pwsh', ['-NoProfile', '-Command', 'exit 0'], { stdio: 'ignore', timeout: 30000 }); return 'pwsh'; } catch { return 'powershell'; } };

export function pidOf(name: string): number {
  const out = execFileSync('tasklist', ['/FI', `IMAGENAME eq ${name}`, '/FO', 'CSV', '/NH'], { encoding: 'utf8', timeout: 30000 });
  const m = out.match(/^"[^"]+","(\d+)"/m);
  if (!m) throw new Error(`${name} not running`);
  return +m[1];
}

export function snap(pid: number, out: string, hash = true): Snap {
  const ps = `Add-Type -Path '${CS.replace(/'/g, "''")}'; [void][RewindCensus]::Snap(${pid}, $${hash}, '${out.replace(/'/g, "''")}')`;
  execFileSync(shell(), ['-NoProfile', '-NonInteractive', '-Command', ps], { stdio: ['ignore', 'ignore', 'pipe'], timeout: 300000, windowsHide: true });
  return JSON.parse(readFileSync(out, 'utf8'));
}

const big = (h: string) => BigInt(h);
const count = (xs: string[]) => xs.reduce((m, x) => (m[x] = (m[x] ?? 0) + 1, m), {} as Record<string, number>);

// module (or "private") holding a thread start address, from the region map
function owner(s: Snap, addr: string): string {
  const a = big(addr);
  if (a === 0n) return '?';
  const r = s.regions.find(r => big(r[0]) <= a && a < big(r[0]) + BigInt(r[1]));
  return r ? (r[6] ?? (r[5] === 'P' ? 'private' : r[5])) : '?';
}

export function diff(a: Snap, b: Snap) {
  // threads: key tid + creation time (a reused tid counts as exit + create)
  const tk = (t: Snap['threads'][number]) => `${t[0]}:${t[2]}`;
  const ta = new Map(a.threads.map(t => [tk(t), t])), tb = new Map(b.threads.map(t => [tk(t), t]));
  const created = b.threads.filter(t => !ta.has(tk(t))), exited = a.threads.filter(t => !tb.has(tk(t)));
  // handles: key value + type (same value, other type = closed + opened)
  const hk = (h: Snap['handles'][number]) => `${h[0]}:${h[1]}`;
  const ha = new Set(a.handles.map(hk)), hb = new Set(b.handles.map(hk));
  const opened = b.handles.filter(h => !ha.has(hk(h))), closed = a.handles.filter(h => !hb.has(hk(h)));
  // regions: by allocation base (allocated / freed), then committed ranges re-protected or (de)committed inside kept allocations
  const alloc = (s: Snap) => { const m = new Map<string, { bytes: number; type: string; name: string | null }>();
    for (const r of s.regions) { const e = m.get(r[2]) ?? { bytes: 0, type: r[5], name: r[6] }; if (r[3] === 'C') e.bytes += r[1]; m.set(r[2], e); } return m; };
  const aa = alloc(a), ab = alloc(b);
  const allocated = [...ab].filter(([k]) => !aa.has(k)), freed = [...aa].filter(([k]) => !ab.has(k));
  let commitGrow = 0, commitShrink = 0;
  for (const [k, e] of ab) { const o = aa.get(k); if (o) { if (e.bytes > o.bytes) commitGrow += e.bytes - o.bytes; else commitShrink += o.bytes - e.bytes; } }
  const rk = (r: Region) => `${r[0]}:${r[1]}`;
  const ra = new Map(a.regions.filter(r => r[3] === 'C').map(r => [rk(r), r]));
  let reprotected = 0, reprotectedBytes = 0;
  for (const r of b.regions) { const o = r[3] === 'C' && ra.get(rk(r)); if (o && o[4] !== r[4]) { reprotected++; reprotectedBytes += r[1]; } }
  // writable pages: same page address in both snapshots, hash differs; pages only in one side are new / gone
  let same = 0, changed = 0, newPages = 0, gonePages = 0;
  const pagesOf = (s: Snap) => { const m = new Map<bigint, bigint>();
    for (const [base, b64] of Object.entries(s.hashes)) { const buf = Buffer.from(b64, 'base64'); const B = big(base);
      for (let i = 0; i < buf.length / 8; i++) m.set(B + BigInt(i * 4096), buf.readBigUInt64LE(i * 8)); } return m; };
  const pa = pagesOf(a), pb = pagesOf(b);
  for (const [p, h] of pb) { const o = pa.get(p); if (o === undefined) newPages++; else if (o === 0n || h === 0n) continue; else if (o === h) same++; else changed++; }
  for (const p of pa.keys()) if (!pb.has(p)) gonePages++;
  const sum = (xs: [string, { bytes: number }][]) => xs.reduce((s, [, e]) => s + e.bytes, 0);
  return {
    seconds: Math.round((Date.parse(b.utc) - Date.parse(a.utc)) / 1000),
    threads: { a: a.threads.length, b: b.threads.length, created: created.length, exited: exited.length,
      createdBy: count(created.map(t => owner(b, t[1]))), exitedBy: count(exited.map(t => owner(a, t[1]))) },
    handles: { a: a.handleCount, b: b.handleCount, listedA: a.handles.length, listedB: b.handles.length, opened: opened.length, closed: closed.length,
      openedByType: count(opened.map(h => h[1])), closedByType: count(closed.map(h => h[1])) },
    regions: { a: a.totals.regions, b: b.totals.regions, allocated: allocated.length, allocatedMB: +(sum(allocated) / MB).toFixed(1),
      freed: freed.length, freedMB: +(sum(freed) / MB).toFixed(1), commitGrowMB: +(commitGrow / MB).toFixed(1), commitShrinkMB: +(commitShrink / MB).toFixed(1),
      reprotected, reprotectedMB: +(reprotectedBytes / MB).toFixed(1),
      allocatedByType: count(allocated.map(([, e]) => e.type)), freedByType: count(freed.map(([, e]) => e.type)) },
    pages: { compared: same + changed, changed, changedMB: +(changed * 4096 / MB).toFixed(1), changedPct: same + changed ? +(100 * changed / (same + changed)).toFixed(1) : 0,
      newMB: +(newPages * 4096 / MB).toFixed(1), goneMB: +(gonePages * 4096 / MB).toFixed(1) },
    totalsMB: Object.fromEntries((['private', 'private32', 'image', 'mapped', 'writable'] as const).map(k => [k, [+(a.totals[k] / MB).toFixed(0), +(b.totals[k] / MB).toFixed(0)]])),
  };
}

function snapCli(args: string[]) {
  const get = (f: string) => { const i = args.indexOf(f); return i >= 0 ? args[i + 1] : undefined; };
  const name = get('--name'), pid = get('--pid') ? +get('--pid')! : pidOf(name ?? 'TS3W.exe');
  const out = get('--out') ?? (mkdirSync(join(ROOT, 'probe-out', 'rewind'), { recursive: true }),
    join(ROOT, 'probe-out', 'rewind', `${(name ?? 'pid' + pid).replace(/\.exe$/i, '')}-${new Date().toISOString().replace(/[:.]/g, '-')}.json`));
  const s = snap(pid, out, !args.includes('--no-hash'));
  console.log(`${out}: pid ${s.pid}${s.wow64 ? ' (32-bit)' : ''}, ${s.threads.length} threads, ${s.handleCount} handles (${s.handles.length} listed, ok ${s.handlesOk}), ` +
    `${s.totals.regions} regions, private ${(s.totals.private / MB).toFixed(0)} MB, image ${(s.totals.image / MB).toFixed(0)} MB, mapped ${(s.totals.mapped / MB).toFixed(0)} MB, ` +
    `writable ${(s.totals.writable / MB).toFixed(0)} MB, ${s.totals.hashedPages} pages hashed (${s.totals.unreadPages} unread), ${s.ms} ms`);
}

// a queue log's "world is up" line (night-run / queue.ts tee output)
export const READY = /^\d\d:\d\d:\d\d ready: \{"in_world":true/;

async function watch(args: string[]) {
  const get = (f: string, d: string) => { const i = args.indexOf(f); return i >= 0 ? args[i + 1] : d; };
  const log = get('--log', ''), tag = get('--tag', 'watch'), startS = +get('--start-s', '130'), endS = +get('--end-s', '560');
  const names = get('--names', 'TS3W.exe,NvRemixBridge.exe').split(',');
  const dir = join(ROOT, 'probe-out', 'rewind'); mkdirSync(dir, { recursive: true });
  const sleep = (ms: number) => new Promise(r => setTimeout(r, ms));
  let seen = readFileSync(log, 'utf8').split('\n').length; // only lines after the start count
  for (;;) {
    const lines = readFileSync(log, 'utf8').split('\n');
    if (lines.slice(seen - 1).some(l => READY.test(l))) break;
    seen = lines.length; await sleep(2000);
  }
  const t0 = Date.now(), running = names.filter(n => { try { pidOf(n); return true; } catch { return false; } });
  console.log(`${new Date().toISOString()} ready seen; snapping ${running.join(', ') || 'nothing (none running)'} at +${startS} s and +${endS} s`);
  const snaps: Record<string, Snap[]> = {};
  for (const [phase, at] of [['start', startS], ['end', endS]] as const) {
    await sleep(Math.max(0, t0 + at * 1000 - Date.now()));
    for (const n of running) {
      try { (snaps[n] ??= []).push(snap(pidOf(n), join(dir, `${tag}-${n.replace(/\.exe$/i, '')}-${phase}.json`))); console.log(`${phase} ${n} ok`); }
      catch (e: any) { console.log(`${phase} ${n} FAILED: ${String(e.message).split('\n')[0]}`); }
    }
  }
  for (const [n, s] of Object.entries(snaps)) if (s.length === 2) {
    const out = join(dir, `${tag}-${n.replace(/\.exe$/i, '')}-diff.json`);
    writeFileSync(out, JSON.stringify(diff(s[0], s[1]), null, 2)); console.log(`diff ${n}: ${out}`);
  }
}

async function selfCheck() {
  const dir = mkdtempSync(join(tmpdir(), 'rewind-census-'));
  const toy = spawn('C:\\Windows\\SysWOW64\\ping.exe', ['-n', '30', '127.0.0.1'], { stdio: 'ignore', windowsHide: true });
  try {
    await new Promise(r => setTimeout(r, 800));
    const a = snap(toy.pid!, join(dir, 'a.json')), b = snap(toy.pid!, join(dir, 'b.json'));
    assert.equal(a.wow64, true, 'toy is 32-bit');
    assert.ok(a.threads.length >= 1 && a.regions.length > 10 && a.totals.image > 0 && a.totals.private > 0);
    assert.ok(a.threads.some(t => /ping\.exe|ntdll\.dll/i.test(owner(a, t[1]))), 'a thread start address maps to ping.exe or ntdll.dll');
    assert.equal(a.handlesOk, true);
    // per-type breakdown: every listed handle has a named type, the list matches GetProcessHandleCount (+-2 for opens between calls)
    assert.ok(a.handles.every(h => !h[1].startsWith('#')), 'every handle type resolves to a name');
    assert.ok(Math.abs(a.handles.length - a.handleCount) <= 2, `listed ${a.handles.length} vs count ${a.handleCount}`);
    assert.ok(a.handles.some(h => h[1] === 'File') && a.handles.some(h => h[1] === 'Event'));
    assert.ok(a.totals.hashedPages > 0 && Object.keys(a.hashes).length > 0);
    const d0 = diff(a, a);
    assert.deepEqual([d0.threads.created, d0.threads.exited, d0.handles.opened, d0.handles.closed, d0.regions.allocated, d0.regions.freed, d0.pages.changed], [0, 0, 0, 0, 0, 0, 0]);
    const d = diff(a, b);
    assert.ok(d.pages.compared > 0 && d.pages.changedPct < 50, `toy churn ${JSON.stringify(d.pages)}`);
    // diff logic on a synthetic change: one thread, one handle, one allocation, one page
    const c: Snap = structuredClone(a);
    c.threads.push([999999, '0x0', 1]); c.handles.push([0xfff0, 'Mutant', 0]); c.regions.push(['0x7F000000', 65536, '0x7F000000', 'C', 4, 'P', null]);
    const [k0, h0] = Object.entries(c.hashes)[0]; const buf = Buffer.from(h0, 'base64'); buf.writeBigUInt64LE(12345n, 0); c.hashes[k0] = buf.toString('base64');
    const dc = diff(a, c);
    assert.deepEqual([dc.threads.created, dc.handles.opened, dc.handles.openedByType.Mutant, dc.regions.allocated, dc.regions.allocatedMB, dc.pages.changed], [1, 1, 1, 1, 0.1, 1]);
    // pidOf's tasklist CSV regex: real line and non-match
    assert.equal('"ping.exe","1234","Console","1","4,321 K"'.match(/^"[^"]+","(\d+)"/m)?.[1], '1234');
    assert.equal('INFO: No tasks are running which match the specified criteria.'.match(/^"[^"]+","(\d+)"/m), null);
    // READY: real queue log lines (chain-reruns-1115-1.out)
    assert.ok(READY.test('10:18:07 ready: {"in_world":true,"load_file":"Baseline-IP-Spring.sims3","households":34}'));
    assert.ok(!READY.test('10:18:06 world state after 0 s'));
    assert.ok(!READY.test('10:18:21 lane-l8-1115-1115b-2026-11-15-ip-l8-dry-tier2-ic-rows-dry: {"in_world":true,"load_file":"Baseline-IP-Spring.sims3"}'));
    console.log(`rewind-census self-check OK: toy ${a.threads.length} threads, ${a.handleCount} handles, ${a.totals.regions} regions, ${a.totals.hashedPages} pages, snap ${a.ms} ms; toy churn ${d.pages.changed} pages`);
  } finally { toy.kill(); rmSync(dir, { recursive: true, force: true }); }
}

const [cmd, ...rest] = process.argv.slice(2);
if (cmd === '--self-check') await selfCheck();
else if (cmd === 'snap') snapCli(rest);
else if (cmd === 'watch') await watch(rest);
else if (cmd === 'diff') { const d = diff(JSON.parse(readFileSync(rest[0], 'utf8')), JSON.parse(readFileSync(rest[1], 'utf8'))); console.log(JSON.stringify(d, null, rest.includes('--json') ? 0 : 2)); }
else { console.error('usage: rewind-census.ts snap --name X.exe | --pid N [--out F] [--no-hash] | diff A B | --self-check'); process.exit(2); }
