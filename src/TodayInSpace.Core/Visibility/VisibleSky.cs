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
        IReadOnlyList<SatelliteVisibility> Satellites)
    {
        // Added for the planets and Moon. They're init properties rather than constructor arguments so the
        // original fields (and anything already built against them) stay exactly as they were.
        public Darkness? Darkness { get; init; }
        public IReadOnlyList<PlanetTonight> Planets { get; init; } = Array.Empty<PlanetTonight>();
        public MoonTonight? Moon { get; init; }

        // Added for "will I actually see it?". Place and Clouds come from the National Weather Service and are
        // null outside the US (or if NWS is down). Aurora is null only if it couldn't be worked out at all.
        public Place? Place { get; init; }
        public AuroraOutlook? Aurora { get; init; }
        public CloudOutlook? Clouds { get; init; }
    }

    public record VisiblePass(string Satellite, string Name, SkyPoint Start, SkyPoint Max, SkyPoint End, int DurationSeconds)
    {
        // Cloud cover at the highest point of the pass; null without a cloud forecast.
        public int? CloudPercent { get; init; }
    }

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

            var tonight = NightSky.Build(lat, lon, now);
            return new VisibleSkyResponse(lat, lon, now, Days, passes.OrderBy(p => p.Start.Time).ToList(), summaries)
            {
                Darkness = tonight.Darkness,
                Planets = tonight.Planets,
                Moon = tonight.Moon,
            };
        }

        // Adds the place name, aurora odds and cloud cover to an already computed sky, and puts the cloud cover on
        // each pass (at its highest point) and each visible planet (at its best time).
        public static VisibleSkyResponse WithConditions(VisibleSkyResponse sky, Place? place, AuroraOutlook? aurora, SkyCover? cover)
        {
            var clouds = cover == null ? null : NwsWeather.Outlook(cover, sky.Darkness, sky.Generated, sky.Generated.AddDays(sky.Days));
            if (clouds == null)
                return sky with { Place = place, Aurora = aurora };

            return sky with
            {
                Place = place,
                Aurora = aurora,
                Clouds = clouds,
                Passes = sky.Passes.Select(p => p with { CloudPercent = NwsWeather.PercentAt(clouds.Series, p.Max.Time) }).ToList(),
                Planets = sky.Planets.Select(p => p.Visible ? p with { CloudPercent = NwsWeather.PercentAt(clouds.Series, p.Best.Time) } : p).ToList(),
            };
        }

        // A tenth of a degree is more than a predictor this simple can promise.
        private static SkyPoint Rounded(SkyPoint p) =>
            new(p.Time, Math.Round(p.Azimuth, 1) % 360, Math.Round(p.Elevation, 1));
    }
}
