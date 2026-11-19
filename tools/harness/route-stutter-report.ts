// Route stutter report (docs/666): what made the sim thread wait in each game hitch. Reads ts3native logs only.
//   node tools/harness/route-stutter-report.ts <log | run dir>... [--min-ms 200] [--hitches] [--top 25] [--all-agents]
//        [--perf-hz 499] [--perf-offset-ms 0] [--no-perf]   (a run dir's sim.perf.data / sim.perf.txt is read when present)
//   node tools/harness/route-stutter-report.ts --self-check
// A run dir means <dir>/ts3native-full.log. Hitches are ts3fixes `hitchwatch: N ms ... agent A, at T ms` lines (agent `-` =
// game; others are the harness and are only counted unless --all-agents). Each hitch gets the log lines of its window
// (from the last time-stamped line at or before T - N - 30 to the first one after T + 30, at most 400 lines) and from them:
//   - sync plans: `route async: sync plan N ms, IC K, reason R ... route H` (+ the `script stack of route H` line of
//     [RouteAsync] LogFallbackStack=N: the managed caller) and ts3fixes `ufplansplit: ... sync N ms` (kind / why / src);
//   - sim waits for a worker job (`route jobs: sim waited N ms for a job at SITE (route H ...)`) and on game locks
//     (`lock holders: sim waited N ms on lock L (at A), held by T from B`, [HitchAttr] Holders);
//   - the native phase split of the plan or job route (`route candidate split: PlanHierarchical ...`, [RouteJobPhases]
//     CandidateSplit=1; with the docs/663 build also world / water / post / insert);
//   - stall watch samples (`stall watch: sim thread ...: eip X, stack scan (n): ...`, [StallWatch] StallMs=150 EveryMs=50):
//     TS3W frames named from game-copy/analysis/functions.csv (Ghidra ExportFunctions, local) and the
//     tools/ghidra/symbols CSVs, leaf = first TS3W frame;
//   - GC count (`gc +N` of the hitchwatch line), P5c release / park lines, other line kinds.
// The cause is the largest measured wait that covers >= 50% of the hitch: sync plan, job wait, lock wait; else GC when
// gc > 0, else the stall-watch leaf when sampled, else "unknown". Then callers x phases are ranked by stall ms.
import { spawn } from 'node:child_process';
import { createReadStream, existsSync, readFileSync, statSync } from 'node:fs';
import { createInterface } from 'node:readline';
import { join, resolve } from 'node:path';
import { caller as stackCaller } from './nostart-report.ts';
import { loadSymbolsCsv, nearest, type Sym } from '../crash-symbolize.ts';

const ROOT = process.env.TS3_REPO ?? join(import.meta.dirname, '..', '..');
const TS3W_BASE = 0x400000;

// ---- symbols ----
type Fn = { at: number; end: number; name: string };
function loadSymbols(): { fns: Fn[]; named: Sym[] } {
  const fp = join(ROOT, 'game-copy', 'analysis', 'functions.csv');
  const fns: Fn[] = existsSync(fp) ? readFileSync(fp, 'utf8').split(/\r?\n/).slice(1).filter(Boolean)
    .map(l => { const [e, end, name] = l.split(','); return { at: parseInt(e, 16), end: parseInt(end, 16), name }; })
    .sort((a, b) => a.at - b.at) : [];
  const named: Sym[] = [];
  for (const f of ['ts3w-1.67.symbols.csv', 'ts3w-1.67.mainthread.csv']) {
    const p = join(ROOT, 'tools', 'ghidra', 'symbols', f);
    if (existsSync(p)) named.push(...loadSymbolsCsv(readFileSync(p, 'utf8')));
  }
  return { fns, named: named.sort((a, b) => a.at - b.at) };
}
let SYMS: { fns: Fn[]; named: Sym[] } | null = null;
export function nameOf(frame: string): string {
  const m = frame.match(/^TS3W\+0x([0-9A-F]+)$/i);
  if (!m) return frame.replace(/\+0x[0-9A-F]+$/i, '');            // other modules: module name only
  SYMS ??= loadSymbols();
  const a = TS3W_BASE + parseInt(m[1], 16);
  const f = SYMS.fns.length ? nearest(SYMS.fns, a) as Fn | undefined : undefined;
  const entry = f && a <= f.end ? f.at : undefined;
  const n = nearest(SYMS.named, entry ?? a);
  if (n && (entry === undefined ? a - n.at < 0x400 : n.at === entry)) return n.name;
  if (entry !== undefined) return f!.name.startsWith('FUN_') ? `FUN_${entry.toString(16).toUpperCase()}` : f!.name;
  return `TS3W:${a.toString(16).toUpperCase()}`;
}

