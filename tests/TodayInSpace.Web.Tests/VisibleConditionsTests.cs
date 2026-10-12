using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TodayInSpace.Core.Visibility;
using TodayInSpace.Web.Models;
using TodayInSpace.Web.Services;
using TodayInSpace.Web.Sky;

namespace TodayInSpace.Web.Tests
{
    // "Will I actually see it?": place name, aurora odds and clouds added to /api/sky/visible.
    public class VisibleConditionsTests
    {
        // Inside the NWS fixture's forecast, which starts 2026-10-11 12:00 UTC.
        private static readonly DateTimeOffset Now = new(2026, 10, 11, 19, 0, 0, TimeSpan.Zero);

        private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

        private const string IssTle =
            "ISS (ZARYA)\n" +
            "1 25544U 98067A   26284.17638745  .00009430  00000+0  18014-3 0  9995\n" +
            "2 25544  51.6312  86.0933 0006713 248.1316 111.8959 15.48821882589734\n";

        // Answers CelesTrak, NOAA's OVATION feed and NWS, and remembers which NWS URLs were asked for.
        private class FakeUpstream : HttpMessageHandler
        {
            public HttpStatusCode PointsStatus { get; init; } = HttpStatusCode.OK;
            public List<string> NwsCalls { get; } = new();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var url = request.RequestUri!;
                if (url.Host == "api.weather.gov")
                {
                    NwsCalls.Add(url.AbsoluteUri);
                    if (url.AbsolutePath.StartsWith("/points/"))
                        return Respond(PointsStatus, PointsStatus == HttpStatusCode.OK ? Fixture("nws-points.json") : "{}");
                    return Respond(HttpStatusCode.OK, Fixture("nws-gridpoint.json"));
                }
                if (url.Host == "services.swpc.noaa.gov")
                    return Respond(HttpStatusCode.OK, """{ "Observation Time": "2026-10-11T18:55:00Z", "coordinates": [[237, 47, 9]] }""");
                return Respond(HttpStatusCode.OK, IssTle);
            }

            private static Task<HttpResponseMessage> Respond(HttpStatusCode status, string body) =>
                Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }

        private class FixedClock(DateTimeOffset now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => now;
        }

        private class NoStore : IPeopleStore, ILaunchStore, ISatelliteStore
        {
            public Task<string?> ReadAsync() => Task.FromResult<string?>(null);
        }

        private class FakeDigests(DigestModel? digest) : DigestService(new ConfigurationBuilder().Build())
        {
            public override Task<DigestModel?> GetLatestAsync() => Task.FromResult(digest);
        }

        private static readonly DigestModel Digest = new()
        {
            Date = "2026-10-11",
            SpaceWeather = new SpaceWeatherInfo
            {
                Forecast = new() { new() { Day = "Sun", Kp = 3 }, new() { Day = "Mon", Kp = 5 }, new() { Day = "Tue", Kp = 7 } },
            },
        };

        private static VisibleSkyService Service(FakeUpstream http, DigestModel? digest = null)
        {
            var clock = new FixedClock(Now);
            var sky = new SkyDataService(new HttpClient(http), new NoStore(), new NoStore(), new NoStore(),
                new MemoryCache(new MemoryCacheOptions()), NullLogger<SkyDataService>.Instance, clock);
            var nws = new NwsWeatherService(new HttpClient(http), new NwsCache(), NullLogger<NwsWeatherService>.Instance);
            return new VisibleSkyService(sky, nws, new FakeDigests(digest ?? Digest), new MemoryCache(new MemoryCacheOptions()),
                new VisibleSkyCache(), clock, NullLogger<VisibleSkyService>.Instance);
        }

        [Fact]
        public async Task Olympia_GetsPlaceAuroraAndClouds()
        {
            var http = new FakeUpstream();

            var sky = await Service(http).GetAsync(46.98, -122.88);

            // Rounded to 0.05° before it goes anywhere, NWS included.
            Assert.Equal("https://api.weather.gov/points/47,-122.9", http.NwsCalls[0]);
            Assert.Equal("https://api.weather.gov/gridpoints/SEW/101,43", http.NwsCalls[1]);

            Assert.Equal(new Place("Tumwater", "WA", "America/Los_Angeles"), sky.Place);

            Assert.NotNull(sky.Aurora);
            Assert.Equal((5, 7), (sky.Aurora.KpForHorizon, sky.Aurora.KpForOverhead));
            Assert.Equal(9, sky.Aurora.NowPercent);
            Assert.Equal(new[] { "unlikely", "horizon", "overhead" }, sky.Aurora.Nights.Select(n => n.Chance));
            Assert.Equal("2026-10-11", sky.Aurora.Nights[0].Date);

            Assert.NotNull(sky.Clouds);
            Assert.Equal("NWS", sky.Clouds.Source);
            // Tonight is Oct 12 ~02:00 to ~14:00 UTC; the fixture covers 02:00-05:00 of it (2-4%).
            Assert.Equal((2, 4), (sky.Clouds.TonightMin, sky.Clouds.TonightMax));
            Assert.All(sky.Clouds.Series, c => Assert.True(c.End > Now.UtcDateTime));
        }

