// Read-only verdict for queues/sim-gone6.json: the first load-time event of a household Sim that differs between the arms.
//   node tools/harness/sim-gone-diff.ts [sim-gone6]         reads probe-out/queue/<tag>-*-<job>-{pair,noours}-<n>.log (newest per job)
//   node tools/harness/sim-gone-diff.ts --self-check
// An event counts for an arm only when it is in every run of that arm, so autonomy noise drops out. Exit 1: a run has no
// sim_gone_load result (log line cut, watcher not armed, game died), then the arm split is not proven.
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join, resolve } from 'node:path';
import assert from 'node:assert/strict';

const ROOT = resolve(import.meta.dirname, '..', '..');
type Ev = { ms: number; sim: string | null; kind: string; what: string };
type Run = { job: string; arm: string; res: any };

export const norm = (e: Ev) => `${e.kind} ${e.what}`.replace(/#[0-9A-F]+/g, '').replace(/ left=[\d.]+/, '').trim();

// the result JSON of the last sim_gone_load step or cleanup line; null when missing or cut
export function parseLog(text: string): any {
  let last: string | null = null;
  for (const l of text.split('\n')) {
    const m = /(?:step sim_gone_load \{\}: |cleanup sim_gone_load: )(\{.*)$/.exec(l.trimEnd());
    if (m) last = m[1];
  }
  try { return last ? JSON.parse(last) : null; } catch { return null; }
}

// events of one Sim present in every run, in the order of the first run
export function stable(runs: Ev[][]): Ev[] {
  const counts = runs.map(r => { const c = new Map<string, number>(); for (const e of r) c.set(norm(e), (c.get(norm(e)) ?? 0) + 1); return c; });
  const used = new Map<string, number>(), out: Ev[] = [];
  for (const e of runs[0] ?? []) {
    const k = norm(e), n = (used.get(k) ?? 0) + 1;
    if (counts.every(c => (c.get(k) ?? 0) >= n)) { out.push(e); used.set(k, n); }
  }
  return out;
}

export function firstDiff(a: Ev[], b: Ev[]): { i: number; a: Ev | null; b: Ev | null } | null {
  for (let i = 0; i < Math.max(a.length, b.length); i++)
    if (!a[i] || !b[i] || norm(a[i]) !== norm(b[i])) return { i, a: a[i] ?? null, b: b[i] ?? null };
  return null;
}

const evs = (r: Run): Ev[] => r.res?.sim_gone_load_events ?? [];

// The Sim's saved stack as noours restored it; * = frame's checksum the pair dropped and noours did not: the swap that refused it
export function refusedFrames(sim: string, pair: Run[], noours: Run[]): string[] {
  const dropped = (r: Run): string[] => r.res?.sim_gone_load_methods?.droppedChecksums ?? [];
  const frames: string[] = noours.map(r => r.res?.sim_gone_load_saved_frames?.[sim] ?? []).find(f => f.length) ?? [];
  if (!frames.length) return [`  ${sim}: no saved frames in noours`];
  const ours = new Set(pair.flatMap(dropped)), theirs = new Set(noours.flatMap(dropped));
  return [`  ${sim} saved stack: ${frames.map(f => { const c = f.split(' ')[1]; return ours.has(c) && !theirs.has(c) ? `${f} *` : f; }).join(' | ')}`];
}

export function verdict(pair: Run[], noours: Run[]): { lines: string[]; ok: boolean } {
  const lines: string[] = [], all = [...pair, ...noours], bad = all.filter(r => !r.res);
  for (const r of bad) lines.push(`MISSING: ${r.job} has no sim_gone_load result`);
  const sims = [...new Set(all.flatMap(evs).map(e => e.sim).filter((s): s is string => !!s))];
  const stackRate = (runs: Run[], s: string) => runs.filter(r => evs(r).some(e => e.sim === s && e.kind === 'stackfail')).length;
  const diffs: { sim: string; a: Ev | null; b: Ev | null; i: number }[] = [];
  for (const s of sims) {
    const a = stable(pair.map(r => evs(r).filter(e => e.sim === s))), b = stable(noours.map(r => evs(r).filter(e => e.sim === s)));
    const d = firstDiff(a, b);
    lines.push(`${s}: stack refused at load pair ${stackRate(pair, s)}/${pair.length}, noours ${stackRate(noours, s)}/${noours.length}`);
    if (d) { diffs.push({ sim: s, ...d }); lines.push(`  first diff at event ${d.i}: pair [${d.a ? `${d.a.ms} ms ${norm(d.a)}` : 'none'}] | noours [${d.b ? `${d.b.ms} ms ${norm(d.b)}` : 'none'}]`); }
    else lines.push('  no stable difference');
  }
  for (const [name, runs] of [['pair', pair], ['noours', noours]] as const) {
    const m = runs.map(r => r.res?.sim_gone_load_methods).filter(Boolean);
    lines.push(`${name}: method table captured ${m.length}/${runs.length}, invalid tasks ${m.map((x: any) => x.invalidTasks).join('/') || '?'}, dropped methods ${m.map((x: any) => x.droppedMethods).join('/') || '?'}`);
    const hooks = [...new Set(m.flatMap((x: any) => x.droppedHooks ?? []))];
    if (hooks.length) lines.push(`  our hooks on refused frames: ${hooks.join(', ')}`);
    const miss = runs.map(r => r.res?.sim_gone_load_capture_miss).filter(Boolean);
    if (miss.length) lines.push(`  capture miss: ${[...new Set(miss)].join('; ')}`);
    lines.push(`  failed objects: ${JSON.stringify(runs[0]?.res?.sim_gone_load_failed_types ?? {})}`);
    lines.push(`  ts3fixes counters moved: ${(runs[0]?.res?.sim_gone_load_hooks ?? []).join(' ') || '-'}`);
  }
  const ca = pair[0]?.res?.sim_gone_load_checksums, cb = noours[0]?.res?.sim_gone_load_checksums;
  if (ca && cb) for (const k of Object.keys(ca)) lines.push(`checksum ${k}: pair ${ca[k]} noours ${cb[k] ?? '?'} ${ca[k] === cb[k] ? 'same' : 'DIFFERS'}`);
  const refused = sims.filter(s => stackRate(pair, s) === pair.length && pair.length > 0 && stackRate(noours, s) === 0);
  for (const s of refused) lines.push(...refusedFrames(s, pair, noours));
  lines.push(refused.length ? `VERDICT: saved task stack refused at load in every pair run, in no noours run: ${refused.join(', ')}. Load reset, not a route cancel.`
    : diffs.length ? `VERDICT: not the stack check; first differing event: ${diffs[0].sim} ${diffs[0].a ? norm(diffs[0].a) : 'none'} vs ${diffs[0].b ? norm(diffs[0].b) : 'none'}`
    : 'VERDICT: no stable difference between the arms');
  return { lines, ok: bad.length === 0 && pair.length > 0 && noours.length > 0 };
}

function load(tag: string, dir: string): { pair: Run[]; noours: Run[] } {
  const newest = new Map<string, { f: string; t: number }>();
  for (const f of readdirSync(dir)) {
    const m = new RegExp(`^${tag}-.*-(\\w+-(?:pair|noours)-\\d+)\\.log$`).exec(f);
    if (!m) continue;
    const t = statSync(join(dir, f)).mtimeMs;
    if (t > (newest.get(m[1])?.t ?? 0)) newest.set(m[1], { f, t });
  }
  const runs = [...newest].map(([job, { f }]) => ({ job, arm: /pair/.test(job) ? 'pair' : 'noours', res: parseLog(readFileSync(join(dir, f), 'utf8')) }));
  return { pair: runs.filter(r => r.arm === 'pair'), noours: runs.filter(r => r.arm === 'noours') };
}

function selfCheck() {
  const ev = (ms: number, sim: string, kind: string, what: string): Ev => ({ ms, sim, kind, what });
  assert.equal(norm(ev(1, 'a', 'chain', 'Sit#E201D708 x left=28.8 prev=null')), 'chain Sit x prev=null');
  const res = (e: Ev[]) => ({ sim_gone_load_events: e, sim_gone_load_methods: { invalidTasks: 3, droppedMethods: 2, droppedHooks: ['RflRouteHook.DoRouteOrig'] } });
  assert.deepEqual(parseLog(`x\n00:01 step sim_gone_load {}: {"a":1}\n00:02 cleanup sim_gone_load: {"a":2}\n`), { a: 2 });
  assert.equal(parseLog('00:01 step sim_gone_load {}: {"a":1'), null); // cut line
  assert.equal(stable([[ev(1, 'L', 'x', 'a'), ev(2, 'L', 'x', 'b')], [ev(3, 'L', 'x', 'b')]]).length, 1);
  const refused = ev(-1, 'Lala', 'stackfail', 'Sit PortalRouteThroughAction prev=null');
  const sit = ev(0, 'Lala', 'chain', 'PortalRouteThroughAction prev=null left=28.8');
  const pair = [1, 2].map(i => ({ job: `p${i}`, arm: 'pair', res: res([refused, ev(0, 'Lala', 'chain', 'prev=null left=0.0')]) }));
  const noours = [1, 2].map(i => ({ job: `n${i}`, arm: 'noours', res: res([sit]) }));
  const v = verdict(pair, noours);
  assert.ok(v.ok && v.lines.at(-1)!.includes('stack refused') && v.lines.some(l => l.includes('RflRouteHook.DoRouteOrig')));
  const fr = (d: string[], f?: string[]) => ({ job: 'x', arm: 'x', res: { sim_gone_load_methods: { droppedChecksums: d }, sim_gone_load_saved_frames: { Lala: f } } });
  assert.equal(refusedFrames('Lala', [fr(['AA', 'BB'])], [fr(['BB'], ['R.DoRoute AA', 'S.Sim BB', 'T.Run CC'])])[0], '  Lala saved stack: R.DoRoute AA * | S.Sim BB | T.Run CC');
  assert.ok(refusedFrames('Lala', [fr(['AA'])], [fr([])])[0].includes('no saved frames'));
  const w = verdict([{ job: 'p', arm: 'pair', res: res([ev(0, 'L', 'exit', 'a')]) }], [{ job: 'n', arm: 'noours', res: res([ev(0, 'L', 'exit', 'b')]) }]);
  assert.ok(w.lines.at(-1)!.includes('not the stack check'));
  assert.equal(verdict([{ job: 'p', arm: 'pair', res: null }], []).ok, false);
  console.log('sim-gone-diff self-check ok');
}

if (process.argv.includes('--self-check')) selfCheck();
else {
  const tag = process.argv[2] ?? 'sim-gone6', { pair, noours } = load(tag, join(ROOT, 'probe-out', 'queue'));
  const { lines, ok } = verdict(pair, noours);
  console.log(`${tag}: pair ${pair.length} runs, noours ${noours.length} runs`);
  for (const l of lines) console.log(l);
  if (!ok) process.exit(1);
}
