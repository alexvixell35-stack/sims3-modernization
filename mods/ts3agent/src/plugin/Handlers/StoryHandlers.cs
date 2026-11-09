using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.DreamsAndPromises;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects;
using Sims3.Gameplay.Objects.Appliances;
using Sims3.Gameplay.Pools;
using Sims3.Gameplay.Situations;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Skills;
using Sims3.Gameplay.StoryProgression;
using Sims3.Gameplay.Utilities;
using Sims3.UI.Hud;
using Sims3.SimIFace;
using GF = Sims3.Gameplay.Gameflow;

namespace Ts3Agent
{
    // Story fuzzer commands (docs/171 §3, piece A): life events through the game's own code paths, plus the world census
    // (§4) the engine plans stories from. Every command names the game call it makes; docs/171 §6 lists limits.
    public static partial class Handlers
    {
        static void RegisterStory()
        {
            Dispatcher.Register("make_occult", MakeOccult);
            Dispatcher.Register("make_celebrity", MakeCelebrity);
            Dispatcher.Register("throw_party", ThrowParty);
            Dispatcher.Register("move_household", MoveHousehold);
            Dispatcher.Register("story_days", StoryDays);
            Dispatcher.Register("story_burst", StoryBurst);
            Dispatcher.Register("story_burst_state", StoryBurstState);
            Dispatcher.Register("social", Social);
            Dispatcher.Register("start_fire", StartFire);
            Dispatcher.Register("pregnancy_advance", PregnancyAdvance);
            Dispatcher.Register("set_aging", SetAging);
            Dispatcher.Register("world_census", WorldCensus);
            // day planner (docs/171 §12)
            Dispatcher.Register("story_day_state", StoryDayState);
            Dispatcher.Register("go_to_work", GoToWork);
            Dispatcher.Register("sim_fix_need", SimFixNeed);
            Dispatcher.Register("sim_pursue", SimPursue);
            Dispatcher.Register("sim_practice", SimPractice);
            Dispatcher.Register("death_rescue", DeathRescue);
            // off-camera life (docs/180)
            Dispatcher.Register("town_snapshot", TownSnapshot);
            Dispatcher.Register("switch_household", SwitchHousehold);
        }

        static Household sPreviousHousehold;

        // switch_household {household: name | id | "@other" | "@previous"}: Edit Town's "Change Active Household" (EditTownPuck -> PlayFlow), its household step
        // PlayFlowModel.ActivateSimScreenMaskedFuncFromHousehold: LotManager.LockActiveLot(home), the household's internal
        // PrepareToBecomeActiveHousehold (reflection), BinCommon.ActivateSim(household, home), camera on the home lot.
        // Refused where the player's button is: vacation, University, future world (GameUtils), and where Edit Town is
        // refused (InWorldSubState.IsEditTownValid on the active lot: move, reaper, social worker, age-up, fire, burglar, ...).
        // shortcut: skips the PlayFlow state transition itself (map view, its LotManager.ForceReplanOfAllSimRoutes); if Sims
        // of the old household route oddly after a switch, go through GameStates.TransitionToPlayFlow instead.
        static object SwitchHousehold(Args a)
        {
            RequireLive();
            if (GameUtils.IsOnVacation() || GameUtils.IsUniversityWorld() || GameUtils.IsFutureWorld())
                throw new McpException("not available: changing the active household is off on vacation, at University and in the future");
            Household cur = Household.ActiveHousehold;
            string why = null;
            if (cur != null && cur.LotHome != null && !InWorldSubState.IsEditTownValid(cur.LotHome, ref why))
                throw new McpException("not available: Edit Town refused (" + (why ?? "busy") + ")");
            string want = a.Str("household");
            Household h = null;
            if (want == "@previous") h = sPreviousHousehold; // the one active before the last switch_household (rotation repros)
            else if (want == "@other") // the playable household with the most humans other than the active one (rotation repros)
            {
                foreach (Household x in Household.sHouseholdList)
                    if (x != null && x != cur && x.LotHome != null && !x.IsSpecialHousehold && !x.IsPreviousTravelerHousehold && x.NumMembers > 0 && (h == null || x.NumMembers > h.NumMembers)) h = x;
            }
            else
                foreach (Household x in Household.sHouseholdList)
                    if (x != null && (Util.Id(x.HouseholdId) == want || x.Name == want)) { h = x; break; }
            if (h == null) throw new McpException("no household '" + want + "'");
            if (h == cur) throw new McpException("'" + h.Name + "' is already the active household");
            if (h.LotHome == null || h.IsSpecialHousehold) throw new McpException("not available: '" + h.Name + "' has no home lot or is a service household");
            MethodInfo prep = typeof(Household).GetMethod("PrepareToBecomeActiveHousehold", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            // BinCommon is internal to Sims3GameplaySystems: reflection, as for PrepareToBecomeActiveHousehold
            Type bc = typeof(Household).Assembly.GetType("Sims3.Gameplay.BinCommon");
            MethodInfo activate = bc == null ? null : bc.GetMethod("ActivateSim", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, new Type[] { typeof(Household), typeof(Lot) }, null);
            if (prep == null || activate == null) throw new McpException("Household.PrepareToBecomeActiveHousehold or BinCommon.ActivateSim not found");
            LotManager.LockActiveLot(h.LotHome);
            prep.Invoke(h, null);
            Sim s = activate.Invoke(null, new object[] { h, h.LotHome }) as Sim;
            if (s == null) throw new McpException("BinCommon.ActivateSim gave no Sim for '" + h.Name + "'");
            try { Camera.FocusOnLot(h.LotHome.LotId, 0f); } catch (Exception) { }
            sPreviousHousehold = cur;
            Dictionary<string, object> r = Util.Obj();
            r["from"] = cur != null ? cur.Name : null;
            r["household"] = h.Name; r["id"] = Util.Id(h.HouseholdId); r["active_sim"] = s.FullName;
            return r;
        }

        // town_snapshot: every SimDescription of every household with a home lot or in the world (Household.sHouseholdList),
        // for the story-day diff of off-camera changes (births, deaths, moves, jobs, marriages; docs/180 §4): id, name, age,
        // household id/name, home lot, partner id + IsMarried, occupation name + level, dead. Plus the clock and EA's story
        // progression mode (StoryProgressionService.sService.mMode, private: reflection; -disablestoryprogression = Disabled).
        // Read only; one pass over households (a few hundred SimDescriptions).
        static object TownSnapshot(Args a)
        {
            Dictionary<string, object> r = Util.Obj();
            r["day_number"] = SimClock.ElapsedCalendarDays() + 1;
            r["hour"] = Util.Round(SimClock.HoursPassedOfDay);
            Household act = Household.ActiveHousehold;
            r["active_household"] = act != null ? Util.Id(act.HouseholdId) : null;
            try
            {
                FieldInfo fs = typeof(StoryProgressionService).GetField("sService", BindingFlags.NonPublic | BindingFlags.Static);
                object svc = fs != null ? fs.GetValue(null) : null;
                FieldInfo fm = typeof(StoryProgressionService).GetField("mMode", BindingFlags.NonPublic | BindingFlags.Instance);
                r["story_progression"] = svc != null && fm != null ? fm.GetValue(svc).ToString() : "unknown";
            }
            catch (Exception) { r["story_progression"] = "unknown"; }
            List<object> list = new List<object>();
            foreach (Household h in new List<Household>(Household.sHouseholdList))
            {
                if (h == null || h.IsServiceNpcHousehold || h.IsTouristHousehold) continue;
                // EA's pools (stray pets, aliens, mermaids, travellers; same set as list_households' special): churn, not life events
                bool pool = h.IsPetHousehold || h.IsAlienHousehold || h.IsMermaidHousehold || h.IsTravelHousehold;
                foreach (SimDescription sd in new List<SimDescription>(h.AllSimDescriptions))
                {
                    if (sd == null) continue;
                    Dictionary<string, object> d = Util.Obj();
                    d["id"] = Util.Id(sd.SimDescriptionId); d["name"] = sd.FullName; d["age"] = sd.Age.ToString();
                    d["household"] = Util.Id(h.HouseholdId); d["household_name"] = h.Name;
                    d["lot"] = h.LotHome != null ? Util.Id(h.LotHome.LotId) : null;
                    if (pool) d["pool"] = true;
                    if (!sd.IsHuman) d["species"] = sd.Species.ToString();
                    if (sd.Partner != null) { d["partner"] = Util.Id(sd.Partner.SimDescriptionId); d["married"] = sd.IsMarried; }
                    if (sd.Occupation != null) { d["job"] = sd.Occupation.CareerName; d["level"] = sd.Occupation.Level; }
                    if (sd.IsDead) d["dead"] = sd.DeathStyle.ToString();
                    list.Add(d);
                }
            }
            r["sims"] = list;
            return r;
        }

