// Offline self-check of the lane A managed consumers (mods/ts3fixes/src/LaneACore.cs, docs/321 §10): lot drops, world
// pruning, high water, and 50 load / quit cycles with lot churn staying flat. Windows (.NET Framework csc).
//   node tools/selftest/lanea/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const SRC = join(ROOT, 'mods', 'ts3fixes', 'src');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'lanea-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'LaneATest.cs'), join(SRC, 'LaneACore.cs'), join(SRC, 'LotHubCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
