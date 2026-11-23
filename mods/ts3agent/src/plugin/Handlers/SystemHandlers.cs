using System;
using System.Collections.Generic;

namespace Ts3Agent
{
    public static partial class Handlers
    {
        public static void RegisterAll()
        {
            Dispatcher.Register("ping", Ping);
            Dispatcher.Register("commands", ListCommands);
            Dispatcher.Register("notify", Notify); // kit benchmark start / end notice (docs/315)
            RegisterReflect();
            RegisterSims();
            RegisterControl();
            RegisterBuild();
            RegisterSaver();
            RegisterHarness();
            RegisterPets(); // PetHandlers.cs
            RegisterSocialRound(); // SocialRoundHandlers.cs
            RegisterCareerLadder(); // CareerLadderHandlers.cs
            RegisterAutCensus(); // AutCensusHandlers.cs, docs/546 row 2
            RegisterHitchWatch(); RegisterRoutine(); // HitchWatchHandlers.cs, docs/546 §6
            RegisterAlarmAudit(); // AlarmAuditHandlers.cs, docs/546 row 5
            RegisterDoors(); // DoorHandlers.cs, docs/546 §8
            RegisterHoldSims(); // HoldSimsHandlers.cs
            RegisterTombMummy(); // TombMummyHandlers.cs, docs/607 §3 in-game repro
            RegisterOutfitStress(); // OutfitStressHandlers.cs
            RegisterThumbCensus(); // ThumbCensusHandlers.cs
            RegisterBoatGone(); // BoatGoneHandlers.cs
            RegisterBoatGetOutStress(); // BoatGetOutStressHandlers.cs
            RegisterBoatNoPathRepro(); // BoatNoPathReproHandlers.cs, docs/851
            RegisterRouteStorm(); // RouteStormHandlers.cs, docs/668
            RegisterSimHealth(); // SimHealthHandlers.cs: soak health census
            RegisterInteractionSweep(); // InteractionSweepHandlers.cs, docs/268
            RegisterFightOrphan(); // FightOrphanHandlers.cs, repro ip-fight-orphan
            RegisterHouseboatLegRepro(); // HouseboatLegReproHandlers.cs, docs/853 §2-§3
            RegisterBoatRoamRepro(); // BoatRoamReproHandlers.cs, docs/853 §4
            RegisterDishCensus(); // DishCensusHandlers.cs, docs/652
        }

        static object Ping(Args a)
        {
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["pong"] = true;
            r["mod_version"] = Instantiator.ModVersion;
            r["plugin"] = typeof(PluginEntry).Assembly.GetName().Name; // changes with every hot reload
            r["echo"] = a.Raw("echo");
            return r;
        }

        // notify {text}: one system notification in the top-right corner (what the kit benchmark tells the player). Text capped at 300.
        static object Notify(Args a)
        {
            string t = a.Str("text");
            if (t.Length > 300) t = t.Substring(0, 300);
            Sims3.UI.StyledNotification.Show(new Sims3.UI.StyledNotification.Format(t, Sims3.UI.StyledNotification.NotificationStyle.kSystemMessage));
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["shown"] = t;
            return r;
        }

        static object ListCommands(Args a)
        {
            List<string> names = new List<string>(Dispatcher.Commands);
            names.Sort(StringComparer.Ordinal);
            return names;
        }
    }
}
