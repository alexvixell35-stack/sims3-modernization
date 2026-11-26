// Offline self-check of boatroamcap (mods/ts3fixes/src/BoatRoamCapCore.cs, docs/853 §4): streak, night and motive caps,
// streak resets, back-off after a failed return and the bounded table.
// Windows (.NET Framework csc), or Linux with TS3_CSC (csc-shim).
//   node tools/selftest/boatroamcap/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'boatroamcap-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'BoatRoamCapTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'BoatRoamCapCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