        // make_occult {sim, type, death?}: type = an OccultTypes name (Vampire, Werewolf, Fairy, Witch, Genie, Mummy, Mermaid,
        // PlantSim, ImaginaryFriend, Robot, Frankenstein, Unicorn, TimeTraveler) -> OccultManager.AddOccultType(type,
        // addOutfit: true, isReward: false, fromRestore: false), what the game's own DEBUG "Occult... > Add..." cheat
        // (Sim.DEBUG_ModifyOccultTypes) runs; EP check = OccultManager.IsOccultTypeSupportedWithCurrentInstall.
        // Ghost: SimDescription.IsGhost + SetDeathStyle(death) + Urnstone.SimToPlayableGhost(sim, isCASGhost: true), the CAS
        // ghost path (CASLogic + BinCommon). Zombie (EP7): the Zombie buff (Origin.FromFullMoon), as LunarCycleManager's
        // full-moon picks get it (same as the zombies command).
        static object MakeOccult(Args a)
        {
            RequireLive();
            Sim sim = Util.FindSim(a, "sim");
            string type = a.Str("type").Trim();
            Dictionary<string, object> r = Util.Obj();
            r["sim"] = sim.FullName;
            r["type"] = type;
            if (string.Compare(type, "Zombie", true) == 0)
            {
                if (!GameUtils.IsInstalled(ProductVersion.EP7)) throw new McpException("make_occult Zombie needs Supernatural (EP7)");
                r["ok"] = sim.BuffManager.AddElement(BuffNames.Zombie, Origin.FromFullMoon);
                if (!(bool)r["ok"]) throw new McpException("make_occult: the game refused the Zombie buff for " + sim.FullName);
                return r;
            }
            if (string.Compare(type, "Ghost", true) == 0)
            {
                if (sim.SimDescription.IsGhost) throw new McpException(sim.FullName + " is already a ghost");
                SimDescription.DeathType dt = Util.ParseEnum<SimDescription.DeathType>(a.Str("death", "Electrocution"));
                sim.SimDescription.IsGhost = true;
                sim.SimDescription.SetDeathStyle(dt, sim.IsSelectable);
                Urnstone.SimToPlayableGhost(sim, true);
                r["death"] = dt.ToString();
                r["ok"] = sim.SimDescription.IsGhost;
                return r;
            }
            OccultTypes ot = Util.ParseEnum<OccultTypes>(type);
            if (ot == OccultTypes.None || ot == OccultTypes.Ghost) throw new McpException("make_occult: no transform for " + ot);
            if (!OccultManager.IsOccultTypeSupportedWithCurrentInstall(ot))
                throw new McpException("make_occult " + ot + ": the expansion for it is not installed");
            OccultManager om = sim.SimDescription.OccultManager;
            if (om.HasOccultType(ot)) throw new McpException(sim.FullName + " is already " + ot);
            if (!om.AddOccultType(ot, true, false, false))
                throw new McpException("make_occult: the game refused " + ot + " for " + sim.FullName + " (OccultBaseClass.CanAdd: age, species or a clashing occult)");
            r["ok"] = om.HasOccultType(ot);
            r["occults"] = om.CurrentOccultTypes.ToString();
            return r;
        }

        // make_celebrity {sim | all: true, level?: 1} (issue #40 leak stress): CelebrityManager.ForceSetLevel(level), what the
        // game's cheat forceSetCelebrityLevel (Commands.OnForceSetCelebrityLevel) runs for every human SimDescription that
        // CanBeCelebrity. all: the same loop, but the active household is never touched; one sim: refused for an active
        // household member. level 0 = CelebrityManager.RemoveCelebrityStatus. Returns how many descriptions changed.
        // throw_party {count?: 10, hours?: 1} (issue #40 leak stress): a random townie host (in the world, young adult or older,
        // household with a home lot, never the active household) throws a house party there, as the phone's Throw Party does
        // (Phone.CallThrowParty.HaveParty): new HouseParty(home lot, host, guests, Everyday, now + hours) and its
        // PartyEvent (event 57). Guests: up to count random living human townies, teen or older, of any other household
        // except the active one (the party brings in ones not in the world). Returns host, lot and guest count.
        // social {sim, target, name (an EA social: Chat, Insult, Fight!, Tell Joke, Flirt...)}: queues EA's SocialInteractionA
        // on the actor, the way GoToCommunityLot pushes Chat. For townie pairs the pie menu (list_interactions) offers no
        // socials at all (al337-al339: 0 enabled), so the stress drip names them. Townies only: never the active household.
        static object Social(Args a)
        {
            RequireLive();
            Sim actor = Util.FindSim(a, "sim"), target = Util.FindSim(a, "target");
            if (actor == target) throw new McpException("sim and target are the same Sim");
            Household act = Household.ActiveHousehold;
            if (act != null && (actor.Household == act || target.Household == act)) throw new McpException("townies only: the active household stays untouched");
            string name = a.Str("name");
            if (Sims3.Gameplay.Socializing.ActionData.Get(name) == null) throw new McpException("no EA social named " + name);
            InteractionInstance i = new Sims3.Gameplay.Socializing.SocialInteractionA.Definition(name, new string[0], null, false)
                .CreateInstance(target, actor, new InteractionPriority(InteractionPriorityLevel.UserDirected), false, true);
            Dictionary<string, object> r = Util.Obj();
            r["queued"] = actor.InteractionQueue.Add(i);
            r["sim"] = actor.FullName; r["target"] = target.FullName; r["social"] = name;
            return r;
        }

        static object ThrowParty(Args a)
        {
            RequireLive();
            int count = (int)a.Long("count", 10);
            float hours = (float)a.Double("hours", 1);
            Household act = Household.ActiveHousehold;
            const Sims3.SimIFace.CAS.CASAgeGenderFlags adult = Sims3.SimIFace.CAS.CASAgeGenderFlags.YoungAdult | Sims3.SimIFace.CAS.CASAgeGenderFlags.Adult | Sims3.SimIFace.CAS.CASAgeGenderFlags.Elder;
            const Sims3.SimIFace.CAS.CASAgeGenderFlags teenUp = adult | Sims3.SimIFace.CAS.CASAgeGenderFlags.Teen;
            Random rnd = new Random();
            List<Sim> hosts = new List<Sim>();
            foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>())
            {
                SimDescription sd = s.SimDescription;
                if (sd == null || !sd.IsHuman || (sd.Age & adult) == 0 || s.HasBeenDestroyed) continue;
                if (s.Household == null || s.Household == act || s.Household.LotHome == null) continue;
                hosts.Add(s);
            }
            if (hosts.Count == 0) throw new McpException("no townie host in the world whose household has a home lot");
            Sim host = hosts[rnd.Next(hosts.Count)];
            List<SimDescription> pool = new List<SimDescription>();
            foreach (SimDescription sd in Household.EveryHumanSimDescription())
                if (sd != host.SimDescription && !sd.IsDead && (sd.Age & teenUp) != 0 && sd.Household != null && sd.Household != act && sd.Household != host.Household)
                    pool.Add(sd);
            List<SimDescription> guests = new List<SimDescription>();
            while (guests.Count < count && pool.Count > 0)
            {
                int i = rnd.Next(pool.Count);
                guests.Add(pool[i]);
                pool.RemoveAt(i);
            }
            Lot lot = host.Household.LotHome;
            DateAndTime start = SimClock.CurrentTime() + new DateAndTime(0, DaysOfTheWeek.Sunday, 0, (int)Math.Round(hours * 60), 0);
            HouseParty party = new HouseParty(lot, host, guests, Sims3.SimIFace.CAS.OutfitCategories.Everyday, start);
            EventTracker.SendEvent(new PartyEvent((EventTypeId)57, host, host.SimDescription, party));
            Dictionary<string, object> r = Util.Obj();
            r["host"] = host.FullName;
            r["lot"] = lot.Name;
            r["guests"] = guests.Count;
            return r;
        }

