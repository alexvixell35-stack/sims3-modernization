// Offline self-check of the free vacation fix decisions (mods/ts3fixes/src/EaVacationCore.cs, issue #97): which stored
// adults come back, stale opportunity id refresh, tick-0 expiry, load repair of stuck trips.
// Windows (.NET Framework csc).
//   node tools/selftest/vacation/run.ts
import { execFileSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const CSC = 'C:/Windows/Microsoft.NET/Framework/v4.0.30319/csc.exe';
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'vacation-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'VacationTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'EaVacationCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
