// Offline self-check of the Compaction rules (mods/ts3fixes/src/CompactionCore.cs, docs/204): scope, the offer's time
// estimate, nearest-object match, the native bit answers and the texts. Windows (.NET Framework csc).
//   node tools/selftest/compaction/run.ts
import { execFileSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const CSC = 'C:/Windows/Microsoft.NET/Framework/v4.0.30319/csc.exe';
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'compaction-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'CompactionTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'CompactionCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
