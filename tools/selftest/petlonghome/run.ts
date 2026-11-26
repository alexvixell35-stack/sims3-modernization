// Offline self-check of petlonghome (mods/ts3fixes/src/PetLongHomeCore.cs, docs/656 §8.2): the gate order,
// distance and trip-age rules, unseen checks and the trip table.
// Windows (.NET Framework csc), or Linux with TS3_CSC (csc-shim).
//   node tools/selftest/petlonghome/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'petlonghome-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'PetLongHomeTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'PetLongHomeCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
