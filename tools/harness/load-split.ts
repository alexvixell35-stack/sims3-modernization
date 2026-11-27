// Load split (docs/17 M1, docs/302 §6.3 job 1): cycle.ts --save logs one line per load, queue.ts copies it into row.loadSplit.
//   launch->menu = cycle.ts start (the probe has just launched TS3W.exe) to the "Mod Scripts Found" dialog (the main menu is up;
//   every arm has ts3fixes, so the dialog always comes); menu->live = load_save sent to live mode ready; peak private = the
//   process's PeakPagefileUsage (peak commit charge = peak private bytes) read at live; launch->live = cycle.ts start to live
//   mode ready (the number a direct load and a menu load compare on).
// Direct load (queue job key launch "direct"): the probe starts the game with EA's -worldFile:"<save>.sims3" switch (as Apex
//   Radiance's launcher and restart host do), MainMenuState.Startup loads the save without the menu, and cycle.ts --direct
//   only waits for the world; it proves the load with one DIRECT_LOAD_RE line (the save read back from game_state.load_file).
//   node tools/harness/load-split.ts --self-check
import { execFileSync } from 'node:child_process';

// launch->live is optional: lines logged before 2026-11-27 end at "MB"
export const LOAD_SPLIT_RE = /^(?:\d\d:\d\d:\d\d )?load split: launch->menu (\d+\.\d|-) s, menu->live (\d+\.\d) s, peak private (\d+|-) MB(?:, launch->live (\d+\.\d) s)?$/;
export type LoadSplit = { menuS: number | null; liveS: number; peakPrivateMB: number | null; totalS: number | null };

export const loadSplitLine = (start: number, menu: number, load: number, live: number, peakMB: number | null) =>
  `load split: launch->menu ${menu ? ((menu - start) / 1000).toFixed(1) : '-'} s, menu->live ${((live - load) / 1000).toFixed(1)} s, peak private ${peakMB ?? '-'} MB, launch->live ${((live - start) / 1000).toFixed(1)} s`;

export function parseLoadSplit(text: string): LoadSplit | null {
  const m = text.split(/\r?\n/).map(l => LOAD_SPLIT_RE.exec(l)).filter(Boolean).pop();
  return m ? { menuS: m[1] === '-' ? null : Number(m[1]), liveS: Number(m[2]), peakPrivateMB: m[3] === '-' ? null : Number(m[3]), totalS: m[4] ? Number(m[4]) : null } : null;
}

// ---- direct load
const sims3 = (s: string) => (/\.sims3$/i.test(s) ? s : `${s}.sims3`);
// the save in the -worldFile switch and game_state.load_file name the same file (case and a folder prefix aside)
export const sameSave = (want: string, loadFile: unknown) =>
  typeof loadFile === 'string' && loadFile !== '' && sims3(loadFile.split(/[\\/]/).pop()!).toLowerCase() === sims3(want).toLowerCase();
export const DIRECT_LOAD_RE = /^(?:\d\d:\d\d:\d\d )?direct load: -worldFile:"([^"]+\.sims3)" in world after (\d+\.\d) s from launch, load_file (\S.*)$/;
export const directLoadLine = (save: string, start: number, live: number, loadFile: string) =>
  `direct load: -worldFile:"${sims3(save)}" in world after ${((live - start) / 1000).toFixed(1)} s from launch, load_file ${loadFile}`;
// what queue.ts asks of cycle.ts --direct output: the last direct load line, for this save
export function directLoadProof(text: string, save: string): { ok: boolean; why: string; s: number | null } {
  const m = text.split(/\r?\n/).map(l => DIRECT_LOAD_RE.exec(l)).filter(Boolean).pop();
  if (!m) return { ok: false, why: 'no "direct load:" line in the cycle.ts output', s: null };
  if (!sameSave(save, m[1]) || !sameSave(save, m[3])) return { ok: false, why: `direct load line names ${m[1]}, load_file ${m[3]}; expected ${sims3(save)}`, s: null };
  return { ok: true, why: '', s: Number(m[2]) };
}
// game_state at the main menu: the agent answers, no world, no load file. A -worldFile load sets GameStates.LoadFileName when it
// starts (inferred from the menu path, where load_save sets it; the smoke queue apex-direct-world-smoke-1127 checks it).
export const atMenu = (gs: any) => gs?.ok === true && gs.result?.in_world === false && !gs.result?.load_file && !gs.result?.shutting_down;

// .NET PeakPagedMemorySize64 = PROCESS_MEMORY_COUNTERS.PeakPagefileUsage [inferred: .NET reference source maps PageFileBytesPeak];
// null when the read fails (not Windows, game gone)
export function peakPrivateMB(exe = 'TS3W'): number | null {
  if (process.platform !== 'win32') return null;
  try {
    const out = execFileSync('powershell', ['-NoProfile', '-NonInteractive', '-Command', `(Get-Process -Name ${exe} -ErrorAction Stop | Select-Object -First 1).PeakPagedMemorySize64`], { encoding: 'utf8', timeout: 15000, stdio: ['ignore', 'pipe', 'ignore'] });
    const b = Number(out.trim());
    return Number.isFinite(b) && b > 0 ? Math.round(b / 1048576) : null;
  } catch { return null; }
}

