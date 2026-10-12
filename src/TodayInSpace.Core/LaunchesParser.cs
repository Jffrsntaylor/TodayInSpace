using System.Text.Json;

namespace TodayInSpace.Core
{
    // One launch as LL2 lists it. ProviderId is null when LL2 has no launch service provider.
    // The pad fields are null when LL2 doesn't have them, so a launch is never dropped for a missing pad.
    public record Ll2Launch(string Id, string Name, DateTimeOffset Net, int StatusId, string StatusAbbrev,
                            int? ProviderId, string? ProviderName, string? Precision, int? Probability, string? Location,
                            string? PadName = null, double? PadLat = null, double? PadLon = null);

    // One page of an LL2 launches list. Next is the URL of the following page, or null on the last one.
    public record Ll2LaunchPage(IReadOnlyList<Ll2Launch> Launches, string? Next, int Count);

    // Parsing for The Space Devs Launch Library 2 `launches/previous/` and `launches/upcoming/` lists (mode=normal).
    public static class LaunchesParser
    {
        // Null if the JSON isn't a launches page, so a broken response is never counted as "no launches".
        public static Ll2LaunchPage? ParsePage(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("results", out var results)
                    || results.ValueKind != JsonValueKind.Array
                    || IntProp(root, "count") is not { } count)
                    return null;

                var launches = new List<Ll2Launch>();
                foreach (var l in results.EnumerateArray())
                    if (ToLaunch(l) is { } launch)
                        launches.Add(launch);
                return new Ll2LaunchPage(launches, StringProp(root, "next"), count);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // An entry without an id, name, time or status can't be counted or shown, so it's skipped.
        private static Ll2Launch? ToLaunch(JsonElement l)
        {
            if (l.ValueKind != JsonValueKind.Object)
                return null;

            string? id = StringProp(l, "id");
            string? name = StringProp(l, "name")?.Trim();
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name)
                || !DateTimeOffset.TryParse(StringProp(l, "net"), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var net)
                || !l.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.Object
                || IntProp(status, "id") is not { } statusId)
                return null;

            int? providerId = null;
            string? providerName = null;
            if (l.TryGetProperty("launch_service_provider", out var lsp) && lsp.ValueKind == JsonValueKind.Object)
            {
                providerId = IntProp(lsp, "id");
                providerName = StringProp(lsp, "name")?.Trim();
            }

            string? precision = l.TryGetProperty("net_precision", out var np) && np.ValueKind == JsonValueKind.Object
                ? StringProp(np, "abbrev")
                : null;
            // Null for most launches: LL2 only has a weather forecast in the last few days.
            int? probability = IntProp(l, "probability");
            string? location = null, padName = null;
            double? padLat = null, padLon = null;
            if (l.TryGetProperty("pad", out var pad) && pad.ValueKind == JsonValueKind.Object)
            {
                if (pad.TryGetProperty("location", out var loc) && loc.ValueKind == JsonValueKind.Object)
                    location = StringProp(loc, "name")?.Trim();
                padName = StringProp(pad, "name")?.Trim();
                // Both or neither: half a position can't be put on the globe.
                padLat = DegreesProp(pad, "latitude", 90);
                padLon = DegreesProp(pad, "longitude", 180);
                if (padLat == null || padLon == null)
                    padLat = padLon = null;
            }

            return new Ll2Launch(id, name, net, statusId, StringProp(status, "abbrev") ?? "", providerId,
                string.IsNullOrEmpty(providerName) ? null : providerName, precision, probability,
                string.IsNullOrEmpty(location) ? null : location,
                string.IsNullOrEmpty(padName) ? null : padName, padLat, padLon);
        }

        // LL2 sends pad coordinates as numbers today, but older versions sent strings like "28.56194122",
        // and unknown ones come as null. Anything out of range is treated as unknown too.
        private static double? DegreesProp(JsonElement obj, string name, double max)
        {
            if (!obj.TryGetProperty(name, out var el))
                return null;
            double value;
            if (el.ValueKind == JsonValueKind.Number)
                value = el.GetDouble();
            else if (el.ValueKind != JsonValueKind.String
                     || !double.TryParse(el.GetString(), System.Globalization.NumberStyles.Float,
                         System.Globalization.CultureInfo.InvariantCulture, out value))
                return null;
            return double.IsFinite(value) && Math.Abs(value) <= max ? value : null;
        }

        // TryGetInt32 throws on a JSON null, which LL2 sends for fields it doesn't know yet.
        private static int? IntProp(JsonElement obj, string name) =>
            obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int n) ? n : null;

        private static string? StringProp(JsonElement obj, string name) =>
            obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }
}
