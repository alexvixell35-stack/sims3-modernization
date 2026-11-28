namespace Ts3Fixes
{
    // Game-independent rule of elevatorlightrig (ElevatorLightRig.cs, docs/658 §7.1). Self-check:
    // tools/selftest/elevatorlightrig. ElevatorDoors.OnEnterElevator gives the rider the "CAS" custom light rig; only
    // OnExitElevator takes it off. Exit paths that skip OnExitElevator: the rider's portal action reset
    // (ElevatorPortalComponent.OnPortalActionReset, also reached from the doors' reset), the route ending while the Sim
    // is still in the elevator (ElevatorOnRouteActionsFinished: EA resets the Sim), the interior's OnReset (mAssignedSims
    // cleared) and owelevidle's drop of an idle entry. Clear on those paths when the Sim object is still valid and was
    // registered with (or inside) the elevator, and is not riding any more after EA's code.
    public static class ElevatorLightRigCore
    {
        public const int kNone = 0, kClear = 1, kSeenOff = 2;

        public static int Decide(bool on, bool simValid, bool wasRiding, bool stillRiding)
        {
            if (!simValid || !wasRiding || stillRiding) return kNone;
            return on ? kClear : kSeenOff;
        }
    }
}
