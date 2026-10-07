using System.Text.Json;

namespace TodayInSpace.Core
{
    // Everyone in space right now, for the "People in Space" section. The function app writes this to
    // storage and the web app serves it as-is, so the blob and /api/sky/people have the same shape.
    public record PeopleInSpace(int Count, DateTimeOffset Updated, string Source, IReadOnlyList<PersonDto> People);

    public record PersonDto(string Name, string? Agency, IReadOnlyList<FlagDto> Flags, string Station);

    public record FlagDto(string Code, string Country);

    // A fresh snapshot, plus where its station labels came from, for the run's log line.
    public record PeopleRefresh(PeopleInSpace Snapshot, string Stations);

    // Builds the people snapshot from the two LL2 feeds. No I/O, so the refresh rules are easy to test.
    public static class PeopleInSpaceSnapshot
    {
        public const string Source = "The Space Devs";

        // In the digests container. The "sky/" prefix keeps it apart from the dated digests, and the
        // web app's archive only accepts YYYY-MM-DD names, so it can't be read through that route.
        public const string BlobName = "sky/people.json";

        // Values for PeopleRefresh.Stations.
        public const string StationsFresh = "fresh";        // from today's expeditions feed
        public const string StationsPrevious = "previous";  // expeditions failed; kept the last known labels
        public const string StationsNone = "none";          // expeditions failed and nothing to fall back on

        // Same casing the browser already gets from ASP.NET Core, so the stored JSON can be served unchanged.
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        // Null when the astronaut list didn't parse, meaning the stored snapshot should be left alone.
        // The astronaut list decides who and how many; the expeditions only add station labels.
        public static PeopleRefresh? Build(string? astronautsJson, string? expeditionsJson, PeopleInSpace? previous, DateTimeOffset now)
        {
            var astronauts = PeopleInSpaceParser.ParsePeople(astronautsJson);
            if (astronauts == null)
                return null;

            var stations = ExpeditionParser.ParseStations(expeditionsJson);
            string from = StationsFresh;
            if (stations == null)
            {
                // A failed expeditions call shouldn't move a whole crew to "In orbit" for three hours.
                stations = StationsFromPrevious(astronauts, previous);
                from = stations.Count > 0 ? StationsPrevious : StationsNone;
            }

            var people = PeopleInSpaceMerge.Group(astronauts, stations)
                .Select(p => new PersonDto(
                    p.Person.Name,
                    p.Agency,
                    p.Person.Nationalities.Select(n => new FlagDto(n.Code, n.Country)).ToList(),
                    p.Station))
                .ToList();
            return new PeopleRefresh(new PeopleInSpace(astronauts.Count, now, Source, people), from);
        }

        // Station labels from the last snapshot for people who are still up there. Matched by name,
        // because the stored snapshot doesn't carry LL2 ids. Anyone new is left out, so they show as
        // "In orbit" rather than a guessed station.
        public static IReadOnlyDictionary<int, string> StationsFromPrevious(IEnumerable<Astronaut> astronauts, PeopleInSpace? previous)
        {
            var result = new Dictionary<int, string>();
            if (previous == null)
                return result;

            var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in previous.People)
                if (p.Station != PeopleInSpaceMerge.InOrbit)
                    known[p.Name] = p.Station;

            foreach (var a in astronauts)
                if (known.TryGetValue(a.Name, out var station))
                    result[a.Id] = station;
            return result;
        }

        public static string Serialize(PeopleInSpace snapshot) => JsonSerializer.Serialize(snapshot, Json);

        // Null if the JSON is missing or isn't a snapshot we wrote.
        public static PeopleInSpace? Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;
            try
            {
                var snapshot = JsonSerializer.Deserialize<PeopleInSpace>(json, Json);
                return snapshot?.People == null ? null : snapshot;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
