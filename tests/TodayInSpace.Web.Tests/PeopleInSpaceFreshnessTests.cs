using TodayInSpace.Core;

namespace TodayInSpace.Web.Tests
{
    // When a stored people snapshot is too old to show, and what the daily digest copies from it.
    public class PeopleInSpaceFreshnessTests
    {
        private static readonly DateTimeOffset Updated = new(2026, 10, 7, 12, 15, 0, TimeSpan.Zero);

        private static string Json(string updated) => @"{""count"":2," + updated + @"""source"":""The Space Devs"",""people"":[
            {""name"":""Jessica Meir"",""agency"":""NASA"",""flags"":[{""code"":""US"",""country"":""United States of America""}],""station"":""ISS""},
            {""name"":""Luke Delaney"",""agency"":""NASA"",""flags"":[],""station"":""In orbit""}
        ]}";

        private static readonly string Stored = Json(@"""updated"":""2026-10-07T12:15:00+00:00"",");

        private static PeopleInSpace Snapshot() => PeopleInSpaceSnapshot.Parse(Stored)!;

        [Fact]
        public void IsTooOld_Fresh_IsFalse()
        {
            Assert.False(PeopleInSpaceSnapshot.IsTooOld(Snapshot(), Updated.AddHours(3)));
        }

        [Fact]
        public void IsTooOld_ExactlySevenDays_IsFalse()
        {
            Assert.False(PeopleInSpaceSnapshot.IsTooOld(Snapshot(), Updated.AddDays(7)));
        }

        [Fact]
        public void IsTooOld_OlderThanSevenDays_IsTrue()
        {
            Assert.True(PeopleInSpaceSnapshot.IsTooOld(Snapshot(), Updated.AddDays(7).AddMinutes(1)));
        }

        [Fact]
        public void IsTooOld_MissingUpdated_IsTrue()
        {
            var snapshot = PeopleInSpaceSnapshot.Parse(Json(""))!;
            Assert.True(PeopleInSpaceSnapshot.IsTooOld(snapshot, Updated));
        }

        [Fact]
        public void ForDigest_ValidSnapshot_CopiesCountUpdatedAndPeople()
        {
            var people = PeopleInSpaceSnapshot.ForDigest(Stored, Updated.AddHours(6))!;

            Assert.Equal(2, people.Count);
            Assert.Equal(Updated, people.Updated);
            Assert.Equal(new[] { "Jessica Meir", "Luke Delaney" }, people.People.Select(p => p.Name));
            Assert.Equal("ISS", people.People[0].Station);
            Assert.Equal("US", people.People[0].Flags.Single().Code);
        }

        [Fact]
        public void ForDigest_Stale_IsLeftOut()
        {
            Assert.Null(PeopleInSpaceSnapshot.ForDigest(Stored, Updated.AddDays(8)));
        }

        [Fact]
        public void ForDigest_MissingUpdated_IsLeftOut()
        {
            Assert.Null(PeopleInSpaceSnapshot.ForDigest(Json(""), Updated));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not json")]
        [InlineData(@"{""count"":2}")]
        public void ForDigest_MissingOrMalformed_IsLeftOut(string? json)
        {
            Assert.Null(PeopleInSpaceSnapshot.ForDigest(json, Updated));
        }
    }
}
