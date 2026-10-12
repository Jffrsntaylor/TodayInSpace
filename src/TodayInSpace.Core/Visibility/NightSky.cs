using CosineKitty;

namespace TodayInSpace.Core.Visibility
{
    // Tonight's dark hours: from when the Sun sinks to -6° in the evening (or now, if it's already dark)
    // to when it climbs back to -6° in the morning.
    public record Darkness(DateTime Start, DateTime End);

    // One naked-eye planet during tonight's darkness. Best is its highest point while it's dark.
    // Rise and Set are only given when they happen during darkness, which is when they change what you see.
    // Reason says why a planet can't be seen: "nearSun" (lost in the Sun's glare) or "belowHorizon".
    public record PlanetTonight(
        string Id,
        string Name,
        bool Visible,
        double Magnitude,
        SkyPoint Best,
        DateTime? Rise,
        DateTime? Set,
        double Elongation,
        string? Reason);

    // Illumination is 0..1. Rise and Set are only given when they happen during darkness.
    public record MoonTonight(string Phase, double Illumination, DateTime? Rise, DateTime? Set, bool UpDuringDarkness);

    // Darkness is null when the Sun never gets 6° below the horizon tonight (summer near the poles).
    public record Tonight(Darkness? Darkness, IReadOnlyList<PlanetTonight> Planets, MoonTonight Moon);

    // The planets and Moon for one place tonight, using Astronomy Engine.
    public static class NightSky
    {
        // The same "dark enough" line the satellite passes use: the end of civil twilight.
        public const double DarkSunAltitude = PassFinder.MaxSunAltitude;
        // Below 10° a planet is lost in haze, trees and buildings for most people.
        public const double MinPlanetAltitude = 10;
        // Closer than this to the Sun, a planet is only ever up in bright twilight.
        public const double NearSunDegrees = 15;

        // Planets move across the sky about 2.5° in 10 minutes, so this can't skip over a peak.
        private static readonly TimeSpan Step = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan Precision = TimeSpan.FromMinutes(1);

        // The five planets you can see without a telescope, in the order the page lists them.
        public static readonly IReadOnlyList<(Body Body, string Id, string Name)> Planets = new[]
        {
            (Body.Mercury, "mercury", "Mercury"),
            (Body.Venus, "venus", "Venus"),
            (Body.Mars, "mars", "Mars"),
            (Body.Jupiter, "jupiter", "Jupiter"),
            (Body.Saturn, "saturn", "Saturn"),
        };

        public static Tonight Build(double lat, double lon, DateTime nowUtc)
        {
            var observer = new Observer(lat, lon, 0);
            var darkness = FindDarkness(observer, nowUtc);
            if (darkness == null)
                return new Tonight(null, Array.Empty<PlanetTonight>(), Moon(observer, nowUtc, null));

            var planets = Planets.Select(p => Planet(observer, p.Body, p.Id, p.Name, darkness)).ToList();
            return new Tonight(darkness, planets, Moon(observer, darkness.Start, darkness));
        }

        public static Darkness? FindDarkness(double lat, double lon, DateTime nowUtc) =>
            FindDarkness(new Observer(lat, lon, 0), nowUtc);

        private static Darkness? FindDarkness(Observer observer, DateTime nowUtc)
        {
            DateTime start;
            if (SunAltitude(observer, nowUtc) <= DarkSunAltitude)
                start = nowUtc;
            else
            {
                // No dusk in the next day means the Sun stays too high all night.
                var dusk = Astronomy.SearchAltitude(Body.Sun, observer, Direction.Set, new AstroTime(nowUtc), 1, DarkSunAltitude);
                if (dusk == null)
                    return null;
                start = Utc(dusk);
            }

            // In polar winter there's no dawn to find; "tonight" is then just the next day.
            var dawn = Astronomy.SearchAltitude(Body.Sun, observer, Direction.Rise, new AstroTime(start), 1, DarkSunAltitude);
            return new Darkness(Second(start), Second(dawn != null ? Utc(dawn) : start.AddDays(1)));
        }

        private static PlanetTonight Planet(Observer observer, Body body, string id, string name, Darkness darkness)
        {
            var best = Highest(observer, body, darkness);
            var at = new AstroTime(best.Time);
            double magnitude = Astronomy.Illumination(body, at).mag;
            double elongation = Astronomy.AngleFromSun(body, at);

            bool visible = best.Elevation >= MinPlanetAltitude;
            string? reason = visible ? null : elongation < NearSunDegrees ? "nearSun" : "belowHorizon";

            return new PlanetTonight(id, name, visible, Math.Round(magnitude, 1), Rounded(best),
                RiseOrSet(observer, body, Direction.Rise, darkness),
                RiseOrSet(observer, body, Direction.Set, darkness),
                Math.Round(elongation, 1), reason);
        }

