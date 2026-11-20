// Triage extraction for a minidump: writes <name>-stacks.txt next to <name>.dmp (non-empty = triage done, cleanup.ts may then delete the .dmp).
//   node tools/harness/dump-stacks.ts <file.dmp>     exit 0 written/already there, 3 unreadable
//   node tools/harness/dump-stacks.ts --self-check
// Content: cdb ~*kvn 64 + !locks (marker line "# cdb ~*kvn 64"; only that counts as done), then tools/dump-locks.ts lock-wait text.
// (hang capture, kill dump) and by cleanup.ts for dumps that have no stacks file yet (docs/283).
import { execFileSync, spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

export const stacksPath = (dmp: string) => dmp.replace(/\.dmp$/i, '') + '-stacks.txt';
// Only a cdb-made file (thread headers from ~*kvn) counts as triage done; lock-wait text alone never lets the .dmp go (docs/283).
export const CDB_MARK = '# cdb ~*kvn 64';
export const hasStacks = (dmp: string) => { try { return statSync(stacksPath(dmp)).size > 0 && readFileSync(stacksPath(dmp), 'utf8').includes(CDB_MARK); } catch { return false; } };

// Windows Kits\10\Debuggers\x86|x64\cdb.exe; x86 for TS3W (32-bit), x64 for NvRemixBridge and the rest. TS3_CDB overrides (x86 path).
export function findCdb(arch: 'x86' | 'x64'): string | null {
  if (process.env.TS3_CDB) return existsSync(process.env.TS3_CDB) ? process.env.TS3_CDB : null;
  for (const pf of [process.env['ProgramFiles(x86)'], process.env.ProgramFiles, 'C:\\Program Files (x86)'])
    if (pf) { const c = join(pf, 'Windows Kits', '10', 'Debuggers', arch, 'cdb.exe'); if (existsSync(c)) return c; }
  // WinDbg (store) ships amd64|x86|arm64\cdb.exe under WindowsApps: not listable (ACL), but it runs; ask the package registry for the folder
  try {
    const loc = execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', '(Get-AppxPackage Microsoft.WinDbg* | Select-Object -First 1).InstallLocation'], { encoding: 'utf8', timeout: 30e3, windowsHide: true }).trim();
    const c = loc && join(loc, arch === 'x86' ? 'x86' : 'amd64', 'cdb.exe'); if (c && existsSync(c)) return c;
  } catch { /* no PowerShell / no package */ }
  return null;
}
export const archOf = (dmp: string): 'x86' | 'x64' => /^(ts3w|ts3-|ts3\.)/i.test(dmp.split(/[\\/]/).at(-1)!) ? 'x86' : 'x64';

export function cdbArgs(dmp: string, main: string, extraDirs: string[] = []): string[] {
  const dirs = [...extraDirs, ...(process.env.TS3_PDB_DIRS?.split(';') ?? []), ...pdbDirs(main)].filter(d => d && existsSync(d));
  const cache = join(main, 'probe-out', 'symcache'); try { mkdirSync(cache, { recursive: true }); } catch { /* cdb makes it or skips */ }
  // -y, not .symfix+/.sympath+: those take the rest of the line, so a `;` after them is part of the path
  return ['-y', [`srv*${cache}*https://msdl.microsoft.com/download/symbols`, ...dirs].join(';'), '-z', dmp, '-c', '.reload; ~*kvn 64; !locks; q'];
}
// folders that hold our .pdb files: ts3native / Apex / bridge build output (found under game-copy: rewind-bridge-*/<tree>/Release|Public, _output, apex-*)
export function pdbDirs(main: string): string[] {
  const out = [join(main, 'out'), join(main, 'out-master'), join(main, 'native', 'build')], gc = join(main, 'game-copy');
  for (const l1 of safeLs(gc)) for (const d of [join(gc, l1), ...safeLs(join(gc, l1)).map(l2 => join(gc, l1, l2))]) for (const sub of ['Release', 'Public', '_output', '']) if (existsSync(join(d, sub)) && safeLs(join(d, sub)).some(f => /\.pdb$/i.test(f))) { out.push(join(d, sub)); break; }
  return out.slice(0, 40); // path length stays below dbghelp's limits
}
const safeLs = (d: string) => { try { return readdirSync(d); } catch { return []; } };

export function extractStacks(dmp: string, o: { tool?: string; cdb?: string | null; main?: string } = {}): boolean {
  if (hasStacks(dmp)) return true;
  const main = o.main ?? join(import.meta.dirname, '..', '..'), cdb = o.cdb === undefined ? findCdb(archOf(dmp)) : o.cdb;
  const lock = spawnSync(process.execPath, [o.tool ?? join(import.meta.dirname, '..', 'dump-locks.ts'), dmp], { encoding: 'utf8', timeout: 300e3, windowsHide: true });
  const locks = lock.status === 0 ? `${lock.stdout ?? ''}`.trim() : '';
  let cdbOut = '';
  if (cdb) { const js = /\.(ts|js)$/.test(cdb), r = spawnSync(js ? process.execPath : cdb, [...(js ? [cdb] : []), ...cdbArgs(dmp, main)], { encoding: 'utf8', timeout: 300e3, windowsHide: true, maxBuffer: 256 << 20 }); cdbOut = `${r.stdout ?? ''}`; }
  const stacks = /^[.# ]*\d+\s+Id: /m.test(cdbOut) || /^\s*# ChildEBP|^\s*# Child-SP/m.test(cdbOut); // thread headers / kvn frames
  if (!stacks && !locks) return false;
  writeFileSync(stacksPath(dmp), `# triage extraction of ${dmp.split(/[\\/]/).at(-1)}, ${new Date().toISOString()}\n${stacks ? `${CDB_MARK}\n${cdbOut.trim()}\n` : '# no cdb stacks (cdb missing or failed): lock-wait text only, the .dmp stays\n'}${locks ? `# tools/dump-locks.ts\n${locks}\n` : ''}`);
  return stacks;
}

if (import.meta.main) {
  const a = process.argv[2];
  if (a === '--self-check') {
    const d = mkdtempSync(join(tmpdir(), 'dump-stacks-')), f = join(d, 'x.dmp'), t = join(d, 'fake-tool.ts');
    try {
      writeFileSync(f, 'junk'); writeFileSync(t, "console.log('thread 1 waits on cs 0x10 owned by thread 2');");
      const fakeCdb = join(d, 'cdb.ts'); writeFileSync(fakeCdb, "console.log('   0  Id: 1.2 Suspend: 1 Teb: 7ffd Unfrozen\\n# ChildEBP RetAddr');"); // a script stands in for cdb.exe
      if (extractStacks(f, { tool: join(d, 'missing.ts'), cdb: null })) throw new Error('nothing to extract must fail');
      if (hasStacks(f)) throw new Error('no file expected');
      if (extractStacks(f, { tool: t, cdb: null }) || hasStacks(f)) throw new Error('lock-only text must not count as stacks');
      if (!existsSync(join(d, 'x-stacks.txt'))) throw new Error('lock-only file still written');
      rmSync(join(d, 'x-stacks.txt'));
      if (!extractStacks(f, { tool: t, cdb: fakeCdb }) || !hasStacks(f)) throw new Error('cdb output must count');
      // archOf: Windows (backslash) and / paths; bridge and other dumps are x64
      for (const [p, w] of [['C:\\x\\TS3W-1.dmp', 'x86'], ['C:/x/ts3-hang-1.dmp', 'x86'], ['C:\\x\\NvRemixBridge.exe.123.dmp', 'x64'], ['C:\\ts3w\\other.dmp', 'x64']] as const)
        if (archOf(p) !== w) throw new Error(`archOf ${p}: ${archOf(p)}, want ${w}`);
      const fakeErr = join(d, 'cdb-err.ts'); writeFileSync(fakeErr, "console.log('Could not open dump file [x.dmp], Win32 error 0n2\\n    \"The system cannot find the file specified.\"');");
      rmSync(join(d, 'x-stacks.txt')); const g = join(d, 'y.dmp'); writeFileSync(g, 'junk');
      if (extractStacks(g, { tool: join(d, 'missing.ts'), cdb: fakeErr }) || hasStacks(g)) throw new Error('cdb error output is not stacks');
      console.log('ok   dump-stacks self-check');
    } finally { rmSync(d, { recursive: true, force: true }); }
  } else if (a) process.exit(extractStacks(a) ? 0 : 3);
  else { console.error('usage: node tools/harness/dump-stacks.ts <file.dmp>'); process.exit(2); }
}
