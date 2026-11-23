// Offline self-check of the game-independent parts of the ErrorTrap load-up check spreading (mods/ts3fixes/src/
// ErrorTrapSpreadCore.cs, docs/146): upstream marker detection without the static constructor, the yield rule, slices.
// Windows (.NET Framework csc).
//   node tools/selftest/errortrapspread/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const SRC = join(ROOT, 'mods', 'ts3fixes', 'src');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'errortrapspread-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'ErrorTrapSpreadTest.cs'), join(SRC, 'ErrorTrapSpreadCore.cs'), join(SRC, 'OverwatchSpreadCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
