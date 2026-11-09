using System;
using System.Collections.Generic;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Skills;
using Sims3.SimIFace;
using Sims3.SimIFace.CAS;

namespace Ts3Agent
{
    // Outfits rebuilt without CAS, for the leak stress (issue #40; user 11-09: "anything that changes sims outfits all the
    // time ... for cas, like giving a bad makeover without having to enter cas to everyone, or a bad tattoo"). Both are EA's
    // own builders [verified: decompiled 1.67]: the tattoo chair's AddFailureTattoo (Tattooing.ApplyTattoo =
    // SimDescription.ApplyBodyTypeToAllOutfits: every outfit of the Sim rebuilt) and the stylist's
    // FinishReceiveBadMakeover (Styling.LoadMakeoverOutfitForClothesSpin: OutfitUtils.ApplyUniformToOutfit, the new outfit
    // added, the old one released). The outfit switch that follows yields (SwitchOutfitHelper.Wait), so it runs in a game
    // task; its results come with the next call ("last"). TOWNIES ONLY: never the active household or a special household.
    // Each command walks all townies in a fixed rotation (SimDescriptionId order, wrapping), as career_ladder.
    public static partial class Handlers
    {
        static void RegisterOutfitStress()
        {
            Dispatcher.Register("bad_tattoo", BadTattoo);
            Dispatcher.Register("bad_makeover", BadMakeover);
        }

        static ulong sTattooCursor, sMakeoverCursor;
        static int sOutfitTaskDone, sOutfitTaskSkipped, sOutfitTaskErrors;
        static long sTattooMs, sTattooN, sTattooMaxMs;  // ApplyTattoo time per Sim, in the task
        static string sOutfitTaskLastError;

        const CASAgeGenderFlags kTeenUp = CASAgeGenderFlags.Teen | CASAgeGenderFlags.YoungAdult | CASAgeGenderFlags.Adult | CASAgeGenderFlags.Elder;

        // the next n townie humans, teen and older, after cursor (wrapping); inWorld: only Sims instantiated in the world
        static List<SimDescription> NextTownies(ref ulong cursor, int n, bool inWorld, out int eligible)
        {
            Household active = Household.ActiveHousehold;
            List<SimDescription> all = new List<SimDescription>();
            foreach (Household h in Household.sHouseholdList)
            {
                if (h == null || h == active || h.IsActive || h.IsSpecialHousehold) continue;
                foreach (SimDescription sd in h.AllSimDescriptions)
                {
                    if (sd == null || sd.IsPet || sd.IsDead || sd.IsGhost || (sd.Age & kTeenUp) == 0) continue;
                    if (inWorld && !Util.InWorldNow(sd)) continue;
                    all.Add(sd);
                }
            }
            eligible = all.Count;
            all.Sort(delegate(SimDescription x, SimDescription y) { return x.SimDescriptionId.CompareTo(y.SimDescriptionId); });
            ulong c = cursor;
            int start = all.FindIndex(delegate(SimDescription sd) { return sd.SimDescriptionId > c; });
            if (start < 0) start = 0;
            List<SimDescription> next = new List<SimDescription>();
            for (int i = 0; i < n && i < all.Count; i++) next.Add(all[(start + i) % all.Count]);
            if (next.Count > 0) cursor = next[next.Count - 1].SimDescriptionId;
            return next;
        }

        static Dictionary<string, object> OutfitTaskReport()
        {
            Dictionary<string, object> d = Util.Obj();
            d["done"] = sOutfitTaskDone;
            d["skipped"] = sOutfitTaskSkipped;
            d["errors"] = sOutfitTaskErrors;
            d["last_error"] = sOutfitTaskLastError;
            sOutfitTaskDone = sOutfitTaskSkipped = sOutfitTaskErrors = 0;
            sOutfitTaskLastError = null;
            return d;
        }

        static void OutfitTaskError(Sim s, Exception e)
        {
            sOutfitTaskErrors++;
            sOutfitTaskLastError = (s != null ? s.FullName : "?") + ": " + e.GetType().Name + ": " + e.Message;
        }

