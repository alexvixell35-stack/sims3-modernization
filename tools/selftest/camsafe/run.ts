// Offline self-check of the game-independent rules of camsafe
// (mods/ts3fixes/src/CamSafeCore.cs, docs/690). Windows (.NET Framework csc), or TS3_CSC on Linux.
//   node tools/selftest/camsafe/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'camsafe-test.exe');
const src = (name: string) => join(ROOT, 'mods', 'ts3fixes', 'src', name);
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'CamSafeTest.cs'), src('CamSafeCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
