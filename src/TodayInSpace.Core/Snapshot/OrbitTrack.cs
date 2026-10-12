using SGPdotNET.CoordinateSystem;
using SGPdotNET.Observation;
using SGPdotNET.Util;

namespace TodayInSpace.Core.Snapshot
{
    // Where a satellite is over the ground, from its TLE.
    public sealed class OrbitTrack
    {
        private readonly Satellite _satellite;

        public OrbitTrack(string tleLine1, string tleLine2)
        {
            _satellite = new Satellite("satellite", tleLine1, tleLine2);
        }

        // The point on the ground directly below the satellite (geodetic, like a map's latitude).
        public GroundPoint SubPoint(DateTime utc)
        {
            var geo = _satellite.Predict(utc).ToGeodetic();
            return new GroundPoint(geo.Latitude.Degrees, DayNight.NormalizeLongitude(geo.Longitude.Degrees));
        }

        // Sub-points from..to (inclusive), one per step.
        public IReadOnlyList<GroundPoint> Path(DateTime fromUtc, DateTime toUtc, TimeSpan step)
        {
            var points = new List<GroundPoint>();
            for (var t = fromUtc; t <= toUtc; t += step)
                points.Add(SubPoint(t));
            return points;
        }

        // Degrees above the horizon, seen from a place on the ground.
        public double ElevationFrom(double lat, double lon, DateTime utc)
        {
            var station = new GroundStation(new GeodeticCoordinate(Angle.FromDegrees(lat), Angle.FromDegrees(lon), 0));
            return station.Observe(_satellite, utc).Elevation.Degrees;
        }
    }
}
