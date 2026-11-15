// [LoadProf] report (docs/39 §10): per job, the load time, the routing build time by function and the routing share.
//   node tools/harness/load-profile-report.ts probe-out/queue/load-profile-<date>.jsonl [more .jsonl or -native.json files]
//   node tools/harness/load-profile-report.ts --self-check
// Reads each row's native dump (`after`, counters v59 lp*: the window rows freeze at world event 7, so no before/after delta)
// and the job log next to it (`<job>.log`: cycle.ts "world loaded after N s", the harness load time from load_save to the
// agent's world-loaded flag). Site names and kinds come from the kLpSites table in native/ts3native/loadprof.cpp, so the
// report and the build never disagree on the order.
import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import assert from 'node:assert/strict';

type Counters = Record<string, number>;
type Site = { name: string; routing: boolean };
const SRC = join(import.meta.dirname, '..', '..', 'native', 'ts3native', 'loadprof.cpp');

export function sites(src = readFileSync(SRC, 'utf8')): Site[] {
  const body = src.slice(src.indexOf('kLpSites[LP_N] = {'), src.indexOf('};', src.indexOf('kLpSites[LP_N] = {')));
  return [...body.matchAll(/\{(?:TS3A\()?0x[0-9A-Fa-f]+\)?, \d+, \{[^}]*\}, (?:(?:TS3A\()?0x[0-9A-Fa-f]+\)?|0), (LPK_\w+), "([^"]+)"\}/g)]  // TS3A(): addr-codemod
    .map(m => ({ name: m[2], routing: m[1] === 'LPK_ROUTING' }));
}

const ms = (us: number) => (us / 1000).toFixed(0);
const pct = (a: number, b: number) => b > 0 ? `${(100 * a / b).toFixed(1)}%` : 'n/a';

export function report(a: Counters, loadS: number | null, list: Site[]) {
  const lines: string[] = [];
  const v = (s: number, row: number, k: number) => a[`lpSite_${s}_${row * 3 + k}`] ?? 0;
  const ok = (a.version ?? 0) >= 59 && a.lpReady === 1 && a.lpEv7 >= 1;
  const win = a.lpWindowMs ?? 0, rt = (a.lpRtUs_0 ?? 0) / 1000;
  lines.push(`window ${(win / 1000).toFixed(1)} s (LoadWorld -> world event 7), harness load ${loadS ?? '?'} s; ` +
    `routing ${rt.toFixed(0)} ms = ${pct(rt, win)} of the window, ${loadS ? pct(rt, loadS * 1000) : 'n/a'} of the harness load ` +
    `(main ${ms(a.lpRtThrUs_0 ?? 0)}, sim ${ms(a.lpRtThrUs_1 ?? 0)}, other ${ms(a.lpRtThrUs_2 ?? 0)} ms)`);
  if (!ok) lines.push(`  CHECK: version ${a.version}, lpReady ${a.lpReady}, event 7 ${a.lpEv7}, ${a.lpHooks} hooked, ${a.lpSkipped} skipped (SKIP loadprof lines in the log)`);
  const rows = list.map((s, i) => ({ ...s, i, n: v(i, 0, 0), us: v(i, 0, 1), max: v(i, 0, 2), n1: v(i, 1, 0), us1: v(i, 1, 1) }));
  const phase = rows.filter(r => !r.routing && r.n);
  const lw = phase.filter(r => r.name.startsWith('WorldManager_LoadWorld')).reduce((s, r) => s + r.us, 0);
  lines.push(`  phases: ${phase.map(r => `${r.name} ${ms(r.us)} ms`).join(', ') || 'none'}; window outside LoadWorld (script side) ${Math.max(0, win - lw / 1000).toFixed(0)} ms`);
  lines.push('  routing by function (inclusive ms, nested calls also count in their caller; calls; max ms) in the window | after it:');
  for (const r of rows.filter(r => r.routing && (r.n || r.n1)).sort((x, y) => y.us - x.us))
    lines.push(`    ${r.name.padEnd(54)} ${ms(r.us).padStart(6)} ms ${String(r.n).padStart(7)} calls  max ${(r.max / 1000).toFixed(1).padStart(6)} | ${ms(r.us1)} ms ${r.n1} calls`);
  lines.push(`  after the window (lazy builds in the first minute of play): routing ${ms(a.lpRtUs_1 ?? 0)} ms`);
  return { ok, lines, routingMs: rt, windowMs: win };
}