        // move_household {} (issue #40 leak stress): a random townie household with a home lot (never the active household)
        // moves to a random free furnished residential lot (no household, not the player's, not a dorm, apartment or
        // vacationing family's home, 20+ objects), as EA's story progression MoveToLargerHouse.Execute does: oldLot.MoveOut,
        // newLot.MoveIn, MidlifeCrisisManager.OnMoved + SendSimHome per member, HouseholdUpdateEvent 73.
        static object MoveHousehold(Args a)
        {
            RequireLive();
            Household act = Household.ActiveHousehold;
            Random rnd = new Random();
            List<Household> movers = new List<Household>();
            foreach (Household h in Household.sHouseholdList)
                if (h != null && h != act && h.LotHome != null && h.NumMembers > 0 && !h.LotHome.IsPlayerHomeLot && !h.LotHome.IsHomeLotOfVacationingFamily) movers.Add(h);
            List<Lot> free = new List<Lot>();
            foreach (Lot l in LotManager.AllLots)
                if (l != null && l.IsResidentialLot && l.Household == null && !l.IsWorldLot && !l.IsReservedForPlayer && !l.IsDormitoryLot && !l.IsApartmentLot
                    && !l.IsPlayerHomeLot && !l.IsHomeLotOfVacationingFamily && l.GetObjects<GameObject>().Length >= 20) free.Add(l);
            if (movers.Count == 0) throw new McpException("no townie household with a home lot");
            if (free.Count == 0) throw new McpException("no free furnished residential lot");
            Household hh = movers[rnd.Next(movers.Count)];
            Lot from = hh.LotHome, to = free[rnd.Next(free.Count)];
            from.MoveOut(hh);
            to.MoveIn(hh);
            foreach (SimDescription sd in hh.AllSimDescriptions)
            {
                MidlifeCrisisManager.OnMoved(sd);
                sd.SendSimHome();
            }
            EventTracker.SendEvent(new HouseholdUpdateEvent((EventTypeId)73, hh));
            Dictionary<string, object> r = Util.Obj();
            r["household"] = hh.Name;
            r["from"] = from.Name;
            r["to"] = to.Name;
            r["members"] = hh.NumMembers;
            return r;
        }

        // story_days {days?: 1, mode?: "Disabled" | "Enabled" | "Accelerated"} (issue #40 leak stress): EA story progression on
        // demand. mode: StoryProgressionService.SetMode (Accelerated = EA's delay multiplier 60). days > 0: a OneShotFunctionTask
        // runs the service's private DoOneDayOfActions that many times (kActionsMin..kActionsMax TryOneAction each; it Sleeps
        // between actions, so never on the poll task; result in async_log). EA's actions skip the active household (IsActive
        // checks) except the roommate ones (KillRoommate, RemoveRoommate), so refused while it accepts roommates.
        static object StoryDays(Args a)
        {
            RequireLive();
            Household act = Household.ActiveHousehold;
            if (act != null && Household.RoommateManager != null && Household.RoommateManager.AcceptingRoommates)  // static: the active household's
                throw new McpException("the active household accepts roommates: EA's roommate story actions would touch it");
            object svc = typeof(StoryProgressionService).GetField("sService", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            if (svc == null) throw new McpException("EA story progression service not running (NRaas StoryProgression replaces it?)");
            FieldInfo mf = typeof(StoryProgressionService).GetField("mMode", BindingFlags.Instance | BindingFlags.NonPublic);
            Dictionary<string, object> r = Util.Obj();
            r["mode_before"] = mf.GetValue(svc).ToString();
            string mode = a.Str("mode", "");
            if (mode != "") StoryProgressionService.SetMode(Util.ParseEnum<StoryProgressionService.Mode>(mode));
            r["mode"] = mf.GetValue(svc).ToString();
            // actions: EA's per-day count range (static kActionsMin / kActionsMax, tuning) set to that many, for story spam
            const BindingFlags sf = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            FieldInfo amin = typeof(StoryProgressionService).GetField("kActionsMin", sf), amax = typeof(StoryProgressionService).GetField("kActionsMax", sf);
            if (a.Has("actions"))
            {
                int n = (int)a.Long("actions");
                amin.SetValue(null, n);
                amax.SetValue(null, n);
            }
            r["actions_per_day"] = amin.GetValue(null) + "-" + amax.GetValue(null);
            int days = (int)a.Long("days", 1);
            if (sStoryBusy) { r["days_started"] = 0; r["busy"] = true; return r; }  // never two story tasks at once
            MethodInfo day = typeof(StoryProgressionService).GetMethod("DoOneDayOfActions", BindingFlags.Instance | BindingFlags.NonPublic);
            // a delegate, not day.Invoke: DoOneDayOfActions sleeps, and the game cannot yield through a reflection call
            // ("Attempting to yield in a non-yielding context!", al339)
            Function oneDay = (Function)Delegate.CreateDelegate(typeof(Function), svc, day);
            if (days > 0)
            {
                sStoryBusy = true;
                RunAsync("story_days " + days, delegate { try { for (int i = 0; i < days; i++) oneDay(); } finally { sStoryBusy = false; } });
            }
            r["days_started"] = days;
            return r;
        }

        static bool sStoryBusy;

