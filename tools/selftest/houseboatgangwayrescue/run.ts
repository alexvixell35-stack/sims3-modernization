// Offline self-check of houseboatgangwayrescue (mods/ts3fixes/src/HouseboatGangwayRescueCore.cs, docs/857 §3): marking, the
// once-per-hour rescue rule, exemption, refusals that drop or keep the mark, and the bounded table.
// Windows (.NET Framework csc), or Linux with TS3_CSC (csc-shim).
//   node tools/selftest/houseboatgangwayrescue/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'houseboatgangwayrescue-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'HouseboatGangwayRescueTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'HouseboatGangwayRescueCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
