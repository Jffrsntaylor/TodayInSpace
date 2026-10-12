namespace TodayInSpace.Core.Visibility
{
    // Is a satellite lit by the Sun, or inside Earth's shadow?
    public static class EarthShadow
    {
        public const double EarthRadiusKm = 6378.137;

        // A cylinder model: the shadow is treated as a tube of Earth's radius pointing straight away from the Sun.
        // The real umbra narrows very slowly and the penumbra is thin at low orbit, so at worst this moves the
        // moment a satellite fades by a few seconds.
        //
        // satellite: Earth-centred position in km. sunDirection: any vector from Earth toward the Sun.
        // SGP4 gives positions in TEME and the Sun comes in J2000. The two frames differ by about a third of a
        // degree, which shifts the shadow edge by roughly 40 km, or a few seconds of an ISS pass.
        public static bool IsSunlit(double x, double y, double z, double sunX, double sunY, double sunZ)
        {
            double sunLength = Math.Sqrt(sunX * sunX + sunY * sunY + sunZ * sunZ);
            double sx = sunX / sunLength, sy = sunY / sunLength, sz = sunZ / sunLength;

            // On the day side of Earth: always lit.
            double along = x * sx + y * sy + z * sz;
            if (along >= 0)
                return true;

            // On the night side: lit only if it's farther from the Earth-Sun line than Earth's radius.
            double px = x - along * sx, py = y - along * sy, pz = z - along * sz;
            return Math.Sqrt(px * px + py * py + pz * pz) >= EarthRadiusKm;
        }
    }
}
