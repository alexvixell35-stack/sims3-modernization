// Queue verdict digest: at most 5 lines per job of a finished queue run, then `verdict: PASS | FAIL (<jobs>)`.
//   node tools/harness/queue-digest.ts <tag> [--since HH:MM] [--queue <file.json>] [--json]
//     tag = <queue> (newest date) or <queue>-<YYYY-MM-DD>; rules from tools/harness/queues/<queue>.json or --queue
//   node tools/harness/queue-digest.ts --self-check
// Per job: PASS / FAIL / VOID with the reason (run-verdict.ts classify()), minutes, start; the job's own PASS rules
// (minCounters, nativeRequire, logRequire, logForbid, fixesRequire, scriptErrorsMax) each with its measured value; hitches
// (p99, max, >= 300 / >= 1000 ms per arm) from hitch-ab.ts loadRun + armStats, the numbers hitchReport() prints; for jobs with
// [AutShadow] / [MonoThread] the gate lines of mono-cost-report.ts costLines(). run-verdict.ts is the full table; this is the
// short form. Reads only; set TS3_REPO=<main checkout> from a worktree. Last line `needs review: ...` (missing files, rule
// fields this tool does not read, rows without a job, VOID jobs).
import { existsSync, readFileSync } from 'node:fs';
import { basename, join, resolve } from 'node:path';
import { parseArgs } from 'node:util';
import { parseRequire } from './fixes-require.ts';
import { armStats, loadRun, type Arm, type Stats } from './hitch-ab.ts';
import { costLines, inputs, type Input } from './mono-cost-report.ts';
import { QUEUE_DIR, classify, jsonlRows, queueFile } from './run-verdict.ts';

const QDEF = join(import.meta.dirname, 'queues');
const RULES = ['minCounters', 'nativeRequire', 'logRequire', 'logForbid', 'scriptErrorsMax'];
const clip = (s: string, n = 150) => (s.length > n ? s.slice(0, n - 3) + '...' : s);
const hhmm = (iso: string) => { const d = new Date(iso); return isNaN(+d) ? '?' : `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`; };

export type Hitch = { arms: { arm: string; s: Stats }[]; invalid: string | null };
export interface Ctx { log: (row: any) => string | null; hitch: (tag: string) => Hitch | string; mono: (row: any) => string[] }

// each rule with its measured value: "native sypPlans.1>=5 (7) ok"; ok null = could not be measured
const cmp = (v: number, op: string, w: number) => op === '>=' ? v >= w : op === '<=' ? v <= w : op === '==' ? v === w : op === '>' ? v > w : v < w;
export function ruleChecks(job: any, row: any, logText: string | null): { text: string; ok: boolean | null }[] {
  const out: { text: string; ok: boolean | null }[] = [], lines = logText?.split(/\r?\n/);
  const count = (re: string) => lines ? lines.filter(l => new RegExp(re).test(l)).length : NaN;
  if (job.minCounters !== undefined) { const v = row.counters?.after?.version; out.push({ text: `minCounters ${job.minCounters} (v${v ?? '?'})`, ok: typeof v === 'number' ? v >= job.minCounters : null }); }
  for (const r of job.nativeRequire ? parseRequire(job.nativeRequire) : []) {
    const v = row.nativeDelta?.[r.name];
    out.push({ text: `native ${r.name}${r.op}${r.value} (${v ?? 'absent'})`, ok: typeof v === 'number' ? cmp(v, r.op, r.value) : row.nativeDelta ? false : null });
  }
  for (const [re, min] of Object.entries(job.logRequire ?? {})) { const n = count(re); out.push({ text: `log /${re}/ >= ${min} (${isNaN(n) ? 'no log' : n})`, ok: isNaN(n) ? null : n >= (min as number) }); }
  for (const [re, max] of Object.entries(job.logForbid ?? {})) { const n = count(re); out.push({ text: `log /${re}/ <= ${max} (${isNaN(n) ? 'no log' : n})`, ok: isNaN(n) ? null : n <= (max as number) }); }
  const fr = job.run?.profile?.fixesRequire;
  for (const r of fr ? parseRequire(String(fr)) : []) {
    const c = row.fixActivity?.counters?.[r.name], v = typeof c?.after === 'number' ? c.after - (c.before ?? 0) : undefined;
    out.push({ text: `fixes ${r.name}${r.op}${r.value} (${v ?? 'absent'})`, ok: v === undefined ? (row.fixActivity ? false : null) : cmp(v, r.op, r.value) });
  }
  if (job.scriptErrorsMax !== undefined) { const n = Array.isArray(row.scriptErrors) ? row.scriptErrors.length : undefined; out.push({ text: `script errors <= ${job.scriptErrorsMax} (${n ?? '?'})`, ok: n === undefined ? null : n <= job.scriptErrorsMax }); }
  return out;
}

