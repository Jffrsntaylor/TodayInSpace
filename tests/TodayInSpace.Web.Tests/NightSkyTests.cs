using System.Text.Json;
using TodayInSpace.Core.Visibility;

namespace TodayInSpace.Web.Tests
{
    // Planets and the Moon tonight. Olympia, WA (98501, rounded the way the API rounds it) on fixed dates,
    // so the answers never change.
    public class NightSkyTests
    {
        private const double OlympiaLat = 47.0, OlympiaLon = -122.9;

        private static DateTime Utc(int month, int day, int h, int m, int s = 0) => new(2026, month, day, h, m, s, DateTimeKind.Utc);

        [Fact]
        public void Darkness_MatchesCivilTwilight_ForOlympia()
        {
            // Source: US Naval Observatory, aa.usno.navy.mil/api/rstt/oneday?date=2026-10-12&coords=47.0,-122.9&tz=0
            // End of civil twilight Oct 12 02:02 UTC (7:02 pm PDT on the 11th), begin civil twilight 13:55 UTC.
            var dark = NightSky.FindDarkness(OlympiaLat, OlympiaLon, Utc(10, 11, 19, 0));

            Assert.NotNull(dark);
            Assert.Equal(Utc(10, 12, 2, 2), dark.Start, TimeSpan.FromMinutes(2));
            Assert.Equal(Utc(10, 12, 13, 55), dark.End, TimeSpan.FromMinutes(2));
        }

        [Fact]
        public void Darkness_StartsNow_WhenItsAlreadyDark()
        {
            // 2 am PDT.
            var now = Utc(10, 12, 9, 0);

            var dark = NightSky.FindDarkness(OlympiaLat, OlympiaLon, now);

            Assert.NotNull(dark);
            Assert.Equal(now, dark.Start);
            Assert.Equal(Utc(10, 12, 13, 55), dark.End, TimeSpan.FromMinutes(2));
        }

        [Fact]
        public void PolarSummer_HasNoDarkness_AndNoPlanets()
        {
            // Svalbard at midsummer: the Sun never sets.
            var tonight = NightSky.Build(78, 15, Utc(6, 21, 12, 0));

            Assert.Null(tonight.Darkness);
            Assert.Empty(tonight.Planets);
            Assert.False(tonight.Moon.UpDuringDarkness);
        }

        [Fact]
        public void Venus_AtConjunction_IsNearSun()
        {
            // Venus passes between Earth and the Sun on Oct 24, 2026 (inferior conjunction).
            var tonight = NightSky.Build(OlympiaLat, OlympiaLon, Utc(10, 24, 19, 0));

            var venus = tonight.Planets.Single(p => p.Id == "venus");
            Assert.False(venus.Visible);
            Assert.Equal("nearSun", venus.Reason);
            Assert.True(venus.Elongation < NightSky.NearSunDegrees);
        }

        [Fact]
        public void SaturnAndJupiter_WellPlaced_AreVisible_WithBestTimeInTheDark()
        {
            // Mid-October 2026: Saturn is a week past opposition and up all evening; Jupiter rises after
            // midnight and is high before dawn.
            var tonight = NightSky.Build(OlympiaLat, OlympiaLon, Utc(10, 11, 19, 0));
            var dark = tonight.Darkness!;

            foreach (var id in new[] { "saturn", "jupiter" })
            {
                var planet = tonight.Planets.Single(p => p.Id == id);
                Assert.True(planet.Visible, id);
                Assert.Null(planet.Reason);
                Assert.InRange(planet.Best.Time, dark.Start, dark.End);
                Assert.True(planet.Best.Elevation >= NightSky.MinPlanetAltitude, id);
            }

            // Saturn is highest when it crosses due south, and sets before dawn.
            var saturn = tonight.Planets.Single(p => p.Id == "saturn");
            Assert.InRange(saturn.Best.Azimuth, 175, 185);
            Assert.NotNull(saturn.Set);
            Assert.InRange(saturn.Set.Value, dark.Start, dark.End);

            // Jupiter rises during the night: very bright, highest at dawn in the southeast.
            var jupiter = tonight.Planets.Single(p => p.Id == "jupiter");
            Assert.NotNull(jupiter.Rise);
            Assert.InRange(jupiter.Rise.Value, dark.Start, dark.End);
            Assert.InRange(jupiter.Magnitude, -2.5, -1.5);
        }

