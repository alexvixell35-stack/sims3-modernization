// csc-shim: lets the offline C# self-checks (tools/selftest/*/run.ts and friends) run on Linux. They were written for
// Windows: compile with the .NET Framework csc, then run the .exe. On Windows (or with no TS3_CSC set) this is plain
// child_process.execFileSync. With TS3_CSC set on a non-Windows host:
//   - a call to the Windows csc goes to TS3_CSC (Roslyn), with the .NET runtime's reference assemblies added and
//     /platform:x86 dropped;
//   - running an .exe that this shim compiled goes through `dotnet <exe>` with a generated runtimeconfig.json.
// Usage in a runner: import { execFileSync, CSC } from '<rel>/selftest/csc-shim.ts';
import { execFileSync as nodeExec, type ExecFileSyncOptions } from 'node:child_process';
import { existsSync, readdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { homedir } from 'node:os';

export const CSC = 'C:/Windows/Microsoft.NET/Framework/v4.0.30319/csc.exe';
const LINUX = process.platform !== 'win32' && !!process.env.TS3_CSC;
const DOTNET = process.env.DOTNET_ROOT ?? join(homedir(), '.dotnet');
const built = new Set<string>();

function newest(dir: string): string | null {
  if (!existsSync(dir)) return null;
  const v = readdirSync(dir).sort((a, b) => a.localeCompare(b, undefined, { numeric: true }));
  return v.length ? v[v.length - 1] : null;
}

function coreRefs(): { refs: string[]; version: string } {
  const pack = join(DOTNET, 'packs', 'Microsoft.NETCore.App.Ref');
  const ver = newest(pack);
  if (!ver) throw new Error('csc-shim: no Microsoft.NETCore.App.Ref pack under ' + DOTNET);
  const tfmDir = join(pack, ver, 'ref');
  const tfm = newest(tfmDir)!;
  const dir = join(tfmDir, tfm);
  const run = newest(join(DOTNET, 'shared', 'Microsoft.NETCore.App')) ?? ver;
  return { refs: readdirSync(dir).filter((f) => f.endsWith('.dll')).map((f) => '/reference:' + join(dir, f)), version: run };
}

export function execFileSync(file: string, args: readonly string[] = [], opts?: ExecFileSyncOptions): any {
  if (!LINUX) return nodeExec(file, args, opts);
  if (file === CSC) {
    const { refs, version } = coreRefs();
    const a = args.filter((x) => !/^\/platform:/i.test(x));
    const out = a.find((x) => /^\/out:/i.test(x))?.slice(5);
    const r = nodeExec(process.env.TS3_CSC!, ['/nostdlib+', '/noconfig', ...refs, ...a], opts);
    if (out) {
      built.add(out);
      writeFileSync(out.replace(/\.(exe|dll)$/i, '.runtimeconfig.json'),
        JSON.stringify({ runtimeOptions: { tfm: 'net' + version.split('.').slice(0, 2).join('.'), framework: { name: 'Microsoft.NETCore.App', version } } }));
    }
    return r;
  }
  if (built.has(file)) return nodeExec(join(DOTNET, 'dotnet'), [file, ...args], opts);
  return nodeExec(file, args, opts);
}
