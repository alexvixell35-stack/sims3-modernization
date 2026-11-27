// Offline self-check of simdescindex (docs/359): the description index (mods/ts3fixes/src/SimDescIndexCore.cs) against a
// model of the game's walk under random hooked and unhooked writers, the urn set, and the mod-writer scan's call
// targets (DescWriterScan.CallTargets / AliasLocals / FreshReceiver).
// Windows (.NET Framework csc).
//   node tools/selftest/simdescindex/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'simdescindex-test.exe');
const src = (f: string) => join(ROOT, 'mods', 'ts3fixes', 'src', f);
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'SimDescIndexTest.cs'), src('SimDescIndexCore.cs'), src('DescWriterScan.cs'), src('AutShadowCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
