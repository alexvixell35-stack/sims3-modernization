// Offline self-check of deadrefsweep's holder helpers (mods/ts3fixes/src/DeadRefsCore.cs, issue #91): gone entries
// dropped from lists, arrays and dictionary keys, live ones kept in order, the load-time guard, and the pause gate
// (SimFrozenCore.cs, docs/205 §2.3b).
// Windows (.NET Framework csc).
//   node tools/selftest/deadrefs/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'deadrefs-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'DeadRefsTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'DeadRefsCore.cs'),
  join(ROOT, 'mods', 'ts3fixes', 'src', 'SimFrozenCore.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'AutShadowCore.cs'),
  join(ROOT, 'mods', 'ts3fixes', 'src', 'DescWriterScan.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