        [Fact]
        public void Planets_AreTheFiveNakedEyeOnes_InOrder()
        {
            var tonight = NightSky.Build(OlympiaLat, OlympiaLon, Utc(10, 11, 19, 0));

            Assert.Equal(new[] { "mercury", "venus", "mars", "jupiter", "saturn" }, tonight.Planets.Select(p => p.Id));
            Assert.All(tonight.Planets, p => Assert.Equal(p.Visible, p.Reason == null));
        }

        [Fact]
        public void FullMoon_Oct26()
        {
            // Full moon 2026-10-26 04:12 UTC (9:12 pm PDT on the 25th, already dark in Olympia).
            var tonight = NightSky.Build(OlympiaLat, OlympiaLon, Utc(10, 26, 4, 12));

            Assert.Equal("fullMoon", tonight.Moon.Phase);
            Assert.True(tonight.Moon.Illumination >= 0.99);
            Assert.True(tonight.Moon.UpDuringDarkness);
        }

        [Fact]
        public void NewMoon_Oct10()
        {
            // New moon 2026-10-10 15:50 UTC. Tonight's darkness starts about ten hours later.
            var tonight = NightSky.Build(OlympiaLat, OlympiaLon, Utc(10, 10, 15, 50));

            Assert.Equal("newMoon", tonight.Moon.Phase);
            Assert.True(tonight.Moon.Illumination <= 0.01);
        }

        [Theory]
        [InlineData(0, "newMoon")]
        [InlineData(355, "newMoon")]
        [InlineData(45, "waxingCrescent")]
        [InlineData(90, "firstQuarter")]
        [InlineData(135, "waxingGibbous")]
        [InlineData(180, "fullMoon")]
        [InlineData(225, "waningGibbous")]
        [InlineData(270, "lastQuarter")]
        [InlineData(315, "waningCrescent")]
        public void PhaseId_FromAngle(double angle, string expected)
        {
            Assert.Equal(expected, NightSky.PhaseId(angle));
        }

        // Brief 1's shape test (VisibleSkyApiTests.ResponseShape_IsLocked) still runs unchanged; this checks the
        // fields added for the planets and Moon, including "darkness": null rather than a missing field.
        [Fact]
        public void ResponseShape_AddsPlanetsAndMoon()
        {
            var sky = VisibleSky.Build(OlympiaLat, OlympiaLon, Utc(10, 11, 19, 0), Array.Empty<SatelliteOrbit>());
            var json = Json(sky);

            Assert.True(json.TryGetProperty("darkness", out var darkness));
            AssertHas(darkness, "start", "end");
            Assert.Equal("2026-10-12T02:01:52Z", darkness.GetProperty("start").GetString());

            var planet = json.GetProperty("planets")[0];
            AssertHas(planet, "id", "name", "visible", "magnitude", "best", "rise", "set", "elongation", "reason");
            AssertHas(planet.GetProperty("best"), "time", "azimuth", "elevation");
            AssertHas(json.GetProperty("moon"), "phase", "illumination", "rise", "set", "upDuringDarkness");

            var polar = Json(VisibleSky.Build(78, 15, Utc(6, 21, 12, 0), Array.Empty<SatelliteOrbit>()));
            Assert.Equal(JsonValueKind.Null, polar.GetProperty("darkness").ValueKind);
            Assert.Equal(0, polar.GetProperty("planets").GetArrayLength());
        }

        // The same settings ASP.NET Core uses for API responses.
        private static JsonElement Json(VisibleSkyResponse sky) =>
            JsonDocument.Parse(JsonSerializer.Serialize(sky, new JsonSerializerOptions(JsonSerializerDefaults.Web))).RootElement;

        private static void AssertHas(JsonElement element, params string[] names)
        {
            foreach (var name in names)
                Assert.True(element.TryGetProperty(name, out _), $"Missing field \"{name}\"");
        }
    }
}
