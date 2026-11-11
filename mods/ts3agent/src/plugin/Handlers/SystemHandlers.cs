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
            RegisterReflect();
            RegisterSims();
            RegisterControl();
            RegisterBuild();
            RegisterSaver();
            RegisterHarness();
            RegisterPets(); // PetHandlers.cs
            RegisterSocialRound(); // SocialRoundHandlers.cs
            RegisterCareerLadder(); // CareerLadderHandlers.cs
            RegisterHoldSims(); // HoldSimsHandlers.cs
            RegisterOutfitStress(); // OutfitStressHandlers.cs
            RegisterThumbCensus(); // ThumbCensusHandlers.cs
            RegisterBoatGone(); // BoatGoneHandlers.cs
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

        static object ListCommands(Args a)
        {
            List<string> names = new List<string>(Dispatcher.Commands);
            names.Sort(StringComparer.Ordinal);
            return names;
        }
    }
}
