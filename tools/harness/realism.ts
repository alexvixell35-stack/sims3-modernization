// Gameplay realism (docs/179): what a player could have done. Reads docs/data/gameplay-preconditions.csv (one row per agent
// command: the game modes it is valid in, the player action it stands for, its realism class) and labels step lists.
//   node tools/harness/realism.ts --repros        label every repros.json entry (steps + loop) and list mode violations
//   node tools/harness/realism.ts --self-check    CSV covers every registered agent command; fixtures; repros mode-clean
// Labels: natural (a player can do it; harness reads count as natural) < cheat (a cheat or mod a player could use, or a
// forced jump such as pregnancy timers) < stress (volume or speed no player produces, or no player equivalent at all).
// story-lint.ts checks templates with the same rules (`realism` must be declared and not lower than the computed label).
import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = resolve(import.meta.dirname, '..', '..');
export const CSV = join(ROOT, 'docs', 'data', 'gameplay-preconditions.csv');
export const LABELS = ['natural', 'cheat', 'stress'] as const;
export type Label = typeof LABELS[number];
export type Row = { command: string; states: string[]; realism: string; player_action: string; preconditions: string; gate: string };
const RANK: Record<string, number> = { harness: 0, natural: 0, cas: 0, cheat: 1, stress: 2 };
// repro steps the harness runs itself (probe and window calls, not agent commands): no game action
const PROBE = new Set(['native_set', 'native_mark', 'native_check', 'window_restore', 'window_minimize', 'screenshot']);

// minimal CSV: quoted fields may hold commas
export function parseCsv(text: string): Record<string, string>[] {
  const rows: string[][] = [];
  for (const line of text.split(/\r?\n/)) {
    if (!line.trim()) continue;
    const f: string[] = []; let cur = '', q = false;
    for (let i = 0; i < line.length; i++) {
      const c = line[i];
      if (q) { if (c === '"' && line[i + 1] === '"') { cur += '"'; i++; } else if (c === '"') q = false; else cur += c; }
      else if (c === '"') q = true; else if (c === ',') { f.push(cur); cur = ''; } else cur += c;
    }
    f.push(cur); rows.push(f);
  }
  const [h, ...body] = rows;
  return body.map(r => Object.fromEntries(h.map((k, i) => [k, r[i] ?? ''])));
}

let cache: Map<string, Row> | undefined;
export function table(file = CSV): Map<string, Row> {
  if (cache && file === CSV) return cache;
  const m = new Map(parseCsv(readFileSync(file, 'utf8')).map(r => [r.command, { ...r, states: r.states.split(';') } as Row]));
  if (file === CSV) cache = m;
  return m;
}

// per step: the CSV class, raised by args that make an otherwise natural action a cheat
export function stepLabel(s: { cmd: string; args?: any }, t = table()): Label {
  const r = t.get(s.cmd);
  if (!r) return PROBE.has(s.cmd) ? 'natural' : 'stress'; // unknown command: no player equivalent known
  let k = RANK[r.realism] ?? 2;
  if (s.args?.free === true) k = Math.max(k, 1); // buy_object free: no money spent
  if (s.cmd === 'career' && s.args?.action === 'promote') k = Math.max(k, 1);
  if (s.cmd === 'make_household' && s.args?.funds !== undefined) k = Math.max(k, 1); // familyfunds cheat (docs/181 §3.1)
  return LABELS[k];
}
export const worst = (ls: Label[]): Label => LABELS[Math.max(0, ...ls.map(l => LABELS.indexOf(l)))];

