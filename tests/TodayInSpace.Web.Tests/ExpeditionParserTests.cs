using TodayInSpace.Core;

namespace TodayInSpace.Web.Tests
{
    public class ExpeditionParserTests
    {
        private static string Feed(params string[] expeditions) =>
            @"{""count"": " + expeditions.Length + @", ""results"": [" + string.Join(",", expeditions) + "]}";

        // crew pairs are (membership id, astronaut id), like LL2's crew[].id and crew[].astronaut.id.
        private static string Expedition(string station, string start, params (int CrewId, int AstronautId)[] crew) =>
            @"{""id"": 1, ""name"": ""Exp"", ""start"": """ + start + @""", ""end"": null,
               ""spacestation"": {""id"": 4, ""name"": """ + station + @"""},
               ""crew"": [" + string.Join(",", crew.Select(c =>
                   @"{""id"": " + c.CrewId + @", ""role"": {""role"": ""Flight Engineer""}, ""astronaut"": {""id"": " + c.AstronautId + "}}")) + "]}";

        [Fact]
        public void ParseStations_UsesAstronautId_NotCrewMembershipId()
        {
            var json = Feed(Expedition("International Space Station", "2026-07-26T07:02:00Z", (5279, 573)));

            var stations = ExpeditionParser.ParseStations(json)!;

            Assert.Equal("ISS", stations[573]);
            Assert.False(stations.ContainsKey(5279));
        }

        [Fact]
        public void ParseStations_ShortensStationNames()
        {
            var json = Feed(
                Expedition("International Space Station", "2026-07-26T07:02:00Z", (1, 573)),
                Expedition("Tiangong space station", "2026-05-24T18:45:00Z", (2, 764)),
                Expedition("Axiom Station", "2026-09-01T00:00:00Z", (3, 900)));

            var stations = ExpeditionParser.ParseStations(json)!;

            Assert.Equal("ISS", stations[573]);
            Assert.Equal("Tiangong", stations[764]);
            Assert.Equal("Axiom Station", stations[900]);
        }

        [Fact]
        public void ParseStations_InTwoExpeditions_LatestStartWins()
        {
            var json = Feed(
                Expedition("Tiangong space station", "2026-09-01T00:00:00Z", (2, 10)),
                Expedition("International Space Station", "2026-06-01T00:00:00Z", (1, 10)));

            Assert.Equal("Tiangong", ExpeditionParser.ParseStations(json)![10]);
        }

        [Fact]
        public void ParseStations_NoActiveExpeditions_IsEmpty()
        {
            Assert.Empty(ExpeditionParser.ParseStations(Feed())!);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not json")]
        [InlineData("[]")]
        [InlineData(@"{""results"": null}")]
        [InlineData(@"{""detail"": ""Request was throttled.""}")]
        public void ParseStations_ReturnsNull_WhenMalformed(string? json)
        {
            Assert.Null(ExpeditionParser.ParseStations(json));
        }
    }
}
