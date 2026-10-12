using TodayInSpace.Core.Visibility;

namespace TodayInSpace.Web.Tests
{
    // Aurora odds for a location: geomagnetic latitude, the Kp it takes, and OVATION's chance right now.
    public class AuroraOddsTests
    {
        // Dipole approximation, so only roughly what a full field model gives.
        [Theory]
        [InlineData(47.0, -122.9, 52, 53)]     // Olympia, WA
        [InlineData(64.84, -147.72, 65, 66)]   // Fairbanks, AK
        [InlineData(25.76, -80.19, 34, 36)]    // Miami, FL
        public void GeomagneticLatitude_ForKnownPlaces(double lat, double lon, double min, double max)
        {
            Assert.InRange(AuroraOdds.GeomagneticLatitude(lat, lon), min, max);
        }

        [Fact]
        public void GeomagneticLatitude_IsNegativeInTheSouth()
        {
            // Hobart, Tasmania, a good aurora australis spot. The dipole puts it near -50°.
            Assert.InRange(AuroraOdds.GeomagneticLatitude(-42.9, 147.3), -51, -48);
        }

        [Fact]
        public void KpTable_Is66Point5Minus2Point05PerKp()
        {
            Assert.Equal(10, AuroraOdds.OverheadLatitudeByKp.Count);
            for (int kp = 0; kp <= 9; kp++)
                Assert.Equal(66.5 - 2.05 * kp, AuroraOdds.OverheadLatitudeByKp[kp], 6);
        }

        [Fact]
        public void Olympia_NeedsKp5ForTheHorizon_AndKp7Overhead()
        {
            double gm = AuroraOdds.GeomagneticLatitude(47.0, -122.9);

            Assert.Equal(5, AuroraOdds.KpForHorizon(gm));
            Assert.Equal(7, AuroraOdds.KpForOverhead(gm));
        }

        [Fact]
        public void Fairbanks_SeesItAtLowKp_AndMiamiNever()
        {
            double fairbanks = AuroraOdds.GeomagneticLatitude(64.84, -147.72);
            Assert.Equal(0, AuroraOdds.KpForHorizon(fairbanks));
            Assert.Equal(1, AuroraOdds.KpForOverhead(fairbanks));

            double miami = AuroraOdds.GeomagneticLatitude(25.76, -80.19);
            Assert.Null(AuroraOdds.KpForHorizon(miami));
            Assert.Null(AuroraOdds.KpForOverhead(miami));
        }

        [Theory]
        [InlineData(3, 5, 7, "unlikely")]
        [InlineData(5, 5, 7, "horizon")]
        [InlineData(6, 5, 7, "horizon")]
        [InlineData(7, 5, 7, "overhead")]
        [InlineData(9, 5, 7, "overhead")]
        [InlineData(0, 0, 1, "horizon")]
        [InlineData(9, null, null, "unlikely")]
        public void Chance_ForEachCase(int kp, int? horizon, int? overhead, string expected)
        {
            Assert.Equal(expected, AuroraOdds.Chance(kp, horizon, overhead));
        }

        [Theory]
        [InlineData(-122.9, 237.1)]
        [InlineData(0.0, 0.0)]
        [InlineData(-180.0, 180.0)]
        [InlineData(179.5, 179.5)]
        [InlineData(-0.3, 359.7)]
        public void OvationLongitude_Is0To360East(double lon, double expected)
        {
            Assert.Equal(expected, AuroraOdds.ToOvationLongitude(lon), 6);
        }

        // Points as AuroraSnapshot holds them: [longitude -180..180, latitude, percent], only 5% and up.
        private static readonly int[][] Points =
        {
            new[] { -123, 47, 12 },
            new[] { -122, 47, 3 },     // wouldn't be in a real snapshot, but shows the right cell wins
            new[] { -180, 65, 30 },
            new[] { 0, 60, 8 },
        };

        [Fact]
        public void NowPercent_UsesTheNearestGridPoint()
        {
            // -122.9 is 237.1° east, nearest cell 237° = -123.
            Assert.Equal(12, AuroraOdds.NowPercent(Points, 47.2, -122.9));
        }

        [Theory]
        [InlineData(65.0, 179.7, 30)]     // rounds to 180° east, which the snapshot stores as -180
        [InlineData(65.0, -179.8, 30)]
        [InlineData(60.0, -0.3, 8)]       // 359.7° rounds up to 360, which is 0
        public void NowPercent_WrapsAroundTheGrid(double lat, double lon, int expected)
        {
            Assert.Equal(expected, AuroraOdds.NowPercent(Points, lat, lon));
        }

        [Fact]
        public void NowPercent_IsZero_WhenThePointIsMissing()
        {
            // Under 5% isn't in the snapshot at all.
            Assert.Equal(0, AuroraOdds.NowPercent(Points, 40.0, -100.0));
        }

        [Fact]
        public void ForecastDays_PlacesShortDayNamesOnDates()
        {
            var days = AuroraOdds.ForecastDays("2026-10-11",
                new (string?, int?)[] { ("Sun", 2), ("Mon", 5), ("Tue", 3) },
                new DateTime(2026, 10, 11, 18, 0, 0, DateTimeKind.Utc));

            Assert.Equal(new[]
            {
                new KpDay(new DateOnly(2026, 10, 11), 2),
                new KpDay(new DateOnly(2026, 10, 12), 5),
                new KpDay(new DateOnly(2026, 10, 13), 3),
            }, days);
        }

        [Fact]
        public void ForecastDays_SkipsOldDays_AndOnesItCantPlace()
        {
            // A digest from the 11th read on the 13th: the 11th is gone, the 12th is kept (an American
            // evening on the 12th is already the 13th in UTC). "Xyz" and a missing Kp are skipped.
            var days = AuroraOdds.ForecastDays("2026-10-11",
                new (string?, int?)[] { ("Sun", 2), ("Mon", 5), ("Xyz", 4), ("Tue", null) },
                new DateTime(2026, 10, 13, 9, 0, 0, DateTimeKind.Utc));

            Assert.Equal(new[] { new KpDay(new DateOnly(2026, 10, 12), 5) }, days);
        }

        [Fact]
        public void ForecastDays_IsEmpty_WithoutADigestDate()
        {
            Assert.Empty(AuroraOdds.ForecastDays(null, new (string?, int?)[] { ("Sun", 2) }, DateTime.UtcNow));
        }

        [Fact]
        public void Build_ForOlympia()
        {
            var forecast = new[]
            {
                new KpDay(new DateOnly(2026, 10, 13), 7),
                new KpDay(new DateOnly(2026, 10, 11), 3),
                new KpDay(new DateOnly(2026, 10, 12), 5),
            };

            var aurora = AuroraOdds.Build(47.0, -122.9, Points, forecast);

            Assert.InRange(aurora.GeomagneticLatitude, 52, 53);
            Assert.Equal((5, 7), (aurora.KpForHorizon, aurora.KpForOverhead));
            Assert.Equal(12, aurora.NowPercent);
            Assert.Equal(new[]
            {
                new AuroraNight("2026-10-11", 3, "unlikely"),
                new AuroraNight("2026-10-12", 5, "horizon"),
                new AuroraNight("2026-10-13", 7, "overhead"),
            }, aurora.Nights);
        }

        [Fact]
        public void Build_WithoutOvation_HasNoNowPercent()
        {
            var aurora = AuroraOdds.Build(47.0, -122.9, null, Array.Empty<KpDay>());

            Assert.Null(aurora.NowPercent);
            Assert.Empty(aurora.Nights);
        }
    }
}