        // bad_tattoo {count?: 10 (1..50)}: the next `count` townies (in the world or not) get a failure tattoo, as
        // TattooChair.AddFailureTattoo: Tattooing.ApplyTattoo(<age><gender>tattoofailure<0..5>), which rebuilds every outfit
        // of the Sim with the new Tattoo part (one slot: the last bad tattoo replaces the one before). Sims in the world then
        // switch into the rebuilt outfit (ApplyTattooChanges: SwitchOutfitHelper Start, Wait, ChangeOutfit, Dispose; a mature
        // mermaid's tail outfit rebuilt) in a game task. Returns tattooed, errors, eligible and last (the last call's task).
        static object BadTattoo(Args a)
        {
            RequireLive();
            int n = (int)a.Long("count", 10);
            if (n < 1 || n > 50) throw new McpException("count must be 1..50");
            Dictionary<string, object> r = Util.Obj();
            r["last"] = OutfitTaskReport();
            int eligible;
            List<SimDescription> next = NextTownies(ref sTattooCursor, n, false, out eligible);
            // al360 (11-09): ten ApplyTattoo calls in the command (each a full outfit build) held the main thread past the
            // 20 s reply limit, and at 14:56 past 60 s, so the harness killed the game (heartbeat frozen). Now a game task
            // does one Sim per frame and the command answers at once.
            List<object> tattooed = new List<object>();
            foreach (SimDescription sd in next) tattooed.Add(sd.FullName);
            if (next.Count > 0)
                Simulator.AddObject(new OneShotFunctionTask(delegate
                {
                    foreach (SimDescription sd in next)
                    {
                        Sim s = null;
                        try
                        {
                            if (sd.Household == null || sd.Household == Household.ActiveHousehold) { sOutfitTaskSkipped++; continue; }
                            string outfit = OutfitUtils.GetAgePrefix(sd.Age, true) + OutfitUtils.GetGenderPrefix(sd.Gender) + "tattoofailure" + RandomUtil.GetInt(0, 5);
                            long t0 = DateTime.Now.Ticks;
                            Tattooing.ApplyTattoo(sd, outfit);
                            long ms = (DateTime.Now.Ticks - t0) / 10000;
                            sTattooMs += ms; sTattooN++; if (ms > sTattooMaxMs) sTattooMaxMs = ms;
                            s = Util.InWorldNow(sd) ? sd.CreatedSim : null;
                            if (s != null && !s.HasBeenDestroyed && s.InWorld)
                            {
                                Sim.SwitchOutfitHelper h = new Sim.SwitchOutfitHelper(s, s.CurrentOutfitCategory, s.CurrentOutfitIndex);
                                h.Start();
                                h.Wait(false);
                                h.ChangeOutfit();
                                h.Dispose();
                                if (s.SimDescription.IsMatureMermaid) Sims3.Gameplay.ActorSystems.OccultMermaid.CreateTailOutfit(s.SimDescription);
                            }
                            sOutfitTaskDone++;
                        }
                        catch (Exception e) { OutfitTaskError(s, e); }
                        Simulator.Sleep(0);  // one outfit build per frame
                    }
                }));
            r["tattooed"] = tattooed;
            r["tattoo_ms"] = sTattooN > 0 ? sTattooMs / sTattooN : 0;
            r["tattoo_max_ms"] = sTattooMaxMs;
            r["eligible"] = eligible;
            return r;
        }

        // bad_makeover {count?: 5 (1..30), kind?: "bad" | "good"}: the next `count` townies in the world get a stylist makeover
        // in a game task, as FinishReceiveBadMakeover without the stylist: Styling.LoadMakeoverOutfitForClothesSpin, then the
        // switch (Wait, ChangeOutfit, Dispose). bad = EA's epic failure (a makeover-failure uniform into the Makeover category,
        // the Makeover outfit before it released); good = a player-directed makeover (a stylish uniform over the current
        // category's outfit, kept: at 3 outfits the current one is released; the uniform also goes into the household's
        // wardrobe, Household.AddOutfitToWardrobe). Needs Ambitions (the uniforms are EP2). Returns queued, eligible and last.
        static object BadMakeover(Args a)
        {
            RequireLive();
            int n = (int)a.Long("count", 5);
            if (n < 1 || n > 30) throw new McpException("count must be 1..30");
            string kind = a.Str("kind", "bad").ToLowerInvariant();
            if (kind != "bad" && kind != "good") throw new McpException("kind: bad or good");
            bool bad = kind == "bad";
            Dictionary<string, object> r = Util.Obj();
            r["last"] = OutfitTaskReport();
            int eligible;
            List<SimDescription> next = NextTownies(ref sMakeoverCursor, n, true, out eligible);
            List<Sim> sims = new List<Sim>();
            List<object> names = new List<object>();
            foreach (SimDescription sd in next) { sims.Add(sd.CreatedSim); names.Add(sd.FullName); }
            if (sims.Count > 0)
                Simulator.AddObject(new OneShotFunctionTask(delegate
                {
                    foreach (Sim s in sims)
                    {
                        try
                        {
                            if (s.HasBeenDestroyed || !s.InWorld) { sOutfitTaskSkipped++; continue; }
                            Sim.SwitchOutfitHelper h = null;
                            Styling.LoadMakeoverOutfitForClothesSpin(s, bad, bad, ref h);  // isAutonomous false for good: EA's permanent makeover
                            if (h == null) { sOutfitTaskSkipped++; continue; }  // no uniform for this age and gender
                            h.Wait(false);
                            h.ChangeOutfit();
                            h.Dispose();
                            sOutfitTaskDone++;
                        }
                        catch (Exception e) { OutfitTaskError(s, e); }
                    }
                }));
            r["queued"] = names;
            r["kind"] = kind;
            r["eligible"] = eligible;
            return r;
        }
    }
}
