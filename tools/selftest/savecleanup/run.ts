// Offline self-check of the aggressive save cleanup rules (mods/ts3fixes/src/SaveCleanupCore.cs, docs/14 §8): the dead
// outfit categories match the game's enum values and never include what a grave, ghost or resurrection uses, and the
// quiet-days rule. Windows (.NET Framework csc).
//   node tools/selftest/savecleanup/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'savecleanup-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'SaveCleanupTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'SaveCleanupCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
