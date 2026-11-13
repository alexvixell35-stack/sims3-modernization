// ts3fixes counter pass rules for queue jobs (profile.fixesRequire -> profile-world.ts --fixes-require): a rule list such as
// "easpread_lots_passes>=1,easpread_lots_max_tick_us<5000" checked against the agent's `patches` answer at the end of the run,
// so a job proves the mechanism ran (and stayed under its cap) from counters, not from timing.
//   node tools/harness/fixes-require.ts    self-check
import assert from 'node:assert/strict';

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
export const STARTUP_COUNTERS = ['tdOn', 'jitRegionKB', 'bcFills', 'rwMode', 'jitMethCap', 'jitDirect', 'bcOn', 'clOn', 'cpOn', 'mfReady'];
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
  return out;
}

// Repro steps native_mark / native_check {name?, require} (docs/202 §6): the /native delta over a window inside one repro, e.g.
// only while Build/Buy runs (whole-job nativeRequire also counts the live-mode minutes). A failed rule fails the step, so
// profile-world.ts fails the job. Marks are per name ('' when none is given).
export async function nativeWindowStep(s: { cmd: string; args?: any }, native: () => Promise<any>, marks: Map<string, any>):
  Promise<{ ok: boolean; result?: unknown; error?: string }> {
  const name = String(s.args?.name ?? '');
  if (s.cmd === 'native_mark') { marks.set(name, await native()); return { ok: true, result: { mark: name } }; }
  if (!marks.has(name)) return { ok: false, error: `native_check: no native_mark "${name}" before it` };
  const rule = String(s.args?.require ?? ''), d = flatDelta(marks.get(name), await native()), fails = checkRequire(rule, d);
  if (fails.length) return { ok: false, error: `native_check ${name}: ${fails.join(', ')}` };
  return { ok: true, result: Object.fromEntries(parseRequire(rule).map(r => [r.name, d[r.name] ?? d[r.name.replace(/\.(\d+)$/, '_$1')]])) };
}

if (import.meta.url === `file:///${process.argv[1]?.replace(/\\/g, '/').replace(/^\//, '')}`) {
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
  console.log('fixes-require selftest: PASS');
}
