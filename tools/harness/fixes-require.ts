// ts3fixes counter pass rules for queue jobs (profile.fixesRequire -> profile-world.ts --fixes-require): a rule list such as
// "easpread_lots_passes>=1,easpread_lots_max_tick_us<5000" checked against the agent's `patches` answer at the end of the run,
// so a job proves the mechanism ran (and stayed under its cap) from counters, not from timing.
//   node tools/harness/fixes-require.ts    self-check
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

// "@name" reads the /native after value instead of after - before (nativeRequire): for values set once at install/startup
export const RULE = /^(@?[A-Za-z0-9_.]+)(>=|<=|==|>|<)(-?\d+)$/;

export function parseRequire(s: string): { name: string; op: string; value: number }[] {
  return s.split(',').map(r => {
    const m = RULE.exec(r.trim());
    if (!m) throw new Error(`fixes-require: "${r}" is not name<op>number (ops >= <= == > <; @name = the after value)`);
    return { name: m[1], op: m[2], value: Number(m[3]) };
  });
}

// /native fields set only at DllMain/install (or a switch read back once): after - before is always 0, so a delta rule on them
// always FAILs (tdOn, jitRegionKB, bcFills on 2026-11-13; rwMode is a read-back switch). nativeRequire must write them as @name.
// *Ready fields already keep their after value in flatDelta.
// + jitMethCap, jitDirect, bcOn, clOn, cpOn (2026-11-13 jit-bundle queues: read back once at startup)
export const STARTUP_COUNTERS = ['tdOn', 'jitRegionKB', 'bcFills', 'rwMode', 'jitMethCap', 'jitDirect', 'bcOn', 'clOn', 'cpOn', 'mfReady', 'mcSwitch', 'mdtOn', 'mdtHz'];
export function startupDeltaLint(s: string): string[] {
  return parseRequire(s).filter(r => STARTUP_COUNTERS.includes(r.name)).map(r =>
    `nativeRequire "${r.name}${r.op}${r.value}": ${r.name} is set at startup, its after - before is always 0; write @${r.name}${r.op}${r.value}`);
}

// Failed rules as text; [] = all hold. A counter missing from the answer fails (old agent or wrong name).
export function checkRequire(s: string, answer: Record<string, unknown> | null | undefined): string[] {
  const fails: string[] = [];
  for (const { name, op, value } of parseRequire(s)) {
    // agent.js flattens counter arrays as name_i; rules may write name.i (repro-check-route sypPlans.1 read "missing", 2026-10-22)
    const v = answer?.[name] ?? answer?.[name.replace(/\.(\d+)$/, '_$1')];
    if (typeof v !== 'number') { fails.push(`${name} missing`); continue; }
    const ok = op === '>=' ? v >= value : op === '<=' ? v <= value : op === '==' ? v === value : op === '>' ? v > value : v < value;
    if (!ok) fails.push(`${name}=${v} (want ${op}${value})`);
  }
  return fails;
}

// The numeric fields of a `patches` answer (switch states and counters), for per-slice snapshots.
export function numericFields(answer: Record<string, unknown> | null | undefined): Record<string, number | boolean> {
  return Object.fromEntries(Object.entries(answer ?? {}).filter(([, v]) => typeof v === 'number' || typeof v === 'boolean')) as Record<string, number | boolean>;
}

// Repro step pass rules (repros.json entry stepsRequire, same rule syntax): "<cmd>.<field>" = how many of that command's steps
// answered ok with result[field] === true, e.g. "sim_visit_lot.queued>=4" (bp-preplancb-order 2026-10-24 passed on counters
// with all 6 visits queued:false). A step with "tally": "<name>" counts as <name> instead of its cmd (per-arm rules such as
// "visit_c.queued>=1"). A field no ok answer carried stays missing, so its rule fails.
export function stepTally(steps: { cmd: string; r: { ok?: boolean; result?: unknown } }[]): Record<string, number> {
  const t: Record<string, number> = {};
  for (const { cmd, r } of steps) {
    const res = r?.ok && r.result && typeof r.result === 'object' ? r.result as Record<string, unknown> : {};
    for (const [k, v] of Object.entries(res)) if (typeof v === 'boolean') t[`${cmd}.${k}`] = (t[`${cmd}.${k}`] ?? 0) + (v ? 1 : 0);
    t[cmd] = (t[cmd] ?? 0) + 1;
  }
  return t;
}

