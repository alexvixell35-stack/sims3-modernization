// Offline self-check of h4core (mods/ts3fixes/src/H4Core.cs, docs/250 §7.0, §7.1, §7.4): gvattr source classes and scopes,
// the watchdog async check decisions and its stale-verdict rule. Windows (.NET Framework csc).
//   node tools/selftest/h4core/run.ts
import { execFileSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const CSC = 'C:/Windows/Microsoft.NET/Framework/v4.0.30319/csc.exe';
const SRC = join(ROOT, 'mods', 'ts3fixes', 'src');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'h4core-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'H4CoreTest.cs'), join(SRC, 'H4Core.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
