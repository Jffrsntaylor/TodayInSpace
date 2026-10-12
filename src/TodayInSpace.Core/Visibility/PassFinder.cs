using CosineKitty;
using SGPdotNET.CoordinateSystem;
using SGPdotNET.Observation;
using SGPdotNET.Util;

namespace TodayInSpace.Core.Visibility
{
    // Where something is in the visitor's sky at a moment. Azimuth is a compass bearing (0 = north, 90 = east).
    public record SkyPoint(DateTime Time, double Azimuth, double Elevation);

    // The part of a pass you can actually see: start, highest point and end.
    public record PassWindow(SkyPoint Start, SkyPoint Max, SkyPoint End)
    {
        public int DurationSeconds => (int)Math.Round((End.Time - Start.Time).TotalSeconds);
    }

    // AnyPassAbove10 = false means the orbit never climbs high enough here, so "no passes" isn't about timing.
    public record PassSearch(IReadOnlyList<PassWindow> Visible, bool AnyPassAbove10);

    // Finds when a satellite can be seen with the naked eye from one place. A moment counts as visible when
    // the satellite is at least 10° up, the visitor's sky is dark (Sun at or below -6°, the end of civil
    // twilight), and the satellite itself is still in sunlight. That last one is why the ISS is mostly seen
    // in the hours after sunset and before sunrise: later in the night it's in Earth's shadow too.
    public class PassFinder
    {
        public const double MinElevation = 10;
        public const double MaxSunAltitude = -6;
        public const int MinVisibleSeconds = 30;

        // Fast enough to catch any pass that spends 30 s above 10°; shorter ones are dropped anyway.
        private static readonly TimeSpan CoarseStep = TimeSpan.FromSeconds(30);
        // Light and darkness change slowly within a pass, so it's sampled every 10 s and the edges refined.
        private static readonly TimeSpan VisibilityStep = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan Precision = TimeSpan.FromSeconds(1);

        private readonly Satellite _satellite;
        private readonly GroundStation _station;
        private readonly CosineKitty.Observer _observer;

        public PassFinder(string tleLine1, string tleLine2, double lat, double lon)
        {
            _satellite = new Satellite("satellite", tleLine1, tleLine2);
            _station = new GroundStation(new GeodeticCoordinate(Angle.FromDegrees(lat), Angle.FromDegrees(lon), 0));
            _observer = new CosineKitty.Observer(lat, lon, 0);
        }

        public PassSearch Find(DateTime startUtc, TimeSpan window)
        {
            DateTime end = startUtc + window;
            var visible = new List<PassWindow>();
            bool anyAbove = false;

            DateTime t = startUtc;
            bool wasAbove = Elevation(t) >= MinElevation;
            DateTime rise = startUtc;   // a pass already under way at the start is clipped to the start

            while (t < end)
            {
                DateTime next = t + CoarseStep < end ? t + CoarseStep : end;
                bool isAbove = Elevation(next) >= MinElevation;

                if (!wasAbove && isAbove)
                    rise = Crossing(t, next);
                else if (wasAbove && !isAbove)
                {
                    anyAbove = true;
                    visible.AddRange(VisibleParts(rise, Crossing(t, next)));
                }

                wasAbove = isAbove;
                t = next;
            }
            if (wasAbove)
            {
                anyAbove = true;
                visible.AddRange(VisibleParts(rise, end));
            }

            return new PassSearch(visible, anyAbove);
        }

        public SkyPoint Look(DateTime time)
        {
            var seen = _station.Observe(_satellite, time);
            double azimuth = (seen.Azimuth.Degrees % 360 + 360) % 360;
            return new SkyPoint(time, azimuth, seen.Elevation.Degrees);
        }

        private double Elevation(DateTime time) => _station.Observe(_satellite, time).Elevation.Degrees;

        // A pass above 10° from rise to set can be partly in daylight or partly in shadow;
        // this returns the stretches of it that can be seen.
        private IEnumerable<PassWindow> VisibleParts(DateTime rise, DateTime set)
        {
            DateTime peak = HighestPoint(rise, set);

            DateTime? partStart = IsVisible(rise) ? rise : null;
            DateTime t = rise;
            while (t < set)
            {
                DateTime next = t + VisibilityStep < set ? t + VisibilityStep : set;
                bool now = IsVisible(next);

                if (partStart == null && now)
                    partStart = Boundary(t, next);
                else if (partStart != null && !now)
                {
                    var part = Window(partStart.Value, Boundary(t, next), peak);
                    if (part != null) yield return part;
                    partStart = null;
                }
                t = next;
            }
            if (partStart != null)
            {
                var part = Window(partStart.Value, set, peak);
                if (part != null) yield return part;
            }
        }

        private PassWindow? Window(DateTime start, DateTime end, DateTime peak)
        {
            // Rounded inward, so the end points stay inside the visible part.
            start = CeilingSecond(start);
            end = FloorSecond(end);
            if ((end - start).TotalSeconds < MinVisibleSeconds)
                return null;

            // Elevation rises to the pass's peak and falls after it, so if the peak isn't inside the
            // visible part, the visible part's highest point is whichever end is nearer to it.
            DateTime highest = peak < start ? start : peak > end ? end : FloorSecond(peak);
            return new PassWindow(Look(start), Look(highest), Look(end));
        }

        public bool IsVisible(DateTime time)
        {
            if (Elevation(time) < MinElevation)
                return false;

            var astroTime = new AstroTime(time);

            var sunSky = Astronomy.Equator(Body.Sun, astroTime, _observer, EquatorEpoch.OfDate, Aberration.Corrected);
            if (Astronomy.Horizon(astroTime, _observer, sunSky.ra, sunSky.dec, Refraction.None).altitude > MaxSunAltitude)
                return false;

            var position = _satellite.Predict(time).Position;
            var sun = Astronomy.GeoVector(Body.Sun, astroTime, Aberration.None);
            return EarthShadow.IsSunlit(position.X, position.Y, position.Z, sun.x, sun.y, sun.z);
        }

        // Bisects a rise or set between a time below 10° and one above (in either order).
        private DateTime Crossing(DateTime a, DateTime b)
        {
            bool aAbove = Elevation(a) >= MinElevation;
            while (b - a > Precision)
            {
                DateTime mid = a + (b - a) / 2;
                if ((Elevation(mid) >= MinElevation) == aAbove) a = mid; else b = mid;
            }
            // Report the first second it's actually above, so the end points always read 10° or more.
            return aAbove ? a : b;
        }

        private DateTime Boundary(DateTime a, DateTime b)
        {
            bool aVisible = IsVisible(a);
            while (b - a > Precision)
            {
                DateTime mid = a + (b - a) / 2;
                if (IsVisible(mid) == aVisible) a = mid; else b = mid;
            }
            return aVisible ? a : b;
        }

        // Ternary search: within one pass, elevation has a single peak.
        private DateTime HighestPoint(DateTime a, DateTime b)
        {
            while (b - a > Precision)
            {
                DateTime m1 = a + (b - a) / 3, m2 = b - (b - a) / 3;
                if (Elevation(m1) < Elevation(m2)) a = m1; else b = m2;
            }
            return a + (b - a) / 2;
        }

        private static DateTime FloorSecond(DateTime t) =>
            new(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

        private static DateTime CeilingSecond(DateTime t) =>
            t.Ticks % TimeSpan.TicksPerSecond == 0 ? t : FloorSecond(t).AddSeconds(1);
    }
}
