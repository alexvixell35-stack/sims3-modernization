// Offline self-check of the decision of jazzparam (mods/ts3fixes/src/JazzParamCore.cs, issue #166 build 21): which exceptions
// from Sim.UpdateJazzParameter it counts and catches.
// Windows (.NET Framework csc).
//   node tools/selftest/jazzparam/run.ts
import { execFileSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const CSC = 'C:/Windows/Microsoft.NET/Framework/v4.0.30319/csc.exe';
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'jazzparam-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'JazzParamTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'JazzParamCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
