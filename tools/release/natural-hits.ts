// Natural-hit census (docs/279 "Natural evidence"): per switch, how often a finished game test job ran with it on and how often its counters
// rose while it was on, split into natural play (no repro steering the job) and repro jobs, plus the crashes / script errors /
// own error counters of those runs. A switch with >= --min natural hits in >= 1 run, 0 own error counts, and no crash or script
// error in its hit runs counts as "verified naturally" (small-fix rule evidence next to docs/data/verified-switches.json).
//   node tools/release/natural-hits.ts [--since YYYY-MM-DD] [--min 3] [--queue-dir probe-out/queue] [--dry-run]
//   node tools/release/natural-hits.ts --self-check
// Reads the queue rows probe-out/queue/<queue>-<date>.jsonl (queue.ts) and each row's /native before/after file (row.nativeFile):
//  - native switches: one per ts3native.ini [Section]; its counters = the camelCase fields named in the registry's native
//    reservations whose text starts with that [Section] (docs/data/registry-reservations.json); on = row.featuresOn (sections
//    with Enabled >= 1 in the ini the game started with) or the job ini / repo ini Mode >= 1 for sections without Enabled
//  - ts3fixes switches: the agent's names (mods/ts3fixes/src/Fixes.cs `case "name": return Instantiator.kX;`); counters = the
//    `patches` keys that start with "<name>_"; on = row.fixCensus.on (queue.ts from 11-18) else profile fixesOn/fixesOff, else
//    the shipped tuning default (tools/setup/build-fixes.ts TUNING_XML), counted as "inferred"
// Writes docs/data/natural-hits.json and docs/data/natural-hits.md (unless --dry-run).
// Older rows only carry the ts3fixes counters their job reported (fixesReport prefixes + the ACTIVITY set), so managed switches
// are under-counted before queue.ts wrote row.fixCensus; native counters are complete wherever the /native file survived.
import { existsSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { parseArgs } from 'node:util';
import assert from 'node:assert/strict';

const ROOT = resolve(import.meta.dirname, '..', '..');
const ROW_FILE = /-(\d{4}-\d\d-\d\d)\.jsonl$/;
const FIXES_CASE = /case "([a-z0-9_]+)":\s*return Instantiator\.(k\w+)\s*;/g;
const TUNING = /<(k\w+) value="(True|False)"\s*\/>/g;
const REG_SECTION = /^\s*\[([A-Z]\w*)\]/;
const CAMEL = /\b[a-z][a-z0-9]*[A-Z]\w*\b/g;
// fields that are settings, sizes, timings or maxima, not "the fix acted" counts
const NATIVE_NOT_HIT = /(Mode|Ready|On|Max\w*|Us|Ms|MB|KB|Enabled|Last\w*|Version|Cap|Slot\w*)$/;
const NATIVE_ERR = /(Err|Fail|Fault|Mismatch)/i;
const MANAGED_NOT_HIT = /_(max\w*|us|ms|min|sec|simsec)$/;
const MANAGED_ERR = /_(errors?|err\w*|fail\w*|exceptions?|threw)$/;

type Def = { hit?: string[]; err?: string[]; offHit?: string[] };
// switches whose meaning the generic name rules get wrong (release plan §1b conditionals first)
const OVERRIDE: Record<string, Def> = {
  '[PkgLock]': { hit: ['pkCloseDefer', 'pkCloseForce'], err: ['pkAbbaSeen'], offHit: ['pkAbbaSeen'] },
  mooddrift: { hit: ['mooddrift_corrected'], err: ['mooddrift_errors'], offHit: ['mooddrift_corrected'] },
  moodpending: { hit: ['moodpending_skipped'], err: ['moodpending_errors'], offHit: ['moodpending_seen'] },
  weddingcleanup: { hit: ['weddingcleanup_guarded'], err: ['weddingcleanup_errors'] },
  bouncerskip: { hit: ['bouncerskip_skipped'] },
  takeariskguard: { hit: ['takeariskguard_skips'] },
  menumaven: { hit: ['menumaven_done'] },
};
export const CONDITIONALS = ['[PkgLock]', 'bouncerskip', 'takeariskguard', 'menumaven', 'mooddrift', 'moodpending', 'weddingcleanup'];

export const managedNames = (fixesCs: string) => new Map([...fixesCs.matchAll(FIXES_CASE)].map(m => [m[1], m[2]])); // name -> tunable
export const tuningDefaults = (src: string) => new Map([...src.matchAll(TUNING)].map(m => [m[1], m[2] === 'True']));
export function parseIni(text: string): Record<string, Record<string, string>> {
  const out: Record<string, Record<string, string>> = {}; let s = '';
  for (const l of text.split(/\r?\n/)) {
    const h = /^\s*\[([^\]]+)\]/.exec(l); if (h) { s = h[1]; out[s] ??= {}; continue; }
    const kv = /^\s*([A-Za-z]\w*)\s*=\s*([^;\r\n]*)/.exec(l); if (kv && s) out[s][kv[1]] = kv[2].trim();
  }
  return out;
}
// native reservation text "[PkgLock] pkMode, pkCloseOk, ..." -> section + its camelCase field names
export function regFields(what: string): { section: string; fields: string[] } | null {
  const m = REG_SECTION.exec(what);
  return m ? { section: m[1], fields: [...new Set(what.slice(m[0].length).match(CAMEL) ?? [])] } : null;
}
// longest agent name owning a `patches` key: "<name>_..." or, without underscores, "<name>..." (unseen_park_* -> unseenpark)
// shortcut: the underscore-free prefix can claim another switch's counter that happens to share a prefix; OVERRIDE fixes those
const owners = new WeakMap<string[], Map<string, string | null>>();
export function ownerOf(key: string, names: string[]): string | null {
  const m = owners.get(names) ?? owners.set(names, new Map()).get(names)!;
  if (!m.has(key)) m.set(key, ownerOf1(key, names));
  return m.get(key)!;
}
function ownerOf1(key: string, names: string[]): string | null {
  const flat = key.replace(/_/g, '');
  return names.filter(n => key.startsWith(`${n}_`) || flat.startsWith(n)).sort((a, b) => b.length - a.length)[0] ?? null;
}
export const isRepro = (run: any) => !!(run?.profile?.repro || run?.profile?.repros?.length || run?.script || run?.repro);
const num = (v: unknown) => (v === undefined || v === null || v === '' ? NaN : Number(v));
const list = (s: unknown) => String(s ?? '').split(',').map(x => x.trim()).filter(Boolean);

