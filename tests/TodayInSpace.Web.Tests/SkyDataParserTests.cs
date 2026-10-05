using TodayInSpace.Web.Sky;

namespace TodayInSpace.Web.Tests
{
    public class SkyDataParserTests
    {
        private const string Line1 = "1 25544U 98067A   08264.51782528 -.00002182  00000-0 -11606-4 0  2927";
        private const string Line2 = "2 25544  51.6416 247.4627 0006703 130.5360 325.0288 15.72125391563537";

        // ---------- TLE ----------

        [Fact]
        public void ParseTle_ReadsCelestrakThreeLineFormat()
        {
            var tle = SkyDataParser.ParseTle($"ISS (ZARYA)             \r\n{Line1}\r\n{Line2}\r\n");

            Assert.NotNull(tle);
            Assert.Equal("ISS (ZARYA)", tle!.Name);
            Assert.Equal(Line1, tle.Line1);
            Assert.Equal(Line2, tle.Line2);
        }

        [Fact]
        public void ParseTle_WorksWithoutNameLine()
        {
            var tle = SkyDataParser.ParseTle($"{Line1}\n{Line2}");
            Assert.NotNull(tle);
            Assert.Equal("", tle!.Name);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("No GP data found")]                 // CelesTrak's reply for an unknown object
        [InlineData("ISS\n1 25544U short line\n2 25544 short line")]
        public void ParseTle_ReturnsNull_ForInvalidInput(string? text)
        {
            Assert.Null(SkyDataParser.ParseTle(text));
        }

        [Fact]
        public void ParseTle_ReturnsNull_WhenLinesAreOutOfOrder()
        {
            Assert.Null(SkyDataParser.ParseTle($"{Line2}\n{Line1}"));
        }

        [Fact]
        public void TrackedSatellites_AreIssHubbleAndTiangong()
        {
            Assert.Equal(25544, SkyDataService.Satellites["iss"]);
            Assert.Equal(20580, SkyDataService.Satellites["hubble"]);
            Assert.Equal(48274, SkyDataService.Satellites["tiangong"]);
            Assert.Equal(3, SkyDataService.Satellites.Count);
            Assert.True(SkyDataService.Satellites.ContainsKey("ISS"));       // case-insensitive ids
            Assert.False(SkyDataService.Satellites.ContainsKey("25544"));    // raw catalog numbers aren't accepted
            Assert.False(SkyDataService.Satellites.ContainsKey("48274"));
        }

        // ---------- OVATION aurora ----------

        private const string OvationSample = @"{
            ""Observation Time"": ""2026-09-29T00:08:00Z"",
            ""Forecast Time"": ""2026-09-29T01:32:00Z"",
            ""Data Format"": ""[Longitude, Latitude, Aurora]"",
            ""coordinates"": [
                [0, -90, 3],
                [10, 65, 12],
                [200, 67, 40],
                [359, 70, 5],
                [180, 60, 4.6]
            ]
        }";

        [Fact]
        public void ParseOvation_FiltersByMinimumProbability()
        {
            var snap = SkyDataParser.ParseOvation(OvationSample, 5);

            Assert.NotNull(snap);
            // [0,-90,3] dropped (below 5); 4.6 rounds to 5 and is kept.
            Assert.Equal(4, snap!.Points.Count);
            Assert.DoesNotContain(snap.Points, p => p[2] < 5);
        }

        [Fact]
        public void ParseOvation_ConvertsLongitudeToMinus180To180()
        {
            var snap = SkyDataParser.ParseOvation(OvationSample, 5)!;

            Assert.Contains(snap.Points, p => p[0] == 10 && p[1] == 65 && p[2] == 12);
            Assert.Contains(snap.Points, p => p[0] == -160 && p[1] == 67 && p[2] == 40);  // 200 -> -160
            Assert.Contains(snap.Points, p => p[0] == -1 && p[1] == 70);                  // 359 -> -1
            Assert.Contains(snap.Points, p => p[0] == -180 && p[1] == 60);                // 180 -> -180
        }

        [Fact]
        public void ParseOvation_ReadsTimes()
        {
            var snap = SkyDataParser.ParseOvation(OvationSample, 5)!;
            Assert.Equal("2026-09-29T00:08:00Z", snap.ObservationTime);
            Assert.Equal("2026-09-29T01:32:00Z", snap.ForecastTime);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not json")]
        [InlineData("[]")]
        [InlineData(@"{""coordinates"": 5}")]
        public void ParseOvation_ReturnsNull_ForUnexpectedShape(string? json)
        {
            Assert.Null(SkyDataParser.ParseOvation(json, 5));
        }

        [Fact]
        public void ParseOvation_SkipsMalformedEntries()
        {
            var snap = SkyDataParser.ParseOvation(@"{""coordinates"": [[1, 60], [""a"", 60, 50], [2, 61, 50]]}", 5);
            Assert.NotNull(snap);
            Assert.Single(snap!.Points);
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(179, 179)]
        [InlineData(180, -180)]
        [InlineData(270, -90)]
        [InlineData(359, -1)]
        [InlineData(360, 0)]
        [InlineData(-10, -10)]
        public void NormalizeLongitude_MapsToMinus180To179(int input, int expected)
        {
            Assert.Equal(expected, SkyDataParser.NormalizeLongitude(input));
        }
    }
}
