// Fills the verdict column of docs/51-inference-ledger.md from finished verify runs (tools/harness/queues/verify.json), where
// the row's rule is mechanical; every other row is printed for review and left as it is.
//   node tools/harness/verify-verdicts.ts --run verify-2026-10-16-bp-verify [--run verify-2026-10-16-ip-verify] [--write] [--force]
//   node tools/harness/verify-verdicts.ts --self-check
// A run id is <queue>-<date>-<job> (the queue's profile tag): it reads probe-out/queue/<run>-native.json and
// probe-out/profile/<run>-mainstack.jsonl. Live window: counts and *Us are `after` - `before` (the dump after the load, modulo
// 2^32 for wrapped LONGs); maxima (*Max*) and levels (vfMem*, Ready, Mode, Tid, Ret<i>, Objects, Slice) are the `after` value,
// and the maxima cover play only when the queue zeroed them after the load (dump maxReset: true, queue.ts with Verify.Enabled).
// Several runs add counts up and take the largest maximum or level.
// vf key convention (docs/51 §2): vf<Group><Target><Suffix>, Target = a hex address without 0x (D5D910) or a short token (Device,
// R1); thread-role suffix Main / Sim / Other (Job* and Off count as Other); race keys end in Race or Overlap; costs MaxUs; memory
// keys vfMem<Target>Bytes. Rules (ledger column "rule"):
//   main:T sim:T      only that thread hit T: confirmed; another role hit it: refuted; no hits: no data
//   offmain:T         Sim + Other > 0: confirmed; Main only: refuted
//   both:T            Main > 0 and Sim > 0: confirmed; one side only: review
//   race:T            any key ending in Race or Overlap > 0: confirmed; all 0: review (not seen is not proof)
//   max:T<N max:T>N   largest *MaxUs key below (<) or above (>) N us: confirmed, else refuted
//   seen:T            any role key (Main/Sim/Other) of T > 0: confirmed; all 0: review (not seen is not proof)
//   none:T/D          T's role keys all 0 while D's role keys > 0: confirmed; T > 0: refuted; D = 0: no data
//   mem:T<N           vfMem*T* bytes < N MB: confirmed, else refuted; a negative value (-1 = no readout) is no data
//   stackmain:A[-B]   main-thread samples with a frame in function A (FUN_00A) or address range A-B: > 0 confirmed, 0 review
//   mainshare:A-B>P   share (%) of main samples with a frame in A-B above (>) or below (<) P: confirmed, else refuted
//   roles:T cost:T    print the matching keys for review, no verdict
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { parseArgs } from 'node:util';

const ROOT = resolve(import.meta.dirname, '..', '..');
const LEDGER = join(ROOT, 'docs', '51-inference-ledger.md');
type Counters = Record<string, number>;
type Verdict = { verdict: string | null; detail: string };

const keysFor = (c: Counters, t: string) => Object.entries(c).filter(([k, v]) => /^vf/i.test(k) && k.toLowerCase().includes(t.toLowerCase()) && typeof v === 'number');
const role = (k: string) => /main$/i.test(k) ? 'Main' : /sim$/i.test(k) ? 'Sim' : /(other|off|job\w*)$/i.test(k) ? 'Other' : null;
const show = (ks: [string, number][]) => ks.map(([k, v]) => `${k}=${v}`).join(', ') || 'no vf keys';
// keys read as they are at the end (not differenced): maxima and levels
const isLevel = (k: string) => /max|bytes$|ready\d?$|mode$|tid$|ret\d+$|objects$|slice$|startup$/i.test(k);

// one run's vf keys over the live window (see the header); `before` missing: the cumulative `after`
export function live(before: Counters | null | undefined, after: Counters): Counters {
  const r: Counters = {};
  for (const [k, v] of Object.entries(after)) {
    if (typeof v !== 'number' || !/^vf/i.test(k)) continue;
    const b = before?.[k];
    r[k] = isLevel(k) || typeof b !== 'number' ? v : ((v - b) % 4294967296 + 4294967296) % 4294967296;
  }
  return r;
}
export function merge(sum: Counters, c: Counters): void {
  for (const [k, v] of Object.entries(c)) sum[k] = k in sum ? (isLevel(k) ? Math.max(sum[k], v) : sum[k] + v) : v;
}
// frames look like "0x4e27a9 TS3W.exe!FUN_004e2760+0x49"
function inFrame(f: string, a: number, b: number | null): boolean {
  if (b === null) return new RegExp(`FUN_0*${a.toString(16)}\\b`, 'i').test(f);
  const x = parseInt(f.split(' ')[0], 16);
  return x >= a && x < b;
}

