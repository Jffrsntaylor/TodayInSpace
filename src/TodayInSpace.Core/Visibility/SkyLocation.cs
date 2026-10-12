namespace TodayInSpace.Core.Visibility
{
    // Checks and coarsens a visitor's location before anything is computed or cached.
    public static class SkyLocation
    {
        // 0.05° is about 5 km. Moving that far shifts a pass by seconds, and it means nearby visitors
        // share one cache entry and the server never works with a precise location.
        public const double GridDegrees = 0.05;

        public static bool IsValid(double? lat, double? lon) =>
            lat is double la && lon is double lo &&
            double.IsFinite(la) && double.IsFinite(lo) &&
            la >= -90 && la <= 90 && lo >= -180 && lo <= 180;

        // The final Math.Round tidies float noise, so 47.05 doesn't come out as 47.050000000000004.
        public static double Round(double degrees) =>
            Math.Round(Math.Round(degrees / GridDegrees) * GridDegrees, 2);

        public static string CacheKey(double lat, double lon) =>
            FormattableString.Invariant($"sky:visible:{Round(lat):F2}:{Round(lon):F2}");
    }
}
