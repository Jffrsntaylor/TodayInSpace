using TodayInSpace.Core.Snapshot;

namespace TodayInSpace.Web.Tests
{
    // The Earth snapshot's maths: the projection, day and night, the ISS's ground track, and the request rules.
    public class EarthSnapshotTests
    {
        private const string IssLine1 = "1 25544U 98067A   26284.17638745  .00009430  00000+0  18014-3 0  9995";
        private const string IssLine2 = "2 25544  51.6312  86.0933 0006713 248.1316 111.8959 15.48821882589734";
        private static readonly DateTime Now = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

        // ---- Projection ----

        [Theory]
        [InlineData(47.05, -122.9)]
        [InlineData(0, 0)]
        [InlineData(-33.9, 151.2)]
        [InlineData(89.9, 10)]
        public void CentreOfDisc_IsTheCentrePoint(double lat, double lon)
        {
            var view = new OrthographicView(lat, lon);

            Assert.True(view.TryUnprojectLatLon(0, 0, out var ground));

            Assert.Equal(lat, ground.Lat, 6);
            Assert.Equal(lon, ground.Lon, 6);
        }

        [Fact]
        public void UpIsNorth_RightIsEast()
        {
            var view = new OrthographicView(0, 0);

            view.TryUnprojectLatLon(0, 0.5, out var up);
            view.TryUnprojectLatLon(0.5, 0, out var right);

            Assert.Equal(30, up.Lat, 6);     // asin(0.5)
            Assert.Equal(0, up.Lon, 6);
            Assert.Equal(0, right.Lat, 6);
            Assert.Equal(30, right.Lon, 6);
        }

        [Theory]
        [InlineData(0.3, -0.4)]
        [InlineData(-0.7, 0.6)]
        [InlineData(0.0, 0.99)]
        public void ProjectAndUnproject_RoundTrip(double u, double v)
        {
            var view = new OrthographicView(47.05, -122.9);

            Assert.True(view.TryUnprojectLatLon(u, v, out var ground));
            var back = view.Project(ground.Lat, ground.Lon);

            Assert.True(back.Visible);
            Assert.Equal(u, back.U, 6);
            Assert.Equal(v, back.V, 6);
        }

        [Fact]
        public void FarSide_IsNotVisible()
        {
            var view = new OrthographicView(47.05, -122.9);

            Assert.False(view.Project(-47.05, 57.1).Visible);    // the antipode
            Assert.True(view.Project(47.05, -122.9 + 80).Visible);
            Assert.False(view.TryUnprojectLatLon(0.8, 0.8, out _));  // outside the disc
        }

        // ---- Day and night ----

        [Fact]
        public void NightFactor_DayAtSubsolar_NightAtAntisolar_BetweenAtTerminator()
        {
            Assert.Equal(0, DayNight.NightFactor(1));
            Assert.Equal(1, DayNight.NightFactor(-1));

            // On the terminator (Sun on the horizon) it's mostly dark, still easing in.
            double terminator = DayNight.NightFactor(0);
            Assert.InRange(terminator, 0.1, 0.9);
            // 10° below the horizon: night. 3° above: day.
            Assert.Equal(1, DayNight.NightFactor(Math.Sin(-10.0 * Math.PI / 180)), 2);
            Assert.Equal(0, DayNight.NightFactor(Math.Sin(3.0 * Math.PI / 180)), 2);
        }

        [Fact]
        public void SubsolarPoint_AtJuneSolstice_IsOnTheTropicOfCancer_NearGreenwichAtNoon()
        {
            var sun = DayNight.SubsolarPoint(new DateTime(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc));

            Assert.InRange(sun.Lat, 23.3, 23.5);
            // Noon UTC puts the Sun over Greenwich, give or take the equation of time (a degree or two).
            Assert.InRange(sun.Lon, -2.0, 2.0);
        }

