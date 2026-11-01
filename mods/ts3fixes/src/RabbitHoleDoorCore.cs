using System;

namespace Ts3Fixes
{
    // rabbitholedoor (docs/505): the rule the door hooks apply to an exception from EA's door request, with no game types,
    // so tools/selftest/rabbitholedoor can run it offline.
    public static class RabbitHoleDoorCore
    {
        public const string kSacsError = "Sims3.SimIFace.SacsErrorException";

        // true = skip it (switch on and a SACS request error); false = let it go on up, as EA's code does
        public static bool Swallow(Exception e, bool on)
        {
            return on && e != null && e.GetType().FullName == kSacsError;
        }
    }
}
