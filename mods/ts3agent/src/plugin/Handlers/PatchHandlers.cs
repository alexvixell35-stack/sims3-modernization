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
        static bool? ModOn(string n)
        {
            // The mod's own answer (Fixes.ShortcutOn: switch, else tuning incl. release defaults); null if the mod is not loaded.
            try
            {
                if (sShortcutOn == null)
                    foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        Type ty = asm.GetType("Ts3Fixes.Fixes", false);
                        if (ty != null) { sShortcutOn = ty.GetMethod("ShortcutOn", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public); break; }
                    }
                return sShortcutOn == null ? (bool?)null : (bool)sShortcutOn.Invoke(null, new object[] { n });
            }
            catch { return null; }
        }
        static MethodInfo sShortcutOn;
        static object Patches(Args a)        {
            AppDomain d = AppDomain.CurrentDomain;
            Dictionary<string, object> r = Util.Obj();
            r["applied"] = d.GetData("ts3fixes.applied");
            int[] c = d.GetData("ts3fixes.counters") as int[] ?? new int[2058];
            Dictionary<string, bool> sw = d.GetData("ts3fixes.switches") as Dictionary<string, bool>;
            if (sw == null) { sw = new Dictionary<string, bool>(); d.SetData("ts3fixes.switches", sw); }
            string[] names = { "stucktaskflag", "watchdogbusydefer", "minirelset", "nearestlotlazy", "safespotnospot", "fglinbound", "fglinboundhh", "doorshare", "jobyieldroad", "jobyieldroadow", "jobyieldspawn", "jobyieldfp", "jobyieldready", "boattaximemo", "unseenoutfitdefer", "detailprioexact", "buyiconleak", "casrowleak", "compositorleak", "genealogycycle", "spouseunlink", "relremovesafe", "familyinvghost", "sellcursorleak", "attractionalarm", "uncleguard", "mooddrift", "dnpdisposelistener", "seasondup", "dreamrootorphan", "blogorphan", "dnpupdateguard", "buycellrecycle", "buyclearrecycle", "buygridsweep", "moodpending", "joyridecostume", "alarmkilllog", "synctime", "socialfinish", "socialreceivefail", "petfireplan", "splashroof", "shuffleboardbcast", "cookclicklistener", "viproomrefresh", "courselistener", "observatorymsg", "bubblepipeguard", "etderefdiag", "awardkeep", "ceremonyspot", "eventcrowdring", "eventrouteretry", "simfestkeep", "weddingarchkeep", "hsgradkeep", "unigradkeep", "bsrefund", "comboprotest", "combovenue", "combosituation", "combojob", "comboprotestlot", "comboopp", "hiddenroomnpc", "hiddenroomspawn", "aptleave", "partyoutcome", "partymotive", "filtergridleak", "compositorghost", "caspuckevents", "famcursorleak", "casexitsweep", "seasonlistener", "casfiltergrid", "vehparkreserve", "vehgetinleak", "vehseatdestroy", "vehfrankentaxi", "vehprotestcar", "watchdogasync", "watchdogasyncshadow", "watchdoglockskip", "dancefloorlistguard", "boatparkgone", "boatgetoutdiag", "boatgetoutguard", "swapinvokediag", "alarmtime", "boatreplanlog", "owresettime", "compactionfail", "ageupswap", "gohome", "rabbithole", "visit", "visitlot", "gotolot", "gotocommunity", "broadcast", "virtualhome", "failreplan", "watchdog", "watchdogidle", "backoff", "stations", "spotspread", "savecleanup", "overwatchstuck", "asyncreplanforeign", "portaladaptorleak", "casgrid", "casmemo", "spatialcache", "routablecache", "stuckcheckbusy", "stuckcheckasync", "safespotcap", "farthestsorted", "indoorcap", "fastreftable", "cachedreflection", "motivebar", "buffgrid", "namecache", "detailprio", "detailprioverify", "hoursmemo", "hoursmemoverify", "venuememo", "venuememoverify", "caneearly", "tooltipthrottle", "safespotreuse", "safespotreuseverify", "wildflowerprobe", "wildflowertask", "doormirror", "autshadow", "autshadowverify", "autledger", "replandefer", "replanspread", "unseenenv", "unseenmotives", "unseenbroadcast", "unseenprobe", "unseencost", "unseencars", "overwatchspread", "overwatchchain", "overwatchspreadverify", "overwatchspreadfakeupstream", "errortrapspread", "errortrapspreadverify", "errortrapspreadfakeupstream", "easpreadlots", "easpreadlotsverify", "easpreadpets", "easpreaddecay", "easpreaddecayverify", "routesrc", "legreplanskip", "bouncerskip", "idlememo", "stuckchecknear", "failrepeat", "jobyield", "drivertaskasync", "stuckcheckown", "overwatchstuckslice", "seasonmarkercache", "safespotyield", "disposespread", "overwatchstucknotice", "preplancb", "owprobeplans", "stuckcheckcap", "unseenfbstuck", "unseenfbplan", "unseenfbteleport", "unseenfbsites", "unseenfbpre", "unseenfbarrive", "unseenfbleave", "unseenfbtrip", "unseenfbgohere", "modquitguard", "unseenfbtriphold", "unseenfbloopdet", "unseenfbloopbreak", "unseenfbfinish", "unseenroute", "posturepopcycle", "seasonimposter", "seasonimpostercold", "preferredservice", "socialworkeronlot", "eanullguards", "showstagethrowables", "firepitchair", "ageupkeepoutfits", "outfitwaitpoll", "bakesaleseller", "deadrefguards", "deadrefsweep", "savecleanupaggressive", "autoffenv", "autoffbcast", "autoffdry", "vacexport", "vacstoreguard", "vacreturnskip", "vacteendelete", "vacexitreturn", "vaclistenernull", "vacimportfix", "wakeprobe", "wakepark", "wakepoll", "autoffenvapply", "autoffbcastapply", "autoffnoref", "autofftick", "autofft2", "autofft2ic", "autofflot", "autt2inuse", "autt2fire", "autt2dragwarm", "autt2drag", "autt2cache", "autt2tuning", "autt2lod", "autlive", "autliveguard", "autunseenpace", "autlivelate", "autunseen", "autunseencmp", "autunseenrh", "autseengame", "autunseenpacecredit", "uniformbackup", "menumaven", "nullservice", "rabbitholedoor", "photosession", "eggspot", "graduationtable", "jazzparam", "buffupdate", "carnoroute", "convturn", "npcdriverpool", "bookcarry", "bookhand", "bookhanddiag", "routefaillog", "ageupabort", "unseenposcache", "unseentablecopy", "rewardgiver", "datingprofile", "leavejobdispose", "invnotyet", "metricskillmissing", "fireworksfizzle", "petpoolorphan", "reltablesapart", "asyncjigsync", "unseenpark", "unseenboat", "unseenboatstart", "goalhhexempt", "errortrapsims", "celebritydeadsim", "seasonsdeadsim", "ghostreactdeadsim", "casuihandlers", "errortraplookup", "errortrapctl", "dreamnodedeadsim", "overwatchdeadsim", "simrefsdeadsim", "outfitwaitsaved", "deadsimcount", "deadsimhollow", "dnpsubjectcache", "hudunsub", "mapviewkeep", "tinyuieventreg", "customctlsweep", "exitcbsweep", "dnppetkeys", "dnpobjkeys", "dancefloorlistdiag", "autt2l0", "autt2lcov", "autt2cewarm", "autt2frontdoor", "autt2suite", "autt2callpet", "minihomelot", "buffstate", "storeworldevents", "deadrefcooking", "deadrefconvo", "takeariskguard", "permamemo", "permamemoverify", "buffremovenoop", "catnamecache", "catnamecacheverify", "autroommemo", "autroommemoverify", "autslicetime", "invcache", "invcacheverify", "autlivelod", "autlivetune", "autliveseen", "autliveseendry", "spawncapcount", "spawncapprobe", "routelen", "vaclistenerpad", "birthroute", "putdownplace", "bowlinglane", "geniebanish", "spacesimpartner", "bandjam", "statueline", "cleanflag", "birdtvnull", "gohomewalk", "photoboothfunds", "shelllighting", "bonfirespice", "djcleanup", "vampirehuntglow", "hottubwoohoofx", "bribeguard", "oppdeadlisteners", "daycarequit", "privateeyecase", "weddingcancel", "ghostspawnargs", "moveinpartner", "reaperflow", "serviceleave", "visitgreetalarm", "fieldtripadult", "autonomyfailblock", "visittoolate", "spoonreset", "homeworkhelp", "grouptalkstale", "convmerge", "portraitpose", "picniccamera", "upgradefailalarm", "repairtimecache", "burglaridle", "gigwaitleave", "sculptpose", "trainboost", "eatoutsidecount", "autoffunseen", "zdemptyout", "zdemptyoutverify", "decaymemo", "decaymemoverify", "burnttilefloor", "hhsearchprobe", "envlistmemo", "bcposdry", "envlistmemoapply", "envlistmemoverify", "envlistmemoexact", "envscancost", "agingalarmcancel", "alarmzeroperiod", "asyncreplan", "asyncroute", "autonomyinuseblock", "autt2local", "awkwardscience", "backpacklotleak", "barputdownwait", "bathcleanup", "bathsituationfx", "birthdayagerecord", "boardingschoolmoney", "boatstaleswimmer", "bouncerrole", "bubblingspringsreset", "buffremovedelayed", "businessplannerrun", "butlerpayalarm", "cancelposturedef", "carpoolschoolskip", "catchpreyodds", "cattoypickerrel", "celebalarmnull", "chemistryvfx", "chessdefeated", "chestuselist", "chocfountaingeyser", "cleanupvfx", "compositionnull", "computergametype", "coopeggquality", "copyhomeworkfx", "dartboardvfx", "datadiscputback", "daycareliking", "digsiteemptypick", "disposebase", "disposevirtualslot", "distressgate", "dogswimaroundlist", "dogswimslots", "donationbuffchance", "doorgen", "doorlockowner", "doorprivacynull", "dragoncurserun", "dreamcleanup", "eatpostonce", "eatpreycancel", "elevatorkitten", "elevatorresetriders", "elevatorridertimeout", "elevatorsavedlog", "equestriantrophyname", "eventcost", "facialidleweight", "fallenrockcollected", "fearfaintundo", "fightbroadcastend", "firefightercount", "fireplaceherb", "floorswitchuselist", "flowersinuse", "foodtraygrill", "fragmentreconstruct", "freshclothingresume", "fridgetestnull", "frolicexit", "futuredescendantsize0", "fvstandskillcurve", "gigmaster", "gm6trace", "gohereboatlist", "goodbyepure", "guitargnomeboost", "handsetreset", "harvesterunripe", "harvestextras", "harvestregion", "hauntedgnomeinv", "hayscattertime", "haystackvfx", "heartbrokenbcast", "hhcleanupdup", "holidaylights", "homeworkskillbonus", "horseranchchallenge", "hottubseatposture", "houdinignomeleak", "hovertraintest", "hypnotrickrelease", "igloolookats", "initstacktrace", "innerbeautyonce", "intmultiple", "jogterrain", "junglegymexitqueue", "kegbreakage", "labobjectpush", "leafburnfail", "leaveconvnull", "lifeguardquit", "lifeguardsurvey", "lightcustom", "lightningweights", "lookatcopy", "lotmgrcallbacks", "lotprune", "lottotooltip", "loyaldoglay", "ltrhistorybits", "maildogchaser", "maptagtaskalive", "megaphonerun", "meltalarm", "moodidleflavor", "moodtankreset", "moondiallistener", "moonriselistener", "mopunreachable", "motivemultonce", "mountfishcost", "newcartest", "newspaperdef", "nocommercialcar", "npcbusypartner", "nullsituation", "oppcategoryprotect", "oppchainworld", "oppfailcharge", "outerwearautoloop", "outerwearrestore", "outfitwaitend", "ovenmenunull", "partylotlistener", "petdreamtimer", "petdroolvfx", "petharvestvfx", "petpoolghost", "petpuddletile", "petwakebed", "photofunds", "photosubjectstars", "picnicants", "plantdormantflip", "plantdormanttime", "playpentalkcast", "pokerdeadplayer", "pondclearscan", "prankditchempty", "privacyothers", "protestexit", "putdowninuse", "putdownplaced", "putdownsurfaceuse", "queueproximity", "rainsheltercheap", "reactioncopy", "reactiongrumpy", "repairmanskill", "resetinusefree", "residentspawnslot", "rhexitunseen", "rolenullguard", "roleunprotect", "romancecooldown", "roulettehackreset", "roundupreturn", "rubbleuselist", "rummagebcast", "safespotpool", "sandwichalarm", "saunatubclear", "savedstacks", "scavengernolot", "sciencefxstop", "serviceassignexit", "serviceshowalarm", "shelldoorsummon", "shoodef", "showstagefxend", "shroomslots", "simdisposelink", "slavewakereason", "slotmachinedefs", "snublistener", "soakedpetselfreact", "soakedstat", "socialnoeffect", "spawnerskip", "spbucketindex", "spclonedict", "spcollectnull", "spcreatemovein", "speedlockowner", "speedrecalclock", "speedskiplevel", "speedskipnoactor", "spellbooksummonload", "spelltargetend", "spjobatwork", "splotdensity", "splotlookup", "spoccultpick", "spoilbroadcast", "spoonslavechecks", "spotcheck", "sppensionprompt", "sprecheck", "sprelcount", "sproommatejob", "stairwaitend", "starlightreset", "stereoupgrade", "streetartmasterboost", "suatgradebonus", "subroutefail", "swiperestore", "tagbagsurfaceleak", "tagunseen", "talkoccupationchance", "teenpartybreakup", "teleportcommunitylot", "tempforecast", "timeportalvfx", "tipsnpcend", "tpaptgoal", "tpdoorrights", "trainsimgone", "traiticondislike", "treewishingwell", "tricksocials", "tutorballoonalarm", "tvdisclistener", "tvsyncalarm", "tvwatchskill", "twinbellyshape", "unicornspawnfail", "uselistprune", "vacationalarms", "vampirefruitsnack", "vehiclefxhelper", "vhomewalk", "virtualhomeshare", "virtualmoveindup", "visitlisteners", "voodoofairyfadein", "wardrobewandvfx", "weathermanagerthrow", "weatherstonelistener", "weatherstonerelisten", "weddingcleanup", "weddinglistener", "wellstealcooldown", "werewolffind", "wildhorselot", "fireactivejob", "firegreet", "firetrapcount", "birthdayagers", "sneakoutlistener", "promcarpool", "curfewalarm", "trickortreatalarm", "snowfightjoinwait", "hauntedumbrella", "permazombiemoon", "steadygigmiss", "npcshowstuck", "gigpullaudience", "lunacysocials", "fairyauratimer", "npcseasonalparty", "professorterm", "protestlastday", "seasickpaused", "resortweddingalarm", "plumbotfacelistener", "efficientchip", "sentiencename", "jetpackflyingbuff", "horsetraitscore", "wildhorsegender", "littercap", "phoneloadreset", "phonecallschedule", "phonecallrearm", "woohootywait", "invitecallreply", "gradvisitors", "resumetns", "unitravelflag", "returnhomealarms", "phonefixup", "phonegive", "wildherdmother", "splitpartner", "movingflag", "sandscoins", "tombmusic", "artsellnoartist", "royaltyorphan", "promoteshift", "gotoworkstale", "swapdelegates", "socialreplanlog", "barorderlog", "petputdownwait", "puddlereach", "routecauselog", "scarepriority", "groupchatpriority", "trashpileimport", "catgymwatch", "badsecretkey", "traitlistenerremove", "portasknohome", "lecturebroadcaster", "residentspawnwalk", "midlifewishsave", "betrayednullsim", "topictestnull", "opptrackerrelisten", "traitremovenull", "homeworkcopyold", "laundrymoodeach", "partycleanupnull", "safelocunhide", "midlifeuiending", "nemesisnoromance", "displaycasereset", "mermaidsharknull", "jetpackflyherecleanup", "naniteroutefail", "springdanceclear", "nectarcopylevel10", "harvesteatvfx", "buffetemptypick", "krakenfriendsailor", "mummycursetrait", "bullridercleanup", "broomtipperformer", "balancingballcareer", "elvisgnomeguard", "elvisgnomecap", "gourmetpetrecipe", "chipfamilycrystal", "chipcomponentorphan", "chipanycount", "nanitemerge", "consignmentlastday", "bartendinghourlypay", "influencefollowerbonus", "broomarenachoice", "fortunetellerscamodds", "consignmentskillcurve", "consignmentstockleak", "herbqualityfloor", "snackbowlslobvfx", "worldpondskatetest", "pingpongopponent", "mermaiddehydratelook", "meteormagnetorphan", "daycarelatealarm", "boxofmysterywoohoo", "bodysculptorweight", "nectarconsignvalue", "straygnometns", "fireworkswatchlate", "rolenullpurge", "fightstaycap", "errortraploop", "loopidlebridge", "enumcmpnobox", "alarmcmpnobox", "autlastsearch", "callcensus", "spotasync", "spotbusydefer", "spotcut", "swslots", "autmetarows", "buffremovemerge", "lifeeventidfirst", "buffsrcempty", "buffpausedtimeout", "mooddriftnoalloc", "stuckwatchscan", "unseenloopskip", "unseenloopskip_offscreen", "pushwaitskip", "playerfirst", "zoomtier", "routefailprobe", "rhentryrelax", "rhentryrelaxnear", "routableasync", "bouncerplace", "stcsocials", "stcsocialscheck", "spotedgememo", "tspawninvdup", "sarcmummynpc", "ufplansplit", "unseenservice_offscreen", "unseenservicewait", "unseenservice", "visitcar", "visitwalkfinish", "timeralarmleak", "wishfeedbackkeep", "wishsubjectgone", "wishupdatelost", "socialworkerpromise", "dormantprobe", "motivebuf", "motivebufcheck", "buffbufcheck", "unseenpianist", "unseenmusicwatch", "unseenjam", "unseenshow", "boatroutenull", "outfitmissing", "ageupdupinst", "overwatchresetgone", "motivesnohousehold", "overwatchrelretry", "fightorphanwait", "poolnolane", "autonomyfailcooldown", "venuepicktime", "venuepickfast", "boatroamcap", "houseboatgangwayrescue", "houseboatlegmemo", "safespotdedupe", "rabbitholedirect", "rhoutside", "unseenlanesnap", "unseenlegsnap", "stagedarrival", "icecreamdirect", "nearvenue", "unseenoutfitnospin", "visitarrive", "visitonlot", "gohomeshell", "unseentraffic", "npccarexit", "spawnfix", "spawnoffworld", "festivalhidden", "bouncermemo", "frontstageowner", "careerskillnull", "stuckhbskip", "turnjigskip", "unseenrelax", "iafailmemo", "safespotplanyield", "unseenfbghost", "unseenfbnosync", "unseenhouseboatview", "boattrip", "boatplace", "retryscope", "boatlandstuck", "commodmapfast", "commodmapshadow", "ladderfootshoo", "ladderfootshoohh", "fireleftover", "aptgasleak", "aptpossess", "firedoorkeep", "horseshoesolo", "burglarapt", "firenre", "firecount", "blanktns", "sharelanes", "sharelaneshh", "laddershare", "laddersharehh", "spiralshare", "spiralsharehh", "simpass", "simpasshh", "angleentry", "angleentryhh", "lanewaitlog", "standwaitfix", "standwaitfixhh", "standwaitlog", "blockeduse", "blockedusehh", "makebedboth", "makebedbothhh", "makebedbothlog", "simbotworld", "deadrefcadence", "viewerpushdelta" };
            foreach (string n in names)
            {
                if (a.Has(n)) { sw[n] = a.Bool(n, true); Util.SwitchesChanged(); }
                bool on;
                r[n] = sw.TryGetValue(n, out on) ? on : ModOn(n) ?? (n != "boatroamcap" && n != "ladderfootshoo" && n != "ladderfootshoohh" && n != "commodmapfast" && n != "commodmapshadow" && n != "houseboatgangwayrescue" && n != "houseboatlegmemo" && n != "watchdogasyncshadow" && n != "watchdoglockskip" && n != "dancefloorlistdiag" && n != "boatgetoutdiag" && n != "boatgetoutguard" && n != "alarmtime" && n != "boatreplanlog" && n != "owresettime" && n != "autseengame" && n != "autunseenpacecredit" && n != "ageupswap" && n != "savecleanup" && n != "casgrid" && n != "casmemo" && n != "spatialcache" && n != "routablecache" && n != "stuckcheckbusy" && n != "stuckcheckasync" && n != "safespotcap" && n != "farthestsorted" && n != "indoorcap" && n != "unseenoutfitdefer" &&n != "fastreftable" && n != "cachedreflection" && n != "motivebar" && n != "buffgrid" && n != "namecache" && n != "detailprio" && n != "detailprioverify" && n != "hoursmemo" && n != "hoursmemoverify" && n != "venuememo" && n != "venuememoverify" && n != "permamemo" && n != "permamemoverify" && n != "buffremovenoop" && n != "catnamecache" && n != "catnamecacheverify" && n != "autroommemo" && n != "autroommemoverify" && n != "autslicetime" && n != "invcache" && n != "invcacheverify" && n != "caneearly" && n != "tooltipthrottle" && n != "safespotreuse" && n != "safespotreuseverify" && n != "wildflowerprobe" && n != "doormirror" && n != "autshadow" && n != "autshadowverify" && n != "autledger" && n != "replandefer" && n != "replanspread" && n != "unseenenv" && n != "unseenmotives" && n != "unseenbroadcast" && n != "unseenprobe" && n != "unseencost" && n != "unseentablecopy" && n != "unseencars" && n != "unseenpark" && n != "hhsearchprobe" && !n.StartsWith("overwatchspread") && n != "overwatchchain" && !n.StartsWith("errortrapspread") && !n.StartsWith("easpread") && n != "routesrc" && n != "legreplanskip" && n != "bouncerskip" && n != "idlememo" && n != "stuckchecknear" && n != "failrepeat" && n != "jobyield" && n != "drivertaskasync" && n != "stuckcheckown" && n != "overwatchstuckslice" && n != "seasonmarkercache" && n != "safespotyield" && n != "disposespread" && n != "overwatchstucknotice" && n != "preplancb" && n != "stuckcheckcap" && n != "unseenfbstuck" && n != "unseenfbplan" && n != "unseenfbteleport" && n != "unseenfbsites" && n != "unseenfbpre" && n != "unseenfbarrive" && n != "unseenfbleave" && n != "unseenfbtrip" && n != "unseenfbtriphold" && n != "unseenfbloopdet" && n != "unseenfbloopbreak" && n != "unseenfbfinish" && n != "unseenroute" && n != "posturepopcycle" && n != "seasonimposter" && n != "seasonimpostercold" && n != "preferredservice" && n != "socialworkeronlot" && n != "eanullguards" && n != "showstagethrowables" && n != "firepitchair" && n != "ageupkeepoutfits" && n != "outfitwaitpoll" && n != "bakesaleseller" && n != "deadrefguards" && n != "deadrefsweep" && n != "savecleanupaggressive" && n != "photosession" && n != "eggspot" && n != "graduationtable" && n != "jazzparam" && n != "buffupdate" && n != "carnoroute" && n != "ageupabort" && !n.StartsWith("autoff") && n != "autlive" && n != "autlivelate" && n != "autunseen" && n != "autunseencmp" && n != "unseenboat" && n != "unseenboatstart" && n != "autlivelod" && n != "autlivetune" && n != "autliveseen" && n != "autliveseendry" && !n.StartsWith("vac") && !n.StartsWith("wake") && n != "uniformbackup" && n != "menumaven" && n != "convturn" && n != "npcdriverpool" && n != "bookcarry" && n != "bookhand" && n != "bookhanddiag" && n != "goalhhexempt" && n != "errortrapsims" && n != "celebritydeadsim" && n != "seasonsdeadsim" && n != "ghostreactdeadsim" && n != "casuihandlers" && n != "errortraplookup" && n != "errortrapctl" && n != "dreamnodedeadsim" && n != "overwatchdeadsim" && n != "simrefsdeadsim" && n != "outfitwaitsaved" && n != "deadsimcount" && n != "deadsimhollow" && n != "dnpsubjectcache" && n != "hudunsub" && n != "mapviewkeep" && n != "tinyuieventreg" && n != "customctlsweep" && n != "exitcbsweep" && n != "dnppetkeys" && n != "dnpobjkeys" && n != "spawncapcount" && n != "spawncapprobe" && n != "routelen" && n != "vaclistenerpad" && n != "zdemptyout" && n != "zdemptyoutverify" && n != "decaymemo" && n != "decaymemoverify"); // unset = the mod's tuning (all on by default except savecleanup, the CAS grid and the spatial cache)
            }
            // a switch name not in the list above is still passed to ts3fixes (ShortcutOn reads any name from this table), and
            // reported under "unlisted", so a new fix's switch never silently stays off because this list lagged behind
            Dictionary<string, object> unlisted = Util.Obj();
            foreach (string k in a.Keys)
            {
                if (Array.IndexOf(names, k) >= 0 || !(a.Raw(k) is bool)) continue;
                sw[k] = a.Bool(k, true);
                unlisted[k] = sw[k];
            }
            if (unlisted.Count > 0) r["unlisted"] = unlisted;
            Util.SwitchesChanged();
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
            if (c.Length > 1381) { r["houseboatlegmemo_recorded"] = c[1378]; r["houseboatlegmemo_answered"] = c[1379]; r["houseboatlegmemo_answered_ms"] = c[1380]; r["houseboatlegmemo_exempt"] = c[1381]; } // docs/853 §2
            if (c.Length > 1391) { r["houseboatgangwayrescue_marked"] = c[1382]; r["houseboatgangwayrescue_rescued"] = c[1383]; r["houseboatgangwayrescue_refused"] = c[1384]; r["houseboatgangwayrescue_errors"] = c[1385]; r["houseboatgangwayrescue_refused_exempt"] = c[1386]; r["houseboatgangwayrescue_refused_houseboat"] = c[1387]; r["houseboatgangwayrescue_refused_hour"] = c[1388]; r["houseboatgangwayrescue_refused_busy"] = c[1389]; r["houseboatgangwayrescue_refused_lot_far_stale"] = c[1390]; r["houseboatgangwayrescue_refused_nospot"] = c[1391]; } // docs/853 §3
            if (c.Length > 1399) { r["boatroamcap_capped"] = c[1392]; r["boatroamcap_returns_pushed"] = c[1393]; r["boatroamcap_returns_failed"] = c[1394]; r["boatroamcap_exempt"] = c[1395]; r["boatroamcap_errors"] = c[1396]; r["boatroamcap_shore_reached"] = c[1397]; r["boatroamcap_service_logged"] = c[1398]; r["boatroamcap_night_or_motive"] = c[1399]; } // docs/853 §4
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
            if (c.Length > 2030) { r["photosession_dropped"] = c[2026]; r["photosession_ended"] = c[2027]; r["photosession_load_ended"] = c[2028]; r["eggspot_refused"] = c[2029]; r["graduationtable_no_table"] = c[2030]; } // docs/508
            if (c.Length > 2036) { r["jazzparam_seen"] = c[2035]; r["jazzparam_caught"] = c[2036]; } // JazzParam.cs, docs/511
            if (c.Length > 784) { r["unseen_refresh_run"] = c[778]; r["unseen_refresh_us"] = c[779]; r["unseen_refresh_gap_lt1"] = c[780]; r["unseen_refresh_gap_lt5"] = c[781]; r["unseen_refresh_gap_lt15"] = c[782]; r["unseen_refresh_gap_lt30"] = c[783]; r["unseen_refresh_gap_ge30"] = c[784]; } // UnseenThrottle.cs, docs/245
            if (c.Length > 849) { r["unseenboat_gate"] = c[836]; r["unseenboat_seen"] = c[837]; r["unseenboat_unseen"] = c[838]; r["unseenboat_shortcuts"] = c[839]; r["unseenboat_no_spot"] = c[840]; r["unseenboat_rj_route"] = c[841]; r["unseenboat_rj_sim"] = c[842]; r["unseenboat_houseboat"] = c[843]; r["unseenboat_rj_owned"] = c[844]; r["unseenboat_candidates"] = c[845]; r["unseenboat_released_seen"] = c[846]; r["unseenboat_rj_goal"] = c[847]; r["unseenboat_max_hold_min"] = c[848]; r["unseenboat_rj_unknown"] = c[849]; }
            if (c.Length > 853) { r["unseenboatstart_candidates"] = c[850]; r["unseenboatstart_shortcuts"] = c[851]; r["unseenboatstart_no_spot"] = c[852]; r["unseenboatstart_left_dirty"] = c[853]; } // docs/248 §10 // docs/248
            if (c.Length > 1269) { r["boattaximemo_recorded"] = c[1264]; r["boattaximemo_boatoff"] = c[1265]; r["boattaximemo_boatoff_ok"] = c[1266]; r["boattaximemo_answered"] = c[1267]; r["boattaximemo_answered_ms"] = c[1268]; r["boattaximemo_refused"] = c[1269]; } // BoatTaxiMemo.cs, docs/851
            if (c.Length > 1481) { r["safespotdedupe_same"] = c[1476]; r["safespotdedupe_other"] = c[1477]; r["safespotdedupe_none"] = c[1478]; r["safespotdedupe_after_none"] = c[1479]; r["safespotdedupe_stopped"] = c[1480]; r["safespotdedupe_second_skipped"] = c[1481]; }
            if (c.Length > 1455) { r["rabbitholedirect_asked"] = c[1441]; r["rabbitholedirect_entered"] = c[1442]; r["rabbitholedirect_noproxy"] = c[1443]; r["rabbitholedirect_followers"] = c[1444]; r["rabbitholedirect_posture"] = c[1445]; r["rabbitholedirect_seen_scope"] = c[1446]; r["rabbitholedirect_door_view"] = c[1447]; r["rabbitholedirect_before_no"] = c[1448]; r["rabbitholedirect_moved"] = c[1449]; r["rabbitholedirect_exit_reason"] = c[1450]; r["rabbitholedirect_anim_failed"] = c[1451]; r["rabbitholedirect_faded"] = c[1452]; r["rhoutside_calls"] = c[1453]; r["rhoutside_placed"] = c[1454]; r["rhoutside_no_spot"] = c[1455]; }
            if (c.Length > 1470) { r["unseenlanesnap_asked"] = c[1456]; r["unseenlanesnap_snapped"] = c[1457]; r["unseenlanesnap_near"] = c[1458]; r["unseenlanesnap_refused_shape"] = c[1459]; r["unseenlanesnap_refused_nextpath"] = c[1460]; r["unseenlanesnap_refused_scope"] = c[1461]; r["unseenlanesnap_refused_badpoint"] = c[1462]; r["unseenlanesnap_refused_spot"] = c[1463]; r["unseenlanesnap_refused_seen"] = c[1464]; r["unseenlanesnap_errors"] = c[1465]; r["unseenlanesnap_longest_cm"] = c[1466]; r["unseenlanesnap_elevator"] = c[1467]; r["unseenlanesnap_door"] = c[1468]; r["unseenlanesnap_other"] = c[1469]; r["unseenlanesnap_elevator_cab"] = c[1470]; }
            if (c.Length > 1475) { r["unseenlegsnap_asked"] = c[1471]; r["unseenlegsnap_not_unseen"] = c[1472]; r["unseenlegsnap_path1_not_walk"] = c[1473]; r["unseenlegsnap_spot_refused"] = c[1474]; r["unseenlegsnap_snapped"] = c[1475]; }
            if (c.Length > 1420) { r["stagedarrival_asked"] = c[1400]; r["stagedarrival_placed"] = c[1401]; r["stagedarrival_scope"] = c[1402]; r["stagedarrival_colour_unknown"] = c[1403]; r["stagedarrival_no_candidate"] = c[1404]; r["stagedarrival_too_short"] = c[1405]; r["stagedarrival_tested"] = c[1406]; r["stagedarrival_loop_guard"] = c[1407]; r["stagedarrival_placed_lottrip"] = c[1408]; r["stagedarrival_placed_gohome"] = c[1409]; r["stagedarrival_placed_rabbithole"] = c[1410]; r["stagedarrival_hold_gave_up"] = c[1411]; r["stagedarrival_ref_inside_lot"] = c[1412]; r["stagedarrival_ref_water"] = c[1413]; r["stagedarrival_ref_seen"] = c[1414]; r["stagedarrival_ref_colour"] = c[1415]; r["stagedarrival_ref_unroutable"] = c[1416]; r["stagedarrival_ring_rescue"] = c[1417]; r["stagedarrival_entry_placed"] = c[1418]; r["stagedarrival_entry_tested"] = c[1419]; r["stagedarrival_sim_colour"] = c[1420]; }
            if (c.Length > 1430) { r["icecreamdirect_calls"] = c[1421]; r["icecreamdirect_skipped"] = c[1422]; r["icecreamdirect_would_skip"] = c[1423]; r["icecreamdirect_truck_seen"] = c[1424]; r["icecreamdirect_spot_seen"] = c[1425]; r["icecreamdirect_household_lot"] = c[1426]; r["icecreamdirect_fallback"] = c[1427]; r["icecreamdirect_ending_service"] = c[1428]; r["icecreamdirect_routing"] = c[1429]; r["icecreamdirect_community_spot"] = c[1430]; }
            if (c.Length > 1440) { r["nearvenue_asked"] = c[1431]; r["nearvenue_lowered"] = c[1432]; r["nearvenue_raised"] = c[1433]; r["nearvenue_kept"] = c[1434]; r["nearvenue_out_of_scope"] = c[1435]; r["nearvenue_no_venue_type"] = c[1436]; r["nearvenue_water"] = c[1437]; r["nearvenue_colour_unknown"] = c[1438]; r["nearvenue_worker_aborts"] = c[1439]; r["nearvenue_groups"] = c[1440]; }
            if (c.Length > 1584) // docs/330 our loop cost: deadrefsweep per phase, cadence gates, snapshot; viewer push wakes and sends
            {
                for (int p = 0; p < 15; p++) { r["deadrefsweep_p" + (p + 1) + "_ms"] = c[1543 + p]; r["deadrefsweep_p" + (p + 1) + "_items"] = c[1558 + p]; }
                r["deadrefcadence_desc_skipped"] = c[1573]; r["deadrefcadence_sim_skipped"] = c[1574]; r["deadrefcadence_snapshots"] = c[1575]; r["deadrefcadence_snapshot_objects"] = c[1576];
                r["viewerpush_wakes"] = c[1577]; r["viewerpush_ms"] = c[1578]; r["viewerpush_max_wake_us"] = c[1579]; r["viewerpush_table_sends"] = c[1580];
                r["viewerpush_actor_walks"] = c[1581]; r["viewerpush_held_sends"] = c[1582]; r["viewerpush_actors_walked"] = c[1583]; r["viewerpush_walks_skipped"] = c[1584];
            }
            if (c.Length > 1529) { r["capaut_capped"] = c[1518]; r["capaut_capped_objs"] = c[1519]; r["capaut_capped_pairs"] = c[1520]; r["capaut_objs_hw"] = c[1521]; r["capaut_pairs_hw"] = c[1522]; r["capnearvenue_over"] = c[1523]; r["capnearvenue_hw"] = c[1524]; r["capspawnerskip_hw"] = c[1525]; r["capspawnerskip_clears"] = c[1526]; r["capbackoff_hw"] = c[1527]; r["capbackoff_clears"] = c[1528]; r["capcookclick_hw"] = c[1529]; } // docs/321 lane 0 (Caps.cs)
            if (c.Length > 1491) { r["unseenoutfitnospin_taken"] = c[1482]; r["unseenoutfitnospin_ref_helper"] = c[1483]; r["unseenoutfitnospin_ref_nochange"] = c[1484]; r["unseenoutfitnospin_ref_posture"] = c[1485]; r["unseenoutfitnospin_ref_form"] = c[1486]; r["unseenoutfitnospin_ref_category"] = c[1487]; r["unseenoutfitnospin_ref_pending"] = c[1488]; r["unseenoutfitnospin_ref_seen"] = c[1489]; r["unseenoutfitnospin_ran_at_once"] = c[1490]; r["unseenoutfitnospin_threw"] = c[1491]; }
            if (c.Length > 949) { r["unseenboat_norefusal_n"] = c[948]; r["unseenboat_norefusal_ms"] = c[949]; } // docs/247 §12.11: boat replans booked with no sUbLastWhy entry, their ms
            if (c.Length > 865) { r["goalhh_lot"] = c[862]; r["goalhh_target"] = c[863]; r["goalhh_wanted"] = c[864]; r["goalhh_check_failed"] = c[865]; } // UnseenFallback.cs UfGoalWhy, docs/197 §7
            if (c.Length > 861) { r["scopegap_driverless_ownable"] = c[854]; r["scopegap_driverless_notownable"] = c[855]; r["scopegap_driverless_hh_occupant"] = c[856]; r["scopegap_driverless_nonvehicle"] = c[857]; r["scopegap_viewer_only_plans"] = c[858]; r["scopegap_viewer_only_ge250"] = c[859]; r["scopegap_viewer_only_ms"] = c[860]; r["scopegap_driverless_total"] = c[861]; } // UnseenFallback.cs UfDriverless / UfSlowPlanLog
            if (c.Length > 823) { r["unseen_park_held"] = c[818]; r["unseen_park_snap"] = c[819]; r["unseen_park_exempt"] = c[820]; r["unseen_park_unseen"] = c[821]; r["unseen_park_seen"] = c[822]; r["unseen_park_arrivals"] = c[823]; if (c.Length > 825) { r["unseen_park_burst_max"] = c[824]; r["unseen_park_stagger_defer"] = c[825]; } if (c.Length > 1049) r["unseenpark_household_held"] = c[1049]; } // UnseenThrottle.cs, docs/243 §9
            if (c.Length > 2034) { r["buffupdate_seen"] = c[2033]; r["buffupdate_caught"] = c[2034]; } // BuffUpdate.cs, docs/510
            if (c.Length > 897) { r["dancefloordiag_calls"] = c[892]; r["dancefloordiag_returns"] = c[893]; r["dancefloordiag_throws"] = c[894]; r["dancefloordiag_steps"] = c[895]; r["dancefloordiag_mismatch"] = c[896]; r["dancefloordiag_freed"] = c[897]; } // DanceFloorDiag.cs, docs/253
            if (c.Length > 929) { r["dancefloorguard_calls"] = c[928]; r["dancefloorguard_changes"] = c[929]; }
            if (c.Length > 2040) { r["carnoroute_seen"] = c[2039]; r["carnoroute_false"] = c[2040]; } // CarNoRoute.cs, docs/513
            if (c.Length > 2032) { r["convturn_seen"] = c[2031]; r["convturn_taken"] = c[2032]; } // ConvTurn.cs, docs/509
            if (c.Length > 2216) foreach (KeyValuePair<string, object> kv in VehLeakCounters(c)) r[kv.Key] = kv.Value; // VehicleLeaks.cs, docs/544
            if (c.Length > 2042) { r["npcdriverpool_seen"] = c[2041]; r["npcdriverpool_dropped"] = c[2042]; } // NpcDriverPool.cs, docs/514
            for (int i = 3982; i < 3998 && i < c.Length; i++) r["slot" + i] = c[i]; // boatroutenull 3982-3984, outfitmissing 3985-3987, ageupdupinst 3988-3990, overwatchrelretry 3991-3993, overwatchresetgone 3994-3995, motivesnohousehold 3996-3997
            if (c.Length > 4057) { r["frontstageowner_null"] = c[4052]; r["careerskillnull_skipped"] = c[4053]; r["careerskillnull_household"] = c[4054]; r["stuckhbskip_skipped"] = c[4055]; r["turnjigskip_scope"] = c[4056]; r["turnjigskip_skipped"] = c[4057]; } // EaNullGuards.cs, FewerPlans.cs, JobYieldRoad.cs
            if (c.Length > 4082) { r["unseenrelax_relaxed"] = c[4078]; r["unseenrelax_relaxed_failed"] = c[4079]; r["iafailmemo_remembered"] = c[4080]; r["iafailmemo_answered"] = c[4081]; r["iafailmemo_saved_ms"] = c[4082]; } // unseenrelax / iafailmemo (RouteBackoff.cs)
            if (c.Length > 4077) { r["boattrip_placed"] = c[4070]; r["boattrip_start_ok"] = c[4071]; r["boattrip_step_ok"] = c[4072]; r["boattrip_all_failed"] = c[4073]; r["boattrip_leftover"] = c[4074]; r["boatplace_moved"] = c[4075]; r["retryscope_resent"] = c[4076]; r["boatlandstuck_out"] = c[4077]; } // boattrip / boatplace / retryscope / boatlandstuck (BoatTrip.cs, BoatPlace.cs, RetryScope.cs, BoatLandStuck.cs)
            for (int i = 2700; i < 2800 && i < c.Length; i++) r["slot" + i] = c[i]; // raw slots for the 27xx switches with no named export (docs/registry.md)
            for (int i = 3052; i < 3055 && i < c.Length; i++) r["slot" + i] = c[i]; // socialnoeffect
            for (int i = 1345; i < 1370 && i < c.Length; i++) r["slot" + i] = c[i]; // fightorphanwait 1345-1347, poolnolane 1348-1350, venuepicktime 1353-1356, venuepickfast 1357-1359, autonomyfailcooldown 1360-1369
            for (int i = 1374; i < 1376 && i < c.Length; i++) r["slot" + i] = c[i]; // autonomyfailcooldown follow-up: search type None not recorded 1374, NPC pianist breaks 1375
            for (int i = 1500; i < 1508 && i < c.Length; i++) r["slot" + i] = c[i]; // commodmapfast 1500-1507 (CommodMapFast.cs, docs/313)
            if (c.Length > 835) { r["alarmtime_timed"] = c[826]; r["alarmtime_lines"] = c[827]; r["alarmtime_passes"] = c[828]; r["boatreplan_lines"] = c[829]; r["boatreplan_could"] = c[830]; r["boatreplan_unseen"] = c[831]; r["owreset_lines"] = c[832]; r["hitchcause_capped"] = c[833]; r["boatreplan_seen_ms"] = c[834]; r["boatreplan_unseen_ms"] = c[835]; } // HitchCause.cs, docs/247 §9
            if (c.Length > 866) r["routefaillog_household_plans"] = c[866]; // RouteFailLog.cs RflPlan
            if (c.Length > 689) { r["routefaillog_routefail"] = c[687]; r["routefaillog_showoff"] = c[688]; r["routefaillog_capped"] = c[689]; } // RouteFailLog.cs, docs/226
            if (c.Length > 2044) { r["bookcarry_dropped"] = c[2043]; r["bookcarry_ended"] = c[2044]; } // BookCarry.cs, docs/515
            if (c.Length > 877) { r["bookhand_seen"] = c[876]; r["bookhand_fixed"] = c[877]; } // BookHand.cs, docs/251
            if (c.Length > 2038) { r["ageupabort_seen"] = c[2037]; r["ageupabort_caught"] = c[2038]; } // AgeUpAbort.cs, docs/512
            if (c.Length > 2045) r["deadrefsweep_dartboards"] = c[2045]; // DeadRefs.cs phase 9, docs/516
            if (c.Length > 2046) r["deadrefsweep_uselists"] = c[2046]; // DeadRefs.cs phase 10, docs/517
            if (c.Length > 2047) r["deadrefsweep_objectowners"] = c[2047]; // DeadRefs.cs phase 11, docs/519
            if (c.Length > 2048) r["deadrefsweep_handlers"] = c[2048]; // DeadRefs.cs phase 12, docs/522
            if (c.Length > 2049) r["deadrefsweep_celebrity"] = c[2049]; // DeadRefs.cs phase 13, docs/523
            if (c.Length > 2050) r["rewardgiver_added"] = c[2050]; // RewardGiver.cs, docs/525
            if (c.Length > 2052) { r["datingprofile_no_profile"] = c[2051]; r["datingprofile_no_record"] = c[2052]; } // DatingProfile.cs, docs/528
            if (c.Length > 2054) r["leavejobdispose_leaves"] = c[2054]; // LeaveJobDispose.cs, docs/529
            if (c.Length > 2058) r["invnotyet_refused"] = c[2058]; // InvNotYet.cs, docs/531
            if (c.Length > 2060) { r["metricskillmissing_names"] = c[2059]; r["metricskillmissing_values"] = c[2060]; } // MetricSkillMissing.cs, docs/532
            if (c.Length > 2061) r["fireworksfizzle_finished"] = c[2061]; // FireworksFizzle.cs, docs/533
            if (c.Length > 2064) { r["petpoolorphan_disposed"] = c[2062]; r["petpoolorphan_inworld"] = c[2063]; r["petpoolorphan_threw"] = c[2064]; } // PetPoolOrphan.cs, docs/534
            if (c.Length > 2065) r["reltablesapart_rejoined"] = c[2065]; // RelTablesApart.cs, docs/535
            if (c.Length > 2068) { r["errortrapsims_dropped"] = c[2067]; r["errortrapsims_left"] = c[2068]; } // ErrorTrapSims.cs
            if (c.Length > 2072) { r["celebritydeadsim_unregistered"] = c[2072]; } // DeadSimHolders.cs
            if (c.Length > 2074) { r["seasonsdeadsim_dead"] = c[2073]; r["seasonsdeadsim_replaced"] = c[2074]; }
            if (c.Length > 2075) { r["ghostreactdeadsim_removed"] = c[2075]; }
            if (c.Length > 2077) { r["casuihandlers_closed"] = c[2076]; r["casuihandlers_dups"] = c[2077]; } // CasUiHandlers.cs
            if (c.Length > 2078) { r["errortraplookup_dropped"] = c[2078]; } // ErrorTrapSims.cs
            if (c.Length > 2079) { r["dreamnodedeadsim_removed"] = c[2079]; } // DeadSimHolders.cs
            if (c.Length > 2080) { r["overwatchdeadsim_removed"] = c[2080]; } // DeadSimHolders.cs
            if (c.Length > 2086) { r["simrefsdeadsim_talkingto"] = c[2084]; r["simrefsdeadsim_pusher"] = c[2085]; r["simrefsdeadsim_reactors"] = c[2086]; } // SimRefsDeadSim.cs
            if (c.Length > 2087) { r["simrefsdeadsim_occultowners"] = c[2087]; }
            if (c.Length > 2083) { r["outfitwaitsaved_destroyed"] = c[2083]; } // OutfitWaitSaved.cs
            if (c.Length > 937) { r["boatparkgone_skipped"] = c[937]; } // BoatParkGone.cs
            if (c.Length > 997) { r["deadrefcooking_handlers"] = c[994]; r["deadrefconvo_members"] = c[995]; r["deadrefconvo_household"] = c[996]; r["takeariskguard_skips"] = c[997]; } // DeadRefs.cs phases 14-15, TakeARiskGuard.cs
            if (c.Length > 1013) { r["autlivetune_drops"] = c[1012]; r["autlivetune_us"] = c[1013]; } // AutLive.cs autlivetune (docs/124 §3.1)
            if (c.Length > 1056) { r["autliveseen_posted"] = c[1050]; r["autliveseen_dry"] = c[1051]; r["autliveseen_hh_resume"] = c[1052]; r["autliveseen_hh_post"] = c[1053]; r["autliveseen_dry_top10_mis"] = c[1054]; r["autliveseen_dry_top1_mis"] = c[1055]; r["autliveseen_dry_sim_mis"] = c[1056]; } // AutLive.cs autliveseen (docs/124 §17.12)
            if (c.Length > 1072) { r["hoursmemo_checked"] = c[1069]; r["hoursmemo_barhits"] = c[1070]; r["hoursmemo_keymiss"] = c[1071]; r["hoursmemo_nostore"] = c[1072]; } // ScriptHotSpots.cs hoursmemo exactness (docs/276 §1.3)
            if (c.Length > 1084)
            {
                string[] hn = { "hhsearch_n", "hhsearch_local", "hhsearch_push", "hhsearch_nopick", "hhsearch_busyuser", "hhsearch_busyauto",
                    "hhsearch_samedef", "hhsearch_shortgap", "hhsearch_nopushrun", "hhsearch_sims_n", "hhsearch_maxpersim", "hhsearch_exc" };
                for (int i = 0; i < hn.Length; i++) r[hn[i]] = c[1073 + i]; // HhSearchProbe.cs hhsearchprobe (docs/276 §2.3 A)
                r["hhsearch_sims"] = d.GetData("ts3fixes.hhsearch");
            }
            if (c.Length > 1140) { r["burnttile_dropped"] = c[1135]; r["burnttile_lists"] = c[1136]; r["burnttile_empty"] = c[1137]; r["burnttile_err"] = c[1138]; r["burnttile_learned"] = c[1139]; r["burnttile_repeat"] = c[1140]; } // BurntTileFloor.cs (docs/277 §4.1)
            if (c.Length > 1134)
            {
                string[] hx = { "hhsearch_scope_hh", "hhsearch_scope_unknown", "hhsearch_scope_gone", "hhsearch_scope_notlive",
                    "hhsearch_why_immok", "hhsearch_why_immfail", "hhsearch_why_lt5", "hhsearch_why_5to10", "hhsearch_why_10to60", "hhsearch_why_over60", "hhsearch_why_none",
                    "hhsearch_gap_lt500ms", "hhsearch_gap_lt2s", "hhsearch_gap_lt10s", "hhsearch_gap_ge10s", "hhsearch_nopick_over60",
                    "hhsearch_pairn", "hhsearch_pairs", "hhsearch_activelot", "hhsearch_pets" };
                for (int i = 0; i < hx.Length; i++) r[hx[i]] = c[1115 + i]; // HhSearchProbe.cs docs/277 §5 items (fixes-1115)
            }
            if (c.Length > 1057) r["householdtoo_lift"] = c[1057]; // PlayerSims.cs HouseholdExempt (docs/271): asks [Experimental] HouseholdToo=1 let through; 0 with the flag off
            if (c.Length > 1068) { r["decaymemo_hits"] = c[1060]; r["decaymemo_misses"] = c[1061]; r["decaymemo_mismatches"] = c[1062]; r["decaymemo_buffadds"] = c[1063]; r["zdemptyout_stereo_calls"] = c[1064]; r["zdemptyout_stereo_empty"] = c[1065]; r["zdemptyout_timeout_calls"] = c[1066]; r["zdemptyout_timeout_empty"] = c[1067]; r["zdemptyout_mismatches"] = c[1068]; } // WriterMemos.cs (docs/275 Z1, M1)
            if (c.Length > 998) r["autlivelod_drops"] = c[998]; // AutLive.cs autlivelod (docs/124 §3.1)
            if (c.Length > 993) { r["minihomelot_setlot"] = c[991]; r["minihomelot_load"] = c[992]; r["minihomelot_apart"] = c[993]; } // MiniHomeLot.cs, docs/266
            if (c.Length > 101) { r["invcache_hits"] = c[99]; r["invcache_rebuilds"] = c[100]; r["invcache_mismatches"] = c[101]; } // ScriptHotSpots.cs, docs/115
            if (c.Length > 1037) { r["permamemo_hits"] = c[1014]; r["permamemo_misses"] = c[1015]; r["permamemo_mismatches"] = c[1016]; r["permamemo_clears"] = c[1017]; r["buffremovenoop_early"] = c[1018]; r["buffremovenoop_passed"] = c[1019]; r["catnamecache_hits"] = c[1020]; r["catnamecache_misses"] = c[1021]; r["catnamecache_mismatches"] = c[1022]; r["catnamecache_clears"] = c[1023]; } // ExactMemos.cs
            if (c.Length > 1037) { r["autmemo_pairs"] = c[1024]; r["autmemo_segments"] = c[1025]; r["autroommemo_hits"] = c[1026]; r["autroommemo_misses"] = c[1027]; r["autroommemo_mismatches"] = c[1028]; r["autroommemo_outside"] = c[1029]; r["autroommemo_floods"] = c[1030]; r["autroommemo_drops"] = c[1031]; r["autslice_lt1"] = c[1032]; r["autslice_1to2"] = c[1033]; r["autslice_2to5"] = c[1034]; r["autslice_5to10"] = c[1035]; r["autslice_ge10"] = c[1036]; r["autslice_max_us"] = c[1037]; } // AutRoomMemo.cs, docs/97 §7
            if (c.Length > 1003) { r["spawncap_pet_checks"] = c[999]; r["spawncap_pet_under"] = c[1000]; r["spawncap_flower_checks"] = c[1001]; r["spawncap_flower_under"] = c[1002]; r["spawncap_mismatch"] = c[1003]; } // SpawnCap.cs, docs/147 §5 D1
            if (c.Length > 1011) { r["routelen_seen_n"] = c[1004]; r["routelen_seen_m"] = c[1005]; r["routelen_seen_40m"] = c[1006]; r["routelen_unseen_n"] = c[1007]; r["routelen_unseen_m"] = c[1008]; r["routelen_unseen_40m"] = c[1009]; r["routelen_unseen_endseen"] = c[1010]; r["routelen_errors"] = c[1011]; } // RouteLen.cs, docs/246 §6 counters 1/4
            if (c.Length > 1039) { r["vaclistenerpad_padded"] = c[1038]; r["vaclistenerpad_left"] = c[1039]; } // EaVacation.cs, docs/210 §6
            if (c.Length > 1045) { r["unseentp_refused_hidden"] = c[1040]; r["unseentp_refused_lot"] = c[1041]; r["unseentp_refused_closed"] = c[1042]; r["unseentp_refused_raised"] = c[1043]; r["unseentp_clean_moved"] = c[1044]; r["unseentp_clean_left"] = c[1045]; } // UnseenTeleportGoal.cs, docs/267 F1
            bool rlOn;
            if (sw.TryGetValue("routelen", out rlOn) && rlOn) // docs/246 §6 counter 3: cumulative pushes per definition, per --ab slice snapshot
            {
                Dictionary<string, int> picks = d.GetData("ts3fixes.picks") as Dictionary<string, int>;
                if (picks != null) foreach (KeyValuePair<string, int> p in new List<KeyValuePair<string, int>>(picks)) r["pick:" + p.Key] = p.Value;
            }
            if (c.Length > 947) { r["boatgetoutdiag_watched"] = c[945]; r["boatgetoutdiag_replaced"] = c[946]; r["boatgetoutdiag_stale"] = c[947]; } // BoatGetOutDiag.cs, docs/257 §7
            if (c.Length > 2588) { r["unseenoutfitdefer_deferred"] = c[2570]; r["unseenoutfitdefer_replaced"] = c[2571]; r["unseenoutfitdefer_applied_seen"] = c[2572]; r["unseenoutfitdefer_applied_selected"] = c[2573]; r["unseenoutfitdefer_applied_delayed"] = c[2574]; r["unseenoutfitdefer_applied_flush"] = c[2575]; r["unseenoutfitdefer_dropped_gone"] = c[2576]; r["unseenoutfitdefer_dropped_ea_set"] = c[2577]; r["unseenoutfitdefer_dropped_model"] = c[2578]; r["unseenoutfitdefer_waiting"] = c[2579]; r["unseenoutfitdefer_most_waiting"] = c[2580]; r["unseenoutfitdefer_not_fresh"] = c[2581]; r["unseenoutfitdefer_not_open"] = c[2582]; r["unseenoutfitdefer_not_category"] = c[2583]; r["unseenoutfitdefer_threw"] = c[2584]; r["unseenoutfitdefer_not_margin"] = c[2585]; r["unseenoutfitdefer_applied_margin"] = c[2586]; r["unseenoutfitdefer_worst_ms"] = c[2587]; r["unseenoutfitdefer_late"] = c[2588]; } // UnseenOutfitDefer.cs, issue #451
            if (c.Length > 954) { r["boatgetoutguard_hit"] = c[953]; r["boatgetoutguard_skipped_household"] = c[954]; } // BoatGetOutDiag.cs, docs/257 §9
            if (c.Length > 2091) { r["deadsimcount_alive"] = c[2088]; r["deadsimcount_old"] = c[2089]; r["deadsimcount_collected"] = c[2090]; r["deadsimcount_tracked"] = c[2091]; } // DeadSimCount.cs
            if (c.Length > 2109) { r["hudunsub_panels"] = c[2108]; r["hudunsub_missing"] = c[2109]; } // HudUnsub.cs
            if (c.Length > 2093) { r["deadsimhollow_sims"] = c[2092]; r["deadsimhollow_fields"] = c[2093]; } // DeadSimCount.cs
            if (c.Length > 2107) { r["dnpsubjectcache_cleared"] = c[2106]; r["dnpsubjectcache_entries"] = c[2107]; } // DnpSubjectCache.cs
            if (c.Length > 2105) { r["mapviewkeep_shown"] = c[2103]; r["mapviewkeep_built"] = c[2104]; r["mapviewkeep_dropped"] = c[2105]; } // MapViewKeep.cs
            if (c.Length > 2096) { r["tinyuieventreg_entries"] = c[2094]; r["tinyuieventreg_handlers"] = c[2095]; r["tinyuieventreg_left"] = c[2096]; } // TinyUiEventReg.cs
            if (c.Length > 2099) { r["customctlsweep_removed"] = c[2097]; r["customctlsweep_kept"] = c[2098]; r["customctlsweep_left"] = c[2099]; } // CustomCtlSweep.cs
            if (c.Length > 2123) { r["exitcbsweep_removed"] = c[2120]; r["exitcbsweep_longest"] = c[2121]; r["dnppetkeys_dropped"] = c[2122]; r["dnpobjkeys_dropped"] = c[2123]; } // ExitCbSweep.cs, DnpRemovalKeys.cs
            if (c.Length > 2081) { r["errortrapctl_dropped"] = c[2081]; } // ErrorTrapSims.cs
            if (c.Length > 2007) { r["ageupswap_everyday"] = c[2006]; r["ageupswap_released"] = c[2007]; } // AgeUpSwap.cs, docs/503
            if (c.Length > 2057) { r["deadrefsweep_objwalks"] = c[2055]; r["deadrefsweep_objms"] = c[2056]; r["deadrefsweep_objasked"] = c[2057]; } // DeadRefs.cs phases 10-12, docs/205 §2.3a
            if (c.Length > 759) { r["asyncjigsync_kept_sync"] = c[758]; r["asyncjigsync_linked"] = c[759]; } // AsyncJigSync.cs, docs/240
            if (c.Length > 604) { r["autshadow_gm6_methods"] = c[602]; r["autshadow_gm6_mismatch"] = c[603]; r["autshadow_gm6_us"] = c[604]; }
            if (c.Length > 605) r["autshadow_gm6_faults"] = c[605];
            if (c.Length > 606) r["autshadow_gm6_owners_skipped"] = c[606];
            if (c.Length > 2025) { r["autshadow_content_modules"] = c[2022]; r["autshadow_content_unknown"] = c[2023]; r["autshadow_gm6_cha_methods"] = c[2024]; r["autshadow_gm6_cha_mismatch"] = c[2025]; } // docs/218 §5 content guard (-1 = MVID gate)
            if (c.Length > 682) { r["outfitwaitpoll_stale"] = c[680]; r["outfitwaitpoll_polls"] = c[681]; r["outfitwaitpoll_ended"] = c[682]; } // OutfitWait.cs, issue #115
            if (c.Length > 933) r["outfitwaitpoll_dead_ended"] = c[933]; // OutfitWait.cs, issue #40
            if (c.Length > 2009) { r["outfitwaitpoll_clear_instant"] = c[2008]; r["outfitwaitpoll_clear_delayed"] = c[2009]; } // OutfitWait.cs
            if (c.Length > 2011) { r["outfitwaitpoll_cb_dropped"] = c[2010]; r["outfitwaitpoll_cause_dropped"] = c[2011]; } // OutfitWait.cs
            if (c.Length > 2013) { r["outfitwaitpoll_stale_onscreen"] = c[2012]; r["outfitwaitpoll_ended_onscreen"] = c[2013]; } // OutfitWait.cs, docs/224 §5
            if (c.Length > 2005) { r["outfitwaitpoll_cause_own"] = c[2003]; r["outfitwaitpoll_cause_replaced"] = c[2004]; r["outfitwaitpoll_cause_cleared"] = c[2005]; } // OutfitWait.cs, docs/224 §5
            if (c.Length > 684) { r["bakesaleseller_reads"] = c[683]; r["bakesaleseller_rearmed"] = c[684]; } // BakeSale.cs, issue #102
            if (c.Length > 817)
            {
                string[] qn = { "g8_q_seen_n", "g8_q_seen_ticks", "g8_q_seen_b0", "g8_q_seen_b1", "g8_q_seen_b2", "g8_q_seen_b3", "g8_q_seen_b4",
                    "g8_q_unseen_n", "g8_q_unseen_ticks", "g8_q_unseen_b0", "g8_q_unseen_b1", "g8_q_unseen_b2", "g8_q_unseen_b3", "g8_q_unseen_b4",
                    "g8_q_high_sum", "g8_q_low_sum", "g8_q_samples", "g8_q_empty", "g8_seen_exact", "g8_seen_exact_nopick", "g8_seen_game", "g8_seen_game_nopick",
                    "g8_hh_searches", "g8_hh_pushes", "g8_hh_simhours_x100", "g8_w_seen_n", "g8_w_seen_ticks", "g8_w_unseen_n", "g8_w_unseen_ticks",
                    "g8_len_seen_ticks", "g8_len_unseen_ticks", "autunseen_seengame_runs", "autunseen_pace_credit_us" };
                for (int i = 0; i < qn.Length; i++) r[qn[i]] = c[785 + i]; // AutLive.cs QueueNote (docs/222 §11.8)
            }
            if (c.Length > 870) { r["g8_hh_q_n"] = c[867]; r["g8_hh_q_ticks"] = c[868]; r["g8_hh_w_n"] = c[869]; r["g8_hh_w_ticks"] = c[870]; }
            if (c.Length > 875) { r["watchdogasync_sent"] = c[872]; r["watchdogasync_started"] = c[873]; r["watchdogasync_refused"] = c[874]; r["watchdogasync_ticks"] = c[875]; }  // RouteFixes.cs CheckIdle watchdogasync A1 (docs/250 §7.1)
            if (c.Length > 932) { r["watchdogasync_stale"] = c[930]; r["watchdogasync_shadow_runs"] = c[931]; r["watchdogasync_shadow_mismatch"] = c[932]; }  // A1 stale drops, watchdogasyncshadow
            if (c.Length > 871) r["wdLockSkip"] = c[871];  // RouteFixes.cs CheckIdle watchdoglockskip (docs/250 §7.4) // AutLive.cs household part of seen (docs/222 §11.8)
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
            if (c.Length > 4061) { r["unseenfbghost_holds"] = c[4058]; r["unseenfbghost_shortcuts"] = c[4059]; r["unseenfbghost_lastfail"] = c[4060]; r["unseenfbghost_g3_refused"] = c[4061]; } // unseenfbghost (UnseenFallback.cs, docs/202 §3.10)
            if (c.Length > 4089) { r["unseenhouseboatview_asked"] = c[4084]; r["unseenhouseboatview_changed"] = c[4085]; r["unseenfbnosync_asked"] = c[4086]; r["unseenfbnosync_recovered"] = c[4087]; r["safespotplanyield_yields"] = c[4088]; r["safespotplanyield_given_up"] = c[4089]; } // unseenhouseboatview, unseenfbnosync (RouteBackoff.cs), safespotplanyield (Fixes.cs SafeSpot), docs/202 §3.10
            if (c.Length > 4247) { r["minirelset_ours"] = c[4233]; r["minirelset_ea"] = c[4234]; r["minirelset_errors"] = c[4235]; r["stucktaskflag_other_tasks"] = c[4244]; r["watchdogbusydefer_refused"] = c[4245]; r["watchdogbusydefer_deferred"] = c[4246]; r["watchdogbusydefer_capped"] = c[4247]; } // docs/665 minirelset (AlgoSweep665.cs), docs/662 stutter sweep (StutterSweep.cs)
            if (c.Length > 4661) { r["safespotnospot_skipped"] = c[4660]; r["safespotnospot_recorded"] = c[4661]; } // safespotnospot (Fixes.cs SafeSpotBody, the s152-s189 runs)
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
            if (c.Length > 2253) { r["mooddrift_corrected"] = c[2251]; r["mooddrift_points"] = c[2252]; r["mooddrift_errors"] = c[2253]; } // MoodDrift.cs (corrected also counts with the switch off)
            if (c.Length > 2280) { r["moodpending_seen"] = c[2278]; r["moodpending_skipped"] = c[2279]; r["moodpending_errors"] = c[2280]; } // MoodPending.cs (seen also counts off)
            if (c.Length > 1059) { r["weddingcleanup_guarded"] = c[1058]; r["weddingcleanup_errors"] = c[1059]; } // WeddingCleanup.cs, registry fixes-1058
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
            if (c.Length > 776) // AutOffload.cs autofft2, autofflot (docs/208 §9)
            {
                string[] rk = { "autoff_t2_items", "autoff_t2_a0", "autoff_t2_a2", "autoff_t2_a4", "autoff_t2_a5", "autoff_t2_exc",
                    "autoff_lot_items", "autoff_lot_a0", "autoff_lot_a2", "autoff_lot_a4", "autoff_lot_a5", "autoff_lot_exc", "autoff_lot_dry", "autoff_lot_dry_eq" };
                for (int i = 0; i < rk.Length; i++) r[rk[i]] = c[763 + i];
            }
            if (c.Length > 883) // AutOffload.cs autofft2ic (docs/126 §8)
            {
                string[] ic = { "autoff_t2ic_items", "autoff_t2ic_eq", "autoff_t2ic_mis", "autoff_t2ic_abort", "autoff_t2ic_skip", "autoff_t2ic_exc" };
                for (int i = 0; i < ic.Length; i++) r[ic[i]] = c[878 + i];
            }
            if (c.Length > 891) // AutShadow.cs tier-2 P1 (docs/219 §3.1): worker aborts by class, drag pre-warm fills
            {
                string[] p1 = { "aut_t2_inuse_soil", "aut_t2_inuse_hoop", "aut_t2_inuse_arcade", "aut_t2_inuse_skewer", "aut_t2_inuse_cone",
                    "aut_t2_inuse_prop", "aut_t2_fire", "aut_t2_dragwarm" };
                for (int i = 0; i < p1.Length; i++) r[p1[i]] = c[884 + i];
            }
            if (c.Length > 902) // AutShadow.cs tier-2 H7-H10 (docs/126 §3): cache/tuning aborts, drag and LOD answers
            {
                string[] h = { "aut_t2_cache", "aut_t2_tuning", "aut_t2_drag", "aut_t2_lod", "aut_t2_lod_stale" };
                for (int i = 0; i < h.Length; i++) r[h[i]] = c[898 + i];
            }
            if (c.Length > 943) // AutShadow.cs tier-2 P2 (docs/data/tier2-rows35-handoff.md): CauseEffect pre-warm, front-door memo, BuyModeSuite guard
            {
                string[] p2 = { "aut_t2_cewarm", "aut_t2_ce_abort", "aut_t2_door", "aut_t2_door_abort", "aut_t2_suite", "aut_t2_suite_abort" };
                for (int i = 0; i < p2.Length; i++) r[p2[i]] = c[938 + i];
            }
            if (c.Length > 944) r["aut_t2_callpet"] = c[944]; // AutShadow.cs tier-2 P3: CallPet self Tests on the worker, shared list put back
            if (c.Length > 952) { r["gangway_lines"] = c[950]; r["gangway_unseen_ms"] = c[951]; r["gangway_other_ms"] = c[952]; } // HitchCause.cs boatreplanlog gangway fixups, docs/247 §13.3 R1
            if (c.Length > 927) // AutLocalProbe.cs tier-2 L0 (docs/data/tier2-local-phase-handoff.md §7): local phase by actor class
            {
                string[] l0 = { "aut_l0_n_hh", "aut_l0_n_seen", "aut_l0_n_unseen", "aut_l0_us_hh", "aut_l0_us_seen", "aut_l0_us_unseen",
                    "aut_l0_pairn_hh", "aut_l0_pairn_seen", "aut_l0_pairn_unseen", "aut_l0_pairs_hh", "aut_l0_pairs_seen", "aut_l0_pairs_unseen",
                    "aut_l0_side_n", "aut_l0_side_us", "aut_l0_nolocal_n", "aut_l0_nolocal_us", "aut_l0_r_activelot", "aut_l0_r_samelot",
                    "aut_l0_r_daycare", "aut_l0_r_resort", "aut_l0_r_household", "aut_l0_r_other", "aut_l0_span_ms", "aut_l0_nested", "aut_l0_exc" };
                for (int i = 0; i < l0.Length; i++) r[l0[i]] = c[903 + i];
            }
            if (c.Length > 1157) // AutLocalProbe.cs autt2lcov (docs/276 rank 5 piece 1): local-phase C2 by local allow-list, per class
            {
                string[] cov = { "aut_lcov_on_n_hh", "aut_lcov_on_n_seen", "aut_lcov_on_n_unseen", "aut_lcov_on_us_hh", "aut_lcov_on_us_seen", "aut_lcov_on_us_unseen",
                    "aut_lcov_off_n_hh", "aut_lcov_off_n_seen", "aut_lcov_off_n_unseen", "aut_lcov_off_us_hh", "aut_lcov_off_us_seen", "aut_lcov_off_us_unseen", "aut_lcov_live" };
                for (int i = 0; i < cov.Length; i++) r[cov[i]] = c[1145 + i];
            }
            if (c.Length > 1181) // AutShadowLocal.cs autt2local (docs/276 rank 5 piece 3): local request driver, Mode R/W shadow
            {
                string[] lp = { "aut_lp_post", "aut_lp_off", "aut_lp_notmanager", "aut_lp_forced", "aut_lp_scopehh", "aut_lp_scopeunknown", "aut_lp_nolocal", "aut_lp_busy",
                    "aut_lp_notready", "aut_lp_refdone", "aut_lp_workdone", "aut_lp_dropped", "aut_lp_pairs", "aut_lp_dup", "aut_lp_cap", "aut_lp_skip", "aut_lp_pass",
                    "aut_lp_fail", "aut_lp_abort", "aut_lp_pairexc", "aut_lp_noop", "aut_lp_cacheab", "aut_lp_exc", "aut_lp_refpairs" };
                for (int i = 0; i < lp.Length; i++) r[lp[i]] = c[1158 + i];
            }
            if (c.Length > 1184) { r["aut_lp_rows"] = c[1182]; r["aut_lp_rowcap"] = c[1183]; r["aut_lp_rowmax"] = c[1184]; } // AutShadowLocal.cs piece 4 local object rows
            if (c.Length > 1199) { string[] lm = { "aut_lp_a0", "aut_lp_a1", "aut_lp_a2", "aut_lp_a3", "aut_lp_a4", "aut_lp_a5", "aut_lp_walkmiss", "aut_lp_g_pairs", "aut_lp_g_equal", "aut_lp_g_testagree", "aut_lp_g_top1", "aut_lp_g_top1agree", "aut_lp_g_ordermiss", "aut_lp_g_only", "aut_lp_g_cap" }; for (int k = 0; k < lm.Length; k++) r[lm[k]] = c[1185 + k]; } // AutShadowLocal.cs piece 5 Mode R compare + game report
            if (c.Length > 537) { r["autoff_tick_items"] = c[534]; r["autoff_tick_a0"] = c[535]; r["autoff_tick_a2"] = c[536]; r["autoff_tick_unfinished"] = c[537]; } // AutOffload.cs autofftick (docs/206 §15)
            if (c.Length > 490) // AutOffload.cs (docs/208 §8): ready store and the apply at the game's own call
            {
                string[] ap = { "autoff_env_ready", "autoff_env_applied", "autoff_env_apply_none", "autoff_env_apply_stale", "autoff_env_apply_dry_eq",
                    "autoff_bcast_ready", "autoff_bcast_applied", "autoff_bcast_apply_none", "autoff_bcast_apply_stale", "autoff_bcast_dry_order" };
                for (int i = 0; i < ap.Length; i++) r[ap[i]] = c[481 + i];
            }
            if (c.Length > 962) // AutOffload.cs autoffunseen (docs/208 §10): fresh answers sent to the game's own path by scope
            {
                string[] sc = { "autoff_env_scope_household", "autoff_env_scope_seen", "autoff_env_scope_unknown",
                    "autoff_bcast_scope_household", "autoff_bcast_scope_seen", "autoff_bcast_scope_unknown" };
                for (int i = 0; i < sc.Length; i++) r[sc[i]] = c[957 + i];
            }
            if (c.Length > 987) { r["autoff_env_pick_overdue"] = c[985]; r["autoff_env_asked_expired"] = c[986]; r["autoff_env_never_asked"] = c[987]; } // AutOffload.cs env pickup loss (docs/208 §11)
            if (c.Length > 1048) { r["autoff_env_pick_scoped"] = c[1047]; r["autoff_bcast_pick_scoped"] = c[1048]; } // AutOffload.cs autoffunseen pick scope (docs/208 §12)
            if (c.Length > 1114) // EnvBcProbes.cs (docs/274 R1 envlistmemo, R2 bcposdry): dry probes, counts only
            {
                string[] el = { "envlistmemo_calls", "envlistmemo_first", "envlistmemo_hit", "envlistmemo_hit_wrong", "envlistmemo_miss_same", "envlistmemo_miss_diff",
                    "envlistmemo_exc", "envlistmemo_scans", "envlistmemo_events", "envlistmemo_clears", "envlistmemo_list_ms", "envlistmemo_hit_ms",
                    "bcposdry_pulses", "bcposdry_compared", "bcposdry_set_eq", "bcposdry_order_eq", "bcposdry_missing", "bcposdry_extra",
                    "bcposdry_exc", "bcposdry_tables", "bcposdry_table_ms", "bcposdry_scan_ms", "bcposdry_game_ms", "bcposdry_aborts" };
                for (int i = 0; i < el.Length; i++) r[el[i]] = c[1091 + i];
            }
            if (c.Length > 1240) // EnvBcProbes.cs (docs/274 §9 R1 envlistmemoapply / envlistmemoverify): unseen-only env list reuse
            {
                string[] ea = { "envlistapply_scans", "envlistapply_reuse", "envlistapply_live", "envlistapply_fb_actor", "envlistapply_fb_other",
                    "envlistapply_fb_notindoor", "envlistapply_fb_seqover", "envlistapply_exc", "envlistapply_verify", "envlistapply_verify_diff", "envlistapply_verify_reorder" };
                for (int i = 0; i < ea.Length; i++) r[ea[i]] = c[1230 + i];
            }
            if (c.Length > 1260) // EnvListExact.cs (docs/274 §11 R1 envlistmemoexact, every Sim; envscancost by scope)
            {
                string[] ex = { "envlistexact_scans", "envlistexact_reuse", "envlistexact_capture", "envlistexact_fb_notindoor", "envlistexact_fb_puddle",
                    "envlistexact_fb_lot", "envlistexact_fb_noprint", "envlistexact_exc", "envlistexact_verify", "envlistexact_verify_diff", "envlistexact_fb_player",
                    "envlistexact_lot_off", "envlistexact_foreign", "envlistexact_refresh_seen",
                    "envscancost_calls_household", "envscancost_calls_seen", "envscancost_calls_unseen",
                    "envscancost_us_household", "envscancost_us_seen", "envscancost_us_unseen" };
                for (int i = 0; i < ex.Length; i++) r[ex[i]] = c[1241 + i];
                if (c.Length > 1261) r["envlistexact_fb_worker"] = c[1261];
                if (c.Length > 1262) r["envlistexact_standdown"] = c[1262];
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
            if (c.Length > 743) // UnseenCost.cs (docs/227): unseen-check cost probe (unseencost); unseen-cost-report.ts reads these
            {
                string[] us = { "far", "refresh", "simunseen", "townie", "gohome", "pos", "lot", "goal", "dispose", "gate", "ufwhy" };
                for (int i = 0; i < us.Length; i++)
                {
                    r["unseencost_" + us[i] + "_calls"] = c[690 + 4 * i]; r["unseencost_" + us[i] + "_top"] = c[691 + 4 * i];
                    r["unseencost_" + us[i] + "_timed"] = c[692 + 4 * i]; r["unseencost_" + us[i] + "_us"] = c[693 + 4 * i];
                }
                string[] ux = { "onscreen", "camask", "viewread", "sim_asks", "sim_distinct", "ticks", "self_n", "self_us", "floor_n", "floor_us" };
                for (int i = 0; i < ux.Length; i++) r["unseencost_" + ux[i]] = c[734 + i];
            }
            if (c.Length > 757) // UnseenCost.cs sites 11-12 (docs/227 §9): the unseen table's script reads
            {
                string[] us2 = { "utfetch", "utlookup" };
                for (int i = 0; i < us2.Length; i++)
                {
                    r["unseencost_" + us2[i] + "_calls"] = c[750 + 4 * i]; r["unseencost_" + us2[i] + "_top"] = c[751 + 4 * i];
                    r["unseencost_" + us2[i] + "_timed"] = c[752 + 4 * i]; r["unseencost_" + us2[i] + "_us"] = c[753 + 4 * i];
                }
            }
            if (c.Length > 749) // UnseenTable.cs (docs/227 §6): the central unseen table, script side (native [UnseenTable], --ab unseentable)
            {
                string[] ut = { "ticks", "answers", "norow", "mis_unsafe", "mis_safe", "compared" };
                for (int i = 0; i < ut.Length; i++) r["unseentable_" + ut[i]] = c[744 + i];
            }
            Dictionary<string, int> ufCallers = d.GetData("ts3fixes.unseenfb.callers") as Dictionary<string, int>; // caller frames D refused (gate 336)
            if (ufCallers != null) { List<object> l = new List<object>(); foreach (KeyValuePair<string, int> kv in ufCallers) l.Add(kv.Value + " " + kv.Key); r["unseenfbpre_callers"] = l; }
            Dictionary<string, int> urRj = d.GetData("ts3fixes.unseenroute.rj") as Dictionary<string, int>; // relaxed D refusals "gate:reason" (docs/202 §3.9.7b)
            if (urRj != null) { List<object> l = new List<object>(); foreach (KeyValuePair<string, int> kv in urRj) l.Add(kv.Value + " " + kv.Key); r["unseenroute_rj_detail"] = l; }
            if (c.Length > 302) r["owstuck_max_select_us"] = c[302]; // overwatchstuckslice: longest stretch at the IsSelectable hook (OverwatchStuck.cs, docs/145 §12)
            r["unseenboat_log"] = d.GetData("ts3fixes.unseenboat.log"); // docs/248: first refusals per reason
            r["unseenboat_ms"] = d.GetData("ts3fixes.unseenboat.ms"); // docs/247 §11.3: boat replan ms per refusal reason (boatreplanlog on)
            r["easpread_log"] = d.GetData("ts3fixes.easpread"); // one line per pass and path (ours / game's), verify censuses
            r["autshadow_reasons"] = d.GetData("ts3fixes.autshadow.reasons"); // A4 aborts per native reason index (row, pos, memo, rng, ... other)
            r["wildflower_log"] = d.GetData("ts3fixes.wildflower"); // one line per fire with wildflowerprobe or wildflowertask on
            r["recent"] = d.GetData("ts3fixes.recent");
            r["moved"] = d.GetData("ts3fixes.moved");
            return r;
        }
    }
}
