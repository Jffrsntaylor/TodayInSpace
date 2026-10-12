using TodayInSpace.Core;

namespace TodayInSpace.Web.Tests
{
    // Parsing LL2's launch lists and building the "Launches" card from them. The fixtures are real LL2
    // responses from 2026-10-11, trimmed to a few launches and the fields we read.
    public class LaunchesTests
    {
        private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 45, 0, TimeSpan.Zero);

        private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

        private static Ll2LaunchPage PreviousPage() => LaunchesParser.ParsePage(Fixture("ll2-launches-previous.json"))!;

        private static Ll2Launch Launch(string id, int statusId, int? providerId, string? provider, int day) =>
            new(id, "Launch " + id, new DateTimeOffset(2026, 10, day, 0, 0, 0, TimeSpan.Zero), statusId, "", providerId, provider, "SEC", null, null);

        private static YearStats Stats(int year = 2026, DateTimeOffset? updated = null) =>
            new(year, 1, 1, 0, 0, 0, Array.Empty<ProviderCount>(), null, updated ?? Now.AddHours(-1));

        private static UpcomingLaunch Upcoming(DateTimeOffset net) =>
            new("Falcon 9 Block 5 | Starlink", net, "SEC", Launches.StatusGo, "Go", null, "SpaceX", "Vandenberg SFB, CA, USA");

        private static LaunchesSnapshot Snapshot(YearStats? stats, params UpcomingLaunch[] upcoming) =>
            new(stats, upcoming, Now.AddHours(-1), Launches.Source);

        // ---- Parsing ----

