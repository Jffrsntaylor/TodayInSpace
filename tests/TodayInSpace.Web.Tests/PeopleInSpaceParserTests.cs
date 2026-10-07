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

        // Trimmed from the real LL2 response on 2026-10-07.
        private const string RealFeed = @"{""count"": 3, ""next"": null, ""results"": [
            {""id"": 573, ""name"": ""Jessica Meir"", ""type"": {""id"": 2, ""name"": ""Government""},
             ""agency"": {""id"": 44, ""name"": ""National Aeronautics and Space Administration"", ""abbrev"": ""NASA""},
             ""nationality"": [{""id"": 1, ""name"": ""United States of America"", ""alpha_2_code"": ""US"", ""alpha_3_code"": ""USA"", ""nationality_name"": ""American""}]},
            {""id"": 638, ""name"": ""Starman"", ""type"": {""id"": 6, ""name"": ""Non-Human""},
             ""agency"": {""id"": 121, ""name"": ""SpaceX"", ""abbrev"": ""SpX""},
             ""nationality"": [{""id"": 99, ""name"": ""Unknown"", ""alpha_2_code"": ""??"", ""alpha_3_code"": ""???"", ""nationality_name"": ""Earthling""}]},
            {""id"": 747, ""name"": ""Sophie Adenot"", ""type"": {""id"": 2, ""name"": ""Government""},
             ""agency"": {""id"": 27, ""name"": ""European Space Agency"", ""abbrev"": ""ESA""},
             ""nationality"": [{""id"": 7, ""name"": ""France"", ""alpha_2_code"": ""FR"", ""alpha_3_code"": ""FRA"", ""nationality_name"": ""French""}]}
        ]}";

        private const string NasaAgency = @"""agency"": {""abbrev"": ""NASA""}, ";

        private static string Person(string nationality, string agency = NasaAgency) =>
            @"{""id"": 5, ""name"": ""Test Person"", ""type"": {""name"": ""Government""}, " + agency + @"""nationality"": [" + nationality + "]}";

        private static string Nat(string code, string name) =>
            @"{""name"": """ + name + @""", ""alpha_2_code"": """ + code + @"""}";

        [Fact]
        public void ParsePeople_ReadsNameAgencyAndNationality()
        {
            var people = PeopleInSpaceParser.ParsePeople(RealFeed)!;

            Assert.Equal(2, people.Count);
            var meir = people[0];
            Assert.Equal(573, meir.Id);
            Assert.Equal("Jessica Meir", meir.Name);
            Assert.Equal("NASA", meir.AgencyAbbrev);
            Assert.Equal(new Nationality("US", "United States of America"), Assert.Single(meir.Nationalities));
            Assert.Equal("Sophie Adenot", people[1].Name);
        }

        [Fact]
        public void ParsePeople_ExcludesNonHumans()
        {
            var people = PeopleInSpaceParser.ParsePeople(RealFeed)!;

            Assert.DoesNotContain(people, p => p.Name == "Starman");
        }

        [Fact]
        public void ParsePeople_DualNationality_GivesTwoFlags()
        {
            var json = Feed(Person(Nat("US", "United States of America") + "," + Nat("it", "Italy")));

            var nats = Assert.Single(PeopleInSpaceParser.ParsePeople(json)!).Nationalities;

            Assert.Equal(["US", "IT"], nats.Select(n => n.Code));
        }

        [Theory]
        [InlineData("??")]
        [InlineData("")]
        [InlineData("USA")]
        [InlineData("U1")]
        public void ParsePeople_CodeThatIsNotTwoLetters_GivesNoFlag(string code)
        {
            var json = Feed(Person(Nat(code, "Somewhere")));

            Assert.Empty(Assert.Single(PeopleInSpaceParser.ParsePeople(json)!).Nationalities);
        }

        [Theory]
        [InlineData("")]
        [InlineData(@"""agency"": null, ")]
        [InlineData(@"""agency"": {""abbrev"": """"}, ")]
        public void ParsePeople_MissingAgency_IsNull(string agency)
        {
            var json = Feed(Person(Nat("US", "United States of America"), agency));

            Assert.Null(Assert.Single(PeopleInSpaceParser.ParsePeople(json)!).AgencyAbbrev);
        }

        [Fact]
        public void ParsePeople_EmptyList_IsEmpty()
        {
            Assert.Empty(PeopleInSpaceParser.ParsePeople(Feed())!);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("not json")]
        [InlineData(@"{""results"": null}")]
        [InlineData(@"{""detail"": ""Request was throttled.""}")]
        public void ParsePeople_ReturnsNull_WhenMalformed(string? json)
        {
            Assert.Null(PeopleInSpaceParser.ParsePeople(json));
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
