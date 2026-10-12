using System.Text.Json;

namespace TodayInSpace.Core
{
    // The "Launches" card: the next few launches plus this year's orbital launch count. The function app
    // writes this to storage and the web app serves it as-is, so the blob and /api/sky/launches match.
    // Stats is null only if the year count has never been fetched; Upcoming is empty if the list never was.
    public record LaunchesSnapshot(YearStats? Stats, IReadOnlyList<UpcomingLaunch> Upcoming, DateTimeOffset Updated, string Source);

    // Every orbital launch attempt in one UTC calendar year (LL2's own convention).
    public record YearStats(int Year, int Total, int Successes, int Failures, int PartialFailures, int InFlight,
                            IReadOnlyList<ProviderCount> Providers, LatestLaunch? Latest, DateTimeOffset StatsUpdated);

    // Precision is LL2's net_precision abbrev (SEC, MIN, HR, DAY, M, Q...). It decides whether the front end
    // may show a time and a countdown, because far-off launches carry placeholder times like 00:00:00Z.
    public record UpcomingLaunch(string Name, DateTimeOffset Net, string Precision, int StatusId, string StatusAbbrev,
                                 int? Probability, string Provider, string? Location);

    public record ProviderCount(string Name, int Count);

    public record LatestLaunch(string Name, DateTimeOffset Net, string Status);

    // Year stats plus LL2's own total, which can differ by a launch or two if the list moved while paging.
    public record YearStatsBuild(YearStats Stats, int Ll2Count)
    {
        public int Difference => Ll2Count - Stats.Total;
    }

    // A snapshot to write, plus which parts are new, for the run's log line.
    public record LaunchesRefresh(LaunchesSnapshot Snapshot, string StatsFrom, string UpcomingFrom);

    // Builds the launches snapshot from LL2 pages. No I/O, so the refresh rules are easy to test.
    public static class Launches
    {
        public const string Source = "The Space Devs";

        // In the digests container, next to sky/people.json, out of reach of the YYYY-MM-DD archive route.
        public const string BlobName = "sky/launches.json";

        // Values for LaunchesRefresh.StatsFrom / UpcomingFrom.
        public const string Fresh = "fresh";
        public const string Previous = "previous";

        // About 100 launches a page and ~300 a year, so 6 pages is plenty. Past that something is wrong,
        // and following it further would spend LL2's ~15 calls an hour.
        public const int MaxYearPages = 6;

        // How many upcoming launches to keep. Between hourly refreshes the first one can fly, and the
        // front end then moves on to the next.
        public const int UpcomingKept = 5;

        // The year count is the expensive part (several ~1 MB pages), so it's only re-fetched this often
        // unless a launch has probably just happened.
        public static readonly TimeSpan StatsMaxAge = TimeSpan.FromHours(6);

        // The function refreshes hourly, so a week-old snapshot means it has been broken for days.
        public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

        // Status ids: 3 Launch Successful, 4 Launch Failure, 6 In Flight, 7 Partial Failure.
        // Listing them keeps scrubbed or TBD launches out even if LL2 ever puts them under /previous/.
        public const int StatusGo = 1, StatusSuccess = 3, StatusFailure = 4, StatusInFlight = 6, StatusPartialFailure = 7;

        public const string UpcomingUrl =
            "https://ll.thespacedevs.com/2.3.0/launches/upcoming/?include_suborbital=false&limit=5&mode=normal";

        // mode=normal because mode=list has no launch_service_provider. include_suborbital=false matters:
        // without it, suborbital hops are counted too.
        public static string YearUrl(int year) =>
            $"https://ll.thespacedevs.com/2.3.0/launches/previous/?include_suborbital=false&net__gte={year:D4}-01-01T00:00:00Z&status__ids=3,4,6,7&limit=100&mode=normal";

        // Only follow "next" links back to LL2 itself.
        public static bool IsLl2Url(string? url) =>
            url != null && url.StartsWith("https://ll.thespacedevs.com/", StringComparison.OrdinalIgnoreCase);

        // LL2's full names are long ("China Aerospace Science and Technology Corporation") and its abbrevs
        // are cryptic ("SpX", "RFSA"), so a few well-known agencies get the name people actually use.
        private static readonly IReadOnlyDictionary<int, string> ProviderNames = new Dictionary<int, string>
        {
            [88] = "CASC",
            [63] = "Roscosmos",
            [31] = "ISRO",
            [44] = "NASA",
            [96] = "Khrunichev",
            [257] = "Northrop Grumman",
        };

