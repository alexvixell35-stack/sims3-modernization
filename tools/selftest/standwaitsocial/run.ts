// Offline self-check of the game-independent rules of standwaitsocial
// (mods/ts3fixes/src/StandWaitSocialCore.cs, docs/686 §30). Windows (.NET Framework csc), or TS3_CSC on Linux.
//   node tools/selftest/standwaitsocial/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'standwaitsocial-test.exe');
const src = (name: string) => join(ROOT, 'mods', 'ts3fixes', 'src', name);
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'StandWaitSocialTest.cs'), src('StandWaitSocialCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