// game mode a step leaves the game in (the agent's mode commands), and steps sent in a mode their row does not allow
const NEXT: Record<string, (a: any) => string> = {
  build_mode: a => a?.on === false ? 'Live' : 'Build', buy_open: () => 'Buy', buy_close: () => 'Live', cas_open: () => 'CAS',
  cas_close: () => 'Live', enter_live_mode: () => 'Live', travel_vacation: () => 'Travel', travel_university: () => 'Travel',
  travel_future: () => 'Travel', travel_home: () => 'Live', fresh_world: () => 'Live', load_save: () => 'Live', main_menu: () => 'MainMenu',
  // start-of-game paths (StartPathHandlers.cs): the family picker is the map view
  load_world: a => (a?.state === 'PlayFlow' ? 'Map' : a?.state === 'EditTown' ? 'EditTown' : 'Live'), enter_play_flow: () => 'Map',
  picker_play: () => 'Live', picker_nav: a => ((a?.nav ?? 'Play') === 'Play' ? 'Live' : 'Map'), picker_cas: () => 'CAS',
  et_move_lot: () => 'EditTown',
};
export function modeViolations(steps: { cmd: string; args?: any }[], t = table(), start = 'Live'): string[] {
  const out: string[] = [];
  let mode = start, casBack = 'Live'; // CAS Done returns to where CAS was opened from (the picker's CAS flow = Map)
  for (const [i, s] of steps.entries()) {
    const r = t.get(s.cmd);
    const ok = !r || r.states.includes('Any') || r.states.includes(mode) || (mode === 'Travel' && r.states.includes('Live'));
    if (!ok) out.push(`step ${i} ${s.cmd} in ${mode} (valid: ${r!.states.join('/')})`);
    if (s.cmd === 'picker_cas' || s.cmd === 'cas_open') casBack = s.cmd === 'picker_cas' ? 'Map' : 'Live';
    if (s.cmd === 'cas_close') mode = casBack;
    else if (NEXT[s.cmd]) mode = NEXT[s.cmd](s.args);
  }
  return out;
}

// repros.json: steps plus every loop phase that holds steps
export function reproSteps(r: any): { cmd: string; args?: any }[][] {
  const seqs = [r.steps ?? []];
  for (const v of Object.values(r.loop ?? {})) if (Array.isArray(v) && v.some((s: any) => s?.cmd)) seqs.push(v);
  return seqs;
}
export function labelRepro(r: any, t = table()): { label: Label; why: string[]; modes: string[] } {
  const all = reproSteps(r).flat().filter((s: any) => s?.cmd);
  const label = worst(all.map(s => stepLabel(s, t)));
  const why = [...new Set(all.filter(s => stepLabel(s, t) === label && label !== 'natural').map(s => s.cmd))];
  const start = r.save === 'menu' ? 'MainMenu' : 'Live'; // a "menu" repro starts at the main menu (queue save "menu")
  return { label, why, modes: reproSteps(r).flatMap(s => modeViolations(s.filter((x: any) => x?.cmd), t, start)) };
}

