using System.Net;
using TodayInSpace.Core;
using TodayInSpace.Function;
using static TodayInSpace.Function.RefreshSatellites;

namespace TodayInSpace.Web.Tests
{
    // The RefreshSatellites function's decisions, run against canned CelesTrak responses instead of the network.
    public class RefreshSatellitesTests
    {
        private static readonly DateTimeOffset Now = new(2026, 10, 11, 18, 30, 0, TimeSpan.Zero);
        private static readonly DateTimeOffset LastModified = new(2026, 10, 11, 18, 52, 57, TimeSpan.Zero);

        private static readonly string Csv = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "satcat-sample.csv"));

        // The fixture's own numbers, as if stored six hours ago.
        private static SatellitesSnapshot Previous() =>
            Satcat.Build(Csv, LastModified.AddDays(-1), "\"old\"", Now.AddHours(-6), minRows: 17)!;

        private static RunResult Decide(SatcatResponse response, SatellitesSnapshot? previous) =>
            RefreshSatellites.Decide(response, previous, Now, minRows: 17);

        [Fact]
        public void NotModified_KeepsPreviousNumbers_WithNewCheckedTime()
        {
            var previous = Previous();

            var run = Decide(new SatcatResponse(HttpStatusCode.NotModified, null, null, null), previous);

            Assert.Equal(previous with { Checked = Now }, run.Write);
            Assert.False(run.CatalogChanged);
        }

        [Fact]
        public void NotModified_NothingStored_WritesNothing()
        {
            var run = Decide(new SatcatResponse(HttpStatusCode.NotModified, null, null, null), null);

            Assert.Null(run.Write);
        }

        [Fact]
        public void NewCatalog_IsWritten_WithItsHeaders()
        {
            var run = Decide(new SatcatResponse(HttpStatusCode.OK, Csv, LastModified, "\"new\""), Previous());

            Assert.True(run.CatalogChanged);
            Assert.Equal(9, run.Write!.Active);
            Assert.Equal((LastModified, "\"new\"", Now), (run.Write.CatalogUpdated, run.Write.ETag, run.Write.Checked));
        }

        [Fact]
        public void TruncatedFile_KeepsPrevious()
        {
            var truncated = string.Join("\n", Csv.Split('\n').Take(10));

            var run = Decide(new SatcatResponse(HttpStatusCode.OK, truncated, LastModified, "\"new\""), Previous());

            Assert.Null(run.Write);
            Assert.Equal("unexpected format", run.Reason);
        }

        [Fact]
        public void BigJump_KeepsPrevious()
        {
            var previous = Previous() with { Active = 100 };

            var run = Decide(new SatcatResponse(HttpStatusCode.OK, Csv, LastModified, "\"new\""), previous);

            Assert.Null(run.Write);
            Assert.StartsWith("looks wrong", run.Reason);
        }

        [Theory]
        [InlineData(HttpStatusCode.ServiceUnavailable)]
        [InlineData(HttpStatusCode.Forbidden)]
        [InlineData(null)]  // timeout or DNS failure
        public void Error_WritesNothing(HttpStatusCode? status)
        {
            var run = Decide(new SatcatResponse(status, null, null, null), Previous());

            Assert.Null(run.Write);
            Assert.Equal("fetch failed", run.Reason);
        }

        [Fact]
        public void Request_WithPrevious_IsConditional()
        {
            using var request = BuildRequest(Previous());

            Assert.Equal(Satcat.Url, request.RequestUri!.ToString());
            Assert.Equal("\"old\"", request.Headers.IfNoneMatch.ToString());
            Assert.Equal(LastModified.AddDays(-1), request.Headers.IfModifiedSince);
        }

        [Fact]
        public void Request_WithoutPrevious_DownloadsTheFile()
        {
            using var request = BuildRequest(null);

            Assert.Empty(request.Headers.IfNoneMatch);
            Assert.Null(request.Headers.IfModifiedSince);
        }
    }
}