export const hitchLine = (arms: Hitch['arms']) => 'hitches: ' + arms.map(({ arm, s }) =>
  `${arm === 'all' ? '' : `${arm} `}p99 ${s.p99.bounded ? '<' : ''}${Math.round(s.p99.ms)} ms, max ${Math.round(s.max)} ms, >=300 ${s.counts[3]}, >=1000 ${s.counts[5]} (${s.mins.toFixed(1)} min)`).join('; ');

// gate lines of costLines(): Mode R classes, aborts, worker cost, dry apply / behaviour, go-live net
const GATES = /^- (Mode R match rate|worker aborts by reason|worker busy|G7 dry apply|G8 behaviour|go-live after consume|go-live estimate|worker adds)/;
export const monoGates = (lines: string[]) => lines.filter(l => GATES.test(l)).map(l => l.slice(2).replace(/\s*\[(measured|inferred)[^\]]*\]/g, ''));
export const isMono = (row: any) => Object.keys(row.ini ?? {}).some(k => /^(AutShadow|MonoThread)\./.test(k));

export function digest(rows: any[], jobs: Map<string, any>, ctx: Ctx): { lines: string[]; failed: string[]; review: string[] } {
  const L: string[] = [], failed: string[] = [], review: string[] = [];
  for (const row of rows) {
    const id = String(row.job), job = jobs.get(String(row.rerunOf ?? row.job));
    if (!job && jobs.size) review.push(`${id}: not in the queue file`);
    const h = row.profileTag ? ctx.hitch(row.profileTag) : null, v = classify(row, h && typeof h !== 'string' ? h.invalid : null);
    if (v.v !== 'PASS') failed.push(v.v === 'VOID' ? `${id} void` : id);
    if (v.v === 'VOID') review.push(`${id}: VOID, rerun`);
    const timing = row.timing === 'invalid' ? `, timing invalid (${clip(String(row.timingWhy ?? '?'), 60)})` : '';
    L.push(`${id}: ${v.v}, ${row.minutes ?? '?'} min from ${hhmm(row.time)}${timing}${v.v !== 'PASS' && v.why ? `; ${clip(v.why.replace(/FAIL:\s*/g, ''), 200)}` : ''}`);
    if (job) {
      const extra = Object.keys(job).filter(k => /require|forbid|max$/i.test(k) && !RULES.includes(k));
      if (extra.length) review.push(`${id}: rule fields not read: ${extra.join(', ')}`);
      const log = job.logRequire || job.logForbid ? ctx.log(row) : null;
      if ((job.logRequire || job.logForbid) && log === null) review.push(`${id}: ts3native.log copy missing`);
      const rc = ruleChecks(job, row, log);
      L.push(`  rules: ${rc.length ? clip(rc.map(r => `${r.text} ${r.ok === null ? '?' : r.ok ? 'ok' : 'FAIL'}`).join(', '), 300) : 'none beyond the run verdict'}`);
    }
    if (typeof h === 'string') review.push(`${id}: ${h}`); else if (h) L.push(`  ${hitchLine(h.arms)}`);
    if (isMono(row)) {
      const g = ctx.mono(row);
      if (!g.length) review.push(`${id}: Mono job without gate lines (no /native dump?)`);
      for (let i = 0; i < g.length && i < 4; i += 2) L.push(`  ${clip(g.slice(i, i + 2).join(' | '), 300)}`);
    }
  }
  return { lines: L, failed, review };
}

