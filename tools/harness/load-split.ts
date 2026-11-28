// Load split (docs/17 M1, docs/302 §6.3 job 1): cycle.ts --save logs one line per load, queue.ts copies it into row.loadSplit.
//   launch->menu = cycle.ts start (the probe has just launched TS3W.exe) to the "Mod Scripts Found" dialog (the main menu is up;
//   every arm has ts3fixes, so the dialog always comes); menu->live = load_save sent to live mode ready; peak private = the
//   process's PeakPagefileUsage (peak commit charge = peak private bytes) read at live.
//   node tools/harness/load-split.ts --self-check
import { execFileSync } from 'node:child_process';

export const LOAD_SPLIT_RE = /^(?:\d\d:\d\d:\d\d )?load split: launch->menu (\d+\.\d|-) s, menu->live (\d+\.\d) s, peak private (\d+|-) MB$/;
export type LoadSplit = { menuS: number | null; liveS: number; peakPrivateMB: number | null };

export const loadSplitLine = (start: number, menu: number, load: number, live: number, peakMB: number | null) =>
  `load split: launch->menu ${menu ? ((menu - start) / 1000).toFixed(1) : '-'} s, menu->live ${((live - load) / 1000).toFixed(1)} s, peak private ${peakMB ?? '-'} MB`;

export function parseLoadSplit(text: string): LoadSplit | null {
  const m = text.split(/\r?\n/).map(l => LOAD_SPLIT_RE.exec(l)).filter(Boolean).pop();
  return m ? { menuS: m[1] === '-' ? null : Number(m[1]), liveS: Number(m[2]), peakPrivateMB: m[3] === '-' ? null : Number(m[3]) } : null;
}

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
  assert.equal(l, 'load split: launch->menu 63.5 s, menu->live 111.3 s, peak private 2911 MB');
  // regexCases: the line as cycle.ts logs it (stamped) and bare; non-matches
  assert.ok(LOAD_SPLIT_RE.test(`06:12:01 ${l}`)); assert.ok(LOAD_SPLIT_RE.test(l));
  assert.ok(LOAD_SPLIT_RE.test(loadSplitLine(0, 0, 0, 5000, null)), 'no dialog, no peak read');
  assert.ok(!LOAD_SPLIT_RE.test('06:12:01 ready: {"in_world":true}'));
  assert.ok(!LOAD_SPLIT_RE.test('load split: launch->menu 63.5 s, menu->live 111.3 s'));
  assert.deepEqual(parseLoadSplit(`x\r\n06:12:01 ${loadSplitLine(0, 0, 0, 5000, null)}\r\n06:20:00 ${l}\r\n`), { menuS: 63.5, liveS: 111.3, peakPrivateMB: 2911 }, 'last load wins');
  assert.equal(parseLoadSplit('nothing'), null);
  assert.equal(peakPrivateMB('ts3w-not-running-selfcheck'), null);
  if (process.platform === 'win32') assert.ok((peakPrivateMB('node') ?? 0) > 0, 'reads a running process');
  console.log('self-check ok');
}
