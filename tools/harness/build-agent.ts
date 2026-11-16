// Compiles mods/ts3agent against the game's own DLLs: the loader (src/loader, stable) and the plugin
// (src/plugin, all commands, hot-reloadable), and packs both into build/ts3agent.package.
//   node tools/harness/build-agent.ts          full package (needs a game restart to take effect)
//   node tools/harness/build-agent.ts --hot    plugin only, uniquely named, for tools/harness/reload-agent.ts
// Windows-only by nature: it needs the .NET Framework C# compiler that ships with Windows (csc.exe, C# 5).
import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { TYPE_S3SA, fnv64, s3saEncode, writePackage } from '../dbpf.ts';

const ROOT = resolve(import.meta.dirname, '..', '..');
const CSC = process.env.TS3_CSC ?? 'C:\\Windows\\Microsoft.NET\\Framework\\v4.0.30319\\csc.exe'; // TS3_CSC: another C# compiler, e.g. Roslyn on Linux
const REF_DIR = process.env.TS3_REF_DIR ?? join(ROOT, 'game-copy', 'assemblies'); // from tools/extract-assemblies.ts
const REFS = ['mscorlib', 'System', 'System.Xml', 'ScriptCore', 'SimIFace', 'Sims3Metadata',
              'Sims3GameplaySystems', 'Sims3GameplayObjects', 'Sims3StoreObjects', 'UI'];
const ASSEMBLY = 'Ts3Agent';
const INSTANTIATOR = 'Ts3Agent.Instantiator';
const TYPE_XML = 0x0333406c;
// Tuning XML for the instantiator class: loading it runs the static constructor that starts the agent.
const TUNING_XML = `<?xml version="1.0" encoding="utf-8"?>
<base>
  <Current_Tuning>
    <kInstantiator value="True" />
  </Current_Tuning>
</base>
`;

function sources(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap(e =>
    e.isDirectory() ? sources(join(dir, e.name)) : e.name.endsWith('.cs') ? [join(dir, e.name)] : []);
}

const missing = REFS.filter(r => !existsSync(join(REF_DIR, `${r}.dll`)));
if (missing.length) {
  console.error(`missing game DLLs in ${REF_DIR}: ${missing.join(', ')}. Run tools/extract-assemblies.ts first.`);
  process.exit(1);
}
const outDir = process.env.TS3_BUILD_DIR ?? join(ROOT, 'build');
mkdirSync(outDir, { recursive: true });
const src = join(ROOT, 'mods', 'ts3agent', 'src');
const refs = REFS.map(r => `/reference:${join(REF_DIR, `${r}.dll`)}`);
function csc(out: string, files: string[], extra: string[] = []) {
  execFileSync(CSC, ['/nologo', '/nostdlib+', '/noconfig', '/target:library', '/optimize+', '/langversion:5', '/warn:4',
    '/nowarn:414,169,649', `/out:${out}`, ...refs, ...extra, ...files], { stdio: 'inherit' });
}
const loaderDll = join(outDir, `${ASSEMBLY}.dll`);
const hot = process.argv.includes('--hot');
if (!hot) csc(loaderDll, sources(join(src, 'loader')));
// A hot plugin needs a new assembly name each time, or the runtime may hand back the copy it already loaded.
const pluginName = hot ? `${ASSEMBLY}Plugin_${Date.now().toString(36)}` : `${ASSEMBLY}Plugin`;
const pluginDll = join(outDir, `${pluginName}.dll`);
csc(pluginDll, sources(join(src, 'plugin')), [`/reference:${loaderDll}`]);
if (hot) {
  writeFileSync(join(outDir, 'hot-plugin.txt'), pluginDll);
  console.log(`built hot plugin ${pluginDll}`);
} else {
  const pkg = writePackage([
    { type: TYPE_S3SA, group: 0, instance: fnv64(ASSEMBLY), data: s3saEncode(readFileSync(loaderDll)) },
    { type: TYPE_S3SA, group: 0, instance: fnv64(`${ASSEMBLY}Plugin`), data: s3saEncode(readFileSync(pluginDll)) },
    { type: TYPE_XML, group: 0, instance: fnv64(INSTANTIATOR), data: Buffer.from(TUNING_XML, 'utf8') },
  ]);
  const out = join(outDir, 'ts3agent.package');
  writeFileSync(out, pkg);
  console.log(`built ${out} (${pkg.length} bytes)`);
}
