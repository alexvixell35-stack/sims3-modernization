// Faster tests (docs/170 §1-2): several repros per game load and session reuse across queue jobs. queue.ts keeps the game
// loaded between profile windows that need the same game start (asi, ini, save, mods, configs); this file holds the pure parts
// and the carry-over check run between two windows of one load:
//   1. put every ts3fixes switch (agent `patches` booleans) and ts3native runtime switch (native-switches.ts) back to the value
//      read right after the load, and read them back;
//   2. snapshot cheap world state (sim clock, Sims, active household members, active lot and its object count, /memory) and
//      compare it with the snapshot right after the load: past a limit (REUSE_DEFAULTS, queue `reuseSession: {...}`), after
//      maxJobs windows, with a stuck Sim, a failed repro cleanup or a switch that did not go back, the next window gets a fresh
//      game start and load instead.
// Counter baselines are not part of the snapshot: every job (and every repro of a multi-repro job) reads /native and the
// patches counters at its own start, so deltas never span two windows.
//   node tools/harness/session-reuse.ts      self-check (fixtures, fake agent; no game)
import assert from 'node:assert/strict';
import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { parseRequire, startupDeltaLint } from './fixes-require.ts';
import { parseHitchTasks } from './hitch-tasks.ts';
import { parseRule } from './frame-lead.ts';
import { counterSwitches, nativeModeFields, nativeNames, nativeValues } from './native-switches.ts';

// ---- options
export type ReuseOpts = { maxJobs: number; simDelta: number; membersDelta: number; lotObjectsDelta: number; vasMB: number; minLargestFreeMB: number; simHours: number; stuck: number; onFail: 'auto' | 'restart' | 'stop' };
// maxJobs: windows per load (a multi-repro job counts each repro); simDelta: Sims in the world (townies and services come and go);
// membersDelta: active household members; lotObjectsDelta: objects on the active household's lot; vasMB: commit growth since the
// load; minLargestFreeMB: largest free address block (Error 12 margin); simHours: sim time since the load; stuck: stuck Sims a
// window may leave; onFail: after a crash or failed window, auto = TS3_NIGHT=1 (night-run.ts) restart fresh, else stop the queue
export const REUSE_DEFAULTS: ReuseOpts = { maxJobs: 4, simDelta: 8, membersDelta: 0, lotObjectsDelta: 5, vasMB: 300, minLargestFreeMB: 150, simHours: 1000, stuck: 0, onFail: 'auto' };
export function parseReuse(v: unknown, at = 'reuseSession'): ReuseOpts | null {
  if (v === undefined || v === false) return null;
  if (v === true) return { ...REUSE_DEFAULTS };
  if (!v || typeof v !== 'object' || Array.isArray(v)) throw new Error(`${at} must be true, false or { ${Object.keys(REUSE_DEFAULTS).join(', ')} }`);
  const o: any = { ...REUSE_DEFAULTS };
  for (const [k, x] of Object.entries(v)) {
    if (!(k in REUSE_DEFAULTS)) throw new Error(`${at}.${k}: unknown (known: ${Object.keys(REUSE_DEFAULTS).join(', ')})`);
    if (k === 'onFail') { if (!['auto', 'restart', 'stop'].includes(x as string)) throw new Error(`${at}.onFail must be auto, restart or stop`); o.onFail = x; continue; }
    if (!(typeof x === 'number' && Number.isFinite(x) && x >= 0)) throw new Error(`${at}.${k} must be a number >= 0`);
    if (k === 'maxJobs' && !(Number.isInteger(x) && x >= 1)) throw new Error(`${at}.maxJobs must be an integer >= 1`);
    o[k] = x;
  }
  return o;
}
// what a crash or failed window does to the queue (memory rule: night chains skip and go on, daytime stops)
export const failMode = (o: Pick<ReuseOpts, 'onFail'>, env: Record<string, string | undefined> = process.env) => o.onFail !== 'auto' ? o.onFail : env.TS3_NIGHT === '1' ? 'restart' : 'stop';

// same game start = same key: object keys sorted, so {a, b} and {b, a} ini maps compare equal
export const stableKey = (v: unknown): string => JSON.stringify(v, (_k, x) => typeof x === 'bigint' ? String(x) : x && typeof x === 'object' && !Array.isArray(x) ? Object.fromEntries(Object.keys(x).sort().map(k => [k, x[k]])) : x);
// '' when the job's run may share a load with its neighbours, else why not
export function reuseBlock(j: any): string {
  const kind = Object.keys(j.run ?? {})[0];
  if (!j.run?.profile) return `run.${kind} starts its own game`;
  if (j.run.profile.travel) return 'profile.travel changes the world';
  if (!j.save || j.save === 'menu') return 'no save to share';
  return '';
}