// ---- parsing ----
export type Split = { route: string; ms: number; mode: string; scope: string; phases: Map<string, number>; src: 'cand' | 'syncphases' };
export type Hitch = {
  log: string; ms: number; at: number; tick: number; gc: number; agent: string;
  plans: { ms: number; ic: string; reason: string; route: string; stack: string[] | null }[];
  ufsync: { ms: number; kind: string; why: string; src: string }[];
  jobWaits: { ms: number; site: string; route: string; own: boolean }[];
  lockWaits: { ms: number; at: string; holder: string; from: string }[];
  splits: Split[]; samples: string[][]; p5c: number; kinds: Map<string, number>; flags: Set<string>;
  perf?: string[][];  // sampled sim-thread stacks (perf, leaf first, `TS3W+0xRVA` / module names) in the hitch window
  perfMain?: string[][]; perfHz?: number;
  cause?: string; caller?: string; covered?: number; jobCaller?: string;
};

export function parseSplit(l: string): Split | null {
  const m = l.match(/route candidate (?:split|top \d+\/\d+): PlanHierarchical (\d+) ms code \d+, ([^,]+), route ([0-9A-F]+), scope ([^:]+): (.*)$/);
  if (!m) return null;
  const phases = new Map<string, number>();
  const body = m[5].replace(/lot legs by call.*?\(incl\. ms\/calls\);/, '');
  for (const p of body.matchAll(/(?:^|, |; )([A-Za-z][\w ]*?) (\d+)\/(\d+)/g)) phases.set(p[1], (phases.get(p[1]) ?? 0) + +p[2]);
  const rest = body.match(/rest (\d+) ms/);
  if (rest) phases.set('rest', +rest[1]);
  return { route: m[3], ms: +m[1], mode: m[2], scope: m[4], phases, src: 'cand' };
}
// [RouteJobPhases] SyncPlans=1 (syncphases.cpp): "route phases: sync plan N ms R, entry E, route H, ...; ms plan=0 cell=598 ..."
export function parseSyncPhases(l: string): Split | null {
  const m = l.match(/route phases: sync plan (\d+) ms \w+, entry ([^,]+), route ([0-9A-F]+),.*?; ms (.*)$/);
  if (!m) return null;
  const phases = new Map<string, number>();
  for (const p of m[4].matchAll(/(\w+)=(\d+)/g)) phases.set(p[1], +p[2]);
  return { route: m[3], ms: +m[1], mode: 'sync', scope: m[2], phases, src: 'syncphases' };
}

