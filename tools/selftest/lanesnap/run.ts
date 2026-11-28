// Offline self-check of unseenlanesnap's decision (mods/ts3fixes/src/LaneSnapCore.cs). Windows (.NET Framework csc).
//   node tools/selftest/lanesnap/run.ts
import { execFileSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const CSC = 'C:/Windows/Microsoft.NET/Framework/v4.0.30319/csc.exe';
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'lanesnap-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'LaneSnapTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'LaneSnapCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
