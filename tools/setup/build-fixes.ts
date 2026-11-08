// Compiles mods/ts3fixes (the player mod with the stutter fixes, docs/04 F8-F12) against the game's own DLLs and packs
// it into build/ts3fixes.package. Windows-only by nature: it needs the .NET Framework C# compiler (csc.exe, C# 5).
//   node tools/setup/build-fixes.ts [--on kTuningA,kTuningB=value]   (--on: also a package with those tunings True, or =value)
// Also builds the TEST-ONLY mod mods/portalplan-test (MonoPatcher [TypePatch] on Lot.OnPortalPathPlan, docs/94 §20 job 17)
// into build/ts3-portalplan-test.package when MonoPatcher's package is in the installer cache; skipped otherwise.
import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { join, resolve } from 'node:path';
import { TYPE_S3SA, fnv64, s3saEncode, writePackage } from '../dbpf.ts';

const ROOT = resolve(import.meta.dirname, '..', '..');
const CSC = 'C:/Windows/Microsoft.NET/Framework/v4.0.30319/csc.exe';
const REF_DIR = process.env.TS3_REF_DIR ?? join(ROOT, 'game-copy', 'assemblies'); // from tools/extract-assemblies.ts
const REFS = ['mscorlib', 'System', 'System.Xml', 'ScriptCore', 'SimIFace', 'Sims3Metadata',
              'Sims3GameplaySystems', 'Sims3GameplayObjects', 'Sims3StoreObjects', 'UI'];
