// Offline self-check of the decisions of unseentraffic + npccarexit (mods/ts3fixes/src/UnseenTrafficCore.cs): corridor
// samples, teleport-leg hold time, the drive-away call shape, exit memo keys.
// Windows (.NET Framework csc).
//   node tools/selftest/unseentraffic/run.ts
import { execFileSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const CSC = 'C:/Windows/Microsoft.NET/Framework/v4.0.30319/csc.exe';
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'unseentraffic-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'UnseenTrafficTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'UnseenTrafficCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
