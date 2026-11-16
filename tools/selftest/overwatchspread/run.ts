// Offline self-check of the game-independent parts of the Overwatch cleanup spreading (mods/ts3fixes/src/OverwatchSpreadCore.cs,
// docs/145): upstream marker detection and the yield path, slots, slices. Windows (.NET Framework csc).
//   node tools/selftest/overwatchspread/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'overwatchspread-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'OverwatchSpreadTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'OverwatchSpreadCore.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'EaSpreadCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
