// Offline self-check of the game-independent rules of nextplan
// (mods/ts3fixes/src/DoorSeatCore.cs, docs/686 §38). Windows (.NET Framework csc), or TS3_CSC on Linux.
//   node tools/selftest/doorseat/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'doorseat-test.exe');
const src = (name: string) => join(ROOT, 'mods', 'ts3fixes', 'src', name);
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'DoorSeatTest.cs'), src('DoorSeatCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
