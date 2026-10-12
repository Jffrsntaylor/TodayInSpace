using System.Text.Json;

namespace TodayInSpace.Core
{
    // The "Satellites" card: how many working satellites are in Earth orbit, whose they are, and how much
    // else is up there. The function app writes this to storage and the web app serves it as-is.
    // CatalogUpdated and ETag come from CelesTrak's response, so the next run can ask "has it changed?".
    // Checked is when the function last asked, which is what the site's freshness line uses.
    public record SatellitesSnapshot(int Active, int Starlink, IReadOnlyList<OwnerCount> Owners, int InOrbit, int Payloads,
                                     int Debris, int RocketBodies, int Unknown, DateTimeOffset? CatalogUpdated, string? ETag,
                                     DateTimeOffset Checked, string Source);

    // Active satellites for one SATCAT owner code. Flag is an uppercase ISO code, or null for organizations.
    public record OwnerCount(string Code, string Name, string? Flag, int Count);

    // Builds the satellites snapshot from CelesTrak's SATCAT CSV. No I/O, so the counting rules are easy to test.
    public static class Satcat
    {
        public const string Source = "CelesTrak SATCAT";

        public const string Url = "https://celestrak.org/pub/satcat.csv";

        // In the digests container, next to sky/people.json and sky/launches.json.
        public const string BlobName = "sky/satellites.json";

        // The real file has ~71,000 rows (every object ever catalogued). Far fewer means a truncated
        // or wrong file, and that must never replace good numbers.
        public const int MinRows = 50_000;

        // The catalogue doesn't move this much in a day, so a bigger jump means something is off.
        public const double MaxChange = 0.25;

        // The function checks every 6 hours, so a week-old snapshot means it has been broken for days.
        public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

        // Unassigned objects. Counted in "Others", never shown as an owner of its own.
        public const string Unassigned = "TBD";

        private static readonly string[] RequiredHeaders =
            { "OBJECT_NAME", "OBJECT_TYPE", "OPS_STATUS_CODE", "OWNER", "DECAY_DATE", "ORBIT_CENTER", "ORBIT_TYPE" };

        // CelesTrak's own "active": operational, partially operational, backup, spare, extended mission.
        private static readonly HashSet<string> ActiveStatuses = new() { "+", "P", "B", "S", "X" };

        // SATCAT owner codes are CelesTrak's own, not ISO, so the common ones get a name and a flag.
        private static readonly IReadOnlyDictionary<string, (string Name, string? Flag)> Owners =
            new Dictionary<string, (string, string?)>
            {
                ["US"] = ("United States", "US"),
                ["PRC"] = ("China", "CN"),
                ["UK"] = ("United Kingdom", "GB"),
                ["CIS"] = ("Russia", "RU"),   // CelesTrak's code for the USSR and Russia
                ["JPN"] = ("Japan", "JP"),
                ["IND"] = ("India", "IN"),
                ["FR"] = ("France", "FR"),
                ["IT"] = ("Italy", "IT"),
                ["GER"] = ("Germany", "DE"),
                ["SKOR"] = ("South Korea", "KR"),
                ["CA"] = ("Canada", "CA"),
                ["SPN"] = ("Spain", "ES"),
                ["TURK"] = ("Türkiye", "TR"),
                ["AUS"] = ("Australia", "AU"),
                ["FIN"] = ("Finland", "FI"),
                ["ARGN"] = ("Argentina", "AR"),
                ["ROC"] = ("Taiwan", "TW"),
                ["UAE"] = ("United Arab Emirates", "AE"),
                ["NOR"] = ("Norway", "NO"),
                ["ISRA"] = ("Israel", "IL"),
                ["GREC"] = ("Greece", "GR"),
                ["SING"] = ("Singapore", "SG"),
                ["SAUD"] = ("Saudi Arabia", "SA"),
                ["ESA"] = ("European Space Agency", "EU"),
                ["SES"] = ("SES", null),
                ["O3B"] = ("SES (O3b)", null),
                ["ITSO"] = ("Intelsat", null),
                ["EUTE"] = ("Eutelsat", null),
                ["GLOB"] = ("Globalstar", null),
                [Unassigned] = ("Unassigned", null),
            };

