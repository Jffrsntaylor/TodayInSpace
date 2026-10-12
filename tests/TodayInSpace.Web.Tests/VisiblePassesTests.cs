using TodayInSpace.Core.Visibility;

namespace TodayInSpace.Web.Tests
{
    // Visible-pass rules, run against real CelesTrak elements from 2026-10-11 so the answers never change.
    // Olympia, WA is the reference location (98501, rounded the way the API rounds it).
    public class VisiblePassesTests
    {
        private const string IssLine1 = "1 25544U 98067A   26284.17638745  .00009430  00000+0  18014-3 0  9995";
        private const string IssLine2 = "2 25544  51.6312  86.0933 0006713 248.1316 111.8959 15.48821882589734";
        private const string HubbleLine1 = "1 20580U 90037B   26284.53776240  .00008960  00000+0  27488-3 0  9994";
        private const string HubbleLine2 = "2 20580  28.4739   4.6482 0001787  20.7144 339.3525 15.31904935806425";

        private const double OlympiaLat = 47.0, OlympiaLon = -122.9;
        private static readonly DateTime Start = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

        private static PassFinder Iss(double lat = OlympiaLat, double lon = OlympiaLon) => new(IssLine1, IssLine2, lat, lon);
        private static DateTime Utc(int day, int h, int m, int s = 0) => new(2026, 10, day, h, m, s, DateTimeKind.Utc);

        [Fact]
        public void DaytimePass_IsNotVisible()
        {
            // 20:08-20:14 UTC is 1 pm in Olympia: the ISS climbs to 69°, but the sky is bright.
            var found = Iss().Find(Utc(11, 20, 0), TimeSpan.FromMinutes(20));

            Assert.True(found.AnyPassAbove10);
            Assert.Empty(found.Visible);
        }

        [Fact]
        public void PreDawnPass_IsVisible_WithRiseMaxAndSet()
        {
            // Oct 17, 6:46 am PDT: dark sky, and the ISS is already in sunlight. Rise, peak and set agree with
            // the vendored satellite.js to within a second (13:46:00, 13:49:23 at 83°, 13:52:48).
            var found = Iss().Find(Utc(17, 13, 40), TimeSpan.FromMinutes(20));

            var pass = Assert.Single(found.Visible);
            Assert.Equal(Utc(17, 13, 46, 0), pass.Start.Time, TimeSpan.FromSeconds(2));
            Assert.Equal(Utc(17, 13, 49, 23), pass.Max.Time, TimeSpan.FromSeconds(5));
            Assert.Equal(Utc(17, 13, 52, 48), pass.End.Time, TimeSpan.FromSeconds(2));
            Assert.InRange(pass.Max.Elevation, 82.5, 83.5);
            // Rises in the WSW and sets in the ENE.
            Assert.InRange(pass.Start.Azimuth, 235, 245);
            Assert.InRange(pass.End.Azimuth, 60, 70);
            Assert.InRange(pass.DurationSeconds, 400, 410);
        }

        [Fact]
        public void VisiblePart_StaysAtOrAbove10Degrees()
        {
            var found = Iss().Find(Start, TimeSpan.FromDays(7));

            Assert.NotEmpty(found.Visible);
            Assert.All(found.Visible, p =>
            {
                Assert.True(p.Start.Elevation >= 10 && p.End.Elevation >= 10);
                Assert.True(p.Max.Elevation >= p.Start.Elevation && p.Max.Elevation >= p.End.Elevation);
                Assert.True(p.DurationSeconds >= PassFinder.MinVisibleSeconds);
            });
        }

        [Fact]
        public void Hubble_NeverClimbsAbove10_At47North()
        {
            // A 28.5° orbit stays too far south to clear 10° from Olympia.
            var found = new PassFinder(HubbleLine1, HubbleLine2, OlympiaLat, OlympiaLon).Find(Start, TimeSpan.FromDays(7));

            Assert.False(found.AnyPassAbove10);
            Assert.Empty(found.Visible);
        }

        [Fact]
        public void Hubble_ClimbsAbove10_At20North()
        {
            var found = new PassFinder(HubbleLine1, HubbleLine2, 20, OlympiaLon).Find(Start, TimeSpan.FromDays(7));

            Assert.True(found.AnyPassAbove10);
        }

        [Fact]
        public void Build_SortsPasses_AndKeepsGoingWithoutATle()
        {
            var sky = VisibleSky.Build(OlympiaLat, OlympiaLon, Start, new[]
            {
                new SatelliteOrbit("hubble", "Hubble", HubbleLine1, HubbleLine2),
                new SatelliteOrbit("tiangong", "Tiangong", null, null),
                new SatelliteOrbit("iss", "ISS", IssLine1, IssLine2),
            });

            Assert.NotEmpty(sky.Passes);
            Assert.All(sky.Passes, p => Assert.Equal("iss", p.Satellite));
            Assert.Equal(sky.Passes.OrderBy(p => p.Start.Time).Select(p => p.Start.Time), sky.Passes.Select(p => p.Start.Time));

            var tiangong = sky.Satellites.Single(s => s.Id == "tiangong");
            Assert.False(tiangong.Available);
            var iss = sky.Satellites.Single(s => s.Id == "iss");
            Assert.Equal(sky.Passes.Count, iss.VisiblePasses);
            Assert.True(iss.Available && iss.AnyPassAbove10);
            Assert.Equal(7, sky.Days);
        }

        // Shadow model: the Sun is along +x, so the night side is x < 0.
        [Theory]
        [InlineData(7000, 0, 0, true)]      // day side
        [InlineData(-7000, 0, 0, false)]    // straight behind Earth
        [InlineData(-7000, 6000, 0, false)] // night side, 6000 km off the Earth-Sun line: still inside the shadow
        [InlineData(-7000, 6500, 0, true)]  // night side but beyond Earth's edge, so still lit
        [InlineData(0, 0, 7000, true)]      // over the terminator
        public void EarthShadow_CylinderModel(double x, double y, double z, bool sunlit)
        {
            Assert.Equal(sunlit, EarthShadow.IsSunlit(x, y, z, 1.0, 0, 0));
        }
    }
}
