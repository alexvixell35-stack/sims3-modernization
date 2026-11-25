// Offline self-check of petstartleskip (mods/ts3fixes/src/PetStartleSkipCore.cs, docs/656 §8.1): recording,
// forced startles, exemption, expiry and the bounded table.
// Windows (.NET Framework csc), or Linux with TS3_CSC (csc-shim).
//   node tools/selftest/petstartleskip/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'petstartleskip-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'PetStartleSkipTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'PetStartleSkipCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
