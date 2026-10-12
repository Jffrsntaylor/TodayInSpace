using TodayInSpace.Core.Visibility;

namespace TodayInSpace.Web.Tests
{
    // Parsing the National Weather Service API. The fixtures are real responses for 47.0,-122.9 (Olympia's zip,
    // rounded the way the API rounds it) from 2026-10-11, trimmed: the gridpoint keeps 2 temperature values
    // and 27 of its sky cover values, so there's a gap between Oct 12 05:00 and Oct 13 00:00 UTC.
    public class NwsWeatherTests
    {
        private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

        private static DateTime Utc(int day, int hour, int minute = 0) => new(2026, 10, day, hour, minute, 0, DateTimeKind.Utc);

        [Fact]
        public void PointsUrl_HasAtMostTwoDecimals()
        {
            Assert.Equal("https://api.weather.gov/points/47,-122.9", NwsWeather.PointsUrl(47.0, -122.9));
            Assert.Equal("https://api.weather.gov/points/47.05,-122.85", NwsWeather.PointsUrl(47.05, -122.85));
        }

        [Fact]
        public void ParsePoint_ReadsGridAndPlace()
        {
            var point = NwsWeather.ParsePoint(Fixture("nws-points.json"));

            Assert.NotNull(point);
            Assert.Equal(("SEW", 101, 43), (point.GridId, point.GridX, point.GridY));
            Assert.Equal("https://api.weather.gov/gridpoints/SEW/101,43", point.GridDataUrl);
            // NWS names the nearest town to the grid point; at this rounding that's Tumwater, next to Olympia.
            Assert.Equal(new Place("Tumwater", "WA", "America/Los_Angeles"), point.Place);
        }

        [Fact]
        public void ParsePoint_WithoutRelativeLocation_HasNoPlace()
        {
            var point = NwsWeather.ParsePoint("""{ "properties": { "gridId": "SEW", "gridX": 1, "gridY": 2 } }""");

            Assert.NotNull(point);
            Assert.Null(point.Place);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("<!DOCTYPE html><html>An Error Occurred</html>")]
        [InlineData("""{ "title": "Data Unavailable For Requested Point" }""")]
        [InlineData("""{ "properties": { "gridId": "../../evil", "gridX": 1, "gridY": 2 } }""")]
        [InlineData("""{ "properties": { "gridId": "SEW", "gridX": "1", "gridY": 2 } }""")]
        public void ParsePoint_RejectsAnythingElse(string? json)
        {
            Assert.Null(NwsWeather.ParsePoint(json));
        }

        [Theory]
        [InlineData("2026-10-11T15:00:00+00:00/PT1H", 11, 15, 11, 16)]
        [InlineData("2026-10-11T21:00:00+00:00/PT2H", 11, 21, 11, 23)]
        [InlineData("2026-10-13T21:00:00+00:00/PT3H", 13, 21, 14, 0)]
        [InlineData("2026-10-15T12:00:00+00:00/PT6H", 15, 12, 15, 18)]
        [InlineData("2026-10-11T12:00:00+00:00/P1DT6H", 11, 12, 12, 18)]
        [InlineData("2026-10-11T05:00:00-07:00/PT1H", 11, 12, 11, 13)]
        public void Interval_HasAStartAndADuration(string interval, int d1, int h1, int d2, int h2)
        {
            Assert.True(NwsWeather.TryParseInterval(interval, out var start, out var end));
            Assert.Equal(Utc(d1, h1), start);
            Assert.Equal(Utc(d2, h2), end);
            Assert.Equal(DateTimeKind.Utc, start.Kind);
        }

        [Theory]
        [InlineData("2026-10-11T15:00:00+00:00")]
        [InlineData("2026-10-11T15:00:00+00:00/")]
        [InlineData("2026-10-11T15:00:00+00:00/1 hour")]
        [InlineData("2026-10-11T15:00:00+00:00/PT0H")]
        [InlineData("yesterday/PT1H")]
        public void Interval_RejectsMalformed(string interval)
        {
            Assert.False(NwsWeather.TryParseInterval(interval, out _, out _));
        }

