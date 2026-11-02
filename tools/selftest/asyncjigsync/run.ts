// Offline self-check of asyncjigsync (mods/ts3fixes/src/AsyncJigSyncCore.cs, docs/240): fake interactions
// with jig fields. Windows (.NET Framework csc).
//   node tools/selftest/asyncjigsync/run.ts
import { execFileSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const CSC = 'C:/Windows/Microsoft.NET/Framework/v4.0.30319/csc.exe';
const SRC = join(ROOT, 'mods', 'ts3fixes', 'src');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'asyncjigsync-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'AsyncJigSyncTest.cs'), join(SRC, 'AsyncJigSyncCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
