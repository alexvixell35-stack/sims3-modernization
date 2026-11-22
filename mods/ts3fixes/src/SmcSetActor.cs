using System;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // StateMachineClient.SetActor, the moment an object or Sim becomes an actor of a state machine (docs/686 §14-§15).
    // servingshare (ObjShare.cs) and showerpee (ShowerShare.cs) read it while a run of theirs is open. On the contributor's combo branch the same two
    // lines sit in ActDelay.cs's SetActor hook; ActDelay is not upstream, so this is the minimum on its own.
    // The swap yields (SetActor > ... > RequestState): saved stacks get the stub remap (docs/242). Off: one int test.
    public static partial class Fixes
    {
        static void ApplySmcSetActor()
        {
            Patch(typeof(StateMachineClient), "SetActor", new Type[] { typeof(string), typeof(IHasScriptProxy) }, typeof(SmcHook), "SetActorHook", "SetActorOrig");
        }

        public class SmcHook
        {
            public void SetActorHook(string actorName, IHasScriptProxy actor)
            {
                if (sOjsArmed > 0) OjsSetActor(actor);  // docs/686 §14 servingshare (ObjShare.cs): a serving run is open
                if (sShpArmed > 0) ShpSetActor(actor);  // docs/686 §15 showerpee (ShowerShare.cs): a shower that refills a bladder is open
                SetActorOrig(actorName, actor);
            }
            public void SetActorOrig(string actorName, IHasScriptProxy actor) { }  // receives SetActor's original body
        }
    }
}