const ASSEMBLY = 'Ts3Fixes';
const TYPE_XML = 0x0333406c;
// Tuning for Ts3Fixes.Instantiator: loading it applies the fixes; the other values are the player's settings.
const TUNING_XML = `<?xml version="1.0" encoding="utf-8"?>
<base>
  <Current_Tuning>
    <kInstantiator value="True" />
    <kGoHomeShortcut value="True" />
    <kBuildingTripShortcut value="True" />
    <kLotTripShortcut value="True" />
    <kBroadcasterThrottle value="True" />
    <kWalkStyleComparer value="True" />
    <kVirtualHomeShortcut value="True" />
    <kNoRouteFailWalkOffscreen value="True" />
    <kStuckWatchdog value="True" />
    <kStuckWatchdogIdle value="True" />
    <kRouteFailureBackoff value="True" />
    <kSaveCleanup value="False" />
    <kSnapshotCap value="0" />
    <kSaveCleanupAggressive value="False" />
    <kAggressiveSnapshotKeep value="10" />
    <kAggressiveQuietDays value="56" />
    <kPlayedGenerations value="2" />
    <kPlayedSnapshotKeep value="40" />
    <kNoStationDetourOffscreen value="True" />
    <kPortalAdaptorLeak value="True" />
    <kCasGrid value="False" />
    <kSpatialCache value="False" />
    <kRoutableCache value="False" />
    <kStuckCheckBusy value="False" />
    <kStuckCheckAsync value="True" />
    <kSafeSpotCap value="False" />
    <kFarthestLotSorted value="False" />
    <kIndoorPositionCap value="False" />
    <kFastRefTable value="True" />
    <kCachedReflection value="False" />
    <kMotiveBarImageCache value="True" />
    <kBuffGridInPlace value="True" />
    <kSimNameCache value="True" />
    <kDetailPriorityCache value="False" />
    <kHoursMemo value="False" />
    <kVenueMemo value="False" />
    <kAutLive value="True" />
    <kAutLiveGuard value="True" />
    <kAutLiveGuardOffPm value="1000" />
    <kAutLiveGuardOnPm value="800" />
    <kAutLiveGuardMinSearches value="40" />
    <kAutLiveGuardMinFrames value="300" />
    <kAutLiveLate value="False" />
    <kAutLiveLateTicks value="3" />
    <kAutLiveGuardHold value="600" />
    <kAutUnseenPace value="True" />
    <kAutUnseenCmp value="False" />
    <kAutUnseenRabbitHoleGame value="True" />
    <kAutUnseenPacePct value="78" />
    <kSafeSpotReuse value="False" />
    <kWildflowerProbe value="False" />
    <kWildflowerTask value="True" />
    <kCaneEarlyOut value="False" />
    <kTooltipThrottle value="False" />
    <kDoorMirror value="False" />
    <kAutShadow value="False" />
    <kReplanDefer value="False" />
    <kReplanSpread value="False" />
    <kUnseenEnvThrottle value="False" />
    <kUnseenMotiveThrottle value="False" />
    <kUnseenBroadcastThrottle value="False" />
    <kUnseenPark value="False" />
    <kUnseenParkMinutes value="30" />
    <kUnseenParkStaggerTicks value="16" />
    <kUnseenThrottleEvery value="4" />
    <kUnseenBroadcastEvery value="16" />
    <kUnseenCarThrottle value="False" />
    <kHitchCauseHooks value="False" />
    <kWakeHooks value="False" />
    <kWakePark value="False" />
    <kWakePoll value="False" />
    <kWakeParkTicks value="12" />
    <kWakePollTicks value="30" />
    <kUnseenCarEvery value="4" />
    <kOverwatchSpread value="False" />
    <kOverwatchChain value="True" />
    <kErrorTrapSpread value="True" />
    <kEaSpreadLots value="True" />
    <kEaSpreadPets value="True" />
    <kEaSpreadDecay value="True" />
    <kRouteSources value="False" />
    <kLegReplanSkip value="False" />
    <kBouncerSlotSkip value="False" />
    <kIdlePlanMemo value="False" />
    <kStuckCheckNear value="False" />
    <kFailRepeat value="False" />
    <kJobYield value="False" />
    <kDriverTaskAsync value="False" />
    <kStuckCheckOwn value="False" />
    <kOverwatchStuckSlice value="True" />
    <kSeasonMarkerCache value="False" />
    <kSafeSpotYield value="False" />
    <kDisposeSpread value="False" />
    <kOverwatchStuckNoticeSummary value="False" />
    <kPrePlanCallback value="False" />
    <kStuckCheckCap value="False" />
    <kUnseenFallbackStuck value="True" />
    <kUnseenFallbackPlan value="True" />
    <kUnseenFallbackTeleport value="True" />
    <kUnseenFallbackSites value="False" />
    <kUnseenFallbackPre value="True" />
    <kUnseenFallbackArrive value="True" />
    <kUnseenFallbackLeave value="True" />
    <kUnseenFallbackTrip value="False" />
    <kUnseenFallbackTripHold value="False" />
    <kGoalHouseholdExempt value="True" />
    <kUnseenFallbackLoopDetect value="True" />
    <kUnseenFallbackLoopBreak value="False" />
    <kUnseenFallbackFinish value="False" />
    <kUnseenRouteRelaxed value="False" />
    <kUnseenBoat value="False" />
    <kUnseenBoatStart value="False" />
    <kPosturePopCycle value="False" />
    <kOverwatchIgnoreStuckProbes value="True" />
    <kSeasonImposter value="False" />
    <kSeasonImposterCold value="False" />
    <kPreferredService value="False" />
    <kSocialWorkerOnLot value="False" />
    <kEaNullGuards value="False" />
    <kShowStageThrowables value="False" />
    <kFirePitChair value="False" />
    <kAgeUpKeepOutfits value="True" />
    <kAgeUpSwap value="False" />
    <kOutfitWaitPoll value="True" />
    <kUniformBackup value="True" />
    <kConvTurn value="False" />
    <kDeadRefGuards value="False" />
    <kBakeSaleSeller value="True" />
    <kNullService value="True" />
    <kRabbitHoleDoor value="True" />
    <kPhotoSession value="True" />
    <kEggSpot value="True" />
    <kGraduationTable value="True" />
    <kJazzParam value="True" />
    <kBuffUpdate value="True" />
    <kCarNoRoute value="True" />
    <kNpcDriverPool value="True" />
    <kBookCarry value="False" />
    <kAgeUpAbort value="True" />
    <kRewardGiver value="True" />
    <kDatingProfile value="True" />
    <kLeaveJobDispose value="True" />
    <kInvNotYet value="True" />
    <kMetricSkillMissing value="True" />
    <kFireworksFizzle value="True" />
    <kBookHand value="False" />
    <kBookHandDiag value="False" />
    <kPetPoolOrphan value="True" />
    <kRelTablesApart value="True" />
    <kAsyncJigSync value="True" />
    <kDeadRefSweep value="False" />
    <kVacExport value="False" />
    <kVacStoreGuard value="False" />
    <kVacReturnSkip value="False" />
    <kVacTeenDelete value="False" />
    <kVacExitReturn value="False" />
    <kVacListenerNull value="False" />
    <kVacImportFix value="False" />
    <kMenuMaven value="False" />
  </Current_Tuning>
</base>
`;

