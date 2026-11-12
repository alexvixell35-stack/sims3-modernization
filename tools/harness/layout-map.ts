// build_layout's map parser offline (mods/ts3agent/src/plugin/LayoutMap.cs, the parser the game runs): how a text map turns into
// wall edges, tiles and facings, without the game. Windows (.NET Framework csc).
//   node tools/harness/layout-map.ts <map.txt>     print the plan: size, wall runs, item tiles and facings
//   node tools/harness/layout-map.ts --self-check  parser fixtures (tools/selftest/layoutmap/LayoutMapTest.cs)
import { execFileSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..');
const CSC = process.env.TS3_CSC ?? 'C:/Windows/Microsoft.NET/Framework/v4.0.30319/csc.exe'; // TS3_CSC: another C# compiler, e.g. Roslyn on Linux
const arg = process.argv[2];
if (!arg) { console.error('usage: layout-map.ts <map.txt> | --self-check'); process.exit(2); }
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'layoutmap-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(ROOT, 'tools', 'selftest', 'layoutmap', 'LayoutMapTest.cs'), join(ROOT, 'mods', 'ts3agent', 'src', 'plugin', 'LayoutMap.cs')], { stdio: 'inherit' });
execFileSync(exe, arg === '--self-check' ? [] : [arg], { stdio: 'inherit' });
