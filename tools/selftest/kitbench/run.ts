// Offline self-check of the kit benchmark runner (tools/player/ts3-kit/tools/TS3Bench.ps1, docs/315): its pure helpers, and an
// end-to-end mailbox round trip against a fake game built from the real ts3agent loader (Mailbox.cs, Dispatcher.cs, Json.cs),
// including the session_lock token rules. Windows (.NET Framework csc + Windows PowerShell 5.1, and pwsh 7 when installed).
//   node tools/selftest/kitbench/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { execFileSync as run } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'kitbench-fakegame.exe');
const loader = join(ROOT, 'mods', 'ts3agent', 'src', 'loader');
execFileSync(CSC, ['/nologo', '/platform:x86', '/langversion:5', `/out:${exe}`, join(import.meta.dirname, 'FakeGame.cs'),
  join(loader, 'Mailbox.cs'), join(loader, 'Dispatcher.cs'), join(loader, 'Json.cs')], { stdio: 'inherit' });
const ps1 = join(ROOT, 'tools', 'player', 'ts3-kit', 'tools', 'TS3Bench.ps1');
const hosts = ['powershell'];
try { run('pwsh', ['-NoProfile', '-Command', 'exit 0'], { stdio: 'ignore', timeout: 30000 }); hosts.push('pwsh'); } catch { /* PowerShell 7 not installed */ }
for (const h of hosts) {
  console.log(`-- ${h}`);
  run(h, ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ps1, '-SelfCheck', '-FakeTarget', exe], { stdio: 'inherit', timeout: 120000 });
}
