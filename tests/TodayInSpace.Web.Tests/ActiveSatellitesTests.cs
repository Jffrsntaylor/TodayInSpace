using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using TodayInSpace.Core;
using TodayInSpace.Function;
using TodayInSpace.Web.Controllers;
using TodayInSpace.Web.Sky;
using static TodayInSpace.Function.RefreshActiveSatellites;

namespace TodayInSpace.Web.Tests
{
    // The globe's point cloud: parsing CelesTrak's active TLE file, sorting satellites into the toggles,
    // the function's keep-or-write decision, and the endpoint that serves the stored file.
    public class ActiveSatellitesTests
    {
        private static readonly DateTimeOffset Now = new(2026, 10, 12, 19, 50, 0, TimeSpan.Zero);

        // Real lines from the 10-12 file: one of each group, one eccentric once-a-day orbit (QZS-2),
        // and the three stations that have their own markers. LF line endings, with a trailing blank line.
        private static readonly string Sample =
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "active-tles-sample.txt"));

        private static string Line2(string name)
        {
            var lines = Sample.Split('\n');
            return lines[Array.FindIndex(lines, l => l.Trim() == name) + 2];
        }

        [Fact]
        public void Parse_ReadsThreeLineBlocks_AndTrimsNames()
        {
            var satellites = ActiveSatellites.Parse(Sample);

            Assert.Equal(new[] { "STARLINK-1008", "GOES 19", "NAVSTAR 43 (USA 132)", "NOAA 20 (JPSS-1)", "QZS-2 (MICHIBIKI-2)" },
                satellites.Select(s => s.N));
            var starlink = satellites[0];
            Assert.StartsWith("1 44714U", starlink.L1);
            Assert.StartsWith("2 44714 ", starlink.L2);
            Assert.Equal(69, starlink.L1.Length);
            Assert.Equal(69, starlink.L2.Length);
        }

        [Fact]
        public void Parse_CrlfFile_GivesTheSameResult()
        {
            Assert.Equal(ActiveSatellites.Parse(Sample), ActiveSatellites.Parse(Sample.Replace("\n", "\r\n")));
        }

        [Fact]
        public void Parse_LeavesOutTheStationsShownSeparately()
        {
            var names = ActiveSatellites.Parse(Sample).Select(s => s.N).ToList();

            Assert.DoesNotContain("ISS (ZARYA)", names);
            Assert.DoesNotContain("HST", names);
            Assert.DoesNotContain("CSS (TIANHE)", names);
        }

        [Fact]
        public void ShownSeparately_MatchesTheStationsTheSiteTracks()
        {
            Assert.Equal(SkyDataService.Satellites.Values.OrderBy(n => n), ActiveSatellites.ShownSeparately.OrderBy(n => n));
        }

        [Fact]
        public void Parse_SkipsABrokenBlock_AndKeepsTheRest()
        {
            var lines = Sample.Split('\n').ToList();
            lines[1] = lines[1][..40];  // Starlink's line 1 cut short

            var names = ActiveSatellites.Parse(string.Join("\n", lines)).Select(s => s.N).ToList();

            Assert.DoesNotContain("STARLINK-1008", names);
            Assert.Contains("GOES 19", names);
            Assert.Equal(4, names.Count);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("<html>Too many requests</html>")]
        public void Parse_NotATleFile_GivesNothing(string? text)
        {
            Assert.Empty(ActiveSatellites.Parse(text));
        }

        [Theory]
        [InlineData("STARLINK-1008", ActiveSatellites.Starlink)]
        [InlineData("GOES 19", ActiveSatellites.Geostationary)]
        [InlineData("NAVSTAR 43 (USA 132)", ActiveSatellites.Navigation)]
        [InlineData("NOAA 20 (JPSS-1)", ActiveSatellites.Other)]
        [InlineData("QZS-2 (MICHIBIKI-2)", ActiveSatellites.Other)]  // once a day, but too eccentric for the ring
        public void Classify_SortsRealTles(string name, string group)
        {
            Assert.Equal(group, ActiveSatellites.Classify(name, Line2(name)));
        }

        [Fact]
        public void Classify_UnreadableLine2_IsNull()
        {
            Assert.Null(ActiveSatellites.Classify("JUNK", "2 99999  garbage"));
        }

        // The floor is lowered for the 5-satellite sample; the real one is 5,000.
        [Theory]
        [InlineData(5, true)]
        [InlineData(6, false)]
        public void Build_BelowTheFloor_IsNull(int minSatellites, bool built)
        {
            var snapshot = ActiveSatellites.Build(Sample, Now, minSatellites);

            Assert.Equal(built, snapshot != null);
        }

        [Fact]
        public void Build_RealFloor_RejectsASmallFile()
        {
            Assert.Null(ActiveSatellites.Build(Sample, Now));
        }

        [Fact]
        public void SerializeAndParse_RoundTrip_WithShortNames()
        {
            var snapshot = ActiveSatellites.Build(Sample, Now, minSatellites: 1)!;

            string json = ActiveSatellites.Serialize(snapshot);
            var back = ActiveSatellites.ParseJson(json)!;

            Assert.Contains("\"n\":\"STARLINK-1008\",\"g\":\"starlink\",\"l1\":\"1 44714U", json);
            Assert.Contains("\"source\":\"CelesTrak\"", json);
            Assert.Equal(snapshot.Updated, back.Updated);
            Assert.Equal(snapshot.Satellites, back.Satellites);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("not json")]
        [InlineData("{\"updated\":\"2026-10-12T00:00:00Z\"}")]
        public void ParseJson_NotOurs_IsNull(string? json)
        {
            Assert.Null(ActiveSatellites.ParseJson(json));
        }

        // ---- The function's decision, without CelesTrak or storage ----

        [Fact]
        public void Function_GoodFile_IsWritten()
        {
            var run = RefreshActiveSatellites.Decide(new TleResponse(HttpStatusCode.OK, Sample), Now, minSatellites: 5);

            Assert.Equal(5, run.Write!.Satellites.Count);
            Assert.Equal(Now, run.Write.Updated);
        }

        [Theory]
        [InlineData(6)]      // too few
        [InlineData(5_000)]  // the real floor
        public void Function_TooFew_KeepsPrevious(int minSatellites)
        {
            var run = RefreshActiveSatellites.Decide(new TleResponse(HttpStatusCode.OK, Sample), Now, minSatellites);

            Assert.Null(run.Write);
            Assert.Equal("too few satellites or unexpected format", run.Reason);
        }

        [Fact]
        public void Function_Garbage_KeepsPrevious()
        {
            var run = RefreshActiveSatellites.Decide(new TleResponse(HttpStatusCode.OK, "<html>blocked</html>"), Now, minSatellites: 1);

            Assert.Null(run.Write);
        }

        [Theory]
        [InlineData(HttpStatusCode.ServiceUnavailable)]
        [InlineData(HttpStatusCode.Forbidden)]
        [InlineData(null)]  // timeout or DNS failure
        public void Function_Error_WritesNothing(HttpStatusCode? status)
        {
            var run = RefreshActiveSatellites.Decide(new TleResponse(status, null), Now, minSatellites: 1);

            Assert.Null(run.Write);
            Assert.Equal("fetch failed", run.Reason);
        }

        // ---- The endpoint ----

        private class FakeStore : IActiveSatelliteStore
        {
            public string? Json { get; set; }
            public int Reads { get; private set; }

            public Task<string?> ReadAsync()
            {
                Reads++;
                return Task.FromResult(Json);
            }
        }

        private static ActiveSatellitesService Service(FakeStore store) =>
            new(store, new MemoryCache(new MemoryCacheOptions()), NullLogger<ActiveSatellitesService>.Instance);

        private static SkyController Controller() => new(null!)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        [Fact]
        public async Task Endpoint_NoBlob_Returns503()
        {
            var result = await Controller().GetActiveSatellites(Service(new FakeStore()));

            Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<StatusCodeResult>(result).StatusCode);
        }

        [Fact]
        public async Task Endpoint_Blob_Returns200AsStored_WithHourCache()
        {
            string stored = ActiveSatellites.Serialize(ActiveSatellites.Build(Sample, Now, minSatellites: 1)!);
            var controller = Controller();

            var result = await controller.GetActiveSatellites(Service(new FakeStore { Json = stored }));

            var content = Assert.IsType<ContentResult>(result);
            Assert.Equal(stored, content.Content);
            Assert.Equal("application/json", content.ContentType);
            Assert.Equal("public, max-age=3600", controller.Response.Headers.CacheControl.ToString());
        }

        [Fact]
        public async Task Service_SecondCall_IsServedFromMemory()
        {
            var store = new FakeStore { Json = "{\"satellites\":[]}" };
            var service = Service(store);

            await service.GetJsonAsync();
            await service.GetJsonAsync();

            Assert.Equal(1, store.Reads);
        }

        [Fact]
        public async Task Service_NotJson_IsNotServed()
        {
            Assert.Null(await Service(new FakeStore { Json = "<html>" }).GetJsonAsync());
        }
    }
}
