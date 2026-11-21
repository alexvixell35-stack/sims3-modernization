// Offline self-check of the game-independent parts of the EA maintenance spreading (mods/ts3fixes/src/EaSpreadCore.cs,
// docs/147): sliced passes hand out every item once, and the sliced daily relationship decay equals the one-tick
// reference day after day. Windows (.NET Framework csc).
//   node tools/selftest/easpread/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'easpread-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'EaSpreadTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'EaSpreadCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
