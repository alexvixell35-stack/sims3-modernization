// Offline self-check of the strand lines' rules (mods/ts3fixes/src/StrandLogCore.cs, docs/183 §8): which failed plans get
// lines, how often, the privacy lock rule, the stair filter. Windows (.NET Framework csc).
//   node tools/selftest/strandlog/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const SRC = join(ROOT, 'mods', 'ts3fixes', 'src');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'strandlog-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'StrandLogTest.cs'), join(SRC, 'StrandLogCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
