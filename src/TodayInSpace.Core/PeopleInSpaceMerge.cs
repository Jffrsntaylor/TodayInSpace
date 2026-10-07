namespace TodayInSpace.Core
{
    // An astronaut with the station label we'll show them under and the agency name to display.
    public record PersonOnStation(Astronaut Person, string? Agency, string Station);

    // Joins the astronaut list with the expedition data and puts everyone in display order.
    public static class PeopleInSpaceMerge
    {
        // For people the expedition data doesn't place yet. LL2's expeditions lag behind launches and
        // undockings by a day or more, and "in orbit" is always true, so we never guess a station.
        public const string InOrbit = "In orbit";

        // Abbreviations LL2 uses that most readers wouldn't recognise. Everything else is shown as-is.
        private static readonly Dictionary<string, string> AgencyNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ["RFSA"] = "Roscosmos",
        };

        // stations: astronaut id -> station label, or null if the expeditions feed couldn't be read.
        // Sorted ISS, then Tiangong, then other stations A–Z, then "In orbit"; by name within each group.
        public static IReadOnlyList<PersonOnStation> Group(IEnumerable<Astronaut> people, IReadOnlyDictionary<int, string>? stations)
        {
            return people
                .Select(p => new PersonOnStation(
                    p,
                    AgencyDisplay(p.AgencyAbbrev),
                    stations != null && stations.TryGetValue(p.Id, out var s) ? s : InOrbit))
                .OrderBy(p => GroupRank(p.Station))
                .ThenBy(p => p.Station, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.Person.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static string? AgencyDisplay(string? abbrev) =>
            abbrev != null && AgencyNames.TryGetValue(abbrev, out var name) ? name : abbrev;

        private static int GroupRank(string station) => station switch
        {
            "ISS" => 0,
            "Tiangong" => 1,
            InOrbit => 3,
            _ => 2,
        };
    }
}
