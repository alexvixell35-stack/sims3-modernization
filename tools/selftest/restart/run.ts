// Offline self-check of the wrapper restart's record (mods/ts3fixes/src/RestartCore.cs, docs/530 step 1): the state as
// handoff lines and back, every refusal of a bad line, the limits, the save-name match and the bit reads. Windows
// (.NET Framework csc).
//   node tools/selftest/restart/run.ts
import { execFileSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const CSC = 'C:/Windows/Microsoft.NET/Framework/v4.0.30319/csc.exe';
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'restart-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'RestartTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'RestartCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
