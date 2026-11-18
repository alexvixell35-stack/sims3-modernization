// Offline self-check of the envlistmemoexact stand-down rule (mods/ts3fixes/src/EnvListExactCore.cs, docs/274 §11.8).
// Windows (.NET Framework csc).
//   node tools/selftest/envlistexact/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'envlistexact-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'EnvListExactTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'EnvListExactCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
