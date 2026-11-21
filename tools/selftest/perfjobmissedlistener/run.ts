// Offline self-check of perfjobmissedlistener (mods/ts3fixes/src/PerfJobMissedCore.cs, docs/654 §1): a
// PerformanceCareer.OnJobMissed listener is removed only when its career is no longer its owner's occupation or the owner
// is gone; a current career always keeps it; with the switch off a stale listener is only counted.
// Windows (.NET Framework csc), or Linux with TS3_CSC (csc-shim).
//   node tools/selftest/perfjobmissedlistener/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'perfjobmissedlistener-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'PerfJobMissedTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'PerfJobMissedCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
