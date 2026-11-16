// Offline self-check of the eventcost table (mods/ts3fixes/src/EventCostCore.cs, docs/545 §5): calls, inclusive and
// exclusive time per event id across nested ProcessEvent calls.
// Windows (.NET Framework csc).
//   node tools/selftest/eventcost/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'eventcost-test.exe');
try {
  execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
    join(import.meta.dirname, 'EventCostTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'EventCostCore.cs')], { stdio: 'inherit' });
  execFileSync(exe, [], { stdio: 'inherit' });
} catch (e) {
  console.error(`eventcost self-check failed: ${(e as Error).message}`);
  process.exit(1);
}