type Agg = { kind: 'native' | 'ts3fixes'; runsOn: number; runsMeasured: number; runsOnNatural: number; onInferred: number; hitsNatural: number; hitRunsNatural: number;
  hitsRepro: number; hitRunsRepro: number; offHits: number; offHitRuns: number; errCount: number; crashHitRuns: number; scriptErrorHitRuns: number;
  examples: string[]; bases: Record<string, number>; naturalVerified?: boolean };
const agg = (kind: Agg['kind']): Agg => ({ kind, runsOn: 0, runsMeasured: 0, runsOnNatural: 0, onInferred: 0, hitsNatural: 0, hitRunsNatural: 0, hitsRepro: 0, hitRunsRepro: 0,
  offHits: 0, offHitRuns: 0, errCount: 0, crashHitRuns: 0, scriptErrorHitRuns: 0, examples: [], bases: {} });
// the base a row ran on (queue.ts row.base from 2026-11-18: repo / dogfood <id> / iniBase <path> / release / none)
export const baseOf = (row: any) => String(row.base ?? 'unrecorded (before 11-18)');

export type Catalog = { native: Map<string, string[]>; repoIni: Record<string, Record<string, string>>; names: Map<string, string>; defaults: Map<string, boolean> };
const nameList = new WeakMap<Catalog, string[]>();
// one row's per-switch outcome: on (true/false/null unknown), inferred, hit/err/offHit sums
export function rowSwitches(row: any, deltas: ReturnType<typeof rowDeltas>, cat: Catalog) {
  const out: { sw: string; kind: Agg['kind']; on: boolean | null; inferred: boolean; measured: boolean; hit: number; err: number; offHit: number }[] = [];
  const ini = row.ini ?? {}, feats: string[] | undefined = Array.isArray(row.featuresOn) ? row.featuresOn : undefined;
  for (const [sec, fields] of cat.native) {
    const sw = `[${sec}]`, d = OVERRIDE[sw] ?? {}, repo = cat.repoIni[sec] ?? {};
    const val = (k: string) => num(ini[`${sec}.${k}`] ?? repo[k]);
    const on = 'Enabled' in repo ? (feats ? feats.includes(sec) : val('Enabled') >= 1) : 'Mode' in repo ? val('Mode') >= 1 : null;
    const sum = (ks: string[]) => ks.reduce((s, k) => s + (deltas.native[k] ?? 0), 0);
    const hit = sum(d.hit ?? fields.filter(f => !NATIVE_NOT_HIT.test(f) && !NATIVE_ERR.test(f)));
    out.push({ sw, kind: 'native', on, measured: deltas.nativeSeen, inferred: !('Enabled' in repo && feats) && !(`${sec}.Mode` in ini), hit, err: sum(d.err ?? fields.filter(f => NATIVE_ERR.test(f))), offHit: sum(d.offHit ?? []) });
  }
  const names = nameList.get(cat) ?? nameList.set(cat, [...cat.names.keys()]).get(cat)!, byOwner = new Map<string, Record<string, number>>();
  for (const [k, v] of Object.entries(deltas.managed)) { const o = ownerOf(k, names); if (o) (byOwner.get(o) ?? byOwner.set(o, {}).get(o)!)[k] = v; }
  const p = row.run?.profile ?? {}, fon = new Set([...list(p.fixesOn), ...list(p.ab), ...(p.repros ?? []).flatMap((r: any) => list(r?.fixesOn))]), foff = new Set(list(p.fixesOff));
  const seen = deltas.managedSeen && new Set(deltas.managedSeen.map(k => ownerOf(k, names)));
  for (const [n, tun] of cat.names) {
    const ks = byOwner.get(n) ?? {}, d = OVERRIDE[n] ?? {}, census: string[] | undefined = row.fixCensus?.on;
    const exact = !!census || fon.has(n) || foff.has(n);
    const on = census ? census.includes(n) : fon.has(n) ? true : foff.has(n) ? false : cat.defaults.get(tun) ?? null;
    if (on === null && !Object.keys(ks).length) continue;
    const sum = (f: (k: string) => boolean) => Object.entries(ks).filter(([k]) => f(k)).reduce((s, [, v]) => s + v, 0);
    const hit = d.hit ? sum(k => d.hit!.includes(k)) : sum(k => !MANAGED_NOT_HIT.test(k) && !MANAGED_ERR.test(k));
    out.push({ sw: n, kind: 'ts3fixes', on, measured: !seen || seen.has(n), inferred: !exact, hit, err: d.err ? sum(k => d.err!.includes(k)) : sum(k => MANAGED_ERR.test(k)), offHit: d.offHit ? sum(k => d.offHit!.includes(k)) : hit });
  }
  return out;
}
// /native before/after -> rises; fixCensus.rose, else fixActivity counters (after - before, missing before = 0); *Seen = what the
// row could show at all (managedSeen null = every ts3fixes counter: fixCensus; else the fixActivity keys recorded)
export function rowDeltas(row: any, nativeJson: any) {
  const native: Record<string, number> = {}, managed: Record<string, number> = {};
  const a = nativeJson?.after ?? row.counters?.after, b = nativeJson?.before ?? row.counters?.before;
  if (a && !a.error) for (const [k, v] of Object.entries(a)) if (typeof v === 'number') { const d = v - (typeof b?.[k] === 'number' ? b[k] : 0); if (d > 0) native[k] = d; }
  if (row.fixCensus?.rose) Object.assign(managed, row.fixCensus.rose);
  else for (const [k, v] of Object.entries<any>(row.fixActivity?.counters ?? {})) { const d = Number(v.after) - Number(v.before ?? 0); if (d > 0) managed[k] = d; }
  return { native, managed, nativeSeen: !!(a && !a.error), managedSeen: row.fixCensus?.rose ? null : Object.keys(row.fixActivity?.counters ?? {}) };
}
export function census(rows: { row: any; nativeJson: any }[], cat: Catalog, min: number) {
  const res: Record<string, Agg> = {};
  for (const { row, nativeJson } of rows) {
    if (row.ourMods === false || row.na === true) continue;
    const rep = isRepro(row.run), crashed = !!row.crashed, se = Array.isArray(row.scriptErrors) && row.scriptErrors.length > 0, tag = `${row.queue}/${row.job}`;
    for (const s of rowSwitches(row, rowDeltas(row, nativeJson), cat)) {
      const a = res[s.sw] ??= agg(s.kind);
      if (s.on === false) { if (s.offHit > 0) { a.offHits += s.offHit; a.offHitRuns++; } continue; }
      if (s.on === null && !s.hit) continue;
      a.runsOn++; a.bases[baseOf(row)] = (a.bases[baseOf(row)] ?? 0) + 1; if (s.measured) a.runsMeasured++; if (!rep) a.runsOnNatural++; if (s.inferred) a.onInferred++;
      a.errCount += s.err;
      if (s.hit > 0) {
        if (rep) { a.hitsRepro += s.hit; a.hitRunsRepro++; } else { a.hitsNatural += s.hit; a.hitRunsNatural++; }
        if (crashed) a.crashHitRuns++; if (se) a.scriptErrorHitRuns++;
        if (a.examples.length < 3 && !a.examples.includes(tag)) a.examples.push(tag);
      }
    }
  }
  for (const a of Object.values(res)) a.naturalVerified = a.hitsNatural >= min && a.hitRunsNatural >= 1 && a.errCount === 0 && a.crashHitRuns === 0 && a.scriptErrorHitRuns === 0;
  for (const [k, a] of Object.entries(res)) if (!a.runsMeasured && !a.offHitRuns && !CONDITIONALS.includes(k)) delete res[k]; // unmeasured = no evidence either way
  return res;
}
// verified-switches.json entries -> their member switches' natural status
export function crossRef(entries: any[], res: Record<string, Agg>, names: Map<string, string>) {
  const byTun = new Map([...names].map(([n, t]) => [t, n]));
  return Object.fromEntries(entries.map(e => {
    // a sub-key switch ([GoalValidRO] UnseenDefer) is not its section's Enabled: no census row of its own, so no evidence
    const members = [...Object.entries<any>(e.on?.ini ?? {}).flatMap(([s, kv]) => Object.keys(kv).map(k => (k === 'Enabled' || k === 'Mode' ? `[${s}]` : `[${s}] ${k}`))), ...Object.keys(e.on?.tunables ?? {}).map((t: string) => byTun.get(t) ?? t)];
    const st = members.map(m => res[m]);
    const natural = st.length && st.every(a => a?.naturalVerified) ? 'verified' : st.some(a => a && (a.hitsNatural || a.hitsRepro)) ? (st.some(a => a && (a.errCount || a.crashHitRuns || a.scriptErrorHitRuns)) ? 'hits-with-errors' : 'some-hits') : 'none';
    return [e.id, { status: e.status, natural, members: Object.fromEntries(members.map((m, i) => [m, st[i] ? { hitsNatural: st[i].hitsNatural, hitsRepro: st[i].hitsRepro, naturalVerified: st[i].naturalVerified } : null])) }];
  }));
}
export function loadCatalog(root = ROOT): Catalog {
  const repoIni = parseIni(readFileSync(join(root, 'native', 'ts3native', 'ts3native.ini'), 'utf8'));
  const native = new Map<string, string[]>();
  for (const r of JSON.parse(readFileSync(join(root, 'docs', 'data', 'registry-reservations.json'), 'utf8')).reservations ?? []) {
    if (r.kind !== 'native') continue;
    const f = regFields(String(r.what ?? '')); if (!f || !repoIni[f.section]) continue;
    native.set(f.section, [...new Set([...(native.get(f.section) ?? []), ...f.fields])]);
  }
  const names = managedNames(readFileSync(join(root, 'mods', 'ts3fixes', 'src', 'Fixes.cs'), 'utf8'));
  return { native, repoIni, names, defaults: tuningDefaults(readFileSync(join(root, 'tools', 'setup', 'build-fixes.ts'), 'utf8')) };
}
export function loadRows(dir: string, since: string) {
  const out: { row: any; nativeJson: any }[] = [];
  for (const f of readdirSync(dir).filter(f => (ROW_FILE.exec(f)?.[1] ?? '') >= since).sort())
    for (const l of readFileSync(join(dir, f), 'utf8').split('\n')) {
      if (!l.trim()) continue;
      let row: any; try { row = JSON.parse(l); } catch { continue; }
      let nativeJson = null;
      const nf = row.nativeFile && (existsSync(row.nativeFile) ? row.nativeFile : join(dir, String(row.nativeFile).split(/[\\/]/).pop()!));
      if (nf && existsSync(nf)) try { nativeJson = JSON.parse(readFileSync(nf, 'utf8')); } catch { /* torn file: row counters */ }
      out.push({ row, nativeJson });
    }
  return out;
}
export function markdown(res: Record<string, Agg>, meta: { since: string; rows: number; min: number }, cat: Catalog) {
  const line = (k: string, a: Agg) => `| \`${k}\` | ${a.kind} | ${a.runsOn} (${a.runsOnNatural} natural, ${a.runsMeasured} measured${a.onInferred ? `, ${a.onInferred} inferred` : ''}) | ${a.hitsNatural} / ${a.hitRunsNatural} | ${a.hitsRepro} / ${a.hitRunsRepro} | ${a.offHits} / ${a.offHitRuns} | ${a.errCount} | ${a.crashHitRuns} / ${a.scriptErrorHitRuns} | ${a.naturalVerified ? 'yes' : 'no'} |`;
  const head = '| switch | kind | runs on | natural hits / runs | repro hits / runs | off-arm hits / runs | own errors | crash / script-error hit runs | verified naturally |\n|---|---|---|---|---|---|---|---|---|';
  const defOn = (k: string, a: Agg) => (a.kind === 'native' ? cat.repoIni[k.slice(1, -1)]?.Enabled === '1' || cat.repoIni[k.slice(1, -1)]?.Mode === '1' : cat.defaults.get(cat.names.get(k) ?? '') === true);
  const hitList = Object.entries(res).filter(([, a]) => a.hitsNatural > 0).sort((x, y) => y[1].hitRunsNatural - x[1].hitRunsNatural);
  return [`# Natural-hit census`, '', `Generated by \`node tools/release/natural-hits.ts\` (docs/279 "Natural evidence") over ${meta.rows} queue rows since ${meta.since}; verified naturally = >= ${meta.min} natural hits, 0 own error counts, no crash or script error in its hit runs. "measured" = the row recorded that switch's counters at all (rows before queue.ts row.fixCensus only kept reported ts3fixes counters); "inferred" = on-state from the default, not the run's own reply.`, '',
    '## Release plan §1b conditionals', '', head, ...CONDITIONALS.map(k => res[k] ? line(k, res[k]) : `| \`${k}\` | - | 0 | 0 / 0 | 0 / 0 | 0 / 0 | 0 | 0 / 0 | no |`), '',
    `## Default-ON switches with natural hits (${hitList.filter(([k, a]) => defOn(k, a)).length}; all switches with natural hits: ${hitList.length})`, '', head, ...hitList.filter(([k, a]) => defOn(k, a)).map(([k, a]) => line(k, a)), ''].join('\n');
}

