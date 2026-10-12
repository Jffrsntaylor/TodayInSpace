using System.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using TodayInSpace.Core.Visibility;
using TodayInSpace.Web.Services;

namespace TodayInSpace.Web.Sky
{
    // Its own cache with a size limit, kept apart from the feeds in the shared IMemoryCache, so a flood of
    // random coordinates can only ever evict other locations' passes.
    public sealed class VisibleSkyCache : IDisposable
    {
        public const int MaxLocations = 500;

        public MemoryCache Cache { get; } = new(new MemoryCacheOptions { SizeLimit = MaxLocations });

        public void Dispose() => Cache.Dispose();
    }

    // "Your sky this week": visible ISS, Tiangong and Hubble passes for one place, the planets and Moon tonight,
    // and whether you'll see anything: aurora odds, the place name and cloud cover.
    // Location privacy: the browser already rounds to 2 decimals, this rounds again to 0.05°,
    // and neither the location nor the cache key is ever logged.
    public class VisibleSkyService
    {
        // Pass times barely move in 20 minutes, and the page hides passes that have already ended.
        private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(20);
        private static readonly TimeSpan SlowWarning = TimeSpan.FromSeconds(1);
        // The Kp forecast in the digest changes twice a day; this saves a blob read per location.
        private static readonly TimeSpan KpForecastTtl = TimeSpan.FromMinutes(30);

        private readonly SkyDataService _sky;
        private readonly NwsWeatherService _nws;
        private readonly DigestService _digests;
        private readonly IMemoryCache _shared;
        private readonly VisibleSkyCache _cache;
        private readonly TimeProvider _clock;
        private readonly ILogger<VisibleSkyService> _logger;

        public VisibleSkyService(SkyDataService sky, NwsWeatherService nws, DigestService digests, IMemoryCache shared,
            VisibleSkyCache cache, TimeProvider clock, ILogger<VisibleSkyService> logger)
        {
            _sky = sky;
            _nws = nws;
            _digests = digests;
            _shared = shared;
            _cache = cache;
            _clock = clock;
            _logger = logger;
        }

        // Display names for the satellites, in the order the page lists them.
        public static readonly IReadOnlyList<(string Id, string Name)> Tracked = new[]
        {
            ("iss", "ISS"),
            ("tiangong", "Tiangong"),
            ("hubble", "Hubble"),
        };

        public async Task<VisibleSkyResponse> GetAsync(double lat, double lon)
        {
            string key = SkyLocation.CacheKey(lat, lon);
            if (_cache.Cache.TryGetValue(key, out VisibleSkyResponse? cached) && cached != null)
                return cached;

            double roundedLat = SkyLocation.Round(lat), roundedLon = SkyLocation.Round(lon);
            // Started first so the NWS calls overlap with the pass predictions.
            var weather = _nws.GetAsync(roundedLat, roundedLon);

            var orbits = new List<SatelliteOrbit>();
            foreach (var (id, name) in Tracked)
            {
                var tle = await _sky.GetTleAsync(id);
                orbits.Add(new SatelliteOrbit(id, name, tle?.Line1, tle?.Line2));
            }

            var now = _clock.GetUtcNow().UtcDateTime;
            var timer = Stopwatch.StartNew();
            var response = VisibleSky.Build(roundedLat, roundedLon, now, orbits);
            timer.Stop();
            if (timer.Elapsed > SlowWarning)
                _logger.LogWarning("Visible passes took {ms} ms to compute.", timer.ElapsedMilliseconds);

            var ovation = await _sky.GetAuroraAsync();
            var aurora = AuroraOdds.Build(roundedLat, roundedLon, ovation?.Points, await GetKpForecastAsync(now));
            var conditions = await weather;
            response = VisibleSky.WithConditions(response, conditions.Place, aurora, conditions.Clouds);

            // Don't keep an answer that's missing a satellite for the full 20 minutes; CelesTrak may be back soon.
            var ttl = response.Satellites.All(s => s.Available) ? Ttl : TimeSpan.FromMinutes(2);
            _cache.Cache.Set(key, response, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = ttl });
            return response;
        }

        // The 3-day Kp forecast from the latest daily digest. Empty if there's no digest or it has no forecast.
        private async Task<IReadOnlyList<KpDay>> GetKpForecastAsync(DateTime nowUtc)
        {
            const string key = "sky:kp-forecast";
            if (_shared.TryGetValue(key, out IReadOnlyList<KpDay>? cached) && cached != null)
                return cached;

            var digest = await _digests.GetLatestAsync();
            var forecast = digest?.SpaceWeather?.Forecast;
            if (forecast == null)
                return Array.Empty<KpDay>();

            var days = AuroraOdds.ForecastDays(digest!.Date, forecast.Select(f => (f.Day, f.Kp)), nowUtc);
            _shared.Set(key, days, KpForecastTtl);
            return days;
        }
    }
}
