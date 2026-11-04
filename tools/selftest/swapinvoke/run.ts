// Static check of mods/ts3fixes/src: no MethodInfo.Invoke with a target on a method ts3fixes swaps (Fixes.Patch).
// Mono's InternalInvoke checks the target against the swapped-in hook class and throws "TargetException: Unable to invoke
// an invalid target" (RefTable.cs; repros bp-eafixes-check prefdup and bp-deadrefs-check aging, 2026-10-30). Call such a
// method compiled, or through Delegate.CreateDelegate(type, target, method) (EaFixes.cs PrefRemoveFn).
// Matched by method name, whatever the lookup type: an interface MethodInfo (IAlarmOption.PerformAlarm) dispatches to the
// swapped class method too (OverwatchSpread.cs sOwPerform). Scope: MethodInfos looked up as `<expr>.GetMethod("M"` and
// invoked by the same variable name; Invoke(null, ...) passes.
//   node tools/selftest/swapinvoke/run.ts
import { readdirSync, readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';

export function findSwapInvokes(files: Record<string, string>): string[] {
  const all = Object.values(files).join('\n');
  const patched = new Set<string>();
  for (const m of all.matchAll(/Patch\(\s*[^,"()]+(?:\([^()]*\))?,\s*"(\w+)"/g)) patched.add(m[1]);
  const hits: string[] = [];
  for (const [name, text] of Object.entries(files)) {
    for (const m of text.matchAll(/(\w+)\s*=\s*(?:[^;=]|==)*?\.GetMethod\("(\w+)"/g)) {
      if (!patched.has(m[2])) continue;
      const call = new RegExp(`\\b${m[1]}\\.Invoke\\((?!\\s*null\\b)`, 'g');
      for (const c of text.matchAll(call))
        hits.push(`${name}:${text.slice(0, c.index).split('\n').length}: ${m[1]}.Invoke on swapped ${m[2]}`);
    }
  }
  return hits;
}

// fixture: swapped method invoked with a target (hit), through an interface lookup (hit), with null (ok), unswapped (ok)
const fx = findSwapInvokes({
  'A.cs': 'Patch(typeof(Svc), "Remove", typeof(H), "RemoveHook", "RemoveOrig");\nPatch(typeof(Svc), "Stat", typeof(H), "S", "SO");\n'
    + 'Patch(alarmBase, "Perform", Type.EmptyTypes, typeof(H), "PH", "PO");',
  'B.cs': 'MethodInfo r = typeof(Svc).GetMethod("Remove", f);\nr.Invoke(s, args);\nMethodInfo t = typeof(Svc).GetMethod("Stat");\n'
    + 't.Invoke(null, args);\nMethodInfo u = typeof(Svc).GetMethod("Other"); u.Invoke(s, null);\n'
    + 'sP = alarm == null ? null : alarm.GetMethod("Perform", Type.EmptyTypes);\nelse sP.Invoke(option, null);',
});
const ok = fx.length === 2 && fx[0].startsWith('B.cs:2:') && fx[1].startsWith('B.cs:7:');
console.assert(ok, 'fixture: ' + JSON.stringify(fx));
if (!ok) process.exit(1);

const SRC = resolve(import.meta.dirname, '..', '..', '..', 'mods', 'ts3fixes', 'src');
const files: Record<string, string> = {};
for (const f of readdirSync(SRC)) if (f.endsWith('.cs')) files[f] = readFileSync(join(SRC, f), 'utf8');
const hits = findSwapInvokes(files);
for (const h of hits) console.log('FAIL ' + h);
if (hits.length) process.exit(1);
console.log(`swapinvoke ok: ${Object.keys(files).length} files, no reflection call on a swapped method`);