function realCtx(file: string): Ctx {
  let mono: Input[] | null = null;
  return {
    log: row => { const p = row.nativeLog ? [row.nativeLog, join(QUEUE_DIR, basename(String(row.nativeLog).replace(/\\/g, '/')))].find(existsSync) : undefined; return p ? readFileSync(p, 'utf8') : null; },
    hitch: tag => {
      try { const r = loadRun(tag); return { arms: (['all', 'off', 'on'] as const).filter(a => r.arms[a]).map(a => ({ arm: a, s: armStats(r.arms[a] as Arm) })), invalid: r.invalid ?? null }; }
      catch (e) { return `no hitch files (${clip((e as Error).message, 80)})`; }
    },
    mono: row => { mono ??= inputs(file, 10); const i = mono.find(x => x.tag === row.profileTag); return i ? monoGates(costLines(i)) : []; },
  };
}

function selfCheck() {
  let k = 0;
  const t = (c: unknown, m: string) => { console.assert(c, m); if (!c) process.exitCode = 1; k++; };
  const jobs = new Map<string, any>([
    ['a', { id: 'a', minCounters: 140, nativeRequire: 'sypPlans.1>=5,fbAsync>=1', logRequire: { 'route async: START': 2 }, logForbid: { MISMATCH: 0 }, run: { profile: { minutes: 10, fixesRequire: 'stuckcheck_rearmed>=1' } }, scriptErrorsMax: 0 }],
    ['m', { id: 'm', ini: { 'AutShadow.Enabled': 1 }, run: { profile: { minutes: 10 } }, someRequire: 'x' }], ['v', { id: 'v', run: { profile: { minutes: 10 } } }]]);
  const rows = [
    { job: 'a', time: '2026-10-30T16:01:48Z', minutes: 13.1, pass: true, profileTag: 'q-a', counters: { after: { version: 140 } }, nativeDelta: { 'sypPlans.1': 7, fbAsync: 0 },
      fixActivity: { counters: { stuckcheck_rearmed: { before: 0, after: 47 } } }, scriptErrors: [], verdict: ['PASS: profile 10 min'] },
    { job: 'm', time: '2026-10-30T16:20:00Z', minutes: 12, pass: false, crashed: true, crash: 'TS3W+0x1DDFBD', profileTag: 'q-m', ini: { 'AutShadow.Enabled': 1, 'AutShadow.Mode': 1 },
      verdict: ['FAIL: profile 10 min', 'FAIL: sim clock stuck at day 13 22:33 for ~60 s'] },
    { job: 'v', time: '2026-10-30T16:40:00Z', minutes: 9, pass: true, profileTag: 'q-v', verdict: ['PASS: profile 10 min'] },
    { job: 'zz', time: '2026-10-30T17:00:00Z', minutes: 1, pass: true, verdict: [] }];
  const arm: Arm = { iters: 200, secs: 600, hitches: [350, 1200, 80, 60].map(wallMs => ({ wallMs })) as any, excl: new Map() };
  const as0 = { version: 5, mode: 1, req: 0, cuts: 0, cutUs: 0, workUs: 0, workRuns: 0, busy: 0, abortTotal: 0, overrun: 0, noWorker: 0 };
  const nat = { before: { autshadow: as0 }, after: { autshadow: { ...as0, req: 500, cuts: 500, cutUs: 600_000, workUs: 1_200_000, workRuns: 400, abortTotal: 60, abort: { rng: 40, row: 20 } } } };
  const fix = Object.fromEntries([['autshadow_a0', 297], ['autshadow_a1', 3], ['autshadow_a2', 0], ['autshadow_a3', 0], ['autshadow_a4', 60], ['autshadow_a5', 40]].map(([n, v]) => [n, { before: 0, after: v as number }]));
  const gates = monoGates(costLines({ tag: 'q-m', minutes: 10, native: nat, fix }));
  t(gates.some(g => g.startsWith('Mode R match rate: ')) && gates.some(g => g.startsWith('worker aborts by reason: rng 40')) && gates.every(g => !/\[(measured|inferred)/.test(g)), 'mono gate lines from costLines');
  const ctx: Ctx = { log: r => r.job === 'a' ? 'route async: START x\nroute async: START y\nok\n' : null, mono: () => gates,
    hitch: tag => tag === 'q-a' ? { arms: [{ arm: 'all', s: armStats(arm) }], invalid: null } : tag === 'q-v' ? { arms: [], invalid: 'profiler cut short (473 of 600 s)' } : 'no hitch files (x)' };
  const d = digest(rows, jobs, ctx), out = d.lines.join('\n');
  t(d.lines[0] === `a: PASS, 13.1 min from ${hhmm(rows[0].time)}`, 'result line');
  t(out.includes('minCounters 140 (v140) ok, native sypPlans.1>=5 (7) ok, native fbAsync>=1 (0) FAIL, log /route async: START/ >= 2 (2) ok, log /MISMATCH/ <= 0 (0) ok, fixes stuckcheck_rearmed>=1 (47) ok, script errors <= 0 (0) ok'), 'rules with measured values');
  t(out.includes('  hitches: p99 80 ms, max 1200 ms, >=300 2, >=1000 1 (10.0 min)'), 'hitch line from armStats (200 iterations: p99 = 3rd longest)');
  t(/^m: FAIL, 12 min from \d\d:\d\d; crash: TS3W\+0x1DDFBD; profile 10 min; sim clock stuck at day 13 22:33 for ~60 s$/m.test(out), 'fail reasons via classify()');
  t(/^v: VOID, 9 min from \d\d:\d\d; runInvalid: profiler cut short/m.test(out) && d.review.includes('v: VOID, rerun'), 'VOID from the profile run');
  t(out.includes('  rules: none beyond the run verdict') && out.includes('  Mode R match rate'), 'Mono job: gates, no rules');
  t(d.lines.filter(l => !l.startsWith('  ')).length === 4 && d.lines.length <= 4 * 5, 'at most 5 lines per job');
  t(d.failed.join() === 'm,v void' && d.review.includes('zz: not in the queue file') && d.review.includes('m: rule fields not read: someRequire') && d.review.includes('m: no hitch files (x)'), 'failed jobs and needs review');
  t(ruleChecks({ logRequire: { x: 1 } }, {}, null)[0].ok === null && ruleChecks({ nativeRequire: 'a>=1' }, { nativeDelta: {} }, null)[0].text === 'native a>=1 (absent)', 'missing log / counter');
  console.log(`queue-digest self-check: ${k} checks${process.exitCode ? ', FAILED' : ', ok'}`);
}

if (resolve(process.argv[1] ?? '') === import.meta.filename) {
  const { values: v, positionals: p } = parseArgs({ allowPositionals: true, options: { since: { type: 'string' }, queue: { type: 'string' }, json: { type: 'boolean' }, 'self-check': { type: 'boolean' } } });
  if (v['self-check']) selfCheck();
  else if (p.length !== 1) { console.error('usage: node tools/harness/queue-digest.ts <queue tag> [--since HH:MM] [--queue <file.json>] [--json]'); process.exit(2); }
  else {
    const m = p[0].match(/^(.*)-(\d{4}-\d\d-\d\d)$/), name = m ? m[1] : p[0], file = existsSync(QUEUE_DIR) ? queueFile(name, m?.[2]) : null;
    if (!file) { console.log(`needs review: no probe-out/queue/${p[0]}${m ? '' : '-<date>'}.jsonl (set TS3_REPO to the main checkout)`); process.exit(1); }
    const qf = v.queue ?? join(QDEF, `${name}.json`), review: string[] = [];
    const q = existsSync(qf) ? JSON.parse(readFileSync(qf, 'utf8')) : (review.push(`no ${v.queue ?? `tools/harness/queues/${name}.json`} (rules not checked; --queue <file>)`), []);
    const jobs = new Map<string, any>((Array.isArray(q) ? q : q.jobs ?? []).map((j: any) => [String(j.id), j]));
    const rows = jsonlRows(file).filter(x => x.job && (!v.since || hhmm(x.time) >= v.since));
    const d = digest(rows, jobs, realCtx(file));
    if (v.json) console.log(JSON.stringify(d, null, 2));
    else console.log([`${basename(file)}: ${rows.length} jobs${v.since ? ` since ${v.since}` : ''}`, ...d.lines,
      `verdict: ${d.failed.length ? `FAIL (${d.failed.join(', ')})` : rows.length ? 'PASS' : 'no jobs'}`, `needs review: ${[...review, ...d.review].join('; ') || 'none'}`].join('\n'));
    if (d.failed.length || !rows.length) process.exitCode = 1;
  }
}
