// Offline self-check of the far-object lot machine, FarLight part (mods/ts3fixes/src/FarObjModel.cs, docs/350 §4-§5, docs/358). Windows (.NET Framework csc).
//   node tools/selftest/farobj/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const SRC = join(ROOT, 'mods', 'ts3fixes', 'src');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'farobj-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'FarObjTest.cs'), join(SRC, 'FarObjModel.cs'), join(SRC, 'FarHoldCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
