// Offline self-check of nearestlotlazy (mods/ts3fixes/src/NearestLotCore.cs, docs/677).
// Windows (.NET Framework csc), or Linux with TS3_CSC (csc-shim).
//   node tools/selftest/nearestlot/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'nearestlot-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'NearestLotTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'NearestLotCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
