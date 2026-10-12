using System.Net;
using TodayInSpace.Core;
using TodayInSpace.Function;

namespace TodayInSpace.Web.Tests
{
    // The RefreshLaunches function's decisions, run against canned LL2 responses instead of the network.
    public class RefreshLaunchesTests
    {
        private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 45, 0, TimeSpan.Zero);

        private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

        private static readonly string UpcomingJson = Fixture("ll2-launches-upcoming.json");

        // Two year pages: the first points at the second, which is the last.
        private const string Page2Url = "https://ll.thespacedevs.com/2.3.0/launches/previous/?page=2";
        private const string Page1Json = @"{""count"": 2, ""next"": """ + Page2Url + @""", ""results"": [
            {""id"": ""a"", ""name"": ""Falcon 9 | A"", ""net"": ""2026-10-10T07:39:44Z"", ""status"": {""id"": 3, ""abbrev"": ""Success""},
             ""launch_service_provider"": {""id"": 121, ""name"": ""SpaceX""}}]}";
        private const string Page2Json = @"{""count"": 2, ""next"": null, ""results"": [
            {""id"": ""b"", ""name"": ""Long March 7A | B"", ""net"": ""2026-03-01T00:00:00Z"", ""status"": {""id"": 4, ""abbrev"": ""Failure""},
             ""launch_service_provider"": {""id"": 88, ""name"": ""China Aerospace Science and Technology Corporation""}}]}";

        // Answers each URL from a map; anything missing fails like a 503. Records what was asked for.
        private class FakeLl2
        {
            public Dictionary<string, string?> Responses { get; } = new();
            public List<string> Calls { get; } = new();

            public Task<(string? Body, HttpStatusCode? Status)> Fetch(string url)
            {
                Calls.Add(url);
                return Task.FromResult(Responses.TryGetValue(url, out var body) && body != null
                    ? (body, (HttpStatusCode?)HttpStatusCode.OK)
                    : ((string?)null, (HttpStatusCode?)HttpStatusCode.ServiceUnavailable));
            }

            public static FakeLl2 AllWorking()
            {
                var ll2 = new FakeLl2();
                ll2.Responses[Launches.UpcomingUrl] = UpcomingJson;
                ll2.Responses[Launches.YearUrl(2026)] = Page1Json;
                ll2.Responses[Page2Url] = Page2Json;
                return ll2;
            }
        }

        // A snapshot from an hour ago whose next launch hasn't flown yet, so the year isn't re-counted.
        private static LaunchesSnapshot Previous(DateTimeOffset? statsUpdated = null) => new(
            new YearStats(2026, 100, 99, 1, 0, 0, new[] { new ProviderCount("SpaceX", 100) }, null, statsUpdated ?? Now.AddHours(-1)),
            new[] { new UpcomingLaunch("Old next launch", Now.AddDays(1), "SEC", 1, "Go", null, "SpaceX", null) },
            Now.AddHours(-1),
            Launches.Source);

        [Fact]
        public async Task NoPrevious_FetchesUpcomingAndEveryYearPage()
        {
            var ll2 = FakeLl2.AllWorking();

            var run = await RefreshLaunches.RunAsync(ll2.Fetch, null, Now);

            Assert.Equal(new[] { Launches.UpcomingUrl, Launches.YearUrl(2026), Page2Url }, ll2.Calls);
            Assert.Equal(2, run.Pages);
            var snapshot = run.Refresh!.Snapshot;
            Assert.Equal((2, 1, 1), (snapshot.Stats!.Total, snapshot.Stats.Successes, snapshot.Stats.Failures));
            Assert.Equal(5, snapshot.Upcoming.Count);
            Assert.Equal((Launches.Fresh, Launches.Fresh), (run.Refresh.StatsFrom, run.Refresh.UpcomingFrom));
        }

        [Fact]
        public async Task RecentStats_OnlyFetchesUpcoming()
        {
            var ll2 = FakeLl2.AllWorking();
            var previous = Previous();

            var run = await RefreshLaunches.RunAsync(ll2.Fetch, previous, Now);

            Assert.Equal(new[] { Launches.UpcomingUrl }, ll2.Calls);
            Assert.Same(previous.Stats, run.Refresh!.Snapshot.Stats);
            Assert.Equal((Launches.Previous, Launches.Fresh), (run.Refresh.StatsFrom, run.Refresh.UpcomingFrom));
        }

        [Fact]
        public async Task UpcomingFails_KeepsPreviousUpcoming_StillWritesFreshStats()
        {
            var ll2 = FakeLl2.AllWorking();
            ll2.Responses[Launches.UpcomingUrl] = null;
            var previous = Previous(statsUpdated: Now.AddHours(-7));

            var run = await RefreshLaunches.RunAsync(ll2.Fetch, previous, Now);

            Assert.Same(previous.Upcoming, run.Refresh!.Snapshot.Upcoming);
            Assert.Equal(2, run.Refresh.Snapshot.Stats!.Total);
            Assert.Equal((Launches.Fresh, Launches.Previous), (run.Refresh.StatsFrom, run.Refresh.UpcomingFrom));
        }

        [Fact]
        public async Task YearPageFails_KeepsPreviousStats_StillWritesFreshUpcoming()
        {
            var ll2 = FakeLl2.AllWorking();
            ll2.Responses[Page2Url] = @"{""detail"": ""Request was throttled.""}";
            var previous = Previous(statsUpdated: Now.AddHours(-7));

            var run = await RefreshLaunches.RunAsync(ll2.Fetch, previous, Now);

            // Page 1 alone would be a count that's too low, so none of it is used.
            Assert.Same(previous.Stats, run.Refresh!.Snapshot.Stats);
            Assert.Equal(5, run.Refresh.Snapshot.Upcoming.Count);
            Assert.Equal("year page fetch failed", run.StatsError);
            Assert.Equal((Launches.Previous, Launches.Fresh), (run.Refresh.StatsFrom, run.Refresh.UpcomingFrom));
        }

        [Fact]
        public async Task BothFail_WritesNothing()
        {
            var ll2 = new FakeLl2();

            var run = await RefreshLaunches.RunAsync(ll2.Fetch, Previous(statsUpdated: Now.AddHours(-7)), Now);

            Assert.Null(run.Refresh);
        }

        [Fact]
        public async Task UpcomingFails_AndStatsNotDue_WritesNothing()
        {
            var ll2 = new FakeLl2();

            var run = await RefreshLaunches.RunAsync(ll2.Fetch, Previous(), Now);

            Assert.Null(run.Refresh);
            Assert.Equal(new[] { Launches.UpcomingUrl }, ll2.Calls);
        }

        [Fact]
        public async Task NextLaunchTimeHasPassed_RecountsTheYear()
        {
            var ll2 = FakeLl2.AllWorking();
            var previous = Previous() with
            {
                Upcoming = new[] { new UpcomingLaunch("Just flew", Now.AddMinutes(-20), "SEC", 1, "Go", null, "SpaceX", null) },
            };

            var run = await RefreshLaunches.RunAsync(ll2.Fetch, previous, Now);

            Assert.Equal(Launches.Fresh, run.Refresh!.StatsFrom);
            Assert.Equal(2, run.Refresh.Snapshot.Stats!.Total);
        }

        [Fact]
        public async Task TooManyPages_KeepsPreviousStats()
        {
            // Every page says there's another: stop at the cap rather than keep spending LL2 calls.
            var ll2 = FakeLl2.AllWorking();
            ll2.Responses[Launches.YearUrl(2026)] = Page1Json;
            ll2.Responses[Page2Url] = Page1Json;
            var previous = Previous(statsUpdated: Now.AddHours(-7));

            var run = await RefreshLaunches.RunAsync(ll2.Fetch, previous, Now);

            Assert.Equal(Launches.MaxYearPages, run.Pages);
            Assert.Equal("too many year pages", run.StatsError);
            Assert.Same(previous.Stats, run.Refresh!.Snapshot.Stats);
        }

        [Fact]
        public async Task NewYear_EmptyFirstPage_IsAValidZero()
        {
            var newYear = new DateTimeOffset(2027, 1, 1, 0, 45, 0, TimeSpan.Zero);
            var ll2 = FakeLl2.AllWorking();
            ll2.Responses[Launches.YearUrl(2027)] = @"{""count"": 0, ""next"": null, ""previous"": null, ""results"": []}";

            var run = await RefreshLaunches.RunAsync(ll2.Fetch, Previous(), newYear);

            Assert.Contains(Launches.YearUrl(2027), ll2.Calls);
            Assert.Equal((2027, 0), (run.Refresh!.Snapshot.Stats!.Year, run.Refresh.Snapshot.Stats.Total));
            Assert.Equal(Launches.Fresh, run.Refresh.StatsFrom);
        }

        [Fact]
        public async Task NextLinkOffLl2_IsNotFollowed()
        {
            var ll2 = FakeLl2.AllWorking();
            ll2.Responses[Launches.YearUrl(2026)] = Page1Json.Replace(Page2Url, "https://example.com/next");

            var run = await RefreshLaunches.RunAsync(ll2.Fetch, null, Now);

            Assert.DoesNotContain("https://example.com/next", ll2.Calls);
            Assert.Equal("unexpected next link", run.StatsError);
            Assert.Null(run.Refresh!.Snapshot.Stats);
        }
    }
}
