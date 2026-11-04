// Unseen wake-up report (docs/211, ts3fixes wakeprobe / wakepark / wakepoll, counters 491-533): wake-ups per second by class
// (seen / unseen x high / low LOD), their timed cost, and what the park switches did.
//   node tools/harness/wake-report.ts <queue>-<date>[-<job>]   (tag: rows of probe-out/queue/<queue>-<date>.jsonl whose job matches)
//   node tools/harness/wake-report.ts --self-check
// Reads the queue row's fixActivity.counters ({k: {before, after}}, the agent's wake_* names) over run.profile.minutes.
// The ms/s figures are timed script bodies; the wake itself (scheduler + interpreter resume) is not timed here: --wake-us N
// (default 3, [inferred]) prices each wake for the "removable" line. Pass: wake_su_pass_* > 0 (hooks patched and wakeprobe on).
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

type Counters = Record<string, { before?: number; after?: number }>;
const CLS = ['seen_hi', 'seen_lo', 'unseen_hi', 'unseen_lo'];
const REACT = ['full_seen', 'full_unseen', 'bcast_seen', 'bcast_unseen'];

export function report(c: Counters, seconds: number, wakeUs = 3, parkTicks = 12) {
  const d = (k: string) => (c[k]?.after ?? 0) - (c[k]?.before ?? 0);
  const n16 = (k: string) => Math.floor((c[k]?.after ?? 0) / 16) - Math.floor((c[k]?.before ?? 0) / 16); // 1-in-16 samples by count
  const rate = (n: number) => (n / seconds).toFixed(1);
  const msps = (us: number) => (us / 1000 / seconds).toFixed(2);
  const lines: string[] = [];
  const pass = CLS.map(k => d(`wake_su_pass_${k}`));
  const ok = pass.some(n => n > 0);
  lines.push(`SimUpdate passes/s: ${CLS.map((k, i) => `${k} ${rate(pass[i])}`).join(', ')}`);
  lines.push(`  timed body (mean us, est ms/s): ${CLS.map((k, i) => {
    const s = d(`wake_su_samples_${k}`), us = d(`wake_su_us_${k}`);
    const mean = s ? us / s : 0;
    return `${k} ${mean.toFixed(1)} / ${msps(mean * pass[i])}`;
  }).join(', ')}`);
  lines.push(`exit-reason polls/s: ${CLS.map(k => `${k} ${rate(d(`wake_poll_${k}`))}`).join(', ')}; waits/s seen ${rate(d('wake_wait_seen'))}, unseen ${rate(d('wake_wait_unseen'))}`);
  lines.push(`idle passes/s: ${CLS.map(k => `${k} ${rate(d(`wake_idle_${k}`))}`).join(', ')}`);
  lines.push(`UpdateReactions calls/s (mean us, est ms/s): ${REACT.map(k => {
    const n = d(`wake_react_${k}`), s = n16(`wake_react_${k}`), us = d(`wake_react_us_${k}`);
    const mean = s ? us / s : 0;
    return `${k} ${rate(n)} (${mean.toFixed(1)}, ${msps(mean * n)})`;
  }).join(', ')}; timed samples dropped (yielded) ${d('wake_yielded')}`);
  const cls = d('wake_classify'), cs = n16('wake_classify');
  const cMean = cs ? d('wake_classify_us') / cs : 0;
  lines.push(`verdicts asked ${rate(cls)}/s, mean ${cMean.toFixed(1)} us, probe classify cost ~${msps(cMean * cls)} ms/s`);
  // removable wakes [inferred]: a park of k ticks keeps 1 pass in k; parked polls are what wakepoll removes
  const ul = pass[3];
  const parkOut = ul * (1 - 1 / Math.max(1, parkTicks));
  const pollU = d('wake_poll_unseen_hi') + d('wake_poll_unseen_lo');
  lines.push(`removable at k=${parkTicks} [inferred]: unseen low-LOD passes ${rate(parkOut)}/s ~${msps(parkOut * (wakeUs + (d('wake_su_samples_unseen_lo') ? d('wake_su_us_unseen_lo') / d('wake_su_samples_unseen_lo') : 0)))} ms/s; ` +
    `unseen polls ${rate(pollU)}/s ~${msps(pollU * wakeUs)} ms/s (at ${wakeUs} us per wake)`);
  const parks = d('wake_parks');
  if (parks || d('wake_poll_long')) {
    lines.push(`wakepark: parks ${rate(parks)}/s, mean ${(parks ? d('wake_park_ticks') / parks : 0).toFixed(1)} ticks, woken early ${parks ? ((100 * d('wake_park_early')) / parks).toFixed(1) : 0}%, ` +
      `view-change wake-alls ${d('wake_wakeall')}, refused (save) ${d('wake_park_refused')}`);
    lines.push(`wakepoll: long polls ${rate(d('wake_poll_long'))}/s, woken by AddExitReason ${d('wake_poll_woken')}, ended at deadline ${d('wake_poll_deadline')}, ` +
      `second waiter ${d('wake_poll_shared')}, most ticks past a deadline ${c.wake_poll_over?.after ?? 0}`);
  }
  return { ok, lines };
}

