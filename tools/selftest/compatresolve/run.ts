// Offline self-check of the docs/328 §7 runtime precedence table (mods/ts3fixes/src/CompatResolveCore.cs).
// Windows (.NET Framework csc).
//   node tools/selftest/compatresolve/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'compatresolve-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'CompatResolveTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'CompatResolveCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