        // story_burst {actions?: 200, cooldowns?: false, sleep_every?: 5, sleep_ticks?: 10} (issue #40 leak stress, user
        // 11-09 "crank story progression up"): EA's own story action step without the per-day cap and, by default, without
        // the per-category cooldowns. Each step is EA's TryOneAction: Demographics.Collect, GenerateActions(ignore =
        // !cooldowns), ScoreAction (multiplier 1) for every action still IsValid, a weighted pick (RandomUtil, so seed_rng
        // holds), Execute only if it is still IsValid. EA runs the same with cooldowns off in its travel fix-up
        // (DoTravelActions). EA's candidate lists skip the active and travel households (StoryAction IsActive checks); refused
        // while the active household accepts roommates, as story_days. Runs in a task and returns at once;
        // story_burst_state reads the counts by action type, errors and the demographics error.
        static object StoryBurst(Args a)
        {
            RequireLive();
            Household act = Household.ActiveHousehold;
            if (act != null && Household.RoommateManager != null && Household.RoommateManager.AcceptingRoommates)
                throw new McpException("the active household accepts roommates: EA's roommate story actions would touch it");
            object svc = typeof(StoryProgressionService).GetField("sService", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            if (svc == null) throw new McpException("EA story progression service not running (NRaas StoryProgression replaces it?)");
            Demographics demo = typeof(StoryProgressionService).GetField("mDemographics", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(svc) as Demographics;
            if (demo == null) throw new McpException("EA story progression has no demographics yet");
            int n = (int)a.Long("actions", 200), every = Math.Max(1, (int)a.Long("sleep_every", 5));
            uint ticks = (uint)a.Long("sleep_ticks", 10);
            bool ignore = !a.Bool("cooldowns", false);
            Dictionary<string, object> r = Util.Obj();
            if (sStoryBusy) { r["started"] = 0; r["busy"] = true; return r; }
            sStoryBusy = true;
            sBurstDone = 0; sBurstSteps = 0; sBurstNone = 0; sBurstByType.Clear(); sBurstErrors.Clear();
            RunAsync("story_burst " + n, delegate
            {
                try
                {
                    for (int i = 0; i < n && !GameStates.IsTravelling; i++)
                    {
                        sBurstSteps++;
                        try
                        {
                            demo.Collect();
                            List<StoryAction> list = demo.GenerateActions(ignore);
                            float[] w = new float[list.Count];
                            for (int j = 0; j < list.Count; j++)
                                if (list[j] != null && list[j].IsValid()) { float sc = demo.ScoreAction(list[j], 1f); if (sc > 0f) w[j] = sc; }
                            float sum = 0f;
                            foreach (float x in w) sum += x;
                            if (sum <= 0f) { sBurstNone++; }
                            else
                            {
                                StoryAction pick = list[RandomUtil.GetWeightedIndex(w)];
                                if (pick.IsValid())
                                {
                                    pick.Execute();
                                    sBurstDone++;
                                    string t = pick.GetType().Name;
                                    int c;
                                    sBurstByType.TryGetValue(t, out c);
                                    sBurstByType[t] = c + 1;
                                }
                            }
                        }
                        catch (ResetException) { throw; }
                        catch (Exception e)
                        {
                            string k = e.GetType().Name + ": " + e.Message;
                            int c;
                            sBurstErrors.TryGetValue(k, out c);
                            sBurstErrors[k] = c + 1;
                        }
                        Simulator.Sleep(i % every == every - 1 ? ticks : 0u);
                    }
                }
                finally { sStoryBusy = false; }
            });
            r["started"] = n;
            r["cooldowns"] = !ignore;
            r["error_before"] = demo.TotalError;
            return r;
        }

        static int sBurstDone, sBurstSteps, sBurstNone;
        static readonly Dictionary<string, int> sBurstByType = new Dictionary<string, int>(), sBurstErrors = new Dictionary<string, int>();

        static object StoryBurstState(Args a)
        {
            Dictionary<string, object> r = Util.Obj();
            r["running"] = sStoryBusy;
            r["steps"] = sBurstSteps;
            r["executed"] = sBurstDone;
            r["no_action"] = sBurstNone;
            Dictionary<string, object> t = Util.Obj();
            foreach (KeyValuePair<string, int> kv in sBurstByType) t[kv.Key] = kv.Value;
            r["by_type"] = t;
            Dictionary<string, object> e = Util.Obj();
            foreach (KeyValuePair<string, int> kv in sBurstErrors) e[kv.Key] = kv.Value;
            r["errors"] = e;
            return r;
        }

        static object MakeCelebrity(Args a)
        {
            RequireLive();
            uint level = (uint)a.Long("level", 1);
            Household act = Household.ActiveHousehold;
            List<SimDescription> sds = a.Bool("all", false) ? Household.EveryHumanSimDescription() : new List<SimDescription> { Util.FindSimDescription(a, "sim") };
            int set = 0, skipped = 0;
            foreach (SimDescription sd in sds)
            {
                if (sd == null || sd.CelebrityManager == null || !sd.CanBeCelebrity || (act != null && sd.Household == act))
                {
                    if (!a.Bool("all", false)) throw new McpException((sd == null ? "?" : sd.FullName) + ": not a townie that can be a celebrity");
                    skipped++;
                    continue;
                }
                if (level == 0) sd.CelebrityManager.RemoveCelebrityStatus();
                else sd.CelebrityManager.ForceSetLevel(level);
                set++;
            }
            Dictionary<string, object> r = Util.Obj();
            r["set"] = set;
            r["skipped"] = skipped;
            r["level"] = level;
            return r;
        }

        // start_fire {object | sim | x,z}: FireManager.AddFire(pos, allowOffHomeLot: true) -> Fire.CreateFire(lotId,
        // LotLocation): the same spawner stoves, fireplaces and candles use (BurstIntoFlame, spread, "Talk About Fire").
        // Firefighters come only through the lot's smoke detectors (FireManager.CheckForSmokeDetector) or a phone call.
        // The game refuses without an active Sim that has a home lot, off any lot (world lot id 0), and on tiles that
        // cannot burn (Fire.SpreadChanceForTile 0: water, pools).
        static object StartFire(Args a)
        {
            RequireLive();
            Vector3 pos;
            if (a.Has("object")) pos = Util.FindObject(a, "object").PositionOnFloor;
            else if (a.Has("sim")) pos = Util.FindSim(a, "sim").PositionOnFloor;
            else if (a.Has("x"))
            {
                float x = (float)a.Double("x"), z = (float)a.Double("z");
                pos = new Vector3(x, World.GetTerrainHeight(x, z), z);
            }
            else throw new McpException("start_fire needs object, sim or x,z");
            Fire fire = Sims3.Gameplay.Controllers.FireManager.AddFire(pos, true);
            if (fire == null) throw new McpException("start_fire: the game made no fire there (no active Sim with a home lot, not on a lot, or the tile cannot burn)");
            Lot lot = fire.LotCurrent;
            Dictionary<string, object> r = Util.Obj();
            r["fire"] = Util.Id(fire.ObjectId);
            r["lot"] = lot != null ? Util.LotName(lot) : null;
            r["lot_id"] = lot != null ? Util.Id(lot.LotId) : null;
            r["smoke_detectors"] = lot != null ? Sims3.Gameplay.Queries.GetObjects<Sims3.Gameplay.Controllers.SmokeDetector>(lot).Length : 0;
            r["x"] = Util.Round(pos.x); r["z"] = Util.Round(pos.z);
            return r;
        }

        // pregnancy_advance {sim, partner?, to: pregnant|labor|birth}: Pregnancy.Start(female, maleDescription) (the game's
        // try-for-baby result; partner defaults to sim's partner), then Pregnancy.HourlyCallback() once per pregnancy hour
        // up to the stage, as the game's own DEBUG "Pregnancy... > Have Baby" cheat steps it: labor = hour
        // kHourToStartContractions (water breaks, contractions), birth = kHoursOfPregnancy (HaveTheBaby). An already
        // pregnant sim is only advanced. Does nothing on vacation or in the University world (the callback returns there).
        static object PregnancyAdvance(Args a)
        {
            RequireLive();
            Sim sim = Util.FindSim(a, "sim");
            string to = a.Str("to", "pregnant").ToLowerInvariant();
            Pregnancy p = sim.SimDescription.Pregnancy;
            Dictionary<string, object> r = Util.Obj();
            if (p == null)
            {
                SimDescription partner = a.Has("partner") ? Util.FindSimDescription(a, "partner") : sim.SimDescription.Partner;
                if (partner == null) throw new McpException(sim.FullName + " has no partner: pass partner");
                Sim mom = sim.SimDescription.IsFemale ? sim : partner.CreatedSim;
                SimDescription dad = sim.SimDescription.IsFemale ? partner : sim.SimDescription;
                if (mom == null || !mom.SimDescription.IsFemale) throw new McpException("pregnancy_advance needs a female in the world (" + sim.FullName + " + " + partner.FullName + ")");
                if (!Pregnancy.Start(mom, dad))
                    throw new McpException("pregnancy_advance: the game refused (" + mom.FullName + ": no home lot, household full, aging up, plant sim gets a forbidden fruit, or already pregnant)");
                sim = mom;
                p = mom.SimDescription.Pregnancy;
                r["started_with"] = dad.FullName;
                if (p == null) { r["mom"] = mom.FullName; r["note"] = "plant sim: forbidden fruit in inventory"; return r; }
            }
            int goal = to == "birth" ? Pregnancy.kHoursOfPregnancy
                : to == "labor" ? (int)typeof(Pregnancy).GetField("kHourToStartContractions", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null)
                : to == "pregnant" ? 0 : -1;
            if (goal < 0) throw new McpException("to must be pregnant, labor or birth");
            int steps = 0;
            while (sim.SimDescription.Pregnancy == p && Pregnancy.kHoursOfPregnancy - p.HoursUntilBirth < goal && steps < 200)
            {
                int before = p.HoursUntilBirth;
                p.HourlyCallback();
                steps++;
                if (p.HoursUntilBirth == before) break;  // vacation / University / tourist leaving: the hour does not move
            }
            r["mom"] = sim.FullName;
            r["hour"] = Pregnancy.kHoursOfPregnancy - p.HoursUntilBirth;
            r["hours_until_birth"] = p.HoursUntilBirth;
            r["steps"] = steps;
            r["reached"] = Pregnancy.kHoursOfPregnancy - p.HoursUntilBirth >= goal;
            return r;
        }

        // set_aging {enabled?, lifespan?: short|medium|normal|long|epic, speed?: sim days per aging year, story?: bool}:
        // the Options dialog's own setters (OptionsModel): AgingManager.Singleton.Enabled, SetSimDaysPerAgingYear(level 0-4)
        // or SimDaysPerAgingYear = speed, StoryProgressionService.SetMode(Enabled|Disabled). Runtime only: the ini/option
        // files are not written, so the next game start uses the player's own settings.
        static object SetAging(Args a)
        {
            RequireLive();
            AgingManager am = AgingManager.Singleton;
            if (am == null) throw new McpException("no AgingManager in this world");
            if (a.Has("enabled")) am.Enabled = a.Bool("enabled", true);
            if (a.Has("lifespan"))
            {
                int level = Array.IndexOf(new string[] { "short", "medium", "normal", "long", "epic" }, a.Str("lifespan").ToLowerInvariant());
                if (level < 0) throw new McpException("lifespan must be short, medium, normal, long or epic");
                am.SetSimDaysPerAgingYear(level);
            }
            if (a.Has("speed")) am.SimDaysPerAgingYear = Math.Max(1f, (float)a.Double("speed"));
            if (a.Has("story"))
                StoryProgressionService.SetMode(a.Bool("story", true) ? StoryProgressionService.Mode.Enabled : StoryProgressionService.Mode.Disabled);
            Dictionary<string, object> r = Util.Obj();
            r["aging"] = am.Enabled;
            r["sim_days_per_year"] = am.SimDaysPerAgingYear;
            if (a.Has("story")) r["story"] = a.Bool("story", true);
            return r;
        }

        // world_census {samples?: 20}: what the world offers stories (docs/171 §4). Reads only: LotManager.AllLots
        // (CommercialLotSubType per community lot, IsDivingLot, kEP10_Port = houseboat docks), Queries.GetObjects
        // <RabbitHole> by class, every GameObject matched to the §4 object kinds by class or interface name (KindTypes),
        // SimDescription.GetSimDescriptionsInWorld by age / species / occult / household, GameUtils world and EPs,
        // season and moon from test_env {mode: read}. Objects keep up to `samples` ids per kind with their lot.
        static readonly string[][] KindTypes = {
            new string[] { "stove", "IStove" }, new string[] { "pool", "Pool" }, new string[] { "pool_ladder", "PoolLadder" },
            new string[] { "book", "Book" }, new string[] { "skill_book", "BookSkill" }, new string[] { "bookshelf", "Bookshelf" },
            new string[] { "instrument", "MusicalInstrument", "Violin" }, new string[] { "easel", "Easel" },
            new string[] { "chess", "IChessTable" }, new string[] { "gym", "IGymObject" }, new string[] { "telescope", "ITelescope" },
            new string[] { "garden_plot", "Plant" }, new string[] { "crystal_ball", "CrystalBall" },
            new string[] { "register", "IShoppingRegister" }, new string[] { "stall", "BaseFoodStand", "FVShoppingRegister", "LemonadeStand" },
            new string[] { "houseboat", "Houseboat" }, new string[] { "smoke_detector", "SmokeDetector" },
            new string[] { "fishing_spot", "FishingSpot" },
            // everyday home objects (docs/171 §11: without them a need like "fridge" is never seen and every run buys one);
            // type names read from the script DLL metadata
            new string[] { "fridge", "IFridge" }, new string[] { "bed", "IBed" }, new string[] { "crib", "ICrib" },
            new string[] { "shower", "IShower" }, new string[] { "toilet", "IToilet" }, new string[] { "sink", "Sink" },
            new string[] { "tv", "ITelevision" }, new string[] { "computer", "IComputer" }, new string[] { "mailbox", "IMailbox" },
            new string[] { "trash_can", "Trashcan" }, new string[] { "table", "ITableDining" }, new string[] { "bar", "Bar", "IBarProfessional" },
            new string[] { "dance_floor", "DanceFloor" }, new string[] { "cake", "ICake" }, new string[] { "phone", "Phone" },
            new string[] { "homework", "Homework" },
        };

        static object WorldCensus(Args a)
        {
            RequireLive();
            int samples = (int)a.Long("samples", 20);
            Dictionary<string, object> r = Util.Obj();
            r["world"] = GameUtils.GetCurrentWorld().ToString();
            r["world_file"] = GameStates.GetCurrentWorldName(false);
            List<object> eps = new List<object>();
            foreach (ProductVersion pv in Enum.GetValues(typeof(ProductVersion)))
            {
                string n = pv.ToString();
                if ((n.StartsWith("EP") || n.StartsWith("SP")) && GameUtils.IsInstalled(pv)) eps.Add(n);
            }
            r["eps"] = eps;
            Dictionary<string, object> env = TestEnv(new Args(new Dictionary<string, object> { { "mode", "read" } })) as Dictionary<string, object>;
            foreach (string k in new string[] { "season", "weather", "moon_phase", "days_to_full_moon", "hour" })
                if (env != null && env.ContainsKey(k)) r[k] = env[k];

            // lots: community venues by subtype, residential occupied / vacant; ports and dive lots also as kinds
            Dictionary<string, object> venues = Util.Obj();
            int resOccupied = 0, resVacant = 0;
            List<object> communityLots = new List<object>();
            foreach (Lot lot in LotManager.AllLots)
            {
                if (lot == null || lot.IsWorldLot) continue;
                if (lot.IsResidentialLot)
                {
                    if (lot.Household != null) resOccupied++; else resVacant++;
                    continue;
                }
                string sub = lot.CommercialLotSubType.ToString();
                venues[sub] = (venues.ContainsKey(sub) ? (int)venues[sub] : 0) + 1;
                Dictionary<string, object> o = Util.Obj();
                o["id"] = Util.Id(lot.LotId); o["name"] = Util.LotName(lot); o["venue"] = sub;
                o["diving"] = lot.IsDivingLot;
                o["pos"] = Pos(lot.GetCenterPosition());  // camera jumps between stories (docs/171 §7)
                communityLots.Add(o);
            }
            r["venues"] = venues;
            r["community_lots"] = communityLots;
            r["residential_occupied"] = resOccupied;
            r["residential_vacant"] = resVacant;

            Dictionary<string, object> rabbitHoles = Util.Obj();
            List<object> rhList = new List<object>();
            foreach (RabbitHole rh in Sims3.Gameplay.Queries.GetObjects<RabbitHole>())
            {
                string t = rh.GetType().Name;
                rabbitHoles[t] = (rabbitHoles.ContainsKey(t) ? (int)rabbitHoles[t] : 0) + 1;
                Dictionary<string, object> o = Util.Obj();
                o["id"] = Util.Id(rh.ObjectId); o["type"] = t; o["lot"] = rh.LotCurrent != null ? Util.LotName(rh.LotCurrent) : null;
                rhList.Add(o);
            }
            r["rabbit_holes"] = rabbitHoles;
            r["rabbit_hole_list"] = rhList;

            // objects by kind: one pass, kinds per class cached (base classes and interfaces by short name)
            Dictionary<Type, List<string>> kindsOf = new Dictionary<Type, List<string>>();
            Dictionary<string, int> counts = new Dictionary<string, int>();
            Dictionary<string, List<object>> sample = new Dictionary<string, List<object>>();
            int scanned = 0;
            foreach (GameObject go in Sims3.Gameplay.Queries.GetObjects<GameObject>())
            {
                if (go == null || go is Sim) continue;
                scanned++;
                List<string> kinds;
                Type t = go.GetType();
                if (!kindsOf.TryGetValue(t, out kinds)) { kinds = KindsOf(t); kindsOf[t] = kinds; }
                foreach (string k in kinds)
                {
                    counts[k] = (counts.ContainsKey(k) ? counts[k] : 0) + 1;
                    if (!sample.ContainsKey(k)) sample[k] = new List<object>();
                    if (sample[k].Count >= samples) continue;
                    Dictionary<string, object> o = Util.Obj();
                    o["id"] = Util.Id(go.ObjectId); o["type"] = t.Name;
                    Lot lot = go.LotCurrent;
                    o["lot"] = lot != null && !lot.IsWorldLot ? Util.Id(lot.LotId) : null;
                    o["inventory"] = !go.InWorld;
                    sample[k].Add(o);
                }
            }
            Dictionary<string, object> objects = Util.Obj();
            foreach (string[] kt in KindTypes)
            {
                Dictionary<string, object> o = Util.Obj();
                o["count"] = counts.ContainsKey(kt[0]) ? counts[kt[0]] : 0;
                o["samples"] = sample.ContainsKey(kt[0]) ? sample[kt[0]] : new List<object>();
                objects[kt[0]] = o;
            }
            r["objects"] = objects;
            r["objects_scanned"] = scanned;

            // Sims: counts by age, species, occult; households; a compact list the engine picks actors from
            Dictionary<string, object> byAge = Util.Obj(), bySpecies = Util.Obj(), byOccult = Util.Obj();
            List<object> sims = new List<object>();
            Dictionary<ulong, int> hhSize = new Dictionary<ulong, int>();
            foreach (SimDescription sd in SimDescription.GetSimDescriptionsInWorld())
            {
                if (sd == null) continue;
                Bump(byAge, sd.Age.ToString());
                Bump(bySpecies, sd.Species.ToString());
                List<string> occ = new List<string>();
                if (sd.OccultManager != null)
                    foreach (OccultTypes ot in Enum.GetValues(typeof(OccultTypes)))
                        if (ot != OccultTypes.None && (sd.OccultManager.CurrentOccultTypes & ot) == ot) occ.Add(ot.ToString());
                if (sd.IsGhost && !occ.Contains("Ghost")) occ.Add("Ghost");
                if (sd.IsZombie) occ.Add("Zombie");
                if (occ.Count == 0) Bump(byOccult, "None");
                foreach (string o in occ) Bump(byOccult, o);
                if (sd.Household != null) hhSize[sd.Household.HouseholdId] = (hhSize.ContainsKey(sd.Household.HouseholdId) ? hhSize[sd.Household.HouseholdId] : 0) + 1;
                if (sims.Count < 800)
                {
                    Dictionary<string, object> s = Util.Obj();
                    s["id"] = Util.Id(sd.SimDescriptionId); s["name"] = sd.FullName; s["age"] = sd.Age.ToString();
                    s["species"] = sd.Species.ToString(); s["female"] = sd.IsFemale;
                    s["occult"] = string.Join(",", occ.ToArray());
                    s["household"] = sd.Household != null ? Util.Id(sd.Household.HouseholdId) : null;
                    s["instantiated"] = sd.CreatedSim != null;
                    s["active_household"] = sd.Household != null && sd.Household == Household.ActiveHousehold;
                    sims.Add(s);
                }
            }
            r["sims_by_age"] = byAge;
            r["sims_by_species"] = bySpecies;
            r["sims_by_occult"] = byOccult;
            r["sims"] = sims;
            List<object> households = new List<object>();
            foreach (Household hh in Household.sHouseholdList)
            {
                if (hh == null) continue;
                Dictionary<string, object> o = Util.Obj();
                o["id"] = Util.Id(hh.HouseholdId); o["name"] = hh.Name;
                o["members"] = hhSize.ContainsKey(hh.HouseholdId) ? hhSize[hh.HouseholdId] : 0;
                o["lot"] = hh.LotHome != null ? Util.Id(hh.LotHome.LotId) : null;
                o["active"] = hh == Household.ActiveHousehold;
                households.Add(o);
            }
            r["households"] = households;
            return r;
        }

        // ------------------------------------------------------------ day planner (docs/171 §12)

        static List<Sim> SimsArg(Args a)
        {
            List<string> names = new List<string>();
            if (a.Raw("sims") is string) names.AddRange(a.Str("sims").Split('|'));
            else if (a.Has("sims")) foreach (object o in a.List("sims")) names.Add(Convert.ToString(o));
            else names.Add(a.Str("sim"));
            List<Sim> sims = new List<Sim>();
            foreach (string n in names)
            {
                if (n.Trim().Length == 0) continue;
                try { sims.Add(Util.FindSim(new Args(new Dictionary<string, object> { { "sim", n.Trim() } }), "sim")); }
                catch (McpException) { }  // not instantiated (at work in a rabbit hole is still instantiated) or gone
            }
            return sims;
        }

        // the game's own skill for a wish or lifetime wish: a skill whose display name is in the wish text (Skill.Name, the
        // localized name the wish text uses) [inferred: wish texts name the skill, e.g. "Reach Level 5 in Cooking"]
        static string SkillOfText(string text, IEnumerable<Skill> skills)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string best = null; int len = 0;
            foreach (Skill k in skills)
                if (k != null && !string.IsNullOrEmpty(k.Name) && k.Name.Length > len && text.IndexOf(k.Name, StringComparison.OrdinalIgnoreCase) >= 0) { best = k.Guid.ToString(); len = k.Name.Length; }
            return best;
        }

