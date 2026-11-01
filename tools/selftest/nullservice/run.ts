// Offline self-check of the null-service drop (mods/ts3fixes/src/NullServiceCore.cs, docs/504): every null element goes, the
// rest keep their order, and a list without a null is left alone (same list version, so an enumerator over it survives).
// Windows (.NET Framework csc).
//   node tools/selftest/nullservice/run.ts
import { execFileSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const CSC = 'C:/Windows/Microsoft.NET/Framework/v4.0.30319/csc.exe';
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'nullservice-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'NullServiceTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'NullServiceCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
