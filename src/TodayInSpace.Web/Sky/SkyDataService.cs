using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using TodayInSpace.Core;

namespace TodayInSpace.Web.Sky
{
    // Everyone in space right now, for the "People in Space" section. A fixed shape built from the
    // parsed feeds, so upstream JSON is never passed through to the browser.
    public record PeopleInSpace(int Count, DateTimeOffset Updated, string Source, IReadOnlyList<PersonDto> People)
    {
        // True when the expeditions feed failed and everyone is filed under "In orbit". Only used to
        // pick a shorter cache time; not sent to the browser.
        [JsonIgnore]
        public bool StationsMissing { get; init; }
    }

    public record PersonDto(string Name, string? Agency, IReadOnlyList<FlagDto> Flags, string Station);

    public record FlagDto(string Code, string Country);

    // Fetches the Live Sky feeds server-side and caches them, so visitors' browsers never hit
    // CelesTrak or NOAA directly (no rate limits, no CORS issues, one upstream call per cache window).
    // If an upstream call fails, the last good value is served for up to a few days.
    public class SkyDataService
    {
        private const string TleUrlFormat = "https://celestrak.org/NORAD/elements/gp.php?CATNR={0}&FORMAT=TLE";
        private const string OvationUrl = "https://services.swpc.noaa.gov/json/ovation_aurora_latest.json";
        // Production LL2 only. The free tier allows about 15 requests an hour, hence the long cache below.
        private const string PeopleUrl = "https://ll.thespacedevs.com/2.3.0/astronauts/?in_space=true&limit=100";
        // Only used to label which station each person is on.
        private const string ExpeditionsUrl = "https://ll.thespacedevs.com/2.3.0/expeditions/?is_active=true&mode=detailed";
        public const string PeopleSource = "The Space Devs";

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
        private static readonly TimeSpan PeopleTtl = TimeSpan.FromHours(6);     // crews change every few weeks or months
        private static readonly TimeSpan StaleTtl = TimeSpan.FromDays(3);
        // LL2's limit is tight enough that retrying on every page view would keep us throttled.
        private static readonly TimeSpan PeopleRetryAfterFailure = TimeSpan.FromMinutes(15);

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

        // Both LL2 feeds are fetched together and cached as one entry, so that's two upstream calls per
        // cache window. The astronaut list decides who and how many; the expeditions only add station
        // labels, so if that call fails everyone is still shown, under "In orbit".
        public Task<PeopleInSpace?> GetPeopleAsync() =>
            GetCachedAsync("sky:people", PeopleTtl, async () =>
            {
                var astronauts = PeopleInSpaceParser.ParsePeople(await _http.GetStringAsync(PeopleUrl));
                if (astronauts == null)
                    return null;

                IReadOnlyDictionary<int, string>? stations = null;
                try
                {
                    stations = ExpeditionParser.ParseStations(await _http.GetStringAsync(ExpeditionsUrl));
                    if (stations == null)
                        _logger.LogWarning("Expeditions feed returned data in an unexpected format.");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Expeditions feed fetch failed: {msg}", ex.Message);
                }

                var people = PeopleInSpaceMerge.Group(astronauts, stations)
                    .Select(p => new PersonDto(
                        p.Person.Name,
                        p.Agency,
                        p.Person.Nationalities.Select(n => new FlagDto(n.Code, n.Country)).ToList(),
                        p.Station))
                    .ToList();
                return new PeopleInSpace(astronauts.Count, DateTimeOffset.UtcNow, PeopleSource, people)
                {
                    StationsMissing = stations == null,
                };
            },
            PeopleRetryAfterFailure,
            // Without station labels, try again after the cooldown rather than showing everyone
            // "In orbit" for the full six hours.
            ttlFor: p => p.StationsMissing ? PeopleRetryAfterFailure : PeopleTtl);

        // retryAfterFailure: if set, a failed fetch isn't retried until that much time has passed.
        // ttlFor: if set, picks the cache time from the fetched value instead of using ttl.
        private async Task<T?> GetCachedAsync<T>(string key, TimeSpan ttl, Func<Task<T?>> fetch,
            TimeSpan? retryAfterFailure = null, Func<T, TimeSpan>? ttlFor = null) where T : class
        {
            if (_cache.TryGetValue(key, out T? fresh) && fresh != null)
                return fresh;
            if (_cache.TryGetValue(key + ":cooldown", out _))
                return Stale<T>(key);

            await RefreshLock.WaitAsync();
            try
            {
                // Another request may have refreshed it while we waited.
                if (_cache.TryGetValue(key, out fresh) && fresh != null)
                    return fresh;

                var value = await fetch();
                if (value != null)
                {
                    _cache.Set(key, value, ttlFor?.Invoke(value) ?? ttl);
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

            if (retryAfterFailure is TimeSpan cooldown)
                _cache.Set(key + ":cooldown", true, cooldown);
            return Stale<T>(key);
        }

        private T? Stale<T>(string key) where T : class =>
            _cache.TryGetValue(key + ":stale", out T? stale) ? stale : null;
    }
}
