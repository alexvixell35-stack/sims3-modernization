// Offline self-check of failreplanboat's rule (mods/ts3fixes/src/FailReplanBoatCore.cs, docs/851 §8): which failed plans
// get the route-fail behaviour switched off, and that everything else is kept.
// Windows (.NET Framework csc), or Linux with TS3_CSC (csc-shim).
//   node tools/selftest/failreplanboat/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'failreplanboat-test.exe');
const src = join(ROOT, 'mods', 'ts3fixes', 'src');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'FailReplanBoatTest.cs'), join(src, 'FailReplanBoatCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
