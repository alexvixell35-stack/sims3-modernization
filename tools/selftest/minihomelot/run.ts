// Offline self-check of the minihomelot sync rule (mods/ts3fixes/src/MiniHomeLotCore.cs, docs/266): when a Sim's
// MiniSimDescription home lot is set to its household's lot.
// Windows (.NET Framework csc).
//   node tools/selftest/minihomelot/run.ts
import { execFileSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const CSC = 'C:/Windows/Microsoft.NET/Framework/v4.0.30319/csc.exe';
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'minihomelot-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'MiniHomeLotTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'MiniHomeLotCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