export function judge(rule: string, c: Counters, main: string[][]): Verdict {
  const m = rule.match(/^(\w+):(.+)$/);
  if (!m) return { verdict: null, detail: `no rule` };
  const [, kind, arg] = m;
  if (kind === 'none') {
    const [t, d] = arg.split('/'), roleSum = (x: string) => keysFor(c, x).reduce((s, [k, v]) => s + (role(k) ? v : 0), 0);
    if (!d) return { verdict: null, detail: `bad rule ${rule}` };
    const n = roleSum(t), den = roleSum(d), det = `${show(keysFor(c, t).filter(([k]) => role(k)))}; ${d} calls ${den}`;
    return { verdict: n > 0 ? 'refuted' : den > 0 ? 'confirmed' : null, detail: den > 0 || n > 0 ? det : `no data (${det})` };
  }
  if (['main', 'sim', 'offmain', 'both', 'roles', 'cost', 'seen'].includes(kind)) {
    const ks = keysFor(c, arg), sum = { Main: 0, Sim: 0, Other: 0 };
    for (const [k, v] of ks) { const r = role(k); if (r) sum[r] += v; }
    const d = show(ks), hit = sum.Main + sum.Sim + sum.Other;
    if (kind === 'roles' || kind === 'cost') return { verdict: null, detail: d };
    if (!hit) return { verdict: null, detail: `no data (${d})` };
    if (kind === 'seen') return { verdict: 'confirmed', detail: d };
    if (kind === 'offmain') return { verdict: sum.Sim + sum.Other > 0 ? 'confirmed' : 'refuted', detail: d };
    if (kind === 'both') return { verdict: sum.Main > 0 && sum.Sim > 0 ? 'confirmed' : null, detail: d };
    const want = kind === 'main' ? 'Main' : 'Sim';
    return { verdict: hit === sum[want] ? 'confirmed' : 'refuted', detail: d };
  }
  if (kind === 'race') {
    const ks = keysFor(c, arg).filter(([k]) => /(race|overlap)$/i.test(k));
    if (!ks.length) return { verdict: null, detail: 'no race keys' };
    return { verdict: ks.some(([, v]) => v > 0) ? 'confirmed' : null, detail: show(ks) };
  }
  if (kind === 'max' || kind === 'mem') {
    const op = kind === 'max' && arg.includes('>') ? '>' : '<', [t, n] = arg.split(op), lim = Number(n);
    const ks = keysFor(c, t).filter(([k, v]) => kind === 'max' ? /maxus$/i.test(k) : /^vfmem/i.test(k) && v >= 0);
    if (!ks.length || !Number.isFinite(lim)) return { verdict: null, detail: `no data (${show(keysFor(c, t))})` };
    const v = Math.max(...ks.map(([, x]) => x)), val = kind === 'mem' ? v / 1048576 : v;
    return { verdict: (op === '>' ? val > lim : val < lim) ? 'confirmed' : 'refuted', detail: `${show(ks)}${kind === 'mem' ? ` (${val.toFixed(1)} MB)` : ''}` };
  }
  if (kind === 'stackmain' || kind === 'mainshare') {
    const r = arg.match(/^([0-9a-f]+)(?:-([0-9a-f]+))?(?:([<>])([\d.]+))?$/i);
    if (!r) return { verdict: null, detail: `bad rule ${rule}` };
    if (!main.length) return { verdict: null, detail: 'no main-thread samples' };
    const a = parseInt(r[1], 16), b = r[2] ? parseInt(r[2], 16) : null;
    const n = main.filter(s => s.some(f => inFrame(f, a, b))).length, pct = 100 * n / main.length;
    const d = `${n} of ${main.length} main samples (${pct.toFixed(2)}%)`;
    if (kind === 'stackmain') return { verdict: n > 0 ? 'confirmed' : null, detail: d };
    if (!r[3]) return { verdict: null, detail: d };
    return { verdict: (r[3] === '>' ? pct > Number(r[4]) : pct < Number(r[4])) ? 'confirmed' : 'refuted', detail: d };
  }
  return { verdict: null, detail: `unknown rule kind ${kind}` };
}

