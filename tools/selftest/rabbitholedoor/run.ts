// Offline self-check of the rabbit hole door rule (mods/ts3fixes/src/RabbitHoleDoorCore.cs, docs/505): only a SACS request
// error is skipped, only with the switch on, and the door counts of EA's open/close pattern stay paired when every request
// fails. Windows (.NET Framework csc).
//   node tools/selftest/rabbitholedoor/run.ts
import { execFileSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const CSC = 'C:/Windows/Microsoft.NET/Framework/v4.0.30319/csc.exe';
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'rabbitholedoor-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'RabbitHoleDoorTest.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'RabbitHoleDoorCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
