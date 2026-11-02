// Per-queue verdict table without reading logs by hand: one row per job (and per profile.repros window) with PASS / FAIL / VOID
// and the reason (verdict lines: self checks such as hook FAIL lines, asyncIdle and feature/fix activity, early noise stop, crash,
// timeout; plus hitch-ab/worst-hitches runInvalid for profile runs: profiler cut short, noisy machine, crash), and for profile jobs
// the hitch numbers (>= 300 / >= 1000 ms per 10 min, max, p99, it/s; A/B runs show off / on), and the timing verdict per job and
// window (docs/168 §10: valid / valid-single / invalid (reason); queue.ts stores it as row.timing, older rows get it computed here).
// Markdown on stdout. Read-only.
//   node tools/harness/run-verdict.ts <queue name> [--date YYYY-MM-DD] [--baseline <profile tag>]
//   node tools/harness/run-verdict.ts --self-check
// Inputs: probe-out/queue/<queue>-<date>.jsonl (queue.ts rows; newest date when --date is left out), probe-out/profile/<tag>-*.
// Set TS3_REPO=<main checkout> when running from a git worktree.
import assert from 'node:assert/strict';
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { THRESH, armStats, loadRun, repoIniAt, runIters, toolCosts, toolsOn, withBaseIni, type Arm, type ToolCosts } from './hitch-ab.ts';

const ROOT = process.env.TS3_REPO ?? join(import.meta.dirname, '..', '..');
export const QUEUE_DIR = join(ROOT, 'probe-out', 'queue');

export const jsonlRows = (f: string): any[] => existsSync(f) ? readFileSync(f, 'utf8').split('\n').filter(Boolean).flatMap(l => { try { return [JSON.parse(l)]; } catch { return []; } }) : [];
// newest <queue>-<date>.jsonl of a queue in dir (date sorts as text)
export function queueFile(name: string, date?: string, dir = QUEUE_DIR): string | null {
  if (date) { const f = join(dir, `${name}-${date}.jsonl`); return existsSync(f) ? f : null; }
  const re = new RegExp(`^${name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}-(\\d{4}-\\d\\d-\\d\\d)\\.jsonl$`);
  const f = readdirSync(dir).filter(x => re.test(x)).sort().at(-1);
  return f ? join(dir, f) : null;
}

export type Verdict = { v: 'PASS' | 'FAIL' | 'VOID' | 'N/A'; why: string };
const VOID_RE = /run void|noisy machine|NOT comparable/;
// a queue row (or a repro sub-row) -> verdict; invalid = runInvalid reason of its profile run, if any
export function classify(row: any, invalid: string | null = null): Verdict {
  const lines: string[] = [...(row.verdict ?? []), ...(row.error ? [String(row.error)] : [])];
  const voids = lines.filter(l => VOID_RE.test(l));
  if (voids.length || invalid) return { v: 'VOID', why: [...voids, ...(invalid ? [`runInvalid: ${invalid}`] : [])].join('; ') };
  const bad = [row.crashed && `crash${row.crash ? `: ${String(row.crash).split('\n')[0].slice(0, 120)}` : ''}`, row.timedOut && 'timed out',
    ...lines.filter(l => /^(FAIL|STOP)\b|FAIL:|ERROR/.test(l))].filter(Boolean) as string[];
  if (row.na) return { v: 'N/A', why: lines.filter(l => /^N\/A: /.test(l)).join('; ') }; // queue.ts naRow (docs/236 Round 9): never PASS
  const pass = row.pass ?? (row.exitCode === 0 && !bad.length);
  if (!pass) return { v: 'FAIL', why: bad.join('; ') || `exit code ${row.exitCode ?? '?'}` };
  return { v: 'PASS', why: lines.filter(l => /^ts3fixes activity|self check|mono ledger INCOMPLETE/.test(l)).join('; ') };
}