// ---- several repros per load: run.profile.repros
export type ReproItem = { id: string; minutes?: number; fixesRequire?: string; hitchTasks?: string[]; nativeRequire?: string; frameLead?: unknown };
const ITEM_KEYS = ['id', 'minutes', 'fixesRequire', 'hitchTasks', 'nativeRequire', 'frameLead'];
export function reproItems(v: unknown, at = 'run.profile.repros'): ReproItem[] {
  if (!Array.isArray(v) || !v.length) throw new Error(`${at} must be a non-empty list of repro ids or { ${ITEM_KEYS.join(', ')} }`);
  const seen = new Set<string>();
  return v.map((x0, i) => {
    const x: any = typeof x0 === 'string' ? { id: x0 } : x0, w = `${at}[${i}]`;
    if (!x || typeof x !== 'object' || typeof x.id !== 'string' || !/^[\w.-]+$/.test(x.id)) throw new Error(`${w}: id must be a repro id (letters, digits, . _ -)`);
    if (seen.has(x.id)) throw new Error(`${w}: repro ${x.id} twice (each repro gets its own tag <job tag>-<id>)`);
    seen.add(x.id);
    for (const k of Object.keys(x)) if (!ITEM_KEYS.includes(k)) throw new Error(`${w}.${k}: unknown (known: ${ITEM_KEYS.join(', ')})`);
    if (x.minutes !== undefined && !(Number(x.minutes) > 0)) throw new Error(`${w}.minutes must be > 0`);
    if (x.fixesRequire !== undefined) parseRequire(String(x.fixesRequire));
    if (x.nativeRequire !== undefined) { const lint = startupDeltaLint(String(x.nativeRequire)); if (lint.length) throw new Error(`${w}: ${lint.join('; ')}`); }
    if (x.hitchTasks !== undefined) parseHitchTasks(x.hitchTasks);
    if (x.frameLead !== undefined) parseRule(JSON.stringify(x.frameLead));
    return x as ReproItem;
  });
}
export const subTag = (jobTag: string, id: string) => `${jobTag}-${id}`;

// a repro's first step that only waits out the load minute ("lead-in" note) is not needed when no load came before the window
export const LEAD_IN_S = 15;
export const leadInCut = (steps: any[]) => steps.map((s, i) => i === 0 && /^lead-in\b/i.test(s?.note ?? '') && (s.waitS ?? 0) > LEAD_IN_S
  ? { ...s, waitS: LEAD_IN_S, note: `${s.note} (cut to ${LEAD_IN_S} s: no load before this window)` } : s);

// frames.csv rows (epochMs first) of [from, to] from the load's frame log into one run's file; returns the row count
export function sliceFrames(src: string, dst: string, from: number, to: number): number {
  if (!existsSync(src)) return 0;
  const [head, ...rows] = readFileSync(src, 'utf8').split(/\r?\n/);
  const keep = rows.filter(l => { const t = Number(l.slice(0, l.indexOf(','))); return l && t >= from && t <= to; });
  writeFileSync(dst, [head, ...keep].join('\n') + '\n');
  return keep.length;
}
// ts3native.log copy for one window of a shared load: the start-up part (up to the end of the load: hook OK lines, ini echo) plus
// what the window itself wrote, so logRequire and the --causes sync-plan join never see an earlier window's lines
export const logCopy = (b: Buffer, loadOff: number, from: number) => b.length < loadOff ? b : Buffer.concat([b.subarray(0, loadOff), b.subarray(Math.max(from, loadOff))]);

// ---- carry-over check
export type IO = { ag: (cmd: string, args?: object) => Promise<any>; get: (path: string) => Promise<any> };
export type Switches = { patches: Record<string, boolean>; native: Record<string, number> };
export type WorldSnap = Switches & { t: string; simMinutes: number | null; day: number | null; sims: number | null; members: number | null; household: string | null;
  activeLot: string | null; lotObjects: number | null; commitMB: number | null; privateMB: number | null; largestFreeMB: number | null };
