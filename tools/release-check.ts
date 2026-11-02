// Release check generator (docs/170 §4): builds the release test queue from what changed since the previous release, so a
// release is checked in hours instead of the 46-job release-validate.json night. Parts:
//   a  smoke: BP/IP/SV short play with the measurement settings (fixed)
//   b  save matrix: BP always, SV/IP + the other travel cases only when a save-path feature changed (feature map group @save)
//   c  one forced repro per shipped feature that is new or changed, fix ON only, fixed event count (counters rise, no crash)
//   d  bundle A/B on those forced loop repros: every new ship-set switch off vs on together, ABBA, fixed events per arm
//   e  the old release-validate.json rare jobs of shipped features whose code changed (git diff, tools/harness/feature-repros.json),
//      only those no forced loop reaches (rare); the rest are listed as replaced by the forced loop (replaces, docs/170 §5)
//   f  one soak: 45 min natural play (real-hitch metrics, VAS growth from the queue's /memory before/after)
// Usage:
//   node tools/release-check.ts <prev tag|commit> [--features id,id] [--asi game-copy/ts3native.asi.rel020] [--min-counters 98]
//        [--fixes-build ts3fixes.vX.Y.Z] [--out tools/harness/queues/release-check.json] [--write] [--install-repros]
//   --fixes-build (required with --write): the release ts3fixes.package entry in docs/data/builds.json (tools/builds.ts); every job
//   gets it as fixesBuild, so queue.ts refuses a job whose installed package does not have that tuning (2026-10-24 stale package).
//   node tools/release-check.ts --self-check
// Without --write it prints the plan, the time estimate per part and the coverage table against release-validate.json.
// --write writes the queue and tools/harness/release-check-repros.json (the derived rc-* loop repros). profile-world.ts reads
// repros only from tools/harness/repros.json, so --install-repros merges the rc-* entries into it (replaces rc-* ids only);
// run it on the release branch once nobody else is editing repros.json. Ship set = --features, else the features the repo
// ini/tuning have on now (tools/release-features.ts status). A release tag that is missing locally resolves through
// feature-repros.json "releases" (v0.1.4 = d6ff7a91, the commit the v0.1.4 asi was built from).
// TODO(multi-repro, session reuse): queue.ts/profile-world.ts fields for several repros in one load and for reusing a loaded
// session are not on master yet. Once they are, group parts c-e by save (part e is already sorted by save) so each world
// loads once (~1.7 min load + profiler start per job saved), and let the soak save at its end in the same load (save size
// after 45 min of play). Until then every job is a plain one-load job and the queue runs as is.
import { execFileSync } from 'node:child_process';
import { existsSync, readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import assert from 'node:assert/strict';
import { FEATURES as RF, isOn, setIni, setTuning } from './release-features.ts';
import { checkLoop, isSetup } from './harness/repro-loop.ts';
import { loadDb, sha256, tuningXmlOf } from './builds.ts';

const ROOT = resolve(import.meta.dirname, '..');
const H = (f: string) => join(ROOT, 'tools', 'harness', f);
const INI_PATH = 'native/ts3native/ts3native.ini', TUNING_PATH = 'tools/setup/build-fixes.ts';

type Req = { nativeRequire?: string; fixesRequire?: string };
export type Feature = Req & { what: string; on?: { ini?: Record<string, string>; tuning?: string[] }; native?: string[]; fixes?: string[]; values?: string[];
  paths: string[]; match?: string; repro?: { id: string; minutes?: number; noEnv?: boolean; save?: string; onEvents?: number; bundlePerArm?: number };
  needsRepro?: string; task?: string; rare: string[]; rareWhy?: string; replaces?: string[]; replacesWhy?: string };
export type FeatureMap = { releases: Record<string, string>; shared: string[]; groups: Record<string, string[]>; features: Record<string, Feature> };
type Step = { cmd: string; args?: any; waitS?: number; note?: string; setup?: boolean; optional?: boolean; waitUntil?: unknown; minS?: number; maxS?: number };
type Repro = { id: string; save?: string; bug?: string; fix?: string; note?: string; steps?: Step[]; loop?: { steps: Step[]; arms: string[]; until: any; maxMinutes?: number }; cleanup?: Step[] };
type Job = Record<string, any> & { id: string };

// measurement settings of tool-check.json (docs/169) minus every feature switch: logging and attribution only
const MEASURE_INI = { 'HitchAttr.Enabled': 1, 'HitchAttr.Holders': 1, 'Harness.ForegroundRender': 1, 'RouteJobPhases.Enabled': 1, 'RouteJobPhases.SyncPlans': 1,
  'RouteAsync.LogFallbackMs': 20, 'RouteAsync.LogFallbackStack': 16 };
// part c firings per forced loop (fix on only): enough to re-create the state and fire again several times; correctness, not
// power (docs/170 §5: 6 -> 4)
export const ON_EVENTS = 4;
const SMOKE = [['bp', 'Baseline-BP-Spring'], ['ip', 'Baseline-IP-Spring'], ['sv', 'Baseline-SV-Spring']];

// ---------------------------------------------------------------- ship set and change detection
// a feature is on when every one of its on-keys already has its on value (setIni/setTuning log no edit); a key missing in an
// older ini means off
export function onIn(f: Feature, id: string, ini: string, tuning: string): boolean {
  if (!f.on && RF[id]) { try { return isOn(id, ini, tuning); } catch { return false; } }
  const on = f.on;
  if (!on) return false;
  const log: string[] = [];
  try {
    for (const [k, v] of Object.entries(on.ini ?? {})) setIni(ini, k, v, log);
    for (const t of on.tuning ?? []) setTuning(tuning, t, true, log);
  } catch { return false; }
  return log.length === 0;
}

const git = (...a: string[]) => execFileSync('git', ['-C', ROOT, ...a], { encoding: 'utf8', maxBuffer: 256 << 20 });
export function resolveRev(rev: string, map: FeatureMap): string {
  for (const r of [rev, map.releases[rev]].filter(Boolean)) { try { return git('rev-parse', '--verify', '--quiet', `${r}^{commit}`).trim(); } catch { /* next */ } }
  throw new Error(`no commit for ${rev} (not a tag/commit here and not in feature-repros.json releases)`);
}
// changed lines (+/-, no file headers) of a unified diff
export const changedLines = (diff: string) => diff.split('\n').filter(l => /^[+-]/.test(l) && !/^(\+\+\+|---) /.test(l));
// feature -> why it counts as changed (file list hit, or a changed line of a shared file matching its regex); test files never count
export function changedFeatures(map: FeatureMap, files: string[], sharedLines: string[]): Map<string, string> {
  const real = files.filter(f => !/(^|\/)selftest\.cpp$|-test\.(h|cpp)$|-fake\.h$/.test(f));
  const out = new Map<string, string>();
  for (const [id, f] of Object.entries(map.features)) {
    const hit = real.filter(p => f.paths.includes(p));
    if (hit.length) { out.set(id, hit.join(', ')); continue; }
    if (f.match) { const re = new RegExp(f.match); const n = sharedLines.filter(l => re.test(l)).length; if (n) out.set(id, `${n} changed lines in shared files match /${f.match}/`); }
  }
  return out;
}

// ---------------------------------------------------------------- repro derivation
const ARM = /^arm (on|off) #(\d+)/;
const isToggle = (s: Step) => (s.cmd === 'patches' || s.cmd === 'native_set') && s.args && typeof s.args === 'object' && Object.keys(s.args).length > 0;
const cleanNote = (n?: string) => n?.replace(/ (on|off) #\d+/, '').trim();
const secs = (steps: Step[]) => steps.reduce((t, s) => t + (s.waitS ?? 0), 0);
// expected seconds: adaptive steps (docs/170 §3) at the middle of minS..maxS [inferred], fixed steps at waitS; secs() stays the cap basis
const estSecs = (steps: Step[]) => steps.reduce((t, s) => t + (s.waitUntil && s.maxS ? ((s.minS ?? 0) + s.maxS) / 2 : s.waitS ?? 0), 0);
const isBool = (v: unknown) => v === true || v === false || v === 0 || v === 1;

// the firing body of an ABBA repro: prefix steps, one on firing and one off firing with the same step list
export function firing(r: Repro): { prefix: Step[]; on: Step[]; off: Step[] } | null {
  if (r.loop) {
    const sub = (v: any, on: boolean): any => v === '$on' ? on : Array.isArray(v) ? v.map(x => sub(x, on)) : v && typeof v === 'object' ? Object.fromEntries(Object.entries(v).map(([k, x]) => [k, sub(x, on)])) : v;
    return { prefix: r.steps ?? [], on: r.loop.steps.map(s => ({ ...s, args: sub(s.args, true) })), off: r.loop.steps.map(s => ({ ...s, args: sub(s.args, false) })) };
  }
  const steps = r.steps ?? [], at = steps.flatMap((s, i) => ARM.test(s.note ?? '') ? [i] : []);
  if (at.length < 2) return null;
  const len = at[1] - at[0], block = (i: number) => steps.slice(i, i + len);
  const on = at.find(i => ARM.exec(steps[i].note!)![1] === 'on'), off = at.find(i => ARM.exec(steps[i].note!)![1] === 'off');
  if (on === undefined || off === undefined) return null;
  const b = block(on), c = block(off);
  if (b.map(s => s.cmd).join() !== c.map(s => s.cmd).join()) throw new Error(`${r.id}: on and off firings differ in their steps`);
  return { prefix: steps.slice(0, at[0]), on: b.map(s => ({ ...s, note: cleanNote(s.note) })), off: c.map(s => ({ ...s, note: cleanNote(s.note) })) };
}

export type Owners = Record<string, { id: string; value: boolean }>;   // switch name -> feature, value = a cap (not on/off)
export function owners(map: FeatureMap): Owners {
  const o: Owners = {};
  for (const [id, f] of Object.entries(map.features)) {
    for (const s of [...(f.native ?? []), ...(f.fixes ?? [])]) o[s] ??= { id, value: false };
    for (const s of f.values ?? []) o[s] ??= { id, value: true };
  }
  return o;
}

// mode 'on': fix ON only, arms [on], until N firings. Toggle keys: owned by the target or a ship-set feature -> on value,
// by a feature not shipped -> its off value (the release does not run it), unknown -> on value (warned).
// mode 'ab': arms ABBA until perArm; keys of the bundle (new ship-set features) -> "$on" when on/off-valued, caps -> ship value;
// the bundle switches the repro does not toggle itself are added as an arm step at the body's start.
export function derive(r: Repro, mode: { kind: 'on'; target: string; events: number } | { kind: 'ab'; bundle: Set<string>; perArm: number },
  ship: Set<string>, own: Owners, map: FeatureMap, warn: (m: string) => void): Repro | null {
  const fi = firing(r);
  if (!fi) return null;
  const body: Step[] = fi.on.map((s, i) => {
    if (!isToggle(s)) return s;
    const args: Record<string, unknown> = {};
    for (const [k, v] of Object.entries(s.args)) {
      const off = fi.off[i].args?.[k], o = own[k];
      if (!o) { if (v !== off) warn(`${r.id}: switch ${k} has no owner in feature-repros.json: kept at its on value`); args[k] = v; continue; }
      if (mode.kind === 'on') args[k] = o.id === mode.target || ship.has(o.id) ? v : off;
      else args[k] = mode.bundle.has(o.id) && !o.value && isBool(v) && isBool(off) ? '$on' : ship.has(o.id) ? v : off;
    }
    return { ...s, args };
  });
  if (mode.kind === 'ab') {
    const has = new Set(body.filter(isToggle).flatMap(s => Object.keys(s.args)));
    const nat = [...mode.bundle].flatMap(id => map.features[id].native ?? []).filter(n => !has.has(n));
    const fix = [...mode.bundle].flatMap(id => map.features[id].fixes ?? []).filter(n => !has.has(n));
    body.unshift(...(nat.length ? [{ cmd: 'native_set', args: Object.fromEntries(nat.map(n => [n, '$on'])), waitS: 1, note: 'bundle arm' }] : []),
      ...(fix.length ? [{ cmd: 'patches', args: Object.fromEntries(fix.map(n => [n, '$on'])), waitS: 1, note: 'bundle arm' }] : []));
  }
  const firings = mode.kind === 'on' ? mode.events : Math.ceil(2 * mode.perArm / 4) * 4;
  const capS = secs(fi.prefix) + firings * secs(body), expectS = estSecs(fi.prefix) + firings * estSecs(body);
  // every loop step marked setup: true/false explicitly (repro-loop.ts self-check rule for repros.json, docs/168 §7)
  const loop = { steps: body.map(s => ({ ...s, setup: isSetup(s) })), arms: mode.kind === 'on' ? ['on'] : ['off', 'on', 'on', 'off'], until: mode.kind === 'on' ? { events: mode.events } : { perArm: mode.perArm },
    maxMinutes: Math.ceil(capS * 1.5 / 60) };
  checkLoop(loop);
  const id = mode.kind === 'on' ? `rc-on-${mode.target}` : `rc-ab-${r.id}`;
  return { id, save: r.save, bug: r.bug, fix: mode.kind === 'on' ? `${mode.target} on only (release-check part c)` : `bundle ${[...mode.bundle].join(',')} off vs on (release-check part d)`,
    note: `Generated by tools/release-check.ts from ${r.id}; do not edit (regenerate). ${mode.kind === 'on' ? `Fix on only, stops after ${mode.events} firings` : `ABBA, stops at ${mode.perArm} firings per arm (fixed before the run, docs/168 §6)`}; expected ${(expectS / 60).toFixed(1)} min, cap ${loop.maxMinutes} min.`,
    steps: fi.prefix, loop, ...(r.cleanup && { cleanup: r.cleanup }) };
}

// ---------------------------------------------------------------- measured job times (docs/data/results)
export type Times = { overhead: number; byId: Map<string, number>; matrixCases: number; reload: number };
export function measuredTimes(validate: Job[]): Times {
  const dir = join(ROOT, 'docs', 'data', 'results'), byId = new Map<string, number[]>(), over: number[] = [], spans: number[] = [], reload: number[] = [];
  const med = (a: number[], d: number) => a.length ? [...a].sort((x, y) => x - y)[a.length >> 1] : d;
  const prof = new Map(validate.filter(j => j.run.profile && !j.run.profile.travel).map(j => [j.id, j.run.profile.minutes]));
  for (const f of existsSync(dir) ? readdirSync(dir) : []) {
    const text = readFileSync(join(dir, f), 'utf8');
    if (/^Machine: (?!main\b)/m.test(text)) continue; // another machine's runs (TS3_MACHINE): its times never feed the main machine's estimates
    if (/-queue-/.test(f)) for (const m of text.matchAll(/^\| ([\w.-]+) \| \d\d:\d\d[^|]*\| ([\d.]+) \| (\*\*FAIL\*\*|PASS)/gm)) {
      const [, id, min, res] = m;
      if (res !== 'PASS') continue;
      (byId.get(id) ?? byId.set(id, []).get(id)!).push(Number(min));
      if (prof.has(id)) over.push(Number(min) - prof.get(id)!);
      if (/reload-run/.test(id)) reload.push(Number(min));
    }
    if (/save-matrix\.md$/.test(f)) for (const run of text.split(/^## /m)) {
      const t = [...run.matchAll(/^\| (\d\d):(\d\d):(\d\d) \|/gm)].map(m => +m[1] * 60 + +m[2] + +m[3] / 60);
      if (t.length >= 9) spans.push(t.at(-1)! - t[0]);
    }
  }
  return { overhead: med(over, 1.7), byId: new Map([...byId].map(([k, v]) => [k, med(v, 0)])), matrixCases: med(spans, 3.5), reload: med(reload, 4.7) };
}

// ---------------------------------------------------------------- queue
export function build(o: { map: FeatureMap; repros: Repro[]; validate: Job[]; ship: Set<string>; prevShip: Set<string>; changed: Map<string, string>;
  asi: string; minCounters: number; iniOverride: Record<string, unknown>; fixesOn: string; times: Times; warn: (m: string) => void; fixesBuild?: string }) {
  const { map, repros, validate, ship, prevShip, changed, times } = o;
  const own = owners(map), newShip = new Set([...ship].filter(id => !prevShip.has(id)));
  const touched = [...ship].filter(id => newShip.has(id) || changed.has(id));
  const jobs: (Job & { part: string; estMin: number; est: string })[] = [], derived: Repro[] = [], needs: string[] = [];
  const base = (id: string, save: string, run: any, timeoutMin: number, note: string, extra: Record<string, unknown> = {}) => ({ id, note, asi: o.asi, ...(o.fixesBuild && { fixesBuild: o.fixesBuild }), save, minCounters: o.minCounters,
    ini: { ...MEASURE_INI, ...o.iniOverride }, run, gameArgs: ['-disablestoryprogression'], timeoutMin, releaseDefaults: true, ...extra });
  const profile = (p: Record<string, unknown>) => ({ profile: { seed: 12345, noSamplers: true, ...(o.fixesOn && { fixesOn: o.fixesOn }), ...p } });
  const old = (id: string) => validate.find(j => j.id === id);
  const oldJob = (id: string, part: string) => {
    const j = old(id)!;
    const est = times.byId.get(id), m = j.run.profile?.minutes;
    const run = j.run.profile && o.fixesOn ? { profile: { ...j.run.profile, fixesOn: o.fixesOn } } : j.run;
    jobs.push({ ...j, asi: o.asi, releaseDefaults: true, ...(o.fixesBuild && { fixesBuild: o.fixesBuild }), minCounters: o.minCounters, ...(Object.keys(o.iniOverride).length && { ini: { ...o.iniOverride } }), run, part,
      estMin: est ?? (j.run.matrix ? matrixMin : (m ?? 5) + times.overhead), est: est ? 'measured (v0.1.4 release-validate)' : '[inferred]' });
  };
  const matrixMin = times.overhead + times.matrixCases + 2 * times.reload;
  // a smoke
  for (const [w, save] of SMOKE) jobs.push({ ...base(`${w}-rc-smoke`, save, profile({ minutes: 6, cameraPan: true }), 20,
    'Release check part a (smoke): 6 min play, release ini + measurement logging. Pass: 0 crashes, 0 watchdog lines, ts3native version as docs/RELEASE.md, asyncStarted > 0, every tripwire of docs/RELEASE.md 0, route fails per minute not above v0.1.4 (2026-10-20 release-validate *-rel-play) by > 10%. Read: freeze-report.ts and hitch-ab.ts <tag>.'),
    part: 'a smoke', estMin: 6 + times.overhead, est: 'measured overhead' });
  // b save matrix (+ travel france always; SV/IP matrices and the other trips through the @save group in part e)
  oldJob('bp-rel-matrix', 'b save matrix'); jobs.at(-1)!.est = `[inferred] ${times.matrixCases.toFixed(1)} min for 9 cases + 2 reload loads (${times.reload.toFixed(1)} min each), all measured apart`;
  oldJob('bp-rel-travel-france', 'b save matrix');
  // c forced repro, fix on only: one job per source repro, so features that share a forced loop (e.g. routegate, tailunlock,
  // h2 on route-gate-crowd-loop) share one load with all their counter requirements (docs/170 §5)
  const groups = new Map<string, string[]>();
  for (const id of touched) {
    const f = map.features[id];
    if (!f.repro) { needs.push(`${id}: ${f.needsRepro ?? 'no repro mapped'}`); continue; }
    if (!repros.some(r => r.id === f.repro!.id)) { needs.push(`${id}: repro ${f.repro.id} missing in repros.json`); continue; }
    (groups.get(f.repro.id) ?? groups.set(f.repro.id, []).get(f.repro.id)!).push(id);
  }
  for (const [rid, ids] of groups) {
    const src = repros.find(r => r.id === rid)!, fs = ids.map(id => map.features[id]), label = ids.length > 1 ? rid : ids[0];
    const events = Math.max(...fs.map(f => f.repro!.onEvents ?? ON_EVENTS));
    const d = derive(src, { kind: 'on', target: label, events }, ship, own, map, o.warn);
    const save = fs[0].repro!.save ?? src.save ?? 'Baseline-BP-Spring', noEnv = fs.some(f => f.repro!.noEnv);
    const n = d ? String(events) : '1', join = (k: 'fixesRequire' | 'nativeRequire') => fs.map(f => f[k]?.replaceAll('{n}', n)).filter(Boolean).join(',');
    const req: Record<string, unknown> = {};
    if (join('fixesRequire')) req.fixesRequire = join('fixesRequire');
    const native = join('nativeRequire') ? { nativeRequire: join('nativeRequire') } : {};
    const what = ids.map((id, i) => `${id} (${fs[i].what})`).join(', '), task = fs.map(f => f.task).filter(Boolean).join(' / ');
    const read = `Read: hitch lines in the window (hitch-ab.ts --scenario <tag>) should name ${task || 'the fix\'s path'} when they occur, not the game's one-tick path.`;
    const pass = `${req.fixesRequire ? ` (${req.fixesRequire})` : ''}${native.nativeRequire ? `, ${native.nativeRequire}` : ''}`;
    if (d) {
      derived.push(d);
      const expect = estSecs(d.steps ?? []) / 60 + events * estSecs(d.loop!.steps) / 60;
      jobs.push({ ...base(`rc-on-${label}`, save, profile({ minutes: d.loop!.maxMinutes! + 1, repro: d.id, ...req, ...(noEnv && { noEnv: true }) }), d.loop!.maxMinutes! + 16,
        `Release check part c: ${what} on only, forced repro ${src.id} until ${events} firings. Pass: no crash, the stop line, counters rose${pass}. ${read}`, native),
        part: 'c fix on', estMin: expect + times.overhead, est: '[inferred] from step waits (adaptive steps at mid minS..maxS)' });
    } else {
      const m = Math.max(...fs.map(f => f.repro!.minutes ?? 6));
      jobs.push({ ...base(`rc-on-${label}`, save, profile({ minutes: m, repro: src.id, ...req, ...(noEnv && { noEnv: true }) }), m + 15,
        `Release check part c: ${what} on, plain forced repro ${src.id} (${m} min, no fixed-count loop yet: needs a loop repro for part d). Pass: no crash${pass}. ${read}`, native),
        part: 'c fix on', estMin: m + times.overhead, est: 'measured overhead' });
    }
  }
  // d bundle A/B on the loop repros of the new features (one per source repro). A changed-but-shipped feature's loop runs
  // fix-on in part c; an A/B there would flip only the new switches on a repro not aimed at them (docs/170 §5).
  const bundle = new Set([...newShip].filter(id => (map.features[id].native ?? []).length + (map.features[id].fixes ?? []).length));
  const abSrc = [...new Set([...newShip].map(id => map.features[id].repro?.id).filter((r): r is string => !!r))]
    .map(r => repros.find(x => x.id === r)!).filter(r => r && firing(r));
  if (bundle.size) for (const src of abSrc) {
    const per = Math.max(...[...newShip].filter(id => map.features[id].repro?.id === src.id).map(id => map.features[id].repro!.bundlePerArm ?? 10));
    const d = derive(src, { kind: 'ab', bundle, perArm: per }, ship, own, map, o.warn)!;
    derived.push(d);
    const fir = Math.ceil(2 * per / 4) * 4, expect = (estSecs(d.steps ?? []) + fir * estSecs(d.loop!.steps)) / 60;
    jobs.push({ ...base(`rc-ab-${src.id}`, src.save ?? 'Baseline-BP-Spring', profile({ minutes: d.loop!.maxMinutes! + 1, repro: d.id }), d.loop!.maxMinutes! + 16,
      `Release check part d: bundle ${[...bundle].join(',')} off vs on together (off = the previous release for all of them), ABBA on ${src.id} until ${per} firings per arm. Pass: no crash, no tripwire, the stop line. Read: hitch-ab.ts --scenario <tag> (per-arm hitches and causes); on must not be worse than off beyond the docs/136 null.`),
      part: 'd bundle A/B', estMin: expect + times.overhead, est: '[inferred] from step waits' });
  }
  // e old rare jobs of shipped features whose code changed (new features count as changed)
  const want = new Map<string, string[]>(), replaced: Replaced = new Map();
  const expand = (xs: string[]) => xs.flatMap(x => x.startsWith('@') ? map.groups[x.slice(1)] ?? [] : [x]);
  for (const id of touched) for (const r of expand(map.features[id].rare)) (want.get(r) ?? want.set(r, []).get(r)!).push(id);
  // old jobs a changed feature's forced loop stands in for (docs/170 §5): listed in the coverage table, not run
  for (const id of touched) for (const r of expand(map.features[id].replaces ?? [])) if (!want.has(r))
    (replaced.get(r) ?? replaced.set(r, { loops: [], why: map.features[id].replacesWhy ?? 'forced loop' }).get(r)!).loops.push(`${map.features[id].repro?.id ?? '?'} (${id})`);
  for (const [r, by] of [...want].sort(([a], [b]) => String(old(a)?.save).localeCompare(String(old(b)?.save)))) if (old(r) && !jobs.some(j => j.id === r)) { oldJob(r, 'e rare'); jobs.at(-1)!.note += ` [release-check: back because ${by.join(', ')} changed]`; }
  // f soak
  jobs.push({ ...base('bp-rc-soak', 'Baseline-BP-Spring', profile({ minutes: 45 }), 60,
    'Release check part f (soak): 45 min natural play, no repro. Pass: no crash, 0 watchdog lines, stuck Sims within docs/TESTING.md. Read: hitch-ab.ts <tag> (docs/161 real-hitch counts, p99, max), freeze-report.ts <tag>, VAS growth = the row\'s commit/private/largest free MB before -> after (v0.1.4 bp-rel-play as reference). TODO(session reuse): save at the end in the same load and record the save folder MB (today: the matrix saves give save size, not after 45 min of play).'),
    part: 'f soak', estMin: 45 + times.overhead, est: 'measured overhead' });
  return { jobs, derived, needs, touched, newShip, want, replaced };
}

// minutes saved when parts c-e and the soak share one load per save (multi-repro / session reuse, docs/170 §5): every job
// after the first of its save skips the load and profiler start (times.overhead). Optional factor, not in the totals.
export function reuseSaving(jobs: { part: string; save?: string }[], overhead: number): number {
  const per = new Map<string, number>();
  for (const j of jobs) if (/^[cdef] /.test(j.part)) per.set(String(j.save), (per.get(String(j.save)) ?? 0) + 1);
  return [...per.values()].reduce((t, n) => t + (n - 1) * overhead, 0);
}

// what release-validate.json ran that this queue does not, with the reason or what replaces it
export type Replaced = Map<string, { loops: string[]; why: string }>;   // old job -> forced loops (feature) standing in, first reason
export function coverage(validate: Job[], jobs: Job[], want: Map<string, string[]>, secondWorld: string[] = [], replaced: Replaced = new Map()): string[] {
  const have = new Set(jobs.map(j => j.id)), rows: string[] = [];
  const why = (id: string) =>
    replaced.has(id) ? `replaced by forced loop ${replaced.get(id)!.loops.join(', ')}: ${replaced.get(id)!.why}`
    : secondWorld.includes(id) ? 'second world of a rare route mode: the mode runs in its first world when its features changed; add @route-all-worlds to a feature with a world-specific risk'
    : /-rel-play$/.test(id) ? 'smoke a (6 min per world instead of 12) + soak f (45 min BP) cover play length; same pass rules'
    : id === 'bp-rel-stuck-unseen' ? 'soak f (45 min BP, stuck Sims rule) replaces the 15 min stuck run; unseen throttle has its own part c repro when shipped'
    : /-rel-matrix$/.test(id) ? 'save code unchanged since the previous release: its full matrices passed there; BP matrix still runs every release'
    : /-rel-travel-/.test(id) ? 'travel/save code unchanged: france still runs every release, the other trips come back with @save'
    : /-rel-feat-/.test(id) ? 'feature repro of a feature not shipped on or unchanged: part c runs the forced repro of every new/changed shipped feature (fix on, counters)'
    : /-rel-rare-/.test(id) ? 'rare route mode of unchanged shipped features: passed in the previous release on the same code; new features pull their own rare jobs (part e); interactions: bundle d + soak f'
    : 'not mapped';
  for (const j of validate) if (!have.has(j.id) && !want.has(j.id)) rows.push(`| ${j.id} | ${why(j.id)} |`);
  return rows;
}

// ---------------------------------------------------------------- main
function selfCheck() {
  const map: FeatureMap = { releases: {}, shared: [], groups: { g: ['old-rare'] }, features: {
    a: { what: 'A', on: { ini: { 'A.Enabled': '1' } }, native: ['a'], paths: ['src/a.cpp'], match: 'AThing', repro: { id: 'r' }, fixesRequire: 'a_run>={n}', rare: ['@g'] },
    b: { what: 'B', on: { ini: { 'B.Enabled': '1' } }, fixes: ['b'], values: ['bcap'], paths: [], match: 'BThing', rare: [] },
    c: { what: 'C', on: { ini: { 'C.Enabled': '1' } }, fixes: ['c'], paths: [], rare: [] } } };
  const ini = '[A]\nEnabled=1\n[B]\nEnabled=0\n[C]\nEnabled=1\n';
  assert.equal(onIn(map.features.a, 'a', ini, ''), true);
  assert.equal(onIn(map.features.b, 'b', ini, ''), false);
  assert.equal(onIn(map.features.a, 'a', '[X]\n', ''), false, 'missing key = off');
  const ch = changedFeatures(map, ['src/a.cpp', 'x/selftest.cpp'], changedLines('--- a/x\n+++ b/x\n+int BThing = 1;\n context\n-old\n'));
  assert.deepEqual([...ch.keys()], ['a', 'b']);
  assert.equal(changedFeatures(map, ['src/a-test.h'], []).size, 0, 'test files do not count');
  // hand-written ABBA: arm steps toggle a (native) and c (patches); b unshipped; z unknown
  const fire = (arm: 'on' | 'off', i: number): Step[] => [
    { cmd: 'native_set', args: { a: arm === 'on' ? 1 : 0, bcap: arm === 'on' ? 500 : 0 }, waitS: 1, note: `arm ${arm} #${i}` },
    { cmd: 'patches', args: { c: arm === 'on', b: arm === 'on', z: arm === 'on' }, waitS: 1 },
    { cmd: 'reflect_call', args: { path: 'X' }, waitS: 20, note: `fire ${arm} #${i}` }];
  const r: Repro = { id: 'r', save: 'S', steps: [{ cmd: 'time', waitS: 75 }, ...fire('off', 1), ...fire('on', 2), ...fire('on', 3), ...fire('off', 4), { cmd: 'patches', args: {} }] };
  const warns: string[] = [];
  const on = derive(r, { kind: 'on', target: 'a', events: 6 }, new Set(['a', 'c']), owners(map), map, m => warns.push(m))!;
  assert.equal(on.id, 'rc-on-a');
  assert.deepEqual(on.steps!.map(s => s.cmd), ['time']);
  assert.deepEqual(on.loop!.steps[0].args, { a: 1, bcap: 0 }, 'target on, unshipped cap off');
  assert.deepEqual(on.loop!.steps[1].args, { c: true, b: false, z: true }, 'ship on, unshipped off, unknown on');
  assert.equal(on.loop!.steps[2].note, 'fire');
  assert.deepEqual(on.loop!.arms, ['on']); assert.deepEqual(on.loop!.until, { events: 6 });
  assert.equal(on.loop!.maxMinutes, Math.ceil((75 + 6 * 22) * 1.5 / 60));
  assert.equal(warns.length, 1, 'unknown switch warned once');
  const ab = derive(r, { kind: 'ab', bundle: new Set(['a', 'c']), perArm: 10 }, new Set(['a', 'c']), owners(map), map, () => {})!;
  assert.deepEqual(ab.loop!.steps[0].args, { a: '$on', bcap: 0 });
  assert.deepEqual(ab.loop!.steps[1].args, { c: '$on', b: false, z: true });
  assert.deepEqual(ab.loop!.arms, ['off', 'on', 'on', 'off']); assert.deepEqual(ab.loop!.until, { perArm: 10 });
  const ab2 = derive({ ...r, id: 'r2', steps: [...fire('off', 1), ...fire('on', 2)].map(s => s.cmd === 'patches' ? { ...s, args: { z: true } } : s) }, { kind: 'ab', bundle: new Set(['a', 'c']), perArm: 10 }, new Set(['a', 'c']), owners(map), map, () => {})!;
  assert.deepEqual(ab2.loop!.steps[0], { cmd: 'patches', args: { c: '$on' }, waitS: 1, note: 'bundle arm', setup: true }, 'missing bundle switch added');
  assert.ok(ab2.loop!.steps.every(s => typeof s.setup === 'boolean'), 'every loop step marked setup: true/false');
  // loop-format source
  const lf: Repro = { id: 'lf', steps: [{ cmd: 'set_speed', waitS: 2 }], loop: { steps: [{ cmd: 'patches', args: { c: '$on' }, waitS: 1, note: 'arm' }, { cmd: 'reflect_call', waitS: 44 }], arms: ['off', 'on', 'on', 'off'], until: { perArm: 10 } } };
  const lfOn = derive(lf, { kind: 'on', target: 'c', events: 4 }, new Set(['c']), owners(map), map, () => {})!;
  assert.deepEqual(lfOn.loop!.steps[0].args, { c: true });
  assert.equal(derive({ id: 'plain', steps: [{ cmd: 'x' }] }, { kind: 'on', target: 'a', events: 6 }, new Set(), owners(map), map, () => {}), null, 'plain repro: no loop');
  // whole build on fixtures
  const times: Times = { overhead: 1.7, byId: new Map([['old-rare', 7.6]]), matrixCases: 3.5, reload: 4.7 };
  const validate: Job[] = [{ id: 'bp-rel-matrix', asi: 'x', run: { matrix: [] } }, { id: 'bp-rel-travel-france', asi: 'x', run: { matrix: ['--travel'] } },
    { id: 'old-rare', asi: 'x', run: { profile: { minutes: 6 } } }, { id: 'other-rare', asi: 'x', run: { profile: { minutes: 6 } } }];
  const b = build({ map, repros: [r], validate, ship: new Set(['a', 'c']), prevShip: new Set(['c']), changed: new Map([['a', 'src/a.cpp']]), asi: 'A', minCounters: 1,
    iniOverride: {}, fixesOn: '', times, warn: () => {} });
  assert.deepEqual(b.jobs.map(j => j.id), ['bp-rc-smoke', 'ip-rc-smoke', 'sv-rc-smoke', 'bp-rel-matrix', 'bp-rel-travel-france', 'rc-on-a', 'rc-ab-r', 'old-rare', 'bp-rc-soak']);
  assert.equal(b.jobs.find(j => j.id === 'rc-on-a')!.run.profile.fixesRequire, `a_run>=${ON_EVENTS}`);
  assert.equal(b.jobs.find(j => j.id === 'old-rare')!.estMin, 7.6);
  assert.deepEqual(coverage(validate, b.jobs, b.want), ['| other-rare | not mapped |']);
  // build verification: --fixes-build goes on every job (queue.ts refuses a job whose installed ts3fixes.package is not that build)
  assert.ok(b.jobs.every(j => !('fixesBuild' in j)));
  const bf = build({ map, repros: [r], validate, ship: new Set(['a', 'c']), prevShip: new Set(['c']), changed: new Map([['a', 'src/a.cpp']]), asi: 'A', minCounters: 1,
    iniOverride: {}, fixesOn: '', times, warn: () => {}, fixesBuild: 'ts3fixes.vX' });
  assert.ok(bf.jobs.length === b.jobs.length && bf.jobs.every(j => (j as any).fixesBuild === 'ts3fixes.vX'), 'fixesBuild on every job, old jobs too');
  // docs/170 §5: replaces (old job stood in for, not run, listed), {n} in nativeRequire, part d only on new features' loops,
  // adaptive steps estimated at mid minS..maxS, session reuse saving per save
  const map2: FeatureMap = { ...map, groups: { ...map.groups, h: ['other-rare'] }, features: { ...map.features,
    a: { ...map.features.a, nativeRequire: 'aCount>={n}', replaces: ['@h'], replacesWhy: 'loop r fires a' },
    c: { ...map.features.c, repro: { id: 'r' } } } };
  const b2 = build({ map: map2, repros: [r], validate, ship: new Set(['a', 'c']), prevShip: new Set(['a', 'c']), changed: new Map([['a', 'x'], ['c', 'y']]), asi: 'A', minCounters: 1,
    iniOverride: {}, fixesOn: '', times, warn: () => {} });
  const c2 = b2.jobs.filter(j => j.part.startsWith('c '));
  assert.deepEqual(c2.map(j => j.id), ['rc-on-r'], 'a and c share repro r: one part c job named after the repro');
  assert.equal(c2[0].nativeRequire, `aCount>=${ON_EVENTS}`); assert.equal(c2[0].run.profile.fixesRequire, `a_run>=${ON_EVENTS}`);
  assert.ok(!b2.jobs.some(j => j.part.startsWith('d ')), 'no new feature: no bundle A/B even with changed loops');
  assert.deepEqual(coverage(validate, b2.jobs, b2.want, [], b2.replaced), ['| other-rare | replaced by forced loop r (a): loop r fires a |']);
  assert.equal(estSecs([{ cmd: 'x', waitS: 20, waitUntil: { quiet: 1 }, minS: 4, maxS: 20 }, { cmd: 'y', waitS: 3 }]), 15);
  assert.equal(reuseSaving([{ part: 'a smoke', save: 'S' }, { part: 'c fix on', save: 'S' }, { part: 'c fix on', save: 'S' }, { part: 'e rare', save: 'T' }, { part: 'f soak', save: 'S' }], 2), 4);
  const rj = '[\r\n  { "id": "x", "steps": [] }\r\n]\r\n';
  const i1 = installRepros(rj, [on]);
  assert.ok(i1.startsWith('[\r\n  { "id": "x", "steps": [] },\r\n  {\r\n    "id": "rc-on-a"') && i1.endsWith('\r\n]\r\n'), 'block appended, CRLF kept, rest untouched');
  const i2 = installRepros(i1, [ab]);
  assert.deepEqual(JSON.parse(i2).map((x: Repro) => x.id), ['x', 'rc-ab-r'], 'rerun replaces the block');
  assert.equal(installRepros(i2, []), rj, 'empty set removes it');
  // the real files: map ids and repro ids resolve, every rare id exists in release-validate.json, derivation runs on every mapped loop repro
  const real = load();
  for (const [id, f] of Object.entries(real.map.features)) {
    if (f.repro) assert.ok(real.repros.some(x => x.id === f.repro!.id), `${id}: repro ${f.repro.id} in repros.json`);
    for (const x of [...f.rare, ...f.replaces ?? []]) for (const y of x.startsWith('@') ? real.map.groups[x.slice(1)] ?? assert.fail(`${id}: group ${x}`) : [x]) assert.ok(real.validate.some(j => j.id === y), `${id}: ${y} in release-validate.json`);
    if (!f.on && !RF[id]) assert.fail(`${id}: no on-keys and not in release-features.ts`);
    if (f.match) new RegExp(f.match);
  }
  const all = new Set(Object.keys(real.map.features));
  for (const [id, f] of Object.entries(real.map.features)) if (f.repro) { const src = real.repros.find(x => x.id === f.repro!.id)!; if (firing(src)) { derive(src, { kind: 'ab', bundle: all, perArm: 10 }, all, owners(real.map), real.map, () => {}); derive(src, { kind: 'on', target: id, events: ON_EVENTS }, all, owners(real.map), real.map, () => {}); }
    else assert.ok(!f.replaces?.length, `${id}: replaces old jobs but its repro ${f.repro.id} is not a loop`); }
  console.log('release-check self-check ok');
}

function load() {
  return { map: JSON.parse(readFileSync(H('feature-repros.json'), 'utf8')) as FeatureMap, repros: JSON.parse(readFileSync(H('repros.json'), 'utf8')) as Repro[],
    validate: (JSON.parse(readFileSync(H('queues/release-validate.json'), 'utf8')) as { jobs: Job[] }).jobs };
}

// rc-* entries go at the end of repros.json as one block (hand-formatted file: text splice, never a full rewrite); a rerun
// replaces that block
export function installRepros(text: string, derived: Repro[]): string {
  const eol = text.includes('\r\n') ? '\r\n' : '\n';
  let t = text.replace(/\r\n/g, '\n');
  const at = t.indexOf('\n  {\n    "id": "rc-');
  if (at >= 0) t = t.slice(0, at).replace(/,\s*$/, '') + '\n]\n';
  else if (/"id": "rc-/.test(t)) throw new Error('repros.json has rc-* entries not in the block release-check writes: remove them first');
  if (derived.length) t = t.replace(/\s*\]\s*$/, `,\n${derived.map(r => JSON.stringify(r, null, 2).replace(/^/gm, '  ')).join(',\n')}\n]\n`);
  JSON.parse(t);
  return t.replace(/\n/g, eol);
}

const args = process.argv.slice(2);
const opt = (n: string, d: string) => { const i = args.indexOf(`--${n}`); return i >= 0 ? args[i + 1] : d; };
const VALUED = new Set(['--features', '--asi', '--min-counters', '--out', '--fixes-build']);
if (import.meta.filename === resolve(process.argv[1] ?? '')) {
  if (args.includes('--self-check')) selfCheck();
  else {
    const prev = args.find((a, i) => !a.startsWith('--') && !VALUED.has(args[i - 1]));
    if (!prev) { console.error('usage: node tools/release-check.ts <prev tag|commit> [--features id,id] [--asi path] [--min-counters n] [--fixes-build name] [--out file] [--write] [--install-repros] | --self-check'); process.exit(2); }
    const { map, repros, validate } = load();
    const rev = resolveRev(prev, map);
    const ini = readFileSync(join(ROOT, INI_PATH), 'utf8'), tuning = readFileSync(join(ROOT, TUNING_PATH), 'utf8');
    const show = (p: string) => { try { return git('show', `${rev}:${p}`); } catch { return ''; } };
    const prevIni = show(INI_PATH), prevTuning = show(TUNING_PATH);
    const ids = Object.keys(map.features);
    const repoShip = new Set(ids.filter(id => onIn(map.features[id], id, ini, tuning)));
    const listArg = opt('features', '');
    for (const id of listArg.split(',').filter(Boolean)) if (!map.features[id]) { console.error(`unknown feature ${id} (tools/harness/feature-repros.json)`); process.exit(2); }
    // --features adds to what was on in the previous release (the base set stays), so "slotenabledro,..." means "v0.1.4 + these"
    const prevShip = new Set(ids.filter(id => onIn(map.features[id], id, prevIni, prevTuning)));
    const ship = listArg ? new Set([...prevShip, ...listArg.split(',').filter(Boolean)]) : repoShip;
    const iniOverride: Record<string, unknown> = {}, fixesOn: string[] = [];
    // release-features.ts order, so a later feature's on value wins a shared key (routesnapplan's RouteSnap.Kinds=9)
    const rfOrder = (id: string) => Object.keys(RF).indexOf(id);
    if (listArg) for (const id of [...ship].sort((a, b) => rfOrder(a) - rfOrder(b))) {
      const f = map.features[id], on = f.on ?? { ini: Object.fromEntries(Object.entries(RF[id]?.ini ?? {}).map(([k, [v]]) => [k, v])), tuning: RF[id]?.tuning };
      for (const [k, v] of Object.entries(on.ini ?? {})) if (!repoShip.has(id)) iniOverride[k] = /^-?\d+$/.test(v) ? Number(v) : v;  // RouteSnap.Lots=replay stays a string
      if ((on.tuning ?? []).length && !repoShip.has(id)) fixesOn.push(...(f.fixes ?? []));
    }
    const files = git('diff', '--name-only', rev, 'HEAD').split('\n').filter(Boolean);
    const sharedLines = changedLines(git('diff', '-U0', rev, 'HEAD', '--', ...map.shared));
    const changed = changedFeatures(map, files, sharedLines);
    const warns: string[] = [];
    const times = measuredTimes(validate);
    // the release package (docs/data/builds.json): every job refuses unless the installed ts3fixes.package has its tuning
    const fixesBuild = opt('fixes-build', '') || undefined, fb = loadDb().find(x => x.name === fixesBuild);
    if (args.includes('--write') && !fixesBuild) { console.error('--write needs --fixes-build <ts3fixes name in docs/data/builds.json> (node tools/builds.ts add build/ts3fixes.package --name ts3fixes.vX.Y.Z)'); process.exit(2); }
    if (fixesBuild && fb?.kind !== 'ts3fixes') { console.error(`--fixes-build ${fixesBuild}: no ts3fixes entry in docs/data/builds.json (node tools/builds.ts list)`); process.exit(2); }
    if (fb?.kind === 'ts3fixes' && fb.tuningDigest !== sha256(Buffer.from(tuningXmlOf(tuning), 'utf8'))) warns.push(`${fixesBuild} tuning differs from this checkout's build-fixes.ts: cycle.ts installs build/ts3fixes.package, so every job will refuse until they match`);
    const b = build({ map, repros, validate, ship, prevShip, changed, asi: opt('asi', 'game-copy/ts3native.asi.rel020'), minCounters: Number(opt('min-counters', '98')), fixesBuild,
      iniOverride, fixesOn: fixesOn.join(','), times, warn: m => warns.push(m) });
    const out = opt('out', H('queues/release-check.json'));
    console.log(`release check: ${prev} = ${rev.slice(0, 8)}, ${files.length} files changed; ship set ${[...ship].join(', ')}`);
    console.log(`new since ${prev}: ${[...b.newShip].join(', ') || 'none'}; shipped and changed: ${[...ship].filter(id => changed.has(id) && !b.newShip.has(id)).map(id => `${id} (${changed.get(id)})`).join('; ') || 'none'}`);
    if (listArg && [...ship].some(id => !repoShip.has(id))) console.log(`NOTE: --features differs from the repo defaults: jobs carry ini/fixesOn overrides; the final run comes after release-features.ts --write (no overrides then)`);
    for (const w of new Set(warns)) console.log(`WARNING: ${w}`);
    console.log('\n| part | jobs | est. min | source |\n|---|---|---|---|');
    let total = 0;
    for (const part of [...new Set(b.jobs.map(j => j.part))]) {
      const js = b.jobs.filter(j => j.part === part), m = js.reduce((t, j) => t + j.estMin, 0); total += m;
      console.log(`| ${part} | ${js.length} | ${m.toFixed(0)} | ${[...new Set(js.map(j => j.est))].join('; ')} |`);
    }
    console.log(`| total | ${b.jobs.length} | ${total.toFixed(0)} (${(total / 60).toFixed(1)} h) | profile overhead ${times.overhead.toFixed(1)} min/job measured |`);
    const dMin = b.jobs.filter(j => j.part.startsWith('d ')).reduce((t, j) => t + j.estMin, 0), save = reuseSaving(b.jobs, times.overhead);
    console.log(`without part d (clean-night timing read): ${((total - dMin) / 60).toFixed(1)} h; with session reuse / multi-repro (one load per save for parts c-f, optional): -${save.toFixed(0)} min -> ${((total - save) / 60).toFixed(1)} h (${((total - dMin - save) / 60).toFixed(1)} h without d)`);
    console.log(`\nneeds repro (part c skipped, covered by smoke/soak/bundle only):\n${b.needs.map(n => `- ${n}`).join('\n') || '- none'}`);
    const kept = [...b.want].filter(([r]) => b.jobs.some(j => j.id === r && j.part === 'e rare'));
    console.log(`\nold rare jobs kept (no forced path reaches the code):\n${kept.map(([r, by]) => `- ${r} (${by.join(', ')}): ${by.map(id => map.features[id].rareWhy).filter(Boolean)[0] ?? 'no reason in feature-repros.json rareWhy'}`).join('\n') || '- none'}`);
    console.log(`\nnot run vs release-validate.json (${validate.length} jobs):\n| job | why acceptable / what replaces it |\n|---|---|\n${coverage(validate, b.jobs, b.want, map.groups['route-all-worlds'], b.replaced).join('\n')}`);
    const queue = { note: `Generated by tools/release-check.ts ${prev} (${rev.slice(0, 8)})${listArg ? ` --features ${listArg}` : ''} on ${new Date().toISOString().slice(0, 10)}; do not edit, regenerate (docs/170 §4, docs/RELEASE.md). Parts: a smoke, b save matrix, c fix-on forced repros, d bundle A/B, e rare jobs of changed features, f soak. Estimated ${(total / 60).toFixed(1)} h. Needs the rc-* repros in repros.json (--install-repros). Clean night only for part d timing reads; a-c, e, f are correctness and run in the day.`,
      jobs: b.jobs.map(({ part, estMin, est, ...j }) => j) };
    if (args.includes('--write')) {
      writeFileSync(out, JSON.stringify(queue, null, 2) + '\n');
      writeFileSync(H('release-check-repros.json'), JSON.stringify(b.derived, null, 2) + '\n');
      console.log(`\nwritten: ${out}, tools/harness/release-check-repros.json (${b.derived.length} derived repros)`);
    } else console.log('\n(dry run: --write writes the queue and the derived repros)');
    if (args.includes('--install-repros')) {
      writeFileSync(H('repros.json'), installRepros(readFileSync(H('repros.json'), 'utf8'), b.derived));
      console.log(`installed ${b.derived.length} rc-* repros into tools/harness/repros.json`);
    }
  }
}
