// Hitch attribution (docs/96 §9): what part of the sim's long frames is waiting for the route job, from one profile run with
// ts3native [HitchAttr] Enabled=1 (0.34.10+). Reads only; never talks to the game.
//   node tools/harness/hitch-attr-report.ts [<tag or ...-hitches.jsonl> ...]   default: every probe-out/profile/hitch-attr-*-hitches.jsonl
//   node tools/harness/hitch-attr-report.ts --self-check
// Inputs: <tag>-<secs>-hitches.jsonl and -seconds.csv (tools/frida/stutter-profile.js). Each hitch record carries the sim's waits in
// that same iteration (read at the iteration boundary on the sim thread: no clock fit): rtLock (+0x918, gate waits included),
// rtOtherLock (another game lock while a job runs or is queued), rtJob (RtWaitJob destroy / drain, discourage drain), rtPlanWait
// (a plan that waited for the job), rtMaxMs / rtMaxKey (the longest wait and its docs/84 key). The first 60 s after the first
// seconds row are left out (docs/96 class C: load work, no routing).
// Set TS3_REPO=<main checkout> when running from a git worktree.
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = process.env.TS3_REPO ?? join(import.meta.dirname, '..', '..');
const PROFILE = join(ROOT, 'probe-out', 'profile');
const SKIP_MS = 60_000;
const W = ['rtLock', 'rtOtherLock', 'rtJob', 'rtPlanWait'] as const;
// docs/94 §22.10.1 and docs/84-90 wait keys (the leaf's caller; 0x02/0x05/0x06 = our own keys)
const KEYS: Record<string, string> = {
  '0x0085fe10': 'steering obstacle query', '0x00846d21': 'steering avoidance A', '0x0085b50c': 'steering avoidance B',
  '0x0082f0b0': 'goal footprint gather', '0x0062ef7a': 'gate 0 (transform wrapper)', '0x0062f782': 'gate 12 LotTable_Contains',
  '0x00631814': 'gate 12 F-C front door', '0x00631f40': 'gate 12 F-G placement', '0x0062fb74': 'gate 12 GetIfPresent',
  '0x00760bff': 'gate 10 goal step (sync plan)', '0x00833593': 'UpdateSimFootprint', '0x008334cc': 'ObjectFootprintFromTool',
  '0x0082d296': 'UpdateObjectLot', '0x0082a0f6': 'RemovePrivateFootprint', '0x00834890': 'discourage remove',
  '0x008343b9': 'discourage add', '0x02000003': 'destroy (RtWaitJob)', '0x05000000': 'plan waited for the job',
  '0x06000000': 'discourage drain', '0x0080e5b0': 'steering rebuild (P0)',
  '0x07000000': 'pending lot updates at START (FbApplyPending)',
};
export const keyName = (k: string) => KEYS[k] ?? (k.startsWith('0x02') ? 'RtWaitJob drain' : k);

type H = Record<string, any> & { t: number; wallMs: number };
export type Run = { name: string; secs: number; hitches: H[]; rows: Record<string, number>[] };
const wait = (h: H) => W.reduce((s, k) => s + (h[k] ?? 0), 0);