function selfCheck() {
  // real source lines
  assert.deepEqual([...managedNames('                case "bouncerskip": return Instantiator.kBouncerSlotSkip;\n                case "takeariskguard": return Instantiator.kTakeARiskGuard;  // EA bug fix')], [['bouncerskip', 'kBouncerSlotSkip'], ['takeariskguard', 'kTakeARiskGuard']]);
  assert.equal(managedNames('            if (ShortcutOn("moodpending")) { buff.mEffectValue = moodValue; c[2279]++; return; }').size, 0);
  assert.deepEqual([...tuningDefaults('    <kBouncerSlotSkip value="False" />\n    <kMoodDrift value="True" />\n    <kFoo value="3" />')], [['kBouncerSlotSkip', false], ['kMoodDrift', true]]);
  assert.deepEqual(regFields('[PkgLock] pkMode, pkCloseOk, pkCloseDefer, pkCloseForce, pkBeginUnowned, pkBeginLocked, pkAbbaSeen, pkDeferMax (pkglock.cpp, docs/539 13.9)'),
    { section: 'PkgLock', fields: ['pkMode', 'pkCloseOk', 'pkCloseDefer', 'pkCloseForce', 'pkBeginUnowned', 'pkBeginLocked', 'pkAbbaSeen', 'pkDeferMax'] });
  assert.equal(regFields('P6 all own kinds: TakeKinds bits 2/4 (Replan, PlanFromPoint) via [RouteSnap] TakeKindsAll (p6All, pk*)'), null, 'section must lead');
  assert.equal(ROW_FILE.exec('hoursmemo-timing-1116-master-1117b-2026-11-18.jsonl')?.[1], '2026-11-18');
  assert.equal(ROW_FILE.exec('alloc-sites-2026-10-26-bp-bb-allocsites-0109-machine.jsonl'), null);
  assert.deepEqual(parseIni('[PkgLock]\n; c\nMode=0\nDetect=0 ; x\n[Watch]\nLot=0'), { PkgLock: { Mode: '0', Detect: '0' }, Watch: { Lot: '0' } });
  assert.equal(ownerOf('bouncerskip_skipped', ['bouncer', 'bouncerskip']), 'bouncerskip');
  assert.equal(ownerOf('unseen_park_arrivals', ['unseenpark']), 'unseenpark');
  assert.equal(ownerOf('etspread_passes', ['bouncerskip']), null);
  assert.ok(isRepro({ profile: { repro: 'pkg-abba' } }) && isRepro({ script: ['x.ts'] }) && !isRepro({ profile: { minutes: 10, ab: 'x' } }));
  // census over fake rows
  const cat: Catalog = { native: new Map([['PkgLock', ['pkMode', 'pkCloseOk', 'pkCloseDefer', 'pkAbbaSeen']], ['GC', ['gcRuns', 'gcFails']]]),
    repoIni: { PkgLock: { Mode: '0', Detect: '0' }, GC: { Enabled: '1' } }, names: new Map([['bouncerskip', 'kBouncerSlotSkip'], ['mooddrift', 'kMoodDrift']]),
    defaults: new Map([['kBouncerSlotSkip', false], ['kMoodDrift', true]]) };
  const nat = (pk: number, extra: any = {}) => ({ row: { queue: 'q', job: `j${pk}`, ini: { 'PkgLock.Mode': 1 }, featuresOn: ['GC'], run: { profile: { minutes: 10 } }, scriptErrors: [], crashed: false,
    fixCensus: { on: ['mooddrift'], rose: { mooddrift_corrected: 2 } }, ...extra }, nativeJson: { before: { pkCloseDefer: 1, gcRuns: 0 }, after: { pkCloseDefer: 1 + pk, gcRuns: 5, pkMode: 1 } } });
  const r = census([nat(2), nat(1), nat(0, { run: { profile: { repro: 'x' } } }), nat(4, { ini: { 'PkgLock.Mode': 0 } }),
    { row: { queue: 'q', job: 'old', run: { profile: { fixesOn: 'bouncerskip' } }, fixActivity: { counters: { bouncerskip_skipped: { before: null, after: 2 } } } }, nativeJson: null },
    { row: { queue: 'v', job: 'vanilla', ourMods: false }, nativeJson: null }], cat, 3);
  assert.deepEqual([r['[PkgLock]'].runsOn, r['[PkgLock]'].hitsNatural, r['[PkgLock]'].hitRunsNatural, r['[PkgLock]'].naturalVerified, r['[PkgLock]'].offHitRuns], [3, 3, 2, true, 0]);
  assert.deepEqual(r['[PkgLock]'].bases, { 'unrecorded (before 11-18)': 3 });
  assert.deepEqual(census([nat(1, { base: 'dogfood x 1234' })], cat, 3)['[PkgLock]'].bases, { 'dogfood x 1234': 1 });
  assert.equal(r['[GC]'].hitsNatural, 15, 'gcRuns counts, gcFails is an error field');
  assert.deepEqual([r.mooddrift.runsOn, r.mooddrift.hitsNatural, r.mooddrift.onInferred], [5, 6, 1]);
  assert.deepEqual([r.bouncerskip.runsOn, r.bouncerskip.hitsNatural, r.bouncerskip.naturalVerified], [1, 2, false], 'below --min');
  const crashed = census([nat(5, { crashed: true })], cat, 3);
  assert.equal(crashed['[PkgLock]'].naturalVerified, false, 'a crash in a hit run blocks');
  const x = crossRef([{ id: 'pkglock', status: 'verified', on: { ini: { PkgLock: { Mode: '1' } } } }, { id: 'gcsub', status: 'unverified', on: { ini: { GC: { IdleGate: '1' } } } }, { id: 'bouncerskip', status: 'verified', on: { tunables: { kBouncerSlotSkip: 'True' } } }], r, cat.names);
  assert.deepEqual([x.pkglock.natural, x.bouncerskip.natural, x.gcsub.natural], ['verified', 'some-hits', 'none']);
  assert.deepEqual(Object.keys(x.gcsub.members), ['[GC] IdleGate']);
  assert.match(markdown(r, { since: '2026-11-11', rows: 6, min: 3 }, cat), /\| `\[PkgLock\]` \| native \| 3 \(2 natural, 3 measured\) \| 3 \/ 2 \|/);
  console.log('natural-hits self-check ok');
}