        // An unknown code is shown as-is, with no flag.
        public static OwnerCount Owner(string code, int count) =>
            Owners.TryGetValue(code, out var o) ? new OwnerCount(code, o.Name, o.Flag, count) : new OwnerCount(code, code, null, count);

        // Null when the file is missing a column we need or is too short to be the real catalogue.
        public static SatellitesSnapshot? Build(string? csv, DateTimeOffset? catalogUpdated, string? etag,
                                                DateTimeOffset now, int minRows = MinRows)
        {
            if (string.IsNullOrEmpty(csv))
                return null;

            using var rows = SatellitesCsv.Rows(csv).GetEnumerator();
            if (!rows.MoveNext())
                return null;

            // By name, not position, in case CelesTrak adds or reorders columns.
            var header = rows.Current.Select(h => h.Trim()).ToList();
            var col = RequiredHeaders.ToDictionary(h => h, h => header.IndexOf(h));
            if (col.Values.Any(i => i < 0))
                return null;
            int width = col.Values.Max() + 1;

            int total = 0, inOrbit = 0, payloads = 0, debris = 0, rocketBodies = 0, unknown = 0, active = 0, starlink = 0;
            var owners = new Dictionary<string, int>();

            while (rows.MoveNext())
            {
                var row = rows.Current;
                total++;
                if (row.Length < width)
                    continue;

                // In Earth orbit and still up: not docked to the ISS, not a deep-space probe, not decayed.
                if (row[col["ORBIT_CENTER"]] != "EA" || row[col["ORBIT_TYPE"]] != "ORB" || row[col["DECAY_DATE"]].Trim() != "")
                    continue;
                inOrbit++;

                switch (row[col["OBJECT_TYPE"]])
                {
                    case "PAY": payloads++; break;
                    case "DEB": debris++; break;
                    case "R/B": rocketBodies++; break;
                    default: unknown++; break;
                }

                // Payloads only: a few rocket bodies are marked operational, and they aren't satellites.
                if (row[col["OBJECT_TYPE"]] != "PAY" || !ActiveStatuses.Contains(row[col["OPS_STATUS_CODE"]]))
                    continue;
                active++;
                if (row[col["OBJECT_NAME"]].StartsWith("STARLINK", StringComparison.OrdinalIgnoreCase))
                    starlink++;
                string owner = row[col["OWNER"]].Trim();
                owners[owner] = owners.GetValueOrDefault(owner) + 1;
            }

            if (total < minRows)
                return null;

            // Biggest first, then by name. Unassigned goes last, so it's never one of the owners shown.
            var ownerList = owners
                .Select(kv => Owner(kv.Key, kv.Value))
                .OrderBy(o => o.Code == Unassigned)
                .ThenByDescending(o => o.Count)
                .ThenBy(o => o.Name, StringComparer.Ordinal)
                .ToList();

            return new SatellitesSnapshot(active, starlink, ownerList, inOrbit, payloads, debris, rocketBodies, unknown,
                catalogUpdated, etag, now, Source);
        }

        // True when the fresh numbers jumped too far from the last good ones to trust.
        public static bool LooksWrong(SatellitesSnapshot? previous, SatellitesSnapshot fresh)
        {
            if (previous == null)
                return false;
            return Jumped(previous.Active, fresh.Active) || Jumped(previous.InOrbit, fresh.InOrbit);
        }

        // Nothing to compare against when the old number is 0.
        private static bool Jumped(int before, int after) =>
            before > 0 && Math.Abs(after - before) > before * MaxChange;

        // "now" is passed in so tests can pin it. A snapshot with no "checked" reads as year 1, so it's too old.
        public static bool IsTooOld(SatellitesSnapshot snapshot, DateTimeOffset now) => now - snapshot.Checked > MaxAge;

        // Same casing the browser already gets from ASP.NET Core, so the stored JSON can be served unchanged.
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public static string Serialize(SatellitesSnapshot snapshot) => JsonSerializer.Serialize(snapshot, Json);

        // Null if the JSON is missing or isn't a snapshot we wrote.
        public static SatellitesSnapshot? Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;
            try
            {
                var snapshot = JsonSerializer.Deserialize<SatellitesSnapshot>(json, Json);
                return snapshot?.Owners == null ? null : snapshot;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