        [Fact]
        public async Task CloudPercent_IsOnPassesAndVisiblePlanets_WhereThereIsAForecast()
        {
            var sky = await Service(new FakeUpstream()).GetAsync(47.0, -122.9);
            var series = sky.Clouds!.Series;

            Assert.NotEmpty(sky.Passes);
            Assert.All(sky.Passes, p => Assert.Equal(NwsWeather.PercentAt(series, p.Max.Time), p.CloudPercent));
            Assert.Contains(sky.Passes, p => p.CloudPercent != null);

            Assert.All(sky.Planets, p =>
                Assert.Equal(p.Visible ? NwsWeather.PercentAt(series, p.Best.Time) : null, p.CloudPercent));
        }

        [Fact]
        public async Task OutsideTheUS_NoPlaceOrClouds_RestIntact()
        {
            // London: /points answers 404 (or a 500, which we treat the same).
            var http = new FakeUpstream { PointsStatus = HttpStatusCode.NotFound };
            var service = Service(http);

            var sky = await service.GetAsync(51.5, -0.1);

            Assert.Null(sky.Place);
            Assert.Null(sky.Clouds);
            Assert.All(sky.Passes, p => Assert.Null(p.CloudPercent));
            Assert.All(sky.Planets, p => Assert.Null(p.CloudPercent));
            Assert.Equal(3, sky.Satellites.Count);
            Assert.All(sky.Satellites, s => Assert.True(s.Available));
            Assert.NotNull(sky.Moon);
            Assert.NotNull(sky.Aurora);
            Assert.Single(http.NwsCalls);   // no gridpoint call without a grid cell
        }

        [Fact]
        public async Task NotCovered_IsRemembered()
        {
            var http = new FakeUpstream { PointsStatus = HttpStatusCode.NotFound };
            var nws = new NwsWeatherService(new HttpClient(http), new NwsCache(), NullLogger<NwsWeatherService>.Instance);

            await nws.GetAsync(51.5, -0.1);
            var again = await nws.GetAsync(51.5, -0.1);

            Assert.Null(again.Place);
            Assert.Single(http.NwsCalls);
        }

        [Fact]
        public async Task Nws_CachesThePointAndTheGrid()
        {
            var http = new FakeUpstream();
            var nws = new NwsWeatherService(new HttpClient(http), new NwsCache(), NullLogger<NwsWeatherService>.Instance);

            var first = await nws.GetAsync(47.0, -122.9);
            var second = await nws.GetAsync(47.0, -122.9);

            Assert.Equal(2, http.NwsCalls.Count);
            Assert.Same(first.Clouds, second.Clouds);
        }

        [Fact]
        public async Task WithoutADigest_AuroraStillHasNowAndThresholds()
        {
            var service = new VisibleSkyService(
                new SkyDataService(new HttpClient(new FakeUpstream()), new NoStore(), new NoStore(), new NoStore(),
                    new MemoryCache(new MemoryCacheOptions()), NullLogger<SkyDataService>.Instance, new FixedClock(Now)),
                new NwsWeatherService(new HttpClient(new FakeUpstream()), new NwsCache(), NullLogger<NwsWeatherService>.Instance),
                new FakeDigests(new DigestModel()), new MemoryCache(new MemoryCacheOptions()),
                new VisibleSkyCache(), new FixedClock(Now), NullLogger<VisibleSkyService>.Instance);

            var sky = await service.GetAsync(47.0, -122.9);

            Assert.NotNull(sky.Aurora);
            Assert.Empty(sky.Aurora.Nights);
            Assert.Equal(9, sky.Aurora.NowPercent);
        }

        // Brief 1's and 2's shape tests still run unchanged; this checks the fields added here,
        // including null rather than missing when there's no data.
        [Fact]
        public async Task ResponseShape_AddsPlaceAuroraAndClouds()
        {
            var json = Json(await Service(new FakeUpstream()).GetAsync(47.0, -122.9));

            AssertHas(json.GetProperty("place"), "city", "state", "timeZone");
            var aurora = json.GetProperty("aurora");
            AssertHas(aurora, "geomagneticLatitude", "kpForHorizon", "kpForOverhead", "nowPercent", "nights");
            AssertHas(aurora.GetProperty("nights")[0], "date", "kp", "chance");
            var clouds = json.GetProperty("clouds");
            AssertHas(clouds, "source", "updated", "tonightMin", "tonightMax", "series");
            AssertHas(clouds.GetProperty("series")[0], "start", "end", "percent");
            AssertHas(json.GetProperty("passes")[0], "cloudPercent");
            AssertHas(json.GetProperty("planets")[0], "cloudPercent");

            Assert.Equal("2026-10-11T18:26:34Z", clouds.GetProperty("updated").GetString());

            var abroad = Json(await Service(new FakeUpstream { PointsStatus = HttpStatusCode.NotFound }).GetAsync(51.5, -0.1));
            Assert.Equal(JsonValueKind.Null, abroad.GetProperty("place").ValueKind);
            Assert.Equal(JsonValueKind.Null, abroad.GetProperty("clouds").ValueKind);
            Assert.Equal(JsonValueKind.Null, abroad.GetProperty("planets")[0].GetProperty("cloudPercent").ValueKind);
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