export function analyze(r: Run) {
  const t0 = r.rows.length ? r.rows[0].t : (r.hitches[0]?.t ?? 0);
  const rows = r.rows.filter(x => x.t - t0 >= SKIP_MS), hs = r.hitches.filter(h => h.t - t0 >= SKIP_MS);
  const secs = Math.max(rows.length, 1), hours = secs / 3600;
  const perSec = Object.fromEntries(W.map(k => [k, rows.reduce((s, x) => s + (x[k] ?? 0), 0) / secs]));
  const tier = (min: number) => {
    const sel = hs.filter(h => h.wallMs >= min);
    const wall = sel.reduce((s, h) => s + h.wallMs, 0), w = sel.reduce((s, h) => s + wait(h), 0);
    const lock = sel.reduce((s, h) => s + (h.rtLock ?? 0), 0);
    const byKey = new Map<string, { n: number; ms: number }>();
    for (const h of sel) if (h.rtMaxKey) {
      const e = byKey.get(h.rtMaxKey) ?? { n: 0, ms: 0 };
      e.n++, e.ms += h.rtMaxMs ?? 0, byKey.set(h.rtMaxKey, e);
    }
    return {
      min, n: sel.length, perH: sel.length / hours, wallMs: wall, waitMs: w, lockMs: lock, share: wall ? w / wall : 0,
      waitMsPerH: w / hours, wallMsPerH: wall / hours,
      dominated: sel.filter(h => wait(h) >= h.wallMs / 2).length,           // waits are at least half the frame
      wouldDrop: sel.filter(h => h.wallMs - wait(h) < min).length,           // below the threshold without any wait
      wouldDropLock: sel.filter(h => h.wallMs - (h.rtLock ?? 0) < min).length, // ... without the +0x918 waits alone
      keys: [...byKey].sort((a, b) => b[1].ms - a[1].ms).slice(0, 8),
    };
  };
  const active = rows.some(x => W.some(k => x[k])) || hs.some(h => W.some(k => h[k]));
  return { name: r.name, secs, perSec, waitPerSec: W.reduce((s, k) => s + perSec[k], 0), t50: tier(50), t100: tier(100), active };
}

const f1 = (x: number) => x.toFixed(1);
export function report(a: ReturnType<typeof analyze>): string {
  const L = [`## ${a.name} (${f1(a.secs / 60)} min after the first minute)`];
  if (!a.active) L.push('WARNING: no rt* data (the build is older than 0.34.10, [HitchAttr] Enabled=0, or no job ran): numbers below are 0');
  L.push(`sim waits for the route job, all frames: ${f1(a.waitPerSec)} ms/s (+0x918 ${f1(a.perSec.rtLock)}, other lock ${f1(a.perSec.rtOtherLock)}, ` +
    `job wait ${f1(a.perSec.rtJob)}, plan waited ${f1(a.perSec.rtPlanWait)})`);
  for (const t of [a.t50, a.t100]) {
    L.push(`>= ${t.min} ms: ${t.n} hitches (${f1(t.perH)}/h), ${Math.round(t.wallMs)} ms wall (${Math.round(t.wallMsPerH)} ms/h); route-job waits ` +
      `${Math.round(t.waitMs)} ms (${(100 * t.share).toFixed(1)}%, ${Math.round(t.waitMsPerH)} ms/h; +0x918 ${Math.round(t.lockMs)} ms), rest ` +
      `${Math.round(t.wallMs - t.waitMs)} ms; wait >= half the frame: ${t.dominated}; below ${t.min} ms without the waits: ${t.wouldDrop} ` +
      `(without +0x918 only: ${t.wouldDropLock})`);
    if (t.keys.length) L.push(`  longest wait per hitch by key: ${t.keys.map(([k, e]) => `${keyName(k)} ${k} ${e.n}x ${Math.round(e.ms)} ms`).join('; ')}`);
  }
  // docs/96 §9.3 rule (Rodney 2026-10-19): a stable P5c ships if it saves ~20 ms/s of sim time or measurably cuts >= 50 ms hitches.
  // These are caps: P5c can at most remove the +0x918 waits it admits, never the rest of the frame.
  L.push(`P5c cap: +0x918 ${f1(a.perSec.rtLock)} ms/s of sim time (rule ~20 ms/s: ${a.perSec.rtLock >= 20 ? 'reachable' : 'not reachable'}); ` +
    `>= 50 ms hitches it could remove at most: ${a.t50.wouldDropLock} of ${a.t50.n}, ${Math.round(a.t50.lockMs)} ms of ${Math.round(a.t50.wallMs)} ms`);
  return L.join('\n');
}

