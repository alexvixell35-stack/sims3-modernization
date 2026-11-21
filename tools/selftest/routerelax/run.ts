// Offline self-check of fglnonescan, fglnnearspot and followerlasttry (mods/ts3fixes/src/RouteRelaxCore.cs, docs/675).
// Windows (.NET Framework csc), or Linux with TS3_CSC (csc-shim).
//   node tools/selftest/routerelax/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'routerelax-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'RouteRelaxTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'RouteRelaxCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
