// Offline self-check of the conversation turn decision (mods/ts3fixes/src/ConvTurnCore.cs, issue #166 build 19): when a local Sim
// takes a turn that its holder cannot or does not take.
// Windows (.NET Framework csc).
//   node tools/selftest/convturn/run.ts
import { execFileSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const CSC = 'C:/Windows/Microsoft.NET/Framework/v4.0.30319/csc.exe';
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'convturn-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'ConvTurnTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'ConvTurnCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
