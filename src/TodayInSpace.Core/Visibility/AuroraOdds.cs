using System.Globalization;

namespace TodayInSpace.Core.Visibility
{
    // One day of NOAA's 3-day Kp forecast (the highest Kp expected that UTC day).
    public record KpDay(DateOnly Date, int Kp);

    // Chance is "overhead", "horizon" (low in the north, or the south below the equator) or "unlikely".
    public record AuroraNight(string Date, int Kp, string Chance);

    // KpForHorizon / KpForOverhead are null when even Kp 9 isn't enough (e.g. Miami).
    // NowPercent is OVATION's chance of aurora overhead right now; 0 means under 5%, null means the feed is down.
    public record AuroraOutlook(
        double GeomagneticLatitude,
        int? KpForHorizon,
        int? KpForOverhead,
        int? NowPercent,
        IReadOnlyList<AuroraNight> Nights);

    // "Could I see the aurora from here?", from data the site already has: the Kp forecast in the daily digest
    // and NOAA's OVATION grid. A rule of thumb on purpose; a better aurora model can replace it later.
    public static class AuroraOdds
    {
        // Geomagnetic north pole of the centered dipole, IGRF-14 at epoch 2025.0 (80.8° N, 72.6° W),
        // as listed in NOAA NCEI's "Geomagnetic poles" table. It drifts about a tenth of a degree a year.
        public const double PoleLat = 80.8;
        public const double PoleLon = -72.6;

        // Geomagnetic latitude where the aurora is overhead, by Kp 0-9: about 66.5° − 2.05° × Kp.
        // A rough fit to the equatorward edge of the auroral oval, which is what most people see.
        // This is the one table to tune when we have something better.
        public static readonly IReadOnlyList<double> OverheadLatitudeByKp = new[]
        {
            66.5, 64.45, 62.4, 60.35, 58.3, 56.25, 54.2, 52.15, 50.1, 48.05,
        };

        // A tall aurora can be seen low on the poleward horizon from about 5° further from the pole.
        public const double HorizonExtraDegrees = 5;

        // Centered dipole: the angle from the geomagnetic equator. Negative in the southern geomagnetic hemisphere,
        // which is the mirror image, so the rest of the class works on its absolute value.
        public static double GeomagneticLatitude(double lat, double lon)
        {
            double phi = Radians(lat), pole = Radians(PoleLat);
            double sin = Math.Sin(phi) * Math.Sin(pole) +
                         Math.Cos(phi) * Math.Cos(pole) * Math.Cos(Radians(lon - PoleLon));
            return Math.Asin(Math.Clamp(sin, -1, 1)) * 180 / Math.PI;
        }

        public static int? KpForOverhead(double geomagneticLatitude) =>
            LowestKpReaching(Math.Abs(geomagneticLatitude));

        public static int? KpForHorizon(double geomagneticLatitude) =>
            LowestKpReaching(Math.Abs(geomagneticLatitude) + HorizonExtraDegrees);

        public static string Chance(int kp, int? kpForHorizon, int? kpForOverhead) =>
            kpForOverhead is int overhead && kp >= overhead ? "overhead"
            : kpForHorizon is int horizon && kp >= horizon ? "horizon"
            : "unlikely";

        // OVATION's grid runs 0-359° east; ours runs -180..180. -122.9 -> 237.1.
        public static double ToOvationLongitude(double lon) => ((lon % 360) + 360) % 360;

        // points are [longitude -180..180, latitude, percent] on OVATION's 1° grid, with everything under 5% left out,
        // so a missing point means "under 5%" and comes back as 0.
        public static int NowPercent(IEnumerable<int[]> points, double lat, double lon)
        {
            int gridLat = (int)Math.Round(lat);
            int gridLon = (int)Math.Round(ToOvationLongitude(lon)) % 360;   // 359.6 rounds to 360, which is 0

            foreach (var p in points)
            {
                if (p.Length >= 3 && p[1] == gridLat && (int)ToOvationLongitude(p[0]) == gridLon)
                    return p[2];
            }
            return 0;
        }

        public static AuroraOutlook Build(double lat, double lon, IEnumerable<int[]>? ovationPoints, IEnumerable<KpDay> forecast)
        {
            double gm = GeomagneticLatitude(lat, lon);
            int? horizon = KpForHorizon(gm), overhead = KpForOverhead(gm);

            var nights = forecast
                .OrderBy(d => d.Date)
                .Select(d => new AuroraNight(d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), d.Kp, Chance(d.Kp, horizon, overhead)))
                .ToList();

            int? now = ovationPoints == null ? null : NowPercent(ovationPoints, lat, lon);
            return new AuroraOutlook(Math.Round(gm, 1), horizon, overhead, now, nights);
        }

        // The digest stores each forecast day as a short day name ("Sun") starting on the digest's date, so the
        // real date is the first one on or after the digest's date with that name. Days we can't place are skipped,
        // as are days before yesterday (UTC): an evening in the Americas is already tomorrow in UTC.
        public static IReadOnlyList<KpDay> ForecastDays(string? digestDate, IEnumerable<(string? Day, int? Kp)> forecast, DateTime nowUtc)
        {
            if (!DateOnly.TryParseExact(digestDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                return Array.Empty<KpDay>();

            var oldest = DateOnly.FromDateTime(nowUtc).AddDays(-1);
            var days = new List<KpDay>();
            foreach (var (day, kp) in forecast)
            {
                if (kp is not int k || string.IsNullOrEmpty(day))
                    continue;

                int ahead = Enumerable.Range(0, 7).FirstOrDefault(i => ShortDayName(date.AddDays(i)) == day, -1);
                if (ahead < 0)
                    continue;

                date = date.AddDays(ahead);
                if (date >= oldest)
                    days.Add(new KpDay(date, k));
                date = date.AddDays(1);
            }
            return days;
        }

        private static string ShortDayName(DateOnly d) => d.ToString("ddd", CultureInfo.InvariantCulture);

        private static int? LowestKpReaching(double geomagneticLatitude)
        {
            for (int kp = 0; kp < OverheadLatitudeByKp.Count; kp++)
            {
                if (OverheadLatitudeByKp[kp] <= geomagneticLatitude)
                    return kp;
            }
            return null;
        }

        private static double Radians(double degrees) => degrees * Math.PI / 180;
    }
}
