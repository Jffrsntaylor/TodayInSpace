using System.Net;
using Microsoft.Extensions.Caching.Memory;
using TodayInSpace.Core.Visibility;

namespace TodayInSpace.Web.Sky
{
    // Like VisibleSkyCache: its own size limit, because the keys come from visitors' locations.
    public sealed class NwsCache : IDisposable
    {
        public const int MaxEntries = 2000;

        public MemoryCache Cache { get; } = new(new MemoryCacheOptions { SizeLimit = MaxEntries });

        public void Dispose() => Cache.Dispose();
    }

    public record NwsConditions(Place? Place, SkyCover? Clouds);

    // Place name and cloud cover from the National Weather Service, for "Your sky this week".
    // Unlike the other feeds this is called on demand, because the locations come from visitors, so it caches hard:
    // the grid cell for a location barely ever changes, and NWS updates its gridded forecast about once an hour.
    // One GET each and no retries. Anything that goes wrong just means no place name or clouds.
    public class NwsWeatherService
    {
        private static readonly TimeSpan PointTtl = TimeSpan.FromDays(7);
        private static readonly TimeSpan GridTtl = TimeSpan.FromHours(1);
        // Outside the US /points answers 404 (or sometimes a 500), and that won't change, so remember it for a week.
        // Other failures are probably NWS having a bad moment, so try again in an hour.
        private static readonly TimeSpan NotCoveredTtl = TimeSpan.FromDays(7);
        private static readonly TimeSpan FailedTtl = TimeSpan.FromHours(1);

        private static readonly NwsConditions None = new(null, null);

        private readonly HttpClient _http;
        private readonly NwsCache _cache;
        private readonly ILogger<NwsWeatherService> _logger;

        public NwsWeatherService(HttpClient http, NwsCache cache, ILogger<NwsWeatherService> logger)
        {
            _http = http;
            _cache = cache;
            _logger = logger;
        }

        // lat/lon should already be rounded to 0.05° (SkyLocation.Round). They are never logged.
        public async Task<NwsConditions> GetAsync(double lat, double lon)
        {
            var point = await GetPointAsync(lat, lon);
            if (point == null)
                return None;

            return new NwsConditions(point.Place, await GetSkyCoverAsync(point));
        }

        private async Task<NwsPoint?> GetPointAsync(double lat, double lon)
        {
            string key = FormattableString.Invariant($"nws:point:{lat:F2}:{lon:F2}");
            if (_cache.Cache.TryGetValue(key, out PointResult? cached) && cached != null)
                return cached.Point;

            NwsPoint? point = null;
            TimeSpan ttl;
            try
            {
                using var response = await _http.GetAsync(NwsWeather.PointsUrl(lat, lon));
                if (response.IsSuccessStatusCode)
                {
                    point = NwsWeather.ParsePoint(await response.Content.ReadAsStringAsync());
                    ttl = point != null ? PointTtl : FailedTtl;
                }
                else
                {
                    ttl = response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.InternalServerError
                        ? NotCoveredTtl : FailedTtl;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _logger.LogWarning("NWS points lookup failed: {msg}", ex.Message);
                ttl = FailedTtl;
            }

            _cache.Cache.Set(key, new PointResult(point), new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = ttl });
            return point;
        }

        // Failures aren't cached here: the whole sky response is cached for 20 minutes anyway.
        private async Task<SkyCover?> GetSkyCoverAsync(NwsPoint point)
        {
            string key = FormattableString.Invariant($"nws:grid:{point.GridId}:{point.GridX}:{point.GridY}");
            if (_cache.Cache.TryGetValue(key, out SkyCover? cached) && cached != null)
                return cached;

            try
            {
                var cover = NwsWeather.ParseSkyCover(await _http.GetStringAsync(point.GridDataUrl));
                if (cover == null)
                {
                    _logger.LogWarning("NWS gridpoint {grid} returned data in an unexpected format.", point.GridId);
                    return null;
                }

                _cache.Cache.Set(key, cover, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = GridTtl });
                return cover;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _logger.LogWarning("NWS gridpoint {grid} fetch failed: {msg}", point.GridId, ex.Message);
                return null;
            }
        }

        // Wraps the point so "not covered" (null) can be cached too.
        private record PointResult(NwsPoint? Point);
    }
}
