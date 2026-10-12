using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using TodayInSpace.Core;
using TodayInSpace.Web.Controllers;
using TodayInSpace.Web.Sky;

namespace TodayInSpace.Web.Tests
{
    // The web app serves the satellite counts the function stored, and never downloads SATCAT itself.
    public class SatellitesServiceTests
    {
        private static readonly DateTimeOffset Checked = new(2026, 10, 11, 18, 30, 0, TimeSpan.Zero);
        private static readonly DateTimeOffset Now = Checked.AddHours(2);

        private static readonly string Stored = Satcat.Serialize(new SatellitesSnapshot(
            17191, 11129, new[] { new OwnerCount("US", "United States", "US", 12965) },
            34970, 20095, 12523, 2297, 55, Checked.AddMinutes(22), "\"abc\"", Checked, Satcat.Source));

        private class FakeStore : ISatelliteStore, ILaunchStore, IPeopleStore
        {
            public string? Json { get; init; }
            public int Reads { get; private set; }

            public Task<string?> ReadAsync()
            {
                Reads++;
                return Task.FromResult(Json);
            }
        }

        // Fails the test if anything tries to go upstream: the web app must not call CelesTrak for this.
        private class NoNetwork : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                throw new InvalidOperationException("Unexpected HTTP call to " + request.RequestUri);
        }

        private class FixedClock(DateTimeOffset now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => now;
        }

        private static SkyDataService Service(FakeStore satellites, DateTimeOffset? now = null) =>
            new(new HttpClient(new NoNetwork()), new FakeStore(), new FakeStore(), satellites, new MemoryCache(new MemoryCacheOptions()),
                NullLogger<SkyDataService>.Instance, new FixedClock(now ?? Now));

        private static SkyController Controller(SkyDataService service) => new(service)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        [Fact]
        public async Task GetSatellites_ReadsStoredSnapshot_WithoutCallingCelesTrak()
        {
            var satellites = (await Service(new FakeStore { Json = Stored }).GetSatellitesAsync())!;

            Assert.Equal((17191, 11129, 34970), (satellites.Active, satellites.Starlink, satellites.InOrbit));
            Assert.Equal("United States", satellites.Owners.Single().Name);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("not json")]
        public async Task GetSatellites_NoUsableSnapshot_ReturnsNull(string? json)
        {
            Assert.Null(await Service(new FakeStore { Json = json }).GetSatellitesAsync());
        }

        [Fact]
        public async Task GetSatellites_CheckedOverSevenDaysAgo_ReturnsNull()
        {
            Assert.NotNull(await Service(new FakeStore { Json = Stored }, Checked.AddDays(7)).GetSatellitesAsync());
            Assert.Null(await Service(new FakeStore { Json = Stored }, Checked.AddDays(8)).GetSatellitesAsync());
        }

        [Fact]
        public async Task GetSatellites_SecondCall_IsServedFromCache()
        {
            var store = new FakeStore { Json = Stored };
            var service = Service(store);

            await service.GetSatellitesAsync();
            await service.GetSatellitesAsync();

            Assert.Equal(1, store.Reads);
        }

        [Fact]
        public async Task Endpoint_NoSnapshot_Returns503()
        {
            var result = await Controller(Service(new FakeStore())).GetSatellites();

            Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<StatusCodeResult>(result).StatusCode);
        }

        [Fact]
        public async Task Endpoint_Snapshot_Returns200WithHourCache()
        {
            var controller = Controller(Service(new FakeStore { Json = Stored }));

            var result = await controller.GetSatellites();

            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.Equal(17191, Assert.IsType<SatellitesSnapshot>(ok.Value).Active);
            Assert.Equal("public, max-age=3600", controller.Response.Headers.CacheControl.ToString());
        }
    }
}