// ---- timing verdict (docs/168 §10): may this run serve as timing (A/B) evidence without a separate timing run?
// valid = in-load A/B with all of: job PASS (not FAIL/VOID, no crash), machine noise low over the whole job (machine-load.ts:
// no other CPU >= 10%, no clock drop, nothing on the game CPUs or GPU), no profile runInvalid, only light measurement tools
// that tool-costs.json allows in timing runs (heavy = invalid: heavy tool X; light with timing 'no' = correctness-only tool X),
// and >= MIN_AB_ITERS iterations per arm (repro firings, else kept --ab slices). valid-single = the same without an A/B: one
// side of a cross-run comparison with the same job id, asi sha256 and ts3fixes tuning digest (hitch-ab.ts --reuse). valid-half =
// valid except an ABBA --ab run with MIN_AB_ITERS / 2 to MIN_AB_ITERS - 1 kept slices per arm (10 min = 2 + 2): valid once pooled
// with the opposite-order run (-offfirst / -onfirst) of the same job and build; repro firings get no half (>= 4 per arm per run)
export const MIN_AB_ITERS = 4;
export type Timing = { timing: 'valid' | 'valid-half' | 'valid-single' | 'invalid'; why: string };
export function timingVerdict(x: { verdict: Verdict; noise?: string | null; invalid?: string | null; tools: string[]; ab: boolean; perArm?: { on: number; off: number; src?: string } | null }, costs: ToolCosts): Timing {
  const why: string[] = [];
  if (x.verdict.v !== 'PASS') why.push(`job ${x.verdict.v}${x.verdict.why ? `: ${x.verdict.why.slice(0, 120)}` : ''}`);
  if (x.noise !== 'low') why.push(`noise ${x.noise ?? 'unknown'}`);
  if (x.invalid) why.push(`run invalid: ${x.invalid}`);
  for (const t of x.tools.map(t => t.replace(/ \(on arm\)$/, ''))) {
    const c = costs.tools[t];
    if (!c) why.push(`unknown tool ${t}`); else if (c.class === 'heavy') why.push(`heavy tool ${t}`); else if (c.timing === 'no') why.push(`correctness-only tool ${t}`);
  }
  const lo = x.perArm ? Math.min(x.perArm.on, x.perArm.off) : 0, half = x.perArm?.src === 'slices' && lo >= MIN_AB_ITERS / 2 && lo < MIN_AB_ITERS;
  if (x.ab && !x.perArm) why.push('A/B iterations unknown');
  else if (x.ab && lo < MIN_AB_ITERS && !half) why.push(`A/B ${x.perArm!.off} off / ${x.perArm!.on} on iterations, < ${MIN_AB_ITERS} per arm${x.perArm!.src === 'slices' ? '' : ' (repro firings)'}`);
  return why.length ? { timing: 'invalid', why: why.join('; ') } : { timing: !x.ab ? 'valid-single' : half ? 'valid-half' : 'valid', why: '' };
}
export const timingText = (t: { timing: string; why: string }) => t.timing === 'invalid' ? `invalid (${t.why})` : t.timing;
const subPass = (rr: any) => ({ ...rr, pass: rr.exitCode === 0 && !(rr.verdict ?? []).some((l: string) => /^FAIL/.test(l)) });
// a queue row -> its timing verdict (and one per profile.repros window). iniText = the ts3native.ini the job ran with (queue.ts);
// left out: the repo ini at the row's build plus the row's overrides (hitch-ab.ts toolsOn rule)
export function rowTiming(row: any, iniText?: string | null, costs: ToolCosts = toolCosts()): Timing & { windows?: Record<string, Timing> } {
  const base = iniText !== undefined ? iniText : repoIniAt(row.build), prof = row.run?.profile;
  const one = (p: any, tag: string | null, sub: any): Timing => {
    let run = null; if (tag) try { run = loadRun(tag); } catch { /* no profile files */ }
    const perArm = run ? runIters(run) : null;
    const tools = toolsOn(withBaseIni({ ini: row.ini, run: { profile: p ?? { noProf: true, noSamplers: true } } }, base)); // no profile: no profiler, no samplers
    return timingVerdict({ verdict: classify(sub, run?.invalid ?? null), noise: row.noise, tools, ab: !!p?.ab || !!perArm, perArm }, costs);
  };
  if (!row.repros?.length) return one(prof, row.profileTag ?? null, row);
  const items: any[] = prof?.repros ?? [], windows: Record<string, Timing> = {};
  for (const rr of row.repros) {
    const it = items.find(x => (typeof x === 'string' ? x : x?.id) === rr.id), { repros: _, ...p } = { ...prof, ...(typeof it === 'object' ? it : {}), repro: rr.id };
    windows[rr.id] = one(p, rr.tag, subPass(rr));
  }
  const ws = Object.entries(windows), bad = ws.filter(([, t]) => t.timing === 'invalid'), jv = classify(row);
  const why = [...(jv.v === 'PASS' ? [] : [`job ${jv.v}${jv.why ? `: ${jv.why.slice(0, 120)}` : ''}`]), ...bad.map(([id, t]) => `${id}: ${t.why}`)];
  const all = (ok: string[]) => ws.every(([, t]) => ok.includes(t.timing));
  return { timing: why.length ? 'invalid' : all(['valid']) ? 'valid' : all(['valid', 'valid-half']) ? 'valid-half' : 'valid-single', why: why.join('; '), windows };
}

