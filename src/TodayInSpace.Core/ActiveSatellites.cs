using System.Globalization;
using System.Text.Json;

namespace TodayInSpace.Core
{
    // Every active satellite's orbital elements, for the globe's point cloud. The function app writes this
    // to storage every 6 hours and the web app serves it as-is; the browser does the propagation.
    // Short property names because there are ~16,000 of them: n = name, g = group, l1/l2 = the TLE lines.
    public record ActiveSatellitesSnapshot(DateTimeOffset Updated, string Source, IReadOnlyList<ActiveSatellite> Satellites);

    public record ActiveSatellite(string N, string G, string L1, string L2);

    // Parses CelesTrak's "active" TLE file and sorts each satellite into a globe toggle. No I/O.
    public static class ActiveSatellites
    {
        public const string Source = "CelesTrak";

        public const string Url = "https://celestrak.org/NORAD/elements/gp.php?GROUP=active&FORMAT=tle";

        // In the digests container, next to sky/satellites.json.
        public const string BlobName = "sky/active-tles.json";

        // The real file has ~16,000. Far fewer means a truncated or wrong file, and that must never
        // replace the stored one.
        public const int MinSatellites = 5_000;

        public const string Starlink = "starlink";
        public const string Geostationary = "geo";
        public const string Navigation = "navigation";
        public const string Other = "other";

        // The ISS, Hubble and Tiangong already have their own markers and orbits (SkyDataService.Satellites),
        // so they're left out of the cloud rather than drawn twice.
        public static readonly IReadOnlySet<int> ShownSeparately = new HashSet<int> { 25544, 20580, 48274 };

        // Each satellite is three lines: a name, then lines 1 and 2. Anything that doesn't fit that shape is
        // skipped rather than failing the whole file, so one bad block can't lose the other 15,000.
        public static List<ActiveSatellite> Parse(string? text)
        {
            var result = new List<ActiveSatellite>();
            if (string.IsNullOrEmpty(text))
                return result;

            var lines = text.Split('\n').Select(l => l.TrimEnd('\r', ' ')).ToArray();
            int i = 0;
            while (i + 2 < lines.Length)
            {
                string name = lines[i].Trim(), l1 = lines[i + 1], l2 = lines[i + 2];
                if (name.Length == 0 || !IsLine(l1, '1') || !IsLine(l2, '2') || CatalogNumber(l1) != CatalogNumber(l2))
                {
                    i++;
                    continue;
                }
                i += 3;

                string? group = Classify(name, l2);
                if (group == null || ShownSeparately.Contains(CatalogNumber(l1)))
                    continue;
                result.Add(new ActiveSatellite(name, group, l1, l2));
            }
            return result;
        }

        // Sorted from the TLE itself, so no extra downloads. Null if line 2's numbers can't be read.
        // Mean motion is revolutions per day: once a day is geostationary (if the orbit is round; the tilted
        // eccentric ones like QZSS aren't on the ring), twice a day is GPS/GLONASS/Galileo/BeiDou.
        public static string? Classify(string name, string line2)
        {
            if (!TryOrbit(line2, out double meanMotion, out double eccentricity))
                return null;
            if (name.StartsWith("STARLINK", StringComparison.OrdinalIgnoreCase))
                return Starlink;
            if (meanMotion >= 0.95 && meanMotion <= 1.05 && eccentricity < 0.05)
                return Geostationary;
            if (meanMotion >= 1.5 && meanMotion <= 3.0)
                return Navigation;
            return Other;
        }

        // Line 2: eccentricity is columns 27-33 with an implied "0.", mean motion is columns 53-63.
        private static bool TryOrbit(string line2, out double meanMotion, out double eccentricity)
        {
            meanMotion = eccentricity = 0;
            return line2.Length >= 63
                && double.TryParse("0." + line2.Substring(26, 7).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out eccentricity)
                && double.TryParse(line2.Substring(52, 11), NumberStyles.Float, CultureInfo.InvariantCulture, out meanMotion)
                && meanMotion > 0;
        }

        private static bool IsLine(string line, char number) => line.Length == 69 && line[0] == number && line[1] == ' ';

        private static int CatalogNumber(string line) =>
            int.TryParse(line.AsSpan(2, 5), NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : -1;

        // Null when the file is too short to be the real list.
        public static ActiveSatellitesSnapshot? Build(string? text, DateTimeOffset now, int minSatellites = MinSatellites)
        {
            var satellites = Parse(text);
            return satellites.Count < minSatellites ? null : new ActiveSatellitesSnapshot(now, Source, satellites);
        }

        // Same casing ASP.NET Core uses, so the stored JSON can be served unchanged.
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public static string Serialize(ActiveSatellitesSnapshot snapshot) => JsonSerializer.Serialize(snapshot, Json);

        // Null if the JSON is missing or isn't a snapshot we wrote.
        public static ActiveSatellitesSnapshot? ParseJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;
            try
            {
                var snapshot = JsonSerializer.Deserialize<ActiveSatellitesSnapshot>(json, Json);
                return snapshot?.Satellites == null ? null : snapshot;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
