using System.Globalization;
using System.Text.Json;

namespace TodayInSpace.Core
{
    // Parsing for NOAA SWPC feeds, kept separate from the Azure Function so it can be unit tested.
    public static class NoaaParsers
    {
        // Reads the latest solar wind speed (km/s) from products/summary/solar-wind-speed.json.
        // Current shape: [{"proton_speed": 412, "time_tag": "2026-09-29T22:10:00Z"}]
        // Also accepts a bare object, the older "WindSpeed" key, and numbers sent as strings.
        // Returns null if there's no usable value.
        public static int? ParseSolarWindSpeed(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                JsonElement entry;
                if (root.ValueKind == JsonValueKind.Array)
                {
                    if (root.GetArrayLength() == 0)
                        return null;
                    entry = root[root.GetArrayLength() - 1];   // latest reading last
                }
                else
                {
                    entry = root;
                }

                if (entry.ValueKind != JsonValueKind.Object)
                    return null;

                foreach (var key in new[] { "proton_speed", "WindSpeed", "speed" })
                {
                    if (entry.TryGetProperty(key, out var v) && TryGetNumber(v, out double speed) && speed > 0)
                        return (int)Math.Round(speed);
                }
                return null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static bool TryGetNumber(JsonElement el, out double value)
        {
            value = 0;
            return el.ValueKind switch
            {
                JsonValueKind.Number => el.TryGetDouble(out value),
                JsonValueKind.String => double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value),
                _ => false
            };
        }
    }
}
