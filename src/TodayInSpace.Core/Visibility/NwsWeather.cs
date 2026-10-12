using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace TodayInSpace.Core.Visibility
{
    // The nearest town NWS names for a location, and that place's IANA time zone ("America/Los_Angeles").
    public record Place(string City, string State, string? TimeZone);

    // Percent of the sky covered by cloud from Start to End.
    public record CloudPeriod(DateTime Start, DateTime End, int Percent);

    // Tonight's range is over the dark hours. Series covers the same 7 days as the passes.
    public record CloudOutlook(string Source, DateTime? Updated, int? TonightMin, int? TonightMax, IReadOnlyList<CloudPeriod> Series);

    // The NWS forecast grid cell for a location (e.g. SEW 101,43), plus the place it's near.
    public record NwsPoint(string GridId, int GridX, int GridY, Place? Place)
    {
        // Built from the parts rather than taken from the response, so we only ever call api.weather.gov.
        public string GridDataUrl => FormattableString.Invariant($"https://api.weather.gov/gridpoints/{GridId}/{GridX},{GridY}");
    }

    public record SkyCover(DateTime? Updated, IReadOnlyList<CloudPeriod> Periods);

    // Parsing for the National Weather Service API (api.weather.gov): /points for the grid cell and place name,
    // and the gridpoint's raw forecast for cloud cover. US only; anywhere else /points fails and we skip clouds.
    public static partial class NwsWeather
    {
        public const string Source = "NWS";

        // NWS wants at most 4 decimals; ours are already rounded to 0.05°.
        public static string PointsUrl(double lat, double lon) =>
            FormattableString.Invariant($"https://api.weather.gov/points/{lat:0.##},{lon:0.##}");

        public static NwsPoint? ParsePoint(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("properties", out var p) || p.ValueKind != JsonValueKind.Object)
                    return null;

                string? gridId = Text(p, "gridId");
                if (gridId == null || !GridIdPattern().IsMatch(gridId) ||
                    !p.TryGetProperty("gridX", out var x) || x.ValueKind != JsonValueKind.Number || !x.TryGetInt32(out int gridX) ||
                    !p.TryGetProperty("gridY", out var y) || y.ValueKind != JsonValueKind.Number || !y.TryGetInt32(out int gridY))
                    return null;

                Place? place = null;
                if (p.TryGetProperty("relativeLocation", out var rel) &&
                    rel.TryGetProperty("properties", out var relProps) &&
                    Text(relProps, "city") is string city && Text(relProps, "state") is string state)
                {
                    place = new Place(city, state, Text(p, "timeZone"));
                }

                return new NwsPoint(gridId, gridX, gridY, place);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // Reads properties.skyCover from a gridpoint forecast. Each value covers an interval written as
        // "start/duration", e.g. "2026-10-11T15:00:00+00:00/PT1H"; NWS merges equal hours into PT2H, PT3H, PT6H...
        public static SkyCover? ParseSkyCover(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("properties", out var p) ||
                    !p.TryGetProperty("skyCover", out var sky) ||
                    !sky.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
                    return null;

                var periods = new List<CloudPeriod>();
                foreach (var v in values.EnumerateArray())
                {
                    if (Text(v, "validTime") is string interval && TryParseInterval(interval, out var start, out var end) &&
                        v.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Number)
                    {
                        periods.Add(new CloudPeriod(start, end, (int)Math.Round(Math.Clamp(value.GetDouble(), 0, 100))));
                    }
                }

                DateTime? updated = DateTimeOffset.TryParse(Text(p, "updateTime"), CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var u) ? u.UtcDateTime : null;
                return new SkyCover(updated, periods.OrderBy(c => c.Start).ToList());
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // ISO-8601 interval with a duration: "2026-10-11T12:00:00+00:00/PT3H" -> 12:00 to 15:00 UTC.
        public static bool TryParseInterval(string interval, out DateTime start, out DateTime end)
        {
            start = end = default;
            var parts = interval.Split('/');
            if (parts.Length != 2 ||
                !DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out var from))
                return false;

            try
            {
                var length = XmlConvert.ToTimeSpan(parts[1]);   // the BCL's ISO-8601 duration parser
                if (length <= TimeSpan.Zero)
                    return false;
                start = from.UtcDateTime;
                end = start + length;
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        // Cloud cover at one moment, or null outside the forecast.
        public static int? PercentAt(IReadOnlyList<CloudPeriod> periods, DateTime time)
        {
            foreach (var c in periods)
            {
                if (c.Start <= time && time < c.End)
                    return c.Percent;
            }
            return null;
        }

        // Keeps the periods that overlap from..to (the passes' 7 days), and the range during tonight's dark hours.
        public static CloudOutlook Outlook(SkyCover cover, Darkness? tonight, DateTime from, DateTime to)
        {
            var series = cover.Periods.Where(c => c.End > from && c.Start < to).ToList();

            var dark = tonight == null
                ? new List<int>()
                : series.Where(c => c.End > tonight.Start && c.Start < tonight.End).Select(c => c.Percent).ToList();

            return new CloudOutlook(Source, cover.Updated,
                dark.Count > 0 ? dark.Min() : null,
                dark.Count > 0 ? dark.Max() : null,
                series);
        }

        // NWS office ids are three letters (SEW, OKX, AFG...).
        [GeneratedRegex("^[A-Z]{3}$")]
        private static partial Regex GridIdPattern();

        private static string? Text(JsonElement el, string name) =>
            el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;
    }
}