        private static MoonTonight Moon(Observer observer, DateTime when, Darkness? darkness)
        {
            var at = new AstroTime(when);
            string phase = PhaseId(Astronomy.MoonPhase(at));
            double illumination = Math.Round(Astronomy.Illumination(Body.Moon, at).phase_fraction, 2);
            if (darkness == null)
                return new MoonTonight(phase, illumination, null, null, UpDuringDarkness: false);

            // Up at any moment of the dark hours, refined only as far as the 10-minute steps.
            bool up = Samples(darkness).Any(t => Look(observer, Body.Moon, t).Elevation > 0);
            return new MoonTonight(phase, illumination,
                RiseOrSet(observer, Body.Moon, Direction.Rise, darkness),
                RiseOrSet(observer, Body.Moon, Direction.Set, darkness),
                up);
        }

        // Phase angle: 0 = new, 90 = first quarter, 180 = full, 270 = last quarter. The four main phases
        // get about a day either side (the Moon moves ~12° a day against the Sun); "full moon" at 85% lit
        // would look wrong, so the in-between phases take the rest.
        public static string PhaseId(double angle)
        {
            const double Margin = 12;
            angle = (angle % 360 + 360) % 360;
            if (angle < Margin || angle >= 360 - Margin) return "newMoon";
            if (angle < 90 - Margin) return "waxingCrescent";
            if (angle < 90 + Margin) return "firstQuarter";
            if (angle < 180 - Margin) return "waxingGibbous";
            if (angle < 180 + Margin) return "fullMoon";
            if (angle < 270 - Margin) return "waningGibbous";
            if (angle < 270 + Margin) return "lastQuarter";
            return "waningCrescent";
        }

        // Steps through the dark hours, then narrows in on the highest sample to about a minute.
        private static SkyPoint Highest(Observer observer, Body body, Darkness darkness)
        {
            DateTime best = Samples(darkness).MaxBy(t => Look(observer, body, t).Elevation);

            // Within a step either side the altitude has a single peak, so a ternary search finds it.
            DateTime a = best - Step < darkness.Start ? darkness.Start : best - Step;
            DateTime b = best + Step > darkness.End ? darkness.End : best + Step;
            while (b - a > Precision)
            {
                DateTime m1 = a + (b - a) / 3, m2 = b - (b - a) / 3;
                if (Look(observer, body, m1).Elevation < Look(observer, body, m2).Elevation) a = m1; else b = m2;
            }
            return Look(observer, body, Second(a + (b - a) / 2));
        }

        private static IEnumerable<DateTime> Samples(Darkness darkness)
        {
            for (DateTime t = darkness.Start; t < darkness.End; t += Step)
                yield return t;
            yield return darkness.End;
        }

        private static DateTime? RiseOrSet(Observer observer, Body body, Direction direction, Darkness darkness)
        {
            double days = (darkness.End - darkness.Start).TotalDays;
            var found = Astronomy.SearchRiseSet(body, observer, direction, new AstroTime(darkness.Start), days, 0);
            return found == null ? null : Second(Utc(found));
        }

        // Altitude includes normal refraction, as you'd see it and as planetarium apps show it.
        private static SkyPoint Look(Observer observer, Body body, DateTime time)
        {
            var at = new AstroTime(time);
            var sky = Astronomy.Equator(body, at, observer, EquatorEpoch.OfDate, Aberration.Corrected);
            var horizon = Astronomy.Horizon(at, observer, sky.ra, sky.dec, Refraction.Normal);
            return new SkyPoint(time, horizon.azimuth, horizon.altitude);
        }

        // Twilight is defined without refraction, the same way SearchAltitude and PassFinder measure it.
        private static double SunAltitude(Observer observer, DateTime time)
        {
            var at = new AstroTime(time);
            var sky = Astronomy.Equator(Body.Sun, at, observer, EquatorEpoch.OfDate, Aberration.Corrected);
            return Astronomy.Horizon(at, observer, sky.ra, sky.dec, Refraction.None).altitude;
        }

        private static SkyPoint Rounded(SkyPoint p) =>
            new(p.Time, Math.Round(p.Azimuth, 1) % 360, Math.Round(p.Elevation, 1));

        private static DateTime Utc(AstroTime time) => DateTime.SpecifyKind(time.ToUtcDateTime(), DateTimeKind.Utc);

        // Whole seconds keep the JSON tidy; nothing here is good to better than a minute anyway.
        private static DateTime Second(DateTime t) =>
            new(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    }
}