// ledger rows: | id | doc § | claim | how to test | repro | expected if true | rule | verdict |
export function fill(md: string, c: Counters, main: string[][], label: string, force = false): { md: string; log: string[] } {
  const log: string[] = [];
  const out = md.split(/\r?\n/).map(line => {
    const cells = line.split('|');
    if (!/^\|\s*I-\d+-\d+\s*\|/.test(line) || cells.length !== 10) return line;
    const id = cells[1].trim(), rule = cells[7].replace(/`/g, '').trim(), old = cells[8].trim();
    if (!rule || rule === '-') return line;
    const v = judge(rule, c, main);
    log.push(`${id} ${rule}: ${v.verdict ?? 'review'} (${v.detail})`);
    if (!v.verdict || (old && !force)) return line;
    cells[8] = ` ${v.verdict} (${label}: ${v.detail.replace(/\|/g, '/')}) `;
    return cells.join('|');
  });
  return { md: out.join(md.includes('\r\n') ? '\r\n' : '\n'), log };
}

const { values } = parseArgs({ options: { run: { type: 'string', multiple: true }, write: { type: 'boolean' }, force: { type: 'boolean' }, 'self-check': { type: 'boolean' } } });

if (values['self-check']) {
  const assert = (await import('node:assert')).strict;
  const c = { version: 20, vfThrD5D910Sim: 3, vfThrD5D910Main: 0, vfThr5AD8F0Main: 9, vfThrC688A0Main: 2, vfThrC688A0Sim: 1, vfRaceC2A840Overlap: 0,
    vfRace4B1AE0Race: 2, vfCostABFAC0MaxUs: 420, vfMemSpeedTreeBytes: 3 * 1048576, vfThr7135B0Main: 5, vfThr7135B0Job0: 1 };
  const main = [['0x611700 TS3W.exe!FUN_00611680+0x80'], ['0x50b4d0 TS3W.exe!FUN_0050b4c0+0x10'], ['0x611690 TS3W.exe!FUN_00611680+0x10']];
  const j = (r: string) => judge(r, c, main).verdict;
  assert.equal(j('offmain:D5D910'), 'confirmed');
  assert.equal(j('main:5AD8F0'), 'confirmed');
  assert.equal(j('main:D5D910'), 'refuted');
  assert.equal(j('main:7135B0'), 'refuted'); // a job thread hit it too
  assert.equal(j('sim:123456'), null);
  assert.equal(j('both:C688A0'), 'confirmed');
  assert.equal(j('race:C2A840'), null);
  assert.equal(j('race:4B1AE0'), 'confirmed');
  assert.equal(j('max:ABFAC0<1000'), 'confirmed');
  assert.equal(j('max:ABFAC0<100'), 'refuted');
  assert.equal(j('max:ABFAC0>100'), 'confirmed'); // "expected above N" (I-36-1)
  assert.equal(j('max:ABFAC0>1000'), 'refuted');
  const c2 = { vfEvPumpSamePassSim: 3, vfEvRcInlineSim: 0, vfEvLotAddManagedSim: 0, vfEvLotAddManagedMain: 0, vfThrAC1130Sim: 40, vfThrAC1130Us: 900,
    vfThrAC1130MaxUs: 80, vfMonoD71900Sim: 0 };
  assert.equal(judge('seen:PumpSamePass', c2, []).verdict, 'confirmed');
  assert.equal(judge('seen:RcInline', c2, []).verdict, null);
  assert.equal(judge('none:LotAddManaged/AC1130', c2, []).verdict, 'confirmed');
  assert.equal(judge('none:LotAddManaged/AC1130', { ...c2, vfEvLotAddManagedSim: 1 }, []).verdict, 'refuted');
  assert.equal(judge('none:LotAddManaged/AC1130', { ...c2, vfThrAC1130Sim: 0 }, []).verdict, null); // no lot adds: no data
  assert.equal(judge('none:LotAddManaged', c2, []).verdict, null);
  assert.deepEqual(live({ vfCost799780Startup: 3000, vfReady3: 5 }, { vfCost799780Startup: 3000, vfReady3: 7 }), { vfCost799780Startup: 3000, vfReady3: 7 });
  assert.equal(j('mem:SpeedTree<16'), 'confirmed');
  assert.equal(j('stackmain:611680'), 'confirmed');
  assert.equal(j('stackmain:7135B0'), null);
  assert.equal(j('mainshare:611000-612000>50'), 'confirmed');
  assert.equal(j('mainshare:611000-612000<50'), 'refuted');
  assert.equal(j('roles:D5D910'), null);
  assert.equal(judge('stackmain:611680', c, []).verdict, null);
  assert.equal(judge('race:C2A840', { vfRaceC2A840Calls: 128, vfRaceC2A840Race: 0 }, []).verdict, null); // a Calls key is no race key
  assert.equal(judge('race:Scene', { vfRaceSceneOverlap: 1 }, []).verdict, 'confirmed');
  assert.equal(judge('mem:SpeedTree<16', { vfMemSpeedTreeBytes: -1 }, []).verdict, null);
  assert.equal(judge('main:C2A840', { vfRaceC2A840Calls: 9, vfThrC2A840Main: 4, vfThrC2A840OffDevice: 3 }, []).verdict, 'confirmed');
  const lv = live({ vfThrD5D910Sim: 10, vfThrD5D910Us: 4294967000, vfThrD5D910MaxUs: 900, vfMemResCacheBytes: 5 },
    { vfThrD5D910Sim: 13, vfThrD5D910Us: 200, vfThrD5D910MaxUs: 40, vfMemResCacheBytes: 7, vfNew: 2, other: 5 });
  assert.deepEqual(lv, { vfThrD5D910Sim: 3, vfThrD5D910Us: 496, vfThrD5D910MaxUs: 40, vfMemResCacheBytes: 7, vfNew: 2 });
  const sm: Counters = {};
  merge(sm, lv); merge(sm, { vfThrD5D910Sim: 1, vfThrD5D910MaxUs: 90, vfMemResCacheBytes: 3 });
  assert.deepEqual(sm, { vfThrD5D910Sim: 4, vfThrD5D910Us: 496, vfThrD5D910MaxUs: 90, vfMemResCacheBytes: 7, vfNew: 2 });
  const md = '| id | doc § | claim | how to test | repro | expected if true | rule | verdict |\r\n|---|---|---|---|---|---|---|---|\r\n' +
    '| I-43-3 | 43 §1 | thumbs | vf threads | verify-thumbs-save | Sim > 0 | `offmain:D5D910` |  |\r\n| I-38-3 | 38 §6 | swap | vf races | verify-pan | overlap | `race:C2A840` |  |\r\n' +
    '| I-34-1 | 34 §1 | 4.x | static only | - | - | - |  |\r\n| I-42-1 | 42 §3 | main | vf | verify-outfits | Main only | `main:5AD8F0` | refuted (old) |\r\n';
  const f = fill(md, c, main, 'bp');
  assert.match(f.md, /\| I-43-3 \|.*\| confirmed \(bp: vfThrD5D910Sim=3, vfThrD5D910Main=0\) \|\r\n/);
  assert.match(f.md, /\| I-38-3 \|.*`race:C2A840` \|  \|/);
  assert.match(f.md, /refuted \(old\)/);
  assert.equal(f.log.length, 3);
  assert.match(fill(md, c, main, 'bp', true).md, /\| I-42-1 \|.*\| confirmed \(bp:/);
  console.log('self-check ok');
  process.exit(0);
}

if (!values.run?.length) { console.error('usage: node tools/harness/verify-verdicts.ts --run <queue>-<date>-<job> [--run ...] [--write] [--force] | --self-check'); process.exit(2); }
const sum: Counters = {}, main: string[][] = [], skipped: string[] = [];
for (const run of values.run) {
  const nf = join(ROOT, 'probe-out', 'queue', `${run}-native.json`), mf = join(ROOT, 'probe-out', 'profile', `${run}-mainstack.jsonl`);
  if (!existsSync(nf)) { console.error(`${run}: no ${nf} (did the job run with minCounters?)`); process.exit(2); }
  const dump = JSON.parse(readFileSync(nf, 'utf8')), after = dump.after ?? {};
  // docs/236 #22: no [Verify] counters or no "before" snapshot = load-time counts would be credited to play: no verdict, unless --force
  if (!(Number(after.version) >= 20)) { console.error(`${run}: /native version ${after.version ?? '?'}: no [Verify] counters${values.force ? ' (--force: used anyway)' : ': skipped (--force to use anyway)'}`); if (!values.force) { skipped.push(run); continue; } }
  if (!dump.before) { console.error(`${run}: no /native before: counts include the world load${values.force ? ' (--force: used anyway)' : ': skipped (--force to use anyway)'}`); if (!values.force) { skipped.push(run); continue; } }
  if (!dump.maxReset) console.error(`${run}: maxima were not reset after the load (dump without maxReset): *Max* include the load`);
  merge(sum, live(dump.before, after));
  if (existsSync(mf)) for (const l of readFileSync(mf, 'utf8').split('\n')) if (l.trim()) { try { main.push(JSON.parse(l).stack ?? []); } catch { /* torn line */ } }
  else console.error(`${run}: no ${mf} (stack rules give no verdict)`);
}
if (skipped.length && skipped.length === values.run.length) process.exit(2);
const r = fill(readFileSync(LEDGER, 'utf8'), sum, main, values.run.join('+'), values.force);
for (const l of r.log) console.log(l);
console.log(`${Object.keys(sum).length} vf keys, ${main.length} main samples; ${r.log.filter(l => / (confirmed|refuted) /.test(l)).length} mechanical verdicts`);
if (values.write) { writeFileSync(LEDGER, r.md); console.log(`written: ${LEDGER}`); } else console.log('dry run: --write fills the empty verdict cells');
