// Offline self-check of what the focus repros throw on (mods/ts3agent/src/plugin/FocusRules.cs, docs/222 §11.2).
// Windows (.NET Framework csc).
//   node tools/selftest/focusrules/run.ts
import { execFileSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const CSC = 'C:/Windows/Microsoft.NET/Framework/v4.0.30319/csc.exe';
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'focusrules-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'FocusRulesTest.cs'), join(ROOT, 'mods', 'ts3agent', 'src', 'plugin', 'FocusRules.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
