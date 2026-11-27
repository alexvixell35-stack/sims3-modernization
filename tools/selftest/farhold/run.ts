// Offline self-check of the FarHold owner table and Hook A decision (mods/ts3fixes/src/FarHoldCore.cs, docs/346 §3, docs/351 §2). Windows (.NET Framework csc).
//   node tools/selftest/farhold/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const SRC = join(ROOT, 'mods', 'ts3fixes', 'src');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'farhold-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'FarHoldTest.cs'), join(SRC, 'FarHoldCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
