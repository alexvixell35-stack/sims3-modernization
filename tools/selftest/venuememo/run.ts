// Offline self-check of the game-independent part of the venue memo (mods/ts3fixes/src/VenueMemoCore.cs, docs/122 §14.24).
// Windows (.NET Framework csc).
//   node tools/selftest/venuememo/run.ts
import { execFileSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const CSC = 'C:/Windows/Microsoft.NET/Framework/v4.0.30319/csc.exe';
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'venuememo-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'VenueMemoTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'VenueMemoCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