function registered(): Set<string> {
  const dir = join(ROOT, 'mods', 'ts3agent', 'src', 'plugin', 'Handlers'), out = new Set<string>();
  for (const f of readdirSync(dir).filter(f => f.endsWith('.cs')))
    for (const m of readFileSync(join(dir, f), 'utf8').matchAll(/Register\("([a-z_0-9]+)"/g)) out.add(m[1]);
  return out;
}

// the mode gate each command has in the agent: GateHandlers.cs wraps (live | live-or-buy), else a RequireLive / LiveModeState
// check in the handler's first lines (live), else none
export function gates(): Map<string, string> {
  const dir = join(ROOT, 'mods', 'ts3agent', 'src', 'plugin', 'Handlers'), src = readdirSync(dir).filter(f => f.endsWith('.cs')).map(f => readFileSync(join(dir, f), 'utf8')).join('\n');
  const out = new Map<string, string>();
  for (const m of src.matchAll(/Dispatcher\.Register\("([a-z_0-9]+)", ([A-Za-z]+)\);/g)) {
    const body = new RegExp(`static object ${m[2]}\\(Args a\\)[\\s\\S]{0,400}`).exec(src)?.[0] ?? '';
    if (/RequireLive\(\)|SubState\(\) != "LiveModeState"/.test(body.split('\n').slice(0, 5).join('\n'))) out.set(m[1], 'live');
  }
  for (const m of src.matchAll(/Dispatcher\.Register\("([a-z_0-9]+)", Gate\((kLiveOnly|kLiveOrBuy),/g)) out.set(m[1], m[2] === 'kLiveOnly' ? 'live' : 'live-or-buy');
  return out;
}

function selfCheck() {
  const t = table(), reg = registered(), g = gates();
  const wrong = [...t.values()].filter(r => r.gate !== (g.get(r.command) ?? 'none')).map(r => `${r.command}: CSV ${r.gate}, agent ${g.get(r.command) ?? 'none'}`);
  assert.deepEqual(wrong, [], 'CSV gate column = the agent\'s mode gates');
  const missing = [...reg].filter(c => !t.has(c)), extra = [...t.keys()].filter(c => !reg.has(c));
  assert.deepEqual(missing, [], 'every registered agent command has a CSV row');
  assert.deepEqual(extra, [], 'every CSV row is a registered agent command');
  for (const [c, r] of t) {
    assert.ok(r.realism in RANK, `${c}: realism ${r.realism}`);
    assert.ok(r.states.every(s => ['Any', 'Live', 'Build', 'Buy', 'CAS', 'EditTown', 'Map', 'Travel', 'MainMenu'].includes(s)), `${c}: states ${r.states}`);
  }
  // fixtures: the build-mode make_sim of the 0x575651 crash; a cheat; a stress repro
  assert.deepEqual(modeViolations([{ cmd: 'build_mode', args: { on: true } }, { cmd: 'make_sim' }, { cmd: 'build_mode', args: { on: false } }]), ['step 1 make_sim in Build (valid: Live)']);
  assert.deepEqual(modeViolations([{ cmd: 'buy_open' }, { cmd: 'buy_object' }, { cmd: 'buy_close' }, { cmd: 'go_here' }]), []);
  assert.equal(worst([stepLabel({ cmd: 'go_here' }), stepLabel({ cmd: 'buy_object', args: { free: true } })]), 'cheat');
  assert.equal(stepLabel({ cmd: 'make_household', args: { funds: 30000 } }), 'cheat');
  assert.equal(stepLabel({ cmd: 'make_household', args: {} }), 'natural');
  assert.equal(labelRepro({ steps: [{ cmd: 'spawn_sims', args: { count: 30 } }, { cmd: 'crowd_go' }] }).label, 'stress');
  // start-of-game paths: menu -> picker -> CAS -> back to the picker -> lot -> Play -> live; a picker step in live mode is not
  const cas = [{ cmd: 'load_world', args: { state: 'PlayFlow' } }, { cmd: 'picker_cas' }, { cmd: 'cas_close' }, { cmd: 'picker_lot' }, { cmd: 'picker_nav' }, { cmd: 'save_game' }];
  assert.deepEqual(modeViolations(cas, t, 'MainMenu'), []);
  assert.deepEqual(labelRepro({ save: 'menu', steps: cas }).modes, []);
  assert.deepEqual(modeViolations([{ cmd: 'picker_play' }]), ['step 0 picker_play in Live (valid: Map)']);
  assert.deepEqual(modeViolations([{ cmd: 'et_move_lot', args: { action: 'enter' } }, { cmd: 'et_move_lot' }, { cmd: 'enter_live_mode' }, { cmd: 'go_here' }]), []);
  const repros = JSON.parse(readFileSync(join(import.meta.dirname, 'repros.json'), 'utf8'));
  const bad = repros.flatMap((r: any) => labelRepro(r, t).modes.map(m => `${r.id}: ${m}`));
  assert.deepEqual(bad, [], 'repros.json: no step in a game mode a player could not send it in');
  console.log(`realism self-check ok (${t.size} commands, ${repros.length} repros mode-clean)`);
}

if (resolve(process.argv[1] ?? '') === fileURLToPath(import.meta.url)) {
  const args = process.argv.slice(2);
  if (args.includes('--self-check')) selfCheck();
  else if (args.includes('--repros')) {
    const repros = JSON.parse(readFileSync(join(import.meta.dirname, 'repros.json'), 'utf8'));
    const n: Record<string, number> = {};
    for (const r of repros) {
      const l = labelRepro(r);
      n[l.label] = (n[l.label] ?? 0) + 1;
      console.log(`${r.id},${l.label},${l.why.join(' ')}${l.modes.length ? `,MODE ${l.modes.join('; ')}` : ''}`);
    }
    console.log(`# ${repros.length} repros: ${LABELS.map(k => `${k} ${n[k] ?? 0}`).join(', ')}`);
  } else { console.log('usage: realism.ts --repros | --self-check'); process.exit(2); }
}
