// Builds out/d3d9.dll (32-bit proxy, see d3d9on12.cpp). --self-check: source and .def agree on the exports (TS3W.exe imports
// only Direct3DCreate9; Ex kept for completeness), and a built dll, when present, exports them.
import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import assert from 'node:assert/strict';

const DIR = import.meta.dirname, OUT = join(DIR, 'out');
const EXPORTS = ['Direct3DCreate9', 'Direct3DCreate9Ex'];

function selfCheck() {
  const def = readFileSync(join(DIR, 'd3d9on12.def'), 'utf8'), cpp = readFileSync(join(DIR, 'd3d9on12.cpp'), 'utf8');
  for (const e of EXPORTS) assert.ok(new RegExp(`^\\s+${e}\\s*$`, 'm').test(def), `def lacks ${e}`);
  for (const k of ['Direct3DCreate9On12', 'Direct3DCreate9On12Ex', 'Enable9On12 = TRUE', 'FELL BACK', 'path=9On12', 'GetSystemDirectoryA']) assert.ok(cpp.includes(k), `cpp lacks ${k}`);
  const dll = join(OUT, 'd3d9.dll');
  if (existsSync(dll)) { const s = readFileSync(dll).toString('latin1'); for (const e of EXPORTS) assert.ok(s.includes(`${e}\0`), `built dll lacks export ${e}`); }
  console.log(`d3d9on12-test self-check OK (${existsSync(dll) ? 'dll checked' : 'no built dll, source only'})`);
}

if (process.argv.includes('--self-check')) selfCheck();
else {
  mkdirSync(OUT, { recursive: true });
  const vswhere = join(process.env['ProgramFiles(x86)'] ?? 'C:\\Program Files (x86)', 'Microsoft Visual Studio', 'Installer', 'vswhere.exe');
  const vs = execFileSync(vswhere, ['-latest', '-products', '*', '-requires', 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64', '-property', 'installationPath'], { encoding: 'utf8' }).trim();
  assert.ok(vs, 'Visual Studio with the C++ tools not found');
  const cmd = `call "${vs}\\VC\\Auxiliary\\Build\\vcvars32.bat" >nul && cl /nologo /LD /O2 /MT /W4 /WX /Fo"${OUT}\\\\" /Fe"${OUT}\\d3d9.dll" "${join(DIR, 'd3d9on12.cpp')}" /link /DEF:"${join(DIR, 'd3d9on12.def')}" kernel32.lib`;
  execFileSync('cmd.exe', ['/d', '/s', '/c', `"${cmd}"`], { stdio: 'inherit', windowsVerbatimArguments: true });
  selfCheck();
}
