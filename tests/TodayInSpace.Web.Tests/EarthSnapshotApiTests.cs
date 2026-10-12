using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using TodayInSpace.Core.Snapshot;
using TodayInSpace.Web.Controllers;
using TodayInSpace.Web.Sky;

namespace TodayInSpace.Web.Tests
{
    // GET /api/sky/earth.png: real PNGs through SkiaSharp (so CI on Linux proves the native library loads),
    // drawn from tiny made-up maps: an all-blue day map and an all-white "city lights" map.
    public class EarthSnapshotApiTests
    {
        private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

        private static readonly Lazy<EarthSnapshotRenderer> Renderer = new(() => new EarthSnapshotRenderer(
            Solid(0, 0, 255), Solid(255, 255, 255)));

        private class FakeCelesTrak : HttpMessageHandler
        {
            public bool Down { get; init; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                if (Down)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                // Served for the aurora feed too, which then just has no data.
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

        // Writes every log line, with its raw arguments, into a list shared by all the services under test.
        private class CapturingLogger<T>(List<string> lines) : ILogger<T>
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                lines.Add(formatter(state, exception) + " | " + string.Join(", ",
                    (state as IEnumerable<KeyValuePair<string, object?>>)?.Select(kv => kv.Value) ?? []));
        }

        private static EarthTexture Solid(byte r, byte g, byte b)
        {
            const int w = 8, h = 4;
            var rgb = new byte[w * h * 3];
            for (int i = 0; i < rgb.Length; i += 3)
            {
                rgb[i] = r;
                rgb[i + 1] = g;
                rgb[i + 2] = b;
            }
            return new EarthTexture(w, h, rgb);
        }

        private static (EarthSnapshotService Service, EarthSnapshotCache Cache, List<string> Logs) Service(bool down = false)
        {
            var clock = new FixedClock(Now);
            var logs = new List<string>();
            var sky = new SkyDataService(new HttpClient(new FakeCelesTrak { Down = down }), new NoStore(), new NoStore(), new NoStore(),
                new MemoryCache(new MemoryCacheOptions()), new CapturingLogger<SkyDataService>(logs), clock);
            var cache = new EarthSnapshotCache();
            var service = new EarthSnapshotService(sky, Renderer.Value, cache, clock, new CapturingLogger<EarthSnapshotService>(logs));
            return (service, cache, logs);
        }

        private static SkyController Controller() => new(null!)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        private static SKBitmap Decode(IActionResult result)
        {
            var file = Assert.IsType<FileContentResult>(result);
            Assert.Equal("image/png", file.ContentType);
            // PNG signature
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, file.FileContents[..4]);
            return SKBitmap.Decode(file.FileContents);
        }

        [Fact]
        public async Task Default_IsA400pxPng_WithTransparentCorners()
        {
            var (service, _, _) = Service();
            var controller = Controller();

            using var png = Decode(await controller.GetEarth(null, null, null, null, null, service));

            Assert.Equal((400, 400), (png.Width, png.Height));
            Assert.Equal(0, png.GetPixel(0, 0).Alpha);
            Assert.Equal(0, png.GetPixel(399, 0).Alpha);
            Assert.Equal(0, png.GetPixel(0, 399).Alpha);
            Assert.Equal(0, png.GetPixel(399, 399).Alpha);
            Assert.Equal(255, png.GetPixel(200, 200).Alpha);
            Assert.Equal("public, max-age=600", controller.Response.Headers.CacheControl.ToString());
        }

        [Theory]
        [InlineData(100, 200)]
        [InlineData(800, 800)]
        [InlineData(4000, 800)]
        public async Task Size_IsClamped(int asked, int expected)
        {
            var (service, _, _) = Service();

            using var png = Decode(await Controller().GetEarth(47.04, -122.9, null, asked, null, service));

            Assert.Equal((expected, expected), (png.Width, png.Height));
        }

