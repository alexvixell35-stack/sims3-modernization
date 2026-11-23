// Offline self-check of the tester kit's A/B session counters (mods/ts3fixes/src/KitArmCore.cs, docs/314). Windows
// (.NET Framework csc).
//   node tools/selftest/kitarm/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'kitarm-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'KitArmTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'KitArmCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