        public static string ProviderName(int? id, string? ll2Name) =>
            id is { } i && ProviderNames.TryGetValue(i, out var name) ? name : ll2Name ?? "Unknown";

        // Should this run fetch the year pages again? Yes when there's nothing to keep, when the count is
        // getting old, when it's a new year, or when the next launch's time has passed (it probably just flew).
        public static bool ShouldRefreshStats(LaunchesSnapshot? previous, DateTimeOffset now)
        {
            var stats = previous?.Stats;
            if (stats == null)
                return true;
            if (now - stats.StatsUpdated >= StatsMaxAge)
                return true;
            if (stats.Year != now.UtcDateTime.Year)
                return true;
            var next = previous!.Upcoming.FirstOrDefault();
            return next != null && next.Net < now;
        }

        // Counts the year's launches across all fetched pages. A launch that lands mid-paging shifts the
        // offsets, so the same launch can show up on two pages: it's counted once, by id.
        public static YearStatsBuild BuildStats(int year, IReadOnlyList<Ll2LaunchPage> pages, DateTimeOffset now)
        {
            var seen = new HashSet<string>();
            var launches = new List<Ll2Launch>();
            foreach (var page in pages)
                foreach (var l in page.Launches)
                    if (seen.Add(l.Id))
                        launches.Add(l);

            var providers = launches
                .GroupBy(l => l.ProviderId)
                .Select(g => new ProviderCount(ProviderName(g.Key, g.First().ProviderName), g.Count()))
                .OrderByDescending(p => p.Count)
                .ThenBy(p => p.Name, StringComparer.Ordinal)
                .ToList();

            var newest = launches.OrderByDescending(l => l.Net).FirstOrDefault();
            var latest = newest == null ? null : new LatestLaunch(newest.Name, newest.Net, newest.StatusAbbrev);

            var stats = new YearStats(
                year,
                launches.Count,
                launches.Count(l => l.StatusId == StatusSuccess),
                launches.Count(l => l.StatusId == StatusFailure),
                launches.Count(l => l.StatusId == StatusPartialFailure),
                launches.Count(l => l.StatusId == StatusInFlight),
                providers,
                latest,
                now);
            return new YearStatsBuild(stats, pages.Count > 0 ? pages[0].Count : 0);
        }

        // The first few launches from an upcoming page, soonest first as LL2 sends them. Null when the
        // JSON isn't a launches page.
        public static IReadOnlyList<UpcomingLaunch>? ParseUpcoming(string? json) =>
            LaunchesParser.ParsePage(json)?.Launches
                .OrderBy(l => l.Net)
                .Take(UpcomingKept)
                .Select(l => new UpcomingLaunch(l.Name, l.Net, l.Precision ?? "", l.StatusId, l.StatusAbbrev,
                    l.Probability, ProviderName(l.ProviderId, l.ProviderName), l.Location))
                .ToList();

        // Puts fresh parts together with whatever is kept from the last snapshot. Null when nothing new
        // came in, meaning the stored snapshot should be left alone.
        public static LaunchesRefresh? Combine(LaunchesSnapshot? previous, YearStats? freshStats,
                                               IReadOnlyList<UpcomingLaunch>? freshUpcoming, DateTimeOffset now)
        {
            if (freshStats == null && freshUpcoming == null)
                return null;

            var snapshot = new LaunchesSnapshot(
                freshStats ?? previous?.Stats,
                freshUpcoming ?? previous?.Upcoming ?? Array.Empty<UpcomingLaunch>(),
                now,
                Source);
            return new LaunchesRefresh(snapshot, freshStats != null ? Fresh : Previous, freshUpcoming != null ? Fresh : Previous);
        }

        // "now" is passed in so tests can pin it. A snapshot with no "updated" reads as year 1, so it's too old.
        public static bool IsTooOld(LaunchesSnapshot snapshot, DateTimeOffset now) => now - snapshot.Updated > MaxAge;

        // Same casing the browser already gets from ASP.NET Core, so the stored JSON can be served unchanged.
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public static string Serialize(LaunchesSnapshot snapshot) => JsonSerializer.Serialize(snapshot, Json);

        // Null if the JSON is missing or isn't a snapshot we wrote.
        public static LaunchesSnapshot? Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;
            try
            {
                var snapshot = JsonSerializer.Deserialize<LaunchesSnapshot>(json, Json);
                return snapshot?.Upcoming == null ? null : snapshot;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