        static Dictionary<string, object> JobState(Sim s, Career c, bool school)
        {
            Dictionary<string, object> d = Util.Obj();
            d["name"] = c.CareerName;
            d["school"] = school;
            d["start"] = Util.Round(c.StartTime);
            d["finish"] = Util.Round(c.FinishTime);
            d["days"] = c.DaysOfWeekToWork.ToString();
            d["work_day"] = c.IsWorkDay;                          // Occupation.IsWorkDay (IsTimeDuringWorkDay(now))
            d["day_off"] = c.IsDayOff;                            // HasVacationOrLeave or not a work day
            d["leave"] = c.HasVacationOrLeave;
            d["should_be_at_work"] = c.ShouldBeAtWork();          // what the game's own work alarm and the HUD use
            d["at_work"] = c.IsAtWork;                            // on the job lot, mIsAtWork, or on the way (IsSimGoingToWork)
            d["in_rabbit_hole"] = s.SimInRabbitHolePosture;
            d["hours_until_work"] = Util.Round(c.HoursUntilWork);
            d["carpool"] = c.CarpoolTime;
            d["carpool_enabled"] = c.CarpoolEnabled;
            return d;
        }

        // story_day_state {sims: [names] | "a|b", sim?}: one read per planner tick for every driven Sim: sim clock
        // (SimClock), GameUtils.IsOnVacation, per Sim the career and school schedule (CareerManager.OccupationAsCareer /
        // School: StartTime, FinishTime, DaysOfWeekToWork, IsWorkDay, IsDayOff, ShouldBeAtWork, IsAtWork, CarpoolTime),
        // RetiredCareer, motives (Motives.GetValue), skills (SkillManager.List), lifetime wish and promised wishes
        // (DreamsAndPromisesManager.LifetimeWishNode / GetPromisedNodes) with the skill each names, and the facts the death
        // guard (story-day.ts guardDecide) judges: swimming (SwimmingInPool posture), the lethal buffs (OnFire, Drowning,
        // Fatigued, SingedElectricity, Starving), Unlucky, the current interaction, aging (AgingManager.Enabled,
        // SimDescription.AgingEnabled, AgingManager.NumDaysBeforeAging), dying (Urnstone.KillSim queued) and dead. Reads only.
        static object StoryDayState(Args a)
        {
            RequireLive();
            Dictionary<string, object> r = Util.Obj();
            r["day"] = SimClock.CurrentDayOfWeek.ToString();
            r["hour"] = Util.Round(SimClock.HoursPassedOfDay);
            r["day_number"] = SimClock.ElapsedCalendarDays() + 1;
            r["vacation"] = GameUtils.IsOnVacation();
            r["speed"] = GF.CurrentGameSpeed.ToString();
            AgingManager am = AgingManager.Singleton;
            bool agingOn = am != null && am.Enabled;
            r["aging"] = agingOn;
            List<Skill> skills = new List<Skill>(SkillManager.SkillDictionary);
            List<object> list = new List<object>();
            foreach (Sim s in SimsArg(a))
            {
                SimDescription sd = s.SimDescription;
                Dictionary<string, object> d = Util.Obj();
                d["sim"] = sd.FullName; d["id"] = Util.Id(sd.SimDescriptionId); d["age"] = sd.Age.ToString();
                CareerManager cm = sd.CareerManager;
                if (cm != null)
                {
                    if (cm.OccupationAsCareer != null && !(cm.OccupationAsCareer is School)) d["job"] = JobState(s, cm.OccupationAsCareer, false);
                    else if (sd.Occupation != null) d["occupation"] = sd.Occupation.CareerName;  // self-employed / active careers: no fixed hours
                    if (cm.School != null) d["school"] = JobState(s, cm.School, true);
                    d["retired"] = cm.RetiredCareer != null;
                }
                d["motives"] = MotiveValues(s);
                List<object> sk = new List<object>();
                if (sd.SkillManager != null)
                    foreach (Skill k in sd.SkillManager.List)
                    {
                        if (k.IsHiddenSkill()) continue;
                        Dictionary<string, object> o = Util.Obj();
                        o["skill"] = k.Guid.ToString(); o["level"] = k.SkillLevel; o["max"] = k.MaxSkillLevel;
                        sk.Add(o);
                    }
                d["skills"] = sk;
                try
                {
                    DreamsAndPromisesManager dm = s.DreamsAndPromisesManager;
                    if (dm != null && dm.LifetimeWishNode != null)
                    {
                        Dictionary<string, object> o = Util.Obj();
                        o["name"] = dm.LifetimeWishNode.GetName(); o["skill"] = SkillOfText((string)o["name"], skills);
                        d["ltw"] = o;
                    }
                    List<object> w = new List<object>();
                    if (dm != null)
                        foreach (ActiveDreamNode n in dm.GetPromisedNodes())
                        {
                            if (n == null) continue;
                            Dictionary<string, object> o = Util.Obj();
                            o["name"] = n.GetName(); o["skill"] = SkillOfText((string)o["name"], skills); o["points"] = Util.Round(n.AchievementPoints);
                            w.Add(o);
                        }
                    d["wishes"] = w;
                }
                catch (Exception) { }
                // death guard facts
                Dictionary<string, object> g = Util.Obj();
                g["swimming"] = s.Posture is SwimmingInPool;
                List<object> buffs = new List<object>();
                if (s.BuffManager != null)
                    foreach (BuffNames b in new BuffNames[] { BuffNames.OnFire, BuffNames.Drowning, BuffNames.Fatigued, BuffNames.SingedElectricity, BuffNames.Starving })
                        if (s.BuffManager.HasElement(b)) buffs.Add(b.ToString());
                g["buffs"] = buffs;
                g["unlucky"] = s.TraitManager != null && s.TraitManager.HasElement(TraitNames.Unlucky);
                g["interaction"] = s.CurrentInteraction != null ? SafeName(s.CurrentInteraction) : null;
                g["aging"] = agingOn && sd.AgingEnabled;
                if (sd.Elder && agingOn) g["days_to_age"] = Util.Round(AgingManager.NumDaysBeforeAging(sd));
                g["dying"] = s.InteractionQueue != null && s.InteractionQueue.HasInteractionOfType(Urnstone.KillSim.Singleton);
                g["dead"] = sd.IsDead;
                g["ghost"] = sd.IsGhost;
                // the death's kind (story-day.ts: OldAge = natural, docs/171 §16): SimDescription.DeathStyle once dead, else
                // the queued Urnstone.KillSim's simDeathType (AgingManager.AgeTransitionWithoutCake kills elders with OldAge)
                if (sd.IsDead || sd.IsGhost) g["death_type"] = sd.DeathStyle.ToString();
                else if (s.InteractionQueue != null)
                    foreach (InteractionInstance ii in s.InteractionQueue.InteractionList)
                    {
                        Urnstone.KillSim k = ii as Urnstone.KillSim;
                        if (k != null) { g["death_type"] = k.simDeathType.ToString(); break; }
                    }
                d["guard"] = g;
                list.Add(d);
            }
            r["sims"] = list;
            return r;
        }