const val = async (p: Promise<any>) => { try { const r = await p; return r && r.ok !== false ? r.result ?? null : null; } catch { return null; } };
const raw = async (p: Promise<any>) => { try { return (await p) ?? null; } catch { return null; } };
const num = (x: unknown) => typeof x === 'number' && Number.isFinite(x) ? x : null;

export async function readSwitches(io: IO): Promise<Switches> {
  const p = await val(io.ag('patches')), nat = await raw(io.get('native'));
  const patches = Object.fromEntries(Object.entries(p ?? {}).filter(([, v]) => typeof v === 'boolean')) as Record<string, boolean>;
  const native: Record<string, number> = {};
  for (const [n, f] of Object.entries(nativeModeFields)) if (num(nat?.[f]) !== null) native[n] = nat[f];
  // a read only (no value): offsets past the asi's counters block read as an error and are left out
  for (const [n, off] of Object.entries({ ...counterSwitches, ...nativeValues })) {
    const r = await raw(io.get(`nativeexport?name=ts3native_counters&offset=${off}`));
    if (num(r?.value) !== null) native[n] = r.value;
  }
  return { patches, native };
}
export async function snapshot(io: IO): Promise<WorldSnap> {
  const t = await val(io.ag('time')), gs = await val(io.ag('game_state')), sa = await val(io.ag('sims_activity')), wh = await val(io.ag('world_households'));
  const lot = Array.isArray(wh) ? wh.find((h: any) => h.name === gs?.active_household)?.lot ?? null : null;
  const lo = lot ? await val(io.ag('list_objects', { lot, include_sims: false, limit: 100000 })) : null;
  const mem = await raw(io.get('memory'));
  return { t: new Date().toISOString(), simMinutes: num(t?.minutes_total), day: num(t?.day_number), sims: Array.isArray(sa) ? sa.length : null,
    members: Array.isArray(sa) ? sa.filter((s: any) => s.active_household).length : null, household: gs?.active_household ?? null, activeLot: lot,
    lotObjects: Array.isArray(lo) ? lo.length : null, commitMB: num(mem?.commitMB), privateMB: num(mem?.privateMB), largestFreeMB: num(mem?.largestFreeMB), ...await readSwitches(io) };
}

// every switch back to its value after the load; a native value other than 0/1 cannot be written through the probe (failed)
export async function restoreSwitches(io: IO, def: Switches, cur: Switches): Promise<{ restored: string[]; failed: string[] }> {
  const restored: string[] = [], failed: string[] = [];
  const pset = Object.fromEntries(Object.entries(def.patches).filter(([k, v]) => cur.patches[k] !== v));
  if (Object.keys(pset).length) {
    const r = await val(io.ag('patches', pset));
    for (const [k, v] of Object.entries(pset)) (r?.[k] === v ? restored : failed).push(`${k}=${v}`);
  }
  for (const [n, v] of Object.entries(def.native)) {
    if (cur.native[n] === v) continue;
    if (v !== 0 && v !== 1) { failed.push(`${n}=${v} (the probe writes 0/1 only)`); continue; }
    const ok = nativeNames.includes(n) ? num((await raw(io.get(`native?${n}=${v}`)))?.[nativeModeFields[n]]) === v
      : num((await raw(io.get(`nativeexport?name=ts3native_counters&offset=${counterSwitches[n] ?? nativeValues[n]}&value=${v}`)))?.value) === v;
    (ok ? restored : failed).push(`${n}=${v}`);
  }
  return { restored, failed };
}

