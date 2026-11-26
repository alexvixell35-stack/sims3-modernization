// Pure decisions of unseentraffic + npccarexit (UnseenTraffic.cs). No game types, so tools/selftest/unseentraffic compiles it
// with plain csc (the CarNoRouteCore pattern).
namespace Ts3Fixes
{
    public static class UnseenTrafficCore
    {
        // corridor test: a sample every kCorridorStep metres along the straight line car -> spot, both ends included.
        // shortcut: a straight line, not the road path; a road that bends into view between samples is not seen. Upgrade:
        // test the planned path's nodes, which would need the plan we are trying to avoid.
        public const float kCorridorStep = 50f;
        public const int kCorridorMaxSteps = 60; // 3 km: longer than any TS3 world's diagonal at 50 m steps

        public static int CorridorSteps(float sx, float sz, float gx, float gz)
        {
            float dx = gx - sx, dz = gz - sz;
            float d = (float)System.Math.Sqrt(dx * dx + dz * dz);
            int n = (int)(d / kCorridorStep);
            if (n < 1) n = 1;
            return n > kCorridorMaxSteps ? kCorridorMaxSteps : n;
        }

        // sample i of n (0 = start, n = goal)
        public static float Lerp(float a, float b, int i, int n) { return a + (b - a) * i / n; }

        // the time a teleported leg holds its spot, as EA's drive would: road length / Vehicle.CarSpeed (EA's tunable, metres
        // per sim minute, 18; EA's own service cars use distance = minutes x CarSpeed). shortcut: road length = straight x
        // kPathFactor (BP jk2 LiteDriver plans: path / straight 1.28-1.94, typical 1.3-1.4) and no acceleration; upgrade: a
        // per-world factor from the routetrace lines if a run shows legs far off.
        public const float kPathFactor = 1.4f, kDefaultSpeed = 18f, kMinLegMin = 1f, kMaxLegMin = 90f;

        public static float LegMinutes(float straight, float speed)
        {
            if (!(speed > 0.1f)) speed = kDefaultSpeed;  // also NaN
            float m = straight * kPathFactor / speed;
            if (!(m >= kMinLegMin)) return kMinLegMin;
            return m > kMaxLegMin ? kMaxLegMin : m;
        }

        // CarNpcManager.FindOffScreenPlaceOnRoad has three callers [ld-SGS.cs 372622, 372703, 372819]. Only
        // DriveAwayInNpcCar passes the car's own position with 100 m; the other two pass a lot/service point.
        public static bool IsDriveAwayAsk(float distance, float tx, float ty, float tz, float cx, float cy, float cz)
        {
            if (distance != 100f) return false;
            float dx = tx - cx, dy = ty - cy, dz = tz - cz;
            return dx * dx + dy * dy + dz * dz < 0.01f;
        }

        // memo key of an exit: start in kStartCell cells (the car stops a few metres apart on each visit), goal in 1 m cells
        // (the IC answers the same road point to the centimetre for the same start). 16 bits per coordinate: TS3 worlds are
        // under 4 km, cells wrap harmlessly beyond that (a wrong hit only re-asks for another exit).
        public const float kStartCell = 8f;

        public static ulong ExitKey(float sx, float sz, float gx, float gz)
        {
            ulong a = (ushort)(int)System.Math.Floor(sx / kStartCell), b = (ushort)(int)System.Math.Floor(sz / kStartCell);
            ulong c = (ushort)(int)System.Math.Floor(gx), d = (ushort)(int)System.Math.Floor(gz);
            return (a << 48) | (b << 32) | (c << 16) | d;
        }

        // a failed car plan belongs to the pending exit when its goal is that exit (route goals are the point asked for,
        // GetApproximateOriginalDestination; 1 m allows float noise)
        public static bool SameGoal(float ax, float az, float bx, float bz)
        {
            float dx = ax - bx, dz = az - bz;
            return dx * dx + dz * dz < 1f;
        }
    }
}