        // go_to_work {sim, school?}: the work (or school) interaction the game's own regular work-day alarm queues
        // (Career.RegularWorkDayGoToWorkHandle: InteractionQueue.Add(Career.CreateWorkInteractionInstance()), WorkInRabbitHole
        // or GoToSchoolInRabbitHole at the career's location), only when ShouldBeAtWork or IsAllowedToWork (the early show-up
        // window) and not already at work or on the way. The carpool / school bus stays the game's own.
        static object GoToWork(Args a)
        {
            RequireLive();
            Sim s = Util.FindSim(a, "sim");
            CareerManager cm = s.SimDescription.CareerManager;
            Career c = cm == null ? null : a.Bool("school", false) ? cm.School : cm.OccupationAsCareer;
            if (c == null) throw new McpException(s.FullName + " has no " + (a.Bool("school", false) ? "school" : "career with work hours"));
            Dictionary<string, object> r = JobState(s, c, c is School);
            r["sim"] = s.FullName;
            if (c.IsAtWork) { r["queued"] = false; r["why"] = "already at work or on the way"; return r; }
            if (!c.ShouldBeAtWork() && !c.IsAllowedToWork()) throw new McpException(s.FullName + ": not a work hour for " + c.CareerName);
            if (c.CareerLoc == null || c.CareerLoc.Owner == null) throw new McpException(c.CareerName + " has no work location");
            r["queued"] = s.InteractionQueue.Add(c.CreateWorkInteractionInstance());
            return r;
        }

