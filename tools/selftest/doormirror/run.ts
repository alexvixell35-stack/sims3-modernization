// Offline self-check of the game-independent parts of the P3 door mirror (mods/ts3fixes/src/DoorMirrorCore.cs, docs/105 §10.5).
// Windows (.NET Framework csc).
//   node tools/selftest/doormirror/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'doormirror-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'DoorMirrorTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'DoorMirrorCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
