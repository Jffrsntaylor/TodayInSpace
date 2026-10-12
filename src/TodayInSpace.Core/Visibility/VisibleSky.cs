namespace TodayInSpace.Core.Visibility
{
    // The JSON returned by GET /api/sky/visible. This is a contract with the website and any future app:
    // fields may be added later, but never renamed or removed. VisibleSkyShapeTests locks the names.
    // Times are UTC, angles are degrees, azimuth is a compass bearing (0 = north).
    public record VisibleSkyResponse(
        double Lat,
        double Lon,
        DateTime Generated,
        int Days,
        IReadOnlyList<VisiblePass> Passes,
        IReadOnlyList<SatelliteVisibility> Satellites);

    public record VisiblePass(string Satellite, string Name, SkyPoint Start, SkyPoint Max, SkyPoint End, int DurationSeconds);

    // Available = false when the satellite's orbit data couldn't be loaded, so it has no predictions at all.
    public record SatelliteVisibility(string Id, string Name, int VisiblePasses, bool AnyPassAbove10, bool Available);

    public record SatelliteOrbit(string Id, string Name, string? TleLine1, string? TleLine2);

    public static class VisibleSky
    {
        public const int Days = 7;

        // lat/lon should already be rounded (SkyLocation.Round). A satellite without orbit data is left out of
        // the passes and marked unavailable, and the rest are still computed.
        public static VisibleSkyResponse Build(double lat, double lon, DateTime nowUtc, IEnumerable<SatelliteOrbit> satellites)
        {
            var passes = new List<VisiblePass>();
            var summaries = new List<SatelliteVisibility>();
            var now = new DateTime(nowUtc.Ticks - nowUtc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

            foreach (var sat in satellites)
            {
                if (sat.TleLine1 == null || sat.TleLine2 == null)
                {
                    summaries.Add(new SatelliteVisibility(sat.Id, sat.Name, 0, AnyPassAbove10: false, Available: false));
                    continue;
                }

                var found = new PassFinder(sat.TleLine1, sat.TleLine2, lat, lon).Find(now, TimeSpan.FromDays(Days));
                passes.AddRange(found.Visible.Select(p =>
                    new VisiblePass(sat.Id, sat.Name, Rounded(p.Start), Rounded(p.Max), Rounded(p.End), p.DurationSeconds)));
                summaries.Add(new SatelliteVisibility(sat.Id, sat.Name, found.Visible.Count, found.AnyPassAbove10, Available: true));
            }

            return new VisibleSkyResponse(lat, lon, now, Days, passes.OrderBy(p => p.Start.Time).ToList(), summaries);
        }

        // A tenth of a degree is more than a predictor this simple can promise.
        private static SkyPoint Rounded(SkyPoint p) =>
            new(p.Time, Math.Round(p.Azimuth, 1) % 360, Math.Round(p.Elevation, 1));
    }
}