        [Fact]
        public void ParseSkyCover_ReadsEveryInterval()
        {
            var cover = NwsWeather.ParseSkyCover(Fixture("nws-gridpoint.json"));

            Assert.NotNull(cover);
            Assert.Equal(new DateTime(2026, 10, 11, 18, 26, 34, DateTimeKind.Utc), cover.Updated);
            Assert.Equal(27, cover.Periods.Count);
            Assert.Equal(new CloudPeriod(Utc(11, 12), Utc(11, 15), 17), cover.Periods[0]);     // PT3H
            Assert.Equal(new CloudPeriod(Utc(11, 15), Utc(11, 16), 29), cover.Periods[1]);     // PT1H
            Assert.Equal(new CloudPeriod(Utc(11, 21), Utc(11, 23), 2), cover.Periods[7]);      // PT2H
            Assert.Equal(new CloudPeriod(Utc(15, 12), Utc(15, 18), 64), cover.Periods[^1]);    // PT6H
        }

        [Theory]
        [InlineData(null)]
        [InlineData("not json")]
        [InlineData("""{ "properties": { "temperature": { "values": [] } } }""")]
        public void ParseSkyCover_RejectsAnythingElse(string? json)
        {
            Assert.Null(NwsWeather.ParseSkyCover(json));
        }

        [Fact]
        public void PercentAt_InsideAndOutsideTheForecast()
        {
            var periods = NwsWeather.ParseSkyCover(Fixture("nws-gridpoint.json"))!.Periods;

            Assert.Equal(17, NwsWeather.PercentAt(periods, Utc(11, 13, 30)));   // inside a PT3H
            Assert.Equal(29, NwsWeather.PercentAt(periods, Utc(11, 15)));       // start is inclusive
            Assert.Equal(28, NwsWeather.PercentAt(periods, Utc(11, 16)));       // end is exclusive
            Assert.Equal(42, NwsWeather.PercentAt(periods, Utc(15, 11, 59)));   // inside a PT6H
            Assert.Null(NwsWeather.PercentAt(periods, Utc(11, 11, 59)));        // before it starts
            Assert.Null(NwsWeather.PercentAt(periods, Utc(12, 12)));            // in the trimmed gap
            Assert.Null(NwsWeather.PercentAt(periods, Utc(15, 18)));            // after it ends
        }

        [Fact]
        public void Outlook_RangeOverTonight_AndSeriesForTheWeek()
        {
            var cover = NwsWeather.ParseSkyCover(Fixture("nws-gridpoint.json"))!;
            // Oct 12 02:00-05:00 UTC: the 2, 3 and 4% hours.
            var tonight = new Darkness(Utc(12, 2), Utc(12, 5));

            var outlook = NwsWeather.Outlook(cover, tonight, Utc(11, 16, 30), Utc(15, 0));

            Assert.Equal("NWS", outlook.Source);
            Assert.Equal((2, 4), (outlook.TonightMin, outlook.TonightMax));
            // The 15:00-16:00 hour has already ended; the 16:00 hour is under way, so it stays.
            Assert.Equal(Utc(11, 16), outlook.Series[0].Start);
            // Nothing that starts at or after the end of the window.
            Assert.All(outlook.Series, c => Assert.True(c.Start < Utc(15, 0)));
            Assert.Equal(Utc(14, 6), outlook.Series[^1].End);
        }

        [Fact]
        public void Outlook_WithoutDarkness_HasNoTonightRange()
        {
            var cover = NwsWeather.ParseSkyCover(Fixture("nws-gridpoint.json"))!;

            var outlook = NwsWeather.Outlook(cover, null, Utc(11, 12), Utc(18, 12));

            Assert.Null(outlook.TonightMin);
            Assert.Null(outlook.TonightMax);
            Assert.Equal(27, outlook.Series.Count);
        }
    }
}