function load(arg: string): Run {
  const hf = arg.endsWith('-hitches.jsonl') ? arg : (() => {
    const base = arg.split(/[\\/]/).pop()!;
    const m = readdirSync(PROFILE).find(n => n.startsWith(`${base}-`) && /^\d+-hitches\.jsonl$/.test(n.slice(base.length + 1)));
    if (!m) throw new Error(`no ${base}-<secs>-hitches.jsonl in ${PROFILE}`);
    return join(PROFILE, m);
  })();
  const p = existsSync(hf) ? hf : join(PROFILE, hf);
  const hitches = readFileSync(p, 'utf8').split('\n').filter(Boolean).map(l => JSON.parse(l));
  const cf = p.replace('-hitches.jsonl', '-seconds.csv');
  const rows = existsSync(cf) ? (() => {
    const [head, ...ls] = readFileSync(cf, 'utf8').trim().split(/\r?\n/), c = head.split(',');
    return ls.map(l => Object.fromEntries(l.split(',').map((v, i) => [c[i], Number(v)])));
  })() : [];
  const name = p.split(/[\\/]/).pop()!.replace(/-\d+-hitches\.jsonl$/, '');
  return { name, secs: rows.length, hitches, rows };
}

function selfCheck() {
  const assert = (c: boolean, m: string) => { if (!c) throw new Error(`self-check: ${m}`); };
  const rows = Array.from({ length: 120 }, (_, i) => ({ t: i * 1000, rtLock: i >= 60 ? 30 : 99, rtOtherLock: 0, rtJob: i >= 60 ? 5 : 0, rtPlanWait: 0 }));
  const hitches: H[] = [
    { t: 30_000, wallMs: 500, rtLock: 400, rtMaxMs: 400, rtMaxKey: '0x0085fe10' },                       // first minute: out
    { t: 70_000, wallMs: 120, rtLock: 80, rtJob: 0, rtMaxMs: 60, rtMaxKey: '0x0085fe10' },               // drops below 50 and 100
    { t: 80_000, wallMs: 60, rtLock: 5, rtMaxMs: 5, rtMaxKey: '0x0062ef7a' },                            // stays
    { t: 90_000, wallMs: 300, rtLock: 0, rtJob: 200, rtMaxMs: 200, rtMaxKey: '0x02000003' },             // job wait, not +0x918: 100 ms left, stays
    { t: 95_000, wallMs: 55 },                                                                            // no waits
  ];
  const a = analyze({ name: 'self', secs: 120, hitches, rows });
  assert(a.secs === 60 && Math.abs(a.perSec.rtLock - 30) < 1e-9 && Math.abs(a.waitPerSec - 35) < 1e-9, 'per-second sums skip the first minute');
  assert(a.t50.n === 4 && a.t50.wallMs === 535 && a.t50.waitMs === 285 && a.t50.lockMs === 85, '>= 50 ms tier');
  assert(a.t50.dominated === 2 && a.t50.wouldDrop === 1 && a.t50.wouldDropLock === 1, '>= 50 ms counterfactuals');
  assert(a.t100.n === 2 && a.t100.waitMs === 280 && a.t100.wouldDrop === 1 && a.t100.wouldDropLock === 1, '>= 100 ms tier');
  assert(a.t50.keys[0][0] === '0x02000003' && a.t50.keys[0][1].ms === 200 && a.t50.keys.length === 3, 'keys by longest-wait ms');
  assert(Math.abs(a.t50.perH - 240) < 1e-9 && a.active, 'rate per hour');
  const txt = report(a);
  assert(txt.includes('destroy (RtWaitJob)') && txt.includes('(rule ~20 ms/s: reachable)') && !txt.includes('WARNING'), 'report text');
  assert(report(analyze({ name: 'old', secs: 0, hitches: [{ t: 0, wallMs: 80 }], rows: [] })).includes('WARNING'), 'old build warns');
  console.log('hitch-attr-report self-check ok');
}

if (process.argv.includes('--self-check')) selfCheck();
else if (resolve(process.argv[1] ?? '') === fileURLToPath(import.meta.url)) { // not when imported (sim-budget-report.ts)
  const args = process.argv.slice(2);
  const files = args.length ? args : existsSync(PROFILE) ? readdirSync(PROFILE).filter(n => /^hitch-attr-.*-\d+-hitches\.jsonl$/.test(n)) : [];
  if (!files.length) { console.error(`no hitch-attr-*-hitches.jsonl in ${PROFILE} (set TS3_REPO from a worktree, or pass tags)`); process.exit(2); }
  console.log(files.map(f => report(analyze(load(f)))).join('\n\n'));
}