        static object PushBest(Sim s, CommodityKind c, AutonomySearchType how, InteractionPriorityLevel prio, string what)
        {
            InteractionInstance ii = s.Autonomy.FindBestActionForCommodity(c, how);
            if (ii == null) throw new McpException(s.FullName + ": the game's autonomy found nothing for " + what + " (" + c + ")");
            ii.SetPriority(prio);
            Dictionary<string, object> r = Util.Obj();
            r["sim"] = s.FullName;
            r["commodity"] = c.ToString();
            r["interaction"] = SafeName(ii);
            GameObject t = ii.Target as GameObject;
            if (t != null && t != s) { r["target"] = Util.ObjectName(t); r["targetId"] = Util.Id(t.ObjectId); }
            r["queued"] = s.InteractionQueue.Add(ii);
            return r;
        }

        // sim_fix_need {sim, motive}: the game's own fix for a low motive: Autonomy.FindBestActionForCommodity(motive,
        // AutonomySearchType.BuffAutoSolve), the search BuffManager.CreateInteractionForAutoSolveBuff runs for a "hungry" or
        // "tired" moodlet, queued at NonCriticalNPCBehavior priority as there. Energy = sleep (bed), Hunger = eat, ...
        static object SimFixNeed(Args a)
        {
            RequireLive();
            Sim s = Util.FindSim(a, "sim");
            CommodityKind c = Util.ParseEnum<CommodityKind>(a.Str("motive"));
            if (s.Motives != null) c = OccultMotive(s.Motives, c);
            if (s.Motives == null || !s.Motives.HasMotive(c)) throw new McpException(s.FullName + " has no motive " + c);
            Dictionary<string, object> r = PushBest(s, c, AutonomySearchType.BuffAutoSolve, InteractionPriorityLevel.NonCriticalNPCBehavior, "motive " + c) as Dictionary<string, object>;
            r["value"] = Util.Round(s.Motives.GetValue(c));
            return r;
        }

        static CommodityKind SkillCommodity(Sim s, SkillNames n)
        {
            Skill st = SkillManager.GetStaticSkill(n);
            if (st != null && !SkillManager.SimCanLearnSkill(st, s.SimDescription)) throw new McpException(s.FullName + " cannot learn " + n + " (age or species)");
            if (st != null && st.Commodity != CommodityKind.None) return st.Commodity;
            return Util.ParseEnum<CommodityKind>("Skill" + n);  // e.g. Homework has no skill record but a commodity
        }

        // the player's way to practice (docs/171 §16 #8): why FindBestActionForCommodity(Skill<Name>) finds nothing [verified,
        // ITUN of GameplayData/gameplay.package + Autonomy.cs]: the commodity map only lists outputs with advertised > 0
        // (CommodityChange.ValidForCommodityInteractionMap) and local autonomy scans only the current lot; Mirror/VanityDresser
        // PracticeSpeech advertise SkillCharisma but are DisallowAutonomous; the fridge's Have <recipe> advertises Hunger, not
        // SkillCooking (the skill comes from the pan step); fishing spots sit on world/community lots; nothing advertises
        // SkillPhotography (the camera's Take Photo is player-aimed). So: scan the home lot's objects for interactions whose
        // tuning outputs the skill (advertised or actual > 0), plus the fridge's non-snack recipes for Cooking, and keep the best
        // one whose user-directed Test passes (a player's click).
        sealed class Practice { public InteractionObjectPair Iop; public float Value; }

        static Dictionary<SkillNames, Practice> PracticeScan(Sim s, Dictionary<CommodityKind, SkillNames> want)
        {
            Dictionary<SkillNames, Practice> best = new Dictionary<SkillNames, Practice>();
            Lot home = s.LotHome;
            if (home == null) return best;
            bool cook = want.ContainsKey(CommodityKind.SkillCooking);
            foreach (GameObject o in home.GetObjects<GameObject>())
            {
                if (o == null || o is Sim || o.HasBeenDestroyed || o.Interactions == null) continue;
                foreach (InteractionObjectPair iop in o.Interactions)
                {
                    Tradeoff t = iop.Tuning != null ? iop.Tuning.mTradeoff : null;
                    if (t == null || t.mOutputs == null) continue;
                    foreach (CommodityChange ch in t.mOutputs)
                    {
                        SkillNames n;
                        float v = Math.Max(ch.ConstantChange, ch.Value);
                        if (v > 0f && want.TryGetValue(ch.Commodity, out n)) Consider(best, n, iop, v, s);
                    }
                }
                if (cook && o is Fridge)
                {
                    List<InteractionObjectPair> all;
                    try { all = o.GetAllInteractionsForActor(s); } catch (Exception) { continue; }
                    foreach (InteractionObjectPair iop in all)
                    {
                        Fridge_Have.Definition fd = iop.InteractionDefinition as Fridge_Have.Definition;
                        if (fd != null && fd.ChosenRecipe != null && !fd.ChosenRecipe.IsSnack) Consider(best, SkillNames.Cooking, iop, 100f, s);
                    }
                }
            }
            return best;
        }

        static void Consider(Dictionary<SkillNames, Practice> best, SkillNames n, InteractionObjectPair iop, float v, Sim s)
        {
            Practice p;
            if (best.TryGetValue(n, out p) && p.Value >= v) return;
            InteractionInstanceParameters ip = new InteractionInstanceParameters(iop, s, new InteractionPriority(InteractionPriorityLevel.UserDirected), false, true);
            GreyedOutTooltipCallback cb = null;
            try { if (iop.InteractionDefinition.Test(ref ip, ref cb) != InteractionTestResult.Pass) return; }
            catch (ResetException) { throw; }
            catch (Exception) { return; }
            best[n] = new Practice { Iop = iop, Value = v };
        }

        // sim_practice {sim, skills}: which of the skills the Sim can practice at home the player's way (PracticeScan), once per
        // planned day (story-day.ts census): {skills: {Painting: {object, interaction}}, missing: [...], cannot: [...]}
        static object SimPractice(Args a)
        {
            RequireLive();
            Sim s = Util.FindSim(a, "sim");
            Dictionary<CommodityKind, SkillNames> want = new Dictionary<CommodityKind, SkillNames>();
            List<object> cannot = new List<object>();
            foreach (object o in a.List("skills"))
            {
                SkillNames n = Util.ParseEnum<SkillNames>(Convert.ToString(o));
                try { want[SkillCommodity(s, n)] = n; } catch (McpException) { cannot.Add(n.ToString()); }
            }
            Dictionary<SkillNames, Practice> best = PracticeScan(s, want);
            Dictionary<string, object> found = Util.Obj();
            List<object> missing = new List<object>();
            foreach (SkillNames n in want.Values)
            {
                Practice p;
                if (!best.TryGetValue(n, out p)) { missing.Add(n.ToString()); continue; }
                Dictionary<string, object> d = Util.Obj();
                d["object"] = Util.ObjectName(p.Iop.Target as GameObject);
                d["interaction"] = p.Iop.InteractionDefinition.GetType().FullName;
                found[n.ToString()] = d;
            }
            Dictionary<string, object> r = Util.Obj();
            r["sim"] = s.FullName;
            r["skills"] = found;
            r["missing"] = missing;
            r["cannot"] = cannot;
            return r;
        }