if (process.argv[1] && import.meta.filename === process.argv[1]) {
  const args = process.argv.slice(2);
  const wi = args.indexOf('--wake-us');
  const wakeUs = wi >= 0 ? Number(args.splice(wi, 2)[1]) : 3;
  if (args[0] === '--self-check') {
    const c: Counters = {
      wake_su_pass_seen_hi: { before: 0, after: 6000 }, wake_su_pass_unseen_lo: { before: 1000, after: 361000 },
      wake_su_us_unseen_lo: { after: 20000 }, wake_su_samples_unseen_lo: { after: 2000 },
      wake_poll_unseen_lo: { after: 60000 }, wake_wait_unseen: { after: 600 },
      wake_react_full_unseen: { before: 15, after: 1615 }, wake_react_us_full_unseen: { after: 5000 },
      wake_classify: { after: 3200 }, wake_classify_us: { after: 4000 },
      wake_parks: { after: 30000 }, wake_park_ticks: { after: 360000 }, wake_park_early: { after: 300 }, wake_poll_long: { after: 6000 },
      wake_poll_woken: { after: 50 }, wake_poll_over: { after: 0 },
    };
    const r = report(c, 600, 3, 12);
    const text = r.lines.join('\n');
    const want = ['seen_hi 10.0', 'unseen_lo 600.0', 'unseen_lo 10.0 / 6.00', 'unseen 1.0', 'full_unseen 2.7 (50.0, 0.13)',
      'verdicts asked 5.3/s, mean 20.0 us', 'unseen low-LOD passes 550.0/s ~7.15 ms/s', 'unseen polls 100.0/s ~0.30 ms/s',
      'parks 50.0/s, mean 12.0 ticks, woken early 1.0%', 'woken by AddExitReason 50'];
    const miss = want.filter(w => !text.includes(w));
    if (miss.length || !r.ok) throw new Error(`wake-report self-check failed (${miss.join('; ')})\n${text}`);
    if (report({}, 600).ok) throw new Error('wake-report self-check: no passes must fail');
    console.log('wake-report self-check ok');
  } else {
    const dir = 'probe-out/queue';
    let ok = true, found = 0;
    for (const tag of args) {
      const files = existsSync(tag) ? [tag] : readdirSync(dir).filter(f => f.endsWith('.jsonl') && tag.startsWith(f.slice(0, -6))).map(f => join(dir, f));
      for (const f of files)
        for (const l of readFileSync(f, 'utf8').split('\n')) {
          let row: any;
          try { row = JSON.parse(l); } catch { continue; }
          if (!row?.fixActivity?.counters || (!existsSync(tag) && !`${f.slice(dir.length + 1, -6)}-${row.job}`.startsWith(tag))) continue;
          const seconds = (Number(row.run?.profile?.minutes) || 10) * 60;
          const r = report(row.fixActivity.counters, seconds, wakeUs);
          console.log(`== ${row.job} (${seconds} s${row.run?.profile?.ab ? `, --ab ${row.run.profile.ab}: both arms summed` : ''})\n${r.lines.join('\n')}`);
          ok &&= r.ok; found++;
        }
    }
    if (!found) { console.error(`no queue rows with fix counters for ${args.join(' ') || '(no args)'}`); ok = false; }
    process.exitCode = ok ? 0 : 1;
  }
}
