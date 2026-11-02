// Offline self-check of the decision of ageupabort (mods/ts3fixes/src/AgeUpAbortCore.cs, issue #166 build 32): which exceptions
// from AgingState.PreInstantiateSim it counts and catches.
// Windows (.NET Framework csc).
//   node tools/selftest/ageupabort/run.ts
import { execFileSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const CSC = 'C:/Windows/Microsoft.NET/Framework/v4.0.30319/csc.exe';
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'ageupabort-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'AgeUpAbortTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'AgeUpAbortCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
