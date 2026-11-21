// Static check of every Delegate.CreateDelegate site in mods/ts3fixes/src and mods/ts3agent*/src. The game's Mono binds a
// delegate made from a MethodInfo to exactly that method, with no virtual dispatch: a base virtual MethodInfo runs the base
// body on a subclass target, and an abstract one (no body) crashes the interpreter (UnseenService88 RunOrderBehavior,
// C0000005 at 0xE6463F, Bridgeport 2026-11-20). A site passes when its MethodInfo argument goes through
// DelegateTarget(target, mi) (mods/ts3fixes/src/DelegateTarget.cs), or when it is on ALLOW below with the reason its
// method is safe (static, non-virtual, final or on a sealed type, looked up on the target's own runtime type, or a
// deliberate non-virtual base call). Also fails when a MethodInfo looked up as typeof(X).GetMethod("M") for an M in
// ABSTRACT_OR_OVERRIDDEN reaches CreateDelegate without DelegateTarget, whatever ALLOW says. A new site: check the game
// method's flags (tools/managed-callgraph.ts loadStandalone, method flags 0x40 virtual / 0x400 abstract / 0x20 final, and
// whether any game subclass declares the same name), then use DelegateTarget or add it to ALLOW with the reason.
// Keys: "<file>|<MethodInfo argument as written>"; one key covers every site in that file with that argument.
//   node tools/selftest/delegatesite/run.ts            check
//   node tools/selftest/delegatesite/run.ts --list     print every site key (to fill ALLOW)
import { readdirSync, readFileSync } from 'node:fs';
import { join, relative, resolve } from 'node:path';

// Game methods that are abstract, or virtual with a game override, where a site was found or fixed (2026-11-20 audit).
const ABSTRACT_OR_OVERRIDDEN = ['RunOrderBehavior', 'SetBroadcaster', 'CanSimSkipExitWait'];

