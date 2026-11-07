// Release feature switchboard (docs/RELEASE.md "v0.1.5"): one step from "the A/Bs decided these features" to the release
// defaults. It knows every v0.1.5 candidate and where its default lives: ts3native.ini keys (native/ts3native/ts3native.ini,
// which build.cmd copies to out/) and ts3fixes tuning (tools/setup/build-fixes.ts TUNING_XML). Unchosen candidates are set
// back to off, so the result depends only on the list.
//   node tools/release-features.ts --list                      candidates, their edits, what they need
//   node tools/release-features.ts --status                    which candidates the current ini/tuning have on
//   node tools/release-features.ts slotenabledro,fallbackasync dry run: print every edit, change nothing
//   node tools/release-features.ts slotenabledro,... --write   write ini + tuning
//   node tools/release-features.ts slotenabledro,... --write --docs   also resolve the feature blocks in docs/PLAYERS.md
//                                                             and CHANGELOG.md (one way: the markers are removed)
//   node tools/release-features.ts none --write                every candidate off (= the v0.1.4 defaults)
//   node tools/release-features.ts --self-check                offline checks (CI)
// Doc blocks: lines between "<!-- feature:<id> on -->" and "<!-- /feature -->" stay only when <id> is chosen, between
// "<!-- feature:<id> off -->" and "<!-- /feature -->" only when it is not. Until --docs runs, PLAYERS.md names every
// candidate, so the CI PLAYERS.md check passes for any choice.
import { readFileSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..');
const INI = join(ROOT, 'native', 'ts3native', 'ts3native.ini');
const TUNING = join(ROOT, 'tools', 'setup', 'build-fixes.ts');
const DOCS = [join(ROOT, 'docs', 'PLAYERS.md'), join(ROOT, 'CHANGELOG.md')];
const CI = join(ROOT, '.github', 'workflows', 'build.yml');

type Feature = { ini?: Record<string, [on: string, off: string]>; tuning?: string[]; needs?: string[]; doc: string };
// ini key -> [release value when chosen, value when not]; tuning = ts3fixes tunables set True when chosen, False when not
export const FEATURES: Record<string, Feature> = {
  fallbackasync: { ini: { 'RouteAsync.FallbackAsync': ['1', '0'], 'RouteAsync.SyncCapMs': ['1000', '0'] }, doc: 'docs/134 (SyncCapMs=1000 decided 2026-10-21, ships with FallbackAsync)' },
  m1retry: { ini: { 'RouteAsync.M1Retry': ['1', '0'] }, needs: ['fallbackasync'], doc: 'docs/134 §2.6' },
  fbjobcap: { ini: { 'RouteAsync.FbJobCapMs': ['300', '0'] }, needs: ['fallbackasync'], doc: 'docs/134 §10 (ts3native 0.36.16)' },
  slotenabledro: { ini: { 'SlotEnabledRO.Enabled': ['1', '0'], 'SlotEnabledRO.Mode': ['1', '1'], 'SlotEnabledRO.Shadow': ['0', '0'] }, doc: 'docs/90, docs/136 (decided ship)' },
  deferfootprint: { ini: { 'Discourage.DeferFootprint': ['1', '0'] }, doc: 'docs/89 design U' },
  refcountguard: { ini: { 'RouteAsync.RefcountGuard': ['1', '0'], 'RouteAsync.RefcountGuardMode': ['1', '1'] }, doc: 'docs/177 §7' },
  destroypark: { ini: { 'Discourage.DestroyPark': ['1', '0'] }, doc: 'docs/88 §10 design G' },
  avoidro: { ini: { 'AvoidRO.Enabled': ['1', '0'], 'AvoidRO.Mode': ['1', '1'], 'AvoidRO.Shadow': ['0', '0'] }, doc: 'docs/137' },
  unseenthrottle: { ini: { 'UnseenThrottle.Enabled': ['1', '0'] }, doc: 'docs/143 (needs ts3fixes from the same release: household positions)' },
  // P5 + P5b/P5c lending + docs/182 consumer, as the 2026-10-23 night ran it (overnight-1023.json) minus verify/audit probes
  // (VerifyEvery/AuditEvery stay at the ini defaults); takes ReplanFromPosition answers only, so [RouteAsync] Kinds = 4
  routesnap: { ini: { 'RouteAsync.Kinds': ['4', '0'], 'RouteSnap.Enabled': ['1', '0'], 'RouteSnap.Mode': ['2', '0'], 'RouteSnap.Kinds': ['8', '15'],
    'RouteSnap.Lots': ['replay', 'none'], 'RouteSnap.Prebuild': ['1', '0'], 'RouteSnap.LazyKeep': ['1', '0'], 'RouteSnap.ReplaySigMs': ['250', '0'],
    'RouteSnap.LotsUnlocked': ['1', '0'], 'RouteSnap.InsFreeInPlace': ['1', '0'], 'RouteSnap.RecsUnlocked': ['1', '0'],
    'RouteSnap.ReleaseLock': ['1', '0'], 'RouteSnap.ReleaseGates': ['1', '0'], 'RouteSnap.FreeLock': ['1', '0'] },
    doc: 'docs/94 §19-§22, §20 (P6 gate G6), docs/182; opt-in: CI keeps [RouteSnap] out of the release defaults' },
  // P6 widening (docs/94 §24, ts3native 0.36.41): the Sims' own Route.Plan jobs take the clone answer too, and Mode=2 STARTs
  // behind a busy job share the pending cut. A separate id so routesnap (RFP only) can ship without it. p5.cpp takes
  // TakeKinds & Kinds, so Kinds=9 (bit 1 Route.Plan + bit 8 ReplanFromPosition) overrides routesnap's 8 (apply: a chosen
  // feature's value wins over an unchosen one's, so unchosen it leaves Kinds to routesnap)
  routesnapplan: { ini: { 'RouteSnap.Kinds': ['9', '8'], 'RouteSnap.TakeKinds': ['9', '8'], 'RouteSnap.ShareCuts': ['1', '0'] }, needs: ['routesnap'], doc: 'docs/94 §24 (P6 widening; gates G0-G2 of §22.30.4 first)' },
  stuckcheckasync: { tuning: ['kStuckCheckAsync'], doc: 'docs/134 §8' },
  replandefer: { tuning: ['kReplanDefer'], doc: 'docs/141 §6' },
  replanspread: { tuning: ['kReplanSpread'], doc: 'docs/141 §6' },
  safespotcap: { tuning: ['kSafeSpotCap'], doc: 'docs/141 §7' },
  farthestsorted: { tuning: ['kFarthestLotSorted'], doc: 'docs/141 §7' },
  indoorcap: { tuning: ['kIndoorPositionCap'], doc: 'docs/141 §7' },
  overwatchspread: { tuning: ['kOverwatchSpread'], doc: 'docs/145' },
  overwatchchain: { tuning: ['kOverwatchChain'], doc: 'docs/145 §10 (Overwatch v2; overwatchspread must stay off: it wins over the chain, OverwatchSpreadCore.WithChain)' },
  easpreadlots: { tuning: ['kEaSpreadLots'], doc: 'docs/147 §2' },
  easpreadpets: { tuning: ['kEaSpreadPets'], doc: 'docs/147 §3' },
  easpreaddecay: { tuning: ['kEaSpreadDecay'], doc: 'docs/147 §4' },
  errortrapspread: { tuning: ['kErrorTrapSpread'], doc: 'docs/146' },
  unseenenv: { tuning: ['kUnseenEnvThrottle'], doc: 'docs/144' },
  unseenmotives: { tuning: ['kUnseenMotiveThrottle'], doc: 'docs/144' },
  unseenpark: { tuning: ['kUnseenPark'], doc: 'docs/243' },
  unseenbroadcast: { tuning: ['kUnseenBroadcastThrottle'], doc: 'docs/144' },
  // v0.2.0 routing set, as the v020-natural on arms ran it (2026-10-26/27/28)
  arenaguard: { ini: { 'ArenaGuard.InitobjFix': ['1', '0'], 'ArenaGuard.TailPad': ['1', '0'] }, doc: 'docs/67 §22.9 (crash S)' },
  plandeadline: { ini: { 'RouteAsync.PlanDeadlineMs': ['300', '0'] }, doc: 'docs/164 §7' },
  fbjobdeadline: { ini: { 'RouteAsync.FbJobDeadline': ['1', '0'] }, needs: ['fallbackasync'], doc: 'docs/178 T1' },
  kinddeadlines: { ini: { 'RouteAsync.KindDeadlines': ['1', '0'] }, doc: 'docs/197' },
  overwatchstuckslice: { tuning: ['kOverwatchStuckSlice'], doc: 'docs/145 §12' },
  unseenfallback: {
    ini: { 'UnseenFallback.Enabled': ['1', '0'], 'UnseenFallback.DeadlineMs': ['300', '300'] },
    tuning: ['kUnseenFallbackStuck', 'kUnseenFallbackPlan', 'kUnseenFallbackTeleport', 'kUnseenFallbackPre', 'kUnseenFallbackArrive', 'kUnseenFallbackLeave', 'kUnseenFallbackLoopDetect'],
    doc: 'docs/202 A/B (hitch guard)',
  },
  unseenfbsites: { ini: { 'UnseenFallback.Sites': ['1', '0'], 'UnseenFallback.SiteWaitMs': ['0', '0'] }, tuning: ['kUnseenFallbackSites'], needs: ['unseenfallback'], doc: 'docs/202 §3.4 C1-C4' },
};

export function parseChosen(arg: string): Set<string> {
  const ids = arg === 'none' ? [] : arg.split(',').map(s => s.trim()).filter(Boolean);
  for (const id of ids) if (!FEATURES[id]) throw new Error(`unknown feature "${id}" (--list shows the candidates)`);
  for (const id of ids) for (const n of FEATURES[id].needs ?? []) if (!ids.includes(n)) throw new Error(`${id} needs ${n}`);
  if (ids.includes('overwatchchain') && ids.includes('overwatchspread')) throw new Error('overwatchchain and overwatchspread together: spread wins, the chain never runs');
  return new Set(ids);
}

// set Section.Key=value on its non-comment line; throws when the key is missing (the ini layout changed)
export function setIni(text: string, key: string, value: string, log: string[]): string {
  const [sec, k] = key.split('.');
  const lines = text.split(/\r?\n/);
  const eol = text.includes('\r\n') ? '\r\n' : '\n';
  let cur = '', hit = -1;
  for (let i = 0; i < lines.length; i++) {
    const t = lines[i].trim();
    if (t.startsWith('[')) cur = t.slice(1, t.indexOf(']'));
    else if (cur === sec && !t.startsWith(';') && t.split('=')[0].trim() === k) { hit = i; break; }
  }
  if (hit < 0) throw new Error(`ts3native.ini has no ${key}`);
  const old = lines[hit].split('=').slice(1).join('=').trim();
  if (old !== value) { log.push(`ini  [${sec}] ${k}: ${old} -> ${value}`); lines[hit] = `${k}=${value}`; }
  return lines.join(eol);
}

export function setTuning(text: string, name: string, on: boolean, log: string[]): string {
  const re = new RegExp(`<${name} value="(True|False)"`);
  const m = text.match(re);
  if (!m) throw new Error(`build-fixes.ts tuning has no ${name}`);
  const v = on ? 'True' : 'False';
  if (m[1] !== v) log.push(`tuning ${name}: ${m[1]} -> ${v}`);
  return text.replace(re, `<${name} value="${v}"`);
}

export function resolveBlocks(text: string, chosen: Set<string>, log: string[], file: string): string {
  const out: string[] = [];
  let keep = true, open = '';
  const eol = text.includes('\r\n') ? '\r\n' : '\n';
  for (const line of text.split(/\r?\n/)) {
    const s = line.match(/^<!-- feature:([a-z0-9]+) (on|off) -->$/);
    if (s) {
      if (open) throw new Error(`${file}: feature block ${s[1]} opens inside ${open}`);
      if (!FEATURES[s[1]]) throw new Error(`${file}: unknown feature block ${s[1]}`);
      open = s[1]; keep = (s[2] === 'on') === chosen.has(s[1]);
      log.push(`doc  ${file}: ${s[1]} ${s[2]} block ${keep ? 'kept' : 'removed'}`);
      continue;
    }
    if (line === '<!-- /feature -->') { if (!open) throw new Error(`${file}: stray <!-- /feature -->`); open = ''; keep = true; continue; }
    if (keep) out.push(line);
  }
  if (open) throw new Error(`${file}: feature block ${open} not closed`);
  return out.join(eol);
}

// a key set by several features (RouteSnap.Kinds): the last chosen feature's on value, else the first feature's off value
export function iniValues(chosen: Set<string>): Record<string, string> {
  const vals: Record<string, string> = {};
  for (const [id, f] of Object.entries(FEATURES)) for (const [key, [on, off]] of Object.entries(f.ini ?? {}))
    if (chosen.has(id)) vals[key] = on; else if (!(key in vals)) vals[key] = off;
  return vals;
}

export function apply(chosen: Set<string>, ini: string, tuning: string, log: string[]): { ini: string; tuning: string } {
  const vals = iniValues(chosen);
  for (const [id, f] of Object.entries(FEATURES)) {
    for (const key of Object.keys(f.ini ?? {})) ini = setIni(ini, key, vals[key], log);
    for (const t of f.tuning ?? []) tuning = setTuning(tuning, t, chosen.has(id), log);
  }
  return { ini, tuning };
}

// id is on when every key is at its "on" value, or at the on value of a feature that needs id and sets the same key
// (routesnap with routesnapplan's Kinds=9)
export function isOn(id: string, ini: string, tuning: string): boolean {
  const f = FEATURES[id];
  for (const [key, [on]] of Object.entries(f.ini ?? {})) {
    const vals = [on, ...Object.values(FEATURES).filter(g => g.needs?.includes(id) && g.ini?.[key]).map(g => g.ini![key][0])];
    if (!vals.some(v => { const l: string[] = []; setIni(ini, key, v, l); return l.length === 0; })) return false;
  }
  const log: string[] = [];
  for (const t of f.tuning ?? []) setTuning(tuning, t, true, log);
  return log.length === 0;
}

// which candidates the given ini/tuning have on
export function status(ini: string, tuning: string): string[] {
  return Object.keys(FEATURES).filter(id => isOn(id, ini, tuning));
}

// sections the CI release step accepts as Enabled=1 (build.yml "Not stable" check)
function ciAllowed(): string[] {
  const m = readFileSync(CI, 'utf8').match(/extra=\$\(awk[^\n]*/);
  return m ? [...m[0].matchAll(/s!="\[(\w+)\]"/g)].map(x => x[1]) : [];
}

function selfCheck(): void {
  const ok = (c: boolean, what: string) => { if (!c) { console.error(`self-check FAILED: ${what}`); process.exit(1); } };
  const ini = '[RouteAsync]\n; FallbackAsync=9 comment\nEnabled=1\nKinds=0\nFallbackAsync=0\nM1Retry=0\nSyncCapMs=0\nFbJobCapMs=0\nRefcountGuard=0\nRefcountGuardMode=1\nPlanDeadlineMs=0\nFbJobDeadline=0\nKindDeadlines=0\n[ArenaGuard]\nTailPad=0\nInitobjFix=0\n[UnseenFallback]\nEnabled=0\nDeadlineMs=300\nSites=0\nSiteWaitMs=0\n[SlotEnabledRO]\nEnabled=0\nMode=1\nShadow=0\n' +
    '[Discourage]\nDeferFootprint=0\nDestroyPark=0\n[AvoidRO]\nEnabled=0\nMode=1\nShadow=0\n[UnseenThrottle]\nEnabled=0\n' +
    '[RouteSnap]\n' + Object.entries(iniValues(new Set())).filter(([k]) => k.startsWith('RouteSnap.')).map(([k, off]) => `${k.slice(10)}=${off}\n`).join('');
  const tun = Object.values(FEATURES).flatMap(f => f.tuning ?? []).map(t => `<${t} value="False" />`).join('\n') + '\n<kGoHomeShortcut value="True" />';
  const log: string[] = [];
  const a = apply(parseChosen('fallbackasync,m1retry,stuckcheckasync'), ini, tun, log);
  ok(a.ini.includes('\nFallbackAsync=1\n') && a.ini.includes('\nSyncCapMs=1000\n') && a.ini.includes('\nM1Retry=1\n'), 'fallbackasync + m1retry set');
  ok(a.ini.includes('; FallbackAsync=9 comment'), 'comment lines untouched');
  ok(a.tuning.includes('<kStuckCheckAsync value="True"') && a.tuning.includes('<kReplanDefer value="False"') && a.tuning.includes('<kGoHomeShortcut value="True"'), 'tuning set');
  ok(log.length === 4, `4 edits logged (got ${log.length})`);
  ok(status(a.ini, a.tuning).join() === 'fallbackasync,m1retry,stuckcheckasync', `status reads back (${status(a.ini, a.tuning).join()})`);
  const b = apply(parseChosen('none'), a.ini, a.tuning, []);
  ok(b.ini === ini && b.tuning === tun, 'none = back to all off');
  const s = apply(parseChosen('routesnap'), ini, tun, []);
  ok(s.ini.includes('\nLots=replay\n') && s.ini.includes('\nKinds=4\n') && s.ini.includes('\nTakeKinds=8\n') && status(s.ini, s.tuning).join() === 'routesnap', 'routesnap on reads back');
  const p6 = apply(parseChosen('routesnap,routesnapplan'), ini, tun, []);
  ok(p6.ini.includes('\nTakeKinds=9\n') && p6.ini.includes('\nShareCuts=1\n') && status(p6.ini, p6.tuning).join() === 'routesnap,routesnapplan', 'routesnapplan on reads back');
  ok(apply(parseChosen('none'), p6.ini, p6.tuning, []).ini === ini, 'routesnapplan off = Kinds back to routesnap off (15)');
  ok(iniValues(new Set(['routesnapplan', 'routesnap']))['RouteSnap.Kinds'] === '9', 'Kinds=9 whatever the list order');
  let t6 = ''; try { parseChosen('routesnapplan'); } catch (e: any) { t6 = e.message; } ok(/needs routesnap/.test(t6), 'routesnapplan needs routesnap');
  let t2 = ''; try { parseChosen('overwatchchain,overwatchspread'); } catch (e: any) { t2 = e.message; } ok(/spread wins/.test(t2), 'chain vs spread refused');
  let threw = ''; try { parseChosen('m1retry'); } catch (e: any) { threw = e.message; } ok(/needs fallbackasync/.test(threw), 'needs checked');
  threw = ''; try { parseChosen('nope'); } catch (e: any) { threw = e.message; } ok(/unknown/.test(threw), 'unknown id refused');
  threw = ''; try { setIni(ini, 'AvoidRO.Missing', '1', []); } catch (e: any) { threw = e.message; } ok(/no AvoidRO.Missing/.test(threw), 'missing key refused');
  const doc = 'a\n<!-- feature:avoidro on -->\nAV on\n<!-- /feature -->\n<!-- feature:avoidro off -->\nAV off\n<!-- /feature -->\nb';
  ok(resolveBlocks(doc, new Set(['avoidro']), [], 'x') === 'a\nAV on\nb', 'on block kept');
  ok(resolveBlocks(doc, new Set(), [], 'x') === 'a\nAV off\nb', 'off block kept');
  threw = ''; try { resolveBlocks('<!-- feature:avoidro on -->\nx', new Set(), [], 'x'); } catch (e: any) { threw = e.message; } ok(/not closed/.test(threw), 'open block refused');
  // the real files: every key and tunable exists, every doc block parses, the repo state reads back
  const realIni = readFileSync(INI, 'utf8'), realTun = readFileSync(TUNING, 'utf8');
  apply(new Set(Object.keys(FEATURES)), realIni, realTun, []);
  for (const f of DOCS) resolveBlocks(readFileSync(f, 'utf8'), new Set(), [], f);
  // the P6 extras queue (docs/94 §20) runs the same snapshot stack as this feature
  for (const j of JSON.parse(readFileSync(join(ROOT, 'tools', 'harness', 'queues', 'snap-release.json'), 'utf8')).jobs)
    for (const [k, [on]] of Object.entries(FEATURES.routesnap.ini!)) ok(String(j.ini?.[k]) === on, `snap-release.json ${j.id}: ${k}=${on}`);
  // the P6 night queue runs routesnap + routesnapplan: TakeKinds is ANDed with Kinds (p5.cpp), so both must say 9
  for (const j of JSON.parse(readFileSync(join(ROOT, 'tools', 'harness', 'queues', 'night-1024-p6.json'), 'utf8')).jobs)
    if (j.ini?.['RouteSnap.TakeKinds'] !== undefined) for (const [k, [on]] of Object.entries(FEATURES.routesnapplan.ini!)) ok(String(j.ini[k]) === on, `night-1024-p6.json ${j.id}: ${k}=${on}`);
  console.log('release-features self-check ok');
}

const args = process.argv.slice(2);
// only as a script: tools/release-check.ts imports FEATURES, setIni and setTuning
if (import.meta.filename !== resolve(process.argv[1] ?? '')) { /* imported */ }
else if (args.includes('--self-check')) selfCheck();
else if (args.includes('--list')) {
  for (const [id, f] of Object.entries(FEATURES))
    console.log(`${id.padEnd(16)} ${[...Object.entries(f.ini ?? {}).map(([k, [on]]) => `${k}=${on}`), ...(f.tuning ?? []).map(t => `${t}=True`)].join(', ')}${f.needs ? `  (needs ${f.needs.join(', ')})` : ''}  [${f.doc}]`);
} else if (args.includes('--status')) {
  console.log(`on in the repo defaults: ${status(readFileSync(INI, 'utf8'), readFileSync(TUNING, 'utf8')).join(', ') || 'none'}`);
} else {
  const list = args.find(a => !a.startsWith('--'));
  if (!list) { console.error('usage: node tools/release-features.ts <id,id,...|none> [--write [--docs]] | --list | --status | --self-check'); process.exit(2); }
  const chosen = parseChosen(list);
  const log: string[] = [];
  const r = apply(chosen, readFileSync(INI, 'utf8'), readFileSync(TUNING, 'utf8'), log);
  const docs = args.includes('--docs') ? DOCS.map(f => [f, resolveBlocks(readFileSync(f, 'utf8'), chosen, log, f.slice(ROOT.length + 1))]) : [];
  console.log(log.join('\n') || 'no changes');
  // the CI release step refuses an Enabled=1 section it does not list
  const allowed = new Set(ciAllowed());
  const blocked = [...chosen].flatMap(id => Object.keys(FEATURES[id].ini ?? {}).filter(k => k.endsWith('.Enabled')).map(k => k.split('.')[0])).filter(s => !allowed.has(s));
  if (blocked.length) console.log(`WARNING: .github/workflows/build.yml does not allow ${blocked.join(', ')} as Enabled=1: add them to the "Not stable" check`);
  if (args.includes('--write')) {
    writeFileSync(INI, r.ini); writeFileSync(TUNING, r.tuning);
    for (const [f, t] of docs) writeFileSync(f, t);
    console.log(`written: ts3native.ini, build-fixes.ts${docs.length ? ', PLAYERS.md, CHANGELOG.md' : ''}. Next: native/ts3native/build.cmd, node tools/setup/build-fixes.ts, hashes into docs/RELEASE.md`);
  } else console.log('(dry run: add --write to change the files)');
}