        [Fact]
        public void SubsolarPoint_MovesWestFifteenDegreesAnHour()
        {
            var noon = DayNight.SubsolarPoint(Now);
            var later = DayNight.SubsolarPoint(Now.AddHours(1));

            Assert.Equal(-15, DayNight.NormalizeLongitude(later.Lon - noon.Lon), 0);
        }

        [Fact]
        public void PointUnderTheSun_IsDay_AndOppositeIsNight()
        {
            var sun = DayNight.SubsolarPoint(Now);
            var sunVec = Vec3.FromLatLon(sun.Lat, sun.Lon);

            Assert.Equal(0, DayNight.NightFactor(sunVec.Dot(Vec3.FromLatLon(sun.Lat, sun.Lon))));
            Assert.Equal(1, DayNight.NightFactor(sunVec.Dot(Vec3.FromLatLon(-sun.Lat, sun.Lon + 180))));
        }

        // ---- ISS ----

        [Fact]
        public void IssSubPoint_HasTheIssStraightOverhead()
        {
            var iss = new OrbitTrack(IssLine1, IssLine2);

            var below = iss.SubPoint(Now);

            // An independent check through SGP.NET's own observer maths: from the point directly underneath,
            // the ISS is at the zenith. 89.9° is about 0.7 km of ground at ISS altitude.
            Assert.InRange(below.Lat, -51.7, 51.7);
            Assert.True(iss.ElevationFrom(below.Lat, below.Lon, Now) > 89.9);
        }

        [Fact]
        public void IssPath_MovesAboutFourHundredKmAMinute()
        {
            var iss = new OrbitTrack(IssLine1, IssLine2);

            var path = iss.Path(Now, Now.AddMinutes(1), TimeSpan.FromMinutes(1));

            Assert.Equal(2, path.Count);
            double km = 6371 * Math.Acos(Vec3.FromLatLon(path[0].Lat, path[0].Lon).Dot(Vec3.FromLatLon(path[1].Lat, path[1].Lon)));
            Assert.InRange(km, 400, 480);  // ~7.4 km/s over the ground
        }

        [Fact]
        public void Scene_CentredOnIss_HasTrackAndNoPin()
        {
            var request = EarthSnapshotRequest.Parse(null, null, null, null, null)!;

            var scene = EarthScene.Build(request, Now, new OrbitTrack(IssLine1, IssLine2), null)!;

            Assert.Equal(scene.Iss, scene.Center);
            Assert.Null(scene.Pin);
            Assert.Equal(61, scene.Track.Count);  // -20..+10 minutes every 30 s
            Assert.Equal(-20, scene.Track[0].Minutes);
            Assert.Equal(10, scene.Track[^1].Minutes);
            Assert.Empty(scene.Aurora);
        }

        [Fact]
        public void Scene_WithoutOrbitData_NullWhenCentredOnIss_OtherwiseNoIss()
        {
            Assert.Null(EarthScene.Build(EarthSnapshotRequest.Parse(null, null, null, null, null)!, Now, null, null));

            var scene = EarthScene.Build(EarthSnapshotRequest.Parse(47.04, -122.9, null, null, null)!, Now, null, null)!;
            Assert.Null(scene.Iss);
            Assert.Empty(scene.Track);
            Assert.Equal(new GroundPoint(47.05, -122.9), scene.Center);
            Assert.False(scene.IssAbovePin);
        }

        [Fact]
        public void Scene_RingsThePin_WhenTheIssIsOverhead()
        {
            var iss = new OrbitTrack(IssLine1, IssLine2);
            var below = iss.SubPoint(Now);

            var under = EarthScene.Build(EarthSnapshotRequest.Parse(below.Lat, below.Lon, null, null, null)!, Now, iss, null)!;
            var far = EarthScene.Build(EarthSnapshotRequest.Parse(-below.Lat, DayNight.NormalizeLongitude(below.Lon + 180), null, null, null)!, Now, iss, null)!;

            Assert.True(under.IssAbovePin);
            Assert.False(far.IssAbovePin);
        }