        // sim_pursue {sim, skill}: practice a skill or hobby the player's way: the best home-lot practice interaction
        // (PracticeScan) queued at UserDirected; none = Autonomy.FindBestActionForCommodity(Skill<Name>, Generic) as before
        // (homework = skill Homework -> SkillHomework on the Sim's own homework).
        static object SimPursue(Args a)
        {
            RequireLive();
            Sim s = Util.FindSim(a, "sim");
            SkillNames n = Util.ParseEnum<SkillNames>(a.Str("skill"));
            CommodityKind c = SkillCommodity(s, n);
            Dictionary<CommodityKind, SkillNames> want = new Dictionary<CommodityKind, SkillNames>();
            want[c] = n;
            Practice p;
            Dictionary<string, object> r;
            if (PracticeScan(s, want).TryGetValue(n, out p))
            {
                InteractionInstanceParameters ip = new InteractionInstanceParameters(p.Iop, s, new InteractionPriority(InteractionPriorityLevel.UserDirected), false, true);
                InteractionInstance ii = p.Iop.InteractionDefinition.CreateInstanceFromParameters(ref ip);
                r = Util.Obj();
                r["sim"] = s.FullName;
                r["commodity"] = c.ToString();
                r["interaction"] = SafeName(ii);
                GameObject t = p.Iop.Target as GameObject;
                if (t != null) { r["target"] = Util.ObjectName(t); r["targetId"] = Util.Id(t.ObjectId); }
                r["how"] = "home object";
                r["queued"] = s.InteractionQueue.Add(ii);
            }
            else
            {
                r = PushBest(s, c, AutonomySearchType.Generic, InteractionPriorityLevel.UserDirected, "skill " + n + " (no home object for it)") as Dictionary<string, object>;
                r["how"] = "autonomy";
            }
            r["skill"] = n.ToString();
            Skill own = s.SkillManager != null ? s.SkillManager.GetElement(n) : null;
            r["level"] = own != null ? own.SkillLevel : 0;
            return r;
        }

        // death_rescue {sim, kind}: undo what is about to kill a Sim (story-day.ts guardDecide picks the kind), each with the
        // game's own calls; the guard never touches Sims a death template owns.
        //   starve: Motives.SetValue(Hunger or VampireThirst, 0) (the value Motive.cs gives Sims who cannot starve is the same
        //           kind of call), then the BuffAutoSolve eat search (sim_fix_need)
        //   drown:  BuffManager.RemoveElement(Drowning, Fatigued), Energy to >= 0, then GetOutOfPool on the posture's container,
        //           the interaction BuffDrowning queues itself (internal class: its Singleton by reflection)
        //   burn:   BuffManager.RemoveElement(OnFire) (BuffOnFire.OnTimeout is the Burn kill; OnRemoval re-adds it only when
        //           the timeout already ran on a Sim that cannot be killed)
        //   electrocution: cancel the current repair/upgrade (InteractionQueue.CancelInteraction) and remove SingedElectricity
        //           (the repair's failure path kills only a Singed or Unlucky Sim: TV.cs and siblings)
        //   freeze: Motives.SetValue(Temperature, 0)
        //   old_age: AgingManager.CancelAgingAlarmsForSim(sim), the call AgeTransitionWithoutCake makes after an elder dies;
        //           the elder keeps living until the next aging restart (load or option change) [inferred until a game run]
        static object DeathRescue(Args a)
        {
            RequireLive();
            Sim s = Util.FindSim(a, "sim");
            string kind = a.Str("kind").ToLowerInvariant();
            List<object> did = new List<object>();
            switch (kind)
            {
                case "starve":
                {
                    CommodityKind c = s.SimDescription.IsVampire && s.Motives.HasMotive(CommodityKind.VampireThirst) ? CommodityKind.VampireThirst : CommodityKind.Hunger;
                    s.Motives.SetValue(c, 0f); did.Add("set " + c + " 0");
                    if (s.BuffManager.HasElement(BuffNames.Starving)) { s.BuffManager.RemoveElement(BuffNames.Starving); did.Add("removed Starving"); }
                    try { PushBest(s, c, AutonomySearchType.BuffAutoSolve, InteractionPriorityLevel.NonCriticalNPCBehavior, "eat"); did.Add("eat queued"); } catch (McpException) { }
                    break;
                }
                case "drown":
                {
                    foreach (BuffNames b in new BuffNames[] { BuffNames.Drowning, BuffNames.Fatigued })
                        if (s.BuffManager.HasElement(b)) { s.BuffManager.RemoveElement(b); did.Add("removed " + b); }
                    if (s.Motives.GetValue(CommodityKind.Energy) < 0f) { s.Motives.SetValue(CommodityKind.Energy, 0f); did.Add("set Energy 0"); }
                    if (s.Motives.GetValue(CommodityKind.Hunger) < -50f) { s.Motives.SetValue(CommodityKind.Hunger, 0f); did.Add("set Hunger 0"); }  // Motive.cs: starving while swimming = Drown
                    SwimmingInPool sp = s.Posture as SwimmingInPool;
                    IGameObject pool = sp != null ? sp.ContainerPool as IGameObject : null;
                    if (pool != null)
                    {
                        Type t = typeof(SwimmingInPool).Assembly.GetType("Sims3.Gameplay.Pools.GetOutOfPool");
                        InteractionDefinition def = t != null ? t.GetField("Singleton", BindingFlags.Public | BindingFlags.Static).GetValue(null) as InteractionDefinition : null;
                        if (def != null && s.InteractionQueue.AddNextIfPossible(def.CreateInstance(pool, s, new InteractionPriority(InteractionPriorityLevel.UserDirected), false, true))) did.Add("GetOutOfPool queued");
                    }
                    break;
                }
                case "burn":
                    if (s.BuffManager.HasElement(BuffNames.OnFire)) { s.BuffManager.RemoveElement(BuffNames.OnFire); did.Add("removed OnFire"); }
                    break;
                case "electrocution":
                {
                    InteractionInstance cur = s.CurrentInteraction;
                    string nm = cur != null ? SafeName(cur) : "";
                    if (cur != null && (nm.IndexOf("Repair", StringComparison.OrdinalIgnoreCase) >= 0 || nm.IndexOf("Upgrade", StringComparison.OrdinalIgnoreCase) >= 0))
                    { s.InteractionQueue.CancelInteraction(cur, false); did.Add("cancelled " + nm); }
                    if (s.BuffManager.HasElement(BuffNames.SingedElectricity)) { s.BuffManager.RemoveElement(BuffNames.SingedElectricity); did.Add("removed SingedElectricity"); }
                    break;
                }
                case "freeze":
                    if (s.Motives.HasMotive(CommodityKind.Temperature)) { s.Motives.SetValue(CommodityKind.Temperature, 0f); did.Add("set Temperature 0"); }
                    break;
                case "old_age":
                    if (AgingManager.Singleton != null) { AgingManager.Singleton.CancelAgingAlarmsForSim(s); did.Add("aging alarms cancelled"); }
                    break;
                default:
                    throw new McpException("kind must be starve, drown, burn, electrocution, freeze or old_age");
            }
            Dictionary<string, object> r = Util.Obj();
            r["sim"] = s.FullName;
            r["kind"] = kind;
            r["did"] = did;
            r["dying"] = s.InteractionQueue.HasInteractionOfType(Urnstone.KillSim.Singleton);
            return r;
        }

        static void Bump(Dictionary<string, object> d, string k) { d[k] = (d.ContainsKey(k) ? (int)d[k] : 0) + 1; }

        static List<string> KindsOf(Type t)
        {
            List<string> names = new List<string>();
            for (Type b = t; b != null; b = b.BaseType) names.Add(b.Name);
            foreach (Type i in t.GetInterfaces()) names.Add(i.Name);
            List<string> kinds = new List<string>();
            foreach (string[] kt in KindTypes)
                for (int j = 1; j < kt.Length; j++)
                    if (names.Contains(kt[j])) { kinds.Add(kt[0]); break; }
            return kinds;
        }
    }
}
