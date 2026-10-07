using System.Globalization;
using System.Text.Json;

namespace TodayInSpace.Core
{
    // Parsing for LL2's `expeditions/?is_active=true&mode=detailed` feed. The astronaut list has no
    // station field, so this is the only way to tell who is on which station.
    public static class ExpeditionParser
    {
        // Astronaut id -> short station label ("ISS", "Tiangong", or LL2's name for anything else).
        // Null if the JSON isn't the shape we expect.
        public static IReadOnlyDictionary<int, string>? ParseStations(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("results", out var results)
                    || results.ValueKind != JsonValueKind.Array)
                    return null;

                // During a handover someone can briefly be in two active expeditions. The one that
                // started last is where they are now.
                var best = new Dictionary<int, (DateTimeOffset Start, string Station)>();
                foreach (var exp in results.EnumerateArray())
                {
                    if (exp.ValueKind != JsonValueKind.Object)
                        continue;
                    string? station = StationName(exp);
                    if (station == null || !exp.TryGetProperty("crew", out var crew) || crew.ValueKind != JsonValueKind.Array)
                        continue;
                    var start = Start(exp);

                    foreach (var member in crew.EnumerateArray())
                    {
                        // crew[].id is the crew membership id, not the astronaut, so read astronaut.id.
                        if (member.ValueKind != JsonValueKind.Object
                            || !member.TryGetProperty("astronaut", out var astro)
                            || astro.ValueKind != JsonValueKind.Object
                            || !astro.TryGetProperty("id", out var idEl)
                            || !idEl.TryGetInt32(out int id))
                            continue;
                        if (!best.TryGetValue(id, out var current) || start > current.Start)
                            best[id] = (start, ShortLabel(station));
                    }
                }
                return best.ToDictionary(kv => kv.Key, kv => kv.Value.Station);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public static string ShortLabel(string stationName)
        {
            if (string.Equals(stationName, "International Space Station", StringComparison.OrdinalIgnoreCase))
                return "ISS";
            if (stationName.Contains("Tiangong", StringComparison.OrdinalIgnoreCase))
                return "Tiangong";
            return stationName;
        }

        private static string? StationName(JsonElement exp) =>
            exp.TryGetProperty("spacestation", out var ss) && ss.ValueKind == JsonValueKind.Object
            && ss.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(n.GetString())
                ? n.GetString()!.Trim()
                : null;

        private static DateTimeOffset Start(JsonElement exp) =>
            exp.TryGetProperty("start", out var s) && s.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(s.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
                ? t
                : DateTimeOffset.MinValue;
    }
}
