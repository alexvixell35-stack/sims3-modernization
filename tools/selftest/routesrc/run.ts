// Offline self-check of the game-independent parts of the docs/151 §6 route sources (mods/ts3fixes/src/RouteSourcesCore.cs).
// Windows (.NET Framework csc).
//   node tools/selftest/routesrc/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'routesrc-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'RouteSrcTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'RouteSourcesCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
