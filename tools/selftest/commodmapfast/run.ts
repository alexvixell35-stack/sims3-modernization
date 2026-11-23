// Offline self-check of commodmapfast (mods/ts3fixes/src/CommodMapFastCore.cs, docs/313): random commodity maps,
// EA's one-Remove-per-pair rule vs the one-pass plan. Windows (.NET Framework csc).
//   node tools/selftest/commodmapfast/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const SRC = join(ROOT, 'mods', 'ts3fixes', 'src');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'commodmapfast-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'CommodMapFastTest.cs'), join(SRC, 'CommodMapFastCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
