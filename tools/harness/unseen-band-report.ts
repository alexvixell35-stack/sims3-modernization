// [UnseenProbe] Bands report (docs/322): sim-thread ms/s per distance band and site, Sims per band, bands over the tighten line.
//   node tools/harness/unseen-band-report.ts probe-out/queue/<queue>-<date>-<job>-native.json [...] [--seconds 600] [--md]
//   node tools/harness/unseen-band-report.ts <queue>-<date>[-<job>]   (tag: every probe-out/queue/<tag>*-native.json)
//   node tools/harness/unseen-band-report.ts --self-check
// Reads the native dump ({before, after}; agent.js ub* fields from export ts3native_unseenband) and uses after - before over the
// profile length (--seconds, else the queue row's run.profile.minutes, else 600). Pass: bands in force, locomotion and task sites on,
// Sims counted in some band. Total per band = loco + pose + Sim task + autonomy (disjoint); waits, footprint and plans are parts
// of those and are shown, not added.
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { basename, dirname, join } from 'node:path';

type Counters = Record<string, number>;
const SITES = ['loco', 'waits', 'footprint', 'plans', 'pose', 'simtask', 'autonomy'];
const TOTAL = [0, 4, 5, 6];  // disjoint sites
const HH = 7, UNK = 8;
export const TIGHTEN_MS = 5;  // docs/320 §5.4 step 4: a band that owns more than ~5 ms/s is a candidate

export function report(b: Counters, a: Counters, seconds: number) {
  const d = (k: string) => (a[k] ?? 0) - (b[k] ?? 0);
  const edges = a.ubEdges ?? 0, ready = a.ubReady ?? 0;
  const em = Array.from({ length: edges }, (_, i) => a[`ubEdgeM_${i}`] ?? 0);
  const name = (r: number) => r === HH ? 'household' : r === UNK ? 'no position' :
    r === 0 ? `<${em[0]} m` : r === edges ? `>=${em[edges - 1]} m` : `${em[r - 1]}-${em[r]} m`;
  const rows = [...Array.from({ length: edges + 1 }, (_, i) => i), HH, UNK];
  const ms = (s: number, r: number) => d(`ubUs_${s}_${r}`) / 1000 / seconds;
  const passes = d('unPasses');
  const lines: string[] = [];
  const ok = (ready & 1) === 1 && (ready & 2) === 2 && (ready & 32) === 32 && rows.some(r => d(`ubSimUpd_${r}`) > 0);
  lines.push(`ubReady 0x${ready.toString(16)} (loco ${ready & 2 ? 'on' : 'off'}, footprint ${ready & 4 ? 'on' : 'off'}, plans ${ready & 8 ? 'on' : 'off'}, ` +
    `pose ${ready & 16 ? 'on' : 'off'}, tasks ${ready & 32 ? 'on' : 'off'}, autonomy actors ${ready & 64 ? 'named' : 'none'}), edges ${em.join(',')} m, ` +
    `${seconds} s, ${passes} locomotion passes; autonomy actors named ${d('ubAutNamed')}, runs with none ${d('ubAutNone')}, camera-only asks ${d('ubCamOnly')}`);
  lines.push(`| band | Sims/pass | ${SITES.map(s => `${s} ms/s`).join(' | ')} | total ms/s | simtask runs/s |`);
  lines.push(`|---|---:|${SITES.map(() => '---:').join('|')}|---:|---:|`);
  const over: string[] = [];
  let npcTotal = 0;
  for (const r of rows) {
    const tot = TOTAL.reduce((t, s) => t + ms(s, r), 0);
    if (r !== HH) npcTotal += tot;
    if (r !== HH && tot > TIGHTEN_MS) over.push(`${name(r)} ${tot.toFixed(1)} ms/s`);
    lines.push(`| ${name(r)} | ${passes ? (d(`ubSimUpd_${r}`) / passes).toFixed(1) : '0'} | ${SITES.map((_, s) => ms(s, r).toFixed(2)).join(' | ')} | ` +
      `${tot.toFixed(2)} | ${(d(`ubN_5_${r}`) / seconds).toFixed(1)} |`);
  }
  lines.push(`NPC total ${npcTotal.toFixed(1)} ms/s; bands over ${TIGHTEN_MS} ms/s: ${over.join(', ') || 'none'}`);
  return { ok, lines, over, npcTotal };
}

function findRow(file: string): any {
  const dir = dirname(file), base = basename(file);
  for (const j of readdirSync(dir).filter(f => f.endsWith('.jsonl') && base.startsWith(f.slice(0, -6) + '-'))) {
    for (const l of readFileSync(join(dir, j), 'utf8').split('\n')) {
      if (!l.includes(base)) continue;
      try { const r = JSON.parse(l); if (basename(String(r.nativeDump ?? '').replace(/\\/g, '/')) === base) return r; } catch { /* partial line */ }
    }
  }
  return undefined;
}

if (process.argv[1] && import.meta.filename === process.argv[1]) {
  const args = process.argv.slice(2);
  const si = args.indexOf('--seconds');
  const fixedSeconds = si >= 0 ? Number(args.splice(si, 2)[1]) : 0;
  if (args[0] === '--self-check') {
    const b: Counters = { unPasses: 100 };
    const a: Counters = { ubReady: 0x7f, ubEdges: 2, ubEdgeM_0: 50, ubEdgeM_1: 400, unPasses: 1100, ubSimUpd_0: 2000, ubSimUpd_2: 12000, ubSimUpd_7: 3000,
      ubUs_0_2: 6_000_000, ubUs_5_2: 1_200_000, ubN_5_2: 6000, ubUs_1_2: 900_000, ubUs_0_7: 9_000_000, ubUs_6_1: 600_000, ubAutNamed: 40 };
    const r = report(b, a, 600);
    const t = r.lines.join('\n');
    const want = ['edges 50,400 m', '| >=400 m | 12.0 | 10.00 | 1.50 |', '| 12.00 | 10.0 |', '| household | 3.0 | 15.00 |', '| <50 m | 2.0 |',
      'bands over 5 ms/s: >=400 m 12.0 ms/s', 'NPC total 13.0 ms/s'];
    const miss = want.filter(w => !t.includes(w));
    if (miss.length || !r.ok) throw new Error(`unseen-band-report self-check failed (${miss.join('; ')})\n${t}`);
    if (report(b, { ...a, ubReady: 0 }, 600).ok) throw new Error('unseen-band-report self-check: bands off must fail');
    if (report(b, { ...a, ubReady: 0x1f }, 600).ok) throw new Error('unseen-band-report self-check: no task sites must fail');
    console.log('unseen-band-report self-check ok');
  } else {
    let ok = true;
    const files = args.flatMap(f => existsSync(f) ? [f] : readdirSync('probe-out/queue')
      .filter(n => n.startsWith(f) && n.endsWith('-native.json')).map(n => join('probe-out/queue', n)));
    if (!files.length) { console.error(`no native dumps for ${args.join(' ') || '(no args)'}`); process.exitCode = 1; }
    for (const f of files) {
      const j = JSON.parse(readFileSync(f, 'utf8'));
      const minutes = Number(findRow(f)?.run?.profile?.minutes) || 0;
      const seconds = fixedSeconds || minutes * 60 || 600;
      const r = report(j.before ?? {}, j.after ?? j, seconds);
      console.log(`== ${basename(f)} (${seconds} s)\n${r.lines.join('\n')}${r.ok ? '' : '\nFAIL: bands, loco or task sites not on, or no Sims counted'}`);
      ok &&= r.ok;
    }
    process.exitCode = ok ? 0 : 1;
  }
}
