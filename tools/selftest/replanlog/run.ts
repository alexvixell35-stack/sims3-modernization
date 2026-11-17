// Offline self-check of mods/ts3fixes/src/ReplanLogCore.cs (replanlog, docs/261 §6 L1). Windows (.NET Framework csc).
//   node tools/selftest/replanlog/run.ts
import { execFileSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const CSC = 'C:/Windows/Microsoft.NET/Framework/v4.0.30319/csc.exe';
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'replanlog-test.exe');
const src = (f: string) => join(ROOT, 'mods', 'ts3fixes', 'src', f);
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'ReplanLogTest.cs'), src('ReplanLogCore.cs'), src('UnseenFallbackCore.cs'), src('NoStartCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
