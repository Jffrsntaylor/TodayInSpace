using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using TodayInSpace.Core;
using TodayInSpace.Web.Controllers;
using TodayInSpace.Web.Sky;

namespace TodayInSpace.Web.Tests
{
    // The web app serves the launches snapshot the function stored, and never calls LL2 itself.
    public class LaunchesServiceTests
    {
        private static readonly DateTimeOffset Updated = new(2026, 10, 11, 12, 45, 0, TimeSpan.Zero);
        private static readonly DateTimeOffset Now = Updated.AddMinutes(30);

        private static readonly string Stored = Launches.Serialize(new LaunchesSnapshot(
            new YearStats(2026, 239, 232, 7, 0, 0, new[] { new ProviderCount("SpaceX", 115) },
                new LatestLaunch("Falcon 9 Block 5 | SDA Tranche 1 Transport Layer A", Updated.AddDays(-1), "Success"), Updated),
            new[] { new UpcomingLaunch("Falcon 9 Block 5 | Starlink Group 15-25", Updated.AddDays(1), "SEC", 1, "Go", null, "SpaceX", "Vandenberg SFB, CA, USA") },
            Updated,
            Launches.Source));

        private class FakeStore : ILaunchStore, IPeopleStore
        {
            public string? Json { get; init; }
            public int Reads { get; private set; }

            public Task<string?> ReadAsync()
            {
                Reads++;
                return Task.FromResult(Json);
            }
        }

        // Fails the test if anything tries to go upstream: the web app must not call LL2.
        private class NoNetwork : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                throw new InvalidOperationException("Unexpected HTTP call to " + request.RequestUri);
        }

        private class FixedClock(DateTimeOffset now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => now;
        }

        private static SkyDataService Service(FakeStore launches, DateTimeOffset? now = null) =>
            new(new HttpClient(new NoNetwork()), new FakeStore(), launches, new MemoryCache(new MemoryCacheOptions()),
                NullLogger<SkyDataService>.Instance, new FixedClock(now ?? Now));

        private static SkyController Controller(SkyDataService service) => new(service)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        [Fact]
        public async Task GetLaunches_ReadsStoredSnapshot_WithoutCallingLl2()
        {
            var launches = (await Service(new FakeStore { Json = Stored }).GetLaunchesAsync())!;

            Assert.Equal(239, launches.Stats!.Total);
            Assert.Equal("Falcon 9 Block 5 | Starlink Group 15-25", launches.Upcoming.Single().Name);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("not json")]
        public async Task GetLaunches_NoUsableSnapshot_ReturnsNull(string? json)
        {
            Assert.Null(await Service(new FakeStore { Json = json }).GetLaunchesAsync());
        }

        [Fact]
        public async Task GetLaunches_OlderThanSevenDays_ReturnsNull()
        {
            Assert.NotNull(await Service(new FakeStore { Json = Stored }, Updated.AddDays(7)).GetLaunchesAsync());
            Assert.Null(await Service(new FakeStore { Json = Stored }, Updated.AddDays(8)).GetLaunchesAsync());
        }

        [Fact]
        public async Task GetLaunches_SecondCall_IsServedFromCache()
        {
            var store = new FakeStore { Json = Stored };
            var service = Service(store);

            await service.GetLaunchesAsync();
            await service.GetLaunchesAsync();

            Assert.Equal(1, store.Reads);
        }

        [Fact]
        public async Task Endpoint_NoSnapshot_Returns503()
        {
            var result = await Controller(Service(new FakeStore())).GetLaunches();

            Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<StatusCodeResult>(result).StatusCode);
        }

        [Fact]
        public async Task Endpoint_Snapshot_Returns200WithShortCache()
        {
            var controller = Controller(Service(new FakeStore { Json = Stored }));

            var result = await controller.GetLaunches();

            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.Equal(239, Assert.IsType<LaunchesSnapshot>(ok.Value).Stats!.Total);
            Assert.Equal("public, max-age=600", controller.Response.Headers.CacheControl.ToString());
        }
    }
}
