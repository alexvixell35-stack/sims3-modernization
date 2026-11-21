// Offline self-check of the save serializer type cache (mods/ts3fixes/src/TypeCache.cs) against a model of the game's
// per-Type lookups. Windows (.NET Framework csc).
//   node tools/selftest/typecache/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'typecache-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', `/out:${exe}`,
  join(import.meta.dirname, 'TypeCacheTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'TypeCache.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
