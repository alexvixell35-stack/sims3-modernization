// Dogfood base for correctness jobs (docs/279 "Dogfood", Rodney 2026-11-18 "Yes, from tonight"): every correctness job (queue.ts
// !isTimingJob) runs the v0.3.0 release-candidate ON set instead of master defaults, the job's own ini on top. This script writes
// tools/harness/dogfood.json = the ini keys where that set differs from the repo ini; queue.ts merges them under each job's ini.
//   node tools/release/dogfood.ts [--rc-ini game-copy/ts3native.ini.rc-1119] [--dry-run]
//   node tools/release/dogfood.ts --self-check
// Without --rc-ini (until the rc files exist): the release plan §1 ON values (RC_ON below, N1-N11) plus the off values of every
// `blocked` entry in docs/data/verified-switches.json. With --rc-ini: every key of the rc ini that differs from the repo ini.
// Both: minus EXCLUDED (save-data fixes, blocked switches' on values, the plan's §2 decide-later items) and minus keys whose repo
// value already matches. Tunables cannot be overlaid per job (they live in the package): the ones the plan wants ON that the
// shipped tuning (tools/setup/build-fixes.ts) does not have are listed in tunablesNotApplied, never applied.
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { parseArgs } from 'node:util';
import assert from 'node:assert/strict';
import { parseIni, tuningDefaults } from './natural-hits.ts';

const ROOT = resolve(import.meta.dirname, '..', '..');
type Ini = Record<string, Record<string, string>>;
// probe-out/release-030-plan.md §1 (N1-N11; T1-T5 and T7a are tunables)
export const RC_ON: Ini = {
  MonoJit: { Enabled: '1', Direct: '1', DirectLeaf: '0', Wide: '1', Inline: '2', BoundCall: '1', BoundCallMask: '3', ClauseLazy: '1', FaultRecover: '1', HotReserve: '1' },
  MonoFast: { CallPath: '1' }, MintFuse: { Sub: '1' }, Binder: { TypeHandleDirect: '1' }, MonoThread: { QuitStop: '1', QuitWaitMs: '2000' },
  ThumbStall: { Enabled: '1', TimeoutMs: '10000', Action: '1' }, EaCrashFixes: { HeightsInfLoop: '1', BridgeHeightNaN: '1' }, RouteAsync: { JobYield: '1' },
  AutShadow: { Races: '7' },
};
export const RC_TUNABLES = ['kJobYield', 'kMiniHomeLot', 'kWatchdogAsync', 'kBookHand', 'kErrorTrapSims', 'kErrorTrapLookup', 'kErrorTrapCtl', 'kCelebrityDeadSim',
  'kGhostReactDeadSim', 'kDreamNodeDeadSim', 'kOverwatchDeadSim', 'kOutfitWaitSaved'];
// §2 decide-later (D1-D8) and HouseholdToo: master default stays; the save-data tunables stay off (save matrix first)
export const EXCLUDED = new Set(['EqualsFast.Enabled', 'EqualsFast.SmallEnums', 'GC.IdleGate', 'MonoJit.YRows', 'Startup.DxDiagCache', 'Experimental.HouseholdToo']);

export function overlay(repo: Ini, want: Ini): Record<string, string> {
  const out: Record<string, string> = {};
  for (const [s, kv] of Object.entries(want)) for (const [k, v] of Object.entries(kv))
    if (!EXCLUDED.has(`${s}.${k}`) && repo[s] && k in repo[s] && repo[s][k] !== v) out[`${s}.${k}`] = v; // unknown keys: an older ini, skip
  return out;
}
export function blockedOff(entries: any[]): Ini {
  const out: Ini = {};
  for (const e of entries.filter(e => e.status === 'blocked')) for (const [s, kv] of Object.entries<any>(e.off?.ini ?? {})) Object.assign(out[s] ??= {}, kv);
  return out;
}
const merge = (a: Ini, b: Ini): Ini => { const o: Ini = structuredClone(a); for (const [s, kv] of Object.entries(b)) Object.assign(o[s] ??= {}, kv); return o; };

function selfCheck() {
  const repo: Ini = { MonoJit: { Enabled: '1', YRows: '0' }, RouteSnap: { DoorMemoX: '3' }, Experimental: { HouseholdToo: '0' }, GC: { IdleGate: '0' } };
  const want = merge({ MonoJit: { Enabled: '1', YRows: '1' }, GC: { IdleGate: '1' }, Foo: { Bar: '1' } }, blockedOff([{ id: 'doormemox', status: 'blocked', off: { ini: { RouteSnap: { DoorMemoX: '-1' } } } },
    { id: 'x', status: 'verified', off: { ini: { RouteSnap: { DoorMemoX: '9' } } } }, { id: 'hh', status: 'blocked', off: { ini: { Experimental: { HouseholdToo: '1' } } } }]));
  assert.deepEqual(overlay(repo, want), { 'RouteSnap.DoorMemoX': '-1' }, 'same value, excluded and unknown keys dropped; only blocked entries');
  console.log('dogfood self-check ok');
}

if (import.meta.main) {
  const { values } = parseArgs({ options: { 'rc-ini': { type: 'string' }, 'dry-run': { type: 'boolean' }, 'self-check': { type: 'boolean' } } });
  if (values['self-check']) { selfCheck(); process.exit(0); }
  const repo = parseIni(readFileSync(join(ROOT, 'native', 'ts3native', 'ts3native.ini'), 'utf8'));
  const vs = JSON.parse(readFileSync(join(ROOT, 'docs', 'data', 'verified-switches.json'), 'utf8')).switches ?? [];
  const rc = values['rc-ini'] && resolve(ROOT, values['rc-ini']);
  if (rc && !existsSync(rc)) { console.error(`no ${rc}`); process.exit(1); }
  const want = rc ? parseIni(readFileSync(rc, 'utf8')) : merge(RC_ON, blockedOff(vs));
  const tuning = tuningDefaults(readFileSync(join(ROOT, 'tools', 'setup', 'build-fixes.ts'), 'utf8'));
  const out = { about: 'tools/release/dogfood.ts (docs/279 "Dogfood"): ini keys merged under every correctness job\'s own ini by tools/harness/queue.ts', generated: new Date().toISOString().slice(0, 10),
    source: rc ? `rc ini ${values['rc-ini']}` : 'master + release plan §1 ON + blocked off', ini: overlay(repo, want), excluded: [...EXCLUDED, 'save-data tunables (savedata-fixes)'],
    tunablesNotApplied: rc ? [] : RC_TUNABLES.filter(t => tuning.get(t) !== true) };
  const text = `${JSON.stringify(out, null, 1)}\n`;
  if (values['dry-run']) console.log(text); else { writeFileSync(join(ROOT, 'tools', 'harness', 'dogfood.json'), text); console.log(`dogfood: ${Object.keys(out.ini).length} ini keys -> tools/harness/dogfood.json (${out.source})`); }
}
