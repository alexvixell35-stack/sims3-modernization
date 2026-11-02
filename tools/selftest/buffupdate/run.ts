// Offline self-check of the out-of-sync test of buffupdate (mods/ts3fixes/src/BuffUpdateCore.cs, issue #166 build 20): which exceptions
// from BuffManager.UpdateBuffs it catches.
// Windows (.NET Framework csc).
//   node tools/selftest/buffupdate/run.ts
import { execFileSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const CSC = 'C:/Windows/Microsoft.NET/Framework/v4.0.30319/csc.exe';
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'buffupdate-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'BuffUpdateTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'BuffUpdateCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
