// Offline self-check of the boatgetoutdiag watch table (mods/ts3fixes/src/BoatGetOutDiagCore.cs, docs/257 §7): push/pop,
// the own task's normal end, foreign and nested writers, overflow. Windows (.NET Framework csc).
//   node tools/selftest/boatgetoutdiag/run.ts
import { execFileSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const CSC = 'C:/Windows/Microsoft.NET/Framework/v4.0.30319/csc.exe';
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'boatgetoutdiag-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'BoatGetOutDiagTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'BoatGetOutDiagCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