export type Extra = { windows: number; stuck: number; cleanupFailed?: boolean };
// reasons to give the next window a fresh game start; [] = the load may be shared on
export function drift(base: WorldSnap, cur: WorldSnap, o: ReuseOpts, x: Extra): string[] {
  const r: string[] = [];
  if (x.windows >= o.maxJobs) r.push(`${x.windows} windows since load (maxJobs ${o.maxJobs})`);
  const miss = (['simMinutes', 'sims', 'members', 'lotObjects', 'commitMB', 'largestFreeMB'] as const).filter(k => cur[k] === null || base[k] === null);
  if (miss.length) r.push(`snapshot incomplete (${miss.join(', ')})`);
  const d = (k: 'sims' | 'members' | 'lotObjects' | 'commitMB' | 'simMinutes') => cur[k] !== null && base[k] !== null ? cur[k]! - base[k]! : 0;
  if (Math.abs(d('sims')) > o.simDelta) r.push(`Sims ${base.sims} -> ${cur.sims} (limit +-${o.simDelta})`);
  if (Math.abs(d('members')) > o.membersDelta) r.push(`household members ${base.members} -> ${cur.members}`);
  if (cur.household !== base.household) r.push(`active household ${base.household} -> ${cur.household}`);
  if (cur.activeLot !== base.activeLot) r.push(`active lot ${base.activeLot} -> ${cur.activeLot}`);
  if (Math.abs(d('lotObjects')) > o.lotObjectsDelta) r.push(`lot objects ${base.lotObjects} -> ${cur.lotObjects} (limit +-${o.lotObjectsDelta})`);
  if (d('commitMB') > o.vasMB) r.push(`commit +${d('commitMB')} MB since load (limit ${o.vasMB})`);
  if (cur.largestFreeMB !== null && cur.largestFreeMB < o.minLargestFreeMB) r.push(`largest free block ${cur.largestFreeMB} MB (min ${o.minLargestFreeMB})`);
  if (d('simMinutes') > o.simHours * 60) r.push(`sim time +${(d('simMinutes') / 60).toFixed(1)} h since load (limit ${o.simHours} h)`);
  if (x.stuck > o.stuck) r.push(`${x.stuck} stuck Sims`);
  if (x.cleanupFailed) r.push('repro cleanup failed');
  const sw = [...Object.entries(base.patches).filter(([k, v]) => cur.patches[k] !== v).map(([k, v]) => `${k} ${cur.patches[k]} (load ${v})`),
    ...Object.entries(base.native).filter(([k, v]) => cur.native[k] !== v).map(([k, v]) => `${k} ${cur.native[k]} (load ${v})`)];
  if (sw.length) r.push(`switches not back: ${sw.join(', ')}`);
  return r;
}
const sgn = (x: number) => `${x >= 0 ? '+' : ''}${x}`;
export const driftLine = (base: WorldSnap, cur: WorldSnap, x: Extra, reasons: string[]) =>
  `${reasons.length ? `drift: ${reasons.join('; ')}` : 'drift ok'} (Sims ${cur.sims} (${sgn((cur.sims ?? 0) - (base.sims ?? 0))}), members ${cur.members}, lot objects ${cur.lotObjects} (${sgn((cur.lotObjects ?? 0) - (base.lotObjects ?? 0))}), ` +
  `commit ${sgn((cur.commitMB ?? 0) - (base.commitMB ?? 0))} MB, largest free ${cur.largestFreeMB} MB, sim +${(((cur.simMinutes ?? 0) - (base.simMinutes ?? 0)) / 60).toFixed(1)} h, stuck ${x.stuck}, window ${x.windows} since load)`;

