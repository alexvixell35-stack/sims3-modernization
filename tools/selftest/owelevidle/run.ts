// Offline self-check of owelevidle (mods/ts3fixes/src/OwElevIdleCore.cs, docs/655): only an elevator entry whose Sim
// shows no sign of a ride is dropped before Overwatch's CleanupElevators; riders and exempt Sims stay with Overwatch.
// Windows (.NET Framework csc), or Linux with TS3_CSC (csc-shim).
//   node tools/selftest/owelevidle/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'owelevidle-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'OwElevIdleTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'OwElevIdleCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