// nativeRequire: every numeric /native field's after - before (arrays as name.i), checked with the rules above;
// `*Ready` fields are load-time install flags/masks, so they keep their after value (a delta is always 0: 3 FAILs 2026-10-23);
// every numeric field is also given as `@name` (`@name.i`) with its after value, for @ rules
export function flatDelta(a: any, b: any): Record<string, number> {
  const out: Record<string, number> = {};
  for (const [k, v] of Object.entries(b ?? {})) {
    if (typeof v === 'number') out[`@${k}`] = v;
    else if (Array.isArray(v)) v.forEach((x, i) => { if (typeof x === 'number') out[`@${k}.${i}`] = x; });
    if (typeof v === 'number' && typeof a?.[k] === 'number') out[k] = /Ready$/.test(k) ? v : v - a[k];
    else if (Array.isArray(v) && Array.isArray(a?.[k])) v.forEach((x, i) => { if (typeof x === 'number' && typeof a[k][i] === 'number') out[`${k}.${i}`] = x - a[k][i]; });
  }
  // derived: FinishWalk holds live now (every hold ends in exactly one ufwRelease_0..3; the log's "held now" agrees, bundle1124 SV)
  if (typeof b?.ufwHolds === 'number') out['@ufwHeldNow'] = b.ufwHolds - [0, 1, 2, 3].reduce((t, i) => t + (Number(b[`ufwRelease_${i}`]) || 0), 0);
  return out;
}

// Repro steps native_mark / native_check {name?, require, untilS?} (docs/202 §6): the /native delta over a window inside one repro, e.g.
// only while Build/Buy runs (whole-job nativeRequire also counts the live-mode minutes). A failed rule fails the step, so
// profile-world.ts fails the job. Marks are per name ('' when none is given). untilS: poll once a second until the rules hold,
// fail after untilS s with the last values (a condition to act on, e.g. a FinishWalk hold live before a pause).
export async function nativeWindowStep(s: { cmd: string; args?: any }, native: () => Promise<any>, marks: Map<string, any>,
  pollMs = 1000): Promise<{ ok: boolean; result?: unknown; error?: string }> {
  const name = String(s.args?.name ?? '');
  if (s.cmd === 'native_mark') { marks.set(name, await native()); return { ok: true, result: { mark: name } }; }
  if (!marks.has(name)) return { ok: false, error: `native_check: no native_mark "${name}" before it` };
  const rule = String(s.args?.require ?? ''), until = Number(s.args?.untilS ?? 0), t0 = Date.now();
  let d = flatDelta(marks.get(name), await native()), fails = checkRequire(rule, d);
  while (fails.length && until > 0 && Date.now() - t0 < until * 1000) {
    await new Promise(r => setTimeout(r, pollMs));
    d = flatDelta(marks.get(name), await native()); fails = checkRequire(rule, d);
  }
  if (fails.length) return { ok: false, error: `native_check ${name}: ${fails.join(', ')}${until > 0 ? ` (not met in ${until} s)` : ''}` };
  return { ok: true, result: Object.fromEntries(parseRequire(rule).map(r => [r.name, d[r.name] ?? d[r.name.replace(/\.(\d+)$/, '_$1')]])) };
}

// Repro step "mustMatch": "<regex>": the step's answer (result text, or JSON when not a string) must match, else the step FAILs
// (2026-11-15 bp-unseen-tp-goal PASSed hollow: trap "N/A", mark "no trapped NPC", cleanup "moved 0" were only logged)
// "hollow": "<text>" next to mustMatch names the case that never happened: "hollow repro: <text>; ..." (profile-world FAIL line)
export function mustMatchFail(s: { cmd: string; mustMatch?: string; hollow?: string }, r: { ok?: boolean; result?: unknown }): string | null {
  if (!s.mustMatch || !r?.ok) return null;
  const text = typeof r.result === 'string' ? r.result : JSON.stringify(r.result);
  return new RegExp(s.mustMatch).test(text) ? null : `${s.hollow ? `hollow repro: ${s.hollow}; ` : ''}repro step ${s.cmd} answer does not match /${s.mustMatch}/: ${String(text).slice(0, 300)}`;
}

