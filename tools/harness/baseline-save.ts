// Community baseline save recipe (docs/23 #1): one command that builds and checks a Baseline-BP/SV/IP the same way on any machine.
//   node tools/harness/baseline-save.ts --list                      recipes in docs/data/baseline-saves.json
//   node tools/harness/baseline-save.ts --world bp                  print the plan (no game touched)
//   node tools/harness/baseline-save.ts --world bp --run            new-save.ts then age-save.ts (game at the main menu, agent attached)
//   node tools/harness/baseline-save.ts --world bp --check [save]   save-stats fingerprint of the save vs the recipe's ranges
//   node tools/harness/baseline-save.ts --self-check
// Preconditions for --run (printed with the plan): Default.ini [Config] SimulatorRandomSeed = <seed> (docs/27; queue.ts jobs set it
// with defaultIni), community layer only, game restarted to the main menu (cycle.ts --restart). Never run it while a queue owns the game.
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { join } from 'node:path';
import { parseArgs } from 'node:util';
import { saveStats } from './save-stats.ts';

export type Fingerprint = { totalMB: number; types: Record<string, number> }; // types: resource type (hex) -> entry count
export type World = { save: string; worldFile: string; household: string; members: number; days: number; checkpoint: number; keepAlive: string; fingerprint: Fingerprint | null };
export type Recipe = { seed: number; tolerancePct: number; worlds: Record<string, World> };

const here = import.meta.dirname;
const RECIPE = join(here, '..', '..', 'docs', 'data', 'baseline-saves.json');

// the two harness scripts and their arguments; the seed is a Default.ini precondition, not a script argument
export function plan(w: World): string[][] {
  return [
    ['new-save.ts', '--world', w.worldFile, '--save', `${w.save}-fresh`, '--household', w.household, '--members', String(w.members)],
    ['age-save.ts', '--days', String(w.days), '--save', w.save, '--checkpoint', String(w.checkpoint), '--keep-alive', w.keepAlive],
  ];
}

// measured fingerprint of a save folder: total MB and entry count of the 10 biggest resource types
export function fingerprint(dir: string): Fingerprint {
  const { total, stats } = saveStats(dir);
  const top = [...stats.entries()].sort((a, b) => b[1].bytes - a[1].bytes).slice(0, 10);
  return { totalMB: +(total / 1048576).toFixed(1), types: Object.fromEntries(top.map(([k, s]) => [k, s.count])) };
}

// mismatches (empty = equivalent): every recorded value must be within tolPct of the measured one
export function compare(want: Fingerprint, got: Fingerprint, tolPct: number): string[] {
  const bad: string[] = [];
  const off = (a: number, b: number) => Math.abs(a - b) > Math.abs(a) * tolPct / 100;
  if (off(want.totalMB, got.totalMB)) bad.push(`total MB ${got.totalMB} vs ${want.totalMB}`);
  for (const [t, n] of Object.entries(want.types)) {
    const g = got.types[t];
    if (g === undefined) bad.push(`type ${t} missing (want ${n})`);
    else if (off(n, g)) bad.push(`type ${t} count ${g} vs ${n}`);
  }
  return bad;
}

function selfCheck() {
  const w: World = { save: 'B', worldFile: 'X.world', household: 'H', members: 2, days: 14, checkpoint: 2, keepAlive: '-50', fingerprint: null };
  const p = plan(w);
  console.assert(p.length === 2 && p[0].includes('H') && p[1].join(' ') === 'age-save.ts --days 14 --save B --checkpoint 2 --keep-alive -50', 'plan');
  const f: Fingerprint = { totalMB: 100, types: { '0x1': 1000 } };
  console.assert(compare(f, { totalMB: 110, types: { '0x1': 900 } }, 15).length === 0, 'inside tolerance');
  console.assert(compare(f, { totalMB: 130, types: { '0x1': 1000 } }, 15).length === 1, 'total off');
  console.assert(compare(f, { totalMB: 100, types: {} }, 15)[0] === 'type 0x1 missing (want 1000)', 'type missing');
  const r: Recipe = JSON.parse(readFileSync(RECIPE, 'utf8'));
  for (const [id, x] of Object.entries(r.worlds)) console.assert(/^Baseline-/.test(x.save) && x.worldFile.endsWith('.world') && x.days > 0, `recipe ${id}`);
  console.log('baseline-save self-check ok');
}

if (process.argv[1]?.endsWith('baseline-save.ts')) {
  const { values, positionals } = parseArgs({ options: { world: { type: 'string' }, run: { type: 'boolean' }, check: { type: 'boolean' }, list: { type: 'boolean' }, 'self-check': { type: 'boolean' } }, allowPositionals: true });
  if (values['self-check']) { selfCheck(); process.exit(process.exitCode ?? 0); }
  const r: Recipe = JSON.parse(readFileSync(RECIPE, 'utf8'));
  if (values.list || !values.world) {
    for (const [id, w] of Object.entries(r.worlds)) console.log(`${id}: ${w.save} = ${w.worldFile}, ${w.household} (${w.members} members), ${w.days} days, fingerprint ${w.fingerprint ? 'recorded' : 'not recorded'}`);
    process.exit(values.list ? 0 : 2);
  }
  const w = r.worlds[values.world];
  if (!w) { console.error(`unknown world "${values.world}" (${Object.keys(r.worlds).join(', ')})`); process.exit(2); }
  if (values.check) {
    const name = (positionals[0] ?? w.save).replace(/\.sims3$/, '');
    const dir = join(process.env.TS3_USER ?? join(homedir(), 'Documents', 'Electronic Arts', 'The Sims 3'), 'Saves', `${name}.sims3`);
    const got = fingerprint(dir);
    if (!w.fingerprint) { console.log(`no fingerprint recorded for ${values.world}; measured (paste into docs/data/baseline-saves.json): ${JSON.stringify(got)}`); process.exit(0); }
    const bad = compare(w.fingerprint, got, r.tolerancePct);
    console.log(bad.length ? `NOT equivalent to the ${w.save} recipe:\n  ${bad.join('\n  ')}` : `equivalent to the ${w.save} recipe (within ${r.tolerancePct}%)`);
    process.exit(bad.length ? 1 : 0);
  }
  const steps = plan(w);
  console.log(`Preconditions: Default.ini [Config] SimulatorRandomSeed = ${r.seed}; community layer only; game at the main menu (cycle.ts --restart); back up your Saves folder first (a save with the name ${w.save} is overwritten).`);
  for (const s of steps) console.log(`  node tools/harness/${s.join(' ')}`);
  if (values.run) for (const s of steps) execFileSync(process.execPath, [join(here, s[0]), ...s.slice(1)], { stdio: 'inherit', timeout: 4 * 3600_000 });
  else console.log('Dry run. Add --run to execute, then --check.');
}
