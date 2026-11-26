// Offline self-check of the sites whose household exemption was dropped (docs/289, mods/ts3fixes/src/HhAuditCore.cs):
// woohootywait, serviceshowalarm, exitcbsweep. Household-scope Sims take the fixed path; the guards that stay still hold.
// Windows (.NET Framework csc).
//   node tools/selftest/hhaudit/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'hhaudit-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'HhAuditTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'HhAuditCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