// hitch numbers of a profile arm: per 10 min >= 300 / >= 1000, max, p99, it/s
export function armNums(a: Arm) {
  const s = armStats(a);
  return { per300: s.per10[THRESH.indexOf(300)], per1000: s.per10[THRESH.indexOf(1000)], max: s.max, p99: s.p99.bounded ? null : s.p99.ms, its: s.mins ? s.iters / (s.mins * 60) : 0, n1000: s.counts[THRESH.indexOf(1000)], mins: s.mins };
}
type Nums = ReturnType<typeof armNums>;
export function runNums(tag: string): { nums: Record<string, Nums>; invalid: string | null } | null {
  let r; try { r = loadRun(tag); } catch { return null; } // no profile files (not a profile job, or the run died early)
  return { nums: Object.fromEntries(Object.entries(r.arms).map(([k, a]) => [k, armNums(a)])), invalid: r.invalid };
}
const f1 = (x: number) => x.toFixed(1), p99s = (x: number | null) => x === null ? '<50' : x.toFixed(0);
const NUM_COLS: [string, (n: Nums) => string][] = [['>=300/10min', n => f1(n.per300)], ['>=1000/10min', n => f1(n.per1000)], ['max ms', n => n.max.toFixed(0)], ['p99 ms', n => p99s(n.p99)], ['it/s', n => f1(n.its)]];
// one cell per number: 'all' arm, or 'off / on' for an --ab run
// an arm with no kept seconds (whole window excluded, e.g. repro-triggered) shows '-'
const numCells = (n: Record<string, Nums> | undefined) => NUM_COLS.map(([, f]) => { const g = (x: Nums) => x.mins ? f(x) : '-'; return !n ? '' : n.all ? g(n.all) : n.off && n.on ? `${g(n.off)} / ${g(n.on)}` : ''; });
const cell = (s: string) => s.replace(/\|/g, '\\|').replace(/\n/g, ' ').slice(0, 300);

export function verdictTable(file: string, baseline?: string): string {
  const rows = jsonlRows(file), base = baseline ? runNums(baseline) : null;
  const out = [`### ${file.replace(/^.*[\\/]/, '').replace(/\.jsonl$/, '')}`, '',
    `| Job | Verdict | Timing | Reason | min | ${NUM_COLS.map(c => c[0]).join(' | ')} |${baseline ? ` vs ${baseline} |` : ''}`,
    `|${'---|'.repeat(5 + NUM_COLS.length + (baseline ? 1 : 0))}`];
  const count = { PASS: 0, FAIL: 0, VOID: 0, 'N/A': 0 };
  const add = (name: string, row: any, tag: string | null, mins: number | string, tm: { timing: string; why: string } | undefined) => {
    const r = tag ? runNums(tag) : null, v = classify(row, r?.invalid ?? null); count[v.v]++;
    let cmp = '';
    if (base && r) { const a = r.nums.all ?? r.nums.on, b = base.nums.all ?? base.nums.off; if (a && b) cmp = `>=300 ${f1(b.per300)} -> ${f1(a.per300)}, max ${b.max.toFixed(0)} -> ${a.max.toFixed(0)}, p99 ${p99s(b.p99)} -> ${p99s(a.p99)}${base.invalid ? ' (baseline INVALID)' : ''}`; }
    out.push(`| ${name} | ${v.v === 'PASS' ? 'PASS' : `**${v.v}**`} | ${tm ? cell(timingText(tm)) : '-'} | ${cell(v.why) || '-'} | ${mins} | ${numCells(r?.nums).join(' | ')} |${baseline ? ` ${cmp} |` : ''}`);
  };
  for (const row of rows) {
    const t = row.timing ? { timing: row.timing, why: row.timingWhy ?? '' } : rowTiming(row); // stored by queue.ts, else computed
    const tag = row.machineTag ? ` [${row.machineTag}]` : '';
    add(row.job + tag, row, row.repros?.length ? null : row.profileTag ?? null, row.minutes ?? '-', t);
    for (const rr of row.repros ?? []) add(`${row.job}${tag} / ${rr.id}`, subPass(rr), rr.tag, '', rr.timing ? { timing: rr.timing, why: rr.timingWhy ?? '' } : (t as any).windows?.[rr.id]);
  }
  out.push('', `${count.PASS} PASS, ${count.FAIL} FAIL, ${count.VOID} VOID, ${count['N/A']} N/A of ${count.PASS + count.FAIL + count.VOID + count['N/A']}. Timing (docs/168 §10): valid = in-load A/B usable as timing evidence, valid-half = valid once pooled with the opposite-order run (same job, same build), valid-single = one side of a cross-run comparison (hitch-ab.ts --reuse). A/B runs: off / on. p99 '<50' = below the profiler's 50 ms hitch threshold.`);
  return out.join('\n');
}