// repros.json regex lint (2026-11-24 bp-dmx2s-venueclock-1 FAILed twice on an unescaped mustMatch /"adopted":[{/): every
// mustMatch (any depth, loop steps too) must compile; an entry's optional regexCases { "<mustMatch>": { match, noMatch } }
// (real answer lines, as queue regexCases) must name one of its mustMatch regexes and hold
export function reproRegexErrors(entries: any[]): string[] {
  const errs: string[] = [];
  for (const e of entries) {
    const res = new Set<string>();
    const walk = (o: any): void => {
      if (Array.isArray(o)) return o.forEach(walk);
      if (!o || typeof o !== 'object') return;
      if (typeof o.mustMatch === 'string') {
        try { new RegExp(o.mustMatch); res.add(o.mustMatch); } catch (x) { errs.push(`${e.id}: ${o.cmd ?? 'step'} mustMatch: ${(x as Error).message}`); }
      }
      for (const [k, v] of Object.entries(o)) if (k !== 'regexCases') walk(v);
    };
    walk(e);
    for (const [re, c] of Object.entries<any>(e.regexCases ?? {})) {
      if (!res.has(re)) { errs.push(`${e.id}: regexCases /${re}/ is no mustMatch of this entry`); continue; }
      if (!c?.match?.length || !c?.noMatch?.length) { errs.push(`${e.id}: regexCases /${re}/ needs match and noMatch lists of >= 1 line`); continue; }
      const r = new RegExp(re);
      for (const l of c.match) if (!r.test(l)) errs.push(`${e.id}: regexCases /${re}/ does not match: ${l}`);
      for (const l of c.noMatch) if (r.test(l)) errs.push(`${e.id}: regexCases /${re}/ matches a noMatch line: ${l}`);
    }
  }
  return errs;
}

