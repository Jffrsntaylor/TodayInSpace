using System.Text.Json;

namespace TodayInSpace.Web.Sky
{
    // Two-line element set for the ISS, propagated in the browser with satellite.js.
    public record IssTle(string Name, string Line1, string Line2);

    // Aurora probability points from NOAA's OVATION model: [longitude (-180..180), latitude, probability %].
    public record AuroraSnapshot(string? ObservationTime, string? ForecastTime, IReadOnlyList<int[]> Points);

    // Pure parsing logic for the Live Sky data feeds, kept separate from HTTP/caching so it can be unit tested.
    public static class SkyDataParser
    {
        // Parses CelesTrak's 3-line TLE format (name, line 1, line 2). Returns null if no valid pair is found.
        public static IssTle? ParseTle(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var lines = text.Split('\n')
                .Select(l => l.TrimEnd('\r', ' ', '\t'))
                .Where(l => l.Length > 0)
                .ToList();

            for (int i = 0; i < lines.Count - 1; i++)
            {
                if (IsTleLine(lines[i], '1') && IsTleLine(lines[i + 1], '2'))
                {
                    string name = i > 0 ? lines[i - 1].Trim() : "ISS";
                    return new IssTle(name, lines[i], lines[i + 1]);
                }
            }
            return null;
        }

        private static bool IsTleLine(string line, char number)
        {
            // Standard TLE lines are 69 characters and start with the line number and a space.
            return line.Length >= 69 && line[0] == number && line[1] == ' ';
        }

        // Parses NOAA's ovation_aurora_latest.json and keeps only points at or above minProbability,
        // converting longitude from 0..359 to -180..180 for the map. Returns null if the shape is unexpected.
        public static AuroraSnapshot? ParseOvation(string? json, int minProbability)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("coordinates", out var coords) ||
                    coords.ValueKind != JsonValueKind.Array)
                    return null;

                var points = new List<int[]>();
                foreach (var c in coords.EnumerateArray())
                {
                    if (c.ValueKind != JsonValueKind.Array || c.GetArrayLength() < 3)
                        continue;
                    if (!TryGetNumber(c[0], out double lon) ||
                        !TryGetNumber(c[1], out double lat) ||
                        !TryGetNumber(c[2], out double prob))
                        continue;

                    int p = (int)Math.Round(prob);
                    if (p < minProbability)
                        continue;

                    int lon180 = NormalizeLongitude((int)Math.Round(lon));
                    points.Add(new[] { lon180, (int)Math.Round(lat), p });
                }

                return new AuroraSnapshot(
                    GetString(root, "Observation Time"),
                    GetString(root, "Forecast Time"),
                    points);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // 0..359 -> -180..179
        public static int NormalizeLongitude(int lon)
        {
            int l = ((lon % 360) + 360) % 360;
            return l >= 180 ? l - 360 : l;
        }

        private static bool TryGetNumber(JsonElement el, out double value)
        {
            value = 0;
            return el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out value);
        }

        private static string? GetString(JsonElement el, string prop)
        {
            return el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
    }
}
