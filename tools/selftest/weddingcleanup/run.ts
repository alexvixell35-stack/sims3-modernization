// Offline self-check of the decisions of weddingcleanup (mods/ts3fixes/src/WeddingCleanupCore.cs): where the guard acts
// before EA's WeddingCeremony.CleanUp body (only where EA would throw) and where EA's body runs unchanged.
// Windows (.NET Framework csc).
//   node tools/selftest/weddingcleanup/run.ts
import { execFileSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const CSC = 'C:/Windows/Microsoft.NET/Framework/v4.0.30319/csc.exe';
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'weddingcleanup-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'WeddingCleanupTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'WeddingCleanupCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
