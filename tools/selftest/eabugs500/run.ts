// Offline self-check of the game-independent part of the docs/500 fixes (mods/ts3fixes/src/EaBugs500Core.cs).
// Windows (.NET Framework csc).
//   node tools/selftest/eabugs500/run.ts
import { execFileSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const CSC = 'C:/Windows/Microsoft.NET/Framework/v4.0.30319/csc.exe';
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'eabugs500-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'EaBugs500Test.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'EaBugs500Core.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
