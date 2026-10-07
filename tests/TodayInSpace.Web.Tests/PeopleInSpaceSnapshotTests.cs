using TodayInSpace.Core;

namespace TodayInSpace.Web.Tests
{
    // The refresh rules the RefreshPeopleInSpace function relies on: when to write, and which station
    // labels to use.
    public class PeopleInSpaceSnapshotTests
    {
        private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 15, 0, TimeSpan.Zero);

        private const string Astronauts = @"{""results"": [
            {""id"": 573, ""name"": ""Jessica Meir"", ""type"": {""name"": ""Government""}, ""agency"": {""abbrev"": ""NASA""},
             ""nationality"": [{""name"": ""United States of America"", ""alpha_2_code"": ""US""}]},
            {""id"": 710, ""name"": ""Luke Delaney"", ""type"": {""name"": ""Government""}, ""agency"": {""abbrev"": ""NASA""},
             ""nationality"": [{""name"": ""United States of America"", ""alpha_2_code"": ""US""}]},
            {""id"": 900, ""name"": ""Wang Yaping"", ""type"": {""name"": ""Government""}, ""agency"": {""abbrev"": ""CMSA""},
             ""nationality"": [{""name"": ""China"", ""alpha_2_code"": ""CN""}]},
            {""id"": 638, ""name"": ""Starman"", ""type"": {""name"": ""Non-Human""}, ""nationality"": []}
        ]}";

        private const string Expeditions = @"{""results"": [
            {""start"": ""2026-07-26T07:02:00Z"", ""spacestation"": {""name"": ""International Space Station""},
             ""crew"": [{""id"": 5279, ""astronaut"": {""id"": 573}}]}
        ]}";

        // A snapshot from an earlier run. Delaney has since been placed on the ISS; someone who has
        // since landed is still listed.
        private static PeopleInSpace Previous() => new(3, Now.AddHours(-3), PeopleInSpaceSnapshot.Source, new[]
        {
            new PersonDto("Jessica Meir", "NASA", new[] { new FlagDto("US", "United States of America") }, "ISS"),
            new PersonDto("Luke Delaney", "NASA", new[] { new FlagDto("US", "United States of America") }, "ISS"),
            new PersonDto("Landed Person", "NASA", Array.Empty<FlagDto>(), "ISS"),
        });

        [Fact]
        public void Build_BothFeedsOk_UsesFreshStations()
        {
            var refresh = PeopleInSpaceSnapshot.Build(Astronauts, Expeditions, Previous(), Now)!;

            Assert.Equal(PeopleInSpaceSnapshot.StationsFresh, refresh.Stations);
            Assert.Equal(3, refresh.Snapshot.Count);
            Assert.Equal(Now, refresh.Snapshot.Updated);
            Assert.Equal("ISS", refresh.Snapshot.People.Single(p => p.Name == "Jessica Meir").Station);
            // Fresh data wins over the previous run, even where it knows less.
            Assert.Equal("In orbit", refresh.Snapshot.People.Single(p => p.Name == "Luke Delaney").Station);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("<html>Too Many Requests</html>")]
        [InlineData(@"{""detail"": ""Request was throttled.""}")]
        public void Build_AstronautsUnusable_ReturnsNull_SoNothingIsOverwritten(string? astronauts)
        {
            Assert.Null(PeopleInSpaceSnapshot.Build(astronauts, Expeditions, Previous(), Now));
        }

        [Fact]
        public void Build_ExpeditionsFailed_KeepsPreviousStations_ForPeopleStillUp()
        {
            var refresh = PeopleInSpaceSnapshot.Build(Astronauts, null, Previous(), Now)!;

            Assert.Equal(PeopleInSpaceSnapshot.StationsPrevious, refresh.Stations);
            Assert.Equal(3, refresh.Snapshot.Count);
            Assert.Equal("ISS", refresh.Snapshot.People.Single(p => p.Name == "Jessica Meir").Station);
            Assert.Equal("ISS", refresh.Snapshot.People.Single(p => p.Name == "Luke Delaney").Station);
            // New since the last run, so there's no label to keep.
            Assert.Equal("In orbit", refresh.Snapshot.People.Single(p => p.Name == "Wang Yaping").Station);
            // Only people in the current astronaut list are shown.
            Assert.DoesNotContain(refresh.Snapshot.People, p => p.Name == "Landed Person");
        }

        [Fact]
        public void Build_ExpeditionsFailed_NoPrevious_EveryoneInOrbit()
        {
            var refresh = PeopleInSpaceSnapshot.Build(Astronauts, "not json", null, Now)!;

            Assert.Equal(PeopleInSpaceSnapshot.StationsNone, refresh.Stations);
            Assert.All(refresh.Snapshot.People, p => Assert.Equal("In orbit", p.Station));
        }

        [Fact]
        public void SerializeThenParse_RoundTrips_WithTheShapeTheBrowserUses()
        {
            var snapshot = PeopleInSpaceSnapshot.Build(Astronauts, Expeditions, null, Now)!.Snapshot;
            string json = PeopleInSpaceSnapshot.Serialize(snapshot);

            // sky-core.js reads these camelCase names.
            Assert.Contains(@"""count"":3", json);
            Assert.Contains(@"""updated"":", json);
            Assert.Contains(@"""people"":[", json);
            Assert.Contains(@"""station"":""ISS""", json);
            Assert.Contains(@"""flags"":[{""code"":""US""", json);

            var parsed = PeopleInSpaceSnapshot.Parse(json)!;
            Assert.Equal(snapshot.Count, parsed.Count);
            Assert.Equal(snapshot.Updated, parsed.Updated);
            Assert.Equal(snapshot.People.Select(p => (p.Name, p.Station)), parsed.People.Select(p => (p.Name, p.Station)));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not json")]
        [InlineData("{}")]
        public void Parse_Unusable_ReturnsNull(string? json)
        {
            Assert.Null(PeopleInSpaceSnapshot.Parse(json));
        }
    }
}
