// Offline self-check of the game-independent rules of makebedboth / makebedbothhh
// (mods/ts3fixes/src/MakeBedBothCore.cs, docs/686 §13). Windows (.NET Framework csc), or TS3_CSC on Linux.
//   node tools/selftest/makebedboth/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'makebedboth-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'MakeBedBothTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'MakeBedBothCore.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'BlockedUseCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