function jobs(files: string[]): { id: string; dump: string }[] {
  const out: { id: string; dump: string }[] = [];
  for (const f of files) {
    if (f.endsWith('.jsonl')) {
      const last = new Map<string, string>();  // a rerun of a job replaces the earlier row
      for (const l of readFileSync(f, 'utf8').split(/\r?\n/).filter(Boolean)) {
        const r = JSON.parse(l);
        if (r.nativeDump) last.set((r.id ?? r.job), r.nativeDump);
        else if ((r.id ?? r.job)) console.log(`== ${(r.id ?? r.job)}: no native dump (${r.error ?? r.verdict ?? 'no row data'})`);
      }
      for (const [id, dump] of last) out.push({ id, dump });
    } else out.push({ id: f, dump: f });
  }
  return out;
}

if (process.argv[1] && import.meta.filename === process.argv[1]) {
  const args = process.argv.slice(2);
  const list = sites();
  if (args[0] === '--self-check') {
    assert.equal(list.length, 28);
    assert.ok(list[3].name === 'LotGraph_Build' && list[3].routing && !list[12].routing && list[22].name.startsWith('WorldManager_LoadWorld'));
    const a: Counters = { version: 59, lpReady: 1, lpEv7: 1, lpHooks: 28, lpSkipped: 0, lpWindowMs: 40000, lpRtUs_0: 4000000, lpRtUs_1: 250000,
      lpRtThrUs_0: 500000, lpRtThrUs_1: 3500000, lpSite_3_0: 80, lpSite_3_1: 900000, lpSite_3_2: 40000, lpSite_3_3: 5, lpSite_3_4: 60000,
      lpSite_22_0: 1, lpSite_22_1: 30000000 };
    const r = report(a, 50, list);
    const t = r.lines.join('\n');
    assert.ok(r.ok && r.routingMs === 4000, t);
    assert.match(t, /routing 4000 ms = 10\.0% of the window, 8\.0% of the harness load \(main 500, sim 3500, other 0 ms\)/);
    assert.match(t, /LotGraph_Build +900 ms +80 calls +max +40\.0 \| 60 ms 5 calls/);
    assert.match(t, /window outside LoadWorld \(script side\) 10000 ms/);
    assert.ok(!report({ ...a, lpEv7: 0 }, null, list).ok);
    console.log('load-profile-report self-check ok');
  } else {
    let ok = true;
    const sum: string[] = [];
    for (const j of jobs(args)) {
      if (!existsSync(j.dump)) { console.log(`== ${j.id}: ${j.dump} missing`); ok = false; continue; }
      const d = JSON.parse(readFileSync(j.dump, 'utf8'));
      const log = j.dump.replace(/-native\.json$/, '.log');
      const m = existsSync(log) ? [...readFileSync(log, 'utf8').matchAll(/world loaded after (\d+) s/g)].at(-1) : undefined;
      const r = report(d.after ?? d, m ? Number(m[1]) : null, list);
      console.log(`== ${j.id}\n${r.lines.join('\n')}`);
      sum.push(`${j.id}: window ${(r.windowMs / 1000).toFixed(1)} s, routing ${r.routingMs.toFixed(0)} ms (${pct(r.routingMs, r.windowMs)})`);
      ok &&= r.ok;
    }
    if (sum.length > 1) console.log(`== summary\n${sum.join('\n')}`);
    process.exitCode = ok ? 0 : 1;
  }
}
