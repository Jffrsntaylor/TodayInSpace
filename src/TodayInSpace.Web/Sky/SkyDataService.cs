using Microsoft.Extensions.Caching.Memory;
using TodayInSpace.Core;

namespace TodayInSpace.Web.Sky
{
    // Fetches the Live Sky feeds server-side and caches them, so visitors' browsers never hit
    // CelesTrak or NOAA directly (no rate limits, no CORS issues, one upstream call per cache window).
    // If an upstream call fails, the last good value is served for up to a few days.
    public class SkyDataService
    {
        private const string TleUrlFormat = "https://celestrak.org/NORAD/elements/gp.php?CATNR={0}&FORMAT=TLE";
        private const string OvationUrl = "https://services.swpc.noaa.gov/json/ovation_aurora_latest.json";

        // Satellites the site tracks, by URL-friendly id -> NORAD catalog number.
        // Only these ids are accepted, so the endpoint can't be used to query arbitrary objects.
        public static readonly IReadOnlyDictionary<string, int> Satellites = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["iss"] = 25544,     // International Space Station
            ["hubble"] = 20580,  // Hubble Space Telescope
            ["tiangong"] = 48274, // Tiangong space station (Tianhe core module)
        };

        // Only points with at least this aurora probability (%) are sent to the browser.
        public const int AuroraMinProbability = 5;

        private static readonly TimeSpan TleTtl = TimeSpan.FromHours(6);        // CelesTrak asks clients not to poll more often than every couple of hours
        private static readonly TimeSpan AuroraTtl = TimeSpan.FromMinutes(10);  // OVATION updates roughly every 5-10 minutes
        private static readonly TimeSpan PeopleTtl = TimeSpan.FromMinutes(10);  // saves a blob read per visitor; the function refreshes every 3 hours
        private static readonly TimeSpan StaleTtl = TimeSpan.FromDays(3);

        // One refresh at a time per process so a burst of visitors triggers a single upstream call.
        private static readonly SemaphoreSlim RefreshLock = new(1, 1);

        private readonly HttpClient _http;
        private readonly IPeopleStore _people;
        private readonly IMemoryCache _cache;
        private readonly ILogger<SkyDataService> _logger;
        private readonly TimeProvider _clock;

        public SkyDataService(HttpClient http, IPeopleStore people, IMemoryCache cache, ILogger<SkyDataService> logger, TimeProvider clock)
        {
            _http = http;
            _people = people;
            _cache = cache;
            _logger = logger;
            _clock = clock;
        }

        // Returns null for ids we don't track, or if the data can't be fetched and nothing is cached.
        public Task<SatelliteTle?> GetTleAsync(string id)
        {
            if (!Satellites.TryGetValue(id, out int catalogNumber))
                return Task.FromResult<SatelliteTle?>(null);

            string url = string.Format(System.Globalization.CultureInfo.InvariantCulture, TleUrlFormat, catalogNumber);
            return GetCachedAsync("sky:tle:" + catalogNumber, TleTtl,
                async () => SkyDataParser.ParseTle(await _http.GetStringAsync(url)));
        }

        public Task<AuroraSnapshot?> GetAuroraAsync() =>
            GetCachedAsync("sky:aurora", AuroraTtl,
                async () => SkyDataParser.ParseOvation(await _http.GetStringAsync(OvationUrl), AuroraMinProbability));

        // The RefreshPeopleInSpace function fetches LL2 on a timer and stores the result; this only reads
        // that copy. Calling LL2 from here got the site rate-limited (App Service outbound IPs are shared).
        // Null until the function has written it once, and null again if it stops refreshing for a week.
        public async Task<PeopleInSpace?> GetPeopleAsync()
        {
            var people = await GetCachedAsync("sky:people", PeopleTtl,
                async () => PeopleInSpaceSnapshot.Parse(await _people.ReadAsync()));

            // Checked after the cache so the stale fallback copy ages out too.
            if (people != null && PeopleInSpaceSnapshot.IsTooOld(people, _clock.GetUtcNow()))
            {
                _logger.LogWarning("people.json is too old to show (updated {Updated}).", people.Updated);
                return null;
            }
            return people;
        }

        private async Task<T?> GetCachedAsync<T>(string key, TimeSpan ttl, Func<Task<T?>> fetch) where T : class
        {
            if (_cache.TryGetValue(key, out T? fresh) && fresh != null)
                return fresh;

            await RefreshLock.WaitAsync();
            try
            {
                // Another request may have refreshed it while we waited.
                if (_cache.TryGetValue(key, out fresh) && fresh != null)
                    return fresh;

                var value = await fetch();
                if (value != null)
                {
                    _cache.Set(key, value, ttl);
                    _cache.Set(key + ":stale", value, StaleTtl);
                    return value;
                }
                _logger.LogWarning("Sky feed {key} returned data in an unexpected format.", key);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Sky feed {key} fetch failed: {msg}", key, ex.Message);
            }
            finally
            {
                RefreshLock.Release();
            }

            return Stale<T>(key);
        }

        private T? Stale<T>(string key) where T : class =>
            _cache.TryGetValue(key + ":stale", out T? stale) ? stale : null;
    }
}
