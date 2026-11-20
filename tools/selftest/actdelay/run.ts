// Offline self-check of actdelay (mods/ts3fixes/src/ActDelayCore.cs, docs/684).
// Windows (.NET Framework csc), or Linux with TS3_CSC (csc-shim).
//   node tools/selftest/actdelay/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'actdelay-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'ActDelayTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'ActDelayCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