const kindOf = (l: string) => l.replace(/^ts3fixes: /, 'ts3fixes:').replace(/[0-9A-Fa-fx]{6,}|\d+/g, '#').split(/[:(,]/).slice(0, 2).join(':').slice(0, 60);

// context flags of a window (several per hitch): what else ran in that frame, for hitches no measured wait covers
const FLAGS: [string, RegExp][] = [
  ['worker job >= 250 ms ended', /route async: long job \d+ ms/],
  ['P5c park / release', /^P5c (park|final release)/],
  ['job wait logged (>= 300 ms)', /route jobs: sim waited \d+ ms (for|behind)/],
  ['lock wait logged (>= 20 ms)', /lock holders: sim waited|route async: sim waited \d+ ms on lock/],
  ['60 s stats block (ts3native)', /^route jobs: longest foreign/],
  ['60 s planwait (ts3fixes)', /ts3fixes: planwait: 60 s/],
  ['diag mem walk', /^diag: (mem #\d+ periodic|low address space)/],
  ['callcensus dump', /ts3fixes: callcensus:/],
  ['Go Here cmd (routecauselog)', /ts3fixes: gohere /],
  ['routefail', /routefail/],
  ['stall watch sample', /stall watch: sim thread/],
  ['GC heap grow', /GC growth step|GC black-list/],
  ['save', /save phase|ts3fixes: save|UserDiag save/],
];

export function parseLog(text: string, log: string, minMs: number, allAgents: boolean): { hitches: Hitch[]; harness: number } {
  const L = text.split(/\r?\n/);
  const ts: (number | null)[] = L.map(l => { const m = l.match(/, at (\d+) ms\b/); return m ? +m[1] : null; });
  const stamped: number[] = []; ts.forEach((t, i) => { if (t !== null) stamped.push(i); });
  const hitches: Hitch[] = []; let harness = 0;
  for (let i = 0; i < L.length; i++) {
    const m = L[i].match(/hitchwatch: (\d+) ms, tick (\d+), .*?gc \+(\d+), agent (.+?), at (\d+) ms/);
    if (!m || +m[1] < minMs) continue;
    if (m[4] !== '-' && !allAgents) { harness++; continue; }
    const ms = +m[1], at = +m[5];
    // window: last stamped line <= at - ms - 30 .. first stamped line > at + 30
    let lo = Math.max(0, i - 400), hi = Math.min(L.length, i + 60);
    let k0 = 0, k1 = stamped.length;                       // first stamped index > i (binary search)
    while (k0 < k1) { const mid = (k0 + k1) >> 1; if (stamped[mid] <= i) k0 = mid + 1; else k1 = mid; }
    for (let k = k0 - 1; k >= 0 && stamped[k] >= lo; k--) if (stamped[k] < i && ts[stamped[k]]! <= at - ms - 30) { lo = stamped[k] + 1; break; }
    for (let k = k0; k < stamped.length && stamped[k] < hi; k++) if (ts[stamped[k]]! > at + 30) { hi = stamped[k]; break; }
    const h: Hitch = { log, ms, at, tick: +m[2], gc: +m[3], agent: m[4], plans: [], ufsync: [], jobWaits: [], lockWaits: [], splits: [], samples: [], p5c: 0, kinds: new Map(), flags: new Set() };
    for (let j = lo; j < hi; j++) {
      const l = L[j]; let x;
      if (j === i || /hitchwatch:/.test(l)) continue;
      for (const [f, re] of FLAGS) if (re.test(l)) h.flags.add(f);
      if ((x = l.match(/ufplansplit: .*?wait \d+ \(\d+ polls, longest (\d+)\)/)) && +x[1] * 2 >= ms) h.flags.add('async plan slept through it');
      if ((x = l.match(/route async: sync plan (\d+) ms, IC (\w+), reason (\w+).*?route ([0-9A-F]+)/))) h.plans.push({ ms: +x[1], ic: x[2], reason: x[3], route: x[4], stack: null });
      else if ((x = l.match(/route async: script stack of route ([0-9A-F]+) \(innermost first\): (.*)/))) { const p = h.plans.find(q => q.route === x![1]); if (p) p.stack = x[2].split(' < '); }
      else if ((x = l.match(/ufplansplit: kind (\d+) \d+ ms, sync (\d+) ms.*?why (-?\d+)(?:, src ([^,]+))?/))) h.ufsync.push({ ms: +x[2], kind: x[1], why: x[3], src: (x[4] ?? '').replace(/^(\S+) .*?( in .*)?$/, '$1$2') });  // drop the Sim's name
      else if ((x = l.match(/route jobs: sim waited (\d+) ms for a job at (\w+) \(route ([0-9A-F]+), (own|foreign) job/))) h.jobWaits.push({ ms: +x[1], site: x[2], route: x[3], own: x[4] === 'own' });
      else if ((x = l.match(/route jobs: sim waited (\d+) ms behind an own job \(route ([0-9A-F]+)/))) h.jobWaits.push({ ms: +x[1], site: 'behind-own', route: x[2], own: true });
      else if ((x = l.match(/lock holders: sim waited (\d+) ms on lock \w+ \(at (0x[0-9A-F]+)\), held by (\w+) \d+ from (0x[0-9A-F]+)/))) h.lockWaits.push({ ms: +x[1], at: x[2], holder: x[3], from: x[4] });
      else if ((x = l.match(/route async: sim waited (\d+) ms on lock \w+ \(route lock\)/))) h.lockWaits.push({ ms: +x[1], at: 'route-lock', holder: 'job', from: '-' });
      else if ((x = l.match(/route async: long job (\d+) ms, (.*?), IC kind (\d+), route ([0-9A-F]+), arm \S+, scope ([^,\s]+)/))) h.jobCaller ??= `long job ${x[2]} IC kind ${x[3]} scope ${x[5]}`;
      else if (/route candidate (split|top \d+\/\d+): PlanHierarchical/.test(l)) { const s = parseSplit(l); if (s) h.splits.push(s); }
      else if (/route phases: sync plan \d+ ms/.test(l)) { const s = parseSyncPhases(l); if (s) h.splits.push(s); }
      else if ((x = l.match(/stall watch: sim thread \d+: no Simulate iteration finished for \d+ ms.*?: eip (\S+), stack scan \(\d+\): (.*)/))) h.samples.push([x[1].replace(/,$/, ''), ...x[2].trim().split(/\s+/)]);
      else if (/^P5c (park|final release)/.test(l)) h.p5c++;
      else { const k = kindOf(l); if (k) h.kinds.set(k, (h.kinds.get(k) ?? 0) + 1); }
    }
    classify(h); hitches.push(h);
  }
  return { hitches, harness };
}

// leaf of a stall sample: first TS3W frame (eip when it is in TS3W), with the module the eip is in when that is not TS3W
export function leaf(s: string[]): string {
  const first = s.find(f => /^TS3W\+/i.test(f));
  const top = /^TS3W\+/i.test(s[0]) ? '' : `${nameOf(s[0])} < `;
  return first ? top + nameOf(first) : nameOf(s[0]);
}

const sum = (a: { ms: number }[]) => a.reduce((x, y) => x + y.ms, 0);
export function classify(h: Hitch): void {
  const planMs = Math.max(sum(h.plans), sum(h.ufsync)), jobMs = sum(h.jobWaits), lockMs = sum(h.lockWaits);
  const best = [['sync plan', planMs], ['job wait', jobMs], ['lock wait', lockMs]].sort((a, b) => +b[1] - +a[1])[0] as [string, number];
  if (best[1] * 2 >= h.ms) {
    h.cause = best[0]; h.covered = best[1];
    if (best[0] === 'sync plan') {
      const p = [...h.plans].sort((a, b) => b.ms - a.ms)[0], u = [...h.ufsync].sort((a, b) => b.ms - a.ms)[0];
      h.caller = p?.stack ? stackCaller(p.stack) : u ? `ufplan kind ${u.kind} why ${u.why}${u.src ? ' ' + u.src : ''}` : `IC ${p.ic} ${p.reason} (no stack)`;
    } else if (best[0] === 'job wait') {
      const w = [...h.jobWaits].sort((a, b) => b.ms - a.ms)[0]; h.caller = `${w.own ? 'own' : 'foreign'} job @${w.site}`;
    } else {
      const w = [...h.lockWaits].sort((a, b) => b.ms - a.ms)[0]; h.caller = `lock at ${w.at} held by ${w.holder} from ${w.from}`;
    }
  } else if (h.gc > 0) { h.cause = 'GC'; h.caller = `gc +${h.gc}`; }
  else if (h.perfHz && onCpu(h) < 0.5) { h.cause = 'sim thread off CPU (waiting)'; h.caller = `main busy in ${mainLeaf(h)}`; }
  else if (h.perf?.length) { h.cause = 'sim on CPU (perf)'; h.caller = perfLeaf(h); }
  else if (h.samples.length) { h.cause = 'sampled'; h.caller = leaf(h.samples[0]); }
  // a worker route job of >= 250 ms ended in the window: 14x more often than in ordinary frames (docs/666 §2), but the sim
  // thread's wait for it is only logged from 300 ms (SwNote) and lock waits only from 20 ms on hooked game locks
  else if (h.flags.has('worker job >= 250 ms ended')) { h.cause = 'worker job in frame (no logged wait)'; h.caller = h.jobCaller ?? 'long job'; }
  else { h.cause = 'unknown'; h.caller = 'no measured wait'; }  // P5c lines are as common in ordinary frames (docs/662 §5)
}

// phases of the hitch: the split of a sync plan's route or a waited job's route, else every sync split in the window
export function phasesOf(h: Hitch): Map<string, number> {
  const routes = new Set([...h.plans.map(p => p.route), ...h.jobWaits.map(w => w.route)]);
  let sp = h.splits.filter(s => routes.has(s.route) && s.route !== '00000000');
  if (!sp.length) sp = h.splits.filter(s => s.mode === 'sync');
  // one breakdown per route: the CandidateSplit one (finer inside PlanHierarchical) when there is one, else SyncPlans'
  const byRoute = new Map<string, Split[]>();
  for (const s of sp) { const k = s.route === '00000000' ? `${s.src}:${byRoute.size}` : s.route; byRoute.set(k, [...(byRoute.get(k) ?? []), s]); }
  const out = new Map<string, number>();
  for (const g of byRoute.values()) {
    const use = g.some(s => s.src === 'cand') ? g.filter(s => s.src === 'cand') : g;
    for (const s of use) for (const [k, v] of s.phases) out.set(k, (out.get(k) ?? 0) + v);
  }
  return out;
}

// ---- perf (s140: `perf record -k boottime -e cpu-clock:u -F HZ -g -t <Simulator tids>,<main tid>`) ----
// Wine's GetTickCount (the `at N ms` of the logs) runs on CLOCK_BOOTTIME on this Deck [measured 2026-11-19: log start
// 10:39:04 = boottime 209607 s, CLOCK_MONOTONIC_RAW was ~54,000 s behind], so perf time * 1000 = log ms (+ --perf-offset-ms).
// Thread role from perf's comm: EA names its sim thread "Simulator" (seen in /proc/<pid>/task/*/comm); any other is "main".
// A sample is on-CPU only (cpu-clock): a sim thread that waits (lock, job, main thread) has no samples, so the on-CPU share
// of a hitch = sim samples / (hitch ms * HZ / 1000).
export function perfFrame(ip: string, dso: string): string {
  const a = parseInt(ip, 16), mod = dso.replace(/^.*[\\/]/, '').replace(/[()]/g, '');
  if (/^TS3W\.exe$/i.test(mod) || (a >= 0x400000 && a < 0x125A000 && /unknown/.test(mod))) return `TS3W+0x${(a - TS3W_BASE).toString(16).toUpperCase()}`;
  return mod || 'unknown';
}
export async function attachPerf(hs: Hitch[], src: string, hz: number, offsetMs: number): Promise<number> {
  if (!hs.length) return 0;
  const win = hs.map(h => ({ h, lo: h.at - h.ms - 20, hi: h.at + 10 })).sort((a, b) => a.lo - b.lo);
  for (const w of win) { w.h.perf = []; w.h.perfMain = []; w.h.perfHz = hz; }
  const input = /\.data$/.test(src) ? spawn('perf', ['script', '-i', src, '-F', 'comm,tid,time,ip,dso'], { stdio: ['ignore', 'pipe', 'ignore'] }).stdout : createReadStream(src);
  let cur: { comm: string; t: number; fr: string[] } | null = null, k = 0, n = 0;
  const flush = () => {
    if (!cur || !cur.fr.length) return;
    while (k < win.length && win[k].hi < cur.t) k++;            // samples arrive time-ordered
    for (let j = k; j < win.length && win[j].lo <= cur.t; j++) if (cur.t <= win[j].hi) { (cur.comm === 'Simulator' ? win[j].h.perf! : win[j].h.perfMain!).push(cur.fr); n++; }
  };
  for await (const l of createInterface({ input })) {
    const m = l.match(/^\s*(.+?)\s+(\d+)\s+(\d+\.\d+):\s*$/);
    if (m) { flush(); cur = { comm: m[1], t: +m[3] * 1000 + offsetMs, fr: [] }; continue; }
    const f = l.match(/^\s+([0-9a-f]+)\s+(\(.*\))\s*$/);
    if (f && cur && cur.fr.length < 32) cur.fr.push(perfFrame(f[1], f[2]));
  }
  flush();
  for (const h of hs) classify(h);
  return n;
}
const firstTs3w = (s: string[]) => s.find(f => /^TS3W\+/.test(f));
export function perfLeaf(h: Hitch): string {
  const g = new Map<string, number>();
  for (const s of h.perf ?? []) { const f = firstTs3w(s), k = f ? nameOf(f) : s[0]; g.set(k, (g.get(k) ?? 0) + 1); }
  const top = [...g].sort((a, b) => b[1] - a[1])[0];
  return top ? `${top[0]} (${top[1]}/${h.perf!.length} samples)` : '-';
}
export function mainLeaf(h: Hitch): string {
  const g = new Map<string, number>();
  for (const s of h.perfMain ?? []) { const f = firstTs3w(s), k = f ? nameOf(f) : s[0]; g.set(k, (g.get(k) ?? 0) + 1); }
  const top = [...g].sort((a, b) => b[1] - a[1])[0];
  return top ? `${top[0]} (${top[1]} samples)` : 'nothing (main idle too)';
}
export const onCpu = (h: Hitch) => h.perfHz ? (h.perf?.length ?? 0) / (h.ms * h.perfHz / 1000) : NaN;

// ---- report ----
const fmtPh = (m: Map<string, number>) => [...m].filter(([, v]) => v > 0).sort((a, b) => b[1] - a[1]).map(([k, v]) => `${k} ${v}`).join(', ') || '-';
function rank<T>(rows: T[], key: (r: T) => string, ms: (r: T) => number): [string, number, number][] {
  const g = new Map<string, [number, number]>();
  for (const r of rows) { const k = key(r), e = g.get(k) ?? [0, 0]; e[0]++; e[1] += ms(r); g.set(k, e); }
  return [...g].map(([k, [n, s]]) => [k, n, s] as [string, number, number]).sort((a, b) => b[2] - a[2]);
}

export function report(hs: Hitch[], harness: number, opt: { top: number; perHitch: boolean }): string[] {
  const o: string[] = [];
  const tot = sum(hs);
  o.push(`${hs.length} game hitches, ${(tot / 1000).toFixed(1)} s (harness hitches skipped: ${harness})`);
  if (opt.perHitch) {
    o.push('', '| log | at | ms | gc | cause | caller | covered | phases (self ms) | stall-watch leaves | perf sim on-CPU, top |', '|---|---|---|---|---|---|---|---|---|---|');
    for (const h of hs) o.push(`| ${h.log} | ${h.at} | ${h.ms} | ${h.gc} | ${h.cause} | ${h.caller} | ${h.covered ?? '-'} | ${fmtPh(phasesOf(h))} | ${[...new Set(h.samples.map(leaf))].slice(0, 3).join('; ') || '-'} | ${h.perfHz ? `${(100 * onCpu(h)).toFixed(0)}% ${perfLeaf(h)}` : '-'} |`);
  }
  o.push('', '## Causes', '', '| cause | hitches | s |', '|---|---|---|');
  for (const [k, n, s] of rank(hs, h => h.cause!, h => h.ms)) o.push(`| ${k} | ${n} | ${(s / 1000).toFixed(1)} |`);
  o.push('', '## Callers (cause: caller), by hitch ms', '', '| caller | hitches | s |', '|---|---|---|');
  for (const [k, n, s] of rank(hs, h => `${h.cause}: ${h.caller}`, h => h.ms).slice(0, opt.top)) o.push(`| ${k} | ${n} | ${(s / 1000).toFixed(1)} |`);
  // callers x phases: each hitch's ms shared out by its phase split (hitches with no split: phase "unsplit")
  const cx: { k: string; ms: number }[] = [];
  for (const h of hs) {
    const ph = phasesOf(h), t = [...ph.values()].reduce((a, b) => a + b, 0);
    if (!t) { cx.push({ k: `${h.caller} x unsplit`, ms: h.ms }); continue; }
    for (const [p, v] of ph) if (v > 0) cx.push({ k: `${h.caller} x ${p}`, ms: h.ms * v / t });
  }
  o.push('', '## Callers x phases, by stall ms (hitch ms shared out by its phase split)', '', '| caller x phase | rows | s |', '|---|---|---|');
  for (const [k, n, s] of rank(cx, r => r.k, r => r.ms).slice(0, opt.top)) o.push(`| ${k} | ${n} | ${(s / 1000).toFixed(1)} |`);
  const ph = new Map<string, number>();
  for (const h of hs.filter(x => x.cause === 'sync plan' || x.cause === 'job wait')) for (const [k, v] of phasesOf(h)) ph.set(k, (ph.get(k) ?? 0) + v);
  const pt = [...ph.values()].reduce((a, b) => a + b, 0);
  o.push('', `## Phase totals in plan / job-wait hitches (self ms): ${[...ph].sort((a, b) => b[1] - a[1]).map(([k, v]) => `${k} ${v} (${(100 * v / (pt || 1)).toFixed(0)}%)`).join(', ') || '-'}`);
  const samp = hs.flatMap(h => h.samples.map(s => ({ k: leaf(s), ms: h.ms / h.samples.length })));
  if (samp.length) {
    o.push('', '## Sampled leaves (stall watch), hitch ms shared over its samples', '', '| leaf | samples | s |', '|---|---|---|');
    for (const [k, n, s] of rank(samp, r => r.k, r => r.ms).slice(0, opt.top)) o.push(`| ${k} | ${n} | ${(s / 1000).toFixed(1)} |`);
    const fr = hs.flatMap(h => h.samples.flatMap(s => [...new Set(s.filter(f => /^TS3W\+/i.test(f)).slice(0, 8).map(nameOf))].map(k => ({ k, ms: h.ms / h.samples.length }))));
    o.push('', '## Sampled TS3W functions anywhere in the top 8 frames (inclusive)', '', '| function | samples | s |', '|---|---|---|');
    for (const [k, n, s] of rank(fr, r => r.k, r => r.ms).slice(0, opt.top)) o.push(`| ${k} | ${n} | ${(s / 1000).toFixed(1)} |`);
  }
  const np = hs.filter(h => h.cause !== 'sync plan');
  if (np.length) {
    o.push('', `## Context flags of the ${np.length} hitches with no sync plan (a hitch counts under every flag it has)`, '', '| flag | hitches | s |', '|---|---|---|');
    const fl = np.flatMap(h => [...h.flags, ...(h.gc ? ['gc +N in hitchwatch'] : []), ...(h.flags.size || h.gc ? [] : ['(none)'])].map(k => ({ k, ms: h.ms })));
    for (const [k, n, s] of rank(fl, r => r.k, r => r.ms)) o.push(`| ${k} | ${n} | ${(s / 1000).toFixed(1)} |`);
  }
  const ph2 = hs.filter(h => h.perfHz);
  if (ph2.length) {
    const self = ph2.flatMap(h => (h.perf ?? []).map(s => ({ k: firstTs3w(s) ? nameOf(firstTs3w(s)!) : s[0], ms: 1000 / h.perfHz! })));
    const incl = ph2.flatMap(h => (h.perf ?? []).flatMap(s => [...new Set(s.filter(f => /^TS3W\+/.test(f)).slice(0, 12).map(nameOf))].map(k => ({ k, ms: 1000 / h.perfHz! }))));
    const main = ph2.filter(h => onCpu(h) < 0.5).flatMap(h => (h.perfMain ?? []).map(s => ({ k: firstTs3w(s) ? nameOf(firstTs3w(s)!) : s[0], ms: 1000 / h.perfHz! })));
    const on = ph2.reduce((a, h) => a + (h.perf?.length ?? 0) * 1000 / h.perfHz!, 0), all = ph2.reduce((a, h) => a + h.ms, 0);
    o.push('', `## perf: ${ph2.length} hitches, sim thread on CPU ${(on / 1000).toFixed(1)} of ${(all / 1000).toFixed(1)} s`);
    for (const [t, rows] of [['sim thread self (first TS3W frame), on-CPU ms', self], ['sim thread inclusive (TS3W functions in the top 12 frames)', incl], ['main thread self while the sim thread waited (on-CPU < 50%)', main]] as [string, { k: string; ms: number }[]][]) {
      o.push('', `### ${t}`, '', '| function | samples | s |', '|---|---|---|');
      for (const [k, n, s] of rank(rows, r => r.k, r => r.ms).slice(0, opt.top)) o.push(`| ${k} | ${n} | ${(s / 1000).toFixed(2)} |`);
    }
  }
  const unk = hs.filter(h => h.cause === 'unknown' || h.cause === 'GC');
  if (unk.length) {
    const kinds = new Map<string, number>();
    for (const h of unk) for (const k of h.kinds.keys()) kinds.set(k, (kinds.get(k) ?? 0) + 1);
    o.push('', `## Line kinds in the windows of ${unk.length} GC / unknown hitches (hitches holding the kind; P5c in ${unk.filter(h => h.p5c).length})`, '');
    for (const [k, n] of [...kinds].sort((a, b) => b[1] - a[1]).slice(0, opt.top)) o.push(`- ${n} ${k}`);
  }
  return o;
}

async function selfCheck(): Promise<boolean> {
  const t = [
    'route async: long job 357 ms, A10 foreign START, IC kind 3, route 25505163, arm -, scope unseen, at 1000 ms',
    'route async: sync plan 300 ms, IC Plan, reason NoStart, entry nojob, route 2519BFE1, caller 3F7D2227 (0 lines rate-limited before)',
    'route async: script stack of route 2519BFE1 (innermost first): ScriptCore.Route.Route_Plan < RouteBackoffHook.Plan < Sims3.SimIFace.Route.PlanToPoint < RunStuckCheckHelper.RunOrig',
    'route candidate split: PlanHierarchical 290 ms code 0, sync, route 2519BFE1, scope unseen: closestWps 0/0, cell 89/7, lotPlan 0/7, world 50/2, post 20/9 (self ms/calls); lot legs by call 65F11C 1/1 (incl. ms/calls); rest 131 ms (own code + allocator + unhooked helpers); cut 0',
    'route phases: sync plan 295 ms succeeded, entry IC_Route_Plan, route 2519BFE1, planlocked 1; loops entry=0/0; ms plan=0 cell=200 post=1 alloc=9',
    'ts3fixes: hitchwatch: 320 ms, tick 5, Sun. at 10:26 PM, gc +0, agent -, at 1400 ms',
    'lock holders: sim waited 230 ms on lock 34083400 (at 0x004A94EC), held by main 228 from 0x004A8E22 (wait #1)',
    'stall watch: sim thread 9: no Simulate iteration finished for 246 ms (1/3): eip ntdll+0xDA90, stack scan (2): kernelbase+0x32B4F TS3W+0x2A4F010',
    'ts3fixes: hitchwatch: 300 ms, tick 6, Sun. at 10:26 PM, gc +0, agent -, at 2000 ms',
    'ts3fixes: hitchwatch: 400 ms, tick 7, Sun. at 10:27 PM, gc +1, agent social_round 3, at 3000 ms',
  ].join('\n');
  const { hitches, harness } = parseLog(t, 'x', 200, false);
  const [a, b] = hitches, ph = phasesOf(a);
  const ok0 = hitches.length === 2 && harness === 1 && a.cause === 'sync plan' && a.caller!.startsWith('LotManager.RunStuckCheckHelper')
    && ph.get('cell') === 89 && !ph.has('alloc') && parseSyncPhases('route phases: sync plan 5 ms failed, entry X, route 00000000, planlocked 1; ms cell=3 alloc=2')!.phases.get('alloc') === 2 && ph.get('world') === 50 && ph.get('rest') === 131 && !ph.has('65F11C')
    && b.cause === 'lock wait' && b.samples.length === 1 && leaf(b.samples[0]).startsWith('ntdll < ');
  // perf text: one Simulator sample (TS3W 0x4A94EC) and one main sample inside hitch b's window (1680-2010 ms), one outside
  const { mkdtempSync, writeFileSync, rmSync } = await import('node:fs'), { tmpdir } = await import('node:os');
  const dir = mkdtempSync(join(tmpdir(), 'rsr-')), pf = join(dir, 'sim.perf.txt');
  writeFileSync(pf, 'Simulator  4242 1.700000: \n\t  4a94ec (/x/TS3W.exe)\n\t  e2ede (/x/TS3W.exe)\n\nTS3W.exe  4200 1.800000: \n\t  7f00 (/x/ntdll.so)\n\nSimulator  4242 2.500000: \n\t  4a94ec (/x/TS3W.exe)\n');
  const n = await attachPerf([b], pf, 10, 0); rmSync(dir, { recursive: true });
  const okPerf = n === 2 && b.perf!.length === 1 && b.perf![0][0] === 'TS3W+0xA94EC' && b.perfMain![0][0] === 'ntdll.so' && Math.abs(onCpu(b) - 1 / 3) < 1e-9 && b.cause === 'lock wait';
  const ok = ok0 && okPerf;
  console.log(ok ? 'route-stutter-report self-check ok' : `route-stutter-report self-check FAILED ${JSON.stringify(hitches.map(h => [h.cause, h.caller, [...phasesOf(h)]]))}`);
  return ok;
}

if (import.meta.filename === resolve(process.argv[1] ?? '')) {
  const argv = process.argv.slice(2);
  if (argv.includes('--self-check')) process.exit(await selfCheck() ? 0 : 1);
  const val = (k: string, d: number) => { const i = argv.indexOf(k); return i >= 0 ? +argv[i + 1] : d; };
  const minMs = val('--min-ms', 200), top = val('--top', 25);
  const files = argv.filter((a, i) => !a.startsWith('--') && !['--min-ms', '--top', '--perf-hz', '--perf-offset-ms'].includes(argv[i - 1]))
    .map(a => existsSync(a) && statSync(a).isDirectory() ? join(a, 'ts3native-full.log') : a).filter(f => existsSync(f));
  if (!files.length) { console.error('usage: route-stutter-report.ts <log | run dir>... [--min-ms 200] [--hitches] [--top 25] [--all-agents] [--perf-hz 499] [--perf-offset-ms 0] [--no-perf]'); process.exit(2); }
  const hz = val('--perf-hz', 499), offset = val('--perf-offset-ms', 0);
  const all: Hitch[] = []; let harness = 0;
  for (const f of files) {
    const r = parseLog(readFileSync(f, 'utf8'), f.replace(/.*\/(phase-[\d-]+)\/.*/, '$1'), minMs, argv.includes('--all-agents'));
    all.push(...r.hitches); harness += r.harness;
    // <run dir>/sim.perf.data (s140-run.sh) or a `perf script -F comm,tid,time,ip,dso` text next to the log
    const pd = [join(f, '..', 'sim.perf.data'), join(f, '..', 'sim.perf.txt')].find(x => existsSync(x));
    if (pd && !argv.includes('--no-perf')) console.error(`${pd}: ${await attachPerf(r.hitches, pd, hz, offset)} perf samples in hitch windows`);
  }
  console.log(report(all, harness, { top, perHitch: argv.includes('--hitches') }).join('\n'));
}