if (import.meta.main) {
  if (process.argv[2] !== '--self-check') { console.error('usage: node tools/harness/load-split.ts --self-check'); process.exit(2); }
  const assert = (await import('node:assert')).strict;
  const l = loadSplitLine(1000, 64500, 70000, 181250, 2911);
  assert.equal(l, 'load split: launch->menu 63.5 s, menu->live 111.3 s, peak private 2911 MB, launch->live 180.3 s');
  // a line as cycle.ts logged it before launch->live (heapcall-monojit-off-1126 job log): still parses, total unknown
  assert.deepEqual(parseLoadSplit('11:41:00 load split: launch->menu 70.1 s, menu->live 27.9 s, peak private 1435 MB'), { menuS: 70.1, liveS: 27.9, peakPrivateMB: 1435, totalS: null });
  assert.ok(!LOAD_SPLIT_RE.test('11:41:00 load split: launch->menu 70.1 s, menu->live 27.9 s, peak private 1435 MB, launch->live 98 s'), 'one decimal');
  // regexCases: the line as cycle.ts logs it (stamped) and bare; non-matches
  assert.ok(LOAD_SPLIT_RE.test(`06:12:01 ${l}`)); assert.ok(LOAD_SPLIT_RE.test(l));
  assert.ok(LOAD_SPLIT_RE.test(loadSplitLine(0, 0, 0, 5000, null)), 'no dialog, no peak read');
  assert.ok(!LOAD_SPLIT_RE.test('06:12:01 ready: {"in_world":true}'));
  assert.ok(!LOAD_SPLIT_RE.test('load split: launch->menu 63.5 s, menu->live 111.3 s'));
  assert.deepEqual(parseLoadSplit(`x\r\n06:12:01 ${loadSplitLine(0, 0, 0, 5000, null)}\r\n06:20:00 ${l}\r\n`), { menuS: 63.5, liveS: 111.3, peakPrivateMB: 2911, totalS: 180.3 }, 'last load wins');
  // direct load: the line cycle.ts --direct logs, read back by queue.ts; a ready line or another save never counts
  const d = directLoadLine('Baseline-SV', 1000, 62500, 'Baseline-SV.sims3');
  assert.equal(d, 'direct load: -worldFile:"Baseline-SV.sims3" in world after 61.5 s from launch, load_file Baseline-SV.sims3');
  assert.ok(DIRECT_LOAD_RE.test(`12:00:01 ${d}`) && DIRECT_LOAD_RE.test(d));
  assert.ok(!DIRECT_LOAD_RE.test('12:00:01 ready: {"in_world":true,"load_file":"Baseline-SV.sims3"}'));
  assert.ok(!DIRECT_LOAD_RE.test('direct load: -worldFile:"Sunset Valley.world" in world after 61.5 s from launch, load_file x'), 'a .world is no save');
  assert.ok(!DIRECT_LOAD_RE.test('direct load: -worldFile:"Baseline-SV.sims3" in world after 61 s from launch, load_file Baseline-SV.sims3'), 'one decimal');
  assert.deepEqual(directLoadProof(`x\r\n12:00:01 ${d}\r\n`, 'Baseline-SV.sims3'), { ok: true, why: '', s: 61.5 });
  assert.equal(directLoadProof(`12:00:01 ${directLoadLine('Baseline-SV', 0, 1000, 'Baseline-BP.sims3')}`, 'Baseline-SV').ok, false, 'load_file of another save');
  assert.equal(directLoadProof('12:00:01 load split: launch->menu - s, menu->live 5.0 s, peak private - MB', 'Baseline-SV').ok, false);
  assert.ok(sameSave('Baseline-SV', 'baseline-sv.SIMS3') && sameSave('My Town.sims3', 'C:\\Saves\\My Town.sims3'));
  assert.ok(!sameSave('Baseline-SV', '') && !sameSave('Baseline-SV', null) && !sameSave('Baseline-SV', 'Baseline-SV-Spring.sims3'));
  // game_state replies (HarnessHandlers.GameState): at the menu; no answer (loading); in the world; a load started
  assert.ok(atMenu({ ok: true, result: { in_world: false, sub_state: null, load_file: null, shutting_down: false } }));
  assert.ok(!atMenu({ ok: false, error: 'no reply' }) && !atMenu({ ok: true, result: { in_world: true, load_file: 'Baseline-SV.sims3' } }));
  assert.ok(!atMenu({ ok: true, result: { in_world: false, load_file: 'Baseline-SV.sims3' } }), 'load started: not the menu');
  assert.equal(parseLoadSplit('nothing'), null);
  assert.equal(peakPrivateMB('ts3w-not-running-selfcheck'), null);
  if (process.platform === 'win32') assert.ok((peakPrivateMB('node') ?? 0) > 0, 'reads a running process');
  console.log('self-check ok');
}
