// Offline self-check of the game-independent rules of fridgeshare
// (mods/ts3fixes/src/FridgeShareCore.cs, docs/686 §41). Windows (.NET Framework csc), or TS3_CSC on Linux.
//   node tools/selftest/fridgeshare/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'fridgeshare-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'FridgeShareTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'FridgeShareCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