const NV = 'non-virtual game method (2026-11-20 audit)';
const ST = 'static method, no target';
const OWN = 'looked up on the target\'s own runtime type: GetMethod returns the most-derived declaration';
const SEALED = 'method on a sealed type or final';
const ALLOW: Record<string, string> = {
  'mods/ts3fixes/src/AutOffload.cs|mDoSimEnv': NV, 'mods/ts3fixes/src/AutOffload.cs|mSendObjEnv': NV,
  'mods/ts3fixes/src/BakeSale.cs|sBstHandler': NV,
  'mods/ts3fixes/src/Batch582fLogic.cs|m': 'NpcParty child OnSimLotChanged, looked up on the hooked class itself (self is that class)',
  'mods/ts3fixes/src/Batch582gLogic.cs|sTpToAncient': NV, 'mods/ts3fixes/src/Batch582gLogic.cs|sLbCallback': ST,
  'mods/ts3fixes/src/Batch582hLogic.cs|sBtLoc': ST,
  'mods/ts3fixes/src/Batch589Logic.cs|bt': ST, 'mods/ts3fixes/src/Batch589Logic.cs|bf': ST,
  'mods/ts3fixes/src/Batch589Logic.cs|sCsHonk': SEALED + ' (DoCarpoolPickupAndDropoff), non-virtual',
  'mods/ts3fixes/src/Batch589Logic.cs|sCsSkip': SEALED + ' (DoCarpoolPickupAndDropoff), non-virtual',
  'mods/ts3fixes/src/Batch589Logic.cs|sCsLoop': SEALED + ' (DoCarpoolPickupAndDropoff), non-virtual',
  'mods/ts3fixes/src/Batch589bLogic.cs|sSuReset': 'Stereo.ResetBroadcaster: ' + NV,
  'mods/ts3fixes/src/Batch589dLogic.cs|sDgShould': NV, 'mods/ts3fixes/src/Batch589dLogic.cs|pr': ST,
  'mods/ts3fixes/src/Batch591Logic.cs|sLdSelf': NV, 'mods/ts3fixes/src/Batch591Logic.cs|now ? sMaInstant : sMaStage': NV,
  'mods/ts3fixes/src/CarNoRoute.cs|mi': NV,
  'mods/ts3fixes/src/CasExitSweep.cs|sCesBaseDispose': 'WindowBase.Dispose(bool), non-virtual: a deliberate base call (DeclaringType checked)',
  'mods/ts3fixes/src/CasRowLeak.cs|sCrlBaseDispose': 'WindowBase.Dispose(bool), non-virtual: a deliberate base call (DeclaringType checked)',
  'mods/ts3fixes/src/CompositorLeak.cs|sCmlBaseDispose': 'WindowBase.Dispose(bool), non-virtual: a deliberate base call (DeclaringType checked)',
  'mods/ts3fixes/src/CasPuckLeak.cs|sCplDashHandler': NV, 'mods/ts3fixes/src/CasPuckLeak.cs|sCplOptHandler': NV,
  'mods/ts3fixes/src/ComboChecks.cs|start': NV, 'mods/ts3fixes/src/ComboChecks.cs|end': NV, 'mods/ts3fixes/src/ComboChecks.cs|m': NV,
  'mods/ts3fixes/src/CookClickListener.cs|sCcOnDisposed': NV, 'mods/ts3fixes/src/CookClickListener.cs|sCcClear': NV,
  'mods/ts3fixes/src/DanceFloorDiag.cs|sDfNear': ST, 'mods/ts3fixes/src/DanceFloorDiag.cs|sDfRoute': NV,
  'mods/ts3fixes/src/DeadRefs.cs|sCancelAging': NV, 'mods/ts3fixes/src/DeadRefs.cs|sCpAddListeners': NV, 'mods/ts3fixes/src/DeadRefs.cs|valid': NV,
  'mods/ts3fixes/src/DetailPrio603.cs|sDpCalc': NV,
  'mods/ts3fixes/src/EaFixes.cs|remove': 'Service.RemovePreferredSim: ' + NV,
  'mods/ts3fixes/src/EaSpread.cs|sChangeSeason': NV, 'mods/ts3fixes/src/EggSpot.cs|valid': NV,
  'mods/ts3fixes/src/EpBatch1bLogic.cs|sCtValidSim': ST, 'mods/ts3fixes/src/EpBatch1bLogic.cs|sCtValidPet': ST,
  'mods/ts3fixes/src/EpBatch4Logic.cs|sFrTest': NV, 'mods/ts3fixes/src/EpBatch4Logic.cs|sFrSound': SEALED + ' (Reconstruct), non-virtual',
  'mods/ts3fixes/src/EpBatch4Logic.cs|sLbDone': NV, 'mods/ts3fixes/src/EpBatch4Logic.cs|sLbFail': NV,
  'mods/ts3fixes/src/EpBatch4Logic.cs|sBpBroadcast': SEALED + ' (UseBusinessPlanner), non-virtual',
  'mods/ts3fixes/src/EpBatch4bLogic.cs|sTuShow': NV, 'mods/ts3fixes/src/EpBatch5bLogic.cs|sBpOnLot': NV,
  'mods/ts3fixes/src/ErrorTrapSims.cs|clear': OWN + '; interface fallback through DelegateTarget, skipped when abstract',
  'mods/ts3fixes/src/ErrorTrapSpread.cs|typeof(Fixes).GetMethod("EtCountLog")': ST,
  'mods/ts3fixes/src/ErrorTrapSpread.cs|typeof(Fixes).GetMethod("EtCountDebugLog")': ST,
  'mods/ts3fixes/src/ErrorTrapSpread.cs|logM': ST + ' (NRaas ErrorTrap LogCorrection)', 'mods/ts3fixes/src/ErrorTrapSpread.cs|dbgM': ST + ' (NRaas ErrorTrap DebugLogCorrection)',
  'mods/ts3fixes/src/FireActiveJob.cs|sFajMinute': NV, 'mods/ts3fixes/src/GraduationTable.cs|wait': NV + ', exact type created',
  'mods/ts3fixes/src/HitchTriggers.cs|exec': NV, 'mods/ts3fixes/src/HudUnsub.cs|h': OWN,
  'mods/ts3fixes/src/ListenerFixes.cs|sLfObjDelegate': NV, 'mods/ts3fixes/src/LotCallbacks80.cs|s.Handler': ST + ' (LotManager handlers)',
  'mods/ts3fixes/src/ObjectLeakChecks.cs|trigger': SEALED + ' (Shuffleboard+Play), non-virtual',
  'mods/ts3fixes/src/OverwatchSpread.cs|map.TargetMethods[i]': 'interface map target: the class method that implements the slot',
  'mods/ts3fixes/src/OverwatchSpread.cs|m': OWN,
  'mods/ts3fixes/src/OwElevIdle.cs|m': 'looked up on CleanupElevators, the exact type Activator creates',
  'mods/ts3fixes/src/PhoneLogic.cs|sPhConsider': ST, 'mods/ts3fixes/src/PhoneLogic.cs|sPhSchedule': ST,
  'mods/ts3fixes/src/PicnicCamera.cs|sPcHandler': NV, 'mods/ts3fixes/src/ReaperFlow.cs|sRfPostVending': NV,
  'mods/ts3fixes/src/Scan84.cs|w': NV, 'mods/ts3fixes/src/SeasonsLogicChecks.cs|m': SEALED + ' (HauntedHouse+Enter)',
  'mods/ts3fixes/src/ServiceLeave.cs|m': OWN + ' (SlMethod walks state.GetType() up, DeclaredOnly)',
  'mods/ts3fixes/src/SocialStc.cs|m': ST, 'mods/ts3fixes/src/StoreWorldEventsChecks.cs|m': NV,
  'mods/ts3fixes/src/UnseenService88.cs|m': 'from DelegateTarget (SvcRunOrder)',
  'mods/ts3fixes/src/VenueMemo.cs|sVmNumSims': NV, 'mods/ts3fixes/src/VenueMemo.cs|sVmCelebs': NV,
  'mods/ts3fixes/src/WeddingCancel.cs|sWcEnd': NV, 'mods/ts3fixes/src/Wishes86.cs|sW86TCallback': NV,
  'mods/ts3fixes/src/Wishes86.cs|typeof(Fixes).GetMethod("W86ThrowingWrapper", kW86All)': ST,
  'mods/ts3fixes/src/Wishes86.cs|sW86FRun': NV, 'mods/ts3fixes/src/Wishes86.cs|sW86MDestroyed': NV, 'mods/ts3fixes/src/Wishes86.cs|sW86MChild': NV,
  'mods/ts3agent/src/plugin/Handlers/HarnessHandlers.cs|typeof(PlayFlowModel).GetMethod("ActivateSimScreenMaskedFunc", np, null, new Type[] { typeof(object) }, null)': NV,
  'mods/ts3agent/src/plugin/Handlers/HarnessHandlers.cs|typeof(PlayFlowModel).GetMethod("LotsLoadedFunc", np)': NV,
  'mods/ts3agent/src/plugin/Handlers/HarnessHandlers.cs|typeof(PlayFlowModel).GetMethod("ScreenUnmaskedFunc", np)': NV,
  'mods/ts3agent/src/plugin/Handlers/InteractionSweepHandlers.cs|typeof(Handlers).GetMethod("OnIswScriptError", BindingFlags.Public | BindingFlags.Static)': ST,
  'mods/ts3agent/src/plugin/Handlers/ReproHandlers.cs|typeof(Handlers).GetMethod("OnSgScriptError", BindingFlags.Public | BindingFlags.Static)': ST,
  'mods/ts3agent/src/plugin/Handlers/RouteFreezeHandlers.cs|exec': NV,
  'mods/ts3agent/src/plugin/Handlers/StoryHandlers.cs|day': NV,
  'mods/ts3agent/src/plugin/Handlers/TravelHandlers.cs|sw.Switch': ST + ' (GameStates.SwitchWorlds)',
};

