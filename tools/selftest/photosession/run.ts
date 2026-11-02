// Offline self-check of the greeting-card photo session rule (mods/ts3fixes/src/PhotoSessionCore.cs, docs/508): which expected Sims
// a session drops (phantoms, toddlers and babies with their adult, never the last Sim in SimMadeItInside).
// Windows (.NET Framework csc).
//   node tools/selftest/photosession/run.ts
import { execFileSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const CSC = 'C:/Windows/Microsoft.NET/Framework/v4.0.30319/csc.exe';
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'photosession-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'PhotoSessionTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'PhotoSessionCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
