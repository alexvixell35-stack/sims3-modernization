// Offline self-check of the game-independent rules of the docs/686 §7 privacy-lock rule (mods/ts3fixes/src/StandWaitLockedCore.cs).
// Windows (.NET Framework csc), or TS3_CSC on Linux.
//   node tools/selftest/standwaitlocked/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'standwaitlocked-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'StandWaitLockedTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'StandWaitLockedCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
