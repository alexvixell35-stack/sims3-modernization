// Offline self-check of the game-independent rules of showertogether / showertogetherhh and showerpee / showerpeehh
// (mods/ts3fixes/src/ShowerShareCore.cs, docs/686 §15). Windows (.NET Framework csc), or TS3_CSC on Linux.
//   node tools/selftest/showershare/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'showershare-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'ShowerShareTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'ShowerShareCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
