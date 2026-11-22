// Offline self-check of the game-independent rules of linewaitlog, rhshare / rhsharehh and servingshare /
// servingsharehh (mods/ts3fixes/src/ObjShareCore.cs, docs/686 §14). Windows (.NET Framework csc), or TS3_CSC on Linux.
//   node tools/selftest/objshare/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'objshare-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'ObjShareTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'ObjShareCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