        [Fact]
        public void ParsePage_RealPage_ReadsLaunchesNextAndCount()
        {
            var page = PreviousPage();

            Assert.Equal(239, page.Count);
            Assert.StartsWith("https://ll.thespacedevs.com/2.3.0/launches/previous/", page.Next);
            Assert.Equal(4, page.Launches.Count);

            var first = page.Launches[0];
            Assert.Equal("da43dc2c-00af-4cc1-8ef1-90a60b2eb9e5", first.Id);
            Assert.Equal("Falcon 9 Block 5 | SDA Tranche 1 Transport Layer A", first.Name);
            Assert.Equal(new DateTimeOffset(2026, 10, 10, 7, 39, 44, TimeSpan.Zero), first.Net);
            Assert.Equal(Launches.StatusSuccess, first.StatusId);
            Assert.Equal(121, first.ProviderId);
            Assert.Equal("SpaceX", first.ProviderName);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not json")]
        [InlineData("[]")]
        [InlineData(@"{""detail"": ""Request was throttled.""}")]
        [InlineData(@"{""results"": []}")]
        public void ParsePage_NotALaunchesPage_ReturnsNull(string? json)
        {
            Assert.Null(LaunchesParser.ParsePage(json));
        }

        [Fact]
        public void ParsePage_EmptyYear_IsAnEmptyPageNotNull()
        {
            // January 1, before anything has flown.
            var page = LaunchesParser.ParsePage(@"{""count"": 0, ""next"": null, ""previous"": null, ""results"": []}")!;

            Assert.Empty(page.Launches);
            Assert.Null(page.Next);
        }

        // ---- Year stats ----

        [Fact]
        public void BuildStats_RealPage_CountsStatusesAndProviders()
        {
            var built = Launches.BuildStats(2026, new[] { PreviousPage() }, Now);
            var stats = built.Stats;

            Assert.Equal(2026, stats.Year);
            Assert.Equal(4, stats.Total);
            Assert.Equal(3, stats.Successes);
            Assert.Equal(1, stats.Failures);
            Assert.Equal(0, stats.PartialFailures);
            Assert.Equal(0, stats.InFlight);
            Assert.Equal(Now, stats.StatsUpdated);

            // CASC (two launches, one a failure) under its short name, then the singles by name.
            Assert.Equal(new[]
            {
                new ProviderCount("CASC", 2),
                new ProviderCount("Korea Aerospace Research Institute", 1),
                new ProviderCount("SpaceX", 1),
            }, stats.Providers);
        }

        [Fact]
        public void BuildStats_RealPage_LatestIsTheNewestLaunch()
        {
            var latest = Launches.BuildStats(2026, new[] { PreviousPage() }, Now).Stats.Latest!;

            Assert.Equal("Falcon 9 Block 5 | SDA Tranche 1 Transport Layer A", latest.Name);
            Assert.Equal(new DateTimeOffset(2026, 10, 10, 7, 39, 44, TimeSpan.Zero), latest.Net);
            Assert.Equal("Success", latest.Status);
        }

        [Fact]
        public void BuildStats_ReturnsTheDifferenceFromLl2sCount()
        {
            // The fixture is trimmed to 4 launches, but LL2 said 239.
            var built = Launches.BuildStats(2026, new[] { PreviousPage() }, Now);

            Assert.Equal(239, built.Ll2Count);
            Assert.Equal(235, built.Difference);
        }

        [Fact]
        public void BuildStats_LaunchOnTwoPages_IsCountedOnce()
        {
            // A launch landing mid-paging pushes the last one on page 1 onto page 2 as well.
            var page1 = new Ll2LaunchPage(new[] { Launch("a", 3, 121, "SpaceX", 10), Launch("b", 3, 121, "SpaceX", 9) }, "next", 3);
            var page2 = new Ll2LaunchPage(new[] { Launch("b", 3, 121, "SpaceX", 9), Launch("c", 4, 88, "China Aerospace Science and Technology Corporation", 8) }, null, 3);

            var built = Launches.BuildStats(2026, new[] { page1, page2 }, Now);

            Assert.Equal(3, built.Stats.Total);
            Assert.Equal(0, built.Difference);
            Assert.Equal(new[] { new ProviderCount("SpaceX", 2), new ProviderCount("CASC", 1) }, built.Stats.Providers);
        }

        [Fact]
        public void BuildStats_CountsPartialFailuresAndInFlight()
        {
            var page = new Ll2LaunchPage(new[]
            {
                Launch("a", Launches.StatusSuccess, 1, "A", 1),
                Launch("b", Launches.StatusFailure, 1, "A", 2),
                Launch("c", Launches.StatusPartialFailure, 1, "A", 3),
                Launch("d", Launches.StatusInFlight, 1, "A", 4),
            }, null, 4);

            var stats = Launches.BuildStats(2026, new[] { page }, Now).Stats;

            Assert.Equal((4, 1, 1, 1, 1), (stats.Total, stats.Successes, stats.Failures, stats.PartialFailures, stats.InFlight));
        }

        [Fact]
        public void BuildStats_TiesSortByName()
        {
            var page = new Ll2LaunchPage(new[]
            {
                Launch("a", 3, 193, "Russian Space Forces", 1),
                Launch("b", 3, 63, "Russian Federal Space Agency (ROSCOSMOS)", 2),
                Launch("c", 3, 1040, "CAS Space", 3),
            }, null, 3);

            var names = Launches.BuildStats(2026, new[] { page }, Now).Stats.Providers.Select(p => p.Name);

            Assert.Equal(new[] { "CAS Space", "Roscosmos", "Russian Space Forces" }, names);
        }

        [Theory]
        [InlineData(88, "China Aerospace Science and Technology Corporation", "CASC")]
        [InlineData(63, "Russian Federal Space Agency (ROSCOSMOS)", "Roscosmos")]
        [InlineData(31, "Indian Space Research Organization", "ISRO")]
        [InlineData(44, "National Aeronautics and Space Administration", "NASA")]
        [InlineData(96, "Khrunichev State Research and Production Space Center", "Khrunichev")]
        [InlineData(257, "Northrop Grumman Space Systems", "Northrop Grumman")]
        [InlineData(121, "SpaceX", "SpaceX")]
        [InlineData(147, "Rocket Lab", "Rocket Lab")]
        [InlineData(null, null, "Unknown")]
        public void ProviderName_UsesShortNamesForAFewAgencies(int? id, string? ll2Name, string expected)
        {
            Assert.Equal(expected, Launches.ProviderName(id, ll2Name));
        }

        [Fact]
        public void BuildStats_NoLaunchesYet_IsZeroNotAFailure()
        {
            var empty = LaunchesParser.ParsePage(@"{""count"": 0, ""next"": null, ""results"": []}")!;

            var stats = Launches.BuildStats(2027, new[] { empty }, Now).Stats;

            Assert.Equal(2027, stats.Year);
            Assert.Equal(0, stats.Total);
            Assert.Empty(stats.Providers);
            Assert.Null(stats.Latest);
        }

        [Fact]
        public void YearUrl_FiltersToOrbitalAttemptsInThatYear()
        {
            Assert.Equal(
                "https://ll.thespacedevs.com/2.3.0/launches/previous/?include_suborbital=false&net__gte=2026-01-01T00:00:00Z&status__ids=3,4,6,7&limit=100&mode=normal",
                Launches.YearUrl(2026));
        }

        // ---- Upcoming ----

        [Fact]
        public void ParseUpcoming_RealPage_KeepsPrecisionStatusAndProbability()
        {
            var upcoming = Launches.ParseUpcoming(Fixture("ll2-launches-upcoming.json"))!;

            Assert.Equal(5, upcoming.Count);

            var next = upcoming[0];
            Assert.Equal("Falcon 9 Block 5 | Starlink Group 15-25", next.Name);
            Assert.Equal(new DateTimeOffset(2026, 10, 13, 0, 11, 47, TimeSpan.Zero), next.Net);
            Assert.Equal("SEC", next.Precision);
            Assert.Equal(Launches.StatusGo, next.StatusId);
            Assert.Equal("Go", next.StatusAbbrev);
            Assert.Null(next.Probability);
            Assert.Equal("SpaceX", next.Provider);
            Assert.Equal("Vandenberg SFB, CA, USA", next.Location);

            Assert.Equal(99, upcoming[1].Probability);

            // Far-off launches have a placeholder time, flagged by a coarse precision and TBD status.
            var vulcan = upcoming[4];
            Assert.Equal("DAY", vulcan.Precision);
            Assert.Equal(2, vulcan.StatusId);
            Assert.Equal("TBD", vulcan.StatusAbbrev);
            Assert.Equal("United Launch Alliance", vulcan.Provider);
        }

        [Fact]
        public void ParseUpcoming_NotALaunchesPage_ReturnsNull()
        {
            Assert.Null(Launches.ParseUpcoming(@"{""detail"": ""Request was throttled.""}"));
        }

        // ---- When to re-count the year ----

        [Fact]
        public void ShouldRefreshStats_NoPreviousSnapshot_Yes()
        {
            Assert.True(Launches.ShouldRefreshStats(null, Now));
            Assert.True(Launches.ShouldRefreshStats(Snapshot(null, Upcoming(Now.AddDays(1))), Now));
        }

        [Fact]
        public void ShouldRefreshStats_RecentAndNothingFlew_No()
        {
            Assert.False(Launches.ShouldRefreshStats(Snapshot(Stats(), Upcoming(Now.AddDays(1))), Now));
            Assert.False(Launches.ShouldRefreshStats(Snapshot(Stats()), Now));
        }

        [Fact]
        public void ShouldRefreshStats_SixHoursOld_Yes()
        {
            Assert.False(Launches.ShouldRefreshStats(Snapshot(Stats(updated: Now.AddHours(-6).AddMinutes(1)), Upcoming(Now.AddDays(1))), Now));
            Assert.True(Launches.ShouldRefreshStats(Snapshot(Stats(updated: Now.AddHours(-6)), Upcoming(Now.AddDays(1))), Now));
        }

        [Fact]
        public void ShouldRefreshStats_NextLaunchTimeHasPassed_Yes()
        {
            Assert.True(Launches.ShouldRefreshStats(Snapshot(Stats(), Upcoming(Now.AddMinutes(-5))), Now));
        }

        [Fact]
        public void ShouldRefreshStats_NewYear_Yes()
        {
            var newYear = new DateTimeOffset(2027, 1, 1, 0, 45, 0, TimeSpan.Zero);
            var lastYear = Snapshot(Stats(2026, newYear.AddHours(-1)), Upcoming(newYear.AddDays(1)));

            Assert.True(Launches.ShouldRefreshStats(lastYear, newYear));
        }

        // ---- Combining fresh and kept parts ----

        [Fact]
        public void Combine_BothFresh_UsesBoth()
        {
            var fresh = Stats(updated: Now);
            var refresh = Launches.Combine(Snapshot(Stats()), fresh, new[] { Upcoming(Now.AddDays(2)) }, Now)!;

            Assert.Same(fresh, refresh.Snapshot.Stats);
            Assert.Equal(Now.AddDays(2), refresh.Snapshot.Upcoming.Single().Net);
            Assert.Equal(Now, refresh.Snapshot.Updated);
            Assert.Equal((Launches.Fresh, Launches.Fresh), (refresh.StatsFrom, refresh.UpcomingFrom));
        }

        [Fact]
        public void Combine_NoFreshStats_KeepsPreviousStats()
        {
            var previous = Snapshot(Stats(), Upcoming(Now.AddDays(1)));
            var refresh = Launches.Combine(previous, null, new[] { Upcoming(Now.AddDays(2)) }, Now)!;

            Assert.Same(previous.Stats, refresh.Snapshot.Stats);
            Assert.Equal((Launches.Previous, Launches.Fresh), (refresh.StatsFrom, refresh.UpcomingFrom));
        }

        [Fact]
        public void Combine_NoFreshUpcoming_KeepsPreviousUpcoming()
        {
            var previous = Snapshot(Stats(), Upcoming(Now.AddDays(1)));
            var refresh = Launches.Combine(previous, Stats(updated: Now), null, Now)!;

            Assert.Same(previous.Upcoming, refresh.Snapshot.Upcoming);
            Assert.Equal((Launches.Fresh, Launches.Previous), (refresh.StatsFrom, refresh.UpcomingFrom));
        }

        [Fact]
        public void Combine_NothingFresh_ReturnsNull()
        {
            Assert.Null(Launches.Combine(Snapshot(Stats(), Upcoming(Now.AddDays(1))), null, null, Now));
        }

        // ---- Storage ----

        [Fact]
        public void SerializeParse_RoundTrips()
        {
            var stats = Launches.BuildStats(2026, new[] { PreviousPage() }, Now).Stats;
            var upcoming = Launches.ParseUpcoming(Fixture("ll2-launches-upcoming.json"))!;
            var snapshot = new LaunchesSnapshot(stats, upcoming, Now, Launches.Source);

            var back = Launches.Parse(Launches.Serialize(snapshot))!;

            Assert.Equal(stats.Total, back.Stats!.Total);
            Assert.Equal(stats.Providers, back.Stats.Providers);
            Assert.Equal(stats.Latest, back.Stats.Latest);
            Assert.Equal(upcoming, back.Upcoming);
            Assert.Equal(Now, back.Updated);
            Assert.Equal(Launches.Source, back.Source);
        }

        [Fact]
        public void Serialize_UsesCamelCase()
        {
            var json = Launches.Serialize(new LaunchesSnapshot(Stats(), new[] { Upcoming(Now) }, Now, Launches.Source));

            Assert.Contains(@"""stats"":{""year"":2026", json);
            Assert.Contains(@"""statusId"":1", json);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("not json")]
        [InlineData("{}")]
        public void Parse_NotASnapshot_ReturnsNull(string? json)
        {
            Assert.Null(Launches.Parse(json));
        }

        [Fact]
        public void IsTooOld_AfterSevenDays()
        {
            var snapshot = new LaunchesSnapshot(null, Array.Empty<UpcomingLaunch>(), Now, Launches.Source);

            Assert.False(Launches.IsTooOld(snapshot, Now.AddDays(7)));
            Assert.True(Launches.IsTooOld(snapshot, Now.AddDays(7).AddMinutes(1)));
        }
    }
}
