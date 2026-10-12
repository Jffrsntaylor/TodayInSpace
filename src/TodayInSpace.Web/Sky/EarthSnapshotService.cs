using System.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using TodayInSpace.Core.Snapshot;

namespace TodayInSpace.Web.Sky
{
    // Its own cache, limited by total bytes, kept apart from the shared IMemoryCache like VisibleSkyCache.
    // A flood of random coordinates can only evict other pictures.
    public sealed class EarthSnapshotCache : IDisposable
    {
        public const long MaxBytes = 32 * 1024 * 1024;

        public MemoryCache Cache { get; } = new(new MemoryCacheOptions { SizeLimit = MaxBytes });

        public void Dispose() => Cache.Dispose();
    }

    // /api/sky/earth.png: a picture of the Earth right now, for home-screen widgets and link previews.
    // Location privacy follows VisibleSkyService: the request arrives already rounded to 0.05°, and neither
    // the location nor the cache key is ever logged.
    public class EarthSnapshotService
    {
        private static readonly TimeSpan SlowWarning = TimeSpan.FromSeconds(1);
        // If the ISS couldn't be drawn, try again soon rather than serving a picture without it for 10 minutes.
        private static readonly TimeSpan MissingIssTtl = TimeSpan.FromMinutes(2);

        // Rendering is CPU work on a small App Service plan. Two at a time keeps a burst of cache misses
        // from starving the rest of the site.
        private static readonly SemaphoreSlim RenderSlots = new(2, 2);

        private readonly SkyDataService _sky;
        private readonly EarthSnapshotRenderer _renderer;
        private readonly EarthSnapshotCache _cache;
        private readonly TimeProvider _clock;
        private readonly ILogger<EarthSnapshotService> _logger;

        public EarthSnapshotService(SkyDataService sky, EarthSnapshotRenderer renderer, EarthSnapshotCache cache,
            TimeProvider clock, ILogger<EarthSnapshotService> logger)
        {
            _sky = sky;
            _renderer = renderer;
            _cache = cache;
            _clock = clock;
            _logger = logger;
        }

        // The PNG, or null if it's meant to be centred on the ISS and there's no orbit data at all.
        public async Task<byte[]?> GetPngAsync(EarthSnapshotRequest request)
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            string key = request.CacheKey(now);
            if (_cache.Cache.TryGetValue(key, out byte[]? cached) && cached != null)
                return cached;

            var tle = await _sky.GetTleAsync("iss");
            var aurora = await _sky.GetAuroraAsync();

            await RenderSlots.WaitAsync();
            try
            {
                // Someone else may have drawn it while we waited.
                if (_cache.Cache.TryGetValue(key, out cached) && cached != null)
                    return cached;

                var iss = tle == null ? null : new OrbitTrack(tle.Line1, tle.Line2);
                var scene = EarthScene.Build(request, now, iss, aurora?.Points);
                if (scene == null)
                    return null;

                var timer = Stopwatch.StartNew();
                byte[] png = _renderer.RenderPng(scene, request);
                timer.Stop();
                if (timer.Elapsed > SlowWarning)
                    _logger.LogWarning("Earth snapshot took {ms} ms to render at {width}x{height}.",
                        timer.ElapsedMilliseconds, request.Width, request.Height);

                // Kept until the end of its 10-minute bucket, when the key changes anyway. Relative, because
                // MemoryCache keeps its own clock rather than our TimeProvider.
                var ttl = scene.Iss == null
                    ? MissingIssTtl
                    : EarthSnapshotRequest.BucketStart(now) + EarthSnapshotRequest.Bucket - now;
                _cache.Cache.Set(key, png, new MemoryCacheEntryOptions
                {
                    Size = png.Length,
                    AbsoluteExpirationRelativeToNow = ttl,
                });
                return png;
            }
            finally
            {
                RenderSlots.Release();
            }
        }
    }
}
