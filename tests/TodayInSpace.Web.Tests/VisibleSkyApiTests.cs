using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TodayInSpace.Core.Visibility;
using TodayInSpace.Web.Controllers;
using TodayInSpace.Web.Services;
using TodayInSpace.Web.Sky;

namespace TodayInSpace.Web.Tests
{
    // GET /api/sky/visible: input checks, location rounding, caching, and the JSON shape apps will depend on.
    public class VisibleSkyApiTests
    {
        private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

        // Serves the same ISS elements for every satellite, and counts the calls.
        private class FakeCelesTrak : HttpMessageHandler
        {
            public int Calls { get; private set; }
            public bool Down { get; init; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Calls++;
                if (Down)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "ISS (ZARYA)\n" +
                        "1 25544U 98067A   26284.17638745  .00009430  00000+0  18014-3 0  9995\n" +
                        "2 25544  51.6312  86.0933 0006713 248.1316 111.8959 15.48821882589734\n"),
                });
            }
        }

        private class FixedClock(DateTimeOffset now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => now;
        }

        private class NoStore : IPeopleStore, ILaunchStore, ISatelliteStore
        {
            public Task<string?> ReadAsync() => Task.FromResult<string?>(null);
        }

        private static (VisibleSkyService Service, VisibleSkyCache Cache, FakeCelesTrak Http) Service(bool down = false)
        {
            var http = new FakeCelesTrak { Down = down };
            var clock = new FixedClock(Now);
            var sky = new SkyDataService(new HttpClient(http), new NoStore(), new NoStore(), new NoStore(),
                new MemoryCache(new MemoryCacheOptions()), NullLogger<SkyDataService>.Instance, clock);
            var cache = new VisibleSkyCache();
            // NWS gets the same TLE text, which isn't JSON, so these tests run without place or clouds.
            var nws = new NwsWeatherService(new HttpClient(http), new NwsCache(), NullLogger<NwsWeatherService>.Instance);
            var digests = new DigestService(new ConfigurationBuilder().Build());
            var service = new VisibleSkyService(sky, nws, digests, new MemoryCache(new MemoryCacheOptions()), cache, clock,
                NullLogger<VisibleSkyService>.Instance);
            return (service, cache, http);
        }

        private static SkyController Controller() => new(null!)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        [Theory]
        [InlineData(null, -122.9)]
        [InlineData(47.0, null)]
        [InlineData(90.5, 0.0)]
        [InlineData(-91.0, 0.0)]
        [InlineData(0.0, 180.01)]
        [InlineData(0.0, -181.0)]
        [InlineData(double.NaN, 0.0)]
        [InlineData(0.0, double.PositiveInfinity)]
        public async Task BadLocation_Is400(double? lat, double? lon)
        {
            var (service, _, http) = Service();

            Assert.IsType<BadRequestResult>(await Controller().GetVisible(lat, lon, service));
            Assert.Equal(0, http.Calls);
        }

        [Theory]
        [InlineData(47.04, 47.05)]
        [InlineData(47.02, 47.0)]
        [InlineData(-122.876, -122.9)]
        [InlineData(-122.87, -122.85)]
        [InlineData(0.01, 0.0)]
        [InlineData(179.99, 180.0)]
        public void Round_SnapsToFiveHundredths(double degrees, double expected)
        {
            Assert.Equal(expected, SkyLocation.Round(degrees));
        }

        [Fact]
        public void CacheKey_IsSharedByNearbyLocations()
        {
            Assert.Equal("sky:visible:47.05:-122.90", SkyLocation.CacheKey(47.04, -122.90));
            Assert.Equal(SkyLocation.CacheKey(47.04, -122.90), SkyLocation.CacheKey(47.06, -122.89));
            Assert.NotEqual(SkyLocation.CacheKey(47.04, -122.90), SkyLocation.CacheKey(47.10, -122.90));
        }

        [Fact]
        public async Task Visible_ReturnsRoundedLocation_AndCachesIt()
        {
            var (service, cache, _) = Service();
            var controller = Controller();

            var ok = Assert.IsType<OkObjectResult>(await controller.GetVisible(47.04, -122.90, service));
            var sky = (VisibleSkyResponse)ok.Value!;

            Assert.Equal((47.05, -122.9), (sky.Lat, sky.Lon));
            Assert.Equal(3, sky.Satellites.Count);
            Assert.NotEmpty(sky.Passes);
            Assert.Equal("public, max-age=900", controller.Response.Headers.CacheControl.ToString());

            // A visitor a kilometre away gets the same cached answer.
            Assert.Same(sky, await service.GetAsync(47.06, -122.89));
            Assert.Equal(1, cache.Cache.Count);
        }

        [Fact]
        public async Task Visible_WithoutOrbitData_StillAnswers()
        {
            var (service, _, _) = Service(down: true);

            var sky = await service.GetAsync(47.0, -122.9);

            Assert.Empty(sky.Passes);
            Assert.All(sky.Satellites, s => Assert.False(s.Available));
        }

        [Fact]
        public void CacheHasASizeLimit()
        {
            using var cache = new VisibleSkyCache();

            for (int i = 0; i < VisibleSkyCache.MaxLocations + 50; i++)
                cache.Cache.Set(i, i, new MemoryCacheEntryOptions { Size = 1 });

            Assert.True(cache.Cache.Count <= VisibleSkyCache.MaxLocations);
        }

        // The website and future apps read these exact names. Later work may ADD fields, so this only
        // checks that every name below is still there; a rename or removal fails here.
        [Fact]
        public void ResponseShape_IsLocked()
        {
            var point = new SkyPoint(new DateTime(2026, 10, 17, 13, 46, 0, DateTimeKind.Utc), 240.4, 10.0);
            var sky = new VisibleSkyResponse(47.05, -122.9, new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc), 7,
                new[] { new VisiblePass("iss", "ISS", point, point, point, 407) },
                new[] { new SatelliteVisibility("iss", "ISS", 1, true, true) });

            // The same settings ASP.NET Core uses for API responses.
            var json = JsonDocument.Parse(JsonSerializer.Serialize(sky, new JsonSerializerOptions(JsonSerializerDefaults.Web))).RootElement;

            AssertHas(json, "lat", "lon", "generated", "days", "passes", "satellites");
            var pass = json.GetProperty("passes")[0];
            AssertHas(pass, "satellite", "name", "start", "max", "end", "durationSeconds");
            foreach (var name in new[] { "start", "max", "end" })
                AssertHas(pass.GetProperty(name), "time", "azimuth", "elevation");
            AssertHas(json.GetProperty("satellites")[0], "id", "name", "visiblePasses", "anyPassAbove10");

            // ISO-8601 UTC.
            Assert.Equal("2026-10-17T13:46:00Z", pass.GetProperty("start").GetProperty("time").GetString());
            Assert.Equal("2026-10-11T12:00:00Z", json.GetProperty("generated").GetString());
        }

        private static void AssertHas(JsonElement element, params string[] names)
        {
            foreach (var name in names)
                Assert.True(element.TryGetProperty(name, out _), $"Missing field \"{name}\"");
        }
    }
}
