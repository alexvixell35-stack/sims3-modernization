// Offline self-check of the game-independent rules of termlenguard
// (mods/ts3fixes/src/TermLenGuardCore.cs, docs/692). Windows (.NET Framework csc), or TS3_CSC on Linux.
//   node tools/selftest/termlenguard/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'termlenguard-test.exe');
const src = (name: string) => join(ROOT, 'mods', 'ts3fixes', 'src', name);
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'TermLenGuardTest.cs'), src('TermLenGuardCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
