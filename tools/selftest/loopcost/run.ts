// Offline self-check of docs/330 (our own loop cost): mods/ts3fixes/src/LoopCostCore.cs (deadrefsweep group cadence,
// viewer-table threshold, held-walk cadence) and SlicedPass.RunSliceEvery (EaSpreadCore.cs), with the staleness bounds.
// Windows (.NET Framework csc).
//   node tools/selftest/loopcost/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'loopcost-test.exe');
const src = join(ROOT, 'mods', 'ts3fixes', 'src');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'LoopCostTest.cs'), join(src, 'LoopCostCore.cs'), join(src, 'EaSpreadCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
