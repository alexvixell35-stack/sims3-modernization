// Offline self-check of the FarHub lifecycle order, pause gate and frame budget (mods/ts3fixes/src/FarHubCore.cs, docs/351 §4). Windows (.NET Framework csc).
//   node tools/selftest/farhub/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const SRC = join(ROOT, 'mods', 'ts3fixes', 'src');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'farhub-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'FarHubTest.cs'), join(SRC, 'FarHubCore.cs'), join(SRC, 'FarHoldCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
