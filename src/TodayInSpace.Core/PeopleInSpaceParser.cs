using System.Text.Json;

namespace TodayInSpace.Core
{
    // One person in space, as LL2 describes them. AgencyAbbrev is null when LL2 has no agency.
    public record Astronaut(int Id, string Name, string? AgencyAbbrev, IReadOnlyList<Nationality> Nationalities);

    // Code is an upper-case ISO 3166 alpha-2 code, used to pick the flag image.
    public record Nationality(string Code, string Country);

    // Parsing for The Space Devs Launch Library 2 `astronauts/?in_space=true` feed.
    public static class PeopleInSpaceParser
    {
        // LL2 lists Starman (the mannequin in SpaceX's Roadster) as "in space" with this astronaut type.
        private const string NonHumanType = "Non-Human";

        // Number of people in space right now. Returns null if the JSON isn't the shape we expect,
        // so a broken response is never shown as "0 people".
        public static int? ParseCount(string? json) => ParsePeople(json)?.Count;

        // Everyone in space right now. Null if the JSON isn't the shape we expect.
        public static IReadOnlyList<Astronaut>? ParsePeople(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                using var doc = JsonDocument.Parse(json);
                return HumanEntries(doc.RootElement)?.Select(ToAstronaut).ToList();
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // The astronaut objects that are people. Null when there's no "results" array.
        private static IEnumerable<JsonElement>? HumanEntries(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array)
                return null;

            return results.EnumerateArray()
                .Where(a => a.ValueKind == JsonValueKind.Object && !IsNonHuman(a));
        }

        private static Astronaut ToAstronaut(JsonElement a)
        {
            int id = a.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out int n) ? n : 0;
            string name = StringProp(a, "name") ?? "";
            string? agency = a.TryGetProperty("agency", out var ag) && ag.ValueKind == JsonValueKind.Object
                ? StringProp(ag, "abbrev")
                : null;
            return new Astronaut(id, name.Trim(), string.IsNullOrWhiteSpace(agency) ? null : agency.Trim(), Nationalities(a));
        }

        // LL2 gives nationality as an array, so dual nationals get two flags. Entries without a usable
        // two-letter code (Starman's is "??") are dropped, since there'd be no flag to show.
        private static List<Nationality> Nationalities(JsonElement a)
        {
            var list = new List<Nationality>();
            if (!a.TryGetProperty("nationality", out var nats) || nats.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var nat in nats.EnumerateArray())
            {
                if (nat.ValueKind != JsonValueKind.Object)
                    continue;
                string? code = StringProp(nat, "alpha_2_code")?.Trim();
                if (code is not { Length: 2 } || !code.All(char.IsAsciiLetter))
                    continue;
                code = code.ToUpperInvariant();
                string country = StringProp(nat, "name")?.Trim() is { Length: > 0 } c ? c : code;
                list.Add(new Nationality(code, country));
            }
            return list;
        }

        private static string? StringProp(JsonElement obj, string name) =>
            obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

        private static bool IsNonHuman(JsonElement astronaut) =>
            astronaut.TryGetProperty("type", out var type)
            && type.ValueKind == JsonValueKind.Object
            && type.TryGetProperty("name", out var name)
            && name.ValueKind == JsonValueKind.String
            && string.Equals(name.GetString(), NonHumanType, StringComparison.OrdinalIgnoreCase);
    }
}
