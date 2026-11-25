// Offline self-check of preplancb (mods/ts3fixes/src/PrePlanCbCore.cs, docs/185 §5): a fake route with EA's
// Route.Plan order and a fake async job; the callback records call order against START. Windows (.NET Framework csc).
//   node tools/selftest/preplancb/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const SRC = join(ROOT, 'mods', 'ts3fixes', 'src');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'preplancb-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'PrePlanCbTest.cs'), join(SRC, 'PrePlanCbCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
