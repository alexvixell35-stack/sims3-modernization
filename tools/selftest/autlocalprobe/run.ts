// Offline self-check of autt2l0's pure rules (mods/ts3fixes/src/AutLocalProbeCore.cs, tier-2 L0): actor class and B8 reason.
// Windows (.NET Framework csc).
//   node tools/selftest/autlocalprobe/run.ts
import { execFileSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const CSC = 'C:/Windows/Microsoft.NET/Framework/v4.0.30319/csc.exe';
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'autlocalprobe-test.exe');
const src = join(ROOT, 'mods', 'ts3fixes', 'src');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'AutLocalProbeTest.cs'), join(src, 'AutLocalProbeCore.cs'), join(src, 'UnseenFallbackCore.cs'), join(src, 'NoStartCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
