// The pure rules of the stove-wall-repeat run (mods/ts3agent/src/plugin/StoveRepeatCore.cs, the code the game runs), offline.
//   node tools/harness/stove-repeat-core.ts --self-check   fixtures from the 2026-11-07 runs (tools/selftest/stoverepeat/StoveRepeatTest.cs)
import { execFileSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..');
const CSC = 'C:/Windows/Microsoft.NET/Framework/v4.0.30319/csc.exe';
if (process.argv[2] !== '--self-check') { console.error('usage: stove-repeat-core.ts --self-check'); process.exit(2); }
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'stoverepeat-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(ROOT, 'tools', 'selftest', 'stoverepeat', 'StoveRepeatTest.cs'), join(ROOT, 'mods', 'ts3agent', 'src', 'plugin', 'StoveRepeatCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