const stripComments = (t: string) => t.replace(/\/\*[\s\S]*?\*\//g, m => m.replace(/[^\n]/g, ' ')).replace(/"(?:\\.|[^"\\])*"|\/\/[^\n]*/g, m => m[0] === '"' ? m : ' '.repeat(m.length));

// Top-level arguments of the call whose '(' is at `open`.
function args(t: string, open: number): string[] {
  const out: string[] = []; let depth = 0, start = open + 1;
  for (let i = open; i < t.length; i++) {
    const c = t[i];
    if (c === '"') { i++; while (i < t.length && t[i] !== '"') { if (t[i] === '\\') i++; i++; } continue; }
    if (c === '(' || c === '[' || c === '{') depth++;
    else if (c === ')' || c === ']' || c === '}') { depth--; if (depth === 0) { out.push(t.slice(start, i)); break; } }
    else if (c === ',' && depth === 1) { out.push(t.slice(start, i)); start = i + 1; }
  }
  return out.map(a => a.replace(/\s+/g, ' ').trim());
}

export type Site = { file: string; line: number; mi: string; key: string };
export function sites(files: Record<string, string>): Site[] {
  const out: Site[] = [];
  for (const [file, raw] of Object.entries(files)) {
    const t = stripComments(raw);
    for (const m of t.matchAll(/\bDelegate\.CreateDelegate\s*\(/g)) {
      const a = args(t, m.index + m[0].length - 1);
      const mi = a.length >= 3 ? a[2] : a[1] ?? '';
      out.push({ file, line: t.slice(0, m.index).split('\n').length, mi, key: `${file}|${mi}` });
    }
  }
  return out;
}

export function check(files: Record<string, string>, allow: Record<string, string>): string[] {
  const bad: string[] = [];
  for (const s of sites(files)) {
    const wrapped = /^DelegateTarget\(/.test(s.mi);
    const text = stripComments(files[s.file]);
    const ident = /^\w+$/.test(s.mi) ? s.mi : null;
    for (const name of ABSTRACT_OR_OVERRIDDEN) {
      const direct = s.mi.includes(`GetMethod("${name}"`);
      const viaVar = ident && new RegExp(`\\b${ident}\\s*=\\s*typeof\\([\\w.]+\\)\\.GetMethod\\(\\s*"${name}"`).test(text);
      if (!wrapped && (direct || viaVar)) bad.push(`${s.file}:${s.line}: CreateDelegate on ${name} (abstract or overridden in the game) without DelegateTarget`);
    }
    if (!wrapped && !(s.key in allow)) bad.push(`${s.file}:${s.line}: CreateDelegate site not checked: ${s.key} (use DelegateTarget or add to ALLOW with the reason)`);
  }
  const live = new Set(sites(files).map(s => s.key));
  for (const k of Object.keys(allow)) if (k.startsWith('mods/') && !live.has(k)) bad.push(`ALLOW entry with no site left: ${k}`);
  return bad;
}

// fixture: wrapped (ok), allowed (ok), new site (hit), abstract via var despite ALLOW (hit), stale allow entry (hit)
{
  const fx = {
    'mods/A.cs': 'static readonly MethodInfo sRun = typeof(B).GetMethod("RunOrderBehavior", f);\n'
      + 'F x = (F)Delegate.CreateDelegate(typeof(F), ia, DelegateTarget(ia, sRun));\n'
      + 'F y = (F)Delegate.CreateDelegate(typeof(F), ia, sRun); // CreateDelegate(typeof(F), ia, zz)\n'
      + 'G g = (G)Delegate.CreateDelegate(typeof(G), sStat);\nG h = (G)Delegate.CreateDelegate(typeof(G), o, sNew);\n',
  };
  const got = check(fx, { 'mods/A.cs|sRun': 'x', 'mods/A.cs|sStat': 'static', 'mods/A.cs|gone': 'x' });
  const ok = got.length === 3 && got[0].startsWith('mods/A.cs:3: CreateDelegate on RunOrderBehavior') && got[1].startsWith('mods/A.cs:5: CreateDelegate site not checked')
    && got[2].includes('no site left: mods/A.cs|gone');
  console.assert(ok, 'fixture: ' + JSON.stringify(got));
  if (!ok) process.exit(1);
}

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const files: Record<string, string> = {};
const walk = (d: string) => {
  for (const e of readdirSync(d, { withFileTypes: true })) {
    const p = join(d, e.name);
    if (e.isDirectory()) { if (!['bin', 'obj'].includes(e.name)) walk(p); }
    else if (p.endsWith('.cs')) files[relative(ROOT, p).split('\\').join('/')] = readFileSync(p, 'utf8');
  }
};
walk(join(ROOT, 'mods', 'ts3fixes', 'src'));
for (const d of readdirSync(join(ROOT, 'mods'))) if (d.startsWith('ts3agent')) walk(join(ROOT, 'mods', d));
if (process.argv.includes('--list')) { for (const s of sites(files)) console.log(`${s.file}:${s.line}  ${s.key}`); process.exit(0); }
const bad = check(files, ALLOW);
for (const b of bad) console.log('FAIL ' + b);
if (bad.length) process.exit(1);
console.log(`delegatesite ok: ${sites(files).length} CreateDelegate sites, each through DelegateTarget or allow-listed with a reason`);
