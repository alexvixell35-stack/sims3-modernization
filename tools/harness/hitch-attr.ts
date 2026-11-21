// Hitch attribution for HWATCH=1 soaks: splits each run's hitch_watch hitches ("ts3fixes: hitchwatch: <ms> ms, tick, game time,
// gc +N, agent <cmd>" in ts3native.log) into harness hitches (agent != '-') and game hitches, and classes each game hitch:
//   load    in the first game minutes after the world load (world-load passes, ours and EA's)
//   hour    at hh:00 game time (hourly alarms: our ErrorTrapSims sweep passes, EA's hourly work)
//   gc      a Mono GC ran inside the hitch (gc +N > 0)
//   other
// and lists the log lines just before it that carry a real-time stamp ("at <ms> ms") within --window ms, plus any
// "hourlypass:" / "alarmtime:" stamps (switch alarmtime; all alarms need the kHitchCauseHooks test package) in that stretch.
//   node tools/harness/hitch-attr.ts <run dir>... [--min 300] [--window 3000] [--lines 6] [--quiet]
//   node tools/harness/hitch-attr.ts --self-check
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { parseArgs } from 'node:util';

export const RE_HITCH = /hitchwatch: (\d+) ms, tick (\d+), (\S+) at (\d+):(\d+) (AM|PM), gc \+(\d+), agent (.*?), at (\d+) ms/;
const RE_AT = / at (\d+) ms\s*$/;
const RE_STAMP = /hourlypass: |alarmtime: |worldloadpass: /;
const NOISE = /^(PNc |thread |autshadow |pkglock: )/;

export type Hitch = { ms: number; tick: number; when: string; gc: number; agent: string; at: number; cls: string; before: string[] };

export function parseRun(text: string, minMs: number, windowMs: number, keep: number): Hitch[] {
  const lines = text.split(/\r?\n/), out: Hitch[] = [];
  let firstTick = -1, lastHitchLine = -1;
  for (let i = 0; i < lines.length; i++) {
    const m = RE_HITCH.exec(lines[i]);
    if (!m) continue;
    const tick = Number(m[2]);
    if (firstTick < 0) firstTick = tick;
    const h: Hitch = { ms: Number(m[1]), tick, when: `${m[3]} ${m[4]}:${m[5]} ${m[6]}`, gc: Number(m[7]), agent: m[8], at: Number(m[9]), cls: 'other', before: [] };
    const from = lastHitchLine;
    lastHitchLine = i;
    if (h.ms < minMs) continue;
    // world load ends near tick 18000 (8:00) in these soaks; the first hitch's tick marks it
    h.cls = h.agent !== '-' ? 'harness' : tick - firstTick < 300 && m[5] === '00' ? 'load' : m[5] === '00' ? 'hour' : h.gc > 0 ? 'gc' : 'other';
    for (let j = i - 1; j > from && j >= i - 400 && h.before.length < keep + 4; j--) {
      const l = lines[j].replace(/^[^A-Za-z]*/, '');
      if (NOISE.test(l)) continue;
      const a = RE_AT.exec(l);
      if (RE_STAMP.test(l) || (a && h.at - Number(a[1]) <= windowMs && h.at - Number(a[1]) >= 0)) h.before.push(l.length > 200 ? l.slice(0, 200) + '...' : l);
    }
    h.before = h.before.slice(0, keep);
    out.push(h);
  }
  return out;
}

export function summary(name: string, hs: Hitch[]): string {
  const by = new Map<string, number>();
  for (const h of hs) by.set(h.cls, (by.get(h.cls) ?? 0) + 1);
  const game = hs.filter(h => h.cls !== 'harness');
  return `${name}: ${hs.length} hitches, harness ${by.get('harness') ?? 0}, game ${game.length} (load ${by.get('load') ?? 0}, hour ${by.get('hour') ?? 0}, gc ${by.get('gc') ?? 0}, other ${by.get('other') ?? 0}), game ms ${game.reduce((s, h) => s + h.ms, 0)}`;
}

function selfCheck() {
  const ok = (c: boolean, what: string) => { if (!c) { console.error('hitch-attr self-check FAILED: ' + what); process.exit(1); } };
  const log = [
    'ts3fixes: hitchwatch: 300 ms, tick 18000, Sun. at 8:00 AM, gc +0, agent -, at 60092762 ms',
    'PNc final release: object 1 id 2',
    'ts3fixes: ufplan: kind 0 414 ms why -1, tick 18020, arm -, at 60093369 ms',
    'ts3fixes: hitchwatch: 333 ms, tick 18030, Sun. at 8:00 AM, gc +0, agent -, at 60093371 ms',
    'ts3fixes: ufplan: kind 0 157 ms why 0 marked Rocky Tanker own task, tick 112674, arm -, at 54092771 ms',
    'ts3fixes: hourlypass: OutfitWaitSavedPass 54 ms, day 2 hour 9.00',
    'ts3fixes: hitchwatch: 382 ms, tick 236266, Thu. at 9:00 AM, gc +0, agent -, at 60889220 ms',
    'ts3fixes: hitchwatch: 3717 ms, tick 276265, Fri. at 2:47 AM, gc +2, agent -, at 57849205 ms',
    'ts3fixes: hitchwatch: 750 ms, tick 26230, Sun. at 11:39 AM, gc +2, agent async mem_gc 751, at 56921050 ms',
    'ts3fixes: hitchwatch: 200 ms, tick 26240, Sun. at 11:40 AM, gc +0, agent -, at 56921445 ms'].join('\n');
  const hs = parseRun(log, 300, 3000, 6);
  ok(hs.length === 5, 'hitches >= --min: ' + hs.length);
  ok(hs.map(h => h.cls).join() === 'load,load,hour,gc,harness', 'classes: ' + hs.map(h => h.cls).join());
  ok(hs[1].before.length === 1 && hs[1].before[0].startsWith('ts3fixes: ufplan'), 'stamped line in the window, noise skipped');
  ok(hs[2].before.some(l => l.startsWith('ts3fixes: hourlypass')) && !hs[2].before.some(l => l.includes('Rocky')), 'hourlypass kept, out-of-window line dropped');
  ok(summary('x', hs).includes('game 4 (load 2, hour 1, gc 1, other 0)'), 'summary');
  console.log('hitch-attr self-check: ok');
}

if (process.argv.includes('--self-check')) selfCheck();
else {
  const { values, positionals } = parseArgs({ allowPositionals: true, options: { min: { type: 'string' }, window: { type: 'string' }, lines: { type: 'string' }, quiet: { type: 'boolean' } } });
  if (!positionals.length) { console.error('usage: node tools/harness/hitch-attr.ts <run dir>... [--min 300] [--window 3000] [--lines 6] [--quiet]'); process.exit(1); }
  for (const dir of positionals) {
    const hs = parseRun(readFileSync(join(dir, 'ts3native.log'), 'latin1'), Number(values.min ?? 300), Number(values.window ?? 3000), Number(values.lines ?? 6));
    console.log(summary(dir.replace(/\/+$/, '').split('/').pop()!, hs));
    if (values.quiet) continue;
    for (const h of hs.filter(x => x.cls !== 'harness')) {
      console.log(`  ${h.cls.padEnd(5)} ${h.ms} ms, ${h.when}, tick ${h.tick}, gc +${h.gc}`);
      for (const l of h.before) console.log(`      ${l}`);
    }
  }
}