if (import.meta.url === `file:///${process.argv[1]?.replace(/\\/g, '/').replace(/^\//, '')}`) {
  // every repros.json regex compiles and its regexCases hold; the 2026-11-24 unescaped key is caught
  assert.deepEqual(reproRegexErrors(JSON.parse(readFileSync(new URL('./repros.json', import.meta.url), 'utf8'))), []);
  assert.match(reproRegexErrors([{ id: 'x', steps: [{ cmd: 'pet_adopt', mustMatch: '"adopted":[{' }] }])[0] ?? '', /^x: pet_adopt mustMatch: Invalid regular expression/);
  assert.match(reproRegexErrors([{ id: 'x', steps: [{ loop: { steps: [{ cmd: 'c', mustMatch: '(' }] } }] }])[0] ?? '', /^x: c mustMatch: /);
  assert.match(reproRegexErrors([{ id: 'x', regexCases: { a: { match: ['a'], noMatch: ['b'] } }, steps: [] }])[0] ?? '', /is no mustMatch/);
  assert.match(reproRegexErrors([{ id: 'x', regexCases: { a: { match: ['b'], noMatch: ['c'] } }, steps: [{ mustMatch: 'a' }] }])[0] ?? '', /does not match: b/);
  assert.deepEqual(checkRequire('a>=1,b<5000', { a: 2, b: 4999 }), []);
  assert.deepEqual(checkRequire('a>=1,b<5000', { a: 0, b: 5000 }), ['a=0 (want >=1)', 'b=5000 (want <5000)']);
  assert.deepEqual(checkRequire('c==0', {}), ['c missing']);
  assert.deepEqual(checkRequire('c==-1', { c: -1 }), []);
  assert.throws(() => parseRequire('a=>1'), /not name<op>number/);
  assert.deepEqual(numericFields({ a: 1, b: true, c: 'x', d: [1] }), { a: 1, b: true });
  const tally = stepTally([{ cmd: 'v', r: { ok: true, result: { queued: false } } }, { cmd: 'v', r: { ok: true, result: { queued: true } } },
    { cmd: 'v', r: { ok: false } }, { cmd: 'w', r: { ok: true, result: null } }]);
  assert.deepEqual(tally, { 'v.queued': 1, v: 3, w: 1 });
  assert.deepEqual(checkRequire('v.queued>=1', tally), []);
  assert.deepEqual(checkRequire('v.queued>=2,w.queued>=1', tally), ['v.queued=1 (want >=2)', 'w.queued missing']);
  assert.deepEqual(checkRequire('v.queued>=1', stepTally([{ cmd: 'v', r: { ok: true, result: { queued: false } } }])), ['v.queued=0 (want >=1)']);
  assert.deepEqual(flatDelta({ a: 1, p: [1, 2], s: 'x', fgReady: 1 }, { a: 4, p: [1, 7], s: 'y', fgReady: 1 }),
    { a: 3, '@a': 4, 'p.0': 0, 'p.1': 5, '@p.0': 1, '@p.1': 7, fgReady: 1, '@fgReady': 1 });
  // @ rules on startup values (2026-11-13 binder-typehandle: tdOn before 1, after 1, so the delta rule FAILed every on arm)
  const td = flatDelta({ tdOn: 1, bcFills: 4, bcHits: 10 }, { tdOn: 1, bcFills: 4, bcHits: 900 });
  assert.deepEqual(checkRequire('tdOn>=1', td), ['tdOn=0 (want >=1)']);
  assert.deepEqual(checkRequire('@tdOn>=1,@bcFills>=1,bcHits>=1', td), []);
  assert.deepEqual(checkRequire('@jitRegionKB>=4096', td), ['@jitRegionKB missing']);
  assert.deepEqual(parseRequire('@x.1>=2'), [{ name: '@x.1', op: '>=', value: 2 }]);
  assert.throws(() => parseRequire('@@x>=1'), /not name<op>number/);
  assert.throws(() => parseRequire('x@>=1'), /not name<op>number/);
  assert.equal(startupDeltaLint('tdOn>=1,bcHits>=1,@jitRegionKB>=4096').length, 1);
  assert.match(startupDeltaLint('bcFills>=1')[0], /write @bcFills>=1/);
  assert.deepEqual(startupDeltaLint('@tdOn>=1,@rwMode<=0,jitFaults<=0'), []);
  assert.equal(startupDeltaLint('jitMethCap>=4096,bcOn==3,clOn>=1,cpOn>=1,jitDirect==2,@bcOn==3,clKillWhy<=0').length, 5);
  // native_mark / native_check: delta since the named mark only (the 2026-10-26 sv-ufc-buildbuy C4 tries were live mode)
  const seq = [{ ufcTry_3: 5, w: 1 }, { ufcTry_3: 5, w: 3 }, { ufcTry_3: 6, w: 3 }];
  let at = 0;
  const nat = async () => seq[at++], marks = new Map<string, any>();
  assert.equal((await nativeWindowStep({ cmd: 'native_check', args: { require: 'w<=0' } }, nat, marks)).ok, false); // no mark yet
  at = 0;
  assert.deepEqual(await nativeWindowStep({ cmd: 'native_mark', args: { name: 'bb' } }, nat, marks), { ok: true, result: { mark: 'bb' } });
  assert.deepEqual(await nativeWindowStep({ cmd: 'native_check', args: { name: 'bb', require: 'ufcTry_3<=0' } }, nat, marks), { ok: true, result: { ufcTry_3: 0 } });
  assert.deepEqual(await nativeWindowStep({ cmd: 'native_check', args: { name: 'bb', require: 'ufcTry_3<=0,w<=2' } }, nat, marks),
    { ok: false, error: 'native_check bb: ufcTry_3=1 (want <=0)' });
  // untilS polls until the rule holds; @ufwHeldNow = holds - releases (bundle1124 SV: 26 holds, 22+0+3+0 ended = 1 held now)
  assert.equal(flatDelta({}, { ufwHolds: 26, ufwRelease_0: 22, ufwRelease_1: 0, ufwRelease_2: 3, ufwRelease_3: 0 })['@ufwHeldNow'], 1);
  const hs = [{ ufwHolds: 0 }, { ufwHolds: 5, ufwRelease_0: 5 }, { ufwHolds: 6, ufwRelease_0: 5 }];
  let hi = 0;
  const hn = async () => hs[Math.min(hi++, hs.length - 1)], hm = new Map<string, any>();
  await nativeWindowStep({ cmd: 'native_mark', args: { name: 'live' } }, hn, hm);
  assert.deepEqual(await nativeWindowStep({ cmd: 'native_check', args: { name: 'live', require: '@ufwHeldNow>=1', untilS: 5 } }, hn, hm, 1),
    { ok: true, result: { '@ufwHeldNow': 1 } });
  assert.match((await nativeWindowStep({ cmd: 'native_check', args: { name: 'live', require: '@ufwHeldNow>=2', untilS: 0.01 } }, async () => hs[1], hm, 1)).error ?? '',
    /@ufwHeldNow=0 \(want >=2\) \(not met in 0\.01 s\)/);
  // mustMatch on the real 2026-11-15 answers: the hollow ones fail, the wanted ones pass
  const mm = (re: string, result: unknown) => mustMatchFail({ cmd: 'reflect_call', mustMatch: re }, { ok: true, result });
  assert.match(mm('\\(in the room\\) hidden=True exit=closed', 'trap: N/A (lot Air Spray - 2br, 2ba has no hidden room; lots ...') ?? '', /does not match/);
  assert.equal(mm('\\(in the room\\) hidden=True exit=closed', 'trap: X -> hidden room 26 level 0 on F; now room 26 lot F (in the room) hidden=True exit=closed'), null);
  assert.match(mm('\\(in the room\\) hidden=True exit=closed', 'trap: X -> hidden room 26 ...; now room 1 lot F (NOT in the room) hidden=False exit=out') ?? '', /does not match/);
  assert.match(mm('^mark: (?!no trapped)', 'mark: no trapped NPC (run HiddenRoomCheck trap first) | counters') ?? '', /does not match/);
  assert.equal(mm('^mark: (?!no trapped)', 'mark: Lenny Shutter @1,2 room 26 | counters'), null);
  assert.match(mm('moved [1-9]', 'cleanup: moved 0, left 0 | counters') ?? '', /does not match/);
  assert.equal(mm('left [1-9]', 'cleanup: moved 0, left 1 | counters'), null);
  assert.match(mm('moved 0, left [1-9]', 'cleanup: moved 0, left 0 | counters 1040-1045: 0 0 0 0 0 0, unseenfbteleport on') ?? '', /does not match/);
  // who (HrSimsIn line format): an NPC in a closed room passes; household, or no Sim line at all, fails
  const who = '(^|\\n)sim [^\\n]* npc lot [0-9A-F]+ "[^"\\n]*" room \\d+ level \\d+( hidden)? exit=closed';
  assert.equal(mm(who, 'sim Lenny Shutter npc lot F59000345CBEAE40 "Founders Keep - 1br, 1ba" room 28 level 0 hidden exit=closed routeFails=0 there 1 s / 0 sim-min doing nothing\ntrapped Lenny Shutter (room 28 on Founders Keep, 1 s / 0 sim-min ago): still in room\nwho: 1 Sims'), null);
  assert.match(mm(who, 'sim Ann Angelista household lot 6C1100337657B720 "Air Spray" room 3 level 0 hidden exit=closed routeFails=0\nwho: 1 Sims') ?? '', /does not match/);
  assert.match(mm(who, 'trapped Lenny Shutter (room 28 on Founders Keep, 1 s / 0 sim-min ago): left (not by hiddenroomnpc): now room 27 on Founders Keep, doing nothing\nwho: 0 Sims in hidden/closed rooms') ?? '', /does not match/);
  assert.match(mm('\\nPASS: ', 'goal: lot A\n  entry point outside: teleport ok\nN/A (no closed room on this lot): closed 0') ?? '', /does not match/);
  assert.equal(mm('\\nPASS: ', 'goal: lot F\n  hidden marker r26(H) closed: refused\nPASS: closed 4'), null);
  // lineswitch-force-defaults end step (household exempt: kept, never switched); non-match = the real 2026-11-24 force-defaults-m1124b answer
  const lsd = 'switched 0; .*kept [1-9]\\d*; .*errors 0';
  assert.match(mm(lsd, 'lineswitch check (switch on, lineswitchhh off): ...; verdict: FIXED; lineswitch: switched 0; off: would 0; kept 0; plans 0; errors 0; members found=True') ?? '', /does not match/);
  assert.equal(mm(lsd, 'lineswitch check (switch on, lineswitchhh off): ...; verdict: FIXED; lineswitch: switched 0; off: would 0; kept 1; plans 0; errors 0; members found=True'), null);
  assert.match(mm(lsd, 'lineswitch: switched 1; off: would 0; kept 1; plans 1; errors 0') ?? '', /does not match/);
  assert.equal(mm('x', { a: 'x' }), null);  // object answers match against their JSON
  assert.equal(mustMatchFail({ cmd: 'c', mustMatch: 'x' }, { ok: false }), null);
  // hollow names the case (2026-11-24 bp-dmx2-venueclock-1: pet_adopt adopted [] and venue_hours crossings never flipped)
  const hol = mustMatchFail({ cmd: 'pet_adopt', mustMatch: '"adopted":\\[\\{', hollow: 'no adoptable pet' }, { ok: true, result: { adopted: [], skipped: ['AdoptDog pool empty'] } });
  assert.match(hol ?? '', /^hollow repro: no adoptable pet; repro step pet_adopt answer does not match/);
  assert.equal(mustMatchFail({ cmd: 'pet_adopt', mustMatch: '"adopted":\\[\\{', hollow: 'no adoptable pet' }, { ok: true, result: { adopted: [{ pet: 'Rex', path: 'pool AdoptCat' }] } }), null);
  const vh = (result: unknown) => mustMatchFail({ cmd: 'venue_hours', mustMatch: '"flipped":true', hollow: 'venue hour crossed without an open-state flip' }, { ok: true, result });
  assert.equal(vh({ pair_role: 'after', pair_open_before: false, flipped: true, open_now: true }), null);
  assert.match(vh({ pair_role: 'after', pair_open_before: true, flipped: false, open_now: true, advanced_min: 0 }) ?? '', /^hollow repro: venue hour crossed without an open-state flip; /);  // a failed step is the optional/failed rule's job
  console.log('fixes-require selftest: PASS');
}
