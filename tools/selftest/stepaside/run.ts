// Offline self-check of JobYield step-aside loop (mods/ts3fixes/src/StepAsideCore.cs, docs/183 §7): scripted JOBBUSY answers,
// cap, reset. Windows (.NET Framework csc).
//   node tools/selftest/stepaside/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const SRC = join(ROOT, 'mods', 'ts3fixes', 'src');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'stepaside-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'StepAsideTest.cs'), join(SRC, 'StepAsideCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
