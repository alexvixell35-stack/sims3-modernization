// Host micro-benchmark for one MonoJit piece (docs/data/jit-bench-howto.md, docs/537 §25): HostTest.JitBench cases run in monohost
// on the game's real EA interpreter and our compiled code, interpreter vs compiled per process, and one process per arm (env set),
// so a piece's switch gives the third column: interp / compiled (first arm, the base) / compiled + the piece (every later arm).
// Every timed run is n = 200000 iterations, after a warm-up of every case; reps alternate interp / compiled inside a process and
// the arms alternate A B B A across rounds, on one pinned core at high priority. ns per call = per iteration minus the empty loop
// (case 20, its median in the same process) / the case's step. Stats over all reps of all rounds: median and p95 (nearest rank).
//   node tools/jit-piece-bench.ts --cases 14 [--arm base:] [--arm bc:MONOHOST_JITBIND=1,MONOHOST_JITBOUNDCALL=3] [--common K=V,..]
//        [--rounds 2] [--reps 7] [--core 2] [--game-copy <main checkout>/game-copy] [--json <out.json>] [--force]
//   node tools/jit-piece-bench.ts --self-check
// Without --arm there is one arm, `base:` (the host defaults). --common goes into every arm (e.g. MONOHOST_JITLAZY=1).
// CPU-heavy: refuses a core the running game (TS3W.exe) may use unless --force; Windows only for the run (monohost is 32-bit Win32).
import { execFileSync } from 'node:child_process';
import { rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { gameCopy, OUT, runHost } from './jit-coverage.ts';

const args = process.argv.slice(2);
const arg = (n: string) => { const i = args.indexOf(n); return i >= 0 ? args[i + 1] : undefined; };
const all = (n: string) => args.flatMap((a, i) => a === n && args[i + 1] !== undefined ? [args[i + 1]] : []);

export type Arm = { name: string; env: Record<string, string> };
export function parseArm(s: string): Arm {
  const i = s.indexOf(':');
  if (i <= 0) throw new Error(`--arm wants name:K=V,K=V (name: for no env), got "${s}"`);
  return { name: s.slice(0, i), env: parseEnv(s.slice(i + 1)) };
}
export function parseEnv(s: string): Record<string, string> {
  const env: Record<string, string> = {};
  for (const kv of s.split(',').filter(x => x.trim())) {
    const m = kv.match(/^\s*([A-Z_][A-Z0-9_]*)=(.*)$/);
    if (!m) throw new Error(`bad env pair "${kv}" (want MONOHOST_X=value)`);
    env[m[1]] = m[2];
  }
  return env;
}
// "jitbench reps case <c> step <s> <interp|compiled> <ns> <ns> ..." (native/monohost/monohost-jit.cpp JitBenchRun)
export type Reps = Map<number, { step: number; interp: number[]; compiled: number[] }>;
export function parseReps(log: string): Reps {
  const out: Reps = new Map();
  for (const m of log.matchAll(/^jitbench reps case (\d+) step (\d+) (interp|compiled)((?: [\d.]+)+)\s*$/gm)) {
    const c = +m[1], r = out.get(c) ?? { step: +m[2], interp: [], compiled: [] };
    r[m[3] as 'interp' | 'compiled'].push(...m[4].trim().split(' ').map(Number));
    out.set(c, r);
  }
  return out;
}
// "jitbench warm case <c> (<name>): checksum <n>": the compiled warm-up result of each case, compared across arms
// "jitbench case <c> sums off <interp> on <compiled>": the timed runs' checksums (n = 200000), logged for every case
export const runSums = (log: string) => new Map([...log.matchAll(/^jitbench case (\d+) sums off (-?\d+) on (-?\d+)\s*$/gm)].map(m => [+m[1], { off: +m[2], on: +m[3] }]));
export const warmSums = (log: string) => new Map([...log.matchAll(/^jitbench warm case (\d+) \(.*\): checksum (-?\d+)\s*$/gm)].map(m => [+m[1], +m[2]]));
// "jitbench rettd case <c> bails <b> iters <n> sites <s>" (RetTeardown on: block bails over the timed compiled iterations) and
// "jitbench sums case <c> interp <x> compiled <y>" (the checksum values of the last timed pair)
export const rettdBails = (log: string) => new Map([...log.matchAll(/^jitbench rettd case (\d+) bails (\d+) iters (\d+) sites (\d+)\s*$/gm)].map(m => [+m[1], { bails: +m[2], iters: +m[3], sites: +m[4] }]));
export const timedSums = (log: string) => new Map([...log.matchAll(/^jitbench sums case (\d+) interp (-?\d+) compiled (-?\d+)\s*$/gm)].map(m => [+m[1], { interp: +m[2], compiled: +m[3] }]));
export const median = (x: number[]) => { const s = [...x].sort((a, b) => a - b), n = s.length; return !n ? NaN : n % 2 ? s[(n - 1) / 2] : (s[n / 2 - 1] + s[n / 2]) / 2; };
export const p95 = (x: number[]) => { const s = [...x].sort((a, b) => a - b); return s.length ? s[Math.min(s.length - 1, Math.ceil(0.95 * s.length) - 1)] : NaN; };
// one process: ns per call of each case and arm, the empty loop's median off
export function perCall(r: Reps): Map<number, { interp: number[]; compiled: number[] }> {
  const loop = r.get(20);
  if (!loop) throw new Error('no case 20 (empty loop) reps in the log');
  const l = median([...loop.interp, ...loop.compiled]), out = new Map<number, { interp: number[]; compiled: number[] }>();
  for (const [c, x] of r) if (c !== 20) out.set(c, { interp: x.interp.map(t => t - l / x.step), compiled: x.compiled.map(t => t - l / x.step) });
  return out;
}
// ABBA over rounds: round 0 in order, round 1 reversed, ...
export const armOrder = (n: number, round: number) => { const o = [...Array(n).keys()]; return round % 2 ? o.reverse() : o; };

export type Row = { c: number; arm: string; interpMed: number; interpP95: number; compMed: number; compP95: number; n: number; vsBase: number };
export function summarize(arms: Arm[], samples: Map<string, Map<number, { interp: number[]; compiled: number[] }>>): Row[] {
  const rows: Row[] = [];
  const cases = [...new Set([...samples.values()].flatMap(m => [...m.keys()]))].sort((a, b) => a - b);
  for (const c of cases) {
    const base = samples.get(arms[0].name)?.get(c);
    for (const a of arms) {
      const s = samples.get(a.name)?.get(c);
      if (!s) continue;
      const cm = median(s.compiled);
      rows.push({ c, arm: a.name, interpMed: median(s.interp), interpP95: p95(s.interp), compMed: cm, compP95: p95(s.compiled), n: s.compiled.length, vsBase: base ? cm - median(base.compiled) : NaN });
    }
  }
  return rows;
}
// the game's core mask (Windows), or 0 when no game runs / unknown
function gameMask(): number {
  if (process.platform !== 'win32') return 0;
  try {
    const out = execFileSync('powershell', ['-NoProfile', '-Command', '(Get-Process TS3W -ErrorAction SilentlyContinue | Select-Object -First 1).ProcessorAffinity'], { encoding: 'utf8', timeout: 20000 }).trim();
    return out ? Number(out) : 0;
  } catch { return 0; }
}
const f1 = (x: number) => Number.isFinite(x) ? x.toFixed(1) : '-';

function selfCheck() {
  const ok = (c: boolean, m: string) => { if (!c) { console.error(`FAIL ${m}`); process.exit(1); } };
  ok(median([3, 1, 2]) === 2 && median([1, 2, 3, 4]) === 2.5 && p95([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 100]) === 19 && p95([5]) === 5, 'median, p95 nearest rank');
  const a = parseArm('bc:MONOHOST_JITBIND=1,MONOHOST_JITBOUNDCALL=3');
  ok(a.name === 'bc' && a.env.MONOHOST_JITBOUNDCALL === '3' && Object.keys(parseArm('base:').env).length === 0, 'arm parse');
  let threw = false; try { parseArm('noname'); } catch { threw = true; } ok(threw, 'arm without a colon refused');
  threw = false; try { parseEnv('jitlazy=1'); } catch { threw = true; } ok(threw, 'lower-case env refused');
  // match lines and non-match lines (the old best-of line, a case with no numbers, a stray word)
  const log = ['jitbench reps case 14 step 1 interp 30.00 32.00 31.00', 'jitbench reps case 14 step 1 compiled 10.00 12.00 11.00',
    'jitbench reps case 20 step 1 interp 2.00 2.00', 'jitbench reps case 20 step 1 compiled 2.00 2.00',
    'jitbench reps case 18 step 64 compiled 100.00', 'jitbench reps case 18 step 64 interp 228.00',
    'jitbench case 14 JitId (compiled, trivial) interp 29.0 ns  compiled 9.0 ns  speedup 3.2x  saved 20.0 ns',
    'jitbench reps case 15 step 1 interp', 'jitbench reps case 16 step 1 compiledx 1.0'].join('\r\n');
  const r = parseReps(log);
  const ws = warmSums('jitbench warm case 32 (Array.Copy 5-arg int[] 4-64 (64 copies)): checksum -17\r\njitbench warm case 33 (x): checksum\r\njitbench case 14 JitId checksum 5');
  const rb = rettdBails('jitbench rettd case 35 bails 12 iters 3000 sites 9\r\njitbench rettd case 36 bails 1 iters\r\njitbench rettd case 14 bails 0 iters 5 sites 2 x');
  const ts = timedSums('jitbench sums case 14 interp -5 compiled -5\r\njitbench sums case 15 interp 5\r\njitbench sums case 16 interp 5 compiled 6 x');
  ok(rb.size === 1 && rb.get(35)!.bails === 12 && rb.get(35)!.iters === 3000 && ts.size === 1 && ts.get(14)!.interp === -5, 'rettd / sums parse: matches + non-matches');
  ok(ws.size === 1 && ws.get(32) === -17, 'warm checksum parse: match + non-matches');
  const rs = runSums('jitbench case 3 sums off -5 on -5\r\njitbench case 4 CHECKSUM DIFFERS: off 1 on 2\r\n');
  ok(rs.size === 1 && rs.get(3)!.off === -5 && rs.get(3)!.on === -5, 'run checksum parse: match + non-match');
  ok(r.size === 3 && r.get(14)!.interp.length === 3 && r.get(14)!.compiled[1] === 12 && !r.has(15) && !r.has(16), 'reps parse: matches + non-matches');
  const pc = perCall(r);
  ok(pc.get(14)!.interp[0] === 28 && Math.abs(pc.get(18)!.compiled[0] - (100 - 2 / 64)) < 1e-9 && !pc.has(20), 'per call: loop off, per step');
  threw = false; try { perCall(new Map([[14, { step: 1, interp: [1], compiled: [1] }]])); } catch { threw = true; } ok(threw, 'no loop case refused');
  ok(armOrder(3, 0).join() === '0,1,2' && armOrder(3, 1).join() === '2,1,0', 'ABBA order');
  const arms = [parseArm('base:'), parseArm('g:MONOHOST_X=1')];
  const rows = summarize(arms, new Map([['base', new Map([[14, { interp: [30, 32], compiled: [10, 12] }]])], ['g', new Map([[14, { interp: [31], compiled: [15] }]])]]));
  ok(rows.length === 2 && rows[0].vsBase === 0 && rows[1].vsBase === 4 && rows[1].compMed === 15 && rows[0].n === 2, 'summary: delta vs the first arm');
  console.log('jit-piece-bench self-check OK');
}

if (import.meta.main) {
  if (args.includes('--self-check')) selfCheck();
  else {
    const cases = arg('--cases');
    if (!cases || !/^\d+(,\d+)*$/.test(cases)) throw new Error('--cases <n,n> (HostTest.JitBench case numbers, native/monohost/monohost-jit.cpp kJitCase) or --self-check');
    const arms = all('--arm').map(parseArm);
    if (!arms.length) arms.push({ name: 'base', env: {} });
    if (new Set(arms.map(x => x.name)).size !== arms.length) throw new Error('arm names must differ');
    const common = parseEnv(arg('--common') ?? ''), rounds = +(arg('--rounds') ?? '2'), reps = arg('--reps') ?? '7', core = +(arg('--core') ?? '2');
    const gm = gameMask();
    if (gm && (gm >> core & 1) && !args.includes('--force')) throw new Error(`core ${core} is in the running game's affinity mask 0x${gm.toString(16)}: pick a free --core or stop the game (--force to run anyway)`);
    const gc = gameCopy();
    let commit = '';
    try { commit = execFileSync('git', ['rev-parse', '--short', 'HEAD'], { encoding: 'utf8' }).trim(); } catch { /* not a checkout */ }
    const samples = new Map<string, Map<number, { interp: number[]; compiled: number[] }>>(), sums = new Map<string, Map<number, number>>(), rsums = new Map<string, Map<number, { off: number; on: number }>>();
    const rettd = new Map<string, Map<number, { bails: number; iters: number; sites: number }>>(), tsums = new Map<string, Map<number, Set<string>>>();
    for (let round = 0; round < rounds; round++)
      for (const i of armOrder(arms.length, round)) {
        const a = arms[i], t0 = Date.now();
        // a log per run: parallel bench runs shared monohost.exe.log (EBUSY, or one run read another's lines)
        const logPath = join(OUT, `monohost.bench-${process.pid}-${round}-${a.name}.log`);
        const env = { MONOHOST_JIT: 'on', MONOHOST_JITCORPUS: '0', MONOHOST_MINTCALL: 'off', ...common, ...a.env, MONOHOST_JITCASES: cases, MONOHOST_JITREPS: reps, MONOHOST_JITCORE: String(core), MONOHOST_LOG: logPath };
        const { log } = runHost(env, gc, 600, false);
        rmSync(logPath, { force: true });
        if (/CHECKSUM DIFFERS/.test(log)) console.log(`WARN ${a.name}: ${log.match(/jitbench case \d+ CHECKSUM DIFFERS.*/)![0]} (compiled result differs from the interpreter)`);
        sums.set(a.name, warmSums(log));
        const rm = rettd.get(a.name) ?? new Map(), tm = tsums.get(a.name) ?? new Map();
        for (const [c, x] of rettdBails(log)) { const o = rm.get(c) ?? { bails: 0, iters: 0, sites: x.sites }; o.bails += x.bails; o.iters += x.iters; rm.set(c, o); }
        for (const [c, x] of timedSums(log)) tm.set(c, (tm.get(c) ?? new Set()).add(`${x.interp}/${x.compiled}`));
        rettd.set(a.name, rm); tsums.set(a.name, tm);
        rsums.set(a.name, runSums(log));
        const pc = perCall(parseReps(log)), m = samples.get(a.name) ?? new Map();
        for (const [c, x] of pc) { const s = m.get(c) ?? { interp: [], compiled: [] }; s.interp.push(...x.interp); s.compiled.push(...x.compiled); m.set(c, s); }
        samples.set(a.name, m);
        console.log(`${new Date().toISOString().slice(11, 19)} round ${round + 1}/${rounds} arm ${a.name} done (${((Date.now() - t0) / 1000).toFixed(0)} s)`);
      }
    const rows = summarize(arms, samples);
    const want = cases.split(',').map(Number), checksums = Object.fromEntries(want.map(c => [c, Object.fromEntries(arms.map(a => [a.name, sums.get(a.name)?.get(c)]))]));
    for (const c of want) { const v = Object.values(checksums[c]); console.log(`checksum case ${c}: ${arms.map(a => `${a.name} ${checksums[c][a.name]}`).join(', ')}${v.every(x => x !== undefined && x === v[0]) ? '' : '  WARN: differs between arms'}`); }
    const runChecksums = Object.fromEntries(want.map(c => [c, Object.fromEntries(arms.map(a => [a.name, rsums.get(a.name)?.get(c)]))]));
    for (const c of want) { const r = arms.map(a => rsums.get(a.name)?.get(c)); console.log(`run checksum case ${c} (n = 200000): ${arms.map((a, i) => `${a.name} interp ${r[i]?.off} compiled ${r[i]?.on}`).join(', ')}${r.every(x => x && x.off === x.on && x.on === r[0]!.on) ? '' : '  WARN: differs'}`); }
    console.log(`ns per call, ${rounds} rounds x ${reps} reps per arm, core ${core}, commit ${commit || '?'}; first arm = compiled base, later arms = compiled + their switches`);
    console.log('case  arm            interp med   p95   compiled med   p95   vs base   n');
    for (const x of rows) console.log(`${String(x.c).padStart(4)}  ${x.arm.padEnd(14)} ${f1(x.interpMed).padStart(10)} ${f1(x.interpP95).padStart(6)} ${f1(x.compMed).padStart(14)} ${f1(x.compP95).padStart(6)} ${(x === rows.find(y => y.c === x.c) ? '' : (x.vsBase >= 0 ? '+' : '') + f1(x.vsBase)).padStart(9)} ${String(x.n).padStart(3)}`);
    for (const a of arms) for (const [c, x] of rettd.get(a.name) ?? []) console.log(`rettd ${a.name} case ${c}: bails ${x.bails} over ${x.iters} compiled iterations (${(x.bails / x.iters).toFixed(4)} per iteration), sites ${x.sites}`);
    const tsOut = Object.fromEntries(arms.map(a => [a.name, Object.fromEntries([...(tsums.get(a.name) ?? [])].map(([c, s]) => [c, [...s]]))]));
    for (const c of want) console.log(`timed sums case ${c} (interp/compiled per process): ${arms.map(a => `${a.name} ${[...(tsums.get(a.name)?.get(c) ?? [])].join(' ')}`).join('; ')}`);
    const rettdOut = Object.fromEntries(arms.map(a => [a.name, Object.fromEntries(rettd.get(a.name) ?? [])]));
    const out = arg('--json');
    if (out) writeFileSync(out, JSON.stringify({ commit, core, rounds, reps: +reps, common, arms, rows, checksums, timedSums: tsOut, rettd: rettdOut, runChecksums }, null, 1) + '\n');
  }
}
