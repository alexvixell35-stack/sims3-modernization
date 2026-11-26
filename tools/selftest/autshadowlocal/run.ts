// Offline self-check of the HouseholdToo local path pure core (mods/ts3fixes/src/AutShadowLocalCore.cs, docs/276 rank 5 piece 2):
// post gate, B9 source order, B11 commodity order, H8 local rule, dedupe set + pair cap. Windows (.NET Framework csc).
//   node tools/selftest/autshadowlocal/run.ts
import { execFileSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const CSC = 'C:/Windows/Microsoft.NET/Framework/v4.0.30319/csc.exe';
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'autshadowlocal-test.exe');
const src = join(ROOT, 'mods', 'ts3fixes', 'src');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'AutShadowLocalTest.cs'), join(src, 'AutShadowLocalCore.cs'), join(src, 'AutShadowCore.cs'), join(src, 'UnseenFallbackCore.cs'), join(src, 'NoStartCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