if (import.meta.main) {
  const { values } = parseArgs({ options: { since: { type: 'string' }, min: { type: 'string', default: '3' }, 'queue-dir': { type: 'string' }, 'dry-run': { type: 'boolean' }, 'self-check': { type: 'boolean' } } });
  if (values['self-check']) { selfCheck(); process.exit(0); }
  const since = values.since ?? new Date(Date.now() - 7 * 864e5).toISOString().slice(0, 10), min = Number(values.min);
  const dir = resolve(ROOT, values['queue-dir'] ?? join('probe-out', 'queue'));
  if (!existsSync(dir)) { console.error(`no queue dir ${dir}`); process.exit(1); }
  const cat = loadCatalog(), rows = loadRows(dir, since), res = census(rows, cat, min);
  const vs = JSON.parse(readFileSync(join(ROOT, 'docs', 'data', 'verified-switches.json'), 'utf8')).switches ?? [];
  const out = { schema: 1, generated: new Date().toISOString(), since, rows: rows.length, rowsByBase: rows.reduce((m: Record<string, number>, r) => ({ ...m, [baseOf(r.row)]: (m[baseOf(r.row)] ?? 0) + 1 }), {}), min, about: 'tools/release/natural-hits.ts (docs/279 "Natural evidence"): per-switch natural-play evidence from queue rows; verifiedSwitches = docs/data/verified-switches.json entries with their members\' natural status', verifiedSwitches: crossRef(vs, res, cat.names), switches: res };
  const md = markdown(res, { since, rows: rows.length, min }, cat);
  if (values['dry-run']) console.log(md);
  else { writeFileSync(join(ROOT, 'docs', 'data', 'natural-hits.json'), `${JSON.stringify(out, null, 1)}\n`); writeFileSync(join(ROOT, 'docs', 'data', 'natural-hits.md'), md); console.log(`natural-hits: ${rows.length} rows since ${since}, ${Object.keys(res).length} switches -> docs/data/natural-hits.json, natural-hits.md`); }
}