const missing = REFS.filter(r => !existsSync(join(REF_DIR, `${r}.dll`)));
if (missing.length) {
  console.error(`missing game DLLs in ${REF_DIR}: ${missing.join(', ')}. Run tools/extract-assemblies.ts first.`);
  process.exit(1);
}
const outDir = process.env.TS3_BUILD_DIR ?? join(ROOT, 'build');
mkdirSync(outDir, { recursive: true });
const src = join(ROOT, 'mods', 'ts3fixes', 'src');
const dll = join(outDir, `${ASSEMBLY}.dll`);
const compile = (srcDir: string, out: string, extraRefs: string[] = []) =>
  execFileSync(CSC, ['/nologo', '/nostdlib+', '/noconfig', '/target:library', '/optimize+', '/langversion:5', '/warn:4',
    '/nowarn:414,169,649', `/out:${out}`, ...REFS.map(r => `/reference:${join(REF_DIR, `${r}.dll`)}`), ...extraRefs.map(r => `/reference:${r}`),
    ...readdirSync(srcDir).filter(f => f.endsWith('.cs')).map(f => join(srcDir, f))], { stdio: 'inherit' });
compile(src, dll);
const pkg = writePackage([
  { type: TYPE_S3SA, group: 0, instance: fnv64(ASSEMBLY), data: s3saEncode(readFileSync(dll)) },
  { type: TYPE_XML, group: 0, instance: fnv64(`${ASSEMBLY}.Instantiator`), data: Buffer.from(TUNING_XML, 'utf8') },
]);
const out = join(outDir, 'ts3fixes.package');
writeFileSync(out, pkg);
console.log(`built ${out} (${pkg.length} bytes)`);

// --on kA,kB: also writes build/ts3fixes-on-kA-kB.package, the same DLL with those tunings True (test queues that need a
// hook patched at startup, e.g. kVenueMemo, docs/122 §14.24.7, pick it with fixesPackage).
const onArg = process.argv.indexOf('--on');
if (onArg > 0) {
  const names = (process.argv[onArg + 1] ?? '').split(',').filter(Boolean);
  let xml = TUNING_XML;
  for (const n of names) {
    const [key, value] = n.split('=');
    if (value !== undefined) { // any tuning, e.g. kAutUnseenPacePct=50 or kAutLive=False
      const line = new RegExp(`<${key} value="[^"]*" />`);
      if (!line.test(xml)) { console.error(`--on: ${key} is not in TUNING_XML`); process.exit(1); }
      xml = xml.replace(line, `<${key} value="${value}" />`);
      continue;
    }
    const off = `<${n} value="False" />`;
    if (!xml.includes(off)) { console.error(`--on: ${n} is not a False tuning in TUNING_XML`); process.exit(1); }
    xml = xml.replace(off, `<${n} value="True" />`);
  }
  const variant = writePackage([
    { type: TYPE_S3SA, group: 0, instance: fnv64(ASSEMBLY), data: s3saEncode(readFileSync(dll)) },
    { type: TYPE_XML, group: 0, instance: fnv64(`${ASSEMBLY}.Instantiator`), data: Buffer.from(xml, 'utf8') },
  ]);
  const vOut = join(outDir, `ts3fixes-on-${names.join('-')}.package`);
  writeFileSync(vOut, variant);
  console.log(`built ${vOut} (${variant.length} bytes, ${names.join(', ')})`);
}

// Test-only mod. MonoPatcher has no license, so its DLL never goes in the repo: take it from install-fixes.ts's unpacked download.
const MP_PKG = join(process.env.LOCALAPPDATA ?? join(homedir(), '.local', 'share'), 'ts3-fixes', 'unpacked', 'monopatcher', 'Packages', 'ld_MonoPatcher.package');
if (!existsSync(MP_PKG)) {
  console.log(`skipped portalplan-test (test only): no ${MP_PKG}; run node tools/setup/install-fixes.ts install --only monopatcher first`);
} else {
  const refDir = join(outDir, 'refs');
  mkdirSync(refDir, { recursive: true });
  execFileSync(process.execPath, [join(ROOT, 'tools', 'extract-assemblies.ts'), refDir, MP_PKG], { stdio: 'ignore' });
  const TEST = 'Ts3PortalPlanTest';
  const testDll = join(outDir, `${TEST}.dll`);
  compile(join(ROOT, 'mods', 'portalplan-test', 'src'), testDll, [join(refDir, 'MonoPatcher.dll')]);
  const testPkg = writePackage([{ type: TYPE_S3SA, group: 0, instance: fnv64(TEST), data: s3saEncode(readFileSync(testDll)) }]);
  const testOut = join(outDir, 'ts3-portalplan-test.package');
  writeFileSync(testOut, testPkg);
  console.log(`built ${testOut} (${testPkg.length} bytes, TEST ONLY)`);
}
