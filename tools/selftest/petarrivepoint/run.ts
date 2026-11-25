// Offline self-check of petarrivepoint (mods/ts3fixes/src/PetArrivePointCore.cs, docs/656 §4): rule
// cases, exemption and bounds.
// Windows (.NET Framework csc), or Linux with TS3_CSC (csc-shim).
//   node tools/selftest/petarrivepoint/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'petarrivepoint-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'PetArrivePointTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'PetArrivePointCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