// ---- self-check
if (process.argv[1] && import.meta.url === `file:///${process.argv[1].replace(/\\/g, '/').replace(/^\//, '')}`) {
  // options
  assert.equal(parseReuse(undefined), null); assert.equal(parseReuse(false), null);
  assert.deepEqual(parseReuse(true), REUSE_DEFAULTS);
  assert.equal(parseReuse({ maxJobs: 2, vasMB: 500, onFail: 'restart' })!.maxJobs, 2);
  assert.throws(() => parseReuse({ maxJob: 2 }), /unknown/);
  assert.throws(() => parseReuse({ maxJobs: 0 }), /maxJobs/);
  assert.throws(() => parseReuse({ simDelta: -1 }), />= 0/);
  assert.throws(() => parseReuse({ onFail: 'skip' }), /onFail/);
  assert.throws(() => parseReuse('yes'), /true, false/);
  assert.equal(failMode({ onFail: 'auto' }, { TS3_NIGHT: '1' }), 'restart');
  assert.equal(failMode({ onFail: 'auto' }, {}), 'stop');
  assert.equal(failMode({ onFail: 'restart' }, {}), 'restart');
  // keys and blocks
  assert.equal(stableKey({ ini: { b: 1, a: 2 }, save: 'X' }), stableKey({ save: 'X', ini: { a: 2, b: 1 } }));
  assert.notEqual(stableKey({ ini: { a: 1 } }), stableKey({ ini: { a: 2 } }));
  assert.equal(reuseBlock({ save: 'B', run: { profile: { minutes: 10 } } }), '');
  assert.match(reuseBlock({ save: 'B', run: { matrix: [] } }), /run.matrix/);
  assert.match(reuseBlock({ save: 'B', run: { profile: { minutes: 10, travel: 'france' } } }), /travel/);
  assert.match(reuseBlock({ save: 'menu', run: { profile: { minutes: 10 } } }), /no save/);
  // repro items
  assert.deepEqual(reproItems(['a', { id: 'b', minutes: 5, fixesRequire: 'x>=1', hitchTasks: ['Foo>=1'], nativeRequire: 'sypPlans.1>=1', frameLead: { service: 'World/Service', min: 3 } }]).map(r => r.id), ['a', 'b']);
  assert.throws(() => reproItems([]), /non-empty/);
  assert.throws(() => reproItems(['a', 'a']), /twice/);
  assert.throws(() => reproItems([{ id: 'a', ab: 'x' }]), /unknown/);
  assert.throws(() => reproItems([{ id: 'a', minutes: 0 }]), /minutes/);
  assert.throws(() => reproItems([{ id: 'a b' }]), /id must/);
  assert.equal(subTag('q-2026-10-22-bp-scen', 'scen-cas'), 'q-2026-10-22-bp-scen-scen-cas');
  // lead-in cut: only a first step noted "lead-in"
  const steps = [{ cmd: 'time', waitS: 75, note: 'lead-in: the first 60 s ...' }, { cmd: 'cas_open', waitS: 30 }];
  assert.deepEqual(leadInCut(steps).map(s => s.waitS), [15, 30]);
  assert.deepEqual(leadInCut([steps[1], steps[0]]).map(s => s.waitS), [30, 75]);
  assert.deepEqual(leadInCut([{ cmd: 'time', waitS: 10, note: 'lead-in' }]).map(s => s.waitS), [10]);
  // frames slice and log copy
  const dir = mkdtempSync(join(tmpdir(), 'reuse-')), src = join(dir, 'load-frames.csv'), dst = join(dir, 'run-600-frames.csv');
  writeFileSync(src, 'epochMs,qpcMs,frameMs\n1000,1,16\n2000,2,16\n3000,3,16\n4000,4,16\n');
  assert.equal(sliceFrames(src, dst, 2000, 3500), 2);
  assert.equal(readFileSync(dst, 'utf8'), 'epochMs,qpcMs,frameMs\n2000,2,16\n3000,3,16\n');
  assert.equal(sliceFrames(join(dir, 'none.csv'), dst, 0, 1), 0);
  rmSync(dir, { recursive: true, force: true });
  assert.equal(logCopy(Buffer.from('HEADjob1job2'), 4, 8).toString(), 'HEADjob2');
  assert.equal(logCopy(Buffer.from('HEADjob1'), 4, 4).toString(), 'HEADjob1');
  assert.equal(logCopy(Buffer.from('NEW'), 4, 8).toString(), 'NEW'); // the game restarted its log: all of it

  // fake game: switches, world, memory; a job flips switches and grows things
  const world = { minutes: 1000, sims: Array.from({ length: 40 }, (_, i) => ({ active_household: i < 4 })), objs: 200, commit: 2400, free: 600,
    patches: { gohome: true, overwatchspread: false, owspread_alarms: 3 } as Record<string, any>, native: { asyncMode: 1, earlyout: 2, fallbackasync: 1 } as Record<string, number> };
  const off = (n: string) => counterSwitches[n] ?? nativeValues[n];
  const io: IO = {
    ag: async (cmd, args: any) => {
      if (cmd === 'time') return { ok: true, result: { minutes_total: world.minutes, day_number: 3 } };
      if (cmd === 'game_state') return { ok: true, result: { active_household: 'Alto' } };
      if (cmd === 'sims_activity') return { ok: true, result: world.sims };
      if (cmd === 'world_households') return { ok: true, result: [{ name: 'Bunch', lot: 'A' }, { name: 'Alto', lot: 'Alto Estate' }] };
      if (cmd === 'list_objects') return { ok: true, result: args.lot === 'Alto Estate' ? Array.from({ length: world.objs }, () => ({})) : [] };
      if (cmd === 'patches') { Object.assign(world.patches, args ?? {}); return { ok: true, result: { ...world.patches } }; }
      return { ok: false, error: 'unknown' };
    },
    get: async p => {
      if (p === 'memory') return { commitMB: world.commit, privateMB: 2000, largestFreeMB: world.free };
      if (p.startsWith('native?routeasync=')) { world.native.asyncMode = Number(p.split('=')[1]); return { asyncMode: world.native.asyncMode }; }
      if (p === 'native') return { version: 98, asyncMode: world.native.asyncMode };
      const m = /offset=(\d+)(?:&value=(\d+))?/.exec(p);
      const name = m && ['earlyout', 'fallbackasync'].find(n => off(n) === Number(m[1]));
      if (!name) throw new Error('read past the block'); // other offsets: not in this asi
      if (m![2] !== undefined) world.native[name] = Number(m![2]);
      return { name: 'ts3native_counters', value: world.native[name] };
    },
  };
  const base = await snapshot(io);
  assert.deepEqual([base.simMinutes, base.sims, base.members, base.household, base.activeLot, base.lotObjects, base.commitMB, base.largestFreeMB], [1000, 40, 4, 'Alto', 'Alto Estate', 200, 2400, 600]);
  assert.deepEqual(base.patches, { gohome: true, overwatchspread: false }); // counters are not switches
  assert.deepEqual(base.native, { routeasync: 1, earlyout: 2, fallbackasync: 1 });
  // a job: --ab turns gohome on/off and leaves it on, native_set flips fallbackasync and routeasync; time passes, 3 Sims arrive
  world.patches.gohome = false; world.patches.overwatchspread = true; world.native.fallbackasync = 0; world.native.asyncMode = 0;
  world.minutes += 600; world.sims.push({ active_household: false }, { active_household: false }, { active_household: false }); world.commit += 120;
  const rs = await restoreSwitches(io, base, await readSwitches(io));
  assert.deepEqual(rs, { restored: ['gohome=true', 'overwatchspread=false', 'routeasync=1', 'fallbackasync=1'], failed: [] });
  let cur = await snapshot(io);
  assert.deepEqual(drift(base, cur, REUSE_DEFAULTS, { windows: 1, stuck: 0 }), []);
  assert.match(driftLine(base, cur, { windows: 1, stuck: 0 }, []), /^drift ok \(Sims 43 \(\+3\), members 4, lot objects 200 \(\+0\), commit \+120 MB, largest free 600 MB, sim \+10\.0 h, stuck 0, window 1 since load\)$/);
  // every limit on its own
  const o = REUSE_DEFAULTS, x = { windows: 1, stuck: 0 };
  const with_ = (p: Partial<WorldSnap>) => drift(base, { ...cur, ...p }, o, x);
  assert.match(with_({ sims: 49 }).join(), /Sims 40 -> 49/);
  assert.match(with_({ members: 5 }).join(), /household members 4 -> 5/);
  assert.match(with_({ household: 'Other' }).join(), /active household/);
  assert.match(with_({ activeLot: 'B' }).join(), /active lot Alto Estate -> B/);
  assert.match(with_({ lotObjects: 206 }).join(), /lot objects 200 -> 206/);
  assert.equal(with_({ lotObjects: 205 }).length, 0);
  assert.match(with_({ commitMB: 2701 }).join(), /commit \+301 MB/);
  assert.match(with_({ largestFreeMB: 149 }).join(), /largest free block 149/);
  assert.match(with_({ simMinutes: 1000 + 1000 * 60 + 1 }).join(), /sim time/);
  assert.match(with_({ sims: null }).join(), /snapshot incomplete \(sims\)/);
  assert.match(drift(base, cur, o, { windows: 4, stuck: 0 }).join(), /4 windows since load \(maxJobs 4\)/);
  assert.match(drift(base, cur, o, { windows: 1, stuck: 1 }).join(), /1 stuck Sims/);
  assert.match(drift(base, cur, o, { windows: 1, stuck: 0, cleanupFailed: true }).join(), /cleanup failed/);
  assert.equal(drift(base, cur, { ...o, stuck: 2 }, { windows: 1, stuck: 2 }).length, 0);
  // a mode value the probe cannot write back fails, and the switch shows as not back
  world.native.earlyout = 0;
  const rs2 = await restoreSwitches(io, base, await readSwitches(io));
  assert.deepEqual(rs2, { restored: [], failed: ['earlyout=2 (the probe writes 0/1 only)'] });
  cur = await snapshot(io);
  assert.match(drift(base, cur, o, x).join(), /switches not back: earlyout 0 \(load 2\)/);
  // a patches answer that does not take the value fails
  const deaf: IO = { ...io, ag: async (c, a) => c === 'patches' && a ? { ok: true, result: { gohome: false } } : io.ag(c, a) };
  world.patches.gohome = false;
  assert.deepEqual((await restoreSwitches(deaf, { patches: { gohome: true }, native: {} }, await readSwitches(io))).failed, ['gohome=true']);
  console.log('session-reuse selftest: PASS');
}
