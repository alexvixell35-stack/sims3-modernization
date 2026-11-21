// Offline self-check of elevatorlightrig (mods/ts3fixes/src/ElevatorLightRigCore.cs, docs/658 §7.1).
// Windows (.NET Framework csc), or Linux with TS3_CSC (csc-shim).
//   node tools/selftest/elevatorlightrig/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'elevatorlightrig-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'ElevatorLightRigTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'ElevatorLightRigCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
