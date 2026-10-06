using TodayInSpace.Core;

namespace TodayInSpace.Web.Tests
{
    public class PeopleInSpaceParserTests
    {
        // Trimmed-down shape of the real LL2 2.3.0 astronauts response.
        private static string Feed(params string[] astronauts) =>
            @"{""count"": " + astronauts.Length + @", ""next"": null, ""results"": [" + string.Join(",", astronauts) + "]}";

        private static string Astronaut(string name, string type) =>
            @"{""id"": 1, ""name"": """ + name + @""", ""type"": {""id"": 2, ""name"": """ + type + @"""}, ""in_space"": true}";

        [Fact]
        public void ParseCount_CountsEveryone()
        {
            var json = Feed(
                Astronaut("Jessica Meir", "Government"),
                Astronaut("Sophie Adenot", "Government"),
                Astronaut("Zhang Zhiyuan", "Government"));

            Assert.Equal(3, PeopleInSpaceParser.ParseCount(json));
        }

        [Fact]
        public void ParseCount_ExcludesNonHumans()
        {
            var json = Feed(
                Astronaut("Jessica Meir", "Government"),
                Astronaut("Starman", "Non-Human"),
                Astronaut("Anil Menon", "Government"));

            Assert.Equal(2, PeopleInSpaceParser.ParseCount(json));
        }

        [Fact]
        public void ParseCount_CountsPeopleWithNoType()
        {
            var json = Feed(@"{""id"": 1, ""name"": ""Someone""}");

            Assert.Equal(1, PeopleInSpaceParser.ParseCount(json));
        }

        [Fact]
        public void ParseCount_EmptyList_IsZero()
        {
            Assert.Equal(0, PeopleInSpaceParser.ParseCount(Feed()));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not json")]
        [InlineData(@"{""results"": [")]
        [InlineData("[]")]
        [InlineData(@"{""count"": 3}")]
        [InlineData(@"{""results"": null}")]
        [InlineData(@"{""detail"": ""Request was throttled.""}")]   // LL2's rate-limit response
        public void ParseCount_ReturnsNull_WhenMalformed(string? json)
        {
            Assert.Null(PeopleInSpaceParser.ParseCount(json));
        }
    }
}
