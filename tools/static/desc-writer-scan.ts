// Offline deadrefdescrev writer scan (docs/336 §11): extracts the script assemblies of the given packages (and/or takes
// .dll files), runs mods/ts3fixes/src/DescWriterScan.cs over them on .NET Framework, prints per assembly the kinds it makes
// untrusted, its writer methods and the scan time. Windows (.NET Framework csc), needs game-copy/assemblies.
//   node tools/static/desc-writer-scan.ts <file.package|file.dll>...
import { execFileSync } from 'node:child_process';
import { mkdirSync, readdirSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { CSC } from '../selftest/csc-shim.ts';

const ROOT = resolve(import.meta.dirname, '..', '..');
const inputs = process.argv.slice(2);
if (inputs.length === 0) { console.error('usage: node tools/static/desc-writer-scan.ts <file.package|file.dll>...'); process.exit(2); }
const out = join(ROOT, 'build', 'desc-writer-scan');
const dlls = join(out, 'dlls');
mkdirSync(dlls, { recursive: true });
const pkgs = inputs.filter(f => f.endsWith('.package'));
if (pkgs.length) execFileSync(process.execPath, [join(ROOT, 'tools', 'extract-assemblies.ts'), dlls, ...pkgs], { stdio: ['ignore', 'ignore', 'inherit'] });
const files = [...inputs.filter(f => f.endsWith('.dll')), ...(pkgs.length ? readdirSync(dlls).filter(f => f.endsWith('.dll')).map(f => join(dlls, f)) : [])];
const exe = join(out, 'desc-writer-scan.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`,
  join(import.meta.dirname, 'desc-writer-scan', 'Scan.cs'), join(ROOT, 'mods', 'ts3fixes', 'src', 'DescWriterScan.cs'),
  join(ROOT, 'mods', 'ts3fixes', 'src', 'AutShadowCore.cs')], { stdio: 'inherit' });
execFileSync(exe, [join(ROOT, 'game-copy', 'assemblies'), ...files], { stdio: 'inherit' });
