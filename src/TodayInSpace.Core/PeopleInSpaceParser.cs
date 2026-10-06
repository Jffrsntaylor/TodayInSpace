using System.Text.Json;

namespace TodayInSpace.Core
{
    // Parsing for The Space Devs Launch Library 2 `astronauts/?in_space=true` feed.
    public static class PeopleInSpaceParser
    {
        // LL2 lists Starman (the mannequin in SpaceX's Roadster) as "in space" with this astronaut type.
        private const string NonHumanType = "Non-Human";

        // Number of people in space right now. Returns null if the JSON isn't the shape we expect,
        // so a broken response is never shown as "0 people".
        public static int? ParseCount(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                using var doc = JsonDocument.Parse(json);
                var humans = HumanEntries(doc.RootElement);
                return humans?.Count();
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // The astronaut objects that are people. Kept separate from the count so a later version can
        // read names and nationalities from the same entries. Null when there's no "results" array.
        private static IEnumerable<JsonElement>? HumanEntries(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array)
                return null;

            return results.EnumerateArray()
                .Where(a => a.ValueKind == JsonValueKind.Object && !IsNonHuman(a));
        }

        private static bool IsNonHuman(JsonElement astronaut) =>
            astronaut.TryGetProperty("type", out var type)
            && type.ValueKind == JsonValueKind.Object
            && type.TryGetProperty("name", out var name)
            && name.ValueKind == JsonValueKind.String
            && string.Equals(name.GetString(), NonHumanType, StringComparison.OrdinalIgnoreCase);
    }
}
