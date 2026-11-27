// Offline self-check of the docs/325 instprobe summary math (mods/ts3fixes/src/InstProbeCore.cs): nearest-rank p50 / p95 /
// max, frame median, the part split (never below 0), phase naming and the summary line format. Windows (.NET Framework csc).
//   node tools/selftest/instprobe/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'instprobe-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'InstProbeTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'InstProbeCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
