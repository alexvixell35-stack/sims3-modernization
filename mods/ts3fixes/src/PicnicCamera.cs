using System;
using System.Reflection;
using Sims3.Gameplay.Objects.CookingObjects;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // picniccamera (tuning kPicnicCamera, off by default; docs/566 §5). EA bug fix in PicnicBasket "Serve".
    //
    // [verified, decompiled] PicnicBasket.Serve.Run adds OnMapViewEnabled to the static
    // CameraController.OnCameraMapViewEnabledCallback (and OnGameStateChanging to Responder.GameStateChanging) while the
    // blanket is placed with the hand tool. The failure path removes both; the success path removes only
    // GameStateChanging. Every served picnic leaves its Serve interaction on the static camera event for the rest of the
    // session: each later map-view toggle runs UserToolUtils.OnClose (closes whatever placement tool is open), and the
    // interaction (Sim, basket, blanket refs) is never freed.
    // Fix: Serve.Run runs inside try/finally; afterwards this instance's OnMapViewEnabled is removed from the camera
    // event (EA's own -= with the same handler; a no-op when EA already removed it). GameStateChanging is left to EA
    // (its handler also unlocks the active lot).
    //
    // Counters: [2548] camera callbacks removed, [2549] errors. Check: PicnicCameraChecks.cs.
    public static partial class Fixes
    {
        static readonly MethodInfo sPcHandler = typeof(PicnicBasket.Serve).GetMethod("OnMapViewEnabled", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        static readonly FieldInfo sPcEvent = typeof(CameraController).GetField("OnCameraMapViewEnabledCallback", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        static void ApplyPicnicCamera()
        {
            if (sPcHandler == null)
            {
                AppliedLog().Add("picniccamera: PicnicBasket.Serve.OnMapViewEnabled not found, not patched");
                return;
            }
            Patch(typeof(PicnicBasket.Serve), "Run", typeof(PcHook), "RunHook", "RunOrig");
        }

        // ---- decision (also used by the check): remove the Serve's camera callback after Run? ----
        internal static bool PcRemove(bool on, bool stillRegistered)
        {
            return on && stillRegistered;
        }

        static int PcCount()
        {
            Delegate d = sPcEvent == null ? null : sPcEvent.GetValue(null) as Delegate;
            return d == null ? 0 : d.GetInvocationList().Length;
        }

        public class PcHook
        {
            public bool RunHook()
            {
                try { return RunOrig(); }
                finally
                {
                    if (ShortcutOn("picniccamera"))
                    {
                        try
                        {
                            object st = this;
                            int before = PcCount();
                            CameraController.CameraMapViewEnabledHandler h = (CameraController.CameraMapViewEnabledHandler)Delegate.CreateDelegate(typeof(CameraController.CameraMapViewEnabledHandler), st, sPcHandler);
                            CameraController.OnCameraMapViewEnabledCallback -= h;
                            if (sPcEvent == null || PcRemove(true, PcCount() < before)) PatchCounters()[2548]++;
                        }
                        catch (Exception) { PatchCounters()[2549]++; }
                    }
                }
            }
            public bool RunOrig() { return false; } // receives PicnicBasket.Serve.Run's original body
        }
    }
}
