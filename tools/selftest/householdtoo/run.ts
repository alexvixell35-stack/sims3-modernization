// Offline self-check of docs/271 [Experimental] HouseholdToo (mods/ts3fixes/src/HouseholdTooCore.cs with AutLiveCore and
// UnseenFallbackCore): flag off = no behaviour change, flag on = only live household-scope Sims change. Windows (.NET Framework csc).
//   node tools/selftest/householdtoo/run.ts
import { execFileSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const CSC = 'C:/Windows/Microsoft.NET/Framework/v4.0.30319/csc.exe';
const src = join(ROOT, 'mods', 'ts3fixes', 'src');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'householdtoo-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`, join(import.meta.dirname, 'HouseholdTooTest.cs'),
  join(src, 'HouseholdTooCore.cs'), join(src, 'AutLiveCore.cs'), join(src, 'UnseenFallbackCore.cs'), join(src, 'NoStartCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
