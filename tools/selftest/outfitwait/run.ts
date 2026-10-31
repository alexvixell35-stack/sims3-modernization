// Offline self-check of outfitwaitpoll's sleep rule (mods/ts3fixes/src/OutfitWaitCore.cs, issue #115): a simulated
// SwitchOutfitHelper wait, off and on, ready at a random tick or never.
// Windows (.NET Framework csc).
//   node tools/selftest/outfitwait/run.ts
import { execFileSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const CSC = 'C:/Windows/Microsoft.NET/Framework/v4.0.30319/csc.exe';
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'outfitwait-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'OutfitWaitTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'OutfitWaitCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
