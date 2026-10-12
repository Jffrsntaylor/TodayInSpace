using CosineKitty;

namespace TodayInSpace.Core.Snapshot
{
    // A place on the ground, in degrees. Longitude is -180..180.
    public record GroundPoint(double Lat, double Lon);

    // A direction from Earth's centre. X points at 0°N 0°E, Y at 0°N 90°E, Z at the North Pole.
    public readonly record struct Vec3(double X, double Y, double Z)
    {
        public double Dot(Vec3 o) => X * o.X + Y * o.Y + Z * o.Z;

        public static Vec3 FromLatLon(double lat, double lon)
        {
            double phi = lat * Math.PI / 180, lambda = lon * Math.PI / 180;
            return new Vec3(Math.Cos(phi) * Math.Cos(lambda), Math.Cos(phi) * Math.Sin(lambda), Math.Sin(phi));
        }

        public GroundPoint ToLatLon() =>
            new(Math.Asin(Math.Clamp(Z, -1, 1)) * 180 / Math.PI, Math.Atan2(Y, X) * 180 / Math.PI);
    }

    // The globe as seen from far away, looking straight down at one place (an orthographic projection).
    // Disc coordinates run -1..1: u is left to right (east), v is bottom to top (north), and the disc's edge
    // is u² + v² = 1. Working with unit vectors rather than the textbook trig formulas keeps the per-pixel
    // cost to one square root plus the lat/lon conversion, which matters at 640k pixels a render.
    public sealed class OrthographicView
    {
        public double CenterLat { get; }
        public double CenterLon { get; }

        // The view's axes: toward the viewer, and east and north at the centre of the disc.
        public Vec3 Center { get; }
        public Vec3 East { get; }
        public Vec3 North { get; }

        public OrthographicView(double centerLat, double centerLon)
        {
            CenterLat = centerLat;
            CenterLon = centerLon;
            double phi = centerLat * Math.PI / 180, lambda = centerLon * Math.PI / 180;
            Center = Vec3.FromLatLon(centerLat, centerLon);
            East = new Vec3(-Math.Sin(lambda), Math.Cos(lambda), 0);
            North = new Vec3(-Math.Sin(phi) * Math.Cos(lambda), -Math.Sin(phi) * Math.Sin(lambda), Math.Cos(phi));
        }

        // The point on the globe under disc position (u, v). False outside the disc.
        public bool TryUnproject(double u, double v, out Vec3 point)
        {
            double r2 = u * u + v * v;
            if (r2 > 1)
            {
                point = default;
                return false;
            }
            double w = Math.Sqrt(1 - r2);
            point = new Vec3(
                u * East.X + v * North.X + w * Center.X,
                u * East.Y + v * North.Y + w * Center.Y,
                u * East.Z + v * North.Z + w * Center.Z);
            return true;
        }

        public bool TryUnprojectLatLon(double u, double v, out GroundPoint ground)
        {
            bool inside = TryUnproject(u, v, out Vec3 point);
            ground = inside ? point.ToLatLon() : new GroundPoint(0, 0);
            return inside;
        }

        // Where a place lands on the disc. Visible is false on the far side of the Earth.
        public (double U, double V, bool Visible) Project(double lat, double lon)
        {
            var p = Vec3.FromLatLon(lat, lon);
            return (p.Dot(East), p.Dot(North), p.Dot(Center) >= 0);
        }
    }

    // Where the Sun is overhead, and how dark it is everywhere else.
    public static class DayNight
    {
        // The point where the Sun is straight up at this moment.
        public static GroundPoint SubsolarPoint(DateTime utc)
        {
            var time = new AstroTime(utc);
            // Seen from a point on the equator rather than Earth's centre, which shifts the Sun by under 9
            // arcseconds. Far too small to see, and it saves converting frames by hand.
            var sun = Astronomy.Equator(Body.Sun, time, new Observer(0, 0, 0), EquatorEpoch.OfDate, Aberration.Corrected);
            double lon = (sun.ra - Astronomy.SiderealTime(time)) * 15;
            return new GroundPoint(sun.dec, NormalizeLongitude(lon));
        }

        // 0 in daylight, 1 at night, easing across the twilight band. cosSun is the dot product of the
        // ground point and the subsolar point as unit vectors (the cosine of the Sun's angle from overhead).
        // Same band as the 3D globe's shader (sky-globe.js), so the picture and the globe agree: full
        // night from about 10° below the horizon, full day from about 3° above it.
        public static double NightFactor(double cosSun) => 1 - SmoothStep(-0.17, 0.05, cosSun);

        public static double SmoothStep(double edge0, double edge1, double x)
        {
            double t = Math.Clamp((x - edge0) / (edge1 - edge0), 0, 1);
            return t * t * (3 - 2 * t);
        }

        public static double NormalizeLongitude(double lon)
        {
            double l = ((lon + 180) % 360 + 360) % 360 - 180;
            return l == -180 ? 180 : l;
        }
    }
}