        [Fact]
        public void Scene_CenterIss_StillPinsThePlace()
        {
            var scene = EarthScene.Build(EarthSnapshotRequest.Parse(47.04, -122.9, "iss", null, null)!, Now,
                new OrbitTrack(IssLine1, IssLine2), null)!;

            Assert.Equal(scene.Iss, scene.Center);
            Assert.Equal(new GroundPoint(47.05, -122.9), scene.Pin);
        }

        // ---- Request rules ----

        [Theory]
        [InlineData(47.0, null, null, null)]          // lat without lon
        [InlineData(null, -122.9, null, null)]        // lon without lat
        [InlineData(90.5, 0.0, null, null)]
        [InlineData(0.0, -180.5, null, null)]
        [InlineData(double.NaN, 0.0, null, null)]
        [InlineData(null, null, "moon", null)]
        [InlineData(null, null, null, "poster")]
        public void BadInput_IsRefused(double? lat, double? lon, string? center, string? format)
        {
            Assert.Null(EarthSnapshotRequest.Parse(lat, lon, center, null, format));
        }

        [Fact]
        public void Defaults_CentreOnIss_At400()
        {
            var r = EarthSnapshotRequest.Parse(null, null, null, null, null)!;

            Assert.True(r.CenterOnIss);
            Assert.False(r.HasPin);
            Assert.Equal((400, 400), (r.Width, r.Height));
            Assert.Equal(SnapshotFormat.Square, r.Format);
        }

        [Fact]
        public void Location_IsRoundedToTheGrid_AndCentred()
        {
            var r = EarthSnapshotRequest.Parse(47.04, -122.876, null, 300, null)!;

            Assert.Equal((47.05, -122.9), (r.Lat, r.Lon));
            Assert.False(r.CenterOnIss);
            Assert.True(r.HasPin);
            Assert.Equal(300, r.Size);
        }

        [Theory]
        [InlineData(10, 200)]
        [InlineData(199, 200)]
        [InlineData(200, 200)]
        [InlineData(555, 555)]
        [InlineData(800, 800)]
        [InlineData(5000, 800)]
        [InlineData(-1, 200)]
        public void Size_IsClamped(int asked, int expected)
        {
            Assert.Equal(expected, EarthSnapshotRequest.Parse(null, null, null, asked, null)!.Size);
        }

        [Fact]
        public void Card_Is1200x630_WhateverTheSize()
        {
            var r = EarthSnapshotRequest.Parse(null, null, "ISS", 300, "Card")!;

            Assert.Equal(SnapshotFormat.Card, r.Format);
            Assert.Equal((1200, 630), (r.Width, r.Height));
            Assert.Equal(EarthSnapshotRequest.Parse(null, null, null, 800, "card")!.CacheKey(Now), r.CacheKey(Now));
        }

        [Fact]
        public void CacheKey_SharedByNeighboursAndWithinTenMinutes()
        {
            var a = EarthSnapshotRequest.Parse(47.04, -122.90, null, 400, null)!;
            var b = EarthSnapshotRequest.Parse(47.06, -122.89, null, 400, null)!;

            Assert.Equal(a.CacheKey(Now), b.CacheKey(Now.AddMinutes(9.9)));
            Assert.NotEqual(a.CacheKey(Now), a.CacheKey(Now.AddMinutes(10)));
            Assert.NotEqual(a.CacheKey(Now), EarthSnapshotRequest.Parse(47.04, -122.90, null, 200, null)!.CacheKey(Now));
            Assert.NotEqual(a.CacheKey(Now), EarthSnapshotRequest.Parse(47.04, -122.90, "iss", 400, null)!.CacheKey(Now));
        }

        [Fact]
        public void BucketStart_FloorsToTenMinutes()
        {
            Assert.Equal(new DateTime(2026, 10, 11, 12, 30, 0, DateTimeKind.Utc),
                EarthSnapshotRequest.BucketStart(new DateTime(2026, 10, 11, 12, 39, 59, DateTimeKind.Utc)));
        }
    }
}
