using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Ts3Agent
{
    // Patch host experiment: replace a C# method at runtime by copying the replacement's MonoMethod header over the
    // original's (the idea MonoPatcher's ReplaceMethod uses: 40 bytes, keeping the name pointer at +0x18; +0x14 is the
    // interpreter info this build caches per method). patch_test checks which call paths see a swap made mid-game.
    public static partial class Handlers
    {
        const int kMethodHeader = 40, kNameOffset = 0x18;

        static byte[] SwapMethod(MethodBase original, MethodBase replacement)
        {
            IntPtr o = original.MethodHandle.Value, r = replacement.MethodHandle.Value;
            byte[] saved = new byte[kMethodHeader], repl = new byte[kMethodHeader];
            Marshal.Copy(o, saved, 0, kMethodHeader);
            Marshal.Copy(r, repl, 0, kMethodHeader);
            Marshal.Copy(repl, 0, o, kNameOffset);
            Marshal.Copy(repl, kNameOffset + 4, new IntPtr(o.ToInt32() + kNameOffset + 4), kMethodHeader - kNameOffset - 4);
            AppDomain.CurrentDomain.SetData("ts3fixes.autshadow.resweep", true);  // docs/128 G-M4: ts3fixes re-runs its L3 sweep
            return saved;
        }

        static void RestoreMethod(MethodBase original, byte[] saved)
        {
            Marshal.Copy(saved, 0, original.MethodHandle.Value, kMethodHeader);
            AppDomain.CurrentDomain.SetData("ts3fixes.autshadow.resweep", true);
        }

        public class PtVirt
        {
            public virtual int F() { return 1; }
            public int G() { return 2; }
        }

        static int PtA() { return 1; }
        static int PtB() { return 2; }
        static int PtCallWarm() { return PtA(); }
        static int PtCallCold() { return PtA(); }
        static int PtVirtCall(PtVirt v) { return v.F(); }

        // method_handle {type (or "self"), method, warm?}: MonoMethod pointer and the interpreter info at +0x14 (0 until first run), for the probe's read
        static object MethodHandle(Args a)
        {
            const BindingFlags all = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            string tn = a.Str("type");
            Type t = tn == "self" ? typeof(Handlers) : null; // this plugin's own test methods
            if (t == null) foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type x = asm.GetType(tn, false);
                if (x != null) t = x; // last match: a hot-reloaded plugin loads after its predecessors
            }
            if (t == null) throw new McpException("no type " + tn);
            MethodInfo m = t.GetMethod(a.Str("method"), all);
            if (m == null) throw new McpException("no method " + a.Str("method"));
            if (a.Bool("warm", false)) { PtCallWarm(); PtB(); PtVirtCall(new PtVirt()); new PtVirt().G(); }
            IntPtr h = m.MethodHandle.Value;
            Dictionary<string, object> r = Util.Obj();
            r["method"] = "0x" + h.ToInt32().ToString("x");
            r["info"] = "0x" + Marshal.ReadInt32(new IntPtr(h.ToInt32() + 0x14)).ToString("x");
            return r;
        }

        // Cold variants: never run before patch_test_cold swaps them, as game methods are at startup.
        public class PcBase
        {
            public int Field = 40;
            public virtual int Run() { return Field + 1; }
            public int Orig() { return -1; } // stub: receives the original's header, so the replacement can call it
        }
        public class PcFix : PcBase
        {
            public int Run2() { return Orig() + 100; } // replacement: wraps the original
        }
        static int PcCallDirect(PcBase b) { return b.Run(); }

        static object PatchTestCold(Args a)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            MethodInfo run = typeof(PcBase).GetMethod("Run", all), orig = typeof(PcBase).GetMethod("Orig", all);
            MethodInfo fix = typeof(PcFix).GetMethod("Run2", all);
            Dictionary<string, object> r = Util.Obj();
            if (a.Bool("swap", true))
            {
                SwapMethod(orig, run); // stub now runs the original body
                SwapMethod(run, fix);  // Run now runs the replacement
            }
            PcBase b = new PcBase();
            r["virtual_call"] = PcCallDirect(b);     // expect 141 (original 41 + 100)
            r["stub_call"] = b.Orig();               // expect 41
            r["derived_call"] = PcCallDirect(new PcFix());
            return r;
        }

        static object PatchTest(Args a)
        {
            const BindingFlags all = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type h = typeof(Handlers);
            Dictionary<string, object> r = Util.Obj();
            PtVirt v = new PtVirt();
            // compile the warm callers before the swap; the cold one only after
            r["before_warm"] = PtCallWarm();
            r["before_virt"] = PtVirtCall(v);
            MethodInfo a1 = h.GetMethod("PtA", all), b1 = h.GetMethod("PtB", all);
            MethodInfo f = typeof(PtVirt).GetMethod("F", all), g = typeof(PtVirt).GetMethod("G", all);
            byte[] sa = SwapMethod(a1, b1), sf = SwapMethod(f, g);
            try
            {
                r["after_warm"] = PtCallWarm();
                r["after_cold"] = PtCallCold();
                r["after_virt"] = PtVirtCall(v);
                r["after_reflect"] = a1.Invoke(null, null);
            }
            finally
            {
                RestoreMethod(a1, sa);
                RestoreMethod(f, sf);
            }
            r["restored_warm"] = PtCallWarm();
            r["restored_virt"] = PtVirtCall(v);
            return r;
        }

        // ---- The fixes themselves live in the player mod (mods/ts3fixes); this test agent only reads and switches them
        // through the AppDomain data that mod shares (ts3fixes.applied, .counters, .switches, .recent, .moved).
        // patches {name: bool, ...}: switch fixes for this session (overrides the mod's tuning), returns state and counters.
        static object Patches(Args a)
        {
            AppDomain d = AppDomain.CurrentDomain;
            Dictionary<string, object> r = Util.Obj();
            r["applied"] = d.GetData("ts3fixes.applied");
            int[] c = d.GetData("ts3fixes.counters") as int[] ?? new int[2016];
            Dictionary<string, bool> sw = d.GetData("ts3fixes.switches") as Dictionary<string, bool>;
            if (sw == null) { sw = new Dictionary<string, bool>(); d.SetData("ts3fixes.switches", sw); }
            string[] names = { "gohome", "rabbithole", "visit", "visitlot", "gotolot", "gotocommunity", "broadcast", "virtualhome", "failreplan", "watchdog", "watchdogidle", "backoff", "stations", "spotspread", "savecleanup", "overwatchstuck", "asyncreplanforeign", "portaladaptorleak", "casgrid", "casmemo", "spatialcache", "routablecache", "stuckcheckbusy", "stuckcheckasync", "safespotcap", "farthestsorted", "indoorcap", "fastreftable", "cachedreflection", "motivebar", "buffgrid", "namecache", "detailprio", "detailprioverify", "hoursmemo", "hoursmemoverify", "venuememo", "venuememoverify", "caneearly", "tooltipthrottle", "safespotreuse", "safespotreuseverify", "wildflowerprobe", "wildflowertask", "doormirror", "autshadow", "autshadowverify", "autledger", "replandefer", "replanspread", "unseenenv", "unseenmotives", "unseenbroadcast", "unseenprobe", "unseencars", "overwatchspread", "overwatchchain", "overwatchspreadverify", "overwatchspreadfakeupstream", "errortrapspread", "errortrapspreadverify", "errortrapspreadfakeupstream", "easpreadlots", "easpreadlotsverify", "easpreadpets", "easpreaddecay", "easpreaddecayverify", "routesrc", "legreplanskip", "bouncerskip", "idlememo", "stuckchecknear", "failrepeat", "jobyield", "drivertaskasync", "stuckcheckown", "overwatchstuckslice", "seasonmarkercache", "safespotyield", "disposespread", "overwatchstucknotice", "preplancb", "owprobeplans", "stuckcheckcap", "unseenfbstuck", "unseenfbplan", "unseenfbteleport", "unseenfbsites", "unseenfbpre", "unseenfbarrive", "unseenfbleave", "unseenfbtrip", "unseenfbtriphold", "unseenfbloopdet", "unseenfbloopbreak", "unseenfbfinish", "unseenroute", "posturepopcycle", "seasonimposter", "seasonimpostercold", "preferredservice", "socialworkeronlot", "eanullguards", "showstagethrowables", "firepitchair", "ageupkeepoutfits", "outfitwaitpoll", "bakesaleseller", "deadrefguards", "deadrefsweep", "savecleanupaggressive", "autoffenv", "autoffbcast", "autoffdry", "vacexport", "vacstoreguard", "vacreturnskip", "vacteendelete", "vacexitreturn", "vaclistenernull", "vacimportfix", "wakeprobe", "wakepark", "wakepoll", "autoffenvapply", "autoffbcastapply", "autoffnoref", "autofftick", "autlive", "autliveguard", "autunseenpace", "autlivelate", "autunseen", "autunseencmp", "autunseenrh", "uniformbackup", "menumaven", "nullservice", "rabbitholedoor" };
            foreach (string n in names)
            {
                if (a.Has(n)) sw[n] = a.Bool(n, true);
                bool on;
                r[n] = sw.TryGetValue(n, out on) ? on : n != "savecleanup" && n != "casgrid" && n != "casmemo" && n != "spatialcache" && n != "routablecache" && n != "stuckcheckbusy" && n != "stuckcheckasync" && n != "safespotcap" && n != "farthestsorted" && n != "indoorcap" &&n != "fastreftable" && n != "cachedreflection" && n != "motivebar" && n != "buffgrid" && n != "namecache" && n != "detailprio" && n != "detailprioverify" && n != "hoursmemo" && n != "hoursmemoverify" && n != "venuememo" && n != "venuememoverify" && n != "caneearly" && n != "tooltipthrottle" && n != "safespotreuse" && n != "safespotreuseverify" && n != "wildflowerprobe" && n != "wildflowertask" && n != "doormirror" && n != "autshadow" && n != "autshadowverify" && n != "autledger" && n != "replandefer" && n != "replanspread" && n != "unseenenv" && n != "unseenmotives" && n != "unseenbroadcast" && n != "unseenprobe" && n != "unseencars" && !n.StartsWith("overwatchspread") && n != "overwatchchain" && !n.StartsWith("errortrapspread") && !n.StartsWith("easpread") && n != "routesrc" && n != "legreplanskip" && n != "bouncerskip" && n != "idlememo" && n != "stuckchecknear" && n != "failrepeat" && n != "jobyield" && n != "drivertaskasync" && n != "stuckcheckown" && n != "overwatchstuckslice" && n != "seasonmarkercache" && n != "safespotyield" && n != "disposespread" && n != "overwatchstucknotice" && n != "preplancb" && n != "stuckcheckcap" && n != "unseenfbstuck" && n != "unseenfbplan" && n != "unseenfbteleport" && n != "unseenfbsites" && n != "unseenfbpre" && n != "unseenfbarrive" && n != "unseenfbleave" && n != "unseenfbtrip" && n != "unseenfbtriphold" && n != "unseenfbloopdet" && n != "unseenfbloopbreak" && n != "unseenfbfinish" && n != "unseenroute" && n != "posturepopcycle" && n != "seasonimposter" && n != "seasonimpostercold" && n != "preferredservice" && n != "socialworkeronlot" && n != "eanullguards" && n != "showstagethrowables" && n != "firepitchair" && n != "ageupkeepoutfits" && n != "outfitwaitpoll" && n != "bakesaleseller" && n != "deadrefguards" && n != "deadrefsweep" && n != "savecleanupaggressive" && n != "nullservice" && n != "rabbitholedoor" && !n.StartsWith("autoff") && n != "autlive" && n != "autlivelate" && n != "autunseen" && n != "autunseencmp" && !n.StartsWith("vac") && !n.StartsWith("wake") && n != "uniformbackup" && n != "menumaven"; // unset = the mod's tuning (all on by default except savecleanup, the CAS grid and the spatial cache)
            }
            r["gohome_runs"] = c[0]; r["gohome_shortcuts"] = c[1]; r["gohome_no_spot"] = c[2]; r["gohome_loop_guard"] = c[3];
            r["rabbithole_calls"] = c[4]; r["rabbithole_shortcuts"] = c[5]; r["rabbithole_no_spot"] = c[6];
            string[] lotTrips = { "visit", "visitlot", "gotolot", "gotocommunity" };
            for (int i = 0; i < lotTrips.Length; i++)
            {
                r[lotTrips[i] + "_runs"] = c[7 + 3 * i];
                r[lotTrips[i] + "_shortcuts"] = c[8 + 3 * i];
                r[lotTrips[i] + "_no_spot"] = c[9 + 3 * i];
            }
            r["houseboat_shortcuts"] = c[21];
            r["broadcast_pulses"] = c[22];
            r["broadcast_skipped"] = c[23];
            r["backoff_plans"] = c[24]; r["backoff_failures"] = c[25]; r["backoff_shortcuts"] = c[26]; // skipped plans
            r["virtualhome_runs"] = c[27]; r["virtualhome_shortcuts"] = c[28];
            r["failreplan_off"] = c[29]; r["failreplan_shortcuts"] = c[30]; // route-fail walks skipped
            r["watchdog_shortcuts"] = c[31]; // stuck Sims reset or faded
            if (c.Length > 32) r["stations_shortcuts"] = c[32]; // station detours skipped
            if (c.Length > 36) { r["watchdog_home_none"] = c[33]; r["watchdog_home_shell"] = c[34]; r["watchdog_home_house"] = c[35]; r["watchdog_second"] = c[36]; } // watchdog home kinds (none = faded), second resets in a day
            if (c.Length > 39) { r["safespot_hits"] = c[37]; r["safespot_misses"] = c[38]; r["safespot_checks"] = c[39]; } // SafeSpot verdict cache; checks = full route plans run
            if (c.Length > 43) { r["savecleanup_pairs"] = c[40]; r["savecleanup_minisims"] = c[41]; r["savecleanup_runs"] = c[42]; r["savecleanup_skipped"] = c[43]; }
            if (c.Length > 45) { r["overwatch_checks"] = c[44]; r["overwatch_yields"] = c[45]; } // NRaas Stuck Check plans seen / spread to their own tick
            if (c.Length > 51) { r["watchdog_idle_unroutable"] = c[46]; r["watchdog_idle_hidden_room"] = c[47]; r["watchdog_idle_stationary"] = c[48]; r["watchdog_route_fails"] = c[49]; r["watchdog_idle_verdicts"] = c[50]; r["watchdog_route_fail_sims"] = c[51]; } // watchdog idle cases (switch watchdogidle): resets per case, routability verdicts asked, Sims with failing plans tracked
            if (c.Length > 52) r["savecleanup_lifeevent_lists"] = c[52]; // empty life-event lists trimmed (docs/14 7a)
            if (c.Length > 53) r["savecleanup_snapshots"] = c[53]; // old memory pictures removed by the snapshot cap (docs/14 7b)
            if (c.Length > 55) { r["portaladaptor_unsubscribes"] = c[54]; r["savecleanup_route_callbacks"] = c[55]; } // route adaptor leak on ocean/houseboat portals (docs/09 section 5)
            if (c.Length > 58) { r["asyncroute_started"] = c[56]; r["asyncroute_yields"] = c[57]; r["asyncroute_pollcap"] = c[58]; } // async route plans (docs/18, ts3native [RouteAsync])
            if (c.Length > 61) { r["casgrid_fills"] = c[59]; r["casmemo_hits"] = c[60]; r["casmemo_lookups"] = c[61]; } // CAS hair/makeup incremental fills, hair preset cache (docs/21)
            if (c.Length > 64) { r["spatialcache_hits"] = c[62]; r["spatialcache_misses"] = c[63]; r["spatialcache_clears"] = c[64]; } // lot object query cache: hits, native lot walks, clears on object destroy (docs/20)
            if (c.Length > 67) { r["routablecache_hits"] = c[65]; r["routablecache_plans"] = c[66]; r["stuckcheck_busy_skipped"] = c[67]; } // fewer route plans (FewerPlans.cs, docs/04a section 7)
            if (c.Length > 70) { r["reftable_converted"] = c[68]; r["reftable_max_entries"] = c[69]; r["reftable_grows"] = c[70]; } // fast ReferenceHashtable (RefTable.cs)
            if (c.Length > 73) { r["typecache_started"] = c[71]; r["typecache_hits"] = c[72]; r["typecache_stored"] = c[73]; } // serializer per-type answer cache (CachedReflection.cs)
            if (c.Length > 79) { r["motivebar_cached"] = c[74]; r["motivebar_loads"] = c[75]; r["buffgrid_inplace"] = c[76]; r["buffgrid_rebuilds"] = c[77]; r["namecache_hits"] = c[78]; r["namecache_misses"] = c[79]; } // HUD caches (UiCaches.cs, docs/49), Sim name cache (NameCache.cs, docs/54)
            if (c.Length > 82) { r["detailprio_hits"] = c[80]; r["detailprio_sets"] = c[81]; r["detailprio_mismatches"] = c[82]; } // lot detail priority from one lot set per tick (ScriptHotSpots.cs, docs/74)
            if (c.Length > 84) { r["caneearly_skipped"] = c[83]; r["caneearly_passed"] = c[84]; } // cane-walk checks answered without the inventory search / run by the game (ScriptHotSpots.cs, docs/74)
            if (c.Length > 87) { r["hoursmemo_hits"] = c[85]; r["hoursmemo_misses"] = c[86]; r["hoursmemo_mismatches"] = c[87]; } // lot hours of operation memo per tick (ScriptHotSpots.cs, docs/74)
            if (c.Length > 92) { r["safespotreuse_hits"] = c[90]; r["safespotreuse_searches"] = c[91]; r["safespotreuse_mismatches"] = c[92]; } // SafeSpot placements from a stored FindGoodLocation result / native searches run / verify: a fresh search found another spot
            if (c.Length > 89) { r["tooltipthrottle_skipped"] = c[88]; r["tooltipthrottle_walks"] = c[89]; } // tooltip window walks skipped while the cursor rests / run (ScriptHotSpots.cs, docs/74)
            if (c.Length > 98) { r["wildflower_fires"] = c[93]; r["wildflower_tries"] = c[94]; r["wildflower_placed"] = c[95]; r["wildflower_failed"] = c[96]; r["wildflower_maxms"] = c[97]; r["wildflower_saveaborts"] = c[98]; } // spring wildflower drain (docs/110): fires that took a lot, tries, placed, failed, longest fire/slice ms, save aborts
            if (c.Length > 105) { r["doormirror_imports"] = c[102]; r["doormirror_invalidations"] = c[103]; r["doormirror_builds"] = c[104]; r["doormirror_refused"] = c[105]; } // P3 door mirror (DoorMirror.cs, docs/105 §10.5): CommonDoor.ImportContent postfixes, lot invalidations, lot tables built, builds refused (a door not readable)
            if (c.Length > 121) { r["autshadow_a0"] = c[106]; r["autshadow_a1"] = c[107]; r["autshadow_a2"] = c[108]; r["autshadow_a3"] = c[109]; r["autshadow_a4"] = c[110]; r["autshadow_a5"] = c[111]; r["autshadow_l_pairs"] = c[112]; r["autshadow_l_equal"] = c[113]; r["autshadow_l_testagree"] = c[114]; r["autshadow_l_top1agree"] = c[115]; r["autshadow_skips"] = c[116]; r["autshadow_warmin"] = c[117]; r["autshadow_requests"] = c[118]; r["autshadow_busy"] = c[119]; r["autshadow_guardskips"] = c[120]; r["autshadow_exceptions"] = c[121]; } // [AutShadow] pilot (AutShadow.cs, docs/122 §5): Mode R classes, Mode W L report, request and skip counts
            if (c.Length > 126) { r["autshadow_sf_skips"] = c[122]; r["autshadow_gm4_methods"] = c[123]; r["autshadow_gm4_us"] = c[124]; r["autshadow_gm4_foreign"] = c[125]; r["autshadow_gm5_types"] = c[126]; }
            if (c.Length > 2002) r["uniformbackup_cleared"] = c[2002]; // UniformBackup.cs, issue #101, docs/502
            if (c.Length > 2014) r["nullservice_dropped"] = c[2014]; // NullService.cs, docs/504
            if (c.Length > 2015) r["rabbitholedoor_skipped"] = c[2015]; // RabbitHoleDoor.cs, docs/505
            if (c.Length > 604) { r["autshadow_gm6_methods"] = c[602]; r["autshadow_gm6_mismatch"] = c[603]; r["autshadow_gm6_us"] = c[604]; }
            if (c.Length > 605) r["autshadow_gm6_faults"] = c[605];
            if (c.Length > 606) r["autshadow_gm6_owners_skipped"] = c[606];
            if (c.Length > 682) { r["outfitwaitpoll_stale"] = c[680]; r["outfitwaitpoll_polls"] = c[681]; r["outfitwaitpoll_ended"] = c[682]; } // OutfitWait.cs, issue #115
            if (c.Length > 2009) { r["outfitwaitpoll_clear_instant"] = c[2008]; r["outfitwaitpoll_clear_delayed"] = c[2009]; } // OutfitWait.cs
            if (c.Length > 2011) { r["outfitwaitpoll_cb_dropped"] = c[2010]; r["outfitwaitpoll_cause_dropped"] = c[2011]; } // OutfitWait.cs
            if (c.Length > 2013) { r["outfitwaitpoll_stale_onscreen"] = c[2012]; r["outfitwaitpoll_ended_onscreen"] = c[2013]; } // OutfitWait.cs, docs/224 §5
            if (c.Length > 2005) { r["outfitwaitpoll_cause_own"] = c[2003]; r["outfitwaitpoll_cause_replaced"] = c[2004]; r["outfitwaitpoll_cause_cleared"] = c[2005]; } // OutfitWait.cs, docs/224 §5
            if (c.Length > 684) { r["bakesaleseller_reads"] = c[683]; r["bakesaleseller_rearmed"] = c[684]; } // BakeSale.cs, issue #102
            if (c.Length > 686) { r["autunseen_rh_game"] = c[685]; r["autunseen_cmp_wpgf_w0"] = c[686]; } // AutLive.cs autunseenrh + compare split (docs/222 §10)
            if (c.Length > 131) { r["stuckcheckasync_queued"] = c[127]; r["stuckcheckasync_run"] = c[128]; r["stuckcheckasync_dropped"] = c[129]; r["stuckcheckasync_maxdepth"] = c[130]; r["stuckcheckasync_maxwait_simsec"] = c[131]; } // 3 AM stuck check in our task (FewerPlans.cs, docs/134 §8)
            if (c.Length > 136) { r["replandefer_deferred"] = c[132]; r["replandefer_kept_sync"] = c[133]; r["replanspread_held"] = c[134]; r["replanspread_replayed"] = c[135]; r["replanspread_exceptions"] = c[136]; } // ReplanDefer.cs (docs/141 §6)
            if (c.Length > 145) { r["safespotcap_calls"] = c[137]; r["safespotcap_limited"] = c[138]; r["farthest_calls"] = c[139]; r["farthest_plans"] = c[140]; r["farthest_cache"] = c[141]; r["farthest_capped"] = c[142]; r["indoorcap_calls"] = c[143]; r["indoorcap_plans"] = c[144]; r["indoorcap_rooms_given_up"] = c[145]; } // candidate-loop caps (CandidateLoops.cs, docs/141 section 7)
            if (c.Length > 155) { r["unseen_env_calls"] = c[146]; r["unseen_env_unseen"] = c[147]; r["unseen_env_skipped"] = c[148]; r["unseen_motive_calls"] = c[149]; r["unseen_motive_unseen"] = c[150]; r["unseen_motive_skipped"] = c[151]; r["unseen_broadcast_run"] = c[152]; r["unseen_broadcast_skipped"] = c[153]; r["unseen_motive_critical"] = c[154]; r["unseen_gate_restarts"] = c[155]; } // unseen script throttle (UnseenThrottle.cs, docs/144)
            if (c.Length > 166) { r["owspread_alarms"] = c[156]; r["owspread_types_scheduled"] = c[157]; r["owspread_types_run"] = c[158]; r["owspread_inv_passes"] = c[159]; r["owspread_inventories"] = c[160]; r["owspread_repairs"] = c[161]; r["owspread_ticks"] = c[162]; r["owspread_max_tick_us"] = c[163]; r["owspread_pending_after"] = c[164]; r["owspread_upstream"] = c[165]; r["owspread_theirs"] = c[166]; } // NRaas Overwatch nightly cleanups spread (OverwatchSpread.cs, docs/145)
            if (c.Length > 176) { r["etspread_passes"] = c[167]; r["etspread_inventories"] = c[168]; r["etspread_ticks"] = c[169]; r["etspread_max_tick_us"] = c[170]; r["etspread_names_skipped"] = c[171]; r["etspread_gone"] = c[172]; r["etspread_upstream"] = c[173]; r["etspread_theirs"] = c[174]; r["etspread_corrections"] = c[175]; r["etspread_failed"] = c[176]; } // NRaas ErrorTrap load-up inventory check spread (ErrorTrapSpread.cs, docs/146)
            r["errortrapspread_log"] = d.GetData("ts3fixes.errortrapspread"); // per pass: inventories, skipped names, ticks; verify and upstream lines
            if (c.Length > 200) { r["unseen_car_asks"] = c[197]; r["unseen_car_unseen"] = c[198]; r["unseen_car_parked"] = c[199]; r["unseen_car_seen"] = c[200]; } // unseen traffic cars stay parked (UnseenThrottle.cs, docs/143 §8.3)
            r["overwatchspread_log"] = d.GetData("ts3fixes.overwatchspread"); // per pass: repairs per kind, ticks, verify counts; upstream/yield lines
            if (c.Length > 196)
            {
                r["easpread_lots_passes"] = c[177]; r["easpread_lots"] = c[178]; r["easpread_lots_sims"] = c[179]; r["easpread_lots_ticks"] = c[180]; r["easpread_lots_max_tick_us"] = c[181]; r["easpread_lots_left_to_clean"] = c[182]; r["easpread_lots_stopped"] = c[183];
                r["easpread_pets_moved"] = c[184]; r["easpread_pets_added"] = c[185]; r["easpread_pets_ticks"] = c[186]; r["easpread_pets_max_ticks"] = c[187]; r["easpread_pets_busy"] = c[188];
                r["easpread_decay_passes"] = c[189]; r["easpread_decay_sims"] = c[190]; r["easpread_decay_pairs"] = c[191]; r["easpread_decay_dropped"] = c[192]; r["easpread_decay_ticks"] = c[193]; r["easpread_decay_max_tick_us"] = c[194]; r["easpread_decay_finished_early"] = c[195]; r["easpread_decay_threw"] = c[196];
            } // EA maintenance alarms spread (EaSpread.cs, docs/147): venue cleanup, pet pool alarm, relationship decay
            if (c.Length > 212) { r["etspread_largest_inventory_items"] = c[211]; r["etspread_max_items_tick"] = c[212]; } // ErrorTrap inventory check: biggest inventory run whole (their code), items in the busiest tick
            if (c.Length > 210) r["owspread_max_items_tick"] = c[210]; // NRaas Overwatch inventory items checked in the busiest tick of our pass (the budget unit)
            if (c.Length > 209) r["easpread_lots_max_units_tick"] = c[209]; // venue-cleanup units in the busiest tick (objects tested, cleaned or repaired, per-lot steps)
            if (c.Length > 208) r["easpread_decay_max_pairs_tick"] = c[208]; // relationships stepped in the busiest decay tick (the budget's unit)
            if (c.Length > 207) r["easpread_pets_extra_dropped"] = c[207]; // pet pool alarm fires from a second alarm chain, dropped (docs/147 §3.1)
            if (c.Length > 227) { r["owspread_max_type_us"] = c[223]; r["owspread_slow_types"] = c[224]; r["unseen_catchup_max_tick"] = c[225]; r["unseen_catchup_held"] = c[226]; r["unseen_catchup_max_us"] = c[227]; } // Overwatch types run whole at their slot (OverwatchSpread.cs); unseen motive catch-ups per sim tick (UnseenThrottle.cs, docs/144 §8)
            if (c.Length > 237) // route sources and redundant-plan cuts (RouteSources.cs, docs/151 §6); per bucket in routesrc_buckets
            {
                r["routesrc_plans"] = c[228]; r["routesrc_classify_us"] = c[229]; r["routesrc_legend_near"] = c[230]; r["routesrc_houseboat_legs"] = c[231];
                r["routesrc_bouncer_at_slot"] = c[232]; r["routesrc_idle_memo_hits"] = c[233]; r["legreplanskip_skipped"] = c[234];
                r["bouncerskip_skipped"] = c[235]; r["idlememo_skipped"] = c[236]; r["idlememo_capped"] = c[237];
                int[] rs = d.GetData("ts3fixes.routesrc") as int[];
                if (rs != null) r["routesrc_buckets"] = rs; // [0-15] plans, [16-31] us, [32-47] fails per bucket; [48-53] repeats <0.5/<2/<10 s x prev ok/fail; [54-59] leg-end action Boat/Vehicle/PortalTraversal/Portal/GetInBoat/other; [60-62] remainder start <0.5/<2/more m; [63] houseboat legs inside a replan
            }
            if (c.Length > 243) // docs/163 sync-plan callers: stuckchecknear (FewerPlans.cs), failrepeat (RouteBackoff.cs)
            {
                r["stuckchecknear_checks"] = c[238]; r["stuckchecknear_plans"] = c[239]; r["stuckchecknear_allfailed"] = c[240];
                r["failrepeat_recorded"] = c[241]; r["failrepeat_answered"] = c[242]; r["failrepeat_skipped_ms"] = c[243];
            }
            if (c.Length > 245) { r["owchain_alarms"] = c[244]; r["owchain_yields"] = c[245]; } // overwatchchain (OverwatchSpread.cs, docs/145 §10)
            if (c.Length > 247) { r["a10foreign_sent"] = c[246]; r["a10foreign_started"] = c[247]; } // asyncreplanforeign (RouteBackoff.cs, docs/176)
            if (c.Length > 250) { r["jobyield_steps"] = c[248]; r["jobyield_ticks"] = c[249]; r["jobyield_capped"] = c[250]; } // jobyield (RouteBackoff.cs StepAside, docs/183)
            if (c.Length > 254) { r["drivertask_car_sent"] = c[251]; r["drivertask_rider_sent"] = c[252]; r["stuckcheckown_sent"] = c[253]; r["ownertask_started"] = c[254]; } // drivertaskasync, stuckcheckown (RouteBackoff.cs OwnerTaskStart, docs/163 §7)
            if (c.Length > 259) { r["owstuck_slices"] = c[255]; r["owstuck_sims"] = c[256]; r["owstuck_max_stretch_us"] = c[257]; r["owstuck_runs"] = c[258]; r["owstuck_ms"] = c[259]; } // overwatchstuckslice (OverwatchStuck.cs, docs/145 §12)
            if (c.Length > 275) { r["seasonmarkercache_hits"] = c[270]; r["seasonmarkercache_passed"] = c[271]; r["seasonmarkercache_verifies"] = c[272]; r["seasonmarkercache_mismatches"] = c[273]; r["seasonmarkercache_seeds"] = c[274]; r["seasonmarkercache_markers"] = c[275]; } // seasonmarkercache (SeasonMarkerCache.cs, docs/74 §11 (a))
            if (c.Length > 278) { r["safespotyield_yields"] = c[276]; r["safespotyield_given_up"] = c[277]; r["safespotyield_not_yielded"] = c[278]; } // safespotyield (Fixes.cs SafeSpotYield, docs/74 §11 (b))
            if (c.Length > 289) // disposespread (DisposeSpread.cs, docs/74 §12)
            {
                r["disposespread_queued"] = c[280]; r["disposespread_destroyed"] = c[281]; r["disposespread_flushed"] = c[282];
                r["disposespread_skipped"] = c[283]; r["disposespread_max_depth"] = c[284]; r["disposespread_max_tick_us"] = c[285];
                r["disposespread_gone"] = c[286]; r["disposespread_full"] = c[287]; r["disposespread_dropped"] = c[288]; r["disposespread_max_wait_ticks"] = c[289];
            }
            if (c.Length > 292) { r["owstucknotice_held"] = c[290]; r["owstucknotice_summaries"] = c[291]; r["owstucknotice_named"] = c[292]; } // overwatchstucknotice (OverwatchNotice.cs, docs/145 §13)
            if (c.Length > 294) { r["owprobeplans_hidden"] = c[293]; r["owprobeplans_runs"] = c[294]; } // owprobeplans (OverwatchProbe.cs, docs/163 §9)
            if (c.Length > 296) { r["preplancb_precalls"] = c[295]; r["preplancb_doors_restored"] = c[296]; } // preplancb (RouteBackoff.cs PreCb, docs/185 §5)
            if (c.Length > 301) { r["stuckcheckcap_marked"] = c[297]; r["stuckcheck_noplan"] = c[298]; r["stuckcheck_notstuck"] = c[299]; r["stuckcheck_rearmed"] = c[300]; r["stuckcheck_reset"] = c[301]; } // stuck-check outcomes per check (FewerPlans.cs StuckCheckRun, docs/163 §10)
            if (c.Length > 312) { r["unseenfb_stuck_deferred"] = c[303]; r["unseenfb_stuck_seen"] = c[304]; r["unseenfb_marked"] = c[305]; r["unseenfb_cut"] = c[306]; r["unseenfbteleport_shortcuts"] = c[307]; r["unseenfbteleport_no_spot"] = c[308]; r["unseenfb_noretry"] = c[309]; r["unseenfb_seen"] = c[310]; r["unseenfb_refused"] = c[311]; r["unseenfb_max_ms"] = c[312]; } // unseen-Sim fallback (UnseenFallback.cs, docs/202); teleport names as verify-shortcut.ts reads them
            if (c.Length > 353) { r["ufloop_flagged"] = c[348]; r["ufloopbreak_sims"] = c[349]; r["ufloopbreak_lots"] = c[350]; r["ufloopbreak_lots_stopped"] = c[351]; r["ufloopbreak_steps"] = c[352]; r["ufloopbreak_refused"] = c[353]; } // docs/202 §5.7 E loop detector + breaker (UnseenFallbackLoop.cs)
            if (c.Length > 370) { r["unseenfbfinish_claims"] = c[363]; r["unseenfbfinish_shortcuts"] = c[364]; r["unseenfbfinish_no_spot"] = c[365]; r["unseenfbfinish_cancelled"] = c[366]; r["unseenfbfinish_flagged"] = c[367]; r["unseenfbfinish_rj_end"] = c[368]; r["unseenfbfinish_rj_route"] = c[369]; r["unseenfbfinish_max_hold_min"] = c[370]; } // docs/202 §3.4.2 finish the walk (UnseenFallbackFinish.cs; verify-shortcut names)
            if (c.Length > 372) { r["posture_chain_loops"] = c[371]; r["posturepopcycle_unwinds"] = c[372]; } // issue #89 (UnseenFallback.cs UfHeld, PosturePop.cs)
            if (c.Length > 325) { r["unseenfbsites_lists"] = c[323]; r["unseenfbsites_ids"] = c[324]; r["unseenfbsites_refused"] = c[325]; } // unseen-Sim list for ts3native's C1-C4 sites (UnseenFallback.cs, docs/202 §3.4)
            if (c.Length > 318) { r["unseenfbpre_shortcuts"] = c[313]; r["unseenfbpre_no_spot"] = c[314]; r["unseenfbpre_failfast"] = c[315]; r["unseenfbpre_cancelled"] = c[316]; r["unseenfbpre_holds"] = c[317]; r["unseenfbpre_max_hold_min"] = c[318]; }
            if (c.Length > 322) { r["unseenfbarrive_shortcuts"] = c[319]; r["unseenfbarrive_no_spot"] = c[320]; r["unseenfbleave_shortcuts"] = c[321]; r["unseenfbleave_no_spot"] = c[322]; } // docs/202 §3.6 (verify-shortcut names)
            if (c.Length > 327) { r["unseenfbpre_no_verdict"] = c[326]; r["unseenfbpre_filled"] = c[327]; } // docs/202 §3.5.1 risk (a) fix // docs/202 §3.5 D; names as verify-shortcut.ts reads them // unseen-Sim fallback (UnseenFallback.cs, docs/202); teleport names as verify-shortcut.ts reads them
            if (c.Length > 340) { r["unseenfbpre_rj_task"] = c[328]; r["unseenfbpre_rj_short"] = c[329]; r["unseenfbpre_rj_sim"] = c[330]; r["unseenfbpre_rj_townie"] = c[331]; r["unseenfbpre_rj_vehicle"] = c[332]; r["unseenfbpre_rj_samelot"] = c[333]; r["unseenfbpre_rj_lot"] = c[334]; r["unseenfbpre_rj_indoor"] = c[335]; r["unseenfbpre_rj_caller"] = c[336]; r["unseenfbpre_rj_noyield"] = c[337]; r["unseenfbpre_rj_colour"] = c[338]; r["unseenfbpre_rj_unroutable"] = c[339]; r["unseenfbleave_rj_goalseen"] = c[340]; } // docs/202 §3.5.1 D-family reject gates
            if (c.Length > 347) { r["unseenfbtrip_runs"] = c[341]; r["unseenfbtrip_shortcuts"] = c[342]; r["unseenfbtrip_no_spot"] = c[343]; r["unseenfbtrip_held"] = c[344]; r["unseenfbtrip_cancelled"] = c[345]; r["unseenfbtrip_rj_colour"] = c[346]; r["unseenfbtrip_rj_noyield"] = c[347]; } // docs/202 §3.6.4 lot trips with D's hold
            if (c.Length > 379) { r["seasonimposter_queued"] = c[373]; r["seasonimposter_warm"] = c[374]; r["seasonimposter_cold"] = c[375]; r["seasonimposter_flushed"] = c[376]; r["seasonimposter_dropped"] = c[377]; r["seasonimposter_max_ms"] = c[378]; r["seasonimposter_max_depth"] = c[379]; } // SeasonImposter.cs, issue #82
            if (c.Length > 390) { r["preferredservice_extra"] = c[380]; r["preferredservice_repicked"] = c[381]; r["preferredservice_swept"] = c[382]; r["socialworkeronlot_deferred"] = c[383]; r["socialworkeronlot_dropped"] = c[384]; r["eanullguards_bribe"] = c[385]; r["eanullguards_topic"] = c[386]; r["eanullguards_motives"] = c[387]; r["eanullguards_carry"] = c[388]; r["showstagethrowables_destroyed"] = c[389]; r["firepitchair_destroyed"] = c[390]; } // EaFixes.cs, issues #86-#90
            if (c.Length > 410) { r["ageupkeepoutfits_kept"] = c[391]; r["ageupkeepoutfits_nooutfit"] = c[392]; r["deadrefguards_relget"] = c[393]; r["deadrefguards_aging"] = c[394]; r["deadrefsweep_relkeys"] = c[395]; r["deadrefsweep_relentries"] = c[396]; r["deadrefsweep_aging"] = c[397]; r["deadrefsweep_alarmowners"] = c[398]; r["deadrefsweep_react"] = c[399]; r["deadrefsweep_coworkers"] = c[400]; r["deadrefsweep_topics"] = c[401]; r["deadrefsweep_fishers"] = c[402]; r["deadrefsweep_simfields"] = c[403]; r["deadrefsweep_passengers"] = c[404]; r["deadrefsweep_passes"] = c[405]; r["deadrefsweep_slices"] = c[406]; r["deadrefsweep_max_slice_us"] = c[407]; r["deadrefsweep_total_ms"] = c[408]; r["deadrefsweep_max_save_us"] = c[409]; r["deadrefsweep_refused"] = c[410]; } // EaFixes.cs #100, DeadRefs.cs #91
            if (c.Length > 422) { r["savecleanup_spread_passes"] = c[411]; r["savecleanup_spread_slices"] = c[412]; r["savecleanup_spread_max_slice_us"] = c[413]; r["savecleanup_spread_total_ms"] = c[414]; r["savecleanup_spread_refused"] = c[415]; r["savecleanupaggressive_snapshots"] = c[416]; r["savecleanupaggressive_pairs"] = c[417]; r["savecleanupaggressive_history"] = c[418]; r["savecleanupaggressive_dead_outfits"] = c[419]; r["savecleanupaggressive_dead_disposed"] = c[420]; r["savecleanupaggressive_cleanse_overwatch"] = c[421]; r["savecleanupaggressive_dead_passport"] = c[422]; }
            if (c.Length > 426) { r["played_households"] = c[423]; r["played_households_added"] = c[424]; r["savecleanup_light_pictures"] = c[425]; r["savecleanupaggressive_played_skipped"] = c[426]; } // PlayedHouseholds.cs (docs/14 section 8.6) // SaveCleanupSpread.cs (docs/14 section 8)
            if (c.Length > 438) { r["compaction_saves"] = c[427]; r["compaction_started"] = c[428]; r["compaction_offered"] = c[429]; r["compaction_accepted"] = c[430]; r["compaction_declined"] = c[431]; r["compaction_refused_state"] = c[432]; r["compaction_failed"] = c[433]; r["compaction_recorded"] = c[434]; r["compaction_requeued"] = c[435]; r["compaction_requeue_skipped"] = c[436]; r["compaction_not_restorable"] = c[437]; r["compaction_requeue_fail"] = c[438]; }
            if (c.Length > 439) r["compaction_restored"] = c[439]; // Compaction.cs (docs/204)
            if (c.Length > 465) // AutOffload.cs (docs/208): Mode R per candidate, dry checks at the game's own call
            {
                string[] on = { "autoff_env_items", "autoff_env_a0", "autoff_env_a2", "autoff_env_a4", "autoff_env_a5", "autoff_env_exc", "autoff_env_dry", "autoff_env_dry_eq", "autoff_env_game_eq",
                    "autoff_bcast_items", "autoff_bcast_a0", "autoff_bcast_a1", "autoff_bcast_a2", "autoff_bcast_a4", "autoff_bcast_a5", "autoff_bcast_exc", "autoff_bcast_dry", "autoff_bcast_dry_eq" };
                for (int i = 0; i < on.Length; i++) r[on[i]] = c[448 + i];
                r["autoff_reasons"] = d.GetData("ts3fixes.autoffload.reasons");
            }
            if (c.Length > 480) // EaVacation.cs (issue #97, docs/210)
            {
                string[] vn = { "vacexport_exported", "vacexport_dreams", "vacstoreguard_readded", "vacstoreguard_threw", "vacstoreguard_load_bringback", "vacstoreguard_load_ended",
                    "vacreturnskip_skipped", "vacreturnskip_ended", "vacteendelete_deferred", "vacteendelete_rejoined", "vacteendelete_returns", "vacexitreturn_back",
                    "vaclistenernull_skipped", "vacimportfix_ids", "vacimportfix_expiry" };
                for (int i = 0; i < vn.Length; i++) r[vn[i]] = c[466 + i];
            }
            if (c.Length > 541) { r["autliveguard_windows"] = c[538]; r["autliveguard_skipwindows"] = c[539]; r["autliveguard_skipped"] = c[540]; r["autliveguard_ratio_pm"] = c[541]; } // AutLive.cs cost guard (docs/124 §17.10)
            if (c.Length > 574) // AutLive.cs autlivelate (docs/217 L1) and G8 counters (docs/124 §8)
            {
                string[] ln = { "autlivelate_slices", "autlivelate_ref_age", "autlivelate_ref_epoch", "autlivelate_ref_state", "autlivelate_ref_lot",
                    "autlivelate_ref_head", "autlivelate_ref_actor", "autlivelate_ref_other", "autlivelate_consumed", "autlivelate_stale",
                    "autlivelate_age1", "autlivelate_age2", "autlivelate_age3", "autlivelate_us_consumed", "autlivelate_us_late_game",
                    "autlivelate_us_invalid_game", "g8_pushes", "g8_null_picks", "g8_gohome", "g8_simhours_x100", "g8_lat0", "g8_lat1",
                    "g8_lat2", "g8_lat3", "g8_lat4" };
                for (int i = 0; i < ln.Length; i++) r[ln[i]] = c[550 + i];
            }
            if (c.Length > 610) { r["autshadow_rain_refused_span"] = c[607]; r["autshadow_rain_refused_tid"] = c[608]; r["autshadow_rain_hook"] = c[609]; r["autshadow_rain_done"] = c[610]; } // AutShadow.cs `rain` aborts by path
            if (c.Length > 658) // UnseenFallback.cs unseenroute2 (docs/202 §3.9.5-§3.9.7)
            {
                string[] u2 = { "unseenroute_other_task", "unseenroute_rj_nested", "unseenroute_group", "unseenroute_rj_group", "unseenroute_child",
                    "unseenroute_b_pass", "unseenroute_c_learn", "unseenroute_c_hit", "unseenroute_d_check", "unseenroute_d_repair",
                    "unseenroute_ghost_start", "unseenroute_ghost_exit" };
                for (int i = 0; i < u2.Length; i++) r[u2[i]] = c[647 + i];
                int n = c[653] + c[654];
                r["unseenroute_c_hit_pm"] = n > 0 ? (int)((long)c[654] * 1000 / n) : -1;  // C warm-up: hits per mille of C asks
            }
            if (c.Length > 646) // UnseenFallback.cs unseenroute (docs/202 §3.9)
            {
                string[] ur = { "unseenroute_acts", "unseenroute_no_verdict", "unseenroute_colour_unknown", "unseenroute_fast", "unseenroute_snap_mid", "unseenroute_snap_stand" };
                for (int i = 0; i < ur.Length; i++) r[ur[i]] = c[641 + i];
            }
            if (c.Length > 2001) { r["menumaven_reads"] = c[2000]; r["menumaven_done"] = c[2001]; } // MenuMaven.cs, docs/501
            if (c.Length > 669) // AutLive.cs unseen-direct pacing + G8 by actor class (docs/222 §8)
            {
                string[] pg = { "autunseen_pace_us", "autunseen_pace_forced", "g8_unseen_searches", "g8_unseen_pushes", "g8_unseen_null_picks",
                    "g8_unseen_gohome", "g8_seen_searches", "g8_seen_pushes", "g8_seen_null_picks", "g8_seen_gohome", "g8_unseen_simhours_x100" };
                for (int i = 0; i < pg.Length; i++) r[pg[i]] = c[659 + i];
            }
            if (c.Length > 679) // AutLive.cs autunseencmp Dry per-definition compare (docs/222 §9)
            {
                string[] dc = { "autunseen_cmp_searches", "autunseen_cmp_pairs", "autunseen_cmp_wfail_gpass", "autunseen_cmp_wpass_gfail", "autunseen_cmp_both_pass",
                    "autunseen_cmp_both_fail", "autunseen_cmp_late_pairs", "autunseen_cmp_late_mis", "autunseen_cmp_obj_skipped", "autunseen_cmp_snapped" };
                for (int i = 0; i < dc.Length; i++) r[dc[i]] = c[670 + i];
            }
            if (c.Length > 640) // AutLive.cs unseen-direct go-live (docs/222, switch autunseen)
            {
                string[] un = { "autunseen_posted", "autunseen_seen_post", "autunseen_snap_resume", "autunseen_snap_slice", "autunseen_dropped",
                    "autunseen_direct", "autunseen_pairs", "autunseen_offlist", "autunseen_obj_game", "autunseen_test_pass", "autunseen_test_fail",
                    "autunseen_vanilla", "autunseen_nopick", "autunseen_tagged", "autunseen_flip_kept", "autunseen_flip_cancel",
                    "autunseen_guard_windows", "autunseen_guard_skipwindows", "autunseen_guard_skipped", "autunseen_guard_ratio_pm",
                    "autunseen_cost_us", "autunseen_saved_us", "autlive_b14_ads", "autlive_b14_us", "autunseen_dry_runs",
                    "autunseen_dry_top10", "autunseen_dry_top1", "autunseen_exc", "autunseen_dry_seen_runs", "autunseen_test_us" };
                for (int i = 0; i < un.Length; i++) r[un[i]] = c[611 + i];
            }
            if (c.Length > 601) // AutLive.cs per-search feature mask (AutLiveCore.Feat): searches / mismatches / give-ups per bit
            {
                string[] fb = { "late", "prelearn", "tierreqlots", "rain", "desirefn", "satb", "guard", "cold", "partial" };
                for (int i = 0; i < fb.Length; i++) { r["feat_" + fb[i] + "_searches"] = c[575 + i]; r["feat_" + fb[i] + "_mis"] = c[584 + i]; r["feat_" + fb[i] + "_drop"] = c[593 + i]; }
            }
            if (c.Length > 537) { r["autoff_tick_items"] = c[534]; r["autoff_tick_a0"] = c[535]; r["autoff_tick_a2"] = c[536]; r["autoff_tick_unfinished"] = c[537]; } // AutOffload.cs autofftick (docs/206 §15)
            if (c.Length > 490) // AutOffload.cs (docs/208 §8): ready store and the apply at the game's own call
            {
                string[] ap = { "autoff_env_ready", "autoff_env_applied", "autoff_env_apply_none", "autoff_env_apply_stale", "autoff_env_apply_dry_eq",
                    "autoff_bcast_ready", "autoff_bcast_applied", "autoff_bcast_apply_none", "autoff_bcast_apply_stale", "autoff_bcast_dry_order" };
                for (int i = 0; i < ap.Length; i++) r[ap[i]] = c[481 + i];
            }
            if (c.Length > 447) { r["venuememo_tuning_hits"] = c[440]; r["venuememo_tuning_misses"] = c[441]; r["venuememo_tuning_mismatches"] = c[442]; r["venuememo_clears"] = c[443]; r["venuememo_scores"] = c[444]; r["venuememo_passed"] = c[445]; r["venuememo_score_mismatches"] = c[446]; r["venuememo_score_checks"] = c[447]; } // VenueMemo.cs (docs/122 section 14.24)
            if (c.Length > 533) // WakePark.cs (docs/211): unseen wake-ups by class (seen/unseen x high/low LOD), park and poll
            {
                string[] k4 = { "_seen_hi", "_seen_lo", "_unseen_hi", "_unseen_lo" }, kr = { "_full_seen", "_full_unseen", "_bcast_seen", "_bcast_unseen" };
                for (int i = 0; i < 4; i++)
                {
                    r["wake_su_pass" + k4[i]] = c[491 + i]; r["wake_su_us" + k4[i]] = c[495 + i]; r["wake_su_samples" + k4[i]] = c[530 + i];
                    r["wake_poll" + k4[i]] = c[499 + i]; r["wake_idle" + k4[i]] = c[505 + i]; r["wake_react" + kr[i]] = c[509 + i]; r["wake_react_us" + kr[i]] = c[513 + i];
                }
                r["wake_wait_seen"] = c[503]; r["wake_wait_unseen"] = c[504]; r["wake_yielded"] = c[517]; r["wake_classify"] = c[518]; r["wake_classify_us"] = c[519];
                r["wake_parks"] = c[520]; r["wake_park_ticks"] = c[521]; r["wake_park_early"] = c[522]; r["wake_wakeall"] = c[523]; r["wake_park_refused"] = c[524];
                r["wake_poll_long"] = c[525]; r["wake_poll_woken"] = c[526]; r["wake_poll_deadline"] = c[527]; r["wake_poll_shared"] = c[528]; r["wake_poll_over"] = c[529];
            }
            Dictionary<string, int> ufCallers = d.GetData("ts3fixes.unseenfb.callers") as Dictionary<string, int>; // caller frames D refused (gate 336)
            if (ufCallers != null) { List<object> l = new List<object>(); foreach (KeyValuePair<string, int> kv in ufCallers) l.Add(kv.Value + " " + kv.Key); r["unseenfbpre_callers"] = l; }
            Dictionary<string, int> urRj = d.GetData("ts3fixes.unseenroute.rj") as Dictionary<string, int>; // relaxed D refusals "gate:reason" (docs/202 §3.9.7b)
            if (urRj != null) { List<object> l = new List<object>(); foreach (KeyValuePair<string, int> kv in urRj) l.Add(kv.Value + " " + kv.Key); r["unseenroute_rj_detail"] = l; }
            if (c.Length > 302) r["owstuck_max_select_us"] = c[302]; // overwatchstuckslice: longest stretch at the IsSelectable hook (OverwatchStuck.cs, docs/145 §12)
            r["easpread_log"] = d.GetData("ts3fixes.easpread"); // one line per pass and path (ours / game's), verify censuses
            r["autshadow_reasons"] = d.GetData("ts3fixes.autshadow.reasons"); // A4 aborts per native reason index (row, pos, memo, rng, ... other)
            r["wildflower_log"] = d.GetData("ts3fixes.wildflower"); // one line per fire with wildflowerprobe or wildflowertask on
            r["recent"] = d.GetData("ts3fixes.recent");
            r["moved"] = d.GetData("ts3fixes.moved");
            return r;
        }
    }
}
