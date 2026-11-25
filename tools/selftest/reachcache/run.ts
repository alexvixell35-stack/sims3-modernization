// Offline self-check of the game-independent rules of reachcache
// (mods/ts3fixes/src/ReachCacheCore.cs, docs/688). Windows (.NET Framework csc), or TS3_CSC on Linux.
//   node tools/selftest/reachcache/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'reachcache-test.exe');
const src = (name: string) => join(ROOT, 'mods', 'ts3fixes', 'src', name);
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'ReachCacheTest.cs'), src('ReachCacheCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
