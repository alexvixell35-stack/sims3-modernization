using System;

namespace Ts3Agent
{
    // Game-mode gates (docs/179 §3): commands that act on Sims or the lot refuse outside the modes a player could send them in,
    // with "not available: <state> (...)". Build, Buy, CAS and Edit Town stop the clock and hide the pie menus
    // (LotManager.EnterBuildBuyMode / CASModeState lock the speed at Pause [verified static]); a Sim action sent there is
    // nothing a player can do, and a Sim created there crashed the game (0x575651). Handlers that already call RequireLive
    // are not listed. Called last in RegisterAll: re-registering wraps the plain handlers.
    public static partial class Handlers
    {
        static readonly string[] kLiveOnly = { "LiveModeState" };
        static readonly string[] kLiveOrBuy = { "LiveModeState", "BuyModeState" };

        static Handler Gate(string[] states, Handler h)
        {
            return delegate(Args a)
            {
                string s = SubState();
                if (Array.IndexOf(states, s) < 0) throw new McpException("not available: " + s + " (" + string.Join(" or ", states) + " only)");
                return h(a);
            };
        }

        static void RegisterGates()
        {
            Dispatcher.Register("do_interaction", Gate(kLiveOnly, DoInteraction));
            Dispatcher.Register("cancel_interactions", Gate(kLiveOnly, CancelInteractions));
            Dispatcher.Register("go_here", Gate(kLiveOnly, GoHere));
            Dispatcher.Register("sim_visit_lot", Gate(kLiveOnly, SimVisitLot));
            Dispatcher.Register("sim_route_to", Gate(kLiveOnly, SimRouteTo));
            Dispatcher.Register("place_sim", Gate(kLiveOnly, PlaceSim));
            Dispatcher.Register("setup_sims", Gate(kLiveOnly, SetupSims));
            Dispatcher.Register("sim_go_home", Gate(kLiveOnly, SimGoHome));
            Dispatcher.Register("sim_strand", Gate(kLiveOnly, SimStrand));
            Dispatcher.Register("strand_many", Gate(kLiveOnly, StrandMany));
            Dispatcher.Register("route_replan", Gate(kLiveOnly, RouteReplan));
            Dispatcher.Register("sim_portal_trip", Gate(kLiveOnly, SimPortalTrip));
            Dispatcher.Register("set_active_sim", Gate(kLiveOnly, SetActiveSim));
            Dispatcher.Register("set_motive", Gate(kLiveOnly, SetMotive));
            Dispatcher.Register("set_skill", Gate(kLiveOnly, SetSkill));
            Dispatcher.Register("set_trait", Gate(kLiveOnly, SetTrait));
            Dispatcher.Register("moodlet", Gate(kLiveOnly, Moodlet));
            Dispatcher.Register("lifetime_happiness", Gate(kLiveOnly, LifetimeHappiness));
            Dispatcher.Register("modify_relationship", Gate(kLiveOnly, ModifyRelationship));
            Dispatcher.Register("career", Gate(kLiveOnly, CareerCmd));
            Dispatcher.Register("age_up", Gate(kLiveOnly, AgeUp));
            Dispatcher.Register("vfx_burst", Gate(kLiveOnly, VfxBurst));
            Dispatcher.Register("camera_jumps", Gate(kLiveOnly, CameraJumps));
            Dispatcher.Register("travel_vacation", Gate(kLiveOnly, TravelVacation));
            Dispatcher.Register("travel_university", Gate(kLiveOnly, TravelUniversity));
            Dispatcher.Register("travel_future", Gate(kLiveOnly, TravelFuture));
            Dispatcher.Register("portal_prepare", Gate(kLiveOnly, PortalPrepare));
            Dispatcher.Register("buy_object", Gate(kLiveOrBuy, BuyObject));
            Dispatcher.Register("sell_object", Gate(kLiveOrBuy, SellObject));
            Dispatcher.Register("move_object", Gate(kLiveOrBuy, MoveObject));
            Dispatcher.Register("place_object", Gate(kLiveOrBuy, PlaceObject));
            Dispatcher.Register("replace_object", Gate(kLiveOrBuy, ReplaceObject));
        }
    }
}
