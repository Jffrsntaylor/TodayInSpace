using Microsoft.Extensions.Caching.Memory;

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
        private static readonly TimeSpan StaleTtl = TimeSpan.FromDays(3);

        // One refresh at a time per process so a burst of visitors triggers a single upstream call.
        private static readonly SemaphoreSlim RefreshLock = new(1, 1);

        private readonly HttpClient _http;
        private readonly IMemoryCache _cache;
        private readonly ILogger<SkyDataService> _logger;

        public SkyDataService(HttpClient http, IMemoryCache cache, ILogger<SkyDataService> logger)
        {
            _http = http;
            _cache = cache;
            _logger = logger;
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

            return _cache.TryGetValue(key + ":stale", out T? stale) ? stale : null;
        }
    }
}
