using TodayInSpace.Core;

namespace TodayInSpace.Web.Tests
{
    public class PeopleInSpaceMergeTests
    {
        private static Astronaut A(int id, string name, string? agency = "NASA") =>
            new(id, name, agency, [new Nationality("US", "United States of America")]);

        [Fact]
        public void Group_PersonInNoExpedition_IsInOrbit()
        {
            var stations = new Dictionary<int, string> { [1] = "ISS" };

            var people = PeopleInSpaceMerge.Group([A(1, "Jessica Meir"), A(2, "Luke Delaney")], stations);

            Assert.Equal("ISS", people.Single(p => p.Person.Id == 1).Station);
            Assert.Equal(PeopleInSpaceMerge.InOrbit, people.Single(p => p.Person.Id == 2).Station);
        }

        [Fact]
        public void Group_NoStationData_EveryoneInOrbit_AndNobodyDropped()
        {
            var people = PeopleInSpaceMerge.Group([A(1, "Jessica Meir"), A(2, "Zhu Yangzhu"), A(3, "Luke Delaney")], null);

            Assert.Equal(3, people.Count);
            Assert.All(people, p => Assert.Equal(PeopleInSpaceMerge.InOrbit, p.Station));
        }

        [Fact]
        public void Group_SortsIssThenTiangongThenOthersThenInOrbit_ThenByName()
        {
            var stations = new Dictionary<int, string>
            {
                [1] = "Tiangong", [2] = "ISS", [3] = "Zeta Station", [4] = "Axiom Station", [5] = "ISS",
            };
            Astronaut[] astronauts =
            [
                A(1, "Zhu Yangzhu"), A(2, "Sophie Adenot"), A(3, "Zed"), A(4, "Ana"), A(5, "Anil Menon"), A(6, "Luke Delaney"),
            ];

            var order = PeopleInSpaceMerge.Group(astronauts, stations).Select(p => p.Person.Name);

            Assert.Equal(["Anil Menon", "Sophie Adenot", "Zhu Yangzhu", "Ana", "Zed", "Luke Delaney"], order);
        }

        [Fact]
        public void Group_ShowsRoscosmosForRfsa_AndLeavesOtherAgenciesAlone()
        {
            var people = PeopleInSpaceMerge.Group([A(1, "Pyotr Dubrov", "RFSA"), A(2, "Sophie Adenot", "ESA"), A(3, "Nobody", null)], null);

            Assert.Equal("Roscosmos", people.Single(p => p.Person.Id == 1).Agency);
            Assert.Equal("ESA", people.Single(p => p.Person.Id == 2).Agency);
            Assert.Null(people.Single(p => p.Person.Id == 3).Agency);
        }
    }
}