        [Fact]
        public async Task Card_Is1200x630_OnTheDarkBackground()
        {
            var (service, _, _) = Service();

            using var png = Decode(await Controller().GetEarth(null, null, null, null, "card", service));

            Assert.Equal((1200, 630), (png.Width, png.Height));
            Assert.Equal(new SKColor(0x04, 0x06, 0x0d, 255), png.GetPixel(0, 0));
        }

        [Theory]
        [InlineData(47.0, null, null, null)]
        [InlineData(91.0, 0.0, null, null)]
        [InlineData(null, null, "mars", null)]
        [InlineData(null, null, null, "gif")]
        public async Task BadInput_Is400(double? lat, double? lon, string? center, string? format)
        {
            var (service, _, _) = Service();

            Assert.IsType<BadRequestResult>(await Controller().GetEarth(lat, lon, center, null, format, service));
        }

        [Fact]
        public async Task NoOrbitData_IssCentred_Is503_ButAPlaceStillWorks()
        {
            var (service, _, _) = Service(down: true);

            var iss = await Controller().GetEarth(null, null, null, null, null, service);
            var place = await Controller().GetEarth(47.04, -122.9, null, null, null, service);

            Assert.Equal(503, Assert.IsType<StatusCodeResult>(iss).StatusCode);
            using var png = Decode(place);
            Assert.Equal(400, png.Width);
        }

        [Fact]
        public async Task DaySideIsTheDayMap_NightSideHasTheLights()
        {
            var (service, _, _) = Service();
            var sun = DayNight.SubsolarPoint(Now.UtcDateTime);
            double antiLon = DayNight.NormalizeLongitude(sun.Lon + 180);

            var day = await service.GetPngAsync(EarthSnapshotRequest.Parse(sun.Lat, sun.Lon, null, 200, null)!);
            var night = await service.GetPngAsync(EarthSnapshotRequest.Parse(-sun.Lat, antiLon, null, 200, null)!);

            // A spot just off the centre, clear of the pin.
            using var dayPng = SKBitmap.Decode(day);
            using var nightPng = SKBitmap.Decode(night);
            var d = dayPng.GetPixel(70, 100);
            var n = nightPng.GetPixel(70, 100);

            Assert.True(d.Blue > 200 && d.Red < 40 && d.Green < 40, $"day was {d}");
            // The all-white lights map shows through, not the blue day map.
            Assert.True(n.Red > 200 && n.Green > 200, $"night was {n}");
        }

        [Fact]
        public async Task LocationIsNeverLogged()
        {
            // Orbit data down and no aurora, so the feeds do log warnings.
            var (service, _, logs) = Service(down: true);

            await Controller().GetEarth(47.04, -122.9, null, null, null, service);
            await Controller().GetEarth(47.04, -122.9, "iss", null, null, service);

            Assert.NotEmpty(logs);
            Assert.All(logs, line =>
            {
                Assert.DoesNotContain("47.0", line);
                Assert.DoesNotContain("122.9", line);
                Assert.DoesNotContain("sky:earth", line);
            });
        }

        [Fact]
        public async Task SamePlaceAndTenMinutes_IsDrawnOnce()
        {
            var (service, cache, _) = Service();

            var a = await service.GetPngAsync(EarthSnapshotRequest.Parse(47.04, -122.9, null, 200, null)!);
            var b = await service.GetPngAsync(EarthSnapshotRequest.Parse(47.06, -122.89, null, 200, null)!);

            Assert.Same(a, b);
            Assert.Equal(1, cache.Cache.Count);
        }

        [Fact]
        public void CacheHasAByteLimit()
        {
            using var cache = new EarthSnapshotCache();
            var big = new byte[1024 * 1024];

            for (int i = 0; i < 50; i++)
                cache.Cache.Set(i, big, new MemoryCacheEntryOptions { Size = big.Length });

            Assert.True(cache.Cache.Count * (long)big.Length <= EarthSnapshotCache.MaxBytes);
        }
    }
}
