using System.Globalization;
using System.Text.RegularExpressions;

namespace TodayInSpace.Core.Visibility
{
    // Approximate = the zip isn't a Census ZCTA (PO-box and single-building zips like 20500 aren't),
    // so this is the average of the zips that share its first 3 digits.
    public record ZipLocation(string Zip, double Lat, double Lon, bool Approximate)
    {
        // The zip's postal city and state ("Olympia", "WA"), so the page can name it the way people know it.
        // Null when the data has no name for it, and for approximate locations.
        public string? City { get; init; }
        public string? State { get; init; }
    }

    // US zip code -> latitude/longitude and postal name, from the compact zip,lat,lon,city,state file built by
    // tools/build-zip-data.py out of the Census ZCTA Gazetteer and GeoNames.
    public partial class ZipLocations
    {
        private record Entry(double Lat, double Lon, string? City, string? State);

        private readonly Dictionary<string, Entry> _zips;
        private readonly Dictionary<string, (double Lat, double Lon)> _prefixes;

        private ZipLocations(Dictionary<string, Entry> zips)
        {
            _zips = zips;

            // The 3-digit areas are averaged once up front. Plain averaging of longitudes is safe here:
            // no US 3-digit area straddles the antimeridian.
            _prefixes = zips
                .GroupBy(z => z.Key[..3])
                .ToDictionary(g => g.Key, g => (g.Average(z => z.Value.Lat), g.Average(z => z.Value.Lon)));
        }

        public int Count => _zips.Count;

        [GeneratedRegex(@"^\d{5}$")]
        private static partial Regex FiveDigits();

        public static bool IsValidZip(string? zip) => zip != null && FiveDigits().IsMatch(zip);

        // Lines look like "98501,46.979,-122.876,Olympia,WA", or "00601,18.181,-66.750,," without a name
        // (3-column lines from the older file still parse). The zip stays text so leading zeros survive.
        // A header line or any malformed line is skipped.
        public static ZipLocations Parse(string csv)
        {
            var zips = new Dictionary<string, Entry>();
            foreach (var rawLine in csv.Split('\n'))
            {
                var parts = rawLine.Trim().Split(',');
                if ((parts.Length != 3 && parts.Length != 5) || !IsValidZip(parts[0]))
                    continue;
                if (double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat) &&
                    double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon))
                {
                    bool named = parts.Length == 5 && parts[3].Length > 0 && parts[4].Length > 0;
                    zips[parts[0]] = new Entry(lat, lon, named ? parts[3] : null, named ? parts[4] : null);
                }
            }
            return new ZipLocations(zips);
        }

        // Null when the zip isn't 5 digits or neither it nor its 3-digit area is known.
        public ZipLocation? Find(string zip)
        {
            if (!IsValidZip(zip))
                return null;
            if (_zips.TryGetValue(zip, out var exact))
                return new ZipLocation(zip, exact.Lat, exact.Lon, Approximate: false) { City = exact.City, State = exact.State };
            if (_prefixes.TryGetValue(zip[..3], out var area))
                return new ZipLocation(zip, Math.Round(area.Lat, 3), Math.Round(area.Lon, 3), Approximate: true);
            return null;
        }
    }
}