function selfCheck() {
  assert.equal(classify({ pass: true, verdict: ['PASS: profile 10 min'] }).v, 'PASS');
  assert.deepEqual(classify({ pass: false, na: true, exitCode: 1, verdict: ['N/A: not triggered: x', 'N/A (vanilla): asi build check'] }), { v: 'N/A', why: 'N/A: not triggered: x' });
  assert.equal(classify({ pass: false, exitCode: 1, verdict: ['FAIL: self check: [RouteAsync] on but 0 async route jobs started (invokeHook 0): async off, run void'] }).v, 'VOID');
  assert.equal(classify({ pass: false, exitCode: 1, verdict: ['FAIL: noisy machine early: machine load: ...'] }).v, 'VOID');
  assert.equal(classify({ pass: true, verdict: ['PASS'] }, 'profiler cut short (100 of 600 s)').v, 'VOID');
  const c = classify({ pass: false, crashed: true, crash: 'fault TS3W.exe+0x1234\nmore', verdict: [] });
  assert.equal(c.v, 'FAIL'); assert.match(c.why, /^crash: fault TS3W\.exe\+0x1234$/);
  assert.equal(classify({ pass: false, timedOut: true, verdict: [] }).why, 'timed out');
  assert.match(classify({ pass: false, verdict: ['PASS: x', 'FAIL: events not fired: 3am'] }).why, /events not fired/);
  assert.match(classify({ pass: true, verdict: ['PASS', 'ts3fixes activity: stuckcheckasync 12'] }).why, /ts3fixes activity/);
  assert.deepEqual(numCells({ all: { per300: 0, per1000: 0, max: 0, p99: null, its: 0, n1000: 0, mins: 0 } }), ['-', '-', '-', '-', '-']);
  assert.deepEqual(numCells({ off: { per300: 1, per1000: 0, max: 300, p99: null, its: 50, n1000: 0, mins: 1 }, on: { per300: 2, per1000: 0, max: 400, p99: 80, its: 60, n1000: 0, mins: 1 } }),
    ['1.0 / 2.0', '0.0 / 0.0', '300 / 400', '<50 / 80', '50.0 / 60.0']);
  // timing verdict (docs/168 §10): fixtures only, no game data (CI runs on Linux without probe-out)
  const tc = toolCosts(), ok = { verdict: { v: 'PASS' as const, why: '' }, noise: 'low', tools: ['prof-lean', 'frames'], ab: true, perArm: { on: 4, off: 4, src: 'slices' } };
  assert.deepEqual(timingVerdict(ok, tc), { timing: 'valid', why: '' });
  assert.deepEqual(timingVerdict({ ...ok, ab: false, perArm: null }, tc), { timing: 'valid-single', why: '' });
  assert.deepEqual(timingVerdict({ ...ok, perArm: { on: 2, off: 2, src: 'slices' } }, tc), { timing: 'valid-half', why: '' }, '10-min ABBA: valid once both orders pool');
  assert.equal(timingVerdict({ ...ok, perArm: { on: 3, off: 2, src: 'slices' } }, tc).timing, 'valid-half');
  assert.equal(timingVerdict({ ...ok, perArm: { on: 1, off: 2, src: 'slices' } }, tc).why, 'A/B 2 off / 1 on iterations, < 4 per arm');
  assert.equal(timingVerdict({ ...ok, perArm: { on: 2, off: 2, src: 'repro' } }, tc).why, 'A/B 2 off / 2 on iterations, < 4 per arm (repro firings)');
  assert.equal(timingVerdict({ ...ok, noise: 'medium', perArm: { on: 2, off: 2, src: 'slices' } }, tc).why, 'noise medium');
  assert.equal(timingVerdict({ ...ok, perArm: null }, tc).why, 'A/B iterations unknown');
  assert.equal(timingVerdict({ ...ok, noise: 'medium' }, tc).why, 'noise medium');
  assert.equal(timingVerdict({ ...ok, noise: undefined }, tc).why, 'noise unknown');
  assert.equal(timingVerdict({ ...ok, tools: ['prof-stages', 'samplers', 'CellMemoProbe', 'RouteEarlyOut.observe (on arm)'] }, tc).why, 'heavy tool samplers; correctness-only tool CellMemoProbe');
  assert.equal(timingVerdict({ ...ok, tools: ['mystery'] }, tc).why, 'unknown tool mystery');
  assert.match(timingVerdict({ ...ok, verdict: classify({ pass: false, crashed: true, crash: 'fault x', verdict: [] }) }, tc).why, /^job FAIL: crash: fault x$/);
  assert.equal(timingVerdict({ ...ok, invalid: 'profiler cut short (100 of 600 s)' }, tc).timing, 'invalid');
  assert.equal(timingText({ timing: 'invalid', why: 'noise high' }), 'invalid (noise high)');
  // rowTiming on rows with no profile files (iniText '' = no repo ini, no git): soak = valid-single; --ab job with no slices = unknown
  // iterations; a plain profile window has the stack samplers on (heavy); a repros job is invalid when one window is
  const soak = { pass: true, exitCode: 0, verdict: ['PASS'], noise: 'low', ini: {}, run: { soak: { minutes: 5 } } };
  assert.deepEqual(rowTiming(soak, '', tc), { timing: 'valid-single', why: '' });
  assert.equal(rowTiming({ ...soak, noise: 'high' }, '', tc).why, 'noise high');
  assert.equal(rowTiming({ ...soak, ini: { 'ProbeSnap.Enabled': 1 } }, '', tc).why, 'heavy tool ProbeSnap');
  assert.equal(rowTiming({ ...soak, ini: { 'HitchAttr.Enabled': 1 } }, '', tc).timing, 'valid-single');
  const prof = (p: object) => ({ ...soak, run: { profile: { minutes: 10, noSamplers: true, profOpts: { stages: false }, ...p } }, profileTag: 'selfcheck-no-such-tag' });
  assert.equal(rowTiming(prof({}), '', tc).timing, 'valid-single');
  assert.equal(rowTiming(prof({ noSamplers: false }), '', tc).why, 'heavy tool samplers');
  assert.equal(rowTiming(prof({ ab: 'unseenthrottle' }), '', tc).why, 'A/B iterations unknown');
  const rp = { ...prof({ repros: ['a', { id: 'b', taskstall: 250 }] }), repros: [{ id: 'a', tag: 'selfcheck-x-a', exitCode: 0, verdict: ['PASS: a'] }, { id: 'b', tag: 'selfcheck-x-b', exitCode: 0, verdict: ['PASS: b'] }] };
  const rt = rowTiming(rp, '', tc);
  assert.deepEqual([rt.timing, rt.why, rt.windows!.a.timing], ['invalid', 'b: heavy tool taskstall', 'valid-single']);
  assert.equal(rowTiming({ ...rp, repros: rp.repros.slice(0, 1) }, '', tc).timing, 'valid-single');
  console.log('run-verdict self-check ok');
}

if (import.meta.main) {
  const args = process.argv.slice(2), opt = (k: string) => { const i = args.indexOf(k); return i < 0 ? undefined : args.splice(i, 2)[1]; };
  if (args.includes('--self-check')) selfCheck();
  else {
    const date = opt('--date'), baseline = opt('--baseline'), name = args[0];
    if (!name) { console.log('usage: node tools/harness/run-verdict.ts <queue name> [--date YYYY-MM-DD] [--baseline <tag>]'); process.exit(2); }
    const f = queueFile(name, date);
    if (!f) { console.log(`no ${name}-${date ?? '<date>'}.jsonl in ${QUEUE_DIR} (set TS3_REPO from a worktree)`); process.exit(1); }
    console.log(verdictTable(f, baseline));
  }
}
