// Offline self-check of the classification of dancefloorlistdiag (mods/ts3fixes/src/DanceFloorDiagCore.cs, docs/253 §7): what a route
// step left in the local list.
// Windows (.NET Framework csc).
//   node tools/selftest/dancefloorlistdiag/run.ts
import { execFileSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const CSC = 'C:/Windows/Microsoft.NET/Framework/v4.0.30319/csc.exe';
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'dancefloorlistdiag-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'DanceFloorDiagTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'DanceFloorDiagCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
